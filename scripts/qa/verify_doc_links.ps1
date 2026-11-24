# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Multi-kind (PATH, ANCHOR, COMMAND, SYMBOL) documentation drift verifier with label- and line-invariant SHA-256 fingerprint baselining (#239).

.DESCRIPTION
    Ported from repowise-dev/repowise (Gem 6D):
    - Extracts 4 distinct documentation drift kinds across markdown files:
      1. PATH    : Broken relative or file:/// markdown links to non-existent files.
      2. ANCHOR  : Markdown links with #heading-slug anchors where the target heading does not exist.
      3. COMMAND : Invocations of scripts/<name>.ps1 with non-existent scripts or undeclared -Flag parameters.
      4. SYMBOL  : Symbol links ([symbol:SymbolName](file) or symbol://file#SymbolName) where the symbol is missing in the target file.
    - Computes label-invariant and line-number-invariant fingerprints:
      SHA256("kongor.doc_drift.fingerprint.v1\0" + doc + "\0" + kind + "\0" + target)[:32]
      so line shifts or link-label edits never invalidate baselines.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = "",

    [Parameter(Mandatory = $false)]
    [string]$DocsPath = "",

    [Parameter(Mandatory = $false)]
    [string]$ScriptsRoot = "",

    [Parameter(Mandatory = $false)]
    [switch]$EmitFingerprints,

    [Parameter(Mandatory = $false)]
    [string]$BaselineFile = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [Parameter(Mandatory = $false)]
    [switch]$FailOnUnbaselined,

    [Parameter(Mandatory = $false)]
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
} else {
    $RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
}

if ([string]::IsNullOrWhiteSpace($DocsPath)) {
    $DocsPath = Join-Path $RepoRoot "docs"
} elseif (-not [System.IO.Path]::IsPathRooted($DocsPath)) {
    $DocsPath = Join-Path $RepoRoot $DocsPath
}

if ([string]::IsNullOrWhiteSpace($ScriptsRoot)) {
    $ScriptsRoot = $RepoRoot
}

# satisfies: #239 (repowise-multikind-doc-drift-fingerprint-baseline)
function Get-KongorDocDriftFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DocRelPath,

        [Parameter(Mandatory = $true)]
        [ValidateSet("PATH", "ANCHOR", "COMMAND", "SYMBOL")]
        [string]$Kind,

        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    $normDoc = ($DocRelPath -replace '\\', '/').Trim().TrimStart('/').ToLowerInvariant()
    $normKind = $Kind.Trim().ToUpperInvariant()
    $normTarget = ($Target -replace '\\', '/').Trim()
    $payload = "kongor.doc_drift.fingerprint.v1`0$normDoc`0$normKind`0$normTarget"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
        $hex = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
        return $hex.Substring(0, 32)
    } finally {
        $sha.Dispose()
    }
}

function ConvertTo-GitHubHeadingSlug {
    param([string]$HeadingText)
    if ([string]::IsNullOrWhiteSpace($HeadingText)) { return "" }
    $clean = $HeadingText.Trim()
    $clean = [regex]::Replace($clean, '\[([^\]]+)\]\([^)]+\)', '$1')
    $clean = $clean -replace '[`*~]', ''
    $clean = $clean.ToLowerInvariant()
    # GitHub github-slugger removes punctuation/emoji first (keeping spaces and hyphens), then maps each space -> '-'.
    $clean = [regex]::Replace($clean, '[^a-z0-9 \-_]', '')
    $clean = $clean.Trim() -replace ' ', '-'
    return $clean
}

$script:DocHeadingSlugCache = @{}
function Get-MarkdownHeadingSlugs {
    param([string]$FilePath)
    if ($script:DocHeadingSlugCache.ContainsKey($FilePath)) {
        return $script:DocHeadingSlugCache[$FilePath]
    }
    $slugSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if (Test-Path -LiteralPath $FilePath -PathType Leaf) {
        $raw = [System.IO.File]::ReadAllText($FilePath)
        $counts = @{}
        foreach ($hm in [regex]::Matches($raw, '(?m)^#{1,6}\s+(.+?)\s*$')) {
            $baseSlug = ConvertTo-GitHubHeadingSlug -HeadingText $hm.Groups[1].Value
            if (-not [string]::IsNullOrWhiteSpace($baseSlug)) {
                [void]$slugSet.Add($baseSlug)
                # Also accept single-hyphen collapsed form for hand-written anchors
                $collapsedSlug = [regex]::Replace($baseSlug, '-{2,}', '-')
                if (-not [string]::IsNullOrWhiteSpace($collapsedSlug)) {
                    [void]$slugSet.Add($collapsedSlug)
                }
                if ($counts.ContainsKey($baseSlug)) {
                    $idx = [int]$counts[$baseSlug]
                    [void]$slugSet.Add("$baseSlug-$idx")
                    [void]$slugSet.Add("$collapsedSlug-$idx")
                    $counts[$baseSlug] = $idx + 1
                } else {
                    $counts[$baseSlug] = 1
                }
            }
        }
    }
    $script:DocHeadingSlugCache[$FilePath] = $slugSet
    return $slugSet
}

