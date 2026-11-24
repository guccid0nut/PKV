# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Launches a Separate Queue Mini-Game ("While You Wait" Arcade) COMPEL Fleet alongside Regular Match Servers.
.DESCRIPTION
    Runs COMPEL in QueueArcadeMode on isolated UDP ports (default PortOffset = 250) so it never collides
    with local Ranked/Public game servers (PortOffset = 0, ControlPort = 11234):
    - Control Plane:      http://127.0.0.1:11484
    - Ping Responder:     UDP 11484 (Public Proxy: 21484)
    - Game Ports:         UDP 11485.. (Public Proxy: 21485..)
    - Voice Ports:        UDP 11985.. (Public Proxy: 21985..)
    - Queue Arcade Flags: sv_afkTimeout=0, svr_recordReplay=false, KONGOR_QUEUE_ARCADE=1, Ranked-Isolated
#>

param(
    [string]$HostUsername = $env:HOST_USERNAME,
    [string]$HostPassword = $env:HOST_PASSWORD,
    [string]$AuthToken = $env:COMPEL_AUTHENTICATION_TOKEN,
    [string]$ServerPrefix = "QUEUE ARCADE",
    [int]$Instances = 2,
    [int]$WarmInstancesTarget = 1,
    [string]$Location = $env:COMPEL_LOCATION,
    [int]$PortOffset = 250,
    [int]$ControlPort = 11484,
    [int]$RoundDurationSeconds = 600,
    [string]$MasterServerAddress = $env:MASTER_SERVER_ADDRESS,
    [switch]$Background = $false,
    [switch]$Stop = $false,
    [switch]$Status = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ServerDir = Join-Path $ProjectRoot "games\hon server"
if (-not (Test-Path $ServerDir)) {
    $rawServer = Join-Path $ProjectRoot "games\server"
    New-Item -ItemType Junction -Path $ServerDir -Target $rawServer | Out-Null
}
$LogDir = Join-Path $ProjectRoot "logs"
$LogFile = Join-Path $LogDir "queue_minigame_servers.log"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Log-ArcadeServerMessage([string]$msg) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] [QUEUE-ARCADE-FLEET] $msg"
    Write-Host $line -ForegroundColor Magenta
    Add-Content -Path $LogFile -Value $line
}

$minArcadeGamePort = 11235 + $PortOffset
$maxArcadeGamePort = $minArcadeGamePort + [Math]::Max($Instances, 16) - 1

if ($Status -or $Stop) {
    $controlListener = Get-NetTCPConnection -LocalPort $ControlPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    $arcadeCompelPids = @()
    if ($controlListener -and $controlListener.OwningProcess -gt 0) {
        $arcadeCompelPids += [int]$controlListener.OwningProcess
    }
    $wmiCompels = Get-CimInstance Win32_Process -Filter "Name = 'COMPEL.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "QueueArcadeMode=true|ControlPlanePort=$ControlPort|FleetTier=Q" }
    foreach ($c in $wmiCompels) {
        if ($arcadeCompelPids -notcontains [int]$c.ProcessId) {
            $arcadeCompelPids += [int]$c.ProcessId
        }
    }

    $arcadeSlaveProcs = Get-CimInstance Win32_Process -Filter "Name = 'hon_x64.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            ($arcadeCompelPids -contains [int]$_.ParentProcessId) -or
            ($_.CommandLine -match "QUEUE ARCADE") -or
            ($_.CommandLine -match "svr_port\s+(\d+)" -and [int]$Matches[1] -ge $minArcadeGamePort -and [int]$Matches[1] -le $maxArcadeGamePort)
        }

    if ($Status) {
        Log-ArcadeServerMessage "Status check (ControlPort=$ControlPort, GamePorts=$minArcadeGamePort..$maxArcadeGamePort): COMPEL PIDs=[$($arcadeCompelPids -join ', ')], Slave hon_x64 PIDs=[$(($arcadeSlaveProcs | Select-Object -ExpandProperty ProcessId) -join ', ')]"
        exit 0
    }

    Log-ArcadeServerMessage "Stopping Queue Arcade fleet on ControlPort=$ControlPort (COMPEL PIDs=[$($arcadeCompelPids -join ', ')], Slave PIDs=[$(($arcadeSlaveProcs | Select-Object -ExpandProperty ProcessId) -join ', ')])..."
    foreach ($compelPid in $arcadeCompelPids) {
        $proc = Get-Process -Id $compelPid -ErrorAction SilentlyContinue
        if ($proc) {
            Stop-Process -Id $compelPid -Force
            Log-ArcadeServerMessage "Stopped Queue Arcade COMPEL (PID $compelPid)."
        }
    }
    foreach ($slave in $arcadeSlaveProcs) {
        $sProc = Get-Process -Id $slave.ProcessId -ErrorAction SilentlyContinue
        if ($sProc) {
            Stop-Process -Id $slave.ProcessId -Force
            Log-ArcadeServerMessage "Stopped Queue Arcade dedicated slave hon_x64.exe (PID $($slave.ProcessId))."
        }
    }
    $lockFile = Join-Path $ServerDir "COMPEL.lock"
    if (Test-Path $lockFile) {
        $remainingCompels = Get-CimInstance Win32_Process -Filter "Name = 'COMPEL.exe'" -ErrorAction SilentlyContinue
        if (-not $remainingCompels) {
            Remove-Item -Path $lockFile -Force -ErrorAction Stop
            Log-ArcadeServerMessage "Cleared stale COMPEL.lock after stopping Queue Arcade COMPEL."
        }
    }
    Log-ArcadeServerMessage "Queue Arcade fleet stopped cleanly (Ranked servers untouched)."
    exit 0
}

