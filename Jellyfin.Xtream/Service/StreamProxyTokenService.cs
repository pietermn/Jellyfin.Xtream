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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Xtream.Client;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Issues and validates purpose-specific stream proxy grants using persisted random keys.
/// </summary>
public sealed class StreamProxyTokenService
{
    private const int KeyRingVersion = 1;
    private const int SecretLength = 32;
    private const int KeyIdLength = 12;
    private const string KeyRingFileName = "Jellyfin.Xtream.proxy-keys.json";
    private static readonly TimeSpan _playbackLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _syncRoot = new();
    private readonly string _keyRingPath;
    private readonly TimeProvider _timeProvider;
    private ProxyKeyRing _keyRing;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamProxyTokenService"/> class.
    /// </summary>
    /// <param name="applicationPaths">The Jellyfin application paths.</param>
    public StreamProxyTokenService(IApplicationPaths applicationPaths)
        : this(
            Path.Combine(applicationPaths.PluginConfigurationsPath, KeyRingFileName),
            TimeProvider.System)
    {
    }

    internal StreamProxyTokenService(string keyRingPath, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRingPath);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _keyRingPath = Path.GetFullPath(keyRingPath);
        _timeProvider = timeProvider;
        _keyRing = LoadOrCreateKeyRing();
    }

    /// <summary>
    /// Rotates the playback signing key, immediately revoking outstanding playback grants.
    /// Durable STRM resolver grants remain valid.
    /// </summary>
    public void RotatePlaybackKey()
    {
        lock (_syncRoot)
        {
            ProxyKeyRing replacement = _keyRing with { Playback = CreateKey() };
            SaveKeyRing(replacement, overwrite: true);
            _keyRing = replacement;
        }
    }

    /// <summary>
    /// Rotates the durable STRM signing key, immediately revoking exported STRM grants.
    /// Existing STRM files must be regenerated after calling this method.
    /// </summary>
    public void RotatePersistentStrmKey()
    {
        lock (_syncRoot)
        {
            ProxyKeyRing replacement = _keyRing with { PersistentStrm = CreateKey() };
            SaveKeyRing(replacement, overwrite: true);
            _keyRing = replacement;
        }
    }

    internal StreamProxyGrant CreatePlaybackGrant(
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes)
    {
        ProxySigningKey key = GetKeyRingSnapshot().Playback;
        long expiresAt = _timeProvider.GetUtcNow().Add(_playbackLifetime).ToUnixTimeSeconds();
        string signature = Sign(
            key,
            StreamProxyGrantPurpose.Playback,
            connection,
            configurationFingerprint,
            type,
            id,
            extension,
            startTicks,
            durationMinutes,
            expiresAt);
        return new(key.Id, signature, expiresAt);
    }

    internal StreamProxyGrant CreatePersistentStrmGrant(
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes)
    {
        ProxySigningKey key = GetKeyRingSnapshot().PersistentStrm;
        string signature = Sign(
            key,
            StreamProxyGrantPurpose.PersistentStrm,
            connection,
            configurationFingerprint,
            type,
            id,
            extension,
            startTicks,
            durationMinutes,
            expiresAtUnixSeconds: null);
        return new(key.Id, signature, ExpiresAtUnixSeconds: null);
    }

    internal bool VerifyPlaybackGrant(
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        string keyId,
        long expiresAtUnixSeconds,
        string signature)
    {
        if (expiresAtUnixSeconds <= _timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        ProxySigningKey key = GetKeyRingSnapshot().Playback;
        return Verify(
            key,
            StreamProxyGrantPurpose.Playback,
            connection,
            configurationFingerprint,
            type,
            id,
            extension,
            startTicks,
            durationMinutes,
            expiresAtUnixSeconds,
            keyId,
            signature);
    }

    internal bool VerifyPersistentStrmGrant(
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        string keyId,
        string signature)
    {
        ProxySigningKey key = GetKeyRingSnapshot().PersistentStrm;
        return Verify(
            key,
            StreamProxyGrantPurpose.PersistentStrm,
            connection,
            configurationFingerprint,
            type,
            id,
            extension,
            startTicks,
            durationMinutes,
            expiresAtUnixSeconds: null,
            keyId,
            signature);
    }

    private static ProxySigningKey CreateKey()
    {
        return new(
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(KeyIdLength)),
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretLength)));
    }

    private static ProxyKeyRing CreateKeyRing()
    {
        return new(KeyRingVersion, CreateKey(), CreateKey());
    }

    private static ProxyKeyRing DeserializeAndValidate(string json, string path)
    {
        try
        {
            ProxyKeyRing? keyRing = JsonSerializer.Deserialize<ProxyKeyRing>(json, _serializerOptions);
            if (keyRing is null
                || keyRing.Version != KeyRingVersion
                || !IsValidKey(keyRing.Playback)
                || !IsValidKey(keyRing.PersistentStrm))
            {
                throw new InvalidDataException("The proxy key ring has an unsupported or invalid format.");
            }

            return keyRing;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The proxy key ring at '{path}' is not valid JSON.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"The proxy key ring at '{path}' contains invalid key material.", ex);
        }
    }

    private static bool IsValidKey(ProxySigningKey? key)
    {
        return key is not null
               && !string.IsNullOrWhiteSpace(key.Id)
               && !string.IsNullOrWhiteSpace(key.Secret)
               && WebEncoders.Base64UrlDecode(key.Secret).Length == SecretLength;
    }

    private ProxyKeyRing LoadOrCreateKeyRing()
    {
        string? directory = Path.GetDirectoryName(_keyRingPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The proxy key ring path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(_keyRingPath))
        {
            return DeserializeAndValidate(File.ReadAllText(_keyRingPath, Encoding.UTF8), _keyRingPath);
        }

        ProxyKeyRing created = CreateKeyRing();
        try
        {
            SaveKeyRing(created, overwrite: false);
            return created;
        }
        catch (IOException) when (File.Exists(_keyRingPath))
        {
            return DeserializeAndValidate(File.ReadAllText(_keyRingPath, Encoding.UTF8), _keyRingPath);
        }
    }

    private void SaveKeyRing(ProxyKeyRing keyRing, bool overwrite)
    {
        string? directory = Path.GetDirectoryName(_keyRingPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The proxy key ring path has no parent directory.");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_keyRingPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            string json = JsonSerializer.Serialize(keyRing, _serializerOptions);
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                byte[] content = Encoding.UTF8.GetBytes(json);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            SetOwnerOnlyPermissions(temporaryPath);
            File.Move(temporaryPath, _keyRingPath, overwrite);
            SetOwnerOnlyPermissions(_keyRingPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private ProxyKeyRing GetKeyRingSnapshot()
    {
        lock (_syncRoot)
        {
            return _keyRing;
        }
    }

    private static string Sign(
        ProxySigningKey key,
        StreamProxyGrantPurpose purpose,
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        long? expiresAtUnixSeconds)
    {
        byte[] secret = WebEncoders.Base64UrlDecode(key.Secret);
        try
        {
            return StreamProxySigner.Sign(
                secret,
                purpose,
                key.Id,
                connection,
                configurationFingerprint,
                type,
                id,
                extension,
                startTicks,
                durationMinutes,
                expiresAtUnixSeconds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static bool Verify(
        ProxySigningKey key,
        StreamProxyGrantPurpose purpose,
        ConnectionInfo connection,
        string configurationFingerprint,
        StreamType type,
        int id,
        string? extension,
        long? startTicks,
        int durationMinutes,
        long? expiresAtUnixSeconds,
        string keyId,
        string signature)
    {
        if (string.IsNullOrWhiteSpace(keyId)
            || string.IsNullOrWhiteSpace(signature)
            || !string.Equals(key.Id, keyId, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] secret = WebEncoders.Base64UrlDecode(key.Secret);
        try
        {
            return StreamProxySigner.Verify(
                secret,
                purpose,
                key.Id,
                connection,
                configurationFingerprint,
                type,
                id,
                extension,
                startTicks,
                durationMinutes,
                expiresAtUnixSeconds,
                signature);
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private sealed record ProxyKeyRing(
        int Version,
        ProxySigningKey Playback,
        ProxySigningKey PersistentStrm);

    private sealed record ProxySigningKey(string Id, string Secret);
}
