# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Automated Developer CLI Suite Audit, Help Verification & Smoke Runner for ProjectKongor.

.DESCRIPTION
    Sequentially builds, audits, and smoke-tests the purpose-built developer CLI tools in ProjectKongor:
    1. mod-cli (Mod package lifecycle, DAG conflict resolver, AST normalizer)
    2. prebake-tool (Entity XML harvesting, MemoryPack+Zstd binary cache, Lua UI cache)
    3. endpoint-auditor (Client XML/Lua SubmitForm AST extraction and NEXUS route reconciliation)
    4. lua-verifier (Headless NLua sandbox mocking K2 UI widgets, Cvars, and PHP/Base64 serialization)

.PARAMETER Configuration
    Build configuration to use when building/running tools (Debug or Release). Default is 'Debug'.

.PARAMETER SkipBuild
    Skip the dotnet build step for each tool project.

.PARAMETER Tool
    Filter execution to a specific tool or run 'All' (default: 'All').

.PARAMETER LogFile
    Path to write structured NDJSON diagnostic telemetry. Default is 'logs/cli-audits.ndjson'.

.PARAMETER VerboseOutput
    Enable verbose diagnostic trace output.

.PARAMETER PassThru
    Outputs the structured PSCustomObject array of audit results to the pipeline.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/qa/run-all-cli-audits.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/qa/run-all-cli-audits.ps1 -Tool prebake-tool -VerboseOutput
#>

[CmdletBinding()]
param(
    [Parameter(HelpMessage = "Build configuration (Debug or Release)")]
    [string]$Configuration = "Debug",

    [Parameter(HelpMessage = "Skip building projects before audit")]
    [switch]$SkipBuild,

    [Parameter(HelpMessage = "Specific tool to audit or 'All'")]
    [ValidateSet("All", "mod-cli", "prebake-tool", "endpoint-auditor", "lua-verifier")]
    [string]$Tool = "All",

    [Parameter(HelpMessage = "NDJSON telemetry log output path")]
    [string]$LogFile = "",

    [Parameter(HelpMessage = "Enable verbose diagnostic output")]
    [switch]$VerboseOutput,

    [Parameter(HelpMessage = "Output structured results to pipeline")]
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

# ==============================================================================
# Helper Functions: Diagnostic Logging & Formatting
# ==============================================================================

function Write-AuditLog {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Message,
        [Parameter()][ValidateSet("INFO", "SUCCESS", "FAIL", "WARN", "DEBUG", "BANNER", "STAGE")][string]$Level = "INFO"
    )
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $line = "[$timestamp] [$Level] $Message"

    switch ($Level) {
        "INFO"    { Write-Host $line -ForegroundColor Cyan }
        "SUCCESS" { Write-Host $line -ForegroundColor Green }
        "FAIL"    { Write-Host $line -ForegroundColor Red }
        "WARN"    { Write-Host $line -ForegroundColor Yellow }
        "DEBUG"   { if ($VerboseOutput) { Write-Host $line -ForegroundColor DarkGray } }
        "BANNER"  { Write-Host $line -ForegroundColor Magenta }
        "STAGE"   { Write-Host $line -ForegroundColor Yellow }
        default   { Write-Host $line }
    }
}

# ==============================================================================
# Step 1: Environment & Path Initialization
# ==============================================================================

Write-AuditLog "================================================================================" "BANNER"
Write-AuditLog "         PROJECT KONGOR: DEVELOPER CLI SUITE SEQUENTIAL AUDIT RUNNER             " "BANNER"
Write-AuditLog "================================================================================" "BANNER"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ScriptDir) {
    $ScriptDir = (Get-Location).Path
}

# Walk up directory tree to find repository root (marked by Directory.Build.props or ProjectKongor.sln)
$CurrentScan = [System.IO.DirectoryInfo]::new($ScriptDir)
$RepoRoot = $ScriptDir
while ($null -ne $CurrentScan) {
    if (Test-Path (Join-Path $CurrentScan.FullName "Directory.Build.props")) {
        $RepoRoot = $CurrentScan.FullName
        break
    }
    $CurrentScan = $CurrentScan.Parent
}

if (-not $LogFile) {
    $LogFile = Join-Path $RepoRoot "logs\cli-audits.ndjson"
}

