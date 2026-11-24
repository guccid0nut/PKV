// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

internal enum DocDriftKind
{
    Path,
    Anchor,
    Command,
    Symbol
}

internal sealed record DocDriftFinding(
    string Doc,
    DocDriftKind Kind,
    string Target,
    string Label,
    string Fingerprint,
    bool IsBaselined);

internal sealed record LayerViolation(
    string RuleId,
    string FromLayer,
    string ToLayer,
    string SourceModule,
    string TargetModule);

internal sealed record ArchitectureAuditResult(
    IReadOnlyList<LayerViolation> LayerViolations,
    IReadOnlyList<string> DetectedCycles,
    bool CycleCapReached);

internal sealed record SecretScanFinding(
    string RuleId,
    string File,
    int Line,
    string MaskedSnippet,
    bool PresentInHead,
    string CommitHash);

internal sealed record CoChangeCommit(
    double CommitAge,
    IReadOnlyList<string> TouchedFiles);

internal sealed record DirectionalCoChangePair(
    string FileA,
    string FileB,
    double ConfidenceAToB,
    double ConfidenceBToA,
    int Support,
    bool IsUnexplainedCoupling);

internal sealed record DiffFileChurn(
    string FilePath,
    int LinesAdded,
    int LinesDeleted);

internal sealed record KameiJitRiskReport(
    int NF,
    int NS,
    int LA,
    int LD,
    int LT,
    double NormalizedShannonEntropy,
    double RiskScore,
    string RiskLevel);

internal sealed record BlastRadiusEntry(
    string ModulePath,
    int MinHopDistance,
    double ImpactScore,
    bool IsTest);

internal sealed record BlastRadiusReport(
    IReadOnlyList<string> SeedFiles,
    double DecayFactor,
    IReadOnlyList<BlastRadiusEntry> AffectedModules,
    IReadOnlyList<BlastRadiusEntry> RankedTests);

internal enum SymbolChangeKind
{
    Added,
    Removed,
    SignatureChanged,
    BodyChanged
}

internal sealed record SymbolDeclaration(
    string Name,
    string Signature,
    string Body,
    int MinArity);

internal sealed record SymbolCallerSite(
    string CallerFile,
    string SymbolName,
    int ArgCount,
    bool WasUpdatedInDiff);

internal sealed record SymbolImpactFinding(
    string SymbolName,
    SymbolChangeKind ChangeKind,
    bool IsEffectBreaking,
    IReadOnlyList<string> BrokenCallers);

internal sealed record ProtocolFieldSpec(
    string Name,
    string Type,
    int Tag,
    bool Required);

internal sealed record ProtocolMessageSpec(
    string Name,
    string Direction,
    IReadOnlyList<ProtocolFieldSpec> Fields);

internal sealed record ProtocolWireBreakFinding(
    string Kind,
    string MessageName,
    string FieldName,
    string Description);

internal sealed record AssetRuntimeReference(
    string CandidateAsset,
    string ReferencedInFile,
    string LoaderKind,
    string MatchedSnippet);

internal sealed record HotLoopViolation(
    string RuleId,
    string FilePath,
    string Description);

internal sealed record DiffTestSignalReport(
    IReadOnlyList<string> MissingTestSignalFiles,
    IReadOnlyList<string> StaleTestCandidates,
    bool HasMissingTestSignal,
    bool HasStaleTests);

internal enum KbProvenanceTier
{
    Inferred = 1,
    Comment = 2,
    Commit = 3,
    ADR = 4,
    Session = 5
}

internal sealed record KbFieldNoteClaim(
    string NoteId,
    KbProvenanceTier ProvenanceTier,
    string BinaryOrSubsystem,
    string ClaimKey,
    string ClaimValue,
    string? SupersedesId,
    string? EvidenceSourcePath,
    string? EvidenceQuote);

internal sealed record KbVerificationResult(
    bool IsValid,
    bool QuoteVerified,
    bool SupersessionAllowed,
    bool HasUnresolvedContradiction,
    IReadOnlyList<string> Errors);

internal enum AutoRatchetDecisionKind
{
    ImprovedKeep,
    SimplicityDeletionKeep,
    ComplexityBloatDiscard,
    RegressedDiscard,
    RetryableTypoCrash,
    HardTimeoutDiscard,
    FatalCrashDiscard
}

internal sealed record AutoRatchetEvaluation(
    AutoRatchetDecisionKind Decision,
    bool KeepCommit,
    bool ShouldRetryTypo,
    string StatusToken,
    string Reason,
    string ResultsTsvRow);

internal sealed record ShallowCloneStatus(
    bool IsShallowClone,
    int CommitCount,
    string FallbackMode);

