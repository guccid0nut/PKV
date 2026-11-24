# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Manages TTL-bounded Redis runtime overrides for the Parallel Windows + Linux Queue Arcade & Matchmaking Journey,
    guaranteeing permanent 5v5 default restoration on disk and in Garnet/Redis (:28298).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/docker/set_matchmaking_journey_mode.ps1 -Mode Arcade5v5Stage
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/docker/set_matchmaking_journey_mode.ps1 -Mode Pop1v1Stage
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/docker/set_matchmaking_journey_mode.ps1 -Mode Restore5v5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Arcade5v5Stage", "Pop1v1Stage", "Restore5v5", "Status")]
    [string]$Mode,

    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 28298,
    [string]$RedisPassword = "",
    [int]$RoundDurationSeconds = 45,
    [int]$VictoryHoldSeconds = 8,
    [int]$ArcadeTtlSeconds = 360,
    [int]$Pop1v1TtlSeconds = 180
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$AppSettingsPath = Join-Path $RepoRoot "src\services\chat-server\appsettings.json"

if ([string]::IsNullOrWhiteSpace($RedisPassword)) {
    if (-not [string]::IsNullOrWhiteSpace($env:GARNET_PASSWORD)) {
        $RedisPassword = $env:GARNET_PASSWORD
    } elseif (Test-Path (Join-Path $RepoRoot ".env")) {
        $m = Select-String -Path (Join-Path $RepoRoot ".env") -Pattern "^GARNET_PASSWORD=(.+)$" -ErrorAction SilentlyContinue
        if ($m) { $RedisPassword = $m.Matches[0].Groups[1].Value.Trim() }
    }
    if ([string]::IsNullOrWhiteSpace($RedisPassword)) {
        try {
            $envOut = & docker inspect kongor-stack-chat-server --format "{{range .Config.Env}}{{println .}}{{end}}" 2>$null
            $cacheLine = $envOut | Select-String "ConnectionStrings__DISTRIBUTED-CACHE"
            if ($cacheLine) {
                $RedisPassword = ($cacheLine.Line -replace '.*password=([^,]+).*','$1').Trim()
            }
        } catch {}
    }
}

function Invoke-RespCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$CommandParts
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $async = $client.BeginConnect($RedisHost, $RedisPort, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(2000)) {
            throw "Timed out connecting to Garnet/Redis at ${RedisHost}:${RedisPort}. Ensure 'kongor-garnet-cache' is running ('docker start kongor-garnet-cache')."
        }
        $client.EndConnect($async)
        $client.ReceiveTimeout = 2000
        $client.SendTimeout = 2000

        $stream = $client.GetStream()
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $false, 4096, $true)
        if (-not [string]::IsNullOrWhiteSpace($RedisPassword)) {
            $authBytes = [System.Text.Encoding]::UTF8.GetBytes($RedisPassword)
            $authPayload = [System.Text.Encoding]::UTF8.GetBytes("*2`r`n`$4`r`nAUTH`r`n`$$($authBytes.Length)`r`n$RedisPassword`r`n")
            $stream.Write($authPayload, 0, $authPayload.Length)
            $stream.Flush()
            [void]$reader.ReadLine()
        }

        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.Append("*$($CommandParts.Count)`r`n")
        foreach ($part in $CommandParts) {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($part)
            [void]$sb.Append("`$$($bytes.Length)`r`n")
            [void]$sb.Append("$part`r`n")
        }

        $payload = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
        $stream.Write($payload, 0, $payload.Length)
        $stream.Flush()

        $line = $reader.ReadLine()
        if ($null -eq $line) {
            throw "Empty RESP response from ${RedisHost}:${RedisPort} for command '$($CommandParts -join ' ')'. Check container logs via 'docker logs kongor-garnet-cache'."
        }

        $prefix = $line[0]
        $body = $line.Substring(1)
        switch ($prefix) {
            '+' { return $body }
            ':' { return [int64]$body }
            '-' { throw "Redis error for '$($CommandParts -join ' ')': $body" }
            '$' {
                $len = [int]$body
                if ($len -lt 0) { return $null }
                $buf = New-Object char[] $len
                $readTotal = 0
                while ($readTotal -lt $len) {
                    $r = $reader.Read($buf, $readTotal, $len - $readTotal)
                    if ($r -le 0) { break }
                    $readTotal += $r
                }
                [void]$reader.ReadLine()
                return (-join $buf[0..($readTotal - 1)])
            }
            default { return $line }
        }
    }
    finally {
        $client.Dispose()
    }
}

function Write-JourneyLog {
    param([string]$Message)
    $ts = [DateTime]::Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$ts] [JOURNEY-MODE] $Message"
}

if (-not (Test-Path $AppSettingsPath)) {
    throw "Missing '$AppSettingsPath'. Verify workspace root ($RepoRoot)."
}

