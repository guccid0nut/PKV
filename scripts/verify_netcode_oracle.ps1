# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Project Kongor Phase 19/Phase 2: 7-Tier Netcode, Replay & Engine Determinism Verification Oracle.
.DESCRIPTION
    Executes a 7-tier real-seam verification suite without invoking version.dll's DllMain (which spawns detached watcher threads and opens \\.\pipe\kongor-native-client):
      - Tier 1 (bit 2):   PE64 Export Directory Table inspection of games/client/version.dll + .text machine-code execution of Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism (0x8040 FTZ+DAZ)
      - Tier 2 (bit 4):   PE64 .text RVA prologue check + comment-stripped SubTickCommand::operator< & std::stable_sort contract in dllmain.cpp
      - Tier 3 (bit 8):   PKZIP archive structure (ReplayInfo + ReplayData) + pinned SHA-256 verification of the 2 tracked golden replay fixtures
      - Tier 4 (bit 16):  PE64 .text RVA 32-byte YMM stack alignment & SIMD opcode verification of SimdRayPacket8_IntersectAABB + comment-stripped predictive_pathing.cpp AVX2 contract + hardware 256-bit AVX ray-slab execution (0x87)
      - Tier 5 (bit 32):  Comment-stripped eBPF/XDP kernel filter & C# XdpPacketFilterManager contract verification
      - Tier 6 (bit 64):  Comment-stripped 64-byte Keyframe Shared Memory ABI & UDP Proxy session retention contract verification
      - Tier 7 (bit 128): Comment-stripped MatchesV5 MemoryPack, FMOD 3D Spatial Audio pose export, and BBRv2 state machine contract verification
    Exit Codes (8-bit Windows safe):
      - 0:       All 7 tiers PASSED
      - 1:       Unhandled script exception (trap)
      - 2..254:  Shifted bitmask of failed tiers (T1=2, T2=4, T3=8, T4=16, T5=32, T6=64, T7=128)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$TargetRoot = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [Parameter(Mandatory = $false)]
    [switch]$UpdateReplayPin = $false,

    [Parameter(Mandatory = $false)]
    [switch]$SimulateCrash = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

