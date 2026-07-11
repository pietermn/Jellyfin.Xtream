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
using System.Net;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Resolves and validates the Jellyfin base URL embedded in playback and STRM links.
/// </summary>
internal static class PublicServerUrlPolicy
{
    /// <summary>
    /// Uses the explicit plugin override when present, otherwise Jellyfin's advertised URL.
    /// </summary>
    /// <param name="configuredUrl">Optional plugin override.</param>
    /// <param name="jellyfinAdvertisedUrl">URL selected by Jellyfin, including its Published Server URL setting.</param>
    /// <returns>A normalized absolute base URL without a trailing slash.</returns>
    public static string Resolve(string? configuredUrl, string jellyfinAdvertisedUrl)
    {
        return Normalize(string.IsNullOrWhiteSpace(configuredUrl)
            ? jellyfinAdvertisedUrl
            : configuredUrl);
    }

    /// <summary>
    /// Validates an optional saved override.
    /// </summary>
    /// <param name="configuredUrl">Optional plugin override.</param>
    public static void ValidateOptional(string? configuredUrl)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            _ = Normalize(configuredUrl);
        }
    }

    private static string Normalize(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || (IPAddress.TryParse(uri.IdnHost, out IPAddress? address)
                && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))))
        {
            throw new ArgumentException(
                "The public Jellyfin URL must be an absolute HTTP(S) URL without credentials, query, fragment, or an unspecified host.",
                nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
