# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Project Kongor K2 Engine Texture Pipeline & Shadowing Auditor.

.DESCRIPTION
    Audits textures in games/client/override against legacy games/client/game/textures.s2z entries
    to detect silent shadowing hazards caused by CTextureArchive stem resolution (vid_d3d9_x64.dll).
    Skips byte-identical vanilla copies while flagging divergent .dds overrides and modified
    cross-extension (.png/.tga) collisions. Also validates DDS headers, Power-of-Two (POT)
    dimensions, and K2 normal map pipelines (tangent RGB .tga sources vs .agb DXT5 _rxgb.dds).

.PARAMETER TargetDir
    Directory to scan. Defaults to "games/client/override".

.PARAMETER ClientRoot
    Path to games/client root. Defaults to "games/client".

.PARAMETER ChannelMode
    Audit mode filter: "All", "UIRgba", or "RxgbNormal". Defaults to "All".

.PARAMETER Report
    Optional output path for a Markdown audit report.

.PARAMETER JsonOutput
    Optional output path for JSON telemetry results.

.PARAMETER Filter
    Substring to filter audited VFS paths.

.PARAMETER Strict
    Exit with non-zero code if high-risk UI shadowing, corrupt headers, or invalid/double-swizzled normal maps are found.

.PARAMETER Detailed
    Emit verbose per-file shadowing matches to stdout.

.EXAMPLE
    .\scripts\assets\audit_k2_textures.ps1 -ChannelMode UIRgba
    .\scripts\assets\audit_k2_textures.ps1 -ChannelMode RxgbNormal -Strict
    .\scripts\assets\audit_k2_textures.ps1 -Report docs/TEXTURE_AUDIT.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$TargetDir = "games/client/override",

    [Parameter(Mandatory = $false)]
    [string]$ClientRoot = "games/client",

    [Parameter(Mandatory = $false)]
    [ValidateSet("All", "UIRgba", "RxgbNormal")]
    [string]$ChannelMode = "All",

    [Parameter(Mandatory = $false)]
    [string]$Report = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [Parameter(Mandatory = $false)]
    [string]$Filter = "",

    [Parameter(Mandatory = $false)]
    [switch]$Strict = $false,

    [Parameter(Mandatory = $false)]
    [switch]$Detailed = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -Path (Join-Path -Path $PSScriptRoot -ChildPath "..\..")).Path
$pyScript = Join-Path -Path $repoRoot -ChildPath "tools\textures\audit_k2_textures.py"

if (-not (Test-Path -LiteralPath $pyScript)) {
    Write-Error -Message "K2 Texture Auditor script missing at '$pyScript'. Verify repository checkout with 'git status -- tools/textures/audit_k2_textures.py'."
    exit 1
}

$pythonCmd = Get-Command -Name "python" -ErrorAction SilentlyContinue
if ($null -eq $pythonCmd) {
    Write-Error -Message "Python 3 interpreter not found on PATH; cannot run K2 texture auditor. Install Python 3 + dependencies ('python -m pip install Pillow numpy') and verify with 'python --version'."
    exit 2
}

# Resolve target directory and client root relative to repo root if relative
$resolvedTargetDir = if ([System.IO.Path]::IsPathRooted($TargetDir)) { $TargetDir } else { Join-Path -Path $repoRoot -ChildPath $TargetDir }
$resolvedClientRoot = if ([System.IO.Path]::IsPathRooted($ClientRoot)) { $ClientRoot } else { Join-Path -Path $repoRoot -ChildPath $ClientRoot }

$pyChannelMode = switch ($ChannelMode) {
    "UIRgba"     { "ui-rgba" }
    "RxgbNormal" { "rxgb-normal" }
    Default      { "all" }
}

$pyArgs = New-Object -TypeName 'System.Collections.Generic.List[string]'
$pyArgs.Add($pyScript)
$pyArgs.Add("--target-dir")
$pyArgs.Add($resolvedTargetDir)
$pyArgs.Add("--client-root")
$pyArgs.Add($resolvedClientRoot)
$pyArgs.Add("--channel-mode")
$pyArgs.Add($pyChannelMode)

if (-not [string]::IsNullOrWhiteSpace($Report)) {
    $resolvedReport = if ([System.IO.Path]::IsPathRooted($Report)) { $Report } else { Join-Path -Path $repoRoot -ChildPath $Report }
    $pyArgs.Add("--report")
    $pyArgs.Add($resolvedReport)
}

if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    $resolvedJson = if ([System.IO.Path]::IsPathRooted($JsonOutput)) { $JsonOutput } else { Join-Path -Path $repoRoot -ChildPath $JsonOutput }
    $pyArgs.Add("--json")
    $pyArgs.Add($resolvedJson)
}

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $pyArgs.Add("--filter")
    $pyArgs.Add($Filter)
}

if ($Strict) {
    $pyArgs.Add("--strict")
}

if ($Detailed) {
    $pyArgs.Add("--detailed")
}

try {
    & python @pyArgs
    $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    exit $exitCode
} catch {
    Write-Error -Message "Failed to execute Python texture auditor: $($_.Exception.Message). Verify Python is on PATH with 'python --version' and run 'python tools/textures/audit_k2_textures.py --help'."
    exit 1
}
