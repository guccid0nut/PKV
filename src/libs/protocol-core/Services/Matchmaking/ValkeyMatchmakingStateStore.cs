// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ASPIRE.Common.Services.Matchmaking;

/// <summary>
///     Serializable snapshot of an active matchmaking party/group stored in Valkey so any
///     stateless MatchmakingService pod can observe or recover queued groups after a pod restart.
/// </summary>
public sealed record DistributedGroupSnapshot(
    string GroupGuid,
    int LeaderAccountId,
    IReadOnlyList<int> MemberAccountIds,
    string MapName,
    int GroupType,
    int GameType,
    IReadOnlyList<string> GameModes,
    IReadOnlyList<string> GameRegions,
    DateTimeOffset? QueueStartTimeUtc,
    string OwnerPodId,
    DateTimeOffset UpdatedAtUtc,
    bool Ranked = true,
    string ClientVersion = "",
    byte MatchFidelity = 0,
    byte BotDifficulty = 0,
    bool RandomizeBots = false,
    byte TeamSize = 5);

/// <summary>
///     Serializable snapshot of a pending match waiting for dedicated game server confirmation.
/// </summary>
public sealed record DistributedPendingMatchSnapshot(
    int MatchUpId,
    int AllocatedServerId,
    string? ResolvedMapName,
    IReadOnlyList<string> GroupGuids,
    IReadOnlyList<int> MemberAccountIds,
    string? SlotLockKey,
    string? SlotLeaseToken,
    string OwnerPodId,
    DateTimeOffset CreatedAtUtc,
    string Status = "PENDING",
    byte MatchType = 10,
    int GroupType = 0,
    int GameType = 0,
    IReadOnlyList<string>? GameModes = null,
    IReadOnlyList<string>? GameRegions = null,
    int TeamSize = 0,
    bool Ranked = true,
    byte BotDifficulty = 0,
    bool RandomizeBots = false);

/// <summary>
///     Per-group membership & queue priority metadata carried inside a distributed ready-check snapshot
///     so failover pods can eject offending parties and restore compliant parties accurately.
/// </summary>
public sealed record DistributedReadyCheckGroupMembership(
    string GroupGuid,
    IReadOnlyList<int> MemberAccountIds,
    DateTimeOffset? OriginalQueueStartTimeUtc = null);

/// <summary>
///     Serializable snapshot of a cluster-wide ready check across multiple ChatServer pods.
/// </summary>
public sealed record DistributedReadyCheckSnapshot(
    int ReadyCheckId,
    IReadOnlyList<string> GroupGuids,
    IReadOnlyList<int> RequiredAccountIds,
    IReadOnlyList<int> AcceptedAccountIds,
    int? DeclinedByAccountId,
    string Status,
    string OwnerPodId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string MapName = "",
    string GameMode = "",
    int GroupType = 0,
    int GameType = 0,
    IReadOnlyList<string>? GameModes = null,
    IReadOnlyList<string>? GameRegions = null,
    bool Ranked = true,
    string ClientVersion = "",
    byte MatchFidelity = 0,
    byte BotDifficulty = 0,
    bool RandomizeBots = false,
    IReadOnlyList<DistributedReadyCheckGroupMembership>? RequiredMemberGroups = null);

public enum DistributedReadyCheckVoteOutcome
{
    NotFound = 0,
    AlreadyResolved = 1,
    NotParticipant = 2,
    DuplicateVote = 3,
    Accepted = 4,
    QuorumReached = 5,
    Declined = 6,
    Expired = 7
}

public sealed record DistributedReadyCheckVoteResult(
    DistributedReadyCheckVoteOutcome Outcome,
    int ReadyCheckId,
    int AcceptedCount,
    int TotalPlayers,
    string Status,
    IReadOnlyList<int>? MissingAccountIds = null,
    string? Message = null);

public sealed record DistributedPendingMatchTransitionResult(
    bool Transitioned,
    string FinalStatus,
    DistributedPendingMatchSnapshot? Snapshot,
    string? Message = null);

public sealed record DistributedMatchmakingStreamEvent(
    string EntryId,
    string EventType,
    string PodId,
    string PayloadJson,
    long TimestampUnixMs);

public enum DistributedMatchmakingStoreFailureKind
{
    TransientConnectivity = 0,
    ClusterCrossSlotMisconfiguration = 1,
    RedisCommandError = 2,
    NotConnected = 3
}

/// <summary>
///     Explicit failure raised when a Valkey/Redis-backed matchmaking store encounters a cluster
///     misconfiguration (e.g. CROSSSLOT) or connectivity failure, rather than silently falling back
///     to pod-local memory and inducing multi-pod split-brain.
/// </summary>
public sealed class DistributedMatchmakingStoreException : Exception
{
    public DistributedMatchmakingStoreException()
        : base("Distributed matchmaking state store operation failed.")
    {
        Operation = "unknown";
        PodId = "unknown";
        FailureKind = DistributedMatchmakingStoreFailureKind.RedisCommandError;
    }

    public DistributedMatchmakingStoreException(string message)
        : base(message)
    {
        Operation = "unknown";
        PodId = "unknown";
        FailureKind = DistributedMatchmakingStoreFailureKind.RedisCommandError;
    }

    public DistributedMatchmakingStoreException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Operation = "unknown";
        PodId = "unknown";
        FailureKind = DistributedMatchmakingStoreFailureKind.RedisCommandError;
    }

    public DistributedMatchmakingStoreException(
        string operation,
        string podId,
        DistributedMatchmakingStoreFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        PodId = podId;
        FailureKind = failureKind;
    }

    public string Operation { get; }
    public string PodId { get; }
    public DistributedMatchmakingStoreFailureKind FailureKind { get; }
}

internal sealed record GroupRemovedPayload(
    string GroupGuid,
    string RemovedByPod);

internal sealed record PendingMatchConfirmedPayload(
    int MatchUpId,
    int RealMatchId,
    string ServerAddress,
    int ServerPort,
    string ConfirmedByPod);

internal sealed record PendingMatchExpiredPayload(
    int MatchUpId,
    string ExpiredByPod);

public sealed record MatchConfirmedDispatchPayload(
    int MatchUpId,
    int RealMatchId,
    byte MatchType,
    string ServerAddress,
    int ServerPort,
    string? LocalAddress,
    int? LocalPort,
    string ResolvedMapName,
    int GroupType,
    int GameType,
    int TeamSize,
    IReadOnlyList<string> GameModes,
    IReadOnlyList<string> GameRegions,
    IReadOnlyList<int> TargetAccountIds,
    string DispatchedByPod);

internal sealed record ReadyCheckVoteEventPayload(
    int ReadyCheckId,
    int? AccountId,
    int AcceptedCount,
    int TotalPlayers,
    int? MissingCount = null);

public sealed record MatchAllocationRequestedPayload(
    int ReadyCheckId,
    string MapName,
    string GameMode,
    int GroupType,
    int GameType,
    IReadOnlyList<string> GroupGuids,
    IReadOnlyList<int> RequiredAccountIds,
    string RequestedByPod);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(DistributedGroupSnapshot))]
[JsonSerializable(typeof(DistributedPendingMatchSnapshot))]
[JsonSerializable(typeof(DistributedReadyCheckGroupMembership))]
[JsonSerializable(typeof(DistributedReadyCheckSnapshot))]
[JsonSerializable(typeof(GroupRemovedPayload))]
[JsonSerializable(typeof(PendingMatchConfirmedPayload))]
[JsonSerializable(typeof(PendingMatchExpiredPayload))]
[JsonSerializable(typeof(MatchConfirmedDispatchPayload))]
[JsonSerializable(typeof(ReadyCheckVoteEventPayload))]
[JsonSerializable(typeof(MatchAllocationRequestedPayload))]
internal sealed partial class ValkeyMatchmakingJsonContext : JsonSerializerContext;

/// <summary>
///     Wave 3B: Valkey-backed distributed state store for stateless MatchmakingService pods.
///     Externalizes Groups, PendingMatches, ReadyChecks, and cluster-unique ID sequences with atomic
///     cluster-safe Lua transitions and an append-only Valkey Stream audit/event log (`{kongor:mm}:events`).
/// </summary>
public interface IDistributedMatchmakingStateStore
{
    string PodId { get; }
    bool IsDistributedActive { get; }

    Task<bool> TryVerifyStreamsCapabilityAsync(CancellationToken ct = default) =>
        Task.FromResult(IsDistributedActive);

    Task<int> AllocateNextMatchUpIdAsync(CancellationToken ct = default);
    Task<int> AllocateNextReadyCheckIdAsync(CancellationToken ct = default);

    Task UpsertGroupAsync(DistributedGroupSnapshot snapshot, CancellationToken ct = default);
    Task<bool> RemoveGroupAsync(string groupGuid, CancellationToken ct = default);
    Task<IReadOnlyList<DistributedGroupSnapshot>> ListActiveGroupsAsync(CancellationToken ct = default);

    Task<bool> TryRegisterPendingMatchAsync(
        DistributedPendingMatchSnapshot snapshot,
        TimeSpan ttl,
        CancellationToken ct = default);

    Task<DistributedPendingMatchSnapshot?> GetPendingMatchAsync(int matchUpId, CancellationToken ct = default);

    Task<DistributedPendingMatchTransitionResult> TryConfirmPendingMatchAsync(
        int matchUpId,
        int realMatchId,
        string serverAddress,
        int serverPort,
        CancellationToken ct = default);

    Task<DistributedPendingMatchTransitionResult> TryExpirePendingMatchAsync(
        int matchUpId,
        CancellationToken ct = default);

