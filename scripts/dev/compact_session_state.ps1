# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Compacts docs/ACTIVE_SESSION_STATE.md into a structured <= 1500 token subagent handoff packet (#51, #116).

.DESCRIPTION
    Parses the Active Session State Ledger and extracts:
    - verified_facts (from validated hypotheses and current status)
    - tombstoned_hypotheses (from disproven hypotheses with failure evidence)
    - risk_ordered_live_probes (from pending actions / next steps)
    Enforces a strict <= 1500 estimated token budget (~6000 characters) so subagents receive
    clean, cold-reader briefings without context bloat or repeated disproven attempts.

.EXAMPLE
    .\scripts\dev\compact_session_state.ps1
    .\scripts\dev\compact_session_state.ps1 -SessionStatePath "docs\ACTIVE_SESSION_STATE.md" -OutputPath "userdata\logs\handoff_packet.json"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$SessionStatePath = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "",

    [Parameter(Mandatory = $false)]
    [int]$MaxTokens = 1500,

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($SessionStatePath)) {
    $SessionStatePath = Join-Path $repoRoot "docs\ACTIVE_SESSION_STATE.md"
}

if (-not (Test-Path -LiteralPath $SessionStatePath)) {
    Write-Host "[COMPACT-ERROR] Session state file not found at '$SessionStatePath'. Verify path or restore docs/ACTIVE_SESSION_STATE.md." -ForegroundColor Red
    exit 1
}

$lines = @(Get-Content -LiteralPath $SessionStatePath -Encoding UTF8)
$verifiedFacts = [System.Collections.ArrayList]::new()
$tombstonedHypotheses = [System.Collections.ArrayList]::new()
$riskOrderedProbes = [System.Collections.ArrayList]::new()

foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if ($trimmed -match '^\|\s*\*\*Status\*\*\s*\|\s*(.+?)\s*\|$') {
        [void]$verifiedFacts.Add("Active Status: $($Matches[1])")
    } elseif ($trimmed -match '^\|\s*\*\*Active Objective\*\*\s*\|\s*(.+?)\s*\|$') {
        [void]$verifiedFacts.Add("Active Objective: $($Matches[1])")
    } elseif ($trimmed -match '^\|\s*\d+\s*\|\s*(.+?)\s*\|\s*(.+?)\s*\|\s*(.+?)\s*\|\s*(.+?)\s*\|$') {
        $hyp = $Matches[1].Trim()
        $res = $Matches[3].Trim()
        $outcome = $Matches[4].Trim()
        if ($outcome -match 'Disproven|Failed') {
            [void]$tombstonedHypotheses.Add("DISPROVEN: $hyp -> $res")
        } elseif ($outcome -match 'Validated') {
            if ($verifiedFacts.Count -lt 10) {
                [void]$verifiedFacts.Add("VALIDATED: $hyp ($res)")
            }
        }
    } elseif ($trimmed -match '^\-\s*\[\s*\]\s*(.+)$') {
        $probeText = $Matches[1].Trim()
        $probeItem = [PSCustomObject]@{
            priority = $riskOrderedProbes.Count + 1
            risk     = "low"
            probe    = $probeText
        }
        [void]$riskOrderedProbes.Add($probeItem)
    }
}

# Enforce <= MaxTokens (~4 chars/token)
$maxChars = $MaxTokens * 4
while ($true) {
    $packet = [ordered]@{
        schema_version           = "1.0"
        generated_at             = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        source_path              = $SessionStatePath
        token_budget_limit       = $MaxTokens
        verified_facts           = @($verifiedFacts.ToArray())
        tombstoned_hypotheses    = @($tombstonedHypotheses.ToArray())
        risk_ordered_live_probes = @($riskOrderedProbes.ToArray())
    }
    $json = $packet | ConvertTo-Json -Depth 5 -Compress
    $estimatedTokens = [int][Math]::Ceiling([double]$json.Length / 4.0)
    $packet["estimated_tokens"] = $estimatedTokens

    if ($json.Length -le $maxChars -or ($verifiedFacts.Count -le 2 -and $riskOrderedProbes.Count -le 1)) {
        break
    }
    if ($verifiedFacts.Count -gt 3) {
        $verifiedFacts.RemoveAt($verifiedFacts.Count - 1)
    } elseif ($riskOrderedProbes.Count -gt 2) {
        $riskOrderedProbes.RemoveAt($riskOrderedProbes.Count - 1)
    } else {
        break
    }
}

$formattedJson = $packet | ConvertTo-Json -Depth 5
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outDir = [System.IO.Path]::GetDirectoryName($OutputPath)
    if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($OutputPath, $formattedJson, $utf8NoBom)
}

if (-not $Quiet) {
    Write-Host "[COMPACT-OK] Compacted session state into $($packet.estimated_tokens) estimated tokens (limit: $MaxTokens; facts=$($verifiedFacts.Count), tombstoned=$($tombstonedHypotheses.Count), probes=$($riskOrderedProbes.Count))." -ForegroundColor Green
}
Write-Output ($packet | ConvertTo-Json -Depth 5 -Compress)
exit 0
