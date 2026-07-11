// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Identifies and quarantines credential-bearing STRM files produced by v0.8.
/// </summary>
/// <remarks>
/// A file is considered a candidate only when its exact v0.8 path layout and its
/// credential-bearing provider URL agree with the historical exporter shape. ID-bearing
/// layouts must also agree on the stream identifier. Files are moved, never deleted.
/// </remarks>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public sealed partial class LegacyStrmMigrationService(ILogger<LegacyStrmMigrationService> logger)
{
    private const int MaxCandidateCount = 50_000;
    private const int MaxLegacyStrmBytes = 4096;
    private const string QuarantineDirectoryName = ".jellyfin-xtream-legacy-quarantine";
    private const string ReportFileName = "migration-report.json";
    private static readonly TimeSpan _previewLifetime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim _migrationGate = new(1, 1);
    private static readonly JsonSerializerOptions _reportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<Guid, StoredPreview> _previews = new();

    /// <summary>
    /// Scans the configured export root without changing it.
    /// </summary>
    /// <param name="kind">Legacy export layout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>High-confidence legacy candidates.</returns>
    public async Task<LegacyStrmMigrationPreview> PreviewAsync(
        LegacyStrmExportKind kind,
        CancellationToken cancellationToken)
    {
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginConfiguration configuration = Plugin.Instance.Configuration;
            LegacyStrmMigrationPreview preview = await PreviewRootAsync(
                GetConfiguredRoot(configuration, kind),
                kind,
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset expiresUtc = DateTimeOffset.UtcNow.Add(_previewLifetime);
            Guid previewId = Guid.NewGuid();
            RemoveExpiredAndSupersededPreviews(kind);
            LegacyStrmMigrationPreview identified = preview with
            {
                PreviewId = previewId,
                ExpiresUtc = expiresUtc,
            };
            _previews[previewId] = new(identified, expiresUtc);
            return identified;
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    /// <summary>
    /// Re-scans and moves high-confidence legacy files into a timestamped quarantine folder.
    /// </summary>
    /// <param name="kind">Legacy export layout.</param>
    /// <param name="previewId">Identifier returned by the reviewed preview.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Quarantine result.</returns>
    public async Task<LegacyStrmMigrationResult> QuarantineAsync(
        LegacyStrmExportKind kind,
        Guid previewId,
        CancellationToken cancellationToken)
    {
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (previewId == Guid.Empty
                || !TryTakePreview(previewId, out StoredPreview storedPreview)
                || storedPreview.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("The migration preview expired or was already used. Preview the files again.");
            }

            PluginConfiguration configuration = Plugin.Instance.Configuration;
            string currentRoot = StrmExportPathPolicy.NormalizeRoot(GetConfiguredRoot(configuration, kind));
            if (storedPreview.Preview.Kind != kind
                || !StrmExportPathPolicy.PortablePathComparer.Equals(
                    storedPreview.Preview.RootPath,
                    currentRoot))
            {
                throw new InvalidOperationException("The export folder changed after the preview. Preview the files again.");
            }

            return await QuarantinePreviewAsync(
                storedPreview.Preview,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    internal async Task<LegacyStrmMigrationPreview> PreviewRootAsync(
        string rootPath,
        LegacyStrmExportKind kind,
        CancellationToken cancellationToken)
    {
        string normalizedRoot = StrmExportPathPolicy.NormalizeRoot(rootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException("The configured STRM export folder does not exist.");
        }

        if (File.GetAttributes(normalizedRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The configured STRM export folder must not be a symbolic link.");
        }

        List<LegacyStrmMigrationCandidate> candidates = [];
        Dictionary<string, byte[]> candidateHashes = new(
            StrmExportPathPolicy.PortablePathComparer);
        ScanDiagnostics diagnostics = new();
        bool truncated = false;
        foreach (string path in EnumerateSafeStrmFiles(
                     normalizedRoot,
                     diagnostics,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateMatch? match = await TryCreateCandidateAsync(
                normalizedRoot,
                path,
                kind,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (match != null)
            {
                candidates.Add(match.Candidate);
                candidateHashes[match.Candidate.RelativePath] = match.ContentHash;
                if (candidates.Count >= MaxCandidateCount)
                {
                    truncated = true;
                    break;
                }
            }
        }

        candidates.Sort((left, right) =>
            StrmExportPathPolicy.PortablePathComparer.Compare(left.RelativePath, right.RelativePath));
        return new(kind, normalizedRoot, candidates, truncated)
        {
            CandidateHashes = candidateHashes,
            Incomplete = diagnostics.SkippedPathCount > 0,
            SkippedPathCount = diagnostics.SkippedPathCount,
        };
    }

    internal async Task<LegacyStrmMigrationResult> QuarantinePreviewAsync(
        LegacyStrmMigrationPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Truncated)
        {
            throw new InvalidOperationException(
                "The legacy scan reached its safety limit. Narrow the export root before quarantining.");
        }

        if (preview.Incomplete)
        {
            throw new InvalidOperationException(
                "The legacy scan was incomplete. Fix unreadable paths or narrow the export root before quarantining.");
        }

        if (preview.Candidates.Count == 0)
        {
            return new(preview.Kind, 0, null, 0);
        }

        string rootPath = StrmExportPathPolicy.NormalizeRoot(preview.RootPath);
        string batchName = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        string quarantineRoot = Path.Combine(rootPath, QuarantineDirectoryName, batchName);
        EnsureSafeDirectoryPath(rootPath, quarantineRoot);
        List<QuarantineReportEntry> moved = [];
        int skipped = 0;
        await WriteQuarantineReportAsync(
            quarantineRoot,
            preview.Kind,
            "inProgress",
            preview.Candidates.Select(candidate =>
                new QuarantineReportEntry(candidate.RelativePath, candidate.StreamId, "pending")).ToList(),
            skipped).ConfigureAwait(false);

        foreach (LegacyStrmMigrationCandidate candidate in preview.Candidates)
        {
            CancellationToken operationToken = moved.Count == 0
                ? cancellationToken
                : CancellationToken.None;
            operationToken.ThrowIfCancellationRequested();
            if (!StrmExportPathPolicy.TryResolveManagedStrmPath(
                    rootPath,
                    candidate.RelativePath,
                    out string? sourcePath))
            {
                skipped++;
                continue;
            }

            CandidateMatch? current = await TryCreateCandidateAsync(
                rootPath,
                sourcePath!,
                preview.Kind,
                new ScanDiagnostics(),
                operationToken).ConfigureAwait(false);
            if (current == null
                || current.Candidate.StreamId != candidate.StreamId
                || !StrmExportPathPolicy.PortablePathComparer.Equals(
                    current.Candidate.RelativePath,
                    candidate.RelativePath)
                || !preview.CandidateHashes.TryGetValue(
                    candidate.RelativePath,
                    out byte[]? previewHash)
                || !CryptographicOperations.FixedTimeEquals(
                    current.ContentHash,
                    previewHash))
            {
                skipped++;
                continue;
            }

            string destinationPath = Path.GetFullPath(Path.Combine(
                quarantineRoot,
                candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar) + ".quarantined"));
            if (!IsWithinRoot(quarantineRoot, destinationPath))
            {
                skipped++;
                continue;
            }

            try
            {
                EnsureSafeDirectoryPath(rootPath, Path.GetDirectoryName(destinationPath)!);
                if (!StrmExportPathPolicy.TryResolveManagedStrmPath(
                        rootPath,
                        candidate.RelativePath,
                        out string? revalidatedSourcePath)
                    || !string.Equals(
                        sourcePath,
                        revalidatedSourcePath,
                        StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                File.Move(revalidatedSourcePath!, destinationPath, false);
                if (!await HasContentHashAsync(
                        destinationPath,
                        current.ContentHash,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    if (!RestoreChangedFile(revalidatedSourcePath!, destinationPath)
                        && File.Exists(destinationPath))
                    {
                        moved.Add(new(candidate.RelativePath, candidate.StreamId, "retainedChanged"));
                    }

                    skipped++;
                    continue;
                }

                moved.Add(new(candidate.RelativePath, candidate.StreamId, "moved"));
            }
            catch (IOException)
            {
                skipped++;
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
            }
            catch (InvalidOperationException)
            {
                skipped++;
            }
        }

        await WriteQuarantineReportAsync(
            quarantineRoot,
            preview.Kind,
            "complete",
            moved,
            skipped).ConfigureAwait(false);

        logger.LogInformation(
            "Quarantined {Count} legacy {ExportKind} STRM files; {SkippedCount} changed or unsafe files were skipped.",
            moved.Count,
            preview.Kind,
            skipped);
        return new(preview.Kind, moved.Count, quarantineRoot, skipped);
    }

    private static Task WriteQuarantineReportAsync(
        string quarantineRoot,
        LegacyStrmExportKind kind,
        string status,
        IReadOnlyList<QuarantineReportEntry> entries,
        int skippedCount)
    {
        QuarantineReport report = new(
            1,
            "Jellyfin.Xtream",
            kind,
            status,
            DateTimeOffset.UtcNow,
            skippedCount,
            entries);
        string reportJson = JsonSerializer.Serialize(report, _reportJsonOptions) + Environment.NewLine;
        return StrmExportManifestStore.WriteTextAtomicallyAsync(
            Path.Combine(quarantineRoot, ReportFileName),
            reportJson,
            CancellationToken.None);
    }

    private static string GetConfiguredRoot(
        PluginConfiguration configuration,
        LegacyStrmExportKind kind)
    {
        string root = kind == LegacyStrmExportKind.Vod
            ? configuration.VodStrmExportPath
            : configuration.SeriesStrmExportPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"The {kind} STRM export folder must be configured before scanning for legacy files.");
        }

        return root;
    }

    private static IEnumerable<string> EnumerateSafeStrmFiles(
        string rootPath,
        ScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(rootPath);
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pendingDirectories.Pop();
            DirectoryInfo[] children;
            FileInfo[] files;
            try
            {
                DirectoryInfo directoryInfo = new(directory);
                if (!directoryInfo.Exists
                    || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || string.Equals(
                        directoryInfo.Name,
                        QuarantineDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                children = directoryInfo.GetDirectories();
                files = directoryInfo.GetFiles("*.strm");
            }
            catch (IOException)
            {
                diagnostics.SkippedPathCount++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                diagnostics.SkippedPathCount++;
                continue;
            }

            foreach (DirectoryInfo child in children)
            {
                if (string.Equals(
                        child.Name,
                        QuarantineDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = child.Attributes;
                }
                catch (IOException)
                {
                    diagnostics.SkippedPathCount++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    diagnostics.SkippedPathCount++;
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.SkippedPathCount++;
                }
                else
                {
                    pendingDirectories.Push(child.FullName);
                }
            }

            foreach (FileInfo file in files)
            {
                bool shouldInspect;
                try
                {
                    shouldInspect = !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && file.Length <= MaxLegacyStrmBytes;
                }
                catch (IOException)
                {
                    diagnostics.SkippedPathCount++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    diagnostics.SkippedPathCount++;
                    continue;
                }

                if (shouldInspect)
                {
                    yield return file.FullName;
                }
                else
                {
                    diagnostics.SkippedPathCount++;
                }
            }
        }
    }

    private static async Task<CandidateMatch?> TryCreateCandidateAsync(
        string rootPath,
        string path,
        LegacyStrmExportKind kind,
        ScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        string relativePath = StrmExportPathPolicy.NormalizeRelativePath(
            Path.GetRelativePath(rootPath, path));
        if (!StrmExportPathPolicy.TryResolveManagedStrmPath(rootPath, relativePath, out string? safePath)
            || !File.Exists(safePath))
        {
            return null;
        }

        LegacyPathMatch? pathMatch = MatchLegacyPath(relativePath, kind);
        if (pathMatch == null)
        {
            return null;
        }

        try
        {
            FileInfo fileInfo = new(safePath);
            if (fileInfo.Length > MaxLegacyStrmBytes
                || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }
        }
        catch (IOException)
        {
            diagnostics.SkippedPathCount++;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.SkippedPathCount++;
            return null;
        }

        byte[] contentBytes;
        try
        {
            contentBytes = await ReadSmallFileAsync(safePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            diagnostics.SkippedPathCount++;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.SkippedPathCount++;
            return null;
        }

        string content = System.Text.Encoding.UTF8.GetString(contentBytes).Trim();
        LegacyUrlMatch? urlMatch = MatchLegacyUrl(content, kind, pathMatch);
        if (urlMatch == null)
        {
            return null;
        }

        return new(
            new LegacyStrmMigrationCandidate(relativePath, urlMatch.StreamId),
            SHA256.HashData(contentBytes));
    }

    private static LegacyPathMatch? MatchLegacyPath(
        string relativePath,
        LegacyStrmExportKind kind)
    {
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => V9IdentityRegex().IsMatch(segment)))
        {
            return null;
        }

        if (kind == LegacyStrmExportKind.Vod && segments.Length == 2)
        {
            Match match = LegacyVodFileNameRegex().Match(segments[1]);
            if (match.Success
                && string.Equals(match.Groups["title"].Value, segments[0], StringComparison.Ordinal)
                && int.TryParse(
                    match.Groups["id"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int streamId))
            {
                return new(streamId, match.Groups["extension"].Value);
            }

            string suffix = ".strm";
            if (!segments[1].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string stem = segments[1][..^suffix.Length];
            if (string.Equals(stem, segments[0], StringComparison.Ordinal))
            {
                return new(null, null);
            }

            string extensionPrefix = segments[0] + ".";
            if (stem.StartsWith(extensionPrefix, StringComparison.Ordinal))
            {
                string extension = stem[extensionPrefix.Length..];
                if (extension.Length is >= 1 and <= 16
                    && extension.All(char.IsAsciiLetterOrDigit))
                {
                    return new(null, extension);
                }
            }
        }

        if (kind == LegacyStrmExportKind.Series && segments.Length == 3)
        {
            Match match = LegacyEpisodeFileNameRegex().Match(segments[2]);
            Match seasonMatch = LegacySeasonDirectoryRegex().Match(segments[1]);
            if (match.Success
                && seasonMatch.Success
                && string.Equals(
                    match.Groups["season"].Value,
                    seasonMatch.Groups["season"].Value,
                    StringComparison.Ordinal)
                && int.TryParse(
                    match.Groups["id"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int streamId))
            {
                return new(streamId, null);
            }

            Match legacyMatch = LegacyPreIdEpisodeFileNameRegex().Match(segments[2]);
            if (legacyMatch.Success
                && seasonMatch.Success
                && string.Equals(
                    legacyMatch.Groups["series"].Value,
                    segments[0],
                    StringComparison.Ordinal)
                && string.Equals(
                    legacyMatch.Groups["season"].Value,
                    seasonMatch.Groups["season"].Value,
                    StringComparison.Ordinal))
            {
                return new(null, null);
            }
        }

        return null;
    }

    private static LegacyUrlMatch? MatchLegacyUrl(
        string content,
        LegacyStrmExportKind kind,
        LegacyPathMatch pathMatch)
    {
        if (content.Length == 0
            || content.Contains('\r', StringComparison.Ordinal)
            || content.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        Match urlMatch = LegacyStreamUrlRegex().Match(content);
        string expectedType = kind == LegacyStrmExportKind.Vod ? "movie" : "series";
        if (!urlMatch.Success
            || !string.Equals(
                urlMatch.Groups["type"].Value,
                expectedType,
                StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                urlMatch.Groups["id"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int streamId)
            || streamId <= 0
            || (pathMatch.StreamId.HasValue && pathMatch.StreamId.Value != streamId))
        {
            return null;
        }

        string extension = urlMatch.Groups["extension"].Value;
        if (kind == LegacyStrmExportKind.Vod)
        {
            string expectedExtension = pathMatch.ContainerExtension ?? string.Empty;
            if (!string.Equals(extension, expectedExtension, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return new(streamId);
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeDirectoryPath(string rootPath, string directoryPath)
    {
        string normalizedRoot = StrmExportPathPolicy.NormalizeRoot(rootPath);
        string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (!string.Equals(normalizedRoot, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            && !IsWithinRoot(normalizedRoot, normalizedDirectory))
        {
            throw new InvalidOperationException("The quarantine directory escaped the configured export root.");
        }

        if (!Directory.Exists(normalizedRoot)
            || File.GetAttributes(normalizedRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The configured export root became unsafe.");
        }

        string relativePath = Path.GetRelativePath(normalizedRoot, normalizedDirectory);
        string currentPath = normalizedRoot;
        foreach (string segment in relativePath.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException("The quarantine directory contained an unsafe path segment.");
            }

            currentPath = Path.Combine(currentPath, segment);
            if (File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                throw new InvalidOperationException("A quarantine directory segment is an existing file.");
            }

            Directory.CreateDirectory(currentPath);
            if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The quarantine directory must not contain symbolic links.");
            }
        }
    }

    private static async Task<byte[]> ReadSmallFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[MaxLegacyStrmBytes + 1];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead > MaxLegacyStrmBytes)
        {
            throw new IOException("The legacy STRM file exceeded the scan size limit.");
        }

        return buffer[..totalRead];
    }

    private static async Task<bool> HasContentHashAsync(
        string path,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] content = await ReadSmallFileAsync(path, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), expectedHash);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool RestoreChangedFile(string sourcePath, string destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath) && File.Exists(destinationPath))
            {
                File.Move(destinationPath, sourcePath, false);
            }

            return !File.Exists(destinationPath);
        }
        catch (IOException)
        {
            // Leave the changed file quarantined when a safe restoration is no longer possible.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the changed file quarantined when a safe restoration is no longer possible.
            return false;
        }
    }

    private void RemoveExpiredAndSupersededPreviews(LegacyStrmExportKind kind)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<Guid, StoredPreview> preview in _previews)
        {
            if (preview.Value.ExpiresUtc <= now || preview.Value.Preview.Kind == kind)
            {
                _previews.TryRemove(preview.Key, out _);
            }
        }
    }

    private bool TryTakePreview(Guid requestedPreviewId, out StoredPreview storedPreview)
    {
        foreach (KeyValuePair<Guid, StoredPreview> availablePreview in _previews)
        {
            if (availablePreview.Key == requestedPreviewId)
            {
                if (_previews.TryRemove(availablePreview.Key, out StoredPreview? foundPreview)
                    && foundPreview != null)
                {
                    storedPreview = foundPreview;
                    return true;
                }

                break;
            }
        }

        storedPreview = null!;
        return false;
    }

    [GeneratedRegex(@"^(?<title>.+) \[(?<id>[0-9]+)\](?:\.(?<extension>[A-Za-z0-9]{1,16}))?\.strm$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVodFileNameRegex();

    [GeneratedRegex(@"^Season (?<season>[0-9]{2,})$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySeasonDirectoryRegex();

    [GeneratedRegex(@"^S(?<season>[0-9]{2,})E[0-9]{2,} - .+ \[(?<id>[0-9]+)\]\.strm$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyEpisodeFileNameRegex();

    [GeneratedRegex(@"^(?<series>.+) - S(?<season>[0-9]{2,})E[0-9]{2,} - .+\.strm$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyPreIdEpisodeFileNameRegex();

    [GeneratedRegex(@"^https?://[^\r\n?#]+/(?<type>movie|series)/[^/\r\n?#]+/[^/\r\n?#]+/(?<id>[0-9]+)(?:\.(?<extension>[A-Za-z0-9]{1,16}))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LegacyStreamUrlRegex();

    [GeneratedRegex(@"\[xtream-(?:vod|series|episode)-[0-9]+\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex V9IdentityRegex();

    private sealed record CandidateMatch(
        LegacyStrmMigrationCandidate Candidate,
        byte[] ContentHash);

    private sealed record LegacyPathMatch(int? StreamId, string? ContainerExtension);

    private sealed record LegacyUrlMatch(int StreamId);

    private sealed class ScanDiagnostics
    {
        public int SkippedPathCount { get; set; }
    }

    private sealed record StoredPreview(
        LegacyStrmMigrationPreview Preview,
        DateTimeOffset ExpiresUtc);

    private sealed record QuarantineReport(
        int SchemaVersion,
        string Owner,
        LegacyStrmExportKind Kind,
        string Status,
        DateTimeOffset UpdatedUtc,
        int SkippedCount,
        IReadOnlyList<QuarantineReportEntry> Entries);

    private sealed record QuarantineReportEntry(
        string OriginalRelativePath,
        int StreamId,
        string Outcome);
}
