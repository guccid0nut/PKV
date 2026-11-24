// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using COMPEL.Services.Supervision;
using Microsoft.Extensions.Logging;

namespace COMPEL.Internals;

/// <summary>
///     Pluggable operating-system abstraction strategy for process sandboxing, resource containment,
///     and process tree cleanup across Windows (Win32 Job Objects) and Linux (cgroups v2 &amp; POSIX signals).
/// </summary>
public interface IProcessIsolationStrategy : IDisposable
{
    /// <summary>
    ///     Indicates whether this strategy is supported and active on the current host operating system.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    ///     Indicates whether the underlying sandbox resource (Job Object, cgroup, or scope) has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    ///     Initializes the sandbox container with the specified resource limit options.
    /// </summary>
    bool Initialize(SandboxLimitsOptions options, ILogger? logger);

    /// <summary>
    ///     Assigns an active process to the sandbox.
    /// </summary>
    bool AssignProcess(Process process);

    /// <summary>
    ///     Assigns an active process to the sandbox with optional port and CPU/NUMA pinning.
    /// </summary>
    bool AssignProcess(Process process, int port, WorkerCpuPinning? pinning = null) => AssignProcess(process);

    /// <summary>
    ///     Assigns a native process handle to the sandbox (supported on Windows for suspended processes).
    /// </summary>
    bool AssignProcessHandle(nint hProcess, int processId = 0);

    /// <summary>
    ///     Queries real-time telemetry metrics for processes currently contained in the sandbox.
    /// </summary>
    JobObjectSandboxMetrics? GetSandboxMetrics(IReadOnlyDictionary<int, Process> trackedProcesses);

    /// <summary>
    ///     Terminates all processes assigned to the sandbox immediately.
    /// </summary>
    void TerminateAll(IReadOnlyDictionary<int, Process> trackedProcesses);
}
