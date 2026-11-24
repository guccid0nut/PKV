// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;

namespace COMPEL.Services.Proxy;

/// <summary>
///     Classifies endpoint addresses as globally routable public internet addresses versus loopback,
///     private RFC 1918 LANs, link-local, IPv6 Unique Local (ULA), or RFC 6598 shared/Tailscale CGNAT ranges.
/// </summary>
internal static class NetworkAddressClassification
{
    public static bool IsPublicRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> octets = stackalloc byte[4];
            if (!address.TryWriteBytes(octets, out _))
            {
                return true;
            }

            return octets[0] switch
            {
                // 0.0.0.0/8 ("This" network, RFC 1122)
                0 => false,
                // 10.0.0.0/8 (Private-Use, RFC 1918)
                10 => false,
                // 100.64.0.0/10 (Shared Address Space / CGNAT / Tailscale, RFC 6598: 100.64.0.0 - 100.127.255.255)
                100 when (octets[1] & 0xC0) == 64 => false,
                // 127.0.0.0/8 (Loopback, RFC 1122)
                127 => false,
                // 169.254.0.0/16 (Link-Local, RFC 3927)
                169 when octets[1] == 254 => false,
                // 172.16.0.0/12 (Private-Use, RFC 1918: 172.16.0.0 - 172.31.255.255)
                172 when (octets[1] & 0xF0) == 16 => false,
                // 192.168.0.0/16 (Private-Use, RFC 1918)
                192 when octets[1] == 168 => false,
                // 255.255.255.255/32 (Limited Broadcast)
                255 when octets[1] == 255 && octets[2] == 255 && octets[3] == 255 => false,
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            {
                return false;
            }

            Span<byte> octets = stackalloc byte[16];
            if (!address.TryWriteBytes(octets, out _))
            {
                return true;
            }

            // fc00::/7 (Unique Local Unicast, RFC 4193)
            if ((octets[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            // fe80::/10 (Link-Local Unicast, RFC 4291)
            if (octets[0] == 0xFE && (octets[1] & 0xC0) == 0x80)
            {
                return false;
            }

            return true;
        }

        return true;
    }
}