/// <summary>
/// Phase 19 (#239–252): RepoWise Codebase Intelligence &amp; Karpathy AutoResearch Ratchet Primitives.
/// </summary>
internal static class RepoWiseIntelligenceService
{
    private static readonly Regex MarkdownLinkRegex = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex ScriptCommandRegex = new Regex(@"(?m)(?<!\]\()scripts[\\/]([A-Za-z0-9_./\\-]+\.ps1)([^\r\n`"")*]*)", RegexOptions.Compiled);
    private static readonly Regex FlagTokenRegex = new Regex(@"(?<=\s)-([A-Za-z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);
    private static readonly Regex LoopBlockRegex = new Regex(@"(?s)\b(?:for|foreach|while)\s*\([^)]*\)\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}", RegexOptions.Compiled);

    private static readonly (string RuleId, Regex Pattern)[] SecretPatterns = new (string RuleId, Regex Pattern)[]
    {
        ("SEC-001", new Regex(@"-----BEGIN\s+(?:RSA\s+|EC\s+|DSA\s+|OPENSSH\s+|PRIVATE\s+)?PRIVATE\s+KEY-----", RegexOptions.Compiled)),
        ("SEC-002", new Regex(@"(?:AKIA|ASIA)[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("SEC-003", new Regex(@"(?:ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{82})", RegexOptions.Compiled)),
        ("SEC-004", new Regex(@"(?i)Authorization\s*:\s*Bearer\s+([A-Za-z0-9\-._~+/]{20,}=*)", RegexOptions.Compiled)),
        ("SEC-005", new Regex(@"(?i)(?:password|passwd|pwd|api_key|apikey|client_secret|access_token|auth_token)\s*[:=]\s*['""](?!(?:\$\{|\$[A-Za-z_]|<[A-Za-z_]|YOUR_|REPLACE_|REDACTED|CHANGEME|\*+|test|dummy|mock|example|none|null|empty|localhost|127\.0\.0\.1))([^'""\r\n]{8,})['""]", RegexOptions.Compiled))
    };

    private static readonly string[] LineSplitSeparators = new string[] { "\r\n", "\n" };

    // satisfies: #239 (repowise-multikind-doc-drift-fingerprint-baseline)
    public static string ComputeDocDriftFingerprint(string docRelPath, DocDriftKind kind, string target)
    {
        string normDoc = (docRelPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        string normKind = kind.ToString().ToUpperInvariant();
        string normTarget = (target ?? string.Empty).Replace('\\', '/').Trim();
        string payload = string.Concat("kongor.doc_drift.fingerprint.v1\0", normDoc, "\0", normKind, "\0", normTarget);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 32);
    }

    public static string ConvertToGitHubHeadingSlug(string headingText)
    {
        if (string.IsNullOrWhiteSpace(headingText))
        {
            return string.Empty;
        }

        string clean = headingText.Trim();
        clean = Regex.Replace(clean, @"\[([^\]]+)\]\([^)]+\)", "$1");
        clean = Regex.Replace(clean, @"[`*~]", string.Empty);
        clean = clean.ToLowerInvariant();
        clean = Regex.Replace(clean, @"[^a-z0-9\s\-_]", string.Empty);
        clean = Regex.Replace(clean.Trim(), @"\s+", "-");
        return clean.Trim('-');
    }

    public static IReadOnlyList<DocDriftFinding> AnalyzeMarkdownDocDrift(
        string docRelPath,
        string markdownContent,
        IReadOnlySet<string> existingRelativeFiles,
        IReadOnlyDictionary<string, IReadOnlySet<string>> markdownHeadingSlugsByFile,
        IReadOnlyDictionary<string, IReadOnlySet<string>> allowedScriptFlags,
        IReadOnlyDictionary<string, IReadOnlySet<string>> fileSymbols,
        IReadOnlySet<string>? baselineFingerprints = null)
    {
        List<DocDriftFinding> findings = new List<DocDriftFinding>();
        HashSet<string> seenFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string normDoc = (docRelPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

        MatchCollection linkMatches = MarkdownLinkRegex.Matches(markdownContent ?? string.Empty);
        foreach (Match match in linkMatches)
        {
            string label = match.Groups[1].Value.Trim();
            string rawUrl = match.Groups[2].Value.Trim();
            if (rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                rawUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rawUrl.StartsWith("symbol://", StringComparison.OrdinalIgnoreCase))
            {
                string symSpec = rawUrl.Substring("symbol://".Length);
                string[] symParts = symSpec.Split('#', 2);
                string symFile = symParts[0].Replace('\\', '/').TrimStart('.', '/');
                string symTargetName = symParts.Length > 1 ? symParts[1].Trim() : label;
                if (!existingRelativeFiles.Contains(symFile))
                {
                    AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Path, symFile, label);
                }
                else if (!string.IsNullOrWhiteSpace(symTargetName))
                {
                    bool hasTargetSym = fileSymbols.TryGetValue(symFile, out IReadOnlySet<string>? targetSyms) &&
                                        targetSyms != null &&
                                        targetSyms.Contains(symTargetName);
                    if (!hasTargetSym)
                    {
                        AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Symbol, string.Concat(symFile, "#", symTargetName), label);
                    }
                }
                continue;
            }

            if (rawUrl.StartsWith('#'))
            {
                string slug = rawUrl.Substring(1).Trim();
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    bool hasSelfSlug = markdownHeadingSlugsByFile.TryGetValue(normDoc, out IReadOnlySet<string>? selfSlugs) &&
                                       selfSlugs != null &&
                                       selfSlugs.Contains(slug);
                    if (!hasSelfSlug)
                    {
                        AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Anchor, rawUrl, label);
                    }
                }
                continue;
            }

            string[] urlParts = rawUrl.Split('#', 2);
            string targetFile = urlParts[0].Replace('\\', '/').TrimStart('.', '/');
            string anchorPart = urlParts.Length > 1 ? urlParts[1].Trim() : string.Empty;

            if (!existingRelativeFiles.Contains(targetFile))
            {
                AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Path, targetFile, label);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(anchorPart) &&
                targetFile.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(anchorPart, @"^L\d+"))
            {
                bool hasTargetSlug = markdownHeadingSlugsByFile.TryGetValue(targetFile, out IReadOnlySet<string>? targetSlugs) &&
                                     targetSlugs != null &&
                                     targetSlugs.Contains(anchorPart);
                if (!hasTargetSlug)
                {
                    AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Anchor, string.Concat(targetFile, "#", anchorPart), label);
                }
            }

