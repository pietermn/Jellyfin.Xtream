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
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Client;

/// <summary>
/// The Xtream API client implementation.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamClient"/> class.
/// </remarks>
/// <param name="client">The credential-safe provider HTTP client.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class XtreamClient(ProviderHttpClient client, ILogger<XtreamClient> logger) : IDisposable, IXtreamClient
{
    private const int MaxQueryAttempts = 3;
    private const long MaxApiResponseBytes = 256L * 1024 * 1024;
    private static readonly string _defaultUserAgent = $"Jellyfin.Xtream/{Assembly.GetExecutingAssembly().GetName().Version}";
    private readonly JsonSerializerSettings _serializerSettings = new()
    {
        Error = NullableEventHandler(logger),
    };

    private string _userAgent = string.Empty;

    public void UpdateUserAgent()
    {
        Volatile.Write(ref _userAgent, Plugin.Instance.Configuration.UserAgent?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Ignores error events if the target property is nullable.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <returns>An event handler using the given logger.</returns>
    public static EventHandler<Newtonsoft.Json.Serialization.ErrorEventArgs> NullableEventHandler(ILogger<XtreamClient> logger)
    {
        return (object? sender, Newtonsoft.Json.Serialization.ErrorEventArgs args) =>
        {
            if (args.ErrorContext.OriginalObject?.GetType() is Type type && args.ErrorContext.Member is string jsonName)
            {
                PropertyInfo? property = type.GetProperties().FirstOrDefault((p) =>
                {
                    CustomAttributeData? attribute = p.CustomAttributes.FirstOrDefault(a => a.AttributeType == typeof(JsonPropertyAttribute));
                    if (attribute == null)
                    {
                        return false;
                    }

                    if (attribute.ConstructorArguments.Count > 0)
                    {
                        // Attribute contains a `propertyName`.
                        string? value = attribute.ConstructorArguments.First().Value as string;
                        return jsonName.Equals(value, StringComparison.Ordinal);
                    }
                    else
                    {
                        // Attribute does not contain a `propertyName`, compare with the name of the property itself.
                        return jsonName.Equals(p.Name, StringComparison.Ordinal);
                    }
                });

                if (property != null && Nullable.GetUnderlyingType(property.PropertyType) != null)
                {
                    logger.LogDebug("Ignoring invalid nullable Xtream property {Property} ({JsonName}).", property.Name, jsonName);
                    args.ErrorContext.Handled = true;
                }
            }
        };
    }

    private async Task<T> QueryApi<T>(
        ConnectionInfo connectionInfo,
        string operation,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildApiUri(connectionInfo, parameters);
        using HttpResponseMessage response = await SendWithRetryAsync(connectionInfo, uri, operation, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxApiResponseBytes)
        {
            throw new HttpRequestException($"Xtream {operation} response exceeds the configured size limit.", null, HttpStatusCode.RequestEntityTooLarge);
        }

        Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (responseStream.ConfigureAwait(false))
        {
            using SizeLimitedReadStream limitedStream = new(responseStream, MaxApiResponseBytes, cancellationToken);
            using StreamReader streamReader = new(limitedStream, Encoding.UTF8, true, 16 * 1024, leaveOpen: true);
            using JsonTextReader jsonReader = new(streamReader)
            {
                CloseInput = false,
            };
            try
            {
                JsonSerializer serializer = JsonSerializer.Create(_serializerSettings);
                T? result = await Task.Run(
                    () => serializer.Deserialize<T>(jsonReader),
                    cancellationToken).ConfigureAwait(false);
                return result ?? throw new JsonSerializationException("The JSON document did not contain the expected value.");
            }
            catch (JsonException ex)
            {
                throw new HttpRequestException($"Xtream {operation} returned an invalid response.", ex, HttpStatusCode.BadGateway);
            }
            catch (InvalidDataException ex)
            {
                throw new HttpRequestException($"Xtream {operation} response exceeds the configured size limit.", ex, HttpStatusCode.RequestEntityTooLarge);
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        ConnectionInfo connectionInfo,
        Uri uri,
        string operation,
        CancellationToken cancellationToken)
    {
        Uri providerBaseUri = GetProviderBaseUri(connectionInfo);
        for (int attempt = 1; attempt <= MaxQueryAttempts; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            AddUserAgent(request);
            try
            {
                HttpResponseMessage response = await client.SendAsync(
                    request,
                    providerBaseUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || attempt == MaxQueryAttempts || !IsTransient(response.StatusCode))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    HttpStatusCode statusCode = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Xtream {operation} returned HTTP {(int)statusCode}.", null, statusCode);
                }

                TimeSpan delay = GetRetryDelay(response, attempt);
                logger.LogWarning(
                    "Xtream {Operation} returned HTTP {StatusCode} on attempt {Attempt} of {MaxAttempts}; retrying after {DelayMs} ms.",
                    operation,
                    (int)response.StatusCode,
                    attempt,
                    MaxQueryAttempts,
                    delay.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < MaxQueryAttempts && IsTransient(ex.StatusCode))
                {
                    TimeSpan delay = GetRetryDelay(null, attempt);
                    logger.LogWarning(
                        "Xtream {Operation} request failed on attempt {Attempt} of {MaxAttempts}; retrying after {DelayMs} ms.",
                        operation,
                        attempt,
                        MaxQueryAttempts,
                        delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new HttpRequestException($"Xtream {operation} request failed.", null, ex.StatusCode);
            }
        }

        throw new InvalidOperationException("The Xtream retry loop completed without a response.");
    }

    private static Uri BuildApiUri(ConnectionInfo connectionInfo, IReadOnlyDictionary<string, string?> parameters)
    {
        Uri baseUri = GetProviderBaseUri(connectionInfo);
        Uri endpoint = new(baseUri, baseUri.AbsolutePath.TrimEnd('/') + "/player_api.php");

        Dictionary<string, string?> query = parameters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        query["username"] = connectionInfo.UserName;
        query["password"] = connectionInfo.Password;
        return new Uri(QueryHelpers.AddQueryString(endpoint.ToString(), query));
    }

    private static Uri GetProviderBaseUri(ConnectionInfo connectionInfo)
    {
        if (!Uri.TryCreate(connectionInfo.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException("The Xtream base URL must be an absolute HTTP or HTTPS URL without user information.", nameof(connectionInfo));
        }

        return baseUri;
    }

    private void AddUserAgent(HttpRequestMessage request)
    {
        string configured = Volatile.Read(ref _userAgent);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(configured) ? _defaultUserAgent : configured);
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        TimeSpan? retryAfter = response?.Headers.RetryAfter?.Delta;
        if (!retryAfter.HasValue && response?.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
        }

        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            return retryAfter.Value > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : retryAfter.Value;
        }

        double jitterMilliseconds = Random.Shared.Next(100, 501);
        return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private static bool IsTransient(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return true;
        }

        return statusCode.Value is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode.Value >= 500;
    }

    public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        QueryApi<PlayerApi>(
            connectionInfo,
            "provider status",
            new Dictionary<string, string?>(),
            cancellationToken);

    public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<Series>>(
            connectionInfo,
            "series catalog",
            new Dictionary<string, string?> { ["action"] = "get_series", ["category_id"] = categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

    public Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken) =>
         QueryApi<SeriesStreamInfo>(
            connectionInfo,
            "series details",
            new Dictionary<string, string?> { ["action"] = "get_series_info", ["series_id"] = seriesId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

    public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<StreamInfo>>(
            connectionInfo,
            "VOD catalog",
            new Dictionary<string, string?> { ["action"] = "get_vod_streams", ["category_id"] = categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

    public Task<VodStreamInfo> GetVodInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) =>
         QueryApi<VodStreamInfo>(
            connectionInfo,
            "VOD details",
            new Dictionary<string, string?> { ["action"] = "get_vod_info", ["vod_id"] = streamId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

    public Task<List<StreamInfo>> GetLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<StreamInfo>>(
            connectionInfo,
            "live stream catalog",
            new Dictionary<string, string?> { ["action"] = "get_live_streams" },
            cancellationToken);

    public Task<List<StreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<StreamInfo>>(
            connectionInfo,
            "live category",
            new Dictionary<string, string?> { ["action"] = "get_live_streams", ["category_id"] = categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

    public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
            connectionInfo,
            "series categories",
            new Dictionary<string, string?> { ["action"] = "get_series_categories" },
            cancellationToken);

    public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
            connectionInfo,
            "VOD categories",
            new Dictionary<string, string?> { ["action"] = "get_vod_categories" },
            cancellationToken);

    public Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
            connectionInfo,
            "live categories",
            new Dictionary<string, string?> { ["action"] = "get_live_categories" },
            cancellationToken);

    public Task<EpgListings> GetEpgInfoAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken) =>
         QueryApi<EpgListings>(
            connectionInfo,
            "EPG",
            new Dictionary<string, string?> { ["action"] = "get_simple_data_table", ["stream_id"] = streamId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

    /// <summary>
    /// Dispose the HTTP client.
    /// </summary>
    /// <param name="b">Unused.</param>
    protected virtual void Dispose(bool b)
    {
        _ = b;
        // The provider client is a shared singleton whose lifetime is managed by dependency injection.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