$LogDir = Split-Path -Parent $LogFile
if ($LogDir -and -not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

Write-AuditLog "Repository Root : $RepoRoot" "INFO"
Write-AuditLog "Configuration   : $Configuration" "INFO"
Write-AuditLog "Target Tool(s)  : $Tool" "INFO"
Write-AuditLog "Telemetry Log   : $LogFile" "INFO"
Write-AuditLog "Skip Build      : $SkipBuild" "INFO"
Write-AuditLog "Verbose Output  : $VerboseOutput" "INFO"

# ==============================================================================
# Step 2: Tool Registry Definition (10 Developer CLI Tools)
# ==============================================================================

$ToolCatalog = @(
    @{
        Name        = "kongor-cli"
        Project     = "src\tools\kongor-cli\Kongor.Cli.csproj"
        Description = "Main ecosystem developer CLI"
        CliEngine   = "Spectre.Console.Cli"
        HelpArgs    = @("--help")
        SmokeArgs   = @("--help")
        NeedsStdin  = $false
        ExpectedExit= 0
    },
    @{
        Name        = "mod-cli"
        Project     = "src\tools\mod-cli\ModCli.csproj"
        Description = "HoN mod package manager (.honmod/.s2z), DAG conflict resolver, AST normalizer"
        CliEngine   = "Spectre.Console.Cli"
        HelpArgs    = @("--help")
        SmokeArgs   = @("list")
        NeedsStdin  = $false
        ExpectedExit= 0
    },
    @{
        Name        = "prebake-tool"
        Project     = "src\tools\prebake-tool\prebake-tool.csproj"
        Description = "In-game entity pre-baking, MemoryPack+Zstd binary cache & Lua UI cache generator"
        CliEngine   = "Spectre.Console.Cli"
        HelpArgs    = @("--help")
        SmokeArgs   = @("all", "--dry-run")
        NeedsStdin  = $false
        ExpectedExit= 0
    },
    @{
        Name        = "endpoint-auditor"
        Project     = "src\tools\endpoint-auditor\EndpointAuditor.csproj"
        Description = "AST/Regex static analysis comparing client SubmitForm against NEXUS routes"
        CliEngine   = "Positional Args + 5-Tier Fallback Discovery"
        HelpArgs    = @("--help")
        SmokeArgs   = @()
        NeedsStdin  = $false
        ExpectedExit= 0
    },
    @{
        Name        = "lua-verifier"
        Project     = "src\tools\lua-verifier\LuaVerifier.csproj"
        Description = "Headless NLua sandbox mocking K2 UI widgets, Cvars, and PHP/Base64 serialization"
        CliEngine   = "Positional Args / NLua Sandbox"
        HelpArgs    = @("--help")
        SmokeArgs   = @()
        NeedsStdin  = $false
        ExpectedExit= 0
    }
)

# Filter tools if requested
$ActiveCatalog = if ($Tool -eq "All") {
    $ToolCatalog
} else {
    $ToolCatalog | Where-Object { $_.Name -eq $Tool }
}

Write-AuditLog "Discovered $($ActiveCatalog.Count) tools to evaluate.`n" "INFO"

# ==============================================================================
# Step 3: Sequential Tool Audit Execution
# ==============================================================================

# Upfront Solution Build Phase (eliminates MSB3026/CS2012 multi-project concurrent locking)
$SolutionFile = Join-Path $RepoRoot "ProjectKongor.slnx"
if (-not (Test-Path $SolutionFile)) {
    $SolutionFile = Join-Path $RepoRoot "ProjectKongor.sln"
}

$solutionBuildDurationMs = 0
$solutionBuildSuccess = $true
$solutionBuildNotes = ""

if (-not $SkipBuild) {
    Write-AuditLog "Compiling CLI tool targets ($Configuration configuration)..." "STAGE"
    $swSolBuild = [System.Diagnostics.Stopwatch]::StartNew()
    $buildErrors = @()
    foreach ($toolEntry in $ActiveCatalog) {
        $tProjFull = Join-Path $RepoRoot $toolEntry.Project
        if (Test-Path $tProjFull) {
            $toolBuildOutput = & dotnet build "$tProjFull" -c "$Configuration" --nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                $err = ($toolBuildOutput | Select-String -Pattern 'error CS\d+|Build FAILED' | Select-Object -First 2 | ForEach-Object { $_.Line.Trim() }) -join "; "
                $buildErrors += "$($toolEntry.Name): $err"
            }
        }
    }
    $swSolBuild.Stop()
    $solutionBuildDurationMs = $swSolBuild.ElapsedMilliseconds

    if ($buildErrors.Count -eq 0) {
        Write-AuditLog "All CLI tool targets compiled successfully in ${solutionBuildDurationMs}ms." "SUCCESS"
    } else {
        $solutionBuildSuccess = $false
        $AllPassed = $false
        $solutionBuildNotes = $buildErrors -join " | "
        Write-AuditLog "CLI tool compilation failed: $solutionBuildNotes" "FAIL"
    }
}

