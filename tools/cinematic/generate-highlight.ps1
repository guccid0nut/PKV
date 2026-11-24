# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Automated Game-Winning Teamfight Highlight Clip Generator Runner (PowerShell).

.DESCRIPTION
    Analyzes a .honreplay match archive, discovers the decisive game-winning teamfight,
    identifies the winning MVP hero, and renders a 1080p 60FPS highlight MP4 clip.

.PARAMETER Replay
    Path to the input .honreplay file. Defaults to auto-discovering in games/client/.

.PARAMETER Output
    Path to the output .mp4 file. Defaults to output/highlights/<matchid>_teamfight_<hero>.mp4.

.PARAMETER Player
    Player number (1..10) or hero/player name to override camera locking. Defaults to the MVP hero.

.PARAMETER Duration
    Highlight clip duration in seconds (default: 45.0).

.PARAMETER Resolution
    Target video resolution (default: "1920x1080").

.PARAMETER Fps
    Target video framerate (default: 60).

.PARAMETER DryRun
    Analyze and display detected highlight statistics without rendering video.

.PARAMETER VerboseLogging
    Enable verbose telemetry logging.

.EXAMPLE
    .\generate-highlight.ps1 -Replay "games/client/M0000000001.honreplay" -Duration 45
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0, HelpMessage = "Path to the .honreplay file")]
    [string]$Replay = "",

    [Parameter(Position = 1, HelpMessage = "Path to the output .mp4 file")]
    [string]$Output = "",

    [Parameter(HelpMessage = "Player index (1..10) or hero/player name for camera locking")]
    [string]$Player = "",

    [Parameter(HelpMessage = "Duration of highlight in seconds")]
    [double]$Duration = 45.0,

    [Parameter(HelpMessage = "Output resolution (e.g. 1920x1080)")]
    [string]$Resolution = "1920x1080",

    [Parameter(HelpMessage = "Framerate in FPS")]
    [int]$Fps = 60,

    [Parameter(HelpMessage = "Inspect highlight without rendering video")]
    [switch]$DryRun,

    [Parameter(HelpMessage = "Enable verbose telemetry logging")]
    [switch]$VerboseLogging
)

$repoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = $PWD.Path
}
# Navigate up to repo root from tools/cinematic if needed
if (Test-Path (Join-Path $repoRoot "..\..\scripts\qa\render-replay.ps1")) {
    $repoRoot = (Resolve-Path (Join-Path $repoRoot "..\..")).Path
}
$cliScript = Join-Path $repoRoot "tools\cinematic\highlight_cli.py"
if (-not (Test-Path -Path $cliScript -PathType Leaf)) {
    $cliScript = Join-Path $repoRoot "src\tools\cinematic\highlight_cli.py"
}
if (-not (Test-Path -Path $cliScript -PathType Leaf)) {
    Write-Host "[ERROR] Highlight CLI script not found at: $cliScript" -ForegroundColor Red
    exit 1
}

$argsList = @()
if (-not [string]::IsNullOrWhiteSpace($Replay)) {
    $argsList += @("-r", $Replay)
}
if (-not [string]::IsNullOrWhiteSpace($Output)) {
    $argsList += @("-o", $Output)
}
if (-not [string]::IsNullOrWhiteSpace($Player)) {
    $argsList += @("-p", $Player)
}
if ($Duration -gt 0) {
    $argsList += @("-d", $Duration.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}
if (-not [string]::IsNullOrWhiteSpace($Resolution)) {
    $argsList += @("--resolution", $Resolution)
}
if ($Fps -gt 0) {
    $argsList += @("--fps", $Fps.ToString())
}
if ($DryRun) {
    $argsList += @("--dry-run")
}
if ($VerboseLogging) {
    $argsList += @("-v")
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " 🎬 PROJECT KONGOR: GAME-WINNING TEAMFIGHT HIGHLIGHT CLIPPER" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

& python $cliScript @argsList
exit $LASTEXITCODE
