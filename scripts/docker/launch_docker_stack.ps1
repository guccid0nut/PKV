# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Turnkey Docker Compose Stack Launcher for ProjectKongor (Cluster + Game Servers + Queue Arcade + Optional Host Client).

.DESCRIPTION
    Launches the complete ProjectKongor backend inside Docker via a single CLI command:
      1. Infrastructure:     Garnet (Redis-compatible cache, :28298) + optional PostgreSQL 17 (-LocalPostgres, :25432)
      2. Backend Cluster:    KONGOR MasterServer (:55555), TRANSMUTANSTEIN ChatServer (:5554 HTTP / :11031-11033 TCP),
                             ZORGATH WebAPI (:5200), DAWNBRINGER WebUI (:5266)
      3. Dedicated Fleet:    COMPEL Match Server Fleet (Tier D, Control :11334, Game UDP 11335-11342)
      4. Queue Arcade Fleet: COMPEL "While You Wait" Mini-Game Fleet (Tier Q, Control :11484, Game UDP 11485-11492)
      5. Optional Client:    Native Windows Game Client (hon_x64.exe or WILLOWMAKER) on host (-WithClient)

    Enforces a strict fail-closed pre-flight gate: refuses to start if any required .NET User Secret,
    environment variable, or runtime binary is missing or still set to a development placeholder, and prints
    copy-pasteable remediation commands for every missing item.

.EXAMPLE
    .\scripts\docker\launch_docker_stack.ps1
    .\scripts\docker\launch_docker_stack.ps1 up -Performance High -WithClient
    .\scripts\docker\launch_docker_stack.ps1 check
    .\scripts\docker\launch_docker_stack.ps1 status
    .\scripts\docker\launch_docker_stack.ps1 logs -Service master-server
    .\scripts\docker\launch_docker_stack.ps1 down
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Action = "up",

    [Parameter()]
    [Alias("Perf", "PerformanceLevel")]
    [string]$Performance = "Balanced",

    [Parameter()]
    [Alias("Client", "LaunchClient")]
    [switch]$WithClient,

    [Parameter()]
    [Alias("Launcher", "Willowmaker")]
    [switch]$UseLauncher,

    [Parameter()]
    [switch]$LocalPostgres,

    [Parameter()]
    [Alias("Build")]
    [switch]$Rebuild,

    [Parameter()]
    [switch]$StopHostConflict,

    [Parameter()]
    [int]$MatchInstances = 0,

    [Parameter()]
    [int]$ArcadeInstances = 0,

    [Parameter()]
    [string]$Service = "",

    [Parameter()]
    [int]$Tail = 120,

    [Parameter()]
    [switch]$Follow,

    [Parameter()]
    [string]$SecretsFilePath = "",

    [Parameter()]
    [string]$DotEnvPath = "",

    [Parameter()]
    [string]$OutputEnvFilePath = "",

    [Parameter()]
    [switch]$IgnoreProcessEnv,

    [Parameter()]
    [switch]$SkipRuntimeChecks,

    [Parameter()]
    [string]$LogFilePath = "",

    [Parameter()]
    [Alias("TestDomain")]
    [string]$TestingDomain = "",

    [Parameter()]
    [Alias("Standalone")]
    [switch]$StandaloneAuth,

    [Parameter()]
    [Alias("NoBuild")]
    [switch]$SkipAutoRebuild,

    [Parameter()]
    [switch]$DryRun,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RepoRootSlash = $RepoRoot.Replace('\', '/')
$ComposeFile = Join-Path $RepoRoot "infra\docker\docker-compose.stack.yml"
$LogDir = Join-Path $RepoRoot "logs"
$LogFile = if (-not [string]::IsNullOrWhiteSpace($LogFilePath)) {
    $LogFilePath
} else {
    Join-Path $LogDir "docker_stack.log"
}
$targetLogDir = Split-Path -Parent $LogFile
if (-not [string]::IsNullOrWhiteSpace($targetLogDir) -and -not (Test-Path -LiteralPath $targetLogDir)) {
    New-Item -ItemType Directory -Path $targetLogDir -Force | Out-Null
}
$VaultId = "00005072-6f6a-6563-7420-4b4f4e474f52"
$DefaultSecretsJson = Join-Path $env:APPDATA "Microsoft\UserSecrets\$VaultId\secrets.json"
$DefaultDotEnv = Join-Path $RepoRoot ".env"
$RuntimeDockerDir = Join-Path $RepoRoot "artifacts\docker"
$DefaultRuntimeEnvFile = Join-Path $RuntimeDockerDir ".env.stack"
$LegacyRuntimeEnvFile = Join-Path $RepoRoot "userdata\docker\.env.stack"
$EmptySecretsStub = Join-Path $RuntimeDockerDir "empty_secrets.json"
$BuildStampFile = Join-Path $RepoRoot "artifacts\publish\.build-stamp.json"
$TestLockScript = Join-Path $RepoRoot "scripts\Invoke-KongorTestLock.ps1"
$WorkerLockScript = Join-Path $RepoRoot "scripts\kongor_worker_lock.ps1"
$StackLockFilePath = Join-Path $env:TEMP "kongor_docker_stack.lock"
$script:StackLockOwner = "docker-stack-$PID"
$script:StackLockAcquired = $false

if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Write-StackLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter()][ValidateSet("INFO", "WARN", "ERROR", "DEBUG", "SUCCESS")][string]$Level = "INFO"
    )
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $line = "[$ts] [DOCKER-STACK] [$Level] $Message"
    switch ($Level) {
        "INFO"    { Write-Host $line -ForegroundColor Cyan }
        "WARN"    { Write-Host $line -ForegroundColor Yellow }
        "ERROR"   { Write-Host $line -ForegroundColor Red }
        "DEBUG"   { Write-Host $line -ForegroundColor DarkGray }
        "SUCCESS" { Write-Host $line -ForegroundColor Green }
    }
    try {
        Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
    } catch {
        Write-Warning "[DOCKER-STACK] Could not append to log file '$LogFile': $($_.Exception.Message)"
    }
}

function Show-StackHelp {
    Write-Host @"
================================================================================
PROJECT KONGOR: DOCKER STACK ORCHESTRATOR
================================================================================

USAGE:
    .\kongor.ps1 docker [up|restart|rebuild|sync|down|status|logs|check|init-secrets|cleanup] [options]
    kongor docker [up|restart|rebuild|sync|down|status|logs|check|init-secrets|cleanup] [options]
    kongor sync [options]
    kongor docker-rebuild [options]

ACTIONS:
    up               Validate prerequisites, build missing/stale images, launch full Docker stack (default)
    restart          Fast restart keeping Garnet Redis live when config unchanged; passes -Service <name> for single container
    rebuild          Re-publish linux-x64 binaries, rebuild Docker images, stop host conflicts, and launch stack
    sync             All-in-one: check -> build -> cutover restart -> verify full status (alias: retest)
    check            Run strict pre-flight secrets, binary, Docker, and port checks without starting containers
    init-secrets     Safely generate CSPRNG secrets for missing/dev-default internal keys in secrets.json & .env (.bak backups)
    status           Inspect running container health plus COMPEL Match & Queue Arcade fleet status
    logs             Display container logs (-Service <name>, -Tail <n>, -Follow)
    cleanup          Remove obsolete stopped ProjectKongor containers, orphan networks & dangling images (preserves volumes/paperclip)
    down             Stop and remove the Docker stack containers cleanly (volumes preserved)

OPTIONS:
    -Performance <Eco|Balanced|High>   Backend/Docker performance preset (default: Balanced; Low aliases Eco, Max aliases High)
    -WithClient                        Launch native Windows game client (hon_x64.exe) once all containers & fleets are ready
    -UseLauncher                       With -WithClient, open WILLOWMAKER desktop launcher instead of direct hon_x64.exe
    -LocalPostgres                     Run a containerized PostgreSQL 17 instance inside Compose (merrick:25432)
    -Rebuild                           Force re-publish of linux-x64 binaries and Docker image rebuild
    -StopHostConflict                  Stop conflicting host ProjectKongor .NET/COMPEL processes before starting Docker
    -MatchInstances <1..8>             Override Dedicated Match Server worker count from preset
    -ArcadeInstances <1..8>            Override Queue Arcade worker count from preset
    -TestingDomain <domain>            Additional testing domain(s) to whitelist for CORS & SAN fallback (e.g. preview.example.com)
    -StandaloneAuth                    Switch Web Portal to offline username/password mode instead of 'Start Cluster' Discord OAuth mode
    -SkipAutoRebuild                   Reuse existing local Docker images for faster preset switches & restarts (if all images exist)
    -DryRun                            With 'cleanup', preview removable containers/networks/images without deleting anything
    -Service <name>                    Target a single service for 'logs' or 'restart' (e.g. master-server, chat-server, web-ui, compel-dedicated)
================================================================================
"@ -ForegroundColor Cyan
}

# Normalize POSIX-style flags forwarded via CLI (--perf, --client, --local-postgres, etc.)
if ($Action -in @("--help", "-h", "-?", "help")) {
    Show-StackHelp
    exit 0
}
if ($Action.StartsWith("-")) {
    $ExtraArgs = @($Action) + $ExtraArgs
    $Action = "up"
}

$parsedExplicitPerf = $false
$parsedExplicitLocalPg = $false
$argIdx = 0
while ($argIdx -lt $ExtraArgs.Count) {
    $tok = [string]$ExtraArgs[$argIdx]
    switch -Regex ($tok.ToLowerInvariant()) {
        '^(--help|-h|-\?)$' {
            Show-StackHelp
            exit 0
        }
        '^(--perf|--performance|-p)$' {
            $argIdx++
            if ($argIdx -ge $ExtraArgs.Count) { throw "Missing value for $tok. Expected Eco, Balanced, or High." }
            $Performance = [string]$ExtraArgs[$argIdx]
            $parsedExplicitPerf = $true
        }
        '^--perf=(.+)$' { $Performance = $Matches[1]; $parsedExplicitPerf = $true }
        '^--performance=(.+)$' { $Performance = $Matches[1]; $parsedExplicitPerf = $true }
        '^(--client|--with-client|-c)$' { $WithClient = $true }
        '^(--launcher|--use-launcher|--willowmaker)$' { $UseLauncher = $true; $WithClient = $true }
        '^(--local-postgres|--local-db)$' { $LocalPostgres = $true; $parsedExplicitLocalPg = $true }
        '^(--rebuild|--build|-b)$' { $Rebuild = $true }
        '^(--stop-host-conflict|--stop-conflicts)$' { $StopHostConflict = $true }
        '^(--skip-auto-rebuild|--no-build)$' { $SkipAutoRebuild = $true }
        '^(--dry-run)$' { $DryRun = $true }
        '^(--match-instances)$' {
            $argIdx++
            if ($argIdx -ge $ExtraArgs.Count) { throw "Missing integer value for --match-instances." }
            $MatchInstances = [int]$ExtraArgs[$argIdx]
        }
        '^(--arcade-instances)$' {
            $argIdx++
            if ($argIdx -ge $ExtraArgs.Count) { throw "Missing integer value for --arcade-instances." }
            $ArcadeInstances = [int]$ExtraArgs[$argIdx]
        }
        '^(--service|-s)$' {
            $argIdx++
            if ($argIdx -ge $ExtraArgs.Count) { throw "Missing service name for $tok." }
            $Service = [string]$ExtraArgs[$argIdx]
        }
        '^(--tail|-n)$' {
            $argIdx++
            if ($argIdx -ge $ExtraArgs.Count) { throw "Missing line count for $tok." }
            $Tail = [int]$ExtraArgs[$argIdx]
        }
        '^(--follow|-f)$' { $Follow = $true }
        '^(--testing-domain|--test-domain)$' {
            $argIdx++
            if ($argIdx -ge $ExtraArgs.Count) { throw "Missing domain value for $tok." }
            $TestingDomain = [string]$ExtraArgs[$argIdx]
        }
        '^--testing-domain=(.+)$' { $TestingDomain = $Matches[1] }
        '^--test-domain=(.+)$' { $TestingDomain = $Matches[1] }
        '^(--standalone-auth|--standalone)$' { $StandaloneAuth = $true }
        default {
            throw "Unknown option '$tok'. Run '.\kongor.ps1 docker --help' to inspect valid flags."
        }
    }
    $argIdx++
}

$NormalizedAction = $Action.Trim().ToLowerInvariant()
if ($NormalizedAction -in @("start", "launch", "run")) { $NormalizedAction = "up" }
if ($NormalizedAction -in @("restart", "reload", "bounce")) { $NormalizedAction = "restart" }
if ($NormalizedAction -in @("rebuild", "build")) {
    $NormalizedAction = "rebuild"
    $Rebuild = $true
    $StopHostConflict = $true
}
if ($NormalizedAction -in @("sync", "retest", "redeploy", "fresh", "reset")) {
    $NormalizedAction = "sync"
    $Rebuild = $true
    $StopHostConflict = $true
}
if ($NormalizedAction -in @("stop", "rm", "shutdown")) { $NormalizedAction = "down" }
if ($NormalizedAction -in @("preflight", "validate", "doctor")) { $NormalizedAction = "check" }
if ($NormalizedAction -in @("initsecrets", "rotate-secrets", "seed-secrets")) { $NormalizedAction = "init-secrets" }
if ($NormalizedAction -in @("cleanup", "prune", "clean")) { $NormalizedAction = "cleanup" }

if ($NormalizedAction -notin @("up", "restart", "rebuild", "sync", "down", "status", "logs", "check", "init-secrets", "cleanup")) {
    Write-StackLog "Unsupported action '$Action'. Valid actions: up, restart, rebuild, sync, down, status, logs, check, init-secrets, cleanup. Run '.\kongor.ps1 docker --help'." "ERROR"
    exit 1
}

$EffectiveSecretsPath = if (-not [string]::IsNullOrWhiteSpace($SecretsFilePath)) { $SecretsFilePath } else { $DefaultSecretsJson }
$EffectiveDotEnvPath = if (-not [string]::IsNullOrWhiteSpace($DotEnvPath)) { $DotEnvPath } else { $DefaultDotEnv }
$EffectiveRuntimeEnvPath = if (-not [string]::IsNullOrWhiteSpace($OutputEnvFilePath)) { $OutputEnvFilePath } else { $DefaultRuntimeEnvFile }
$ReadRuntimeEnvPath = if (Test-Path -LiteralPath $EffectiveRuntimeEnvPath) {
    $EffectiveRuntimeEnvPath
} elseif ([string]::IsNullOrWhiteSpace($OutputEnvFilePath) -and (Test-Path -LiteralPath $LegacyRuntimeEnvFile)) {
    $LegacyRuntimeEnvFile
} else {
    $EffectiveRuntimeEnvPath
}

$performanceExplicitlySet = $PSBoundParameters.ContainsKey("Performance") -or $parsedExplicitPerf
$localPostgresExplicitlySet = $PSBoundParameters.ContainsKey("LocalPostgres") -or $parsedExplicitLocalPg
if ($NormalizedAction -eq "restart" -and (Test-Path -LiteralPath $ReadRuntimeEnvPath)) {
    foreach ($rtLine in (Get-Content -LiteralPath $ReadRuntimeEnvPath -Encoding UTF8 -ErrorAction SilentlyContinue)) {
        $tl = "$rtLine".Trim()
        if ($tl -match '^PERFORMANCE_PRESET=[''"]?([^''"#]+)[''"]?' -and -not $performanceExplicitlySet) {
            $Performance = $Matches[1].Trim()
        } elseif ($tl -match '^USE_LOCAL_POSTGRES=[''"]?(true|false)[''"]?' -and -not $localPostgresExplicitlySet) {
            if ($Matches[1].Equals("true", [System.StringComparison]::OrdinalIgnoreCase)) {
                $LocalPostgres = $true
            }
        }
    }
}

# Normalize performance preset
$PerfKey = switch ($Performance.Trim().ToLowerInvariant()) {
    { $_ -in @("eco", "low", "minimal", "power-saver") } { "Eco" }
    { $_ -in @("balanced", "medium", "normal", "default") } { "Balanced" }
    { $_ -in @("high", "max", "ultra", "tournament") } { "High" }
    default {
        Write-StackLog "Invalid performance level '$Performance'. Allowed presets: Eco (or Low), Balanced, High (or Max). Example: .\kongor.ps1 docker up -Performance Balanced" "ERROR"
        exit 1
    }
}

$PresetTable = @{
    "Eco" = @{
        DotNetGcServer       = "0"
        DotNetGcConserve     = "7"
        GarnetMemory         = "128m"
        GarnetContainerMem   = "768M"
        DashboardMemLimit    = "256M"
        InfraCpuLimit        = "1.0"
        PostgresMemLimit     = "768M"
        ClusterCpuLimit      = "1.0"
        ClusterMemLimit      = "768M"
        MasterMemLimit       = "1024M"
        MatchInstances       = 1
        MatchWarmInstances   = 1
        MatchCpuLimit        = "2.0"
        MatchMemLimit        = "2048M"
        ArcadeInstances      = 1
        ArcadeWarmInstances  = 1
        ArcadeCpuLimit       = "1.5"
        ArcadeMemLimit       = "2048M"
        CompelPriorityMode   = "Background"
    }
    "Balanced" = @{
        DotNetGcServer       = "0"
        DotNetGcConserve     = "3"
        GarnetMemory         = "256m"
        GarnetContainerMem   = "1024M"
        DashboardMemLimit    = "256M"
        InfraCpuLimit        = "1.5"
        PostgresMemLimit     = "1024M"
        ClusterCpuLimit      = "2.0"
        ClusterMemLimit      = "1024M"
        MasterMemLimit       = "1536M"
        MatchInstances       = 2
        MatchWarmInstances   = 1
        MatchCpuLimit        = "4.0"
        MatchMemLimit        = "4096M"
        ArcadeInstances      = 2
        ArcadeWarmInstances  = 1
        ArcadeCpuLimit       = "3.0"
        ArcadeMemLimit       = "3584M"
        CompelPriorityMode   = "Adaptive"
    }
    "High" = @{
        DotNetGcServer       = "1"
        DotNetGcConserve     = "0"
        GarnetMemory         = "1g"
        GarnetContainerMem   = "2048M"
        DashboardMemLimit    = "384M"
        InfraCpuLimit        = "2.0"
        PostgresMemLimit     = "2048M"
        ClusterCpuLimit      = "4.0"
        ClusterMemLimit      = "2048M"
        MasterMemLimit       = "3072M"
        MatchInstances       = 4
        MatchWarmInstances   = 2
        MatchCpuLimit        = "6.0"
        MatchMemLimit        = "8192M"
        ArcadeInstances      = 2
        ArcadeWarmInstances  = 2
        ArcadeCpuLimit       = "4.0"
        ArcadeMemLimit       = "4608M"
        CompelPriorityMode   = "HighPriority"
    }
}