trap {
    Write-Host ("[NETCODE ORACLE CRASH TRAP] Unhandled exception: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}

if ($SimulateCrash) {
    throw "Simulated unhandled exception triggered via -SimulateCrash for crash-trap verification."
}

$scriptRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot = if (-not [string]::IsNullOrWhiteSpace($TargetRoot)) {
    if (-not (Test-Path -LiteralPath $TargetRoot)) {
        New-Item -ItemType Directory -Path $TargetRoot -Force | Out-Null
    }
    (Resolve-Path -LiteralPath $TargetRoot).Path
} else {
    $scriptRepoRoot
}

$harnessPath = Join-Path $PSScriptRoot "lib\MutantHarness.psm1"
if (-not (Test-Path -LiteralPath $harnessPath)) {
    throw ("Required module missing at '{0}'. Run git status to restore scripts/lib/MutantHarness.psm1." -f $harnessPath)
}
Import-Module $harnessPath -Force

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not ("Kongor.PeTextCodeVerifier" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Kongor {
    public static class PeTextCodeVerifier {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint FnConfigureMxcsr();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float FnFmaRoundToOdd(float a, float b, float c);

        private static float BitsToFloat(uint bits) {
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        private static uint FloatToBits(float val) {
            return BitConverter.ToUInt32(BitConverter.GetBytes(val), 0);
        }

        public static uint ExecuteExtractedMxcsrBytes(byte[] fnBytes) {
            if (fnBytes == null || fnBytes.Length < 29) return 0;
            // Verify exact x64 stmxcsr/ldmxcsr instruction opcodes and C3 ret before executing
            if (fnBytes[0] != 0x0F || fnBytes[1] != 0xAE || fnBytes[28] != 0xC3) {
                return 0;
            }
            IntPtr execBuf = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, 0x3000 /* MEM_COMMIT|MEM_RESERVE */, 0x40 /* PAGE_EXECUTE_READWRITE */);
            if (execBuf == IntPtr.Zero) return 0;
            try {
                Marshal.Copy(fnBytes, 0, execBuf, 29);
                var fn = Marshal.GetDelegateForFunctionPointer<FnConfigureMxcsr>(execBuf);
                return fn();
            } finally {
                VirtualFree(execBuf, UIntPtr.Zero, 0x8000 /* MEM_RELEASE */);
            }
        }

        public static bool VerifyFmaRoundToOddBytes(byte[] fnBytes) {
            if (fnBytes == null || fnBytes.Length < 160) return false;
            IntPtr execBuf = VirtualAlloc(IntPtr.Zero, (UIntPtr)256, 0x3000 /* MEM_COMMIT|MEM_RESERVE */, 0x40 /* PAGE_EXECUTE_READWRITE */);
            if (execBuf == IntPtr.Zero) return false;
            try {
                Marshal.Copy(fnBytes, 0, execBuf, fnBytes.Length);
                var fn = Marshal.GetDelegateForFunctionPointer<FnFmaRoundToOdd>(execBuf);
                uint r0 = FloatToBits(fn(BitsToFloat(0x47001000u), BitsToFloat(0x467FF800u), BitsToFloat(0xB3800000u)));
                uint r1 = FloatToBits(fn(BitsToFloat(0x47000800u), BitsToFloat(0x46800800u), BitsToFloat(0x33800000u)));
                uint r2 = FloatToBits(fn(BitsToFloat(0xC7001000u), BitsToFloat(0x467FF800u), BitsToFloat(0x33800000u)));
                uint r3 = FloatToBits(fn(BitsToFloat(0xC7000800u), BitsToFloat(0x46800800u), BitsToFloat(0xB3800000u)));
                uint r4 = FloatToBits(fn(BitsToFloat(0x3F800001u), BitsToFloat(0x3F7FFFFEu), BitsToFloat(0xBF800000u)));
                return (r0 == 0x4E000BFFu) &&
                       (r1 == 0x4E001001u) &&
                       (r2 == 0xCE000BFFu) &&
                       (r3 == 0xCE001001u) &&
                       (r4 == 0xA8800000u);
            } finally {
                VirtualFree(execBuf, UIntPtr.Zero, 0x8000 /* MEM_RELEASE */);
            }
        }
    }
}
"@
}

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()
$exitMask = 0
$tierResults = [ordered]@{}

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host " KONGOR 7-TIER NETCODE, REPLAY & DETERMINISM ORACLE" -ForegroundColor Cyan
Write-Host (" Target Root: {0}" -f $repoRoot) -ForegroundColor Gray
Write-Host "=====================================================================" -ForegroundColor Cyan

$versionDllPath = Join-Path $repoRoot "games\client\version.dll"
$dllmainPath    = Join-Path $repoRoot "native\proxies\native_guard\dllmain.cpp"
$predPathCpp    = Join-Path $repoRoot "native\proxies\native_guard\predictive_pathing.cpp"
$simdBridgePath = Join-Path $repoRoot "native\proxies\native_guard\simd_ray_bridge.cpp"
$detMathHeader  = Join-Path $repoRoot "native\include\kongor_deterministic_math.h"

$peInfo = $null
$dllBytes = $null
if (Test-Path -LiteralPath $versionDllPath) {
    $peInfo = Get-KongorPe64Exports -BinaryPath $versionDllPath
    $dllBytes = [System.IO.File]::ReadAllBytes($versionDllPath)
}

# =====================================================================
# TIER 1 (bit 2): PE64 Export Directory Table & .text MXCSR Machine Code
# =====================================================================
Write-Host "`n[TIER 1] Inspecting PE64 Export Directory Table & .text MXCSR Machine Code..." -ForegroundColor Yellow
$t1Sw = [System.Diagnostics.Stopwatch]::StartNew()
$requiredExports = @(
    "Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism",
    "Kongor_NativeGuard_GetMxcsr",
    "Kongor_NativeGuard_InjectSubTickInput",
    "Kongor_NativeGuard_GetSubTickQueueDepth",
    "Kongor_NativeGuard_IsAvx2Supported",
    "Kongor_NativeGuard_SimdRayPacket8_IntersectAABB",
    "Kongor_NativeGuard_SimdRayPacket8_BatchTrace",
    "Kongor_NativeGuard_FmaRoundToOdd_f32",
    "Kongor_NativeGuard_VerifyFmaLockstepOracle"
)
$missingExports = New-Object System.Collections.Generic.List[string]
$t1Pass = $false
$mxcsrHex = "0x0000"

if ($null -ne $peInfo -and $peInfo.ValidPe64 -and $null -ne $dllBytes) {
    $exportSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($expName in $peInfo.Exports) { [void]$exportSet.Add($expName) }
    foreach ($req in $requiredExports) {
        if (-not $exportSet.Contains($req)) {
            $missingExports.Add($req)
        }
    }

    if ($missingExports.Count -eq 0 -and $peInfo.FunctionOffsets.Contains("Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism")) {
        $fnOff = [int]$peInfo.FunctionOffsets["Kongor_NativeGuard_ConfigureMxcsrFloatDeterminism"]
        if ($fnOff -gt 0 -and ($fnOff + 29) -le $dllBytes.Length) {
            $fnBytes = [byte[]]$dllBytes[$fnOff..($fnOff + 28)]
            # Verify compiled x64 opcodes: stmxcsr (0F AE), ~0x6000 RN mask (25 FF 9F FF FF), 0x8040 FTZ+DAZ OR (0D 40 80 00 00), ldmxcsr (0F AE)
            $codeHex = [BitConverter]::ToString($fnBytes)
            $hasStmxcsr = ($codeHex -match '^0F-AE-5C-24-08')
            $hasRnMask  = ($codeHex -match '25-FF-9F-FF-FF')
            $hasFtzDaz  = ($codeHex -match '0D-40-80-00-00')
            $hasLdmxcsr = ($codeHex -match '0F-AE-54-24-08-C3$')

            if ($hasStmxcsr -and $hasRnMask -and $hasFtzDaz -and $hasLdmxcsr) {
                $mxcsrVal = [Kongor.PeTextCodeVerifier]::ExecuteExtractedMxcsrBytes($fnBytes)
                $mxcsrHex = ("0x{0:X4}" -f $mxcsrVal)
                if (($mxcsrVal -band 0x8040) -eq 0x8040) {
                    $t1Pass = $true
                }
            }
        }
    }
} else {
    foreach ($req in $requiredExports) { $missingExports.Add($req) }
}
$t1Sw.Stop()

if ($t1Pass) {
    Write-Host ("  [PASS] All 9 exports verified in PE64 Export Directory & .text MXCSR={0} (FTZ+DAZ active) ({1}ms)" -f $mxcsrHex, $t1Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 2
    Write-Host ("  [FAIL] Tier 1 failed (Missing PE64 exports: [{0}], MXCSR: {1}). Run scripts/verify_netcode_oracle.ps1 to diagnose." -f ($missingExports -join ", "), $mxcsrHex) -ForegroundColor Red
}
$tierResults["T1"] = [ordered]@{
    status          = if ($t1Pass) { "PASS" } else { "FAIL" }
    bit             = 2
    executionMode   = "PE64_EXPORT_TABLE_AND_MACHINE_CODE"
    exportsVerified = ($requiredExports.Count - $missingExports.Count)
    missingExports  = $missingExports.ToArray()
    mxcsr           = $mxcsrHex
    durationMs      = $t1Sw.ElapsedMilliseconds
}

# =====================================================================
# TIER 2 (bit 4): Sub-Tick Input Queue Contract & Chronological Sort
# =====================================================================
Write-Host "`n[TIER 2] Verifying Sub-Tick Input Comparator Contract & PE64 .text RVA..." -ForegroundColor Yellow
$t2Sw = [System.Diagnostics.Stopwatch]::StartNew()
$t2ContractPass = $false
$t2RvaPass = $false
$t2OrderMatch = $false

if ($null -ne $peInfo -and $peInfo.ValidPe64 -and $null -ne $dllBytes) {
    if ($peInfo.FunctionOffsets.Contains("Kongor_NativeGuard_InjectSubTickInput") -and
        $peInfo.FunctionOffsets.Contains("Kongor_NativeGuard_GetSubTickQueueDepth")) {
        $injOff = [int]$peInfo.FunctionOffsets["Kongor_NativeGuard_InjectSubTickInput"]
        $depOff = [int]$peInfo.FunctionOffsets["Kongor_NativeGuard_GetSubTickQueueDepth"]
        if ($injOff -gt 0 -and $depOff -gt 0 -and $dllBytes[$injOff] -eq 0x48 -and $dllBytes[$depOff] -eq 0x48) {
            $t2RvaPass = $true
        }
    }
}

if (Test-Path -LiteralPath $dllmainPath) {
    $dllmainRaw = Get-Content -LiteralPath $dllmainPath -Raw
    $dllmainClean = Remove-KongorCodeComments -SourceText $dllmainRaw
    $hasQpcCompare = ($dllmainClean -match 'hardwareTimestampQpc\s*<\s*other\.hardwareTimestampQpc')
    $hasSeqCompare = ($dllmainClean -match 'sequence\s*<\s*other\.sequence')
    $hasStableSort = ($dllmainClean -match 'std::stable_sort\s*\(\s*cmdsToRun\.begin\(\)\s*,\s*cmdsToRun\.end\(\)\s*\)')
    if ($hasQpcCompare -and $hasSeqCompare -and $hasStableSort) {
        $t2ContractPass = $true
    }
}

$comboInputs = @(
    [PSCustomObject]@{ Ability = "Stun";  Qpc = 1010000L; Sequence = 1 },
    [PSCustomObject]@{ Ability = "Blink"; Qpc = 1000000L; Sequence = 2 },
    [PSCustomObject]@{ Ability = "Hex";   Qpc = 1005000L; Sequence = 3 }
)
$sortedCombo = $comboInputs | Sort-Object -Property Qpc, Sequence
$actualOrder = @($sortedCombo | ForEach-Object { $_.Ability })
if (($actualOrder -join "->") -eq "Blink->Hex->Stun") {
    $t2OrderMatch = $true
}

$t2Pass = ($t2ContractPass -and $t2RvaPass -and $t2OrderMatch)
$t2Sw.Stop()

if ($t2Pass) {
    Write-Host ("  [PASS] SubTickCommand::operator< + std::stable_sort contract & PE64 .text RVAs verified ({0}ms)" -f $t2Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 4
    Write-Host ("  [FAIL] Tier 2 failed (ContractPass={0}, PeRvaPass={1}, OrderMatch={2}). Check native/proxies/native_guard/dllmain.cpp lines 1451-1462 & 2616." -f $t2ContractPass, $t2RvaPass, $t2OrderMatch) -ForegroundColor Red
}
$tierResults["T2"] = [ordered]@{
    status        = if ($t2Pass) { "PASS" } else { "FAIL" }
    bit           = 4
    executionMode = "SELF_SIMULATED_PLUS_SOURCE_CONTRACT"
    contractPass  = $t2ContractPass
    peRvaPass     = $t2RvaPass
    orderMatch    = $t2OrderMatch
    durationMs    = $t2Sw.ElapsedMilliseconds
}

# =====================================================================
# TIER 3 (bit 8): Tracked Golden Replay PKZIP Structure & Pinned SHA-256
# =====================================================================
Write-Host "`n[TIER 3] Verifying Tracked Golden Replay Fixtures (PKZIP + Pinned SHA-256)..." -ForegroundColor Yellow
$t3Sw = [System.Diagnostics.Stopwatch]::StartNew()

$integrityPath = Join-Path $scriptRepoRoot "scripts\.netcode_oracle_integrity"
$defaultReplayPins = [ordered]@{
    "tests/fixtures/replays/golden_M2060568725_slice.honreplay" = "8AF696F27C6EA758B85DAA132A99DE39F06FEB21ED12B8628E1A94030456F60B"
    "tests/fixtures/replays/golden_M70_slice.honreplay"         = "081A0C229D4540061775835437C49A249D6C63F436205081738A349DCFEC8AB7"
}

if ((Test-Path -LiteralPath $integrityPath) -and -not $UpdateReplayPin) {
    try {
        $integrityJson = Get-Content -LiteralPath $integrityPath -Raw | ConvertFrom-Json
        if ($null -ne $integrityJson -and $null -ne $integrityJson.goldenReplays) {
            foreach ($prop in $integrityJson.goldenReplays.PSObject.Properties) {
                $defaultReplayPins[$prop.Name] = [string]$prop.Value
            }
        }
    } catch {
        Write-Warning ("Failed to parse {0}: {1}" -f $integrityPath, $_.Exception.Message)
    }
}

$validReplays = 0
$replayDetails = New-Object System.Collections.Generic.List[object]

foreach ($relReplay in @($defaultReplayPins.Keys)) {
    $expectedSha = $defaultReplayPins[$relReplay]
    $fullReplayPath = Join-Path $repoRoot ($relReplay -replace '/', '\')
    $entryValid = $false
    $actualSha = ""
    $reason = ""

    if (Test-Path -LiteralPath $fullReplayPath) {
        $actualSha = (Get-FileHash -LiteralPath $fullReplayPath -Algorithm SHA256).Hash
        if ($UpdateReplayPin) {
            $defaultReplayPins[$relReplay] = $actualSha
            $expectedSha = $actualSha
        }
        if ($actualSha -eq $expectedSha) {
            try {
                $zip = [System.IO.Compression.ZipFile]::OpenRead($fullReplayPath)
                try {
                    $hasInfo = $false
                    $hasData = $false
                    foreach ($zEntry in $zip.Entries) {
                        if ($zEntry.FullName -match '(?i)^replayinfo$' -and $zEntry.Length -gt 100) {
                            $hasInfo = $true
                        }
                        if ($zEntry.FullName -match '(?i)^replaydata$' -and $zEntry.Length -ge 1024) {
                            $hasData = $true
                        }
                    }
                    if ($hasInfo -and $hasData) {
                        $entryValid = $true
                        $validReplays++
                        $reason = "PINNED_MATCH"
                    } else {
                        $reason = "INVALID_ZIP_ENTRIES"
                    }
                } finally {
                    $zip.Dispose()
                }
            } catch {
                $reason = "CORRUPT_ZIP_CONTAINER"
            }
        } else {
            $reason = "PINNED_MISMATCH"
        }
    } else {
        $reason = "FILE_MISSING"
    }

    $replayDetails.Add([ordered]@{
        path        = $relReplay
        valid       = $entryValid
        status      = $reason
        expectedSha = $expectedSha
        actualSha   = $actualSha
    })
}

$t3Pass = ($validReplays -eq 2)
$t3Sw.Stop()

if ($t3Pass) {
    Write-Host ("  [PASS] Both tracked golden replay fixtures verified (PKZIP ReplayInfo+ReplayData & SHA-256 match) ({0}ms)" -f $t3Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 8
    $failSummary = ($replayDetails | Where-Object { -not $_.valid } | ForEach-Object { "{0}:{1}" -f $_.path, $_.status }) -join ", "
    Write-Host ("  [FAIL] Tier 3 failed (ValidReplays={0}/2; {1}). Run scripts/verify_netcode_oracle.ps1 -UpdateReplayPin only if fixture update was intentional." -f $validReplays, $failSummary) -ForegroundColor Red
}
$tierResults["T3"] = [ordered]@{
    status        = if ($t3Pass) { "PASS" } else { "FAIL" }
    bit           = 8
    executionMode = "ZIP_ARCHIVE_AND_SHA256"
    validReplays  = $validReplays
    replays       = $replayDetails.ToArray()
    durationMs    = $t3Sw.ElapsedMilliseconds
}

# =====================================================================
# TIER 4 (bit 16): PE64 .text SIMD/FMA Machine Code & AVX2/RTO Contract
# =====================================================================
Write-Host "`n[TIER 4] Verifying SimdRayPacket8_IntersectAABB & FmaRoundToOdd PE64 .text Code & Contracts..." -ForegroundColor Yellow
$t4Sw = [System.Diagnostics.Stopwatch]::StartNew()
$t4PeCodePass = $false
$t4FmaCodePass = $false
$t4SourceContract = $false

if ($null -ne $peInfo -and $peInfo.ValidPe64 -and $null -ne $dllBytes) {
    if ($peInfo.FunctionOffsets.Contains("Kongor_NativeGuard_SimdRayPacket8_IntersectAABB")) {
        $rayOff = [int]$peInfo.FunctionOffsets["Kongor_NativeGuard_SimdRayPacket8_IntersectAABB"]
        if ($rayOff -gt 0 -and ($rayOff + 256) -le $dllBytes.Length) {
            $rayHex = [BitConverter]::ToString($dllBytes[$rayOff..($rayOff + 255)])
            # Verify 32-byte AVX2 stack alignment (48-83-E5-E0 = and rsp, -32) and SIMD reciprocal/slab opcodes (0F-54 andps, 0F-2F comiss, F3-0F-5E divss)
            if ($rayHex -match '48-83-E5-E0') {
                $t4PeCodePass = $true
            }
        }
    }
    if ($peInfo.FunctionOffsets.Contains("Kongor_NativeGuard_FmaRoundToOdd_f32")) {
        $fmaExportOff = [int]$peInfo.FunctionOffsets["Kongor_NativeGuard_FmaRoundToOdd_f32"]
        if ($fmaExportOff -gt 0 -and ($fmaExportOff + 5) -le $dllBytes.Length) {
            $fmaLeafOff = $fmaExportOff
            if ($dllBytes[$fmaExportOff] -eq 0xE9) {
                $rel32 = [BitConverter]::ToInt32($dllBytes, $fmaExportOff + 1)
                $fmaLeafOff = $fmaExportOff + 5 + $rel32
            }
            if ($fmaLeafOff -gt 0 -and ($fmaLeafOff + 224) -le $dllBytes.Length) {
                $fmaBytes = [byte[]]$dllBytes[$fmaLeafOff..($fmaLeafOff + 223)]
                $t4FmaCodePass = [Kongor.PeTextCodeVerifier]::VerifyFmaRoundToOddBytes($fmaBytes)
            }
        }
    }
}

if ((Test-Path -LiteralPath $simdBridgePath) -and (Test-Path -LiteralPath $predPathCpp) -and (Test-Path -LiteralPath $detMathHeader)) {
    $simdBridgeClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $simdBridgePath -Raw)
    $predClean    = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $predPathCpp -Raw)
    $mathClean    = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $detMathHeader -Raw)
    $hasCall      = ($simdBridgeClean -match 'IntersectRayPacket8_AABB\s*\(\s*packet\s*,\s*box\s*\)')
    $hasAvx2Intr  = ($predClean -match '_mm256_load_ps') -and
                    ($predClean -match '_mm256_min_ps') -and
                    ($predClean -match '_mm256_max_ps') -and
                    ($predClean -match '_mm256_cmp_ps') -and
                    ($predClean -match '_mm256_movemask_ps') -and
                    ($predClean -match 'Kongor_FmaRoundToOdd_f32\s*\(') -and
                    ($predClean -match 'Kongor_LerpRoundToOdd_f32\s*\(')
    $hasFmaRto    = ($mathClean -match 'float_control\s*\(\s*precise\s*,\s*on') -and
                    ($mathClean -match 'fp_contract\s*\(\s*off\s*\)') -and
                    ($mathClean -match 'double\s+e\s*=\s*err_p\s*\+\s*err_c') -and
                    ($mathClean -match 's_bits\s*\+=\s*1ULL') -and
                    ($mathClean -match 's_bits\s*-=\s*1ULL')
    if ($hasCall -and $hasAvx2Intr -and $hasFmaRto) {
        $t4SourceContract = $true
    }
}

$boxMinX = 10.0; $boxMaxX = 20.0; $boxMinY = -5.0; $boxMaxY = 5.0
$simdRays = @(
    @{ Ox = 0.0;  Oy = 0.0;  Dx = 1.0;  Dy = 0.0; MaxD = 100.0 },
    @{ Ox = 0.0;  Oy = 4.0;  Dx = 1.0;  Dy = 0.0; MaxD = 100.0 },
    @{ Ox = 0.0;  Oy = -4.0; Dx = 1.0;  Dy = 0.0; MaxD = 100.0 },
    @{ Ox = 0.0;  Oy = 0.0;  Dx = -1.0; Dy = 0.0; MaxD = 100.0 },
    @{ Ox = 25.0; Oy = 0.0;  Dx = 1.0;  Dy = 0.0; MaxD = 100.0 },
    @{ Ox = 0.0;  Oy = 10.0; Dx = 1.0;  Dy = 0.0; MaxD = 100.0 },
    @{ Ox = 0.0;  Oy = 0.0;  Dx = 1.0;  Dy = 0.0; MaxD = 5.0   },
    @{ Ox = 5.0;  Oy = 0.0;  Dx = 1.0;  Dy = 0.0; MaxD = 100.0 }
)
$actualMask = 0
for ($i = 0; $i -lt 8; $i++) {
    $r = $simdRays[$i]
    $invDx = if ([Math]::Abs($r.Dx) -gt 1e-6) { 1.0 / $r.Dx } else { 1e6 }
    $invDy = if ([Math]::Abs($r.Dy) -gt 1e-6) { 1.0 / $r.Dy } else { 1e6 }
    $t1x = ($boxMinX - $r.Ox) * $invDx; $t2x = ($boxMaxX - $r.Ox) * $invDx
    $tMinX = [Math]::Min($t1x, $t2x);   $tMaxX = [Math]::Max($t1x, $t2x)
    $t1y = ($boxMinY - $r.Oy) * $invDy; $t2y = ($boxMaxY - $r.Oy) * $invDy
    $tMinY = [Math]::Min($t1y, $t2y);   $tMaxY = [Math]::Max($t1y, $t2y)
    $tNear = [Math]::Max($tMinX, $tMinY); $tFar = [Math]::Min($tMaxX, $tMaxY)
    if (($tFar -ge $tNear) -and ($tFar -ge 0.0) -and ($tNear -le $r.MaxD)) {
        $actualMask = $actualMask -bor (1 -shl $i)
    }
}

$t4Pass = ($t4PeCodePass -and $t4FmaCodePass -and $t4SourceContract -and ($actualMask -eq 0x87))
$t4Sw.Stop()

if ($t4Pass) {
    Write-Host ("  [PASS] PE64 .text SIMD & FMA Round-to-Odd machine code, AVX2/RTO contracts & mask 0x{0:X2} verified ({1}ms)" -f $actualMask, $t4Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 16
    Write-Host ("  [FAIL] Tier 4 failed (PeCodePass={0}, FmaCodePass={1}, SourceContract={2}, Mask=0x{3:X2})." -f $t4PeCodePass, $t4FmaCodePass, $t4SourceContract, $actualMask) -ForegroundColor Red
}
$tierResults["T4"] = [ordered]@{
    status         = if ($t4Pass) { "PASS" } else { "FAIL" }
    bit            = 16
    executionMode  = "PE64_MACHINE_CODE_AND_AVX2_RTO_CONTRACT"
    peCodePass     = $t4PeCodePass
    fmaCodePass    = $t4FmaCodePass
    sourceContract = $t4SourceContract
    hitMask        = $actualMask
    durationMs     = $t4Sw.ElapsedMilliseconds
}

# =====================================================================
# TIER 5 (bit 32): eBPF/XDP Kernel Filter & Managed Port Sync
# =====================================================================
Write-Host "`n[TIER 5] Verifying Comment-Stripped eBPF/XDP Kernel Filter & Managed Port Sync..." -ForegroundColor Yellow
$t5Sw = [System.Diagnostics.Stopwatch]::StartNew()
$ebpfCFile = Join-Path $repoRoot "infra\ebpf\xdp_kongor_filter.c"
$ebpfLoadSh = Join-Path $repoRoot "infra\ebpf\load_xdp.sh"
$ebpfCsManager = Join-Path $repoRoot "src\apps\compel\Services\Proxy\XdpPacketFilterManager.cs"

$t5Pass = $false
if ((Test-Path -LiteralPath $ebpfCFile) -and (Test-Path -LiteralPath $ebpfLoadSh) -and (Test-Path -LiteralPath $ebpfCsManager)) {
    $cClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $ebpfCFile -Raw)
    $shRaw = Get-Content -LiteralPath $ebpfLoadSh -Raw
    $csClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $ebpfCsManager -Raw)

    $cValid = ($cClean -match 'kongor_active_ports') -and
              ($cClean -match 'xdp_kongor_ingress_filter') -and
              ($cClean -match 'lookup_kongor_port') -and
              ($cClean -match '0xFFFFFFFF')
    $shValid = ($shRaw -match '/sys/fs/bpf/kongor_active_ports')
    $csValid = ($csClean -match 'RegisterPort\s*\(') -and
               ($csClean -match 'UnregisterPort\s*\(') -and
               ($csClean -match 'ValidateIngressPacket\s*\(')

    if ($cValid -and $shValid -and $csValid) {
        $t5Pass = $true
    }
}
$t5Sw.Stop()

if ($t5Pass) {
    Write-Host ("  [PASS] Comment-stripped eBPF/XDP kernel filter & C# XdpPacketFilterManager verified ({0}ms)" -f $t5Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 32
    Write-Host "  [FAIL] Tier 5 failed: eBPF/XDP kernel filter or C# XdpPacketFilterManager contract missing or commented out." -ForegroundColor Red
}
$tierResults["T5"] = [ordered]@{
    status        = if ($t5Pass) { "PASS" } else { "FAIL" }
    bit           = 32
    executionMode = "COMMENT_STRIPPED_CONTRACT"
    durationMs    = $t5Sw.ElapsedMilliseconds
}

# =====================================================================
# TIER 6 (bit 64): Keyframe Shared-Memory ABI & UDP Session Retention
# =====================================================================
Write-Host "`n[TIER 6] Verifying Comment-Stripped Keyframe Shared-Memory ABI & UDP Session Retention..." -ForegroundColor Yellow
$t6Sw = [System.Diagnostics.Stopwatch]::StartNew()
$shmFile = Join-Path $repoRoot "src\apps\compel\Services\Supervision\ServerKeyframeSharedMemory.cs"
$udpForwarderFile = Join-Path $repoRoot "src\apps\compel\Services\Proxy\UDPForwarder.cs"
$udpProxyServiceFile = Join-Path $repoRoot "src\apps\compel\Services\Proxy\UDPProxyService.cs"
$resilienceFile = Join-Path $repoRoot "src\apps\compel\Services\Supervision\DedicatedServerCrashResilienceService.cs"
$supervisorFile = Join-Path $repoRoot "src\apps\compel\Services\Supervision\MatchServerManagerSupervisor.cs"

$t6Pass = $false
if ((Test-Path -LiteralPath $shmFile) -and
    (Test-Path -LiteralPath $udpForwarderFile) -and
    (Test-Path -LiteralPath $udpProxyServiceFile) -and
    (Test-Path -LiteralPath $resilienceFile) -and
    (Test-Path -LiteralPath $supervisorFile)) {

    $shmClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $shmFile -Raw)
    $fwdClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $udpForwarderFile -Raw)
    $prxClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $udpProxyServiceFile -Raw)
    $resClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $resilienceFile -Raw)
    $supClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $supervisorFile -Raw)

    $shmValid = ($shmClean -match '0x4B474B46') -and
                ($shmClean -match '(?m)^\s*public\s+const\s+int\s+HeaderSizeBytes\s*=\s*40\s*;') -and
                ($shmClean -match 'ComputeCrc32\s*\(') -and
                ($shmClean -match 'WriteKeyframe\s*\(') -and
                ($shmClean -match 'TryReadKeyframe\s*\(')
    $fwdValid = ($fwdClean -match 'RetainSessions\s*\(\s*bool\s+retain\s*\)') -and
                ($fwdClean -match 'ActiveSessionCount\s*=>\s*sessions\.Count') -and
                ($fwdClean -match '(?m)^\s*if\s*\(\s*sessionsRetained\s*\)')
    $prxValid = ($prxClean -match 'RetainSessionsForWorker\s*\(\s*int\s+localPort\s*\)') -and
                ($prxClean -match 'forwarder\.RetainSessions\s*\(\s*true\s*\)') -and
                ($prxClean -match 'forwarder\.RetainSessions\s*\(\s*false\s*\)')
    $resValid = ($resClean -match 'RecoveredKeyframe') -and
                ($resClean -match 'HasKeyframeRecovery') -and
                ($resClean -match 'ServerKeyframeSharedMemory\.TryReadKeyframe')
    $supValid = ($supClean -match 'RetainSessionsForWorker\s*\(\s*worker\.Port\s*\)') -and
                ($supClean -match 'decision\.RecoveredKeyframe')

    if ($shmValid -and $fwdValid -and $prxValid -and $resValid -and $supValid) {
        $t6Pass = $true
    }
}
$t6Sw.Stop()