    Task PublishMatchConfirmedDispatchAsync(
        MatchConfirmedDispatchPayload payload,
        CancellationToken ct = default);

    Task PublishMatchAllocationRequestedAsync(
        MatchAllocationRequestedPayload payload,
        CancellationToken ct = default);

    Task<bool> TryCreateReadyCheckAsync(
        DistributedReadyCheckSnapshot snapshot,
        TimeSpan ttl,
        CancellationToken ct = default);

    Task<DistributedReadyCheckSnapshot?> GetReadyCheckAsync(int readyCheckId, CancellationToken ct = default);

    Task<int?> FindActiveReadyCheckIdForAccountAsync(int accountId, CancellationToken ct = default);

    Task<DistributedReadyCheckVoteResult> TrySubmitReadyCheckVoteAsync(
        int readyCheckId,
        int accountId,
        bool accepted,
        CancellationToken ct = default);

    Task<DistributedReadyCheckVoteResult> TryExpireReadyCheckAsync(
        int readyCheckId,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistributedMatchmakingStreamEvent>> ReadRecentEventsAsync(
        int count = 50,
        CancellationToken ct = default);
}

/// <summary>
///     Valkey 8.1 / Redis Cluster implementation of <see cref="IDistributedMatchmakingStateStore"/> using
///     hash-tagged keys (`{kongor:mm}:*`) for zero-CROSSSLOT single-round-trip atomic Lua scripts,
///     bounded Valkey Streams (`{kongor:mm}:events`), and strict error propagation when configured with Redis.
///     Local in-memory state is reserved strictly for explicit offline / unit-test mode (`redis == null`).
/// </summary>
public sealed class ValkeyMatchmakingStateStore : IDistributedMatchmakingStateStore
{
    public const string DefaultKeyPrefix = "{kongor:mm}";
    private const int StreamMaxLenApprox = 5000;
    private const int MatchUpIdBase = 100_000;
    private const int ReadyCheckIdBase = 200_000;
    private const long TombstoneTtlMs = 300_000; // 5 minutes tombstone to prevent duplicate confirmations

    // KEYS[1] = {prefix}:pending_matches hash, KEYS[2] = {prefix}:pending_match:{id}:state, KEYS[3] = {prefix}:events stream
    // ARGV[1] = matchUpId, ARGV[2] = snapshotJson, ARGV[3] = ttlMs, ARGV[4] = podId, ARGV[5] = nowUnixMs
    private const string RegisterPendingMatchLua = @"
        if redis.call('EXISTS', KEYS[2]) == 1 then
            return 0
        end
        if redis.call('HEXISTS', KEYS[1], ARGV[1]) == 1 then
            redis.call('HDEL', KEYS[1], ARGV[1])
        end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('SET', KEYS[2], 'PENDING', 'PX', ARGV[3])
        redis.call('XADD', KEYS[3], 'MAXLEN', '~', 5000, '*',
            'event_type', 'pending_match_registered',
            'pod_id', ARGV[4],
            'matchup_id', ARGV[1],
            'payload', ARGV[2],
            'ts_ms', ARGV[5])
        return 1";

    // KEYS[1] = {prefix}:pending_matches hash, KEYS[2] = {prefix}:pending_match:{id}:state, KEYS[3] = {prefix}:events stream
    // ARGV[1] = matchUpId, ARGV[2] = targetStatus ('CONFIRMED'|'EXPIRED'), ARGV[3] = eventType,
    // ARGV[4] = podId, ARGV[5] = eventPayloadJson, ARGV[6] = nowUnixMs, ARGV[7] = tombTtlMs
    private const string TransitionPendingMatchLua = @"
        local state = redis.call('GET', KEYS[2])
        local snap = redis.call('HGET', KEYS[1], ARGV[1])
        if not snap then
            if state then
                return {0, state, ''}
            end
            return {0, 'NOT_FOUND', ''}
        end
        if state ~= 'PENDING' then
            redis.call('HDEL', KEYS[1], ARGV[1])
            if not state then
                redis.call('SET', KEYS[2], 'EXPIRED', 'PX', ARGV[7])
                return {0, 'EXPIRED', snap}
            end
            return {0, state, snap}
        end
        redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[7])
        redis.call('XADD', KEYS[3], 'MAXLEN', '~', 5000, '*',
            'event_type', ARGV[3],
            'pod_id', ARGV[4],
            'matchup_id', ARGV[1],
            'payload', ARGV[5],
            'ts_ms', ARGV[6])
        return {1, ARGV[2], snap}";

