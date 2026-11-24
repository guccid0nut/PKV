# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Executes a build or test command under the Project Kongor singleton test-runner mutex.

.DESCRIPTION
    Serializes concurrent agent test/build runs (dotnet build, dotnet test, pytest, cmake --build)
    across the workspace using $env:TEMP\kongor_test_runner.lock.
    - Parses $args manually so Windows PowerShell 5.1 '-File ... -- dotnet test --nologo' never
      trips parameter-binder ambiguity on '--' or '--filter'.
    - Tracks this wrapper process's own $PID (so crashes or kills are reaped immediately).
    - Auto-queues up to -WaitSeconds (default 240s) when another agent is running tests.
    - Guarantees lock cleanup via try/finally + Win32 SetConsoleCtrlHandler.
    - Propagates the exact child exit code.

.EXAMPLE
    .\scripts\Invoke-KongorTestLock.ps1 -Owner "flash-b1" -- dotnet test tests/unit/libs/NEXUS.Contracts.Tests --nologo
    .\scripts\Invoke-KongorTestLock.ps1 -Owner "flash-b2" -Command "python -m pytest tests/unit/test_worker_test_lock.py"
#>

$ErrorActionPreference = 'Stop'

$Owner = ""
$WaitSeconds = 240
$LeaseTtlSeconds = 600
$LockFilePath = ""
$Command = ""
$NoWait = $false
$Quiet = $false
$effectiveArgs = New-Object System.Collections.Generic.List[string]

$idx = 0
$rawArgs = @($args)
while ($idx -lt $rawArgs.Count) {
    $tok = [string]$rawArgs[$idx]
    if ($tok -eq "--") {
        for ($j = $idx + 1; $j -lt $rawArgs.Count; $j++) {
            $effectiveArgs.Add([string]$rawArgs[$j])
        }
        break
    }
    switch -Regex ($tok) {
        '^-Owner$' {
            $idx++
            if ($idx -ge $rawArgs.Count) { throw "Missing value for -Owner" }
            $Owner = [string]$rawArgs[$idx]
        }
        '^-WaitSeconds$' {
            $idx++
            if ($idx -ge $rawArgs.Count) { throw "Missing value for -WaitSeconds" }
            $WaitSeconds = [int]$rawArgs[$idx]
        }
        '^-LeaseTtlSeconds$' {
            $idx++
            if ($idx -ge $rawArgs.Count) { throw "Missing value for -LeaseTtlSeconds" }
            $LeaseTtlSeconds = [int]$rawArgs[$idx]
        }
        '^-LockFilePath$' {
            $idx++
            if ($idx -ge $rawArgs.Count) { throw "Missing value for -LockFilePath" }
            $LockFilePath = [string]$rawArgs[$idx]
        }
        '^-Command$' {
            $idx++
            if ($idx -ge $rawArgs.Count) { throw "Missing value for -Command" }
            $Command = [string]$rawArgs[$idx]
        }
        '^-NoWait$' {
            $NoWait = $true
        }
        '^-Quiet$' {
            $Quiet = $true
        }
        default {
            for ($j = $idx; $j -lt $rawArgs.Count; $j++) {
                $effectiveArgs.Add([string]$rawArgs[$j])
            }
            $idx = $rawArgs.Count
        }
    }
    $idx++
}

$lockScriptPath = Join-Path $PSScriptRoot "kongor_worker_lock.ps1"
if (-not (Test-Path -LiteralPath $lockScriptPath)) {
    Write-Host "[TEST-LOCK ERROR] Missing lock script at '$lockScriptPath'. Verify repository checkout and retry." -ForegroundColor Red
    exit 2
}

if ([string]::IsNullOrWhiteSpace($LockFilePath)) {
    $LockFilePath = Join-Path $env:TEMP "kongor_test_runner.lock"
}

if ([string]::IsNullOrWhiteSpace($Owner)) {
    if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_WORKER_OWNER)) {
        $Owner = $env:KONGOR_WORKER_OWNER
    } else {
        $Owner = "test-runner-$PID"
    }
}

if ($NoWait) {
    $WaitSeconds = 0
}

