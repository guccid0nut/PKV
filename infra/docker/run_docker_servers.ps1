# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Turnkey launch script for Docker COMPEL Dedicated Servers (Fleet: DOCKER).
.DESCRIPTION
    Launches the Docker container with configured host account and Port Offset 100:
    - Control Plane: http://127.0.0.1:11334
    - Game Ports: UDP 11335, 11336
    - Voice Ports: UDP 11835, 11836
    - Public Proxy Ports: UDP 21334-21336, Voice Proxy 21835-21836
    - Server Prefix: ^c[DOCKER] ^oKONGOR ARENA ^g
#>

param(
    [string]$HostUsername = $env:HOST_USERNAME,
    [string]$HostPassword = $env:HOST_PASSWORD,
    [string]$AuthToken = $env:COMPEL_AUTHENTICATION_TOKEN,
    [string]$ServerPrefix = $env:COMPEL_SERVER_NAME_PREFIX,
    [string]$MasterServerAddress = $env:MASTER_SERVER_ADDRESS,
    [int]$Instances = 0,
    [string]$Location = $env:COMPEL_LOCATION,
    [int]$PortOffset = 100,
    [int]$ControlPort = 11334,
    [switch]$Rebuild = $false,
    [switch]$E2eHarness = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$LogDir = Join-Path $ProjectRoot "logs"
$LogFile = Join-Path $LogDir "docker_servers.log"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Log-Message([string]$msg) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] [DOCKER-FLEET] $msg"
    Write-Host $line -ForegroundColor Green
    Add-Content -Path $LogFile -Value $line
}

# Resolve settings from games/server/COMPEL.JSON if available and not overridden
$compelJsonPath = Join-Path $ProjectRoot "games\server\COMPEL.JSON"
if (-not (Test-Path $compelJsonPath)) {
    $compelJsonPath = Join-Path $ProjectRoot "games\server\COMPEL.json"
}
if (Test-Path $compelJsonPath) {
    try {
        $json = Get-Content $compelJsonPath -Raw | ConvertFrom-Json
        if (-not $HostUsername -and $json.UserName.Value -and $json.UserName.Value -ne "...") { $HostUsername = $json.UserName.Value }
        if (-not $HostPassword -and $json.Password.Value -and $json.Password.Value -ne "..." -and $json.Password.Value -notmatch "^YOUR_") { $HostPassword = $json.Password.Value }
        if (-not $AuthToken -and $json.AuthenticationToken.Value -and $json.AuthenticationToken.Value -ne "..." -and $json.AuthenticationToken.Value -notmatch "^YOUR_") { $AuthToken = $json.AuthenticationToken.Value }
        if (-not $ServerPrefix -and $json.ServerNamePrefix.Value -and $json.ServerNamePrefix.Value -notmatch "\[LOCAL\]") { $ServerPrefix = $json.ServerNamePrefix.Value }
        if ($Instances -eq 0 -and $json.Instances.Value) { $Instances = [int]$json.Instances.Value }
        if (-not $Location -and $json.Location.Value) { $Location = $json.Location.Value }
        if (-not $MasterServerAddress -and $json.MasterServerAddress.Value) { $MasterServerAddress = $json.MasterServerAddress.Value }
        if (-not $MasterServerAddress -and $json.Gateway.Value -and $json.Gateway.Value -notmatch "^(localhost|127\.0\.0\.1)$") { $MasterServerAddress = "http://$($json.Gateway.Value)" }
    } catch {
        Log-Message "Note: Could not parse ${compelJsonPath}: $($_.Exception.Message)"
    }
}
$userSecretsJsonPath = Join-Path $env:APPDATA "Microsoft\UserSecrets\00005072-6f6a-6563-7420-4b4f4e474f52\secrets.json"
if (Test-Path -LiteralPath $userSecretsJsonPath) {
    try {
        $uSecrets = Get-Content -LiteralPath $userSecretsJsonPath -Raw | ConvertFrom-Json
        if (-not $HostPassword) {
            foreach ($k in @("HOST_PASSWORD", "Compel:Password", "HostAccount:Password")) {
                if ($uSecrets.PSObject.Properties[$k] -and $uSecrets.$k) { $HostPassword = [string]$uSecrets.$k; break }
            }
        }
        if (-not $AuthToken) {
            foreach ($k in @("COMPEL_AUTHENTICATION_TOKEN", "Compel:AuthenticationToken", "COMPEL_CONTROL_PLANE_AUTHENTICATION_TOKEN")) {
                if ($uSecrets.PSObject.Properties[$k] -and $uSecrets.$k) { $AuthToken = [string]$uSecrets.$k; break }
            }
        }
    } catch {}
}

