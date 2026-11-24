// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ASPIRE.Common.K2;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Core.Diagnostics;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Phase 12 (#145-#154) unit &amp; mutant tests for ImHex Binary Forensics, Pattern Scanning &amp; Decompiler Math Gems.
/// </summary>
public sealed class ImHexWave3ForensicsTests
{
    [Fact]
    public void Item145_AobScanner_SupportsNibbleMasks_InlineStrings_TypedInts_AndAlignment()
    {
        // Construct a buffer with:
        // [0..3]: padding
        // [4..15]: 0x48, 0x8B, "SMDL" (4 bytes), u32le(0x12345678), u16be(0xCAFE)
        byte[] buffer = new byte[32];
        buffer[4] = 0x48;
        buffer[5] = 0x8B;
        Encoding.ASCII.GetBytes("SMDL", buffer.AsSpan(6, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), 0x12345678U);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(14, 2), 0xCAFE);

        // Also place a copy at unaligned offset 19 that should be skipped when alignment = 4
        Array.Copy(buffer, 4, buffer, 19, 12);

        AobPattern pattern = AobPattern.Parse("4? ?B \"SMDL\" u32le(0x12345678) u16be(0xCAFE)");
        Assert.Equal(12, pattern.Length);
        Assert.Equal(0xF0, pattern.BitMasks[0]);
        Assert.Equal(0x0F, pattern.BitMasks[1]);
        Assert.Equal(2, pattern.FirstFullyExactIndex); // 'S' at index 2 is first 0xFF mask

        IReadOnlyList<int> alignedMatches = AobScanner.FindAll(buffer, pattern, alignment: 4);
        Assert.Single(alignedMatches);
        Assert.Equal(4, alignedMatches[0]);

