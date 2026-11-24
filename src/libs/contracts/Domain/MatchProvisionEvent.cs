// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Event payload dispatched when a match is allocated and ready for client connection.
/// </summary>
public sealed record MatchProvisionEvent(
    ulong MatchId,
    string MapName,
    int GameModeId,
    string ServerPublicIp,
    int ServerPublicPort,
    string MatchSecurityToken,
    IReadOnlyList<uint> LegionAccountIds,
    IReadOnlyList<uint> HellbourneAccountIds,
    DateTimeOffset StartTimeUtc)
{
    public string ServerIp => ServerPublicIp;
    public int ServerPort => ServerPublicPort;
    public int GameMode => GameModeId;
}
