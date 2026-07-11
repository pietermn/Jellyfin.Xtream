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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Sends credential-bearing provider requests without the default HTTP-client-factory logging handlers.
/// </summary>
/// <remarks>
/// This client deliberately owns its handler instead of using <see cref="IHttpClientFactory"/>. The factory's
/// default logging handlers can include complete request URIs, while Xtream puts credentials in paths and queries.
/// Redirects are disabled at the socket handler and followed only after an explicit safety check.
/// </remarks>
public sealed class ProviderHttpClient : IDisposable
{
    private const int MaxRedirects = 5;
    private static readonly HttpRequestOptionsKey<IPAddress[]> _approvedAddressesKey = new("Jellyfin.Xtream.ApprovedProviderAddresses");
    private static readonly TimeSpan _endpointLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, ApprovedEndpoint> _approvedEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _endpointResolutionLock = new(1, 1);
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveAddresses;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderHttpClient"/> class.
    /// </summary>
    public ProviderHttpClient()
        : this(CreatePrimaryHandler(), ResolveAddressesAsync)
    {
    }

    internal ProviderHttpClient(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _client = new HttpClient(handler, disposeHandler: true);
        _resolveAddresses = resolveAddresses ?? ResolveAddressesAsync;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _endpointResolutionLock.Dispose();
    }

    internal async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Uri providerBaseUri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providerBaseUri);

        if (request.RequestUri is not Uri initialUri)
        {
            throw new ArgumentException("The provider request must have an absolute URI.", nameof(request));
        }

        if (request.Content is not null)
        {
            throw new ArgumentException("Provider redirects only support requests without a body.", nameof(request));
        }

        ProviderRedirectPolicy.EnsureSameOrigin(providerBaseUri, initialUri);
        IPAddress[] approvedAddresses = await GetApprovedAddressesAsync(providerBaseUri, cancellationToken).ConfigureAwait(false);
        request.Options.Set(_approvedAddressesKey, approvedAddresses);

        HttpRequestMessage currentRequest = request;
        HttpRequestMessage? redirectedRequest = null;
        try
        {
            for (int redirectCount = 0; ; redirectCount++)
            {
                HttpResponseMessage response = await _client.SendAsync(currentRequest, completionOption, cancellationToken).ConfigureAwait(false);
                if (!ProviderRedirectPolicy.IsRedirect(response.StatusCode))
                {
                    return response;
                }

                try
                {
                    if (response.Headers.Location is not Uri location)
                    {
                        throw new HttpRequestException("The provider returned a redirect without a location.");
                    }

                    if (redirectCount >= MaxRedirects)
                    {
                        throw new HttpRequestException(
                            "The provider exceeded the redirect limit.",
                            null,
                            response.StatusCode);
                    }

                    Uri redirectUri = location.IsAbsoluteUri ? location : new Uri(currentRequest.RequestUri!, location);
                    ProviderRedirectPolicy.EnsureSameOrigin(providerBaseUri, redirectUri);

                    redirectedRequest?.Dispose();
                    redirectedRequest = CloneForRedirect(request, redirectUri);
                    redirectedRequest.Options.Set(_approvedAddressesKey, approvedAddresses);
                    currentRequest = redirectedRequest;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }

                response.Dispose();
            }
        }
        finally
        {
            redirectedRequest?.Dispose();
        }
    }

    internal static SocketsHttpHandler CreatePrimaryHandler()
    {
        return new SocketsHttpHandler
        {
            ActivityHeadersPropagator = null,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectApprovedEndpointAsync,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            UseCookies = false,
            UseProxy = false,
        };
    }

    internal static bool TryGetApprovedAddresses(HttpRequestMessage request, out IPAddress[]? addresses)
        => request.Options.TryGetValue(_approvedAddressesKey, out addresses);

    private static HttpRequestMessage CloneForRedirect(HttpRequestMessage original, Uri redirectUri)
    {
        HttpRequestMessage clone = new(original.Method, redirectUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private async Task<IPAddress[]> GetApprovedAddressesAsync(Uri providerBaseUri, CancellationToken cancellationToken)
    {
        string origin = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{providerBaseUri.Scheme}://{providerBaseUri.IdnHost}:{providerBaseUri.Port}");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_approvedEndpoints.TryGetValue(origin, out ApprovedEndpoint? endpoint)
            && endpoint.ExpiresAt > now)
        {
            return endpoint.Addresses;
        }

        await _endpointResolutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_approvedEndpoints.TryGetValue(origin, out endpoint)
                && endpoint.ExpiresAt > now)
            {
                return endpoint.Addresses;
            }

            string host = providerBaseUri.IdnHost.TrimEnd('.');
            ProviderRedirectPolicy.EnsureAllowedHostName(host);
            IPAddress[] resolved = IPAddress.TryParse(host, out IPAddress? literalAddress)
                ? [literalAddress]
                : await _resolveAddresses(host, cancellationToken).ConfigureAwait(false);
            IPAddress[] approved = ProviderRedirectPolicy.SelectApprovedAddresses(host, resolved);
            _approvedEndpoints[origin] = new ApprovedEndpoint(approved, now.Add(_endpointLifetime));
            return approved;
        }
        finally
        {
            _endpointResolutionLock.Release();
        }
    }

    private static async ValueTask<Stream> ConnectApprovedEndpointAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        Uri? requestUri = context.InitialRequestMessage.RequestUri;
        if (requestUri is null
            || context.DnsEndPoint.Port != requestUri.Port
            || !string.Equals(
                context.DnsEndPoint.Host.TrimEnd('.'),
                requestUri.IdnHost.TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase)
            || !TryGetApprovedAddresses(context.InitialRequestMessage, out IPAddress[]? approvedAddresses)
            || approvedAddresses is null
            || approvedAddresses.Length == 0)
        {
            throw new HttpRequestException("The provider connection did not have an approved endpoint.");
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(approvedAddresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress[]> ResolveAddressesAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException("The provider host could not be resolved.", ex);
        }
    }

    private sealed record ApprovedEndpoint(IPAddress[] Addresses, DateTimeOffset ExpiresAt);
}
