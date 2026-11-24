# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    3-Stage Graceful Quit-Before-Kill & 1-Thread Windowless Kernel Zombie Guard (Rec #9 / FN-008).
.DESCRIPTION
    Prevents and detects unkillable 1-thread windowless kernel zombie processes (Threads.Count <= 1,
    MainWindowHandle == 0) caused by force-killing Vulkan/DXVK/D3D9 processes while a GPU thread is
    blocked inside a non-alertable dxgkrnl.sys WDDM kernel transition.
#>

function Test-KongorKernelZombieState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$ThreadCount,
        [Parameter(Mandatory = $true)][long]$MainWindowHandle
    )
    return ($ThreadCount -le 1 -and $MainWindowHandle -eq 0)
}

function Send-KongorPipeQuitCommand {
    [CmdletBinding()]
    param(
        [string]$PipeName = "kongor-native-client",
        [int]$ConnectTimeoutMs = 250
    )
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($ConnectTimeoutMs)
        $writer = New-Object System.IO.StreamWriter($pipe, [System.Text.Encoding]::UTF8, 1024, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine("cmd quit")
        return $true
    } catch {
        return $false
    } finally {
        if ($null -ne $pipe) {
            try { $pipe.Dispose() } catch { }
        }
    }
}

function Stop-KongorGameCleanly {
    [CmdletBinding()]
    param(
        [string]$ProcessName = "hon_x64",
        [int[]]$TargetPid = @(),
        [int]$GracefulTimeoutMs = 1500,
        [string]$PipeName = "kongor-native-client",
        [switch]$SkipPipeQuit,
        [object[]]$MockProcesses = $null,
        [bool]$MockPipeQuitResult = $false
    )

    $useMocks = ($null -ne $MockProcesses)
    $targets = New-Object System.Collections.Generic.List[object]

    if ($useMocks) {
        foreach ($mp in $MockProcesses) {
            if ($null -ne $mp) { $targets.Add($mp) }
        }
    } elseif ($TargetPid -and $TargetPid.Count -gt 0) {
        foreach ($pidVal in $TargetPid) {
            $p = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
            if ($null -ne $p) { $targets.Add($p) }
        }
    } else {
        $procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
        foreach ($p in $procs) {
            if ($null -ne $p) { $targets.Add($p) }
        }
    }

    if ($targets.Count -eq 0) {
        Write-Verbose "[STOP-CLEAN] No running '$ProcessName' processes found."
        return [PSCustomObject]@{
            StoppedCount     = 0
            GracefulCount    = 0
            ForcedCount      = 0
            PipeQuitSent     = $false
            PreZombieFlagged = $false
        }
    }

    # Stage 1: Pre-Flight 1-Thread Windowless Kernel Zombie Inspection
    $preZombieFlagged = $false
    foreach ($p in $targets) {
        $tCount = 0
        $hwnd = 0L
        $pName = $ProcessName
        $procIdVal = 0
        if ($useMocks) {
            $tCount = [int]$p.ThreadsCount
            $hwnd = [long]$p.MainWindowHandle
            $pName = [string]$p.ProcessName
            $procIdVal = [int]$p.Id
        } else {
            try {
                $p.Refresh()
                $tCount = $p.Threads.Count
                $hwnd = [long]$p.MainWindowHandle
                $pName = $p.ProcessName
                $procIdVal = $p.Id
            } catch { }
        }

        if ($tCount -gt 0 -and (Test-KongorKernelZombieState -ThreadCount $tCount -MainWindowHandle $hwnd)) {
            $preZombieFlagged = $true
            Write-Host ("[STOP-CLEAN] Pre-flight warning: Process '{0}' (PID={1}) matches 1-thread windowless zombie signature (Threads={2}, HWND=0x{3:X})." -f
                $pName, $procIdVal, $tCount, $hwnd) -ForegroundColor Yellow
            if ($useMocks -and $p.PSObject.Properties["AbortOnPreZombie"] -and [bool]$p.AbortOnPreZombie) {
                throw ("[FN-008 ZOMBIE GUARD] Pre-flight check detected unkillable 1-thread windowless kernel zombie for '{0}' (PID={1}, Threads={2}, MainWindowHandle=0x0) wedged in dxgkrnl.sys. Run 'pnputil /enum-devices /class Display' to restart the GPU driver or reboot Windows. See docs/reference/field-notes/FN-008-gpu-force-kill-single-thread-windowless-zombie-wedge.md" -f
                    $pName, $procIdVal, $tCount)
            }
        }
    }

    # Stage 2: Graceful Quit Request (IPC pipe 'cmd quit' + WM_CLOSE CloseMainWindow)
    $pipeQuitSent = $false
    if (-not $SkipPipeQuit -and $ProcessName -eq "hon_x64") {
        if ($useMocks) {
            $pipeQuitSent = $MockPipeQuitResult
        } else {
            $pipeQuitSent = Send-KongorPipeQuitCommand -PipeName $PipeName
        }
        if ($pipeQuitSent) {
            Write-Host "[STOP-CLEAN] Sent graceful 'cmd quit' via \\.\pipe\$PipeName." -ForegroundColor Cyan
        }
    }

    if ($useMocks) {
        $gracefulCount = 0
        $remainingMocks = New-Object System.Collections.Generic.List[object]
        foreach ($mp in $targets) {
            if ([bool]$mp.ExitsGracefully) {
                $gracefulCount++
            } else {
                $remainingMocks.Add($mp)
            }
        }
        $forcedCount = 0
        foreach ($rem in $remainingMocks) {
            Write-Host ("[STOP-CLEAN] Process '{0}' (PID={1}) still alive after {2}ms graceful window; invoking Stop-Process -Force fallback..." -f
                $rem.ProcessName, $rem.Id, $GracefulTimeoutMs) -ForegroundColor Yellow
            if ([bool]$rem.SurvivesForceKill) {
                throw ("[FN-008 ZOMBIE GUARD] Process '{0}' (PID={1}) survived Stop-Process -Force as a 1-thread windowless kernel zombie (Threads={2}, HWND=0x{3:X}). Its GPU thread is wedged inside dxgkrnl.sys holding .s2z file locks. See docs/reference/field-notes/FN-008-gpu-force-kill-single-thread-windowless-zombie-wedge.md" -f
                    $rem.ProcessName, $rem.Id, $rem.ThreadsCount, $rem.MainWindowHandle)
            }
            $forcedCount++
        }
        return [PSCustomObject]@{
            StoppedCount     = ($gracefulCount + $forcedCount)
            GracefulCount    = $gracefulCount
            ForcedCount      = $forcedCount
            PipeQuitSent     = $pipeQuitSent
            PreZombieFlagged = $preZombieFlagged
        }
    }

    $targetPids = @($targets | ForEach-Object { $_.Id })
    foreach ($pidVal in $targetPids) {
        $p = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
        if ($null -ne $p) {
            try {
                if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
                    [void]$p.CloseMainWindow()
                }
            } catch { }
        }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $GracefulTimeoutMs) {
        $alive = @($targetPids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
        if ($alive.Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 50
    }
    $sw.Stop()

    $remainingPids = @($targetPids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
    $gracefulCount = $targetPids.Count - $remainingPids.Count
    $forcedCount = 0

    # Stage 3: Force-Kill Fallback + Post-Kill 1-Thread Zombie Verification
    foreach ($remPid in $remainingPids) {
        $remProc = Get-Process -Id $remPid -ErrorAction SilentlyContinue
        if ($null -eq $remProc) {
            $gracefulCount++
            continue
        }
        Write-Host ("[STOP-CLEAN] Process '{0}' (PID={1}) still alive after {2}ms graceful window; invoking Stop-Process -Force fallback..." -f
            $remProc.ProcessName, $remPid, $GracefulTimeoutMs) -ForegroundColor Yellow

        $killError = $null
        try {
            Stop-Process -Id $remPid -Force -ErrorAction Stop
            $forcedCount++
        } catch {
            $killError = $_.Exception.Message
        }

        $postSw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($postSw.ElapsedMilliseconds -lt 500) {
            if ($null -eq (Get-Process -Id $remPid -ErrorAction SilentlyContinue)) {
                break
            }
            Start-Sleep -Milliseconds 50
        }

        $survivor = Get-Process -Id $remPid -ErrorAction SilentlyContinue
        if ($null -ne $survivor) {
            $sThreads = try { $survivor.Threads.Count } catch { 1 }
            $sHwnd = try { [long]$survivor.MainWindowHandle } catch { 0L }
            throw ("[FN-008 ZOMBIE GUARD] Process '{0}' (PID={1}) survived Stop-Process -Force as a 1-thread windowless kernel zombie (Threads={2}, HWND=0x{3:X}, KillErr='{4}'). Its GPU thread is wedged inside dxgkrnl.sys holding .s2z file locks. See docs/reference/field-notes/FN-008-gpu-force-kill-single-thread-windowless-zombie-wedge.md" -f
                $ProcessName, $remPid, $sThreads, $sHwnd, $killError)
        }
    }

    return [PSCustomObject]@{
        StoppedCount     = ($gracefulCount + $forcedCount)
        GracefulCount    = $gracefulCount
        ForcedCount      = $forcedCount
        PipeQuitSent     = $pipeQuitSent
        PreZombieFlagged = $preZombieFlagged
    }
}
