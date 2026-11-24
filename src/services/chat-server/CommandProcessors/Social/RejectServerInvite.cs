// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Extensions;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Relays game server invite rejections back to the inviter (Command 0x0028 - CHAT_CMD_INVITE_REJECTED).
///     Client -> Server (0x180708900): uint16(0x0028) + int32(inviterAccountId)
///     Server -> Inviter (FUN_1806d1020): uint16(0x0028) + string(rejecterNameWithClanTag) + int32(rejecterAccountId)
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_INVITE_REJECTED)]
internal sealed class RejectServerInvite(IChatContext chatContext) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        RejectServerInviteData requestData = new(buffer);

        if ((object?)session.Account is null || requestData.InviterAccountId <= 0) return;

        ChatSession? inviterSession = chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
            (object?)s.Account is not null && s.Account.ID == requestData.InviterAccountId);

        if (inviterSession is null)
        {
            Log.Debug(
                "[ServerInvite] Reject notification from '{Rejecter}' (ID: {RejecterID}) dropped: inviter #{InviterID} is offline",
                session.Account.Name, session.Account.ID, requestData.InviterAccountId);
            return;
        }

        if (!session.Account.IsStaff &&
            (inviterSession.Account.HasIgnoredPeer(session.Account.ID) ||
             inviterSession.Account.HasBannedPeer(session.Account.ID)))
        {
            return;
        }

        ChatBuffer notification = new();
        notification.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_REJECTED);
        notification.WriteString(session.Account.GetNameWithClanTag());
        notification.WriteInt32(session.Account.ID);

        inviterSession.Send(notification);

        Log.Information(
            "[ServerInvite] Relayed 0x0028 invite rejection from '{Rejecter}' (ID: {RejecterID}) -> '{Inviter}' (ID: {InviterID})",
            session.Account.Name, session.Account.ID, inviterSession.Account.Name, inviterSession.Account.ID);
    }
}

file class RejectServerInviteData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int InviterAccountId { get; init; } = buffer.ReadInt32();
}
