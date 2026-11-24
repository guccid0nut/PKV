# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ============================================================================
# PROJECT KONGOR: CLIENT LUA QUALITY GATE & STATIC VERIFICATION SCRIPT
# Validates Lua 5.1 bytecode syntax across all modular definitions, catalogs,
# and runs the headless NLua (Lua 5.4) SafeCall and runtime test suite.
# ============================================================================

[CmdletBinding()]
param(
    [string]$Path = $null,
    [switch]$VerboseOutput = $false
)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = $PSScriptRoot | Split-Path -Parent

$luacCandidates = @(
    (Join-Path $repoRoot "native/build/luac51_build/Release/luac51.exe"),
    (Join-Path $repoRoot "native/tools/luac51/build/Release/luac51.exe"),
    (Join-Path $repoRoot "artifacts/native-binaries/windows-x64/luac51.exe"),
    (Join-Path $repoRoot ".tools/bin/lua/luac51.exe")
)
$luacBin = $luacCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $luacBin) {
    $cmd = Get-Command luac51 -ErrorAction SilentlyContinue
    if ($cmd) { $luacBin = $cmd.Source }
}
if (-not $luacBin -or -not (Test-Path -LiteralPath $luacBin)) {
    Write-Error "luac51 compiler not found. Build it with 'cmake --build native/build --config Release --target luac51' or stage at '.tools/bin/lua/luac51.exe'."
    exit 1
}

