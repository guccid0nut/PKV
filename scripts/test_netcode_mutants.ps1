# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Hermetic 14-Case Mutant Matrix for scripts/verify_netcode_oracle.ps1 (Phase 2 Milestone 2.1 + Phase 4 Rec #6).
.DESCRIPTION
    Verifies that scripts/verify_netcode_oracle.ps1 catches real-seam defects across all 7 tiers
    (PE64 Export Table corruption with EOF decoy, PE64 .text SIMD stack-alignment corruption,
    PE64 .text FMA Round-to-Odd double-rounding tie-trap corruption, PKZIP golden replay bit-flips
    and missing-second-replay traps, and comment-out mutants)
    and enforces dual-file SHA-256 integrity pinning in scripts/.netcode_oracle_integrity.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [switch]$UpdateIntegrityPin = $false,

    [Parameter(Mandatory = $false)]
    [switch]$KeepScratch = $false,

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$oracleScript = Join-Path $PSScriptRoot "verify_netcode_oracle.ps1"
$integrityFile = Join-Path $PSScriptRoot ".netcode_oracle_integrity"
$harnessModule = Join-Path $PSScriptRoot "lib\MutantHarness.psm1"

Import-Module $harnessModule -Force

$baselineRelativeFiles = @(
    "games\client\version.dll",
    "native\include\kongor_deterministic_math.h",
    "native\proxies\native_guard\dllmain.cpp",
    "native\proxies\native_guard\predictive_pathing.cpp",
    "native\proxies\native_guard\simd_ray_bridge.cpp",
    "tests\fixtures\replays\golden_M2060568725_slice.honreplay",
    "tests\fixtures\replays\golden_M70_slice.honreplay",
    "infra\ebpf\xdp_kongor_filter.c",
    "infra\ebpf\load_xdp.sh",
    "src\apps\compel\Services\Proxy\XdpPacketFilterManager.cs",
    "src\apps\compel\Services\Supervision\ServerKeyframeSharedMemory.cs",
    "src\apps\compel\Services\Proxy\UDPForwarder.cs",
    "src\apps\compel\Services\Proxy\UDPProxyService.cs",
    "src\apps\compel\Services\Supervision\DedicatedServerCrashResilienceService.cs",
    "src\apps\compel\Services\Supervision\MatchServerManagerSupervisor.cs",
    "src\services\master-server\Controllers\MatchesV5Controller.cs",
    "src\extensions\spatial-audio\native\src\FmodExProxy.h",
    "src\extensions\spatial-audio\native\src\FmodExProxy.cpp",
    "src\extensions\spatial-audio\src\SpatialAudioEngine.cs",
    "src\apps\compel\Services\Proxy\Congestion\BbrCongestionController.cs"
)

if ($UpdateIntegrityPin) {
    & $oracleScript -UpdateReplayPin | Out-Null
    $pinObj = Get-Content -LiteralPath $integrityFile -Raw | ConvertFrom-Json
    $pinObj.verifyNetcodeOraclePs1Sha256 = (Get-FileHash -LiteralPath $oracleScript -Algorithm SHA256).Hash
    $pinObj.testNetcodeMutantsPs1Sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $pinObj | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $integrityFile -Encoding UTF8
    Write-Host ("[INTEGRITY PIN UPDATED] {0}" -f $integrityFile) -ForegroundColor Cyan
}

# Verify dual-file integrity pin first
if (-not (Test-Path -LiteralPath $integrityFile)) {
    throw ("Missing integrity pin file '{0}'. Run .\scripts\test_netcode_mutants.ps1 -UpdateIntegrityPin to initialize." -f $integrityFile)
}
$pinData = Get-Content -LiteralPath $integrityFile -Raw | ConvertFrom-Json
$actualOracleHash = (Get-FileHash -LiteralPath $oracleScript -Algorithm SHA256).Hash
$actualMutantHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash

if ($pinData.verifyNetcodeOraclePs1Sha256 -ne $actualOracleHash -or $pinData.testNetcodeMutantsPs1Sha256 -ne $actualMutantHash) {
    throw ("Integrity pin mismatch in '{0}' (OracleMatch={1}, SuiteMatch={2}). Run .\scripts\test_netcode_mutants.ps1 -UpdateIntegrityPin after reviewing changes." -f
        $integrityFile,
        ($pinData.verifyNetcodeOraclePs1Sha256 -eq $actualOracleHash),
        ($pinData.testNetcodeMutantsPs1Sha256 -eq $actualMutantHash))
}

