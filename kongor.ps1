# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Thin Developer CLI Bootstrapper for ProjectKongor (delegates all routing to Kongor.Cli).

.DESCRIPTION
    Preserves caller CWD (KONGOR_CALLER_CWD), binds KONGOR_ROOT to this checkout, builds
    src/tools/kongor-cli/Kongor.Cli.csproj via scripts/Invoke-KongorTestLock.ps1 only when
    missing or stale, and delegates every command verbatim to Kongor.Cli on Windows, Linux, and macOS.
    Includes a compile-failure escape hatch so 'build', 'test', 'gate', and 'clean' remain
    recoverable even if an in-progress edit breaks Kongor.Cli compilation.

.EXAMPLE
    .\kongor.ps1 play
    .\kongor.ps1 where
    .\kongor.ps1 docker restart|cleanup --dry-run
#>

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$IsWinHost = ($env:OS -eq "Windows_NT") -or ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)
$ExeSuffix = if ($IsWinHost) { ".exe" } else { "" }
$PwshExe = if ($IsWinHost) { "powershell.exe" } else { "pwsh" }

$AllArgs = @($args)
$RepoRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot } else { (Get-Location).Path }
$CallerCwd = (Get-Location).Path

$env:KONGOR_ROOT = $RepoRoot
$env:KONGOR_REPO_ROOT = $RepoRoot
$env:KONGOR_CALLER_CWD = $CallerCwd

function Invoke-RecoveryFallback {
    param([string]$Verb, [string[]]$SubArgs)
    switch ($Verb) {
        { $_ -in @("build", "compile", "rebuild") } {
            $script = Join-Path $RepoRoot "scripts/build.ps1"
            & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $script @SubArgs
            return $LASTEXITCODE
        }
        { $_ -in @("gate", "gate-check", "gatecheck") } {
            $script = Join-Path $RepoRoot "scripts/gate_check.ps1"
            & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $script @SubArgs
            return $LASTEXITCODE
        }
        { $_ -in @("test", "tests", "tunit", "xunit") } {
            $sln = Join-Path $RepoRoot "ProjectKongor.slnx"
            $lock = Join-Path $RepoRoot "scripts/Invoke-KongorTestLock.ps1"
            if (Test-Path -LiteralPath $lock) {
                & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $lock -Owner "ps-fallback-test-$PID" -- dotnet test $sln --nologo @SubArgs
            } else {
                & dotnet test $sln --nologo @SubArgs
            }
            return $LASTEXITCODE
        }
        { $_ -in @("clean", "purge", "deep-clean") } {
            & dotnet clean (Join-Path $RepoRoot "ProjectKongor.slnx") --nologo
            return $LASTEXITCODE
        }
        default { return $null }
    }
}

# Identify the primary verb (skipping leading --root/--workspace options)
$primaryVerb = ""
$verbIndex = -1
for ($i = 0; $i -lt $AllArgs.Count; $i++) {
    $tok = $AllArgs[$i]
    if ($tok -in @("--root", "--workspace")) { $i++; continue }
    if ($tok -like "--root=*" -or $tok -like "--workspace=*") { continue }
    $primaryVerb = $tok.ToLowerInvariant()
    $verbIndex = $i
    break
}
[string[]]$remainingAfterVerb = if ($verbIndex -ge 0 -and ($verbIndex + 1) -lt $AllArgs.Count) {
    @($AllArgs[($verbIndex + 1)..($AllArgs.Count - 1)])
} else { @() }

# Never lock src/tools/kongor-cli/bin/Release while running 'refresh'/'install'
if ($primaryVerb -in @("refresh", "install", "install-cli", "reinstall", "self-update", "update-cli")) {
    $globalShim = Join-Path $HOME ".kongor/bin/kongor$ExeSuffix"
    if (Test-Path -LiteralPath $globalShim -PathType Leaf) {
        & $globalShim --root $RepoRoot @AllArgs
        exit $LASTEXITCODE
    }
    $installScript = Join-Path $RepoRoot "scripts/install-cli.ps1"
    if ($AllArgs -contains "--no-build") {
        & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $installScript -SkipBuild
    } else {
        & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $installScript
    }
    exit $LASTEXITCODE
}

$CliProj = Join-Path $RepoRoot "src/tools/kongor-cli/Kongor.Cli.csproj"
$CliExe = Join-Path $RepoRoot "src/tools/kongor-cli/bin/Release/net11.0/kongor$ExeSuffix"
$TestLock = Join-Path $RepoRoot "scripts/Invoke-KongorTestLock.ps1"

$needsBuild = -not (Test-Path -LiteralPath $CliExe -PathType Leaf)
if (-not $needsBuild) {
    $binTime = (Get-Item -LiteralPath $CliExe).LastWriteTimeUtc
    $cliSrcDir = Join-Path $RepoRoot "src/tools/kongor-cli"
    $newer = Get-ChildItem -Path $cliSrcDir -Recurse -File -Include "*.cs","*.csproj" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" -and $_.LastWriteTimeUtc -gt $binTime } |
        Select-Object -First 1
    if ($newer) { $needsBuild = $true }
}

if ($needsBuild) {
    Write-Host "[kongor.ps1] Bootstrapping Kongor.Cli (Release)..." -ForegroundColor Yellow
    if (Test-Path -LiteralPath $TestLock) {
        & $PwshExe -NoProfile -ExecutionPolicy Bypass -File $TestLock -Owner "kongor-ps1-$PID" -- dotnet build $CliProj -c Release --nologo
    } else {
        & dotnet build $CliProj -c Release --nologo
    }
    if ($LASTEXITCODE -ne 0) {
        $buildExit = $LASTEXITCODE
        Write-Warning "[kongor.ps1] Kongor.Cli bootstrap build failed (exit $buildExit)."
        $fallbackCode = Invoke-RecoveryFallback -Verb $primaryVerb -SubArgs $remainingAfterVerb
        if ($null -ne $fallbackCode) {
            Write-Warning "[kongor.ps1] Executed direct recovery fallback for '$primaryVerb' (exit $fallbackCode)."
            exit $fallbackCode
        }
        Write-Error "[kongor.ps1] Cannot execute '$primaryVerb' until Kongor.Cli compiles. Fix: Run '.\kongor.ps1 build' or 'dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release'."
        exit $buildExit
    }
}

& $CliExe @AllArgs
exit $LASTEXITCODE
