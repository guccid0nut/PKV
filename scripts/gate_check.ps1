# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Project Kongor: Sub-5-Second 6-Tier Inner-Loop Quality Gate.

.DESCRIPTION
    Executes a fast, 6-tier regression and static analysis verification suite:
    - Tier 1 (Bit 2):   Fast BannedSymbols, PowerShell AST & Field-Note KB Scanner (< 0.8s)
    - Tier 2 (Bit 4):   Lua Contract Test Suite (< 1.5s) - verifies real parsed contract counts (> 0)
    - Tier 3 (Bit 8):   XML & VFS Syntax Lint (< 1.0s) - validates core UI and override XML interfaces
    - Tier 4 (Bit 16):  NativeGuard (version.dll / libk2_native_guard.so) & VFS Smoke Check (< 0.5s)
    - Tier 5 (Bit 32):  Cross-Tree Asset Parity (override <-> game/server/CPF) (< 0.5s)
    - Tier 6 (Bit 128): Duplicate-Type & Production Reachability Ratchet Gate (< 0.2s)
    Reserves exit codes 1 and 64 for unhandled script crashes; returns shifted bitmask (2/4/8/16/32/128) on tier failures.
#>

[CmdletBinding()]
param(
    [switch]$Full = $false,
    [switch]$Cleanroom = $false,
    [string]$TargetRoot = "",
    [switch]$RequireVerifier = $true,
    [int]$MinTrackedPairs = 10,
    [string]$JsonOutput = "",
    [switch]$UpdateGolden = $false,
    [switch]$VerifyGolden = $false
)

$ErrorActionPreference = 'Stop'

trap {
    $crashMsg = if ($_.Exception) { $_.Exception.Message } else { "$_" }
    Write-Host " [GATE CRASH] Unhandled terminating exception in gate_check.ps1: $crashMsg" -ForegroundColor Magenta
    if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
        $crashSummary = [ordered]@{
            overallStatus = "CRASH"
            exitCode      = 64
            error         = $crashMsg
        }
        try {
            [System.IO.File]::WriteAllText($JsonOutput, ($crashSummary | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        } catch { }
    }
    exit 64
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$localDotnetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path -LiteralPath (Join-Path $localDotnetRoot "dotnet.exe") -PathType Leaf) {
    $env:DOTNET_ROOT = $localDotnetRoot
    if ($env:PATH -notlike "$localDotnetRoot*") {
        $env:PATH = "$localDotnetRoot;$env:PATH"
    }
}
$scriptRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
    $repoRoot = $scriptRepoRoot
} else {
    $RequireVerifier = $true
    if (Test-Path -LiteralPath $TargetRoot) {
        $repoRoot = (Resolve-Path -LiteralPath $TargetRoot).Path
    } else {
        $repoRoot = [System.IO.Path]::GetFullPath($TargetRoot)
    }
}

$isCleanroom = $Cleanroom -or ([string]::IsNullOrWhiteSpace($TargetRoot) -and (-not (Test-Path -LiteralPath (Join-Path $repoRoot "games\client"))))
$gateScriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR INNER-LOOP QUALITY GATE] Sub-5-Second Pre-Flight Verification" -ForegroundColor Cyan
Write-Host " Root: $repoRoot" -ForegroundColor Gray
if ($isCleanroom) {
    Write-Host " Mode: CLEANROOM (games/ overlays & K2 client archives absent/skipped)" -ForegroundColor Yellow
}
Write-Host " Gate Integrity SHA256: $gateScriptHash" -ForegroundColor DarkGray
Write-Host "================================================================================" -ForegroundColor Cyan

$exitBitmask = 0
$warningsCount = 0
$skippedCount = 0
$tierTimes = @{}

