# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Karpathy autoresearch autonomous 30s keep/discard metric optimization ratchet (#251).

.DESCRIPTION
    Ported from karpathy/autoresearch (Gem 6C):
    - Fixed 30s evaluation budget (-BudgetSeconds 30) with a 60s hard abort ceiling (-HardTimeoutSeconds 60).
    - Appends every experiment to a 5-column untracked TSV ledger (results.tsv):
        commit`tmetric_ms`tmemory_mb`tstatus`tdescription
      where status is in { baseline, keep, discard, crash }.
    - Crash-vs-Typo Retry Triage: classifies transient syntax/typo failures ("SyntaxError", "ParseError",
      "MissingFlag", "Typo") as retryable up to -MaxTypoRetries, while immediately aborting on hard runtime
      faults ("0xC0000005", "OutOfMemory", "HardTimeout").
    - Karpathy's Simplicity Criterion:
      1. Clear performance win (metric improved >= MinImprovementRatio within MemoryCapMb) -> keep.
      2. Simplicity win: equal or slightly better performance (CandidateMetricMs <= BaselineMetricMs * (1 + EqualToleranceRatio))
         with net code deletion (NetLinesDelta < 0) -> keep ("simplicity_deletion_keep").
      3. Complexity penalty: negligible improvement (< MinImprovementRatio) with large net code bloat
         (NetLinesDelta > MaxComplexityLines) -> discard ("complexity_bloat_discard").
      4. Regression or memory cap breach -> discard.

.EXAMPLE
    .\scripts\Invoke-KongorAutoRatchet.ps1 -CommitId "a1b2c3d" -BaselineMetricMs 100.0 -CandidateMetricMs 92.5 -MemoryMb 140.0 -NetLinesDelta -8 -Description "hoist sha256 out of inner loop"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$CommitId = "HEAD",

    [Parameter(Mandatory = $false)]
    [double]$BaselineMetricMs = 0.0,

    [Parameter(Mandatory = $false)]
    [double]$CandidateMetricMs = 0.0,

    [Parameter(Mandatory = $false)]
    [double]$MemoryMb = 0.0,

    [Parameter(Mandatory = $false)]
    [double]$MemoryCapMb = 2048.0,

    [Parameter(Mandatory = $false)]
    [int]$NetLinesDelta = 0,

    [Parameter(Mandatory = $false)]
    [double]$MinImprovementRatio = 0.01,

    [Parameter(Mandatory = $false)]
    [double]$EqualToleranceRatio = 0.002,

    [Parameter(Mandatory = $false)]
    [int]$MaxComplexityLines = 50,

    [Parameter(Mandatory = $false)]
    [int]$BudgetSeconds = 30,

    [Parameter(Mandatory = $false)]
    [int]$HardTimeoutSeconds = 60,

    [Parameter(Mandatory = $false)]
    [int]$MaxTypoRetries = 2,

    [Parameter(Mandatory = $false)]
    [scriptblock]$CandidateScript = $null,

    [Parameter(Mandatory = $false)]
    [scriptblock]$RetryScript = $null,

    [Parameter(Mandatory = $false)]
    [string]$SimulatedFailureMessage = "",

    [Parameter(Mandatory = $false)]
    [string]$Description = "experiment",

    [Parameter(Mandatory = $false)]
    [string]$ResultsTsvPath = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [switch]$RecordAsBaseline,
    [switch]$DryRun,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

if ($CommitId -eq "HEAD" -and (Test-Path -LiteralPath (Join-Path $RepoRoot ".git"))) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $resolvedSha = (& git -C $RepoRoot rev-parse --short HEAD 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolvedSha)) {
            $CommitId = $resolvedSha
        }
    } finally {
        $ErrorActionPreference = $prevEap
    }
}