function Invoke-OracleInWorkspace {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Workspace,
        [Parameter(Mandatory = $false)]
        [switch]$SimulateCrash = $false
    )
    $jsonOut = Join-Path $Workspace ("oracle_out_{0}.json" -f ([guid]::NewGuid().ToString("N").Substring(0, 8)))
    $argsList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $oracleScript, "-TargetRoot", $Workspace, "-JsonOutput", $jsonOut)
    if ($SimulateCrash) {
        $argsList += "-SimulateCrash"
    }
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argsList -NoNewWindow -Wait -PassThru
    $exitCode = $proc.ExitCode
    $parsedJson = $null
    if (Test-Path -LiteralPath $jsonOut) {
        $parsedJson = Get-Content -LiteralPath $jsonOut -Raw | ConvertFrom-Json
    }
    return [PSCustomObject]@{
        ExitCode = $exitCode
        Json     = $parsedJson
    }
}

$suiteSw = [System.Diagnostics.Stopwatch]::StartNew()
$results = New-Object System.Collections.Generic.List[object]
$tierCoverage = [ordered]@{
    "T1" = @{ Mutants = 0; Caught = 0 }
    "T2" = @{ Mutants = 0; Caught = 0 }
    "T3" = @{ Mutants = 0; Caught = 0 }
    "T4" = @{ Mutants = 0; Caught = 0 }
    "T5" = @{ Mutants = 0; Caught = 0 }
    "T6" = @{ Mutants = 0; Caught = 0 }
    "T7" = @{ Mutants = 0; Caught = 0 }
}

function Invoke-MutantCase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $false)]
        [string[]]$TargetTiers = @(),
        [Parameter(Mandatory = $false)]
        [string[]]$FilesToCopy = $baselineRelativeFiles,
        [Parameter(Mandatory = $true)]
        [scriptblock]$MutateAndAssert
    )

    foreach ($t in $TargetTiers) {
        if ($tierCoverage.Contains($t)) {
            $tierCoverage[$t].Mutants++
        }
    }

    $caseSw = [System.Diagnostics.Stopwatch]::StartNew()
    $ws = New-KongorMutantWorkspace -Prefix ("netcode_" + $Name) -SourceRoot $repoRoot -RelativeFiles $FilesToCopy
    $passed = $false
    $detail = ""
    try {
        $verdict = & $MutateAndAssert $ws
        $passed = [bool]$verdict.Passed
        $detail = [string]$verdict.Detail
        if ($passed) {
            foreach ($t in $TargetTiers) {
                if ($tierCoverage.Contains($t)) {
                    $tierCoverage[$t].Caught++
                }
            }
        }
    } catch {
        $passed = $false
        $detail = "Exception: " + $_.Exception.Message
    } finally {
        $caseSw.Stop()
        Remove-KongorMutantWorkspace -WorkspacePath $ws -KeepScratch:$KeepScratch
    }

    $color = if ($passed) { "Green" } else { "Red" }
    $tag = if ($passed) { "PASS" } else { "FAIL" }
    Write-Host ("  [{0}] {1,-44} ({2,4}ms) - {3}" -f $tag, $Name, $caseSw.ElapsedMilliseconds, $detail) -ForegroundColor $color

    $results.Add([ordered]@{
        name       = $Name
        status     = $tag
        passed     = $passed
        durationMs = $caseSw.ElapsedMilliseconds
        detail     = $detail
    })
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " KONGOR 14-CASE HERMETIC NETCODE & REPLAY MUTANT SUITE" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

