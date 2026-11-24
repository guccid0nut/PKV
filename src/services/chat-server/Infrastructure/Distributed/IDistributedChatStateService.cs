// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Chat;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

/// <summary>
///     Defines distributed cross-node chat coordination across the TRANSMUTANSTEIN cluster.
/// </summary>
public interface IDistributedChatStateService
{
    /// <summary>
    ///     Unique identifier of this chat server node instance in the cluster.
    /// </summary>
    string NodeId { get; }

    /// <summary>
    ///     Indicates whether distributed multi-node coordination via Redis is active.
    /// </summary>
    bool IsDistributed { get; }

    /// <summary>
    ///     Registers a local session in memory for fast O(1) clan and account lookups.
    /// </summary>
    void RegisterLocalSession(ChatSession session);

    /// <summary>
    ///     Unregisters a local session in memory and cleans up empty clan indices.
    /// </summary>
    void UnregisterLocalSession(ChatSession session);

    /// <summary>
    ///     Updates clan indexing when a user joins, leaves, or switches clans.
    /// </summary>
    void UpdateSessionClan(ChatSession session, int? oldClanId, int? newClanId);

    /// <summary>
    ///     Registers an authenticated client session into cluster presence.
    /// </summary>
    Task RegisterSessionAsync(ChatSession session, CancellationToken ct = default);

    /// <summary>
    ///     Removes a disconnected client session from cluster presence.
    /// </summary>
    Task UnregisterSessionAsync(string accountName, int accountId, CancellationToken ct = default);

    /// <summary>
    ///     Removes a batch of disconnected client sessions from cluster presence in a single pipelined operation.
    /// </summary>
    Task<int> UnregisterSessionsBatchAsync(IReadOnlyList<(string accountName, int accountId)> sessions, CancellationToken ct = default);

    /// <summary>
    ///     Reaps all cluster presences associated with a dead or partitioned node instance.
    /// </summary>
    Task<int> ReapDeadNodeSessionsAsync(string deadNodeId, CancellationToken ct = default);

    /// <summary>
    ///     Updates a user's cluster presence state (e.g. In-Game, AFK, DND).
    /// </summary>
    Task UpdatePresenceAsync(
        int accountId,
        string accountName,
        ChatProtocol.ChatClientStatus status,
        ChatProtocol.ChatModeType chatMode,
        string? matchAddress = null,
        int? matchId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves cluster presence for a user by account nickname.
    /// </summary>
    Task<DistributedUserPresence?> GetUserPresenceAsync(string accountName, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves cluster presence for a user by numerical account ID.
    /// </summary>
    Task<DistributedUserPresence?> GetUserPresenceByIdAsync(int accountId, CancellationToken ct = default);

    /// <summary>
    ///     Routes a whisper across the cluster to the target recipient's host node.
    /// </summary>
    Task<bool> RouteWhisperAsync(
        string senderName,
        int senderId,
        string recipientName,
        string message,
        CancellationToken ct = default);

    /// <summary>
    ///     Publishes a channel message across the cluster to all nodes hosting active channel members.
    /// </summary>
    Task PublishChannelMessageAsync(
        int channelId,
        string channelName,
        int senderId,
        string senderName,
        string message,
        CancellationToken ct = default);

    /// <summary>
    ///     Publishes a clan whisper message to all nodes hosting members of the specified clan.
    /// </summary>
    Task PublishClanWhisperAsync(
        int clanId,
        int senderId,
        string senderName,
        string message,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves or allocates cluster-wide channel metadata.
    /// </summary>
    Task<DistributedChannelMetadata> GetOrCreateChannelAsync(
        string channelName,
        Func<DistributedChannelMetadata> factory,
        CancellationToken ct = default);

    /// <summary>
    ///     Enumerates all active channels across the cluster.
    /// </summary>
    Task<IReadOnlyList<DistributedChannelMetadata>> GetActiveChannelsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Records a member joining a channel in the cluster roster.
    /// </summary>
    Task JoinChannelAsync(
        string channelName,
        int accountId,
        string accountName,
        byte adminLevel,
        CancellationToken ct = default);

    /// <summary>
    ///     Removes a member from a channel in the cluster roster.
    /// </summary>
    Task LeaveChannelAsync(string channelName, string accountName, CancellationToken ct = default);

    /// <summary>
    ///     Updates the cluster-wide topic for a channel.
    /// </summary>
    Task SetChannelTopicAsync(string channelName, string topic, CancellationToken ct = default);

    /// <summary>
    ///     Dispatches an eviction notice to terminate duplicate concurrent sessions on remote nodes.
    /// </summary>
    Task EvictConcurrentSessionAsync(
        int accountId,
        string accountName,
        byte reasonCode,
        CancellationToken ct = default);
}
