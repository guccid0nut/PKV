# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Project Kongor Searchable Field-Note Knowledge Base CLI (search, rebuild-index, check, new).

.DESCRIPTION
    Manages curated K2/Kongor reverse-engineering field notes under docs/reference/field-notes/.
    - search        : Sub-50ms search across index.json and note bodies using Select-String.
    - rebuild-index : Parses YAML frontmatter across FN-*.md and writes deterministic index.json + README.md.
    - check         : Validates frontmatter schema, runs shared secret scrubbing, and enforces zero index.json drift.
    - new           : Scaffolds a new FN-NNN-*.md from TEMPLATE.md and rebuilds index.json.

.EXAMPLE
    .\scripts\kongor_kb.ps1 -Action search -Query "0x92BB66"
    .\scripts\kongor_kb.ps1 -Action rebuild-index
    .\scripts\kongor_kb.ps1 -Action check
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("search", "rebuild-index", "check", "new", "check-decay", "add-dead-end", "search-dead-ends", "verify-quote", "verify_quote")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$Query = "",

    [Parameter(Mandatory = $false)]
    [string]$NotesDir = "",

    [Parameter(Mandatory = $false)]
    [string]$Title = "",

    [Parameter(Mandatory = $false)]
    [string]$Kind = "technique",

    [Parameter(Mandatory = $false)]
    [int]$MaxAgeDays = 30,

    [Parameter(Mandatory = $false)]
    [string]$Subsystem = "k2_x64.dll",

    [Parameter(Mandatory = $false)]
    [string]$Hypothesis = "",

    [Parameter(Mandatory = $false)]
    [string]$Counterevidence = "",

    [Parameter(Mandatory = $false)]
    [string]$EvidenceSource = "",

    [Parameter(Mandatory = $false)]
    [string]$EvidenceQuote = "",

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [switch]$IncludeSuperseded,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($NotesDir)) {
    $NotesDir = Join-Path $RepoRoot "docs\reference\field-notes"
}

$secretScrubHelper = Join-Path $PSScriptRoot "lib\Test-KongorSecretScrub.ps1"
if (Test-Path -LiteralPath $secretScrubHelper) {
    . $secretScrubHelper
}

function Write-KbLog {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Cyan
    )
    if (-not $Quiet) {
        $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
        Write-Host "[$ts] [KONGOR-KB] $Message" -ForegroundColor $Color
    }
}

# satisfies: #250 (repowise-kb-provenance-ladder-and-quote-verifier — 5-tier provenance rank)
$script:KbProvenanceRanks = @{
    "session"  = 5
    "adr"      = 4
    "commit"   = 3
    "comment"  = 2
    "inferred" = 1
}

function Get-KbProvenanceRank {
    param([string]$Provenance)
    if ([string]::IsNullOrWhiteSpace($Provenance)) {
        return 5 # Default verified notes without explicit tag to Session(5)
    }
    $key = $Provenance.Trim().ToLowerInvariant()
    if ($script:KbProvenanceRanks.ContainsKey($key)) {
        return [int]$script:KbProvenanceRanks[$key]
    }
    return 0
}