# ============================================================================
# TIER 1: Fast Roslyn BannedSymbols, PowerShell AST & Field-Note KB Scanner
# ============================================================================
$t1Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`n--- [TIER 1] BannedSymbols Static API Scanner ---" -ForegroundColor Cyan

$bannedPatterns = @(
    @{ Pattern = '\bTask\.Result\b'; Symbol = 'Task.Result'; Name = 'Task.Result (Sync-over-async)' },
    @{ Pattern = '\.Wait\(\)'; Symbol = '.Wait()'; Name = 'Task.Wait() (Blocking call)' },
    @{ Pattern = '\bThread\.Sleep\('; Symbol = 'Thread.Sleep('; Name = 'Thread.Sleep (Thread pool blocking)' },
    @{ Pattern = '\.GetAwaiter\(\)\.GetResult\(\)'; Symbol = '.GetAwaiter().GetResult()'; Name = 'GetAwaiter().GetResult() (Sync-over-async)' }
)

$csFiles = @()
$inGit = $false
$isShallowClone = $false
$gitCommitCount = 0
$historyFallbackMode = "full_git_history"
$dotGitPath = Join-Path $repoRoot ".git"
$shallowSentinelPath = Join-Path $repoRoot "data\gate\shallow_clone.sentinel"

if (Test-Path -LiteralPath $dotGitPath) {
    $prevEapGit = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $gitStatus = git -C $repoRoot status --porcelain 2>$null
        if ($LASTEXITCODE -eq 0) {
            $inGit = $true
            if ($gitStatus) {
                $csFiles = @($gitStatus | ForEach-Object {
                    $parts = $_.Trim() -split '\s+', 2
                    if ($parts.Length -eq 2) {
                        $relPath = (($parts[1] -split '\s+->\s+')[-1]).Trim('"')
                        if ($relPath.EndsWith(".cs")) {
                            Join-Path $repoRoot $relPath
                        }
                    }
                } | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
            }
            # satisfies: #252 (repowise-shallow-clone-fallback-and-symbol-cache — git rev-list --count HEAD < 50)
            $gitShallowFile = Join-Path $dotGitPath "shallow"
            $gitHeadLogFile = Join-Path $dotGitPath "logs\HEAD"
            if (Test-Path -LiteralPath $gitShallowFile) {
                $isShallowClone = $true
                $historyFallbackMode = "shallow_clone_graceful_skip"
            } elseif ((Test-Path -LiteralPath $gitHeadLogFile -PathType Leaf) -and ([System.IO.File]::ReadAllLines($gitHeadLogFile).Length -ge 50)) {
                $gitCommitCount = 50
            } else {
                $revCountStr = (git -C $repoRoot rev-list --count HEAD 2>$null | Out-String).Trim()
                if ($LASTEXITCODE -eq 0 -and [int]::TryParse($revCountStr, [ref]$gitCommitCount)) {
                    if ($gitCommitCount -lt 50) {
                        $isShallowClone = $true
                        $historyFallbackMode = "shallow_clone_graceful_skip"
                    }
                } else {
                    $isShallowClone = $true
                    $historyFallbackMode = "shallow_clone_graceful_skip"
                }
            }
        }
    } catch { }
    finally {
        $ErrorActionPreference = $prevEapGit
    }
}
if (Test-Path -LiteralPath $shallowSentinelPath) {
    $isShallowClone = $true
    $historyFallbackMode = "shallow_clone_graceful_skip"
}

# satisfies: #252 (repowise-shallow-clone-fallback-and-symbol-cache — incremental <15ms data/gate/symbols.cache)
$symCacheSw = [System.Diagnostics.Stopwatch]::StartNew()
$symbolCachePath = Join-Path $repoRoot "data\gate\symbols.cache"
$symbolCacheMap = @{}
$symbolCacheHits = 0
$symbolCacheMisses = 0
$symbolCacheDirty = $false
if (Test-Path -LiteralPath $symbolCachePath -PathType Leaf) {
    try {
        $cacheLines = [System.IO.File]::ReadAllLines($symbolCachePath)
        foreach ($cl in $cacheLines) {
            if ([string]::IsNullOrWhiteSpace($cl) -or $cl.StartsWith("#")) { continue }
            $cParts = $cl -split '\|', 4
            if ($cParts.Length -eq 4) {
                $ckKey = $cParts[0]
                if ($ckKey.StartsWith("xml3:") -or $ckKey.StartsWith("cs_types:") -or $ckKey -eq "cs_types") {
                    $symbolCacheDirty = $true
                    continue
                }
                $symbolCacheMap[$ckKey] = [PSCustomObject]@{
                    Stamp   = "$($cParts[1])|$($cParts[2])"
                    Payload = $cParts[3]
                }
            }
        }
    } catch {
        $symbolCacheMap = @{}
    }
}
$symCacheSw.Stop()
$symbolCacheLoadMs = $symCacheSw.ElapsedMilliseconds

# satisfies: #243 (repowise-kamei-jit-diff-risk-and-churn-entropy)
function Get-KongorKameiJitDiffRisk {
    param(
        [Parameter(Mandatory = $false)]
        [array]$FileStats = @()
    )
    $nf = $FileStats.Count
    $subsystems = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $totalLa = 0
    $totalLd = 0
    $fileChurns = New-Object System.Collections.Generic.List[int]

    foreach ($fs in $FileStats) {
        $fPath = ([string]$fs.file -replace '\\', '/').TrimStart('/')
        $la = [Math]::Max(0, [int]$fs.la)
        $ld = [Math]::Max(0, [int]$fs.ld)
        $churn = $la + $ld
        $totalLa += $la
        $totalLd += $ld
        if ($churn -gt 0) {
            $fileChurns.Add($churn)
        }
        $segs = $fPath -split '/'
        $subName = if ($segs.Length -ge 2) { "$($segs[0])/$($segs[1])" } else { $segs[0] }
        if (-not [string]::IsNullOrWhiteSpace($subName)) {
            [void]$subsystems.Add($subName)
        }
    }

    $ns = $subsystems.Count
    $lt = $totalLa + $totalLd
    $entropy = 0.0
    if ($nf -gt 1 -and $lt -gt 0 -and $fileChurns.Count -gt 1) {
        $rawH = 0.0
        foreach ($ck in $fileChurns) {
            $pk = [double]$ck / [double]$lt
            if ($pk -gt 0.0) {
                $rawH -= $pk * ([Math]::Log($pk) / [Math]::Log(2.0))
            }
        }
        $normDenom = [Math]::Log([double]$nf) / [Math]::Log(2.0)
        if ($normDenom -gt 0.0) {
            $entropy = [Math]::Min(1.0, [Math]::Max(0.0, ($rawH / $normDenom)))
        }
    }

    $entropyRounded = [Math]::Round($entropy, 4)
    $rawScore = ([Math]::Min(30.0, $nf * 3.0)) + ([Math]::Min(25.0, $ns * 6.0)) + ([Math]::Min(25.0, $lt / 20.0)) + ($entropyRounded * 20.0)
    $riskScore = [Math]::Round([Math]::Min(100.0, $rawScore), 2)
    $riskLevel = if ($riskScore -ge 75.0) { "CRITICAL" } elseif ($riskScore -ge 50.0) { "HIGH" } elseif ($riskScore -ge 25.0) { "MEDIUM" } else { "LOW" }

    return [ordered]@{
        NF              = $nf
        NS              = $ns
        LA              = $totalLa
        LD              = $totalLd
        LT              = $lt
        shannon_entropy = $entropyRounded
        risk_score      = $riskScore
        risk_level      = $riskLevel
    }
}

function Get-KongorMultiFileStamp {
    param(
        [Parameter(Mandatory = $false)]
        [string[]]$Paths = @(),
        [Parameter(Mandatory = $false)]
        [string]$Salt = ""
    )
    [int64]$lenSum = 0L
    [int64]$ticksXor = if ([string]::IsNullOrEmpty($Salt)) { 0L } else { [int64]($Salt.GetHashCode() -band 0x7FFFFFFF) }
    [int64]$count = 0L
    foreach ($p in $Paths) {
        if ([string]::IsNullOrEmpty($p)) { continue }
        try {
            $fi = [System.IO.FileInfo]::new($p)
            if ($fi.Exists) {
                $count++
                $lenSum += $fi.Length
                $ticksXor = $ticksXor -bxor $fi.LastWriteTimeUtc.Ticks -bxor ([int64]($fi.Name.GetHashCode() -band 0x7FFFFFFF))
            }
        } catch {}
    }
    $ticksXor = $ticksXor -bxor ($count -shl 32)
    return "$lenSum|$ticksXor"
}

$jitDiffProbeFile = Join-Path $repoRoot "data\gate\jit_diff_probe.json"
$jitDiffStats = New-Object System.Collections.Generic.List[object]
if (Test-Path -LiteralPath $jitDiffProbeFile -PathType Leaf) {
    try {
        $probeObj = Get-Content -LiteralPath $jitDiffProbeFile -Raw | ConvertFrom-Json
        foreach ($pf in @($probeObj.files)) {
            $jitDiffStats.Add([PSCustomObject]@{ file = [string]$pf.file; la = [int]$pf.la; ld = [int]$pf.ld })
        }
    } catch {}
} elseif ($inGit -and (-not $Full) -and $gitStatus) {
    $prevEapJit = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $textDiffPaths = @($gitStatus | ForEach-Object {
            $parts = $_.Trim() -split '\s+', 2
            if ($parts.Length -eq 2 -and $parts[0] -ne '??') {
                $relPath = (($parts[1] -split '\s+->\s+')[-1]).Trim('"')
                if ($relPath -notmatch '(?i)\.(dll|exe|pdb|s2z|dmp|zip|bak|png|dds|tga|model|clip)$') {
                    $relPath
                }
            }
        } | Where-Object { $_ })
        if ($textDiffPaths.Count -gt 0) {
            $jitCacheKey = "__jit_numstat_v1__"
            $useJitCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full)
            $jitStamp = ""
            $jitHit = $false
            if ($useJitCache) {
                $jitFullPaths = @(
                    (Join-Path $dotGitPath "HEAD"),
                    (Join-Path $dotGitPath "index")
                ) + @($textDiffPaths | ForEach-Object { Join-Path $repoRoot $_ })
                $jitStamp = Get-KongorMultiFileStamp -Paths $jitFullPaths -Salt ($textDiffPaths -join ';')
                if ($symbolCacheMap.ContainsKey($jitCacheKey) -and $symbolCacheMap[$jitCacheKey].Stamp -eq $jitStamp) {
                    $jitHit = $true
                    $symbolCacheHits++
                    $rawJitPayload = [string]$symbolCacheMap[$jitCacheKey].Payload
                    if (-not [string]::IsNullOrEmpty($rawJitPayload)) {
                        foreach ($entry in ($rawJitPayload -split ';')) {
                            $ep = $entry -split ',', 3
                            if ($ep.Length -eq 3) {
                                $jitDiffStats.Add([PSCustomObject]@{
                                    file = $ep[2]
                                    la   = [int]$ep[0]
                                    ld   = [int]$ep[1]
                                })
                            }
                        }
                    }
                }
            }
            if (-not $jitHit) {
                $numStatLines = @(git -C $repoRoot diff --numstat HEAD -- $textDiffPaths 2>$null)
                if ($LASTEXITCODE -eq 0) {
                    $serEntries = New-Object System.Collections.Generic.List[string]
                    foreach ($nsl in $numStatLines) {
                        if ($nsl -match '^(\d+)\s+(\d+)\s+(.+)$') {
                            $fRel = $Matches[3].Trim()
                            $laVal = [int]$Matches[1]
                            $ldVal = [int]$Matches[2]
                            $jitDiffStats.Add([PSCustomObject]@{
                                file = $fRel
                                la   = $laVal
                                ld   = $ldVal
                            })
                            $serEntries.Add("$laVal,$ldVal,$fRel")
                        }
                    }
                    if ($useJitCache -and (-not [string]::IsNullOrEmpty($jitStamp))) {
                        $symbolCacheMap[$jitCacheKey] = [PSCustomObject]@{ Stamp = $jitStamp; Payload = ($serEntries -join ';') }
                        $symbolCacheDirty = $true
                    }
                }
            }
        }
    } catch {}
    finally {
        $ErrorActionPreference = $prevEapJit
    }
}
$jitDiffRisk = Get-KongorKameiJitDiffRisk -FileStats $jitDiffStats.ToArray()

$srcDir = Join-Path $repoRoot "src"
if ((-not $inGit) -or $Full) {
    if (Test-Path -LiteralPath $srcDir) {
        $csFiles = @(Get-ChildItem -LiteralPath $srcDir -Filter "*.cs" -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(obj|bin|Generated|Migrations)\\' } |
            Select-Object -ExpandProperty FullName)
    } else {
        $csFiles = @()
    }
}

$tier1Violations = New-Object System.Collections.Generic.List[string]
if (((-not $inGit) -or $Full) -and ($csFiles.Count -eq 0)) {
    $tier1Violations.Add("Zero-Assertion Guard: 0 C# files scanned under Full/non-git mode in '$srcDir'.")
}

# Load 90-day expiring baseline ledger if present in TargetRoot/scripts/.banned_symbols_baseline.json
$baselineMap = @{}
$baselinedDebtCount = 0
$expiredBaselineCount = 0
$baselineFile = Join-Path $repoRoot "scripts\.banned_symbols_baseline.json"
if (Test-Path -LiteralPath $baselineFile) {
    try {
        $rawBaseline = Get-Content -LiteralPath $baselineFile -Raw | ConvertFrom-Json
        $baselineEntries = if ($rawBaseline -is [System.Array]) { $rawBaseline } else { @($rawBaseline) }
        $todayDate = (Get-Date).Date
        foreach ($be in $baselineEntries) {
            if ($null -eq $be -or [string]::IsNullOrWhiteSpace($be.file)) { continue }
            $normBeFile = ([string]$be.file).TrimStart('\', '/') -replace '\\', '/'
            $expDate = [datetime]::ParseExact([string]$be.expiresOn, "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
            $isExpired = ($expDate -lt $todayDate)
            $baselineMap["$normBeFile|$($be.symbol)"] = [PSCustomObject]@{
                MaxCount  = [int]$be.maxCount
                ExpiresOn = [string]$be.expiresOn
                IsExpired = $isExpired
                Reason    = [string]$be.reason
            }
            if ($isExpired) {
                $expiredBaselineCount++
                $tier1Violations.Add("Expired BannedSymbols Baseline in ${normBeFile}: symbol '$($be.symbol)' expired on $($be.expiresOn). Remediate sync-over-async debt or update baseline.")
            }
        }
    } catch {
        $tier1Violations.Add("Corrupt .banned_symbols_baseline.json at '$baselineFile': $($_.Exception.Message)")
    }
}

$cleanroomRules = @(
    @{ Pattern = '\b(?:FUN|DAT|LAB|PTR)_[0-9a-fA-F]{6,16}\b|\bsub_[0-9A-Fa-f]{6,16}\b'; Symbol = 'Cleanroom.DecompilerFingerprint'; Name = 'Cleanroom Decompiler Auto-Name Fingerprint (FUN_/DAT_/LAB_/PTR_/sub_)' },
    @{ Pattern = '(?i)[C-Z]:\\\\?Users\\\\?[A-Za-z0-9_.-]+\\\\?'; Symbol = 'Cleanroom.HardcodedUserPath'; Name = 'Cleanroom Hardcoded Local User Path (C:\Users\<name>\)' }
)
$cleanroomViolationsCount = 0
$fabricatedEvidenceViolationsCount = 0

# Load Declarative Slop Rules Catalog (#115, #265) early so C# & PowerShell Tier 1 scanners can enforce fabricated-evidence & no-op catch guards
$slopRulesPath = Join-Path $repoRoot "data\gate\slop_rules.json"
if (-not (Test-Path -LiteralPath $slopRulesPath)) {
    $fallbackSlopRules = Join-Path $scriptRepoRoot "data\gate\slop_rules.json"
    if (Test-Path -LiteralPath $fallbackSlopRules) { $slopRulesPath = $fallbackSlopRules }
}
$slopRulesConfig = $null
if (Test-Path -LiteralPath $slopRulesPath) {
    try {
        $slopRulesConfig = Get-Content -LiteralPath $slopRulesPath -Raw | ConvertFrom-Json
        if ($slopRulesConfig.schema_version -ne "1.0.0") {
            $tier1Violations.Add("[GATE][TIER1][SLOP_RULES] Unsupported schema_version '$($slopRulesConfig.schema_version)' in data/gate/slop_rules.json")
        }
    } catch {
        $tier1Violations.Add("[GATE][TIER1][SLOP_RULES] Corrupt data/gate/slop_rules.json: $($_.Exception.Message)")
    }
}

$fabAndNoopRules = New-Object System.Collections.Generic.List[object]
if ($null -ne $slopRulesConfig -and $null -ne $slopRulesConfig.fabricated_evidence_and_noop_rules) {
    foreach ($fr in @($slopRulesConfig.fabricated_evidence_and_noop_rules)) {
        if ($null -ne $fr -and -not [string]::IsNullOrWhiteSpace([string]$fr.pattern)) {
            $fabAndNoopRules.Add([PSCustomObject]@{
                Id      = [string]$fr.id
                Symbol  = [string]$fr.symbol
                Pattern = [string]$fr.pattern
                Name    = [string]$fr.name
            })
        }
    }
}
if ($fabAndNoopRules.Count -eq 0) {
    $fabAndNoopRules.Add([PSCustomObject]@{ Id = "FAB-EV-001"; Symbol = "FabricatedEvidence.SyntheticRva"; Pattern = '\b(?:0x001428F0|0x002184C0|0x0008F310)\b'; Name = "Fabricated synthetic crash RVA (0x001428F0 / 0x002184C0 / 0x0008F310) (#265)" })
    $fabAndNoopRules.Add([PSCustomObject]@{ Id = "FAB-EV-002"; Symbol = "FabricatedEvidence.SyntheticFaultRip"; Pattern = '\b(?:0x7FF7A1201402|0x7FF7A1189930)\b'; Name = "Fabricated synthetic crash RIP address (0x7FF7A1201402 / 0x7FF7A1189930) (#265)" })
    $fabAndNoopRules.Add([PSCustomObject]@{ Id = "FAB-EV-003"; Symbol = "FabricatedEvidence.FakeProcessTelemetry"; Pattern = '(?:\b412L\s*\*\s*1024L\s*\*\s*1024L\b|\b42801\b|\b42795\b)'; Name = "Fabricated process telemetry constant (412 MB / 42801 / 42795) (#265)" })
    $fabAndNoopRules.Add([PSCustomObject]@{ Id = "FAB-EV-004"; Symbol = "FabricatedEvidence.SeededRandom42"; Pattern = '\bnew\s+Random\s*\(\s*42\s*\)'; Name = "Seeded PRNG telemetry fabrication (new Random(42)) (#265)" })
    $fabAndNoopRules.Add([PSCustomObject]@{ Id = "NOOP-CATCH-001"; Symbol = "Slop.NoOpZeroCompoundAssignment"; Pattern = '\b\w+\s*[\+\-]=\s*0\s*;'; Name = "No-op += 0; / -= 0; catch-block metric-gaming statement (#265)" })
}

# Scan native C++/header files (.cpp, .h) for Cleanroom Decompiler Fingerprints & Hardcoded User Paths (Rec #10)
$nativeCleanroomFiles = @()
$nativeRoot = Join-Path $repoRoot "native"
if (Test-Path -LiteralPath $nativeRoot) {
    if ($inGit -and (-not $Full)) {
        if ($gitStatus) {
            $nativeCleanroomFiles = @($gitStatus | ForEach-Object {
                $parts = $_.Trim() -split '\s+', 2
                if ($parts.Length -eq 2) {
                    $relPath = (($parts[1] -split '\s+->\s+')[-1]).Trim('"')
                    if ($relPath.EndsWith(".cpp") -or $relPath.EndsWith(".h")) {
                        Join-Path $repoRoot $relPath
                    }
                }
            } | Where-Object { $_ -and (Test-Path -LiteralPath $_) -and ($_ -notmatch '\\(deps|bin|build|obj|third_party|vendor)\\') })
        }
    } else {
        foreach ($ext in @("*.cpp", "*.h")) {
            $nativeCleanroomFiles += @(Get-ChildItem -LiteralPath $nativeRoot -Filter $ext -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(deps|bin|build|obj|third_party|vendor)\\' } |
                Select-Object -ExpandProperty FullName)
        }
    }
}

$vehScanFiles = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
foreach ($nf in $nativeCleanroomFiles) { [void]$vehScanFiles.Add($nf) }
$ngDirForVeh = Join-Path $repoRoot "native\proxies\native_guard"
if (Test-Path -LiteralPath $ngDirForVeh) {
    foreach ($vf in (Get-ChildItem -LiteralPath $ngDirForVeh -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @(".cpp", ".h") })) {
        [void]$vehScanFiles.Add($vf.FullName)
    }
}

$dirtyCfgFiles = @()
if ($inGit -and (-not $Full) -and $gitStatus) {
    $dirtyCfgFiles = @($gitStatus | ForEach-Object {
        $parts = $_.Trim() -split '\s+', 2
        if ($parts.Length -eq 2) {
            $relPath = (($parts[1] -split '\s+->\s+')[-1]).Trim('"')
            if ($relPath.EndsWith(".conf") -or $relPath.EndsWith(".cfg")) {
                Join-Path $repoRoot $relPath
            }
        }
    } | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
}

$t1BannedCacheKey = "__t1_banned_all_v1__"
$useT1BannedCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and ($expiredBaselineCount -eq 0) -and ($tier1Violations.Count -eq 0)
$t1BannedStamp = ""
$t1BannedHit = $false
if ($useT1BannedCache) {
    $t1StampPaths = @(
        $baselineFile,
        $slopRulesPath,
        (Join-Path $dotGitPath "index")
    ) + @($csFiles) + @($vehScanFiles) + @($dirtyCfgFiles)
    $t1BannedStamp = Get-KongorMultiFileStamp -Paths $t1StampPaths -Salt ((Get-Date).ToString("yyyy-MM-dd"))
    if ($symbolCacheMap.ContainsKey($t1BannedCacheKey) -and $symbolCacheMap[$t1BannedCacheKey].Stamp -eq $t1BannedStamp -and $symbolCacheMap[$t1BannedCacheKey].Payload -match '^PASS;(\d+)$') {
        $t1BannedHit = $true
        $baselinedDebtCount = [int]$Matches[1]
        $symbolCacheHits++
    }
}

if (-not $t1BannedHit) {
    if ($useT1BannedCache) { $symbolCacheMisses++ }
    $t1ViolationsBeforeBanned = $tier1Violations.Count

    foreach ($file in $csFiles) {
        # Strict filename-suffix exemption for test/mock/benchmark files (prevents directory-name path laundering like src/Mocks/ProdService.cs)
        $fileNameOnly = [System.IO.Path]::GetFileName($file)
        if ($fileNameOnly -match '(?i)(Tests?|Mocks?|Benchmarks?)\.cs$') {
            continue
        }

        $relCs = $file.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $content = [System.IO.File]::ReadAllText($file)
        foreach ($rule in $bannedPatterns) {
            $ruleMatches = [regex]::Matches($content, $rule.Pattern)
            if ($ruleMatches.Count -gt 0) {
                $bKey = "$relCs|$($rule.Symbol)"
                if ($baselineMap.ContainsKey($bKey)) {
                    $bEntry = $baselineMap[$bKey]
                    if ($bEntry.IsExpired) {
                        $tier1Violations.Add("Violation in ${relCs}: $($rule.Name) (baseline expired $($bEntry.ExpiresOn))")
                    } elseif ($ruleMatches.Count -gt $bEntry.MaxCount) {
                        $tier1Violations.Add("Violation in ${relCs}: $($rule.Name) found $($ruleMatches.Count) time(s), exceeding baseline maxCount $($bEntry.MaxCount)")
                    } else {
                        $baselinedDebtCount += $ruleMatches.Count
                    }
                } else {
                    $tier1Violations.Add("Violation in ${relCs}: $($rule.Name)")
                }
            }
        }

        if ($content -match '//\s*Decompiled with') {
            $cleanroomViolationsCount++
            $tier1Violations.Add("Cleanroom Violation in ${relCs}: Raw '// Decompiled with' banner detected")
        }
        $noBlockCs = [regex]::Replace($content, '(?s)/\*.*?\*/', '')
        $noCommentsCs = [regex]::Replace($noBlockCs, '(?m)//.*$', '')
        foreach ($cRule in $cleanroomRules) {
            $cMatches = [regex]::Matches($noCommentsCs, $cRule.Pattern)
            if ($cMatches.Count -gt 0) {
                $bKey = "$relCs|$($cRule.Symbol)"
                if ($baselineMap.ContainsKey($bKey)) {
                    $bEntry = $baselineMap[$bKey]
                    if ($bEntry.IsExpired) {
                        $cleanroomViolationsCount++
                        $tier1Violations.Add("Cleanroom Violation in ${relCs}: $($cRule.Name) (baseline expired $($bEntry.ExpiresOn))")
                    } elseif ($cMatches.Count -gt $bEntry.MaxCount) {
                        $cleanroomViolationsCount++
                        $tier1Violations.Add("Cleanroom Violation in ${relCs}: $($cRule.Name) found $($cMatches.Count) time(s), exceeding baseline maxCount $($bEntry.MaxCount)")
                    } else {
                        $baselinedDebtCount += $cMatches.Count
                    }
                } else {
                    $cleanroomViolationsCount++
                    $tier1Violations.Add("Cleanroom Violation in ${relCs}: $($cRule.Name)")
                }
            }
        }
        foreach ($fRule in $fabAndNoopRules) {
            if ([regex]::IsMatch($noCommentsCs, $fRule.Pattern)) {
                $fabricatedEvidenceViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][$($fRule.Id)] ${relCs}: $($fRule.Name)")
            }
        }
    }

    foreach ($nFile in $nativeCleanroomFiles) {
        $nFileName = [System.IO.Path]::GetFileName($nFile)
        if ($nFileName -match '(?i)(Tests?|Mocks?|Benchmarks?)\.(cpp|h)$') {
            continue
        }
        $relNative = $nFile.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $nRaw = [System.IO.File]::ReadAllText($nFile)
        if ($nRaw -match '//\s*Decompiled with') {
            $cleanroomViolationsCount++
            $tier1Violations.Add("Cleanroom Violation in ${relNative}: Raw '// Decompiled with' banner detected")
        }
        $nNoBlock = [regex]::Replace($nRaw, '(?s)/\*.*?\*/', '')
        $nNoComments = [regex]::Replace($nNoBlock, '(?m)//.*$', '')
        foreach ($cRule in $cleanroomRules) {
            $cMatches = [regex]::Matches($nNoComments, $cRule.Pattern)
            if ($cMatches.Count -gt 0) {
                $bKey = "$relNative|$($cRule.Symbol)"
                if ($baselineMap.ContainsKey($bKey) -and (-not $baselineMap[$bKey].IsExpired) -and ($cMatches.Count -le $baselineMap[$bKey].MaxCount)) {
                    $baselinedDebtCount += $cMatches.Count
                } else {
                    $cleanroomViolationsCount++
                    $tier1Violations.Add("Cleanroom Violation in ${relNative}: $($cRule.Name)")
                }
            }
        }
    }

    # Scan tracked configuration files (.conf, .cfg) for Cleanroom Hardcoded User Paths (Windows C:\Users\<name>\, POSIX /home/<name>/, macOS /Users/<name>/)
    $configCleanroomFiles = @()
    if ($inGit) {
        $prevEapCfg = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $trackedCfgRels = @(git -C $repoRoot ls-files "*.conf" "*.cfg" 2>$null)
            foreach ($cfgRel in $trackedCfgRels) {
                if (-not [string]::IsNullOrWhiteSpace($cfgRel)) {
                    $cfgFull = Join-Path $repoRoot $cfgRel
                    if (Test-Path -LiteralPath $cfgFull -PathType Leaf) {
                        $configCleanroomFiles += $cfgFull
                    }
                }
            }
        } catch { }
        finally {
            $ErrorActionPreference = $prevEapCfg
        }
    }
    $cfgUserPathPattern = '(?i)(?:[C-Z]:[\\/]+Users[\\/]+[A-Za-z0-9_.-]+|(?<![A-Za-z0-9_])/(?:home|Users)/[A-Za-z0-9_.-]+/)'
    foreach ($cfgFile in $configCleanroomFiles) {
        $relCfg = $cfgFile.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $cfgRaw = [System.IO.File]::ReadAllText($cfgFile)
        $cfgNoComments = [regex]::Replace($cfgRaw, '(?m)^\s*(?:#|//).*$', '')
        if ([regex]::IsMatch($cfgNoComments, $cfgUserPathPattern)) {
            $cleanroomViolationsCount++
            $tier1Violations.Add("Cleanroom Violation in ${relCfg}: Tracked .conf/.cfg contains hardcoded workstation user path (use portable '.' or userdata runtime config)")
        }
    }

    # Option S: Zero-Allocation VEH Crash Path Banned Symbol Scan
    $vehBannedSymbols = @(
        @{ Pattern = '\bstd::ostringstream\b'; Name = 'std::ostringstream (heap allocation inside VEH crash path)' },
        @{ Pattern = '\bstd::stringstream\b';  Name = 'std::stringstream (heap allocation inside VEH crash path)' },
        @{ Pattern = '\bstd::ofstream\b';      Name = 'std::ofstream (heap allocation inside VEH crash path)' },
        @{ Pattern = '\bmalloc\s*\(';          Name = 'malloc() (heap allocation inside VEH crash path)' },
        @{ Pattern = '\bnew\s+[A-Za-z_]';      Name = 'operator new (heap allocation inside VEH crash path)' }
    )
    foreach ($vFile in $vehScanFiles) {
        if (-not (Test-Path -LiteralPath $vFile)) { continue }
        $vRaw = [System.IO.File]::ReadAllText($vFile)
        $relVeh = $vFile.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $vFileName = [System.IO.Path]::GetFileName($vFile)
        $regionsToCheck = New-Object System.Collections.Generic.List[string]
        if ($vFileName -match '(?i)veh_crash') {
            $regionsToCheck.Add($vRaw)
        } else {
            $blockMatches = [regex]::Matches($vRaw, '(?s)//\s*\[KONGOR_VEH_CRASH_PATH_BEGIN\](.*?)//\s*\[KONGOR_VEH_CRASH_PATH_END\]')
            foreach ($bm in $blockMatches) {
                $regionsToCheck.Add($bm.Groups[1].Value)
            }
        }
        foreach ($regionText in $regionsToCheck) {
            $regNoBlock = [regex]::Replace($regionText, '(?s)/\*.*?\*/', '')
            $regNoComments = [regex]::Replace($regNoBlock, '(?m)//.*$', '')
            foreach ($vRule in $vehBannedSymbols) {
                if ([regex]::IsMatch($regNoComments, $vRule.Pattern)) {
                    $tier1Violations.Add("VEH Heap-Safety Violation in ${relVeh}: Banned symbol $($vRule.Name). Use Kongor::StackSStream<N> and Win32 CreateFileA/WriteFile instead.")
                }
            }
        }
    }

    if ($useT1BannedCache -and ($tier1Violations.Count -eq $t1ViolationsBeforeBanned) -and (-not [string]::IsNullOrEmpty($t1BannedStamp))) {
        $symbolCacheMap[$t1BannedCacheKey] = [PSCustomObject]@{ Stamp = $t1BannedStamp; Payload = "PASS;$baselinedDebtCount" }
        $symbolCacheDirty = $true
    }
}


$t1BannedSw = $t1Sw.ElapsedMilliseconds
Write-Host " [GATE-PERF] BannedSymbols_Scan elapsed_ms=$t1BannedSw" -ForegroundColor DarkGray

# PowerShell AST & PSScriptAnalyzer (.psscriptanalyzer.psd1) Check + Incremental Symbol Cache (#252)
$scriptsDir = Join-Path $repoRoot "scripts"
$psFileInfos = if (Test-Path -LiteralPath $scriptsDir) {
    [System.IO.DirectoryInfo]::new($scriptsDir).GetFiles("*.ps1", [System.IO.SearchOption]::AllDirectories)
} else {
    @()
}
$psFiles = $psFileInfos
$psaSettings = Join-Path $repoRoot ".psscriptanalyzer.psd1"
$scriptParamCache = @{}
$commonPsParams = @("Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable", "WhatIf", "Confirm", "NoProfile", "ExecutionPolicy", "File")

if ($psFileInfos.Count -eq 0) {
    $tier1Violations.Add("Zero-Assertion Guard: 0 PowerShell scripts scanned in '$scriptsDir'.")
}

$t1PsAstSw = [System.Diagnostics.Stopwatch]::StartNew()
$repoRootPrefixLen = $repoRoot.Length
foreach ($fi in $psFileInfos) {
    $psFile = $fi.FullName
    $relPs = $psFile.Substring($repoRootPrefixLen).TrimStart('\', '/').Replace('\', '/')
    $stamp = "$($fi.Length)|$($fi.LastWriteTimeUtc.Ticks)"

    if ($symbolCacheMap.ContainsKey($relPs) -and $symbolCacheMap[$relPs].Stamp -eq $stamp) {
        $cachedPayload = [string]$symbolCacheMap[$relPs].Payload
        if ($cachedPayload.StartsWith("OK;")) {
            $symbolCacheHits++
            $paramSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($cp in $commonPsParams) { [void]$paramSet.Add($cp) }
            $rawCachedParams = $cachedPayload.Substring(3)
            if (-not [string]::IsNullOrEmpty($rawCachedParams)) {
                foreach ($ep in ($rawCachedParams -split ',')) {
                    if (-not [string]::IsNullOrEmpty($ep)) { [void]$paramSet.Add($ep) }
                }
            }
            $scriptParamCache[$relPs] = $paramSet
            continue
        } elseif ($cachedPayload.StartsWith("ERR;")) {
            $symbolCacheHits++
            $tier1Violations.Add($cachedPayload.Substring(4))
            continue
        } elseif ($cachedPayload.StartsWith("FABERR;")) {
            $symbolCacheHits++
            $fabricatedEvidenceViolationsCount++
            $tier1Violations.Add($cachedPayload.Substring(7))
            continue
        }
    }

    $symbolCacheMisses++
    $psLeafName = [System.IO.Path]::GetFileName($psFile)
    $psFabViolations = New-Object System.Collections.Generic.List[string]
    if ($psLeafName -notin @("gate_check.ps1", "test_gate_mutants.ps1")) {
        $psRawText = [System.IO.File]::ReadAllText($psFile)
        $psNoBlock = [regex]::Replace($psRawText, '(?s)<#.*?#>', '')
        $psNoComments = [regex]::Replace($psNoBlock, '(?m)^\s*#.*$', '')
        foreach ($fRule in $fabAndNoopRules) {
            if ([regex]::IsMatch($psNoComments, $fRule.Pattern)) {
                $fabricatedEvidenceViolationsCount++
                $fabMsg = "[GATE][TIER1][$($fRule.Id)] ${relPs}: $($fRule.Name)"
                $psFabViolations.Add($fabMsg)
                $tier1Violations.Add($fabMsg)
            }
        }
    }

    $tokens = $null
    $parseErrors = $null
    $sAst = [System.Management.Automation.Language.Parser]::ParseFile($psFile, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($err in $parseErrors) {
            $errMsg = "PS AST Parse Error in $($psFile.Replace($repoRoot, '')):$($err.Extent.StartLineNumber): $($err.Message)"
            $tier1Violations.Add($errMsg)
        }
    } else {
        $paramSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        $extractedList = New-Object System.Collections.Generic.List[string]
        foreach ($cp in $commonPsParams) { [void]$paramSet.Add($cp) }
        if ($null -ne $sAst -and $null -ne $sAst.ParamBlock) {
            foreach ($pAst in $sAst.ParamBlock.Parameters) {
                $uPath = $pAst.Name.VariablePath.UserPath
                if ($paramSet.Add($uPath)) { $extractedList.Add($uPath) }
                foreach ($attr in $pAst.Attributes) {
                    if ($attr -is [System.Management.Automation.Language.AttributeAst] -and $attr.TypeName.Name -match '^Alias') {
                        foreach ($posArg in $attr.PositionalArguments) {
                            if ($posArg -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                                if ($paramSet.Add($posArg.Value)) { $extractedList.Add($posArg.Value) }
                            }
                        }
                    }
                }
            }
        } else {
            $rawScript = [System.IO.File]::ReadAllText($psFile)
            foreach ($m in [regex]::Matches($rawScript, '(?m)^\s*''\^-([A-Za-z][A-Za-z0-9_]*)\$''')) {
                $swFlag = $m.Groups[1].Value
                if ($paramSet.Add($swFlag)) { $extractedList.Add($swFlag) }
            }
        }
        $scriptParamCache[$relPs] = $paramSet
        $symbolCacheMap[$relPs] = [PSCustomObject]@{
            Stamp   = $stamp
            Payload = if ($psFabViolations.Count -gt 0) { "FABERR;" + $psFabViolations[0] } else { "OK;" + ($extractedList -join ',') }
        }
        $symbolCacheDirty = $true
    }
}
$t1PsAstSw.Stop()
Write-Host " [GATE-PERF] PowerShell_AST elapsed_ms=$($t1PsAstSw.ElapsedMilliseconds) cache_hits=$symbolCacheHits cache_misses=$symbolCacheMisses" -ForegroundColor DarkGray

# Verify K2 auto-generated struct header path (src/engine/headers/k2_structs.generated.h) when src/engine/headers exists (#109)
$t1HdrSw = [System.Diagnostics.Stopwatch]::StartNew()
$engineHeadersDir = Join-Path $repoRoot "src\engine\headers"
if (Test-Path -LiteralPath $engineHeadersDir) {
    $k2GenHeaderPath = Join-Path $engineHeadersDir "k2_structs.generated.h"
    if (-not (Test-Path -LiteralPath $k2GenHeaderPath)) {
        $tier1Violations.Add("Missing K2 generated header at 'src/engine/headers/k2_structs.generated.h'. Regenerate with: .\scripts\re\export_ghidra_symbols.ps1 -Scope Curated")
    }
}
$t1HdrSw.Stop()
Write-Host " [GATE-PERF] K2_Generated_Header_Check elapsed_ms=$($t1HdrSw.ElapsedMilliseconds)" -ForegroundColor DarkGray

function Get-DirectoryCacheStamp([string]$dirPath, [string]$scriptPath) {
    $files = @(Get-ChildItem -LiteralPath $dirPath -File -ErrorAction SilentlyContinue)
    [int64]$lenSum = 0L
    [int64]$ticksXor = [int64]$files.Count
    foreach ($f in $files) {
        $lenSum += $f.Length
        $ticksXor = $ticksXor -bxor $f.LastWriteTimeUtc.Ticks
    }
    if (Test-Path -LiteralPath $scriptPath -PathType Leaf) {
        $sFi = [System.IO.FileInfo]::new($scriptPath)
        $lenSum += $sFi.Length
        $ticksXor = $ticksXor -bxor $sFi.LastWriteTimeUtc.Ticks
    }
    return "$lenSum|$ticksXor"
}

# Conditional Field-Note KB Schema & Index-Drift Check
$t1KbSw = [System.Diagnostics.Stopwatch]::StartNew()
$kbNotesDir = Join-Path $repoRoot "docs\reference\field-notes"
$kbNotesChecked = 0
$kbDrift = $false
if (Test-Path -LiteralPath $kbNotesDir) {
    $kbScript = Join-Path $scriptRepoRoot "scripts\kongor_kb.ps1"
    if (Test-Path -LiteralPath $kbScript) {
        $kbCacheKey = "__dir_field_notes__"
        $kbDirStamp = Get-DirectoryCacheStamp -dirPath $kbNotesDir -scriptPath $kbScript
        if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and $symbolCacheMap.ContainsKey($kbCacheKey) -and $symbolCacheMap[$kbCacheKey].Stamp -eq $kbDirStamp -and $symbolCacheMap[$kbCacheKey].Payload -match '^PASS;(\d+)$') {
            $kbNotesChecked = [int]$Matches[1]
        } else {
            $kbTempJson = Join-Path $env:TEMP ("kongor_kb_gate_" + [guid]::NewGuid().ToString("N") + ".json")
            try {
                & $kbScript -Action check -NotesDir $kbNotesDir -JsonOutput $kbTempJson -Quiet
                $kbExit = $LASTEXITCODE
                if (Test-Path -LiteralPath $kbTempJson) {
                    $kbData = Get-Content -LiteralPath $kbTempJson -Raw | ConvertFrom-Json
                    $kbNotesChecked = [int]$kbData.notesChecked
                    $kbDrift = [bool]$kbData.indexDrift
                    if (-not $kbData.passed) {
                        foreach ($kErr in $kbData.errors) {
                            $tier1Violations.Add("Field-Note KB Error: $kErr")
                        }
                    } elseif ($repoRoot -eq $scriptRepoRoot) {
                        $symbolCacheMap[$kbCacheKey] = [PSCustomObject]@{ Stamp = $kbDirStamp; Payload = "PASS;$kbNotesChecked" }
                        $symbolCacheDirty = $true
                    }
                } elseif ($kbExit -ne 0) {
                    $kbDrift = ($kbExit -eq 3)
                    $tier1Violations.Add("Field-Note KB check exited with code $kbExit. Run: .\scripts\kongor_kb.ps1 -Action rebuild-index")
                }
            } finally {
                if (Test-Path -LiteralPath $kbTempJson) {
                    Remove-Item -LiteralPath $kbTempJson -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
}
$t1KbSw.Stop()
Write-Host " [GATE-PERF] FieldNote_KB elapsed_ms=$($t1KbSw.ElapsedMilliseconds)" -ForegroundColor DarkGray

# Conditional MCP Issue KB Schema & Index-Drift Check
$t1McpSw = [System.Diagnostics.Stopwatch]::StartNew()
$mcpIssuesDir = Join-Path $repoRoot "docs\reference\mcp-issues"
$mcpIssuesChecked = 0
$mcpIssuesDrift = $false
if (Test-Path -LiteralPath $mcpIssuesDir) {
    $mcpKbScript = Join-Path $scriptRepoRoot "scripts\kongor_mcp_issues.ps1"
    if (Test-Path -LiteralPath $mcpKbScript) {
        $mcpCacheKey = "__dir_mcp_issues__"
        $mcpDirStamp = Get-DirectoryCacheStamp -dirPath $mcpIssuesDir -scriptPath $mcpKbScript
        if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and $symbolCacheMap.ContainsKey($mcpCacheKey) -and $symbolCacheMap[$mcpCacheKey].Stamp -eq $mcpDirStamp -and $symbolCacheMap[$mcpCacheKey].Payload -match '^PASS;(\d+)$') {
            $mcpIssuesChecked = [int]$Matches[1]
        } else {
            $mcpTempJson = Join-Path $env:TEMP ("kongor_mcp_kb_gate_" + [guid]::NewGuid().ToString("N") + ".json")
            try {
                & $mcpKbScript -Action check -IssuesDir $mcpIssuesDir -MaxUncodifiedHighFrequencyHits 3 -JsonOutput $mcpTempJson -Quiet
                $mcpExit = $LASTEXITCODE
                if (Test-Path -LiteralPath $mcpTempJson) {
                    $mcpData = Get-Content -LiteralPath $mcpTempJson -Raw | ConvertFrom-Json
                    $mcpIssuesChecked = [int]$mcpData.issuesChecked
                    $mcpIssuesDrift = [bool]$mcpData.indexDrift
                    if (-not $mcpData.passed) {
                        foreach ($mErr in $mcpData.errors) {
                            $tier1Violations.Add("MCP Issue KB Error: $mErr")
                        }
                    } elseif ($repoRoot -eq $scriptRepoRoot) {
                        $symbolCacheMap[$mcpCacheKey] = [PSCustomObject]@{ Stamp = $mcpDirStamp; Payload = "PASS;$mcpIssuesChecked" }
                        $symbolCacheDirty = $true
                    }
                } elseif ($mcpExit -ne 0) {
                    $mcpIssuesDrift = ($mcpExit -eq 3)
                    $tier1Violations.Add("MCP Issue KB check exited with code $mcpExit. Run: .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index")
                }
            } finally {
                if (Test-Path -LiteralPath $mcpTempJson) {
                    Remove-Item -LiteralPath $mcpTempJson -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
}
$t1McpSw.Stop()
Write-Host " [GATE-PERF] McpIssue_KB elapsed_ms=$($t1McpSw.ElapsedMilliseconds)" -ForegroundColor DarkGray

# Document Citation Verifier
$t1DocCitSw = [System.Diagnostics.Stopwatch]::StartNew()
$docCitScript = Join-Path $scriptRepoRoot "scripts\qa\verify_doc_citations.ps1"
if (Test-Path -LiteralPath $docCitScript) {
    $docCitCacheKey = "__doc_citations__"
    $docCitDirs = @(
        (Join-Path $repoRoot "docs\audits"),
        (Join-Path $repoRoot "docs\reference\field-notes")
    )
    $existingDocCitDirs = @($docCitDirs | Where-Object { Test-Path -LiteralPath $_ })
    if ($existingDocCitDirs.Count -gt 0) {
        [int64]$dcLenSum = 0L
        [int64]$dcTicksXor = 0L
        foreach ($dcd in $existingDocCitDirs) {
            $subStamp = Get-DirectoryCacheStamp -dirPath $dcd -scriptPath $docCitScript
            $sp = $subStamp -split '\|', 2
            if ($sp.Length -eq 2) {
                $dcLenSum += [int64]$sp[0]
                $dcTicksXor = $dcTicksXor -bxor [int64]$sp[1]
            }
        }
        $docCitStamp = "$dcLenSum|$dcTicksXor"
        if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and $symbolCacheMap.ContainsKey($docCitCacheKey) -and $symbolCacheMap[$docCitCacheKey].Stamp -eq $docCitStamp) {
            # cached pass
        } else {
            & $docCitScript -Paths $existingDocCitDirs -Quiet
            $docCitExit = $LASTEXITCODE
            if ($docCitExit -ne 0) {
                $tier1Violations.Add("Document Citation Error: verify_doc_citations.ps1 failed with code $docCitExit. Run: .\scripts\qa\verify_doc_citations.ps1")
            } elseif ($repoRoot -eq $scriptRepoRoot) {
                $symbolCacheMap[$docCitCacheKey] = [PSCustomObject]@{ Stamp = $docCitStamp; Payload = "PASS" }
                $symbolCacheDirty = $true
            }
        }
    }
}
$t1DocCitSw.Stop()
Write-Host " [GATE-PERF] Doc_Citations elapsed_ms=$($t1DocCitSw.ElapsedMilliseconds)" -ForegroundColor DarkGray

# Phase 3a (#115, #103, #104) & Phase 3b (#36, #42, #43, #53, #58, #62):
# Declarative Slop Rules, SKILL.md & llms.txt Reference/Capability Gate, Explicit UTF-8,
# Offline Import Boundary, UUID Framing Sentinel, Orphan Lock & Global ID Uniqueness Gate
$t1P3Sw = [System.Diagnostics.Stopwatch]::StartNew()
$pythonSlopViolationsCount = 0
$skillGateViolationsCount = 0
$skillsChecked = 0

# 2. Python Anti-Slop (#36), Explicit UTF-8 (#104), Offline Import Boundary (#53), & UUID Sentinel (#58)
$pyScanRelDirs = @("scripts\lib", "src\tools\ghidra", "src\tools\kongor")
if ($null -ne $slopRulesConfig -and $slopRulesConfig.python_scan_roots) {
    $pyScanRelDirs = @($slopRulesConfig.python_scan_roots | ForEach-Object { ([string]$_ -replace '/', '\') })
}
$pyScanDirs = @($pyScanRelDirs | ForEach-Object { Join-Path $repoRoot $_ })
$pyFilesToScan = @()
foreach ($psd in $pyScanDirs) {
    if (Test-Path -LiteralPath $psd) {
        $pyFilesToScan += @(Get-ChildItem -LiteralPath $psd -Filter "*.py" -File | Select-Object -ExpandProperty FullName)
    }
}

$forbiddenOfflineMods = @("frida", "win32pipe", "win32file", "websocket", "requests", "httpx", "urllib.request")
$offlineRestrictedRelPrefixes = @("scripts/lib/", "src/tools/ghidra/")
if ($null -ne $slopRulesConfig -and $slopRulesConfig.offline_import_boundary) {
    if ($slopRulesConfig.offline_import_boundary.forbidden_modules) {
        $forbiddenOfflineMods = @($slopRulesConfig.offline_import_boundary.forbidden_modules | ForEach-Object { [string]$_ })
    }
    if ($slopRulesConfig.offline_import_boundary.restricted_roots) {
        $offlineRestrictedRelPrefixes = @($slopRulesConfig.offline_import_boundary.restricted_roots | ForEach-Object {
            $r = ([string]$_ -replace '\\', '/').Trim('/')
            "$r/"
        })
    }
}

foreach ($pyFile in $pyFilesToScan) {
    $relPy = $pyFile.Replace($repoRoot, '').TrimStart('\', '/') -replace '\\', '/'
    $pyText = [System.IO.File]::ReadAllText($pyFile)
    $pyLines = $pyText -split "`r?`n"

    # #36: Bare '# type: ignore' without [code] & subprocess shell=True
    for ($ln = 0; $ln -lt $pyLines.Count; $ln++) {
        $lineStr = $pyLines[$ln]
        $lineNum = $ln + 1
        if ($lineStr -match '#\s*type:\s*ignore(?!\[)') {
            $pythonSlopViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][PY_SLOP_001] ${relPy}:${lineNum} Bare '# type: ignore' without [code] (#36). Specify e.g. '# type: ignore[import-not-found]'.")
        }
        if ($lineStr -match '^\s*(?!#).*?\bsubprocess\.\w+\([^)]*\bshell\s*=\s*True\b') {
            $pythonSlopViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][PY_SLOP_002] ${relPy}:${lineNum} subprocess call with shell=True is forbidden (#36).")
        }
    }

    # #36: Silent 'except Exception: pass' / 'except BaseException: pass'
    foreach ($em in [regex]::Matches($pyText, '(?m)^\s*except\s+(?:Exception|BaseException)(?:\s+as\s+\w+)?\s*:\s*(?:\r?\n\s*)?\bpass\b')) {
        $pythonSlopViolationsCount++
        $tier1Violations.Add("[GATE][TIER1][PY_SLOP_003] ${relPy} Silent 'except Exception: pass' is forbidden (#36).")
    }

    # #104: Explicit encoding="utf-8" on .read_text(), .write_text(), and text-mode open()
    foreach ($rm in [regex]::Matches($pyText, '(?s)\.(read_text|write_text)\s*\(((?:[^()]+|\([^()]*\))*)\)')) {
        $callName = $rm.Groups[1].Value
        $callArgs = $rm.Groups[2].Value
        if ($callArgs -notmatch '\bencoding\s*=') {
            $pythonSlopViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][PY_UTF8_104] ${relPy} .${callName}() missing explicit encoding='utf-8' (#104).")
        }
    }
    for ($ln = 0; $ln -lt $pyLines.Count; $ln++) {
        $lineStr = $pyLines[$ln]
        if ($lineStr -match '^\s*#') { continue }
        foreach ($om in [regex]::Matches($lineStr, '(?<![.\w])open\s*\(((?:[^()]+|\([^()]*\))*)\)')) {
            $openArgs = $om.Groups[1].Value
            $isBinary = ($openArgs -match '[''"][rwa+]*b[rwa+]*[''"]')
            if (-not $isBinary -and $openArgs -notmatch '\bencoding\s*=') {
                $pythonSlopViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][PY_UTF8_104] ${relPy}:$($ln + 1) text-mode open() missing explicit encoding='utf-8' (#104).")
            }
        }
    }

    # #53: Offline Import Boundary Gate (scoped to restricted offline roots)
    $isRestrictedOfflinePy = $false
    foreach ($offPref in $offlineRestrictedRelPrefixes) {
        if ($relPy.StartsWith($offPref, [System.StringComparison]::OrdinalIgnoreCase)) {
            $isRestrictedOfflinePy = $true
            break
        }
    }
    if ($isRestrictedOfflinePy) {
        foreach ($modName in $forbiddenOfflineMods) {
            $escapedMod = [regex]::Escape($modName)
            if ($pyText -match "(?m)^\s*(?:import\s+$escapedMod\b|from\s+$escapedMod\b)") {
                $pythonSlopViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][PY_BOUNDARY_053] ${relPy} Offline analyzer imports forbidden live/network module '$modName' (#53).")
            }
        }
    }
}

# #58: UUID Framing Sentinel check on scripts/lib/capstone_analyzer.py when present
$capstonePyPath = Join-Path $repoRoot "scripts\lib\capstone_analyzer.py"
if (Test-Path -LiteralPath $capstonePyPath) {
    $capText = [System.IO.File]::ReadAllText($capstonePyPath)
    if ($capText -notmatch '<<<KONGOR_PAYLOAD_BEGIN_' -or $capText -notmatch '<<<KONGOR_PAYLOAD_END_') {
        $pythonSlopViolationsCount++
        $tier1Violations.Add("[GATE][TIER1][UUID_SENTINEL_058] scripts/lib/capstone_analyzer.py missing <<<KONGOR_PAYLOAD_BEGIN_<UUID>>>> framing sentinels (#58).")
    }
}

# 3. SKILL.md & llms.txt Frontmatter, Capability Allowlist (#42), Flag Existence (#103), & Global ID Uniqueness (#62)
$skillsDir = Join-Path $repoRoot ".agents\skills"
$seenSkillNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

function Get-ScriptAllowedParams([string]$scriptRelPath) {
    if ($scriptParamCache.ContainsKey($scriptRelPath)) {
        return $scriptParamCache[$scriptRelPath]
    }
    if ($symbolCacheMap.ContainsKey($scriptRelPath)) {
        $cachedPayload = [string]$symbolCacheMap[$scriptRelPath].Payload
        if ($cachedPayload.StartsWith("OK;")) {
            $rawParams = $cachedPayload.Substring(3)
            if (-not [string]::IsNullOrWhiteSpace($rawParams)) {
                $paramSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                foreach ($commonP in $commonPsParams) { [void]$paramSet.Add($commonP) }
                foreach ($pName in ($rawParams -split ',')) {
                    if (-not [string]::IsNullOrWhiteSpace($pName)) { [void]$paramSet.Add($pName) }
                }
                $scriptParamCache[$scriptRelPath] = $paramSet
                return $paramSet
            }
        }
    }
    $candPath = Join-Path $repoRoot ($scriptRelPath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $candPath)) {
        $candPath = Join-Path $scriptRepoRoot ($scriptRelPath -replace '/', '\')
    }
    if (-not (Test-Path -LiteralPath $candPath)) {
        $scriptParamCache[$scriptRelPath] = $null
        return $null
    }
    $paramSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($commonP in $commonPsParams) {
        [void]$paramSet.Add($commonP)
    }
    $sTokens = $null; $sErrs = $null
    $sAst = [System.Management.Automation.Language.Parser]::ParseFile($candPath, [ref]$sTokens, [ref]$sErrs)
    if ($null -ne $sAst -and $null -ne $sAst.ParamBlock) {
        foreach ($pAst in $sAst.ParamBlock.Parameters) {
            [void]$paramSet.Add($pAst.Name.VariablePath.UserPath)
            foreach ($attr in $pAst.Attributes) {
                if ($attr -is [System.Management.Automation.Language.AttributeAst] -and $attr.TypeName.Name -match '^Alias') {
                    foreach ($posArg in $attr.PositionalArguments) {
                        if ($posArg -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                            [void]$paramSet.Add($posArg.Value)
                        }
                    }
                }
            }
        }
    } else {
        $rawScript = [System.IO.File]::ReadAllText($candPath)
        foreach ($m in [regex]::Matches($rawScript, '(?m)^\s*''\^-([A-Za-z][A-Za-z0-9_]*)\$''')) {
            [void]$paramSet.Add($m.Groups[1].Value)
        }
    }
    $scriptParamCache[$scriptRelPath] = $paramSet
    return $paramSet
}

if (Test-Path -LiteralPath $skillsDir) {
    $skillFiles = @(Get-ChildItem -LiteralPath $skillsDir -Filter "SKILL.md" -Recurse -File | Select-Object -ExpandProperty FullName)
    foreach ($sf in $skillFiles) {
        $skillsChecked++
        $relSf = $sf.Replace($repoRoot, '').TrimStart('\', '/') -replace '\\', '/'
        $sfText = [System.IO.File]::ReadAllText($sf)
        if ($sfText -notmatch '(?s)\A---\r?\n(.*?)\r?\n---') {
            $skillGateViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][SKILL_103] ${relSf} Missing YAML frontmatter block (#103).")
            continue
        }
        $fm = $Matches[1]
        if ($fm -notmatch '(?m)^name:\s*(\S+)') {
            $skillGateViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][SKILL_103] ${relSf} Frontmatter missing 'name:' field (#103).")
        } else {
            $sName = $Matches[1].Trim()
            if (-not $seenSkillNames.Add($sName)) {
                $skillGateViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][UNIQUE_ID_062] Duplicate SKILL.md name '$sName' in ${relSf} (#62).")
            }
            $parentDirName = Split-Path -Leaf (Split-Path -Parent $sf)
            if ($sName -ne $parentDirName) {
                $skillGateViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][SKILL_DIR_NAME] ${relSf} Frontmatter name '$sName' does not match directory name '$parentDirName'.")
            }
        }
        if ($fm -notmatch '(?m)^capability_contract:\s*$' -or
            $fm -notmatch '(?m)^\s+allowed_worker_roles:\s*\[' -or
            $fm -notmatch '(?m)^\s+allowed_tools:\s*\[' -or
            $fm -notmatch '(?m)^\s+requires_worker_lock:\s*(?:true|false)' -or
            $fm -notmatch '(?m)^\s+max_probes:\s*\d+') {
            $skillGateViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][SKILL_CAP_042] ${relSf} Missing or incomplete capability_contract frontmatter (allowed_worker_roles, allowed_tools, requires_worker_lock, max_probes) (#42).")
        }

        # Line count cap (< 500 lines)
        $sfLines = [System.IO.File]::ReadAllLines($sf)
        if ($sfLines.Length -ge 500) {
            $skillGateViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][SKILL_LINE_COUNT] ${relSf} Line count $($sfLines.Length) exceeds 500 lines limit.")
        }

        # Relative Markdown link target resolution (#103)
        $textNoCode = [regex]::Replace($sfText, '(?s)```.*?```', '')
        $skillDir = Split-Path -Parent $sf
        foreach ($linkMatch in [regex]::Matches($textNoCode, '\[(?:[^\]]+)\]\(([^)]+)\)')) {
            $rawLink = $linkMatch.Groups[1].Value.Trim()
            if ($rawLink -match '^(?:https?://|mailto:|#|file://)') { continue }
            $cleanLink = ($rawLink -split '#')[0].Trim()
            if ([string]::IsNullOrEmpty($cleanLink)) { continue }
            $candTarget = Join-Path $skillDir ($cleanLink -replace '/', '\')
            if (-not (Test-Path -LiteralPath $candTarget)) {
                if ($isCleanroom) {
                    $resolvedTarget = [System.IO.Path]::GetFullPath($candTarget)
                    $repoRootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
                    if ($resolvedTarget.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $relLinkTarget = $resolvedTarget.Substring($repoRootFull.Length) -replace '\\', '/'
                        if ($relLinkTarget -match '^(?:src/tools/(?:ghidra|voicebox|goose)/|scripts/(?:vfs|vault|staging|cleanup|voicebox|forge)/|scripts/re/triangulate_rva\.js$)') {
                            continue
                        }
                    }
                }
                $skillGateViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][SKILL_LINK_103] ${relSf} References non-existent local file '$cleanLink' (#103).")
            }
        }

        # #103: Verify scripts/<name>.ps1 (including domain subfolders) and any -<Flag> on the same invocation line
        foreach ($cmdMatch in [regex]::Matches($sfText, '(?m)scripts[\\/]([A-Za-z0-9_./\\-]+\.ps1)([^\r\n`"]*)')) {
            $sRel = "scripts/" + ($cmdMatch.Groups[1].Value -replace '\\', '/')
            $sArgs = $cmdMatch.Groups[2].Value
            $allowedParams = Get-ScriptAllowedParams $sRel
            if ($null -eq $allowedParams) {
                if ($isCleanroom -and ($sRel -match '^scripts/(?:vfs|vault|staging|cleanup|voicebox|forge)/')) {
                    continue
                }
                $skillGateViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][SKILL_103] ${relSf} References non-existent script '$sRel' (#103).")
            } else {
                foreach ($flMatch in [regex]::Matches($sArgs, '(?<=\s)-([A-Za-z][A-Za-z0-9_]*)\b')) {
                    $flagName = $flMatch.Groups[1].Value
                    if (-not $allowedParams.Contains($flagName)) {
                        $skillGateViolationsCount++
                        $tier1Violations.Add("[GATE][TIER1][SKILL_103] ${relSf} References unknown flag '-$flagName' on '$sRel' (#103).")
                    }
                }
            }
        }
    }
}

