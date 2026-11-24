# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Enforces the Project Kongor singleton worker & test-runner filesystem mutexes.

.DESCRIPTION
    Manages machine-wide lock files ($env:TEMP\kongor_game_worker.lock for live game/override work,
    and $env:TEMP\kongor_test_runner.lock for serialized dotnet build/test, pytest, and cmake runs)
    so concurrent agents never collide on hon_x64.exe, \\.\pipe\kongor-native-client,
    games/client/override/, or MSBuild obj/bin file locks.
    Automatically reaps stale locks whose owning PID has terminated or whose lease TTL expired.

.EXAMPLE
    .\scripts\kongor_worker_lock.ps1 -Action acquire -Owner "game-worker-main"
    .\scripts\kongor_worker_lock.ps1 -Scope test-runner -Action acquire -Owner "flash-b1" -WaitSeconds 240
    .\scripts\kongor_worker_lock.ps1 -Scope test-runner -Action status
    .\scripts\kongor_worker_lock.ps1 -Scope test-runner -Action release -Owner "flash-b1"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("acquire", "release", "status", "assert", "check-roe", "record-call", "heartbeat", "register-ctrl-handler")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [ValidateSet("game-worker", "test-runner")]
    [string]$Scope = "game-worker",

    [Parameter(Mandatory = $false)]
    [string]$Owner = "",

    [Parameter(Mandatory = $false)]
    [int]$PidToTrack = 0,

    [Parameter(Mandatory = $false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory = $false)]
    [int]$LeaseTtlSeconds = 0,

    [Parameter(Mandatory = $false)]
    [string]$CommandSummary = "",

    [Parameter(Mandatory = $false)]
    [string]$LockFilePath = "",

    [Parameter(Mandatory = $false)]
    [string]$ToolName = "",

    [Parameter(Mandatory = $false)]
    [string]$ArgsSha256 = "",

    [Parameter(Mandatory = $false)]
    [string]$ErrorCode = "",

    [Parameter(Mandatory = $false)]
    [string]$TargetPath = "",

    [Parameter(Mandatory = $false)]
    [int]$ProbeCount = 0,

    [Parameter(Mandatory = $false)]
    [int]$MaxProbes = 10,

    [Parameter(Mandatory = $false)]
    [double]$ElapsedSeconds = 0.0,

    [Parameter(Mandatory = $false)]
    [double]$DeadlineSeconds = 300.0,

    [Parameter(Mandatory = $false)]
    [string]$StateFilePath = "",

    [Parameter(Mandatory = $false)]
    [string]$EventStreamPath = "",

    [Parameter(Mandatory = $false)]
    [string]$SteerQueuePath = "",

    [Parameter(Mandatory = $false)]
    [string]$HandoffDir = "",

    [switch]$ZeroDelta,
    [switch]$RequireLock,
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($LockFilePath)) {
    if ($Scope -eq "test-runner") {
        $LockFilePath = Join-Path $env:TEMP "kongor_test_runner.lock"
    } else {
        $LockFilePath = Join-Path $env:TEMP "kongor_game_worker.lock"
    }
}
if ($LeaseTtlSeconds -le 0 -and $Scope -eq "test-runner") {
    $LeaseTtlSeconds = 600
}
if ([string]::IsNullOrWhiteSpace($StateFilePath)) {
    $StateFilePath = "$LockFilePath.calls.json"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($EventStreamPath)) {
    $EventStreamPath = Join-Path $repoRoot "logs\worker_events.jsonl"
}
if ([string]::IsNullOrWhiteSpace($SteerQueuePath)) {
    $SteerQueuePath = Join-Path $repoRoot "logs\worker_steer.jsonl"
}
if ([string]::IsNullOrWhiteSpace($HandoffDir)) {
    $HandoffDir = Join-Path $repoRoot "logs"
}

if ([string]::IsNullOrWhiteSpace($Owner) -and -not [string]::IsNullOrWhiteSpace($env:KONGOR_WORKER_OWNER)) {
    $Owner = $env:KONGOR_WORKER_OWNER
}

function Write-LockLog {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Cyan
    )
    if (-not $Quiet) {
        $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
        Write-Host "[$ts] [WORKER-LOCK] $Message" -ForegroundColor $Color
    }
}

