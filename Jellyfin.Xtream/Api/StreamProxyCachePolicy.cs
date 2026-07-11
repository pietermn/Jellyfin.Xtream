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
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Prevents bearer grants, redirects, and proxied media from being retained by a client or intermediary cache.
/// </summary>
internal static class StreamProxyCachePolicy
{
    /// <summary>
    /// Applies cache prevention to a proxy response.
    /// </summary>
    /// <param name="response">Current HTTP response.</param>
    public static void Apply(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers[HeaderNames.CacheControl] = "no-store, private";
        response.Headers[HeaderNames.Pragma] = "no-cache";
        response.Headers[HeaderNames.Expires] = "0";
    }
}
