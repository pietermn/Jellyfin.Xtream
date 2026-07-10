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
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Class StringBoolConverter converts strings from and to base64.
/// </summary>
public class StringBoolConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(bool) || objectType == typeof(bool?);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return objectType == typeof(bool?) ? null! : false;
        }

        return reader.Value switch
        {
            bool boolValue => boolValue,
            long integerValue => integerValue != 0,
            int integerValue => integerValue != 0,
            string stringValue => string.Equals(stringValue, "1", StringComparison.Ordinal)
                || string.Equals(stringValue, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stringValue, "yes", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        writer.WriteValue(value is true ? "1" : "0");
    }
}