# satisfies: #97 (manimgx-self-healing-api-hints)
function Get-KongorLuaSelfHealingHint {
    param(
        [string]$ErrorText,
        [string]$SourcePath = ""
    )
    $hints = New-Object System.Collections.Generic.List[string]
    if ($ErrorText -match "unexpected symbol near '&'|unexpected symbol near '\|'|unexpected symbol near '~'") {
        $hints.Add("K2 uses Lua 5.1 (not Lua 5.3+); bitwise operators (&, |, ~, <<, >>) are invalid syntax. Use bit.* or math arithmetic instead.")
    }
    if ($ErrorText -match "goto|::") {
        $hints.Add("K2 uses Lua 5.1; 'goto' and '::label::' are Lua 5.2+ features and fail luac51 bytecode compilation.")
    }
    if ($ErrorText -match "expected near 'continue'") {
        $hints.Add("Lua 5.1 does not support 'continue' in loops; use a guard condition or repeat ... until true with break.")
    }
    if (-not [string]::IsNullOrWhiteSpace($SourcePath) -and (Test-Path -LiteralPath $SourcePath)) {
        $rawSrc = Get-Content -LiteralPath $SourcePath -Raw -ErrorAction SilentlyContinue
        if ($rawSrc -match '\bGetWidget\s*\(') {
            $hints.Add("Self-Healing API Hint (#97): Global GetWidget(...) is deprecated in modular NewUI; prefer interface:GetWidget(...) orKongorSafeCall.GetWidget(...).")
        }
        if ($rawSrc -match '\bloadfile\s*\(') {
            $hints.Add("Self-Healing API Hint (#97): Avoid per-frame loadfile(...) polling (causes luaV_concat OOM at k2_x64.dll+0xCB6477); use CVar triggers (tao_req) instead.")
        }
    }
    if ($hints.Count -eq 0) {
        $hints.Add("Run: .\scripts\verify_client_lua.ps1 -Path `"$SourcePath`" to re-verify after fixing syntax.")
    }
    return ($hints -join " | ")
}

if (-not [string]::IsNullOrWhiteSpace($Path)) {
    if (-not (Test-Path $Path)) {
        Write-Host " [FAIL] Specified Lua path does not exist: $Path" -ForegroundColor Red
        exit 1
    }
    $resolvedPath = (Resolve-Path $Path).Path
    $output = & $luacBin -p $resolvedPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        $err = $output -join "`n"
        $hint = Get-KongorLuaSelfHealingHint -ErrorText $err -SourcePath $resolvedPath
        Write-Host " [FAIL] $Path : $err" -ForegroundColor Red
        Write-Host " [SELF-HEALING HINT] $hint" -ForegroundColor Yellow
        exit 1
    } else {
        $elapsed = $sw.ElapsedMilliseconds
        Write-Host " [PASS] $Path passed Lua 5.1 bytecode compilation (0 errors) in ${elapsed}ms!" -ForegroundColor Green
        exit 0
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR CLIENT LUA QUALITY GATE] Starting Comprehensive Client Verification" -ForegroundColor Cyan
Write-Host " Root: $repoRoot" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

$verifierBin = Join-Path $repoRoot "src/tools/lua-verifier/bin/Release/net11.0/LuaVerifier.exe"
if (-not (Test-Path $verifierBin)) {
    $verifierBin = Join-Path $repoRoot "src/tools/lua-verifier/bin/Debug/net11.0/LuaVerifier.exe"
}

# 1. Gather all client script files to validate
$targetDirs = @(
    "games/client/ClientPackageFiles/ui/scripts",
    "games/client/ClientPackageFiles/ui/scripts/nexus",
    "games/client/ClientPackageFiles/ui/scripts/newui",
    "games/client/ClientPackageFiles/ui/scripts/newui/definitions/heroes",
    "games/client/ClientPackageFiles/ui/scripts/newui/definitions/items",
    "games/client/ClientPackageFiles/ui/scripts/newui/definitions/cosmetics",
    "games/client/ClientPackageFiles/ui/fe2/scripts",
    "games/client/override/ui/scripts",
    "games/client/override/ui/scripts/nexus",
    "games/client/override/ui/scripts/newui",
    "games/client/override/ui/scripts/newui/definitions/heroes",
    "games/client/override/ui/scripts/newui/definitions/items",
    "games/client/override/ui/scripts/newui/definitions/cosmetics",
    "games/client/override/ui/fe2/scripts"
)

$filePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$allFiles = @()
foreach ($dir in $targetDirs) {
    $fullDir = Join-Path $repoRoot $dir
    if (Test-Path $fullDir) {
        $files = Get-ChildItem -Path $fullDir -Filter "*.lua"
        foreach ($f in $files) {
            if ($filePaths.Add($f.FullName)) {
                $allFiles += $f
            }
        }
    }
}

Write-Host "`n--- [STAGE 1] Bytecode Syntax Validation with luac51 (Total files: $($allFiles.Count)) ---" -ForegroundColor Yellow

$syntaxFailures = @()
$checkedCount = 0

foreach ($file in $allFiles) {
    $checkedCount++
    $output = & $luacBin -p $file.FullName 2>&1
    
    if ($LASTEXITCODE -ne 0) {
        $err = $output -join "`n"
        $hint = Get-KongorLuaSelfHealingHint -ErrorText $err -SourcePath $file.FullName
        Write-Host " [FAIL] $($file.Name): $err" -ForegroundColor Red
        Write-Host " [SELF-HEALING HINT] $hint" -ForegroundColor Yellow
        $syntaxFailures += @{ File = $file.Name; Error = $err; Hint = $hint }
    } elseif ($VerboseOutput) {
        Write-Host " [OK] $($file.Name)" -ForegroundColor Green
    }
}

if ($syntaxFailures.Count -gt 0) {
    Write-Host "`n[ERROR] $($syntaxFailures.Count) files failed bytecode validation!" -ForegroundColor Red
    exit 1
} else {
    Write-Host " [PASS] All $checkedCount files passed Lua 5.1 bytecode compilation (0 errors)!" -ForegroundColor Green
}

# 1B. Validate XML <template> + <instance> *lua="..." expansions with real luac51 (Lua 5.1)
# Closes the NLua (Lua 5.4) vs. K2 (Lua 5.1) blind spot where empty statements (';;') pass Lua 5.4 but fail K2!
Write-Host "`n--- [STAGE 1B] XML Template Instance Lua 5.1 Bytecode Check (luac51) ---" -ForegroundColor Yellow
$optionsPkgPath = Join-Path $repoRoot "games/client/override/ui/fe2/game_options.package"
if (Test-Path -LiteralPath $optionsPkgPath) {
    $xmlDoc = [xml](Get-Content -LiteralPath $optionsPkgPath -Raw)
    $templateMap = @{}
    foreach ($tmplNode in $xmlDoc.SelectNodes('//template')) {
        $tName = $tmplNode.GetAttribute('name')
        if ($tName) { $templateMap[$tName] = $tmplNode }
    }

    $batchBuilder = [System.Text.StringBuilder]::new()
    $expandedItems = [System.Collections.Generic.List[object]]::new()

    foreach ($instNode in $xmlDoc.SelectNodes('//instance')) {
        $refName = $instNode.GetAttribute('name')
        if (-not $refName -or -not $templateMap.ContainsKey($refName)) { continue }
        $instArgs = @{}
        foreach ($ia in $instNode.Attributes) {
            if ($ia.Name -ne 'name') { $instArgs[$ia.Name] = $ia.Value }
        }
        $tmpl = $templateMap[$refName]
        foreach ($desc in $tmpl.SelectNodes('.//*')) {
            foreach ($ta in $desc.Attributes) {
                if (-not ($ta.Name -match 'lua$') -or [string]::IsNullOrWhiteSpace($ta.Value)) { continue }
                # Skip nested inner <instance> pass-through attributes (e.g. button2 onclicklua="{...}")
                if ($desc.LocalName -eq 'instance') { continue }
                $expanded = [regex]::Replace($ta.Value, '\{([^}=]+)(?:=([^}]*))?\}', {
                    param($m)
                    $k = $m.Groups[1].Value
                    if ($instArgs.ContainsKey($k)) { return $instArgs[$k] }
                    if ($m.Groups[2].Success) { return $m.Groups[2].Value }
                    return ''
                })
                if ([string]::IsNullOrWhiteSpace($expanded)) { continue }
                $idx = $expandedItems.Count + 1
                $label = "$refName(cvar=$($instArgs['cvar'])).$($ta.Name)"
                $expandedItems.Add([PSCustomObject]@{ Index = $idx; Label = $label; Code = $expanded })
                [void]$batchBuilder.AppendLine("function _xml_lua_$idx(self, param0, param1, param2, param3)")
                [void]$batchBuilder.AppendLine($expanded)
                [void]$batchBuilder.AppendLine("end")
            }
        }
    }

    $tmpBatch = [System.IO.Path]::ChangeExtension([System.IO.Path]::GetTempFileName(), ".lua")
    try {
        [System.IO.File]::WriteAllText($tmpBatch, $batchBuilder.ToString(), [System.Text.UTF8Encoding]::new($false))
        $batchOut = & $luacBin -p $tmpBatch 2>&1
        if ($LASTEXITCODE -ne 0) {
            $errStr = $batchOut -join "`n"
            $failLine = 0
            if ($errStr -match '\.lua:(\d+):') { $failLine = [int]$Matches[1] }
            $failIdx = [Math]::Max(1, [int][Math]::Ceiling($failLine / 3.0))
            $culprit = if ($failIdx -le $expandedItems.Count) { $expandedItems[$failIdx - 1] } else { $null }
            if ($culprit) {
                Write-Host " [FAIL] game_options.package $($culprit.Label): $errStr | Expanded: $($culprit.Code)" -ForegroundColor Red
            } else {
                Write-Host " [FAIL] game_options.package XML template expansion: $errStr" -ForegroundColor Red
            }
            Write-Host " [SELF-HEALING HINT] K2 uses Lua 5.1, which rejects empty statements ('; ;'). Wrap optional template callbacks in 'do {onclicklua} end' instead of '{onclicklua};', then re-run .\scripts\verify_client_lua.ps1" -ForegroundColor Yellow
            exit 1
        } else {
            Write-Host " [PASS] All $($expandedItems.Count) expanded game_options.package *lua instance chunks passed Lua 5.1 bytecode compilation!" -ForegroundColor Green
        }
    } finally {
        Remove-Item -LiteralPath $tmpBatch -Force -ErrorAction SilentlyContinue
    }
}

# 2. Run Headless NLua Runtime Simulation Tests
if (Test-Path $verifierBin) {
    Write-Host "`n--- [STAGE 2] Headless NLua Runtime Simulation (Lua 5.4 Sandbox) ---" -ForegroundColor Yellow

    $testScripts = @(
        Get-ChildItem -Path (Join-Path $repoRoot "tests/client_lua") -Filter "test_*.lua" -File |
            Sort-Object Name |
            Select-Object -ExpandProperty FullName
    )

    if ($testScripts.Count -gt 0) {
        Write-Host " Running $($testScripts.Count) client Lua domain suites via LuaVerifier.exe..." -ForegroundColor Gray
        & $verifierBin @testScripts
        if ($LASTEXITCODE -ne 0) {
            Write-Host " [FAIL] Client Lua domain suite execution failed (ExitCode: $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        } else {
            Write-Host " [PASS] All $($testScripts.Count) client Lua domain suites passed cleanly!" -ForegroundColor Green
        }
    }
} else {
    Write-Host "`n[SKIP] LuaVerifier.exe not found, skipping stage 2 sandbox test." -ForegroundColor DarkYellow
}

$sw.Stop()
Write-Host "`n================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR CLIENT LUA QUALITY GATE] ALL CHECKS PASSED in $($sw.Elapsed.TotalSeconds.ToString('0.00'))s!" -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Cyan
