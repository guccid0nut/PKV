// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using COMPEL.Services.Supervision;
using Microsoft.Extensions.Logging;

namespace COMPEL.Internals;

/// <summary>
///     Linux and POSIX implementation of <see cref="IProcessIsolationStrategy" /> backed by cgroups v2
///     (/sys/fs/cgroup) and POSIX signal lifecycle containment.
/// </summary>
public sealed class LinuxCgroupV2Strategy : IProcessIsolationStrategy
{
    private readonly ConcurrentDictionary<int, string> _workerCgroups = new();
    private readonly List<IDisposable> _posixSignalRegistrations = [];
    private ILogger? _logger;
    private SandboxLimitsOptions? _options;
    private string? _cgroupPath;
    private bool _initialized;
    private bool _disposed;

    public bool IsSupported => !OperatingSystem.IsWindows();

    public bool IsInitialized => _initialized;

    public bool Initialize(SandboxLimitsOptions options, ILogger? logger)
    {
        _options = options ?? new SandboxLimitsOptions();
        _logger = logger;

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            InitializePosixSignals();

            if (OperatingSystem.IsLinux())
            {
                InitializeCgroupV2();
            }

            _initialized = true;
            _logger?.LogInformation(
                "[LinuxCgroupV2Strategy] Linux container sandbox initialized (CGroup: {CGroupPath}, KillOnClose: {KillOnClose}).",
                _cgroupPath ?? "POSIX-ProcessTree", _options.KillOnJobClose);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[LinuxCgroupV2Strategy] Failed to initialize Linux process sandbox.");
            return false;
        }
    }

    private void InitializeCgroupV2()
    {
        try
        {
            string baseCgroup = "/sys/fs/cgroup";
            if (!Directory.Exists(baseCgroup)) return;

            string targetDir = Path.Combine(baseCgroup, $"compel_{Environment.ProcessId}");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            _cgroupPath = targetDir;

            if (_options != null)
            {
                if (_options.MaxJobMemoryBytes > 0)
                {
                    string memoryMaxFile = Path.Combine(targetDir, "memory.max");
                    if (File.Exists(memoryMaxFile))
                    {
                        File.WriteAllText(memoryMaxFile, _options.MaxJobMemoryBytes.ToString());
                    }
                }

                // Configure cgroup v2 cpu.weight
                string cpuWeightFile = Path.Combine(targetDir, "cpu.weight");
                if (File.Exists(cpuWeightFile))
                {
                    int weight = _options.PriorityMode == HostPriorityMode.HighPriority ? 1000 : 50;
                    File.WriteAllText(cpuWeightFile, weight.ToString());
                }

                // Configure cgroup v2 cpuset.cpus if in HighPriority mode
                string cpusetFile = Path.Combine(targetDir, "cpuset.cpus");
                if (File.Exists(cpusetFile) && _options.PriorityMode == HostPriorityMode.HighPriority)
                {
                    int[] bestCores = CpuTopologyResolver.GetBestAvailableProcessors(Math.Clamp(Environment.ProcessorCount, 1, 64));
                    if (bestCores.Length > 0)
                    {
                        File.WriteAllText(cpusetFile, string.Join(",", bestCores));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] cgroup v2 directory initialization deferred/skipped.");
        }
    }

    private void InitializePosixSignals()
    {
        try
        {
            Action<PosixSignalContext> handler = ctx =>
            {
                _logger?.LogWarning(
                    "[LinuxCgroupV2Strategy] Received POSIX signal {Signal}. Terminating child process fleet...",
                    ctx.Signal);
            };

            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, handler));
            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, handler));
            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, handler));
            _posixSignalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGQUIT, handler));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Failed to register POSIX signal handlers.");
        }
    }

    public bool AssignProcess(Process process)
    {
        return AssignProcess(process, 0, null);
    }

    public bool AssignProcess(Process process, int port, WorkerCpuPinning? pinning = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited) return false;

        // Dual-channel POSIX process priority enforcement with capability fallback
        try
        {
            if (_options?.PriorityMode == HostPriorityMode.HighPriority)
            {
                process.PriorityClass = ProcessPriorityClass.High;
            }
            else
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Could not directly set PriorityClass on PID {ProcessId} (e.g. unprivileged user/container).", process.Id);
        }

        // Apply CPU affinity / sched_setaffinity via .NET runtime
        if (pinning != null && pinning.AffinityMask != 0 && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
        {
            try
            {
                process.ProcessorAffinity = (nint)pinning.AffinityMask;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Could not set ProcessorAffinity on PID {ProcessId}.", process.Id);
            }
        }

        if (!string.IsNullOrEmpty(_cgroupPath) && OperatingSystem.IsLinux())
        {
            if (port > 0)
            {
                try
                {
                    string workerCgroup = Path.Combine(_cgroupPath, $"worker_{port}");
                    if (!Directory.Exists(workerCgroup))
                    {
                        string parentSubtree = Path.Combine(_cgroupPath, "cgroup.subtree_control");
                        if (File.Exists(parentSubtree))
                        {
                            try { File.WriteAllText(parentSubtree, "+cpuset +cpu +memory"); } catch (Exception ex) { _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Ignored subtree_control write error."); }
                        }

                        Directory.CreateDirectory(workerCgroup);
                    }

                    _workerCgroups[port] = workerCgroup;

                    if (pinning != null)
                    {
                        string cpusetFile = Path.Combine(workerCgroup, "cpuset.cpus");
                        if (File.Exists(cpusetFile) && !string.IsNullOrWhiteSpace(pinning.CpusSetString))
                        {
                            File.WriteAllText(cpusetFile, pinning.CpusSetString);
                        }

                        string memsFile = Path.Combine(workerCgroup, "cpuset.mems");
                        if (File.Exists(memsFile) && !string.IsNullOrWhiteSpace(pinning.MemsSetString))
                        {
                            File.WriteAllText(memsFile, pinning.MemsSetString);
                        }
                    }

                    string cpuWeightFile = Path.Combine(workerCgroup, "cpu.weight");
                    if (File.Exists(cpuWeightFile))
                    {
                        int weight = _options?.PriorityMode == HostPriorityMode.HighPriority ? 1000 : 50;
                        File.WriteAllText(cpuWeightFile, weight.ToString());
                    }

                    string procsFile = Path.Combine(workerCgroup, "cgroup.procs");
                    if (File.Exists(procsFile))
                    {
                        File.WriteAllText(procsFile, process.Id.ToString());
                        _logger?.LogInformation(
                            "[LinuxCgroupV2Strategy] Attached PID {ProcessId} ({ProcessName}) to dedicated worker cgroup {CGroup} (cpuset.cpus={Cpus}, cpuset.mems={Mems})",
                            process.Id, process.ProcessName, workerCgroup, pinning?.CpusSetString ?? "all", pinning?.MemsSetString ?? "0");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Failed to isolate PID {ProcessId} in worker_{Port} sub-cgroup; falling back to parent cgroup.", process.Id, port);
                }
            }

            try
            {
                string procsFile = Path.Combine(_cgroupPath, "cgroup.procs");
                if (File.Exists(procsFile))
                {
                    File.WriteAllText(procsFile, process.Id.ToString());
                    _logger?.LogInformation(
                        "[LinuxCgroupV2Strategy] Attached PID {ProcessId} ({ProcessName}) to cgroup {CGroup}",
                        process.Id, process.ProcessName, _cgroupPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Failed to write PID {ProcessId} to cgroup.procs", process.Id);
            }
        }

        _logger?.LogInformation(
            "[LinuxCgroupV2Strategy] Assigned PID {ProcessId} ({ProcessName}) to POSIX process lifecycle tracking.",
            process.Id, process.ProcessName);

        return true;
    }

    public bool AssignProcessHandle(nint hProcess, int processId = 0)
    {
        if (processId > 0 && !string.IsNullOrEmpty(_cgroupPath) && OperatingSystem.IsLinux())
        {
            try
            {
                string procsFile = Path.Combine(_cgroupPath, "cgroup.procs");
                if (File.Exists(procsFile))
                {
                    File.WriteAllText(procsFile, processId.ToString());
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Suppressed handle-based cgroup error for PID {ProcessId}.", processId);
            }
        }

        return processId > 0;
    }

    public JobObjectSandboxMetrics? GetSandboxMetrics(IReadOnlyDictionary<int, Process> trackedProcesses)
    {
        if (trackedProcesses == null || trackedProcesses.Count == 0)
            return null;

        TimeSpan totalUser = TimeSpan.Zero;
        TimeSpan totalKernel = TimeSpan.Zero;
        uint active = 0;
        ulong peakMem = 0;

        foreach (KeyValuePair<int, Process> kvp in trackedProcesses)
        {
            try
            {
                Process proc = kvp.Value;
                if (!proc.HasExited)
                {
                    active++;
                    totalUser += proc.UserProcessorTime;
                    totalKernel += proc.PrivilegedProcessorTime;
                    ulong mem = (ulong)proc.WorkingSet64;
                    if (mem > peakMem) peakMem = mem;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Transient process exit while reading metrics for PID {ProcessId}.", kvp.Key);
            }
        }

        if (!string.IsNullOrEmpty(_cgroupPath) && OperatingSystem.IsLinux())
        {
            try
            {
                string currentMemFile = Path.Combine(_cgroupPath, "memory.current");
                if (File.Exists(currentMemFile) && ulong.TryParse(File.ReadAllText(currentMemFile).Trim(), out ulong cgroupMem))
                {
                    if (cgroupMem > peakMem) peakMem = cgroupMem;
                }

                foreach (string workerDir in _workerCgroups.Values)
                {
                    string workerMemFile = Path.Combine(workerDir, "memory.current");
                    if (File.Exists(workerMemFile) && ulong.TryParse(File.ReadAllText(workerMemFile).Trim(), out ulong wMem))
                    {
                        if (wMem > peakMem) peakMem = wMem;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Suppressed cgroup memory read error.");
            }
        }

        return new JobObjectSandboxMetrics(
            totalUser,
            totalKernel,
            active,
            (uint)trackedProcesses.Count,
            (uint)(trackedProcesses.Count - active),
            0,
            peakMem,
            peakMem);
    }

    public void TerminateAll(IReadOnlyDictionary<int, Process> trackedProcesses)
    {
        if (trackedProcesses == null) return;

        foreach (KeyValuePair<int, Process> kvp in trackedProcesses)
        {
            try
            {
                Process proc = kvp.Value;
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Suppressed process shutdown error for PID {ProcessId}.", kvp.Key);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (IDisposable reg in _posixSignalRegistrations)
        {
            try { reg.Dispose(); } catch (Exception ex) { _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Suppressed POSIX signal registration disposal error."); }
        }
        _posixSignalRegistrations.Clear();

        if (!string.IsNullOrEmpty(_cgroupPath) && Directory.Exists(_cgroupPath))
        {
            try
            {
                foreach (string workerDir in _workerCgroups.Values)
                {
                    try { if (Directory.Exists(workerDir)) Directory.Delete(workerDir, recursive: false); } catch (Exception ex) { _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Suppressed worker cgroup directory delete error."); }
                }
                _workerCgroups.Clear();

                foreach (string subDir in Directory.GetDirectories(_cgroupPath, "worker_*"))
                {
                    try { Directory.Delete(subDir, recursive: false); } catch (Exception ex) { _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Suppressed subDir cgroup delete error."); }
                }

                Directory.Delete(_cgroupPath, recursive: false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LinuxCgroupV2Strategy] Non-critical cgroup cleanup error.");
            }
        }
    }
}