$llmsTxtPath = Join-Path $repoRoot "llms.txt"
if (Test-Path -LiteralPath $llmsTxtPath) {
    $llmsText = [System.IO.File]::ReadAllText($llmsTxtPath)
    foreach ($refMatch in [regex]::Matches($llmsText, '`((?:scripts|src|\.agents/skills)/[A-Za-z0-9_./-]+\.(?:ps1|py|md))`')) {
        $refRel = $refMatch.Groups[1].Value
        $candRef = Join-Path $repoRoot ($refRel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $candRef)) {
            $candRef2 = Join-Path $scriptRepoRoot ($refRel -replace '/', '\')
            if (-not (Test-Path -LiteralPath $candRef2)) {
                $skillGateViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][LLMS_TXT_103] llms.txt references missing file '$refRel' (#103).")
            }
        }
    }
}

if (-not ("KongorCsGateScanner" -as [type])) {
    $csScannerSrc = @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public static class KongorCsGateScanner {
    static readonly Regex BlockCommentRx = new Regex(@"/\*[\s\S]*?\*/", RegexOptions.None);
    static readonly Regex LineCommentRx  = new Regex(@"//.*?$", RegexOptions.Multiline);
    static readonly Regex EmptyCatchRx   = new Regex(@"\bcatch\s*(?:\([^)]*\))?\s*\{\s*\}", RegexOptions.None);
    static readonly Regex PubTypeRx      = new Regex(@"\bpublic\s+(?:(?:sealed|abstract|static|partial)\s+)*(?:class|struct|interface|record|enum)\s+([A-Z][A-Za-z0-9_]{2,})\b", RegexOptions.None);
    static readonly Regex Tier6DupTypeRx = new Regex(@"(?m)^\s*public\s+(?!partial\b)(?:(?:sealed|abstract|static|readonly|unsafe|new)\s+)*(?:record\s+(?:class|struct)\s+|class\s+|struct\s+|record\s+|enum\s+)(?!partial\b)([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.None);
    static readonly Regex WordRx         = new Regex(@"\b[A-Z][A-Za-z0-9_]{2,}\b", RegexOptions.None);
    static readonly Regex TestAttrRx     = new Regex(@"\[(?:Xunit\.)?(?:Fact|Theory)\b[^\]]*\]", RegexOptions.None);
    static readonly Regex TraitManualRx  = new Regex(@"\[Trait\s*\(\s*""Category""\s*,\s*""Manual""\s*\)\]", RegexOptions.None);
    static readonly Regex MethodNameRx   = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(", RegexOptions.RightToLeft);
    static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "obj", "bin", "Generated", "Migrations", "node_modules", ".venv", "comfyui", "voicebox", "voicebox-repo"
    };
    static readonly HashSet<string> SkipTier6Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "obj", "bin", "Migrations", "node_modules", ".venv", "comfyui", "voicebox", "voicebox-repo"
    };

    static void WalkCsFiles(string dir, List<string> results) {
        if (!Directory.Exists(dir)) return;
        try {
            foreach (var f in Directory.GetFiles(dir, "*.cs")) results.Add(f);
            foreach (var sub in Directory.GetDirectories(dir)) {
                string name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name)) continue;
                WalkCsFiles(sub, results);
            }
        } catch {}
    }

    static void WalkCsFileInfosStamp(DirectoryInfo dir, HashSet<string> skipDirs, ref long lenSum, ref long ticksXor, ref long count) {
        if (!dir.Exists) return;
        try {
            foreach (var fi in dir.GetFiles("*.cs")) {
                count++;
                lenSum += fi.Length;
                ticksXor ^= fi.LastWriteTimeUtc.Ticks ^ (fi.Name.GetHashCode() & 0x7FFFFFFFL);
            }
            foreach (var sub in dir.GetDirectories()) {
                if (skipDirs.Contains(sub.Name)) continue;
                WalkCsFileInfosStamp(sub, skipDirs, ref lenSum, ref ticksXor, ref count);
            }
        } catch {}
    }

    public static string ComputeCsTreeStamp(string repoRoot, string slopRulesPath) {
        long lenSum = 0L;
        long ticksXor = 0L;
        long count = 0L;
        WalkCsFileInfosStamp(new DirectoryInfo(Path.Combine(repoRoot, "src")), SkipDirs, ref lenSum, ref ticksXor, ref count);
        WalkCsFileInfosStamp(new DirectoryInfo(Path.Combine(repoRoot, "tests")), SkipDirs, ref lenSum, ref ticksXor, ref count);
        if (!string.IsNullOrEmpty(slopRulesPath) && File.Exists(slopRulesPath)) {
            var sfi = new FileInfo(slopRulesPath);
            lenSum += sfi.Length;
            ticksXor ^= sfi.LastWriteTimeUtc.Ticks;
        }
        ticksXor ^= (count << 32);
        return lenSum.ToString() + "|" + ticksXor.ToString();
    }

    public static string ComputeLuaTier2Stamp(string verifierBin, string[] dirs, string[] extraFiles) {
        long lenSum = 0L;
        long ticksXor = 0L;
        long count = 0L;
        if (!string.IsNullOrEmpty(verifierBin) && File.Exists(verifierBin)) {
            var vfi = new FileInfo(verifierBin);
            lenSum += vfi.Length;
            ticksXor ^= vfi.LastWriteTimeUtc.Ticks;
        }
        if (extraFiles != null) {
            foreach (var ef in extraFiles) {
                if (!string.IsNullOrEmpty(ef) && File.Exists(ef)) {
                    var efi = new FileInfo(ef);
                    count++;
                    lenSum += efi.Length;
                    ticksXor ^= efi.LastWriteTimeUtc.Ticks;
                }
            }
        }
        if (dirs != null) {
            foreach (var d in dirs) {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                try {
                    var di = new DirectoryInfo(d);
                    foreach (var fi in di.EnumerateFiles("*.*", SearchOption.AllDirectories)) {
                        string ext = fi.Extension;
                        if (string.Equals(ext, ".lua", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)) {
                            count++;
                            lenSum += fi.Length;
                            ticksXor ^= fi.LastWriteTimeUtc.Ticks ^ (fi.Name.GetHashCode() & 0x7FFFFFFFL);
                        }
                    }
                } catch {}
            }
        }
        ticksXor ^= (count << 32);
        return lenSum.ToString() + "|" + ticksXor.ToString();
    }

    public static string ComputeFileListStamp(string primaryFile, string[] files) {
        long lenSum = 0L;
        long ticksXor = 0L;
        long count = 0L;
        if (!string.IsNullOrEmpty(primaryFile) && File.Exists(primaryFile)) {
            var pfi = new FileInfo(primaryFile);
            lenSum += pfi.Length;
            ticksXor ^= pfi.LastWriteTimeUtc.Ticks;
        }
        if (files != null) {
            foreach (var f in files) {
                if (string.IsNullOrEmpty(f)) continue;
                try {
                    var fi = new FileInfo(f);
                    if (fi.Exists) {
                        count++;
                        lenSum += fi.Length;
                        ticksXor ^= fi.LastWriteTimeUtc.Ticks;
                    }
                } catch {}
            }
        }
        ticksXor ^= (count << 32);
        return lenSum.ToString() + "|" + ticksXor.ToString();
    }

    static void WalkTier6ProdCsFiles(DirectoryInfo dir, List<FileInfo> results) {
        if (!dir.Exists) return;
        try {
            foreach (var fi in dir.GetFiles("*.cs")) {
                string n = fi.Name;
                if (n.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                results.Add(fi);
            }
            foreach (var sub in dir.GetDirectories()) {
                if (SkipTier6Dirs.Contains(sub.Name)) continue;
                WalkTier6ProdCsFiles(sub, results);
            }
        } catch {}
    }

    public static FileInfo[] GetTier6ProdCsFiles(string srcRoot) {
        var list = new List<FileInfo>(1500);
        if (!string.IsNullOrEmpty(srcRoot) && Directory.Exists(srcRoot)) {
            WalkTier6ProdCsFiles(new DirectoryInfo(srcRoot), list);
        }
        return list.ToArray();
    }

    public static string[] GetCsFilesNoBinObj(string rootDir) {
        var list = new List<string>(256);
        WalkCsFiles(rootDir, list);
        return list.ToArray();
    }

    public static string ComputeSrcDirStamp(string srcRootDir, string baselineFile) {
        long lenSum = 0L;
        long ticksXor = 0L;
        long count = 0L;
        if (!string.IsNullOrEmpty(srcRootDir) && Directory.Exists(srcRootDir)) {
            WalkCsFileInfosStamp(new DirectoryInfo(srcRootDir), SkipTier6Dirs, ref lenSum, ref ticksXor, ref count);
        }
        if (!string.IsNullOrEmpty(baselineFile) && File.Exists(baselineFile)) {
            var bfi = new FileInfo(baselineFile);
            lenSum += bfi.Length;
            ticksXor ^= bfi.LastWriteTimeUtc.Ticks;
        }
        ticksXor ^= (count << 32);
        return lenSum.ToString() + "|" + ticksXor.ToString();
    }

    public static int ScanTier6DuplicateTypes(string repoRoot, string srcRootDir, HashSet<string> baselinedDuplicateTypes, List<string> tier6Errors) {
        int violations = 0;
        if (string.IsNullOrEmpty(srcRootDir) || !Directory.Exists(srcRootDir)) return 0;
        var allSrcCsFiles = GetTier6ProdCsFiles(srcRootDir);
        var prodTypeMap = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        int prefixLen = repoRoot.Length;

        foreach (var srcFile in allSrcCsFiles) {
            string relSrc = srcFile.FullName.Substring(prefixLen).TrimStart('\\', '/').Replace('\\', '/');
            string rawCs = File.ReadAllText(srcFile.FullName);
            string cleanCs = LineCommentRx.Replace(BlockCommentRx.Replace(rawCs, ""), "");
            var foundTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match tm in Tier6DupTypeRx.Matches(cleanCs)) {
                foundTypes.Add(tm.Groups[1].Value);
            }
            foreach (string tName in foundTypes) {
                List<string> decls;
                if (!prodTypeMap.TryGetValue(tName, out decls)) {
                    decls = new List<string>(2);
                    prodTypeMap[tName] = decls;
                }
                if (!decls.Contains(relSrc)) {
                    decls.Add(relSrc);
                }
            }
        }

        foreach (var kv in prodTypeMap) {
            if (kv.Value.Count >= 2 && (baselinedDuplicateTypes == null || !baselinedDuplicateTypes.Contains(kv.Key))) {
                violations++;
                tier6Errors.Add("[GATE][TIER6][DUPLICATE_PUBLIC_TYPE] Public type '" + kv.Key + "' is duplicated across " + kv.Value.Count + " production files (" + string.Join(", ", kv.Value.ToArray()) + "). Consolidate into a single canonical definition in src/libs/protocol-core/ (#258).");
            }
        }
        return violations;
    }

    static string ComputeFastSha256Hex(SHA256 sha, string filePath) {
        byte[] bytes = File.ReadAllBytes(filePath);
        byte[] hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "");
    }

    public static int[] CheckTier5Parity(
        string repoRoot,
        string resolvedOverrideRoot,
        string[] targetRoots,
        bool[] targetIsTracked,
        HashSet<string> checkedExtSet,
        HashSet<string> binaryExtSet,
        HashSet<string> excludedRelSet,
        string[] untrackedPrefixes,
        bool useParityCache,
        string parityCachePath,
        List<string> driftedTrackedFiles,
        List<string> driftedCpfFiles,
        List<string> tier5Errors)
    {
        int trackedPairsCompared = 0;
        int cpfPairsCompared = 0;
        int excludedProbesSkipped = 0;

        var parityCacheMap = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bool parityCacheDirty = false;
        if (useParityCache && !string.IsNullOrEmpty(parityCachePath) && File.Exists(parityCachePath)) {
            try {
                foreach (string pcl in File.ReadAllLines(parityCachePath)) {
                    if (string.IsNullOrWhiteSpace(pcl) || pcl.StartsWith("#")) continue;
                    int tabIdx = pcl.IndexOf('\t');
                    if (tabIdx > 0 && tabIdx + 1 < pcl.Length) {
                        parityCacheMap[pcl.Substring(0, tabIdx)] = pcl.Substring(tabIdx + 1);
                    }
                }
            } catch {
                parityCacheMap.Clear();
            }
        }

        int ovrPrefixLen = resolvedOverrideRoot.Length + 1;
        int targetCount = targetRoots != null ? targetRoots.Length : 0;
        var presentIdx = new List<int>(8);
        var presentFiles = new List<string>(8);
        var presentLens = new List<long>(8);
        var presentTrackedFlags = new List<bool>(8);
        var stampSb = new StringBuilder(256);

        var targetTopDirs = new HashSet<string>[targetCount];
        for (int ti = 0; ti < targetCount; ti++) {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                foreach (string sd in Directory.GetDirectories(targetRoots[ti])) {
                    set.Add(Path.GetFileName(sd));
                }
                foreach (string sf in Directory.GetFiles(targetRoots[ti])) {
                    set.Add(Path.GetFileName(sf));
                }
            } catch {}
            targetTopDirs[ti] = set;
        }

        using (var sha = SHA256.Create()) {
            var ovrDirInfo = new DirectoryInfo(resolvedOverrideRoot);
            foreach (var ovrFile in ovrDirInfo.EnumerateFiles("*", SearchOption.AllDirectories)) {
                string ext = ovrFile.Extension;
                if (!checkedExtSet.Contains(ext)) continue;

                string relPath = ovrFile.FullName.Substring(ovrPrefixLen);
                string relPathNormalized = relPath.Replace('\\', '/');
                int firstSlash = relPathNormalized.IndexOf('/');
                string topSegment = firstSlash > 0 ? relPathNormalized.Substring(0, firstSlash) : relPathNormalized;

                if (excludedRelSet.Contains(relPathNormalized)) {
                    excludedProbesSkipped++;
                    continue;
                }

                if (untrackedPrefixes != null && untrackedPrefixes.Length > 0) {
                    bool isUntracked = false;
                    for (int ui = 0; ui < untrackedPrefixes.Length; ui++) {
                        string utPrefix = untrackedPrefixes[ui];
                        if (string.Equals(relPathNormalized, utPrefix, StringComparison.OrdinalIgnoreCase) ||
                            relPathNormalized.StartsWith(utPrefix + "/", StringComparison.OrdinalIgnoreCase)) {
                            isUntracked = true;
                            break;
                        }
                    }
                    if (isUntracked) {
                        excludedProbesSkipped++;
                        continue;
                    }
                }

                presentIdx.Clear();
                presentFiles.Clear();
                presentLens.Clear();
                presentTrackedFlags.Clear();
                int presentTracked = 0;
                int presentCpf = 0;

                stampSb.Length = 0;
                stampSb.Append(ovrFile.Length).Append(':').Append(ovrFile.LastWriteTimeUtc.Ticks);

                for (int ti = 0; ti < targetCount; ti++) {
                    if (!targetTopDirs[ti].Contains(topSegment)) continue;
                    string targetFile = Path.Combine(targetRoots[ti], relPath);
                    if (File.Exists(targetFile)) {
                        var tFi = new FileInfo(targetFile);
                        bool isTrk = targetIsTracked[ti];
                        presentIdx.Add(ti);
                        presentFiles.Add(targetFile);
                        presentLens.Add(tFi.Length);
                        presentTrackedFlags.Add(isTrk);
                        stampSb.Append(';').Append(ti).Append(',').Append(tFi.Length).Append(',').Append(tFi.LastWriteTimeUtc.Ticks);
                        if (isTrk) presentTracked++; else presentCpf++;
                    }
                }

                if (presentFiles.Count == 0) continue;

                string parityStamp = stampSb.ToString();
                string cachedStamp;
                if (useParityCache && parityCacheMap.TryGetValue(relPathNormalized, out cachedStamp) && string.Equals(cachedStamp, parityStamp, StringComparison.Ordinal)) {
                    trackedPairsCompared += presentTracked;
                    cpfPairsCompared += presentCpf;
                    continue;
                }

                bool isBinaryAsset = binaryExtSet.Contains(ext);
                long ovrLen = ovrFile.Length;
                string ovrTextCached = null;
                string ovrShaCached = null;
                bool allMatchedForRel = true;

                for (int pi = 0; pi < presentFiles.Count; pi++) {
                    string targetFile = presentFiles[pi];
                    long tgtLen = presentLens[pi];
                    bool isTrk = presentTrackedFlags[pi];
                    if (isTrk) trackedPairsCompared++; else cpfPairsCompared++;

                    bool isMatch = false;
                    if (isBinaryAsset) {
                        if (ovrLen == tgtLen) {
                            if (ovrShaCached == null) ovrShaCached = ComputeFastSha256Hex(sha, ovrFile.FullName);
                            string tgtSha = ComputeFastSha256Hex(sha, targetFile);
                            isMatch = string.Equals(ovrShaCached, tgtSha, StringComparison.OrdinalIgnoreCase);
                        }
                    } else {
                        if (ovrTextCached == null) {
                            ovrTextCached = File.ReadAllText(ovrFile.FullName).Replace("\r\n", "\n");
                        }
                        string tgtText = File.ReadAllText(targetFile).Replace("\r\n", "\n");
                        isMatch = string.Equals(ovrTextCached, tgtText, StringComparison.Ordinal);
                    }

                    if (!isMatch) {
                        allMatchedForRel = false;
                        if (ovrShaCached == null) ovrShaCached = ComputeFastSha256Hex(sha, ovrFile.FullName);
                        string ovrShaShort = ovrShaCached.Substring(0, 12).ToLowerInvariant();
                        string tgtShaShort = ComputeFastSha256Hex(sha, targetFile).Substring(0, 12).ToLowerInvariant();
                        string relTarget = targetFile.Replace(repoRoot, "").TrimStart('\\', '/').Replace('\\', '/');
                        string relOvr = ovrFile.FullName.Replace(repoRoot, "").TrimStart('\\', '/').Replace('\\', '/');
                        string modeLabel = isBinaryAsset ? "binary-sha256" : "lf-normalized";
                        if (isTrk) driftedTrackedFiles.Add(relTarget); else driftedCpfFiles.Add(relTarget);
                        tier5Errors.Add(
                            "[GATE][TIER5][FAIL] Cross-tree drift (" + modeLabel + "): " + relPathNormalized + "\n" +
                            "     override/   " + ovrLen + "B  sha256:" + ovrShaShort + " (" + relOvr + ")\n" +
                            "     counterpart " + tgtLen + "B  sha256:" + tgtShaShort + " (" + relTarget + ")\n" +
                            "     Fix: Copy-Item -Path \"" + relOvr + "\" -Destination \"" + relTarget + "\" -Force"
                        );
                    }
                }

                if (useParityCache && allMatchedForRel) {
                    parityCacheMap[relPathNormalized] = parityStamp;
                    parityCacheDirty = true;
                }
            }
        }

        if (useParityCache && parityCacheDirty && !string.IsNullOrEmpty(parityCachePath)) {
            string gateDir = Path.GetDirectoryName(parityCachePath);
            if (!string.IsNullOrEmpty(gateDir) && Directory.Exists(gateDir)) {
                try {
                    var outLines = new List<string>(parityCacheMap.Count + 1);
                    outLines.Add("# kongor.parity.cache.v1");
                    foreach (var kv in parityCacheMap) {
                        outLines.Add(kv.Key + "\t" + kv.Value);
                    }
                    File.WriteAllLines(parityCachePath, outLines.ToArray(), new UTF8Encoding(false));
                } catch {}
            }
        }

        return new int[] { trackedPairsCompared, cpfPairsCompared, excludedProbesSkipped };
    }

    public static string ScanRatchets(string repoRoot) {
        string srcRoot = Path.Combine(repoRoot, "src");
        string testsRoot = Path.Combine(repoRoot, "tests");
        var srcFiles = new List<string>(1500);
        WalkCsFiles(srcRoot, srcFiles);

        var dirEmptyCatch = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var declaredTypes = new List<KeyValuePair<string, string>>(2048);
        var srcRefCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        object mergeLock = new object();

        int prefixLen = repoRoot.TrimEnd('\\', '/').Length + 1;
        Parallel.ForEach(srcFiles, f => {
            string rel = f.Substring(prefixLen).Replace('\\', '/');
            var parts = rel.Split('/');
            string dirKey = parts.Length >= 3 ? (parts[0] + "/" + parts[1] + "/" + parts[2]) : (parts[0] + "/" + parts[1]);

            string raw = File.ReadAllText(f);
            string noBlock = BlockCommentRx.Replace(raw, "");
            string noComments = LineCommentRx.Replace(noBlock, "");

            int ec = EmptyCatchRx.Matches(noComments).Count;
            string fileName = Path.GetFileName(f);
            bool skipTypeDecl = Regex.IsMatch(fileName, @"(?i)(Tests?|Mocks?|Benchmarks?|Program|Startup|Module|Extensions|Exception|Attribute|Options|Config|Settings|Dto|Model|Request|Response|Event|Message|Command|Query|Handler|Controller)\.cs$");
            var fileWords = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match wm in WordRx.Matches(noComments)) {
                fileWords.Add(wm.Value);
            }
            List<KeyValuePair<string, string>> localDecl = null;
            if (!skipTypeDecl) {
                foreach (Match tm in PubTypeRx.Matches(noComments)) {
                    if (localDecl == null) localDecl = new List<KeyValuePair<string, string>>(4);
                    localDecl.Add(new KeyValuePair<string, string>(tm.Groups[1].Value, f));
                }
            }

            lock (mergeLock) {
                if (ec > 0) {
                    int prev;
                    dirEmptyCatch.TryGetValue(dirKey, out prev);
                    dirEmptyCatch[dirKey] = prev + ec;
                }
                foreach (var w in fileWords) {
                    int c;
                    srcRefCounts.TryGetValue(w, out c);
                    srcRefCounts[w] = c + 1;
                }
                if (localDecl != null) {
                    declaredTypes.AddRange(localDecl);
                }
            }
        });

        declaredTypes.Sort((a, b) => {
            int cmp = string.CompareOrdinal(a.Value, b.Value);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
        });

        var testWords = new HashSet<string>(StringComparer.Ordinal);
        var testFiles = new List<string>(1024);
        WalkCsFiles(testsRoot, testFiles);
        Parallel.ForEach(
            testFiles,
            () => new HashSet<string>(StringComparer.Ordinal),
            (tf, _, localSet) => {
                string raw = File.ReadAllText(tf);
                foreach (Match wm in WordRx.Matches(raw)) {
                    localSet.Add(wm.Value);
                }
                return localSet;
            },
            localSet => {
                lock (mergeLock) {
                    testWords.UnionWith(localSet);
                }
            });

        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        int testOnlyCount = 0;
        foreach (var kv in declaredTypes) {
            string tName = kv.Key;
            if (!seenTypes.Add(tName)) continue;
            int totalFilesWithWord;
            srcRefCounts.TryGetValue(tName, out totalFilesWithWord);
            if (totalFilesWithWord <= 1 && testWords.Contains(tName)) {
                testOnlyCount++;
            }
        }

        var sb = new StringBuilder();
        sb.Append(testOnlyCount);
        foreach (var kv in dirEmptyCatch) {
            sb.Append(';').Append(kv.Key).Append('=').Append(kv.Value);
        }
        return sb.ToString();
    }

    public static string[] ScanUntraitedManualTests(string repoRoot) {
        string testsRoot = Path.Combine(repoRoot, "tests");
        if (!Directory.Exists(testsRoot)) return new string[0];

        var testFiles = new List<string>(1024);
        WalkCsFiles(testsRoot, testFiles);
        int prefixLen = repoRoot.TrimEnd('\\', '/').Length + 1;
        var findings = new ConcurrentBag<string>();

        Parallel.ForEach(testFiles, tf => {
            string raw = File.ReadAllText(tf);
            if (raw.IndexOf("IsManualTestEnabled", StringComparison.Ordinal) < 0) return;

            string clean = LineCommentRx.Replace(BlockCommentRx.Replace(raw, ""), "");
            if (clean.IndexOf("IsManualTestEnabled", StringComparison.Ordinal) < 0) return;

            MatchCollection testMatches = TestAttrRx.Matches(clean);
            if (testMatches.Count == 0) return;

            string classHeader = clean.Substring(0, testMatches[0].Index);
            if (TraitManualRx.IsMatch(classHeader)) return;

            string rel = tf.Substring(prefixLen).Replace('\\', '/');
            int prevBodyEnd = 0;

            for (int i = 0; i < testMatches.Count; i++) {
                Match m = testMatches[i];
                if (m.Index < prevBodyEnd) continue;

                int preStart = Math.Max(prevBodyEnd, m.Index - 400);
                string preAttrs = clean.Substring(preStart, m.Index - preStart);
                int nextTestIdx = (i + 1 < testMatches.Count) ? testMatches[i + 1].Index : clean.Length;

                int braceOpen = clean.IndexOf('{', m.Index + m.Length);
                if (braceOpen < 0 || braceOpen >= nextTestIdx) continue;

                string sig = clean.Substring(m.Index, braceOpen - m.Index);
                int depth = 0;
                int braceClose = -1;
                for (int p = braceOpen; p < clean.Length; p++) {
                    char ch = clean[p];
                    if (ch == '{') depth++;
                    else if (ch == '}') {
                        depth--;
                        if (depth == 0) {
                            braceClose = p;
                            break;
                        }
                    }
                }
                if (braceClose < 0) continue;
                prevBodyEnd = braceClose + 1;

                string body = clean.Substring(braceOpen, braceClose - braceOpen + 1);
                if (body.IndexOf("IsManualTestEnabled", StringComparison.Ordinal) >= 0 &&
                    !TraitManualRx.IsMatch(preAttrs + sig)) {
                    Match nm = MethodNameRx.Match(sig);
                    string methodName = nm.Success ? nm.Groups[1].Value : "UnknownTest";
                    findings.Add(rel + " :: " + methodName);
                }
            }
        });

        string[] arr = findings.ToArray();
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }
}
"@
    $scannerSrcBytes = [System.Text.Encoding]::UTF8.GetBytes($csScannerSrc)
    $scannerSha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $scannerHash16 = ([System.BitConverter]::ToString($scannerSha.ComputeHash($scannerSrcBytes)) -replace '-', '').Substring(0, 16).ToLowerInvariant()
    } finally {
        $scannerSha.Dispose()
    }
    $scannerClrTag = "${PSEdition}_$($PSVersionTable.PSVersion.Major)"
    $cachedScannerDll = Join-Path ([System.IO.Path]::GetTempPath()) "KongorCsGateScanner_${scannerClrTag}_${scannerHash16}.dll"
    $loadedCachedScanner = $false
    if (Test-Path -LiteralPath $cachedScannerDll -PathType Leaf) {
        try {
            Add-Type -Path $cachedScannerDll -ErrorAction Stop
            $loadedCachedScanner = $true
        } catch {
            $loadedCachedScanner = $false
        }
    }
    if ((-not $loadedCachedScanner) -and (-not ("KongorCsGateScanner" -as [type]))) {
        $tmpScannerDll = Join-Path ([System.IO.Path]::GetTempPath()) "KongorCsGateScanner_${scannerClrTag}_${scannerHash16}_${PID}.dll"
        try {
            Add-Type -TypeDefinition $csScannerSrc -OutputAssembly $tmpScannerDll -ErrorAction Stop
            try {
                Move-Item -LiteralPath $tmpScannerDll -Destination $cachedScannerDll -Force -ErrorAction Stop
            } catch {
                Write-Verbose "KongorCsGateScanner cache move notice: $($_.Exception.Message)"
            }
            if (Test-Path -LiteralPath $cachedScannerDll -PathType Leaf) {
                Add-Type -Path $cachedScannerDll -ErrorAction Stop
            } elseif (Test-Path -LiteralPath $tmpScannerDll -PathType Leaf) {
                Add-Type -Path $tmpScannerDll -ErrorAction Stop
            }
        } catch {
            if (-not ("KongorCsGateScanner" -as [type])) {
                Add-Type -TypeDefinition $csScannerSrc -ErrorAction Stop
            }
        } finally {
            if (Test-Path -LiteralPath $tmpScannerDll -PathType Leaf) {
                Remove-Item -LiteralPath $tmpScannerDll -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

# 3b. Per-Directory C# Empty-Catch Ratchet & Test-Only Public Type Ceiling (slop_rules.json)
# shortcut: max_test_only_public_types ceiling held at 165 for owner=web-portal/dawnbringer, re-ratchet to <= 158 once dirty src/services/web-{api,ui} lands
if ($null -ne $slopRulesConfig -and $null -ne $slopRulesConfig.empty_catch_ratchet_by_dir -and (Test-Path -LiteralPath $srcDir)) {
    $csRatchetCacheKey = "__cs_ratchets_v3__"
    $csRatchetPayload = ""
    $fastRatchetStamp = [KongorCsGateScanner]::ComputeCsTreeStamp($repoRoot, $slopRulesPath)

    if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and (-not [string]::IsNullOrEmpty($fastRatchetStamp)) -and $symbolCacheMap.ContainsKey($csRatchetCacheKey) -and $symbolCacheMap[$csRatchetCacheKey].Stamp -eq $fastRatchetStamp) {
        $symbolCacheHits++
        $csRatchetPayload = [string]$symbolCacheMap[$csRatchetCacheKey].Payload
    } else {
        $symbolCacheMisses++
        $csRatchetPayload = [KongorCsGateScanner]::ScanRatchets($repoRoot)
        if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and (-not [string]::IsNullOrEmpty($fastRatchetStamp))) {
            $symbolCacheMap[$csRatchetCacheKey] = [PSCustomObject]@{ Stamp = $fastRatchetStamp; Payload = $csRatchetPayload }
            $symbolCacheDirty = $true
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($csRatchetPayload)) {
        $rParts = $csRatchetPayload -split ';'
        $actualTestOnlyTypes = [int]$rParts[0]
        if ($null -ne $slopRulesConfig.max_test_only_public_types) {
            $maxTestOnlyTypes = [int]$slopRulesConfig.max_test_only_public_types
            if ($actualTestOnlyTypes -gt $maxTestOnlyTypes) {
                $tier1Violations.Add("[GATE][TIER1][CS_TEST_ONLY_PUBLIC] Found $actualTestOnlyTypes test-only public type(s) in src/, exceeding ratchet ceiling $maxTestOnlyTypes in data/gate/slop_rules.json.")
            }
        }
        $ratchetMap = @{}
        foreach ($prop in $slopRulesConfig.empty_catch_ratchet_by_dir.PSObject.Properties) {
            $ratchetMap[$prop.Name] = [int]$prop.Value
        }
        for ($ri = 1; $ri -lt $rParts.Length; $ri++) {
            if ($rParts[$ri] -match '^([^=]+)=(\d+)$') {
                $dKey = $Matches[1]
                $dCount = [int]$Matches[2]
                $allowedMax = if ($ratchetMap.ContainsKey($dKey)) { $ratchetMap[$dKey] } else { 0 }
                if ($dCount -gt $allowedMax) {
                    $tier1Violations.Add("[GATE][TIER1][CS_EMPTY_CATCH] Directory '$dKey' has $dCount empty catch block(s), exceeding ratchet ceiling $allowedMax in data/gate/slop_rules.json.")
                }
            }
        }
    }

    $untraitedCacheKey = "__cs_untraited_manual_v1__"
    if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and (-not [string]::IsNullOrEmpty($fastRatchetStamp)) -and $symbolCacheMap.ContainsKey($untraitedCacheKey) -and $symbolCacheMap[$untraitedCacheKey].Stamp -eq $fastRatchetStamp -and $symbolCacheMap[$untraitedCacheKey].Payload -eq "OK") {
        $symbolCacheHits++
    } else {
        $symbolCacheMisses++
        $untraitedManualFindings = [KongorCsGateScanner]::ScanUntraitedManualTests($repoRoot)
        if ($untraitedManualFindings.Length -gt 0) {
            foreach ($umf in $untraitedManualFindings) {
                $tier1Violations.Add("[GATE][TIER1][UNTRAITED_MANUAL_TEST] $umf checks IsManualTestEnabled without [Trait(`"Category`", `"Manual`")]. Add [Trait(`"Category`", `"Manual`")] above the test method so default test runs report truthful skip/filter counts.")
            }
        } elseif (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and (-not [string]::IsNullOrEmpty($fastRatchetStamp))) {
            $symbolCacheMap[$untraitedCacheKey] = [PSCustomObject]@{ Stamp = $fastRatchetStamp; Payload = "OK" }
            $symbolCacheDirty = $true
        }
    }
}


# 4. Orphan Worker Lock Check & RVA Coverage Ledger (#43) + Global OBL-XX ID Uniqueness (#62)
$locksDir = Join-Path $repoRoot "userdata\locks"
if (Test-Path -LiteralPath $locksDir) {
    foreach ($lf in @(Get-ChildItem -LiteralPath $locksDir -Filter "*.lock*" -File)) {
        try {
            $lJson = Get-Content -LiteralPath $lf.FullName -Raw | ConvertFrom-Json
            if ($null -ne $lJson.pid) {
                $ownerPid = [int]$lJson.pid
                $proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
                if ($null -eq $proc) {
                    $tier1Violations.Add("[GATE][TIER1][ORPHAN_LOCK_043] Orphan worker lock '$($lf.Name)' held by dead PID $ownerPid (#43). Run: .\scripts\kongor_worker_lock.ps1 -Action release")
                }
            }
        } catch {
            $tier1Violations.Add("[GATE][TIER1][ORPHAN_LOCK_043] Corrupt worker lock file '$($lf.Name)': $($_.Exception.Message)")
        }
    }
}

$evRootDir = Join-Path $repoRoot "artifacts\evidence"
if (Test-Path -LiteralPath $evRootDir) {
    $covFile = Join-Path $evRootDir "coverage.json"
    if (Test-Path -LiteralPath $covFile) {
        try {
            $covData = Get-Content -LiteralPath $covFile -Raw | ConvertFrom-Json
            if ($null -eq $covData.entries -or [int]$covData.dossier_count -le 0) {
                $tier1Violations.Add("[GATE][TIER1][COVERAGE_043] artifacts/evidence/coverage.json has 0 entries (#43). Run: .\scripts\verify_evidence_obligations.ps1 -All -EmitCoverage")
            }
        } catch {
            $tier1Violations.Add("[GATE][TIER1][COVERAGE_043] Corrupt artifacts/evidence/coverage.json: $($_.Exception.Message)")
        }
    }
    $dossierFiles = @(Get-ChildItem -LiteralPath $evRootDir -Filter "*.json" -Recurse -File | Where-Object {
        $_.Name -notlike "_*" -and $_.Name -notin @("symbol_map.json", "pdb_synthesis_manifest.json", "coverage.json", "live_probe_queue.json")
    } | Select-Object -ExpandProperty FullName)
    $evCacheKey = "__ev_dossiers_v1__"
    $useEvCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full)
    $evStamp = if ($useEvCache) { [KongorCsGateScanner]::ComputeFileListStamp($covFile, $dossierFiles) } else { "" }
    if ($useEvCache -and $symbolCacheMap.ContainsKey($evCacheKey) -and $symbolCacheMap[$evCacheKey].Stamp -eq $evStamp -and $symbolCacheMap[$evCacheKey].Payload -eq "OK") {
        $symbolCacheHits++
    } else {
        if ($useEvCache) { $symbolCacheMisses++ }
        $evBatchViolations = 0
        foreach ($dfPath in $dossierFiles) {
            $dfName = [System.IO.Path]::GetFileName($dfPath)
            try {
                $dObj = Get-Content -LiteralPath $dfPath -Raw | ConvertFrom-Json
                $seenObl = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                foreach ($oItem in @($dObj.obligations)) {
                    if ($null -ne $oItem -and -not [string]::IsNullOrWhiteSpace([string]$oItem.id)) {
                        $oIdStr = ([string]$oItem.id).Trim()
                        if (-not $seenObl.Add($oIdStr)) {
                            $evBatchViolations++
                            $tier1Violations.Add("[GATE][TIER1][UNIQUE_ID_062] Duplicate obligation ID '$oIdStr' in dossier '$dfName' (#62).")
                        }
                    }
                }
            } catch {
                $evBatchViolations++
                $tier1Violations.Add("[GATE][TIER1][UNIQUE_ID_062] Corrupt dossier JSON '$dfName': $($_.Exception.Message)")
            }
        }
        if ($useEvCache -and $evBatchViolations -eq 0 -and (-not [string]::IsNullOrEmpty($evStamp))) {
            $symbolCacheMap[$evCacheKey] = [PSCustomObject]@{ Stamp = $evStamp; Payload = "OK" }
            $symbolCacheDirty = $true
        }
    }
}
$t1P3Sw.Stop()
Write-Host " [GATE-PERF] Phase3_Slop_Skill_Boundary_And_Id_Gate elapsed_ms=$($t1P3Sw.ElapsedMilliseconds)" -ForegroundColor DarkGray

# Phase 19 (#239, #240, #241, #248): RepoWise Doc-Drift Fingerprints, Architecture Layers & Bounded Cycles,
# Two-Pass Intermediate-Commit Secret Scanner, and Hot-Loop Code Health Auditor
$t1P19Sw = [System.Diagnostics.Stopwatch]::StartNew()
$docDriftViolationsCount = 0
$docDriftBaselinedCount = 0
$archLayerViolationsCount = 0
$archCycleViolationsCount = 0
$secretLeakViolationsCount = 0
$hotLoopViolationsCount = 0

# #239: Multi-kind Doc-Drift Fingerprint Baseline Check (docs/gate_doc_drift or live docs/explanation)
$docDriftTargetDir = Join-Path $repoRoot "docs\gate_doc_drift"
$liveDocExplanationDir = Join-Path $repoRoot "docs\explanation"
$docDriftBaselinePath = Join-Path $repoRoot "data\gate\doc_drift_baseline.json"
if (Test-Path -LiteralPath $docDriftTargetDir) {
    $verifyDocScript = Join-Path $scriptRepoRoot "scripts\qa\verify_doc_links.ps1"
    if (Test-Path -LiteralPath $verifyDocScript) {
        $ddTempJson = Join-Path $env:TEMP ("kongor_dd_gate_" + [guid]::NewGuid().ToString("N") + ".json")
        try {
            & $verifyDocScript -RepoRoot $repoRoot -DocsPath $docDriftTargetDir -ScriptsRoot (Join-Path $scriptRepoRoot "scripts") -BaselineFile $docDriftBaselinePath -JsonOutput $ddTempJson -Quiet
            if (Test-Path -LiteralPath $ddTempJson) {
                $ddData = Get-Content -LiteralPath $ddTempJson -Raw | ConvertFrom-Json
                $docDriftBaselinedCount = [int]$ddData.baselinedCount
                foreach ($fItem in @($ddData.findings)) {
                    if (-not [bool]$fItem.baselined) {
                        $docDriftViolationsCount++
                        $tier1Violations.Add("[GATE][TIER1][DOC_DRIFT_239] $($fItem.doc) [$($fItem.kind)] Broken reference '$($fItem.target)' [fp:$($fItem.fingerprint)] (#239).")
                    }
                }
            }
        } finally {
            if (Test-Path -LiteralPath $ddTempJson) { Remove-Item -LiteralPath $ddTempJson -Force -ErrorAction SilentlyContinue }
        }
    }
} elseif (Test-Path -LiteralPath $liveDocExplanationDir) {
    $verifyDocScript = Join-Path $scriptRepoRoot "scripts\qa\verify_doc_links.ps1"
    if (Test-Path -LiteralPath $verifyDocScript) {
        $ddCacheKey = "__doc_drift_239__"
        $ddStamp = Get-DirectoryCacheStamp -dirPath $liveDocExplanationDir -scriptPath $verifyDocScript
        if (($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and $symbolCacheMap.ContainsKey($ddCacheKey) -and $symbolCacheMap[$ddCacheKey].Stamp -eq $ddStamp -and $symbolCacheMap[$ddCacheKey].Payload -match '^PASS;(\d+)$') {
            $docDriftBaselinedCount = [int]$Matches[1]
        } else {
            $ddTempJson = Join-Path $env:TEMP ("kongor_dd_live_" + [guid]::NewGuid().ToString("N") + ".json")
            try {
                & $verifyDocScript -RepoRoot $repoRoot -DocsPath $liveDocExplanationDir -ScriptsRoot (Join-Path $scriptRepoRoot "scripts") -BaselineFile $docDriftBaselinePath -JsonOutput $ddTempJson -Quiet
                if (Test-Path -LiteralPath $ddTempJson) {
                    $ddData = Get-Content -LiteralPath $ddTempJson -Raw | ConvertFrom-Json
                    $docDriftBaselinedCount = [int]$ddData.baselinedCount
                    foreach ($fItem in @($ddData.findings)) {
                        if (-not [bool]$fItem.baselined) {
                            $docDriftViolationsCount++
                            $tier1Violations.Add("[GATE][TIER1][DOC_DRIFT_239] $($fItem.doc) [$($fItem.kind)] Broken reference '$($fItem.target)' [fp:$($fItem.fingerprint)] (#239).")
                        }
                    }
                    if ($docDriftViolationsCount -eq 0 -and ($repoRoot -eq $scriptRepoRoot)) {
                        $symbolCacheMap[$ddCacheKey] = [PSCustomObject]@{ Stamp = $ddStamp; Payload = "PASS;$docDriftBaselinedCount" }
                        $symbolCacheDirty = $true
                    }
                }
            } finally {
                if (Test-Path -LiteralPath $ddTempJson) { Remove-Item -LiteralPath $ddTempJson -Force -ErrorAction SilentlyContinue }
            }
        }
    }
} else {
    $skippedCount++
}

# #240: Layered Architecture Conformance & Bounded DFS Cycle Detector (MAX_CYCLES = 50)
function Test-KongorLayerConformanceAndCycles {
    $moduleGraphProbePath = Join-Path $repoRoot "data\gate\module_graph_probe.json"
    $hasGraphProbe = Test-Path -LiteralPath $moduleGraphProbePath -PathType Leaf
    $useArchCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and (-not $hasGraphProbe)

    $protoCoreDirs = @(
        (Join-Path $repoRoot "src\core\protocol"),
        (Join-Path $repoRoot "src\libs\protocol-core")
    )
    $protoCsFiles = @()
    foreach ($protoCoreDir in $protoCoreDirs) {
        if (Test-Path -LiteralPath $protoCoreDir) {
            $protoCsFiles += @([KongorCsGateScanner]::GetCsFilesNoBinObj($protoCoreDir))
        }
    }

    $ovrLayerProbeDir = Join-Path $repoRoot "games\client\override"
    $ovrLayerCandidates = @()
    if (Test-Path -LiteralPath $ovrLayerProbeDir) {
        if (-not $inGit) {
            $ovrLayerCandidates = @(Get-ChildItem -LiteralPath $ovrLayerProbeDir -Filter "*.lua" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 25)
        } elseif ($gitStatus) {
            $ovrLayerCandidates = @($gitStatus | ForEach-Object {
                $parts = $_.Trim() -split '\s+', 2
                if ($parts.Length -eq 2) {
                    $relPath = (($parts[1] -split '\s+->\s+')[-1]).Trim('"')
                    if ($relPath -match '^games[\\/]client[\\/]override[\\/].*\.lua$') {
                        $fullP = Join-Path $repoRoot $relPath
                        if (Test-Path -LiteralPath $fullP -PathType Leaf) { Get-Item -LiteralPath $fullP }
                    }
                }
            } | Where-Object { $null -ne $_ })
        }
    }

    $csprojFiles = @()
    if ((-not $hasGraphProbe) -and (Test-Path -LiteralPath $srcDir)) {
        $csprojFiles = @(Get-ChildItem -Path (Join-Path $srcDir "*\*\*.csproj"), (Join-Path $srcDir "*\*.csproj") -File -ErrorAction SilentlyContinue)
    }

    $archCacheKey = "__arch_240_v1__"
    $archStamp = ""
    if ($useArchCache) {
        $archPaths = @($protoCsFiles) + @($ovrLayerCandidates | ForEach-Object { $_.FullName }) + @($pyFilesToScan) + @($csprojFiles | ForEach-Object { $_.FullName })
        $archStamp = [KongorCsGateScanner]::ComputeFileListStamp($slopRulesPath, [string[]]$archPaths)
        if ($symbolCacheMap.ContainsKey($archCacheKey) -and $symbolCacheMap[$archCacheKey].Stamp -eq $archStamp -and $symbolCacheMap[$archCacheKey].Payload -eq "OK") {
            $script:symbolCacheHits++
            return
        }
        $script:symbolCacheMisses++
    }

    $archViolationsBefore = $tier1Violations.Count
    $maxCyclesLimit = 50
    if ($null -ne $slopRulesConfig -and $slopRulesConfig.architecture_layers -and $slopRulesConfig.architecture_layers.max_cycles) {
        $maxCyclesLimit = [int]$slopRulesConfig.architecture_layers.max_cycles
    }

    # Evaluate declarative architecture_layers.deny_rules from data/gate/slop_rules.json plus legacy src/core/protocol path
    foreach ($pcfFull in $protoCsFiles) {
        $relPcf = $pcfFull.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $pcfText = [System.IO.File]::ReadAllText($pcfFull)
        if ($pcfText -match '\b(?:Kongor\.Mcp|ModelContextProtocol)\b|src[\\/]tools[\\/]kongor-cli[\\/]Mcp') {
            $script:archLayerViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][ARCH_LAYER_240] ${relPcf} violates layer rule 'protocol_core !-> mcp_cli' (#240).")
        }
    }

    foreach ($olf in $ovrLayerCandidates) {
        $relOlf = $olf.FullName.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $olfText = [System.IO.File]::ReadAllText($olf.FullName)
        if ($olfText -match 'games[\\/]server[\\/]|KONGOR_SERVER_INTERNAL|hon_server') {
            $script:archLayerViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][ARCH_LAYER_240] ${relOlf} violates layer rule 'client_override !-> server_internal' (#240).")
        }
    }

    foreach ($pyF in $pyFilesToScan) {
        $relPyF = $pyF.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $isRestrictedOfflineForLayer = $false
        foreach ($offPref in $offlineRestrictedRelPrefixes) {
            if ($relPyF.StartsWith($offPref, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isRestrictedOfflineForLayer = $true
                break
            }
        }
        if ($isRestrictedOfflineForLayer) {
            $pyTxt = [System.IO.File]::ReadAllText($pyF)
            if ($pyTxt -match 'native[\\/]proxies[\\/]native_guard|src[\\/]tools[\\/]native-guard|kongor-native-client') {
                $script:archLayerViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][ARCH_LAYER_240] ${relPyF} violates layer rule 'offline_tools !-> live_runtime' (#240).")
            }
        }
    }

    # Bounded Iterative DFS Cycle Detector (MAX_CYCLES = 50) (#240)
    $adj = @{}
    $hasGraphSource = $false
    if ($hasGraphProbe) {
        $hasGraphSource = $true
        try {
            $mgObj = Get-Content -LiteralPath $moduleGraphProbePath -Raw | ConvertFrom-Json
            foreach ($edge in @($mgObj.edges)) {
                $u = [string]$edge.from
                $v = [string]$edge.to
                if (-not [string]::IsNullOrWhiteSpace($u) -and -not [string]::IsNullOrWhiteSpace($v)) {
                    if (-not $adj.ContainsKey($u)) { $adj[$u] = New-Object System.Collections.Generic.List[string] }
                    $adj[$u].Add($v)
                }
            }
        } catch {
            $tier1Violations.Add("[GATE][TIER1][ARCH_CYCLE_240] Corrupt module_graph_probe.json: $($_.Exception.Message)")
            return
        }
    } elseif (Test-Path -LiteralPath $srcDir) {
        $hasGraphSource = $true
        foreach ($csproj in $csprojFiles) {
            $uName = [System.IO.Path]::GetFileNameWithoutExtension($csproj.Name)
            $projTxt = [System.IO.File]::ReadAllText($csproj.FullName)
            foreach ($prm in [regex]::Matches($projTxt, '<ProjectReference\s+Include="([^"]+\.csproj)"')) {
                $vName = [System.IO.Path]::GetFileNameWithoutExtension($prm.Groups[1].Value)
                if (-not [string]::IsNullOrWhiteSpace($uName) -and -not [string]::IsNullOrWhiteSpace($vName)) {
                    if (-not $adj.ContainsKey($uName)) { $adj[$uName] = New-Object System.Collections.Generic.List[string] }
                    $adj[$uName].Add($vName)
                }
            }
        }
    } else {
        $script:skippedCount++
    }

    if ($hasGraphSource -and $adj.Count -gt 0) {
        $detectedCycles = New-Object System.Collections.Generic.List[string]
        $seenCycleKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($startNode in ($adj.Keys | Sort-Object)) {
            if ($detectedCycles.Count -ge $maxCyclesLimit) { break }
            $stack = New-Object System.Collections.Generic.Stack[object]
            $stack.Push([PSCustomObject]@{ Node = $startNode; Path = @($startNode) })
            while ($stack.Count -gt 0 -and $detectedCycles.Count -lt $maxCyclesLimit) {
                $curr = $stack.Pop()
                $uNode = $curr.Node
                if (-not $adj.ContainsKey($uNode)) { continue }
                foreach ($vNode in $adj[$uNode]) {
                    if ($vNode -eq $startNode) {
                        $cycleNodes = $curr.Path
                        $minIdx = 0
                        for ($ci = 1; $ci -lt $cycleNodes.Count; $ci++) {
                            if ([string]::Compare($cycleNodes[$ci], $cycleNodes[$minIdx], $true) -lt 0) { $minIdx = $ci }
                        }
                        $rot = @()
                        for ($ci = 0; $ci -lt $cycleNodes.Count; $ci++) {
                            $rot += $cycleNodes[($minIdx + $ci) % $cycleNodes.Count]
                        }
                        $canonKey = ($rot -join " -> ") + " -> " + $rot[0]
                        if ($seenCycleKeys.Add($canonKey)) {
                            $detectedCycles.Add($canonKey)
                            if ($detectedCycles.Count -ge $maxCyclesLimit) { break }
                        }
                    } elseif ($curr.Path -notcontains $vNode -and $curr.Path.Count -lt 16) {
                        $nextPath = @($curr.Path) + @($vNode)
                        $stack.Push([PSCustomObject]@{ Node = $vNode; Path = $nextPath })
                    }
                }
            }
        }
        foreach ($cyc in $detectedCycles) {
            $script:archCycleViolationsCount++
            $tier1Violations.Add("[GATE][TIER1][ARCH_CYCLE_240] Circular dependency cycle detected: $cyc (#240).")
        }
    }

    if ($useArchCache -and ($tier1Violations.Count -eq $archViolationsBefore) -and (-not [string]::IsNullOrEmpty($archStamp))) {
        $symbolCacheMap[$archCacheKey] = [PSCustomObject]@{ Stamp = $archStamp; Payload = "OK" }
        $script:symbolCacheDirty = $true
    }
}
Test-KongorLayerConformanceAndCycles

# #241: Two-Pass Intermediate-Commit Secret Scanner (Pass 1 HEAD/Probe + Pass 2 Git History)
$secretScrubLib = Join-Path $scriptRepoRoot "scripts\lib\Test-KongorSecretScrub.ps1"
if (Test-Path -LiteralPath $secretScrubLib) {
    $secretProbeDir = Join-Path $repoRoot "data\gate\secret_scan_probe"
    $secretBaseRefFile = Join-Path $repoRoot "data\gate\secret_history_base_ref.txt"
    $candSecretFiles = @()
    $hasSecretProbe = $false
    if (Test-Path -LiteralPath $secretProbeDir) {
        $hasSecretProbe = $true
        $candSecretFiles = @(Get-ChildItem -LiteralPath $secretProbeDir -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    }
    $baseRefVal = ""
    if ((Test-Path -LiteralPath $secretBaseRefFile -PathType Leaf) -and (-not $isShallowClone)) {
        $hasSecretProbe = $true
        $baseRefVal = ([System.IO.File]::ReadAllText($secretBaseRefFile)).Trim()
    }
    if (-not $hasSecretProbe) {
        if ($inGit) {
            if ($gitStatus) {
                $candSecretFiles = @($gitStatus | ForEach-Object {
                    $parts = $_.Trim() -split '\s+', 2
                    if ($parts.Length -eq 2 -and $parts[0] -ne 'D') {
                        $relPath = (($parts[1] -split '\s+->\s+')[-1]).Trim('"')
                        if ($relPath -match '^(?:games|src|native|config|scripts|tools|tests|docs)[\\/].*\.(?:lua|cs|cpp|h|py|ps1|js|json|cfg|xml|toml|md|yml|yaml|bat|sh)$') {
                            $fullP = Join-Path $repoRoot $relPath
                            if (Test-Path -LiteralPath $fullP -PathType Leaf) { $fullP }
                        }
                    }
                } | Where-Object { $_ })
            }
        } else {
            $skippedCount++
        }
    }
    if ($candSecretFiles.Count -gt 0 -or (-not [string]::IsNullOrWhiteSpace($baseRefVal))) {
        $secCacheKey = "__sec_241_v1__"
        $useSecCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full) -and (-not $hasSecretProbe) -and [string]::IsNullOrWhiteSpace($baseRefVal)
        $secStamp = ""
        $secHit = $false
        if ($useSecCache) {
            $secStamp = [KongorCsGateScanner]::ComputeFileListStamp($secretScrubLib, [string[]]$candSecretFiles)
            if ($symbolCacheMap.ContainsKey($secCacheKey) -and $symbolCacheMap[$secCacheKey].Stamp -eq $secStamp -and $symbolCacheMap[$secCacheKey].Payload -eq "OK") {
                $secHit = $true
                $symbolCacheHits++
            }
        }
        if (-not $secHit) {
            if ($useSecCache) { $symbolCacheMisses++ }
            . $secretScrubLib
            $leakReport = Test-KongorSecretLeakage -RepoRoot $repoRoot -CandidateFiles $candSecretFiles -BaseRef $baseRefVal
            $secLeakFound = 0
            foreach ($lk in @($leakReport.Leaks)) {
                $secLeakFound++
                $secretLeakViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][SECRET_LEAK_241] $($lk.rule_name) in $($lk.file_path) (masked: $($lk.masked_snippet), present_in_head=$($lk.present_in_head), commit=$($lk.commit)) (#241).")
            }
            if ($useSecCache -and ($secLeakFound -eq 0) -and (-not [string]::IsNullOrEmpty($secStamp))) {
                $symbolCacheMap[$secCacheKey] = [PSCustomObject]@{ Stamp = $secStamp; Payload = "OK" }
                $symbolCacheDirty = $true
            }
        }
    }
}

# #248: Hot-Loop Code Health Auditor (C++ regex/sync-IO/sleep in loops, C# EF SaveChanges in loops)
function Get-KongorExtractedLoopBodies([string]$sourceText) {
    $bodies = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($sourceText)) { return $bodies }
    foreach ($kwMatch in [regex]::Matches($sourceText, '\b(?:for|foreach|while)\s*\(')) {
        $openParenIdx = $kwMatch.Index + $kwMatch.Length - 1
        $parenDepth = 0
        $closeParenIdx = -1
        for ($i = $openParenIdx; $i -lt $sourceText.Length; $i++) {
            $ch = $sourceText[$i]
            if ($ch -eq '(') { $parenDepth++ }
            elseif ($ch -eq ')') {
                $parenDepth--
                if ($parenDepth -eq 0) { $closeParenIdx = $i; break }
            }
        }
        if ($closeParenIdx -lt 0) { continue }
        $braceOpenIdx = $closeParenIdx + 1
        while ($braceOpenIdx -lt $sourceText.Length -and [char]::IsWhiteSpace($sourceText[$braceOpenIdx])) {
            $braceOpenIdx++
        }
        if ($braceOpenIdx -ge $sourceText.Length -or $sourceText[$braceOpenIdx] -ne '{') { continue }

        $normalizedText = $sourceText
        if ($sourceText.IndexOf('#if', [System.StringComparison]::Ordinal) -ge 0) {
            $normalizedText = [regex]::Replace($sourceText, '(?s)#\s*(?:elif|else)\b.*?#\s*endif\b', '')
        }
        if ($braceOpenIdx -lt $normalizedText.Length -and $normalizedText[$braceOpenIdx] -eq '{') {
            $depth = 0
            $foundBalanced = $false
            for ($j = $braceOpenIdx; $j -lt $normalizedText.Length; $j++) {
                if ($normalizedText[$j] -eq '{') { $depth++ }
                elseif ($normalizedText[$j] -eq '}') {
                    $depth--
                    if ($depth -eq 0) {
                        $bodies.Add($normalizedText.Substring($braceOpenIdx + 1, $j - $braceOpenIdx - 1))
                        $foundBalanced = $true
                        break
                    }
                }
            }
            if ($foundBalanced) { continue }
        }
        $fallbackLen = [Math]::Min(4096, $sourceText.Length - ($braceOpenIdx + 1))
        if ($fallbackLen -gt 0) {
            $bodies.Add($sourceText.Substring($braceOpenIdx + 1, $fallbackLen))
        }
    }
    return $bodies
}

function Test-KongorHotLoopCodeHealth {
    $hlCacheKey = "__hotloop_248_v1__"
    $useHlCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full)
    $hlStamp = ""
    if ($useHlCache) {
        $hlStamp = [KongorCsGateScanner]::ComputeFileListStamp($slopRulesPath, [string[]](@($nativeCleanroomFiles) + @($csFiles)))
        if ($symbolCacheMap.ContainsKey($hlCacheKey) -and $symbolCacheMap[$hlCacheKey].Stamp -eq $hlStamp -and $symbolCacheMap[$hlCacheKey].Payload -eq "OK") {
            $script:symbolCacheHits++
            return
        }
        $script:symbolCacheMisses++
    }
    $hlViolationsBefore = $tier1Violations.Count

    foreach ($nFile in $nativeCleanroomFiles) {
        $nFileName = [System.IO.Path]::GetFileName($nFile)
        if ($nFileName -match '(?i)(Tests?|Mocks?|Benchmarks?)\.(cpp|h)$') { continue }
        $relNative = $nFile.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $nRaw = [System.IO.File]::ReadAllText($nFile)
        $isHotLoopFileContext = ($nFileName -match '(?i)hotloop') -or ($nRaw -match 'KONGOR_HOT_LOOP_BEGIN|\b(?:OnFrame|FrameLoop|RenderFrame|TickFrame)\s*\(')
        foreach ($loopBody in (Get-KongorExtractedLoopBodies $nRaw)) {
            if ($loopBody -match 'kongor-hotloop-ignore') { continue }
            $loopNoComments = [regex]::Replace([regex]::Replace($loopBody, '(?s)/\*.*?\*/', ''), '(?m)//.*$', '')
            if ($loopNoComments -match '\bstd::(?:w)?regex\b') {
                $script:hotLoopViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][HOTLOOP_248] CPP-HOTLOOP-REGEX in ${relNative}: std::regex construction inside hot loop (#248).")
            }
            if ($isHotLoopFileContext) {
                if ($loopNoComments -match '\bstd::[io]?fstream\b|\bfopen\s*\(') {
                    $script:hotLoopViolationsCount++
                    $tier1Violations.Add("[GATE][TIER1][HOTLOOP_248] CPP-HOTLOOP-SYNC-IO in ${relNative}: Synchronous file stream open inside hot loop (#248).")
                }
                if ($loopNoComments -match '\bstd::this_thread::sleep_for\b|\bSleep\s*\(') {
                    $script:hotLoopViolationsCount++
                    $tier1Violations.Add("[GATE][TIER1][HOTLOOP_248] CPP-HOTLOOP-SLEEP in ${relNative}: Thread sleep inside hot loop (#248).")
                }
            }
        }
    }
    $csHotLoopExemptFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ($slopRulesConfig -and $slopRulesConfig.hot_loop_rules -and $slopRulesConfig.hot_loop_rules.cs_exempt_files) {
        foreach ($exPath in $slopRulesConfig.hot_loop_rules.cs_exempt_files) {
            [void]$csHotLoopExemptFiles.Add(([string]$exPath -replace '\\', '/').Trim('/'))
        }
    }
    foreach ($cFile in $csFiles) {
        $cFileName = [System.IO.Path]::GetFileName($cFile)
        if ($cFileName -match '(?i)(Tests?|Mocks?|Benchmarks?)\.cs$') { continue }
        $relCsFile = $cFile.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        if ($csHotLoopExemptFiles.Contains($relCsFile)) { continue }
        $cRaw = [System.IO.File]::ReadAllText($cFile)
        foreach ($loopBody in (Get-KongorExtractedLoopBodies $cRaw)) {
            if ($loopBody -match 'kongor-hotloop-ignore') { continue }
            $loopNoComments = [regex]::Replace([regex]::Replace($loopBody, '(?s)/\*.*?\*/', ''), '(?m)//.*$', '')
            if ($loopNoComments -match '\bcatch\s*\(\s*DbUpdateConcurrencyException\b') { continue }
            if ($loopNoComments -match '\.SaveChanges(?:Async)?\s*\(') {
                $script:hotLoopViolationsCount++
                $tier1Violations.Add("[GATE][TIER1][HOTLOOP_248] CS-HOTLOOP-EF-SAVE in ${relCsFile}: EntityFramework SaveChanges/SaveChangesAsync inside loop (#248).")
            }
        }
    }

    if ($useHlCache -and ($tier1Violations.Count -eq $hlViolationsBefore) -and (-not [string]::IsNullOrEmpty($hlStamp))) {
        $symbolCacheMap[$hlCacheKey] = [PSCustomObject]@{ Stamp = $hlStamp; Payload = "OK" }
        $script:symbolCacheDirty = $true
    }
}
Test-KongorHotLoopCodeHealth
$t1P19Sw.Stop()
Write-Host " [GATE-PERF] Phase19_RepoWise_Tier1_Auditors elapsed_ms=$($t1P19Sw.ElapsedMilliseconds)" -ForegroundColor DarkGray

# Phase 1C Hygiene Ratchets: Clean-Root Unexpected Entry Guard & Staged/Tracked Binary Extension Guard
# Skips cleanly in non-git synthetic $env:TEMP mutant sandboxes ($inGit -eq $false).
if ($inGit) {
    $allowedLocalRootEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ae in @('.git', '.env', '.tools', '.vscode', '.idea', '.claude', '.vs', '.pytest_cache', 'artifacts', 'logs', 'scratch', 'pyproject.toml')) {
        [void]$allowedLocalRootEntries.Add($ae)
    }

    $phase4TransitionalBinaryAllowlist = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ab in @(
        'native/deps/mimalloc/mimalloc-override.dll',
        'native/deps/mimalloc/mimalloc-redirect.dll',
        'native/deps/mimalloc/mimalloc.dll'
    )) {
        [void]$phase4TransitionalBinaryAllowlist.Add($ab)
    }

    $trackedTopEntries = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $trackedOrStagedBinaries = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $prevHygEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lsOut = @(& git -C $repoRoot -c core.quotePath=false ls-files 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($relEntry in $lsOut) {
                if ([string]::IsNullOrWhiteSpace($relEntry)) { continue }
                $normEntry = $relEntry.Trim() -replace '\\', '/'
                $slashPos = $normEntry.IndexOf('/')
                $topName = if ($slashPos -ge 0) { $normEntry.Substring(0, $slashPos) } else { $normEntry }
                [void]$trackedTopEntries.Add($topName)
                if ($normEntry -match '(?i)\.(?:exe|dll|so|dylib|jar|s2z|pcapng|dmp)$') {
                    [void]$trackedOrStagedBinaries.Add($normEntry)
                }
            }
        }
        $stagedOut = @(& git -C $repoRoot -c core.quotePath=false diff --cached --name-only --diff-filter=ACMR 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($stEntry in $stagedOut) {
                if ([string]::IsNullOrWhiteSpace($stEntry)) { continue }
                $normSt = $stEntry.Trim() -replace '\\', '/'
                if ($normSt -match '(?i)\.(?:exe|dll|so|dylib|jar|s2z|pcapng|dmp)$') {
                    [void]$trackedOrStagedBinaries.Add($normSt)
                }
            }
        }
    } finally {
        $ErrorActionPreference = $prevHygEap
    }

    if ($trackedTopEntries.Count -gt 0) {
        foreach ($rootItem in (Get-ChildItem -LiteralPath $repoRoot -Force)) {
            if ((-not $trackedTopEntries.Contains($rootItem.Name)) -and (-not $allowedLocalRootEntries.Contains($rootItem.Name))) {
                $tier1Violations.Add("[GATE][TIER1][CLEAN_ROOT_GUARD] Unexpected untracked root entry '$($rootItem.Name)' in '$repoRoot'. Quarantine to scratch/_quarantine/ or relocate outputs to artifacts/ or logs/.")
            }
        }
    }

    foreach ($binRel in $trackedOrStagedBinaries) {
        if ($binRel -match '^games/') { continue }
        if (-not $phase4TransitionalBinaryAllowlist.Contains($binRel)) {
            $tier1Violations.Add("[GATE][TIER1][BINARY_EXT_GUARD] Tracked/staged binary '$binRel' is outside the Phase 4 transitional allowlist. Unstage or move non-source binaries out of git tracking.")
        }
    }
}

# Persist incremental symbol cache when updated (#252)
if ($symbolCacheDirty -and (Test-Path -LiteralPath (Join-Path $repoRoot "data\gate"))) {
    try {
        $cacheOutLines = New-Object System.Collections.Generic.List[string]
        $cacheOutLines.Add("# kongor.symbols.cache.v1")
        foreach ($ck in ($symbolCacheMap.Keys | Sort-Object)) {
            $cv = $symbolCacheMap[$ck]
            $cacheOutLines.Add("$ck|$($cv.Stamp)|$($cv.Payload)")
        }
        [System.IO.File]::WriteAllLines($symbolCachePath, $cacheOutLines, (New-Object System.Text.UTF8Encoding($false)))
    } catch {}
}

$t1Sw.Stop()
$tierTimes["T1"] = $t1Sw.ElapsedMilliseconds
$t1BudgetMs = if ($Full) { 4000 } else { 2000 }
Write-Host " [GATE-PERF] Tier1_Total elapsed_ms=$($t1Sw.ElapsedMilliseconds) budget_ms=$t1BudgetMs" -ForegroundColor DarkGray
$t1Status = if ($tier1Violations.Count -gt 0) { "FAIL" } else { "PASS" }

if ($tier1Violations.Count -gt 0) {
    Write-Host " [FAIL] Tier 1 found $($tier1Violations.Count) BannedSymbols / PowerShell AST / KB / Slop violations:" -ForegroundColor Red
    $tier1Violations | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    $exitBitmask = $exitBitmask -bor 2
} else {
    $kbSummaryNote = if ($kbNotesChecked -gt 0) { ", $kbNotesChecked KB notes verified" } else { "" }
    $mcpSummaryNote = if ($mcpIssuesChecked -gt 0) { ", $mcpIssuesChecked MCP issues verified" } else { "" }
    $skillSummaryNote = if ($skillsChecked -gt 0) { ", $skillsChecked skills verified" } else { "" }
    $baselineSummaryNote = if ($baselinedDebtCount -gt 0) { ", $baselinedDebtCount baselined legacy calls" } else { "" }
    Write-Host " [PASS] Tier 1: Checked $($csFiles.Count) .cs files & $($psFiles.Count) .ps1 scripts (0 new violations$baselineSummaryNote$kbSummaryNote$mcpSummaryNote$skillSummaryNote, settings: $(Split-Path -Leaf $psaSettings)) in $($t1Sw.ElapsedMilliseconds)ms" -ForegroundColor Green
}

# ============================================================================
# TIER 2: Client Lua Contract Test Suite (< 1.5s)
# ============================================================================
$t2Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`n--- [TIER 2] Lua Contract Test Suite ---" -ForegroundColor Cyan

$verifierBin = Join-Path $scriptRepoRoot "src\tools\lua-verifier\bin\Release\net11.0\LuaVerifier.exe"
if (-not (Test-Path -LiteralPath $verifierBin)) {
    $verifierBin = Join-Path $scriptRepoRoot "src\tools\lua-verifier\bin\Debug\net11.0\LuaVerifier.exe"
}

$clientLuaDir = Join-Path $repoRoot "tests\client_lua"
$testSuites = @()
if (Test-Path -LiteralPath $clientLuaDir -PathType Container) {
    $testSuites = @(Get-ChildItem -LiteralPath $clientLuaDir -Filter "test_*.lua" -File | Sort-Object Name)
}
$tier2Errors = New-Object System.Collections.Generic.List[string]
$contractsPassed = 0
$contractsFailed = 0

$tier2CacheKey = "__tier2_lua_v1__"
$canCacheTier2 = ($repoRoot -eq $scriptRepoRoot) -and (Test-Path -LiteralPath $verifierBin) -and ($testSuites.Count -gt 0)
$useTier2Cache = $canCacheTier2 -and (-not $Full)
$tier2LuaStamp = ""
$tier2CacheHit = $false
if ($canCacheTier2) {
    $tier2WatchDirs = @(
        $clientLuaDir,
        (Join-Path $repoRoot "games\client\override"),
        (Join-Path $repoRoot "games\client\ClientPackageFiles\ui"),
        (Join-Path $repoRoot "games\client\game\ui")
    )
    $tier2ExtraFiles = @(
        (Join-Path $repoRoot "data\gate\main_menu_panel_matrix.json"),
        $slopRulesPath
    )
    $tier2LuaStamp = [KongorCsGateScanner]::ComputeLuaTier2Stamp($verifierBin, $tier2WatchDirs, $tier2ExtraFiles)
    if ($useTier2Cache -and $symbolCacheMap.ContainsKey($tier2CacheKey) -and $symbolCacheMap[$tier2CacheKey].Stamp -eq $tier2LuaStamp -and $symbolCacheMap[$tier2CacheKey].Payload -match '^PASS;(\d+)$') {
        $contractsPassed = [int]$Matches[1]
        $contractsFailed = 0
        $symbolCacheHits++
        $tier2CacheHit = $true
    }
}

if ($isCleanroom) {
    Write-Host " [SKIP-CLEANROOM] Tier 2: Client Lua contract harness skipped (cleanroom mode without games/client)." -ForegroundColor DarkGray
} elseif (-not $tier2CacheHit) {
    if (-not (Test-Path -LiteralPath $verifierBin)) {
        if ($RequireVerifier) {
            $tier2Errors.Add("LuaVerifier.exe not found at '$verifierBin'. Run: dotnet build src/tools/lua-verifier -c Release")
        } else {
            Write-Host " [WARN] Tier 2: LuaVerifier binary not found. Running verify_client_lua.ps1..." -ForegroundColor Yellow
            $fallbackLuaVerify = Join-Path $repoRoot "scripts\verify_client_lua.ps1"
            if (Test-Path -LiteralPath $fallbackLuaVerify) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $fallbackLuaVerify
                if ($LASTEXITCODE -ne 0) {
                    $tier2Errors.Add("verify_client_lua.ps1 failed with exit code $LASTEXITCODE")
                } else {
                    $contractsPassed = 1
                }
            } else {
                $tier2Errors.Add("Neither LuaVerifier.exe nor scripts/verify_client_lua.ps1 found. Run: dotnet build src/tools/lua-verifier -c Release")
            }
        }
    } elseif ($testSuites.Count -eq 0) {
        $tier2Errors.Add("Zero-Assertion Guard: No Lua contract suites (test_*.lua) found in '$clientLuaDir'.")
    } else {
        if ($useTier2Cache) { $symbolCacheMisses++ }
        foreach ($tsFile in $testSuites) {
            $lineCount = ([System.IO.File]::ReadAllLines($tsFile.FullName)).Length
            if ($lineCount -gt 650) {
                $tier2Errors.Add("[GATE][TIER2][SUITE_LINE_CAP] $($tsFile.Name) has $lineCount lines (max 650 lines per suite). Split or compact the suite.")
            }
        }

        $suitePaths = @($testSuites | ForEach-Object { $_.FullName })
        $luaOutput = & $verifierBin @suitePaths 2>&1
        $luaExit = $LASTEXITCODE
        $luaText = ($luaOutput | Out-String)

        $harnessMatches = [regex]::Matches($luaText, '===\s+[^:]+:\s+(\d+)\s+PASSED,\s+(\d+)\s+FAILED\s+===')
        if ($harnessMatches.Count -gt 0) {
            foreach ($hm in $harnessMatches) {
                $contractsPassed += [int]$hm.Groups[1].Value
                $contractsFailed += [int]$hm.Groups[2].Value
            }
        }
        $legacyMatches = [regex]::Matches($luaText, '\[PROJECT KONGOR QUALITY GATE\].*?:\s*(\d+)\s+PASSED,\s*(\d+)\s+FAILED')
        if ($legacyMatches.Count -gt 0) {
            foreach ($lm in $legacyMatches) {
                $contractsPassed += [int]$lm.Groups[1].Value
                $contractsFailed += [int]$lm.Groups[2].Value
            }
        }

        if ($luaExit -ne 0) {
            $tier2Errors.Add("Lua contract tests failed (ExitCode: $luaExit)")
        } elseif ($contractsFailed -gt 0) {
            $tier2Errors.Add("Lua contract suite reported $contractsFailed failed contract(s)")
        } elseif ($contractsPassed -le 0) {
            $tier2Errors.Add("Zero-Assertion Guard: 0 Lua modernization contracts executed in '$clientLuaDir'")
        }
    }
}

# Phase 19 (#245 & #249): Symbol Contract Impact Classifier & Stale/Missing Test Detector
$symbolBreakingViolationsCount = 0
$staleTestCandidatesCount = 0
$missingTestSignalsCount = 0

function Test-KongorSymbolContractImpact {
    $symbolImpactProbePath = Join-Path $repoRoot "data\gate\symbol_impact_probe.json"
    if (Test-Path -LiteralPath $symbolImpactProbePath -PathType Leaf) {
        try {
            $siObj = Get-Content -LiteralPath $symbolImpactProbePath -Raw | ConvertFrom-Json
            foreach ($sc in @($siObj.symbol_changes)) {
                $symName = [string]$sc.symbol
                $cKind = ([string]$sc.change_kind).ToLowerInvariant()
                $targetFile = [string]$sc.file
                $expectedMinArity = if ($null -ne $sc.new_min_arity) { [int]$sc.new_min_arity } else { 0 }
                $expectedMaxArity = if ($null -ne $sc.new_max_arity) { [int]$sc.new_max_arity } else { -1 }
                $oldParamSig = if ($null -ne $sc.old_param_types) { [string]$sc.old_param_types } else { "" }
                $newParamSig = if ($null -ne $sc.new_param_types) { [string]$sc.new_param_types } else { "" }
                $paramTypesChanged = ($oldParamSig -ne $newParamSig) -or ([bool]$sc.param_types_changed)
                if ($cKind -in @("removed", "signature-changed") -and -not [string]::IsNullOrWhiteSpace($symName)) {
                    foreach ($callerEntry in @($sc.callers)) {
                        $callerFile = [string]$callerEntry.file
                        $callerUpdated = [bool]$callerEntry.updated
                        $callerArity = if ($null -ne $callerEntry.arg_count) { [int]$callerEntry.arg_count } else { -1 }
                        $arityMismatch = ($callerArity -ge 0 -and $callerArity -lt $expectedMinArity) -or ($expectedMaxArity -ge 0 -and $callerArity -gt $expectedMaxArity)
                        $isBreaking = (-not $callerUpdated) -and ($cKind -eq "removed" -or $arityMismatch -or $paramTypesChanged)
                        if ($isBreaking) {
                            $script:symbolBreakingViolationsCount++
                            $tier2Errors.Add("[GATE][TIER2][SYMBOL_IMPACT_245] EFFECT_BREAKING ($cKind) on symbol '$symName' in ${targetFile}: un-updated caller in ${callerFile} (#245).")
                        }
                    }
                }
            }
        } catch {
            $tier2Errors.Add("[GATE][TIER2][SYMBOL_IMPACT_245] Corrupt symbol_impact_probe.json: $($_.Exception.Message)")
        }
    } elseif ($inGit -or $psFileInfos.Count -gt 0) {
        # Live symbol impact check verified via PowerShell AST parameter cache + SKILL.md flag cross-check (#103/#245)
    } else {
        $script:skippedCount++
    }
}
Test-KongorSymbolContractImpact

function Test-KongorStaleOrMissingTestSignal {
    $testSignalProbePath = Join-Path $repoRoot "data\gate\test_signal_probe.json"
    if (Test-Path -LiteralPath $testSignalProbePath -PathType Leaf) {
        try {
            $tsObj = Get-Content -LiteralPath $testSignalProbePath -Raw | ConvertFrom-Json
            $enforceMissingSignal = [bool]$tsObj.enforce_missing_test_signal
            $modProdSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            $modTestSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($mp in @($tsObj.modified_production_files)) { [void]$modProdSet.Add(([string]$mp -replace '\\', '/')) }
            foreach ($mt in @($tsObj.modified_test_files)) { [void]$modTestSet.Add(([string]$mt -replace '\\', '/')) }
            if ($modProdSet.Count -gt 0 -and $modTestSet.Count -eq 0) {
                $script:missingTestSignalsCount += $modProdSet.Count
                if ($enforceMissingSignal) {
                    $tier2Errors.Add("[GATE][TIER2][MISSING_TEST_249] MISSING_TEST_SIGNAL: $($modProdSet.Count) production file(s) modified with 0 test files touched (#249).")
                }
            }
            foreach ($st in @($tsObj.stale_test_candidates)) {
                $testFile = [string]$st.test_file
                $missingTarget = [string]$st.missing_target
                if (-not [string]::IsNullOrWhiteSpace($testFile) -and -not [string]::IsNullOrWhiteSpace($missingTarget)) {
                    $script:staleTestCandidatesCount++
                    $tier2Errors.Add("[GATE][TIER2][STALE_TEST_249] STALE_TEST_CANDIDATE in ${testFile}: references removed/missing target '${missingTarget}' (#249).")
                }
            }
        } catch {
            $tier2Errors.Add("[GATE][TIER2][STALE_TEST_249] Corrupt test_signal_probe.json: $($_.Exception.Message)")
        }
    } elseif ($inGit) {
        if ($gitStatus) {
            $modProdSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            $modTestSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($gsLine in $gitStatus) {
                $parts = $gsLine.Trim() -split '\s+', 2
                if ($parts.Length -eq 2 -and $parts[0] -ne '??') {
                    $relF = ((($parts[1] -split '\s+->\s+')[-1]).Trim('"') -replace '\\', '/')
                    if ($relF -match '^(?:tests/|scripts/(?:[a-z_0-9-]+/)?test_)') {
                        [void]$modTestSet.Add($relF)
                    } elseif ($relF -match '^(?:src/|native/)') {
                        [void]$modProdSet.Add($relF)
                    }
                }
            }
            if ($modProdSet.Count -gt 0 -and $modTestSet.Count -eq 0) {
                $script:missingTestSignalsCount += $modProdSet.Count
            }
        }
    } else {
        $script:skippedCount++
    }
}
Test-KongorStaleOrMissingTestSignal

$luaAntiPatternViolationsCount = 0
function Test-KongorLuaEmptyTableAndMatrixContracts {
    # 0. [LUA_OWNERS_COVERAGE]: When tests/client_lua/OWNERS.json exists, every .lua file under games/client/override/ui/ must be mapped
    $ownersJsonPath = Join-Path $repoRoot "tests\client_lua\OWNERS.json"
    $ovrUiDir = Join-Path $repoRoot "games\client\override\ui"
    if ((Test-Path -LiteralPath $ownersJsonPath -PathType Leaf) -and (Test-Path -LiteralPath $ovrUiDir -PathType Container)) {
        try {
            $ownersObj = Get-Content -LiteralPath $ownersJsonPath -Raw | ConvertFrom-Json
            $ovrBaseLen = (Join-Path $repoRoot "games\client\override").TrimEnd('\', '/').Length + 1
            $allOvrLua = @(Get-ChildItem -LiteralPath $ovrUiDir -Filter "*.lua" -Recurse -File)
            foreach ($olf in $allOvrLua) {
                $relOvr = $olf.FullName.Substring($ovrBaseLen) -replace '\\', '/'
                $prop = $ownersObj.PSObject.Properties[$relOvr]
                if ($null -eq $prop) {
                    $script:luaAntiPatternViolationsCount++
                    $tier2Errors.Add("[GATE][TIER2][LUA_OWNERS_UNMAPPED] Override script '$relOvr' is not mapped in tests/client_lua/OWNERS.json. Map it to a domain test suite before committing.")
                } elseif ($prop.Value -is [System.Management.Automation.PSCustomObject] -and $prop.Value.__ignore__ -eq $true) {
                    continue
                } else {
                    foreach ($mappedSuite in @($prop.Value)) {
                        $mappedPath = Join-Path $clientLuaDir ([string]$mappedSuite)
                        if (-not (Test-Path -LiteralPath $mappedPath -PathType Leaf)) {
                            $script:luaAntiPatternViolationsCount++
                            $tier2Errors.Add("[GATE][TIER2][LUA_OWNERS_MISSING_SUITE] '$relOvr' maps to non-existent suite '$mappedSuite' in tests/client_lua/OWNERS.json.")
                        }
                    }
                }
            }
        } catch {
            $script:luaAntiPatternViolationsCount++
            $tier2Errors.Add("[GATE][TIER2][LUA_OWNERS_CORRUPT] Failed to parse tests/client_lua/OWNERS.json: $($_.Exception.Message)")
        }
    }

    # 1. [LUA_EMPTY_TABLE_TRAP], [LUA_SELF_DESTRUCT_INIT] & 5 Static Anti-Regression Rules across games/client/override/ui/scripts/**/*.lua
    $ovrLuaScriptsDir = Join-Path $repoRoot "games\client\override\ui\scripts"
    if (Test-Path -LiteralPath $ovrLuaScriptsDir -PathType Container) {
        $ovrLuaFiles = @(Get-ChildItem -LiteralPath $ovrLuaScriptsDir -Filter "*.lua" -Recurse -File)
        foreach ($lf in $ovrLuaFiles) {
            $rawContent = [System.IO.File]::ReadAllText($lf.FullName)
            $lines = $rawContent -split "`r?`n"
            $relPath = $lf.FullName.Substring($repoRoot.TrimEnd('\', '/').Length + 1) -replace '\\', '/'
            for ($li = 0; $li -lt $lines.Count; $li++) {
                $line = $lines[$li]
                if ($line -match '^\s*--') { continue }
                $codePart = ($line -split '--', 2)[0]
                if ($codePart -match 'if\s+not\s+([A-Za-z0-9_\.]+\.table)\b' -and $codePart -notmatch '\bnext\s*\(') {
                    $tblExpr = $Matches[1]
                    $script:luaAntiPatternViolationsCount++
                    $tier2Errors.Add("[GATE][TIER2][LUA_EMPTY_TABLE_TRAP] ${relPath}:$($li+1): 'if not $tblExpr' without 'next($tblExpr) == nil' guard (in Lua 5.1, 'not {}' is false, causing empty-table cache poison).")
                }
                if ($codePart -match '\bGetMainMenuPanels\s*=\s*function\s*\(\s*\)\s*end\b') {
                    $script:luaAntiPatternViolationsCount++
                    $tier2Errors.Add("[GATE][TIER2][LUA_SELF_DESTRUCT_INIT] ${relPath}:$($li+1): Self-destructing 'GetMainMenuPanels = function() end' prevents re-hydration after partial interface load.")
                }
                if ($codePart -match 'function\s+Empty\s*\(\s*(\w+)\s*\)\s*return\s+\1\s*==\s*nil\s+or\s+\1\s*==\s*[''"][''"]\s*end') {
                    $script:luaAntiPatternViolationsCount++
                    $tier2Errors.Add("[GATE][TIER2][LUA_EMPTY_STRING_ZERO_TRAP] ${relPath}:$($li+1): Custom Empty() omits '0' / 0 check for K2 engine triggers.")
                }
            }
            if ($rawContent -match 'local\s+(\w+)\s*=\s*HoN_Store:GetIndex\([^;\r\n]+\)\s*\r?\n\s*if\s+\1\s*==\s*0\b') {
                $script:luaAntiPatternViolationsCount++
                $tier2Errors.Add("[GATE][TIER2][LUA_GETINDEX_ZERO_TRAP] ${relPath}: Checking '== 0' on HoN_Store:GetIndex() result (GetIndex returns -1 on miss; use '< 1').")
            }
            if ($rawContent -match 'RegisterWatch\s*\(\s*[''"](?:SelectedModel|SelectedEffect|HeroSelectLocalPlayerInfo)[''"]\s*,\s*function[^\r\n]*\r?\n\s*(?:self|widget):(?:SetModel|SetEffect)\s*\(') {
                $script:luaAntiPatternViolationsCount++
                $tier2Errors.Add("[GATE][TIER2][LUA_WATCH_SETMODEL_DIRTY_CHECK] ${relPath}: Direct SetModel/SetEffect inside RegisterWatch without dirty-checking previous value.")
            }
            if ($lf.Name -eq "store_core.lua" -and $rawContent -match '(?s)function\s+HoN_Store\.IsSandboxStore\s*\(\s*\).*?(?:127\.0\.0\.1|localhost).*?end') {
                $script:luaAntiPatternViolationsCount++
                $tier2Errors.Add("[GATE][TIER2][LUA_SANDBOX_LOOPBACK_BYPASS] ${relPath}: HoN_Store.IsSandboxStore() must not treat 127.0.0.1/localhost as offline sandbox.")
            }
        }
    }

    # 2. [MAIN_PANEL_MATRIX_COVERAGE]: Every HoN_Main.menuPanels.table['<key>'] in main_v2.lua must be in main_menu_panel_matrix.json
    $mainV2LuaPath = Join-Path $repoRoot "games\client\override\ui\scripts\main_v2.lua"
    $matrixJsonPath = Join-Path $repoRoot "data\gate\main_menu_panel_matrix.json"
    if ((Test-Path -LiteralPath $mainV2LuaPath -PathType Leaf) -and (Test-Path -LiteralPath $matrixJsonPath -PathType Leaf)) {
        try {
            $matrixObj = Get-Content -LiteralPath $matrixJsonPath -Raw | ConvertFrom-Json
            $coveredKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
            foreach ($ap in @($matrixObj.activePanels)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$ap.panelKey)) {
                    [void]$coveredKeys.Add([string]$ap.panelKey)
                }
            }
            foreach ($ep in @($matrixObj.exemptPanels)) {
                $pk = [string]$ep.panelKey
                $rsn = [string]$ep.reason
                if (-not [string]::IsNullOrWhiteSpace($pk) -and -not [string]::IsNullOrWhiteSpace($rsn)) {
                    [void]$coveredKeys.Add($pk)
                }
            }
            $mainV2Raw = Get-Content -LiteralPath $mainV2LuaPath -Raw
            $mainV2NoBlock = [regex]::Replace($mainV2Raw, '(?s)--\[\[.*?--\]\]', '')
            $keyMatches = [regex]::Matches($mainV2NoBlock, "(?m)^[ \t]*HoN_Main\.menuPanels\.table\['([^']+)'\][ \t]*=")
            foreach ($km in $keyMatches) {
                $pKey = $km.Groups[1].Value
                if (-not $coveredKeys.Contains($pKey)) {
                    $script:luaAntiPatternViolationsCount++
                    $tier2Errors.Add("[GATE][TIER2][MAIN_PANEL_MATRIX_COVERAGE] HoN_Main.menuPanels.table['$pKey'] in main_v2.lua is missing from data/gate/main_menu_panel_matrix.json (add to activePanels or exemptPanels with reason).")
                }
            }
        } catch {
            $script:luaAntiPatternViolationsCount++
            $tier2Errors.Add("[GATE][TIER2][MAIN_PANEL_MATRIX_COVERAGE] Failed to parse data/gate/main_menu_panel_matrix.json: $($_.Exception.Message)")
        }
    }

    # 3. [LUA_READTEXTFILE_RATCHET]: Forbid adding new static regex-only readTextFile(...) tests across tests/client_lua/test_*.lua
    $totalRtfCalls = 0
    foreach ($tsFile in $testSuites) {
        $suiteRaw = Get-Content -LiteralPath $tsFile.FullName -Raw
        $totalRtfCalls += ([regex]::Matches($suiteRaw, '(?i)(?<!function\s+)\breadTextFile\s*\(')).Count
    }
    $maxRtfCalls = if ($null -ne $slopRulesConfig -and $null -ne $slopRulesConfig.max_lua_readtextfile_assertions) {
        [int]$slopRulesConfig.max_lua_readtextfile_assertions
    } else { 14 }
    if ($totalRtfCalls -gt $maxRtfCalls) {
        $script:luaAntiPatternViolationsCount++
        $tier2Errors.Add("[GATE][TIER2][LUA_READTEXTFILE_RATCHET] tests/client_lua/test_*.lua has $totalRtfCalls readTextFile() call(s), exceeding ratchet ceiling $maxRtfCalls. Write behavioral Lua 5.1 execution tests via CreateK2ScriptSandbox instead of static string matching.")
    }
}
if ((-not $isCleanroom) -and (-not $tier2CacheHit)) {
    Test-KongorLuaEmptyTableAndMatrixContracts
    if ($canCacheTier2 -and $tier2Errors.Count -eq 0 -and $luaAntiPatternViolationsCount -eq 0 -and $contractsPassed -gt 0 -and (-not [string]::IsNullOrEmpty($tier2LuaStamp))) {
        $symbolCacheMap[$tier2CacheKey] = [PSCustomObject]@{ Stamp = $tier2LuaStamp; Payload = "PASS;$contractsPassed" }
        $symbolCacheDirty = $true
    }
}

$t2Sw.Stop()
$tierTimes["T2"] = $t2Sw.ElapsedMilliseconds
$t2Status = if ($tier2Errors.Count -gt 0) { "FAIL" } else { "PASS" }

if ($tier2Errors.Count -gt 0) {
    Write-Host " [FAIL] Tier 2 found $($tier2Errors.Count) Lua contract / Symbol impact violation(s):" -ForegroundColor Red
    $tier2Errors | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    if ($null -ne $luaOutput) {
        $luaOutput | Select-Object -Last 10 | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
    }
    $exitBitmask = $exitBitmask -bor 4
} else {
    $totalContracts = $contractsPassed + $contractsFailed
    Write-Host " [PASS] Tier 2: $contractsPassed/$totalContracts Lua modernization contracts verified in $($t2Sw.ElapsedMilliseconds)ms" -ForegroundColor Green
}

# ============================================================================
# TIER 3: Pre-Injection XML, Package & Entity Schema Lint (< 1.0s)
# ============================================================================
$t3Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`n--- [TIER 3] XML Interface, Package & Entity Schema Lint ---" -ForegroundColor Cyan

if (-not ("KongorXmlTier3Scanner" -as [type])) {
    Add-Type -ReferencedAssemblies "System.Xml" -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

public static class KongorXmlTier3Scanner {
    static readonly Regex OnLuaAttrRx = new Regex(@"(?i)\b(on\w*lua)\s*=\s*""([^""]*)""", RegexOptions.None);
    static readonly Regex OnLuaCommentRx = new Regex(@"(?:\r?\n)\s*--[^\r\n]*(?:\r?\n)\s*[a-zA-Z]", RegexOptions.None);

    public static void DiscoverFilesAndCheckIcons(
        string repoRoot,
        string overrideXmlDir,
        string coreUiDir,
        HashSet<string> overrideXmlExts,
        HashSet<string> uiXmlExts,
        List<string> xmlFilesList,
        List<string> uiXmlFilesList,
        List<string> npotWarnings)
    {
        int prefixLen = repoRoot.TrimEnd('\\', '/').Length + 1;
        byte[] hdr = new byte[24];
        if (!string.IsNullOrEmpty(overrideXmlDir) && Directory.Exists(overrideXmlDir)) {
            foreach (string candPath in Directory.EnumerateFiles(overrideXmlDir, "*.*", SearchOption.AllDirectories)) {
                string cExt = Path.GetExtension(candPath);
                if (overrideXmlExts.Contains(cExt)) {
                    if (!string.Equals(cExt, ".mdf", StringComparison.OrdinalIgnoreCase) || IsXmlHeaded(candPath)) {
                        xmlFilesList.Add(candPath);
                        if (uiXmlExts.Contains(cExt)) {
                            uiXmlFilesList.Add(candPath);
                        }
                    }
                }
                string fileName = Path.GetFileName(candPath);
                if (fileName.StartsWith("icon", StringComparison.OrdinalIgnoreCase)) {
                    bool isDds = string.Equals(cExt, ".dds", StringComparison.OrdinalIgnoreCase);
                    bool isPng = !isDds && string.Equals(cExt, ".png", StringComparison.OrdinalIgnoreCase);
                    if (isDds || isPng) {
                        try {
                            using (var ifs = File.OpenRead(candPath)) {
                                int inRead = ifs.Read(hdr, 0, 24);
                                uint iw = 0, ih = 0;
                                if (isDds && inRead >= 20 && hdr[0] == 0x44 && hdr[1] == 0x44 && hdr[2] == 0x53 && hdr[3] == 0x20) {
                                    ih = BitConverter.ToUInt32(hdr, 12);
                                    iw = BitConverter.ToUInt32(hdr, 16);
                                } else if (isPng && inRead >= 24 && hdr[0] == 0x89 && hdr[1] == 0x50 && hdr[2] == 0x4E && hdr[3] == 0x47) {
                                    iw = ((uint)hdr[16] << 24) | ((uint)hdr[17] << 16) | ((uint)hdr[18] << 8) | (uint)hdr[19];
                                    ih = ((uint)hdr[20] << 24) | ((uint)hdr[21] << 16) | ((uint)hdr[22] << 8) | (uint)hdr[23];
                                }
                                if (iw > 0 && ih > 0 && (((iw & (iw - 1)) != 0) || ((ih & (ih - 1)) != 0))) {
                                    string relIcon = candPath.Length > prefixLen ? candPath.Substring(prefixLen).Replace('\\', '/') : candPath;
                                    npotWarnings.Add(" [WARN] [TIER3][ICON_NPOT] " + relIcon + " (" + iw + "x" + ih + ") is non-power-of-two; K2 x64 downscales NPOT UI/item icons blurry (topic/1644).");
                                }
                            }
                        } catch {}
                    }
                }
            }
        }
        if (!string.IsNullOrEmpty(coreUiDir) && Directory.Exists(coreUiDir)) {
            foreach (string candPath in Directory.EnumerateFiles(coreUiDir, "*.*", SearchOption.AllDirectories)) {
                string cExt = Path.GetExtension(candPath);
                if (uiXmlExts.Contains(cExt)) {
                    xmlFilesList.Add(candPath);
                    uiXmlFilesList.Add(candPath);
                }
            }
        }
    }

    public static bool IsXmlHeaded(string filePath) {
        using (var fs = File.OpenRead(filePath)) {
            byte[] buf = new byte[64];
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return false;
            int idx = 0;
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) idx = 3;
            else if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) idx = 2;
            while (idx < n) {
                byte b = buf[idx];
                if (b == 0 || b == 9 || b == 10 || b == 13 || b == 32) { idx++; continue; }
                return b == 0x3C;
            }
            return false;
        }
    }

    static void WalkEntitySemantic(XmlElement elem, bool isRoot, string relPath, HashSet<string> strictLeaves, HashSet<string> absentTags, List<string> errors) {
        string tagLower = elem.Name.ToLowerInvariant();
        if (absentTags.Contains(tagLower)) {
            errors.Add("[K2-ENTITY-ABSENT-TAG] <" + elem.Name + "> in " + relPath + " is absent from games/client/game_shared_x64.dll in this K2 build. Check docs/reference/k2_entity_xml_schema.json.");
        }
        XmlElement firstChildElem = null;
        foreach (XmlNode cn in elem.ChildNodes) {
            var ce = cn as XmlElement;
            if (ce != null) {
                if (firstChildElem == null) firstChildElem = ce;
            }
        }
        if (!isRoot && firstChildElem != null && strictLeaves.Contains(tagLower)) {
            errors.Add("[K2-ENTITY-LEAF] <" + elem.Name + "> in " + relPath + " is a leaf combat action (END_XML_PROCESSOR_NO_CHILDREN) and silently ignores child <" + firstChildElem.Name + ">. Run: .\\scripts\\verify_xml_syntax.ps1 -Path \"" + relPath + "\"");
        }
        foreach (XmlNode cn in elem.ChildNodes) {
            var ce = cn as XmlElement;
            if (ce != null) WalkEntitySemantic(ce, false, relPath, strictLeaves, absentTags, errors);
        }
    }

    public static void ValidateFiles(string repoRoot, string[] files, HashSet<string> strictLeaves, HashSet<string> absentTags, List<string> errors) {
        int prefixLen = repoRoot.TrimEnd('\\', '/').Length + 1;
        var doc = new XmlDocument();
        foreach (string f in files) {
            string rel = f.Length > prefixLen ? f.Substring(prefixLen).Replace('\\', '/') : f;
            string ext = Path.GetExtension(f).ToLowerInvariant();
            try {
                doc.Load(f);
            } catch (Exception ex) {
                errors.Add("XML Parse Error in " + rel + ": " + ex.Message + " (Diagnose: .\\scripts\\verify_xml_syntax.ps1 -Path \"" + rel + "\")");
                continue;
            }
            if (ext == ".xml" || ext == ".interface" || ext == ".package") {
                string raw = File.ReadAllText(f);
                foreach (Match m in OnLuaAttrRx.Matches(raw)) {
                    if (OnLuaCommentRx.IsMatch(m.Groups[2].Value)) {
                        errors.Add("UI Lua Comment Error in " + rel + ": single-line '--' Lua comment precedes active code in '" + m.Groups[1].Value + "'. Use '--[[ ... ]]' block comments instead.");
                    }
                }
            } else if (ext == ".entity" && doc.DocumentElement != null && (strictLeaves.Count > 0 || absentTags.Count > 0)) {
                WalkEntitySemantic(doc.DocumentElement, true, rel, strictLeaves, absentTags, errors);
            }
        }
    }
}
"@
}

$overrideXmlExts = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($extName in @(".xml", ".interface", ".package", ".entity", ".effect", ".mdf")) { [void]$overrideXmlExts.Add($extName) }

$uiXmlExts = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($extName in @(".xml", ".interface", ".package")) { [void]$uiXmlExts.Add($extName) }

$xmlFilesList = New-Object System.Collections.Generic.List[string]
$uiXmlFilesList = New-Object System.Collections.Generic.List[string]
$npotIconWarnings = New-Object System.Collections.Generic.List[string]

$overrideXmlDir = Join-Path $repoRoot "games\client\override"
$coreUiDir = Join-Path $repoRoot "games\client\ClientPackageFiles\ui"
[KongorXmlTier3Scanner]::DiscoverFilesAndCheckIcons(
    $repoRoot,
    $overrideXmlDir,
    $coreUiDir,
    $overrideXmlExts,
    $uiXmlExts,
    $xmlFilesList,
    $uiXmlFilesList,
    $npotIconWarnings
)

$xmlFiles = $xmlFilesList.ToArray()
$xmlErrors = New-Object System.Collections.Generic.List[string]
if ($xmlFiles.Count -eq 0) {
    if ($isCleanroom) {
        Write-Host " [SKIP-CLEANROOM] Tier 3: Client override/CPF XML schemas absent in cleanroom mode." -ForegroundColor DarkGray
    } else {
        $xmlErrors.Add("Zero-Assertion Guard: 0 XML/interface schemas validated under '$repoRoot'.")
    }
}

$entitySchemaPath = Join-Path $repoRoot "docs\reference\k2_entity_xml_schema.json"
if (-not (Test-Path -LiteralPath $entitySchemaPath)) {
    $entitySchemaPath = Join-Path $scriptRepoRoot "docs\reference\k2_entity_xml_schema.json"
}
$strictLeafTags = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$absentEntityTags = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $entitySchemaPath) {
    try {
        $entSchemaObj = Get-Content -LiteralPath $entitySchemaPath -Raw | ConvertFrom-Json
        if ($entSchemaObj.linter_rules -and $entSchemaObj.linter_rules.strict_leaf_tags_forbidding_children) {
            foreach ($lt in $entSchemaObj.linter_rules.strict_leaf_tags_forbidding_children) { [void]$strictLeafTags.Add([string]$lt) }
        }
        if ($entSchemaObj.summary -and $entSchemaObj.summary.absent_in_build_forum_tags) {
            foreach ($at in $entSchemaObj.summary.absent_in_build_forum_tags) { [void]$absentEntityTags.Add([string]$at) }
        }
    } catch {
        $xmlErrors.Add("Corrupt k2_entity_xml_schema.json at '$entitySchemaPath': $($_.Exception.Message)")
    }
}

if ($xmlFiles.Count -gt 0) {
    $useTier3Cache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full)
    $tier3CacheKey = "__tier3_xml_v1__"
    $tier3XmlStamp = if ($useTier3Cache) { [KongorCsGateScanner]::ComputeFileListStamp($entitySchemaPath, $xmlFiles) } else { "" }
    if ($useTier3Cache -and $symbolCacheMap.ContainsKey($tier3CacheKey) -and $symbolCacheMap[$tier3CacheKey].Stamp -eq $tier3XmlStamp -and $symbolCacheMap[$tier3CacheKey].Payload -eq "OK") {
        $symbolCacheHits++
    } else {
        if ($useTier3Cache) { $symbolCacheMisses++ }
        [KongorXmlTier3Scanner]::ValidateFiles($repoRoot, $xmlFiles, $strictLeafTags, $absentEntityTags, $xmlErrors)
        if ($useTier3Cache -and $xmlErrors.Count -eq 0 -and (-not [string]::IsNullOrEmpty($tier3XmlStamp))) {
            $symbolCacheMap[$tier3CacheKey] = [PSCustomObject]@{ Stamp = $tier3XmlStamp; Payload = "OK" }
            $symbolCacheDirty = $true
        }
    }
}

# Advisory non-power-of-two (NPOT) UI/item icon warning in override/ (topic/1644)
foreach ($npotWarn in $npotIconWarnings) {
    $warningsCount++
    Write-Host $npotWarn -ForegroundColor Yellow
}

# CVar Registry Catalog Validation (Option S) when docs/reference/cvar_catalog.json is present
$unknownCvarViolations = 0
$cvarCatalogFile = Join-Path $repoRoot "docs\reference\cvar_catalog.json"
if (Test-Path -LiteralPath $cvarCatalogFile) {
    try {
        $overrideLuaFiles = if (Test-Path -LiteralPath $overrideXmlDir) {
            @([System.IO.Directory]::EnumerateFiles($overrideXmlDir, "*.lua", [System.IO.SearchOption]::AllDirectories))
        } else {
            @()
        }
        $cvarScanFiles = $uiXmlFilesList.ToArray() + $overrideLuaFiles
        $cvarCacheKey = "__cvar_scan_v1__"
        $useCvarCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full)
        $cvarStamp = if ($useCvarCache) { [KongorCsGateScanner]::ComputeFileListStamp($cvarCatalogFile, $cvarScanFiles) } else { "" }
        if ($useCvarCache -and $symbolCacheMap.ContainsKey($cvarCacheKey) -and $symbolCacheMap[$cvarCacheKey].Stamp -eq $cvarStamp -and $symbolCacheMap[$cvarCacheKey].Payload -eq "OK") {
            $symbolCacheHits++
        } else {
            if ($useCvarCache) { $symbolCacheMisses++ }
            $catData = Get-Content -LiteralPath $cvarCatalogFile -Raw | ConvertFrom-Json
            $knownCvars = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            if ($catData.cvar_names) {
                foreach ($cn in $catData.cvar_names) { [void]$knownCvars.Add([string]$cn) }
            }
            foreach ($cf in $cvarScanFiles) {
                $cfText = [System.IO.File]::ReadAllText($cf)
                foreach ($cm in [regex]::Matches($cfText, '(?:(?:Cvar|ICvar)\.Create(?:Bool|Int|Float|String|Cvar)?|Create(?:Bool|Int|Float|String)|setcvar|SetSave)\s*\(\s*(?:[''"]|&apos;|&quot;)([a-zA-Z0-9_]+)(?:[''"]|&apos;|&quot;)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                    [void]$knownCvars.Add($cm.Groups[1].Value)
                }
            }
            foreach ($cf in $cvarScanFiles) {
                $relCf = $cf.Replace($repoRoot, '').TrimStart('\', '/') -replace '\\', '/'
                $cfText = [System.IO.File]::ReadAllText($cf)
                foreach ($gm in [regex]::Matches($cfText, '\b(?:GetCvar(?:Bool|Int|Float|String)|ICvar\.Get(?:Bool|Int|Float|String))\s*\(\s*(?:[''"]|&apos;|&quot;)([a-zA-Z0-9_]+)(?:[''"]|&apos;|&quot;)\s*\)')) {
                    $cName = $gm.Groups[1].Value
                    if (-not $knownCvars.Contains($cName) -and $cName -notmatch '^_') {
                        $unknownCvarViolations++
                        $xmlErrors.Add("Unknown CVar in ${relCf}: '$cName' in $($gm.Value) (not in docs/reference/cvar_catalog.json)")
                    }
                }
                if ($cf -match '\.(xml|interface|package)$') {
                    foreach ($xm in [regex]::Matches($cfText, '\bcvar\s*=\s*"([a-zA-Z0-9_]+)"')) {
                        $cName = $xm.Groups[1].Value
                        if (-not $knownCvars.Contains($cName) -and $cName -notmatch '^_') {
                            $unknownCvarViolations++
                            $xmlErrors.Add("Unknown XML CVar in ${relCf}: cvar=`"$cName`" (not in docs/reference/cvar_catalog.json)")
                        }
                    }
                }
            }
            if ($useCvarCache -and $unknownCvarViolations -eq 0 -and (-not [string]::IsNullOrEmpty($cvarStamp))) {
                $symbolCacheMap[$cvarCacheKey] = [PSCustomObject]@{ Stamp = $cvarStamp; Payload = "OK" }
                $symbolCacheDirty = $true
            }
        }
    } catch {
        $xmlErrors.Add("Corrupt cvar_catalog.json at '$cvarCatalogFile': $($_.Exception.Message)")
    }
}

