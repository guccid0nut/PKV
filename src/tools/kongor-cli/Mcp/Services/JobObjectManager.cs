// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Win32 Job Object manager ensuring that any ephemeral or JIT-spawned child processes
/// (such as offscreen hon_x64 client or dedicated server workers) are bound to a kernel Job Object
/// configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_HANDLE_CLOSE.
/// When the MCP server process exits or is terminated, Windows kernel automatically terminates
/// all child processes in the job, preventing zombie or orphaned processes.
/// </summary>
public sealed record JobObjectTelemetry(
    uint ActiveProcesses,
    uint TotalProcessesSpawned,
    uint TotalTerminatedProcesses,
    ulong PeakProcessMemoryBytes,
    ulong PeakJobMemoryBytes,
    ulong ConfiguredJobMemoryLimitBytes,
    bool MemoryLimitReached = false);

public static class JobObjectManager
{
    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_HANDLE_CLOSE = 0x00002000;

    public const ulong DefaultJobMemoryLimitBytes = 8192UL * 1024UL * 1024UL; // 8 GiB default cap

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion

    private static readonly Lock s_lock = new();
    private static IntPtr s_jobHandle = IntPtr.Zero;
    private static bool s_isInitialized;
    private static ulong s_configuredJobMemoryLimitBytes = ResolveConfiguredJobMemoryLimitBytes();
    private static readonly ConcurrentDictionary<int, DateTimeOffset> s_trackedProcesses = new();

    public static bool Enabled { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("KONGOR_DISABLE_JOB_OBJECT"), "1", StringComparison.OrdinalIgnoreCase);

    public static ulong ConfiguredJobMemoryLimitBytes
    {
        get => s_configuredJobMemoryLimitBytes;
        set => s_configuredJobMemoryLimitBytes = value;
    }

    public static ulong ResolveConfiguredJobMemoryLimitBytes(string? envOverrideMb = null)
    {
        string? rawMb = envOverrideMb ?? Environment.GetEnvironmentVariable("KONGOR_JOB_MEMORY_LIMIT_MB");
        if (!string.IsNullOrWhiteSpace(rawMb) && ulong.TryParse(rawMb.Trim(), out ulong parsedMb))
        {
            return parsedMb * 1024UL * 1024UL;
        }
        return DefaultJobMemoryLimitBytes;
    }

