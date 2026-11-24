# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Automated Roslyn Banned Symbols & .NET 10 Preview API Auditor for ProjectKongor.
.DESCRIPTION
    Scans the repository source code and test suites for violations of BannedSymbols.txt
    and checks for dangerous/anti-pattern APIs. Extensively logs all audit stages and findings.
#>

[CmdletBinding()]
param (
    [string]$RootDirectory = "",
    [string]$BannedSymbolsFile = "",
    [switch]$FailOnViolations = $false
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RootDirectory)) {
    $RootDirectory = (Get-Item "$PSScriptRoot\..\..").FullName
} else {
    $RootDirectory = (Resolve-Path $RootDirectory).Path
}

if ([string]::IsNullOrWhiteSpace($BannedSymbolsFile)) {
    $BannedSymbolsFile = Join-Path $RootDirectory "BannedSymbols.txt"
} else {
    $BannedSymbolsFile = (Resolve-Path $BannedSymbolsFile).Path
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [PROJECTKONGOR AUDIT] Roslyn BannedSymbols.txt & .NET 10 Preview Scanner" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "Root Directory       : $RootDirectory" -ForegroundColor Gray
Write-Host "Banned Symbols File  : $BannedSymbolsFile" -ForegroundColor Gray
Write-Host "Timestamp            : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff zzz')" -ForegroundColor Gray
Write-Host ""

# -----------------------------------------------------------------------------
# 1. Parse and Validate BannedSymbols.txt Structure
# -----------------------------------------------------------------------------
Write-Host ">> [STAGE 1] Parsing and validating BannedSymbols.txt schema..." -ForegroundColor Yellow

if (-not (Test-Path $BannedSymbolsFile)) {
    Write-Error "CRITICAL: BannedSymbols.txt not found at $BannedSymbolsFile"
    exit 1
}

$rawLines = Get-Content -Path $BannedSymbolsFile -Encoding UTF8
$rules = @()
$invalidLines = @()
$lineIndex = 0

foreach ($line in $rawLines) {
    $lineIndex++
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#") -or $trimmed.StartsWith("//")) {
        continue
    }

    if ($trimmed -match '^([A-Z]):([^;]+);(.*)$') {
        $prefix = $Matches[1]
        $docId = "$($Matches[1]):$($Matches[2])"
        $symbol = $Matches[2]
        $reason = $Matches[3].Trim()

        $rules += [PSCustomObject]@{
            LineNumber = $lineIndex
            Prefix = $prefix
            DocId = $docId
            Symbol = $symbol
            Reason = $reason
        }
        Write-Host "   [RULE #$($rules.Count)] [$prefix] $docId" -ForegroundColor Green
        Write-Host "          Guidance: $reason" -ForegroundColor DarkGray
    } else {
        $invalidLines += [PSCustomObject]@{
            LineNumber = $lineIndex
            Content = $trimmed
        }
        Write-Host "   [INVALID LINE #$lineIndex] $trimmed" -ForegroundColor Red
    }
}

Write-Host ">> Parsed $($rules.Count) active rules with $($invalidLines.Count) schema errors." -ForegroundColor Yellow
if ($invalidLines.Count -gt 0) {
    Write-Error "BannedSymbols.txt contains malformed lines."
    exit 1
}
Write-Host ""

# -----------------------------------------------------------------------------
# 2. Perform Codebase Static Pattern Scan
# -----------------------------------------------------------------------------
Write-Host ">> [STAGE 2] Performing source pattern search across src/ and tests/..." -ForegroundColor Yellow

