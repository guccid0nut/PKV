// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

[ChatCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_UNBLOCK_PHRASE)]
public class UnblockPhrase(MerrickContext merrick) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        UnblockPhraseRequestData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        string phrase = requestData.Phrase?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(phrase)) return;
        if (phrase.Length > 200) phrase = phrase[..200];
 
        // Fetch account from DB with BlockedPhrases navigation collection
        Account? account = await merrick.Accounts
            .Include(a => a.BlockedPhrases)
            .FirstOrDefaultAsync(a => a.ID == session.Account.ID);

        if (account is not null)
        {
            if (account.RemoveBlockedPhrase(phrase))
            {
                try
                {
                    await merrick.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Concurrently removed; safe to ignore
                }

                // Update Session Cache
                session.Account.RemoveBlockedPhrase(phrase);
            }
        }
    }
}
