# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    16-Case Hermetic Mutant & Zero-Assertion Trap Suite for scripts/gate_check.ps1.

.DESCRIPTION
    Proves that all 5 tiers of scripts/gate_check.ps1 fail deterministically on injected defects
    and zero-assertion traps inside an isolated $env:TEMP workspace, using typed JSON telemetry
    (-JsonOutput) and shifted bitmask exit codes (2/4/8/16/32; 1 and 64 reserved for crashes).
    Also enforces a dual-file SHA-256 integrity pin (scripts/.gate_integrity) covering both
    scripts/gate_check.ps1 and scripts/test_gate_mutants.ps1.

.EXAMPLE
    .\scripts\test_gate_mutants.ps1
    .\scripts\test_gate_mutants.ps1 -UpdateIntegrityPin
#>

[CmdletBinding()]
param(
    [switch]$UpdateIntegrityPin = $false
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$GateScript = Join-Path $PSScriptRoot "gate_check.ps1"
$MutantScript = $PSCommandPath
$IntegrityFile = Join-Path $PSScriptRoot ".gate_integrity"

$gateSha = (Get-FileHash -LiteralPath $GateScript -Algorithm SHA256).Hash.ToLowerInvariant()
$mutantSha = (Get-FileHash -LiteralPath $MutantScript -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR GATE MUTANT VERIFICATION SUITE] 16-Case Hermetic 5-Tier Oracle" -ForegroundColor Cyan
Write-Host " gate_check.ps1 SHA256        : $gateSha" -ForegroundColor Gray
Write-Host " test_gate_mutants.ps1 SHA256 : $mutantSha" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

if (-not $UpdateIntegrityPin) {
    if (-not (Test-Path -LiteralPath $IntegrityFile)) {
        Write-Host " [INTEGRITY FAIL] Missing '$IntegrityFile'. Run: .\scripts\test_gate_mutants.ps1 -UpdateIntegrityPin" -ForegroundColor Red
        exit 1
    }
    $pinData = Get-Content -LiteralPath $IntegrityFile -Raw | ConvertFrom-Json
    if ($pinData.gateCheckSha256 -ne $gateSha -or $pinData.testGateMutantsSha256 -ne $mutantSha) {
        Write-Host " [INTEGRITY FAIL] Gate or mutant suite modified since last pin!" -ForegroundColor Red
        Write-Host "   Expected gate_check.ps1        : $($pinData.gateCheckSha256)" -ForegroundColor Yellow
        Write-Host "   Actual   gate_check.ps1        : $gateSha" -ForegroundColor Yellow
        Write-Host "   Expected test_gate_mutants.ps1 : $($pinData.testGateMutantsSha256)" -ForegroundColor Yellow
        Write-Host "   Actual   test_gate_mutants.ps1 : $mutantSha" -ForegroundColor Yellow
        Write-Host "   Action: Review the diff, then run: .\scripts\test_gate_mutants.ps1 -UpdateIntegrityPin" -ForegroundColor Red
        exit 1
    }
    Write-Host " [INTEGRITY PASS] Dual-file SHA-256 pin verified against scripts/.gate_integrity." -ForegroundColor Green
}

$scratchRoot = Join-Path $env:TEMP ("kongor_gate_mutants_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    $dir = [System.IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function New-BaselineWorkspace {
    param([string]$CaseName)
    $ws = Join-Path $scratchRoot $CaseName
    if (Test-Path -LiteralPath $ws) {
        Remove-Item -LiteralPath $ws -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ws -Force | Out-Null

    # 1. Clean production C# file
    Write-Utf8NoBom -Path (Join-Path $ws "src\CleanSample.cs") -Content @"
namespace Kongor.Sample
{
    public sealed class CleanSample
    {
        public int Add(int a, int b) => a + b;
    }
}
"@

    # 2. Genuine *Tests.cs file containing .Wait() (proves positive-direction filename exemption)
    Write-Utf8NoBom -Path (Join-Path $ws "src\SampleTests.cs") -Content @"
namespace Kongor.Sample
{
    public sealed class SampleTests
    {
        public void Run()
        {
            System.Threading.Tasks.Task.CompletedTask.Wait();
        }
    }
}
"@

    # 3. Clean PowerShell script
    Write-Utf8NoBom -Path (Join-Path $ws "scripts\clean_sample.ps1") -Content 'Write-Output "ok"'

    # 4. Minimal passing Lua contract suite
    Write-Utf8NoBom -Path (Join-Path $ws "tests\client_lua\test_core_orchestrator_runtime.lua") -Content 'print("=== OrchestratorRuntimeSuite: 1 PASSED, 0 FAILED ===")'

    # 5. Clean override XML interface
    $cleanXml = '<?xml version="1.0" encoding="UTF-8"?><interface name="clean"></interface>'
    Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\clean.interface") -Content $cleanXml

    # 6. Matching tracked mirror XML interface in games/client/game (for Tier 5 parity)
    Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\clean.interface") -Content $cleanXml

    # 7. Valid >= 40,960-byte version.dll (Phase 18 Item 6 canonical artifact)
    $dllPath = Join-Path $ws "games\client\version.dll"
    [System.IO.File]::WriteAllBytes($dllPath, [byte[]]::new(45056))

    # 8. Valid config/vfs_mounts.json
    Write-Utf8NoBom -Path (Join-Path $ws "config\vfs_mounts.json") -Content '{"mounts":[]}'

    return $ws
}

function Invoke-HermeticGate {
    param(
        [string]$Workspace,
        [int]$MinTrackedPairs = 1,
        [switch]$OmitFull
    )
    $jsonOut = Join-Path $scratchRoot ("result_" + [guid]::NewGuid().ToString("N") + ".json")
    $argList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $GateScript,
        "-TargetRoot", $Workspace,
        "-RequireVerifier",
        "-MinTrackedPairs", "$MinTrackedPairs",
        "-JsonOutput", $jsonOut
    )
    if (-not $OmitFull) {
        $argList += "-Full"
    }

    $output = & powershell @argList 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 1 -or $code -eq 64) {
        throw "gate_check.ps1 crashed with reserved exit code $code! Output: $($output | Out-String)"
    }
    if (-not (Test-Path -LiteralPath $jsonOut)) {
        throw "gate_check.ps1 did not emit -JsonOutput file at '$jsonOut'! ExitCode=$code"
    }

    $jsonObj = Get-Content -LiteralPath $jsonOut -Raw | ConvertFrom-Json
    return [PSCustomObject]@{
        ExitCode = $code
        Json     = $jsonObj
        Output   = ($output | Out-String)
    }
}

