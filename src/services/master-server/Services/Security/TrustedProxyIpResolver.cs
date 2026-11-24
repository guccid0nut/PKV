// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;

namespace KONGOR.MasterServer.Services.Security;

/// <summary>
///     Resolves the effective client IP address while rejecting spoofed proxy headers
///     (<c>CF-Connecting-IP</c>, <c>X-Forwarded-For</c>) from untrusted direct WAN peers.
/// </summary>
public static class TrustedProxyIpResolver
{
    public static IPAddress ResolveClientIp(HttpContext context, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPAddress remoteIp = context.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        if (!IsTrustedProxyPeer(remoteIp, configuration))
        {
            return remoteIp;
        }

        string? forwardedHeader = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                                  ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(forwardedHeader))
        {
            string firstIpToken = forwardedHeader.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (IPAddress.TryParse(firstIpToken, out IPAddress? parsedForwardedIp))
            {
                return parsedForwardedIp;
            }
        }

        return remoteIp;
    }

    public static bool IsTrustedProxyPeer(IPAddress ip, IConfiguration? configuration = null)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        string trustAll = configuration?["TRUST_ALL_PROXIES"]
                          ?? Environment.GetEnvironmentVariable("TRUST_ALL_PROXIES")
                          ?? string.Empty;
        if (string.Equals(trustAll, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? configuredGateway = configuration?["INFRASTRUCTURE_GATEWAY"]
                                    ?? Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY");
        if (!string.IsNullOrWhiteSpace(configuredGateway) &&
            IPAddress.TryParse(configuredGateway.Trim(), out IPAddress? gatewayIp) &&
            gatewayIp.Equals(ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 link-local
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            byte[] bytes = ip.GetAddressBytes();
            // fc00::/7 Unique Local Address
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }
}
