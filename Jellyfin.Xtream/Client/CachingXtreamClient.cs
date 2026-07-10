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
    private static readonly TimeSpan _catalogLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _detailLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _epgLifetime = TimeSpan.FromMinutes(5);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxCacheUnits });
    private readonly IXtreamClient _inner;
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingXtreamClient"/> class.
    /// </summary>
    /// <param name="inner">The provider client to decorate.</param>
    public CachingXtreamClient(IXtreamClient inner)
    {
        _inner = inner;
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
        _cache.Dispose();
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
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        Lazy<Task<object>> pending = _pending.GetOrAdd(
            key,
            _ => new Lazy<Task<object>>(
                () => LoadAndCacheAsync(key, lifetime, factory),
                LazyThreadSafetyMode.ExecutionAndPublication));

        object value = await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (T)value;
    }

    private async Task<object> LoadAndCacheAsync<T>(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> factory)
        where T : class
    {
        try
        {
            T value = await factory(CancellationToken.None).ConfigureAwait(false);
            MemoryCacheEntryOptions options = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(lifetime)
                .SetSize(GetCacheUnits(value));
            _cache.Set(key, value, options);
            return value;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private static long GetCacheUnits(object value)
    {
        return value is ICollection collection ? Math.Clamp((collection.Count / 100) + 1, 1, 256) : 1;
    }
}