    // KEYS[1] = {prefix}:readycheck:{id}:meta, KEYS[2] = {prefix}:readycheck:{id}:required,
    // KEYS[3] = {prefix}:readycheck:{id}:accepted, KEYS[4] = {prefix}:events stream,
    // KEYS[5] = {prefix}:account_readycheck hash
    // ARGV[1] = readyCheckId, ARGV[2] = ttlMs, ARGV[3] = podId, ARGV[4] = snapshotJson,
    // ARGV[5] = nowUnixMs, ARGV[6] = requiredCount,
    // ARGV[7..6+requiredCount] = requiredAccountIds, ARGV[7+requiredCount..#ARGV] = prePopulatedAcceptedAccountIds
    private const string CreateReadyCheckLua = @"
        if redis.call('EXISTS', KEYS[1]) == 1 then
            return 0
        end
        redis.call('DEL', KEYS[2], KEYS[3])
        local reqCount = tonumber(ARGV[6]) or 0
        for i = 7, 6 + reqCount do
            redis.call('SADD', KEYS[2], ARGV[i])
            redis.call('HSET', KEYS[5], ARGV[i], ARGV[1])
        end
        local totalPlayers = tonumber(redis.call('SCARD', KEYS[2]))
        for i = 7 + reqCount, #ARGV do
            if redis.call('SISMEMBER', KEYS[2], ARGV[i]) == 1 then
                redis.call('SADD', KEYS[3], ARGV[i])
            end
        end
        redis.call('HSET', KEYS[1],
            'status', 'OPEN',
            'total_players', tostring(totalPlayers),
            'owner_pod', ARGV[3],
            'snapshot', ARGV[4],
            'declined_by', '')
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[2])
        if redis.call('EXISTS', KEYS[3]) == 1 then
            redis.call('PEXPIRE', KEYS[3], ARGV[2])
        end
        redis.call('XADD', KEYS[4], 'MAXLEN', '~', 5000, '*',
            'event_type', 'readycheck_created',
            'pod_id', ARGV[3],
            'readycheck_id', ARGV[1],
            'payload', ARGV[4],
            'ts_ms', ARGV[5])
        return 1";

    // KEYS[1] = {prefix}:readycheck:{id}:meta, KEYS[2] = {prefix}:readycheck:{id}:required,
    // KEYS[3] = {prefix}:readycheck:{id}:accepted, KEYS[4] = {prefix}:events stream,
    // KEYS[5] = {prefix}:account_readycheck hash
    // ARGV[1] = readyCheckId, ARGV[2] = accountId, ARGV[3] = accepted ('1'|'0'), ARGV[4] = podId, ARGV[5] = nowUnixMs
    // Returns: { outcomeCode, acceptedCount, totalPlayers, status }
    // outcomeCode: 0=NotFound, 1=AlreadyResolved, 2=NotParticipant, 3=DuplicateVote, 4=Accepted, 5=QuorumReached, 6=Declined
    private const string SubmitReadyCheckVoteLua = @"
        local function clearAccountMappings()
            local members = redis.call('SMEMBERS', KEYS[2])
            for i = 1, #members do
                if redis.call('HGET', KEYS[5], members[i]) == ARGV[1] then
                    redis.call('HDEL', KEYS[5], members[i])
                end
            end
        end
        if redis.call('EXISTS', KEYS[1]) == 0 then
            if redis.call('HGET', KEYS[5], ARGV[2]) == ARGV[1] then
                redis.call('HDEL', KEYS[5], ARGV[2])
            end
            return {0, 0, 0, 'NOT_FOUND'}
        end
        local status = redis.call('HGET', KEYS[1], 'status') or 'UNKNOWN'
        local total = tonumber(redis.call('HGET', KEYS[1], 'total_players') or '0')
        local acceptedCount = tonumber(redis.call('SCARD', KEYS[3]))
        if status ~= 'OPEN' then
            return {1, acceptedCount, total, status}
        end
        if redis.call('SISMEMBER', KEYS[2], ARGV[2]) == 0 then
            return {2, acceptedCount, total, status}
        end
        local pttl = redis.call('PTTL', KEYS[1])
        if ARGV[3] == '0' then
            redis.call('HSET', KEYS[1], 'status', 'DECLINED', 'declined_by', ARGV[2])
            clearAccountMappings()
            redis.call('XADD', KEYS[4], 'MAXLEN', '~', 5000, '*',
                'event_type', 'readycheck_declined',
                'pod_id', ARGV[4],
                'readycheck_id', ARGV[1],
                'payload', '{""readyCheckId"":' .. ARGV[1] .. ',""accountId"":' .. ARGV[2] .. ',""acceptedCount"":' .. tostring(acceptedCount) .. ',""totalPlayers"":' .. tostring(total) .. '}',
                'ts_ms', ARGV[5])
            return {6, acceptedCount, total, 'DECLINED'}
        end
        local added = redis.call('SADD', KEYS[3], ARGV[2])
        if pttl > 0 then
            redis.call('PEXPIRE', KEYS[3], pttl)
        end
        acceptedCount = tonumber(redis.call('SCARD', KEYS[3]))
        if added == 0 then
            return {3, acceptedCount, total, status}
        end
        if acceptedCount >= total and total > 0 then
            redis.call('HSET', KEYS[1], 'status', 'QUORUM_REACHED')
            clearAccountMappings()
            redis.call('XADD', KEYS[4], 'MAXLEN', '~', 5000, '*',
                'event_type', 'readycheck_quorum_reached',
                'pod_id', ARGV[4],
                'readycheck_id', ARGV[1],
                'payload', '{""readyCheckId"":' .. ARGV[1] .. ',""acceptedCount"":' .. tostring(acceptedCount) .. ',""totalPlayers"":' .. tostring(total) .. '}',
                'ts_ms', ARGV[5])
            return {5, acceptedCount, total, 'QUORUM_REACHED'}
        end
        redis.call('XADD', KEYS[4], 'MAXLEN', '~', 5000, '*',
            'event_type', 'readycheck_vote_accepted',
            'pod_id', ARGV[4],
            'readycheck_id', ARGV[1],
            'payload', '{""readyCheckId"":' .. ARGV[1] .. ',""accountId"":' .. ARGV[2] .. ',""acceptedCount"":' .. tostring(acceptedCount) .. ',""totalPlayers"":' .. tostring(total) .. '}',
            'ts_ms', ARGV[5])
        return {4, acceptedCount, total, 'OPEN'}";

    // KEYS[1] = {prefix}:readycheck:{id}:meta, KEYS[2] = {prefix}:readycheck:{id}:required,
    // KEYS[3] = {prefix}:readycheck:{id}:accepted, KEYS[4] = {prefix}:events stream,
    // KEYS[5] = {prefix}:account_readycheck hash
    // ARGV[1] = readyCheckId, ARGV[2] = podId, ARGV[3] = nowUnixMs
    // Returns: { outcomeCode (7=Expired, 0=NotFound, 1=AlreadyResolved), acceptedCount, totalPlayers, status, missing... }
    private const string ExpireReadyCheckLua = @"
        if redis.call('EXISTS', KEYS[1]) == 0 then
            return {0, 0, 0, 'NOT_FOUND'}
        end
        local status = redis.call('HGET', KEYS[1], 'status') or 'UNKNOWN'
        local total = tonumber(redis.call('HGET', KEYS[1], 'total_players') or '0')
        local acceptedCount = tonumber(redis.call('SCARD', KEYS[3]))
        if status ~= 'OPEN' then
            return {1, acceptedCount, total, status}
        end
        redis.call('HSET', KEYS[1], 'status', 'EXPIRED')
        local members = redis.call('SMEMBERS', KEYS[2])
        for i = 1, #members do
            if redis.call('HGET', KEYS[5], members[i]) == ARGV[1] then
                redis.call('HDEL', KEYS[5], members[i])
            end
        end
        local missing = redis.call('SDIFF', KEYS[2], KEYS[3])
        redis.call('XADD', KEYS[4], 'MAXLEN', '~', 5000, '*',
            'event_type', 'readycheck_expired',
            'pod_id', ARGV[2],
            'readycheck_id', ARGV[1],
            'payload', '{""readyCheckId"":' .. ARGV[1] .. ',""acceptedCount"":' .. tostring(acceptedCount) .. ',""totalPlayers"":' .. tostring(total) .. ',""missingCount"":' .. tostring(#missing) .. '}',
            'ts_ms', ARGV[3])
        local out = {7, acceptedCount, total, 'EXPIRED'}
        for i = 1, #missing do
            table.insert(out, missing[i])
        end
        return out";

    private const int StreamsProbeUnverified = 0;
    private const int StreamsProbeVerified = 1;
    private const int StreamsProbeIncompatible = 2;

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<ValkeyMatchmakingStateStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;
    private int _streamsProbeState;
    private int _degradedToLocalMode;

    // In-memory atomic state for single-pod local mode (_redis is null or auto-degraded from Streams-incompatible cache).
    private readonly object _fallbackLock = new();
    private int _localNextMatchUpId = MatchUpIdBase;
    private int _localNextReadyCheckId = ReadyCheckIdBase;
    private readonly ConcurrentDictionary<string, DistributedGroupSnapshot> _localGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LocalPendingMatchEntry> _localPendingMatches = new();
    private readonly Dictionary<int, LocalReadyCheckEntry> _localReadyChecks = new();
    private readonly Dictionary<int, int> _localAccountReadyChecks = new();
    private readonly List<DistributedMatchmakingStreamEvent> _localEvents = [];

    private sealed class LocalPendingMatchEntry
    {
        public required DistributedPendingMatchSnapshot Snapshot { get; set; }
        public required string Status { get; set; }
        public required DateTimeOffset ExpiresAtUtc { get; set; }
    }

    private sealed class LocalReadyCheckEntry
    {
        public required DistributedReadyCheckSnapshot Snapshot { get; set; }
        public required HashSet<int> Required { get; init; }
        public required HashSet<int> Accepted { get; init; }
        public int? DeclinedBy { get; set; }
        public string Status { get; set; } = "OPEN";
        public required DateTimeOffset VoteDeadlineUtc { get; set; }
        public required DateTimeOffset TtlExpiresAtUtc { get; set; }
        public DateTimeOffset? RetentionUntilUtc { get; set; }
    }

    public ValkeyMatchmakingStateStore(
        ILogger<ValkeyMatchmakingStateStore> logger,
        IConnectionMultiplexer? redis = null,
        TimeProvider? timeProvider = null,
        string? podId = null,
        string? keyPrefix = null)
    {
        _logger = logger;
        _redis = redis;
        _timeProvider = timeProvider ?? TimeProvider.System;
        PodId = string.IsNullOrWhiteSpace(podId)
            ? $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..Math.Min(48, Environment.MachineName.Length + 20)]
            : podId.Trim();
        _keyPrefix = NormalizeKeyPrefix(keyPrefix);

        _logger.LogInformation(
            "[ValkeyMMState] Initialized store Pod={PodId} Mode={Mode} KeyPrefix='{KeyPrefix}' HashTag='{HashTag}'",
            PodId,
            _redis is null ? "OFFLINE_IN_MEMORY" : "VALKEY_DISTRIBUTED",
            _keyPrefix,
            ExtractRedisHashTag(_keyPrefix));
    }

    public string PodId { get; }

    public string KeyPrefix => _keyPrefix;

    private bool IsRedisBackplaneEnabled => _redis is not null && Volatile.Read(ref _degradedToLocalMode) == 0;

    public bool IsDistributedActive => IsRedisBackplaneEnabled && _redis!.IsConnected;

    public bool IsDegradedToLocalMode => Volatile.Read(ref _degradedToLocalMode) != 0;

    /// <summary>
    ///     Normalizes a Redis/Valkey key prefix so that all keys constructed under it share a single
    ///     non-empty Redis Cluster <c>{hash-tag}</c>, eliminating <c>CROSSSLOT</c> errors on multi-key Lua scripts.
    /// </summary>
    public static string NormalizeKeyPrefix(string? keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            return DefaultKeyPrefix;
        }

        string trimmed = keyPrefix.Trim().TrimEnd(':');
        if (trimmed.Length == 0)
        {
            return DefaultKeyPrefix;
        }

        int openBrace = trimmed.IndexOf('{', StringComparison.Ordinal);
        if (openBrace >= 0)
        {
            int closeBrace = trimmed.IndexOf('}', openBrace + 1);
            if (closeBrace > openBrace + 1)
            {
                return trimmed;
            }

            string sanitized = trimmed
                .Replace("{", string.Empty, StringComparison.Ordinal)
                .Replace("}", string.Empty, StringComparison.Ordinal)
                .Trim(':');
            return sanitized.Length == 0 ? DefaultKeyPrefix : $"{{{sanitized}}}";
        }

        return $"{{{trimmed}}}";
    }

    /// <summary>
    ///     Extracts the Redis Cluster hash-slot input substring (the content inside the first non-empty
    ///     <c>{...}</c> pair, or the entire key if no valid hash tag is present).
    /// </summary>
    public static string ExtractRedisHashTag(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        int openBrace = key.IndexOf('{', StringComparison.Ordinal);
        if (openBrace < 0)
        {
            return key;
        }

        int closeBrace = key.IndexOf('}', openBrace + 1);
        if (closeBrace <= openBrace + 1)
        {
            return key;
        }

        return key[(openBrace + 1)..closeBrace];
    }

    private string GroupsKey => $"{_keyPrefix}:groups";
    private string PendingMatchesKey => $"{_keyPrefix}:pending_matches";
    private string PendingMatchStateKey(int matchUpId) => $"{_keyPrefix}:pending_match:{matchUpId}:state";
    private string ReadyCheckMetaKey(int readyCheckId) => $"{_keyPrefix}:readycheck:{readyCheckId}:meta";
    private string ReadyCheckRequiredKey(int readyCheckId) => $"{_keyPrefix}:readycheck:{readyCheckId}:required";
    private string ReadyCheckAcceptedKey(int readyCheckId) => $"{_keyPrefix}:readycheck:{readyCheckId}:accepted";
    private string AccountReadyCheckKey => $"{_keyPrefix}:account_readycheck";
    private string MatchUpIdSequenceKey => $"{_keyPrefix}:seq:matchup_id";
    private string ReadyCheckIdSequenceKey => $"{_keyPrefix}:seq:readycheck_id";
    private string EventsStreamKey => $"{_keyPrefix}:events";

    private IDatabase GetConnectedDatabaseOrThrow(string operation, string context)
    {
        if (!_redis!.IsConnected)
        {
            _logger.LogError(
                "[VALKEY-MM-ERROR] Operation={Operation} Context={Context} Pod={PodId} FailureKind={FailureKind}: Valkey/Redis multiplexer is disconnected. Refusing silent local fallback to prevent multi-pod split-brain.",
                operation,
                context,
                PodId,
                DistributedMatchmakingStoreFailureKind.NotConnected);

            throw new DistributedMatchmakingStoreException(
                operation,
                PodId,
                DistributedMatchmakingStoreFailureKind.NotConnected,
                $"[VALKEY-MM-ERROR] {operation} ({context}) failed on pod '{PodId}': Valkey/Redis connection is disconnected (refusing silent local fallback).");
        }

        return _redis.GetDatabase();
    }

    private DistributedMatchmakingStoreException CreateAndLogRedisException(string operation, string context, Exception ex)
    {
        DistributedMatchmakingStoreFailureKind failureKind;
        if (ex is RedisServerException && ex.Message.Contains("CROSSSLOT", StringComparison.OrdinalIgnoreCase))
        {
            failureKind = DistributedMatchmakingStoreFailureKind.ClusterCrossSlotMisconfiguration;
            _logger.LogError(
                ex,
                "[VALKEY-MM-ERROR] Cluster CROSSSLOT misconfiguration during {Operation} ({Context}) on Pod={PodId} KeyPrefix='{KeyPrefix}'. All multi-key Lua keys must share a single {{hash-tag}}; refusing silent local fallback.",
                operation,
                context,
                PodId,
                _keyPrefix);
        }
        else if (ex is RedisConnectionException or RedisTimeoutException or TimeoutException)
        {
            failureKind = DistributedMatchmakingStoreFailureKind.TransientConnectivity;
            _logger.LogError(
                ex,
                "[VALKEY-MM-ERROR] Transient Valkey connectivity outage during {Operation} ({Context}) on Pod={PodId} Connected={IsConnected}. Refusing silent local fallback to prevent split-brain.",
                operation,
                context,
                PodId,
                _redis?.IsConnected ?? false);
        }
        else
        {
            failureKind = DistributedMatchmakingStoreFailureKind.RedisCommandError;
            _logger.LogError(
                ex,
                "[VALKEY-MM-ERROR] Valkey command error during {Operation} ({Context}) on Pod={PodId}. Refusing silent local fallback.",
                operation,
                context,
                PodId);
        }

        return new DistributedMatchmakingStoreException(
            operation,
            PodId,
            failureKind,
            $"[VALKEY-MM-ERROR] {operation} ({context}) failed on pod '{PodId}' ({failureKind}): {ex.Message}",
            ex);
    }

    private static int MapSequenceToSeededId(long rawSeq, int baseSeed)
    {
        long span = (long)int.MaxValue - baseSeed;
        long normalized = ((rawSeq - 1) % span + span) % span + 1;
        return baseSeed + (int)normalized;
    }

    private static void AdvanceLocalSequenceFloor(ref int target, int allocatedId)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (allocatedId <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, allocatedId, current) == current)
            {
                return;
            }
        }
    }

    private void MarkStreamsCapabilityVerified()
    {
        if (Volatile.Read(ref _streamsProbeState) == StreamsProbeUnverified)
        {
            Interlocked.CompareExchange(ref _streamsProbeState, StreamsProbeVerified, StreamsProbeUnverified);
        }
    }

    private bool IsUnsupportedStreamsException(Exception ex)
    {
        if (ex is not RedisServerException rse)
        {
            return false;
        }

        string msg = rse.Message ?? string.Empty;
        if (msg.Contains("CROSSSLOT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!msg.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool mentionsStreamCommand =
            msg.Contains("XADD", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("XINFO", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("XRANGE", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("XREVRANGE", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("XREAD", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("XLEN", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("XTRIM", StringComparison.OrdinalIgnoreCase);

        return mentionsStreamCommand || Volatile.Read(ref _streamsProbeState) != StreamsProbeVerified;
    }

    private void DegradeToLocalSinglePodMode(Exception ex, string operation, string context)
    {
        Volatile.Write(ref _streamsProbeState, StreamsProbeIncompatible);
        if (Interlocked.CompareExchange(ref _degradedToLocalMode, 1, 0) == 0)
        {
            _logger.LogCritical(
                ex,
                "[VALKEY-MM-INCOMPATIBLE] Connected cache server does not support Redis/Valkey Streams (e.g. Microsoft Garnet). Run: docker rm -f kongor-garnet-cache && docker compose -f infra/docker/docker-compose.infra.yml up -d valkey. Auto-degrading MatchmakingStateStore to single-pod local mode. (Pod={PodId}, Operation={Operation}, Context={Context})",
                PodId,
                operation,
                context);
        }
    }

    public async Task<bool> TryVerifyStreamsCapabilityAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsRedisBackplaneEnabled)
        {
            return false;
        }

        int probeState = Volatile.Read(ref _streamsProbeState);
        if (probeState == StreamsProbeVerified)
        {
            return true;
        }

        if (probeState == StreamsProbeIncompatible)
        {
            return false;
        }

        try
        {
            IDatabase db = GetConnectedDatabaseOrThrow(nameof(TryVerifyStreamsCapabilityAsync), EventsStreamKey);
            RedisResult? probeResult = await db.ExecuteAsync("XINFO", "STREAM", EventsStreamKey).ConfigureAwait(false);
            if (probeResult is not null && !probeResult.IsNull)
            {
                MarkStreamsCapabilityVerified();
                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} verified Valkey/Redis Streams capability via XINFO STREAM '{StreamKey}'.",
                    PodId,
                    EventsStreamKey);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DistributedMatchmakingStoreException)
        {
            throw;
        }
        catch (RedisServerException rse) when (rse.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            MarkStreamsCapabilityVerified();
            _logger.LogInformation(
                "[ValkeyMMState] Pod={PodId} verified Valkey/Redis Streams capability via XINFO STREAM '{StreamKey}' (stream not yet created).",
                PodId,
                EventsStreamKey);
            return true;
        }
        catch (Exception ex) when (IsUnsupportedStreamsException(ex))
        {
            DegradeToLocalSinglePodMode(ex, nameof(TryVerifyStreamsCapabilityAsync), EventsStreamKey);
            return false;
        }
        catch (Exception ex)
        {
            throw CreateAndLogRedisException(nameof(TryVerifyStreamsCapabilityAsync), EventsStreamKey, ex);
        }
    }

    public async Task<int> AllocateNextMatchUpIdAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(nameof(AllocateNextMatchUpIdAsync), MatchUpIdSequenceKey);
                long seq = await db.StringIncrementAsync(MatchUpIdSequenceKey).ConfigureAwait(false);
                int allocatedId = MapSequenceToSeededId(seq, MatchUpIdBase);
                AdvanceLocalSequenceFloor(ref _localNextMatchUpId, allocatedId);
                _logger.LogDebug(
                    "[ValkeyMMState] Pod={PodId} allocated distributed MatchUpId={MatchUpId} (Seq={Seq}, Key='{Key}')",
                    PodId, allocatedId, seq, MatchUpIdSequenceKey);
                return allocatedId;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(ex, nameof(AllocateNextMatchUpIdAsync), MatchUpIdSequenceKey);
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(nameof(AllocateNextMatchUpIdAsync), MatchUpIdSequenceKey, ex);
            }
        }

        int localId = Interlocked.Increment(ref _localNextMatchUpId);
        _logger.LogDebug("[ValkeyMMState] Pod={PodId} allocated offline MatchUpId={MatchUpId}", PodId, localId);
        return localId;
    }

    public async Task<int> AllocateNextReadyCheckIdAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(nameof(AllocateNextReadyCheckIdAsync), ReadyCheckIdSequenceKey);
                long seq = await db.StringIncrementAsync(ReadyCheckIdSequenceKey).ConfigureAwait(false);
                int allocatedId = MapSequenceToSeededId(seq, ReadyCheckIdBase);
                AdvanceLocalSequenceFloor(ref _localNextReadyCheckId, allocatedId);
                _logger.LogDebug(
                    "[ValkeyMMState] Pod={PodId} allocated distributed ReadyCheckId={ReadyCheckId} (Seq={Seq}, Key='{Key}')",
                    PodId, allocatedId, seq, ReadyCheckIdSequenceKey);
                return allocatedId;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(ex, nameof(AllocateNextReadyCheckIdAsync), ReadyCheckIdSequenceKey);
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(nameof(AllocateNextReadyCheckIdAsync), ReadyCheckIdSequenceKey, ex);
            }
        }

        int localId = Interlocked.Increment(ref _localNextReadyCheckId);
        _logger.LogDebug("[ValkeyMMState] Pod={PodId} allocated offline ReadyCheckId={ReadyCheckId}", PodId, localId);
        return localId;
    }

    public async Task UpsertGroupAsync(DistributedGroupSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);

        string json = JsonSerializer.Serialize(snapshot, ValkeyMatchmakingJsonContext.Default.DistributedGroupSnapshot);
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(nameof(UpsertGroupAsync), snapshot.GroupGuid);
                await db.HashSetAsync(GroupsKey, snapshot.GroupGuid, json)
                    .ConfigureAwait(false);
                await db.StreamAddAsync(
                    EventsStreamKey,
                    [
                        new NameValueEntry("event_type", "group_upserted"),
                        new NameValueEntry("pod_id", PodId),
                        new NameValueEntry("group_guid", snapshot.GroupGuid),
                        new NameValueEntry("payload", json),
                        new NameValueEntry("ts_ms", nowMs.ToString(CultureInfo.InvariantCulture))
                    ],
                    maxLength: StreamMaxLenApprox,
                    useApproximateMaxLength: true).ConfigureAwait(false);
                MarkStreamsCapabilityVerified();

                _logger.LogDebug(
                    "[ValkeyMMState] Pod={PodId} upserted Group={GroupGuid} (Members={MemberCount}, Map={MapName}, Queued={Queued}, Ranked={Ranked})",
                    PodId, snapshot.GroupGuid, snapshot.MemberAccountIds.Count, snapshot.MapName, snapshot.QueueStartTimeUtc.HasValue, snapshot.Ranked);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(ex, nameof(UpsertGroupAsync), snapshot.GroupGuid);
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(nameof(UpsertGroupAsync), snapshot.GroupGuid, ex);
            }
        }

        _localGroups[snapshot.GroupGuid] = snapshot;
        AppendLocalEvent("group_upserted", json, nowMs);
    }

    public async Task<bool> RemoveGroupAsync(string groupGuid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(groupGuid);

        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        string removedPayloadJson = JsonSerializer.Serialize(
            new GroupRemovedPayload(groupGuid, PodId),
            ValkeyMatchmakingJsonContext.Default.GroupRemovedPayload);

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(nameof(RemoveGroupAsync), groupGuid);
                bool removed = await db.HashDeleteAsync(GroupsKey, groupGuid)
                    .ConfigureAwait(false);
                if (removed)
                {
                    await db.StreamAddAsync(
                        EventsStreamKey,
                        [
                            new NameValueEntry("event_type", "group_removed"),
                            new NameValueEntry("pod_id", PodId),
                            new NameValueEntry("group_guid", groupGuid),
                            new NameValueEntry("payload", removedPayloadJson),
                            new NameValueEntry("ts_ms", nowMs.ToString(CultureInfo.InvariantCulture))
                        ],
                        maxLength: StreamMaxLenApprox,
                        useApproximateMaxLength: true).ConfigureAwait(false);
                    MarkStreamsCapabilityVerified();
                    _logger.LogDebug("[ValkeyMMState] Pod={PodId} removed Group={GroupGuid} from Valkey.", PodId, groupGuid);
                }
                return removed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(ex, nameof(RemoveGroupAsync), groupGuid);
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(nameof(RemoveGroupAsync), groupGuid, ex);
            }
        }

        bool localRemoved = _localGroups.TryRemove(groupGuid, out _);
        if (localRemoved)
        {
            AppendLocalEvent("group_removed", removedPayloadJson, nowMs);
        }
        return localRemoved;
    }

    public async Task<IReadOnlyList<DistributedGroupSnapshot>> ListActiveGroupsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsRedisBackplaneEnabled && await TryVerifyStreamsCapabilityAsync(ct).ConfigureAwait(false))
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(nameof(ListActiveGroupsAsync), GroupsKey);
                HashEntry[] entries = await db.HashGetAllAsync(GroupsKey).ConfigureAwait(false);
                List<DistributedGroupSnapshot> results = new(entries.Length);
                foreach (HashEntry entry in entries)
                {
                    if (!entry.Value.HasValue) continue;
                    DistributedGroupSnapshot? snap = JsonSerializer.Deserialize(
                        entry.Value.ToString(), ValkeyMatchmakingJsonContext.Default.DistributedGroupSnapshot);
                    if (snap is not null)
                    {
                        results.Add(snap);
                    }
                }
                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(ex, nameof(ListActiveGroupsAsync), GroupsKey);
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(nameof(ListActiveGroupsAsync), GroupsKey, ex);
            }
        }

        return [.. _localGroups.Values];
    }

    public async Task<bool> TryRegisterPendingMatchAsync(
        DistributedPendingMatchSnapshot snapshot,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);

        long ttlMs = Math.Max(1000L, (long)ttl.TotalMilliseconds);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        long nowMs = now.ToUnixTimeMilliseconds();
        string json = JsonSerializer.Serialize(snapshot, ValkeyMatchmakingJsonContext.Default.DistributedPendingMatchSnapshot);

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(TryRegisterPendingMatchAsync),
                    snapshot.MatchUpId.ToString(CultureInfo.InvariantCulture));
                RedisResult res = await db.ScriptEvaluateAsync(
                    RegisterPendingMatchLua,
                    [
                        new RedisKey(PendingMatchesKey),
                        new RedisKey(PendingMatchStateKey(snapshot.MatchUpId)),
                        new RedisKey(EventsStreamKey)
                    ],
                    [
                        new RedisValue(snapshot.MatchUpId.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(json),
                        new RedisValue(ttlMs.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(PodId),
                        new RedisValue(nowMs.ToString(CultureInfo.InvariantCulture))
                    ]).ConfigureAwait(false);
                if (!res.IsNull)
                {
                    MarkStreamsCapabilityVerified();
                }

                bool registered = res.ToString() == "1";
                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} TryRegisterPendingMatch MatchUpId={MatchUpId} ServerId={ServerId} MatchType={MatchType} -> {Result}",
                    PodId, snapshot.MatchUpId, snapshot.AllocatedServerId, snapshot.MatchType, registered ? "REGISTERED" : "ALREADY_EXISTS");
                return registered;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(TryRegisterPendingMatchAsync),
                    snapshot.MatchUpId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(TryRegisterPendingMatchAsync),
                    snapshot.MatchUpId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        lock (_fallbackLock)
        {
            PruneLocalPendingMatchesLocked(now);
            if (_localPendingMatches.TryGetValue(snapshot.MatchUpId, out LocalPendingMatchEntry? existing) &&
                existing.ExpiresAtUtc > now)
            {
                return false;
            }

            _localPendingMatches[snapshot.MatchUpId] = new LocalPendingMatchEntry
            {
                Snapshot = snapshot,
                Status = "PENDING",
                ExpiresAtUtc = now.AddMilliseconds(ttlMs)
            };
            AppendLocalEventLocked("pending_match_registered", json, nowMs);
            return true;
        }
    }

    public async Task<DistributedPendingMatchSnapshot?> GetPendingMatchAsync(int matchUpId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(GetPendingMatchAsync),
                    matchUpId.ToString(CultureInfo.InvariantCulture));
                RedisValue stateVal = await db.StringGetAsync(PendingMatchStateKey(matchUpId)).ConfigureAwait(false);
                if (!stateVal.HasValue || !string.Equals(stateVal.ToString(), "PENDING", StringComparison.Ordinal))
                {
                    await db.HashDeleteAsync(PendingMatchesKey, matchUpId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return null;
                }

                RedisValue val = await db.HashGetAsync(PendingMatchesKey, matchUpId.ToString(CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                if (!val.HasValue) return null;
                return JsonSerializer.Deserialize(val.ToString(), ValkeyMatchmakingJsonContext.Default.DistributedPendingMatchSnapshot);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(GetPendingMatchAsync),
                    matchUpId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(GetPendingMatchAsync),
                    matchUpId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_fallbackLock)
        {
            PruneLocalPendingMatchesLocked(now);
            return _localPendingMatches.TryGetValue(matchUpId, out LocalPendingMatchEntry? entry) &&
                   entry.Status == "PENDING" &&
                   entry.ExpiresAtUtc > now
                ? entry.Snapshot
                : null;
        }
    }

    public Task<DistributedPendingMatchTransitionResult> TryConfirmPendingMatchAsync(
        int matchUpId,
        int realMatchId,
        string serverAddress,
        int serverPort,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string payload = JsonSerializer.Serialize(
            new PendingMatchConfirmedPayload(matchUpId, realMatchId, serverAddress, serverPort, PodId),
            ValkeyMatchmakingJsonContext.Default.PendingMatchConfirmedPayload);

        return TransitionPendingMatchCoreAsync(matchUpId, "CONFIRMED", "pending_match_confirmed", payload, ct);
    }

    public Task<DistributedPendingMatchTransitionResult> TryExpirePendingMatchAsync(
        int matchUpId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string payload = JsonSerializer.Serialize(
            new PendingMatchExpiredPayload(matchUpId, PodId),
            ValkeyMatchmakingJsonContext.Default.PendingMatchExpiredPayload);

        return TransitionPendingMatchCoreAsync(matchUpId, "EXPIRED", "pending_match_expired", payload, ct);
    }

    public async Task PublishMatchConfirmedDispatchAsync(
        MatchConfirmedDispatchPayload payload,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        string json = JsonSerializer.Serialize(payload, ValkeyMatchmakingJsonContext.Default.MatchConfirmedDispatchPayload);

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(PublishMatchConfirmedDispatchAsync),
                    payload.MatchUpId.ToString(CultureInfo.InvariantCulture));
                await db.StreamAddAsync(
                    EventsStreamKey,
                    [
                        new NameValueEntry("event_type", "MATCH_CONFIRMED_DISPATCH"),
                        new NameValueEntry("pod_id", PodId),
                        new NameValueEntry("matchup_id", payload.MatchUpId.ToString(CultureInfo.InvariantCulture)),
                        new NameValueEntry("match_id", payload.RealMatchId.ToString(CultureInfo.InvariantCulture)),
                        new NameValueEntry("payload", json),
                        new NameValueEntry("ts_ms", nowMs.ToString(CultureInfo.InvariantCulture))
                    ],
                    maxLength: StreamMaxLenApprox,
                    useApproximateMaxLength: true).ConfigureAwait(false);
                MarkStreamsCapabilityVerified();

                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} published MATCH_CONFIRMED_DISPATCH MatchUpId={MatchUpId} -> MatchId={MatchId} MatchType={MatchType} RemoteTargets={TargetCount}",
                    PodId, payload.MatchUpId, payload.RealMatchId, payload.MatchType, payload.TargetAccountIds.Count);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(PublishMatchConfirmedDispatchAsync),
                    payload.MatchUpId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(PublishMatchConfirmedDispatchAsync),
                    payload.MatchUpId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        AppendLocalEvent("MATCH_CONFIRMED_DISPATCH", json, nowMs);
        _logger.LogInformation(
            "[ValkeyMMState] Pod={PodId} published offline MATCH_CONFIRMED_DISPATCH MatchUpId={MatchUpId} -> MatchId={MatchId} RemoteTargets={TargetCount}",
            PodId, payload.MatchUpId, payload.RealMatchId, payload.TargetAccountIds.Count);
    }

    public async Task PublishMatchAllocationRequestedAsync(
        MatchAllocationRequestedPayload payload,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        string json = JsonSerializer.Serialize(payload, ValkeyMatchmakingJsonContext.Default.MatchAllocationRequestedPayload);

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(PublishMatchAllocationRequestedAsync),
                    payload.ReadyCheckId.ToString(CultureInfo.InvariantCulture));
                await db.StreamAddAsync(
                    EventsStreamKey,
                    [
                        new NameValueEntry("event_type", "MATCH_ALLOCATION_REQUESTED"),
                        new NameValueEntry("pod_id", PodId),
                        new NameValueEntry("readycheck_id", payload.ReadyCheckId.ToString(CultureInfo.InvariantCulture)),
                        new NameValueEntry("payload", json),
                        new NameValueEntry("ts_ms", nowMs.ToString(CultureInfo.InvariantCulture))
                    ],
                    maxLength: StreamMaxLenApprox,
                    useApproximateMaxLength: true).ConfigureAwait(false);
                MarkStreamsCapabilityVerified();

                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} published MATCH_ALLOCATION_REQUESTED ReadyCheckId={ReadyCheckId} Map={MapName} Players={Players}",
                    PodId, payload.ReadyCheckId, payload.MapName, payload.RequiredAccountIds.Count);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(PublishMatchAllocationRequestedAsync),
                    payload.ReadyCheckId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(PublishMatchAllocationRequestedAsync),
                    payload.ReadyCheckId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        AppendLocalEvent("MATCH_ALLOCATION_REQUESTED", json, nowMs);
    }

    private async Task<DistributedPendingMatchTransitionResult> TransitionPendingMatchCoreAsync(
        int matchUpId,
        string targetStatus,
        string eventType,
        string eventPayloadJson,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        long nowMs = now.ToUnixTimeMilliseconds();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(TransitionPendingMatchCoreAsync),
                    $"{matchUpId}->{targetStatus}");
                RedisResult raw = await db.ScriptEvaluateAsync(
                    TransitionPendingMatchLua,
                    [
                        new RedisKey(PendingMatchesKey),
                        new RedisKey(PendingMatchStateKey(matchUpId)),
                        new RedisKey(EventsStreamKey)
                    ],
                    [
                        new RedisValue(matchUpId.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(targetStatus),
                        new RedisValue(eventType),
                        new RedisValue(PodId),
                        new RedisValue(eventPayloadJson),
                        new RedisValue(nowMs.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(TombstoneTtlMs.ToString(CultureInfo.InvariantCulture))
                    ]).ConfigureAwait(false);
                if (!raw.IsNull)
                {
                    MarkStreamsCapabilityVerified();
                }

                RedisResult[] arr = (RedisResult[])raw!;
                bool transitioned = arr[0].ToString() == "1";
                string finalStatus = arr[1].ToString() ?? "UNKNOWN";
                string snapJson = arr[2].ToString() ?? string.Empty;
                DistributedPendingMatchSnapshot? snap = string.IsNullOrEmpty(snapJson)
                    ? null
                    : JsonSerializer.Deserialize(snapJson, ValkeyMatchmakingJsonContext.Default.DistributedPendingMatchSnapshot);

                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} PendingMatch {MatchUpId} -> {TargetStatus}: Transitioned={Transitioned}, FinalStatus={FinalStatus}",
                    PodId, matchUpId, targetStatus, transitioned, finalStatus);

                return new DistributedPendingMatchTransitionResult(
                    transitioned,
                    finalStatus,
                    snap,
                    transitioned ? $"Atomically transitioned to {finalStatus}" : $"Skipped: already {finalStatus}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(TransitionPendingMatchCoreAsync),
                    $"{matchUpId}->{targetStatus}");
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(TransitionPendingMatchCoreAsync),
                    $"{matchUpId}->{targetStatus}",
                    ex);
            }
        }

        lock (_fallbackLock)
        {
            PruneLocalPendingMatchesLocked(now);
            if (!_localPendingMatches.TryGetValue(matchUpId, out LocalPendingMatchEntry? entry))
            {
                return new DistributedPendingMatchTransitionResult(false, "NOT_FOUND", null, "Not found in offline store");
            }

            if (entry.Status != "PENDING")
            {
                return new DistributedPendingMatchTransitionResult(false, entry.Status, entry.Snapshot, $"Already {entry.Status}");
            }

            if (now >= entry.ExpiresAtUtc)
            {
                entry.Status = "EXPIRED";
                entry.Snapshot = entry.Snapshot with { Status = "EXPIRED" };
                entry.ExpiresAtUtc = now.AddMilliseconds(TombstoneTtlMs);
                return new DistributedPendingMatchTransitionResult(false, "EXPIRED", entry.Snapshot, "Pending match TTL expired");
            }

            DistributedPendingMatchSnapshot original = entry.Snapshot;
            entry.Snapshot = original with { Status = targetStatus };
            entry.Status = targetStatus;
            entry.ExpiresAtUtc = now.AddMilliseconds(TombstoneTtlMs);
            AppendLocalEventLocked(eventType, eventPayloadJson, nowMs);
            return new DistributedPendingMatchTransitionResult(true, targetStatus, original, $"Transitioned to {targetStatus}");
        }
    }

    public async Task<bool> TryCreateReadyCheckAsync(
        DistributedReadyCheckSnapshot snapshot,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);

        long ttlMs = Math.Max(1000L, (long)ttl.TotalMilliseconds);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        long nowMs = now.ToUnixTimeMilliseconds();
        string json = JsonSerializer.Serialize(snapshot, ValkeyMatchmakingJsonContext.Default.DistributedReadyCheckSnapshot);

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(TryCreateReadyCheckAsync),
                    snapshot.ReadyCheckId.ToString(CultureInfo.InvariantCulture));
                int reqCount = snapshot.RequiredAccountIds.Count;
                int preAcceptedCount = snapshot.AcceptedAccountIds.Count;
                RedisValue[] args = new RedisValue[6 + reqCount + preAcceptedCount];
                args[0] = snapshot.ReadyCheckId.ToString(CultureInfo.InvariantCulture);
                args[1] = ttlMs.ToString(CultureInfo.InvariantCulture);
                args[2] = PodId;
                args[3] = json;
                args[4] = nowMs.ToString(CultureInfo.InvariantCulture);
                args[5] = reqCount.ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < reqCount; i++)
                {
                    args[6 + i] = snapshot.RequiredAccountIds[i].ToString(CultureInfo.InvariantCulture);
                }
                for (int i = 0; i < preAcceptedCount; i++)
                {
                    args[6 + reqCount + i] = snapshot.AcceptedAccountIds[i].ToString(CultureInfo.InvariantCulture);
                }

                RedisResult res = await db.ScriptEvaluateAsync(
                    CreateReadyCheckLua,
                    [
                        new RedisKey(ReadyCheckMetaKey(snapshot.ReadyCheckId)),
                        new RedisKey(ReadyCheckRequiredKey(snapshot.ReadyCheckId)),
                        new RedisKey(ReadyCheckAcceptedKey(snapshot.ReadyCheckId)),
                        new RedisKey(EventsStreamKey),
                        new RedisKey(AccountReadyCheckKey)
                    ],
                    args).ConfigureAwait(false);
                if (!res.IsNull)
                {
                    MarkStreamsCapabilityVerified();
                }

                bool created = res.ToString() == "1";
                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} TryCreateReadyCheck Id={ReadyCheckId} Required={Required} PreAccepted={PreAccepted} TTL={TtlMs}ms -> {Result}",
                    PodId, snapshot.ReadyCheckId, reqCount, preAcceptedCount, ttlMs, created ? "CREATED" : "ALREADY_EXISTS");
                return created;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(TryCreateReadyCheckAsync),
                    snapshot.ReadyCheckId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(TryCreateReadyCheckAsync),
                    snapshot.ReadyCheckId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        lock (_fallbackLock)
        {
            PruneLocalReadyChecksLocked(now);
            if (_localReadyChecks.ContainsKey(snapshot.ReadyCheckId))
            {
                return false;
            }

            HashSet<int> requiredSet = [.. snapshot.RequiredAccountIds];
            HashSet<int> acceptedSet = [.. snapshot.AcceptedAccountIds.Where(requiredSet.Contains)];
            DateTimeOffset ttlExpiry = now.AddMilliseconds(ttlMs);
            DateTimeOffset voteDeadline = snapshot.ExpiresAtUtc != default && snapshot.ExpiresAtUtc < ttlExpiry
                ? snapshot.ExpiresAtUtc
                : ttlExpiry;

            _localReadyChecks[snapshot.ReadyCheckId] = new LocalReadyCheckEntry
            {
                Snapshot = snapshot,
                Required = requiredSet,
                Accepted = acceptedSet,
                DeclinedBy = snapshot.DeclinedByAccountId,
                Status = "OPEN",
                VoteDeadlineUtc = voteDeadline,
                TtlExpiresAtUtc = ttlExpiry
            };

            foreach (int accountId in requiredSet)
            {
                _localAccountReadyChecks[accountId] = snapshot.ReadyCheckId;
            }

            AppendLocalEventLocked("readycheck_created", json, nowMs);
            return true;
        }
    }

    public async Task<DistributedReadyCheckSnapshot?> GetReadyCheckAsync(int readyCheckId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(GetReadyCheckAsync),
                    readyCheckId.ToString(CultureInfo.InvariantCulture));
                HashEntry[] meta = await db.HashGetAllAsync(ReadyCheckMetaKey(readyCheckId)).ConfigureAwait(false);
                if (meta.Length == 0) return null;

                Dictionary<string, string> map = meta.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
                if (!map.TryGetValue("snapshot", out string? snapJson) || string.IsNullOrEmpty(snapJson))
                {
                    return null;
                }
                DistributedReadyCheckSnapshot? baseSnap = JsonSerializer.Deserialize(snapJson, ValkeyMatchmakingJsonContext.Default.DistributedReadyCheckSnapshot);
                if (baseSnap is null) return null;

                RedisValue[] acceptedVals = await db.SetMembersAsync(ReadyCheckAcceptedKey(readyCheckId)).ConfigureAwait(false);
                List<int> accepted = [];
                foreach (RedisValue v in acceptedVals)
                {
                    if (int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    {
                        accepted.Add(id);
                    }
                }
                accepted.Sort();

                int? declinedBy = map.TryGetValue("declined_by", out string? declRaw) &&
                                  int.TryParse(declRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int declId)
                    ? declId
                    : null;

                return baseSnap with
                {
                    AcceptedAccountIds = accepted,
                    DeclinedByAccountId = declinedBy,
                    Status = map.TryGetValue("status", out string? st) ? st : baseSnap.Status
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(GetReadyCheckAsync),
                    readyCheckId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(GetReadyCheckAsync),
                    readyCheckId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_fallbackLock)
        {
            PruneLocalReadyChecksLocked(now);
            if (!_localReadyChecks.TryGetValue(readyCheckId, out LocalReadyCheckEntry? entry))
            {
                return null;
            }

            string effectiveStatus = entry.Status == "OPEN" && now >= entry.VoteDeadlineUtc
                ? "EXPIRED"
                : entry.Status;

            return entry.Snapshot with
            {
                AcceptedAccountIds = [.. entry.Accepted.OrderBy(x => x)],
                DeclinedByAccountId = entry.DeclinedBy,
                Status = effectiveStatus
            };
        }
    }

    public async Task<int?> FindActiveReadyCheckIdForAccountAsync(int accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(FindActiveReadyCheckIdForAccountAsync),
                    accountId.ToString(CultureInfo.InvariantCulture));
                string field = accountId.ToString(CultureInfo.InvariantCulture);
                RedisValue mapped = await db.HashGetAsync(AccountReadyCheckKey, field).ConfigureAwait(false);
                if (!mapped.HasValue || !int.TryParse(mapped.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int readyCheckId))
                {
                    return null;
                }

                RedisValue statusVal = await db.HashGetAsync(ReadyCheckMetaKey(readyCheckId), "status").ConfigureAwait(false);
                if (!statusVal.HasValue || !string.Equals(statusVal.ToString(), "OPEN", StringComparison.Ordinal))
                {
                    await db.HashDeleteAsync(AccountReadyCheckKey, field).ConfigureAwait(false);
                    _logger.LogDebug(
                        "[ValkeyMMState] Pod={PodId} pruned stale account_readycheck mapping for AccountId={AccountId} -> ReadyCheckId={ReadyCheckId} (Status={Status})",
                        PodId, accountId, readyCheckId, statusVal.HasValue ? statusVal.ToString() : "EXPIRED_OR_MISSING");
                    return null;
                }

                _logger.LogDebug(
                    "[ValkeyMMState] Pod={PodId} resolved AccountId={AccountId} -> active ReadyCheckId={ReadyCheckId}",
                    PodId, accountId, readyCheckId);
                return readyCheckId;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(FindActiveReadyCheckIdForAccountAsync),
                    accountId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(FindActiveReadyCheckIdForAccountAsync),
                    accountId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_fallbackLock)
        {
            PruneLocalReadyChecksLocked(now);
            if (!_localAccountReadyChecks.TryGetValue(accountId, out int rcId))
            {
                return null;
            }

            if (!_localReadyChecks.TryGetValue(rcId, out LocalReadyCheckEntry? entry) ||
                entry.Status != "OPEN" ||
                now >= entry.VoteDeadlineUtc)
            {
                _localAccountReadyChecks.Remove(accountId);
                return null;
            }

            return rcId;
        }
    }

    public async Task<DistributedReadyCheckVoteResult> TrySubmitReadyCheckVoteAsync(
        int readyCheckId,
        int accountId,
        bool accepted,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        long nowMs = now.ToUnixTimeMilliseconds();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(TrySubmitReadyCheckVoteAsync),
                    $"{readyCheckId}:{accountId}");
                RedisResult raw = await db.ScriptEvaluateAsync(
                    SubmitReadyCheckVoteLua,
                    [
                        new RedisKey(ReadyCheckMetaKey(readyCheckId)),
                        new RedisKey(ReadyCheckRequiredKey(readyCheckId)),
                        new RedisKey(ReadyCheckAcceptedKey(readyCheckId)),
                        new RedisKey(EventsStreamKey),
                        new RedisKey(AccountReadyCheckKey)
                    ],
                    [
                        new RedisValue(readyCheckId.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(accountId.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(accepted ? "1" : "0"),
                        new RedisValue(PodId),
                        new RedisValue(nowMs.ToString(CultureInfo.InvariantCulture))
                    ]).ConfigureAwait(false);
                if (!raw.IsNull)
                {
                    MarkStreamsCapabilityVerified();
                }

                RedisResult[] arr = (RedisResult[])raw!;
                DistributedReadyCheckVoteOutcome outcome = (DistributedReadyCheckVoteOutcome)(int)arr[0];
                int acceptedCount = (int)arr[1];
                int totalPlayers = (int)arr[2];
                string status = arr[3].ToString() ?? "UNKNOWN";

                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} ReadyCheckVote Id={ReadyCheckId} Account={AccountId} Accepted={Accepted} -> Outcome={Outcome} ({AcceptedCount}/{TotalPlayers}, Status={Status})",
                    PodId, readyCheckId, accountId, accepted, outcome, acceptedCount, totalPlayers, status);

                return new DistributedReadyCheckVoteResult(
                    outcome,
                    readyCheckId,
                    acceptedCount,
                    totalPlayers,
                    status,
                    Message: $"Cluster vote resolved as {outcome}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(TrySubmitReadyCheckVoteAsync),
                    $"{readyCheckId}:{accountId}");
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(TrySubmitReadyCheckVoteAsync),
                    $"{readyCheckId}:{accountId}",
                    ex);
            }
        }

        lock (_fallbackLock)
        {
            PruneLocalReadyChecksLocked(now);
            if (!_localReadyChecks.TryGetValue(readyCheckId, out LocalReadyCheckEntry? entry))
            {
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.NotFound, readyCheckId, 0, 0, "NOT_FOUND");
            }

            if (entry.Status != "OPEN")
            {
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.AlreadyResolved, readyCheckId, entry.Accepted.Count, entry.Required.Count, entry.Status);
            }

            if (now >= entry.VoteDeadlineUtc)
            {
                entry.Status = "EXPIRED";
                entry.RetentionUntilUtc = now.AddMilliseconds(TombstoneTtlMs);
                ClearLocalAccountReadyCheckMappingsLocked(readyCheckId, entry.Required);
                List<int> missing = [.. entry.Required.Except(entry.Accepted).OrderBy(x => x)];
                string expiredPayload = JsonSerializer.Serialize(
                    new ReadyCheckVoteEventPayload(readyCheckId, null, entry.Accepted.Count, entry.Required.Count, missing.Count),
                    ValkeyMatchmakingJsonContext.Default.ReadyCheckVoteEventPayload);
                AppendLocalEventLocked("readycheck_expired", expiredPayload, nowMs);
                return new DistributedReadyCheckVoteResult(
                    DistributedReadyCheckVoteOutcome.Expired,
                    readyCheckId,
                    entry.Accepted.Count,
                    entry.Required.Count,
                    "EXPIRED",
                    missing,
                    "Ready check expired before vote arrived");
            }

            if (!entry.Required.Contains(accountId))
            {
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.NotParticipant, readyCheckId, entry.Accepted.Count, entry.Required.Count, entry.Status);
            }

            if (!accepted)
            {
                entry.Status = "DECLINED";
                entry.DeclinedBy = accountId;
                entry.RetentionUntilUtc = now.AddMilliseconds(TombstoneTtlMs);
                ClearLocalAccountReadyCheckMappingsLocked(readyCheckId, entry.Required);
                string declinedPayload = JsonSerializer.Serialize(
                    new ReadyCheckVoteEventPayload(readyCheckId, accountId, entry.Accepted.Count, entry.Required.Count),
                    ValkeyMatchmakingJsonContext.Default.ReadyCheckVoteEventPayload);
                AppendLocalEventLocked("readycheck_declined", declinedPayload, nowMs);
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.Declined, readyCheckId, entry.Accepted.Count, entry.Required.Count, "DECLINED");
            }

            if (!entry.Accepted.Add(accountId))
            {
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.DuplicateVote, readyCheckId, entry.Accepted.Count, entry.Required.Count, entry.Status);
            }

            if (entry.Accepted.Count >= entry.Required.Count && entry.Required.Count > 0)
            {
                entry.Status = "QUORUM_REACHED";
                entry.RetentionUntilUtc = now.AddMilliseconds(TombstoneTtlMs);
                ClearLocalAccountReadyCheckMappingsLocked(readyCheckId, entry.Required);
                string quorumPayload = JsonSerializer.Serialize(
                    new ReadyCheckVoteEventPayload(readyCheckId, accountId, entry.Accepted.Count, entry.Required.Count),
                    ValkeyMatchmakingJsonContext.Default.ReadyCheckVoteEventPayload);
                AppendLocalEventLocked("readycheck_quorum_reached", quorumPayload, nowMs);
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.QuorumReached, readyCheckId, entry.Accepted.Count, entry.Required.Count, "QUORUM_REACHED");
            }

            string acceptedPayload = JsonSerializer.Serialize(
                new ReadyCheckVoteEventPayload(readyCheckId, accountId, entry.Accepted.Count, entry.Required.Count),
                ValkeyMatchmakingJsonContext.Default.ReadyCheckVoteEventPayload);
            AppendLocalEventLocked("readycheck_vote_accepted", acceptedPayload, nowMs);
            return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.Accepted, readyCheckId, entry.Accepted.Count, entry.Required.Count, "OPEN");
        }
    }

    public async Task<DistributedReadyCheckVoteResult> TryExpireReadyCheckAsync(
        int readyCheckId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        long nowMs = now.ToUnixTimeMilliseconds();

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(
                    nameof(TryExpireReadyCheckAsync),
                    readyCheckId.ToString(CultureInfo.InvariantCulture));
                RedisResult raw = await db.ScriptEvaluateAsync(
                    ExpireReadyCheckLua,
                    [
                        new RedisKey(ReadyCheckMetaKey(readyCheckId)),
                        new RedisKey(ReadyCheckRequiredKey(readyCheckId)),
                        new RedisKey(ReadyCheckAcceptedKey(readyCheckId)),
                        new RedisKey(EventsStreamKey),
                        new RedisKey(AccountReadyCheckKey)
                    ],
                    [
                        new RedisValue(readyCheckId.ToString(CultureInfo.InvariantCulture)),
                        new RedisValue(PodId),
                        new RedisValue(nowMs.ToString(CultureInfo.InvariantCulture))
                    ]).ConfigureAwait(false);
                if (!raw.IsNull)
                {
                    MarkStreamsCapabilityVerified();
                }

                RedisResult[] arr = (RedisResult[])raw!;
                DistributedReadyCheckVoteOutcome outcome = (DistributedReadyCheckVoteOutcome)(int)arr[0];
                int acceptedCount = (int)arr[1];
                int totalPlayers = (int)arr[2];
                string status = arr[3].ToString() ?? "UNKNOWN";
                List<int> missing = [];
                for (int i = 4; i < arr.Length; i++)
                {
                    if (int.TryParse(arr[i].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int missId))
                    {
                        missing.Add(missId);
                    }
                }
                missing.Sort();

                _logger.LogInformation(
                    "[ValkeyMMState] Pod={PodId} TryExpireReadyCheck Id={ReadyCheckId} -> Outcome={Outcome} Missing={MissingCount}",
                    PodId, readyCheckId, outcome, missing.Count);

                return new DistributedReadyCheckVoteResult(
                    outcome,
                    readyCheckId,
                    acceptedCount,
                    totalPlayers,
                    status,
                    MissingAccountIds: missing,
                    Message: $"Cluster expire resolved as {outcome}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(
                    ex,
                    nameof(TryExpireReadyCheckAsync),
                    readyCheckId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(
                    nameof(TryExpireReadyCheckAsync),
                    readyCheckId.ToString(CultureInfo.InvariantCulture),
                    ex);
            }
        }

        lock (_fallbackLock)
        {
            PruneLocalReadyChecksLocked(now);
            if (!_localReadyChecks.TryGetValue(readyCheckId, out LocalReadyCheckEntry? entry))
            {
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.NotFound, readyCheckId, 0, 0, "NOT_FOUND");
            }

            if (entry.Status != "OPEN")
            {
                return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.AlreadyResolved, readyCheckId, entry.Accepted.Count, entry.Required.Count, entry.Status);
            }

            entry.Status = "EXPIRED";
            entry.RetentionUntilUtc = now.AddMilliseconds(TombstoneTtlMs);
            ClearLocalAccountReadyCheckMappingsLocked(readyCheckId, entry.Required);
            List<int> missing = [.. entry.Required.Except(entry.Accepted).OrderBy(x => x)];
            string expiredPayload = JsonSerializer.Serialize(
                new ReadyCheckVoteEventPayload(readyCheckId, null, entry.Accepted.Count, entry.Required.Count, missing.Count),
                ValkeyMatchmakingJsonContext.Default.ReadyCheckVoteEventPayload);
            AppendLocalEventLocked("readycheck_expired", expiredPayload, nowMs);
            return new DistributedReadyCheckVoteResult(DistributedReadyCheckVoteOutcome.Expired, readyCheckId, entry.Accepted.Count, entry.Required.Count, "EXPIRED", missing);
        }
    }

    public async Task<IReadOnlyList<DistributedMatchmakingStreamEvent>> ReadRecentEventsAsync(
        int count = 50,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int clamped = Math.Clamp(count, 1, 500);

        if (IsRedisBackplaneEnabled)
        {
            try
            {
                IDatabase db = GetConnectedDatabaseOrThrow(nameof(ReadRecentEventsAsync), EventsStreamKey);
                StreamEntry[] entries = await db.StreamRangeAsync(
                    EventsStreamKey,
                    minId: "-",
                    maxId: "+",
                    count: clamped,
                    messageOrder: Order.Descending).ConfigureAwait(false);
                MarkStreamsCapabilityVerified();

                List<DistributedMatchmakingStreamEvent> results = new(entries.Length);
                foreach (StreamEntry entry in entries)
                {
                    Dictionary<string, string> fields = entry.Values.ToDictionary(
                        nv => nv.Name.ToString(),
                        nv => nv.Value.ToString());
                    long tsMs = fields.TryGetValue("ts_ms", out string? tsRaw) &&
                                long.TryParse(tsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTs)
                        ? parsedTs
                        : 0L;
                    results.Add(new DistributedMatchmakingStreamEvent(
                        entry.Id.ToString(),
                        fields.GetValueOrDefault("event_type", "unknown"),
                        fields.GetValueOrDefault("pod_id", "unknown"),
                        fields.GetValueOrDefault("payload", "{}"),
                        tsMs));
                }
                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DistributedMatchmakingStoreException)
            {
                throw;
            }
            catch (Exception ex) when (IsUnsupportedStreamsException(ex))
            {
                DegradeToLocalSinglePodMode(ex, nameof(ReadRecentEventsAsync), EventsStreamKey);
            }
            catch (Exception ex)
            {
                throw CreateAndLogRedisException(nameof(ReadRecentEventsAsync), EventsStreamKey, ex);
            }
        }

        lock (_fallbackLock)
        {
            return [.. _localEvents.TakeLast(clamped).Reverse()];
        }
    }

    private void PruneLocalPendingMatchesLocked(DateTimeOffset now)
    {
        if (_localPendingMatches.Count == 0)
        {
            return;
        }

        List<int>? toRemove = null;
        foreach (KeyValuePair<int, LocalPendingMatchEntry> kvp in _localPendingMatches)
        {
            LocalPendingMatchEntry entry = kvp.Value;
            if (entry.Status != "PENDING" && now >= entry.ExpiresAtUtc)
            {
                toRemove ??= [];
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove is not null)
        {
            foreach (int id in toRemove)
            {
                _localPendingMatches.Remove(id);
            }
        }
    }

    private void PruneLocalReadyChecksLocked(DateTimeOffset now)
    {
        if (_localReadyChecks.Count == 0)
        {
            return;
        }

        List<int>? toRemove = null;
        foreach (KeyValuePair<int, LocalReadyCheckEntry> kvp in _localReadyChecks)
        {
            LocalReadyCheckEntry entry = kvp.Value;
            bool resolvedPastRetention = entry.Status != "OPEN" &&
                                         entry.RetentionUntilUtc.HasValue &&
                                         now >= entry.RetentionUntilUtc.Value;
            bool openPastTtl = entry.Status == "OPEN" && now >= entry.TtlExpiresAtUtc;
            if (resolvedPastRetention || openPastTtl)
            {
                ClearLocalAccountReadyCheckMappingsLocked(kvp.Key, entry.Required);
                toRemove ??= [];
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove is not null)
        {
            foreach (int id in toRemove)
            {
                _localReadyChecks.Remove(id);
            }
        }
    }

    private void ClearLocalAccountReadyCheckMappingsLocked(int readyCheckId, HashSet<int> requiredAccounts)
    {
        foreach (int accountId in requiredAccounts)
        {
            if (_localAccountReadyChecks.TryGetValue(accountId, out int mappedId) && mappedId == readyCheckId)
            {
                _localAccountReadyChecks.Remove(accountId);
            }
        }
    }

    private void AppendLocalEvent(string eventType, string payloadJson, long tsMs)
    {
        lock (_fallbackLock)
        {
            AppendLocalEventLocked(eventType, payloadJson, tsMs);
        }
    }

    private void AppendLocalEventLocked(string eventType, string payloadJson, long tsMs)
    {
        _localEvents.Add(new DistributedMatchmakingStreamEvent(
            $"{tsMs}-{_localEvents.Count}",
            eventType,
            PodId,
            payloadJson,
            tsMs));
        if (_localEvents.Count > StreamMaxLenApprox)
        {
            _localEvents.RemoveRange(0, _localEvents.Count - StreamMaxLenApprox);
        }
    }
}
