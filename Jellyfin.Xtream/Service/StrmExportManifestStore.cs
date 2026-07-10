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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Stores and reconciles marker-owned STRM files beneath one export root.
/// </summary>
internal sealed class StrmExportManifestStore
{
    internal const string ManifestFileName = ".jellyfin-xtream-strm-manifest.json";
    private const string ManifestOwner = "Jellyfin.Xtream";
    private const int ManifestSchemaVersion = 1;
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly UTF8Encoding _utf8WithoutBom = new(false);
    private readonly string _exportKind;
    private readonly string _rootPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmExportManifestStore"/> class.
    /// </summary>
    /// <param name="rootPath">Normalized export root.</param>
    /// <param name="exportKind">Stable export kind.</param>
    public StrmExportManifestStore(string rootPath, string exportKind)
    {
        _rootPath = StrmExportPathPolicy.NormalizeRoot(rootPath);
        _exportKind = string.IsNullOrWhiteSpace(exportKind)
            ? throw new ArgumentException("An export kind is required.", nameof(exportKind))
            : exportKind;
    }

    /// <summary>
    /// Loads and validates the ownership manifest without changing the filesystem.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The manifest load result.</returns>
    public async Task<StrmExportManifestLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(_rootPath, ManifestFileName);
        if (!File.Exists(path))
        {
            return new(StrmExportManifestState.Missing, []);
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            return Invalid("The ownership manifest must not be a symbolic link.");
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            StrmExportManifest? manifest = JsonSerializer.Deserialize<StrmExportManifest>(json, _serializerOptions);
            if (manifest == null)
            {
                return Invalid("The manifest was empty.");
            }

            if (manifest.SchemaVersion != ManifestSchemaVersion
                || !string.Equals(manifest.Owner, ManifestOwner, StringComparison.Ordinal)
                || !string.Equals(manifest.ExportKind, _exportKind, StringComparison.Ordinal))
            {
                return Invalid("The manifest marker, schema, or export kind did not match.");
            }

            List<StrmExportManifestEntry?> serializedEntries = manifest.Entries ?? [];
            List<StrmExportManifestEntry> entries = new(serializedEntries.Count);
            HashSet<string> identities = new(StringComparer.Ordinal);
            HashSet<string> relativePaths = new(StrmExportPathPolicy.PortablePathComparer);
            foreach (StrmExportManifestEntry? entry in serializedEntries)
            {
                if (entry == null
                    || string.IsNullOrWhiteSpace(entry.Identity)
                    || !identities.Add(entry.Identity)
                    || !relativePaths.Add(entry.RelativePath)
                    || !StrmExportPathPolicy.TryResolveManagedStrmPath(_rootPath, entry.RelativePath, out _))
                {
                    return Invalid("The manifest contained a duplicate or unsafe entry.");
                }

                entries.Add(entry);
            }

            return new(StrmExportManifestState.Valid, entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Invalid($"The manifest could not be read: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Deletes stale files listed by a valid previous manifest, then atomically commits the new manifest.
    /// No files are removed when the previous manifest is missing or invalid.
    /// </summary>
    /// <param name="previous">Previously loaded manifest.</param>
    /// <param name="expectedEntries">Successfully written entries for this run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of stale managed files deleted.</returns>
    public async Task<int> ReconcileAndCommitAsync(
        StrmExportManifestLoadResult previous,
        IReadOnlyCollection<StrmExportManifestEntry> expectedEntries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (previous.State == StrmExportManifestState.Invalid)
        {
            throw new InvalidOperationException("An invalid ownership manifest cannot be replaced automatically.");
        }

        List<StrmExportManifestEntry> canonicalEntries = ValidateExpectedEntries(expectedEntries);
        HashSet<string> expectedPaths = new(
            canonicalEntries.Select(entry => entry.RelativePath),
            StrmExportPathPolicy.PortablePathComparer);

        int deleted = 0;
        cancellationToken.ThrowIfCancellationRequested();
        if (previous.State == StrmExportManifestState.Valid)
        {
            foreach (StrmExportManifestEntry staleEntry in previous.Entries
                         .Where(entry => !expectedPaths.Contains(entry.RelativePath))
                         .OrderBy(entry => entry.RelativePath, StrmExportPathPolicy.PortablePathComparer))
            {
                if (!StrmExportPathPolicy.TryResolveManagedStrmPath(_rootPath, staleEntry.RelativePath, out string? stalePath))
                {
                    throw new InvalidOperationException("A previously validated manifest path became unsafe.");
                }

                if (File.Exists(stalePath))
                {
                    File.Delete(stalePath);
                    deleted++;
                }
            }
        }

        StrmExportManifest manifest = new()
        {
            SchemaVersion = ManifestSchemaVersion,
            Owner = ManifestOwner,
            ExportKind = _exportKind,
            Entries = [.. canonicalEntries],
        };
        string json = JsonSerializer.Serialize(manifest, _serializerOptions) + Environment.NewLine;
        // Once stale-file deletion starts, finish the small manifest transaction even if the caller
        // is cancelled so the on-disk ownership record cannot be left partially advanced.
        await WriteTextAtomicallyAsync(Path.Combine(_rootPath, ManifestFileName), json, CancellationToken.None).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Writes a managed STRM file atomically, avoiding partially written library entries.
    /// </summary>
    /// <param name="path">Absolute STRM path.</param>
    /// <param name="content">Complete STRM content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The managed file path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)
            || (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidOperationException("Managed files cannot be written through symbolic links.");
        }

        if (File.Exists(path)
            && string.Equals(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                content,
                StringComparison.Ordinal))
        {
            return;
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, _utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static StrmExportManifestLoadResult Invalid(string error)
    {
        return new(StrmExportManifestState.Invalid, [], error);
    }

    private List<StrmExportManifestEntry> ValidateExpectedEntries(
        IReadOnlyCollection<StrmExportManifestEntry> expectedEntries)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> relativePaths = new(StrmExportPathPolicy.PortablePathComparer);
        List<StrmExportManifestEntry> result = new(expectedEntries.Count);
        foreach (StrmExportManifestEntry entry in expectedEntries
                     .OrderBy(entry => entry.Identity, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(entry.Identity)
                || !identities.Add(entry.Identity)
                || !relativePaths.Add(entry.RelativePath)
                || !StrmExportPathPolicy.TryResolveManagedStrmPath(_rootPath, entry.RelativePath, out _))
            {
                throw new InvalidOperationException("The export produced a duplicate or unsafe manifest entry.");
            }

            result.Add(entry with { RelativePath = StrmExportPathPolicy.NormalizeRelativePath(entry.RelativePath) });
        }

        return result;
    }

    private sealed class StrmExportManifest
    {
        public int SchemaVersion { get; set; }

        public string Owner { get; set; } = string.Empty;

        public string ExportKind { get; set; } = string.Empty;

        public List<StrmExportManifestEntry?>? Entries { get; set; }
    }
}
