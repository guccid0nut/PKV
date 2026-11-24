# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Turnkey deploy script for K3s Kubernetes COMPEL Dedicated Servers (Fleet: K3S).
.DESCRIPTION
    Applies Kubernetes manifests for configured host account and Port Offset 200:
    - Control Plane: http://<NodeIP>:11434 (NodePort 31434)
    - Game Ports: UDP 11435, 11436
    - Voice Ports: UDP 11935, 11936
    - Public Proxy Ports: UDP 21435, 21436 (Voice Proxy: 21935, 21936)
    - Server Prefix: ^m[K3S] ^oKONGOR ARENA ^r
#>

param(
    [string]$HostUsername = $env:HOST_USERNAME,
    [string]$HostPassword = $env:HOST_PASSWORD,
    [string]$AuthToken = $env:COMPEL_AUTHENTICATION_TOKEN,
    [string]$ServerPrefix = $env:COMPEL_SERVER_NAME_PREFIX,
    [int]$PortOffset = 200,
    [int]$ControlPort = 11434,
    [string]$Kubeconfig = "",
    [switch]$ImportImage
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$LogDir = Join-Path $ProjectRoot "logs"
$LogFile = Join-Path $LogDir "k3s_servers.log"
$Manifest = Join-Path $ProjectRoot "infra\k8s\compel-k3s.yaml"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Log-Message([string]$msg) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] [K3S-FLEET] $msg"
    Write-Host $line -ForegroundColor Magenta
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
        if (-not $HostUsername -and $json.UserName.Value) { $HostUsername = $json.UserName.Value }
        if (-not $HostPassword -and $json.Password.Value) { $HostPassword = $json.Password.Value }
        if (-not $AuthToken -and $json.AuthenticationToken.Value) { $AuthToken = $json.AuthenticationToken.Value }
        if (-not $ServerPrefix -and $json.ServerNamePrefix.Value) { $ServerPrefix = $json.ServerNamePrefix.Value }
    } catch {
        Log-Message "Note: Could not parse ${compelJsonPath}: $($_.Exception.Message)"
    }
}

$hostPrefix = if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_HOST_USER_PREFIX)) { $env:KONGOR_HOST_USER_PREFIX } else { "kongor_host" }
if (-not $HostUsername -or $HostUsername -in @($hostPrefix, "${hostPrefix}_local", "${hostPrefix}_dock", "YOUR_KONGOR_USERNAME")) { $HostUsername = "${hostPrefix}_k3s" }
if (-not $ServerPrefix) { $ServerPrefix = "^m[K3S] ^oKONGOR ARENA ^r" }
$displayUser = if ($HostUsername) { $HostUsername } else { "(from manifest / secrets)" }

$detectedHostIp = $env:COMPEL_GATEWAY
if ([string]::IsNullOrWhiteSpace($detectedHostIp)) {
    try {
        $lanCandidate = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.PrefixOrigin -ne "WellKnown" } |
            Select-Object -First 1 -ExpandProperty IPAddress
        if (-not [string]::IsNullOrWhiteSpace($lanCandidate)) {
            $detectedHostIp = $lanCandidate
        }
    } catch {
        # Fallback to AUTO inside pod
    }
}
if ([string]::IsNullOrWhiteSpace($detectedHostIp)) { $detectedHostIp = "AUTO" }

Log-Message "========================================================"
Log-Message "Deploying K3s Kubernetes Match Server Fleet"
Log-Message "========================================================"
Log-Message "Host Account:       $displayUser"
Log-Message "Advertised Gateway: $detectedHostIp"
Log-Message "Port Offset:        $PortOffset"
Log-Message "Control Plane:      Port $ControlPort (NodePort 31434) + Ping UDP $(11234 + $PortOffset)"
Log-Message "Game Ports (UDP):   $(11235 + $PortOffset), $(11235 + $PortOffset + 1)"
Log-Message "Voice Ports (UDP):  $(11735 + $PortOffset), $(11735 + $PortOffset + 1)"
Log-Message "Proxy Ports (UDP):  $(21235 + $PortOffset), $(21235 + $PortOffset + 1)"
Log-Message "Server Prefix:      $ServerPrefix"
Log-Message "Manifest:           $Manifest"
Log-Message "========================================================"

