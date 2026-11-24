# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Turnkey local K3s + Agones deployment script for ProjectKongor dedicated game servers.
.DESCRIPTION
    Creates a local lightweight K3s cluster using k3d, imports the compel-dedicated Docker image,
    installs Agones game server orchestrator via Helm, configures Port Offset 20 mappings,
    and deploys the Kongor Dedicated Fleet with dynamic standby buffer scaling.

.PARAMETER Teardown
    Deletes the local k3d cluster and cleans up resources.
.PARAMETER ClusterName
    Name of the k3d cluster (default: "kongor-k3s").
.PARAMETER Replicas
    Number of initial warm GameServer replicas (default: 2).
.PARAMETER ImageTag
    Docker image tag for COMPEL dedicated server (default: "projectkongor/compel-dedicated:latest").
#>

param(
    [switch]$Teardown,
    [string]$ClusterName = "kongor-k3s",
    [string]$HostUsername = $env:HOST_USERNAME,
    [string]$HostPassword = $env:HOST_PASSWORD,
    [string]$AuthToken = $env:COMPEL_AUTHENTICATION_TOKEN,
    [string]$ImageTag = "projectkongor/compel-dedicated:latest",
    [string]$MasterServerAddress = $env:MASTER_SERVER_ADDRESS,
    [int]$Replicas = 1,
    [switch]$SkipImageImport,
    [switch]$SkipAgonesInstall,
    [switch]$E2eHarness
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$LogDir = Join-Path $ProjectRoot "logs"
$LogFile = Join-Path $LogDir "setup_k3d_agones.log"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Log-Message([string]$msg, [string]$color = "Cyan") {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] [K3S-AGONES] $msg"
    Write-Host $line -ForegroundColor $color
    Add-Content -Path $LogFile -Value $line
}

Log-Message "======================================================================"
Log-Message "Project Kongor: Local K3s + Agones Dedicated Server Orchestration"
Log-Message "======================================================================"
Log-Message "Project Root: $ProjectRoot"

# Disable k3d automatic CoreDNS injection hang on Windows/WSL2
$env:K3D_FIX_DNS = "0"

# Ensure .kube config directory exists so k3d can persist kubeconfig
$kubeDir = Join-Path $env:USERPROFILE ".kube"
if (-not (Test-Path $kubeDir)) {
    New-Item -ItemType Directory -Path $kubeDir -Force | Out-Null
}

# Ensure WinGet and user tool directories are on current session PATH
$extraPaths = @(
    "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\k3d.k3d_Microsoft.Winget.Source_8wekyb3d8bbwe",
    "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Helm.Helm_Microsoft.Winget.Source_8wekyb3d8bbwe\windows-amd64",
    "$env:LOCALAPPDATA\Microsoft\WinGet\Links",
    "C:\Program Files\Docker\Docker\resources\bin"
)
foreach ($p in $extraPaths) {
    if ((Test-Path $p) -and ($env:PATH -notlike "*$p*")) {
        $env:PATH = "$p;$env:PATH"
    }
}


# Teardown handling
if ($Teardown) {
    Log-Message "Executing teardown for cluster '$ClusterName'..." "Yellow"
    $existing = & k3d cluster list -o json 2>$null | ConvertFrom-Json
    $found = $existing | Where-Object { $_.name -eq $ClusterName }
    if ($found) {
        & k3d cluster delete $ClusterName
        Log-Message "Cluster '$ClusterName' deleted successfully." "Green"
    } else {
        Log-Message "Cluster '$ClusterName' does not exist." "Yellow"
    }
    return
}

# Pre-Flight Checks
Log-Message "Performing pre-flight tool checks..."
$dockerCmd = Get-Command docker -ErrorAction SilentlyContinue
$k3dCmd = Get-Command k3d -ErrorAction SilentlyContinue
$helmCmd = Get-Command helm -ErrorAction SilentlyContinue
$kubectlCmd = Get-Command kubectl -ErrorAction SilentlyContinue