$ActivePreset = $PresetTable[$PerfKey].Clone()
if ($MatchInstances -gt 0) {
    if ($MatchInstances -gt 8) {
        Write-StackLog "-MatchInstances ($MatchInstances) exceeds published Compose UDP ceiling (8). Pass a value between 1 and 8." "ERROR"
        exit 1
    }
    $ActivePreset.MatchInstances = $MatchInstances
    if ($ActivePreset.MatchWarmInstances -gt $MatchInstances) { $ActivePreset.MatchWarmInstances = $MatchInstances }
}
if ($ArcadeInstances -gt 0) {
    if ($ArcadeInstances -gt 8) {
        Write-StackLog "-ArcadeInstances ($ArcadeInstances) exceeds published Compose UDP ceiling (8). Pass a value between 1 and 8." "ERROR"
        exit 1
    }
    $ActivePreset.ArcadeInstances = $ArcadeInstances
    if ($ActivePreset.ArcadeWarmInstances -gt $ArcadeInstances) { $ActivePreset.ArcadeWarmInstances = $ArcadeInstances }
}

Write-StackLog "Invoked action='$NormalizedAction' performance='$PerfKey' localPostgres=$LocalPostgres withClient=$WithClient useLauncher=$UseLauncher rebuild=$Rebuild skipAutoRebuild=$SkipAutoRebuild dryRun=$DryRun" "INFO"

function Read-ConfigStores {
    $vaultMap = @{}
    $dotEnvMap = @{}

    if (Test-Path -LiteralPath $EffectiveSecretsPath) {
        try {
            $rawJson = Get-Content -LiteralPath $EffectiveSecretsPath -Raw -Encoding UTF8
            if (-not [string]::IsNullOrWhiteSpace($rawJson)) {
                $parsed = $rawJson | ConvertFrom-Json
                foreach ($prop in $parsed.PSObject.Properties) {
                    if ($null -ne $prop.Value) {
                        $vaultMap[$prop.Name] = [string]$prop.Value
                    }
                }
            }
            Write-StackLog "Loaded $($vaultMap.Count) key(s) from User Secrets store ($EffectiveSecretsPath)." "DEBUG"
        } catch {
            throw "Failed to parse User Secrets JSON at '$EffectiveSecretsPath': $($_.Exception.Message). Repair the file or run '.\kongor.ps1 secret list' to diagnose."
        }
    } else {
        Write-StackLog "User Secrets file not found at '$EffectiveSecretsPath' (checking .env and environment variables)." "DEBUG"
    }

    if (Test-Path -LiteralPath $EffectiveDotEnvPath) {
        $lines = Get-Content -LiteralPath $EffectiveDotEnvPath -Encoding UTF8
        foreach ($line in $lines) {
            $trimmed = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }
            $eqIdx = $trimmed.IndexOf('=')
            if ($eqIdx -le 0) { continue }
            $k = $trimmed.Substring(0, $eqIdx).Trim()
            $v = $trimmed.Substring($eqIdx + 1).Trim()
            if (($v.StartsWith('"') -and $v.EndsWith('"')) -or ($v.StartsWith("'") -and $v.EndsWith("'"))) {
                if ($v.Length -ge 2) { $v = $v.Substring(1, $v.Length - 2) }
            }
            $dotEnvMap[$k] = $v
        }
        Write-StackLog "Loaded $($dotEnvMap.Count) key(s) from .env store ($EffectiveDotEnvPath)." "DEBUG"
    }

    return @{
        Vault  = $vaultMap
        DotEnv = $dotEnvMap
    }
}

function Test-IsPlaceholderValue {
    param(
        [string]$Value,
        [string[]]$ExtraDisallowed = @()
    )
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    $v = $Value.Trim()
    if ($v -in @("...", "CHANGEME", "CHANGE_ME", "REPLACE_ME", "TODO", "MANAGED_BY_ASPIRE", "dev-placeholder")) {
        return $true
    }
    if ($v -match '^(YOUR_|<[^>]+>|INSERT_)'){
        return $true
    }
    foreach ($bad in $ExtraDisallowed) {
        if ([string]::Equals($v, $bad, [System.StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Resolve-ConfigSetting {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Stores,
        [Parameter(Mandatory = $true)][string[]]$Keys,
        [string[]]$ExtraDisallowed = @()
    )

    $firstFoundFallback = $null

    # 1. Process / User Environment Variables (highest priority)
    if (-not $IgnoreProcessEnv) {
        foreach ($candidate in $Keys) {
            $candidatesToTry = @($candidate)
            $envForm = $candidate.Replace(":", "__")
            if ($envForm -ne $candidate) { $candidatesToTry += $envForm }

            foreach ($k in $candidatesToTry) {
                $envVal = [Environment]::GetEnvironmentVariable($k)
                if (-not [string]::IsNullOrWhiteSpace($envVal)) {
                    $hit = @{ Value = $envVal.Trim(); Source = "env:$k"; Found = $true }
                    if (-not (Test-IsPlaceholderValue -Value $hit.Value -ExtraDisallowed $ExtraDisallowed)) {
                        return $hit
                    }
                    if ($null -eq $firstFoundFallback) { $firstFoundFallback = $hit }
                }
            }
        }
    }

    # 2. .NET User Secrets Vault (updated immediately by '.\kongor.ps1 secret set' & Control Panel GUI)
    foreach ($candidate in $Keys) {
        foreach ($vKey in $Stores.Vault.Keys) {
            if ([string]::Equals($vKey, $candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
                $val = $Stores.Vault[$vKey]
                if (-not [string]::IsNullOrWhiteSpace($val)) {
                    $hit = @{ Value = $val.Trim(); Source = "user-secrets:$vKey"; Found = $true }
                    if (-not (Test-IsPlaceholderValue -Value $hit.Value -ExtraDisallowed $ExtraDisallowed)) {
                        return $hit
                    }
                    if ($null -eq $firstFoundFallback) { $firstFoundFallback = $hit }
                }
            }
        }
    }

    # 3. Repository .env file fallback
    foreach ($candidate in $Keys) {
        $candidatesToTry = @($candidate, $candidate.Replace(":", "__"))
        foreach ($k in $candidatesToTry) {
            if ($Stores.DotEnv.ContainsKey($k) -and -not [string]::IsNullOrWhiteSpace($Stores.DotEnv[$k])) {
                $hit = @{ Value = $Stores.DotEnv[$k].Trim(); Source = ".env:$k"; Found = $true }
                if (-not (Test-IsPlaceholderValue -Value $hit.Value -ExtraDisallowed $ExtraDisallowed)) {
                    return $hit
                }
                if ($null -eq $firstFoundFallback) { $firstFoundFallback = $hit }
            }
        }
    }

    if ($null -ne $firstFoundFallback) {
        return $firstFoundFallback
    }
    return @{ Value = ""; Source = "none"; Found = $false }
}

function Convert-LoopbackConnectionStringForDocker {
    param([Parameter(Mandatory = $true)][string]$ConnStr)
    $normalized = $ConnStr
    $normalized = $normalized -replace '(?i)(Host|Server|Data Source)\s*=\s*(localhost|127\.0\.0\.1)\b', '$1=host.docker.internal'
    $normalized = $normalized -replace '(?i)@(localhost|127\.0\.0\.1)(?=[:/])', '@host.docker.internal'
    $normalized = $normalized -replace '(?i)://(localhost|127\.0\.0\.1)(?=[:/])', '://host.docker.internal'
    return $normalized
}

function Get-DefaultHostUserPrefix {
    param($Stores = $null)
    if ($null -ne $Stores) {
        $prefixRes = Resolve-ConfigSetting -Stores $Stores -Keys @("KONGOR_HOST_USER_PREFIX", "HostAccount:UserPrefix")
        if ($prefixRes.Found -and -not [string]::IsNullOrWhiteSpace($prefixRes.Value)) {
            return $prefixRes.Value.Trim()
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_HOST_USER_PREFIX)) {
        return $env:KONGOR_HOST_USER_PREFIX.Trim()
    }
    return "kongor_host"
}

function Get-DerivedArcadeHostUsername {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUsername,
        [string]$DefaultPrefix = (Get-DefaultHostUserPrefix)
    )
    $trimmed = $BaseUsername.Trim()
    if ($trimmed -match '(?i)_arc$' -and $trimmed.Length -le 15 -and $trimmed.Length -gt 4) {
        return $trimmed
    }
    $stem = $trimmed -replace '(?i)(_arc|_arcade|_local|_dock|_docker|_k3s)$', ''
    if ([string]::IsNullOrWhiteSpace($stem)) { $stem = $DefaultPrefix }
    if ($stem.Length -gt 11) {
        $stem = $stem.Substring(0, 11).TrimEnd('_')
    }
    return "${stem}_arc"
}

function Get-MatchHostUsername {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUsername,
        [string]$DefaultPrefix = (Get-DefaultHostUserPrefix)
    )
    $trimmed = $BaseUsername.Trim()
    if ($trimmed -in @($DefaultPrefix, "${DefaultPrefix}_local")) {
        return "${DefaultPrefix}_dock"
    }
    if ($trimmed.Length -gt 15) {
        return $trimmed.Substring(0, 15)
    }
    return $trimmed
}

function Format-DotEnvLine {
    param(
        [Parameter(Mandatory = $true)][string]$Key,
        [AllowEmptyString()][string]$Value
    )
    $safe = if ($null -eq $Value) { "" } else { $Value.Replace("`r", "").Replace("`n", "") }
    # Single-quote values so Docker Compose treats '$' and '!' literally without variable expansion
    if (-not $safe.Contains("'")) {
        return "${Key}='${safe}'"
    }
    $escaped = $safe.Replace('\', '\\').Replace('"', '\"').Replace('$', '$$')
    return "${Key}=`"$escaped`""
}

function Invoke-Compose {
    param([Parameter(Mandatory = $true)][string[]]$ComposeArgs)
    $baseArgs = @("compose", "-f", $ComposeFile)
    if (Test-Path -LiteralPath $ReadRuntimeEnvPath) {
        $baseArgs += @("--env-file", $ReadRuntimeEnvPath)
    }
    if ($LocalPostgres) {
        $baseArgs += @("--profile", "local-postgres")
    }
    $all = $baseArgs + $ComposeArgs
    Write-StackLog "Executing: docker $($all -join ' ')" "DEBUG"
    & docker @all | Out-Host
    return [int]$LASTEXITCODE
}

function Acquire-DockerStackLock {
    if (-not (Test-Path -LiteralPath $WorkerLockScript)) {
        Write-StackLog "Worker lock script missing at '$WorkerLockScript'; proceeding without external stack lock." "DEBUG"
        return
    }
    $prevLockEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $WorkerLockScript `
            -Action acquire `
            -Scope test-runner `
            -Owner $script:StackLockOwner `
            -PidToTrack $PID `
            -WaitSeconds 5 `
            -LeaseTtlSeconds 1800 `
            -CommandSummary "docker-stack:$NormalizedAction" `
            -LockFilePath $StackLockFilePath `
            -Quiet
        $lockExit = [int]$LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevLockEap
    }

    if ($lockExit -ne 0) {
        $holderDesc = "another running Docker stack operation"
        if (Test-Path -LiteralPath $StackLockFilePath) {
            try {
                $lockInfo = Get-Content -LiteralPath $StackLockFilePath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($null -ne $lockInfo) {
                    $holderDesc = "owner='$($lockInfo.owner)', pid=$($lockInfo.pid), cmd='$($lockInfo.commandSummary)', acquired=$($lockInfo.acquiredAt)"
                }
            } catch {
                Write-StackLog "Could not inspect active stack lock file: $($_.Exception.Message)" "DEBUG"
            }
        }
        Write-StackLog "[STACK-LOCK] Stack busy ($holderDesc). Wait for the active operation to complete before starting '$NormalizedAction'." "ERROR"
        exit 75
    }

    $script:StackLockAcquired = $true
    Write-StackLog "[STACK-LOCK] Acquired singleton stack lock (owner='$script:StackLockOwner', ttl=1800s) at '$StackLockFilePath'." "DEBUG"
}

function Release-DockerStackLock {
    if (-not $script:StackLockAcquired) {
        return
    }
    $script:StackLockAcquired = $false
    if (-not (Test-Path -LiteralPath $WorkerLockScript)) {
        return
    }
    $prevRelEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $WorkerLockScript `
            -Action release `
            -Scope test-runner `
            -Owner $script:StackLockOwner `
            -PidToTrack $PID `
            -LockFilePath $StackLockFilePath `
            -Quiet *> $null
        Write-StackLog "[STACK-LOCK] Released singleton stack lock (owner='$script:StackLockOwner')." "DEBUG"
    } catch {
        Write-StackLog "[STACK-LOCK] Ignored lock release notice: $($_.Exception.Message)" "DEBUG"
    } finally {
        $ErrorActionPreference = $prevRelEap
    }
}

function Resolve-DockerServiceTarget {
    param([Parameter(Mandatory = $true)][string]$Name)
    $norm = $Name.Trim().ToLowerInvariant()
    switch ($norm) {
        "master-server" { return "master-server" }
        "chat-server" { return "chat-server" }
        { $_ -in @("web-api", "web-portal-api") } { return "web-api" }
        { $_ -in @("web-ui", "web-portal-ui") } { return "web-ui" }
        { $_ -in @("compel-dedicated", "game-servers", "compel", "match") } { return "compel-dedicated" }
        { $_ -in @("compel-queue-arcade", "queue-arcade", "compel-arcade", "arcade") } { return "compel-queue-arcade" }
        { $_ -in @("garnet", "redis") } { return "garnet" }
        { $_ -in @("aspire-dashboard", "dashboard") } { return "aspire-dashboard" }
        { $_ -in @("postgres", "merrick") } { return "postgres" }
        default {
            Write-StackLog "Unknown Docker service target '$Name'. Valid services: master-server, chat-server, web-api, web-ui, compel-dedicated, compel-queue-arcade, garnet, aspire-dashboard, postgres." "ERROR"
            exit 1
        }
    }
}

function Get-NormalizedEnvConfigText {
    param([AllowEmptyString()][string]$RawContent)
    if ([string]::IsNullOrWhiteSpace($RawContent)) {
        return ""
    }
    $keptLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($RawContent -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }
        # Source stamp changes on dirty working tree do not alter running container configuration when --no-build is active
        if ($trimmed -match '^KONGOR_SOURCE_STAMP=') { continue }
        $keptLines.Add($trimmed)
    }
    return ($keptLines -join "`n")
}

function Test-ProjectKongorDanglingImage {
    param([Parameter(Mandatory = $true)][string]$ImageId)
    $prevImgEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $inspectJson = (& docker image inspect $ImageId --format '{{json .Config}}' 2>$null) -join ""
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($inspectJson)) {
            return $false
        }
        $cfg = $inspectJson | ConvertFrom-Json
        if ($null -eq $cfg) { return $false }

        $epList = if ($null -ne $cfg.Entrypoint) { @($cfg.Entrypoint) } else { @() }
        $epJoined = ($epList -join " ")
        if ($epJoined -match '/app/run-service|/app/entrypoint\.sh') {
            return $true
        }

        if ($null -ne $cfg.Labels) {
            foreach ($prop in $cfg.Labels.PSObject.Properties) {
                $lk = [string]$prop.Name
                $lv = [string]$prop.Value
                if ($lk -eq "com.docker.compose.project" -and $lv -eq "kongor-stack") { return $true }
                if ($lk.StartsWith("org.kongor.", [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                if ($lk -eq "org.opencontainers.image.title" -and $lv.StartsWith("ProjectKongor", [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                if ($lv -match 'ProjectKongor') { return $true }
            }
        }
    } catch {
        Write-StackLog "Ignored image inspect failure for '$ImageId': $($_.Exception.Message)" "DEBUG"
    } finally {
        $ErrorActionPreference = $prevImgEap
    }
    return $false
}

function Invoke-DockerLegacyCleanup {
    param([switch]$IsDryRun)

    Write-StackLog "$(if ($IsDryRun) { '[CLEANUP] [DRY-RUN]' } else { '[CLEANUP]' }) Scanning legacy ProjectKongor Docker artifacts (strictly preserving paperclip-*, mcp/*, kongor-pyroscope, active kongor-stack-*, and all volumes)..." "INFO"

    $prevCleanEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & docker info *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-StackLog "Docker Linux daemon is offline; start Docker Desktop before running 'cleanup'." "ERROR"
            exit 1
        }

        $targetContainers = New-Object System.Collections.Generic.List[string]
        $rawPs = @(& docker ps -a --format '{{.Names}}|{{.Image}}|{{.State}}' 2>$null)
        foreach ($row in $rawPs) {
            if ([string]::IsNullOrWhiteSpace($row)) { continue }
            $parts = $row -split '\|', 3
            if ($parts.Count -lt 3) { continue }
            $cName = $parts[0].Trim()
            $cImage = $parts[1].Trim()
            $cState = $parts[2].Trim().ToLowerInvariant()

            if ($cState -eq "running") { continue }
            if ($cName -match '^paperclip' -or $cName -eq "kongor-pyroscope") { continue }
            if ($cImage -match '^(mcp/|paperclip)' -or $cName -match '^kongor-stack-') { continue }

            $isLegacyKongor =
                ($cName -eq "kongor-garnet-cache") -or
                ($cName -match '-dac75550$') -or
                ($cName -in @("arcade-compel-server", "compel-dedicated-server", "ranked-compel-server", "compel-queue-arcade-server", "kongor-linux-client-journey")) -or
                ($cName -match '^kongor-[0-9a-f]{8}-') -or
                ($cImage -match '^projectkongor/')

            if ($isLegacyKongor -and -not $targetContainers.Contains($cName)) {
                $targetContainers.Add($cName)
            }
        }

        $targetNetworks = New-Object System.Collections.Generic.List[string]
        $rawNets = @(& docker network ls --format '{{.Name}}' 2>$null)
        foreach ($netName in $rawNets) {
            $n = "$netName".Trim()
            if ([string]::IsNullOrWhiteSpace($n)) { continue }
            if ($n -in @("bridge", "host", "none", "docker_default", "kongor-stack_default")) { continue }
            if ($n -match 'paperclip') { continue }
            if ($n -match '^aspire-persistent-network-' -or $n -match '^kongor-[0-9a-f]{8}_default$') {
                $targetNetworks.Add($n)
            }
        }

        $targetImages = New-Object System.Collections.Generic.List[object]
        $rawImgs = @(& docker images -f "dangling=true" --format '{{.ID}}|{{.Size}}|{{.CreatedSince}}' 2>$null)
        $seenImgIds = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($imgRow in $rawImgs) {
            if ([string]::IsNullOrWhiteSpace($imgRow)) { continue }
            $ip = $imgRow -split '\|', 3
            $imgId = $ip[0].Trim()
            $imgSize = if ($ip.Count -gt 1) { $ip[1].Trim() } else { "?" }
            $imgAge = if ($ip.Count -gt 2) { $ip[2].Trim() } else { "?" }
            if ([string]::IsNullOrWhiteSpace($imgId) -or -not $seenImgIds.Add($imgId)) { continue }

            if (Test-ProjectKongorDanglingImage -ImageId $imgId) {
                $targetImages.Add([PSCustomObject]@{ Id = $imgId; Size = $imgSize; Age = $imgAge })
            }
        }

        Write-StackLog "[CLEANUP] Identified: $($targetContainers.Count) legacy container(s), $($targetNetworks.Count) orphan network(s), $($targetImages.Count) dangling ProjectKongor image(s) (0 volumes touched)." "INFO"
        foreach ($tc in $targetContainers) {
            Write-StackLog "  $(if ($IsDryRun) { '[DRY-RUN container]' } else { '[REMOVE container]' }) $tc" "INFO"
        }
        foreach ($tn in $targetNetworks) {
            Write-StackLog "  $(if ($IsDryRun) { '[DRY-RUN network]' } else { '[REMOVE network]' }) $tn" "INFO"
        }
        foreach ($ti in $targetImages) {
            Write-StackLog "  $(if ($IsDryRun) { '[DRY-RUN image]' } else { '[REMOVE image]' }) $($ti.Id) ($($ti.Size), $($ti.Age))" "INFO"
        }

        if ($IsDryRun) {
            Write-StackLog "[CLEANUP] Dry-run complete; no Docker resources were modified." "SUCCESS"
            return
        }

        $removedContainers = 0
        foreach ($tc in $targetContainers) {
            & docker rm $tc *> $null
            if ($LASTEXITCODE -eq 0) {
                $removedContainers++
            } else {
                Write-StackLog "[CLEANUP] Could not remove container '$tc' (exit $LASTEXITCODE)." "WARN"
            }
        }

        $removedNetworks = 0
        foreach ($tn in $targetNetworks) {
            & docker network rm $tn *> $null
            if ($LASTEXITCODE -eq 0) {
                $removedNetworks++
            } else {
                Write-StackLog "[CLEANUP] Could not remove network '$tn' (exit $LASTEXITCODE)." "WARN"
            }
        }

        $removedImages = 0
        foreach ($ti in $targetImages) {
            & docker image rm $ti.Id *> $null
            if ($LASTEXITCODE -eq 0) {
                $removedImages++
            } else {
                Write-StackLog "[CLEANUP] Skipped image '$($ti.Id)' (referenced by an active container/tag; exit $LASTEXITCODE)." "DEBUG"
            }
        }

        Write-StackLog "[CLEANUP] Complete -> removed $removedContainers container(s), $removedNetworks network(s), $removedImages dangling image(s). paperclip-*, mcp/*, and all volumes preserved." "SUCCESS"
    } finally {
        $ErrorActionPreference = $prevCleanEap
    }
}

$mutatingActions = @("up", "restart", "rebuild", "sync", "down", "init-secrets")
if ($NormalizedAction -in $mutatingActions -or ($NormalizedAction -eq "cleanup" -and -not $DryRun)) {
    Acquire-DockerStackLock
}

try {

# Handle 'down', 'logs', and 'cleanup' early without requiring full preflight rebuilds
if ($NormalizedAction -eq "down") {
    Write-StackLog "Stopping ProjectKongor Docker stack (volumes preserved)..." "INFO"
    $downArgs = @("compose", "-f", $ComposeFile, "--profile", "local-postgres")
    if (Test-Path -LiteralPath $ReadRuntimeEnvPath) {
        $downArgs += @("--env-file", $ReadRuntimeEnvPath)
    }
    $downArgs += @("down", "--remove-orphans")
    & docker @downArgs
    if ($LASTEXITCODE -ne 0) {
        Write-StackLog "docker compose down exited with code $LASTEXITCODE. Ensure Docker Desktop is running ('docker info')." "ERROR"
        exit $LASTEXITCODE
    }
    Write-StackLog "Docker stack stopped cleanly." "SUCCESS"
    exit 0
}

if ($NormalizedAction -eq "logs") {
    $logArgs = @("compose", "-f", $ComposeFile, "--profile", "local-postgres")
    if (Test-Path -LiteralPath $ReadRuntimeEnvPath) {
        $logArgs += @("--env-file", $ReadRuntimeEnvPath)
    }
    $logArgs += @("logs", "--tail", "$Tail")
    if ($Follow) { $logArgs += "--follow" }
    if (-not [string]::IsNullOrWhiteSpace($Service)) { $logArgs += (Resolve-DockerServiceTarget -Name $Service) }
    & docker @logArgs
    exit $LASTEXITCODE
}

if ($NormalizedAction -eq "cleanup") {
    Invoke-DockerLegacyCleanup -IsDryRun:$DryRun
    exit 0
}

function New-CsprngAlphanumericSecret {
    param([int]$Length = 48)
    $alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
    $chars = New-Object char[] $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $buf = New-Object byte[] 1
        for ($idx = 0; $idx -lt $Length; $idx++) {
            # Rejection sampling (< 248 = 62 * 4) eliminates modulo bias on PS 5.1 & PS 7+
            do {
                $rng.GetBytes($buf)
            } while ($buf[0] -ge 248)
            $chars[$idx] = $alphabet[($buf[0] % 62)]
        }
    } finally {
        $rng.Dispose()
    }
    return -join $chars
}

function Test-HttpsEndpointReady {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter()][string]$Path = "/health/ready",
        [Parameter()][string]$HostHeader = "localhost",
        [Parameter()][string]$ExpectedThumbprint = "",
        [Parameter()][int[]]$AcceptedStatusCodes = @(200)
    )
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $tcp.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(2500, $false) -or -not $tcp.Connected) {
            return @{ Ready = $false; StatusCode = 0 }
        }
        $tcp.EndConnect($async)
        $tcp.ReceiveTimeout = 2500
        $tcp.SendTimeout = 2500

        $thumbToMatch = $ExpectedThumbprint
        $ssl = New-Object System.Net.Security.SslStream(
            $tcp.GetStream(),
            $false,
            ({
                param($sender, $certificate, $chain, $sslPolicyErrors)
                if ($sslPolicyErrors -eq [System.Net.Security.SslPolicyErrors]::None) { return $true }
                if ($null -ne $certificate -and -not [string]::IsNullOrWhiteSpace($thumbToMatch)) {
                    $cert2 = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificate)
                    if ([string]::Equals($cert2.Thumbprint, $thumbToMatch, [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $true
                    }
                }
                # Accept localhost dev certificate in status checks when thumbprint is not passed
                if ([string]::IsNullOrWhiteSpace($thumbToMatch) -and $null -ne $certificate -and $certificate.Subject -match "CN=localhost") {
                    return $true
                }
                return $false
            }.GetNewClosure())
        )
        try {
            $ssl.AuthenticateAsClient("localhost")
            $reqBytes = [System.Text.Encoding]::ASCII.GetBytes("GET $Path HTTP/1.1`r`nHost: ${HostHeader}:$Port`r`nConnection: close`r`n`r`n")
            $ssl.Write($reqBytes, 0, $reqBytes.Length)
            $ssl.Flush()
            $reader = New-Object System.IO.StreamReader($ssl, [System.Text.Encoding]::ASCII)
            $statusLine = $reader.ReadLine()
            if ($statusLine -match '^HTTP/\d\.\d\s+(\d{3})\b') {
                $code = [int]$Matches[1]
                return @{ Ready = ($code -in $AcceptedStatusCodes); StatusCode = $code }
            }
            return @{ Ready = $false; StatusCode = 0 }
        } finally {
            $ssl.Dispose()
        }
    } catch {
        return @{ Ready = $false; StatusCode = 0 }
    } finally {
        $tcp.Close()
    }
}

