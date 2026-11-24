# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Fast (<1.5s) offline unit test suite for scripts/qa/lib/Invoke-KongorShjPerf.ps1.
    Covers zero-window fail-closed logic, vid_vulkan_spy.log regex & harmonic FPS math,
    [ordered]@{} multi-window aggregation, High-Hz budget/borderline evaluation,
    dxvk.conf inline comment parsing, and [USER_DIR] path scrubbing.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$libPath = Join-Path $PSScriptRoot "lib\Invoke-KongorShjPerf.ps1"
if (-not (Test-Path -LiteralPath $libPath -PathType Leaf)) {
    throw "Missing library: $libPath"
}

. $libPath

$passed = 0
$failed = 0
$failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param([bool]$Condition, [string]$Name, [string]$Detail = "")
    if ($Condition) {
        $script:passed++
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    } else {
        $script:failed++
        $msg = if ([string]::IsNullOrWhiteSpace($Detail)) { $Name } else { "$Name ($Detail)" }
        $script:failures.Add($msg)
        Write-Host "  [FAIL] $msg" -ForegroundColor Red
    }
}

function New-CleanWindowMetrics {
    param(
        [double]$AvgFps = 196.0,
        [double]$MedFps = 201.5,
        [double]$P1Low = 60.0,
        [double]$P01Low = 48.0,
        [double]$P99Ms = 13.0,
        [double]$P999Ms = 18.5,
        [double]$MaxMs = 22.0,
        [double]$RateGt10 = 1.2,
        [int]$Gt33 = 0,
        [int]$Gt50 = 0,
        [int]$GpuStall = 0,
        [uint64]$Drops = 0,
        [int]$SampledFrames = 3900,
        [bool]$RenderAccurate = $true,
        [bool]$SpyAvailable = $true,
        [double]$SpyFps = 195.9,
        [double]$SpyDivPct = 0.05,
        [bool]$FrameBudgetPassed = $true,
        [int]$BudgetViolations = 0,
        [object]$ModuleAuditPassed = $true
    )
    return [ordered]@{
        windowIndex           = 1
        telemetrySource       = "vid_vulkan_render_fastbus"
        isRenderFrameAccurate = $RenderAccurate
        sampledFrames         = $SampledFrames
        engineFramesElapsed   = [uint64]$SampledFrames
        droppedRingFrames     = $Drops
        averageFps            = $AvgFps
        medianFps             = $MedFps
        p1LowFps              = $P1Low
        p01LowFps             = $P01Low
        avgFrameTimeMs        = [Math]::Round(1000.0 / $AvgFps, 3)
        p50FrameTimeMs        = [Math]::Round(1000.0 / $MedFps, 3)
        p95FrameTimeMs        = 9.5
        p99FrameTimeMs        = $P99Ms
        p999FrameTimeMs       = $P999Ms
        maxFrameTimeMs        = $MaxMs
        avgCpuEngineMs        = 3.4
        avgGpuRenderMs        = 1.2
        avgPresentWaitMs      = 0.3
        avgDrawCalls          = 1250.0
        gt8ms                 = 40
        gt10ms                = [int]([Math]::Round($RateGt10 * 20))
        gt16_67ms             = 4
        gt33_33ms             = $Gt33
        gt50ms                = $Gt50
        rateGt8msPerSec       = 2.0
        rateGt10msPerSec      = $RateGt10
        cpuStallCount         = 1
        gpuStallCount         = $GpuStall
        dwmWaitCount          = 0
        preemptionCount       = 0
        budgetViolationsCount = $BudgetViolations
        frameBudgetPassed     = $FrameBudgetPassed
        spyAvailable          = $SpyAvailable
        spyHarmonicAvgFps     = $SpyFps
        spyWeightedAvgDraws   = 1250.0
        spyDivergencePct      = $SpyDivPct
        moduleAuditPassed     = $ModuleAuditPassed
    }
}

Write-Host "`n[SHJ-PERF-TEST] Running offline unit tests for Invoke-KongorShjPerf.ps1..." -ForegroundColor Cyan

# 1. ZeroWindowExposure fail-closed & geometry rules
$zMissingFailClosed = Resolve-KongorZeroWindowExposureDecision -HasHandle $false -RequireRenderWindow $true -WindowDesktop "exebox-test"
Assert-True (-not [bool]$zMissingFailClosed.zeroWindowExposure -and $zMissingFailClosed.reason -eq "k2_window_handle_not_found") `
    "ZeroWindowExposure fails closed when vid_vulkan is loaded and hwnd==0x0" `
    "got zero=$($zMissingFailClosed.zeroWindowExposure), reason=$($zMissingFailClosed.reason)"

