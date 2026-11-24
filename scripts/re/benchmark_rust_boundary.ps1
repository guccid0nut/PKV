# SPDX-License-Identifier: MPL-2.0
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

<#
.SYNOPSIS
    Wave 4 Track 3 (w4-rust-boundary) CMake + Cargo Cold/Incremental Benchmark & FFI Verification.

.DESCRIPTION
    Empirically validates the Rust untrusted-boundary parser staticlib integration:
      1. Runs offline `cargo test` across `kongor_parsers`, `kongor_parsers_ffi`, and `kongor_xdp_aya`.
      2. Performs a clean cold CMake + Cargo Release build in isolated `native/build_rust_spike/`
         (leaves `native/build/` untouched).
      3. Runs `ctest -R ^test_rust_parsers_ffi$` to verify real MSVC C++17 <-> Rust staticlib FFI.
      4. Performs a touch-one-file incremental rebuild of `native/rust/kongor_parsers/src/udp.rs`
         (restoring exact original bytes in `finally`) and evaluates the `< 15.0s` Go/No-Go gate.
      5. Emits detailed telemetry logs and writes JSON results to
         `native/build_rust_spike/benchmark_rust_boundary_result.json`.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-KongorTestLock.ps1 `
        -Owner w4-rust-boundary-worker -- `
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts/re/benchmark_rust_boundary.ps1
#>

[CmdletBinding()]
param(
    [string]$BuildDir = "native/build_rust_spike",
    [string]$Config = "Release",
    [double]$IncrementalGateSeconds = 15.0,
    [switch]$StandaloneTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$absBuildDir = if ([System.IO.Path]::IsPathRooted($BuildDir)) { $BuildDir } else { Join-Path $repoRoot $BuildDir }
$nativeDir = Join-Path $repoRoot "native"
$testsDir = Join-Path $repoRoot "native\tests"
$cargoManifest = Join-Path $repoRoot "native\rust\Cargo.toml"
$touchTargetFile = Join-Path $repoRoot "native\rust\kongor_parsers\src\udp.rs"
$cachedRaylibSrc = Join-Path $repoRoot "native\build\_deps\raylib-src"

function Write-BenchLog {
    param([string]$Level, [string]$Message)
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$ts] [RUST-BENCH][$Level] $Message"
}

function Invoke-TimedProcess {
    param(
        [string]$Label,
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )
    Write-BenchLog "INFO" "Starting '$Label': $FilePath $($Arguments -join ' ')"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location $WorkingDirectory
    try {
        $prevPref = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $outputLines = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = $prevPref
    } finally {
        Pop-Location
    }
    $sw.Stop()
    $text = ($outputLines | ForEach-Object { $_.ToString() }) -join "`n"
    Write-Host $text
    Write-BenchLog "INFO" "Finished '$Label' in $([Math]::Round($sw.Elapsed.TotalSeconds, 3))s (exitCode=$exitCode)"
    if ($exitCode -ne 0) {
        throw "Step '$Label' failed with exit code $exitCode. Diagnose by running: $FilePath $($Arguments -join ' ')"
    }
    return [PSCustomObject]@{
        Label          = $Label
        ElapsedSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
        ElapsedMs      = [int64]$sw.ElapsedMilliseconds
        ExitCode       = $exitCode
        Output         = $text
    }
}

Write-BenchLog "INFO" "=================================================================="
Write-BenchLog "INFO" "  Wave 4 Track 3 (w4-rust-boundary) CMake + Cargo Benchmark"
Write-BenchLog "INFO" "=================================================================="
Write-BenchLog "INFO" "RepoRoot=$repoRoot | BuildDir=$absBuildDir | Config=$Config | Gate=<${IncrementalGateSeconds}s"

