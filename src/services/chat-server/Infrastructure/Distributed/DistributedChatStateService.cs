// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MemoryPack;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;

namespace TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

/// <summary>
///     High-performance distributed chat backplane coordinating channels, whispers,
///     clan messaging, and user presences across TRANSMUTANSTEIN cluster nodes via Redis.
/// </summary>
public sealed class DistributedChatStateService : IDistributedChatStateService, IHostedService, IDisposable
{
    private const string KeyPresenceUsers = "kongor:chat:users:presence";
    private const string KeyPresenceById = "kongor:chat:users:by_id";
    private const string KeyChannels = "kongor:chat:channels";
    private const string KeyNodes = "kongor:chat:nodes";
    private const string KeyNodeSessionsPrefix = "kongor:chat:node_sessions:";
    private const string ChannelWhisperPrefix = "kongor:chat:whispers:";
    private const string ChannelChanPrefix = "kongor:chat:chan:";
    private const string ChannelClanPrefix = "kongor:chat:clan:";
    private const string ChannelEvictLiteral = "kongor:chat:cluster:evict";
    private const string ChannelPeerInvalidationLiteral = "account:peer_invalidation";

    private readonly IConnectionMultiplexer? _redis;
    private readonly IChatContext _chatContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DistributedChatStateService> _logger;
    private readonly IServiceProvider? _serviceProvider;

    private readonly ConcurrentDictionary<string, DistributedUserPresence> _localPresenceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DistributedChannelMetadata> _localChannelCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DistributedChannelMember>> _localChannelMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, ChatSession>> _clanSessionsByClanId = new();
    private readonly ConcurrentDictionary<int, ChatSession> _sessionsByAccountId = new();

    private sealed class ChannelSubscriptionEntry : IDisposable
    {
        public int RefCount;
        public readonly SemaphoreSlim Gate = new(1, 1);

        public void Dispose()
        {
            Gate.Dispose();
        }
    }

    private sealed class ClanSubscriptionEntry : IDisposable
    {
        public int RefCount;
        public readonly SemaphoreSlim Gate = new(1, 1);