            if (label.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase))
            {
                string symName = label.Substring("symbol:".Length).Trim();
                bool hasSym = fileSymbols.TryGetValue(targetFile, out IReadOnlySet<string>? syms) &&
                              syms != null &&
                              syms.Contains(symName);
                if (!hasSym)
                {
                    AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Symbol, string.Concat(targetFile, "#", symName), label);
                }
            }
        }

        MatchCollection cmdMatches = ScriptCommandRegex.Matches(markdownContent ?? string.Empty);
        foreach (Match cmdMatch in cmdMatches)
        {
            string scriptRel = string.Concat("scripts/", cmdMatch.Groups[1].Value.Replace('\\', '/'));
            string argsSegment = cmdMatch.Groups[2].Value;
            if (!allowedScriptFlags.TryGetValue(scriptRel, out IReadOnlySet<string>? validFlags) || validFlags == null)
            {
                AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Command, scriptRel, scriptRel);
            }
            else
            {
                MatchCollection flagMatches = FlagTokenRegex.Matches(argsSegment);
                foreach (Match flMatch in flagMatches)
                {
                    string flagName = flMatch.Groups[1].Value;
                    if (!validFlags.Contains(flagName))
                    {
                        string cmdTarget = string.Concat(scriptRel, " -", flagName);
                        AddDocFinding(findings, seenFingerprints, baselineFingerprints, normDoc, DocDriftKind.Command, cmdTarget, cmdTarget);
                    }
                }
            }
        }

        return findings;
    }

    private static void AddDocFinding(
        List<DocDriftFinding> findings,
        HashSet<string> seenFingerprints,
        IReadOnlySet<string>? baselineFingerprints,
        string doc,
        DocDriftKind kind,
        string target,
        string label)
    {
        string fp = ComputeDocDriftFingerprint(doc, kind, target);
        if (seenFingerprints.Add(fp))
        {
            bool isBaselined = baselineFingerprints != null && baselineFingerprints.Contains(fp);
            findings.Add(new DocDriftFinding(doc, kind, target, label, fp, isBaselined));
        }
    }

    // satisfies: #240 (repowise-layered-architecture-and-bounded-cycle-gate)
    public static ArchitectureAuditResult AuditLayeredArchitectureAndCycles(
        IReadOnlyList<(string FromModule, string ToModule)> dependencyEdges,
        int maxCycles = 50)
    {
        int effectiveMaxCycles = Math.Clamp(maxCycles, 1, 50);
        List<LayerViolation> layerViolations = new List<LayerViolation>();
        Dictionary<string, List<string>> adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string rawFrom, string rawTo) in dependencyEdges)
        {
            string fromMod = (rawFrom ?? string.Empty).Replace('\\', '/').Trim();
            string toMod = (rawTo ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(fromMod) || string.IsNullOrWhiteSpace(toMod))
            {
                continue;
            }

            if (!adjacency.TryGetValue(fromMod, out List<string>? neighbors))
            {
                neighbors = new List<string>();
                adjacency[fromMod] = neighbors;
            }
            neighbors.Add(toMod);

            string? fromLayer = ClassifyArchitectureLayer(fromMod);
            string? toLayer = ClassifyArchitectureLayer(toMod);
            if (fromLayer == "offline_tools" && toLayer == "live_runtime")
            {
                layerViolations.Add(new LayerViolation("LAYER-001", fromLayer, toLayer, fromMod, toMod));
            }
            else if (fromLayer == "client_override" && toLayer == "server_internal")
            {
                layerViolations.Add(new LayerViolation("LAYER-002", fromLayer, toLayer, fromMod, toMod));
            }
            else if (fromLayer == "protocol_core" && toLayer == "mcp_cli")
            {
                layerViolations.Add(new LayerViolation("LAYER-003", fromLayer, toLayer, fromMod, toMod));
            }
        }

        List<string> detectedCycles = new List<string>();
        HashSet<string> canonicalCycleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> sortedNodes = adjacency.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (string startNode in sortedNodes)
        {
            if (detectedCycles.Count >= effectiveMaxCycles)
            {
                break;
            }

            Stack<(string Current, List<string> Path)> stack = new Stack<(string Current, List<string> Path)>();
            stack.Push((startNode, new List<string> { startNode }));

            while (stack.Count > 0 && detectedCycles.Count < effectiveMaxCycles)
            {
                (string current, List<string> path) = stack.Pop();
                if (!adjacency.TryGetValue(current, out List<string>? nextNodes))
                {
                    continue;
                }

                foreach (string nextNode in nextNodes)
                {
                    if (string.Equals(nextNode, startNode, StringComparison.OrdinalIgnoreCase))
                    {
                        string canonCycle = CanonicalizeCycle(path);
                        if (canonicalCycleSet.Add(canonCycle))
                        {
                            detectedCycles.Add(canonCycle);
                            if (detectedCycles.Count >= effectiveMaxCycles)
                            {
                                break;
                            }
                        }
                    }
                    else if (path.Count < 16 && !path.Contains(nextNode, StringComparer.OrdinalIgnoreCase))
                    {
                        List<string> nextPath = new List<string>(path) { nextNode };
                        stack.Push((nextNode, nextPath));
                    }
                }
            }
        }

        bool capReached = detectedCycles.Count >= effectiveMaxCycles;
        return new ArchitectureAuditResult(layerViolations, detectedCycles, capReached);
    }

    private static string? ClassifyArchitectureLayer(string modulePath)
    {
        if (modulePath.StartsWith("scripts/lib/", StringComparison.OrdinalIgnoreCase) ||
            modulePath.StartsWith("src/tools/ghidra/", StringComparison.OrdinalIgnoreCase))
        {
            return "offline_tools";
        }
        if (modulePath.StartsWith("native/proxies/native_guard/", StringComparison.OrdinalIgnoreCase))
        {
            return "live_runtime";
        }
        if (modulePath.StartsWith("games/client/override/", StringComparison.OrdinalIgnoreCase))
        {
            return "client_override";
        }
        if (modulePath.StartsWith("games/server/", StringComparison.OrdinalIgnoreCase))
        {
            return "server_internal";
        }
        if (modulePath.StartsWith("data/schemas/", StringComparison.OrdinalIgnoreCase) ||
            modulePath.StartsWith("src/core/protocol/", StringComparison.OrdinalIgnoreCase) ||
            modulePath.StartsWith("src/libs/protocol-core/", StringComparison.OrdinalIgnoreCase) ||
            modulePath.StartsWith("src/libs/contracts/", StringComparison.OrdinalIgnoreCase))
        {
            return "protocol_core";
        }
        if (modulePath.StartsWith("src/tools/kongor-cli/Mcp/", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp_cli";
        }
        return null;
    }

    private static string CanonicalizeCycle(List<string> cycleNodes)
    {
        int minIdx = 0;
        for (int i = 1; i < cycleNodes.Count; i++)
        {
            if (string.Compare(cycleNodes[i], cycleNodes[minIdx], StringComparison.OrdinalIgnoreCase) < 0)
            {
                minIdx = i;
            }
        }

        List<string> rotated = new List<string>(cycleNodes.Count + 1);
        for (int i = 0; i < cycleNodes.Count; i++)
        {
            rotated.Add(cycleNodes[(minIdx + i) % cycleNodes.Count]);
        }
        rotated.Add(rotated[0]);
        return string.Join(" -> ", rotated);
    }

    // satisfies: #241 (repowise-intermediate-commit-secret-scanner)
    public static string MaskSecretSnippet(string rawSecret)
    {
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            return "****";
        }

        string trimmed = rawSecret.Trim();
        if (trimmed.Length <= 8)
        {
            return string.Concat(trimmed.Substring(0, Math.Min(2, trimmed.Length)), "****");
        }

        int underscoreIdx = trimmed.IndexOf('_');
        string prefix = (underscoreIdx >= 2 && underscoreIdx <= 10)
            ? trimmed.Substring(0, underscoreIdx + 1)
            : trimmed.Substring(0, 4);
        string suffix = trimmed.Substring(trimmed.Length - 4);
        return string.Concat(prefix, "****", suffix);
    }

    public static IReadOnlyList<SecretScanFinding> ScanTwoPassSecrets(
        IReadOnlyDictionary<string, string> headFiles,
        IReadOnlyList<(string CommitHash, string FilePath, IReadOnlyList<string> AddedLines)> commitDiffHistory)
    {
        List<SecretScanFinding> findings = new List<SecretScanFinding>();
        HashSet<string> headSecretKeys = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: Scan current HEAD files
        foreach (KeyValuePair<string, string> kvp in headFiles)
        {
            string filePath = kvp.Key.Replace('\\', '/');
            string[] lines = (kvp.Value ?? string.Empty).Split(LineSplitSeparators, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("kongor-security-ignore", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach ((string ruleId, Regex pattern) in SecretPatterns)
                {
                    Match m = pattern.Match(line);
                    if (m.Success)
                    {
                        string rawVal = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                        string masked = MaskSecretSnippet(rawVal);
                        string key = string.Concat(ruleId, "|", filePath, "|", masked);
                        if (headSecretKeys.Add(key))
                        {
                            findings.Add(new SecretScanFinding(ruleId, filePath, i + 1, masked, true, "HEAD"));
                        }
                    }
                }
            }
        }

        // Pass 2: Scan intermediate commit diffs for secrets added and later deleted (present_in_head = false)
        HashSet<string> historyKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string commitHash, string rawFilePath, IReadOnlyList<string> addedLines) in commitDiffHistory)
        {
            string filePath = (rawFilePath ?? string.Empty).Replace('\\', '/');
            for (int i = 0; i < addedLines.Count; i++)
            {
                string line = addedLines[i];
                if (line.Contains("kongor-security-ignore", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach ((string ruleId, Regex pattern) in SecretPatterns)
                {
                    Match m = pattern.Match(line);
                    if (m.Success)
                    {
                        string rawVal = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                        string masked = MaskSecretSnippet(rawVal);
                        string key = string.Concat(ruleId, "|", filePath, "|", masked);
                        if (!headSecretKeys.Contains(key) && historyKeys.Add(key))
                        {
                            findings.Add(new SecretScanFinding(ruleId, filePath, i + 1, masked, false, commitHash));
                        }
                    }
                }
            }
        }

        return findings;
    }

    // satisfies: #242 (repowise-directional-cochange-coupling-detector)
    public static IReadOnlyList<DirectionalCoChangePair> ComputeDirectionalCoChange(
        IReadOnlyList<CoChangeCommit> history,
        IReadOnlySet<string> modifiedFiles,
        double tau = 500.0,
        double minConfidence = 0.80,
        int minSupport = 3,
        bool isShallowClone = false)
    {
        if (isShallowClone || history == null || history.Count == 0)
        {
            return Array.Empty<DirectionalCoChangePair>();
        }

        double effectiveTau = tau > 0.0 ? tau : 500.0;
        Dictionary<string, double> fileWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string A, string B), (double Weight, int Support)> pairStats = new Dictionary<(string A, string B), (double Weight, int Support)>();

        foreach (CoChangeCommit commit in history)
        {
            double age = Math.Max(0.0, commit.CommitAge);
            double weight = Math.Exp(-age / effectiveTau);
            List<string> distinctFiles = commit.TouchedFiles
                .Select(static f => (f ?? string.Empty).Replace('\\', '/').Trim())
                .Where(static f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string fileA in distinctFiles)
            {
                fileWeights[fileA] = fileWeights.TryGetValue(fileA, out double curW) ? curW + weight : weight;
                foreach (string fileB in distinctFiles)
                {
                    if (string.Equals(fileA, fileB, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    (string A, string B) pairKey = (fileA, fileB);
                    if (pairStats.TryGetValue(pairKey, out (double Weight, int Support) curPair))
                    {
                        pairStats[pairKey] = (curPair.Weight + weight, curPair.Support + 1);
                    }
                    else
                    {
                        pairStats[pairKey] = (weight, 1);
                    }
                }
            }
        }

        List<DirectionalCoChangePair> results = new List<DirectionalCoChangePair>();
        foreach (KeyValuePair<(string A, string B), (double Weight, int Support)> kvp in pairStats)
        {
            string fileA = kvp.Key.A;
            string fileB = kvp.Key.B;
            int support = kvp.Value.Support;
            if (support < minSupport)
            {
                continue;
            }

            double weightA = fileWeights.TryGetValue(fileA, out double wA) && wA > 0.0 ? wA : 1.0;
            double weightB = fileWeights.TryGetValue(fileB, out double wB) && wB > 0.0 ? wB : 1.0;
            double confAToB = Math.Round(kvp.Value.Weight / weightA, 4);
            double confBToA = Math.Round(kvp.Value.Weight / weightB, 4);
            bool aModified = modifiedFiles != null && modifiedFiles.Contains(fileA);
            bool bModified = modifiedFiles != null && modifiedFiles.Contains(fileB);
            bool unexplained = aModified && !bModified && confAToB >= minConfidence;

            results.Add(new DirectionalCoChangePair(fileA, fileB, confAToB, confBToA, support, unexplained));
        }

        return results
            .OrderByDescending(static r => r.ConfidenceAToB)
            .ThenBy(static r => r.FileA, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // satisfies: #243 (repowise-kamei-jit-diff-risk-and-churn-entropy)
    public static KameiJitRiskReport ComputeKameiJitDiffRisk(IReadOnlyList<DiffFileChurn> fileStats)
    {
        if (fileStats == null || fileStats.Count == 0)
        {
            return new KameiJitRiskReport(0, 0, 0, 0, 0, 0.0, 0.0, "LOW");
        }

        int nf = fileStats.Count;
        HashSet<string> subsystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalLa = 0;
        int totalLd = 0;
        List<int> fileChurns = new List<int>(nf);

        foreach (DiffFileChurn item in fileStats)
        {
            string normPath = (item.FilePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            int la = Math.Max(0, item.LinesAdded);
            int ld = Math.Max(0, item.LinesDeleted);
            int churn = la + ld;
            totalLa += la;
            totalLd += ld;
            if (churn > 0)
            {
                fileChurns.Add(churn);
            }

            string[] segs = normPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string sub = segs.Length >= 2 ? string.Concat(segs[0], "/", segs[1]) : (segs.Length == 1 ? segs[0] : "root");
            subsystems.Add(sub);
        }

        int ns = subsystems.Count;
        int lt = totalLa + totalLd;
        double normalizedEntropy = 0.0;
        if (nf > 1 && lt > 0 && fileChurns.Count > 1)
        {
            double rawH = 0.0;
            foreach (int ck in fileChurns)
            {
                double pk = (double)ck / lt;
                if (pk > 0.0)
                {
                    rawH -= pk * (Math.Log(pk) / Math.Log(2.0));
                }
            }

            double denom = Math.Log(nf) / Math.Log(2.0);
            if (denom > 0.0)
            {
                normalizedEntropy = Math.Clamp(rawH / denom, 0.0, 1.0);
            }
        }

        double entropyRounded = Math.Round(normalizedEntropy, 4);
        double rawScore = Math.Min(30.0, nf * 3.0) +
                          Math.Min(25.0, ns * 6.0) +
                          Math.Min(25.0, lt / 20.0) +
                          (entropyRounded * 20.0);
        double riskScore = Math.Round(Math.Min(100.0, rawScore), 2);
        string riskLevel = riskScore >= 75.0 ? "CRITICAL" : (riskScore >= 50.0 ? "HIGH" : (riskScore >= 25.0 ? "MEDIUM" : "LOW"));

        return new KameiJitRiskReport(nf, ns, totalLa, totalLd, lt, entropyRounded, riskScore, riskLevel);
    }

    // satisfies: #244 (repowise-decayed-blast-radius-and-test-ranker)
    public static BlastRadiusReport ComputeDecayedBlastRadius(
        IReadOnlyList<string> seedFiles,
        IReadOnlyList<(string Caller, string Callee)> dependencyEdges,
        double decayFactor = 0.6,
        int maxDepth = 6)
    {
        double clampedDecay = Math.Clamp(decayFactor, 0.1, 0.99);
        int clampedDepth = Math.Clamp(maxDepth, 1, 12);

        Dictionary<string, List<string>> reverseAdj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawCaller, string rawCallee) in dependencyEdges)
        {
            string caller = (rawCaller ?? string.Empty).Replace('\\', '/').Trim();
            string callee = (rawCallee ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(callee))
            {
                continue;
            }

            if (!reverseAdj.TryGetValue(callee, out List<string>? callers))
            {
                callers = new List<string>();
                reverseAdj[callee] = callers;
            }
            callers.Add(caller);
        }

        Dictionary<string, double> cumulativeImpact = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> minHopDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawSeed in seedFiles)
        {
            string seed = (rawSeed ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(seed))
            {
                continue;
            }

            Dictionary<string, int> visitedFromSeed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [seed] = 0
            };
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                int dist = visitedFromSeed[current];
                double contrib = Math.Pow(clampedDecay, dist);
                cumulativeImpact[current] = cumulativeImpact.TryGetValue(current, out double prevScore) ? prevScore + contrib : contrib;

                if (!minHopDist.TryGetValue(current, out int prevHop) || dist < prevHop)
                {
                    minHopDist[current] = dist;
                }

                if (dist >= clampedDepth || !reverseAdj.TryGetValue(current, out List<string>? dependents))
                {
                    continue;
                }

                foreach (string dep in dependents)
                {
                    if (!visitedFromSeed.ContainsKey(dep))
                    {
                        visitedFromSeed[dep] = dist + 1;
                        queue.Enqueue(dep);
                    }
                }
            }
        }

        List<BlastRadiusEntry> allEntries = cumulativeImpact
            .Select(kvp =>
            {
                string path = kvp.Key;
                bool isTest = path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
                              path.Contains("test_", StringComparison.OrdinalIgnoreCase);
                return new BlastRadiusEntry(path, minHopDist[path], Math.Round(kvp.Value, 4), isTest);
            })
            .OrderByDescending(static e => e.ImpactScore)
            .ThenBy(static e => e.MinHopDistance)
            .ThenBy(static e => e.ModulePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<BlastRadiusEntry> rankedTests = allEntries.Where(static e => e.IsTest).ToList();
        return new BlastRadiusReport(seedFiles, clampedDecay, allEntries, rankedTests);
    }

    // satisfies: #245 (repowise-symbol-contract-impact-classifier)
    public static IReadOnlyList<SymbolDeclaration> ExtractSymbolContracts(string filePath, string sourceCode)
    {
        List<SymbolDeclaration> symbols = new List<SymbolDeclaration>();
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return symbols;
        }

        string ext = (filePath ?? string.Empty).ToLowerInvariant();
        if (ext.EndsWith(".ps1", StringComparison.Ordinal))
        {
            MatchCollection psMatches = Regex.Matches(sourceCode, @"(?m)^\s*function\s+([A-Za-z0-9_-]+)\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}");
            foreach (Match m in psMatches)
            {
                string name = m.Groups[1].Value.Trim();
                string body = m.Groups[2].Value.Trim();
                int reqParams = Regex.Count(body, @"(?i)Mandatory\s*=\s*\$true");
                symbols.Add(new SymbolDeclaration(name, string.Concat("function ", name, "(min=", reqParams, ")"), body, reqParams));
            }
        }
        else if (ext.EndsWith(".lua", StringComparison.Ordinal))
        {
            MatchCollection luaMatches = Regex.Matches(sourceCode, @"(?ms)^\s*function\s+([A-Za-z0-9_.:]+)\s*\(([^)]*)\)(.*?)\bend\b");
            foreach (Match m in luaMatches)
            {
                string name = m.Groups[1].Value.Trim();
                string rawParams = m.Groups[2].Value.Trim();
                string body = m.Groups[3].Value.Trim();
                int arity = string.IsNullOrWhiteSpace(rawParams) ? 0 : rawParams.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
                symbols.Add(new SymbolDeclaration(name, string.Concat("function ", name, "(", rawParams, ")"), body, arity));
            }
        }
        else
        {
            MatchCollection cMatches = Regex.Matches(
                sourceCode,
                @"(?m)(?:^|[;{])\s*(?:public\s+|exported\s+|extern\s+)?(?:static\s+|virtual\s+|async\s+|inline\s+)*([A-Za-z0-9_<>,\[\]\s*&]+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}");
            foreach (Match m in cMatches)
            {
                string retType = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
                if (retType.StartsWith("class ", StringComparison.Ordinal) || retType.StartsWith("struct ", StringComparison.Ordinal))
                {
                    continue;
                }
                string name = m.Groups[2].Value.Trim();
                if (name is "if" or "for" or "foreach" or "while" or "switch" or "catch")
                {
                    continue;
                }
                string rawParams = Regex.Replace(m.Groups[3].Value.Trim(), @"\s+", " ");
                string body = m.Groups[4].Value.Trim();
                int minArity = 0;
                if (!string.IsNullOrWhiteSpace(rawParams))
                {
                    string[] parts = rawParams.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in parts)
                    {
                        if (!p.Contains('='))
                        {
                            minArity++;
                        }
                    }
                }
                symbols.Add(new SymbolDeclaration(name, string.Concat(retType, " ", name, "(", rawParams, ")"), body, minArity));
            }
        }

        return symbols;
    }

    public static IReadOnlyList<SymbolImpactFinding> ClassifySymbolContractImpact(
        IReadOnlyList<SymbolDeclaration> baselineSymbols,
        IReadOnlyList<SymbolDeclaration> currentSymbols,
        IReadOnlyList<SymbolCallerSite> callerSites)
    {
        Dictionary<string, SymbolDeclaration> baseMap = baselineSymbols.ToDictionary(static s => s.Name, StringComparer.Ordinal);
        Dictionary<string, SymbolDeclaration> currMap = currentSymbols.ToDictionary(static s => s.Name, StringComparer.Ordinal);
        List<SymbolImpactFinding> findings = new List<SymbolImpactFinding>();

        foreach (KeyValuePair<string, SymbolDeclaration> kvp in currMap)
        {
            string name = kvp.Key;
            SymbolDeclaration curr = kvp.Value;
            if (!baseMap.TryGetValue(name, out SymbolDeclaration? prev) || prev == null)
            {
                findings.Add(new SymbolImpactFinding(name, SymbolChangeKind.Added, false, Array.Empty<string>()));
            }
            else if (!string.Equals(prev.Signature, curr.Signature, StringComparison.Ordinal) || prev.MinArity != curr.MinArity)
            {
                List<string> brokenCallers = callerSites
                    .Where(c => string.Equals(c.SymbolName, name, StringComparison.Ordinal) &&
                                !c.WasUpdatedInDiff &&
                                (c.ArgCount < curr.MinArity ||
                                 prev.MinArity == curr.MinArity ||
                                 (prev.MinArity > curr.MinArity && c.ArgCount > curr.MinArity)))
                    .Select(static c => c.CallerFile)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                findings.Add(new SymbolImpactFinding(name, SymbolChangeKind.SignatureChanged, brokenCallers.Count > 0, brokenCallers));
            }
            else if (!string.Equals(prev.Body, curr.Body, StringComparison.Ordinal))
            {
                findings.Add(new SymbolImpactFinding(name, SymbolChangeKind.BodyChanged, false, Array.Empty<string>()));
            }
        }

        foreach (KeyValuePair<string, SymbolDeclaration> kvp in baseMap)
        {
            string name = kvp.Key;
            if (!currMap.ContainsKey(name))
            {
                List<string> brokenCallers = callerSites
                    .Where(c => string.Equals(c.SymbolName, name, StringComparison.Ordinal) && !c.WasUpdatedInDiff)
                    .Select(static c => c.CallerFile)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                findings.Add(new SymbolImpactFinding(name, SymbolChangeKind.Removed, brokenCallers.Count > 0, brokenCallers));
            }
        }

        return findings;
    }

    // satisfies: #246 (repowise-semantic-protocol-breaking-change-guard)
    public static IReadOnlyList<ProtocolWireBreakFinding> VerifyProtocolSemanticWireCompatibility(
        IReadOnlyList<ProtocolMessageSpec> baselineMessages,
        IReadOnlyList<ProtocolMessageSpec> candidateMessages)
    {
        List<ProtocolWireBreakFinding> breaks = new List<ProtocolWireBreakFinding>();
        Dictionary<string, ProtocolMessageSpec> candMap = candidateMessages.ToDictionary(static m => m.Name, StringComparer.Ordinal);

        foreach (ProtocolMessageSpec baseMsg in baselineMessages)
        {
            if (!candMap.TryGetValue(baseMsg.Name, out ProtocolMessageSpec? candMsg) || candMsg == null)
            {
                breaks.Add(new ProtocolWireBreakFinding("REMOVED_ENDPOINT", baseMsg.Name, "*", $"Message/endpoint '{baseMsg.Name}' was removed."));
                continue;
            }

            if (!string.Equals(baseMsg.Direction, candMsg.Direction, StringComparison.Ordinal))
            {
                breaks.Add(new ProtocolWireBreakFinding("DIRECTION_CHANGED", baseMsg.Name, "*", $"Direction changed from '{baseMsg.Direction}' to '{candMsg.Direction}'."));
            }

            Dictionary<string, ProtocolFieldSpec> baseFieldsByName = baseMsg.Fields.ToDictionary(static f => f.Name, StringComparer.Ordinal);
            Dictionary<int, ProtocolFieldSpec> baseFieldsByTag = baseMsg.Fields.ToDictionary(static f => f.Tag);
            Dictionary<string, ProtocolFieldSpec> candFieldsByName = candMsg.Fields.ToDictionary(static f => f.Name, StringComparer.Ordinal);
            Dictionary<int, ProtocolFieldSpec> candFieldsByTag = candMsg.Fields.ToDictionary(static f => f.Tag);

            foreach (ProtocolFieldSpec bf in baseMsg.Fields)
            {
                if (!candFieldsByName.TryGetValue(bf.Name, out ProtocolFieldSpec? cf) || cf == null)
                {
                    if (candFieldsByTag.TryGetValue(bf.Tag, out ProtocolFieldSpec? tagHolder) && tagHolder != null)
                    {
                        breaks.Add(new ProtocolWireBreakFinding("REUSED_FIELD_TAG", baseMsg.Name, tagHolder.Name, $"Tag {bf.Tag} from '{bf.Name}' reused by '{tagHolder.Name}'."));
                    }
                    else
                    {
                        breaks.Add(new ProtocolWireBreakFinding("REMOVED_FIELD", baseMsg.Name, bf.Name, $"Field '{bf.Name}' (tag {bf.Tag}) was removed."));
                    }
                    continue;
                }

                string normBaseType = Regex.Replace(bf.Type ?? string.Empty, @"\s+", string.Empty);
                string normCandType = Regex.Replace(cf.Type ?? string.Empty, @"\s+", string.Empty);
                if (!string.Equals(normBaseType, normCandType, StringComparison.Ordinal))
                {
                    breaks.Add(new ProtocolWireBreakFinding("RETYPED_FIELD", baseMsg.Name, bf.Name, $"Field '{bf.Name}' changed wire type from '{bf.Type}' to '{cf.Type}'."));
                }

                if (bf.Tag != cf.Tag)
                {
                    breaks.Add(new ProtocolWireBreakFinding("REUSED_FIELD_TAG", baseMsg.Name, bf.Name, $"Field '{bf.Name}' changed wire tag from {bf.Tag} to {cf.Tag}."));
                }
            }

            foreach (ProtocolFieldSpec cf in candMsg.Fields)
            {
                if (!baseFieldsByName.ContainsKey(cf.Name))
                {
                    if (baseFieldsByTag.TryGetValue(cf.Tag, out ProtocolFieldSpec? oldTagOwner) && oldTagOwner != null)
                    {
                        breaks.Add(new ProtocolWireBreakFinding("REUSED_FIELD_TAG", baseMsg.Name, cf.Name, $"New field '{cf.Name}' reuses tag {cf.Tag} from '{oldTagOwner.Name}'."));
                    }
                    if (cf.Required)
                    {
                        breaks.Add(new ProtocolWireBreakFinding("NEW_REQUIRED_FIELD", baseMsg.Name, cf.Name, $"New field '{cf.Name}' is marked required, breaking older senders."));
                    }
                }
            }
        }

        return breaks;
    }

    // satisfies: #247 (repowise-safe-asset-deletion-runtime-load-gate)
    public static IReadOnlyList<AssetRuntimeReference> ScanAssetRuntimeReferences(
        IReadOnlyList<string> candidateAssets,
        IReadOnlyDictionary<string, string> runtimeFiles)
    {
        List<AssetRuntimeReference> refs = new List<AssetRuntimeReference>();
        foreach (string rawAsset in candidateAssets)
        {
            string normAsset = (rawAsset ?? string.Empty).Replace('\\', '/').TrimStart('/');
            int slashIdx = normAsset.LastIndexOf('/');
            string fileName = slashIdx >= 0 ? normAsset.Substring(slashIdx + 1) : normAsset;
            int dotIdx = fileName.LastIndexOf('.');
            string stem = dotIdx > 0 ? fileName.Substring(0, dotIdx) : fileName;

            Regex luaDynRegex = new Regex(
                $@"(?i)\b(loadfile|dofile|DoFile|Import|RegisterTexture|GetTexture|GetWidget)\s*\([^)\r\n]*?(?:{Regex.Escape(fileName)}|{Regex.Escape(stem)})",
                RegexOptions.Compiled);
            Regex xmlAttrRegex = new Regex(
                $@"(?i)\b(texture|file|model|material|script|src)\s*=\s*[""'][^""'\r\n]*?(?:{Regex.Escape(fileName)}|{Regex.Escape(stem)}\.(?:dds|tga|png|lua|xml|interface|package|mdf|model|effect))[""']",
                RegexOptions.Compiled);

            foreach (KeyValuePair<string, string> kvp in runtimeFiles)
            {
                string rFile = kvp.Key.Replace('\\', '/');
                string content = kvp.Value ?? string.Empty;

                Match lm = luaDynRegex.Match(content);
                if (lm.Success)
                {
                    refs.Add(new AssetRuntimeReference(normAsset, rFile, string.Concat("LUA_", lm.Groups[1].Value.ToUpperInvariant()), lm.Value.Trim()));
                }

                Match xm = xmlAttrRegex.Match(content);
                if (xm.Success)
                {
                    refs.Add(new AssetRuntimeReference(normAsset, rFile, string.Concat("XML_", xm.Groups[1].Value.ToUpperInvariant()), xm.Value.Trim()));
                }
            }
        }

        return refs;
    }

    // satisfies: #248 (repowise-hotloop-code-health-auditor)
    public static IReadOnlyList<HotLoopViolation> AuditHotLoopCodeHealth(IReadOnlyDictionary<string, string> sourceFiles)
    {
        List<HotLoopViolation> violations = new List<HotLoopViolation>();
        foreach (KeyValuePair<string, string> kvp in sourceFiles)
        {
            string filePath = kvp.Key.Replace('\\', '/');
            string content = kvp.Value ?? string.Empty;
            bool isCpp = filePath.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
                         filePath.EndsWith(".h", StringComparison.OrdinalIgnoreCase);
            bool isCs = filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<string> loopBodies = ExtractLoopBodies(content);
            foreach (string loopBody in loopBodies)
            {
                if (loopBody.Contains("kongor-hotloop-ignore", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string cleanBody = Regex.Replace(Regex.Replace(loopBody, @"(?s)/\*.*?\*/", string.Empty), @"(?m)//.*$", string.Empty);
                if (isCpp)
                {
                    if (Regex.IsMatch(cleanBody, @"\bstd::(?:w)?regex\b"))
                    {
                        violations.Add(new HotLoopViolation("CPP-HOTLOOP-REGEX", filePath, "std::regex construction inside hot loop."));
                    }
                    if (Regex.IsMatch(cleanBody, @"\bstd::[io]?fstream\b|\bfopen\s*\("))
                    {
                        violations.Add(new HotLoopViolation("CPP-HOTLOOP-SYNC-IO", filePath, "Synchronous file stream open inside hot loop."));
                    }
                    if (Regex.IsMatch(cleanBody, @"\bstd::this_thread::sleep_for\b|\bSleep\s*\("))
                    {
                        violations.Add(new HotLoopViolation("CPP-HOTLOOP-SLEEP", filePath, "Thread sleep inside hot loop."));
                    }
                }
                else if (isCs)
                {
                    if (Regex.IsMatch(cleanBody, @"\.(?:SaveChangesAsync|SaveChanges|ExecuteSqlRawAsync)\s*\("))
                    {
                        violations.Add(new HotLoopViolation("CS-HOTLOOP-EF-SAVE", filePath, "EntityFramework SaveChanges/SaveChangesAsync inside loop."));
                    }
                }
            }
        }

        return violations;
    }

    private static IReadOnlyList<string> ExtractLoopBodies(string source)
    {
        List<string> bodies = new List<string>();
        if (string.IsNullOrWhiteSpace(source))
        {
            return bodies;
        }

        MatchCollection headerMatches = Regex.Matches(source, @"\b(?:for|foreach|while)\s*\(");
        foreach (Match hm in headerMatches)
        {
            int openParenIdx = hm.Index + hm.Length - 1;
            int parenDepth = 0;
            int bodyStartBraceIdx = -1;

            for (int i = openParenIdx; i < source.Length; i++)
            {
                char ch = source[i];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        int j = i + 1;
                        while (j < source.Length && char.IsWhiteSpace(source[j]))
                        {
                            j++;
                        }
                        if (j < source.Length && source[j] == '{')
                        {
                            bodyStartBraceIdx = j;
                        }
                        break;
                    }
                }
            }

            if (bodyStartBraceIdx < 0)
            {
                continue;
            }

            string extracted = SliceBalancedOrPreprocessorLoopBody(source, bodyStartBraceIdx);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                bodies.Add(extracted);
            }
        }

        return bodies;
    }

    private static string SliceBalancedOrPreprocessorLoopBody(string source, int openBraceIdx)
    {
        string remainder = source.Substring(openBraceIdx + 1);
        string[] lines = remainder.Split(LineSplitSeparators, StringSplitOptions.None);
        int braceDepth = 1;
        Stack<int> preprocDepthStack = new Stack<int>();
        StringBuilder sb = new StringBuilder();

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            string line = lines[lineIdx];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#if", StringComparison.Ordinal))
            {
                preprocDepthStack.Push(braceDepth);
                sb.AppendLine(line);
                continue;
            }
            if (trimmed.StartsWith("#else", StringComparison.Ordinal) || trimmed.StartsWith("#elif", StringComparison.Ordinal))
            {
                if (preprocDepthStack.Count > 0)
                {
                    braceDepth = preprocDepthStack.Peek();
                }
                sb.AppendLine(line);
                continue;
            }
            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (preprocDepthStack.Count > 0)
                {
                    _ = preprocDepthStack.Pop();
                }
                sb.AppendLine(line);
                continue;
            }

            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/')
                {
                    sb.Append(line.AsSpan(c));
                    break;
                }
                if (ch == '{')
                {
                    braceDepth++;
                }
                else if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        return sb.ToString();
                    }
                }
                sb.Append(ch);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // satisfies: #249 (repowise-stale-and-missing-test-detector)
    public static DiffTestSignalReport EvaluateDiffTestCoverageSignal(
        IReadOnlyList<string> modifiedProductionFiles,
        IReadOnlyList<string> modifiedTestFiles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> testReferencedTargets,
        IReadOnlySet<string> existingTargets)
    {
        List<string> missingSignalFiles = (modifiedProductionFiles != null && modifiedProductionFiles.Count > 0 && (modifiedTestFiles == null || modifiedTestFiles.Count == 0))
            ? modifiedProductionFiles.ToList()
            : new List<string>();

        List<string> staleTests = new List<string>();
        foreach (KeyValuePair<string, IReadOnlyList<string>> kvp in testReferencedTargets)
        {
            string testFile = kvp.Key;
            foreach (string target in kvp.Value)
            {
                if (!existingTargets.Contains(target))
                {
                    staleTests.Add(string.Concat(testFile, " -> ", target));
                }
            }
        }

        return new DiffTestSignalReport(
            missingSignalFiles,
            staleTests,
            missingSignalFiles.Count > 0,
            staleTests.Count > 0);
    }

    // satisfies: #250 (repowise-kb-provenance-ladder-and-quote-verifier)
    public static int GetProvenanceRank(KbProvenanceTier tier)
    {
        return (int)tier;
    }

    public static KbVerificationResult VerifyKbProvenanceAndQuotes(
        IReadOnlyList<KbFieldNoteClaim> notes,
        IReadOnlyDictionary<string, string> sourceFiles)
    {
        List<string> errors = new List<string>();
        bool allQuotesVerified = true;
        bool allSupersessionsAllowed = true;
        bool hasContradiction = false;

        Dictionary<string, KbFieldNoteClaim> noteById = notes.ToDictionary(static n => n.NoteId, StringComparer.OrdinalIgnoreCase);
        HashSet<string> supersededIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (KbFieldNoteClaim note in notes)
        {
            if (!string.IsNullOrWhiteSpace(note.EvidenceSourcePath) || !string.IsNullOrWhiteSpace(note.EvidenceQuote))
            {
                if (string.IsNullOrWhiteSpace(note.EvidenceSourcePath) ||
                    string.IsNullOrWhiteSpace(note.EvidenceQuote) ||
                    !sourceFiles.TryGetValue(note.EvidenceSourcePath, out string? srcText) ||
                    srcText == null)
                {
                    allQuotesVerified = false;
                    errors.Add($"{note.NoteId}: Missing evidence source file '{note.EvidenceSourcePath}'.");
                }
                else
                {
                    string normSrc = Regex.Replace(srcText.Trim(), @"\s+", " ");
                    string normQuote = Regex.Replace(note.EvidenceQuote.Trim(), @"\s+", " ");
                    if (!srcText.Contains(note.EvidenceQuote, StringComparison.Ordinal) &&
                        !normSrc.Contains(normQuote, StringComparison.Ordinal))
                    {
                        allQuotesVerified = false;
                        errors.Add($"{note.NoteId}: Evidence quote not found verbatim in '{note.EvidenceSourcePath}'.");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(note.SupersedesId) &&
                noteById.TryGetValue(note.SupersedesId, out KbFieldNoteClaim? targetNote) &&
                targetNote != null)
            {
                if (GetProvenanceRank(note.ProvenanceTier) < GetProvenanceRank(targetNote.ProvenanceTier))
                {
                    allSupersessionsAllowed = false;
                    errors.Add($"{note.NoteId} ({note.ProvenanceTier}) cannot supersede higher-authority note {targetNote.NoteId} ({targetNote.ProvenanceTier}).");
                }
                else
                {
                    supersededIds.Add(targetNote.NoteId);
                }
            }
        }

        Dictionary<string, KbFieldNoteClaim> activeClaims = new Dictionary<string, KbFieldNoteClaim>(StringComparer.OrdinalIgnoreCase);
        foreach (KbFieldNoteClaim note in notes)
        {
            if (supersededIds.Contains(note.NoteId) || string.IsNullOrWhiteSpace(note.ClaimKey))
            {
                continue;
            }

            string compositeKey = string.Concat(note.BinaryOrSubsystem, "|", note.ClaimKey);
            if (activeClaims.TryGetValue(compositeKey, out KbFieldNoteClaim? existing) && existing != null)
            {
                if (!string.Equals(existing.ClaimValue, note.ClaimValue, StringComparison.OrdinalIgnoreCase))
                {
                    hasContradiction = true;
                    errors.Add($"Contradictory active claim on ({note.BinaryOrSubsystem}, {note.ClaimKey}): {existing.NoteId}='{existing.ClaimValue}' vs {note.NoteId}='{note.ClaimValue}'.");
                }
            }
            else
            {
                activeClaims[compositeKey] = note;
            }
        }

        return new KbVerificationResult(
            errors.Count == 0,
            allQuotesVerified,
            allSupersessionsAllowed,
            hasContradiction,
            errors);
    }

    // satisfies: #251 (autoresearch-keep-discard-metric-ratchet)
    public static AutoRatchetEvaluation EvaluateAutoRatchetRun(
        string commitShort,
        double baselineMs,
        double candidateMs,
        double memoryMb,
        int linesAdded,
        int linesDeleted,
        double elapsedSeconds,
        int exitCode,
        string errorOutput,
        string description,
        double budgetSeconds = 30.0,
        double hardTimeoutSeconds = 60.0)
    {
        string cleanCommit = string.IsNullOrWhiteSpace(commitShort) ? "0000000" : commitShort.Trim();
        string cleanDesc = (description ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (elapsedSeconds > hardTimeoutSeconds)
        {
            string row = FormatResultsTsvRow(cleanCommit, 0.0, 0.0, "crash", string.Concat(cleanDesc, " [hard_timeout_kill]"));
            return new AutoRatchetEvaluation(AutoRatchetDecisionKind.HardTimeoutDiscard, false, false, "crash", "Exceeded 60s hard timeout.", row);
        }

        if (exitCode != 0)
        {
            bool isTypo = !string.IsNullOrWhiteSpace(errorOutput) &&
                          Regex.IsMatch(errorOutput, @"(?i)(?:CS\d{4}|ParserError|SyntaxError|Missing closing|Unexpected token|ParameterBindingException)");
            string suffix = isTypo ? " [typo_syntax_error]" : " [broken_idea_crash]";
            string row = FormatResultsTsvRow(cleanCommit, 0.0, 0.0, "crash", string.Concat(cleanDesc, suffix));
            AutoRatchetDecisionKind kind = isTypo ? AutoRatchetDecisionKind.RetryableTypoCrash : AutoRatchetDecisionKind.FatalCrashDiscard;
            return new AutoRatchetEvaluation(kind, false, isTypo, "crash", isTypo ? "Retryable syntax/typo error." : "Non-retryable runtime crash.", row);
        }

        double deltaMs = candidateMs - baselineMs;
        double pctChange = baselineMs > 0.0 ? (deltaMs / baselineMs) * 100.0 : 0.0;
        int netLinesDelta = linesAdded - linesDeleted;

        if (netLinesDelta < 0 && deltaMs <= 0.0)
        {
            string row = FormatResultsTsvRow(cleanCommit, candidateMs, memoryMb, "keep", string.Concat(cleanDesc, " [simplicity_deletion_keep]"));
            return new AutoRatchetEvaluation(AutoRatchetDecisionKind.SimplicityDeletionKeep, true, false, "keep", "Equal or better latency with net code deletion.", row);
        }

        if (deltaMs < 0.0)
        {
            if (Math.Abs(pctChange) < 0.5 && netLinesDelta > 25)
            {
                string row = FormatResultsTsvRow(cleanCommit, candidateMs, memoryMb, "discard", string.Concat(cleanDesc, " [complexity_bloat_discard]"));
                return new AutoRatchetEvaluation(AutoRatchetDecisionKind.ComplexityBloatDiscard, false, false, "discard", "Marginal (<0.5%) improvement not worth >25 lines of added complexity.", row);
            }

            string keepRow = FormatResultsTsvRow(cleanCommit, candidateMs, memoryMb, "keep", string.Concat(cleanDesc, " [improved_keep]"));
            return new AutoRatchetEvaluation(AutoRatchetDecisionKind.ImprovedKeep, true, false, "keep", "Candidate improved primary latency metric.", keepRow);
        }

        string discardRow = FormatResultsTsvRow(cleanCommit, candidateMs, memoryMb, "discard", string.Concat(cleanDesc, " [regressed_discard]"));
        return new AutoRatchetEvaluation(AutoRatchetDecisionKind.RegressedDiscard, false, false, "discard", "Candidate did not improve primary latency metric.", discardRow);
    }

    public static string FormatResultsTsvRow(string commit, double metricMs, double memoryMb, string status, string description)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}\t{1:F3}\t{2:F1}\t{3}\t{4}",
            commit,
            metricMs,
            memoryMb,
            status,
            description);
    }

    // satisfies: #252 (repowise-shallow-clone-fallback-and-symbol-cache)
    public static ShallowCloneStatus EvaluateShallowCloneFallback(int commitCount, bool hasShallowFile = false, int minCommitThreshold = 50)
    {
        bool isShallow = hasShallowFile || commitCount < minCommitThreshold;
        string mode = isShallow ? "shallow_clone_graceful_skip" : "full_git_history";
        return new ShallowCloneStatus(isShallow, commitCount, mode);
    }
}
