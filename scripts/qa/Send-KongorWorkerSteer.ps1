# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Enqueues a non-disruptive mid-run operator steering directive (#50 mid-run-operator-steering).

.DESCRIPTION
    Appends a structured steering directive (focus_rva, abort_probe, switch_hypothesis) to
    userdata/logs/worker_steer.jsonl under an exclusive FileStream lock with OS fsync.
    Active workers drain this queue on every lock heartbeat without killing or restarting
    the subagent process.

.EXAMPLE
    .\scripts\qa\Send-KongorWorkerSteer.ps1 -Directive focus_rva -TargetValue "k2_x64.dll+0x951020" -Reason "Investigate CModelPanel::Frame"
    .\scripts\qa\Send-KongorWorkerSteer.ps1 -Directive switch_hypothesis -TargetValue "H2-vtable-offset" -TargetOwner "game-worker-main"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("focus_rva", "abort_probe", "switch_hypothesis")]
    [string]$Directive,

    [Parameter(Mandatory = $true)]
    [string]$TargetValue,

    [Parameter(Mandatory = $false)]
    [string]$TargetOwner = "*",

    [Parameter(Mandatory = $false)]
    [string]$Reason = "",

    [Parameter(Mandatory = $false)]
    [string]$SteerQueuePath = "",

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($SteerQueuePath)) {
    $SteerQueuePath = Join-Path $repoRoot "logs\worker_steer.jsonl"
}

$queueDir = [System.IO.Path]::GetDirectoryName($SteerQueuePath)
if (-not [string]::IsNullOrWhiteSpace($queueDir) -and -not (Test-Path -LiteralPath $queueDir)) {
    New-Item -ItemType Directory -Path $queueDir -Force | Out-Null
}

$record = [ordered]@{
    timestamp    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    directive    = $Directive
    target_value = $TargetValue
    target_owner = $TargetOwner
    reason       = $Reason
    sender_pid   = $PID
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$jsonLine = ($record | ConvertTo-Json -Compress -Depth 4) + "`n"
$lineBytes = $utf8NoBom.GetBytes($jsonLine)

$fs = $null
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        $fs = New-Object System.IO.FileStream(
            $SteerQueuePath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
        break
    } catch {
        if ($attempt -eq 20) {
            throw "Failed to open steering queue '$SteerQueuePath' after 20 attempts: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds (10 * $attempt)
    }
}

try {
    $fs.Seek(0, [System.IO.SeekOrigin]::End) | Out-Null
    $fs.Write($lineBytes, 0, $lineBytes.Length)
    $fs.Flush($true)
} finally {
    if ($null -ne $fs) {
        $fs.Dispose()
    }
}

if (-not $Quiet) {
    Write-Host "[STEER-QUEUED] Queued '$Directive' -> '$TargetValue' for owner='$TargetOwner' at '$SteerQueuePath'." -ForegroundColor Cyan
}
Write-Output ($record | ConvertTo-Json -Compress)
exit 0
