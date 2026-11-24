# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Safe Workstation & Dev-Environment RAM Optimizer for Project Kongor.

.DESCRIPTION
    Reclaims leaked workstation memory without interrupting active IDE MCP tools,
    live game sessions, or running infrastructure containers:
    1. Walks Windows process parent PIDs and reaps ONLY orphaned `docker.exe` MCP
       processes (`mcp/*`, `github-mcp-server`) whose parent process has exited.
    2. Optionally stages a 4 GB + dropCache `~/.wslconfig` profile (backing up any
       existing `~/.wslconfig` to `~/.wslconfig.bak`) WITHOUT running `wsl --shutdown`.
    3. Optionally reports idle non-Kongor MCP processes so the developer can toggle
       them off in the IDE MCP panel to reclaim an additional ~1+ GB of RAM.

.EXAMPLE
    .\scripts\dev\optimize_dev_ram.ps1 -WhatIf
    .\scripts\dev\optimize_dev_ram.ps1 -ApplyWslConfig -ReportUnusedMcps
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$ApplyWslConfig,
    [switch]$ReportUnusedMcps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-RamLog {
    param([string]$Message, [string]$Level = "INFO", [ConsoleColor]$Color = [ConsoleColor]::Cyan)
    $ts = [DateTime]::Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$ts] [RAM-OPT] [$Level] $Message" -ForegroundColor $Color
}

function Get-SystemRamSnapshot {
    $os = Get-CimInstance Win32_OperatingSystem
    $totalGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)
    $freeGB  = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
    $usedGB  = [math]::Round($totalGB - $freeGB, 2)
    return [PSCustomObject]@{
        TotalGB = $totalGB
        UsedGB  = $usedGB
        FreeGB  = $freeGB
    }
}

$before = Get-SystemRamSnapshot
Write-RamLog ("Baseline System RAM: Used {0} GB / {1} GB ({2} GB Free)" -f $before.UsedGB, $before.TotalGB, $before.FreeGB)

# 1. Discover and reap orphaned docker.exe MCP CLI processes (dead parent PID only)
$livePidSet = [System.Collections.Generic.HashSet[int]]::new()
foreach ($proc in (Get-Process -ErrorAction SilentlyContinue)) {
    [void]$livePidSet.Add([int]$proc.Id)
}

$dockerProcs = @(Get-CimInstance Win32_Process -Filter "Name = 'docker.exe'" -ErrorAction SilentlyContinue)
$orphanMcpProcs = [System.Collections.Generic.List[object]]::new()
$keptLiveProcs = 0

foreach ($dp in $dockerProcs) {
    $cmd = [string]$dp.CommandLine
    $isMcpRun = ($cmd -match '\brun\b') -and ($cmd -match '\bmcp/' -or $cmd -match 'github-mcp-server')
    if (-not $isMcpRun) {
        $keptLiveProcs++
        continue
    }

    $parentId = [int]$dp.ParentProcessId
    if ($parentId -gt 0 -and $livePidSet.Contains($parentId)) {
        $keptLiveProcs++
        continue
    }

    $orphanMcpProcs.Add($dp)
}

$orphanMbEstimate = 0.0
if ($orphanMcpProcs.Count -gt 0) {
    $measure = $orphanMcpProcs | Measure-Object WorkingSetSize -Sum
    if ($null -ne $measure -and $null -ne $measure.Sum) {
        $orphanMbEstimate = [math]::Round($measure.Sum / 1MB, 1)
    }
}
Write-RamLog ("Scanned {0} docker.exe processes: {1} dead-parent MCP orphans (~{2} MB host WS), {3} active/live kept untouched." -f $dockerProcs.Count, $orphanMcpProcs.Count, $orphanMbEstimate, $keptLiveProcs)

