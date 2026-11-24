// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WindowsJobObjectSandbox.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.ProcessLifecycle;

/// <summary>
///     Encapsulates a Win32 Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
///     Guarantees that when the C# orchestrator terminates or the Job Object is disposed,
///     all associated child processes (hon_x64.exe, ffmpeg.exe, helper tools) are forcefully
///     terminated by the Windows NT kernel, preventing orphaned or zombie background processes.
/// </summary>
public sealed class WindowsJobObjectSandbox : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly ILogger<WindowsJobObjectSandbox> _logger;
    private bool _disposed;
    private nint _jobHandle = nint.Zero;

    public WindowsJobObjectSandbox(ILogger<WindowsJobObjectSandbox> logger)
    {
        _logger = logger;
        InitializeJobObject();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        if (_jobHandle != nint.Zero && OperatingSystem.IsWindows())
        {
            _logger.LogInformation(
                "[JobSandbox] Closing Win32 Job Object handle (0x{Handle:X}). Any active child processes will be terminated by OS kernel.",
                _jobHandle);
            CloseHandle(_jobHandle);
            _jobHandle = nint.Zero;
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
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    private void InitializeJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                "[JobSandbox] Non-Windows operating system detected. Win32 Job Object sandbox is bypassed.");
            return;
        }

        _jobHandle = CreateJobObject(nint.Zero, null);
        if (_jobHandle == nint.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            _logger.LogError("[JobSandbox] CreateJobObject failed with Win32 error code {ErrorCode}.", errorCode);
            throw new InvalidOperationException($"CreateJobObject failed with Win32 error {errorCode}");
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION extendedInfo = new();
        extendedInfo.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        int structSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        bool resultSet = SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ref extendedInfo,
            (uint)structSize);

        if (!resultSet)
        {
            int errorCode = Marshal.GetLastWin32Error();
            _logger.LogError("[JobSandbox] SetInformationJobObject failed with Win32 error code {ErrorCode}.",
                errorCode);
            CloseHandle(_jobHandle);
            _jobHandle = nint.Zero;
            throw new InvalidOperationException($"SetInformationJobObject failed with Win32 error {errorCode}");
        }

        _logger.LogInformation(
            "[JobSandbox] Win32 Job Object created successfully (Handle: 0x{Handle:X}) with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.",
            _jobHandle);
    }

    /// <summary>
    ///     Assigns a managed Process instance to the Job Object sandbox.
    /// </summary>
    public bool AssignProcess(Process? process)
    {
        if (process == null)
        {
            _logger.LogWarning("[JobSandbox] Cannot assign null process to Job Object.");
            return false;
        }

        if (process.HasExited)
        {
            _logger.LogWarning("[JobSandbox] Cannot assign exited process (PID: {PID}) to Job Object.", process.Id);
            return false;
        }

        if (!OperatingSystem.IsWindows() || _jobHandle == nint.Zero)
        {
            _logger.LogDebug("[JobSandbox] Bypass process assignment on non-Windows environment for PID {PID}.",
                process.Id);
            return true;
        }

        try
        {
            bool success = AssignProcessToJobObject(_jobHandle, process.Handle);
            if (!success)
            {
                int errorCode = Marshal.GetLastWin32Error();
                _logger.LogError(
                    "[JobSandbox] AssignProcessToJobObject failed for PID {PID} ({Name}) with Win32 error {ErrorCode}.",
                    process.Id, process.ProcessName, errorCode);
                return false;
            }

            _logger.LogInformation("[JobSandbox] Successfully sandboxed PID {PID} ({Name}) into Job Object.",
                process.Id, process.ProcessName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JobSandbox] Exception occurred while assigning PID {PID} to Job Object: {Message}",
                process.Id, ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Assigns a native process handle to the Job Object sandbox.
    /// </summary>
    public bool AssignProcess(nint processHandle)
    {
        if (processHandle == nint.Zero) return false;

        if (!OperatingSystem.IsWindows() || _jobHandle == nint.Zero) return true;

        bool success = AssignProcessToJobObject(_jobHandle, processHandle);
        if (!success)
        {
            int errorCode = Marshal.GetLastWin32Error();
            _logger.LogError(
                "[JobSandbox] AssignProcessToJobObject failed for raw handle 0x{Handle:X} with error {ErrorCode}.",
                processHandle, errorCode);
            return false;
        }

        _logger.LogInformation("[JobSandbox] Successfully assigned process handle 0x{Handle:X} to Job Object.",
            processHandle);
        return true;
    }

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
}
