// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class RepoWiseAndAutoResearchWave4Tests
{
    // =========================================================================
    // #239: Multi-Kind Doc-Drift Fingerprint Baseline (PATH, ANCHOR, COMMAND, SYMBOL)
    // =========================================================================

    [Fact]
    public void DocDriftFingerprint_IsLabelAndLineInvariant_AndKindSpecific()
    {
        string fp1 = RepoWiseIntelligenceService.ComputeDocDriftFingerprint(
            "docs/reference/guide.md",
            DocDriftKind.Path,
            "scripts/missing_tool.ps1");
        string fp2 = RepoWiseIntelligenceService.ComputeDocDriftFingerprint(
            "docs\\reference\\guide.md",
            DocDriftKind.Path,
            "scripts\\missing_tool.ps1");
        string fpAnchor = RepoWiseIntelligenceService.ComputeDocDriftFingerprint(
            "docs/reference/guide.md",
            DocDriftKind.Anchor,
            "scripts/missing_tool.ps1");

        fp1.Should().HaveLength(32);
        fp1.Should().Be(fp2);
        fp1.Should().NotBe(fpAnchor);
    }

    [Fact]
    public void AnalyzeMarkdownDocDrift_DetectsAllFourDriftKinds_AndHonorsBaseline()
    {
        string md = @"
# Architecture Overview
See [Missing Script](scripts/nonexistent.ps1) and [Broken Section](docs/other.md#missing-heading).
Also check [symbol:DeletedNativeHook](src/Engine.cs).
Run `powershell -File scripts/gate_check.ps1 -NonExistentFakeFlag999`
";
        HashSet<string> existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "docs/other.md",
            "src/Engine.cs",
            "scripts/gate_check.ps1"
        };
        Dictionary<string, IReadOnlySet<string>> slugs = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs/other.md"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "valid-heading" }
        };
        Dictionary<string, IReadOnlySet<string>> flags = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["scripts/gate_check.ps1"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Full", "TargetRoot", "JsonOutput" }
        };
        Dictionary<string, IReadOnlySet<string>> symbols = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Engine.cs"] = new HashSet<string>(StringComparer.Ordinal) { "ExistingNativeHook" }
        };

        string baselinedPathFp = RepoWiseIntelligenceService.ComputeDocDriftFingerprint(
            "docs/arch.md",
            DocDriftKind.Path,
            "scripts/nonexistent.ps1");
        HashSet<string> baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baselinedPathFp };

        IReadOnlyList<DocDriftFinding> findings = RepoWiseIntelligenceService.AnalyzeMarkdownDocDrift(
            "docs/arch.md",
            md,
            existingFiles,
            slugs,
            flags,
            symbols,
            baseline);

        findings.Should().HaveCount(4);
        findings.Should().ContainSingle(f => f.Kind == DocDriftKind.Path && f.IsBaselined);
        findings.Should().ContainSingle(f => f.Kind == DocDriftKind.Anchor && !f.IsBaselined && f.Target == "docs/other.md#missing-heading");
        findings.Should().ContainSingle(f => f.Kind == DocDriftKind.Symbol && !f.IsBaselined && f.Target == "src/Engine.cs#DeletedNativeHook");
        findings.Should().ContainSingle(f => f.Kind == DocDriftKind.Command && !f.IsBaselined && f.Target == "scripts/gate_check.ps1 -NonExistentFakeFlag999");
    }

    // =========================================================================
    // #240: Layered Architecture Conformance & Bounded DFS Cycle Gate (MAX_CYCLES = 50)
    // =========================================================================

    [Fact]
    public void AuditLayeredArchitectureAndCycles_DetectsForbiddenLayerEdges_AndCapsCyclesAt50()
    {
        List<(string FromModule, string ToModule)> edges = new List<(string FromModule, string ToModule)>
        {
            ("scripts/lib/offline_parser.py", "native/proxies/native_guard/dllmain.cpp"),
            ("games/client/override/ui/main.lua", "games/server/game_rules.lua"),
            ("src/core/protocol/PacketSpec.cs", "src/tools/kongor-cli/Mcp/McpIpcServer.cs"),
            ("src/ModA.cs", "src/ModB.cs"),
            ("src/ModB.cs", "src/ModC.cs"),
            ("src/ModC.cs", "src/ModA.cs")
        };

        ArchitectureAuditResult result = RepoWiseIntelligenceService.AuditLayeredArchitectureAndCycles(edges, maxCycles: 50);

        result.LayerViolations.Should().HaveCount(3);
        result.LayerViolations.Select(static v => v.RuleId).Should().BeEquivalentTo(new string[] { "LAYER-001", "LAYER-002", "LAYER-003" });
        result.DetectedCycles.Should().ContainSingle().Which.Should().Be("src/ModA.cs -> src/ModB.cs -> src/ModC.cs -> src/ModA.cs");
        result.CycleCapReached.Should().BeFalse();
    }

    [Fact]
    public void AuditLayeredArchitectureAndCycles_EnforcesMaxCycles50CapOnDenseGraph()
    {
        List<(string FromModule, string ToModule)> denseEdges = new List<(string FromModule, string ToModule)>();
        for (int i = 0; i < 12; i++)
        {
            for (int j = 0; j < 12; j++)
            {
                if (i != j)
                {
                    denseEdges.Add(($"src/Node{i:D2}.cs", $"src/Node{j:D2}.cs"));
                }
            }
        }

        ArchitectureAuditResult result = RepoWiseIntelligenceService.AuditLayeredArchitectureAndCycles(denseEdges, maxCycles: 50);

        result.DetectedCycles.Should().HaveCount(50);
        result.CycleCapReached.Should().BeTrue();
    }

    // =========================================================================
    // #241: Two-Pass Intermediate-Commit Secret Scanner & Masked Snippets
    // =========================================================================

    [Fact]
    public void ScanTwoPassSecrets_FlagsHeadAndDeletedIntermediateCommitSecrets_AndHonorsInlineIgnore()
    {
        string rawGithubPat = "ghp_1234567890abcdefghijklmnopqrstuvwxyz";
        string masked = RepoWiseIntelligenceService.MaskSecretSnippet(rawGithubPat);
        masked.Should().Be("ghp_****wxyz");

        Dictionary<string, string> headFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/Config.cs"] = $"string k = \"{rawGithubPat}\";\nstring ok = \"AKIA1234567890ABCDEF\"; // kongor-security-ignore"
        };

        List<(string CommitHash, string FilePath, IReadOnlyList<string> AddedLines)> history =
            new List<(string CommitHash, string FilePath, IReadOnlyList<string> AddedLines)>
            {
                ("a1b2c3d", "src/DeletedSecret.cs", new string[] { "string aws = \"AKIA9999888877776666\";" })
            };

        IReadOnlyList<SecretScanFinding> findings = RepoWiseIntelligenceService.ScanTwoPassSecrets(headFiles, history);

        findings.Should().HaveCount(2);
        findings.Should().ContainSingle(f => f.File == "src/Config.cs" && f.PresentInHead && f.MaskedSnippet == "ghp_****wxyz");
        findings.Should().ContainSingle(f => f.File == "src/DeletedSecret.cs" && !f.PresentInHead && f.CommitHash == "a1b2c3d" && f.MaskedSnippet == "AKIA****6666");
    }

    // =========================================================================
    // #242: Directional Co-Change Coupling Detector (Asymmetric Conf(A->B), tau=500)
    // =========================================================================

    [Fact]
    public void ComputeDirectionalCoChange_ProducesAsymmetricConfidence_AndFlagsUnexplainedCoupling()
    {
        List<CoChangeCommit> history = new List<CoChangeCommit>
        {
            new CoChangeCommit(5.0, new string[] { "data/schemas/protocol.json", "src/core/ProtocolGen.cs" }),
            new CoChangeCommit(10.0, new string[] { "data/schemas/protocol.json", "src/core/ProtocolGen.cs" }),
            new CoChangeCommit(20.0, new string[] { "data/schemas/protocol.json", "src/core/ProtocolGen.cs" }),
            new CoChangeCommit(25.0, new string[] { "src/core/ProtocolGen.cs", "src/core/Other.cs" }),
            new CoChangeCommit(30.0, new string[] { "src/core/ProtocolGen.cs", "src/core/Other.cs" }),
            new CoChangeCommit(35.0, new string[] { "src/core/ProtocolGen.cs", "src/core/Other.cs" })
        };

        HashSet<string> modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "data/schemas/protocol.json"
        };

        IReadOnlyList<DirectionalCoChangePair> pairs = RepoWiseIntelligenceService.ComputeDirectionalCoChange(
            history,
            modified,
            tau: 500.0,
            minConfidence: 0.80,
            minSupport: 3,
            isShallowClone: false);

        DirectionalCoChangePair schemaToGen = pairs.Single(p => p.FileA == "data/schemas/protocol.json" && p.FileB == "src/core/ProtocolGen.cs");
        schemaToGen.ConfidenceAToB.Should().Be(1.0);
        schemaToGen.ConfidenceBToA.Should().BeLessThan(0.6);
        schemaToGen.ConfidenceAToB.Should().NotBe(schemaToGen.ConfidenceBToA);
        schemaToGen.IsUnexplainedCoupling.Should().BeTrue();

        IReadOnlyList<DirectionalCoChangePair> shallowSkipped = RepoWiseIntelligenceService.ComputeDirectionalCoChange(
            history,
            modified,
            isShallowClone: true);
        shallowSkipped.Should().BeEmpty();
    }

    // =========================================================================
    // #243: Kamei JIT Diff Risk & Normalized Shannon Churn Entropy
    // =========================================================================

    [Fact]
    public void ComputeKameiJitDiffRisk_CalculatesNormalizedShannonEntropyAndSubsystemCount()
    {
        List<DiffFileChurn> uniformDiff = new List<DiffFileChurn>
        {
            new DiffFileChurn("src/mcp/File1.cs", 50, 50),
            new DiffFileChurn("games/client/File2.lua", 50, 50),
            new DiffFileChurn("native/proxies/File3.cpp", 50, 50),
            new DiffFileChurn("scripts/lib/File4.py", 50, 50)
        };

        KameiJitRiskReport uniformReport = RepoWiseIntelligenceService.ComputeKameiJitDiffRisk(uniformDiff);
        uniformReport.NF.Should().Be(4);
        uniformReport.NS.Should().Be(4);
        uniformReport.LT.Should().Be(400);
        uniformReport.NormalizedShannonEntropy.Should().Be(1.0);

        List<DiffFileChurn> skewedDiff = new List<DiffFileChurn>
        {
            new DiffFileChurn("src/mcp/File1.cs", 397, 0),
            new DiffFileChurn("src/mcp/File2.cs", 1, 0),
            new DiffFileChurn("src/mcp/File3.cs", 1, 0),
            new DiffFileChurn("src/mcp/File4.cs", 1, 0)
        };

        KameiJitRiskReport skewedReport = RepoWiseIntelligenceService.ComputeKameiJitDiffRisk(skewedDiff);
        skewedReport.NS.Should().Be(1);
        skewedReport.NormalizedShannonEntropy.Should().BeLessThan(0.10);
        uniformReport.RiskScore.Should().BeGreaterThan(skewedReport.RiskScore);
    }

    // =========================================================================
    // #244: Decayed Blast-Radius & Test Ranker (decay = 0.6^d)
    // =========================================================================

    [Fact]
    public void ComputeDecayedBlastRadius_AppliesExponentialHopDecay_AndRanksTests()
    {
        List<string> seeds = new List<string> { "src/core/VfsIndex.cs" };
        List<(string Caller, string Callee)> edges = new List<(string Caller, string Callee)>
        {
            ("src/mcp/AssetInspectService.cs", "src/core/VfsIndex.cs"),
            ("tests/unit/DirectVfsTests.cs", "src/core/VfsIndex.cs"),
            ("tests/unit/TransitiveAssetTests.cs", "src/mcp/AssetInspectService.cs")
        };

        BlastRadiusReport report = RepoWiseIntelligenceService.ComputeDecayedBlastRadius(seeds, edges, decayFactor: 0.6, maxDepth: 6);

        report.AffectedModules.Should().Contain(e => e.ModulePath == "src/core/VfsIndex.cs" && e.MinHopDistance == 0 && e.ImpactScore == 1.0);
        report.AffectedModules.Should().Contain(e => e.ModulePath == "tests/unit/DirectVfsTests.cs" && e.MinHopDistance == 1 && e.ImpactScore == 0.6);
        report.AffectedModules.Should().Contain(e => e.ModulePath == "tests/unit/TransitiveAssetTests.cs" && e.MinHopDistance == 2 && e.ImpactScore == 0.36);
        report.RankedTests.First().ModulePath.Should().Be("tests/unit/DirectVfsTests.cs");
    }

    // =========================================================================
    // #245: Symbol Contract Impact Classifier (added, removed, signature-changed, body-changed)
    // =========================================================================

    [Fact]
    public void ClassifySymbolContractImpact_DistinguishesFourCategories_AndFlagsBreakingCallers()
    {
        List<SymbolDeclaration> baseline = new List<SymbolDeclaration>
        {
            new SymbolDeclaration("RemovedFunc", "void RemovedFunc()", "{ }", 0),
            new SymbolDeclaration("SigChangedFunc", "void SigChangedFunc(int a)", "{ return; }", 1),
            new SymbolDeclaration("BodyChangedFunc", "int BodyChangedFunc()", "{ return 1; }", 0)
        };
        List<SymbolDeclaration> current = new List<SymbolDeclaration>
        {
            new SymbolDeclaration("AddedFunc", "void AddedFunc()", "{ }", 0),
            new SymbolDeclaration("SigChangedFunc", "void SigChangedFunc(int a, int b)", "{ return; }", 2),
            new SymbolDeclaration("BodyChangedFunc", "int BodyChangedFunc()", "{ return 2; }", 0)
        };
        List<SymbolCallerSite> callers = new List<SymbolCallerSite>
        {
            new SymbolCallerSite("src/CallerA.cs", "RemovedFunc", 0, false),
            new SymbolCallerSite("src/CallerB.cs", "SigChangedFunc", 1, false),
            new SymbolCallerSite("src/CallerC.cs", "BodyChangedFunc", 0, false)
        };

        IReadOnlyList<SymbolImpactFinding> findings = RepoWiseIntelligenceService.ClassifySymbolContractImpact(baseline, current, callers);

        findings.Should().ContainSingle(f => f.SymbolName == "AddedFunc" && f.ChangeKind == SymbolChangeKind.Added && !f.IsEffectBreaking);
        findings.Should().ContainSingle(f => f.SymbolName == "BodyChangedFunc" && f.ChangeKind == SymbolChangeKind.BodyChanged && !f.IsEffectBreaking);
        findings.Should().ContainSingle(f => f.SymbolName == "RemovedFunc" && f.ChangeKind == SymbolChangeKind.Removed && f.IsEffectBreaking);
        findings.Should().ContainSingle(f => f.SymbolName == "SigChangedFunc" && f.ChangeKind == SymbolChangeKind.SignatureChanged && f.IsEffectBreaking);
    }

    // =========================================================================
    // #246: Semantic Protocol Breaking-Change Guard
    // =========================================================================

    [Fact]
    public void VerifyProtocolSemanticWireCompatibility_FlagsBreakingChanges_AndAllowsOptionalAdditions()
    {
        List<ProtocolMessageSpec> baseline = new List<ProtocolMessageSpec>
        {
            new ProtocolMessageSpec("Handshake", "C2S", new ProtocolFieldSpec[]
            {
                new ProtocolFieldSpec("protocolVersion", "uint32", 1, true),
                new ProtocolFieldSpec("clientToken", "string", 2, true)
            }),
            new ProtocolMessageSpec("LegacyPing", "C2S", Array.Empty<ProtocolFieldSpec>())
        };

        List<ProtocolMessageSpec> brokenCandidate = new List<ProtocolMessageSpec>
        {
            new ProtocolMessageSpec("Handshake", "C2S", new ProtocolFieldSpec[]
            {
                new ProtocolFieldSpec("protocolVersion", "uint64", 1, true),
                new ProtocolFieldSpec("reusedTagField", "string", 2, false),
                new ProtocolFieldSpec("mandatoryNewField", "uint32", 3, true)
            })
        };

        IReadOnlyList<ProtocolWireBreakFinding> breaks = RepoWiseIntelligenceService.VerifyProtocolSemanticWireCompatibility(baseline, brokenCandidate);

        breaks.Select(static b => b.Kind).Should().Contain(new string[]
        {
            "REMOVED_ENDPOINT",
            "RETYPED_FIELD",
            "REUSED_FIELD_TAG",
            "NEW_REQUIRED_FIELD"
        });
    }

    // =========================================================================
    // #247: Safe Asset Deletion Runtime-Load Gate
    // =========================================================================

    [Fact]
    public void ScanAssetRuntimeReferences_BlocksQuarantineOfDynamicallyLoadedLuaAndXmlAssets()
    {
        string[] candidates = new string[]
        {
            "ui/fe2/store_avatar_card.dds",
            "ui/scripts/dynamic_helper.lua",
            "ui/fe2/truly_unused_orphan.dds"
        };
        Dictionary<string, string> runtimeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["games/client/override/ui/main_v2.lua"] = "RegisterTexture('/ui/fe2/store_avatar_card.dds'); DoFile('ui/scripts/dynamic_helper.lua')",
            ["games/client/override/ui/store.interface"] = "<image texture=\"/ui/fe2/store_avatar_card.dds\" />"
        };

        IReadOnlyList<AssetRuntimeReference> refs = RepoWiseIntelligenceService.ScanAssetRuntimeReferences(candidates, runtimeFiles);

        refs.Should().Contain(r => r.CandidateAsset == "ui/fe2/store_avatar_card.dds" && r.LoaderKind == "LUA_REGISTERTEXTURE");
        refs.Should().Contain(r => r.CandidateAsset == "ui/fe2/store_avatar_card.dds" && r.LoaderKind == "XML_TEXTURE");
        refs.Should().Contain(r => r.CandidateAsset == "ui/scripts/dynamic_helper.lua" && r.LoaderKind == "LUA_DOFILE");
        refs.Should().NotContain(r => r.CandidateAsset == "ui/fe2/truly_unused_orphan.dds");
    }

    // =========================================================================
    // #248: Hot-Loop Code Health Auditor
    // =========================================================================

    [Fact]
    public void AuditHotLoopCodeHealth_DetectsCppAndCSharpHotLoopAntipatterns_AndHonorsSuppression()
    {
        Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["native/proxies/FrameLoop.cpp"] = @"
void OnFrame() {
    for (int i = 0; i < 10; i++) {
        std::regex re(""[a-z]+"");
        std::ifstream f(""cfg.txt"");
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    while (active) {
        // kongor-hotloop-ignore
        std::regex suppressedRe(""ok"");
    }
}",
            ["src/Services/BatchWriter.cs"] = @"
public async Task ProcessAsync() {
    foreach (Item item in items) {
        await dbContext.SaveChangesAsync();
    }
}"
        };

        IReadOnlyList<HotLoopViolation> violations = RepoWiseIntelligenceService.AuditHotLoopCodeHealth(files);

        violations.Should().HaveCount(4);
        violations.Select(static v => v.RuleId).Should().BeEquivalentTo(new string[]
        {
            "CPP-HOTLOOP-REGEX",
            "CPP-HOTLOOP-SYNC-IO",
            "CPP-HOTLOOP-SLEEP",
            "CS-HOTLOOP-EF-SAVE"
        });
    }

    // =========================================================================
    // #249: Stale and Missing Test Detector
    // =========================================================================

    [Fact]
    public void EvaluateDiffTestCoverageSignal_FlagsMissingTestSignal_AndStaleTestCandidates()
    {
        string[] modProd = new string[] { "src/Services/NewService.cs" };
        string[] modTests = Array.Empty<string>();
        Dictionary<string, IReadOnlyList<string>> testRefs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tests/OldServiceTests.cs"] = new string[] { "DeletedLegacyMethod", "ExistingMethod" }
        };
        HashSet<string> existingTargets = new HashSet<string>(StringComparer.Ordinal) { "ExistingMethod" };

        DiffTestSignalReport report = RepoWiseIntelligenceService.EvaluateDiffTestCoverageSignal(
            modProd,
            modTests,
            testRefs,
            existingTargets);

        report.HasMissingTestSignal.Should().BeTrue();
        report.HasStaleTests.Should().BeTrue();
        report.StaleTestCandidates.Should().ContainSingle().Which.Should().Be("tests/OldServiceTests.cs -> DeletedLegacyMethod");
    }

    // =========================================================================
    // #250: KB Provenance Ladder & Verbatim Evidence Quote Verifier
    // =========================================================================

    [Fact]
    public void VerifyKbProvenanceAndQuotes_EnforcesHierarchyQuoteSubstringAndContradictions()
    {
        RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.Session).Should().BeGreaterThan(
            RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.ADR));
        RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.ADR).Should().BeGreaterThan(
            RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.Commit));
        RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.Commit).Should().BeGreaterThan(
            RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.Comment));
        RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.Comment).Should().BeGreaterThan(
            RepoWiseIntelligenceService.GetProvenanceRank(KbProvenanceTier.Inferred));

        Dictionary<string, string> sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["native/proxies/native_guard/dllmain.cpp"] = "if (GetCurrentThreadId() != mainThreadId) { PostMessageA(hWnd, WM_USER, 0, 0); }"
        };

        List<KbFieldNoteClaim> invalidNotes = new List<KbFieldNoteClaim>
        {
            new KbFieldNoteClaim(
                "FN-001",
                KbProvenanceTier.Session,
                "k2_x64.dll",
                "CWidget::Frame.vtable_slot",
                "0x88",
                null,
                "native/proxies/native_guard/dllmain.cpp",
                "GetCurrentThreadId() != mainThreadId"),
            new KbFieldNoteClaim(
                "FN-002",
                KbProvenanceTier.Inferred,
                "k2_x64.dll",
                "CWidget::Frame.vtable_slot",
                "0x90",
                "FN-001",
                "native/proxies/native_guard/dllmain.cpp",
                "non_existent_hallucinated_quote()")
        };

        KbVerificationResult invalidResult = RepoWiseIntelligenceService.VerifyKbProvenanceAndQuotes(invalidNotes, sources);
        invalidResult.IsValid.Should().BeFalse();
        invalidResult.QuoteVerified.Should().BeFalse();
        invalidResult.SupersessionAllowed.Should().BeFalse();
        invalidResult.HasUnresolvedContradiction.Should().BeTrue();
    }

    // =========================================================================
    // #251: Karpathy AutoResearch Keep/Discard Metric Ratchet & Simplicity Criterion
    // =========================================================================

    [Fact]
    public void EvaluateAutoRatchetRun_EnforcesSimplicityCriterion_AndCrashVsTypoTriage()
    {
        // 1. Simplicity Deletion Keep: equal latency (100ms -> 100ms) with net code deletion (+2 / -18)
        AutoRatchetEvaluation simplicityKeep = RepoWiseIntelligenceService.EvaluateAutoRatchetRun(
            "a1b2c3d",
            baselineMs: 100.0,
            candidateMs: 100.0,
            memoryMb: 42.5,
            linesAdded: 2,
            linesDeleted: 18,
            elapsedSeconds: 4.2,
            exitCode: 0,
            errorOutput: string.Empty,
            description: "Delete redundant wrapper");
        simplicityKeep.Decision.Should().Be(AutoRatchetDecisionKind.SimplicityDeletionKeep);
        simplicityKeep.KeepCommit.Should().BeTrue();
        simplicityKeep.ResultsTsvRow.Split('\t').Should().HaveCount(5);

        // 2. Complexity Bloat Discard: tiny 0.2% win (100.0ms -> 99.8ms) but adds +40 net lines
        AutoRatchetEvaluation bloatDiscard = RepoWiseIntelligenceService.EvaluateAutoRatchetRun(
            "b2c3d4e",
            baselineMs: 100.0,
            candidateMs: 99.8,
            memoryMb: 44.0,
            linesAdded: 45,
            linesDeleted: 5,
            elapsedSeconds: 4.5,
            exitCode: 0,
            errorOutput: string.Empty,
            description: "Add complex cache table");
        bloatDiscard.Decision.Should().Be(AutoRatchetDecisionKind.ComplexityBloatDiscard);
        bloatDiscard.KeepCommit.Should().BeFalse();

        // 3. Retryable Typo vs Hard Timeout
        AutoRatchetEvaluation typoCrash = RepoWiseIntelligenceService.EvaluateAutoRatchetRun(
            "c3d4e5f",
            baselineMs: 100.0,
            candidateMs: 0.0,
            memoryMb: 0.0,
            linesAdded: 3,
            linesDeleted: 1,
            elapsedSeconds: 1.1,
            exitCode: 1,
            errorOutput: "error CS1002: ; expected",
            description: "Candidate with syntax typo");
        typoCrash.Decision.Should().Be(AutoRatchetDecisionKind.RetryableTypoCrash);
        typoCrash.ShouldRetryTypo.Should().BeTrue();

        AutoRatchetEvaluation timeoutKill = RepoWiseIntelligenceService.EvaluateAutoRatchetRun(
            "d4e5f6a",
            baselineMs: 100.0,
            candidateMs: 0.0,
            memoryMb: 0.0,
            linesAdded: 5,
            linesDeleted: 0,
            elapsedSeconds: 61.5,
            exitCode: 1,
            errorOutput: "Timed out",
            description: "Infinite loop candidate");
        timeoutKill.Decision.Should().Be(AutoRatchetDecisionKind.HardTimeoutDiscard);
        timeoutKill.KeepCommit.Should().BeFalse();
    }

    // =========================================================================
    // #252: Shallow Clone Fallback Detection
    // =========================================================================

    [Fact]
    public void EvaluateShallowCloneFallback_DetectsShallowHistoryUnder50Commits()
    {
        ShallowCloneStatus shallow = RepoWiseIntelligenceService.EvaluateShallowCloneFallback(commitCount: 12, hasShallowFile: false);
        shallow.IsShallowClone.Should().BeTrue();
        shallow.FallbackMode.Should().Be("shallow_clone_graceful_skip");

        ShallowCloneStatus full = RepoWiseIntelligenceService.EvaluateShallowCloneFallback(commitCount: 250, hasShallowFile: false);
        full.IsShallowClone.Should().BeFalse();
        full.FallbackMode.Should().Be("full_git_history");
    }

    // =========================================================================
    // Adversarial Edge Cases (#239, #245, #246, #248)
    // =========================================================================

    [Fact]
    public void ConvertToGitHubHeadingSlug_PreservesWordTicks_AndNormalizesFingerprintCase()
    {
        // Protects: #239 GitHub heading slugifier against accidental stripping of 'ticks' and verifies case-insensitive DocRelPath fingerprint parity
        string slug = RepoWiseIntelligenceService.ConvertToGitHubHeadingSlug("Frame Budget (`mtime_utc_ticks`) & [Guide](docs/guide.md)");
        slug.Should().Be("frame-budget-mtime_utc_ticks-guide");

        string fpUpper = RepoWiseIntelligenceService.ComputeDocDriftFingerprint("/Docs/MCP_ISSUES.md", DocDriftKind.Symbol, "src/Foo.cs#Bar");
        string fpLower = RepoWiseIntelligenceService.ComputeDocDriftFingerprint("docs/mcp_issues.md", DocDriftKind.Symbol, "src\\Foo.cs#Bar");
        fpUpper.Should().Be(fpLower);
    }

    [Fact]
    public void AuditHotLoopCodeHealth_DetectsMethodCallLoopCondition_AndPreprocessorIfdefInsideLoop()
    {
        // Protects: #248 hot-loop parser when loop header calls items.size() or body contains #ifdef/#else unbalanced braces
        Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["native/proxies/native_guard/hotloop_ifdef.cpp"] = @"
void OnFrame() {
    for (size_t i = 0; i < items.size(); ++i) {
#ifdef _WIN32
        if (ready) {
#else
        if (ready && fallback) {
#endif
            if (innerCheck) {
                std::regex pattern(""[a-z]+"");
            }
        }
    }
}"
        };
        IReadOnlyList<HotLoopViolation> violations = RepoWiseIntelligenceService.AuditHotLoopCodeHealth(files);
        violations.Should().Contain(v => v.RuleId == "CPP-HOTLOOP-REGEX");
    }

    [Fact]
    public void ClassifySymbolContractImpact_FlagsSameArityTypeChange_AndArityReduction()
    {
        // Protects: #245 symbol contract classifier when parameter types change at identical arity or max arity shrinks
        IReadOnlyList<SymbolDeclaration> baseline = RepoWiseIntelligenceService.ExtractSymbolContracts(
            "src/Net/PacketSender.cs",
            "public class PacketSender { public void Send(int packetId, int flags = 0) { } }");
        IReadOnlyList<SymbolDeclaration> currentTypeChange = RepoWiseIntelligenceService.ExtractSymbolContracts(
            "src/Net/PacketSender.cs",
            "public class PacketSender { public void Send(string packetId, int flags = 0) { } }");
        IReadOnlyList<SymbolDeclaration> currentArityShrink = RepoWiseIntelligenceService.ExtractSymbolContracts(
            "src/Net/PacketSender.cs",
            "public class PacketSender { public void Send(int packetId) { } }");
        List<SymbolCallerSite> callersPassingTwoArgs = new List<SymbolCallerSite>
        {
            new SymbolCallerSite("src/Client/Session.cs", "Send", 2, false)
        };

        IReadOnlyList<SymbolImpactFinding> typeChangeFindings = RepoWiseIntelligenceService.ClassifySymbolContractImpact(
            baseline,
            currentTypeChange,
            callersPassingTwoArgs);
        typeChangeFindings.Should().ContainSingle(f =>
            f.SymbolName == "Send" &&
            f.ChangeKind == SymbolChangeKind.SignatureChanged &&
            f.IsEffectBreaking);

        IReadOnlyList<SymbolImpactFinding> arityShrinkFindings = RepoWiseIntelligenceService.ClassifySymbolContractImpact(
            baseline,
            currentArityShrink,
            callersPassingTwoArgs);
        arityShrinkFindings.Should().ContainSingle(f =>
            f.SymbolName == "Send" &&
            f.ChangeKind == SymbolChangeKind.SignatureChanged &&
            f.IsEffectBreaking);
    }

    [Fact]
    public void VerifyProtocolSemanticWireCompatibility_DetectsFieldTagSwapBetweenExistingFields()
    {
        // Protects: #246 wire compatibility verifier against tag swaps between two existing fields
        List<ProtocolMessageSpec> baseline = new List<ProtocolMessageSpec>
        {
            new ProtocolMessageSpec("AuthPacket", "C2S", new ProtocolFieldSpec[]
            {
                new ProtocolFieldSpec("accountId", "uint32", 1, true),
                new ProtocolFieldSpec("sessionId", "uint32", 2, true)
            })
        };
        List<ProtocolMessageSpec> tagSwappedCandidate = new List<ProtocolMessageSpec>
        {
            new ProtocolMessageSpec("AuthPacket", "C2S", new ProtocolFieldSpec[]
            {
                new ProtocolFieldSpec("accountId", "uint32", 2, true),
                new ProtocolFieldSpec("sessionId", "uint32", 1, true)
            })
        };

        IReadOnlyList<ProtocolWireBreakFinding> violations =
            RepoWiseIntelligenceService.VerifyProtocolSemanticWireCompatibility(baseline, tagSwappedCandidate);
        violations.Should().Contain(v => v.Kind == "REUSED_FIELD_TAG");
    }
}

