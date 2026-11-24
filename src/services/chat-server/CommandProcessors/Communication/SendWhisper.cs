// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_WHISPER)]
internal class SendWhisper(
    IChatContext chatContext,
    IDistributedChatStateService? distributedState = null,
    IQueueArcadeService? queueArcadeService = null,
    IMatchmakingService? matchmakingService = null)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        Task task = ProcessAsync(session, buffer);
        if (task.IsFaulted)
        {
            Log.Error(task.Exception, "[SendWhisper] Command processing failed synchronously for Session={SessionId}", session.ID);
        }
        else if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                static (t, state) => Log.Error(t.Exception, "[SendWhisper] Async command processing failed for Session={SessionId}", state),
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
        SendWhisperRequestData requestData = new(buffer);

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
                    "[SendWhisper][ReadyCheck] Routed Whisper response from AccountID={AccountID} Raw='{Raw}' Handled={Handled}",
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

        Whisper
            .Create(requestData.Message)
            .Send(chatContext, session, requestData.TargetName, distributedState);
    }
}