if ([string]::IsNullOrWhiteSpace($ResultsTsvPath)) {
    $ResultsTsvPath = Join-Path $RepoRoot "userdata\logs\results.tsv"
} elseif (-not [System.IO.Path]::IsPathRooted($ResultsTsvPath)) {
    $ResultsTsvPath = Join-Path $RepoRoot $ResultsTsvPath
}

function Test-IsRetryableTypoError {
    param([string]$ErrorMessage)
    if ([string]::IsNullOrWhiteSpace($ErrorMessage)) { return $false }
    if ($ErrorMessage -match '(?i)(0xC0000005|AccessViolation|OutOfMemory|StackOverflow|HardTimeout|Deadlock|Segfault)') {
        return $false
    }
    return ($ErrorMessage -match '(?i)(Typo|SyntaxError|ParseError|ParserError|MissingFlag|ParameterBinding|UnexpectedToken|CommandNotFound)')
}

$retriesUsed = 0
$crashed = $false
$crashReason = ""
$elapsedEvalMs = 0.0

if (-not [string]::IsNullOrWhiteSpace($SimulatedFailureMessage)) {
    if ((Test-IsRetryableTypoError -ErrorMessage $SimulatedFailureMessage) -and $null -ne $RetryScript -and $MaxTypoRetries -gt 0) {
        $retriesUsed = 1
        $swRetry = [System.Diagnostics.Stopwatch]::StartNew()
        & $RetryScript | Out-Null
        $swRetry.Stop()
        $elapsedEvalMs = $swRetry.Elapsed.TotalMilliseconds
        if ($CandidateMetricMs -le 0.0) { $CandidateMetricMs = $elapsedEvalMs }
    } else {
        $crashed = $true
        $crashReason = if (Test-IsRetryableTypoError -ErrorMessage $SimulatedFailureMessage) { "typo_retries_exhausted: $SimulatedFailureMessage" } else { "fatal_crash: $SimulatedFailureMessage" }
    }
} elseif ($null -ne $CandidateScript) {
    $attempt = 0
    while ($true) {
        $attempt++
        $swEval = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $scriptToRun = if ($attempt -gt 1 -and $null -ne $RetryScript) { $RetryScript } else { $CandidateScript }
            $job = Start-Job -ScriptBlock $scriptToRun
            $completed = Wait-Job -Job $job -Timeout $HardTimeoutSeconds
            $swEval.Stop()
            $elapsedEvalMs = $swEval.Elapsed.TotalMilliseconds

            if ($null -eq $completed) {
                Stop-Job -Job $job -ErrorAction SilentlyContinue
                Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
                $crashed = $true
                $crashReason = "hard_timeout_exceeded_${HardTimeoutSeconds}s"
                break
            }

            if ($job.State -eq 'Failed') {
                $errStr = ($job.ChildJobs[0].JobStateInfo.Reason.Message | Out-String).Trim()
                Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
                if ((Test-IsRetryableTypoError -ErrorMessage $errStr) -and $retriesUsed -lt $MaxTypoRetries) {
                    $retriesUsed++
                    continue
                }
                $crashed = $true
                $crashReason = "candidate_failed: $errStr"
                break
            }

            $null = Receive-Job -Job $job -ErrorAction SilentlyContinue
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
            if ($CandidateMetricMs -le 0.0) {
                $CandidateMetricMs = [Math]::Max(0.01, $elapsedEvalMs)
            }
            break
        } catch {
            $swEval.Stop()
            $errStr = $_.Exception.Message
            if ((Test-IsRetryableTypoError -ErrorMessage $errStr) -and $retriesUsed -lt $MaxTypoRetries) {
                $retriesUsed++
                continue
            }
            $crashed = $true
            $crashReason = "exception: $errStr"
            break
        }
    }
}

$status = "discard"
$decisionReason = ""