if (-not $dockerCmd) {
    throw "Docker CLI ('docker') not found in PATH. Please install/start Docker Desktop."
}
if (-not $k3dCmd) {
    throw "k3d CLI ('k3d') not found in PATH. Install via: winget install k3d.k3d"
}
if (-not $helmCmd) {
    throw "Helm CLI ('helm') not found in PATH. Install via: winget install Helm.Helm"
}
if (-not $kubectlCmd) {
    throw "kubectl CLI ('kubectl') not found in PATH."
}

# Verify Docker engine is running
try {
    $dockerVer = (& docker version --format '{{.Server.Version}}' 2>$null)
    if ([string]::IsNullOrWhiteSpace($dockerVer)) {
        throw "Docker daemon is not running. Please start Docker Desktop."
    }
    Log-Message "Docker Engine verified: v$dockerVer" "Green"
} catch {
    throw "Failed to communicate with Docker daemon: $($_.Exception.Message)"
}

# Verify Linux server assets existence
$linuxServerBin = Join-Path $ProjectRoot "games\server\linux-x64\hon-x86_64-server"
$linuxClientBin = Join-Path $ProjectRoot "games\server\linux-x64\hon-x86_64"
if (-not (Test-Path $linuxServerBin) -and -not (Test-Path $linuxClientBin)) {
    throw "Linux HoN server binary not found at '$linuxServerBin'. Please verify 'games/server/linux-x64' contains HoN Linux binaries."
}
Log-Message "Linux dedicated server assets verified at games/server/linux-x64." "Green"

# Ensure runtime artefacts directory exists
$artefactsDir = Join-Path $ProjectRoot "artifacts\runtime"
if (-not (Test-Path $artefactsDir)) {
    New-Item -ItemType Directory -Path $artefactsDir -Force | Out-Null
    Log-Message "Created runtime artefacts directory at '$artefactsDir'."
}

# Detect Host Gateway IP (On Windows Docker Desktop, 192.168.65.254 reaches the Windows host; on native Linux, bridge gateway)
$gatewayIp = "192.168.65.254"
if (-not ($env:OS -like "*Windows*" -or $IsWindows)) {
    try {
        $detectedGateway = & docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}' 2>$null
        if (-not [string]::IsNullOrWhiteSpace($detectedGateway)) {
            $gatewayIp = $detectedGateway.Trim()
        }
    } catch {
        Log-Message "Could not inspect docker bridge gateway, falling back to default: $gatewayIp" "Yellow"
    }
}
Log-Message "Target Host Gateway IP for pods: $gatewayIp"

# Normalize volume paths with forward slashes for k3d
$serverPathNormalized = (Join-Path $ProjectRoot "games\server").Replace("\", "/")
$artefactsPathNormalized = $artefactsDir.Replace("\", "/")

# Resolve Host Credentials from COMPEL.JSON if not provided
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
        if (-not $MasterServerAddress -and $json.MasterServerAddress.Value) { $MasterServerAddress = $json.MasterServerAddress.Value }
    } catch {
        Log-Message "Note: Could not parse ${compelJsonPath}: $($_.Exception.Message)" "Yellow"
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
if (-not $MasterServerAddress) {
    $secretAddress = dotnet user-secrets list --project "$ProjectRoot/src/apps/compel/COMPEL.csproj" 2>$null | Select-String "MASTER_SERVER_ADDRESS\s*=\s*(.+)" | ForEach-Object { $_.Matches.Groups[1].Value.Trim() }
    if ($secretAddress) {
        $MasterServerAddress = $secretAddress
    }
}
if (-not $MasterServerAddress) { $MasterServerAddress = "http://host.k3d.internal:55655" }
$hostPrefix = if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_HOST_USER_PREFIX)) { $env:KONGOR_HOST_USER_PREFIX.Trim() } else { "kongor_host" }
if (-not $HostUsername -or $HostUsername -in @($hostPrefix, "${hostPrefix}_local", "YOUR_KONGOR_USERNAME", "AnonymousHost")) { $HostUsername = "${hostPrefix}_k3s" }
if (-not $HostPassword) { $HostPassword = [System.Guid]::NewGuid().ToString("N") }
if (-not $AuthToken) { $AuthToken = [System.Guid]::NewGuid().ToString("N") }

