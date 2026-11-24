# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Runs a repeatable multi-map bot soak suite on the Linux dedicated server container.
.DESCRIPTION
    Syncs games/server/override/bots into compel-dedicated-server via sync_linux_dedicated_bots.ps1,
    launches consecutive botmatches on a target COMPEL worker port, optionally bursts all bots to
    Level 25 via GiveExp (-ForceLevel25) to exercise max-level SkillBuild tables (e.g. tSkills[26]),
    and audits each Slave*_M*_console.clog for 0 Lua errors, 0 invalid paths, and 0 unrescued stalls.
#>
[CmdletBinding()]
param(
    [string[]]$Maps = @("caldavar", "midwars", "devowars_10v10", "team_deathmatch"),
    [int]$SoakSeconds = 25,
    [double]$TimeScale = 10.0,
    [switch]$ForceLevel25,
    [string]$ContainerName = "compel-dedicated-server",
    [string]$ControlUrl = "http://127.0.0.1:11334",
    [int]$WorkerPort = 11335
)

$ErrorActionPreference = 'Stop'
$token = $env:COMPEL_AUTH_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "COMPEL_AUTH_TOKEN environment variable is required. Export COMPEL_AUTH_TOKEN and re-run scripts/docker/run_linux_bot_soak.ps1."
}

$syncScript = Join-Path -Path $PSScriptRoot -ChildPath "sync_linux_dedicated_bots.ps1"
& $syncScript -ContainerName $ContainerName -ControlUrl $ControlUrl

$headers = @{ Authorization = "Bearer $token" }

function Invoke-WorkerCommand {
    param([Parameter(Mandatory = $true)][string]$CommandText)
    $payload = @{ command = $CommandText } | ConvertTo-Json -Compress
    $null = Invoke-RestMethod -Uri "$ControlUrl/instances/$WorkerPort/command" -Method Post -ContentType "application/json" -Headers $headers -Body $payload
}

function Restart-AndWaitWorker {
    $null = Invoke-RestMethod -Uri "$ControlUrl/instances/$WorkerPort/restart" -Method Post -ContentType "application/json" -Headers $headers
    Start-Sleep -Seconds 2
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $statusDoc = Invoke-RestMethod -Uri "$ControlUrl/status" -Headers $headers
        $worker = $statusDoc.workers | Where-Object { $_.port -eq $WorkerPort } | Select-Object -First 1
        if ($null -ne $worker -and $worker.running -eq $true -and $worker.state -eq "Available") {
            return $worker
        }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for COMPEL worker on port $WorkerPort to reach state=Available via $ControlUrl/status."
}

$results = New-Object System.Collections.Generic.List[object]

foreach ($mapKey in $Maps) {
    $workerPre = Restart-AndWaitWorker
    $baselineCrashCount = [int]$workerPre.crashCount

    $beforeClogs = (& docker exec $ContainerName sh -c "ls -1 /opt/hon/config/server/logs/Slave*_M*_console.clog 2>/dev/null || true") -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }

    Invoke-WorkerCommand -CommandText "Set sv_gameCountdownLength 1000; Set sv_heroSelectAllReadyTime 1000; Set sv_preMatchTime 2000; Set sv_matchStartAnnounceTime 1000; Set svr_botMatch true; Set sv_cheats true; Set con_developer 1"
    $matchLabel = "Soak_$($mapKey -replace '[^A-Za-z0-9_]', '_')"
    Invoke-WorkerCommand -CommandText "CreateGame $matchLabel map:$mapKey mode:botmatch teamsize:5 allowduplicate:true spectators:0 referees:0 allheroes:true nostats:true"
    Start-Sleep -Seconds 3

    # Include multiple legacy community `tSkills[26]` heroes (PyromancerBot, AlunaBot, BalphagoreBot, LegionnaireBot)
    # so random bans in team_deathmatch cannot prevent max-level SkillBuild coverage.
    $team1Bots = @("ValkyrieBot", "DevourerBot", "AlunaBot", "BubblesBot", "LegionnaireBot")
    $team2Bots = @("ChronosBot", "PyromancerBot", "BalphagoreBot", "GlaciusBot", "PuppetMasterBot")
    $addCmds = (($team1Bots | ForEach-Object { "AddBot 1 $_" }) + ($team2Bots | ForEach-Object { "AddBot 2 $_" })) -join "; "
    Invoke-WorkerCommand -CommandText $addCmds
    Start-Sleep -Seconds 1

    Invoke-WorkerCommand -CommandText ("StartMatch; Set host_timescale {0:0.0}" -f $TimeScale)

    if ($ForceLevel25) {
        Start-Sleep -Seconds 5
        # GiveExp matches CPlayer::GetHeroName ("Hero_*") or "GiveExp team <Hero_*> <xp>" (game_x64 FUN_1800a2000).
        # Using "GiveExp team" across multiple roster anchors guarantees all 5 heroes per team hit L25 even if TDM bans 1-2 picks.
        $team1Anchors = @("Hero_Valkyrie", "Hero_Devourer", "Hero_Aluna", "Hero_Bubbles", "Hero_Legionnaire")
        $team2Anchors = @("Hero_Chronos", "Hero_Pyromancer", "Hero_Balphagore", "Hero_Glacius", "Hero_PuppetMaster")
        $xpCmds = (($team1Anchors + $team2Anchors) | ForEach-Object { "GiveExp $_ 50000" }) + @(
            "GiveExp team Hero_Valkyrie 50000",
            "GiveExp team Hero_Devourer 50000",
            "GiveExp team Hero_Chronos 50000",
            "GiveExp team Hero_Pyromancer 50000"
        )
        Invoke-WorkerCommand -CommandText ($xpCmds -join "; ")
        $midWaitSec = [Math]::Min(8, [Math]::Max(3, $SoakSeconds - 5))
        Start-Sleep -Seconds $midWaitSec
        # Re-issue after ban/pick modes (e.g. team_deathmatch) finish spawning heroes in-world
        Invoke-WorkerCommand -CommandText ($xpCmds -join "; ")
        $remainingSec = [Math]::Max(5, $SoakSeconds - 5 - $midWaitSec)
        Start-Sleep -Seconds $remainingSec
    } else {
        Start-Sleep -Seconds $SoakSeconds
    }

    $workerPost = Restart-AndWaitWorker
    $crashDelta = [int]$workerPost.crashCount - $baselineCrashCount

    $afterClogs = (& docker exec $ContainerName sh -c "ls -1t /opt/hon/config/server/logs/Slave*_M*_console.clog 2>/dev/null || true") -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
    $targetClog = $afterClogs | Where-Object { $_ -notin $beforeClogs } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($targetClog)) {
        $targetClog = $afterClogs | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($targetClog)) {
        throw "No Slave*_M*_console.clog produced for map '$mapKey'. Ensure master-server:55555 is running (kongor_server_manage start master-server)."
    }

    $auditSh = @"