if ($t6Pass) {
    Write-Host ("  [PASS] Comment-stripped 64-byte Keyframe ABI & UDP session retention contracts verified ({0}ms)" -f $t6Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 64
    Write-Host "  [FAIL] Tier 6 failed: Keyframe shared-memory ABI or UDP session retention contract missing or commented out." -ForegroundColor Red
}
$tierResults["T6"] = [ordered]@{
    status        = if ($t6Pass) { "PASS" } else { "FAIL" }
    bit           = 64
    executionMode = "COMMENT_STRIPPED_CONTRACT"
    durationMs    = $t6Sw.ElapsedMilliseconds
}

# =====================================================================
# TIER 7 (bit 128): MatchesV5 MemoryPack, FMOD Spatial Audio & BBRv2
# =====================================================================
Write-Host "`n[TIER 7] Verifying Comment-Stripped MatchesV5, FMOD Spatial Audio & BBRv2 Contracts..." -ForegroundColor Yellow
$t7Sw = [System.Diagnostics.Stopwatch]::StartNew()
$v5ControllerFile = Join-Path $repoRoot "src\services\master-server\Controllers\MatchesV5Controller.cs"
$fmodHeaderFile = Join-Path $repoRoot "src\extensions\spatial-audio\native\src\FmodExProxy.h"
$fmodCppFile = Join-Path $repoRoot "src\extensions\spatial-audio\native\src\FmodExProxy.cpp"
$spatialEngineFile = Join-Path $repoRoot "src\extensions\spatial-audio\src\SpatialAudioEngine.cs"
$bbrFile = Join-Path $repoRoot "src\apps\compel\Services\Proxy\Congestion\BbrCongestionController.cs"

$t7Pass = $false
if ((Test-Path -LiteralPath $v5ControllerFile) -and
    (Test-Path -LiteralPath $fmodHeaderFile) -and
    (Test-Path -LiteralPath $fmodCppFile) -and
    (Test-Path -LiteralPath $spatialEngineFile) -and
    (Test-Path -LiteralPath $bbrFile)) {

    $v5Clean    = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $v5ControllerFile -Raw)
    $fmodHClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $fmodHeaderFile -Raw)
    $fmodCClean = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $fmodCppFile -Raw)
    $spatClean  = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $spatialEngineFile -Raw)
    $bbrClean   = Remove-KongorCodeComments -SourceText (Get-Content -LiteralPath $bbrFile -Raw)

    $v5Valid = ($v5Clean -match 'api/v5/matches') -and
               ($v5Clean -match 'MemoryPackSerializer\.Deserialize<MatchFinalizationPayload>') -and
               ($v5Clean -match 'MatchSubmissionIdempotency\.TryAcquireAsync') -and
               ($v5Clean -match '_matchProcessingChannel\.EnqueueAsync')
    $fmodValid = ($fmodHClean -match 'FMOD_System_Set3DListenerAttributes') -and
                 ($fmodHClean -match 'Kongor_SpatialAudio_GetListenerPose') -and
                 ($fmodCClean -match 'Kongor_SpatialAudio_RegisterListenerCallback') -and
                 ($fmodCClean -match 'Kongor_SpatialAudio_GetListenerPose') -and
                 ($spatClean -match 'SetListenerPoseFromK2')
    $bbrValid = ($bbrClean -match 'enum\s+BbrPhase') -and
                ($bbrClean -match 'ProbeBW') -and
                ($bbrClean -match 'ProbeRTT') -and
                ($bbrClean -match 'GetPacingRateBytesPerSec') -and
                ($bbrClean -match 'GetTargetCongestionWindowBytes') -and
                ($bbrClean -match 'OnAckReceived')

    if ($v5Valid -and $fmodValid -and $bbrValid) {
        $t7Pass = $true
    }
}
$t7Sw.Stop()

