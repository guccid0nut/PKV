// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;

namespace TRANSMUTANSTEIN.ChatServer.Services.Events;

/// <summary>
///     Handles <see cref="AccountBannedEvent" /> in ChatServer by terminating active chat sessions for the banned account.
/// </summary>
public sealed class ChatServerAccountBannedEventHandler(
    IChatContext chatContext,
    ILogger<ChatServerAccountBannedEventHandler> logger) : IEventHandler<AccountBannedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(AccountBannedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[ChatServerAccountBannedEventHandler] Received AccountBannedEvent for Account #{AccountId} ({AccountName}). Reason: '{Reason}'",
                @event.AccountId, @event.AccountName, @event.BanReason ?? "None specified");

        try
        {
            // Find active sessions for the account
            List<ChatSession> matchingSessions =
            [
                .. chatContext.ClientChatSessions.Values.Where(s =>
                    s.Account != null && (s.Account.ID == @event.AccountId ||
                                          s.Account.Name.Equals(@event.AccountName,
                                              StringComparison.OrdinalIgnoreCase)))
            ];

            foreach (ChatSession session in matchingSessions)
                try
                {
                    // Send whisper or system disconnect notice
                    ChatBuffer buffer = new();
                    buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                    buffer.WriteString("System");
                    buffer.WriteString(
                        $"Your account has been suspended. Reason: {@event.BanReason ?? "Administrative action"}");
                    session.Send(buffer);

                    session.Disconnect();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "[ChatServerAccountBannedEventHandler] Error disconnecting banned session for Account #{AccountId}",
                        @event.AccountId);
                }

            if (matchingSessions.Count > 0 && logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "[ChatServerAccountBannedEventHandler] Terminated {Count} active chat sessions for banned account #{AccountId} ({AccountName})",
                    matchingSessions.Count, @event.AccountId, @event.AccountName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[ChatServerAccountBannedEventHandler] Error handling AccountBannedEvent for Account #{AccountId}",
                @event.AccountId);
        }

        return Task.CompletedTask;
    }
}