# 1. Baseline_And_Determinism
Invoke-MutantCase -Name "1_Baseline_And_Determinism" -MutateAndAssert {
    param($ws)
    $r1 = Invoke-OracleInWorkspace -Workspace $ws
    $r2 = Invoke-OracleInWorkspace -Workspace $ws
    $detMatch = ($r1.ExitCode -eq 0) -and ($r2.ExitCode -eq 0) -and
                ($r1.Json.tiers.T1.mxcsr -eq $r2.Json.tiers.T1.mxcsr) -and
                ($r1.Json.tiers.T3.validReplays -eq 2) -and
                ($r1.Json.tiers.T4.hitMask -eq 135) -and
                ([bool]$r1.Json.tiers.T4.fmaCodePass)
    return @{ Passed = $detMatch; Detail = ("Exit={0}/{1}, MXCSR={2}, Replays={3}, Mask=0x{4:X2}, FmaRTO={5}" -f $r1.ExitCode, $r2.ExitCode, $r1.Json.tiers.T1.mxcsr, $r1.Json.tiers.T3.validReplays, $r1.Json.tiers.T4.hitMask, $r1.Json.tiers.T4.fmaCodePass) }
}

# 2. Mutant_Null_Scratch_Control
Invoke-MutantCase -Name "2_Mutant_Null_Scratch_Control" -MutateAndAssert {
    param($ws)
    $r = Invoke-OracleInWorkspace -Workspace $ws
    return @{ Passed = ($r.ExitCode -eq 0 -and $r.Json.overallStatus -eq "PASS"); Detail = ("Null mutation control ExitCode={0}" -f $r.ExitCode) }
}

# 3. Mutant_Crash_Trap
Invoke-MutantCase -Name "3_Mutant_Crash_Trap" -FilesToCopy @() -MutateAndAssert {
    param($ws)
    $r = Invoke-OracleInWorkspace -Workspace $ws -SimulateCrash
    return @{ Passed = ($r.ExitCode -eq 1); Detail = ("Unhandled crash trapped with 8-bit safe ExitCode={0} (expected 1)" -f $r.ExitCode) }
}

# 4. Mutant_EmptyRoot_Leak_Guard
Invoke-MutantCase -Name "4_Mutant_EmptyRoot_Leak_Guard" -FilesToCopy @() -MutateAndAssert {
    param($ws)
    $r = Invoke-OracleInWorkspace -Workspace $ws
    return @{ Passed = ($r.ExitCode -eq 254); Detail = ("Empty TargetRoot ExitCode={0} (expected 254 = 2|4|8|16|32|64|128)" -f $r.ExitCode) }
}

# 5. Mutant_PartialRoot_HalfTree
Invoke-MutantCase -Name "5_Mutant_PartialRoot_HalfTree" -FilesToCopy @(
    "games\client\version.dll",
    "native\include\kongor_deterministic_math.h",
    "native\proxies\native_guard\dllmain.cpp",
    "native\proxies\native_guard\predictive_pathing.cpp",
    "native\proxies\native_guard\simd_ray_bridge.cpp",
    "tests\fixtures\replays\golden_M2060568725_slice.honreplay",
    "tests\fixtures\replays\golden_M70_slice.honreplay"
) -MutateAndAssert {
    param($ws)
    $r = Invoke-OracleInWorkspace -Workspace $ws
    $expected = (32 -bor 64 -bor 128) # 224: T1..T4 PASS, T5..T7 FAIL
    return @{ Passed = ($r.ExitCode -eq $expected); Detail = ("Partial root (T1..T4 only) ExitCode={0} (expected {1})" -f $r.ExitCode, $expected) }
}

# 6. M1_PeExportTable_Corrupt_With_EofGrepDecoy (Tier 1)
Invoke-MutantCase -Name "6_M1_PeExportTable_Corrupt_With_Decoy" -TargetTiers @("T1") -MutateAndAssert {
    param($ws)
    $dllPath = Join-Path $ws "games\client\version.dll"
    $pe = Get-KongorPe64Exports -BinaryPath $dllPath
    $nameOff = [int]$pe.Offsets["Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism"]
    $bytes = [System.IO.File]::ReadAllBytes($dllPath)
    # Corrupt 1 byte in the PE64 Export Name Table
    $bytes[$nameOff] = [byte][char]'X'
    # Append the full literal string at EOF as a decoy (which would fool raw ASCII grep!)
    $decoy = [System.Text.Encoding]::ASCII.GetBytes("`0Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism`0")
    $combined = New-Object byte[] ($bytes.Length + $decoy.Length)
    [Array]::Copy($bytes, 0, $combined, 0, $bytes.Length)
    [Array]::Copy($decoy, 0, $combined, $bytes.Length, $decoy.Length)
    [System.IO.File]::WriteAllBytes($dllPath, $combined)

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 2) -ne 0) -and ($r.Json.tiers.T1.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T1.status={1} (PE64 export corrupt caught despite EOF grep decoy)" -f $r.ExitCode, $r.Json.tiers.T1.status) }
}

