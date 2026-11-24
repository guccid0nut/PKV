# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Installs and configures Together As One (TAO) MCP Server for Project Kongor
    across external AI services (Claude Desktop, GitHub Copilot, Cursor, Antigravity).

.DESCRIPTION
    Safely merges the 'kongor' MCP server configuration into target service config files
    without overwriting or clobbering existing configured MCP servers.

.PARAMETER Target
    Target service to configure: 'All', 'Claude', 'Copilot', 'Cursor', or 'Antigravity'.
    Default is 'All'.

.PARAMETER LaunchMode
    Execution mode: 'Python' (recommended, using src/tools/tao_mcp.py) or 'DotNet' (direct DLL).
    Default is 'Python'.

.PARAMETER DryRun
    Previews configuration changes without writing files to disk.

.PARAMETER SkipSmokeTest
    Skips the post-install pre-flight verification probe.

.EXAMPLE
    .\scripts\dev\install_tao_mcp.ps1 -Target All
    .\scripts\dev\install_tao_mcp.ps1 -Target Claude -LaunchMode Python
    .\scripts\dev\install_tao_mcp.ps1 -DryRun
#>

[CmdletBinding()]
param (
    [ValidateSet("All", "Claude", "Copilot", "Cursor", "Antigravity")]
    [string]$Target = "All",

    [ValidateSet("Python", "DotNet")]
    [string]$LaunchMode = "Python",

    [switch]$DryRun,

    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"

function Write-TaoLog {
    param (
        [string]$Message,
        [string]$Level = "INFO",
        [ConsoleColor]$Color = [ConsoleColor]::White
    )
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $prefix = "[$timestamp] [TAO-INSTALLER] [$Level]"
    Write-Host "$prefix $Message" -ForegroundColor $Color
}

Write-TaoLog "==================================================================" -Level "INIT" -Color Cyan
Write-TaoLog "Together As One (TAO) - KONGOR MCP Server Universal Installer" -Level "INIT" -Color Cyan
Write-TaoLog "Target Services: $Target | Mode: $LaunchMode | DryRun: $DryRun" -Level "INIT" -Color Cyan
Write-TaoLog "==================================================================" -Level "INIT" -Color Cyan

# 1. Resolve Repository Root & Launch Paths
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Get-Item $scriptDirectory).Parent.Parent.FullName
Write-TaoLog "Resolved ProjectKongor Repository Root: $repoRoot" -Color Gray

$pythonShimPath = Join-Path $repoRoot "src\tools\tao_mcp.py"
$dotnetDllPath = Join-Path $repoRoot "src\tools\kongor-cli\bin\Release\net11.0\kongor.dll"
$dotnetDebugDllPath = Join-Path $repoRoot "src\tools\kongor-cli\bin\Debug\net11.0\kongor.dll"

if (-not (Test-Path $pythonShimPath)) {
    Write-TaoLog "WARNING: Python shim not found at '$pythonShimPath'. Falling back to 'src\tools\MCP.py'..." -Level "WARN" -Color Yellow
    $pythonShimPath = Join-Path $repoRoot "src\tools\MCP.py"
}
Write-TaoLog "Canonical Python Launcher: $pythonShimPath" -Color Gray

# 2. Build Server Configuration Payload
$serverConfig = $null
if ($LaunchMode -eq "Python") {
    $serverConfig = @{
        command = "python"
        args = @($pythonShimPath)
        env = @{
            PYTHONUNBUFFERED = "1"
        }
    }
} else {
    $targetDll = if (Test-Path $dotnetDllPath) { $dotnetDllPath } else { $dotnetDebugDllPath }
    $serverConfig = @{
        command = "dotnet"
        args = @($targetDll, "mcp")
        env = @{
            KONGOR_ENABLE_SELF_HEALING = "true"
        }
    }
}

# Workspace-relative config for VS Code / Cursor
$workspaceRelativeConfig = @{
    command = "python"
    args = @('${workspaceFolder}/src/tools/tao_mcp.py')
    env = @{
        PYTHONUNBUFFERED = "1"
    }
}

