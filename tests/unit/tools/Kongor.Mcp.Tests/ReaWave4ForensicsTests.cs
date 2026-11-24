// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit and mutant negative-control verification suite for Phase 15 (#181-195):
/// REA Epistemic Forensics, Obligation Closure &amp; Dual-Oracle Bridge (morluto/rea).
/// </summary>
public sealed class ReaWave4ForensicsTests
{
    [Fact]
    public void Item181_Rfc8785ClosureDigest_And_18CodeObligationAuthorityGate_CatchesMutants()
    {
        // 1. Key-order invariance under RFC 8785 canonical JSON
        using JsonDocument docA = JsonDocument.Parse("{\"zeta\":42,\"alpha\":\"hello\",\"nested\":{\"b\":true,\"a\":\"world\"}}");
        using JsonDocument docB = JsonDocument.Parse("{\"nested\":{\"a\":\"world\",\"b\":true},\"alpha\":\"hello\",\"zeta\":42}");

        string jsonA = ReaEpistemicForensicsEngine.ComputeRfc8785CanonicalJson(docA.RootElement);
        string jsonB = ReaEpistemicForensicsEngine.ComputeRfc8785CanonicalJson(docB.RootElement);
        Assert.Equal("{\"alpha\":\"hello\",\"nested\":{\"a\":\"world\",\"b\":true},\"zeta\":42}", jsonA);
        Assert.Equal(jsonA, jsonB);
        Assert.Equal(
            ReaEpistemicForensicsEngine.ComputeClosureDigestSha256(docA.RootElement, excludeSelfDigestField: false),
            ReaEpistemicForensicsEngine.ComputeClosureDigestSha256(docB.RootElement, excludeSelfDigestField: false));
        Assert.Equal(18, ReaEpistemicForensicsEngine.ObligationIntegrityDiagnosticCodes.Count);

        // 2. Positive closure: all obligations satisfied with dynamic_confirmed tier
        using JsonDocument cleanDoc = JsonDocument.Parse(
            """
            {
              "schema_version": "2.0",
              "dossier_id": "DOSSIER-CLEAN-181",
              "authority_scope": "server_authoritative",
              "target": {
                "binary_name": "k2_x64.dll",
                "rva": "0x15a0"
              },
              "limitations_and_unknowns": {
                "triangulation_status": "dynamically_confirmed"
              },
              "obligations": [
                {
                  "id": "OBL-01",
                  "status": "satisfied",
                  "description": "Verify non-null skeleton pointer before animation state transition",
                  "satisfied_in": "docs/CLEAN.md",
                  "evidence_source": "frida_dynamic"
                },
                {
                  "id": "OBL-02",
                  "status": "satisfied",
                  "description": "Preserve main-thread frame delta clamp on CModelPanel::SetAnim",
                  "satisfied_in": "docs/CLEAN.md",
                  "evidence_source": "frida_dynamic",
                  "blocked_by": ["OBL-01"]
                }
              ]
            }
            """);

        ObligationClosureVerificationReport cleanResult =
            ReaEpistemicForensicsEngine.VerifyObligationClosure(
                cleanDoc.RootElement,
                rawJson: null,
                fileContentResolver: _ => "// satisfies: OBL-01\n// satisfies: OBL-02");
        Assert.True(cleanResult.IsClosedAndValid);
        Assert.Empty(cleanResult.BlockedByStaticOnlyClaims);
        Assert.Empty(cleanResult.SatisfiedDependsOnOpenEdges);
        Assert.Empty(cleanResult.Issues);
        Assert.Equal(64, cleanResult.ClosureDigestSha256.Length);

        // 3. Mutant negative control: satisfied-depends-on-open and static-only-authority-claim
        using JsonDocument mutantDoc = JsonDocument.Parse(
            """
            {
              "schema_version": "2.0",
              "dossier_id": "DOSSIER-MUTANT-181",
              "authority_scope": "server_authoritative",
              "target": {
                "binary_name": "k2_x64.dll",
                "rva": "0x15a0"
              },
              "limitations_and_unknowns": {
                "triangulation_status": "static_only"
              },
              "obligations": [
                {
                  "id": "OBL-OPEN",
                  "status": "open",
                  "description": "Unresolved vtable slot +0x88 dispatch target"
                },
                {
                  "id": "OBL-SAT",
                  "status": "satisfied",
                  "description": "Depends on open obligation while claiming server_authoritative satisfaction from static_only",
                  "satisfied_in": "docs/CLEAN.md",
                  "evidence_source": "static_only",
                  "requires_dynamic_confirmation": true,
                  "blocked_by": ["OBL-OPEN"]
                }
              ]
            }
            """);

        ObligationClosureVerificationReport mutantResult =
            ReaEpistemicForensicsEngine.VerifyObligationClosure(
                mutantDoc.RootElement,
                rawJson: null,
                fileContentResolver: _ => "// satisfies: OBL-SAT");
        Assert.False(mutantResult.IsClosedAndValid);
        Assert.Contains("OBL-SAT", mutantResult.BlockedByStaticOnlyClaims);
        Assert.NotEmpty(mutantResult.SatisfiedDependsOnOpenEdges);
        Assert.Contains(mutantResult.Issues, i => i.Code == "satisfied-depends-on-open");
        Assert.Contains(mutantResult.Issues, i => i.Code == "static-only-authority-claim");
    }