$localMasterConn = Get-NetTCPConnection -LocalPort 55555 -State Listen -ErrorAction SilentlyContinue
$e2eMasterConn = Get-NetTCPConnection -LocalPort 55655 -State Listen -ErrorAction SilentlyContinue
if ($localMasterConn) {
    $MasterServerAddress = "http://localhost:55555"
    Log-Message "Detected active Local Master Server on port 55555. Binding to http://localhost:55555."
} elseif ($e2eMasterConn) {
    $MasterServerAddress = "http://localhost:55655"
    Log-Message "Detected active E2E Master Server on port 55655. Binding to http://localhost:55655."
} elseif (-not $MasterServerAddress) {
    try {
        $secretsList = dotnet user-secrets list --id "00005072-6f6a-6563-7420-4b4f4e474f52" 2>$null
        foreach ($sLine in $secretsList) {
            if ($sLine -match "^\s*(MASTER_SERVER_ADDRESS|MasterServer:Address)\s*=\s*(.+)$") {
                $val = $matches[2].Trim()
                if ($val -match '^https?://(localhost|127\.0\.0\.1|host\.docker\.internal)(:|/|$)') {
                    $MasterServerAddress = $val
                    break
                }
            }
        }
    } catch {
        Write-Warning "Could not read MasterServer address from dotnet user-secrets: $($_.Exception.Message)"
    }
}
if (-not $MasterServerAddress) { $MasterServerAddress = "http://localhost:55655" }

# Adaptively normalize for Docker:
# If loopback (127.0.0.1 or localhost), route to host.docker.internal
# If external (e.g. master.example.com, api.kongor.net, or remote IP), pass directly!
$containerMasterAddress = $MasterServerAddress
if ($containerMasterAddress -match "://(localhost|127\.0\.0\.1)(:\d+)?") {
    $containerMasterAddress = $containerMasterAddress -replace "://(localhost|127\.0\.0\.1)", "://host.docker.internal"
} elseif ($containerMasterAddress -match "^(localhost|127\.0\.0\.1)(:\d+)?") {
    $containerMasterAddress = $containerMasterAddress -replace "^(localhost|127\.0\.0\.1)", "host.docker.internal"
}

$hostPrefix = if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_HOST_USER_PREFIX)) { $env:KONGOR_HOST_USER_PREFIX.Trim() } else { "kongor_host" }
if (-not $HostUsername -or $HostUsername -in @($hostPrefix, "${hostPrefix}_local", "YOUR_KONGOR_USERNAME")) { $HostUsername = "${hostPrefix}_dock" }
if (-not $HostPassword) { $HostPassword = "YOUR_KONGOR_PASSWORD" }
if (-not $AuthToken) { $AuthToken = "YOUR_COMPEL_AUTH_TOKEN" }
if (-not $ServerPrefix) { $ServerPrefix = "^c[DOCKER] ^oKONGOR ARENA ^g" }
if ($Instances -le 0) { $Instances = 2 }
if (-not $Location) { $Location = "USE" }

$hostLanIp = $env:COMPEL_GATEWAY
if ([string]::IsNullOrWhiteSpace($hostLanIp) -and $localMasterConn) {
    $hostLanIp = "127.0.0.1"
}
if ([string]::IsNullOrWhiteSpace($hostLanIp)) {
    $hostLanIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.InterfaceAlias -notlike "*vEthernet*" -and $_.IPAddress -notlike "169.254*" } | Select-Object -ExpandProperty IPAddress -First 1)
}
if (-not $hostLanIp) {
    $hostLanIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.IPAddress -notlike "169.254*" } | Select-Object -ExpandProperty IPAddress -First 1)
}
if (-not $hostLanIp) { $hostLanIp = "127.0.0.1" }

$firstGamePort = 11235 + $PortOffset
$lastGamePort = $firstGamePort + $Instances - 1
$firstVoicePort = 11735 + $PortOffset
$lastVoicePort = $firstVoicePort + $Instances - 1
$firstProxyPort = 21234 + $PortOffset
$lastProxyPort = $firstProxyPort + $Instances
$firstProxyVoicePort = 21735 + $PortOffset
$lastProxyVoicePort = $firstProxyVoicePort + $Instances - 1

