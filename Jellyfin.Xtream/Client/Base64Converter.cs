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
using System.Text;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Class Base64Converter converts strings from and to base64.
/// </summary>
public class Base64Converter : JsonConverter
{
    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(string);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.Value is not string value || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Plain provider titles can themselves be syntactically valid unpadded Base64
        // (for example, "Test"). Decode only when the value has an explicit Base64 marker.
        if (!value.EndsWith('=')
            && !value.Contains('+', StringComparison.Ordinal)
            && !value.Contains('/', StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            string decoded = _strictUtf8.GetString(bytes);
            return decoded.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character))
                ? value
                : decoded;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            // Some Xtream implementations return plain text despite documenting base64.
            return value;
        }
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value as string ?? string.Empty);
        writer.WriteValue(Convert.ToBase64String(bytes));
    }
}
