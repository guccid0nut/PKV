// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public class ClientChatSessionMetadata
{
    public required string RemoteIP { get; set; }

    public required string SessionCookie { get; set; }

    public required string SessionAuthenticationHash { get; set; }

    public required int ChatProtocolVersion { get; set; }

    public required byte OperatingSystemIdentifier { get; set; }

    public required byte OperatingSystemVersionMajor { get; set; }

    public required byte OperatingSystemVersionMinor { get; set; }

    public required byte OperatingSystemVersionPatch { get; set; }

    public required string OperatingSystemBuildCode { get; set; }

    public required string OperatingSystemArchitecture { get; set; }

    public required byte ClientVersionMajor { get; set; }

    public required byte ClientVersionMinor { get; set; }

    public required byte ClientVersionPatch { get; set; }

    public required byte ClientVersionRevision { get; set; }

    public required ChatProtocol.ChatClientStatus LastKnownClientState { get; set; }

    public required ChatProtocol.ChatModeType ClientChatModeState { get; set; }

    public required string ClientRegion { get; set; }

    public required string ClientLanguage { get; set; }

    public string? MatchServerAddress { get; set; }

    public int? MatchID { get; set; }

    public string? MatchName { get; set; }

    public bool QueueArcadeOptIn { get; set; } = true;

    public int? ActiveArcadeMatchId { get; set; }

    public int? ActiveArcadeServerId { get; set; }

    public DateTimeOffset? ArcadeJoinSentAtUtc { get; set; }

    public bool RankedPullWarningSent { get; set; }

    public int? LastVoluntarilyLeftArcadeMatchId { get; set; }

    public DateTimeOffset? LastVoluntarilyLeftArcadeAtUtc { get; set; }

    public DateTimeOffset? PauseArcadeUntilNextQueueUtc { get; set; }

    public DateTimeOffset? LastArcadeChatJoinAtUtc { get; set; }

    public DateTimeOffset? LastArcadePushAttemptAtUtc { get; set; }

    public DateTimeOffset? ArcadePushCooldownUntilUtc { get; set; }

    public int? ClientAckedMatchId { get; set; }

    public string? ClientAckedServerAddress { get; set; }

    public int? PendingRankedMatchId { get; set; }

    public string? PendingRankedServerAddress { get; set; }

    public bool AwaitingArcadeDisconnectForRankedPull { get; set; }

    public int? PostArcadeVictoryMatchId { get; set; }

    public int? PostArcadeVictoryServerId { get; set; }

    public DateTimeOffset? PostArcadeVictoryEndedAtUtc { get; set; }

    public int? AwaitingVictoryTransferFromServerId { get; set; }

    public bool VictoryTransferKickSent { get; set; }

    public string? LastArcadeChatCommand { get; set; }

    public DateTimeOffset? LastArcadeChatCommandAtUtc { get; set; }
}