$results = New-Object System.Collections.Generic.List[object]

function Run-MutantCase {
    param(
        [string]$Name,
        [scriptblock]$TestBlock
    )
    $caseSw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $TestBlock
        $caseSw.Stop()
        Write-Host ("  [PASS] {0,-42} ({1}ms)" -f $Name, $caseSw.ElapsedMilliseconds) -ForegroundColor Green
        $results.Add([PSCustomObject]@{ Name = $Name; Passed = $true; Error = ""; DurationMs = $caseSw.ElapsedMilliseconds })
    } catch {
        $caseSw.Stop()
        $errMsg = $_.Exception.Message
        Write-Host ("  [FAIL] {0,-42} ({1}ms): {2}" -f $Name, $caseSw.ElapsedMilliseconds, $errMsg) -ForegroundColor Red
        $results.Add([PSCustomObject]@{ Name = $Name; Passed = $false; Error = $errMsg; DurationMs = $caseSw.ElapsedMilliseconds })
    }
}

try {
    # 1. Baseline_Clean
    Run-MutantCase -Name "1. Baseline_Clean" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case01_baseline"
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if ($r.ExitCode -ne 0) { throw "Expected ExitCode 0, got $($r.ExitCode)" }
        if ($r.Json.overallStatus -ne "PASS") { throw "Expected overallStatus PASS, got $($r.Json.overallStatus)" }
        if ($r.Json.T1.csFilesScanned -ne 2) { throw "Expected 2 .cs files (CleanSample.cs + SampleTests.cs), got $($r.Json.T1.csFilesScanned)" }
        if ($r.Json.T1.psFilesScanned -ne 1) { throw "Expected 1 .ps1 file, got $($r.Json.T1.psFilesScanned)" }
        if ($r.Json.T2.contractsPassed -ne 1) { throw "Expected 1 Lua contract passed, got $($r.Json.T2.contractsPassed)" }
        if ($r.Json.T3.xmlFilesScanned -ne 1) { throw "Expected 1 XML file scanned, got $($r.Json.T3.xmlFilesScanned)" }
        if ($r.Json.T4.nativeGuardBytes -ne 45056) { throw "Expected nativeGuardBytes 45056, got $($r.Json.T4.nativeGuardBytes)" }
        if ($r.Json.T5.trackedPairsCompared -ne 1) { throw "Expected 1 tracked pair compared, got $($r.Json.T5.trackedPairsCompared)" }
    }

    # 2. Mutant_0_TargetRoot_Leak
    Run-MutantCase -Name "2. Mutant_0_TargetRoot_Leak" -TestBlock {
        $emptyWs = Join-Path $scratchRoot "case02_empty"
        New-Item -ItemType Directory -Path $emptyWs -Force | Out-Null
        $r = Invoke-HermeticGate -Workspace $emptyWs -MinTrackedPairs 1
        if ($r.ExitCode -ne 62) { throw "Expected ExitCode 62 (2|4|8|16|32 all 5 tiers failing), got $($r.ExitCode)" }
        if ($r.Json.T1.csFilesScanned -ne 0 -or $r.Json.T1.psFilesScanned -ne 0 -or
            $r.Json.T2.contractsPassed -ne 0 -or $r.Json.T3.xmlFilesScanned -ne 0 -or
            $r.Json.T4.nativeGuardBytes -ne 0 -or $r.Json.T5.trackedPairsCompared -ne 0) {
            throw "TargetRoot leaked scan counts from real repo!"
        }
    }

    # 3. Mutant_1_BannedSymbol_SyncOverAsync
    Run-MutantCase -Name "3. Mutant_1_BannedSymbol_SyncOverAsync" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case03_bannedsym"
        Write-Utf8NoBom -Path (Join-Path $ws "src\CleanSample.cs") -Content @"
