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
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using XtreamConnectionInfo = Jellyfin.Xtream.Client.ConnectionInfo;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Resolves a durable exported STRM grant into a fresh short-lived playback grant.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Plugins/JellyfinXtream/v1/Strm")]
public sealed class StrmProxyController(
    StreamProxyTokenService tokenService,
    StreamProxyUrlBuilder urlBuilder) : ControllerBase
{
    /// <summary>
    /// Validates a persistent resolver grant and redirects to short-lived playback.
    /// </summary>
    /// <param name="type">The requested stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="startTicks">The optional catch-up start in ticks.</param>
    /// <param name="duration">The optional catch-up duration.</param>
    /// <param name="keyId">The persistent STRM signing key identifier.</param>
    /// <param name="signature">The persistent resolver signature.</param>
    /// <returns>A temporary redirect to a short-lived playback URL.</returns>
    [AcceptVerbs("GET", "HEAD")]
    public IActionResult Resolve(
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int duration,
        string keyId,
        string signature)
    {
        StreamProxyCachePolicy.Apply(Response);
        PluginConfiguration configuration = Plugin.Instance.Configuration;
        XtreamConnectionInfo connection = new(
            configuration.BaseUrl,
            configuration.Username,
            configuration.Password);
        string configurationFingerprint = StreamProxyConfigurationFingerprint.Create(configuration);
        if (id <= 0
            || !Enum.IsDefined(type)
            || string.IsNullOrWhiteSpace(signature)
            || !tokenService.VerifyPersistentStrmGrant(
                connection,
                configurationFingerprint,
                type,
                id,
                extension,
                startTicks,
                duration,
                keyId,
                signature))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        DateTime? start;
        try
        {
            start = startTicks.HasValue ? new DateTime(startTicks.Value, DateTimeKind.Utc) : null;
            string playbackUrl = urlBuilder.BuildPlayback(
                connection,
                configurationFingerprint,
                type,
                id,
                extension,
                start,
                duration);
            return RedirectPreserveMethod(playbackUrl);
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }
}
