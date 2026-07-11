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
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Adds bounded, request-coalescing caches around immutable Xtream catalog responses.
/// </summary>
public sealed class CachingXtreamClient : IXtreamClient, IDisposable
{
    private const long MaxCacheUnits = 4096;
    private const long MaxCacheEntryUnits = 256;
    private const long ItemsPerCacheUnit = 100;
    private static readonly TimeSpan _catalogLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _detailLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _epgLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _providerLoadTimeout = TimeSpan.FromMinutes(2);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxCacheUnits });
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly IXtreamClient _inner;
    private readonly TimeSpan _loadTimeout;
    private readonly ConcurrentDictionary<string, PendingLoad> _pending = new(StringComparer.Ordinal);
    private int _disposeState;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingXtreamClient"/> class.
    /// </summary>
    /// <param name="inner">The provider client to decorate.</param>
    public CachingXtreamClient(IXtreamClient inner)
        : this(inner, _providerLoadTimeout)
    {
    }

    internal CachingXtreamClient(IXtreamClient inner, TimeSpan loadTimeout)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(loadTimeout, TimeSpan.Zero);

        _inner = inner;
        _loadTimeout = loadTimeout;
    }

    /// <inheritdoc />
    public Task<EpgListings> GetEpgInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "epg", streamId),
            _epgLifetime,
            token => _inner.GetEpgInfoAsync(connectionInfo, streamId, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "live-categories"),
            _catalogLifetime,
            token => _inner.GetLiveCategoryAsync(connectionInfo, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<List<StreamInfo>> GetLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamsAsync(connectionInfo, cancellationToken);

    /// <inheritdoc />
    public Task<List<StreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamsByCategoryAsync(connectionInfo, categoryId, cancellationToken);

    /// <inheritdoc />
    public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "series-category", categoryId),
            _catalogLifetime,
            token => _inner.GetSeriesByCategoryAsync(connectionInfo, categoryId, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "series-categories"),
            _catalogLifetime,
            token => _inner.GetSeriesCategoryAsync(connectionInfo, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "series", seriesId),
            _detailLifetime,
            token => _inner.GetSeriesStreamsBySeriesAsync(connectionInfo, seriesId, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        _inner.GetUserAndServerInfoAsync(connectionInfo, cancellationToken);

    /// <inheritdoc />
    public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "vod-categories"),
            _catalogLifetime,
            token => _inner.GetVodCategoryAsync(connectionInfo, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<VodStreamInfo> GetVodInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "vod", streamId),
            _detailLifetime,
            token => _inner.GetVodInfoAsync(connectionInfo, streamId, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
        GetOrCreateAsync(
            CreateKey(connectionInfo, "vod-category", categoryId),
            _catalogLifetime,
            token => _inner.GetVodStreamsByCategoryAsync(connectionInfo, categoryId, token),
            cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _disposeTokenSource.Cancel();
        foreach (PendingLoad pending in _pending.Values)
        {
            pending.Cancel();
        }

        _cache.Dispose();
        _disposeTokenSource.Dispose();
    }

    internal static long CalculateCacheUnits(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        long itemCount = value switch
        {
            EpgListings epg => epg.Listings?.Count ?? 0,
            SeriesStreamInfo series => CountSeriesGraph(series),
            ICollection collection => collection.Count,
            _ => 1,
        };

        long units = (Math.Min(itemCount, MaxCacheEntryUnits * ItemsPerCacheUnit) + ItemsPerCacheUnit - 1) / ItemsPerCacheUnit;
        return Math.Clamp(units, 1, MaxCacheEntryUnits);
    }

    private static string CreateKey(ConnectionInfo connectionInfo, string operation, int? id = null)
    {
        byte[] identity = Encoding.UTF8.GetBytes($"{connectionInfo.BaseUrl.TrimEnd('/')}\0{connectionInfo.UserName}\0{connectionInfo.Password}");
        string fingerprint = Convert.ToHexString(SHA256.HashData(identity));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{fingerprint}:{operation}:{id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}");
    }

    private async Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            if (_cache.TryGetValue(key, out T? cached) && cached is not null)
            {
                return cached;
            }

            if (!_pending.TryGetValue(key, out PendingLoad? pending))
            {
                PendingLoad candidate = new(
                    this,
                    key,
                    token => LoadAndCacheAsync(key, lifetime, factory, token),
                    _loadTimeout,
                    _disposeTokenSource.Token);
                if (!_pending.TryAdd(key, candidate))
                {
                    candidate.DisposeUnused();
                    continue;
                }

                pending = candidate;
            }

            if (!pending.TryAcquire(out Task<object> loadTask, out CancellationToken loadCancellationToken))
            {
                RemovePending(key, pending);
                continue;
            }

            try
            {
                object value = await loadTask
                    .WaitAsync(loadCancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return (T)value;
            }
            finally
            {
                pending.Release();
            }
        }
    }

    private async Task<object> LoadAndCacheAsync<T>(
        string key,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
        where T : class
    {
        T value = await factory(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        MemoryCacheEntryOptions options = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(lifetime)
            .SetSize(CalculateCacheUnits(value));
        _cache.Set(key, value, options);
        return value;
    }

    private static long CountSeriesGraph(SeriesStreamInfo series)
    {
        long maximumItems = MaxCacheEntryUnits * ItemsPerCacheUnit;
        long count = Math.Min(series.Seasons?.Count ?? 0, maximumItems);
        if (series.Episodes is null)
        {
            return count;
        }

        count = Math.Min(count + series.Episodes.Count, maximumItems);
        foreach (ICollection<Episode>? episodes in series.Episodes.Values)
        {
            count = Math.Min(count + (episodes?.Count ?? 0), maximumItems);
            if (count >= maximumItems)
            {
                break;
            }
        }

        return count;
    }

    private void RemovePending(string key, PendingLoad pending)
    {
        ((ICollection<KeyValuePair<string, PendingLoad>>)_pending).Remove(new KeyValuePair<string, PendingLoad>(key, pending));
    }

    private sealed class PendingLoad
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly string _key;
        private readonly Lazy<Task<object>> _loadTask;
        private readonly CachingXtreamClient _owner;
        private readonly object _syncRoot = new();
        private bool _acceptingWaiters = true;
        private bool _completed;
        private bool _disposed;
        private int _waiterCount;

        public PendingLoad(
            CachingXtreamClient owner,
            string key,
            Func<CancellationToken, Task<object>> factory,
            TimeSpan timeout,
            CancellationToken disposeToken)
        {
            _owner = owner;
            _key = key;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(disposeToken);
            _cancellationTokenSource.CancelAfter(timeout);
            _loadTask = new Lazy<Task<object>>(
                () => ExecuteAsync(factory),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public bool TryAcquire(out Task<object> task, out CancellationToken loadCancellationToken)
        {
            lock (_syncRoot)
            {
                if (!_acceptingWaiters || _cancellationTokenSource.IsCancellationRequested)
                {
                    _acceptingWaiters = false;
                    task = null!;
                    loadCancellationToken = default;
                    return false;
                }

                _waiterCount++;
                task = _loadTask.Value;
                loadCancellationToken = _cancellationTokenSource.Token;
                return true;
            }
        }

        public void Release()
        {
            bool cancel = false;
            bool dispose = false;
            lock (_syncRoot)
            {
                _waiterCount--;
                if (_waiterCount == 0)
                {
                    if (_completed)
                    {
                        dispose = true;
                    }
                    else
                    {
                        _acceptingWaiters = false;
                        cancel = true;
                    }
                }
            }

            if (cancel)
            {
                _owner.RemovePending(_key, this);
                CancelTokenSource();
            }

            if (dispose)
            {
                DisposeTokenSource();
            }
        }

        public void Cancel()
        {
            lock (_syncRoot)
            {
                _acceptingWaiters = false;
            }

            _owner.RemovePending(_key, this);
            CancelTokenSource();
        }

        public void DisposeUnused()
        {
            lock (_syncRoot)
            {
                _acceptingWaiters = false;
                _completed = true;
            }

            DisposeTokenSource();
        }

        private async Task<object> ExecuteAsync(Func<CancellationToken, Task<object>> factory)
        {
            try
            {
                return await factory(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            finally
            {
                bool dispose;
                lock (_syncRoot)
                {
                    _completed = true;
                    _acceptingWaiters = false;
                    dispose = _waiterCount == 0;
                }

                _owner.RemovePending(_key, this);
                if (dispose)
                {
                    DisposeTokenSource();
                }
            }
        }

        private void DisposeTokenSource()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _cancellationTokenSource.Dispose();
        }

        private void CancelTokenSource()
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A concurrently completing load already disposed its cancellation source.
            }
        }
    }
}
