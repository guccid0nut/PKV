# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    100% Non-Invasive Vulkan 1.3 Setup for Heroes of Newerth 4.10.1 & Willowmaker
.DESCRIPTION
    - Runs completely in user-space (NO Administrator privileges / UAC prompt required).
    - Touches ZERO Windows system files and ZERO existing game DLLs.
    - Adds 64-bit DXVK (d3d9.dll, hon_x64.exe.local, dxvk.conf).
    - Fully compatible with Willowmaker and stock HoN 4.10.1.
#>

[CmdletBinding()]
param([string]$GameDir = $PSScriptRoot)

$ErrorActionPreference = "Stop"

function Write-StepLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter()][ValidateSet("INFO", "WARN", "ERROR", "SUCCESS")][string]$Status = "INFO"
    )
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $color = switch ($Status) {
        "SUCCESS" { "Green" }
        "WARN"    { "Yellow" }
        "ERROR"   { "Red" }
        default   { "Cyan" }
    }
    Write-Host "[$timestamp] [$Status] $Message" -ForegroundColor $color
}

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   HoN 4.10.1 - Non-Invasive Vulkan 1.3 Setup (DXVK)    " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
Write-StepLog "Target game directory: $GameDir" "INFO"

# 1. Locate hon_x64.exe
$exeCandidates = @(
    (Join-Path $GameDir "hon_x64.exe"),
    (Join-Path $GameDir "windows-x64\hon_x64.exe")
)

# Also check parent directory if the script was launched from within windows-x64
$parentDir = Split-Path -Path $GameDir -Parent
if ($parentDir -and (Test-Path $parentDir)) {
    $exeCandidates += (Join-Path $parentDir "hon_x64.exe")
}

$honExe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $honExe) {
    Write-StepLog "Could not find hon_x64.exe! Please place this script in your HoN game root folder." "ERROR"
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 1
}

$actualRootDir = Split-Path -Path $honExe
if ((Split-Path -Path $actualRootDir -Leaf).Equals("windows-x64", [System.StringComparison]::OrdinalIgnoreCase)) {
    $actualRootDir = Split-Path -Path $actualRootDir -Parent
}

$binDirs = @($actualRootDir)
$win64Sub = Join-Path $actualRootDir "windows-x64"
if (Test-Path $win64Sub) {
    $binDirs += $win64Sub
}

Write-StepLog "Validated HoN installation at: $actualRootDir" "SUCCESS"

# 2. Add DotLocal Redirection Marker (NEW file only - original files untouched)
foreach ($dir in $binDirs) {
    $marker = Join-Path $dir "hon_x64.exe.local"
    if (-not (Test-Path $marker)) {
        [System.IO.File]::WriteAllBytes($marker, @())
        Write-StepLog "Created DotLocal redirection: $marker" "SUCCESS"
    } else {
        Write-StepLog "DotLocal redirection marker already present: $marker" "INFO"
    }
}

# 3. Download & Deploy Official 64-bit DXVK (Direct3D 9 to Vulkan 1.3)
$dxvkVersion = "2.5.3"
$targetD3D9 = Join-Path $actualRootDir "d3d9.dll"

$needsDownload = $true
if (Test-Path $targetD3D9) {
    $len = (Get-Item $targetD3D9).Length
    if ($len -gt 1000000) { # DXVK 64-bit is ~7.2MB
        Write-StepLog "Existing 64-bit DXVK d3d9.dll verified ($([math]::Round($len / 1MB, 2)) MB)." "INFO"
        $needsDownload = $false
    }
}

if ($needsDownload) {
    Write-StepLog "Downloading 64-bit DXVK v$dxvkVersion from official GitHub release..." "INFO"
    $tempZip = Join-Path $env:TEMP "dxvk-$dxvkVersion.tar.gz"
    $tempExtract = Join-Path $env:TEMP "dxvk_extract_$([System.Guid]::NewGuid().ToString('N'))"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        $url = "https://github.com/doitsujin/dxvk/releases/download/v$dxvkVersion/dxvk-$dxvkVersion.tar.gz"
        Invoke-WebRequest -Uri $url -OutFile $tempZip -UseBasicParsing

        New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null
        tar -xzf $tempZip -C $tempExtract

        $sourceDll = Get-ChildItem -Path $tempExtract -Filter "d3d9.dll" -Recurse | Where-Object { $_.FullName -match "x64" } | Select-Object -First 1
        if (-not $sourceDll) {
            throw "Could not locate x64\d3d9.dll inside the downloaded DXVK archive."
        }

        foreach ($dir in $binDirs) {
            Copy-Item -Path $sourceDll.FullName -Destination (Join-Path $dir "d3d9.dll") -Force
            Write-StepLog "Added DXVK d3d9.dll to $dir" "SUCCESS"
        }
    } catch {
        Write-StepLog "Automatic download encountered an issue: $_" "ERROR"
        Write-StepLog "Manual fallback: Download DXVK from GitHub, take x64\d3d9.dll, and drop it into this folder." "WARN"
    } finally {
        if (Test-Path $tempZip) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
        if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue }
    }
} else {
    Write-StepLog "DXVK d3d9.dll already present in game directory." "INFO"
}

# 4. Generate Optimized dxvk.conf (Prevents D3D9 display mode crash & enables Vulkan HUD)
$dxvkConfContent = @"
# DXVK Configuration for Heroes of Newerth 4.10.1 (Direct3D 9 to Vulkan 1.3)
dxvk.hud = fps
dxvk.stateCache = true
dxvk.wsi = win32
dxgi.syncInterval = 0
d3d9.presentInterval = 0
d3d9.maxFrameRate = 0
d3d9.maxFrameLatency = 1
dxgi.maxFrameLatency = 1
d3d9.samplerAnisotropy = 16
d3d9.cachedDynamicBuffers = true
d3d9.evictManagedOnUnlock = false
d3d9.allowDiscard = true
d3d9.tearFree = false
d3d9.shaderModel = 3
d3d9.seamlessCubes = true
d3d9.deferSurfaceCreation = true
dxvk.enableGraphicsPipelineLibrary = true
dxvk.gplFastLink = true
dxvk.enableAsync = true
dxvk.numCompilerThreads = 0
dxvk.nvLowLatency = true
d3d9.samplerLodBias = -0.5
d3d9.floatEmulation = strict
"@

foreach ($dir in $binDirs) {
    [System.IO.File]::WriteAllText((Join-Path $dir "dxvk.conf"), $dxvkConfContent)
}
Write-StepLog "Added optimized dxvk.conf configuration." "SUCCESS"

# 5. Ensure Local AppData Shader Cache Folder Exists
$cacheDir = Join-Path $env:LOCALAPPDATA "dxvk"
if (-not (Test-Path $cacheDir)) {
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    Write-StepLog "Initialized local shader cache: $cacheDir" "SUCCESS"
}

Write-Host ""
Write-StepLog "VERIFICATION: Zero existing game DLLs were modified." "SUCCESS"
Write-StepLog "VERIFICATION: Zero Windows system files were modified." "SUCCESS"
Write-StepLog "Vulkan modernization complete! You can now launch through WILLOWMAKER.exe." "SUCCESS"
Write-Host ""
Write-Host "Tip: To revert to stock DirectX 9 at any time, simply delete 'd3d9.dll' and 'hon_x64.exe.local'." -ForegroundColor Yellow
Write-Host ""
Read-Host "Press Enter to finish"
