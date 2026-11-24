# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Launches Local Windows Native COMPEL Dedicated Match Servers (Fleet: LOCAL).
.DESCRIPTION
    Runs COMPEL on Windows using configured host account at Port Offset 0:
    - Control Plane: http://127.0.0.1:11234
    - Game Ports: UDP 11235, 11236
    - Voice Ports: UDP 11735, 11736
    - Public Proxy Ports: UDP 21235, 21236
#>

param(
    [string]$HostUsername = $env:HOST_USERNAME,
    [string]$HostPassword = $env:HOST_PASSWORD,
    [string]$AuthToken = $env:COMPEL_AUTHENTICATION_TOKEN,
    [string]$ServerPrefix = $env:COMPEL_SERVER_NAME_PREFIX,
    [int]$Instances = 0,
    [string]$Location = $env:COMPEL_LOCATION,
    [int]$PortOffset = 0,
    [int]$ControlPort = 11234,
    [string]$MasterServerAddress = $env:MASTER_SERVER_ADDRESS,
    [switch]$Background = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ServerDir = Join-Path $ProjectRoot "games\hon server"
if (-not (Test-Path $ServerDir)) {
    $rawServer = Join-Path $ProjectRoot "games\server"
    New-Item -ItemType Junction -Path $ServerDir -Target $rawServer | Out-Null
}
$LogDir = Join-Path $ProjectRoot "logs"
$LogFile = Join-Path $LogDir "local_servers.log"

if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Log-Message([string]$msg) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "[$timestamp] [LOCAL-FLEET] $msg"
    Write-Host $line -ForegroundColor Cyan
    Add-Content -Path $LogFile -Value $line
}

# Resolve settings from games/server/COMPEL.JSON if available and not overridden
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
        if (-not $ServerPrefix -and $json.ServerNamePrefix.Value) { $ServerPrefix = $json.ServerNamePrefix.Value }
        if ($Instances -eq 0 -and $json.Instances.Value) { $Instances = [int]$json.Instances.Value }
        if (-not $Location -and $json.Location.Value) { $Location = $json.Location.Value }
        if ($ControlPort -eq 11234 -and $json.ControlPlanePort.Value) { $ControlPort = [int]$json.ControlPlanePort.Value }
        if ($PortOffset -eq 0 -and $json.PortRangeOffset.Value) { $PortOffset = [int]$json.PortRangeOffset.Value }
        if (-not $MasterServerAddress -and $json.MasterServerAddress.Value) { $MasterServerAddress = $json.MasterServerAddress.Value }
    } catch {
        Log-Message "Note: Could not parse ${compelJsonPath}: $($_.Exception.Message)"
    }
}

if (-not $MasterServerAddress) { $MasterServerAddress = "http://127.0.0.1:55555" }

$hostPrefix = if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_HOST_USER_PREFIX)) { $env:KONGOR_HOST_USER_PREFIX.Trim() } else { "kongor_host" }
if (-not $HostUsername -or $HostUsername -eq $hostPrefix -or $HostUsername -eq "YOUR_KONGOR_USERNAME") { $HostUsername = "${hostPrefix}_local" }
if (-not $ServerPrefix) { $ServerPrefix = "^oKONGOR ARENA ^g" }
if ($Instances -le 0) { $Instances = 2 }
if (-not $Location) { $Location = "USE" }
$displayUser = if ($HostUsername) { $HostUsername } else { "(from secrets / COMPEL.JSON)" }

Log-Message "========================================================"
Log-Message "Starting Local Windows Dedicated Match Servers"
Log-Message "========================================================"
Log-Message "Host Account:       $displayUser"
Log-Message "Port Offset:        $PortOffset"
Log-Message "Control Plane:      http://127.0.0.1:$ControlPort"
Log-Message "Master Server:      $MasterServerAddress"
Log-Message "Game Ports (UDP):   $(11235 + $PortOffset), $(11235 + $PortOffset + $Instances - 1)"
Log-Message "Voice Ports (UDP):  $(11735 + $PortOffset), $(11735 + $PortOffset + $Instances - 1)"
Log-Message "Proxy Ports (UDP):  $(21235 + $PortOffset), $(21235 + $PortOffset + $Instances - 1)"
Log-Message "Server Prefix:      $ServerPrefix"
Log-Message "Server Directory:   $ServerDir"
Log-Message "========================================================"