$diskJson = Get-Content -Raw -Path $AppSettingsPath | ConvertFrom-Json
$diskPlayersPerTeam = [int]$diskJson.Matchmaking.PlayersPerTeam
$diskAllowTwoPlayer = [bool]$diskJson.Matchmaking.AllowTwoPlayerFallback

switch ($Mode) {
    "Arcade5v5Stage" {
        Write-JourneyLog "Applying Arcade5v5Stage (5v5 queue + ${RoundDurationSeconds}s Queue Arcade round + ${VictoryHoldSeconds}s VictoryHold, TTL=${ArcadeTtlSeconds}s)..."
        [void](Invoke-RespCommand @("SET", "kongor:matchmaking:players_per_team", "5", "EX", "$ArcadeTtlSeconds"))
        [void](Invoke-RespCommand @("SET", "kongor:matchmaking:allow_two_player_fallback", "false", "EX", "$ArcadeTtlSeconds"))
        [void](Invoke-RespCommand @("SET", "kongor:queue_arcade:enabled", "true"))
        [void](Invoke-RespCommand @("SET", "kongor:queue_arcade:round_duration_seconds", "$RoundDurationSeconds", "EX", "$ArcadeTtlSeconds"))
        [void](Invoke-RespCommand @("SET", "kongor:queue_arcade:victory_hold_seconds", "$VictoryHoldSeconds", "EX", "$ArcadeTtlSeconds"))
    }
    "Pop1v1Stage" {
        Write-JourneyLog "Applying Pop1v1Stage (kongor:matchmaking:players_per_team=1 with auto-expiring EX=${Pop1v1TtlSeconds}s fail-safe)..."
        [void](Invoke-RespCommand @("SET", "kongor:matchmaking:players_per_team", "1", "EX", "$Pop1v1TtlSeconds"))
    }
    "Restore5v5" {
        Write-JourneyLog "Restoring permanent 5v5 matchmaking defaults and clearing all temporary journey Redis overrides..."
        [void](Invoke-RespCommand @(
            "DEL",
            "kongor:matchmaking:players_per_team",
            "kongor:matchmaking:allow_two_player_fallback",
            "kongor:queue_arcade:round_duration_seconds",
            "kongor:queue_arcade:victory_hold_seconds"
        ))
        [void](Invoke-RespCommand @("SET", "kongor:queue_arcade:enabled", "true"))
        if ($diskPlayersPerTeam -ne 5 -or $diskAllowTwoPlayer) {
            throw "Safety assertion failed: '$AppSettingsPath' has PlayersPerTeam=$diskPlayersPerTeam, AllowTwoPlayerFallback=$diskAllowTwoPlayer (expected 5 and false). Fix src/services/chat-server/appsettings.json immediately."
        }
    }
    "Status" {
        Write-JourneyLog "Querying current matchmaking & Queue Arcade runtime state..."
    }
}

$redisTeamSize = Invoke-RespCommand @("GET", "kongor:matchmaking:players_per_team")
$redisTeamSizeTtl = Invoke-RespCommand @("TTL", "kongor:matchmaking:players_per_team")
$redisFallback = Invoke-RespCommand @("GET", "kongor:matchmaking:allow_two_player_fallback")
$redisArcadeEnabled = Invoke-RespCommand @("GET", "kongor:queue_arcade:enabled")
$redisRoundSec = Invoke-RespCommand @("GET", "kongor:queue_arcade:round_duration_seconds")
$redisRoundTtl = Invoke-RespCommand @("TTL", "kongor:queue_arcade:round_duration_seconds")
$redisHoldSec = Invoke-RespCommand @("GET", "kongor:queue_arcade:victory_hold_seconds")

$effectiveTeamSize = if ($null -ne $redisTeamSize -and "$redisTeamSize" -ne "") { [int]$redisTeamSize } else { $diskPlayersPerTeam }
$summary = [ordered]@{
    mode = $Mode
    diskPlayersPerTeam = $diskPlayersPerTeam
    diskAllowTwoPlayerFallback = $diskAllowTwoPlayer
    redisPlayersPerTeam = $redisTeamSize
    redisPlayersPerTeamTtlSec = $redisTeamSizeTtl
    redisAllowTwoPlayerFallback = $redisFallback
    effectivePlayersPerTeam = $effectiveTeamSize
    effectivePlayersPerMatch = ($effectiveTeamSize * 2)
    queueArcadeEnabled = $redisArcadeEnabled
    queueArcadeRoundDurationSec = $redisRoundSec
    queueArcadeRoundDurationTtlSec = $redisRoundTtl
    queueArcadeVictoryHoldSec = $redisHoldSec
}

Write-JourneyLog ("State verified: " + ($summary | ConvertTo-Json -Compress))
if ($Mode -eq "Restore5v5" -and $effectiveTeamSize -ne 5) {
    throw "Restore5v5 verification failed: effectivePlayersPerTeam=$effectiveTeamSize (expected 5). Run 'scripts/docker/set_matchmaking_journey_mode.ps1 -Mode Restore5v5'."
}
