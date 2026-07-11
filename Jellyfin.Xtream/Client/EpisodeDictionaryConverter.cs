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
using System.Linq;
using Jellyfin.Xtream.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Converts Xtream episode payloads to a dictionary keyed by season number.
/// </summary>
public class EpisodeDictionaryConverter : JsonConverter
{
    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(Dictionary<int, ICollection<Episode>>);
    }

    /// <inheritdoc/>
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        switch (token.Type)
        {
            case JTokenType.Object:
                return ReadSeasonDictionary((JObject)token, serializer);
            case JTokenType.Array:
                return ReadEpisodeArray((JArray)token, serializer);
            case JTokenType.Null:
                return new Dictionary<int, ICollection<Episode>>();
            default:
                return new Dictionary<int, ICollection<Episode>>();
        }
    }

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        throw new NotSupportedException();
    }

    private static Dictionary<int, ICollection<Episode>> ReadSeasonDictionary(JObject token, JsonSerializer serializer)
    {
        var result = new Dictionary<int, ICollection<Episode>>();
        foreach (JProperty property in token.Properties())
        {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seasonNumber))
            {
                continue;
            }

            result[seasonNumber] = ReadEpisodes(property.Value, serializer);
        }

        return result;
    }

    private static Dictionary<int, ICollection<Episode>> ReadEpisodeArray(JArray token, JsonSerializer serializer)
    {
        var episodes = new List<Episode>();
        int seasonNumber = 1;
        foreach (JToken child in token.Children())
        {
            List<Episode> childEpisodes = ReadEpisodes(child, serializer);
            if (child.Type == JTokenType.Array)
            {
                foreach (Episode episode in childEpisodes.Where(episode => episode.Season <= 0))
                {
                    episode.Season = seasonNumber;
                }

                seasonNumber++;
            }

            episodes.AddRange(childEpisodes);
        }

        return episodes
            .GroupBy(episode => episode.Season)
            .ToDictionary(group => group.Key, group => (ICollection<Episode>)group.ToList());
    }

    private static List<Episode> ReadEpisodes(JToken token, JsonSerializer serializer)
    {
        return token.Type switch
        {
            JTokenType.Array => token.Children().SelectMany(child => ReadEpisodes(child, serializer)).ToList(),
            JTokenType.Object => token.ToObject<Episode>(serializer) is Episode episode ? [episode] : [],
            JTokenType.Null => [],
            _ => [],
        };
    }
}
