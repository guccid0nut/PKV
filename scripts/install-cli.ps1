# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Installs the location-independent 'kongor' command globally on Windows, Linux, or macOS.

.DESCRIPTION
    Builds Kongor.Cli and Kongor.Shim (Release) via Invoke-KongorTestLock.ps1,
    stages a Host-RID filtered side-by-side slot under ~/.kongor/cli/,
    installs the zero-lock forwarder shim into ~/.kongor/bin/kongor[.exe],
    prepends ~/.kongor/bin onto User PATH, registers this checkout in ~/.kongor/config.json,
    installs static (<2ms) shell tab-completions, and verifies PATH resolution.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/install-cli.ps1
    pwsh scripts/install-cli.ps1
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$NoProfile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$IsWinHost = ($env:OS -eq "Windows_NT") -or ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)
$ExeSuffix = if ($IsWinHost) { ".exe" } else { "" }
$PwshExe = if ($IsWinHost) { "powershell.exe" } else { "pwsh" }
$PathSep = [System.IO.Path]::PathSeparator

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CliProj = Join-Path $RepoRoot "src/tools/kongor-cli/Kongor.Cli.csproj"
$ShimProj = Join-Path $RepoRoot "src/tools/kongor-shim/Kongor.Shim.csproj"
$TestLock = Join-Path $RepoRoot "scripts/Invoke-KongorTestLock.ps1"
$CliExe = Join-Path $RepoRoot "src/tools/kongor-cli/bin/Release/net11.0/kongor$ExeSuffix"

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "         INSTALLING LOCATION-INDEPENDENT GLOBAL 'kongor' CLI                    " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "Workspace Root : $RepoRoot" -ForegroundColor Gray

if (-not $SkipBuild) {
    Write-Host "[1/3] Building Kongor.Cli and Kongor.Shim (Release)..." -ForegroundColor Yellow
    if (Test-Path -LiteralPath $TestLock) {
        & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $TestLock -Owner "install-cli-$PID" -- dotnet build $CliProj -c Release --nologo
    } else {
        & dotnet build $CliProj -c Release --nologo
    }
    if ($LASTEXITCODE -ne 0) {
        throw "[INSTALL_CLI_ERR] Failed to build Kongor.Cli (exit $LASTEXITCODE). Fix: Run 'dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release' to inspect compiler errors."
    }

    if ($IsWinHost -and (Test-Path -LiteralPath $ShimProj)) {
        if (Test-Path -LiteralPath $TestLock) {
            & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $TestLock -Owner "install-shim-$PID" -- dotnet build $ShimProj -c Release --nologo
        } else {
            & dotnet build $ShimProj -c Release --nologo
        }
        if ($LASTEXITCODE -ne 0) {
            throw "[INSTALL_CLI_ERR] Failed to build Kongor.Shim (exit $LASTEXITCODE). Fix: Run 'dotnet build src/tools/kongor-shim/Kongor.Shim.csproj -c Release'."
        }
    }
}

if (-not (Test-Path -LiteralPath $CliExe -PathType Leaf)) {
    throw "[INSTALL_CLI_ERR] Compiled CLI executable not found at '$CliExe'. Fix: Re-run scripts/install-cli.ps1 without -SkipBuild."
}

Write-Host "[2/3] Staging side-by-side slot (~/.kongor/cli) & installing ~/.kongor/bin shim..." -ForegroundColor Yellow
& $CliExe --root $RepoRoot refresh --no-build
if ($LASTEXITCODE -ne 0) {
    throw "[INSTALL_CLI_ERR] Unified CLI self-installer failed (exit $LASTEXITCODE). Fix: Inspect diagnostics above and verify write access to '$HOME/.kongor'."
}

$targetShell = if ($IsWinHost) { "powershell" } else { "bash" }
if (-not $NoProfile) {
    Write-Host "[2.5/3] Installing instant (<2ms) static $targetShell tab-completions (pass -NoProfile to skip)..." -ForegroundColor Yellow
    & $CliExe --root $RepoRoot completion $targetShell --install-profile
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "[INSTALL_CLI_WARN] Static $targetShell completion profile setup exited with $LASTEXITCODE (non-fatal)."
    }
} else {
    Write-Host "[2.5/3] Skipping shell profile update (-NoProfile specified); writing static script only..." -ForegroundColor DarkGray
    & $CliExe --root $RepoRoot completion $targetShell --write
}

Write-Host "[3/3] Updating current shell PATH & verifying active 'kongor' resolution..." -ForegroundColor Yellow
$KongorBinDir = Join-Path $HOME ".kongor/bin"
$ExpectedShim = Join-Path $KongorBinDir "kongor$ExeSuffix"

if (Test-Path Alias:\kongor) {
    Remove-Item Alias:\kongor -Force -ErrorAction SilentlyContinue
}

$pathParts = @($env:PATH -split [regex]::Escape([string]$PathSep) | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and
    ($_.TrimEnd('\', '/') -ine $KongorBinDir.TrimEnd('\', '/'))
})
$env:PATH = (@($KongorBinDir) + $pathParts) -join $PathSep

$resolvedCmds = @(Get-Command kongor -All -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -eq 'Application' })
if ($resolvedCmds.Count -eq 0) {
    throw "[INSTALL_CLI_ERR] 'kongor' was not found on PATH after install. Expected '$ExpectedShim'. Fix: Add '$KongorBinDir' to User PATH."
}

$activeSource = $resolvedCmds[0].Source
Write-Host "  Active 'kongor' : $activeSource" -ForegroundColor Green
if ($activeSource -ine $ExpectedShim) {
    Write-Host "  [WARN] Another 'kongor' binary precedes '$ExpectedShim' on PATH: $activeSource" -ForegroundColor Yellow
    Write-Host "         Fix: Open a new terminal or remove '$activeSource'." -ForegroundColor Yellow
} elseif ($resolvedCmds.Count -gt 1) {
    $others = ($resolvedCmds | Select-Object -Skip 1 | ForEach-Object { $_.Source }) -join ", "
    Write-Host "  [INFO] Additional legacy shims found lower on PATH (shadowed cleanly): $others" -ForegroundColor DarkGray
}

Write-Host "================================================================================" -ForegroundColor Green
Write-Host "✨ SUCCESS! 'kongor' is installed globally and works from ANY directory!" -ForegroundColor Green
Write-Host ""
Write-Host "Try running from anywhere (e.g. $HOME):" -ForegroundColor Cyan
Write-Host "    kongor where" -ForegroundColor Yellow
Write-Host "    kongor play" -ForegroundColor Yellow
Write-Host "    kongor status" -ForegroundColor Yellow
Write-Host "    kongor workspace list" -ForegroundColor Yellow
Write-Host "================================================================================" -ForegroundColor Green
