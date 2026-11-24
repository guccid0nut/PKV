// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Wave v4 Phase 15 (#181-#195: wave4-rea-epistemic-forensics-and-dual-oracle) core forensics engine.
/// Absorbs morluto/rea reverse-engineering intelligence primitives:
/// - #181 (Gem 1A): RFC 8785 canonical JSON closure_digest_sha256, ownership/dependency DAG verifier (satisfied-depends-on-open),
///                  blocked_by_static_only_claims authority gate, and 18-code obligation integrity verifier.
/// - #182 (Gem 1H): 14-rule semantic readiness checker (false-equivalence, unsafe-concurrency-semantics, etc.).
/// - #183 (Gem 1B): 5-state static-vs-runtime correlation matrix with side_alignment and content-addressed cor_/contra_ IDs.
/// - #185 (Gem 1C): Ghidra HighFunction P-Code def_use, parameter_uses, block_membership, and typed effects with omitted_operations_lower_bound.
/// - #186 (Gem 1G): 28-node / 16-edge typed native investigation DAG with 5 epistemic statuses.
/// - #187 (Gem 1I): 8-facet cleanroom equivalence comparator with auto-name and NOP-padding normalization.
/// - #191 (Gem 1K): Canonical crash minidump register envelope (r15..rax, rip, rsp, eflags) and stream completeness check.
/// - #192 (Gem 1L): Poly-base AddressCoordinate(space, offset) algebra preventing file/RVA/VA conflation.
/// - #193 (Gem 1M): Bounded 8 MiB PE .rsrc 3-level hierarchy &amp; Type-16/Type-24 manifest forensics parser.
/// - #194 (Gem 1R): COFF machine &amp; ReadyToRun (RTR_SIGNATURE = 0x00525452) OS-override XOR mask decoder within a 64 MiB budget.
/// </summary>
public static class ReaEpistemicForensicsEngine
{
    public const int MaxResourceSectionBytes = 8 * 1024 * 1024; // 8 MiB (#193)
    public const long MaxCoffWorkingMemoryBudgetBytes = 64L * 1024L * 1024L; // 64 MiB (#194)
    public const uint RtrSignature = 0x00525452U; // "RTR\0" (#194)

    // ========================================================================
    // #181 (Gem 1A): 18-Code Obligation Integrity Verifier & RFC 8785 Digest
    // ========================================================================
    public static readonly IReadOnlyList<string> ObligationIntegrityDiagnosticCodes = new string[]
    {
        "missing-schema-version",
        "missing-dossier-id",
        "missing-target-rva",
        "missing-obligations-array",
        "missing-limitations-block",
        "duplicate-json-key",
        "duplicate-obligation-id",
        "unknown-dependency",
        "dependency-cycle",
        "satisfied-depends-on-open",
        "static-only-authority-claim",
        "missing-satisfied-in",
        "missing-obligation-tag",
        "placeholder-specificity-violation",
        "crash-masking-unguarded",
        "stale-cryptographic-waiver",
        "closure-digest-mismatch",
        "unverified-branch-coverage"
    };

    // ========================================================================
    // #182 (Gem 1H): 14 Semantic Integrity Readiness Rules
    // ========================================================================
    public static readonly IReadOnlyList<string> SemanticReadinessRuleCodes = new string[]
    {
        "false-equivalence",
        "unsafe-concurrency-semantics",
        "divergence-not-localized",
        "contradiction-proof-incomplete",
        "unbounded-loop-effect",
        "aliasing-hazard-untracked",
        "calling-convention-drift",
        "unobserved-side-effect-claim",
        "floating-point-nan-hazard",
        "stale-oracle-snapshot",
        "unresolved-indirect-dispatch",
        "exception-unwind-broken",
        "thread-affinity-violation",
        "incomplete-rollback-bytes"
    };

    // ========================================================================
    // #186 (Gem 1G): 28-Node & 16-Edge Epistemic Investigation Graph Taxonomy
    // ========================================================================
    public static readonly IReadOnlyList<string> InvestigationNodeKinds = new string[]
    {
        "function",
        "dispatch_table",
        "dispatch_slot",
        "thunk",
        "state_value",
        "global_value",
        "field",
        "branch_condition",
        "sink",
        "call_site",
        "basic_block",
        "parameter",
        "return_value",
        "local_variable",
        "register_value",
        "constant_literal",
        "string_literal",
        "struct_type",
        "enum_type",
        "vtable_pointer",
        "import_symbol",
        "export_symbol",
        "section_region",
        "patch_site",
        "trampoline_stub",
        "exception_handler",
        "unwind_record",
        "evidence_artifact"
    };

    public static readonly IReadOnlyList<string> InvestigationEdgeKinds = new string[]
    {
        "calls",
        "dispatches_to",
        "reads_from",
        "writes_to",
        "branches_on",
        "flows_to",
        "aliases",
        "overrides",
        "contains_field",
        "indexes_slot",
        "thunks_to",
        "unwinds_via",
        "patches",
        "corroborates",
        "contradicts",
        "depends_on"
    };

    public static readonly IReadOnlyList<string> InvestigationEpistemicStatuses = new string[]
    {
        "observed",
        "inferred",
        "resolved",
        "ambiguous",
        "unresolved"
    };

    // ========================================================================
    // #187 (Gem 1I): 8-Facet Cleanroom Equivalence Comparator Taxonomy
    // ========================================================================
    public static readonly IReadOnlyList<string> CleanroomEquivalenceFacets = new string[]
    {
        "identity",
        "pseudocode",
        "assembly",
        "comments",
        "calls",
        "references",
        "strings_names",
        "cfg"
    };

    // ========================================================================
    // #191 (Gem 1K): Canonical x64 Register Order (r15..rax, rip, rsp, eflags)
    // ========================================================================
    public static readonly IReadOnlyList<string> CanonicalX64RegisterOrder = new string[]
    {
        "r15",
        "r14",
        "r13",
        "r12",
        "r11",
        "r10",
        "r9",
        "r8",
        "rdi",
        "rsi",
        "rbp",
        "rbx",
        "rdx",
        "rcx",
        "rax",
        "rip",
        "rsp",
        "eflags"
    };

    private static readonly Regex PlaceholderRegex = new Regex(
        @"^\s*(?:TODO|TBD|FIXME|XXX|N/?A|<[^>]+>|\?+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EarlyReturnNullGuardRegex = new Regex(
        @"^\s*if\s*\(\s*(?:!\s*[A-Za-z0-9_>-]+|[A-Za-z0-9_>-]+\s*==\s*(?:NULL|nullptr|0))\s*\)\s*(?:\{\s*)?return(?:\s+[0-9A-Za-z_]+)?\s*;\s*(?:\}\s*)?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex DecompilerAutoFuncRegex = new Regex(
        @"\b(?:FUN|sub|LAB|thunk_FUN)_[0-9a-fA-F]+\b",
        RegexOptions.Compiled);

    private static readonly Regex DecompilerAutoDataRegex = new Regex(
        @"\b(?:DAT|PTR|s|u)_[0-9a-fA-F]{4,}\b",
        RegexOptions.Compiled);