$script:AllowedScriptParamCache = @{}
function Get-AllowedScriptParameters {
    param([string]$ScriptRelPath)
    if ($script:AllowedScriptParamCache.ContainsKey($ScriptRelPath)) {
        return $script:AllowedScriptParamCache[$ScriptRelPath]
    }
    $relBackslash = $ScriptRelPath -replace '/', '\'
    $candPath = Join-Path $RepoRoot $relBackslash
    if (-not (Test-Path -LiteralPath $candPath -PathType Leaf)) {
        $candPath = Join-Path $ScriptsRoot $relBackslash
    }
    if (-not (Test-Path -LiteralPath $candPath -PathType Leaf)) {
        $script:AllowedScriptParamCache[$ScriptRelPath] = $null
        return $null
    }
    $paramSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($cp in @("Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable", "WhatIf", "Confirm", "NoProfile", "ExecutionPolicy", "File")) {
        [void]$paramSet.Add($cp)
    }
    $tokens = $null; $errs = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($candPath, [ref]$tokens, [ref]$errs)
    if ($null -ne $ast -and $null -ne $ast.ParamBlock) {
        foreach ($pAst in $ast.ParamBlock.Parameters) {
            [void]$paramSet.Add($pAst.Name.VariablePath.UserPath)
            foreach ($attr in $pAst.Attributes) {
                if ($attr -is [System.Management.Automation.Language.AttributeAst] -and $attr.TypeName.Name -match '^Alias') {
                    foreach ($posArg in $attr.PositionalArguments) {
                        if ($posArg -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                            [void]$paramSet.Add($posArg.Value)
                        }
                    }
                }
            }
        }
    } elseif ($null -ne $ast) {
        # Scripts that manually parse $args (e.g. Invoke-KongorTestLock.ps1 with '--' passthrough) declare flags in switch regexes
        foreach ($mFlag in [regex]::Matches($ast.Extent.Text, '[\x27"]\^-([A-Za-z][A-Za-z0-9_]*)\$[\x27"]')) {
            [void]$paramSet.Add($mFlag.Groups[1].Value)
        }
    }
    $script:AllowedScriptParamCache[$ScriptRelPath] = $paramSet
    return $paramSet
}

