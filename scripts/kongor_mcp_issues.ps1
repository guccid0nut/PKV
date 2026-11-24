# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Project Kongor Searchable MCP Issue Knowledge Base CLI (search, rebuild-index, check, new, bump).

.DESCRIPTION
    Manages curated MCP runtime issue & prevention documents under docs/reference/mcp-issues/.
    - search        : Sub-50ms search across index.json and issue bodies by -Query, -Server, -Tool, -Status.
    - rebuild-index : Parses YAML frontmatter across MCP-*.md and writes deterministic index.json + README.md + docs/MCP_ISSUES.md stub.
    - check         : Validates frontmatter schema, runs shared secret scrubbing, and enforces zero index.json drift.
    - new           : Checks for existing issues on the same server/tool (anti-duplicate guard), scaffolds MCP-NNN-*.md from TEMPLATE.md, and rebuilds index.json.
    - bump          : Increments occurrences and updates last_seen date on an existing MCP-NNN issue file, then rebuilds index.json.

.EXAMPLE
    .\scripts\kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua
    .\scripts\kongor_mcp_issues.ps1 -Action search -Query "willowmaker-ipc"
    .\scripts\kongor_mcp_issues.ps1 -Action bump -Id MCP-213
    .\scripts\kongor_mcp_issues.ps1 -Action new -Server ghidra -Tool decompile_function -Category decompiler-timeout -Title "Timeout on CRT init" -PreventionRule "Use disassemble instead of decompile_function on CRT init blocks."
    .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index
    .\scripts\kongor_mcp_issues.ps1 -Action check
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("search", "rebuild-index", "check", "new", "bump", "cluster", "audit-unverified", "stats", "ingest-outcomes", "semantic-dedup")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$Query = "",

    [Parameter(Mandatory = $false)]
    [string]$Server = "",

    [Parameter(Mandatory = $false)]
    [string]$Tool = "",

    [Parameter(Mandatory = $false)]
    [string]$Status = "",

    [Parameter(Mandatory = $false)]
    [string]$Id = "",

    [Parameter(Mandatory = $false)]
    [string]$IssuesDir = "",

    [Parameter(Mandatory = $false)]
    [string]$Title = "",

    [Parameter(Mandatory = $false)]
    [string]$Category = "runtime-error",

    [Parameter(Mandatory = $false)]
    [string]$PreventionRule = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [Parameter(Mandatory = $false)]
    [string]$OutcomeLogPath = "",

    [Parameter(Mandatory = $false)]
    [int]$MaxUncodifiedHighFrequencyHits = 3,

    [Parameter(Mandatory = $false)]
    [double]$Threshold = 0.55,

    [Parameter(Mandatory = $false)]
    [int]$TopK = 25,

    [Parameter(Mandatory = $false)]
    [string]$PgvectorDsn = "",

    [Parameter(Mandatory = $false)]
    [string]$EmitSql = "",

    [switch]$IncludeSuperseded,
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($IssuesDir)) {
    $IssuesDir = Join-Path $RepoRoot "docs\reference\mcp-issues"
}

$secretScrubHelper = Join-Path $PSScriptRoot "lib\Test-KongorSecretScrub.ps1"
if (Test-Path -LiteralPath $secretScrubHelper) {
    . $secretScrubHelper
}

function Write-McpKbLog {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Cyan
    )
    if (-not $Quiet) {
        $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
        Write-Host "[$ts] [KONGOR-MCP-KB] $Message" -ForegroundColor $Color
    }
}

function Parse-InlineYamlArray {
    param([string]$RawValue)
    $trimmed = $RawValue.Trim()
    if ($trimmed -match '^\[(.*)\]$') {
        $inner = $Matches[1].Trim()
        if ([string]::IsNullOrWhiteSpace($inner)) {
            return @()
        }
        $items = New-Object System.Collections.Generic.List[string]
        foreach ($part in ($inner -split ',')) {
            $clean = $part.Trim().Trim('"', "'")
            if (-not [string]::IsNullOrWhiteSpace($clean)) {
                $items.Add($clean)
            }
        }
        return $items.ToArray()
    }
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return @()
    }
    return @($trimmed.Trim('"', "'"))
}

# satisfies: #40 (canonical-path-no-reparse)
function Test-CanonicalIssuePath {
    param(
        [string]$CandidatePath,
        [string]$AllowedRoot
    )
    if ([string]::IsNullOrWhiteSpace($CandidatePath) -or -not (Test-Path -LiteralPath $CandidatePath)) {
        return [PSCustomObject]@{ Valid = $false; Reason = "Path does not exist: $CandidatePath" }
    }
    $item = Get-Item -LiteralPath $CandidatePath -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return [PSCustomObject]@{ Valid = $false; Reason = "Security violation (#40): ReparsePoint/Symlink rejected for '$CandidatePath'" }
    }
    $fullCandidate = [System.IO.Path]::GetFullPath($item.FullName)
    $fullRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
    if (-not $fullCandidate.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return [PSCustomObject]@{ Valid = $false; Reason = "Security violation (#40): Path '$fullCandidate' escapes allowed root '$fullRoot'" }
    }
    return [PSCustomObject]@{ Valid = $true; Reason = "" }
}

