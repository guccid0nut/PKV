// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Extensions;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_IM)]
internal class SendInstantMessage(
    IChatContext chatContext,
    IQueueArcadeService? queueArcadeService = null,
    IMatchmakingService? matchmakingService = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        Task task = ProcessAsync(session, buffer);
        if (task.IsFaulted)
        {
            Log.Error(task.Exception, "[SendInstantMessage] Command processing failed synchronously for Session={SessionId}", session.ID);
        }
        else if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                static (t, state) => Log.Error(t.Exception, "[SendInstantMessage] Async command processing failed for Session={SessionId}", state),
                session.ID,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    Task IAsynchronousCommandProcessor<ChatSession>.Process(ChatSession session, ChatBuffer buffer) =>
        ProcessAsync(session, buffer);

    public async Task ProcessAsync(ChatSession session, ChatBuffer buffer)
    {
        SendInstantMessageRequestData requestData = new(buffer);

        if (session.Account is null)
        {
            return;
        }

        if (session.Account.Name.StartsWith("__kongor", StringComparison.OrdinalIgnoreCase))
        {
            ChatBuffer securityNotice = new();
            securityNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            securityNotice.WriteString("System");
            securityNotice.WriteString("[Security] Reserved system prefix '__kongor' is forbidden for player messaging.");
            session.Send(securityNotice);
            return;
        }

        if (queueArcadeService?.TryHandleArcadeChatCommand(session, requestData.Message) == true)
        {
            return;
        }

        if (string.Equals(requestData.TargetName, MatchmakingService.ReadyCheckSenderName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestData.TargetName, "__kongor_ready_check__", StringComparison.OrdinalIgnoreCase))
        {
            if (matchmakingService is not null)
            {
                bool handled = false;
                if (MatchmakingService.TryParseReadyCheckResponse(requestData.Message, out int readyCheckId, out bool accepted))
                {
                    if (readyCheckId > 0)
                    {
                        handled = await matchmakingService.SubmitReadyCheckResponseAsync(readyCheckId, session.Account.ID, accepted)
                            .ConfigureAwait(false);
                    }
                    else if (accepted)
                    {
                        handled = await matchmakingService.SubmitPendingMatchAcceptAsync(session.Account.ID)
                            .ConfigureAwait(false);
                    }
                    else if (matchmakingService is MatchmakingService concreteMm)
                    {
                        handled = await concreteMm.SubmitPendingMatchDeclineAsync(session.Account.ID)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    string trimmed = requestData.Message?.Trim() ?? string.Empty;
                    if (string.Equals(trimmed, "ACCEPT", StringComparison.OrdinalIgnoreCase) ||
                        trimmed == "1" ||
                        string.Equals(trimmed, "TRUE", StringComparison.OrdinalIgnoreCase))
                    {
                        handled = await matchmakingService.SubmitPendingMatchAcceptAsync(session.Account.ID)
                            .ConfigureAwait(false);
                    }
                    else if ((string.Equals(trimmed, "DECLINE", StringComparison.OrdinalIgnoreCase) ||
                              trimmed == "0" ||
                              string.Equals(trimmed, "FALSE", StringComparison.OrdinalIgnoreCase)) &&
                             matchmakingService is MatchmakingService concreteMm)
                    {
                        handled = await concreteMm.SubmitPendingMatchDeclineAsync(session.Account.ID)
                            .ConfigureAwait(false);
                    }
                }

                Log.Debug(
                    "[SendInstantMessage][ReadyCheck] Routed IM response from AccountID={AccountID} Raw='{Raw}' Handled={Handled}",
                    session.Account.ID,
                    requestData.Message,
                    handled);
            }
            return;
        }

        if (string.Equals(requestData.TargetName, "__kongor_arcade__", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (session.Account.IsMuted)
        {
            ChatBuffer mutedNotice = new();
            mutedNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            mutedNotice.WriteString("System");
            mutedNotice.WriteString(
                $"[Moderation] You are currently muted until {session.Account.MuteExpiration!.Value:yyyy-MM-dd HH:mm} UTC. Reason: {session.Account.MuteReason ?? "Violating Code of Conduct"}");
            session.Send(mutedNotice);
            return;
        }

        // Find Target Session
        if (!chatContext.ClientChatSessions.TryGetValue(requestData.TargetName, out ChatSession? targetSession))
            targetSession = chatContext.ClientChatSessions.Values
                .FirstOrDefault(s => s.Account.GetNameWithClanTag().Equals(requestData.TargetName,
                    StringComparison.OrdinalIgnoreCase));

        // Check If Target Is Offline Or Invisible
        if (targetSession is null || targetSession.ClientMetadata.ClientChatModeState ==
            ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
        {
            // Send Failure Response To Sender
            ChatBuffer failure = new();
            failure.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM_FAILED);
            failure.WriteString(requestData.TargetName);

            session.Send(failure);
            return;
        }

        // Check If Target Has Ignored Sender
        if (targetSession.Account.HasIgnoredPeer(session.Account.ID))
        {
            ChatBuffer failure = new();
            failure.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM_FAILED);
            failure.WriteString(requestData.TargetName);
            session.Send(failure);

            ChatBuffer systemNotice = new();
            systemNotice.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
            systemNotice.WriteString("System");
            systemNotice.WriteString($"[System] Player {targetSession.Account.Name} has you on their ignore list.");
            session.Send(systemNotice);

            return;
        }

        // Send Message To Target
        ChatBuffer message = new();
        message.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM);
        message.WriteInt8(requestData.Flags);
        message.WriteString(session.Account.GetNameWithClanTag()); // Sender Name (Always Sent)

        if (requestData.Flags != 0)
        {
            message.WriteInt32(session.Account.ID);
            message.WriteInt8(Convert.ToByte(session.ClientMetadata.LastKnownClientState)); // Status
            message.WriteInt8(session.Account.GetChatClientFlags()); // Client Flags (Staff, etc)
            message.WriteString(session.Account.GetNameColourNoPrefixCode());
            message.WriteString(session.Account.GetIconNoPrefixCode()); // Account Icon
            message.WriteInt32(session.Account.AscensionLevel); // Ascension Level
        }

        string safeMessage = requestData.Message.Length > ChatProtocol.CHAT_MESSAGE_MAX_LENGTH
            ? requestData.Message[..ChatProtocol.CHAT_MESSAGE_MAX_LENGTH]
            : requestData.Message;

        message.WriteString(safeMessage);

        targetSession.Send(message);

        // If Flag is 1, Echo Back To Sender (Confirmation)
        if (requestData.Flags == 1)
        {
            ChatBuffer echo = new();
            echo.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM);
            echo.WriteInt8(2); // Flags = 2
            echo.WriteString(targetSession.Account.GetNameWithClanTag()); // Target Name

            // Because Flags != 0 (it is 2), we populate the extra fields for the target
            echo.WriteInt32(targetSession.Account.ID);
            echo.WriteInt8(Convert.ToByte(targetSession.ClientMetadata.LastKnownClientState)); // Status
            echo.WriteInt8(targetSession.Account.GetChatClientFlags()); // Target Flags
            echo.WriteString(targetSession.Account.GetNameColourNoPrefixCode());
            echo.WriteString(targetSession.Account.GetIconNoPrefixCode()); // Target Icon
            echo.WriteInt32(targetSession.Account.AscensionLevel); // Target Level

            echo.WriteString(safeMessage);

            session.Send(echo);
        }
    }
}