    private static readonly Regex DecompilerAutoVarRegex = new Regex(
        @"\b(?:[uiblpfc]u?Var\d+|local_[0-9a-fA-F]+|in_[A-Z0-9]+|extraout_[A-Za-z0-9_]+)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// #181 (Gem 1A): Serializes a <see cref="JsonElement"/> into RFC 8785 Canonical JSON
    /// (recursive ordinal key sorting, zero whitespace, canonical number/string escaping).
    /// </summary>
    // satisfies: #181
    public static string ComputeRfc8785CanonicalJson(JsonElement element, bool excludeClosureDigestProperty = false)
    {
        StringBuilder sb = new StringBuilder(256);
        WriteRfc8785CanonicalElement(element, sb, isRoot: true, excludeClosureDigestProperty: excludeClosureDigestProperty);
        return sb.ToString();
    }

    /// <summary>
    /// #181 (Gem 1A): Computes lowercase hex SHA-256 of the RFC 8785 canonical JSON representation.
    /// </summary>
    public static string ComputeClosureDigestSha256(JsonElement element, bool excludeSelfDigestField = true)
    {
        string canonicalJson = ComputeRfc8785CanonicalJson(element, excludeClosureDigestProperty: excludeSelfDigestField);
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(canonicalJson);
        byte[] hashBytes = SHA256.HashData(utf8Bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static void WriteRfc8785CanonicalElement(
        JsonElement element,
        StringBuilder sb,
        bool isRoot,
        bool excludeClosureDigestProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                List<JsonProperty> props = new List<JsonProperty>();
                foreach (JsonProperty p in element.EnumerateObject())
                {
                    if (isRoot && excludeClosureDigestProperty &&
                        string.Equals(p.Name, "closure_digest_sha256", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    props.Add(p);
                }

                props.Sort((JsonProperty a, JsonProperty b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                sb.Append('{');
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    WriteRfc8785EscapedString(props[i].Name, sb);
                    sb.Append(':');
                    WriteRfc8785CanonicalElement(props[i].Value, sb, isRoot: false, excludeClosureDigestProperty: false);
                }
                sb.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                sb.Append('[');
                bool first = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteRfc8785CanonicalElement(item, sb, isRoot: false, excludeClosureDigestProperty: false);
                }
                sb.Append(']');
                break;
            }
            case JsonValueKind.String:
            {
                WriteRfc8785EscapedString(element.GetString() ?? string.Empty, sb);
                break;
            }
            case JsonValueKind.Number:
            {
                if (element.TryGetInt64(out long longVal))
                {
                    sb.Append(longVal.ToString(CultureInfo.InvariantCulture));
                }
                else if (element.TryGetUInt64(out ulong ulongVal))
                {
                    sb.Append(ulongVal.ToString(CultureInfo.InvariantCulture));
                }
                else if (element.TryGetDouble(out double dblVal))
                {
                    if (dblVal == 0.0)
                    {
                        sb.Append('0');
                    }
                    else
                    {
                        sb.Append(dblVal.ToString("G17", CultureInfo.InvariantCulture));
                    }
                }
                else
                {
                    sb.Append(element.GetRawText().Trim());
                }
                break;
            }
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteRfc8785EscapedString(string value, StringBuilder sb)
    {
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// #181 (Gem 1A): Verifies Evidence Dossier v2 closure digest, ownership/dependency DAGs
    /// (catching satisfied-depends-on-open), static-only authority claims, and all 18 obligation integrity codes.
    /// </summary>
    public static ObligationClosureVerificationReport VerifyObligationClosure(
        JsonElement dossier,
        string? rawJson = null,
        Func<string, string?>? fileContentResolver = null)
    {
        List<ObligationIntegrityIssue> issues = new List<ObligationIntegrityIssue>();
        List<string> blockedByStaticOnlyClaims = new List<string>();
        List<string> satisfiedDependsOnOpenEdges = new List<string>();

        string computedClosureDigest = ComputeClosureDigestSha256(dossier, excludeSelfDigestField: true);
        string? declaredClosureDigest = null;
        if (dossier.ValueKind == JsonValueKind.Object &&
            dossier.TryGetProperty("closure_digest_sha256", out JsonElement cdEl) &&
            cdEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(cdEl.GetString()))
        {
            declaredClosureDigest = cdEl.GetString()!.Trim().ToLowerInvariant();
            if (!string.Equals(declaredClosureDigest, computedClosureDigest, StringComparison.Ordinal))
            {
                issues.Add(new ObligationIntegrityIssue(
                    "closure-digest-mismatch",
                    "dossier",
                    $"[REA-CLOSURE] Declared closure_digest_sha256 '{declaredClosureDigest}' does not match RFC 8785 digest '{computedClosureDigest}'."));
            }
        }

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            IReadOnlyList<string> dupKeys = DetectDuplicateJsonKeys(rawJson);
            if (dupKeys.Count > 0)
            {
                issues.Add(new ObligationIntegrityIssue(
                    "duplicate-json-key",
                    "dossier",
                    $"Duplicate JSON key(s) detected: {string.Join(", ", dupKeys)}"));
            }
        }

        if (dossier.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ObligationIntegrityIssue("missing-schema-version", "dossier", "Root dossier must be a JSON object."));
            return new ObligationClosureVerificationReport(
                IsClosedAndValid: false,
                ClosureDigestSha256: computedClosureDigest,
                DeclaredClosureDigestSha256: declaredClosureDigest,
                TopologicalOrder: Array.Empty<string>(),
                ReadyNextObligations: Array.Empty<string>(),
                BlockedByStaticOnlyClaims: blockedByStaticOnlyClaims,
                SatisfiedDependsOnOpenEdges: satisfiedDependsOnOpenEdges,
                Issues: issues);
        }

        if (!dossier.TryGetProperty("schema_version", out JsonElement svEl) || string.IsNullOrWhiteSpace(svEl.GetString()))
        {
            issues.Add(new ObligationIntegrityIssue("missing-schema-version", "dossier", "Missing 'schema_version'."));
        }

        if (!dossier.TryGetProperty("dossier_id", out JsonElement didEl) || string.IsNullOrWhiteSpace(didEl.GetString()))
        {
            issues.Add(new ObligationIntegrityIssue("missing-dossier-id", "dossier", "Missing 'dossier_id'."));
        }

        string targetInsnSha = string.Empty;
        if (!dossier.TryGetProperty("target", out JsonElement tgtEl) ||
            tgtEl.ValueKind != JsonValueKind.Object ||
            !tgtEl.TryGetProperty("binary_name", out JsonElement bnEl) ||
            string.IsNullOrWhiteSpace(bnEl.GetString()) ||
            !tgtEl.TryGetProperty("rva", out JsonElement rvaEl) ||
            string.IsNullOrWhiteSpace(rvaEl.GetString()))
        {
            issues.Add(new ObligationIntegrityIssue("missing-target-rva", "target", "Missing 'target.binary_name' or 'target.rva'."));
        }
        else if (tgtEl.TryGetProperty("instruction_bytes_sha256", out JsonElement tShaEl) && tShaEl.ValueKind == JsonValueKind.String)
        {
            targetInsnSha = (tShaEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
        }

        if (!dossier.TryGetProperty("limitations_and_unknowns", out JsonElement luEl) || luEl.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new ObligationIntegrityIssue("missing-limitations-block", "dossier", "Missing 'limitations_and_unknowns' block."));
        }

        string dossierAuthorityScope = dossier.TryGetProperty("authority_scope", out JsonElement asEl) && asEl.ValueKind == JsonValueKind.String
            ? (asEl.GetString() ?? string.Empty).Trim()
            : string.Empty;

        string dossierTriangulationStatus = string.Empty;
        if (dossier.TryGetProperty("limitations_and_unknowns", out JsonElement luObj) &&
            luObj.ValueKind == JsonValueKind.Object &&
            luObj.TryGetProperty("triangulation_status", out JsonElement tsEl) &&
            tsEl.ValueKind == JsonValueKind.String)
        {
            dossierTriangulationStatus = (tsEl.GetString() ?? string.Empty).Trim();
        }

        bool dossierRequiresDynamicAuthority =
            string.Equals(dossierAuthorityScope, "server_authoritative", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dossierAuthorityScope, "shared_simulation_desync_risk", StringComparison.OrdinalIgnoreCase);

        if (!dossier.TryGetProperty("obligations", out JsonElement oblsEl) || oblsEl.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ObligationIntegrityIssue("missing-obligations-array", "dossier", "Missing 'obligations' array."));
            return new ObligationClosureVerificationReport(
                IsClosedAndValid: false,
                ClosureDigestSha256: computedClosureDigest,
                DeclaredClosureDigestSha256: declaredClosureDigest,
                TopologicalOrder: Array.Empty<string>(),
                ReadyNextObligations: Array.Empty<string>(),
                BlockedByStaticOnlyClaims: blockedByStaticOnlyClaims,
                SatisfiedDependsOnOpenEdges: satisfiedDependsOnOpenEdges,
                Issues: issues);
        }

        Dictionary<string, string> statusById = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, List<string>> depsById = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> orderedIds = new List<string>();
        HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement obl in oblsEl.EnumerateArray())
        {
            if (obl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string oblId = obl.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? (idEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(oblId))
            {
                issues.Add(new ObligationIntegrityIssue("duplicate-obligation-id", "obligations", "Obligation missing 'id'."));
                continue;
            }

            if (!seenIds.Add(oblId))
            {
                issues.Add(new ObligationIntegrityIssue("duplicate-obligation-id", oblId, $"Duplicate obligation ID '{oblId}'."));
                continue;
            }

            string status = obl.TryGetProperty("status", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.String
                ? (stEl.GetString() ?? "pending").Trim().ToLowerInvariant()
                : "pending";

            statusById[oblId] = status;
            orderedIds.Add(oblId);

            List<string> depList = new List<string>();
            if (obl.TryGetProperty("blocked_by", out JsonElement bbEl) && bbEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement bItem in bbEl.EnumerateArray())
                {
                    string depId = (bItem.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(depId))
                    {
                        depList.Add(depId);
                    }
                }
            }
            depsById[oblId] = depList;

            // Specificity floor check
            string desc = obl.TryGetProperty("description", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.String
                ? (dEl.GetString() ?? string.Empty)
                : string.Empty;
            string rationale = obl.TryGetProperty("rationale", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                ? (rEl.GetString() ?? string.Empty)
                : (obl.TryGetProperty("waiver_reason", out JsonElement wrEl) && wrEl.ValueKind == JsonValueKind.String
                    ? (wrEl.GetString() ?? string.Empty)
                    : string.Empty);

            if (PlaceholderRegex.IsMatch(desc) || (status == "waived" && PlaceholderRegex.IsMatch(rationale)))
            {
                issues.Add(new ObligationIntegrityIssue(
                    "placeholder-specificity-violation",
                    oblId,
                    $"Obligation '{oblId}' contains forbidden placeholder text in description or rationale."));
            }

            // Crash-masking check
            string patchStrategy = obl.TryGetProperty("patch_strategy", out JsonElement psEl) && psEl.ValueKind == JsonValueKind.String
                ? (psEl.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                : string.Empty;
            string patchSnippet = obl.TryGetProperty("patch_snippet", out JsonElement snipEl) && snipEl.ValueKind == JsonValueKind.String
                ? (snipEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (patchStrategy == "early_return_noop" ||
                patchStrategy == "null_guard_early_return" ||
                patchStrategy == "bailout_noop" ||
                (!string.IsNullOrEmpty(patchSnippet) && EarlyReturnNullGuardRegex.IsMatch(patchSnippet)))
            {
                string postInv = obl.TryGetProperty("post_patch_state_invariant", out JsonElement piEl) && piEl.ValueKind == JsonValueKind.String
                    ? (piEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                string negCtrl = obl.TryGetProperty("negative_control_crash_input", out JsonElement ncEl) && ncEl.ValueKind == JsonValueKind.String
                    ? (ncEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(postInv) || PlaceholderRegex.IsMatch(postInv) ||
                    string.IsNullOrWhiteSpace(negCtrl) || PlaceholderRegex.IsMatch(negCtrl))
                {
                    issues.Add(new ObligationIntegrityIssue(
                        "crash-masking-unguarded",
                        oblId,
                        $"Early-return null guard in '{oblId}' requires non-placeholder 'post_patch_state_invariant' and 'negative_control_crash_input'."));
                }
            }

            // Static-only authority gate (#181)
            string oblEvidenceSource = obl.TryGetProperty("evidence_source", out JsonElement esEl) && esEl.ValueKind == JsonValueKind.String
                ? (esEl.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                : string.Empty;
            string oblAuthority = obl.TryGetProperty("authority_scope", out JsonElement oasEl) && oasEl.ValueKind == JsonValueKind.String
                ? (oasEl.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                : dossierAuthorityScope.ToLowerInvariant();
            bool requiresDynamic = (obl.TryGetProperty("requires_dynamic_confirmation", out JsonElement rdcEl) && rdcEl.ValueKind == JsonValueKind.True) ||
                                   dossierRequiresDynamicAuthority ||
                                   oblAuthority == "server_authoritative" ||
                                   oblAuthority == "shared_simulation_desync_risk";
            bool isStaticOnly = oblEvidenceSource == "static_only" ||
                                (string.IsNullOrEmpty(oblEvidenceSource) &&
                                 string.Equals(dossierTriangulationStatus, "static_only", StringComparison.OrdinalIgnoreCase));

            if (status == "satisfied" && requiresDynamic && isStaticOnly)
            {
                blockedByStaticOnlyClaims.Add(oblId);
                issues.Add(new ObligationIntegrityIssue(
                    "static-only-authority-claim",
                    oblId,
                    $"[REA-AUTHORITY-GATE] Obligation '{oblId}' is marked 'satisfied' under authority '{oblAuthority}' but backed only by static_only evidence."));
            }

            // Status-specific checks
            if (status == "satisfied")
            {
                string satisfiedIn = obl.TryGetProperty("satisfied_in", out JsonElement siEl) && siEl.ValueKind == JsonValueKind.String
                    ? (siEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(satisfiedIn))
                {
                    issues.Add(new ObligationIntegrityIssue(
                        "missing-satisfied-in",
                        oblId,
                        $"Obligation '{oblId}' is 'satisfied' but missing 'satisfied_in'."));
                }
                else if (fileContentResolver != null)
                {
                    string? fileText = fileContentResolver(satisfiedIn);
                    if (fileText == null)
                    {
                        issues.Add(new ObligationIntegrityIssue(
                            "missing-satisfied-in",
                            oblId,
                            $"Obligation '{oblId}' implementation file '{satisfiedIn}' not found."));
                    }
                    else
                    {
                        string ext = Path.GetExtension(satisfiedIn).ToLowerInvariant();
                        if (ext != ".json" && ext != ".md" && ext != ".txt" &&
                            !fileText.Contains(oblId, StringComparison.Ordinal))
                        {
                            issues.Add(new ObligationIntegrityIssue(
                                "missing-obligation-tag",
                                oblId,
                                $"File '{satisfiedIn}' does not contain obligation marker '// satisfies: {oblId}'."));
                        }
                    }
                }
            }
            else if (status == "waived")
            {
                string approvedSha = obl.TryGetProperty("approved_instruction_sha256", out JsonElement appShaEl) && appShaEl.ValueKind == JsonValueKind.String
                    ? (appShaEl.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                    : string.Empty;
                string expectedSha = obl.TryGetProperty("instruction_bytes_sha256", out JsonElement insShaEl) && insShaEl.ValueKind == JsonValueKind.String
                    ? (insShaEl.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                    : targetInsnSha;

                if ((!string.IsNullOrEmpty(approvedSha) && !string.IsNullOrEmpty(expectedSha) && approvedSha != expectedSha) ||
                    (obl.TryGetProperty("approved_instruction_sha256", out JsonElement _) && string.IsNullOrEmpty(approvedSha) && !string.IsNullOrEmpty(expectedSha)))
                {
                    issues.Add(new ObligationIntegrityIssue(
                        "stale-cryptographic-waiver",
                        oblId,
                        $"Waived obligation '{oblId}' has stale or empty approved_instruction_sha256 ('{approvedSha}' vs '{expectedSha}')."));
                }
            }
        }

        // Incorporate additional dependency_graph and ownership_graph edges (#181)
        MergeAdditionalGraphEdges(dossier, "dependency_graph", depsById, statusById, orderedIds);
        MergeAdditionalGraphEdges(dossier, "ownership_graph", depsById, statusById, orderedIds);

        // Verify unknown dependencies and satisfied-depends-on-open (#181)
        foreach (string oid in orderedIds)
        {
            string ownerStatus = statusById.TryGetValue(oid, out string? stVal) ? stVal : "pending";
            if (!depsById.TryGetValue(oid, out List<string>? depIds))
            {
                continue;
            }

            foreach (string depId in depIds)
            {
                if (!statusById.TryGetValue(depId, out string? depStatus))
                {
                    issues.Add(new ObligationIntegrityIssue(
                        "unknown-dependency",
                        oid,
                        $"Obligation '{oid}' depends on unknown ID '{depId}'."));
                    continue;
                }

                if (ownerStatus == "satisfied" &&
                    (depStatus == "pending" || depStatus == "open" || depStatus == "contradicted" || depStatus == "unresolved"))
                {
                    string edgeDesc = $"{oid}(satisfied)->{depId}({depStatus})";
                    satisfiedDependsOnOpenEdges.Add(edgeDesc);
                    issues.Add(new ObligationIntegrityIssue(
                        "satisfied-depends-on-open",
                        oid,
                        $"[REA-DAG-CLOSURE] Obligation '{oid}' is marked 'satisfied' but depends on '{depId}' which has open/unresolved status '{depStatus}'."));
                }
            }
        }

        // Cycle detection & topological sort
        Dictionary<string, int> visitState = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string oid in orderedIds)
        {
            visitState[oid] = 0;
        }

        List<string> topoOrder = new List<string>();
        bool hasCycle = false;
        string cyclePath = string.Empty;

        foreach (string oid in orderedIds)
        {
            if (visitState[oid] == 0)
            {
                List<string> stack = new List<string> { oid };
                if (WalkDependencyDagDfs(oid, depsById, visitState, topoOrder, stack, out string detectedCycle))
                {
                    hasCycle = true;
                    cyclePath = detectedCycle;
                    issues.Add(new ObligationIntegrityIssue(
                        "dependency-cycle",
                        oid,
                        $"Obligation dependency cycle detected: {cyclePath}"));
                    break;
                }
            }
        }

        // Branch coverage check
        if (dossier.TryGetProperty("branch_annotations", out JsonElement baEl) && baEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement ba in baEl.EnumerateArray())
            {
                if (ba.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string covObl = ba.TryGetProperty("covered_by_obligation", out JsonElement coEl) && coEl.ValueKind == JsonValueKind.String
                    ? (coEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                string brRva = ba.TryGetProperty("rva", out JsonElement brEl) && brEl.ValueKind == JsonValueKind.String
                    ? (brEl.GetString() ?? "0x0")
                    : "0x0";
                if (string.IsNullOrWhiteSpace(covObl) || !statusById.ContainsKey(covObl))
                {
                    issues.Add(new ObligationIntegrityIssue(
                        "unverified-branch-coverage",
                        brRva,
                        $"Branch at RVA '{brRva}' is missing a valid 'covered_by_obligation' in the obligations ledger."));
                }
            }
        }

        List<string> readyNext = new List<string>();
        if (!hasCycle)
        {
            foreach (string oid in topoOrder)
            {
                if (!statusById.TryGetValue(oid, out string? st) || st != "pending")
                {
                    continue;
                }

                bool allDepsSatisfied = true;
                if (depsById.TryGetValue(oid, out List<string>? depIds))
                {
                    foreach (string depId in depIds)
                    {
                        if (!statusById.TryGetValue(depId, out string? dst) ||
                            (dst != "satisfied" && dst != "waived" && dst != "tombstoned"))
                        {
                            allDepsSatisfied = false;
                            break;
                        }
                    }
                }
                if (allDepsSatisfied)
                {
                    readyNext.Add(oid);
                }
            }
        }

        return new ObligationClosureVerificationReport(
            IsClosedAndValid: issues.Count == 0,
            ClosureDigestSha256: computedClosureDigest,
            DeclaredClosureDigestSha256: declaredClosureDigest,
            TopologicalOrder: topoOrder,
            ReadyNextObligations: readyNext,
            BlockedByStaticOnlyClaims: blockedByStaticOnlyClaims,
            SatisfiedDependsOnOpenEdges: satisfiedDependsOnOpenEdges,
            Issues: issues);
    }

    private static void MergeAdditionalGraphEdges(
        JsonElement dossier,
        string propertyName,
        Dictionary<string, List<string>> depsById,
        Dictionary<string, string> statusById,
        List<string> orderedIds)
    {
        if (!dossier.TryGetProperty(propertyName, out JsonElement graphEl) || graphEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (graphEl.TryGetProperty("nodes", out JsonElement nodesEl) && nodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement n in nodesEl.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string nid = n.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                    ? (idEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(nid))
                {
                    continue;
                }
                string nStatus = n.TryGetProperty("status", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.String
                    ? (stEl.GetString() ?? "pending").Trim().ToLowerInvariant()
                    : "pending";
                if (!statusById.ContainsKey(nid))
                {
                    statusById[nid] = nStatus;
                    orderedIds.Add(nid);
                }
                if (!depsById.ContainsKey(nid))
                {
                    depsById[nid] = new List<string>();
                }
            }
        }

        if (graphEl.TryGetProperty("edges", out JsonElement edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in edgesEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string fromId = e.TryGetProperty("from", out JsonElement fEl) && fEl.ValueKind == JsonValueKind.String
                    ? (fEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                string toId = e.TryGetProperty("to", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                    ? (tEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(fromId) || string.IsNullOrWhiteSpace(toId))
                {
                    continue;
                }
                if (!depsById.TryGetValue(fromId, out List<string>? list))
                {
                    list = new List<string>();
                    depsById[fromId] = list;
                    if (!statusById.ContainsKey(fromId))
                    {
                        statusById[fromId] = "pending";
                        orderedIds.Add(fromId);
                    }
                }
                if (!list.Contains(toId, StringComparer.Ordinal))
                {
                    list.Add(toId);
                }
            }
        }
    }

    private static bool WalkDependencyDagDfs(
        string current,
        Dictionary<string, List<string>> depsById,
        Dictionary<string, int> visitState,
        List<string> topoOrder,
        List<string> stack,
        out string cycleDescription)
    {
        visitState[current] = 1;
        if (depsById.TryGetValue(current, out List<string>? deps))
        {
            foreach (string depId in deps)
            {
                if (!visitState.TryGetValue(depId, out int depState))
                {
                    continue;
                }
                if (depState == 1)
                {
                    List<string> cycleNodes = new List<string>(stack) { depId };
                    cycleDescription = string.Join(" -> ", cycleNodes);
                    return true;
                }
                if (depState == 0)
                {
                    stack.Add(depId);
                    if (WalkDependencyDagDfs(depId, depsById, visitState, topoOrder, stack, out cycleDescription))
                    {
                        return true;
                    }
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        visitState[current] = 2;
        topoOrder.Add(current);
        cycleDescription = string.Empty;
        return false;
    }

    public static IReadOnlyList<string> DetectDuplicateJsonKeys(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Array.Empty<string>();
        }

        List<string> duplicates = new List<string>();
        Dictionary<int, HashSet<string>> seenByDepth = new Dictionary<int, HashSet<string>>();
        int depth = 0;
        MatchCollection matches = Regex.Matches(rawJson, @"(""(?:[^""\\]|\\.)*""(?:\s*:)?|\{|\})");
        foreach (Match m in matches)
        {
            string tok = m.Value;
            if (tok == "{")
            {
                depth++;
                seenByDepth[depth] = new HashSet<string>(StringComparer.Ordinal);
            }
            else if (tok == "}")
            {
                seenByDepth.Remove(depth);
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (depth > 0 && tok.EndsWith(':'))
            {
                int firstQuote = tok.IndexOf('"');
                int lastQuote = tok.LastIndexOf('"');
                if (firstQuote >= 0 && lastQuote > firstQuote)
                {
                    string key = tok.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    if (!seenByDepth.TryGetValue(depth, out HashSet<string>? set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        seenByDepth[depth] = set;
                    }
                    if (!set.Add(key) && !duplicates.Contains(key, StringComparer.Ordinal))
                    {
                        duplicates.Add(key);
                    }
                }
            }
        }

        return duplicates;
    }

    // ========================================================================
    // #182 (Gem 1H): 14-Rule Semantic Integrity Readiness Checker
    // ========================================================================
    // satisfies: #182
    public static SemanticReadinessReport EvaluateSemanticReadiness(JsonElement root)
    {
        List<SemanticReadinessViolation> violations = new List<SemanticReadinessViolation>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new SemanticReadinessReport(true, SemanticReadinessRuleCodes.Count, violations);
        }

        EvaluateSemanticObject(root, "root", violations);

        if (root.TryGetProperty("semantic_claims", out JsonElement claimsEl) && claimsEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (JsonElement claim in claimsEl.EnumerateArray())
            {
                string claimId = claim.ValueKind == JsonValueKind.Object &&
                                 claim.TryGetProperty("id", out JsonElement cidEl) &&
                                 cidEl.ValueKind == JsonValueKind.String
                    ? (cidEl.GetString() ?? $"claim_{idx}")
                    : $"claim_{idx}";
                EvaluateSemanticObject(claim, claimId, violations);
                idx++;
            }
        }

        if (root.TryGetProperty("obligations", out JsonElement oblsEl) && oblsEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (JsonElement obl in oblsEl.EnumerateArray())
            {
                string oblId = obl.ValueKind == JsonValueKind.Object &&
                               obl.TryGetProperty("id", out JsonElement oidEl) &&
                               oidEl.ValueKind == JsonValueKind.String
                    ? (oidEl.GetString() ?? $"obl_{idx}")
                    : $"obl_{idx}";
                EvaluateSemanticObject(obl, oblId, violations);
                idx++;
            }
        }

        return new SemanticReadinessReport(
            IsReady: violations.Count == 0,
            CheckedRuleCount: SemanticReadinessRuleCodes.Count,
            Violations: violations);
    }

    private static void EvaluateSemanticObject(
        JsonElement obj,
        string subjectId,
        List<SemanticReadinessViolation> violations)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Rule 1: false-equivalence (claiming equivalence when evidence is truncated or unstable)
        bool equivalenceClaimed = GetBool(obj, "equivalence_claimed") ||
                                  string.Equals(GetString(obj, "equivalence_status"), "equivalent", StringComparison.OrdinalIgnoreCase);
        bool evidenceTruncated = GetBool(obj, "evidence_truncated") || GetBool(obj, "truncated");
        bool evidenceUnstable = GetBool(obj, "evidence_unstable") || GetBool(obj, "unstable");
        if (equivalenceClaimed && (evidenceTruncated || evidenceUnstable))
        {
            violations.Add(new SemanticReadinessViolation(
                "false-equivalence",
                subjectId,
                $"[REA-SEMANTIC-01] Subject '{subjectId}' claims functional equivalence on truncated or unstable evidence."));
        }

        // Rule 2: unsafe-concurrency-semantics (total-order assumption on concurrent flow without sync proof)
        bool isConcurrentFlow = GetBool(obj, "is_concurrent_flow") || GetBool(obj, "concurrent");
        bool assumesTotalOrder = GetBool(obj, "assumes_total_order");
        bool syncProven = GetBool(obj, "synchronization_proven");
        if (isConcurrentFlow && assumesTotalOrder && !syncProven)
        {
            violations.Add(new SemanticReadinessViolation(
                "unsafe-concurrency-semantics",
                subjectId,
                $"[REA-SEMANTIC-02] Subject '{subjectId}' assumes total ordering across a concurrent flow without synchronization proof."));
        }

        // Rule 3: divergence-not-localized (has_divergence == true without first_divergence_site / first_divergence)
        bool hasDivergence = GetBool(obj, "has_divergence") ||
                             string.Equals(GetString(obj, "conformance_status"), "divergent", StringComparison.OrdinalIgnoreCase);
        string firstDivSite = GetString(obj, "first_divergence_site");
        bool hasFirstDivObj = obj.TryGetProperty("first_divergence", out JsonElement fdEl) && fdEl.ValueKind == JsonValueKind.Object;
        if (hasDivergence && string.IsNullOrWhiteSpace(firstDivSite) && !hasFirstDivObj)
        {
            violations.Add(new SemanticReadinessViolation(
                "divergence-not-localized",
                subjectId,
                $"[REA-SEMANTIC-03] Subject '{subjectId}' reports divergence without localizing 'first_divergence_site' or 'first_divergence'."));
        }

        // Rule 4: contradiction-proof-incomplete (status == contradicted requires >= 2 distinct evidence digests)
        string status = GetString(obj, "status");
        string classification = GetString(obj, "classification");
        if (string.Equals(status, "contradicted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(classification, "contradiction", StringComparison.OrdinalIgnoreCase))
        {
            int distinctDigests = CountDistinctStrings(obj, "evidence_digests");
            if (obj.TryGetProperty("distinct_evidence_digests", out JsonElement dedEl) && dedEl.TryGetInt32(out int explicitCount))
            {
                distinctDigests = Math.Max(distinctDigests, explicitCount);
            }
            if (distinctDigests < 2)
            {
                violations.Add(new SemanticReadinessViolation(
                    "contradiction-proof-incomplete",
                    subjectId,
                    $"[REA-SEMANTIC-04] Subject '{subjectId}' claims contradiction with {distinctDigests} distinct evidence digest(s) (requires >= 2)."));
            }
        }

        // Rule 5: unbounded-loop-effect
        if (GetBool(obj, "has_unbounded_loop") && !GetBool(obj, "loop_bound_verified"))
        {
            violations.Add(new SemanticReadinessViolation(
                "unbounded-loop-effect",
                subjectId,
                $"[REA-SEMANTIC-05] Subject '{subjectId}' contains an unbounded loop/recursion cycle without 'loop_bound_verified'."));
        }

        // Rule 6: aliasing-hazard-untracked
        if (GetBool(obj, "pointer_aliasing_possible") && !GetBool(obj, "alias_separation_verified"))
        {
            violations.Add(new SemanticReadinessViolation(
                "aliasing-hazard-untracked",
                subjectId,
                $"[REA-SEMANTIC-06] Subject '{subjectId}' has overlapping pointer read/write alias domain without 'alias_separation_verified'."));
        }

        // Rule 7: calling-convention-drift
        if (GetBool(obj, "abi_volatile_clobber"))
        {
            violations.Add(new SemanticReadinessViolation(
                "calling-convention-drift",
                subjectId,
                $"[REA-SEMANTIC-07] Subject '{subjectId}' clobbers Win64 ABI non-volatile registers without prologue/epilogue preservation."));
        }

        // Rule 8: unobserved-side-effect-claim
        int omittedOps = GetInt(obj, "omitted_operations_lower_bound");
        if (GetBool(obj, "claims_pure") && (omittedOps > 0 || GetBool(obj, "has_memory_writes")))
        {
            violations.Add(new SemanticReadinessViolation(
                "unobserved-side-effect-claim",
                subjectId,
                $"[REA-SEMANTIC-08] Subject '{subjectId}' claims purity while omitted_operations_lower_bound={omittedOps} or memory writes exist."));
        }

        // Rule 9: floating-point-nan-hazard
        if (GetBool(obj, "has_raw_float_equality") && !GetBool(obj, "uint_view_or_epsilon_verified"))
        {
            violations.Add(new SemanticReadinessViolation(
                "floating-point-nan-hazard",
                subjectId,
                $"[REA-SEMANTIC-09] Subject '{subjectId}' performs IEEE-754 float equality without _UINT_VIEW or epsilon guard."));
        }

        // Rule 10: stale-oracle-snapshot
        if (GetBool(obj, "oracle_binary_sha256_mismatch"))
        {
            violations.Add(new SemanticReadinessViolation(
                "stale-oracle-snapshot",
                subjectId,
                $"[REA-SEMANTIC-10] Subject '{subjectId}' combines Ghidra and IDA/runtime snapshots from mismatched binary SHA-256 digests."));
        }

        // Rule 11: unresolved-indirect-dispatch
        if (GetBool(obj, "indirect_dispatch_marked_resolved") && GetInt(obj, "resolved_target_count", defaultValue: 1) <= 0)
        {
            violations.Add(new SemanticReadinessViolation(
                "unresolved-indirect-dispatch",
                subjectId,
                $"[REA-SEMANTIC-11] Subject '{subjectId}' marks indirect call/branch resolved with 0 concrete target RVAs."));
        }

        // Rule 12: exception-unwind-broken
        if (GetBool(obj, "modifies_stack_without_unwind"))
        {
            violations.Add(new SemanticReadinessViolation(
                "exception-unwind-broken",
                subjectId,
                $"[REA-SEMANTIC-12] Subject '{subjectId}' modifies RSP without matching .pdata UNWIND_INFO or trampoline unwind callback."));
        }

        // Rule 13: thread-affinity-violation
        if (GetBool(obj, "off_main_thread_ui_mutation"))
        {
            violations.Add(new SemanticReadinessViolation(
                "thread-affinity-violation",
                subjectId,
                $"[REA-SEMANTIC-13] Subject '{subjectId}' mutates K2 UI/WndProc/CConsole state off the main window thread."));
        }

        // Rule 14: incomplete-rollback-bytes
        if (GetBool(obj, "rollback_bytes_incomplete"))
        {
            violations.Add(new SemanticReadinessViolation(
                "incomplete-rollback-bytes",
                subjectId,
                $"[REA-SEMANTIC-14] Subject '{subjectId}' patch site lacks complete originalBytes snapshot for deterministic RAII rollback."));
        }
    }

    // ========================================================================
    // #183 (Gem 1B): 5-State Static-vs-Runtime Correlation Matrix
    // ========================================================================
    // satisfies: #183
    public static StaticRuntimeCorrelationRecord CorrelateStaticAndRuntime(
        string subjectId,
        string? staticBeforeDigest,
        string? staticAfterDigest,
        string? runtimeBeforeDigest,
        string? runtimeAfterDigest,
        bool staticTruncated = false,
        bool runtimeTruncated = false)
    {
        bool staticMissing = staticTruncated ||
                             string.IsNullOrWhiteSpace(staticBeforeDigest) ||
                             string.IsNullOrWhiteSpace(staticAfterDigest);
        bool runtimeMissing = runtimeTruncated ||
                              string.IsNullOrWhiteSpace(runtimeBeforeDigest) ||
                              string.IsNullOrWhiteSpace(runtimeAfterDigest);

        string sBefore = (staticBeforeDigest ?? string.Empty).Trim().ToLowerInvariant();
        string sAfter = (staticAfterDigest ?? string.Empty).Trim().ToLowerInvariant();
        string rBefore = (runtimeBeforeDigest ?? string.Empty).Trim().ToLowerInvariant();
        string rAfter = (runtimeAfterDigest ?? string.Empty).Trim().ToLowerInvariant();

        string state;
        string sideAlignment = "left_to_left";
        string classification;

        if (staticMissing || runtimeMissing)
        {
            state = "unknown";
            classification = "unresolved_branch";
        }
        else
        {
            bool staticChanged = !string.Equals(sBefore, sAfter, StringComparison.Ordinal);
            bool runtimeChanged = !string.Equals(rBefore, rAfter, StringComparison.Ordinal);

            if (staticChanged && runtimeChanged &&
                string.Equals(sBefore, rAfter, StringComparison.Ordinal) &&
                string.Equals(sAfter, rBefore, StringComparison.Ordinal))
            {
                sideAlignment = "left_to_right";
            }

            if (staticChanged && runtimeChanged)
            {
                state = "cochanged";
                classification = sideAlignment == "left_to_right" ? "contradiction" : "hypothesis";
            }
            else if (staticChanged && !runtimeChanged)
            {
                state = "static_only";
                classification = "contradiction";
            }
            else if (!staticChanged && runtimeChanged)
            {
                state = "runtime_only";
                classification = "contradiction";
            }
            else
            {
                state = "both_unchanged";
                classification = "hypothesis";
            }
        }

        string canonicalPayload =
            $"{{\"classification\":\"{classification}\",\"runtime_after\":\"{rAfter}\",\"runtime_before\":\"{rBefore}\"," +
            $"\"side_alignment\":\"{sideAlignment}\",\"state\":\"{state}\",\"static_after\":\"{sAfter}\"," +
            $"\"static_before\":\"{sBefore}\",\"subject_id\":\"{subjectId}\"}}";
        string digestHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
        string correlationId = $"cor_{digestHex}";
        string? contradictionId = classification == "contradiction" ? $"contra_{digestHex}" : null;

        return new StaticRuntimeCorrelationRecord(
            CorrelationId: correlationId,
            ContradictionId: contradictionId,
            SubjectId: subjectId,
            State: state,
            SideAlignment: sideAlignment,
            Classification: classification,
            StaticBeforeDigest: sBefore,
            StaticAfterDigest: sAfter,
            RuntimeBeforeDigest: rBefore,
            RuntimeAfterDigest: rAfter,
            IsTruncated: staticMissing || runtimeMissing);
    }

    // ========================================================================
    // #185 (Gem 1C): Ghidra HighFunction P-Code Def-Use & Effect Graph
    // ========================================================================
    // satisfies: #185
    public static HighPCodeDefUseAndEffectGraph BuildHighPCodeDefUseAndEffectGraph(
        string functionAddress,
        IReadOnlyList<HighPCodeOperationInput> operations,
        int maxEmittedOperations = 256)
    {
        int cap = Math.Max(1, maxEmittedOperations);
        int totalOps = operations.Count;
        int emittedCount = Math.Min(totalOps, cap);
        int omittedLowerBound = Math.Max(0, totalOps - emittedCount);

        Dictionary<string, string> definingOpByVarnode = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, List<string>> usesByVarnode = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Dictionary<string, List<string>> parameterUses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> blockMembership = new Dictionary<string, string>(StringComparer.Ordinal);
        List<HighPCodeTypedEffect> effects = new List<HighPCodeTypedEffect>();

        for (int i = 0; i < emittedCount; i++)
        {
            HighPCodeOperationInput op = operations[i];
            string membership = string.IsNullOrWhiteSpace(op.BasicBlockId)
                ? "unavailable"
                : (op.BasicBlockId.StartsWith("detached", StringComparison.OrdinalIgnoreCase) ? "detached" : "member");
            blockMembership[op.OpId] = membership;

            if (!string.IsNullOrWhiteSpace(op.OutputVarnode))
            {
                definingOpByVarnode[op.OutputVarnode] = op.OpId;
                if (!usesByVarnode.ContainsKey(op.OutputVarnode))
                {
                    usesByVarnode[op.OutputVarnode] = new List<string>();
                }
            }

            foreach (string inVn in op.InputVarnodes)
            {
                if (string.IsNullOrWhiteSpace(inVn))
                {
                    continue;
                }

                if (!usesByVarnode.TryGetValue(inVn, out List<string>? useList))
                {
                    useList = new List<string>();
                    usesByVarnode[inVn] = useList;
                }
                useList.Add(op.OpId);

                if (inVn.StartsWith("param_", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(inVn, "rcx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(inVn, "rdx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(inVn, "r8", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(inVn, "r9", StringComparison.OrdinalIgnoreCase))
                {
                    if (!parameterUses.TryGetValue(inVn, out List<string>? paramOpList))
                    {
                        paramOpList = new List<string>();
                        parameterUses[inVn] = paramOpList;
                    }
                    if (!paramOpList.Contains(op.OpId, StringComparer.Ordinal))
                    {
                        paramOpList.Add(op.OpId);
                    }
                }
            }

            string upperOpcode = op.Opcode.Trim().ToUpperInvariant();
            string? effectKind = upperOpcode switch
            {
                "LOAD" or "MOV_LOAD" or "MEM_READ" => "memory_read",
                "STORE" or "MOV_STORE" or "MEM_WRITE" => "memory_write",
                "CBRANCH" or "JCC" or "JE" or "JNE" => "conditional_branch",
                "BRANCHIND" or "JMP_IND" => "indirect_branch",
                "CALL" => "direct_call",
                "CALLIND" or "CALL_IND" => "indirect_call",
                _ => null
            };

            if (effectKind != null)
            {
                effects.Add(new HighPCodeTypedEffect(
                    OpId: op.OpId,
                    Address: op.Address,
                    EffectKind: effectKind,
                    TargetOrSlot: op.TargetAddress ?? op.InputVarnodes.FirstOrDefault() ?? "unknown",
                    BlockMembership: membership));
            }
        }

        List<HighPCodeDefUseChain> defUseChains = new List<HighPCodeDefUseChain>();
        foreach (KeyValuePair<string, string> kvp in definingOpByVarnode)
        {
            string vn = kvp.Key;
            string defOp = kvp.Value;
            IReadOnlyList<string> consumers = usesByVarnode.TryGetValue(vn, out List<string>? uList)
                ? uList
                : Array.Empty<string>();
            defUseChains.Add(new HighPCodeDefUseChain(vn, defOp, consumers));
        }

        return new HighPCodeDefUseAndEffectGraph(
            FunctionAddress: functionAddress,
            DefUseChains: defUseChains,
            ParameterUses: parameterUses.ToDictionary(
                (KeyValuePair<string, List<string>> k) => k.Key,
                (KeyValuePair<string, List<string>> v) => (IReadOnlyList<string>)v.Value,
                StringComparer.OrdinalIgnoreCase),
            BlockMembership: blockMembership,
            Effects: effects,
            TotalOperations: totalOps,
            EmittedOperations: emittedCount,
            OmittedOperationsLowerBound: omittedLowerBound);
    }

    // ========================================================================
    // #186 (Gem 1G): 28-Node / 16-Edge Native Investigation DAG
    // ========================================================================
    // satisfies: #186
    public static InvestigationDagEvaluation EvaluateInvestigationDag(
        IReadOnlyList<InvestigationDagNode> nodes,
        IReadOnlyList<InvestigationDagEdge> edges)
    {
        HashSet<string> validNodeKinds = new HashSet<string>(InvestigationNodeKinds, StringComparer.Ordinal);
        HashSet<string> validEdgeKinds = new HashSet<string>(InvestigationEdgeKinds, StringComparer.Ordinal);
        HashSet<string> validStatuses = new HashSet<string>(InvestigationEpistemicStatuses, StringComparer.Ordinal);

        List<string> schemaErrors = new List<string>();
        Dictionary<string, InvestigationDagNode> nodeMap = new Dictionary<string, InvestigationDagNode>(StringComparer.Ordinal);
        Dictionary<string, string> effectiveStatuses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (InvestigationDagNode node in nodes)
        {
            if (!validNodeKinds.Contains(node.Kind))
            {
                schemaErrors.Add($"Invalid node kind '{node.Kind}' on node '{node.Id}'.");
            }
            if (!validStatuses.Contains(node.Status))
            {
                schemaErrors.Add($"Invalid epistemic status '{node.Status}' on node '{node.Id}'.");
            }
            nodeMap[node.Id] = node;
            effectiveStatuses[node.Id] = node.Status;
        }

        Dictionary<string, List<string>> dispatchTargetsBySlot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> contradictedNodes = new List<string>();
        List<string> corroboratedNodes = new List<string>();

        foreach (InvestigationDagEdge edge in edges)
        {
            if (!validEdgeKinds.Contains(edge.Kind))
            {
                schemaErrors.Add($"Invalid edge kind '{edge.Kind}' ({edge.FromId} -> {edge.ToId}).");
            }
            if (!nodeMap.ContainsKey(edge.FromId))
            {
                schemaErrors.Add($"Edge '{edge.Kind}' references unknown source node '{edge.FromId}'.");
            }
            if (!nodeMap.ContainsKey(edge.ToId))
            {
                schemaErrors.Add($"Edge '{edge.Kind}' references unknown target node '{edge.ToId}'.");
            }

            if (edge.Kind == "contradicts" && effectiveStatuses.ContainsKey(edge.ToId))
            {
                effectiveStatuses[edge.ToId] = "ambiguous";
                if (!contradictedNodes.Contains(edge.ToId, StringComparer.Ordinal))
                {
                    contradictedNodes.Add(edge.ToId);
                }
            }
            else if (edge.Kind == "corroborates" && effectiveStatuses.TryGetValue(edge.ToId, out string? curSt))
            {
                if (curSt == "inferred")
                {
                    effectiveStatuses[edge.ToId] = "resolved";
                }
                if (!corroboratedNodes.Contains(edge.ToId, StringComparer.Ordinal))
                {
                    corroboratedNodes.Add(edge.ToId);
                }
            }
            else if (edge.Kind == "dispatches_to")
            {
                if (!dispatchTargetsBySlot.TryGetValue(edge.FromId, out List<string>? targets))
                {
                    targets = new List<string>();
                    dispatchTargetsBySlot[edge.FromId] = targets;
                }
                if (!targets.Contains(edge.ToId, StringComparer.Ordinal))
                {
                    targets.Add(edge.ToId);
                }
            }
        }

        foreach (KeyValuePair<string, List<string>> kvp in dispatchTargetsBySlot)
        {
            if (kvp.Value.Count > 1 &&
                effectiveStatuses.TryGetValue(kvp.Key, out string? st) &&
                st != "observed" &&
                st != "resolved")
            {
                effectiveStatuses[kvp.Key] = "ambiguous";
            }
        }

        return new InvestigationDagEvaluation(
            IsValid: schemaErrors.Count == 0,
            NodeCount: nodes.Count,
            EdgeCount: edges.Count,
            SupportedNodeKindCount: InvestigationNodeKinds.Count,
            SupportedEdgeKindCount: InvestigationEdgeKinds.Count,
            EffectiveNodeStatuses: effectiveStatuses,
            CorroboratedNodeIds: corroboratedNodes,
            ContradictedNodeIds: contradictedNodes,
            SchemaErrors: schemaErrors);
    }

    // ========================================================================
    // #187 (Gem 1I): 8-Facet Cleanroom Function Equivalence Comparator
    // ========================================================================
    // satisfies: #187
    public static CleanroomEquivalenceReport CompareCleanroomEquivalence8Facet(
        CleanroomFunctionSnapshot original,
        CleanroomFunctionSnapshot cleanroom)
    {
        Dictionary<string, FacetComparisonResult> facetResults = new Dictionary<string, FacetComparisonResult>(StringComparer.Ordinal);

        // 1. identity
        string origIdentity = $"{original.ReturnType.Trim()}|{original.ParameterCount}|{original.CallingConvention.Trim().ToLowerInvariant()}";
        string cleanIdentity = $"{cleanroom.ReturnType.Trim()}|{cleanroom.ParameterCount}|{cleanroom.CallingConvention.Trim().ToLowerInvariant()}";
        facetResults["identity"] = new FacetComparisonResult(
            "identity",
            string.Equals(origIdentity, cleanIdentity, StringComparison.OrdinalIgnoreCase),
            origIdentity,
            cleanIdentity);

        // 2. pseudocode (auto-name normalized)
        string origPseudo = NormalizeDecompilerAutoNames(original.Pseudocode);
        string cleanPseudo = NormalizeDecompilerAutoNames(cleanroom.Pseudocode);
        facetResults["pseudocode"] = new FacetComparisonResult(
            "pseudocode",
            string.Equals(origPseudo, cleanPseudo, StringComparison.Ordinal),
            origPseudo,
            cleanPseudo);

        // 3. assembly (NOP & alignment padding stripped)
        string origAsm = string.Join(";", NormalizeAssemblyPadding(original.AssemblyInstructions));
        string cleanAsm = string.Join(";", NormalizeAssemblyPadding(cleanroom.AssemblyInstructions));
        facetResults["assembly"] = new FacetComparisonResult(
            "assembly",
            string.Equals(origAsm, cleanAsm, StringComparison.OrdinalIgnoreCase),
            origAsm,
            cleanAsm);

        // 4. comments
        string origComments = string.Join("|", original.Comments.Select((string c) => c.Trim()).Where((string c) => c.Length > 0));
        string cleanComments = string.Join("|", cleanroom.Comments.Select((string c) => c.Trim()).Where((string c) => c.Length > 0));
        facetResults["comments"] = new FacetComparisonResult(
            "comments",
            string.Equals(origComments, cleanComments, StringComparison.Ordinal),
            origComments,
            cleanComments);

        // 5. calls (auto-name normalized)
        string origCalls = string.Join(",", original.Callees.Select(NormalizeDecompilerAutoNames));
        string cleanCalls = string.Join(",", cleanroom.Callees.Select(NormalizeDecompilerAutoNames));
        facetResults["calls"] = new FacetComparisonResult(
            "calls",
            string.Equals(origCalls, cleanCalls, StringComparison.Ordinal),
            origCalls,
            cleanCalls);

        // 6. references
        string origRefs = string.Join(",", original.References.Select(NormalizeDecompilerAutoNames));
        string cleanRefs = string.Join(",", cleanroom.References.Select(NormalizeDecompilerAutoNames));
        facetResults["references"] = new FacetComparisonResult(
            "references",
            string.Equals(origRefs, cleanRefs, StringComparison.Ordinal),
            origRefs,
            cleanRefs);

        // 7. strings_names
        string origStrings = string.Join("|", original.StringLiterals);
        string cleanStrings = string.Join("|", cleanroom.StringLiterals);
        facetResults["strings_names"] = new FacetComparisonResult(
            "strings_names",
            string.Equals(origStrings, cleanStrings, StringComparison.Ordinal),
            origStrings,
            cleanStrings);

        // 8. cfg
        string origCfg = $"bb={original.BasicBlockCount},edges={original.EdgeCount},cc={original.CyclomaticComplexity}";
        string cleanCfg = $"bb={cleanroom.BasicBlockCount},edges={cleanroom.EdgeCount},cc={cleanroom.CyclomaticComplexity}";
        facetResults["cfg"] = new FacetComparisonResult(
            "cfg",
            string.Equals(origCfg, cleanCfg, StringComparison.Ordinal),
            origCfg,
            cleanCfg);

        int matchedCount = facetResults.Values.Count((FacetComparisonResult f) => f.IsEquivalent);
        bool structuralMatch =
            facetResults["identity"].IsEquivalent &&
            facetResults["pseudocode"].IsEquivalent &&
            facetResults["assembly"].IsEquivalent &&
            facetResults["calls"].IsEquivalent &&
            facetResults["cfg"].IsEquivalent;

        return new CleanroomEquivalenceReport(
            IsEquivalent: structuralMatch,
            MatchedFacetCount: matchedCount,
            TotalFacetCount: CleanroomEquivalenceFacets.Count,
            Facets: facetResults);
    }

    public static string NormalizeDecompilerAutoNames(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        Dictionary<string, string> funcMap = new Dictionary<string, string>(StringComparer.Ordinal);
        string step1 = DecompilerAutoFuncRegex.Replace(input, (Match m) =>
        {
            if (!funcMap.TryGetValue(m.Value, out string? replacement))
            {
                replacement = $"FUNC_{funcMap.Count}";
                funcMap[m.Value] = replacement;
            }
            return replacement;
        });

        Dictionary<string, string> dataMap = new Dictionary<string, string>(StringComparer.Ordinal);
        string step2 = DecompilerAutoDataRegex.Replace(step1, (Match m) =>
        {
            if (!dataMap.TryGetValue(m.Value, out string? replacement))
            {
                replacement = $"DATA_{dataMap.Count}";
                dataMap[m.Value] = replacement;
            }
            return replacement;
        });

        Dictionary<string, string> varMap = new Dictionary<string, string>(StringComparer.Ordinal);
        string step3 = DecompilerAutoVarRegex.Replace(step2, (Match m) =>
        {
            if (!varMap.TryGetValue(m.Value, out string? replacement))
            {
                replacement = $"VAR_{varMap.Count}";
                varMap[m.Value] = replacement;
            }
            return replacement;
        });

        return Regex.Replace(step3.Trim(), @"\s+", " ");
    }

    public static IReadOnlyList<string> NormalizeAssemblyPadding(IReadOnlyList<string> instructions)
    {
        List<string> normalized = new List<string>();
        foreach (string raw in instructions)
        {
            string trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            string lower = trimmed.ToLowerInvariant();
            if (lower == "nop" ||
                lower.StartsWith("nop ", StringComparison.Ordinal) ||
                lower == "int3" ||
                lower == "int 3" ||
                lower == "xchg ax, ax" ||
                lower == "xchg eax, eax")
            {
                continue;
            }

            normalized.Add(Regex.Replace(lower, @"\s+", " "));
        }
        return normalized;
    }

    // ========================================================================
    // #191 (Gem 1K): Canonical Crash Minidump Register Envelope & Completeness
    // ========================================================================
    // satisfies: #191
    public static CanonicalCrashRegisterEnvelope NormalizeCrashRegisters(
        string? rawRegisterDump,
        IReadOnlyDictionary<string, string>? registerMap = null)
    {
        Dictionary<string, ulong> parsedValues = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        if (registerMap != null)
        {
            foreach (KeyValuePair<string, string> kvp in registerMap)
            {
                string normName = NormalizeRegisterName(kvp.Key);
                if (TryParseHexUInt64(kvp.Value, out ulong val))
                {
                    parsedValues[normName] = val;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(rawRegisterDump))
        {
            // Match WinDbg CDB style: rax=00007ff612345678 or efl=00010206
            MatchCollection cdbMatches = Regex.Matches(
                rawRegisterDump,
                @"\b(r1[0-5]|r[89]|r[abcd]x|r[sd]i|rbp|rsp|rip|eflags|efl)\s*=\s*(?:0x)?([0-9a-fA-F`]+)",
                RegexOptions.IgnoreCase);
            foreach (Match m in cdbMatches)
            {
                string regName = NormalizeRegisterName(m.Groups[1].Value);
                string hexStr = m.Groups[2].Value.Replace("`", string.Empty, StringComparison.Ordinal);
                if (ulong.TryParse(hexStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong val))
                {
                    parsedValues[regName] = val;
                }
            }

            // Match Linux GDB style: "rax            0x7ff612345678    140694847"
            MatchCollection gdbMatches = Regex.Matches(
                rawRegisterDump,
                @"(?m)^\s*(r1[0-5]|r[89]|r[abcd]x|r[sd]i|rbp|rsp|rip|eflags)\s+0x([0-9a-fA-F]+)",
                RegexOptions.IgnoreCase);
            foreach (Match m in gdbMatches)
            {
                string regName = NormalizeRegisterName(m.Groups[1].Value);
                if (ulong.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong val))
                {
                    parsedValues[regName] = val;
                }
            }
        }

        List<CanonicalRegisterEntry> orderedEntries = new List<CanonicalRegisterEntry>(CanonicalX64RegisterOrder.Count);
        Dictionary<string, string> orderedDict = new Dictionary<string, string>(StringComparer.Ordinal);
        int populatedCount = 0;

        foreach (string reg in CanonicalX64RegisterOrder)
        {
            bool present = parsedValues.TryGetValue(reg, out ulong regVal);
            if (present)
            {
                populatedCount++;
            }
            string formatted = $"0x{regVal:x16}";
            orderedEntries.Add(new CanonicalRegisterEntry(reg, formatted, present));
            orderedDict[reg] = formatted;
        }

        return new CanonicalCrashRegisterEnvelope(
            RegisterOrder: CanonicalX64RegisterOrder,
            Registers: orderedEntries,
            RegisterMap: orderedDict,
            PopulatedRegisterCount: populatedCount);
    }

    public static MinidumpStreamCompletenessReport EvaluateMinidumpStreamCompleteness(
        IReadOnlyList<(uint StreamType, uint DataSize, uint Rva)> streams,
        long fileLength)
    {
        (uint TypeId, string Name)[] required = new (uint TypeId, string Name)[]
        {
            (3u, "ThreadListStream"),
            (4u, "ModuleListStream"),
            (6u, "ExceptionStream"),
            (16u, "MemoryInfoListStream")
        };

        List<string> present = new List<string>();
        List<string> missing = new List<string>();
        List<string> truncated = new List<string>();

        foreach ((uint typeId, string name) in required)
        {
            (uint StreamType, uint DataSize, uint Rva) match = streams.FirstOrDefault(
                ((uint StreamType, uint DataSize, uint Rva) s) => s.StreamType == typeId);

            if (match.StreamType != typeId || match.DataSize == 0 || match.Rva == 0)
            {
                missing.Add(name);
            }
            else if (fileLength > 0 && ((long)match.Rva + match.DataSize > fileLength))
            {
                truncated.Add(name);
            }
            else
            {
                present.Add(name);
            }
        }

        bool isComplete = missing.Count == 0 && truncated.Count == 0;
        string readiness = isComplete
            ? "authoritative_complete"
            : (present.Contains("ExceptionStream", StringComparer.Ordinal) ? "degraded_partial_streams" : "incomplete_unusable");

        return new MinidumpStreamCompletenessReport(
            IsComplete: isComplete,
            TriageReadiness: readiness,
            PresentStreams: present,
            MissingStreams: missing,
            TruncatedStreams: truncated);
    }

    private static string NormalizeRegisterName(string name)
    {
        string lower = name.Trim().ToLowerInvariant();
        return lower == "efl" ? "eflags" : lower;
    }

    private static bool TryParseHexUInt64(string raw, out ulong value)
    {
        value = 0UL;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        string clean = raw.Trim().Replace("`", string.Empty, StringComparison.Ordinal);
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(2);
        }
        return ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    // ========================================================================
    // #193 (Gem 1M): Bounded 8 MiB PE .rsrc 3-Level Hierarchy & Manifest Parser
    // ========================================================================
    // satisfies: #193
    public static PeResourceForensicsReport ParsePeResourceHierarchyAndManifest(
        ReadOnlySpan<byte> rsrcBytes,
        uint rsrcSectionRva = 0)
    {
        if (rsrcBytes.Length > MaxResourceSectionBytes)
        {
            return new PeResourceForensicsReport(
                IsWithinBudget: false,
                ResourceSectionBytes: rsrcBytes.Length,
                BudgetLimitBytes: MaxResourceSectionBytes,
                Entries: Array.Empty<PeResourceDirectoryEntryNode>(),
                VersionInfo: null,
                ManifestXml: null,
                RequestedExecutionLevel: null,
                DpiAware: false,
                LongPathAware: false,
                DiagnosticMessage: $"[REA-RSRC-BUDGET] PE .rsrc section ({rsrcBytes.Length} bytes) exceeds 8 MiB safety ceiling ({MaxResourceSectionBytes} bytes).");
        }

        if (rsrcBytes.Length < 16)
        {
            return new PeResourceForensicsReport(
                IsWithinBudget: true,
                ResourceSectionBytes: rsrcBytes.Length,
                BudgetLimitBytes: MaxResourceSectionBytes,
                Entries: Array.Empty<PeResourceDirectoryEntryNode>(),
                VersionInfo: null,
                ManifestXml: null,
                RequestedExecutionLevel: null,
                DpiAware: false,
                LongPathAware: false,
                DiagnosticMessage: "Empty or minimal .rsrc buffer.");
        }

        List<PeResourceDirectoryEntryNode> entries = new List<PeResourceDirectoryEntryNode>();
        PeVersionInfoForensics? versionInfo = null;
        string? manifestXml = null;
        string? requestedExecLevel = null;
        bool dpiAware = false;
        bool longPathAware = false;

        int typeCount = ReadDirectoryEntryCount(rsrcBytes, 0);
        for (int t = 0; t < typeCount; t++)
        {
            int tEntryOff = 16 + (t * 8);
            if (tEntryOff + 8 > rsrcBytes.Length)
            {
                break;
            }

            uint typeIdOrName = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(tEntryOff, 4));
            uint level2OffsetRaw = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(tEntryOff + 4, 4));
            if ((level2OffsetRaw & 0x80000000u) == 0)
            {
                continue;
            }

            int level2Off = (int)(level2OffsetRaw & 0x7FFFFFFFu);
            if (level2Off <= 0 || level2Off + 16 > rsrcBytes.Length)
            {
                continue;
            }

            int nameCount = ReadDirectoryEntryCount(rsrcBytes, level2Off);
            for (int n = 0; n < nameCount; n++)
            {
                int nEntryOff = level2Off + 16 + (n * 8);
                if (nEntryOff + 8 > rsrcBytes.Length)
                {
                    break;
                }

                uint nameId = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(nEntryOff, 4));
                uint level3OffsetRaw = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(nEntryOff + 4, 4));
                if ((level3OffsetRaw & 0x80000000u) == 0)
                {
                    continue;
                }

                int level3Off = (int)(level3OffsetRaw & 0x7FFFFFFFu);
                if (level3Off <= 0 || level3Off + 16 > rsrcBytes.Length)
                {
                    continue;
                }

                int langCount = ReadDirectoryEntryCount(rsrcBytes, level3Off);
                for (int l = 0; l < langCount; l++)
                {
                    int lEntryOff = level3Off + 16 + (l * 8);
                    if (lEntryOff + 8 > rsrcBytes.Length)
                    {
                        break;
                    }

                    uint langId = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(lEntryOff, 4));
                    uint dataEntryOff = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(lEntryOff + 4, 4));
                    if ((dataEntryOff & 0x80000000u) != 0 || dataEntryOff + 16 > rsrcBytes.Length)
                    {
                        continue;
                    }

                    int dOff = (int)dataEntryOff;
                    uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(dOff, 4));
                    uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(dOff + 4, 4));
                    uint codePage = BinaryPrimitives.ReadUInt32LittleEndian(rsrcBytes.Slice(dOff + 8, 4));

                    int payloadOffset = dataRva >= rsrcSectionRva ? (int)(dataRva - rsrcSectionRva) : (int)dataRva;
                    entries.Add(new PeResourceDirectoryEntryNode(
                        TypeId: typeIdOrName & 0xFFFFu,
                        NameId: nameId & 0xFFFFu,
                        LanguageId: langId & 0xFFFFu,
                        DataRva: dataRva,
                        DataSize: dataSize,
                        CodePage: codePage));

                    if (payloadOffset >= 0 && payloadOffset + dataSize <= rsrcBytes.Length && dataSize > 0)
                    {
                        ReadOnlySpan<byte> payload = rsrcBytes.Slice(payloadOffset, (int)dataSize);
                        uint resourceType = typeIdOrName & 0xFFFFu;
                        if (resourceType == 16u && versionInfo == null)
                        {
                            versionInfo = TryParseVsVersionInfo(payload);
                        }
                        else if (resourceType == 24u && manifestXml == null)
                        {
                            manifestXml = Encoding.UTF8.GetString(payload).Trim('\0', '\uFEFF', ' ', '\t', '\r', '\n');
                            Match execMatch = Regex.Match(
                                manifestXml,
                                @"requestedExecutionLevel\s+level\s*=\s*[""']([^""']+)[""']",
                                RegexOptions.IgnoreCase);
                            if (execMatch.Success)
                            {
                                requestedExecLevel = execMatch.Groups[1].Value;
                            }
                            dpiAware = manifestXml.Contains("dpiAware", StringComparison.OrdinalIgnoreCase) &&
                                       manifestXml.Contains("true", StringComparison.OrdinalIgnoreCase);
                            longPathAware = manifestXml.Contains("longPathAware", StringComparison.OrdinalIgnoreCase) &&
                                            manifestXml.Contains("true", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }
        }

        return new PeResourceForensicsReport(
            IsWithinBudget: true,
            ResourceSectionBytes: rsrcBytes.Length,
            BudgetLimitBytes: MaxResourceSectionBytes,
            Entries: entries,
            VersionInfo: versionInfo,
            ManifestXml: manifestXml,
            RequestedExecutionLevel: requestedExecLevel,
            DpiAware: dpiAware,
            LongPathAware: longPathAware,
            DiagnosticMessage: $"Parsed {entries.Count} resource node(s) across 3-level Type->Name->Language hierarchy.");
    }

    private static int ReadDirectoryEntryCount(ReadOnlySpan<byte> rsrc, int dirOffset)
    {
        if (dirOffset < 0 || dirOffset + 16 > rsrc.Length)
        {
            return 0;
        }
        ushort named = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 12, 2));
        ushort idCount = BinaryPrimitives.ReadUInt16LittleEndian(rsrc.Slice(dirOffset + 14, 2));
        return Math.Min(256, named + idCount);
    }

    private static PeVersionInfoForensics? TryParseVsVersionInfo(ReadOnlySpan<byte> payload)
    {
        // Scan for VS_FIXEDFILEINFO signature 0xFEEF04BD (little-endian BD 04 EF FE)
        for (int i = 0; i + 52 <= payload.Length; i += 2)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i, 4));
            if (sig == 0xFEEF04BDu)
            {
                uint fileVersionMs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i + 8, 4));
                uint fileVersionLs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i + 12, 4));
                uint prodVersionMs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i + 16, 4));
                uint prodVersionLs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i + 20, 4));

                string fileVer = $"{fileVersionMs >> 16}.{fileVersionMs & 0xFFFF}.{fileVersionLs >> 16}.{fileVersionLs & 0xFFFF}";
                string prodVer = $"{prodVersionMs >> 16}.{prodVersionMs & 0xFFFF}.{prodVersionLs >> 16}.{prodVersionLs & 0xFFFF}";
                return new PeVersionInfoForensics(0xFEEF04BDu, fileVer, prodVer);
            }
        }
        return null;
    }

    // ========================================================================
    // #194 (Gem 1R): COFF Machine & ReadyToRun OS-Override Decoder (64 MiB Budget)
    // ========================================================================
    // satisfies: #194
    public static CoffAndReadyToRunForensicsReport DecodeCoffAndReadyToRunHeader(
        ReadOnlySpan<byte> binaryBytes,
        long declaredTotalBytes = -1)
    {
        long effectiveBytes = declaredTotalBytes >= 0 ? declaredTotalBytes : binaryBytes.Length;
        if (effectiveBytes > MaxCoffWorkingMemoryBudgetBytes)
        {
            return new CoffAndReadyToRunForensicsReport(
                IsWithinMemoryBudget: false,
                WorkingMemoryBudgetBytes: MaxCoffWorkingMemoryBudgetBytes,
                RawMachine: 0,
                DecodedBaseMachine: 0,
                AppliedOsOverrideXorMask: 0,
                Architecture: "budget_exceeded",
                TargetOsOverride: "budget_exceeded",
                HasReadyToRunHeader: false,
                R2RMajorVersion: 0,
                R2RMinorVersion: 0,
                R2RFlags: 0,
                R2RSectionCount: 0,
                DiagnosticMessage: $"[REA-R2R-BUDGET] Input size ({effectiveBytes} bytes) exceeds 64 MiB working memory budget ({MaxCoffWorkingMemoryBudgetBytes} bytes).");
        }

        ushort rawMachine = 0;
        if (binaryBytes.Length >= 0x40 && binaryBytes[0] == (byte)'M' && binaryBytes[1] == (byte)'Z')
        {
            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(binaryBytes.Slice(0x3C, 4));
            if (peOffset >= 0 && peOffset + 6 <= binaryBytes.Length &&
                binaryBytes[peOffset] == (byte)'P' && binaryBytes[peOffset + 1] == (byte)'E')
            {
                rawMachine = BinaryPrimitives.ReadUInt16LittleEndian(binaryBytes.Slice(peOffset + 4, 2));
            }
        }
        else if (binaryBytes.Length >= 2)
        {
            rawMachine = BinaryPrimitives.ReadUInt16LittleEndian(binaryBytes.Slice(0, 2));
        }

        (ushort baseMachine, ushort xorMask, string archName, string osOverride) = DeobfuscateCoffMachine(rawMachine);

        bool hasR2R = false;
        ushort r2rMajor = 0;
        ushort r2rMinor = 0;
        uint r2rFlags = 0;
        uint r2rSections = 0;

        for (int i = 0; i + 16 <= binaryBytes.Length; i++)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(binaryBytes.Slice(i, 4));
            if (sig == RtrSignature)
            {
                hasR2R = true;
                r2rMajor = BinaryPrimitives.ReadUInt16LittleEndian(binaryBytes.Slice(i + 4, 2));
                r2rMinor = BinaryPrimitives.ReadUInt16LittleEndian(binaryBytes.Slice(i + 6, 2));
                r2rFlags = BinaryPrimitives.ReadUInt32LittleEndian(binaryBytes.Slice(i + 8, 4));
                r2rSections = BinaryPrimitives.ReadUInt32LittleEndian(binaryBytes.Slice(i + 12, 4));
                break;
            }
        }

        return new CoffAndReadyToRunForensicsReport(
            IsWithinMemoryBudget: true,
            WorkingMemoryBudgetBytes: MaxCoffWorkingMemoryBudgetBytes,
            RawMachine: rawMachine,
            DecodedBaseMachine: baseMachine,
            AppliedOsOverrideXorMask: xorMask,
            Architecture: archName,
            TargetOsOverride: osOverride,
            HasReadyToRunHeader: hasR2R,
            R2RMajorVersion: r2rMajor,
            R2RMinorVersion: r2rMinor,
            R2RFlags: r2rFlags,
            R2RSectionCount: r2rSections,
            DiagnosticMessage: $"[REA-R2R-DECODE] RawMachine=0x{rawMachine:X4} -> BaseMachine=0x{baseMachine:X4} ({archName}) via XorMask=0x{xorMask:X4} ({osOverride}), HasR2R={hasR2R}.");
    }

    public static (ushort BaseMachine, ushort XorMask, string Architecture, string OsOverride) DeobfuscateCoffMachine(ushort rawMachine)
    {
        (ushort Mask, string OsName)[] masks = new (ushort Mask, string OsName)[]
        {
            (0x0000, "Windows_Default"),
            (0x4644, "Linux_OS_Override"),
            (0xADC4, "Apple_macOS_Override"),
            (0xADC5, "FreeBSD_OS_Override"),
            (0x7B79, "NetBSD_R2R_Override"),
            (0x1993, "MSVC_CLR_Override_1993"),
            (0x1992, "MSVC_CLR_Override_1992")
        };

        (ushort Machine, string Arch)[] baseMachines = new (ushort Machine, string Arch)[]
        {
            (0x8664, "x64"),
            (0x014C, "x86"),
            (0xAA64, "arm64"),
            (0x01C4, "armnt")
        };

        foreach ((ushort mask, string osName) in masks)
        {
            ushort candidate = (ushort)(rawMachine ^ mask);
            foreach ((ushort bm, string arch) in baseMachines)
            {
                if (candidate == bm)
                {
                    return (bm, mask, arch, osName);
                }
            }
        }

        return (rawMachine, 0x0000, "unknown", "Unrecognized_Machine");
    }

    // ========================================================================
    // JSON Helper Utilities
    // ========================================================================
    private static bool GetBool(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (el.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        return false;
    }

    private static int GetInt(JsonElement obj, string propName, int defaultValue = 0)
    {
        if (obj.TryGetProperty(propName, out JsonElement el) && el.TryGetInt32(out int val))
        {
            return val;
        }
        return defaultValue;
    }

    private static string GetString(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
        {
            return (el.GetString() ?? string.Empty).Trim();
        }
        return string.Empty;
    }

    private static int CountDistinctStrings(JsonElement obj, string arrayPropName)
    {
        if (!obj.TryGetProperty(arrayPropName, out JsonElement arrEl) || arrEl.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        HashSet<string> distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in arrEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string val = (item.GetString() ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    distinct.Add(val);
                }
            }
        }
        return distinct.Count;
    }
}

// ============================================================================
// Phase 15 Value-Type & Record Contracts
// ============================================================================
public sealed record ObligationIntegrityIssue(
    string Code,
    string SubjectId,
    string Message);

public sealed record ObligationClosureVerificationReport(
    bool IsClosedAndValid,
    string ClosureDigestSha256,
    string? DeclaredClosureDigestSha256,
    IReadOnlyList<string> TopologicalOrder,
    IReadOnlyList<string> ReadyNextObligations,
    IReadOnlyList<string> BlockedByStaticOnlyClaims,
    IReadOnlyList<string> SatisfiedDependsOnOpenEdges,
    IReadOnlyList<ObligationIntegrityIssue> Issues);

public sealed record SemanticReadinessViolation(
    string RuleCode,
    string SubjectId,
    string Message);

public sealed record SemanticReadinessReport(
    bool IsReady,
    int CheckedRuleCount,
    IReadOnlyList<SemanticReadinessViolation> Violations);

public sealed record StaticRuntimeCorrelationRecord(
    string CorrelationId,
    string? ContradictionId,
    string SubjectId,
    string State,
    string SideAlignment,
    string Classification,
    string StaticBeforeDigest,
    string StaticAfterDigest,
    string RuntimeBeforeDigest,
    string RuntimeAfterDigest,
    bool IsTruncated);

public sealed record HighPCodeOperationInput(
    string OpId,
    string Opcode,
    string Address,
    string? BasicBlockId,
    string? OutputVarnode,
    IReadOnlyList<string> InputVarnodes,
    string? TargetAddress = null);

public sealed record HighPCodeDefUseChain(
    string Varnode,
    string DefiningOpId,
    IReadOnlyList<string> UsingOpIds);

public sealed record HighPCodeTypedEffect(
    string OpId,
    string Address,
    string EffectKind,
    string TargetOrSlot,
    string BlockMembership);

public sealed record HighPCodeDefUseAndEffectGraph(
    string FunctionAddress,
    IReadOnlyList<HighPCodeDefUseChain> DefUseChains,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ParameterUses,
    IReadOnlyDictionary<string, string> BlockMembership,
    IReadOnlyList<HighPCodeTypedEffect> Effects,
    int TotalOperations,
    int EmittedOperations,
    int OmittedOperationsLowerBound);

public sealed record InvestigationDagNode(
    string Id,
    string Kind,
    string Label,
    string Status);

public sealed record InvestigationDagEdge(
    string FromId,
    string ToId,
    string Kind,
    string Status = "observed");

public sealed record InvestigationDagEvaluation(
    bool IsValid,
    int NodeCount,
    int EdgeCount,
    int SupportedNodeKindCount,
    int SupportedEdgeKindCount,
    IReadOnlyDictionary<string, string> EffectiveNodeStatuses,
    IReadOnlyList<string> CorroboratedNodeIds,
    IReadOnlyList<string> ContradictedNodeIds,
    IReadOnlyList<string> SchemaErrors);

public sealed record CleanroomFunctionSnapshot(
    string Name,
    string ReturnType,
    int ParameterCount,
    string CallingConvention,
    string Pseudocode,
    IReadOnlyList<string> AssemblyInstructions,
    IReadOnlyList<string> Comments,
    IReadOnlyList<string> Callees,
    IReadOnlyList<string> References,
    IReadOnlyList<string> StringLiterals,
    int BasicBlockCount,
    int EdgeCount,
    int CyclomaticComplexity);

public sealed record FacetComparisonResult(
    string Facet,
    bool IsEquivalent,
    string NormalizedOriginal,
    string NormalizedCleanroom);

public sealed record CleanroomEquivalenceReport(
    bool IsEquivalent,
    int MatchedFacetCount,
    int TotalFacetCount,
    IReadOnlyDictionary<string, FacetComparisonResult> Facets);

public sealed record CanonicalRegisterEntry(
    string Name,
    string HexValue,
    bool IsPresent);

public sealed record CanonicalCrashRegisterEnvelope(
    IReadOnlyList<string> RegisterOrder,
    IReadOnlyList<CanonicalRegisterEntry> Registers,
    IReadOnlyDictionary<string, string> RegisterMap,
    int PopulatedRegisterCount);

public sealed record MinidumpStreamCompletenessReport(
    bool IsComplete,
    string TriageReadiness,
    IReadOnlyList<string> PresentStreams,
    IReadOnlyList<string> MissingStreams,
    IReadOnlyList<string> TruncatedStreams);

public sealed record PeResourceDirectoryEntryNode(
    uint TypeId,
    uint NameId,
    uint LanguageId,
    uint DataRva,
    uint DataSize,
    uint CodePage);

public sealed record PeVersionInfoForensics(
    uint Signature,
    string FileVersion,
    string ProductVersion);

public sealed record PeResourceForensicsReport(
    bool IsWithinBudget,
    int ResourceSectionBytes,
    int BudgetLimitBytes,
    IReadOnlyList<PeResourceDirectoryEntryNode> Entries,
    PeVersionInfoForensics? VersionInfo,
    string? ManifestXml,
    string? RequestedExecutionLevel,
    bool DpiAware,
    bool LongPathAware,
    string DiagnosticMessage);

public sealed record CoffAndReadyToRunForensicsReport(
    bool IsWithinMemoryBudget,
    long WorkingMemoryBudgetBytes,
    ushort RawMachine,
    ushort DecodedBaseMachine,
    ushort AppliedOsOverrideXorMask,
    string Architecture,
    string TargetOsOverride,
    bool HasReadyToRunHeader,
    ushort R2RMajorVersion,
    ushort R2RMinorVersion,
    uint R2RFlags,
    uint R2RSectionCount,
    string DiagnosticMessage);

// ============================================================================
// #192 (Gem 1L): Poly-Base Address Coordinate Algebra (file:0x / rva:0x / va:0x)
// ============================================================================
public enum AddressSpaceKind
{
    FileOffset = 0,
    Rva = 1,
    VirtualAddress = 2
}

public readonly record struct PeSectionRange(
    string Name,
    ulong VirtualAddressRva,
    ulong VirtualSize,
    ulong RawFileOffset,
    ulong RawSize);

// satisfies: #192
public readonly struct AddressCoordinate : IEquatable<AddressCoordinate>
{
    public AddressSpaceKind Space { get; }
    public ulong Offset { get; }

    public AddressCoordinate(AddressSpaceKind space, ulong offset)
    {
        Space = space;
        Offset = offset;
    }

    public string SpacePrefix => Space switch
    {
        AddressSpaceKind.FileOffset => "file",
        AddressSpaceKind.Rva => "rva",
        AddressSpaceKind.VirtualAddress => "va",
        _ => "rva"
    };

    public override string ToString() => $"{SpacePrefix}:0x{Offset:x}";

    public static AddressCoordinate Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("[REA-COORD-CONFLATION] AddressCoordinate string cannot be empty. Expected 'file:0x...', 'rva:0x...', or 'va:0x...'.");
        }

        string trimmed = raw.Trim();
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx <= 0)
        {
            throw new FormatException($"[REA-COORD-CONFLATION] Ambiguous unprefixed address '{raw}'. Explicitly specify coordinate space ('file:0x...', 'rva:0x...', or 'va:0x...').");
        }

        string prefix = trimmed.Substring(0, colonIdx).Trim().ToLowerInvariant();
        string hexPart = trimmed.Substring(colonIdx + 1).Trim();
        if (hexPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hexPart = hexPart.Substring(2);
        }

        if (!ulong.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong offset))
        {
            throw new FormatException($"[REA-COORD-CONFLATION] Invalid hexadecimal offset in '{raw}'.");
        }

        AddressSpaceKind space = prefix switch
        {
            "file" or "raw" => AddressSpaceKind.FileOffset,
            "rva" => AddressSpaceKind.Rva,
            "va" => AddressSpaceKind.VirtualAddress,
            _ => throw new FormatException($"[REA-COORD-CONFLATION] Unknown address space '{prefix}' in '{raw}'. Expected 'file', 'rva', or 'va'.")
        };

        return new AddressCoordinate(space, offset);
    }

    public AddressCoordinate AddDelta(long signedDelta)
    {
        long next = checked((long)Offset + signedDelta);
        if (next < 0)
        {
            throw new OverflowException($"[REA-COORD-CONFLATION] AddressCoordinate underflow adding {signedDelta} to {this}.");
        }
        return new AddressCoordinate(Space, (ulong)next);
    }

    public long Subtract(AddressCoordinate other)
    {
        EnsureSameSpace(other);
        return checked((long)Offset - (long)other.Offset);
    }

    public bool EqualsSameSpace(AddressCoordinate other)
    {
        EnsureSameSpace(other);
        return Offset == other.Offset;
    }

    public void EnsureSameSpace(AddressCoordinate other)
    {
        if (Space != other.Space)
        {
            throw new InvalidOperationException(
                $"[REA-COORD-CONFLATION] Cannot mix '{SpacePrefix}' coordinate ({this}) with '{other.SpacePrefix}' coordinate ({other}) without explicit PE section conversion.");
        }
    }

    public AddressCoordinate ConvertTo(
        AddressSpaceKind targetSpace,
        ulong imageBase,
        IReadOnlyList<PeSectionRange> sections)
    {
        if (Space == targetSpace)
        {
            return this;
        }

        // Normalize to RVA first
        ulong rva;
        if (Space == AddressSpaceKind.Rva)
        {
            rva = Offset;
        }
        else if (Space == AddressSpaceKind.VirtualAddress)
        {
            if (Offset < imageBase)
            {
                throw new InvalidOperationException($"[REA-COORD-CONFLATION] Virtual address {this} is below ImageBase 0x{imageBase:x}.");
            }
            rva = Offset - imageBase;
        }
        else
        {
            // FileOffset -> Rva
            bool matched = false;
            rva = 0UL;
            foreach (PeSectionRange sec in sections)
            {
                if (Offset >= sec.RawFileOffset && Offset < sec.RawFileOffset + sec.RawSize)
                {
                    rva = sec.VirtualAddressRva + (Offset - sec.RawFileOffset);
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                throw new InvalidOperationException($"[REA-COORD-CONFLATION] File offset {this} does not map to any mapped PE section.");
            }
        }

        if (targetSpace == AddressSpaceKind.Rva)
        {
            return new AddressCoordinate(AddressSpaceKind.Rva, rva);
        }

        if (targetSpace == AddressSpaceKind.VirtualAddress)
        {
            return new AddressCoordinate(AddressSpaceKind.VirtualAddress, checked(imageBase + rva));
        }

        // Rva -> FileOffset
        foreach (PeSectionRange sec in sections)
        {
            ulong span = Math.Max(sec.VirtualSize, sec.RawSize);
            if (rva >= sec.VirtualAddressRva && rva < sec.VirtualAddressRva + span)
            {
                ulong delta = rva - sec.VirtualAddressRva;
                if (delta >= sec.RawSize)
                {
                    throw new InvalidOperationException($"[REA-COORD-CONFLATION] RVA 0x{rva:x} falls in uninitialized virtual BSS tail of section '{sec.Name}'.");
                }
                return new AddressCoordinate(AddressSpaceKind.FileOffset, sec.RawFileOffset + delta);
            }
        }

        throw new InvalidOperationException($"[REA-COORD-CONFLATION] RVA 0x{rva:x} does not map to any raw PE section on disk.");
    }

    public bool Equals(AddressCoordinate other) => Space == other.Space && Offset == other.Offset;
    public override bool Equals(object? obj) => obj is AddressCoordinate other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((int)Space, Offset);
    public static bool operator ==(AddressCoordinate left, AddressCoordinate right) => left.Equals(right);
    public static bool operator !=(AddressCoordinate left, AddressCoordinate right) => !left.Equals(right);
}
