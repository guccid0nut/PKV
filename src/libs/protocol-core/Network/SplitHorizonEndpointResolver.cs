// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Models.ServerManagement;

namespace ASPIRE.Common.Network;

/// <summary>
///     Shared split-horizon game-server endpoint resolver used by both KONGOR.MasterServer and
///     TRANSMUTANSTEIN.ChatServer so loopback (127.0.0.1), LAN/Tailscale VPN (RFC1918 / 100.64.0.0/10),
///     NAT-hairpinned home-router clients, and external WAN clients always receive a directly reachable
///     IPv4:Port endpoint instead of unroutable "127.0.0.1" or private LAN addresses.
/// </summary>
public static class SplitHorizonEndpointResolver
{
    private static volatile string? s_cachedPublicIP;

    /// <summary>
    ///     Gets the latest discovered or configured public WAN IPv4 address for this host process.
    /// </summary>
    public static string? CurrentPublicIP => s_cachedPublicIP;

    /// <summary>
    ///     Updates the cached public WAN IPv4 address discovered for this host process.
    /// </summary>
    public static void SetCachedPublicIP(string? publicIp)
    {
        if (string.IsNullOrWhiteSpace(publicIp))
        {
            s_cachedPublicIP = null;
            return;
        }

        s_cachedPublicIP = publicIp.Trim();
    }

    /// <summary>
    ///     Clears the cached public WAN IPv4 address (for deterministic test isolation).
    /// </summary>
    public static void ResetCachedPublicIPForTesting() => s_cachedPublicIP = null;

