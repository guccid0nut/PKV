# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Deterministic, idempotent, BOM/CRLF/shebang-preserving SPDX & provenance header stamper.
.DESCRIPTION
    Applies exact 7-bit ASCII REUSE/SPDX top-of-file headers across Project KONGOR V cleanroom
    source, native C/C++, script, and test files driven by config/header_provenance_manifest.json.
    Never modifies games/**, third-party vendored code, auto-generated files, files that already carry
    an SPDX-License-Identifier header, or files with uncommitted changes from concurrent sessions.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$ManifestPath = "",
    [switch]$IncludeDirtyWip,
    [switch]$ScanNewFiles,
    [switch]$DryRun,
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
} else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $RepoRoot "config\header_provenance_manifest.json"
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "[HEADER-STAMP] Provenance manifest not found at '$ManifestPath'. Run Step 0 provenance oracle first."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$upstreamYears = [string]$manifest.upstreamYearSpan
if ([string]::IsNullOrWhiteSpace($upstreamYears)) {
    throw "[HEADER-STAMP] Manifest is missing upstreamYearSpan."
}

function Get-CommentPrefix {
    param([string]$Extension, [string]$FileName = "")
    if ($FileName -ieq "CMakeLists.txt" -or $FileName -ieq "Makefile") { return "#" }
    switch ($Extension.ToLowerInvariant()) {
        ".py"    { return "#" }
        ".ps1"   { return "#" }
        ".psm1"  { return "#" }
        ".psd1"  { return "#" }
        ".sh"    { return "#" }
        ".cmake" { return "#" }
        ".yaml"  { return "#" }
        ".yml"   { return "#" }
        ".toml"  { return "#" }
        ".lua"   { return "--" }
        ".cmd"   { return "REM" }
        ".bat"   { return "REM" }
        default  { return "//" }
    }
}

function Get-HeaderLines {
    param(
        [string]$Tier,
        [string]$CommentPrefix,
        [string]$UpstreamYearSpan
    )
    switch ($Tier) {
        "TIER_1A_TAO_MCP_AGPL3" {
            return @(
                "$CommentPrefix SPDX-License-Identifier: AGPL-3.0-or-later",
                "$CommentPrefix SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors",
                "$CommentPrefix TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)"
            )
        }
        "TIER_1B_PKV_ORIGINAL_AGPL3" {
            return @(
                "$CommentPrefix SPDX-License-Identifier: AGPL-3.0-or-later",
                "$CommentPrefix SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors",
                "$CommentPrefix Project KONGOR V (https://github.com/guccidonut/PKV)"
            )
        }
        "TIER_2_NATIVE_MPL2" {
            return @(
                "$CommentPrefix SPDX-License-Identifier: MPL-2.0",
                "$CommentPrefix SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors",
                "$CommentPrefix This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.",
                "$CommentPrefix If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/."
            )
        }
        "TIER_3_UPSTREAM_SHARED_AGPL3" {
            return @(
                "$CommentPrefix SPDX-License-Identifier: AGPL-3.0-or-later",
                "$CommentPrefix SPDX-FileCopyrightText: $UpstreamYearSpan [K]ONGOR and Contributors",
                "$CommentPrefix SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors",
                "$CommentPrefix Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source"
            )
        }
        "TIER_4A_BLENDER_GPL3" {
            return @(
                "$CommentPrefix SPDX-License-Identifier: GPL-3.0-or-later",
                "$CommentPrefix SPDX-FileCopyrightText: 2024-2026 Anton Romanov, K2-Blender Contributors",
                "$CommentPrefix SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors",
                "$CommentPrefix Project KONGOR V (https://github.com/guccidonut/PKV)"
            )
        }
        "TIER_4B_EBPF_DUAL" {
            return @(
                "$CommentPrefix SPDX-License-Identifier: GPL-2.0-only OR BSD-2-Clause",
                "$CommentPrefix SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors",
                "$CommentPrefix Project KONGOR V (https://github.com/guccidonut/PKV)"
            )
        }
        default {
            return @()
        }
    }
}

function Resolve-DefaultTierForNewPath {
    param([string]$RelNorm)
    if ($RelNorm -match '^(games/|tests/client_lua/|native/deps/|native/vulkan-layer/deps/|native/vulkan-layer/include/vulkan/|native/vulkan-layer/include/vk_video/|native/include/concurrentqueue\.h$|native/tools/luac51/lua-5\.1\.5/|src/tools/blender/reference/|src/tools/mixamo-mcp/|src/tools/proximo/|src/tools/voicebox/voicebox-repo/|src/tools/blender/K2-Blender-master/|src/services/web-ui/wwwroot/lib/)') {
        return "SKIP_VENDOR"
    }
    if ($RelNorm -match '(/Migrations/.*\.cs$|/bin/|/obj/|k2_structs\.generated\.h$|\.g\.cs$|\.Designer\.cs$)') {
        return "SKIP_GENERATED"
    }
    if ($RelNorm -match '^(src/tools/kongor-cli/|src/tools/kongor-shim/|src/tools/kongor/|src/tools/tao_mcp\.py$|src/tools/MCP\.py$|scripts/dev/install_tao_mcp\.ps1$|tests/unit/tools/Kongor\.Mcp\.Tests/|tests/unit/tools/Kongor\.Cli\.Tests/)') {
        return "TIER_1A_TAO_MCP_AGPL3"
    }
    if ($RelNorm -match '^(native/ebpf/|infra/ebpf/|native/rust/kongor_xdp_aya/ebpf/)') {
        return "TIER_4B_EBPF_DUAL"
    }
    if ($RelNorm -match '^src/tools/blender/io_scene_hon/') {
        return "TIER_4A_BLENDER_GPL3"
    }
    if ($RelNorm -match '^native/(CMakeLists\.txt$|cmake/|drivers/|goose/|include/|proxies/|rust/|server/|shared/|tests/|tools/kongor-cas/|tools/kongor-metagen/|tools/kongor-model-viewer/|tools/kongor-physics-sandbox/|tools/luac51/CMakeLists\.txt$|vulkan-layer/CMakeLists\.txt$|vulkan-layer/src/)') {
        return "TIER_2_NATIVE_MPL2"
    }
    return "TIER_1B_PKV_ORIGINAL_AGPL3"
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$stampedCount = 0
$alreadyStampedCount = 0
$skippedWipCount = 0
$skippedOtherCount = 0
$missingInVerify = New-Object System.Collections.Generic.List[string]

$entriesToProcess = New-Object System.Collections.Generic.List[object]
$knownPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $entriesToProcess.Add($entry)
    [void]$knownPaths.Add(([string]$entry.path -replace '\\', '/'))
}

if ($ScanNewFiles) {
    $scanSkipDirRegex = '[\\/](?:blender-[^\\/]+|K2-Blender-master|ComfyUI|clangd_[^\\/]+|forge-scraper|mixamo-mcp|proximo|voicebox-repo|reference|deps|_deps|CMakeFiles|node_modules|\.venv|venv|bin|obj|target|build|build[-_][^\\/]+)[\\/]'
    $scanExts = @(".cs", ".cpp", ".c", ".h", ".hpp", ".rs", ".py", ".ps1", ".psm1", ".psd1", ".sh", ".cmake", ".lua", ".cmd", ".bat", ".js", ".mjs", ".ts")
    foreach ($scanRootRel in @("src", "native", "scripts", "infra", "tests", "tools")) {
        $scanDir = Join-Path $RepoRoot $scanRootRel
        if (-not (Test-Path -LiteralPath $scanDir -PathType Container)) { continue }
        $stack = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
        $stack.Push([System.IO.DirectoryInfo]::new($scanDir))
        while ($stack.Count -gt 0) {
            $curDir = $stack.Pop()
            foreach ($subDir in $curDir.GetDirectories()) {
                if (($subDir.FullName + "\") -notmatch $scanSkipDirRegex) {
                    $stack.Push($subDir)
                }
            }
            foreach ($f in $curDir.GetFiles()) {
                if ($f.Extension.ToLowerInvariant() -notin $scanExts -and $f.Name -ine "CMakeLists.txt") { continue }
                $relCand = $f.FullName.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                if ($knownPaths.Add($relCand)) {
                    $entriesToProcess.Add([pscustomobject]@{
                        path = $relCand
                        tier = (Resolve-DefaultTierForNewPath -RelNorm $relCand)
                        isDirtyConcurrentWip = $false
                    })
                }
            }
        }
    }
}

foreach ($entry in $entriesToProcess) {
    $rel = [string]$entry.path
    $tier = [string]$entry.tier

    if ($tier.StartsWith("SKIP_", [System.StringComparison]::OrdinalIgnoreCase)) {
        $skippedOtherCount++
        continue
    }
    if ((-not $IncludeDirtyWip) -and [bool]$entry.isDirtyConcurrentWip) {
        $skippedWipCount++
        continue
    }

    $fullPath = Join-Path $RepoRoot ($rel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        $skippedOtherCount++
        continue
    }

    $rawBytes = [System.IO.File]::ReadAllBytes($fullPath)
    $hasBom = ($rawBytes.Length -ge 3 -and $rawBytes[0] -eq 0xEF -and $rawBytes[1] -eq 0xBB -and $rawBytes[2] -eq 0xBF)
    $offset = if ($hasBom) { 3 } else { 0 }
    $content = $utf8NoBom.GetString($rawBytes, $offset, $rawBytes.Length - $offset)

    # Check first 15 lines for idempotency or auto-generated markers
    $lines = $content -split "`r?`n", 16
    $headSample = ($lines | Select-Object -First ([Math]::Min(15, $lines.Count))) -join "`n"

    if ($headSample -match '(?i)<auto-generated') {
        $skippedOtherCount++
        continue
    }
    if ($headSample -match 'SPDX-License-Identifier:') {
        $alreadyStampedCount++
        continue
    }

    if ($VerifyOnly) {
        $missingInVerify.Add("$rel ($tier)")
        continue
    }

    $ext = [System.IO.Path]::GetExtension($rel)
    $fname = [System.IO.Path]::GetFileName($rel)
    $prefix = Get-CommentPrefix -Extension $ext -FileName $fname
    $hdrLines = @(Get-HeaderLines -Tier $tier -CommentPrefix $prefix -UpstreamYearSpan $upstreamYears)
    if ($hdrLines.Count -eq 0) {
        $skippedOtherCount++
        continue
    }

    # Enforce strict 7-bit ASCII invariant on all generated header lines
    foreach ($hl in $hdrLines) {
        foreach ($ch in $hl.ToCharArray()) {
            if ([int][char]$ch -gt 127) {
                throw "[HEADER-STAMP] Non-ASCII character detected in header template for '$rel': '$hl'"
            }
        }
    }

    # Detect dominant line ending in file (preserve CRLF vs LF)
    $crlfMatches = ([regex]::Matches($content, "`r`n")).Count
    $lfTotal = ([regex]::Matches($content, "`n")).Count
    $nl = if ($lfTotal -eq 0 -or $crlfMatches -ge ($lfTotal / 2)) { "`r`n" } else { "`n" }

    # Preserve line-1 shebang (#!) and line-1/2 PEP 263 coding cookie
    $preservedTop = New-Object System.Collections.Generic.List[string]
    $restContent = $content
    for ($i = 0; $i -lt 2; $i++) {
        $nlIdx = $restContent.IndexOf("`n")
        $firstLineRaw = if ($nlIdx -ge 0) { $restContent.Substring(0, $nlIdx).TrimEnd("`r") } else { $restContent }
        $isShebang = ($i -eq 0 -and $firstLineRaw.StartsWith("#!"))
        $isCoding = ($ext -ieq ".py" -and $firstLineRaw -match 'coding[:=]\s*[-\w.]+')
        if ($isShebang -or $isCoding) {
            $preservedTop.Add($firstLineRaw)
            $restContent = if ($nlIdx -ge 0) { $restContent.Substring($nlIdx + 1) } else { "" }
        } else {
            break
        }
    }

    $restContent = $restContent.TrimStart("`r", "`n")
    $hdrBlock = ($hdrLines -join $nl)
    $prefixBlock = if ($preservedTop.Count -gt 0) {
        ($preservedTop -join $nl) + $nl + $hdrBlock
    } else {
        $hdrBlock
    }

    $newContent = if ([string]::IsNullOrEmpty($restContent)) {
        $prefixBlock + $nl
    } else {
        $prefixBlock + $nl + $nl + $restContent
    }

    if (-not $DryRun) {
        $bodyBytes = $utf8NoBom.GetBytes($newContent)
        if ($hasBom) {
            $outBytes = New-Object byte[] ($bodyBytes.Length + 3)
            $outBytes[0] = 0xEF; $outBytes[1] = 0xBB; $outBytes[2] = 0xBF
            [System.Buffer]::BlockCopy($bodyBytes, 0, $outBytes, 3, $bodyBytes.Length)
            [System.IO.File]::WriteAllBytes($fullPath, $outBytes)
        } else {
            [System.IO.File]::WriteAllBytes($fullPath, $bodyBytes)
        }
    }
    $stampedCount++
}

if ($VerifyOnly) {
    if ($missingInVerify.Count -gt 0) {
        $sample = ($missingInVerify | Select-Object -First 15) -join "; "
        throw "[HEADER-STAMP] VERIFY FAILED: $($missingInVerify.Count) file(s) missing SPDX headers. Sample: $sample. Fix: Run .\scripts\release\Apply-CleanroomFileHeaders.ps1"
    }
    Write-Host "[HEADER-STAMP] VERIFY PASSED: All eligible cleanroom files carry SPDX headers (alreadyStamped=$alreadyStampedCount, skippedConcurrentWip=$skippedWipCount, skippedExcluded=$skippedOtherCount)." -ForegroundColor Green
    exit 0
}

$modeLabel = if ($DryRun) { "DRY-RUN" } else { "APPLIED" }
Write-Host "[HEADER-STAMP] ${modeLabel}: stamped=$stampedCount | alreadyStamped=$alreadyStampedCount | skippedConcurrentWip=$skippedWipCount | skippedExcluded=$skippedOtherCount" -ForegroundColor Green
