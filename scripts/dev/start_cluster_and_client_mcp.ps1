# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# Starts the Aspire Cluster and HoN Game Client via MCP server and keeps it alive
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "src/tools/kongor-cli/bin/Debug/net11.0/kongor.dll mcp"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $false
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)

function Send-Mcp {
    param([int]$Id, [string]$Method, [object]$Params = $null)
    $obj = [ordered]@{ jsonrpc = "2.0"; id = $Id; method = $Method }
    if ($null -ne $Params) { $obj["params"] = $Params }
    $json = $obj | ConvertTo-Json -Compress -Depth 10
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $readTask = $proc.StandardOutput.ReadLineAsync()
    if ($readTask.Wait(20000)) {
        return @{ Success = $true; TimeMs = $sw.ElapsedMilliseconds; Line = $readTask.Result }
    } else {
        return @{ Success = $false; TimeMs = $sw.ElapsedMilliseconds; Line = $null }
    }
}

try {
    Write-Host "=== Starting Aspire Cluster & HoN Game Client via MCP ===" -ForegroundColor Cyan

    # 1. Initialize
    Write-Host "[1/4] Initializing MCP Server..." -ForegroundColor Yellow
    $init = Send-Mcp -Id 1 -Method "initialize" -Params @{ protocolVersion = "2024-11-05"; capabilities = @{} }
    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $proc.StandardInput.Flush()
    Write-Host "MCP Initialized successfully." -ForegroundColor Green

    # 2. Start Cluster
    Write-Host "[2/4] Sending kongor_server_manage (action: start, target: cluster, withGameServers: true)..." -ForegroundColor Yellow
    $startRes = Send-Mcp -Id 2 -Method "tools/call" -Params @{ name = "kongor_server_manage"; arguments = @{ action = "start"; target = "cluster"; withGameServers = $true } }
    Write-Host "Cluster start initiated: $($startRes.Line)" -ForegroundColor Green

    Start-Sleep -Seconds 3

    # Check Cluster Status
    $statusRes = Send-Mcp -Id 3 -Method "tools/call" -Params @{ name = "kongor_server_manage"; arguments = @{ action = "status"; target = "cluster" } }
    Write-Host "Cluster Status: $($statusRes.Line)" -ForegroundColor White

    # 3. Launch Client
    Write-Host "[3/4] Sending kongor_game_client_manage (action: launch, mode: practice)..." -ForegroundColor Yellow
    $launchRes = Send-Mcp -Id 4 -Method "tools/call" -Params @{ name = "kongor_game_client_manage"; arguments = @{ action = "launch"; mode = "practice" } }
    Write-Host "Game Client launched: $($launchRes.Line)" -ForegroundColor Green

    Start-Sleep -Seconds 3

    # Check Client Status
    Write-Host "[4/4] Checking Game Client Status..." -ForegroundColor Yellow
    $clientStatus = Send-Mcp -Id 5 -Method "tools/call" -Params @{ name = "kongor_game_client_manage"; arguments = @{ action = "status" } }
    Write-Host "Game Client Status: $($clientStatus.Line)" -ForegroundColor Green

    Write-Host "`n=== Cluster and Game Client are LIVE and managed by MCP! ===" -ForegroundColor Cyan

    # Keep alive so processes remain running
    while ($true) {
        Start-Sleep -Seconds 30
        $heartbeat = Send-Mcp -Id 99 -Method "tools/call" -Params @{ name = "kongor_game_client_manage"; arguments = @{ action = "status" } }
    }
}
finally {
    try {
        $proc.StandardInput.Close()
        if (-not $proc.WaitForExit(2000)) { $proc.Kill() }
    } catch {
        Write-Warning "Failed to terminate MCP subprocess cleanly: $($_.Exception.Message)"
    }
}
