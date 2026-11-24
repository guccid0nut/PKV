// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_SET_CHAT_MODE_TYPE)]
internal sealed class SetChatMode : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        SetChatModeRequestData requestData = new(buffer);

        ChatProtocol.ChatModeType oldMode = session.ClientMetadata.ClientChatModeState;
        ChatProtocol.ChatModeType newMode = (ChatProtocol.ChatModeType)requestData.ChatModeType;

        session.ClientMetadata.ClientChatModeState = newMode;

        if (oldMode != ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE &&
            newMode == ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
        {
            // Transition TO Invisible: mask as DISCONNECTED to observers without overwriting LastKnownClientState
            session.BroadcastInvisibleDisconnect();
        }
        else if (oldMode == ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE &&
                 newMode != ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
        {
            // Transition FROM Invisible: restore true LastKnownClientState to observers and Redis presence
            ChatProtocol.ChatClientStatus restoredState =
                session.ClientMetadata.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED
                    ? session.ClientMetadata.MatchID.HasValue
                        ? ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                        : !string.IsNullOrWhiteSpace(session.ClientMetadata.MatchServerAddress)
                            ? ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
                            : ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED
                    : session.ClientMetadata.LastKnownClientState;

            session.BroadcastConnectionStatusUpdate(restoredState);
        }
        else if ((object?)session.Account is not null)
        {
            _ = session.DistributedChatState?.UpdatePresenceAsync(
                session.Account.ID,
                session.Account.Name,
                session.ClientMetadata.LastKnownClientState,
                newMode,
                session.ClientMetadata.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
                    or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                    ? session.ClientMetadata.MatchServerAddress
                    : null,
                session.ClientMetadata.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                    ? session.ClientMetadata.MatchID
                    : null);
        }

        Log.Information(
            "[SetChatMode] Account '{AccountName}' (ID: {AccountID}) transitioned ChatMode {OldMode} -> {NewMode} (Reason: '{Reason}')",
            session.Account?.Name ?? "unknown", session.Account?.ID ?? 0, oldMode, newMode, requestData.Reason);

        // Send Confirmation To Client
        ChatBuffer response = new();
        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_SET_CHAT_MODE_TYPE);
        response.WriteInt8(requestData.ChatModeType);
        response.WriteString(requestData.Reason);

        session.Send(response);
    }
}