function Parse-McpIssueFrontmatter {
    param(
        [string]$FilePath,
        [string]$RawContent = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($FilePath) -and (Test-Path -LiteralPath $FilePath)) {
        $canonCheck = Test-CanonicalIssuePath -CandidatePath $FilePath -AllowedRoot $IssuesDir
        if (-not $canonCheck.Valid) {
            return [PSCustomObject]@{
                Valid  = $false
                Reason = $canonCheck.Reason
                Meta   = $null
                Body   = ""
            }
        }
    }

    $raw = if (-not [string]::IsNullOrEmpty($RawContent)) {
        $RawContent
    } else {
        [System.IO.File]::ReadAllText($FilePath)
    }

    $normalized = $raw -replace "`r`n", "`n"
    if ($normalized -notmatch '(?s)^---\n(.*?)\n---\n(.*)$') {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "Missing YAML frontmatter delimiter block (--- ... ---)"
            Meta   = $null
            Body   = $raw
        }
    }

    $yamlBlock = $Matches[1]
    $body = $Matches[2]
    $meta = [ordered]@{}
    $seenKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $hasStatusComment = $false

    foreach ($line in ($yamlBlock -split "`n")) {
        if ($line -match '^\s*status\s*:.*#') {
            $hasStatusComment = $true
        }
        $trimmedLine = $line.Trim()
        if ($trimmedLine.StartsWith("#")) { continue }
        $cleanLine = ($trimmedLine -replace '^([a-zA-Z0-9_]+\s*:\s*"[^"]*")\s+#.*$', '$1' -replace '^([a-zA-Z0-9_]+\s*:\s*[^"\r\n]+?)\s+#.*$', '$1').Trim()
        if ([string]::IsNullOrWhiteSpace($cleanLine)) { continue }
        if ($cleanLine -match '^([a-zA-Z0-9_]+)\s*:\s*(.*)$') {
            $key = $Matches[1].Trim()
            $val = $Matches[2].Trim()
            # satisfies: #40 (canonical-path-no-reparse — duplicate YAML frontmatter key rejection)
            if (-not $seenKeys.Add($key)) {
                return [PSCustomObject]@{
                    Valid            = $false
                    Reason           = "Duplicate YAML frontmatter key '$key' detected (#40 anti-shadowing guard)"
                    Meta             = $meta
                    Body             = $body
                    HasStatusComment = $hasStatusComment
                }
            }
            if ($key -in @("tags", "supersedes")) {
                $meta[$key] = @(Parse-InlineYamlArray -RawValue $val)
            } elseif ($key -eq "occurrences") {
                $parsedInt = 1
                if ([int]::TryParse($val.Trim('"', "'"), [ref]$parsedInt)) {
                    $meta[$key] = $parsedInt
                } else {
                    $meta[$key] = 1
                }
            } elseif ($key -eq "status") {
                $meta[$key] = ($val -replace '#.*$', '').Trim('"', "'").Trim()
            } else {
                $meta[$key] = $val.Trim('"', "'")
            }
        }
    }

    $requiredKeys = @(
        "id", "server", "tool", "category", "title", "status",
        "superseded_by", "supersedes", "occurrences", "date",
        "last_seen", "tags", "prevention_rule"
    )
    $missingKeys = New-Object System.Collections.Generic.List[string]
    foreach ($rk in $requiredKeys) {
        if (-not $meta.Contains($rk)) {
            $missingKeys.Add($rk)
        }
    }

    if ($missingKeys.Count -gt 0) {
        return [PSCustomObject]@{
            Valid            = $false
            Reason           = "Missing required frontmatter key(s): $($missingKeys -join ', ')"
            Meta             = $meta
            Body             = $body
            HasStatusComment = $hasStatusComment
        }
    }

    $validStatuses = @("open", "investigating", "patched", "resolved", "prevented", "documented", "superseded", "known_limitation", "codified")
    if ($meta["status"] -notin $validStatuses) {
        return [PSCustomObject]@{
            Valid            = $false
            Reason           = "Invalid status '$($meta["status"])' (must be one of: $($validStatuses -join ', '))"
            Meta             = $meta
            Body             = $body
            HasStatusComment = $hasStatusComment
        }
    }

    $codifiedInVal = if ($meta.Contains("codified_in")) { [string]$meta["codified_in"] } else { "" }
    $verifiedByVal = if ($meta.Contains("verified_by")) { [string]$meta["verified_by"] } else { "" }

    $entry = [ordered]@{
        id              = [string]$meta["id"]
        file            = [System.IO.Path]::GetFileName($FilePath)
        server          = [string]$meta["server"]
        tool            = [string]$meta["tool"]
        category        = [string]$meta["category"]
        title           = [string]$meta["title"]
        status          = [string]$meta["status"]
        codified_in     = $codifiedInVal
        verified_by     = $verifiedByVal
        superseded_by   = [string]$meta["superseded_by"]
        supersedes      = @($meta["supersedes"])
        occurrences     = [int]$meta["occurrences"]
        date            = [string]$meta["date"]
        last_seen       = [string]$meta["last_seen"]
        tags            = @($meta["tags"])
        prevention_rule = [string]$meta["prevention_rule"]
    }

    return [PSCustomObject]@{
        Valid            = $true
        Reason           = ""
        Meta             = $entry
        Body             = $body
        HasStatusComment = $hasStatusComment
    }
}

function Get-AllMcpIssueFiles {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $Dir -Filter "MCP-*.md" -File | Sort-Object Name)
}

function Build-DeterministicIndexJson {
    param([array]$Entries)
    return (ConvertTo-Json -InputObject @($Entries) -Depth 6) -replace "`r`n", "`n"
}

