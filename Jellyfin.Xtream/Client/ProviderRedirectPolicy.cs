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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Jellyfin.Xtream.Client;

internal static class ProviderRedirectPolicy
{
    private static readonly IPAddress _awsMetadataIpv6Address = IPAddress.Parse("fd00:ec2::254");
    private static readonly IPAddress _googleMetadataIpv6Address = IPAddress.Parse("fd20:ce::254");
    private static readonly string[] _metadataHostNames =
    [
        "instance-data",
        "metadata",
        "metadata.google.internal",
        "metadata.goog",
    ];

    internal static bool IsRedirect(HttpStatusCode statusCode)
        => (int)statusCode is 301 or 302 or 303 or 307 or 308;

    internal static void EnsureSameOrigin(Uri providerBaseUri, Uri requestUri)
    {
        if (!IsSameOrigin(providerBaseUri, requestUri))
        {
            throw new HttpRequestException("The provider request URI does not match the configured provider origin.");
        }
    }

    internal static bool IsSameOrigin(Uri providerBaseUri, Uri requestUri)
    {
        EnsureHttpUri(providerBaseUri);
        EnsureHttpUri(requestUri);

        return string.IsNullOrEmpty(providerBaseUri.UserInfo)
            && string.IsNullOrEmpty(requestUri.UserInfo)
            && string.Equals(providerBaseUri.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(providerBaseUri.IdnHost, requestUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && providerBaseUri.Port == requestUri.Port;
    }

    internal static void EnsurePublicRedirectUri(Uri redirectUri)
    {
        EnsureHttpUri(redirectUri);
        if (!string.IsNullOrEmpty(redirectUri.UserInfo))
        {
            throw new HttpRequestException("The provider redirect URI contains user information.");
        }

        EnsureAllowedHostName(redirectUri.IdnHost.TrimEnd('.'));
    }

    internal static IPAddress[] SelectApprovedAddresses(string host, IEnumerable<IPAddress> addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(addresses);

        host = host.TrimEnd('.');
        EnsureAllowedHostName(host);

        IPAddress[] distinct = addresses.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            throw new HttpRequestException("The provider host did not resolve to an address.");
        }

        IPAddress[] publiclyRoutable = distinct.Where(IsPublicAddress).ToArray();
        if (publiclyRoutable.Length > 0)
        {
            return publiclyRoutable;
        }

        bool explicitLocalOrigin = IPAddress.TryParse(host, out _)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal);
        if (!explicitLocalOrigin)
        {
            throw new HttpRequestException("The provider host resolved only to private or local addresses.");
        }

        IPAddress[] explicitlyConfigured = distinct.Where(IsAllowedExplicitLocalAddress).ToArray();
        return explicitlyConfigured.Length > 0
            ? explicitlyConfigured
            : throw new HttpRequestException("The provider endpoint is not publicly routable or an explicitly configured private-network address.");
    }

    internal static IPAddress[] SelectPublicAddresses(string host, IEnumerable<IPAddress> addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(addresses);

        host = host.TrimEnd('.');
        EnsureAllowedHostName(host);

        IPAddress[] publiclyRoutable = addresses.Distinct().Where(IsPublicAddress).ToArray();
        return publiclyRoutable.Length > 0
            ? publiclyRoutable
            : throw new HttpRequestException("The provider redirect endpoint is not publicly routable.");
    }

    internal static void EnsureAllowedHostName(string host)
    {
        if (_metadataHostNames.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("The provider endpoint is a reserved metadata host.");
        }
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (TryGetNat64Address(address, out IPAddress translatedAddress))
        {
            return IsPublicAddress(translatedAddress);
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 => false,
                10 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 0 => false,
                192 when bytes[1] == 88 && bytes[2] == 99 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // RFC 4193 unique-local, RFC 6052 local-use translation, discard-only,
            // special-purpose 2001::/23, documentation, and deprecated 6to4 ranges.
            return (bytes[0] & 0xFE) != 0xFC
                && !(bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B && bytes[4] == 0x00 && bytes[5] == 0x01)
                && !(bytes[0] == 0x01 && bytes[1..8].All(value => value == 0))
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xFE) == 0)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
                && !(bytes[0] == 0x20 && bytes[1] == 0x02);
        }

        return false;
    }

    private static void EnsureHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            throw new HttpRequestException("Provider requests require an absolute HTTP or HTTPS URI.");
        }
    }

    private static bool IsAllowedExplicitLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xFE) == 0xFC
            && !address.Equals(_awsMetadataIpv6Address)
            && !address.Equals(_googleMetadataIpv6Address);
    }

    private static bool TryGetNat64Address(IPAddress address, out IPAddress translatedAddress)
    {
        translatedAddress = IPAddress.None;
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] != 0x00
            || bytes[1] != 0x64
            || bytes[2] != 0xFF
            || bytes[3] != 0x9B
            || bytes[4..12].Any(value => value != 0))
        {
            return false;
        }

        translatedAddress = new IPAddress(bytes[12..16]);
        return true;
    }
}
