# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Tamper-evident SHA-256 / HMAC-chained JSONL audit ledger, cross-process exclusive file lock,
    and 3-tier blast-radius classifier for Project Kongor (#39, #55, #111).

.DESCRIPTION
    Implements Phase 1 audit & blast-radius primitives:
    - Get-KongorActionBlastRadius (#55): Classifies operations into read_only, reversible_local,
      or high_blast_radius.
    - Write-KongorAuditRecord (#39, #111): Appends a canonical SHA-256 hash-chained JSONL record
      to userdata/logs/kongor_audit_ledger.jsonl under an exclusive [System.IO.FileShare]::None
      file lock with 20 x 10ms exponential backoff retry and tail-hash recovery.
    - Test-KongorAuditLedger (#39): Verifies contiguous seq numbers, prev_hash continuity,
      recomputed SHA-256 record hashes, and optional HMAC-SHA256 signatures.
#>

function Get-KongorActionBlastRadius {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Action,

        [Parameter(Mandatory = $false)]
        [string]$Tool = ""
    )

    $key = "$Tool`:$Action".ToLowerInvariant()
    if ($key -match 'status|check|search|list|inspect|triage|read|verify|audit|resolve|locate|get|query|diff|preview|snapshot') {
        return "read_only"
    }
    if ($key -match 'inject|s2z|archive|force|delete|kill|overwrite|deploy|migrate|reset_hard') {
        return "high_blast_radius"
    }
    return "reversible_local"
}

function Get-KongorStringSha256Hex {
    param([string]$InputText)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $bytes = $utf8.GetBytes($InputText)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-KongorHmacSha256Hex {
    param([string]$InputText, [string]$KeyText)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $keyBytes = $utf8.GetBytes($KeyText)
    $msgBytes = $utf8.GetBytes($InputText)
    $hmac = New-Object System.Security.Cryptography.HMACSHA256(,$keyBytes)
    try {
        $hashBytes = $hmac.ComputeHash($msgBytes)
        return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $hmac.Dispose()
    }
}

function Get-KongorCanonicalAuditPayload {
    param(
        [int]$Seq,
        [string]$Timestamp,
        [string]$Tool,
        [string]$Action,
        [string]$BlastRadius,
        [string]$Decision,
        [string]$ReasonCode,
        [string]$Target,
        [string]$PrevHash
    )
    return "seq=$Seq|ts=$Timestamp|tool=$Tool|action=$Action|blast_radius=$BlastRadius|decision=$Decision|reason=$ReasonCode|target=$Target|prev_hash=$PrevHash"
}

function Write-KongorAuditRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Action,

        [Parameter(Mandatory = $false)]
        [string]$Tool = "",

        [Parameter(Mandatory = $false)]
        [Alias("Status")]
        [ValidateSet("allow", "allowed", "refuse", "refused", "info")]
        [string]$Decision = "allow",

        [Parameter(Mandatory = $false)]
        [Alias("Reason")]
        [string]$ReasonCode = "ok",

        [Parameter(Mandatory = $false)]
        [string]$Target = "",

        [Parameter(Mandatory = $false)]
        [string]$Actor = "",

        [Parameter(Mandatory = $false)]
        [string]$BlastRadius = "",

        [Parameter(Mandatory = $false)]
        [hashtable]$Metadata = @{},

        [Parameter(Mandatory = $false)]
        [string]$LedgerPath = ""
    )

    if ([string]::IsNullOrWhiteSpace($Tool)) {
        $Tool = $Action
    }
    if ($Decision -eq "allowed") { $Decision = "allow" }
    if ($Decision -eq "refused") { $Decision = "refuse" }
    if (-not [string]::IsNullOrWhiteSpace($Actor)) {
        $Metadata["actor"] = $Actor
    }

    if ([string]::IsNullOrWhiteSpace($LedgerPath)) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $LedgerPath = Join-Path $repoRoot "logs\kongor_audit_ledger.jsonl"
    }

    $ledgerDir = [System.IO.Path]::GetDirectoryName($LedgerPath)
    if (-not [string]::IsNullOrWhiteSpace($ledgerDir) -and -not (Test-Path -LiteralPath $ledgerDir)) {
        New-Item -ItemType Directory -Path $ledgerDir -Force | Out-Null
    }

    if ([string]::IsNullOrWhiteSpace($BlastRadius)) {
        $BlastRadius = Get-KongorActionBlastRadius -Action $Action -Tool $Tool
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $maxAttempts = 20
    $baseDelayMs = 10
    $fs = $null

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $fs = New-Object System.IO.FileStream(
                $LedgerPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None
            )
            break
        } catch {
            if ($attempt -eq $maxAttempts) {
                throw "Failed to acquire exclusive audit ledger lock on '$LedgerPath' after $maxAttempts attempts: $($_.Exception.Message)"
            }
            $sleepMs = [Math]::Min(250, $baseDelayMs * $attempt)
            Start-Sleep -Milliseconds $sleepMs
        }
    }

    try {
        # Read existing lines to recover tail seq and tail hash (#111 tail-hash recovery)
        $reader = New-Object System.IO.StreamReader($fs, $utf8NoBom, $true, 4096, $true)
        $content = $reader.ReadToEnd()
        $reader.Dispose()

        $prevSeq = 0
        $prevHash = ("0" * 64)

        if (-not [string]::IsNullOrWhiteSpace($content)) {
            $lines = @($content -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($lines.Count -gt 0) {
                $lastLine = $lines[$lines.Count - 1]
                $lastObj = $lastLine | ConvertFrom-Json -ErrorAction Stop
                $prevSeq = [int]$lastObj.seq
                $prevHash = [string]$lastObj.hash
            }
        }

        $nextSeq = $prevSeq + 1
        $ts = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        $canonical = Get-KongorCanonicalAuditPayload `
            -Seq $nextSeq `
            -Timestamp $ts `
            -Tool $Tool `
            -Action $Action `
            -BlastRadius $BlastRadius `
            -Decision $Decision `
            -ReasonCode $ReasonCode `
            -Target $Target `
            -PrevHash $prevHash

        $recordHash = Get-KongorStringSha256Hex -InputText $canonical
        $hmacKey = $env:KONGOR_AUDIT_HMAC_KEY
        $hmacHex = if (-not [string]::IsNullOrWhiteSpace($hmacKey)) {
            Get-KongorHmacSha256Hex -InputText "$canonical|hash=$recordHash" -KeyText $hmacKey
        } else {
            $null
        }

        $record = [ordered]@{
            seq          = $nextSeq
            timestamp    = $ts
            tool         = $Tool
            action       = $Action
            blast_radius = $BlastRadius
            decision     = $Decision
            reason_code  = $ReasonCode
            target       = $Target
            prev_hash    = $prevHash
            hash         = $recordHash
            hmac         = $hmacHex
            metadata     = $Metadata
        }

        $jsonLine = ($record | ConvertTo-Json -Compress -Depth 5) + "`n"
        $lineBytes = $utf8NoBom.GetBytes($jsonLine)

        $fs.Seek(0, [System.IO.SeekOrigin]::End) | Out-Null
        $fs.Write($lineBytes, 0, $lineBytes.Length)
        $fs.Flush($true)

        return [PSCustomObject]$record
    } finally {
        if ($null -ne $fs) {
            $fs.Dispose()
        }
    }
}

function Test-KongorAuditLedger {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$LedgerPath = "",

        [Parameter(Mandatory = $false)]
        [switch]$Quiet
    )

    if ([string]::IsNullOrWhiteSpace($LedgerPath)) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $LedgerPath = Join-Path $repoRoot "logs\kongor_audit_ledger.jsonl"
    }

    if (-not (Test-Path -LiteralPath $LedgerPath)) {
        return [PSCustomObject]@{
            Ok             = $true
            RecordsChecked = 0
            FirstBadSeq    = $null
            Reason         = "Ledger file does not exist yet (0 records)."
        }
    }

    $rawLines = @(Get-Content -LiteralPath $LedgerPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $expectedPrevHash = ("0" * 64)
    $expectedSeq = 1
    $hmacKey = $env:KONGOR_AUDIT_HMAC_KEY

    foreach ($line in $rawLines) {
        $rec = $null
        try {
            $rec = $line | ConvertFrom-Json -ErrorAction Stop
        } catch {
            if (-not $Quiet) {
                Write-Host "[AUDIT-FAIL] Corrupt JSON at seq=$expectedSeq in '$LedgerPath': $($_.Exception.Message)" -ForegroundColor Red
            }
            return [PSCustomObject]@{
                Ok             = $false
                RecordsChecked = $expectedSeq - 1
                FirstBadSeq    = $expectedSeq
                Reason         = "Corrupt JSON at line $expectedSeq"
            }
        }

        if ([int]$rec.seq -ne $expectedSeq) {
            $reason = "Sequence gap at line ${expectedSeq}: expected seq=$expectedSeq, got seq=$($rec.seq)"
            if (-not $Quiet) { Write-Host "[AUDIT-FAIL] $reason" -ForegroundColor Red }
            return [PSCustomObject]@{
                Ok             = $false
                RecordsChecked = $expectedSeq - 1
                FirstBadSeq    = $expectedSeq
                Reason         = $reason
            }
        }

        if ([string]$rec.prev_hash -ne $expectedPrevHash) {
            $reason = "Broken prev_hash link at seq=${expectedSeq}: expected '$expectedPrevHash', got '$($rec.prev_hash)'"
            if (-not $Quiet) { Write-Host "[AUDIT-FAIL] $reason" -ForegroundColor Red }
            return [PSCustomObject]@{
                Ok             = $false
                RecordsChecked = $expectedSeq - 1
                FirstBadSeq    = $expectedSeq
                Reason         = $reason
            }
        }

        $canonical = Get-KongorCanonicalAuditPayload `
            -Seq ([int]$rec.seq) `
            -Timestamp ([string]$rec.timestamp) `
            -Tool ([string]$rec.tool) `
            -Action ([string]$rec.action) `
            -BlastRadius ([string]$rec.blast_radius) `
            -Decision ([string]$rec.decision) `
            -ReasonCode ([string]$rec.reason_code) `
            -Target ([string]$rec.target) `
            -PrevHash ([string]$rec.prev_hash)

        $recomputedHash = Get-KongorStringSha256Hex -InputText $canonical
        if ($recomputedHash -ne [string]$rec.hash) {
            $reason = "Hash mismatch at seq=${expectedSeq}: expected '$recomputedHash', got '$($rec.hash)'"
            if (-not $Quiet) { Write-Host "[AUDIT-FAIL] $reason" -ForegroundColor Red }
            return [PSCustomObject]@{
                Ok             = $false
                RecordsChecked = $expectedSeq - 1
                FirstBadSeq    = $expectedSeq
                Reason         = $reason
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($hmacKey) -and -not [string]::IsNullOrWhiteSpace([string]$rec.hmac)) {
            $expectedHmac = Get-KongorHmacSha256Hex -InputText "$canonical|hash=$recomputedHash" -KeyText $hmacKey
            if ($expectedHmac -ne [string]$rec.hmac) {
                $reason = "HMAC signature mismatch at seq=${expectedSeq}"
                if (-not $Quiet) { Write-Host "[AUDIT-FAIL] $reason" -ForegroundColor Red }
                return [PSCustomObject]@{
                    Ok             = $false
                    RecordsChecked = $expectedSeq - 1
                    FirstBadSeq    = $expectedSeq
                    Reason         = $reason
                }
            }
        }

        $expectedPrevHash = $recomputedHash
        $expectedSeq++
    }

    $totalVerified = $expectedSeq - 1
    if (-not $Quiet) {
        Write-Host "[AUDIT-OK] Verified $totalVerified chained SHA-256 records" -ForegroundColor Green
    }

    return [PSCustomObject]@{
        Ok             = $true
        RecordsChecked = $totalVerified
        FirstBadSeq    = $null
        Reason         = ""
    }
}

function Write-KongorWorkerEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$EventType,

        [Parameter(Mandatory = $false)]
        [string]$WorkerOwner = "",

        [Parameter(Mandatory = $false)]
        [string]$WorkerRole = "",

        [Parameter(Mandatory = $false)]
        [string]$Message = "",

        [Parameter(Mandatory = $false)]
        [hashtable]$Payload = @{},

        [Parameter(Mandatory = $false)]
        [string]$EventStreamPath = ""
    )

    if ([string]::IsNullOrWhiteSpace($WorkerOwner)) {
        $WorkerOwner = if ($env:KONGOR_WORKER_OWNER) { $env:KONGOR_WORKER_OWNER } else { "worker-$PID" }
    }
    if ([string]::IsNullOrWhiteSpace($WorkerRole)) {
        $WorkerRole = if ($env:KONGOR_WORKER_ROLE) { $env:KONGOR_WORKER_ROLE } else { "kongor-game-worker" }
    }
    if ([string]::IsNullOrWhiteSpace($EventStreamPath)) {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
        $EventStreamPath = Join-Path $repoRoot "logs\worker_events.jsonl"
    }

    $streamDir = [System.IO.Path]::GetDirectoryName($EventStreamPath)
    if (-not [string]::IsNullOrWhiteSpace($streamDir) -and -not (Test-Path -LiteralPath $streamDir)) {
        New-Item -ItemType Directory -Path $streamDir -Force | Out-Null
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $maxAttempts = 30
    $baseDelayMs = 10
    $fs = $null

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $fs = New-Object System.IO.FileStream(
                $EventStreamPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None
            )
            break
        } catch {
            if ($attempt -eq $maxAttempts) {
                throw "Failed to acquire exclusive worker event stream lock on '$EventStreamPath' after $maxAttempts attempts: $($_.Exception.Message)"
            }
            $sleepMs = [Math]::Min(250, $baseDelayMs * $attempt)
            Start-Sleep -Milliseconds $sleepMs
        }
    }

    try {
        $eventRecord = [ordered]@{
            timestamp    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            pid          = $PID
            worker_owner = $WorkerOwner
            worker_role  = $WorkerRole
            event_type   = $EventType
            message      = $Message
            payload      = $Payload
        }

        $jsonLine = ($eventRecord | ConvertTo-Json -Compress -Depth 6) + "`n"
        $lineBytes = $utf8NoBom.GetBytes($jsonLine)

        $fs.Seek(0, [System.IO.SeekOrigin]::End) | Out-Null
        $fs.Write($lineBytes, 0, $lineBytes.Length)
        $fs.Flush($true) # Mandatory OS-level fsync (#49)

        return [PSCustomObject]$eventRecord
    } finally {
        if ($null -ne $fs) {
            $fs.Dispose()
        }
    }
}

Export-ModuleMember -Function @(
    'Get-KongorActionBlastRadius',
    'Write-KongorAuditRecord',
    'Test-KongorAuditLedger',
    'Write-KongorWorkerEvent'
)

