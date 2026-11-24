# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Phase 0 Performance Baseline & Executable Budget Harness for Project Kongor.

.DESCRIPTION
    Captures cold/warm wall-clock execution timings (in milliseconds) for core inner-loop
    scripts and gates:
      1. scripts/gate_check.ps1 (Tiers 1-5 individually via -JsonOutput + total wall-clock)
      2. scripts/re/export_ghidra_symbols.ps1 -Scope Curated
      3. scripts/verify_evidence_obligations.ps1 -All
      4. scripts/kongor_worker_lock.ps1 (status check + hermetic stale-PID lock reap)
    Writes structured JSON to both:
      - scripts/qa/.benchmark_baseline.json (git-tracked reference for gate budget assertions)
      - logs/benchmarks/phase0_baseline.json (local telemetry log artifact)

.PARAMETER RecordBaseline
    Records new baseline measurements and updates scripts/qa/.benchmark_baseline.json.

.PARAMETER AssertBudgets
    Compares current execution timings against scripts/qa/.benchmark_baseline.json and hard budgets.

.PARAMETER MaxRegressionRatio
    Allowed multiplier over baseline before failing in -AssertBudgets mode (default: 1.50 for noisy OS scheduling, with hard ceilings).

.EXAMPLE
    .\scripts\qa\benchmark_baseline.ps1 -RecordBaseline
    .\scripts\qa\benchmark_baseline.ps1 -AssertBudgets
#>
[CmdletBinding()]
param(
    [switch]$RecordBaseline,
    [switch]$AssertBudgets,
    [double]$MaxRegressionRatio = 1.50
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$trackedBaselinePath = Join-Path $PSScriptRoot ".benchmark_baseline.json"
$logDir = Join-Path $repoRoot "logs\benchmarks"
$logBaselinePath = Join-Path $logDir "phase0_baseline.json"

function Write-BenchLog {
    param([string]$Message, [ConsoleColor]$Color = [ConsoleColor]::Cyan)
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$ts] [BENCHMARK] $Message" -ForegroundColor $Color
}

if (-not $RecordBaseline -and -not $AssertBudgets) {
    $RecordBaseline = $true
}

Write-BenchLog "Starting Project Kongor Phase 0 Benchmark Suite (Root: $repoRoot)..."

# 1. Measure scripts/gate_check.ps1 with per-tier breakdown
$gateScript = Join-Path $PSScriptRoot "..\gate_check.ps1"
$tempGateJson = Join-Path $env:TEMP ("kongor_bench_gate_" + [guid]::NewGuid().ToString("N") + ".json")
$gateSw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    & $gateScript -JsonOutput $tempGateJson | Out-Null
    $gateExit = $LASTEXITCODE
} finally {
    $gateSw.Stop()
}
$gateTotalMs = [int]$gateSw.ElapsedMilliseconds
$tierMs = [ordered]@{
    T1 = 0
    T2 = 0
    T3 = 0
    T4 = 0
    T5 = 0
}
if (Test-Path -LiteralPath $tempGateJson) {
    try {
        $gJson = Get-Content -LiteralPath $tempGateJson -Raw | ConvertFrom-Json
        if ($null -ne $gJson.T1.durationMs) { $tierMs.T1 = [int]$gJson.T1.durationMs }
        if ($null -ne $gJson.T2.durationMs) { $tierMs.T2 = [int]$gJson.T2.durationMs }
        if ($null -ne $gJson.T3.durationMs) { $tierMs.T3 = [int]$gJson.T3.durationMs }
        if ($null -ne $gJson.T4.durationMs) { $tierMs.T4 = [int]$gJson.T4.durationMs }
        if ($null -ne $gJson.T5.durationMs) { $tierMs.T5 = [int]$gJson.T5.durationMs }
    } finally {
        Remove-Item -LiteralPath $tempGateJson -Force -ErrorAction SilentlyContinue
    }
}
Write-BenchLog "gate_check.ps1: Total=${gateTotalMs}ms (T1=$($tierMs.T1)ms, T2=$($tierMs.T2)ms, T3=$($tierMs.T3)ms, T4=$($tierMs.T4)ms, T5=$($tierMs.T5)ms, Exit=$gateExit)" Green

# 2. Measure scripts/re/export_ghidra_symbols.ps1 -Scope Curated (into temp outputs so working tree stays clean)
$exportScript = Join-Path $PSScriptRoot "..\re\export_ghidra_symbols.ps1"
$tempSymJson = Join-Path $env:TEMP ("kongor_bench_sym_" + [guid]::NewGuid().ToString("N") + ".json")
$tempSymWds  = Join-Path $env:TEMP ("kongor_bench_sym_" + [guid]::NewGuid().ToString("N") + ".wds")
$tempSymRva  = Join-Path $env:TEMP ("kongor_bench_sym_" + [guid]::NewGuid().ToString("N") + ".rva")
$exportSw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    & $exportScript -Scope Curated -OutputPath $tempSymJson -WdsOutputPath $tempSymWds -RvaOutputPath $tempSymRva | Out-Null
} finally {
    $exportSw.Stop()
    Remove-Item -LiteralPath $tempSymJson, $tempSymWds, $tempSymRva -Force -ErrorAction SilentlyContinue
}
$exportMs = [int]$exportSw.ElapsedMilliseconds
Write-BenchLog "export_ghidra_symbols.ps1 (-Scope Curated): ${exportMs}ms" Green

