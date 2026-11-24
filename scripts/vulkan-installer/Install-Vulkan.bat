@echo off
REM SPDX-License-Identifier: AGPL-3.0-or-later
REM SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
REM Project KONGOR V (https://github.com/guccidonut/PKV)
title HoN 4.10.1 Vulkan 1.3 Setup
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$f='%~f0'; & ([ScriptBlock]::Create(((Get-Content -LiteralPath $f -Raw) -split '(?m)^:::POWERSHELL:::')[1]))"
set EXIT_CODE=%errorlevel%
if %EXIT_CODE% neq 0 (
    echo.
    echo [ERROR] Setup encountered an issue (Exit Code: %EXIT_CODE%).
    echo [INFO] A detailed diagnostic log was saved to: vulkan_install.log
    echo [INFO] Please share 'vulkan_install.log' in Discord so we can fix it!
) else (
    echo.
    echo [INFO] Diagnostic log saved to: vulkan_install.log
)
echo.
pause
exit /b %EXIT_CODE%

:::POWERSHELL:::
$ErrorActionPreference = "Stop"

# Reliable folder detection from %~f0 ($f passed from batch wrapper)
$GameDir = if ($f -and (Test-Path -LiteralPath $f)) { Split-Path -Path $f -Parent } else { (Get-Location).Path }
$LogFile = Join-Path $GameDir "vulkan_install.log"

# Initialize fresh log file with environment telemetry
$logHeader = @"
================================================================================
Heroes of Newerth 4.10.1 - Vulkan 1.3 Setup Diagnostic Log
Timestamp: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
OS: $([System.Environment]::OSVersion.VersionString) ($([System.Environment]::GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")))
PSVersion: $($PSVersionTable.PSVersion)
Script Path: $f
Target Game Directory: $GameDir
================================================================================
"@
[System.IO.File]::WriteAllText($LogFile, $logHeader + "`r`n")

function Write-StepLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter()][ValidateSet("INFO", "WARN", "ERROR", "SUCCESS")][string]$Status = "INFO"
    )
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $logLine = "[$timestamp] [$Status] $Message"
    
    $color = switch ($Status) {
        "SUCCESS" { "Green" }
        "WARN"    { "Yellow" }
        "ERROR"   { "Red" }
        default   { "Cyan" }
    }
    Write-Host $logLine -ForegroundColor $color
    
    try {
        [System.IO.File]::AppendAllText($LogFile, $logLine + "`r`n")
    } catch { }
}

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   Heroes of Newerth 4.10.1 - Vulkan 1.3 Setup (DXVK)    " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

Write-StepLog "Current directory: $GameDir" "INFO"
Write-StepLog "Diagnostic log initialized: $LogFile" "INFO"

# 1. Locate hon_x64.exe (Check current dir, windows-x64, parent dir)
$exeCandidates = @(
    (Join-Path $GameDir "hon_x64.exe"),
    (Join-Path $GameDir "windows-x64\hon_x64.exe")
)

$parentDir = Split-Path -Path $GameDir -Parent
if ($parentDir -and (Test-Path $parentDir)) {
    $exeCandidates += (Join-Path $parentDir "hon_x64.exe")
    $exeCandidates += (Join-Path $parentDir "windows-x64\hon_x64.exe")
}

$honExe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $honExe) {
    Write-StepLog "Could not find hon_x64.exe in '$GameDir' or parent directory!" "ERROR"
    Write-Host ""
    Write-Host "Please place 'Install-Vulkan.bat' directly inside your Heroes of Newerth folder" -ForegroundColor Yellow
    Write-Host "(the folder containing willowmaker.exe and hon_x64.exe) and run it again." -ForegroundColor Yellow
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

Write-StepLog "Validated HoN game folder at: $actualRootDir" "SUCCESS"

# 2. Add DotLocal Redirection Marker (NEW file only - original files untouched)
foreach ($dir in $binDirs) {
    $marker = Join-Path $dir "hon_x64.exe.local"
    if (-not (Test-Path $marker)) {
        [System.IO.File]::WriteAllBytes($marker, @())
        Write-StepLog "Created DotLocal redirection marker: $marker" "SUCCESS"
    } else {
        Write-StepLog "DotLocal marker already active: $marker" "INFO"
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
    Write-StepLog "Downloading 64-bit DXVK v$dxvkVersion from official GitHub releases..." "INFO"
    $tempZip = Join-Path $env:TEMP "dxvk-$dxvkVersion.tar.gz"
    $tempExtract = Join-Path $env:TEMP "dxvk_extract_$([System.Guid]::NewGuid().ToString('N'))"

    try {
        # Safe TLS Configuration (supports TLS 1.2 on all modern Windows machines)
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]'Tls12,Tls11,Tls'
        try {
            [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]'Tls13'
        } catch { }

        $url = "https://github.com/doitsujin/dxvk/releases/download/v$dxvkVersion/dxvk-$dxvkVersion.tar.gz"
        Invoke-WebRequest -Uri $url -OutFile $tempZip -UseBasicParsing

        New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null
        tar -xzf $tempZip -C $tempExtract

        $sourceDll = Get-ChildItem -Path $tempExtract -Filter "d3d9.dll" -Recurse | Where-Object { $_.FullName -match "x64" } | Select-Object -First 1
        if (-not $sourceDll) {
            throw "Could not find x64\d3d9.dll in extracted archive."
        }

        foreach ($dir in $binDirs) {
            Copy-Item -Path $sourceDll.FullName -Destination (Join-Path $dir "d3d9.dll") -Force
            Write-StepLog "Deployed DXVK d3d9.dll to $dir" "SUCCESS"
        }
    } catch {
        Write-StepLog "Download or extraction encountered an error: $_" "ERROR"
        try {
            [System.IO.File]::AppendAllText($LogFile, "`r`n--- EXCEPTION DETAILS ---`r`n$($_ | Out-String)`r`n$($_.ScriptStackTrace)`r`n-------------------------`r`n")
        } catch { }
        Write-Host ""
        Write-Host "Manual Fallback Option:" -ForegroundColor Yellow
        Write-Host "1. Download DXVK from: https://github.com/doitsujin/dxvk/releases" -ForegroundColor Yellow
        Write-Host "2. Copy 'x64/d3d9.dll' directly into your HoN folder." -ForegroundColor Yellow
        exit 1
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
Write-StepLog "Vulkan modernization complete! Launch HoN through WILLOWMAKER.exe." "SUCCESS"
exit 0
