// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using MERRICK.DatabaseContext.Extensions;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles /whois / User Info requests (Command 0x002A - CHAT_CMD_USER_INFO).
///     Wire format verified against k2_x64.dll (0x180708ad0, 0x1806c28e0, 0x1806c2670, 0x1806c19a0, 0x1806c2070):
///     - 0x002B (NO_EXIST): uint16 + string(requestedName)
///     - 0x002C (OFFLINE):  uint16 + string(nameWithClanTag) + string(lastSeenUtc)
///     - 0x002D (ONLINE):   uint16 + string(nameWithClanTag) + int32(channelCount) + string[channelCount]
///     - 0x002E (IN_GAME):  uint16 + string(nameWithClanTag) + string(matchName) + string(gameTime)
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_USER_INFO)]
internal sealed class GetUserInfo(
    IChatContext chatContext,
    MerrickContext? merrick = null,
    IDatabase? distributedCacheStore = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        GetUserInfoData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        string targetName = MERRICK.DatabaseContext.Extensions.AccountExtensions
            .SeparateClanTagFromAccountName(requestData.TargetName.Trim())
            .AccountName
            .Trim();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            SendNoExist(session, requestData.TargetName);
            return;
        }

        ChatSession? targetSession = chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
            (object?)s.Account is not null &&
            string.Equals(s.Account.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (targetSession is not null && targetSession.ClientMetadata is not null)
        {
            bool canSeeInvisible = session.Account.ID == targetSession.Account.ID || session.Account.IsStaff;

            if (!canSeeInvisible &&
                targetSession.ClientMetadata.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
            {
                Log.Information(
                    "[GetUserInfo] Masking CHAT_MODE_INVISIBLE target '{Target}' as 0x002C OFFLINE for requester '{Requester}'",
                    targetSession.Account.Name, session.Account.Name);
                SendOffline(session, targetSession.Account);
                return;
            }

            ChatProtocol.ChatClientStatus state = targetSession.ClientMetadata.LastKnownClientState;
            if (state is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME)
            {
                bool isInGame = state is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
                string matchName = string.Empty;
                string formattedTime = string.Empty;

                if (isInGame)
                {
                    MatchStartData? matchData = null;
                    if (distributedCacheStore is not null && targetSession.ClientMetadata.MatchID is > 0)
                    {
                        matchData = await distributedCacheStore.GetMatchStartData(targetSession.ClientMetadata.MatchID.Value);
                    }

                    MatchServerChatSessionMetadata? serverMeta = chatContext.MatchServerChatSessions.Values
                        .Select(s => s.ServerMetadata)
                        .FirstOrDefault(m => m is not null &&
                                             ((matchData is not null && m.ServerID == matchData.ServerID) ||
                                              (targetSession.ClientMetadata.MatchID is > 0 && m.MatchID == targetSession.ClientMetadata.MatchID)));

                    matchName = !string.IsNullOrWhiteSpace(serverMeta?.GameName)
                        ? serverMeta.GameName
                        : !string.IsNullOrWhiteSpace(targetSession.ClientMetadata.MatchName)
                            ? targetSession.ClientMetadata.MatchName
                            : !string.IsNullOrWhiteSpace(matchData?.MatchName)
                                ? matchData.MatchName
                                : "HoN Match";

                    formattedTime = RequestGameInfo.FormatElapsedTime(
                        serverMeta?.CurrentGameTime ?? 0,
                        matchData?.TimestampStarted);
                }

                ChatBuffer inGame = new();
                inGame.WriteCommand(ChatProtocol.Command.CHAT_CMD_USER_INFO_IN_GAME);
                inGame.WriteString(targetSession.Account.GetNameWithClanTag());
                inGame.WriteString(matchName);
                inGame.WriteString(formattedTime);
                session.Send(inGame);

                Log.Information(
                    "[GetUserInfo] Sent 0x002E IN_GAME info for '{Target}' (Match='{MatchName}', Time='{Time}') -> '{Requester}'",
                    targetSession.Account.Name, matchName, formattedTime, session.Account.Name);
                return;
            }

            List<string> visibleChannels = chatContext.ChatChannels.Values
                .Where(ch => ch.Members.ContainsKey(targetSession.Account.Name) &&
                             !ch.Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_HIDDEN) &&
                             (!ch.Flags.HasFlag(ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_CLAN) ||
                              session.Account.IsStaff ||
                              (session.Account.Clan?.ID is > 0 && session.Account.Clan.ID == targetSession.Account.Clan?.ID)))
                .Select(ch => ch.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ChatBuffer online = new();
            online.WriteCommand(ChatProtocol.Command.CHAT_CMD_USER_INFO_ONLINE);
            online.WriteString(targetSession.Account.GetNameWithClanTag());
            online.WriteInt32(visibleChannels.Count);
            foreach (string channelName in visibleChannels)
            {
                online.WriteString(channelName);
            }

            session.Send(online);

            Log.Information(
                "[GetUserInfo] Sent 0x002D ONLINE info for '{Target}' ({ChannelCount} visible channels) -> '{Requester}'",
                targetSession.Account.Name, visibleChannels.Count, session.Account.Name);
            return;
        }

        if (merrick is not null)
        {
            Account? dbAccount = await merrick.Accounts
                .AsNoTracking()
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.Name == targetName);

            if (dbAccount is not null)
            {
                SendOffline(session, dbAccount);
                Log.Information(
                    "[GetUserInfo] Sent 0x002C OFFLINE info for '{Target}' -> '{Requester}'",
                    dbAccount.Name, session.Account.Name);
                return;
            }
        }

        SendNoExist(session, targetName);
        Log.Information(
            "[GetUserInfo] Sent 0x002B NO_EXIST for '{Target}' -> '{Requester}'",
            targetName, session.Account.Name);
    }

    private static void SendOffline(ChatSession session, Account account)
    {
        ChatBuffer offline = new();
        offline.WriteCommand(ChatProtocol.Command.CHAT_CMD_USER_INFO_OFFLINE);
        offline.WriteString(account.GetNameWithClanTag());
        offline.WriteString(account.TimestampLastActive.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        session.Send(offline);
    }

    private static void SendNoExist(ChatSession session, string requestedName)
    {
        ChatBuffer noExist = new();
        noExist.WriteCommand(ChatProtocol.Command.CHAT_CMD_USER_INFO_NO_EXIST);
        noExist.WriteString(requestedName);
        session.Send(noExist);
    }
}

file class GetUserInfoData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public string TargetName { get; } = buffer.ReadString();
}