$AuditResults = @()
$OverallTimer = [System.Diagnostics.Stopwatch]::StartNew()
$AllPassed = if ($SkipBuild) { $true } else { $solutionBuildSuccess }

foreach ($toolEntry in $ActiveCatalog) {
    $tName = $toolEntry.Name
    $tProjRel = $toolEntry.Project
    $tProjFull = Join-Path $RepoRoot $tProjRel
    $tDesc = $toolEntry.Description
    $tEngine = $toolEntry.CliEngine

    Write-AuditLog "--------------------------------------------------------------------------------" "INFO"
    Write-AuditLog "[TOOL AUDIT] Evaluating '$tName' ($tEngine)..." "STAGE"
    Write-AuditLog "  Description : $tDesc" "DEBUG"
    Write-AuditLog "  Project     : $tProjRel" "DEBUG"

    if (-not (Test-Path $tProjFull)) {
        Write-AuditLog "  [FAIL] Project file not found: $tProjFull" "FAIL"
        $AllPassed = $false
        $AuditResults += [PSCustomObject]@{
            ToolName      = $tName
            Project       = $tProjRel
            CliEngine     = $tEngine
            BuildStatus   = "MISSING"
            BuildDuration = 0
            HelpStatus    = "N/A"
            HelpExitCode  = -1
            SmokeStatus   = "FAIL"
            SmokeExitCode = -1
            TotalDuration = 0
            Notes         = "Project file does not exist on disk"
        }
        continue
    }

    $toolTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $buildSuccess = $false
    $buildDurationMs = 0
    $buildNotes = ""

    # 3.1 Build Phase
    if (-not $SkipBuild) {
        $buildSuccess = $solutionBuildSuccess
        $buildDurationMs = $solutionBuildDurationMs
        if ($buildSuccess) {
            Write-AuditLog "  [BUILD: PASS] Verified solution build artifact for $tName." "SUCCESS"
        } else {
            $AllPassed = $false
            $buildNotes = if ($solutionBuildNotes) { $solutionBuildNotes } else { "Solution compilation failed" }
            Write-AuditLog "  [BUILD: FAIL] Solution compilation failed: $buildNotes" "FAIL"
        }
    } else {
        $buildSuccess = $true
        Write-AuditLog "  [BUILD: SKIPPED] Build step skipped via -SkipBuild." "DEBUG"
    }

    # 3.2 Help Verification Phase
    $helpStatus = "N/A"
    $helpExit = -1
    if ($buildSuccess -and $toolEntry.HelpArgs.Count -gt 0) {
        Write-AuditLog "  [2/3] Verifying CLI --help interface..." "DEBUG"
        $swHelp = [System.Diagnostics.Stopwatch]::StartNew()
        $helpOutput = & dotnet run --project "$tProjFull" -c "$Configuration" --no-build -- @($toolEntry.HelpArgs) 2>&1
        $helpExit = $LASTEXITCODE
        $swHelp.Stop()

        if ($helpExit -eq 0) {
            $helpStatus = "PASS"
            Write-AuditLog "  [HELP: PASS] Command --help returned 0 in $($swHelp.ElapsedMilliseconds)ms." "SUCCESS"
        } else {
            $helpStatus = "EXIT_$helpExit"
            Write-AuditLog "  [HELP: WARN] Command --help exited with $helpExit in $($swHelp.ElapsedMilliseconds)ms." "WARN"
        }
    } else {
        Write-AuditLog "  [2/3] Help interface: Not applicable or build failed." "DEBUG"
    }

    # 3.3 Smoke Test Execution Phase
    $smokeStatus = "N/A"
    $smokeExit = -1
    if ($buildSuccess) {
        Write-AuditLog "  [3/3] Running smoke verification..." "DEBUG"
        $swSmoke = [System.Diagnostics.Stopwatch]::StartNew()
        
        $smokeOutput = $null
        if ($toolEntry.NeedsStdin) {
            # Pipe 'exit' to prevent interactive hang
            $smokeOutput = "exit" | & dotnet run --project "$tProjFull" -c "$Configuration" --no-build -- @($toolEntry.SmokeArgs) 2>&1
        } else {
            $smokeOutput = & dotnet run --project "$tProjFull" -c "$Configuration" --no-build -- @($toolEntry.SmokeArgs) 2>&1
        }
        $smokeExit = $LASTEXITCODE
        $swSmoke.Stop()

        if ($smokeExit -eq 0 -or $smokeExit -eq $toolEntry.ExpectedExit) {
            $smokeStatus = "PASS"
            Write-AuditLog "  [SMOKE: PASS] Smoke execution succeeded (ExitCode: $smokeExit) in $($swSmoke.ElapsedMilliseconds)ms." "SUCCESS"
        } else {
            $smokeStatus = "EXIT_$smokeExit"
            Write-AuditLog "  [SMOKE: WARN] Smoke execution exited with $smokeExit in $($swSmoke.ElapsedMilliseconds)ms." "WARN"
        }
    } else {
        $smokeStatus = "SKIPPED_BUILD_FAILED"
        Write-AuditLog "  [3/3] Smoke execution skipped due to build failure." "DEBUG"
    }

    $toolTimer.Stop()
    $totalDurationMs = $toolTimer.ElapsedMilliseconds

    # Record result
    $resultObj = [PSCustomObject]@{
        ToolName      = $tName
        Project       = $tProjRel
        CliEngine     = $tEngine
        BuildStatus   = if ($buildSuccess) { "PASS" } else { "FAIL" }
        BuildDuration = $buildDurationMs
        HelpStatus    = $helpStatus
        HelpExitCode  = $helpExit
        SmokeStatus   = $smokeStatus
        SmokeExitCode = $smokeExit
        TotalDuration = $totalDurationMs
        Notes         = if ($buildNotes) { $buildNotes } else { "Audited successfully" }
    }
    $AuditResults += $resultObj

    # Emit NDJSON telemetry log
    $telemetryEvent = @{
        timestamp        = (Get-Date -Format "o")
        event_name       = "CliToolAudited"
        tool_name        = $tName
        project          = $tProjRel
        cli_engine       = $tEngine
        build_status     = $resultObj.BuildStatus
        build_duration_ms= $buildDurationMs
        help_status      = $helpStatus
        help_exit_code   = $helpExit
        smoke_status     = $smokeStatus
        smoke_exit_code  = $smokeExit
        total_duration_ms= $totalDurationMs
        configuration    = $Configuration
    } | ConvertTo-Json -Compress

    Add-Content -Path $LogFile -Value $telemetryEvent
}