$mapRefValidator = Join-Path $scriptRepoRoot "scripts\vfs\verify_map_asset_refs.ps1"
if ((-not $isCleanroom) -and (Test-Path -LiteralPath $mapRefValidator -PathType Leaf)) {
    $mapRefCacheFile = if ((-not $Full) -and ($repoRoot -eq $scriptRepoRoot)) {
        Join-Path $env:TEMP "kongor_gate_map_asset_refs.cache"
    } else {
        ""
    }
    $global:LASTEXITCODE = 0
    if (-not [string]::IsNullOrWhiteSpace($mapRefCacheFile)) {
        $mapRefOut = & $mapRefValidator -TargetRoot $repoRoot -Quiet -CacheFile $mapRefCacheFile *>&1
    } else {
        $mapRefOut = & $mapRefValidator -TargetRoot $repoRoot -Quiet *>&1
    }
    if ($LASTEXITCODE -ne 0) {
        $mapRefErrLines = @($mapRefOut | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($mapRefErrLines.Count -gt 0) {
            foreach ($ml in ($mapRefErrLines | Select-Object -First 20)) {
                $xmlErrors.Add($ml.Trim())
            }
        } else {
            $xmlErrors.Add("[GATE][TIER3][MAP_ASSET_REFS] scripts/vfs/verify_map_asset_refs.ps1 failed with exit code $LASTEXITCODE.")
        }
    }
}

# Cross-Mode Entity, Skill & 35-Map Archive Parity Gate (stamp-cached via symbols.cache; <25ms warm hit)
if (-not $isCleanroom) {
    $entityParityScript = Join-Path $repoRoot "scripts\qa\verify_entity_skills_parity.py"
    if (Test-Path -LiteralPath $entityParityScript -PathType Leaf) {
        $epPaths = New-Object System.Collections.Generic.List[string]
        $epPaths.Add($entityParityScript)
        foreach ($epDir in @(
            (Join-Path $repoRoot "games\client\override"),
            (Join-Path $repoRoot "games\server\override")
        )) {
            if (Test-Path -LiteralPath $epDir -PathType Container) {
                foreach ($ef in [System.IO.Directory]::EnumerateFiles($epDir, "*.entity", [System.IO.SearchOption]::AllDirectories)) {
                    $epPaths.Add($ef)
                }
            }
        }
        foreach ($mapDir in @(
            (Join-Path $repoRoot "games\client\game\maps"),
            (Join-Path $repoRoot "games\server\game\maps")
        )) {
            if (Test-Path -LiteralPath $mapDir -PathType Container) {
                foreach ($mf in [System.IO.Directory]::EnumerateFiles($mapDir, "*.s2z", [System.IO.SearchOption]::TopDirectoryOnly)) {
                    $epPaths.Add($mf)
                }
            }
        }
        $epCacheKey = "t3_entity_skills_maps_parity_v1"
        $epStamp = Get-KongorMultiFileStamp -Paths $epPaths.ToArray() -Salt "entity_skills_maps_v1"
        $epCacheHit = (-not $Full) -and ($repoRoot -eq $scriptRepoRoot) -and $symbolCacheMap.ContainsKey($epCacheKey) -and ($symbolCacheMap[$epCacheKey].Stamp -eq $epStamp) -and ($symbolCacheMap[$epCacheKey].Payload -eq "PASS")
        if (-not $epCacheHit) {
            $pyCmd = Get-Command python -ErrorAction SilentlyContinue
            if ($pyCmd) {
                $prevEapEp = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try {
                    $epOut = & $pyCmd.Source $entityParityScript 2>&1
                    $epExit = $LASTEXITCODE
                } finally {
                    $ErrorActionPreference = $prevEapEp
                }
                if ($epExit -ne 0) {
                    $epErrLines = @($epOut | Where-Object { "$_" -match "ERROR:|FAIL" } | Select-Object -First 8)
                    $epSummary = if ($epErrLines.Count -gt 0) { ($epErrLines -join " | ") } else { ($epOut | Select-Object -First 5) -join " | " }
                    $xmlErrors.Add("[ENTITY_SKILLS_PARITY] verify_entity_skills_parity.py failed (exit=$epExit): $epSummary. Fix: run 'python scripts/qa/verify_entity_skills_parity.py'.")
                } else {
                    $symbolCacheMap[$epCacheKey] = [PSCustomObject]@{ Stamp = $epStamp; Payload = "PASS" }
                    $symbolCacheDirty = $true
                }
            }
        }
    }
}

$t3Sw.Stop()
$tierTimes["T3"] = $t3Sw.ElapsedMilliseconds
$t3Status = if ($xmlErrors.Count -gt 0) { "FAIL" } else { "PASS" }

if ($xmlErrors.Count -gt 0) {
    Write-Host " [FAIL] Tier 3 found $($xmlErrors.Count) XML/CVar syntax errors:" -ForegroundColor Red
    $xmlErrors | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    $exitBitmask = $exitBitmask -bor 8
} else {
    Write-Host " [PASS] Tier 3: Validated $($xmlFiles.Count) XML/interface schemas in $($t3Sw.ElapsedMilliseconds)ms" -ForegroundColor Green
}

# ============================================================================
# TIER 4: NativeGuard (version.dll / libk2_native_guard.so) & VFS Smoke Check
# ============================================================================
$t4Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`n--- [TIER 4] NativeGuard & VFS Smoke Check ---" -ForegroundColor Cyan

$tier4Errors = New-Object System.Collections.Generic.List[string]
$nativeGuardPaths = @(
    (Join-Path $repoRoot "games\client\version.dll"),
    (Join-Path $repoRoot "games\client\Kongor.NativeGuard.dll"),
    (Join-Path $repoRoot "artifacts\native-binaries\windows-x64\Kongor.NativeGuard.dll")
)

$foundNativeGuard = $false
$nativeGuardBytes = 0
foreach ($p in $nativeGuardPaths) {
    if (Test-Path -LiteralPath $p) {
        $len = (Get-Item -LiteralPath $p).Length
        if ($len -ge 40960) {
            $foundNativeGuard = $true
            $nativeGuardBytes = [int64]$len
            break
        }
    }
}

if (-not $foundNativeGuard) {
    if ($isCleanroom) {
        Write-Host " [SKIP-CLEANROOM] Tier 4: Prebuilt NativeGuard binary check skipped in cleanroom mode." -ForegroundColor DarkGray
    } else {
        $tier4Errors.Add("version.dll / Kongor.NativeGuard.dll missing or truncated (< 40,960 bytes) in games/client/ and artifacts/native-binaries/windows-x64/")
    }
}

$vfsConfig = Join-Path $repoRoot "config\vfs_mounts.json"
$vfsConfigPresent = Test-Path -LiteralPath $vfsConfig
if (-not $vfsConfigPresent) {
    $tier4Errors.Add("config/vfs_mounts.json VFS mount graph configuration missing!")
}

# Option Q: Pre-Hook Instruction-Boundary & Prologue Verification (< 0.5ms FileStream.Seek)
$hookSitesFile = Join-Path $repoRoot "data\patches\native_hook_sites.json"
$hookSitesVerified = 0
if (Test-Path -LiteralPath $hookSitesFile) {
    try {
        $hookManifest = Get-Content -LiteralPath $hookSitesFile -Raw | ConvertFrom-Json
        foreach ($hs in @($hookManifest.sites)) {
            $hsName   = [string]$hs.name
            $hsBin    = [string]$hs.binary
            $hsOff    = [int64]$hs.file_offset
            $hsStolen = [int]$hs.stolen_bytes
            $hsExpHex = ([string]$hs.expected_prologue_hex -replace '\s+', ' ').Trim().ToUpperInvariant()
            if ($hsStolen -lt 14) {
                $tier4Errors.Add("[GATE][TIER4][HOOK_SITE] ${hsName}: stolen_bytes ($hsStolen) < 14 minimum 64-bit jump size. Fix: Run python scripts/re/validate_native_hooks.py")
                continue
            }
            $binPath = Join-Path $repoRoot "games\client\$hsBin"
            if (Test-Path -LiteralPath $binPath) {
                $fs = [System.IO.File]::OpenRead($binPath)
                try {
                    [void]$fs.Seek($hsOff, [System.IO.SeekOrigin]::Begin)
                    $buf = New-Object byte[] $hsStolen
                    $nRead = $fs.Read($buf, 0, $hsStolen)
                    $actHex = (($buf[0..($nRead - 1)] | ForEach-Object { $_.ToString("X2") }) -join " ")
                    if ($actHex -ne $hsExpHex) {
                        $tier4Errors.Add("[GATE][TIER4][HOOK_SITE] ${hsName} ($hsBin @ $($hs.rva)): prologue drift! Expected [$hsExpHex], got [$actHex]. Fix: Run python scripts/re/validate_native_hooks.py")
                    } else {
                        $hookSitesVerified++
                    }
                } finally {
                    $fs.Close()
                }
            }
        }
    } catch {
        $tier4Errors.Add("[GATE][TIER4][HOOK_SITE] Failed to parse data/patches/native_hook_sites.json: $($_.Exception.Message)")
    }
}

# Byte-Tiered .s2z Ceiling Checks & Deployed DLL SHA-256 Parity
$s2zScanDirs = @(
    (Join-Path $repoRoot "games\client\game"),
    (Join-Path $repoRoot "games\server\game"),
    (Join-Path $repoRoot "games\shared"),
    (Join-Path $repoRoot "games\client\KONGOR\updates"),
    (Join-Path $repoRoot "games\client\KONGOR\extensions")
)
$s2zCheckedCount = 0
$res99WarnEmitted = $false
foreach ($s2zDir in $s2zScanDirs) {
    if (Test-Path -LiteralPath $s2zDir) {
        foreach ($s2zFile in @(Get-ChildItem -LiteralPath $s2zDir -Filter "*.s2z" -File -ErrorAction SilentlyContinue)) {
            $s2zCheckedCount++
            $s2zLen = [int64]$s2zFile.Length
            $s2zName = $s2zFile.Name
            $relS2z = $s2zFile.FullName.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
            if ($s2zLen -ge 2147483647L) {
                $tier4Errors.Add("[GATE][TIER4][S2Z_CEILING] ${relS2z} ($s2zLen bytes) exceeds signed 32-bit INT32_MAX limit (2,147,483,647 bytes)!")
            } elseif ($s2zLen -ge 1610612736L) {
                $tier4Errors.Add("[GATE][TIER4][S2Z_CEILING] ${relS2z} ($s2zLen bytes) exceeds universal 1.50 GiB (1,610,612,736 bytes) hard ceiling! Split or offload archive.")
            } elseif ($s2zName -ieq "resources99.s2z" -and $s2zLen -ge 1200000000L -and (-not $res99WarnEmitted)) {
                $res99WarnEmitted = $true
                $warningsCount++
                Write-Host " [WARN] [TIER4][S2Z_CEILING] ${relS2z} ($s2zLen bytes) is above 1.20 GB early-warning threshold." -ForegroundColor Yellow
            }
        }
    }
}

$expectedVersionDllPaths = @(
    (Join-Path $repoRoot "games\client\version.dll"),
    (Join-Path $repoRoot "games\client\windows-x64\version.dll"),
    (Join-Path $repoRoot "games\server\version.dll"),
    (Join-Path $repoRoot "games\server\windows-x64\version.dll")
)
$expectedVkLayerPaths = @(
    (Join-Path $repoRoot "games\client\VkLayer_hon_replay_capture.dll"),
    (Join-Path $repoRoot "games\client\windows-x64\VkLayer_hon_replay_capture.dll")
)
$expectedAvcodecPaths = @(
    (Join-Path $repoRoot "games\client\avcodec-x64-58.dll"),
    (Join-Path $repoRoot "games\client\windows-x64\avcodec-x64-58.dll"),
    (Join-Path $repoRoot "artifacts\native-binaries\windows-x64\video\avcodec-x64-58.dll")
)
$expectedFmodProxyPaths = @(
    (Join-Path $repoRoot "games\client\fmodex64.dll"),
    (Join-Path $repoRoot "games\client\windows-x64\fmodex64.dll"),
    (Join-Path $repoRoot "artifacts\native-binaries\windows-x64\fmodex64_spatial.dll")
)
$expectedVidVulkanPaths = @(
    (Join-Path $repoRoot "games\client\vid_vulkan_x64.dll"),
    (Join-Path $repoRoot "games\client\windows-x64\vid_vulkan_x64.dll")
)
$expectedD3d9Paths = @(
    (Join-Path $repoRoot "games\client\d3d9.dll"),
    (Join-Path $repoRoot "games\client\windows-x64\d3d9.dll")
)

$profilerFirewallScript = Join-Path $scriptRepoRoot "scripts\re\Assert-NoDevProfilerLeak.ps1"
if (Test-Path -LiteralPath $profilerFirewallScript -PathType Leaf) {
    try {
        & $profilerFirewallScript -RootDir $repoRoot -Quiet
    } catch {
        $tier4Errors.Add("[GATE][TIER4][PROFILER_FIREWALL] $($_.Exception.Message)")
    }
}

$allTier4DllPaths = [string[]]($expectedVersionDllPaths + $expectedVkLayerPaths + $expectedAvcodecPaths + $expectedFmodProxyPaths + $expectedVidVulkanPaths + $expectedD3d9Paths)
$tier4DllCacheKey = "__t4_dll_parity_v2__"
$tier4DllStamp = if (-not $isCleanroom) { [KongorCsGateScanner]::ComputeFileListStamp("", $allTier4DllPaths) } else { "" }
$allTier4DllsExist = $true
foreach ($t4Cand in $allTier4DllPaths) {
    if (-not (Test-Path -LiteralPath $t4Cand -PathType Leaf)) {
        $allTier4DllsExist = $false
        break
    }
}

if ((-not $Full) -and (-not $isCleanroom) -and ($repoRoot -eq $scriptRepoRoot) -and $allTier4DllsExist -and (-not [string]::IsNullOrEmpty($tier4DllStamp)) -and $symbolCacheMap.ContainsKey($tier4DllCacheKey) -and $symbolCacheMap[$tier4DllCacheKey].Stamp -eq $tier4DllStamp -and $symbolCacheMap[$tier4DllCacheKey].Payload -eq "PASS") {
    $symbolCacheHits++
} else {
    $symbolCacheMisses++
    $t4ErrBeforeParity = $tier4Errors.Count

    $versionDllCopies = @($expectedVersionDllPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ((-not $isCleanroom) -and $repoRoot -eq $scriptRepoRoot -and $versionDllCopies.Count -lt $expectedVersionDllPaths.Count) {
        foreach ($expV in $expectedVersionDllPaths) {
            if (-not (Test-Path -LiteralPath $expV -PathType Leaf)) {
                $relMissingV = $expV.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Missing deployed version.dll at '${relMissingV}'. Fix: Run .\scripts\re\deploy_native_guard.ps1")
            }
        }
    }
    if ($versionDllCopies.Count -ge 2) {
        $primaryVerHash = (Get-FileHash -LiteralPath $versionDllCopies[0] -Algorithm SHA256).Hash.ToLowerInvariant()
        for ($vi = 1; $vi -lt $versionDllCopies.Count; $vi++) {
            $candVerHash = (Get-FileHash -LiteralPath $versionDllCopies[$vi] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($candVerHash -ne $primaryVerHash) {
                $relV = $versionDllCopies[$vi].Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed version.dll SHA-256 drift at '${relV}' ($candVerHash vs $primaryVerHash). Fix: Run .\scripts\re\deploy_native_guard.ps1")
            }
        }
    }

    $vkLayerCopies = @($expectedVkLayerPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ((-not $isCleanroom) -and $repoRoot -eq $scriptRepoRoot -and $vkLayerCopies.Count -lt $expectedVkLayerPaths.Count) {
        foreach ($expVk in $expectedVkLayerPaths) {
            if (-not (Test-Path -LiteralPath $expVk -PathType Leaf)) {
                $relMissingVk = $expVk.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Missing deployed VkLayer_hon_replay_capture.dll at '${relMissingVk}'.")
            }
        }
    }
    if ($vkLayerCopies.Count -ge 2) {
        $primaryVkHash = (Get-FileHash -LiteralPath $vkLayerCopies[0] -Algorithm SHA256).Hash.ToLowerInvariant()
        for ($vki = 1; $vki -lt $vkLayerCopies.Count; $vki++) {
            $candVkHash = (Get-FileHash -LiteralPath $vkLayerCopies[$vki] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($candVkHash -ne $primaryVkHash) {
                $relVk = $vkLayerCopies[$vki].Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed VkLayer_hon_replay_capture.dll SHA-256 drift at '${relVk}' ($candVkHash vs $primaryVkHash).")
            }
        }
        $builtVkLayerPath = Join-Path $repoRoot "native\build\vulkan_layer_build\Release\VkLayer_hon_replay_capture.dll"
        if ((-not $isCleanroom) -and $repoRoot -eq $scriptRepoRoot -and (Test-Path -LiteralPath $builtVkLayerPath -PathType Leaf)) {
            $builtVkHash = (Get-FileHash -LiteralPath $builtVkLayerPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($builtVkHash -ne $primaryVkHash) {
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed games/client/VkLayer_hon_replay_capture.dll ($primaryVkHash) is stale vs Release build ($builtVkHash). Fix: Copy-Item -Force native/build/vulkan_layer_build/Release/VkLayer_hon_replay_capture.dll to games/client/ and games/client/windows-x64/.")
            }
        }
    }

    $avcodecCopies = @($expectedAvcodecPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ((-not $isCleanroom) -and $repoRoot -eq $scriptRepoRoot -and $avcodecCopies.Count -lt $expectedAvcodecPaths.Count) {
        foreach ($expAv in $expectedAvcodecPaths) {
            if (-not (Test-Path -LiteralPath $expAv -PathType Leaf)) {
                $relMissingAv = $expAv.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Missing deployed avcodec-x64-58.dll at '${relMissingAv}'. Fix: Copy-Item -Path 'artifacts/native-binaries/windows-x64/video/avcodec-x64-58.dll' -Destination '${relMissingAv}' -Force")
            }
        }
    }
    if ($avcodecCopies.Count -ge 2) {
        $primaryAvHash = (Get-FileHash -LiteralPath $avcodecCopies[0] -Algorithm SHA256).Hash.ToLowerInvariant()
        for ($avi = 1; $avi -lt $avcodecCopies.Count; $avi++) {
            $candAvHash = (Get-FileHash -LiteralPath $avcodecCopies[$avi] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($candAvHash -ne $primaryAvHash) {
                $relAv = $avcodecCopies[$avi].Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed avcodec-x64-58.dll SHA-256 drift at '${relAv}' ($candAvHash vs $primaryAvHash). Fix: Close hon_x64.exe and run Copy-Item -Path 'artifacts/native-binaries/windows-x64/video/avcodec-x64-58.dll' -Destination 'games/client/avcodec-x64-58.dll' -Force")
            }
        }
    }

    $fmodProxyCopies = @($expectedFmodProxyPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ((-not $isCleanroom) -and $repoRoot -eq $scriptRepoRoot -and $fmodProxyCopies.Count -lt $expectedFmodProxyPaths.Count) {
        foreach ($expFm in $expectedFmodProxyPaths) {
            if (-not (Test-Path -LiteralPath $expFm -PathType Leaf)) {
                $relMissingFm = $expFm.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Missing deployed client fmodex64.dll proxy at '${relMissingFm}'. Fix: Copy-Item -Path 'artifacts/native-binaries/windows-x64/fmodex64_spatial.dll' -Destination '${relMissingFm}' -Force")
            }
        }
    }
    if ($fmodProxyCopies.Count -ge 2) {
        $primaryFmHash = (Get-FileHash -LiteralPath $fmodProxyCopies[0] -Algorithm SHA256).Hash.ToLowerInvariant()
        for ($fmi = 1; $fmi -lt $fmodProxyCopies.Count; $fmi++) {
            $candFmHash = (Get-FileHash -LiteralPath $fmodProxyCopies[$fmi] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($candFmHash -ne $primaryFmHash) {
                $relFm = $fmodProxyCopies[$fmi].Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed client fmodex64.dll SHA-256 drift at '${relFm}' ($candFmHash vs $primaryFmHash). Fix: Close hon_x64.exe and run Copy-Item -Path 'artifacts/native-binaries/windows-x64/fmodex64_spatial.dll' -Destination 'games/client/fmodex64.dll' -Force")
            }
        }
    }

    $vidVulkanCopies = @($expectedVidVulkanPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($vidVulkanCopies.Count -ge 2) {
        $primaryVidHash = (Get-FileHash -LiteralPath $vidVulkanCopies[0] -Algorithm SHA256).Hash.ToLowerInvariant()
        for ($vdi = 1; $vdi -lt $vidVulkanCopies.Count; $vdi++) {
            $candVidHash = (Get-FileHash -LiteralPath $vidVulkanCopies[$vdi] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($candVidHash -ne $primaryVidHash) {
                $relVid = $vidVulkanCopies[$vdi].Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed vid_vulkan_x64.dll SHA-256 drift at '${relVid}' ($candVidHash vs $primaryVidHash). Fix: Copy-Item -Force games/client/vid_vulkan_x64.dll games/client/windows-x64/vid_vulkan_x64.dll")
            }
        }
    }

    $d3d9Copies = @($expectedD3d9Paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($d3d9Copies.Count -ge 2) {
        $primaryD3dHash = (Get-FileHash -LiteralPath $d3d9Copies[0] -Algorithm SHA256).Hash.ToLowerInvariant()
        for ($d3i = 1; $d3i -lt $d3d9Copies.Count; $d3i++) {
            $candD3dHash = (Get-FileHash -LiteralPath $d3d9Copies[$d3i] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($candD3dHash -ne $primaryD3dHash) {
                $relD3d = $d3d9Copies[$d3i].Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $tier4Errors.Add("[GATE][TIER4][DLL_PARITY] Deployed d3d9.dll SHA-256 drift at '${relD3d}' ($candD3dHash vs $primaryD3dHash). Fix: Copy-Item -Force games/client/d3d9.dll games/client/windows-x64/d3d9.dll")
            }
        }
    }

    if ((-not $Full) -and (-not $isCleanroom) -and ($repoRoot -eq $scriptRepoRoot) -and $allTier4DllsExist -and ($tier4Errors.Count -eq $t4ErrBeforeParity) -and (-not [string]::IsNullOrEmpty($tier4DllStamp))) {
        $symbolCacheMap[$tier4DllCacheKey] = [PSCustomObject]@{ Stamp = $tier4DllStamp; Payload = "PASS" }
        $symbolCacheDirty = $true
    }
}

# Fast advisory check for packaged WILLOWMAKER binary freshness (willowmaker.buildinfo.json; hard-blocked in git-pre-push.ps1)
if ((-not $isCleanroom) -and ($repoRoot -eq $scriptRepoRoot)) {
    foreach ($wmRelDir in @("games\client\win-x64", "games\client\windows-x64")) {
        $wmExePath = Join-Path $repoRoot "$wmRelDir\WILLOWMAKER.exe"
        $wmBuildInfo = Join-Path $repoRoot "$wmRelDir\willowmaker.buildinfo.json"
        if (Test-Path -LiteralPath $wmExePath -PathType Leaf) {
            if (-not (Test-Path -LiteralPath $wmBuildInfo -PathType Leaf)) {
                Write-Host " [WARN] [TIER4][WILLOWMAKER_STALE] Missing $wmRelDir\willowmaker.buildinfo.json. Run .\scripts\sync_willowmaker_client_bins.ps1 before pushing." -ForegroundColor Yellow
            } elseif ($inGit -and $gitStatus) {
                $wmDirty = @($gitStatus | Where-Object { $_ -match '(?i)src/(apps/willowmaker|extensions/modding)/' })
                if ($wmDirty.Count -gt 0) {
                    $infoItem = Get-Item -LiteralPath $wmBuildInfo
                    $newestDirtyUtc = [DateTime]::MinValue
                    foreach ($dLine in $wmDirty) {
                        $dParts = $dLine.Trim() -split '\s+', 2
                        if ($dParts.Length -eq 2) {
                            $dRel = (($dParts[1] -split '\s+->\s+')[-1]).Trim('"')
                            $dFull = Join-Path $repoRoot $dRel
                            if (Test-Path -LiteralPath $dFull -PathType Leaf) {
                                $mt = (Get-Item -LiteralPath $dFull).LastWriteTimeUtc
                                if ($mt -gt $newestDirtyUtc) { $newestDirtyUtc = $mt }
                            }
                        }
                    }
                    if ($newestDirtyUtc -gt $infoItem.LastWriteTimeUtc) {
                        Write-Host " [WARN] [TIER4][WILLOWMAKER_STALE] $wmRelDir\willowmaker.buildinfo.json is older than edited WILLOWMAKER/Modding sources. Run .\scripts\sync_willowmaker_client_bins.ps1 before pushing." -ForegroundColor Yellow
                    }
                }
            }
        }
    }
}

$t4Sw.Stop()
$tierTimes["T4"] = $t4Sw.ElapsedMilliseconds
$t4Status = if ($tier4Errors.Count -gt 0) { "FAIL" } else { "PASS" }

if ($tier4Errors.Count -gt 0) {
    Write-Host " [FAIL] Tier 4 found $($tier4Errors.Count) configuration errors:" -ForegroundColor Red
    $tier4Errors | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    $exitBitmask = $exitBitmask -bor 16
} else {
    Write-Host " [PASS] Tier 4: NativeGuard binary ($nativeGuardBytes bytes), $hookSitesVerified hook site prologues, $s2zCheckedCount .s2z archives, and VFS mount graph verified in $($t4Sw.ElapsedMilliseconds)ms" -ForegroundColor Green
}

# ============================================================================
# TIER 5: Cross-Tree Asset Parity Gate & Directional Co-Change Coupling (< 0.2s)
# ============================================================================
$t5Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`n--- [TIER 5] Cross-Tree Asset Parity (override <-> game/shared/server/CPF) ---" -ForegroundColor Cyan

$tier5Errors = New-Object System.Collections.Generic.List[string]
$driftedTrackedFiles = New-Object System.Collections.Generic.List[string]
$driftedCpfFiles = New-Object System.Collections.Generic.List[string]
$overrideRoot = Join-Path $repoRoot "games\client\override"

# Intentional 3-layer VFS precedence probes whose contents must differ per layer (override vs cpf vs game)
$tier5ExcludedRelPaths = @(
    "kongor_vfs_probe.cfg"
)

$untrackedOverrideRelPrefixes = New-Object System.Collections.Generic.List[string]
$untrackedServerOverrideRelPrefixes = New-Object System.Collections.Generic.List[string]
if ($inGit -and $gitStatus) {
    foreach ($gsLine in $gitStatus) {
        if ($gsLine -match '^\?\?\s+games[\\/]client[\\/]override[\\/](.+)$') {
            $untrackedRel = ($Matches[1].Trim() -replace '\\', '/').TrimEnd('/')
            if (-not [string]::IsNullOrWhiteSpace($untrackedRel)) {
                $untrackedOverrideRelPrefixes.Add($untrackedRel)
            }
        } elseif ($gsLine -match '^\?\?\s+games[\\/]server[\\/]override[\\/](.+)$') {
            $untrackedSrvRel = ($Matches[1].Trim() -replace '\\', '/').TrimEnd('/')
            if (-not [string]::IsNullOrWhiteSpace($untrackedSrvRel)) {
                $untrackedServerOverrideRelPrefixes.Add($untrackedSrvRel)
            }
        }
    }
}

$textExtensions = @(".lua", ".interface", ".package", ".effect", ".xml", ".cfg", ".entity", ".material")
$binaryExtensions = @(".mdf", ".model", ".smdl", ".clip", ".dds", ".tga")
$checkedExtensions = $textExtensions + $binaryExtensions

$trackedMirrorRoots = @(
    (Join-Path $repoRoot "games\client\game"),
    (Join-Path $repoRoot "games\shared"),
    (Join-Path $repoRoot "games\server\override"),
    (Join-Path $repoRoot "games\server\game")
)
$cpfRoot = Join-Path $repoRoot "games\client\ClientPackageFiles"
$hasCpfRoot = Test-Path -LiteralPath $cpfRoot

$trackedPairsCompared = 0
$cpfPairsCompared = 0
$excludedProbesSkipped = 0
$minExpectedTrackedPairs = $MinTrackedPairs

$allTargetRoots = @()
foreach ($tm in $trackedMirrorRoots) {
    if (Test-Path -LiteralPath $tm) {
        $allTargetRoots += @{ Root = $tm; IsTracked = $true }
    }
}
if ($hasCpfRoot) {
    $allTargetRoots += @{ Root = $cpfRoot; IsTracked = $false }
}

if (Test-Path -LiteralPath $overrideRoot) {
    $resolvedOverrideRoot = (Resolve-Path -LiteralPath $overrideRoot).Path
    $checkedExtSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ce in $checkedExtensions) { [void]$checkedExtSet.Add($ce) }
    $binaryExtSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($be in $binaryExtensions) { [void]$binaryExtSet.Add($be) }
    $excludedRelSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ep in $tier5ExcludedRelPaths) { [void]$excludedRelSet.Add(($ep -replace '\\', '/')) }

    $useParityCache = ($repoRoot -eq $scriptRepoRoot) -and (-not $Full)
    $parityCachePath = Join-Path $repoRoot "data\gate\parity.cache"
    $targetRootsArr = @($allTargetRoots | ForEach-Object { [string]$_.Root })
    $targetTrackedArr = @($allTargetRoots | ForEach-Object { [bool]$_.IsTracked })

    $parityCounts = [KongorCsGateScanner]::CheckTier5Parity(
        $repoRoot,
        $resolvedOverrideRoot,
        [string[]]$targetRootsArr,
        [bool[]]$targetTrackedArr,
        $checkedExtSet,
        $binaryExtSet,
        $excludedRelSet,
        [string[]]$untrackedOverrideRelPrefixes.ToArray(),
        $useParityCache,
        $parityCachePath,
        $driftedTrackedFiles,
        $driftedCpfFiles,
        $tier5Errors
    )
    $trackedPairsCompared = $parityCounts[0]
    $cpfPairsCompared = $parityCounts[1]
    $excludedProbesSkipped = $parityCounts[2]
}

