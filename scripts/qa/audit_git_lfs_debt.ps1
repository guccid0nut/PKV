# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Project Kongor: Git Repository Bloat & LFS Debt Auditor.

.DESCRIPTION
    Audits the workspace to ensure that large binary assets, raw bakes,
    CAS chunk stores (.cas/objects/), intermediate .bak rotations, and
    compiled binaries are quarantined outside Git tracking.
#>

[CmdletBinding()]
param(
    [int]$SizeThresholdMB = 50,
    [switch]$FailOnDebt = $false
)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

# shortcut: private .git pack retains 7.86 GiB historical blobs + ~19 GB local LFS cache (reclaimable locally via git lfs prune), public PKV export creates clean orphan commits with 0 LFS / <150 MB so history rewrite is intentionally deferred

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [PROJECT KONGOR] GIT REPOSITORY & LFS DEBT AUDIT" -ForegroundColor Cyan
Write-Host " Repo Root       : $repoRoot" -ForegroundColor Gray
Write-Host " Size Threshold  : $SizeThresholdMB MB" -ForegroundColor Gray
Write-Host " Fail on Debt    : $FailOnDebt" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

# 1. Verify .cas/ quarantine
$casDir = Join-Path $repoRoot ".cas"
$casIgnored = $false
try {
    $checkIgnore = git check-ignore ".cas" 2>$null
    if ($checkIgnore -match "\.cas") {
        $casIgnored = $true
    }
} catch { }

if ($casIgnored -or -not (Test-Path $casDir)) {
    Write-Host "[PASS] .cas/ store is properly quarantined outside Git." -ForegroundColor Green
} else {
    Write-Host "[FAIL] .cas/ store is NOT ignored in .gitignore!" -ForegroundColor Red
}

# 2. Check tracked files for large binaries
Write-Host "`n[*] Scanning Git-tracked files for oversized binary assets (> $SizeThresholdMB MB)..." -ForegroundColor Cyan
$trackedFiles = @()
try {
    $trackedFiles = git ls-files 2>$null
} catch {
    Write-Host "[!] Git command unavailable or not a git repository." -ForegroundColor Yellow
}

$oversizedTracked = @()
$bannedTrackedExts = @('.s2z', '.kpatch', '.dmp', '.pdb', '.lib', '.obj', '.exp')

if ($trackedFiles) {
    foreach ($rel in $trackedFiles) {
        $full = Join-Path $repoRoot $rel
        if (Test-Path $full -PathType Leaf) {
            $item = Get-Item $full
            $ext = [System.IO.Path]::GetExtension($rel).ToLower()
            $szMb = $item.Length / 1MB

            if ($szMb -gt $SizeThresholdMB -or $bannedTrackedExts -contains $ext) {
                # override/ is an allowed development exception unless > 50MB
                if ($rel -match "^games/client/override/" -and $szMb -le $SizeThresholdMB) {
                    continue
                }
                $oversizedTracked += [PSCustomObject]@{
                    Path = $rel
                    SizeMB = [math]::Round($szMb, 2)
                    Extension = $ext
                }
            }
        }
    }
}

# 3. Report Results
Write-Host "`n================================================================================" -ForegroundColor Cyan
Write-Host " [AUDIT RESULTS]" -ForegroundColor Cyan
Write-Host " Tracked Files Scanned : $($trackedFiles.Count)" -ForegroundColor Gray
Write-Host " Oversized Tracked Debt: $($oversizedTracked.Count)" -ForegroundColor $(if ($oversizedTracked.Count -eq 0) { "Green" } else { "Red" })

if ($oversizedTracked.Count -gt 0) {
    Write-Host "`n[!] Flagged Tracked Binary Assets:" -ForegroundColor Yellow
    foreach ($f in $oversizedTracked) {
        Write-Host "    - $($f.Path) ($($f.SizeMB) MB) [$($f.Extension)]" -ForegroundColor Yellow
    }
} else {
    Write-Host "`n[PASS] Zero binary repo bloat detected. All archives and CAS chunks are quarantined." -ForegroundColor Green
}

Write-Host "================================================================================" -ForegroundColor Cyan
$sw.Stop()
Write-Host " Audit completed in $([math]::Round($sw.Elapsed.TotalSeconds, 2))s." -ForegroundColor Gray

if ($FailOnDebt -and $oversizedTracked.Count -gt 0) {
    exit 1
}

exit 0