# Check if k3d cluster already exists
$existingClusters = & k3d cluster list -o json 2>$null | ConvertFrom-Json
$clusterExists = $existingClusters | Where-Object { $_.name -eq $ClusterName }

if ($clusterExists) {
    Log-Message "k3d cluster '$ClusterName' already exists and is active." "Green"
} else {
    Log-Message "Creating k3d cluster '$ClusterName' with Port Offset 200 mappings..."
    Log-Message "  - HTTP Control Plane: Port 11434 -> NodePort 31434"
    Log-Message "  - Dedicated Game UDP: Ports 11435-11436 -> Node Ports 11435-11436/udp"
    Log-Message "  - Dedicated Voice UDP: Ports 11935-11936 -> Node Ports 11935-11936/udp"
    Log-Message "  - Server Volume:      $serverPathNormalized -> /opt/kongor/games/server"
    Log-Message "  - Artefacts Volume:   $artefactsPathNormalized -> /opt/kongor/artefacts"

    $k3dArgs = @(
        "cluster", "create", $ClusterName,
        "-i", "rancher/k3s:v1.30.5-k3s1",
        "--servers", "1",
        "--api-port", "6550",
        "-p", "11434:31434@server:0",
        "-p", "11435-11436:11435-11436/udp@server:0",
        "-p", "11935-11936:11935-11936/udp@server:0",
        "-v", "$($serverPathNormalized):/opt/kongor/games/server@server:0",
        "-v", "$($artefactsPathNormalized):/opt/kongor/artefacts@server:0",
        "--k3s-arg", "--disable=traefik@server:0",
        "--wait"
    )

    & k3d @k3dArgs
    if ($LASTEXITCODE -ne 0) {
        throw "k3d cluster create failed with exit code $LASTEXITCODE."
    }
    Log-Message "k3d cluster '$ClusterName' created successfully." "Green"
}

# Ensure kubeconfig uses 127.0.0.1 for local k3d API (host.docker.internal may resolve to LAN IP on Windows)
try {
    & kubectl config set-cluster "k3d-$ClusterName" --server="https://127.0.0.1:6550" 2>$null | Out-Null
} catch {}

# Verify kubectl context
& kubectl cluster-info
if ($LASTEXITCODE -ne 0) {
    throw "Failed to reach Kubernetes cluster via kubectl."
}

# Configure k3d load balancer NGINX to single worker to prevent UDP port-hopping
$serverlbContainer = "k3d-$ClusterName-serverlb"
try {
    Log-Message "Configuring load balancer '$serverlbContainer' NGINX to single worker process..."
    & docker exec $serverlbContainer sed -i "s/worker_processes auto;/worker_processes 1;/g" /etc/confd/templates/nginx.tmpl /etc/nginx/nginx.conf
    & docker exec $serverlbContainer nginx -s reload
    Log-Message "k3d load balancer NGINX successfully pinned to 1 worker process." "Green"
} catch {
    Log-Message "Warning: Could not configure k3d load balancer NGINX: $($_.Exception.Message)" "Yellow"
}

# Import Docker image into k3d cluster
if (-not $SkipImageImport) {
    Log-Message "Checking Docker image '$ImageTag'..."
    $imgExists = & docker image inspect $ImageTag 2>$null
    if (-not $imgExists) {
        throw "Docker image '$ImageTag' not found locally. Please build it first."
    }
    Log-Message "Importing Docker image '$ImageTag' into k3d cluster '$ClusterName'..."
    & k3d image import $ImageTag -c $ClusterName
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to import Docker image into k3d cluster."
    }
    Log-Message "Image imported into cluster successfully." "Green"
}

