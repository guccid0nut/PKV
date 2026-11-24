// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using NEXUS.Contracts.Data;

namespace TRANSMUTANSTEIN.ChatServer.Infrastructure.Services;

/// <summary>
///     Synchronizes live <see cref="ChatSession" /> <c>Account.Peers</c> and <c>Account.Friendships</c> collections
///     when <c>account:peer_invalidation</c> events are published by MasterServer (<c>FriendsHandler</c>), using
///     <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}" /> queries and pushing immediate
///     <c>0x000C</c> status updates when friendships are added or removed.
/// </summary>
public sealed class PeerInvalidationSubscriber(
    ILogger<PeerInvalidationSubscriber> logger,
    IServiceProvider serviceProvider,
    IChatContext chatContext)
{
    public const string ChannelName = "account:peer_invalidation";

    public async Task ProcessInvalidationPayloadAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return;

        int accountId = 0;
        int peerAccountId = 0;
        string? type = null;
        string? action = null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("AccountId", out JsonElement accProp) ||
                root.TryGetProperty("account_id", out accProp))
            {
                accountId = accProp.GetInt32();
            }

            if (root.TryGetProperty("PeerAccountId", out JsonElement peerProp) ||
                root.TryGetProperty("peer_account_id", out peerProp))
            {
                peerAccountId = peerProp.GetInt32();
            }

            if (root.TryGetProperty("Type", out JsonElement typeProp) ||
                root.TryGetProperty("type", out typeProp))
            {
                type = typeProp.GetString();
            }

            if (root.TryGetProperty("Action", out JsonElement actionProp) ||
                root.TryGetProperty("action", out actionProp))
            {
                action = actionProp.GetString();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PeerInvalidationSubscriber] Invalid JSON payload on '{Channel}': {Payload}",
                ChannelName, payloadJson);
            return;
        }

        if (accountId <= 0 && peerAccountId <= 0) return;

        await ProcessInvalidationAsync(accountId, peerAccountId, type, action, cancellationToken);
    }

    public async Task ProcessInvalidationAsync(
        int accountId,
        int peerAccountId,
        string? type,
        string? action,
        CancellationToken cancellationToken = default)
    {
        ChatSession? accountSession = accountId > 0
            ? chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
                (object?)s.Account is not null && s.Account.ID == accountId)
            : null;

        ChatSession? peerSession = peerAccountId > 0
            ? chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
                (object?)s.Account is not null && s.Account.ID == peerAccountId)
            : null;

        if (accountSession is null && peerSession is null)
        {
            logger.LogDebug(
                "[PeerInvalidationSubscriber] Neither Account #{AccountId} nor Peer #{PeerAccountId} is online locally (Type={Type}, Action={Action})",
                accountId, peerAccountId, type ?? "unknown", action ?? "unknown");
            return;
        }

        try
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            MerrickContext? merrick = scope.ServiceProvider.GetService<MerrickContext>();
            IAccountRepository? accountRepo = scope.ServiceProvider.GetService<IAccountRepository>();

            if (accountSession is not null)
            {
                await RefreshSessionCollectionsAsync(accountSession, merrick, accountRepo, cancellationToken);
            }

            if (peerSession is not null && peerSession.Account.ID != accountId)
            {
                await RefreshSessionCollectionsAsync(peerSession, merrick, accountRepo, cancellationToken);
            }

            if (accountSession is not null &&
                peerSession is not null &&
                string.Equals(type, "Friend", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(action, "Added", StringComparison.OrdinalIgnoreCase))
                {
                    accountSession.SendDirectStatusUpdateTo(peerSession);
                    peerSession.SendDirectStatusUpdateTo(accountSession);
                    logger.LogInformation(
                        "[PeerInvalidationSubscriber] Pushed mutual 0x000C status sync after Friend Added between '{AccA}' (#{IdA}) and '{AccB}' (#{IdB})",
                        accountSession.Account.Name, accountId, peerSession.Account.Name, peerAccountId);
                }
                else if (string.Equals(action, "Removed", StringComparison.OrdinalIgnoreCase))
                {
                    bool shareClan = accountSession.Account.Clan?.ID is > 0 &&
                                     accountSession.Account.Clan.ID == peerSession.Account.Clan?.ID;
                    if (!shareClan)
                    {
                        accountSession.SendDirectStatusUpdateTo(
                            peerSession,
                            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);
                        peerSession.SendDirectStatusUpdateTo(
                            accountSession,
                            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);
                        logger.LogInformation(
                            "[PeerInvalidationSubscriber] Pushed mutual 0x000C DISCONNECTED after Friend Removed between '{AccA}' (#{IdA}) and '{AccB}' (#{IdB})",
                            accountSession.Account.Name, accountId, peerSession.Account.Name, peerAccountId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[PeerInvalidationSubscriber] Failed refreshing peer/friendship state for Account #{AccountId} <-> Peer #{PeerAccountId} (Type={Type}, Action={Action})",
                accountId, peerAccountId, type ?? "unknown", action ?? "unknown");
        }
    }

    private async Task RefreshSessionCollectionsAsync(
        ChatSession session,
        MerrickContext? merrick,
        IAccountRepository? accountRepo,
        CancellationToken cancellationToken)
    {
        int id = session.Account.ID;

        if (merrick is not null)
        {
            List<AccountPeer> peers = await merrick.AccountPeers
                .AsNoTracking()
                .Include(p => p.PeerAccount)
                .Where(p => p.AccountID == id)
                .ToListAsync(cancellationToken);

            List<Friendship> friendships = await merrick.Friendships
                .AsNoTracking()
                .Include(f => f.FriendAccount)
                .Where(f => f.AccountID == id)
                .ToListAsync(cancellationToken);

            session.Account.Peers = peers;
            session.Account.Friendships = friendships;

            logger.LogInformation(
                "[PeerInvalidationSubscriber] Refreshed live session '{AccountName}' (#{AccountId}) via AsNoTracking(): {PeerCount} peers, {FriendCount} friendships",
                session.Account.Name, id, peers.Count, friendships.Count);
            return;
        }

        if (accountRepo is not null)
        {
            IReadOnlyList<AccountPeer> peers = await accountRepo.GetPeersAsync(id, ct: cancellationToken);
            session.Account.Peers = [.. peers];

            logger.LogInformation(
                "[PeerInvalidationSubscriber] Refreshed live session '{AccountName}' (#{AccountId}) via IAccountRepository: {PeerCount} peers",
                session.Account.Name, id, peers.Count);
        }
    }
}
