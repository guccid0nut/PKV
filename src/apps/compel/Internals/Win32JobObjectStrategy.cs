// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using COMPEL.Services.Supervision;
using Microsoft.Extensions.Logging;

namespace COMPEL.Internals;

/// <summary>
///     Windows implementation of <see cref="IProcessIsolationStrategy" /> backed by the Win32 Job Object kernel API.
/// </summary>
public sealed class Win32JobObjectStrategy : IProcessIsolationStrategy
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;

    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitAffinity = 0x00000010;
    private const uint JobObjectLimitPriorityClass = 0x00000020;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private const uint NormalPriorityClass = 0x00000020;
    private const uint HighPriorityClass = 0x00000080;
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const uint AboveNormalPriorityClass = 0x00008000;

    private ILogger? _logger;
    private SandboxLimitsOptions? _options;
    private nint _jobHandle = nint.Zero;
    private bool _disposed;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsInitialized => _jobHandle != nint.Zero;

    public bool Initialize(SandboxLimitsOptions options, ILogger? logger)
    {
        _options = options ?? new SandboxLimitsOptions();
        _logger = logger;

        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogDebug("[Win32JobObjectStrategy] Non-Windows operating system; Win32 Job Objects not supported.");
            return false;
        }

        try
        {
            _jobHandle = CreateJobObject(nint.Zero, null);
            if (_jobHandle == nint.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                _logger?.LogWarning("[Win32JobObjectStrategy] CreateJobObject failed with error {ErrorCode}.", error);
                return false;
            }

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new();
            uint limitFlags = 0;

            if (_options.KillOnJobClose) limitFlags |= JobObjectLimitKillOnJobClose;

            if (_options.MaxProcessMemoryBytes > 0)
            {
                limitFlags |= JobObjectLimitProcessMemory;
                info.ProcessMemoryLimit = (nuint)_options.MaxProcessMemoryBytes;
            }

            if (_options.MaxJobMemoryBytes > 0)
            {
                limitFlags |= JobObjectLimitJobMemory;
                info.JobMemoryLimit = (nuint)_options.MaxJobMemoryBytes;
            }

            if (_options.MaxActiveProcesses > 0)
            {
                limitFlags |= JobObjectLimitActiveProcess;
                info.BasicLimitInformation.ActiveProcessLimit = _options.MaxActiveProcesses;
            }

            uint jobPriorityClass = _options.PriorityMode switch
            {
                HostPriorityMode.Background => BelowNormalPriorityClass,
                HostPriorityMode.HighPriority => HighPriorityClass,
                HostPriorityMode.Adaptive => BelowNormalPriorityClass,
                _ => _options.EnforceAboveNormalPriority ? AboveNormalPriorityClass : NormalPriorityClass
            };

            limitFlags |= JobObjectLimitPriorityClass;
            info.BasicLimitInformation.PriorityClass = jobPriorityClass;

            nuint? effectiveJobAffinity = _options.CpuAffinityMask;
            if ((!effectiveJobAffinity.HasValue || effectiveJobAffinity.Value == 0) &&
                _options.AffinityMode == CpuAffinityMode.PCoresOnly)
            {
                nint pcoreMask = _options.PriorityMode == HostPriorityMode.HighPriority
                    ? CpuTopologyResolver.GetAggregateBestProcessorsMask()
                    : CpuTopologyResolver.GetAggregatePcoreMask();

                if (pcoreMask != 0)
                {
                    effectiveJobAffinity = (nuint)pcoreMask;
                }
            }

            if (effectiveJobAffinity.HasValue && effectiveJobAffinity.Value != 0)
            {
                limitFlags |= JobObjectLimitAffinity;
                info.BasicLimitInformation.Affinity = effectiveJobAffinity.Value;
            }

            info.BasicLimitInformation.LimitFlags = limitFlags;

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            bool success = SetInformationJobObject(
                _jobHandle,
                JobObjectExtendedLimitInformation,
                ref info,
                (uint)length);

            if (!success && (limitFlags & JobObjectLimitPriorityClass) != 0 && jobPriorityClass == HighPriorityClass)
            {
                // HighPriorityClass in Win32 Job Object requires SE_INC_BASE_PRIORITY_NAME; fallback to AboveNormalPriorityClass on envelope
                _logger?.LogDebug("[Win32JobObjectStrategy] HighPriorityClass in Job Object required SE_INC_BASE_PRIORITY_NAME; falling back to AboveNormalPriorityClass on Job envelope.");
                info.BasicLimitInformation.PriorityClass = AboveNormalPriorityClass;
                success = SetInformationJobObject(
                    _jobHandle,
                    JobObjectExtendedLimitInformation,
                    ref info,
                    (uint)length);
            }

            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                _logger?.LogWarning("[Win32JobObjectStrategy] SetInformationJobObject failed with error {ErrorCode}.", error);
                return false;
            }

            _logger?.LogInformation(
                "[Win32JobObjectStrategy] Win32 Job Object initialized (KillOnClose: {KillOnClose}, MaxProcMem: {ProcMemMb}MB, MaxJobMem: {JobMemMb}MB, MaxProc: {MaxProc}, PriorityMode: {PriorityMode}).",
                _options.KillOnJobClose,
                _options.MaxProcessMemoryBytes / (1024 * 1024),
                _options.MaxJobMemoryBytes / (1024 * 1024),
                _options.MaxActiveProcesses,
                _options.PriorityMode);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Win32JobObjectStrategy] Failed to initialize Windows Job Object.");
            return false;
        }
    }

    public bool AssignProcess(Process process)
    {
        return AssignProcess(process, 0, null);
    }

    public bool AssignProcess(Process process, int port, WorkerCpuPinning? pinning = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited || _jobHandle == nint.Zero) return false;

        if (pinning != null && OperatingSystem.IsWindows() && pinning.AffinityMask != 0)
        {
            try
            {
                process.ProcessorAffinity = (nint)pinning.AffinityMask;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Win32JobObjectStrategy] Could not set ProcessorAffinity on PID {Pid}", process.Id);
            }
        }

        return AssignProcessHandle(process.Handle, process.Id);
    }

    public bool AssignProcessHandle(nint hProcess, int processId = 0)
    {
        if (_disposed || hProcess == nint.Zero || !OperatingSystem.IsWindows() || _jobHandle == nint.Zero)
            return false;

        try
        {
            bool assigned = AssignProcessToJobObject(_jobHandle, hProcess);
            if (assigned)
            {
                _logger?.LogInformation(
                    "[Win32JobObjectStrategy] Assigned process handle 0x{Handle:X} (PID {ProcessId}) to Sandbox Job Object.",
                    hProcess, processId);
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            _logger?.LogWarning(
                "[Win32JobObjectStrategy] AssignProcessToJobObject failed for handle 0x{Handle:X} (PID {ProcessId}) with error {ErrorCode}.",
                hProcess, processId, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "[Win32JobObjectStrategy] Exception while assigning process handle 0x{Handle:X} to Job Object.", hProcess);
            return false;
        }
    }

    public JobObjectSandboxMetrics? GetSandboxMetrics(IReadOnlyDictionary<int, Process> trackedProcesses)
    {
        if (!OperatingSystem.IsWindows() || _jobHandle == nint.Zero)
            return null;

        try
        {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting = new();
            int accountingSize = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
            nint pAccounting = Marshal.AllocHGlobal(accountingSize);

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION extended = new();
            int extendedSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            nint pExtended = Marshal.AllocHGlobal(extendedSize);

            try
            {
                bool accSuccess = QueryInformationJobObject(
                    _jobHandle,
                    JobObjectBasicAccountingInformation,
                    pAccounting,
                    (uint)accountingSize,
                    nint.Zero);

                if (accSuccess)
                    accounting = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(pAccounting);

                bool extSuccess = QueryInformationJobObject(
                    _jobHandle,
                    JobObjectExtendedLimitInformation,
                    pExtended,
                    (uint)extendedSize,
                    nint.Zero);

                if (extSuccess)
                    extended = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(pExtended);

                if (accSuccess || extSuccess)
                    return new JobObjectSandboxMetrics(
                        TimeSpan.FromTicks(accounting.TotalUserTime),
                        TimeSpan.FromTicks(accounting.TotalKernelTime),
                        accounting.ActiveProcesses,
                        accounting.TotalProcesses,
                        accounting.TotalTerminatedProcesses,
                        accounting.TotalPageFaultCount,
                        extended.PeakProcessMemoryLimit,
                        extended.PeakJobMemoryLimit);
            }
            finally
            {
                Marshal.FreeHGlobal(pAccounting);
                Marshal.FreeHGlobal(pExtended);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Win32JobObjectStrategy] Failed to query Job Object telemetry.");
        }

        return null;
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
                _logger?.LogWarning(
                    ex,
                    "[Win32JobObjectStrategy] Failed to terminate tracked process key {Key}: {ExceptionType}: {Message}",
                    kvp.Key,
                    ex.GetType().Name,
                    ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_jobHandle != nint.Zero && OperatingSystem.IsWindows())
        {
            try
            {
                if (!CloseHandle(_jobHandle))
                {
                    int win32Err = Marshal.GetLastWin32Error();
                    _logger?.LogWarning("[Win32JobObjectStrategy] CloseHandle on Job Object returned false (Win32Error={ErrorCode}).", win32Err);
                }
                _jobHandle = nint.Zero;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Win32JobObjectStrategy] Exception closing Job Object handle during Dispose.");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        int JobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        nint hJob,
        int JobObjectInformationClass,
        nint lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        nint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

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
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryLimit;
        public nuint PeakJobMemoryLimit;
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
}
