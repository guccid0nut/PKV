// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Configuration;

/// <summary>
///     Governs the host CPU scheduling priority, core affinity targeting, and operating system yielding behavior
///     for supervised dedicated game server workers.
/// </summary>
public enum HostPriorityMode
{
    /// <summary>
    ///     Background mode: Actively yields CPU priority (BelowNormal / nice +10 / cpu.weight = 50) and targets
    ///     Efficiency Cores (E-Cores) or secondary cores so hosting on a desktop PC produces 0% disruption to gaming or work.
    ///     Default for community hosts.
    /// </summary>
    Background = 0,

    /// <summary>
    ///     High-priority mode: Non-yielding scheduling (High / nice -10 / cpu.weight = 1000) pinned to the highest-boost
    ///     Performance Cores (P-Cores) and maximum L3 cache CCDs (AMD 3D V-Cache).
    ///     Ideal for dedicated servers, tournaments, and cloud clusters.
    /// </summary>
    HighPriority = 1,

    /// <summary>
    ///     Adaptive mode: Automatically switches between HighPriority when idle, and Background when a local game client
    ///     (hon_x64.exe) is launched on the same machine.
    /// </summary>
    Adaptive = 2
}
