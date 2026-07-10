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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream implementation that can be restreamed.
/// </summary>
public class Restream : ILiveStream, IDirectStreamProvider, IDisposable
{
    internal const int StreamBufferSize = 8 * 1024 * 1024;

    private const int MaxRedirects = 5;

    /// <summary>
    /// The global constant for the restream tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Restream";

    private readonly WrappedBufferStream _buffer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CancellationTokenSource _lifetimeTokenSource = new();
    private readonly ILogger _logger;
    private readonly Uri _sourceUri;
    private readonly object _syncRoot = new();
    private readonly string? _userAgent;

    private Task? _closeTask;
    private Task? _copyTask;
    private bool _closed;
    private bool _disposeStarted;
    private bool _enableStreamSharing = true;
    private Stream? _inputStream;
    private Task? _openTask;
    private HttpResponseMessage? _response;

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    public Restream(IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger logger, MediaSourceInfo mediaSource)
        : this(
            httpClientFactory,
            logger,
            mediaSource,
            path => appHost.GetSmartApiUrl(IPAddress.Any) + path,
            path => appHost.GetApiUrlForLocalAccess() + path,
            StreamBufferSize,
            Plugin.Instance.Configuration.UserAgent)
    {
    }

    internal Restream(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        MediaSourceInfo mediaSource,
        Func<string, string> getPublicStreamUrl,
        Func<string, string> getLocalStreamUrl,
        int bufferSize,
        string? userAgent)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentNullException.ThrowIfNull(getPublicStreamUrl);
        ArgumentNullException.ThrowIfNull(getLocalStreamUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaSource.Path);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _sourceUri = new Uri(mediaSource.Path, UriKind.Absolute);
        _userAgent = userAgent;
        _buffer = new WrappedBufferStream(bufferSize);

        MediaSource = mediaSource;
        OriginalStreamId = mediaSource.Id;
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        string path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        MediaSource.Path = getPublicStreamUrl(path);
        MediaSource.EncoderPath = getLocalStreamUrl(path);
        MediaSource.Protocol = MediaProtocol.Http;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId => TunerHost;

