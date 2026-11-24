# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Automated A/B performance, memory (Peak Working Set / RSS), binary size, and output parity harness
    comparing C++ baseline CLIs against experimental Goose CLIs.

.EXAMPLE
    .\scripts\benchmark_goose_ab.ps1 `
        -Name "kongor-metagen" `
        -BaselineExe "native/bin/kongor-metagen.exe" `
        -BaselineArgs @("schemas/protocol.json", "temp_cpp_out") `
        -CandidateExe "native/bin/kongor-metagen-goose.exe" `
        -CandidateArgs @("schemas/protocol.json", "temp_goose_out") `
        -Iterations 20
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [string]$BaselineExe,

    [string[]]$BaselineArgs = @(),

    [string]$BaselineArgLine = "",

    [Parameter(Mandatory = $true)]
    [string]$CandidateExe,

    [string[]]$CandidateArgs = @(),

    [string]$CandidateArgLine = "",

    [int]$WarmupIterations = 3,

    [int]$Iterations = 20,

    [string]$BaselineParityPath = "",

    [string]$CandidateParityPath = "",

    [string]$OutputJsonPath = ""
)

$ErrorActionPreference = "Stop"

function Write-BenchLog {
    param([string]$Level, [string]$Message)
    $ts = [DateTime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffZ")
    Write-Host "[$ts] [BENCH-AB] [$Level] $Message"
}

function Resolve-RequiredExe {
    param([string]$PathArg, [string]$Label)
    if (-not (Test-Path -LiteralPath $PathArg)) {
        throw "[BENCH-AB] Missing $Label executable at '$PathArg'. Diagnose/Fix: Run .\scripts\Invoke-KongorTestLock.ps1 -Owner bench -- cmake --build native/build_goose --config Release"
    }
    return (Resolve-Path -LiteralPath $PathArg).Path
}

function Get-StringSha256Hex {
    param([string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")
    } finally {
        $sha.Dispose()
    }
}

function Format-ProcessArguments {
    param([string[]]$ArgList)
    $quoted = foreach ($a in $ArgList) {
        if ($a -match '[\s"]') {
            '"' + ($a -replace '"', '\"') + '"'
        } else {
            $a
        }
    }
    return ($quoted -join " ")
}

function Invoke-MeasuredRuns {
    param(
        [string]$Label,
        [string]$ExePath,
        [string[]]$Arguments,
    [string]$RawArgLine = "",
        [int]$Warmups,
        [int]$Runs
    )

    $joinedArgs = if ($RawArgLine) { $RawArgLine } else { Format-ProcessArguments -ArgList $Arguments }
    Write-BenchLog "INFO" "Starting $Label benchmark: exe='$ExePath' args='$joinedArgs' warmups=$Warmups runs=$Runs"

    $psiTemplate = {
        param([string]$exe, [string]$argString)
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = $argString
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        # Suppress verbose debug chatter on stderr during hot timing runs so I/O pipe buffering does not skew CPU/memory metrics
        $psi.EnvironmentVariables["KONGOR_GOOSE_QUIET"] = "1"
        $psi.EnvironmentVariables["KONGOR_GOOSE_LOG_FILE"] = "0"
        return $psi
    }

    for ($w = 0; $w -lt $Warmups; $w++) {
        $psi = & $psiTemplate $ExePath $joinedArgs
        $proc = [System.Diagnostics.Process]::Start($psi)
        $stdout = $proc.StandardOutput.ReadToEnd()
        $stderr = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) {
            throw "[BENCH-AB] $Label warmup #$w failed with exit code $($proc.ExitCode). Stderr: $stderr | Diagnose/Fix: Run '$ExePath $joinedArgs' manually without KONGOR_GOOSE_QUIET."
        }
        $proc.Dispose()
    }

    $timesMs = New-Object 'System.Collections.Generic.List[double]' $Runs
    $peakRssKb = New-Object 'System.Collections.Generic.List[double]' $Runs
    $lastStdout = ""

    for ($i = 0; $i -lt $Runs; $i++) {
        $psi = & $psiTemplate $ExePath $joinedArgs
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $proc = [System.Diagnostics.Process]::Start($psi)
        $peakBytes = 0L
        try {
            $peakBytes = $proc.PeakWorkingSet64
        } catch {
            $peakBytes = 0L
        }
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        while (-not $proc.WaitForExit(1)) {
            try {
                $proc.Refresh()
                if ($proc.PeakWorkingSet64 -gt $peakBytes) {
                    $peakBytes = $proc.PeakWorkingSet64
                }
            } catch {
                # Process already exited between poll ticks
            }
        }
        $proc.WaitForExit()
        $sw.Stop()
        try {
            if ($proc.PeakWorkingSet64 -gt $peakBytes) {
                $peakBytes = $proc.PeakWorkingSet64
            }
        } catch {
            # Process handle peak counter unavailable post-exit on some kernels; retain last sampled peakBytes
        }

        $lastStdout = $stdoutTask.GetAwaiter().GetResult()
        $errText = $stderrTask.GetAwaiter().GetResult()
        if ($proc.ExitCode -ne 0) {
            throw "[BENCH-AB] $Label run #$i failed with exit code $($proc.ExitCode). Stderr: $errText | Diagnose/Fix: Run '$ExePath $($Arguments -join ' ')' directly."
        }
        $proc.Dispose()

        $timesMs.Add($sw.Elapsed.TotalMilliseconds)
        $peakRssKb.Add([Math]::Round($peakBytes / 1024.0, 1))
    }

    $timesMs.Sort()
    $peakRssKb.Sort()

    $medianIdx = [int][Math]::Floor($Runs / 2.0)
    $p95Idx = [Math]::Min($Runs - 1, [int][Math]::Ceiling($Runs * 0.95) - 1)
    $exeSizeKb = [Math]::Round((Get-Item -LiteralPath $ExePath).Length / 1024.0, 1)

    $result = [PSCustomObject]@{
        Label          = $Label
        ExePath        = $ExePath
        BinarySizeKB   = $exeSizeKb
        MedianMs       = [Math]::Round($timesMs[$medianIdx], 3)
        P95Ms          = [Math]::Round($timesMs[$p95Idx], 3)
        MinMs          = [Math]::Round($timesMs[0], 3)
        MaxMs          = [Math]::Round($timesMs[$Runs - 1], 3)
        MedianPeakKB   = $peakRssKb[$medianIdx]
        MaxPeakKB      = $peakRssKb[$Runs - 1]
        StdoutSha256   = (Get-StringSha256Hex -Text $lastStdout)
    }

    Write-BenchLog "INFO" ("{0}: Median={1:F3} ms | P95={2:F3} ms | PeakRSS(med)={3:F1} KB | Binary={4:F1} KB" -f $Label, $result.MedianMs, $result.P95Ms, $result.MedianPeakKB, $result.BinarySizeKB)
    return $result
}

function Get-TreeHashDigest {
    param([string]$TargetPath)
    if (-not (Test-Path -LiteralPath $TargetPath)) {
        throw "[BENCH-AB] Parity target path '$TargetPath' does not exist. Diagnose/Fix: Verify CLI output directory argument."
    }
    $item = Get-Item -LiteralPath $TargetPath
    if (-not $item.PSIsContainer) {
        return (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
    $rootFull = $item.FullName.TrimEnd('\', '/')
    $entries = Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in $entries) {
        $rel = $f.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
        $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        [void]$sb.AppendLine("$rel`:$h")
    }
    return (Get-StringSha256Hex -Text $sb.ToString())
}

$baseResolved = Resolve-RequiredExe -PathArg $BaselineExe -Label "C++ Baseline"
$candResolved = Resolve-RequiredExe -PathArg $CandidateExe -Label "Goose Candidate"

$baseStats = Invoke-MeasuredRuns -Label "CPP-Baseline" -ExePath $baseResolved -Arguments $BaselineArgs -RawArgLine $BaselineArgLine -Warmups $WarmupIterations -Runs $Iterations
$candStats = Invoke-MeasuredRuns -Label "Goose-Candidate" -ExePath $candResolved -Arguments $CandidateArgs -RawArgLine $CandidateArgLine -Warmups $WarmupIterations -Runs $Iterations

$parityMatch = $true
$baseTreeHash = ""
$candTreeHash = ""
if ($BaselineParityPath -and $CandidateParityPath) {
    $baseTreeHash = Get-TreeHashDigest -TargetPath $BaselineParityPath
    $candTreeHash = Get-TreeHashDigest -TargetPath $CandidateParityPath
    $parityMatch = ($baseTreeHash -eq $candTreeHash)
    Write-BenchLog "INFO" "Artifact Parity SHA-256: Baseline=$baseTreeHash | Candidate=$candTreeHash | Match=$parityMatch"
}

$speedRatio = if ($candStats.MedianMs -gt 0) { [Math]::Round($baseStats.MedianMs / $candStats.MedianMs, 3) } else { 0.0 }
$rssDeltaPct = if ($baseStats.MedianPeakKB -gt 0) { [Math]::Round((($candStats.MedianPeakKB - $baseStats.MedianPeakKB) / $baseStats.MedianPeakKB) * 100.0, 2) } else { 0.0 }

$summary = [PSCustomObject]@{
    BenchmarkName    = $Name
    TimestampUtc     = [DateTime]::UtcNow.ToString("O")
    Iterations       = $Iterations
    Baseline         = $baseStats
    Candidate        = $candStats
    SpeedupVsCpp     = $speedRatio
    PeakRssDeltaPct  = $rssDeltaPct
    ParityVerified   = $parityMatch
    BaselineHash     = $baseTreeHash
    CandidateHash    = $candTreeHash
}

Write-Host ""
Write-Host "================ A/B SCOREBOARD: $Name ================"
Write-Host ("  C++ Baseline    : {0,8:F3} ms (P95 {1,8:F3} ms) | Peak RSS: {2,8:F1} KB | Bin: {3,7:F1} KB" -f $baseStats.MedianMs, $baseStats.P95Ms, $baseStats.MedianPeakKB, $baseStats.BinarySizeKB)
Write-Host ("  Goose Candidate : {0,8:F3} ms (P95 {1,8:F3} ms) | Peak RSS: {2,8:F1} KB | Bin: {3,7:F1} KB" -f $candStats.MedianMs, $candStats.P95Ms, $candStats.MedianPeakKB, $candStats.BinarySizeKB)
Write-Host ("  Speedup Ratio   : {0}x (>1.0 = Goose faster) | Peak RSS Delta: {1}%" -f $speedRatio, $rssDeltaPct)
if ($BaselineParityPath -and $CandidateParityPath) {
    Write-Host ("  Output Parity   : {0}" -f $(if ($parityMatch) { "100% MATCH ($candTreeHash)" } else { "MISMATCH!" }))
}
Write-Host "======================================================="

if ($OutputJsonPath) {
    $parent = Split-Path -Parent $OutputJsonPath
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputJsonPath -Encoding UTF8
    Write-BenchLog "INFO" "Wrote JSON benchmark report to '$OutputJsonPath'"
}

if (-not $parityMatch) {
    throw "[BENCH-AB] Output parity check FAILED for '$Name' ($baseTreeHash != $candTreeHash). Diagnose/Fix: Diff '$BaselineParityPath' vs '$CandidateParityPath'."
}