# 0. Toolchain preflight
foreach ($tool in @("rustc", "cargo", "cmake", "ctest")) {
    $cmd = Get-Command $tool -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw "Required tool '$tool' was not found on PATH. Install '$tool' and retry."
    }
    $ver = (& $tool --version 2>&1 | Select-Object -First 1).ToString().Trim()
    Write-BenchLog "INFO" "Toolchain $tool -> $ver ($($cmd.Source))"
}

# 1. Run offline Rust workspace tests
$cargoTestRes = Invoke-TimedProcess `
    -Label "cargo_test_workspace_offline" `
    -FilePath "cargo" `
    -Arguments @("test", "--offline", "--manifest-path", $cargoManifest) `
    -WorkingDirectory $repoRoot

$cargoPassedTotal = 0
foreach ($m in [regex]::Matches($cargoTestRes.Output, 'test result: ok\. (\d+) passed; 0 failed;')) {
    $cargoPassedTotal += [int]$m.Groups[1].Value
}
Write-BenchLog "INFO" "Parsed offline Rust workspace test count: $cargoPassedTotal passed (0 failed)"

# 2. Clean dedicated spike build directory (never touch native/build)
if ($absBuildDir -eq (Join-Path $repoRoot "native\build")) {
    throw "Refusing to wipe primary 'native/build'. Use an isolated build directory such as 'native/build_rust_spike'."
}
if (Test-Path -LiteralPath $absBuildDir) {
    Write-BenchLog "INFO" "Removing prior spike directory '$absBuildDir' for true cold build measurement..."
    Remove-Item -LiteralPath $absBuildDir -Recurse -Force
}

# 3. Cold CMake Configure
$cmakeSourceDir = if ($StandaloneTests) { $testsDir } else { $nativeDir }
$cmakeConfigureArgs = @(
    "-S", $cmakeSourceDir,
    "-B", $absBuildDir,
    "-DKONGOR_ENABLE_RUST_PARSERS=ON"
)
if (-not $StandaloneTests -and (Test-Path -LiteralPath $cachedRaylibSrc)) {
    Write-BenchLog "INFO" "Using local cached raylib source '$cachedRaylibSrc' so top-level native/ configure stays 100% offline."
    $cmakeConfigureArgs += "-DFETCHCONTENT_SOURCE_DIR_RAYLIB=$cachedRaylibSrc"
}

$coldConfigureRes = Invoke-TimedProcess `
    -Label "cmake_configure_cold" `
    -FilePath "cmake" `
    -Arguments $cmakeConfigureArgs `
    -WorkingDirectory $repoRoot

# 4. Cold CMake + Cargo Build (test_rust_parsers_ffi + kongor_parsers_ffi.lib)
$coldBuildRes = Invoke-TimedProcess `
    -Label "cmake_build_cold" `
    -FilePath "cmake" `
    -Arguments @("--build", $absBuildDir, "--config", $Config, "--target", "test_rust_parsers_ffi") `
    -WorkingDirectory $repoRoot

# 5. Run C++17 <-> Rust staticlib FFI test via CTest
$ctestColdRes = Invoke-TimedProcess `
    -Label "ctest_rust_parsers_ffi_cold" `
    -FilePath "ctest" `
    -Arguments @("--test-dir", $absBuildDir, "-C", $Config, "-R", "^test_rust_parsers_ffi$", "--output-on-failure", "-V") `
    -WorkingDirectory $repoRoot

# 6. No-op warm build check (measures pure CMake + Cargo fingerprint overhead)
$noopBuildRes = Invoke-TimedProcess `
    -Label "cmake_build_noop_warm" `
    -FilePath "cmake" `
    -Arguments @("--build", $absBuildDir, "--config", $Config, "--target", "test_rust_parsers_ffi") `
    -WorkingDirectory $repoRoot

# 7. Touch-one-file incremental Rust + CMake rebuild (restores exact bytes in finally)
if (-not (Test-Path -LiteralPath $touchTargetFile)) {
    throw "Touch target Rust source not found: '$touchTargetFile'"
}