    [Fact]
    public void Item182_14RuleSemanticReadinessChecker_EnforcesAll14RulesAndDualDigestContradictions()
    {
        Assert.Equal(14, ReaEpistemicForensicsEngine.SemanticReadinessRuleCodes.Count);

        // Positive case: contradiction has >= 2 distinct evidence digests and no semantic hazards
        using JsonDocument validDoc = JsonDocument.Parse(
            """
            {
              "id": "k2_x64+0x15a0",
              "equivalence_claimed": true,
              "evidence_truncated": false,
              "evidence_unstable": false,
              "semantic_claims": [
                {
                  "id": "contra_01",
                  "status": "contradicted",
                  "evidence_digests": ["sha256:static_digest_1", "sha256:runtime_digest_2"]
                }
              ]
            }
            """);

        SemanticReadinessReport validReport =
            ReaEpistemicForensicsEngine.EvaluateSemanticReadiness(validDoc.RootElement);
        Assert.True(validReport.IsReady);
        Assert.Equal(14, validReport.CheckedRuleCount);
        Assert.Empty(validReport.Violations);

        // Negative control: trigger all 14 semantic readiness rules including contradiction-proof-incomplete (< 2 distinct digests)
        using JsonDocument mutantDoc = JsonDocument.Parse(
            """
            {
              "id": "k2_x64+0x15a0",
              "equivalence_claimed": true,
              "evidence_truncated": true,
              "is_concurrent_flow": true,
              "assumes_total_order": true,
              "synchronization_proven": false,
              "has_divergence": true,
              "status": "contradicted",
              "evidence_digests": ["sha256:same_digest", "sha256:same_digest"],
              "has_unbounded_loop": true,
              "loop_bound_verified": false,
              "pointer_aliasing_possible": true,
              "alias_separation_verified": false,
              "abi_volatile_clobber": true,
              "claims_pure": true,
              "omitted_operations_lower_bound": 3,
              "has_raw_float_equality": true,
              "uint_view_or_epsilon_verified": false,
              "oracle_binary_sha256_mismatch": true,
              "indirect_dispatch_marked_resolved": true,
              "resolved_target_count": 0,
              "modifies_stack_without_unwind": true,
              "off_main_thread_ui_mutation": true,
              "rollback_bytes_incomplete": true
            }
            """);

        SemanticReadinessReport mutantReport =
            ReaEpistemicForensicsEngine.EvaluateSemanticReadiness(mutantDoc.RootElement);
        Assert.False(mutantReport.IsReady);
        Assert.Equal(14, mutantReport.Violations.Count);
        Assert.Contains(mutantReport.Violations, v => v.RuleCode == "false-equivalence");
        Assert.Contains(mutantReport.Violations, v => v.RuleCode == "unsafe-concurrency-semantics");
        Assert.Contains(mutantReport.Violations, v => v.RuleCode == "divergence-not-localized");
        Assert.Contains(mutantReport.Violations, v => v.RuleCode == "contradiction-proof-incomplete");
    }

