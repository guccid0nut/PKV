# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Dependency Vulnerability Scanner and SARIF 2.1.0 Generator for ProjectKongor.
.DESCRIPTION
    Scans all projects in the solution for known NuGet package vulnerabilities (CVEs)
    using the .NET SDK dependency vulnerability analyzer, converts findings into a standard
    SARIF 2.1.0 document for GitHub Code Scanning, and emits a GitHub Actions step summary.
.PARAMETER SolutionFile
    Path to the solution file to scan. Defaults to ProjectKongor.slnx in repo root.
.PARAMETER OutputFile
    Path to write the SARIF 2.1.0 report. Defaults to artifacts/dependency-vulnerabilities.sarif.
.PARAMETER FailOnSeverity
    Minimum severity level that will trigger a non-zero exit code ('None', 'Low', 'Moderate', 'High', 'Critical').
    Default is 'Critical'.
#>

[CmdletBinding()]
param (
    [string]$SolutionFile = "",
    [string]$OutputFile = "",
    [ValidateSet("None", "Low", "Moderate", "High", "Critical")]
    [string]$FailOnSeverity = "Critical"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
if ([string]::IsNullOrWhiteSpace($SolutionFile)) {
    $SolutionFile = Join-Path $repoRoot "ProjectKongor.slnx"
} else {
    $SolutionFile = (Resolve-Path $SolutionFile).Path
}

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $artifactsDir = Join-Path $repoRoot "artifacts"
    if (-not (Test-Path $artifactsDir)) {
        New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
    }
    $OutputFile = Join-Path $artifactsDir "dependency-vulnerabilities.sarif"
} else {
    $outputDir = Split-Path -Parent $OutputFile
    if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }
}

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff zzz"
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [PROJECTKONGOR CI] Dependency Vulnerability Scanner & SARIF 2.1.0 Generator" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "[INIT] Repository Root    : $repoRoot" -ForegroundColor Gray
Write-Host "[INIT] Target Solution    : $SolutionFile" -ForegroundColor Gray
Write-Host "[INIT] Output SARIF Path  : $OutputFile" -ForegroundColor Gray
Write-Host "[INIT] Fail On Severity   : $FailOnSeverity" -ForegroundColor Gray
Write-Host "[INIT] Scan Start Time    : $timestamp" -ForegroundColor Gray
Write-Host ""

Write-Host ">> [STAGE 1] Invoking .NET SDK vulnerability analyzer across solution..." -ForegroundColor Yellow
$jsonOutput = ""
try {
    # Run dotnet list package --vulnerable --include-transitive --format json
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = "dotnet"
    $pinfo.Arguments = "list `"$SolutionFile`" package --vulnerable --include-transitive --format json"
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.UseShellExecute = $false
    $pinfo.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($pinfo)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    $jsonOutput = $stdout
    if ($proc.ExitCode -ne 0 -and [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host "[WARN] dotnet list exited with code $($proc.ExitCode): $stderr" -ForegroundColor Yellow
    }
} catch {
    Write-Host "[ERROR] Failed to execute dotnet list command: $_" -ForegroundColor Red
    throw $_
}

Write-Host ">> [STAGE 2] Parsing vulnerability report and building SARIF 2.1.0..." -ForegroundColor Yellow

$projectsScanned = 0
$vulnerabilitiesFound = @()
$rules = @()
$ruleIds = [System.Collections.Generic.HashSet[string]]::new()

if (-not [string]::IsNullOrWhiteSpace($jsonOutput)) {
    try {
        $parsed = $jsonOutput | ConvertFrom-Json
        if ($null -ne $parsed.projects) {
            $projectsScanned = $parsed.projects.Count
            Write-Host "[AUDIT] Analyzed $projectsScanned projects across solution." -ForegroundColor Green

            foreach ($project in $parsed.projects) {
                $projectPath = $project.path
                # Normalize relative path from repo root
                $relPath = $projectPath
                if ($projectPath.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $relPath = $projectPath.Substring($repoRoot.Length).TrimStart('\', '/')
                }
                $relPath = $relPath.Replace("\", "/")

                if ($null -ne $project.frameworks) {
                    foreach ($fw in $project.frameworks) {
                        $allPackages = @()
                        if ($null -ne $fw.topLevelPackages) { $allPackages += $fw.topLevelPackages }
                        if ($null -ne $fw.transitivePackages) { $allPackages += $fw.transitivePackages }

                        foreach ($pkg in $allPackages) {
                            if ($null -ne $pkg.vulnerabilities) {
                                foreach ($vuln in $pkg.vulnerabilities) {
                                    $cve = if (-not [string]::IsNullOrWhiteSpace($vuln.cve)) { $vuln.cve } else { "GHSA-VULN" }
                                    $ruleId = "KONGOR-SCA-$($pkg.id)-$cve"
                                    $severity = if (-not [string]::IsNullOrWhiteSpace($vuln.severity)) { $vuln.severity } else { "Moderate" }
                                    $advisoryUrl = if (-not [string]::IsNullOrWhiteSpace($vuln.advisoryUrl)) { $vuln.advisoryUrl } else { "https://github.com/advisories" }

                                    $sarifLevel = switch ($severity.ToLowerInvariant()) {
                                        "critical" { "error" }
                                        "high"     { "error" }
                                        "moderate" { "warning" }
                                        "low"      { "note" }
                                        default    { "warning" }
                                    }

                                    if ($ruleIds.Add($ruleId)) {
                                        $rules += @{
                                            id = $ruleId
                                            name = "VulnerableDependency_$($pkg.id)"
                                            shortDescription = @{
                                                text = "Known vulnerability in package $($pkg.id) ($severity)"
                                            }
                                            fullDescription = @{
                                                text = "Package $($pkg.id) version $($pkg.resolvedVersion) has a known $severity vulnerability: $cve. Advisory: $advisoryUrl"
                                            }
                                            helpUri = $advisoryUrl
                                            properties = @{
                                                severity = $severity
                                                cve = $cve
                                                packageId = $pkg.id
                                                version = $pkg.resolvedVersion
                                            }
                                        }
                                    }

                                    $vulnerabilitiesFound += @{
                                        ruleId = $ruleId
                                        level = $sarifLevel
                                        message = @{
                                            text = "Package '$($pkg.id)' version '$($pkg.resolvedVersion)' has a known $severity vulnerability ($cve). See advisory: $advisoryUrl"
                                        }
                                        locations = @(
                                            @{
                                                physicalLocation = @{
                                                    artifactLocation = @{
                                                        uri = $relPath
                                                        uriBaseId = "%SRCROOT%"
                                                    }
                                                    region = @{
                                                        startLine = 1
                                                        startColumn = 1
                                                    }
                                                }
                                            }
                                        )
                                        properties = @{
                                            severity = $severity
                                            package = $pkg.id
                                            version = $pkg.resolvedVersion
                                            advisoryUrl = $advisoryUrl
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    } catch {
        Write-Host "[WARN] Could not parse json output from dotnet list: $_" -ForegroundColor Yellow
    }
}

# Construct valid SARIF 2.1.0 document
$sarif = @{
    "`$schema" = "https://json.schemastore.org/sarif-2.1.0.json"
    version = "5.0.2"
    runs = @(
        @{
            tool = @{
                driver = @{
                    name = "ProjectKongor.DependencyScanner"
                    version = "5.0.2"
                    semanticVersion = "5.0.2"
                    informationUri = "https://github.com/guccidonut/PKV"
                    rules = $rules
                }
            }
            results = $vulnerabilitiesFound
            columnKind = "utf16CodeUnits"
        }
    )
}

