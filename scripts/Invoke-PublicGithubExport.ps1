# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Manual-only, default-deny 8-gate cleanroom GitHub source exporter for Project KONGOR V (PKV).

.DESCRIPTION
    Exports ONLY allowlisted cleanroom source, governance, configuration, test, and documentation files
    defined in config/public_export_manifest.json into an isolated temporary staging directory, enforces
    8 fail-closed security/IP/build/identity gates, writes artifacts/public_export_receipt.json, and stops
    in dry-run mode unless -Push and -GithubRepoUrl are explicitly supplied by a human operator.

    Invariants:
    1. NEVER modifies local git remotes ('origin' stays untouched on Azure DevOps).
    2. NEVER modifies local or global git user.name / user.email.
    3. NEVER exports Azure DevOps commit history, Git LFS pointer rules, retail game assets, or secrets/PII.
    4. Commits inside the isolated temporary repository strictly as:
       guccid0nut <guccid0nut@users.noreply.github.com>

    Exit Codes:
      0  : Success (Dry-Run Certified or Manual Push Complete)
      10 : Argument, Manifest, or Target Remote Guard Error
      11 : Gate 1 Failed (Default-Deny Allowlist / Forbidden Extension / File Size Limit)
      12 : Gate 2 Failed (Retail Binary / Overlay Asset SHA-256 Blocklist Collision)
      13 : Gate 3 Failed (Local PII & Workstation Path Leak Guard)
      14 : Gate 4 Failed (Secret Scrubbing & Gitleaks Audit)
      15 : Gate 5 Failed (Zero-LFS .gitattributes, Submodules, or Multi-License Governance Gate)
      16 : Gate 6 Failed (Isolated Cleanroom CMake + Release .NET Build + 12/12 Unit Test Gate)
      17 : Gate 7 Failed (Scoped Cleanroom Git Commit & Identity Verification Gate)
      18 : Gate 8 Failed (Receipt Serialization or Manual Git Push Error)

.EXAMPLE
    .\scripts\Invoke-PublicGithubExport.ps1
    Runs all 8 gates in Dry-Run mode (default) and writes artifacts/public_export_receipt.json.

.EXAMPLE
    .\scripts\Invoke-PublicGithubExport.ps1 -GithubRepoUrl "https://github.com/guccid0nut/PKV.git" -Branch main -Push
    Runs all 8 gates and pushes the certified cleanroom snapshot directly from the temp repo to GitHub.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManifestPath = "",

    [Parameter(Mandatory = $false)]
    [string]$SourceRoot = "",

    [Parameter(Mandatory = $false)]
    [string]$GithubRepoUrl = "",

    [Parameter(Mandatory = $false)]
    [string]$Branch = "main",

    [Parameter(Mandatory = $false)]
    [switch]$Push = $false,

    [Parameter(Mandatory = $false)]
    [string]$ReceiptPath = "artifacts/public_export_receipt.json",

    [Parameter(Mandatory = $false)]
    [string]$PiiConfigPath = "",

    [Parameter(Mandatory = $false)]
    [string[]]$ExtraRetailHashDirs = @(),

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuildGate = $false,

    [Parameter(Mandatory = $false)]
    [string]$CanaryPostStageMutation = "",

    [Parameter(Mandatory = $false)]
    [string]$StagingParentDir = ""
)

$ErrorActionPreference = 'Stop'
$overallSw = [System.Diagnostics.Stopwatch]::StartNew()
$gateTimingsMs = [ordered]@{}

$scriptRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = $scriptRepoRoot
} else {
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $SourceRoot "config\public_export_manifest.json"
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        $ManifestPath = Join-Path $scriptRepoRoot "config\public_export_manifest.json"
    }
} elseif (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {
    $ManifestPath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $ManifestPath))
}

if (-not [System.IO.Path]::IsPathRooted($ReceiptPath)) {
    $ReceiptPath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $ReceiptPath))
}

function Write-ExportLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $false)][ValidateSet("INFO", "GATE", "PASS", "WARN", "FAIL")][string]$Level = "INFO"
    )
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $color = switch ($Level) {
        "GATE" { "Cyan" }
        "PASS" { "Green" }
        "WARN" { "Yellow" }
        "FAIL" { "Red" }
        default { "Gray" }
    }
    Write-Host "[$ts] [PUBLIC-EXPORT] [$Level] $Message" -ForegroundColor $color
}

function Stop-ExportWithActionableError {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$GateName,
        [Parameter(Mandatory = $true)][string]$WhatFailed,
        [Parameter(Mandatory = $true)][string]$HowToFix
    )
    Write-ExportLog "[$GateName] FAILED (ExitCode=$ExitCode): $WhatFailed" -Level "FAIL"
    Write-ExportLog "[$GateName] REMEDIATION: $HowToFix" -Level "WARN"
    exit $ExitCode
}

function Convert-GlobToRegex {
    param([Parameter(Mandatory = $true)][string]$Glob)
    $norm = ($Glob -replace '\\', '/').Trim().TrimStart('/')
    $tokenized = $norm
    $tokenized = $tokenized.Replace("**/", "__KONGOR_GLOBSTAR_SLASH__")
    $tokenized = $tokenized.Replace("/**", "__KONGOR_SLASH_GLOBSTAR__")
    $tokenized = $tokenized.Replace("**", "__KONGOR_GLOBSTAR__")
    $tokenized = $tokenized.Replace("*", "__KONGOR_STAR__")
    $tokenized = $tokenized.Replace("?", "__KONGOR_QMARK__")

    $escaped = [regex]::Escape($tokenized)
    $escaped = $escaped.Replace("__KONGOR_GLOBSTAR_SLASH__", "(?:.+/)?")
    $escaped = $escaped.Replace("__KONGOR_SLASH_GLOBSTAR__", "(?:/.*)?")
    $escaped = $escaped.Replace("__KONGOR_GLOBSTAR__", ".*")
    $escaped = $escaped.Replace("__KONGOR_STAR__", "[^/]*")
    $escaped = $escaped.Replace("__KONGOR_QMARK__", "[^/]")
    return [regex]::new("^$escaped$", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Compiled)
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hashBytes = $sha.ComputeHash($stream)
        return [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Find-GitleaksExecutable {
    $cmd = Get-Command "gitleaks" -ErrorAction SilentlyContinue
    if ($null -ne $cmd -and -not [string]::IsNullOrWhiteSpace($cmd.Source)) {
        return $cmd.Source
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $linksExe = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\gitleaks.exe"
        if (Test-Path -LiteralPath $linksExe -PathType Leaf) {
            return $linksExe
        }
        $pkgRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
        if (Test-Path -LiteralPath $pkgRoot -PathType Container) {
            $pkgDirs = @(Get-ChildItem -LiteralPath $pkgRoot -Directory -Filter "*gitleaks*" -ErrorAction SilentlyContinue)
            foreach ($pd in $pkgDirs) {
                $hits = @(Get-ChildItem -LiteralPath $pd.FullName -Filter "gitleaks.exe" -Recurse -File -ErrorAction SilentlyContinue)
                if ($hits.Count -gt 0) {
                    return $hits[0].FullName
                }
            }
        }
    }
    return $null
}

# -----------------------------------------------------------------------------
# Pre-Flight Argument & Safety Guards
# -----------------------------------------------------------------------------
Write-ExportLog "Starting Project KONGOR V Manual Public GitHub Export (Mode=$(if ($Push) { 'LIVE PUSH' } else { 'DRY-RUN CERTIFICATION' }))" -Level "GATE"
Write-ExportLog "SourceRoot='$SourceRoot' | ManifestPath='$ManifestPath' | Branch='$Branch'"

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
        -WhatFailed "Export manifest not found at '$ManifestPath'." `
        -HowToFix "Restore or create config/public_export_manifest.json and re-run ./scripts/Invoke-PublicGithubExport.ps1."
}

if ($SkipBuildGate) {
    if ($Push -or $env:KONGOR_EXPORT_CANARY_TEST -ne "1") {
        Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
            -WhatFailed "-SkipBuildGate is strictly forbidden outside hermetic canary self-tests (KONGOR_EXPORT_CANARY_TEST=1 without -Push)." `
            -HowToFix "Remove -SkipBuildGate and run full Gate 6 certification via ./scripts/Invoke-PublicGithubExport.ps1."
    }
}