function Merge-JsonMcpConfig {
    param (
        [string]$FilePath,
        [hashtable]$NewKongorConfig,
        [string]$Description
    )

    Write-TaoLog "Processing $Description at: $FilePath" -Color Yellow

    $parentDir = Split-Path -Parent $FilePath
    if (-not (Test-Path $parentDir)) {
        Write-TaoLog "Creating target directory: $parentDir" -Color Gray
        if (-not $DryRun) {
            New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
        }
    }

    $data = [ordered]@{}
    if (Test-Path $FilePath) {
        try {
            $rawContent = Get-Content -Path $FilePath -Raw -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace($rawContent)) {
                $data = $rawContent | ConvertFrom-Json -AsHashtable
                Write-TaoLog "Existing JSON configuration successfully loaded from: $FilePath" -Color Gray
            }
        } catch {
            Write-TaoLog "Warning: Existing file was empty or contained invalid JSON. Starting fresh." -Level "WARN" -Color Yellow
            $data = [ordered]@{}
        }
    } else {
        Write-TaoLog "No existing configuration file found. Initializing new configuration structure." -Color Gray
    }

    # Ensure mcpServers exists and is a hashtable/dictionary
    $hasMcpServers = if ($data -is [System.Collections.IDictionary]) { $data.Contains("mcpServers") } else { $false }
    if (-not $hasMcpServers -or $data["mcpServers"] -isnot [System.Collections.IDictionary]) {
        $data["mcpServers"] = [ordered]@{}
    }

    # Merge or update kongor server definition
    $data["mcpServers"]["kongor"] = $NewKongorConfig

    $jsonOutput = $data | ConvertTo-Json -Depth 10

    if ($DryRun) {
        Write-TaoLog "[DRY RUN] Would write following payload to '$FilePath':" -Color Magenta
        Write-Host $jsonOutput -ForegroundColor DarkGray
    } else {
        Set-Content -Path $FilePath -Value $jsonOutput -Encoding UTF8 -Force
        Write-TaoLog "Successfully updated '$FilePath' with 'kongor' MCP server." -Level "SUCCESS" -Color Green
    }
}

$configuredTargets = [System.Collections.Generic.List[string]]::new()

# 3. Configure Anthropic Claude Desktop
if ($Target -eq "All" -or $Target -eq "Claude") {
    $claudeConfigDir = Join-Path $env:APPDATA "Claude"
    $claudeConfigFile = Join-Path $claudeConfigDir "claude_desktop_config.json"
    Merge-JsonMcpConfig -FilePath $claudeConfigFile -NewKongorConfig $serverConfig -Description "Anthropic Claude Desktop"
    $configuredTargets.Add("Claude Desktop ($claudeConfigFile)")
}

# 4. Configure GitHub Copilot & VS Code Workspace
if ($Target -eq "All" -or $Target -eq "Copilot") {
    $vscodeConfigDir = Join-Path $repoRoot ".vscode"
    $vscodeConfigFile = Join-Path $vscodeConfigDir "mcp.json"
    
    Write-TaoLog "Processing VS Code / GitHub Copilot Workspace at: $vscodeConfigFile" -Color Yellow
    if (-not (Test-Path $vscodeConfigDir)) {
        if (-not $DryRun) { New-Item -ItemType Directory -Path $vscodeConfigDir -Force | Out-Null }
    }

    $vscodePayload = [ordered]@{
        "`$schema" = "http://json.schemastore.org/mcp"
        mcpServers = [ordered]@{
            kongor = $workspaceRelativeConfig
        }
        servers = [ordered]@{
            kongor = $workspaceRelativeConfig
        }
    }
    $vscodeJson = $vscodePayload | ConvertTo-Json -Depth 10

    if ($DryRun) {
        Write-TaoLog "[DRY RUN] Would write VS Code MCP payload to '$vscodeConfigFile'" -Color Magenta
    } else {
        Set-Content -Path $vscodeConfigFile -Value $vscodeJson -Encoding UTF8 -Force
        Write-TaoLog "Successfully updated '$vscodeConfigFile'." -Level "SUCCESS" -Color Green
    }
    $configuredTargets.Add("GitHub Copilot / VS Code ($vscodeConfigFile)")
}

