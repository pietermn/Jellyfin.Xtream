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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Exports selected VOD and series streams as STRM files for normal Jellyfin libraries.
/// </summary>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class StrmExportService(ILogger<StrmExportService> logger)
{
    private const int MaxDirectoryNameLength = 96;
    private const int MaxEpisodeTitleLength = 72;
    private const int MaxFileNameLength = 180;
    private static readonly char[] _invalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Exports configured STRM files.
    /// </summary>
    /// <param name="progress">The progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ExportAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        List<Func<CancellationToken, Task>> enabledExports = [];

        if (config.IsVodStrmExportEnabled && !string.IsNullOrWhiteSpace(config.VodStrmExportPath))
        {
            enabledExports.Add(ExportVodAsync);
        }

        if (config.IsSeriesStrmExportEnabled && !string.IsNullOrWhiteSpace(config.SeriesStrmExportPath))
        {
            enabledExports.Add(ExportSeriesAsync);
        }

        if (enabledExports.Count == 0)
        {
            progress.Report(100);
            return;
        }

        for (int i = 0; i < enabledExports.Count; i++)
        {
            await enabledExports[i](cancellationToken).ConfigureAwait(false);
            progress.Report((i + 1) * 100.0 / enabledExports.Count);
        }
    }

    private static string GetStreamUrl(StreamType type, int id, string? extension)
    {
        return Plugin.Instance.StreamService.GetMediaSourceInfo(type, id, extension).Path;
    }

    private static bool IsConfigured(SerializableDictionary<int, HashSet<int>> config, int category, int id)
    {
        return config.TryGetValue(category, out HashSet<int>? values) && (values.Count == 0 || values.Contains(id));
    }

    private static string SafePathPart(string name, int maxLength = MaxDirectoryNameLength)
    {
        string result = _invalidFileNameChars.Aggregate(name, (current, c) => current.Replace(c, ' '));
        result = result.Trim().TrimEnd('.');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].Trim().TrimEnd('.');
        }

        return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
    }

    private static string BuildFileName(string name, string extension)
    {
        string safeExtension = extension.Length > 0 && extension[0] == '.' ? extension : $".{extension}";
        int maxNameLength = MaxFileNameLength - safeExtension.Length;
        return $"{SafePathPart(name, maxNameLength)}{safeExtension}";
    }

    private static async Task WriteStrmFileAsync(string path, string url, HashSet<string> expectedPaths, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException("Invalid STRM path", nameof(path)));
        string content = url + Environment.NewLine;
        expectedPaths.Add(Path.GetFullPath(path));
        if (File.Exists(path) && string.Equals(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), content, StringComparison.Ordinal))
        {
            return;
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteStaleStrmFiles(string rootPath, HashSet<string> expectedPaths)
    {
        foreach (string path in Directory.EnumerateFiles(rootPath, "*.strm", SearchOption.AllDirectories))
        {
            if (!expectedPaths.Contains(Path.GetFullPath(path)))
            {
                File.Delete(path);
            }
        }

        foreach (string path in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
    }

    private async Task ExportVodAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        Directory.CreateDirectory(config.VodStrmExportPath);
        HashSet<string> expectedPaths = new(StringComparer.Ordinal);
        bool hasFailures = false;

        foreach (KeyValuePair<int, HashSet<int>> categoryConfig in config.Vod)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<StreamInfo> streams = await Plugin.Instance.StreamService.GetVodStreams(categoryConfig.Key, cancellationToken).ConfigureAwait(false);
            foreach (StreamInfo stream in streams.Where(stream => IsConfigured(config.Vod, categoryConfig.Key, stream.StreamId)))
            {
                try
                {
                    ParsedName parsedName = StreamService.ParseName(stream.Name);
                    string movieName = SafePathPart(parsedName.Title);
                    string containerExtension = string.IsNullOrWhiteSpace(stream.ContainerExtension) ? "strm" : $"{SafePathPart(stream.ContainerExtension, 16)}.strm";
                    string fileName = BuildFileName($"{movieName} [{stream.StreamId}]", containerExtension);
                    string path = Path.Combine(config.VodStrmExportPath, movieName, fileName);
                    string url = GetStreamUrl(StreamType.Vod, stream.StreamId, stream.ContainerExtension);

                    logger.LogDebug("Exporting VOD STRM file {Path}", path);
                    await WriteStrmFileAsync(path, url, expectedPaths, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    hasFailures = true;
                    logger.LogError(ex, "Failed to export VOD STRM file for stream {StreamId}", stream.StreamId);
                }
            }
        }

        if (hasFailures)
        {
            logger.LogWarning("Skipping stale VOD STRM cleanup because one or more VOD exports failed.");
            return;
        }

        DeleteStaleStrmFiles(config.VodStrmExportPath, expectedPaths);
    }

    private async Task ExportSeriesAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        Directory.CreateDirectory(config.SeriesStrmExportPath);
        HashSet<string> expectedPaths = new(StringComparer.Ordinal);
        bool hasFailures = false;

        foreach (KeyValuePair<int, HashSet<int>> categoryConfig in config.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<Series> seriesItems = await Plugin.Instance.StreamService.GetSeries(categoryConfig.Key, cancellationToken).ConfigureAwait(false);
            foreach (Series series in seriesItems.Where(series => IsConfigured(config.Series, categoryConfig.Key, series.SeriesId)))
            {
                try
                {
                    await ExportSeriesItemAsync(series, expectedPaths, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    hasFailures = true;
                    logger.LogError(ex, "Failed to export series STRM files for series {SeriesId}", series.SeriesId);
                }
            }
        }

        if (hasFailures)
        {
            logger.LogWarning("Skipping stale series STRM cleanup because one or more series exports failed.");
            return;
        }

        DeleteStaleStrmFiles(config.SeriesStrmExportPath, expectedPaths);
    }

    private async Task ExportSeriesItemAsync(Series series, HashSet<string> expectedPaths, CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        ParsedName seriesName = StreamService.ParseName(series.Name);
        string safeSeriesName = SafePathPart(seriesName.Title);
        SeriesStreamInfo seriesInfo = await Plugin.Instance.StreamService.GetSeriesStreamsBySeriesAsync(series.SeriesId, cancellationToken).ConfigureAwait(false);

        foreach (KeyValuePair<int, ICollection<Episode>> season in seriesInfo.Episodes.OrderBy(season => season.Key))
        {
            string seasonFolder = $"Season {season.Key.ToString("00", CultureInfo.InvariantCulture)}";
            foreach (Episode episode in season.Value.OrderBy(episode => episode.EpisodeNum))
            {
                ParsedName episodeName = StreamService.ParseName(episode.Title);
                string safeEpisodeName = SafePathPart(episodeName.Title, MaxEpisodeTitleLength);
                string episodeNumber = episode.EpisodeNum.ToString("00", CultureInfo.InvariantCulture);
                string fileName = BuildFileName($"S{season.Key.ToString("00", CultureInfo.InvariantCulture)}E{episodeNumber} - {safeEpisodeName} [{episode.EpisodeId}]", "strm");
                string path = Path.Combine(config.SeriesStrmExportPath, safeSeriesName, seasonFolder, fileName);
                string url = GetStreamUrl(StreamType.Series, episode.EpisodeId, episode.ContainerExtension);

                logger.LogDebug("Exporting series STRM file {Path}", path);
                await WriteStrmFileAsync(path, url, expectedPaths, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