function Get-KongorDocDriftFindings {
    [CmdletBinding()]
    param(
        [string]$TargetRepoRoot = $RepoRoot,
        [string]$TargetDocsPath = $DocsPath,
        [string]$TargetBaselineFile = $BaselineFile
    )

    $baselineSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not [string]::IsNullOrWhiteSpace($TargetBaselineFile) -and (Test-Path -LiteralPath $TargetBaselineFile -PathType Leaf)) {
        try {
            $bJson = Get-Content -LiteralPath $TargetBaselineFile -Raw | ConvertFrom-Json
            foreach ($fpItem in @($bJson.fingerprints)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$fpItem)) {
                    [void]$baselineSet.Add(([string]$fpItem).Trim())
                }
            }
        } catch {}
    }

    $findings = New-Object System.Collections.Generic.List[object]
    $seenFingerprints = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $totalLinks = 0

    $docFiles = @()
    if (Test-Path -LiteralPath $TargetDocsPath -PathType Leaf) {
        $docFiles = @(Get-Item -LiteralPath $TargetDocsPath)
    } elseif (Test-Path -LiteralPath $TargetDocsPath -PathType Container) {
        $docFiles = @(Get-ChildItem -LiteralPath $TargetDocsPath -Filter "*.md" -Recurse -File | Where-Object { $_.FullName -notlike "*\_site\*" })
    } else {
        return [PSCustomObject]@{
            totalLinksScanned = 0
            totalFindings     = 0
            unbaselinedCount  = 0
            baselinedCount    = 0
            findings          = @()
        }
    }

    foreach ($file in $docFiles) {
        $relDoc = $file.FullName.Substring($TargetRepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $content = [System.IO.File]::ReadAllText($file.FullName)
        $dir = $file.DirectoryName

        # 1. Markdown links: PATH, ANCHOR, and [symbol:Name](path)
        $linkMatches = [regex]::Matches($content, '\[([^\]]+)\]\(([^)]+)\)')
        foreach ($m in $linkMatches) {
            $label = $m.Groups[1].Value.Trim()
            $url = $m.Groups[2].Value.Trim()
            if ($url.StartsWith("http", [System.StringComparison]::OrdinalIgnoreCase) -or
                $url.StartsWith("mailto:", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            # Same-file anchor: [label](#section-slug)
            if ($url.StartsWith("#")) {
                $totalLinks++
                $anchorSlug = $url.Substring(1).Trim()
                if (-not [string]::IsNullOrWhiteSpace($anchorSlug)) {
                    $slugs = Get-MarkdownHeadingSlugs -FilePath $file.FullName
                    if (-not $slugs.Contains($anchorSlug)) {
                        $fp = Get-KongorDocDriftFingerprint -DocRelPath $relDoc -Kind "ANCHOR" -Target $url
                        if ($seenFingerprints.Add($fp)) {
                            $isBaselined = $baselineSet.Contains($fp)
                            $findings.Add([PSCustomObject]@{
                                doc         = $relDoc
                                kind        = "ANCHOR"
                                target      = $url
                                label       = $label
                                fingerprint = $fp
                                baselined   = $isBaselined
                            })
                        }
                    }
                }
                continue
            }

            $parts = $url -split '#', 2
            $cleanUrl = $parts[0].Trim()
            $anchorPart = if ($parts.Length -gt 1) { $parts[1].Trim() } else { "" }
            if ([string]::IsNullOrWhiteSpace($cleanUrl)) { continue }

            $isSymbolScheme = $cleanUrl.StartsWith("symbol://", [System.StringComparison]::OrdinalIgnoreCase)
            if ($isSymbolScheme) {
                $cleanUrl = $cleanUrl.Substring(9).TrimStart('/')
            }

            $totalLinks++
            if ($cleanUrl.StartsWith("file:///", [System.StringComparison]::OrdinalIgnoreCase)) {
                $pathPart = $cleanUrl.Substring(8).Replace("/", "\")
                if ($pathPart -match '^(?i)[A-Za-z]:\\(?:Users\\[^\\]+\\PycharmProjects\\ProjectKongor|ProjectKongor)\\(.*)$') { # kongor-security-ignore
                    $resolved = Join-Path $TargetRepoRoot $matches[1]
                } elseif ($pathPart -match '^[A-Za-z]:\\') {
                    $resolved = $pathPart
                } else {
                    # Headless or relative file:/// URI (e.g. file:///captures/...) resolves against drive root on Windows (e.g. C:\captures\...)
                    $resolved = Join-Path ([System.IO.Path]::GetPathRoot($TargetRepoRoot)) $pathPart
                }
            } elseif ($isSymbolScheme) {
                $candRoot = Join-Path $TargetRepoRoot ($cleanUrl -replace '/', '\')
                $candLocal = Join-Path $dir ($cleanUrl -replace '/', '\')
                $resolved = if (Test-Path -LiteralPath $candRoot) { $candRoot } else { $candLocal }
            } else {
                $resolved = Join-Path $dir ($cleanUrl -replace '/', '\')
            }

            if (-not (Test-Path -LiteralPath $resolved)) {
                $normTarget = $cleanUrl -replace '\\', '/'
                $fp = Get-KongorDocDriftFingerprint -DocRelPath $relDoc -Kind "PATH" -Target $normTarget
                if ($seenFingerprints.Add($fp)) {
                    $isBaselined = $baselineSet.Contains($fp)
                    $findings.Add([PSCustomObject]@{
                        doc         = $relDoc
                        kind        = "PATH"
                        target      = $normTarget
                        label       = $label
                        fingerprint = $fp
                        baselined   = $isBaselined
                    })
                }
                continue
            }

            # Cross-file markdown heading anchor check (#239 ANCHOR)
            if (-not [string]::IsNullOrWhiteSpace($anchorPart) -and $resolved.EndsWith(".md", [System.StringComparison]::OrdinalIgnoreCase) -and $anchorPart -notmatch '^L\d+') {
                $slugs = Get-MarkdownHeadingSlugs -FilePath $resolved
                if (-not $slugs.Contains($anchorPart)) {
                    $normTarget = ($cleanUrl -replace '\\', '/') + "#" + $anchorPart
                    $fp = Get-KongorDocDriftFingerprint -DocRelPath $relDoc -Kind "ANCHOR" -Target $normTarget
                    if ($seenFingerprints.Add($fp)) {
                        $isBaselined = $baselineSet.Contains($fp)
                        $findings.Add([PSCustomObject]@{
                            doc         = $relDoc
                            kind        = "ANCHOR"
                            target      = $normTarget
                            label       = $label
                            fingerprint = $fp
                            baselined   = $isBaselined
                        })
                    }
                }
            }

            # Explicit symbol contract reference in link label [symbol:SymbolName](path) or symbol://file#SymbolName (#239 SYMBOL)
            $symName = ""
            if ($label -match '^symbol:([A-Za-z_][A-Za-z0-9_]*)$') {
                $symName = $Matches[1]
            } elseif ($isSymbolScheme -and $anchorPart -match '^([A-Za-z_][A-Za-z0-9_]*)$') {
                $symName = $Matches[1]
            }
            if (-not [string]::IsNullOrWhiteSpace($symName) -and (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                $targetContent = [System.IO.File]::ReadAllText($resolved)
                if ($targetContent -notmatch "\b$([regex]::Escape($symName))\b") {
                    $normTarget = ($cleanUrl -replace '\\', '/') + "#" + $symName
                    $fp = Get-KongorDocDriftFingerprint -DocRelPath $relDoc -Kind "SYMBOL" -Target $normTarget
                    if ($seenFingerprints.Add($fp)) {
                        $isBaselined = $baselineSet.Contains($fp)
                        $findings.Add([PSCustomObject]@{
                            doc         = $relDoc
                            kind        = "SYMBOL"
                            target      = $normTarget
                            label       = $label
                            fingerprint = $fp
                            baselined   = $isBaselined
                        })
                    }
                }
            }
        }

        # 2. COMMAND drift: scripts/<name>.ps1 (including domain subfolders) and -<Flag> existence (#239 COMMAND)
        foreach ($cmdMatch in [regex]::Matches($content, '(?m)(?<!\]\()scripts[\\/]([A-Za-z0-9_./\\-]+\.ps1)([^\r\n`")]*)')) {
            $sRel = "scripts/" + ($cmdMatch.Groups[1].Value -replace '\\', '/')
            $sArgs = $cmdMatch.Groups[2].Value
            $allowedParams = Get-AllowedScriptParameters -ScriptRelPath $sRel
            if ($null -eq $allowedParams) {
                $fp = Get-KongorDocDriftFingerprint -DocRelPath $relDoc -Kind "COMMAND" -Target $sRel
                if ($seenFingerprints.Add($fp)) {
                    $isBaselined = $baselineSet.Contains($fp)
                    $findings.Add([PSCustomObject]@{
                        doc         = $relDoc
                        kind        = "COMMAND"
                        target      = $sRel
                        label       = $sRel
                        fingerprint = $fp
                        baselined   = $isBaselined
                    })
                }
            } else {
                foreach ($flMatch in [regex]::Matches($sArgs, '(?<=\s)-([A-Za-z][A-Za-z0-9_]*)\b')) {
                    $flagName = $flMatch.Groups[1].Value
                    if (-not $allowedParams.Contains($flagName)) {
                        $cmdTarget = "$sRel -$flagName"
                        $fp = Get-KongorDocDriftFingerprint -DocRelPath $relDoc -Kind "COMMAND" -Target $cmdTarget
                        if ($seenFingerprints.Add($fp)) {
                            $isBaselined = $baselineSet.Contains($fp)
                            $findings.Add([PSCustomObject]@{
                                doc         = $relDoc
                                kind        = "COMMAND"
                                target      = $cmdTarget
                                label       = $cmdTarget
                                fingerprint = $fp
                                baselined   = $isBaselined
                            })
                        }
                    }
                }
            }
        }
    }

    $unbaselined = @($findings | Where-Object { -not $_.baselined })
    $baselined = @($findings | Where-Object { $_.baselined })

    return [PSCustomObject]@{
        totalLinksScanned = $totalLinks
        totalFindings     = $findings.Count
        unbaselinedCount  = $unbaselined.Count
        baselinedCount    = $baselined.Count
        findings          = $findings.ToArray()
    }
}

$report = Get-KongorDocDriftFindings -TargetRepoRoot $RepoRoot -TargetDocsPath $DocsPath -TargetBaselineFile $BaselineFile
$brokenPaths = @($report.findings | Where-Object { $_.kind -eq "PATH" -and -not $_.baselined })

if (-not $Quiet) {
    foreach ($f in $report.findings) {
        if ($f.baselined) { continue }
        if ($EmitFingerprints -or $f.kind -eq "PATH") {
            $fpSuffix = if ($EmitFingerprints) { " [fp:$($f.fingerprint)]" } else { "" }
            Write-Host "[BROKEN:$($f.kind)] \$($f.doc) -> $($f.target)$fpSuffix" -ForegroundColor Red
        }
    }
    Write-Host "Scanned $($report.totalLinksScanned) relative links across markdown documents." -ForegroundColor Cyan
    $brokenCountToShow = if ($EmitFingerprints) { $report.unbaselinedCount } else { $brokenPaths.Count }
    $color = if ($brokenCountToShow -eq 0) { "Green" } else { "Red" }
    Write-Host "Total broken relative links found: $brokenCountToShow" -ForegroundColor $color
}

if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    $outJson = $report | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($JsonOutput, $outJson, (New-Object System.Text.UTF8Encoding($false)))
}

if ($FailOnUnbaselined -and $report.unbaselinedCount -gt 0) {
    exit 1
}
