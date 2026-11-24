// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public class MatchServerChatSessionMetadata
{
    public required int ServerID { get; set; }

    public required string SessionCookie { get; set; }

    public required int ChatProtocolVersion { get; set; }

    public ChatProtocol.ServerStatus Status { get; set; } = ChatProtocol.ServerStatus.SERVER_STATUS_UNKNOWN;

    public string Address { get; set; } = string.Empty;

    public int Port { get; set; }

    public string LocalAddress { get; set; } = string.Empty;

    public int LocalPort { get; set; }

    public int MatchID { get; set; } = -1;

    public string Location { get; set; } = string.Empty;

    public string GameName { get; set; } = string.Empty;

    public string MapName { get; set; } = string.Empty;

    public string GameMode { get; set; } = string.Empty;

    public byte TeamSize { get; set; } = 5;

    public int CurrentGameTime { get; set; }

    public int CurrentGamePhase { get; set; }

    public int ClientCount { get; set; }

    public int MaxSpectators { get; set; } = 10;

    public bool CasualMode { get; set; }

    public byte Official { get; set; }

    public bool Gated { get; set; }

    public string LegionTeamInformation { get; set; } = string.Empty;

    public string HellbourneTeamInformation { get; set; } = string.Empty;

    public string[] PlayerInformation { get; set; } = new string[10];

    public bool IsQueueArcade { get; set; }

    public static bool DetectQueueArcade(
        string? serverName,
        string? gameName,
        int port,
        int localPort,
        string? hostAccountName = null,
        int instance = 0)
    {
        // Never classify by player-chosen lobby gameName (spoof hazard on public Tier D servers).
        _ = gameName;

        if (MatchServer.IsQueueArcadeHostAccount(hostAccountName) ||
            MatchServer.IsQueueArcadeInstance(instance) ||
            MatchServer.IsQueueArcadePort(port) ||
            MatchServer.IsQueueArcadePort(localPort))
        {
            return true;
        }

        // If authoritative non-arcade host/instance or Tier L/D/K port is present, never fall back to serverName.
        if (!string.IsNullOrWhiteSpace(hostAccountName) ||
            instance > 0 ||
            MatchServer.IsStandardDedicatedPort(port) ||
            MatchServer.IsStandardDedicatedPort(localPort))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(serverName) &&
            serverName.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
