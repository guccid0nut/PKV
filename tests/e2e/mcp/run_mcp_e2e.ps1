# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    ProjectKongor MCP Server E2E Verification Runner (PowerShell)
.DESCRIPTION
    Launches xUnit and standalone E2E verification suites across Tiers 1-4.
.PARAMETER Tier
    Test tier to execute: '1', '2', '3', '4', or 'All' (default: 'All')
.PARAMETER VerboseLogging
    Enable verbose log tracing
#>
[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [ValidateSet("1", "2", "3", "4", "All")]
    [string]$Tier = "All",

    [Parameter()]
    [switch]$VerboseLogging
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path "$RepoRoot\PROJECT.md")) {
    $RepoRoot = (Get-Location).Path
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "          PROJECT KONGOR: MODEL CONTEXT PROTOCOL (MCP) SERVER" -ForegroundColor Cyan
Write-Host "                    OPAQUE-BOX E2E TEST RUNNER" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "  Repo Root : $RepoRoot"
Write-Host "  Tier      : $Tier"
Write-Host "  Transport : JSON-RPC 2.0 over Stdio (kongor mcp)"
Write-Host "================================================================================"

$FilterArg = ""
if ($Tier -ne "All") {
    $FilterArg = "--filter FullyQualifiedName~Tier$Tier"
}

$TestProjectPath = Join-Path $RepoRoot "tests\e2e\mcp\Kongor.Mcp.E2E.Tests.csproj"

Write-Host "`n[1/2] Executing xUnit .NET 11 E2E Test Suite..." -ForegroundColor Yellow
$DotnetCmd = "dotnet test `"$TestProjectPath`" -v normal $FilterArg"
Write-Host "Command: $DotnetCmd" -ForegroundColor DarkGray
Invoke-Expression $DotnetCmd
$DotnetExit = $LASTEXITCODE

Write-Host "`n[2/2] Executing Python Standalone Stdio Verification..." -ForegroundColor Yellow
$PythonScript = Join-Path $RepoRoot "tests\e2e\mcp\run_mcp_e2e.py"
$VerbFlag = if ($VerboseLogging) { "-v" } else { "" }
$PyCmd = "python `"$PythonScript`" --tier $Tier $VerbFlag"
Write-Host "Command: $PyCmd" -ForegroundColor DarkGray
Invoke-Expression $PyCmd
$PyExit = $LASTEXITCODE

if ($DotnetExit -eq 0 -and $PyExit -eq 0) {
    Write-Host "`n  🏆 100% Passed - A WINNER IS YOU`n" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n  ❌ Verification failed (dotnet: $DotnetExit, python: $PyExit)`n" -ForegroundColor Red
    exit 1
}
