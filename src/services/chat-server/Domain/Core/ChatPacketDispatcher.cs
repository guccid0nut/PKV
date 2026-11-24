// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

/// <summary>
///     High-performance zero-allocation precompiled binary packet dispatcher.
///     Dispatches directly through the precompiled FrozenDictionary registry in O(1) time without reflection.
/// </summary>
public static class ChatPacketDispatcher
{
    public static async Task DispatchAsync(
        IServiceProvider serviceProvider,
        ChatSession session,
        ushort opcode,
        ChatBuffer buffer)
    {
        if (ChatCommandRegistry.TryGetDescriptor(opcode, out ChatCommandDescriptor? descriptor))
        {
            await descriptor.ExecuteAsync(serviceProvider, session, buffer).ConfigureAwait(false);
        }
    }
}
