# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Hot-syncs games/server/override/bots into the running Linux dedicated server container (/app/run-server/override/bots).
.DESCRIPTION
    Copies modified Lua/bot/botmetadata files from the mounted /app/server/override/bots tree into /app/run-server/override/bots
    inside compel-dedicated-server, refreshes the case-sensitive behaviorLib.lua mirror (matching infra/docker/entrypoint.sh),
    verifies SHA-256 parity against the Windows host, and optionally restarts a target COMPEL worker port.
#>
[CmdletBinding()]
param(
    [string]$ContainerName = "",
    [string]$ControlUrl = "http://127.0.0.1:11334",
    [int]$RestartWorkerPort = 0
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -Parent
$hostBotDir = Join-Path -Path $repoRoot -ChildPath "games\server\override\bots"

if (-not (Test-Path -LiteralPath $hostBotDir -PathType Container)) {
    throw "Host server bot directory not found at '$hostBotDir'. Diagnose/fix: run from the ProjectKongor repository root checkout."
}

$targetContainers = @()
if (-not [string]::IsNullOrWhiteSpace($ContainerName)) {
    $targetContainers = @($ContainerName)
} else {
    $runningNames = @(& docker ps --format "{{.Names}}" 2>$null)
    foreach ($candidate in @("kongor-stack-compel-dedicated", "kongor-stack-compel-arcade", "compel-dedicated-server")) {
        if ($runningNames -contains $candidate) {
            $targetContainers += $candidate
        }
    }
    if ($targetContainers.Count -eq 0) {
        throw "No running COMPEL dedicated/arcade Docker containers found. Diagnose/fix: run 'docker ps' or start the stack via '.\scripts\docker\launch_docker_stack.ps1'."
    }
}

$syncCmd = @'
set -euo pipefail
if [ -d /app/server/override/bots ]; then
    mkdir -p /app/run-server/override/bots /app/run-server/override/bots/teambot
    (cd /app/server/override/bots && find . -path './rl_runtime' -prune -o -type f -print | tar -cf - -T - | tar -xf - -C /app/run-server/override/bots/)
    for pair in "behaviorlib.lua:behaviorLib.lua" "eventslib.lua:eventsLib.lua" "shoppinglib.lua:shoppingLib.lua" "itembehaviors.lua:itemBehaviors.lua" "illusionlib.lua:illusionLib.lua"; do
        src="${pair%%:*}"
        dst="${pair##*:}"
        if [ -f "/app/run-server/override/bots/$src" ]; then
            cp -f "/app/run-server/override/bots/$src" "/app/run-server/override/bots/$dst"
        fi
    done
fi
sha256sum \
    /app/run-server/override/bots/core.lua \
    /app/run-server/override/bots/botbraincore.lua \
    /app/run-server/override/bots/behaviorlib.lua \
    /app/run-server/override/bots/behaviorLib.lua \
    /app/run-server/override/bots/custom_map_behaviors.lua \
    /app/run-server/override/bots/itembehaviors.lua \
    /app/run-server/override/bots/rl_inference.lua \
    /app/run-server/override/bots/rl_weights_baseline.lua \
    /app/run-server/override/bots/soccer_bot_brain.lua \
    /app/run-server/override/bots/teambot/teambotbrain.lua
'@

$coreFiles = @(
    @{ Host = "core.lua"; Container = "/app/run-server/override/bots/core.lua" },
    @{ Host = "botbraincore.lua"; Container = "/app/run-server/override/bots/botbraincore.lua" },
    @{ Host = "behaviorlib.lua"; Container = "/app/run-server/override/bots/behaviorlib.lua" },
    @{ Host = "behaviorlib.lua"; Container = "/app/run-server/override/bots/behaviorLib.lua" },
    @{ Host = "custom_map_behaviors.lua"; Container = "/app/run-server/override/bots/custom_map_behaviors.lua" },
    @{ Host = "itembehaviors.lua"; Container = "/app/run-server/override/bots/itembehaviors.lua" },
    @{ Host = "rl_inference.lua"; Container = "/app/run-server/override/bots/rl_inference.lua" },
    @{ Host = "rl_weights_baseline.lua"; Container = "/app/run-server/override/bots/rl_weights_baseline.lua" },
    @{ Host = "soccer_bot_brain.lua"; Container = "/app/run-server/override/bots/soccer_bot_brain.lua" },
    @{ Host = "teambot\teambotbrain.lua"; Container = "/app/run-server/override/bots/teambot/teambotbrain.lua" }
)

foreach ($targetContainer in $targetContainers) {
    $containerSums = & docker exec $targetContainer bash -c ($syncCmd -replace "`r", "")
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to sync /app/run-server/override/bots inside container '$targetContainer'. Diagnose/fix: verify container is running via 'docker ps' and check 'docker logs $targetContainer'."
    }

    $containerHashTable = @{}
    foreach ($line in $containerSums) {
        if ($line -match '^([0-9a-fA-F]{64})\s+(.+)$') {
            $containerHashTable[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant()
        }
    }

    foreach ($entry in $coreFiles) {
        $hostFile = Join-Path -Path $hostBotDir -ChildPath $entry.Host
        $hostHash = (Get-FileHash -LiteralPath $hostFile -Algorithm SHA256).Hash.ToUpperInvariant()
        $remoteHash = $containerHashTable[$entry.Container]
        if ($hostHash -ne $remoteHash) {
            throw "SHA-256 mismatch in '$targetContainer' for $($entry.Container): host=$hostHash vs container=$remoteHash. Diagnose/fix: re-run .\scripts\docker\sync_linux_dedicated_bots.ps1 -ContainerName $targetContainer."
        }
    }

    Write-Host -Object "[LINUX-BOT-SYNC] Verified $($coreFiles.Count)/$($coreFiles.Count) SHA-256 bot script hashes in ${targetContainer}:/app/run-server/override/bots/ (including rl_inference.lua, soccer_bot_brain.lua, and behaviorLib.lua)." -ForegroundColor Green
}

if ($RestartWorkerPort -gt 0) {
    $token = $env:COMPEL_AUTH_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "COMPEL_AUTH_TOKEN environment variable is required when -RestartWorkerPort is specified."
    }
    $headers = @{ Authorization = "Bearer $token" }
    $restartUri = "$ControlUrl/instances/$RestartWorkerPort/restart"
    $null = Invoke-RestMethod -Uri $restartUri -Method Post -ContentType "application/json" -Headers $headers
    Write-Host -Object "[LINUX-BOT-SYNC] Requested restart for worker port $RestartWorkerPort via $ControlUrl." -ForegroundColor Cyan
}
