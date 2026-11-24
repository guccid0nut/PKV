# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Verifies that all Reconstruction Obligations and Unknowns in Project Kongor Evidence Dossiers are satisfied.
.DESCRIPTION
    Audits Evidence Dossiers (artifacts/evidence/**/*.json) against cleanroom code, test suites, and dynamic
    triangulation logs. Enforces double-entry verification adapted from the REA architecture.
.PARAMETER Dossier
    Path to a specific evidence dossier JSON file to audit.
.PARAMETER All
    Audits all non-template dossiers in artifacts/evidence/.
.PARAMETER Files
    A list of modified files (e.g. from an override injection or git diff) to verify against any mapping dossiers.
.PARAMETER Strict
    Fails if any unknown is waived or triangulation status is not 'dynamically_confirmed'.
.PARAMETER SkipObligationCheck
    Bypasses verification entirely (emergency hotfix override).
.EXAMPLE
    .\scripts\verify_evidence_obligations.ps1 -Dossier artifacts/evidence/packets/game_x64_0x35d40_packet_parser.json
    .\scripts\verify_evidence_obligations.ps1 -All
    .\scripts\verify_evidence_obligations.ps1 -Files "src/services/master-server/PacketDispatcher.cs"
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [ValidateSet("audit", "next")]
    [string]$Action = "audit",

    [Parameter(Mandatory = $false)]
    [Alias("DossierDir", "EvidenceDir")]
    [string]$Dossier,

    [Parameter(Mandatory = $false)]
    [switch]$All,

    [Parameter(Mandatory = $false)]
    [string[]]$Files,

    [Parameter(Mandatory = $false)]
    [switch]$Strict,

    [Parameter(Mandatory = $false)]
    [switch]$StrictEpistemic,

    [Parameter(Mandatory = $false)]
    [Alias("i")]
    [switch]$Interactive,

    [Parameter(Mandatory = $false)]
    [switch]$Json,

    [Parameter(Mandatory = $false)]
    [string]$JsonOutput = "",

    [Parameter(Mandatory = $false)]
    [switch]$EmitCoverage,

    [Parameter(Mandatory = $false)]
    [switch]$SkipObligationCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-JsonProp {
    param([object]$Obj, [string]$Name, [object]$Default = $null)
    if ($null -eq $Obj) { return $Default }
    if ($Obj -is [System.Collections.IDictionary]) {
        if ($Obj.Contains($Name)) { return $Obj[$Name] }
        return $Default
    }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -ne $p) { return $p.Value }
    return $Default
}

function Write-AuditLog {
    param ([string]$Message, [string]$Level = "INFO")
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $color = switch ($Level) {
        "ERROR"   { "Red" }
        "WARN"    { "Yellow" }
        "SUCCESS" { "Green" }
        default   { "Cyan" }
    }
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $color
}

if ($SkipObligationCheck) {
    Write-AuditLog "SkipObligationCheck specified. Bypassing obligation verification." "WARN"
    exit 0
}

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$evidenceDir = Join-Path $repoRoot "artifacts\evidence"

function Test-CanonicalRepoPath {
    # satisfies: #40 (canonical-path-no-reparse)
    param([string]$CandidatePath, [string]$AllowedRoot = $repoRoot)
    if ([string]::IsNullOrWhiteSpace($CandidatePath) -or -not (Test-Path -LiteralPath $CandidatePath)) {
        return [PSCustomObject]@{ Valid = $false; Reason = "Path does not exist: '$CandidatePath'" }
    }
    $item = Get-Item -LiteralPath $CandidatePath -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return [PSCustomObject]@{ Valid = $false; Reason = "ReparsePoint/Symlink rejected (#40): '$CandidatePath'" }
    }
    return [PSCustomObject]@{ Valid = $true; Reason = "" }
}

function Test-DuplicateJsonTopKeys {
    # satisfies: #40 (canonical-path-no-reparse duplicate key guard)
    param([string]$RawJson)
    if ([string]::IsNullOrWhiteSpace($RawJson)) { return @() }
    $dups = New-Object System.Collections.Generic.List[string]
    $seenByDepth = @{}
    $depth = 0
    $tokenMatches = [regex]::Matches($RawJson, '("(?:[^"\\]|\\.)*"(?:\s*:)?|\{|\})')
    foreach ($m in $tokenMatches) {
        $tok = $m.Value
        if ($tok -eq "{") {
            $depth++
            $seenByDepth[$depth] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        } elseif ($tok -eq "}") {
            if ($seenByDepth.ContainsKey($depth)) {
                [void]$seenByDepth.Remove($depth)
            }
            if ($depth -gt 0) { $depth-- }
        } elseif ($depth -gt 0 -and $tok.EndsWith(":") -and $tok -match '^"((?:[^"\\]|\\.)*)"\s*:$') {
            $k = $Matches[1]
            if (-not $seenByDepth.ContainsKey($depth)) {
                $seenByDepth[$depth] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            }
            if (-not $seenByDepth[$depth].Add($k)) {
                if (-not $dups.Contains($k)) { $dups.Add($k) }
            }
        }
    }
    return $dups.ToArray()
}

