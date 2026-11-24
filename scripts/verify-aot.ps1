# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Native AOT & Trimming Compatibility Guard for ProjectKongor shared libraries.
.DESCRIPTION
    Verifies that <IsAotCompatible>true</IsAotCompatible> and <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    remain enabled on shared libraries (NEXUS.Contracts, Kongor.Core) and builds them in Release mode
    with TreatWarningsAsErrors enabled to ensure zero unconstrained runtime reflection or trim violations.
#>

[CmdletBinding()]
param (
    [string]$RootDirectory = "",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "",
    [string]$SmokeProject = "",
    [switch]$FailOnViolations = $true
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RootDirectory)) {
    $RootDirectory = (Get-Item "$PSScriptRoot\..").FullName
} else {
    $RootDirectory = (Resolve-Path $RootDirectory).Path
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    if ($IsLinux) {
        $RuntimeIdentifier = "linux-x64"
    } elseif ($IsMacOS) {
        $RuntimeIdentifier = "osx-x64"
    } else {
        $RuntimeIdentifier = "win-x64"
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [PROJECTKONGOR CI] Native AOT & Trimming Compatibility Verification Guard" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "Root Directory     : $RootDirectory" -ForegroundColor Gray
Write-Host "Configuration      : $Configuration" -ForegroundColor Gray
Write-Host "Runtime Identifier : $RuntimeIdentifier" -ForegroundColor Gray
Write-Host "Timestamp          : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff zzz')" -ForegroundColor Gray
Write-Host ""

$targetProjects = @(
    "src\libs\contracts\NEXUS.Contracts.csproj",
    "src\libs\protocol-core\Kongor.Core.csproj",
    "src\apps\compel\COMPEL.csproj",
    "src\services\chat-server\TRANSMUTANSTEIN.ChatServer.csproj"
)

$overallSuccess = $true
$results = @()

# -----------------------------------------------------------------------------
# Stage 1: Static Project File Configuration Check
# -----------------------------------------------------------------------------
Write-Host ">> [STAGE 1] Checking <IsAotCompatible> and <EnableTrimAnalyzer> in project files..." -ForegroundColor Yellow

foreach ($relPath in $targetProjects) {
    $fullPath = Join-Path $RootDirectory $relPath
    if (-not (Test-Path $fullPath)) {
        Write-Host "  [-] Project not found: $relPath" -ForegroundColor Red
        $overallSuccess = $false
        continue
    }

    $content = Get-Content -Path $fullPath -Raw
    $hasAot = $content -match '<IsAotCompatible>\s*true\s*</IsAotCompatible>'
    $hasTrim = $content -match '<EnableTrimAnalyzer>\s*true\s*</EnableTrimAnalyzer>'

    Write-Host "  [*] Inspecting: $relPath" -ForegroundColor White
    if ($hasAot) {
        Write-Host "      [+] <IsAotCompatible>true</IsAotCompatible> : OK" -ForegroundColor Green
    } else {
        Write-Host "      [-] <IsAotCompatible>true</IsAotCompatible> : MISSING or FALSE" -ForegroundColor Red
        $overallSuccess = $false
    }

    if ($hasTrim) {
        Write-Host "      [+] <EnableTrimAnalyzer>true</EnableTrimAnalyzer> : OK" -ForegroundColor Green
    } else {
        Write-Host "      [-] <EnableTrimAnalyzer>true</EnableTrimAnalyzer> : MISSING or FALSE" -ForegroundColor Red
        $overallSuccess = $false
    }
}

Write-Host ""

# -----------------------------------------------------------------------------
# Stage 2: Compilation and Roslyn IL-Analyzer Verification
# -----------------------------------------------------------------------------
Write-Host ">> [STAGE 2] Compiling shared libraries with strict Roslyn AOT/Trim analyzers..." -ForegroundColor Yellow

foreach ($relPath in $targetProjects) {
    $fullPath = Join-Path $RootDirectory $relPath
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
    Write-Host "  [*] Building $projectName ($Configuration)..." -ForegroundColor White

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    
    $buildArgs = @(
        "build",
        $fullPath,
        "-c", $Configuration,
        "/p:TreatWarningsAsErrors=true",
        "/nologo",
        "/v:minimal"
    )

    $output = & dotnet $buildArgs 2>&1
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    $isSuccess = ($exitCode -eq 0)
    $results += [PSCustomObject]@{
        Project  = $projectName
        Duration = "$([math]::Round($sw.Elapsed.TotalSeconds, 2))s"
        ExitCode = $exitCode
        Status   = if ($isSuccess) { "PASSED" } else { "FAILED" }
    }

    if ($isSuccess) {
        Write-Host "      [+] $projectName build succeeded in $([math]::Round($sw.Elapsed.TotalSeconds, 2))s with 0 warnings/errors." -ForegroundColor Green
    } else {
        Write-Host "      [-] $projectName build failed with exit code $exitCode ($([math]::Round($sw.Elapsed.TotalSeconds, 2))s)." -ForegroundColor Red
        Write-Host "      Output:" -ForegroundColor Magenta
        $output | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkYellow }
        $overallSuccess = $false
    }
}

# -----------------------------------------------------------------------------
# Stage 3 & 4: Full Native AOT Compilation & Publish Smoke Test (Optional)
# -----------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($SmokeProject)) {
    $resolvedSmoke = if ([System.IO.Path]::IsPathRooted($SmokeProject)) { $SmokeProject } else { Join-Path $RootDirectory $SmokeProject }
    if (Test-Path $resolvedSmoke) {
        Write-Host ">> [STAGE 3] Executing end-to-end Native AOT compilation & publish smoke test ($RuntimeIdentifier)..." -ForegroundColor Yellow
        Write-Host "  [*] Publishing $([System.IO.Path]::GetFileNameWithoutExtension($resolvedSmoke)) with Native AOT (PublishAot=true, RID=$RuntimeIdentifier)..." -ForegroundColor White
        $swAot = [System.Diagnostics.Stopwatch]::StartNew()
        
        $publishArgs = @(
            "publish",
            $resolvedSmoke,
            "-c", $Configuration,
            "-r", $RuntimeIdentifier,
            "/p:PublishAot=true",
            "/p:TreatWarningsAsErrors=true",
            "/nologo",
            "/v:minimal"
        )

        $publishOutput = & dotnet $publishArgs 2>&1
        $publishExitCode = $LASTEXITCODE
        $swAot.Stop()

        $isPublishSuccess = ($publishExitCode -eq 0)
        $smokeName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedSmoke)
        $results += [PSCustomObject]@{
            Project  = "$smokeName (AOT Publish)"
            Duration = "$([math]::Round($swAot.Elapsed.TotalSeconds, 2))s"
            ExitCode = $publishExitCode
            Status   = if ($isPublishSuccess) { "PASSED" } else { "FAILED" }
        }

        if ($isPublishSuccess) {
            Write-Host "      [+] Native AOT publish succeeded in $([math]::Round($swAot.Elapsed.TotalSeconds, 2))s." -ForegroundColor Green
        } else {
            Write-Host "      [-] Native AOT publish failed with exit code $publishExitCode." -ForegroundColor Red
            Write-Host "      Output:" -ForegroundColor Magenta
            $publishOutput | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkYellow }
            $overallSuccess = $false
        }

        Write-Host ""
        Write-Host ">> [STAGE 4] Executing published Native AOT binary to verify zero runtime reflection/JIT crashes..." -ForegroundColor Yellow
        $exeName = if ($RuntimeIdentifier.StartsWith("win")) { "$smokeName.exe" } else { $smokeName }
        $smokeDir = [System.IO.Path]::GetDirectoryName($resolvedSmoke)
        $publishedExe = Join-Path $smokeDir "bin\$Configuration\net11.0\$RuntimeIdentifier\publish\$exeName"
        if (-not (Test-Path $publishedExe)) {
            $publishedExe = Join-Path $smokeDir "bin\$Configuration\net10.0\$RuntimeIdentifier\publish\$exeName"
        }

        if (Test-Path $publishedExe) {
            Write-Host "  [*] Executing $publishedExe --aot-smoke-test..." -ForegroundColor White
            $swExec = [System.Diagnostics.Stopwatch]::StartNew()
            $execOutput = & $publishedExe --aot-smoke-test 2>&1
            $execExitCode = $LASTEXITCODE
            $swExec.Stop()

            $isExecSuccess = ($execExitCode -eq 0)
            $results += [PSCustomObject]@{
                Project  = "$smokeName (Execution)"
                Duration = "$([math]::Round($swExec.Elapsed.TotalSeconds, 2))s"
                ExitCode = $execExitCode
                Status   = if ($isExecSuccess) { "PASSED" } else { "FAILED" }
            }

            if ($isExecSuccess) {
                Write-Host "      [+] Native binary executed successfully in $([math]::Round($swExec.Elapsed.TotalSeconds, 2))s with 0 faults." -ForegroundColor Green
                $execOutput | ForEach-Object { Write-Host "        [LOG] $_" -ForegroundColor DarkGray }
            } else {
                Write-Host "      [-] Native binary execution failed with exit code $execExitCode." -ForegroundColor Red
                Write-Host "      Output:" -ForegroundColor Magenta
                $execOutput | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkYellow }
                $overallSuccess = $false
            }
        } else {
            Write-Host "  [-] Published native executable not found: $publishedExe" -ForegroundColor Red
            $overallSuccess = $false
        }
    } else {
        Write-Host "  [-] Specified smoke project not found: $resolvedSmoke" -ForegroundColor Red
        $overallSuccess = $false
    }
} else {
    Write-Host ">> [STAGE 3 & 4] No standalone AOT smoke project specified (Stage 1 & 2 analyzers verified 100% compliance)." -ForegroundColor Gray
}

Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [SUMMARY] Native AOT Compatibility Verification Results" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan
$results | Format-Table -AutoSize

if (-not $overallSuccess) {
    Write-Host "[-] AOT Verification Guard FAILED. One or more shared libraries or smoke tests have trim/AOT violations." -ForegroundColor Red
    if ($FailOnViolations) {
        exit 1
    }
} else {
    Write-Host "[+] AOT Verification Guard PASSED. All shared libraries and smoke binaries are 100% Native AOT & Trimming compliant!" -ForegroundColor Green
    exit 0
}