# Check if port $ControlPort is already listening
$activeControl = Get-NetTCPConnection -LocalPort $ControlPort -ErrorAction SilentlyContinue
if ($activeControl) {
    Log-Message "WARNING: Port $ControlPort is already in use by PID $($activeControl.OwningProcess). Another COMPEL instance may already be running."
}

# Verify Master Server connectivity if loopback
$isLoopback = $MasterServerAddress -match "^(https?:\/\/)?(127\.0\.0\.1|localhost)"
if ($isLoopback) {
    Log-Message "Verifying Local Master Server availability at 127.0.0.1:55555..."
    $masterOnline = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            $tcp = Test-NetConnection -ComputerName "127.0.0.1" -Port 55555 -WarningAction SilentlyContinue
            if ($tcp.TcpTestSucceeded) {
                $masterOnline = $true
                Log-Message "Local Master Server is online at 127.0.0.1:55555."
                break
            }
        } catch {}
        Log-Message "Local Master Server not ready (attempt $attempt/10). Waiting 1s..."
        Start-Sleep -Seconds 1
    }

    if (-not $masterOnline) {
        Log-Message "WARNING: Local Master Server at 127.0.0.1:55555 is not responding. COMPEL may fail authentication if Master Server is offline."
    }
} else {
    Log-Message "Targeting Remote/External Master Server: $MasterServerAddress"
}

# Ensure environment variables for COMPEL execution
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
$env:COMPEL_LOCATION = $Location
$env:COMPEL_SERVER_NAME_PREFIX = $ServerPrefix
$env:COMPEL_PORT_RANGE_OFFSET = "$PortOffset"
$env:COMPEL_CONTROL_PLANE_PORT = "$ControlPort"
$env:COMPEL_USE_PROXY = "true"
$env:COMPEL_GATEWAY = "127.0.0.1"
$env:MASTER_SERVER_ADDRESS = $MasterServerAddress
$env:Compel__MasterServerAddress = $MasterServerAddress
$env:COMPEL_INSTALLATION_DIRECTORY = $ServerDir

$compelExe = Join-Path $ProjectRoot "src\apps\compel\bin\Release\net11.0\COMPEL.exe"
if (-not (Test-Path $compelExe)) {
    $compelExe = Join-Path $ProjectRoot "src\apps\compel\bin\Debug\net11.0\COMPEL.exe"
}
if (-not (Test-Path $compelExe)) {
    $compelExe = Join-Path $ServerDir "COMPEL.exe"
}

if (-not (Test-Path $compelExe)) {
    Log-Message "Building COMPEL..."
    dotnet build (Join-Path $ProjectRoot "src\apps\compel\COMPEL.csproj") -c Release
    $compelExe = Join-Path $ProjectRoot "src\apps\compel\bin\Release\net11.0\COMPEL.exe"
}

Log-Message "Launching COMPEL from: $compelExe"

$compelArgs = @(
    "--Compel:InstallationDirectory=`"$ServerDir`"",
    "--Compel:Gateway=127.0.0.1",
    "--Compel:MasterServerAddress=`"$MasterServerAddress`"",
    "--Compel:UseProxy=true",
    "--Compel:Instances=$Instances",
    "--Compel:Location=`"$Location`"",
    "--Compel:PortRangeOffset=$PortOffset",
    "--Compel:ControlPlanePort=$ControlPort"
)

if ($Background) {
    $cmdLine = "`"$compelExe`" " + ($compelArgs -join " ")
    $wmi = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $cmdLine
        CurrentDirectory = $ServerDir
    } -ErrorAction SilentlyContinue
    if ($wmi -and $wmi.ReturnValue -eq 0 -and $wmi.ProcessId) {
        Log-Message "COMPEL started in detached background (Win32_Process) with PID: $($wmi.ProcessId)"
    } else {
        $proc = Start-Process -FilePath $compelExe -ArgumentList ($compelArgs -join " ") -WorkingDirectory $ServerDir -PassThru -WindowStyle Hidden
        Log-Message "COMPEL started in background with PID: $($proc.Id)"
    }
} else {
    Push-Location $ServerDir
    try {
        & $compelExe @compelArgs
    } finally {
        Pop-Location
    }
}