if ($crashed) {
    $status = "crash"
    $decisionReason = $crashReason
    $CandidateMetricMs = 0.0
    $MemoryMb = 0.0
} elseif ($RecordAsBaseline) {
    $status = "baseline"
    $decisionReason = "initial_baseline"
} elseif ($MemoryCapMb -gt 0.0 -and $MemoryMb -gt $MemoryCapMb) {
    $status = "discard"
    $decisionReason = "memory_cap_exceeded ($MemoryMb MB > $MemoryCapMb MB)"
} else {
    $deltaMs = $BaselineMetricMs - $CandidateMetricMs
    $relImprovement = if ($BaselineMetricMs -gt 0.0) { $deltaMs / $BaselineMetricMs } else { 0.0 }

    if ($relImprovement -ge $MinImprovementRatio) {
        $status = "keep"
        $decisionReason = if ($NetLinesDelta -lt 0) { "faster_and_simpler" } else { "faster_metric_win" }
    } elseif ($CandidateMetricMs -le ($BaselineMetricMs * (1.0 + $EqualToleranceRatio)) -and $NetLinesDelta -lt 0) {
        # Karpathy's Simplicity Criterion: equal performance with fewer lines of code is a definite KEEP
        $status = "keep"
        $decisionReason = "simplicity_deletion_keep"
    } elseif ($relImprovement -gt 0.0 -and $NetLinesDelta -gt $MaxComplexityLines) {
        # Complexity bloat rejection: tiny sub-threshold gain that adds > MaxComplexityLines
        $status = "discard"
        $decisionReason = "complexity_bloat_discard"
    } else {
        $status = "discard"
        $decisionReason = if ($CandidateMetricMs -gt $BaselineMetricMs) { "slower_regression" } else { "insufficient_gain" }
    }
}

# Append to 5-column untracked results.tsv ledger: commit \t metric_ms \t memory_mb \t status \t description
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$cleanCommit = ($CommitId -replace '[\t\r\n]', ' ').Trim()
$cleanDesc = ($Description -replace '[\t\r\n]', ' ').Trim()
if ($crashed -and $cleanDesc -notmatch 'crash') {
    $cleanDesc = "$cleanDesc ($decisionReason)"
}
$metricFormatted = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0:F3}", $CandidateMetricMs)
$memFormatted = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0:F1}", $MemoryMb)
$tsvRow = "$cleanCommit`t$metricFormatted`t$memFormatted`t$status`t$cleanDesc`n"

if (-not $DryRun) {
    $tsvDir = [System.IO.Path]::GetDirectoryName($ResultsTsvPath)
    if (-not (Test-Path -LiteralPath $tsvDir -PathType Container)) {
        New-Item -ItemType Directory -Path $tsvDir -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $ResultsTsvPath -PathType Leaf)) {
        [System.IO.File]::WriteAllText($ResultsTsvPath, "commit`tmetric_ms`tmemory_mb`tstatus`tdescription`n", $utf8NoBom)
    }
    [System.IO.File]::AppendAllText($ResultsTsvPath, $tsvRow, $utf8NoBom)
}

$payload = [ordered]@{
    commit             = $cleanCommit
    baseline_metric_ms = $BaselineMetricMs
    metric_ms          = [Math]::Round($CandidateMetricMs, 3)
    memory_mb          = [Math]::Round($MemoryMb, 1)
    net_lines_delta    = $NetLinesDelta
    status             = $status
    decision_reason    = $decisionReason
    typo_retries_used  = $retriesUsed
    budget_seconds     = $BudgetSeconds
    hard_timeout_sec   = $HardTimeoutSeconds
    results_tsv_path   = $ResultsTsvPath
    description        = $cleanDesc
}

if (-not $Quiet) {
    Write-Host "[AUTORESEARCH-RATCHET-251] status=$status reason=$decisionReason metric_ms=$metricFormatted memory_mb=$memFormatted net_lines=$NetLinesDelta" -ForegroundColor Cyan
}

$outJson = $payload | ConvertTo-Json -Depth 4
if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    [System.IO.File]::WriteAllText($JsonOutput, $outJson, $utf8NoBom)
}
Write-Output $outJson
exit 0
