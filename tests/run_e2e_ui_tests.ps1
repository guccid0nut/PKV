# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Automated End-to-End (E2E) Test Runner for Project Kongor UI Restoration.

.DESCRIPTION
    Executes the 4-Tier E2E test suite covering Features 1-15 in SCOPE.md § Feature Inventory:
    - Tier 1: Feature Coverage (Features 1-15, 75 tests)
    - Tier 2: Boundary & Corner Cases (Features 1-15, 75 tests)
    - Tier 3: Cross-Feature Pairwise Combinations (10 tests)
    - Tier 4: Real-World Application Production Flows (6 tests)
    Total: 166 Test Cases.

.PARAMETER TestTier
    Tier filter: 'All', '1', '2', '3', or '4' (default: "All").

.PARAMETER Feature
    Feature filter: 1 to 15 (default: 0 = All Features).

.PARAMETER TestFilter
    Optional string or regex pattern to filter test names or IDs.

.PARAMETER VerboseLogging
    Enable verbose per-test diagnostics and trace output.

.PARAMETER JsonReport
    Optional file path to output detailed JSON results report.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests/run_e2e_ui_tests.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests/run_e2e_ui_tests.ps1 -TestTier 1 -Feature 10 -VerboseLogging
#>

[CmdletBinding()]
param(
    [Parameter(HelpMessage = "Tier to execute: All, 1, 2, 3, 4")]
    [ValidateSet("All", "1", "2", "3", "4")]
    [string]$TestTier = "All",

    [Parameter(HelpMessage = "Feature number: 1-15 (0 = All)")]
    [ValidateRange(0, 15)]
    [int]$Feature = 0,

    [Parameter(HelpMessage = "Filter test names/IDs by pattern")]
    [string]$TestFilter = "",

    [Parameter(HelpMessage = "Enable verbose debug output")]
    [switch]$VerboseLogging,

    [Parameter(HelpMessage = "Path to export JSON report")]
    [string]$JsonReport = ""
)

$ErrorActionPreference = "Stop"

function Write-TestBanner([string]$Message) {
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "================================================================================" -ForegroundColor Cyan
}

function Write-TestLog([string]$Message, [string]$Level = "INFO") {
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $line = "[$timestamp] [$Level] $Message"
    switch ($Level) {
        "INFO"    { Write-Host $line -ForegroundColor Gray }
        "SUCCESS" { Write-Host $line -ForegroundColor Green }
        "FAIL"    { Write-Host $line -ForegroundColor Red }
        "WARN"    { Write-Host $line -ForegroundColor Yellow }
        "DEBUG"   { if ($VerboseLogging) { Write-Host $line -ForegroundColor DarkGray } }
        default   { Write-Host $line }
    }
}

Write-TestBanner "PROJECT KONGOR: UI RESTORATION & DIAGNOSTICS E2E TEST RUNNER"
Write-TestLog "Target Tier   : $TestTier" "INFO"
Write-TestLog "Target Feature: $(if ($Feature -gt 0) { $Feature } else { 'All (1-15)' })" "INFO"
Write-TestLog "Filter Query  : $(if ($TestFilter) { $TestFilter } else { 'None (All Tests)' })" "INFO"
Write-TestLog "Verbose Mode  : $VerboseLogging" "INFO"

# Locate runner script
$runnerScript = Join-Path $PSScriptRoot "client_ui_e2e\run_ui_e2e.py"
if (-not (Test-Path $runnerScript)) {
    Write-TestLog "Python test runner not found at: $runnerScript" "FAIL"
    exit 1
}

# Build arguments
$pyArgs = @($runnerScript, "--tier", $TestTier)
if ($Feature -gt 0) {
    $pyArgs += @("--feature", $Feature)
}
if ($TestFilter) {
    $pyArgs += @("--filter", $TestFilter)
}
if ($VerboseLogging) {
    $pyArgs += @("--verbose")
}
if ($JsonReport) {
    $pyArgs += @("--json-output", $JsonReport)
}

Write-TestLog "Executing: python $($pyArgs -join ' ')" "DEBUG"

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& python $pyArgs
$exitCode = $LASTEXITCODE
$sw.Stop()

Write-Host ""
if ($exitCode -eq 0) {
    Write-TestLog "E2E UI Test Suite Execution Completed Successfully in $($sw.Elapsed.TotalSeconds.ToString('F3'))s." "SUCCESS"
} else {
    Write-TestLog "E2E UI Test Suite Execution Detected Failures (Exit Code: $exitCode) in $($sw.Elapsed.TotalSeconds.ToString('F3'))s." "WARN"
}

exit $exitCode