        public void Dispose()
        {
            Gate.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, ChannelSubscriptionEntry> _channelSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ClanSubscriptionEntry> _clanSubscriptions = new();

    public static string GetPresenceUsersKey(string accountName) =>
        $"kongor:chat:users:presence:{System.IO.Hashing.XxHash32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes((accountName ?? string.Empty).ToLowerInvariant())) % 64}";

    public static string GetPresenceByIdKey(int accountId) =>
        $"kongor:chat:users:by_id:{unchecked((uint)accountId) % 64}";

    public static string GetNodeSessionsKey(string nodeId) =>
        $"{KeyNodeSessionsPrefix}{nodeId}";

    private readonly CancellationTokenSource _cts = new();
    private ISubscriber? _subscriber;
    private Task? _heartbeatTask;
    private bool _disposed;

    public string NodeId { get; }
    public bool IsDistributed => _redis != null && _redis.IsConnected;

    public DistributedChatStateService(
        IChatContext chatContext,
        TimeProvider timeProvider,
        ILogger<DistributedChatStateService> logger,
        IConnectionMultiplexer? redis = null,
        IServiceProvider? serviceProvider = null)
    {
        _chatContext = chatContext;
        _timeProvider = timeProvider;
        _logger = logger;
        _redis = redis;
        _serviceProvider = serviceProvider;

        NodeId = $"{Environment.MachineName.ToLowerInvariant()}-{Guid.NewGuid():N}"[..16];
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[DistributedChatState] Starting chat node {NodeId}. Distributed: {IsDistributed}",
            NodeId, IsDistributed);

        if (IsDistributed && _redis != null)
        {
            try
            {
                _subscriber = _redis.GetSubscriber();

                // 1. Subscribe to directed cross-node whispers for this node
                await _subscriber.SubscribeAsync(RedisChannel.Literal($"{ChannelWhisperPrefix}{NodeId}"), OnWhisperReceived);

                // 2. Subscribe to cluster-wide eviction notices
                await _subscriber.SubscribeAsync(RedisChannel.Literal(ChannelEvictLiteral), OnEvictionReceived);

                // 2b. Subscribe to peer list invalidations
                await _subscriber.SubscribeAsync(RedisChannel.Literal(ChannelPeerInvalidationLiteral), OnPeerInvalidationReceived);

                // 3. Register node heartbeat
                IDatabase db = _redis.GetDatabase();
                await db.HashSetAsync(KeyNodes, NodeId, _timeProvider.GetUtcNow().ToUnixTimeSeconds());

                _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);
                _logger.LogInformation("[DistributedChatState] Successfully subscribed to Redis cluster backplane channels.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to initialize Redis subscriptions. Falling back to local standalone mode.");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[DistributedChatState] Stopping node {NodeId}...", NodeId);
        _cts.Cancel();

        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (IsDistributed && _redis != null && _subscriber != null)
        {
            try
            {
                await _subscriber.UnsubscribeAllAsync();
                _channelSubscriptions.Clear();
                _clanSubscriptions.Clear();
                IDatabase db = _redis.GetDatabase();
                await db.HashDeleteAsync(KeyNodes, NodeId);

                // Prune local sessions from cluster presence via fast batched pipeline
                List<(string accountName, int accountId)> localSessions = _chatContext.ClientChatSessions.Values
                    .Where(s => s.Account != null)
                    .Select(s => (s.Account!.Name, s.Account!.ID))
                    .ToList();

                if (localSessions.Count > 0)
                {
                    await UnregisterSessionsBatchAsync(localSessions, cancellationToken).ConfigureAwait(false);
                }
                await db.KeyDeleteAsync(GetNodeSessionsKey(NodeId)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Cleanup during stop encountered an error: {Message}", ex.Message);
            }
        }
    }

    public void RegisterLocalSession(ChatSession session)
    {
        if (session.Account is { ID: > 0 } acc)
        {
            _sessionsByAccountId[acc.ID] = session;
            if (acc.Clan is { ID: > 0 } clan)
            {
                var clanSessions = _clanSessionsByClanId.GetOrAdd(clan.ID, _ => new ConcurrentDictionary<Guid, ChatSession>());
                clanSessions[session.ID] = session;
            }
        }
    }

    public void UnregisterLocalSession(ChatSession session)
    {
        if (session.Account is { ID: > 0 } acc)
        {
            _sessionsByAccountId.TryRemove(acc.ID, out _);
            if (acc.Clan is { ID: > 0 } clan)
            {
                if (_clanSessionsByClanId.TryGetValue(clan.ID, out var clanSessions))
                {
                    clanSessions.TryRemove(session.ID, out _);
                    if (clanSessions.IsEmpty)
                    {
                        _clanSessionsByClanId.TryRemove(clan.ID, out _);
                    }
                }
            }
        }
    }

    public void UpdateSessionClan(ChatSession session, int? oldClanId, int? newClanId)
    {
        if (oldClanId is > 0 && _clanSessionsByClanId.TryGetValue(oldClanId.Value, out var oldClanSessions))
        {
            oldClanSessions.TryRemove(session.ID, out _);
            if (oldClanSessions.IsEmpty)
            {
                _clanSessionsByClanId.TryRemove(oldClanId.Value, out _);
                _ = ReleaseClanSubscriptionAsync(oldClanId.Value);
            }
        }

        if (newClanId is > 0)
        {
            var newClanSessions = _clanSessionsByClanId.GetOrAdd(newClanId.Value, _ => new ConcurrentDictionary<Guid, ChatSession>());
            newClanSessions[session.ID] = session;
            _ = EnsureClanSubscribedAsync(newClanId.Value);
        }
    }

    public async Task RegisterSessionAsync(ChatSession session, CancellationToken ct = default)
    {
        if (session.Account is null) return;

        RegisterLocalSession(session);

        if (session.Account.Clan is { ID: > 0 } clan)
        {
            await EnsureClanSubscribedAsync(clan.ID);
        }

        string accountName = session.Account.Name;
        int accountId = session.Account.ID;

        DistributedUserPresence presence = new()
        {
            AccountID = accountId,
            AccountName = accountName,
            NodeID = NodeId,
            ClientStatus = (byte)(session.ClientMetadata?.LastKnownClientState ?? ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED),
            ChatMode = (byte)(session.ClientMetadata?.ClientChatModeState ?? ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE),
            ClanID = session.Account.Clan?.ID,
            ClientFlags = session.Account.GetChatClientFlags(),
            ChatSymbol = session.Account.GetChatSymbolNoPrefixCode(),
            NameColour = session.Account.GetNameColourNoPrefixCode(),
            AccountIcon = session.Account.GetIconNoPrefixCode(),
            AscensionLevel = session.Account.AscensionLevel,
            MatchAddress = session.ClientMetadata?.MatchServerAddress,
            MatchID = session.ClientMetadata?.MatchID,
            LastHeartbeatUtc = _timeProvider.GetUtcNow()
        };

        _localPresenceCache[accountName] = presence;

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                byte[] bytes = MemoryPackSerializer.Serialize(presence);
                await db.HashSetAsync(GetPresenceUsersKey(accountName), accountName.ToLowerInvariant(), bytes);
                await db.HashSetAsync(GetPresenceByIdKey(accountId), accountId, accountName.ToLowerInvariant());
                await db.SetAddAsync(GetNodeSessionsKey(NodeId), $"{accountName}:{accountId}");

                // Evict any existing sessions on other nodes for this account
                await EvictConcurrentSessionAsync(accountId, accountName, (byte)ChatProtocol.ChatRejectReason.ECR_ACCOUNT_SHARING, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to register session in Redis for {AccountName}", accountName);
            }
        }
    }

    public async Task UnregisterSessionAsync(string accountName, int accountId, CancellationToken ct = default)
    {
        _localPresenceCache.TryRemove(accountName, out _);

        if (_sessionsByAccountId.TryRemove(accountId, out ChatSession? localSession) && localSession.Account?.Clan is { ID: > 0 } clan)
        {
            if (_clanSessionsByClanId.TryGetValue(clan.ID, out var clanSessions))
            {
                clanSessions.TryRemove(localSession.ID, out _);
                if (clanSessions.IsEmpty)
                {
                    _clanSessionsByClanId.TryRemove(clan.ID, out _);
                }
            }

            await ReleaseClanSubscriptionAsync(clan.ID);
        }

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                await db.HashDeleteAsync(GetPresenceUsersKey(accountName), accountName.ToLowerInvariant());
                await db.HashDeleteAsync(GetPresenceByIdKey(accountId), accountId);
                await db.SetRemoveAsync(GetNodeSessionsKey(NodeId), $"{accountName}:{accountId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to unregister session from Redis for {AccountName}", accountName);
            }
        }
    }