function Ensure-DockerHttpsCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$CertDirectory,
        [Parameter()][string[]]$ExtraSanDomains = @(),
        [Parameter()][switch]$PreferFallbackOnly
    )
    if (-not (Test-Path -LiteralPath $CertDirectory)) {
        New-Item -ItemType Directory -Path $CertDirectory -Force | Out-Null
    }
    $pfxPath = Join-Path $CertDirectory "aspnetcore-devcert.pfx"
    $passPath = Join-Path $CertDirectory "aspnetcore-devcert.pass"

    if ((Test-Path -LiteralPath $pfxPath) -and (Test-Path -LiteralPath $passPath)) {
        try {
            $savedPass = (Get-Content -LiteralPath $passPath -Raw -Encoding UTF8).Trim()
            if (-not [string]::IsNullOrWhiteSpace($savedPass)) {
                $existingCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
                    $pfxPath,
                    $savedPass,
                    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
                )
                $expiresUtc = $existingCert.NotAfter.ToUniversalTime()
                $thumb = $existingCert.Thumbprint
                $existingCert.Dispose()
                if ($expiresUtc -gt (Get-Date).ToUniversalTime().AddDays(7)) {
                    Write-StackLog "[PREFLIGHT-OK] Reusing cached Docker HTTPS certificate (Thumbprint=$thumb, Expires=$($expiresUtc.ToString('yyyy-MM-dd')))." "DEBUG"
                    return @{
                        PfxPath    = $pfxPath
                        Password   = $savedPass
                        Thumbprint = $thumb
                        Source     = "cached-pfx"
                    }
                }
            }
        } catch {
            Write-StackLog "Regenerating Docker HTTPS certificate because cached PFX could not be opened: $($_.Exception.Message)" "DEBUG"
        }
    }

    $certPass = New-CsprngAlphanumericSecret -Length 32
    $exportedViaDevCerts = $false

    if (-not $PreferFallbackOnly) {
        $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($dotnetCmd) {
            $prevCertEap = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            try {
                if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force }
                & dotnet dev-certs https -ep $pfxPath -p $certPass --quiet *> $null
                if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $pfxPath)) {
                    $exportedViaDevCerts = $true
                }
            } catch {
                $exportedViaDevCerts = $false
            } finally {
                $ErrorActionPreference = $prevCertEap
            }
        }
    }

    if (-not $exportedViaDevCerts) {
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        try {
            $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost",
                $rsa,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
            )
            $req.CertificateExtensions.Add(
                (New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true))
            )
            $req.CertificateExtensions.Add(
                (New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                    ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment),
                    $true
                ))
            )
            $ekuCol = New-Object System.Security.Cryptography.OidCollection
            [void]$ekuCol.Add((New-Object System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")))
            $req.CertificateExtensions.Add(
                (New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($ekuCol, $false))
            )
            $san = New-Object System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
            $san.AddDnsName("localhost")
            $san.AddIpAddress([System.Net.IPAddress]::Loopback)
            $san.AddIpAddress([System.Net.IPAddress]::IPv6Loopback)
            $publicDomain = if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_PUBLIC_DOMAIN)) { $env:KONGOR_PUBLIC_DOMAIN.Trim() } else { "example.com" }
            foreach ($dns in (@($publicDomain, "www.$publicDomain", "masterserver.$publicDomain", "test.$publicDomain", "staging.$publicDomain", "dev.$publicDomain") + $ExtraSanDomains | Select-Object -Unique)) {
                if (-not [string]::IsNullOrWhiteSpace($dns)) {
                    $san.AddDnsName($dns.Trim())
                }
            }
            $req.CertificateExtensions.Add($san.Build())

            $nowUtc = [System.DateTimeOffset]::UtcNow
            $generatedCert = $req.CreateSelfSigned($nowUtc.AddMinutes(-5), $nowUtc.AddDays(365))
            try {
                $pfxBytes = $generatedCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $certPass)
                [System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
            } finally {
                $generatedCert.Dispose()
            }
        } finally {
            $rsa.Dispose()
        }
    }

    Set-Content -LiteralPath $passPath -Value $certPass -NoNewline -Encoding UTF8
    $loadedCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $certPass)
    $finalThumb = $loadedCert.Thumbprint
    $finalExp = $loadedCert.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd")
    $loadedCert.Dispose()
    $certSource = if ($exportedViaDevCerts) { "dotnet-dev-certs" } else { "self-signed-san-fallback" }
    Write-StackLog "[PREFLIGHT-OK] Provisioned Docker HTTPS certificate (Source=$certSource, Thumbprint=$finalThumb, Expires=$finalExp)." "DEBUG"
    return @{
        PfxPath    = $pfxPath
        Password   = $certPass
        Thumbprint = $finalThumb
        Source     = $certSource
    }
}

function Test-TcpEndpointReady {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter()][string]$HostAddress = "127.0.0.1",
        [Parameter()][int]$TimeoutMs = 2000
    )
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $tcp.BeginConnect($HostAddress, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false) -or -not $tcp.Connected) {
            return $false
        }
        $tcp.EndConnect($async)
        return $true
    } catch {
        return $false
    } finally {
        $tcp.Close()
    }
}

