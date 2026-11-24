// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public delegate Task CommandExecutionHandler(IServiceProvider serviceProvider, ChatSession session, ChatBuffer buffer);

public sealed record ChatCommandDescriptor(
    ushort Command,
    string Name,
    CommandExecutionHandler ExecuteAsync,
    bool AllowUnauthenticated = false,
    ChatPeerRole AllowedRoles = ChatPeerRole.Client
);

/// <summary>
///     High-performance zero-reflection chat command registry and precompiled delegate dispatcher.
/// </summary>
public static class ChatCommandRegistry
{
    private static readonly FrozenDictionary<ushort, ChatCommandDescriptor> Descriptors = InitializeDescriptors();
    private static readonly ConditionalWeakTable<IServiceProvider, ConcurrentDictionary<Type, object>> ProcessorCache = new();

    /// <summary>
    ///     Gets the total number of registered chat commands.
    /// </summary>
    public static int RegisteredCount => Descriptors.Count;

    /// <summary>
    ///     Attempts to retrieve the precompiled command descriptor for the given command opcode in O(1) time without
    ///     reflection.
    /// </summary>
    public static bool TryGetDescriptor(ushort command, [NotNullWhen(true)] out ChatCommandDescriptor? descriptor)
    {
        return Descriptors.TryGetValue(command, out descriptor);
    }

    public static bool AuthorizeCommand(
        ChatSession session,
        ushort command,
        string commandName,
        bool allowUnauthenticated,
        ChatPeerRole allowedRoles)
    {
        if (!allowUnauthenticated && !session.IsAuthenticated)
        {
            Log.Warning(
                "[SECURITY-ALERT] Blocked pre-authenticated command 0x{Command:X4} ({CommandName}) on Session {SessionID} (PeerRole={PeerRole})",
                command,
                commandName,
                session.ID,
                session.PeerRole);
            return false;
        }

        ChatPeerRole peerRole = session.PeerRole;
        if (peerRole != ChatPeerRole.None && (allowedRoles & peerRole) == 0)
        {
            Log.Warning(
                "[SECURITY-ALERT] Blocked cross-role command 0x{Command:X4} ({CommandName}) on Session {SessionID} (AccountID={AccountID}, PeerRole={PeerRole}, AllowedRoles={AllowedRoles})",
                command,
                commandName,
                session.ID,
                (object?)session.Account is not null ? session.Account.ID : 0,
                peerRole,
                allowedRoles);
            return false;
        }

        return true;
    }

    private static FrozenDictionary<ushort, ChatCommandDescriptor> InitializeDescriptors()
    {
        Dictionary<ushort, ChatCommandDescriptor> map = [];
        Assembly assembly = typeof(ChatCommandRegistry).Assembly;
        Type[] types = assembly.GetTypes();

        foreach (Type type in types)
        {
            ChatCommandAttribute? attribute = type.GetCustomAttribute<ChatCommandAttribute>();
            if (attribute is null) continue;

            ushort command = attribute.Command;
            string commandName = type.Name;
            bool allowUnauthenticated = attribute.AllowUnauthenticated;
            ChatPeerRole allowedRoles = attribute.AllowedRoles;
            ObjectFactory factory = ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
            CommandExecutionHandler innerDelegate;

            if (typeof(ISynchronousCommandProcessor<ChatSession>).IsAssignableFrom(type))
                innerDelegate = (sp, session, buffer) =>
                {
                    ConcurrentDictionary<Type, object> instances = ProcessorCache.GetOrCreateValue(sp);
                    object instance = instances.GetOrAdd(type, t => factory(sp, null));
                    ISynchronousCommandProcessor<ChatSession> handler =
                        (ISynchronousCommandProcessor<ChatSession>)instance;
                    handler.Process(session, buffer);
                    return Task.CompletedTask;
                };
            else if (typeof(IAsynchronousCommandProcessor<ChatSession>).IsAssignableFrom(type))
                innerDelegate = (sp, session, buffer) =>
                {
                    ConcurrentDictionary<Type, object> instances = ProcessorCache.GetOrCreateValue(sp);
                    object instance = instances.GetOrAdd(type, t => factory(sp, null));
                    IAsynchronousCommandProcessor<ChatSession> handler =
                        (IAsynchronousCommandProcessor<ChatSession>)instance;
                    return handler.Process(session, buffer);
                };
            else
                continue;

            CommandExecutionHandler guardedDelegate = (sp, session, buffer) =>
            {
                if (!AuthorizeCommand(session, command, commandName, allowUnauthenticated, allowedRoles))
                {
                    return Task.CompletedTask;
                }

                return innerDelegate(sp, session, buffer);
            };

            ChatCommandDescriptor descriptor = new(command, commandName, guardedDelegate, allowUnauthenticated, allowedRoles);
            map[command] = descriptor;
        }

        return map.ToFrozenDictionary();
    }
}