namespace Kongor.Sample
{
    public sealed class CleanSample
    {
        public int Bad() => System.Threading.Tasks.Task.FromResult(1).GetAwaiter().GetResult();
    }
}
"@
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL") {
            throw "Expected Tier 1 FAIL (bit 2), got ExitCode=$($r.ExitCode), T1=$($r.Json.T1.status)"
        }
    }

    # 4. Mutant_2_PowerShell_Syntax_Corrupt
    Run-MutantCase -Name "4. Mutant_2_PowerShell_Syntax_Corrupt" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case04_psast"
        Write-Utf8NoBom -Path (Join-Path $ws "scripts\clean_sample.ps1") -Content 'function Broken-Syntax {'
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL") {
            throw "Expected Tier 1 FAIL (bit 2), got ExitCode=$($r.ExitCode), T1=$($r.Json.T1.status)"
        }
    }

    # 5. Mutant_3_Lua_Contract_Failure
    Run-MutantCase -Name "5. Mutant_3_Lua_Contract_Failure" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case05_luafail"
        Write-Utf8NoBom -Path (Join-Path $ws "tests\client_lua\test_core_orchestrator_runtime.lua") -Content 'error("injected lua contract failure")'
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 4) -eq 0 -or $r.Json.T2.status -ne "FAIL") {
            throw "Expected Tier 2 FAIL (bit 4), got ExitCode=$($r.ExitCode), T2=$($r.Json.T2.status)"
        }
    }

    # 6. Mutant_4_Malformed_Interface_Xml
    Run-MutantCase -Name "6. Mutant_4_Malformed_Interface_Xml" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case06_xmlfail"
        $badXml = '<?xml version="1.0" encoding="UTF-8"?><interface name="clean"><panel></interface>'
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\clean.interface") -Content $badXml
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\clean.interface") -Content $badXml
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 8) -eq 0 -or $r.Json.T3.status -ne "FAIL") {
            throw "Expected Tier 3 FAIL (bit 8), got ExitCode=$($r.ExitCode), T3=$($r.Json.T3.status)"
        }
    }

    # 7. Mutant_5_Truncated_NativeGuard_Dll
    Run-MutantCase -Name "7. Mutant_5_Truncated_NativeGuard_Dll" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case07_dlltrunc"
        [System.IO.File]::WriteAllBytes((Join-Path $ws "games\client\version.dll"), [byte[]]::new(1024))
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 16) -eq 0 -or $r.Json.T4.status -ne "FAIL") {
            throw "Expected Tier 4 FAIL (bit 16), got ExitCode=$($r.ExitCode), T4=$($r.Json.T4.status)"
        }
    }

    # 8. Mutant_6_Tier5_CrossTree_Text_Drift
    Run-MutantCase -Name "8. Mutant_6_Tier5_CrossTree_Text_Drift" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case08_t5text"
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\clean.interface") -Content '<?xml version="1.0" encoding="UTF-8"?><interface name="drifted"></interface>'
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 32) -eq 0 -or $r.Json.T5.status -ne "FAIL" -or $r.Json.T5.driftedTrackedFiles.Count -ne 1) {
            throw "Expected Tier 5 FAIL (bit 32) with 1 drifted tracked file, got ExitCode=$($r.ExitCode), T5=$($r.Json.T5.status)"
        }
    }

    # 9. Mutant_7_Tier5_CrossTree_Binary_Drift
    Run-MutantCase -Name "9. Mutant_7_Tier5_CrossTree_Binary_Drift" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case09_t5bin"
        $ovrMdf = Join-Path $ws "games\client\override\heroes\test.mdf"
        $gameMdf = Join-Path $ws "games\client\game\heroes\test.mdf"
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($ovrMdf)) -Force | Out-Null
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($gameMdf)) -Force | Out-Null
        [System.IO.File]::WriteAllBytes($ovrMdf, [byte[]]@(1, 2, 3, 4))
        [System.IO.File]::WriteAllBytes($gameMdf, [byte[]]@(1, 2, 3, 9))
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 32) -eq 0 -or $r.Json.T5.status -ne "FAIL" -or $r.Json.T5.driftedTrackedFiles.Count -ne 1) {
            throw "Expected Tier 5 binary SHA-256 FAIL (bit 32), got ExitCode=$($r.ExitCode), T5=$($r.Json.T5.status)"
        }
    }

    # 10. Mutant_8_PathLaundered_BannedSymbol
    Run-MutantCase -Name "10. Mutant_8_PathLaundered_BannedSymbol" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case10_pathlaunder"
        Write-Utf8NoBom -Path (Join-Path $ws "src\Mocks\ProdService.cs") -Content @"
namespace Kongor.Sample
{
    public sealed class ProdService
    {
        public void Execute()
        {
            System.Threading.Tasks.Task.CompletedTask.Wait();
        }
    }
}
"@
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL") {
            throw "Expected Tier 1 FAIL on path-laundered src/Mocks/ProdService.cs, got ExitCode=$($r.ExitCode)"
        }
    }

    # 11. Trap_Tier2_Zero_Lua_Contracts
    Run-MutantCase -Name "11. Trap_Tier2_Zero_Lua_Contracts" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case11_zerolua"
        Write-Utf8NoBom -Path (Join-Path $ws "tests\client_lua\test_core_orchestrator_runtime.lua") -Content 'print("=== OrchestratorRuntimeSuite: 0 PASSED, 0 FAILED ===")'
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 4) -eq 0 -or $r.Json.T2.status -ne "FAIL" -or $r.Json.T2.contractsPassed -ne 0) {
            throw "Expected Tier 2 Zero-Assertion Guard FAIL (bit 4), got ExitCode=$($r.ExitCode), contractsPassed=$($r.Json.T2.contractsPassed)"
        }
    }

    # 12. Trap_Tier3_Zero_Xml_Files
    Run-MutantCase -Name "12. Trap_Tier3_Zero_Xml_Files" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case12_zeroxml"
        Remove-Item -LiteralPath (Join-Path $ws "games\client\override\ui\clean.interface") -Force
        Remove-Item -LiteralPath (Join-Path $ws "games\client\game\ui\clean.interface") -Force
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 0
        if (($r.ExitCode -band 8) -eq 0 -or $r.Json.T3.status -ne "FAIL" -or $r.Json.T3.xmlFilesScanned -ne 0) {
            throw "Expected Tier 3 Zero-Assertion Guard FAIL (bit 8), got ExitCode=$($r.ExitCode), xmlFilesScanned=$($r.Json.T3.xmlFilesScanned)"
        }
    }

    # 13. Trap_Tier5_Below_Min_Tracked_Pairs
    Run-MutantCase -Name "13. Trap_Tier5_Below_Min_Tracked_Pairs" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case13_t5minpairs"
        Remove-Item -LiteralPath (Join-Path $ws "games\client\game\ui\clean.interface") -Force
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 32) -eq 0 -or $r.Json.T5.status -ne "FAIL" -or $r.Json.T5.trackedPairsCompared -ne 0) {
            throw "Expected Tier 5 floor guard FAIL (bit 32), got ExitCode=$($r.ExitCode), trackedPairsCompared=$($r.Json.T5.trackedPairsCompared)"
        }
    }

    # 14. Trap_Kb_Index_Drift
    Run-MutantCase -Name "14. Trap_Kb_Index_Drift" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case14_kbdrift"
        $wsKbDir = Join-Path $ws "docs\reference\field-notes"
        New-Item -ItemType Directory -Path $wsKbDir -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $RepoRoot "docs\reference\field-notes\FN-001-obs-graphics-hook-thread29-wndproc-race.md") -Destination $wsKbDir -Force
        Write-Utf8NoBom -Path (Join-Path $wsKbDir "index.json") -Content "[]"

        $wsMcpDir = Join-Path $ws "docs\reference\mcp-issues"
        New-Item -ItemType Directory -Path $wsMcpDir -Force | Out-Null
        $sampleMcpFile = Get-ChildItem -LiteralPath (Join-Path $RepoRoot "docs\reference\mcp-issues") -Filter "MCP-001-*.md" -File | Select-Object -First 1
        Copy-Item -LiteralPath $sampleMcpFile.FullName -Destination $wsMcpDir -Force
        Write-Utf8NoBom -Path (Join-Path $wsMcpDir "index.json") -Content "[]"
        Write-Utf8NoBom -Path (Join-Path $ws "docs\MCP_ISSUES.md") -Content "### [MCP-999] Legacy inline regression"

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        $hasMonolithErr = @($r.Json.T1.violations | Where-Object { $_ -match 'Legacy monolith regression in docs/MCP_ISSUES\.md' }).Count -gt 0
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL" -or (-not $r.Json.T1.kbDrift) -or (-not $r.Json.T1.mcpIssuesDrift) -or (-not $hasMonolithErr)) {
            throw "Expected Tier 1 KB/MCP drift + monolith regression FAIL (bit 2), got ExitCode=$($r.ExitCode), kbDrift=$($r.Json.T1.kbDrift), mcpIssuesDrift=$($r.Json.T1.mcpIssuesDrift), hasMonolithErr=$hasMonolithErr"
        }
    }

    # 15. Incremental_Git_Zero_Cs_Allowed
    Run-MutantCase -Name "15. Incremental_Git_Zero_Cs_Allowed" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case15_gitinc"
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $null = & git -C $ws -c core.autocrlf=false init 2>$null
            $null = & git -C $ws -c core.autocrlf=false add -A 2>$null
            $null = & git -C $ws -c core.autocrlf=false -c user.email=gate@kongor.local -c user.name=GateMutants -c commit.gpgsign=false commit -m "init" 2>$null
        } finally {
            $ErrorActionPreference = $prevEap
        }
        $updatedXml = '<?xml version="1.0" encoding="UTF-8"?><interface name="clean_modified"></interface>'
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\clean.interface") -Content $updatedXml
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\clean.interface") -Content $updatedXml
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1 -OmitFull
        if ($r.ExitCode -ne 0 -or -not $r.Json.T1.inGit -or $r.Json.T1.csFilesScanned -ne 0) {
            throw "Expected ExitCode 0 with inGit=true and csFilesScanned=0, got ExitCode=$($r.ExitCode), inGit=$($r.Json.T1.inGit), csFiles=$($r.Json.T1.csFilesScanned)"
        }
    }

    # 16. Trap_Expired_BannedSymbol_Baseline
    Run-MutantCase -Name "16. Trap_Expired_BannedSymbol_Baseline" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case16_expiredbase"
        Write-Utf8NoBom -Path (Join-Path $ws "src\CleanSample.cs") -Content @"