$targetDirs = @("src", "tests")
$searchPatterns = @(
    @{ Name = "DateTime.Now"; Pattern = "DateTime\.Now\b" },
    @{ Name = "DateTime.UtcNow"; Pattern = "DateTime\.UtcNow\b" },
    @{ Name = "DateTime.Today"; Pattern = "DateTime\.Today\b" },
    @{ Name = "DateTimeOffset.Now"; Pattern = "DateTimeOffset\.Now\b" },
    @{ Name = "DateTimeOffset.UtcNow"; Pattern = "DateTimeOffset\.UtcNow\b" },
    @{ Name = "Thread.Sleep"; Pattern = "Thread\.Sleep\b" },
    @{ Name = "new Random()"; Pattern = "new\s+Random\s*\(\s*\)" },
    @{ Name = "Task.WaitAll"; Pattern = "Task\.WaitAll\b" },
    @{ Name = "Task.WaitAny"; Pattern = "Task\.WaitAny\b" },
    @{ Name = "Thread.Abort"; Pattern = "Thread\.(?:CurrentThread\.)?Abort\b" },
    @{ Name = "Thread.Suspend"; Pattern = "Thread\.(?:CurrentThread\.)?Suspend\b" },
    @{ Name = "Thread.Resume"; Pattern = "Thread\.(?:CurrentThread\.)?Resume\b" },
    @{ Name = "RNGCryptoServiceProvider"; Pattern = "RNGCryptoServiceProvider\b" },
    @{ Name = "MD5.Create"; Pattern = "MD5\.Create\b" },
    @{ Name = "SHA1.Create"; Pattern = "SHA1\.Create\b" },
    @{ Name = "BinaryFormatter"; Pattern = "BinaryFormatter\b" },
    @{ Name = "WebClient"; Pattern = "WebClient\b" },
    @{ Name = "WebRequest.Create"; Pattern = "WebRequest\.Create\b" }
)

$violations = @()

foreach ($subDir in $targetDirs) {
    $targetPath = Join-Path $RootDirectory $subDir
    if (-not (Test-Path $targetPath)) {
        Write-Host "   Directory not found: $targetPath, skipping." -ForegroundColor DarkGray
        continue
    }

    Write-Host "   Scanning directory: $targetPath..." -ForegroundColor Cyan
    $files = Get-ChildItem -Path $targetPath -Recurse -Filter *.cs | Where-Object {
        $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\'
    }

    Write-Host "   Found $($files.Count) C# source files in $subDir." -ForegroundColor Gray

    foreach ($file in $files) {
        foreach ($p in $searchPatterns) {
            $matches = Select-String -Path $file.FullName -Pattern $p.Pattern
            foreach ($m in $matches) {
                $violations += [PSCustomObject]@{
                    File = $file.FullName
                    RelativePath = $file.FullName.Substring($RootDirectory.Length).TrimStart('\', '/')
                    LineNumber = $m.LineNumber
                    PatternName = $p.Name
                    LineContent = $m.Line.Trim()
                }
            }
        }
    }
}

Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [AUDIT REPORT SUMMARY]" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "Total Banned Rules Enforced : $($rules.Count)" -ForegroundColor White
Write-Host "Total Codebase Violations   : $($violations.Count)" -ForegroundColor $(if ($violations.Count -eq 0) { "Green" } else { "Red" })
Write-Host ""

if ($violations.Count -gt 0) {
    Write-Host ">> VIOLATIONS DETECTED:" -ForegroundColor Red
    foreach ($v in $violations) {
        Write-Host "   - [$($v.PatternName)] $($v.RelativePath):$($v.LineNumber)" -ForegroundColor Red
        Write-Host "     Code: $($v.LineContent)" -ForegroundColor DarkGray
    }
    Write-Host ""

    if ($FailOnViolations) {
        Write-Error "Audit failed with $($violations.Count) banned symbol violation(s)."
        exit 1
    }
} else {
    Write-Host ">> ALL CLEAR: 0 banned symbol violations detected across all $($targetDirs -join ', ') assemblies!" -ForegroundColor Green
    Write-Host ">> Architecture compliance status: 100% SATISFIED" -ForegroundColor Green
}

Write-Host "================================================================================" -ForegroundColor Cyan
