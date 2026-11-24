// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PS2RecompAndShadPs4Wave4Tests.cs
// System: Kongor.Mcp.Tests - Phase 16 (#196-#209) Verification Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Core.Services.Native;
using ReplayVulkanRenderer;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Wave v4 Phase 16 (#196-#209: wave4-ps2recomp-shadps4-binary-and-vulkan-gems) verification suite.
/// Protects:
/// - #196 Relocation-masked function signature trie (&gt;= 256 static bits &amp; &gt;= 8 instructions)
/// - #197 Stub/thunk &amp; auto-generated symbol filter (FUN_*, sub_*, thunk_*, nullsub_*)
/// - #198 Self-modifying code (SMC) &amp; dynamic patch write detector (5-instruction backward lookback)
/// - #199 Tarjan SCC mutual-recursion detector &amp; recursion-depth clamp detour synthesizer
/// - #200 Heuristic jump-table recovery with 50% code-range validity gate
/// - #201 Pattern-scanned code-cave bidirectional detour synthesizer (Mask_Jump32)
/// - #202 Ghidra function &amp; jump-table JSON map exporter &amp; headless crash symbolizer import
/// - #203 Headless RenderDoc dynamic capture bridge (eRENDERDOC_API_Version_1_6_0 &amp; UnloadCrashHandler)
/// - #204 Timeline-semaphore dirty-state command scheduler &amp; priority-queue deferred retirement
/// - #205 Three-tier staging buffer pool with TickFrame() idle-epoch garbage collection
/// - #206 Two-level 4KB page bitmask memory tracker (cpu_modified / gpu_modified via TrailingZeroCount)
/// - #207 8-slot download_buffer ring &amp; GPU page-fault bitmask scanner
/// - #208 64-byte cache-line aligned hot-page texture &amp; pointer lookup (8KB page mask ~0x1FFFULL)
/// </summary>
public sealed class PS2RecompAndShadPs4Wave4Tests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir, "native", "proxies", "native_guard")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Repository root not found. Run 'dotnet test' from within the ProjectKongor workspace.");
    }

    [Fact]
    public void RelocationMaskedSignatureTrie_Enforces256BitAnd8InsnFloor_And_MatchesAcrossRelocatedBuilds()
    {
        // Build v1 of a 10-instruction function prologue containing relocated call/branch words (masked Lower26/Lower16)
        MaskedInstructionPattern[] buildV1Patterns =
        [
            new MaskedInstructionPattern(0x27BDFFC0U, 0xFFFFFFFFU), // 32 static bits
            new MaskedInstructionPattern(0xAFBF0030U, 0xFFFFFFFFU), // 32 static bits
            new MaskedInstructionPattern(0xAFB00020U, 0xFFFFFFFFU), // 32 static bits
            new MaskedInstructionPattern(0x0080802DU, 0xFFFFFFFFU), // 32 static bits
            new MaskedInstructionPattern(0x0C041234U, 0xFC000000U), // JAL target masked (6 static bits)
            new MaskedInstructionPattern(0x00000000U, 0xFFFFFFFFU), // 32 static bits
            new MaskedInstructionPattern(0x3C02801AU, 0xFFFF0000U), // LUI upper16 masked (16 static bits)
            new MaskedInstructionPattern(0x8C435678U, 0xFFFF0000U), // LW disp16 masked (16 static bits)
            new MaskedInstructionPattern(0x10600008U, 0xFFFFFFFFU), // 32 static bits
            new MaskedInstructionPattern(0x0200202DU, 0xFFFFFFFFU)  // 32 static bits (total = 262 static bits >= 256)
        ];

        RelocationMaskedSignature sigV1 = SymbolCorrelatorService.BuildRelocationMaskedSignature(
            "CWorld::UpdateEntityFrame",
            buildV1Patterns);

        Assert.True(sigV1.IsEligibleForCorrelation);
        Assert.Equal(10, sigV1.InstructionCount);
        Assert.True(sigV1.StaticBitCount >= SymbolCorrelatorService.MinStaticBits);

        RelocationMaskedSignatureTrie trie = new RelocationMaskedSignatureTrie();
        Assert.True(trie.TryInsert(sigV1));
        Assert.Equal(1, trie.InsertedSignatureCount);

        // Simulate relocated build v2 where JAL target and LUI/LW displacements changed completely
        ReadOnlySpan<uint> buildV2RelocatedStream =
        [
            0x27BDFFC0U,
            0xAFBF0030U,
            0xAFB00020U,
            0x0080802DU,
            0x0C09FEDCU, // relocated JAL target
            0x00000000U,
            0x3C029044U, // relocated LUI immediate
            0x8C43ABCDU, // relocated LW displacement
            0x10600008U,
            0x0200202DU
        ];

        IReadOnlyList<string> matchedSymbols = trie.MatchInstructionStream(buildV2RelocatedStream);
        Assert.Contains("CWorld::UpdateEntityFrame", matchedSymbols);

        IReadOnlyList<string> byHash = trie.LookupByMaskedSha1(sigV1.MaskedSha1Hex);
        Assert.Contains("CWorld::UpdateEntityFrame", byHash);

        // Mutant negative control 1: < 8 instructions must be rejected
        MaskedInstructionPattern[] tooFewPatterns =
        [
            new MaskedInstructionPattern(0x27BDFFC0U, 0xFFFFFFFFU),
            new MaskedInstructionPattern(0xAFBF0030U, 0xFFFFFFFFU),
            new MaskedInstructionPattern(0xAFB00020U, 0xFFFFFFFFU),
            new MaskedInstructionPattern(0x0080802DU, 0xFFFFFFFFU),
            new MaskedInstructionPattern(0x00000000U, 0xFFFFFFFFU),
            new MaskedInstructionPattern(0x10600008U, 0xFFFFFFFFU),
            new MaskedInstructionPattern(0x0200202DU, 0xFFFFFFFFU)
        ];
        RelocationMaskedSignature shortSig = SymbolCorrelatorService.BuildRelocationMaskedSignature(
            "TooShortFn",
            tooFewPatterns);
        Assert.False(shortSig.IsEligibleForCorrelation);
        Assert.False(trie.TryInsert(shortSig));
        Assert.Equal(1, trie.RejectedShortSignatureCount);

        // Mutant negative control 2: >= 8 instructions but < 256 static bits must be rejected
        MaskedInstructionPattern[] lowEntropyPatterns =
        [
            new MaskedInstructionPattern(0x0C000001U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000002U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000003U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000004U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000005U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000006U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000007U, 0xFC000000U),
            new MaskedInstructionPattern(0x0C000008U, 0xFC000000U)
        ];
        RelocationMaskedSignature lowEntropySig = SymbolCorrelatorService.BuildRelocationMaskedSignature(
            "LowEntropyStub",
            lowEntropyPatterns);
        Assert.False(lowEntropySig.IsEligibleForCorrelation);
        Assert.False(trie.TryInsert(lowEntropySig));
    }

    [Fact]
    public void StubThunkAndAutoGeneratedSymbolFilter_RejectsDummyLabels_And_ClassifiesSingleInstructionStubs()
    {
        // #197 Auto-generated symbol prefix filter
        string funDummy = string.Concat("FUN_", "140012340");
        string subDummy = string.Concat("sub_", "180004500");
        string thunkDummy = string.Concat("thunk_FUN_", "140001000");
        Assert.False(SymbolCorrelatorService.IsReliableSymbolName(funDummy));
        Assert.False(SymbolCorrelatorService.IsReliableSymbolName(subDummy));
        Assert.False(SymbolCorrelatorService.IsReliableSymbolName(thunkDummy));
        Assert.False(SymbolCorrelatorService.IsReliableSymbolName("LAB_140002000"));
        Assert.False(SymbolCorrelatorService.IsReliableSymbolName("nullsub_42"));
        Assert.False(SymbolCorrelatorService.IsReliableSymbolName("0x140005000"));
        Assert.True(SymbolCorrelatorService.IsReliableSymbolName("CHost::ExecuteFrame"));

        // #197 Single-instruction stub classifier
        Assert.Equal(
            FunctionStubKind.PureReturnNoOpStub,
            SymbolCorrelatorService.ClassifyFunctionStubKind([0xC3]));

        Assert.Equal(
            FunctionStubKind.Int3PaddingOrTrap,
            SymbolCorrelatorService.ClassifyFunctionStubKind([0xCC, 0xCC, 0xCC, 0xCC]));

        Assert.Equal(
            FunctionStubKind.TailJumpThunk,
            SymbolCorrelatorService.ClassifyFunctionStubKind([0xE9, 0x20, 0x01, 0x00, 0x00, 0xCC, 0xCC]));

        Assert.Equal(
            FunctionStubKind.ImportThunk,
            SymbolCorrelatorService.ClassifyFunctionStubKind([0xFF, 0x25, 0x00, 0x20, 0x05, 0x00]));

        Assert.Equal(
            FunctionStubKind.CrtSecurityCookieStub,
            SymbolCorrelatorService.ClassifyFunctionStubKind([0x48, 0x3B, 0x0D, 0x10, 0x20, 0x03, 0x00]));

        Assert.Equal(
            FunctionStubKind.None,
            SymbolCorrelatorService.ClassifyFunctionStubKind(
                [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x83, 0xEC, 0x20, 0xC3],
                "CConsole::AddCmdBuffer"));
    }

    [Fact]
    public void SelfModifyingCodeWriteAuditor_FlagsExecutableSectionStores_With5InsnLookback_And_IgnoresDataWrites()
    {
        // #198: AuditSelfModifyingCodeWrites with 5-instruction backward register lookback
        DisassembledInstructionRow[] smcStream =
        [
            new DisassembledInstructionRow(0x140002000UL, 7, "lea", "r10, [0x140001000]"),
            new DisassembledInstructionRow(0x140002007UL, 2, "xor", "eax, eax"),
            new DisassembledInstructionRow(0x140002009UL, 11, "mov", "dword ptr [r10 + 0x500], 0x90909090")
        ];

        SelfModifyingCodeAuditReport hazardReport =
            DisassemblyCfgLaneAnalyzer.AuditSelfModifyingCodeWrites(
                smcStream,
                execSectionStart: 0x140001000UL,
                execSectionEnd: 0x140008000UL);

        Assert.True(hazardReport.HasSmcWrites);
        Assert.Single(hazardReport.Hazards);
        Assert.Equal(0x140002009UL, hazardReport.Hazards[0].StoreInstructionAddress);
        Assert.Equal(0x140001500UL, hazardReport.Hazards[0].ResolvedTargetAddress);
        Assert.Equal("r10", hazardReport.Hazards[0].BaseRegister);
        Assert.Equal(2, hazardReport.Hazards[0].LookbackDistance);

        // Mutant negative control: store targeting .data section (0x140015000 outside [0x140001000..0x140008000)) must NOT flag SMC
        DisassembledInstructionRow[] safeDataStream =
        [
            new DisassembledInstructionRow(0x140002000UL, 7, "lea", "r10, [0x140000000]"),
            new DisassembledInstructionRow(0x140002007UL, 11, "mov", "dword ptr [r10 + 0x15000], 1")
        ];
        SelfModifyingCodeAuditReport safeReport =
            DisassemblyCfgLaneAnalyzer.AuditSelfModifyingCodeWrites(
                safeDataStream,
                execSectionStart: 0x140001000UL,
                execSectionEnd: 0x140008000UL);
        Assert.False(safeReport.HasSmcWrites);

        // Regression test (#198): Negative RIP-relative lea + secondary register add + implicit [rdi] stosd
        DisassembledInstructionRow[] stosAndRegAddStream =
        [
            new DisassembledInstructionRow(0x140002100UL, 7, "lea", "rdi, [rip - 0x107]"), // nextRip=0x140002107 -> 0x140002000
            new DisassembledInstructionRow(0x140002107UL, 7, "mov", "rcx, 0x40"),
            new DisassembledInstructionRow(0x14000210EUL, 3, "add", "rdi, rcx"),            // 0x140002040
            new DisassembledInstructionRow(0x140002111UL, 1, "stosd", "")                   // writes to [rdi] = 0x140002040
        ];
        SelfModifyingCodeAuditReport stosReport =
            DisassemblyCfgLaneAnalyzer.AuditSelfModifyingCodeWrites(
                stosAndRegAddStream,
                execSectionStart: 0x140001000UL,
                execSectionEnd: 0x140008000UL);
        Assert.True(stosReport.HasSmcWrites);
        Assert.Single(stosReport.Hazards);
        Assert.Equal(0x140002040UL, stosReport.Hazards[0].ResolvedTargetAddress);

        // Regression negative control (#198): clobbered base register after lea must NOT resolve to stale lea
        DisassembledInstructionRow[] clobberedStream =
        [
            new DisassembledInstructionRow(0x140002000UL, 7, "lea", "r10, [0x140001500]"),
            new DisassembledInstructionRow(0x140002007UL, 4, "shr", "r10, 4"),
            new DisassembledInstructionRow(0x14000200BUL, 7, "mov", "dword ptr [r10], 1")
        ];
        SelfModifyingCodeAuditReport clobberedReport =
            DisassemblyCfgLaneAnalyzer.AuditSelfModifyingCodeWrites(
                clobberedStream,
                execSectionStart: 0x140001000UL,
                execSectionEnd: 0x140008000UL);
        Assert.False(clobberedReport.HasSmcWrites);
    }

    [Fact]
    public void TarjanSccRecursionDetector_IsolatesSelfAndMutualCycles_And_SynthesizesDepthClampDetour()
    {
        // #199: Build call graph with:
        // - Direct self-recursion: FnSelf -> FnSelf
        // - 3-node mutual recursion cycle: FnA -> FnB -> FnC -> FnA
        // - Acyclic caller: FnRoot -> FnA
        Dictionary<string, IReadOnlyList<string>> callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["FnSelf"] = ["FnSelf"],
            ["FnA"] = ["FnB"],
            ["FnB"] = ["FnC"],
            ["FnC"] = ["FnA"],
            ["FnRoot"] = ["FnA"]
        };

        TarjanRecursionCycleReport report =
            AbiSafeDetourSynthesizer.DetectRecursionCyclesTarjan(callGraph, defaultMaxDepthClamp: 48);

        Assert.True(report.HasRecursionCycles);
        Assert.Equal(5, report.VertexCount);
        Assert.Equal(2, report.Cycles.Count);
        Assert.Contains(report.Cycles, c => c.Kind == RecursionCycleKind.SelfRecursion && c.Functions.Contains("FnSelf"));
        Assert.Contains(report.Cycles, c => c.Kind == RecursionCycleKind.MutualRecursion && c.Functions.Count == 3);
        Assert.DoesNotContain(report.Cycles, c => c.Functions.Contains("FnRoot"));

        // Synthesize recursion-depth clamp detour directly
        SynthesizedDetour clampDetour =
            AbiSafeDetourSynthesizer.SynthesizeRecursionDepthClampDetour(
                symbolName: "CInterface::RecalculateWidgets",
                maxDepthClamp: 48);
        Assert.False(clampDetour.CanLiveInject);
        Assert.True(clampDetour.Maintains16ByteStackAlignment);
        Assert.NotEmpty(clampDetour.DetourBytesHex);
        Assert.Contains("48", clampDetour.SafetyVerification);
    }

    [Fact]
    public void HeuristicJumpTableRecovery_AcceptsValidCodeSwitchTable_And_RejectsBelow50PercentValidity()
    {
        // #200: Valid 4-entry switch jump table inside code section [0x1000..0x9000)
        ReadOnlySpan<uint> validRvas = [0x2100U, 0x2140U, 0x2180U, 0x21C0U];
        JumpTableRecoveryReport validReport =
            DisassemblyCfgLaneAnalyzer.RecoverJumpTableTargets(
                rawTableEntries: validRvas,
                execStartRva: 0x1000UL,
                execEndRva: 0x9000UL,
                tableBaseRva: 0xA000UL,
                imageBase: 0x140000000UL);

        Assert.True(validReport.IsValidJumpTable);
        Assert.Equal(4, validReport.ValidCodeTargets);
        Assert.Equal(4, validReport.TotalEntriesRead);
        Assert.Equal(1.0, validReport.ValidTargetRatio, 3);

        // Mutant negative control: 1 valid code RVA followed by 3 non-code data words (25% < 50% threshold)
        ReadOnlySpan<uint> pollutedRvas = [0x2100U, 0x99000U, 0x9A000U, 0x9B000U];
        JumpTableRecoveryReport rejectedReport =
            DisassemblyCfgLaneAnalyzer.RecoverJumpTableTargets(
                rawTableEntries: pollutedRvas,
                execStartRva: 0x1000UL,
                execEndRva: 0x9000UL,
                tableBaseRva: 0xA100UL,
                imageBase: 0x140000000UL);

        Assert.False(rejectedReport.IsValidJumpTable);
        Assert.Empty(rejectedReport.RecoveredTargets);
    }

    [Fact]
    public void BidirectionalCodeCaveDetourSynthesizer_EmitsForwardAndReturnRel32Jumps_And_NopPadding()
    {
        // #201: Module buffer with code at 0x100 and a contiguous 0xCC padding cave at 0x180
        byte[] moduleImage = new byte[512];
        for (int i = 0; i < 0x180; i++)
        {
            moduleImage[i] = 0x48;
        }
        for (int i = 0x180; i < 0x1C0; i++)
        {
            moduleImage[i] = 0xCC; // 64-byte int3 code cave
        }

        byte[] replacementPayload = [0x48, 0x31, 0xC0, 0x90]; // xor rax, rax; nop (4 bytes)
        BidirectionalCodeCaveDetourResult result =
            AbiSafeDetourSynthesizer.SynthesizeBidirectionalCodeCaveDetourInModule(
                moduleBaseAddress: 0x140000000UL,
                moduleBytes: moduleImage,
                patchOffsetInModule: 0x100,
                patchSize: 7,
                cavePayloadBytes: replacementPayload);

        Assert.True(result.IsValid);
        Assert.Equal(0x140000100UL, result.PatchAddress);
        Assert.Equal(0x140000180UL, result.CodeCaveAddress);

        // Entry stub: 5-byte E9 rel32 + 2 NOPs (0x90)
        Assert.Equal(7, result.PatchSiteBytes.Length);
        Assert.Equal(0xE9, result.PatchSiteBytes[0]);
        Assert.Equal(0x90, result.PatchSiteBytes[5]);
        Assert.Equal(0x90, result.PatchSiteBytes[6]);
        Assert.Equal(0x180 - (0x100 + 5), result.ForwardRel32Displacement);

        // Cave stub: 4-byte payload + 7-byte overwritten prologue + 5-byte E9 rel32 returning to 0x107
        Assert.Equal(16, result.CodeCaveBytes.Length);
        Assert.Equal(0xE9, result.CodeCaveBytes[11]);
        Assert.Equal(0x107 - (0x180 + 16), result.BackwardRel32Displacement);

        // Mutant negative control: cave > 2 GB away from patch site must be rejected
        BidirectionalCodeCaveDetourResult outOfRange =
            AbiSafeDetourSynthesizer.SynthesizeBidirectionalCodeCaveDetour(
                patchAddress: 0x140000100UL,
                overwrittenPrologueBytes: [0x48, 0x89, 0x5C, 0x24, 0x08, 0x90],
                codeCaveAddress: 0x240000100UL, // +4 GB away
                cavePayloadBytes: replacementPayload);
        Assert.False(outOfRange.IsValid);

        // Verify C++ runtime_patch_controller.cpp contract (#201): relocates original prologue bytes (payloadLen + patchSize + 5)
        string repoRoot = FindRepoRoot();
        string patchCtrlCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "runtime_patch_controller.cpp"));
        Assert.Contains("InstallCodeCaveDetour32", patchCtrlCpp);
        Assert.Contains("FindContiguousCodeCaveNear", patchCtrlCpp);
        Assert.Contains("payloadLen + patchSize + 5", patchCtrlCpp);
        Assert.DoesNotContain("first != 0x00", patchCtrlCpp);
    }

    [Fact]
    public void GhidraFunctionAndJumpTableMapExporter_ImportsCleanSymbols_And_ResolvesRvaBounds()
    {
        // #202: Structured JSON map with reliable functions, synthetic labels, and jump tables
        string funPlaceholder = string.Concat("FUN_", "140001500");
        string sampleJson = $$"""
        {
          "binary_name": "k2_x64.dll",
          "image_base": "0x180000000",
          "functions": [
            {
              "name": "CConsole::Execute",
              "start_rva": "0x1000",
              "end_rva": "0x1200",
              "calling_convention": "__fastcall",
              "stack_frame_size": 64,
              "is_thunk": false,
              "jump_tables": [
                {
                  "instruction_rva": "0x1040",
                  "targets": ["0x1060", "0x1080", "0x10C0"]
                }
              ]
            },
            {
              "name": "{{funPlaceholder}}",
              "start_rva": "0x1500",
              "end_rva": "0x1540",
              "calling_convention": "__fastcall",
              "stack_frame_size": 32,
              "is_thunk": false,
              "jump_tables": []
            }
          ]
        }
        """;

        HarvestedBinaryFunctionMap map = SymbolCorrelatorService.ImportHarvestedFunctionMapJson(sampleJson);

        Assert.Equal("k2_x64.dll", map.BinaryName);
        Assert.Equal(0x180000000UL, map.ImageBase);
        Assert.Equal(2, map.Functions.Count);
        Assert.True(map.Functions[0].IsReliableSymbol);
        Assert.False(map.Functions[1].IsReliableSymbol);

        HarvestedBinaryFunctionEntry? resolvedFn = map.ResolveFunctionContainingRva(0x1080U);
        Assert.NotNull(resolvedFn);
        Assert.Equal("CConsole::Execute", resolvedFn.Name);
        Assert.Single(resolvedFn.JumpTables);
        Assert.Equal(3, resolvedFn.JumpTables[0].TargetRvas.Count);

        // Regression test (#202): also accept top-level jump_tables and 'module'/'start'/'end' keys from ingest_binaries.py
        string legacyPyGhidraJson = """
        {
          "module": "cgame_x64.dll",
          "image_base": "0x180000000",
          "functions": [
            {
              "name": "CGameInterface::ProcessSnapshot",
              "start": "0x180002000",
              "end": "0x180002400",
              "is_reliable_symbol": true
            }
          ],
          "jump_tables": [
            {
              "address": "0x180002100",
              "targets": ["0x180002120", "0x180002160"]
            }
          ]
        }
        """;
        HarvestedBinaryFunctionMap legacyMap = SymbolCorrelatorService.ImportHarvestedFunctionMapJson(legacyPyGhidraJson);
        Assert.Equal("cgame_x64.dll", legacyMap.BinaryName);
        Assert.Single(legacyMap.Functions);
        Assert.Equal(0x2000U, legacyMap.Functions[0].StartRva);
        Assert.Equal(0x2400U, legacyMap.Functions[0].EndRva);
        Assert.Single(legacyMap.Functions[0].JumpTables);
        Assert.Equal(0x2100U, legacyMap.Functions[0].JumpTables[0].InstructionRva);
        Assert.Equal(2, legacyMap.Functions[0].JumpTables[0].TargetRvas.Count);

        // Verify Python exporter contract in ingest_binaries.py (#202)
        if (!NexusTestEnvironment.HasGameAssets) return;
        string repoRoot = FindRepoRoot();
        string ingestPy = File.ReadAllText(Path.Combine(repoRoot, "src", "tools", "ghidra", "ingest_binaries.py"));
        Assert.Contains("def harvest_binary_function_map", ingestPy);
        Assert.Contains("--harvest-json", ingestPy);
        Assert.Contains("\"binary_name\"", ingestPy);
        Assert.Contains("\"start_rva\"", ingestPy);
        Assert.Contains("\"end_rva\"", ingestPy);
    }

    [Fact]
    public async Task HeadlessRenderDocDynamicCaptureBridge_DiscoversDllFromDefaultIcon_UnloadsCrashHandler_And_Drives3StateMachine()
    {
        // #203: Registry DefaultIcon parser
        string? parsedDll = HeadlessRenderDocBridge.ParseRenderDocDllPathFromDefaultIcon(
            "\"C:\\Program Files\\RenderDoc\\qrenderdoc.exe\",0");
        Assert.Equal("C:\\Program Files\\RenderDoc\\renderdoc.dll", parsedDll);
        Assert.Null(HeadlessRenderDocBridge.ParseRenderDocDllPathFromDefaultIcon(""));

        HeadlessRenderDocBridge bridge = new HeadlessRenderDocBridge();
        Assert.Equal(RenderDocCaptureState.Idle, bridge.State);
        Assert.False(bridge.UnloadCrashHandlerInvoked);

        bool crashHandlerUnloadedCallbackRan = false;
        Assert.True(bridge.InitializeHeadlessSession("logs/captures/test_cap", () => crashHandlerUnloadedCallbackRan = true));
        Assert.True(bridge.UnloadCrashHandlerInvoked);
        Assert.True(crashHandlerUnloadedCallbackRan);

        // Mutant negative control: cannot begin frame when Idle
        Assert.False(bridge.OnPresentFrameBegin(10UL));

        // State machine: Idle -> Triggered -> InProgress -> Idle
        Assert.True(bridge.TriggerCapture("logs/captures/test_cap"));
        Assert.Equal(RenderDocCaptureState.Triggered, bridge.State);
        Assert.False(bridge.TriggerCapture("logs/captures/duplicate")); // cannot re-trigger while Triggered

        Assert.True(bridge.OnPresentFrameBegin(42UL));
        Assert.Equal(RenderDocCaptureState.InProgress, bridge.State);

        Assert.True(bridge.OnPresentFrameEnd(42UL));
        Assert.Equal(RenderDocCaptureState.Idle, bridge.State);
        Assert.Equal(1, bridge.CompletedCapturesCount);
        Assert.Equal("logs/captures/test_cap_f42.rdc", bridge.LastCapturedRdcPath);

        // Verify MCP handler integration
        VulkanManageHandler handler = new VulkanManageHandler(logger: _ => { });
        using JsonDocument doc = JsonDocument.Parse("""{"action":"renderdoc_capture","captureAction":"full_cycle","frameNumber":77}""");
        ToolCallResult mcpRes = await handler.HandleAsync(doc.RootElement, CancellationToken.None);
        Assert.False(mcpRes.IsError);
    }

    [Fact]
    public void TimelineSemaphoreDirtyStateScheduler_SuppressesRedundantBinds_And_RetiresResourcesWithoutFenceStalls()
    {
        // #204: DirtyStateFlags + priority-queue deferred retirement
        TimelineSemaphoreDirtyStateScheduler scheduler = new TimelineSemaphoreDirtyStateScheduler();

        // First bind after init (all flags dirty) must emit
        Assert.True(scheduler.TryBindViewport(1920f, 1080f));
        Assert.True(scheduler.TryBindScissor(1920u, 1080u));
        Assert.True(scheduler.TryBindDepthBias(1.0f, 2.0f));

        // Redundant binds with identical values must be suppressed
        Assert.False(scheduler.TryBindViewport(1920f, 1080f));
        Assert.False(scheduler.TryBindScissor(1920u, 1080u));
        Assert.False(scheduler.TryBindDepthBias(1.0f, 2.0f));
        Assert.Equal(3, scheduler.SuppressedStateBindCount);

        // Command buffer split must re-dirty all states so next bind emits once
        scheduler.OnCommandBufferSplit();
        Assert.True(scheduler.TryBindViewport(1920f, 1080f));

        // Out-of-order retirement enqueue, min-priority-queue retirement by completed GPU tick
        ulong tick1 = scheduler.SubmitCommandChunk(); // 1
        ulong tick2 = scheduler.SubmitCommandChunk(); // 2
        ulong tick3 = scheduler.SubmitCommandChunk(); // 3
        scheduler.EnqueueDeferredRetirement(0xAA03UL, "StagingBuf3", tick3);
        scheduler.EnqueueDeferredRetirement(0xAA01UL, "StagingBuf1", tick1);
        scheduler.EnqueueDeferredRetirement(0xAA02UL, "StagingBuf2", tick2);

        List<DeferredResourceRetirementEntry> retiredAtTick2 = scheduler.PollCompletedGpuTimeline(2UL);
        Assert.Equal(2, retiredAtTick2.Count);
        Assert.Equal(0xAA01UL, retiredAtTick2[0].ResourceHandle);
        Assert.Equal(0xAA02UL, retiredAtTick2[1].ResourceHandle);
        Assert.Equal(1, scheduler.PendingRetirementCount);
    }

    [Fact]
    public void ThreeTierStagingBufferPool_RoutesSmallToRing_ReusesLargeBuffers_And_EvictsAfterMaxIdleFrames()
    {
        // #205: Tier 1 ring (<= 256 KiB), Tier 2 large pool (> 256 KiB), Tier 3 TickFrame() idle GC
        ThreeTierStagingBufferPool pool = new ThreeTierStagingBufferPool(
            ringCapacityBytes: 1024UL * 1024UL,
            tier1MaxAllocBytes: 64UL * 1024UL,
            maxIdleFrames: 4U);

        StagingAllocationLease small1 = pool.Allocate(16UL * 1024UL, submissionTimelineTick: 1UL);
        StagingAllocationLease small2 = pool.Allocate(16UL * 1024UL, submissionTimelineTick: 1UL);
        Assert.Equal(StagingBufferTier.Tier1RingStreamBuffer, small1.Tier);
        Assert.Equal(StagingBufferTier.Tier1RingStreamBuffer, small2.Tier);
        Assert.Equal(0UL, small1.OffsetBytes);
        Assert.Equal(16UL * 1024UL, small2.OffsetBytes);

        // Large allocation (512 KiB) goes to Tier 2 pool
        StagingAllocationLease large1 = pool.Allocate(512UL * 1024UL, submissionTimelineTick: 2UL);
        Assert.Equal(StagingBufferTier.Tier2FencedLargePool, large1.Tier);
        Assert.Equal(1, pool.Tier2PoolCount);

        // Advance GPU tick to 2 so large1 is no longer busy, then reuse it
        pool.TickFrame(completedGpuTimelineTick: 2UL);
        StagingAllocationLease largeReused = pool.Allocate(512UL * 1024UL, submissionTimelineTick: 3UL);
        Assert.Equal(large1.BufferId, largeReused.BufferId);
        Assert.Equal(1, pool.Tier2ReusedAllocationsTotal);
        pool.ReleaseLease(in largeReused);

        // Advance 4 idle frame epochs past tick 3 -> Tier 3 GC must evict the idle Tier 2 buffer
        for (int f = 0; f < 4; f++)
        {
            pool.TickFrame(completedGpuTimelineTick: 10UL);
        }
        Assert.Equal(0, pool.Tier2PoolCount);
        Assert.Equal(1, pool.EvictedBuffersTotal);
    }

    [Fact]
    public void BidirectionalPageMemoryTracker_ScansDirty4KbPagesViaTrailingZeroCount_And_CoalescesSpans()
    {
        // #206: 1 MiB region = 256 pages of 4KB across 4 L1 64-bit words
        BidirectionalPageMemoryTracker tracker = new BidirectionalPageMemoryTracker(1024UL * 1024UL);
        Assert.Equal(256, tracker.TotalPages);
        Assert.Equal(4, tracker.L1WordCount);

        // Mark pages 2..3 (8 KiB at offset 8192) as GPU modified, and page 70 as CPU modified
        tracker.MarkRegionGpuModified(8192UL, 8192UL);
        tracker.MarkRegionCpuModified(70UL * 4096UL, 4096UL);

        Assert.True(tracker.IsRegionGpuModified(8192UL, 4096UL));
        Assert.False(tracker.IsRegionGpuModified(0UL, 4096UL));
        Assert.False(tracker.IsRegionCpuModified(8192UL, 4096UL));
        Assert.True(tracker.IsRegionCpuModified(70UL * 4096UL, 4096UL));

        List<DirtyPageSpan> gpuSpans = tracker.CollectDirtyGpuSpansAndClear(clearAfterCollect: true);
        Assert.Single(gpuSpans);
        Assert.Equal(2, gpuSpans[0].StartPageIndex);
        Assert.Equal(2, gpuSpans[0].PageCount);
        Assert.Equal(8192UL, gpuSpans[0].ByteOffset);
        Assert.Equal(8192UL, gpuSpans[0].ByteLength);

        // After clearAfterCollect, GPU modified bitmask must be clean while CPU bitmask remains intact
        Assert.False(tracker.IsRegionGpuModified(8192UL, 8192UL));
        Assert.True(tracker.IsRegionCpuModified(70UL * 4096UL, 4096UL));

        // Verify VulkanMemoryMappedReceiver integration (#206): skips unmodified menu frames & copies only dirty 4KB spans
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            VulkanMemoryMappedReceiver receiver = new VulkanMemoryMappedReceiver(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<VulkanMemoryMappedReceiver>.Instance,
                processId: 99999,
                width: 64,
                height: 64,
                slotCount: 1,
                skipUnmodifiedMenuFrames: true);
            byte[] srcFrame = new byte[64 * 64 * 4]; // 16 KiB (4 pages)
            byte[] dstFrame = new byte[64 * 64 * 4];
            srcFrame[4096] = 0x7F;

            // Clear initial dirty pages -> unmodified menu frame must be skipped
            receiver.PageTracker.CollectDirtyGpuSpansAndClear(clearAfterCollect: true);
            Assert.False(receiver.TryCopyModifiedFrameRegions(srcFrame, dstFrame, out int skippedBytes));
            Assert.Equal(0, skippedBytes);
            Assert.Equal(1L, receiver.SkippedUnmodifiedFramesCount);

            // Dirty only page 1 (offset 4096..8192) -> only 4096 bytes copied
            receiver.PageTracker.MarkRegionGpuModified(4096UL, 4096UL);
            Assert.True(receiver.TryCopyModifiedFrameRegions(srcFrame, dstFrame, out int partialBytes));
            Assert.Equal(4096, partialBytes);
            Assert.Equal(0x7F, dstFrame[4096]);
        }
    }

    [Fact]
    public async Task GpuFaultBufferComputeRing_IsolatesFaulting4KbPages_And_Cycles8DownloadSlots()
    {
        // #207: Register two GPU buffers and scan a 64-bit fault bitmask word
        GpuFaultBufferComputeRing ring = new GpuFaultBufferComputeRing(baseVirtualAddress: 0x1000_0000UL);
        ring.RegisterBufferRegion(bufferId: 10UL, debugLabel: "TerrainVertexBuffer", baseVirtualAddress: 0x1000_0000UL, sizeBytes: 16UL * 4096UL);
        ring.RegisterBufferRegion(bufferId: 20UL, debugLabel: "HeroSkelUniformBuffer", baseVirtualAddress: 0x1001_0000UL, sizeBytes: 8UL * 4096UL);

        // Set fault bits for page 3 (inside TerrainVertexBuffer) and page 18 (inside HeroSkelUniformBuffer)
        ulong faultWord0 = (1UL << 3) | (1UL << 18);
        List<GpuPageFaultIsolationRecord> faults = ring.ScanFaultBitmaskWords([faultWord0]);

        Assert.Equal(2, faults.Count);
        Assert.Equal(3, faults[0].PageIndex);
        Assert.Equal(10UL, faults[0].MatchedBufferId);
        Assert.Equal("TerrainVertexBuffer", faults[0].MatchedBufferLabel);
        Assert.Equal(3UL * 4096UL, faults[0].OffsetInBufferBytes);

        Assert.Equal(18, faults[1].PageIndex);
        Assert.Equal(20UL, faults[1].MatchedBufferId);
        Assert.Equal("HeroSkelUniformBuffer", faults[1].MatchedBufferLabel);
        Assert.Equal(2UL * 4096UL, faults[1].OffsetInBufferBytes);

        // Acquire all 8 download_buffer ring slots at tick 5
        for (int i = 0; i < GpuFaultBufferComputeRing.DownloadRingSlotCount; i++)
        {
            Assert.True(ring.TryAcquireDownloadRingSlot(completedGpuTimelineTick: 0UL, submissionTimelineTick: 5UL, byteSize: 4096UL, out DownloadRingSlotLease lease));
            Assert.Equal(i, lease.SlotIndex);
        }

        // 9th acquire before GPU reaches tick 5 must fail (all 8 slots busy)
        Assert.False(ring.TryAcquireDownloadRingSlot(completedGpuTimelineTick: 4UL, submissionTimelineTick: 6UL, byteSize: 4096UL, out DownloadRingSlotLease _));

        // Once GPU reaches tick 5, slot 0 becomes reusable
        Assert.True(ring.TryAcquireDownloadRingSlot(completedGpuTimelineTick: 5UL, submissionTimelineTick: 6UL, byteSize: 4096UL, out DownloadRingSlotLease reused));
        Assert.Equal(0, reused.SlotIndex);

        // Verify VulkanManageHandler 'gpu_fault_scan' MCP action (#207)
        VulkanManageHandler handler = new VulkanManageHandler(logger: _ => { });
        using JsonDocument faultDoc = JsonDocument.Parse("""
        {
          "action": "gpu_fault_scan",
          "slotIndex": 2,
          "registerBuffer": {
            "bufferId": 55,
            "baseVa": 268435456,
            "sizeBytes": 16384,
            "debugName": "ParticleComputeSSBO"
          },
          "faultAddresses": [268439552, 268500992]
        }
        """);
        ToolCallResult mcpFaultRes = await handler.HandleAsync(faultDoc.RootElement, CancellationToken.None);
        Assert.False(mcpFaultRes.IsError);
    }

    [Fact]
    public void HotPageTextureCache64_IsExactly64Bytes_HitsOnSame8KbPage_And_InvalidatesCleanly()
    {
        // #208: 64-byte L1 cache-line size & 8KB page mask (~0x1FFFUL)
        Assert.Equal(64, Marshal.SizeOf<HotPageTextureCache64>());

        HotPageTextureCache64 cache = default;
        cache.Invalidate();
        cache.InvalidationCount = 0UL;

        // First access on 8KB page 0x2000_4000 misses and populates cache
        bool hit1 = cache.TryLookupOrPopulate(0x2000_4120UL, fallbackSlot: 7U, fallbackTexturePtr: 0x7000_4000UL, out uint slot1, out ulong ptr1);
        Assert.False(hit1);
        Assert.Equal(7U, slot1);
        Assert.Equal(0x7000_4000UL, ptr1);
        Assert.Equal(0UL, cache.HitCount);
        Assert.Equal(1UL, cache.MissCount);

        // Second access within the same 8KB page (0x2000_5FF0 & ~0x1FFF == 0x2000_4000) hits L1 cache
        bool hit2 = cache.TryLookupOrPopulate(0x2000_5FF0UL, fallbackSlot: 99U, fallbackTexturePtr: 0UL, out uint slot2, out ulong ptr2);
        Assert.True(hit2);
        Assert.Equal(7U, slot2);
        Assert.Equal(0x7000_4000UL, ptr2);
        Assert.Equal(1UL, cache.HitCount);
        Assert.Equal(1UL, cache.MissCount);

        // Invalidate the 8KB page -> next access must miss
        cache.Invalidate();
        Assert.Equal(1UL, cache.InvalidationCount);
        bool hit3 = cache.TryLookupOrPopulate(0x2000_4120UL, fallbackSlot: 7U, fallbackTexturePtr: 0x7000_4000UL, out uint _, out ulong _);
        Assert.False(hit3);
        Assert.Equal(2UL, cache.MissCount);

        // Verify C++ alignas(64) HotPageTextureCache header contract (#208)
        string repoRoot = FindRepoRoot();
        string healerHeader = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "d3d9_texture_healer.h"));
        Assert.Contains("alignas(64) HotPageTextureCache", healerHeader);
        Assert.Contains("static_assert(alignof(HotPageTextureCache) == 64", healerHeader);
    }
}