# Install / Upgrade Agones via Helm
$agonesValues = Join-Path $ProjectRoot "infra\k8s\agones\values-k3s.yaml"
if (-not $SkipAgonesInstall) {
    Log-Message "Configuring Agones Helm repository..."
    & helm repo add agones https://agones.dev/chart/stable 2>$null | Out-Null
    & helm repo update agones

    Log-Message "Installing / Upgrading Agones in namespace 'agones-system' using '$agonesValues'..."
    $helmArgs = @(
        "upgrade", "--install", "agones", "agones/agones",
        "--namespace", "agones-system",
        "--create-namespace",
        "-f", $agonesValues,
        "--server-side", "false",
        "--disable-openapi-validation",
        "--wait",
        "--timeout", "180s"
    )
    & helm @helmArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Agones Helm installation failed with exit code $LASTEXITCODE."
    }
    Log-Message "Agones installed successfully." "Green"

    Log-Message "Waiting for Agones controller deployment to be Available..."
    & kubectl wait --for=condition=Available deployment/agones-controller -n agones-system --timeout=120s
    Log-Message "Agones controller is ready." "Green"
}

# Create / Update Host Credentials Secret
Log-Message "Applying 'kongor-host-credentials' and 'kongor-k3s-host-credentials' secrets..."
foreach ($secretName in @("kongor-host-credentials", "kongor-k3s-host-credentials")) {
    $secretYaml = & kubectl create secret generic $secretName `
        "--from-literal=username=$HostUsername" `
        "--from-literal=password=$HostPassword" `
        "--from-literal=token=$AuthToken" `
        --dry-run=client -o yaml
    $secretYaml | & kubectl apply -f -
}
Log-Message "Credentials secrets configured." "Green"

# Deploy Agones Fleet Manifest
$fleetFile = Join-Path $ProjectRoot "infra\k8s\agones\fleet.yaml"
$autoscalerFile = Join-Path $ProjectRoot "infra\k8s\agones\fleet-autoscaler.yaml"

