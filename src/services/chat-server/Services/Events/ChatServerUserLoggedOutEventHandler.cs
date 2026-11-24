// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;
using NEXUS.Contracts.Security;

namespace TRANSMUTANSTEIN.ChatServer.Services.Events;

/// <summary>
///     Handles <see cref="UserLoggedOutEvent" /> in ChatServer by terminating only the chat session(s)
///     whose <see cref="ClientChatSessionMetadata.SessionCookie" /> matches the logged-out session cookie.
///     Preserves newer sessions if the same account has already re-authenticated with a fresh session cookie.
/// </summary>
public sealed class ChatServerUserLoggedOutEventHandler(
    IChatContext chatContext,
    ILogger<ChatServerUserLoggedOutEventHandler> logger) : IEventHandler<UserLoggedOutEvent>
{
    internal static string NormalizeSessionCookie(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return string.Empty;

        string trimmed = cookie.Trim();
        return Guid.TryParse(trimmed, out Guid parsedGuid)
            ? parsedGuid.ToString("N")
            : trimmed;
    }

    /// <inheritdoc />
    public Task HandleAsync(UserLoggedOutEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        string maskedCookie = SecurityMasking.MaskToken(@event.SessionCookie);
        string targetCookie = NormalizeSessionCookie(@event.SessionCookie);

        if (string.IsNullOrEmpty(targetCookie))
        {
            logger.LogWarning(
                "[ChatServerUserLoggedOutEventHandler] Ignoring UserLoggedOutEvent {EventId} for Account '{AccountName}' with empty session cookie.",
                @event.EventId, @event.AccountName ?? "<unknown>");
            return Task.CompletedTask;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[ChatServerUserLoggedOutEventHandler] Processing UserLoggedOutEvent {EventId} for Account '{AccountName}' (Cookie: '{MaskedCookie}').",
                @event.EventId, @event.AccountName ?? "<unknown>", maskedCookie);
        }

        try
        {
            List<ChatSession> matchingSessions =
            [
                .. chatContext.ClientChatSessions.Values.Where(s =>
                    s.ClientMetadata is not null &&
                    !string.IsNullOrWhiteSpace(s.ClientMetadata.SessionCookie) &&
                    string.Equals(
                        NormalizeSessionCookie(s.ClientMetadata.SessionCookie),
                        targetCookie,
                        StringComparison.OrdinalIgnoreCase))
            ];

            if (matchingSessions.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(@event.AccountName) &&
                    chatContext.ClientChatSessions.TryGetValue(@event.AccountName, out ChatSession? activeSession))
                {
                    logger.LogInformation(
                        "[ChatServerUserLoggedOutEventHandler] Preserved active session {SessionId} for Account '{AccountName}' because its active cookie '{ActiveCookie}' differs from logged-out cookie '{LoggedOutCookie}'.",
                        activeSession.ID,
                        @event.AccountName,
                        SecurityMasking.MaskToken(activeSession.ClientMetadata?.SessionCookie),
                        maskedCookie);
                }
                else if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "[ChatServerUserLoggedOutEventHandler] No active chat session matched logged-out cookie '{MaskedCookie}' (Account '{AccountName}').",
                        maskedCookie, @event.AccountName ?? "<unknown>");
                }

                return Task.CompletedTask;
            }

            foreach (ChatSession session in matchingSessions)
            {
                Guid sessionId = session.ID;
                string accountName = session.Account?.Name ?? @event.AccountName ?? "<unknown>";
                int accountId = session.Account?.ID ?? 0;

                try
                {
                    session.TerminateClient();

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "[ChatServerUserLoggedOutEventHandler] Terminated chat session {SessionId} for Account #{AccountId} ('{AccountName}', Cookie: '{MaskedCookie}').",
                            sessionId, accountId, accountName, maskedCookie);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "[ChatServerUserLoggedOutEventHandler] Error terminating chat session {SessionId} for Account #{AccountId} ('{AccountName}', Cookie: '{MaskedCookie}').",
                        sessionId, accountId, accountName, maskedCookie);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[ChatServerUserLoggedOutEventHandler] Unhandled error processing UserLoggedOutEvent {EventId} (Account '{AccountName}', Cookie: '{MaskedCookie}').",
                @event.EventId, @event.AccountName ?? "<unknown>", maskedCookie);
        }

        return Task.CompletedTask;
    }
}
