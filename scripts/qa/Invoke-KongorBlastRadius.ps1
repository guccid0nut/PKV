# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Exponential distance-decayed (decay = 0.6^d) transitive blast-radius analyzer and downstream unit-test ranker (#244).

.DESCRIPTION
    Ported from repowise-dev/repowise (Gem 6E):
    - Traverses reverse dependency edges (dependency -> dependent) via BFS up to -MaxDepth hops from
      each modified seed node u in -ChangedNodes.
    - Computes cumulative distance-decayed transitive impact score:
        I(v) = sum_{u in ChangedNodes, 1 <= dist(u,v) <= MaxDepth} (Decay ^ dist(u,v))
      with default Decay = 0.6 (hop 1 = 0.60, hop 2 = 0.36, hop 3 = 0.216, hop 4 = 0.1296).
    - Ranks downstream affected modules and unit tests by I(v) descending (tie-broken by shortest hop
      distance, then lexicographical node name).

.EXAMPLE
    .\scripts\Invoke-KongorBlastRadius.ps1 -ChangedNodes @("src/libs/contracts/NEXUS.Contracts.csproj")
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [Alias("ChangedFiles", "SeedFiles")]
    [string[]]$ChangedNodes = @(),

    [Parameter(Mandatory = $false)]
    [string]$GraphJson = "",

    [Parameter(Mandatory = $false)]
    [string]$GraphJsonPath = "",

    [Parameter(Mandatory = $false)]
    [ValidateRange(0.05, 0.99)]
    [double]$Decay = 0.6,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 16)]
    [int]$MaxDepth = 6,

    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = "",

    [Parameter(Mandatory = $false)]
    [Alias("OutputPath")]
    [string]$JsonOutputPath = "",

    [switch]$JsonOutput,

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
} else {
    $RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
}

$ChangedNodes = @($ChangedNodes | ForEach-Object { $_ -split ',' } | ForEach-Object { ($_.Trim() -replace '\\', '/') } | Where-Object { $_ })

# Reverse adjacency map: dependency -> HashSet of dependents that import/reference it
$reverseAdj = @{}
$allNodesSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

