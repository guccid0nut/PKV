// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable CollectionNeverQueried.Global
// ReSharper disable SimplifyLinqExpressionUseAll
// ReSharper disable LoopCanBeConvertedToQuery

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Extensions;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Public GUID property")]
[SuppressMessage("ReSharper", "IdentifierNameContainsTypeName", Justification = "Public GUID property")]
[SuppressMessage("ReSharper", "CollectionNeverQueried.Global", Justification = "Bot slot state for practice lobbies")]
public class MatchmakingGroup
{
    private readonly Lock _lock = new();
    private MatchmakingService? _ownerMatchmakingService;

    internal void BindMatchmakingService(MatchmakingService service)
    {
        _ownerMatchmakingService = service;
    }

    private void NotifyDistributedStateChanged(string reason)
    {
        _ownerMatchmakingService?.NotifyGroupStateChanged(this, reason);
    }

    /// <summary>
    ///     Hidden Constructor Which Enforces <see cref="Create" /> As The Primary Mechanism For Creating Matchmaking Groups
    /// </summary>
    public MatchmakingGroup()
    {
    }

    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Public GUID property")]
    [SuppressMessage("ReSharper", "IdentifierNameContainsTypeName", Justification = "Public GUID property")]
    public Guid GUID { get; } = Guid.CreateVersion7();

    public MatchmakingGroupMember Leader =>
        Members.FirstOrDefault(member => member.IsLeader) ?? Members.FirstOrDefault()!;

    public required List<MatchmakingGroupMember> Members { get; set; }