    /// <summary>
    ///     Resolves the optimal (Address, Port) game-server target for a specific client IP address.
    /// </summary>
    public static (string Address, int Port) ResolveGameServerEndpoint(
        string serverAddress,
        int serverPort,
        string? localAddress,
        int? localPort,
        IPAddress? clientIp,
        string? serverTruePublicIp = null,
        string? discoveredLanIp = null,
        string? discoveredPublicIp = null)
    {
        if (clientIp is null)
        {
            return (serverAddress, serverPort);
        }

        int targetLocalPort = localPort is > 0 ? localPort.Value : serverPort;
        IPAddress normalizedClientIp = clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp;
        string requestorIp = normalizedClientIp.ToString();

        string? envLanHost = Environment.GetEnvironmentVariable("CHAT_SERVER_LAN_HOST")?.Trim();
        if (IsLoopbackOrLocalhost(envLanHost))
        {
            envLanHost = null;
        }

        string hostLocalIp = !string.IsNullOrWhiteSpace(discoveredLanIp)
            ? discoveredLanIp.Trim()
            : (!string.IsNullOrWhiteSpace(envLanHost) ? envLanHost : GetLocalIPAddress());

        string? publicIp = !string.IsNullOrWhiteSpace(discoveredPublicIp)
            ? discoveredPublicIp.Trim()
            : s_cachedPublicIP;

        string? envPublicHost = Environment.GetEnvironmentVariable("CHAT_SERVER_PUBLIC_HOST")?.Trim()
                                ?? Environment.GetEnvironmentVariable("PUBLIC_GAME_SERVER_IP")?.Trim();
        if (IsLoopbackOrLocalhost(envPublicHost))
        {
            envPublicHost = null;
        }

        string ipAddress = serverAddress ?? string.Empty;
        string? effectiveLocalIp = !string.IsNullOrWhiteSpace(localAddress) ? localAddress : ipAddress;
        bool isServerLoopback = string.IsNullOrWhiteSpace(ipAddress)
                                || IsLoopbackOrLocalhost(ipAddress)
                                || IsLoopbackOrLocalhost(effectiveLocalIp);

        bool matchesHostPublicIpWithoutExplicitLocal =
            string.IsNullOrWhiteSpace(localAddress)
            && !string.IsNullOrWhiteSpace(publicIp)
            && !IsLoopbackOrLocalhost(publicIp)
            && string.Equals(ipAddress, publicIp, StringComparison.OrdinalIgnoreCase);

        bool isHostedOnThisMachine =
            isServerLoopback
            || serverTruePublicIp is "127.0.0.1" or "::1"
            || (!string.IsNullOrWhiteSpace(effectiveLocalIp)
                && !IsLoopbackOrLocalhost(hostLocalIp)
                && string.Equals(effectiveLocalIp, hostLocalIp, StringComparison.OrdinalIgnoreCase))
            || matchesHostPublicIpWithoutExplicitLocal;

        string? serverPublicIp = null;
        if (!string.IsNullOrWhiteSpace(serverTruePublicIp) && !IsLoopbackOrLocalhost(serverTruePublicIp) && !IsPrivateOrVpnIPString(serverTruePublicIp))
        {
            serverPublicIp = serverTruePublicIp;
        }
        else if (!string.IsNullOrWhiteSpace(ipAddress) && !IsLoopbackOrLocalhost(ipAddress) && !IsPrivateOrVpnIPString(ipAddress))
        {
            serverPublicIp = ipAddress;
        }
        else if (!string.IsNullOrWhiteSpace(envPublicHost))
        {
            serverPublicIp = envPublicHost;
        }
        else if (!string.IsNullOrWhiteSpace(publicIp) && !IsLoopbackOrLocalhost(publicIp) && !IsPrivateOrVpnIPString(publicIp))
        {
            serverPublicIp = publicIp;
        }

        // 1. Same-PC loopback client (127.0.0.1 or ::1)
        if (IPAddress.IsLoopback(normalizedClientIp))
        {
            if (isServerLoopback || serverTruePublicIp is "127.0.0.1" or "::1" || matchesHostPublicIpWithoutExplicitLocal)
            {
                return ("127.0.0.1", targetLocalPort);
            }

            if (!string.IsNullOrWhiteSpace(serverTruePublicIp)
                && !IsPrivateOrVpnIPString(serverTruePublicIp)
                && (string.IsNullOrWhiteSpace(publicIp) || !string.Equals(serverTruePublicIp, publicIp, StringComparison.OrdinalIgnoreCase)))
            {
                return (serverTruePublicIp, serverPort);
            }

            if (!string.IsNullOrWhiteSpace(effectiveLocalIp) && IsPrivateOrVpnIPString(effectiveLocalIp))
            {
                return (effectiveLocalIp, targetLocalPort);
            }

            return (serverPublicIp ?? effectiveLocalIp ?? "127.0.0.1", serverPort);
        }

        // 2. Client IP equals the server's own local IP or this host's LAN IP (same physical machine)
        if (!string.IsNullOrWhiteSpace(localAddress)
            && !IsLoopbackOrLocalhost(localAddress)
            && requestorIp.Equals(localAddress, StringComparison.OrdinalIgnoreCase))
        {
            return ("127.0.0.1", targetLocalPort);
        }

        if (isHostedOnThisMachine
            && !IsLoopbackOrLocalhost(hostLocalIp)
            && requestorIp.Equals(hostLocalIp, StringComparison.OrdinalIgnoreCase))
        {
            return ("127.0.0.1", targetLocalPort);
        }

        // 3. NAT Hairpinning: Client shares the server's or host's public WAN IP (behind the same home/office router)
        if ((!string.IsNullOrWhiteSpace(serverTruePublicIp) && !IsLoopbackOrLocalhost(serverTruePublicIp) && requestorIp.Equals(serverTruePublicIp, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(publicIp) && !IsLoopbackOrLocalhost(publicIp) && requestorIp.Equals(publicIp, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(localAddress) && !IsLoopbackOrLocalhost(localAddress))
            {
                return (localAddress, targetLocalPort);
            }

            if (!string.IsNullOrWhiteSpace(ipAddress) && !IsLoopbackOrLocalhost(ipAddress) && IsPrivateOrVpnIPString(ipAddress))
            {
                return (ipAddress, targetLocalPort);
            }

            if (isHostedOnThisMachine && !IsLoopbackOrLocalhost(hostLocalIp))
            {
                return (hostLocalIp, targetLocalPort);
            }

            return ("127.0.0.1", targetLocalPort);
        }

        // 4. Private Subnet / LAN / Tailscale VPN Client (RFC1918 + 100.64.0.0/10)
        if (IsPrivateOrVpnIP(normalizedClientIp))
        {
            // Explicit non-loopback private localAddress registered by the game server
            if (!string.IsNullOrWhiteSpace(localAddress) && !IsLoopbackOrLocalhost(localAddress) && IsPrivateOrVpnIPString(localAddress))
            {
                return (localAddress, targetLocalPort);
            }

            // Same private/VPN subnet as effectiveLocalIp
            if (AreInSamePrivateOrVpnSubnet(requestorIp, effectiveLocalIp) && !IsLoopbackOrLocalhost(effectiveLocalIp))
            {
                return (effectiveLocalIp!, targetLocalPort);
            }

            // Self-hosted server on this machine (registered as 127.0.0.1 or host LAN/public IP):
            // route LAN/VPN peer directly to the host interface IP (never 127.0.0.1 and never WAN hairpin).
            if (isHostedOnThisMachine)
            {
                if (!string.IsNullOrWhiteSpace(discoveredLanIp) && !IsLoopbackOrLocalhost(discoveredLanIp))
                {
                    return (discoveredLanIp.Trim(), targetLocalPort);
                }

                if (!string.IsNullOrWhiteSpace(envLanHost) && !IsLoopbackOrLocalhost(envLanHost))
                {
                    return (envLanHost, targetLocalPort);
                }

                string routedHostIp = GetLocalIPAddressForDestination(normalizedClientIp);
                if (!string.IsNullOrWhiteSpace(routedHostIp) && !IsLoopbackOrLocalhost(routedHostIp))
                {
                    return (routedHostIp, targetLocalPort);
                }

                if (!IsLoopbackOrLocalhost(hostLocalIp))
                {
                    return (hostLocalIp, targetLocalPort);
                }
            }

            if (!string.IsNullOrWhiteSpace(ipAddress) && !IsLoopbackOrLocalhost(ipAddress) && IsPrivateOrVpnIPString(ipAddress))
            {
                return (ipAddress, targetLocalPort);
            }
        }

        // 5. External WAN client -> return reachable Public IP (never 127.0.0.1 or RFC1918 when public IP is known)
        if (!string.IsNullOrWhiteSpace(serverPublicIp))
        {
            return (serverPublicIp, serverPort);
        }

        // 6. Offline / pure-LAN fallback when no public IP is configured or discovered
        if (!string.IsNullOrWhiteSpace(localAddress) && !IsLoopbackOrLocalhost(localAddress))
        {
            return (localAddress, serverPort);
        }

        if (!string.IsNullOrWhiteSpace(ipAddress) && !IsLoopbackOrLocalhost(ipAddress))
        {
            return (ipAddress, serverPort);
        }

        return (isHostedOnThisMachine ? hostLocalIp : "127.0.0.1", serverPort);
    }

    /// <summary>
    ///     Resolves a game-server IP address string (without port) using split-horizon topology rules.
    /// </summary>
    public static string ResolveSplitHorizonGameServerAddress(
        string? requestorIP,
        string? registeredServerIp,
        string? serverLocalIp = null,
        string? serverTruePublicIp = null,
        string? discoveredLanIp = null,
        string? discoveredPublicIp = null)
    {
        IPAddress? parsedClientIp = null;
        if (string.IsNullOrWhiteSpace(requestorIP) || requestorIP is "127.0.0.1" or "::1")
        {
            parsedClientIp = IPAddress.Loopback;
        }
        else if (IPAddress.TryParse(requestorIP, out IPAddress? parsed))
        {
            parsedClientIp = parsed;
        }

        (string resolvedIp, _) = ResolveGameServerEndpoint(
            registeredServerIp ?? string.Empty,
            serverPort: 11235,
            localAddress: serverLocalIp,
            localPort: null,
            clientIp: parsedClientIp,
            serverTruePublicIp: serverTruePublicIp,
            discoveredLanIp: discoveredLanIp,
            discoveredPublicIp: discoveredPublicIp);

        return resolvedIp;
    }

    /// <summary>
    ///     Resolves a stored "IP:Port" match-server string (or live <see cref="MatchServer" /> record)
    ///     for a specific recipient client IP (Friend Presence, Initial Status, Player Spectate).
    /// </summary>
    public static string ResolveStoredServerAddressForClient(
        string? storedIpPort,
        IPAddress? recipientClientIp,
        MatchServer? matchServer = null,
        string? discoveredLanIp = null,
        string? discoveredPublicIp = null)
    {
        if (matchServer is not null)
        {
            (string resolvedHost, int resolvedPort) = ResolveGameServerEndpoint(
                matchServer.IPAddress,
                matchServer.Port,
                matchServer.LocalIPAddress,
                matchServer.LocalPort,
                recipientClientIp,
                matchServer.TruePublicIPAddress,
                discoveredLanIp,
                discoveredPublicIp);

            return string.Create(CultureInfo.InvariantCulture, $"{resolvedHost}:{resolvedPort}");
        }

        if (string.IsNullOrWhiteSpace(storedIpPort) || storedIpPort == "0.0.0.0:0")
        {
            return "0.0.0.0:0";
        }

        if (recipientClientIp is null)
        {
            return storedIpPort;
        }

        int lastColon = storedIpPort.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= storedIpPort.Length - 1)
        {
            return storedIpPort;
        }

        string rawHost = storedIpPort[..lastColon];
        if (!int.TryParse(storedIpPort.AsSpan(lastColon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port <= 0)
        {
            return storedIpPort;
        }

        (string host, int resolvedTargetPort) = ResolveGameServerEndpoint(
            rawHost,
            port,
            localAddress: null,
            localPort: null,
            clientIp: recipientClientIp,
            serverTruePublicIp: null,
            discoveredLanIp: discoveredLanIp,
            discoveredPublicIp: discoveredPublicIp);

        return string.Create(CultureInfo.InvariantCulture, $"{host}:{resolvedTargetPort}");
    }

    public static bool IsLoopbackOrLocalhost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip);
    }

    public static bool IsCgnat(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                return false;
            }
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
        }

