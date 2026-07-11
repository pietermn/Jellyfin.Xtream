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

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelSharedLoad()
    {
        TaskCompletionSource<SeriesStreamInfo> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CancellationToken> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubClient inner = new()
        {
            SeriesDetailHandler = async token =>
            {
                started.TrySetResult(token);
                return await completion.Task.WaitAsync(token);
            },
        };
        using CachingXtreamClient client = new(inner);
        using CancellationTokenSource firstWaiterTokenSource = new();
        ConnectionInfo connection = new("https://provider.example", "user", "password");

        Task<SeriesStreamInfo> first = client.GetSeriesStreamsBySeriesAsync(connection, 42, firstWaiterTokenSource.Token);
        CancellationToken providerToken = await started.Task;
        Task<SeriesStreamInfo> second = client.GetSeriesStreamsBySeriesAsync(connection, 42, CancellationToken.None);
        firstWaiterTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(providerToken.IsCancellationRequested);

        SeriesStreamInfo expected = new();
        completion.SetResult(expected);
        Assert.Same(expected, await second);
        Assert.Equal(1, inner.SeriesDetailCalls);
    }

    [Fact]
    public async Task CancelingAllWaitersCancelsProviderAndAllowsRetry()
    {
        TaskCompletionSource providerCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource providerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubClient inner = new();
        inner.SeriesDetailHandler = async token =>
        {
            if (inner.SeriesDetailCalls > 1)
            {
                return new SeriesStreamInfo();
            }

            providerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The provider wait completed without cancellation.");
            }
            catch (OperationCanceledException)
            {
                providerCanceled.TrySetResult();
                throw;
            }
        };
        using CachingXtreamClient client = new(inner);
        using CancellationTokenSource firstTokenSource = new();
        using CancellationTokenSource secondTokenSource = new();
        ConnectionInfo connection = new("https://provider.example", "user", "password");

        Task<SeriesStreamInfo> first = client.GetSeriesStreamsBySeriesAsync(connection, 42, firstTokenSource.Token);
        await providerStarted.Task;
        Task<SeriesStreamInfo> second = client.GetSeriesStreamsBySeriesAsync(connection, 42, secondTokenSource.Token);
        firstTokenSource.Cancel();
        secondTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await providerCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotNull(await client.GetSeriesStreamsBySeriesAsync(connection, 42, CancellationToken.None));
        Assert.Equal(2, inner.SeriesDetailCalls);
    }

    [Fact]
    public async Task ProviderLoadTimeoutCancelsStalledCoalescedRequest()
    {
        TaskCompletionSource<SeriesStreamInfo> providerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CancellationToken> providerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubClient inner = new()
        {
            SeriesDetailHandler = token =>
            {
                providerStarted.TrySetResult(token);
                return providerCompletion.Task;
            },
        };
        using CachingXtreamClient client = new(inner, TimeSpan.FromMilliseconds(50));
        ConnectionInfo connection = new("https://provider.example", "user", "password");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetSeriesStreamsBySeriesAsync(connection, 42, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1)));

        CancellationToken providerToken = await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(providerToken.IsCancellationRequested);
        providerCompletion.SetResult(new SeriesStreamInfo());
        Assert.Equal(1, inner.SeriesDetailCalls);
    }

    [Fact]
    public void NestedEpgAndEpisodeGraphsContributeToCacheSize()
    {
        EpgListings epg = new()
        {
            Listings = Enumerable.Range(0, 250).Select(_ => new EpgInfo()).ToList(),
        };
        SeriesStreamInfo series = new()
        {
            Seasons = Enumerable.Range(0, 10).Select(_ => new Season()).ToList(),
            Episodes = new Dictionary<int, ICollection<Episode>>
            {
                [1] = Enumerable.Range(0, 150).Select(_ => new Episode()).ToList(),
                [2] = Enumerable.Range(0, 55).Select(_ => new Episode()).ToList(),
            },
        };

        Assert.Equal(3, CachingXtreamClient.CalculateCacheUnits(epg));
        Assert.Equal(3, CachingXtreamClient.CalculateCacheUnits(series));
        Assert.Equal(1, CachingXtreamClient.CalculateCacheUnits(new List<Category>()));
    }

    private sealed class StubClient : IXtreamClient
    {
        private int _seriesDetailCalls;

        public int SeriesDetailCalls => _seriesDetailCalls;

        public Func<CancellationToken, Task<SeriesStreamInfo>>? SeriesDetailHandler { get; set; }

        public Task<EpgListings> GetEpgInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _seriesDetailCalls);
            if (SeriesDetailHandler is not null)
            {
                return await SeriesDetailHandler(cancellationToken);
            }

            await Task.Yield();
            return new SeriesStreamInfo();
        }

        public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VodStreamInfo> GetVodInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