# Auto-start k3d cluster if available and stopped
$k3d = Get-Command k3d -ErrorAction SilentlyContinue
if ($k3d) {
    try {
        $clusterStatus = k3d cluster list kongor-k3s --output json 2>$null | ConvertFrom-Json
        if ($clusterStatus -and ($clusterStatus.serversRunning -eq 0 -or $clusterStatus.running -eq 0)) {
            Log-Message "k3d cluster 'kongor-k3s' is stopped. Auto-starting..."
            k3d cluster start kongor-k3s
            Start-Sleep -Seconds 3
        }
    } catch {
        # Fallback if cluster list fails
    }

    if ($ImportImage) {
        Log-Message "Importing Docker image 'projectkongor/compel-dedicated:latest' into k3d cluster 'kongor-k3s'..."
        k3d image import projectkongor/compel-dedicated:latest -c kongor-k3s
    }
}

# Check if kubectl is available
$kubectl = Get-Command kubectl -ErrorAction SilentlyContinue
if (-not $kubectl) {
    Log-Message "ERROR: 'kubectl' command not found in PATH."
    Log-Message "Please install kubectl or run this script inside your K3s cluster environment."
    return
}

# Dynamically apply host credentials secret if provided
if ($HostUsername -and $HostPassword) {
    $tokenVal = if ($AuthToken) { $AuthToken } else {
        $ephemeralToken = [System.Guid]::NewGuid().ToString("N")
        Log-Message "WARN: COMPEL_AUTHENTICATION_TOKEN / -AuthToken not supplied; generated ephemeral random token for K3s secret."
        $ephemeralToken
    }
    foreach ($secretName in @("kongor-host-credentials", "kongor-k3s-host-credentials")) {
        $secretArgs = @("create", "secret", "generic", $secretName,
            "--from-literal=username=$HostUsername",
            "--from-literal=password=$HostPassword",
            "--from-literal=token=$tokenVal",
            "--dry-run=client", "-o", "yaml")
        if (-not [string]::IsNullOrWhiteSpace($Kubeconfig)) {
            $secretArgs += @("--kubeconfig", $Kubeconfig)
        }
        Log-Message "Creating / Updating $secretName secret for '$HostUsername'..."
        & kubectl @secretArgs | & kubectl apply -f -
    }
}

$agonesManifest = Join-Path $ProjectRoot "infra\k8s\agones\fleet.yaml"
$kubeArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Kubeconfig)) {
    $kubeArgs += @("--kubeconfig", $Kubeconfig)
}

$hasAgones = $false
try {
    & kubectl @kubeArgs get crd fleets.agones.dev -o name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0 -and (Test-Path $agonesManifest)) {
        $hasAgones = $true
    }
} catch {}

if ($hasAgones) {
    Log-Message "Detected Agones CRD in K3s cluster. Applying Agones Fleet manifest: $agonesManifest"
    & kubectl @kubeArgs delete deployment compel-k3s-server --ignore-not-found=true 2>$null | Out-Null
    & kubectl @kubeArgs apply -f $agonesManifest
    Log-Message "Recycling Agones GameServer pod(s) to pick up latest image, secrets, and overrides..."
    & kubectl @kubeArgs delete pod -l agones.dev/fleet=kongor-dedicated-fleet --ignore-not-found=true
    Start-Sleep -Seconds 3
    Log-Message "Agones Fleet & GameServers:"
    & kubectl @kubeArgs get fleet,gs,pods -l app=kongor-dedicated -o wide
    Log-Message "Service status:"
    & kubectl @kubeArgs get svc kongor-dedicated-service -o wide
} else {
    $cmdArgs = @("apply", "-f", $Manifest) + $kubeArgs
    $kubectlEnvArgs = @("set", "env", "deployment/compel-k3s-server", "COMPEL_GATEWAY=$detectedHostIp", "Compel__Gateway=$detectedHostIp", "COMPEL_SERVER_NAME_PREFIX=$ServerPrefix") + $kubeArgs

    Log-Message "Executing: kubectl $($cmdArgs -join ' ')"
    & kubectl @cmdArgs
    Log-Message "Synchronizing K3s fleet advertised gateway: COMPEL_GATEWAY=$detectedHostIp"
    & kubectl @kubectlEnvArgs

    Log-Message "Waiting for rollout..."
    & kubectl @kubeArgs rollout status deployment/compel-k3s-server --timeout=60s

    Log-Message "Current pods:"
    & kubectl @kubeArgs get pods -l app=compel-k3s -o wide

    Log-Message "Service status:"
    & kubectl @kubeArgs get svc compel-k3s-service -o wide
}

Log-Message "K3s fleet deployment complete!"