        IReadOnlyList<int> unalignedMatches = AobScanner.FindAll(buffer, pattern, alignment: 1);
        Assert.Equal(2, unalignedMatches.Count);
        Assert.Equal(4, unalignedMatches[0]);
        Assert.Equal(19, unalignedMatches[1]);
    }

    [Fact]
    public void Item146_IpsPatchCodec_And_VirtualByteOverlay_HandleEofCollision_And_LargeChunks()
    {
        byte[] baseImage = new byte[0x500000];
        Array.Fill(baseImage, (byte)0xAA);

        VirtualByteOverlay overlay = new VirtualByteOverlay(baseImage);
        // Stage an edit right at IPS "EOF" magic offset 0x454F46 (4,542,278)
        overlay.ApplyOverlay(IpsPatchCodec.IpsEofCollisionOffset, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        byte[] ips24 = overlay.ExportIpsPatch(useIps32: false);
        VirtualByteOverlay restoredOverlay = new VirtualByteOverlay(baseImage);
        IpsDecodeResult decoded24 = restoredOverlay.ApplyIpsPatch(ips24);
        Assert.True(decoded24.Success);
        Assert.False(decoded24.IsIps32);

        Span<byte> window = stackalloc byte[4];
        restoredOverlay.ReadEffectiveSpan(IpsPatchCodec.IpsEofCollisionOffset, window);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, window.ToArray());

        // Test IPS32 with > 64 KiB payload chunk splitting
        byte[] largePayload = new byte[70000];
        Array.Fill(largePayload, (byte)0x55);
        OverlayPatchRecord[] largeRecords = new OverlayPatchRecord[]
        {
            new OverlayPatchRecord(0x01020304L, largePayload)
        };

        byte[] ips32 = IpsPatchCodec.Encode(largeRecords, useIps32: true);
        IpsDecodeResult decoded32 = IpsPatchCodec.Decode(ips32);
        Assert.True(decoded32.Success);
        Assert.True(decoded32.IsIps32);
        Assert.True(decoded32.Records.Count >= 2);
        Assert.Equal(70000, decoded32.TotalPatchedBytes);
    }

    [Fact]
    public void Item147_BinaryEntropyAndDigramAnalyzer_ComputesShannonEntropy_And_DetectsAnomalies()
    {
        byte[] zeroes = new byte[1024];
        Assert.Equal(0.0, BinaryEntropyAndDigramAnalyzer.ComputeNormalizedShannonEntropy(zeroes), precision: 6);

        byte[] uniform = new byte[256 * 16];
        for (int i = 0; i < uniform.Length; i++)
        {
            uniform[i] = (byte)(i & 0xFF);
        }
        Assert.Equal(1.0, BinaryEntropyAndDigramAnalyzer.ComputeNormalizedShannonEntropy(uniform), precision: 6);

        // Mixed buffer: 1024 zero bytes + 1024 uniform high-entropy bytes
        byte[] mixed = new byte[2048];
        Array.Copy(uniform, 0, mixed, 1024, 1024);

        ByteDistributionProfile profile = BinaryEntropyAndDigramAnalyzer.AnalyzeEntropyAndDistribution(
            mixed,
            requestedBlockSize: 256,
            highEntropyThreshold: 0.90);
        Assert.Single(profile.HighEntropySpans);
        Assert.Equal(1024, profile.HighEntropySpans[0].StartByteOffset);
        Assert.Equal(2048, profile.HighEntropySpans[0].EndByteOffset);

        DigramMatrixResult digram = BinaryEntropyAndDigramAnalyzer.ComputeDigramMatrix(zeroes);
        Assert.Equal(1023, digram.TotalTransitions);
        Assert.Equal(1, digram.NonZeroPairCount);
        Assert.Equal(0, digram.PeakFirstByte);
        Assert.Equal(0, digram.PeakSecondByte);
    }

    [Fact]
    public void Item148_K2ModelMeshIntegrityAuditor_DetectsOutOfBoundsIndices_And_Computes3DBounds()
    {
        K2Model model = new K2Model
        {
            Version = 3,
            NumMeshes = 1,
            NumBones = 2
        };
        model.Bones.Add(new K2Bone { Index = 0, ParentIndex = -1, Name = "Root" });
        model.Bones.Add(new K2Bone { Index = 1, ParentIndex = 0, Name = "Spine" });

        K2Mesh mesh = new K2Mesh
        {
            Index = 0,
            Name = "body",
            NumVertices = 3,
            NumFaces = 2,
            Vertices = new Vector3[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(2f, 0f, 0f),
                new Vector3(0f, 2f, 50f) // Tall Z extent to verify 3D Z-axis bounding box fix
            },
            Normals = new Vector3[]
            {
                Vector3.Zero, // Invalid zero-length normal
                Vector3.UnitZ,
                Vector3.UnitZ
            },
            // Face 0 is valid (0, 1, 2); Face 1 has out-of-bounds vertex index 99
            Indices = new int[] { 0, 1, 2, 0, 1, 99 }
        };
        model.Meshes.Add(mesh);

        ModelIntegrityAuditResult audit = K2ModelMeshIntegrityAuditor.AuditModel(model);
        Assert.False(audit.IsRenderSafe);
        Assert.Equal(1, audit.TotalOutOfBoundsFaces);
        Assert.Equal(1, audit.TotalInvalidNormals);
        Assert.Equal(50f, audit.MeshReports[0].ComputedMaxExtent, precision: 3);

        Vector3[] synthesized = K2ModelMeshIntegrityAuditor.SynthesizeVertexNormals(mesh.Vertices, mesh.Indices);
        Assert.Equal(3, synthesized.Length);
        Assert.True(synthesized[0].Length() > 0.99f);
    }

    [Fact]
    public void Item149_BinaryCustomFloatAndLeb128Codec_DecodesCustomFloats_Bitfields_And_Leb128()
    {
        // BFloat16 (E=8, M=7) for 1.5f (IEEE 0x3FC00000 -> BF16 0x3FC0)
        float bf16 = BinaryCustomFloatAndLeb128Codec.CustomFloatToFloat32(0x3FC0UL, exponentBits: 8, mantissaBits: 7, hasSign: true);
        Assert.Equal(1.5f, bf16, precision: 4);

        // FP16 (E=5, M=10) for -2.0f (0xC000)
        float fp16 = BinaryCustomFloatAndLeb128Codec.CustomFloatToFloat32(0xC000UL, exponentBits: 5, mantissaBits: 10, hasSign: true);
        Assert.Equal(-2.0f, fp16, precision: 4);

        // Fixed-point Q16.16: 0x00028000 == 2.5
        Assert.Equal(2.5, BinaryCustomFloatAndLeb128Codec.DecodeFixedPoint(0x00028000L, 16), precision: 6);

        // Bitfield extract & sign-extend
        ulong extracted = BinaryCustomFloatAndLeb128Codec.ExtractBits(0xABCD_EF00UL, bitOffset: 8, bitWidth: 8);
        Assert.Equal(0xEFUL, extracted);
        Assert.Equal(-17L, BinaryCustomFloatAndLeb128Codec.SignExtend(0xEFUL, 8));

        // ULEB128 & SLEB128 round-trip
        Span<byte> buf = stackalloc byte[16];
        int uLen = BinaryCustomFloatAndLeb128Codec.EncodeUleb128(624485UL, buf);
        Assert.Equal(3, uLen);
        Assert.True(BinaryCustomFloatAndLeb128Codec.TryDecodeUleb128(buf.Slice(0, uLen), out ulong uDecoded, out int uConsumed));
        Assert.Equal(624485UL, uDecoded);
        Assert.Equal(3, uConsumed);

        int sLen = BinaryCustomFloatAndLeb128Codec.EncodeSleb128(-123456L, buf);
        Assert.True(BinaryCustomFloatAndLeb128Codec.TryDecodeSleb128(buf.Slice(0, sLen), out long sDecoded, out int sConsumed));
        Assert.Equal(-123456L, sDecoded);
        Assert.Equal(sLen, sConsumed);
    }

    [Fact]
    public void Item150_InvariantMultiplierDivisorDecoder_RecoversExactDivisorsFromCompilerMagicConstants()
    {
        ulong[] testDivisors = new ulong[] { 3, 5, 7, 10, 12, 60, 100, 1000 };
        foreach (ulong d in testDivisors)
        {
            FastDivSynthesisResult syn = InvariantMultiplierDivisorDecoder.SynthesizeUnsigned(d, bitWidth: 32);
            FastDivDecodeResult dec = InvariantMultiplierDivisorDecoder.DecodeUnsigned(
                syn.Multiplier,
                syn.PostShift,
                bitWidth: 32,
                variant: syn.Variant,
                preShift: syn.PreShift);

            Assert.True(dec.Success, $"Failed to recover divisor {d}: {dec.Explanation}");
            Assert.Equal(d, dec.RecoveredDivisor);
            Assert.True(dec.IsVerifiedExact);
        }
    }

    [Fact]
    public void Item151_BinaryWindowedDiffEngine_FindsShiftedInsertions_And_FixesMultiWindowOffsets()
    {
        byte[] a = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 };
        byte[] b = new byte[] { 0x10, 0x20, 0xFF, 0x30, 0x40, 0x50, 0x60 };

        Assert.Equal(2, BinaryWindowedDiffEngine.FindNextDifferingByte(a, b, 0, forward: true));

        BinaryDiffSummary diff = BinaryWindowedDiffEngine.ComputeWindowedMyersDiff(a, b, windowSize: 4);
        Assert.Equal(2, diff.FirstDifferenceOffset);
        Assert.Equal(1, diff.InsertedBytes);
        Assert.Equal(0, diff.DeletedBytes);
        Assert.Single(diff.Spans);
        Assert.Equal(BinaryDiffSpanType.Inserted, diff.Spans[0].Type);
        Assert.Equal(2, diff.Spans[0].OffsetB);
    }

    [Fact]
    public void Item152_BinaryStringAndNumericScanner_EnforcesStrictRfc3629Utf8_And_ScansUtf16AndFloats()
    {
        // Reject overlong 3-byte 0xE0 0x80 0x80 and UTF-16 surrogate 0xED 0xA0 0x80
        byte[] invalidUtf8 = new byte[] { 0xE0, 0x80, 0x80, 0xED, 0xA0, 0x80 };
        Assert.False(BinaryStringAndNumericScanner.TryConsumeValidUtf8Codepoint(invalidUtf8, 0, out int _, out uint _));
        Assert.False(BinaryStringAndNumericScanner.TryConsumeValidUtf8Codepoint(invalidUtf8, 3, out int _, out uint _));

        // Buffer with ASCII "cvar_fov", UTF-16LE "Hero_Devourer", and f32 144.0f
        byte[] buffer = new byte[64];
        Encoding.ASCII.GetBytes("cvar_fov", buffer.AsSpan(0, 8));
        Encoding.Unicode.GetBytes("Hero_Devourer", buffer.AsSpan(12, 26));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48, 4), BitConverter.SingleToUInt32Bits(144.0f));

        IReadOnlyList<ExtractedBinaryString> strings = BinaryStringAndNumericScanner.ExtractStrings(buffer, minCharacters: 5);
        Assert.Contains(strings, s => s.Value == "cvar_fov" && s.Encoding == ExtractedStringEncoding.AsciiOrUtf8);
        Assert.Contains(strings, s => s.Value == "Hero_Devourer" && s.Encoding == ExtractedStringEncoding.Utf16LittleEndian);

        IReadOnlyList<RangedNumericMatch> floats = BinaryStringAndNumericScanner.ScanFloat32InRange(buffer, 140.0f, 150.0f, alignment: 4);
        Assert.Contains(floats, f => f.ByteOffset == 48 && Math.Abs(f.NumericValue - 144.0) < 0.001);
    }

    [Fact]
    public void Item153_ProcessMemoryMapInspector_BinarySearchesRegions_And_ExtractsPebCommandLine()
    {
        (ulong, ulong, ulong, uint, uint, uint, string?)[] raw = new (ulong, ulong, ulong, uint, uint, uint, string?)[]
        {
            (0x00007FF700100000UL, 0x00007FF700000000UL, 0x50000UL, ProcessMemoryMapInspector.MEM_COMMIT, ProcessMemoryMapInspector.PAGE_EXECUTE_READ, ProcessMemoryMapInspector.MEM_IMAGE, "k2_x64.dll"),
            (0x00007FF700000000UL, 0x00007FF700000000UL, 0x1000UL, ProcessMemoryMapInspector.MEM_COMMIT, ProcessMemoryMapInspector.PAGE_READONLY, ProcessMemoryMapInspector.MEM_IMAGE, "k2_x64.dll"),
            (0x000001F000000000UL, 0x000001F000000000UL, 0x10000UL, ProcessMemoryMapInspector.MEM_COMMIT, ProcessMemoryMapInspector.PAGE_READWRITE, ProcessMemoryMapInspector.MEM_PRIVATE, null)
        };

        IReadOnlyList<MemoryRegionDescriptor> sorted = ProcessMemoryMapInspector.ClassifyMemoryRegions(raw);
        Assert.Equal(3, sorted.Count);
        Assert.Equal(0x000001F000000000UL, sorted[0].BaseAddress);

        MemoryRegionDescriptor? hit = ProcessMemoryMapInspector.LookupRegion(sorted, 0x00007FF700123456UL);
        Assert.NotNull(hit);
        Assert.Equal("k2_x64.dll", hit!.AssociatedModuleOrLabel);
        Assert.Equal("R-X", hit.ProtectionSummary);
        Assert.Equal("ImageCodeSection", hit.RegionClassification);

        // Synthetic x64 RTL_USER_PROCESS_PARAMETERS with CommandLine UNICODE_STRING at +0x70
        string expectedCmd = "hon_x64.exe -dedicated -mod game;KONGOR";
        byte[] cmdBytes = Encoding.Unicode.GetBytes(expectedCmd);
        byte[] rtlParams = new byte[0x80];
        BinaryPrimitives.WriteUInt16LittleEndian(rtlParams.AsSpan(0x70, 2), (ushort)cmdBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(rtlParams.AsSpan(0x72, 2), (ushort)cmdBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(rtlParams.AsSpan(0x78, 8), 0x0000020012340000UL);

        bool extracted = ProcessMemoryMapInspector.TryExtractCommandLineFromProcessParameters(
            rtlParams,
            (ulong addr, int len) => addr == 0x0000020012340000UL && len == cmdBytes.Length ? cmdBytes : null,
            out string actualCmd);
        Assert.True(extracted);
        Assert.Equal(expectedCmd, actualCmd);
    }

    [Fact]
    public async Task Item154_BoundedDecompressionPaginator_CapsBombOutput_And_HandlersDispatchCleanly()
    {
        byte[] payload = new byte[32768];
        Array.Fill(payload, (byte)'K');

        byte[] compressed;
        using (MemoryStream ms = new MemoryStream())
        {
            using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                ds.Write(payload, 0, payload.Length);
            }
            compressed = ms.ToArray();
        }

        // Enforce a 4096-byte ceiling on a 32768-byte stream
        BoundedDecompressionResult capped = BoundedDecompressionPaginator.DecompressBounded(
            compressed,
            isGZip: false,
            maxOutputBytes: 4096);
        Assert.False(capped.Succeeded);
        Assert.True(capped.ExceededOutputCeiling);
        Assert.Equal(4096, capped.DecompressedBytesProduced);
        Assert.NotNull(capped.FaultInputOffset);

        // Verify NativeManageHandler IPS & FastDiv actions
        NativeManageHandler nativeHandler = new NativeManageHandler();
        using JsonDocument fastDivDoc = JsonDocument.Parse("""{"action":"decode_fastdiv","multiplier":"0xAAAAAAAB","post_shift":1,"bit_width":32}""");
        ToolCallResult fastDivRes = await nativeHandler.HandleAsync(fastDivDoc.RootElement);
        Assert.False(fastDivRes.IsError);
        string fastDivJson = JsonSerializer.Serialize(fastDivRes);
        Assert.Contains("recoveredDivisor", fastDivJson, StringComparison.Ordinal);

        // Verify ReplayDiffHandler binary_diff action
        ReplayDiffHandler replayHandler = new ReplayDiffHandler();
        using JsonDocument diffDoc = JsonDocument.Parse("""{"action":"binary_diff","hex_a":"10203040","hex_b":"1020FF3040","allowSimulation":true}""");
        ToolCallResult diffRes = await replayHandler.HandleAsync(diffDoc.RootElement);
        Assert.False(diffRes.IsError);
        string diffJson = JsonSerializer.Serialize(diffRes);
        Assert.Contains("insertedBytes", diffJson, StringComparison.Ordinal);
    }
}
