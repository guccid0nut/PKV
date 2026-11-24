# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Installs optional developer, asset-pipeline, and AI-agent MCP tools for Project KONGOR V.
.DESCRIPTION
    Project KONGOR V builds and passes all unit tests from a cleanroom source checkout with zero
    external binary tools beyond the .NET SDK and CMake. This helper script installs optional
    local tools (clangd, DirectXTex texconv, Voidtools Everything CLI, FFmpeg, Proximo, K2-Blender
    reference, Voicebox, and opt-in mixamo-mcp) into their expected local paths without polluting Git.
.EXAMPLE
    .\scripts\Install-OptionalAgentTools.ps1 -WhatIf
.EXAMPLE
    .\scripts\Install-OptionalAgentTools.ps1 -Tool clangd,texconv,everything
.EXAMPLE
    .\scripts\Install-OptionalAgentTools.ps1 -Tool All
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [ValidateSet('All', 'clangd', 'texconv', 'everything', 'ffmpeg', 'proximo', 'k2-blender', 'voicebox', 'mixamo-mcp', 'goose')]
    [string[]]$Tool = @('All'),

    [Parameter()]
    [switch]$AcceptUnlicensedThirdParty = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$isWindowsHost = ($env:OS -eq 'Windows_NT') -or ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)

function Write-Step {
    param([string]$Message)
    Write-Host "[OPTIONAL-TOOLS] $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "[OPTIONAL-TOOLS] [OK] $Message" -ForegroundColor Green
}

function Write-WarnMsg {
    param([string]$Message)
    Write-Host "[OPTIONAL-TOOLS] [WARN] $Message" -ForegroundColor Yellow
}

function Install-GitToolRepo {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RepoUrl,
        [Parameter(Mandatory)][string]$RelativeDest,
        [Parameter(Mandatory)][string]$LicenseLabel
    )

    $destPath = Join-Path $repoRoot $RelativeDest
    if (Test-Path (Join-Path $destPath '.git')) {
        Write-Ok "$Name already cloned at '$RelativeDest' ($LicenseLabel)."
        return
    }

    if ($PSCmdlet.ShouldProcess($destPath, "Clone $Name ($LicenseLabel) from $RepoUrl")) {
        $parentDir = Split-Path -Parent $destPath
        if (-not (Test-Path $parentDir)) {
            New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
        }
        Write-Step "Cloning $Name ($LicenseLabel) -> $RelativeDest ..."
        & git clone --depth 1 $RepoUrl $destPath
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to clone $Name from $RepoUrl (exit code $LASTEXITCODE). Verify network access and run: git clone $RepoUrl `"$destPath`""
        }
        Write-Ok "Installed $Name at '$RelativeDest'."
    }
}

function Install-WingetPackageIfMissing {
    param(
        [Parameter(Mandatory)][string]$CommandName,
        [Parameter(Mandatory)][string]$WingetId,
        [Parameter(Mandatory)][string]$DisplayName,
        [Parameter()][string]$LocalCandidateGlob = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($LocalCandidateGlob)) {
        $localMatch = Get-ChildItem -Path (Join-Path $repoRoot $LocalCandidateGlob) -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $localMatch) {
            Write-Ok "$DisplayName already present in workspace at '$($localMatch.FullName)'."
            return
        }
    }

    $existing = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Write-Ok "$DisplayName already available at '$($existing.Source)'."
        return
    }

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        Write-WarnMsg "winget not found on PATH; cannot auto-install $DisplayName ($WingetId). Install manually and re-run .\kongor.ps1 doctor."
        return
    }

    if ($PSCmdlet.ShouldProcess($WingetId, "Install $DisplayName via winget")) {
        Write-Step "Installing $DisplayName via winget ($WingetId) ..."
        & winget install --id $WingetId --exact --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            Write-WarnMsg "winget exited with code $LASTEXITCODE for $WingetId. Run manually: winget install --id $WingetId --exact"
        } else {
            Write-Ok "Installed $DisplayName ($WingetId)."
        }
    }
}

function Install-DirectXTexTools {
    $texconvDir = Join-Path $repoRoot '.tools\bin\texconv'
    $texconvExe = Join-Path $texconvDir 'texconv.exe'
    if (Test-Path $texconvExe) {
        Write-Ok "DirectXTex texconv.exe already present at '.tools/bin/texconv/texconv.exe'."
        return
    }

    $releaseBase = 'https://github.com/microsoft/DirectXTex/releases/latest/download'
    $binaries = @('texconv.exe', 'texdiag.exe', 'texassemble.exe')

    if ($PSCmdlet.ShouldProcess($texconvDir, "Download Microsoft DirectXTex (MIT) CLI binaries ($($binaries -join ', '))")) {
        New-Item -ItemType Directory -Path $texconvDir -Force | Out-Null
        foreach ($bin in $binaries) {
            $targetFile = Join-Path $texconvDir $bin
            $url = "$releaseBase/$bin"
            Write-Step "Downloading $bin (MIT) from $url ..."
            Invoke-WebRequest -Uri $url -OutFile $targetFile -UseBasicParsing
        }
        Write-Ok "Installed DirectXTex tools to '.tools/bin/texconv/'."
    }
}

$expandAll = $Tool -contains 'All'
$selected = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if ($expandAll) {
    foreach ($t in @('clangd', 'texconv', 'everything', 'ffmpeg', 'proximo', 'k2-blender', 'voicebox')) {
        [void]$selected.Add($t)
    }
}
foreach ($t in $Tool) {
    if ($t -ne 'All') {
        [void]$selected.Add($t)
    }
}

Write-Step "Selected optional tool targets: $([string]::Join(', ', $selected))"

