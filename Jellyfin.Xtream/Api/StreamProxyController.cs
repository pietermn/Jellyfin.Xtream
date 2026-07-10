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
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using XtreamConnectionInfo = Jellyfin.Xtream.Client.ConnectionInfo;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Proxies signed media requests without exposing Xtream account credentials.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Plugins/JellyfinXtream/v1/Stream")]
public sealed class StreamProxyController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    /// <summary>
    /// Proxies a signed provider stream.
    /// </summary>
    /// <param name="type">The requested stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="startTicks">The optional catch-up start in ticks.</param>
    /// <param name="duration">The optional catch-up duration.</param>
    /// <param name="signature">The request signature.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task representing the proxy operation.</returns>
    [AcceptVerbs("GET", "HEAD")]
    public async Task Proxy(
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int duration,
        string signature,
        CancellationToken cancellationToken)
    {
        XtreamConnectionInfo connection = Plugin.Instance.Creds;
        if (id <= 0
            || !Enum.IsDefined(type)
            || string.IsNullOrWhiteSpace(signature)
            || !StreamProxySigner.Verify(connection, type, id, extension, startTicks, duration, signature))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        DateTime? start = startTicks.HasValue ? new DateTime(startTicks.Value, DateTimeKind.Utc) : null;
        Uri upstreamUri;
        try
        {
            upstreamUri = StreamUriBuilder.Build(connection, type, id, extension, start, duration);
        }
        catch (ArgumentException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using HttpRequestMessage upstreamRequest = new(
            HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
            upstreamUri);
        if (Request.Headers.TryGetValue("Range", out var range))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Range", range.ToArray());
        }

        string userAgent = Plugin.Instance.Configuration.UserAgent;
        upstreamRequest.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? $"Jellyfin.Xtream/{typeof(Plugin).Assembly.GetName().Version}" : userAgent);

        using HttpResponseMessage upstreamResponse = await httpClientFactory.CreateClient(NamedClient.Default)
            .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        Response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyResponseHeaders(upstreamResponse, Response);

        if (!HttpMethods.IsHead(Request.Method) && upstreamResponse.Content is not null)
        {
            await upstreamResponse.Content.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstream, HttpResponse downstream)
    {
        if (upstream.Content.Headers.ContentType is not null)
        {
            downstream.ContentType = upstream.Content.Headers.ContentType.ToString();
        }

        if (upstream.Content.Headers.ContentLength.HasValue)
        {
            downstream.ContentLength = upstream.Content.Headers.ContentLength.Value;
        }

        foreach (string header in new[] { "Accept-Ranges", "Content-Range", "ETag", "Last-Modified" })
        {
            if (upstream.Headers.TryGetValues(header, out var values) || upstream.Content.Headers.TryGetValues(header, out values))
            {
                downstream.Headers[header] = values.ToArray();
            }
        }
    }
}
