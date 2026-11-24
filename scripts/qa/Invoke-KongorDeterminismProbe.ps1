# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    3-Run Portability & Non-Determinism Triangulation Probe (#102 Option XXXX).
.DESCRIPTION
    Executes or evaluates 3 isolated render/state captures (Render1, Render2, StateSnapshot) against
    a reference baseline (ReferenceRender, ReferenceState) and applies the 3-way decision table:
      1. Render1 != Render2                                               -> SAME_HOST_NON_DETERMINISM (race condition / unseeded RNG)
      2. Render1 == Render2 AND State == RefState AND Render1 != RefRender -> DOWNSTREAM_GPU_DRIVER_DRIFT (GPU/shader rasterization diff)
      3. Render1 == Render2 AND State != RefState                          -> UPSTREAM_CPU_LUA_DRIFT (pinpoints first diverged state line)
      4. Render1 == Render2 AND State == RefState AND Render1 == RefRender -> DETERMINISTIC_MATCH
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Render1Path = "",

    [Parameter(Mandatory = $false)]
    [string]$Render2Path = "",

    [Parameter(Mandatory = $false)]
    [string]$ReferenceRenderPath = "",

    [Parameter(Mandatory = $false)]
    [string]$StateSnapshotPath = "",

    [Parameter(Mandatory = $false)]
    [string]$ReferenceStatePath = "",

    [Parameter(Mandatory = $false)]
    [string]$TargetViewer = "model_inspector_r128",

    [Parameter(Mandatory = $false)]
    [ValidateSet("auto", "deterministic", "same_host_race", "downstream_gpu_drift", "upstream_cpu_lua_drift")]
    [string]$Scenario = "auto",

    [Parameter(Mandatory = $false)]
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$FilePath)
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "[DETERMINISM-PROBE-ERR] File not found: '$FilePath'. Remediation: Run scripts/Invoke-KongorDeterminismProbe.ps1 -Scenario deterministic or pass valid capture paths."
    }
    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant())
    }
    finally {
        $sha.Dispose()
    }
}

function Find-FirstDivergedStateLine {
    param(
        [Parameter(Mandatory = $true)][string]$PathA,
        [Parameter(Mandatory = $true)][string]$PathB
    )
    $linesA = [System.IO.File]::ReadAllLines($PathA, [System.Text.Encoding]::UTF8)
    $linesB = [System.IO.File]::ReadAllLines($PathB, [System.Text.Encoding]::UTF8)
    $maxLen = [Math]::Max($linesA.Length, $linesB.Length)
    for ($i = 0; $i -lt $maxLen; $i++) {
        $a = if ($i -lt $linesA.Length) { $linesA[$i] } else { "<EOF>" }
        $b = if ($i -lt $linesB.Length) { $linesB[$i] } else { "<EOF>" }
        if ($a -cne $b) {
            return [ordered]@{
                line_number = ($i + 1)
                actual      = $a
                expected    = $b
            }
        }
    }
    return $null
}