$originalBytes = [System.IO.File]::ReadAllBytes($touchTargetFile)
$originalHash = (Get-FileHash -LiteralPath $touchTargetFile -Algorithm SHA256).Hash
$exeRelPath = if ($StandaloneTests) { "$Config\test_rust_parsers_ffi.exe" } else { "native_tests_build\$Config\test_rust_parsers_ffi.exe" }
$exePath = Join-Path $absBuildDir $exeRelPath
$libPath = Join-Path $absBuildDir "rust_ffi\$Config\kongor_parsers_ffi.lib"
$exeBeforeTicks = (Get-Item -LiteralPath $exePath).LastWriteTimeUtc.Ticks
$libBeforeTicks = (Get-Item -LiteralPath $libPath).LastWriteTimeUtc.Ticks
$incrementalBuildRes = $null
$recompiledRust = $false
$updatedStaticLib = $false
$relinkedCpp = $false

try {
    # Modify a compiled behavior-neutral constant inside `kongor_parsers/src/udp.rs`
    # (error display text) so rustc emits genuinely new object bytes, `copy_if_different`
    # updates `kongor_parsers_ffi.lib`, and MSVC link.exe actually relinks `test_rust_parsers_ffi.exe`.
    $originalText = [System.Text.Encoding]::UTF8.GetString($originalBytes)
    $needle = 'write!(f, "UDP payload too short: have {actual} bytes, require {required}")'
    if (-not $originalText.Contains($needle)) {
        throw "Expected benchmark touch anchor not found in '$touchTargetFile'."
    }
    $stamp = [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff")
    $replacement = "write!(f, `"UDP payload too short [bench-$stamp]: have {actual} bytes, require {required}`")"
    $modifiedText = $originalText.Replace($needle, $replacement)
    [System.IO.File]::WriteAllText($touchTargetFile, $modifiedText, [System.Text.UTF8Encoding]::new($false))
    Write-BenchLog "INFO" "Modified compiled code in '$touchTargetFile' (Display format string) to force full Rust recompile + .lib rewrite + MSVC relink."

    $incrementalBuildRes = Invoke-TimedProcess `
        -Label "cmake_build_incremental_touch_one_file" `
        -FilePath "cmake" `
        -Arguments @("--build", $absBuildDir, "--config", $Config, "--target", "test_rust_parsers_ffi") `
        -WorkingDirectory $repoRoot

    $exeAfterTicks = (Get-Item -LiteralPath $exePath).LastWriteTimeUtc.Ticks
    $libAfterTicks = (Get-Item -LiteralPath $libPath).LastWriteTimeUtc.Ticks
    $recompiledRust = ($incrementalBuildRes.Output -match 'Compiling kongor_parsers') -and
                      ($incrementalBuildRes.Output -match 'Compiling kongor_parsers_ffi')
    $updatedStaticLib = ($libAfterTicks -gt $libBeforeTicks)
    $relinkedCpp = ($exeAfterTicks -gt $exeBeforeTicks)
    Write-BenchLog "INFO" "Incremental evidence: recompiledRust=$recompiledRust, updatedStaticLib=$updatedStaticLib, relinkedCpp=$relinkedCpp"
} finally {
    [System.IO.File]::WriteAllBytes($touchTargetFile, $originalBytes)
    $restoredHash = (Get-FileHash -LiteralPath $touchTargetFile -Algorithm SHA256).Hash
    if ($restoredHash -ne $originalHash) {
        throw "FATAL: Failed to restore exact bytes of '$touchTargetFile' (expected=$originalHash, got=$restoredHash)"
    }
    Write-BenchLog "INFO" "Restored exact original bytes of '$touchTargetFile' (SHA256=$restoredHash)."
}

# 8. Rebuild after restore and re-run CTest to confirm final tree matches compiled binary
$postRestoreBuildRes = Invoke-TimedProcess `
    -Label "cmake_build_post_restore" `
    -FilePath "cmake" `
    -Arguments @("--build", $absBuildDir, "--config", $Config, "--target", "test_rust_parsers_ffi") `
    -WorkingDirectory $repoRoot

$ctestFinalRes = Invoke-TimedProcess `
    -Label "ctest_rust_parsers_ffi_final" `
    -FilePath "ctest" `
    -Arguments @("--test-dir", $absBuildDir, "-C", $Config, "-R", "^test_rust_parsers_ffi$", "--output-on-failure", "-V") `
    -WorkingDirectory $repoRoot

$coldTotalSeconds = [Math]::Round($coldConfigureRes.ElapsedSeconds + $coldBuildRes.ElapsedSeconds, 3)
$incrementalSeconds = $incrementalBuildRes.ElapsedSeconds
$gatePassed = ($incrementalSeconds -lt $IncrementalGateSeconds) -and $recompiledRust -and $updatedStaticLib -and $relinkedCpp
$verdict = if ($gatePassed) { "GO" } else { "NO-GO" }

$report = [ordered]@{
    timestamp_utc              = [DateTime]::UtcNow.ToString("O")
    cmake_source_dir           = $cmakeSourceDir
    build_dir                  = $absBuildDir
    config                     = $Config
    cargo_test_passed          = $cargoPassedTotal
    cargo_test_seconds         = $cargoTestRes.ElapsedSeconds
    cold_cmake_configure_sec   = $coldConfigureRes.ElapsedSeconds
    cold_cmake_cargo_build_sec = $coldBuildRes.ElapsedSeconds
    cold_total_sec             = $coldTotalSeconds
    noop_warm_build_sec        = $noopBuildRes.ElapsedSeconds
    incremental_touch_sec      = $incrementalSeconds
    post_restore_build_sec     = $postRestoreBuildRes.ElapsedSeconds
    ctest_cold_sec             = $ctestColdRes.ElapsedSeconds
    ctest_final_sec            = $ctestFinalRes.ElapsedSeconds
    recompiled_rust_crates     = $recompiledRust
    updated_static_lib         = $updatedStaticLib
    relinked_cpp_executable    = $relinkedCpp
    incremental_gate_sec       = $IncrementalGateSeconds
    verdict                    = $verdict
}

$reportPath = Join-Path $absBuildDir "benchmark_rust_boundary_result.json"
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-BenchLog "INFO" "=================================================================="
Write-BenchLog "INFO" "  BENCHMARK SUMMARY (w4-rust-boundary)"
Write-BenchLog "INFO" "=================================================================="
Write-BenchLog "INFO" "Offline Cargo Tests Passed : $cargoPassedTotal ($($cargoTestRes.ElapsedSeconds)s)"
Write-BenchLog "INFO" "Cold CMake Configure       : $($coldConfigureRes.ElapsedSeconds)s"
Write-BenchLog "INFO" "Cold CMake + Cargo Build   : $($coldBuildRes.ElapsedSeconds)s (Total Cold: ${coldTotalSeconds}s)"
Write-BenchLog "INFO" "Warm No-Op Build           : $($noopBuildRes.ElapsedSeconds)s"
Write-BenchLog "INFO" "Incremental 1-File Rebuild : ${incrementalSeconds}s (Rust=$recompiledRust, StaticLib=$updatedStaticLib, CppRelink=$relinkedCpp)"
Write-BenchLog "INFO" "Post-Restore Rebuild       : $($postRestoreBuildRes.ElapsedSeconds)s"
Write-BenchLog "INFO" "Incremental Gate (<${IncrementalGateSeconds}s)   : $verdict"
Write-BenchLog "INFO" "JSON Report Written        : $reportPath"
Write-BenchLog "INFO" "=================================================================="

if (-not $gatePassed) {
    throw "w4-rust-boundary benchmark FAILED Go/No-Go gate (incremental=${incrementalSeconds}s, gate=<${IncrementalGateSeconds}s, Rust=$recompiledRust, StaticLib=$updatedStaticLib, CppRelink=$relinkedCpp)."
}
