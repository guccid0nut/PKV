// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Diagnostics;
using Kongor.Core.Memory;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Phase 13 (#155-#165) verification suite for WerWolv/ImHex + libwolv + PatternLanguage
/// deep-pass ecosystem gems, including negative-control tests for the 3 upstream ImHex bugs fixed.
/// </summary>
public sealed class ImHexBoostEcosystemTests
{
    [Fact]
    public void Item155_FlatAugmentedIntervalTree_QueriesOverlaps_And_JumpsAcrossMemFreeHoles()
    {
        // protects: O(log N + K) flat-vector interval overlap queries and MEM_FREE hole skipping (#155)
        // fails_when: subtree MaxEnd augmentation is stale or explicit-stack pruning skips overlapping left/right children
        // seam: FlatAugmentedIntervalTree<string>, ProcessMemoryMapInspector.BuildRegionIntervalTree, and VirtualByteOverlay.QueryOverlappingPatches
        FlatAugmentedIntervalTree<string> tree = new FlatAugmentedIntervalTree<string>();
        tree.Insert(0x1000UL, 0x1FFFUL, "TextSection");
        tree.Insert(0x1500UL, 0x2500UL, "OverlappingDetour");
        tree.Insert(0x4000UL, 0x4FFFUL, "RdataSection");
        tree.Insert(0x8000UL, 0x8FFFUL, "DataSection");

        List<IntervalEntry<string>> hits = tree.QueryOverlapping(0x1800UL, 0x2100UL);
        Assert.Equal(2, hits.Count);
        Assert.Equal("TextSection", hits[0].Value);
        Assert.Equal("OverlappingDetour", hits[1].Value);

        // Next/Prev interval across unmapped hole [0x2501, 0x3FFF]
        IntervalEntry<string>? nextAfterHole = tree.FindNextInterval(0x2500UL);
        Assert.NotNull(nextAfterHole);
        Assert.Equal("RdataSection", nextAfterHole.Value.Value);

        IntervalEntry<string>? prevBeforeHole = tree.FindPrevInterval(0x4000UL);
        Assert.NotNull(prevBeforeHole);
        Assert.Equal("OverlappingDetour", prevBeforeHole.Value.Value);

        // VirtualByteOverlay integration
        byte[] baseBuf = new byte[64];
        VirtualByteOverlay overlay = new VirtualByteOverlay(baseBuf);
        overlay.ApplyOverlay(10, new byte[] { 0xAA, 0xBB, 0xCC });
        overlay.ApplyOverlay(40, new byte[] { 0xDD, 0xEE });

        List<IntervalEntry<OverlayPatchRecord>> patchOverlaps = overlay.QueryOverlappingPatches(8UL, 15UL);
        Assert.Single(patchOverlaps);
        Assert.Equal(10L, patchOverlaps[0].Value.Offset);
    }

    [Fact]
    public async Task Item156_DisassemblyCfgLaneAnalyzer_AllocatesLanes_And_DetectsDetourReturnAndInteriorJumpHazards()
    {
        // protects: O(1) ReturnPrefix RET-boundary check, 8-lane sweep-line CFG allocation, and detour interior-jump safety (#156)
        // fails_when: a 5-byte or 14-byte detour overwrites a branch target inside the prologue or crosses a RET instruction undetected
        // seam: DisassemblyCfgLaneAnalyzer.Analyze + AuditDetourOverwriteSafety + NativeManageHandler (audit_detour_cfg)
        List<DisassembledInstructionRow> rows = new List<DisassembledInstructionRow>
        {
            new DisassembledInstructionRow(0x1000UL, 2, "test", "rcx, rcx"),
            new DisassembledInstructionRow(0x1002UL, 2, "je", "0x100A"),
            new DisassembledInstructionRow(0x1004UL, 6, "mov", "dword ptr [rcx], 1"),
            new DisassembledInstructionRow(0x100AUL, 2, "xor", "eax, eax"),
            new DisassembledInstructionRow(0x100CUL, 1, "ret", string.Empty),
            new DisassembledInstructionRow(0x100DUL, 2, "jmp", "0x1002") // jumps into byte +2 of [0x1000, 0x1005)!
        };

        DisassemblyCfgAnalysisResult cfg = DisassemblyCfgLaneAnalyzer.Analyze(rows);
        Assert.Equal(2, cfg.Edges.Count);
        Assert.True(cfg.Edges[0].AssignedLane >= 0);
        Assert.True(cfg.CrossesReturnBoundary(0, 4));
        Assert.False(cfg.CrossesReturnBoundary(0, 3));

        // Layout Y math round-trip via ReturnPrefix
        float row5Y = cfg.ComputeRowTopY(5, 20.0f, 8.0f);
        Assert.Equal((5 * 20.0f) + (1 * 8.0f), row5Y);
        Assert.Equal(5, cfg.FindRowIndexAtY(row5Y, 20.0f, 8.0f));

        // 5-byte detour at 0x1000 overwrites [0x1000, 0x1005), which splits instruction at 0x1004 AND has an interior jump target at 0x1002!
        DetourOverwriteSafetyReport unsafeReport = cfg.AuditDetourOverwriteSafety(0x1000UL, 5);
        Assert.False(unsafeReport.IsSafe);
        Assert.Single(unsafeReport.InteriorJumpHazards);
        Assert.Equal(0x1002UL, unsafeReport.InteriorJumpHazards[0].TargetAddress);

        // Verify MCP handler dispatch
        NativeManageHandler handler = new NativeManageHandler(_ => { });
        using JsonDocument doc = JsonDocument.Parse("""{"action":"audit_detour_cfg","detour_start":4096,"detour_length":5}""");
        ToolCallResult mcpRes = await handler.HandleAsync(doc.RootElement);
        Assert.False(mcpRes.IsError);
    }

    [Fact]
    public void Item157_SlotLruCachedProvider_And_VirtualConcatenatedProvider_FixUnsignedUnderflow_And_EvictLru()
    {
        // protects: 64 KiB slot LRU paging, upstream provider.cpp:143 unsigned subtraction underflow fix, and zero-copy VirtualConcatenatedProvider (#157)
        // fails_when: offset < baseAddress or size > actualSize wraps unsigned arithmetic, or cross-slot/cross-segment reads corrupt bytes
        // seam: SlotLruCachedProvider, VirtualConcatenatedProvider, and ShiftBytesInPlace
        Assert.False(SlotLruCachedProvider.ValidateBoundsSafe(offset: 0x0500L, size: 16, baseAddress: 0x1000L, actualSize: 0x2000L));
        Assert.False(SlotLruCachedProvider.ValidateBoundsSafe(offset: 0x1000L, size: 0x3000, baseAddress: 0x1000L, actualSize: 0x2000L));
        Assert.True(SlotLruCachedProvider.ValidateBoundsSafe(offset: 0x1000L, size: 0x2000, baseAddress: 0x1000L, actualSize: 0x2000L));

        byte[] rawStorage = new byte[256];
        for (int i = 0; i < rawStorage.Length; i++)
        {
            rawStorage[i] = (byte)i;
        }

        using MemoryStream ms = new MemoryStream(rawStorage, writable: true);
        using SlotLruCachedProvider cached = new SlotLruCachedProvider(ms, baseAddress: 0x1000L, slotSize: 64, maxCachedSlots: 2);

        Span<byte> readBuf = stackalloc byte[80]; // spans across slot 0 and slot 1
        Assert.True(cached.ReadRaw(0x1000L, readBuf));
        Assert.Equal(0, readBuf[0]);
        Assert.Equal(79, readBuf[79]);
        Assert.Equal(2, cached.CachedSlotCount);

        // Touch slot 2 -> evicts least-recently-used slot 0
        Span<byte> slot2Buf = stackalloc byte[16];
        Assert.True(cached.ReadRaw(0x1080L, slot2Buf));
        Assert.Equal(128, slot2Buf[0]);
        Assert.Equal(2, cached.CachedSlotCount);

        // VirtualConcatenatedProvider cross-boundary read
        VirtualConcatenatedProvider concat = new VirtualConcatenatedProvider(new ReadOnlyMemory<byte>[]
        {
            new byte[] { 10, 20, 30 },
            new byte[] { 40, 50 },
            new byte[] { 60, 70, 80 }
        });
        Assert.Equal(8L, concat.TotalLength);
        Span<byte> joined = stackalloc byte[6];
        Assert.True(concat.ReadRaw(1L, joined));
        Assert.Equal(new byte[] { 20, 30, 40, 50, 60, 70 }, joined.ToArray());

        // ShiftBytesInPlace 4 KiB sliding window insert & remove
        using MemoryStream shiftStream = new MemoryStream();
        shiftStream.Write(new byte[] { 1, 2, 3, 4, 5, 6 });
        SlotLruCachedProvider.ShiftBytesInPlace(shiftStream, offset: 2L, byteDelta: 3L);
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 3, 4, 5, 6 }, shiftStream.ToArray());
        SlotLruCachedProvider.ShiftBytesInPlace(shiftStream, offset: 2L, byteDelta: -3L);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, shiftStream.ToArray());
    }

    [Fact]
    public void Item158_FloatToCustomBits_CarriesMantissaRoundingIntoExponent_And_DecodesOddWidthsDosDateAndGuid()
    {
        // protects: FloatToCustomBits round-to-nearest-even carry overflow fix (ieee_decoder.cpp:427), odd-width u24/i24/u48/i48, DOS DateTime, and RFC-4122 GUID (#158)
        // fails_when: rounding 1.9999999 in FP16 wraps mantissa to 0 without incrementing biased exponent to 2.0 (0x4000)
        // seam: BinaryCustomFloatAndLeb128Codec
        // 1.9999998807907104 in FP16 (E=5, M=10) must round up to exactly 2.0 (bits = 0x4000, not 0x3C00!)
        ulong fp16RoundedToTwo = BinaryCustomFloatAndLeb128Codec.FloatToCustomBits(1.9999998807907104, exponentBits: 5, mantissaBits: 10);
        Assert.Equal(0x4000UL, fp16RoundedToTwo);
        float decodedBack = BinaryCustomFloatAndLeb128Codec.CustomFloatToFloat32(fp16RoundedToTwo, 5, 10);
        Assert.Equal(2.0f, decodedBack);

        // Signaling NaN vs Quiet NaN preservation
        ulong sNanDoubleBits = 0x7FF0_0000_0000_0001UL; // signaling NaN (bit 51 == 0, payload != 0)
        double sNanDouble = BitConverter.UInt64BitsToDouble(sNanDoubleBits);
        ulong fp16SNan = BinaryCustomFloatAndLeb128Codec.FloatToCustomBits(sNanDouble, 5, 10);
        Assert.Equal(0UL, (fp16SNan >> 9) & 1UL); // quiet bit (bit 9) must remain 0
        Assert.NotEqual(0UL, fp16SNan & 0x3FFUL); // mantissa must be non-zero

        // u24 / i24 / u48 / i48
        ReadOnlySpan<byte> neg3Bytes = stackalloc byte[] { 0xFE, 0xFF, 0xFF }; // -2 in 24-bit two's complement
        Assert.Equal(0xFFFFFEU, BinaryCustomFloatAndLeb128Codec.ReadUInt24LittleEndian(neg3Bytes));
        Assert.Equal(-2, BinaryCustomFloatAndLeb128Codec.ReadInt24LittleEndian(neg3Bytes));

        ReadOnlySpan<byte> neg6Bytes = stackalloc byte[] { 0xFB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }; // -5 in 48-bit
        Assert.Equal(-5L, BinaryCustomFloatAndLeb128Codec.ReadInt48LittleEndian(neg6Bytes));

        // MS-DOS Date/Time (2026-10-18 13:30:10 UTC)
        ushort dosDate = (ushort)(((2026 - 1980) << 9) | (10 << 5) | 18);
        ushort dosTime = (ushort)((13 << 11) | (30 << 5) | (10 / 2));
        Assert.True(BinaryCustomFloatAndLeb128Codec.TryDecodeDosDateTime(dosDate, dosTime, out DateTime dt));
        Assert.Equal(new DateTime(2026, 10, 18, 13, 30, 10, DateTimeKind.Utc), dt);
        Assert.False(BinaryCustomFloatAndLeb128Codec.TryDecodeDosDateTime(0, 0, out _)); // month=0, day=0 invalid

        // RFC-4122 GUID v4 check
        Guid v4 = Guid.Parse("d75430fb-6569-4935-9b67-ba957944c1cb");
        Assert.True(BinaryCustomFloatAndLeb128Codec.IsValidRfc4122Guid(v4.ToByteArray(), out int guidVer, out byte variant));
        Assert.Equal(4, guidVer);
        Assert.Equal(0b10, variant);
    }

    [Fact]
    public void Item159_UnalignedBitfieldStreamCodec_ReadsAndWritesAcrossByteBoundariesWithoutClobberingNeighbors()
    {
        // protects: Unaligned bit-stream reading and non-destructive 128-bit RMW bitfield writing (#159)
        // fails_when: WriteBits overwrites neighboring bits outside [bitOffset, bitOffset + bitCount)
        // seam: UnalignedBitfieldStreamCodec + BitfieldCursor
        byte[] packet = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

        // Write an 11-bit value (0x2A5 = 0b010_1010_0101) starting at byte 0, bit 5 (spans across byte 0, byte 1, and byte 2)
        UnalignedBitfieldStreamCodec.WriteBits(packet, byteOffset: 0, bitOffset: 5, bitCount: 11, value: 0x2A5UL);

        // Surrounding bits [0..4] in byte 0 (5 bits = 0x1F) and [16..31] in bytes 2..3 (0xFFFF) must remain untouched (all 1s)!
        Assert.Equal(0x1FUL, UnalignedBitfieldStreamCodec.ReadBits(packet, 0, 0, 5));
        Assert.Equal(0x2A5UL, UnalignedBitfieldStreamCodec.ReadBits(packet, 0, 5, 11));
        Assert.Equal(0xFFFFUL, UnalignedBitfieldStreamCodec.ReadBits(packet, 2, 0, 16));

        // Verify BitfieldCursor sequential unpacking
        BitfieldCursor cursor = new BitfieldCursor(0, 0);
        (int b0, int bit0) = cursor.GetAndAdvance(5);
        (int b1, int bit1) = cursor.GetAndAdvance(11);
        Assert.Equal(0x1FUL, UnalignedBitfieldStreamCodec.ReadBits(packet, b0, bit0, 5));
        Assert.Equal(0x2A5UL, UnalignedBitfieldStreamCodec.ReadBits(packet, b1, bit1, 11));
    }

    [Fact]
    public void Item160_CustomTableEncodingAndPrefixAuditor_DecodesLongestMatch_And_DetectsAmbiguousPrefixes()
    {
        // protects: Longest-match reverse-length bucketed .tbl decoding, path sandbox, and O(N log N) prefix ambiguity detection (#160)
        // fails_when: shorter 1-byte table entry shadows a 2-byte sequence or prefix conflict goes undetected
        // seam: CustomTableEncodingAndPrefixAuditor + AssetInspectService.AuditCustomStringTable
        string tblContent = """
            # K2 Color & Token Table
            5E=[CARET]
            5E72=[RED]
            5E77=[WHITE]
            41=A
            42=B
            00=<END>
            """;

        CustomTableEncodingAndPrefixAuditor auditor = AssetInspectService.AuditCustomStringTable(
            tblContent,
            out PrefixAmbiguityConflict? conflict);

        // 0x5E is a strict prefix of 0x5E72!
        Assert.NotNull(conflict);
        Assert.Equal(new byte[] { 0x5E }, conflict.PrefixBytes);

        // Despite prefix overlap, longest-match reverse-length bucketed DecodeBounded matches 0x5E72 -> "[RED]" first!
        byte[] encoded = new byte[] { 0x5E, 0x72, 0x41, 0x42, 0x5E, 0x00, 0x41 };
        DecodeTableResult decoded = auditor.DecodeBounded(
            encoded,
            new DecodeTableOptions(StopToken: "<END>"));
        Assert.Equal(DecodeStopReason.StopTokenReached, decoded.StopReason);
        Assert.Equal("[RED]AB[CARET]", decoded.DecodedText);
        Assert.Equal(6, decoded.BytesConsumed);

        // Path traversal guard blocks "../" escape
        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_tbl_sandbox");
        Assert.True(CustomTableEncodingAndPrefixAuditor.IsWithinAllowedDirectory(Path.Combine(tempRoot, "sub", "k2.tbl"), tempRoot));
        Assert.False(CustomTableEncodingAndPrefixAuditor.IsWithinAllowedDirectory(Path.Combine(tempRoot, "..", "evil.tbl"), tempRoot));
    }

    [Fact]
    public void Item161_ParameterizedRocksoftCrcEngine_MatchesStandardCheckVectors_Across8_16_32_64BitWidths()
    {
        // protects: Parameterized Rocksoft CRC-8/16/32/64 engine and NativeZlibBridge managed CRC fallbacks (#161)
        // fails_when: bit reflection or polynomial table shifts produce wrong Rocksoft check values for "123456789"
        // seam: ParameterizedRocksoftCrcEngine + NativeZlibBridge
        ReadOnlySpan<byte> checkAscii = "123456789"u8;

        Assert.Equal((byte)0xF4, ParameterizedRocksoftCrcEngine.Crc8Standard.ComputeByte(checkAscii));
        Assert.Equal((ushort)0x29B1, ParameterizedRocksoftCrcEngine.Crc16CcittFalse.ComputeUInt16(checkAscii));
        Assert.Equal((ushort)0xBB3D, ParameterizedRocksoftCrcEngine.Crc16Arc.ComputeUInt16(checkAscii));
        Assert.Equal(0xCBF43926U, ParameterizedRocksoftCrcEngine.Crc32Ieee.ComputeUInt32(checkAscii));
        Assert.Equal(0xE3069283U, ParameterizedRocksoftCrcEngine.Crc32Castagnoli.ComputeUInt32(checkAscii));
        Assert.Equal(0x995DC9BBDF1939FAUL, ParameterizedRocksoftCrcEngine.Crc64Xz.Compute(checkAscii));

        Assert.Equal(0xCBF43926U, NativeZlibBridge.ComputeManagedCrc32Ieee(checkAscii));
        Assert.Equal(0xE3069283U, NativeZlibBridge.ComputeManagedCrc32Castagnoli(checkAscii));
        Assert.Equal((ushort)0x29B1, NativeZlibBridge.ComputeManagedCrc16Ccitt(checkAscii));
    }

    [Fact]
    public void Item162_AobScanner_ScanStreamChunked_FindsBoundaryStraddlingPatterns_And_Demangles3PrefixSymbols()
    {
        // protects: Chunked sliding-window stream AOB scanning across chunk boundaries with 64-bit offsets and 3-prefix symbol demangling (#162)
        // fails_when: a pattern straddling the boundary between two stream chunks is missed, or stripped Itanium/MSVC symbols fail to demangle
        // seam: AobScanner.ScanStreamChunked + SymbolCorrelatorService.NormalizeAndDemangleSymbol
        byte[] streamData = new byte[200];
        // Place a 6-byte pattern "48 8B ?5 11 22 33" right across the 64-byte chunk boundary at offset 61..66!
        streamData[61] = 0x48;
        streamData[62] = 0x8B;
        streamData[63] = 0x05;
        streamData[64] = 0x11;
        streamData[65] = 0x22;
        streamData[66] = 0x33;

        // And a second match at offset 150
        streamData[150] = 0x48;
        streamData[151] = 0x8B;
        streamData[152] = 0xF5;
        streamData[153] = 0x11;
        streamData[154] = 0x22;
        streamData[155] = 0x33;

        using MemoryStream ms = new MemoryStream(streamData);
        AobPattern pattern = AobPattern.Parse("48 8B ?5 11 22 33");
        AobStreamScanResult scanRes = AobScanner.ScanStreamChunked(ms, pattern, chunkSizeBytes: 64);

        Assert.True(scanRes.Found);
        Assert.Equal(61L, scanRes.FirstOffset);
        Assert.Equal(new long[] { 61L, 150L }, scanRes.AllOffsets);

        // 3-prefix symbol demangler (raw, "_" + raw, "_Z" + raw)
        Assert.Equal("CHost::Execute", SymbolCorrelatorService.NormalizeAndDemangleSymbol("?Execute@CHost@@QEAAXXZ"));
        Assert.Equal("K2::CHost::Execute", SymbolCorrelatorService.NormalizeAndDemangleSymbol("_ZN2K25CHost7ExecuteEv"));
        Assert.Equal("K2::CHost::Execute", SymbolCorrelatorService.NormalizeAndDemangleSymbol("N2K25CHost7ExecuteEv")); // stripped "_Z" prefix!
    }

    [Fact]
    public void Item163_DeferredOnceDispatcher_CoalescesCallSiteDuplicates_WhilePreservingExplicitLoopKeys()
    {
        // protects: Call-site vs. explicit-key DoLaterOnce coalescing and Win32 SetThreadDescription (#163)
        // fails_when: calling DoLaterOnce inside a foreach loop collapses distinct entities into a single call-site line key
        // seam: DeferredOnceDispatcher
        DeferredOnceDispatcher dispatcher = new DeferredOnceDispatcher();
        int callSiteCounter = 0;

        // 1. Call-site deduplication: invoking the same helper 5 times coalesces into 1 execution
        void EnqueueFromSameCallSite()
        {
            dispatcher.DoLaterOnce(() => callSiteCounter++);
        }

        for (int i = 0; i < 5; i++)
        {
            EnqueueFromSameCallSite();
        }

        Assert.Equal(1, dispatcher.PendingCount);
        Assert.Equal(4L, dispatcher.CoalescedDuplicatesCount);
        Assert.Equal(1, dispatcher.RunDeferredCalls());
        Assert.Equal(1, callSiteCounter);

        // 2. Explicit key overload inside a foreach loop: distinct entity keys do NOT collapse!
        List<string> reloadedEntities = new List<string>();
        string[] entities = new string[] { "Hero_Devourer", "Hero_Pebbles", "Hero_Devourer", "Hero_Scout" };
        foreach (string entity in entities)
        {
            string captured = entity;
            dispatcher.DoLaterOnce(captured, () => reloadedEntities.Add(captured));
        }

        Assert.Equal(3, dispatcher.PendingCount);
        Assert.Equal(3, dispatcher.RunDeferredCalls());
        Assert.Equal(new string[] { "Hero_Devourer", "Hero_Pebbles", "Hero_Scout" }, reloadedEntities);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(DeferredOnceDispatcher.TrySetCurrentThreadDescription("Kongor_TestRunner_163"));
        }
    }

    [Fact]
    public async Task Item164_BinaryTransformPullDag_PermitsDiamondSharedAncestors_And_RejectsTrueDirectedCycles()
    {
        // protects: Active-DFS cycle guard (m_processedInputs) vs. diamond DAG shared ancestor evaluation (#164)
        // fails_when: diamond DAGs throw a false-positive cycle exception or true directed cycles cause infinite recursion
        // seam: BinaryTransformPullDag + VirtualByteOverlay + NativeManageHandler (transform_dag)
        BinaryTransformPullDag dag = new BinaryTransformPullDag();
        int src = dag.AddSourceNode("Source", new byte[] { 0x10, 0x20, 0x30, 0x40 });
        int branchA = dag.AddXorNode("BranchA", src, new byte[] { 0xFF });
        int branchB = dag.AddXorNode("BranchB", src, new byte[] { 0xFF });

        // Diamond join: consumes both BranchA and BranchB (which share ancestor `src`)
        int joinNode = dag.AddCustomNode("DiamondJoin", new int[] { branchA, branchB }, inputs =>
        {
            byte[] a = inputs[0];
            byte[] b = inputs[1];
            byte[] outBuf = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                outBuf[i] = (byte)(a[i] ^ b[i]); // (src ^ 0xFF) ^ (src ^ 0xFF) == 0x00
            }

            return outBuf;
        });

        byte[] diamondOutput = dag.EvaluateNode(joinNode, out int uniqueEvaluated);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, diamondOutput);
        Assert.Equal(4, uniqueEvaluated); // Source evaluated once via memoization!

        // Stage branchA into a VirtualByteOverlay
        byte[] baseRom = new byte[] { 0, 0, 0, 0, 0, 0 };
        VirtualByteOverlay overlay = dag.EvaluateToVirtualOverlay(branchA, baseRom, patchOffset: 1L);
        Assert.Equal(new byte[] { 0x00, 0xEF, 0xDF, 0xCF, 0xBF, 0x00 }, overlay.ToEffectiveArray());

        // Introduce a true directed cycle: Source depends on JoinNode!
        dag.AddInputEdge(src, joinNode);
        Assert.Throws<InvalidOperationException>(() => dag.EvaluateNode(joinNode));

        // Verify MCP handler dispatch
        NativeManageHandler handler = new NativeManageHandler(_ => { });
        using JsonDocument doc = JsonDocument.Parse("""{"action":"transform_dag","input_hex":"4B4F4E474F52","xor_key_hex":"5A"}""");
        ToolCallResult mcpRes = await handler.HandleAsync(doc.RootElement);
        Assert.False(mcpRes.IsError);
    }

    [Fact]
    public void Item165_CaretDiagnosticWindowFormatter_WindowsUltraLongLinesAtWordBoundaries_And_GuardsBufferRefCounts()
    {
        // protects: 120-column word-boundary windowing with ^~~~~ caret alignment on minified lines, plus ReferenceCountedBufferGuard (#165)
        // fails_when: ultra-long lines are not windowed or left/right "..." truncation misaligns the ^~~~~ caret column under the token
        // seam: CaretDiagnosticWindowFormatter + AssetInspectService.FormatSyntaxDiagnosticCaret + ReferenceCountedBufferGuard
        StringBuilder longLineBuilder = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            longLineBuilder.Append($"<panel name=\"pad_{i:D2}\" width=\"100%\" height=\"100%\" /> ");
        }

        int tokenColOneBased = longLineBuilder.Length + 15; // points right at `broken_attr="123"`
        longLineBuilder.Append("<button name=\"broken_attr\" onclicklua=\"self:DoThing()\" /> ");
        for (int i = 40; i < 80; i++)
        {
            longLineBuilder.Append($"<panel name=\"pad_{i:D2}\" width=\"100%\" height=\"100%\" /> ");
        }

        string minifiedXml = longLineBuilder.ToString();
        FormattedCaretDiagnostic diag = AssetInspectService.FormatSyntaxDiagnosticCaret(
            minifiedXml,
            lineOneBased: 1,
            columnOneBased: tokenColOneBased,
            tokenLength: 11, // "broken_attr"
            message: "Invalid button identifier 'broken_attr'",
            maxLineLength: 120);

        Assert.True(diag.TruncatedLeft);
        Assert.True(diag.TruncatedRight);
        Assert.StartsWith("... ", diag.WindowedLineText, StringComparison.Ordinal);
        Assert.EndsWith(" ...", diag.WindowedLineText, StringComparison.Ordinal);

        // Verify that the '^' in CaretLineText aligns with the exact column of "broken_attr" inside WindowedLineText!
        Assert.Equal("broken_attr", diag.WindowedLineText.Substring(diag.CaretOffsetInWindow, 11));
        Assert.Equal('^', diag.CaretLineText[diag.CaretOffsetInWindow]);
        Assert.Contains("^~~~~~~~~~~", diag.CaretLineText, StringComparison.Ordinal);

        // Verify ReferenceCountedBufferGuard catches double-free and recursion limit
        ReferenceCountedBufferGuard guard = new ReferenceCountedBufferGuard(maxPatternLimit: 4, maxRecursionDepth: 2);
        int bufId = guard.AllocateBuffer();
        guard.AddReference(bufId);
        Assert.Equal(1, guard.ReleaseBuffer(bufId));
        Assert.Equal(0, guard.ReleaseBuffer(bufId));
        Assert.Throws<InvalidOperationException>(() => guard.ReleaseBuffer(bufId)); // double-free caught!

        guard.EnterPatternScope();
        guard.EnterPatternScope();
        Assert.Throws<InvalidOperationException>(() => guard.EnterPatternScope()); // depth > 2 caught!
    }
}