# 7. M2_SubTick_Comparator_Inversion (Tier 2)
Invoke-MutantCase -Name "7_M2_SubTick_Comparator_Inversion" -TargetTiers @("T2") -MutateAndAssert {
    param($ws)
    $cppPath = Join-Path $ws "native\proxies\native_guard\dllmain.cpp"
    $raw = Get-Content -LiteralPath $cppPath -Raw
    $mutated = $raw -replace 'hardwareTimestampQpc\s*<\s*other\.hardwareTimestampQpc', 'hardwareTimestampQpc > other.hardwareTimestampQpc'
    Set-Content -LiteralPath $cppPath -Value $mutated -Encoding UTF8

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 4) -ne 0) -and ($r.Json.tiers.T2.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T2.status={1}" -f $r.ExitCode, $r.Json.tiers.T2.status) }
}

# 8. M3_Tracked_Replay_BitFlip (Tier 3)
Invoke-MutantCase -Name "8_M3_Tracked_Replay_BitFlip" -TargetTiers @("T3") -MutateAndAssert {
    param($ws)
    $repPath = Join-Path $ws "tests\fixtures\replays\golden_M70_slice.honreplay"
    $bytes = [System.IO.File]::ReadAllBytes($repPath)
    $bytes[100] = $bytes[100] -bxor 0xFF
    [System.IO.File]::WriteAllBytes($repPath, $bytes)

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 8) -ne 0) -and ($r.Json.tiers.T3.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T3.status={1}, ValidReplays={2}/2" -f $r.ExitCode, $r.Json.tiers.T3.status, $r.Json.tiers.T3.validReplays) }
}

# 9. M4_Tracked_Replay_Missing_Second (Tier 3)
Invoke-MutantCase -Name "9_M4_Tracked_Replay_Missing_Second" -TargetTiers @("T3") -MutateAndAssert {
    param($ws)
    $repPath = Join-Path $ws "tests\fixtures\replays\golden_M70_slice.honreplay"
    Remove-Item -LiteralPath $repPath -Force

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 8) -ne 0) -and ($r.Json.tiers.T3.status -eq "FAIL") -and ($r.Json.tiers.T3.validReplays -eq 1)
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T3.status={1}, ValidReplays={2}/2" -f $r.ExitCode, $r.Json.tiers.T3.status, $r.Json.tiers.T3.validReplays) }
}

# 10. M5_Simd_Text_StackAlign_Corrupt (Tier 4)
Invoke-MutantCase -Name "10_M5_Simd_Text_StackAlign_Corrupt" -TargetTiers @("T4") -MutateAndAssert {
    param($ws)
    $dllPath = Join-Path $ws "games\client\version.dll"
    $pe = Get-KongorPe64Exports -BinaryPath $dllPath
    $fnOff = [int]$pe.FunctionOffsets["Kongor_NativeGuard_SimdRayPacket8_IntersectAABB"]
    $bytes = [System.IO.File]::ReadAllBytes($dllPath)
    # Corrupt the 32-byte AVX2 stack alignment instruction (48 83 E5 E0 at offset +26) in .text
    for ($k = $fnOff; $k -lt ($fnOff + 64); $k++) {
        if ($bytes[$k] -eq 0x48 -and $bytes[$k+1] -eq 0x83 -and $bytes[$k+2] -eq 0xE5 -and $bytes[$k+3] -eq 0xE0) {
            $bytes[$k+3] = 0xF0
            break
        }
    }
    [System.IO.File]::WriteAllBytes($dllPath, $bytes)

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 16) -ne 0) -and ($r.Json.tiers.T4.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T4.status={1} (.text 32B stack alignment corruption caught)" -f $r.ExitCode, $r.Json.tiers.T4.status) }
}

