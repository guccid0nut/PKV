# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ============================================================================
# PROJECT KONGOR: CLIENT LOG & TELEMETRY TRIAGE UTILITY
# Rapid filter, canary-liveness verification, Union-Find error clustering,
# secret redaction, and <UNTRUSTED_TOOL_OUTPUT> spotlighting (#37, #38, #44, #52, #60, #63).
# ============================================================================

[CmdletBinding()]
param(
    [string]$Path = "games/client/override/logs/client_lua_debug.log",
    [switch]$ErrorsOnly,
    [switch]$WarningsOnly,
    [string]$Channel = "",
    [string]$Hero = "",
    [string]$Avatar = "",
    [int]$Tail = 50,
    [switch]$Follow,
    [switch]$VerifyCanaryLiveness,
    [string]$ProcessName = "hon_x64",
    [int]$StaleThresholdSeconds = 120,
    [switch]$UntrustedEnvelope,
    [switch]$RedactSecrets,
    [switch]$ClusterErrors,
    [int]$MaxOutputBytes = 12288,
    [string]$SpillDir = "",
    [switch]$UseWatermark,
    [string]$WatermarkFile = ""
)

$repoRoot = $PSScriptRoot | Split-Path -Parent
$targetPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repoRoot $Path }

$secretScrubLib = Join-Path $PSScriptRoot "lib\Test-KongorSecretScrub.ps1"
if (Test-Path -LiteralPath $secretScrubLib) {
    . $secretScrubLib
}

$untrustedModule = Join-Path $PSScriptRoot "lib\KongorUntrustedOutput.psm1"
if (Test-Path -LiteralPath $untrustedModule) {
    Import-Module $untrustedModule -Force -ErrorAction SilentlyContinue
}

