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
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Xtream.Client;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Signs opaque proxy URLs so they grant access to one provider stream without revealing credentials.
/// </summary>
internal static class StreamProxySigner
{
    /// <summary>
    /// Creates a signature for a proxied stream request.
    /// </summary>
    /// <param name="connection">The provider connection.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="startTicks">The optional catch-up start in ticks.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <returns>The URL-safe signature.</returns>
    public static string Sign(
        ConnectionInfo connection,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes)
    {
        byte[] key = GetKey(connection);
        byte[] payload = Encoding.UTF8.GetBytes(GetPayload(type, id, extension, startTicks, durationMinutes));
        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload));
    }

    /// <summary>
    /// Validates a signature for a proxied stream request.
    /// </summary>
    /// <param name="connection">The provider connection.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="startTicks">The optional catch-up start in ticks.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <param name="signature">The supplied signature.</param>
    /// <returns><see langword="true"/> when the signature is valid.</returns>
    public static bool Verify(
        ConnectionInfo connection,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        string signature)
    {
        try
        {
            byte[] supplied = WebEncoders.Base64UrlDecode(signature);
            byte[] expected = WebEncoders.Base64UrlDecode(Sign(connection, type, id, extension, startTicks, durationMinutes));
            return CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] GetKey(ConnectionInfo connection)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{connection.BaseUrl}\0{connection.UserName}\0{connection.Password}"));
    }

    private static string GetPayload(StreamType type, int id, string? extension, long? startTicks, int durationMinutes)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)type}:{id}:{extension?.Trim().TrimStart('.').ToUpperInvariant() ?? string.Empty}:{startTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}:{durationMinutes}");
    }
}
