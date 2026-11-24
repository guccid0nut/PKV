// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG)]
internal class SendChannelMessage(
    FloodPreventionService floodPreventionService,
    IChatContext chatContext,
    IDistributedChatStateService? distributedState = null,
    IQueueArcadeService? queueArcadeService = null,
    ChatModerationFilterService? moderationFilter = null)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        SendChannelMessageRequestData requestData = new(buffer);

        if (queueArcadeService?.TryHandleArcadeChatCommand(session, requestData.Message) == true) return;

        ChatChannel? channel = ChatChannel.Get(chatContext, session, requestData.ChannelID);

        if (channel is null) return;

        // Check Flood Prevention (Service Handles Both Check And Response)
        if (!floodPreventionService.CheckAndHandleFloodPrevention(session)) return;

        // Check If The Sender Is Globally Muted
        if (session.Account.IsMuted)
        {
            ChatBuffer mutedNotice = new();
            mutedNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            mutedNotice.WriteString("System");
            mutedNotice.WriteString(
                $"[Moderation] You are currently muted until {session.Account.MuteExpiration!.Value:yyyy-MM-dd HH:mm} UTC. Reason: {session.Account.MuteReason ?? "Violating Code of Conduct"}");
            session.Send(mutedNotice);

            ChatBuffer silencedResponse = new();
            silencedResponse.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCED);
            silencedResponse.WriteInt32(requestData.ChannelID);
            session.Send(silencedResponse);

            return;
        }

        // Check If The Sender Is Silenced In This Channel
        if (channel.IsSilenced(session))
        {
            ChatBuffer response = new();

            response.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCED);
            response.WriteInt32(requestData.ChannelID); // Channel ID

            session.Send(response);

            return;
        }

        string messageContent = requestData.Message;

        // Evaluate Config-Driven Sliding-Window Spam & Profanity Moderation Filter
        if (moderationFilter is not null)
        {
            ChatModerationResult modResult = moderationFilter.EvaluateMessage(
                session.Account.ID,
                session.Account.Type,
                messageContent);

            if (modResult.IsBlocked)
            {
                ChatBuffer modNotice = new();
                modNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                modNotice.WriteString("System");
                modNotice.WriteString($"[Moderation] {modResult.Reason}");
                session.Send(modNotice);

                if (modResult.Action is ChatModerationAction.RejectSpam or ChatModerationAction.RejectDuplicateSpam)
                {
                    ChatBuffer floodWarning = new();
                    floodWarning.WriteCommand(ChatProtocol.Command.CHAT_CMD_FLOODING);
                    session.Send(floodWarning);
                }

                return;
            }

            messageContent = modResult.ProcessedMessage;
        }

        // Enforce Message Content Length Limit
        // Staff Accounts Are Exempt From Message Length Restrictions, For Moderation And Administration Purposes
        if (session.Account.Type is not AccountType.Staff &&
            messageContent.Length > ChatProtocol.CHAT_MESSAGE_MAX_LENGTH)
        {
            messageContent = messageContent[..ChatProtocol.CHAT_MESSAGE_MAX_LENGTH];

            ChatBuffer truncationWarning = new();

            truncationWarning.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            truncationWarning.WriteString("Channel Service");
            truncationWarning.WriteString("Your message was too long and has been truncated.");

            session.Send(truncationWarning);
        }

        // Broadcast The Message To All Channel Members
        ChatBuffer broadcast = new();

        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        broadcast.WriteInt32(session.Account.ID); // Sender Account ID
        broadcast.WriteInt32(requestData.ChannelID); // Channel ID
        broadcast.WriteString(messageContent); // Message Content (Potentially Truncated / Filtered)

        // K2 CChatManager::SubmitChatMessage (0x1806a6060) locally echoes chat_channel_message_sent (isSelf=true)
        // immediately upon send, and inbound 0x0003 handler (0x1806c9c70) does not filter self AccountID.
        // Never echo 0x0003 back to the sender session.
        channel.BroadcastMessage(broadcast, excludeAccountID: session.Account.ID, senderAccountId: session.Account.ID);

        // Dispatch to remote cluster nodes
        if (distributedState != null)
        {
            _ = distributedState.PublishChannelMessageAsync(
                requestData.ChannelID,
                channel.Name,
                session.Account.ID,
                session.Account.Name,
                messageContent);
        }
    }
}