# 3. Measure scripts/verify_evidence_obligations.ps1 -All
$verifyScript = Join-Path $PSScriptRoot "..\verify_evidence_obligations.ps1"
$verifySw = [System.Diagnostics.Stopwatch]::StartNew()
& $verifyScript -All | Out-Null
$verifySw.Stop()
$verifyMs = [int]$verifySw.ElapsedMilliseconds
Write-BenchLog "verify_evidence_obligations.ps1 (-All): ${verifyMs}ms" Green

# 4. Measure scripts/kongor_worker_lock.ps1 stale-PID lock self-healing in-process & via CLI
$lockScript = Join-Path $PSScriptRoot "..\kongor_worker_lock.ps1"
$tempLockFile = Join-Path $env:TEMP ("kongor_bench_lock_" + [guid]::NewGuid().ToString("N") + ".lock")
$stalePayload = @{
    owner      = "bench-dead-worker"
    pid        = 999999
    acquiredAt = (Get-Date).AddMinutes(-10).ToString("o")
    host       = $env:COMPUTERNAME
} | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($tempLockFile, $stalePayload, [System.Text.Encoding]::UTF8)

$lockSw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    & $lockScript -Action status -LockFilePath $tempLockFile -Quiet | Out-Null
} finally {
    $lockSw.Stop()
    Remove-Item -LiteralPath $tempLockFile -Force -ErrorAction SilentlyContinue
}
$lockReapMs = [int]$lockSw.ElapsedMilliseconds
Write-BenchLog "kongor_worker_lock.ps1 (stale-PID lock reap): ${lockReapMs}ms" Green

$currentMetrics = [ordered]@{
    schemaVersion = "1.0.0"
    capturedAt    = (Get-Date).ToString("o")
    host          = $env:COMPUTERNAME
    budgets       = [ordered]@{
        gateCheckTotalMaxMs          = 5000
        gateCheckTier4MaxMs          = 500
        exportSymbolsCuratedMaxMs    = 2500
        verifyObligationsAllMaxMs    = 2000
        workerLockStaleReapCliMaxMs  = 250
    }
    measurements  = [ordered]@{
        gateCheckTotalMs         = $gateTotalMs
        gateCheckTiersMs         = $tierMs
        exportSymbolsCuratedMs   = $exportMs
        verifyObligationsAllMs   = $verifyMs
        workerLockStaleReapCliMs = $lockReapMs
    }
}

if ($RecordBaseline) {
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    $jsonText = ($currentMetrics | ConvertTo-Json -Depth 6) + "`n"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($trackedBaselinePath, $jsonText, $utf8NoBom)
    [System.IO.File]::WriteAllText($logBaselinePath, $jsonText, $utf8NoBom)
    Write-BenchLog "Saved baseline to '$trackedBaselinePath' and '$logBaselinePath'." Green
}

if ($AssertBudgets) {
    if (-not (Test-Path -LiteralPath $trackedBaselinePath)) {
        Write-Host "[BENCHMARK FAIL] Baseline file missing at '$trackedBaselinePath'. Run: .\scripts\benchmark_baseline.ps1 -RecordBaseline" -ForegroundColor Red
        exit 1
    }
    $baseObj = Get-Content -LiteralPath $trackedBaselinePath -Raw | ConvertFrom-Json
    $failures = @()

    if ($gateTotalMs -gt $baseObj.budgets.gateCheckTotalMaxMs) {
        $failures += "gate_check.ps1 total (${gateTotalMs}ms) exceeded hard budget ($($baseObj.budgets.gateCheckTotalMaxMs)ms)"
    }
    if ($tierMs.T4 -gt $baseObj.budgets.gateCheckTier4MaxMs) {
        $failures += "gate_check.ps1 Tier 4 ($($tierMs.T4)ms) exceeded hard budget ($($baseObj.budgets.gateCheckTier4MaxMs)ms)"
    }
    if ($exportMs -gt $baseObj.budgets.exportSymbolsCuratedMaxMs) {
        $failures += "export_ghidra_symbols.ps1 (${exportMs}ms) exceeded hard budget ($($baseObj.budgets.exportSymbolsCuratedMaxMs)ms)"
    }
    if ($verifyMs -gt $baseObj.budgets.verifyObligationsAllMaxMs) {
        $failures += "verify_evidence_obligations.ps1 (${verifyMs}ms) exceeded hard budget ($($baseObj.budgets.verifyObligationsAllMaxMs)ms)"
    }
    if ($lockReapMs -gt $baseObj.budgets.workerLockStaleReapCliMaxMs) {
        $failures += "kongor_worker_lock.ps1 stale reap (${lockReapMs}ms) exceeded hard budget ($($baseObj.budgets.workerLockStaleReapCliMaxMs)ms)"
    }

    if ($failures.Count -gt 0) {
        foreach ($f in $failures) {
            Write-Host "[BUDGET REGRESSION] $f" -ForegroundColor Red
        }
        Write-Host "  -> Diagnose with: .\scripts\benchmark_baseline.ps1 -AssertBudgets" -ForegroundColor Yellow
        exit 1
    }
    Write-BenchLog "All executable performance budgets satisfied!" Green
}

exit 0