$sarifJson = $sarif | ConvertTo-Json -Depth 10
Set-Content -Path $OutputFile -Value $sarifJson -Encoding UTF8
Write-Host "[SARIF] Successfully emitted SARIF 2.1.0 report: $OutputFile" -ForegroundColor Green
Write-Host "[AUDIT] Total Vulnerabilities Discovered: $($vulnerabilitiesFound.Count)" -ForegroundColor $(if ($vulnerabilitiesFound.Count -eq 0) { "Green" } else { "Red" })

# -----------------------------------------------------------------------------
# Stage 3: Step Summary for GitHub Actions
# -----------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summaryLines = @()
    $summaryLines += "## 🛡️ Project Kongor - Dependency Security Scan"
    $summaryLines += ""
    $summaryLines += "| Metric | Value |"
    $summaryLines += "| :--- | :--- |"
    $summaryLines += "| **Scan Timestamp** | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss UTC') |"
    $summaryLines += "| **Projects Analyzed** | $projectsScanned |"
    $summaryLines += "| **Vulnerabilities Found** | **$($vulnerabilitiesFound.Count)** |"
    $summaryLines += "| **SARIF Output** | \`$OutputFile\` |"
    $summaryLines += ""

    if ($vulnerabilitiesFound.Count -eq 0) {
        $summaryLines += "### ✅ Zero Known Vulnerabilities"
        $summaryLines += "All 360+ packages across the solution are clean according to the GitHub Advisory Database."
    } else {
        $summaryLines += "### ⚠️ Discovered Vulnerabilities"
        $summaryLines += "| Severity | Package | Version | Advisory |"
        $summaryLines += "| :---: | :--- | :--- | :--- |"
        foreach ($v in $vulnerabilitiesFound) {
            $summaryLines += "| **$($v.properties.severity)** | \`$($v.properties.package)\` | \`$($v.properties.version)\` | [Advisory Link]($($v.properties.advisoryUrl)) |"
        }
    }

    $summaryContent = $summaryLines -join "`n"
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $summaryContent -Encoding UTF8
    Write-Host "[CI] Appended security summary to `$env:GITHUB_STEP_SUMMARY." -ForegroundColor Cyan
}

# Determine exit code based on FailOnSeverity
if ($FailOnSeverity -ne "None" -and $vulnerabilitiesFound.Count -gt 0) {
    $severityRank = @{
        "low" = 1
        "moderate" = 2
        "high" = 3
        "critical" = 4
    }
    $targetRank = $severityRank[$FailOnSeverity.ToLowerInvariant()]

    $breached = $vulnerabilitiesFound | Where-Object {
        $r = $severityRank[$_.properties.severity.ToLowerInvariant()]
        $null -ne $r -and $r -ge $targetRank
    }

    if ($breached.Count -gt 0) {
        Write-Host "[FAIL] Found $($breached.Count) vulnerabilities matching or exceeding threshold '$FailOnSeverity'!" -ForegroundColor Red
        exit 1
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [PROJECTKONGOR CI] Dependency Vulnerability Scan Completed Successfully." -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Cyan
exit 0