$zMissingNoDriver = Resolve-KongorZeroWindowExposureDecision -HasHandle $false -RequireRenderWindow $false
Assert-True ([bool]$zMissingNoDriver.zeroWindowExposure -and $zMissingNoDriver.reason -eq "no_render_driver_loaded") `
    "ZeroWindowExposure passes only when no render driver is loaded yet"

$zVisibleOnScreen = Resolve-KongorZeroWindowExposureDecision -HasHandle $true -HandleHex "0x1234" -IsVisible $true -IsForeground $false -Rect ([ordered]@{ left = 0; top = 0; right = 2560; bottom = 1440 }) -WindowDesktop "Default" -WindowClass "K2_Vulkan"
Assert-True (-not [bool]$zVisibleOnScreen.zeroWindowExposure -and $zVisibleOnScreen.reason -eq "visible_on_desktop_or_foreground") `
    "ZeroWindowExposure fails when K2 window is visible on desktop"

$zOffscreenClean = Resolve-KongorZeroWindowExposureDecision -HasHandle $true -HandleHex "0x1234" -IsVisible $true -IsForeground $false -Rect ([ordered]@{ left = -32000; top = -32000; right = -29440; bottom = -30560 }) -WindowDesktop "Default" -WindowClass "K2_Vulkan"
Assert-True ([bool]$zOffscreenClean.zeroWindowExposure -and $zOffscreenClean.reason -eq "offscreen_coordinates") `
    "ZeroWindowExposure passes for offscreen (-32000,-32000) non-foreground window"

$zOffscreenForeground = Resolve-KongorZeroWindowExposureDecision -HasHandle $true -HandleHex "0x1234" -IsVisible $true -IsForeground $true -Rect ([ordered]@{ left = -32000; top = -32000; right = -29440; bottom = -30560 })
Assert-True (-not [bool]$zOffscreenForeground.zeroWindowExposure) `
    "ZeroWindowExposure fails if target process owns foreground focus"

$zNullRectFailClosed = Resolve-KongorZeroWindowExposureDecision -HasHandle $true -HandleHex "0x1234" -IsVisible $false -IsForeground $false -Rect $null
Assert-True ((-not [bool]$zNullRectFailClosed.zeroWindowExposure) -and $zNullRectFailClosed.reason -eq "window_rect_unavailable") `
    "ZeroWindowExposure fails closed when handle exists but GetWindowRect returned null (cross-desktop guard)" `
    "got zero=$($zNullRectFailClosed.zeroWindowExposure), reason=$($zNullRectFailClosed.reason)"

