// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

#pragma warning disable CS9113 // Parameter is unread.

public abstract class ServerListResponse
{
    protected ServerListResponse(string cookie)
    {
        string key = Guid.CreateVersion7().ToString();

        AccountKey = key;
        AccountKeyHash = SRPAuthenticationHandlers.ComputeMatchServerChatAuthenticationHash(key, cookie);
    }

    [PHPProperty("acc_key")] public string AccountKey { get; set; }

    [PHPProperty("acc_key_hash")] public string AccountKeyHash { get; set; }

    [PHPProperty("vested_threshold")] public int VestedThreshold => 5;

    [PHPProperty(0)] public bool Zero => true;

    public static string? ResolveIP(MatchServer server, string? requestorIP) =>
        global::KONGOR.MasterServer.Services.Network.NetworkTopologyResolverService.ResolveSplitHorizonGameServerAddress(
            requestorIP,
            server.IPAddress,
            server.LocalIPAddress,
            server.TruePublicIPAddress);

    public static string ResolvePort(MatchServer server, string? requestorIP)
    {
        // When COMPEL proxy is active (server.Port is the proxy port 21235+), all clients (both external and local)
        // connect through server.Port to ensure DDoS filtering, netcode acceleration, and clean PONG probe handling.
        return server.Port.ToString();
    }

    private static bool IsUniversalLocation(string? location) =>
        string.IsNullOrWhiteSpace(location) ||
        location.Equals("ALL", StringComparison.OrdinalIgnoreCase) ||
        location.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) ||
        location.Equals("LAN", StringComparison.OrdinalIgnoreCase) ||
        location.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase);

    public static List<MatchServer> FilterByRegion(IEnumerable<MatchServer> servers, string? region)
    {
        if (string.IsNullOrWhiteSpace(region) || region.Equals("ALL", StringComparison.OrdinalIgnoreCase) || region.Equals("0", StringComparison.OrdinalIgnoreCase))
            return servers.ToList();

        string regUpper = region.Trim().ToUpperInvariant();

        return regUpper switch
        {
            "US" => servers.Where(s => IsUniversalLocation(s.Location) || (s.Location != null && (
                s.Location.Equals("US", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("USE", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("USW", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("USC", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("USS", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("NEWERTH", StringComparison.OrdinalIgnoreCase)
            ))).ToList(),

            "EU" => servers.Where(s => IsUniversalLocation(s.Location) || (s.Location != null && (
                s.Location.Equals("EU", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("DE", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("UK", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("NL", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("FR", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("RU", StringComparison.OrdinalIgnoreCase)
            ))).ToList(),

            "SEA" => servers.Where(s => IsUniversalLocation(s.Location) || (s.Location != null && (
                s.Location.Equals("SEA", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("SG", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("MY", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("PH", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("TH", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("VN", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("ID", StringComparison.OrdinalIgnoreCase) ||
                s.Location.Equals("CN", StringComparison.OrdinalIgnoreCase)
            ))).ToList(),

            _ => servers.Where(s => IsUniversalLocation(s.Location) || (s.Location != null && s.Location.Equals(regUpper, StringComparison.OrdinalIgnoreCase))).ToList()
        };
    }
}
