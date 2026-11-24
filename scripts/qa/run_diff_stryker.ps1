# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Diff-scoped Stryker.NET mutation testing orchestrator for Project Kongor.

.DESCRIPTION
    Computes changed C# source files under src/ via `git diff --name-only`, maps each file
    to its enclosing .csproj and matching unit test project(s) under tests/unit/, and runs
    (or prints in -DryRun mode) diff-scoped `dotnet stryker` invocations using stryker-config.json.

.EXAMPLE
    .\scripts\qa\run_diff_stryker.ps1 -DryRun
    .\scripts\qa\run_diff_stryker.ps1 -BaseRef origin/main -HeadRef HEAD -OutputDir artifacts/stryker
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$BaseRef = "origin/main",

    [Parameter(Mandatory = $false)]
    [string]$HeadRef = "HEAD",

    [Parameter(Mandatory = $false)]
    [string]$ConfigPath = "stryker-config.json",

    [Parameter(Mandatory = $false)]
    [string[]]$ChangedFiles = @(),

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "artifacts/stryker",

    [Parameter(Mandatory = $false)]
    [string]$Owner = "diff-stryker-runner",

    [switch]$IncludeWorkingTree,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

function Write-StrykerLog {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Cyan
    )
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$ts] [DIFF-STRYKER] $Message" -ForegroundColor $Color
}

$resolvedConfigPath = if ([System.IO.Path]::IsPathRooted($ConfigPath)) {
    $ConfigPath
} else {
    Join-Path $RepoRoot $ConfigPath
}

if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
    Write-Error "Stryker config not found at '$resolvedConfigPath'. Pass -ConfigPath pointing to stryker-config.json."
    exit 1
}

try {
    $configJson = Get-Content -LiteralPath $resolvedConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $configJson.'stryker-config') {
        Write-Error "Invalid stryker-config.json at '$resolvedConfigPath': missing top-level 'stryker-config' object."
        exit 1
    }
} catch {
    Write-Error "Failed to parse JSON in '$resolvedConfigPath': $($_.Exception.Message)"
    exit 1
}