function Group-KongorLogClusters {
    param([string[]]$Lines)

    # Disjoint-Set Union-Find over normalized (module, rva_bucket, exception_code / message_stem) keys (#60)
    $parent = @{}
    $clusterMeta = @{}
    $orderedKeys = New-Object System.Collections.Generic.List[string]

    function Find-Root([string]$k) {
        $curr = $k
        while ($parent.ContainsKey($curr) -and $parent[$curr] -ne $curr) {
            $parent[$curr] = $parent[$parent[$curr]]
            $curr = $parent[$curr]
        }
        return $curr
    }

    function Union-Keys([string]$a, [string]$b) {
        $ra = Find-Root $a
        $rb = Find-Root $b
        if ($ra -ne $rb) {
            $parent[$rb] = $ra
        }
    }

    $prevModRvaKey = @{}

    foreach ($rawLine in $Lines) {
        if ([string]::IsNullOrWhiteSpace($rawLine)) { continue }

        $mod = "lua_ui"
        if ($rawLine -match '(?i)\b(k2_x64|cgame_x64|game_x64|game_shared_x64|hon_x64|vid_d9_x64|vid_vulkan_x64)(?:\.dll|\.exe)?\b') {
            $mod = $Matches[1].ToLowerInvariant()
        } elseif ($rawLine -match '\[([A-Za-z0-9_-]+)\]') {
            $mod = $Matches[1].ToLowerInvariant()
        }

        $rvaBucket = "no_rva"
        if ($rawLine -match '(?i)(?:\+\s*0x|rva[=:\s]+0x|at\s+0x)([0-9a-fA-F]{4,16})' -or ($rawLine -match '0x(?!C0000005|E06D7363|C0000409|80000003)([0-9a-fA-F]{4,16})')) {
            try {
                $rvaVal = [Convert]::ToInt64($Matches[1], 16)
                # Bucket RVAs into 256-byte (0x100) function pages so adjacent instructions inside the same function union together
                $bucketVal = $rvaVal -band (-256L)
                $rvaBucket = ("0x{0:x}" -f $bucketVal)
            } catch {
                $rvaBucket = "0x" + $Matches[1].ToLowerInvariant()
            }
        }

        $excCode = "generic"
        if ($rawLine -match '(?i)(0xC0000005|0xE06D7363|0xC0000409|0x80000003|attempt to index|attempt to call|bad argument|stack overflow|CRASH_GUARD)') {
            $excCode = $Matches[1].ToLowerInvariant()
        } else {
            # Strip timestamps, frame numbers, and numeric literals to form a stable stem
            $stem = [regex]::Replace($rawLine, '^\[?[0-9:\.\-\sT]+\]?\s*', '')
            $stem = [regex]::Replace($stem, '\b\d+\b', '#')
            if ($stem.Length -gt 64) { $stem = $stem.Substring(0, 64) }
            $excCode = $stem
        }

        $fullKey = "$mod|$rvaBucket|$excCode"
        if (-not $parent.ContainsKey($fullKey)) {
            $parent[$fullKey] = $fullKey
            $orderedKeys.Add($fullKey)
            $clusterMeta[$fullKey] = [PSCustomObject]@{
                Key        = $fullKey
                Module     = $mod
                RvaBucket  = $rvaBucket
                Exception  = $excCode
                Count      = 0
                SampleLine = $rawLine
            }
        }

        # Union with any cluster sharing the same (module, rva_bucket) when an explicit RVA bucket is present
        if ($rvaBucket -ne "no_rva") {
            $modRva = "$mod|$rvaBucket"
            if ($prevModRvaKey.ContainsKey($modRva)) {
                Union-Keys $prevModRvaKey[$modRva] $fullKey
            } else {
                $prevModRvaKey[$modRva] = $fullKey
            }
        }

        $rootKey = Find-Root $fullKey
        $clusterMeta[$rootKey].Count++
    }

    $results = New-Object System.Collections.Generic.List[object]
    foreach ($k in $orderedKeys) {
        if ($parent[$k] -eq $k -and $clusterMeta[$k].Count -gt 0) {
            $results.Add($clusterMeta[$k])
        }
    }
    return @($results | Sort-Object Count -Descending)
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR LOG TRIAGE] Analyzing In-Game Diagnostics" -ForegroundColor Cyan
Write-Host " Log File: $targetPath" -ForegroundColor Gray
if ($ErrorsOnly) { Write-Host " Filter: ERRORS ONLY" -ForegroundColor Red }
if ($WarningsOnly) { Write-Host " Filter: WARNINGS ONLY" -ForegroundColor Yellow }
if ($Channel) { Write-Host " Filter Channel: $Channel" -ForegroundColor Cyan }
if ($Hero) { Write-Host " Filter Hero: $Hero" -ForegroundColor Magenta }
if ($Avatar) { Write-Host " Filter Avatar: $Avatar" -ForegroundColor Magenta }
Write-Host "================================================================================" -ForegroundColor Cyan

# Canary Process & Log Liveness Check (#44 canary-liveness-log-triage)
if ($VerifyCanaryLiveness) {
    $procObj = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    $procAlive = ($null -ne $procObj)
    $fileExists = Test-Path -LiteralPath $targetPath
    $fileBytes = if ($fileExists) { (Get-Item -LiteralPath $targetPath).Length } else { 0L }
    $fileAgeSec = if ($fileExists) {
        [Math]::Round(((Get-Date).ToUniversalTime() - (Get-Item -LiteralPath $targetPath).LastWriteTimeUtc).TotalSeconds, 1)
    } else {
        999999.0
    }

    $canaryHealthy = $procAlive -and $fileExists -and ($fileBytes -gt 0) -and ($fileAgeSec -le $StaleThresholdSeconds)
    $canaryReason = if (-not $procAlive) {
        "process_dead($ProcessName)"
    } elseif (-not $fileExists -or $fileBytes -eq 0) {
        "empty_or_missing_log"
    } elseif ($fileAgeSec -gt $StaleThresholdSeconds) {
        "stale_log(${fileAgeSec}s>${StaleThresholdSeconds}s)"
    } else {
        "active"
    }

    $canaryColor = if ($canaryHealthy) { [ConsoleColor]::Green } else { [ConsoleColor]::Red }
    Write-Host "[CANARY] canary_healthy=$($canaryHealthy.ToString().ToLowerInvariant()) process_alive=$($procAlive.ToString().ToLowerInvariant()) log_bytes=$fileBytes age_sec=$fileAgeSec reason=$canaryReason" -ForegroundColor $canaryColor
}