        return false;
    }

    public static bool IsPrivateOrVpnIP(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
                if (IPAddress.IsLoopback(ip))
                {
                    return true;
                }
            }
            else
            {
                byte[] bytes = ip.GetAddressBytes();
                return (bytes[0] & 0xFE) == 0xFC;
            }
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || IsCgnat(ip);
        }

        return false;
    }

    public static bool IsPrivateOrVpnIPString(string? ipStr)
    {
        if (string.IsNullOrWhiteSpace(ipStr) || !IPAddress.TryParse(ipStr, out IPAddress? ip))
        {
            return false;
        }

        return IsPrivateOrVpnIP(ip);
    }

    public static bool AreInSamePrivateOrVpnSubnet(string? ip1Str, string? ip2Str)
    {
        if (string.IsNullOrWhiteSpace(ip1Str) || string.IsNullOrWhiteSpace(ip2Str))
        {
            return false;
        }

        if (!IPAddress.TryParse(ip1Str, out IPAddress? ip1) || !IPAddress.TryParse(ip2Str, out IPAddress? ip2))
        {
            return false;
        }

        if (ip1.IsIPv4MappedToIPv6) ip1 = ip1.MapToIPv4();
        if (ip2.IsIPv4MappedToIPv6) ip2 = ip2.MapToIPv4();

        if (ip1.AddressFamily != AddressFamily.InterNetwork || ip2.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b1 = ip1.GetAddressBytes();
        byte[] b2 = ip2.GetAddressBytes();

        if (b1[0] == 192 && b1[1] == 168 && b2[0] == 192 && b2[1] == 168)
        {
            return b1[2] == b2[2];
        }

        if (b1[0] == 10 && b2[0] == 10)
        {
            return b1[1] == b2[1];
        }

        if (b1[0] == 172 && b1[1] >= 16 && b1[1] <= 31 && b2[0] == 172 && b2[1] == b1[1])
        {
            return true;
        }

        if (b1[0] == 100 && b1[1] >= 64 && b1[1] <= 127 && b2[0] == 100 && b2[1] >= 64 && b2[1] <= 127)
        {
            return true;
        }

        return false;
    }

    public static string GetLocalIPAddressForDestination(IPAddress destination)
    {
        try
        {
            IPAddress target = destination.IsIPv4MappedToIPv6 ? destination.MapToIPv4() : destination;
            if (target.AddressFamily == AddressFamily.InterNetwork)
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect(target, 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint && !IPAddress.IsLoopback(endPoint.Address))
                {
                    return endPoint.Address.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SplitHorizonEndpointResolver] Destination route probe for '{destination}' fell back to default interface: {ex.Message}");
        }

        return GetLocalIPAddress();
    }

    public static string GetLocalIPAddress()
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SplitHorizonEndpointResolver] UDP route probe failed: {ex.Message}");
            try
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch (Exception dnsEx)
            {
                Debug.WriteLine($"[SplitHorizonEndpointResolver] DNS host fallback failed: {dnsEx.Message}");
            }
        }

        return "127.0.0.1";
    }
}