    [Fact]
    public void Item183_5StateStaticRuntimeCorrelationMatrix_EmitsCorAndContraSha256Records()
    {
        StateReconciliationService service = new StateReconciliationService();

        StaticRuntimeCorrelationRecord cochanged = service.CorrelateStaticVsRuntimeObservation(
            subjectId: "subj_cochanged",
            staticBeforeDigest: "sha256:a1",
            staticAfterDigest: "sha256:a2",
            runtimeBeforeDigest: "sha256:b1",
            runtimeAfterDigest: "sha256:b2");
        Assert.Equal("cochanged", cochanged.State);
        Assert.Equal("left_to_left", cochanged.SideAlignment);
        Assert.Equal("hypothesis", cochanged.Classification);
        Assert.StartsWith("cor_", cochanged.CorrelationId, StringComparison.Ordinal);
        Assert.Null(cochanged.ContradictionId);

        StaticRuntimeCorrelationRecord crossSwapped = service.CorrelateStaticVsRuntimeObservation(
            subjectId: "subj_cross_swapped",
            staticBeforeDigest: "sha256:x1",
            staticAfterDigest: "sha256:x2",
            runtimeBeforeDigest: "sha256:x2",
            runtimeAfterDigest: "sha256:x1");
        Assert.Equal("cochanged", crossSwapped.State);
        Assert.Equal("left_to_right", crossSwapped.SideAlignment);
        Assert.Equal("contradiction", crossSwapped.Classification);
        Assert.NotNull(crossSwapped.ContradictionId);
        Assert.StartsWith("contra_", crossSwapped.ContradictionId!, StringComparison.Ordinal);

        StaticRuntimeCorrelationRecord staticOnly = service.CorrelateStaticVsRuntimeObservation(
            subjectId: "subj_static_only",
            staticBeforeDigest: "sha256:s1",
            staticAfterDigest: "sha256:s2",
            runtimeBeforeDigest: "sha256:r1",
            runtimeAfterDigest: "sha256:r1");
        Assert.Equal("static_only", staticOnly.State);
        Assert.Equal("contradiction", staticOnly.Classification);
        Assert.NotNull(staticOnly.ContradictionId);
        Assert.StartsWith("contra_", staticOnly.ContradictionId!, StringComparison.Ordinal);

        StaticRuntimeCorrelationRecord runtimeOnly = service.CorrelateStaticVsRuntimeObservation(
            subjectId: "subj_runtime_only",
            staticBeforeDigest: "sha256:s1",
            staticAfterDigest: "sha256:s1",
            runtimeBeforeDigest: "sha256:r1",
            runtimeAfterDigest: "sha256:r2");
        Assert.Equal("runtime_only", runtimeOnly.State);
        Assert.Equal("contradiction", runtimeOnly.Classification);
        Assert.NotNull(runtimeOnly.ContradictionId);

        StaticRuntimeCorrelationRecord bothUnchanged = service.CorrelateStaticVsRuntimeObservation(
            subjectId: "subj_unchanged",
            staticBeforeDigest: "sha256:s1",
            staticAfterDigest: "sha256:s1",
            runtimeBeforeDigest: "sha256:r1",
            runtimeAfterDigest: "sha256:r1");
        Assert.Equal("both_unchanged", bothUnchanged.State);
        Assert.Equal("hypothesis", bothUnchanged.Classification);

        StaticRuntimeCorrelationRecord unknownTruncated = service.CorrelateStaticVsRuntimeObservation(
            subjectId: "subj_unknown",
            staticBeforeDigest: "sha256:s1",
            staticAfterDigest: null,
            runtimeBeforeDigest: "sha256:r1",
            runtimeAfterDigest: "sha256:r2",
            staticTruncated: true);
        Assert.Equal("unknown", unknownTruncated.State);
        Assert.Equal("unresolved_branch", unknownTruncated.Classification);
        Assert.True(unknownTruncated.IsTruncated);
    }

