# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Two-stage graceful process termination (3.0s minidump flush -> tree kill) and Win32
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000) child-process containment (#117).

.DESCRIPTION
    Implements Phase 4 item #117 (graceful-kill-and-job-object):
    - Register-KongorProcessInJobObject: Creates or reuses a Win32 Job Object with
      JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000) and assigns the target process handle
      so that unexpected parent termination never leaves orphan hon_x64.exe / hon_server.exe
      processes holding file locks on .s2z archives.
    - Stop-KongorProcessGracefully: Executes Stage 1 graceful close (CloseMainWindow / WM_CLOSE)
      and waits up to GracePeriodMs (default 3000ms) for logs/minidumps to flush, followed by
      Stage 2 hard process-tree termination if the process has not exited.
#>

if (-not ("KongorWin32JobObject" -as [type])) {
    $jobCsharp = @"
using System;
using System.Runtime.InteropServices;

public static class KongorWin32JobObject
{
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoType,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private static IntPtr _sharedJobHandle = IntPtr.Zero;

    public static IntPtr EnsureKillOnCloseJob()
    {
        if (_sharedJobHandle != IntPtr.Zero)
        {
            return _sharedJobHandle;
        }

        IntPtr hJob = CreateJobObjectW(IntPtr.Zero, null);
        if (hJob == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        uint length = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));

        if (!SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ref info, length))
        {
            return IntPtr.Zero;
        }

        _sharedJobHandle = hJob;
        return _sharedJobHandle;
    }

    public static bool AttachProcess(IntPtr processHandle)
    {
        IntPtr hJob = EnsureKillOnCloseJob();
        if (hJob == IntPtr.Zero || processHandle == IntPtr.Zero)
        {
            return false;
        }
        return AssignProcessToJobObject(hJob, processHandle);
    }
}
"@
    Add-Type -TypeDefinition $jobCsharp -Language CSharp -ErrorAction Stop
}

function Register-KongorProcessInJobObject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId
    )

    $proc = [System.Diagnostics.Process]::GetProcessById($ProcessId)
    if ($proc.HasExited) {
        return [PSCustomObject]@{
            Attached   = $false
            ProcessId  = $ProcessId
            LimitFlags = 0
            Reason     = "Process already exited"
        }
    }

    $attached = [KongorWin32JobObject]::AttachProcess($proc.Handle)
    return [PSCustomObject]@{
        Attached   = $attached
        ProcessId  = $ProcessId
        LimitFlags = [KongorWin32JobObject]::JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        Reason     = if ($attached) { "Assigned to JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000)" } else { "AssignProcessToJobObject returned false (process may already belong to an incompatible job)" }
    }
}

function Stop-KongorProcessGracefully {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId,

        [Parameter(Mandatory = $false)]
        [int]$GracePeriodMs = 3000,

        [Parameter(Mandatory = $false)]
        [switch]$Quiet
    )

    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::GetProcessById($ProcessId)
    } catch {
        return [PSCustomObject]@{
            ProcessId      = $ProcessId
            StageReached   = "AlreadyExited"
            GracefulExited = $true
            ForceKilled    = $false
            ElapsedMs      = 0
        }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($proc.HasExited) {
        return [PSCustomObject]@{
            ProcessId      = $ProcessId
            StageReached   = "AlreadyExited"
            GracefulExited = $true
            ForceKilled    = $false
            ElapsedMs      = 0
        }
    }

    # Stage 1: Request graceful close so minidumps and logs flush cleanly (#117)
    if (-not $Quiet) {
        Write-Host "[GRACEFUL-KILL] Stage 1: Requesting graceful shutdown for PID $ProcessId (GracePeriod=${GracePeriodMs}ms)..." -ForegroundColor Cyan
    }
    try {
        [void]$proc.CloseMainWindow()
    } catch {
        Write-Verbose "CloseMainWindow not supported for headless PID ${ProcessId}: $($_.Exception.Message)"
    }

    $exitedInStage1 = $proc.WaitForExit($GracePeriodMs)
    if ($exitedInStage1) {
        $sw.Stop()
        if (-not $Quiet) {
            Write-Host "[GRACEFUL-KILL] Stage 1 succeeded: PID $ProcessId exited cleanly in $($sw.ElapsedMilliseconds)ms." -ForegroundColor Green
        }
        return [PSCustomObject]@{
            ProcessId      = $ProcessId
            StageReached   = "Stage1Graceful"
            GracefulExited = $true
            ForceKilled    = $false
            ElapsedMs      = $sw.ElapsedMilliseconds
        }
    }

    # Stage 2: Escalate to hard process-tree termination so no zombie handles remain on .s2z archives
    if (-not $Quiet) {
        Write-Host "[GRACEFUL-KILL] Stage 2: Grace period (${GracePeriodMs}ms) expired for PID $ProcessId; escalating to hard tree kill..." -ForegroundColor Yellow
    }

    $childProcs = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $childProcs) {
        try {
            Stop-Process -Id ([int]$child.ProcessId) -Force -ErrorAction Stop
        } catch {
            if (-not (Get-Process -Id ([int]$child.ProcessId) -ErrorAction SilentlyContinue)) {
                Write-Verbose "Child PID $($child.ProcessId) already exited."
            } else {
                throw "Failed to terminate child PID $($child.ProcessId) of parent PID ${ProcessId}: $($_.Exception.Message)"
            }
        }
    }

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
    } catch {
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
            throw "Failed to terminate PID $ProcessId in Stage 2: $($_.Exception.Message)"
        }
    }

    [void]$proc.WaitForExit(2000)
    $sw.Stop()
    if (-not $Quiet) {
        Write-Host "[GRACEFUL-KILL] Stage 2 completed: PID $ProcessId terminated in $($sw.ElapsedMilliseconds)ms." -ForegroundColor Green
    }

    return [PSCustomObject]@{
        ProcessId      = $ProcessId
        StageReached   = "Stage2TreeKill"
        GracefulExited = $false
        ForceKilled    = $true
        ElapsedMs      = $sw.ElapsedMilliseconds
    }
}