    /// <inheritdoc />
    public bool EnableStreamSharing
    {
        get
        {
            lock (_syncRoot)
            {
                return _enableStreamSharing;
            }
        }
    }

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken)
    {
        Task openTask;
        lock (_syncRoot)
        {
            ThrowIfDisposedLocked();
            if (_closed)
            {
                throw new InvalidOperationException("A closed restream cannot be opened again.");
            }

            _openTask ??= OpenCoreAsync(openCancellationToken);
            openTask = _openTask;
        }

        return openTask.WaitAsync(openCancellationToken);
    }

    /// <inheritdoc />
    public Task Close()
    {
        lock (_syncRoot)
        {
            if (_closeTask is not null)
            {
                return _closeTask;
            }

            _closed = true;
            _enableStreamSharing = false;
            _closeTask = CloseCoreAsync();
            return _closeTask;
        }
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposedLocked();
            if (_closed)
            {
                throw new InvalidOperationException("The restream has already been closed.");
            }

            if (_openTask is null || !_openTask.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The restream has not been opened successfully.");
            }

            _logger.LogInformation("Opening restream {Count} for channel {ChannelId}.", ConsumerCount, MediaSource.Id);
            return new WrappedBufferReadStream(_buffer);
        }
    }

    /// <summary>
    /// Disposes the fields.
    /// </summary>
    /// <param name="disposing">Whether or not to dispose.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_disposeStarted)
            {
                return;
            }

            _disposeStarted = true;
        }

        try
        {
            Close().GetAwaiter().GetResult();
        }
        finally
        {
            lock (_syncRoot)
            {
                DisposeTransportLocked();
                _buffer.Dispose();
                _lifetimeTokenSource.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private async Task OpenCoreAsync(CancellationToken openCancellationToken)
    {
        string channelId = MediaSource.Id;
        _logger.LogInformation("Starting restream for channel {ChannelId}.", channelId);

        using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            openCancellationToken,
            _lifetimeTokenSource.Token);

        HttpResponseMessage? response = null;
        Stream? inputStream = null;
        try
        {
            response = await SendStreamRequestWithRedirectsAsync(_sourceUri, linkedTokenSource.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            inputStream = await response.Content.ReadAsStreamAsync(linkedTokenSource.Token).ConfigureAwait(false);

            lock (_syncRoot)
            {
                if (_closed || _disposeStarted)
                {
                    throw new OperationCanceledException(_lifetimeTokenSource.Token);
                }

                _response = response;
                _inputStream = inputStream;
                _copyTask = CopyInputToBufferAsync(response, inputStream, _lifetimeTokenSource.Token);
                response = null;
                inputStream = null;
            }
        }
        catch (Exception ex)
        {
            if (inputStream is not null)
            {
                await inputStream.DisposeAsync().ConfigureAwait(false);
            }

            response?.Dispose();

            lock (_syncRoot)
            {
                _enableStreamSharing = false;
            }

            if (_lifetimeTokenSource.IsCancellationRequested)
            {
                _buffer.Complete();
            }
            else
            {
                _buffer.Complete(ex);
            }

            throw;
        }
    }

    private async Task CopyInputToBufferAsync(HttpResponseMessage response, Stream inputStream, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await inputStream.CopyToAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Restream for channel {ChannelId} reached the end of the upstream stream.", MediaSource.Id);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Restream for channel {ChannelId} was canceled.", MediaSource.Id);
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogWarning(ex, "Restream for channel {ChannelId} failed while copying the upstream stream.", MediaSource.Id);
        }
        finally
        {
            bool disposeInputStream;
            bool disposeResponse;
            lock (_syncRoot)
            {
                disposeInputStream = ReferenceEquals(_inputStream, inputStream);
                if (disposeInputStream)
                {
                    _inputStream = null;
                }

                disposeResponse = ReferenceEquals(_response, response);
                if (disposeResponse)
                {
                    _response = null;
                }

                _enableStreamSharing = false;
            }

            if (disposeInputStream)
            {
                await inputStream.DisposeAsync().ConfigureAwait(false);
            }

            if (disposeResponse)
            {
                response.Dispose();
            }

            _buffer.Complete(failure);
        }
    }

    private async Task CloseCoreAsync()
    {
        Task cancellation = _lifetimeTokenSource.CancelAsync();
        _buffer.Complete();
        await cancellation.ConfigureAwait(false);
        await DisposeTransportAsync().ConfigureAwait(false);

        Task? openTask;
        lock (_syncRoot)
        {
            openTask = _openTask;
        }

        if (openTask is not null)
        {
            try
            {
                await openTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Close observes failed or canceled opens and still completes normally.
            }
        }

        Task? copyTask;
        lock (_syncRoot)
        {
            copyTask = _copyTask;
        }

        if (copyTask is not null)
        {
            await copyTask.ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendStreamRequestWithRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int redirectCount = 0; ; redirectCount++)
        {
            HttpResponseMessage response = await SendStreamRequestAsync(currentUri, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            Uri? location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new HttpRequestException("The upstream stream returned a redirect without a location.");
            }

            if (redirectCount >= MaxRedirects)
            {
                HttpStatusCode statusCode = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException("The upstream stream exceeded the redirect limit.", null, statusCode);
            }

            Uri redirectBase = response.RequestMessage?.RequestUri ?? currentUri;
            currentUri = location.IsAbsoluteUri ? location : new Uri(redirectBase, location);
            response.Dispose();
            _logger.LogDebug("Following upstream redirect for channel {ChannelId}.", MediaSource.Id);
        }
    }

    private async Task<HttpResponseMessage> SendStreamRequestAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(_userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        }

        return await _httpClientFactory.CreateClient(NamedClient.Default)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private async ValueTask DisposeTransportAsync()
    {
        Stream? inputStream;
        HttpResponseMessage? response;
        lock (_syncRoot)
        {
            inputStream = _inputStream;
            _inputStream = null;
            response = _response;
            _response = null;
        }

        if (inputStream is not null)
        {
            await inputStream.DisposeAsync().ConfigureAwait(false);
        }

        response?.Dispose();
    }

    private void DisposeTransportLocked()
    {
        _inputStream?.Dispose();
        _inputStream = null;
        _response?.Dispose();
        _response = null;
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposeStarted, this);
    }
}
