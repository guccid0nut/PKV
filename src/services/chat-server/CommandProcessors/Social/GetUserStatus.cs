// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Chat;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

[ChatCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_GET_USER_STATUS)]
internal class GetUserStatus(
    IChatContext chatContext,
    IDistributedChatStateService? distributedState = null)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        GetUserStatusRequestData requestData = new(buffer);
        int targetAccountId = requestData.AccountID;

        bool canSeeInvisible = (object?)session.Account is not null &&
                               (session.Account.ID == targetAccountId || session.Account.IsStaff);

        ChatSession? targetSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(s => (object?)s.Account is not null && s.Account.ID == targetAccountId);

        ChatBuffer response = new();
        response.WriteCommand(ChatProtocol.ChatServerToClient.NET_CHAT_CL_USER_STATUS);
        response.WriteInt32(targetAccountId);

        if (targetSession != null)
        {
            bool maskInvisible = !canSeeInvisible &&
                                 targetSession.ClientMetadata?.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE;

            byte reportedStatus = maskInvisible
                ? (byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED
                : (byte)(targetSession.ClientMetadata?.LastKnownClientState ?? ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);

            response.WriteInt8(reportedStatus);

            // Visuals
            response.WriteInt8(targetSession.Account.GetChatClientFlags());
            response.WriteString(targetSession.Account.GetChatSymbolNoPrefixCode());
            response.WriteString(targetSession.Account.GetNameColourNoPrefixCode());
            response.WriteString(targetSession.Account.GetIconNoPrefixCode());
            response.WriteInt32(targetSession.Account.AscensionLevel);
        }
        else
        {
            DistributedUserPresence? presence = null;
            if (distributedState != null)
            {
                presence = await distributedState.GetUserPresenceByIdAsync(targetAccountId);
            }

            if (presence != null)
            {
                bool maskInvisible = !canSeeInvisible &&
                                     presence.ChatMode == (byte)ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE;

                response.WriteInt8(maskInvisible
                    ? (byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED
                    : presence.ClientStatus);
                response.WriteInt8(presence.ClientFlags);
                response.WriteString(presence.ChatSymbol ?? string.Empty);
                response.WriteString(presence.NameColour ?? string.Empty);
                response.WriteString(presence.AccountIcon ?? string.Empty);
                response.WriteInt32(presence.AscensionLevel);
            }
            else
            {
                response.WriteInt8((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);

                // Defaults to prevent partial read errors
                response.WriteInt8(0);
                response.WriteString(string.Empty);
                response.WriteString(string.Empty);
                response.WriteString(string.Empty);
                response.WriteInt32(0);
            }
        }

        session.Send(response);
    }
}
