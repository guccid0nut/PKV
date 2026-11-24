// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: K2UiBridgeExtensions.cs
// Subsystem: ProjectKongor Client UI & K2 Scripting
// Description: Extension methods providing fluent, strongly-typed K2 UI access
//              over any IGameIpcBridge or INativeClientIpcBridge.
// ============================================================================

using System;
using NEXUS.Contracts.K2;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Fluent extension methods connecting IPC bridges to strongly-typed K2 UI contracts.
/// </summary>
public static class K2UiBridgeExtensions
{
    /// <summary>
    /// Exposes strongly-typed K2 UI interface contracts over this IPC bridge.
    /// Invariant: Guarantees zero raw string concatenation and eliminates invalid DoEvent signatures.
    /// </summary>
    public static K2UiContractsRoot Ui(this IGameIpcBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return new K2UiContractsRoot((lua, timeout, ct) => bridge.ExecuteLuaAsync(lua, timeout, ct));
    }
}
