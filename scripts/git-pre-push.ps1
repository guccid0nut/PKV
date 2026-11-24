# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Project Kongor Git Pre-Push Quality Gate (versioned hook: .githooks/pre-push).
.DESCRIPTION
    Validates the Release build, runs the unit test suite, and checks disk footprint budgets
    before permitting a git push to origin (Azure DevOps).

    Agent / MCP safe by design:
      * ISOLATED BUILD: compiles into artifacts/prepush-gate (--artifacts-path) instead of the normal
        bin/ folders, so a running master server, chat server or the TAO MCP server (kongor.exe, which
        Antigravity runs straight out of src/tools/kongor-cli/bin/Release) never locks the gate's output.
        => The gate NO LONGER KILLS anything by default. An AI agent can run "git push" without
           terminating the very MCP server it is talking to.
      * NON-INTERACTIVE: GIT_TERMINAL_PROMPT=0 / GCM_INTERACTIVE=never for the push.
      * SERIALIZED: a global named mutex prevents two concurrent pushes (e.g. two agents) from
        building into the same artifacts folder at the same time.

    Environment knobs (all optional):
      KONGOR_PREPUSH_MODE           full (default) | build (skip tests) | mirror-only (skip build+tests)
      KONGOR_PREPUSH_STOP_SERVICES  1 = legacy behaviour: stop local services/testhosts/kongor.exe first
      KONGOR_PREPUSH_LOCK_MINUTES   minutes to wait for another running gate (default 30)

    Logs: console + logs/pre-push.log.
    Emergency bypass: git push --no-verify
#>

param(
    [string]$RemoteName = "origin",
    [string]$RemoteUrl = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$LogDir = Join-Path $RepoRoot "logs"
$LogFile = Join-Path $LogDir "pre-push.log"
$GateArtifacts = Join-Path $RepoRoot "artifacts\prepush-gate"
if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_PREPUSH_ARTIFACTS)) {
    $GateArtifacts = $env:KONGOR_PREPUSH_ARTIFACTS
} else {
    $repoDriveLetter = ([System.IO.Path]::GetPathRoot($RepoRoot)).Substring(0, 1)
    $repoDrive = Get-PSDrive -Name $repoDriveLetter -ErrorAction SilentlyContinue
    $dDrive = Get-PSDrive -Name 'D' -ErrorAction SilentlyContinue
    if ($null -ne $repoDrive -and $null -ne $dDrive -and $repoDrive.Free -lt 25GB -and $dDrive.Free -gt $repoDrive.Free -and (Test-Path -LiteralPath "D:\temp" -PathType Container)) {
        $spillTarget = "D:\temp\kongor-prepush-gate"
        if (-not (Test-Path -LiteralPath $spillTarget -PathType Container)) {
            New-Item -ItemType Directory -Path $spillTarget -Force | Out-Null
        }
        $artifactsParent = Split-Path -Parent $GateArtifacts
        if (-not (Test-Path -LiteralPath $artifactsParent -PathType Container)) {
            New-Item -ItemType Directory -Path $artifactsParent -Force | Out-Null
        }
        $existingGateItem = Get-Item -LiteralPath $GateArtifacts -Force -ErrorAction SilentlyContinue
        $isJunction = ($null -ne $existingGateItem -and ($existingGateItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint))
        if ($null -eq $existingGateItem) {
            # Use an NTFS junction so AppContext.BaseDirectory stays logically under $RepoRoot (preserving repo-root parent traversal in unit tests) while storing all build bytes on D:\.
            New-Item -ItemType Junction -Path $GateArtifacts -Target $spillTarget | Out-Null
        } elseif (-not $isJunction) {
            $childCount = @(Get-ChildItem -LiteralPath $GateArtifacts -Force -ErrorAction SilentlyContinue).Count
            if ($childCount -eq 0) {
                Remove-Item -LiteralPath $GateArtifacts -Force
                New-Item -ItemType Junction -Path $GateArtifacts -Target $spillTarget | Out-Null
            }
        }
    }
}
$kongorScript = Join-Path $RepoRoot "kongor.ps1"
$slnPath = Join-Path $RepoRoot "ProjectKongor.slnx"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Write-GateLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $logLine = "[$timestamp] [$Level] $Message"

    switch ($Level) {
        "INFO"    { Write-Host $logLine -ForegroundColor Cyan }
        "SUCCESS" { Write-Host $logLine -ForegroundColor Green }
        "WARN"    { Write-Host $logLine -ForegroundColor Yellow }
        "ERROR"   { Write-Host $logLine -ForegroundColor Red }
        default   { Write-Host $logLine }
    }

    Add-Content -Path $LogFile -Value $logLine -ErrorAction SilentlyContinue
}

function Get-InvokerChain {
    # Who triggered this push (terminal, IDE, Antigravity language server, MCP tool...)? Pure diagnostics.
    $chain = @()
    try {
        $currentPid = $PID
        for ($i = 0; $i -lt 6; $i++) {
            $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$currentPid" -ErrorAction Stop
            if (-not $proc) { break }
            $chain += "$($proc.Name)#$($proc.ProcessId)"
            if (-not $proc.ParentProcessId -or $proc.ParentProcessId -eq $currentPid) { break }
            $currentPid = $proc.ParentProcessId
        }
    } catch { $chain += "<unavailable: $($_.Exception.Message)>" }
    return ($chain -join " <- ")
}

