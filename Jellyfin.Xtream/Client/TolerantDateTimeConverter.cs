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
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Reads provider timestamps without failing an entire response when one value is malformed.
/// Unix seconds, Unix milliseconds, and invariant date strings are supported.
/// </summary>
public sealed class TolerantDateTimeConverter : JsonConverter
{
    private const long MinUnixSeconds = -62_135_596_800;
    private const long MaxUnixSeconds = 253_402_300_799;
    private const long MinUnixMilliseconds = -62_135_596_800_000;
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(DateTime) || objectType == typeof(DateTime?);
    }

    /// <inheritdoc />
    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        if (TryParse(reader.Value, out DateTime value))
        {
            return value;
        }

        return objectType == typeof(DateTime?) ? null : default(DateTime);
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        throw new NotSupportedException();
    }

    internal static bool TryParse(object? value, out DateTime result)
    {
        switch (value)
        {
            case DateTime dateTime:
                result = dateTime.Kind switch
                {
                    DateTimeKind.Utc => dateTime,
                    DateTimeKind.Local => dateTime.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
                };
                return true;
            case DateTimeOffset dateTimeOffset:
                result = dateTimeOffset.UtcDateTime;
                return true;
            case long unixValue:
                return TryParseUnixValue(unixValue, out result);
            case int unixValue:
                return TryParseUnixValue(unixValue, out result);
            case decimal decimalValue when decimalValue == decimal.Truncate(decimalValue)
                                           && decimalValue >= long.MinValue
                                           && decimalValue <= long.MaxValue:
                return TryParseUnixValue((long)decimalValue, out result);
            case string text:
                return TryParseString(text, out result);
            default:
                result = default;
                return false;
        }
    }

    private static bool TryParseString(string text, out DateTime result)
    {
        string value = text.Trim();
        if (value.Length == 4
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            && year is >= 1 and <= 9999)
        {
            result = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixValue))
        {
            return TryParseUnixValue(unixValue, out result);
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed))
        {
            result = parsed.UtcDateTime;
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseUnixValue(long value, out DateTime result)
    {
        if (value is >= MinUnixSeconds and <= MaxUnixSeconds)
        {
            result = DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
            return true;
        }

        if (value is >= MinUnixMilliseconds and <= MaxUnixMilliseconds)
        {
            result = DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
            return true;
        }

        result = default;
        return false;
    }
}
