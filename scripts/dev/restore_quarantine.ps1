# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Reversibly restores quarantined files or directories from scratch/_quarantine/.

.DESCRIPTION
    Reads an NDJSON quarantine manifest (default: scratch/_quarantine/manifest.ndjson),
    verifies SHA-256 checksums for all file entries before moving, and restores each
    matching item back to its original repository path with full -WhatIf support.

.PARAMETER ManifestPath
    Path to manifest.ndjson. Defaults to <RepoRoot>/scratch/_quarantine/manifest.ndjson.

.PARAMETER RepoRoot
    Optional repository root directory. Defaults to two levels above $PSScriptRoot.

.PARAMETER Target
    Optional filter (one or more original paths, quarantine paths, or prefixes/leaf names).

.PARAMETER Force
    Overwrite existing destination file/directory if already present. Never bypasses SHA-256 checks.

.PARAMETER PassThru
    Emit structured restoration result objects to the pipeline.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $false)]
    [Alias("Manifest")]
    [string]$ManifestPath = "",

    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = "",

    [Parameter(Mandatory = $false)]
    [string[]]$Target = @(),

    [Parameter(Mandatory = $false)]
    [switch]$Force = $false,

    [Parameter(Mandatory = $false)]
    [switch]$PassThru = $false
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
} else {
    $RepoRoot = (Resolve-Path $RepoRoot).Path
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $RepoRoot "scratch\_quarantine\manifest.ndjson"
} elseif (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {
    $ManifestPath = Join-Path $RepoRoot $ManifestPath
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [RESTORE QUARANTINE] Reversible Quarantine Restoration" -ForegroundColor Cyan
Write-Host " RepoRoot     : $RepoRoot" -ForegroundColor Gray
Write-Host " ManifestPath : $ManifestPath" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Quarantine manifest not found at '$ManifestPath'. Create or specify -ManifestPath explicitly."
}

function Get-Sha256Hex {
    param([string]$FilePath)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = $null
    try {
        $fs = [System.IO.File]::OpenRead($FilePath)
        $bytes = $sha.ComputeHash($fs)
        return ([System.BitConverter]::ToString($bytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        if ($null -ne $fs) { $fs.Dispose() }
        $sha.Dispose()
    }
}

function Resolve-RepoSafePath {
    param(
        [string]$BaseRoot,
        [string]$CandidatePath
    )
    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        throw "Manifest entry contains an empty path."
    }
    $resolved = if ([System.IO.Path]::IsPathRooted($CandidatePath)) {
        [System.IO.Path]::GetFullPath($CandidatePath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $BaseRoot ($CandidatePath -replace '/', '\')))
    }
    $normRoot = $BaseRoot.TrimEnd('\', '/') + "\"
    if (-not $resolved.StartsWith($normRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $resolved.Equals($BaseRoot.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside RepoRoot '$BaseRoot': '$CandidatePath' -> '$resolved'"
    }
    return $resolved
}

function Get-EntryProperty {
    param(
        [psobject]$Obj,
        [string[]]$Names
    )
    foreach ($n in $Names) {
        $prop = $Obj.PSObject.Properties[$n]
        if ($null -ne $prop -and $null -ne $prop.Value -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
            return $prop.Value
        }
    }
    return $null
}

$rawLines = Get-Content -LiteralPath $ManifestPath -Encoding UTF8
$entries = New-Object System.Collections.Generic.List[object]
$lineNum = 0
foreach ($line in $rawLines) {
    $lineNum++
    $trimmed = ([string]$line).Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
    try {
        $obj = $trimmed | ConvertFrom-Json
        $entries.Add([PSCustomObject]@{ Line = $lineNum; Data = $obj })
    } catch {
        throw "Invalid JSON on line $lineNum of '$ManifestPath': $($_.Exception.Message)"
    }
}

Write-Host "Loaded $($entries.Count) manifest entry/entries." -ForegroundColor Gray

$normalizedTargets = @($Target | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { ($_ -replace '\\', '/').Trim('/') })
$results = New-Object System.Collections.Generic.List[object]
$restoredCount = 0
$skippedCount = 0
$failedCount = 0

foreach ($item in $entries) {
    $e = $item.Data
    $origRel = [string](Get-EntryProperty -Obj $e -Names @("originalPath", "sourcePath", "original_path"))
    $quarRel = [string](Get-EntryProperty -Obj $e -Names @("quarantinePath", "destPath", "quarantine_path"))
    $expectedSha = [string](Get-EntryProperty -Obj $e -Names @("sha256", "sha256Hex"))
    $kind = [string](Get-EntryProperty -Obj $e -Names @("kind", "type"))

    if ([string]::IsNullOrWhiteSpace($origRel) -or [string]::IsNullOrWhiteSpace($quarRel)) {
        throw "Manifest line $($item.Line) missing required 'originalPath' or 'quarantinePath'."
    }

    $normOrig = ($origRel -replace '\\', '/').Trim('/')
    $normQuar = ($quarRel -replace '\\', '/').Trim('/')

    if ($normalizedTargets.Count -gt 0) {
        $matched = $false
        foreach ($t in $normalizedTargets) {
            if ($normOrig.Equals($t, [System.StringComparison]::OrdinalIgnoreCase) -or
                $normQuar.Equals($t, [System.StringComparison]::OrdinalIgnoreCase) -or
                $normOrig.StartsWith($t + "/", [System.StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.Path]::GetFileName($normOrig).Equals($t, [System.StringComparison]::OrdinalIgnoreCase)) {
                $matched = $true
                break
            }
        }
        if (-not $matched) {
            Write-Verbose "Skipping '$normOrig' (not matched by -Target filter)."
            continue
        }
    }

    $srcFull = Resolve-RepoSafePath -BaseRoot $RepoRoot -CandidatePath $quarRel
    $destFull = Resolve-RepoSafePath -BaseRoot $RepoRoot -CandidatePath $origRel

    if (-not (Test-Path -LiteralPath $srcFull)) {
        Write-Warning "Quarantined item missing on disk (skipping): '$quarRel' ($srcFull)"
        $skippedCount++
        $results.Add([PSCustomObject]@{
            originalPath = $normOrig
            quarantinePath = $normQuar
            kind = $kind
            sha256Verified = $false
            status = "missing_quarantine_source"
        })
        continue
    }

    $isDir = Test-Path -LiteralPath $srcFull -PathType Container
    if ([string]::IsNullOrWhiteSpace($kind)) {
        $kind = if ($isDir) { "directory" } else { "file" }
    }

    $shaVerified = $false
    if (-not $isDir) {
        if ([string]::IsNullOrWhiteSpace($expectedSha)) {
            throw "Manifest line $($item.Line) for file '$normOrig' is missing required 'sha256' hash."
        }
        $actualSha = Get-Sha256Hex -FilePath $srcFull
        if (-not $actualSha.Equals($expectedSha.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
            $failedCount++
            throw "SHA-256 mismatch for quarantined file '$quarRel': expected '$expectedSha', actual '$actualSha'. Refusing restoration."
        }
        $shaVerified = $true
        Write-Verbose "SHA-256 verified for '$quarRel': $actualSha"
    } else {
        # Verify optional per-file hashes inside directory entry if supplied
        $fileHashesProp = $e.PSObject.Properties["fileHashes"]
        if ($null -ne $fileHashesProp -and $null -ne $fileHashesProp.Value) {
            foreach ($fh in $fileHashesProp.Value.PSObject.Properties) {
                $subFile = Join-Path $srcFull ($fh.Name -replace '/', '\')
                if (-not (Test-Path -LiteralPath $subFile -PathType Leaf)) {
                    throw "Missing quarantined directory member '$($fh.Name)' under '$quarRel'."
                }
                $actualSubSha = Get-Sha256Hex -FilePath $subFile
                if (-not $actualSubSha.Equals(([string]$fh.Value).Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "SHA-256 mismatch for directory member '$($fh.Name)' under '$quarRel'."
                }
            }
            $shaVerified = $true
        }
    }

    if (Test-Path -LiteralPath $destFull) {
        if (-not $Force) {
            throw "Destination already exists at '$origRel' ($destFull). Re-run with -Force to overwrite."
        }
    }

    $actionDesc = "Restore quarantined $kind from '$normQuar' -> '$normOrig'"
    if ($PSCmdlet.ShouldProcess($destFull, $actionDesc)) {
        $destParent = Split-Path -Parent $destFull
        if (-not [string]::IsNullOrWhiteSpace($destParent) -and -not (Test-Path -LiteralPath $destParent)) {
            New-Item -ItemType Directory -Force -Path $destParent | Out-Null
        }

        if ((Test-Path -LiteralPath $destFull) -and $Force) {
            Write-Verbose "Removing existing destination (-Force): $destFull"
            Remove-Item -LiteralPath $destFull -Recurse -Force
        }

        Move-Item -LiteralPath $srcFull -Destination $destFull -Force

        if (-not $isDir -and $shaVerified) {
            $postSha = Get-Sha256Hex -FilePath $destFull
            if (-not $postSha.Equals($expectedSha.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Post-move SHA-256 verification failed for '$origRel': expected '$expectedSha', got '$postSha'."
            }
        }

        Write-Host "  [RESTORED] $normQuar -> $normOrig (sha256Verified=$shaVerified)" -ForegroundColor Green
        $restoredCount++
        $results.Add([PSCustomObject]@{
            originalPath = $normOrig
            quarantinePath = $normQuar
            kind = $kind
            sha256Verified = $shaVerified
            status = "restored"
        })
    } else {
        Write-Host "  [WHATIF] Would restore $normQuar -> $normOrig (sha256Verified=$shaVerified)" -ForegroundColor Yellow
        $results.Add([PSCustomObject]@{
            originalPath = $normOrig
            quarantinePath = $normQuar
            kind = $kind
            sha256Verified = $shaVerified
            status = "whatif"
        })
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " Restore summary: restored=$restoredCount skipped=$skippedCount failed=$failedCount" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

if ($PassThru) {
    return $results
}

exit 0