    [Fact]
    public void Item184_VolatileDimensionConformanceTrustGate_IsolatesVolatileAndReportsFirstDivergence()
    {
        Dictionary<string, string?> baseline = new Dictionary<string, string?>
        {
            ["timing"] = "12.4ms",
            ["timestamp"] = "2026-10-18T10:00:00Z",
            ["pid"] = "1001",
            ["ppid"] = "500",
            ["duration_ms"] = "45",
            ["created_at"] = "2026-10-18T09:00:00Z",
            ["updated_at"] = "2026-10-18T10:00:00Z",
            ["interface_hash"] = "sha256:abcdef",
            ["stale"] = "false"
        };

        // Volatile fields all change, deterministic fields stay identical -> Conforms == true
        Dictionary<string, string?> candidateConforming = new Dictionary<string, string?>
        {
            ["timing"] = "99.1ms",
            ["timestamp"] = "2026-10-18T11:30:00Z",
            ["pid"] = "9876",
            ["ppid"] = "9000",
            ["duration_ms"] = "210",
            ["created_at"] = "2026-10-18T11:00:00Z",
            ["updated_at"] = "2026-10-18T11:30:00Z",
            ["interface_hash"] = "sha256:abcdef",
            ["stale"] = "false"
        };

        ExecutionTruth.ConformanceTrustGateReport passReport =
            ExecutionTruth.EvaluateConformanceTrustGate(baseline, candidateConforming, "ev_reload_01");
        Assert.True(passReport.Conforms);
        Assert.Null(passReport.FirstDivergence);
        Assert.Equal(7, passReport.IsolatedVolatileDimensions.Count);
        Assert.Equal(passReport.BaselineCanonicalSha256, passReport.CandidateCanonicalSha256);

        // Deterministic field diverges -> FirstDivergence populated deterministically
        Dictionary<string, string?> candidateDivergent = new Dictionary<string, string?>(candidateConforming)
        {
            ["interface_hash"] = "sha256:mutated999"
        };
        using JsonDocument argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            evidenceId = "ev_reload_mutant",
            baselineState = baseline,
            candidateState = candidateDivergent
        }));

        ExecutionTruth.ConformanceTrustGateReport failReport =
            ReloadAndVerifyHandler.EvaluateReloadConformanceTrustGate(argsDoc.RootElement);
        Assert.False(failReport.Conforms);
        Assert.NotNull(failReport.FirstDivergence);
        Assert.Equal("interface_hash", failReport.FirstDivergence!.Dimension);
        Assert.Equal("ev_reload_mutant", failReport.FirstDivergence.EvidenceId);
    }

    [Fact]
    public void Item185_GhidraHighPCodeDefUseAndEffectGraph_ExtractsEffectsAndOmittedLowerBound()
    {
        HighPCodeOperationInput[] ops =
        [
            new HighPCodeOperationInput(
                OpId: "op_0",
                Opcode: "LOAD",
                Address: "0x15a0",
                BasicBlockId: "blk_entry",
                OutputVarnode: "v0",
                InputVarnodes: new[] { "param_1" }),
            new HighPCodeOperationInput(
                OpId: "op_1",
                Opcode: "STORE",
                Address: "0x15a4",
                BasicBlockId: "blk_entry",
                OutputVarnode: null,
                InputVarnodes: new[] { "param_1", "v0" }),
            new HighPCodeOperationInput(
                OpId: "op_2",
                Opcode: "CBRANCH",
                Address: "0x15a8",
                BasicBlockId: "detached_blk",
                OutputVarnode: null,
                InputVarnodes: new[] { "v0" }),
            new HighPCodeOperationInput(
                OpId: "op_3",
                Opcode: "CALLIND",
                Address: "0x15ac",
                BasicBlockId: null,
                OutputVarnode: null,
                InputVarnodes: new[] { "v0" })
        ];

        HighPCodeDefUseAndEffectGraph fullReport =
            ReaEpistemicForensicsEngine.BuildHighPCodeDefUseAndEffectGraph("0x15a0", ops, maxEmittedOperations: 10);
        Assert.Equal(0, fullReport.OmittedOperationsLowerBound);
        Assert.Equal(4, fullReport.Effects.Count);
        Assert.Contains(fullReport.Effects, e => e.EffectKind == "memory_read");
        Assert.Contains(fullReport.Effects, e => e.EffectKind == "memory_write");
        Assert.Contains(fullReport.Effects, e => e.EffectKind == "conditional_branch");
        Assert.Contains(fullReport.Effects, e => e.EffectKind == "indirect_call");
        Assert.True(fullReport.ParameterUses.ContainsKey("param_1"));
        Assert.Equal("member", fullReport.BlockMembership["op_0"]);
        Assert.Equal("detached", fullReport.BlockMembership["op_2"]);
        Assert.Equal("unavailable", fullReport.BlockMembership["op_3"]);

        HighPCodeDefUseAndEffectGraph truncatedReport =
            ReaEpistemicForensicsEngine.BuildHighPCodeDefUseAndEffectGraph("0x15a0", ops, maxEmittedOperations: 2);
        Assert.Equal(2, truncatedReport.OmittedOperationsLowerBound);
    }

    [Fact]
    public void Item186_TypedNativeInvestigationDag_Validates28NodeAnd16EdgeCatalogsAndContradictions()
    {
        Assert.Equal(28, ReaEpistemicForensicsEngine.InvestigationNodeKinds.Count);
        Assert.Equal(16, ReaEpistemicForensicsEngine.InvestigationEdgeKinds.Count);

        InvestigationDagNode[] nodes =
        [
            new InvestigationDagNode("n_func", "function", "Entry at +0x15a0", "observed"),
            new InvestigationDagNode("n_slot", "dispatch_slot", "Slot +0x88 dispatch", "inferred"),
            new InvestigationDagNode("n_evidence", "evidence_artifact", "Frida probe hit", "resolved")
        ];

        InvestigationDagEdge[] edges =
        [
            new InvestigationDagEdge("n_evidence", "n_slot", "contradicts"),
            new InvestigationDagEdge("n_func", "n_slot", "indexes_slot")
        ];

        InvestigationDagEvaluation report =
            ReaEpistemicForensicsEngine.EvaluateInvestigationDag(nodes, edges);
        Assert.True(report.IsValid);
        Assert.Empty(report.SchemaErrors);
        Assert.Contains("n_slot", report.ContradictedNodeIds);
        Assert.Equal("ambiguous", report.EffectiveNodeStatuses["n_slot"]);
    }

    [Fact]
    public void Item187_8FacetCleanroomEquivalenceComparator_NormalizesAutoNamesAndNopPadding()
    {
        HotReloadService hotReload = new HotReloadService();
        CleanroomFunctionSnapshot original = new CleanroomFunctionSnapshot(
            Name: "sub_1400015a0",
            ReturnType: "int",
            ParameterCount: 1,
            CallingConvention: "__fastcall",
            Pseudocode: "int uVar1 = *(int*)(in_RCX + 0x88); return FUN_140002000(uVar1);",
            AssemblyInstructions: new[] { "mov eax, [rcx+0x88]", "nop", "int3", "ret" },
            Comments: new[] { "// check anim" },
            Callees: new[] { "FUN_140002000" },
            References: new[] { "DAT_140099000" },
            StringLiterals: new[] { "idle" },
            BasicBlockCount: 2,
            EdgeCount: 1,
            CyclomaticComplexity: 1);

        CleanroomFunctionSnapshot cleanroom = new CleanroomFunctionSnapshot(
            Name: "sub_140009999",
            ReturnType: "int",
            ParameterCount: 1,
            CallingConvention: "__fastcall",
            Pseudocode: "int iVar2 = *(int*)(in_RDX + 0x88); return sub_140008888(iVar2);",
            AssemblyInstructions: new[] { "mov eax, [rcx+0x88]", "xchg ax, ax", "ret" },
            Comments: new[] { "// different comment" },
            Callees: new[] { "sub_140008888" },
            References: new[] { "DAT_140077777" },
            StringLiterals: new[] { "idle" },
            BasicBlockCount: 2,
            EdgeCount: 1,
            CyclomaticComplexity: 1);

        CleanroomEquivalenceReport eqReport =
            hotReload.CompareCleanroomEquivalence(original, cleanroom);
        Assert.Equal(8, eqReport.TotalFacetCount);
        Assert.True(eqReport.IsEquivalent);
        Assert.Equal(7, eqReport.MatchedFacetCount); // 7 semantic facets match, only 'comments' differs
    }

    [Fact]
    public void Items188And189_GhidraFastAnalyzerProfile_And_ReverseSortedSeedsWindowsSupport_ArePresent()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")))
        {
            dir = dir.Parent;
        }
        string repoRoot = dir?.FullName ?? Directory.GetCurrentDirectory();
        string ingestPath = Path.Combine(repoRoot, "src", "tools", "ghidra", "ingest_binaries.py");
        string extPath = Path.Combine(repoRoot, "src", "tools", "ghidra", "kongor_mcp_extensions.py");

        Assert.True(File.Exists(ingestPath));
        Assert.True(File.Exists(extPath));

        string ingestSource = File.ReadAllText(ingestPath);
        Assert.Contains("DISABLED_SLOW_ANALYZERS", ingestSource, StringComparison.Ordinal);
        Assert.Contains("Non-Returning Functions - Discovered", ingestSource, StringComparison.Ordinal);
        Assert.Contains("GCC Exception Handlers", ingestSource, StringComparison.Ordinal);

        string extSource = File.ReadAllText(extPath);
        Assert.Contains("extract_high_pcode_def_use_and_effect_graph", extSource, StringComparison.Ordinal);
        Assert.Contains("commit_reverse_sorted_seeds_and_repair_noreturn", extSource, StringComparison.Ordinal);
        Assert.Contains("reverse_address_highest_first", extSource, StringComparison.Ordinal);
        Assert.Contains("\"windows_supported\": True", extSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item190_IdaManageHandler_Enforces14ReadOnlyOperationsAndMonotonicBusySentinelAsync()
    {
        Assert.Equal(14, IdaManageHandler.ReadOnlyOperations.Count);
        Assert.Equal(100, IdaManageHandler.BusyHealthRetryMs);

        int probeCount = 0;
        IdaManageHandler handler = new IdaManageHandler(busyProbe: _ =>
        {
            probeCount++;
            return ValueTask.FromResult(probeCount < 2); // Busy on attempt 1, ready on attempt 2
        });

        using JsonDocument dualOracleArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            operation = "compare_dual_oracle",
            binaryName = "k2_x64.dll",
            rva = "0x15a0",
            functionName = "CModelPanel::SetAnim"
        }));

        ToolCallResult okResult = await handler.HandleAsync(dualOracleArgs.RootElement);
        Assert.False(okResult.IsError);
        Assert.Equal(2, probeCount);

        // Mutant negative control: mutating operation must be rejected
        using JsonDocument mutateArgs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            operation = "patch_bytes"
        }));
        ToolCallResult blockedResult = await handler.HandleAsync(mutateArgs.RootElement);
        Assert.True(blockedResult.IsError);
    }

    [Fact]
    public void Item191_CanonicalCrashMinidumpRegisterEnvelope_NormalizesRegistersAndChecksStreams()
    {
        Dictionary<string, string> unorderedRegs = new Dictionary<string, string>
        {
            ["rax"] = "0x18",
            ["rip"] = "0x7ff728ab1402",
            ["r15"] = "0xff00",
            ["efl"] = "0x10206"
        };

        CanonicalCrashRegisterEnvelope regEnv =
            ReaEpistemicForensicsEngine.NormalizeCrashRegisters(
                rawRegisterDump: null,
                registerMap: unorderedRegs);

        Assert.Equal(18, regEnv.Registers.Count);
        Assert.Equal("r15", regEnv.Registers[0].Name);
        Assert.Equal("0x000000000000ff00", regEnv.Registers[0].HexValue);
        Assert.Equal("rax", regEnv.Registers[14].Name);
        Assert.Equal("rip", regEnv.Registers[15].Name);
        Assert.Equal("rsp", regEnv.Registers[16].Name);
        Assert.Equal("eflags", regEnv.Registers[17].Name);
        Assert.Equal("0x0000000000010206", regEnv.Registers[17].HexValue);

        // Complete streams: 3 (ThreadList), 4 (ModuleList), 6 (Exception), 16 (MemoryInfoList)
        MinidumpStreamCompletenessReport completeStreams =
            ReaEpistemicForensicsEngine.EvaluateMinidumpStreamCompleteness(
                new (uint StreamType, uint DataSize, uint Rva)[]
                {
                    (3u, 64u, 64u),
                    (4u, 64u, 128u),
                    (6u, 64u, 192u),
                    (16u, 64u, 256u)
                },
                fileLength: 1024L);
        Assert.True(completeStreams.IsComplete);
        Assert.Empty(completeStreams.MissingStreams);

        // Incomplete minidump missing MemoryInfoListStream (16)
        MinidumpStreamCompletenessReport incompleteStreams =
            ReaEpistemicForensicsEngine.EvaluateMinidumpStreamCompleteness(
                new (uint StreamType, uint DataSize, uint Rva)[]
                {
                    (3u, 64u, 64u),
                    (4u, 64u, 128u),
                    (6u, 64u, 192u)
                },
                fileLength: 1024L);
        Assert.False(incompleteStreams.IsComplete);
        Assert.Contains("MemoryInfoListStream", incompleteStreams.MissingStreams);
    }

    [Fact]
    public void Item192_PolyBaseAddressCoordinateAlgebra_PreventsFileOffsetVsRvaConflation()
    {
        PeSectionRange[] sections =
        [
            new PeSectionRange(".text", 0x1000UL, 0x200000UL, 0x400UL, 0x200000UL)
        ];
        ulong imageBase = 0x180000000UL;

        AddressCoordinate rvaCoord = AddressCoordinate.Parse("rva:0x15a0");
        AddressCoordinate fileCoord = rvaCoord.ConvertTo(AddressSpaceKind.FileOffset, imageBase, sections);
        AddressCoordinate vaCoord = rvaCoord.ConvertTo(AddressSpaceKind.VirtualAddress, imageBase, sections);

        Assert.Equal("file:0x9a0", fileCoord.ToString());
        Assert.Equal("va:0x1800015a0", vaCoord.ToString());
        Assert.Equal(rvaCoord, vaCoord.ConvertTo(AddressSpaceKind.Rva, imageBase, sections));
        Assert.Equal(rvaCoord, fileCoord.ConvertTo(AddressSpaceKind.Rva, imageBase, sections));

        // Conflation guard: subtracting coordinates across different address spaces throws InvalidOperationException
        Assert.Throws<InvalidOperationException>(() => rvaCoord.Subtract(fileCoord));

        // Conflation guard: passing rva:0x15a0 when FileOffset is required without section table throws
        Assert.Throws<InvalidOperationException>(() =>
            NativeManageHandler.ValidatePatchCoordinate("rva:0x15a0", AddressSpaceKind.FileOffset, imageBase, null));
    }

    [Fact]
    public void Item193_PeResourceHierarchyAndManifestForensics_Enforces8MiBBudgetAndExtractsVersionManifest()
    {
        NativeHookService hookService = new NativeHookService();
        byte[] rsrcBuffer = BuildSyntheticRsrcWithVersionAndManifest();

        PeResourceForensicsReport report = hookService.InspectPeResources(rsrcBuffer, rsrcSectionRva: 0);
        Assert.True(report.IsWithinBudget);
        Assert.NotNull(report.VersionInfo);
        Assert.Equal("2.10.12.0", report.VersionInfo!.FileVersion);
        Assert.Equal("asInvoker", report.RequestedExecutionLevel);
        Assert.True(report.DpiAware);
        Assert.True(report.LongPathAware);

        // Negative control: buffer > 8 MiB budget
        byte[] overBudgetBuffer = new byte[ReaEpistemicForensicsEngine.MaxResourceSectionBytes + 16];
        PeResourceForensicsReport overBudget = hookService.InspectPeResources(overBudgetBuffer, rsrcSectionRva: 0);
        Assert.False(overBudget.IsWithinBudget);
    }

    [Fact]
    public async Task Item194_CoffMachineAndR2ROsOverrideDecoder_DeobfuscatesAll6XorMasksWithin64MiBBudget()
    {
        ushort rawMachine = 0x8664; // AMD64
        ushort[] masks = [0x4644, 0xadc4, 0xadc5, 0x7b79, 0x1993, 0x1992];

        foreach (ushort mask in masks)
        {
            ushort encoded = (ushort)(rawMachine ^ mask);
            byte[] sample = new byte[32];
            BinaryPrimitives.WriteUInt16LittleEndian(sample.AsSpan(0, 2), encoded);
            // Place RTR_SIGNATURE at non-4-byte-aligned offset 2 to verify byte-granular signature scan
            BinaryPrimitives.WriteUInt32LittleEndian(sample.AsSpan(2, 4), ReaEpistemicForensicsEngine.RtrSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(sample.AsSpan(6, 2), 8); // Major
            BinaryPrimitives.WriteUInt16LittleEndian(sample.AsSpan(8, 2), 0); // Minor

            CoffAndReadyToRunForensicsReport decoded =
                ReaEpistemicForensicsEngine.DecodeCoffAndReadyToRunHeader(sample, declaredTotalBytes: sample.Length);

            Assert.True(decoded.IsWithinMemoryBudget);
            Assert.True(decoded.HasReadyToRunHeader);
            Assert.Equal(rawMachine, decoded.DecodedBaseMachine);
            Assert.Equal(mask, decoded.AppliedOsOverrideXorMask);
            Assert.Equal("x64", decoded.Architecture);
        }

        // Verify HandleDecodeCoffR2R default probe (0xC020 = 0x8664 ^ 0x4644 -> x64 Linux_OS_Override)
        using JsonDocument emptyArgs = JsonDocument.Parse("{}");
        ToolCallResult defaultCoffRes = NativeManageHandler.HandleDecodeCoffR2R(emptyArgs.RootElement);
        Assert.False(defaultCoffRes.IsError);
        string defaultCoffJson = JsonSerializer.Serialize(defaultCoffRes);
        Assert.Contains("0xC020", defaultCoffJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x8664", defaultCoffJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x64", defaultCoffJson, StringComparison.OrdinalIgnoreCase);

        // Verify DetectDuplicateJsonKeys ignores braces and "key": patterns inside string literals (#181)
        IReadOnlyList<string> noDups = ReaEpistemicForensicsEngine.DetectDuplicateJsonKeys(
            "{\"desc\":\"unmatched { brace and \\\"dup\\\":1, \\\"dup\\\":2 inside string\",\"id\":\"OBL-01\"}");
        Assert.Empty(noDups);
        IReadOnlyList<string> realDups = ReaEpistemicForensicsEngine.DetectDuplicateJsonKeys(
            "{\"id\":\"OBL-01\",\"id\":\"OBL-02\"}");
        Assert.Contains("id", realDups);

        // Verify NativeManageHandler delegates action="ida_manage" to IdaManageHandler (#190)
        NativeManageHandler nativeHandler = new NativeManageHandler(_ => { });
        using JsonDocument idaArgs = JsonDocument.Parse("{\"action\":\"ida_manage\",\"operation\":\"decompile_function\",\"rva\":\"0x1400\"}");
        ToolCallResult idaRes = await nativeHandler.HandleAsync(idaArgs.RootElement);
        Assert.False(idaRes.IsError);

        // Negative control: exceeding 64 MiB working memory budget
        byte[] minimal = new byte[16];
        CoffAndReadyToRunForensicsReport overBudget =
            ReaEpistemicForensicsEngine.DecodeCoffAndReadyToRunHeader(
                minimal,
                declaredTotalBytes: ReaEpistemicForensicsEngine.MaxCoffWorkingMemoryBudgetBytes + 1);
        Assert.False(overBudget.IsWithinMemoryBudget);
    }

    [Fact]
    public async Task Item195_FridaSessionLock_And_SplitTokenRedactor_PreventConcurrencyAndBoundaryLeaksAsync()
    {
        // 1. Frida session lock serialization under 4 concurrent tasks
        NativeHookService hookService = new NativeHookService();
        Task<int>[] tasks = new Task<int>[4];
        for (int i = 0; i < tasks.Length; i++)
        {
            int idx = i;
            tasks[i] = hookService.ExecuteSerializedFridaSessionAsync(
                sessionId: $"sess_{idx}",
                scriptName: "probe.js",
                sessionAction: async ct =>
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    return idx;
                });
        }

        await Task.WhenAll(tasks);
        Assert.Equal(0, hookService.ActiveFridaSessions);
        Assert.Equal(1, hookService.MaxConcurrentFridaSessionsObserved);
        Assert.Equal(4, hookService.TotalSerializedFridaTransitions);

        // 2. Split-boundary credential redactor: secret split mid-token across two log chunks
        string[] splitChunks =
        [
            "[Auth] Dispatching +Set login_password \"4d36fc3708f5a8",
            "44b3be75b3f389db8d\" and Bearer eyJhbGciOiJIUz",
            "I1NiIsInR5cCI6IkpXVCJ9_secret_token done."
        ];

        IReadOnlyList<string> redacted = ClientCredentialManager.RedactCredentialsAcrossChunkBoundaries(splitChunks);
        string joined = string.Concat(redacted);
        Assert.DoesNotContain("4d36fc3708f5a8", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("44b3be75b3f389db8d", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGciOiJIUz", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ClientCredentialManager.RedactedPlaceholder, joined, StringComparison.Ordinal);
    }

    private static byte[] BuildSyntheticRsrcWithVersionAndManifest()
    {
        byte[] buf = new byte[512];
        // Root directory at 0x00: 2 ID entries (Type 16 = Version, Type 24 = Manifest)
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), 2);
        // Entry 0: Type 16 -> Level 2 at 0x20
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 16u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 0x80000020u);
        // Entry 1: Type 24 -> Level 2 at 0x40
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), 24u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), 0x80000040u);

        // Level 2 for Type 16 at 0x20: 1 ID entry -> Level 3 at 0x60
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x20 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x20 + 16, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x20 + 20, 4), 0x80000060u);

        // Level 2 for Type 24 at 0x40: 1 ID entry -> Level 3 at 0x80
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x40 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x40 + 16, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x40 + 20, 4), 0x80000080u);

        // Level 3 for Type 16 at 0x60: Lang 1033 -> Data entry at 0xA0
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x60 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x60 + 16, 4), 1033u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x60 + 20, 4), 0xA0u);

        // Level 3 for Type 24 at 0x80: Lang 1033 -> Data entry at 0xB0
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x80 + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x80 + 16, 4), 1033u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x80 + 20, 4), 0xB0u);

        // Data entry for Type 16 at 0xA0 -> payload at 0xC0, size 64
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xA0, 4), 0xC0u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xA0 + 4, 4), 64u);

        // VS_FIXEDFILEINFO at 0xC0: signature 0xFEEF04BD, FileVersion 2.10.12.0, ProductVersion 2.10.12.0
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xC0, 4), 0xFEEF04BDu);
        uint verMs = (2u << 16) | 10u;
        uint verLs = (12u << 16) | 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xC0 + 8, 4), verMs);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xC0 + 12, 4), verLs);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xC0 + 16, 4), verMs);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xC0 + 20, 4), verLs);

        // Manifest XML at 0x110
        string xml = "<assembly><requestedExecutionLevel level=\"asInvoker\" /><dpiAware>true/PM</dpiAware><longPathAware>true</longPathAware></assembly>";
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
        Array.Copy(xmlBytes, 0, buf, 0x110, xmlBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xB0, 4), 0x110u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0xB0 + 4, 4), (uint)xmlBytes.Length);

        return buf;
    }
}