    public async Task<int> UnregisterSessionsBatchAsync(IReadOnlyList<(string accountName, int accountId)> sessions, CancellationToken ct = default)
    {
        if (sessions == null || sessions.Count == 0) return 0;

        List<int> clansToCheck = new();
        foreach (var (accountName, accountId) in sessions)
        {
            if (!string.IsNullOrEmpty(accountName))
            {
                _localPresenceCache.TryRemove(accountName, out _);
            }

            if (accountId > 0 && _sessionsByAccountId.TryRemove(accountId, out ChatSession? localSession) && localSession.Account?.Clan is { ID: > 0 } clan)
            {
                if (_clanSessionsByClanId.TryGetValue(clan.ID, out var clanSessions))
                {
                    clanSessions.TryRemove(localSession.ID, out _);
                    if (clanSessions.IsEmpty)
                    {
                        _clanSessionsByClanId.TryRemove(clan.ID, out _);
                        clansToCheck.Add(clan.ID);
                    }
                }
            }
        }

        foreach (int clanId in clansToCheck.Distinct())
        {
            await ReleaseClanSubscriptionAsync(clanId).ConfigureAwait(false);
        }

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                Dictionary<string, List<RedisValue>> presenceShards = new();
                Dictionary<string, List<RedisValue>> byIdShards = new();
                List<RedisValue> nodeSessionMembers = new(sessions.Count);

                foreach (var (accountName, accountId) in sessions)
                {
                    if (!string.IsNullOrEmpty(accountName))
                    {
                        string presenceKey = GetPresenceUsersKey(accountName);
                        if (!presenceShards.TryGetValue(presenceKey, out var list))
                        {
                            list = new List<RedisValue>();
                            presenceShards[presenceKey] = list;
                        }
                        list.Add(accountName.ToLowerInvariant());
                    }

                    if (accountId > 0)
                    {
                        string byIdKey = GetPresenceByIdKey(accountId);
                        if (!byIdShards.TryGetValue(byIdKey, out var list))
                        {
                            list = new List<RedisValue>();
                            byIdShards[byIdKey] = list;
                        }
                        list.Add(accountId);
                    }

                    if (!string.IsNullOrEmpty(accountName) && accountId > 0)
                    {
                        nodeSessionMembers.Add($"{accountName}:{accountId}");
                    }
                }

                IBatch batch = db.CreateBatch();
                List<Task<long>> deleteTasks = new(presenceShards.Count + byIdShards.Count);

                foreach (var (key, fields) in presenceShards)
                {
                    deleteTasks.Add(batch.HashDeleteAsync(key, fields.ToArray()));
                }
                foreach (var (key, fields) in byIdShards)
                {
                    deleteTasks.Add(batch.HashDeleteAsync(key, fields.ToArray()));
                }

                if (nodeSessionMembers.Count > 0)
                {
                    _ = batch.SetRemoveAsync(GetNodeSessionsKey(NodeId), nodeSessionMembers.ToArray());
                }

                batch.Execute();
                long[] results = await Task.WhenAll(deleteTasks).ConfigureAwait(false);
                long totalReaped = results.Sum();

                _logger.LogInformation("[DistributedChatState] Pipeline-batched unregistration completed: {TotalReaped} presence fields removed for {SessionCount} sessions across {ShardCount} shards.",
                    totalReaped, sessions.Count, presenceShards.Count + byIdShards.Count);

                return (int)totalReaped;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to batch unregister {Count} sessions from Redis.", sessions.Count);
            }
        }

        return sessions.Count;
    }

    public async Task<int> ReapDeadNodeSessionsAsync(string deadNodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deadNodeId)) return 0;

        _logger.LogWarning("[DistributedChatState] Initiating dead node session reaping for node {DeadNodeId}...", deadNodeId);

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                string nodeSessionKey = GetNodeSessionsKey(deadNodeId);
                RedisValue[] members = await db.SetMembersAsync(nodeSessionKey).ConfigureAwait(false);
                if (members.Length == 0)
                {
                    _logger.LogInformation("[DistributedChatState] No active sessions found in node set for dead node {DeadNodeId}.", deadNodeId);
                    await db.KeyDeleteAsync(nodeSessionKey).ConfigureAwait(false);
                    return 0;
                }

                List<(string accountName, int accountId)> sessionsToReap = new(members.Length);
                foreach (RedisValue member in members)
                {
                    string entry = member.ToString();
                    int sepIdx = entry.IndexOf(':');
                    if (sepIdx > 0 && int.TryParse(entry[(sepIdx + 1)..], out int accId))
                    {
                        sessionsToReap.Add((entry[..sepIdx], accId));
                    }
                }

                int reapedCount = await UnregisterSessionsBatchAsync(sessionsToReap, ct).ConfigureAwait(false);
                await db.KeyDeleteAsync(nodeSessionKey).ConfigureAwait(false);

                _logger.LogWarning("[DistributedChatState] Successfully reaped {ReapedCount} presence entries for dead node {DeadNodeId}.",
                    reapedCount, deadNodeId);

                return reapedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DistributedChatState] Failed to reap sessions for dead node {DeadNodeId}.", deadNodeId);
            }
        }

        return 0;
    }

    public async Task UpdatePresenceAsync(
        int accountId,
        string accountName,
        ChatProtocol.ChatClientStatus status,
        ChatProtocol.ChatModeType chatMode,
        string? matchAddress = null,
        int? matchId = null,
        CancellationToken ct = default)
    {
        if (_localPresenceCache.TryGetValue(accountName, out DistributedUserPresence? presence))
        {
            presence.ClientStatus = (byte)status;
            presence.ChatMode = (byte)chatMode;
            presence.MatchAddress = matchAddress;
            presence.MatchID = matchId;
            presence.LastHeartbeatUtc = _timeProvider.GetUtcNow();
        }

        if (IsDistributed && _redis != null)
        {
            try
            {
                DistributedUserPresence current = presence ?? new DistributedUserPresence
                {
                    AccountID = accountId,
                    AccountName = accountName,
                    NodeID = NodeId,
                    ClientStatus = (byte)status,
                    ChatMode = (byte)chatMode,
                    MatchAddress = matchAddress,
                    MatchID = matchId,
                    LastHeartbeatUtc = _timeProvider.GetUtcNow()
                };

                IDatabase db = _redis.GetDatabase();
                byte[] bytes = MemoryPackSerializer.Serialize(current);
                await db.HashSetAsync(GetPresenceUsersKey(accountName), accountName.ToLowerInvariant(), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to update presence in Redis for {AccountName}", accountName);
            }
        }
    }

    public async Task<DistributedUserPresence?> GetUserPresenceAsync(string accountName, CancellationToken ct = default)
    {
        if (_localPresenceCache.TryGetValue(accountName, out DistributedUserPresence? local))
        {
            return local;
        }

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue val = await db.HashGetAsync(GetPresenceUsersKey(accountName), accountName.ToLowerInvariant());
                if (!val.IsNullOrEmpty)
                {
                    return MemoryPackSerializer.Deserialize<DistributedUserPresence>((byte[])val!);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Redis presence query failed for {AccountName}", accountName);
            }
        }

        return null;
    }

    public async Task<DistributedUserPresence?> GetUserPresenceByIdAsync(int accountId, CancellationToken ct = default)
    {
        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue nameVal = await db.HashGetAsync(GetPresenceByIdKey(accountId), accountId);
                if (!nameVal.IsNullOrEmpty)
                {
                    string accountName = nameVal.ToString();
                    return await GetUserPresenceAsync(accountName, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Redis presence query by ID failed for {AccountId}", accountId);
            }
        }

        if (_sessionsByAccountId.TryGetValue(accountId, out ChatSession? localSession) && localSession.Account != null)
        {
            if (_localPresenceCache.TryGetValue(localSession.Account.Name, out DistributedUserPresence? p))
            {
                return p;
            }
        }

        foreach (DistributedUserPresence p in _localPresenceCache.Values)
        {
            if (p.AccountID == accountId) return p;
        }

        return null;
    }

    public async Task<bool> RouteWhisperAsync(
        string senderName,
        int senderId,
        string recipientName,
        string message,
        CancellationToken ct = default)
    {
        // 1. Check local session
        if (_chatContext.ClientChatSessions.TryGetValue(recipientName, out ChatSession? recipientSession))
        {
            DeliverWhisperToLocalSession(senderName, senderId, recipientSession, message);
            return true;
        }

        // 2. Query cluster presence
        DistributedUserPresence? presence = await GetUserPresenceAsync(recipientName, ct);
        if (presence is null)
        {
            return false;
        }

        // 3. Dispatch to target node via Redis Pub/Sub
        if (IsDistributed && _redis != null)
        {
            try
            {
                DistributedWhisperEnvelope envelope = new()
                {
                    SenderName = senderName,
                    SenderAccountID = senderId,
                    TargetName = recipientName,
                    TargetAccountID = presence.AccountID,
                    Message = message,
                    OriginNodeID = NodeId,
                    TimestampUtc = _timeProvider.GetUtcNow()
                };

                byte[] bytes = MemoryPackSerializer.Serialize(envelope);
                ISubscriber sub = _redis.GetSubscriber();
                await sub.PublishAsync(RedisChannel.Literal($"{ChannelWhisperPrefix}{presence.NodeID}"), bytes);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Cross-node whisper publish failed to {TargetNode}", presence.NodeID);
            }
        }

        return false;
    }

    public async Task PublishChannelMessageAsync(
        int channelId,
        string channelName,
        int senderId,
        string senderName,
        string message,
        CancellationToken ct = default)
    {
        if (IsDistributed && _redis != null)
        {
            try
            {
                DistributedChannelMessageEnvelope envelope = new()
                {
                    ChannelID = channelId,
                    ChannelName = channelName,
                    SenderAccountID = senderId,
                    SenderName = senderName,
                    Message = message,
                    OriginNodeID = NodeId,
                    TimestampUtc = _timeProvider.GetUtcNow()
                };

                byte[] bytes = MemoryPackSerializer.Serialize(envelope);
                ISubscriber sub = _redis.GetSubscriber();
                await sub.PublishAsync(RedisChannel.Literal($"{ChannelChanPrefix}{channelName.ToLowerInvariant()}"), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to publish channel message to Redis for {ChannelName}", channelName);
            }
        }
    }

    public async Task PublishClanWhisperAsync(
        int clanId,
        int senderId,
        string senderName,
        string message,
        CancellationToken ct = default)
    {
        if (IsDistributed && _redis != null)
        {
            try
            {
                DistributedClanWhisperEnvelope envelope = new()
                {
                    ClanID = clanId,
                    SenderAccountID = senderId,
                    SenderName = senderName,
                    Message = message,
                    OriginNodeID = NodeId,
                    TimestampUtc = _timeProvider.GetUtcNow()
                };

                byte[] bytes = MemoryPackSerializer.Serialize(envelope);
                ISubscriber sub = _redis.GetSubscriber();
                await sub.PublishAsync(RedisChannel.Literal($"{ChannelClanPrefix}{clanId}"), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to publish clan whisper to Redis for Clan {ClanId}", clanId);
            }
        }
    }

    public async Task<DistributedChannelMetadata> GetOrCreateChannelAsync(
        string channelName,
        Func<DistributedChannelMetadata> factory,
        CancellationToken ct = default)
    {
        if (_localChannelCache.TryGetValue(channelName, out DistributedChannelMetadata? existing))
        {
            return existing;
        }

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue val = await db.HashGetAsync(KeyChannels, channelName.ToLowerInvariant());
                if (!val.IsNullOrEmpty)
                {
                    DistributedChannelMetadata meta = MemoryPackSerializer.Deserialize<DistributedChannelMetadata>((byte[])val!)!;
                    _localChannelCache[channelName] = meta;
                    return meta;
                }

                DistributedChannelMetadata created = factory();
                byte[] bytes = MemoryPackSerializer.Serialize(created);
                await db.HashSetAsync(KeyChannels, channelName.ToLowerInvariant(), bytes);
                _localChannelCache[channelName] = created;
                return created;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to query/create channel in Redis for {ChannelName}", channelName);
            }
        }

        DistributedChannelMetadata fallback = factory();
        _localChannelCache[channelName] = fallback;
        return fallback;
    }

    public async Task<IReadOnlyList<DistributedChannelMetadata>> GetActiveChannelsAsync(CancellationToken ct = default)
    {
        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                HashEntry[] entries = await db.HashGetAllAsync(KeyChannels);
                List<DistributedChannelMetadata> list = [];
                foreach (HashEntry entry in entries)
                {
                    if (!entry.Value.IsNullOrEmpty)
                    {
                        DistributedChannelMetadata? meta = MemoryPackSerializer.Deserialize<DistributedChannelMetadata>((byte[])entry.Value!);
                        if (meta != null) list.Add(meta);
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to enumerate active channels from Redis.");
            }
        }

        return _localChannelCache.Values.ToList();
    }

    private async Task EnsureChannelSubscribedAsync(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return;

        string normalized = channelName.ToLowerInvariant();
        ChannelSubscriptionEntry entry = _channelSubscriptions.GetOrAdd(normalized, _ => new ChannelSubscriptionEntry());
        await entry.Gate.WaitAsync();
        try
        {
            entry.RefCount++;
            _channelSubscriptions[normalized] = entry;

            if (entry.RefCount == 1 && IsDistributed && _subscriber != null)
            {
                await _subscriber.SubscribeAsync(
                    RedisChannel.Literal($"{ChannelChanPrefix}{normalized}"),
                    OnChannelMessageReceived);
                _logger.LogInformation("[DistributedChatState] Subscribed to Redis channel multicast: {Channel}", normalized);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DistributedChatState] Failed to subscribe to Redis channel: {Channel}", normalized);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task ReleaseChannelSubscriptionAsync(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return;

        string normalized = channelName.ToLowerInvariant();
        if (_channelSubscriptions.TryGetValue(normalized, out ChannelSubscriptionEntry? entry))
        {
            await entry.Gate.WaitAsync();
            try
            {
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    _channelSubscriptions.TryRemove(normalized, out _);
                    if (IsDistributed && _subscriber != null)
                    {
                        await _subscriber.UnsubscribeAsync(
                            RedisChannel.Literal($"{ChannelChanPrefix}{normalized}"),
                            OnChannelMessageReceived);
                        _logger.LogInformation("[DistributedChatState] Unsubscribed from Redis channel multicast: {Channel}", normalized);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to unsubscribe from Redis channel: {Channel}", normalized);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private async Task EnsureClanSubscribedAsync(int clanId)
    {
        if (clanId <= 0) return;

        ClanSubscriptionEntry entry = _clanSubscriptions.GetOrAdd(clanId, _ => new ClanSubscriptionEntry());
        await entry.Gate.WaitAsync();
        try
        {
            entry.RefCount++;
            _clanSubscriptions[clanId] = entry;

            if (entry.RefCount == 1 && IsDistributed && _subscriber != null)
            {
                await _subscriber.SubscribeAsync(
                    RedisChannel.Literal($"{ChannelClanPrefix}{clanId}"),
                    OnClanWhisperReceived);
                _logger.LogInformation("[DistributedChatState] Subscribed to Redis clan multicast: Clan {ClanId}", clanId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DistributedChatState] Failed to subscribe to Redis clan: Clan {ClanId}", clanId);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task ReleaseClanSubscriptionAsync(int clanId)
    {
        if (clanId <= 0) return;

        if (_clanSubscriptions.TryGetValue(clanId, out ClanSubscriptionEntry? entry))
        {
            await entry.Gate.WaitAsync();
            try
            {
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    _clanSubscriptions.TryRemove(clanId, out _);
                    if (IsDistributed && _subscriber != null)
                    {
                        await _subscriber.UnsubscribeAsync(
                            RedisChannel.Literal($"{ChannelClanPrefix}{clanId}"),
                            OnClanWhisperReceived);
                        _logger.LogInformation("[DistributedChatState] Unsubscribed from Redis clan multicast: Clan {ClanId}", clanId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to unsubscribe from Redis clan: Clan {ClanId}", clanId);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    public async Task JoinChannelAsync(
        string channelName,
        int accountId,
        string accountName,
        byte adminLevel,
        CancellationToken ct = default)
    {
        DistributedChannelMember member = new()
        {
            AccountID = accountId,
            AccountName = accountName,
            NodeID = NodeId,
            AdminLevel = adminLevel,
            JoinedAtUtc = _timeProvider.GetUtcNow()
        };

        ConcurrentDictionary<string, DistributedChannelMember> members =
            _localChannelMembers.GetOrAdd(channelName, _ => new(StringComparer.OrdinalIgnoreCase));
        members[accountName] = member;

        await EnsureChannelSubscribedAsync(channelName);

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                byte[] bytes = MemoryPackSerializer.Serialize(member);
                await db.HashSetAsync($"kongor:chat:channel:{channelName.ToLowerInvariant()}:members", accountName.ToLowerInvariant(), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to register channel member in Redis: {AccountName} -> {ChannelName}", accountName, channelName);
            }
        }
    }

    public async Task LeaveChannelAsync(string channelName, string accountName, CancellationToken ct = default)
    {
        if (_localChannelMembers.TryGetValue(channelName, out ConcurrentDictionary<string, DistributedChannelMember>? members))
        {
            members.TryRemove(accountName, out _);
            if (members.IsEmpty)
            {
                _localChannelMembers.TryRemove(channelName, out _);
            }
        }

        await ReleaseChannelSubscriptionAsync(channelName);

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                await db.HashDeleteAsync($"kongor:chat:channel:{channelName.ToLowerInvariant()}:members", accountName.ToLowerInvariant());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to remove channel member from Redis: {AccountName} -> {ChannelName}", accountName, channelName);
            }
        }
    }

    public async Task SetChannelTopicAsync(string channelName, string topic, CancellationToken ct = default)
    {
        if (_localChannelCache.TryGetValue(channelName, out DistributedChannelMetadata? meta))
        {
            meta.Topic = topic;
        }

        if (IsDistributed && _redis != null)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                RedisValue val = await db.HashGetAsync(KeyChannels, channelName.ToLowerInvariant());
                if (!val.IsNullOrEmpty)
                {
                    DistributedChannelMetadata current = MemoryPackSerializer.Deserialize<DistributedChannelMetadata>((byte[])val!)!;
                    current.Topic = topic;
                    byte[] bytes = MemoryPackSerializer.Serialize(current);
                    await db.HashSetAsync(KeyChannels, channelName.ToLowerInvariant(), bytes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to update topic in Redis for {ChannelName}", channelName);
            }
        }
    }

    public async Task EvictConcurrentSessionAsync(int accountId, string accountName, byte reasonCode, CancellationToken ct = default)
    {
        if (IsDistributed && _redis != null)
        {
            try
            {
                DistributedEvictionEnvelope envelope = new()
                {
                    AccountID = accountId,
                    AccountName = accountName,
                    RequestingNodeID = NodeId,
                    ReasonCode = reasonCode
                };

                byte[] bytes = MemoryPackSerializer.Serialize(envelope);
                ISubscriber sub = _redis.GetSubscriber();
                await sub.PublishAsync(RedisChannel.Literal(ChannelEvictLiteral), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Failed to broadcast eviction notice for Account ID {AccountId}", accountId);
            }
        }
    }

    private void OnWhisperReceived(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty) return;

        try
        {
            DistributedWhisperEnvelope? envelope = MemoryPackSerializer.Deserialize<DistributedWhisperEnvelope>((byte[])value!);
            if (envelope is null) return;

            if (_chatContext.ClientChatSessions.TryGetValue(envelope.TargetName, out ChatSession? localSession))
            {
                DeliverWhisperToLocalSession(envelope.SenderName, envelope.SenderAccountID, localSession, envelope.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DistributedChatState] Error processing incoming cross-node whisper.");
        }
    }

    private void DeliverWhisperToLocalSession(string senderName, int senderId, ChatSession recipientSession, string message)
    {
        if (recipientSession.Account != null && recipientSession.Account.HasIgnoredPeer(senderId))
        {
            ChatBuffer notice = new();
            notice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            notice.WriteString("System");
            notice.WriteString($"[System] Player {recipientSession.Account.Name} has you on their ignore list.");

            if (_chatContext.ClientChatSessions.TryGetValue(senderName, out ChatSession? senderSession))
            {
                senderSession.Send(notice);
            }
            else
            {
                _ = RouteWhisperAsync("System", 0, senderName, $"[System] Player {recipientSession.Account.Name} has you on their ignore list.");
            }
            return;
        }

        ChatProtocol.ChatModeType mode = recipientSession.ClientMetadata?.ClientChatModeState ?? ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE;
        switch (mode)
        {
            case ChatProtocol.ChatModeType.CHAT_MODE_DND:
                SendWhisperAutoReply(senderName, recipientSession.Account?.Name ?? "Recipient", "Do Not Disturb");
                break;

            case ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE:
                // Treat as offline
                break;

            case ChatProtocol.ChatModeType.CHAT_MODE_AFK:
                SendDirectWhisperPacket(recipientSession, senderName, message);
                SendWhisperAutoReply(senderName, recipientSession.Account?.Name ?? "Recipient", "Away From Keyboard");
                break;

            default:
                SendDirectWhisperPacket(recipientSession, senderName, message);
                break;
        }
    }

    private static void SendDirectWhisperPacket(ChatSession session, string senderName, string message)
    {
        ChatBuffer whisper = new();
        whisper.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisper.WriteString(senderName);
        whisper.WriteString(message);
        session.Send(whisper);
    }

    private void SendWhisperAutoReply(string targetName, string senderName, string reason)
    {
        ChatBuffer autoReply = new();
        autoReply.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        autoReply.WriteString(senderName);
        autoReply.WriteString($"[{reason}]");

        if (_chatContext.ClientChatSessions.TryGetValue(targetName, out ChatSession? targetSession))
        {
            targetSession.Send(autoReply);
        }
        else
        {
            _ = RouteWhisperAsync(senderName, 0, targetName, $"[{reason}]");
        }
    }

    private void OnChannelMessageReceived(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty) return;

        try
        {
            DistributedChannelMessageEnvelope? envelope = MemoryPackSerializer.Deserialize<DistributedChannelMessageEnvelope>((byte[])value!);
            if (envelope is null || envelope.OriginNodeID == NodeId)
            {
                return; // Already dispatched locally by the sending node
            }

            if (_chatContext.ChatChannels.TryGetValue(envelope.ChannelName, out ChatChannel? localChannel))
            {
                localChannel.DeliverRemoteClusterMessage(envelope.SenderAccountID, envelope.SenderName, envelope.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DistributedChatState] Error processing incoming channel broadcast message.");
        }
    }

    private void OnClanWhisperReceived(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty) return;

        try
        {
            DistributedClanWhisperEnvelope? envelope = MemoryPackSerializer.Deserialize<DistributedClanWhisperEnvelope>((byte[])value!);
            if (envelope is null || envelope.OriginNodeID == NodeId)
            {
                return;
            }

            ClanWhisperResponse response = new(envelope.SenderAccountID, envelope.Message);
            if (_clanSessionsByClanId.TryGetValue(envelope.ClanID, out var clanSessions))
            {
                foreach (ChatSession clientSession in clanSessions.Values)
                {
                    if (clientSession.Account is { ID: var accId } && accId != envelope.SenderAccountID &&
                        !clientSession.Account.HasIgnoredPeer(envelope.SenderAccountID))
                    {
                        clientSession.Send(response);
                    }
                }
            }
            else
            {
                foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
                {
                    if (clientSession.Account is { Clan: not null } &&
                        clientSession.Account.Clan.ID == envelope.ClanID &&
                        clientSession.Account.ID != envelope.SenderAccountID &&
                        !clientSession.Account.HasIgnoredPeer(envelope.SenderAccountID))
                    {
                        clientSession.Send(response);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DistributedChatState] Error processing incoming clan whisper.");
        }
    }

    private void OnEvictionReceived(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty) return;

        try
        {
            DistributedEvictionEnvelope? envelope = MemoryPackSerializer.Deserialize<DistributedEvictionEnvelope>((byte[])value!);
            if (envelope is null || envelope.RequestingNodeID == NodeId)
            {
                return;
            }

            if (!_sessionsByAccountId.TryGetValue(envelope.AccountID, out ChatSession? match))
            {
                match = _chatContext.ClientChatSessions.Values
                    .FirstOrDefault(s => s.Account?.ID == envelope.AccountID);
            }

            if (match != null)
            {
                _logger.LogWarning("[DistributedChatState] Evicting local session for Account ID {AccountId} ({AccountName}) due to remote cluster login from node {RemoteNode}",
                    envelope.AccountID, envelope.AccountName, envelope.RequestingNodeID);

                match.Reject((ChatProtocol.ChatRejectReason)envelope.ReasonCode).TerminateClient();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DistributedChatState] Error processing incoming cluster eviction.");
        }
    }

    private void OnPeerInvalidationReceived(RedisChannel channel, RedisValue value)
    {
        if (value.IsNullOrEmpty) return;

        string payload = (string)value!;
        _ = Task.Run(async () =>
        {
            try
            {
                Infrastructure.Services.PeerInvalidationSubscriber? subscriber =
                    _serviceProvider?.GetService<Infrastructure.Services.PeerInvalidationSubscriber>();

                if (subscriber is not null)
                {
                    await subscriber.ProcessInvalidationPayloadAsync(payload);
                    return;
                }

                if (_serviceProvider is not null)
                {
                    var fallback = new Infrastructure.Services.PeerInvalidationSubscriber(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.PeerInvalidationSubscriber>.Instance,
                        _serviceProvider,
                        _chatContext);
                    await fallback.ProcessInvalidationPayloadAsync(payload);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Error processing peer invalidation notice.");
            }
        });
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10), _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                if (IsDistributed && _redis != null)
                {
                    IDatabase db = _redis.GetDatabase();
                    await db.HashSetAsync(KeyNodes, NodeId, _timeProvider.GetUtcNow().ToUnixTimeSeconds()).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DistributedChatState] Heartbeat loop encountered an error: {Message}", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();

        foreach (ChannelSubscriptionEntry entry in _channelSubscriptions.Values)
        {
            entry.Dispose();
        }
        _channelSubscriptions.Clear();

        foreach (ClanSubscriptionEntry entry in _clanSubscriptions.Values)
        {
            entry.Dispose();
        }
        _clanSubscriptions.Clear();
    }
}
