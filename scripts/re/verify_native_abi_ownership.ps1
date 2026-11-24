# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Static ABI Boundary Ownership Linter for Project Kongor Native C/C++ Code.
.DESCRIPTION
    Scans native code (native/proxies/, native/tools/) to prevent the ABI Boundary Ownership Trap:
    ensuring that no scratch-arena allocated pointer (e.g. from kongor_arena.h) escapes across
    the K2 engine boundary where K2's CRT would attempt to call 'delete' or 'K2_Free'.
.PARAMETER Path
    Root directory or specific file to audit. Defaults to native/.
.EXAMPLE
    .\scripts\re\verify_native_abi_ownership.ps1
    .\scripts\re\verify_native_abi_ownership.ps1 -Path "native/proxies/native_guard"
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]$Path = "native"
)

$ErrorActionPreference = "Stop"

function Write-LinterLog {
    param ([string]$Message, [string]$Level = "INFO")
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $color = switch ($Level) {
        "ERROR"   { "Red" }
        "WARN"    { "Yellow" }
        "SUCCESS" { "Green" }
        default   { "Cyan" }
    }
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $color
}

$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$targetDir = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repoRoot $Path }

if (-not (Test-Path $targetDir)) {
    Write-LinterLog "Target path does not exist: $targetDir" "ERROR"
    exit 1
}

Write-LinterLog "Starting ABI Boundary Ownership Audit across $targetDir..." "INFO"

$sourceExtensions = @(".c", ".cpp", ".cc", ".h", ".hpp")
$candidateFiles = if ((Get-Item $targetDir) -is [System.IO.FileInfo]) {
    @((Get-Item $targetDir))
} else {
    Get-ChildItem -Path $targetDir -Recurse -File | Where-Object {
        $ext = $_.Extension.ToLower()
        $ext -in $sourceExtensions -and $_.FullName -notmatch "[\\/]build[\\/]" -and $_.FullName -notmatch "[\\/]deps[\\/]"
    }
}

$violationCount = 0
$checkedFiles = 0

foreach ($file in $candidateFiles) {
    $checkedFiles++
    $relPath = $file.FullName.Replace($repoRoot, "").TrimStart('\', '/')
    $lines = Get-Content -Path $file.FullName

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $lineNum = $i + 1
        $line = $lines[$i]

        # Skip comments
        if ($line.Trim().StartsWith("//") -or $line.Trim().StartsWith("/*") -or $line.Trim().StartsWith("*")) {
            continue
        }

        # Rule 1: Never call free() or delete on an arena-allocated pointer
        if ($line -match '\b(free|delete|delete\[\])\s*\(\s*.*(arena|scratch).*(\)|;)' -or
            $line -match '\b(free|delete|delete\[\])\s+.*(arena|scratch)') {
            # Check for exemption comment
            if ($line -notmatch 'exemption:\s*ownership') {
                Write-LinterLog "[$($relPath):$lineNum] Potential illegal free/delete on arena pointer: '$($line.Trim())'" "ERROR"
                Write-Host "  Action: Arena memory is managed via kongor_arena_temp_end() or kongor_arena_clear(). Never call free/delete on arena allocations." -ForegroundColor Yellow
                $violationCount++
            }
        }

        # Rule 2: Flag returning scratch arena pointers in exported hook functions (extern "C" __declspec(dllexport))
        if ($line -match '__declspec\s*\(\s*dllexport\s*\).*(\*|\&)\s*[a-zA-Z0-9_]+\s*\(') {
            # Look ahead for return of scratch arena within the function body (simple heuristic)
            $lookaheadEnd = [Math]::Min($lines.Count - 1, $i + 40)
            for ($j = $i + 1; $j -le $lookaheadEnd; $j++) {
                if ($lines[$j] -match '\breturn\s+.*(scratch|kongor_get_scratch_arena).*;' -and $lines[$j] -notmatch 'exemption:\s*internal_proxy') {
                    Write-LinterLog "[$($relPath):$($j + 1)] Unsafe escape: exported function returns scratch arena pointer to caller!" "ERROR"
                    Write-Host "  Action: Never transfer ownership of scratch arena memory across the ABI boundary. Use K2_Alloc() or caller-allocated buffers." -ForegroundColor Yellow
                    $violationCount++
                    break
                }
                if ($lines[$j].Trim().StartsWith("}")) {
                    break
                }
            }
        }
    }
}

Write-Host ""
Write-Host "==================== ABI OWNERSHIP AUDIT SUMMARY ====================" -ForegroundColor Cyan
Write-Host "Files Checked:       $checkedFiles"
Write-Host "Violations Detected: $violationCount" -ForegroundColor $(if ($violationCount -eq 0) { "Green" } else { "Red" })
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host ""

if ($violationCount -gt 0) {
    Write-LinterLog "ABI Boundary Ownership Audit FAILED with $violationCount violation(s)." "ERROR"
    exit 1
}

Write-LinterLog "All native source files passed ABI Boundary Ownership Audit cleanly." "SUCCESS"
exit 0
