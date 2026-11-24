// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles game server invite requests by account ID (Command 0x0023 - CHAT_CMD_INVITE_USER_ID).
///     Wire format verified against k2_x64.dll (0x1807086f0, FUN_1806d1020, 0x1806c2b20).
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_ID)]
internal sealed class InviteUserById(
    IChatContext chatContext,
    IDatabase? distributedCacheStore = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        InviteUserByIdData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        ChatSession? targetSession = requestData.TargetAccountId <= 0
            ? null
            : chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
                (object?)s.Account is not null && s.Account.ID == requestData.TargetAccountId);

        await ServerInviteDispatcher.DispatchInviteAsync(
            session,
            targetSession,
            $"#{requestData.TargetAccountId.ToString(CultureInfo.InvariantCulture)}",
            distributedCacheStore,
            chatContext);
    }
}

file class InviteUserByIdData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int TargetAccountId { get; init; } = buffer.ReadInt32();
}
