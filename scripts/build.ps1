# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Comprehensive Multi-Target Build Automation Script for ProjectKongor.

.DESCRIPTION
    Builds the entire ProjectKongor ecosystem or specific target projects
    across Debug and/or Release configurations.

.PARAMETER Configuration
    Build configuration ('Debug', 'Release', 'All', or 'Both'). Default: 'All' (builds both Debug and Release).

.PARAMETER Target
    Optional specific project or category to build:
    'all', 'apps', 'services', 'libs', 'tools', 'tests',
    'willowmaker', 'compel', 'aspire', 'master-server', 'chat-server', 'cli', etc.

.PARAMETER Clean
    Performs 'dotnet clean' before building.

.PARAMETER Restore
    Forces package restoration.

.PARAMETER Verbosity
    MSBuild verbosity ('quiet', 'minimal', 'normal', 'detailed', 'diagnostic'). Default: 'minimal'.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Release
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration All -Clean
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Target willowmaker -Configuration Release
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Configuration = 'All',

    [Parameter(Position = 1)]
    [string]$Target = 'all',

    [switch]$Clean,
    [switch]$Restore,
    [ValidateSet('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')]
    [string]$Verbosity = 'minimal',

    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalArgs
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Smart swap if first positional argument was actually a target (e.g. 'native', 'apps', 'willowmaker')
$validConfigs = @('debug', 'release', 'all', 'both', 'help')
if ($Configuration.ToLowerInvariant() -notin $validConfigs -and $Target -eq 'all') {
    $Target = $Configuration
    $Configuration = 'All'
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "                PROJECT KONGOR ECOSYSTEM BUILD PIPELINE                        " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "[INFO] Repository Root: $RepoRoot" -ForegroundColor DarkGray
Write-Host "[INFO] Target Scope:    $Target" -ForegroundColor DarkGray
Write-Host "[INFO] Configuration:   $Configuration" -ForegroundColor DarkGray
Write-Host "[INFO] Verbosity:       $Verbosity" -ForegroundColor DarkGray
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$isHelp = $Help -or ($AdditionalArgs -contains "-Help") -or ($AdditionalArgs -contains "--help") -or ($AdditionalArgs -contains "-h") -or ($AdditionalArgs -contains "-?") -or ($Configuration -in @("-Help", "--help", "-h", "-?", "help", "Help"))

if ($isHelp) {
    Write-Host @"
USAGE:
    .\scripts\build.ps1 [[-Configuration] <Debug|Release|All|Both>] [[-Target] <project_or_scope>] [-Clean] [-Restore] [-Verbosity <level>] [-Help]

OPTIONS:
    -Configuration <string>   Build configuration ('Debug', 'Release', 'All', 'Both'). Default: 'All'
    -Target <string>          Target project or category ('all', 'apps', 'services', 'libs', 'tools', 'tests', etc.)
    -Clean                    Perform 'dotnet clean' before building
    -Restore                  Force NuGet package restoration
    -Verbosity <string>       MSBuild verbosity ('quiet', 'minimal', 'normal', 'detailed', 'diagnostic'). Default: 'minimal'
    -Help                     Display this help synopsis and options
"@ -ForegroundColor Yellow
    exit 0
}

# 0b. Handle Unified Native C++ Build via CMake
$TargetLower = $Target.ToLowerInvariant().Trim()
if ($TargetLower -in @('native', 'c++', 'cmake', 'vulkan')) {
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host "                PROJECT KONGOR: UNIFIED NATIVE C++ BUILD PIPELINE               " -ForegroundColor Cyan
    Write-Host "================================================================================" -ForegroundColor Cyan
    $nativeDir = Join-Path $RepoRoot "native"
    $buildDir = Join-Path $nativeDir "build"

    $cmakeCmd = Get-Command cmake -ErrorAction SilentlyContinue
    if (-not $cmakeCmd) {
        Write-Host "[ERROR] CMake was not found in PATH. Please install CMake 3.20+." -ForegroundColor Red
        exit 1
    }

    if ($Clean -and (Test-Path $buildDir)) {
        Write-Host "[INFO] Cleaning native build cache ($buildDir)..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
    }

    $buildConfig = if ($Configuration -in @('All', 'Both')) { 'Release' } else { $Configuration }
    Write-Host "[INFO] Configuring CMake build in: $buildDir (Config: $buildConfig)" -ForegroundColor Cyan
    & cmake -B "$buildDir" -S "$nativeDir"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] CMake configuration failed with exit code $LASTEXITCODE." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    Write-Host "[INFO] Compiling native targets (Config: $buildConfig, Parallel)..." -ForegroundColor Cyan
    & cmake --build "$buildDir" --config $buildConfig --parallel
    $buildExit = $LASTEXITCODE
    if ($buildExit -eq 0) {
        Write-Host "[SUCCESS] Native C++ unified build succeeded!" -ForegroundColor Green
    } else {
        Write-Host "[ERROR] Native C++ build failed with exit code $buildExit." -ForegroundColor Red
    }
    exit $buildExit
}