$reapedCount = 0
foreach ($orphan in $orphanMcpProcs) {
    $targetDesc = "PID $($orphan.ProcessId) (dead ParentPID $($orphan.ParentProcessId), WS=$([math]::Round($orphan.WorkingSetSize / 1MB, 1)) MB)"
    if ($PSCmdlet.ShouldProcess($targetDesc, "Stop orphaned docker.exe MCP process")) {
        try {
            Stop-Process -Id ([int]$orphan.ProcessId) -Force -ErrorAction Stop
            $reapedCount++
        } catch {
            $procStillAlive = Get-Process -Id ([int]$orphan.ProcessId) -ErrorAction SilentlyContinue
            if ($null -ne $procStillAlive) {
                throw "Failed to stop orphaned docker.exe PID $($orphan.ProcessId): $($_.Exception.Message). Diagnose with: Get-CimInstance Win32_Process -Filter `"ProcessId = $($orphan.ProcessId)`""
            }
        }
    }
}

if ($reapedCount -gt 0) {
    Start-Sleep -Milliseconds 600
    Write-RamLog ("Reaped {0} orphaned docker.exe MCP processes cleanly." -f $reapedCount) "OK" ([ConsoleColor]::Green)
}

# 2. Optional safe ~/.wslconfig staging (never invokes wsl --shutdown)
if ($ApplyWslConfig) {
    $wslConfigPath = Join-Path $env:USERPROFILE ".wslconfig"
    $wslBackupPath = Join-Path $env:USERPROFILE ".wslconfig.bak"
    $desiredContent = @(
        "[wsl2]"
        "memory=4GB"
        "swap=2GB"
        ""
        "[experimental]"
        "autoMemoryReclaim=dropCache"
        "sparseVhd=true"
    ) -join "`r`n"

    if ($PSCmdlet.ShouldProcess($wslConfigPath, "Stage 4GB + dropCache WSL2 memory profile (without restarting WSL)")) {
        if (Test-Path $wslConfigPath) {
            Copy-Item -Path $wslConfigPath -Destination $wslBackupPath -Force
            Write-RamLog "Backed up existing .wslconfig to $wslBackupPath"
        }
        Set-Content -Path $wslConfigPath -Value ($desiredContent + "`r`n") -Encoding UTF8
        Write-RamLog "Staged $wslConfigPath (memory=4GB, autoMemoryReclaim=dropCache, sparseVhd=true). Takes effect on next Docker/WSL restart without interrupting active containers." "OK" ([ConsoleColor]::Green)
    }
}

# 3. Optional report of non-Kongor background MCP servers
if ($ReportUnusedMcps) {
    $optionalPatterns = @('blender', 'comfyui', 'mixamo', 'ffmpeg', 'wireshark', 'science', 'dak-', 'playwright')
    $pyProcs = @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe' OR Name = 'python3.12.exe'" -ErrorAction SilentlyContinue)
    $matched = [System.Collections.Generic.List[PSCustomObject]]::new()
    foreach ($pp in $pyProcs) {
        $c = [string]$pp.CommandLine
        foreach ($pat in $optionalPatterns) {
            if ($c -match [regex]::Escape($pat)) {
                $matched.Add([PSCustomObject]@{
                    Category  = $pat
                    ProcessId = $pp.ProcessId
                    WS_MB     = [math]::Round($pp.WorkingSetSize / 1MB, 1)
                })
                break
            }
        }
    }
    if ($matched.Count -gt 0) {
        $totalOptMb = [math]::Round((($matched | Measure-Object WS_MB -Sum).Sum), 1)
        $cats = ($matched | Select-Object -ExpandProperty Category -Unique) -join ", "
        Write-RamLog ("Detected {0} optional non-core MCP helper processes (~{1} MB WS across: {2}). Toggle unused MCPs off in the IDE MCP Manager when gaming to reclaim additional RAM." -f $matched.Count, $totalOptMb, $cats) "TIP" ([ConsoleColor]::Yellow)
    }
}

$after = Get-SystemRamSnapshot
$deltaFreeMB = [math]::Round(($after.FreeGB - $before.FreeGB) * 1024, 0)
Write-RamLog ("Post-Optimization System RAM: Used {0} GB / {1} GB ({2} GB Free | Net Free Delta: {3:+#;-#;0} MB)" -f $after.UsedGB, $after.TotalGB, $after.FreeGB, $deltaFreeMB) "SUMMARY" ([ConsoleColor]::Green)