if ($selected.Contains('clangd')) {
    Install-WingetPackageIfMissing -CommandName 'clangd' -WingetId 'LLVM.LLVM' -DisplayName 'LLVM clangd C++ Language Server' -LocalCandidateGlob '.tools\bin\clangd\clangd_*\bin\clangd.exe'
}

if ($selected.Contains('texconv')) {
    if ($isWindowsHost) {
        Install-DirectXTexTools
    } else {
        Write-WarnMsg "Skipping texconv.exe download on non-Windows OS."
    }
}

if ($selected.Contains('everything')) {
    if ($isWindowsHost) {
        Install-WingetPackageIfMissing -CommandName 'Everything' -WingetId 'voidtools.Everything' -DisplayName 'Voidtools Everything Search Engine' -LocalCandidateGlob '.tools\bin\everything\everything.exe'
        Install-WingetPackageIfMissing -CommandName 'es' -WingetId 'voidtools.Everything.Cli' -DisplayName 'Voidtools Everything CLI (es.exe)' -LocalCandidateGlob '.tools\bin\everything\es.exe'
    } else {
        Write-WarnMsg "Skipping Voidtools Everything on non-Windows OS."
    }
}

if ($selected.Contains('ffmpeg')) {
    Install-WingetPackageIfMissing -CommandName 'ffmpeg' -WingetId 'Gyan.FFmpeg' -DisplayName 'FFmpeg Media Toolkit'
}

if ($selected.Contains('proximo')) {
    Install-GitToolRepo -Name 'Proximo DLL Proxy MCP' `
        -RepoUrl 'https://github.com/Piotrixek/Proximo.git' `
        -RelativeDest '.tools/external/proximo' `
        -LicenseLabel 'GPL-3.0'
}

if ($selected.Contains('k2-blender')) {
    Install-GitToolRepo -Name 'K2-Blender Reference' `
        -RepoUrl 'https://github.com/KRAT0Sz/K2-Blender-master.git' `
        -RelativeDest '.tools/external/K2-Blender-master' `
        -LicenseLabel 'MIT'
}

if ($selected.Contains('voicebox')) {
    Install-GitToolRepo -Name 'Voicebox Local TTS Studio' `
        -RepoUrl 'https://github.com/jamiepine/voicebox.git' `
        -RelativeDest '.tools/external/voicebox-repo' `
        -LicenseLabel 'MIT'
}

if ($selected.Contains('mixamo-mcp')) {
    if (-not $AcceptUnlicensedThirdParty) {
        throw "mixamo-mcp (https://github.com/v1xel/mixamo-mcp) does not publish an open-source LICENSE file upstream. Re-run with -Tool mixamo-mcp -AcceptUnlicensedThirdParty if you wish to clone it locally for personal research."
    }
    Install-GitToolRepo -Name 'Mixamo MCP (Personal Opt-In)' `
        -RepoUrl 'https://github.com/v1xel/mixamo-mcp.git' `
        -RelativeDest '.tools/external/mixamo-mcp' `
        -LicenseLabel 'No Upstream License - Personal Opt-In Only'
}

if ($selected.Contains('goose')) {
    $gooseVersion = 'v2026.4'
    $gooseRoot = Join-Path $repoRoot '.tools/goose'
    $gooseBinDir = Join-Path $gooseRoot 'bin'
    $gooseHomeDir = Join-Path $gooseRoot 'home'
    $gooseExeName = if ($isWindowsHost) { 'goose.exe' } else { 'goose' }
    $existingGoose = Get-ChildItem -Path $gooseBinDir -Filter $gooseExeName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($null -ne $existingGoose) {
        Write-Ok "Goose ($gooseVersion, Apache-2.0) already installed at '$($existingGoose.FullName)'."
    } else {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        if ($isWindowsHost) {
            $assetName = "goose-$gooseVersion-windows-x64.zip"
        } elseif ($IsMacOS) {
            $macArch = if ($arch -eq 'arm64') { 'arm64' } else { 'x64' }
            $assetName = "goose-$gooseVersion-macos-$macArch.tar.gz"
        } else {
            $assetName = "goose-$gooseVersion-linux-x64.tar.gz"
        }

        $downloadUrl = "https://github.com/aardappel/goose/releases/download/$gooseVersion/$assetName"
        $archivePath = Join-Path $gooseRoot $assetName

        if ($PSCmdlet.ShouldProcess($gooseBinDir, "Download & extract Goose $gooseVersion (Apache-2.0) from $downloadUrl")) {
            New-Item -ItemType Directory -Path $gooseBinDir -Force | Out-Null
            New-Item -ItemType Directory -Path $gooseHomeDir -Force | Out-Null
            Write-Step "Downloading Goose $gooseVersion (Apache-2.0) from $downloadUrl ..."
            Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
            if ($assetName.EndsWith('.zip')) {
                Expand-Archive -Path $archivePath -DestinationPath $gooseBinDir -Force
            } else {
                & tar -xzf $archivePath -C $gooseBinDir
                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to extract $archivePath via tar (exit code $LASTEXITCODE). Extract manually into '$gooseBinDir'."
                }
            }
            Remove-Item -Path $archivePath -Force -ErrorAction SilentlyContinue
            $installedBin = Get-ChildItem -Path $gooseBinDir -Filter $gooseExeName -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $installedBin) {
                throw "Goose archive extracted to '$gooseBinDir', but '$gooseExeName' was not found. Inspect '$gooseBinDir' or re-run with -Tool goose."
            }
            Write-Ok "Installed Goose $gooseVersion at '$($installedBin.FullName)' (GOOSE_HOME='$gooseHomeDir')."
        }
    }
}

Write-Ok "Optional tooling setup finished. Run .\kongor.ps1 doctor to verify detected integrations."
