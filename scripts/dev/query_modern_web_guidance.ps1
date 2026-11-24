# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Fast offline-first CLI wrapper for Chrome's modern-web-guidance skill corpus.

.DESCRIPTION
    Resolves local modern-web.mjs from the installed Antigravity plugin or npm cache
    for <50ms zero-network execution (search, retrieve, list, legacy-scan), falling
    back to npx.cmd --offline and npx.cmd -y modern-web-guidance@latest when needed.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("search", "retrieve", "list", "legacy-scan")]
    [string]$Action = "search",

    [Parameter(Mandatory = $false)]
    [string]$Query = "",

    [Parameter(Mandatory = $false)]
    [string]$Id = "",

    [Parameter(Mandatory = $false)]
    [string]$Path = "src/services/web-ui",

    [Parameter(Mandatory = $false)]
    [switch]$Json = $false
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Write-MwgLog {
    param([string]$Level, [string]$Message)
    $ts = [DateTime]::Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
    [Console]::Error.WriteLine("[$ts] [KONGOR-MWG] [$Level] $Message")
}

$nodeCmd = Get-Command "node.exe" -ErrorAction SilentlyContinue
if (-not $nodeCmd) {
    $nodeCmd = Get-Command "node" -ErrorAction SilentlyContinue
}
if (-not $nodeCmd) {
    Write-MwgLog -Level "ERROR" -Message "Node.js >= 22 is not on PATH. Install Node.js >= 22 or inspect guides directly under ~/.gemini/config/plugins/modern-web-guidance-plugin/skills/modern-web-guidance/guides/."
    throw "Node.js (>= 22) not found on PATH. Fix: Install Node.js >= 22 (https://nodejs.org/) or read guides directly from C:\Users\$env:USERNAME\.gemini\config\plugins\modern-web-guidance-plugin\skills\modern-web-guidance\guides\."
}

$cliArgs = New-Object System.Collections.Generic.List[string]
switch ($Action) {
    "search" {
        if ([string]::IsNullOrWhiteSpace($Query)) {
            throw "Missing -Query for -Action search. Fix: Run powershell -NoProfile -File scripts/dev/query_modern_web_guidance.ps1 -Action search -Query 'light dismiss dialog'"
        }
        $cliArgs.Add("search")
        $cliArgs.Add($Query)
    }
    "retrieve" {
        if ([string]::IsNullOrWhiteSpace($Id)) {
            throw "Missing -Id for -Action retrieve. Fix: Run powershell -NoProfile -File scripts/dev/query_modern_web_guidance.ps1 -Action retrieve -Id 'dialog,light-dismiss-a-dialog'"
        }
        $cliArgs.Add("retrieve")
        $cliArgs.Add($Id)
    }
    "list" {
        $cliArgs.Add("list")
    }
    "legacy-scan" {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $targetPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repoRoot $Path }
        if (-not (Test-Path -LiteralPath $targetPath)) {
            throw "Target path '$targetPath' does not exist for -Action legacy-scan. Fix: Pass a valid directory via -Path (e.g. -Path src/services/web-ui)."
        }
        $cliArgs.Add("legacy-scan")
        $cliArgs.Add($targetPath)
        if ($Json) {
            $cliArgs.Add("--json")
        }
    }
}

# 1. Resolve local offline bundle first (<50ms, zero network dependency)
$localCandidates = New-Object System.Collections.Generic.List[string]
if ($env:USERPROFILE) {
    $pluginBundle = Join-Path $env:USERPROFILE ".gemini\config\plugins\modern-web-guidance-plugin\skills\modern-web-guidance\modern-web.mjs"
    if (Test-Path -LiteralPath $pluginBundle -PathType Leaf) {
        $localCandidates.Add($pluginBundle)
    }
}
if ($env:LOCALAPPDATA) {
    $npxRoot = Join-Path $env:LOCALAPPDATA "npm-cache\_npx"
    if (Test-Path -LiteralPath $npxRoot -PathType Container) {
        $cachedMjs = Get-ChildItem -LiteralPath $npxRoot -Recurse -Filter "modern-web.mjs" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($cachedMjs) {
            $localCandidates.Add($cachedMjs.FullName)
        }
    }
}

$resolvedEntry = if ($localCandidates.Count -gt 0) { $localCandidates[0] } else { "" }

if (-not [string]::IsNullOrWhiteSpace($resolvedEntry)) {
    Write-MwgLog -Level "INFO" -Message "Executing Action='$Action' via local offline bundle: $resolvedEntry"
    $output = & $nodeCmd.Source $resolvedEntry @cliArgs
    $exitCode = $LASTEXITCODE
    $sw.Stop()
    Write-MwgLog -Level "INFO" -Message "Completed Action='$Action' (exitCode=$exitCode) in $($sw.ElapsedMilliseconds)ms."
    if ($null -ne $output) {
        $output
    }
    if ($exitCode -ne 0) {
        throw "modern-web-guidance CLI failed with exit code $exitCode on Action='$Action'. Fix: Verify query/ID syntax via powershell -NoProfile -File scripts/dev/query_modern_web_guidance.ps1 -Action list"
    }
    exit 0
}

# 2. Fallback to npx.cmd if local plugin/npx bundle is not yet cached
$npxCmd = Get-Command "npx.cmd" -ErrorAction SilentlyContinue
if (-not $npxCmd) {
    $npxCmd = Get-Command "npx" -ErrorAction SilentlyContinue
}
if (-not $npxCmd) {
    throw "Neither local modern-web.mjs bundle nor npx.cmd was found. Fix: Enable/repair modern-web-guidance-plugin or ensure npm/npx is on PATH."
}

Write-MwgLog -Level "WARN" -Message "Local modern-web.mjs bundle not cached yet; falling back to npx.cmd -y modern-web-guidance@latest for Action='$Action'."
$npxArgs = @("-y", "modern-web-guidance@latest") + $cliArgs.ToArray()
$output = & $npxCmd.Source @npxArgs
$exitCode = $LASTEXITCODE
$sw.Stop()
Write-MwgLog -Level "INFO" -Message "Completed npx fallback Action='$Action' (exitCode=$exitCode) in $($sw.ElapsedMilliseconds)ms."
if ($null -ne $output) {
    $output
}
if ($exitCode -ne 0) {
    throw "npx modern-web-guidance@latest failed with exit code $exitCode. Fix: Check network access or retry offline with npx.cmd --offline modern-web-guidance."
}
exit 0
