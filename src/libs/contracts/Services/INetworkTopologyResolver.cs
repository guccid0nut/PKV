// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;

namespace NEXUS.Contracts.Services;

/// <summary>
///     Provides thread-safe, cached network topology information (public IP and chat server routing)
///     without inline blocking HTTP requests on hot request pathways.
/// </summary>
public interface INetworkTopologyResolver
{
    /// <summary>
    ///     Gets the currently cached public IP address, or local IP fallback if not yet resolved.
    /// </summary>
    string? GetPublicIP();

    /// <summary>
    ///     Asynchronously retrieves the cached public IP address, performing a background discovery if uninitialized.
    /// </summary>
    Task<string?> GetPublicIPAsync(CancellationToken ct = default);

    /// <summary>
    ///     Resolves the optimal chat server host and client connection port for an authenticated client.
    /// </summary>
    (string Host, int Port) ResolveChatServer(
        IPAddress clientIP,
        string? hostHeader,
        string accountName,
        string accountType);

    /// <summary>
    ///     Resolves the optimal split-horizon game server IPv4 address for a client:
    ///     same-PC loopback gets 127.0.0.1, LAN/VPN/Tailscale gets the routable local/VPN interface IP,
    ///     and external WAN clients get the server's public IP.
    /// </summary>
    string ResolveGameServerAddress(
        IPAddress? clientIP,
        string? registeredServerIp,
        string? serverLocalIp = null,
        string? serverTruePublicIp = null);
}