if ([string]::IsNullOrWhiteSpace($Command) -and $effectiveArgs.Count -eq 0) {
    Write-Host "[TEST-LOCK ERROR] No command specified. Pass '-Command ""<cmd>""' or '-- <exe> <args...>'. Example: .\scripts\Invoke-KongorTestLock.ps1 -Owner 'agent-1' -- dotnet test --nologo" -ForegroundColor Red
    exit 2
}

$summaryText = if (-not [string]::IsNullOrWhiteSpace($Command)) {
    $Command
} else {
    ($effectiveArgs.ToArray() -join " ")
}
if ($summaryText.Length -gt 160) {
    $summaryText = $summaryText.Substring(0, 160) + "..."
}

function Write-TestLockLog {
    param([string]$Msg, [ConsoleColor]$Color = [ConsoleColor]::Cyan)
    if (-not $Quiet) {
        $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
        Write-Host "[$ts] [TEST-LOCK] $Msg" -ForegroundColor $Color
    }
}

$acquireSw = [System.Diagnostics.Stopwatch]::StartNew()
Write-TestLockLog "Requesting test-runner lock for owner='$Owner' (wrapperPid=$PID, wait=${WaitSeconds}s, cmd='$summaryText')..." Cyan

$acquireParams = @{
    Scope            = "test-runner"
    Action           = "acquire"
    Owner            = $Owner
    PidToTrack       = $PID
    WaitSeconds      = $WaitSeconds
    LeaseTtlSeconds  = $LeaseTtlSeconds
    CommandSummary   = $summaryText
    LockFilePath     = $LockFilePath
}
if ($Quiet) {
    $acquireParams["Quiet"] = $true
}

& $lockScriptPath @acquireParams
$acquireExit = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
$acquireSw.Stop()

if ($acquireExit -ne 0) {
    Write-Host "[TEST-LOCK DENIED] Failed to acquire test-runner lock for '$Owner' after $([Math]::Round($acquireSw.Elapsed.TotalSeconds, 2))s (exit=$acquireExit). Inspect active test holder with: .\scripts\kongor_worker_lock.ps1 -Scope test-runner -Action status" -ForegroundColor Red
    exit $acquireExit
}

Write-TestLockLog "Lock acquired in $($acquireSw.ElapsedMilliseconds)ms. Executing: $summaryText" Green

$execSw = [System.Diagnostics.Stopwatch]::StartNew()
$childExitCode = 1

try {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if (-not [string]::IsNullOrWhiteSpace($Command)) {
            Invoke-Expression $Command
            $childExitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
        } else {
            $exe = $effectiveArgs[0]
            $exeArgs = if ($effectiveArgs.Count -gt 1) { $effectiveArgs.GetRange(1, $effectiveArgs.Count - 1).ToArray() } else { @() }
            & $exe @exeArgs
            $childExitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
        }
    } finally {
        $ErrorActionPreference = $prevEap
    }
} catch {
    Write-Host "[TEST-LOCK EXEC ERROR] Command threw exception for owner='$Owner': $($_.Exception.Message). Re-run or inspect command syntax: $summaryText" -ForegroundColor Red
    $childExitCode = 1
} finally {
    $execSw.Stop()
    try {
        & $lockScriptPath -Scope "test-runner" -Action "release" -Owner $Owner -LockFilePath $LockFilePath -Quiet | Out-Null
    } catch {
        Write-Host "[TEST-LOCK WARN] Fallback direct cleanup on '$LockFilePath' after release error: $($_.Exception.Message)" -ForegroundColor Yellow
        if (Test-Path -LiteralPath $LockFilePath) {
            Remove-Item -LiteralPath $LockFilePath -Force -ErrorAction SilentlyContinue
        }
    }
}

$logColor = if ($childExitCode -eq 0) { [ConsoleColor]::Green } else { [ConsoleColor]::Yellow }
Write-TestLockLog "Completed command for owner='$Owner' in $([Math]::Round($execSw.Elapsed.TotalSeconds, 2))s (exitCode=$childExitCode); test-runner lock released." $logColor
exit $childExitCode