# satisfies: #250 (repowise-kb-provenance-ladder-and-quote-verifier — anti-hallucination verbatim substring verify_quote)
function Test-KbEvidenceQuote {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$QuoteText,

        [Parameter(Mandatory = $false)]
        [string]$BaseNotesDir = $NotesDir,

        [Parameter(Mandatory = $false)]
        [string]$BaseRepoRoot = $RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "[KB-QUOTE-HALLUCINATION-250] Missing evidence_source path for evidence_quote verification."
        }
    }
    if ([string]::IsNullOrWhiteSpace($QuoteText)) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "[KB-QUOTE-HALLUCINATION-250] Empty evidence_quote string."
        }
    }

    $resolved = $SourcePath
    if (-not [System.IO.Path]::IsPathRooted($resolved)) {
        $cand1 = Join-Path $BaseRepoRoot ($SourcePath -replace '/', '\')
        $cand2 = Join-Path $BaseNotesDir ($SourcePath -replace '/', '\')
        if (Test-Path -LiteralPath $cand1 -PathType Leaf) {
            $resolved = $cand1
        } elseif (Test-Path -LiteralPath $cand2 -PathType Leaf) {
            $resolved = $cand2
        } elseif (($SourcePath -replace '\\', '/') -match '^games/' -and (-not (Test-Path -LiteralPath (Join-Path $BaseRepoRoot "games") -PathType Container))) {
            # Cleanroom checkout without games/ overlay: skip retail shader/asset file check while keeping strict verification when games/ exists
            return [PSCustomObject]@{
                Valid  = $true
                Reason = ""
            }
        } else {
            return [PSCustomObject]@{
                Valid  = $false
                Reason = "[KB-QUOTE-HALLUCINATION-250] evidence_source file not found: '$SourcePath'."
            }
        }
    } elseif (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "[KB-QUOTE-HALLUCINATION-250] evidence_source file not found: '$resolved'."
        }
    }

    $srcContent = ([System.IO.File]::ReadAllText($resolved)) -replace "`r`n", "`n"
    $normQuote = $QuoteText -replace "`r`n", "`n"
    if ($srcContent.IndexOf($normQuote, [System.StringComparison]::Ordinal) -lt 0) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "[KB-QUOTE-HALLUCINATION-250] Verbatim quote verification failed: '$normQuote' is not an exact substring of '$SourcePath'."
        }
    }

    return [PSCustomObject]@{
        Valid  = $true
        Reason = ""
    }
}

