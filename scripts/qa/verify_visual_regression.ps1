# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Project Kongor Wave 3A Visual Regression Gate (Headless Pixelmatch CI).
.DESCRIPTION
    Compares candidate K2 client screenshots against golden baseline images using
    tools/visual/pixelmatch_ci.py (pure-Python/NumPy YIQ perceptual diffing + AA filter
    + blank-screen rejection), writes visual diff PNGs for any failure, and emits a
    structured JSON summary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,

    [Parameter(Mandatory = $true)]
    [string]$CandidatePath,

    [Parameter(Mandatory = $false)]
    [string]$DiffOutputPath = "",

    [Parameter(Mandatory = $false)]
    [double]$Threshold = 0.1,

    [Parameter(Mandatory = $false)]
    [double]$MaxDiffRatio = 0.01,

    [Parameter(Mandatory = $false)]
    [string[]]$IgnoreRegion = @(),

    [Parameter(Mandatory = $false)]
    [switch]$IncludeAA,

    [Parameter(Mandatory = $false)]
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$toolPath = Join-Path $repoRoot "tools\visual\pixelmatch_ci.py"

if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
    $missingMsg = "[VISUAL-CI] Missing visual diff engine at '$toolPath'. Restore tools/visual/pixelmatch_ci.py before running."
    if ($Json) {
        $errJson = [ordered]@{
            passed = $false
            error = $missingMsg
            error_type = "MissingVisualEngine"
            baseline_path = $BaselinePath
            candidate_path = $CandidatePath
            diff_output_path = $null
        } | ConvertTo-Json -Depth 4
        Write-Output $errJson
    }
    [Console]::Error.WriteLine($missingMsg)
    exit 2
}

$pyArgs = @(
    $toolPath,
    $BaselinePath,
    $CandidatePath,
    "--threshold", ([string]$Threshold),
    "--max-diff-ratio", ([string]$MaxDiffRatio)
)

if (-not [string]::IsNullOrWhiteSpace($DiffOutputPath)) {
    $pyArgs += @("--diff-out", $DiffOutputPath)
}
if ($IncludeAA) {
    $pyArgs += "--include-aa"
}
foreach ($region in $IgnoreRegion) {
    if (-not [string]::IsNullOrWhiteSpace($region)) {
        $pyArgs += @("--ignore-region", $region)
    }
}
if ($Json) {
    $pyArgs += "--json"
}

$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & python @pyArgs
    $exitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $prevEap
}

if ($exitCode -ne 0) {
    $failMsg = "[VISUAL-CI] FAILED (exitCode=$exitCode). Diagnose via: python tools/visual/pixelmatch_ci.py `"$BaselinePath`" `"$CandidatePath`" --diff-out diff.png"
    if ($Json) {
        [Console]::Error.WriteLine($failMsg)
    } else {
        Write-Host $failMsg -ForegroundColor Red
    }
}
exit $exitCode
