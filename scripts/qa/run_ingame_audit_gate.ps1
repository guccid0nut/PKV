# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Headless QA Gate for In-Game K2 Test+++ Audit (11/11 Suites + VRAM Slot Leak Guard).
.DESCRIPTION
    Connects to a running hon_x64.exe client (via invoke_k2_lua.ps1 / native pipe), triggers
    TestSuite.AuditAll() (all 11 suites including Suite 11/11 PKV Catalog Icons), waits for
    [AUDIT-RUN-END], saves the audit log via TestSuite.SaveAuditLog(), runs
    scripts/qa/export_audit_log.ps1 -Strict, and verifies 0 'No free texture slots' warnings
    plus [AUDIT-MEM] VRAM reclaim in console.log.
.EXAMPLE
    .\scripts\qa\run_ingame_audit_gate.ps1 -TimeoutSeconds 240 -IncludeCustomMaps
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [int]$TimeoutSeconds = 240,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeCustomMaps = $false,

    [Parameter(Mandatory = $false)]
    [switch]$UpdateBaseline = $false,

    [Parameter(Mandatory = $false)]
    [string]$ConsoleLogPath = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$invokeLuaScript = Join-Path $PSScriptRoot "invoke_k2_lua.ps1"
$exportAuditScript = Join-Path $PSScriptRoot "export_audit_log.ps1"

if ([string]::IsNullOrWhiteSpace($ConsoleLogPath)) {
    $candidates = @(
        (Join-Path $repoRoot "games\client\userdata\Heroes of Newerth x64\override\console.log"),
        (Join-Path $repoRoot "games\client\userdata\Heroes of Newerth x64\game\console.log")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) {
            $ConsoleLogPath = $c
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ConsoleLogPath) -or -not (Test-Path -LiteralPath $ConsoleLogPath)) {
    throw "[INGAME-AUDIT-GATE] console.log not found at '$ConsoleLogPath'. Ensure hon_x64.exe is running in lobby (GamePhase == 0) first."
}

if ([string]::IsNullOrWhiteSpace($JsonOutput)) {
    $outDir = Join-Path $repoRoot "artifacts\captures"
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $JsonOutput = Join-Path $outDir "ingame_audit_gate_latest.json"
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [INGAME-AUDIT-GATE] Running Test+++ 11-Suite Audit + D3D Texture Slot Guard" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

function Read-SharedLogLines {
    param([string]$Path)
    $list = New-Object System.Collections.Generic.List[string]
    $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8, $true)
        try {
            while ($null -ne ($ln = $sr.ReadLine())) {
                $list.Add($ln)
            }
        } finally {
            $sr.Dispose()
        }
    } finally {
        $fs.Dispose()
    }
    return $list.ToArray()
}

# Capture baseline line count before starting audit run
& $invokeLuaScript -Code "Echo('[INGAME-AUDIT-GATE-BEGIN]')" -WaitMs 200 -Quiet
$preLines = (Read-SharedLogLines -Path $ConsoleLogPath).Length

# Launch TestSuite.AuditAll() on live client
& $invokeLuaScript -Code "if TestSuite then TestSuite.ClearAuditLog(); TestSuite.AuditAll() else Echo('[INGAME-AUDIT-GATE-ERR] TestSuite global missing') end" -WaitMs 500 -Quiet

Write-Host (" [1/4] Polling console.log for [AUDIT-RUN-END] (timeout={0}s)..." -f $TimeoutSeconds) -ForegroundColor Gray
$completed = $false
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    & $invokeLuaScript -Code "Cmd('FlushLogs')" -WaitMs 150 -Quiet
    $linesNow = Read-SharedLogLines -Path $ConsoleLogPath
    if ($linesNow.Length -gt $preLines) {
        for ($i = $preLines; $i -lt $linesNow.Length; $i++) {
            if ($linesNow[$i] -match "\[AUDIT-RUN-END\]") {
                $completed = $true
                break
            }
            if ($linesNow[$i] -match "\[INGAME-AUDIT-GATE-ERR\]") {
                throw "[INGAME-AUDIT-GATE] Client reported error: $($linesNow[$i])"
            }
        }
    }
    if ($completed) { break }
}

