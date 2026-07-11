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
using System.Globalization;
using System.Net;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Creates client-safe Jellyfin proxy and STRM resolver URLs for Xtream streams.
/// </summary>
public sealed class StreamProxyUrlBuilder(
    IServerApplicationHost applicationHost,
    StreamProxyTokenService tokenService)
{
    private const string PlaybackPath = "/Plugins/JellyfinXtream/v1/Stream";
    private const string PersistentStrmPath = "/Plugins/JellyfinXtream/v1/Strm";

    /// <summary>
    /// Creates a short-lived signed playback URL.
    /// </summary>
    /// <param name="connection">The provider connection.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="start">The optional catch-up start.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <returns>The signed Jellyfin proxy URL.</returns>
    public string BuildPlayback(
        ConnectionInfo connection,
        StreamType type,
        int id,
        string? extension = null,
        DateTime? start = null,
        int durationMinutes = 0)
    {
        PluginConfiguration configuration = Plugin.Instance.Configuration;
        return BuildPlayback(
            connection,
            StreamProxyConfigurationFingerprint.Create(configuration),
            configuration.PublicServerUrl,
            type,
            id,
            extension,
            start,
            durationMinutes);
    }

    /// <summary>
    /// Creates a short-lived playback URL using one already-captured configuration fingerprint.
    /// </summary>
    /// <param name="connection">The captured provider connection.</param>
    /// <param name="configurationFingerprint">The captured selection/export fingerprint.</param>
    /// <param name="publicServerUrl">The captured optional public Jellyfin URL.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="start">The optional catch-up start.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <returns>The signed Jellyfin proxy URL.</returns>
    internal string BuildPlayback(
        ConnectionInfo connection,
        string configurationFingerprint,
        string? publicServerUrl,
        StreamType type,
        int id,
        string? extension = null,
        DateTime? start = null,
        int durationMinutes = 0)
    {
        string normalizedExtension = StreamUriBuilder.NormalizeExtension(extension).TrimStart('.');
        long? startTicks = start?.Ticks;
        StreamProxyGrant grant = tokenService.CreatePlaybackGrant(
            connection,
            configurationFingerprint,
            type,
            id,
            normalizedExtension,
            startTicks,
            durationMinutes);
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["type"] = ((int)type).ToString(CultureInfo.InvariantCulture),
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
            ["extension"] = normalizedExtension,
            ["startTicks"] = startTicks?.ToString(CultureInfo.InvariantCulture),
            ["duration"] = durationMinutes.ToString(CultureInfo.InvariantCulture),
            ["keyId"] = grant.KeyId,
            ["expires"] = grant.ExpiresAtUnixSeconds?.ToString(CultureInfo.InvariantCulture),
            ["signature"] = grant.Signature,
        };
        string serverUrl = ResolvePublicServerUrl(publicServerUrl);
        return QueryHelpers.AddQueryString(serverUrl + PlaybackPath, query);
    }

    /// <summary>
    /// Creates a durable signed STRM resolver URL. The resolver mints a new short-lived playback URL.
    /// </summary>
    /// <param name="connection">The provider connection.</param>
    /// <param name="configurationFingerprint">The selection fingerprint captured for this export run.</param>
    /// <param name="publicServerUrl">The public Jellyfin URL captured for this export run.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="start">The optional catch-up start.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <returns>The durable Jellyfin STRM resolver URL.</returns>
    public string BuildPersistentStrm(
        ConnectionInfo connection,
        string configurationFingerprint,
        string? publicServerUrl,
        StreamType type,
        int id,
        string? extension = null,
        DateTime? start = null,
        int durationMinutes = 0)
    {
        string normalizedExtension = StreamUriBuilder.NormalizeExtension(extension).TrimStart('.');
        long? startTicks = start?.Ticks;
        StreamProxyGrant grant = tokenService.CreatePersistentStrmGrant(
            connection,
            configurationFingerprint,
            type,
            id,
            normalizedExtension,
            startTicks,
            durationMinutes);
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["type"] = ((int)type).ToString(CultureInfo.InvariantCulture),
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
            ["extension"] = normalizedExtension,
            ["startTicks"] = startTicks?.ToString(CultureInfo.InvariantCulture),
            ["duration"] = durationMinutes.ToString(CultureInfo.InvariantCulture),
            ["keyId"] = grant.KeyId,
            ["signature"] = grant.Signature,
        };
        string serverUrl = ResolvePublicServerUrl(publicServerUrl);
        return QueryHelpers.AddQueryString(serverUrl + PersistentStrmPath, query);
    }

    private string ResolvePublicServerUrl(string? publicServerUrl)
    {
        return PublicServerUrlPolicy.Resolve(
            publicServerUrl,
            applicationHost.GetSmartApiUrl(IPAddress.Any));
    }
}