namespace Kongor.Sample
{
    public sealed class CleanSample
    {
        public int Legacy() => System.Threading.Tasks.Task.FromResult(1).GetAwaiter().GetResult();
    }
}
"@
        $expiredBaseline = @(
            [ordered]@{
                file      = "src/CleanSample.cs"
                symbol    = ".GetAwaiter().GetResult()"
                maxCount  = 1
                reason    = "Expired test debt"
                expiresOn = "2020-01-01"
            }
        ) | ConvertTo-Json -Depth 4
        Write-Utf8NoBom -Path (Join-Path $ws "scripts\.banned_symbols_baseline.json") -Content "[$expiredBaseline]"
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL" -or $r.Json.T1.expiredBaselineCount -ne 1) {
            throw "Expected Tier 1 FAIL on expired baseline (expiredBaselineCount=1), got ExitCode=$($r.ExitCode), expired=$($r.Json.T1.expiredBaselineCount)"
        }
    }

    # 17. Mutant_9_Decompiler_Fingerprint_And_UserPath
    Run-MutantCase -Name "17. Mutant_9_Decompiler_Fingerprint_And_UserPath" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case17_cleanroom"
        Write-Utf8NoBom -Path (Join-Path $ws "src\CleanSample.cs") -Content @"
namespace Kongor.Sample
{
    public sealed class CleanSample
    {
        // Historical dossier reference in comment: FUN_180035d40 is allowed
        public int FUN_140012340() => 42;
    }
}
"@
        $nativeDir = Join-Path $ws "native\proxies"
        New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
        Write-Utf8NoBom -Path (Join-Path $nativeDir "SampleHook.cpp") -Content @"
// Cleanroom native sample with hardcoded user path violation
const char* GetLocalDumpPath() {
    return "C:\\Users\\developer\\Desktop\\hon_dump.dmp";
}
"@
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL" -or $r.Json.T1.cleanroomViolationsCount -ne 2) {
            throw "Expected Tier 1 FAIL on decompiler fingerprint + hardcoded user path (cleanroomViolationsCount=2), got ExitCode=$($r.ExitCode), cleanroomViolations=$($r.Json.T1.cleanroomViolationsCount)"
        }
    }

    # 18. Mutant_10_Unknown_CVar_Reference (Option S)
    Run-MutantCase -Name "18. Mutant_10_Unknown_CVar_Reference" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case18_unknowncvar"
        $cvarCatJson = '{"schema_version":"1.0.0","cvar_names":["vid_fps","tao_req"]}'
        Write-Utf8NoBom -Path (Join-Path $ws "docs\reference\cvar_catalog.json") -Content $cvarCatJson
        $badCvarXml = '<?xml version="1.0" encoding="UTF-8"?><interface name="clean"><panel cvar="cg_nonexistent_typo_cvar_999" onloadlua="GetCvarBool(&apos;cg_another_typo_cvar&apos;)" /></interface>'
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\clean.interface") -Content $badCvarXml
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\clean.interface") -Content $badCvarXml
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 8) -eq 0 -or $r.Json.T3.status -ne "FAIL" -or $r.Json.T3.unknownCvarViolations -ne 2) {
            throw "Expected Tier 3 FAIL (bit 8) with unknownCvarViolations=2, got ExitCode=$($r.ExitCode), T3=$($r.Json.T3.status), unknownCvars=$($r.Json.T3.unknownCvarViolations)"
        }
    }

    # 19. Mutant_11_Hook_Site_Prologue_Drift (Option Q)
    Run-MutantCase -Name "19. Mutant_11_Hook_Site_Prologue_Drift" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case19_hooksitedrift"
        $patchDir = Join-Path $ws "data\patches"
        New-Item -ItemType Directory -Path $patchDir -Force | Out-Null
        $fakeBinPath = Join-Path $ws "games\client\k2_x64.dll"
        [byte[]]$fakeBytes = New-Object byte[] 256
        for ($i = 0; $i -lt 16; $i++) { $fakeBytes[64 + $i] = 0x90 }
        [System.IO.File]::WriteAllBytes($fakeBinPath, $fakeBytes)
        $badHookJson = '{"schema_version":"1.0.0","sites":[{"group":"vfs","name":"CArchive::Open","binary":"k2_x64.dll","rva":"0x40","file_offset":64,"stolen_bytes":15,"expected_prologue_hex":"48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20"}]}'
        Write-Utf8NoBom -Path (Join-Path $patchDir "native_hook_sites.json") -Content $badHookJson
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 16) -eq 0 -or $r.Json.T4.status -ne "FAIL") {
            throw "Expected Tier 4 FAIL (bit 16) on hook site prologue drift, got ExitCode=$($r.ExitCode), T4=$($r.Json.T4.status)"
        }
    }

    Run-MutantCase -Name "20. Mutant_12_Veh_Heap_Allocation_Banned_Symbol" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case20_veh_heap"
        $ngDir = Join-Path $ws "native\proxies\native_guard"
        New-Item -ItemType Directory -Path $ngDir -Force | Out-Null
        $vehCpp = @"