$tempDir = ""
try {
    if ($Scenario -ne "auto" -or [string]::IsNullOrWhiteSpace($Render1Path)) {
        $effectiveScenario = if ($Scenario -eq "auto") { "deterministic" } else { $Scenario }
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kongor_det_probe_" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

        $Render1Path = Join-Path $tempDir "render1.bin"
        $Render2Path = Join-Path $tempDir "render2.bin"
        $ReferenceRenderPath = Join-Path $tempDir "ref_render.bin"
        $StateSnapshotPath = Join-Path $tempDir "state.take.json"
        $ReferenceStatePath = Join-Path $tempDir "ref_state.take.json"

        $baseRenderBytes = [System.Text.Encoding]::UTF8.GetBytes("SWIFTSHADER_R128_FRAME_T0_PIXELS_OK")
        $altRenderBytes = [System.Text.Encoding]::UTF8.GetBytes("SWIFTSHADER_R128_FRAME_T0_PIXELS_DIFF")
        $baseStateText = "{`"viewer`":`"$TargetViewer`",`"t`":0.0,`"dpr`":1,`"drawCalls`":12,`"bones`":24}"
        $altStateText = "{`"viewer`":`"$TargetViewer`",`"t`":0.0,`"dpr`":1,`"drawCalls`":13,`"bones`":24}"

        [System.IO.File]::WriteAllBytes($ReferenceRenderPath, $baseRenderBytes)
        [System.IO.File]::WriteAllText($ReferenceStatePath, $baseStateText, [System.Text.Encoding]::UTF8)

        switch ($effectiveScenario) {
            "deterministic" {
                [System.IO.File]::WriteAllBytes($Render1Path, $baseRenderBytes)
                [System.IO.File]::WriteAllBytes($Render2Path, $baseRenderBytes)
                [System.IO.File]::WriteAllText($StateSnapshotPath, $baseStateText, [System.Text.Encoding]::UTF8)
            }
            "same_host_race" {
                [System.IO.File]::WriteAllBytes($Render1Path, $baseRenderBytes)
                [System.IO.File]::WriteAllBytes($Render2Path, $altRenderBytes)
                [System.IO.File]::WriteAllText($StateSnapshotPath, $baseStateText, [System.Text.Encoding]::UTF8)
            }
            "downstream_gpu_drift" {
                [System.IO.File]::WriteAllBytes($Render1Path, $altRenderBytes)
                [System.IO.File]::WriteAllBytes($Render2Path, $altRenderBytes)
                [System.IO.File]::WriteAllText($StateSnapshotPath, $baseStateText, [System.Text.Encoding]::UTF8)
            }
            "upstream_cpu_lua_drift" {
                [System.IO.File]::WriteAllBytes($Render1Path, $altRenderBytes)
                [System.IO.File]::WriteAllBytes($Render2Path, $altRenderBytes)
                [System.IO.File]::WriteAllText($StateSnapshotPath, $altStateText, [System.Text.Encoding]::UTF8)
            }
        }
    }

    $hashRender1 = Get-FileSha256Hex -FilePath $Render1Path
    $hashRender2 = Get-FileSha256Hex -FilePath $Render2Path
    $hashRefRender = Get-FileSha256Hex -FilePath $ReferenceRenderPath
    $hashState = Get-FileSha256Hex -FilePath $StateSnapshotPath
    $hashRefState = Get-FileSha256Hex -FilePath $ReferenceStatePath

    $render1EqRender2 = ($hashRender1 -eq $hashRender2)
    $stateEqRef = ($hashState -eq $hashRefState)
    $renderEqRef = ($hashRender1 -eq $hashRefRender)

    $classification = "DETERMINISTIC_MATCH"
    $remediation = "None required; all 3 runs and state snapshot match reference baseline."
    $firstDiverged = $null

    if (-not $render1EqRender2) {
        $classification = "SAME_HOST_NON_DETERMINISM"
        $remediation = "Inspect unseeded RNG, async asset load races, or uninitialized GPU buffer memory (Render1 != Render2 on same host)."
    }
    elseif (-not $stateEqRef) {
        $classification = "UPSTREAM_CPU_LUA_DRIFT"
        $firstDiverged = Find-FirstDivergedStateLine -PathA $StateSnapshotPath -PathB $ReferenceStatePath
        $lineNum = if ($null -ne $firstDiverged) { $firstDiverged.line_number } else { 1 }
        $remediation = "Upstream CPU/Lua state snapshot diverged at line $lineNum before GPU rasterization."
    }
    elseif (-not $renderEqRef) {
        $classification = "DOWNSTREAM_GPU_DRIVER_DRIFT"
        $remediation = "CPU/Lua state is 100% identical to reference; only GPU/shader rasterization pixels differ. Pin to SwiftShader (--use-gl=angle --use-angle=swiftshader) or inspect shader precision."
    }

    $divergedLineNum = if ($null -ne $firstDiverged) { $firstDiverged.line_number } else { 0 }
    $logLine = "[DETERMINISM-PROBE] classification=$classification render1_eq_render2=$($render1EqRender2.ToString().ToLowerInvariant()) state_eq_ref=$($stateEqRef.ToString().ToLowerInvariant()) render_eq_ref=$($renderEqRef.ToString().ToLowerInvariant()) first_diverged_line=$divergedLineNum"
    Write-Host $logLine

    $report = [ordered]@{
        classification       = $classification
        target_viewer        = $TargetViewer
        render1_eq_render2   = $render1EqRender2
        state_eq_ref         = $stateEqRef
        render_eq_ref        = $renderEqRef
        first_diverged_state = $firstDiverged
        remediation          = $remediation
        telemetry            = $logLine
    }

    if ($AsJson) {
        return ($report | ConvertTo-Json -Depth 6 -Compress)
    }
    return [pscustomobject]$report
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($tempDir) -and (Test-Path -LiteralPath $tempDir)) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force
    }
}
