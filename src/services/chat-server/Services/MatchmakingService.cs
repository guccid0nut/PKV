// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Network;
using ASPIRE.Common.Services.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Services.Agones;

namespace TRANSMUTANSTEIN.ChatServer.Services;

public class MatchmakingService(
    IConfiguration configuration,
    IChatContext chatContext,
    ITelemetryPublisherService? telemetryPublisher = null,
    TimeProvider? timeProvider = null,
    IAgonesAllocationClient? agonesAllocationClient = null,
    IRedisServerSlotLeaseService? slotLeaseService = null,
    StackExchange.Redis.IDatabase? distributedCache = null,
    Microsoft.Extensions.Logging.ILogger<MatchmakingService>? logger = null,
    IDistributedMatchmakingStateStore? distributedStateStore = null)
    : ResilientBackgroundService(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchmakingService>.Instance, nameof(MatchmakingService), timeProvider: timeProvider), IMatchmakingService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public const string ReadyCheckSenderName = "__kongor_ready_check__";
    public const string RedisPlayersPerTeamKey = "kongor:matchmaking:players_per_team";
    public const string RedisAllowTwoPlayerFallbackKey = "kongor:matchmaking:allow_two_player_fallback";
    private static readonly char[] ReadyCheckResponseSeparators = ['|', ':', ' '];
    private static int _nextMatchUpId = 100000;
    private static int _nextReadyCheckId = 200000;
    private IQueueArcadeService? _queueArcadeService;
    private DateTimeOffset? _lastRedisRuntimePollUtc;
    private int? _cachedRedisPlayersPerTeam;
    private bool? _cachedRedisAllowTwoPlayerFallback;
    private readonly ConcurrentDictionary<int, PendingReadyCheckState> _pendingReadyChecks = new();
    private readonly ConcurrentDictionary<(int MatchId, int AccountId), PendingRankedConnectEntry> _pendingRankedConnects = new();
    private readonly ConcurrentDictionary<Guid, byte> _syncedGroupGuids = new();

    public static ChatBuffer CreateReadyCheckWhisperPacket(string message)
    {
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        buffer.WriteString(ReadyCheckSenderName);
        buffer.WriteString(message);
        return buffer;
    }

    public static bool TryParseReadyCheckResponse(string? message, out int readyCheckId, out bool accepted)
    {
        readyCheckId = 0;
        accepted = false;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string trimmed = message.Trim();
        string[] parts = trimmed.Split(ReadyCheckResponseSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        string action = parts[0];
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out readyCheckId))
        {
            return false;
        }

        if (string.Equals(action, "ACCEPT", StringComparison.OrdinalIgnoreCase) || action == "1" || string.Equals(action, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            accepted = true;
            return true;
        }

        if (string.Equals(action, "DECLINE", StringComparison.OrdinalIgnoreCase) || action == "0" || string.Equals(action, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            accepted = false;
            return true;
        }

        return false;
    }

    private sealed class PendingRankedConnectEntry
    {
        public required ChatSession Session { get; init; }
        public required int AccountId { get; init; }
        public required string PlayerName { get; init; }
        public string? ClanTag { get; init; }
        public required int MatchId { get; init; }
        public required byte MatchType { get; init; }
        public required string TargetAddress { get; init; }
        public required int TargetPort { get; init; }
        public required int? PreviousArcadeServerId { get; init; }
        public required DateTimeOffset ConfirmedAtUtc { get; init; }
        public DateTimeOffset LastSentAtUtc { get; set; }
        public bool ArcadeKickSent { get; set; }
    }

    internal void AttachQueueArcadeService(IQueueArcadeService queueArcadeService) =>
        _queueArcadeService = queueArcadeService;

    internal Task<bool> ProcessMatchForSimulationAsync(List<MatchmakingGroup> groups) =>
        ProcessMatchUnsafe(groups);
    internal ConcurrentDictionary<int, PendingMatchState> PendingMatches { get; } = new();
    internal ConcurrentDictionary<int, PendingReadyCheckState> PendingReadyChecks => _pendingReadyChecks;

    public PendingReadyCheckState? GetPendingReadyCheck(int readyCheckId)
    {
        _pendingReadyChecks.TryGetValue(readyCheckId, out PendingReadyCheckState? state);
        return state;
    }

    public ConcurrentDictionary<int, MatchmakingGroup> Groups { get; } = [];

    // ... (GetMatchmakingGroup methods)
    public MatchmakingGroup? GetMatchmakingGroup(OneOf<int, string> memberIdentifier)
    {
        return memberIdentifier.Match(GetMatchmakingGroupByMemberID,
            GetMatchmakingGroupByMemberName);
    }

    public MatchmakingGroup? GetMatchmakingGroupByMemberID(int memberID)
    {
        return Groups.Values.FirstOrDefault(group => group.Members.Any(member => member.Account.ID == memberID));
    }

    public MatchmakingGroup? GetMatchmakingGroupByMemberName(string memberName)
    {
        return Groups.Values.FirstOrDefault(group =>
            group.Members.Any(member => member.Account.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase)));
    }


    public MatchmakingGroup? GetMatchmakingGroupByInvitedUser(string accountName)
    {
        // Try to find a group where the user is in the PendingInvites list
        return Groups.Values.FirstOrDefault(group =>
            group.PendingInvites.ContainsKey(accountName));
    }

    internal async Task PublishPopulationBreakdownSnapshotAsync(
        IEnumerable<MatchmakingGroup>? groups = null,
        DateTimeOffset? timestampUtc = null,
        bool forceImmediate = false)
    {
        if (telemetryPublisher is null)
        {
            return;
        }

        try
        {
            DateTimeOffset now = timestampUtc ?? _timeProvider.GetUtcNow();
            int onlinePlayers = chatContext.ClientChatSessions.Values.Count(static s => s is { IsSessionConnected: true });
            IEnumerable<MatchmakingGroup> groupSnapshot = groups ?? [.. Groups.Values];
            NEXUS.Contracts.CQRS.Telemetry.PopulationBreakdownResponseDto breakdown =
                PopulationBreakdownAggregator.Compute(
                    onlinePlayers,
                    chatContext.MatchServerChatSessions.Values,
                    groupSnapshot,
                    now);

            await telemetryPublisher.PublishPopulationBreakdownAsync(breakdown, forceImmediate).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Matchmaking][Population] Transient failure publishing population breakdown snapshot.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Matchmaking Service Is Stopping");

        await base.StopAsync(cancellationToken);

        Log.Information("Matchmaking Service Has Stopped");
    }

    public async Task SyncGroupStateAsync(MatchmakingGroup group, CancellationToken ct = default)
    {
        if (distributedStateStore is null || group is null)
        {
            return;
        }

        DistributedGroupSnapshot snapshot = new(
            GroupGuid: group.GUID.ToString("N"),
            LeaderAccountId: group.Leader?.Account.ID ?? 0,
            MemberAccountIds: [.. group.Members.Select(m => m.Account.ID)],
            MapName: group.Information.MapName ?? string.Empty,
            GroupType: (int)group.Information.GroupType,
            GameType: (int)group.Information.GameType,
            GameModes: [.. group.Information.GameModes],
            GameRegions: [.. group.Information.GameRegions],
            QueueStartTimeUtc: group.QueueStartTime,
            OwnerPodId: distributedStateStore.PodId,
            UpdatedAtUtc: _timeProvider.GetUtcNow(),
            Ranked: group.Information.Ranked,
            ClientVersion: group.Information.ClientVersion ?? string.Empty,
            MatchFidelity: group.Information.MatchFidelity,
            BotDifficulty: group.Information.BotDifficulty,
            RandomizeBots: group.Information.RandomizeBots,
            TeamSize: group.Information.TeamSize);

        _syncedGroupGuids[group.GUID] = 1;
        await distributedStateStore.UpsertGroupAsync(snapshot, ct).ConfigureAwait(false);
    }

    public async Task RemoveGroupStateAsync(Guid groupGuid, CancellationToken ct = default)
    {
        _syncedGroupGuids.TryRemove(groupGuid, out _);
        if (distributedStateStore is null)
        {
            return;
        }

        await distributedStateStore.RemoveGroupAsync(groupGuid.ToString("N"), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DistributedGroupSnapshot>> ListDistributedActiveGroupsAsync(
        CancellationToken ct = default)
    {
        return distributedStateStore is null
            ? Task.FromResult<IReadOnlyList<DistributedGroupSnapshot>>([])
            : distributedStateStore.ListActiveGroupsAsync(ct);
    }

    internal void NotifyGroupStateChanged(MatchmakingGroup group, string reason)
    {
        if (distributedStateStore is null || group is null)
        {
            return;
        }

        _syncedGroupGuids[group.GUID] = 1;
        DispatchDistributedStateBackground(
            () => SyncGroupStateAsync(group),
            $"SyncGroup({group.GUID:N}:{reason})");
    }

    internal void NotifyGroupStateRemoved(Guid groupGuid, string reason)
    {
        _syncedGroupGuids.TryRemove(groupGuid, out _);
        if (distributedStateStore is null)
        {
            return;
        }

        DispatchDistributedStateBackground(
            () => RemoveGroupStateAsync(groupGuid),
            $"RemoveGroup({groupGuid:N}:{reason})");
    }

    public Task<IReadOnlyList<DistributedMatchmakingStreamEvent>> GetRecentDistributedEventsAsync(
        int count = 50,
        CancellationToken ct = default)
    {
        return distributedStateStore is null
            ? Task.FromResult<IReadOnlyList<DistributedMatchmakingStreamEvent>>([])
            : distributedStateStore.ReadRecentEventsAsync(count, ct);
    }

    private void DispatchDistributedStateBackground(Func<Task> operation, string operationName)
    {
        if (distributedStateStore is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Matchmaking][DistributedState] Background operation {0} failed on Pod={1}",
                    operationName, distributedStateStore.PodId);
            }
        });
    }

    private static byte ResolveArrangedMatchType(ChatProtocol.TMMType groupType, ChatProtocol.TMMGameType gameType)
    {
        return groupType == ChatProtocol.TMMType.TMM_TYPE_COOP
            ? (byte)ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_BOTMATCH
            : (byte)(gameType switch
            {
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS => 4,
                _ => 10
            });
    }

    public async Task ConfirmMatch(int matchupId, int matchId, string serverAddress, int serverPort, string? localAddress = null, int? localPort = null)
    {
        if (!PendingMatches.TryRemove(matchupId, out PendingMatchState? matchState))
        {
            if (distributedStateStore is not null)
            {
                DistributedPendingMatchTransitionResult distConfirm = await distributedStateStore
                    .TryConfirmPendingMatchAsync(matchupId, matchId, serverAddress, serverPort)
                    .ConfigureAwait(false);

                if (distConfirm.Transitioned && distConfirm.Snapshot is not null)
                {
                    DistributedPendingMatchSnapshot snap = distConfirm.Snapshot;
                    bool failoverIsCoop = snap.GroupType == (int)ChatProtocol.TMMType.TMM_TYPE_COOP;
                    BotMatchMapSpec failoverCoopSpec = failoverIsCoop
                        ? BotMatchMapCatalog.ResolveOrFallback(snap.ResolvedMapName)
                        : BotMatchMapCatalog.Default;
                    string failoverMapName = failoverIsCoop
                        ? failoverCoopSpec.Id
                        : (string.IsNullOrWhiteSpace(snap.ResolvedMapName) ? "caldavar" : snap.ResolvedMapName);
                    int failoverTeamSize = snap.TeamSize > 0
                        ? snap.TeamSize
                        : (failoverIsCoop
                            ? failoverCoopSpec.TeamSize
                            : Math.Max(1, (int)Math.Ceiling(snap.MemberAccountIds.Count / 2.0)));
                    byte failoverMatchType = snap.MatchType != 0
                        ? snap.MatchType
                        : ResolveArrangedMatchType((ChatProtocol.TMMType)snap.GroupType, (ChatProtocol.TMMGameType)snap.GameType);
                    IReadOnlyList<string> failoverModes = snap.GameModes ?? [];
                    IReadOnlyList<string> failoverRegions = snap.GameRegions ?? [];

                    Log.Information(
                        "[Matchmaking][DistributedFailover] Pod={0} atomically confirmed cross-pod MatchUpID={1} -> MatchID={2} (OwnerPod={3}, MatchType={4}, Map={5}, Members={6})",
                        distributedStateStore.PodId,
                        matchupId,
                        matchId,
                        snap.OwnerPodId,
                        failoverMatchType,
                        failoverMapName,
                        snap.MemberAccountIds.Count);

                    if (snap.SlotLockKey != null && snap.SlotLeaseToken != null)
                    {
                        _ = slotLeaseService?.ReleaseSlotLeaseAsync(snap.SlotLockKey, snap.SlotLeaseToken);
                    }

                    // Clean up queue state for any groups hosted locally on this failover pod
                    HashSet<string> confirmedGroupGuids = new(snap.GroupGuids, StringComparer.OrdinalIgnoreCase);
                    foreach (MatchmakingGroup localGroup in Groups.Values)
                    {
                        if (confirmedGroupGuids.Contains(localGroup.GUID.ToString("N")) && localGroup.QueueStartTime is not null)
                        {
                            localGroup.QueueStartTime = null;
                            NotifyGroupStateChanged(localGroup, "failover_match_confirmed");
                        }
                    }

                    ChatBuffer failoverMatchFound = new();
                    failoverMatchFound.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE);
                    failoverMatchFound.WriteString(failoverMapName);
                    failoverMatchFound.WriteInt8((byte)failoverTeamSize);
                    failoverMatchFound.WriteInt8((byte)snap.GameType);
                    failoverMatchFound.WriteString(string.Join('|', failoverModes));
                    failoverMatchFound.WriteString(string.Join('|', failoverRegions));
                    failoverMatchFound.WriteString("");

                    ChatBuffer failoverGroupFoundServer = new();
                    failoverGroupFoundServer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
                    failoverGroupFoundServer.WriteInt8(16); // GroupFoundServer

                    DateTimeOffset failoverConfirmedAtUtc = _timeProvider.GetUtcNow();
                    List<int> remoteAccountIds = [];
                    int localDispatchCount = 0;

                    foreach (int memberAccountId in snap.MemberAccountIds)
                    {
                        ChatSession? memberSession = chatContext.ClientChatSessions.Values
                            .FirstOrDefault(s => s.Account?.ID == memberAccountId && s.IsSessionConnected);
                        if (memberSession is null)
                        {
                            remoteAccountIds.Add(memberAccountId);
                            continue;
                        }

                        IPAddress? clientIp = memberSession.GetRemoteClientIP();
                        (string targetAddress, int targetPort) = ResolveClientTargetAddress(
                            serverAddress, serverPort, localAddress, localPort, clientIp);

                        ChatBuffer autoConnect = new();
                        autoConnect.WriteCommand(0x0062);
                        autoConnect.WriteInt8(failoverMatchType);
                        autoConnect.WriteInt32(matchId);
                        autoConnect.WriteString(targetAddress);
                        autoConnect.WriteInt16((short)targetPort);
                        autoConnect.WriteInt32(Random.Shared.Next());

                        string fullServerAddress = $"{targetAddress}:{targetPort}";
                        memberSession.ClientMetadata.MatchID = matchId;
                        memberSession.ClientMetadata.MatchServerAddress = fullServerAddress;
                        memberSession.ClientMetadata.PendingRankedMatchId = matchId;
                        memberSession.ClientMetadata.PendingRankedServerAddress = fullServerAddress;
                        memberSession.ClientMetadata.ClientAckedMatchId = null;
                        memberSession.ClientMetadata.ClientAckedServerAddress = null;
                        memberSession.Send(failoverMatchFound);
                        memberSession.Send(failoverGroupFoundServer);
                        memberSession.Send(autoConnect);
                        memberSession.BroadcastConnectionStatusUpdate(
                            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME);

                        _pendingRankedConnects[(matchId, memberAccountId)] = new PendingRankedConnectEntry
                        {
                            Session = memberSession,
                            AccountId = memberAccountId,
                            PlayerName = memberSession.Account?.Name ?? memberAccountId.ToString(CultureInfo.InvariantCulture),
                            ClanTag = memberSession.Account?.Clan?.Tag,
                            MatchId = matchId,
                            MatchType = failoverMatchType,
                            TargetAddress = targetAddress,
                            TargetPort = targetPort,
                            PreviousArcadeServerId = memberSession.ClientMetadata.ActiveArcadeServerId ?? memberSession.ClientMetadata.PostArcadeVictoryServerId,
                            ConfirmedAtUtc = failoverConfirmedAtUtc,
                            LastSentAtUtc = failoverConfirmedAtUtc
                        };
                        localDispatchCount++;
                    }

                    if (remoteAccountIds.Count > 0)
                    {
                        MatchConfirmedDispatchPayload dispatchPayload = new(
                            MatchUpId: matchupId,
                            RealMatchId: matchId,
                            MatchType: failoverMatchType,
                            ServerAddress: serverAddress,
                            ServerPort: serverPort,
                            LocalAddress: localAddress,
                            LocalPort: localPort,
                            ResolvedMapName: failoverMapName,
                            GroupType: snap.GroupType,
                            GameType: snap.GameType,
                            TeamSize: failoverTeamSize,
                            GameModes: failoverModes,
                            GameRegions: failoverRegions,
                            TargetAccountIds: remoteAccountIds,
                            DispatchedByPod: distributedStateStore.PodId);

                        await distributedStateStore.PublishMatchConfirmedDispatchAsync(dispatchPayload)
                            .ConfigureAwait(false);

                        Log.Information(
                            "[Matchmaking][DistributedFailover] Pod={0} published MATCH_CONFIRMED_DISPATCH for MatchUpID={1} -> MatchID={2} (MatchType={3}, LocalDispatched={4}, RemoteTargets=[{5}])",
                            distributedStateStore.PodId,
                            matchupId,
                            matchId,
                            failoverMatchType,
                            localDispatchCount,
                            string.Join(",", remoteAccountIds));
                    }
                    return;
                }

                Log.Warning(
                    "[Matchmaking][DistributedFailover] Pod={0} skipped ConfirmMatch for MatchUpID={1} -> MatchID={2}: Valkey transition refused (FinalStatus={3}, Message={4})",
                    distributedStateStore.PodId,
                    matchupId,
                    matchId,
                    distConfirm.FinalStatus,
                    distConfirm.Message);
                return;
            }

            Log.Warning("Received ConfirmMatch for unknown MatchUpID: {0}", matchupId);
            return;
        }

        if (distributedStateStore is not null)
        {
            DistributedPendingMatchTransitionResult ownerConfirm = await distributedStateStore
                .TryConfirmPendingMatchAsync(matchupId, matchId, serverAddress, serverPort)
                .ConfigureAwait(false);

            if (!ownerConfirm.Transitioned)
            {
                Log.Warning(
                    "[Matchmaking][DistributedSync] Owner Pod={0} suppressed duplicate ConfirmMatch for MatchUpID={1} -> MatchID={2}: Valkey already transitioned (FinalStatus={3}, Message={4}). Preventing duplicate AUTO_MATCH_CONNECT dispatch.",
                    distributedStateStore.PodId,
                    matchupId,
                    matchId,
                    ownerConfirm.FinalStatus,
                    ownerConfirm.Message);
                return;
            }
        }

        if (matchState.SlotLockKey != null && matchState.SlotLeaseToken != null)
        {
            _ = slotLeaseService?.ReleaseSlotLeaseAsync(matchState.SlotLockKey, matchState.SlotLeaseToken);
        }

        List<MatchmakingGroup> groups = matchState.Groups;

        Log.Information("Match Confirmed! MatchUpID: {MatchUpID} -> MatchID: {MatchID}. Notifying Clients...",
            matchupId, matchId);

        MatchmakingGroup representativeGroup = groups.First();

        // 1. MATCH_FOUND_UPDATE
        // Calculate Dynamic Team Size again for client packet
        List<MatchmakingGroupMember> allPlayers = [.. groups.SelectMany(g => g.Members)];
        bool isCoop = representativeGroup.Information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP;
        BotMatchMapSpec coopConfirmSpec = isCoop
            ? BotMatchMapCatalog.ResolveOrFallback(matchState.ResolvedMapName ?? representativeGroup.Information.MapName)
            : BotMatchMapCatalog.Default;
        int calculatedTeamSize = isCoop
            ? coopConfirmSpec.TeamSize
            : Math.Max(1, (int)Math.Ceiling(allPlayers.Count / 2.0));

        ChatBuffer matchFound = new();
        matchFound.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE);
        matchFound.WriteString(isCoop ? coopConfirmSpec.Id : (matchState.ResolvedMapName ?? representativeGroup.Information.MapName));
        matchFound.WriteInt8((byte)calculatedTeamSize); // Team Size (Dynamic)
        matchFound.WriteInt8((byte)representativeGroup.Information.GameType);
        matchFound.WriteString(string.Join('|', representativeGroup.Information.GameModes));
        matchFound.WriteString(string.Join('|', representativeGroup.Information.GameRegions));
        matchFound.WriteString("");

        // 2. GROUP_QUEUE_UPDATE
        ChatBuffer groupFoundServer = new();
        groupFoundServer.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
        groupFoundServer.WriteInt8(16); // GroupFoundServer

        byte matchType = ResolveArrangedMatchType(
            representativeGroup.Information.GroupType,
            representativeGroup.Information.GameType);

        Dictionary<int, int> previousArcadeServers = new();
        foreach (MatchmakingGroupMember m in allPlayers)
        {
            if ((m.Session?.ClientMetadata?.ActiveArcadeServerId ?? m.Session?.ClientMetadata?.PostArcadeVictoryServerId) is int activeArcadeSrvId)
            {
                previousArcadeServers[m.Account.ID] = activeArcadeSrvId;
            }
        }

        _queueArcadeService?.OnPlayersPulledToRankedMatch(allPlayers, matchId);

        DateTimeOffset confirmedAtUtc = _timeProvider.GetUtcNow();
        List<int> ownerRemoteAccountIds = [];
        foreach (MatchmakingGroupMember member in groups.SelectMany(group => group.Members))
        {
            if (distributedStateStore is not null && !member.Session.IsSessionConnected)
            {
                ownerRemoteAccountIds.Add(member.Account.ID);
                continue;
            }

            IPAddress? clientIp = member.Session.GetRemoteClientIP();

            (string targetAddress, int targetPort) = ResolveClientTargetAddress(
                serverAddress, serverPort, localAddress, localPort, clientIp);

            Log.Debug(
                "[ChatRouting] TMM AUTO_MATCH_CONNECT target for '{AccountName}' ({ClientIp}): {RawServer}:{RawPort} -> {TargetAddress}:{TargetPort}",
                member.Account.Name,
                clientIp?.ToString() ?? "null",
                serverAddress,
                serverPort,
                targetAddress,
                targetPort);

            // 3. AUTO_MATCH_CONNECT per client
            ChatBuffer autoConnect = new();
            autoConnect.WriteCommand(0x0062);
            autoConnect.WriteInt8(matchType);
            autoConnect.WriteInt32(matchId); // The REAL MatchID from Game Server
            autoConnect.WriteString(targetAddress);
            autoConnect.WriteInt16((short)targetPort);
            autoConnect.WriteInt32(Random.Shared.Next());

            string fullServerAddress = $"{targetAddress}:{targetPort}";
            bool wasInArcadeGame = previousArcadeServers.ContainsKey(member.Account.ID) &&
                                   member.Session.ClientMetadata.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
            member.Session.ClientMetadata.MatchID = matchId;
            member.Session.ClientMetadata.MatchServerAddress = fullServerAddress;
            member.Session.ClientMetadata.PendingRankedMatchId = matchId;
            member.Session.ClientMetadata.PendingRankedServerAddress = fullServerAddress;
            member.Session.ClientMetadata.AwaitingArcadeDisconnectForRankedPull = wasInArcadeGame;
            member.Session.ClientMetadata.ClientAckedMatchId = null;
            member.Session.ClientMetadata.ClientAckedServerAddress = null;
            member.Session.Send(matchFound);
            member.Session.Send(groupFoundServer);
            member.Session.Send(autoConnect);
            member.Session.BroadcastConnectionStatusUpdate(
                ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME);

            _pendingRankedConnects[(matchId, member.Account.ID)] = new PendingRankedConnectEntry
            {
                Session = member.Session,
                AccountId = member.Account.ID,
                PlayerName = member.Account.Name,
                ClanTag = member.Account.Clan?.Tag,
                MatchId = matchId,
                MatchType = matchType,
                TargetAddress = targetAddress,
                TargetPort = targetPort,
                PreviousArcadeServerId = previousArcadeServers.TryGetValue(member.Account.ID, out int prevSrvId) ? prevSrvId : null,
                ConfirmedAtUtc = confirmedAtUtc,
                LastSentAtUtc = confirmedAtUtc
            };
        }

        // Check enemy banlist notifications
        List<MatchmakingGroupMember> team1Members = [.. matchState.Team1Groups.SelectMany(g => g.Members)];
        List<MatchmakingGroupMember> team2Members = [.. matchState.Team2Groups.SelectMany(g => g.Members)];

        foreach (MatchmakingGroupMember t1 in team1Members)
        {
            foreach (MatchmakingGroupMember t2 in team2Members)
            {
                if (t1.Account.HasBannedPeer(t2.Account.ID))
                {
                    ChatBuffer enemyBanNotice = new();
                    enemyBanNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                    enemyBanNotice.WriteString("System");
                    enemyBanNotice.WriteString($"[System] A player on your banlist ({t2.Account.Name}) is on the enemy team.");
                    t1.Session.Send(enemyBanNotice);
                }
            }
        }

        foreach (MatchmakingGroupMember t2 in team2Members)
        {
            foreach (MatchmakingGroupMember t1 in team1Members)
            {
                if (t2.Account.HasBannedPeer(t1.Account.ID))
                {
                    ChatBuffer enemyBanNotice = new();
                    enemyBanNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                    enemyBanNotice.WriteString("System");
                    enemyBanNotice.WriteString($"[System] A player on your banlist ({t1.Account.Name}) is on the enemy team.");
                    t2.Session.Send(enemyBanNotice);
                }
            }
        }

        if (distributedStateStore is not null && ownerRemoteAccountIds.Count > 0)
        {
            string effectiveOwnerMap = isCoop ? coopConfirmSpec.Id : (matchState.ResolvedMapName ?? representativeGroup.Information.MapName);
            await distributedStateStore.PublishMatchConfirmedDispatchAsync(new MatchConfirmedDispatchPayload(
                MatchUpId: matchupId,
                RealMatchId: matchId,
                MatchType: matchType,
                ServerAddress: serverAddress,
                ServerPort: serverPort,
                LocalAddress: localAddress,
                LocalPort: localPort,
                ResolvedMapName: effectiveOwnerMap,
                GroupType: (int)representativeGroup.Information.GroupType,
                GameType: (int)representativeGroup.Information.GameType,
                TeamSize: calculatedTeamSize,
                GameModes: [.. representativeGroup.Information.GameModes],
                GameRegions: [.. representativeGroup.Information.GameRegions],
                TargetAccountIds: ownerRemoteAccountIds,
                DispatchedByPod: distributedStateStore.PodId)).ConfigureAwait(false);

            Log.Information(
                "[Matchmaking][DistributedSync] Owner Pod={0} published MATCH_CONFIRMED_DISPATCH for {1} disconnected/cross-pod player(s) in MatchID={2}: [{3}]",
                distributedStateStore.PodId,
                ownerRemoteAccountIds.Count,
                matchId,
                string.Join(",", ownerRemoteAccountIds));
        }

        Log.Information("Clients Notified for MatchID {MatchID}", matchId);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Sweeps pending matches that have waited more than 30 seconds for server confirmation without receiving
    /// NET_CHAT_GS_ANNOUNCE_MATCH. Restitutes trapped player groups back into the queue and resets the server to IDLE.
    /// </summary>
    internal void SweepExpiredPendingMatches(DateTimeOffset now)
    {
        if (PendingMatches.IsEmpty) return;

        foreach (KeyValuePair<int, PendingMatchState> kvp in PendingMatches)
        {
            int matchUpId = kvp.Key;
            PendingMatchState state = kvp.Value;

            if ((now - state.CreatedAt).TotalSeconds > 30.0 && PendingMatches.TryRemove(matchUpId, out PendingMatchState? expiredState))
            {
                Log.Warning(
                    "[Matchmaking] MatchUp {MatchUpID} timed out waiting for server confirmation (>30s) on Server ID {ServerID}. Restituting {Count} groups to active queue.",
                    matchUpId, expiredState.AllocatedServerID, expiredState.Groups.Count);

                if (expiredState.SlotLockKey != null && expiredState.SlotLeaseToken != null)
                {
                    _ = slotLeaseService?.ReleaseSlotLeaseAsync(expiredState.SlotLockKey, expiredState.SlotLeaseToken);
                }

                DispatchDistributedStateBackground(
                    () => distributedStateStore!.TryExpirePendingMatchAsync(matchUpId),
                    $"ExpirePendingMatch({matchUpId})");

                // Reset allocated server back to IDLE if still connected and in LOADING state
                if (chatContext.MatchServerChatSessions.TryGetValue(expiredState.AllocatedServerID, out ChatSession? serverSession))
                {
                    if (serverSession.ServerMetadata?.Status == ChatProtocol.ServerStatus.SERVER_STATUS_LOADING)
                    {
                        serverSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
                        Log.Information("[Matchmaking] Reset Server ID {ServerID} status back to IDLE after match allocation timeout.", expiredState.AllocatedServerID);
                    }
                }

                // Restitute player groups into the active queue
                foreach (MatchmakingGroup group in expiredState.Groups)
                {
                    group.QueueStartTime = now;
                    NotifyGroupStateChanged(group, "pending_match_expired_restituted");

                    ChatBuffer queueHeartbeat = new();
                    queueHeartbeat.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
                    queueHeartbeat.WriteInt8(Convert.ToByte(ChatProtocol.TMMUpdateType.TMM_GROUP_QUEUE_UPDATE, System.Globalization.CultureInfo.InvariantCulture));
                    queueHeartbeat.WriteInt32(0);

                    foreach (MatchmakingGroupMember member in group.Members)
                    {
                        try
                        {
                            member.Session.Send(queueHeartbeat);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Failed to send queue restitution update to member {MemberID}", member.Account.ID);
                        }
                    }
                }
            }
        }
    }

    internal void SweepPendingRankedConnects(DateTimeOffset now)
    {
        if (_pendingRankedConnects.IsEmpty) return;

        double retryIntervalSeconds = configuration.GetValue("Matchmaking:AutoConnectRetryIntervalSeconds", 1.5);
        double maxRetryWindowSeconds = configuration.GetValue("Matchmaking:AutoConnectRetryWindowSeconds", 15.0);
        double arcadeKickFallbackSeconds = configuration.GetValue("Matchmaking:ArcadePullKickFallbackSeconds", 2.5);

        foreach (KeyValuePair<(int MatchId, int AccountId), PendingRankedConnectEntry> kvp in _pendingRankedConnects)
        {
            PendingRankedConnectEntry entry = kvp.Value;
            if (!entry.Session.IsSessionConnected || entry.Session.ClientMetadata is null)
            {
                _pendingRankedConnects.TryRemove(kvp.Key, out _);
                continue;
            }

            ClientChatSessionMetadata meta = entry.Session.ClientMetadata;
            string expectedEndpoint = $"{entry.TargetAddress}:{entry.TargetPort}";
            bool clientAcked =
                meta.ClientAckedMatchId == entry.MatchId ||
                (!string.IsNullOrEmpty(meta.ClientAckedServerAddress) &&
                 string.Equals(meta.ClientAckedServerAddress, expectedEndpoint, StringComparison.OrdinalIgnoreCase));

            if (clientAcked)
            {
                if (_pendingRankedConnects.TryRemove(kvp.Key, out _))
                {
                    meta.PendingRankedMatchId = null;
                    meta.PendingRankedServerAddress = null;
                    Log.Information(
                        "[Matchmaking] [ConnectAck] AccountID={AccountID} ('{AccountName}') acknowledged Ranked Match #{MatchID} ({Endpoint}).",
                        entry.AccountId,
                        entry.PlayerName,
                        entry.MatchId,
                        expectedEndpoint);
                }
                continue;
            }

            double totalElapsedSeconds = (now - entry.ConfirmedAtUtc).TotalSeconds;
            if (totalElapsedSeconds >= maxRetryWindowSeconds)
            {
                if (_pendingRankedConnects.TryRemove(kvp.Key, out _))
                {
                    meta.PendingRankedMatchId = null;
                    meta.PendingRankedServerAddress = null;
                    Log.Warning(
                        "[Matchmaking] [ConnectRetryExpired] AccountID={AccountID} ('{AccountName}') did not acknowledge Ranked Match #{MatchID} within {WindowSec:F1}s.",
                        entry.AccountId,
                        entry.PlayerName,
                        entry.MatchId,
                        maxRetryWindowSeconds);
                }
                continue;
            }

            if (!entry.ArcadeKickSent &&
                entry.PreviousArcadeServerId is int arcadeServerId &&
                totalElapsedSeconds >= arcadeKickFallbackSeconds)
            {
                entry.ArcadeKickSent = true;
                if (meta.AwaitingArcadeDisconnectForRankedPull &&
                    chatContext.MatchServerChatSessions.TryGetValue(arcadeServerId, out ChatSession? arcadeServerSession) &&
                    arcadeServerSession.IsSessionConnected &&
                    arcadeServerSession.ServerMetadata is not null)
                {
                    string rawPlain = entry.PlayerName ?? string.Empty;
                    string? clanTag = entry.ClanTag ?? entry.Session.Account?.Clan?.Tag;
                    string rawWithTag = string.IsNullOrWhiteSpace(clanTag) ? rawPlain : $"[{clanTag.Trim()}]{rawPlain}";
                    string safePlain = SanitizeRemoteConsoleToken(rawPlain);
                    string safeWithTag = SanitizeRemoteConsoleToken(rawWithTag);
                    if (!string.IsNullOrEmpty(safePlain))
                    {
                        string kickCmd = string.Equals(safeWithTag, safePlain, StringComparison.OrdinalIgnoreCase) || safeWithTag.Length == 0
                            ? $"Kick \"{safePlain}\""
                            : $"Kick \"{safeWithTag}\";Kick \"{safePlain}\"";

                        ChatBuffer kickPacket = new();
                        kickPacket.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);
                        kickPacket.WriteString(arcadeServerSession.ServerMetadata.SessionCookie ?? string.Empty);
                        kickPacket.WriteString(kickCmd);
                        arcadeServerSession.Send(kickPacket);

                        Log.Warning(
                            "[Matchmaking] [ArcadePullKickFallback] AccountID={AccountID} ('{AccountName}', ClanTag='{ClanTag}', KickCmd='{KickCmd}') remained IN_GAME on Arcade ServerID={ArcadeServerID} {ElapsedSec:F1}s after Ranked Match #{MatchID} confirmed; dispatched fallback Kick.",
                            entry.AccountId,
                            safePlain,
                            clanTag ?? string.Empty,
                            kickCmd,
                            arcadeServerId,
                            totalElapsedSeconds,
                            entry.MatchId);
                    }
                }
            }

            double sinceLastSendSeconds = (now - entry.LastSentAtUtc).TotalSeconds;
            if (retryIntervalSeconds > 0.0 && sinceLastSendSeconds >= retryIntervalSeconds)
            {
                entry.LastSentAtUtc = now;
                meta.MatchID = entry.MatchId;
                meta.MatchServerAddress = expectedEndpoint;
                meta.PendingRankedMatchId = entry.MatchId;
                meta.PendingRankedServerAddress = expectedEndpoint;

                ChatBuffer retryConnect = new();
                retryConnect.WriteCommand(0x0062);
                retryConnect.WriteInt8(entry.MatchType);
                retryConnect.WriteInt32(entry.MatchId);
                retryConnect.WriteString(entry.TargetAddress);
                retryConnect.WriteInt16((short)entry.TargetPort);
                retryConnect.WriteInt32(Random.Shared.Next());
                entry.Session.Send(retryConnect);

                Log.Information(
                    "[Matchmaking] [ConnectRetry] Re-sent Ranked 0x0062 AutoMatchConnect to AccountID={AccountID} ('{AccountName}') for Match #{MatchID} ({Endpoint}, Elapsed={ElapsedSec:F1}s, ClientState={ClientState}).",
                    entry.AccountId,
                    entry.PlayerName,
                    entry.MatchId,
                    expectedEndpoint,
                    totalElapsedSeconds,
                    meta.LastKnownClientState);
            }
        }
    }

    private static string SanitizeRemoteConsoleToken(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        System.Text.StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            if (c is not ('"' or ';' or '\r' or '\n' or '\\'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static (string Address, int Port) ResolveClientTargetAddress(
        string serverAddress,
        int serverPort,
        string? localAddress,
        int? localPort,
        IPAddress? clientIp)
    {
        return SplitHorizonEndpointResolver.ResolveGameServerEndpoint(
            serverAddress,
            serverPort,
            localAddress,
            localPort,
            clientIp);
    }

    public static bool IsPrivateNetwork(string ipString) =>
        SplitHorizonEndpointResolver.IsPrivateOrVpnIPString(ipString);

    public static bool IsPrivateNetwork(IPAddress ip) =>
        SplitHorizonEndpointResolver.IsPrivateOrVpnIP(ip);

    public static bool IsLoopbackOrLocalhost(string? address) =>
        SplitHorizonEndpointResolver.IsLoopbackOrLocalhost(address);

    public override void Dispose()
    {
        base.Dispose();

        GC.SuppressFinalize(this);
    }

    public int ResolveEffectivePlayersPerTeam()
    {
        if (_cachedRedisPlayersPerTeam is > 0 and <= 5)
        {
            return _cachedRedisPlayersPerTeam.Value;
        }

        int? configuredPlayersPerTeam = configuration.GetValue<int?>("Matchmaking:PlayersPerTeam");
        int configuredTeamSize = configuredPlayersPerTeam ??
                                 SharedConfiguration.MatchmakingConfiguration?.Ranked?.Match?.TeamSize ??
                                 5;
        return configuredTeamSize > 0 ? configuredTeamSize : 5;
    }

    public bool ResolveAllowTwoPlayerFallback() =>
        _cachedRedisAllowTwoPlayerFallback ?? configuration.GetValue("Matchmaking:AllowTwoPlayerFallback", false);

    public int ResolveTargetPlayersForBucket(ChatProtocol.TMMGameType gameType, int candidateCount)
    {
        bool isQuickmatchCasual = gameType is ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL
            or ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL;
        if (isQuickmatchCasual)
        {
            return 10;
        }

        int configuredTeamSize = ResolveEffectivePlayersPerTeam();
        bool allowTwoPlayerFallback = ResolveAllowTwoPlayerFallback();
        return (allowTwoPlayerFallback && candidateCount == 2) || configuredTeamSize == 1
            ? 2
            : configuredTeamSize * 2;
    }

    internal async ValueTask RefreshRuntimeMatchmakingOverridesAsync(DateTimeOffset now)
    {
        if (distributedCache is null)
        {
            return;
        }

        if (_lastRedisRuntimePollUtc.HasValue && (now - _lastRedisRuntimePollUtc.Value).TotalSeconds < 2.0)
        {
            return;
        }

        _lastRedisRuntimePollUtc = now;
        try
        {
            StackExchange.Redis.RedisValue rawPlayersPerTeam = await distributedCache.StringGetAsync(RedisPlayersPerTeamKey).ConfigureAwait(false);
            int? nextPlayersPerTeam = null;
            if (rawPlayersPerTeam.HasValue &&
                int.TryParse(rawPlayersPerTeam.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTeamSize) &&
                parsedTeamSize is >= 1 and <= 5)
            {
                nextPlayersPerTeam = parsedTeamSize;
            }

            if (_cachedRedisPlayersPerTeam != nextPlayersPerTeam)
            {
                int? previousValue = _cachedRedisPlayersPerTeam;
                _cachedRedisPlayersPerTeam = nextPlayersPerTeam;
                Log.Information(
                    "[Matchmaking] [RuntimeOverride] Synchronized Redis key '{RedisKey}': {Previous} -> {Next} (EffectivePlayersPerTeam={Effective})",
                    RedisPlayersPerTeamKey,
                    previousValue?.ToString(CultureInfo.InvariantCulture) ?? "<config>",
                    nextPlayersPerTeam?.ToString(CultureInfo.InvariantCulture) ?? "<config>",
                    ResolveEffectivePlayersPerTeam());
            }

            StackExchange.Redis.RedisValue rawTwoPlayerFallback = await distributedCache.StringGetAsync(RedisAllowTwoPlayerFallbackKey).ConfigureAwait(false);
            bool? nextTwoPlayerFallback = null;
            if (rawTwoPlayerFallback.HasValue)
            {
                string rawText = rawTwoPlayerFallback.ToString().Trim();
                if (bool.TryParse(rawText, out bool parsedBool))
                {
                    nextTwoPlayerFallback = parsedBool;
                }
                else if (rawText == "1")
                {
                    nextTwoPlayerFallback = true;
                }
                else if (rawText == "0")
                {
                    nextTwoPlayerFallback = false;
                }
            }

            if (_cachedRedisAllowTwoPlayerFallback != nextTwoPlayerFallback)
            {
                bool? previousFlag = _cachedRedisAllowTwoPlayerFallback;
                _cachedRedisAllowTwoPlayerFallback = nextTwoPlayerFallback;
                Log.Information(
                    "[Matchmaking] [RuntimeOverride] Synchronized Redis key '{RedisKey}': {Previous} -> {Next} (EffectiveAllowTwoPlayerFallback={Effective})",
                    RedisAllowTwoPlayerFallbackKey,
                    previousFlag?.ToString() ?? "<config>",
                    nextTwoPlayerFallback?.ToString() ?? "<config>",
                    ResolveAllowTwoPlayerFallback());
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                "[Matchmaking] [RuntimeOverride] Failed reading Redis matchmaking overrides ({Error}); preserving EffectivePlayersPerTeam={EffectiveTeamSize}, AllowTwoPlayerFallback={AllowFallback}",
                ex.Message,
                ResolveEffectivePlayersPerTeam(),
                ResolveAllowTwoPlayerFallback());
        }
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        Log.Information("Matchmaking Service Is Starting");

        await ObserveDistributedGroupsOnStartupAsync(stoppingToken).ConfigureAwait(false);
        await RunMatchBroker(stoppingToken);

        Log.Information("Matchmaking Service Has Started");
    }

    private async Task ObserveDistributedGroupsOnStartupAsync(CancellationToken stoppingToken)
    {
        if (distributedStateStore is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<DistributedGroupSnapshot> activeGroups = await distributedStateStore
                .ListActiveGroupsAsync(stoppingToken)
                .ConfigureAwait(false);

            int queuedCount = activeGroups.Count(g => g.QueueStartTimeUtc.HasValue);
            if (!distributedStateStore.IsDistributedActive)
            {
                Log.Information(
                    "[Matchmaking][DistributedRecovery] Pod={0} running in single-pod local matchmaking state mode (ActiveGroups={1}, Queued={2}).",
                    distributedStateStore.PodId,
                    activeGroups.Count,
                    queuedCount);
                return;
            }

            Log.Information(
                "[Matchmaking][DistributedRecovery] Pod={0} observed {1} active group snapshot(s) in Valkey on startup (Queued={2}).",
                distributedStateStore.PodId,
                activeGroups.Count,
                queuedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[Matchmaking][DistributedRecovery] Pod={0} could not list active Valkey groups during startup; continuing match broker loop.",
                distributedStateStore.PodId);
        }
    }

    private async Task RunMatchBroker(CancellationToken cancellationToken)
    {
        int interval = configuration.GetValue("Matchmaking:IntervalInMilliseconds", 1000);
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(Math.Max(10, interval)), _timeProvider);

        try
        {
            while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
                try
                {
                    // Match Broker Logic

                    #region Match Broker Logic

                    // 1. Snapshot all active groups from the concurrent dictionary
                    List<MatchmakingGroup> allGroups = [.. Groups.Values];
                    if (distributedStateStore is not null)
                    {
                        HashSet<Guid> currentGuids = new(allGroups.Count);
                        foreach (MatchmakingGroup g in allGroups)
                        {
                            currentGuids.Add(g.GUID);
                            g.BindMatchmakingService(this);
                            if (_syncedGroupGuids.TryAdd(g.GUID, 1))
                            {
                                NotifyGroupStateChanged(g, "broker_discovered");
                            }
                        }

                        foreach (Guid trackedGuid in _syncedGroupGuids.Keys)
                        {
                            if (!currentGuids.Contains(trackedGuid))
                            {
                                NotifyGroupStateRemoved(trackedGuid, "broker_reconcile_removed");
                            }
                        }
                    }

                    // Record active matchmaking queues count for telemetry
                    int queuedGroupsCount = allGroups.Count(g => g.QueueStartTime != null);
                    _ = telemetryPublisher?.RecordQueueStateAsync(queuedGroupsCount);

                    // 1a. Sweep timed-out pending matches (>30s), refresh live Redis overrides, and retry unacknowledged Ranked 0x0062 connects
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    await RefreshRuntimeMatchmakingOverridesAsync(now).ConfigureAwait(false);
                    SweepExpiredPendingMatches(now);
                    SweepPendingRankedConnects(now);
                    if (configuration.GetValue("Matchmaking:EnableReadyCheck", false))
                    {
                        EvaluateExpiredReadyChecks(now);
                    }
                    foreach (MatchmakingGroup queuedGroup in allGroups.Where(g => g.QueueStartTime != null))
                    {
                        int elapsedSeconds = (int)Math.Max(0.0, (now - queuedGroup.QueueStartTime!.Value).TotalSeconds);
                        ChatBuffer queueHeartbeat = new();
                        queueHeartbeat.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
                        queueHeartbeat.WriteInt8(Convert.ToByte(ChatProtocol.TMMUpdateType.TMM_GROUP_QUEUE_UPDATE, System.Globalization.CultureInfo.InvariantCulture));
                        queueHeartbeat.WriteInt32(elapsedSeconds);

                        foreach (MatchmakingGroupMember member in queuedGroup.Members)
                        {
                            member.Session.Send(queueHeartbeat);
                        }
                    }

                    // 1b. Process Bot Match (Co-Op) groups immediately
                    List<MatchmakingGroup> queuedCoopGroups = allGroups
                        .Where(g => g.QueueStartTime != null &&
                                    g.Information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP)
                        .OrderBy(g => g.QueueStartTime)
                        .ToList();

                    foreach (MatchmakingGroup coopGroup in queuedCoopGroups)
                    {
                        Log.Information("Processing Co-Op Bot Match immediately for Group {GroupGUID} ({PlayerCount} players)",
                            coopGroup.GUID, coopGroup.Members.Count);
                        await ProcessMatchUnsafe([coopGroup]);
                    }

                    // 2. Define our "Bucket" key
                    // A bucket is defined by (Region, GameType, Map).
                    // Since groups can select multiple regions and modes, we need to iterate and place them into potentially multiple buckets.
                    // For simplicity in this iteration: We will group by the FIRST valid permutation found, or just iterate permutations.
                    // Better approach:
                    // Iterate all groups. For each group, iterate their selected regions and game modes.
                    // Add the group to a list for that specific (Region, Mode, Map) tuple.
                    Dictionary<(string Region, ChatProtocol.TMMGameType GameType, string Map), List<MatchmakingGroup>>
                        buckets =
                            [];

                    foreach (MatchmakingGroup group in allGroups)
                    {
                        // Skip if not queued or if Co-Op (handled above)
                        if (group.QueueStartTime == null ||
                            group.Information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP || IsGroupInPendingReadyCheck(group.GUID)) continue;

                        // Expand the group into all eligible buckets
                        foreach (string region in group.Information.GameRegions)
                        foreach (string modeStr in group.Information.GameModes)
                        {
                            // Parse mode string back to enum if necessary, or simplify if we assume standard modes.
                            // For now, let's use the Group's primary GameType if it aligns, or just use the primary keys.
                            // The 'Information.GameType' is usually the authority on what they queued for (e.g. Normal, Casual).
                            (string Region, ChatProtocol.TMMGameType GameType, string Map) key = (Region: region,
                                group.Information.GameType, Map: group.Information.MapName);

                            if (!buckets.TryGetValue(key, out List<MatchmakingGroup>? value))
                            {
                                value = [];
                                buckets[key] = value;
                            }

                            // Avoid adding the same group multiple times to the same bucket (though the set should be unique by key)
                            if (!value.Contains(group)) value.Add(group);
                        }
                    }

                    // 3. Process each bucket
                    double initialSpread = configuration.GetValue("Matchmaking:InitialRatingSpread", 50.0);
                    double expansionRate = configuration.GetValue("Matchmaking:ExpansionRatePerTenSec", 6.9444);
                    double maxSpread = configuration.GetValue("Matchmaking:MaxRatingSpread", 175.0);
                    now = _timeProvider.GetUtcNow();
                    HashSet<Guid> matchedGroupGuids = [];

                    foreach (((string Region, ChatProtocol.TMMGameType GameType, string Map) bucketKey,
                                 List<MatchmakingGroup> candidates) in buckets)
                    {
                        int playersPerMatch = ResolveTargetPlayersForBucket(bucketKey.GameType, candidates.Count);

                        // Debug Bucket State
                        if (candidates.Count > 0)
                            Log.Information(
                                "Matchmaking Bucket State: Region={Region}, Map={Map}, Mode={Mode}, Count={Count}, TargetPlayers={TargetPlayers}",
                                bucketKey.Region, bucketKey.Map, bucketKey.GameType, candidates.Count, playersPerMatch);

                        // Sort candidates by Queue Duration (Longest first) to prioritize older waiters
                        List<MatchmakingGroup> sortedCandidates = [.. candidates.OrderBy(g => g.QueueStartTime)];

                        for (int i = 0; i < sortedCandidates.Count; i++)
                        {
                            MatchmakingGroup anchorGroup = sortedCandidates[i];
                            if (matchedGroupGuids.Contains(anchorGroup.GUID) || anchorGroup.QueueStartTime == null)
                                continue;

                            List<MatchmakingGroup> potentialMatch = [anchorGroup];
                            int currentCount = anchorGroup.Members.Count;

                            if (currentCount == playersPerMatch)
                            {
                                if (await ProcessMatchUnsafe(potentialMatch))
                                {
                                    matchedGroupGuids.Add(anchorGroup.GUID);
                                    break;
                                }
                            }

                            for (int j = 0; j < sortedCandidates.Count; j++)
                            {
                                if (i == j) continue;

                                MatchmakingGroup candidate = sortedCandidates[j];
                                if (matchedGroupGuids.Contains(candidate.GUID) || candidate.QueueStartTime == null)
                                    continue;

                                if (currentCount + candidate.Members.Count <= playersPerMatch &&
                                    CanCombineGroupsWithinSpread(potentialMatch, candidate, now, initialSpread,
                                        expansionRate,
                                        maxSpread) &&
                                    CanCombineGroupsWithoutInternalBanConflict(potentialMatch, candidate, playersPerMatch / 2))
                                {
                                    potentialMatch.Add(candidate);
                                    currentCount += candidate.Members.Count;
                                }

                                if (currentCount == playersPerMatch)
                                {
                                    if (await ProcessMatchUnsafe(potentialMatch))
                                    {
                                        foreach (MatchmakingGroup g in potentialMatch)
                                            matchedGroupGuids.Add(g.GUID);
                                        break;
                                    }
                                }
                            }

                            if (currentCount == playersPerMatch && matchedGroupGuids.Contains(anchorGroup.GUID)) break;
                        }
                    }

                    // 4. Publish population breakdown snapshot (throttled 5s when idle, immediate on server/queue changes)
                    _ = PublishPopulationBreakdownSnapshotAsync(allGroups, _timeProvider.GetUtcNow());

                    #endregion
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[MatchBroker] Transient error during matchmaking tick; continuing loop");
                }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }

    private bool IsGroupInPendingReadyCheck(Guid groupGuid)
    {
        foreach (PendingReadyCheckState rc in _pendingReadyChecks.Values)
        {
            if (!rc.Resolved && rc.Groups.Any(g => g.GUID == groupGuid))
            {
                return true;
            }
        }
        return false;
    }

    public PendingReadyCheckState? StartReadyCheck(IReadOnlyList<MatchmakingGroup> groups)
    {
        if (groups == null || groups.Count == 0)
        {
            return null;
        }

        if (distributedStateStore is not null)
        {
            Log.Error(
                "[Matchmaking][ReadyCheck] Synchronous StartReadyCheck called on Pod={0} with distributedStateStore configured; use StartReadyCheckAsync so distributed registration completes before prompting clients.",
                distributedStateStore.PodId);
            throw new InvalidOperationException(
                "Use StartReadyCheckAsync when IDistributedMatchmakingStateStore is configured to guarantee distributed registration before prompting clients.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        double timeoutSeconds = configuration.GetValue("Matchmaking:ReadyCheckTimeoutSeconds", 15.0);
        int readyCheckId = Interlocked.Increment(ref _nextReadyCheckId);

        PendingReadyCheckState state = CreateLocalReadyCheckState(groups, readyCheckId, now, timeoutSeconds, out string resolvedMap, out string resolvedMode);
        _pendingReadyChecks[readyCheckId] = state;
        DispatchReadyCheckPrompts(state, groups, timeoutSeconds, resolvedMap, resolvedMode);
        return state;
    }

    public async Task<PendingReadyCheckState?> StartReadyCheckAsync(
        IReadOnlyList<MatchmakingGroup> groups,
        CancellationToken ct = default)
    {
        if (groups == null || groups.Count == 0)
        {
            return null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        double timeoutSeconds = configuration.GetValue("Matchmaking:ReadyCheckTimeoutSeconds", 15.0);
        int readyCheckId = 0;
        PendingReadyCheckState? state = null;
        string resolvedMap = string.Empty;
        string resolvedMode = string.Empty;

        if (distributedStateStore is not null)
        {
            const int maxAttempts = 3;
            TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(30.0, timeoutSeconds + 30.0));
            bool registered = false;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    readyCheckId = await distributedStateStore.AllocateNextReadyCheckIdAsync(ct).ConfigureAwait(false);
                    state = CreateLocalReadyCheckState(groups, readyCheckId, now, timeoutSeconds, out resolvedMap, out resolvedMode);
                    DistributedReadyCheckSnapshot rcSnap = BuildDistributedReadyCheckSnapshot(state, groups, distributedStateStore.PodId);

                    registered = await distributedStateStore.TryCreateReadyCheckAsync(rcSnap, ttl, ct).ConfigureAwait(false);
                    if (registered)
                    {
                        break;
                    }

                    Log.Warning(
                        "[Matchmaking][ReadyCheck] Distributed TryCreateReadyCheckAsync returned false for ReadyCheckId={0} on Pod={1} (Attempt={2}/{3}); retrying distributed ID allocation.",
                        readyCheckId,
                        distributedStateStore.PodId,
                        attempt,
                        maxAttempts);
                }
                catch (DistributedMatchmakingStoreException ex)
                {
                    if (ex.FailureKind == DistributedMatchmakingStoreFailureKind.ClusterCrossSlotMisconfiguration)
                    {
                        Log.Error(
                            ex,
                            "[Matchmaking][ReadyCheck] CRITICAL Valkey Cluster CROSSSLOT misconfiguration on Pod={0}. Verify ValkeyMatchmakingStateStore keyPrefix includes a shared {{...}} hash tag.",
                            distributedStateStore.PodId);
                    }
                    else
                    {
                        Log.Error(
                            ex,
                            "[Matchmaking][ReadyCheck] Distributed store error during StartReadyCheckAsync on Pod={0} (FailureKind={1}); aborting ReadyCheck before prompting clients.",
                            distributedStateStore.PodId,
                            ex.FailureKind);
                    }
                    return null;
                }
            }

            if (!registered || state is null)
            {
                Log.Error(
                    "[Matchmaking][ReadyCheck] Aborting ReadyCheck launch on Pod={0} after {1} failed distributed registration attempts (LastReadyCheckId={2}); zero clients prompted.",
                    distributedStateStore.PodId,
                    maxAttempts,
                    readyCheckId);
                return null;
            }
        }
        else
        {
            readyCheckId = Interlocked.Increment(ref _nextReadyCheckId);
            state = CreateLocalReadyCheckState(groups, readyCheckId, now, timeoutSeconds, out resolvedMap, out resolvedMode);
        }

        _pendingReadyChecks[readyCheckId] = state;
        DispatchReadyCheckPrompts(state, groups, timeoutSeconds, resolvedMap, resolvedMode);
        return state;
    }

    private PendingReadyCheckState CreateLocalReadyCheckState(
        IReadOnlyList<MatchmakingGroup> groups,
        int readyCheckId,
        DateTimeOffset now,
        double timeoutSeconds,
        out string resolvedMap,
        out string resolvedMode)
    {
        HashSet<int> requiredAccountIds = groups
            .SelectMany(g => g.Members)
            .Select(m => m.Account.ID)
            .ToHashSet();

        Dictionary<int, DateTimeOffset> originalQueueTimes = new();
        foreach (MatchmakingGroup g in groups)
        {
            if (g.QueueStartTime.HasValue && g.Members.Count > 0)
            {
                originalQueueTimes[g.Members[0].Account.ID] = g.QueueStartTime.Value;
            }
        }

        MatchmakingGroup representative = groups[0];
        resolvedMap = SeasonalMapResolver.ResolveMatchmakingMap(
            representative.Information.MapName,
            representative.Information.GroupType,
            representative.Information.GameType,
            configuration,
            _timeProvider);
        resolvedMode = representative.Information.GameModes is { Length: > 0 } m ? m[0] : "All Pick";

        return new PendingReadyCheckState
        {
            ReadyCheckId = readyCheckId,
            Groups = [.. groups],
            RequiredAccountIds = requiredAccountIds,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(timeoutSeconds),
            MapName = resolvedMap,
            GameMode = resolvedMode,
            OriginalQueueStartTimes = originalQueueTimes
        };
    }

    private static DistributedReadyCheckSnapshot BuildDistributedReadyCheckSnapshot(
        PendingReadyCheckState state,
        IReadOnlyList<MatchmakingGroup> groups,
        string podId)
    {
        MatchmakingGroup representative = groups[0];
        List<DistributedReadyCheckGroupMembership> memberGroups = new(groups.Count);
        foreach (MatchmakingGroup g in groups)
        {
            memberGroups.Add(new DistributedReadyCheckGroupMembership(
                GroupGuid: g.GUID.ToString("N"),
                MemberAccountIds: [.. g.Members.Select(m => m.Account.ID)],
                OriginalQueueStartTimeUtc: g.QueueStartTime));
        }

        return new DistributedReadyCheckSnapshot(
            ReadyCheckId: state.ReadyCheckId,
            GroupGuids: [.. groups.Select(g => g.GUID.ToString("N"))],
            RequiredAccountIds: [.. state.RequiredAccountIds.OrderBy(id => id)],
            AcceptedAccountIds: [],
            DeclinedByAccountId: null,
            Status: "OPEN",
            OwnerPodId: podId,
            CreatedAtUtc: state.CreatedAtUtc,
            ExpiresAtUtc: state.ExpiresAtUtc,
            MapName: state.MapName,
            GameMode: state.GameMode,
            GroupType: (int)representative.Information.GroupType,
            GameType: (int)representative.Information.GameType,
            GameModes: [.. representative.Information.GameModes],
            GameRegions: [.. representative.Information.GameRegions],
            Ranked: representative.Information.Ranked,
            ClientVersion: representative.Information.ClientVersion ?? string.Empty,
            MatchFidelity: representative.Information.MatchFidelity,
            BotDifficulty: representative.Information.BotDifficulty,
            RandomizeBots: representative.Information.RandomizeBots,
            RequiredMemberGroups: memberGroups);
    }

    private void DispatchReadyCheckPrompts(
        PendingReadyCheckState state,
        IReadOnlyList<MatchmakingGroup> groups,
        double timeoutSeconds,
        string resolvedMap,
        string resolvedMode)
    {
        int readyCheckId = state.ReadyCheckId;

        ChatBuffer promptPacket = new();
        promptPacket.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_PROMPT);
        promptPacket.WriteInt32(readyCheckId);
        promptPacket.WriteFloat32((float)timeoutSeconds);
        promptPacket.WriteInt32(state.TotalPlayers);
        promptPacket.WriteString(resolvedMap);
        promptPacket.WriteString(resolvedMode);

        ChatBuffer initialStatusPacket = new();
        initialStatusPacket.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_STATUS);
        initialStatusPacket.WriteInt32(readyCheckId);
        initialStatusPacket.WriteInt32(0); // Accepted count
        initialStatusPacket.WriteInt32(state.TotalPlayers);
        initialStatusPacket.WriteInt32(0); // State: 0 = Waiting
        initialStatusPacket.WriteInt32((int)(timeoutSeconds * 1000));

        int totalTimeoutMs = Math.Max(1000, (int)Math.Round(timeoutSeconds * 1000.0));
        ChatBuffer nativePendingPacket = CreateNativePendingMatchPacket(
            remainingTimeMs: totalTimeoutMs,
            totalTimeMs: totalTimeoutMs,
            totalPlayers: state.TotalPlayers,
            acceptedPlayers: 0,
            recipientAccepted: false);

        ChatBuffer promptWhisper = CreateReadyCheckWhisperPacket(
            $"PROMPT|{readyCheckId}|{state.TotalPlayers}|{(int)Math.Round(timeoutSeconds)}|{resolvedMap}|{resolvedMode}");
        ChatBuffer initialStatusWhisper = CreateReadyCheckWhisperPacket(
            $"STATUS|{readyCheckId}|0|{state.TotalPlayers}|0");

        ChatBuffer arcadePromptWhisper = CreateSystemWhisperPacket(
            $"[QUEUE ARCADE] MATCHMAKING READY CHECK ({resolvedMap} - {resolvedMode})! Click 'Accept Match' on screen within {(int)Math.Round(timeoutSeconds)}s — you stay in your mini-game until all {state.TotalPlayers} players accept.");
        DateTimeOffset readyCheckGuardUntil = state.ExpiresAtUtc.AddSeconds(2.0);

        foreach (int accountId in state.RequiredAccountIds)
        {
            ChatSession? clientSession = chatContext.ClientChatSessions.Values
                .FirstOrDefault(s => s.Account?.ID == accountId);
            if (clientSession is not null && clientSession.IsSessionConnected)
            {
                if (clientSession.ClientMetadata is not null)
                {
                    DateTimeOffset? priorCooldown = clientSession.ClientMetadata.ArcadePushCooldownUntilUtc;
                    state.OriginalArcadePushCooldowns[accountId] = priorCooldown;
                    if (!priorCooldown.HasValue || priorCooldown.Value < readyCheckGuardUntil)
                    {
                        clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = readyCheckGuardUntil;
                    }
                }

                clientSession.Send(nativePendingPacket);
                clientSession.Send(promptPacket);
                clientSession.Send(initialStatusPacket);
                clientSession.Send(promptWhisper);
                clientSession.Send(initialStatusWhisper);

                if (clientSession.ClientMetadata is { ActiveArcadeMatchId: not null } or { ActiveArcadeServerId: not null })
                {
                    clientSession.Send(arcadePromptWhisper);
                    Log.Information(
                        "[Matchmaking] [ReadyCheck] Sent mid-game Queue Arcade ReadyCheck prompt (0x0F04 + whisper) to AccountID={AccountID} in ArcadeMatchID={ArcadeMatchId}",
                        accountId,
                        clientSession.ClientMetadata.ActiveArcadeMatchId ?? -1);
                }
            }
        }

        Log.Information(
            "[Matchmaking] [ReadyCheck] Initiated ReadyCheckId={ReadyCheckId} for {PlayerCount} players across {GroupCount} group(s) (Map='{MapName}', Mode='{Mode}', Timeout={Timeout:F1}s, Distributed={Distributed})",
            readyCheckId,
            state.TotalPlayers,
            groups.Count,
            resolvedMap,
            resolvedMode,
            timeoutSeconds,
            distributedStateStore is not null);
    }

    private static ChatBuffer CreateSystemWhisperPacket(string message)
    {
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        buffer.WriteString("System");
        buffer.WriteString(message);
        return buffer;
    }

    private static ChatBuffer CreateNativePendingMatchPacket(
        int remainingTimeMs,
        int totalTimeMs,
        int totalPlayers,
        int acceptedPlayers,
        bool recipientAccepted)
    {
        // k2_x64.dll FUN_180695ed0 (0x0F04 NET_CHAT_CL_TMM_PENDING_MATCH):
        // Int32 uiAcceptTimeMs, Int32 uiAcceptTotalTimeMs, Int32 uiNumPlayers, Int32 uiNumPlayersAccepted, Int8 bAcceptedPendingMatch
        ChatBuffer packet = new();
        packet.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_PENDING_MATCH);
        packet.WriteInt32(Math.Max(0, remainingTimeMs));
        packet.WriteInt32(Math.Max(0, totalTimeMs));
        packet.WriteInt32(totalPlayers);
        packet.WriteInt32(acceptedPlayers);
        packet.WriteInt8((byte)(recipientAccepted ? 1 : 0));
        return packet;
    }

    private PendingReadyCheckState? FindActiveReadyCheckForAccount(int accountId)
    {
        foreach (KeyValuePair<int, PendingReadyCheckState> kvp in _pendingReadyChecks)
        {
            PendingReadyCheckState candidate = kvp.Value;
            if (!candidate.Resolved && candidate.RequiredAccountIds.Contains(accountId))
            {
                return candidate;
            }
        }

        return null;
    }

    public bool SubmitPendingMatchAccept(int accountId)
    {
        PendingReadyCheckState? activeState = FindActiveReadyCheckForAccount(accountId);
        if (activeState is null)
        {
            Log.Warning(
                "[Matchmaking] [ReadyCheck] Received sync AcceptPendingMatch from AccountID={AccountID}, but no local active ReadyCheck was found.",
                accountId);
            return false;
        }

        return SubmitReadyCheckResponse(activeState.ReadyCheckId, accountId, accepted: true);
    }

    public async Task<bool> SubmitPendingMatchAcceptAsync(int accountId)
    {
        PendingReadyCheckState? activeState = FindActiveReadyCheckForAccount(accountId);
        if (activeState is not null)
        {
            return await SubmitReadyCheckResponseAsync(activeState.ReadyCheckId, accountId, accepted: true)
                .ConfigureAwait(false);
        }

        if (distributedStateStore is not null)
        {
            int? distributedReadyCheckId = await distributedStateStore
                .FindActiveReadyCheckIdForAccountAsync(accountId)
                .ConfigureAwait(false);
            if (distributedReadyCheckId.HasValue)
            {
                Log.Information(
                    "[Matchmaking][ReadyCheck][DistributedLookup] Pod={0} resolved native 0x0F05 AcceptPendingMatch for AccountID={1} -> ReadyCheckId={2}",
                    distributedStateStore.PodId,
                    accountId,
                    distributedReadyCheckId.Value);
                return await SubmitReadyCheckResponseAsync(distributedReadyCheckId.Value, accountId, accepted: true)
                    .ConfigureAwait(false);
            }
        }

        Log.Warning(
            "[Matchmaking] [ReadyCheck] Received native 0x0F05 AcceptPendingMatch from AccountID={0}, but no active ReadyCheck was found (Local or Distributed).",
            accountId);
        return false;
    }

    public async Task<bool> SubmitPendingMatchDeclineAsync(int accountId)
    {
        PendingReadyCheckState? activeState = FindActiveReadyCheckForAccount(accountId);
        if (activeState is not null)
        {
            return await SubmitReadyCheckResponseAsync(activeState.ReadyCheckId, accountId, accepted: false)
                .ConfigureAwait(false);
        }

        if (distributedStateStore is not null)
        {
            int? distributedReadyCheckId = await distributedStateStore
                .FindActiveReadyCheckIdForAccountAsync(accountId)
                .ConfigureAwait(false);
            if (distributedReadyCheckId.HasValue)
            {
                Log.Information(
                    "[Matchmaking][ReadyCheck][DistributedLookup] Pod={0} resolved Decline for AccountID={1} -> ReadyCheckId={2}",
                    distributedStateStore.PodId,
                    accountId,
                    distributedReadyCheckId.Value);
                return await SubmitReadyCheckResponseAsync(distributedReadyCheckId.Value, accountId, accepted: false)
                    .ConfigureAwait(false);
            }
        }

        Log.Warning(
            "[Matchmaking] [ReadyCheck] Received Decline from AccountID={0}, but no active ReadyCheck was found (Local or Distributed).",
            accountId);
        return false;
    }

    public bool SubmitReadyCheckResponse(int readyCheckId, int accountId, bool accepted)
    {
        if (distributedStateStore is not null)
        {
            Log.Warning(
                "[Matchmaking][ReadyCheck] Synchronous SubmitReadyCheckResponse called on Pod={0} with distributedStateStore configured; dispatching through authoritative SubmitReadyCheckResponseAsync.",
                distributedStateStore.PodId);
            DispatchDistributedStateBackground(
                () => SubmitReadyCheckResponseAsync(readyCheckId, accountId, accepted),
                $"SubmitReadyCheckResponse({readyCheckId},{accountId},{accepted})");
            return true;
        }

        bool success = TrySubmitReadyCheckResponseCore(readyCheckId, accountId, accepted, out PendingReadyCheckState? state, out bool allAccepted);
        if (success && allAccepted && state is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessMatchUnsafe(state.Groups, bypassReadyCheck: true);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Matchmaking] [ReadyCheck] Unhandled exception in background match allocation for ReadyCheckId={ReadyCheckId}", state.ReadyCheckId);
                }
            });
        }
        return success;
    }

    public async Task<bool> SubmitReadyCheckResponseAsync(int readyCheckId, int accountId, bool accepted)
    {
        if (distributedStateStore is null)
        {
            bool localSuccess = TrySubmitReadyCheckResponseCore(readyCheckId, accountId, accepted, out PendingReadyCheckState? localOnlyState, out bool allAccepted);
            if (localSuccess && allAccepted && localOnlyState is not null)
            {
                await ProcessMatchUnsafe(localOnlyState.Groups, bypassReadyCheck: true).ConfigureAwait(false);
            }
            return localSuccess;
        }

        // AUTHORITATIVE VALKEY-FIRST TRANSITION:
        // Never mutate local state or launch ProcessMatchUnsafe before Valkey authoritatively validates and applies the vote.
        DistributedReadyCheckVoteResult distVote = await distributedStateStore
            .TrySubmitReadyCheckVoteAsync(readyCheckId, accountId, accepted)
            .ConfigureAwait(false);

        Log.Information(
            "[Matchmaking][DistributedReadyCheck] Pod={0} authoritative vote ReadyCheckId={1} AccountID={2} Accepted={3} -> Outcome={4} ({5}/{6}, Status={7})",
            distributedStateStore.PodId,
            readyCheckId,
            accountId,
            accepted,
            distVote.Outcome,
            distVote.AcceptedCount,
            distVote.TotalPlayers,
            distVote.Status);

        if (distVote.Outcome is DistributedReadyCheckVoteOutcome.NotFound
            or DistributedReadyCheckVoteOutcome.NotParticipant
            or DistributedReadyCheckVoteOutcome.DuplicateVote)
        {
            return false;
        }

        if (distVote.Outcome is DistributedReadyCheckVoteOutcome.AlreadyResolved
            or DistributedReadyCheckVoteOutcome.Expired)
        {
            if (_pendingReadyChecks.TryRemove(readyCheckId, out PendingReadyCheckState? staleState))
            {
                lock (staleState)
                {
                    staleState.Resolved = true;
                }
                Log.Warning(
                    "[Matchmaking][DistributedSync] Pod={0} purged stale local ReadyCheckId={1} after Valkey reported {2} (Status={3}). Suppressing local mutation and match launch.",
                    distributedStateStore.PodId,
                    readyCheckId,
                    distVote.Outcome,
                    distVote.Status);
            }
            return false;
        }

        if (_pendingReadyChecks.TryGetValue(readyCheckId, out PendingReadyCheckState? ownerState))
        {
            if (distVote.Outcome == DistributedReadyCheckVoteOutcome.Accepted)
            {
                HashSet<int> acceptedSetSnapshot;
                lock (ownerState)
                {
                    ownerState.AcceptedAccountIds.Add(accountId);
                    acceptedSetSnapshot = [.. ownerState.AcceptedAccountIds];
                }

                BroadcastReadyCheckAcceptedProgress(
                    readyCheckId,
                    ownerState.RequiredAccountIds,
                    acceptedSetSnapshot,
                    distVote.AcceptedCount,
                    distVote.TotalPlayers,
                    ownerState.CreatedAtUtc,
                    ownerState.ExpiresAtUtc,
                    reachedQuorum: false);
                return true;
            }

            if (distVote.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached)
            {
                HashSet<int> acceptedSetSnapshot;
                lock (ownerState)
                {
                    ownerState.AcceptedAccountIds.Add(accountId);
                    ownerState.Resolved = true;
                    acceptedSetSnapshot = [.. ownerState.RequiredAccountIds];
                }
                _pendingReadyChecks.TryRemove(readyCheckId, out _);

                BroadcastReadyCheckAcceptedProgress(
                    readyCheckId,
                    ownerState.RequiredAccountIds,
                    acceptedSetSnapshot,
                    distVote.AcceptedCount,
                    distVote.TotalPlayers,
                    ownerState.CreatedAtUtc,
                    ownerState.ExpiresAtUtc,
                    reachedQuorum: true);

                Log.Information(
                    "[Matchmaking][DistributedReadyCheck] Owner Pod={0} won authoritative QuorumReached ({1}/{2}) for ReadyCheckId={3}; invoking ProcessMatchUnsafe.",
                    distributedStateStore.PodId,
                    distVote.AcceptedCount,
                    distVote.TotalPlayers,
                    readyCheckId);

                await ProcessMatchUnsafe(ownerState.Groups, bypassReadyCheck: true).ConfigureAwait(false);
                return true;
            }

            if (distVote.Outcome == DistributedReadyCheckVoteOutcome.Declined)
            {
                lock (ownerState)
                {
                    ownerState.DeclinedAccountIds.Add(accountId);
                    ownerState.Resolved = true;
                }
                _pendingReadyChecks.TryRemove(readyCheckId, out _);
                ApplyLocalReadyCheckDeclineCleanup(ownerState, accountId, distVote.AcceptedCount);
                return true;
            }
        }

        // FAILOVER POD PATH: ReadyCheck was owned by another pod; synchronize locally connected clients and handle quorum/decline.
        DistributedReadyCheckSnapshot? distSnap = await distributedStateStore
            .GetReadyCheckAsync(readyCheckId)
            .ConfigureAwait(false);

        if (distVote.Outcome == DistributedReadyCheckVoteOutcome.Accepted)
        {
            if (distSnap is not null)
            {
                HashSet<int> acceptedSet = [.. distSnap.AcceptedAccountIds];
                BroadcastReadyCheckAcceptedProgress(
                    readyCheckId,
                    distSnap.RequiredAccountIds,
                    acceptedSet,
                    distVote.AcceptedCount,
                    distVote.TotalPlayers,
                    distSnap.CreatedAtUtc,
                    distSnap.ExpiresAtUtc,
                    reachedQuorum: false);
            }
            return true;
        }

        if (distVote.Outcome == DistributedReadyCheckVoteOutcome.Declined)
        {
            if (distSnap is not null)
            {
                ApplyFailoverReadyCheckDeclineCleanup(distSnap, accountId, distVote.AcceptedCount);
            }
            return true;
        }

        if (distVote.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached)
        {
            if (distSnap is not null)
            {
                HashSet<int> allAcceptedSet = [.. distSnap.RequiredAccountIds];
                BroadcastReadyCheckAcceptedProgress(
                    readyCheckId,
                    distSnap.RequiredAccountIds,
                    allAcceptedSet,
                    distVote.AcceptedCount,
                    distVote.TotalPlayers,
                    distSnap.CreatedAtUtc,
                    distSnap.ExpiresAtUtc,
                    reachedQuorum: true);

                await HandleFailoverReadyCheckQuorumAsync(distSnap).ConfigureAwait(false);
            }
            return true;
        }

        return false;
    }

    private void BroadcastReadyCheckAcceptedProgress(
        int readyCheckId,
        IEnumerable<int> requiredAccountIds,
        HashSet<int> acceptedSetSnapshot,
        int acceptedCount,
        int totalPlayers,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        bool reachedQuorum)
    {
        double remainingMs = Math.Max(0.0, (expiresAtUtc - _timeProvider.GetUtcNow()).TotalMilliseconds);
        int totalTimeoutMs = Math.Max(1000, (int)Math.Round((expiresAtUtc - createdAtUtc).TotalMilliseconds));
        int nativeRemainingMs = reachedQuorum ? 0 : Math.Max(1, (int)Math.Round(remainingMs));
        int statusCode = reachedQuorum ? 1 : 0;

        ChatBuffer statusPacket = new();
        statusPacket.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_STATUS);
        statusPacket.WriteInt32(readyCheckId);
        statusPacket.WriteInt32(acceptedCount);
        statusPacket.WriteInt32(totalPlayers);
        statusPacket.WriteInt32(statusCode);
        statusPacket.WriteInt32((int)remainingMs);

        ChatBuffer statusWhisper = CreateReadyCheckWhisperPacket(
            $"STATUS|{readyCheckId}|{acceptedCount}|{totalPlayers}|{statusCode}");

        foreach (int targetId in requiredAccountIds)
        {
            ChatSession? clientSession = chatContext.ClientChatSessions.Values
                .FirstOrDefault(s => s.Account?.ID == targetId);
            if (clientSession is not null && clientSession.IsSessionConnected)
            {
                ChatBuffer nativeUpdate = CreateNativePendingMatchPacket(
                    remainingTimeMs: nativeRemainingMs,
                    totalTimeMs: totalTimeoutMs,
                    totalPlayers: totalPlayers,
                    acceptedPlayers: acceptedCount,
                    recipientAccepted: reachedQuorum || acceptedSetSnapshot.Contains(targetId));
                clientSession.Send(nativeUpdate);
                clientSession.Send(statusPacket);
                clientSession.Send(statusWhisper);
            }
        }
    }

    private void ApplyLocalReadyCheckDeclineCleanup(
        PendingReadyCheckState state,
        int decliningAccountId,
        int acceptedSnapshot)
    {
        int readyCheckId = state.ReadyCheckId;
        MatchmakingGroup? offendingGroup = state.Groups.FirstOrDefault(g => g.Members.Any(m => m.Account.ID == decliningAccountId));
        List<MatchmakingGroup> compliantGroups = state.Groups.Where(g => g != offendingGroup).ToList();

        if (offendingGroup is not null)
        {
            offendingGroup.QueueStartTime = null;
            NotifyGroupStateChanged(offendingGroup, "readycheck_declined_ejected");
            ChatBuffer failNotice = new();
            failNotice.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH);
            failNotice.WriteInt32(decliningAccountId);
            failNotice.WriteInt32(-1); // Sentinel required by k2_x64.dll FUN_180695cc0 to fire TMMMatchFailedToAccept
            foreach (MatchmakingGroupMember member in offendingGroup.Members)
            {
                ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == member.Account.ID);
                if (s is not null && s.IsSessionConnected)
                {
                    RestorePreReadyCheckArcadeCooldown(s, state, member.Account.ID);
                    s.Send(failNotice);
                }
                EvictOffendingMemberFromQueueArcade(member, s, "ready_check_declined");
            }
        }

        ChatBuffer arcadeKeepPlayingWhisper = CreateSystemWhisperPacket(
            "[QUEUE ARCADE] Another player failed to accept — keeping you in your mini-game at the front of the Ranked queue!");

        foreach (MatchmakingGroup cg in compliantGroups)
        {
            if (cg.Members.Count > 0 && state.OriginalQueueStartTimes.TryGetValue(cg.Members[0].Account.ID, out DateTimeOffset origTime))
            {
                cg.QueueStartTime = origTime;
                NotifyGroupStateChanged(cg, "readycheck_declined_compliant_rejoin");
            }
            ChatBuffer rejoinNotice = new();
            rejoinNotice.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE);
            foreach (MatchmakingGroupMember member in cg.Members)
            {
                ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == member.Account.ID);
                if (s is not null && s.IsSessionConnected)
                {
                    RestorePreReadyCheckArcadeCooldown(s, state, member.Account.ID);
                    s.Send(rejoinNotice);
                    if (s.ClientMetadata is { ActiveArcadeMatchId: not null } or { ActiveArcadeServerId: not null })
                    {
                        s.Send(arcadeKeepPlayingWhisper);
                    }
                }
            }
        }

        ChatBuffer clearNativePending = CreateNativePendingMatchPacket(
            remainingTimeMs: 0,
            totalTimeMs: 0,
            totalPlayers: state.TotalPlayers,
            acceptedPlayers: acceptedSnapshot,
            recipientAccepted: false);

        ChatBuffer declineStatus = new();
        declineStatus.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_STATUS);
        declineStatus.WriteInt32(readyCheckId);
        declineStatus.WriteInt32(acceptedSnapshot);
        declineStatus.WriteInt32(state.TotalPlayers);
        declineStatus.WriteInt32(2); // State: 2 = Declined
        declineStatus.WriteInt32(0);

        ChatBuffer declineWhisper = CreateReadyCheckWhisperPacket(
            $"STATUS|{readyCheckId}|{acceptedSnapshot}|{state.TotalPlayers}|2");

        foreach (int targetId in state.RequiredAccountIds)
        {
            ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == targetId);
            if (s is not null && s.IsSessionConnected)
            {
                s.Send(clearNativePending);
                s.Send(declineStatus);
                s.Send(declineWhisper);
            }
        }

        Log.Information(
            "[Matchmaking] [ReadyCheck] Player AccountID={AccountID} DECLINED ReadyCheckId={ReadyCheckId}. Ejected offending group '{OffendingGuid}', preserved queue priority for {CompliantCount} compliant group(s) at front of queue.",
            decliningAccountId,
            readyCheckId,
            offendingGroup?.GUID,
            compliantGroups.Count);
    }

    private void ApplyFailoverReadyCheckDeclineCleanup(
        DistributedReadyCheckSnapshot snap,
        int decliningAccountId,
        int acceptedSnapshot)
    {
        HashSet<int> offendingAccounts = [decliningAccountId];
        if (snap.RequiredMemberGroups is { Count: > 0 })
        {
            DistributedReadyCheckGroupMembership? offendingMembership = snap.RequiredMemberGroups
                .FirstOrDefault(mg => mg.MemberAccountIds.Contains(decliningAccountId));
            if (offendingMembership is not null)
            {
                foreach (int id in offendingMembership.MemberAccountIds)
                {
                    offendingAccounts.Add(id);
                }

                MatchmakingGroup? localOffendingGroup = Groups.Values
                    .FirstOrDefault(g => string.Equals(g.GUID.ToString("N"), offendingMembership.GroupGuid, StringComparison.OrdinalIgnoreCase));
                if (localOffendingGroup is not null)
                {
                    localOffendingGroup.QueueStartTime = null;
                    NotifyGroupStateChanged(localOffendingGroup, "failover_readycheck_declined");
                }
            }
        }

        ChatBuffer failNotice = new();
        failNotice.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH);
        failNotice.WriteInt32(decliningAccountId);
        failNotice.WriteInt32(-1);

        ChatBuffer rejoinNotice = new();
        rejoinNotice.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE);

        ChatBuffer clearNativePending = CreateNativePendingMatchPacket(
            remainingTimeMs: 0,
            totalTimeMs: 0,
            totalPlayers: snap.RequiredAccountIds.Count,
            acceptedPlayers: acceptedSnapshot,
            recipientAccepted: false);

        ChatBuffer declineStatus = new();
        declineStatus.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_STATUS);
        declineStatus.WriteInt32(snap.ReadyCheckId);
        declineStatus.WriteInt32(acceptedSnapshot);
        declineStatus.WriteInt32(snap.RequiredAccountIds.Count);
        declineStatus.WriteInt32(2); // Declined
        declineStatus.WriteInt32(0);

        ChatBuffer declineWhisper = CreateReadyCheckWhisperPacket(
            $"STATUS|{snap.ReadyCheckId}|{acceptedSnapshot}|{snap.RequiredAccountIds.Count}|2");

        foreach (int targetId in snap.RequiredAccountIds)
        {
            ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == targetId);
            if (s is not null && s.IsSessionConnected)
            {
                s.Send(offendingAccounts.Contains(targetId) ? failNotice : rejoinNotice);
                s.Send(clearNativePending);
                s.Send(declineStatus);
                s.Send(declineWhisper);
            }
        }

        Log.Information(
            "[Matchmaking][DistributedFailover] Pod={0} processed cross-pod DECLINE for ReadyCheckId={1} by AccountID={2} (OwnerPod={3}).",
            distributedStateStore?.PodId ?? "local",
            snap.ReadyCheckId,
            decliningAccountId,
            snap.OwnerPodId);
    }

    private async Task HandleFailoverReadyCheckQuorumAsync(DistributedReadyCheckSnapshot snap)
    {
        // 1. If all groups exist locally on this pod, route through standard ProcessMatchUnsafe
        List<MatchmakingGroup> localGroups = [];
        foreach (string guid in snap.GroupGuids)
        {
            MatchmakingGroup? found = Groups.Values
                .FirstOrDefault(g => string.Equals(g.GUID.ToString("N"), guid, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                localGroups.Add(found);
            }
        }

        if (localGroups.Count == snap.GroupGuids.Count && localGroups.Count > 0)
        {
            Log.Information(
                "[Matchmaking][DistributedFailover] Pod={0} won QuorumReached for ReadyCheckId={1} and matched all {2} local groups; invoking ProcessMatchUnsafe.",
                distributedStateStore?.PodId ?? "local",
                snap.ReadyCheckId,
                localGroups.Count);
            await ProcessMatchUnsafe(localGroups, bypassReadyCheck: true).ConfigureAwait(false);
            return;
        }

        // 2. Otherwise attempt direct failover allocation against any IDLE match server connected to THIS pod
        bool allocated = await TryAllocateFailoverMatchFromSnapshotAsync(snap).ConfigureAwait(false);
        if (allocated)
        {
            return;
        }

        // 3. No local idle match server available on this failover pod -> emit stream event so any pod with an idle server can allocate
        if (distributedStateStore is not null)
        {
            await distributedStateStore.PublishMatchAllocationRequestedAsync(new MatchAllocationRequestedPayload(
                ReadyCheckId: snap.ReadyCheckId,
                MapName: snap.MapName,
                GameMode: snap.GameMode,
                GroupType: snap.GroupType,
                GameType: snap.GameType,
                GroupGuids: snap.GroupGuids,
                RequiredAccountIds: snap.RequiredAccountIds,
                RequestedByPod: distributedStateStore.PodId)).ConfigureAwait(false);

            Log.Information(
                "[Matchmaking][DistributedFailover] Pod={0} won QuorumReached for ReadyCheckId={1} (OwnerPod={2}) with no local idle game server; published MATCH_ALLOCATION_REQUESTED stream event.",
                distributedStateStore.PodId,
                snap.ReadyCheckId,
                snap.OwnerPodId);
        }
    }

    private async Task<bool> TryAllocateFailoverMatchFromSnapshotAsync(DistributedReadyCheckSnapshot snap)
    {
        if (distributedStateStore is null || snap.RequiredAccountIds.Count == 0)
        {
            return false;
        }

        IReadOnlyList<string> desiredRegions = snap.GameRegions ?? [];
        List<ChatSession> idleServers = chatContext.MatchServerChatSessions.Values
            .Where(s => s.IsSessionConnected &&
                        s.ServerMetadata is { IsQueueArcade: false, Status: ChatProtocol.ServerStatus.SERVER_STATUS_IDLE })
            .ToList();

        ChatSession? matchServer = null;
        SlotLeaseResult? acquiredLease = null;

        if (slotLeaseService is not null)
        {
            string workerId = $"{Environment.MachineName}_{Environment.ProcessId}";
            foreach (ChatSession candidate in idleServers)
            {
                SlotLeaseResult lease = await slotLeaseService.TryAcquireSlotLeaseAsync(
                    candidate.ServerMetadata.Location ?? "local",
                    candidate.ServerMetadata.Address ?? "127.0.0.1",
                    candidate.ServerMetadata.Port,
                    workerId,
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (lease.Succeeded)
                {
                    matchServer = candidate;
                    acquiredLease = lease;
                    break;
                }
            }
        }
        else
        {
            if (desiredRegions.Count > 0)
            {
                matchServer = idleServers.FirstOrDefault(s =>
                    !string.IsNullOrWhiteSpace(s.ServerMetadata.Location) &&
                    desiredRegions.Contains(s.ServerMetadata.Location, StringComparer.OrdinalIgnoreCase));
            }
            matchServer ??= idleServers.FirstOrDefault();
        }

        if (matchServer is null)
        {
            return false;
        }

        matchServer.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_LOADING;

        ChatProtocol.TMMType groupType = (ChatProtocol.TMMType)snap.GroupType;
        ChatProtocol.TMMGameType gameType = (ChatProtocol.TMMGameType)snap.GameType;
        bool isCoop = groupType == ChatProtocol.TMMType.TMM_TYPE_COOP;
        byte matchType = ResolveArrangedMatchType(groupType, gameType);
        string effectiveMap = string.IsNullOrWhiteSpace(snap.MapName) ? "caldavar" : snap.MapName;
        int teamSize = Math.Max(1, (int)Math.Ceiling(snap.RequiredAccountIds.Count / 2.0));

        const int maxAttempts = 3;
        int matchUpId = 0;
        bool registered = false;
        DateTimeOffset createdAt = _timeProvider.GetUtcNow();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                matchUpId = await distributedStateStore.AllocateNextMatchUpIdAsync().ConfigureAwait(false);
                DistributedPendingMatchSnapshot pmSnap = new(
                    MatchUpId: matchUpId,
                    AllocatedServerId: matchServer.ServerMetadata.ServerID,
                    ResolvedMapName: effectiveMap,
                    GroupGuids: snap.GroupGuids,
                    MemberAccountIds: snap.RequiredAccountIds,
                    SlotLockKey: acquiredLease?.LockKey,
                    SlotLeaseToken: acquiredLease?.LeaseToken,
                    OwnerPodId: distributedStateStore.PodId,
                    CreatedAtUtc: createdAt,
                    MatchType: matchType,
                    GroupType: snap.GroupType,
                    GameType: snap.GameType,
                    GameModes: snap.GameModes ?? (string.IsNullOrWhiteSpace(snap.GameMode) ? [] : [snap.GameMode]),
                    GameRegions: desiredRegions,
                    TeamSize: teamSize,
                    Ranked: snap.Ranked,
                    BotDifficulty: snap.BotDifficulty,
                    RandomizeBots: snap.RandomizeBots);

                registered = await distributedStateStore.TryRegisterPendingMatchAsync(pmSnap, TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);
                if (registered)
                {
                    break;
                }
            }
            catch (DistributedMatchmakingStoreException ex)
            {
                Log.Error(
                    ex,
                    "[Matchmaking][DistributedFailover] Failed registering failover pending match for ReadyCheckId={0} on Pod={1}; rolling back server status.",
                    snap.ReadyCheckId,
                    distributedStateStore.PodId);
                break;
            }
        }

        if (!registered)
        {
            matchServer.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
            if (acquiredLease is not null && slotLeaseService is not null)
            {
                await slotLeaseService.ReleaseSlotLeaseAsync(acquiredLease.LockKey, acquiredLease.LeaseToken).ConfigureAwait(false);
            }
            return false;
        }

        ChatBuffer createMatch = new();
        createMatch.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);
        createMatch.WriteInt8(matchType);
        createMatch.WriteInt32(matchUpId);
        createMatch.WriteInt32(0);
        createMatch.WriteInt32(0);
        createMatch.WriteString("TMM Match #");

        string modeToken = string.IsNullOrWhiteSpace(snap.GameMode) ? "normal" : snap.GameMode.ToLowerInvariant();
        string settings = isCoop
            ? $"mode:botmatch map:{effectiveMap} teamsize:{teamSize} casual:true allheroes:true botdifficulty:{Math.Clamp((int)snap.BotDifficulty, 1, 3)} noleaver:true spectators:10"
            : $"mode:{modeToken} map:{effectiveMap} teamsize:{teamSize} noleaver:true spectators:10";
        createMatch.WriteString(settings);
        createMatch.WriteInt8(0);
        createMatch.WriteInt8(0);
        createMatch.WriteInt8((byte)snap.RequiredAccountIds.Count);

        int t1Slot = 0;
        int t2Slot = 0;
        for (int i = 0; i < snap.RequiredAccountIds.Count; i++)
        {
            byte team = isCoop || i < teamSize ? (byte)1 : (byte)2;
            byte slot = team == 1 ? (byte)t1Slot++ : (byte)t2Slot++;
            createMatch.WriteInt32(snap.RequiredAccountIds[i]);
            createMatch.WriteInt8(team);
            createMatch.WriteInt8(slot);
            createMatch.WriteInt8(1);
            createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
            createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
            createMatch.WriteInt8(0);
            createMatch.WriteInt8(0);
            createMatch.WriteInt8(0);
        }
        createMatch.WriteInt32(0);
        matchServer.Send(createMatch);

        Log.Information(
            "[Matchmaking][DistributedFailover] Pod={0} directly allocated ServerID={1} (MatchUpID={2}, MatchType={3}, Players={4}) after cross-pod QuorumReached on ReadyCheckId={5}.",
            distributedStateStore.PodId,
            matchServer.ServerMetadata.ServerID,
            matchUpId,
            matchType,
            snap.RequiredAccountIds.Count,
            snap.ReadyCheckId);
        return true;
    }

    private bool TrySubmitReadyCheckResponseCore(
        int readyCheckId,
        int accountId,
        bool accepted,
        out PendingReadyCheckState? state,
        out bool allAccepted)
    {
        allAccepted = false;
        if (!_pendingReadyChecks.TryGetValue(readyCheckId, out state) || state.Resolved)
        {
            return false;
        }

        if (!state.RequiredAccountIds.Contains(accountId))
        {
            return false;
        }

        if (accepted)
        {
            int acceptedSnapshot;
            HashSet<int> acceptedSetSnapshot;
            bool reachedQuorum = false;
            lock (state)
            {
                if (state.Resolved || !state.AcceptedAccountIds.Add(accountId))
                {
                    return false;
                }
                acceptedSnapshot = state.AcceptedAccountIds.Count;
                acceptedSetSnapshot = [.. state.AcceptedAccountIds];
                if (acceptedSnapshot == state.TotalPlayers)
                {
                    state.Resolved = true;
                    reachedQuorum = true;
                }
            }

            Log.Information(
                "[Matchmaking] [ReadyCheck] Player AccountID={AccountID} ACCEPTED ReadyCheckId={ReadyCheckId} ({AcceptedCount}/{TotalPlayers})",
                accountId,
                readyCheckId,
                acceptedSnapshot,
                state.TotalPlayers);

            BroadcastReadyCheckAcceptedProgress(
                readyCheckId,
                state.RequiredAccountIds,
                acceptedSetSnapshot,
                acceptedSnapshot,
                state.TotalPlayers,
                state.CreatedAtUtc,
                state.ExpiresAtUtc,
                reachedQuorum);

            if (reachedQuorum)
            {
                _pendingReadyChecks.TryRemove(readyCheckId, out _);
                allAccepted = true;

                Log.Information(
                    "[Matchmaking] [ReadyCheck] 10/10 players accepted (All {TotalPlayers}/{TotalPlayers}) for ReadyCheckId={ReadyCheckId}. Transitioning to match server allocation...",
                    state.TotalPlayers,
                    state.TotalPlayers,
                    readyCheckId);
            }

            return true;
        }
        else
        {
            int acceptedSnapshot;
            lock (state)
            {
                if (state.Resolved)
                {
                    return false;
                }
                state.DeclinedAccountIds.Add(accountId);
                acceptedSnapshot = state.AcceptedAccountIds.Count;
                state.Resolved = true;
            }
            _pendingReadyChecks.TryRemove(readyCheckId, out _);
            ApplyLocalReadyCheckDeclineCleanup(state, accountId, acceptedSnapshot);
            return true;
        }
    }

    public IReadOnlyList<PendingReadyCheckState> EvaluateExpiredReadyChecks(DateTimeOffset now)
    {
        List<PendingReadyCheckState> expired = new();
        foreach (KeyValuePair<int, PendingReadyCheckState> kvp in _pendingReadyChecks)
        {
            PendingReadyCheckState state = kvp.Value;
            if (state.Resolved || now < state.ExpiresAtUtc)
            {
                continue;
            }

            HashSet<int> nonAccepted;
            int acceptedSnapshot;
            lock (state)
            {
                if (state.Resolved)
                {
                    continue;
                }
                state.Resolved = true;
                acceptedSnapshot = state.AcceptedAccountIds.Count;
                nonAccepted = state.RequiredAccountIds.Except(state.AcceptedAccountIds).ToHashSet();
            }
            _pendingReadyChecks.TryRemove(state.ReadyCheckId, out _);
            DispatchDistributedStateBackground(
                () => distributedStateStore!.TryExpireReadyCheckAsync(state.ReadyCheckId),
                $"ExpireReadyCheck({state.ReadyCheckId})");

            List<MatchmakingGroup> offendingGroups = state.Groups
                .Where(g => g.Members.Any(m => nonAccepted.Contains(m.Account.ID)))
                .ToList();
            List<MatchmakingGroup> compliantGroups = state.Groups.Except(offendingGroups).ToList();

            foreach (MatchmakingGroup og in offendingGroups)
            {
                og.QueueStartTime = null;
                NotifyGroupStateChanged(og, "readycheck_expired_ejected");
                List<int> timedOutMemberIds = og.Members
                    .Where(m => nonAccepted.Contains(m.Account.ID))
                    .Select(m => m.Account.ID)
                    .ToList();
                if (timedOutMemberIds.Count == 0 && og.Members.Count > 0)
                {
                    timedOutMemberIds.Add(og.Members[0].Account.ID);
                }

                ChatBuffer failNotice = new();
                failNotice.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_ACCEPT_PENDING_MATCH);
                foreach (int timedOutMemberId in timedOutMemberIds)
                {
                    failNotice.WriteInt32(timedOutMemberId);
                }
                failNotice.WriteInt32(-1); // Sentinel required by k2_x64.dll FUN_180695cc0 to fire TMMMatchFailedToAccept

                foreach (MatchmakingGroupMember member in og.Members)
                {
                    ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == member.Account.ID);
                    if (s is not null && s.IsSessionConnected)
                    {
                        RestorePreReadyCheckArcadeCooldown(s, state, member.Account.ID);
                        s.Send(failNotice);
                    }
                    EvictOffendingMemberFromQueueArcade(member, s, "ready_check_failed_to_accept");
                }
            }

            ChatBuffer arcadeKeepPlayingWhisper = CreateSystemWhisperPacket(
                "[QUEUE ARCADE] Another player failed to accept — keeping you in your mini-game at the front of the Ranked queue!");

            foreach (MatchmakingGroup cg in compliantGroups)
            {
                if (cg.Members.Count > 0 && state.OriginalQueueStartTimes.TryGetValue(cg.Members[0].Account.ID, out DateTimeOffset origTime))
                {
                    cg.QueueStartTime = origTime;
                    NotifyGroupStateChanged(cg, "readycheck_expired_compliant_rejoin");
                }
                ChatBuffer rejoinNotice = new();
                rejoinNotice.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_REJOIN_QUEUE);
                foreach (MatchmakingGroupMember member in cg.Members)
                {
                    ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == member.Account.ID);
                    if (s is not null && s.IsSessionConnected)
                    {
                        RestorePreReadyCheckArcadeCooldown(s, state, member.Account.ID);
                        s.Send(rejoinNotice);
                        if (s.ClientMetadata is { ActiveArcadeMatchId: not null } or { ActiveArcadeServerId: not null })
                        {
                            s.Send(arcadeKeepPlayingWhisper);
                        }
                    }
                }
            }

            ChatBuffer clearNativePending = CreateNativePendingMatchPacket(
                remainingTimeMs: 0,
                totalTimeMs: 0,
                totalPlayers: state.TotalPlayers,
                acceptedPlayers: acceptedSnapshot,
                recipientAccepted: false);

            ChatBuffer timeoutStatus = new();
            timeoutStatus.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_READY_CHECK_STATUS);
            timeoutStatus.WriteInt32(state.ReadyCheckId);
            timeoutStatus.WriteInt32(acceptedSnapshot);
            timeoutStatus.WriteInt32(state.TotalPlayers);
            timeoutStatus.WriteInt32(3); // State: 3 = TimedOut
            timeoutStatus.WriteInt32(0);

            ChatBuffer timeoutWhisper = CreateReadyCheckWhisperPacket(
                $"STATUS|{state.ReadyCheckId}|{acceptedSnapshot}|{state.TotalPlayers}|3");

            foreach (int targetId in state.RequiredAccountIds)
            {
                ChatSession? s = chatContext.ClientChatSessions.Values.FirstOrDefault(cs => cs.Account?.ID == targetId);
                if (s is not null && s.IsSessionConnected)
                {
                    s.Send(clearNativePending);
                    s.Send(timeoutStatus);
                    s.Send(timeoutWhisper);
                }
            }

            Log.Warning(
                "[Matchmaking] [ReadyCheck] ReadyCheckId={ReadyCheckId} timed out after {ElapsedSec:F1}s with {Accepted}/{Total} accepted. Ejected {OffendingCount} non-responding group(s), preserved queue priority for {CompliantCount} compliant group(s) at front of queue.",
                state.ReadyCheckId,
                (now - state.CreatedAtUtc).TotalSeconds,
                acceptedSnapshot,
                state.TotalPlayers,
                offendingGroups.Count,
                compliantGroups.Count);

            expired.Add(state);
        }

        return expired;
    }

    private static void RestorePreReadyCheckArcadeCooldown(ChatSession session, PendingReadyCheckState state, int accountId)
    {
        if (session.ClientMetadata is null)
        {
            return;
        }

        if (state.OriginalArcadePushCooldowns.TryGetValue(accountId, out DateTimeOffset? priorCooldown))
        {
            session.ClientMetadata.ArcadePushCooldownUntilUtc = priorCooldown;
        }
    }

    private void EvictOffendingMemberFromQueueArcade(MatchmakingGroupMember member, ChatSession? clientSession, string reason)
    {
        ClientChatSessionMetadata? meta = clientSession?.ClientMetadata ?? member.Session?.ClientMetadata;
        int? activeArcadeServerId = meta?.ActiveArcadeServerId;
        bool wasInArcade = meta is { ActiveArcadeMatchId: not null } or { ActiveArcadeServerId: not null };

        if (meta is not null)
        {
            meta.PauseArcadeUntilNextQueueUtc = null;
            meta.LastVoluntarilyLeftArcadeMatchId = null;
        }

        if (!wasInArcade)
        {
            _queueArcadeService?.ReleasePlayerFromArcade(member.Account.ID, reason, pauseCurrentQueueWait: false);
            return;
        }

        ChatSession? effectiveClient = clientSession ?? member.Session;
        if (effectiveClient is { IsSessionConnected: true })
        {
            effectiveClient.Send(CreateSystemWhisperPacket(
                "[QUEUE ARCADE] You failed to accept the matchmaking ready-check and were removed from both the matchmaking queue and Queue Arcade."));
        }

        if (activeArcadeServerId.HasValue &&
            chatContext.MatchServerChatSessions.TryGetValue(activeArcadeServerId.Value, out ChatSession? arcadeSrv) &&
            arcadeSrv is { IsSessionConnected: true, ServerMetadata.IsQueueArcade: true })
        {
            string rawPlain = member.Account.Name ?? string.Empty;
            string rawWithTag = member.Account.Clan is null ? rawPlain : $"[{member.Account.Clan.Tag}]{rawPlain}";
            string safePlain = new(rawPlain.Where(ch => ch is not ('"' or ';' or '\r' or '\n')).ToArray());
            string safeWithTag = new(rawWithTag.Where(ch => ch is not ('"' or ';' or '\r' or '\n')).ToArray());
            if (safePlain.Length > 0)
            {
                string kickCmd = string.Equals(safeWithTag, safePlain, StringComparison.OrdinalIgnoreCase) || safeWithTag.Length == 0
                    ? $"Kick \"{safePlain}\""
                    : $"Kick \"{safeWithTag}\";Kick \"{safePlain}\"";

                ChatBuffer remoteCmd = new();
                remoteCmd.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);
                remoteCmd.WriteString(arcadeSrv.ServerMetadata?.SessionCookie ?? string.Empty);
                remoteCmd.WriteString(kickCmd);
                arcadeSrv.Send(remoteCmd);

                Log.Information(
                    "[Matchmaking] [ReadyCheck] Evicted non-accepting AccountID={AccountID} ('{PlayerName}', Reason='{Reason}') from Queue Arcade ServerID={ServerID} via ReleasePlayerFromArcade + Kick",
                    member.Account.ID,
                    safePlain,
                    reason,
                    activeArcadeServerId.Value);
            }
        }

        _queueArcadeService?.ReleasePlayerFromArcade(member.Account.ID, reason, pauseCurrentQueueWait: false);
    }

    private async Task<bool> ProcessMatchUnsafe(List<MatchmakingGroup> groups, bool bypassReadyCheck = false)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        double avgWaitSeconds = groups.Average(g => (now - (g.QueueStartTime ?? now)).TotalSeconds);
        int totalPlayers = groups.Sum(g => g.Members.Count);

        MatchmakingGroup representativeGroup = groups.First();
        bool isCoop = representativeGroup.Information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP;
        BotMatchMapSpec coopMapSpec = isCoop
            ? BotMatchMapCatalog.ResolveOrFallback(representativeGroup.Information.MapName)
            : BotMatchMapCatalog.Default;
        int calculatedTeamSize = isCoop
            ? coopMapSpec.TeamSize
            : Math.Max(1, (int)Math.Ceiling(totalPlayers / 2.0));

        List<MatchmakingGroup> team1Groups = [];
        List<MatchmakingGroup> team2Groups = [];

        if (isCoop)
        {
            if (totalPlayers > coopMapSpec.TeamSize)
            {
                Log.Warning(
                    "[COOP_CAPACITY_REJECTED] Rejecting Co-Op match for Group {GroupGUID}: party size ({PlayerCount}) exceeds '{MapId}' team capacity ({TeamSize}). Dequeuing group cleanly — reduce party size or select a larger map.",
                    representativeGroup.GUID,
                    totalPlayers,
                    coopMapSpec.Id,
                    coopMapSpec.TeamSize);

                foreach (MatchmakingGroup group in groups)
                {
                    if (group.Members.Count > 0)
                    {
                        group.LeaveQueue(group.Members[0].Account.ID);
                    }
                    else
                    {
                        group.QueueStartTime = null;
                        NotifyGroupStateChanged(group, "coop_capacity_rejected");
                    }
                }

                return false;
            }

            team1Groups = [.. groups];
            team2Groups = [];
        }
        else
        {
            if (!TryPartitionTeams(groups, calculatedTeamSize, out team1Groups, out team2Groups))
            {
                Log.Warning("Match formed with {TotalPlayers} players, but groups cannot be partitioned into teams without violating avoid-as-teammate banlists. Retaining in queue.", totalPlayers);
                return false;
            }
        }

        if (configuration.GetValue("Matchmaking:EnableReadyCheck", false) && !bypassReadyCheck && !(isCoop && totalPlayers <= 1))
        {
            PendingReadyCheckState? readyCheck = await StartReadyCheckAsync(groups).ConfigureAwait(false);
            return readyCheck != null;
        }

        // 1. Find Available Match Server (Region-Aware Selection)
        string[] desiredRegions = representativeGroup.Information.GameRegions ?? [];

        List<ChatSession> idleServers = chatContext.MatchServerChatSessions.Values
            .Where(s => s.IsSessionConnected &&
                        s.ServerMetadata is { IsQueueArcade: false, Status: ChatProtocol.ServerStatus.SERVER_STATUS_IDLE })
            .ToList();

        ChatSession? matchServer = null;
        SlotLeaseResult? acquiredLease = null;

        if (slotLeaseService != null)
        {
            List<ChatSession> orderedCandidates = desiredRegions.Length > 0
                ? [.. idleServers.Where(s => !string.IsNullOrWhiteSpace(s.ServerMetadata.Location) &&
                                            desiredRegions.Contains(s.ServerMetadata.Location, StringComparer.OrdinalIgnoreCase)),
                   .. idleServers.Where(s => string.IsNullOrWhiteSpace(s.ServerMetadata.Location) ||
                                            !desiredRegions.Contains(s.ServerMetadata.Location, StringComparer.OrdinalIgnoreCase))]
                : idleServers;

            string workerId = $"{Environment.MachineName}_{Environment.ProcessId}";
            foreach (ChatSession candidate in orderedCandidates)
            {
                SlotLeaseResult lease = await slotLeaseService.TryAcquireSlotLeaseAsync(
                    candidate.ServerMetadata.Location ?? "local",
                    candidate.ServerMetadata.Address ?? "127.0.0.1",
                    candidate.ServerMetadata.Port,
                    workerId,
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                if (lease.Succeeded)
                {
                    matchServer = candidate;
                    acquiredLease = lease;
                    break;
                }

                Log.Warning("[MatchmakerLease] Server ID {ServerId} port {Port} is contested ({ContestedBy}). Checking next candidate.",
                    candidate.ServerMetadata.ServerID, candidate.ServerMetadata.Port, lease.ContestedBy);
            }
        }
        else
        {
            if (desiredRegions.Length > 0)
            {
                matchServer = idleServers.FirstOrDefault(s =>
                    !string.IsNullOrWhiteSpace(s.ServerMetadata.Location) &&
                    desiredRegions.Contains(s.ServerMetadata.Location, StringComparer.OrdinalIgnoreCase));
            }

            matchServer ??= idleServers.FirstOrDefault();
        }

        if (matchServer is null)
        {
            // If Agones cloud-native allocation is enabled, request a pod from the Agones Fleet
            if (agonesAllocationClient != null && agonesAllocationClient.IsEnabled)
            {
                Log.Information(
                    "[Agones] No connected IDLE match servers found. Requesting cloud-native allocation for {TotalPlayers} players (Regions: [{Regions}])...",
                    totalPlayers, string.Join(", ", desiredRegions));

                GameServerAllocationResult allocationResult = await agonesAllocationClient.AllocateGameServerAsync(
                    desiredRegions, CancellationToken.None).ConfigureAwait(false);

                if (allocationResult.Succeeded)
                {
                    Log.Information(
                        "[Agones] Successfully allocated pod {PodName} ({Address}:{GamePort}) via Agones. Searching for matching connected session...",
                        allocationResult.GameServerName, allocationResult.Address, allocationResult.GamePort);

                    matchServer = chatContext.MatchServerChatSessions.Values.FirstOrDefault(s =>
                        s.IsSessionConnected &&
                        s.ServerMetadata is { IsQueueArcade: false } &&
                        (s.ServerMetadata.Port == allocationResult.GamePort ||
                         (s.ServerMetadata.Address == allocationResult.Address && s.ServerMetadata.Port == allocationResult.GamePort)));

                    if (matchServer != null)
                    {
                        Log.Information("[Agones] Attached match to newly allocated session Server ID {ServerID}", matchServer.ServerMetadata.ServerID);
                    }
                }
                else
                {
                    Log.Warning("[Agones] Cloud allocation could not be satisfied: {Error}", allocationResult.ErrorMessage);
                }
            }

            if (matchServer is null)
            {
                Log.Warning(
                    "Match formed with {TotalPlayers} players, but no IDLE match servers are available (Desired Regions: [{Regions}]). Retaining groups in queue with expanded MMR spread.",
                    totalPlayers, string.Join(", ", desiredRegions));
                return false;
            }
        }

        matchServer.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_LOADING;

        // Determine Match Type byte map and resolved map up-front so distributed PendingMatch snapshot preserves full recovery metadata
        byte matchType = ResolveArrangedMatchType(
            representativeGroup.Information.GroupType,
            representativeGroup.Information.GameType);

        string resolvedMapName = SeasonalMapResolver.ResolveMatchmakingMap(
            isCoop ? coopMapSpec.Id : groups[0].Information.MapName,
            groups[0].Information.GroupType,
            groups[0].Information.GameType,
            configuration,
            _timeProvider);

        DateTimeOffset pendingCreatedAt = _timeProvider.GetUtcNow();
        PendingMatchState pendingMatchState = new()
        {
            Groups = groups,
            Team1Groups = team1Groups,
            Team2Groups = team2Groups,
            AllocatedServerID = matchServer.ServerMetadata.ServerID,
            CreatedAt = pendingCreatedAt,
            SlotLockKey = acquiredLease?.LockKey,
            SlotLeaseToken = acquiredLease?.LeaseToken,
            ResolvedMapName = resolvedMapName
        };

        // 3. Allocate cluster-unique MatchUpId and register PendingMatch in Valkey BEFORE dequeuing groups or messaging game server
        int matchUpId = 0;
        if (distributedStateStore is not null)
        {
            const int maxRegistrationAttempts = 3;
            bool registered = false;

            for (int attempt = 1; attempt <= maxRegistrationAttempts; attempt++)
            {
                try
                {
                    matchUpId = await distributedStateStore.AllocateNextMatchUpIdAsync().ConfigureAwait(false);
                    DistributedPendingMatchSnapshot pmSnap = new(
                        MatchUpId: matchUpId,
                        AllocatedServerId: pendingMatchState.AllocatedServerID,
                        ResolvedMapName: pendingMatchState.ResolvedMapName,
                        GroupGuids: [.. groups.Select(g => g.GUID.ToString("N"))],
                        MemberAccountIds: [.. groups.SelectMany(g => g.Members).Select(m => m.Account.ID)],
                        SlotLockKey: pendingMatchState.SlotLockKey,
                        SlotLeaseToken: pendingMatchState.SlotLeaseToken,
                        OwnerPodId: distributedStateStore.PodId,
                        CreatedAtUtc: pendingMatchState.CreatedAt,
                        MatchType: matchType,
                        GroupType: (int)representativeGroup.Information.GroupType,
                        GameType: (int)representativeGroup.Information.GameType,
                        GameModes: [.. (representativeGroup.Information.GameModes ?? [])],
                        GameRegions: [.. (representativeGroup.Information.GameRegions ?? [])],
                        TeamSize: calculatedTeamSize,
                        Ranked: representativeGroup.Information.Ranked,
                        BotDifficulty: representativeGroup.Information.BotDifficulty,
                        RandomizeBots: representativeGroup.Information.RandomizeBots);

                    registered = await distributedStateStore.TryRegisterPendingMatchAsync(pmSnap, TimeSpan.FromMinutes(2))
                        .ConfigureAwait(false);
                    if (registered)
                    {
                        break;
                    }

                    Log.Warning(
                        "[Matchmaking][DistributedState] TryRegisterPendingMatchAsync returned false for MatchUpID={0} on Pod={1} (Attempt={2}/{3}); retrying ID allocation.",
                        matchUpId,
                        distributedStateStore.PodId,
                        attempt,
                        maxRegistrationAttempts);
                }
                catch (DistributedMatchmakingStoreException ex)
                {
                    Log.Error(
                        ex,
                        "[Matchmaking][DistributedState] Distributed store failure during PendingMatch registration on ServerID={0} Pod={1} (FailureKind={2}); rolling back server allocation.",
                        matchServer.ServerMetadata.ServerID,
                        distributedStateStore.PodId,
                        ex.FailureKind);
                    registered = false;
                    break;
                }
            }

            if (!registered)
            {
                matchServer.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
                if (acquiredLease is not null && slotLeaseService is not null)
                {
                    await slotLeaseService.ReleaseSlotLeaseAsync(acquiredLease.LockKey, acquiredLease.LeaseToken)
                        .ConfigureAwait(false);
                }
                Log.Error(
                    "[Matchmaking][DistributedState] Aborting match allocation on ServerID={0} Pod={1}: failed to register PendingMatch (LastMatchUpID={2}). Restored server to IDLE and retained {3} group(s) in queue.",
                    matchServer.ServerMetadata.ServerID,
                    distributedStateStore.PodId,
                    matchUpId,
                    groups.Count);
                return false;
            }
        }
        else
        {
            matchUpId = Interlocked.Increment(ref _nextMatchUpId);
        }

        // 4. Store local PendingMatch state and mark groups as no longer in queue AFTER distributed registration succeeds
        PendingMatches[matchUpId] = pendingMatchState;
        foreach (MatchmakingGroup group in groups)
        {
            group.QueueStartTime = null;
            NotifyGroupStateChanged(group, "match_allocated");
        }

        Log.Information("Allocating Match to Server ID {ServerID} ({Address}:{Port}, Location: {Location}, MatchUpID: {MatchUpID}, MatchType: {MatchType})",
            matchServer.ServerMetadata.ServerID, matchServer.ServerMetadata.Address, matchServer.ServerMetadata.Port, matchServer.ServerMetadata.Location, matchUpId, matchType);

        // Record Matchmaking Wait Duration Telemetry
        if (telemetryPublisher != null)
            _ = telemetryPublisher.RecordMatchFoundWaitTimeAsync("5v5_Ranked", totalPlayers,
                Math.Max(0.0, avgWaitSeconds));

        // Notify any players currently inside a Queue Arcade mini-game with a ~3s pre-transfer alert while Ranked loads
        _queueArcadeService?.NotifyRankedMatchAllocating(
            groups,
            PendingMatches[matchUpId].ResolvedMapName ?? groups[0].Information.MapName);

        // Note: Client Notification is deferred until ConfirmMatch is called by the Game Server (NET_CHAT_GS_ANNOUNCE_MATCH)

        // 5. Send CREATE_MATCH to Match Server
        ChatBuffer createMatch = new();

        createMatch.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);
        createMatch.WriteInt8(matchType); // MatchType
        createMatch.WriteInt32(matchUpId); // MatchUp / MatchID (This is the Correlation ID)
        createMatch.WriteInt32(0); // Unknown1
        createMatch.WriteInt32(0); // Password (0 = None)
        createMatch.WriteString("TMM Match #"); // Match Name Prefix

        // Player Info List
        List<MatchmakingGroupMember> allPlayers = [.. groups.SelectMany(g => g.Members)];

        // Construct Match Settings String (mimic GameFinder.CreateMatchSettings)
        string effectiveMapName = isCoop
            ? (PendingMatches[matchUpId].ResolvedMapName ?? coopMapSpec.Id)
            : (PendingMatches[matchUpId].ResolvedMapName ?? representativeGroup.Information.MapName);
        StringBuilder sb = new();
        if (isCoop)
        {
            int teamSize = coopMapSpec.TeamSize;
            bool fillTeamWithBots = representativeGroup.Information.RandomizeBots;
            int allyBots = fillTeamWithBots ? Math.Max(0, teamSize - allPlayers.Count) : 0;
            int enemyBots = teamSize;
            int botDiff = Math.Clamp((int)representativeGroup.Information.BotDifficulty, 1, 3); // 1=Easy, 2=Medium, 3=Hard



            sb.Append("mode:botmatch");
            sb.Append($" map:{effectiveMapName}");
            sb.Append($" teamsize:{teamSize}");
            sb.Append(" casual:true allheroes:true");
            if (coopMapSpec.SingleHeroBot is not null)
            {
                sb.Append(" allowduplicate:true");
                if (allyBots > 0)
                {
                    string allyRoster = string.Join('|', Enumerable.Repeat(coopMapSpec.SingleHeroBot, allyBots));
                    sb.Append($" bots1:{allyRoster}");
                }

                string enemyRoster = string.Join('|', Enumerable.Repeat(coopMapSpec.SingleHeroBot, enemyBots));
                sb.Append($" bots2:{enemyRoster}");
            }
            else if (coopMapSpec.CustomBotPool is { Count: > 0 } customPool)
            {
                string[] shuffledPool = [.. customPool];
                Random.Shared.Shuffle(shuffledPool);
                int cursor = 0;

                if (allyBots > 0)
                {
                    string[] allyRoster = new string[allyBots];
                    for (int i = 0; i < allyBots; i++)
                    {
                        allyRoster[i] = shuffledPool[cursor++ % shuffledPool.Length];
                    }

                    sb.Append($" bots1:{string.Join('|', allyRoster)}");
                }

                string[] enemyRoster = new string[enemyBots];
                for (int i = 0; i < enemyBots; i++)
                {
                    enemyRoster[i] = shuffledPool[cursor++ % shuffledPool.Length];
                }

                sb.Append($" bots2:{string.Join('|', enemyRoster)}");
            }
            else
            {
                sb.Append($" randombots:{allyBots}|{enemyBots}");
            }
            sb.Append($" botdifficulty:{botDiff}");

            Log.Information(
                "[COOP_MATCH_ALLOC] MatchUpID={MatchUpID} ServerID={ServerID} Map='{Map}' TeamSize={TeamSize} Humans={HumanCount} FillTeamWithBots={FillBots} AllyBots={AllyBots} EnemyBots={EnemyBots} BotDifficulty={BotDifficulty} SingleHeroBot='{SingleHeroBot}' Settings=\"{MatchSettings}\"",
                matchUpId,
                matchServer.ServerMetadata.ServerID,
                effectiveMapName,
                teamSize,
                allPlayers.Count,
                fillTeamWithBots,
                allyBots,
                enemyBots,
                botDiff,
                coopMapSpec.SingleHeroBot ?? "none",
                $"{sb} noleaver:true spectators:10");
            sb.Append(" noleaver:true spectators:10");
        }
        else
        {
            string rawMode = representativeGroup.Information.GameModes?.FirstOrDefault() ?? "normal";
            string resolvedMode = rawMode.ToLowerInvariant() switch
            {
                "bp" => "banningpick",
                "br" => "balancedrandom",
                "ap" => "normal",
                "sd" => "singledraft",
                "bd" => "banningdraft",
                "ar" => "allrandom",
                "rd" => "randomdraft",
                "cm" => "captainsmode",
                "lp" => "lockpick",
                "rb" => "reborn",
                "km" => "krosmode",
                _ => rawMode
            };
            sb.Append($"mode:{resolvedMode}");
            sb.Append($" map:{effectiveMapName}");
            sb.Append($" teamsize:{calculatedTeamSize}");
            sb.Append(" noleaver:true spectators:10"); // Defaults
            if (representativeGroup.Information.GameType is ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL
                or ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL)
                sb.Append(" casual:1");
        }

        createMatch.WriteString(sb.ToString()); // MatchSettings

        createMatch.WriteInt8(0); // UseNewMmrSystem
        createMatch.WriteInt8(0); // Unknown3

        createMatch.WriteInt8((byte)allPlayers.Count); // Player Count

        int currentSlotTeam1 = 0;
        int currentSlotTeam2 = 0;

        if (isCoop)
        {
            // Co-Op Bot Match: All human party members are assigned to Team 1 (Legion)
            foreach (MatchmakingGroup group in groups)
            foreach (MatchmakingGroupMember member in group.Members)
            {
                createMatch.WriteInt32(member.Account.ID);
                createMatch.WriteInt8(1); // Team 1 (Legion)

                byte slot = (byte)currentSlotTeam1++;
                createMatch.WriteInt8(slot);
                createMatch.WriteInt8((byte)group.Members.Count);
                createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
                createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
                createMatch.WriteInt8(0);
                createMatch.WriteInt8(0);
                createMatch.WriteInt8(0);
            }
        }
        else
        {
            // Team 1
            foreach (MatchmakingGroup group in team1Groups)
            foreach (MatchmakingGroupMember member in group.Members)
            {
                createMatch.WriteInt32(member.Account.ID);
                createMatch.WriteInt8(1); // Team 1 (Legion)

                byte slot = (byte)currentSlotTeam1++;
                createMatch.WriteInt8(slot);
                createMatch.WriteInt8((byte)group.Members.Count);
                createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
                createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
                createMatch.WriteInt8(0);
                createMatch.WriteInt8(0);
                createMatch.WriteInt8(0);
            }

            // Team 2
            foreach (MatchmakingGroup group in team2Groups)
            foreach (MatchmakingGroupMember member in group.Members)
            {
                createMatch.WriteInt32(member.Account.ID);
                createMatch.WriteInt8(2); // Team 2 (Hellbourne)

                byte slot = (byte)currentSlotTeam2++;
                createMatch.WriteInt8(slot);
                createMatch.WriteInt8((byte)group.Members.Count);
                createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
                createMatch.WriteInt32(BitConverter.SingleToInt32Bits(10.0f));
                createMatch.WriteInt8(0);
                createMatch.WriteInt8(0);
                createMatch.WriteInt8(0);
            }
        }

        createMatch.WriteInt32(0); // GroupIds Count (Empty for now)

        matchServer.Send(createMatch);

        Log.Information("Pending Match Created. MatchUpID: {MatchUpID}. Waiting for Game Server Confirmation...",
            matchUpId);

        return true;
    }

    public static double CalculateEffectiveGroupRating(MatchmakingGroup group, double skewFactor = 0.15)
    {
        if (group.Members.Count == 0) return 0.0;
        if (group.Members.Count == 1) return group.Members[0].Rating;

        double avg = group.Members.Average(m => m.Rating);
        double min = group.Members.Min(m => m.Rating);
        double max = group.Members.Max(m => m.Rating);
        double spread = max - min;

        return avg + (skewFactor * spread);
    }

    public static double GetDynamicRatingSpread(
        MatchmakingGroup group,
        DateTimeOffset now,
        double initialSpread = 50.0,
        double expansionRatePerTenSec = 6.9444,
        double maxSpread = 175.0,
        double disparityThreshold = 75.0,
        double maxDisparityDelaySeconds = 60.0)
    {
        if (group.Information.GameType is ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL
            or ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL)
        {
            return 10000.0;
        }

        if (group.QueueStartTime == null) return initialSpread;

        double durationSeconds = (now - group.QueueStartTime.Value).TotalSeconds;

        // Apply disparity queue friction for high-gap premade stacks
        double delaySeconds = 0.0;
        if (group.Members.Count > 1)
        {
            double internalSpread = group.Members.Max(m => m.Rating) - group.Members.Min(m => m.Rating);
            if (internalSpread > disparityThreshold)
            {
                // Delay expansion by 15s per 100 MMR gap beyond threshold, capped at 60s
                delaySeconds = Math.Min(maxDisparityDelaySeconds, (internalSpread - disparityThreshold) / 100.0 * 15.0);
            }
        }

        double effectiveDuration = Math.Max(0.0, durationSeconds - delaySeconds);
        double expanded = initialSpread + (effectiveDuration / 10.0 * expansionRatePerTenSec);
        return Math.Min(maxSpread, Math.Max(initialSpread, expanded));
    }

    public static bool CanCombineGroupsWithinSpread(
        List<MatchmakingGroup> currentMatch,
        MatchmakingGroup candidate,
        DateTimeOffset now,
        double initialSpread = 50.0,
        double expansionRatePerTenSec = 6.9444,
        double maxSpread = 175.0,
        double skewFactor = 0.15)
    {
        List<double> groupRatings =
        [
            .. currentMatch
                .Concat([candidate])
                .Where(g => g.Members.Count > 0)
                .Select(g => CalculateEffectiveGroupRating(g, skewFactor))
        ];

        if (groupRatings.Count == 0) return true;

        double minRating = groupRatings.Min();
        double maxRating = groupRatings.Max();
        double currentSpread = maxRating - minRating;

        double allowedSpread = currentMatch
            .Concat([candidate])
            .Max(g => GetDynamicRatingSpread(g, now, initialSpread, expansionRatePerTenSec, maxSpread));

        return currentSpread <= allowedSpread;
    }

    public static bool HasBanConflict(MatchmakingGroup g1, MatchmakingGroup g2)
    {
        foreach (MatchmakingGroupMember m1 in g1.Members)
        {
            foreach (MatchmakingGroupMember m2 in g2.Members)
            {
                if (m1.Account.HasBannedPeer(m2.Account.ID) || m2.Account.HasBannedPeer(m1.Account.ID))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool CanCombineGroupsWithoutInternalBanConflict(List<MatchmakingGroup> current, MatchmakingGroup candidate, int teamSize)
    {
        List<MatchmakingGroup> combined = [.. current, candidate];
        return TryPartitionTeams(combined, Math.Max(1, teamSize), out _, out _);
    }

    public static bool TryPartitionTeams(
        List<MatchmakingGroup> groups,
        int teamSize,
        out List<MatchmakingGroup> bestTeam1,
        out List<MatchmakingGroup> bestTeam2)
    {
        bestTeam1 = [];
        bestTeam2 = [];

        int totalPlayers = groups.Sum(g => g.Members.Count);
        if (totalPlayers == 0) return true;

        // Check if any single group internally has mutually banned players
        foreach (MatchmakingGroup g in groups)
        {
            for (int i = 0; i < g.Members.Count; i++)
            {
                for (int j = i + 1; j < g.Members.Count; j++)
                {
                    if (g.Members[i].Account.HasBannedPeer(g.Members[j].Account.ID) ||
                        g.Members[j].Account.HasBannedPeer(g.Members[i].Account.ID))
                    {
                        return false;
                    }
                }
            }
        }

        int n = groups.Count;
        if (n == 1)
        {
            if (groups[0].Members.Count <= teamSize)
            {
                bestTeam1 = [groups[0]];
                bestTeam2 = [];
                return true;
            }
            return false;
        }

        int maxMask = 1 << n;
        double bestMmrDiff = double.MaxValue;
        bool foundValid = false;

        double[] groupMmrs = new double[n];
        int[] groupCounts = new int[n];
        for (int i = 0; i < n; i++)
        {
            groupCounts[i] = groups[i].Members.Count;
            groupMmrs[i] = CalculateEffectiveGroupRating(groups[i]) * groupCounts[i];
        }

        for (int mask = 1; mask < maxMask - 1; mask++)
        {
            int t1Count = 0;
            int t2Count = 0;
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0)
                    t1Count += groupCounts[i];
                else
                    t2Count += groupCounts[i];
            }

            if (t1Count > teamSize || t2Count > teamSize)
                continue;

            // When full match is forming (e.g. totalPlayers == 2 * teamSize), require equal team sizes
            if (totalPlayers == 2 * teamSize && t1Count != teamSize)
                continue;

            if (Math.Abs(t1Count - t2Count) > 1 && totalPlayers % 2 == 0)
                continue;

            // Check ban conflicts in Team 1
            bool conflict = false;
            for (int i = 0; i < n && !conflict; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                for (int j = i + 1; j < n && !conflict; j++)
                {
                    if ((mask & (1 << j)) == 0) continue;
                    if (HasBanConflict(groups[i], groups[j]))
                        conflict = true;
                }
            }
            if (conflict) continue;

            // Check ban conflicts in Team 2
            for (int i = 0; i < n && !conflict; i++)
            {
                if ((mask & (1 << i)) != 0) continue;
                for (int j = i + 1; j < n && !conflict; j++)
                {
                    if ((mask & (1 << j)) != 0) continue;
                    if (HasBanConflict(groups[i], groups[j]))
                        conflict = true;
                }
            }
            if (conflict) continue;

            // Valid partition! Calculate MMR disparity
            double t1Mmr = 0;
            double t2Mmr = 0;
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0)
                    t1Mmr += groupMmrs[i];
                else
                    t2Mmr += groupMmrs[i];
            }

            double mmrDiff = Math.Abs(t1Mmr - t2Mmr);
            if (!foundValid || mmrDiff < bestMmrDiff)
            {
                bestMmrDiff = mmrDiff;
                foundValid = true;

                bestTeam1 = [];
                bestTeam2 = [];
                for (int i = 0; i < n; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        bestTeam1.Add(groups[i]);
                    else
                        bestTeam2.Add(groups[i]);
                }
            }
        }

        return foundValid;
    }
}

public sealed class PendingMatchState
{
    public required List<MatchmakingGroup> Groups { get; init; }
    public required List<MatchmakingGroup> Team1Groups { get; init; }
    public required List<MatchmakingGroup> Team2Groups { get; init; }
    public int AllocatedServerID { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? SlotLockKey { get; init; }
    public string? SlotLeaseToken { get; init; }
    public string? ResolvedMapName { get; init; }
}
