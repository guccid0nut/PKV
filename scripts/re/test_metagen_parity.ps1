# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ============================================================================
# PROJECT KONGOR: PROTOCOL SCHEMA PARITY & INTEGRATION GATE SCRIPT
# Enforces zero-drift between data/schemas/protocol.json and generated
# artifacts across C++, C#, and Lua.
# ============================================================================

[CmdletBinding()]
param(
    [string]$SchemaPath = "data/schemas/protocol.json",
    [string]$BaselineSchemaPath = "",
    [switch]$VerifyWireCompatibilityOnly = $false,
    [string]$JsonOutput = "",
    [switch]$VerboseOutput = $false
)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent ($PSScriptRoot | Split-Path -Parent)

if ($VerifyWireCompatibilityOnly) {
    $wireArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repoRoot "scripts/re/generate_protocol.ps1"),
        "-SchemaPath", $SchemaPath,
        "-VerifyWireCompatibilityOnly"
    )
    if (-not [string]::IsNullOrWhiteSpace($BaselineSchemaPath)) {
        $wireArgs += @("-BaselineSchemaPath", $BaselineSchemaPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
        $wireArgs += @("-JsonOutput", $JsonOutput)
    }
    & powershell.exe @wireArgs
    exit $LASTEXITCODE
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR PROTOCOL PARITY GATE] Running Cross-Language Integrity Checks" -ForegroundColor Cyan
Write-Host " Repo Root: $repoRoot" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

$gateFailed = $false

# 1. Metagen Parity & Semantic Wire-Compatibility Check (--check, #246)
Write-Host "`n[GATE 1/4] Checking semantic wire compatibility & schema drift against generated code..." -ForegroundColor Cyan
$genArgs = @(
    "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $repoRoot "scripts/re/generate_protocol.ps1"),
    "-SchemaPath", $SchemaPath,
    "-Check"
)
if (-not [string]::IsNullOrWhiteSpace($BaselineSchemaPath)) {
    $genArgs += @("-BaselineSchemaPath", $BaselineSchemaPath)
}
& powershell.exe @genArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host " [FAIL] Schema parity or semantic wire-compatibility check failed!" -ForegroundColor Red
    $gateFailed = $true
} else {
    Write-Host " [PASS] Semantic wire-compatibility and schema parity check passed: 0 drift detected." -ForegroundColor Green
}

# 2. Lua Bytecode Verification
Write-Host "`n[GATE 2/4] Verifying generated Lua bytecode syntax..." -ForegroundColor Cyan
$luaFile = Join-Path $repoRoot "games/client/override/ui/scripts/generated/protocol.lua"
& powershell.exe -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts/verify_client_lua.ps1") -Path $luaFile
if ($LASTEXITCODE -ne 0) {
    Write-Host " [FAIL] Generated Lua bytecode check failed!" -ForegroundColor Red
    $gateFailed = $true
} else {
    Write-Host " [PASS] Generated Lua bytecode verified." -ForegroundColor Green
}

# 3. C# Contract Compilation Check
Write-Host "`n[GATE 3/4] Compiling C# contracts with generated structs..." -ForegroundColor Cyan
$csProj = Join-Path $repoRoot "src/libs/contracts/NEXUS.Contracts.csproj"
$buildOut = & dotnet build $csProj -c Release 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host " [FAIL] C# contracts build failed:" -ForegroundColor Red
    $buildOut | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
    $gateFailed = $true
} else {
    Write-Host " [PASS] C# contracts built with 0 errors, 0 warnings." -ForegroundColor Green
}

# 4. Native CTest Suite
Write-Host "`n[GATE 4/4] Running native test suite (Seqlock, Arenas, Strings)..." -ForegroundColor Cyan
$ctestOut = & ctest --test-dir (Join-Path $repoRoot "native/tests/build") -C Release --output-on-failure 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host " [FAIL] Native tests failed!" -ForegroundColor Red
    $ctestOut | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
    $gateFailed = $true
} else {
    Write-Host " [PASS] All native unit tests and benchmarks passed." -ForegroundColor Green
}

Write-Host "`n================================================================================" -ForegroundColor Cyan
if ($gateFailed) {
    Write-Host " [KONGOR PROTOCOL PARITY GATE] FAILED in $($sw.ElapsedMilliseconds)ms" -ForegroundColor Red
    Write-Host " Action: Run 'powershell.exe -File scripts/re/generate_protocol.ps1' to re-synchronize generated files." -ForegroundColor Yellow
    exit 1
} else {
    Write-Host " [KONGOR PROTOCOL PARITY GATE] ALL CHECKS PASSED in $($sw.ElapsedMilliseconds)ms!" -ForegroundColor Green
    Write-Host "================================================================================" -ForegroundColor Cyan
    exit 0
}
