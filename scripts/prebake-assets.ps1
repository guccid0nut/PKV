# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Automated pre-baking and validation pipeline for Project Kongor in-game entity assets.
.DESCRIPTION
    Executes the prebake-tool CLI to extract, serialize, and bundle game entity definitions
    (heroes, alt avatars, abilities, states, projectiles, items, and cosmetics), validates output artifacts
    (entity_cache.bin, hero_definitions_cache.lua, VFS overrides), and outputs structured telemetry tables.
.PARAMETER GameDir
    Path to the game client directory (default: games/client).
.PARAMETER SharedDir
    Path to shared game entities (default: games/shared).
.PARAMETER OutputDir
    Target directory for VFS overrides (default: games/client/override).
.PARAMETER LogFile
    Path to write structured NDJSON diagnostic telemetry.
.PARAMETER TargetMode
    Pre-baking target: 'vfs', 's2z', or 'both' (default: 'both').
.PARAMETER CompressionLevel
    Zstandard compression level (1-22, default: 3).
.PARAMETER Validate
    Validate binary cache and Lua checksums after generation.
.PARAMETER Clean
    Clean override directories prior to running the prebake pipeline.
.PARAMETER DryRun
    Simulate execution without modifying files on disk.
.PARAMETER VerboseOutput
    Enable detailed verbose logging output.
.PARAMETER Configuration
    Build configuration to use when running prebake-tool (Debug/Release).
.PARAMETER PassThru
    Outputs the structured PSCustomObject result to the pipeline.
#>
[CmdletBinding()]
param (
    [Parameter()]
    [string]$GameDir,

    [Parameter()]
    [string]$SharedDir,

    [Parameter()]
    [string]$OutputDir,

    [Parameter()]
    [string]$LogFile,

    [Parameter()]
    [ValidateSet('vfs', 's2z', 'both')]
    [string]$TargetMode = 'both',

    [Parameter()]
    [ValidateRange(1, 22)]
    [int]$CompressionLevel = 3,

    [Parameter()]
    [switch]$Validate = $true,

    [Parameter()]
    [switch]$Clean,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$VerboseOutput,

    [Parameter()]
    [string]$Configuration = 'Debug',

    [Parameter()]
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Project Kongor Pre-Baking & Diagnostic Telemetry Pipeline " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Resolve Root and Working Paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ScriptDir) {
    $ScriptDir = (Get-Location).Path
}

$RepoRoot = $ScriptDir
if (Test-Path (Join-Path $ScriptDir "..\Directory.Build.props")) {
    $RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
}

if (-not $GameDir) {
    $GameDir = (Join-Path $RepoRoot "games\client")
}
if (-not $SharedDir) {
    $PotentialShared = (Join-Path $RepoRoot "games\shared")
    if (Test-Path $PotentialShared) {
        $SharedDir = $PotentialShared
    }
}
if (-not $OutputDir) {
    throw "Prebake: -OutputDir is mandatory; default override is disabled to protect active assets (plan v4 P3.0)."
}
if (-not $LogFile) {
    $LogFile = (Join-Path $RepoRoot "logs\prebake-telemetry.ndjson")
}

Write-Host "[INIT] Repository Root : $RepoRoot" -ForegroundColor Gray
Write-Host "[INIT] Game Client Dir : $GameDir" -ForegroundColor Gray
Write-Host "[INIT] Shared Dir      : $(if ($SharedDir) { $SharedDir } else { '<None - using .s2z archives>' })" -ForegroundColor Gray
Write-Host "[INIT] Override Output : $OutputDir" -ForegroundColor Gray
Write-Host "[INIT] Telemetry Log   : $LogFile" -ForegroundColor Gray
Write-Host "[INIT] Target Mode     : $TargetMode" -ForegroundColor Gray
Write-Host "[INIT] Compression Lvl : $CompressionLevel" -ForegroundColor Gray
Write-Host "[INIT] Validate Cache  : $Validate" -ForegroundColor Gray
Write-Host "[INIT] Dry Run Mode    : $DryRun" -ForegroundColor Gray