function Resolve-ChangedCsFiles {
    param(
        [string]$Root,
        [string]$Base,
        [string]$Head,
        [string[]]$ExplicitFiles,
        [bool]$IncludeDirty
    )

    $candidates = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    if ($ExplicitFiles.Count -gt 0) {
        foreach ($ef in $ExplicitFiles) {
            if (-not [string]::IsNullOrWhiteSpace($ef)) {
                [void]$candidates.Add(($ef.Trim() -replace '\\', '/'))
            }
        }
    } else {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $diffLines = @(& git -C $Root diff --name-only --diff-filter=ACMR "$Base...$Head" 2>$null)
            if ($LASTEXITCODE -ne 0) {
                Write-StrykerLog "BaseRef '$Base' not reachable via merge-base; falling back to HEAD~1..$Head." Yellow
                $diffLines = @(& git -C $Root diff --name-only --diff-filter=ACMR "HEAD~1..$Head" 2>$null)
            }
            foreach ($line in $diffLines) {
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    [void]$candidates.Add(($line.Trim() -replace '\\', '/'))
                }
            }

            if ($IncludeDirty -or $candidates.Count -eq 0) {
                $wtLines = @(& git -C $Root status --porcelain --untracked-files=all -- src 2>$null)
                foreach ($wt in $wtLines) {
                    if ($wt.Length -ge 4) {
                        $rel = $wt.Substring(3).Trim().Trim('"')
                        if ($rel -match ' -> (.+)$') {
                            $rel = $Matches[1].Trim().Trim('"')
                        }
                        if (-not [string]::IsNullOrWhiteSpace($rel)) {
                            [void]$candidates.Add(($rel -replace '\\', '/'))
                        }
                    }
                }
            }
        } finally {
            $ErrorActionPreference = $prevEap
        }
    }

    $filtered = New-Object System.Collections.Generic.List[string]
    foreach ($relPath in ($candidates | Sort-Object)) {
        if (-not $relPath.StartsWith("src/", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $relPath.EndsWith(".cs", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($relPath -match '(?i)/(obj|bin)/|\.g\.cs$|Designer\.cs$') { continue }
        $diskPath = Join-Path $Root ($relPath -replace '/', '\')
        if (Test-Path -LiteralPath $diskPath -PathType Leaf) {
            $filtered.Add($relPath)
        }
    }

    return $filtered.ToArray()
}

function Find-EnclosingCsproj {
    param(
        [string]$Root,
        [string]$RelCsPath
    )

    $fullCs = Join-Path $Root ($RelCsPath -replace '/', '\')
    $dir = Split-Path -Parent $fullCs
    $srcRoot = [System.IO.Path]::GetFullPath((Join-Path $Root "src"))

    while (-not [string]::IsNullOrWhiteSpace($dir)) {
        $fullDir = [System.IO.Path]::GetFullPath($dir)
        if (-not $fullDir.StartsWith($srcRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $projs = @(Get-ChildItem -LiteralPath $fullDir -Filter "*.csproj" -File -ErrorAction SilentlyContinue)
        if ($projs.Count -gt 0) {
            return $projs[0].FullName
        }
        $parent = Split-Path -Parent $fullDir
        if ($parent -eq $fullDir) { break }
        $dir = $parent
    }
    return $null
}

function Build-UnitTestProjectReferenceMap {
    param([string]$Root)

    $map = @{}
    $unitRoot = Join-Path $Root "tests\unit"
    if (-not (Test-Path -LiteralPath $unitRoot -PathType Container)) {
        return $map
    }

    $testProjs = @(Get-ChildItem -LiteralPath $unitRoot -Filter "*.csproj" -Recurse -File -ErrorAction SilentlyContinue)
    foreach ($tp in $testProjs) {
        $raw = Get-Content -LiteralPath $tp.FullName -Raw -Encoding UTF8
        $tpDir = Split-Path -Parent $tp.FullName
        foreach ($m in [regex]::Matches($raw, '<ProjectReference\s+Include="([^"]+\.csproj)"')) {
            $refRaw = $m.Groups[1].Value
            $refFull = [System.IO.Path]::GetFullPath((Join-Path $tpDir $refRaw))
            if (-not $map.ContainsKey($refFull)) {
                $map[$refFull] = New-Object System.Collections.Generic.List[string]
            }
            if (-not $map[$refFull].Contains($tp.FullName)) {
                $map[$refFull].Add($tp.FullName)
            }
        }
    }
    return $map
}

function Get-RelativePathCompat {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )
    $normBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $normTarget = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = New-Object System.Uri($normBase)
    $targetUri = New-Object System.Uri($normTarget)
    return ([System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString())) -replace '\\', '/'
}

$changedCs = @(Resolve-ChangedCsFiles -Root $RepoRoot -Base $BaseRef -Head $HeadRef -ExplicitFiles $ChangedFiles -IncludeDirty:$IncludeWorkingTree)
Write-StrykerLog "Discovered $($changedCs.Count) changed C# source file(s) under src/ (base='$BaseRef', head='$HeadRef', dryRun=$DryRun)."

$refMap = Build-UnitTestProjectReferenceMap -Root $RepoRoot
$projectGroups = [ordered]@{}

foreach ($relCs in $changedCs) {
    $csprojFull = Find-EnclosingCsproj -Root $RepoRoot -RelCsPath $relCs
    if ([string]::IsNullOrWhiteSpace($csprojFull)) {
        Write-StrykerLog "Skipping '$relCs': no enclosing .csproj found under src/." Yellow
        continue
    }
    if (-not $projectGroups.Contains($csprojFull)) {
        $projectGroups[$csprojFull] = New-Object System.Collections.Generic.List[string]
    }
    $projDir = Split-Path -Parent $csprojFull
    $fullCs = Join-Path $RepoRoot ($relCs -replace '/', '\')
    $relToProj = Get-RelativePathCompat -BasePath $projDir -TargetPath $fullCs
    $projectGroups[$csprojFull].Add($relToProj)
}

$plans = New-Object System.Collections.Generic.List[object]
foreach ($csprojFull in $projectGroups.Keys) {
    $relProj = Get-RelativePathCompat -BasePath $RepoRoot -TargetPath $csprojFull
    $projName = [System.IO.Path]::GetFileNameWithoutExtension($csprojFull)
    $mutateFiles = @($projectGroups[$csprojFull].ToArray() | Sort-Object -Unique)

    $candidateTests = @(if ($refMap.ContainsKey($csprojFull)) {
        $refMap[$csprojFull].ToArray() | Sort-Object
    })

    # Prefer direct <ProjectName>.Tests match; for Kongor.Cli Mcp/ changes also include Kongor.Mcp.Tests
    $selectedTests = @($candidateTests | Where-Object {
        $testName = [System.IO.Path]::GetFileNameWithoutExtension($_)
        ($testName -ieq "$projName.Tests") -or
        ($projName -ieq "Kongor.Cli" -and $testName -ieq "Kongor.Mcp.Tests" -and ($mutateFiles | Where-Object { $_ -like "Mcp/*" }).Count -gt 0)
    })
    if ($selectedTests.Count -eq 0 -and $candidateTests.Count -gt 0) {
        $selectedTests = @([string]$candidateTests[0])
    }

    $relTests = @($selectedTests | ForEach-Object { Get-RelativePathCompat -BasePath $RepoRoot -TargetPath $_ })
    $mutateJson = ($mutateFiles | ConvertTo-Json -Compress)
    if ($mutateFiles.Count -eq 1) {
        $mutateJson = "[$mutateJson]"
    }

    $strykerArgs = New-Object System.Collections.Generic.List[string]
    $strykerArgs.Add("stryker")
    $strykerArgs.Add("--config-file")
    $strykerArgs.Add($resolvedConfigPath)
    $strykerArgs.Add("--project")
    $strykerArgs.Add([System.IO.Path]::GetFileName($csprojFull))
    foreach ($rt in $relTests) {
        $strykerArgs.Add("--test-project")
        $strykerArgs.Add($rt)
    }
    $strykerArgs.Add("--mutate")
    $strykerArgs.Add($mutateJson)
    $strykerArgs.Add("--output")
    $strykerArgs.Add((Join-Path $OutputDir $projName))

    $plans.Add([PSCustomObject][ordered]@{
        project      = $relProj
        projectName  = $projName
        testProjects = $relTests
        mutateFiles  = $mutateFiles
        command      = "dotnet " + ($strykerArgs -join " ")
        args         = $strykerArgs.ToArray()
    })
}

$resolvedOutDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $RepoRoot $OutputDir }
if (-not (Test-Path -LiteralPath $resolvedOutDir -PathType Container)) {
    New-Item -ItemType Directory -Path $resolvedOutDir -Force | Out-Null
}

$planSummary = [ordered]@{
    generatedUtc    = (Get-Date).ToUniversalTime().ToString("o")
    baseRef         = $BaseRef
    headRef         = $HeadRef
    configPath      = $ConfigPath
    dryRun          = [bool]$DryRun
    changedCsCount  = $changedCs.Count
    affectedProjects = $plans.Count
    plans           = $plans.ToArray()
}

$planFilePath = Join-Path $resolvedOutDir "diff-stryker-plan.json"
[System.IO.File]::WriteAllText($planFilePath, ($planSummary | ConvertTo-Json -Depth 6) + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-StrykerLog "Wrote diff-scoped mutation plan ($($plans.Count) project(s)) to '$planFilePath'." Green

foreach ($p in $plans) {
    $testsDisplay = if ($p.testProjects.Count -gt 0) { $p.testProjects -join ", " } else { "<none matched>" }
    Write-Host ("  [PROJECT] {0} -> tests=[{1}] mutate=[{2}]" -f $p.project, $testsDisplay, ($p.mutateFiles -join ", ")) -ForegroundColor White
    Write-Host ("            cmd: {0}" -f $p.command) -ForegroundColor DarkGray
}

if ($DryRun) {
    Write-StrykerLog "DryRun complete: stryker-config.json valid, $($changedCs.Count) changed C# file(s) mapped across $($plans.Count) project(s)." Green
    exit 0
}

if ($plans.Count -eq 0) {
    Write-StrykerLog "No mutable C# changes detected under src/; skipping Stryker.NET execution (exit 0)." Green
    exit 0
}

$strykerCheck = & dotnet stryker --version 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Stryker.NET CLI ('dotnet stryker') is not installed. Install with: dotnet tool install -g dotnet-stryker --version 4.5.0"
    exit 1
}
Write-StrykerLog "Using dotnet-stryker version: $strykerCheck" Cyan

$lockScript = Join-Path $RepoRoot "scripts\Invoke-KongorTestLock.ps1"
$failedCount = 0
foreach ($p in $plans) {
    if ($p.testProjects.Count -eq 0) {
        Write-StrykerLog "Skipping '$($p.projectName)': no unit test project references this project under tests/unit/." Yellow
        continue
    }
    Write-StrykerLog "Executing diff-scoped Stryker.NET for '$($p.projectName)' ($($p.mutateFiles.Count) file(s))..." Cyan
    if (Test-Path -LiteralPath $lockScript -PathType Leaf) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $lockScript -Owner $Owner -- dotnet @($p.args)
    } else {
        & dotnet @($p.args)
    }
    if ($LASTEXITCODE -ne 0) {
        $failedCount++
        Write-StrykerLog "Stryker.NET failed for '$($p.projectName)' (exitCode=$LASTEXITCODE)." Red
    }
}

if ($failedCount -gt 0) {
    Write-Error "Diff-scoped Stryker.NET failed for $failedCount project(s). Inspect reports under '$resolvedOutDir'."
    exit 1
}

Write-StrykerLog "All diff-scoped Stryker.NET mutation runs passed." Green
exit 0