# satisfies: #40 (canonical-path-no-reparse)
function Test-CanonicalNotePath {
    param(
        [string]$CandidatePath,
        [string]$AllowedRoot
    )
    if (-not (Test-Path -LiteralPath $CandidatePath)) {
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
    return @($trimmed.Trim('"', "'"))
}

function Parse-FieldNoteFrontmatter {
    param([string]$FilePath)

    $canonCheck = Test-CanonicalNotePath -CandidatePath $FilePath -AllowedRoot $NotesDir
    if (-not $canonCheck.Valid) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = $canonCheck.Reason
            Meta   = $null
            Body   = ""
        }
    }

    $raw = Get-Content -LiteralPath $FilePath -Raw -ErrorAction Stop
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

    foreach ($line in ($yamlBlock -split "`n")) {
        $cleanLine = ($line -replace '\s+#.*$', '').Trim()
        if ([string]::IsNullOrWhiteSpace($cleanLine)) { continue }
        if ($cleanLine -match '^([a-zA-Z0-9_]+)\s*:\s*(.*)$') {
            $key = $Matches[1].Trim()
            $val = $Matches[2].Trim()
            # satisfies: #40 (canonical-path-no-reparse — duplicate YAML frontmatter key rejection)
            if (-not $seenKeys.Add($key)) {
                return [PSCustomObject]@{
                    Valid  = $false
                    Reason = "Duplicate YAML frontmatter key '$key' detected (#40 anti-shadowing guard)"
                    Meta   = $meta
                    Body   = $body
                }
            }
            if ($key -in @("rvas", "tags", "supersedes")) {
                $meta[$key] = @(Parse-InlineYamlArray -RawValue $val)
            } else {
                $meta[$key] = $val.Trim('"', "'")
            }
        }
    }

    $requiredKeys = @("id", "kind", "title", "binary_or_subsystem", "rvas", "status", "supersedes", "invalidated_by", "date", "tags")
    $missingKeys = New-Object System.Collections.Generic.List[string]
    foreach ($rk in $requiredKeys) {
        if (-not $meta.Contains($rk)) {
            $missingKeys.Add($rk)
        }
    }

    if ($missingKeys.Count -gt 0) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "Missing required frontmatter key(s): $($missingKeys -join ', ')"
            Meta   = $meta
            Body   = $body
        }
    }

    # satisfies: #35 (epistemic-dossier-obligations — tombstoned status)
    if ($meta["status"] -notin @("verified", "superseded", "invalidated", "tombstoned")) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "Invalid status '$($meta["status"])' (must be verified, superseded, invalidated, or tombstoned)"
            Meta   = $meta
            Body   = $body
        }
    }

    # satisfies: #250 (repowise-kb-provenance-ladder-and-quote-verifier)
    $provName = if ($meta.Contains("provenance") -and (-not [string]::IsNullOrWhiteSpace([string]$meta["provenance"]))) {
        ([string]$meta["provenance"]).Trim()
    } else {
        "Session"
    }
    $provRank = Get-KbProvenanceRank -Provenance $provName
    if ($provRank -le 0) {
        return [PSCustomObject]@{
            Valid  = $false
            Reason = "[KB-PROVENANCE-250] Invalid provenance tier '$provName' (allowed: Session(5), ADR(4), Commit(3), Comment(2), Inferred(1))"
            Meta   = $meta
            Body   = $body
        }
    }

    $evSource = if ($meta.Contains("evidence_source")) { ([string]$meta["evidence_source"]).Trim() } else { "" }
    $evQuote  = if ($meta.Contains("evidence_quote")) { ([string]$meta["evidence_quote"]) } else { "" }
    if (-not [string]::IsNullOrWhiteSpace($evQuote) -or -not [string]::IsNullOrWhiteSpace($evSource)) {
        $qCheck = Test-KbEvidenceQuote -SourcePath $evSource -QuoteText $evQuote -BaseNotesDir $NotesDir -BaseRepoRoot $RepoRoot
        if (-not $qCheck.Valid) {
            return [PSCustomObject]@{
                Valid  = $false
                Reason = $qCheck.Reason
                Meta   = $meta
                Body   = $body
            }
        }
    }

    $claimKey = if ($meta.Contains("claim_key")) { ([string]$meta["claim_key"]).Trim() } else { "" }
    $claimVal = if ($meta.Contains("claim_value")) { ([string]$meta["claim_value"]).Trim() } else { "" }

    $entry = [ordered]@{
        id                  = [string]$meta["id"]
        file                = [System.IO.Path]::GetFileName($FilePath)
        kind                = [string]$meta["kind"]
        title               = [string]$meta["title"]
        binary_or_subsystem = [string]$meta["binary_or_subsystem"]
        rvas                = @($meta["rvas"])
        status              = [string]$meta["status"]
        supersedes          = @($meta["supersedes"])
        invalidated_by      = [string]$meta["invalidated_by"]
        date                = [string]$meta["date"]
        tags                = @($meta["tags"])
    }

    $provInfo = [PSCustomObject]@{
        id                  = [string]$meta["id"]
        file                = [System.IO.Path]::GetFileName($FilePath)
        status              = [string]$meta["status"]
        binary_or_subsystem = [string]$meta["binary_or_subsystem"]
        supersedes          = @($meta["supersedes"])
        provenance          = $provName
        provenance_rank     = $provRank
        evidence_source     = $evSource
        evidence_quote      = $evQuote
        claim_key           = $claimKey
        claim_value         = $claimVal
    }

    return [PSCustomObject]@{
        Valid          = $true
        Reason         = ""
        Meta           = $entry
        ProvenanceInfo = $provInfo
        Body           = $body
    }
}

function Get-AllFieldNotes {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $Dir -Filter "FN-*.md" -File | Sort-Object Name)
}

function Build-DeterministicIndexJson {
    param([array]$Entries)
    return (ConvertTo-Json -InputObject @($Entries) -Depth 6) -replace "`r`n", "`n"
}

