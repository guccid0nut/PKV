// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
///     Rate limiting categories for MCP tool calls.
///     Each category has its own token bucket budget per agent.
/// </summary>
public enum McpToolCategory
{
    /// <summary>Read-only inspections, manifests, and diagnostics (60/min).</summary>
    ReadOnly,

    /// <summary>UI control, navigation, and view rendering (30/min).</summary>
    UiControl,

    /// <summary>Live game engine commands, actions, steps, and Lua injection (10/min).</summary>
    GameCommand,

    /// <summary>Server and cluster lifecycle management (5/min).</summary>
    ServerManagement,

    /// <summary>Network diagnostics, chaos simulation, packet fuzzing (10/min).</summary>
    NetworkDiagnostics,

    /// <summary>Database fixtures, migrations, and schema operations (5/min).</summary>
    DatabaseOps,

    /// <summary>Asset baking, texture conversion, and mod build pipelines (3/min).</summary>
    AssetPipeline,

    /// <summary>End-to-end match simulation, bot scrimmage launches (2/min).</summary>
    Simulation,

    /// <summary>Flamegraphs, CPU/GPU profiling, memory leak detection (3/min).</summary>
    HeavyProfiling,

    /// <summary>Default budget for general uncategorized tools (20/min).</summary>
    Default
}
