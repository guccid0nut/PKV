// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Clans;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CLAN_REMOVE_NOTIFY)]
public class ClanRemoveNotify : IAsynchronousCommandProcessor<ChatSession>
{
    public Task Process(ChatSession session, ChatBuffer buffer)
    {
        Account? account = (Account?)session.Account;
        if (account is null) return Task.CompletedTask;

        // PASSIVE HANDLER (Legacy Read Mode)
        // We revert to reading just an Int32 (4 bytes), treating it as "consumed" data.
        // This mimics the state where the system was "working" (no freeze).
        // We do strictly nothing else.

        buffer.ReadInt32(); // Consume 4 bytes (Legacy behavior)

        Log.Information("[CLAN] ClanRemoveNotify (Passive/Legacy): Received packet. Consumed 4 bytes. Ignoring.");

        return Task.CompletedTask;
    }
}