    /// <summary>
    /// Assigns a running process to the global Job Object so it is killed on host exit.
    /// </summary>
    public static bool AssignProcess(Process process, Action<string>? logger = null)
    {
        if (!Enabled) return false;
        if (process == null) return false;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        EnsureJobObjectInitialized(logger);

        if (s_jobHandle == IntPtr.Zero) return false;

        try
        {
            if (process.HasExited) return false;

            bool assigned = AssignProcessToJobObject(s_jobHandle, process.Handle);
            if (assigned)
            {
                s_trackedProcesses[process.Id] = TimeProvider.System.GetUtcNow();
            }
            else
            {
                int win32Err = Marshal.GetLastWin32Error();
                logger?.Invoke($"[JobObjectManager] [WARN] AssignProcessToJobObject returned false for PID {process.Id} (Win32Error={win32Err}).");
            }
            return assigned;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[JobObjectManager] [WARN] AssignProcess exception for PID {process.Id}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Queries live Win32 Job Object accounting and peak memory counters (#172).
    /// </summary>
    public static JobObjectTelemetry QueryTelemetry(Action<string>? logger = null)
    {
        if (!Enabled || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new JobObjectTelemetry(0, 0, 0, 0, 0, s_configuredJobMemoryLimitBytes, false);
        }

        EnsureJobObjectInitialized(logger);
        if (s_jobHandle == IntPtr.Zero)
        {
            return new JobObjectTelemetry(0, 0, 0, 0, 0, s_configuredJobMemoryLimitBytes, false);
        }

        int accSize = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        IntPtr accPtr = Marshal.AllocHGlobal(accSize);
        int extSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr extPtr = Marshal.AllocHGlobal(extSize);

        try
        {
            uint activeProcs = 0;
            uint totalProcs = 0;
            uint terminatedProcs = 0;
            ulong peakProcMem = 0;
            ulong peakJobMem = 0;
            ulong configuredLimit = s_configuredJobMemoryLimitBytes;

            if (QueryInformationJobObject(s_jobHandle, JobObjectBasicAccountingInformation, accPtr, (uint)accSize, IntPtr.Zero))
            {
                JOBOBJECT_BASIC_ACCOUNTING_INFORMATION acc = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(accPtr);
                activeProcs = acc.ActiveProcesses;
                totalProcs = acc.TotalProcesses;
                terminatedProcs = acc.TotalTerminatedProcesses;
            }

            if (QueryInformationJobObject(s_jobHandle, JobObjectExtendedLimitInformation, extPtr, (uint)extSize, IntPtr.Zero))
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION ext = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(extPtr);
                peakProcMem = ext.PeakProcessMemoryLimit.ToUInt64();
                peakJobMem = ext.PeakJobMemoryLimit.ToUInt64();
                if (ext.JobMemoryLimit.ToUInt64() > 0)
                {
                    configuredLimit = ext.JobMemoryLimit.ToUInt64();
                }
            }

            bool limitReached = configuredLimit > 0 && peakJobMem >= configuredLimit;

            return new JobObjectTelemetry(
                ActiveProcesses: activeProcs,
                TotalProcessesSpawned: totalProcs,
                TotalTerminatedProcesses: terminatedProcs,
                PeakProcessMemoryBytes: peakProcMem,
                PeakJobMemoryBytes: peakJobMem,
                ConfiguredJobMemoryLimitBytes: configuredLimit,
                MemoryLimitReached: limitReached);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[JobObjectManager] [WARN] QueryTelemetry failed: {ex.GetType().Name}: {ex.Message}");
            return new JobObjectTelemetry(0, 0, 0, 0, 0, s_configuredJobMemoryLimitBytes, false);
        }
        finally
        {
            Marshal.FreeHGlobal(accPtr);
            Marshal.FreeHGlobal(extPtr);
        }
    }

    /// <summary>
    /// Records activity for an ephemeral process to delay idle reaping.
    /// </summary>
    public static void RecordActivity(int processId, TimeProvider? timeProvider = null)
    {
        s_trackedProcesses[processId] = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// Reaps any tracked ephemeral processes that have been idle for longer than the specified threshold.
    /// </summary>
    public static int ReapIdleProcesses(TimeSpan idleThreshold, TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        int reapedCount = 0;
        DateTimeOffset cutoff = (timeProvider ?? TimeProvider.System).GetUtcNow() - idleThreshold;

        foreach (KeyValuePair<int, DateTimeOffset> kvp in s_trackedProcesses)
        {
            int pid = kvp.Key;
            DateTimeOffset lastActive = kvp.Value;

            if (lastActive < cutoff)
            {
                try
                {
                    using Process proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill(true);
                        logger?.Invoke($"[JobObjectManager] Reaped idle ephemeral process PID {pid} (inactive since {lastActive:u}).");
                        reapedCount++;
                    }
                }
                catch (ArgumentException ex)
                {
                    logger?.Invoke($"[JobObjectManager] Idle process PID {pid} already exited before reap: {ex.Message}");
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[JobObjectManager] [WARN] Failed to reap idle ephemeral process PID {pid}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    s_trackedProcesses.TryRemove(pid, out _);
                }
            }
        }

        return reapedCount;
    }

    private static void EnsureJobObjectInitialized(Action<string>? logger = null)
    {
        if (s_isInitialized) return;

        lock (s_lock)
        {
            if (s_isInitialized) return;
            s_isInitialized = true;

            try
            {
                string jobName = $"Kongor_Mcp_Job_{Environment.ProcessId}";
                s_jobHandle = CreateJobObject(IntPtr.Zero, jobName);
                if (s_jobHandle == IntPtr.Zero)
                {
                    s_jobHandle = CreateJobObject(IntPtr.Zero, null);
                }

                if (s_jobHandle != IntPtr.Zero)
                {
                    uint flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_HANDLE_CLOSE;
                    UIntPtr jobMemLimit = UIntPtr.Zero;
                    if (s_configuredJobMemoryLimitBytes > 0)
                    {
                        flags |= JOB_OBJECT_LIMIT_JOB_MEMORY;
                        jobMemLimit = new UIntPtr(s_configuredJobMemoryLimitBytes);
                    }

                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                        {
                            LimitFlags = flags
                        },
                        JobMemoryLimit = jobMemLimit
                    };

                    int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
                    try
                    {
                        Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                        bool ok = SetInformationJobObject(s_jobHandle, JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length);
                        if (!ok && (flags & JOB_OBJECT_LIMIT_JOB_MEMORY) != 0)
                        {
                            int err = Marshal.GetLastWin32Error();
                            logger?.Invoke($"[JobObjectManager] [WARN] SetInformationJobObject with JOB_OBJECT_LIMIT_JOB_MEMORY failed (Win32Error={err}); retrying with KillOnClose only.");
                            extendedInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_HANDLE_CLOSE;
                            extendedInfo.JobMemoryLimit = UIntPtr.Zero;
                            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                            SetInformationJobObject(s_jobHandle, JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(extendedInfoPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[JobObjectManager] [WARN] Failed to initialize JobObject: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