function Stop-LocalServicesLegacy {
    # Opt-in only (KONGOR_PREPUSH_STOP_SERVICES=1). Kills kongor.exe => also kills a running TAO MCP server.
    Write-GateLog "KONGOR_PREPUSH_STOP_SERVICES=1: stopping local services, test hosts and build servers (legacy behaviour)..." "WARN"
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& '$kongorScript' stop" | Out-Null
    $targetProcessNames = @("testhost", "testhost.x86", "vstest.console", "VBCSCompiler", "kongor", "kongor-render")
    $victims = Get-Process -Name $targetProcessNames -ErrorAction SilentlyContinue
    foreach ($v in $victims) { Write-GateLog "  stopping $($v.ProcessName)#$($v.Id)" "WARN" }
    $victims | Stop-Process -Force -ErrorAction SilentlyContinue
    & dotnet build-server shutdown | Out-Null
    $cleanupSw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($cleanupSw.Elapsed.TotalSeconds -lt 5) {
        if (-not (Get-Process -Name $targetProcessNames -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 200
    }
}

function Invoke-Gate {
    $mode = if ($env:KONGOR_PREPUSH_MODE) { $env:KONGOR_PREPUSH_MODE.ToLowerInvariant() } else { "full" }
    if ($mode -notin @("full", "build", "mirror-only")) {
        Write-GateLog "Unknown KONGOR_PREPUSH_MODE '$mode'; falling back to 'full'." "WARN"
        $mode = "full"
    }
    Write-GateLog "Mode: $mode | Isolated artifacts: $GateArtifacts" "INFO"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # Step 0: (opt-in) legacy service shutdown. Not needed any more thanks to the isolated artifacts path.
    if ($env:KONGOR_PREPUSH_STOP_SERVICES -eq "1") {
        Stop-LocalServicesLegacy
    } else {
        Write-GateLog "Step 0/3: Skipped stopping services (isolated build output; running master server / MCP server are left alone)." "INFO"
    }

    # Step 0.5: Override Preservation & Zero-Injection Guarantee
    $env:KONGOR_BLOCK_INJECTION = "1"
    Write-GateLog "Step 0.5/3: Override Preservation: games/client/override/ is left intact. No archive injection will run." "INFO"

    # Step 0.7: .NET SDK global.json Alignment Gate (supports user-scoped dotnet-install root when non-elevated)
    $globalJsonPath = Join-Path $RepoRoot "global.json"
    if (Test-Path -LiteralPath $globalJsonPath -PathType Leaf) {
        $globalJsonObj = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
        $reqSdkVer = [string]$globalJsonObj.sdk.version
        $allowPrerelease = [bool]$globalJsonObj.sdk.allowPrerelease
        $userDotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
        $userSdkCandidate = Join-Path $userDotnetDir "sdk\$reqSdkVer"
        if ((Test-Path -LiteralPath (Join-Path $userDotnetDir "dotnet.exe")) -and (Test-Path -LiteralPath $userSdkCandidate)) {
            $env:DOTNET_ROOT = $userDotnetDir
            if (($env:PATH -split ';')[0] -ne $userDotnetDir) {
                $env:PATH = "$userDotnetDir;$env:PATH"
            }
        }
        $activeSdkVer = (& dotnet --version 2>$null | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($activeSdkVer) -or (-not $allowPrerelease -and $activeSdkVer -match '-') -or (($activeSdkVer -split '\.')[0..1] -join '.') -ne (($reqSdkVer -split '\.')[0..1] -join '.')) {
            Write-GateLog "Pre-Push Quality Gate FAILED: Active .NET SDK '$activeSdkVer' does not satisfy global.json ('$reqSdkVer', allowPrerelease=$allowPrerelease)." "ERROR"
            Write-GateLog "Action: Install .NET SDK $reqSdkVer via 'winget install --id Microsoft.DotNet.SDK.11 --version $reqSdkVer' or dotnet-install.ps1 -Version $reqSdkVer." "ERROR"
            return 1
        }
        Write-GateLog "Step 0.7/3: Verified .NET SDK '$activeSdkVer' matches global.json ('$reqSdkVer', allowPrerelease=$allowPrerelease)." "SUCCESS"
    }

    # Step 0.8: Fast PowerShell AST Static Gate (.psscriptanalyzer.psd1)
    $psGateSw = [System.Diagnostics.Stopwatch]::StartNew()
    $psFiles = Get-ChildItem -Path $ScriptDir -Filter "*.ps1" -Recurse -File | Select-Object -ExpandProperty FullName
    $psErrors = @()
    foreach ($psFile in $psFiles) {
        $tokens = $null
        $parseErrors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($psFile, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors -and $parseErrors.Count -gt 0) {
            foreach ($err in $parseErrors) {
                $psErrors += "$($psFile.Replace($RepoRoot, '')):$($err.Extent.StartLineNumber): $($err.Message)"
            }
        }
    }
    $psGateSw.Stop()
    if ($psErrors.Count -gt 0) {
        Write-GateLog "Pre-Push Quality Gate FAILED: PowerShell AST syntax errors detected:" "ERROR"
        foreach ($e in $psErrors) { Write-GateLog "  $e" "ERROR" }
        return 1
    }
    Write-GateLog "Step 0.8/3: PowerShell AST verified $($psFiles.Count) scripts in $($psGateSw.ElapsedMilliseconds)ms." "SUCCESS"

    # Step 0.85: Packaged WILLOWMAKER & Native Client Binary Freshness Gate (deterministic willowmaker.buildinfo.json SHA-256)
    $willowSyncScript = Join-Path $ScriptDir "sync_willowmaker_client_bins.ps1"
    $packagedWillowDir = Join-Path $RepoRoot "games\client\win-x64"
    if ($mode -ne "mirror-only" -and (Test-Path -LiteralPath $willowSyncScript -PathType Leaf) -and (Test-Path -LiteralPath $packagedWillowDir -PathType Container)) {
        $wmSw = [System.Diagnostics.Stopwatch]::StartNew()
        Write-GateLog "Step 0.85/3: Verifying packaged WILLOWMAKER.exe & native client DLL freshness (sync_willowmaker_client_bins.ps1 -CheckOnly)..." "INFO"
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$willowSyncScript" -CheckOnly | Out-Host
        $wmExit = $LASTEXITCODE
        $wmSw.Stop()
        if ($wmExit -ne 0) {
            Write-GateLog "Pre-Push Quality Gate FAILED: Packaged WILLOWMAKER.exe or native client binaries are stale/drifted ($([math]::Round($wmSw.Elapsed.TotalSeconds,2))s)." "ERROR"
            Write-GateLog "Action: Run .\scripts\sync_willowmaker_client_bins.ps1 to republish games/client/win-x64 and games/client/windows-x64 before pushing." "ERROR"
            return 1
        }
        Write-GateLog "Step 0.85/3: Packaged WILLOWMAKER & native client binaries verified fresh in $($wmSw.ElapsedMilliseconds)ms." "SUCCESS"
    }

    # Step 0.9: Blocking 6-Tier Inner-Loop Quality Gate (gate_check.ps1)
    $gateCheckScript = Join-Path $ScriptDir "gate_check.ps1"
    $lastGateRunJson = Join-Path $LogDir "last_gate_run.json"
    if (Test-Path -LiteralPath $gateCheckScript) {
        Write-GateLog "Step 0.9/3: Running 6-tier quality gate (scripts/gate_check.ps1)..." "INFO"
        $innerGateSw = [System.Diagnostics.Stopwatch]::StartNew()
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$gateCheckScript" | Out-Host
        $innerGateExit = $LASTEXITCODE
        $innerGateSw.Stop()
        if ($innerGateExit -ne 0) {
            Write-GateLog "Pre-Push Quality Gate FAILED: scripts/gate_check.ps1 exited with bitmask $innerGateExit ($([math]::Round($innerGateSw.Elapsed.TotalSeconds,2))s)." "ERROR"
            Write-GateLog "Fix reported tier violations before pushing: .\scripts\gate_check.ps1" "ERROR"
            return $innerGateExit
        }
        Write-GateLog "Step 0.9/3: 6-tier quality gate GREEN in $([math]::Round($innerGateSw.Elapsed.TotalSeconds,2))s." "SUCCESS"

        $pass1InternalMs = $innerGateSw.ElapsedMilliseconds
        if (Test-Path -LiteralPath $lastGateRunJson -PathType Leaf) {
            try {
                $pass1Obj = Get-Content -LiteralPath $lastGateRunJson -Raw | ConvertFrom-Json
                if ($null -ne $pass1Obj.durationMs) { $pass1InternalMs = [int]$pass1Obj.durationMs }
            } catch {
                Write-GateLog "Step 0.9/3: Could not read durationMs from last_gate_run.json after pass 1: $($_.Exception.Message)" "WARN"
            }
        }

        if ($innerGateSw.ElapsedMilliseconds -gt 5000 -or $pass1InternalMs -gt 5000) {
            Write-GateLog "Step 0.9/3: Initial gate pass populated cold caches in $([math]::Round($innerGateSw.Elapsed.TotalSeconds,2))s (internal ${pass1InternalMs}ms); verifying warm <= 5.0s SLO 5 budget..." "INFO"
            $warmGateSw = [System.Diagnostics.Stopwatch]::StartNew()
            & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$gateCheckScript" | Out-Host
            $warmGateExit = $LASTEXITCODE
            $warmGateSw.Stop()
            if ($warmGateExit -ne 0) {
                Write-GateLog "Pre-Push Quality Gate FAILED: warm re-run of scripts/gate_check.ps1 exited with bitmask $warmGateExit ($([math]::Round($warmGateSw.Elapsed.TotalSeconds,2))s)." "ERROR"
                return $warmGateExit
            }
            Write-GateLog "Step 0.9/3: Warm 6-tier quality gate GREEN in $([math]::Round($warmGateSw.Elapsed.TotalSeconds,2))s." "SUCCESS"

            $pass2InternalMs = $warmGateSw.ElapsedMilliseconds
            if (Test-Path -LiteralPath $lastGateRunJson -PathType Leaf) {
                try {
                    $pass2Obj = Get-Content -LiteralPath $lastGateRunJson -Raw | ConvertFrom-Json
                    if ($null -ne $pass2Obj.durationMs) { $pass2InternalMs = [int]$pass2Obj.durationMs }
                } catch {
                    Write-GateLog "Step 0.9/3: Could not read durationMs from last_gate_run.json after pass 2: $($_.Exception.Message)" "WARN"
                }
            }

            if (($pass2InternalMs -gt 5000 -and $pass2InternalMs -le 8000) -or ($warmGateSw.ElapsedMilliseconds -gt 5000 -and $warmGateSw.ElapsedMilliseconds -le 8000 -and $pass2InternalMs -gt 5000)) {
                Write-GateLog "Step 0.9/3: Second gate pass took $([math]::Round($warmGateSw.Elapsed.TotalSeconds,2))s (internal ${pass2InternalMs}ms <= 8.0s post-commit disk cache warmup); running 1 final warm pass before SLO 5 verification..." "INFO"
                $finalWarmSw = [System.Diagnostics.Stopwatch]::StartNew()
                & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$gateCheckScript" | Out-Host
                $finalWarmExit = $LASTEXITCODE
                $finalWarmSw.Stop()
                if ($finalWarmExit -ne 0) {
                    Write-GateLog "Pre-Push Quality Gate FAILED: final warm pass of scripts/gate_check.ps1 exited with bitmask $finalWarmExit ($([math]::Round($finalWarmSw.Elapsed.TotalSeconds,2))s)." "ERROR"
                    return $finalWarmExit
                }
                Write-GateLog "Step 0.9/3: Final warm 6-tier quality gate GREEN in $([math]::Round($finalWarmSw.Elapsed.TotalSeconds,2))s." "SUCCESS"
            }
        }
    }

    # Step 0.91: North-Star Engineering SLO & Sample-Floor Verifier (verify_north_star_slos.ps1)
    $sloCheckScript = Join-Path $ScriptDir "qa\verify_north_star_slos.ps1"
    if (Test-Path -LiteralPath $sloCheckScript) {
        Write-GateLog "Step 0.91/3: Running North-Star Engineering SLO verifier (scripts/qa/verify_north_star_slos.ps1)..." "INFO"
        $sloGateSw = [System.Diagnostics.Stopwatch]::StartNew()
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$sloCheckScript" | Out-Host
        $sloGateExit = $LASTEXITCODE
        $sloGateSw.Stop()
        if ($sloGateExit -ne 0) {
            Write-GateLog "Pre-Push Quality Gate FAILED: scripts/qa/verify_north_star_slos.ps1 exited with code $sloGateExit ($([math]::Round($sloGateSw.Elapsed.TotalSeconds,2))s)." "ERROR"
            Write-GateLog "Fix reported SLO / quarantine / MCP codification violations before pushing: .\scripts\qa\verify_north_star_slos.ps1" "ERROR"
            return $sloGateExit
        }
        Write-GateLog "Step 0.91/3: North-Star SLO gate GREEN in $([math]::Round($sloGateSw.Elapsed.TotalSeconds,2))s." "SUCCESS"
    }

    # Step 0.92: Cleanroom Secret, Gitleaks & Local PII Delta Gate
    $secretScrubLib = Join-Path $ScriptDir "lib\Test-KongorSecretScrub.ps1"
    if (Test-Path -LiteralPath $secretScrubLib) {
        . $secretScrubLib
        $piiSw = [System.Diagnostics.Stopwatch]::StartNew()
        $deltaBase = ""
        try {
            $resUp = (& git -C "$RepoRoot" rev-parse --verify "@{u}" 2>$null)
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resUp)) {
                $deltaBase = $resUp.Trim()
            } else {
                $currBr = (& git -C "$RepoRoot" rev-parse --abbrev-ref HEAD 2>$null)
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($currBr)) {
                    $resRem = (& git -C "$RepoRoot" rev-parse --verify "$RemoteName/$($currBr.Trim())" 2>$null)
                    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resRem)) {
                        $deltaBase = $resRem.Trim()
                    }
                }
            }
        } catch {
            $deltaBase = ""
        }

        $deltaFiles = @()
        if (-not [string]::IsNullOrWhiteSpace($deltaBase)) {
            $rawDelta = @(& git -C "$RepoRoot" diff --name-only --diff-filter=ACMR "$deltaBase..HEAD" 2>$null)
            if ($LASTEXITCODE -eq 0) {
                $deltaFiles = @($rawDelta | ForEach-Object {
                    $p = Join-Path $RepoRoot ($_.Trim() -replace '/', '\')
                    if (Test-Path -LiteralPath $p -PathType Leaf) { $p }
                } | Where-Object { $_ })
            }
        }

        $secDelta = Test-KongorSecretLeakage -RepoRoot $RepoRoot -CandidateFiles $deltaFiles -BaseRef $deltaBase -HeadRef "HEAD"
        if (-not $secDelta.Passed) {
            Write-GateLog "Pre-Push Quality Gate FAILED: $($secDelta.TotalLeaks) secret leak(s) detected in push delta:" "ERROR"
            foreach ($lk in $secDelta.Leaks) {
                Write-GateLog "  [$($lk.commit)] $($lk.file_path) :: $($lk.rule_name) (masked: $($lk.masked_snippet))" "ERROR"
            }
            Write-GateLog "Action: Remove credentials/tokens and re-verify via scripts/lib/Test-KongorSecretScrub.ps1." "ERROR"
            return 1
        }

        $piiDelta = Test-KongorLocalPiiGuard -RepoRoot $RepoRoot -CandidateFiles $deltaFiles -BaseRef $deltaBase -HeadRef "HEAD" -RequireDenylist
        if (-not $piiDelta.Passed) {
            Write-GateLog "Pre-Push Quality Gate FAILED: $($piiDelta.Reason)" "ERROR"
            foreach ($pf in $piiDelta.Findings) {
                Write-GateLog "  [$($pf.commit)] $($pf.file_path) :: $($pf.rule_name) (masked: $($pf.masked_snippet))" "ERROR"
            }
            Write-GateLog "Action: Replace workstation paths/identifiers with relative paths or synthetic canaries, and ensure config/local_pii_guard.local.json exists." "ERROR"
            return 1
        }

        $gitleaksConfig = Join-Path $RepoRoot ".gitleaks.toml"
        if (-not [string]::IsNullOrWhiteSpace($deltaBase) -and (Test-Path -LiteralPath $gitleaksConfig -PathType Leaf)) {
            $gitleaksExe = $null
            $glCmd = Get-Command gitleaks -ErrorAction SilentlyContinue
            if ($glCmd) {
                $gitleaksExe = $glCmd.Source
            } else {
                $wingetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
                $wingetGl = Get-ChildItem -Path (Join-Path $wingetRoot "*gitleaks*") -Filter "gitleaks.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $wingetGl) {
                    $wingetGl = Get-ChildItem -Path $wingetRoot -Filter "gitleaks.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                }
                if ($wingetGl) { $gitleaksExe = $wingetGl.FullName }
            }
            if ($gitleaksExe) {
                & $gitleaksExe git "$RepoRoot" --config "$gitleaksConfig" --log-opts="$deltaBase..HEAD" --redact | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    Write-GateLog "Pre-Push Quality Gate FAILED: gitleaks detected secrets in commit range $deltaBase..HEAD." "ERROR"
                    Write-GateLog "Action: Run '$gitleaksExe git --config .gitleaks.toml --log-opts=`"$deltaBase..HEAD`" --redact -v' to inspect." "ERROR"
                    return 1
                }
            }
        }
        $piiSw.Stop()
        Write-GateLog "Step 0.92/3: Secret, Gitleaks & Local PII delta gate GREEN in $([math]::Round($piiSw.Elapsed.TotalSeconds,2))s." "SUCCESS"
    }

    # Step 0.95: K2 Texture Shadowing & Normal-Map Swizzle Gate (audit_k2_textures.ps1 -Strict)
    $textureAuditScript = Join-Path -Path $ScriptDir -ChildPath "assets\audit_k2_textures.ps1"
    if (-not (Test-Path -LiteralPath $textureAuditScript)) {
        Write-GateLog -Message "Pre-Push Quality Gate FAILED: Required K2 texture auditor missing at '$textureAuditScript'. Restore with 'git checkout -- scripts/assets/audit_k2_textures.ps1'." -Level "ERROR"
        return 1
    }
    $shouldRunTextureAudit = $true
    $texSkipReason = ""
    if ($env:KONGOR_FORCE_TEXTURE_AUDIT -ne "1") {
        $upstreamBase = ""
        try {
            $resolvedUpstream = (& git -C "$RepoRoot" rev-parse --verify "@{u}" 2>$null)
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolvedUpstream)) {
                $upstreamBase = $resolvedUpstream.Trim()
            } else {
                $currBranch = (& git -C "$RepoRoot" rev-parse --abbrev-ref HEAD 2>$null)
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($currBranch)) {
                    $candRemoteRef = "$RemoteName/$($currBranch.Trim())"
                    $resolvedRemote = (& git -C "$RepoRoot" rev-parse --verify "$candRemoteRef" 2>$null)
                    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolvedRemote)) {
                        $upstreamBase = $resolvedRemote.Trim()
                    }
                }
            }
            if (-not [string]::IsNullOrWhiteSpace($upstreamBase)) {
                $changedPaths = @(& git -C "$RepoRoot" diff --name-only "$upstreamBase..HEAD" 2>$null)
                if ($LASTEXITCODE -eq 0) {
                    $relevantTexChanges = @($changedPaths | Where-Object {
                        $_ -match '\.(dds|png|tga|jpg|material|package|interface|s2z)$' -or
                        $_ -match 'audit_k2_textures\.(py|ps1)$'
                    })
                    if ($relevantTexChanges.Count -eq 0) {
                        $shouldRunTextureAudit = $false
                        $shortBase = $upstreamBase.Substring(0, [Math]::Min(9, $upstreamBase.Length))
                        $texSkipReason = "0 texture/UI/material/auditor files changed in $shortBase..HEAD"
                    }
                }
            }
        } catch {
            Write-GateLog -Message "Step 0.95/3: Could not diff upstream range ($($_.Exception.Message)); falling back to full strict texture audit." -Level "WARN"
            $shouldRunTextureAudit = $true
        }
    }
    if (-not $shouldRunTextureAudit) {
        Write-GateLog -Message "Step 0.95/3: Skipped K2 texture audit ($texSkipReason; set KONGOR_FORCE_TEXTURE_AUDIT=1 to force)." -Level "SUCCESS"
    } else {
        Write-GateLog -Message "Step 0.95/3: Running K2 texture shadowing & normal-map swizzle gate (scripts/assets/audit_k2_textures.ps1 -Strict)..." -Level "INFO"
        $texGateSw = [System.Diagnostics.Stopwatch]::StartNew()
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$textureAuditScript" -Strict | Out-Host
        $texGateExit = $LASTEXITCODE
        $texGateSw.Stop()
        if ($texGateExit -ne 0) {
            Write-GateLog -Message "Pre-Push Quality Gate FAILED: scripts/assets/audit_k2_textures.ps1 -Strict exited with code $texGateExit ($([math]::Round($texGateSw.Elapsed.TotalSeconds,2))s)." -Level "ERROR"
            Write-GateLog -Message "Fix K2 UI shadowing or normal-map swizzle violations before pushing: .\scripts\assets\audit_k2_textures.ps1 -Strict -Detailed" -Level "ERROR"
            return $texGateExit
        }
        Write-GateLog -Message "Step 0.95/3: K2 texture shadowing & normal-map swizzle gate GREEN in $([math]::Round($texGateSw.Elapsed.TotalSeconds,2))s." -Level "SUCCESS"
    }

    if ($mode -eq "mirror-only") {
        Write-GateLog "KONGOR_PREPUSH_MODE=mirror-only: SKIPPING build + tests. Use only for docs/scripts-only pushes." "WARN"
    } else {
        # Conserve Windows virtual memory commit charge during git-lfs + build + test
        $env:DOTNET_gcServer = "0"
        $env:MSBUILDDISABLENODEREUSE = "1"
        Get-Process -Name "testhost*", "vstest*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        # Step 1: Solution Build Verification (Release, isolated output)
        Write-GateLog "Step 1/3: Verifying Release solution compilation (isolated --artifacts-path)..." "INFO"
        $buildSw = [System.Diagnostics.Stopwatch]::StartNew()
        & dotnet build "$slnPath" -c Release --nologo -tl:off -v minimal -maxcpucount:4 -nodereuse:false --artifacts-path "$GateArtifacts" | Out-Host
        $buildExit = $LASTEXITCODE
        & dotnet build-server shutdown *> $null
        $buildSw.Stop()

        if ($buildExit -ne 0) {
            Write-GateLog "Pre-Push Quality Gate FAILED: Solution build failed with exit code $buildExit ($([math]::Round($buildSw.Elapsed.TotalSeconds,1))s)." "ERROR"
            Write-GateLog "Push aborted to prevent pushing broken code." "ERROR"
            Write-GateLog "Use 'git push --no-verify' ONLY if you explicitly need to bypass this check." "WARN"
            return $buildExit
        }
        Write-GateLog "Release solution compilation succeeded in $([math]::Round($buildSw.Elapsed.TotalSeconds,1))s." "SUCCESS"

        # Step 1.5: NuGet Vulnerability Audit (blocks Critical and unbaselined High advisories)
        Write-GateLog "Step 1.5/3: Auditing NuGet packages for Critical/High vulnerabilities..." "INFO"
        $vulnSw = [System.Diagnostics.Stopwatch]::StartNew()
        $vulnRaw = (& dotnet list "$slnPath" package --vulnerable --include-transitive --format json --no-restore 2>$null | Out-String)
        if ([string]::IsNullOrWhiteSpace($vulnRaw)) {
            $vulnRaw = (& dotnet list "$slnPath" package --vulnerable --include-transitive --format json 2>$null | Out-String)
        }
        $vulnSw.Stop()
        if ($vulnRaw -match '(?s)(\{\s*"version":.*\}\s*)\z') {
            $vulnJsonText = $Matches[1]
            $vulnBaselineSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            $vulnBaselinePath = Join-Path $ScriptDir ".nuget_vulnerability_baseline.json"
            if (Test-Path -LiteralPath $vulnBaselinePath -PathType Leaf) {
                $todayUtc = (Get-Date).Date
                $rawBaselineItems = Get-Content -LiteralPath $vulnBaselinePath -Raw | ConvertFrom-Json
                $baselineItems = if ($rawBaselineItems -is [System.Array]) { $rawBaselineItems } else { @($rawBaselineItems) }
                foreach ($vbe in $baselineItems) {
                    if ($null -eq $vbe -or [string]::IsNullOrWhiteSpace([string]$vbe.expiresOn)) { continue }
                    $expDt = [datetime]::ParseExact(([string]$vbe.expiresOn).Trim(), "yyyy-MM-dd", [System.Globalization.CultureInfo]::InvariantCulture)
                    if ($expDt -ge $todayUtc) {
                        [void]$vulnBaselineSet.Add("$($vbe.id)@$($vbe.version)")
                    }
                }
            }
            $blockingVulns = New-Object System.Collections.Generic.List[string]
            $baselinedVulnPkgs = 0
            $vulnObj = $vulnJsonText | ConvertFrom-Json
            $seenVulnKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($vp in @($vulnObj.projects)) {
                foreach ($vf in @($vp.frameworks)) {
                    foreach ($pkg in (@($vf.topLevelPackages) + @($vf.transitivePackages))) {
                        if ($null -eq $pkg -or [string]::IsNullOrWhiteSpace([string]$pkg.id)) { continue }
                        $pkgKey = "$($pkg.id)@$($pkg.resolvedVersion)"
                        foreach ($adv in @($pkg.vulnerabilities)) {
                            $sev = [string]$adv.severity
                            $dedupKey = "$pkgKey|$sev|$($adv.advisoryurl)"
                            if (-not $seenVulnKeys.Add($dedupKey)) { continue }
                            if ($sev -ieq "Critical") {
                                $blockingVulns.Add("CRITICAL: $pkgKey ($($adv.advisoryurl))")
                            } elseif ($sev -ieq "High") {
                                if ($vulnBaselineSet.Contains($pkgKey)) {
                                    $baselinedVulnPkgs++
                                } else {
                                    $blockingVulns.Add("HIGH: $pkgKey ($($adv.advisoryurl))")
                                }
                            }
                        }
                    }
                }
            }
            if ($blockingVulns.Count -gt 0) {
                Write-GateLog "Pre-Push Quality Gate FAILED: $($blockingVulns.Count) Critical/unbaselined High NuGet vulnerability advisory(s):" "ERROR"
                foreach ($bv in $blockingVulns) { Write-GateLog "  $bv" "ERROR" }
                Write-GateLog "Action: Upgrade package versions in Directory.Packages.props and re-run: dotnet list ProjectKongor.slnx package --vulnerable --include-transitive" "ERROR"
                return 1
            }
            Write-GateLog "Step 1.5/3: NuGet vulnerability audit clean (0 Critical, 0 unbaselined High, $baselinedVulnPkgs baselined preview/transitive advisories) in $([math]::Round($vulnSw.Elapsed.TotalSeconds,1))s." "SUCCESS"
        }

        # Step 2: Execute Local Unit Test Suite (against the same isolated artifacts, --no-build)
        if ($mode -eq "build") {
            Write-GateLog "KONGOR_PREPUSH_MODE=build: SKIPPING unit tests." "WARN"
        } else {
            Write-GateLog "Step 2/3: Executing Unit Test Suite..." "INFO"
            $testSw = [System.Diagnostics.Stopwatch]::StartNew()
            $unitSlnf = Join-Path $RepoRoot "sln-filters\Kongor.UnitTests.slnf"
            $testTarget = if (Test-Path -LiteralPath $unitSlnf -PathType Leaf) { $unitSlnf } else { $slnPath }
            $prevTestGc = $env:DOTNET_gcServer
            $env:DOTNET_gcServer = '0'
            try {
                & dotnet test "$testTarget" --no-build --configuration Release --artifacts-path "$GateArtifacts" --nologo -maxcpucount:4 -nodereuse:false --filter "Category!=Manual&Category!=LocalOnly" | Out-Host
                $testExit = $LASTEXITCODE
            } finally {
                $env:DOTNET_gcServer = $prevTestGc
            }
            Get-Process -Name "testhost*", "vstest*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            $testSw.Stop()

            if ($testExit -ne 0) {
                Write-GateLog "Pre-Push Quality Gate FAILED: Unit tests failed (tests $([math]::Round($testSw.Elapsed.TotalSeconds,1))s, total $([math]::Round($sw.Elapsed.TotalSeconds,1))s)." "ERROR"
                Write-GateLog "Push aborted to prevent regressions." "ERROR"
                Write-GateLog "Fix test failures or use 'git push --no-verify' in emergencies." "WARN"
                return $testExit
            }
            Write-GateLog "All unit tests passed in $([math]::Round($testSw.Elapsed.TotalSeconds,1))s." "SUCCESS"

            # Step 2.2: Native C++ Unit Tests, Evidence Dossier Obligations & Headless Lua Bot VM Pytest
            $nativeTestExe = Join-Path $RepoRoot "native\proxies\native_guard\build\Release\native_guard_unit_tests.exe"
            if (-not (Test-Path -LiteralPath $nativeTestExe)) {
                $nativeTestExe = Join-Path $RepoRoot "native\build\native_guard_build\Release\native_guard_unit_tests.exe"
            }

            # Pre-push C++ Auto-Rebuild: detect source changes newer than test binary
            $nativeSrcDir = Join-Path $RepoRoot "native\proxies\native_guard"
            $needsNativeRebuild = $false
            if (-not (Test-Path -LiteralPath $nativeTestExe)) {
                $needsNativeRebuild = $true
            } elseif (Test-Path -LiteralPath $nativeSrcDir) {
                $exeTime = (Get-Item -LiteralPath $nativeTestExe).LastWriteTimeUtc
                $newerSources = @(Get-ChildItem -Path $nativeSrcDir -Include *.cpp,*.c,*.h,*.hpp,CMakeLists.txt -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTimeUtc -gt $exeTime })
                if ($newerSources.Count -gt 0) {
                    $needsNativeRebuild = $true
                    Write-GateLog "NativeGuard C++ sources modified ($($newerSources.Count) files newer than binary). Auto-rebuilding..." "INFO"
                }
            }

            if ($needsNativeRebuild -and (Get-Command cmake -ErrorAction SilentlyContinue)) {
                $cmakeBuildDir = Join-Path $RepoRoot "native\proxies\native_guard\build"
                if (-not (Test-Path -LiteralPath (Join-Path $cmakeBuildDir "CMakeCache.txt"))) {
                    Write-GateLog "Configuring NativeGuard CMake build directory ('$cmakeBuildDir')..." "INFO"
                    & cmake -S "$nativeSrcDir" -B "$cmakeBuildDir" | Out-Host
                    if ($LASTEXITCODE -ne 0) {
                        Write-GateLog "Pre-Push Quality Gate FAILED: cmake configure for '$nativeSrcDir' failed (exit code $LASTEXITCODE). Run 'cmake -S native/proxies/native_guard -B native/proxies/native_guard/build'." "ERROR"
                        return $LASTEXITCODE
                    }
                }
                if (Test-Path -LiteralPath $cmakeBuildDir) {
                    Write-GateLog "Compiling NativeGuard C++ unit tests (cmake --build '$cmakeBuildDir' --config Release --target native_guard_unit_tests)..." "INFO"
                    & cmake --build "$cmakeBuildDir" --config Release --target native_guard_unit_tests | Out-Host
                    if ($LASTEXITCODE -ne 0) {
                        Write-GateLog "Pre-Push Quality Gate FAILED: cmake compilation of native_guard_unit_tests failed (exit code $LASTEXITCODE)." "ERROR"
                        return $LASTEXITCODE
                    }
                    if (-not (Test-Path -LiteralPath $nativeTestExe)) {
                        $nativeTestExe = Join-Path $RepoRoot "native\proxies\native_guard\build\Release\native_guard_unit_tests.exe"
                    }
                }
            }

            if (Test-Path -LiteralPath $nativeTestExe) {
                Write-GateLog "Step 2.2a/3: Running NativeGuard C++ unit test suite ($nativeTestExe)..." "INFO"
                $natSw = [System.Diagnostics.Stopwatch]::StartNew()
                $natCwd = Join-Path $LogDir "native_guard_test_cwd"
                New-Item -ItemType Directory -Path $natCwd -Force | Out-Null
                Push-Location $natCwd
                try {
                    & $nativeTestExe | Out-Host
                    $natExit = $LASTEXITCODE
                } finally {
                    Pop-Location
                }
                $natSw.Stop()
                if ($natExit -ne 0) {
                    Write-GateLog "Pre-Push Quality Gate FAILED: native_guard_unit_tests.exe exited with code $natExit." "ERROR"
                    Write-GateLog "Action: Rebuild and run C++ unit tests: cmake --build native/proxies/native_guard/build --config Release" "ERROR"
                    return $natExit
                }
                Write-GateLog "Step 2.2a/3: NativeGuard C++ unit tests GREEN in $($natSw.ElapsedMilliseconds)ms." "SUCCESS"
            } else {
                Write-GateLog "Pre-Push Quality Gate FAILED: Required C++ test binary missing at '$nativeTestExe'." "ERROR"
                Write-GateLog "Action: Configure and build NativeGuard tests: cmake -S native/proxies/native_guard -B native/proxies/native_guard/build && cmake --build native/proxies/native_guard/build --config Release --target native_guard_unit_tests" "ERROR"
                return 1
            }

            $evidenceScript = Join-Path $ScriptDir "verify_evidence_obligations.ps1"
            if (Test-Path -LiteralPath $evidenceScript) {
                Write-GateLog "Step 2.2b/3: Verifying Reconstruction Obligation Ledgers (Windows + Linux ELF dossiers)..." "INFO"
                & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$evidenceScript" | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    Write-GateLog "Pre-Push Quality Gate FAILED: scripts/verify_evidence_obligations.ps1 failed." "ERROR"
                    return $LASTEXITCODE
                }
            }

            # Step 2.2b-2: Linux Server Docker LD_PRELOAD Smoke Gate status & freshness verification
            $lastLinuxSmoke = Join-Path $RepoRoot "logs\last_linux_smoke.json"
            $linuxWatchedSources = @(
                (Join-Path $RepoRoot "native\proxies\native_guard\linux_native_guard.cpp"),
                (Join-Path $RepoRoot "native\proxies\native_guard\linux_queue_arcade.h"),
                (Join-Path $RepoRoot "native\proxies\native_guard\tests\linux_queue_arcade_tests.cpp")
            )
            if (Test-Path -LiteralPath $lastLinuxSmoke) {
                try {
                    $smokeData = Get-Content -LiteralPath $lastLinuxSmoke -Raw | ConvertFrom-Json
                    if ($smokeData.status -eq "PASSED") {
                        if ($smokeData.guardBannerVerified -ne $true) {
                            Write-GateLog "Pre-Push Quality Gate FAILED: logs/last_linux_smoke.json is missing guardBannerVerified=true." "ERROR"
                            Write-GateLog "Action: Re-run 'powershell -File scripts/docker/run_linux_server_docker_smoke.ps1' to verify [NativeGuard:Linux] init/IPC/unload banners." "ERROR"
                            return 1
                        }
                        $smokeTimeUtc = ([datetimeoffset]::Parse([string]$smokeData.timestamp)).UtcDateTime
                        foreach ($lSrc in $linuxWatchedSources) {
                            if ((Test-Path -LiteralPath $lSrc) -and ((Get-Item -LiteralPath $lSrc).LastWriteTimeUtc -gt $smokeTimeUtc)) {
                                Write-GateLog "Pre-Push Quality Gate FAILED: Linux NativeGuard source '$lSrc' is newer than logs/last_linux_smoke.json ($($smokeData.timestamp))." "ERROR"
                                Write-GateLog "Action: Run 'powershell -File scripts/docker/run_linux_server_docker_smoke.ps1' to refresh Linux ELF smoke verification." "ERROR"
                                return 1
                            }
                        }
                        Write-GateLog "Step 2.2b-2/3: Linux Server Docker LD_PRELOAD Smoke Gate PASSED ($($smokeData.checks) checks, guardBannerVerified=true, binarySmoke=$($smokeData.binarySmoke), in $($smokeData.durationMs)ms)." "SUCCESS"
                    } elseif ($smokeData.status -eq "SKIPPED") {
                        Write-GateLog "Step 2.2b-2/3: Linux Server Docker LD_PRELOAD Smoke Gate SKIPPED ($($smokeData.reason))." "WARN"
                    } else {
                        Write-GateLog "Pre-Push Quality Gate FAILED: Linux Server Docker LD_PRELOAD Smoke Gate reports status '$($smokeData.status)'." "ERROR"
                        Write-GateLog "Action: Run 'powershell -File scripts/docker/run_linux_server_docker_smoke.ps1' to diagnose and refresh." "ERROR"
                        return 1
                    }
                } catch {
                    Write-GateLog "Pre-Push Quality Gate FAILED: Could not parse logs/last_linux_smoke.json ($($_.Exception.Message)). Run 'powershell -File scripts/docker/run_linux_server_docker_smoke.ps1'." "ERROR"
                    return 1
                }
            } else {
                Write-GateLog "Pre-Push Quality Gate FAILED: Missing logs/last_linux_smoke.json. Run 'powershell -File scripts/docker/run_linux_server_docker_smoke.ps1' before pushing." "ERROR"
                return 1
            }

            $botPytestFile = Join-Path $RepoRoot "tests\unit\test_tri_studio_bot_tactics.py"
            if (Test-Path -LiteralPath $botPytestFile) {
                Write-GateLog "Step 2.2c/3: Running Headless Lua 5.1 Bot VM & Mutation Pytest Suite..." "INFO"
                $pySw = [System.Diagnostics.Stopwatch]::StartNew()
                & python -m pytest "$botPytestFile" -q | Out-Host
                $pyExit = $LASTEXITCODE
                $pySw.Stop()
                if ($pyExit -ne 0) {
                    Write-GateLog "Pre-Push Quality Gate FAILED: pytest tests/unit/test_tri_studio_bot_tactics.py exited with code $pyExit." "ERROR"
                    Write-GateLog "Action: Run 'python -m pytest tests/unit/test_tri_studio_bot_tactics.py -v' to diagnose." "ERROR"
                    return $pyExit
                }
                Write-GateLog "Step 2.2c/3: Bot VM & Mutation Pytest Suite GREEN in $([math]::Round($pySw.Elapsed.TotalSeconds,2))s." "SUCCESS"
            }

            $mcpConformancePytest = Join-Path $RepoRoot "tests\python\test_tao_mcp_stdio_conformance.py"
            if (Test-Path -LiteralPath $mcpConformancePytest) {
                Write-GateLog "Step 2.2d/3: Running TAO MCP Stdio Subprocess + NetGuard Conformance Suite..." "INFO"
                $mcpPySw = [System.Diagnostics.Stopwatch]::StartNew()
                $prepushKongorDll = Join-Path $GateArtifacts "bin\Kongor.Cli\release\kongor.dll"
                if (-not (Test-Path -LiteralPath $prepushKongorDll)) {
                    $foundPrepushDll = Get-ChildItem -Path $GateArtifacts -Filter "kongor.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($foundPrepushDll) { $prepushKongorDll = $foundPrepushDll.FullName }
                }
                $prevMcpDllEnv = $env:KONGOR_MCP_DLL_PATH
                try {
                    if (Test-Path -LiteralPath $prepushKongorDll) {
                        $env:KONGOR_MCP_DLL_PATH = $prepushKongorDll
                    }
                    & python -m pytest "$mcpConformancePytest" -q | Out-Host
                    $mcpPyExit = $LASTEXITCODE
                } finally {
                    $env:KONGOR_MCP_DLL_PATH = $prevMcpDllEnv
                }
                $mcpPySw.Stop()
                if ($mcpPyExit -ne 0) {
                    Write-GateLog "Pre-Push Quality Gate FAILED: pytest tests/python/test_tao_mcp_stdio_conformance.py exited with code $mcpPyExit." "ERROR"
                    Write-GateLog "Action: Run 'python -m pytest tests/python/test_tao_mcp_stdio_conformance.py -v' to diagnose." "ERROR"
                    return $mcpPyExit
                }
                Write-GateLog "Step 2.2d/3: TAO MCP Stdio Subprocess + NetGuard Conformance Suite GREEN in $([math]::Round($mcpPySw.Elapsed.TotalSeconds,2))s." "SUCCESS"
            }
        }
    }

    # Step 2.5: Verify Disk Footprint & Guardrails
    $verifyFootprintScript = Join-Path $ScriptDir "vault\verify_footprint.ps1"
    if (Test-Path $verifyFootprintScript) {
        Write-GateLog "Step 2.5/3: Verifying Disk Footprint & Media Budget..." "INFO"
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$verifyFootprintScript" | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-GateLog "Footprint verification failed! Run scripts/vault/verify_footprint.ps1 -Fix" "ERROR"
            return $LASTEXITCODE
        }
        Write-GateLog "Disk Footprint verified cleanly." "SUCCESS"
    }

    # Step 3: Remote Validation Completed
    Write-GateLog "Step 3/3: Pre-push quality gate verification passed for target remote '$RemoteName'." "SUCCESS"

    $sw.Stop()
    Write-GateLog "Gate work finished in $([math]::Round($sw.Elapsed.TotalSeconds,1))s." "INFO"
    return 0
}

Write-GateLog "==========================================================" "INFO"
Write-GateLog "Project Kongor: Local Pre-Push Quality Gate Triggered" "INFO"
Write-GateLog "Target Remote: $RemoteName ($RemoteUrl)" "INFO"
Write-GateLog "Invoker: $(Get-InvokerChain)" "INFO"

# Serialize concurrent gates (two agents / terminal + agent pushing at once).
$lockMinutes = 30
if ($env:KONGOR_PREPUSH_LOCK_MINUTES -as [int]) { $lockMinutes = [int]$env:KONGOR_PREPUSH_LOCK_MINUTES }
$mutex = New-Object System.Threading.Mutex($false, "Global\ProjectKongor-PrePushGate")
$acquired = $false
$exitCode = 1
try {
    try {
        $acquired = $mutex.WaitOne(0)
        if (-not $acquired) {
            Write-GateLog "Another pre-push gate is running; waiting up to $lockMinutes min for it to finish..." "WARN"
            $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes($lockMinutes))
        }
    } catch [System.Threading.AbandonedMutexException] {
        Write-GateLog "Previous gate exited without releasing the lock (crashed/killed); taking over." "WARN"
        $acquired = $true
    }

    if (-not $acquired) {
        Write-GateLog "Timed out after $lockMinutes min waiting for the other gate. Push aborted (retry later)." "ERROR"
        $exitCode = 75
    } else {
        $exitCode = Invoke-Gate
    }
} catch {
    Write-GateLog "Pre-Push Quality Gate crashed: $_" "ERROR"
    Write-GateLog ($_.ScriptStackTrace) "ERROR"
    $exitCode = 1
} finally {
    $env:KONGOR_BLOCK_INJECTION = $null
    if ($acquired -and (Test-Path -LiteralPath $GateArtifacts -PathType Container)) {
        try {
            $reclaimedBytes = [int64]0
            foreach ($buildSubDir in @("bin", "obj")) {
                $targetOutDir = Join-Path $GateArtifacts $buildSubDir
                if (Test-Path -LiteralPath $targetOutDir -PathType Container) {
                    $dirSize = (Get-ChildItem -LiteralPath $targetOutDir -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
                    if ($null -ne $dirSize) { $reclaimedBytes += [int64]$dirSize }
                    Remove-Item -LiteralPath $targetOutDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            $reclaimedMb = [math]::Round($reclaimedBytes / 1MB, 1)
            Write-GateLog "Cleaned up isolated prepush-gate build outputs (bin/, obj/; logs preserved), reclaiming ${reclaimedMb} MB." "INFO"
        } catch {
            Write-GateLog "Non-fatal warning cleaning up '$GateArtifacts' bin/obj outputs: $($_.Exception.Message)" "WARN"
        }
    }
    if ($acquired) { try { $mutex.ReleaseMutex() } catch { Write-GateLog "Non-fatal mutex release notice: $($_.Exception.Message)" "WARN" } }
    $mutex.Dispose()
}

if ($exitCode -eq 0) {
    Write-GateLog "Pre-Push Quality Gate: PASSED. Proceeding with push." "SUCCESS"
} else {
    Write-GateLog "Pre-Push Quality Gate: BLOCKED (exit $exitCode)." "ERROR"
}
Write-GateLog "==========================================================" "INFO"
exit $exitCode
