// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Configuration;

/// <summary>
///     Configuration options for Win32 Job Object sandbox resource limits
///     applied to all child Heroes of Newerth dedicated server instances (hon_x64.exe).
/// </summary>
public sealed class SandboxLimitsOptions
{
    public const string SectionName = "SandboxLimits";

    /// <summary>
    ///     Hard commit memory limit per individual match server process in bytes.
    ///     Default: 4096 MB (4 GB).
    /// </summary>
    public ulong MaxProcessMemoryBytes { get; set; } = 4096UL * 1024UL * 1024UL;

    /// <summary>
    ///     Total aggregate commit memory limit for all processes in the job object in bytes.
    ///     Default: 16384 MB (16 GB).
    /// </summary>
    public ulong MaxJobMemoryBytes { get; set; } = 16384UL * 1024UL * 1024UL;

    /// <summary>
    ///     Maximum active child processes allowed simultaneously in the sandbox.
    ///     Default: 32 processes.
    /// </summary>
    public uint MaxActiveProcesses { get; set; } = 32;

    /// <summary>
    ///     Governs the host CPU scheduling priority, core affinity targeting, and operating system yielding behavior.
    ///     Default: Background.
    /// </summary>
    public HostPriorityMode PriorityMode { get; set; } = HostPriorityMode.Background;

    /// <summary>
    ///     Legacy toggle: whether to assign ABOVE_NORMAL_PRIORITY_CLASS to child processes.
    ///     Superseded by <see cref="PriorityMode" />.
    /// </summary>
    public bool EnforceAboveNormalPriority { get; set; } = true;

    /// <summary>
    ///     Whether to enforce automatic termination of all child processes when the supervisor job handle closes.
    ///     Default: true.
    /// </summary>
    public bool KillOnJobClose { get; set; } = true;

    /// <summary>
    ///     CPU core affinity allocation mode for dedicated game server worker instances.
    ///     Default: PCoresOnly.
    /// </summary>
    public CpuAffinityMode AffinityMode { get; set; } = CpuAffinityMode.PCoresOnly;

    /// <summary>
    ///     Optional bitmask specifying CPU core affinity for all spawned child processes.
    ///     When null, uses system default or CpuTopologyResolver.
    /// </summary>
    public nuint? CpuAffinityMask { get; set; }

    /// <summary>
    ///     Whether to inject the Kongor.NativeGuard.dll runtime supervision layer via CREATE_SUSPENDED.
    ///     Default: true.
    /// </summary>
    public bool EnableNativeGuardInjection { get; set; } = true;
}

/// <summary>
///     Governs how COMPEL assigns dedicated CPU core affinity masks to spawned worker instances.
/// </summary>
public enum CpuAffinityMode
{
    /// <summary>
    ///     Allocates dedicated workers exclusively to Performance Cores (P-Cores) on hybrid architectures,
    ///     preventing thread starvation on Efficiency Cores (E-Cores).
    /// </summary>
    PCoresOnly = 0,

    /// <summary>
    ///     Distributes dedicated workers across all logical processor cores (legacy distribution).
    /// </summary>
    AllCores = 1,

    /// <summary>
    ///     Pins each worker to a strictly non-overlapping physical performance core slice.
    /// </summary>
    DedicatedPerWorker = 2,

    /// <summary>
    ///     Enforces an explicit user-supplied affinity bitmask from configuration.
    /// </summary>
    ExplicitMask = 3
}
