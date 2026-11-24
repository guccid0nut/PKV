# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Runs Linux dedicated server LD_PRELOAD and Queue Arcade C++ smoke gate in Docker.
.DESCRIPTION
    Validates:
      1. libkongor_native_guard.so compiles cleanly with -Werror and 0 libstdc++ symbols.
      2. 67-check linux_queue_arcade_tests suite passes against real server ELF binaries.
      3. games/server/linux-x64/hon-x86_64-server.bin boots headless under LD_PRELOAD and exits 0 on Quit.
.OUTPUTS
    Writes result status to logs/last_linux_smoke.json.
#>
[CmdletBinding()]
param(
    [string]$Image = "ubuntu:22.04"
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $PSScriptRoot).Parent.Parent.FullName
$logsDir = Join-Path $repoRoot "logs"
if (-not (Test-Path -LiteralPath $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}
$lastSmokeJson = Join-Path $logsDir "last_linux_smoke.json"

Write-Host "=== Project Kongor: Linux Server Docker LD_PRELOAD Smoke Gate ===" -ForegroundColor Cyan

# Step 1: Detect Docker availability
$dockerAvailable = $false
try {
    $dockerVersion = (& docker info --format '{{.ServerVersion}}' 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($dockerVersion)) {
        $dockerAvailable = $true
    }
} catch {
    $dockerAvailable = $false
}

if (-not $dockerAvailable) {
    $skipReport = [PSCustomObject]@{
        status = "SKIPPED"
        reason = "Docker daemon offline or unavailable"
        timestamp = (Get-Date).ToString("o")
    }
    $skipReport | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $lastSmokeJson -Encoding utf8
    Write-Host "[LINUX DOCKER SMOKE] NOTICE: Docker daemon offline or unavailable; skipping Linux smoke gate." -ForegroundColor Yellow
    exit 0
}

Write-Host "[LINUX DOCKER SMOKE] Docker daemon active (version $($dockerVersion.Trim())). Running container verification..." -ForegroundColor Green
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$dockerScript = @'
set -eo pipefail
export DEBIAN_FRONTEND=noninteractive
echo "[1/4] Installing Linux build toolchain & dependencies..."
apt-get update -qq 2>&1 > /dev/null && apt-get install -y -qq build-essential libfontconfig1 iproute2 > /dev/null 2>&1

echo "[2/4] Compiling libkongor_native_guard.so (libstdc++-free)..."
gcc -x c++ -O2 -fPIC -shared -Werror -fno-exceptions -fno-rtti native/proxies/native_guard/linux_native_guard.cpp -o /tmp/libkongor_native_guard.so -ldl -lpthread -lc

echo "[2b/4] Asserting standard library ABI compliance (0 libstdc++ dependencies)..."
if readelf -d /tmp/libkongor_native_guard.so | grep -i stdc; then
    echo "ERROR: libstdc++ symbol found in libkongor_native_guard.so!" >&2
    exit 12
fi

if [ -d games/server/compatibility ]; then
    cp -f /tmp/libkongor_native_guard.so games/server/compatibility/libkongor_native_guard.so
    cp -f /tmp/libkongor_native_guard.so games/server/compatibility/libk2_native_guard.so
fi
if [ -d games/server/linux-x64 ]; then
    cp -f /tmp/libkongor_native_guard.so games/server/linux-x64/libkongor_native_guard.so
    cp -f /tmp/libkongor_native_guard.so games/server/linux-x64/libk2_native_guard.so
fi
if [ -d games/client/linux-x64/compatibility ]; then
    cp -f /tmp/libkongor_native_guard.so games/client/linux-x64/compatibility/libkongor_native_guard.so
    cp -f /tmp/libkongor_native_guard.so games/client/linux-x64/compatibility/libk2_native_guard.so
fi

echo "[3/4] Compiling & executing linux_queue_arcade_tests..."
g++ -O2 -std=c++20 native/proxies/native_guard/tests/linux_queue_arcade_tests.cpp /tmp/libkongor_native_guard.so -o /tmp/linux_queue_arcade_tests -ldl -lpthread -Wl,-rpath,/tmp
/tmp/linux_queue_arcade_tests

echo "[4/4] Executing headless hon-x86_64-server.bin under LD_PRELOAD..."
cd games/server/linux-x64
chmod +x hon-x86_64-server-wrapper.sh hon-x86_64-server.bin
if [ ! -f ../compatibility/libkongor_native_guard.so ] && [ ! -f compatibility/libkongor_native_guard.so ]; then
    export LD_PRELOAD=/tmp/libkongor_native_guard.so
fi
timeout 15 ./hon-x86_64-server-wrapper.sh -execute "kongor_arcade_mode 1; Quit" 2>&1 | tee /tmp/linux_server_smoke.log

echo "[4b/4] Verifying [NativeGuard:Linux] init, W^X hook arm, IPC socket, and teardown banners..."
grep -Fq "[NativeGuard:Linux] Loading" /tmp/linux_server_smoke.log || { echo "ERROR: Missing '[NativeGuard:Linux] Loading' banner in headless server output!" >&2; exit 21; }
grep -Fq "Unix domain IPC socket listening" /tmp/linux_server_smoke.log || { echo "ERROR: Missing 'Unix domain IPC socket listening' banner in headless server output!" >&2; exit 22; }
grep -Fq "Installed W^X inline trampoline on CHostServer::GenerateClientID" /tmp/linux_server_smoke.log || { echo "ERROR: Missing CHostServer::GenerateClientID W^X hook arm banner!" >&2; exit 24; }
if grep -Fq "ERROR: GenerateClientID hook disarmed" /tmp/linux_server_smoke.log; then
    echo "ERROR: Queue Arcade server falsely disarmed GenerateClientID hook during -execute!" >&2
    exit 25
fi
grep -Fq "[NativeGuard:Linux] Unloading" /tmp/linux_server_smoke.log || { echo "ERROR: Missing '[NativeGuard:Linux] Unloading' banner in headless server output!" >&2; exit 23; }

echo "[SUCCESS] Linux server LD_PRELOAD smoke gate completed cleanly."
'@

$runnerSh = Join-Path $logsDir "run_docker_smoke.sh"
[System.IO.File]::WriteAllText($runnerSh, $dockerScript.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

$containerSw = [System.Diagnostics.Stopwatch]::StartNew()
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$containerResult = & docker run --rm -v "${repoRoot}:/workspace" -w /workspace $Image bash /workspace/logs/run_docker_smoke.sh 2>&1
$containerExit = $LASTEXITCODE
$ErrorActionPreference = $prevEap
$containerSw.Stop()

# Cleanup temp runner script
if (Test-Path -LiteralPath $runnerSh) {
    Remove-Item -LiteralPath $runnerSh -Force -ErrorAction SilentlyContinue
}

$containerResult | Out-Host

if ($containerExit -ne 0) {
    $failReport = [PSCustomObject]@{
        status = "FAILED"
        exitCode = $containerExit
        durationMs = $containerSw.ElapsedMilliseconds
        timestamp = (Get-Date).ToString("o")
        output = ($containerResult -join "`n")
    }
    $failReport | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $lastSmokeJson -Encoding utf8
    Write-Host "[LINUX DOCKER SMOKE] FAILED with exit code $containerExit in $([math]::Round($containerSw.Elapsed.TotalSeconds, 1))s." -ForegroundColor Red
    exit $containerExit
}

$passedChecks = 0
$joinedOutput = ($containerResult -join "`n")
if ($joinedOutput -match 'Passed Checks:\s*(\d+)') {
    $passedChecks = [int]$Matches[1]
}
if ($passedChecks -lt 67) {
    throw "[LINUX DOCKER SMOKE] Expected >= 67 passed checks in linux_queue_arcade_tests output, observed $passedChecks."
}

$passReport = [PSCustomObject]@{
    status = "PASSED"
    exitCode = 0
    checks = $passedChecks
    binarySmoke = "CLEAN_EXIT_0"
    guardBannerVerified = $true
    durationMs = $containerSw.ElapsedMilliseconds
    timestamp = (Get-Date).ToString("o")
}
$passReport | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $lastSmokeJson -Encoding utf8

$sw.Stop()
Write-Host "[LINUX DOCKER SMOKE] SUCCESS: All $passedChecks checks and headless server smoke passed in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s." -ForegroundColor Green
exit 0
