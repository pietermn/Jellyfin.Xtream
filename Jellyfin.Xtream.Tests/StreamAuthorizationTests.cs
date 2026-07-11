using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Tests;

public class StreamAuthorizationTests
{
    private static readonly ConnectionInfo Connection = new("https://provider.example", "user", "password");

    [Fact]
    public async Task CraftedUnselectedLiveIdIsRejectedWithoutProviderCall()
    {
        StubClient client = new();
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [101],
        };
        Guid crafted = StreamService.ToGuid(StreamService.LiveTvPrefix, 999, 0, 0);
        StreamService.FromGuid(crafted, out _, out int streamId, out _, out _);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            StreamAuthorization.EnsureLiveStreamSelectedAsync(
                client,
                selections,
                Connection,
                streamId,
                CancellationToken.None));

        Assert.Equal(0, client.LiveStreamCalls);
    }

    [Fact]
    public async Task LiveWildcardRequiresMembershipInSelectedCategory()
    {
        StubClient client = new()
        {
            LiveStreams =
            [
                new StreamInfo { CategoryId = 8, StreamId = 999 },
            ],
        };
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [],
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            StreamAuthorization.EnsureLiveStreamSelectedAsync(
                client,
                selections,
                Connection,
                999,
                CancellationToken.None));

        Assert.Equal(1, client.LiveStreamCalls);
    }

    [Fact]
    public void CraftedUnselectedCatchupIdIsRejected()
    {
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [101],
        };
        Guid crafted = StreamService.ToGuid(StreamService.CatchupPrefix, 7, 999, 20_000);
        StreamService.FromGuid(crafted, out _, out int categoryId, out int channelId, out _);

        Assert.Throws<UnauthorizedAccessException>(() =>
            StreamAuthorization.EnsureItemSelected(selections, categoryId, channelId, "catch-up channel"));
    }

    [Fact]
    public async Task CraftedUnselectedSeriesIdIsRejectedBeforeDetailCall()
    {
        StubClient client = new();
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [101],
        };
        Guid crafted = StreamService.ToGuid(StreamService.SeriesPrefix, 7, 999, 0);
        StreamService.FromGuid(crafted, out _, out int categoryId, out int seriesId, out _);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            StreamAuthorization.GetAuthorizedSeriesAsync(
                client,
                selections,
                Connection,
                categoryId,
                seriesId,
                CancellationToken.None));

        Assert.Equal(0, client.SeriesDetailCalls);
    }

    [Fact]
    public async Task SeriesResponseFromDifferentCategoryIsRejected()
    {
        StubClient client = new()
        {
            SeriesDetails = new SeriesStreamInfo
            {
                Info = new SeriesInfo { CategoryId = 8 },
            },
        };
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [101],
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            StreamAuthorization.GetAuthorizedSeriesAsync(
                client,
                selections,
                Connection,
                7,
                101,
                CancellationToken.None));

        Assert.Equal(1, client.SeriesDetailCalls);
    }

    [Fact]
    public async Task SeriesWildcardRejectsForeignIdBeforeDetailCall()
    {
        StubClient client = new()
        {
            SeriesCatalog = [new Series { CategoryId = 8, SeriesId = 999 }],
        };
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [],
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            StreamAuthorization.GetAuthorizedSeriesAsync(
                client,
                selections,
                Connection,
                7,
                999,
                CancellationToken.None));

        Assert.Equal(1, client.SeriesCatalogCalls);
        Assert.Equal(0, client.SeriesDetailCalls);
    }

    [Fact]
    public async Task SeriesWildcardAcceptsMissingCategoryFieldsFromCategoryEndpoint()
    {
        StubClient client = new()
        {
            SeriesCatalog = [new Series { CategoryId = 0, SeriesId = 101 }],
            SeriesDetails = new SeriesStreamInfo
            {
                Info = new SeriesInfo { CategoryId = 0 },
            },
        };
        Dictionary<int, HashSet<int>> selections = new()
        {
            [7] = [],
        };

        SeriesStreamInfo result = await StreamAuthorization.GetAuthorizedSeriesAsync(
            client,
            selections,
            Connection,
            7,
            101,
            CancellationToken.None);

        Assert.Same(client.SeriesDetails, result);
        Assert.Equal(1, client.SeriesCatalogCalls);
        Assert.Equal(1, client.SeriesDetailCalls);
    }

    [Fact]
    public void CraftedSeasonAndEpisodeIdsAreRejected()
    {
        SeriesStreamInfo series = new()
        {
            Info = new SeriesInfo { CategoryId = 7 },
            Episodes = new Dictionary<int, ICollection<Episode>>
            {
                [1] = [new Episode { EpisodeId = 501, Season = 1 }],
            },
        };

        Assert.Throws<UnauthorizedAccessException>(() => StreamAuthorization.GetAuthorizedSeason(series, 99));
        Assert.Throws<UnauthorizedAccessException>(() => StreamAuthorization.EnsureEpisodeSelected(series, 1, 999));
    }

    [Fact]
    public void LiveTvMediaSourcesRequireDirectProviderOpening()
    {
        MediaBrowser.Model.Dto.MediaSourceInfo source = new()
        {
            RequiresClosing = false,
            RequiresOpening = false,
        };

        MediaBrowser.Model.Dto.MediaSourceInfo result = LiveTvService.RequireOpening(source);

        Assert.Same(source, result);
        Assert.True(result.RequiresOpening);
        Assert.True(result.RequiresClosing);
    }

    private sealed class StubClient : IXtreamClient
    {
        private int _liveStreamCalls;
        private int _seriesCatalogCalls;
        private int _seriesDetailCalls;

        public int LiveStreamCalls => _liveStreamCalls;

        public int SeriesDetailCalls => _seriesDetailCalls;

        public int SeriesCatalogCalls => _seriesCatalogCalls;

        public List<StreamInfo> LiveStreams { get; init; } = [];

        public List<Series> SeriesCatalog { get; init; } = [];

        public SeriesStreamInfo SeriesDetails { get; init; } = new();

        public Task<EpgListings> GetEpgInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _liveStreamCalls);
            return Task.FromResult(LiveStreams);
        }

        public Task<List<StreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _seriesCatalogCalls);
            return Task.FromResult(SeriesCatalog);
        }

        public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _seriesDetailCalls);
            return Task.FromResult(SeriesDetails);
        }

        public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VodStreamInfo> GetVodInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
