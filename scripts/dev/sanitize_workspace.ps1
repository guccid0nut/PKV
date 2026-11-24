# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Workspace root sanitization and quarantine script for Project Kongor.

.DESCRIPTION
    Sweeps loose scratch scripts, captures, textures, test logs, parquet data,
    and leaked build outputs from the repository root into structured directories:
    - Loose python scratch scripts -> scratch/
    - Loose DDS textures -> artifacts/textures/
    - Loose PNG captures -> artifacts/captures/
    - Loose TXT / log files -> logs/runtime/
    - Leaked build binaries (.dll, .exe, .pdb, .deps.json) -> artifacts/build/
    - Parquet match files -> userdata/matches/
    - Scratch markdown / notes -> docs/reference/
#>

[CmdletBinding()]
param(
    [switch]$DryRun = $false
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR WORKSPACE SANITIZATION & QUARANTINE]" -ForegroundColor Cyan
Write-Host " Root: $repoRoot" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

# Ensure target directories exist
$targetDirs = @(
    "scratch",
    "artifacts/textures",
    "artifacts/captures",
    "artifacts/build",
    "logs/runtime",
    "userdata/matches",
    "docs/reference",
    "docs/roadmaps"
)

foreach ($dir in $targetDirs) {
    $fullDir = Join-Path $repoRoot $dir
    if (-not (Test-Path $fullDir)) {
        if (-not $DryRun) {
            New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
        }
        Write-Host "Created directory: $dir" -ForegroundColor Green
    }
}

$rules = @(
    # Scratch Python scripts
    @{ Filter = "*.py"; Dest = "scratch" },

    # Textures
    @{ Filter = "*.dds"; Dest = "artifacts/textures" },

    # Captures & Images
    @{ Filter = "*.png"; Dest = "artifacts/captures" },

    # Transient Logs & Output Text
    @{ Filter = "audit_*.txt"; Dest = "logs/runtime" },
    @{ Filter = "auth_search.txt"; Dest = "logs/runtime" },
    @{ Filter = "diff.txt"; Dest = "logs/runtime" },
    @{ Filter = "test_*.txt"; Dest = "logs/runtime" },
    @{ Filter = "sprint6_verification.txt"; Dest = "logs/runtime" },
    @{ Filter = "*.log"; Dest = "logs/runtime" },

    # Parquet Data
    @{ Filter = "match_*.parquet"; Dest = "userdata/matches" },

    # Leaked Build Output Binaries & Configs
    @{ Filter = "*.exe"; Dest = "artifacts/build" },
    @{ Filter = "*.dll"; Dest = "artifacts/build" },
    @{ Filter = "*.pdb"; Dest = "artifacts/build" },
    @{ Filter = "WILLOWMAKER.*"; Dest = "artifacts/build" },
    @{ Filter = "ASPIRE.Common.xml"; Dest = "artifacts/build" },
    @{ Filter = "NEXUS.*.xml"; Dest = "artifacts/build" },
    @{ Filter = "Kongor.Modding.xml"; Dest = "artifacts/build" },

    # Scratch markdown & Configs
    @{ Filter = "complex-engineering.md"; Dest = "docs/architecture" },
    @{ Filter = "in_game_client_organization_updates.md"; Dest = "docs/reference" },
    @{ Filter = "junies_recommendation_roadmap.md"; Dest = "docs/roadmaps" },
    @{ Filter = "launcher_settings.json"; Dest = "userdata" },
    @{ Filter = "worldconfig"; Dest = "userdata" },
    @{ Filter = "autoexec.cfg"; Dest = "games/client" }
)

$quarantinedCount = 0

foreach ($rule in $rules) {
    $matchedFiles = Get-ChildItem -Path $repoRoot -Filter $rule.Filter -File -ErrorAction SilentlyContinue
    foreach ($f in $matchedFiles) {
        $destDir = Join-Path $repoRoot $rule.Dest
        $destFile = Join-Path $destDir $f.Name
        if ($DryRun) {
            Write-Host " [DRYRUN] Would move $($f.Name) -> $($rule.Dest)/" -ForegroundColor DarkGray
        } else {
            Move-Item -Path $f.FullName -Destination $destFile -Force
            Write-Host " [MOVED] $($f.Name) -> $($rule.Dest)/" -ForegroundColor Green
        }
    }
}

# Hygiene Rule: Enforce games/shared evacuation (keep only MOVED_TO_VAULT_J.txt)
$sharedDir = Join-Path $repoRoot "games/shared"
if (Test-Path $sharedDir) {
    $sharedDirs = Get-ChildItem -Path $sharedDir -Directory -ErrorAction SilentlyContinue
    foreach ($d in $sharedDirs) {
        if (-not $DryRun) {
            Remove-Item -Path $d.FullName -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host " [EVICTED] Lingering directory from games/shared: $($d.Name)" -ForegroundColor Yellow
            $quarantinedCount++
        } else {
            Write-Host " [DRYRUN] Would evict directory from games/shared: $($d.Name)" -ForegroundColor DarkGray
        }
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [SANITIZATION COMPLETE] Total files relocated: $quarantinedCount" -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Cyan