Log-Message "========================================================"
Log-Message "Launching Docker Dedicated Match Server Fleet"
Log-Message "========================================================"
Log-Message "Host Account:       $HostUsername"
Log-Message "Advertised Gateway: $hostLanIp"
Log-Message "Port Offset:        $PortOffset"
Log-Message "Control Plane:      http://127.0.0.1:$ControlPort"
Log-Message "Master Server:      $MasterServerAddress (Container: $containerMasterAddress)"
Log-Message "Game Ports (UDP):   $firstGamePort - $lastGamePort"
Log-Message "Voice Ports (UDP):  $firstVoicePort - $lastVoicePort"
Log-Message "Proxy Ports (UDP):  $firstProxyPort - $lastProxyPort"
Log-Message "Voice Proxy (UDP):  $firstProxyVoicePort - $lastProxyVoicePort"
Log-Message "Server Prefix:      $ServerPrefix"
Log-Message "========================================================"

if ($Rebuild) {
    Log-Message "Building Docker image projectkongor/compel-dedicated:latest..."
    docker build -f (Join-Path $ProjectRoot "infra\docker\Dockerfile.dedicated") -t projectkongor/compel-dedicated:latest $ProjectRoot
}

$existing = docker ps -a -q -f "name=compel-dedicated-server"
if ($existing) {
    Log-Message "Stopping and removing existing compel-dedicated-server container ($existing)..."
    docker rm -f compel-dedicated-server | Out-Null
}

$serverPath = (Join-Path $ProjectRoot "games\server").Replace('\', '/')
$artefactsDir = Join-Path $ProjectRoot "artifacts\runtime"
if (-not (Test-Path -LiteralPath $artefactsDir)) {
    New-Item -ItemType Directory -Path $artefactsDir -Force | Out-Null
}
$artefactsPath = $artefactsDir.Replace('\', '/')
$entrypointPath = (Join-Path $ProjectRoot "infra\docker\entrypoint.sh").Replace('\', '/')

Log-Message "Running new compel-dedicated-server container..."
$containerId = docker run -d `
  --name compel-dedicated-server `
  --restart unless-stopped `
  --add-host=host.docker.internal:host-gateway `
  --health-cmd="curl -f http://127.0.0.1:${ControlPort}/ping || exit 1" `
  -p ${ControlPort}:${ControlPort} `
  -p ${ControlPort}:${ControlPort}/udp `
  -p ${firstGamePort}-${lastGamePort}:${firstGamePort}-${lastGamePort}/udp `
  -p ${firstVoicePort}-${lastVoicePort}:${firstVoicePort}-${lastVoicePort}/udp `
  -p ${firstProxyPort}-${lastProxyPort}:${firstProxyPort}-${lastProxyPort}/udp `
  -p ${firstProxyVoicePort}-${lastProxyVoicePort}:${firstProxyVoicePort}-${lastProxyVoicePort}/udp `
  --cap-add=SYS_NICE `
  -e MASTER_SERVER_ADDRESS="$containerMasterAddress" `
  -e INFRASTRUCTURE_GATEWAY="$hostLanIp" `
  -e Compel__Gateway="$hostLanIp" `
  -e Gateway="$hostLanIp" `
  -e COMPEL_GATEWAY="$hostLanIp" `
  -e COMPEL_PRIORITY_MODE=Background `
  -e COMPEL_FLEET_TIER=D `
  -e Compel__FleetTier=D `
  -e COMPEL_INSTANCES=$Instances `
  -e COMPEL_LOCATION="$Location" `
  -e COMPEL_SERVER_NAME_PREFIX="$ServerPrefix" `
  -e COMPEL_PORT_RANGE_OFFSET=$PortOffset `
  -e COMPEL_CONTROL_PLANE_PORT=$ControlPort `
  -e COMPEL_CONTROL_PLANE_BIND_ADDRESS="0.0.0.0" `
  -e COMPEL_USE_PROXY=false `
  -e HOST_USERNAME="$HostUsername" `
  -e "HOST_PASSWORD=$HostPassword" `
  -e COMPEL_AUTHENTICATION_TOKEN="$AuthToken" `
  -e KONGOR_E2E_HARNESS="$(& { if ($E2eHarness -or $env:KONGOR_E2E_HARNESS -eq '1') { '1' } else { '0' } })" `
  -v "${serverPath}:/app/server:ro" `
  -v "${artefactsPath}:/app/artefacts:rw" `
  -v "${entrypointPath}:/app/entrypoint.sh:ro" `
  projectkongor/compel-dedicated:latest

Log-Message "Container started: $containerId"
Start-Sleep -Seconds 3

$logs = docker logs compel-dedicated-server --tail 25
Log-Message "Recent container logs:"
Write-Host $logs -ForegroundColor Gray

Log-Message "Docker fleet launch complete!"