function Get-ProcessCreationTicks {
    param([int]$ProcessId)
    if ($ProcessId -le 0) { return 0L }
    try {
        $p = [System.Diagnostics.Process]::GetProcessById($ProcessId)
        if ($p.HasExited) { return 0L }
        return [long]$p.StartTime.ToUniversalTime().Ticks
    } catch {
        return 0L
    }
}

function Test-PidAlive {
    param([int]$ProcessId, [long]$ExpectedCreationTicks)
    if ($ProcessId -le 0) { return $false }
    try {
        $p = [System.Diagnostics.Process]::GetProcessById($ProcessId)
        if ($p.HasExited) { return $false }
        if ($ExpectedCreationTicks -gt 1L) {
            $actualTicks = [long]$p.StartTime.ToUniversalTime().Ticks
            if ([Math]::Abs($actualTicks - $ExpectedCreationTicks) -gt 10000000L) {
                return $false # PID reused by a newer process (> 1s difference)
            }
        }
        return $true
    } catch {
        return $false
    }
}

function Get-ActiveLockState {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($raw)) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            return $null
        }
        $lockObj = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-LockLog "Corrupt lock file detected at '$Path' ($($_.Exception.Message)); reaping stale file." Yellow
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        return $null
    }

    $lockScopeLabel = if ($null -ne $lockObj.scope -and -not [string]::IsNullOrWhiteSpace([string]$lockObj.scope)) { [string]$lockObj.scope } else { $Scope }

    if ($null -ne $lockObj.pid -and [int]$lockObj.pid -gt 0) {
        $expectedTicks = if ($null -ne $lockObj.processCreationTicks) { [long]$lockObj.processCreationTicks } else { 0L }
        if (-not (Test-PidAlive -ProcessId ([int]$lockObj.pid) -ExpectedCreationTicks $expectedTicks)) {
            Write-LockLog "Reaping stale $lockScopeLabel lock owned by '$($lockObj.owner)' (dead/reused PID $($lockObj.pid), acquired $($lockObj.acquiredAt))." Yellow
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            return $null
        }
    }

    if ($null -ne $lockObj.leaseTtlSeconds -and [int]$lockObj.leaseTtlSeconds -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$lockObj.acquiredAt)) {
        $acquiredDto = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse([string]$lockObj.acquiredAt, [ref]$acquiredDto)) {
            $ageSec = ([DateTimeOffset]::UtcNow - $acquiredDto.ToUniversalTime()).TotalSeconds
            if ($ageSec -gt [double]$lockObj.leaseTtlSeconds) {
                Write-LockLog "Reaping expired $lockScopeLabel lock owned by '$($lockObj.owner)' (PID $($lockObj.pid), age=$([Math]::Round($ageSec, 1))s > ttl=$($lockObj.leaseTtlSeconds)s)." Yellow
                Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
                return $null
            }
        }
    }

    return $lockObj
}

