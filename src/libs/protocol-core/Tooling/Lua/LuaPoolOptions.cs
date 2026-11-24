// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NLua;

namespace ASPIRE.Common.Tooling.Lua;

/// <summary>
/// Configuration settings for the isolated Lua state object pool.
/// </summary>
public sealed class LuaPoolOptions
{
    /// <summary>
    /// Maximum number of pooled Lua states retained in memory.
    /// Defaults to Environment.ProcessorCount * 2 (minimum 4).
    /// </summary>
    public int MaximumRetained { get; set; } = Math.Max(4, Environment.ProcessorCount * 2);

    /// <summary>
    /// Number of Lua states to pre-warm upon pool initialization.
    /// Defaults to 2.
    /// </summary>
    public int PrewarmCount { get; set; } = 2;

    /// <summary>
    /// Whether to force Lua garbage collection on return (reclaims native memory between script runs).
    /// Defaults to true.
    /// </summary>
    public bool AutoCollectGarbageOnReturn { get; set; } = true;

    /// <summary>
    /// Whether to initialize standard K2 environment stubs (Widgets, Cvars, Translators, Explode).
    /// Defaults to true.
    /// </summary>
    public bool InitializeK2Mocks { get; set; } = true;

    /// <summary>
    /// Optional custom initialization callback invoked when a new Lua state is created.
    /// </summary>
    public Action<NLua.Lua>? CustomInitializer { get; set; }

    /// <summary>
    /// Optional custom Lua reset script evaluated on state return to clean dirty global tables.
    /// </summary>
    public string? CustomResetScript { get; set; }
}
