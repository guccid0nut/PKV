# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes Release WILLOWMAKER (win-x64), writes deterministic content-hash willowmaker.buildinfo.json,
    and synchronizes games/client/win-x64 and games/client/windows-x64.

.DESCRIPTION
    Eliminates stale packaged WILLOWMAKER.exe drift (HZ-01) where games/client/win-x64/WILLOWMAKER.exe
    lags behind src/apps/willowmaker or src/extensions/modding edits.
    Uses deterministic source-tree SHA-256 hashing (immune to git pull/checkout timestamps) stored in
    willowmaker.buildinfo.json, and audits deployed native client DLL parity (vid_vulkan_x64.dll,
    d3d9.dll, version.dll) across games/client/ and games/client/windows-x64/.
    Supports -CheckOnly to audit binary freshness without building.

.EXAMPLE
    .\scripts\sync_willowmaker_client_bins.ps1
    .\scripts\sync_willowmaker_client_bins.ps1 -CheckOnly
#>

[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$csproj = Join-Path $repoRoot "src\apps\willowmaker\WILLOWMAKER.csproj"
$ridlessReleaseExe = Join-Path $repoRoot "src\apps\willowmaker\bin\$Configuration\net11.0\WILLOWMAKER.exe"
$targets = @(
    (Join-Path $repoRoot "games\client\win-x64"),
    (Join-Path $repoRoot "games\client\windows-x64")
)

$sourceDirs = @(
    (Join-Path $repoRoot "src\apps\willowmaker"),
    (Join-Path $repoRoot "src\extensions\modding")
)

function Get-FileSha256Lower {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return "" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-WillowmakerSourceTreeDigest {
    param(
        [string]$RootPath,
        [string[]]$Directories
    )
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($dir in $Directories) {
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        $found = Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.Extension -in @(".cs", ".axaml", ".csproj")) -and
                ($_.FullName -notmatch '\\(bin|obj)\\')
            }
        foreach ($f in $found) {
            $files.Add($f.FullName)
        }
    }

    $sorted = @($files | Sort-Object)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $sb = New-Object System.Text.StringBuilder
        foreach ($full in $sorted) {
            $rel = $full.Substring($RootPath.Length).TrimStart('\', '/') -replace '\\', '/'
            $bytes = [System.IO.File]::ReadAllBytes($full)
            $fHash = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
            [void]$sb.Append($rel).Append('|').Append($fHash).Append("`n")
        }
        $manifestBytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
        $treeHash = [System.BitConverter]::ToString($sha.ComputeHash($manifestBytes)).Replace("-", "").ToLowerInvariant()
        return [ordered]@{
            sourceTreeSha256 = $treeHash
            sourceFileCount  = $sorted.Count
        }
    } finally {
        $sha.Dispose()
    }
}

$sourceDigest = Get-WillowmakerSourceTreeDigest -RootPath $repoRoot -Directories $sourceDirs