function Register-KongorConsoleCtrlHandler {
    param([string]$TargetLockPath)
    if (-not ("KongorNativeConsoleCtrlHandler" -as [type])) {
        $csharp = @"
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class KongorNativeConsoleCtrlHandler
{
    public delegate bool HandlerRoutine(uint dwCtrlType);

    [DllImport("Kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

    private static HandlerRoutine _pinnedDelegate;
    public static string LockPathToRelease;
    public static uint LastCtrlEvent = 999;

    public static bool Register(string lockPath)
    {
        LockPathToRelease = lockPath;
        if (_pinnedDelegate == null)
        {
            _pinnedDelegate = new HandlerRoutine(OnConsoleCtrl);
        }
        return SetConsoleCtrlHandler(_pinnedDelegate, true);
    }

    public static bool SimulateCtrlEvent(uint ctrlType)
    {
        return OnConsoleCtrl(ctrlType);
    }

    private static bool OnConsoleCtrl(uint dwCtrlType)
    {
        // 0 = CTRL_C_EVENT, 1 = CTRL_BREAK_EVENT, 2 = CTRL_CLOSE_EVENT
        if (dwCtrlType == 0 || dwCtrlType == 1 || dwCtrlType == 2)
        {
            LastCtrlEvent = dwCtrlType;
            try
            {
                if (!string.IsNullOrEmpty(LockPathToRelease) && File.Exists(LockPathToRelease))
                {
                    File.Delete(LockPathToRelease);
                }
            }
            catch
            {
                // Fail-safe during process teardown
            }
        }
        return false;
    }
}
"@
        Add-Type -TypeDefinition $csharp -Language CSharp -ErrorAction Stop
    }
    return [KongorNativeConsoleCtrlHandler]::Register($TargetLockPath)
}

function Write-SoftBudgetHandoffPacket {
    param(
        [string]$DirectoryPath,
        [string]$WorkerOwner,
        [int]$CurrentProbe,
        [int]$BudgetMaxProbes,
        [double]$CurrentElapsedSec,
        [double]$BudgetDeadlineSec
    )
    if (-not (Test-Path -LiteralPath $DirectoryPath)) {
        New-Item -ItemType Directory -Path $DirectoryPath -Force | Out-Null
    }
    $existing = @(Get-ChildItem -LiteralPath $DirectoryPath -Filter "exploration-*.json" -ErrorAction SilentlyContinue)
    $nextIdx = $existing.Count + 1
    $fileName = ("exploration-{0:D3}.json" -f $nextIdx)
    $fullPath = Join-Path $DirectoryPath $fileName

    $packet = [ordered]@{
        schema_version           = "1.0"
        generated_at             = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        worker_owner             = if ($WorkerOwner) { $WorkerOwner } else { "worker-$PID" }
        trigger                  = "BUDGET-WARN-90%"
        probe_count              = $CurrentProbe
        max_probes               = $BudgetMaxProbes
        elapsed_seconds          = $CurrentElapsedSec
        deadline_seconds         = $BudgetDeadlineSec
        token_budget_limit       = 1500
        verified_facts           = @("Soft 90% probe/deadline threshold reached ($CurrentProbe/$BudgetMaxProbes probes, ${CurrentElapsedSec}s/${BudgetDeadlineSec}s).")
        tombstoned_hypotheses    = @()
        risk_ordered_live_probes = @()
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($fullPath, ($packet | ConvertTo-Json -Depth 5), $utf8NoBom)
    return $fullPath
}

$auditLedgerModule = Join-Path $PSScriptRoot "lib\KongorAuditLedger.psm1"
if (Test-Path -LiteralPath $auditLedgerModule) {
    Import-Module $auditLedgerModule -Force -DisableNameChecking
}

$workerRole = if ($env:KONGOR_WORKER_ROLE) { $env:KONGOR_WORKER_ROLE.Trim().ToLower() } else { "" }
$isOfflineWorker = $workerRole -in @("offline", "offline-worker", "kongor-offline-worker")

if ($isOfflineWorker -and $Scope -eq "game-worker" -and $Action -in @("acquire", "assert")) {
    if (Get-Command Write-KongorAuditRecord -ErrorAction SilentlyContinue) {
        [void](Write-KongorAuditRecord -Action "kongor_worker_lock" -Target $LockFilePath -Status "refused" -Reason "Read-only offline worker '$($env:KONGOR_WORKER_ROLE)' attempted $Action on game-worker")
    }
    if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
        [void](Write-KongorWorkerEvent -EventType "ROE-DENIED" -WorkerOwner $Owner -WorkerRole $workerRole -Message "Offline worker attempted $Action on game-worker" -EventStreamPath $EventStreamPath)
    }
    Write-Host "[ROE-DENIED] [LOCK DENIED] Worker role '$($env:KONGOR_WORKER_ROLE)' is a read-only offline worker and cannot acquire or assert the live game-worker lock. Hand off live probes to kongor-game-worker, use '-Scope test-runner' for test serialization, or clear `$env:KONGOR_WORKER_ROLE. Diagnose with: .\scripts\kongor_worker_lock.ps1 -Action status" -ForegroundColor Red
    exit 2
}

$currentLock = Get-ActiveLockState -Path $LockFilePath

switch ($Action) {
    "status" {
        if ($null -eq $currentLock) {
            Write-LockLog "Status ($Scope): UNLOCKED (LockFile: $LockFilePath)" Green
            $statusOut = [ordered]@{
                locked          = $false
                scope           = $Scope
                lockFilePath    = $LockFilePath
                owner           = $null
                pid             = $null
                acquiredAt      = $null
                commandSummary  = $null
                leaseTtlSeconds = $null
            }
        } else {
            $cmdDetail = if (-not [string]::IsNullOrWhiteSpace([string]$currentLock.commandSummary)) { " cmd='$($currentLock.commandSummary)'" } else { "" }
            Write-LockLog "Status ($Scope): LOCKED by owner='$($currentLock.owner)' pid=$($currentLock.pid) acquiredAt=$($currentLock.acquiredAt)$cmdDetail" Cyan
            $statusOut = [ordered]@{
                locked          = $true
                scope           = if ($currentLock.scope) { [string]$currentLock.scope } else { $Scope }
                lockFilePath    = $LockFilePath
                owner           = $currentLock.owner
                pid             = [int]$currentLock.pid
                acquiredAt      = $currentLock.acquiredAt
                commandSummary  = $currentLock.commandSummary
                leaseTtlSeconds = $currentLock.leaseTtlSeconds
            }
        }
        Write-Output ($statusOut | ConvertTo-Json -Compress)
        exit 0
    }

    "check-roe" {
        $forbiddenOfflineTools = @(
            "kongor_hot_reload",
            "kongor_ui_control",
            "kongor_capture_window",
            "kongor_client_eval_lua",
            "kongor_game_action",
            "kongor_game_command",
            "kongor_game_client_manage",
            "kongor_client_navigate",
            "kongor_client_practice",
            "inject_override_to_s2z"
        )
        $normalizedTool = if ($ToolName) { $ToolName.Trim().ToLowerInvariant() } else { "" }
        $normalizedPath = if ($TargetPath) { $TargetPath.Replace('\', '/').Trim().ToLowerInvariant() } else { "" }

        if ($isOfflineWorker) {
            $toolBlocked = (-not [string]::IsNullOrWhiteSpace($normalizedTool)) -and ($normalizedTool -in $forbiddenOfflineTools)
            $pathBlocked = (-not [string]::IsNullOrWhiteSpace($normalizedPath)) -and (
                $normalizedPath -match '(^|/)games/client/override(/|$)' -or
                $normalizedPath -match 'pipe/kongor-native-client' -or
                $normalizedPath -match '\.s2z$'
            )
            if ($toolBlocked -or $pathBlocked) {
                $reasonDetail = if ($toolBlocked) { "tool '$ToolName'" } else { "path '$TargetPath'" }
                if (Get-Command Write-KongorAuditRecord -ErrorAction SilentlyContinue) {
                    [void](Write-KongorAuditRecord -Action "check-roe" -Tool $ToolName -Target $TargetPath -Status "refused" -Actor $Owner -Reason "ROE denied for $workerRole on $reasonDetail")
                }
                if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
                    [void](Write-KongorWorkerEvent -EventType "ROE-DENIED" -WorkerOwner $Owner -WorkerRole $workerRole -Message "ROE denied on $reasonDetail" -Payload @{ tool = $ToolName; target_path = $TargetPath } -EventStreamPath $EventStreamPath)
                }
                Write-Host "[ROE-DENIED] Worker role '$($env:KONGOR_WORKER_ROLE)' is prohibited from invoking live client/override $reasonDetail. Queue this action in risk_ordered_live_probes for kongor-game-worker instead. Diagnose with: .\scripts\kongor_worker_lock.ps1 -Action status" -ForegroundColor Red
                exit 2
            }
        }
        Write-LockLog "ROE check passed for role='$workerRole' tool='$ToolName' path='$TargetPath'." Green
        exit 0
    }

    "record-call" {
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        $callState = @{
            consecutive_failures   = 0
            last_signature         = ""
            consecutive_zero_delta = 0
            total_calls            = 0
        }
        if (Test-Path -LiteralPath $StateFilePath) {
            try {
                $loaded = Get-Content -LiteralPath $StateFilePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                $callState.consecutive_failures   = [int]$loaded.consecutive_failures
                $callState.last_signature         = [string]$loaded.last_signature
                $callState.consecutive_zero_delta = [int]$loaded.consecutive_zero_delta
                $callState.total_calls            = [int]$loaded.total_calls
            } catch {
                Write-LockLog "Resetting unreadable call state file '$StateFilePath'." Yellow
            }
        }

        $callState.total_calls++
        $signature = "$ToolName|$ArgsSha256|$ErrorCode"
        $isFailure = -not [string]::IsNullOrWhiteSpace($ErrorCode) -and ($ErrorCode -notin @("0", "OK", "NONE", "SUCCESS"))

        if ($isFailure) {
            if ($callState.last_signature -eq $signature) {
                $callState.consecutive_failures++
            } else {
                $callState.consecutive_failures = 1
                $callState.last_signature = $signature
            }
        } else {
            $callState.consecutive_failures = 0
            $callState.last_signature = ""
        }

        if ($ZeroDelta) {
            $callState.consecutive_zero_delta++
        } else {
            $callState.consecutive_zero_delta = 0
        }

        [System.IO.File]::WriteAllText($StateFilePath, ($callState | ConvertTo-Json -Depth 4), $utf8NoBom)

        if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
            [void](Write-KongorWorkerEvent -EventType "TOOL-CALL" -WorkerOwner $Owner -WorkerRole $workerRole -Message "Tool=$ToolName Error=$ErrorCode" -Payload @{
                tool_name              = $ToolName
                args_sha256            = $ArgsSha256
                error_code             = $ErrorCode
                consecutive_failures   = $callState.consecutive_failures
                consecutive_zero_delta = $callState.consecutive_zero_delta
                probe_count            = $ProbeCount
                max_probes             = $MaxProbes
            } -EventStreamPath $EventStreamPath)
        }

        # Soft 90% probe/deadline budget check (#61)
        $probeRatio = if ($MaxProbes -gt 0) { [double]$ProbeCount / [double]$MaxProbes } else { 0.0 }
        $timeRatio  = if ($DeadlineSeconds -gt 0.0) { $ElapsedSeconds / $DeadlineSeconds } else { 0.0 }
        if ($probeRatio -ge 0.9 -or $timeRatio -ge 0.9) {
            $handoffPath = Write-SoftBudgetHandoffPacket `
                -DirectoryPath $HandoffDir `
                -WorkerOwner $Owner `
                -CurrentProbe $ProbeCount `
                -BudgetMaxProbes $MaxProbes `
                -CurrentElapsedSec $ElapsedSeconds `
                -BudgetDeadlineSec $DeadlineSeconds
            if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
                [void](Write-KongorWorkerEvent -EventType "BUDGET-WARN-90%" -WorkerOwner $Owner -WorkerRole $workerRole -Message "90% budget reached; wrote $handoffPath" -Payload @{ handoff_path = $handoffPath; probe_count = $ProbeCount; max_probes = $MaxProbes } -EventStreamPath $EventStreamPath)
            }
            Write-Host "[BUDGET-WARN-90%] Probe/deadline budget at >= 90% ($ProbeCount/$MaxProbes probes, ${ElapsedSeconds}s/${DeadlineSeconds}s). Wrote structured handoff packet to '$handoffPath'. Prepare to halt cleanly before hard limit." -ForegroundColor Yellow
        }

        # 3-strike identical tool-call failure or 3-strike zero-delta probe breaker (#116)
        if ($callState.consecutive_failures -ge 3 -or $callState.consecutive_zero_delta -ge 3) {
            $tripReason = if ($callState.consecutive_failures -ge 3) {
                "3 identical consecutive failing calls ($signature)"
            } else {
                "3 consecutive zero-delta probes"
            }
            if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
                [void](Write-KongorWorkerEvent -EventType "LOOP-BREAKER" -WorkerOwner $Owner -WorkerRole $workerRole -Message "Tripped loop breaker: $tripReason" -Payload @{ signature = $signature } -EventStreamPath $EventStreamPath)
            }
            Write-Host "[LOOP-BREAKER] Circuit breaker tripped after ${tripReason}. Freeze edits and re-triage via telemetry or compact state with: .\scripts\dev\compact_session_state.ps1" -ForegroundColor Red
            exit 3
        }

        Write-LockLog "Recorded tool call '$ToolName' (consecutive_failures=$($callState.consecutive_failures), zero_delta=$($callState.consecutive_zero_delta))." Green
        exit 0
    }

    "heartbeat" {
        # Check soft 90% probe/deadline budget (#61)
        $probeRatio = if ($MaxProbes -gt 0) { [double]$ProbeCount / [double]$MaxProbes } else { 0.0 }
        $timeRatio  = if ($DeadlineSeconds -gt 0.0) { $ElapsedSeconds / $DeadlineSeconds } else { 0.0 }
        $handoffPath = $null
        if ($probeRatio -ge 0.9 -or $timeRatio -ge 0.9) {
            $handoffPath = Write-SoftBudgetHandoffPacket `
                -DirectoryPath $HandoffDir `
                -WorkerOwner $Owner `
                -CurrentProbe $ProbeCount `
                -BudgetMaxProbes $MaxProbes `
                -CurrentElapsedSec $ElapsedSeconds `
                -BudgetDeadlineSec $DeadlineSeconds
            Write-Host "[BUDGET-WARN-90%] Probe/deadline budget at >= 90% ($ProbeCount/$MaxProbes probes, ${ElapsedSeconds}s/${DeadlineSeconds}s). Wrote structured handoff packet to '$handoffPath'." -ForegroundColor Yellow
        }

        # Drain mid-run operator steering queue (#50)
        $drainedDirectives = @()
        if (Test-Path -LiteralPath $SteerQueuePath) {
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            $rawLines = @(Get-Content -LiteralPath $SteerQueuePath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $remainingLines = @()
            foreach ($line in $rawLines) {
                try {
                    $obj = $line | ConvertFrom-Json -ErrorAction Stop
                    $targetOwner = [string]$obj.target_owner
                    if ([string]::IsNullOrWhiteSpace($targetOwner) -or $targetOwner -eq "*" -or $targetOwner -eq $Owner) {
                        $drainedDirectives += $obj
                        Write-Host "[STEER-DIRECTIVE] directive='$($obj.directive)' target='$($obj.target_value)' reason='$($obj.reason)'" -ForegroundColor Magenta
                    } else {
                        $remainingLines += $line
                    }
                } catch {
                    Write-LockLog "Skipping malformed steering line in '$SteerQueuePath'." Yellow
                }
            }
            if ($remainingLines.Count -eq 0) {
                Remove-Item -LiteralPath $SteerQueuePath -Force -ErrorAction SilentlyContinue
            } else {
                [System.IO.File]::WriteAllLines($SteerQueuePath, $remainingLines, $utf8NoBom)
            }
        }

        if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
            [void](Write-KongorWorkerEvent -EventType "HEARTBEAT" -WorkerOwner $Owner -WorkerRole $workerRole -Message "Heartbeat (steered=$($drainedDirectives.Count))" -Payload @{
                steered_count = $drainedDirectives.Count
                handoff_path  = $handoffPath
            } -EventStreamPath $EventStreamPath)
        }

        $hbOut = [ordered]@{
            ok                 = $true
            owner              = $Owner
            steered_directives = $drainedDirectives
            handoff_path       = $handoffPath
        }
        Write-Output ($hbOut | ConvertTo-Json -Compress -Depth 5)
        exit 0
    }

    "register-ctrl-handler" {
        $registered = Register-KongorConsoleCtrlHandler -TargetLockPath $LockFilePath
        Write-LockLog "Registered Kernel32!SetConsoleCtrlHandler for '$LockFilePath' (result=$registered)." Green
        Write-Output (@{ registered = $registered; lockFilePath = $LockFilePath } | ConvertTo-Json -Compress)
        exit 0
    }

    "acquire" {
        if ([string]::IsNullOrWhiteSpace($Owner)) {
            $Owner = "$Scope-default"
        }
        if ($PidToTrack -le 0) {
            $PidToTrack = $PID
            $parentProc = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction SilentlyContinue
            if ($null -ne $parentProc -and $parentProc.ParentProcessId -gt 0) {
                $PidToTrack = [int]$parentProc.ParentProcessId
            }
        }

        $creationTicks = Get-ProcessCreationTicks -ProcessId ([int]$PidToTrack)
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

        $lockDir = Split-Path -Parent $LockFilePath
        if (-not [string]::IsNullOrWhiteSpace($lockDir) -and -not (Test-Path -LiteralPath $lockDir)) {
            New-Item -ItemType Directory -Path $lockDir -Force | Out-Null
        }

        function New-LockPayloadBytes {
            $payload = [ordered]@{
                scope                = $Scope
                owner                = $Owner
                pid                  = [int]$PidToTrack
                processCreationTicks = [long]$creationTicks
                acquiredAt           = (Get-Date).ToString("o")
                leaseTtlSeconds      = [int]$LeaseTtlSeconds
                commandSummary       = $CommandSummary
                host                 = $env:COMPUTERNAME
            }
            return $utf8NoBom.GetBytes(($payload | ConvertTo-Json -Depth 4))
        }

        if ($Force) {
            $jsonBytes = New-LockPayloadBytes
            $tmpLockPath = "$LockFilePath.tmp.$PID.$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
            [System.IO.File]::WriteAllBytes($tmpLockPath, $jsonBytes)
            Move-Item -LiteralPath $tmpLockPath -Destination $LockFilePath -Force
        } else {
            $acquiredAtomically = $false
            $waitSw = [System.Diagnostics.Stopwatch]::StartNew()
            $deadlineUtc = [DateTime]::UtcNow.AddSeconds([Math]::Max(0, $WaitSeconds))
            $lastWaitLogMs = -99999L
            $retryReapCount = 0

            while (-not $acquiredAtomically) {
                $jsonBytes = New-LockPayloadBytes
                try {
                    $fs = [System.IO.File]::Open(
                        $LockFilePath,
                        [System.IO.FileMode]::CreateNew,
                        [System.IO.FileAccess]::Write,
                        [System.IO.FileShare]::Read)
                    try {
                        $fs.Write($jsonBytes, 0, $jsonBytes.Length)
                        $fs.Flush($true)
                    } finally {
                        $fs.Dispose()
                    }
                    $acquiredAtomically = $true
                    break
                } catch [System.IO.IOException] {
                    $activeHolder = Get-ActiveLockState -Path $LockFilePath
                    if ($null -eq $activeHolder) {
                        $retryReapCount++
                        if ($retryReapCount -le 5) {
                            continue
                        }
                    } elseif ($activeHolder.owner -eq $Owner) {
                        Write-LockLog "Lock ($Scope) already held by '$Owner' (PID $($activeHolder.pid)); refreshing timestamp." Green
                        $tmpLockPath = "$LockFilePath.tmp.$PID.$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
                        [System.IO.File]::WriteAllBytes($tmpLockPath, $jsonBytes)
                        Move-Item -LiteralPath $tmpLockPath -Destination $LockFilePath -Force
                        $acquiredAtomically = $true
                        break
                    }

                    $nowUtc = [DateTime]::UtcNow
                    if ($WaitSeconds -gt 0 -and $nowUtc -lt $deadlineUtc) {
                        $remainingSec = [Math]::Max(0.1, [Math]::Round(($deadlineUtc - $nowUtc).TotalSeconds, 1))
                        if (($waitSw.ElapsedMilliseconds - $lastWaitLogMs) -ge 5000L) {
                            $holderLabel = if ($null -ne $activeHolder) { "owner='$($activeHolder.owner)' pid=$($activeHolder.pid) cmd='$($activeHolder.commandSummary)'" } else { "transient contention" }
                            Write-LockLog "Waiting for $Scope lock ($holderLabel, remainingWait=${remainingSec}s)..." Yellow
                            $lastWaitLogMs = $waitSw.ElapsedMilliseconds
                        }
                        $sleepMs = [int][Math]::Min(1000.0, [Math]::Max(150.0, (250 + (Get-Random -Minimum 0 -Maximum 350))))
                        Start-Sleep -Milliseconds $sleepMs
                        $retryReapCount = 0
                        continue
                    }

                    $holderOwner = if ($null -ne $activeHolder) { $activeHolder.owner } else { "unknown" }
                    $holderPid = if ($null -ne $activeHolder) { $activeHolder.pid } else { 0 }
                    $holderAt = if ($null -ne $activeHolder) { $activeHolder.acquiredAt } else { "unknown" }
                    if (Get-Command Write-KongorAuditRecord -ErrorAction SilentlyContinue) {
                        [void](Write-KongorAuditRecord -Action "kongor_worker_lock" -Target $LockFilePath -Status "refused" -Actor $Owner -Reason "$Scope lock conflict with active owner '$holderOwner' (PID $holderPid)")
                    }
                    Write-Host "[LOCK CONFLICT] Cannot acquire $Scope lock for '$Owner': already held by '$holderOwner' (PID $holderPid since $holderAt). Run '.\scripts\kongor_worker_lock.ps1 -Scope $Scope -Action status' or '-Scope $Scope -Action release -Owner $holderOwner'." -ForegroundColor Red
                    exit 1
                }
            }
        }

        [void](Register-KongorConsoleCtrlHandler -TargetLockPath $LockFilePath)

        if (Get-Command Write-KongorAuditRecord -ErrorAction SilentlyContinue) {
            [void](Write-KongorAuditRecord -Action "kongor_worker_lock" -Target $LockFilePath -Status "allowed" -Actor $Owner -Reason "Acquired $Scope lock (PID $PidToTrack)")
        }
        if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
            [void](Write-KongorWorkerEvent -EventType "LOCK-ACQUIRE" -WorkerOwner $Owner -WorkerRole $workerRole -Message "Acquired $Scope lock (PID $PidToTrack, cmd=$CommandSummary)" -EventStreamPath $EventStreamPath)
        }
        Write-LockLog "Acquired singleton $Scope lock for owner='$Owner' (trackedPid=$PidToTrack, creationTicks=$creationTicks, ttl=${LeaseTtlSeconds}s) at '$LockFilePath'." Green
        exit 0
    }

    "assert" {
        if ($null -eq $currentLock) {
            if ($RequireLock) {
                Write-Host "[LOCK REQUIRED] No active $Scope lock at '$LockFilePath'. Acquire the lock first with: .\scripts\kongor_worker_lock.ps1 -Scope $Scope -Action acquire -Owner '<worker-id>'" -ForegroundColor Red
                exit 1
            }
            Write-LockLog "Assert passed: no conflicting $Scope lock active." Green
            exit 0
        }

        if (-not [string]::IsNullOrWhiteSpace($Owner) -and $currentLock.owner -ne $Owner) {
            Write-Host "[LOCK CONFLICT] $Scope lock is held by '$($currentLock.owner)' (PID $($currentLock.pid)), not '$Owner'. Wait for completion or inspect with: .\scripts\kongor_worker_lock.ps1 -Scope $Scope -Action status" -ForegroundColor Red
            exit 1
        }

        Write-LockLog "Assert passed: active $Scope lock verified for owner='$($currentLock.owner)' (PID $($currentLock.pid))." Green
        exit 0
    }

    "release" {
        if ($null -eq $currentLock) {
            Write-LockLog "Release no-op: $Scope lock file '$LockFilePath' is already unlocked." Yellow
            exit 0
        }

        if (-not $Force -and -not [string]::IsNullOrWhiteSpace($Owner) -and $currentLock.owner -ne $Owner) {
            if (Get-Command Write-KongorAuditRecord -ErrorAction SilentlyContinue) {
                [void](Write-KongorAuditRecord -Action "kongor_worker_lock" -Target $LockFilePath -Status "refused" -Actor $Owner -Reason "Release denied on $Scope; owned by '$($currentLock.owner)'")
            }
            Write-Host "[LOCK DENIED] Cannot release $Scope lock owned by '$($currentLock.owner)' from caller '$Owner'. Pass -Force or specify -Owner '$($currentLock.owner)'." -ForegroundColor Red
            exit 1
        }

        Remove-Item -LiteralPath $LockFilePath -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $StateFilePath) {
            Remove-Item -LiteralPath $StateFilePath -Force -ErrorAction SilentlyContinue
        }
        if (Get-Command Write-KongorAuditRecord -ErrorAction SilentlyContinue) {
            [void](Write-KongorAuditRecord -Action "kongor_worker_lock" -Target $LockFilePath -Status "allowed" -Actor $Owner -Reason "Released $Scope lock previously held by '$($currentLock.owner)'")
        }
        if (Get-Command Write-KongorWorkerEvent -ErrorAction SilentlyContinue) {
            [void](Write-KongorWorkerEvent -EventType "LOCK-RELEASE" -WorkerOwner $Owner -WorkerRole $workerRole -Message "Released $Scope lock previously held by '$($currentLock.owner)'" -EventStreamPath $EventStreamPath)
        }
        Write-LockLog "Released singleton $Scope lock (previously held by '$($currentLock.owner)', PID $($currentLock.pid))." Green
        exit 0
    }
}