// [KONGOR_VEH_CRASH_PATH_BEGIN]
void VehBadHandler() {
    std::ostringstream badHeapStream;
    badHeapStream << "crash";
}
// [KONGOR_VEH_CRASH_PATH_END]
"@
        Write-Utf8NoBom -Path (Join-Path $ngDir "dllmain.cpp") -Content $vehCpp
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL") {
            throw "Expected Tier 1 FAIL (bit 2) on std::ostringstream inside KONGOR_VEH_CRASH_PATH, got ExitCode=$($r.ExitCode), T1=$($r.Json.T1.status)"
        }
    }

    # 21. Mutant_13_Python_Slop_Utf8_And_Offline_Import_Boundary (#36, #53, #104)
    Run-MutantCase -Name "21. Mutant_13_Python_Slop_Utf8_And_Offline_Import_Boundary" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case21_pyslop"
        $pyLibDir = Join-Path $ws "scripts\lib"
        New-Item -ItemType Directory -Path $pyLibDir -Force | Out-Null
        $badPy = @'
import frida  # type: ignore

def bad_reader(p):
    try:
        with open("foo.txt", "r") as f:
            return f.read()
    except Exception:
        pass
'@
        Write-Utf8NoBom -Path (Join-Path $pyLibDir "bad_offline.py") -Content $badPy
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL" -or [int]$r.Json.T1.pythonSlopViolationsCount -lt 4) {
            throw "Expected Tier 1 FAIL (bit 2) with pythonSlopViolationsCount >= 4 (#36, #53, #104), got ExitCode=$($r.ExitCode), pySlop=$($r.Json.T1.pythonSlopViolationsCount)"
        }
    }

    # 22. Mutant_14_Skill_FakeFlag_And_Missing_Capability_Contract (#42, #103)
    Run-MutantCase -Name "22. Mutant_14_Skill_FakeFlag_And_Missing_Capability_Contract" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case22_skillgate"
        $badSkillDir = Join-Path $ws ".agents\skills\bad-skill"
        New-Item -ItemType Directory -Path $badSkillDir -Force | Out-Null
        $badSkillMd = @'
---
name: bad-skill
description: Missing capability_contract and invokes a non-existent flag on gate_check.ps1.
---

Run: `powershell -File scripts/gate_check.ps1 -NonExistentFakeFlag999`
'@
        Write-Utf8NoBom -Path (Join-Path $badSkillDir "SKILL.md") -Content $badSkillMd
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or $r.Json.T1.status -ne "FAIL" -or [int]$r.Json.T1.skillGateViolationsCount -lt 2) {
            throw "Expected Tier 1 FAIL (bit 2) with skillGateViolationsCount >= 2 (#42, #103), got ExitCode=$($r.ExitCode), skillViolations=$($r.Json.T1.skillGateViolationsCount)"
        }
    }

    # 23. Mutant_15_Phase19_DocDrift_Layer_Cycle_Secret_And_HotLoop (#239, #240, #241, #248)
    Run-MutantCase -Name "23. Mutant_15_Phase19_DocDrift_Layer_Cycle_Secret_And_HotLoop" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case23_phase19_t1"
        # #239: Multi-kind doc drift in docs/gate_doc_drift
        Write-Utf8NoBom -Path (Join-Path $ws "docs\gate_doc_drift\guide.md") -Content @'