    public required MatchmakingGroupInformation Information { get; set; }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global",
        Justification = "Bot slot state for practice lobbies")]
    public string[] AllyBots { get; } = new string[5];

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global",
        Justification = "Bot slot state for practice lobbies")]
    public string[] EnemyBots { get; } = new string[5];

    /// <summary>
    ///     Tracks users who have been invited to this group but have not yet joined.
    ///     Used by <see cref="MatchmakingService.GetMatchmakingGroupByInvitedUser" /> to route JOIN requests
    ///     when the client sends the Invitee's name instead of the Leader's name.
    ///     Key: Invitee Account Name (Case-Insensitive).
    ///     Value: Timestamp of invitation (for future expiration logic).
    /// </summary>
    public ConcurrentDictionary<string, DateTimeOffset> PendingInvites { get; } = new(StringComparer.OrdinalIgnoreCase);

    public float AverageRating => Members.Count > 0 ? (float)Members.Average(member => member.Rating) : 0f;

    public float RatingDisparity => Members.Count > 0
        ? (float)(Members.Max(member => member.Rating) - Members.Min(member => member.Rating))
        : 0;

    public int FullTeamDifference => Information.TeamSize - Members.Count;

    public DateTimeOffset? QueueStartTime { get; set; }

    public TimeSpan QueueDuration =>
        QueueStartTime is not null ? TimeProvider.System.GetUtcNow() - QueueStartTime.Value : TimeSpan.Zero;

    public void UpdateBot(byte team, byte slot, string botName)
    {
        if (slot > 4) return;

        switch (team)
        {
            case 1:
                AllyBots[slot] = botName;
                break;
            case 2:
                EnemyBots[slot] = botName;
                break;
            default:
                return;
        }

        ChatBuffer update = new();
        update.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_BOT_GROUP_UPDATE);
        update.WriteInt8(team);
        update.WriteInt8(slot);
        update.WriteString(botName);

        foreach (MatchmakingGroupMember member in Members) member.Session.Send(update);
    }

    public static MatchmakingGroup GetByMemberAccountID(IMatchmakingService matchmakingService, int accountID)
    {
        MatchmakingGroup group = matchmakingService.GetMatchmakingGroup(accountID)
                                 ?? throw new InvalidOperationException(
                                     $@"No Matchmaking Group Found For Account ID ""{accountID}""");

        return group;
    }

    public static MatchmakingGroup GetByMemberAccountName(IMatchmakingService matchmakingService, string accountName)
    {
        MatchmakingGroup group = matchmakingService.GetMatchmakingGroup(accountName)
                                 ?? throw new InvalidOperationException(
                                     $@"No Matchmaking Group Found For Account Name ""{accountName}""");

        return group;
    }

    public static MatchmakingGroup Create(IMatchmakingService matchmakingService, ChatSession session,
        MerrickContext merrick, MatchmakingGroupInformation information)
    {
        AccountStatistics? stats = merrick.AccountStatistics.SingleOrDefault(s =>
            s.AccountID == session.Account.ID && s.Type == AccountStatisticsType.Matchmaking);
        double rating = stats?.SkillRating ?? 1500.0;

        MatchmakingGroupMember member = new(session)
        {
            Slot = 1, // The Group Leader Is Always In Slot 1
            IsLeader = true,
            IsReady = false,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 0,
            Rating = rating,
            GameModeAccess = string.Join('|', information.GameModes.Select(mode => "true"))
        };

        MatchmakingGroup group = new() { Members = [member], Information = information };
        if (matchmakingService is MatchmakingService concreteService)
        {
            group.BindMatchmakingService(concreteService);
        }

        if (!matchmakingService.Groups.ContainsKey(session.Account.ID))
        {
            MatchmakingGroup? existingGroup = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);

            if (existingGroup is not null)
            {
                Log.Information(
                    @"Account ID ""{AccountID}"" Is Creating A New Matchmaking Group But Is Already In Group ""{GroupGUID}"". Leaving Old Group.",
                    session.Account.ID, existingGroup.GUID);

                existingGroup.RemoveMember(matchmakingService, session.Account.ID);
            }

            if (!matchmakingService.Groups.TryAdd(session.Account.ID, group))
                throw new InvalidOperationException(
                    $@"Failed To Create Matchmaking Group For Account ID ""{session.Account.ID}""");
        }
        else
        {
            if (!matchmakingService.Groups.TryUpdate(session.Account.ID, group,
                    matchmakingService.Groups[session.Account.ID]))
                throw new InvalidOperationException(
                    $@"Failed To Update Matchmaking Group For Account ID ""{session.Account.ID}""");
        }

        group.MulticastUpdate(session.Account.ID, ChatProtocol.TMMUpdateType.TMM_CREATE_GROUP);
        group.NotifyDistributedStateChanged("create");

        return group;
    }

    public void Invite(ChatSession session, MerrickContext merrick, IChatContext chatContext,
        string receiverAccountName)
    {
        lock (_lock)
        {
            ChatBuffer invite = new();

            invite.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_INVITE);
            invite.WriteString(session.Account.Name); // Invite Issuer Name
            invite.WriteInt32(session.Account.ID); // Invite Issuer ID
            invite.WriteInt8(Convert.ToByte(ChatProtocol.ChatClientStatus
                .CHAT_CLIENT_STATUS_CONNECTED, CultureInfo.InvariantCulture)); // Invite Issuer Status
            invite.WriteInt8(session.Account.GetChatClientFlags()); // Invite Issuer Chat Flags
            invite.WriteString(session.Account.GetNameColourNoPrefixCode()); // Invite Issuer Chat Name Colour
            invite.WriteString(session.Account.GetIconNoPrefixCode()); // Invite Issuer Icon
            invite.WriteString(Information.MapName); // Map Name
            invite.WriteInt8(Convert.ToByte(Information.GameType, CultureInfo.InvariantCulture)); // Game Type
            invite.WriteString(string.Join('|', Information.GameModes)); // Game Modes
            invite.WriteString(string.Join('|', Information.GameRegions)); // Game Regions

            chatContext.ClientChatSessions.TryGetValue(receiverAccountName, out ChatSession? inviteReceiverSession);

            if (inviteReceiverSession is null)
            {
                Log.Warning(
                    @"Group Invite Failed: Receiver ""{ReceiverAccountName}"" Not Found Or Offline (Sender: ""{SenderAccountName}"")",
                    receiverAccountName, session.Account.Name);

                return;
            }

            if (inviteReceiverSession.Account.HasBannedPeer(session.Account.ID))
            {
                ChatBuffer systemNotice = new();
                systemNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                systemNotice.WriteString("System");
                systemNotice.WriteString($"[System] Unable to invite {inviteReceiverSession.Account.Name}. Player {inviteReceiverSession.Account.Name} has you on their banlist.");
                session.Send(systemNotice);
                return;
            }

            if (inviteReceiverSession.Account.HasIgnoredPeer(session.Account.ID))
            {
                ChatBuffer systemNotice = new();
                systemNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                systemNotice.WriteString("System");
                systemNotice.WriteString($"[System] Unable to invite {inviteReceiverSession.Account.Name}. Player {inviteReceiverSession.Account.Name} has you on their ignore list.");
                session.Send(systemNotice);
                return;
            }

            if (session.Account.HasBannedPeer(inviteReceiverSession.Account.ID))
            {
                ChatBuffer systemNotice = new();
                systemNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                systemNotice.WriteString("System");
                systemNotice.WriteString($"[System] Unable to invite {inviteReceiverSession.Account.Name}. This player is on your banlist.");
                session.Send(systemNotice);
                return;
            }

            inviteReceiverSession.Send(invite);

            PendingInvites.TryAdd(receiverAccountName, TimeProvider.System.GetUtcNow());

            ChatBuffer broadcast = new();

            Account? inviteReceiver = merrick.Accounts.Include(account => account.Clan)
                .SingleOrDefault(account =>
                    account.Name.Equals(receiverAccountName, StringComparison.OrdinalIgnoreCase));

            if (inviteReceiver is null)
            {
                Log.Warning(
                    @"Group Invite Broadcast Failed: Receiver ""{ReceiverAccountName}"" Account Not Found In Database",
                    receiverAccountName);
                return;
            }

            broadcast.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_INVITE_BROADCAST);
            broadcast.WriteString(inviteReceiver.GetNameWithClanTag()); // Invite Receiver Name
            broadcast.WriteString(session.Account.GetNameWithClanTag()); // Invite Issuer Name

            foreach (MatchmakingGroupMember member in Members) member.Session.Send(broadcast);
        }
    }

    public void Join(ChatSession session, MerrickContext merrick)
    {
        bool joined = false;
        lock (_lock)
        {
            if (Members.Count >= Information.TeamSize)
            {
                Log.Warning(
                    @"Account ID ""{AccountID}"" Failed To Join Matchmaking Group ""{GroupGUID}"" Because The Group Is Full",
                    session.Account.ID, GUID);

                ChatBuffer groupFull = new();

                groupFull.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_JOIN);
                groupFull.WriteInt8(Convert.ToByte(ChatProtocol.TMMFailedToJoinReason.TMMFTJR_GROUP_FULL,
                    CultureInfo.InvariantCulture));

                session.Send(groupFull);

                return;
            }

            if (QueueStartTime is not null)
            {
                Log.Warning(
                    @"Account ID ""{AccountID}"" Failed To Join Matchmaking Group ""{GroupGUID}"" Because The Group Is Already Queued",
                    session.Account.ID, GUID);

                ChatBuffer alreadyQueued = new();

                alreadyQueued.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_JOIN);
                alreadyQueued.WriteInt8(Convert.ToByte(ChatProtocol.TMMFailedToJoinReason.TMMFTJR_ALREADY_QUEUED,
                    CultureInfo.InvariantCulture));

                session.Send(alreadyQueued);

                return;
            }

            PendingInvites.TryRemove(session.Account.Name, out _);

            AccountStatistics? stats = merrick.AccountStatistics.SingleOrDefault(s =>
                s.AccountID == session.Account.ID && s.Type == AccountStatisticsType.Matchmaking);
            double rating = stats?.SkillRating ?? 1500.0;

            MatchmakingGroupMember newMatchmakingGroupMember = new(session)
            {
                Slot = Convert.ToByte(Members.Count + 1, CultureInfo.InvariantCulture),
                IsLeader = false,
                IsReady = true,
                IsInGame = false,
                IsEligibleForMatchmaking = true,
                LoadingPercent = 0,
                Rating = rating,
                HasGameModeAccess = true,
                GameModeAccess = Leader.GameModeAccess
            };

            if (Members.All(member => member.Account.ID != session.Account.ID))
                Members = [.. Members, newMatchmakingGroupMember];
            else
                throw new InvalidOperationException(
                    $@"Player ""{session.Account.Name}"" Tried To Join A Matchmaking Group They Are Already In");

            MulticastUpdate(session.Account.ID, ChatProtocol.TMMUpdateType.TMM_PLAYER_JOINED_GROUP);
            joined = true;
        }

        if (joined)
        {
            NotifyDistributedStateChanged("member_joined");
        }
    }

    public MatchmakingGroup SendLoadingStatusUpdate(ChatSession session, byte loadingPercent)
    {
        lock (_lock)
        {
            MatchmakingGroupMember groupMember = Members.Single(member => member.Account.ID == session.Account.ID);

            groupMember.LoadingPercent = loadingPercent;

            bool allMembersAreFullyLoaded = Members.All(member => member.LoadingPercent is 100);

            if (allMembersAreFullyLoaded) JoinQueue();

            MulticastUpdate(session.Account.ID, ChatProtocol.TMMUpdateType.TMM_PARTIAL_GROUP_UPDATE);
        }

        NotifyDistributedStateChanged("loading_status");
        return this;
    }

    public MatchmakingGroup SendPlayerReadinessStatusUpdate(ChatSession session, ChatProtocol.TMMGameType matchType)
    {
        lock (_lock)
        {
            Information.GameType = matchType;

            MatchmakingGroupMember groupMember = Members.Single(member => member.Account.ID == session.Account.ID);

            if (!groupMember.IsLeader) return this;

            if (!groupMember.IsReady)
            {
                foreach (MatchmakingGroupMember member in Members.Where(member => !member.IsReady))
                {
                    if (!member.IsLeader)
                        Log.Error(
                            @"[BUG] Non-Leader Group Member ""{Member.Account.Name}"" With ID ""{Member.Account.ID}"" Was Not Ready",
                            member.Account.Name, member.Account.ID);

                    member.IsReady = true;
                }

                MulticastUpdate(session.Account.ID, ChatProtocol.TMMUpdateType.TMM_PARTIAL_GROUP_UPDATE);
            }

            if (Members.All(member => member.IsReady)) JoinQueue();
        }

        NotifyDistributedStateChanged("readiness_status");
        return this;
    }

    /// <summary>
    ///     Attempts to join the matchmaking queue.
    ///     Validates that all members are ready and fully loaded (100%) before joining.
    /// </summary>
    public void JoinQueue()
    {
        bool joinedQueue = false;
        lock (_lock)
        {
            if (QueueStartTime is not null)
            {
                Log.Debug(@"Matchmaking Group GUID ""{GroupGUID}"" is already queued.", GUID);

                return;
            }

            bool allMembersReady = Members.All(member => member.IsReady);

            if (!allMembersReady)
            {
                IEnumerable<string> notReady =
                    Members.Where(m => !m.IsReady).Select(m => $"{m.Account.Name}(NotReady)");
                Log.Debug("[DEBUG] JoinQueue Aborted: Members Not Ready. NotReady: {NotReady}",
                    string.Join(",", notReady));

                ChatBuffer failedParams = new();
                failedParams.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_JOIN);
                failedParams.WriteInt8(Convert.ToByte(ChatProtocol.TMMFailedToJoinReason.TMMFTJR_BUSY,
                    CultureInfo.InvariantCulture));

                foreach (MatchmakingGroupMember member in Members) member.Session.Send(failedParams);

                return;
            }

            MatchmakingGroupMember? suspendedMember = Members.FirstOrDefault(m => m.Account.IsSuspended);
            if (suspendedMember is not null)
            {
                Log.Warning(
                    @"Matchmaking Group GUID ""{GroupGUID}"" failed to join queue: Account ""{AccountName}"" is suspended until {Expiration}",
                    GUID, suspendedMember.Account.Name, suspendedMember.Account.SuspensionExpiration);

                ChatBuffer failedParams = new();
                failedParams.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_FAILED_TO_JOIN);
                failedParams.WriteInt8(Convert.ToByte(ChatProtocol.TMMFailedToJoinReason.TMMFTJR_BANNED,
                    CultureInfo.InvariantCulture));

                ChatBuffer suspensionNotice = new();
                suspensionNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                suspensionNotice.WriteString("System");
                suspensionNotice.WriteString(
                    $"[Moderation] Player '{suspendedMember.Account.Name}' is suspended from Matchmaking until {suspendedMember.Account.SuspensionExpiration!.Value:yyyy-MM-dd HH:mm} UTC. Only Public matches are permitted.");

                foreach (MatchmakingGroupMember member in Members)
                {
                    member.Session.Send(failedParams);
                    member.Session.Send(suspensionNotice);
                }

                return;
            }

            if (Information.GameType is ChatProtocol.TMMGameType.TMM_GAME_TYPE_PUBLIC && Members.Count > 1)
            {
                Log.Warning(
                    $@"Matchmaking Group GUID ""{GUID}"" Tried To Join Public Game Queue With {Members.Count} Members. Public Games Are Solo Only.");

                return;
            }

            QueueStartTime = TimeProvider.System.GetUtcNow();

            Log.Debug("[DEBUG] Group {GUID} Joined Queue. Members: {Count}", GUID, Members.Count);

            ChatBuffer joinQueueBroadcast = new();

            joinQueueBroadcast.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_JOIN_QUEUE);

            foreach (MatchmakingGroupMember member in Members) member.Session.Send(joinQueueBroadcast);

            ChatBuffer queueUpdateBroadcast = new();

            queueUpdateBroadcast.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
            queueUpdateBroadcast.WriteInt8(Convert.ToByte(ChatProtocol.TMMUpdateType.TMM_GROUP_QUEUE_UPDATE,
                CultureInfo.InvariantCulture));
            queueUpdateBroadcast.WriteInt32(83);

            foreach (MatchmakingGroupMember member in Members) member.Session.Send(queueUpdateBroadcast);
            joinedQueue = true;
        }

        if (joinedQueue)
        {
            NotifyDistributedStateChanged("join_queue");
        }
    }

    public void UpdateInformation(MatchmakingGroupInformation newInformation)
    {
        lock (_lock)
        {
            newInformation.ClientVersion = Information.ClientVersion;
            newInformation.GroupType = Information.GroupType;

            Information = newInformation;

            MulticastUpdate(Leader.Account.ID, ChatProtocol.TMMUpdateType.TMM_FULL_GROUP_UPDATE);
        }

        NotifyDistributedStateChanged("update_information");
    }

    public void UpdateGroupType(ChatProtocol.TMMType newType)
    {
        lock (_lock)
        {
            Information.GroupType = newType;

            if (newType == ChatProtocol.TMMType.TMM_TYPE_PVP &&
                Information.GameType == ChatProtocol.TMMGameType.TMM_GAME_TYPE_PUBLIC)
                Information.GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL;

            MulticastUpdate(Leader.Account.ID, ChatProtocol.TMMUpdateType.TMM_FULL_GROUP_UPDATE);
        }

        NotifyDistributedStateChanged("update_group_type");
    }

    public void MulticastUpdate(int emitterAccountID, ChatProtocol.TMMUpdateType updateType)
    {
        lock (_lock)
        {
            ChatBuffer update = new();

            update.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_UPDATE);

            update.WriteInt8(Convert.ToByte(updateType, CultureInfo.InvariantCulture));
            update.WriteInt32(emitterAccountID);
            update.WriteInt8(Convert.ToByte(Members.Count, CultureInfo.InvariantCulture));
            update.WriteInt16((short)AverageRating);
            update.WriteInt32(Leader.Account.ID);
            ChatProtocol.ArrangedMatchType arrangedMatchType = Information.GameType switch
            {
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_PUBLIC => ChatProtocol.ArrangedMatchType.AM_PUBLIC,

                ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS when Information.GroupType ==
                                                                    ChatProtocol.TMMType.TMM_TYPE_PVP
                    => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_MIDWARS,
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_RIFTWARS when Information.GroupType ==
                                                                     ChatProtocol.TMMType.TMM_TYPE_PVP
                    => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_RIFTWARS,

                ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL when Information.GroupType ==
                                                                   ChatProtocol.TMMType.TMM_TYPE_PVP
                    => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_CAMPAIGN,
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL when Information.GroupType ==
                                                                   ChatProtocol.TMMType.TMM_TYPE_PVP
                    => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_CAMPAIGN,
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_NORMAL when Information.GroupType ==
                                                                            ChatProtocol.TMMType.TMM_TYPE_PVP
                    => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_CAMPAIGN,
                ChatProtocol.TMMGameType.TMM_GAME_TYPE_CAMPAIGN_CASUAL when Information.GroupType ==
                                                                            ChatProtocol.TMMType.TMM_TYPE_PVP
                    => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_CAMPAIGN,

                _ => ChatProtocol.ArrangedMatchType.AM_MATCHMAKING
            };

            update.WriteInt8(Convert.ToByte(arrangedMatchType, CultureInfo.InvariantCulture));
            update.WriteInt8(Convert.ToByte(Information.GameType, CultureInfo.InvariantCulture));
            update.WriteString(Information.MapName);
            update.WriteString(string.Join('|', Information.GameModes));
            update.WriteString(string.Join('|', Information.GameRegions));

            Log.Information(
                "Sending Group Update: Modes={Modes}, Regions={Regions}, Type={UpdateType}, GroupType={GroupType}, GameType={GameType}, AM={AM}",
                string.Join('|', Information.GameModes),
                string.Join('|', Information.GameRegions),
                updateType,
                Information.GroupType,
                Information.GameType,
                arrangedMatchType);

            update.WriteBool(Information.Ranked);
            update.WriteInt8(Information.MatchFidelity);
            update.WriteInt8(Information.BotDifficulty);
            update.WriteBool(Information.RandomizeBots);
            update.WriteString(string.Empty);

            update.WriteString(string.Empty);
            update.WriteInt8(Information.TeamSize);
            update.WriteInt8(Convert.ToByte(Information.GroupType, CultureInfo.InvariantCulture));

            bool fullGroupUpdate = updateType switch
            {
                ChatProtocol.TMMUpdateType.TMM_CREATE_GROUP => true,
                ChatProtocol.TMMUpdateType.TMM_FULL_GROUP_UPDATE => true,
                ChatProtocol.TMMUpdateType.TMM_PLAYER_JOINED_GROUP => true,
                ChatProtocol.TMMUpdateType.TMM_PLAYER_LEFT_GROUP => true,
                ChatProtocol.TMMUpdateType.TMM_PLAYER_KICKED_FROM_GROUP => true,
                _ => false
            };

            foreach (MatchmakingGroupMember member in Members.ToList())
            {
                if (fullGroupUpdate)
                {
                    update.WriteInt32(member.Account.ID);
                    update.WriteString(member.Account.Name);
                    update.WriteInt8(member.Slot);

                    update.WriteInt32(20);
                    update.WriteInt32(15);
                    update.WriteInt32(20);
                    update.WriteInt32(15);
                    update.WriteBool(true);
                    update.WriteInt16((short)member.Rating);
                }

                update.WriteInt8(member.LoadingPercent);
                update.WriteBool(member.IsReady);
                update.WriteBool(member.IsInGame);

                if (fullGroupUpdate)
                {
                    update.WriteBool(member.IsEligibleForMatchmaking);
                    update.WriteString(member.Account.GetNameColourNoPrefixCode());
                    update.WriteString(member.Account.GetIconNoPrefixCode());
                    update.WriteString(member.Country);
                    update.WriteBool(member.HasGameModeAccess);
                    update.WriteString(member.GameModeAccess);
                }
            }

            if (fullGroupUpdate)
                foreach (MatchmakingGroupMember member in Members)
                    update.WriteBool(false);

            foreach (MatchmakingGroupMember member in Members.ToList()) member.Session.Send(update);
        }
    }

    public void RemoveMember(IMatchmakingService matchmakingService, int accountID, bool kick = false)
    {
        bool removed = false;
        bool disbanded = false;
        lock (_lock)
        {
            MatchmakingGroupMember? memberToRemove = Members.SingleOrDefault(member => member.Account.ID == accountID);

            if (memberToRemove is null) return;

            bool memberToRemoveIsLeader = memberToRemove.IsLeader;

            ChatProtocol.TMMUpdateType updateType = kick
                ? ChatProtocol.TMMUpdateType.TMM_PLAYER_KICKED_FROM_GROUP
                : ChatProtocol.TMMUpdateType.TMM_PLAYER_LEFT_GROUP;

            MulticastUpdate(accountID, updateType);

            Members = [.. Members.Where(member => member != memberToRemove)];
            removed = true;

            if (Members.Count == 0)
            {
                DisbandGroup(matchmakingService, accountID);
                disbanded = true;
            }
            else
            {
                ReassignSlots();

                if (memberToRemoveIsLeader)
                {
                    if (matchmakingService.Groups.TryRemove(accountID, out MatchmakingGroup? existingGroup))
                    {
                        if (existingGroup != this)
                            Log.Error(
                                @"[BUG] Groups Dictionary State Mismatch During Leader Migration. Removed Group GUID ""{RemovedGUID}"" But Expected ""{CurrentGUID}""",
                                existingGroup.GUID, GUID);

                        if (!matchmakingService.Groups.TryAdd(Leader.Account.ID, this))
                            Log.Error(@"[BUG] Failed To Re-Add Matchmaking Group For New Leader ""{LeaderID}""",
                                Leader.Account.ID);
                        else
                            Log.Information(@"Migrated Matchmaking Group Key From ""{OldLeaderID}"" To ""{NewLeaderID}""",
                                accountID, Leader.Account.ID);
                    }
                    else
                    {
                        Log.Error(@"[BUG] Failed To Remove Old Leader Key ""{OldLeaderID}"" During Migration", accountID);
                    }
                }

                foreach (MatchmakingGroupMember member in Members) member.IsReady = !member.IsLeader;

                if (QueueStartTime is not null) LeaveQueue(accountID);

                MulticastUpdate(accountID, ChatProtocol.TMMUpdateType.TMM_FULL_GROUP_UPDATE);
            }
        }

        MatchmakingService? ownerService = matchmakingService as MatchmakingService ?? _ownerMatchmakingService;
        if (disbanded)
        {
            ownerService?.NotifyGroupStateRemoved(GUID, "disband");
        }
        else if (removed)
        {
            ownerService?.NotifyGroupStateChanged(this, kick ? "member_kicked" : "member_removed");
        }
    }

    public void LeaveQueue(int emitterAccountID)
    {
        lock (_lock)
        {
            QueueStartTime = null;

            foreach (MatchmakingGroupMember member in Members)
            {
                member.IsReady = !member.IsLeader;
                member.LoadingPercent = 0;
            }

            ChatBuffer leaveQueueBroadcast = new();

            leaveQueueBroadcast.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_LEAVE_QUEUE);

            foreach (MatchmakingGroupMember member in Members) member.Session.Send(leaveQueueBroadcast);
        }

        NotifyDistributedStateChanged("leave_queue");
    }

    public void KickMember(IMatchmakingService matchmakingService, int accountID)
    {
        RemoveMember(matchmakingService, accountID, true);
    }

    public void Kick(IMatchmakingService matchmakingService, ChatSession session, byte slot)
    {
        lock (_lock)
        {
            if (Leader.Account.ID != session.Account.ID) return;

            MatchmakingGroupMember? target = Members.SingleOrDefault(m => m.Slot == slot);
            if (target != null) KickMember(matchmakingService, target.Account.ID);
        }
    }

    private void ReassignSlots()
    {
        if (Members.Count == 0)
        {
            Log.Error(@"[BUG] Attempted To Reassign Slots In Empty Matchmaking Group GUID ""{Group.GUID}""", GUID);

            return;
        }

        List<MatchmakingGroupMember> orderedMembers = [.. Members.OrderBy(member => member.Slot)];
        for (int index = 0; index < orderedMembers.Count; index++)
            orderedMembers[index].Slot = Convert.ToByte(index + 1, CultureInfo.InvariantCulture);
        orderedMembers[0].IsLeader = true;
        Members = orderedMembers;
    }

    private void DisbandGroup(IMatchmakingService matchmakingService, int accountID)
    {
        if (!matchmakingService.Groups.TryRemove(accountID, out MatchmakingGroup? group))
            Log.Error(
                @"Failed To Disband Matchmaking Group GUID ""{Group.GUID}"" For Account ID ""{Member.Account.ID}""",
                group?.GUID ?? Guid.Empty, accountID);
    }
}