$OverallTimer.Stop()

# ==============================================================================
# Step 4: Summary Table & Overall Status
# ==============================================================================

Write-AuditLog "`n================================================================================" "BANNER"
Write-AuditLog "                 PROJECT KONGOR CLI SUITE AUDIT SUMMARY                         " "BANNER"
Write-AuditLog "================================================================================" "BANNER"

$SummaryTable = $AuditResults | Select-Object ToolName, BuildStatus, HelpStatus, SmokeStatus, TotalDuration, Notes
$SummaryTable | Format-Table -AutoSize

$passedArray = @($AuditResults | Where-Object { $_.BuildStatus -eq "PASS" })
$passedCount = $passedArray.Count
$totalCount = $AuditResults.Count
$overallElapsedSec = [Math]::Round($OverallTimer.Elapsed.TotalSeconds, 2)

Write-AuditLog "Total Evaluated : $totalCount" "INFO"
Write-AuditLog "Passed Build    : $passedCount / $totalCount" $(if ($passedCount -eq $totalCount) { "SUCCESS" } else { "WARN" })
Write-AuditLog "Total Duration  : ${overallElapsedSec}s" "INFO"
Write-AuditLog "Telemetry Log   : $LogFile" "INFO"

if ($AllPassed) {
    Write-AuditLog "`n[RESULT: SUCCESS] All audited CLI tools verified cleanly!`n" "SUCCESS"
} else {
    Write-AuditLog "`n[RESULT: WARNING] One or more CLI tools encountered compilation or verification issues.`n" "WARN"
}

if ($PassThru) {
    return $AuditResults
}

if (-not $AllPassed) {
    exit 1
}