# Inherit host credentials from games/server/COMPEL.JSON if not explicitly supplied
$compelJsonPath = Join-Path $ServerDir "COMPEL.JSON"
if (-not (Test-Path $compelJsonPath)) {
    $compelJsonPath = Join-Path $ServerDir "COMPEL.json"
}
if (Test-Path $compelJsonPath) {
    try {
        $json = Get-Content $compelJsonPath -Raw | ConvertFrom-Json
        if (-not $HostUsername -and $json.UserName.Value) { $HostUsername = $json.UserName.Value }
        if (-not $HostPassword -and $json.Password.Value) { $HostPassword = $json.Password.Value }
        if (-not $AuthToken -and $json.AuthenticationToken.Value) { $AuthToken = $json.AuthenticationToken.Value }
        if (-not $Location -and $json.Location.Value) { $Location = $json.Location.Value }
        if (-not $MasterServerAddress -and $json.MasterServerAddress.Value) { $MasterServerAddress = $json.MasterServerAddress.Value }
    } catch {
        Log-ArcadeServerMessage "Note: Could not parse ${compelJsonPath}: $($_.Exception.Message)"
    }
}

if (-not $MasterServerAddress) { $MasterServerAddress = "http://127.0.0.1:55555" }
$hostPrefix = if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_HOST_USER_PREFIX)) { $env:KONGOR_HOST_USER_PREFIX.Trim() } else { "kongor_host" }
if (-not $HostUsername -or $HostUsername -eq "YOUR_KONGOR_USERNAME" -or $HostUsername -eq $hostPrefix) {
    $HostUsername = "${hostPrefix}_arc"
} elseif ($HostUsername -notmatch "_arc$") {
    $baseStem = $HostUsername -replace '(_arc|_arcade|_local|_dock|_docker|_k3s)$', ''
    if ($baseStem.Length -gt 11) { $baseStem = $baseStem.Substring(0, 11).TrimEnd('_') }
    $HostUsername = "${baseStem}_arc"
}
if (-not $ServerPrefix -or $ServerPrefix -notmatch "QUEUE ARCADE") { $ServerPrefix = "QUEUE ARCADE $ServerPrefix".Trim() }
if ($Instances -le 0) { $Instances = 2 }
if ($WarmInstancesTarget -le 0 -or $WarmInstancesTarget -gt $Instances) { $WarmInstancesTarget = 1 }
if (-not $Location) { $Location = "USE" }

Log-ArcadeServerMessage "========================================================"
Log-ArcadeServerMessage "Starting Queue Mini-Game ('While You Wait' Arcade) Fleet"
Log-ArcadeServerMessage "========================================================"
Log-ArcadeServerMessage "Host Account:       $HostUsername"
Log-ArcadeServerMessage "Location (Region):  $Location"
Log-ArcadeServerMessage "Instances:          $Instances"
Log-ArcadeServerMessage "Port Offset:        $PortOffset"
Log-ArcadeServerMessage "Control Plane:      http://127.0.0.1:$ControlPort"
Log-ArcadeServerMessage "Master Server:      $MasterServerAddress"
Log-ArcadeServerMessage "Game Ports (UDP):   $(11235 + $PortOffset)..$(11235 + $PortOffset + $Instances - 1)"
Log-ArcadeServerMessage "Voice Ports (UDP):  $(11735 + $PortOffset)..$(11735 + $PortOffset + $Instances - 1)"
Log-ArcadeServerMessage "Proxy Ports (UDP):  $(21235 + $PortOffset)..$(21235 + $PortOffset + $Instances - 1)"
Log-ArcadeServerMessage "Round Duration:     ${RoundDurationSeconds}s"
Log-ArcadeServerMessage "Server Prefix:      $ServerPrefix"
Log-ArcadeServerMessage "========================================================"

$activeControl = Get-NetTCPConnection -LocalPort $ControlPort -ErrorAction SilentlyContinue
if ($activeControl) {
    Log-ArcadeServerMessage "WARNING: Control port $ControlPort is already listening (PID $($activeControl.OwningProcess)). Stop existing arcade fleet or pass -ControlPort <port>."
}