function Add-DependencyEdge {
    param(
        [string]$Dependent,
        [string]$Dependency
    )
    $normDep = ($Dependent -replace '\\', '/').Trim()
    $normTarget = ($Dependency -replace '\\', '/').Trim()
    if ([string]::IsNullOrWhiteSpace($normDep) -or [string]::IsNullOrWhiteSpace($normTarget) -or $normDep.Equals($normTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    [void]$allNodesSet.Add($normDep)
    [void]$allNodesSet.Add($normTarget)
    if (-not $reverseAdj.ContainsKey($normTarget)) {
        $reverseAdj[$normTarget] = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    }
    [void]$reverseAdj[$normTarget].Add($normDep)
}

if (-not [string]::IsNullOrWhiteSpace($GraphJsonPath) -and (Test-Path -LiteralPath $GraphJsonPath -PathType Leaf)) {
    $GraphJson = [System.IO.File]::ReadAllText($GraphJsonPath)
}

if (-not [string]::IsNullOrWhiteSpace($GraphJson)) {
    $parsedGraph = $GraphJson | ConvertFrom-Json
    foreach ($edge in @($parsedGraph.edges)) {
        $dependentNode = if ($edge.dependent) { [string]$edge.dependent } else { [string]$edge.from }
        $dependencyNode = if ($edge.dependency) { [string]$edge.dependency } else { [string]$edge.to }
        Add-DependencyEdge -Dependent $dependentNode -Dependency $dependencyNode
    }
} else {
    # Auto-discover project references from .csproj files in RepoRoot/src and RepoRoot/tests
    foreach ($scanSub in @("src", "tests")) {
        $subDir = Join-Path $RepoRoot $scanSub
        if (Test-Path -LiteralPath $subDir -PathType Container) {
            $csprojs = @(Get-ChildItem -LiteralPath $subDir -Filter "*.csproj" -Recurse -File)
            foreach ($cp in $csprojs) {
                $relCp = $cp.FullName.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                $rawXml = [System.IO.File]::ReadAllText($cp.FullName)
                foreach ($pm in [regex]::Matches($rawXml, '<ProjectReference\s+Include="([^"]+)"')) {
                    $refRel = $pm.Groups[1].Value
                    $resolvedRef = [System.IO.Path]::GetFullPath((Join-Path $cp.DirectoryName $refRel))
                    if ($resolvedRef.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $normRef = $resolvedRef.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                        Add-DependencyEdge -Dependent $relCp -Dependency $normRef
                    }
                }
            }
        }
    }
}

# Accumulate distance-decayed impact I(v) = sum_{u in ChangedNodes} (Decay ^ dist(u,v))
$impactScores = @{}
$minDistances = @{}
$contributingSeeds = @{}

foreach ($seed in $ChangedNodes) {
    $visitedDist = @{}
    $queue = New-Object 'System.Collections.Generic.Queue[string]'
    $visitedDist[$seed] = 0
    $queue.Enqueue($seed)

    while ($queue.Count -gt 0) {
        $curr = $queue.Dequeue()
        $currDist = [int]$visitedDist[$curr]
        if ($currDist -ge $MaxDepth) { continue }

        if ($reverseAdj.ContainsKey($curr)) {
            foreach ($nextNode in $reverseAdj[$curr]) {
                if (-not $visitedDist.ContainsKey($nextNode)) {
                    $nextDist = $currDist + 1
                    $visitedDist[$nextNode] = $nextDist
                    $queue.Enqueue($nextNode)

                    $contrib = [Math]::Pow($Decay, $nextDist)
                    if ($impactScores.ContainsKey($nextNode)) {
                        $impactScores[$nextNode] = [double]$impactScores[$nextNode] + $contrib
                    } else {
                        $impactScores[$nextNode] = $contrib
                    }

                    if ((-not $minDistances.ContainsKey($nextNode)) -or ($nextDist -lt [int]$minDistances[$nextNode])) {
                        $minDistances[$nextNode] = $nextDist
                    }

                    if (-not $contributingSeeds.ContainsKey($nextNode)) {
                        $contributingSeeds[$nextNode] = New-Object System.Collections.Generic.List[string]
                    }
                    $contributingSeeds[$nextNode].Add("${seed}@d=${nextDist}")
                }
            }
        }
    }
}

$affectedRecords = New-Object System.Collections.Generic.List[object]
foreach ($nodeKey in $impactScores.Keys) {
    $score = [Math]::Round([double]$impactScores[$nodeKey], 6)
    $minD = [int]$minDistances[$nodeKey]
    $isTest = ($nodeKey -match '(?i)(Tests?\.cs|\.Tests\b|^tests/|/tests/|^test_)')
    $affectedRecords.Add([PSCustomObject]@{
        node         = $nodeKey
        is_test      = $isTest
        impact_score = $score
        min_distance = $minD
        contributors = @($contributingSeeds[$nodeKey].ToArray())
    })
}

$sortedAll = @($affectedRecords | Sort-Object -Property @{ Expression = { -$_.impact_score } }, @{ Expression = { $_.min_distance } }, @{ Expression = { $_.node } })
$rankedTests = @($sortedAll | Where-Object { $_.is_test })
$affectedModules = @($sortedAll | Where-Object { -not $_.is_test })
$maxScore = if ($sortedAll.Count -gt 0) { [double]$sortedAll[0].impact_score } else { 0.0 }

$result = [ordered]@{
    schema_version    = "1.0.0"
    changed_seeds     = @($ChangedNodes)
    decay_factor      = $Decay
    max_depth         = $MaxDepth
    total_affected    = $sortedAll.Count
    max_impact_score  = $maxScore
    ranked_tests      = $rankedTests
    affected_modules  = $affectedModules
    all_ranked        = $sortedAll
}

if (-not $Quiet) {
    Write-Host "[BLAST-RADIUS-244] seeds=$($ChangedNodes.Count) decay=$Decay total_affected=$($sortedAll.Count) ranked_tests=$($rankedTests.Count) max_impact=$maxScore" -ForegroundColor Cyan
}

$jsonString = $result | ConvertTo-Json -Depth 6
if (-not [string]::IsNullOrWhiteSpace($JsonOutputPath)) {
    [System.IO.File]::WriteAllText($JsonOutputPath, $jsonString, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Output $jsonString
exit 0