function Invoke-DockerStackStatusProbe {
    param(
        [switch]$Strict,
        [string]$ExpectedThumbprint = ""
    )

    Write-StackLog "Querying Docker stack container status..." "INFO"

    $rtEnvMap = @{}
    if (Test-Path -LiteralPath $ReadRuntimeEnvPath) {
        foreach ($envLine in (Get-Content -LiteralPath $ReadRuntimeEnvPath -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            $trimmedLine = "$envLine".Trim()
            if ([string]::IsNullOrWhiteSpace($trimmedLine) -or $trimmedLine.StartsWith("#")) { continue }
            $eqIdx = $trimmedLine.IndexOf("=")
            if ($eqIdx -le 0) { continue }
            $k = $trimmedLine.Substring(0, $eqIdx).Trim()
            $v = $trimmedLine.Substring($eqIdx + 1).Trim()
            if (($v.StartsWith("'") -and $v.EndsWith("'") -and $v.Length -ge 2) -or
                ($v.StartsWith('"') -and $v.EndsWith('"') -and $v.Length -ge 2)) {
                $v = $v.Substring(1, $v.Length - 2)
            }
            $rtEnvMap[$k] = $v
        }
    }

    $expectLocalPostgres = [bool]$LocalPostgres -or ($rtEnvMap["USE_LOCAL_POSTGRES"] -eq "true") -or ($rtEnvMap["DATABASE_PROVIDER"] -eq "LocalPostgres") -or ($rtEnvMap["CONTAINER_DB_CONNECTION_STRING"] -match '(^|;)Host=postgres(;|$)')
    $garnetPort = if ($rtEnvMap.ContainsKey("REDIS_HOST_PORT") -and $rtEnvMap["REDIS_HOST_PORT"] -match '^\d+$') { [int]$rtEnvMap["REDIS_HOST_PORT"] } else { 28298 }
    $pgPort = if ($rtEnvMap.ContainsKey("POSTGRES_HOST_PORT") -and $rtEnvMap["POSTGRES_HOST_PORT"] -match '^\d+$') { [int]$rtEnvMap["POSTGRES_HOST_PORT"] } else { 25432 }
    $rtCompelToken = if ($rtEnvMap.ContainsKey("COMPEL_AUTH_TOKEN")) { $rtEnvMap["COMPEL_AUTH_TOKEN"] } else { "" }

    $effectiveThumb = $ExpectedThumbprint
    if ([string]::IsNullOrWhiteSpace($effectiveThumb) -and -not [string]::IsNullOrWhiteSpace($ReadRuntimeEnvPath)) {
        $probeCertDir = Split-Path -Parent $ReadRuntimeEnvPath
        $probePfxPath = Join-Path $probeCertDir "aspnetcore-devcert.pfx"
        $probePassPath = Join-Path $probeCertDir "aspnetcore-devcert.pass"
        if ((Test-Path -LiteralPath $probePfxPath) -and (Test-Path -LiteralPath $probePassPath)) {
            try {
                $probePass = (Get-Content -LiteralPath $probePassPath -Raw -Encoding UTF8).Trim()
                if (-not [string]::IsNullOrWhiteSpace($probePass)) {
                    $probeCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($probePfxPath, $probePass)
                    try {
                        $effectiveThumb = $probeCert.Thumbprint
                    } finally {
                        $probeCert.Dispose()
                    }
                }
            } catch {
                Write-StackLog "Ignored unreadable cached devcert during status probe: $($_.Exception.Message)" "DEBUG"
            }
        }
    }

    $psBaseArgs = @("compose", "-f", $ComposeFile)
    if ($expectLocalPostgres) {
        $psBaseArgs += @("--profile", "local-postgres")
    }
    if (Test-Path -LiteralPath $ReadRuntimeEnvPath) {
        $psBaseArgs += @("--env-file", $ReadRuntimeEnvPath)
    }

    & docker @($psBaseArgs + @("ps")) | Out-Host
    $psExit = [int]$LASTEXITCODE
    $offlineCount = 0

    $rawPsJson = @()
    $prevProbeEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $rawPsJson = @(& docker @($psBaseArgs + @("ps", "-a", "--format", "json")) 2>$null)
    } finally {
        $ErrorActionPreference = $prevProbeEap
    }

    $parsedContainers = @()
    $joinedJson = ($rawPsJson | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") }) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($joinedJson)) {
        $trimmedJson = $joinedJson.Trim()
        if ($trimmedJson.StartsWith("[")) {
            try {
                $arrObj = $trimmedJson | ConvertFrom-Json
                if ($null -ne $arrObj) { $parsedContainers = @($arrObj) }
            } catch {
                Write-StackLog "Ignored unparseable docker compose ps JSON array: $($_.Exception.Message)" "DEBUG"
            }
        } else {
            foreach ($jsonLine in ($rawPsJson | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") })) {
                $tl = "$jsonLine".Trim()
                if ($tl.StartsWith("{")) {
                    try {
                        $parsedContainers += ($tl | ConvertFrom-Json)
                    } catch {
                        Write-StackLog "Ignored unparseable docker compose ps JSON line: $($_.Exception.Message)" "DEBUG"
                    }
                }
            }
        }
    }

    $svcMap = @{}
    foreach ($c in $parsedContainers) {
        $cSvc = if ($null -ne $c.Service) { "$($c.Service)".Trim() } else { "" }
        $cName = if ($null -ne $c.Name) { "$($c.Name)".Trim() } else { "" }
        if ([string]::IsNullOrWhiteSpace($cSvc) -and -not [string]::IsNullOrWhiteSpace($cName)) {
            if ($cName -eq "kongor-stack-compel-arcade") {
                $cSvc = "compel-queue-arcade"
            } elseif ($cName -match '^kongor-stack-(.+)$') {
                $cSvc = $Matches[1]
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($cSvc)) {
            $svcMap[$cSvc] = $c
        }
    }

    $expectedServices = New-Object System.Collections.Generic.List[string]
    foreach ($svcName in @("garnet", "aspire-dashboard", "master-server", "chat-server", "web-api", "web-ui", "compel-dedicated", "compel-queue-arcade")) {
        $expectedServices.Add($svcName)
    }
    if ($expectLocalPostgres) {
        $expectedServices.Add("postgres")
    }

    if ($psExit -eq 0) {
        foreach ($expSvc in $expectedServices) {
            if (-not $svcMap.ContainsKey($expSvc)) {
                Write-StackLog ("  [OFFLINE] {0,-32} -> Container missing from compose stack" -f "Container:$expSvc") "WARN"
                $offlineCount++
                continue
            }
            $cObj = $svcMap[$expSvc]
            $cState = if ($null -ne $cObj.State) { "$($cObj.State)".Trim().ToLowerInvariant() } else { "unknown" }
            $cHealth = if ($null -ne $cObj.Health) { "$($cObj.Health)".Trim().ToLowerInvariant() } else { "" }
            if ($cState -ne "running" -or $cHealth -eq "unhealthy") {
                Write-StackLog ("  [OFFLINE] Container '{0}' state='{1}' health='{2}'" -f $expSvc, $cState, $cHealth) "WARN"
                $offlineCount++
            } else {
                $healthLabel = if ($cHealth) { "$cState ($cHealth)" } else { $cState }
                Write-StackLog ("  [READY]   {0,-32} -> {1}" -f "Container:$expSvc", $healthLabel) "SUCCESS"
            }
        }
    } else {
        $offlineCount++
    }

    if (Test-TcpEndpointReady -Port $garnetPort) {
        Write-StackLog ("  [READY]   {0,-32} -> TCP 127.0.0.1:{1}" -f "Garnet Cache (TCP)", $garnetPort) "SUCCESS"
    } else {
        Write-StackLog ("  [OFFLINE] {0,-32} -> Unreachable (127.0.0.1:{1})" -f "Garnet Cache (TCP)", $garnetPort) "WARN"
        $offlineCount++
    }

    if ($expectLocalPostgres) {
        if (Test-TcpEndpointReady -Port $pgPort) {
            Write-StackLog ("  [READY]   {0,-32} -> TCP 127.0.0.1:{1}" -f "Local PostgreSQL 17 (TCP)", $pgPort) "SUCCESS"
        } else {
            Write-StackLog ("  [OFFLINE] {0,-32} -> Unreachable (127.0.0.1:{1})" -f "Local PostgreSQL 17 (TCP)", $pgPort) "WARN"
            $offlineCount++
        }
    }

    foreach ($ep in @(
        @{ Name = "Aspire Dashboard (HTTPS)"; Port = 5550; Path = "/login"; Accepted = @(200, 302); Url = "https://localhost:5550" },
        @{ Name = "WebAPI (HTTPS)";           Port = 5201; Path = "/health/ready"; Accepted = @(200); Url = "https://localhost:5201/health/ready" },
        @{ Name = "WebUI (HTTPS)";            Port = 5557; Path = "/health/ready"; Accepted = @(200); Url = "https://localhost:5557/health/ready" }
    )) {
        $tlsProbe = Test-HttpsEndpointReady -Port $ep.Port -Path $ep.Path -ExpectedThumbprint $effectiveThumb -AcceptedStatusCodes $ep.Accepted
        if ($tlsProbe.Ready) {
            Write-StackLog ("  [READY]   {0,-32} -> HTTP {1} ({2})" -f $ep.Name, $tlsProbe.StatusCode, $ep.Url) "SUCCESS"
        } else {
            Write-StackLog ("  [OFFLINE] {0,-32} -> Unreachable ({1})" -f $ep.Name, $ep.Url) "WARN"
            $offlineCount++
        }
    }

    foreach ($ep in @(
        @{ Name = "MasterServer"; Url = "http://127.0.0.1:55555/health/ready" },
        @{ Name = "ChatServer";   Url = "http://127.0.0.1:5554/health/ready" },
        @{ Name = "WebAPI (HTTP)"; Url = "http://127.0.0.1:5200/health/ready" },
        @{ Name = "WebUI (HTTP)";  Url = "http://127.0.0.1:5266/health/ready" }
    )) {
        try {
            $resp = Invoke-WebRequest -Uri $ep.Url -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            Write-StackLog ("  [READY]   {0,-32} -> HTTP {1} ({2})" -f $ep.Name, [int]$resp.StatusCode, $ep.Url) "SUCCESS"
        } catch {
            Write-StackLog ("  [OFFLINE] {0,-32} -> Unreachable ({1})" -f $ep.Name, $ep.Url) "WARN"
            $offlineCount++
        }
    }

    foreach ($cf in @(
        @{ Name = "COMPEL Match Fleet (Tier D)";  Port = 11334 },
        @{ Name = "COMPEL Queue Arcade (Tier Q)"; Port = 11484 }
    )) {
        $baseUrl = "http://127.0.0.1:$($cf.Port)"
        if (-not [string]::IsNullOrWhiteSpace($rtCompelToken)) {
            try {
                $statusObj = Invoke-RestMethod -Uri "$baseUrl/status" -Headers @{ Authorization = "Bearer $rtCompelToken" } -TimeoutSec 3 -ErrorAction Stop
                $activeWorkers = 0
                if ($null -ne $statusObj.activeWorkers) { $activeWorkers = [int]$statusObj.activeWorkers }
                elseif ($null -ne $statusObj.ActiveWorkers) { $activeWorkers = [int]$statusObj.ActiveWorkers }
                elseif ($null -ne $statusObj.activeWorkerCount) { $activeWorkers = [int]$statusObj.activeWorkerCount }
                elseif ($null -ne $statusObj.ActiveWorkerCount) { $activeWorkers = [int]$statusObj.ActiveWorkerCount }
                elseif ($null -ne $statusObj.workers) { $activeWorkers = @($statusObj.workers | Where-Object { $_.running -eq $true }).Count }
                $mgrRunning = ($statusObj.managerRunning -eq $true) -or ($statusObj.ManagerRunning -eq $true)
                if ($mgrRunning -and $activeWorkers -ge 1) {
                    Write-StackLog ("  [READY]   {0,-32} -> HTTP 200 (managerRunning={1}, activeWorkers={2})" -f $cf.Name, $mgrRunning, $activeWorkers) "SUCCESS"
                } else {
                    Write-StackLog ("  [OFFLINE] {0,-32} -> Not ready (managerRunning={1}, activeWorkers={2})" -f $cf.Name, $mgrRunning, $activeWorkers) "WARN"
                    $offlineCount++
                }
            } catch {
                Write-StackLog ("  [OFFLINE] {0,-32} -> Unreachable or unauthorized ({1}/status): {2}" -f $cf.Name, $baseUrl, $_.Exception.Message) "WARN"
                $offlineCount++
            }
        } else {
            try {
                $resp = Invoke-WebRequest -Uri "$baseUrl/ping" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
                Write-StackLog ("  [READY]   {0,-32} -> HTTP {1} ({2}/ping)" -f $cf.Name, [int]$resp.StatusCode, $baseUrl) "SUCCESS"
            } catch {
                Write-StackLog ("  [OFFLINE] {0,-32} -> Unreachable ({1}/ping)" -f $cf.Name, $baseUrl) "WARN"
                $offlineCount++
            }
        }
    }

    Write-StackLog "Status probe summary: composePsExit=$psExit, offlineCount=$offlineCount, strict=$($Strict.IsPresent)" "DEBUG"
    if ($psExit -ne 0) {
        return [int]$psExit
    }
    if ($Strict -and $offlineCount -gt 0) {
        return [int]1
    }
    return [int]0
}

if ($NormalizedAction -eq "status") {
    $statusRaw = @(Invoke-DockerStackStatusProbe)
    $statusExit = [int]$statusRaw[-1]
    exit $statusExit
}

if ($NormalizedAction -eq "init-secrets") {
    Write-StackLog "Inspecting secret stores for missing or dev-placeholder internal keys (Vault='$EffectiveSecretsPath', DotEnv='$EffectiveDotEnvPath')..." "INFO"

    $initStores = Read-ConfigStores
    $secretSpecs = @(
        @{
            Name            = "distributed-cache-password"
            ReadKeys        = @("distributed-cache-password", "REDIS_PASSWORD", "DISTRIBUTED_CACHE_PASSWORD", "Parameters:distributed-cache-password")
            VaultWriteKeys  = @("distributed-cache-password", "Parameters:distributed-cache-password")
            EnvMatchKeys    = @("REDIS_PASSWORD", "DISTRIBUTED_CACHE_PASSWORD", "distributed-cache-password", "Parameters__distributed-cache-password", "Parameters__distributed_cache_password")
            EnvEmitKeys     = @("REDIS_PASSWORD")
            ExtraDisallowed = @("DistributedCachePassword123!")
            MinUtf8Bytes    = 16
        },
        @{
            Name            = "jwt-signing-key"
            ReadKeys        = @("jwt-signing-key", "Operational:JWT:SigningKey", "OperationalConfiguration:JWT:SigningKey", "Parameters:jwt-signing-key", "JWT_SIGNING_KEY")
            VaultWriteKeys  = @("jwt-signing-key", "Parameters:jwt-signing-key", "Operational:JWT:SigningKey")
            EnvMatchKeys    = @("Operational__JWT__SigningKey", "JWT_SIGNING_KEY", "Parameters__jwt-signing-key", "Parameters__jwt_signing_key", "jwt-signing-key")
            EnvEmitKeys     = @("Operational__JWT__SigningKey")
            ExtraDisallowed = @("NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!")
            MinUtf8Bytes    = 32
        },
        @{
            Name            = "InternalServiceKey"
            ReadKeys        = @("InternalServiceKey", "INTERNAL_SERVICE_KEY", "NEXUS_INTERNAL_KEY")
            VaultWriteKeys  = @("InternalServiceKey", "INTERNAL_SERVICE_KEY")
            EnvMatchKeys    = @("InternalServiceKey", "INTERNAL_SERVICE_KEY", "NEXUS_INTERNAL_KEY")
            EnvEmitKeys     = @("InternalServiceKey", "INTERNAL_SERVICE_KEY")
            ExtraDisallowed = @("NEXUS-Internal-Service-Key-Dev-2026")
            MinUtf8Bytes    = 16
        }
    )

    if ($LocalPostgres) {
        $secretSpecs += @{
            Name            = "database-password"
            ReadKeys        = @("database-password", "POSTGRES_PASSWORD", "DATABASE_PASSWORD", "Parameters:database-password")
            VaultWriteKeys  = @("database-password", "Parameters:database-password")
            EnvMatchKeys    = @("POSTGRES_PASSWORD", "DATABASE_PASSWORD", "database-password", "Parameters__database-password", "Parameters__database_password")
            EnvEmitKeys     = @("POSTGRES_PASSWORD")
            ExtraDisallowed = @("MerrickDatabasePassword123!")
            MinUtf8Bytes    = 16
        }
    }

    # Load ordered JSON properties from secrets.json so existing formatting/key order is preserved
    $orderedVault = [ordered]@{}
    if (Test-Path -LiteralPath $EffectiveSecretsPath) {
        $rawVaultJson = Get-Content -LiteralPath $EffectiveSecretsPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($rawVaultJson)) {
            $parsedVaultObj = $rawVaultJson | ConvertFrom-Json
            foreach ($prop in $parsedVaultObj.PSObject.Properties) {
                $orderedVault[$prop.Name] = $prop.Value
            }
        }
    }

    $dotEnvExists = Test-Path -LiteralPath $EffectiveDotEnvPath
    $dotEnvLines = if ($dotEnvExists) { @(Get-Content -LiteralPath $EffectiveDotEnvPath -Encoding UTF8) } else { @() }

    $vaultDirty = $false
    $dotEnvDirty = $false
    $rotatedKeys = New-Object System.Collections.Generic.List[string]
    $syncedKeys = New-Object System.Collections.Generic.List[string]
    $unchangedKeys = New-Object System.Collections.Generic.List[string]

    foreach ($spec in $secretSpecs) {
        $res = Resolve-ConfigSetting -Stores $initStores -Keys $spec.ReadKeys -ExtraDisallowed $spec.ExtraDisallowed
        $byteLen = if ($res.Found) { [System.Text.Encoding]::UTF8.GetByteCount($res.Value) } else { 0 }
        $hasValidExisting = $res.Found -and ($byteLen -ge $spec.MinUtf8Bytes) -and (-not (Test-IsPlaceholderValue -Value $res.Value -ExtraDisallowed $spec.ExtraDisallowed))

        $effectiveValue = if ($hasValidExisting) {
            $res.Value
        } else {
            New-CsprngAlphanumericSecret -Length 48
        }

        $specChanged = $false
        foreach ($vk in $spec.VaultWriteKeys) {
            $currentVal = if ($orderedVault.Contains($vk) -and $null -ne $orderedVault[$vk]) { [string]$orderedVault[$vk] } else { "" }
            if (-not [string]::Equals($currentVal, $effectiveValue, [System.StringComparison]::Ordinal)) {
                $orderedVault[$vk] = $effectiveValue
                $vaultDirty = $true
                $specChanged = $true
            }
        }

        if ($dotEnvExists) {
            $emittedInFile = @{}
            for ($li = 0; $li -lt $dotEnvLines.Count; $li++) {
                $trimmedLine = $dotEnvLines[$li].Trim()
                if ([string]::IsNullOrWhiteSpace($trimmedLine) -or $trimmedLine.StartsWith("#")) { continue }
                $eqPos = $trimmedLine.IndexOf('=')
                if ($eqPos -le 0) { continue }
                $lineKey = $trimmedLine.Substring(0, $eqPos).Trim()
                if ($lineKey -in $spec.EnvMatchKeys) {
                    $newLine = "${lineKey}=${effectiveValue}"
                    if (-not [string]::Equals($dotEnvLines[$li], $newLine, [System.StringComparison]::Ordinal)) {
                        $dotEnvLines[$li] = $newLine
                        $dotEnvDirty = $true
                        $specChanged = $true
                    }
                    $emittedInFile[$lineKey] = $true
                }
            }
            foreach ($emitKey in $spec.EnvEmitKeys) {
                if (-not $emittedInFile.ContainsKey($emitKey)) {
                    $dotEnvLines += "${emitKey}=${effectiveValue}"
                    $dotEnvDirty = $true
                    $specChanged = $true
                }
            }
        }

        if (-not $hasValidExisting) {
            $rotatedKeys.Add($spec.Name)
            Write-StackLog "[INIT-SECRETS] Generated fresh CSPRNG secret for '$($spec.Name)' (aliases: $($spec.VaultWriteKeys -join ', '))." "SUCCESS"
        } elseif ($specChanged) {
            $syncedKeys.Add($spec.Name)
            Write-StackLog "[INIT-SECRETS] Synchronized existing custom value across aliases for '$($spec.Name)' (preserved existing secret from $($res.Source))." "INFO"
        } else {
            $unchangedKeys.Add($spec.Name)
            Write-StackLog "[INIT-SECRETS] Preserved existing non-placeholder secret for '$($spec.Name)' (no changes needed)." "DEBUG"
        }
    }

    $backupStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
    if ($vaultDirty) {
        $vaultDir = Split-Path -Parent $EffectiveSecretsPath
        if (-not [string]::IsNullOrWhiteSpace($vaultDir) -and -not (Test-Path -LiteralPath $vaultDir)) {
            New-Item -ItemType Directory -Path $vaultDir -Force | Out-Null
        }
        if (Test-Path -LiteralPath $EffectiveSecretsPath) {
            $vaultBackup = "$EffectiveSecretsPath.bak.$backupStamp"
            Copy-Item -LiteralPath $EffectiveSecretsPath -Destination $vaultBackup -Force
            Write-StackLog "[INIT-SECRETS] Created vault backup at '$vaultBackup'." "INFO"
        }
        ($orderedVault | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $EffectiveSecretsPath -Encoding UTF8
        Write-StackLog "[INIT-SECRETS] Updated User Secrets store at '$EffectiveSecretsPath'." "SUCCESS"
    }

    if ($dotEnvDirty -and $dotEnvExists) {
        $envBackup = "$EffectiveDotEnvPath.bak.$backupStamp"
        Copy-Item -LiteralPath $EffectiveDotEnvPath -Destination $envBackup -Force
        Write-StackLog "[INIT-SECRETS] Created .env backup at '$envBackup'." "INFO"
        $dotEnvLines | Set-Content -LiteralPath $EffectiveDotEnvPath -Encoding UTF8
        Write-StackLog "[INIT-SECRETS] Synchronized .env store at '$EffectiveDotEnvPath'." "SUCCESS"
    }

    Write-StackLog "[INIT-SECRETS] Complete -> rotated=$($rotatedKeys.Count) [$($rotatedKeys -join ', ')], synced=$($syncedKeys.Count) [$($syncedKeys -join ', ')], preserved=$($unchangedKeys.Count) [$($unchangedKeys -join ', ')]." "SUCCESS"
    exit 0
}

# ==============================================================================
# PHASE 1: STRICT PRE-FLIGHT SECRETS, CONFIG & RUNTIME GATE (for 'check' & 'up')
# ==============================================================================
Write-StackLog "Running strict pre-flight configuration & secrets gate..." "INFO"

$Stores = Read-ConfigStores
$MissingItems = New-Object System.Collections.Generic.List[object]
$Warnings = New-Object System.Collections.Generic.List[string]

function Add-MissingRequirement {
    param(
        [string]$Category,
        [string]$Key,
        [string]$Reason,
        [string]$Remediation
    )
    $MissingItems.Add([PSCustomObject]@{
        Category    = $Category
        Key         = $Key
        Reason      = $Reason
        Remediation = $Remediation
    })
    Write-StackLog "[PREFLIGHT-FAIL] [$Category] $Key -> $Reason | Fix: $Remediation" "WARN"
}

# 1. Database Provider & Connection Secrets
$dbProviderRes = Resolve-ConfigSetting -Stores $Stores -Keys @("Database:Provider", "DATABASE_PROVIDER", "DatabaseProvider")
$effectiveDbProvider = if ($LocalPostgres) {
    "PostgreSQL"
} elseif ($dbProviderRes.Found) {
    $dbProviderRes.Value
} else {
    "Supabase"
}

$replayProviderRes = Resolve-ConfigSetting -Stores $Stores -Keys @("ReplayStorage:Provider", "REPLAY_STORAGE_PROVIDER")
$effectiveReplayProvider = if ($replayProviderRes.Found) { $replayProviderRes.Value } else { "LocalDisk" }

$localPgPassRes = Resolve-ConfigSetting -Stores $Stores -Keys @("database-password", "POSTGRES_PASSWORD", "Parameters:database-password") -ExtraDisallowed @("MerrickDatabasePassword123!")
$dbConnRes = Resolve-ConfigSetting -Stores $Stores -Keys @(
    "Supabase:DatabaseConnectionString",
    "SUPABASE_DB_CONNECTION_STRING",
    "Supabase:Database:ConnectionString",
    "Database:PostgreSQL:ConnectionString",
    "DATABASE_POSTGRESQL_CONNECTION_STRING",
    "ConnectionStrings:MERRICK",
    "ConnectionStrings:PostgreSQL",
    "ConnectionStrings:Supabase"
)

$containerDbConnString = ""
if ($LocalPostgres) {
    if (-not $localPgPassRes.Found -or (Test-IsPlaceholderValue -Value $localPgPassRes.Value -ExtraDisallowed @("MerrickDatabasePassword123!"))) {
        Add-MissingRequirement `
            -Category "Database (-LocalPostgres)" `
            -Key "database-password" `
            -Reason "Missing or set to built-in development default ('MerrickDatabasePassword123!')." `
            -Remediation '.\kongor.ps1 secret set "database-password" "<strong-local-db-password>"'
    } else {
        $containerDbConnString = "Host=postgres;Port=5432;Database=merrick;Username=merrick;Password=$($localPgPassRes.Value)"
        Write-StackLog "[PREFLIGHT-OK] Local Postgres password resolved from $($localPgPassRes.Source)." "DEBUG"
    }
} else {
    if (-not $dbConnRes.Found -or (Test-IsPlaceholderValue -Value $dbConnRes.Value)) {
        Add-MissingRequirement `
            -Category "Database ($effectiveDbProvider)" `
            -Key "Supabase:DatabaseConnectionString" `
            -Reason "No database connection string configured (or value is a placeholder)." `
            -Remediation '.\kongor.ps1 secret set "Supabase:DatabaseConnectionString" "Host=<host>;Port=5432;Database=postgres;Username=postgres;Password=<pass>" (or pass -LocalPostgres)'
    } else {
        $containerDbConnString = Convert-LoopbackConnectionStringForDocker -ConnStr $dbConnRes.Value
        Write-StackLog "[PREFLIGHT-OK] Database connection string resolved from $($dbConnRes.Source) (Provider=$effectiveDbProvider)." "DEBUG"
    }
}

$supabaseUrlRes = Resolve-ConfigSetting -Stores $Stores -Keys @("Supabase:Url", "SUPABASE_URL", "ReplayStorage:Supabase:ProjectUrl", "SupabaseUrl")
$supabaseAnonRes = Resolve-ConfigSetting -Stores $Stores -Keys @("Supabase:AnonKey", "SUPABASE_ANON_KEY", "ReplayStorage:Supabase:AnonKey", "SupabaseAnonKey")
$supabaseServiceRes = Resolve-ConfigSetting -Stores $Stores -Keys @("Supabase:ServiceRoleKey", "SUPABASE_SERVICE_ROLE_KEY", "ReplayStorage:Supabase:ServiceRoleKey", "SupabaseServiceRoleKey")

$needsSupabaseKeys = (-not $LocalPostgres) -and (
    [string]::Equals($effectiveDbProvider, "Supabase", [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($effectiveReplayProvider, "Supabase", [System.StringComparison]::OrdinalIgnoreCase)
)
if ($needsSupabaseKeys) {
    if (-not $supabaseUrlRes.Found -or (Test-IsPlaceholderValue -Value $supabaseUrlRes.Value)) {
        Add-MissingRequirement `
            -Category "Supabase" `
            -Key "Supabase:Url" `
            -Reason "Required when Database:Provider or ReplayStorage:Provider is 'Supabase'." `
            -Remediation '.\kongor.ps1 secret set "Supabase:Url" "https://<project-ref>.supabase.co"'
    }
    if (-not $supabaseAnonRes.Found -or (Test-IsPlaceholderValue -Value $supabaseAnonRes.Value)) {
        Add-MissingRequirement `
            -Category "Supabase" `
            -Key "Supabase:AnonKey" `
            -Reason "Required when Database:Provider or ReplayStorage:Provider is 'Supabase'." `
            -Remediation '.\kongor.ps1 secret set "Supabase:AnonKey" "<supabase-anon-key>"'
    }
    if (-not $supabaseServiceRes.Found -or (Test-IsPlaceholderValue -Value $supabaseServiceRes.Value)) {
        Add-MissingRequirement `
            -Category "Supabase" `
            -Key "Supabase:ServiceRoleKey" `
            -Reason "Required when Database:Provider or ReplayStorage:Provider is 'Supabase'." `
            -Remediation '.\kongor.ps1 secret set "Supabase:ServiceRoleKey" "<supabase-service-role-key>"'
    }
}

# 2. Distributed Cache Password
$cachePassRes = Resolve-ConfigSetting -Stores $Stores -Keys @("distributed-cache-password", "REDIS_PASSWORD", "Parameters:distributed-cache-password") -ExtraDisallowed @("DistributedCachePassword123!")
if (-not $cachePassRes.Found -or (Test-IsPlaceholderValue -Value $cachePassRes.Value -ExtraDisallowed @("DistributedCachePassword123!"))) {
    Add-MissingRequirement `
        -Category "Distributed Cache" `
        -Key "distributed-cache-password" `
        -Reason "Missing or set to built-in development default ('DistributedCachePassword123!')." `
        -Remediation '.\kongor.ps1 secret set "distributed-cache-password" "<strong-cache-password>"'
} else {
    Write-StackLog "[PREFLIGHT-OK] Distributed cache password resolved from $($cachePassRes.Source)." "DEBUG"
}

# 3. Security Keys (JWT Signing Key >= 32 UTF-8 bytes & InternalServiceKey)
$defaultJwtPhrase = "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!"
$jwtKeyRes = Resolve-ConfigSetting -Stores $Stores -Keys @(
    "jwt-signing-key",
    "Operational:JWT:SigningKey",
    "OperationalConfiguration:JWT:SigningKey",
    "Parameters:jwt-signing-key"
) -ExtraDisallowed @($defaultJwtPhrase)
$jwtByteCount = if ($jwtKeyRes.Found) { [System.Text.Encoding]::UTF8.GetByteCount($jwtKeyRes.Value) } else { 0 }
if (-not $jwtKeyRes.Found -or $jwtByteCount -lt 32 -or (Test-IsPlaceholderValue -Value $jwtKeyRes.Value -ExtraDisallowed @($defaultJwtPhrase))) {
    Add-MissingRequirement `
        -Category "Security" `
        -Key "jwt-signing-key" `
        -Reason "Missing, shorter than 32 UTF-8 bytes ($jwtByteCount bytes), or set to public dev phrase." `
        -Remediation '.\kongor.ps1 secret set "jwt-signing-key" "<unique-32-plus-byte-signing-secret>"'
} else {
    Write-StackLog "[PREFLIGHT-OK] JWT signing key resolved from $($jwtKeyRes.Source) ($jwtByteCount bytes)." "DEBUG"
}

$internalKeyRes = Resolve-ConfigSetting -Stores $Stores -Keys @("InternalServiceKey", "INTERNAL_SERVICE_KEY") -ExtraDisallowed @("NEXUS-Internal-Service-Key-Dev-2026")
if (-not $internalKeyRes.Found -or (Test-IsPlaceholderValue -Value $internalKeyRes.Value -ExtraDisallowed @("NEXUS-Internal-Service-Key-Dev-2026"))) {
    Add-MissingRequirement `
        -Category "Security" `
        -Key "InternalServiceKey" `
        -Reason "Missing or set to built-in dev fallback ('NEXUS-Internal-Service-Key-Dev-2026')." `
        -Remediation '.\kongor.ps1 secret set "InternalServiceKey" "<unique-internal-service-secret>"'
} else {
    Write-StackLog "[PREFLIGHT-OK] InternalServiceKey resolved from $($internalKeyRes.Source)." "DEBUG"
}

# 4. COMPEL Control Plane Token & Host Account Credentials
$compelTokenRes = Resolve-ConfigSetting -Stores $Stores -Keys @(
    "Compel:AuthenticationToken",
    "COMPEL_AUTHENTICATION_TOKEN",
    "Compel:ControlPlaneAuthenticationToken",
    "COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN"
)
if (-not $compelTokenRes.Found -or (Test-IsPlaceholderValue -Value $compelTokenRes.Value)) {
    Add-MissingRequirement `
        -Category "COMPEL Fleet" `
        -Key "Compel:AuthenticationToken" `
        -Reason "Missing or placeholder COMPEL HTTP control plane bearer token." `
        -Remediation '.\kongor.ps1 secret set "Compel:AuthenticationToken" "<random-bearer-token>"'
} else {
    Write-StackLog "[PREFLIGHT-OK] COMPEL authentication token resolved from $($compelTokenRes.Source)." "DEBUG"
}

$hostUserRes = Resolve-ConfigSetting -Stores $Stores -Keys @(
    "HostAccount:UserName",
    "HOST_USERNAME",
    "Compel:UserName",
    "MatchServer:UserName",
    "Host:UserName",
    "Parameters:host-username"
)
$defaultHostUserPrefix = Get-DefaultHostUserPrefix -Stores $Stores
if (-not $hostUserRes.Found -or (Test-IsPlaceholderValue -Value $hostUserRes.Value)) {
    Add-MissingRequirement `
        -Category "COMPEL Host Account" `
        -Key "HostAccount:UserName" `
        -Reason "Missing or placeholder ServerHost account username." `
        -Remediation ".\kongor.ps1 secret set `"HostAccount:UserName`" `"$defaultHostUserPrefix`""
} else {
    Write-StackLog "[PREFLIGHT-OK] Host account username resolved from $($hostUserRes.Source) ('$($hostUserRes.Value)')." "DEBUG"
}

$hostPassRes = Resolve-ConfigSetting -Stores $Stores -Keys @(
    "HostAccount:Password",
    "HOST_PASSWORD",
    "Compel:Password",
    "MatchServer:Password",
    "Host:Password",
    "Parameters:host-password"
)
if (-not $hostPassRes.Found -or (Test-IsPlaceholderValue -Value $hostPassRes.Value)) {
    Add-MissingRequirement `
        -Category "COMPEL Host Account" `
        -Key "HostAccount:Password" `
        -Reason "Missing or placeholder ServerHost account password." `
        -Remediation '.\kongor.ps1 secret set "HostAccount:Password" "<host-account-password>"'
} else {
    Write-StackLog "[PREFLIGHT-OK] Host account password resolved from $($hostPassRes.Source)." "DEBUG"
}

# 5. Optional Non-Blocking Integrations (Warn Only)
foreach ($opt in @(
    @{ Name = "Discord OAuth"; Keys = @("Authentication:Discord:ClientId", "DISCORD_CLIENT_ID") },
    @{ Name = "Twitch OAuth";  Keys = @("Authentication:Twitch:ClientId", "TWITCH_CLIENT_ID") },
    @{ Name = "Supabase S3 Lakehouse"; Keys = @("Supabase:S3AccessKey", "SUPABASE_S3_ACCESS_KEY") },
    @{ Name = "Grafana OTLP";  Keys = @("OTEL_EXPORTER_OTLP_ENDPOINT", "GRAFANA_OTLP_ENDPOINT") }
)) {
    $optRes = Resolve-ConfigSetting -Stores $Stores -Keys $opt.Keys
    if (-not $optRes.Found) {
        $Warnings.Add("Optional integration '$($opt.Name)' ($($opt.Keys[0])) is not configured; related non-core features will stay disabled.")
    }
}

# Resolve Game Client Path if -WithClient is requested
$clientDirRes = Resolve-ConfigSetting -Stores $Stores -Keys @("GameClient:Path", "GameClient:GamePath", "HON_GAME_PATH")
$resolvedClientDir = if ($clientDirRes.Found -and (Test-Path -LiteralPath $clientDirRes.Value)) {
    $clientDirRes.Value
} else {
    Join-Path $RepoRoot "games\client"
}
$resolvedClientExe = if (Test-Path -LiteralPath $resolvedClientDir -PathType Leaf) {
    $resolvedClientDir
} else {
    Join-Path $resolvedClientDir "hon_x64.exe"
}

# 6. Runtime, Binary & Port Conflict Checks (unless explicitly skipped by isolated unit tests)
if (-not $SkipRuntimeChecks) {
    $dockerDaemonReady = $false
    $dockerCmd = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $dockerCmd) {
        Add-MissingRequirement `
            -Category "Docker Runtime" `
            -Key "docker" `
            -Reason "Docker CLI was not found on PATH." `
            -Remediation "Install Docker Desktop for Windows and ensure 'docker' is on PATH."
    } else {
        $prevRuntimeEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $dockerOsType = (& docker info --format "{{.OSType}}" 2>$null)
            $dockerInfoExit = $LASTEXITCODE
            & docker compose version *> $null
            $dockerComposeExit = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $prevRuntimeEap
        }
        if ($dockerInfoExit -ne 0 -or [string]::IsNullOrWhiteSpace("$dockerOsType")) {
            Add-MissingRequirement `
                -Category "Docker Runtime" `
                -Key "docker daemon" `
                -Reason "Docker daemon is not running or unreachable." `
                -Remediation 'Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"'
        } elseif ("$dockerOsType".Trim().ToLowerInvariant() -ne "linux") {
            Add-MissingRequirement `
                -Category "Docker Runtime" `
                -Key "docker engine mode" `
                -Reason "Docker Desktop is currently in '$dockerOsType' container mode (Linux containers required)." `
                -Remediation 'Switch Docker Desktop to Linux containers: & "C:\Program Files\Docker\Docker\DockerCli.exe" -SwitchLinuxEngine'
        } else {
            Write-StackLog "[PREFLIGHT-OK] Docker Linux daemon is online." "DEBUG"
            $dockerDaemonReady = $true

            $prevMemEap = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            try {
                $dockerMemRaw = (& docker info --format "{{.MemTotal}}" 2>$null)
                $dockerMemExit = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $prevMemEap
            }
            $dockerMemBytes = [int64]0
            if ($dockerMemExit -eq 0 -and [int64]::TryParse("$dockerMemRaw".Trim(), [ref]$dockerMemBytes) -and $dockerMemBytes -gt 0) {
                $dockerMemGiB = [Math]::Round($dockerMemBytes / 1GB, 2)
                $plannedMatchWorkers = [int]$ActivePreset.MatchInstances
                $plannedArcadeWorkers = [int]$ActivePreset.ArcadeInstances
                $totalGameWorkers = $plannedMatchWorkers + $plannedArcadeWorkers
                Write-StackLog "[PREFLIGHT-OK] Docker Linux VM memory: $dockerMemGiB GiB (Preset='$PerfKey', MatchWorkers=$plannedMatchWorkers, ArcadeWorkers=$plannedArcadeWorkers)." "DEBUG"
                if ($dockerMemGiB -lt 5.5 -and $totalGameWorkers -ge 3) {
                    Write-StackLog "[MEM-BUDGET] Docker VM has only $dockerMemGiB GiB RAM for $totalGameWorkers HoN server workers + core services (risk of OOM Exit 137). Consider '-Performance Eco' or setting 'memory=8GB' in '$env:USERPROFILE\.wslconfig' (followed by 'wsl --shutdown')." "WARN"
                }
            }
        }

        if ($dockerComposeExit -ne 0) {
            Add-MissingRequirement `
                -Category "Docker Runtime" `
                -Key "docker compose" `
                -Reason "Docker Compose v2 plugin ('docker compose') is unavailable." `
                -Remediation "Update Docker Desktop so 'docker compose version' succeeds."
        }
    }

    $linuxServerBin = Join-Path $RepoRoot "games\server\linux-x64\hon-x86_64-server"
    $serverArchive0 = Join-Path $RepoRoot "games\server\game\resources0.s2z"
    if (-not (Test-Path -LiteralPath $linuxServerBin)) {
        Add-MissingRequirement `
            -Category "Server Binaries" `
            -Key "games/server/linux-x64/hon-x86_64-server" `
            -Reason "Linux x64 dedicated server binary is missing." `
            -Remediation "Ensure games/server/linux-x64/hon-x86_64-server exists in the workspace."
    }
    if (-not (Test-Path -LiteralPath $serverArchive0)) {
        Add-MissingRequirement `
            -Category "Server Assets" `
            -Key "games/server/game/resources0.s2z" `
            -Reason "Core server game archive resources0.s2z is missing." `
            -Remediation "Populate games/server/game/*.s2z archives before launching dedicated servers."
    }

    if ($WithClient) {
        if ($UseLauncher) {
            $launcherProj = Join-Path $RepoRoot "src\apps\willowmaker\WILLOWMAKER.csproj"
            if (-not (Test-Path -LiteralPath $launcherProj)) {
                Add-MissingRequirement `
                    -Category "Game Client" `
                    -Key "WILLOWMAKER.csproj" `
                    -Reason "WILLOWMAKER project was not found at '$launcherProj'." `
                    -Remediation "Verify repository checkout or omit -UseLauncher."
            }
        } elseif (-not (Test-Path -LiteralPath $resolvedClientExe)) {
            Add-MissingRequirement `
                -Category "Game Client (-WithClient)" `
                -Key "GameClient:Path" `
                -Reason "Native game client binary not found at '$resolvedClientExe'." `
                -Remediation 'Place hon_x64.exe in games\client\ or run: .\kongor.ps1 secret set "GameClient:Path" "C:\Path\To\Client"'
        }
    }

    # Check host TCP port collisions against non-Docker processes
    $portsToVerify = @(5550, 5551, 55555, 5554, 11031, 11032, 11033, 5200, 5201, 5266, 5557, 28298, 11334, 11484)
    if ($LocalPostgres) { $portsToVerify += 25432 }

    $conflictingProcs = @{}
    foreach ($port in $portsToVerify) {
        $listeners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        foreach ($listener in $listeners) {
            $ownerPid = [int]$listener.OwningProcess
            if ($ownerPid -le 4) { continue }
            $proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
            if (-not $proc) { continue }
            $pName = $proc.ProcessName
            # Docker Desktop port forwarders own ports when kongor-stack is already running
            if ($pName -match '^(com\.docker|vpnkit|wslrelay|dockerd)') { continue }
            $conflictingProcs["$port"] = @{ Pid = $ownerPid; Name = $pName; Port = $port }
        }
    }

    $isLaunchAction = ($NormalizedAction -in @("up", "rebuild", "sync"))
    if ($conflictingProcs.Count -gt 0 -or ($StopHostConflict -and $isLaunchAction)) {
        if ($StopHostConflict -and $isLaunchAction) {
            # Stop Aspire ApplicationHost & DCP orchestrators FIRST so they do not respawn child microservices while freeing ports
            $aspireOrphans = Get-Process -Name @("ASPIRE.ApplicationHost", "dcp", "dcpctrl", "dcp-host") -ErrorAction SilentlyContinue
            foreach ($orphan in $aspireOrphans) {
                if ($orphan.ProcessName -in @("NEXUS.ControlPanel.GUI", "WILLOWMAKER", "hon_x64")) { continue }
                Write-StackLog "Stopping host Aspire/DCP orchestrator $($orphan.ProcessName) (PID $($orphan.Id)) due to -StopHostConflict..." "WARN"
                try {
                    Stop-Process -Id $orphan.Id -Force -ErrorAction Stop
                } catch {
                    if (Get-Process -Id $orphan.Id -ErrorAction SilentlyContinue) { throw }
                }
            }

            $hostServerProcs = Get-Process -Name @("hon_server", "COMPEL", "KONGOR.MasterServer", "TRANSMUTANSTEIN.ChatServer", "ZORGATH.WebPortal.API", "DAWNBRINGER.WebPortal.UI") -ErrorAction SilentlyContinue
            foreach ($srvProc in $hostServerProcs) {
                Write-StackLog "Stopping stray host server/service process $($srvProc.ProcessName) (PID $($srvProc.Id)) due to -StopHostConflict..." "WARN"
                try {
                    Stop-Process -Id $srvProc.Id -Force -ErrorAction Stop
                } catch {
                    if (Get-Process -Id $srvProc.Id -ErrorAction SilentlyContinue) { throw }
                }
            }

            $uniquePids = $conflictingProcs.Values | ForEach-Object { $_.Pid } | Select-Object -Unique
            foreach ($conflictPid in $uniquePids) {
                $cProc = Get-Process -Id $conflictPid -ErrorAction SilentlyContinue
                if ($cProc) {
                    if ($cProc.ProcessName -in @("NEXUS.ControlPanel.GUI", "WILLOWMAKER", "hon_x64")) { continue }
                    Write-StackLog "Stopping conflicting host process $($cProc.ProcessName) (PID $conflictPid) due to -StopHostConflict..." "WARN"
                    try {
                        Stop-Process -Id $conflictPid -Force -ErrorAction Stop
                    } catch {
                        if (Get-Process -Id $conflictPid -ErrorAction SilentlyContinue) { throw }
                    }
                }
            }
            if ($aspireOrphans -or $hostServerProcs -or $uniquePids) {
                Start-Sleep -Seconds 1
            }
        } elseif ($conflictingProcs.Count -gt 0) {
            $summaryPairs = ($conflictingProcs.Values | ForEach-Object { "port $($_.Port) -> $($_.Name) (PID $($_.Pid))" }) -join "; "
            Add-MissingRequirement `
                -Category "Host Port Conflict" `
                -Key "TCP Ports" `
                -Reason "Host processes already bound to required Docker stack ports: $summaryPairs" `
                -Remediation "Run '.\kongor.ps1 stop' first, or re-run with '-StopHostConflict'."
        }
    }

    # Check non-stack Docker containers holding required host TCP ports (e.g. standalone kongor-garnet-cache on 28298)
    if ($dockerDaemonReady) {
        $conflictingContainers = @{}
        $prevPsEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $psLines = @(& docker ps --format "{{.Names}}|{{.Ports}}" 2>$null)
        $psExit = $LASTEXITCODE
        $ErrorActionPreference = $prevPsEap
        if ($psExit -eq 0) {
            foreach ($line in $psLines) {
                if ([string]::IsNullOrWhiteSpace($line) -or ($line -notmatch '\|')) { continue }
                $parts = $line -split '\|', 2
                $cName = $parts[0].Trim()
                $cPorts = $parts[1].Trim()
                if ([string]::IsNullOrWhiteSpace($cName) -or ($cName -like 'kongor-stack-*')) { continue }
                foreach ($port in $portsToVerify) {
                    if ($cPorts -match "(^|[\s,:])$port->") {
                        $conflictingContainers["${cName}:${port}"] = @{ Name = $cName; Port = $port }
                    }
                }
            }
        }

        if ($conflictingContainers.Count -gt 0) {
            $uniqueContainers = @($conflictingContainers.Values | ForEach-Object { $_.Name } | Select-Object -Unique)
            if ($StopHostConflict -and ($NormalizedAction -in @("up", "rebuild", "sync"))) {
                foreach ($cName in $uniqueContainers) {
                    Write-StackLog "Stopping conflicting non-stack Docker container '$cName' due to -StopHostConflict..." "WARN"
                    & docker stop $cName | Out-Host
                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to stop conflicting Docker container '$cName' (exit $LASTEXITCODE). Run 'docker stop $cName' manually and retry."
                    }
                }
            } else {
                $containerSummary = ($conflictingContainers.Values | ForEach-Object { "port $($_.Port) -> container '$($_.Name)'" }) -join "; "
                $stopCmd = "docker stop " + ($uniqueContainers -join " ")
                Add-MissingRequirement `
                    -Category "Docker Port Conflict" `
                    -Key "Docker Containers" `
                    -Reason "Non-stack Docker container(s) already bound to required Docker stack ports: $containerSummary" `
                    -Remediation "Run '$stopCmd' first, or re-run with '-StopHostConflict'."
            }
        }
    }
}

foreach ($w in $Warnings) {
    Write-StackLog "[OPTIONAL] $w" "DEBUG"
}

if ($MissingItems.Count -gt 0) {
    Write-Host ""
    Write-Host "================================================================================" -ForegroundColor Red
    Write-Host " PRE-FLIGHT CHECK FAILED: $($MissingItems.Count) REQUIRED ITEM(S) MISSING OR INVALID" -ForegroundColor Red
    Write-Host "================================================================================" -ForegroundColor Red
    Write-Host "Configure the items below (or open '.\kongor.ps1 gui' -> Secrets & Configs) and re-run:" -ForegroundColor Yellow
    Write-Host ""
    $idx = 1
    foreach ($item in $MissingItems) {
        Write-Host ("  [{0}] {1} :: {2}" -f $idx, $item.Category, $item.Key) -ForegroundColor Red
        Write-Host ("      Why: {0}" -f $item.Reason) -ForegroundColor Gray
        Write-Host ("      Fix: {0}" -f $item.Remediation) -ForegroundColor Cyan
        Write-Host ""
        $idx++
    }
    Write-Host "Verify anytime without starting containers via: .\kongor.ps1 docker check" -ForegroundColor Yellow
    Write-Host "================================================================================" -ForegroundColor Red
    Write-StackLog "Aborting '$NormalizedAction' because $($MissingItems.Count) required pre-flight check(s) failed." "ERROR"
    exit 1
}

# Derive Match & Queue Arcade host usernames + source stamp + runtime .env.stack
$matchHostUser = Get-MatchHostUsername -BaseUsername $hostUserRes.Value -DefaultPrefix $defaultHostUserPrefix
$arcadeHostUser = Get-DerivedArcadeHostUsername -BaseUsername $hostUserRes.Value -DefaultPrefix $defaultHostUserPrefix
$compelLocationRes = Resolve-ConfigSetting -Stores $Stores -Keys @("Compel:Location", "COMPEL_LOCATION", "Location")
$effectiveLocation = if ($compelLocationRes.Found) { $compelLocationRes.Value } else { "USE" }
$matchPrefixRes = Resolve-ConfigSetting -Stores $Stores -Keys @("Compel:ServerNamePrefix", "COMPEL_SERVER_NAME_PREFIX")
$effectiveMatchPrefix = if ($matchPrefixRes.Found) { $matchPrefixRes.Value } else { "^c[DOCKER] ^oKONGOR ARENA ^g" }

# Compute git commit + dirty status hash for build staleness tracking
$sourceStamp = "local"
try {
    $gitHead = (& git -C $RepoRoot rev-parse --short=12 HEAD 2>$null)
    $gitStatus = (& git -C $RepoRoot status --porcelain -- src/services src/libs src/apps/compel src/extensions native/proxies/hon_ping_shim infra/docker/Dockerfile.service infra/docker/Dockerfile.dedicated infra/docker/entrypoint.sh native Directory.Build.props Directory.Packages.props 2>$null) -join "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $dirtyBytes = [System.Text.Encoding]::UTF8.GetBytes("$gitHead|$gitStatus")
    $dirtyHex = ([System.BitConverter]::ToString($sha.ComputeHash($dirtyBytes)).Replace("-", "")).Substring(0, 12).ToLowerInvariant()
    $sha.Dispose()
    $sourceStamp = "$($gitHead.Trim())-$dirtyHex"
} catch {
    Write-StackLog "Could not compute git source stamp ($($_.Exception.Message)); falling back to 'local'." "DEBUG"
}

$runtimeEnvDir = Split-Path -Parent $EffectiveRuntimeEnvPath
if (-not (Test-Path -LiteralPath $runtimeEnvDir)) {
    New-Item -ItemType Directory -Path $runtimeEnvDir -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $RuntimeDockerDir)) {
    New-Item -ItemType Directory -Path $RuntimeDockerDir -Force | Out-Null
}