set -euo pipefail
tr -d '\000' < '$targetClog' > /tmp/kongor_soak_audit.txt
MATCH_ID=`$(basename '$targetClog' | sed -E 's/^Slave[0-9]+_(M[0-9]+)_console\.clog$/\1/')
MOVE_CNT=`$(grep -c '\[BOT_MOVE_DIAG\]' /tmp/kongor_soak_audit.txt || true)
INV_PATH=`$(grep -c 'invalid path!' /tmp/kongor_soak_audit.txt || true)
NO_LANE=`$(grep -c 'unable to get my lane' /tmp/kongor_soak_audit.txt || true)
LUA_ERR=`$(grep -Eic '(^|[^A-Za-z0-9_])bots/.*\.lua:[0-9]+:|stack traceback|Script Error|Compile error|BOT_SKILLBUILD_ERR' /tmp/kongor_soak_audit.txt || true)
WRAP_WARN=`$(grep -c 'BOT-SAFE-WRAPPER' /tmp/kongor_soak_audit.txt || true)
STALL_CNT=`$(grep -c '\[BOT_UNRESCUED_STALL\]' /tmp/kongor_soak_audit.txt || true)
L25_CNT=`$((grep -oE 'Hero_[A-Za-z0-9_]+\(L25' /tmp/kongor_soak_audit.txt || true) | sort -u | wc -l | tr -d ' ')
GAVE_XP=`$(grep -c 'experience to ' /tmp/kongor_soak_audit.txt || true)
rm -f /tmp/kongor_soak_audit.txt
printf '%s|%s|%s|%s|%s|%s|%s|%s|%s\n' "`$MATCH_ID" "`$MOVE_CNT" "`$INV_PATH" "`$NO_LANE" "`$LUA_ERR" "`$WRAP_WARN" "`$STALL_CNT" "`$L25_CNT" "`$GAVE_XP"
"@
    $rawMetrics = (($auditSh -replace "`r", "") | & docker exec -i $ContainerName bash).Trim()
    $parts = $rawMetrics -split '\|'
    if ($parts.Count -lt 9) {
        throw "Unexpected audit output for '$mapKey' ('$targetClog'): $rawMetrics"
    }

    $row = [PSCustomObject]@{
        Map             = $mapKey
        MatchId         = $parts[0]
        MoveDiag        = [int]$parts[1]
        InvalidPath     = [int]$parts[2]
        UnableLane      = [int]$parts[3]
        LuaErrors       = [int]$parts[4]
        SafeWrapperWarn = [int]$parts[5]
        UnrescuedStalls = [int]$parts[6]
        Level25Heroes   = [int]$parts[7]
        GaveXpEvents    = [int]$parts[8]
        CrashDelta      = $crashDelta
    }
    $results.Add($row)

    Write-Host -Object ("[LINUX-SOAK] {0,-16} ({1}): moves={2} invPath={3} luaErr={4} wrapWarn={5} stalls={6} L25Heroes={7} xpGrants={8} crashes={9}" -f `
        $row.Map, $row.MatchId, $row.MoveDiag, $row.InvalidPath, $row.LuaErrors, $row.SafeWrapperWarn, $row.UnrescuedStalls, $row.Level25Heroes, $row.GaveXpEvents, $row.CrashDelta) -ForegroundColor Cyan

    if ($row.CrashDelta -gt 0 -or $row.LuaErrors -gt 0 -or $row.InvalidPath -gt 0 -or $row.UnrescuedStalls -gt 0 -or $row.SafeWrapperWarn -gt 0) {
        throw "Linux bot soak failed invariants on map '$mapKey' ($($row.MatchId)): crashes=$($row.CrashDelta) luaErr=$($row.LuaErrors) invPath=$($row.InvalidPath) stalls=$($row.UnrescuedStalls) wrapWarn=$($row.SafeWrapperWarn). Inspect $targetClog."
    }
    if ($ForceLevel25 -and $mapKey -in @("caldavar", "midwars", "team_deathmatch") -and $row.Level25Heroes -lt 1 -and $row.GaveXpEvents -lt 1) {
        throw "ForceLevel25 was requested on '$mapKey' ($($row.MatchId)), but neither L25 telemetry nor GiveExp grants were observed in $targetClog."
    }
}

$results | Format-Table -AutoSize