if (-not (Test-Path -LiteralPath $targetPath)) {
    Write-Host "[INFO] Log file does not exist yet at: $targetPath" -ForegroundColor DarkYellow
    Write-Host "[INFO] Creating empty log directory and file..." -ForegroundColor DarkYellow
    $parentDir = Split-Path $targetPath -Parent
    if (-not (Test-Path -LiteralPath $parentDir)) { New-Item -ItemType Directory -Path $parentDir -Force | Out-Null }
    New-Item -ItemType File -Path $targetPath -Force | Out-Null
}

function Format-KongorLogLine([string]$line) {
    if ($line -match "\[ERROR\]|\[FATAL\]|CRASH_GUARD|CRASH-PREVENTED|FALLBACK-FAILED") {
        Write-Host $line -ForegroundColor Red
    } elseif ($line -match "\[WARN\]") {
        Write-Host $line -ForegroundColor Yellow
    } elseif ($line -match "\[AVATAR\]|\[HERO\]") {
        Write-Host $line -ForegroundColor Cyan
    } elseif ($line -match "\[MAP\]|\[STATE\]") {
        Write-Host $line -ForegroundColor Magenta
    } elseif ($line -match "\[INFO\]") {
        Write-Host $line -ForegroundColor Green
    } elseif ($line -match "\[DEBUG\]") {
        Write-Host $line -ForegroundColor DarkGray
    } else {
        Write-Host $line -ForegroundColor Gray
    }
}

if ($Follow) {
    Write-Host "[STREAM] Following log updates in real-time (Press Ctrl+C to stop)...`n" -ForegroundColor Yellow
    Get-Content -Path $targetPath -Tail 20 -Wait | ForEach-Object {
        $l = $_
        $match = $true
        if ($ErrorsOnly -and ($l -notmatch "\[ERROR\]|\[FATAL\]|CRASH_GUARD")) { $match = $false }
        if ($WarningsOnly -and ($l -notmatch "\[WARN\]")) { $match = $false }
        if ($Channel -and ($l -notmatch "\[$Channel\]")) { $match = $false }
        if ($Hero -and ($l -notmatch "$Hero")) { $match = $false }
        if ($Avatar -and ($l -notmatch "$Avatar")) { $match = $false }

        if ($match) {
            if ($RedactSecrets -and (Get-Command ConvertTo-KongorRedactedText -ErrorAction SilentlyContinue)) {
                $l = ConvertTo-KongorRedactedText -Text $l
            }
            Format-KongorLogLine $l
        }
    }
    exit 0
}

# Static triage query using Select-String
$allLines = @(Get-Content -LiteralPath $targetPath)
if ($allLines.Count -eq 0) {
    Write-Host "[EMPTY] No entries in log file yet. Launch the game client or run a verification test." -ForegroundColor Gray
    exit 0
}

