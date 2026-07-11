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
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Xtream.Configuration;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Creates a stable, non-reversible binding for selections that may mint proxy grants.
/// </summary>
internal static class StreamProxyConfigurationFingerprint
{
    /// <summary>
    /// Creates a fingerprint for the current VOD and series selections.
    /// Provider connection details are bound separately by <see cref="StreamProxySigner"/>.
    /// </summary>
    /// <param name="configuration">The active plugin configuration.</param>
    /// <returns>A URL-safe SHA-256 fingerprint.</returns>
    public static string Create(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using MemoryStream canonical = new();
        using (Utf8JsonWriter writer = new(canonical))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString(
                "publicServerUrl",
                NormalizePublicServerUrl(configuration.PublicServerUrl));
            WriteExport(
                writer,
                "vod",
                configuration.IsVodStrmExportEnabled,
                configuration.VodStrmExportPath,
                configuration.Vod);
            WriteExport(
                writer,
                "series",
                configuration.IsSeriesStrmExportEnabled,
                configuration.SeriesStrmExportPath,
                configuration.Series);
            writer.WriteEndObject();
        }

        return WebEncoders.Base64UrlEncode(SHA256.HashData(canonical.ToArray()));
    }

    private static void WriteExport(
        Utf8JsonWriter writer,
        string name,
        bool enabled,
        string rootPath,
        IEnumerable<KeyValuePair<int, HashSet<int>>> selections)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", enabled);
        writer.WriteString("root", NormalizeRoot(rootPath));
        writer.WritePropertyName("selections");
        writer.WriteStartArray();
        foreach (KeyValuePair<int, HashSet<int>> selection in selections.OrderBy(item => item.Key))
        {
            writer.WriteStartObject();
            writer.WriteNumber("category", selection.Key);
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (int itemId in selection.Value.OrderBy(item => item))
            {
                writer.WriteNumberValue(itemId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return string.Empty;
        }

        try
        {
            return StrmExportPathPolicy.NormalizeRoot(rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // Keep malformed saved configuration deterministic without allowing it to break
            // unrelated playback. The value is only included inside the SHA-256 input.
            return "invalid\0" + rootPath.Trim();
        }
    }

    private static string NormalizePublicServerUrl(string publicServerUrl)
    {
        if (string.IsNullOrWhiteSpace(publicServerUrl))
        {
            return string.Empty;
        }

        try
        {
            return PublicServerUrlPolicy.Resolve(publicServerUrl, "https://unused.invalid");
        }
        catch (ArgumentException)
        {
            return "invalid\0" + publicServerUrl.Trim();
        }
    }
}
