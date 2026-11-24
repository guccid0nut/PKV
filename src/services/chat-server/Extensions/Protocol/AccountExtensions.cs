// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Extensions.Protocol;

public static class AccountExtensions
{
    public static byte GetChatClientFlags(this Account account)
    {
        byte flags = 0x0;

        if (account.Type == AccountType.Staff)
            flags |= Convert.ToByte(ChatProtocol.ChatClientType.CHAT_CLIENT_IS_STAFF);

        if (account.ClanTier == ClanTier.Leader)
            flags |= Convert.ToByte(ChatProtocol.ChatClientType.CHAT_CLIENT_IS_CLAN_LEADER);

        if (account.ClanTier == ClanTier.Officer)
            flags |= Convert.ToByte(ChatProtocol.ChatClientType.CHAT_CLIENT_IS_OFFICER);

        return flags;
    }
}
