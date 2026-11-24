# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Fast Sub-200ms Document Citation & Code Reference Verifier.

.DESCRIPTION
    Scans markdown documents in docs/audit/ and docs/reference/kb/ (or docs/reference/field-notes/),
    or custom paths passed via -Paths, validating that every relative markdown link or file:///
    code citation resolves to a real file on disk and that line ranges (#Lstart-Lend) do not
    exceed the target file's actual line count.

.EXAMPLE
    .\scripts\qa\verify_doc_citations.ps1
    .\scripts\qa\verify_doc_citations.ps1 -Paths @("docs/audits/master-audit-blueprint.md")
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string[]]$Paths = @(),

    [Parameter(Mandatory = $false)]
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

$targetFiles = New-Object System.Collections.Generic.List[string]

if ($Paths -and $Paths.Length -gt 0) {
    foreach ($p in $Paths) {
        $full = if ([System.IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $RepoRoot $p }
        if (Test-Path -LiteralPath $full) {
            if ((Get-Item -LiteralPath $full) -is [System.IO.DirectoryInfo]) {
                foreach ($f in (Get-ChildItem -LiteralPath $full -Filter "*.md" -Recurse -File)) {
                    $targetFiles.Add($f.FullName)
                }
            } else {
                $targetFiles.Add($full)
            }
        }
    }
} else {
    $searchDirs = @(
        (Join-Path $RepoRoot "docs\audits"),
        (Join-Path $RepoRoot "docs\reference\field-notes")
    )
    foreach ($sd in $searchDirs) {
        if (Test-Path -LiteralPath $sd) {
            foreach ($f in (Get-ChildItem -LiteralPath $sd -Filter "*.md" -Recurse -File)) {
                $targetFiles.Add($f.FullName)
            }
        }
    }
}

$errors = New-Object System.Collections.Generic.List[string]
$fileLineCountCache = New-Object 'System.Collections.Generic.Dictionary[string, int]' ([System.StringComparer]::OrdinalIgnoreCase)
$citationsChecked = 0

$linkRegex = [regex]::new('\[([^\]]*)\]\(([^)]+)\)', [System.Text.RegularExpressions.RegexOptions]::Compiled)
$lineRangeRegex = [regex]::new('#L(\d+)(?:-L(\d+))?', [System.Text.RegularExpressions.RegexOptions]::Compiled)

foreach ($docPath in $targetFiles) {
    if (-not (Test-Path -LiteralPath $docPath)) { continue }
    $docRel = $docPath.Replace($RepoRoot, "").TrimStart('\', '/').Replace('\', '/')
    $docDir = Split-Path -Parent $docPath
    $lines = [System.IO.File]::ReadAllLines($docPath)

    for ($lineIdx = 0; $lineIdx -lt $lines.Length; $lineIdx++) {
        $line = $lines[$lineIdx]
        if (-not $line.Contains("](")) { continue }

        $matches = $linkRegex.Matches($line)
        foreach ($m in $matches) {
            $rawTarget = $m.Groups[2].Value.Trim()

            # Skip web URLs, email, anchors, and custom protocols
            if ($rawTarget -match '^(https?://|mailto:|conversation://|sidecar://|#)') {
                continue
            }

            # Handle file:/// URLs
            $cleanTarget = $rawTarget
            if ($cleanTarget.StartsWith("file:///", [System.StringComparison]::OrdinalIgnoreCase)) {
                $cleanTarget = $cleanTarget.Substring(8).TrimStart('/')
            }

            # Separate target path and fragment (#Lstart-Lend)
            $fragIndex = $cleanTarget.IndexOf('#')
            $pathPart = if ($fragIndex -ge 0) { $cleanTarget.Substring(0, $fragIndex) } else { $cleanTarget }
            $fragPart = if ($fragIndex -ge 0) { $cleanTarget.Substring($fragIndex) } else { "" }

            if ([string]::IsNullOrWhiteSpace($pathPart)) {
                continue
            }

            # Resolve path relative to doc directory or repo root
            $resolvedPath = $null
            if ([System.IO.Path]::IsPathRooted($pathPart)) {
                $resolvedPath = $pathPart
            } else {
                $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $docDir $pathPart))
            }

            $citationsChecked++

            if (-not (Test-Path -LiteralPath $resolvedPath)) {
                # In cleanroom mode (when games/ directory is absent at repo root), citations pointing into games/ are expected
                $relFromRoot = $resolvedPath.Replace($RepoRoot, "").TrimStart('\', '/').Replace('\', '/')
                if ((-not (Test-Path -LiteralPath (Join-Path $RepoRoot "games"))) -and ($relFromRoot.StartsWith("games/", [System.StringComparison]::OrdinalIgnoreCase) -or $cleanTarget -match '(?i)(?:^|/)games/')) {
                    continue
                }
                $lineNum = $lineIdx + 1
                $errors.Add("[DOC-CITATION-FAIL] ${docRel}:${lineNum} -> Target '${rawTarget}' resolves to missing file '${resolvedPath}'. Fix: Update citation or run scripts/verify_doc_citations.ps1")
                continue
            }

            # If line range is specified, verify line bounds
            if (-not [string]::IsNullOrEmpty($fragPart)) {
                $rangeMatch = $lineRangeRegex.Match($fragPart)
                if ($rangeMatch.Success) {
                    $startLine = [int]$rangeMatch.Groups[1].Value
                    $endLine = if ($rangeMatch.Groups[2].Success) { [int]$rangeMatch.Groups[2].Value } else { $startLine }

                    $targetLineCount = 0
                    if (-not $fileLineCountCache.TryGetValue($resolvedPath, [ref]$targetLineCount)) {
                        $targetLineCount = [System.Linq.Enumerable]::Count([System.IO.File]::ReadLines($resolvedPath))
                        $fileLineCountCache[$resolvedPath] = $targetLineCount
                    }

                    $lineNum = $lineIdx + 1
                    if ($startLine -gt $targetLineCount -or $endLine -gt $targetLineCount) {
                        $errors.Add("[DOC-CITATION-FAIL] ${docRel}:${lineNum} -> Citation '${rawTarget}' line range #L${startLine}-L${endLine} exceeds file length (${targetLineCount} lines) in '${resolvedPath}'. Fix: Update citation or run scripts/verify_doc_citations.ps1")
                    }
                }
            }
        }
    }
}

$sw.Stop()
$elapsedMs = $sw.ElapsedMilliseconds

if ($errors.Count -gt 0) {
    if (-not $Quiet) {
        Write-Host "================================================================================" -ForegroundColor Red
        Write-Host " [DOC CITATION VERIFIER] FAILED: $($errors.Count) citation error(s) found in ${elapsedMs}ms" -ForegroundColor Red
        Write-Host "================================================================================" -ForegroundColor Red
        foreach ($err in $errors) {
            Write-Host " $err" -ForegroundColor Red
        }
    }
    exit 1
}

if (-not $Quiet) {
    Write-Host "[DOC CITATION VERIFIER] PASS: Verified $citationsChecked citation(s) across $($targetFiles.Count) document(s) in ${elapsedMs}ms." -ForegroundColor Green
}
exit 0