if ($UseWatermark) {
    $wmPath = if (-not [string]::IsNullOrWhiteSpace($WatermarkFile)) { $WatermarkFile } else { "$targetPath.watermark.json" }
    $currentMtimeTicks = [long]$fileInfo.LastWriteTimeUtc.Ticks
    $totalLineCount = $allLines.Count
    $prevOffset = 0
    $prevMtimeTicks = 0L

    if (Test-Path -LiteralPath $wmPath) {
        try {
            $wmObj = Get-Content -LiteralPath $wmPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $prevOffset = [int]$wmObj.last_line_offset
            $prevMtimeTicks = [long]$wmObj.last_mtime_ticks
        } catch {
            $prevOffset = 0
            $prevMtimeTicks = 0L
        }
    }

    if ($prevOffset -gt $totalLineCount) {
        # Log file was truncated or rotated; reset offset to 0
        $prevOffset = 0
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $newWm = [ordered]@{
        target_path      = $targetPath
        last_line_offset = $totalLineCount
        last_mtime_ticks = $currentMtimeTicks
        updated_at       = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    }
    [System.IO.File]::WriteAllText($wmPath, ($newWm | ConvertTo-Json -Depth 3), $utf8NoBom)

    if ($prevOffset -eq $totalLineCount -and $prevMtimeTicks -eq $currentMtimeTicks) {
        Write-Host "[WATERMARK-NO-DELTA] 0 new lines since offset $prevOffset (mtime_ticks=$currentMtimeTicks)." -ForegroundColor Green
        exit 0
    }

    if ($prevOffset -gt 0 -and $prevOffset -lt $totalLineCount) {
        $allLines = @($allLines[$prevOffset..($totalLineCount - 1)])
        Write-Host "[WATERMARK-DELTA] Triaging $($allLines.Count) new line(s) from offset $prevOffset..$totalLineCount." -ForegroundColor Cyan
    }
}

$filtered = $allLines
if ($ErrorsOnly) {
    $filtered = @($filtered | Select-String -Pattern "\[ERROR\]|\[FATAL\]|CRASH_GUARD|CRASH-PREVENTED" | ForEach-Object { $_.Line })
}
if ($WarningsOnly) {
    $filtered = @($filtered | Select-String -Pattern "\[WARN\]" | ForEach-Object { $_.Line })
}
if ($Channel) {
    $filtered = @($filtered | Select-String -Pattern "\[$Channel\]" | ForEach-Object { $_.Line })
}
if ($Hero) {
    $filtered = @($filtered | Select-String -Pattern "$Hero" | ForEach-Object { $_.Line })
}
if ($Avatar) {
    $filtered = @($filtered | Select-String -Pattern "$Avatar" | ForEach-Object { $_.Line })
}

if ($ClusterErrors -and $filtered.Count -gt 0) {
    $clusters = @(Group-KongorLogClusters -Lines $filtered)
    Write-Host "[UNION-FIND CLUSTERS] Collapsed $($filtered.Count) log lines into $($clusters.Count) disjoint error cluster(s):" -ForegroundColor Cyan
    $idx = 1
    foreach ($c in $clusters) {
        $sample = $c.SampleLine
        if ($RedactSecrets -and (Get-Command ConvertTo-KongorRedactedText -ErrorAction SilentlyContinue)) {
            $sample = ConvertTo-KongorRedactedText -Text $sample
        }
        Write-Host "  [CLUSTER #$idx] count=$($c.Count) key=$($c.Key) sample=`"$sample`"" -ForegroundColor Yellow
        $idx++
    }
}

$displayList = if ($filtered.Count -gt $Tail) { $filtered[-$Tail..-1] } else { $filtered }
$joinedDisplay = ($displayList -join "`n")

if ($RedactSecrets -and (Get-Command ConvertTo-KongorRedactedText -ErrorAction SilentlyContinue)) {
    $joinedDisplay = ConvertTo-KongorRedactedText -Text $joinedDisplay
}

$injScan = if (Get-Command Test-KongorPromptInjection -ErrorAction SilentlyContinue) {
    Test-KongorPromptInjection -Text $joinedDisplay
} else {
    [PSCustomObject]@{ Detected = $false; Risk = "low"; Categories = @() }
}

if ($UntrustedEnvelope -or $injScan.Detected) {
    $spot = Format-KongorUntrustedOutput -Text $joinedDisplay -Origin "triage_client_logs" -MaxBytes $MaxOutputBytes -SpillDir $SpillDir
    Write-Host "[SPOTLIGHT] risk=$($spot.Risk) detected=$($spot.Detected.ToString().ToLowerInvariant()) categories=$(($spot.Categories -join ',')) truncated=$($spot.Truncated.ToString().ToLowerInvariant())" -ForegroundColor Magenta
    Write-Host $spot.Envelope
} else {
    $bounded = if (Get-Command Format-KongorBoundedOutput -ErrorAction SilentlyContinue) {
        Format-KongorBoundedOutput -Text $joinedDisplay -MaxBytes $MaxOutputBytes -ToolName "triage_client_logs" -SpillDir $SpillDir
    } else {
        [PSCustomObject]@{ Text = $joinedDisplay; Truncated = $false; SpillPath = "" }
    }
    Write-Host "[MATCHES] Displaying $(@($displayList).Count) of $(@($filtered).Count) matching entries:`n" -ForegroundColor Gray
    foreach ($line in ($bounded.Text -split "`r?`n")) {
        Format-KongorLogLine $line
    }
}

Write-Host "`n[DONE] Triage complete." -ForegroundColor Cyan