if ($CheckOnly) {
    $stale = New-Object System.Collections.Generic.List[string]
    foreach ($dir in $targets) {
        $exe = Join-Path $dir "WILLOWMAKER.exe"
        $dll = Join-Path $dir "WILLOWMAKER.dll"
        $buildInfoPath = Join-Path $dir "willowmaker.buildinfo.json"

        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
            $stale.Add("MISSING: $exe")
            continue
        }
        if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
            $stale.Add("MISSING: $dll")
            continue
        }
        if (-not (Test-Path -LiteralPath $buildInfoPath -PathType Leaf)) {
            $stale.Add("MISSING BUILDINFO: $buildInfoPath (run .\scripts\sync_willowmaker_client_bins.ps1 to generate deterministic source-hash manifest)")
            continue
        }

        try {
            $info = Get-Content -LiteralPath $buildInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $recordedTreeHash = [string]$info.sourceTreeSha256
            $recordedDllHash = [string]$info.willowmakerDllSha256
            $actualDllHash = Get-FileSha256Lower -Path $dll

            if ($recordedTreeHash -ne [string]$sourceDigest.sourceTreeSha256) {
                $stale.Add("STALE SOURCE HASH in ${dir}: deployed sourceTreeSha256=$($recordedTreeHash.Substring(0, [Math]::Min(12, $recordedTreeHash.Length))) != current $($sourceDigest.sourceTreeSha256.Substring(0, 12)) ($($sourceDigest.sourceFileCount) source files).")
            }
            if (-not [string]::IsNullOrWhiteSpace($recordedDllHash) -and $recordedDllHash -ne $actualDllHash) {
                $actShort = $actualDllHash.Substring(0, [Math]::Min(12, $actualDllHash.Length))
                $recShort = $recordedDllHash.Substring(0, [Math]::Min(12, $recordedDllHash.Length))
                $stale.Add("BINARY TAMPER/DRIFT in ${dir}: WILLOWMAKER.dll SHA-256 ($actShort) != buildinfo ($recShort).")
            }
        } catch {
            $stale.Add("CORRUPT BUILDINFO: $buildInfoPath ($($_.Exception.Message))")
        }
    }

    # Audit native client DLL SHA-256 parity across games/client/ and games/client/windows-x64/
    $clientRoot = Join-Path $repoRoot "games\client"
    $clientWin64 = Join-Path $repoRoot "games\client\windows-x64"
    foreach ($dllName in @("vid_vulkan_x64.dll", "d3d9.dll", "version.dll")) {
        $p1 = Join-Path $clientRoot $dllName
        $p2 = Join-Path $clientWin64 $dllName
        if ((Test-Path -LiteralPath $p1 -PathType Leaf) -and (Test-Path -LiteralPath $p2 -PathType Leaf)) {
            $h1 = Get-FileSha256Lower -Path $p1
            $h2 = Get-FileSha256Lower -Path $p2
            if ($h1 -ne $h2) {
                $stale.Add("NATIVE DLL DRIFT: games/client/$dllName ($($h1.Substring(0,12))) != games/client/windows-x64/$dllName ($($h2.Substring(0,12))).")
            }
        }
    }

    if ($stale.Count -gt 0) {
        foreach ($msg in $stale) {
            Write-Warning "[WILLOWMAKER-SYNC] $msg"
        }
        Write-Error "Packaged WILLOWMAKER or native client binaries are stale/drifted. Run .\scripts\sync_willowmaker_client_bins.ps1 to republish."
        exit 1
    }

    Write-Host "[WILLOWMAKER-SYNC] All packaged WILLOWMAKER binaries and native client DLLs verified fresh (sourceTreeSha256=$($sourceDigest.sourceTreeSha256.Substring(0,12)), files=$($sourceDigest.sourceFileCount))." -ForegroundColor Green
    exit 0
}

# Prefer user-scoped .NET SDK when installed (matches global.json / git-pre-push.ps1)
$userDotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path -LiteralPath (Join-Path $userDotnetDir "dotnet.exe") -PathType Leaf) {
    $env:DOTNET_ROOT = $userDotnetDir
    if (($env:PATH -split ';')[0] -ne $userDotnetDir) {
        $env:PATH = "$userDotnetDir;$env:PATH"
    }
}

$publishDir = Join-Path $repoRoot "artifacts\willowmaker-sync\$RuntimeIdentifier"
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
[void](New-Item -ItemType Directory -Path $publishDir -Force)

Write-Host "[WILLOWMAKER-SYNC] Publishing WILLOWMAKER ($Configuration, $RuntimeIdentifier, PublishReadyToRun=false, sourceTreeSha256=$($sourceDigest.sourceTreeSha256.Substring(0,12)))..." -ForegroundColor Cyan
& dotnet publish $csproj -c $Configuration -r $RuntimeIdentifier --self-contained false -p:PublishReadyToRun=false -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for WILLOWMAKER (exit $LASTEXITCODE). Run: dotnet build src\apps\willowmaker\WILLOWMAKER.csproj -c $Configuration"
}

