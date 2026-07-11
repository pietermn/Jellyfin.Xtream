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
/// Signs opaque proxy grants using random server-side key material.
/// </summary>
internal static class StreamProxySigner
{
    /// <summary>
    /// Creates a signature for a proxied stream request.
    /// </summary>
    /// <param name="key">The random server-side signing key.</param>
    /// <param name="purpose">The grant purpose.</param>
    /// <param name="keyId">The identifier for the current signing key.</param>
    /// <param name="connection">The provider connection to bind the grant to.</param>
    /// <param name="configurationFingerprint">The current stream-selection fingerprint.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="startTicks">The optional catch-up start in ticks.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <param name="expiresAtUnixSeconds">The optional expiry time.</param>
    /// <returns>The URL-safe signature.</returns>
    public static string Sign(
        ReadOnlySpan<byte> key,
        StreamProxyGrantPurpose purpose,
        string keyId,
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        long? expiresAtUnixSeconds)
    {
        byte[] payload = Encoding.UTF8.GetBytes(GetPayload(
            purpose,
            keyId,
            connection,
            configurationFingerprint,
            type,
            id,
            extension,
            startTicks,
            durationMinutes,
            expiresAtUnixSeconds));
        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload));
    }

    /// <summary>
    /// Validates a signature for a proxied stream request.
    /// </summary>
    /// <param name="key">The random server-side signing key.</param>
    /// <param name="purpose">The grant purpose.</param>
    /// <param name="keyId">The identifier for the current signing key.</param>
    /// <param name="connection">The provider connection to bind the grant to.</param>
    /// <param name="configurationFingerprint">The current stream-selection fingerprint.</param>
    /// <param name="type">The stream type.</param>
    /// <param name="id">The provider stream identifier.</param>
    /// <param name="extension">The optional container extension.</param>
    /// <param name="startTicks">The optional catch-up start in ticks.</param>
    /// <param name="durationMinutes">The optional catch-up duration.</param>
    /// <param name="expiresAtUnixSeconds">The optional expiry time.</param>
    /// <param name="signature">The supplied signature.</param>
    /// <returns><see langword="true"/> when the signature is valid.</returns>
    public static bool Verify(
        ReadOnlySpan<byte> key,
        StreamProxyGrantPurpose purpose,
        string keyId,
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        long? expiresAtUnixSeconds,
        string signature)
    {
        try
        {
            byte[] supplied = WebEncoders.Base64UrlDecode(signature);
            byte[] expected = WebEncoders.Base64UrlDecode(Sign(
                key,
                purpose,
                keyId,
                connection,
                configurationFingerprint,
                type,
                id,
                extension,
                startTicks,
                durationMinutes,
                expiresAtUnixSeconds));
            return CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string GetConnectionFingerprint(ConnectionInfo connection)
    {
        byte[] value = Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{connection.BaseUrl.Trim().TrimEnd('/')}\0{connection.UserName}\0{connection.Password}"));
        return WebEncoders.Base64UrlEncode(SHA256.HashData(value));
    }

    private static string GetPayload(
        StreamProxyGrantPurpose purpose,
        string keyId,
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        long? expiresAtUnixSeconds)
    {
        string normalizedExtension = StreamUriBuilder.NormalizeExtension(extension).TrimStart('.').ToUpperInvariant();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"v1:{purpose}:{keyId}:{GetConnectionFingerprint(connection)}:{configurationFingerprint}:{(int)type}:{id}:{normalizedExtension}:{startTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}:{durationMinutes}:{expiresAtUnixSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}");
    }
}
