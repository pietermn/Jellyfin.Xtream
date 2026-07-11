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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Validates provider identifiers against the administrator's configured selections.
/// </summary>
internal static class StreamAuthorization
{
    /// <summary>
    /// Determines whether a category is selected.
    /// </summary>
    /// <param name="selections">The configured category and item selections.</param>
    /// <param name="categoryId">The provider category identifier.</param>
    /// <returns><see langword="true"/> when the category is selected.</returns>
    public static bool IsCategorySelected(
        IReadOnlyDictionary<int, HashSet<int>> selections,
        int categoryId)
    {
        ArgumentNullException.ThrowIfNull(selections);
        return selections.ContainsKey(categoryId);
    }

    /// <summary>
    /// Determines whether an item is selected within its category.
    /// An empty item set represents every item in the category.
    /// </summary>
    /// <param name="selections">The configured category and item selections.</param>
    /// <param name="categoryId">The provider category identifier.</param>
    /// <param name="itemId">The provider item identifier.</param>
    /// <returns><see langword="true"/> when the item is selected.</returns>
    public static bool IsItemSelected(
        IReadOnlyDictionary<int, HashSet<int>> selections,
        int categoryId,
        int itemId)
    {
        ArgumentNullException.ThrowIfNull(selections);
        return itemId > 0
            && selections.TryGetValue(categoryId, out HashSet<int>? items)
            && (items.Count == 0 || items.Contains(itemId));
    }

    /// <summary>
    /// Rejects a category that is outside the configured selection.
    /// </summary>
    /// <param name="selections">The configured category and item selections.</param>
    /// <param name="categoryId">The provider category identifier.</param>
    /// <param name="contentKind">A non-sensitive description used in the exception.</param>
    public static void EnsureCategorySelected(
        IReadOnlyDictionary<int, HashSet<int>> selections,
        int categoryId,
        string contentKind)
    {
        if (!IsCategorySelected(selections, categoryId))
        {
            throw Denied(contentKind);
        }
    }

    /// <summary>
    /// Rejects an item that is outside the configured selection.
    /// </summary>
    /// <param name="selections">The configured category and item selections.</param>
    /// <param name="categoryId">The provider category identifier.</param>
    /// <param name="itemId">The provider item identifier.</param>
    /// <param name="contentKind">A non-sensitive description used in the exception.</param>
    public static void EnsureItemSelected(
        IReadOnlyDictionary<int, HashSet<int>> selections,
        int categoryId,
        int itemId,
        string contentKind)
    {
        if (!IsItemSelected(selections, categoryId, itemId))
        {
            throw Denied(contentKind);
        }
    }

    /// <summary>
    /// Rejects a Live TV stream that is outside the configured selection.
    /// Legacy Live TV identifiers contain no category, so an unrestricted category
    /// requires a catalog membership check before the identifier can be trusted.
    /// </summary>
    /// <param name="xtreamClient">The provider client.</param>
    /// <param name="selections">The configured Live TV selections.</param>
    /// <param name="connection">The provider connection.</param>
    /// <param name="streamId">The decoded provider stream identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the authorization check.</returns>
    public static async Task EnsureLiveStreamSelectedAsync(
        IXtreamClient xtreamClient,
        IReadOnlyDictionary<int, HashSet<int>> selections,
        ConnectionInfo connection,
        int streamId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xtreamClient);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        if (streamId <= 0)
        {
            throw Denied("Live TV stream");
        }

        // An explicit item selection is sufficient and avoids a provider round trip.
        if (selections.Values.Any(items => items.Contains(streamId)))
        {
            return;
        }

        HashSet<int> unrestrictedCategories = new(
            selections.Where(selection => selection.Value.Count == 0).Select(selection => selection.Key));
        if (unrestrictedCategories.Count == 0)
        {
            // Reject before any provider request when the id cannot possibly be selected.
            throw Denied("Live TV stream");
        }

        List<StreamInfo> streams = await xtreamClient
            .GetLiveStreamsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        bool selected = streams.Any(stream =>
            stream.StreamId == streamId
            && stream.CategoryId.HasValue
            && unrestrictedCategories.Contains(stream.CategoryId.Value));
        if (!selected)
        {
            throw Denied("Live TV stream");
        }
    }

    /// <summary>
    /// Fetches series details only after the series selection has been authorized,
    /// then verifies that the provider response still belongs to the encoded category.
    /// </summary>
    /// <param name="xtreamClient">The provider client.</param>
    /// <param name="selections">The configured series selections.</param>
    /// <param name="connection">The provider connection.</param>
    /// <param name="categoryId">The decoded category identifier.</param>
    /// <param name="seriesId">The decoded series identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authorized provider series details.</returns>
    public static async Task<SeriesStreamInfo> GetAuthorizedSeriesAsync(
        IXtreamClient xtreamClient,
        IReadOnlyDictionary<int, HashSet<int>> selections,
        ConnectionInfo connection,
        int categoryId,
        int seriesId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xtreamClient);
        ArgumentNullException.ThrowIfNull(connection);
        EnsureItemSelected(selections, categoryId, seriesId, "series");
        cancellationToken.ThrowIfCancellationRequested();

        HashSet<int> configuredSeries = selections[categoryId];
        if (configuredSeries.Count == 0)
        {
            // An unrestricted category does not itself prove that a crafted id belongs
            // to that category. Verify membership without calling the targeted detail API.
            List<Series> catalog = await xtreamClient
                .GetSeriesByCategoryAsync(connection, categoryId, cancellationToken)
                .ConfigureAwait(false);
            // The category-specific endpoint proves membership. Several Xtream variants omit
            // category_id from each row, which deserializes as zero.
            bool belongsToCategory = catalog.Any(series =>
                series.SeriesId == seriesId
                && (series.CategoryId == 0 || series.CategoryId == categoryId));
            if (!belongsToCategory)
            {
                throw Denied("series");
            }
        }

        SeriesStreamInfo series = await xtreamClient
            .GetSeriesStreamsBySeriesAsync(connection, seriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series.Info.CategoryId != 0 && series.Info.CategoryId != categoryId)
        {
            throw Denied("series");
        }

        return series;
    }

    /// <summary>
    /// Gets a season only when it exists in an already-authorized series response.
    /// </summary>
    /// <param name="series">The authorized provider series details.</param>
    /// <param name="seasonId">The decoded season identifier.</param>
    /// <returns>The provider episodes in the authorized season.</returns>
    public static ICollection<Episode> GetAuthorizedSeason(
        SeriesStreamInfo series,
        int seasonId)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!series.Episodes.TryGetValue(seasonId, out ICollection<Episode>? episodes))
        {
            throw Denied("season");
        }

        return episodes;
    }

    /// <summary>
    /// Rejects an episode identifier that does not belong to the authorized season response.
    /// </summary>
    /// <param name="series">The authorized provider series details.</param>
    /// <param name="seasonId">The authorized season identifier.</param>
    /// <param name="episodeId">The provider episode identifier.</param>
    public static void EnsureEpisodeSelected(
        SeriesStreamInfo series,
        int seasonId,
        int episodeId)
    {
        ICollection<Episode> episodes = GetAuthorizedSeason(series, seasonId);
        if (episodeId <= 0 || !episodes.Any(episode => episode.EpisodeId == episodeId))
        {
            throw Denied("episode");
        }
    }

    private static UnauthorizedAccessException Denied(string contentKind)
    {
        return new UnauthorizedAccessException($"The requested {contentKind} is not selected.");
    }
}
