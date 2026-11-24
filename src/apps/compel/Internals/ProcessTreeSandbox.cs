// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using COMPEL.Services.Supervision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace COMPEL.Internals;

/// <summary>
///     Strongly-typed real-time resource accounting telemetry for the process sandbox.
/// </summary>
public sealed record JobObjectSandboxMetrics(
    TimeSpan TotalUserTime,
    TimeSpan TotalKernelTime,
    uint ActiveProcesses,
    uint TotalProcesses,
    uint TotalTerminatedProcesses,
    uint TotalPageFaultCount,
    ulong PeakProcessMemoryBytes,
    ulong PeakJobMemoryBytes);

/// <summary>
///     Cross-platform process tree sandbox and resource supervisor.
///     Delegates to <see cref="IProcessIsolationStrategy" />:
///     - On Windows: Uses <see cref="Win32JobObjectStrategy" /> (Job Objects with hard commit memory caps,
///       AboveNormal priority, P-Core affinity, and kill-on-close guarantees).
///     - On Linux: Uses <see cref="LinuxCgroupV2Strategy" /> (cgroups v2 and POSIX signal containment).
/// </summary>
public sealed class ProcessTreeSandbox : IDisposable
{
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<int, Process> _trackedProcesses = new();
    private readonly IProcessIsolationStrategy _strategy;
    private bool _disposed;

    public ProcessTreeSandbox(ILogger<ProcessTreeSandbox>? logger = null)
        : this(new SandboxLimitsOptions(), logger)
    {
    }

    public ProcessTreeSandbox(IOptions<SandboxLimitsOptions>? options, ILogger<ProcessTreeSandbox>? logger = null)
        : this(options?.Value ?? new SandboxLimitsOptions(), logger)
    {
    }

    public ProcessTreeSandbox(SandboxLimitsOptions options, ILogger? logger = null)
        : this(options, CreateDefaultStrategy(), logger)
    {
    }

    public ProcessTreeSandbox(
        SandboxLimitsOptions options,
        IProcessIsolationStrategy strategy,
        ILogger? logger = null)
    {
        _logger = logger;
        Options = options ?? new SandboxLimitsOptions();
        _strategy = strategy ?? CreateDefaultStrategy();
        _strategy.Initialize(Options, _logger);
    }

    public SandboxLimitsOptions Options { get; }

    public bool IsInitialized => _strategy.IsInitialized;

    public IProcessIsolationStrategy ActiveStrategy => _strategy;

    public int TrackedProcessCount => _trackedProcesses.Count;

    private static IProcessIsolationStrategy CreateDefaultStrategy()
    {
        return OperatingSystem.IsWindows()
            ? new Win32JobObjectStrategy()
            : new LinuxCgroupV2Strategy();
    }

    /// <summary>
    ///     Assigns a spawned process to the sandbox strategy and process lifecycle tracker.
    /// </summary>
    public bool AssignProcess(Process process)
    {
        return AssignProcess(process, 0, null);
    }

    /// <summary>
    ///     Assigns a spawned process to the sandbox strategy and process lifecycle tracker,
    ///     with dedicated port-based sub-cgroups and NUMA core pinning.
    /// </summary>
    public bool AssignProcess(Process process, int port, WorkerCpuPinning? pinning = null)
    {
        if (process == null) return false;

        int pid;
        try
        {
            if (process.HasExited) return false;
            pid = process.Id;
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogDebug(ex, "[ProcessTreeSandbox] Cannot assign unassociated or disposed Process instance.");
            return false;
        }

        _trackedProcesses[pid] = process;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _trackedProcesses.TryRemove(pid, out _);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[ProcessTreeSandbox] Could not subscribe Exited handler for PID {ProcessId}.", pid);
        }

        try
        {
            if (process.HasExited)
            {
                _trackedProcesses.TryRemove(pid, out _);
                return false;
            }
        }
        catch (InvalidOperationException ex)
        {
            _trackedProcesses.TryRemove(pid, out _);
            _logger?.LogDebug(ex, "[ProcessTreeSandbox] Process PID {ProcessId} disposed during AssignProcess.", pid);
            return false;
        }

        return _strategy.AssignProcess(process, port, pinning);
    }

    /// <summary>
    ///     Explicitly removes a stopped, recycled, or disposed process ID from the sandbox lifecycle tracker.
    /// </summary>
    public bool UnassignProcess(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        bool removed = _trackedProcesses.TryRemove(processId, out _);
        if (removed)
        {
            _logger?.LogDebug("[ProcessTreeSandbox] Unassigned PID {ProcessId} from active sandbox tracking.", processId);
        }

        return removed;
    }

    /// <summary>
    ///     Assigns a raw process handle to the sandbox (Windows Job Object).
    /// </summary>
    public bool AssignProcessHandle(nint hProcess, int processId = 0)
    {
        return _strategy.AssignProcessHandle(hProcess, processId);
    }

    /// <summary>
    ///     Queries live resource accounting telemetry for all processes in the sandbox,
    ///     pruning any exited or disposed process entries first so dead PIDs never accumulate.
    /// </summary>
    public JobObjectSandboxMetrics? GetSandboxMetrics()
    {
        PruneExitedOrDisposedProcesses();
        return _strategy.GetSandboxMetrics(_trackedProcesses);
    }

    private void PruneExitedOrDisposedProcesses()
    {
        foreach (KeyValuePair<int, Process> kvp in _trackedProcesses)
        {
            bool shouldPrune = false;
            try
            {
                shouldPrune = kvp.Value == null || kvp.Value.HasExited;
            }
            catch (InvalidOperationException ex)
            {
                shouldPrune = true;
                _logger?.LogDebug(
                    ex,
                    "[ProcessTreeSandbox] Pruning disposed/unassociated Process entry for PID {ProcessId}.",
                    kvp.Key);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(
                    ex,
                    "[ProcessTreeSandbox] Non-fatal error probing HasExited for PID {ProcessId}.",
                    kvp.Key);
            }

            if (shouldPrune && _trackedProcesses.TryRemove(kvp.Key, out _))
            {
                _logger?.LogDebug("[ProcessTreeSandbox] Pruned exited/disposed PID {ProcessId} from sandbox tracker.", kvp.Key);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Options.KillOnJobClose)
        {
            _strategy.TerminateAll(_trackedProcesses);
        }

        _trackedProcesses.Clear();
        _strategy.Dispose();
    }
}
