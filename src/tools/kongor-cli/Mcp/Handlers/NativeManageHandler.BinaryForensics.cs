// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Diagnostics;
using Kongor.Core.Services.Native;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Phase 12 (#146 &amp; #150) and Phase 15 (#181-#194) partial handler for NativeManageHandler.
/// </summary>
public sealed partial class NativeManageHandler
{
    private static readonly string[] DefaultPCodeLoadInputs = ["param_1"];
    private static readonly string[] DefaultPCodeCallIndInputs = ["v_ptr", "param_2"];
    private static readonly string[] DefaultOrigAssembly = ["mov eax, ecx", "inc eax", "nop", "ret"];
    private static readonly string[] DefaultCleanAssembly = ["mov eax, ecx", "inc eax", "ret"];
    private static readonly string[] DefaultObligationComments = ["// satisfies: OBL-01"];
    private static ToolCallResult HandleIpsPatch(JsonElement arguments)
    {
        bool useIps32 = true;
        if (arguments.TryGetProperty("ips32", out JsonElement ips32El) &&
            (ips32El.ValueKind == JsonValueKind.True || ips32El.ValueKind == JsonValueKind.False))
        {
            useIps32 = ips32El.GetBoolean();
        }

        if (arguments.TryGetProperty("patch_hex", out JsonElement patchHexEl) &&
            !string.IsNullOrWhiteSpace(patchHexEl.GetString()))
        {
            byte[] patchBytes = Convert.FromHexString(patchHexEl.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal));
            IpsDecodeResult decoded = IpsPatchCodec.Decode(patchBytes);
            if (!decoded.Success)
            {
                return ToolCallResult.Error(decoded.ErrorMessage ?? "IPS decode failed.");
            }

            return ToolCallResult.Success(new
            {
                success = true,
                action = "ips_decode",
                isIps32 = decoded.IsIps32,
                recordCount = decoded.Records.Count,
                totalModifiedBytes = decoded.TotalPatchedBytes,
                records = decoded.Records
            });
        }

        long offset = 0x1000L;
        if (arguments.TryGetProperty("offset", out JsonElement offEl) && offEl.TryGetInt64(out long offVal))
        {
            offset = offVal;
        }

        byte[] dataBytes = new byte[] { 0x90, 0x90 };
        if (arguments.TryGetProperty("bytes_hex", out JsonElement bytesEl) && !string.IsNullOrWhiteSpace(bytesEl.GetString()))
        {
            dataBytes = Convert.FromHexString(bytesEl.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        OverlayPatchRecord[] records = new OverlayPatchRecord[]
        {
            new OverlayPatchRecord(offset, dataBytes)
        };
        byte[] encodedPatch = IpsPatchCodec.Encode(records, useIps32);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "ips_encode",
            isIps32 = useIps32,
            patchByteLength = encodedPatch.Length,
            patchHex = Convert.ToHexString(encodedPatch),
            stagedOverlayCount = records.Length,
            totalModifiedBytes = dataBytes.Length
        });
    }

    private static ToolCallResult HandleDecodeFastDiv(JsonElement arguments)
    {
        int bitWidth = 32;
        if (arguments.TryGetProperty("bit_width", out JsonElement bwEl) && bwEl.TryGetInt32(out int bwVal))
        {
            bitWidth = bwVal;
        }

        if (arguments.TryGetProperty("divisor", out JsonElement divEl) && divEl.TryGetUInt64(out ulong divisor) && divisor > 0)
        {
            FastDivSynthesisResult synthesized = InvariantMultiplierDivisorDecoder.SynthesizeUnsigned(divisor, bitWidth);
            return ToolCallResult.Success(new
            {
                success = true,
                action = "synthesize_fastdiv",
                divisor = synthesized.Divisor,
                bitWidth = synthesized.BitWidth,
                scheme = synthesized.Variant.ToString(),
                multiplier = $"0x{synthesized.Multiplier:X}",
                preShift = synthesized.PreShift,
                postShift = synthesized.PostShift,
                verifiedExact = synthesized.IsVerifiedExact,
                disassemblySummary = synthesized.DisassemblyFormula
            });
        }

        ulong multiplier = 0xAAAAAAABUL;
        if (arguments.TryGetProperty("multiplier", out JsonElement mulEl))
        {
            if (mulEl.ValueKind == JsonValueKind.Number && mulEl.TryGetUInt64(out ulong mulVal))
            {
                multiplier = mulVal;
            }
            else if (mulEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mulEl.GetString()))
            {
                string raw = mulEl.GetString()!.Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    multiplier = Convert.ToUInt64(raw.Substring(2), 16);
                }
                else
                {
                    multiplier = ulong.Parse(raw);
                }
            }
        }

        int preShift = 0;
        if (arguments.TryGetProperty("pre_shift", out JsonElement preEl) && preEl.TryGetInt32(out int preVal))
        {
            preShift = preVal;
        }

        int postShift = 1;
        if (arguments.TryGetProperty("post_shift", out JsonElement postEl) && postEl.TryGetInt32(out int postVal))
        {
            postShift = postVal;
        }

        bool usesAdd = false;
        if (arguments.TryGetProperty("uses_overflow_add", out JsonElement addEl) &&
            (addEl.ValueKind == JsonValueKind.True || addEl.ValueKind == JsonValueKind.False))
        {
            usesAdd = addEl.GetBoolean();
        }

        FastDivVariant variant = usesAdd
            ? FastDivVariant.MulHiAddShift
            : (preShift > 0 ? FastDivVariant.PreShiftMulHiShift : FastDivVariant.MulHiShift);

        FastDivDecodeResult decodedResult = InvariantMultiplierDivisorDecoder.DecodeUnsigned(
            multiplier,
            postShift,
            bitWidth,
            variant,
            preShift);

        return ToolCallResult.Success(new
        {
            success = decodedResult.Success,
            action = "decode_fastdiv",
            recoveredDivisor = decodedResult.RecoveredDivisor,
            verifiedExact = decodedResult.IsVerifiedExact,
            bitWidth = decodedResult.BitWidth,
            scheme = decodedResult.Variant.ToString(),
            multiplier = $"0x{decodedResult.Multiplier:X}",
            preShift = decodedResult.PreShift,
            postShift = decodedResult.PostShift,
            cExpression = decodedResult.CExpression,
            diagnosticMessage = decodedResult.Explanation
        });
    }

    private static ToolCallResult HandleAuditDetourCfg(JsonElement arguments)
    {
        ulong detourStart = 0x1000UL;
        if (arguments.TryGetProperty("detour_start", out JsonElement dsEl) && dsEl.TryGetUInt64(out ulong dsVal))
        {
            detourStart = dsVal;
        }

        int detourLength = 5;
        if (arguments.TryGetProperty("detour_length", out JsonElement dlEl) && dlEl.TryGetInt32(out int dlVal) && dlVal > 0)
        {
            detourLength = dlVal;
        }

        List<DisassembledInstructionRow> rows = new List<DisassembledInstructionRow>();
        if (arguments.TryGetProperty("instructions", out JsonElement insnsEl) && insnsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in insnsEl.EnumerateArray())
            {
                ulong addr = item.TryGetProperty("address", out JsonElement aEl) && aEl.TryGetUInt64(out ulong aVal) ? aVal : 0UL;
                int len = item.TryGetProperty("length", out JsonElement lEl) && lEl.TryGetInt32(out int lVal) ? lVal : 4;
                string mnem = item.TryGetProperty("mnemonic", out JsonElement mEl) ? (mEl.GetString() ?? "nop") : "nop";
                string ops = item.TryGetProperty("operands", out JsonElement oEl) ? (oEl.GetString() ?? string.Empty) : string.Empty;
                rows.Add(new DisassembledInstructionRow(addr, len, mnem, ops));
            }
        }
        else
        {
            rows.Add(new DisassembledInstructionRow(0x1000UL, 4, "sub", "rsp, 0x28"));
            rows.Add(new DisassembledInstructionRow(0x1004UL, 3, "mov", "rax, rcx"));
            rows.Add(new DisassembledInstructionRow(0x1007UL, 2, "je", "0x100F"));
            rows.Add(new DisassembledInstructionRow(0x1009UL, 6, "mov", "dword ptr [rax], 1"));
            rows.Add(new DisassembledInstructionRow(0x100FUL, 4, "add", "rsp, 0x28"));
            rows.Add(new DisassembledInstructionRow(0x1013UL, 1, "ret", string.Empty));
        }

        DisassemblyCfgAnalysisResult cfg = DisassemblyCfgLaneAnalyzer.Analyze(rows);
        DetourOverwriteSafetyReport safety = cfg.AuditDetourOverwriteSafety(detourStart, detourLength);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "audit_detour_cfg",
            isSafe = safety.IsSafe,
            detourStart = $"0x{safety.DetourStart:X}",
            detourLength = safety.DetourLength,
            crossesReturnBoundary = safety.CrossesReturnBoundary,
            returnCountInSpan = safety.ReturnCountInSpan,
            interiorJumpHazardCount = safety.InteriorJumpHazards.Count,
            splitInstructionHazardCount = safety.SplitInstructionHazards.Count,
            edgeCount = cfg.Edges.Count,
            edges = cfg.Edges,
            diagnosticSummary = safety.DiagnosticSummary
        });
    }

    private static ToolCallResult HandleTransformDag(JsonElement arguments)
    {
        byte[] inputBytes = new byte[] { 0x4B, 0x4F, 0x4E, 0x47, 0x4F, 0x52 }; // "KONGOR"
        if (arguments.TryGetProperty("input_hex", out JsonElement inEl) && !string.IsNullOrWhiteSpace(inEl.GetString()))
        {
            inputBytes = Convert.FromHexString(inEl.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        byte[] xorKey = new byte[] { 0x5A };
        if (arguments.TryGetProperty("xor_key_hex", out JsonElement keyEl) && !string.IsNullOrWhiteSpace(keyEl.GetString()))
        {
            xorKey = Convert.FromHexString(keyEl.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        BinaryTransformPullDag dag = new BinaryTransformPullDag();
        int srcNode = dag.AddSourceNode("SourceBuffer", inputBytes);
        int xorNode1 = dag.AddXorNode("XorStage", srcNode, xorKey);
        int xorNode2 = dag.AddXorNode("RoundTripRestoreStage", xorNode1, xorKey);

        byte[] encryptedBytes = dag.EvaluateNode(xorNode1);
        byte[] restoredBytes = dag.EvaluateNode(xorNode2, out int evaluatedNodeCount);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "transform_dag",
            nodeCount = dag.NodeCount,
            evaluatedUniqueNodes = evaluatedNodeCount,
            encryptedHex = Convert.ToHexString(encryptedBytes),
            restoredHex = Convert.ToHexString(restoredBytes)
        });
    }

    /// <summary>
    /// #181 &amp; #182 (Gems 1A &amp; 1H): RFC 8785 closure digest, ownership/dependency DAG verifier,
    /// blocked_by_static_only_claims authority gate, 18-code obligation verifier, and 14-rule semantic readiness checker.
    /// </summary>
    public static ToolCallResult HandleVerifyClosure(JsonElement arguments)
    {
        JsonElement dossierEl = arguments;
        if (arguments.TryGetProperty("dossier", out JsonElement subDossier) && subDossier.ValueKind == JsonValueKind.Object)
        {
            dossierEl = subDossier;
        }

        string? rawJson = null;
        if (arguments.TryGetProperty("raw_json", out JsonElement rawEl) && rawEl.ValueKind == JsonValueKind.String)
        {
            rawJson = rawEl.GetString();
        }

        ObligationClosureVerificationReport closureReport =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.VerifyObligationClosure(dossierEl, rawJson);
        Kongor.Cli.Mcp.Services.SemanticReadinessReport readinessReport =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.EvaluateSemanticReadiness(dossierEl);

        return ToolCallResult.Success(new
        {
            success = closureReport.IsClosedAndValid && readinessReport.IsReady,
            action = "verify_closure",
            closure_digest_sha256 = closureReport.ClosureDigestSha256,
            declared_closure_digest_sha256 = closureReport.DeclaredClosureDigestSha256,
            is_closed_and_valid = closureReport.IsClosedAndValid,
            is_semantically_ready = readinessReport.IsReady,
            checked_obligation_codes = Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.ObligationIntegrityDiagnosticCodes.Count,
            checked_semantic_rules = readinessReport.CheckedRuleCount,
            topological_order = closureReport.TopologicalOrder,
            ready_next = closureReport.ReadyNextObligations,
            blocked_by_static_only_claims = closureReport.BlockedByStaticOnlyClaims,
            satisfied_depends_on_open = closureReport.SatisfiedDependsOnOpenEdges,
            obligation_issues = closureReport.Issues,
            semantic_violations = readinessReport.Violations
        });
    }

    /// <summary>
    /// #183 (Gem 1B): 5-state static-vs-runtime correlation matrix with side_alignment and cor_/contra_ IDs.
    /// </summary>
    public static ToolCallResult HandleCorrelateStaticRuntime(JsonElement arguments)
    {
        string subjectId = arguments.TryGetProperty("subject_id", out JsonElement sidEl) && sidEl.ValueKind == JsonValueKind.String
            ? (sidEl.GetString() ?? "func_0x1000")
            : "func_0x1000";
        string? staticBefore = arguments.TryGetProperty("static_before", out JsonElement sbEl) ? sbEl.GetString() : "sha_a";
        string? staticAfter = arguments.TryGetProperty("static_after", out JsonElement saEl) ? saEl.GetString() : "sha_b";
        string? runtimeBefore = arguments.TryGetProperty("runtime_before", out JsonElement rbEl) ? rbEl.GetString() : "sha_a";
        string? runtimeAfter = arguments.TryGetProperty("runtime_after", out JsonElement raEl) ? raEl.GetString() : "sha_b";
        bool staticTrunc = arguments.TryGetProperty("static_truncated", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.True;
        bool runtimeTrunc = arguments.TryGetProperty("runtime_truncated", out JsonElement rtEl) && rtEl.ValueKind == JsonValueKind.True;

        Kongor.Cli.Mcp.Services.StaticRuntimeCorrelationRecord record =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.CorrelateStaticAndRuntime(
                subjectId,
                staticBefore,
                staticAfter,
                runtimeBefore,
                runtimeAfter,
                staticTrunc,
                runtimeTrunc);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "correlate_static_runtime",
            correlation_id = record.CorrelationId,
            contradiction_id = record.ContradictionId,
            subject_id = record.SubjectId,
            state = record.State,
            side_alignment = record.SideAlignment,
            classification = record.Classification,
            is_truncated = record.IsTruncated
        });
    }

    /// <summary>
    /// #185 (Gem 1C): Ghidra HighFunction P-Code def_use, parameter_uses, block_membership, and typed effects.
    /// </summary>
    public static ToolCallResult HandleHighPCodeGraph(JsonElement arguments)
    {
        string funcAddr = arguments.TryGetProperty("function_address", out JsonElement faEl) && faEl.ValueKind == JsonValueKind.String
            ? (faEl.GetString() ?? "0x6815a0")
            : "0x6815a0";
        int maxOps = arguments.TryGetProperty("max_operations", out JsonElement moEl) && moEl.TryGetInt32(out int moVal)
            ? moVal
            : 256;

        List<Kongor.Cli.Mcp.Services.HighPCodeOperationInput> ops = new List<Kongor.Cli.Mcp.Services.HighPCodeOperationInput>();
        if (arguments.TryGetProperty("operations", out JsonElement opsEl) && opsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in opsEl.EnumerateArray())
            {
                string opId = item.TryGetProperty("op_id", out JsonElement idEl) ? (idEl.GetString() ?? "op_0") : "op_0";
                string opcode = item.TryGetProperty("opcode", out JsonElement ocEl) ? (ocEl.GetString() ?? "COPY") : "COPY";
                string addr = item.TryGetProperty("address", out JsonElement aEl) ? (aEl.GetString() ?? funcAddr) : funcAddr;
                string? bbId = item.TryGetProperty("basic_block_id", out JsonElement bbEl) ? bbEl.GetString() : "bb_0";
                string? outVn = item.TryGetProperty("output", out JsonElement outEl) ? outEl.GetString() : null;
                string? target = item.TryGetProperty("target", out JsonElement tgtEl) ? tgtEl.GetString() : null;

                List<string> inputs = new List<string>();
                if (item.TryGetProperty("inputs", out JsonElement inArr) && inArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement inItem in inArr.EnumerateArray())
                    {
                        if (inItem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(inItem.GetString()))
                        {
                            inputs.Add(inItem.GetString()!);
                        }
                    }
                }
                ops.Add(new Kongor.Cli.Mcp.Services.HighPCodeOperationInput(opId, opcode, addr, bbId, outVn, inputs, target));
            }
        }
        else
        {
            ops.Add(new Kongor.Cli.Mcp.Services.HighPCodeOperationInput("op_0", "LOAD", "0x6815a0", "bb_0", "v_ptr", DefaultPCodeLoadInputs));
            ops.Add(new Kongor.Cli.Mcp.Services.HighPCodeOperationInput("op_1", "CALLIND", "0x6815a8", "bb_0", "v_ret", DefaultPCodeCallIndInputs, "vtable_slot_0x18"));
        }

        Kongor.Cli.Mcp.Services.HighPCodeDefUseAndEffectGraph graph =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.BuildHighPCodeDefUseAndEffectGraph(funcAddr, ops, maxOps);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "high_pcode_graph",
            function_address = graph.FunctionAddress,
            def_use = graph.DefUseChains,
            parameter_uses = graph.ParameterUses,
            block_membership = graph.BlockMembership,
            effects = graph.Effects,
            total_operations = graph.TotalOperations,
            emitted_operations = graph.EmittedOperations,
            omitted_operations_lower_bound = graph.OmittedOperationsLowerBound
        });
    }

    /// <summary>
    /// #186 (Gem 1G): 28-node / 16-edge typed native investigation DAG.
    /// </summary>
    public static ToolCallResult HandleInvestigationDag(JsonElement arguments)
    {
        List<Kongor.Cli.Mcp.Services.InvestigationDagNode> nodes = new List<Kongor.Cli.Mcp.Services.InvestigationDagNode>();
        List<Kongor.Cli.Mcp.Services.InvestigationDagEdge> edges = new List<Kongor.Cli.Mcp.Services.InvestigationDagEdge>();

        if (arguments.TryGetProperty("nodes", out JsonElement nArr) && nArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in nArr.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out JsonElement iEl) ? (iEl.GetString() ?? "n0") : "n0";
                string kind = item.TryGetProperty("kind", out JsonElement kEl) ? (kEl.GetString() ?? "function") : "function";
                string label = item.TryGetProperty("label", out JsonElement lEl) ? (lEl.GetString() ?? id) : id;
                string status = item.TryGetProperty("status", out JsonElement sEl) ? (sEl.GetString() ?? "observed") : "observed";
                nodes.Add(new Kongor.Cli.Mcp.Services.InvestigationDagNode(id, kind, label, status));
            }
        }

        if (arguments.TryGetProperty("edges", out JsonElement eArr) && eArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in eArr.EnumerateArray())
            {
                string fromId = item.TryGetProperty("from", out JsonElement fEl) ? (fEl.GetString() ?? "") : "";
                string toId = item.TryGetProperty("to", out JsonElement tEl) ? (tEl.GetString() ?? "") : "";
                string kind = item.TryGetProperty("kind", out JsonElement kEl) ? (kEl.GetString() ?? "calls") : "calls";
                string status = item.TryGetProperty("status", out JsonElement sEl) ? (sEl.GetString() ?? "observed") : "observed";
                edges.Add(new Kongor.Cli.Mcp.Services.InvestigationDagEdge(fromId, toId, kind, status));
            }
        }

        Kongor.Cli.Mcp.Services.InvestigationDagEvaluation eval =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.EvaluateInvestigationDag(nodes, edges);

        return ToolCallResult.Success(new
        {
            success = eval.IsValid,
            action = "investigation_dag",
            is_valid = eval.IsValid,
            supported_node_kinds = eval.SupportedNodeKindCount,
            supported_edge_kinds = eval.SupportedEdgeKindCount,
            node_count = eval.NodeCount,
            edge_count = eval.EdgeCount,
            effective_statuses = eval.EffectiveNodeStatuses,
            corroborated_nodes = eval.CorroboratedNodeIds,
            contradicted_nodes = eval.ContradictedNodeIds,
            schema_errors = eval.SchemaErrors
        });
    }

    /// <summary>
    /// #187 (Gem 1I): 8-facet cleanroom equivalence comparator.
    /// </summary>
    public static ToolCallResult HandleCompareEquivalence8Facet(JsonElement arguments)
    {
        string origCode = arguments.TryGetProperty("original_pseudocode", out JsonElement opEl) && opEl.ValueKind == JsonValueKind.String
            ? (opEl.GetString() ?? "int f(int a) { return a + 1; }")
            : "int f(int a) { return a + 1; }";
        string cleanCode = arguments.TryGetProperty("cleanroom_pseudocode", out JsonElement cpEl) && cpEl.ValueKind == JsonValueKind.String
            ? (cpEl.GetString() ?? origCode)
            : origCode;

        Kongor.Cli.Mcp.Services.CleanroomFunctionSnapshot origSnap = new Kongor.Cli.Mcp.Services.CleanroomFunctionSnapshot(
            Name: "OriginalFunc",
            ReturnType: "int",
            ParameterCount: 1,
            CallingConvention: "__fastcall",
            Pseudocode: origCode,
            AssemblyInstructions: DefaultOrigAssembly,
            Comments: DefaultObligationComments,
            Callees: Array.Empty<string>(),
            References: Array.Empty<string>(),
            StringLiterals: Array.Empty<string>(),
            BasicBlockCount: 1,
            EdgeCount: 0,
            CyclomaticComplexity: 1);

        Kongor.Cli.Mcp.Services.CleanroomFunctionSnapshot cleanSnap = new Kongor.Cli.Mcp.Services.CleanroomFunctionSnapshot(
            Name: "CleanroomFunc",
            ReturnType: "int",
            ParameterCount: 1,
            CallingConvention: "__fastcall",
            Pseudocode: cleanCode,
            AssemblyInstructions: DefaultCleanAssembly,
            Comments: DefaultObligationComments,
            Callees: Array.Empty<string>(),
            References: Array.Empty<string>(),
            StringLiterals: Array.Empty<string>(),
            BasicBlockCount: 1,
            EdgeCount: 0,
            CyclomaticComplexity: 1);

        Kongor.Cli.Mcp.Services.CleanroomEquivalenceReport report =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.CompareCleanroomEquivalence8Facet(origSnap, cleanSnap);

        return ToolCallResult.Success(new
        {
            success = true,
            action = "compare_equivalence",
            is_equivalent = report.IsEquivalent,
            matched_facets = report.MatchedFacetCount,
            total_facets = report.TotalFacetCount,
            facets = report.Facets
        });
    }

    /// <summary>
    /// #193 (Gem 1M): Bounded 8 MiB PE .rsrc hierarchy &amp; Type-16/Type-24 manifest forensics.
    /// </summary>
    public static ToolCallResult HandleInspectPeResources(JsonElement arguments)
    {
        byte[] rsrcBytes = Array.Empty<byte>();
        if (arguments.TryGetProperty("rsrc_hex", out JsonElement hexEl) && !string.IsNullOrWhiteSpace(hexEl.GetString()))
        {
            rsrcBytes = Convert.FromHexString(hexEl.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        Kongor.Cli.Mcp.Services.PeResourceForensicsReport report =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.ParsePeResourceHierarchyAndManifest(rsrcBytes);

        return ToolCallResult.Success(new
        {
            success = report.IsWithinBudget,
            action = "inspect_pe_resources",
            is_within_budget = report.IsWithinBudget,
            resource_section_bytes = report.ResourceSectionBytes,
            budget_limit_bytes = report.BudgetLimitBytes,
            entry_count = report.Entries.Count,
            entries = report.Entries,
            version_info = report.VersionInfo,
            manifest_xml = report.ManifestXml,
            requested_execution_level = report.RequestedExecutionLevel,
            dpi_aware = report.DpiAware,
            long_path_aware = report.LongPathAware,
            diagnostic_message = report.DiagnosticMessage
        });
    }

    /// <summary>
    /// #194 (Gem 1R): COFF machine &amp; ReadyToRun OS-override XOR mask decoder (64 MiB budget).
    /// </summary>
    public static ToolCallResult HandleDecodeCoffR2R(JsonElement arguments)
    {
        byte[] binBytes = new byte[] { 0x20, 0xC0 }; // 0x8664 ^ 0x4644 = 0xC020 -> little-endian 0x20, 0xC0
        long declaredBytes = -1;

        if (arguments.TryGetProperty("bytes_hex", out JsonElement hexEl) && !string.IsNullOrWhiteSpace(hexEl.GetString()))
        {
            binBytes = Convert.FromHexString(hexEl.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal));
        }
        else if (arguments.TryGetProperty("raw_machine", out JsonElement rmEl))
        {
            ushort rawVal = 0xC020;
            if (rmEl.ValueKind == JsonValueKind.Number && rmEl.TryGetUInt16(out ushort uVal))
            {
                rawVal = uVal;
            }
            else if (rmEl.ValueKind == JsonValueKind.String)
            {
                string rmStr = (rmEl.GetString() ?? string.Empty).Trim();
                if (rmStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    rmStr = rmStr.Substring(2);
                }
                if (ushort.TryParse(rmStr, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort pVal))
                {
                    rawVal = pVal;
                }
            }
            binBytes = new byte[] { (byte)(rawVal & 0xFF), (byte)((rawVal >> 8) & 0xFF) };
        }
        else if (arguments.TryGetProperty("binary_path", out JsonElement bpEl) &&
                 bpEl.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(bpEl.GetString()) &&
                 File.Exists(bpEl.GetString()!))
        {
            FileInfo fi = new FileInfo(bpEl.GetString()!);
            declaredBytes = fi.Length;
            if (fi.Length <= Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.MaxCoffWorkingMemoryBudgetBytes)
            {
                binBytes = File.ReadAllBytes(fi.FullName);
            }
        }

        if (arguments.TryGetProperty("declared_total_bytes", out JsonElement dtEl) && dtEl.TryGetInt64(out long dtVal))
        {
            declaredBytes = dtVal;
        }

        Kongor.Cli.Mcp.Services.CoffAndReadyToRunForensicsReport report =
            Kongor.Cli.Mcp.Services.ReaEpistemicForensicsEngine.DecodeCoffAndReadyToRunHeader(binBytes, declaredBytes);

        return ToolCallResult.Success(new
        {
            success = report.IsWithinMemoryBudget,
            action = "decode_coff_r2r",
            is_within_memory_budget = report.IsWithinMemoryBudget,
            working_memory_budget_bytes = report.WorkingMemoryBudgetBytes,
            raw_machine = $"0x{report.RawMachine:X4}",
            decoded_base_machine = $"0x{report.DecodedBaseMachine:X4}",
            applied_xor_mask = $"0x{report.AppliedOsOverrideXorMask:X4}",
            architecture = report.Architecture,
            target_os_override = report.TargetOsOverride,
            has_ready_to_run_header = report.HasReadyToRunHeader,
            r2r_major_version = report.R2RMajorVersion,
            r2r_minor_version = report.R2RMinorVersion,
            r2r_flags = $"0x{report.R2RFlags:X8}",
            r2r_section_count = report.R2RSectionCount,
            diagnostic_message = report.DiagnosticMessage
        });
    }

    /// <summary>
    /// Phase 16 (#198 — ran-j/PS2Recomp Gem 4B): 5-instruction backward base-register tracker detecting stores into executable <c>.text</c> ranges.
    /// </summary>
    public static SelfModifyingCodeAuditReport AuditSelfModifyingCodeWrites(
        IReadOnlyList<DisassembledInstructionRow> instructions,
        ulong execSectionStart,
        ulong execSectionEnd)
    {
        return DisassemblyCfgLaneAnalyzer.AuditSelfModifyingCodeWrites(instructions, execSectionStart, execSectionEnd);
    }

    /// <summary>
    /// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): O(V+E) Tarjan SCC recursion cycle detector.
    /// </summary>
    public static TarjanRecursionCycleReport DetectRecursionCyclesTarjan(
        IReadOnlyDictionary<string, IReadOnlyList<string>> callGraph,
        int defaultMaxDepthClamp = 64)
    {
        return AbiSafeDetourSynthesizer.DetectRecursionCyclesTarjan(callGraph, defaultMaxDepthClamp);
    }

    /// <summary>
    /// Phase 16 (#198 — ran-j/PS2Recomp Gem 4B): MCP handler for <c>action: "audit_smc_writes"</c>.
    /// </summary>
    public static ToolCallResult HandleAuditSmcWrites(JsonElement arguments)
    {
        ulong sectionStart = 0x1000UL;
        if (arguments.TryGetProperty("section_start", out JsonElement ssEl))
        {
            sectionStart = ParseHexOrUInt64Arg(ssEl, 0x1000UL);
        }

        ulong sectionEnd = 0x5000UL;
        if (arguments.TryGetProperty("section_end", out JsonElement seEl))
        {
            sectionEnd = ParseHexOrUInt64Arg(seEl, 0x5000UL);
        }

        List<DisassembledInstructionRow> rows = new List<DisassembledInstructionRow>();
        if (arguments.TryGetProperty("instructions", out JsonElement insnsEl) && insnsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in insnsEl.EnumerateArray())
            {
                ulong addr = item.TryGetProperty("address", out JsonElement aEl) ? ParseHexOrUInt64Arg(aEl, 0UL) : 0UL;
                int len = item.TryGetProperty("length", out JsonElement lEl) && lEl.TryGetInt32(out int lVal) ? lVal : 4;
                string mnem = item.TryGetProperty("mnemonic", out JsonElement mEl) ? (mEl.GetString() ?? "nop") : "nop";
                string ops = item.TryGetProperty("operands", out JsonElement oEl) ? (oEl.GetString() ?? string.Empty) : string.Empty;
                rows.Add(new DisassembledInstructionRow(addr, len, mnem, ops));
            }
        }
        else
        {
            rows.Add(new DisassembledInstructionRow(0x1000UL, 7, "lea", "rcx, [rip + 0x100]"));
            rows.Add(new DisassembledInstructionRow(0x1007UL, 3, "mov", "eax, 0x90909090"));
            rows.Add(new DisassembledInstructionRow(0x100AUL, 3, "mov", "[rcx + 0x10], eax"));
        }

        SelfModifyingCodeAuditReport report = AuditSelfModifyingCodeWrites(rows, sectionStart, sectionEnd);
        return ToolCallResult.Success(new
        {
            success = true,
            action = "audit_smc_writes",
            has_smc_writes = report.HasSmcWrites,
            exec_section_start = $"0x{report.ExecSectionStart:X}",
            exec_section_end = $"0x{report.ExecSectionEnd:X}",
            total_instructions_scanned = report.TotalInstructionsScanned,
            hazard_count = report.Hazards.Count,
            hazards = report.Hazards
        });
    }

    /// <summary>
    /// Phase 16 (#199 — ran-j/PS2Recomp Gem 4E): MCP handler for <c>action: "detect_recursion_cycles"</c>.
    /// </summary>
    public static ToolCallResult HandleDetectRecursionCycles(JsonElement arguments)
    {
        int maxDepthClamp = 64;
        if (arguments.TryGetProperty("max_depth_clamp", out JsonElement mdEl) && mdEl.TryGetInt32(out int mdVal) && mdVal > 0)
        {
            maxDepthClamp = mdVal;
        }

        Dictionary<string, IReadOnlyList<string>> callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("call_graph", out JsonElement cgEl) && cgEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in cgEl.EnumerateObject())
            {
                List<string> callees = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            callees.Add(item.GetString()!);
                        }
                    }
                }
                callGraph[prop.Name] = callees;
            }
        }
        else
        {
            callGraph["CWidget::Update"] = new string[] { "CWidget::RecalcLayout" };
            callGraph["CWidget::RecalcLayout"] = new string[] { "CWidget::Update" };
            callGraph["CSkeleton::BoneWalk"] = new string[] { "CSkeleton::BoneWalk" };
        }

        TarjanRecursionCycleReport report = DetectRecursionCyclesTarjan(callGraph, maxDepthClamp);
        return ToolCallResult.Success(new
        {
            success = true,
            action = "detect_recursion_cycles",
            vertex_count = report.VertexCount,
            edge_count = report.EdgeCount,
            has_recursion_cycles = report.HasRecursionCycles,
            cycle_count = report.Cycles.Count,
            cycles = report.Cycles
        });
    }

    private static ulong ParseHexOrUInt64Arg(JsonElement el, ulong fallback)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out ulong numVal))
        {
            return numVal;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            string raw = (el.GetString() ?? string.Empty).Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong hexVal))
                {
                    return hexVal;
                }
            }
            else if (ulong.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ulong decVal))
            {
                return decVal;
            }
        }

        return fallback;
    }
}