# Guide
See [Missing](scripts/no_such_script_239.ps1) and [BadSection](#non-existent-anchor).
'@
        # #240: Layer violation (protocol_core !-> mcp_cli) + cycle in module_graph_probe.json
        Write-Utf8NoBom -Path (Join-Path $ws "src\libs\protocol-core\BadProto.cs") -Content @'
namespace Kongor.Protocol {
    public sealed class BadProto {
        public string Ref => "Kongor.Mcp.Violation";
    }
}
'@
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\module_graph_probe.json") -Content '{"edges":[{"from":"ModA","to":"ModB"},{"from":"ModB","to":"ModA"}]}'

        # #241: Secret leak in data/gate/secret_scan_probe
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\secret_scan_probe\leak.cfg") -Content ('api_' + 'key = "ghp_' + '1234567890abcdefghijklmnopqrstuvwxyz"')

        # #248: Hot-loop std::regex in C++ (with method call in loop header & #ifdef unbalanced brace) and EF SaveChangesAsync in C#
        Write-Utf8NoBom -Path (Join-Path $ws "native\proxies\HotLoopBad.cpp") -Content @'
void OnFrame() {
    for (int i = 0; i < items.size(); i++) {
#ifdef _WIN32
        if (ready) {
#else
        if (ready && fallback) {
#endif
            std::regex badRe("[a-z]+");
        }
    }
}
'@
        Write-Utf8NoBom -Path (Join-Path $ws "src\CleanSample.cs") -Content @'
namespace Kongor.Sample {
    public sealed class CleanSample {
        public void BadLoop(int[] items, dynamic db) {
            foreach (int x in items) {
                db.SaveChangesAsync();
            }
        }
    }
}
'@
        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or
            [int]$r.Json.T1.docDriftViolationsCount -lt 1 -or
            [int]$r.Json.T1.archLayerViolationsCount -lt 1 -or
            [int]$r.Json.T1.archCycleViolationsCount -lt 1 -or
            [int]$r.Json.T1.secretLeakViolationsCount -lt 1 -or
            [int]$r.Json.T1.hotLoopViolationsCount -lt 2) {
            throw "Expected Tier 1 FAIL on #239/#240/#241/#248, got docDrift=$($r.Json.T1.docDriftViolationsCount), layer=$($r.Json.T1.archLayerViolationsCount), cycle=$($r.Json.T1.archCycleViolationsCount), secret=$($r.Json.T1.secretLeakViolationsCount), hotloop=$($r.Json.T1.hotLoopViolationsCount)"
        }
    }

    # 24. Mutant_16_Phase19_SymbolImpact_StaleTest_And_CoChange (#242, #245, #249)
    Run-MutantCase -Name "24. Mutant_16_Phase19_SymbolImpact_StaleTest_And_CoChange" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case24_phase19_t2_t5"
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\symbol_impact_probe.json") -Content '{"symbol_changes":[{"symbol":"DispatchPacket","change_kind":"signature-changed","file":"src/Net.cs","new_min_arity":3,"callers":[{"file":"src/Client.cs","updated":false,"arg_count":1}]}]}'
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\test_signal_probe.json") -Content '{"enforce_missing_test_signal":true,"modified_production_files":["src/Net.cs"],"modified_test_files":[],"stale_test_candidates":[{"test_file":"tests/NetTests.cs","missing_target":"RemovedLegacyEndpoint"}]}'
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\cochange_probe.json") -Content '{"tau":500,"min_confidence":0.80,"min_support":3,"modified_files":["data/schemas/protocol.json"],"commits":[{"age":5,"files":["data/schemas/protocol.json","src/ProtocolGen.cs"]},{"age":10,"files":["data/schemas/protocol.json","src/ProtocolGen.cs"]},{"age":15,"files":["data/schemas/protocol.json","src/ProtocolGen.cs"]}]}'

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 4) -eq 0 -or
            ($r.ExitCode -band 32) -eq 0 -or
            [int]$r.Json.T2.symbolBreakingViolationsCount -ne 1 -or
            [int]$r.Json.T2.staleTestCandidatesCount -ne 1 -or
            [int]$r.Json.T2.missingTestSignalsCount -ne 1 -or
            [int]$r.Json.T5.coChangeViolationsCount -ne 1) {
            throw "Expected Tier 2 & Tier 5 FAIL on #245/#249/#242, got ExitCode=$($r.ExitCode), symBreak=$($r.Json.T2.symbolBreakingViolationsCount), staleTest=$($r.Json.T2.staleTestCandidatesCount), coChange=$($r.Json.T5.coChangeViolationsCount)"
        }
    }

    # 25. Mutant_17_Lua_EmptyTableTrap_SelfDestructInit_MatrixDrift_And_ReadTextFileRatchet
    Run-MutantCase -Name "25. Mutant_17_Lua_EmptyTableTrap_SelfDestructInit_MatrixDrift_And_ReadTextFileRatchet" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case25_lua_shj_lints"
        $badLua = @'
if not HoN_Main.menuPanels.table then
    GetMainMenuPanels = function() end
end
HoN_Main.menuPanels.table['unmapped_secret_panel'] = true
'@
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\scripts\main_v2.lua") -Content $badLua
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\scripts\main_v2.lua") -Content $badLua
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\main_menu_panel_matrix.json") -Content '{"schemaVersion":"1.0.0","activePanels":[{"panelKey":"news"}],"exemptPanels":[]}'

        $badSuite = @'
local function readTextFile(p) return "" end
local a1 = readTextFile("1") local a2 = readTextFile("2") local a3 = readTextFile("3")
local a4 = readTextFile("4") local a5 = readTextFile("5") local a6 = readTextFile("6")
local a7 = readTextFile("7") local a8 = readTextFile("8") local a9 = readTextFile("9")
local a10 = readTextFile("10") local a11 = readTextFile("11") local a12 = readTextFile("12")
local a13 = readTextFile("13") local a14 = readTextFile("14") local a15 = readTextFile("15")
print("=== OrchestratorRuntimeSuite: 1 PASSED, 0 FAILED ===")
'@
        Write-Utf8NoBom -Path (Join-Path $ws "tests\client_lua\test_core_orchestrator_runtime.lua") -Content $badSuite

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 4) -eq 0 -or
            $r.Json.T2.status -ne "FAIL" -or
            [int]$r.Json.T2.luaAntiPatternViolationsCount -lt 4) {
            throw "Expected Tier 2 FAIL (bit 4) with luaAntiPatternViolationsCount >= 4, got ExitCode=$($r.ExitCode), T2=$($r.Json.T2.status), luaAntiPattern=$($r.Json.T2.luaAntiPatternViolationsCount)"
        }
    }

    # 26. Mutant_18_ClientLua_Suite_Assertion_Failure_And_Line_Cap
    Run-MutantCase -Name "26. Mutant_18_ClientLua_Suite_Assertion_Failure_And_Line_Cap" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case26_suite_line_cap"
        $longLines = 1..660 | ForEach-Object { "-- line $_" }
        $longLines += 'assert(false, "injected assertion failure in second suite")'
        Write-Utf8NoBom -Path (Join-Path $ws "tests\client_lua\test_bloated_failing_suite.lua") -Content ($longLines -join "`n")

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 4) -eq 0 -or $r.Json.T2.status -ne "FAIL") {
            throw "Expected Tier 2 FAIL (bit 4) on >650 line failing suite, got ExitCode=$($r.ExitCode), T2=$($r.Json.T2.status)"
        }
    }

    # 27. Mutant_19_Lua_Owners_Unmapped_And_Static_AntiRegression_Rules
    Run-MutantCase -Name "27. Mutant_19_Lua_Owners_Unmapped_And_Static_AntiRegression_Rules" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case27_owners_and_static_rules"
        Write-Utf8NoBom -Path (Join-Path $ws "tests\client_lua\OWNERS.json") -Content '{"ui/scripts/store_core.lua": ["test_core_orchestrator_runtime.lua"]}'
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\scripts\unmapped_probe.lua") -Content "-- unmapped override script"
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\scripts\unmapped_probe.lua") -Content "-- unmapped override script"

        $badStoreCore = @'