$isLoopback = $MasterServerAddress -match "^(https?:\/\/)?(127\.0\.0\.1|localhost)"
if ($isLoopback) {
    $tcp = Test-NetConnection -ComputerName "127.0.0.1" -Port 55555 -WarningAction SilentlyContinue
    if (-not $tcp.TcpTestSucceeded) {
        Log-ArcadeServerMessage "WARNING: Local Master Server at 127.0.0.1:55555 is unreachable. Start the cluster first (e.g. scripts/start_local_cluster.ps1)."
    }
}

if ($HostUsername) {
    $env:COMPEL_USER_NAME = $HostUsername
    $env:HOST_USERNAME = $HostUsername
}
if ($HostPassword) {
    $env:HOST_PASSWORD = $HostPassword
}
if ($AuthToken) {
    $env:COMPEL_AUTHENTICATION_TOKEN = $AuthToken
}
$env:COMPEL_INSTANCES = "$Instances"
$env:COMPEL_WARM_INSTANCES_TARGET = "$WarmInstancesTarget"
$env:COMPEL_FLEET_TIER = "Q"
$env:COMPEL_QUEUE_ARCADE_MODE = "true"
$env:COMPEL_LOCATION = $Location
$env:COMPEL_SERVER_NAME_PREFIX = $ServerPrefix
$env:COMPEL_PORT_RANGE_OFFSET = "$PortOffset"
$env:COMPEL_CONTROL_PLANE_PORT = "$ControlPort"
$env:COMPEL_USE_PROXY = "false"
$env:COMPEL_GATEWAY = "127.0.0.1"
$env:KONGOR_QUEUE_ARCADE = "1"
$env:MASTER_SERVER_ADDRESS = $MasterServerAddress
$env:Compel__MasterServerAddress = $MasterServerAddress
$env:COMPEL_INSTALLATION_DIRECTORY = $ServerDir

$compelCandidates = @(
    (Join-Path $ProjectRoot "src\apps\compel\bin\Debug\net11.0\COMPEL.exe"),
    (Join-Path $ProjectRoot "src\apps\compel\bin\Release\net11.0\COMPEL.exe"),
    (Join-Path $ServerDir "COMPEL.exe")
) | Where-Object { Test-Path $_ } | Sort-Object {
    $dllPath = [System.IO.Path]::ChangeExtension($_, ".dll")
    if (Test-Path $dllPath) { (Get-Item $dllPath).LastWriteTimeUtc } else { (Get-Item $_).LastWriteTimeUtc }
} -Descending
$compelExe = $compelCandidates | Select-Object -First 1
if (-not (Test-Path $compelExe)) {
    Log-ArcadeServerMessage "COMPEL binary not found; building src/apps/compel/COMPEL.csproj via Invoke-KongorTestLock.ps1..."
    & (Join-Path $ProjectRoot "scripts\Invoke-KongorTestLock.ps1") -Owner "launch-queue-arcade" -- dotnet build (Join-Path $ProjectRoot "src\apps\compel\COMPEL.csproj") -c Release
    $compelExe = Join-Path $ProjectRoot "src\apps\compel\bin\Release\net11.0\COMPEL.exe"
}

$compelArgs = @(
    "--Compel:InstallationDirectory=`"$ServerDir`"",
    "--Compel:Gateway=127.0.0.1",
    "--Compel:MasterServerAddress=`"$MasterServerAddress`"",
    "--Compel:UseProxy=false",
    "--Compel:Instances=$Instances",
    "--Compel:WarmInstancesTarget=$WarmInstancesTarget",
    "--Compel:FleetTier=Q",
    "--Compel:Location=`"$Location`"",
    "--Compel:ServerNamePrefix=`"$ServerPrefix`"",
    "--Compel:PortRangeOffset=$PortOffset",
    "--Compel:ControlPlanePort=$ControlPort",
    "--Compel:QueueArcadeMode=true",
    "--Compel:QueueArcadeRoundDurationSeconds=$RoundDurationSeconds",
    "--HostAccount:UserName=`"$HostUsername`""
)

Log-ArcadeServerMessage "Launching Queue Arcade COMPEL: $compelExe $($compelArgs -join ' ')"

if ($Background) {
    $cmdLine = "`"$compelExe`" " + ($compelArgs -join " ")
    $wmi = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $cmdLine
        CurrentDirectory = $ServerDir
    } -ErrorAction SilentlyContinue
    if ($wmi -and $wmi.ReturnValue -eq 0 -and $wmi.ProcessId) {
        Log-ArcadeServerMessage "Queue Arcade COMPEL started in detached background with PID: $($wmi.ProcessId)"
    } else {
        $proc = Start-Process -FilePath $compelExe -ArgumentList ($compelArgs -join " ") -WorkingDirectory $ServerDir -PassThru -WindowStyle Hidden
        Log-ArcadeServerMessage "Queue Arcade COMPEL started in background with PID: $($proc.Id)"
    }
} else {
    Push-Location $ServerDir
    try {
        & $compelExe @compelArgs
    } finally {
        Pop-Location
    }
}
