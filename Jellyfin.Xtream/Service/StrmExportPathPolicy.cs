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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Builds portable, identity-bearing paths for managed STRM exports.
/// </summary>
internal static class StrmExportPathPolicy
{
    private const int MaxDirectoryNameLength = 96;
    private const int MaxEpisodeTitleLength = 72;
    private const int MaxFileNameLength = 180;
    private static readonly char[] _portableInvalidFileNameChars =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] _windowsReservedNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    ];

    /// <summary>
    /// Gets the comparer used for exported paths. Case-insensitive comparison prevents an export
    /// produced on one filesystem from becoming unsafe when moved to another filesystem.
    /// </summary>
    public static StringComparer PortablePathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Normalizes and validates an export root.
    /// </summary>
    /// <param name="path">Configured export root.</param>
    /// <returns>The normalized absolute path.</returns>
    public static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An export root is required.", nameof(path));
        }

        string result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        string? filesystemRoot = Path.GetPathRoot(result);
        if (filesystemRoot != null
            && PortablePathComparer.Equals(
                Path.TrimEndingDirectorySeparator(filesystemRoot),
                result))
        {
            throw new ArgumentException("The filesystem root cannot be used as an STRM export root.", nameof(path));
        }

        return result;
    }

    /// <summary>
    /// Determines whether two roots are equal or one contains the other.
    /// </summary>
    /// <param name="firstRoot">First normalized root.</param>
    /// <param name="secondRoot">Second normalized root.</param>
    /// <returns><see langword="true"/> when the roots overlap.</returns>
    public static bool RootsOverlap(string firstRoot, string secondRoot)
    {
        string first = NormalizeRoot(firstRoot);
        string second = NormalizeRoot(secondRoot);
        return PortablePathComparer.Equals(first, second)
            || IsWithinRoot(first, second)
            || IsWithinRoot(second, first);
    }

    /// <summary>
    /// Builds a collision-safe movie path containing its stable provider identifier.
    /// </summary>
    /// <param name="title">Clean display title.</param>
    /// <param name="streamId">Xtream stream identifier.</param>
    /// <param name="containerExtension">Provider container extension.</param>
    /// <returns>A relative STRM path.</returns>
    public static string BuildVodRelativePath(string title, int streamId, string? containerExtension)
    {
        string identity = $"xtream-vod-{streamId.ToString(CultureInfo.InvariantCulture)}";
        string directory = WithIdentity(title, identity, MaxDirectoryNameLength);
        string sourceExtension = SafeContainerExtension(containerExtension);
        string suffix = sourceExtension.Length == 0 ? ".strm" : $".{sourceExtension}.strm";
        string fileName = WithIdentity(title, identity, MaxFileNameLength - suffix.Length) + suffix;
        return NormalizeRelativePath(Path.Combine(directory, fileName));
    }

    /// <summary>
    /// Builds a collision-safe series directory containing its stable provider identifier.
    /// </summary>
    /// <param name="title">Clean display title.</param>
    /// <param name="seriesId">Xtream series identifier.</param>
    /// <returns>A relative series directory.</returns>
    public static string BuildSeriesRelativeDirectory(string title, int seriesId)
    {
        string identity = $"xtream-series-{seriesId.ToString(CultureInfo.InvariantCulture)}";
        return WithIdentity(title, identity, MaxDirectoryNameLength);
    }

    /// <summary>
    /// Builds a collision-safe episode path containing the stable series and episode identifiers.
    /// </summary>
    /// <param name="seriesTitle">Clean series title.</param>
    /// <param name="seriesId">Xtream series identifier.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="episodeTitle">Clean episode title.</param>
    /// <param name="episodeId">Xtream episode identifier.</param>
    /// <returns>A relative STRM path.</returns>
    public static string BuildEpisodeRelativePath(
        string seriesTitle,
        int seriesId,
        int seasonNumber,
        int episodeNumber,
        string episodeTitle,
        int episodeId)
    {
        string seriesDirectory = BuildSeriesRelativeDirectory(seriesTitle, seriesId);
        string seasonDirectory = $"Season {seasonNumber.ToString("00", CultureInfo.InvariantCulture)}";
        string displayName = $"S{seasonNumber.ToString("00", CultureInfo.InvariantCulture)}E{episodeNumber.ToString("00", CultureInfo.InvariantCulture)} - {SafePathPart(episodeTitle, MaxEpisodeTitleLength)}";
        string identity = $"xtream-episode-{episodeId.ToString(CultureInfo.InvariantCulture)}";
        string fileName = WithIdentity(displayName, identity, MaxFileNameLength - ".strm".Length) + ".strm";
        return NormalizeRelativePath(Path.Combine(seriesDirectory, seasonDirectory, fileName));
    }

    /// <summary>
    /// Resolves a generated relative path beneath an export root.
    /// </summary>
    /// <param name="rootPath">Normalized export root.</param>
    /// <param name="relativePath">Generated relative path.</param>
    /// <returns>The absolute managed path.</returns>
    public static string ResolveGeneratedPath(string rootPath, string relativePath)
    {
        if (!TryResolveManagedStrmPath(rootPath, relativePath, out string? fullPath))
        {
            throw new InvalidOperationException("Generated STRM path escaped the configured export root.");
        }

        return fullPath!;
    }

    /// <summary>
    /// Safely resolves a manifest-owned STRM path.
    /// </summary>
    /// <param name="rootPath">Normalized export root.</param>
    /// <param name="relativePath">Manifest relative path.</param>
    /// <param name="fullPath">Resolved absolute path.</param>
    /// <returns><see langword="true"/> if the path is a safe STRM file beneath the root.</returns>
    public static bool TryResolveManagedStrmPath(string rootPath, string relativePath, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        string platformRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string[] segments = platformRelativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(platformRelativePath), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalizedRoot = NormalizeRoot(rootPath);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, platformRelativePath));
        if (!IsWithinRoot(normalizedRoot, candidate))
        {
            return false;
        }

        if (ContainsReparsePoint(normalizedRoot, candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Normalizes path separators for storage in the manifest.
    /// </summary>
    /// <param name="relativePath">Platform relative path.</param>
    /// <returns>Manifest relative path.</returns>
    public static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        string rootWithSeparator = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsReparsePoint(string rootPath, string candidatePath)
    {
        if (Directory.Exists(rootPath)
            && File.GetAttributes(rootPath).HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        string relativePath = Path.GetRelativePath(rootPath, candidatePath);
        string[] segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        string currentPath = rootPath;
        foreach (string segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((Directory.Exists(currentPath) || File.Exists(currentPath))
                && File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    private static string SafeContainerExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        string result = new(extension
            .Trim()
            .TrimStart('.')
            .Where(character => char.IsAsciiLetterOrDigit(character))
            .Take(16)
            .ToArray());
        return result;
    }

    private static string WithIdentity(string title, string identity, int maxLength)
    {
        string suffix = $" [{identity}]";
        int titleLength = Math.Max(1, maxLength - suffix.Length);
        return SafePathPart(title, titleLength) + suffix;
    }

    private static string SafePathPart(string name, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        string normalized = (name ?? string.Empty).Normalize(NormalizationForm.FormKC);
        StringBuilder builder = new(normalized.Length);
        bool previousWasWhitespace = false;
        foreach (char character in normalized)
        {
            bool replace = char.IsControl(character)
                || char.IsWhiteSpace(character)
                || _portableInvalidFileNameChars.Contains(character);
            if (replace)
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        string result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            result = "Unknown";
        }

        if (result.Length > maxLength)
        {
            result = result[..maxLength].Trim().TrimEnd('.', ' ');
        }

        if (_windowsReservedNames.Contains(result, StringComparer.OrdinalIgnoreCase))
        {
            result = "_" + result;
        }

        return result;
    }
}