# 2. vid_vulkan_spy.log parser (both colon & equals formats + byte offset seeking)
$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kongor_shj_perf_test_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
try {
    $spyPath = Join-Path $tmpDir "vid_vulkan_spy.log"
    $prefixLine = "[12:00:00] [HIGH_HZ_TELEMETRY] AvgFPS: 10.00 | AvgDraws: 50.0 | Samples: 100`r`n"
    [System.IO.File]::WriteAllText($spyPath, $prefixLine, [System.Text.Encoding]::UTF8)
    $offsetAfterPrefix = (Get-Item -LiteralPath $spyPath).Length
    $windowLines = @(
        "[12:00:01] [HIGH_HZ_TELEMETRY] AvgFPS: 200.00 | AvgDraws: 1200.0 | Samples: 200",
        "[12:00:02] [HIGH_HZ_TELEMETRY] AvgFPS=190.00 AvgDraws=1300.0 Samples=190"
    ) -join "`r`n"
    [System.IO.File]::AppendAllText($spyPath, $windowLines + "`r`n", [System.Text.Encoding]::UTF8)

    $spyRes = Read-KongorSpyLogWindowMetrics -SpyLogPath $spyPath -StartByteOffset $offsetAfterPrefix
    Assert-True ([bool]$spyRes.available -and $spyRes.linesParsed -eq 2 -and $spyRes.totalSamples -eq 390) `
        "SpyLog parser skips pre-window prefix and parses both ':' and '=' formats" `
        "lines=$($spyRes.linesParsed), samples=$($spyRes.totalSamples)"
    Assert-True ([Math]::Abs([double]$spyRes.harmonicAvgFps - 195.0) -lt 0.05) `
        "SpyLog parser computes exact harmonic FPS (390 samples / 2.0s = 195.0)" `
        "got=$($spyRes.harmonicAvgFps)"

    # 3. dxvk.conf parser strips inline comments and scrubs [USER_DIR]
    $dxvkPath = Join-Path $tmpDir "dxvk.conf"
    Set-Content -LiteralPath $dxvkPath -Value @(
        "# Header comment",
        "d3d9.maxFrameLatency = 2 # competitive High-Hz",
        "dxvk.latencySleep = False # avoid micro-stutter"
    ) -Encoding UTF8
    $dxvkRes = Get-KongorDxvkConfSummary -DxvkPath $dxvkPath
    Assert-True ($dxvkRes.exists -and $dxvkRes.settings["d3d9.maxFrameLatency"] -eq "2" -and $dxvkRes.settings["dxvk.latencySleep"] -eq "False") `
        "dxvk.conf parser strips inline '#' comments cleanly" `
        "maxLat='$($dxvkRes.settings['d3d9.maxFrameLatency'])', latSleep='$($dxvkRes.settings['dxvk.latencySleep'])'"
} finally {
    Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 4. Path scrubbing ([USER_DIR])
$userProf = $env:USERPROFILE
if (-not [string]::IsNullOrWhiteSpace($userProf)) {
    $sampleWin = Join-Path $userProf "PycharmProjects\ProjectKongor\logs\shj\perf_canary_receipt.json"
    $sampleFwd = $sampleWin.Replace('\', '/')
    $scrubWin = ConvertTo-KongorSanitizedPath -InputPath $sampleWin
    $scrubFwd = ConvertTo-KongorSanitizedPath -InputPath $sampleFwd
    Assert-True ($scrubWin.StartsWith("[USER_DIR]\") -and (-not $scrubWin.Contains($userProf))) `
        "ConvertTo-KongorSanitizedPath scrubs Windows USERPROFILE paths" "got=$scrubWin"
    Assert-True ($scrubFwd.StartsWith("[USER_DIR]/") -and (-not $scrubFwd.Contains($userProf.Replace('\', '/')))) `
        "ConvertTo-KongorSanitizedPath scrubs forward-slash USERPROFILE paths" "got=$scrubFwd"
}

# 5. Merge-KongorPerfWindowMetrics on [ordered]@{} arrays (regression test for Measure-Object crash)
$w1 = New-CleanWindowMetrics -AvgFps 194.0 -MedFps 200.0 -P1Low 58.0 -P99Ms 14.0 -Drops 0 -SpyDivPct 0.12
$w2 = New-CleanWindowMetrics -AvgFps 196.0 -MedFps 202.0 -P1Low 61.0 -P99Ms 12.5 -Drops 2 -SpyDivPct 0.04
$w3 = New-CleanWindowMetrics -AvgFps 195.0 -MedFps 201.0 -P1Low 60.0 -P99Ms 13.0 -Drops 0 -SpyDivPct 0.19
$merged = Merge-KongorPerfWindowMetrics -Windows @($w1, $w2, $w3) -EscalatedToThree $true
Assert-True ($merged.windowsRun -eq 3 -and $merged.averageFps -eq 195.0 -and $merged.medianFps -eq 201.0 -and $merged.p1LowFps -eq 60.0 -and $merged.p99FrameTimeMs -eq 13.0) `
    "Merge-KongorPerfWindowMetrics computes 3-window medians on [ordered]@{} dictionaries" `
    "avg=$($merged.averageFps), med=$($merged.medianFps), 1%=$($merged.p1LowFps)"
Assert-True ($merged.droppedRingFrames -eq 2 -and $merged.spyDivergencePct -eq 0.19) `
    "Merge-KongorPerfWindowMetrics sums droppedRingFrames and takes max spyDivergencePct" `
    "drops=$($merged.droppedRingFrames), div=$($merged.spyDivergencePct)"

# 6. Budget evaluator rules (hard invariants, baseline regression, borderline escalation)
$baseline = [ordered]@{
    medianFps        = 202.0
    averageFps       = 195.5
    p1LowFps         = 60.0
    p99FrameTimeMs   = 13.0
    rateGt10msPerSec = 1.5
}

$evalClean = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True ($evalClean.passed -and (-not $evalClean.isBorderline)) `
    "Clean competitive window passes with zero borderline flags"

$evalNoSpy = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -SpyAvailable $false) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True ((-not $evalNoSpy.passed) -and (($evalNoSpy.failures -join "|") -match "MCP-296 cross-check FAILED")) `
    "Budget evaluator fails closed when vid_vulkan_spy.log emitted 0 samples"

$evalSpyDiv = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -SpyDivPct 3.4) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True ((-not $evalSpyDiv.passed) -and (($evalSpyDiv.failures -join "|") -match "divergence gate FAILED")) `
    "Budget evaluator fails when FastBus vs spy divergence > 3.0%"

$evalGpuStall = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -GpuStall 1) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True (-not $evalGpuStall.passed) "Budget evaluator fails on gpuStall > 0"

$evalGt33 = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -Gt33 1) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True (-not $evalGt33.passed) "Budget evaluator fails on >33.33ms hitch > 0"

$evalDrops = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -Drops 1) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True (-not $evalDrops.passed) "Budget evaluator fails on droppedRingFrames > 0"

$evalMedDrop = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -MedFps 190.0) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True ((-not $evalMedDrop.passed) -and (($evalMedDrop.failures -join "|") -match "MedianFPS regression")) `
    "Budget evaluator catches >5% MedianFPS drop vs baseline"

$evalBorderline = Evaluate-KongorPerfWindowAgainstBudgets -Metrics (New-CleanWindowMetrics -P999Ms 22.5) -TargetFps 200 -WindowSeconds 20 -Baseline $baseline
Assert-True ($evalBorderline.passed -and $evalBorderline.isBorderline) `
    "Budget evaluator flags borderline P99.9 > 21ms for 3-window confirmation"

Write-Host ("`n[SHJ-PERF-TEST] Summary: {0} passed, {1} failed." -f $passed, $failed) -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
if ($failed -gt 0) {
    throw ("SHJ Perf offline tests failed:`n - " + ($failures -join "`n - "))
}
exit 0
