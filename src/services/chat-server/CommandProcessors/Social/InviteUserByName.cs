// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Network;
using MERRICK.DatabaseContext.Extensions;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles game server invite requests by account name (Command 0x0024 - CHAT_CMD_INVITE_USER_NAME).
///     Wire format verified against k2_x64.dll (0x1807087f0, FUN_1806d1020, 0x1806c2b20).
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_NAME)]
internal sealed class InviteUserByName(
    IChatContext chatContext,
    IDatabase? distributedCacheStore = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        InviteUserByNameData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        string strippedTargetName = MERRICK.DatabaseContext.Extensions.AccountExtensions
            .SeparateClanTagFromAccountName(requestData.TargetName.Trim())
            .AccountName
            .Trim();

        ChatSession? targetSession = string.IsNullOrWhiteSpace(strippedTargetName)
            ? null
            : chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
                (object?)s.Account is not null &&
                string.Equals(s.Account.Name, strippedTargetName, StringComparison.OrdinalIgnoreCase));

        await ServerInviteDispatcher.DispatchInviteAsync(
            session,
            targetSession,
            strippedTargetName,
            distributedCacheStore,
            chatContext);
    }
}

internal static class ServerInviteDispatcher
{
    public static async Task DispatchInviteAsync(
        ChatSession inviterSession,
        ChatSession? targetSession,
        string targetDisplayLabel,
        IDatabase? distributedCacheStore,
        IChatContext? chatContext = null)
    {
        if (inviterSession.ClientMetadata is null ||
            inviterSession.ClientMetadata.LastKnownClientState is not (
                ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME or
                ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME) ||
            string.IsNullOrWhiteSpace(inviterSession.ClientMetadata.MatchServerAddress) ||
            inviterSession.ClientMetadata.MatchServerAddress == "0.0.0.0:0")
        {
            Log.Warning(
                "[ServerInvite] Invite from '{Inviter}' -> '{Target}' failed (0x0027 CHAT_CMD_INVITE_FAILED_GAME): inviter is not in a game or MatchServerAddress missing",
                inviterSession.Account.Name, targetDisplayLabel);

            ChatBuffer failedGame = new();
            failedGame.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_GAME);
            inviterSession.Send(failedGame);
            return;
        }

        if (targetSession is null ||
            (object?)targetSession.Account is null ||
            targetSession.ClientMetadata is null ||
            targetSession.Account.ID == inviterSession.Account.ID ||
            (!inviterSession.Account.IsStaff &&
             (targetSession.ClientMetadata.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE ||
              targetSession.Account.HasIgnoredPeer(inviterSession.Account.ID) ||
              targetSession.Account.HasBannedPeer(inviterSession.Account.ID))))
        {
            Log.Information(
                "[ServerInvite] Invite from '{Inviter}' (ID: {InviterID}) -> '{Target}' failed (0x0026 CHAT_CMD_INVITE_FAILED_USER): target offline, self, invisible, or ignoring/banning inviter",
                inviterSession.Account.Name, inviterSession.Account.ID, targetDisplayLabel);

            ChatBuffer failedUser = new();
            failedUser.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_USER);
            inviterSession.Send(failedUser);
            return;
        }

        MatchServer? matchServer = null;
        int parsedPort = 0;
        string[] addressParts = inviterSession.ClientMetadata.MatchServerAddress.Split(':');
        if (addressParts.Length == 2)
        {
            _ = int.TryParse(addressParts[1], out parsedPort);
        }

        if (distributedCacheStore is not null)
        {
            if (inviterSession.ClientMetadata.MatchID is > 0)
            {
                MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(inviterSession.ClientMetadata.MatchID.Value);
                if (matchData is not null)
                {
                    matchServer = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);
                }
            }

            if (matchServer is null && addressParts.Length == 2 && parsedPort > 0)
            {
                matchServer = await distributedCacheStore.GetMatchServerByIPAddressAndPort(addressParts[0], parsedPort);
            }
        }

        bool isInviterInQueueArcade =
            matchServer?.IsQueueArcadeServer() == true ||
            inviterSession.ClientMetadata.ActiveArcadeServerId.HasValue ||
            (parsedPort > 0 && MatchServer.IsQueueArcadePort(parsedPort)) ||
            (chatContext?.MatchServerChatSessions.Values.Any(s =>
                s.IsSessionConnected &&
                s.ServerMetadata is { IsQueueArcade: true } sm &&
                ((inviterSession.ClientMetadata.MatchID is > 0 && sm.MatchID == inviterSession.ClientMetadata.MatchID.Value) ||
                 (matchServer is not null && sm.ServerID == matchServer.ID))) == true);

        if (isInviterInQueueArcade)
        {
            Log.Information(
                "[ServerInvite] Blocked 0x0025 server invite from '{Inviter}' (ID: {InviterID}) -> '{Target}': inviter is in Queue Arcade (ServerID={ServerID}, MatchID={MatchID}, Address='{Address}')",
                inviterSession.Account.Name,
                inviterSession.Account.ID,
                targetDisplayLabel,
                matchServer?.ID ?? inviterSession.ClientMetadata.ActiveArcadeServerId ?? -1,
                inviterSession.ClientMetadata.MatchID ?? 0,
                inviterSession.ClientMetadata.MatchServerAddress);

            ChatBuffer failedArcadeInvite = new();
            failedArcadeInvite.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_USER);
            inviterSession.Send(failedArcadeInvite);
            return;
        }

        string resolvedServerAddress = SplitHorizonEndpointResolver.ResolveStoredServerAddressForClient(
            inviterSession.ClientMetadata.MatchServerAddress,
            targetSession.GetRemoteClientIP(),
            matchServer);

        ChatProtocol.ChatClientStatus inviterStatus = inviterSession.ClientMetadata.LastKnownClientState;

        ChatBuffer invitePacket = new();
        invitePacket.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITED_TO_SERVER); // 0x0025
        invitePacket.WriteString(inviterSession.Account.GetNameWithClanTag());
        invitePacket.WriteInt32(inviterSession.Account.ID);
        invitePacket.WriteInt8((byte)inviterStatus);
        invitePacket.WriteInt8(inviterSession.Account.GetChatClientFlags());
        invitePacket.WriteString(inviterSession.Account.GetNameColourNoPrefixCode());
        invitePacket.WriteString(inviterSession.Account.GetIconNoPrefixCode());
        invitePacket.WriteString(resolvedServerAddress);

        if (inviterStatus is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
        {
            invitePacket.WriteString(inviterSession.ClientMetadata.MatchName ?? string.Empty);
            invitePacket.WriteInt32(inviterSession.ClientMetadata.MatchID ?? 0);
        }

        invitePacket.WriteInt32(inviterSession.Account.AscensionLevel);

        targetSession.Send(invitePacket);

        Log.Information(
            "[ServerInvite] Delivered 0x0025 server invite from '{Inviter}' (ID: {InviterID}, Status: {Status}, Server: {ResolvedServer}, MatchID: {MatchID}) -> '{Invitee}' (ID: {InviteeID})",
            inviterSession.Account.Name,
            inviterSession.Account.ID,
            inviterStatus,
            resolvedServerAddress,
            inviterSession.ClientMetadata.MatchID ?? 0,
            targetSession.Account.Name,
            targetSession.Account.ID);
    }
}

file class InviteUserByNameData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public string TargetName { get; } = buffer.ReadString();
}
