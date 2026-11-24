# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Interleaved randomized [base, head] A/B benchmark runner with exact non-parametric
    Binomial Sign-Test 95% confidence intervals and work_change attribution (#100).

.DESCRIPTION
    Ported from manimgx scripts/benchmark_perf.py:
    - Executes paired rounds in randomized [base, head] or [head, base] order to eliminate
      CPU thermal drift and background OS scheduling bias.
    - Computes exact Binomial Sign-Test 95% confidence bounds over paired (head_ms / base_ms)
      ratios and two-sided sign-test p-values without parametric Gaussian assumptions.
    - Separates genuine throughput improvements ("faster" / "slower" / "no_clear_signal") from
      workload changes ("work_change") when BaseWorkUnits != HeadWorkUnits.

.EXAMPLE
    .\scripts\Invoke-KongorAbBenchmark.ps1 -BaseScript { Start-Sleep -Milliseconds 10 } -HeadScript { Start-Sleep -Milliseconds 5 } -Rounds 10
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [scriptblock]$BaseScript,

    [Parameter(Mandatory = $false)]
    [scriptblock]$HeadScript,

    [Parameter(Mandatory = $false)]
    [double[]]$BaseSamplesMs = @(),

    [Parameter(Mandatory = $false)]
    [double[]]$HeadSamplesMs = @(),

    [Parameter(Mandatory = $false)]
    [ValidateRange(4, 200)]
    [int]$Rounds = 10,

    [Parameter(Mandatory = $false)]
    [long]$BaseWorkUnits = 0,

    [Parameter(Mandatory = $false)]
    [long]$HeadWorkUnits = 0,

    [Parameter(Mandatory = $false)]
    [double]$BaseMemoryMb = 0.0,

    [Parameter(Mandatory = $false)]
    [double]$HeadMemoryMb = 0.0,

    [Parameter(Mandatory = $false)]
    [int]$NetLinesDelta = 0,

    [Parameter(Mandatory = $false)]
    [int]$Seed = 42,

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Get-BinomialProb {
    param([int]$N, [int]$K)
    if ($K -lt 0 -or $K -gt $N) { return 0.0 }
    # Compute C(N, K) * 0.5^N in log-space to avoid overflow
    $logComb = 0.0
    for ($i = 1; $i -le $K; $i++) {
        $logComb += [Math]::Log($N - $K + $i) - [Math]::Log($i)
    }
    return [Math]::Exp($logComb - $N * [Math]::Log(2.0))
}

function Get-SignTestTwoSidedPValue {
    param([int]$Wins, [int]$Losses)
    $n = $Wins + $Losses
    if ($n -le 0) { return 1.0 }
    $minSide = [Math]::Min($Wins, $Losses)
    $cdf = 0.0
    for ($k = 0; $k -le $minSide; $k++) {
        $cdf += Get-BinomialProb -N $n -K $k
    }
    return [Math]::Min(1.0, 2.0 * $cdf)
}

function Get-BinomialMedianConfidenceInterval {
    param([double[]]$SortedValues)
    $n = $SortedValues.Count
    if ($n -eq 0) {
        return @{ Lower = 1.0; Upper = 1.0; Median = 1.0 }
    }
    $mid = [int][Math]::Floor([double]$n / 2.0)
    $median = if (($n % 2) -eq 1) {
        $SortedValues[$mid]
    } else {
        0.5 * ($SortedValues[$mid - 1] + $SortedValues[$mid])
    }

    # Exact Binomial order-statistic index for >= 95% two-sided coverage (tail <= 0.025 each side)
    $cumProb = 0.0
    $lowerIdx = 0
    for ($k = 0; $k -lt $n; $k++) {
        $p = Get-BinomialProb -N $n -K $k
        if (($cumProb + $p) -le 0.025) {
            $cumProb += $p
            $lowerIdx = $k + 1
        } else {
            break
        }
    }
    $upperIdx = [Math]::Max($lowerIdx, ($n - 1 - $lowerIdx))
    return @{
        Lower  = [Math]::Round($SortedValues[$lowerIdx], 4)
        Upper  = [Math]::Round($SortedValues[$upperIdx], 4)
        Median = [Math]::Round($median, 4)
    }
}

$rng = New-Object System.Random($Seed)
$pairedBase = New-Object System.Collections.Generic.List[double]
$pairedHead = New-Object System.Collections.Generic.List[double]
$interleavedOrder = New-Object System.Collections.Generic.List[string]

if ($BaseSamplesMs.Count -gt 0 -and $HeadSamplesMs.Count -gt 0) {
    $pairCount = [Math]::Min($BaseSamplesMs.Count, $HeadSamplesMs.Count)
    for ($i = 0; $i -lt $pairCount; $i++) {
        $headFirst = ($rng.Next(0, 2) -eq 1)
        $interleavedOrder.Add($(if ($headFirst) { "head->base" } else { "base->head" }))
        $pairedBase.Add([double]$BaseSamplesMs[$i])
        $pairedHead.Add([double]$HeadSamplesMs[$i])
    }
} elseif ($null -ne $BaseScript -and $null -ne $HeadScript) {
    for ($r = 0; $r -lt $Rounds; $r++) {
        $headFirst = ($rng.Next(0, 2) -eq 1)
        if ($headFirst) {
            $interleavedOrder.Add("head->base")
            $swH = [System.Diagnostics.Stopwatch]::StartNew()
            & $HeadScript | Out-Null
            $swH.Stop()
            $swB = [System.Diagnostics.Stopwatch]::StartNew()
            & $BaseScript | Out-Null
            $swB.Stop()
        } else {
            $interleavedOrder.Add("base->head")
            $swB = [System.Diagnostics.Stopwatch]::StartNew()
            & $BaseScript | Out-Null
            $swB.Stop()
            $swH = [System.Diagnostics.Stopwatch]::StartNew()
            & $HeadScript | Out-Null
            $swH.Stop()
        }
        $pairedBase.Add([Math]::Max(0.001, $swB.Elapsed.TotalMilliseconds))
        $pairedHead.Add([Math]::Max(0.001, $swH.Elapsed.TotalMilliseconds))
    }
} else {
    throw "Provide either (-BaseScript and -HeadScript) or (-BaseSamplesMs and -HeadSamplesMs)."
}

$ratios = New-Object System.Collections.Generic.List[double]
$headWins = 0
$baseWins = 0

for ($i = 0; $i -lt $pairedBase.Count; $i++) {
    $b = [Math]::Max(0.0001, $pairedBase[$i])
    $h = [Math]::Max(0.0001, $pairedHead[$i])
    $ratios.Add($h / $b)
    if ($h -lt $b) {
        $headWins++
    } elseif ($h -gt $b) {
        $baseWins++
    }
}

$sortedRatios = @($ratios | Sort-Object)
$ci = Get-BinomialMedianConfidenceInterval -SortedValues $sortedRatios
$pValue = [Math]::Round((Get-SignTestTwoSidedPValue -Wins $headWins -Losses $baseWins), 5)

$verdict = "no_clear_signal"
if ($BaseWorkUnits -gt 0 -and $HeadWorkUnits -gt 0 -and $BaseWorkUnits -ne $HeadWorkUnits) {
    $verdict = "work_change"
} elseif ($ci.Upper -lt 1.0 -and $pValue -le 0.05) {
    $verdict = "faster"
} elseif ($ci.Lower -gt 1.0 -and $pValue -le 0.05) {
    $verdict = "slower"
}

$simplicityKeepEligible = ($verdict -in @("faster", "no_clear_signal")) -and ($ci.Median -le 1.005) -and ($NetLinesDelta -lt 0)

$result = [ordered]@{
    rounds                   = $pairedBase.Count
    interleaved_order        = @($interleavedOrder)
    head_wins                = $headWins
    base_wins                = $baseWins
    sign_test_p_value        = $pValue
    median_ratio             = $ci.Median
    ci95_lower_ratio         = $ci.Lower
    ci95_upper_ratio         = $ci.Upper
    base_work_units          = $BaseWorkUnits
    head_work_units          = $HeadWorkUnits
    base_memory_mb           = $BaseMemoryMb
    head_memory_mb           = $HeadMemoryMb
    net_lines_delta          = $NetLinesDelta
    simplicity_keep_eligible = $simplicityKeepEligible
    verdict                  = $verdict
}

if (-not $Quiet) {
    Write-Host "[AB-BENCHMARK] verdict=$verdict median_ratio=$($ci.Median) CI95=[$($ci.Lower), $($ci.Upper)] p=$pValue (head_wins=$headWins, base_wins=$baseWins)" -ForegroundColor Cyan
}
Write-Output ($result | ConvertTo-Json -Compress -Depth 4)
exit 0