# 1. Resolve Target Projects
$AllProjects = @(
    # Core Shared Libraries (src/libs)
    @{ Name = "Kongor.Core"; Path = "src\libs\protocol-core\Kongor.Core.csproj"; Category = "libs" },
    @{ Name = "ASPIRE.ServiceDefaults"; Path = "src\libs\service-defaults\ASPIRE.ServiceDefaults.csproj"; Category = "libs" },
    @{ Name = "MERRICK.DatabaseContext"; Path = "src\libs\database-context\MERRICK.DatabaseContext.csproj"; Category = "libs" },
    @{ Name = "NEXUS.Contracts"; Path = "src\libs\contracts\NEXUS.Contracts.csproj"; Category = "libs" },
    @{ Name = "ASPIRE.SourceGenerator"; Path = "src\libs\source-generators\ASPIRE.SourceGenerator.csproj"; Category = "libs" },

    # Core Backend Cloud Microservices (src/services)
    @{ Name = "ASPIRE AppHost"; Path = "src\services\aspire-host\ASPIRE.ApplicationHost.csproj"; Category = "services" },
    @{ Name = "KONGOR Master Server"; Path = "src\services\master-server\KONGOR.MasterServer.csproj"; Category = "services" },
    @{ Name = "TRANSMUTANSTEIN Chat Server"; Path = "src\services\chat-server\TRANSMUTANSTEIN.ChatServer.csproj"; Category = "services" },
    @{ Name = "ZORGATH Web API"; Path = "src\services\web-api\ZORGATH.WebPortal.API.csproj"; Category = "services" },
    @{ Name = "DAWNBRINGER Web UI"; Path = "src\services\web-ui\DAWNBRINGER.WebPortal.UI.csproj"; Category = "services" },

    # Consumer Applications & Supervisors (src/apps)
    @{ Name = "WILLOWMAKER (Launcher)"; Path = "src\apps\willowmaker\WILLOWMAKER.csproj"; Category = "apps" },
    @{ Name = "COMPEL (Server Host)"; Path = "src\apps\compel\COMPEL.csproj"; Category = "apps" },
    @{ Name = "NEXUS Control Panel"; Path = "src\apps\control-panel\NEXUS.ControlPanel.GUI.csproj"; Category = "apps" },

    # Optional / Pluggable Extensions (src/extensions)
    @{ Name = "Kongor.Modding"; Path = "src\extensions\modding\Kongor.Modding.csproj"; Category = "extensions" },
    @{ Name = "Replay Renderer Service"; Path = "src\extensions\replay-renderer\ReplayRenderer.Service.csproj"; Category = "extensions" },
    @{ Name = "NEXUS.VulkanInterop"; Path = "src\extensions\vulkan-interop\NEXUS.VulkanInterop.csproj"; Category = "extensions" },
    @{ Name = "NEXUS.CdnBroker"; Path = "src\extensions\cdn-broker\NEXUS.CdnBroker.csproj"; Category = "extensions" },

    # Developer CLI Utilities & Verifiers (src/tools)
    @{ Name = "Kongor.Cli"; Path = "src\tools\kongor-cli\Kongor.Cli.csproj"; Category = "tools" },
    @{ Name = "ModCli"; Path = "src\tools\mod-cli\ModCli.csproj"; Category = "tools" },
    @{ Name = "PrebakeTool"; Path = "src\tools\prebake-tool\prebake-tool.csproj"; Category = "tools" },
    @{ Name = "EndpointAuditor"; Path = "src\tools\endpoint-auditor\EndpointAuditor.csproj"; Category = "tools" },
    @{ Name = "LuaVerifier"; Path = "src\tools\lua-verifier\LuaVerifier.csproj"; Category = "tools" },

    # Test Suites (tests/)
    @{ Name = "NEXUS.Contracts.Tests"; Path = "tests\unit\libs\NEXUS.Contracts.Tests\NEXUS.Contracts.Tests.csproj"; Category = "tests" },
    @{ Name = "Kongor.Core.Tests"; Path = "tests\unit\libs\Kongor.Core.Tests\Kongor.Core.Tests.csproj"; Category = "tests" },
    @{ Name = "KONGOR.MasterServer.Tests"; Path = "tests\unit\services\KONGOR.MasterServer.Tests\KONGOR.MasterServer.Tests.csproj"; Category = "tests" },
    @{ Name = "TRANSMUTANSTEIN.ChatServer.Tests"; Path = "tests\unit\services\TRANSMUTANSTEIN.ChatServer.Tests\TRANSMUTANSTEIN.ChatServer.Tests.csproj"; Category = "tests" },
    @{ Name = "ZORGATH.WebPortal.API.Tests"; Path = "tests\unit\services\ZORGATH.WebPortal.API.Tests\ZORGATH.WebPortal.API.Tests.csproj"; Category = "tests" },
    @{ Name = "COMPEL.Tests"; Path = "tests\unit\apps\COMPEL.Tests\COMPEL.Tests.csproj"; Category = "tests" },
    @{ Name = "WILLOWMAKER.Tests"; Path = "tests\unit\apps\WILLOWMAKER.Tests\WILLOWMAKER.Tests.csproj"; Category = "tests" },
    @{ Name = "Kongor.Modding.Tests"; Path = "tests\unit\extensions\Kongor.Modding.Tests\Kongor.Modding.Tests.csproj"; Category = "tests" },
    @{ Name = "ReplayRenderer.Tests"; Path = "tests\unit\extensions\ReplayRenderer.Tests\ReplayRenderer.Tests.csproj"; Category = "tests" },
    @{ Name = "Grpc.Challenger.Tests"; Path = "tests\integration\Grpc.Challenger.Tests\Grpc.Challenger.Tests.csproj"; Category = "tests" },
    @{ Name = "ASPIRE.Prebaking.Tests"; Path = "tests\integration\ASPIRE.Prebaking.Tests\ASPIRE.Prebaking.Tests.csproj"; Category = "tests" },
    @{ Name = "ASPIRE.Integration.Tests"; Path = "tests\integration\ASPIRE.Integration.Tests\ASPIRE.Integration.Tests.csproj"; Category = "tests" },
    @{ Name = "ASPIRE.Tests.Performance"; Path = "tests\performance\ASPIRE.Tests.Performance\ASPIRE.Tests.Performance.csproj"; Category = "tests" }
)

