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
using MediaBrowser.Controller;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Creates client-safe Jellyfin proxy URLs for Xtream streams.
/// </summary>
public sealed class StreamProxyUrlBuilder(IServerApplicationHost applicationHost)
{
    private const string ProxyPath = "/Plugins/JellyfinXtream/v1/Stream";

    /// <summary>
    /// Creates a signed proxy URL.
    /// </summary>
    /// <param name="connection">The provider connection.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="start">The optional catch-up start.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <returns>The signed Jellyfin proxy URL.</returns>
    public string Build(
        ConnectionInfo connection,
        StreamType type,
        int id,
        string? extension = null,
        DateTime? start = null,
        int durationMinutes = 0)
    {
        string normalizedExtension = StreamUriBuilder.NormalizeExtension(extension).TrimStart('.');
        long? startTicks = start?.Ticks;
        string signature = StreamProxySigner.Sign(connection, type, id, normalizedExtension, startTicks, durationMinutes);
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["type"] = ((int)type).ToString(CultureInfo.InvariantCulture),
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
            ["extension"] = normalizedExtension,
            ["startTicks"] = startTicks?.ToString(CultureInfo.InvariantCulture),
            ["duration"] = durationMinutes.ToString(CultureInfo.InvariantCulture),
            ["signature"] = signature,
        };
        string serverUrl = applicationHost.GetSmartApiUrl(IPAddress.Any).TrimEnd('/');
        return QueryHelpers.AddQueryString(serverUrl + ProxyPath, query);
    }
}