Log-Message "Preparing Fleet manifest with Gateway IP '$gatewayIp' and Replicas '$Replicas'..."
$fleetContent = Get-Content $fleetFile -Raw
# Dynamically replace gateway IP if different
$fleetContent = $fleetContent -replace 'ip:\s*"\d+\.\d+\.\d+\.\d+"', "ip: `"$gatewayIp`""
$fleetContent = $fleetContent -replace 'replicas:\s*\d+', "replicas: $Replicas"

# Adapt Master Server Address (loopback -> host.k3d.internal, external -> direct)
if ($MasterServerAddress) {
    if ($MasterServerAddress -match "^(https?:\/\/)?(127\.0\.0\.1|localhost)(:\d+)?") {
        $portSuffix = if ($matches[3]) { $matches[3] } else { ":55555" }
        $k3sMasterServer = "http://host.k3d.internal$portSuffix"
    } else {
        $k3sMasterServer = if ($MasterServerAddress -notmatch "^https?:\/\/") { "http://$MasterServerAddress" } else { $MasterServerAddress }
    }
    $k3sMasterHost = $k3sMasterServer -replace "^https?:\/\/", ""
    $fleetContent = $fleetContent -replace 'value:\s*"host\.k3d\.internal:55555"', "value: `"$k3sMasterHost`""
    $fleetContent = $fleetContent -replace 'value:\s*"http:\/\/host\.k3d\.internal:55555"', "value: `"$k3sMasterServer`""
    Log-Message "Configured Fleet Master Server Target: $k3sMasterServer" "Cyan"
}

if ($E2eHarness -or $env:KONGOR_E2E_HARNESS -eq "1") {
    $e2eEnvBlock = "- name: COMPEL_PREBAKED_IMAGE`n                  value: `"true`"`n                - name: KONGOR_E2E_HARNESS`n                  value: `"1`""
    $fleetContent = $fleetContent -replace '-\s*name:\s*COMPEL_PREBAKED_IMAGE\s*\r?\n\s*value:\s*"true"', $e2eEnvBlock
    Log-Message "Injected KONGOR_E2E_HARNESS=1 into ephemeral fleet.local.yaml overlay." "Cyan"
}

# Apply dynamically via temp file
$tempFleet = Join-Path $ProjectRoot "infra\k8s\agones\fleet.local.yaml"
Set-Content -Path $tempFleet -Value $fleetContent -Encoding utf8
try {
    Log-Message "Applying Fleet manifest: $tempFleet"
    & kubectl apply -f $tempFleet
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to apply Agones Fleet manifest."
    }

    if (Test-Path $autoscalerFile) {
        Log-Message "Applying FleetAutoscaler manifest: $autoscalerFile"
        & kubectl apply -f $autoscalerFile
    }
} finally {
    if (Test-Path $tempFleet) {
        Remove-Item $tempFleet -Force -ErrorAction SilentlyContinue
    }
}

# Monitor Rollout & Readiness
Log-Message "Monitoring GameServer pool readiness (waiting up to 120s)..."
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$isReady = $false

while ($stopwatch.Elapsed.TotalSeconds -lt 120) {
    Start-Sleep -Seconds 5
    $gsOutput = & kubectl get gameservers -o json 2>$null
    if ($gsOutput) {
        $gsList = $gsOutput | ConvertFrom-Json
        $readyCount = 0
        $totalCount = 0
        if ($gsList.items) {
            $totalCount = $gsList.items.Count
            foreach ($item in $gsList.items) {
                $state = $item.status.state
                if ($state -eq "Ready" -or $state -eq "Allocated") {
                    $readyCount++
                }
            }
        }
        Log-Message "GameServers: $readyCount / $totalCount Ready (Elapsed: $([int]$stopwatch.Elapsed.TotalSeconds)s)..."
        if ($readyCount -ge 1) {
            $isReady = $true
            break
        }
    }
}

Log-Message "======================================================================"
Log-Message "Deployment Status Summary"
Log-Message "======================================================================"
& kubectl get gameservers -o wide
Write-Host ""
& kubectl get pods -l app=kongor-dedicated -o wide
Write-Host ""
& kubectl get svc kongor-dedicated-service -o wide

# Test local HTTP health endpoint
Write-Host ""
Log-Message "Testing Control Plane HTTP endpoint at http://127.0.0.1:11434/health..."
try {
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:11434/health" -Method Get -TimeoutSec 5 -ErrorAction Stop
    Log-Message "Health Check SUCCESS: $($resp | ConvertTo-Json -Compress)" "Green"
} catch {
    Log-Message "Health Check Notice: Could not reach http://127.0.0.1:11434/health directly yet ($($_.Exception.Message)). Worker may still be completing warmup." "Yellow"
}

Log-Message "======================================================================"
Log-Message "PROJECT KONGOR K3S + AGONES FLEET ACTIVE!" "Green"
Log-Message "  - Fleet Type:         K3s / Agones Cloud-Native"
Log-Message "  - HTTP Control Plane: http://127.0.0.1:11434 (NodePort 31434)"
Log-Message "  - Game UDP Ports:     11435 - 11436 (Mapped 1:1 via Agones Dynamic)"
Log-Message "  - Voice UDP Ports:    11935 - 11936"
Log-Message "  - NEXUS GUI Badge:    ☸️ K3S"
Log-Message "  - Teardown Command:   powershell -File infra/k8s/setup_local_k3d_agones.ps1 -Teardown"
Log-Message "======================================================================"
