# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    5-Case Verification Suite for Graceful Quit-Before-Kill & 1-Thread Windowless Zombie Guard (Rec #9 / FN-008).
.DESCRIPTION
    Hermetically tests scripts/lib/Stop-KongorGameCleanly.ps1 in < 100ms without spawning child processes
    or touching any live hon_x64.exe session.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$helperPath = Join-Path $PSScriptRoot "..\lib\Stop-KongorGameCleanly.ps1"
if (-not (Test-Path -LiteralPath $helperPath)) {
    throw "Required helper not found at '$helperPath'."
}
. $helperPath

$passed = 0
$failed = 0

function Run-ZombieTestCase {
    param(
        [string]$Name,
        [scriptblock]$Body
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $sw.Stop()
        Write-Host ("  [PASS] {0} ({1}ms)" -f $Name, $sw.ElapsedMilliseconds) -ForegroundColor Green
        $script:passed++
    } catch {
        $sw.Stop()
        Write-Host ("  [FAIL] {0} ({1}ms): {2}" -f $Name, $sw.ElapsedMilliseconds, $_.Exception.Message) -ForegroundColor Red
        $script:failed++
    }
}

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host " KONGOR ZOMBIE GUARD & GRACEFUL QUIT TEST SUITE (Rec #9 - 5 Cases)" -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan

# Case 1: 01_NoOp_When_No_Process_Running
Run-ZombieTestCase -Name "01_NoOp_When_No_Process_Running" -Body {
    $res = Stop-KongorGameCleanly -TargetPid @(999999) -SkipPipeQuit
    if ($res.StoppedCount -ne 0 -or $res.GracefulCount -ne 0 -or $res.ForcedCount -ne 0) {
        throw ("Expected 0 stopped processes for non-existent PID, got Stopped={0}." -f $res.StoppedCount)
    }
}

# Case 2: 02_Graceful_Quit_Via_Pipe_And_CloseMainWindow
Run-ZombieTestCase -Name "02_Graceful_Quit_Via_Pipe_And_CloseMainWindow" -Body {
    $mockProc = [PSCustomObject]@{
        Id               = 42001
        ProcessName      = "hon_x64"
        ThreadsCount     = 24
        MainWindowHandle = 0x20480L
        ExitsGracefully  = $true
        SurvivesForceKill = $false
    }
    $res = Stop-KongorGameCleanly -ProcessName "hon_x64" -MockProcesses @($mockProc) -MockPipeQuitResult $true -GracefulTimeoutMs 1500
    if (-not $res.PipeQuitSent) {
        throw "Expected PipeQuitSent == true when named pipe accepts 'cmd quit'."
    }
    if ($res.GracefulCount -ne 1 -or $res.ForcedCount -ne 0 -or $res.StoppedCount -ne 1) {
        throw ("Expected GracefulCount=1 ForcedCount=0, got Graceful={0} Forced={1}." -f $res.GracefulCount, $res.ForcedCount)
    }
}

# Case 3: 03_Force_Kill_Fallback_When_Graceful_Times_Out
Run-ZombieTestCase -Name "03_Force_Kill_Fallback_When_Graceful_Times_Out" -Body {
    $mockProc = [PSCustomObject]@{
        Id                = 42002
        ProcessName       = "hon_x64"
        ThreadsCount      = 18
        MainWindowHandle  = 0x30800L
        ExitsGracefully   = $false
        SurvivesForceKill = $false
    }
    $res = Stop-KongorGameCleanly -ProcessName "hon_x64" -MockProcesses @($mockProc) -MockPipeQuitResult $false -GracefulTimeoutMs 200
    if ($res.ForcedCount -ne 1 -or $res.GracefulCount -ne 0 -or $res.StoppedCount -ne 1) {
        throw ("Expected ForcedCount=1 GracefulCount=0 after graceful timeout, got Forced={0} Graceful={1}." -f $res.ForcedCount, $res.GracefulCount)
    }
}

# Case 4: 04_PreFlight_1Thread_Windowless_Zombie_Detection
Run-ZombieTestCase -Name "04_PreFlight_1Thread_Windowless_Zombie_Detection" -Body {
    if (-not (Test-KongorKernelZombieState -ThreadCount 1 -MainWindowHandle 0)) {
        throw "Expected (ThreadCount=1, MainWindowHandle=0) to be classified as a 1-thread windowless kernel zombie."
    }
    if (Test-KongorKernelZombieState -ThreadCount 12 -MainWindowHandle 0) {
        throw "Expected multi-thread process (ThreadCount=12, MainWindowHandle=0) NOT to be classified as a 1-thread zombie."
    }
    if (Test-KongorKernelZombieState -ThreadCount 1 -MainWindowHandle 0x10240) {
        throw "Expected process with active MainWindowHandle (0x10240) NOT to be classified as a windowless zombie."
    }

    $preZombieMock = [PSCustomObject]@{
        Id                = 42003
        ProcessName       = "hon_x64"
        ThreadsCount      = 1
        MainWindowHandle  = 0L
        AbortOnPreZombie  = $true
        ExitsGracefully   = $false
        SurvivesForceKill = $true
    }
    $threwFn008 = $false
    try {
        [void](Stop-KongorGameCleanly -ProcessName "hon_x64" -MockProcesses @($preZombieMock))
    } catch {
        if ($_.Exception.Message -match 'FN-008' -and $_.Exception.Message -match 'dxgkrnl\.sys') {
            $threwFn008 = $true
        } else {
            throw ("Unexpected exception message: {0}" -f $_.Exception.Message)
        }
    }
    if (-not $threwFn008) {
        throw "Expected Stop-KongorGameCleanly pre-flight zombie guard to throw FN-008 actionable error."
    }
}

# Case 5: 05_PostKill_1Thread_Zombie_Survival_Diagnostic
Run-ZombieTestCase -Name "05_PostKill_1Thread_Zombie_Survival_Diagnostic" -Body {
    $postZombieMock = [PSCustomObject]@{
        Id                = 42004
        ProcessName       = "hon_x64"
        ThreadsCount      = 1
        MainWindowHandle  = 0L
        AbortOnPreZombie  = $false
        ExitsGracefully   = $false
        SurvivesForceKill = $true
    }
    $threwPost = $false
    try {
        [void](Stop-KongorGameCleanly -ProcessName "hon_x64" -MockProcesses @($postZombieMock) -SkipPipeQuit)
    } catch {
        if ($_.Exception.Message -match 'FN-008' -and $_.Exception.Message -match 'FN-008-gpu-force-kill-single-thread-windowless-zombie-wedge\.md') {
            $threwPost = $true
        } else {
            throw ("Unexpected post-kill zombie exception: {0}" -f $_.Exception.Message)
        }
    }
    if (-not $threwPost) {
        throw "Expected Stop-KongorGameCleanly post-kill zombie survival guard to throw actionable FN-008 error."
    }
}

Write-Host "---------------------------------------------------------------------" -ForegroundColor Gray
if ($failed -gt 0) {
    Write-Host (" RESULT: {0} passed, {1} FAILED. Run .\scripts\test_zombie_guard.ps1 to diagnose." -f $passed, $failed) -ForegroundColor Red
    exit 1
} else {
    Write-Host (" RESULT: {0}/{0} zombie guard verification cases PASSED." -f $passed) -ForegroundColor Green
    exit 0
}
