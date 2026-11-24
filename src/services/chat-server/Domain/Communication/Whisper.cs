// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MERRICK.DatabaseContext.Extensions;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Communication;

public class Whisper
{
    /// <summary>
    ///     Hidden Constructor Which Enforces <see cref="Create" /> As The Primary Mechanism For Creating Whispers
    /// </summary>
    private Whisper()
    {
    }

    public required string Message { get; init; }

    public static Whisper Create(string message)
    {
        if (message.Length > Core.ChatProtocol.CHAT_MESSAGE_MAX_LENGTH)
            message = message[..Core.ChatProtocol.CHAT_MESSAGE_MAX_LENGTH];
        return new Whisper { Message = message };
    }

    public void Send(
        IChatContext chatContext,
        ChatSession senderSession,
        string recipientName,
        IDistributedChatStateService? distributedState = null)
    {
        string normalizedName = StripClanTag(recipientName);

        if (!chatContext.ClientChatSessions.TryGetValue(normalizedName, out ChatSession? recipientSession) &&
            !chatContext.ClientChatSessions.TryGetValue(recipientName, out recipientSession))
        {
            recipientSession = chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
                (object?)s.Account is not null &&
                (s.Account.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                 s.Account.GetNameWithClanTag().Equals(recipientName.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        if (recipientSession != null)
        {
            Send(senderSession, recipientSession);
            return;
        }

        if (distributedState != null)
        {
            _ = Task.Run(async () =>
            {
                bool delivered = await distributedState.RouteWhisperAsync(
                    senderSession.Account.Name,
                    senderSession.Account.ID,
                    normalizedName,
                    Message);

                if (!delivered)
                {
                    SendWhisperFailure(senderSession, recipientName);
                }
            });
            return;
        }

        SendWhisperFailure(senderSession, recipientName);
    }

    private static string StripClanTag(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        string trimmed = rawName.Trim();
        if (trimmed.StartsWith('['))
        {
            int closeIdx = trimmed.IndexOf(']');
            if (closeIdx > 0 && closeIdx + 1 < trimmed.Length)
            {
                return trimmed[(closeIdx + 1)..].Trim();
            }
        }

        return trimmed;
    }

    public void Send(ChatSession senderSession, ChatSession recipientSession)
    {
        // Check if recipient has ignored sender
        if (recipientSession.Account.HasIgnoredPeer(senderSession.Account.ID))
        {
            SendWhisperFailure(senderSession, recipientSession.Account.Name);

            ChatBuffer systemNotice = new();
            systemNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            systemNotice.WriteString("System");
            systemNotice.WriteString($"[System] Player {recipientSession.Account.Name} has you on their ignore list.");
            senderSession.Send(systemNotice);

            return;
        }

        // Check Recipient's Chat Mode
        switch (recipientSession.ClientMetadata.ClientChatModeState)
        {
            // DND: Block Whisper And Send Auto-Response
            case ChatProtocol.ChatModeType.CHAT_MODE_DND:
                SendWhisperFailure(senderSession, recipientSession.Account.Name)
                    .SendAutomaticResponse(senderSession, recipientSession, "Do Not Disturb");

                return;

            // Invisible: Treat As Offline
            case ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE:
                SendWhisperFailure(senderSession, recipientSession.Account.Name);

                return;

            // AFK: Deliver Message But Send Auto-Response
            case ChatProtocol.ChatModeType.CHAT_MODE_AFK:
                SendWhisperSuccess(senderSession.Account.Name, recipientSession)
                    .SendAutomaticResponse(senderSession, recipientSession, "Away From Keyboard");

                return;

            // Available: Normal Delivery
            case ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE:
                SendWhisperSuccess(senderSession.Account.Name, recipientSession);

                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(recipientSession),
                    recipientSession.ClientMetadata.ClientChatModeState,
                    $@"Unknown Chat Mode State ""{recipientSession.ClientMetadata.ClientChatModeState}"" For Recipient ""{recipientSession.Account.Name}""");
        }
    }

    private Whisper SendWhisperSuccess(string senderName, ChatSession recipientSession)
    {
        ChatBuffer whisperSuccess = new();

        whisperSuccess.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisperSuccess.WriteString(senderName); // Sender Name
        whisperSuccess.WriteString(Message); // Message Content

        recipientSession.Send(whisperSuccess);

        return this;
    }


    private Whisper SendWhisperFailure(ChatSession senderSession, string recipientName)
    {
        ChatBuffer whisperFailed = new();

        whisperFailed.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER_FAILED);
        whisperFailed.WriteString(recipientName); // Recipient's Account Name
        whisperFailed.WriteString(Message); // Message Content

        senderSession.Send(whisperFailed);

        return this;
    }

    private void SendAutomaticResponse(ChatSession senderSession, ChatSession recipientSession,
        string messageAutomaticResponse)
    {
        ChatBuffer automaticResponse = new();

        automaticResponse.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHAT_MODE_AUTO_RESPONSE);
        automaticResponse.WriteInt8(Convert.ToByte(recipientSession.ClientMetadata
            .ClientChatModeState)); // Recipient's Chat Mode Type
        automaticResponse.WriteString(recipientSession.Account.Name); // Recipient's Account Name
        automaticResponse.WriteString(messageAutomaticResponse); // Message Content

        senderSession.Send(automaticResponse);
    }
}