# 11. M6_Ebpf_CommentOut_Filter (Tier 5)
Invoke-MutantCase -Name "11_M6_Ebpf_CommentOut_Filter" -TargetTiers @("T5") -MutateAndAssert {
    param($ws)
    $cPath = Join-Path $ws "infra\ebpf\xdp_kongor_filter.c"
    $raw = Get-Content -LiteralPath $cPath -Raw
    $mutated = "/*`n" + $raw + "`n*/"
    Set-Content -LiteralPath $cPath -Value $mutated -Encoding UTF8

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 32) -ne 0) -and ($r.Json.tiers.T5.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T5.status={1} (commented-out eBPF filter caught)" -f $r.ExitCode, $r.Json.tiers.T5.status) }
}

# 12. M7_CommentOut_Keyframe_HeaderSize (Tier 6)
Invoke-MutantCase -Name "12_M7_CommentOut_Keyframe_HeaderSize" -TargetTiers @("T6") -MutateAndAssert {
    param($ws)
    $csPath = Join-Path $ws "src\apps\compel\Services\Supervision\ServerKeyframeSharedMemory.cs"
    $raw = Get-Content -LiteralPath $csPath -Raw
    $mutated = $raw -replace 'public const int HeaderSizeBytes = 40;', '// public const int HeaderSizeBytes = 40;'
    Set-Content -LiteralPath $csPath -Value $mutated -Encoding UTF8

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 64) -ne 0) -and ($r.Json.tiers.T6.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T6.status={1} (commented-out HeaderSizeBytes = 40 caught)" -f $r.ExitCode, $r.Json.tiers.T6.status) }
}

# 13. M8_CommentOut_SpatialAudio_ListenerPose (Tier 7)
Invoke-MutantCase -Name "13_M8_CommentOut_SpatialAudio_Pose" -TargetTiers @("T7") -MutateAndAssert {
    param($ws)
    $hPath = Join-Path $ws "src\extensions\spatial-audio\native\src\FmodExProxy.h"
    $raw = Get-Content -LiteralPath $hPath -Raw
    $mutated = $raw -replace 'Kongor_SpatialAudio_GetListenerPose', '// Kongor_SpatialAudio_GetListenerPose'
    Set-Content -LiteralPath $hPath -Value $mutated -Encoding UTF8

    $r = Invoke-OracleInWorkspace -Workspace $ws
    $caught = (($r.ExitCode -band 128) -ne 0) -and ($r.Json.tiers.T7.status -eq "FAIL")
    return @{ Passed = $caught; Detail = ("ExitCode={0}, T7.status={1} (commented-out ListenerPose declaration caught)" -f $r.ExitCode, $r.Json.tiers.T7.status) }
}

