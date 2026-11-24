// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Services;

internal enum ArcadeIdleMode
{
    Hibernate = 0,
    SelfPlay = 1,
}

internal sealed record QueueArcadeMetricsSnapshot(
    long MatchesStartedTotal,
    long MatchesCompletedTotal,
    long RoundTimerRotationsTotal,
    long PlayersPushedTotal,
    long WastedLoadBlocksTotal,
    long PrePullWarningsSentTotal,
    long RankedPullsTotal,
    long ManualLeavesTotal,
    long MidLoadPullOverridesTotal,
    int ActiveArcadeMatchesCount,
    int ActiveHumanPlayersInArcadeCount,
    string? LastSelectedMap,
    long RegionMismatchSkipsTotal = 0,
    long BotReadinessFallbackTimeoutsTotal = 0,
    ArcadeIdleMode EffectiveIdleMode = ArcadeIdleMode.Hibernate,
    int RegionalLobbyDemandCount = 0,
    long HibernateHoldsTotal = 0,
    long SelfPlayMatchesStartedTotal = 0);

internal sealed record ActiveArcadeMatchSnapshot(
    int ServerId,
    int MatchUpId,
    int MatchId,
    string MapName,
    byte TeamSize,
    int ArcadeMode,
    int Team1HumanCount,
    int Team2HumanCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    bool EndGameSent);

internal interface IQueueArcadeService
{
    IReadOnlyList<string> RotationHistory { get; }

    QueueArcadeMetricsSnapshot GetMetricsSnapshot();

    string SelectNextMap(string? previousMap = null);

    void OnArcadeServerHandshake(ChatSession serverSession);

    void ConfirmArcadeMatchReady(ChatSession serverSession, int matchupId, int matchId);

    void NotifyArcadeServerBotReadiness(int serverId, int activeBotCount, bool explicitReadyAck = false);

    void NotifyArcadeServerStatus(int serverId, int matchId, ChatProtocol.ServerStatus status, int activeClientCount) =>
        NotifyArcadeServerBotReadiness(serverId, activeClientCount, explicitReadyAck: false);

    void OnArcadeMatchEnded(ChatSession serverSession, int matchId, string reason);

    void OnArcadeServerNotIdle(ChatSession serverSession, byte currentStatus, int matchUpId);

    bool ReleasePlayerFromArcade(int accountId, string reason, bool pauseCurrentQueueWait = false);

    void NotifyRankedMatchAllocating(IReadOnlyList<MatchmakingGroup> groups, string resolvedMapName);

    void OnPlayersPulledToRankedMatch(IReadOnlyList<MatchmakingGroupMember> members, int rankedMatchId);

    bool TryHandleArcadeChatCommand(ChatSession clientSession, string rawMessage);

    Task EvaluateTickAsync(CancellationToken cancellationToken = default);
}