# Ensure Log directory exists
$LogDir = Split-Path -Parent $LogFile
if ($LogDir -and -not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

# Phase 3b (#108 manimgx-receipt-last-cache): Transitive-Closure Cache Keying & Receipt-Last Publication
function Get-TransitiveAssetCacheKey {
    param(
        [Parameter(Mandatory=$true)][string[]]$InputPaths,
        [string]$TargetModeName = "both",
        [int]$CompressionLvl = 3
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("schema=kongor-transitive-cache-v1|mode=$TargetModeName|comp=$CompressionLvl")
    foreach ($p in ($InputPaths | Sort-Object)) {
        if (Test-Path -LiteralPath $p) {
            $item = Get-Item -LiteralPath $p
            if (-not $item.PSIsContainer) {
                $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
                [void]$sb.AppendLine("$($item.FullName)|$($item.Length)|$h")
            }
        }
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
    } finally {
        $sha.Dispose()
    }
}

function Write-AtomicCacheOutput {
    param(
        [Parameter(Mandatory=$true)][string]$TargetPath,
        [Parameter(Mandatory=$true)][byte[]]$ContentBytes,
        [Parameter(Mandatory=$true)][string]$CacheKey
    )
    $parentDir = [System.IO.Path]::GetDirectoryName($TargetPath)
    if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    $tmpPath = "$TargetPath.tmp.$PID"
    [System.IO.File]::WriteAllBytes($tmpPath, $ContentBytes)
    Move-Item -LiteralPath $tmpPath -Destination $TargetPath -Force

    $fileSha = (Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $receiptPath = "$TargetPath.receipt.json"
    $receiptObj = [ordered]@{
        schema_version = "1.0.0"
        cache_key      = $CacheKey
        size_bytes     = $ContentBytes.Length
        sha256         = $fileSha
        timestamp      = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    $receiptTmp = "$receiptPath.tmp.$PID"
    [System.IO.File]::WriteAllText($receiptTmp, ($receiptObj | ConvertTo-Json -Depth 4) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $receiptTmp -Destination $receiptPath -Force
    return $receiptPath
}

# 2. Locate Prebake CLI Tool (Prefer published/release binary, fallback to dotnet run)
$PrebakeDll = Join-Path $RepoRoot "src\tools\prebake-tool\bin\$Configuration\net11.0\Kongor.PrebakeTool.dll"
$PrebakeCsproj = Join-Path $RepoRoot "src\tools\prebake-tool\prebake-tool.csproj"
$PrebakeCmd = "dotnet"

if (Test-Path $PrebakeDll) {
    Write-Host "[TOOL] Using compiled binary: $PrebakeDll" -ForegroundColor Green
    $PrebakeArgs = @("$PrebakeDll")
} elseif (Test-Path $PrebakeCsproj) {
    Write-Host "[TOOL] Using dotnet run with project: $PrebakeCsproj" -ForegroundColor Green
    $PrebakeArgs = @("run", "--project", "$PrebakeCsproj", "-c", "$Configuration", "--")
} else {
    Write-Warning "[TOOL] prebake-tool not found at candidate locations. Attempting generic dotnet run..."
    $PrebakeArgs = @("run", "--project", "src/tools/prebake-tool/prebake-tool.csproj", "--")
}

# 3. Optional Clean Step
if ($Clean) {
    $fullOverride = (Join-Path $GameDir "override")
    if ($OutputDir -like "$fullOverride*" -or $OutputDir -eq $fullOverride) {
        throw "Prebake: -Clean is strictly forbidden when OutputDir is inside games/client/override (plan v4 P3.0)."
    }
    Write-Host "[CLEAN] Cleaning destination directory..." -ForegroundColor Yellow
    $CleanArgs = $PrebakeArgs + @("clean", "--game-dir", "$GameDir")
    if ($VerboseOutput) { $CleanArgs += "--verbose" }
    & $PrebakeCmd $CleanArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Clean step failed with exit code $LASTEXITCODE"
    }
}

# 4. Execute Pre-Bake Pipeline
$RunningGameProcesses = Get-Process -Name "hon_x64", "hon" -ErrorAction SilentlyContinue
if ($RunningGameProcesses -and ($TargetMode -in @('s2z', 'both')) -and -not $DryRun) {
    Write-Warning "[LOCK] Detected running game client instance(s): $($RunningGameProcesses.ProcessName) (PID: $(($RunningGameProcesses.Id) -join ', '))."
    Write-Warning "[LOCK] The game client holds a file lock on 'resources0_prebaked.s2z'."
    Write-Warning "[LOCK] To rebuild S2Z archives, please close the game client, or run with '-TargetMode vfs' to stage via VFS override."
}

$PipelineArgs = $PrebakeArgs + @(
    "all",
    "--game-dir", "$GameDir"
)
if ($SharedDir -and (Test-Path $SharedDir)) {
    $PipelineArgs += @("--shared-dir", "$SharedDir")
}
$PipelineArgs += @(
    "--output-dir", "$OutputDir",
    "--target-mode", "$TargetMode",
    "--compression-level", "$CompressionLevel",
    "--log-file", "$LogFile"
)

if ($Validate) {
    $PipelineArgs += "--validate"
}
if ($DryRun) {
    $PipelineArgs += "--dry-run"
}
if ($VerboseOutput) {
    $PipelineArgs += "--verbose"
}

$StartTimestamp = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "[EXEC] Invoking prebake-tool pipeline..." -ForegroundColor Cyan
& $PrebakeCmd $PipelineArgs
$PrebakeExitCode = $LASTEXITCODE
$StartTimestamp.Stop()
$ExecutionDurationMs = $StartTimestamp.ElapsedMilliseconds

if ($PrebakeExitCode -ne 0) {
    Write-Error "prebake-tool failed with exit code $PrebakeExitCode"
    exit $PrebakeExitCode
}

# 5. Validation of Generated Artifacts
Write-Host "[VERIFY] Verifying generated pre-baked artifacts..." -ForegroundColor Cyan

$EntityCacheBinPath = Join-Path $OutputDir "prebaked_assets\entity_cache.bin"
$LuaCachePath = Join-Path $OutputDir "ui\scripts\newui\hero_definitions_cache.lua"

$BinExists = Test-Path $EntityCacheBinPath
$LuaExists = Test-Path $LuaCachePath
$BinSize = 0
$LuaSize = 0

if ($BinExists) {
    $BinItem = Get-ChildItem -Path $EntityCacheBinPath
    $BinSize = $BinItem.Length
    $BinSizeKb = [Math]::Round($BinSize / 1024, 2)
    Write-Host "  [+] entity_cache.bin present ($BinSizeKb KB)" -ForegroundColor Green
} elseif (-not $DryRun) {
    Write-Warning "  [-] entity_cache.bin NOT FOUND at $EntityCacheBinPath"
}

if ($LuaExists) {
    $LuaItem = Get-ChildItem -Path $LuaCachePath
    $LuaSize = $LuaItem.Length
    $LuaSizeKb = [Math]::Round($LuaSize / 1024, 2)
    # Validate Lua header structure using Select-String
    $LuaMatch = Get-Content -Path $LuaCachePath | Select-String -Pattern "HeroDefinitions"
    if ($LuaMatch) {
        Write-Host "  [+] hero_definitions_cache.lua valid ($LuaSizeKb KB, syntax signature matched)" -ForegroundColor Green
    } else {
        Write-Warning "  [!] hero_definitions_cache.lua present but 'HeroDefinitions' signature missing"
    }
} elseif (-not $DryRun) {
    Write-Warning "  [-] hero_definitions_cache.lua NOT FOUND at $LuaCachePath"
}

# 6. Parse Structured NDJSON Telemetry Log
$LatestTelemetryEvent = $null
if (Test-Path $LogFile) {
    $JsonLines = Get-Content -Path $LogFile | Select-String -Pattern '\{.*\}'
    if ($JsonLines) {
        foreach ($line in $JsonLines) {
            try {
                $parsed = $line.Line | ConvertFrom-Json
                if ($parsed.event_name -eq "PrebakePipelineExecuted") {
                    $LatestTelemetryEvent = $parsed
                }
            } catch {
                # Ignore non-JSON or partial line
            }
        }
    }
}

# 7. Construct and Format Structured Status Output
$StatusObject = [PSCustomObject]@{
    Status              = if ($PrebakeExitCode -eq 0) { "SUCCESS" } else { "FAILED" }
    ExecutionDurationMs = $ExecutionDurationMs
    TotalHeroes         = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.heroes_count } else { 0 }
    TotalAltAvatars     = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.avatars_count } else { 0 }
    TotalAbilities      = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.abilities_count } else { 0 }
    TotalStates         = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.states_count } else { 0 }
    TotalProjectiles    = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.projectiles_count } else { 0 }
    TotalItems          = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.items_count } else { 0 }
    TotalCosmetics      = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.cosmetics_count } else { 0 }
    PreloadResources    = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.preload_resources_count } else { 0 }
    BinaryCacheSizeBytes= if ($LatestTelemetryEvent) { $LatestTelemetryEvent.binary_bytes } else { $BinSize }
    LuaCacheSizeBytes   = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.lua_bytes } else { $LuaSize }
    ChecksumXxHash3     = if ($LatestTelemetryEvent) { $LatestTelemetryEvent.checksum } else { "N/A" }
    TargetMode          = $TargetMode
    ArtifactsValid      = ($BinExists -or $DryRun) -and ($LuaExists -or $DryRun)
}