if (-not [string]::IsNullOrWhiteSpace($CanaryPostStageMutation)) {
    if ($Push -or $env:KONGOR_EXPORT_CANARY_TEST -ne "1") {
        Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
            -WhatFailed "-CanaryPostStageMutation is strictly forbidden outside hermetic canary self-tests." `
            -HowToFix "Remove -CanaryPostStageMutation when running ./scripts/Invoke-PublicGithubExport.ps1."
    }
}

if ($Push) {
    if ([string]::IsNullOrWhiteSpace($GithubRepoUrl)) {
        Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
            -WhatFailed "-Push was specified without -GithubRepoUrl." `
            -HowToFix "Pass an explicit GitHub repository URL: ./scripts/Invoke-PublicGithubExport.ps1 -Push -GithubRepoUrl 'https://github.com/guccid0nut/<repo>.git'"
    }
    if ($GithubRepoUrl -match '(?i)(dev\.azure\.com|visualstudio\.com)') {
        Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
            -WhatFailed "Refusing to push cleanroom export to Azure DevOps URL '$GithubRepoUrl' (would overwrite full private repository history)." `
            -HowToFix "Pass a github.com repository URL to -GithubRepoUrl; push normal development commits to Azure DevOps via 'git push origin main'."
    }
    if ($GithubRepoUrl -notmatch '(?i)(github\.com|^file://|^[a-zA-Z]:[\\/]|^/)') {
        Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
            -WhatFailed "Target URL '$GithubRepoUrl' is not a recognized GitHub remote or local test bare repository." `
            -HowToFix "Provide an https://github.com/... or git@github.com:... repository URL."
    }
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$exportUserName = [string]$manifest.exportIdentity.userName
$exportUserEmail = [string]$manifest.exportIdentity.userEmail
if (($exportUserName -ne "guccidonut" -and $exportUserName -ne "guccid0nut") -or ($exportUserEmail -ne "guccidonut@users.noreply.github.com" -and $exportUserEmail -ne "guccid0nut@users.noreply.github.com")) {
    Stop-ExportWithActionableError -ExitCode 10 -GateName "PREFLIGHT" `
        -WhatFailed "Manifest exportIdentity must be strictly 'guccid0nut <guccid0nut@users.noreply.github.com>' or 'guccidonut <guccidonut@users.noreply.github.com>', got '$exportUserName <$exportUserEmail>'." `
        -HowToFix "Set exportIdentity in config/public_export_manifest.json."
}

$maxSingleFileBytes = if ($null -ne $manifest.maxSingleFileBytes) { [int64]$manifest.maxSingleFileBytes } else { 10485760L }
$includeRegexes = @($manifest.includeGlobs | ForEach-Object { Convert-GlobToRegex -Glob ([string]$_) })
$excludeRegexes = @($manifest.excludeGlobs | ForEach-Object { Convert-GlobToRegex -Glob ([string]$_) })
$forbiddenExtSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($ext in @($manifest.forbiddenExtensions)) {
    if (-not [string]::IsNullOrWhiteSpace([string]$ext)) {
        $normExt = ([string]$ext).Trim()
        if (-not $normExt.StartsWith(".")) { $normExt = ".$normExt" }
        [void]$forbiddenExtSet.Add($normExt)
    }
}

$exportRunId = [guid]::NewGuid().ToString("N").Substring(0, 12)
$baseTemp = if (-not [string]::IsNullOrWhiteSpace($StagingParentDir)) {
    $StagingParentDir
} elseif ((Test-Path "D:\temp") -and ((Get-PSDrive D -ErrorAction SilentlyContinue).Free -gt (Get-PSDrive C -ErrorAction SilentlyContinue).Free)) {
    "D:\temp"
} else {
    [System.IO.Path]::GetTempPath()
}
$stagingDir = Join-Path $baseTemp "kongor_public_export_$exportRunId"
$buildVerifyDir = Join-Path $baseTemp "kongor_public_verify_$exportRunId"

try {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Write-ExportLog "Created isolated cleanroom staging directory: $stagingDir"

    # -------------------------------------------------------------------------
    # Stage Candidate Files (Default-Deny Allowlist Copy)
    # -------------------------------------------------------------------------
    $stageSw = [System.Diagnostics.Stopwatch]::StartNew()
    $candidateRelPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $dotGitPath = Join-Path $SourceRoot ".git"

    if (Test-Path -LiteralPath $dotGitPath) {
        $indexLines = @(& git -C $SourceRoot ls-files -s)
        if ($LASTEXITCODE -ne 0) {
            Stop-ExportWithActionableError -ExitCode 10 -GateName "STAGE" `
                -WhatFailed "git ls-files -s failed in '$SourceRoot'." `
                -HowToFix "Verify '$SourceRoot' is a healthy git repository via 'git status'."
        }
        foreach ($line in $indexLines) {
            if ($line -match '^(\d+)\s+[0-9a-fA-F]+\s+\d+\t(.+)$') {
                $mode = $Matches[1]
                $relPath = ($Matches[2] -replace '\\', '/').Trim()
                if ($mode -eq "160000") { continue } # Submodule gitlinks handled explicitly via allowedSubmodules
                [void]$candidateRelPaths.Add($relPath)
            }
        }
        $untrackedLines = @(& git -C $SourceRoot ls-files --others --exclude-standard)
        if ($LASTEXITCODE -eq 0) {
            foreach ($uLine in $untrackedLines) {
                $relU = ($uLine -replace '\\', '/').Trim()
                if (-not [string]::IsNullOrWhiteSpace($relU)) {
                    [void]$candidateRelPaths.Add($relU)
                }
            }
        }
    } else {
        $allDiskFiles = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Force | Where-Object {
            $_.FullName -notmatch '[\\/](?:\.git|games|bin|obj|build|node_modules)[\\/]'
        })
        foreach ($df in $allDiskFiles) {
            $relD = $df.FullName.Substring($SourceRoot.Length).TrimStart('\', '/') -replace '\\', '/'
            [void]$candidateRelPaths.Add($relD)
        }
    }

    $stagedRelFiles = New-Object System.Collections.Generic.List[string]
    foreach ($rel in ($candidateRelPaths | Sort-Object)) {
        # Generated cleanroom root git metadata files are written deterministically below
        if ($rel -in @(".gitattributes", ".gitmodules", ".gitignore")) { continue }

        $matchedInclude = $false
        foreach ($rx in $includeRegexes) {
            if ($rx.IsMatch($rel)) {
                $matchedInclude = $true
                break
            }
        }
        if (-not $matchedInclude) { continue }

        $matchedExclude = $false
        foreach ($exRx in $excludeRegexes) {
            if ($exRx.IsMatch($rel)) {
                $matchedExclude = $true
                break
            }
        }
        if ($matchedExclude) { continue }

        $srcFull = Join-Path $SourceRoot ($rel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $srcFull -PathType Leaf)) { continue }

        $dstFull = Join-Path $stagingDir ($rel -replace '/', '\')
        $dstParent = [System.IO.Path]::GetDirectoryName($dstFull)
        if (-not (Test-Path -LiteralPath $dstParent -PathType Container)) {
            [void][System.IO.Directory]::CreateDirectory($dstParent)
        }
        [System.IO.File]::Copy($srcFull, $dstFull, $true)
        $stagedRelFiles.Add($rel)
    }

    # Apply staging-only cleanroom placeholder substitutions (keeps local workspace & Azure DevOps 100% untouched)
    foreach ($subEntry in @($manifest.cleanroomSubstitutions)) {
        if ($null -eq $subEntry) { continue }
        $subSrcRel = ([string]$subEntry.source -replace '\\', '/').Trim().TrimStart('/')
        $subDstRel = ([string]$subEntry.destination -replace '\\', '/').Trim().TrimStart('/')
        if ([string]::IsNullOrWhiteSpace($subSrcRel) -or [string]::IsNullOrWhiteSpace($subDstRel)) { continue }

        $subSrcFull = Join-Path $SourceRoot ($subSrcRel -replace '/', '\')
        $origTargetInSource = Join-Path $SourceRoot ($subDstRel -replace '/', '\')
        if (Test-Path -LiteralPath $subSrcFull -PathType Leaf) {
            $subDstFull = Join-Path $stagingDir ($subDstRel -replace '/', '\')
            $subDstParent = [System.IO.Path]::GetDirectoryName($subDstFull)
            if (-not (Test-Path -LiteralPath $subDstParent -PathType Container)) {
                [void][System.IO.Directory]::CreateDirectory($subDstParent)
            }
            [System.IO.File]::Copy($subSrcFull, $subDstFull, $true)
            if (-not $stagedRelFiles.Contains($subDstRel)) {
                $stagedRelFiles.Add($subDstRel)
            }
            Write-ExportLog "Applied staging-only cleanroom substitution: $subSrcRel -> $subDstRel" -Level "INFO"
        } elseif (Test-Path -LiteralPath $origTargetInSource -PathType Leaf) {
            Stop-ExportWithActionableError -ExitCode 10 -GateName "STAGE-SUBSTITUTION" `
                -WhatFailed "Cleanroom placeholder '$subSrcRel' is missing for retail asset '$subDstRel'." `
                -HowToFix "Restore '$subSrcRel' or update cleanroomSubstitutions in config/public_export_manifest.json."
        }
    }

    # Write cleanroom .gitattributes (0 Git LFS rules; preserves LF integrity pins across Windows/Linux checkouts)
    $cleanGitattributes = @(
        "# Cleanroom Public Export .gitattributes (Zero Git LFS)",
        ".githooks/* text eol=lf",
        "*.sh text eol=lf",
        "*.command text eol=lf",
        "scripts/gate_check.ps1 text eol=lf",
        "scripts/test_gate_mutants.ps1 text eol=lf",
        "scripts/verify_netcode_oracle.ps1 text eol=lf",
        "scripts/test_netcode_mutants.ps1 text eol=lf",
        "*.lua text eol=lf",
        "*.package text eol=lf",
        "*.interface text eol=lf",
        "*.effect text eol=lf",
        "*.vsh text eol=lf",
        "*.psh text eol=lf",
        "*.honreplay binary -text",
        "*.png binary -text",
        "*.webp binary -text",
        "*.ico binary -text",
        "*.gif binary -text",
        "*.ttf binary -text",
        ""
    ) -join "`n"
    [System.IO.File]::WriteAllBytes((Join-Path $stagingDir ".gitattributes"), [System.Text.Encoding]::UTF8.GetBytes($cleanGitattributes))
    $stagedRelFiles.Add(".gitattributes")

    # Write cleanroom .gitmodules (strictly allowedSubmodules only)
    $subLines = New-Object System.Collections.Generic.List[string]
    foreach ($sub in @($manifest.allowedSubmodules)) {
        $sPath = ([string]$sub.path).Trim()
        $sUrl = ([string]$sub.url).Trim()
        $subLines.Add("[submodule `"$sPath`"]")
        $subLines.Add("`tpath = $sPath")
        $subLines.Add("`turl = $sUrl")
        $subLines.Add("")
    }
    [System.IO.File]::WriteAllBytes((Join-Path $stagingDir ".gitmodules"), [System.Text.Encoding]::UTF8.GetBytes(($subLines -join "`n")))
    $stagedRelFiles.Add(".gitmodules")

    # Write cleanroom .gitignore (standard C#/C++/Python ignores + local game overlay mounts)
    $cleanGitignore = @(
        "# Project KONGOR V -- Cleanroom Public Repository .gitignore",
        "bin/",
        "obj/",
        "build/",
        "native/build/",
        "TestResults/",
        "logs/",
        ".vs/",
        ".idea/",
        ".agents/*",
        "!.agents/rules/",
        "!.agents/rules/**",
        "!.agents/skills/",
        "!.agents/skills/**",
        "data/gate/*.cache",
        "node_modules/",
        "__pycache__/",
        "*.pyc",
        "*.user",
        "*.suo",
        "*.bak",
        "*.bak.*",
        "*.bak_*",
        "*.bak-*",
        "*_bak",
        "*.dmp",
        "*.trx",
        "games/",
        "config/local_pii_guard.local.json",
        "*.pii.local.json",
        "artifacts/public_export_receipt.json",
        ""
    ) -join "`n"
    [System.IO.File]::WriteAllBytes((Join-Path $stagingDir ".gitignore"), [System.Text.Encoding]::UTF8.GetBytes($cleanGitignore))
    $stagedRelFiles.Add(".gitignore")

    # Optional hermetic canary post-stage mutation hook (strictly blocked unless KONGOR_EXPORT_CANARY_TEST=1 and -not $Push)
    if ($env:KONGOR_EXPORT_CANARY_TEST -eq "1" -and (-not $Push) -and (-not [string]::IsNullOrWhiteSpace($CanaryPostStageMutation))) {
        Write-ExportLog "[CANARY] Applying synthetic post-stage mutation: $CanaryPostStageMutation" -Level "WARN"
        switch ($CanaryPostStageMutation) {
            "inject_forbidden_dll" {
                [System.IO.File]::WriteAllBytes((Join-Path $stagingDir "planted_canary.dll"), [byte[]]@(0x4D, 0x5A, 0x90, 0x00))
            }
            "inject_forbidden_s2z" {
                [System.IO.File]::WriteAllBytes((Join-Path $stagingDir "resources0_canary.s2z"), [byte[]]@(0x50, 0x4B, 0x03, 0x04))
            }
            "inject_git_lfs" {
                Add-Content -LiteralPath (Join-Path $stagingDir ".gitattributes") -Value "`n*.s2z filter=lfs diff=lfs merge=lfs -text`n"
            }
            "inject_rogue_submodule" {
                Add-Content -LiteralPath (Join-Path $stagingDir ".gitmodules") -Value "`n[submodule `"src/tools/proximo`"]`n`tpath = src/tools/proximo`n`turl = https://github.com/Piotrixek/Proximo.git`n"
            }
            "remove_governance_license" {
                Remove-Item -LiteralPath (Join-Path $stagingDir "LICENSE") -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $stageSw.Stop()
    $gateTimingsMs["stage_copy"] = $stageSw.ElapsedMilliseconds
    Write-ExportLog "Staged $($stagedRelFiles.Count) cleanroom files in $($stageSw.ElapsedMilliseconds)ms." -Level "INFO"

    # -------------------------------------------------------------------------
    # Gate 1: Default-Deny Allowlist, Forbidden Binary Extensions & Size Cap
    # -------------------------------------------------------------------------
    $g1Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 1: Default-Deny Allowlist, Forbidden Extensions & Size Cap..." -Level "GATE"

    $allStagedFiles = @(Get-ChildItem -LiteralPath $stagingDir -Recurse -File -Force | Where-Object {
        $_.FullName -notmatch '[\\/]\.git[\\/]'
    })
    if ($allStagedFiles.Count -eq 0) {
        Stop-ExportWithActionableError -ExitCode 11 -GateName "GATE-1-ALLOWLIST" `
            -WhatFailed "Staging directory '$stagingDir' contains 0 files." `
            -HowToFix "Verify includeGlobs in config/public_export_manifest.json match cleanroom repository paths."
    }

    $g1Violations = New-Object System.Collections.Generic.List[string]
    $totalBytes = 0L
    foreach ($sf in $allStagedFiles) {
        $relSf = $sf.FullName.Substring($stagingDir.Length).TrimStart('\', '/') -replace '\\', '/'
        $totalBytes += $sf.Length
        $ext = [System.IO.Path]::GetExtension($sf.Name)
        if (-not [string]::IsNullOrEmpty($ext) -and $forbiddenExtSet.Contains($ext)) {
            $g1Violations.Add("Forbidden binary/archive extension '$ext': $relSf")
        }
        if ($sf.Length -gt $maxSingleFileBytes) {
            $g1Violations.Add("File exceeds maxSingleFileBytes ($($sf.Length) > $maxSingleFileBytes): $relSf")
        }
    }

    $g1Sw.Stop()
    $gateTimingsMs["gate1_allowlist_ms"] = $g1Sw.ElapsedMilliseconds
    if ($g1Violations.Count -gt 0) {
        $preview = ($g1Violations | Select-Object -First 10) -join "; "
        Stop-ExportWithActionableError -ExitCode 11 -GateName "GATE-1-ALLOWLIST" `
            -WhatFailed "Detected $($g1Violations.Count) forbidden binary/oversized file(s) in staging: $preview" `
            -HowToFix "Add the offending path(s) to excludeGlobs in config/public_export_manifest.json and re-run ./scripts/Invoke-PublicGithubExport.ps1."
    }
    Write-ExportLog "Gate 1 PASSED ($($allStagedFiles.Count) files, $([Math]::Round($totalBytes / 1MB, 2)) MiB, 0 forbidden extensions) in $($g1Sw.ElapsedMilliseconds)ms." -Level "PASS"

    # -------------------------------------------------------------------------
    # Gate 2: Retail Binary & Overlay Asset SHA-256 Blocklist Audit
    # -------------------------------------------------------------------------
    $g2Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 2: Retail Asset & Overlay SHA-256 Blocklist Audit..." -Level "GATE"

    $retailBlocklistHashes = @{}
    $overlayScanRoots = @(
        (Join-Path $SourceRoot "games\dawnbringer-overlay"),
        (Join-Path $SourceRoot "games\control-panel-overlay"),
        (Join-Path $SourceRoot "games\willowmaker-overlay"),
        (Join-Path $SourceRoot "games\hud-icons-overlay"),
        (Join-Path $SourceRoot "assets\hud\icons"),
        (Join-Path $SourceRoot "src\apps\control-panel\Assets\models"),
        (Join-Path $SourceRoot "src\apps\control-panel\Assets\sounds"),
        (Join-Path $SourceRoot "src\services\web-ui\wwwroot\images\abilities"),
        (Join-Path $SourceRoot "src\services\web-ui\wwwroot\images\heroes"),
        (Join-Path $SourceRoot "src\services\web-ui\wwwroot\images\items"),
        (Join-Path $SourceRoot "src\services\web-ui\wwwroot\images\maps"),
        (Join-Path $SourceRoot "tools\ui-playground\static\assets"),
        (Join-Path $SourceRoot "scripts\fixtures")
    ) + @($ExtraRetailHashDirs)

    $individualRetailFiles = @(
        (Join-Path $SourceRoot "src\apps\willowmaker\Assets\caldavar.png"),
        (Join-Path $SourceRoot "src\apps\willowmaker\Assets\willowmaker.png"),
        (Join-Path $SourceRoot "src\apps\willowmaker\Assets\willowmaker.ico"),
        (Join-Path $SourceRoot "infra\flatpak\net.kongor.Willowmaker.png"),
        (Join-Path $SourceRoot "src\apps\compel\Assets\compel.ico"),
        (Join-Path $SourceRoot "src\services\master-server\Resources\Icons\default.png"),
        (Join-Path $SourceRoot "src\services\web-ui\wwwroot\ability_metadata.json"),
        (Join-Path $SourceRoot "src\services\web-ui\wwwroot\item_metadata.json"),
        (Join-Path $SourceRoot "src\libs\database-context\Data\HeroGuides.json"),
        (Join-Path $SourceRoot "docs\ACTIVE_SESSION_STATE.md"),
        (Join-Path $SourceRoot "src\apps\control-panel\Assets\web\empath_cn.glb")
    )
    foreach ($irf in $individualRetailFiles) {
        if (Test-Path -LiteralPath $irf -PathType Leaf) {
            $irfItem = Get-Item -LiteralPath $irf
            if ($irfItem.Length -gt 0 -and $irfItem.Length -le 52428800L) {
                $irfHash = Get-FileSha256Hex -Path $irfItem.FullName
                if (-not $retailBlocklistHashes.ContainsKey($irfHash)) {
                    $retailBlocklistHashes[$irfHash] = $irfItem.FullName
                }
            }
        }
    }

    foreach ($oRoot in $overlayScanRoots) {
        if (-not [string]::IsNullOrWhiteSpace($oRoot) -and (Test-Path -LiteralPath $oRoot -PathType Container)) {
            foreach ($rf in @(Get-ChildItem -LiteralPath $oRoot -Recurse -File -ErrorAction SilentlyContinue)) {
                if ($rf.Length -gt 0 -and $rf.Length -le 52428800L) {
                    $rHash = Get-FileSha256Hex -Path $rf.FullName
                    if (-not $retailBlocklistHashes.ContainsKey($rHash)) {
                        $retailBlocklistHashes[$rHash] = $rf.FullName
                    }
                }
            }
        }
    }
    $clientRoot = Join-Path $SourceRoot "games\client"
    if (Test-Path -LiteralPath $clientRoot -PathType Container) {
        foreach ($cf in @(Get-ChildItem -LiteralPath $clientRoot -File -ErrorAction SilentlyContinue)) {
            if ($cf.Length -gt 0 -and $cf.Length -le 52428800L -and $cf.Extension -notmatch '^(?i)\.(json|txt|cfg|conf|ini|log|xml)$') {
                $cHash = Get-FileSha256Hex -Path $cf.FullName
                if (-not $retailBlocklistHashes.ContainsKey($cHash)) {
                    $retailBlocklistHashes[$cHash] = $cf.FullName
                }
            }
        }
    }

    $stagedHashMap = [ordered]@{}
    $g2Collisions = New-Object System.Collections.Generic.List[string]
    foreach ($sf in ($allStagedFiles | Sort-Object FullName)) {
        $relSf = $sf.FullName.Substring($stagingDir.Length).TrimStart('\', '/') -replace '\\', '/'
        $sHash = Get-FileSha256Hex -Path $sf.FullName
        $stagedHashMap[$relSf] = $sHash
        if ($retailBlocklistHashes.ContainsKey($sHash)) {
            $g2Collisions.Add("$relSf matches retail/overlay asset '$($retailBlocklistHashes[$sHash])' (sha256:$($sHash.Substring(0, 16))...)")
        }
    }

    $g2Sw.Stop()
    $gateTimingsMs["gate2_retail_sha256_ms"] = $g2Sw.ElapsedMilliseconds
    if ($g2Collisions.Count -gt 0) {
        $preview = ($g2Collisions | Select-Object -First 10) -join "; "
        Stop-ExportWithActionableError -ExitCode 12 -GateName "GATE-2-RETAIL-SHA256" `
            -WhatFailed "Detected $($g2Collisions.Count) staged file(s) byte-identical to retail/overlay assets: $preview" `
            -HowToFix "Move retail assets to games/*-overlay/ and replace any committed placeholder with cleanroom PKV geometry, then re-run ./scripts/Invoke-PublicGithubExport.ps1."
    }
    Write-ExportLog "Gate 2 PASSED ($($retailBlocklistHashes.Count) retail hashes checked, 0 SHA-256 collisions) in $($g2Sw.ElapsedMilliseconds)ms." -Level "PASS"

    # -------------------------------------------------------------------------
    # Gate 3: Zero Personal PII & Workstation Path Leak Guard
    # -------------------------------------------------------------------------
    $g3Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 3: Local PII Denylist & Workstation Path Guard..." -Level "GATE"

    $scrubLibPath = Join-Path $scriptRepoRoot "scripts\lib\Test-KongorSecretScrub.ps1"
    if (-not (Test-Path -LiteralPath $scrubLibPath -PathType Leaf)) {
        Stop-ExportWithActionableError -ExitCode 13 -GateName "GATE-3-PII" `
            -WhatFailed "Missing secret/PII scrub library at '$scrubLibPath'." `
            -HowToFix "Restore scripts/lib/Test-KongorSecretScrub.ps1."
    }
    . $scrubLibPath

    if ([string]::IsNullOrWhiteSpace($PiiConfigPath)) {
        $candPii = Join-Path $SourceRoot "config\local_pii_guard.local.json"
        if (Test-Path -LiteralPath $candPii -PathType Leaf) {
            $PiiConfigPath = $candPii
        } else {
            $PiiConfigPath = Join-Path $scriptRepoRoot "config\local_pii_guard.local.json"
        }
    }

    $stagedFullPaths = @($allStagedFiles | ForEach-Object { $_.FullName })
    $piiResult = Test-KongorLocalPiiGuard -RepoRoot $stagingDir -ConfigPath $PiiConfigPath -CandidateFiles $stagedFullPaths -RequireDenylist

    $auxPiiOverlayCount = 0
    $piiFindingsAll = New-Object System.Collections.Generic.List[object]
    $piiFailedReason = ""
    if (-not $piiResult.Passed) {
        $piiFailedReason = $piiResult.Reason
        foreach ($f in @($piiResult.Findings)) { $piiFindingsAll.Add($f) }
    } elseif (-not [string]::IsNullOrWhiteSpace($PiiConfigPath)) {
        $resolvedPrimaryPii = [System.IO.Path]::GetFullPath($PiiConfigPath)
        $piiConfigDir = [System.IO.Path]::GetDirectoryName($resolvedPrimaryPii)
        if (-not [string]::IsNullOrWhiteSpace($piiConfigDir) -and (Test-Path -LiteralPath $piiConfigDir -PathType Container)) {
            $auxPiiFiles = @(Get-ChildItem -LiteralPath $piiConfigDir -Filter "*.pii.local.json" -File -ErrorAction SilentlyContinue | Sort-Object FullName)
            foreach ($auxPii in $auxPiiFiles) {
                $resolvedAuxPii = [System.IO.Path]::GetFullPath($auxPii.FullName)
                if ([System.StringComparer]::OrdinalIgnoreCase.Equals($resolvedAuxPii, $resolvedPrimaryPii)) { continue }
                $auxPiiOverlayCount++
                $auxResult = Test-KongorLocalPiiGuard -RepoRoot $stagingDir -ConfigPath $resolvedAuxPii -CandidateFiles $stagedFullPaths -RequireDenylist
                if (-not $auxResult.Passed) {
                    if ([string]::IsNullOrWhiteSpace($piiFailedReason)) {
                        $piiFailedReason = "Auxiliary local PII overlay '$($auxPii.Name)' blocked export: $($auxResult.Reason)"
                    }
                    foreach ($af in @($auxResult.Findings)) { $piiFindingsAll.Add($af) }
                }
            }
        }
    }

    $g3Sw.Stop()
    $gateTimingsMs["gate3_pii_guard_ms"] = $g3Sw.ElapsedMilliseconds

    if (-not [string]::IsNullOrWhiteSpace($piiFailedReason)) {
        $preview = @($piiFindingsAll | Select-Object -First 10 | ForEach-Object { "$($_.file_path) [$($_.rule_name): $($_.masked_snippet)]" }) -join "; "
        Stop-ExportWithActionableError -ExitCode 13 -GateName "GATE-3-PII" `
            -WhatFailed "$piiFailedReason Findings: $preview" `
            -HowToFix "Scrub personal/privacy identifiers or hardcoded Windows user paths (file:///C:/Users/...) in the listed file(s) and verify local *.pii.local.json denylist overlays."
    }
    Write-ExportLog "Gate 3 PASSED (0 PII or workstation path hits across $($stagedFullPaths.Count) staged files; auxOverlays=$auxPiiOverlayCount) in $($g3Sw.ElapsedMilliseconds)ms." -Level "PASS"

    $profilerFirewallPath = Join-Path $scriptRepoRoot "scripts\re\Assert-NoDevProfilerLeak.ps1"
    if (Test-Path -LiteralPath $profilerFirewallPath -PathType Leaf) {
        try {
            & $profilerFirewallPath -RootDir $SourceRoot -Quiet
            & $profilerFirewallPath -RootDir $stagingDir -Quiet
        } catch {
            Stop-ExportWithActionableError -ExitCode 13 -GateName "GATE-3B-PROFILER-FIREWALL" `
                -WhatFailed $_.Exception.Message `
                -HowToFix "Rebuild clean Release vid_vulkan_x64.dll with -DKONGOR_ENABLE_TRACY=OFF before running Invoke-PublicGithubExport.ps1."
        }
    }

    # -------------------------------------------------------------------------
    # Gate 4: Zero Secrets (Test-KongorSecretLeakage + Gitleaks CLI)
    # -------------------------------------------------------------------------
    $g4Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 4: Secret Scrubbing & Gitleaks Audit..." -Level "GATE"

    $secretResult = Test-KongorSecretLeakage -RepoRoot $stagingDir -CandidateFiles $stagedFullPaths
    if (-not $secretResult.Passed) {
        $preview = @($secretResult.Leaks | Select-Object -First 10 | ForEach-Object { "$($_.file_path) [$($_.rule_name): $($_.masked_snippet)]" }) -join "; "
        Stop-ExportWithActionableError -ExitCode 14 -GateName "GATE-4-SECRETS" `
            -WhatFailed "Test-KongorSecretLeakage found $($secretResult.TotalLeaks) secret pattern hit(s): $preview" `
            -HowToFix "Remove or replace real credentials/tokens with documented synthetic placeholders and re-run ./scripts/Invoke-PublicGithubExport.ps1."
    }

    $gitleaksExe = Find-GitleaksExecutable
    if ([string]::IsNullOrWhiteSpace($gitleaksExe)) {
        if ($env:GITHUB_ACTIONS -eq "true" -and $env:KONGOR_EXPORT_CANARY_TEST -eq "1" -and (-not $Push)) {
            Write-ExportLog "[CANARY-CI] gitleaks not preinstalled on hosted runner; Test-KongorSecretLeakage passed." -Level "WARN"
        } else {
            Stop-ExportWithActionableError -ExitCode 14 -GateName "GATE-4-SECRETS" `
                -WhatFailed "gitleaks CLI executable not found on PATH or WinGet packages (fail-closed requirement)." `
                -HowToFix "Install gitleaks via 'winget install Gitleaks.Gitleaks' and re-run ./scripts/Invoke-PublicGithubExport.ps1."
        }
    } else {
        $gitleaksConfig = Join-Path $stagingDir ".gitleaks.toml"
        if (-not (Test-Path -LiteralPath $gitleaksConfig -PathType Leaf)) {
            $gitleaksConfig = Join-Path $scriptRepoRoot ".gitleaks.toml"
        }
        $prevGlEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        Push-Location $stagingDir
        try {
            $gitleaksOut = @(& $gitleaksExe dir . --config $gitleaksConfig --redact --no-banner -v 2>&1 | ForEach-Object { [string]$_ })
            $glExit = $LASTEXITCODE
        } finally {
            Pop-Location
            $ErrorActionPreference = $prevGlEap
        }
        if ($glExit -ne 0) {
            $glDetails = @($gitleaksOut | Where-Object { $_ -match '^\s*(RuleID|File|Line):|leaks found' })
            $glSummary = if ($glDetails.Count -gt 0) { ($glDetails | Select-Object -First 20) -join " | " } else { ($gitleaksOut | Select-Object -Last 12) -join " | " }
            Stop-ExportWithActionableError -ExitCode 14 -GateName "GATE-4-SECRETS" `
                -WhatFailed "gitleaks detected potential secret(s) in staging tree (exit=$glExit): $glSummary" `
                -HowToFix "Inspect with '$gitleaksExe dir `"$stagingDir`" --config `"$gitleaksConfig`" --redact -v' and scrub the flagged file(s)."
        }
    }

    $g4Sw.Stop()
    $gateTimingsMs["gate4_secrets_ms"] = $g4Sw.ElapsedMilliseconds
    Write-ExportLog "Gate 4 PASSED (Test-KongorSecretLeakage=0, gitleaks=0) in $($g4Sw.ElapsedMilliseconds)ms." -Level "PASS"

    # -------------------------------------------------------------------------
    # Gate 5: Zero-LFS .gitattributes, Submodules & Multi-License Governance
    # -------------------------------------------------------------------------
    $g5Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 5: Zero-LFS, Submodule Allowlist & Multi-License Governance Audit..." -Level "GATE"

    $gitattrRaw = [System.IO.File]::ReadAllText((Join-Path $stagingDir ".gitattributes"))
    if ($gitattrRaw -match '(?i)filter\s*=\s*lfs') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "Staged .gitattributes contains forbidden 'filter=lfs' rule(s)." `
            -HowToFix "Strip all Git LFS rules from exported .gitattributes so public GitHub clones consume 0 LFS quota."
    }

    $gitmodulesRaw = [System.IO.File]::ReadAllText((Join-Path $stagingDir ".gitmodules"))
    $allowedSubPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($sub in @($manifest.allowedSubmodules)) {
        [void]$allowedSubPaths.Add(([string]$sub.path).Trim())
    }
    foreach ($sm in [regex]::Matches($gitmodulesRaw, '(?m)^\s*path\s*=\s*(.+?)\s*$')) {
        $foundSubPath = $sm.Groups[1].Value.Trim()
        if (-not $allowedSubPaths.Contains($foundSubPath)) {
            Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                -WhatFailed "Staged .gitmodules contains unapproved submodule path '$foundSubPath'." `
                -HowToFix "Restrict exported submodules strictly to allowedSubmodules in config/public_export_manifest.json."
        }
    }

    foreach ($govRel in @($manifest.requiredGovernanceFiles)) {
        $govFull = Join-Path $stagingDir (([string]$govRel) -replace '/', '\')
        if (-not (Test-Path -LiteralPath $govFull -PathType Leaf) -or (Get-Item -LiteralPath $govFull).Length -lt 32) {
            Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                -WhatFailed "Missing or empty required governance file '$govRel' in staging directory." `
                -HowToFix "Ensure '$govRel' exists at the repository path and is included in config/public_export_manifest.json."
        }
    }

    $rootLicenseText = [System.IO.File]::ReadAllText((Join-Path $stagingDir "LICENSE"))
    $nativeLicenseText = [System.IO.File]::ReadAllText((Join-Path $stagingDir "native\LICENSE"))
    $docsLicenseText = [System.IO.File]::ReadAllText((Join-Path $stagingDir "docs\LICENSE.md"))
    if ($rootLicenseText -notmatch 'GNU AFFERO GENERAL PUBLIC LICENSE') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "Root LICENSE does not contain GNU AFFERO GENERAL PUBLIC LICENSE." `
            -HowToFix "Restore AGPL-3.0-or-later text in root LICENSE."
    }
    if ($nativeLicenseText -notmatch 'Mozilla Public License\s+Version 2\.0') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "native/LICENSE does not contain Mozilla Public License Version 2.0." `
            -HowToFix "Restore MPL-2.0 text in native/LICENSE."
    }
    if ($docsLicenseText -notmatch 'CC BY-NC-SA 4\.0|Attribution-NonCommercial-ShareAlike 4\.0') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "docs/LICENSE.md does not contain CC BY-NC-SA 4.0 terms." `
            -HowToFix "Restore CC BY-NC-SA 4.0 license summary in docs/LICENSE.md."
    }

    $citationText = [System.IO.File]::ReadAllText((Join-Path $stagingDir "CITATION.cff"))
    if ($citationText -notmatch 'guccidonut' -or $citationText -notmatch 'AGPL-3\.0-or-later') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "CITATION.cff is missing mandatory author ('guccidonut') or license ('AGPL-3.0-or-later')." `
            -HowToFix "Restore CITATION.cff at the repository root."
    }

    $noticeText = [System.IO.File]::ReadAllText((Join-Path $stagingDir "NOTICE"))
    if ($noticeText -notmatch 'guccidonut' -or $noticeText -notmatch 'TAO' -or $noticeText -notmatch 'Section 7\(b\)') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "NOTICE is missing mandatory AGPL-3.0 Section 7(b) authorship preservation for guccidonut / TAO MCP." `
            -HowToFix "Restore AGPL-3.0 Section 7(b) attribution terms in NOTICE."
    }

    $dmcaText = [System.IO.File]::ReadAllText((Join-Path $stagingDir "INTEROPERABILITY_AND_DMCA.md"))
    if ($dmcaText -notmatch '17 U\.S\.C\.|Interoperability') {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "INTEROPERABILITY_AND_DMCA.md is missing interoperability governance disclosures." `
            -HowToFix "Restore INTEROPERABILITY_AND_DMCA.md at the repository root."
    }

    if (Test-Path -LiteralPath (Join-Path $stagingDir ".github\dependabot.yml") -PathType Leaf) {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed ".github/dependabot.yml is staged in public export (would spam automated PRs against one-way cleanroom mirror)." `
            -HowToFix "Keep .github/dependabot.yml in excludeGlobs inside config/public_export_manifest.json."
    }

    if (Test-Path -LiteralPath (Join-Path $stagingDir ".git-blame-ignore-revs") -PathType Leaf) {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed ".git-blame-ignore-revs is staged in public export (leaks private commit SHAs into 1-commit orphan repository)." `
            -HowToFix "Keep .git-blame-ignore-revs in excludeGlobs inside config/public_export_manifest.json."
    }

    foreach ($reqLicRel in @("LICENSES\AGPL-3.0-or-later.txt", "LICENSES\MPL-2.0.txt", "LICENSES\CC-BY-NC-SA-4.0.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDir $reqLicRel) -PathType Leaf)) {
            Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                -WhatFailed "Required REUSE license text '$($reqLicRel -replace '\\', '/')' is missing from staged tree." `
                -HowToFix "Ensure LICENSES/** is included in config/public_export_manifest.json and canonical license texts exist in LICENSES/."
        }
    }

    # Audit SPDX License-Boundary Integrity, Vendor Non-Contamination, & 7-Bit ASCII Compliance
    $kongorCliCsTotal = 0
    $kongorCliCsStamped = 0
    $nativeProxiesTotal = 0
    $nativeProxiesStamped = 0
    foreach ($sf in $allStagedFiles) {
        $extLower = $sf.Extension.ToLowerInvariant()
        if ($extLower -notin @(".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".ps1", ".js")) {
            continue
        }
        $relNorm = $sf.FullName.Substring($stagingDir.Length).TrimStart('\', '/') -replace '\\', '/'
        $headLines = @(Get-Content -LiteralPath $sf.FullName -TotalCount 12 -ErrorAction SilentlyContinue)
        $headJoined = $headLines -join "`n"

        foreach ($hl in $headLines) {
            if ($hl -match 'SPDX-(License-Identifier|FileCopyrightText):') {
                foreach ($ch in $hl.ToCharArray()) {
                    $cp = [int][char]$ch
                    if ($cp -gt 127) {
                        $hexCp = "U+" + $cp.ToString("X4")
                        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                            -WhatFailed "Non-ASCII character ($hexCp) detected on SPDX header line in '$relNorm'." `
                            -HowToFix "Replace non-ASCII characters (e.g. em-dash) with pure 7-bit ASCII '--' in '$relNorm'."
                    }
                }
            }
        }

        if ($relNorm -match '^(native/deps/|native/vulkan-layer/deps/|native/vulkan-layer/include/vulkan/|native/vulkan-layer/include/vk_video/|native/include/concurrentqueue\.h$|native/tools/luac51/lua-5\.1\.5/)') {
            if ($headJoined -match '(?i)SPDX-FileCopyrightText:.*guccidonut') {
                Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                    -WhatFailed "Vendored third-party file '$relNorm' was improperly stamped with guccidonut copyright." `
                    -HowToFix "Restore original upstream file header in '$relNorm' and keep vendor paths in SKIP_VENDOR."
            }
            continue
        }

        if ($relNorm -match '^src/tools/blender/io_scene_hon/.*\.py$' -and $headJoined -match 'SPDX-License-Identifier:\s*(.+)') {
            $blenderSpdx = $Matches[1].Trim()
            if ($blenderSpdx -ne 'GPL-3.0-or-later') {
                Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                    -WhatFailed "Blender add-on file '$relNorm' declares SPDX '$blenderSpdx' instead of 'GPL-3.0-or-later'." `
                    -HowToFix "Use GPL-3.0-or-later + Anton Romanov / K2-Blender Contributors attribution in '$relNorm'."
            }
        }

        if ($relNorm -match '^(native/ebpf/|infra/ebpf/).*\.(c|h)$' -and $headJoined -match 'SPDX-License-Identifier:\s*(.+)') {
            $ebpfSpdx = $Matches[1].Trim()
            if ($ebpfSpdx -ne 'GPL-2.0-only OR BSD-2-Clause') {
                Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                    -WhatFailed "eBPF kernel program '$relNorm' declares SPDX '$ebpfSpdx' instead of 'GPL-2.0-only OR BSD-2-Clause'." `
                    -HowToFix "Use 'GPL-2.0-only OR BSD-2-Clause' SPDX header for eBPF kernel programs."
            }
        }

        if ($relNorm -match '^native/(drivers|include|proxies|server|shared|tests|tools/kongor-model-viewer|tools/kongor-physics-sandbox|vulkan-layer/src)/') {
            if ($headJoined -match 'SPDX-License-Identifier:\s*(.+)') {
                $spdxId = $Matches[1].Trim()
                if ($spdxId -ne 'MPL-2.0') {
                    Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                        -WhatFailed "Original native file '$relNorm' declares SPDX '$spdxId' instead of 'MPL-2.0'." `
                        -HowToFix "Use MPL-2.0 SPDX header for original native C/C++ files under native/."
                }
            }
            if ($relNorm.StartsWith("native/proxies/")) {
                $nativeProxiesTotal++
                if ($headJoined -match 'SPDX-License-Identifier:\s*MPL-2\.0') { $nativeProxiesStamped++ }
            }
        }

        if ($relNorm.StartsWith("src/tools/kongor-cli/") -and $extLower -eq ".cs") {
            $kongorCliCsTotal++
            if ($headJoined -match 'SPDX-License-Identifier:\s*AGPL-3\.0-or-later') { $kongorCliCsStamped++ }
        }
    }

    if ($kongorCliCsTotal -ge 10 -and ($kongorCliCsStamped / [double]$kongorCliCsTotal) -lt 1.00) {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "SPDX header coverage in src/tools/kongor-cli/ fell below 100% ($kongorCliCsStamped/$kongorCliCsTotal)." `
            -HowToFix "Run .\scripts\release\Apply-CleanroomFileHeaders.ps1 -ScanNewFiles to stamp missing SPDX headers."
    }
    if ($nativeProxiesTotal -ge 5 -and ($nativeProxiesStamped / [double]$nativeProxiesTotal) -lt 0.80) {
        Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
            -WhatFailed "MPL-2.0 SPDX header coverage in native/proxies/ fell below 80% ($nativeProxiesStamped/$nativeProxiesTotal)." `
            -HowToFix "Run .\scripts\release\Apply-CleanroomFileHeaders.ps1 to stamp missing MPL-2.0 headers."
    }

    $underpromiseCheckedFiles = @(
        "README.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "INTEROPERABILITY_AND_DMCA.md",
        "CITATION.cff",
        "llms.txt",
        "NOTICE",
        "NAMING_POLICY.md"
    )
    foreach ($upRel in $underpromiseCheckedFiles) {
        $upFull = Join-Path $stagingDir ($upRel -replace '/', '\')
        if (Test-Path -LiteralPath $upFull -PathType Leaf) {
            $upRaw = [System.IO.File]::ReadAllText($upFull)
            if ($upRaw -match '(?i)\b72\s+hours\b|\b101[- ]tools?\b|cdn\.kongor\.net|\bAzure\s+DevOps\b') {
                Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                    -WhatFailed "Public governance/root doc '$upRel' contains disallowed SLA/tool-count/upstream-CDN/private-remote token ('$($Matches[0])')." `
                    -HowToFix "Use one-week acknowledgment + qualitative tool descriptions + neutral community/S3 CDN wording in '$upRel'."
            }
        }
    }

    $stagedSrcDir = Join-Path $stagingDir "src"
    if (Test-Path -LiteralPath $stagedSrcDir -PathType Container) {
        foreach ($csprojFile in @(Get-ChildItem -LiteralPath $stagedSrcDir -Recurse -Filter "*.csproj" -File -ErrorAction SilentlyContinue)) {
            $csprojRaw = [System.IO.File]::ReadAllText($csprojFile.FullName)
            if ($csprojRaw -match '<PackageLicenseExpression>\s*MIT\s*</PackageLicenseExpression>') {
                $relCsproj = $csprojFile.FullName.Substring($stagingDir.Length).TrimStart('\', '/') -replace '\\', '/'
                Stop-ExportWithActionableError -ExitCode 15 -GateName "GATE-5-GOVERNANCE" `
                    -WhatFailed "Staged C# project '$relCsproj' declares MIT instead of AGPL-3.0-or-later." `
                    -HowToFix "Change <PackageLicenseExpression> in '$relCsproj' to AGPL-3.0-or-later."
            }
        }
    }

    $g5Sw.Stop()
    $gateTimingsMs["gate5_governance_ms"] = $g5Sw.ElapsedMilliseconds
    Write-ExportLog "Gate 5 PASSED (0 LFS rules, $($allowedSubPaths.Count) approved MIT submodules, all $($manifest.requiredGovernanceFiles.Count) multi-license & governance docs verified) in $($g5Sw.ElapsedMilliseconds)ms." -Level "PASS"

    # -------------------------------------------------------------------------
    # Gate 7 (Part A): Author Cleanroom Git Snapshot in $stagingDir & Audit Identity
    # (Executed before Gate 6 so Gate 6 clones the exact committed Git tree!)
    # -------------------------------------------------------------------------
    $g7Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 7: Authoring Scoped Cleanroom Git Commit & Auditing Identity..." -Level "GATE"

    & git init -q -b $Branch $stagingDir
    if ($LASTEXITCODE -ne 0) {
        Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
            -WhatFailed "git init failed in '$stagingDir'." `
            -HowToFix "Verify git CLI is installed and $env:TEMP is writable."
    }
    & git -C $stagingDir config core.autocrlf false
    & git -C $stagingDir config commit.gpgsign false

    # If a remote branch already exists at GithubRepoUrl, audit its commit emails before parenting onto it
    $remoteParentSha = ""
    if (-not [string]::IsNullOrWhiteSpace($GithubRepoUrl)) {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $lsRemoteLines = @(& git ls-remote --heads $GithubRepoUrl "refs/heads/$Branch" 2>$null)
        $lsExit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap

        if ($lsExit -eq 0 -and $lsRemoteLines.Count -gt 0 -and $lsRemoteLines[0] -match '^([0-9a-fA-F]{40})\s+') {
            Write-ExportLog "Existing remote branch '$Branch' detected; fetching history to verify zero private email leakage..."
            $prevFetchEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & git -C $stagingDir fetch -q --depth=50 $GithubRepoUrl "refs/heads/${Branch}:refs/remotes/public_target/$Branch" 2>$null
                $fetchExit = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $prevFetchEap
            }
            if ($fetchExit -ne 0) {
                Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                    -WhatFailed "Failed to fetch existing '$Branch' from '$GithubRepoUrl'." `
                    -HowToFix "Verify GitHub credentials/network access to '$GithubRepoUrl'."
            }
            $remoteEmails = @(& git -C $stagingDir log "refs/remotes/public_target/$Branch" --format="%ae|%ce" | Sort-Object -Unique)
            foreach ($pair in $remoteEmails) {
                foreach ($em in ($pair -split '\|')) {
                    if (-not [string]::IsNullOrWhiteSpace($em) -and $em.Trim() -ne $exportUserEmail) {
                        Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                            -WhatFailed "Target remote '$GithubRepoUrl' branch '$Branch' contains non-noreply commit email '$em'." `
                            -HowToFix "Do not append to dirty remote history; recreate or force-replace the cleanroom branch only after explicit review."
                    }
                }
            }
            $remoteParentSha = (& git -C $stagingDir rev-parse "refs/remotes/public_target/$Branch").Trim()
            & git -C $stagingDir reset --soft $remoteParentSha
        }
    }

    & git -C $stagingDir add -A
    if ($LASTEXITCODE -ne 0) {
        Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
            -WhatFailed "git add -A failed in '$stagingDir'." `
            -HowToFix "Inspect '$stagingDir' permissions or path lengths."
    }

    # Register approved 160000 submodule gitlinks so actions/checkout@v4 (submodules: recursive) works out of the box
    foreach ($sub in @($manifest.allowedSubmodules)) {
        $sPath = ([string]$sub.path).Trim()
        $sCommit = ([string]$sub.commit).Trim()
        if ($sCommit -match '^[0-9a-fA-F]{40}$') {
            & git -C $stagingDir update-index --add --cacheinfo "160000,$sCommit,$sPath"
            if ($LASTEXITCODE -ne 0) {
                Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                    -WhatFailed "Failed to register 160000 gitlink for submodule '$sPath' at '$sCommit'." `
                    -HowToFix "Verify submodule path and 40-char SHA in config/public_export_manifest.json."
            }
        }
    }

    $prevAuthorName = $env:GIT_AUTHOR_NAME
    $prevAuthorEmail = $env:GIT_AUTHOR_EMAIL
    $prevCommitterName = $env:GIT_COMMITTER_NAME
    $prevCommitterEmail = $env:GIT_COMMITTER_EMAIL
    try {
        $env:GIT_AUTHOR_NAME = $exportUserName
        $env:GIT_AUTHOR_EMAIL = $exportUserEmail
        $env:GIT_COMMITTER_NAME = $exportUserName
        $env:GIT_COMMITTER_EMAIL = $exportUserEmail

        $stagedDiffCached = @(& git -C $stagingDir diff --cached --name-only)
        if ($stagedDiffCached.Count -gt 0 -or [string]::IsNullOrWhiteSpace($remoteParentSha)) {
            $commitMsg = "chore(release): Project KONGOR V cleanroom public source snapshot"
            & git -C $stagingDir -c "user.name=$exportUserName" -c "user.email=$exportUserEmail" commit -q -m $commitMsg
            if ($LASTEXITCODE -ne 0) {
                Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                    -WhatFailed "git commit failed in '$stagingDir'." `
                    -HowToFix "Check git status inside staging directory."
            }
        } else {
            Write-ExportLog "Cleanroom tree matches existing remote tip ($remoteParentSha) byte-for-byte; reusing verified commit."
        }
    } finally {
        $env:GIT_AUTHOR_NAME = $prevAuthorName
        $env:GIT_AUTHOR_EMAIL = $prevAuthorEmail
        $env:GIT_COMMITTER_NAME = $prevCommitterName
        $env:GIT_COMMITTER_EMAIL = $prevCommitterEmail
    }

    $commitHash = (& git -C $stagingDir rev-parse HEAD).Trim()
    $commitCountRaw = (& git -C $stagingDir rev-list --count HEAD).Trim()
    $commitCount = [int]$commitCountRaw
    if ([string]::IsNullOrWhiteSpace($remoteParentSha) -and $commitCount -ne 1) {
        Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
            -WhatFailed "Initial orphan cleanroom export produced $commitCount commits instead of 1." `
            -HowToFix "Ensure staging git init creates a fresh orphan repository with zero inherited history."
    }
    if (Test-Path -LiteralPath (Join-Path $scriptRepoRoot ".git")) {
        $localHeadSha = (& git -C $scriptRepoRoot rev-parse HEAD 2>$null)
        if (-not [string]::IsNullOrWhiteSpace($localHeadSha) -and $commitHash -eq $localHeadSha.Trim()) {
            Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                -WhatFailed "Cleanroom export commit SHA ($commitHash) matches local workspace HEAD!" `
                -HowToFix "Never export local Azure DevOps git objects directly; stage cleanroom files into an isolated temp directory."
        }
    }
    $identities = @(& git -C $stagingDir log --format="%an <%ae>|%cn <%ce>" | Sort-Object -Unique)
    $expectedIdentity = "$exportUserName <$exportUserEmail>|$exportUserName <$exportUserEmail>"
    foreach ($idLine in $identities) {
        if ($idLine.Trim() -ne $expectedIdentity) {
            Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                -WhatFailed "Identity audit failed! Found commit identity '$idLine' != '$expectedIdentity'." `
                -HowToFix "Check GIT_AUTHOR_EMAIL / GIT_COMMITTER_EMAIL environment overrides."
        }
    }

    $committedFiles = @(& git -C $stagingDir ls-files -s)
    foreach ($cfLine in $committedFiles) {
        if ($cfLine -match '^(\d+)\s+[0-9a-fA-F]+\s+\d+\t(.+)$') {
            $cfMode = $Matches[1]
            $cfRel = $Matches[2].Trim()
            if ($cfMode -ne "160000") {
                $cfExt = [System.IO.Path]::GetExtension($cfRel)
                if (-not [string]::IsNullOrEmpty($cfExt) -and $forbiddenExtSet.Contains($cfExt)) {
                    Stop-ExportWithActionableError -ExitCode 17 -GateName "GATE-7-GIT-IDENTITY" `
                        -WhatFailed "Committed git index contains forbidden extension '$cfExt': $cfRel" `
                        -HowToFix "Add '$cfRel' to excludeGlobs in config/public_export_manifest.json."
                }
            }
        }
    }

    $g7Sw.Stop()
    $gateTimingsMs["gate7_git_identity_ms"] = $g7Sw.ElapsedMilliseconds
    Write-ExportLog "Gate 7 PASSED (Commit=$commitHash, Author='$exportUserName <$exportUserEmail>', 0 private commits) in $($g7Sw.ElapsedMilliseconds)ms." -Level "PASS"

    # -------------------------------------------------------------------------
    # Gate 6: Isolated Cleanroom Clone, Quality Gate, CMake & .NET Build/Tests
    # -------------------------------------------------------------------------
    $g6Sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($SkipBuildGate) {
        Write-ExportLog "Gate 6 SKIPPED (Hermetic Canary Self-Test Mode: KONGOR_EXPORT_CANARY_TEST=1)." -Level "WARN"
        $gateTimingsMs["gate6_cleanroom_build_ms"] = 0
    } else {
        Write-ExportLog "Running Gate 6: Isolated Cleanroom Clone (core.autocrlf=true), Gate Check, CMake Native Build, Release .NET Build & 12/12 Unit Tests..." -Level "GATE"

        # Clone committed staging repo with Windows core.autocrlf=true to prove .gitattributes LF integrity pins work on fresh clones!
        & git clone -q -c core.autocrlf=true $stagingDir $buildVerifyDir
        if ($LASTEXITCODE -ne 0) {
            Stop-ExportWithActionableError -ExitCode 16 -GateName "GATE-6-CLEANROOM-BUILD" `
                -WhatFailed "Failed to git clone '$stagingDir' into '$buildVerifyDir'." `
                -HowToFix "Check disk space and git installation."
        }

        # Hydrate the 2 approved MIT submodules (rmlui & libdeflate) from local checkout so Gate 6 builds hermetically offline
        foreach ($sub in @($manifest.allowedSubmodules)) {
            $sRel = ([string]$sub.path).Trim() -replace '/', '\'
            $localSubDir = Join-Path $scriptRepoRoot $sRel
            $targetSubDir = Join-Path $buildVerifyDir $sRel
            if (Test-Path -LiteralPath (Join-Path $localSubDir ".git")) {
                $subFiles = @(& git -C $localSubDir ls-files)
                foreach ($sfRel in $subFiles) {
                    $srcSubFile = Join-Path $localSubDir ($sfRel -replace '/', '\')
                    if (Test-Path -LiteralPath $srcSubFile -PathType Leaf) {
                        $dstSubFile = Join-Path $targetSubDir ($sfRel -replace '/', '\')
                        $dstSubParent = [System.IO.Path]::GetDirectoryName($dstSubFile)
                        if (-not (Test-Path -LiteralPath $dstSubParent -PathType Container)) {
                            [void][System.IO.Directory]::CreateDirectory($dstSubParent)
                        }
                        [System.IO.File]::Copy($srcSubFile, $dstSubFile, $true)
                    }
                }
                Write-ExportLog "Hydrated cleanroom submodule '$sRel' ($($subFiles.Count) tracked files)."
            }
        }

        # Verify games/ does NOT exist inside $buildVerifyDir
        if (Test-Path -LiteralPath (Join-Path $buildVerifyDir "games")) {
            Stop-ExportWithActionableError -ExitCode 16 -GateName "GATE-6-CLEANROOM-BUILD" `
                -WhatFailed "Cleanroom verification clone unexpectedly contains a 'games/' directory!" `
                -HowToFix "Ensure 'games/**' remains in excludeGlobs in config/public_export_manifest.json."
        }

        Push-Location $buildVerifyDir
        try {
            # 6.1: 6-Tier Quality Gate (auto-detects cleanroom without games/)
            Write-ExportLog "[Gate 6.1] Running scripts/gate_check.ps1 inside cleanroom clone..."
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $buildVerifyDir "scripts\gate_check.ps1")
            if ($LASTEXITCODE -ne 0) {
                Stop-ExportWithActionableError -ExitCode 16 -GateName "GATE-6-CLEANROOM-BUILD" `
                    -WhatFailed "scripts/gate_check.ps1 failed inside cleanroom clone (exit=$LASTEXITCODE)." `
                    -HowToFix "Run ./scripts/gate_check.ps1 -Cleanroom locally and fix any broken doc links or Tier 1/6 findings."
            }

            # 6.2: Reconstruction Evidence Obligations + MCP Issue Index
            Write-ExportLog "[Gate 6.2] Running verify_evidence_obligations.ps1 and kongor_mcp_issues.ps1..."
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $buildVerifyDir "scripts\verify_evidence_obligations.ps1") -All
            if ($LASTEXITCODE -ne 0) {
                Stop-ExportWithActionableError -ExitCode 16 -GateName "GATE-6-CLEANROOM-BUILD" `
                    -WhatFailed "scripts/verify_evidence_obligations.ps1 -All failed inside cleanroom clone (exit=$LASTEXITCODE)." `
                    -HowToFix "Ensure all source/test files referenced by artifacts/evidence/**/*.json are included in config/public_export_manifest.json."
            }

            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $buildVerifyDir "scripts\kongor_mcp_issues.ps1") -Action check
            if ($LASTEXITCODE -ne 0) {
                Stop-ExportWithActionableError -ExitCode 16 -GateName "GATE-6-CLEANROOM-BUILD" `
                    -WhatFailed "scripts/kongor_mcp_issues.ps1 -Action check failed inside cleanroom clone (exit=$LASTEXITCODE)." `
                    -HowToFix "Run ./scripts/kongor_mcp_issues.ps1 -Action rebuild-index locally."
            }

            # Ensure no background game client or server holds IPC pipes during hermetic offline test runs
            Get-Process hon_x64, hon_server -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

            # 6.3: Unified Native CMake Configure + Build + .NET Release Build + 31/31 Gate Mutants + 12/12 Unit Tests (under Singleton Test-Runner Lock)
            $lockScript = Join-Path $buildVerifyDir "scripts\Invoke-KongorTestLock.ps1"
            $nativeSrc = Join-Path $buildVerifyDir "native"
            $nativeBuild = Join-Path $buildVerifyDir "native\build"
            $slnxPath = Join-Path $buildVerifyDir "ProjectKongor.slnx"
            $unitSlnfPath = Join-Path $buildVerifyDir "sln-filters\Kongor.UnitTests.slnf"
            $mutantsScript = Join-Path $buildVerifyDir "scripts\test_gate_mutants.ps1"

            Write-ExportLog "[Gate 6.3] Acquiring test-runner lock for Cleanroom Native CMake + Release .NET Build + 31/31 Gate Mutants + 12/12 Unit Tests..."
            & powershell -NoProfile -ExecutionPolicy Bypass -File $lockScript -Owner "public-export-gate6" -LeaseTtlSeconds 900 -- powershell -NoProfile -Command "
                `$ErrorActionPreference = 'Stop'
                Set-Location '$buildVerifyDir'
                Write-Host '[Gate 6.3a] Configuring CMake in cleanroom...'
                cmake -B '$nativeBuild' -S '$nativeSrc'
                if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }

                Write-Host '[Gate 6.3b] Building Native C++ targets (Release)...'
                cmake --build '$nativeBuild' --config Release --parallel
                if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }

                Write-Host '[Gate 6.3c] Building ProjectKongor.slnx (Release)...'
                dotnet build '$slnxPath' -c Release -v minimal -maxcpucount:4 -nodereuse:false
                if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }

                Write-Host '[Gate 6.3d] Running Hermetic Gate Mutants (test_gate_mutants.ps1)...'
                powershell -NoProfile -ExecutionPolicy Bypass -File '$mutantsScript'
                if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }

                Write-Host '[Gate 6.3e] Running Unit Test Suites (Kongor.UnitTests.slnf, Category!=Manual, maxcpucount:4)...'
                `$env:DOTNET_gcServer = '0'
                dotnet test '$unitSlnfPath' -c Release --no-build --nologo -maxcpucount:4 -nodereuse:false --filter 'Category!=Manual' --blame-hang-timeout 60s --blame-hang-dump-type none -v minimal
                if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }
            "
            if ($LASTEXITCODE -ne 0) {
                Stop-ExportWithActionableError -ExitCode 16 -GateName "GATE-6-CLEANROOM-BUILD" `
                    -WhatFailed "Cleanroom Native CMake / .NET Release build / Unit Test suite failed (exit=$LASTEXITCODE)." `
                    -HowToFix "Inspect the compiler or test failure above; verify no cleanroom project depends on excluded paths or untracked local binaries."
            }
        } finally {
            Pop-Location
        }

        $g6Sw.Stop()
        $gateTimingsMs["gate6_cleanroom_build_ms"] = $g6Sw.ElapsedMilliseconds
        Write-ExportLog "Gate 6 PASSED (Cleanroom Gate Check + Obligations + 31/31 Gate Mutants + Native CMake + Release .NET + 12/12 Unit Test Suites) in $([Math]::Round($g6Sw.Elapsed.TotalSeconds, 1))s." -Level "PASS"
    }

    # -------------------------------------------------------------------------
    # Gate 8: Deterministic Receipt Emission & Manual-Only Direct Git Push
    # -------------------------------------------------------------------------
    $g8Sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-ExportLog "Running Gate 8: Writing Certification Receipt & Evaluating Push Mode..." -Level "GATE"

    $treeManifestBuilder = New-Object System.Text.StringBuilder
    foreach ($kv in $stagedHashMap.GetEnumerator()) {
        [void]$treeManifestBuilder.Append("$($kv.Key):$($kv.Value)`n")
    }
    $treeBytes = [System.Text.Encoding]::UTF8.GetBytes($treeManifestBuilder.ToString())
    $shaTree = [System.Security.Cryptography.SHA256]::Create()
    try {
        $treeSha256 = [System.BitConverter]::ToString($shaTree.ComputeHash($treeBytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $shaTree.Dispose()
    }

    $pushedToRemote = $false
    if ($Push) {
        Write-ExportLog "Executing explicit manual push to '$GithubRepoUrl' (refs/heads/$Branch)..." -Level "GATE"
        $prevPushEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & git -C $stagingDir push $GithubRepoUrl "HEAD:refs/heads/$Branch"
            $pushExit = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $prevPushEap
        }
        if ($pushExit -ne 0) {
            Stop-ExportWithActionableError -ExitCode 18 -GateName "GATE-8-PUSH" `
                -WhatFailed "git push to '$GithubRepoUrl' (branch '$Branch') failed (exit=$pushExit)." `
                -HowToFix "Verify your GitHub authentication ('gh auth status' or Git Credential Manager) and repository push permissions."
        }
        $pushedToRemote = $true
        Write-ExportLog "Successfully pushed cleanroom commit $commitHash to '$GithubRepoUrl' ($Branch)!" -Level "PASS"
    }

    $overallSw.Stop()
    $g8Sw.Stop()
    $gateTimingsMs["gate8_receipt_and_push_ms"] = $g8Sw.ElapsedMilliseconds

    $receiptDir = [System.IO.Path]::GetDirectoryName($ReceiptPath)
    if (-not [string]::IsNullOrWhiteSpace($receiptDir) -and (-not (Test-Path -LiteralPath $receiptDir -PathType Container))) {
        [void][System.IO.Directory]::CreateDirectory($receiptDir)
    }

    $receiptObj = [ordered]@{
        schemaVersion        = "1.0.0"
        certifiedAtUtc       = (Get-Date).ToUniversalTime().ToString("o")
        mode                 = if ($Push) { "manual_push" } else { "dry_run" }
        pushed               = $pushedToRemote
        branch               = $Branch
        targetRemote         = if ([string]::IsNullOrWhiteSpace($GithubRepoUrl)) { "(dry-run-local-only)" } else { $GithubRepoUrl }
        exportIdentity       = "$exportUserName <$exportUserEmail>"
        cleanroomCommitSha   = $commitHash
        commitCount          = $commitCount
        treeSha256           = $treeSha256
        stagedFileCount      = $allStagedFiles.Count
        totalBytes           = $totalBytes
        totalMegabytes       = [Math]::Round($totalBytes / 1MB, 2)
        retailHashesChecked  = $retailBlocklistHashes.Count
        skipBuildGate        = [bool]$SkipBuildGate
        totalElapsedMs       = $overallSw.ElapsedMilliseconds
        gateTimingsMs        = $gateTimingsMs
        gatesPassed          = @(
            "Gate1_DefaultDenyAllowlistAndExtensions",
            "Gate2_RetailAssetSha256Blocklist",
            "Gate3_LocalPiiAndWorkstationPathGuard",
            "Gate4_SecretScrubAndGitleaksAudit",
            "Gate5_ZeroLfsSubmodulesAndGovernance",
            $(if ($SkipBuildGate) { "Gate6_SkippedInCanaryMode" } else { "Gate6_IsolatedCleanroomBuildAndUnitTests" }),
            "Gate7_ScopedGitIdentityVerification",
            "Gate8_ReceiptEmission"
        )
    }

    $receiptJson = $receiptObj | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($ReceiptPath, $receiptJson, [System.Text.UTF8Encoding]::new($false))
    Write-ExportLog "Wrote certification receipt to: $ReceiptPath" -Level "PASS"

    if (-not $Push) {
        Write-Host ""
        Write-Host "================================================================================" -ForegroundColor Green
        Write-Host " [DRY-RUN CERTIFIED] Project KONGOR V Cleanroom Export Passed All 8 Gates!" -ForegroundColor Green
        Write-Host "   Files Staged   : $($allStagedFiles.Count) ($([Math]::Round($totalBytes / 1MB, 2)) MiB, 0 Git LFS)" -ForegroundColor Cyan
        Write-Host "   Tree SHA-256   : $treeSha256" -ForegroundColor Cyan
        Write-Host "   Commit Author  : $exportUserName <$exportUserEmail>" -ForegroundColor Cyan
        Write-Host "   Receipt Path   : $ReceiptPath" -ForegroundColor Cyan
        Write-Host "   Manual Push Cmd: .\scripts\Invoke-PublicGithubExport.ps1 -GithubRepoUrl 'https://github.com/guccid0nut/PKV.git' -Branch $Branch -Push" -ForegroundColor Yellow
        Write-Host "================================================================================" -ForegroundColor Green
    }
    exit 0
} finally {
    if (Test-Path -LiteralPath $buildVerifyDir) {
        Remove-Item -LiteralPath $buildVerifyDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