# Filter targets
$TargetLower = $Target.ToLowerInvariant().Trim()
$SelectedProjects = @()

if ($TargetLower -eq 'all' -or [string]::IsNullOrWhiteSpace($TargetLower)) {
    $SelectedProjects = $AllProjects
} elseif ($TargetLower -in @('apps', 'services', 'libs', 'tools', 'tests')) {
    $SelectedProjects = $AllProjects | Where-Object { $_.Category -eq $TargetLower }
} else {
    $SelectedProjects = $AllProjects | Where-Object {
        $_.Name.ToLowerInvariant().Contains($TargetLower) -or
        $_.Path.ToLowerInvariant().Contains($TargetLower)
    }
}

if ($SelectedProjects.Count -eq 0) {
    Write-Host "[ERROR] No projects matched target '$Target'." -ForegroundColor Red
    Write-Host "Available targets: all, apps, services, libs, tools, tests, willowmaker, compel, aspire, master, chat, web, mod, cli, etc." -ForegroundColor Yellow
    exit 1
}

Write-Host "[INFO] Discovered $($SelectedProjects.Count) project(s) to build:" -ForegroundColor Green
foreach ($p in $SelectedProjects) {
    Write-Host "  * [$($p.Category.ToUpper())] $($p.Name) -> $($p.Path)" -ForegroundColor DarkCyan
}
Write-Host ""

# Determine configurations to build
$Configs = @()
if ($Configuration -in @('All', 'Both')) {
    $Configs = @('Debug', 'Release')
} else {
    $Configs = @($Configuration)
}

$Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$Failures = New-Object System.Collections.ArrayList
$SuccessCount = 0

