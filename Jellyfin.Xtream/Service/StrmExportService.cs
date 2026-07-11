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
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Exports selected VOD and series streams as STRM files for normal Jellyfin libraries.
/// </summary>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="xtreamClient">Xtream API client.</param>
/// <param name="nameNormalizationService">Name normalization service.</param>
/// <param name="streamProxyUrlBuilder">Signed Jellyfin stream proxy URL builder.</param>
public class StrmExportService(
    ILogger<StrmExportService> logger,
    IXtreamClient xtreamClient,
    NameNormalizationService nameNormalizationService,
    StreamProxyUrlBuilder streamProxyUrlBuilder)
{
    private static readonly SemaphoreSlim _exportGate = new(1, 1);

    /// <summary>
    /// Exports configured STRM files.
    /// </summary>
    /// <param name="progress">The progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ExportAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExportRunSnapshot snapshot = ExportRunSnapshot.Capture(Plugin.Instance.Configuration);
            NameNormalizationSnapshot namingSnapshot = nameNormalizationService.CreateSnapshot();
            ValidateRoots(snapshot);

            List<Func<Action<double>, CancellationToken, Task>> enabledExports = [];
            if (snapshot.VodRoot != null)
            {
                enabledExports.Add((report, token) => ExportVodAsync(snapshot, namingSnapshot, report, token));
            }

            if (snapshot.SeriesRoot != null)
            {
                enabledExports.Add((report, token) => ExportSeriesAsync(snapshot, namingSnapshot, report, token));
            }

            if (enabledExports.Count == 0)
            {
                progress.Report(100);
                return;
            }

            progress.Report(0);
            for (int index = 0; index < enabledExports.Count; index++)
            {
                double phaseStart = index * 100.0 / enabledExports.Count;
                double phaseSize = 100.0 / enabledExports.Count;
                void ReportPhaseProgress(double phaseProgress)
                {
                    progress.Report(phaseStart + (Math.Clamp(phaseProgress, 0, 100) * phaseSize / 100));
                }

                ReportPhaseProgress(0);
                await enabledExports[index](ReportPhaseProgress, cancellationToken).ConfigureAwait(false);
                ReportPhaseProgress(100);
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static void ValidateRoots(ExportRunSnapshot snapshot)
    {
        if (snapshot.VodRoot != null
            && snapshot.SeriesRoot != null
            && StrmExportPathPolicy.RootsOverlap(snapshot.VodRoot, snapshot.SeriesRoot))
        {
            throw new InvalidOperationException("VOD and series STRM export roots must not be equal or nested.");
        }
    }

    private async Task ExportVodAsync(
        ExportRunSnapshot snapshot,
        NameNormalizationSnapshot namingSnapshot,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        string rootPath = snapshot.VodRoot
            ?? throw new InvalidOperationException("The VOD export root was not captured.");
        Directory.CreateDirectory(rootPath);
        StrmExportManifestStore manifestStore = new(rootPath, "vod");
        StrmExportManifestLoadResult previousManifest = await manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        List<StrmExportManifestEntry> expectedEntries = [];
        List<StreamInfo> streamsToExport = [];
        bool hasFailures = false;

        int categoriesProcessed = 0;
        foreach (CategorySelection selection in snapshot.VodSelections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                List<StreamInfo> streams = await xtreamClient.GetVodStreamsByCategoryAsync(
                    snapshot.ConnectionInfo,
                    selection.CategoryId,
                    cancellationToken).ConfigureAwait(false);
                foreach (StreamInfo stream in streams.Where(stream => selection.Includes(stream.StreamId)))
                {
                    if (stream.StreamId <= 0)
                    {
                        hasFailures = true;
                        logger.LogWarning(
                            "Skipping a VOD STRM export entry because the provider returned a non-positive stream identifier.");
                        continue;
                    }

                    streamsToExport.Add(stream);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hasFailures = true;
                logger.LogWarning(
                    ex,
                    "Skipping VOD STRM export for category {CategoryId} because the provider request failed.",
                    selection.CategoryId);
            }

            categoriesProcessed++;
            progress(snapshot.VodSelections.Count == 0
                ? 10
                : categoriesProcessed * 10.0 / snapshot.VodSelections.Count);
        }

        streamsToExport = streamsToExport
            .GroupBy(stream => stream.StreamId)
            .Select(group => group
                .OrderBy(stream => stream.Name, StringComparer.Ordinal)
                .ThenBy(stream => stream.ContainerExtension, StringComparer.Ordinal)
                .First())
            .OrderBy(stream => stream.StreamId)
            .ToList();

        int streamsProcessed = 0;
        foreach (StreamInfo stream in streamsToExport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string title = NormalizeExportTitle(namingSnapshot, stream.Name, NameScope.Vod);
                string relativePath = StrmExportPathPolicy.BuildVodRelativePath(
                    title,
                    stream.StreamId,
                    stream.ContainerExtension);
                string path = StrmExportPathPolicy.ResolveGeneratedPath(rootPath, relativePath);
                string url = streamProxyUrlBuilder.BuildPersistentStrm(
                    snapshot.ConnectionInfo,
                    snapshot.ConfigurationFingerprint,
                    snapshot.PublicServerUrl,
                    StreamType.Vod,
                    stream.StreamId,
                    stream.ContainerExtension);

                logger.LogDebug("Exporting VOD STRM file for stream {StreamId}.", stream.StreamId);
                await StrmExportManifestStore.WriteTextAtomicallyAsync(
                    path,
                    url + Environment.NewLine,
                    cancellationToken).ConfigureAwait(false);
                expectedEntries.Add(new(
                    $"vod:{stream.StreamId.ToString(CultureInfo.InvariantCulture)}",
                    relativePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hasFailures = true;
                logger.LogError(ex, "Failed to export VOD STRM file for stream {StreamId}.", stream.StreamId);
            }

            streamsProcessed++;
            progress(10 + (streamsToExport.Count == 0
                ? 85
                : streamsProcessed * 85.0 / streamsToExport.Count));
        }

        progress(95);
        await ReconcileIfSafeAsync(
            "VOD",
            manifestStore,
            previousManifest,
            expectedEntries,
            hasFailures,
            hasSuspiciousEmptyResult: snapshot.VodSelections.Count > 0 && expectedEntries.Count == 0,
            cancellationToken).ConfigureAwait(false);
        progress(100);
    }

    private async Task ExportSeriesAsync(
        ExportRunSnapshot snapshot,
        NameNormalizationSnapshot namingSnapshot,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        string rootPath = snapshot.SeriesRoot
            ?? throw new InvalidOperationException("The series export root was not captured.");
        Directory.CreateDirectory(rootPath);
        StrmExportManifestStore manifestStore = new(rootPath, "series");
        StrmExportManifestLoadResult previousManifest = await manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        List<StrmExportManifestEntry> expectedEntries = [];
        List<Series> seriesToExport = [];
        bool hasFailures = false;
        bool hasSuspiciousEmptySeries = false;

        int categoriesProcessed = 0;
        foreach (CategorySelection selection in snapshot.SeriesSelections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                List<Series> seriesItems = await xtreamClient.GetSeriesByCategoryAsync(
                    snapshot.ConnectionInfo,
                    selection.CategoryId,
                    cancellationToken).ConfigureAwait(false);
                foreach (Series series in seriesItems.Where(series => selection.Includes(series.SeriesId)))
                {
                    if (series.SeriesId <= 0)
                    {
                        hasFailures = true;
                        logger.LogWarning(
                            "Skipping a series STRM export entry because the provider returned a non-positive series identifier.");
                        continue;
                    }

                    seriesToExport.Add(series);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hasFailures = true;
                logger.LogWarning(
                    ex,
                    "Skipping series STRM export for category {CategoryId} because the provider request failed.",
                    selection.CategoryId);
            }

            categoriesProcessed++;
            progress(snapshot.SeriesSelections.Count == 0
                ? 10
                : categoriesProcessed * 10.0 / snapshot.SeriesSelections.Count);
        }

        seriesToExport = seriesToExport
            .GroupBy(series => series.SeriesId)
            .Select(group => group.OrderBy(series => series.Name, StringComparer.Ordinal).First())
            .OrderBy(series => series.SeriesId)
            .ToList();

        int seriesProcessed = 0;
        foreach (Series series in seriesToExport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                int episodeCount = await ExportSeriesItemAsync(
                    snapshot,
                    namingSnapshot,
                    rootPath,
                    series,
                    expectedEntries,
                    seriesProgress => progress(
                        10 + ((seriesProcessed + (Math.Clamp(seriesProgress, 0, 100) / 100))
                              * 85.0 / seriesToExport.Count)),
                    cancellationToken).ConfigureAwait(false);
                hasSuspiciousEmptySeries |= episodeCount == 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hasFailures = true;
                logger.LogError(ex, "Failed to export series STRM files for series {SeriesId}.", series.SeriesId);
            }
            finally
            {
                seriesProcessed++;
                if (seriesToExport.Count > 0)
                {
                    progress(10 + (seriesProcessed * 85.0 / seriesToExport.Count));
                }
            }
        }

        progress(95);
        await ReconcileIfSafeAsync(
            "series",
            manifestStore,
            previousManifest,
            expectedEntries,
            hasFailures,
            hasSuspiciousEmptySeries || (snapshot.SeriesSelections.Count > 0 && expectedEntries.Count == 0),
            cancellationToken).ConfigureAwait(false);
        progress(100);
    }

    private async Task<int> ExportSeriesItemAsync(
        ExportRunSnapshot snapshot,
        NameNormalizationSnapshot namingSnapshot,
        string rootPath,
        Series series,
        List<StrmExportManifestEntry> expectedEntries,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        string seriesTitle = NormalizeExportTitle(namingSnapshot, series.Name, NameScope.Series);
        SeriesStreamInfo seriesInfo = await xtreamClient.GetSeriesStreamsBySeriesAsync(
            snapshot.ConnectionInfo,
            series.SeriesId,
            cancellationToken).ConfigureAwait(false);
        List<EpisodeExport> episodesToExport = seriesInfo.Episodes
            .SelectMany(season => season.Value.Select(episode => new EpisodeExport(season.Key, episode)))
            .GroupBy(episode => episode.Episode.EpisodeId)
            .Select(group => group
                .OrderBy(episode => episode.SeasonNumber)
                .ThenBy(episode => episode.Episode.EpisodeNum)
                .ThenBy(episode => episode.Episode.Title, StringComparer.Ordinal)
                .First())
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.Episode.EpisodeNum)
            .ThenBy(episode => episode.Episode.EpisodeId)
            .ToList();
        if (episodesToExport.Any(episode => episode.Episode.EpisodeId <= 0))
        {
            throw new InvalidDataException(
                "The provider returned a non-positive episode identifier; this series export was skipped.");
        }

        int episodesProcessed = 0;
        foreach (EpisodeExport episodeExport in episodesToExport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Episode episode = episodeExport.Episode;
            string episodeTitle = NormalizeExportTitle(namingSnapshot, episode.Title, NameScope.Episode);
            string relativePath = StrmExportPathPolicy.BuildEpisodeRelativePath(
                seriesTitle,
                series.SeriesId,
                episodeExport.SeasonNumber,
                episode.EpisodeNum,
                episodeTitle,
                episode.EpisodeId);
            string path = StrmExportPathPolicy.ResolveGeneratedPath(rootPath, relativePath);
            string url = streamProxyUrlBuilder.BuildPersistentStrm(
                snapshot.ConnectionInfo,
                snapshot.ConfigurationFingerprint,
                snapshot.PublicServerUrl,
                StreamType.Series,
                episode.EpisodeId,
                episode.ContainerExtension);

            logger.LogDebug(
                "Exporting series STRM file for series {SeriesId}, episode {EpisodeId}.",
                series.SeriesId,
                episode.EpisodeId);
            await StrmExportManifestStore.WriteTextAtomicallyAsync(
                path,
                url + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            expectedEntries.Add(new(
                $"series:{series.SeriesId.ToString(CultureInfo.InvariantCulture)}:episode:{episode.EpisodeId.ToString(CultureInfo.InvariantCulture)}",
                relativePath));

            episodesProcessed++;
            progress(episodesToExport.Count == 0
                ? 100
                : episodesProcessed * 100.0 / episodesToExport.Count);
        }

        progress(100);
        return episodesToExport.Count;
    }

    internal static string NormalizeExportTitle(
        NameNormalizationSnapshot namingSnapshot,
        string? title,
        NameScope contentScope)
    {
        ArgumentNullException.ThrowIfNull(namingSnapshot);
        return namingSnapshot.Normalize(title, contentScope | NameScope.Filesystem).Title;
    }

    private async Task ReconcileIfSafeAsync(
        string exportKind,
        StrmExportManifestStore manifestStore,
        StrmExportManifestLoadResult previousManifest,
        List<StrmExportManifestEntry> expectedEntries,
        bool hasFailures,
        bool hasSuspiciousEmptyResult,
        CancellationToken cancellationToken)
    {
        if (hasFailures)
        {
            logger.LogWarning(
                "Skipping stale {ExportKind} STRM reconciliation because one or more exports failed.",
                exportKind);
            return;
        }

        if (hasSuspiciousEmptyResult)
        {
            logger.LogWarning(
                "Skipping stale {ExportKind} STRM reconciliation because the export result was unexpectedly empty.",
                exportKind);
            return;
        }

        if (previousManifest.State == StrmExportManifestState.Invalid)
        {
            logger.LogWarning(
                "Skipping stale {ExportKind} STRM reconciliation because the ownership manifest is invalid: {Reason}",
                exportKind,
                previousManifest.Error);
            return;
        }

        int deleted = await manifestStore.ReconcileAndCommitAsync(
            previousManifest,
            expectedEntries,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Reconciled {Count} {ExportKind} STRM entries and removed {DeletedCount} stale managed files.",
            expectedEntries.Count,
            exportKind,
            deleted);
    }

    private sealed record CategorySelection(int CategoryId, HashSet<int> ItemIds)
    {
        public bool Includes(int itemId)
        {
            return ItemIds.Count == 0 || ItemIds.Contains(itemId);
        }
    }

    private sealed record EpisodeExport(int SeasonNumber, Episode Episode);

    private sealed class ExportRunSnapshot
    {
        private ExportRunSnapshot(
            ConnectionInfo connectionInfo,
            string configurationFingerprint,
            string publicServerUrl,
            string? vodRoot,
            string? seriesRoot,
            IReadOnlyList<CategorySelection> vodSelections,
            IReadOnlyList<CategorySelection> seriesSelections)
        {
            ConnectionInfo = connectionInfo;
            ConfigurationFingerprint = configurationFingerprint;
            PublicServerUrl = publicServerUrl;
            VodRoot = vodRoot;
            SeriesRoot = seriesRoot;
            VodSelections = vodSelections;
            SeriesSelections = seriesSelections;
        }

        public ConnectionInfo ConnectionInfo { get; }

        public string ConfigurationFingerprint { get; }

        public string PublicServerUrl { get; }

        public string? VodRoot { get; }

        public string? SeriesRoot { get; }

        public IReadOnlyList<CategorySelection> VodSelections { get; }

        public IReadOnlyList<CategorySelection> SeriesSelections { get; }

        public static ExportRunSnapshot Capture(PluginConfiguration configuration)
        {
            string? vodRoot = configuration.IsVodStrmExportEnabled
                              && !string.IsNullOrWhiteSpace(configuration.VodStrmExportPath)
                ? StrmExportPathPolicy.NormalizeRoot(configuration.VodStrmExportPath)
                : null;
            string? seriesRoot = configuration.IsSeriesStrmExportEnabled
                                 && !string.IsNullOrWhiteSpace(configuration.SeriesStrmExportPath)
                ? StrmExportPathPolicy.NormalizeRoot(configuration.SeriesStrmExportPath)
                : null;
            string baseUrl = vodRoot != null || seriesRoot != null
                ? NormalizeBaseUrl(configuration.BaseUrl)
                : "https://example.invalid";
            string username = configuration.Username;
            string password = configuration.Password;
            string publicServerUrl = configuration.PublicServerUrl;

            return new(
                new ConnectionInfo(baseUrl, username, password),
                StreamProxyConfigurationFingerprint.Create(configuration),
                publicServerUrl,
                vodRoot,
                seriesRoot,
                CaptureSelections(configuration.Vod),
                CaptureSelections(configuration.Series));
        }

        private static List<CategorySelection> CaptureSelections(
            SerializableDictionary<int, HashSet<int>> selections)
        {
            return selections
                .OrderBy(selection => selection.Key)
                .Select(selection => new CategorySelection(selection.Key, new HashSet<int>(selection.Value)))
                .ToList();
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidOperationException("The Xtream base URL must be an absolute HTTP(S) URL without a query or fragment.");
            }

            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
    }
}
