// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MERRICK.DatabaseContext.Extensions;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Communication;

public class ChatChannel
{
    private static int _nextChannelId;

    private static readonly HashSet<string> SystemChannels =
    [
        "General",
        "Lobby",
        "Help",
        "Support",
        "Development",
        "Staff",
        "KONGOR"
    ];

    // Store Authorized User IDs for Auth-Required Channels
    private readonly ConcurrentDictionary<int, byte> _authorizedAccountIDs = [];

    private readonly ConcurrentDictionary<int, byte> _bannedAccountIDs = [];

    private ImmutableDictionary<string, ChatChannelMember> _members =
        [];

    /// <summary>
    ///     Hidden Constructor Which Enforces <see cref="GetOrCreate" /> As The Primary Mechanism For Creating Chat Channels
    /// </summary>
    private ChatChannel()
    {
    }

    public int ID { get; } = Interlocked.Increment(ref _nextChannelId);

    public required string Name { get; set; }

    public string Topic
    {
        get => field ?? $"Welcome To The {Name} Channel !";
        set;
    }

    public required ChatProtocol.ChatChannelType Flags { get; set; }

    public IReadOnlyDictionary<string, ChatChannelMember> Members => _members;

    public ConcurrentDictionary<int, ChatProtocol.AdminLevel> CustomAdmins { get; } = [];

    /// <summary>
    ///     Channel password for access control.
    ///     This value is NULL if no password is set.
    /// </summary>
    public string? Password { get; set; }

    public bool IsFull => Members.Count >= ChatProtocol.MAX_USERS_PER_CHANNEL;