# Ensure read-only User Secrets mount always points to a valid JSON file (never an auto-created directory)
$secretsMountPath = $EffectiveSecretsPath
if (-not (Test-Path -LiteralPath $secretsMountPath)) {
    Set-Content -LiteralPath $EmptySecretsStub -Value "{}" -Encoding UTF8
    $secretsMountPath = $EmptySecretsStub
}
$secretsMountSlash = (Resolve-Path -LiteralPath $secretsMountPath).Path.Replace('\', '/')

# Ensure log directories exist on host before Docker bind-mounts them
foreach ($svcLog in @(
    "master-server",
    "chat-server",
    "web-api",
    "web-ui",
    "compel-dedicated",
    "compel-dedicated\artefacts",
    "compel-queue-arcade",
    "compel-queue-arcade\artefacts"
)) {
    $svcLogPath = Join-Path $RepoRoot "logs\docker\$svcLog"
    if (-not (Test-Path -LiteralPath $svcLogPath)) {
        New-Item -ItemType Directory -Path $svcLogPath -Force | Out-Null
    }
}

$effectiveServerVolume = "kongor-stack-run-server"
if (-not $SkipRuntimeChecks -and $dockerDaemonReady) {
    $prevVolEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $legacyRuntimeVol = (& docker volume ls -q -f "name=^kongor-arcade-runtime$" 2>$null)
    $ErrorActionPreference = $prevVolEap
    if (-not [string]::IsNullOrWhiteSpace("$legacyRuntimeVol")) {
        $effectiveServerVolume = "kongor-arcade-runtime"
    }
}

$webUiClientMount = Join-Path $RepoRoot "games\client"
if (-not (Test-Path -LiteralPath $webUiClientMount)) {
    $webUiClientMount = Join-Path $RuntimeDockerDir "empty_client"
    if (-not (Test-Path -LiteralPath $webUiClientMount)) {
        New-Item -ItemType Directory -Path $webUiClientMount -Force | Out-Null
    }
}
$webUiClientMountSlash = (Resolve-Path -LiteralPath $webUiClientMount).Path.Replace('\', '/')

$webUiPrebakedMount = Join-Path $RepoRoot "src\services\web-ui\wwwroot\assets\prebaked"
if (-not (Test-Path -LiteralPath $webUiPrebakedMount)) {
    $webUiPrebakedMount = Join-Path $RuntimeDockerDir "empty_prebaked"
    if (-not (Test-Path -LiteralPath $webUiPrebakedMount)) {
        New-Item -ItemType Directory -Path $webUiPrebakedMount -Force | Out-Null
    }
}
$webUiPrebakedMountSlash = (Resolve-Path -LiteralPath $webUiPrebakedMount).Path.Replace('\', '/')

# Resolve Discord/Twitch OAuth, Testing Domain, CORS Origins, HTTPS Certificate, and Split-Horizon WAN overrides
$discordClientIdRes = Resolve-ConfigSetting -Stores $Stores -Keys @("discord-client-id", "Authentication:Discord:ClientId", "Parameters:discord-client-id", "DISCORD_CLIENT_ID")
$discordClientSecretRes = Resolve-ConfigSetting -Stores $Stores -Keys @("discord-client-secret", "Authentication:Discord:ClientSecret", "Parameters:discord-client-secret", "DISCORD_CLIENT_SECRET")
$twitchClientIdRes = Resolve-ConfigSetting -Stores $Stores -Keys @("twitch-client-id", "Authentication:Twitch:ClientId", "Parameters:twitch-client-id", "TWITCH_CLIENT_ID")
$twitchClientSecretRes = Resolve-ConfigSetting -Stores $Stores -Keys @("twitch-client-secret", "Authentication:Twitch:ClientSecret", "Parameters:twitch-client-secret", "TWITCH_CLIENT_SECRET")

$effectiveStandaloneWeb = if ($StandaloneAuth) { "true" } else { "false" }
if (-not $StandaloneAuth -and (-not $discordClientIdRes.Found -or -not $discordClientSecretRes.Found)) {
    Write-StackLog "Discord OAuth keys are not configured; Web Portal defaults to 'Start Cluster' Discord gate. Pass -StandaloneAuth if you need offline username/password portal login." "INFO"
}

$testingDomainRes = Resolve-ConfigSetting -Stores $Stores -Keys @("WebPortal:TestingDomain", "TESTING_DOMAIN")
$rawTestingDomains = if (-not [string]::IsNullOrWhiteSpace($TestingDomain)) {
    $TestingDomain.Trim()
} elseif ($testingDomainRes.Found) {
    $testingDomainRes.Value
} else {
    ""
}

$normalizedTestingHostnames = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($rawTestingDomains)) {
    foreach ($entry in ($rawTestingDomains -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })) {
        $cleanHost = ($entry -replace '^(?i)https?://', '').TrimEnd('/')
        if (-not [string]::IsNullOrWhiteSpace($cleanHost) -and -not $normalizedTestingHostnames.Contains($cleanHost)) {
            $normalizedTestingHostnames.Add($cleanHost)
        }
    }
}
$effectiveTestingDomainCsv = $normalizedTestingHostnames -join ","

$publicDomainRes = Resolve-ConfigSetting -Stores $Stores -Keys @("WebPortal:PublicDomain", "KONGOR_PUBLIC_DOMAIN", "PUBLIC_DOMAIN")
$effectivePublicDomain = if ($publicDomainRes.Found -and -not [string]::IsNullOrWhiteSpace($publicDomainRes.Value)) {
    ($publicDomainRes.Value.Trim() -replace '^(?i)https?://', '').TrimEnd('/')
} else {
    "example.com"
}
$publicDomainSans = @($effectivePublicDomain, "www.$effectivePublicDomain", "masterserver.$effectivePublicDomain", "test.$effectivePublicDomain", "staging.$effectivePublicDomain", "dev.$effectivePublicDomain")

$certInfo = Ensure-DockerHttpsCertificate `
    -CertDirectory $runtimeEnvDir `
    -ExtraSanDomains @($publicDomainSans + $normalizedTestingHostnames | Select-Object -Unique) `
    -PreferFallbackOnly:$SkipRuntimeChecks
$httpsCertMountSlash = (Resolve-Path -LiteralPath $certInfo.PfxPath).Path.Replace('\', '/')

$corsOriginSet = New-Object System.Collections.Generic.List[string]
foreach ($baseOrigin in @(
    "http://localhost:5266",
    "http://127.0.0.1:5266",
    "https://localhost:5557",
    "http://localhost:5200",
    "https://localhost:5201",
    "http://localhost:55555",
    "https://localhost:5553",
    "https://localhost:5554",
    "http://localhost:5555",
    "https://localhost:5556",
    "https://$effectivePublicDomain",
    "https://www.$effectivePublicDomain",
    "https://masterserver.$effectivePublicDomain",
    "https://test.$effectivePublicDomain",
    "https://staging.$effectivePublicDomain",
    "https://dev.$effectivePublicDomain"
)) {
    if (-not $corsOriginSet.Contains($baseOrigin)) { $corsOriginSet.Add($baseOrigin) }
}
foreach ($td in $normalizedTestingHostnames) {
    foreach ($scheme in @("https", "http")) {
        $cand = "${scheme}://${td}"
        if (-not $corsOriginSet.Contains($cand)) { $corsOriginSet.Add($cand) }
    }
}
$extraCorsRes = Resolve-ConfigSetting -Stores $Stores -Keys @("CORS_ALLOWED_ORIGINS", "WebPortalApi:CorsAllowedOrigins")
if ($extraCorsRes.Found) {
    foreach ($co in ($extraCorsRes.Value -split '[,;]' | ForEach-Object { $_.Trim().TrimEnd('/') } | Where-Object { $_ -ne "" })) {
        if (-not $corsOriginSet.Contains($co)) { $corsOriginSet.Add($co) }
    }
}
$effectiveCorsOrigins = $corsOriginSet -join ","

$publicGameIpRes = Resolve-ConfigSetting -Stores $Stores -Keys @("PUBLIC_GAME_SERVER_IP", "GameServer:PublicIP", "GameServer:PublicIp")
$effectivePublicGameServerIp = if ($publicGameIpRes.Found -and $publicGameIpRes.Value -notin @("127.0.0.1", "localhost", "0.0.0.0", "::1")) {
    $publicGameIpRes.Value
} else {
    "127.0.0.1"
}

$publicChatHostRes = Resolve-ConfigSetting -Stores $Stores -Keys @("CHAT_SERVER_PUBLIC_HOST", "ChatServer:PublicHost")
$effectiveChatPublicHost = if ($publicChatHostRes.Found -and $publicChatHostRes.Value -notin @("127.0.0.1", "localhost", "0.0.0.0", "::1")) {
    $publicChatHostRes.Value
} else {
    "127.0.0.1"
}

$envLines = @(
    "# Auto-generated by scripts/docker/launch_docker_stack.ps1 (gitignored - DO NOT COMMIT)",
    (Format-DotEnvLine "KONGOR_SOURCE_STAMP" $sourceStamp),
    (Format-DotEnvLine "KONGOR_SERVER_VOLUME" $effectiveServerVolume),
    (Format-DotEnvLine "REPO_ROOT_SLASH" $RepoRootSlash),
    (Format-DotEnvLine "USER_SECRETS_MOUNT_PATH" $secretsMountSlash),
    (Format-DotEnvLine "HTTPS_CERT_MOUNT_PATH" $httpsCertMountSlash),
    (Format-DotEnvLine "HTTPS_CERT_PASSWORD" $certInfo.Password),
    (Format-DotEnvLine "WEB_UI_CLIENT_MOUNT_PATH" $webUiClientMountSlash),
    (Format-DotEnvLine "WEB_UI_PREBAKED_MOUNT_PATH" $webUiPrebakedMountSlash),
    (Format-DotEnvLine "PERFORMANCE_PRESET" $PerfKey),
    (Format-DotEnvLine "DOTNET_GC_SERVER" $ActivePreset.DotNetGcServer),
    (Format-DotEnvLine "DOTNET_GC_CONSERVE_MEMORY" $ActivePreset.DotNetGcConserve),
    (Format-DotEnvLine "GARNET_MEMORY" $ActivePreset.GarnetMemory),
    (Format-DotEnvLine "GARNET_CONTAINER_MEM_LIMIT" $ActivePreset.GarnetContainerMem),
    (Format-DotEnvLine "DASHBOARD_MEM_LIMIT" $ActivePreset.DashboardMemLimit),
    (Format-DotEnvLine "INFRA_CPU_LIMIT" $ActivePreset.InfraCpuLimit),
    (Format-DotEnvLine "POSTGRES_MEM_LIMIT" $ActivePreset.PostgresMemLimit),
    (Format-DotEnvLine "CLUSTER_CPU_LIMIT" $ActivePreset.ClusterCpuLimit),
    (Format-DotEnvLine "CLUSTER_MEM_LIMIT" $ActivePreset.ClusterMemLimit),
    (Format-DotEnvLine "MASTER_MEM_LIMIT" $ActivePreset.MasterMemLimit),
    (Format-DotEnvLine "MATCH_INSTANCES" "$($ActivePreset.MatchInstances)"),
    (Format-DotEnvLine "MATCH_WARM_INSTANCES" "$($ActivePreset.MatchWarmInstances)"),
    (Format-DotEnvLine "MATCH_CPU_LIMIT" $ActivePreset.MatchCpuLimit),
    (Format-DotEnvLine "MATCH_MEM_LIMIT" $ActivePreset.MatchMemLimit),
    (Format-DotEnvLine "ARCADE_INSTANCES" "$($ActivePreset.ArcadeInstances)"),
    (Format-DotEnvLine "ARCADE_WARM_INSTANCES" "$($ActivePreset.ArcadeWarmInstances)"),
    (Format-DotEnvLine "ARCADE_CPU_LIMIT" $ActivePreset.ArcadeCpuLimit),
    (Format-DotEnvLine "ARCADE_MEM_LIMIT" $ActivePreset.ArcadeMemLimit),
    (Format-DotEnvLine "COMPEL_PRIORITY_MODE" $ActivePreset.CompelPriorityMode),
    (Format-DotEnvLine "GARNET_PASSWORD" $cachePassRes.Value),
    (Format-DotEnvLine "LOCAL_POSTGRES_PASSWORD" $(if ($localPgPassRes.Found) { $localPgPassRes.Value } else { "" })),
    (Format-DotEnvLine "USE_LOCAL_POSTGRES" $(if ($LocalPostgres) { "true" } else { "false" })),
    (Format-DotEnvLine "DATABASE_PROVIDER" $effectiveDbProvider),
    (Format-DotEnvLine "REPLAY_STORAGE_PROVIDER" $effectiveReplayProvider),
    (Format-DotEnvLine "CONTAINER_DB_CONNECTION_STRING" $containerDbConnString),
    (Format-DotEnvLine "SUPABASE_URL" $supabaseUrlRes.Value),
    (Format-DotEnvLine "SUPABASE_ANON_KEY" $supabaseAnonRes.Value),
    (Format-DotEnvLine "SUPABASE_SERVICE_ROLE_KEY" $supabaseServiceRes.Value),
    (Format-DotEnvLine "JWT_SIGNING_KEY" $jwtKeyRes.Value),
    (Format-DotEnvLine "INTERNAL_SERVICE_KEY" $internalKeyRes.Value),
    (Format-DotEnvLine "KONGOR_STANDALONE_WEB" $effectiveStandaloneWeb),
    (Format-DotEnvLine "DISCORD_CLIENT_ID" $discordClientIdRes.Value),
    (Format-DotEnvLine "DISCORD_CLIENT_SECRET" $discordClientSecretRes.Value),
    (Format-DotEnvLine "TWITCH_CLIENT_ID" $twitchClientIdRes.Value),
    (Format-DotEnvLine "TWITCH_CLIENT_SECRET" $twitchClientSecretRes.Value),
    (Format-DotEnvLine "CORS_ALLOWED_ORIGINS" $effectiveCorsOrigins),
    (Format-DotEnvLine "TESTING_DOMAIN" $effectiveTestingDomainCsv),
    (Format-DotEnvLine "COMPEL_AUTH_TOKEN" $compelTokenRes.Value),
    (Format-DotEnvLine "MATCH_HOST_USERNAME" $matchHostUser),
    (Format-DotEnvLine "ARCADE_HOST_USERNAME" $arcadeHostUser),
    (Format-DotEnvLine "HOST_PASSWORD" $hostPassRes.Value),
    (Format-DotEnvLine "COMPEL_LOCATION" $effectiveLocation),
    (Format-DotEnvLine "MATCH_SERVER_PREFIX" $effectiveMatchPrefix),
    (Format-DotEnvLine "ARCADE_SERVER_PREFIX" "QUEUE ARCADE"),
    (Format-DotEnvLine "PUBLIC_GAME_SERVER_IP" $effectivePublicGameServerIp),
    (Format-DotEnvLine "CHAT_SERVER_PUBLIC_HOST" $effectiveChatPublicHost),
    (Format-DotEnvLine "CHAT_SERVER_LAN_HOST" $(
        $lanRes = Resolve-ConfigSetting -Stores $Stores -Keys @("CHAT_SERVER_LAN_HOST", "ChatServer:LanHost")
        if ($lanRes.Found -and $lanRes.Value -ne "127.0.0.1" -and $lanRes.Value -ne "localhost") {
            $lanRes.Value
        } else {
            $detectedLan = ""
            try {
                $udpProbe = New-Object System.Net.Sockets.Socket([System.Net.Sockets.AddressFamily]::InterNetwork, [System.Net.Sockets.SocketType]::Dgram, [System.Net.Sockets.ProtocolType]::Udp)
                try {
                    $udpProbe.Connect("8.8.8.8", 53)
                    $ep = [System.Net.IPEndPoint]$udpProbe.LocalEndPoint
                    if ($null -ne $ep -and $ep.Address.ToString() -ne "127.0.0.1") {
                        $detectedLan = $ep.Address.ToString()
                    }
                } finally {
                    $udpProbe.Close()
                }
            } catch { }
            Write-StackLog "[PREFLIGHT-OK] Host LAN IP resolved for Docker split-horizon UDP routing: $(if ($detectedLan) { $detectedLan } else { 'loopback-only' })" "DEBUG"
            $detectedLan
        }
    ))
)
$previousRuntimeEnvRaw = if (Test-Path -LiteralPath $EffectiveRuntimeEnvPath) {
    Get-Content -LiteralPath $EffectiveRuntimeEnvPath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
} else {
    ""
}
$newRuntimeEnvRaw = $envLines -join [Environment]::NewLine
$envConfigChanged = (Get-NormalizedEnvConfigText -RawContent $newRuntimeEnvRaw) -ne (Get-NormalizedEnvConfigText -RawContent $previousRuntimeEnvRaw)
Set-Content -LiteralPath $EffectiveRuntimeEnvPath -Value $newRuntimeEnvRaw -Encoding UTF8

Write-StackLog "Pre-flight checks PASSED. Performance='$PerfKey' | DB='$effectiveDbProvider' | MatchHost='$matchHostUser' ($($ActivePreset.MatchInstances)x) | ArcadeHost='$arcadeHostUser' ($($ActivePreset.ArcadeInstances)x) | Priority='$($ActivePreset.CompelPriorityMode)'" "SUCCESS"

if ($NormalizedAction -eq "check") {
    Write-StackLog "Pre-flight check completed (no containers launched). Run '.\kongor.ps1 docker up' to launch the stack." "SUCCESS"
    exit 0
}

$singleRestartService = if ($NormalizedAction -eq "restart" -and -not [string]::IsNullOrWhiteSpace($Service)) {
    Resolve-DockerServiceTarget -Name $Service
} else {
    ""
}

if ($NormalizedAction -eq "sync") {
    Write-StackLog "[STAGE 1/4 -> STAGE 2/4] Pre-flight check passed. Publishing linux-x64 binaries & building Docker images before container cutover (keeping any live stack online during build)..." "INFO"
} elseif ($NormalizedAction -eq "rebuild") {
    Write-StackLog "[REBUILD] Host conflicts cleared; re-publishing linux-x64 binaries and rebuilding Docker images..." "INFO"
} elseif ($NormalizedAction -eq "restart") {
    if (-not [string]::IsNullOrWhiteSpace($singleRestartService)) {
        Write-StackLog "[RESTART] Restarting single Docker service '$singleRestartService'..." "INFO"
    } else {
        Write-StackLog "[RESTART] Performing fast Docker stack restart (Performance='$PerfKey', envChanged=$envConfigChanged)..." "INFO"
    }
}

# ==============================================================================
# PHASE 2: STAMP-VERIFIED LINUX-X64 PUBLISH & DOCKER IMAGE BUILD
# ==============================================================================
$PublishTargets = @(
    @{ Name = "master-server"; Project = "src\services\master-server\KONGOR.MasterServer.csproj";       OutDir = "artifacts\publish\master-server"; Binary = "KONGOR.MasterServer";       Image = "projectkongor/master-server:latest" },
    @{ Name = "chat-server";   Project = "src\services\chat-server\TRANSMUTANSTEIN.ChatServer.csproj";  OutDir = "artifacts\publish\chat-server";   Binary = "TRANSMUTANSTEIN.ChatServer"; Image = "projectkongor/chat-server:latest" },
    @{ Name = "web-api";       Project = "src\services\web-api\ZORGATH.WebPortal.API.csproj";           OutDir = "artifacts\publish\web-api";       Binary = "ZORGATH.WebPortal.API";     Image = "projectkongor/web-api:latest" },
    @{ Name = "web-ui";        Project = "src\services\web-ui\DAWNBRINGER.WebPortal.UI.csproj";         OutDir = "artifacts\publish\web-ui";        Binary = "DAWNBRINGER.WebPortal.UI";  Image = "projectkongor/web-ui:latest" },
    @{ Name = "compel";        Project = "src\apps\compel\COMPEL.csproj";                               OutDir = "artifacts\publish\compel";        Binary = "COMPEL";                    Image = "projectkongor/compel-dedicated:latest" }
)

$previousPublishStamp = ""
$previousImageStamp = ""
if (Test-Path -LiteralPath $BuildStampFile) {
    try {
        $stampObj = Get-Content -LiteralPath $BuildStampFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $previousPublishStamp = if ($stampObj.PublishStamp) { [string]$stampObj.PublishStamp } else { [string]$stampObj.SourceStamp }
        $previousImageStamp = if ($stampObj.ImageStamp) { [string]$stampObj.ImageStamp } else { [string]$stampObj.SourceStamp }
    } catch {
        Write-StackLog "Ignored unreadable build stamp at '$BuildStampFile': $($_.Exception.Message)" "DEBUG"
    }
}

$needsPublish = $Rebuild -or ($previousPublishStamp -ne $sourceStamp)
$needsImageBuild = $Rebuild -or ($previousImageStamp -ne $sourceStamp)
$hasMissingImage = $false
foreach ($t in $PublishTargets) {
    $binPath = Join-Path $RepoRoot (Join-Path $t.OutDir $t.Binary)
    if (-not (Test-Path -LiteralPath $binPath)) {
        $needsPublish = $true
        $needsImageBuild = $true
        Write-StackLog "Linux binary missing at '$binPath'; scheduling publish." "INFO"
    }
    $imgId = (& docker images -q $t.Image 2>$null)
    if ([string]::IsNullOrWhiteSpace("$imgId")) {
        $needsImageBuild = $true
        $hasMissingImage = $true
        Write-StackLog "Docker image '$($t.Image)' not built yet; scheduling build." "INFO"
    }
}

$useFastCachedImages = ($SkipAutoRebuild -or $NormalizedAction -eq "restart") -and (-not $Rebuild) -and (-not $hasMissingImage)
if ($useFastCachedImages) {
    $needsPublish = $false
    $needsImageBuild = $false
    if ($previousImageStamp -ne $sourceStamp) {
        Write-StackLog "[WARN] Cached Docker images (stamp='$previousImageStamp') differ from working tree (stamp='$sourceStamp'). Reusing existing images for fast '$NormalizedAction'; run 'kongor sync' or click 'Rebuild & Sync' to apply code edits." "WARN"
    } else {
        Write-StackLog "Reusing cached Docker images for fast '$NormalizedAction' (stamp=$sourceStamp)." "INFO"
    }
}

$stampDir = Split-Path -Parent $BuildStampFile
if (-not (Test-Path -LiteralPath $stampDir)) {
    New-Item -ItemType Directory -Path $stampDir -Force | Out-Null
}

if ($needsPublish) {
    Write-StackLog "Publishing self-contained linux-x64 artifacts (stamp: '$previousPublishStamp' -> '$sourceStamp')..." "INFO"
    foreach ($t in $PublishTargets) {
        $projFull = Join-Path $RepoRoot $t.Project
        $outFull = Join-Path $RepoRoot $t.OutDir
        Write-StackLog "Publishing $($t.Name) ($($t.Project)) -> $($t.OutDir)..." "INFO"
        if (Test-Path -LiteralPath $TestLockScript) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $TestLockScript -Owner "docker-stack-publish-$PID" -- dotnet publish $projFull -c Release -r linux-x64 -o $outFull --self-contained /p:PublishTrimmed=false --nologo
        } else {
            & dotnet publish $projFull -c Release -r linux-x64 -o $outFull --self-contained /p:PublishTrimmed=false --nologo
        }
        if ($LASTEXITCODE -ne 0) {
            Write-StackLog "dotnet publish failed for '$($t.Name)' (exit $LASTEXITCODE). Run '.\scripts\Invoke-KongorTestLock.ps1 -Owner diag -- dotnet build $($t.Project) -c Release' to diagnose." "ERROR"
            exit $LASTEXITCODE
        }
    }
    @{
        SourceStamp  = $sourceStamp
        PublishStamp = $sourceStamp
        ImageStamp   = $previousImageStamp
        BuiltAtUtc   = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json | Set-Content -LiteralPath $BuildStampFile -Encoding UTF8
    $needsImageBuild = $true
}

if ($needsImageBuild) {
    Write-StackLog "Building Docker stack container images sequentially (avoids BuildKit named-pipe saturation & shared compel-dedicated tag race)..." "INFO"
    $uniqueImageServices = @("master-server", "chat-server", "web-api", "web-ui", "compel-dedicated")
    foreach ($buildSvc in $uniqueImageServices) {
        Write-StackLog "Building image for service '$buildSvc'..." "INFO"
        $buildExit = Invoke-Compose -ComposeArgs @("build", $buildSvc)
        if ($buildExit -ne 0) {
            Write-StackLog "docker compose build '$buildSvc' failed (exit $buildExit). Inspect build output above or re-run '.\kongor.ps1 docker up -Rebuild'." "ERROR"
            exit $buildExit
        }
    }

    @{
        SourceStamp  = $sourceStamp
        PublishStamp = $sourceStamp
        ImageStamp   = $sourceStamp
        BuiltAtUtc   = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json | Set-Content -LiteralPath $BuildStampFile -Encoding UTF8
    Write-StackLog "Linux-x64 artifacts and Docker images are up-to-date (stamp=$sourceStamp)." "SUCCESS"
} else {
    Write-StackLog "Reusing Docker images & linux-x64 publish cache (stamp=$sourceStamp). Pass -Rebuild to force rebuild." "INFO"
}

if (-not [string]::IsNullOrWhiteSpace($singleRestartService)) {
    $singleRunning = $false
    $prevSingleEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $singlePs = (& docker compose -f $ComposeFile --profile local-postgres --env-file $EffectiveRuntimeEnvPath ps -a --format json $singleRestartService 2>$null) -join ""
        if (-not [string]::IsNullOrWhiteSpace($singlePs) -and $singlePs -match '"State"\s*:\s*"running"') {
            $singleRunning = $true
        }
    } finally {
        $ErrorActionPreference = $prevSingleEap
    }

    if ($singleRunning) {
        $singleExit = Invoke-Compose -ComposeArgs @("restart", $singleRestartService)
    } else {
        $singleExit = Invoke-Compose -ComposeArgs @("up", "-d", "--no-deps", "--no-build", $singleRestartService)
    }
    if ($singleExit -ne 0) {
        Write-StackLog "Failed restarting Docker service '$singleRestartService' (exit $singleExit). Inspect via 'kongor docker logs -Service $singleRestartService'." "ERROR"
        exit $singleExit
    }
}

if ($NormalizedAction -eq "sync") {
    Write-StackLog "[STAGE 2/4 -> STAGE 3/4] Artifacts & images ready. Cycling existing Docker stack containers cleanly before relaunch (volumes preserved)..." "INFO"
    $syncDownArgs = @("compose", "-f", $ComposeFile, "--profile", "local-postgres")
    if (Test-Path -LiteralPath $EffectiveRuntimeEnvPath) {
        $syncDownArgs += @("--env-file", $EffectiveRuntimeEnvPath)
    }
    $syncDownArgs += @("down", "--remove-orphans")
    & docker @syncDownArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-StackLog "docker compose down failed during 'sync' cutover (exit $LASTEXITCODE). Ensure Docker Desktop is running ('docker info')." "ERROR"
        exit $LASTEXITCODE
    }
    Write-StackLog "[STAGE 3/4] Existing Docker stack stopped cleanly. Launching freshly built stack..." "SUCCESS"
}

# ==============================================================================
# PHASE 3: LAUNCH / RESTART DOCKER COMPOSE STACK & DEEP READINESS VERIFICATION
# ==============================================================================
$performedInPlaceRestart = $false
$isSingleServiceRestart = -not [string]::IsNullOrWhiteSpace($singleRestartService)

if (-not $isSingleServiceRestart -and $NormalizedAction -eq "restart" -and -not $envConfigChanged) {
    $expectedServices = @("garnet", "aspire-dashboard", "master-server", "chat-server", "web-api", "web-ui", "compel-dedicated", "compel-queue-arcade")
    if ($LocalPostgres) { $expectedServices += "postgres" }
    $infraMustRun = @("garnet", "aspire-dashboard")
    if ($LocalPostgres) { $infraMustRun += "postgres" }

    $prevPsEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $psJsonLines = @()
    try {
        $psArgs = @("compose", "-f", $ComposeFile)
        if ($LocalPostgres) { $psArgs += @("--profile", "local-postgres") }
        if (Test-Path -LiteralPath $EffectiveRuntimeEnvPath) { $psArgs += @("--env-file", $EffectiveRuntimeEnvPath) }
        $psArgs += @("ps", "-a", "--format", "json")
        $psJsonLines = @(& docker @psArgs 2>$null)
    } finally {
        $ErrorActionPreference = $prevPsEap
    }

    $svcStateMap = @{}
    $joinedRestartPs = ($psJsonLines | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") }) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($joinedRestartPs)) {
        $trimRestartPs = $joinedRestartPs.Trim()
        $psObjs = @()
        if ($trimRestartPs.StartsWith("[")) {
            try { $psObjs = @($trimRestartPs | ConvertFrom-Json) } catch { Write-StackLog "Ignored restart ps JSON parse error: $($_.Exception.Message)" "DEBUG" }
        } else {
            foreach ($jl in ($psJsonLines | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") })) {
                $jlt = "$jl".Trim()
                if ($jlt.StartsWith("{")) {
                    try { $psObjs += ($jlt | ConvertFrom-Json) } catch { Write-StackLog "Ignored restart ps JSON line error: $($_.Exception.Message)" "DEBUG" }
                }
            }
        }
        foreach ($po in $psObjs) {
            if ($po.Service) {
                $svcStateMap[[string]$po.Service] = ([string]$po.State).ToLowerInvariant()
            }
        }
    }

    $allExist = $true
    foreach ($es in $expectedServices) {
        if (-not $svcStateMap.ContainsKey($es)) {
            $allExist = $false
            break
        }
    }
    $infraRunning = $true
    foreach ($ir in $infraMustRun) {
        if (-not $svcStateMap.ContainsKey($ir) -or $svcStateMap[$ir] -ne "running") {
            $infraRunning = $false
            break
        }
    }

    if ($allExist -and $infraRunning) {
        $inPlaceTargets = @("master-server", "chat-server", "web-api", "web-ui", "compel-dedicated", "compel-queue-arcade")
        Write-StackLog "[RESTART] Configuration unchanged; restarting 6 backend & COMPEL containers in-place (preserving Garnet Redis state & Aspire Dashboard)..." "INFO"
        $restartExit = Invoke-Compose (@("restart") + $inPlaceTargets)
        if ($restartExit -ne 0) {
            Write-StackLog "docker compose restart failed (exit $restartExit). Run '.\kongor.ps1 docker logs' to inspect container errors." "ERROR"
            exit $restartExit
        }
        $performedInPlaceRestart = $true
    } else {
        Write-StackLog "[RESTART] Infra containers not running or container missing (allExist=$allExist, infraRunning=$infraRunning); falling back to fast config-diff compose up (--no-build)..." "INFO"
    }
}

if (-not $isSingleServiceRestart -and -not $performedInPlaceRestart) {
    # Pre-seed cold shared dedicated server ext4 volume via native Docker tar stream (bypasses ~10 MB/s WSL2 9P cp bottleneck on first boot)
    $serverVolName = $effectiveServerVolume
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $existingVol = (& docker volume ls -q -f "name=^$serverVolName$" 2>$null)
        if ([string]::IsNullOrWhiteSpace("$existingVol")) {
            & docker volume create --label com.docker.compose.project=kongor-stack --label com.docker.compose.volume=kongor-run-server $serverVolName *> $null
        }

        $lastServerArchive = Get-ChildItem -Path (Join-Path $RepoRoot "games\server\game\*.s2z") -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
        $sentinelArchiveName = if ($lastServerArchive) { $lastServerArchive.Name } else { "resources0.s2z" }
        $sentinelArchiveBytes = if ($lastServerArchive) { [int64]$lastServerArchive.Length } else { 1 }

        $probeSh = "s_ok=0; if [ -f /srv/hon-x86_64-server ] && [ `$(stat -c%s /srv/game/$sentinelArchiveName 2>/dev/null || echo 0) -eq $sentinelArchiveBytes ]; then s_ok=1; fi; echo `$s_ok"
        $volProbeOut = (& docker run --rm --entrypoint /bin/sh `
            -v "${serverVolName}:/srv:ro" `
            projectkongor/compel-dedicated:latest `
            -c $probeSh 2>$null)
        $serverSeeded = ("$volProbeOut".Trim() -eq "1")

        if (-not $serverSeeded) {
            Write-StackLog "Seeding shared Dedicated Server ext4 runtime volume ($serverVolName) via Docker tar pipe..." "INFO"
            $seederName = "kongor-stack-vol-seeder"
            & docker rm -f $seederName *> $null
            & docker create --name $seederName --entrypoint /bin/sh -v "${serverVolName}:/dst" projectkongor/compel-dedicated:latest -c "true" *> $null
            if ($LASTEXITCODE -ne 0) {
                Write-StackLog "Failed to create temporary volume seeder container '$seederName'. Verify Docker Desktop has sufficient disk space." "ERROR"
                exit 1
            }
            try {
                $srcGameDir = Join-Path $RepoRoot "games\server\game"
                Write-StackLog "  [1/2] Streaming game/*.s2z archives & server entities via Docker tar pipe (one-time ext4 cache)..." "INFO"
                & docker cp $srcGameDir "${seederName}:/dst/" | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "docker cp failed for game (exit $LASTEXITCODE)." }
            } finally {
                & docker rm -f $seederName *> $null
            }

            Write-StackLog "  [1.5/2] Normalizing ext4 runtime volume ownership to uid:gid 10001:10001 (non-root kongor)..." "INFO"
            & docker run --rm --user 0:0 --entrypoint /bin/sh `
                -v "${serverVolName}:/dst" `
                projectkongor/compel-dedicated:latest `
                -c "chown -R 10001:10001 /dst" | Out-Host
            if ($LASTEXITCODE -ne 0) {
                Write-StackLog "Failed to normalize ownership on '$serverVolName' (exit $LASTEXITCODE)." "ERROR"
                exit 1
            }

            Write-StackLog "  [2/2] Finalizing Linux server binaries & native shims inside ext4 volume..." "INFO"
            & docker run --rm --entrypoint /bin/sh `
                -v "${RepoRootSlash}/games/server:/app/server:ro" `
                -v "${serverVolName}:/app/run-server" `
                -e COMPEL_FORCE_SYNC=true `
                projectkongor/compel-dedicated:latest `
                -c "rm -f /app/run-server/hon-x86_64-server; sed 's|^CHAT_FWD_HOST=|exit 0 #|' /app/entrypoint.sh | /bin/sh && touch /app/run-server/hon-x86_64-server" | Out-Host
            if ($LASTEXITCODE -ne 0) {
                Write-StackLog "Failed to finalize server runtime on '$serverVolName'." "ERROR"
                exit 1
            }
            Write-StackLog "Shared Dedicated Server ext4 runtime volume ($serverVolName) seeded." "SUCCESS"
        } else {
            Write-StackLog "Shared Dedicated Server ext4 runtime volume '$serverVolName' is warm (fast-path <200ms startup)." "DEBUG"
        }
    } finally {
        $ErrorActionPreference = $prevEap
    }

    Write-StackLog "Starting Docker Compose stack (Performance='$PerfKey')..." "INFO"
    $upComposeArgs = @("up", "-d", "--remove-orphans")
    if (-not $needsImageBuild) {
        $upComposeArgs += "--no-build"
    }
    $upExit = Invoke-Compose -ComposeArgs $upComposeArgs
    if ($upExit -ne 0) {
        Write-StackLog "docker compose up failed (exit $upExit). Collecting failing container diagnostics..." "ERROR"
        $prevDiagEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $diagLines = & docker ps -a --filter "name=kongor-stack-" --format "{{.Names}}|{{.Status}}" 2>$null
            foreach ($dl in @($diagLines)) {
                $parts = "$dl".Split('|')
                if ($parts.Count -ge 2) {
                    $cName = $parts[0].Trim()
                    $cStat = $parts[1].Trim()
                    if ($cStat -match 'Restarting|unhealthy|Exited \([1-9]') {
                        Write-StackLog "  [FAILING-CONTAINER] $cName -> $cStat" "ERROR"
                        $tailOut = (& docker logs $cName --tail 15 2>&1) | Select-Object -Last 8
                        foreach ($tl in @($tailOut)) {
                            Write-StackLog "    [$cName] $tl" "ERROR"
                        }
                    }
                }
            }
        } finally {
            $ErrorActionPreference = $prevDiagEap
        }
        Write-StackLog "Run '.\kongor.ps1 docker logs' to inspect full container logs." "ERROR"
        exit $upExit
    }
}