Write-Host "`n=== Pre-Baking Pipeline Status Summary ===" -ForegroundColor Yellow
$StatusTable = [PSCustomObject]@{
    "Status"            = $StatusObject.Status
    "Duration (ms)"     = $StatusObject.ExecutionDurationMs
    "Heroes"            = $StatusObject.TotalHeroes
    "Avatars"           = $StatusObject.TotalAltAvatars
    "Abilities"         = $StatusObject.TotalAbilities
    "States"            = $StatusObject.TotalStates
    "Projectiles"       = $StatusObject.TotalProjectiles
    "Items"             = $StatusObject.TotalItems
    "Cosmetics"         = $StatusObject.TotalCosmetics
    "Cache Size (KB)"   = [Math]::Round($StatusObject.BinaryCacheSizeBytes / 1024, 2)
    "Lua Size (KB)"     = [Math]::Round($StatusObject.LuaCacheSizeBytes / 1024, 2)
    "Checksum"          = $StatusObject.ChecksumXxHash3
    "Valid"             = $StatusObject.ArtifactsValid
}

$StatusTable | Format-Table -AutoSize

Write-Host "[SUCCESS] Pre-Baking and Diagnostic Telemetry Pipeline Finished Successfully.`n" -ForegroundColor Green

if ($PassThru) {
    return $StatusObject
}