function ConvertTo-Rfc8785CanonicalJson {
    # satisfies: #181 (rea-rfc8785-closure-digest-and-authority-gate)
    param([object]$InputObject)
    if ($null -eq $InputObject) { return "null" }
    if ($InputObject -is [bool]) {
        return $(if ($InputObject) { "true" } else { "false" })
    }
    if ($InputObject -is [string]) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('"')
        foreach ($ch in $InputObject.ToCharArray()) {
            $code = [int][char]$ch
            switch ($code) {
                0x08 { [void]$sb.Append('\b') }
                0x09 { [void]$sb.Append('\t') }
                0x0A { [void]$sb.Append('\n') }
                0x0C { [void]$sb.Append('\f') }
                0x0D { [void]$sb.Append('\r') }
                0x22 { [void]$sb.Append('\"') }
                0x5C { [void]$sb.Append('\\') }
                default {
                    if ($code -lt 0x20) {
                        [void]$sb.AppendFormat('\u{0:x4}', $code)
                    } else {
                        [void]$sb.Append($ch)
                    }
                }
            }
        }
        [void]$sb.Append('"')
        return $sb.ToString()
    }
    if ($InputObject -is [int] -or $InputObject -is [long] -or $InputObject -is [double] -or $InputObject -is [decimal] -or $InputObject -is [single]) {
        return [System.Convert]::ToString($InputObject, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($InputObject -is [System.Collections.IDictionary]) {
        $keys = @($InputObject.Keys | ForEach-Object { [string]$_ })
        [System.Array]::Sort($keys, [System.StringComparer]::Ordinal)
        $pairs = New-Object System.Collections.Generic.List[string]
        foreach ($k in $keys) {
            if ($k -eq "closure_digest_sha256") { continue }
            $keyJson = ConvertTo-Rfc8785CanonicalJson -InputObject $k
            $valJson = ConvertTo-Rfc8785CanonicalJson -InputObject $InputObject[$k]
            $pairs.Add("${keyJson}:${valJson}")
        }
        return "{" + ($pairs -join ",") + "}"
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = New-Object System.Collections.Generic.List[string]
        foreach ($el in $InputObject) {
            $items.Add((ConvertTo-Rfc8785CanonicalJson -InputObject $el))
        }
        return "[" + ($items -join ",") + "]"
    }
    if ($InputObject -is [PSCustomObject]) {
        $propNames = @($InputObject.PSObject.Properties | ForEach-Object { [string]$_.Name })
        [System.Array]::Sort($propNames, [System.StringComparer]::Ordinal)
        $pairs = New-Object System.Collections.Generic.List[string]
        foreach ($k in $propNames) {
            if ($k -eq "closure_digest_sha256") { continue }
            $keyJson = ConvertTo-Rfc8785CanonicalJson -InputObject $k
            $valJson = ConvertTo-Rfc8785CanonicalJson -InputObject $InputObject.$k
            $pairs.Add("${keyJson}:${valJson}")
        }
        return "{" + ($pairs -join ",") + "}"
    }
    return ConvertTo-Rfc8785CanonicalJson -InputObject ([string]$InputObject)
}

function Get-Rfc8785ClosureDigestSha256 {
    # satisfies: #181 (rea-rfc8785-closure-digest-and-authority-gate)
    param([object]$DossierObject)
    $canonical = ConvertTo-Rfc8785CanonicalJson -InputObject $DossierObject
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Invoke-EvaluateSemanticSubject {
    param([object]$Obj, [string]$SubjectId, [System.Collections.Generic.List[string]]$Violations)
    if ($null -eq $Obj) { return }

    # Rule 1: false-equivalence
    $eqClaimed = ((Get-JsonProp $Obj 'equivalence_claimed') -eq $true) -or (([string](Get-JsonProp $Obj 'equivalence_status')).ToLowerInvariant() -eq "equivalent")
    $evTrunc = ((Get-JsonProp $Obj 'evidence_truncated') -eq $true) -or ((Get-JsonProp $Obj 'truncated') -eq $true)
    $evUnst = ((Get-JsonProp $Obj 'evidence_unstable') -eq $true) -or ((Get-JsonProp $Obj 'unstable') -eq $true)
    $unverSideEffects = Get-JsonProp $Obj 'unverified_side_effects'
    $hasUnverSideEffects = ($null -ne $unverSideEffects -and @($unverSideEffects).Count -gt 0)
    if ($eqClaimed -and ($evTrunc -or $evUnst -or $hasUnverSideEffects)) {
        $Violations.Add("Semantic rule 'false-equivalence' (#182) [$SubjectId]: claims functional equivalence on truncated, unstable, or unverified-side-effect evidence.")
    }

    # Rule 2: unsafe-concurrency-semantics
    $isConc = ((Get-JsonProp $Obj 'is_concurrent_flow') -eq $true) -or ((Get-JsonProp $Obj 'concurrent') -eq $true)
    $assumesTotal = ((Get-JsonProp $Obj 'assumes_total_order') -eq $true)
    $syncProven = ((Get-JsonProp $Obj 'synchronization_proven') -eq $true)
    if (($isConc -and $assumesTotal -and -not $syncProven) -or ((Get-JsonProp $Obj 'shared_mutable_state') -eq $true -and [string]::IsNullOrWhiteSpace([string](Get-JsonProp $Obj 'thread_safety_invariant')))) {
        $Violations.Add("Semantic rule 'unsafe-concurrency-semantics' (#182) [$SubjectId]: concurrent flow assumes total ordering or shared mutable state without synchronization proof.")
    }

    # Rule 3: divergence-not-localized
    $hasDiv = ((Get-JsonProp $Obj 'has_divergence') -eq $true) -or (([string](Get-JsonProp $Obj 'conformance_status')).ToLowerInvariant() -eq "divergent")
    $firstDivSite = [string](Get-JsonProp $Obj 'first_divergence_site')
    $hasFirstDivObj = ($null -ne (Get-JsonProp $Obj 'first_divergence'))
    if ($hasDiv -and [string]::IsNullOrWhiteSpace($firstDivSite) -and -not $hasFirstDivObj) {
        $Violations.Add("Semantic rule 'divergence-not-localized' (#182) [$SubjectId]: reports divergence without localizing 'first_divergence_site' or 'first_divergence'.")
    }
    $divergences = Get-JsonProp $Obj 'divergences'
    if ($null -ne $divergences) {
        foreach ($div in @($divergences)) {
            $divRva = Get-JsonProp $div 'rva'
            $divCoords = Get-JsonProp $div 'coordinates'
            $loc = if ($divRva) { [string]$divRva } elseif ($divCoords) { [string]$divCoords } else { "" }
            if ([string]::IsNullOrWhiteSpace($loc)) {
                $Violations.Add("Semantic rule 'divergence-not-localized' (#182) [$SubjectId]: divergence entry is missing localized RVA or coordinates.")
            }
        }
    }

    # Rule 4: contradiction-proof-incomplete
    $st = ([string](Get-JsonProp $Obj 'status')).Trim().ToLowerInvariant()
    $cls = ([string](Get-JsonProp $Obj 'classification')).Trim().ToLowerInvariant()
    if ($st -eq "contradicted" -or $cls -eq "contradiction") {
        $digests = @()
        $evDigests = Get-JsonProp $Obj 'evidence_digests'
        if ($null -ne $evDigests) {
            $digests = @($evDigests | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { ([string]$_).Trim() } | Select-Object -Unique)
        }
        $count = $digests.Count
        $distinctEvDigests = Get-JsonProp $Obj 'distinct_evidence_digests'
        if ($null -ne $distinctEvDigests -and [int]$distinctEvDigests -gt $count) {
            $count = [int]$distinctEvDigests
        }
        if ($count -lt 2) {
            $Violations.Add("Semantic rule 'contradiction-proof-incomplete' (#182) [$SubjectId]: contradiction requires >= 2 distinct evidence digests (found $count).")
        }
    }

    # Rule 5: unbounded-loop-effect
    if (((Get-JsonProp $Obj 'has_unbounded_loop') -eq $true -and (Get-JsonProp $Obj 'loop_bound_verified') -ne $true) -or ((Get-JsonProp $Obj 'unbounded_loop') -eq $true -and [string]::IsNullOrWhiteSpace([string](Get-JsonProp $Obj 'loop_bound_invariant')))) {
        $Violations.Add("Semantic rule 'unbounded-loop-effect' (#182) [$SubjectId]: unbounded loop/recursion cycle without 'loop_bound_verified'.")
    }

    # Rule 6: aliasing-hazard-untracked
    if (((Get-JsonProp $Obj 'pointer_aliasing_possible') -eq $true -and (Get-JsonProp $Obj 'alias_separation_verified') -ne $true) -or ((Get-JsonProp $Obj 'pointer_aliasing_assumed') -eq $true -and [string]::IsNullOrWhiteSpace([string](Get-JsonProp $Obj 'aliasing_proof')))) {
        $Violations.Add("Semantic rule 'aliasing-hazard-untracked' (#182) [$SubjectId]: overlapping pointer read/write alias domain without 'alias_separation_verified'.")
    }

    # Rule 7: calling-convention-drift
    if (((Get-JsonProp $Obj 'abi_volatile_clobber') -eq $true) -or ((Get-JsonProp $Obj 'abi_boundary_crossed') -eq $true -and [string]::IsNullOrWhiteSpace([string](Get-JsonProp $Obj 'calling_convention')))) {
        $Violations.Add("Semantic rule 'calling-convention-drift' (#182) [$SubjectId]: ABI calling convention clobber or unverified calling convention.")
    }

    # Rule 8: unobserved-side-effect-claim
    $omittedOpsProp = Get-JsonProp $Obj 'omitted_operations_lower_bound'
    $omittedOps = if ($null -ne $omittedOpsProp) { [int]$omittedOpsProp } else { 0 }
    if ((Get-JsonProp $Obj 'claims_pure') -eq $true -and ($omittedOps -gt 0 -or (Get-JsonProp $Obj 'has_memory_writes') -eq $true)) {
        $Violations.Add("Semantic rule 'unobserved-side-effect-claim' (#182) [$SubjectId]: claims purity while omitted_operations_lower_bound=$omittedOps or memory writes exist.")
    }

    # Rule 9: floating-point-nan-hazard
    if (((Get-JsonProp $Obj 'has_raw_float_equality') -eq $true -and (Get-JsonProp $Obj 'uint_view_or_epsilon_verified') -ne $true) -or ((Get-JsonProp $Obj 'float_determinism_required') -eq $true -and (Get-JsonProp $Obj 'nan_inf_handling_verified') -ne $true)) {
        $Violations.Add("Semantic rule 'floating-point-nan-hazard' (#182) [$SubjectId]: IEEE-754 float equality without _UINT_VIEW or epsilon guard.")
    }

    # Rule 10: stale-oracle-snapshot
    if ((Get-JsonProp $Obj 'oracle_binary_sha256_mismatch') -eq $true) {
        $Violations.Add("Semantic rule 'stale-oracle-snapshot' (#182) [$SubjectId]: combines Ghidra and IDA/runtime snapshots from mismatched binary SHA-256 digests.")
    }

    # Rule 11: unresolved-indirect-dispatch
    $resTargetsProp = Get-JsonProp $Obj 'resolved_target_count'
    $resTargets = if ($null -ne $resTargetsProp) { [int]$resTargetsProp } else { 1 }
    if (((Get-JsonProp $Obj 'indirect_dispatch_marked_resolved') -eq $true -and $resTargets -le 0) -or ((Get-JsonProp $Obj 'virtual_dispatch_used') -eq $true -and (Get-JsonProp $Obj 'vtable_rva_pinned') -ne $true)) {
        $Violations.Add("Semantic rule 'unresolved-indirect-dispatch' (#182) [$SubjectId]: marks indirect call/branch resolved with 0 concrete target RVAs.")
    }

    # Rule 12: exception-unwind-broken
    if (((Get-JsonProp $Obj 'modifies_stack_without_unwind') -eq $true) -or ((Get-JsonProp $Obj 'seh_frame_present') -eq $true -and (Get-JsonProp $Obj 'unwind_semantics_verified') -ne $true)) {
        $Violations.Add("Semantic rule 'exception-unwind-broken' (#182) [$SubjectId]: modifies RSP without matching .pdata UNWIND_INFO or trampoline unwind callback.")
    }

    # Rule 13: thread-affinity-violation
    if ((Get-JsonProp $Obj 'off_main_thread_ui_mutation') -eq $true) {
        $Violations.Add("Semantic rule 'thread-affinity-violation' (#182) [$SubjectId]: mutates K2 UI/WndProc/CConsole state off the main window thread.")
    }

    # Rule 14: incomplete-rollback-bytes
    if ((Get-JsonProp $Obj 'rollback_bytes_incomplete') -eq $true) {
        $Violations.Add("Semantic rule 'incomplete-rollback-bytes' (#182) [$SubjectId]: patch site lacks complete originalBytes snapshot for deterministic RAII rollback.")
    }
}

function Test-DossierSemanticReadiness {
    # satisfies: #182 (rea-14-rule-semantic-readiness-checker)
    param([object]$Dossier)
    $violations = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Dossier) { return $violations.ToArray() }

    Invoke-EvaluateSemanticSubject -Obj $Dossier -SubjectId "root" -Violations $violations

    $semClaims = Get-JsonProp $Dossier 'semantic_claims'
    if ($null -ne $semClaims) {
        if ($semClaims -is [System.Collections.IEnumerable] -and -not ($semClaims -is [string]) -and -not ($semClaims -is [PSCustomObject])) {
            $idx = 0
            foreach ($claim in @($semClaims)) {
                $claimId = Get-JsonProp $claim 'id'
                $cid = if ($claimId) { [string]$claimId } else { "claim_$idx" }
                Invoke-EvaluateSemanticSubject -Obj $claim -SubjectId $cid -Violations $violations
                $idx++
            }
        } else {
            Invoke-EvaluateSemanticSubject -Obj $semClaims -SubjectId "semantic_claims" -Violations $violations
        }
    }

    $dossierObls = Get-JsonProp $Dossier 'obligations'
    if ($null -ne $dossierObls) {
        $idx = 0
        foreach ($obl in @($dossierObls)) {
            $oblIdProp = Get-JsonProp $obl 'id'
            $oid = if ($oblIdProp) { [string]$oblIdProp } else { "obl_$idx" }
            Invoke-EvaluateSemanticSubject -Obj $obl -SubjectId $oid -Violations $violations
            $idx++
        }
    }

    $contradictionList = @()
    $topContradictions = Get-JsonProp $Dossier 'contradictions'
    if ($null -ne $topContradictions) { $contradictionList += @($topContradictions) }
    $semContradictions = if ($null -ne $semClaims) { Get-JsonProp $semClaims 'contradictions' } else { $null }
    if ($null -ne $semContradictions) {
        $contradictionList += @($semContradictions)
    }
    foreach ($c in $contradictionList) {
        if ($null -eq $c) { continue }
        $cIdProp = Get-JsonProp $c 'id'
        $cid = if ($cIdProp) { [string]$cIdProp } else { "unnamed" }
        Invoke-EvaluateSemanticSubject -Obj $c -SubjectId $cid -Violations $violations
    }

    return $violations.ToArray()
}

function Resolve-ObligationDag {
    # satisfies: #114 (obligation-dag-and-patch-lifecycle), #181 (rea-rfc8785-closure-digest-and-authority-gate)
    param([array]$Obligations, [object]$Dossier = $null)
    $byId = @{}
    $deps = @{}
    $orderedIds = New-Object System.Collections.Generic.List[string]
    $ownedRegions = @{}
    $overlappingOwnership = New-Object System.Collections.Generic.List[string]
    $staticOnlyBlocked = New-Object System.Collections.Generic.List[string]
    $dossierAuthScopeProp = if ($null -ne $Dossier) { Get-JsonProp $Dossier 'authority_scope' } else { $null }
    $dossierAuthScope = if ($null -ne $dossierAuthScopeProp) { ([string]$dossierAuthScopeProp).Trim().ToLowerInvariant() } else { "" }

    foreach ($o in $Obligations) {
        $oIdProp = Get-JsonProp $o 'id'
        if ($null -eq $o -or [string]::IsNullOrWhiteSpace([string]$oIdProp)) { continue }
        $oid = ([string]$oIdProp).Trim()
        $byId[$oid] = $o
        $orderedIds.Add($oid)
        $bList = New-Object System.Collections.Generic.List[string]
        $oBlockedBy = Get-JsonProp $o 'blocked_by'
        if ($null -ne $oBlockedBy) {
            foreach ($b in @($oBlockedBy)) {
                $bs = ([string]$b).Trim()
                if (-not [string]::IsNullOrWhiteSpace($bs)) { $bList.Add($bs) }
            }
        }
        $oDependsOn = Get-JsonProp $o 'depends_on'
        if ($null -ne $oDependsOn) {
            foreach ($b in @($oDependsOn)) {
                $bs = ([string]$b).Trim()
                if (-not [string]::IsNullOrWhiteSpace($bs) -and -not $bList.Contains($bs)) { $bList.Add($bs) }
            }
        }
        $deps[$oid] = $bList

        $oOwnedReg = Get-JsonProp $o 'owned_region'
        if (-not [string]::IsNullOrWhiteSpace([string]$oOwnedReg)) {
            $reg = ([string]$oOwnedReg).Trim()
            if ($ownedRegions.ContainsKey($reg)) {
                $overlappingOwnership.Add("$($ownedRegions[$reg])<->$oid($reg)")
            } else {
                $ownedRegions[$reg] = $oid
            }
        }

        $oEvTier = Get-JsonProp $o 'evidence_tier'
        $oEvSrc = Get-JsonProp $o 'evidence_source'
        $oAuthScope = Get-JsonProp $o 'authority_scope'
        $evTier = if ($null -ne $oEvTier) { ([string]$oEvTier).Trim().ToLowerInvariant() } else { "" }
        $evSrc = if ($null -ne $oEvSrc) { ([string]$oEvSrc).Trim().ToLowerInvariant() } else { "" }
        $oblScope = if ($null -ne $oAuthScope) { ([string]$oAuthScope).Trim().ToLowerInvariant() } else { $dossierAuthScope }
        $stLower = ([string](Get-JsonProp $o 'status')).Trim().ToLowerInvariant()
        $reqRuntime = ((Get-JsonProp $o 'requires_runtime_authority') -eq $true) -or ((Get-JsonProp $o 'requires_dynamic_confirmation') -eq $true) -or ($oblScope -in @("server_authoritative", "shared_simulation_desync_risk"))
        if (($evTier -eq "static_only" -or $evSrc -eq "static_only") -and $reqRuntime -and $stLower -eq "satisfied") {
            $staticOnlyBlocked.Add($oid)
        }
    }

    # Merge top-level dependency_graph and ownership_graph edges (#181)
    if ($null -ne $Dossier) {
        foreach ($graphProp in @("dependency_graph", "ownership_graph")) {
            $gObj = Get-JsonProp $Dossier $graphProp
            if ($null -eq $gObj) { continue }
            $gEdges = Get-JsonProp $gObj 'edges'
            if ($null -ne $gEdges) {
                foreach ($edge in @($gEdges)) {
                    $fromId = ([string](Get-JsonProp $edge 'from')).Trim()
                    $toId = ([string](Get-JsonProp $edge 'to')).Trim()
                    if ([string]::IsNullOrWhiteSpace($fromId) -or [string]::IsNullOrWhiteSpace($toId)) { continue }
                    if (-not $deps.ContainsKey($fromId)) {
                        $deps[$fromId] = New-Object System.Collections.Generic.List[string]
                        if (-not $byId.ContainsKey($fromId)) {
                            $byId[$fromId] = [PSCustomObject]@{ id = $fromId; status = "pending" }
                            $orderedIds.Add($fromId)
                        }
                    }
                    if (-not $deps[$fromId].Contains($toId)) { $deps[$fromId].Add($toId) }
                }
            } else {
                foreach ($p in $gObj.PSObject.Properties) {
                    if ($p.Name -eq "edges") { continue }
                    $fromId = ([string]$p.Name).Trim()
                    if (-not $deps.ContainsKey($fromId)) {
                        $deps[$fromId] = New-Object System.Collections.Generic.List[string]
                        if (-not $byId.ContainsKey($fromId)) {
                            $byId[$fromId] = [PSCustomObject]@{ id = $fromId; status = "pending" }
                            $orderedIds.Add($fromId)
                        }
                    }
                    foreach ($target in @($p.Value)) {
                        $toId = ([string]$target).Trim()
                        if (-not [string]::IsNullOrWhiteSpace($toId) -and -not $deps[$fromId].Contains($toId)) {
                            $deps[$fromId].Add($toId)
                        }
                    }
                }
            }
        }
    }

    $unknownDeps = New-Object System.Collections.Generic.List[string]
    $satisfiedDependsOnOpen = New-Object System.Collections.Generic.List[string]
    foreach ($oid in $orderedIds) {
        $oblStatus = ([string](Get-JsonProp $byId[$oid] 'status')).Trim().ToLowerInvariant()
        foreach ($depId in $deps[$oid]) {
            if (-not $byId.ContainsKey($depId)) {
                $unknownDeps.Add("$oid->$depId")
            } else {
                $depStatus = ([string](Get-JsonProp $byId[$depId] 'status')).Trim().ToLowerInvariant()
                if ($oblStatus -eq "satisfied" -and $depStatus -in @("pending", "open", "contradicted", "unresolved")) {
                    $satisfiedDependsOnOpen.Add("$oid->$depId($depStatus)")
                }
            }
        }
    }

    $state = @{}
    foreach ($oid in $orderedIds) { $state[$oid] = 0 }
    $topo = New-Object System.Collections.Generic.List[string]
    $cycleDesc = ""

    function Invoke-DagDfs([string]$node, [string[]]$stack) {
        $state[$node] = 1
        foreach ($depId in $deps[$node]) {
            if (-not $state.ContainsKey($depId)) { continue }
            if ($state[$depId] -eq 1) {
                $script:cycleFound = ($stack + @($depId)) -join " -> "
                return $true
            }
            if ($state[$depId] -eq 0) {
                if (Invoke-DagDfs $depId ($stack + @($depId))) { return $true }
            }
        }
        $state[$node] = 2
        $topo.Add($node)
        return $false
    }

    $script:cycleFound = ""
    $hasCycle = $false
    foreach ($oid in $orderedIds) {
        if ($state[$oid] -eq 0) {
            if (Invoke-DagDfs $oid @($oid)) {
                $hasCycle = $true
                $cycleDesc = $script:cycleFound
                break
            }
        }
    }

    $readyNext = New-Object System.Collections.Generic.List[string]
    if (-not $hasCycle -and $unknownDeps.Count -eq 0) {
        foreach ($oid in $topo) {
            $obl = $byId[$oid]
            if (([string](Get-JsonProp $obl 'status')).ToLowerInvariant() -ne "pending") { continue }
            $allClear = $true
            foreach ($b in $deps[$oid]) {
                if ($byId.ContainsKey($b)) {
                    $st = ([string](Get-JsonProp $byId[$b] 'status')).ToLowerInvariant()
                    if ($st -notin @("satisfied", "waived", "tombstoned")) {
                        $allClear = $false
                        break
                    }
                }
            }
            if ($allClear) { $readyNext.Add($oid) }
        }
    }

    return [PSCustomObject]@{
        HasCycle               = $hasCycle
        CycleDescription       = $cycleDesc
        UnknownDependencies    = $unknownDeps.ToArray()
        SatisfiedDependsOnOpen = $satisfiedDependsOnOpen.ToArray()
        OverlappingOwnership   = $overlappingOwnership.ToArray()
        BlockedByStaticOnly    = $staticOnlyBlocked.ToArray()
        TopologicalOrder       = $topo.ToArray()
        ReadyNext              = $readyNext.ToArray()
    }
}

if (-not (Test-Path $evidenceDir)) {
    Write-AuditLog "Evidence directory does not exist at $evidenceDir. No dossiers to audit." "INFO"
    exit 0
}

# Resolve candidate dossiers
$dossiersToAudit = @()

if ($Dossier) {
    $resolvedDossier = if ([System.IO.Path]::IsPathRooted($Dossier)) { $Dossier } else { Join-Path $repoRoot $Dossier }
    if (-not (Test-Path $resolvedDossier)) {
        Write-AuditLog "Specified dossier not found: $resolvedDossier" "ERROR"
        Write-Host "Action: Check that the file path is correct or run with -All to scan all dossiers." -ForegroundColor Yellow
        exit 1
    }
    $itemInfo = Get-Item -LiteralPath $resolvedDossier
    if ($itemInfo.PSIsContainer) {
        $dossiersToAudit += @(Get-ChildItem -LiteralPath $resolvedDossier -Filter "*.json" -File | Where-Object {
            $_.Name -notlike "_*" -and $_.Name -notin @("symbol_map.json", "pdb_synthesis_manifest.json", "coverage.json", "live_probe_queue.json")
        })
    } else {
        $dossiersToAudit += $itemInfo
    }
} elseif ($Files -and $Files.Count -gt 0) {
    Write-AuditLog "Filtering dossiers that map to $(($Files).Count) candidate modified files..." "INFO"
    $allCandidateFiles = Get-ChildItem -Path $evidenceDir -Recurse -Filter "*.json" | Where-Object {
        $_.Name -notlike "_*" -and $_.Name -notin @("symbol_map.json", "pdb_synthesis_manifest.json", "coverage.json", "live_probe_queue.json")
    }
    foreach ($candidate in $allCandidateFiles) {
        try {
            $candDossierObj = Get-Content -Raw -Encoding UTF8 -Path $candidate.FullName | ConvertFrom-Json
            $candCleanroom = Get-JsonProp $candDossierObj 'cleanroom_mapping'
            $sourceFile = if ($null -ne $candCleanroom) { Get-JsonProp $candCleanroom 'source_file' } else { $null }
            $candObls = Get-JsonProp $candDossierObj 'obligations'
            $satisfiedFiles = if ($null -ne $candObls) { @($candObls | ForEach-Object { Get-JsonProp $_ 'satisfied_in' } | Where-Object { $_ }) } else { @() }
            
            $matched = $false
            foreach ($f in $Files) {
                $normF = ($f -replace '[\\/]', '/').Trim('/')
                if ($sourceFile -and (($sourceFile -replace '[\\/]', '/').Trim('/') -eq $normF)) {
                    $matched = $true
                    break
                }
                foreach ($sf in $satisfiedFiles) {
                    if (($sf -replace '[\\/]', '/').Trim('/') -eq $normF) {
                        $matched = $true
                        break
                    }
                }
                if ($matched) { break }
            }
            if ($matched) {
                $dossiersToAudit += $candidate
            }
        } catch {
            Write-AuditLog "Error parsing $($candidate.FullName): $_" "WARN"
        }
    }
    if ($dossiersToAudit.Count -eq 0) {
        Write-AuditLog "None of the modified files map to an active Evidence Dossier. Clean pass." "SUCCESS"
        exit 0
    }
} else {
    # Default to all non-template dossiers
    $dossiersToAudit = Get-ChildItem -Path $evidenceDir -Recurse -Filter "*.json" | Where-Object {
        $_.Name -notlike "_*" -and $_.Name -notin @("symbol_map.json", "pdb_synthesis_manifest.json", "coverage.json", "live_probe_queue.json")
    }
}

if ($dossiersToAudit.Count -eq 0) {
    Write-AuditLog "No active evidence dossiers found to audit." "INFO"
    exit 0
}

Write-AuditLog "Starting Reconstruction Obligation Audit across $($dossiersToAudit.Count) dossier(s)..." "INFO"

$totalDossiers = 0
$failedDossiers = 0
$totalObligations = 0
$satisfiedObligations = 0
$pendingObligations = 0
$waivedObligations = 0
$auditErrors = @()

$nextQueueOutput = New-Object System.Collections.Generic.List[object]
$script:rvaCoverageEntries = New-Object System.Collections.Generic.List[object]

foreach ($dossierFile in $dossiersToAudit) {
    $totalDossiers++
    $relPath = $dossierFile.FullName.Replace($repoRoot, "").TrimStart('\', '/')
    Write-AuditLog "Auditing dossier: $relPath" "INFO"

    $dossierErrors = @()

    $canonCheck = Test-CanonicalRepoPath -CandidatePath $dossierFile.FullName
    if (-not $canonCheck.Valid) {
        $auditErrors += "[$relPath] $($canonCheck.Reason)"
        $failedDossiers++
        continue
    }

    try {
        $rawContent = Get-Content -Raw -Encoding UTF8 -Path $dossierFile.FullName
        $dupKeys = @(Test-DuplicateJsonTopKeys -RawJson $rawContent)
        if ($dupKeys.Count -gt 0) {
            $dossierErrors += "Duplicate JSON key(s) detected (#40): $($dupKeys -join ', ')"
        }
        $d = ConvertFrom-Json $rawContent
    } catch {
        $auditErrors += "[$relPath] Failed to parse JSON: $_"
        $failedDossiers++
        continue
    }

    # 1. Structural & Epistemic Validation (#35)
    $dSchemaVersion = Get-JsonProp $d 'schema_version'
    $dDossierId = Get-JsonProp $d 'dossier_id'
    $dTarget = Get-JsonProp $d 'target'
    $dTargetBin = if ($null -ne $dTarget) { Get-JsonProp $dTarget 'binary_name' } else { $null }
    $dTargetRva = if ($null -ne $dTarget) { Get-JsonProp $dTarget 'rva' } else { $null }
    $dTargetFn = if ($null -ne $dTarget) { Get-JsonProp $dTarget 'function_name' } else { $null }
    $dTargetInsnSha = if ($null -ne $dTarget) { Get-JsonProp $dTarget 'instruction_bytes_sha256' } else { $null }
    $dObligations = Get-JsonProp $d 'obligations'
    $dLimitations = Get-JsonProp $d 'limitations_and_unknowns'
    $dAuthScope = Get-JsonProp $d 'authority_scope'
    $dStatus = Get-JsonProp $d 'status'
    $dCounterEvidence = Get-JsonProp $d 'counterevidence_searched'
    $dNegControl = Get-JsonProp $d 'negative_control'
    $dNotes = Get-JsonProp $d 'notes'

    if (-not $dSchemaVersion) {
        $dossierErrors += "Missing 'schema_version' (expected '2.0.0'). Run upgrade."
    }
    if (-not $dDossierId) {
        $dossierErrors += "Missing 'dossier_id'."
    }
    if (-not $dTarget -or -not $dTargetBin -or -not $dTargetRva) {
        $dossierErrors += "Missing 'target.binary_name' or 'target.rva'."
    }
    if ($null -eq $dObligations) {
        $dossierErrors += "Missing 'obligations' array."
    }
    if ($null -eq $dLimitations) {
        $dossierErrors += "Missing 'limitations_and_unknowns' block."
    }

    $validAuthScopes = @("server_authoritative", "client_cosmetic_only", "shared_simulation_desync_risk", "native_guard_fma")
    if ($null -ne $dAuthScope -and [string]$dAuthScope -notin $validAuthScopes) {
        $dossierErrors += "Invalid 'authority_scope' '$($dAuthScope)' (#35). Expected one of: $($validAuthScopes -join ', ')."
    } elseif ($StrictEpistemic -and [string]::IsNullOrWhiteSpace([string]$dAuthScope)) {
        $dossierErrors += "StrictEpistemic (#35): Missing 'authority_scope'."
    }
    if ($StrictEpistemic) {
        if ([string]::IsNullOrWhiteSpace([string]$dCounterEvidence)) {
            $dossierErrors += "StrictEpistemic (#35): Missing non-empty 'counterevidence_searched'."
        }
        if ([string]::IsNullOrWhiteSpace([string]$dNegControl)) {
            $dossierErrors += "StrictEpistemic (#35): Missing non-empty 'negative_control'."
        }
    }
    if ([string]$dStatus -eq "tombstoned" -and [string]::IsNullOrWhiteSpace([string]$dCounterEvidence) -and [string]::IsNullOrWhiteSpace([string]$dNotes)) {
        $dossierErrors += "Status 'tombstoned' (#35) requires 'counterevidence_searched' or 'notes' documenting why the hypothesis was falsified."
    }

    $dTopInsnSha = Get-JsonProp $d 'instruction_bytes_sha256'
    $targetInsnSha = if ($dTargetInsnSha) {
        ([string]$dTargetInsnSha).Trim().ToLowerInvariant()
    } elseif ($dTopInsnSha) {
        ([string]$dTopInsnSha).Trim().ToLowerInvariant()
    } else { "" }
    $placeholderPattern = '^\s*(?:TODO|TBD|FIXME|XXX|N/?A|<[^>]+>|\?+)\s*$'
    $earlyReturnPattern = '(?m)^\s*if\s*\(\s*(?:!\s*[A-Za-z0-9_>-]+|[A-Za-z0-9_>-]+\s*==\s*(?:NULL|nullptr|0))\s*\)\s*(?:\{\s*)?return(?:\s+[0-9A-Za-z_]+)?\s*;\s*(?:\}\s*)?$'

    # Top-level / patch_meta Crash-Masking Detector (#45)
    $dPatchMeta = Get-JsonProp $d 'patch_meta'
    $docPatchObj = if ($null -ne $dPatchMeta) { $dPatchMeta } else { $d }
    $docPatchStrategy = ([string](Get-JsonProp $docPatchObj 'patch_strategy')).Trim().ToLowerInvariant()
    $docSnippetProp = Get-JsonProp $docPatchObj 'patch_snippet'
    $docSummaryProp = Get-JsonProp $docPatchObj 'patch_summary'
    $docPatchSnippet = if ($docSnippetProp) { ([string]$docSnippetProp).Trim() } elseif ($docSummaryProp) { ([string]$docSummaryProp).Trim() } else { "" }
    if ($docPatchStrategy -in @("early_return_noop", "null_guard_early_return", "bailout_noop") -or ($docPatchSnippet -and $docPatchSnippet -match $earlyReturnPattern)) {
        $docPostInv = ([string](Get-JsonProp $docPatchObj 'post_patch_state_invariant')).Trim()
        $docNegCtrl = ([string](Get-JsonProp $docPatchObj 'negative_control_crash_input')).Trim()
        if ([string]::IsNullOrWhiteSpace($docPostInv) -or $docPostInv -match $placeholderPattern -or [string]::IsNullOrWhiteSpace($docNegCtrl) -or $docNegCtrl -match $placeholderPattern) {
            $dossierErrors += "Crash-masking detector (#45): early-return null guard requires non-empty 'post_patch_state_invariant' and 'negative_control_crash_input'."
        }
    }

    # 2. Obligation Ledger Verification (#45, #91, #114, #181)
    if ($dObligations) {
        $dagInfo = Resolve-ObligationDag -Obligations @($dObligations) -Dossier $d
        if ($dagInfo.HasCycle) {
            $dossierErrors += "Obligation blocked_by dependency cycle detected (#114): $($dagInfo.CycleDescription)"
        }
        if ($dagInfo.UnknownDependencies.Count -gt 0) {
            $dossierErrors += "Obligation blocked_by references unknown ID(s) (#114): $($dagInfo.UnknownDependencies -join ', ')"
        }
        if ($dagInfo.SatisfiedDependsOnOpen.Count -gt 0) {
            $dossierErrors += "Obligation integrity 'satisfied-depends-on-open' (#181): satisfied obligation depends on open/pending/contradicted obligation(s): $($dagInfo.SatisfiedDependsOnOpen -join ', ')"
        }
        if ($dagInfo.OverlappingOwnership.Count -gt 0) {
            $dossierErrors += "Obligation integrity 'overlapping-ownership' (#181): conflicting owned_region claims: $($dagInfo.OverlappingOwnership -join ', ')"
        }
        if ($dagInfo.BlockedByStaticOnly.Count -gt 0) {
            $dossierErrors += "Authority gate 'blocked-by-static-only-claims' (#181): runtime-authoritative obligation(s) backed only by static claims: $($dagInfo.BlockedByStaticOnly -join ', ')"
        }

        if ($Action -eq "next") {
            $nextQueueOutput.Add([ordered]@{
                dossier_id        = [string]$dDossierId
                file              = $relPath
                has_cycle         = $dagInfo.HasCycle
                topological_order = $dagInfo.TopologicalOrder
                ready_next        = $dagInfo.ReadyNext
            })
        }

        foreach ($obl in $dObligations) {
            $totalObligations++
            $oblId = Get-JsonProp $obl 'id'
            if (-not $oblId) {
                $dossierErrors += "Obligation missing 'id'."
                continue
            }

            $oblStatus = [string](Get-JsonProp $obl 'status')
            $oblDesc = Get-JsonProp $obl 'description'
            $oblRationale = Get-JsonProp $obl 'rationale'
            $oblWaiverReason = Get-JsonProp $obl 'waiver_reason'
            $oblSatisfiedIn = Get-JsonProp $obl 'satisfied_in'
            $oblVerifiedByTest = Get-JsonProp $obl 'verified_by_test'

            # Specificity Floor (#114)
            $descStr = [string]$oblDesc
            $ratStr = if ($oblRationale) { [string]$oblRationale } elseif ($oblWaiverReason) { [string]$oblWaiverReason } else { "" }
            if ($descStr -match $placeholderPattern -or ($oblStatus -eq "waived" -and $ratStr -match $placeholderPattern)) {
                $dossierErrors += "[$oblId] Specificity floor violation (#114): placeholder description/rationale '$($descStr + $ratStr)' is forbidden."
            }

            # Crash-Masking Detector (#45)
            $patchStrategy = ([string](Get-JsonProp $obl 'patch_strategy')).Trim().ToLowerInvariant()
            $patchSnippet = ([string](Get-JsonProp $obl 'patch_snippet')).Trim()
            if ($patchStrategy -in @("early_return_noop", "null_guard_early_return", "bailout_noop") -or ($patchSnippet -and $patchSnippet -match $earlyReturnPattern)) {
                $postInv = ([string](Get-JsonProp $obl 'post_patch_state_invariant')).Trim()
                $negCtrl = ([string](Get-JsonProp $obl 'negative_control_crash_input')).Trim()
                if ([string]::IsNullOrWhiteSpace($postInv) -or $postInv -match $placeholderPattern -or [string]::IsNullOrWhiteSpace($negCtrl) -or $negCtrl -match $placeholderPattern) {
                    $dossierErrors += "[$oblId] Crash-masking detector (#45): early-return null guard requires non-empty 'post_patch_state_invariant' and 'negative_control_crash_input'."
                }
            }

            switch ($oblStatus) {
                "satisfied" {
                    $satisfiedObligations++
                    # Verify satisfied_in
                    if (-not $oblSatisfiedIn) {
                        $dossierErrors += "[$oblId] Status is 'satisfied' but 'satisfied_in' is missing."
                    } else {
                        $satPath = if ([System.IO.Path]::IsPathRooted($oblSatisfiedIn)) { $oblSatisfiedIn } else { Join-Path $repoRoot $oblSatisfiedIn }
                        if (-not (Test-Path $satPath)) {
                            $dossierErrors += "[$oblId] Implementation file not found: $oblSatisfiedIn"
                        } else {
                            # If implementation file is code (not json/md), verify obligation marker exists
                            $ext = [System.IO.Path]::GetExtension($satPath).ToLower()
                            if ($ext -notin @(".json", ".md", ".txt")) {
                                $matches = Select-String -Path $satPath -Pattern $oblId -SimpleMatch -Quiet
                                if (-not $matches) {
                                    $dossierErrors += "[$oblId] File '$oblSatisfiedIn' does not contain obligation tag '// satisfies: $oblId'."
                                }
                            }
                        }
                    }

                    # Verify test
                    if ($oblVerifiedByTest) {
                        $testPath = if ([System.IO.Path]::IsPathRooted($oblVerifiedByTest)) { $oblVerifiedByTest } else { Join-Path $repoRoot $oblVerifiedByTest }
                        if (-not (Test-Path $testPath)) {
                            $dossierErrors += "[$oblId] Test file not found: $oblVerifiedByTest"
                        }
                    }
                }
                "pending" {
                    $dCleanMap = Get-JsonProp $d 'cleanroom_mapping'
                    $dCleanSrc = if ($null -ne $dCleanMap) { Get-JsonProp $dCleanMap 'source_file' } else { $null }
                    $candidateSource = if ($oblSatisfiedIn) { $oblSatisfiedIn } elseif ($dCleanSrc) { $dCleanSrc } else { $null }
                    $isFixed = $false
                    if ($candidateSource) {
                        $candFull = if ([System.IO.Path]::IsPathRooted($candidateSource)) { $candidateSource } else { Join-Path $repoRoot $candidateSource }
                        if (Test-Path $candFull) {
                            $isFixed = [bool](Select-String -Path $candFull -Pattern $oblId -SimpleMatch -Quiet)
                        }
                    }
                    if ($isFixed) {
                        Write-AuditLog "[FIXED] [$oblId] Previously pending obligation is now satisfied in '$candidateSource'!" "SUCCESS"
                        if ($Interactive) {
                            $obl.status = "satisfied"
                            if (-not $oblSatisfiedIn) {
                                $obl | Add-Member -NotePropertyName "satisfied_in" -NotePropertyValue $candidateSource -Force
                            }
                            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
                            [System.IO.File]::WriteAllText($dossierFile.FullName, ($d | ConvertTo-Json -Depth 10), $utf8NoBom)
                            $satisfiedObligations++
                            Write-AuditLog "  -> Promoted [$oblId] to 'satisfied' via -Interactive (-i)." "SUCCESS"
                        } else {
                            $pendingObligations++
                            $dossierErrors += "[FIXED] [$oblId] Obligation is satisfied in code; run with -Interactive (-i) to promote from 'pending' to 'satisfied'."
                        }
                    } else {
                        $pendingObligations++
                        $dossierErrors += "[$oblId] Status is 'pending': '$oblDesc' is not yet satisfied."
                    }
                }
                "contradicted" {
                    $pendingObligations++
                    $dossierErrors += "[$oblId] Status is 'contradicted' (#181): obligation is contradicted by evidence and cannot be closed."
                }
                "open" {
                    $pendingObligations++
                    $dossierErrors += "[$oblId] Status is 'open' (#181): '$oblDesc' is not yet satisfied."
                }
                "unresolved" {
                    $pendingObligations++
                    $dossierErrors += "[$oblId] Status is 'unresolved' (#181): '$oblDesc' is not yet satisfied."
                }
                "waived" {
                    $waivedObligations++
                    if ([string]::IsNullOrWhiteSpace($ratStr)) {
                        $dossierErrors += "[$oblId] Status is 'waived' but 'rationale' is empty. State why this requirement can be safely bypassed."
                    }
                    # Cryptographic Waiver Pinning (#91)
                    $oblApprovedSha = Get-JsonProp $obl 'approved_instruction_sha256'
                    $oblInsnSha = Get-JsonProp $obl 'instruction_bytes_sha256'
                    $approvedSha = if ($null -ne $oblApprovedSha) { ([string]$oblApprovedSha).Trim().ToLowerInvariant() } else { "" }
                    $expectedSha = if ($null -ne $oblInsnSha) { ([string]$oblInsnSha).Trim().ToLowerInvariant() } else { $targetInsnSha }
                    if ($approvedSha -and $expectedSha -and ($approvedSha -ne $expectedSha)) {
                        $dossierErrors += "[$oblId] stale_waiver (#91): approved_instruction_sha256 '$approvedSha' does not match instruction_bytes_sha256 '$expectedSha'."
                    } elseif ($null -ne $obl.PSObject.Properties["approved_instruction_sha256"] -and [string]::IsNullOrWhiteSpace($approvedSha) -and $expectedSha) {
                        $dossierErrors += "[$oblId] stale_waiver (#91): approved_instruction_sha256 is empty while instruction_bytes_sha256 is defined."
                    }
                }
                "tombstoned" {
                    $waivedObligations++
                }
                default {
                    $dossierErrors += "[$oblId] Invalid status '$oblStatus'. Expected 'satisfied', 'pending', 'contradicted', 'waived', or 'tombstoned'."
                }
            }
        }
    }

    # 3. Limitations, Unknowns, and Triangulation Verification
    if ($dLimitations) {
        $lu = $dLimitations
        $triStatus = Get-JsonProp $lu 'triangulation_status'

        if ($Strict -and $triStatus -ne "dynamically_confirmed") {
            $dossierErrors += "Strict mode: Triangulation status is '$triStatus' (expected 'dynamically_confirmed'). Verify dynamically with Frida/WinDbg."
        }

        $luUnknownOffsets = Get-JsonProp $lu 'unknown_offsets'
        if ($luUnknownOffsets) {
            foreach ($uo in $luUnknownOffsets) {
                $uoStatus = Get-JsonProp $uo 'status'
                $uoOffset = Get-JsonProp $uo 'offset'
                $uoDesc = Get-JsonProp $uo 'description'
                $uoRationale = Get-JsonProp $uo 'rationale'
                if ($uoStatus -eq "unverified") {
                    if ($Strict) {
                        $dossierErrors += "Strict mode: Unknown offset '$uoOffset' is unverified."
                    } else {
                        Write-AuditLog "[$relPath] Unknown offset '$uoOffset' is unverified ($uoDesc)." "WARN"
                    }
                } elseif ($uoStatus -eq "waived" -and [string]::IsNullOrWhiteSpace([string]$uoRationale)) {
                    $dossierErrors += "Unknown offset '$uoOffset' is waived but missing 'rationale'."
                }
            }
        }

        $luIndirectCalls = Get-JsonProp $lu 'indirect_calls'
        if ($luIndirectCalls) {
            foreach ($ic in $luIndirectCalls) {
                $icStatus = Get-JsonProp $ic 'status'
                $icRva = Get-JsonProp $ic 'rva'
                $icRationale = Get-JsonProp $ic 'rationale'
                if ($icStatus -eq "unverified") {
                    if ($Strict) {
                        $dossierErrors += "Strict mode: Indirect call at RVA '$icRva' is unverified."
                    } else {
                        Write-AuditLog "[$relPath] Indirect call at RVA '$icRva' is unverified." "WARN"
                    }
                } elseif ($icStatus -eq "waived" -and [string]::IsNullOrWhiteSpace([string]$icRationale)) {
                    $dossierErrors += "Indirect call at RVA '$icRva' is waived but missing 'rationale'."
                }
            }
        }
    }

    # 3b. Branch-to-RVA Coverage Audit (Option G: rz_annotated_code.h)
    $dBranchAnnotations = Get-JsonProp $d 'branch_annotations'
    if ($dBranchAnnotations) {
        $knownOblIds = @()
        if ($dObligations) {
            $knownOblIds = @($dObligations | ForEach-Object { [string](Get-JsonProp $_ 'id') })
        }
        foreach ($ba in $dBranchAnnotations) {
            $covObl = [string](Get-JsonProp $ba 'covered_by_obligation')
            if ([string]::IsNullOrWhiteSpace($covObl) -or ($knownOblIds -notcontains $covObl)) {
                $dossierErrors += "[Option G] Branch at RVA '$(Get-JsonProp $ba 'rva')' ($(Get-JsonProp $ba 'condition')) is missing a valid 'covered_by_obligation' in obligations ledger."
            }
        }
    }

    # 4. Decompiler Failure-Pattern & Capstone Prologue Hazard Scanner (Option W)
    $dDecompiledC = Get-JsonProp $d 'decompiled_c'
    if (-not [string]::IsNullOrWhiteSpace([string]$dDecompiledC)) {
        $decompText = [string]$dDecompiledC
        $artifactMatches = [regex]::Matches($decompText, '\b(in_RAX|in_RCX|in_RDX|in_R8|in_R9|extraout_[A-Za-z0-9_]+|unaff_[A-Za-z0-9_]+|CONCAT[0-9]{2}|SUB[0-9]{2}|ZEXT[0-9]{2}|SEXT[0-9]{2}|BADSPACEBASE|halt_baddata)\b')
        if ($artifactMatches.Count -gt 0) {
            $uniqueArtifacts = @($artifactMatches | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
            $oblSummaryText = ($dObligations | ConvertTo-Json -Compress -ErrorAction SilentlyContinue) + " " + ($dLimitations | ConvertTo-Json -Compress -ErrorAction SilentlyContinue)
            foreach ($art in $uniqueArtifacts) {
                if ($oblSummaryText -notmatch [regex]::Escape($art) -and $oblSummaryText -notmatch 'decompiler_artifact') {
                    $dossierErrors += "[Option W] Unresolved decompiler artifact '$art' in decompiled_c without matching obligation or limitations_and_unknowns entry."
                }
            }
        }
    }

    $dCapstone = Get-JsonProp $d 'capstone_prologue_analysis'
    $dRipHazards = if ($null -ne $dCapstone) { Get-JsonProp $dCapstone 'rip_relative_hazards' } else { $null }
    if ($dCapstone -and $dRipHazards -and @($dRipHazards).Count -gt 0) {
        $hasRipObl = $false
        if ($dObligations) {
            foreach ($o in $dObligations) {
                if ((Get-JsonProp $o 'id') -match 'OBL-(PROLOGUE-RIP-REL|HOOK-)') {
                    $hasRipObl = $true
                    break
                }
            }
        }
        if (-not $hasRipObl) {
            $dossierErrors += "[Option W] Capstone prologue analysis flagged RIP-relative hazard(s) in detour window without an OBL-PROLOGUE-RIP-REL obligation."
        }
    }

    # 5. RFC 8785 Canonical Closure Digest & Authority Gate (#181) + 14-Rule Semantic Readiness (#182)
    $computedClosureDigest = Get-Rfc8785ClosureDigestSha256 -DossierObject $d
    $dClosureDigest = Get-JsonProp $d 'closure_digest_sha256'
    if (-not [string]::IsNullOrWhiteSpace([string]$dClosureDigest)) {
        $declaredClosureDigest = ([string]$dClosureDigest).Trim().ToLowerInvariant()
        if ($declaredClosureDigest -ne $computedClosureDigest) {
            $dossierErrors += "Closure integrity 'closure-digest-mismatch' (#181): declared closure_digest_sha256 '$declaredClosureDigest' != RFC 8785 canonical digest '$computedClosureDigest'."
        }
    }
    if ((Get-JsonProp $d 'blocked_by_static_only_claims') -eq $true -and ([string]$dStatus -eq "promoted" -or (Get-JsonProp $d 'closure_ready') -eq $true)) {
        $dossierErrors += "Authority gate 'blocked-by-static-only-claims' (#181): dossier cannot be promoted or marked closure_ready while blocked_by_static_only_claims is true."
    }

    $semanticViolations = @(Test-DossierSemanticReadiness -Dossier $d)
    foreach ($sv in $semanticViolations) {
        $dossierErrors += $sv
    }

    if ($dTarget -and $dTargetBin -and $dTargetRva) {
        $script:rvaCoverageEntries.Add([ordered]@{
            dossier_id            = [string]$dDossierId
            binary_name           = [string]$dTargetBin
            rva                   = ([string]$dTargetRva).ToLowerInvariant()
            function_name         = [string]$dTargetFn
            status                = [string]$dStatus
            closure_digest_sha256 = $computedClosureDigest
            file                  = ($relPath -replace '\\', '/')
        })
    }

    if ($dossierErrors.Count -gt 0) {
        $failedDossiers++
        foreach ($err in $dossierErrors) {
            $auditErrors += "[$relPath] $err"
        }
    }
}

if ($EmitCoverage) {
    $covEntries = if ($null -ne $script:rvaCoverageEntries) { @($script:rvaCoverageEntries.ToArray()) } else { @() }
    $covPayload = [ordered]@{
        schema_version = "1.0.0"
        dossier_count  = $covEntries.Count
        entries        = $covEntries
    }
    $covOutPath = Join-Path $evidenceDir "coverage.json"
    [System.IO.File]::WriteAllText($covOutPath, ($covPayload | ConvertTo-Json -Depth 6) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-AuditLog "[COVERAGE-EMITTED] Wrote $($covEntries.Count) RVA coverage entries to $covOutPath (#43)." "SUCCESS"
}

if ($Action -eq "next") {
    $flatUnblocked = New-Object System.Collections.Generic.List[object]
    foreach ($nq in $nextQueueOutput) {
        foreach ($rId in @($nq.ready_next)) {
            $flatUnblocked.Add([ordered]@{
                dossier_id   = $nq.dossier_id
                obligationId = $rId
            })
        }
    }
    $nextSummary = [ordered]@{
        unblockedCount = $flatUnblocked.Count
        unblocked      = @($flatUnblocked.ToArray())
        dossiers       = @($nextQueueOutput.ToArray())
    }
    if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
        [System.IO.File]::WriteAllText($JsonOutput, ($nextSummary | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
    }
    if ($Json) {
        Write-Output ($nextSummary | ConvertTo-Json -Depth 6 -Compress)
    } else {
        foreach ($nq in $nextQueueOutput) {
            Write-Host "[DOSSIER-NEXT] dossier=$($nq.dossier_id) topo=[$($nq.topological_order -join ', ')] ready_next=[$($nq.ready_next -join ', ')]" -ForegroundColor Cyan
        }
    }
    if ($auditErrors | Where-Object { $_ -match 'dependency cycle|unknown ID' }) {
        foreach ($e in $auditErrors) { Write-Host "  * $e" -ForegroundColor Red }
        exit 1
    }
    exit 0
}

if ($Json) {
    $jsonOut = [ordered]@{
        status               = if ($auditErrors.Count -eq 0) { "SUCCESS" } else { "FAILED" }
        total_dossiers       = $totalDossiers
        passed_dossiers      = ($totalDossiers - $failedDossiers)
        failed_dossiers      = $failedDossiers
        total_obligations    = $totalObligations
        satisfied            = $satisfiedObligations
        pending              = $pendingObligations
        waived               = $waivedObligations
        errors               = @($auditErrors)
    }
    Write-Output ($jsonOut | ConvertTo-Json -Depth 6 -Compress)
    if ($auditErrors.Count -gt 0) { exit 1 } else { exit 0 }
}

# Final Report
Write-Host ""
Write-Host "==================== AUDIT SUMMARY ====================" -ForegroundColor Cyan
Write-Host "Total Dossiers Audited:  $totalDossiers"
Write-Host "Passed Dossiers:         $($totalDossiers - $failedDossiers)"
Write-Host "Failed Dossiers:         $failedDossiers"
Write-Host "Total Obligations:       $totalObligations"
Write-Host "  - Satisfied:           $satisfiedObligations" -ForegroundColor Green
Write-Host "  - Pending:             $pendingObligations" -ForegroundColor Yellow
Write-Host "  - Waived:              $waivedObligations" -ForegroundColor Gray
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host ""

if ($auditErrors.Count -gt 0) {
    Write-AuditLog "Obligation verification FAILED with $($auditErrors.Count) error(s):" "ERROR"
    foreach ($err in $auditErrors) {
        Write-Host "  * $err" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "Action Required:" -ForegroundColor Yellow
    Write-Host "  1. Satisfy pending obligations in cleanroom code and tag with '// satisfies: OBL-XX'." -ForegroundColor Yellow
    Write-Host "  2. For low-risk or irrelevant fields, set status to 'waived' and provide an explicit 'rationale'." -ForegroundColor Yellow
    Write-Host "  3. To bypass in an emergency, re-run with -SkipObligationCheck." -ForegroundColor Yellow
    exit 1
}

Write-AuditLog "[DOSSIER-OK] All obligations and unknowns successfully verified ($totalDossiers dossier(s), $satisfiedObligations satisfied, $waivedObligations waived, 0 stale waivers). Audit PASSED." "SUCCESS"
exit 0
