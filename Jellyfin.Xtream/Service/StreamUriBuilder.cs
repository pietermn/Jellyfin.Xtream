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
using Jellyfin.Xtream.Client;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Builds credential-bearing upstream stream URIs without exposing them to clients.
/// </summary>
internal static class StreamUriBuilder
{
    /// <summary>
    /// Builds an upstream Xtream stream URI.
    /// </summary>
    /// <param name="connection">The provider connection.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="start">The optional catch-up start.</param>
    /// <param name="durationMinutes">The catch-up duration.</param>
    /// <returns>The upstream URI.</returns>
    public static Uri Build(
        ConnectionInfo connection,
        StreamType type,
        int id,
        string? extension = null,
        DateTime? start = null,
        int durationMinutes = 0)
    {
        if (!Uri.TryCreate(connection.BaseUrl.TrimEnd('/'), UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The Xtream base URL must be an absolute HTTP or HTTPS URL.", nameof(connection));
        }

        if (type == StreamType.CatchUp)
        {
            Dictionary<string, string?> query = new(StringComparer.Ordinal)
            {
                ["username"] = connection.UserName,
                ["password"] = connection.Password,
                ["stream"] = id.ToString(CultureInfo.InvariantCulture),
                ["start"] = start?.ToString("yyyy'-'MM'-'dd':'HH'-'mm", CultureInfo.InvariantCulture),
                ["duration"] = durationMinutes.ToString(CultureInfo.InvariantCulture),
            };
            return new Uri(QueryHelpers.AddQueryString($"{connection.BaseUrl.TrimEnd('/')}/streaming/timeshift.php", query));
        }

        string prefix = type switch
        {
            StreamType.Series => "series",
            StreamType.Vod => "movie",
            _ => string.Empty,
        };
        string safeExtension = NormalizeExtension(extension);
        string pathPrefix = string.IsNullOrEmpty(prefix) ? string.Empty : $"/{prefix}";
        string path = string.Create(
            CultureInfo.InvariantCulture,
            $"{connection.BaseUrl.TrimEnd('/')}{pathPrefix}/{Uri.EscapeDataString(connection.UserName)}/{Uri.EscapeDataString(connection.Password)}/{id}{safeExtension}");
        return new Uri(path);
    }

    /// <summary>
    /// Normalizes a provider container extension for use in a URL.
    /// </summary>
    /// <param name="extension">The provider extension.</param>
    /// <returns>An empty value or a dot-prefixed safe extension.</returns>
    public static string NormalizeExtension(string? extension)
    {
        string value = extension?.Trim().TrimStart('.') ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                throw new ArgumentException("The stream extension contains unsupported characters.", nameof(extension));
            }
        }

        if (value.Length > 16)
        {
            throw new ArgumentException("The stream extension is too long.", nameof(extension));
        }

        return $".{value}";
    }
}