$ClusterProbes = @(
    @{ Name = "MasterServer";         Service = "master-server"; Url = "http://127.0.0.1:55555/health/ready" },
    @{ Name = "ChatServer";           Service = "chat-server";   Url = "http://127.0.0.1:5554/health/ready" },
    @{ Name = "WebAPI (HTTP :5200)";  Service = "web-api";       Url = "http://127.0.0.1:5200/health/ready" },
    @{ Name = "WebUI (HTTP :5266)";   Service = "web-ui";        Url = "http://127.0.0.1:5266/health/ready" }
)
if ($isSingleServiceRestart) {
    $ClusterProbes = @($ClusterProbes | Where-Object { $_.Service -eq $singleRestartService })
}

$Deadline = (Get-Date).AddSeconds(120)
foreach ($probe in $ClusterProbes) {
    Write-StackLog "Waiting for $($probe.Name) readiness ($($probe.Url))..." "INFO"
    $ready = $false
    while ((Get-Date) -lt $Deadline) {
        try {
            $resp = Invoke-WebRequest -Uri $probe.Url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
            if ([int]$resp.StatusCode -eq 200) {
                $ready = $true
                break
            }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) {
        Write-StackLog "$($probe.Name) failed to pass readiness at $($probe.Url) within timeout. Diagnose via: .\kongor.ps1 docker logs -Service $($probe.Service)" "ERROR"
        exit 1
    }
    Write-StackLog "$($probe.Name) is HEALTHY." "SUCCESS"
}

$HttpsProbes = @(
    @{ Name = "Aspire Dashboard (HTTPS :5550)"; Service = "aspire-dashboard"; Port = 5550; Path = "/login";        Accepted = @(200, 302); Url = "https://localhost:5550" },
    @{ Name = "WebAPI (HTTPS :5201)";           Service = "web-api";          Port = 5201; Path = "/health/ready"; Accepted = @(200);      Url = "https://localhost:5201/health/ready" },
    @{ Name = "WebUI (HTTPS :5557)";            Service = "web-ui";           Port = 5557; Path = "/health/ready"; Accepted = @(200);      Url = "https://localhost:5557/health/ready" }
)
if ($isSingleServiceRestart) {
    $HttpsProbes = @($HttpsProbes | Where-Object { $_.Service -eq $singleRestartService })
}

foreach ($tlsItem in $HttpsProbes) {
    Write-StackLog "Waiting for $($tlsItem.Name) TLS readiness ($($tlsItem.Url))..." "INFO"
    $tlsReady = $false
    while ((Get-Date) -lt $Deadline) {
        $probeRes = Test-HttpsEndpointReady -Port $tlsItem.Port -Path $tlsItem.Path -ExpectedThumbprint $certInfo.Thumbprint -AcceptedStatusCodes $tlsItem.Accepted
        if ($probeRes.Ready) {
            $tlsReady = $true
            break
        }
        Start-Sleep -Seconds 2
    }
    if (-not $tlsReady) {
        Write-StackLog "$($tlsItem.Name) failed TLS readiness at $($tlsItem.Url) within timeout. Diagnose via: .\kongor.ps1 docker logs -Service $($tlsItem.Service)" "ERROR"
        exit 1
    }
    Write-StackLog "$($tlsItem.Name) is HEALTHY." "SUCCESS"
}

# Verify both COMPEL supervisors (Dedicated Match Tier D + Queue Arcade Tier Q) via authenticated /status
$FleetProbes = @(
    @{ Name = "Dedicated Match Fleet (Tier D)"; Service = "compel-dedicated";    PingUrl = "http://127.0.0.1:11334/ping"; StatusUrl = "http://127.0.0.1:11334/status"; HostAccount = $matchHostUser },
    @{ Name = "Queue Arcade Fleet (Tier Q)";    Service = "compel-queue-arcade"; PingUrl = "http://127.0.0.1:11484/ping"; StatusUrl = "http://127.0.0.1:11484/status"; HostAccount = $arcadeHostUser }
)
if ($isSingleServiceRestart) {
    $FleetProbes = @($FleetProbes | Where-Object { $_.Service -eq $singleRestartService })
}

$Deadline = (Get-Date).AddSeconds(150)

$authHeaders = @{ Authorization = "Bearer $($compelTokenRes.Value)" }
foreach ($fleet in $FleetProbes) {
    Write-StackLog "Waiting for $($fleet.Name) control plane & worker initialization ($($fleet.StatusUrl))..." "INFO"
    $fleetReady = $false
    $statusSummary = ""
    while ((Get-Date) -lt $Deadline) {
        try {
            $pingResp = Invoke-WebRequest -Uri $fleet.PingUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
            if ([int]$pingResp.StatusCode -eq 200) {
                try {
                    $statusObj = Invoke-RestMethod -Uri $fleet.StatusUrl -Headers $authHeaders -TimeoutSec 3 -ErrorAction Stop
                    $activeWorkers = 0
                    if ($null -ne $statusObj.activeWorkers) { $activeWorkers = [int]$statusObj.activeWorkers }
                    elseif ($null -ne $statusObj.ActiveWorkers) { $activeWorkers = [int]$statusObj.ActiveWorkers }
                    elseif ($null -ne $statusObj.activeWorkerCount) { $activeWorkers = [int]$statusObj.activeWorkerCount }
                    elseif ($null -ne $statusObj.ActiveWorkerCount) { $activeWorkers = [int]$statusObj.ActiveWorkerCount }
                    elseif ($null -ne $statusObj.workers) { $activeWorkers = @($statusObj.workers | Where-Object { $_.running -eq $true }).Count }
                    $mgrRunning = ($statusObj.managerRunning -eq $true) -or ($statusObj.ManagerRunning -eq $true)

                    if ($mgrRunning -and $activeWorkers -ge 1) {
                        $statusSummary = "managerRunning=$mgrRunning, activeWorkers=$activeWorkers"
                        $fleetReady = $true
                        break
                    }
                    Write-StackLog "  ... $($fleet.Name) warming up (managerRunning=$mgrRunning, activeWorkers=$activeWorkers)..." "DEBUG"
                } catch {
                    # Fallback if /status is still warming up while /ping is live
                }
            }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $fleetReady) {
        Write-StackLog "$($fleet.Name) did not become ready at $($fleet.StatusUrl) within timeout. Diagnose via: .\kongor.ps1 docker logs -Service $($fleet.Service)" "ERROR"
        exit 1
    }
    Write-StackLog "$($fleet.Name) ONLINE (HostAccount='$($fleet.HostAccount)') -> $statusSummary" "SUCCESS"
}

if ($isSingleServiceRestart) {
    if ($singleRestartService -eq "garnet" -and -not (Test-TcpEndpointReady -Port 28298 -TimeoutMs 5000)) {
        Write-StackLog "Garnet Cache did not respond on TCP 127.0.0.1:28298 after restart." "ERROR"
        exit 1
    }
    if ($singleRestartService -eq "postgres" -and -not (Test-TcpEndpointReady -Port 25432 -TimeoutMs 5000)) {
        Write-StackLog "Local PostgreSQL did not respond on TCP 127.0.0.1:25432 after restart." "ERROR"
        exit 1
    }
    Write-StackLog "[RESTART] Docker service '$singleRestartService' restarted and verified healthy." "SUCCESS"
    exit 0
}

# Check MasterServer logs for any HostAccount "Was Not Found" / "Incorrect Password" warnings so fresh DB issues surface immediately
$msLogTail = (& docker logs --tail 80 kongor-stack-master-server 2>&1) -join "`n"
if ($msLogTail -match 'Account "([^"]+)" Was Not Found') {
    $missingAcc = $Matches[1]
    Write-StackLog "WARNING: MasterServer reported ServerHost account '$missingAcc' was not found in database! Set HostAccount:UserName to '$defaultHostUserPrefix' (or provision '$missingAcc' as AccountType.ServerHost (2) in MERRICK)." "WARN"
} elseif ($msLogTail -match 'Password mismatch for') {
    Write-StackLog "WARNING: MasterServer reported a HostAccount password mismatch! Verify 'HostAccount:Password' matches the ServerHost SRP record in MERRICK." "WARN"
}

if ($NormalizedAction -eq "sync") {
    Write-StackLog "[STAGE 4/4] Verifying unified container status, TCP infra & health endpoints ('status')..." "INFO"
    $syncProbeRaw = @(Invoke-DockerStackStatusProbe -Strict -ExpectedThumbprint $certInfo.Thumbprint)
    $syncStatusExit = [int]$syncProbeRaw[-1]
    if ($syncStatusExit -ne 0) {
        Write-StackLog "[STAGE 4/4] One or more containers or endpoints reported OFFLINE during sync verification (exit $syncStatusExit). Inspect via 'kongor docker logs'." "ERROR"
        exit $syncStatusExit
    }
    Write-StackLog "[STAGE 4/4] Full Docker sync & verification PASSED!" "SUCCESS"
}

Write-Host ""
Write-Host "================================================================================" -ForegroundColor Green
Write-Host " PROJECT KONGOR DOCKER STACK IS LIVE (Performance: $PerfKey)" -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Green
Write-Host "  Aspire Dashboard:       https://localhost:5550 (HTTP: http://localhost:5551)" -ForegroundColor Cyan
Write-Host "  Web Portal UI:          https://localhost:5557 (HTTP: http://localhost:5266)" -ForegroundColor Cyan
Write-Host "  Web Portal REST API:    https://localhost:5201/swagger (HTTP: http://localhost:5200/swagger)" -ForegroundColor Cyan
Write-Host "  Master Server:          http://localhost:55555" -ForegroundColor Cyan
Write-Host "  Chat Server (TCP):      127.0.0.1:11031 (HTTP Health: http://127.0.0.1:5554/health)" -ForegroundColor Cyan
Write-Host "  Cloudflare Tunnel:      https://www.$effectivePublicDomain (:5557) | https://$effectivePublicDomain (:5266) | https://masterserver.$effectivePublicDomain (:55555)" -ForegroundColor Cyan
if ($normalizedTestingHostnames.Count -gt 0) {
    foreach ($td in $normalizedTestingHostnames) {
        Write-Host "  Testing Domain:         https://$td (Discord OAuth Callback: https://$td/signin-discord)" -ForegroundColor Yellow
    }
}
Write-Host "  Garnet Cache:           127.0.0.1:28298" -ForegroundColor Cyan
if ($LocalPostgres) {
    Write-Host "  Local PostgreSQL 17:    127.0.0.1:25432 (DB: merrick)" -ForegroundColor Cyan
}
Write-Host "  Match Servers (Tier D): http://127.0.0.1:11334 (UDP 11335.. | $($ActivePreset.MatchInstances) instance(s) | Host: $matchHostUser)" -ForegroundColor Cyan
Write-Host "  Queue Arcade (Tier Q):  http://127.0.0.1:11484 (UDP 11485.. | $($ActivePreset.ArcadeInstances) instance(s) | Host: $arcadeHostUser)" -ForegroundColor Cyan
Write-Host "  Manage Stack:           kongor sync | kongor docker-rebuild | kongor docker status | logs | down" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Green

# ==============================================================================
# PHASE 4: OPTIONAL NATIVE WINDOWS GAME CLIENT LAUNCH (NOT IN DOCKER)
# ==============================================================================
if ($WithClient) {
    if ($UseLauncher) {
        $launcherProj = Join-Path $RepoRoot "src\apps\willowmaker\WILLOWMAKER.csproj"
        Write-StackLog "Launching WILLOWMAKER desktop launcher on host connected to local Docker cluster..." "INFO"
        $env:MASTER_SERVER_ADDRESS = "http://127.0.0.1:55555"
        $launcherCmdLine = "dotnet run --project `"$launcherProj`" -c Release"
        $cimLauncher = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine      = $launcherCmdLine
            CurrentDirectory = $RepoRoot
        } -ErrorAction SilentlyContinue
        if ($null -ne $cimLauncher -and $cimLauncher.ReturnValue -eq 0) {
            Write-StackLog "WILLOWMAKER launcher started via Win32_Process (PID $($cimLauncher.ProcessId))." "SUCCESS"
        } else {
            Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", "`"$launcherProj`"", "-c", "Release") -WorkingDirectory $RepoRoot | Out-Null
            Write-StackLog "WILLOWMAKER launcher started." "SUCCESS"
        }
    } else {
        $clientWorkDir = Split-Path -Parent $resolvedClientExe
        $vfsConfigPath = Join-Path $RepoRoot "config\vfs_mounts.json"
        $canonicalModArg = if (Test-Path -LiteralPath $vfsConfigPath) {
            $vfsObj = Get-Content -LiteralPath $vfsConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            "-mod $($vfsObj.mod_param_string)"
        } else {
            "-mod base;game;ClientPackageFiles;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions;override"
        }

        # Preserve user's saved graphics/FPS preferences; only bind network endpoints, VFS mount order, and console logging
        $clientArgs = @(
            "-masterserver 127.0.0.1:55555",
            "-webserver 127.0.0.1:55555",
            "-messageserver 127.0.0.1:55555",
            "-chatserver 127.0.0.1:11031",
            "+Set chat_serverAddrOverride 127.0.0.1",
            "+Set chat_serverPortOverride 11031",
            $canonicalModArg,
            "-pipe willowmaker-ipc",
            "-writeconsole",
            "-logfile console.log",
            "+Set con_log 1"
        ) -join " "

        Write-StackLog "Spawning native Windows game client '$resolvedClientExe' -> 127.0.0.1:55555 / :11031..." "INFO"
        $cimClient = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine      = "`"$resolvedClientExe`" $clientArgs"
            CurrentDirectory = $clientWorkDir
        } -ErrorAction SilentlyContinue
        if ($null -ne $cimClient -and $cimClient.ReturnValue -eq 0) {
            Write-StackLog "Spawned detached hon_x64.exe via Win32_Process (PID $($cimClient.ProcessId)) connected to local Docker cluster." "SUCCESS"
        } else {
            $clientProc = Start-Process -FilePath $resolvedClientExe -ArgumentList $clientArgs -WorkingDirectory $clientWorkDir -PassThru
            Write-StackLog "Spawned hon_x64.exe (PID $($clientProc.Id)) connected to local Docker cluster." "SUCCESS"
        }
    }
}

exit 0

} finally {
    Release-DockerStackLock
}