switch ($Action) {
    "rebuild-index" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if (-not (Test-Path -LiteralPath $IssuesDir)) {
            Write-Error "MCP issues directory not found: '$IssuesDir'."
            exit 1
        }

        $issueFiles = Get-AllMcpIssueFiles -Dir $IssuesDir
        $entries = New-Object System.Collections.Generic.List[object]
        foreach ($nf in $issueFiles) {
            $raw = [System.IO.File]::ReadAllText($nf.FullName)
            $parsed = Parse-McpIssueFrontmatter -FilePath $nf.FullName -RawContent $raw
            if (-not $parsed.Valid) {
                Write-Error "[MCP-KB SCHEMA ERROR] $($nf.Name): $($parsed.Reason). Fix frontmatter and re-run .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index"
                exit 1
            }
            $entries.Add($parsed.Meta)
        }

        $indexPath = Join-Path $IssuesDir "index.json"
        $jsonContent = Build-DeterministicIndexJson -Entries $entries.ToArray()
        [System.IO.File]::WriteAllText($indexPath, $jsonContent + "`n", (New-Object System.Text.UTF8Encoding($false)))

        $totalCount = $entries.Count
        $activeEntries = @($entries | Where-Object { $_["status"] -ne "superseded" })
        $supersededCount = $totalCount - $activeEntries.Count
        $patchedCount = @($activeEntries | Where-Object { $_["status"] -eq "patched" }).Count
        $resolvedCount = @($activeEntries | Where-Object { $_["status"] -in @("resolved", "documented", "known_limitation") }).Count
        $preventedCount = @($activeEntries | Where-Object { $_["status"] -eq "prevented" }).Count
        $openCount = @($activeEntries | Where-Object { $_["status"] -in @("open", "investigating") }).Count

        $readmePath = Join-Path $IssuesDir "README.md"
        $mdLines = New-Object System.Collections.Generic.List[string]
        $mdLines.Add("# Project Kongor MCP Issue & Prevention Knowledge Base")
        $mdLines.Add("")
        $mdLines.Add('> Auto-generated by `scripts/kongor_mcp_issues.ps1 -Action rebuild-index`. Do not hand-edit without running `rebuild-index`.')
        $mdLines.Add("")
        $mdLines.Add("## Summary Status")
        $mdLines.Add("")
        $mdLines.Add("- **Total Tracked Issues:** $totalCount ($($activeEntries.Count) canonical active, $supersededCount superseded repeats)")
        $mdLines.Add("- **Open / Investigating:** $openCount")
        $mdLines.Add("- **Patched (Code-Level Hardened):** $patchedCount")
        $mdLines.Add("- **Resolved / Documented (Workflow Enforced):** $resolvedCount")
        $mdLines.Add("- **Prevented (Architectural Route):** $preventedCount")
        $mdLines.Add("")
        $mdLines.Add("## Quick CLI Commands")
        $mdLines.Add("")
        $mdLines.Add('```powershell')
        $mdLines.Add('# 1. Ad-hoc ledger lookup (manual pre-flight search is retired; C# McpToolRegistry auto-enriches calls in 0 ms):')
        $mdLines.Add('.\scripts\kongor_mcp_issues.ps1 -Action search -Server kongor -Tool kongor_client_eval_lua')
        $mdLines.Add('')
        $mdLines.Add('# 2. Search by keyword/error snippet:')
        $mdLines.Add('.\scripts\kongor_mcp_issues.ps1 -Action search -Query "willowmaker-ipc"')
        $mdLines.Add('')
        $mdLines.Add('# 3. Bump an existing issue when encountered again:')
        $mdLines.Add('.\scripts\kongor_mcp_issues.ps1 -Action bump -Id MCP-213')
        $mdLines.Add('')
        $mdLines.Add('# 4. Scaffold a brand-new MCP issue:')
        $mdLines.Add('.\scripts\kongor_mcp_issues.ps1 -Action new -Server <server> -Tool <tool> -Category <cat> -Title "<Title>" -PreventionRule "<Rule>"')
        $mdLines.Add('```')
        $mdLines.Add("")
        $mdLines.Add("## Canonical Active MCP Issues ($($activeEntries.Count))")
        $mdLines.Add("")
        $mdLines.Add("| ID | Server / Tool | Category | Status | Occurrences | Last Seen | Primary Prevention Rule |")
        $mdLines.Add("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |")
        foreach ($e in $activeEntries) {
            $safeRule = ([string]$e["prevention_rule"]) -replace '\|', '\|'
            $mdLines.Add(('| [`{0}`](./{1}) | `{2}` / `{3}` | `{4}` | `{5}` | {6} | {7} | {8} |' -f $e["id"], $e["file"], $e["server"], $e["tool"], $e["category"], $e["status"], $e["occurrences"], $e["last_seen"], $safeRule))
        }
        [System.IO.File]::WriteAllText($readmePath, ($mdLines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

        $sw.Stop()
        Write-McpKbLog "Rebuilt index.json and README.md for $totalCount MCP issue(s) ($($activeEntries.Count) active, $supersededCount superseded) in $($sw.ElapsedMilliseconds)ms." Green
        exit 0
    }

    "check" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $errors = New-Object System.Collections.Generic.List[string]
        $indexDrift = $false
        $secretLeaks = 0
        $issueFiles = Get-AllMcpIssueFiles -Dir $IssuesDir
        $entries = New-Object System.Collections.Generic.List[object]

        if ($issueFiles.Count -eq 0) {
            $errors.Add("Zero MCP-*.md issue files found in '$IssuesDir'.")
        }

        $isCleanroomRepo = (-not (Test-Path -LiteralPath (Join-Path $RepoRoot "games"))) -and (-not (Test-Path -LiteralPath (Join-Path $RepoRoot "src\tools\ghidra")))
        function Test-CodifiedInPathExists {
            param([string]$CodInPath)
            $codFull = Join-Path $RepoRoot $CodInPath
            if (Test-Path -LiteralPath $codFull) { return $true }
            return ($isCleanroomRepo -and ($CodInPath -match '^(?:src/tools/(?:ghidra|voicebox|goose)/|scripts/(?:vfs|vault|staging)/)'))
        }

        foreach ($nf in $issueFiles) {
            $raw = [System.IO.File]::ReadAllText($nf.FullName)
            $parsed = Parse-McpIssueFrontmatter -FilePath $nf.FullName -RawContent $raw
            if (-not $parsed.Valid) {
                $errors.Add("$($nf.Name): $($parsed.Reason)")
            } else {
                if ($parsed.HasStatusComment) {
                    $errors.Add("$($nf.Name): Literal '#' comment in 'status:' frontmatter is forbidden (remove inline template comment)")
                }
                if ($parsed.Meta["status"] -eq "codified") {
                    $codIn = [string]$parsed.Meta["codified_in"]
                    if ([string]::IsNullOrWhiteSpace($codIn)) {
                        $errors.Add("$($nf.Name): Status 'codified' requires a non-empty 'codified_in' frontmatter field")
                    } elseif (-not (Test-CodifiedInPathExists -CodInPath $codIn)) {
                        $errors.Add("$($nf.Name): codified_in file not found on disk: '$codIn'")
                    }
                }
                if ($parsed.Meta["status"] -in @("patched", "resolved") -and [string]::IsNullOrWhiteSpace($parsed.Meta["verified_by"])) {
                    $errors.Add("$($nf.Name): Status '$($parsed.Meta["status"])' requires a non-empty 'verified_by' frontmatter field")
                } elseif ($parsed.Meta["status"] -in @("patched", "resolved") -and ([string]$parsed.Meta["category"] -match '^(ipc-|window-resolution|schema-mismatch)')) {
                    $idNum = 0
                    if ([string]$parsed.Meta["id"] -match '^MCP-(\d+)$') {
                        $idNum = [int]$Matches[1]
                    }
                    $lastSeenStr = [string]$parsed.Meta["last_seen"]
                    if ($idNum -ge 240 -or $lastSeenStr -ge "2026-10-23") {
                        $vb = [string]$parsed.Meta["verified_by"]
                        $hasProbeArtifact = ($vb -match '(?i)(logs[\\/][^\s,;)]+|\.(json|log|jsonl)\b)')
                        $hasCommitSha = ($vb -match '\b[0-9a-f]{7,40}\b')
                        if (-not ($hasProbeArtifact -and $hasCommitSha)) {
                            $errors.Add("$($nf.Name): Transport/schema category '$($parsed.Meta["category"])' with status '$($parsed.Meta["status"])' requires both a live probe artifact path (e.g. logs/...json) and a commit SHA in 'verified_by'")
                        }
                    }
                }
                if ($parsed.Meta["status"] -in @("open", "investigating") -and $raw -match '\(category:\s*`[^`]+`,\s*status:\s*`(resolved)`') {
                    $bodyStatus = $Matches[1]
                    $errors.Add("$($nf.Name): Summary body status '$bodyStatus' contradicts frontmatter status '$($parsed.Meta["status"])'")
                }
                $entries.Add($parsed.Meta)
            }

            if (Get-Command Test-KongorSecretScrub -ErrorAction SilentlyContinue) {
                $scrub = Test-KongorSecretScrub -FilePath $nf.FullName -Content $raw
                if (-not $scrub.Passed) {
                    $secretLeaks += $scrub.Findings.Count
                    $errors.Add("$($nf.Name): Secret leak detected ('$($scrub.Findings[0].Match)')")
                }
            }
        }

        $indexPath = Join-Path $IssuesDir "index.json"
        if (-not (Test-Path -LiteralPath $indexPath)) {
            $indexDrift = $true
            $errors.Add("Missing index.json at '$indexPath'. Run: .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index")
        } elseif ($entries.Count -gt 0) {
            $expectedJson = (Build-DeterministicIndexJson -Entries $entries.ToArray()).Trim()
            $actualJson = ([System.IO.File]::ReadAllText($indexPath) -replace "`r`n", "`n").Trim()
            if ($expectedJson -cne $actualJson) {
                $indexDrift = $true
                $errors.Add("index.json is out of sync with MCP-*.md frontmatter. Run: .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index")
            }
        }

        $docsDir = Split-Path (Split-Path $IssuesDir -Parent) -Parent
        if ($docsDir) {
            $stubMdPath = Join-Path $docsDir "MCP_ISSUES.md"
            if (Test-Path -LiteralPath $stubMdPath) {
                $stubContent = [System.IO.File]::ReadAllText($stubMdPath)
                if ($stubContent -match '(?m)^###\s+\[?MCP-\d+\]?') {
                    $errors.Add("Legacy monolith regression in docs/MCP_ISSUES.md: found inline '### [MCP-NNN]' section. Record issues via .\scripts\kongor_mcp_issues.ps1 -Action bump or -Action new instead.")
                }
            }
        }

        # satisfies: #113 (kb-decay-and-mcp-promotion — occurrences >= 3 promotion candidates)
        $promotionCandidates = New-Object System.Collections.Generic.List[string]
        foreach ($e in $entries) {
            $isPromoted = ($e["status"] -in @("superseded", "patched", "prevented", "resolved", "codified")) -or (-not [string]::IsNullOrWhiteSpace([string]$e["codified_in"]))
            $codIn = [string]$e["codified_in"]
            if (-not [string]::IsNullOrWhiteSpace($codIn)) {
                if (-not (Test-CodifiedInPathExists -CodInPath $codIn)) {
                    $errors.Add("$($e['id']): codified_in file not found on disk: '$codIn'")
                    $isPromoted = $false
                }
            }
            if (-not $isPromoted -and [int]$e["occurrences"] -ge 3) {
                $promotionCandidates.Add("$($e['id']) ($($e['server'])/$($e['tool']), occurrences=$($e['occurrences']))")
            }
        }

        if ($MaxUncodifiedHighFrequencyHits -gt 0) {
            foreach ($e in $entries) {
                if ($e["status"] -ne "superseded" -and [int]$e["occurrences"] -ge $MaxUncodifiedHighFrequencyHits) {
                    if ($e["status"] -notin @("patched", "prevented", "resolved")) {
                        $codIn = [string]$e["codified_in"]
                        if ([string]::IsNullOrWhiteSpace($codIn)) {
                            $errors.Add("$($e['id']): High-frequency issue (occurrences=$($e['occurrences']) >= $MaxUncodifiedHighFrequencyHits) has status: $($e['status']) without codified_in. Codify into client remediation guardrails or resolve.")
                        } elseif (-not (Test-CodifiedInPathExists -CodInPath $codIn)) {
                            $errors.Add("$($e['id']): codified_in file not found on disk: '$codIn'")
                        }
                    }
                }
            }
        }

        $sw.Stop()
        $checkSummary = [ordered]@{
            passed              = ($errors.Count -eq 0 -and -not $indexDrift)
            issuesChecked       = $issueFiles.Count
            indexDrift          = $indexDrift
            secretLeaks         = $secretLeaks
            promotionCandidates = @($promotionCandidates.ToArray())
            errors              = $errors.ToArray()
            durationMs          = $sw.ElapsedMilliseconds
        }

        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            $outJson = $checkSummary | ConvertTo-Json -Depth 5
            [System.IO.File]::WriteAllText($JsonOutput, $outJson, (New-Object System.Text.UTF8Encoding($false)))
        }

        if (-not $checkSummary.passed) {
            foreach ($err in $errors) {
                Write-Host "  [MCP-KB FAIL] $err" -ForegroundColor Red
            }
            Write-Host " [FAIL] MCP Issue KB check failed ($($errors.Count) issue(s), indexDrift=$indexDrift). Run: .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index" -ForegroundColor Red
            if ($indexDrift) { exit 3 }
            exit 1
        }

        if ($promotionCandidates.Count -gt 0 -and -not $Quiet) {
            Write-McpKbLog "[MCP-PROMOTE] $($promotionCandidates.Count) unpatched MCP issue(s) have occurrences >= 3 (promote to automated gate or code patch): $($promotionCandidates -join '; ')" Yellow
        }

        Write-McpKbLog "PASS: Checked $($issueFiles.Count) MCP issue(s) (0 schema errors, 0 index drift, 0 secret leaks) in $($sw.ElapsedMilliseconds)ms." Green
        exit 0
    }

    "search" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if ([string]::IsNullOrWhiteSpace($Query) -and [string]::IsNullOrWhiteSpace($Server) -and [string]::IsNullOrWhiteSpace($Tool) -and [string]::IsNullOrWhiteSpace($Status)) {
            Write-Error "Provide at least one filter (-Query, -Server, -Tool, or -Status) for -Action search."
            exit 1
        }

        $indexPath = Join-Path $IssuesDir "index.json"
        if (-not (Test-Path -LiteralPath $indexPath)) {
            Write-Error "index.json not found at '$indexPath'. Run: .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index"
            exit 1
        }

        $parsedJson = [System.IO.File]::ReadAllText($indexPath) | ConvertFrom-Json
        $indexList = if ($parsedJson -is [System.Array]) { $parsedJson } else { @($parsedJson) }
        $matchedIssues = New-Object System.Collections.Generic.List[object]

        # satisfies: #56 (exact-id-lookup-shortcircuit)
        $trimmedQuery = if (-not [string]::IsNullOrWhiteSpace($Query)) { $Query.Trim() } else { "" }
        $isExactId = ($trimmedQuery -match '^MCP-\d+$')

        if ($isExactId) {
            foreach ($entry in $indexList) {
                if (-not $IncludeSuperseded -and $entry.status -eq "superseded") {
                    continue
                }
                if (([string]$entry.id).Equals($trimmedQuery, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $matchedIssues.Add([ordered]@{
                        id              = $entry.id
                        server          = $entry.server
                        tool            = $entry.tool
                        category        = $entry.category
                        status          = $entry.status
                        occurrences     = $entry.occurrences
                        last_seen       = $entry.last_seen
                        title           = $entry.title
                        prevention_rule = $entry.prevention_rule
                        file            = $entry.file
                        matchedIn       = "exact-id"
                    })
                }
            }
        }

        if ($matchedIssues.Count -eq 0) {
            foreach ($entry in $indexList) {
                if (-not $IncludeSuperseded -and $entry.status -eq "superseded") {
                    continue
                }
                if (-not [string]::IsNullOrWhiteSpace($Server) -and $entry.server -notmatch [regex]::Escape($Server)) {
                    continue
                }
                if (-not [string]::IsNullOrWhiteSpace($Tool) -and $entry.tool -notmatch [regex]::Escape($Tool)) {
                    continue
                }
                if (-not [string]::IsNullOrWhiteSpace($Status) -and $entry.status -ne $Status.ToLowerInvariant()) {
                    continue
                }

                $hitSource = "filter"
                if (-not [string]::IsNullOrWhiteSpace($Query)) {
                    $hitSource = ""
                    $notePath = Join-Path $IssuesDir $entry.file
                    $metaString = "$($entry.id) $($entry.server) $($entry.tool) $($entry.category) $($entry.title) $($entry.prevention_rule) $($entry.tags -join ' ')"

                    if ($metaString -match [regex]::Escape($Query)) {
                        $hitSource = "frontmatter"
                    } elseif (Test-Path -LiteralPath $notePath) {
                        $bodyHit = Select-String -LiteralPath $notePath -Pattern $Query -SimpleMatch -ErrorAction SilentlyContinue
                        if ($null -ne $bodyHit) {
                            $hitSource = "body (line $($bodyHit[0].LineNumber))"
                        }
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($hitSource)) {
                    $matchedIssues.Add([ordered]@{
                        id              = $entry.id
                        server          = $entry.server
                        tool            = $entry.tool
                        category        = $entry.category
                        status          = $entry.status
                        occurrences     = $entry.occurrences
                        last_seen       = $entry.last_seen
                        title           = $entry.title
                        prevention_rule = $entry.prevention_rule
                        file            = $entry.file
                        matchedIn       = $hitSource
                    })
                }
            }
        }

        $sw.Stop()
        Write-McpKbLog "Search matched $($matchedIssues.Count) MCP issue(s) in $($sw.ElapsedMilliseconds)ms." Green
        foreach ($m in $matchedIssues) {
            Write-Host ("  [{0}] [{1}] ({2}/{3}) (seen {4}x, last {5}) {6}" -f $m.id, $m.status.ToUpperInvariant(), $m.server, $m.tool, $m.occurrences, $m.last_seen, $m.title) -ForegroundColor White
            Write-Host ("      Prevention : {0}" -f $m.prevention_rule) -ForegroundColor Yellow
            Write-Host ("      Document   : docs/reference/mcp-issues/{0} ({1})" -f $m.file, $m.matchedIn) -ForegroundColor Gray
        }

        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, (ConvertTo-Json -InputObject @($matchedIssues.ToArray()) -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        }
        exit 0
    }

    "bump" {
        if ([string]::IsNullOrWhiteSpace($Id)) {
            Write-Error "Must provide -Id 'MCP-NNN' for -Action bump."
            exit 1
        }
        $normalizedId = $Id.Trim().ToUpperInvariant()
        $targetFiles = @(Get-ChildItem -LiteralPath $IssuesDir -Filter "$normalizedId-*.md" -File -ErrorAction SilentlyContinue)
        if ($targetFiles.Count -eq 0) {
            Write-Error "No MCP issue file matching '$normalizedId-*.md' found in '$IssuesDir'. Run: .\scripts\kongor_mcp_issues.ps1 -Action search -Query '$normalizedId'"
            exit 1
        }

        $targetFile = $targetFiles[0]
        $raw = [System.IO.File]::ReadAllText($targetFile.FullName)
        $parsed = Parse-McpIssueFrontmatter -FilePath $targetFile.FullName -RawContent $raw
        if (-not $parsed.Valid) {
            Write-Error "Cannot bump '$($targetFile.Name)' due to invalid frontmatter: $($parsed.Reason)"
            exit 1
        }

        if ($parsed.Meta["status"] -eq "superseded" -and -not [string]::IsNullOrWhiteSpace($parsed.Meta["superseded_by"])) {
            $canonId = $parsed.Meta["superseded_by"]
            Write-McpKbLog "Issue $normalizedId is superseded by $canonId; redirecting bump to $canonId..." Yellow
            & $PSCommandPath -Action bump -Id $canonId -IssuesDir $IssuesDir -Quiet:$Quiet
            exit $LASTEXITCODE
        }

        $newCount = ([int]$parsed.Meta["occurrences"]) + 1
        $today = (Get-Date).ToString("yyyy-MM-dd")
        $updated = $raw -replace '(?m)^occurrences:\s*\d+', "occurrences: $newCount"
        $updated = $updated -replace '(?m)^last_seen:\s*\S+', "last_seen: $today"
        if ($parsed.Meta["status"] -in @("patched", "resolved")) {
            $prevStatus = $parsed.Meta["status"]
            $updated = $updated -replace '(?m)^status:\s*[^\r\n]+', "status: investigating"
            $updated = $updated -replace '(?m)^verified_by:\s*[^\r\n]+', 'verified_by: ""'
            $parsed.Meta["status"] = "investigating"
            $parsed.Meta["verified_by"] = ""
            Write-McpKbLog "Auto-reopened $normalizedId ($prevStatus -> investigating) on recurrence bump." Yellow
        }
        $updated = $updated -replace '(\(category:\s*`[^`]+`,\s*status:\s*`)[^`]+(`,\s*occurrences:\s*)\d+\)', "`${1}$($parsed.Meta["status"])`${2}$newCount)"
        [System.IO.File]::WriteAllText($targetFile.FullName, $updated, (New-Object System.Text.UTF8Encoding($false)))

        # satisfies: #113 (kb-decay-and-mcp-promotion — occurrences >= 3 promotion alert)
        if ($newCount -ge 3 -and $parsed.Meta["status"] -notin @("patched", "prevented")) {
            Write-Host "  [MCP-PROMOTE] Issue $normalizedId ($($parsed.Meta['server'])/$($parsed.Meta['tool'])) reached occurrences=$newCount! Promote to an automated gate or code-level fix (status: patched)." -ForegroundColor Yellow
        }

        Write-McpKbLog "Bumped $($targetFile.Name) -> occurrences: $newCount, last_seen: $today; rebuilding index.json..." Green
        & $PSCommandPath -Action rebuild-index -IssuesDir $IssuesDir -Quiet:$Quiet
        exit $LASTEXITCODE
    }

    "new" {
        if ([string]::IsNullOrWhiteSpace($Title) -or [string]::IsNullOrWhiteSpace($Server) -or [string]::IsNullOrWhiteSpace($Tool)) {
            Write-Error "Must provide -Server '<server>', -Tool '<tool>', and -Title '<title>' for -Action new."
            exit 1
        }

        $indexPath = Join-Path $IssuesDir "index.json"
        if ((-not $Force) -and (Test-Path -LiteralPath $indexPath)) {
            $parsedJson = [System.IO.File]::ReadAllText($indexPath) | ConvertFrom-Json
            $indexList = if ($parsedJson -is [System.Array]) { $parsedJson } else { @($parsedJson) }
            $existingMatches = @($indexList | Where-Object {
                $_.status -ne "superseded" -and
                $_.server -eq $Server -and
                $_.tool -eq $Tool
            })

            if ($existingMatches.Count -gt 0) {
                Write-Host " [ANTI-DUPLICATE GUARD] Found $($existingMatches.Count) existing active issue(s) for '$Server / $Tool':" -ForegroundColor Yellow
                foreach ($em in $existingMatches) {
                    Write-Host ("   - [{0}] ({1}) {2}" -f $em.id, $em.category, $em.title) -ForegroundColor Cyan
                    Write-Host ("     Prevention: {0}" -f $em.prevention_rule) -ForegroundColor Gray
                }
                Write-Host " If this failure matches an existing issue above, run:" -ForegroundColor Yellow
                Write-Host ("   .\scripts\kongor_mcp_issues.ps1 -Action bump -Id {0}" -f $existingMatches[0].id) -ForegroundColor Green
                Write-Host " If this is genuinely a brand-new root cause, re-run with -Force." -ForegroundColor Yellow
                exit 2
            }
        }

        $issueFiles = Get-AllMcpIssueFiles -Dir $IssuesDir
        $maxNum = 0
        foreach ($nf in $issueFiles) {
            if ($nf.Name -match '^MCP-(\d+)') {
                $n = [int]$Matches[1]
                if ($n -gt $maxNum) { $maxNum = $n }
            }
        }
        $nextNum = $maxNum + 1
        $newId = ("MCP-{0:D3}" -f $nextNum)
        $slug = ($Title.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
        if ($slug.Length -gt 60) {
            $slug = $slug.Substring(0, 60).Trim('-')
        }
        $newFileName = "$newId-$slug.md"
        $newFilePath = Join-Path $IssuesDir $newFileName

        $today = (Get-Date).ToString("yyyy-MM-dd")
        $isoNow = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
        $ruleText = if (-not [string]::IsNullOrWhiteSpace($PreventionRule)) { $PreventionRule } else { "Verify $Server / $Tool parameters and service readiness before invoking." }
        $safeTitle = $Title -replace '"', "'"
        $safeRule = $ruleText -replace '"', "'"

        $templatePath = Join-Path $IssuesDir "TEMPLATE.md"
        $content = [System.IO.File]::ReadAllText($templatePath)
        $content = $content -replace 'id:\s*MCP-000', "id: $newId"
        $content = $content -replace 'server:\s*kongor', "server: $Server"
        $content = $content -replace 'tool:\s*kongor_game_lua', "tool: $Tool"
        $content = $content -replace 'category:\s*runtime-error.*', "category: $Category"
        $content = $content -replace 'title:\s*"<Short descriptive title>"', "title: `"$safeTitle`""
        $content = $content -replace 'date:\s*\d{4}-\d{2}-\d{2}', "date: $today"
        $content = $content -replace 'last_seen:\s*\d{4}-\d{2}-\d{2}', "last_seen: $today"
        $content = $content -replace 'tags:\s*\[kongor,\s*ipc\]', "tags: [$Server, $Tool, $Category]"
        $content = $content -replace 'prevention_rule:\s*"<One-sentence actionable rule to succeed on the first attempt next time>"', "prevention_rule: `"$safeRule`""
        $content = $content -replace '# MCP-000: <Short Descriptive Title>', "# ${newId}: $Title"
        $content = $content -replace '\| \*\*MCP Server & Tool\*\* \| `kongor` / `kongor_game_lua` \|', "| **MCP Server & Tool** | ``$Server`` / ``$Tool`` |"
        $content = $content -replace '-Server kongor -Tool kongor_game_lua', "-Server $Server -Tool $Tool"
        $content = $content -replace 'Actionable rule to follow before invoking this MCP tool so this failure never recurs\.', $ruleText

        [System.IO.File]::WriteAllText($newFilePath, $content, (New-Object System.Text.UTF8Encoding($false)))
        Write-McpKbLog "Created MCP issue '$newFileName'; rebuilding index.json..." Green
        & $PSCommandPath -Action rebuild-index -IssuesDir $IssuesDir -Quiet:$Quiet
        exit $LASTEXITCODE
    }

    "cluster" {
        # satisfies: #60 (union-find-log-clustering)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $indexPath = Join-Path $IssuesDir "index.json"
        if (-not (Test-Path -LiteralPath $indexPath)) {
            Write-Error "index.json not found at '$indexPath'. Run: .\scripts\kongor_mcp_issues.ps1 -Action rebuild-index"
            exit 1
        }

        $parsedJson = [System.IO.File]::ReadAllText($indexPath) | ConvertFrom-Json
        $indexList = @(if ($parsedJson -is [System.Array]) { $parsedJson } else { @($parsedJson) })
        if (-not $IncludeSuperseded) {
            $indexList = @($indexList | Where-Object { $_.status -ne "superseded" })
        }

        $n = $indexList.Count
        $parent = New-Object 'int[]' $n
        for ($i = 0; $i -lt $n; $i++) { $parent[$i] = $i }

        function Find-McpClusterRoot([int]$idx) {
            $root = $idx
            while ($parent[$root] -ne $root) { $root = $parent[$root] }
            $curr = $idx
            while ($curr -ne $root) {
                $nxt = $parent[$curr]
                $parent[$curr] = $root
                $curr = $nxt
            }
            return $root
        }

        function Merge-McpCluster([int]$a, [int]$b) {
            $ra = Find-McpClusterRoot $a
            $rb = Find-McpClusterRoot $b
            if ($ra -ne $rb) {
                if ($ra -lt $rb) { $parent[$rb] = $ra } else { $parent[$ra] = $rb }
            }
        }

        $keyMap = @{}
        for ($i = 0; $i -lt $n; $i++) {
            $entry = $indexList[$i]
            $srv = ([string]$entry.server).ToLowerInvariant()
            $tl = ([string]$entry.tool).ToLowerInvariant()
            $cat = ([string]$entry.category).ToLowerInvariant()
            $primaryKey = "$srv|$tl|$cat"
            if ($keyMap.ContainsKey($primaryKey)) {
                Merge-McpCluster -a $keyMap[$primaryKey] -b $i
            } else {
                $keyMap[$primaryKey] = $i
            }
        }

        $clusterBuckets = @{}
        for ($i = 0; $i -lt $n; $i++) {
            $r = Find-McpClusterRoot $i
            if (-not $clusterBuckets.ContainsKey($r)) {
                $clusterBuckets[$r] = New-Object System.Collections.Generic.List[object]
            }
            $clusterBuckets[$r].Add($indexList[$i])
        }

        $clusters = New-Object System.Collections.Generic.List[object]
        $cIdx = 0
        foreach ($r in ($clusterBuckets.Keys | Sort-Object)) {
            $cIdx++
            $members = $clusterBuckets[$r]
            $rep = $members[0]
            $totalOccurrences = 0
            $memberIds = New-Object System.Collections.Generic.List[string]
            foreach ($m in $members) {
                $totalOccurrences += [int]$m.occurrences
                $memberIds.Add([string]$m.id)
            }
            $clusters.Add([ordered]@{
                clusterId        = ("MCP-CL-{0:D3}" -f $cIdx)
                server           = [string]$rep.server
                tool             = [string]$rep.tool
                category         = [string]$rep.category
                issueCount       = $members.Count
                totalOccurrences = $totalOccurrences
                memberIds        = $memberIds.ToArray()
                representativeTitle = [string]$rep.title
            })
        }

        $sw.Stop()
        Write-McpKbLog "[MCP-CLUSTER] Collapsed $n MCP issue(s) into $($clusters.Count) Union-Find cluster(s) in $($sw.ElapsedMilliseconds)ms." Green
        foreach ($cl in ($clusters | Sort-Object { $_.totalOccurrences } -Descending)) {
            Write-Host ("  [{0}] ({1}/{2} | {3}) issues={4} occurrences={5} ids=[{6}]" -f $cl.clusterId, $cl.server, $cl.tool, $cl.category, $cl.issueCount, $cl.totalOccurrences, ($cl.memberIds -join ", ")) -ForegroundColor Cyan
        }

        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, ($clusters | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        }
        exit 0
    }

    "audit-unverified" {
        $issueFiles = Get-AllMcpIssueFiles -Dir $IssuesDir
        $demotedPatched = 0
        $demotedResolved = 0
        $cleanedComments = 0
        foreach ($nf in $issueFiles) {
            $raw = [System.IO.File]::ReadAllText($nf.FullName)
            $parsed = Parse-McpIssueFrontmatter -FilePath $nf.FullName -RawContent $raw
            if (-not $parsed.Valid) { continue }
            $updated = $raw
            $changed = $false
            if ($parsed.HasStatusComment) {
                $updated = $updated -replace '(?m)^(status:\s*[^\r\n#]+?)\s*#[^\r\n]*', '$1'
                $cleanedComments++
                $changed = $true
            }
            if ([string]::IsNullOrWhiteSpace($parsed.Meta["verified_by"])) {
                if ($parsed.Meta["status"] -eq "patched") {
                    $updated = $updated -replace '(?m)^status:\s*[^\r\n]+', 'status: investigating'
                    $demotedPatched++
                    $changed = $true
                } elseif ($parsed.Meta["status"] -eq "resolved") {
                    $updated = $updated -replace '(?m)^status:\s*[^\r\n]+', 'status: documented'
                    $demotedResolved++
                    $changed = $true
                }
            }
            if ($changed) {
                [System.IO.File]::WriteAllText($nf.FullName, $updated, (New-Object System.Text.UTF8Encoding($false)))
            }
        }
        Write-McpKbLog "Audited $($issueFiles.Count) MCP issue(s): demoted $demotedPatched unverified 'patched' -> 'investigating', $demotedResolved unverified 'resolved' -> 'documented', cleaned $cleanedComments inline status comments." Green
        & $PSCommandPath -Action rebuild-index -IssuesDir $IssuesDir -Quiet:$Quiet
        exit $LASTEXITCODE
    }

    "stats" {
        $indexFile = Join-Path $IssuesDir "index.json"
        $kbEntries = @()
        if (Test-Path -LiteralPath $indexFile) {
            $parsedJson = [System.IO.File]::ReadAllText($indexFile) | ConvertFrom-Json
            if ($null -ne $parsedJson) {
                $kbEntries = @($parsedJson)
            }
        }

        $kbByTool = @{}
        foreach ($entry in $kbEntries) {
            if ($entry.status -eq "superseded" -or -not [string]::IsNullOrWhiteSpace([string]$entry.superseded_by)) {
                continue
            }
            $tName = [string]$entry.tool
            if ([string]::IsNullOrWhiteSpace($tName)) { continue }
            if (-not $kbByTool.ContainsKey($tName)) {
                $kbByTool[$tName] = New-Object System.Collections.Generic.List[object]
            }
            $kbByTool[$tName].Add($entry)
        }

        if ([string]::IsNullOrWhiteSpace($OutcomeLogPath)) {
            if (-not [string]::IsNullOrWhiteSpace($env:KONGOR_MCP_OUTCOME_LOG_PATH)) {
                $OutcomeLogPath = $env:KONGOR_MCP_OUTCOME_LOG_PATH
            } elseif (-not [string]::IsNullOrWhiteSpace($env:KONGOR_MCP_OUTCOME_LOG)) {
                $OutcomeLogPath = $env:KONGOR_MCP_OUTCOME_LOG
            } else {
                $OutcomeLogPath = Join-Path $RepoRoot "logs\mcp_tool_outcomes.jsonl"
            }
        }

        $toolMetrics = @{}
        $totalFirstCalls = 0
        $totalFirstSuccesses = 0
        $rawFirstCalls = 0
        $rawFirstSuccesses = 0
        $totalInvocations = 0
        $totalInvocationSuccesses = 0
        $totalCallerErrors = 0
        $totalToolFaults = 0
        $totalFalseGreens = 0
        $totalEscapeHatches = 0
        $totalUnrequestedMailboxFallbacks = 0

        if (Test-Path -LiteralPath $OutcomeLogPath) {
            $lines = [System.IO.File]::ReadAllLines($OutcomeLogPath)
            foreach ($line in $lines) {
                $trimmed = $line.Trim()
                if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
                try {
                    $rec = $trimmed | ConvertFrom-Json
                    $t = [string]$rec.tool
                    if ([string]::IsNullOrWhiteSpace($t)) { continue }
                    if (-not $toolMetrics.ContainsKey($t)) {
                        $toolMetrics[$t] = [PSCustomObject][ordered]@{
                            tool                        = $t
                            firstCalls                  = 0
                            firstSuccesses              = 0
                            firstFailures               = 0
                            totalCalls                  = 0
                            totalSuccesses              = 0
                            totalErrors                 = 0
                            callerErrors                = 0
                            toolFaults                  = 0
                            falseGreens                 = 0
                            escapeHatches               = 0
                            unrequestedMailboxFallbacks = 0
                        }
                    }
                    $m = $toolMetrics[$t]
                    if (($rec.falseGreen -eq $true) -or ($rec.verdict -eq "false_green")) {
                        $m.falseGreens++
                        $totalFalseGreens++
                        continue
                    }
                    $m.totalCalls++
                    $totalInvocations++
                    if ($rec.escapeHatchUsed -eq $true) {
                        $m.escapeHatches++
                        $totalEscapeHatches++
                    }
                    if (($rec.unrequestedMailboxFallback -eq $true) -or (($rec.channel -eq "mailbox") -and (-not [string]::IsNullOrWhiteSpace([string]$rec.fallbackReason)))) {
                        $m.unrequestedMailboxFallbacks++
                        $totalUnrequestedMailboxFallbacks++
                    }
                    $isSuccess = ($rec.succeeded -eq $true) -or ($rec.success -eq $true) -or ($rec.isError -eq $false)
                    $errClassProp = if ($null -ne $rec.PSObject.Properties["errorClass"]) { [string]$rec.errorClass } else { "" }
                    $recChannel = if ($null -ne $rec.PSObject.Properties["channel"]) { [string]$rec.channel } else { "" }
                    $recElapsed = if ($null -ne $rec.PSObject.Properties["elapsedMs"] -and $null -ne $rec.elapsedMs) { [double]$rec.elapsedMs } else { 9999.0 }
                    $isCallerError = (-not $isSuccess) -and (
                        ($errClassProp -eq "caller_lua_error") -or
                        ([string]::IsNullOrWhiteSpace($errClassProp) -and $t -in @("kongor_client_eval_lua", "kongor_game_lua") -and $recChannel -eq "named_pipe" -and $recElapsed -lt 200.0)
                    )
                    if ($isSuccess) {
                        $m.totalSuccesses++
                        $totalInvocationSuccesses++
                    } elseif ($isCallerError) {
                        $m.totalErrors++
                        $m.callerErrors++
                        $totalCallerErrors++
                    } else {
                        $m.totalErrors++
                        $m.toolFaults++
                        $totalToolFaults++
                    }
                    if (($rec.isFirstCallInSession -eq $true) -or ($rec.firstCall -eq $true)) {
                        $rawFirstCalls++
                        if ($isSuccess) {
                            $rawFirstSuccesses++
                        }
                        if (-not $isCallerError) {
                            $m.firstCalls++
                            $totalFirstCalls++
                            if ($isSuccess) {
                                $m.firstSuccesses++
                                $totalFirstSuccesses++
                            } else {
                                $m.firstFailures++
                            }
                        }
                    }
                } catch {
                    Write-Verbose "Skipping malformed JSONL outcome line: $_"
                }
            }
        }

        $effectiveToolCalls = [Math]::Max(0, $totalInvocations - $totalCallerErrors)
        $overallFirstCallRate = if ($totalFirstCalls -gt 0) { [Math]::Round(($totalFirstSuccesses * 100.0) / $totalFirstCalls, 1) } else { 100.0 }
        $rawFirstCallRate = if ($rawFirstCalls -gt 0) { [Math]::Round(($rawFirstSuccesses * 100.0) / $rawFirstCalls, 1) } else { 100.0 }
        $toolReliabilityRate = if ($effectiveToolCalls -gt 0) { [Math]::Round(($totalInvocationSuccesses * 100.0) / $effectiveToolCalls, 1) } else { 100.0 }
        $overallCallRate = if ($totalInvocations -gt 0) { [Math]::Round(($totalInvocationSuccesses * 100.0) / $totalInvocations, 1) } else { 100.0 }
        $overallFalseGreenRate = if ($totalInvocationSuccesses -gt 0) { [Math]::Round(($totalFalseGreens * 100.0) / $totalInvocationSuccesses, 2) } else { 0.0 }

        Write-McpKbLog "=== MCP FIRST-CALL SUCCESS & FALSE-GREEN SCOREBOARD ===" Cyan
        Write-Host ("  Outcome Log Path        : {0} (Exists: {1})" -f $OutcomeLogPath, (Test-Path -LiteralPath $OutcomeLogPath)) -ForegroundColor Gray
        Write-Host ("  First-Call Success Rate : {0}% ({1}/{2} non-caller-error first calls; raw: {3}%)" -f $overallFirstCallRate, $totalFirstSuccesses, $totalFirstCalls, $rawFirstCallRate) -ForegroundColor Green
        Write-Host ("  Tool Reliability Rate   : {0}% ({1}/{2} non-caller-error calls; {3} tool faults, {4} caller Lua errors)" -f $toolReliabilityRate, $totalInvocationSuccesses, $effectiveToolCalls, $totalToolFaults, $totalCallerErrors) -ForegroundColor Green
        Write-Host ("  Raw Overall Success Rate: {0}% ({1}/{2} total tool calls)" -f $overallCallRate, $totalInvocationSuccesses, $totalInvocations) -ForegroundColor Gray
        $fgColor = if ($totalFalseGreens -gt 0) { [ConsoleColor]::Red } else { [ConsoleColor]::Green }
        Write-Host ("  False-Green Rate        : {0}% ({1} false greens, {2} escape-hatch overrides)" -f $overallFalseGreenRate, $totalFalseGreens, $totalEscapeHatches) -ForegroundColor $fgColor
        $mbColor = if ($totalUnrequestedMailboxFallbacks -gt 0) { [ConsoleColor]::Yellow } else { [ConsoleColor]::Green }
        Write-Host ("  Mailbox Fallbacks       : {0} unrequested mailbox fallback(s)" -f $totalUnrequestedMailboxFallbacks) -ForegroundColor $mbColor

        $rows = New-Object System.Collections.Generic.List[object]
        $allToolNames = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($k in $toolMetrics.Keys) { [void]$allToolNames.Add([string]$k) }
        foreach ($k in $kbByTool.Keys) { [void]$allToolNames.Add([string]$k) }

        foreach ($tName in $allToolNames) {
            if (-not [string]::IsNullOrWhiteSpace($Tool) -and $tName -notlike "*$Tool*") { continue }
            $m = if ($toolMetrics.ContainsKey($tName)) { $toolMetrics[$tName] } else { $null }
            $fc = if ($m) { [int]$m.firstCalls } else { 0 }
            $fs = if ($m) { [int]$m.firstSuccesses } else { 0 }
            $tc = if ($m) { [int]$m.totalCalls } else { 0 }
            $ts = if ($m) { [int]$m.totalSuccesses } else { 0 }
            $te = if ($m) { [int]$m.totalErrors } else { 0 }
            $ce = if ($m) { [int]$m.callerErrors } else { 0 }
            $tf = if ($m) { [int]$m.toolFaults } else { 0 }
            $fg = if ($m) { [int]$m.falseGreens } else { 0 }
            $eh = if ($m) { [int]$m.escapeHatches } else { 0 }
            $umf = if ($m) { [int]$m.unrequestedMailboxFallbacks } else { 0 }
            $fcRate = if ($fc -gt 0) { [Math]::Round(($fs * 100.0) / $fc, 1) } else { -1.0 }
            $effCalls = [Math]::Max(0, $tc - $ce)
            $toolRelRate = if ($effCalls -gt 0) { [Math]::Round(($ts * 100.0) / $effCalls, 1) } else { -1.0 }
            $fgRate = if ($ts -gt 0) { [Math]::Round(($fg * 100.0) / $ts, 2) } else { 0.0 }

            $activeIds = New-Object System.Collections.Generic.List[string]
            $kbOcc = 0
            $unverifiedCount = 0
            if ($kbByTool.ContainsKey($tName)) {
                foreach ($iss in $kbByTool[$tName]) {
                    $activeIds.Add([string]$iss.id)
                    $kbOcc += [int]$iss.occurrences
                    if ($iss.status -in @("investigating", "documented")) {
                        $unverifiedCount++
                    }
                }
            }

            $rows.Add([PSCustomObject][ordered]@{
                tool                        = $tName
                firstCallSuccessRate        = $fcRate
                toolReliabilityRate         = $toolRelRate
                firstCalls                  = $fc
                firstSuccesses              = $fs
                totalCalls                  = $tc
                totalErrors                 = $te
                callerErrors                = $ce
                toolFaults                  = $tf
                falseGreens                 = $fg
                falseGreenRate              = $fgRate
                escapeHatches               = $eh
                unrequestedMailboxFallbacks = $umf
                kbOccurrences               = $kbOcc
                openIssuesCount             = $unverifiedCount
                mcpIds                      = ($activeIds -join ",")
            })
        }

        $sortedRows = $rows | Sort-Object `
            @{ Expression = { $_.falseGreens }; Descending = $true }, `
            @{ Expression = { if ($_.firstCallSuccessRate -ge 0) { $_.firstCallSuccessRate } else { 101.0 } }; Ascending = $true }, `
            @{ Expression = { $_.toolFaults }; Descending = $true }, `
            @{ Expression = { $_.openIssuesCount }; Descending = $true }, `
            @{ Expression = { $_.kbOccurrences }; Descending = $true }

        Write-Host ""
        Write-Host ("  {0,-32} | {1,10} | {2,14} | {3,8} | {4,8} | {5}" -f "Tool", "1st-Call %", "Calls(Flt/Usr)", "FalseGrn", "KB Occ", "Active MCP-NNN IDs") -ForegroundColor Yellow
        Write-Host ("  " + ("-" * 110)) -ForegroundColor DarkGray
        foreach ($r in ($sortedRows | Select-Object -First 20)) {
            $fcStr = if ($r.firstCallSuccessRate -ge 0) { "{0,9:F1}%" -f $r.firstCallSuccessRate } else { "      N/A " }
            $callStr = "{0,5}({1,2}/{2,2})" -f $r.totalCalls, $r.toolFaults, $r.callerErrors
            $idsStr = if ([string]::IsNullOrWhiteSpace($r.mcpIds)) { "-" } else { $r.mcpIds }
            $color = if ($r.falseGreens -gt 0) { [ConsoleColor]::Red } elseif (($r.firstCallSuccessRate -ge 0 -and $r.firstCallSuccessRate -lt 95.0) -or $r.openIssuesCount -gt 0) { [ConsoleColor]::Yellow } else { [ConsoleColor]::Gray }
            Write-Host ("  {0,-32} | {1,10} | {2,14} | {3,8} | {4,8} | {5}" -f $r.tool, $fcStr, $callStr, $r.falseGreens, $r.kbOccurrences, $idsStr) -ForegroundColor $color
        }

        $summaryPayload = [ordered]@{
            outcomeLogPath                = $OutcomeLogPath
            firstCallSuccessRate          = $overallFirstCallRate
            rawFirstCallSuccessRate       = $rawFirstCallRate
            totalFirstCalls               = $totalFirstCalls
            totalFirstSuccesses           = $totalFirstSuccesses
            toolReliabilityRate           = $toolReliabilityRate
            overallCallSuccessRate        = $overallCallRate
            totalInvocations              = $totalInvocations
            totalCallerErrors             = $totalCallerErrors
            totalToolFaults               = $totalToolFaults
            totalFalseGreens              = $totalFalseGreens
            falseGreenRate                = $overallFalseGreenRate
            totalEscapeHatches            = $totalEscapeHatches
            unrequested_mailbox_fallbacks = $totalUnrequestedMailboxFallbacks
            unrequestedMailboxFallbacks   = $totalUnrequestedMailboxFallbacks
            tools                         = $sortedRows
        }

        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, ($summaryPayload | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
        }
        exit 0
    }

    "ingest-outcomes" {
        $inputDir = if (-not [string]::IsNullOrWhiteSpace($OutcomeLogPath)) {
            if (Test-Path -LiteralPath $OutcomeLogPath -PathType Container) { $OutcomeLogPath } else { Split-Path -Parent $OutcomeLogPath }
        } else {
            Join-Path $RepoRoot "logs"
        }
        $parquetOut = Join-Path $inputDir "analytics\mcp_outcomes.parquet"
        $kongorDll = Join-Path $RepoRoot "src\tools\kongor-cli\bin\Release\net11.0\kongor.dll"
        if (-not (Test-Path -LiteralPath $kongorDll -PathType Leaf)) {
            $kongorDll = Join-Path $RepoRoot "src\tools\kongor-cli\bin\Debug\net11.0\kongor.dll"
        }
        if (-not (Test-Path -LiteralPath $kongorDll -PathType Leaf)) {
            Write-Error "Compiled kongor.dll not found. Build first with: ./scripts/Invoke-KongorTestLock.ps1 -Owner 'build' -- dotnet build src/tools/kongor-cli/Kongor.Cli.csproj -c Release"
            exit 1
        }

        $cliArgs = @($kongorDll, "mcp-outcomes", "--input-dir", $inputDir, "--parquet-out", $parquetOut)
        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            $cliArgs += @("--json-out", $JsonOutput)
        }
        Write-McpKbLog "Ingesting MCP NDJSON outcomes from '$inputDir' -> '$parquetOut' via DuckDB/Parquet..."
        & dotnet @cliArgs
        exit $LASTEXITCODE
    }

    "semantic-dedup" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $dedupScript = Join-Path $PSScriptRoot "qa\mcp_issue_semantic_dedup.py"
        if (-not (Test-Path -LiteralPath $dedupScript -PathType Leaf)) {
            Write-Error "Semantic dedup helper not found at '$dedupScript'. Restore scripts/qa/mcp_issue_semantic_dedup.py."
            exit 1
        }

        $pyArgs = @(
            $dedupScript,
            "--issues-dir", $IssuesDir,
            "--threshold", ([string]$Threshold),
            "--top-k", ([string]$TopK)
        )
        if ($IncludeSuperseded) {
            $pyArgs += "--include-superseded"
        }
        if (-not [string]::IsNullOrWhiteSpace($Query)) {
            $pyArgs += @("--query", $Query)
        }
        if (-not [string]::IsNullOrWhiteSpace($PgvectorDsn)) {
            $pyArgs += @("--pgvector-dsn", $PgvectorDsn)
        }
        if (-not [string]::IsNullOrWhiteSpace($EmitSql)) {
            $pyArgs += @("--emit-sql", $EmitSql)
        }
        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            $pyArgs += @("--json-out", $JsonOutput)
        }
        if ($Quiet) {
            $pyArgs += "--quiet"
        }

        Write-McpKbLog "Running 384-d semantic deduplication across '$IssuesDir' (threshold=$Threshold, topK=$TopK, includeSuperseded=$IncludeSuperseded)..."
        & python @pyArgs
        $exitCode = $LASTEXITCODE
        $sw.Stop()
        if ($exitCode -eq 0) {
            Write-McpKbLog "Semantic deduplication completed in $($sw.ElapsedMilliseconds)ms." Green
        } else {
            Write-McpKbLog "Semantic deduplication failed (exitCode=$exitCode) after $($sw.ElapsedMilliseconds)ms." Red
        }
        exit $exitCode
    }
}