switch ($Action) {
    { $_ -in @("verify-quote", "verify_quote") } {
        $qRes = Test-KbEvidenceQuote -SourcePath $EvidenceSource -QuoteText $EvidenceQuote -BaseNotesDir $NotesDir -BaseRepoRoot $RepoRoot
        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, ($qRes | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
        }
        if (-not $qRes.Valid) {
            Write-Host "  [KB FAIL] $($qRes.Reason)" -ForegroundColor Red
            exit 1
        }
        Write-KbLog "PASS: Verbatim quote verified against '$EvidenceSource'." Green
        exit 0
    }

    "rebuild-index" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if (-not (Test-Path -LiteralPath $NotesDir)) {
            Write-Error "Field-notes directory not found: '$NotesDir'."
            exit 1
        }

        $noteFiles = Get-AllFieldNotes -Dir $NotesDir
        $entries = New-Object System.Collections.Generic.List[object]
        foreach ($nf in $noteFiles) {
            $parsed = Parse-FieldNoteFrontmatter -FilePath $nf.FullName
            if (-not $parsed.Valid) {
                Write-Error "[KB SCHEMA ERROR] $($nf.Name): $($parsed.Reason). Fix frontmatter and re-run .\scripts\kongor_kb.ps1 -Action rebuild-index"
                exit 1
            }
            $entries.Add($parsed.Meta)
        }

        $indexPath = Join-Path $NotesDir "index.json"
        $jsonContent = Build-DeterministicIndexJson -Entries $entries.ToArray()
        [System.IO.File]::WriteAllText($indexPath, $jsonContent + "`n", (New-Object System.Text.UTF8Encoding($false)))

        $readmePath = Join-Path $NotesDir "README.md"
        $mdLines = New-Object System.Collections.Generic.List[string]
        $mdLines.Add("# Project Kongor Field-Note Knowledge Base")
        $mdLines.Add("")
        $mdLines.Add('> Auto-generated by `scripts/kongor_kb.ps1 -Action rebuild-index`. Do not hand-edit without running `rebuild-index`.')
        $mdLines.Add("")
        $mdLines.Add("| ID | Kind | Status | Subsystem / Binary | RVAs | Title |")
        $mdLines.Add("| :--- | :--- | :--- | :--- | :--- | :--- |")
        foreach ($e in $entries) {
            $rvaStr = if ($e.rvas.Count -gt 0) { '`' + ($e.rvas -join '`, `') + '`' } else { "-" }
            $mdLines.Add(('| [`{0}`](./{1}) | `{2}` | `{3}` | `{4}` | {5} | {6} |' -f $e["id"], $e["file"], $e["kind"], $e["status"], $e["binary_or_subsystem"], $rvaStr, $e["title"]))
        }
        [System.IO.File]::WriteAllText($readmePath, ($mdLines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

        $sw.Stop()
        Write-KbLog "Rebuilt index.json and README.md for $($entries.Count) field note(s) in $($sw.ElapsedMilliseconds)ms." Green
        exit 0
    }

    "check" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $errors = New-Object System.Collections.Generic.List[string]
        $indexDrift = $false
        $secretLeaks = 0
        $noteFiles = Get-AllFieldNotes -Dir $NotesDir
        $entries = New-Object System.Collections.Generic.List[object]
        $provById = @{}
        $provList = New-Object System.Collections.Generic.List[object]

        if ($noteFiles.Count -eq 0) {
            $errors.Add("Zero FN-*.md field notes found in '$NotesDir'.")
        }

        foreach ($nf in $noteFiles) {
            $parsed = Parse-FieldNoteFrontmatter -FilePath $nf.FullName
            if (-not $parsed.Valid) {
                $errors.Add("$($nf.Name): $($parsed.Reason)")
            } else {
                $entries.Add($parsed.Meta)
                if ($null -ne $parsed.ProvenanceInfo) {
                    $provById[$parsed.ProvenanceInfo.id] = $parsed.ProvenanceInfo
                    $provList.Add($parsed.ProvenanceInfo)
                }
            }

            if (Get-Command Test-KongorSecretScrub -ErrorAction SilentlyContinue) {
                $scrub = Test-KongorSecretScrub -FilePath $nf.FullName
                if (-not $scrub.Passed) {
                    $secretLeaks += $scrub.Findings.Count
                    $masked = if ($scrub.Findings[0].MaskedSnippet) { $scrub.Findings[0].MaskedSnippet } else { $scrub.Findings[0].Match }
                    $errors.Add("$($nf.Name): Secret leak detected ('$masked')")
                }
            }
        }

        # Enforce 5-Tier Provenance Authority Ladder on supersession & Contradictory Claim Detection (#250)
        $activeClaims = @{}
        foreach ($pi in $provList) {
            foreach ($supId in @($pi.supersedes)) {
                if (-not [string]::IsNullOrWhiteSpace($supId) -and $provById.ContainsKey($supId)) {
                    $targetProv = $provById[$supId]
                    if ($pi.provenance_rank -lt $targetProv.provenance_rank) {
                        $errors.Add("[KB-PROVENANCE-250] $($pi.file): Lower-authority note $($pi.id) ($($pi.provenance)=$($pi.provenance_rank)) cannot supersede higher-authority note $supId ($($targetProv.provenance)=$($targetProv.provenance_rank)).")
                    }
                }
            }
            if ($pi.status -eq "verified" -and -not [string]::IsNullOrWhiteSpace($pi.claim_key) -and -not [string]::IsNullOrWhiteSpace($pi.claim_value)) {
                $cLookup = "$($pi.binary_or_subsystem)|$($pi.claim_key)".ToLowerInvariant()
                if ($activeClaims.ContainsKey($cLookup)) {
                    $prevClaimNote = $activeClaims[$cLookup]
                    $validCurrSupersedesPrev = ($pi.supersedes -contains $prevClaimNote.id) -and ($pi.provenance_rank -ge $prevClaimNote.provenance_rank)
                    $validPrevSupersedesCurr = ($prevClaimNote.supersedes -contains $pi.id) -and ($prevClaimNote.provenance_rank -ge $pi.provenance_rank)
                    $supersedesPrev = $validCurrSupersedesPrev -or $validPrevSupersedesCurr
                    if ((-not $supersedesPrev) -and ($prevClaimNote.claim_value -ne $pi.claim_value)) {
                        $errors.Add("[KB-CONTRADICTION-250] Contradictory active claim on '$($pi.claim_key)' in '$($pi.binary_or_subsystem)': $($prevClaimNote.id)='$($prevClaimNote.claim_value)' ($($prevClaimNote.provenance)=$($prevClaimNote.provenance_rank)) vs $($pi.id)='$($pi.claim_value)' ($($pi.provenance)=$($pi.provenance_rank)).")
                    } elseif ($validCurrSupersedesPrev) {
                        $activeClaims[$cLookup] = $pi
                    }
                } else {
                    $activeClaims[$cLookup] = $pi
                }
            }
        }

        $indexPath = Join-Path $NotesDir "index.json"
        if (-not (Test-Path -LiteralPath $indexPath)) {
            $indexDrift = $true
            $errors.Add("Missing index.json at '$indexPath'. Run: .\scripts\kongor_kb.ps1 -Action rebuild-index")
        } elseif ($entries.Count -gt 0) {
            $expectedJson = (Build-DeterministicIndexJson -Entries $entries.ToArray()).Trim()
            $actualJson = ((Get-Content -LiteralPath $indexPath -Raw -ErrorAction SilentlyContinue) -replace "`r`n", "`n").Trim()
            if ($expectedJson -cne $actualJson) {
                $indexDrift = $true
                $errors.Add("index.json is out of sync with FN-*.md frontmatter. Run: .\scripts\kongor_kb.ps1 -Action rebuild-index")
            }
        }

        $sw.Stop()
        $checkSummary = [ordered]@{
            passed       = ($errors.Count -eq 0 -and -not $indexDrift)
            notesChecked = $noteFiles.Count
            indexDrift   = $indexDrift
            secretLeaks  = $secretLeaks
            errors       = $errors.ToArray()
            durationMs   = $sw.ElapsedMilliseconds
        }

        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            $outJson = $checkSummary | ConvertTo-Json -Depth 5
            [System.IO.File]::WriteAllText($JsonOutput, $outJson, (New-Object System.Text.UTF8Encoding($false)))
        }

        if (-not $checkSummary.passed) {
            foreach ($err in $errors) {
                Write-Host "  [KB FAIL] $err" -ForegroundColor Red
            }
            Write-Host " [FAIL] Field-Note KB check failed ($($errors.Count) issue(s), indexDrift=$indexDrift). Run: .\scripts\kongor_kb.ps1 -Action rebuild-index" -ForegroundColor Red
            if ($indexDrift) { exit 3 }
            exit 1
        }

        Write-KbLog "PASS: Checked $($noteFiles.Count) field note(s) (0 schema errors, 0 index drift, 0 secret leaks) in $($sw.ElapsedMilliseconds)ms." Green
        exit 0
    }

    "search" {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if ([string]::IsNullOrWhiteSpace($Query)) {
            Write-Error "Must provide -Query '<keyword|tag|rva>' for -Action search."
            exit 1
        }

        $indexPath = Join-Path $NotesDir "index.json"
        if (-not (Test-Path -LiteralPath $indexPath)) {
            Write-Error "index.json not found at '$indexPath'. Run: .\scripts\kongor_kb.ps1 -Action rebuild-index"
            exit 1
        }

        $parsedJson = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        $indexList = if ($parsedJson -is [System.Array]) { $parsedJson } else { @($parsedJson) }
        $matchedNotes = New-Object System.Collections.Generic.List[object]

        # satisfies: #56 (exact-id-lookup-shortcircuit)
        $trimmedQuery = $Query.Trim()
        $isExactId = ($trimmedQuery -match '^FN-\d+$')
        $isExactRva = ($trimmedQuery -match '^0x[0-9a-fA-F]+$')

        if ($isExactId -or $isExactRva) {
            foreach ($entry in $indexList) {
                if (-not $IncludeSuperseded -and $entry.status -ne "verified") {
                    continue
                }
                $hitSource = ""
                if ($isExactId -and ([string]$entry.id).Equals($trimmedQuery, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $hitSource = "exact-id"
                } elseif ($isExactRva) {
                    foreach ($rva in @($entry.rvas)) {
                        if (([string]$rva).Equals($trimmedQuery, [System.StringComparison]::OrdinalIgnoreCase)) {
                            $hitSource = "exact-rva"
                            break
                        }
                    }
                }
                if (-not [string]::IsNullOrWhiteSpace($hitSource)) {
                    $matchedNotes.Add([ordered]@{
                        id                  = $entry.id
                        title               = $entry.title
                        kind                = $entry.kind
                        status              = $entry.status
                        binary_or_subsystem = $entry.binary_or_subsystem
                        rvas                = @($entry.rvas)
                        tags                = @($entry.tags)
                        file                = $entry.file
                        matchedIn           = $hitSource
                    })
                }
            }
        }

        if ($matchedNotes.Count -eq 0) {
            foreach ($entry in $indexList) {
                if (-not $IncludeSuperseded -and $entry.status -ne "verified") {
                    continue
                }

                $notePath = Join-Path $NotesDir $entry.file
                $metaString = "$($entry.id) $($entry.kind) $($entry.title) $($entry.binary_or_subsystem) $($entry.rvas -join ' ') $($entry.tags -join ' ')"
                $hitSource = ""

                if ($metaString -match [regex]::Escape($Query)) {
                    $hitSource = "frontmatter"
                } elseif (Test-Path -LiteralPath $notePath) {
                    $bodyHit = Select-String -LiteralPath $notePath -Pattern $Query -SimpleMatch -ErrorAction SilentlyContinue
                    if ($null -ne $bodyHit) {
                        $hitSource = "body (line $($bodyHit[0].LineNumber))"
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($hitSource)) {
                    $matchedNotes.Add([ordered]@{
                        id                  = $entry.id
                        title               = $entry.title
                        kind                = $entry.kind
                        status              = $entry.status
                        binary_or_subsystem = $entry.binary_or_subsystem
                        rvas                = @($entry.rvas)
                        tags                = @($entry.tags)
                        file                = $entry.file
                        matchedIn           = $hitSource
                    })
                }
            }
        }

        $sw.Stop()
        Write-KbLog "Search '$Query' matched $($matchedNotes.Count) note(s) in $($sw.ElapsedMilliseconds)ms." Green
        foreach ($m in $matchedNotes) {
            Write-Host "  [$($m.id)] ($($m.kind)) $($m.title) [RVAs: $($m.rvas -join ', ')] -> docs/reference/field-notes/$($m.file) ($($m.matchedIn))" -ForegroundColor White
        }

        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, (ConvertTo-Json -InputObject @($matchedNotes.ToArray()) -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        }
        exit 0
    }

    "check-decay" {
        # satisfies: #113 (kb-decay-and-mcp-promotion — 30-day staleness decay check)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $indexPath = Join-Path $NotesDir "index.json"
        if (-not (Test-Path -LiteralPath $indexPath)) {
            Write-Error "index.json not found at '$indexPath'. Run: .\scripts\kongor_kb.ps1 -Action rebuild-index"
            exit 1
        }

        $parsedJson = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        $indexList = if ($parsedJson -is [System.Array]) { $parsedJson } else { @($parsedJson) }
        $nowUtc = (Get-Date).ToUniversalTime()
        $staleNotes = New-Object System.Collections.Generic.List[object]
        $freshCount = 0

        foreach ($entry in $indexList) {
            if ($entry.status -ne "verified") { continue }
            $parsedDate = [DateTime]::MinValue
            if ([DateTime]::TryParse([string]$entry.date, [ref]$parsedDate)) {
                $ageDays = [int][Math]::Floor(($nowUtc - $parsedDate.ToUniversalTime()).TotalDays)
                if ($ageDays -gt $MaxAgeDays) {
                    $staleNotes.Add([ordered]@{
                        id                  = [string]$entry.id
                        title               = [string]$entry.title
                        date                = [string]$entry.date
                        ageDays             = $ageDays
                        maxAgeDays          = $MaxAgeDays
                        binary_or_subsystem = [string]$entry.binary_or_subsystem
                        file                = [string]$entry.file
                    })
                } else {
                    $freshCount++
                }
            }
        }

        $sw.Stop()
        $decayReport = [ordered]@{
            maxAgeDays = $MaxAgeDays
            freshCount = $freshCount
            staleCount = $staleNotes.Count
            staleNotes = @($staleNotes.ToArray())
            durationMs = $sw.ElapsedMilliseconds
        }
        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, ($decayReport | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        }

        if ($staleNotes.Count -gt 0) {
            Write-KbLog "[KB-DECAY-WARN] $($staleNotes.Count) verified field note(s) exceed ${MaxAgeDays}-day freshness window (re-verify RVAs or update date):" Yellow
            foreach ($sn in $staleNotes) {
                Write-Host "  [STALE $($sn.ageDays)d] [$($sn.id)] $($sn.title) (date=$($sn.date), subsystem=$($sn.binary_or_subsystem))" -ForegroundColor Yellow
            }
        } else {
            Write-KbLog "[KB-DECAY-OK] All $freshCount verified field note(s) are within the ${MaxAgeDays}-day freshness window ($($sw.ElapsedMilliseconds)ms)." Green
        }
        exit 0
    }

    "add-dead-end" {
        # satisfies: #113 (kb-decay-and-mcp-promotion — dead-ends.jsonl negative memory)
        if ([string]::IsNullOrWhiteSpace($Hypothesis) -or [string]::IsNullOrWhiteSpace($Counterevidence)) {
            Write-Error "Must provide -Hypothesis '<text>' and -Counterevidence '<text>' for -Action add-dead-end."
            exit 1
        }
        $deadEndsPath = Join-Path $NotesDir "dead-ends.jsonl"
        $existingLines = @()
        if (Test-Path -LiteralPath $deadEndsPath) {
            $existingLines = @(Get-Content -LiteralPath $deadEndsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        }
        $nextId = ("DE-{0:D3}" -f ($existingLines.Count + 1))
        $record = [ordered]@{
            id              = $nextId
            date            = (Get-Date).ToString("yyyy-MM-dd")
            subsystem       = $Subsystem
            status          = "tombstoned"
            hypothesis      = $Hypothesis
            counterevidence = $Counterevidence
        }
        $jsonLine = ($record | ConvertTo-Json -Compress)
        [System.IO.File]::AppendAllText($deadEndsPath, $jsonLine + "`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-KbLog "[DEAD-END RECORDED] Appended $nextId ($Subsystem) to dead-ends.jsonl." Green
        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, ($record | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
        }
        exit 0
    }

    "search-dead-ends" {
        # satisfies: #113 (kb-decay-and-mcp-promotion — dead-ends.jsonl negative memory lookup)
        $deadEndsPath = Join-Path $NotesDir "dead-ends.jsonl"
        $matchesList = New-Object System.Collections.Generic.List[object]
        if (Test-Path -LiteralPath $deadEndsPath) {
            $lines = @(Get-Content -LiteralPath $deadEndsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            foreach ($line in $lines) {
                $obj = $line | ConvertFrom-Json
                if ([string]::IsNullOrWhiteSpace($Query) -or ($line -match [regex]::Escape($Query))) {
                    $matchesList.Add($obj)
                }
            }
        }
        Write-KbLog "[DEAD-END SEARCH] Query '$Query' matched $($matchesList.Count) tombstoned dead-end(s)." Cyan
        foreach ($de in $matchesList) {
            Write-Host "  [$($de.id)] [TOMBSTONED] ($($de.subsystem)) Hypothesis: $($de.hypothesis) | Counterevidence: $($de.counterevidence)" -ForegroundColor Yellow
        }
        if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
            [System.IO.File]::WriteAllText($JsonOutput, (ConvertTo-Json -InputObject @($matchesList.ToArray()) -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
        }
        exit 0
    }

    "new" {
        if ([string]::IsNullOrWhiteSpace($Title)) {
            Write-Error "Must provide -Title '<title>' for -Action new."
            exit 1
        }

        $noteFiles = Get-AllFieldNotes -Dir $NotesDir
        $maxNum = 0
        foreach ($nf in $noteFiles) {
            if ($nf.Name -match '^FN-(\d+)') {
                $n = [int]$Matches[1]
                if ($n -gt $maxNum) { $maxNum = $n }
            }
        }
        $nextNum = $maxNum + 1
        $newId = ("FN-{0:D3}" -f $nextNum)
        $slug = ($Title.ToLower() -replace '[^a-z0-9]+', '-').Trim('-')
        $newFileName = "$newId-$slug.md"
        $newFilePath = Join-Path $NotesDir $newFileName

        $today = (Get-Date).ToString("yyyy-MM-dd")
        $templatePath = Join-Path $NotesDir "TEMPLATE.md"
        $content = Get-Content -LiteralPath $templatePath -Raw
        $content = $content -replace 'id:\s*FN-000', "id: $newId"
        $content = $content -replace 'kind:\s*technique.*', "kind: $Kind"
        $content = $content -replace 'title:\s*"<Short descriptive title>"', "title: `"$Title`""
        $content = $content -replace 'date:\s*\d{4}-\d{2}-\d{2}', "date: $today"
        $content = $content -replace '# FN-000: <Short Descriptive Title>', "# ${newId}: $Title"

        [System.IO.File]::WriteAllText($newFilePath, $content, (New-Object System.Text.UTF8Encoding($false)))
        Write-KbLog "Created field note '$newFileName'; rebuilding index.json..." Green
        & $PSCommandPath -Action rebuild-index -NotesDir $NotesDir -Quiet:$Quiet
        exit $LASTEXITCODE
    }
}