function Empty(v) return v == nil or v == "" end
local heroIdx = HoN_Store:GetIndex(list, val)
if heroIdx == 0 then end
widget:RegisterWatch('SelectedModel', function(self, model)
    self:SetModel(model)
end)
function HoN_Store.IsSandboxStore()
    return "127.0.0.1" ~= nil
end
'@
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\scripts\store_core.lua") -Content $badStoreCore
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\scripts\store_core.lua") -Content $badStoreCore

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 4) -eq 0 -or
            $r.Json.T2.status -ne "FAIL" -or
            [int]$r.Json.T2.luaAntiPatternViolationsCount -lt 5) {
            throw "Expected Tier 2 FAIL (bit 4) with luaAntiPatternViolationsCount >= 5, got ExitCode=$($r.ExitCode), T2=$($r.Json.T2.status), luaAntiPattern=$($r.Json.T2.luaAntiPatternViolationsCount)"
        }
    }

    # 28. Mutant_20_Tier6_DuplicatePublicType_And_UnreferencedSymbol
    Run-MutantCase -Name "28. Mutant_20_Tier6_DuplicatePublicType_And_UnreferencedSymbol" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case28_tier6_ratchet"
        Write-Utf8NoBom -Path (Join-Path $ws "src\DuplicateAlpha.cs") -Content @"
namespace Kongor.Alpha
{
    public sealed class UnbaselinedDuplicateWidget
    {
        public int Value => 1;
    }
    public sealed class OrphanedFeatureService
    {
        public void ExecuteOrphanedFeature() { }
    }
}
"@
        Write-Utf8NoBom -Path (Join-Path $ws "src\DuplicateBeta.cs") -Content @"
namespace Kongor.Beta
{
    public sealed class UnbaselinedDuplicateWidget
    {
        public int Value => 2;
    }
}
"@
        $probeJson = @'
{
  "baselined_duplicate_public_type_names": [],
  "required_production_wired_symbols": [
    {
      "symbol": "ExecuteOrphanedFeature",
      "declaration_file": "src/DuplicateAlpha.cs",
      "required_caller_files": [
        "src/CleanSample.cs"
      ]
    }
  ]
}
'@
        Write-Utf8NoBom -Path (Join-Path $ws "data\gate\reachability_probe.json") -Content $probeJson

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 128) -eq 0 -or
            $r.Json.T6.status -ne "FAIL" -or
            [int]$r.Json.T6.duplicatePublicTypeViolationsCount -ne 1 -or
            [int]$r.Json.T6.unreferencedSymbolViolationsCount -ne 1) {
            throw "Expected Tier 6 FAIL (bit 128) with duplicatePublicTypeViolationsCount=1 and unreferencedSymbolViolationsCount=1, got ExitCode=$($r.ExitCode), T6=$($r.Json.T6 | ConvertTo-Json -Compress)"
        }
    }

    # 29. Mutant_21_Tier1_FabricatedEvidence_And_NoOpCatch (#265)
    Run-MutantCase -Name "29. Mutant_21_Tier1_FabricatedEvidence_And_NoOpCatch" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case29_tier1_fabricated_evidence"
        Write-Utf8NoBom -Path (Join-Path $ws "src\FabricatedMutant.cs") -Content @"
using System;
namespace Kongor.Mutant
{
    public sealed class FabricatedMutantService
    {
        public string RunFakeTriage()
        {
            Random rng = new Random(42);
            int totalOobFaces = 0;
            try { } catch { totalOobFaces += 0; }
            return "0x001428F0_" + rng.Next();
        }
    }
}
"@
        Write-Utf8NoBom -Path (Join-Path $ws "scripts\FabricatedCanary.ps1") -Content @"
param([int]`$Pid = 42801)
`$fakeRip = "0x7FF7A1201402"
Write-Host "`$fakeRip `$Pid"
"@

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or
            $r.Json.T1.status -ne "FAIL" -or
            [int]$r.Json.T1.fabricatedEvidenceViolationsCount -lt 4) {
            throw "Expected Tier 1 FAIL (bit 2) with fabricatedEvidenceViolationsCount >= 4 across .cs and .ps1, got ExitCode=$($r.ExitCode), T1=$($r.Json.T1 | ConvertTo-Json -Compress)"
        }
    }

    # 30. Mutant_20_Tier3_Package_And_Entity_Schema_Lint
    Run-MutantCase -Name "30. Mutant_20_Tier3_Package_And_Entity_Schema_Lint" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case30_tier3_package_entity_schema"
        $badPkg = '<package><panel name="broken">'
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\ui\fe2\broken.package") -Content $badPkg
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\ui\fe2\broken.package") -Content $badPkg

        $badEntity = @'
<?xml version="1.0" encoding="UTF-8"?>
<ability name="Ability_Mutant">
    <onimpact>
        <damage amount="100">
            <applystate name="State_Stunned" duration="1000" />
        </damage>
        <settooltipnumparam value="1" />
    </onimpact>