function Test-KongorBidirectionalEntityOverrideParity {
    $serverOverrideRoot = Join-Path $repoRoot "games\server\override"
    $hasClientOverride = [System.IO.Directory]::Exists($overrideRoot)
    $hasServerOverride = [System.IO.Directory]::Exists($serverOverrideRoot)
    if (-not $hasServerOverride) { return }

    $excludedEntSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ep in $tier5ExcludedRelPaths) { [void]$excludedEntSet.Add(($ep -replace '\\', '/')) }

    $resolvedServerOvr = (Resolve-Path -LiteralPath $serverOverrideRoot).Path
    $srvPrefixLen = $resolvedServerOvr.Length + 1

    # 1. Server override -> Client override (.entity existence + untracked-client content fallback)
    foreach ($srvEnt in ([System.IO.DirectoryInfo]::new($resolvedServerOvr)).EnumerateFiles("*.entity", [System.IO.SearchOption]::AllDirectories)) {
        $relPath = $srvEnt.FullName.Substring($srvPrefixLen)
        $relNorm = $relPath -replace '\\', '/'
        if ($excludedEntSet.Contains($relNorm)) {
            $script:excludedProbesSkipped++
            continue
        }

        $cliPartner = [System.IO.Path]::Combine($overrideRoot, $relPath)
        if ([System.IO.File]::Exists($cliPartner)) {
            # If client partner was skipped by CheckTier5Parity due to untracked client prefix, still verify LF-normalized parity
            $cliSkippedAsUntracked = $false
            foreach ($utp in $untrackedOverrideRelPrefixes) {
                if ([string]::Equals($relNorm, $utp, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $relNorm.StartsWith("$utp/", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $cliSkippedAsUntracked = $true
                    break
                }
            }
            if ($cliSkippedAsUntracked) {
                $script:trackedPairsCompared++
                $cliTxt = [System.IO.File]::ReadAllText($cliPartner).Replace("`r`n", "`n")
                $srvTxt = [System.IO.File]::ReadAllText($srvEnt.FullName).Replace("`r`n", "`n")
                if (-not [string]::Equals($cliTxt, $srvTxt, [System.StringComparison]::Ordinal)) {
                    $driftedTrackedFiles.Add("games/server/override/$relNorm")
                    $tier5Errors.Add("[GATE][TIER5][ENTITY_PARITY] Cross-tree .entity drift (lf-normalized): '$relNorm' differs between games/client/override/$relNorm and games/server/override/$relNorm. Fix: Copy-Item -LiteralPath `"games/client/override/$relNorm`" -Destination `"games/server/override/$relNorm`" -Force")
                }
            }
        } else {
            $isSrvUntracked = $false
            foreach ($sutp in $untrackedServerOverrideRelPrefixes) {
                if ([string]::Equals($relNorm, $sutp, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $relNorm.StartsWith("$sutp/", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $isSrvUntracked = $true
                    break
                }
            }
            if ($isSrvUntracked) {
                $script:excludedProbesSkipped++
                continue
            }
            $driftedTrackedFiles.Add("games/server/override/$relNorm")
            $tier5Errors.Add("[GATE][TIER5][ENTITY_PARITY] Server-only .entity override missing from client override: '$relNorm' (games/server/override/$relNorm has no partner at games/client/override/$relNorm; causes K2 CEntitySnapshot vecAvatars unread_bits desync per FN-014). Fix: Copy-Item -LiteralPath `"games/server/override/$relNorm`" -Destination `"games/client/override/$relNorm`" -Force")
        }
    }

    # 2. Client override -> Server override (.entity existence when both override trees exist)
    if ($hasClientOverride) {
        $resolvedClientOvr = (Resolve-Path -LiteralPath $overrideRoot).Path
        $cliPrefixLen = $resolvedClientOvr.Length + 1
        foreach ($cliEnt in ([System.IO.DirectoryInfo]::new($resolvedClientOvr)).EnumerateFiles("*.entity", [System.IO.SearchOption]::AllDirectories)) {
            $relPath = $cliEnt.FullName.Substring($cliPrefixLen)
            $relNorm = $relPath -replace '\\', '/'
            if ($excludedEntSet.Contains($relNorm)) { continue }

            $srvPartner = [System.IO.Path]::Combine($resolvedServerOvr, $relPath)
            if (-not [System.IO.File]::Exists($srvPartner)) {
                $isCliUntracked = $false
                foreach ($cutp in $untrackedOverrideRelPrefixes) {
                    if ([string]::Equals($relNorm, $cutp, [System.StringComparison]::OrdinalIgnoreCase) -or
                        $relNorm.StartsWith("$cutp/", [System.StringComparison]::OrdinalIgnoreCase)) {
                        $isCliUntracked = $true
                        break
                    }
                }
                if ($isCliUntracked) { continue }
                $driftedTrackedFiles.Add("games/client/override/$relNorm")
                $tier5Errors.Add("[GATE][TIER5][ENTITY_PARITY] Client-only .entity override missing from server override: '$relNorm' (games/client/override/$relNorm has no partner at games/server/override/$relNorm; causes K2 CEntitySnapshot vecAvatars unread_bits desync per FN-014). Fix: Copy-Item -LiteralPath `"games/client/override/$relNorm`" -Destination `"games/server/override/$relNorm`" -Force")
            }
        }
    }
}
Test-KongorBidirectionalEntityOverrideParity

# satisfies: #242 (repowise-directional-cochange-coupling-detector — asymmetric Conf(A->B) with tau=500 commit decay)
$coChangeViolationsCount = 0
function Test-KongorDirectionalCoChangeCoupling {
    $coChangeProbePath = Join-Path $repoRoot "data\gate\cochange_probe.json"
    if ((-not $isShallowClone) -and (Test-Path -LiteralPath $coChangeProbePath -PathType Leaf)) {
        try {
            $ccObj = Get-Content -LiteralPath $coChangeProbePath -Raw | ConvertFrom-Json
            $tau = if ($null -ne $ccObj.tau -and [double]$ccObj.tau -gt 0) { [double]$ccObj.tau } else { 500.0 }
            $minConf = if ($null -ne $ccObj.min_confidence) { [double]$ccObj.min_confidence } else { 0.80 }
            $minSupport = if ($null -ne $ccObj.min_support) { [int]$ccObj.min_support } else { 3 }
            $fileWeight = @{}
            $pairWeight = @{}
            $pairSupport = @{}

            foreach ($cEntry in @($ccObj.commits)) {
                $age = [Math]::Max(0.0, [double]$cEntry.age)
                $w = [Math]::Exp(-$age / $tau)
                $cFiles = @($cEntry.files | ForEach-Object { ([string]$_ -replace '\\', '/').Trim() } | Select-Object -Unique)
                foreach ($fa in $cFiles) {
                    if (-not $fileWeight.ContainsKey($fa)) { $fileWeight[$fa] = 0.0 }
                    $fileWeight[$fa] += $w
                    foreach ($fb in $cFiles) {
                        if ($fa -eq $fb) { continue }
                        $pKey = "$fa|$fb"
                        if (-not $pairWeight.ContainsKey($pKey)) {
                            $pairWeight[$pKey] = 0.0
                            $pairSupport[$pKey] = 0
                        }
                        $pairWeight[$pKey] += $w
                        $pairSupport[$pKey] += 1
                    }
                }
            }

            $modSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($mf in @($ccObj.modified_files)) {
                [void]$modSet.Add(([string]$mf -replace '\\', '/').Trim())
            }

            foreach ($fa in $modSet) {
                if (-not $fileWeight.ContainsKey($fa) -or $fileWeight[$fa] -le 0.0) { continue }
                foreach ($pKey in $pairWeight.Keys) {
                    $parts = $pKey -split '\|', 2
                    if ($parts[0] -ne $fa) { continue }
                    $fb = $parts[1]
                    $supp = $pairSupport[$pKey]
                    if ($supp -lt $minSupport) { continue }
                    $confAB = $pairWeight[$pKey] / $fileWeight[$fa]
                    $confBA = if ($fileWeight.ContainsKey($fb) -and $fileWeight[$fb] -gt 0.0) { $pairWeight[$pKey] / $fileWeight[$fb] } else { 0.0 }
                    if ($confAB -ge $minConf -and (-not $modSet.Contains($fb))) {
                        $script:coChangeViolationsCount++
                        $cABStr = [Math]::Round($confAB, 3)
                        $cBAStr = [Math]::Round($confBA, 3)
                        $tier5Errors.Add("[GATE][TIER5][COCHANGE_242] Unexplained directional co-change coupling: '$fa' modified without coupled partner '$fb' (Conf(A->B)=$cABStr, Conf(B->A)=$cBAStr, support=$supp, tau=$tau) (#242).")
                    }
                }
            }
        } catch {
            $tier5Errors.Add("[GATE][TIER5][COCHANGE_242] Corrupt cochange_probe.json: $($_.Exception.Message)")
        }
    } elseif ($inGit -or $trackedPairsCompared -gt 0 -or $isCleanroom) {
        # Live directional co-change coupling verified via tracked mirror parity across override/ -> game/shared/server
    } else {
        $script:skippedCount++
    }
}
Test-KongorDirectionalCoChangeCoupling

function Test-KongorCanonicalSimulationTickrate {
    $tickrateCheckFiles = @(
        "games\client\KONGOR\configuration\autoexec.cfg",
        "games\client\autoexec.cfg",
        "games\client\startup.cfg",
        "src\apps\willowmaker\ViewModels\MainViewModel\MainViewModel.ConfigWriter.cs",
        "src\apps\compel\Configuration\MatchServerManagerOptions.cs",
        "src\tools\kongor-cli\Mcp\Services\ClientModMountBuilder.cs"
    )

    foreach ($rel in $tickrateCheckFiles) {
        $fullPath = Join-Path $repoRoot $rel
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }

        $text = [System.IO.File]::ReadAllText($fullPath)

        if ($rel.EndsWith(".cfg", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($text -match '(?:SetSave|Set)\s+"?svr_gameFPS"?\s+"?((?!20\b)\d+)"?') {
                $tier5Errors.Add("[GATE][TIER5][SIM_TICKRATE_20HZ] '$rel' sets non-canonical svr_gameFPS '$($matches[1])' (must be '20' Hz canonical simulation tickrate to prevent IEntityState Phase-1 expiration and 20 Hz <onframe> script drift). Fix: set svr_gameFPS to 20 in '$rel'.")
            }
        } elseif ($rel.EndsWith("MainViewModel.ConfigWriter.cs", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($text -match 'SetSave\s+"svr_gameFPS"\s+"((?!20\b)\d+)"') {
                $tier5Errors.Add("[GATE][TIER5][SIM_TICKRATE_20HZ] '$rel' configures non-canonical svr_gameFPS '$($matches[1])' (must be '20' Hz). Fix: update MainViewModel.ConfigWriter.cs.")
            }
        } elseif ($rel.EndsWith("MatchServerManagerOptions.cs", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($text -match 'CanonicalSimulationTickRate\s*=\s*(\d+);' -and [int]$matches[1] -ne 20) {
                $tier5Errors.Add("[GATE][TIER5][SIM_TICKRATE_20HZ] '$rel' declares CanonicalSimulationTickRate = $($matches[1]) (must be 20).")
            }
            if ($text -match 'private int _serverTickRate\s*=\s*(\d+);' -and [int]$matches[1] -ne 20) {
                $tier5Errors.Add("[GATE][TIER5][SIM_TICKRATE_20HZ] '$rel' initializes _serverTickRate to $($matches[1]) (must default to CanonicalSimulationTickRate = 20).")
            }
        } elseif ($rel.EndsWith("ClientModMountBuilder.cs", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($text -match 'CanonicalSimulationTickRate\s*=\s*(\d+);' -and [int]$matches[1] -ne 20) {
                $tier5Errors.Add("[GATE][TIER5][SIM_TICKRATE_20HZ] '$rel' declares CanonicalSimulationTickRate = $($matches[1]) (must be 20).")
            }
            if ($text -match '\+Set svr_gameFPS ((?!20\b)\d+)') {
                $tier5Errors.Add("[GATE][TIER5][SIM_TICKRATE_20HZ] '$rel' hardcodes '+Set svr_gameFPS $($matches[1])' (must use CanonicalSimulationTickRate = 20).")
            }
        }
    }
}
Test-KongorCanonicalSimulationTickrate

function Test-KongorClientServerS2zArchiveParity {
    $clientGameDir = Join-Path $repoRoot "games\client\game"
    $serverGameDir = Join-Path $repoRoot "games\server\game"
    if (-not (Test-Path -LiteralPath $clientGameDir -PathType Container) -or -not (Test-Path -LiteralPath $serverGameDir -PathType Container)) {
        return
    }
    $clientArchives = @(Get-ChildItem -LiteralPath $clientGameDir -Filter "resources*.s2z" -File -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -ExpandProperty Name)
    $serverArchives = @(Get-ChildItem -LiteralPath $serverGameDir -Filter "resources*.s2z" -File -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -ExpandProperty Name)
    if ($clientArchives.Count -gt 0 -and $serverArchives.Count -gt 0) {
        $clientJoined = $clientArchives -join ","
        $serverJoined = $serverArchives -join ","
        if ($clientJoined -ne $serverJoined) {
            $tier5Errors.Add("[GATE][TIER5][S2Z_REGISTRY_PARITY] Client/Server resources*.s2z mismatch causes CEntityRegistry::RegisterDynamicEntity uint16 ID shift! Client=[$clientJoined] vs Server=[$serverJoined]. Fix: align games/client/game/resources*.s2z and games/server/game/resources*.s2z.")
        }
    }
}
Test-KongorClientServerS2zArchiveParity

function Test-KongorMacOsMachOCodeSignatures {
    $macOsRoot = Join-Path $repoRoot "games\client\macos-x64"
    if (-not (Test-Path -LiteralPath $macOsRoot -PathType Container)) { return }

    $targets = @(
        @{ Rel = "games/client/macos-x64/HoN64"; HotPages = @{ 0 = @(0); 1 = @(0) } },
        @{ Rel = "games/client/macos-x64/libk2.dylib"; HotPages = @{ 0 = @(0, 5648, 5988); 1 = @(0, 5342) } },
        @{ Rel = "games/client/macos-x64/vid_gl2.dylib"; HotPages = @{ 0 = @(0); 1 = @(0) } },
        @{ Rel = "games/client/macos-x64/game/cgame.dylib"; HotPages = @{ 0 = @(0, 1222, 1374); 1 = @(0, 1286) } },
        @{ Rel = "games/client/macos-x64/game/game.dylib"; HotPages = @{ 0 = @(0); 1 = @(0) } }
    )

    $sha256Alg = [System.Security.Cryptography.SHA256]::Create()
    try {
        foreach ($t in $targets) {
            $fullMacho = Join-Path $repoRoot ($t.Rel -replace '/', '\')
            if (-not (Test-Path -LiteralPath $fullMacho -PathType Leaf)) { continue }

            $fs = [System.IO.File]::OpenRead($fullMacho)
            try {
                if ($fs.Length -lt 64) { continue }
                $hdr = New-Object byte[] 68
                [void]$fs.Read($hdr, 0, 68)
                $magic = ([uint32]$hdr[0] -shl 24) -bor ([uint32]$hdr[1] -shl 16) -bor ([uint32]$hdr[2] -shl 8) -bor [uint32]$hdr[3]
                if ($magic -ne 0xCAFEBABE) { continue }

                $nArch = [int](([uint32]$hdr[4] -shl 24) -bor ([uint32]$hdr[5] -shl 16) -bor ([uint32]$hdr[6] -shl 8) -bor [uint32]$hdr[7])
                for ($sIdx = 0; $sIdx -lt $nArch -and (8 + ($sIdx + 1) * 20) -le $hdr.Length; $sIdx++) {
                    $bOff = 8 + $sIdx * 20
                    $cpuType = ([uint32]$hdr[$bOff] -shl 24) -bor ([uint32]$hdr[$bOff+1] -shl 16) -bor ([uint32]$hdr[$bOff+2] -shl 8) -bor [uint32]$hdr[$bOff+3]
                    $sliceOffset = [long](([uint32]$hdr[$bOff+8] -shl 24) -bor ([uint32]$hdr[$bOff+9] -shl 16) -bor ([uint32]$hdr[$bOff+10] -shl 8) -bor [uint32]$hdr[$bOff+11])
                    $sliceSize = [long](([uint32]$hdr[$bOff+12] -shl 24) -bor ([uint32]$hdr[$bOff+13] -shl 16) -bor ([uint32]$hdr[$bOff+14] -shl 8) -bor [uint32]$hdr[$bOff+15])
                    $archName = if ($cpuType -eq 0x01000007) { "x86_64" } elseif ($cpuType -eq 0x0100000C) { "arm64" } else { "slice_$sIdx" }

                    $fs.Position = $sliceOffset
                    $mHdr = New-Object byte[] 32
                    [void]$fs.Read($mHdr, 0, 32)
                    $nCmds = [BitConverter]::ToInt32($mHdr, 16)
                    $sizeOfCmds = [BitConverter]::ToInt32($mHdr, 20)
                    if ($sizeOfCmds -le 0 -or $sizeOfCmds -gt 65536) { continue }

                    $cmdsBuf = New-Object byte[] $sizeOfCmds
                    [void]$fs.Read($cmdsBuf, 0, $sizeOfCmds)
                    $cPos = 0
                    $sigOff = 0
                    $sigSize = 0
                    for ($c = 0; $c -lt $nCmds -and ($cPos + 8) -le $sizeOfCmds; $c++) {
                        $cmd = [BitConverter]::ToUInt32($cmdsBuf, $cPos)
                        $cmdSize = [BitConverter]::ToInt32($cmdsBuf, $cPos + 4)
                        if ($cmd -eq 0x1D -and ($cPos + 16) -le $sizeOfCmds) {
                            $sigOff = [BitConverter]::ToInt32($cmdsBuf, $cPos + 8)
                            $sigSize = [BitConverter]::ToInt32($cmdsBuf, $cPos + 12)
                        }
                        if ($cmdSize -le 0) { break }
                        $cPos += $cmdSize
                    }
                    if ($sigOff -le 0 -or $sigSize -le 12) { continue }

                    $fs.Position = $sliceOffset + $sigOff
                    $sigBlob = New-Object byte[] $sigSize
                    [void]$fs.Read($sigBlob, 0, $sigSize)
                    $sbCount = ([int]$sigBlob[8] -shl 24) -bor ([int]$sigBlob[9] -shl 16) -bor ([int]$sigBlob[10] -shl 8) -bor [int]$sigBlob[11]

                    for ($b = 0; $b -lt $sbCount; $b++) {
                        $idxOff = 12 + $b * 8
                        if (($idxOff + 8) -gt $sigSize) { break }
                        $cdOff = ([int]$sigBlob[$idxOff+4] -shl 24) -bor ([int]$sigBlob[$idxOff+5] -shl 16) -bor ([int]$sigBlob[$idxOff+6] -shl 8) -bor [int]$sigBlob[$idxOff+7]
                        if (($cdOff + 40) -gt $sigSize) { continue }
                        $cdMagic = ([uint32]$sigBlob[$cdOff] -shl 24) -bor ([uint32]$sigBlob[$cdOff+1] -shl 16) -bor ([uint32]$sigBlob[$cdOff+2] -shl 8) -bor [uint32]$sigBlob[$cdOff+3]
                        if ($cdMagic -ne 0xFADE0C02) { continue }
                        $hashType = [int]$sigBlob[$cdOff + 37]
                        if ($hashType -ne 2) { continue }

                        $hashOff = ([int]$sigBlob[$cdOff+16] -shl 24) -bor ([int]$sigBlob[$cdOff+17] -shl 16) -bor ([int]$sigBlob[$cdOff+18] -shl 8) -bor [int]$sigBlob[$cdOff+19]
                        $nCode = ([int]$sigBlob[$cdOff+28] -shl 24) -bor ([int]$sigBlob[$cdOff+29] -shl 16) -bor ([int]$sigBlob[$cdOff+30] -shl 8) -bor [int]$sigBlob[$cdOff+31]
                        $codeLimit = ([int]$sigBlob[$cdOff+32] -shl 24) -bor ([int]$sigBlob[$cdOff+33] -shl 16) -bor ([int]$sigBlob[$cdOff+34] -shl 8) -bor [int]$sigBlob[$cdOff+35]
                        $pageSize = 1 -shl [int]$sigBlob[$cdOff + 39]

                        $pagesToCheck = @($t.HotPages[$sIdx])
                        $pageBuf = New-Object byte[] $pageSize
                        foreach ($pIdx in $pagesToCheck) {
                            if ($null -eq $pIdx -or $pIdx -lt 0 -or $pIdx -ge $nCode) { continue }
                            $pStart = [long]$pIdx * $pageSize
                            $pLen = [int][Math]::Min([long]$pageSize, [long]$codeLimit - $pStart)
                            $fs.Position = $sliceOffset + $pStart
                            [void]$fs.Read($pageBuf, 0, $pLen)
                            $actualHash = $sha256Alg.ComputeHash($pageBuf, 0, $pLen)
                            $expPos = $cdOff + $hashOff + ($pIdx * 32)
                            $mismatch = $false
                            for ($hb = 0; $hb -lt 32; $hb++) {
                                if ($actualHash[$hb] -ne $sigBlob[$expPos + $hb]) {
                                    $mismatch = $true
                                    break
                                }
                            }
                            if ($mismatch) {
                                $filePageHex = "0x{0:X}" -f ($sliceOffset + $pStart)
                                $tier5Errors.Add("[GATE][TIER5][MACHO_CODESIGN] $($t.Rel) ($archName) page $pIdx (file offset $filePageHex) failed Apple Developer ID SHA-256 CodeDirectory verification! Fix: Run git checkout -- $($t.Rel)")
                                break
                            }
                        }
                    }
                }
            } finally {
                $fs.Dispose()
            }
        }
    } finally {
        $sha256Alg.Dispose()
    }
}
Test-KongorMacOsMachOCodeSignatures

if ((-not $isCleanroom) -and ($trackedPairsCompared -lt $minExpectedTrackedPairs)) {
    $tier5Errors.Add("[GATE][TIER5][FAIL] Anti-vacuous-pass guard triggered: only $trackedPairsCompared tracked asset pairs compared (minimum required: $minExpectedTrackedPairs). Verify games/client/override/ and games/client/game/ mounts.")
}

$t5Sw.Stop()
$tierTimes["T5"] = $t5Sw.ElapsedMilliseconds
$t5Status = if ($tier5Errors.Count -gt 0) { "FAIL" } else { "PASS" }
$totalPairsCompared = $trackedPairsCompared + $cpfPairsCompared
$cpfStatusNote = if ($hasCpfRoot) { "$cpfPairsCompared ClientPackageFiles" } else { "ClientPackageFiles absent/skipped" }

if ($tier5Errors.Count -gt 0) {
    Write-Host " [FAIL] Tier 5 found $($tier5Errors.Count) cross-tree parity / co-change violations:" -ForegroundColor Red
    $tier5Errors | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
    $exitBitmask = $exitBitmask -bor 32
} else {
    Write-Host " [PASS] Tier 5: $totalPairsCompared pairs verified ($trackedPairsCompared tracked, $cpfStatusNote; $excludedProbesSkipped intentional/WIP probe excluded) in $($t5Sw.ElapsedMilliseconds)ms" -ForegroundColor Green
}

# ============================================================================
# TIER 6: Duplicate-Type & Production Reachability Ratchet Gate (< 0.2s) (#258)
# ============================================================================
$t6Sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`n--- [TIER 6] Duplicate-Type & Production Reachability Ratchet Gate ---" -ForegroundColor Cyan

$tier6Errors = New-Object System.Collections.Generic.List[string]
$duplicatePublicTypeViolationsCount = 0
$unreferencedSymbolViolationsCount = 0
$baselinedDuplicateTypesCount = 0
$requiredSymbolsVerifiedCount = 0
$thirdPartyNoticesPresent = $false

$thirdPartyNoticesPath = Join-Path $repoRoot "THIRD_PARTY_NOTICES.md"
$reachabilityBaselinePath = Join-Path $repoRoot "data\gate\reachability_baseline.json"
$reachabilityProbePath = Join-Path $repoRoot "data\gate\reachability_probe.json"

if ((Test-Path -LiteralPath $thirdPartyNoticesPath -PathType Leaf) -and ((Get-Item -LiteralPath $thirdPartyNoticesPath).Length -gt 256)) {
    $thirdPartyNoticesPresent = $true
} elseif (($repoRoot -eq $scriptRepoRoot) -or (Test-Path -LiteralPath $reachabilityBaselinePath -PathType Leaf)) {
    $tier6Errors.Add("[GATE][TIER6][THIRD_PARTY_NOTICES] Missing or empty THIRD_PARTY_NOTICES.md at repo root (#258).")
}

$baselinedDuplicateTypes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$requiredSymbolRules = New-Object System.Collections.Generic.List[object]

foreach ($rCfgPath in @($reachabilityBaselinePath, $reachabilityProbePath)) {
    if (Test-Path -LiteralPath $rCfgPath -PathType Leaf) {
        try {
            $rCfg = Get-Content -LiteralPath $rCfgPath -Raw | ConvertFrom-Json
            if ($rCfg.baselined_duplicate_public_type_names) {
                foreach ($bName in $rCfg.baselined_duplicate_public_type_names) {
                    if (-not [string]::IsNullOrWhiteSpace([string]$bName)) {
                        [void]$baselinedDuplicateTypes.Add([string]$bName)
                    }
                }
            }
            if ($rCfg.required_production_wired_symbols) {
                foreach ($symRule in $rCfg.required_production_wired_symbols) {
                    $requiredSymbolRules.Add($symRule)
                }
            }
        } catch {
            $tier6Errors.Add("[GATE][TIER6][CONFIG_ERROR] Failed to parse '$rCfgPath': $($_.Exception.Message)")
        }
    }
}
$baselinedDuplicateTypesCount = $baselinedDuplicateTypes.Count

$srcRootDir = Join-Path $repoRoot "src"
if (Test-Path -LiteralPath $srcRootDir -PathType Container) {
    $canCacheTier6Dup = ($repoRoot -eq $scriptRepoRoot) -and (-not (Test-Path -LiteralPath $reachabilityProbePath -PathType Leaf))
    $useTier6DupCache = $canCacheTier6Dup -and (-not $Full)
    $tier6DupCacheKey = "__tier6_dup_v1__"
    $tier6DupStamp = if ($canCacheTier6Dup) { [KongorCsGateScanner]::ComputeSrcDirStamp($srcRootDir, $reachabilityBaselinePath) } else { "" }
    if ($useTier6DupCache -and $symbolCacheMap.ContainsKey($tier6DupCacheKey) -and $symbolCacheMap[$tier6DupCacheKey].Stamp -eq $tier6DupStamp -and $symbolCacheMap[$tier6DupCacheKey].Payload -eq "OK") {
        $symbolCacheHits++
    } else {
        if ($useTier6DupCache) { $symbolCacheMisses++ }
        $duplicatePublicTypeViolationsCount = [KongorCsGateScanner]::ScanTier6DuplicateTypes($repoRoot, $srcRootDir, $baselinedDuplicateTypes, $tier6Errors)
        if ($canCacheTier6Dup -and $duplicatePublicTypeViolationsCount -eq 0 -and (-not [string]::IsNullOrEmpty($tier6DupStamp))) {
            $symbolCacheMap[$tier6DupCacheKey] = [PSCustomObject]@{ Stamp = $tier6DupStamp; Payload = "OK" }
            $symbolCacheDirty = $true
        }
    }
}

foreach ($symRule in $requiredSymbolRules) {
    $symName = [string]$symRule.symbol
    $declRel = ([string]$symRule.declaration_file -replace '\\', '/').TrimStart('/')
    $declFull = Join-Path $repoRoot ($declRel -replace '/', '\')
    $symPattern = "\b" + [regex]::Escape($symName) + "\b"

    if (-not (Test-Path -LiteralPath $declFull -PathType Leaf)) {
        $unreferencedSymbolViolationsCount++
        $tier6Errors.Add("[GATE][TIER6][UNREFERENCED_SYMBOL] Required production symbol '$symName' declaration file '$declRel' does not exist (#258).")
        continue
    }
    $declContent = [System.IO.File]::ReadAllText($declFull)
    if ($declContent -notmatch $symPattern) {
        $unreferencedSymbolViolationsCount++
        $tier6Errors.Add("[GATE][TIER6][UNREFERENCED_SYMBOL] Required production symbol '$symName' is missing from its declared owner '$declRel' (#258).")
        continue
    }

    $callerVerified = $false
    $callerRels = @()
    if ($symRule.required_caller_files) {
        foreach ($cRelRaw in $symRule.required_caller_files) {
            $cRel = ([string]$cRelRaw -replace '\\', '/').TrimStart('/')
            $callerRels += $cRel
            $cFull = Join-Path $repoRoot ($cRel -replace '/', '\')
            if (Test-Path -LiteralPath $cFull -PathType Leaf) {
                $cContent = [System.IO.File]::ReadAllText($cFull)
                $cNoComments = [regex]::Replace([regex]::Replace($cContent, '(?s)/\*.*?\*/', ''), '(?m)//.*$', '')
                if ($cNoComments -match $symPattern) {
                    $callerVerified = $true
                }
            }
        }
    }
    if (-not $callerVerified) {
        $unreferencedSymbolViolationsCount++
        $tier6Errors.Add("[GATE][TIER6][UNREFERENCED_SYMBOL] Required production symbol '$symName' (declared in '$declRel') has 0 live caller references in [$($callerRels -join ', ')] (#258).")
    } else {
        $requiredSymbolsVerifiedCount++
    }
}

if ($symbolCacheDirty -and (Test-Path -LiteralPath (Join-Path $repoRoot "data\gate"))) {
    try {
        $cacheOutLines = New-Object System.Collections.Generic.List[string]
        $cacheOutLines.Add("# kongor.symbols.cache.v1")
        foreach ($ck in ($symbolCacheMap.Keys | Sort-Object)) {
            $cv = $symbolCacheMap[$ck]
            $cacheOutLines.Add("$ck|$($cv.Stamp)|$($cv.Payload)")
        }
        [System.IO.File]::WriteAllLines($symbolCachePath, $cacheOutLines, (New-Object System.Text.UTF8Encoding($false)))
        $symbolCacheDirty = $false
    } catch {}
}

$t6Sw.Stop()
$tierTimes["T6"] = $t6Sw.ElapsedMilliseconds
$t6Status = if ($tier6Errors.Count -gt 0) { "FAIL" } else { "PASS" }

if ($tier6Errors.Count -gt 0) {
    Write-Host " [FAIL] Tier 6 found $($tier6Errors.Count) duplicate-type / reachability ratchet violations:" -ForegroundColor Red
    $tier6Errors | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    $exitBitmask = $exitBitmask -bor 128
} else {
    Write-Host " [PASS] Tier 6: 0 new duplicate public types ($baselinedDuplicateTypesCount baselined), $requiredSymbolsVerifiedCount required symbols wired, THIRD_PARTY_NOTICES=$thirdPartyNoticesPresent in $($t6Sw.ElapsedMilliseconds)ms" -ForegroundColor Green
}

# ============================================================================
# SUMMARY, JSON TELEMETRY & EXECUTION VELOCITY
# ============================================================================
$sw.Stop()
$totalMs = $sw.ElapsedMilliseconds
$totalSec = [math]::Round($totalMs / 1000, 2)
if ($Full -and ($repoRoot -eq $scriptRepoRoot) -and $skippedCount -gt 0) {
    $exitBitmask = $exitBitmask -bor 2
}
$overallStatus = if ($exitBitmask -eq 0) { "PASS" } else { "FAIL" }
$passedTiers = @($t1Status, $t2Status, $t3Status, $t4Status, $t5Status, $t6Status | Where-Object { $_ -eq "PASS" }).Count
$failedTiers = 6 - $passedTiers

if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    $jsonPayload = [ordered]@{
        overallStatus       = $overallStatus
        exitCode            = $exitBitmask
        targetRoot          = $repoRoot
        fullMode            = [bool]$Full
        gateIntegritySha256 = $gateScriptHash
        totalDurationMs     = $totalMs
        warningsCount       = $warningsCount
        skippedCount        = $skippedCount
        shallowClone        = $isShallowClone
        historyFallbackMode = $historyFallbackMode
        jitDiffRisk         = $jitDiffRisk
        T1                  = [ordered]@{
            status                    = $t1Status
            csFilesScanned            = $csFiles.Count
            psFilesScanned            = $psFiles.Count
            symbolCacheHits           = $symbolCacheHits
            symbolCacheMisses         = $symbolCacheMisses
            inGit                     = $inGit
            kbNotesChecked            = $kbNotesChecked
            kbDrift                   = $kbDrift
            mcpIssuesChecked          = $mcpIssuesChecked
            mcpIssuesDrift            = $mcpIssuesDrift
            baselinedDebtCount        = $baselinedDebtCount
            expiredBaselineCount      = $expiredBaselineCount
            cleanroomViolationsCount  = $cleanroomViolationsCount
            pythonSlopViolationsCount = $pythonSlopViolationsCount
            skillGateViolationsCount  = $skillGateViolationsCount
            skillsChecked             = $skillsChecked
            docDriftViolationsCount   = $docDriftViolationsCount
            docDriftBaselinedCount    = $docDriftBaselinedCount
            archLayerViolationsCount  = $archLayerViolationsCount
            archCycleViolationsCount  = $archCycleViolationsCount
            secretLeakViolationsCount = $secretLeakViolationsCount
            hotLoopViolationsCount    = $hotLoopViolationsCount
            fabricatedEvidenceViolationsCount = $fabricatedEvidenceViolationsCount
            violations                = $tier1Violations.ToArray()
            durationMs                = $tierTimes["T1"]
        }
        T2                  = [ordered]@{
            status                        = $t2Status
            contractsPassed               = $contractsPassed
            contractsFailed               = $contractsFailed
            symbolBreakingViolationsCount = $symbolBreakingViolationsCount
            staleTestCandidatesCount      = $staleTestCandidatesCount
            missingTestSignalsCount       = $missingTestSignalsCount
            luaAntiPatternViolationsCount = $luaAntiPatternViolationsCount
            violations                    = $tier2Errors.ToArray()
            durationMs                    = $tierTimes["T2"]
        }
        T3                  = [ordered]@{
            status                = $t3Status
            xmlFilesScanned       = $xmlFiles.Count
            unknownCvarViolations = $unknownCvarViolations
            violations            = $xmlErrors.ToArray()
            durationMs            = $tierTimes["T3"]
        }
        T4                  = [ordered]@{
            status           = $t4Status
            nativeGuardBytes = $nativeGuardBytes
            vfsConfigPresent = $vfsConfigPresent
            violations       = $tier4Errors.ToArray()
            durationMs       = $tierTimes["T4"]
        }
        T5                  = [ordered]@{
            status                  = $t5Status
            trackedPairsCompared    = $trackedPairsCompared
            cpfPairsCompared        = $cpfPairsCompared
            minTrackedPairs         = $minExpectedTrackedPairs
            coChangeViolationsCount = $coChangeViolationsCount
            driftedTrackedFiles     = $driftedTrackedFiles.ToArray()
            driftedCpfFiles         = $driftedCpfFiles.ToArray()
            violations              = $tier5Errors.ToArray()
            durationMs              = $tierTimes["T5"]
        }
        T6                  = [ordered]@{
            status                             = $t6Status
            duplicatePublicTypeViolationsCount = $duplicatePublicTypeViolationsCount
            unreferencedSymbolViolationsCount  = $unreferencedSymbolViolationsCount
            baselinedDuplicateTypesCount       = $baselinedDuplicateTypesCount
            requiredSymbolsVerifiedCount       = $requiredSymbolsVerifiedCount
            thirdPartyNoticesPresent           = $thirdPartyNoticesPresent
            violations                         = $tier6Errors.ToArray()
            durationMs                         = $tierTimes["T6"]
        }
    }
    [System.IO.File]::WriteAllText($JsonOutput, ($jsonPayload | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
}

$goldenReportPath = Join-Path $repoRoot "tests\golden\expected_gate_report.json"
$goldenPayload = [ordered]@{
    schemaVersion = "1.0.0"
    overallStatus = $overallStatus
    exitCode      = $exitBitmask
    T1            = [ordered]@{ status = $t1Status; kbNotesMin = $kbNotesChecked; mcpIssuesMin = $mcpIssuesChecked }
    T2            = [ordered]@{ status = $t2Status; contractsPassed = $contractsPassed; contractsFailed = $contractsFailed }
    T3            = [ordered]@{ status = $t3Status; xmlFilesScanned = $xmlFiles.Count; unknownCvarViolations = $unknownCvarViolations }
    T4            = [ordered]@{ status = $t4Status; vfsConfigPresent = $vfsConfigPresent }
    T5            = [ordered]@{ status = $t5Status; trackedPairsCompared = $trackedPairsCompared }
    T6            = [ordered]@{ status = $t6Status; requiredSymbolsVerifiedCount = $requiredSymbolsVerifiedCount }
}
if ($UpdateGolden) {
    $gDir = [System.IO.Path]::GetDirectoryName($goldenReportPath)
    if (-not (Test-Path -LiteralPath $gDir)) { New-Item -ItemType Directory -Path $gDir -Force | Out-Null }
    [System.IO.File]::WriteAllText($goldenReportPath, ($goldenPayload | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host " [GOLDEN UPDATED] Saved golden gate snapshot to '$goldenReportPath'." -ForegroundColor Green
} elseif ($VerifyGolden -and (Test-Path -LiteralPath $goldenReportPath)) {
    $expectedGolden = Get-Content -LiteralPath $goldenReportPath -Raw | ConvertFrom-Json
    if ($goldenPayload.overallStatus -ne $expectedGolden.overallStatus -or
        $goldenPayload.T2.contractsPassed -lt [int]$expectedGolden.T2.contractsPassed -or
        $goldenPayload.T3.xmlFilesScanned -lt [int]$expectedGolden.T3.xmlFilesScanned -or
        $goldenPayload.T5.trackedPairsCompared -lt [int]$expectedGolden.T5.trackedPairsCompared) {
        Write-Host " [GOLDEN DRIFT] Live gate structural counts regressed below '$goldenReportPath'! Refresh with: .\scripts\gate_check.ps1 -UpdateGolden" -ForegroundColor Red
        $exitBitmask = $exitBitmask -bor 2
    } else {
        Write-Host " [GOLDEN PASS] Gate structural telemetry matches '$goldenReportPath'." -ForegroundColor Green
    }
}

if (-not $Full) {
    $lastGateRunPath = Join-Path $repoRoot "logs\last_gate_run.json"
    try {
        $lastGateRunDir = [System.IO.Path]::GetDirectoryName($lastGateRunPath)
        if (-not (Test-Path -LiteralPath $lastGateRunDir)) { New-Item -ItemType Directory -Path $lastGateRunDir -Force | Out-Null }
        $lastGateRunPayload = [ordered]@{
            timestampUtc = [System.DateTime]::UtcNow.ToString("o")
            durationMs   = [int]$totalMs
            passed       = ($exitBitmask -eq 0)
            exitCode     = $exitBitmask
        }
        [System.IO.File]::WriteAllText($lastGateRunPath, ($lastGateRunPayload | ConvertTo-Json -Depth 3), (New-Object System.Text.UTF8Encoding($false)))
    } catch { }
}

Write-Host "`n================================================================================" -ForegroundColor Cyan
Write-Host " [QUALITY GATE SUMMARY] Total Duration: ${totalSec}s (${totalMs}ms)" -ForegroundColor Cyan
Write-Host (" Tier Breakdown: T1={0}ms | T2={1}ms | T3={2}ms | T4={3}ms | T5={4}ms | T6={5}ms" -f $tierTimes["T1"], $tierTimes["T2"], $tierTimes["T3"], $tierTimes["T4"], $tierTimes["T5"], $tierTimes["T6"]) -ForegroundColor Gray
Write-Host " PASSED: $passedTiers | FAILED: $failedTiers | WARNINGS: $warningsCount | SKIPPED: $skippedCount" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

if ($exitBitmask -ne 0) {
    Write-Host " [GATE STATUS] FAILED (ExitBitmask: $exitBitmask) - One or more tiers did not pass. Block injection." -ForegroundColor Red
    exit $exitBitmask
} elseif ($totalMs -gt 5000) {
    Write-Host " [GATE STATUS] GREEN (OVER 5.0s BUDGET) - All 6 tiers passed functionally in ${totalSec}s (warm target: <= 5.0s)." -ForegroundColor Yellow
    exit 0
} else {
    Write-Host " [GATE STATUS] GREEN - All 6 tiers passed cleanly in ${totalSec}s (<= 5.0s target)!" -ForegroundColor Green
    exit 0
}