    public bool IsPermanent => Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_PERMANENT);

    public void TryAddMember(string accountName, ChatChannelMember member)
    {
        ImmutableInterlocked.TryAdd(ref _members, accountName, member);
    }

    public bool HasPassword()
    {
        return !string.IsNullOrEmpty(Password);
    }

    public static ChatChannel GetOrCreate(IChatContext chatContext, ChatSession? session, string channelName)
    {
        // Enforce Clan Channel Flag if the name matches the Clan Channel pattern ("Clan {Name}")
        // This prevents non-clan members from creating "spoof" permanent channels with Clan names
        Account? sessionAccount = session?.Account;
        bool isClanChannel =
            (sessionAccount?.Clan is not null && channelName == sessionAccount.Clan.GetChatChannelName()) ||
            channelName.StartsWith("Clan ", StringComparison.OrdinalIgnoreCase);

        ChatProtocol.ChatChannelType chatChannelType = channelName switch
        {
            _ when isClanChannel => ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_RESERVED |
                                    ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_CLAN,
            _ when SystemChannels.Contains(channelName) => ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_RESERVED |
                                                           ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_PERMANENT,
            _ => ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_GENERAL_USE
        };

        ChatChannel channel = chatContext.ChatChannels.GetOrAdd(channelName,
            name => new ChatChannel { Name = name, Flags = chatChannelType });

        chatContext.ChatChannelsByID.TryAdd(channel.ID, channel);

        return channel;
    }

    public static ChatChannel? Get(IChatContext chatContext, ChatSession session, OneOf<string, int> channelIdentifier)
    {
        ChatChannel? channel = channelIdentifier.Match
        (
            channelName => chatContext.ChatChannels.TryGetValue(channelName, out ChatChannel? foundByName) &&
                           foundByName.Members.ContainsKey(session.Account.Name)
                ? foundByName
                : null,
            channelID => chatContext.ChatChannelsByID.TryGetValue(channelID, out ChatChannel? foundById) &&
                         foundById.Members.ContainsKey(session.Account.Name)
                ? foundById
                : null
        );

        return channel;
    }

    public void Join(ChatSession session, string? providedPassword = null)
    {
        // Staff Accounts Are Exempt From Channel Limit Restrictions, For Moderation And Administration Purposes
        if (session.Account.Type is not AccountType.Staff)
        {
            // Log A Bug-Type Error If The Client Has Exceeded The Maximum Number Of Channels
            if (session.CurrentChannels.Count > ChatProtocol.MAX_CHANNELS_PER_CLIENT)
                Log.Error(
                    @"[BUG] Account ""{AccountName}"" Has Exceeded The Maximum Number Of Channels ({MaxChannels})",
                    session.Account.Name, ChatProtocol.MAX_CHANNELS_PER_CLIENT);

            // Reject Join Request If Client Has Reached Maximum Number Of Channels
            if (session.CurrentChannels.Count == ChatProtocol.MAX_CHANNELS_PER_CLIENT)
            {
                ChatBuffer error = new();

                error.WriteCommand(ChatProtocol.Command.CHAT_CMD_MAX_CHANNELS);

                session.Send(error);

                return;
            }
        }

        // If client is already in the channel, resend channel details so client UI re-syncs
        if (Members.ContainsKey(session.Account.Name))
        {
            Log.Information(
                @"[DEBUG] Client Re-Joined Channel ""{ChannelName}"" (ID: {ChannelID}) - Resending ChangedChannel (0x0004)",
                Name, ID);
            SendChangedChannelResponse(session);
            if (!session.CurrentChannels.Contains(ID))
                session.CurrentChannels.Add(ID);
            return;
        }

        // Reject Join Request If The Channel Is A Clan Channel And The Client Is Not In The Clan
        if (Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_CLAN))
            if (session.Account.Clan is null || Name != session.Account.Clan.GetChatChannelName())
            {
                // Legacy Behavior: No Error Message Sent To Client (Silent Rejection)
                ChatBuffer accessDenied = new();
                accessDenied.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                accessDenied.WriteString("Channel Service");
                accessDenied.WriteString("You do not have the correct permission to access this channel.");

                session.Send(accessDenied);

                return;
            }

        if (_bannedAccountIDs.ContainsKey(session.Account.ID))
        {
            ChatBuffer banned = new();
            // Note: This matches the "Victim Notification" OpCode (0x0034), but the payload here is Channel ID.
            // The user said CHAT_CMD_CHANNEL_IS_BANNED expects [String ChannelName].
            // If I change it here, I fix it for Join rejection too.
            banned.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_IS_BANNED);
            banned.WriteString(Name); // Channel Name

            session.Send(banned);

            return;
        }

        // Check. Auth Required
        if (Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_AUTH_REQUIRED))
            // If not in auth list and not staff
            if (!_authorizedAccountIDs.ContainsKey(session.Account.ID) && session.Account.Type != AccountType.Staff)
                return;

        // Reject Join Request If The Channel Has The CHAT_CHANNEL_FLAG_UNJOINABLE Flag
        if (Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_UNJOINABLE))
            if (session.Account.Type != AccountType.Staff)
            {
                ChatBuffer accessDenied = new();
                accessDenied.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                accessDenied.WriteString("Channel Service");
                accessDenied.WriteString("This channel is unjoinable.");

                session.Send(accessDenied);

                return;
            }

        ChatChannelMember newMember = new(session, this);

        // Reject Join Request As Non-Administrator If Channel Is Full
        if (IsFull && !newMember.IsAdministrator)
        {
            ChatBuffer error = new();

            error.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            error.WriteString("Channel Service");
            error.WriteString("This channel is full.");

            session.Send(error);

            return;
        }

        // Note(protocol): Response buffer overflow protection (16KB ceiling) is enforced by the outbound packet framing layer.

        // Check For Password Protection On The Channel
        // Staff Accounts And Channel Administrators Bypass Password Checks
        if (HasPassword())
            if (!newMember.IsAdministrator)
            {
                // If No Password Was Provided, Send Password Prompt To Client
                if (string.IsNullOrEmpty(providedPassword))
                {
                    ChatBuffer prompt = new();

                    prompt.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL_PASSWORD);
                    prompt.WriteString(Name); // Channel Name Only (Does Not Reveal Password)

                    session.Send(prompt);

                    return;
                }

                // If Wrong Password Was Provided, Reject Join Request
                if (Password is not null && !Password.Equals(providedPassword, StringComparison.Ordinal))
                {
                    Log.Warning("Account {AccountName} provided invalid password when joining channel {ChannelName}",
                        session.Account.Name, Name);
                    // Note(protocol): Legacy protocol silent rejection terminates handshake when invalid channel password is provided.
                    return;
                }
            }

        if (!ImmutableInterlocked.TryAdd(ref _members, session.Account.Name, newMember))
            Log.Error(@"[BUG] Failed To Add Account ""{AccountName}"" To Channel ""{ChannelName}""",
                session.Account.Name, Name);

        // Grant Admin Rights To The First Joiner Of A Custom Channel
        if (Members.Count == 1 &&
            !Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_CLAN) &&
            !Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_PERMANENT))
            CustomAdmins.TryAdd(session.Account.ID, ChatProtocol.AdminLevel.CHAT_CLIENT_ADMIN_OFFICER);

        Log.Information(
            @"[DEBUG] Client Joined Channel ""{ChannelName}"" (ID: {ChannelID}) - Sending ChangedChannel (0x0004)",
            Name, ID);

        // Announce To The Requesting Client That They Have Joined The Channel
        SendChangedChannelResponse(session);

        // Announce To The Existing Channel Members That A New Client Has Joined The Channel
        BroadcastJoin(newMember);

        // Track This Channel In The Client's Current Channels List
        session.CurrentChannels.Add(ID);

        _ = session.DistributedChatState?.JoinChannelAsync(
            Name,
            session.Account.ID,
            session.Account.Name,
            (byte)newMember.AdministratorLevel);
    }

    public void SendChangedChannelResponse(ChatSession session)
    {
        ChatBuffer response = new();

        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);
        response.WriteString(Name); // Channel Name
        response.WriteInt32(ID); // Channel ID
        response.WriteInt8(Convert.ToByte(Flags)); // Channel Flags
        response.WriteString(Topic); // Channel Topic

        List<ChatChannelMember> administrators = [.. Members.Values.Where(member => member.IsAdministrator)];

        response.WriteInt32(administrators.Count); // Count Of Channel Administrators

        foreach (ChatChannelMember administrator in administrators)
        {
            response.WriteInt32(administrator.Account.ID); // Administrator Account ID
            response.WriteInt8(Convert.ToByte(administrator.AdministratorLevel)); // Channel Administrator Level
        }

        response.WriteInt32(Members.Count); // Count Of Channel Members

        foreach (ChatChannelMember member in Members.Values)
        {
            response.WriteString(member.Account.GetNameWithClanTag()); // Member Account Name
            response.WriteInt32(member.Account.ID); // Member Account ID
            response.WriteInt8((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
            response.WriteInt8(member.Account.GetChatClientFlags()); // Client's Flags (Chat Client Type)
            response.WriteString(member.Account.GetChatSymbolNoPrefixCode()); // Chat Symbol
            response.WriteString(member.Account.GetNameColourNoPrefixCode()); // Name Color
            response.WriteString(member.Account.GetIconNoPrefixCode()); // Account Icon
            response.WriteInt32(member.Account.AscensionLevel); // Ascension Level
        }

        session.Send(response);
    }

    private void BroadcastJoin(ChatChannelMember? newMember)
    {
        if ((object?)newMember?.Account is null)
        {
            Log.Error("[BUG] BroadcastJoin Called With Null Member Or Account");
            return;
        }

        List<ChatChannelMember> existingMembers =
            [.. Members.Values.Where(member => member.Account.ID != newMember.Account.ID)];

        ChatBuffer broadcast = new();

        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_CHANNEL);
        broadcast.WriteInt32(ID); // Channel ID
        broadcast.WriteString(newMember.Account.GetNameWithClanTag()); // Member Account Name
        broadcast.WriteInt32(newMember.Account.ID); // Member Account ID
        broadcast.WriteInt8((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
        broadcast.WriteInt8(newMember.Account.GetChatClientFlags()); // Client's Flags (Chat Client Type)
        broadcast.WriteString(newMember.Account.GetChatSymbolNoPrefixCode()); // Chat Symbol
        broadcast.WriteString(newMember.Account.GetNameColourNoPrefixCode()); // Name Color
        broadcast.WriteString(newMember.Account.GetIconNoPrefixCode()); // Account Icon
        broadcast.WriteInt32(newMember.Account.AscensionLevel); // Ascension Level

        // Announce To The Existing Channel Members That A New Client Has Joined The Channel
        foreach (ChatChannelMember existingMember in existingMembers) existingMember.Session.Send(broadcast);
    }

    public void Leave(IChatContext chatContext, ChatSession session)
    {
        if (!ImmutableInterlocked.TryRemove(ref _members, session.Account.Name, out ChatChannelMember? member))
            Log.Error(@"[BUG] Failed To Remove Account ""{AccountName}"" From Channel ""{ChannelName}""",
                session.Account.Name, Name);

        if (member is not null)
        {
            // Remove This Channel From The Client's Current Channels List
            session.CurrentChannels.Remove(ID);

            _ = session.DistributedChatState?.LeaveChannelAsync(Name, session.Account.Name);

            ChatBuffer broadcast = new();

            broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL);
            broadcast.WriteInt32(member.Account.ID); // Member Account ID
            broadcast.WriteInt32(ID); // Channel ID

            // Notify The Leaver
            member.Session.Send(broadcast);

            switch (_members.IsEmpty)
            {
                // If There Are No Remaining Members And The Channel Is Not Permanent, Dispose Of It
                case true when !IsPermanent:
                {
                    if (!chatContext.ChatChannels.TryRemove(Name, out ChatChannel? channel))
                    {
                        Log.Debug(@"Channel ""{ChannelName}"" was already removed from Global Channel List", Name);
                    }
                    else
                    {
                        chatContext.ChatChannelsByID.TryRemove(channel.ID, out _);
                        // Clear state
                        channel.CustomAdmins.Clear();
                        channel._bannedAccountIDs.Clear();
                        channel._authorizedAccountIDs.Clear();
                    }

                    break;
                }
                case true when IsPermanent:
                {
                    CustomAdmins.Clear();
                    break;
                }
                case false:
                {
                    // Announce To The Remaining Channel Members That A Client Has Left The Channel
                    foreach (ChatChannelMember channelMember in _members.Values) channelMember.Session.Send(broadcast);

                    break;
                }
            }
        }
        else
        {
            Log.Debug(
                @"Chat Channel Member Instance For Account ""{AccountName}"" In Channel ""{ChannelName}"" Is NULL",
                ((Account?)session.Account)?.Name ?? "Unknown", Name);
        }
    }

    public void Kick(IChatContext chatContext, ChatSession requesterSession, int targetAccountID)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession.Account.ID);
        ChatChannelMember? target = Members.Values.FirstOrDefault(member => member.Account.ID == targetAccountID);

        if (requester is null || target is null) return;

        if (requester.HasHigherAdministratorLevelThan(target))
        {
            ChatBuffer broadcast = new();

            broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK);
            broadcast.WriteInt32(ID); // Channel ID
            broadcast.WriteInt32(requesterSession.Account.ID); // Kicker Account ID
            broadcast.WriteInt32(targetAccountID); // Kicked Account ID

            // Announce To The Channel Members That A Client Will Be Kicked From The Channel
            foreach (ChatChannelMember member in Members.Values) member.Session.Send(broadcast);

            ChatSession? targetSession =
                chatContext.ClientChatSessions.Values.FirstOrDefault(session => session.Account.ID == targetAccountID);

            if (targetSession is not null)
                // Remove The Target Member From The Channel
                Leave(chatContext, targetSession);
            else
                ImmutableInterlocked.TryRemove(ref _members, target.Account.Name, out _);
        }
    }

    public bool IsSilenced(ChatSession session)
    {
        ChatChannelMember? member = Members.Values
            .SingleOrDefault(channelMember => channelMember.Account.ID == session.Account.ID);

        return member?.IsSilenced() ?? false;
    }

    public void Silence(ChatSession requesterSession, int targetAccountID, int durationMilliseconds)
    {
        ChatChannelMember requester = Members.Values.Single(member => member.Account.ID == requesterSession.Account.ID);
        ChatChannelMember target = Members.Values.Single(member => member.Account.ID == targetAccountID);

        // Requester Must Have Higher Administrator Level Than Target (Strict Inequality)
        bool outranks = requester.HasHigherAdministratorLevelThan(target);
        Log.Information(
            "[DEBUG] Silence Check: Requester={RName} ({RLevel}) Target={TName} ({TLevel}) Outranks={Outranks}",
            requester.Account.Name, requester.AdministratorLevel, target.Account.Name, target.AdministratorLevel,
            outranks);

        if (!outranks)
        {
            Log.Information("[DEBUG] Silence Failed: Does not outrank");
            return;
        }

        if (durationMilliseconds == 0)
        {
            // Unsilence
            target.SilencedUntil = null;

            ChatBuffer broadcast = new();
            broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_LIFTED);
            broadcast.WriteString(Name); // Channel Name
            broadcast.WriteString(requester.Account.GetNameWithClanTag()); // Requester Name
            broadcast.WriteString(target.Account.GetNameWithClanTag()); // Target Name

            BroadcastMessage(broadcast);
        }
        else
        {
            // Silence
            target.SilencedUntil = TimeProvider.System.GetUtcNow().AddMilliseconds(durationMilliseconds);

            ChatBuffer broadcast = new();
            broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_PLACED);
            broadcast.WriteString(Name); // Channel Name
            broadcast.WriteString(requester.Account.GetNameWithClanTag()); // Requester Name
            broadcast.WriteString(target.Account.GetNameWithClanTag()); // Target Name
            broadcast.WriteInt32(durationMilliseconds); // Duration In Milliseconds

            BroadcastMessage(broadcast);
        }
    }

    public void BroadcastMessage(ChatBuffer message, int? excludeAccountID = null, int? senderAccountId = null)
    {
        ImmutableDictionary<string, ChatChannelMember> membersSnapshot = _members;

        foreach (ChatChannelMember recipient in membersSnapshot.Values)
        {
            if (excludeAccountID.HasValue && recipient.Account.ID == excludeAccountID.Value) continue;
            if (senderAccountId.HasValue && recipient.Account.HasIgnoredPeer(senderAccountId.Value)) continue;

            recipient.Session.SendAsync(message);
        }
    }

    /// <summary>
    ///     Delivers a channel message originating from a remote cluster node to local members.
    /// </summary>
    public void DeliverRemoteClusterMessage(int senderAccountId, string senderName, string message)
    {
        ChatBuffer broadcast = new();
        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        broadcast.WriteInt32(senderAccountId);
        broadcast.WriteInt32(ID);
        broadcast.WriteString(message);

        BroadcastMessage(broadcast, excludeAccountID: senderAccountId, senderAccountId: senderAccountId);
    }

    public void SetPassword(ChatSession session, string password)
    {
        if (!Members.TryGetValue(session.Account.Name, out ChatChannelMember? member)) return;

        if (!member.HasElevatedPrivileges()) return;

        Password = string.IsNullOrEmpty(password) ? null : password;

        ChatBuffer broadcast = new();

        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SET_PASSWORD);
        broadcast.WriteInt32(ID); // Channel ID
        broadcast.WriteString(session.Account.GetNameWithClanTag()); // Password Setter's Name

        BroadcastMessage(broadcast);
    }

    public void Ban(IChatContext chatContext, ChatSession? requesterSession, int targetAccountID, string targetName)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession?.Account.ID);

        // Check Permissions (Must have elevated privileges to ban)
        bool hasPrivileges = requester?.HasElevatedPrivileges() ?? false;

        if (!hasPrivileges) return;

        _bannedAccountIDs.TryAdd(targetAccountID, 0);

        // If target is currently in the channel, kick them
        if (requesterSession != null && Members.Values.Any(m => m.Account.ID == targetAccountID))
            Kick(chatContext, requesterSession, targetAccountID);

        int kickerId = requesterSession?.Account.ID ?? 0;

        // Broadcast CHAT_CMD_CHANNEL_BAN (0x0032) to Channel
        // Payload: [Int ChannelID] [Int BannerID] [String TargetName]
        ChatBuffer broadcast = new();
        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);
        broadcast.WriteInt32(ID); // Channel ID
        broadcast.WriteInt32(kickerId); // Banner ID
        broadcast.WriteString(targetName); // Target Name
        BroadcastMessage(broadcast);

        // Notify Victim via CHAT_CMD_CHANNEL_IS_BANNED (0x0034)
        // Payload: [String ChannelName]
        ChatSession? targetSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(s => s.Account.ID == targetAccountID);

        if (targetSession != null)
        {
            ChatBuffer victimMsg = new();
            victimMsg.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_IS_BANNED);
            victimMsg.WriteString(Name); // Channel Name
            targetSession.Send(victimMsg);
        }
    }

    public void Unban(ChatSession? requesterSession, int targetAccountID, string targetName)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession?.Account.ID);
        bool hasPrivileges = requester?.HasElevatedPrivileges() ?? false;

        if (!hasPrivileges) return;

        if (_bannedAccountIDs.TryRemove(targetAccountID, out _))
        {
            int unbannerId = requesterSession?.Account.ID ?? 0;
            // Broadcast CHAT_CMD_CHANNEL_UNBAN (0x0033)
            // Payload: [Int ChannelID] [Int UnbannerID] [String TargetName]
            ChatBuffer broadcast = new();
            broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_UNBAN);
            broadcast.WriteInt32(ID); // Channel ID
            broadcast.WriteInt32(unbannerId); // Unbanner ID
            broadcast.WriteString(targetName); // Target Name

            BroadcastMessage(broadcast);
        }
    }

    public void AddAuthUser(ChatSession requesterSession, int targetAccountID, string targetName)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession.Account.ID);
        bool hasPrivileges = requester?.HasElevatedPrivileges() ?? false;

        // Need Admin/Leader
        if (!hasPrivileges) return;

        _authorizedAccountIDs.TryAdd(targetAccountID, 0);
    }

    public void RemoveAuthUser(ChatSession requesterSession, int targetAccountID, string targetName)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession.Account.ID);
        bool hasPrivileges = requester?.HasElevatedPrivileges() ?? false;

        if (!hasPrivileges) return;

        _authorizedAccountIDs.TryRemove(targetAccountID, out _);
    }

    // Helper to get auth list?
    public List<int> GetAuthorizedUsers()
    {
        return [.. _authorizedAccountIDs.Keys];
    }

    public void Promote(ChatSession requesterSession, int targetAccountID)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession.Account.ID);
        bool hasPrivileges = requester?.HasElevatedPrivileges() ?? false;

        if (!hasPrivileges) return;

        CustomAdmins[targetAccountID] = ChatProtocol.AdminLevel.CHAT_CLIENT_ADMIN_OFFICER;

        ChatBuffer broadcast = new();
        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_PROMOTE);
        broadcast.WriteInt32(ID); // Channel ID
        broadcast.WriteInt32(targetAccountID); // Target ID
        broadcast.WriteInt8((byte)ChatProtocol.AdminLevel.CHAT_CLIENT_ADMIN_OFFICER); // New Rank

        BroadcastMessage(broadcast);
    }

    public void Demote(ChatSession requesterSession, int targetAccountID)
    {
        ChatChannelMember? requester =
            Members.Values.FirstOrDefault(member => member.Account.ID == requesterSession.Account.ID);
        bool hasPrivileges = requester?.HasElevatedPrivileges() ?? false;

        if (!hasPrivileges) return;

        if (CustomAdmins.TryRemove(targetAccountID, out _))
        {
            ChatBuffer broadcast = new();
            broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_DEMOTE);
            broadcast.WriteInt32(ID); // Channel ID
            broadcast.WriteInt32(targetAccountID); // Target ID

            BroadcastMessage(broadcast);
        }
    }

    public void Roll(ChatSession requesterSession, string parameters)
    {
        int max = 100;
        if (int.TryParse(parameters, out int parsedMax) && parsedMax > 0) max = parsedMax;

        int roll = Random.Shared.Next(1, max + 1);

        string resultText = $"rolled {roll} (1-{max})";

        ChatBuffer broadcast = new();
        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHAT_ROLL);
        broadcast.WriteInt32(requesterSession.Account.ID);
        broadcast.WriteInt32(ID);
        broadcast.WriteString(resultText);

        BroadcastMessage(broadcast);
    }
}
