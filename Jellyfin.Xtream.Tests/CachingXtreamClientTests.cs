using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;

namespace Jellyfin.Xtream.Tests;

public class CachingXtreamClientTests
{
    [Fact]
    public async Task ConcurrentSeriesRequestsAreCoalesced()
    {
        StubClient inner = new();
        using CachingXtreamClient client = new(inner);
        ConnectionInfo connection = new("https://provider.example", "user", "password");

        Task<SeriesStreamInfo> first = client.GetSeriesStreamsBySeriesAsync(connection, 42, CancellationToken.None);
        Task<SeriesStreamInfo> second = client.GetSeriesStreamsBySeriesAsync(connection, 42, CancellationToken.None);
        SeriesStreamInfo[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, inner.SeriesDetailCalls);
        Assert.Same(results[0], results[1]);
    }

    private sealed class StubClient : IXtreamClient
    {
        private int _seriesDetailCalls;

        public int SeriesDetailCalls => _seriesDetailCalls;

        public Task<EpgListings> GetEpgInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _seriesDetailCalls);
            await Task.Yield();
            return new SeriesStreamInfo();
        }

        public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VodStreamInfo> GetVodInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