# 5. Configure Cursor IDE (Workspace & Global)
if ($Target -eq "All" -or $Target -eq "Cursor") {
    # 5a. Workspace .cursor/mcp.json
    $cursorWorkspaceDir = Join-Path $repoRoot ".cursor"
    $cursorWorkspaceFile = Join-Path $cursorWorkspaceDir "mcp.json"
    Merge-JsonMcpConfig -FilePath $cursorWorkspaceFile -NewKongorConfig $workspaceRelativeConfig -Description "Cursor Workspace Config"
    $configuredTargets.Add("Cursor Workspace ($cursorWorkspaceFile)")

    # 5b. Global user ~/.cursor/mcp.json
    $cursorGlobalDir = Join-Path $env:USERPROFILE ".cursor"
    $cursorGlobalFile = Join-Path $cursorGlobalDir "mcp.json"
    Merge-JsonMcpConfig -FilePath $cursorGlobalFile -NewKongorConfig $serverConfig -Description "Cursor Global User Config"
    $configuredTargets.Add("Cursor Global User ($cursorGlobalFile)")
}

# 6. Configure Google Antigravity
if ($Target -eq "All" -or $Target -eq "Antigravity") {
    $antigravityMcpDir = Join-Path $env:USERPROFILE ".gemini\antigravity\mcp\kongor"
    Write-TaoLog "Processing Antigravity Registration at: $antigravityMcpDir" -Color Yellow
    
    if (-not $DryRun) {
        # Copy instructions.md and ensure directory exists
        if (-not (Test-Path $antigravityMcpDir)) {
            New-Item -ItemType Directory -Path $antigravityMcpDir -Force | Out-Null
        }
        $sourceInstructions = Join-Path $repoRoot "docs\tao-mcp\instructions.md"
        if (-not (Test-Path $sourceInstructions)) {
            $sourceInstructions = Join-Path $repoRoot "docs\instructions.md"
        }
        if (Test-Path $sourceInstructions) {
            Copy-Item -Path $sourceInstructions -Destination (Join-Path $antigravityMcpDir "instructions.md") -Force
            Write-TaoLog "Synced instructions from '$sourceInstructions' to '$antigravityMcpDir\instructions.md'." -Level "SUCCESS" -Color Green
        }
    }
    $configuredTargets.Add("Google Antigravity ($antigravityMcpDir)")
}

# 7. Pre-flight Smoke Test Verification
if (-not $SkipSmokeTest -and -not $DryRun) {
    Write-TaoLog "==================================================================" -Level "TEST" -Color Cyan
    Write-TaoLog "Running Pre-Flight Smoke Test via TAO MCP CLI runner..." -Level "TEST" -Color Cyan
    Write-TaoLog "Command: python `"$pythonShimPath`" --tool kongor_diagnostics" -Color Gray
    
    try {
        $smokeOutput = & python "$pythonShimPath" --tool kongor_diagnostics 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) {
            Write-TaoLog "Pre-flight smoke test PASSED! TAO MCP server is live and responsive." -Level "SUCCESS" -Color Green
            # Print brief snippet of probe duration
            $probeLines = $smokeOutput | Select-String -Pattern "totalProbeDurationMs|overallStatus"
            foreach ($line in $probeLines) {
                Write-TaoLog "  $($line.Line.Trim())" -Color Gray
            }
        } else {
            Write-TaoLog "Warning: Smoke test exited with code $exitCode." -Level "WARN" -Color Yellow
            Write-Host ($smokeOutput | Out-String) -ForegroundColor Yellow
        }
    } catch {
        Write-TaoLog "Warning during smoke test: $_" -Level "WARN" -Color Yellow
    }
}

Write-TaoLog "==================================================================" -Level "SUMMARY" -Color Cyan
Write-TaoLog "TAO MCP SERVER INSTALLATION COMPLETE" -Level "SUCCESS" -Color Green
Write-TaoLog "Configured Services:" -Color White
foreach ($serviceItem in $configuredTargets) {
    Write-TaoLog "  * $serviceItem" -Color Cyan
}
Write-TaoLog "Next Steps:" -Color Yellow
Write-TaoLog "  1. If running Claude Desktop or Cursor, restart the application to detect the new MCP server." -Color Gray
Write-TaoLog "  2. In GitHub Copilot Chat, query: 'Check server health using kongor'." -Color Gray
Write-TaoLog "  3. Refer to instructions.md for complete tool signatures and engine guardrails." -Color Gray
Write-TaoLog "==================================================================" -Level "DONE" -Color Cyan