foreach ($cfg in $Configs) {
    Write-Host "================================================================================" -ForegroundColor Yellow
    Write-Host "BUILDING CONFIGURATION: $cfg" -ForegroundColor Yellow
    Write-Host "================================================================================" -ForegroundColor Yellow

    # Optional Clean
    if ($Clean) {
        Write-Host "[CLEAN] Cleaning $cfg artifacts..." -ForegroundColor DarkYellow
        foreach ($proj in $SelectedProjects) {
            $projFile = Join-Path $RepoRoot $proj.Path
            if (Test-Path $projFile) {
                dotnet clean $projFile -c $cfg -v $Verbosity | Out-Null
            }
        }
    }

    # Build each project
    $Index = 0
    foreach ($proj in $SelectedProjects) {
        $Index++
        $projFile = Join-Path $RepoRoot $proj.Path

        if (-not (Test-Path $projFile)) {
            Write-Host "[$Index/$($SelectedProjects.Count)] [SKIP] Missing project: $($proj.Path)" -ForegroundColor DarkGray
            continue
        }

        Write-Host "[$Index/$($SelectedProjects.Count)] Compiling $($proj.Name) ($cfg)..." -ForegroundColor Cyan

        $projStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        if ($Restore) {
            $output = dotnet build $projFile -c $cfg -v $Verbosity --nologo --force 2>&1
        } else {
            $output = dotnet build $projFile -c $cfg -v $Verbosity --nologo 2>&1
        }
        $exitCode = $LASTEXITCODE
        $projStopwatch.Stop()
        $durationStr = $projStopwatch.Elapsed.TotalSeconds.ToString("F1")

        if ($exitCode -eq 0) {
            $SuccessCount++
            Write-Host "  [OK] Succeeded in ${durationStr}s" -ForegroundColor Green
        } else {
            $failObj = @{
                Name   = $proj.Name
                Path   = $proj.Path
                Config = $cfg
                Output = ($output | Out-String)
            }
            [void]$Failures.Add($failObj)
            Write-Host "  [FAIL] Failed (${durationStr}s)" -ForegroundColor Red
            
            $errLines = $output | Select-String -Pattern "error|fatal error|CS[0-9]"
            if ($errLines) {
                foreach ($err in $errLines) {
                    Write-Host "    $err" -ForegroundColor Red
                }
            }
        }
    }
}

# Stage modernized native binaries to client & server runtime directories idempotently
$nativeBinDir = Join-Path $RepoRoot "artifacts\native-binaries\windows-x64"
if (Test-Path $nativeBinDir) {
    Write-Host ""
    Write-Host "[NATIVE] Verifying and staging native modernization binaries..." -ForegroundColor Cyan
    $stageTargets = @(
        (Join-Path $RepoRoot "games\client\windows-x64"),
        (Join-Path $RepoRoot "games\server\windows-x64")
    )
    foreach ($targetDir in $stageTargets) {
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }
        Get-ChildItem -Path $nativeBinDir -File | ForEach-Object {
            $destFile = Join-Path $targetDir $_.Name
            if (-not (Test-Path $destFile) -or (Get-Item $destFile).Length -ne $_.Length) {
                Copy-Item -Path $_.FullName -Destination $destFile -Force
                Write-Host "  [STAGED] Modernized native binary: $($_.Name) -> $targetDir" -ForegroundColor DarkGreen
            }
        }
    }
}

$Stopwatch.Stop()
$totalElapsedStr = $Stopwatch.Elapsed.TotalSeconds.ToString("F2")
$failColor = if ($Failures.Count -eq 0) { "Green" } else { "Red" }

Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "                       BUILD SUMMARY REPORT                                     " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "Total Elapsed Time:   ${totalElapsedStr} seconds" -ForegroundColor White
Write-Host "Successful Builds:    $SuccessCount" -ForegroundColor Green
Write-Host "Failed Builds:        $($Failures.Count)" -ForegroundColor $failColor

if ($Failures.Count -gt 0) {
    Write-Host ""
    Write-Host "[FAIL] The following projects failed to build:" -ForegroundColor Red
    foreach ($f in $Failures) {
        Write-Host "  * [$($f.Config)] $($f.Name) ($($f.Path))" -ForegroundColor Red
    }
    Write-Host ""
    exit 1
} else {
    Write-Host ""
    Write-Host "ALL TARGETS COMPILED CLEANLY ACROSS ALL SPECIFIED CONFIGURATIONS!" -ForegroundColor Green
    exit 0
}