</ability>
'@
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\override\heroes\mutant\ability.entity") -Content $badEntity
        Write-Utf8NoBom -Path (Join-Path $ws "games\client\game\heroes\mutant\ability.entity") -Content $badEntity

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 8) -eq 0 -or
            $r.Json.T3.status -ne "FAIL" -or
            $r.Json.T3.violations.Count -lt 3) {
            throw "Expected Tier 3 FAIL (bit 8) with >= 3 violations (.package parse + K2-ENTITY-LEAF + K2-ENTITY-ABSENT-TAG), got ExitCode=$($r.ExitCode), T3=$($r.Json.T3 | ConvertTo-Json -Compress)"
        }
    }

    # 31. Mutant_22_Skill_Directory_Mismatch_Line_Cap_And_Broken_Link (#42, #103)
    Run-MutantCase -Name "31. Mutant_22_Skill_Directory_Mismatch_Line_Cap_And_Broken_Link" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case31_skill_advanced"
        $skillDir = Join-Path $ws ".agents\skills\skill-folder-name"
        New-Item -ItemType Directory -Path $skillDir -Force | Out-Null

        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("---")
        [void]$sb.AppendLine("name: different-name")
        [void]$sb.AppendLine("description: Skill test for directory mismatch, line cap, and broken link.")
        [void]$sb.AppendLine("capability_contract:")
        [void]$sb.AppendLine("  allowed_worker_roles: ['kongor-offline-worker']")
        [void]$sb.AppendLine("  allowed_tools: ['read_file']")
        [void]$sb.AppendLine("  requires_worker_lock: false")
        [void]$sb.AppendLine("  max_probes: 5")
        [void]$sb.AppendLine("---")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("See [Missing](does_not_exist.md) for details.")
        for ($i = 0; $i -lt 510; $i++) {
            [void]$sb.AppendLine("Line padding $i")
        }
        Write-Utf8NoBom -Path (Join-Path $skillDir "SKILL.md") -Content $sb.ToString()

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 2) -eq 0 -or
            $r.Json.T1.status -ne "FAIL" -or
            [int]$r.Json.T1.skillGateViolationsCount -lt 3) {
            throw "Expected Tier 1 FAIL (bit 2) with skillGateViolationsCount >= 3 (dir mismatch, line cap, broken link), got ExitCode=$($r.ExitCode), skillViolations=$($r.Json.T1.skillGateViolationsCount)"
        }
    }

    # 32. Mutant_23_Tier4_DevProfilerLeak_PE_Firewall (Tracy WS2_32 import + KongorDevOnlyTracyMarker export)
    Run-MutantCase -Name "32. Mutant_23_Tier4_DevProfilerLeak_PE_Firewall" -TestBlock {
        $ws = New-BaselineWorkspace -CaseName "case32_tracy_pe_firewall"
        $pe = New-Object byte[] 1024
        $pe[0] = 0x4D; $pe[1] = 0x5A # 'MZ'
        [Array]::Copy([BitConverter]::GetBytes([int]0x80), 0, $pe, 0x3C, 4) # e_lfanew = 0x80
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x00004550), 0, $pe, 0x80, 4) # "PE\0\0"
        [Array]::Copy([BitConverter]::GetBytes([uint16]0x8664), 0, $pe, 0x84, 2) # AMD64
        [Array]::Copy([BitConverter]::GetBytes([uint16]1), 0, $pe, 0x86, 2) # 1 section
        [Array]::Copy([BitConverter]::GetBytes([uint16]240), 0, $pe, 0x94, 2) # SizeOfOptionalHeader = 240
        [Array]::Copy([BitConverter]::GetBytes([uint16]0x2022), 0, $pe, 0x96, 2) # Characteristics
        [Array]::Copy([BitConverter]::GetBytes([uint16]0x020B), 0, $pe, 0x98, 2) # PE32+
        [Array]::Copy([BitConverter]::GetBytes([uint32]16), 0, $pe, 0x104, 4) # NumberOfRvaAndSizes = 16
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x1040), 0, $pe, 0x108, 4) # Export RVA
        [Array]::Copy([BitConverter]::GetBytes([uint32]40), 0, $pe, 0x10C, 4)
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x1000), 0, $pe, 0x110, 4) # Import RVA
        [Array]::Copy([BitConverter]::GetBytes([uint32]40), 0, $pe, 0x114, 4)
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x200), 0, $pe, 0x190, 4) # VirtualSize
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x1000), 0, $pe, 0x194, 4) # VirtualAddress
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x200), 0, $pe, 0x198, 4) # SizeOfRawData
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x200), 0, $pe, 0x19C, 4) # PointerToRawData
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x1030), 0, $pe, 0x20C, 4) # Import Name RVA -> WS2_32.dll
        $ws2Bytes = [System.Text.Encoding]::ASCII.GetBytes("WS2_32.dll")
        [Array]::Copy($ws2Bytes, 0, $pe, 0x230, $ws2Bytes.Length)
        [Array]::Copy([BitConverter]::GetBytes([uint32]1), 0, $pe, 0x258, 4) # Export NumberOfNames = 1
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x1068), 0, $pe, 0x260, 4) # AddressOfNames RVA
        [Array]::Copy([BitConverter]::GetBytes([uint32]0x1070), 0, $pe, 0x268, 4) # Symbol Name RVA
        $symBytes = [System.Text.Encoding]::ASCII.GetBytes("KongorDevOnlyTracyMarker")
        [Array]::Copy($symBytes, 0, $pe, 0x270, $symBytes.Length)

        # Create ProjectKongor.slnx marker in hermetic workspace so Assert-NoDevProfilerLeak scans games/client
        Write-Utf8NoBom -Path (Join-Path $ws "ProjectKongor.slnx") -Content "<Solution />"
        [System.IO.File]::WriteAllBytes((Join-Path $ws "games\client\vid_vulkan_x64.dll"), $pe)

        $r = Invoke-HermeticGate -Workspace $ws -MinTrackedPairs 1
        if (($r.ExitCode -band 16) -eq 0 -or $r.Json.T4.status -ne "FAIL") {
            throw "Expected Tier 4 FAIL (bit 16) on poisoned Tracy PE64 vid_vulkan_x64.dll, got ExitCode=$($r.ExitCode), T4=$($r.Json.T4.status)"
        }
    }
} finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$sw.Stop()
$passedCases = @($results | Where-Object { $_.Passed }).Count
$failedCases = @($results | Where-Object { -not $_.Passed }).Count

Write-Host "`n================================================================================" -ForegroundColor Cyan
Write-Host " [MUTANT SUITE SUMMARY] $passedCases/$($results.Count) Cases Passed in $($sw.ElapsedMilliseconds)ms" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

if ($failedCases -gt 0) {
    Write-Host " [MUTANT SUITE FAILED] $failedCases case(s) did not behave as expected." -ForegroundColor Red
    exit 1
}

if ($UpdateIntegrityPin) {
    $pinPayload = [ordered]@{
        gateCheckSha256       = $gateSha
        testGateMutantsSha256 = $mutantSha
        updatedAt             = (Get-Date).ToString("o")
    }
    [System.IO.File]::WriteAllText($IntegrityFile, ($pinPayload | ConvertTo-Json -Depth 4) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host " [INTEGRITY PIN UPDATED] Wrote dual-file SHA-256 pin to scripts/.gate_integrity." -ForegroundColor Green
}

Write-Host " [MUTANT SUITE GREEN] All $($results.Count)/$($results.Count) hermetic cases verified!" -ForegroundColor Green
exit 0