# 14. M9_DoubleRounded_Fma_Tie_Mutant (Tier 4)
Invoke-MutantCase -Name "14_M9_DoubleRounded_Fma_Tie_Mutant" -TargetTiers @("T4") -MutateAndAssert {
    param($ws)
    $dllPath = Join-Path $ws "games\client\version.dll"
    $pe = Get-KongorPe64Exports -BinaryPath $dllPath
    $fmaExportOff = [int]$pe.FunctionOffsets["Kongor_NativeGuard_FmaRoundToOdd_f32"]
    $origBytes = [System.IO.File]::ReadAllBytes($dllPath)
    $bytes = [byte[]]$origBytes.Clone()
    $fmaLeafOff = $fmaExportOff
    if ($bytes[$fmaExportOff] -eq 0xE9) {
        $rel32 = [BitConverter]::ToInt32($bytes, $fmaExportOff + 1)
        $fmaLeafOff = $fmaExportOff + 5 + $rel32
    }
    # Sub-check A: Patch 'test cl, 1; jnz +0x1F' (F6 C1 01 75) to 'test cl, 1; jmp +0x1F' (F6 C1 01 EB) in .text
    # This converts the compiled x64 leaf function into naive double-rounding ((float)((double)a * (double)b + (double)c))
    # while leaving all C++ source files untouched!
    $patchedBin = $false
    for ($k = $fmaLeafOff; $k -lt ($fmaLeafOff + 200); $k++) {
        if ($bytes[$k] -eq 0xF6 -and $bytes[$k+1] -eq 0xC1 -and $bytes[$k+2] -eq 0x01 -and $bytes[$k+3] -eq 0x75) {
            $bytes[$k+3] = 0xEB
            $patchedBin = $true
            break
        }
    }
    [System.IO.File]::WriteAllBytes($dllPath, $bytes)
    $rBin = Invoke-OracleInWorkspace -Workspace $ws
    $binCaught = $patchedBin -and (($rBin.ExitCode -band 16) -ne 0) -and ($rBin.Json.tiers.T4.status -eq "FAIL") -and
                 ($rBin.Json.tiers.T4.fmaCodePass -eq $false) -and ($rBin.Json.tiers.T4.sourceContract -eq $true)

    # Sub-check B: Restore version.dll and strip Round-to-Odd sticky-bit adjustment in kongor_deterministic_math.h
    [System.IO.File]::WriteAllBytes($dllPath, $origBytes)
    $mathPath = Join-Path $ws "native\include\kongor_deterministic_math.h"
    $mathRaw = Get-Content -LiteralPath $mathPath -Raw
    $mathMut = ($mathRaw -replace 's_bits\s*\+=\s*1ULL\s*;', '/* stripped RTO += 1 */') -replace 's_bits\s*-=\s*1ULL\s*;', '/* stripped RTO -= 1 */'
    Set-Content -LiteralPath $mathPath -Value $mathMut -Encoding UTF8
    $rSrc = Invoke-OracleInWorkspace -Workspace $ws
    $srcCaught = (($rSrc.ExitCode -band 16) -ne 0) -and ($rSrc.Json.tiers.T4.status -eq "FAIL") -and
                 ($rSrc.Json.tiers.T4.fmaCodePass -eq $true) -and ($rSrc.Json.tiers.T4.sourceContract -eq $false)

    $bothCaught = ($binCaught -and $srcCaught)
    return @{ Passed = $bothCaught; Detail = ("BinMutantCaught={0} (Exit={1}), SrcMutantCaught={2} (Exit={3})" -f $binCaught, $rBin.ExitCode, $srcCaught, $rSrc.ExitCode) }
}

$suiteSw.Stop()
$passCount = @($results | Where-Object { $_.passed }).Count
$totalCount = $results.Count
$unauditedTiers = @($tierCoverage.Keys | Where-Object { $tierCoverage[$_].Mutants -eq 0 })

Write-Host "`n--------------------------------------------------------------------------------" -ForegroundColor Cyan
Write-Host " PER-TIER MUTATION COVERAGE LEDGER" -ForegroundColor Cyan
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor Cyan
foreach ($tk in $tierCoverage.Keys) {
    $m = $tierCoverage[$tk].Mutants
    $c = $tierCoverage[$tk].Caught
    $statusLabel = if ($m -eq 0) { "UNAUDITED" } elseif ($c -eq $m) { "AUDITED_100%" } else { "PARTIAL" }
    $col = if ($statusLabel -eq "AUDITED_100%") { "Green" } else { "Red" }
    Write-Host ("  Tier {0}: Mutants={1}, Caught={2} [{3}]" -f $tk, $m, $c, $statusLabel) -ForegroundColor $col
}

if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    $payload = [ordered]@{
        passedCases    = $passCount
        totalCases     = $totalCount
        unauditedTiers = $unauditedTiers
        durationMs     = $suiteSw.ElapsedMilliseconds
        tierCoverage   = $tierCoverage
        cases          = $results.ToArray()
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $JsonOutput -Encoding UTF8
}

Write-Host "================================================================================" -ForegroundColor Cyan
if ($passCount -eq $totalCount -and $unauditedTiers.Count -eq 0) {
    Write-Host (" [NETCODE MUTANT MATRIX] GREEN - {0}/{1} cases passed, 7/7 tiers audited in {2:N2}s!" -f $passCount, $totalCount, $suiteSw.Elapsed.TotalSeconds) -ForegroundColor Green
    exit 0
} else {
    Write-Host (" [NETCODE MUTANT MATRIX] RED - {0}/{1} cases passed, UnauditedTiers={2}." -f $passCount, $totalCount, $unauditedTiers.Count) -ForegroundColor Red
    exit 1
}
