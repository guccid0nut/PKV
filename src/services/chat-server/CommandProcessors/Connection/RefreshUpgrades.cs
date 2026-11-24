// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics.CodeAnalysis;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

/// <summary>
///     Handles upgrade refresh requests from game clients (Command 0x0C09).
///     When a player changes their selected upgrades or accepts friend requests, the game client sends this command.
/// </summary>
[ChatCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_REFRESH_UPGRADES)]
public class RefreshUpgrades(MerrickContext merrick) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        RefreshUpgradesRequestData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        Account? account = await merrick.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(account => account.ID == session.Account.ID);

        if (account is null)
        {
            Log.Error(@"[BUG] Account With ID ""{AccountID}"" Could Not Be Found During Upgrade Refresh",
                session.Account.ID);
            return;
        }

        session.Account.SelectedStoreItems = account.SelectedStoreItems;
        session.BroadcastConnectionStatusUpdate(session.ClientMetadata.LastKnownClientState);

        Log.Debug(@"Processed Upgrade Refresh For Account ""{AccountName}""", session.Account.Name);
    }
}

file class RefreshUpgradesRequestData(ChatBuffer buffer)
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
    public byte[] CommandBytes { get; } = buffer.ReadCommandBytes();
}
