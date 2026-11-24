// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable ReservedKeyword

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Social;

[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Domain entity representing a social friend relationship")]
[SuppressMessage("ReSharper", "ReservedKeyword",
    Justification = "Domain entity representing a social friend relationship")]
public class Friend
{
    /// <summary>
    ///     Hidden Constructor Which Enforces <see cref="WithAccountName" /> As The Primary Mechanism For Creating Friends
    /// </summary>
    private Friend()
    {
    }

    public required string AccountName { get; init; }

    public static Friend WithAccountName(string accountName)
    {
        string normalized = MERRICK.DatabaseContext.Extensions.AccountExtensions
            .SeparateClanTagFromAccountName((accountName ?? string.Empty).Trim())
            .AccountName
            .Trim();
        return new Friend { AccountName = normalized };
    }

    /// <summary>
    ///     Sends a friend request to the target account.
    ///     If the target account has already made a friend request to the requester, creates bi-directional friendship
    ///     immediately.
    ///     Otherwise, stores pending request in the distributed cache and notifies the target account.
    /// </summary>
    public async Task Add(ChatSession session, MerrickContext merrick, IDatabase distributedCacheStore,
        IChatContext chatContext)
    {
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.GENERIC_FAILURE, null);

            return;
        }

        string normalizedLower = AccountName.ToLowerInvariant();

        // Look Up Target Account In Database (Peers backs the [NotMapped] IgnoredPeers / BannedPeers properties)
        Account? targetAccount = await merrick.Accounts
            .Include(account => account.Clan)
            .Include(account => account.Peers)
            .SingleOrDefaultAsync(account => account.Name.ToLower() == normalizedLower);

        // Target Account Not Found Or Self
        if (targetAccount is null || targetAccount.ID == session.Account.ID)
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.GENERIC_FAILURE, targetAccount);

            return;
        }

        // Load Requester's Account With Clan
        Account requesterAccount = await merrick.Accounts
            .Include(account => account.Clan)
            .SingleAsync(account => account.ID == session.Account.ID);

        // Check If Requester Is On Target's Ignore List
        bool isIgnored = targetAccount.IgnoredPeers.Any(peer => peer.ID == requesterAccount.ID);

        if (isIgnored)
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.BANNED_OR_IGNORED, targetAccount);

            return;
        }

        // Check If Requester Is On Target's Ban List
        bool isBanned = targetAccount.BannedPeers.Any(peer => peer.ID == requesterAccount.ID);

        if (isBanned)
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.BANNED_OR_IGNORED, targetAccount);

            return;
        }

        // Check If Already Friends via authoritative core.Friendships
        bool alreadyFriends = await merrick.Friendships
            .AnyAsync(f => f.AccountID == requesterAccount.ID && f.FriendAccountID == targetAccount.ID && f.Status == FriendshipStatus.Accepted);

        if (alreadyFriends)
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.DUPLICATE_RECORD, targetAccount);

            return;
        }

        // Check If Requester Has Reached Friend Limit
        int friendCount = await merrick.Friendships
            .CountAsync(f => f.AccountID == requesterAccount.ID && f.Status == FriendshipStatus.Accepted);

        if (friendCount >= ChatProtocol.FRIEND_LIMIT)
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.FRIEND_LIMIT_REACHED, targetAccount);

            return;
        }

        // Check Whether Pending Friend Request Already Exists
        bool existingPendingRequest =
            await distributedCacheStore.PendingFriendRequestExists(requesterAccount.ID, targetAccount.ID);

        if (existingPendingRequest)
        {
            SendFriendAddFailure(session, ChatProtocol.FriendAddStatus.DUPLICATE_RECORD, targetAccount);

            return;
        }

        // Check Whether The Target Account Has Already Sent A Friend Request To The Requester
        (int RequesterNotificationID, int TargetNotificationID)? mutualRequest =
            await distributedCacheStore.GetFriendRequest(targetAccount.ID, requesterAccount.ID);

        if (mutualRequest is not null)
        {
            // Request Is Mutual: Both Accounts Become Friends Immediately
            await CreateMutualFriendship(requesterAccount, targetAccount, merrick, distributedCacheStore, chatContext);

            // Create Notifications (Type 2: notify_buddy_added) for BOTH parties
            Notification requesterNotification =
                CreateNotification(2, "notify_buddy_added", targetAccount, requesterAccount, merrick);
            Notification targetNotification =
                CreateNotification(2, "notify_buddy_added", requesterAccount, targetAccount, merrick);

            // Send Approval Success Notification To Requester
            // NOTE: Current Requester Was Target Of Original Request
            SendFriendRequestApproval(session, targetAccount, ChatProtocol.FriendApproveStatus.SUCCESS_REQUESTER,
                requesterNotification.NotificationId);

            chatContext.ClientChatSessions.TryGetValue(targetAccount.Name, out ChatSession? targetSession);

            // Send Approval Success Notification To Target, If Online
            if (targetSession is not null)
            {
                SendFriendRequestApproval(targetSession, requesterAccount,
                    ChatProtocol.FriendApproveStatus.SUCCESS_APPROVER, targetNotification.NotificationId);
                targetSession.BroadcastConnectionStatusUpdate(targetSession.ClientMetadata.LastKnownClientState);
            }

            session.BroadcastConnectionStatusUpdate(session.ClientMetadata.LastKnownClientState);

            return;
        }

        // Request Is Not Mutual: Generate Both Notification IDs
        int requesterNotificationID = GenerateNotificationID();
        int targetNotificationID = GenerateNotificationID();

        // Store Pending Friend Request In Distributed Cache Store
        await distributedCacheStore.SetFriendRequest(requesterAccount.ID, targetAccount.ID, requesterNotificationID,
            targetNotificationID);

        // Send Success Response To Requester (Friend Request Created Successfully)
        SendFriendAddSuccess(session, targetAccount, requesterNotificationID);

        chatContext.ClientChatSessions.TryGetValue(targetAccount.Name, out ChatSession? targetOnlineSession);

        // Send Friend Request Notification To Target, If Online
        if (targetOnlineSession is not null &&
            targetOnlineSession.ClientMetadata.ClientChatModeState is not ChatProtocol.ChatModeType.CHAT_MODE_DND)
        {
            Notification notification = CreateNotification(23, "notify_buddy_requested_added", requesterAccount,
                targetAccount, merrick);

            SendFriendRequest(targetOnlineSession, requesterAccount, notification.NotificationId);
        }
    }

    /// <summary>
    ///     Approves a pending friend request from another account.
    ///     Creates bi-directional friendship and sends approval responses to both clients.
    /// </summary>
    public async Task Approve(ChatSession session, MerrickContext merrick, IDatabase distributedCacheStore,
        IChatContext chatContext)
    {
        if (string.IsNullOrWhiteSpace(AccountName)) return;

        // Load Approver's Account
        Account approverAccount = await merrick.Accounts
            .Include(account => account.Clan)
            .SingleAsync(account => account.ID == session.Account.ID);

        // Look Up Requester Account
        string normalizedLower = AccountName.ToLowerInvariant();
        Account? requesterAccount = await merrick.Accounts
            .Include(account => account.Clan)
            .SingleOrDefaultAsync(account => account.Name.ToLower() == normalizedLower);

        if (requesterAccount is null || requesterAccount.ID == approverAccount.ID) return;

        // Check Whether Pending Friend Request Exists Or Not
        (int RequesterNotificationID, int TargetNotificationID)? pendingRequest =
            await distributedCacheStore.GetFriendRequest(requesterAccount.ID, approverAccount.ID);

        // Create Bi-Directional Friendship
        await CreateMutualFriendship(requesterAccount, approverAccount, merrick, distributedCacheStore, chatContext);

        // Create Notifications (Type 2: notify_buddy_added) for BOTH parties
        Notification approverNotification =
            CreateNotification(2, "notify_buddy_added", requesterAccount, approverAccount, merrick);
        Notification requesterNotification =
            CreateNotification(2, "notify_buddy_added", approverAccount, requesterAccount, merrick);

        int approverNotificationId = pendingRequest?.TargetNotificationID > 0
            ? pendingRequest.Value.TargetNotificationID
            : approverNotification.NotificationId;

        int requesterNotificationId = pendingRequest?.RequesterNotificationID > 0
            ? pendingRequest.Value.RequesterNotificationID
            : requesterNotification.NotificationId;

        // Send Approval Request To Approver
        SendFriendRequestApproval(session, requesterAccount, ChatProtocol.FriendApproveStatus.SUCCESS_APPROVER,
            approverNotificationId);

        chatContext.ClientChatSessions.TryGetValue(requesterAccount.Name, out ChatSession? requesterSession);

        // Send Approval Response To Requester, If Online
        if (requesterSession is not null)
        {
            SendFriendRequestApproval(requesterSession, approverAccount,
                ChatProtocol.FriendApproveStatus.SUCCESS_REQUESTER, requesterNotificationId);
            requesterSession.BroadcastConnectionStatusUpdate(requesterSession.ClientMetadata.LastKnownClientState);
        }

        session.BroadcastConnectionStatusUpdate(session.ClientMetadata.LastKnownClientState);
    }

    /// <summary>
    ///     Creates bi-directional friendship between two accounts in core.Friendships and removes any pending requests from the cache store.
    /// </summary>
    private static async Task CreateMutualFriendship(Account one, Account two, MerrickContext merrick,
        IDatabase distributedCacheStore, IChatContext? chatContext = null)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        // Check and persist forward friendship (one -> two)
        Friendship? friendshipOneToTwo = await merrick.Friendships
            .FirstOrDefaultAsync(f => f.AccountID == one.ID && f.FriendAccountID == two.ID);

        if (friendshipOneToTwo == null)
        {
            friendshipOneToTwo = new Friendship
            {
                AccountID = one.ID,
                FriendAccountID = two.ID,
                FriendAccount = two,
                FriendGroup = "DEFAULT",
                Status = FriendshipStatus.Accepted,
                TimestampCreated = now
            };
            merrick.Friendships.Add(friendshipOneToTwo);
        }
        else
        {
            friendshipOneToTwo.Status = FriendshipStatus.Accepted;
            friendshipOneToTwo.TimestampUpdated = now;
        }

        // Check and persist reciprocal friendship (two -> one)
        Friendship? friendshipTwoToOne = await merrick.Friendships
            .FirstOrDefaultAsync(f => f.AccountID == two.ID && f.FriendAccountID == one.ID);

        if (friendshipTwoToOne == null)
        {
            friendshipTwoToOne = new Friendship
            {
                AccountID = two.ID,
                FriendAccountID = one.ID,
                FriendAccount = one,
                FriendGroup = "DEFAULT",
                Status = FriendshipStatus.Accepted,
                TimestampCreated = now
            };
            merrick.Friendships.Add(friendshipTwoToOne);
        }
        else
        {
            friendshipTwoToOne.Status = FriendshipStatus.Accepted;
            friendshipTwoToOne.TimestampUpdated = now;
        }

        // Persist Changes To The Database
        await merrick.SaveChangesAsync();

        // Synchronize in-memory ChatSession Account.Friendships collections
        if (chatContext != null)
        {
            if (chatContext.ClientChatSessions.TryGetValue(one.Name, out ChatSession? sessionOne) && sessionOne.Account != null)
            {
                sessionOne.Account.Friendships ??= [];
                if (!sessionOne.Account.Friendships.Any(f => f.FriendAccountID == two.ID))
                {
                    sessionOne.Account.Friendships.Add(friendshipOneToTwo);
                    Log.Information("[Friend] Synchronized in-memory friendship: Added {TargetName} (ID: {TargetID}) to {AccountName}'s live session",
                        two.Name, two.ID, one.Name);
                }
            }

            if (chatContext.ClientChatSessions.TryGetValue(two.Name, out ChatSession? sessionTwo) && sessionTwo.Account != null)
            {
                sessionTwo.Account.Friendships ??= [];
                if (!sessionTwo.Account.Friendships.Any(f => f.FriendAccountID == one.ID))
                {
                    sessionTwo.Account.Friendships.Add(friendshipTwoToOne);
                    Log.Information("[Friend] Synchronized in-memory friendship: Added {TargetName} (ID: {TargetID}) to {AccountName}'s live session",
                        one.Name, one.ID, two.Name);
                }
            }
        }

        // Remove Any Pending Requests From The Distributed Cache Store
        await distributedCacheStore.RemoveFriendRequest(one.ID, two.ID);
        await distributedCacheStore.RemoveFriendRequest(two.ID, one.ID);
    }

    /// <summary>
    ///     Generates a unique notification ID for friend request tracking.
    /// </summary>
    private static int GenerateNotificationID()
    {
        return Guid.CreateVersion7().GetDeterministicInt32Hash();
    }

    /// <summary>
    ///     Sends friend request notification to the target player for approval.
    /// </summary>
    private static void SendFriendRequest(ChatSession targetSession, Account requesterAccount, int notificationID)
    {
        ChatBuffer notification = new();

        notification.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE);
        notification.WriteInt8(Convert.ToByte(ChatProtocol.FriendAddStatus
            .SUCCESS_REQUESTEE, CultureInfo.InvariantCulture)); // Friend Addition Status
        notification.WriteInt32(notificationID); // Notification ID For Tracking This Request
        notification.WriteString(requesterAccount.GetNameWithClanTag()); // Requester's Display Name With Clan Tag

        targetSession.Send(notification);
    }

    /// <summary>
    ///     Sends friend approval response.
    /// </summary>
    private static void SendFriendRequestApproval(ChatSession session, Account friendAccount,
        ChatProtocol.FriendApproveStatus status, int notificationID)
    {
        ChatBuffer response = new();

        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_APPROVE_RESPONSE);
        response.WriteInt8(Convert.ToByte(status, CultureInfo.InvariantCulture)); // Friend Approval Status
        response.WriteInt32(friendAccount.ID); // Friend's Account ID
        response.WriteInt32(notificationID); // Notification ID For Tracking The Friend Request
        response.WriteString(friendAccount.GetNameWithClanTag()); // Friend's Display Name With Clan Tag

        session.Send(response);
    }

    /// <summary>
    ///     Sends friend add success response to the requester.
    ///     This indicates that the friend request was created successfully, not that the friendship has been established.
    /// </summary>
    private static void SendFriendAddSuccess(ChatSession session, Account friendAccount, int notificationID)
    {
        ChatBuffer response = new();

        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE);
        response.WriteInt8(Convert.ToByte(ChatProtocol.FriendAddStatus
                .SUCCESS_REQUESTER,
            CultureInfo.InvariantCulture)); // Success Indicator For The Client Who Initiated The Friend Request
        response.WriteInt32(notificationID); // Requester's Notification ID For This Friend Request
        response.WriteString(friendAccount.GetNameWithClanTag()); // Friend's Display Name With Clan Tag
        response.WriteInt32(friendAccount.ID); // Friend's Account ID
        response.WriteInt8(Convert.ToByte(ChatProtocol.ChatClientStatus
            .CHAT_CLIENT_STATUS_CONNECTED, CultureInfo.InvariantCulture)); // Friend's Current Connection Status
        response.WriteInt8(friendAccount.GetChatClientFlags()); // Friend's Account Flags
        response.WriteInt32(friendAccount.Clan?.ID ?? 0); // Friend's Clan ID (Zero If Not In A Clan)
        response.WriteString(friendAccount.Clan?.Name ??
                             string.Empty); // Friend's Clan Name (Empty String If Not In A Clan)
        response.WriteString(friendAccount.GetChatSymbolNoPrefixCode()); // Friend's Chat Symbol
        response.WriteString(friendAccount.GetNameColourNoPrefixCode()); // Friend's Name Colour
        response.WriteString(friendAccount.GetIconNoPrefixCode()); // Friend's Account Icon
        response.WriteInt32(friendAccount.AscensionLevel); // Friend's Ascension Level

        session.Send(response);
    }

    /// <summary>
    ///     Sends friend add failure response with the specific failure reason.
    /// </summary>
    private void SendFriendAddFailure(ChatSession session, ChatProtocol.FriendAddStatus reason, Account? targetAccount)
    {
        ChatBuffer response = new();

        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE);
        response.WriteInt8(Convert.ToByte(reason, CultureInfo.InvariantCulture)); // Friend Addition Failure Reason
        response.WriteInt32(0); // Notification ID (Zero Indicates Failure)
        response.WriteString(targetAccount?.GetNameWithClanTag() ??
                             AccountName); // Target Display Name With Clan Tag (Or Plain Name If Account Not Found)

        session.Send(response);
    }

    /// <summary>
    ///     Creates and persists a notification entity following the legacy format.
    ///     Format: Nick|unknown|type|kind|type_string|action|timestamp|ID
    /// </summary>
    private static Notification CreateNotification(int type, string kind, Account fromWhom, Account toWhom,
        MerrickContext merrick)
    {
        Notification notification = new()
        {
            AccountId = toWhom.ID,
            Content = string.Empty,
            TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
        };

        merrick.Notifications.Add(notification);

        merrick.SaveChanges(); // Save to generate ID.

        // Nick|unknown|type number|translation string|type string|action|timestring|ID
        notification.Content = string.Join(
            '|',
            fromWhom.Name, // Nick
            "", // unknown
            type, // type number
            kind, // translation string
            "notfication_generic_action", // type string (legacy typo 'notfication' preserved)
            "action_friend_request", // action
            notification.TimestampCreated.ToString("MM/dd/yy HH:mm tt", CultureInfo.InvariantCulture),
            notification.NotificationId // ID
        );

        // Update with content
        merrick.SaveChanges();

        return notification;
    }
}
