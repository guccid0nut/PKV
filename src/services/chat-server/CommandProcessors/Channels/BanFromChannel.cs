// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN)]
public class BanFromChannel(IAccountRepository accountRepository, IChatContext chatContext, IEventBus? eventBus = null)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        BanFromChannelRequestData requestData = new(buffer);
        Log.Information(
            $"[DEBUG] BanFromChannel: Processing Request. TargetName='{requestData.TargetName}', ChannelID={requestData.ChannelID}");

        int targetAccountId;
        string targetName = requestData.TargetName;

        ChatSession? targetSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(chatSession =>
                (object?)chatSession.Account is not null &&
                chatSession.Account.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        Log.Information($"[DEBUG] BanFromChannel: TargetSession Found? {targetSession != null}");

        // Fallback: Try checking for name without clan tag (e.g. "[TAG]Name" -> "Name")
        if (targetSession == null && targetName.Contains(']'))
        {
            // Simple strip of everything up to the first ']'
            int closingBracketIndex = targetName.IndexOf(']');
            if (closingBracketIndex >= 0 && closingBracketIndex < targetName.Length - 1)
            {
                string strippedName = targetName[(closingBracketIndex + 1)..];

                targetSession = chatContext.ClientChatSessions.Values
                    .FirstOrDefault(chatSession =>
                        (object?)chatSession.Account is not null &&
                        chatSession.Account.Name.Equals(strippedName, StringComparison.OrdinalIgnoreCase));

                if (targetSession != null)
                {
                    targetName = strippedName; // Update for DB lookup consistency if needed later? No, we have session.
                    Log.Debug($"[DEBUG] BanFromChannel: TargetSession found via Stripped Name '{strippedName}'");
                }
            }
        }

        if (targetSession != null)
        {
            targetAccountId = targetSession.Account.ID;
            targetName = targetSession.Account.Name;
            Log.Information($"[DEBUG] BanFromChannel: Resolved TargetID={targetAccountId}, Name='{targetName}'");
        }
        else
        {
            Log.Information("[DEBUG] BanFromChannel: TargetSession NULL. Looking up in DB...");
            // Lookup in DB for offline users
            Account? account = await accountRepository.GetByNicknameAsync(targetName);

            // Fallback for DB: Try stripped name
            if (account == null && targetName.Contains(']'))
            {
                int closingBracketIndex = targetName.IndexOf(']');
                if (closingBracketIndex >= 0 && closingBracketIndex < targetName.Length - 1)
                {
                    string strippedName = targetName[(closingBracketIndex + 1)..];

                    account = await accountRepository.GetByNicknameAsync(strippedName);

                    if (account != null) targetName = strippedName;
                }
            }

            if (account == null)
            {
                Log.Information(
                    $"[DEBUG] BanFromChannel: Target User '{requestData.TargetName}' Not Found in Session OR DB. Exiting.");

                ChatBuffer errorBuffer = new();
                errorBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
                errorBuffer.WriteString("Channel Service");
                errorBuffer.WriteString($"The user '{requestData.TargetName}' could not be found.");

                session.Send(errorBuffer);

                return;
            }

            targetAccountId = account.ID;
            targetName = account.Name;
        }

        ChatChannel? channel = ChatChannel.Get(chatContext, session, requestData.ChannelID);
        Log.Information($"[DEBUG] BanFromChannel: Channel Found? {channel != null}. Calling Ban...");

        channel?.Ban(chatContext, session, targetAccountId, targetName);

        if (eventBus != null)
        {
            AccountBannedEvent domainEvent = new(
                Guid.NewGuid(),
                TimeProvider.System.GetUtcNow(),
                targetAccountId,
                targetName,
                TimeProvider.System.GetUtcNow().AddDays(7),
                $"Channel ban from {channel?.Name ?? requestData.ChannelID.ToString()}",
                session.Account?.ID
            );

            await eventBus.PublishAsync(domainEvent);
        }
    }
}