if (-not $completed) {
    throw "[INGAME-AUDIT-GATE] Timed out after ${TimeoutSeconds}s waiting for [AUDIT-RUN-END] in '$ConsoleLogPath'."
}

if ($IncludeCustomMaps) {
    Write-Host " [2/4] Running TestSuite.AuditCustomMaps() sweeper..." -ForegroundColor Gray
    $cmPre = (Read-SharedLogLines -Path $ConsoleLogPath).Length
    & $invokeLuaScript -Code "TestSuite.AuditCustomMaps()" -WaitMs 500 -Quiet
    $cmDone = $false
    $cmDeadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $cmDeadline) {
        Start-Sleep -Seconds 2
        & $invokeLuaScript -Code "Cmd('FlushLogs')" -WaitMs 150 -Quiet
        $linesNow = Read-SharedLogLines -Path $ConsoleLogPath
        for ($i = $cmPre; $i -lt $linesNow.Length; $i++) {
            if ($linesNow[$i] -match "\[AUDIT-CUSTOM-MAPS-END\]") {
                $cmDone = $true
                break
            }
        }
        if ($cmDone) { break }
    }
    if (-not $cmDone) {
        throw "[INGAME-AUDIT-GATE] Timed out waiting for [AUDIT-CUSTOM-MAPS-END]."
    }
}

Write-Host " [3/4] Saving audit log & verifying via export_audit_log.ps1..." -ForegroundColor Gray
& $invokeLuaScript -Code "TestSuite.SaveAuditLog(); Cmd('FlushLogs')" -WaitMs 1000 -Quiet

$exportArgs = @{
    ConsoleLogPath = $ConsoleLogPath
}
if ($UpdateBaseline) {
    $exportArgs["UpdateBaseline"] = $true
} else {
    $exportArgs["Strict"] = $true
}

& $exportAuditScript @exportArgs
$exportExit = $LASTEXITCODE

Write-Host " [4/4] Checking D3D texture slot leak guard & [AUDIT-MEM] reclaim telemetry..." -ForegroundColor Gray
$postLines = Read-SharedLogLines -Path $ConsoleLogPath
$delta = if ($postLines.Length -gt $preLines) { $postLines[$preLines..($postLines.Length - 1)] } else { @() }

$texSlotWarnings = @($delta | Where-Object { $_ -match "No free texture slots" })
$reclaimEvents = @($delta | Where-Object { $_ -match "\[AUDIT-MEM\]\s*ReclaimAuditResources" })
$suitePasses = @($delta | Where-Object { (($_ -replace '\^.', '') -match "Audit Complete:\s*\d+\s*/\s*\d+") -and ($_ -notmatch "\[AUDIT-EXPORT\]") })

$sw.Stop()
$passed = ($exportExit -eq 0) -and ($texSlotWarnings.Count -eq 0) -and ($reclaimEvents.Count -gt 0) -and ($suitePasses.Count -ge 11)
$exitCode = if ($passed) { 0 } else { 1 }

$summary = [ordered]@{
    passed               = $passed
    exitCode             = $exitCode
    durationSeconds      = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
    suiteSummaryLines    = $suitePasses.Count
    textureSlotWarnings  = $texSlotWarnings.Count
    vramReclaimEvents    = $reclaimEvents.Count
    includeCustomMaps    = [bool]$IncludeCustomMaps
    exportExitCode       = $exportExit
    consoleLogPath       = $ConsoleLogPath
    capturedAtUtc        = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $JsonOutput -Encoding UTF8

$color = if ($passed) { "Green" } else { "Red" }
Write-Host ("[INGAME-AUDIT-GATE RESULT] Status={0} | Duration={1}s | Suites={2} | TexSlotWarnings={3} | VramReclaims={4} | Report={5}" -f
    ($(if ($passed) { "PASS" } else { "FAIL" })), $summary.durationSeconds, $summary.suiteSummaryLines, $summary.textureSlotWarnings, $summary.vramReclaimEvents, $JsonOutput) -ForegroundColor $color

exit $exitCode