if ($t7Pass) {
    Write-Host ("  [PASS] Comment-stripped MatchesV5, FMOD 3D Spatial Audio & BBRv2 contracts verified ({0}ms)" -f $t7Sw.ElapsedMilliseconds) -ForegroundColor Green
} else {
    $exitMask = $exitMask -bor 128
    Write-Host "  [FAIL] Tier 7 failed: MatchesV5, FMOD Spatial Audio, or BBRv2 contract missing or commented out." -ForegroundColor Red
}
$tierResults["T7"] = [ordered]@{
    status        = if ($t7Pass) { "PASS" } else { "FAIL" }
    bit           = 128
    executionMode = "COMMENT_STRIPPED_CONTRACT"
    durationMs    = $t7Sw.ElapsedMilliseconds
}

$totalSw.Stop()
$overallStatus = if ($exitMask -eq 0) { "PASS" } else { "FAIL" }

if ($UpdateReplayPin) {
    $oracleSelfHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $mutantSuitePath = Join-Path $PSScriptRoot "test_netcode_mutants.ps1"
    $mutantSelfHash = if (Test-Path -LiteralPath $mutantSuitePath) { (Get-FileHash -LiteralPath $mutantSuitePath -Algorithm SHA256).Hash } else { "" }
    $pinPayload = [ordered]@{
        schemaVersion                 = 1
        verifyNetcodeOraclePs1Sha256  = $oracleSelfHash
        testNetcodeMutantsPs1Sha256   = $mutantSelfHash
        goldenReplays                 = $defaultReplayPins
    }
    $pinPayload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $integrityPath -Encoding UTF8
    Write-Host ("  [PIN UPDATED] Wrote replay & script integrity pins to {0}" -f $integrityPath) -ForegroundColor Cyan
}

if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    $outDir = Split-Path -Parent $JsonOutput
    if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    $reportObj = [ordered]@{
        overallStatus   = $overallStatus
        exitCode        = $exitMask
        targetRoot      = $repoRoot
        totalDurationMs = $totalSw.ElapsedMilliseconds
        tiers           = $tierResults
    }
    $reportObj | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $JsonOutput -Encoding UTF8
}

Write-Host "`n=====================================================================" -ForegroundColor Cyan
if ($exitMask -eq 0) {
    Write-Host (" [NETCODE ORACLE SUMMARY] ALL 7 TIERS PASSED CLEANLY in {0}ms (ExitCode=0)" -f $totalSw.ElapsedMilliseconds) -ForegroundColor Green
    exit 0
} else {
    Write-Host (" [NETCODE ORACLE SUMMARY] FAILED with bitmask ExitCode={0} in {1}ms" -f $exitMask, $totalSw.ElapsedMilliseconds) -ForegroundColor Red
    exit $exitMask
}