$lockedRidless = Get-Process -Name "WILLOWMAKER" -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.Equals($ridlessReleaseExe, [System.StringComparison]::OrdinalIgnoreCase)
}
if ($lockedRidless) {
    Write-Warning "[WILLOWMAKER-SYNC] Skipping RID-less build of '$ridlessReleaseExe' because an active WILLOWMAKER.exe (PID $($lockedRidless.Id -join ',')) currently locks it. Close WILLOWMAKER and re-run to refresh bin\$Configuration\net11.0\."
} else {
    Write-Host "[WILLOWMAKER-SYNC] Refreshing RID-less build ($Configuration, net11.0)..." -ForegroundColor Cyan
    & dotnet build $csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "[WILLOWMAKER-SYNC] RID-less dotnet build returned exit $LASTEXITCODE; continuing with win-x64 publish sync."
    }
}

$publishedFiles = Get-ChildItem -LiteralPath $publishDir -File
$publishedDllSha = Get-FileSha256Lower -Path (Join-Path $publishDir "WILLOWMAKER.dll")
$publishedExeSha = Get-FileSha256Lower -Path (Join-Path $publishDir "WILLOWMAKER.exe")
$buildInfoObj = [ordered]@{
    schemaVersion        = 1
    sourceTreeSha256     = $sourceDigest.sourceTreeSha256
    sourceFileCount      = $sourceDigest.sourceFileCount
    willowmakerDllSha256 = $publishedDllSha
    willowmakerExeSha256 = $publishedExeSha
    configuration        = $Configuration
    runtimeIdentifier    = $RuntimeIdentifier
    publishedAtUtc       = [DateTimeOffset]::UtcNow.ToString("o")
}
$buildInfoJson = $buildInfoObj | ConvertTo-Json -Depth 4

foreach ($targetDir in $targets) {
    if (-not (Test-Path -LiteralPath $targetDir)) {
        [void](New-Item -ItemType Directory -Path $targetDir -Force)
    }
    $copiedCount = 0
    foreach ($f in $publishedFiles) {
        Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $targetDir $f.Name) -Force
        $copiedCount++
    }
    $subDirs = Get-ChildItem -LiteralPath $publishDir -Directory
    foreach ($d in $subDirs) {
        $destSub = Join-Path $targetDir $d.Name
        if (-not (Test-Path -LiteralPath $destSub)) {
            [void](New-Item -ItemType Directory -Path $destSub -Force)
        }
        Copy-Item -Path (Join-Path $d.FullName "*") -Destination $destSub -Recurse -Force
    }
    $buildInfoDest = Join-Path $targetDir "willowmaker.buildinfo.json"
    Set-Content -LiteralPath $buildInfoDest -Value $buildInfoJson -Encoding UTF8
    $exeInfo = Get-Item -LiteralPath (Join-Path $targetDir "WILLOWMAKER.exe")
    Write-Host "[WILLOWMAKER-SYNC] Deployed $copiedCount files + willowmaker.buildinfo.json to '$targetDir' (WILLOWMAKER.exe=$($exeInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')), sourceTreeSha256=$($sourceDigest.sourceTreeSha256.Substring(0,12)))." -ForegroundColor Green
}

# Mirror primary native client DLLs from games/client/ to games/client/windows-x64/ if drifted so sync resolves every -CheckOnly failure
$clientRoot = Join-Path $repoRoot "games\client"
$clientWin64 = Join-Path $repoRoot "games\client\windows-x64"
foreach ($dllName in @("vid_vulkan_x64.dll", "d3d9.dll", "version.dll")) {
    $srcDll = Join-Path $clientRoot $dllName
    $dstDll = Join-Path $clientWin64 $dllName
    if ((Test-Path -LiteralPath $srcDll -PathType Leaf) -and (Test-Path -LiteralPath $clientWin64 -PathType Container)) {
        $srcH = Get-FileSha256Lower -Path $srcDll
        $dstH = Get-FileSha256Lower -Path $dstDll
        if ($srcH -ne $dstH) {
            Copy-Item -LiteralPath $srcDll -Destination $dstDll -Force
            Write-Host "[WILLOWMAKER-SYNC] Mirrored native client DLL '$dllName' from games/client/ to games/client/windows-x64/ ($($srcH.Substring(0,12)))." -ForegroundColor Green
        }
    }
}

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
exit 0
