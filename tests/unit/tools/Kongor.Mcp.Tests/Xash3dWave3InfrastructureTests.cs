// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ASPIRE.Common.Diagnostics;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Protocol.Diagnostics;
using ReplayVulkanRenderer;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Phase 11 (Wave v3 #135-#144) Xash3D-FWGS VFS, Console, Crash Shield, Delta Schema,
/// 32-Opcode Flight Recorder, DDS Mipmap/Dead-Alpha Validator, and Fast AABB Transform tests.
/// Protects: OBL-W3-01 through OBL-W3-06 and cross-tree C++/C# vector parity.
/// </summary>
public sealed class Xash3dWave3InfrastructureTests
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
    public void VfsPathFirewall_CrossTreeVectors_And_NativeSourceContract_Parity()
    {
        string repoRoot = FindRepoRoot();
        string vectorsPath = Path.Combine(repoRoot, "data", "schemas", "wave3_cross_tree_vectors.json");
        Assert.True(File.Exists(vectorsPath), $"Missing shared cross-tree vector file at {vectorsPath}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(vectorsPath));
        JsonElement root = doc.RootElement;

        // 1. Verify all shared nasty_path_vectors (#144, OBL-W3-01)
        JsonElement nastyVectors = root.GetProperty("nasty_path_vectors");
        int nastyChecked = 0;
        foreach (JsonElement item in nastyVectors.EnumerateArray())
        {
            string path = item.GetProperty("path").GetString() ?? string.Empty;
            bool expectedSafe = item.GetProperty("expected_safe").GetBoolean();
            bool actualSafe = VfsPathFirewall.CheckNastyPath(path);
            Assert.True(
                actualSafe == expectedSafe,
                $"NastyPath vector mismatch for '{path}': expected {expectedSafe}, got {actualSafe}");
            nastyChecked++;
        }
        Assert.True(nastyChecked >= 12);

        // 2. Verify all shared wildcard_vectors (#135)
        JsonElement wildcardVectors = root.GetProperty("wildcard_vectors");
        int wildcardChecked = 0;
        foreach (JsonElement item in wildcardVectors.EnumerateArray())
        {
            string input = item.GetProperty("input").GetString() ?? string.Empty;
            string pattern = item.GetProperty("pattern").GetString() ?? string.Empty;
            bool caseInsensitive = item.GetProperty("case_insensitive").GetBoolean();
            bool expectedMatch = item.GetProperty("expected_match").GetBoolean();
            bool actualMatch = VfsPathFirewall.MatchWildcard(input, pattern, caseInsensitive);
            Assert.True(
                actualMatch == expectedMatch,
                $"Wildcard vector mismatch for input='{input}', pattern='{pattern}': expected {expectedMatch}, got {actualMatch}");
            wildcardChecked++;
        }
        Assert.True(wildcardChecked >= 6);

        // 3. Verify C++ header and source contracts for OBL-W3-01 and OBL-W3-06
        string hookHeader = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "modern_archive_hook.h"));
        string hookCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "modern_archive_hook.cpp"));

        Assert.Contains("SehSafeVsnprintf", hookHeader);
        Assert.Contains("satisfies: OBL-W3-06", hookHeader);
        Assert.Contains("Kongor_CheckNastyPathA", hookHeader);
        Assert.Contains("Kongor_MatchWildcardA", hookHeader);
        Assert.Contains("TryResolveS2zDirLooseFileW", hookCpp);
        Assert.Contains("Kongor_NativeGuard_VfsFindFile", hookCpp);
        Assert.Contains("std::shared_lock<std::shared_mutex> lock(g_archiveMapMutex)", hookCpp);
        Assert.Contains("satisfies: OBL-W3-01", hookCpp);
    }

    [Fact]
    public void BaseCmdSortedHash_And_CommandSplitter_CrossTreeVectors_Pass()
    {
        string repoRoot = FindRepoRoot();
        string vectorsPath = Path.Combine(repoRoot, "data", "schemas", "wave3_cross_tree_vectors.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(vectorsPath));
        JsonElement splitVectors = doc.RootElement.GetProperty("command_split_vectors");

        int checkedCount = 0;
        foreach (JsonElement item in splitVectors.EnumerateArray())
        {
            string raw = item.GetProperty("buffer").GetString() ?? string.Empty;
            bool isPrivileged = item.GetProperty("is_privileged").GetBoolean();
            int expectedBlocked = item.GetProperty("expected_blocked").GetInt32();
            JsonElement expectedArr = item.GetProperty("expected_commands");
            List<string> expected = new List<string>();
            foreach (JsonElement cmdEl in expectedArr.EnumerateArray())
            {
                expected.Add(cmdEl.GetString() ?? string.Empty);
            }

            IReadOnlyList<string> actual = VfsPathFirewall.SplitAndFilterCommandBuffer(
                raw, isPrivileged, out int blockedCount, out bool wasTruncated);

            Assert.False(wasTruncated);
            Assert.Equal(expectedBlocked, blockedCount);
            Assert.Equal(expected, actual);
            checkedCount++;
        }
        Assert.True(checkedCount >= 2);

        // Verify MaxCmdLineLength (2048-character) cap truncation
        string oversized = new string('a', 3000);
        IReadOnlyList<string> truncatedCmds = VfsPathFirewall.SplitAndFilterCommandBuffer(
            oversized, true, out int _, out bool didTruncate);
        Assert.True(didTruncate);
        Assert.Single(truncatedCmds);
        Assert.Equal(VfsPathFirewall.MaxCmdLineLength, truncatedCmds[0].Length);

        // Verify C++ BaseCmdTable (#136, OBL-W3-02) and CmdSanitizer (#137, OBL-W3-03)
        string baseCmdHeader = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "kongor_base_cmd.h"));
        string dllMainCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "dllmain.cpp")) +
                            File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "vfs_and_hooks.cpp"));

        Assert.Contains("kBucketCount = 128", baseCmdHeader);
        Assert.Contains("satisfies: OBL-W3-02", baseCmdHeader);
        Assert.Contains("SplitAndValidateCommandBufferW", baseCmdHeader);
        Assert.Contains("satisfies: OBL-W3-03", dllMainCpp);
        Assert.Contains("GetGlobalBaseCmdTable().MatchCommandLine", dllMainCpp);
    }

    [Fact]
    public void ZoneTailCanary_And_TwoTierMinidump_NativeContracts_AreEnforced()
    {
        string repoRoot = FindRepoRoot();
        string hookCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "modern_archive_hook.cpp"));
        string crashCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "veh_crash_shield.cpp"));

        // #138 (OBL-W3-04): +2 byte allocation with '\0' terminator and 0xDF tail canary
        Assert.Contains("uncompSize + 2", hookCpp);
        Assert.Contains("0xDF", hookCpp);
        Assert.Contains("[XASH3D-CANARY-CORRUPT]", hookCpp);
        Assert.Contains("satisfies: OBL-W3-04", hookCpp);

        // #139 (OBL-W3-05): 16-frame zero-allocation RVA stack walk + Minimal-First two-tier minidump
        Assert.Contains("AppendSehSafeStackWalk64", crashCpp);
        Assert.Contains("MiniDumpNormal | MiniDumpWithThreadInfo", crashCpp);
        Assert.Contains("MiniDumpWithIndirectlyReferencedMemory", crashCpp);
        Assert.Contains("FlushFileBuffers(hDumpFile)", crashCpp);
        Assert.Contains("minidump_%s.rich.dmp", crashCpp);
        Assert.Contains("satisfies: OBL-W3-05", crashCpp);
    }

    [Fact]
    public void MinizLazyArchiveFallback_And_GetFileListHooks_Contract_IsEnforced()
    {
        string repoRoot = FindRepoRoot();
        string hookCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "modern_archive_hook.cpp"));
        string dllMainCpp = File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "dllmain.cpp")) +
                            File.ReadAllText(Path.Combine(repoRoot, "native", "proxies", "native_guard", "vfs_and_hooks.cpp"));
        string hookSitesJson = File.ReadAllText(Path.Combine(repoRoot, "data", "patches", "native_hook_sites.json"));

        // 1. Lazy fallback gate (zero overhead on < 1.5 GiB archives when K2 native CMMapUnzip succeeds)
        //    plus Zip64 / >= 1.5 GiB pre-check forcing ARCHIVE_DISABLE_MMAP (0x80) to prevent CMMapUnzip 0xC0000005
        Assert.Contains("ShouldUseModernArchive", hookCpp);
        Assert.Contains("1610612736ULL", hookCpp);
        Assert.Contains("g_passthroughArchives", hookCpp);
        Assert.Contains("IsArchiveZip64OrLargeOnDisk", hookCpp);
        Assert.Contains("iMode | 0x80", hookCpp);

        // 2. Preserve/reconstruct CArchive m_sPath (+0x08), m_sPathToArchive (+0x28), m_sMod (+0x48), and m_sCompleteDiskPath (+0x88)
        //    plus slash-agnostic prefix stripping for ContainsFile and ReadFile
        Assert.Contains("g_pK2StringWAssign", hookCpp);
        Assert.Contains("g_pK2StringWVecPushBackRval", hookCpp);
        Assert.Contains("g_pK2StringWDtor", hookCpp);
        Assert.Contains("0x2A4890", hookCpp);
        Assert.Contains("0x2B2E40", hookCpp);
        Assert.Contains("0x2A4A60", hookCpp);
        Assert.Contains("StripPathPrefixW", hookCpp);

        // 3. Safe pInternal + 0x40..0x50 vector restoration & directory iteration hooks
        Assert.Contains("synthesized_unzip_vec", hookCpp);
        Assert.Contains("orig_vec_40", hookCpp);
        Assert.Contains("orig_vec_48", hookCpp);
        Assert.Contains("orig_vec_50", hookCpp);
        Assert.Contains("Hooked_CArchive_GetFileList", hookCpp);
        Assert.Contains("Hooked_CArchive_GetNextFileAtPath", hookCpp);
        Assert.Contains("0x679270", hookCpp);
        Assert.Contains("0x679410", hookCpp);

        // 4. Default enablement with KONGOR_DISABLE_MINIZ_ARCHIVE=1 kill-switch in dllmain.cpp
        Assert.Contains("KONGOR_DISABLE_MINIZ_ARCHIVE", dllMainCpp);
        Assert.Contains("InstallModernArchiveHooks", dllMainCpp);

        using JsonDocument doc = JsonDocument.Parse(hookSitesJson);
        JsonElement sites = doc.RootElement.GetProperty("sites");
        Assert.Equal(7, sites.GetArrayLength());

        if (!NexusTestEnvironment.HasGameAssets) return;

        // 5. Verify all 7 hook sites in data/patches/native_hook_sites.json against on-disk k2_x64.dll PE64 binary
        string k2Path = Path.Combine(repoRoot, "games", "client", "k2_x64.dll");
        Assert.True(File.Exists(k2Path), $"Expected k2_x64.dll at {k2Path}");
        byte[] k2Bytes = File.ReadAllBytes(k2Path);

        foreach (JsonElement site in sites.EnumerateArray())
        {
            string rvaHex = site.GetProperty("rva").GetString() ?? "0x0";
            uint rva = Convert.ToUInt32(rvaHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rvaHex[2..] : rvaHex, 16);
            string expectedHex = site.GetProperty("expected_prologue_hex").GetString() ?? string.Empty;
            string[] hexTokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(hexTokens);
            uint fileOffset = MapPeRvaToFileOffset(k2Bytes, rva);
            Assert.True(fileOffset > 0, $"Failed to map RVA {rvaHex} in k2_x64.dll");
            for (int i = 0; i < hexTokens.Length; i++)
            {
                byte expectedByte = Convert.ToByte(hexTokens[i], 16);
                byte actualByte = k2Bytes[fileOffset + i];
                Assert.True(expectedByte == actualByte, $"Prologue mismatch at {rvaHex}+{i}: expected 0x{expectedByte:X2}, got 0x{actualByte:X2}");
            }
        }

        // 6. Verify compiled version.dll binary markers and 4-way deployment parity
        string[] deployedDlls =
        [
            Path.Combine(repoRoot, "games", "client", "version.dll"),
            Path.Combine(repoRoot, "games", "client", "windows-x64", "version.dll"),
            Path.Combine(repoRoot, "games", "server", "version.dll"),
            Path.Combine(repoRoot, "games", "server", "windows-x64", "version.dll")
        ];
        byte[] primaryVersionDll = File.ReadAllBytes(deployedDlls[0]);
        string versionDllAscii = Encoding.ASCII.GetString(primaryVersionDll);
        Assert.Contains("KONGOR_DISABLE_MINIZ_ARCHIVE", versionDllAscii);
        Assert.Contains("KONGOR_FORCE_MINIZ_ALL", versionDllAscii);
        Assert.Contains("CArchive::GetFileList", versionDllAscii);
        Assert.Contains("CArchive::GetNextFileAtPath", versionDllAscii);

        string primaryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(primaryVersionDll));
        for (int i = 1; i < deployedDlls.Length; i++)
        {
            Assert.True(File.Exists(deployedDlls[i]), $"Missing deployed version.dll at {deployedDlls[i]}");
            string h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(deployedDlls[i])));
            Assert.Equal(primaryHash, h);
        }
    }

    private static uint MapPeRvaToFileOffset(ReadOnlySpan<byte> peBytes, uint rva)
    {
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(peBytes.Slice(0x3C, 4));
        ushort numSections = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.Slice(peOffset + 6, 2));
        ushort sizeOfOptHeader = BinaryPrimitives.ReadUInt16LittleEndian(peBytes.Slice(peOffset + 20, 2));
        int secTableOffset = peOffset + 24 + sizeOfOptHeader;
        for (int i = 0; i < numSections; i++)
        {
            int secOffset = secTableOffset + (i * 40);
            uint vSize = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.Slice(secOffset + 8, 4));
            uint vAddr = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.Slice(secOffset + 12, 4));
            uint rSize = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.Slice(secOffset + 16, 4));
            uint rPtr = BinaryPrimitives.ReadUInt32LittleEndian(peBytes.Slice(secOffset + 20, 4));
            uint span = Math.Max(vSize, rSize);
            if (rva >= vAddr && rva < vAddr + span)
            {
                return rPtr + (rva - vAddr);
            }
        }
        return 0;
    }

    [Fact]
    public async Task SnapshotDeltaSchema_QuantizesFields_And_IntegratesWithReplayDiffHandler()
    {
        Dictionary<string, double> serverSnap = new Dictionary<string, double>
        {
            ["origin_x"] = 1000.0,
            ["origin_y"] = -500.0,
            ["yaw"] = 180.0,
            ["health"] = 1500.0
        };

        // Sub-quantization noise on origin_x (0.01 * 8.0 = 0.08 -> rounds to same integer)
        // Real drift on origin_y (-500.0 vs -495.0) and health (1500 vs 1250)
        Dictionary<string, double> clientSnap = new Dictionary<string, double>
        {
            ["origin_x"] = 1000.01,
            ["origin_y"] = -495.0,
            ["yaw"] = 180.0,
            ["health"] = 1250.0
        };

        SnapshotDeltaReport report = SnapshotDeltaSchema.CompareSnapshots(
            "k2_entity_state",
            SnapshotDeltaSchema.DefaultEntityStateFields,
            serverSnap,
            clientSnap);

        Assert.Equal(2, report.DriftedFieldCount);
        Assert.Equal("origin_y", report.Diffs[0].FieldName);
        Assert.Equal(40L, report.Diffs[0].QuantizedDelta); // (-495 - (-500)) * 8 = +40
        Assert.Equal("health", report.Diffs[1].FieldName);
        Assert.Equal(-250L, report.Diffs[1].QuantizedDelta);

        ReplayDiffHandler handler = new ReplayDiffHandler();
        using JsonDocument argsDoc = JsonDocument.Parse("""
        {
            "action": "diff_schema",
            "snapshot_a": { "origin_x": 100.0, "health": 800.0 },
            "snapshot_b": { "origin_x": 110.0, "health": 800.0 },
            "allowSimulation": true
        }
        """);
        ToolCallResult res = await handler.HandleAsync(argsDoc.RootElement);
        Assert.False(res.IsError);
        string resJson = JsonSerializer.Serialize(res);
        Assert.Contains("driftedFieldCount", resJson);
    }

    [Fact]
    public async Task PacketOpcodeFlightRecorder_32SlotRing_CapturesWraparoundAndTruncatedFrames()
    {
        PacketOpcodeFlightRecorder recorder = new PacketOpcodeFlightRecorder();
        byte[] samplePayload = new byte[] { 0x11, 0x22, 0x33, 0x44 };

        // Record 50 commands to exercise power-of-two ring wraparound (MSG_COUNT = 32, MSG_MASK = 31)
        for (int i = 0; i < 50; i++)
        {
            recorder.RecordCommand(frameNumber: 100 + i, opcode: (byte)(i & 0x7F), opcodeName: $"svc_cmd_{i}", byteOffset: i * 7, samplePayload);
        }

        IReadOnlyList<PacketOpcodeEntry> history = recorder.GetChronologicalHistory();
        Assert.Equal(PacketOpcodeFlightRecorder.MSG_COUNT, history.Count);
        Assert.Equal(18, history[0].SequenceIndex);
        Assert.Equal(49, history[31].SequenceIndex);

        // Build a stream with 2 valid commands followed by a truncated command
        byte[] streamBytes = new byte[]
        {
            0x01, 0x02, 0x00, 0xAA, 0xBB,       // valid cmd 0 (len=2)
            0x02, 0x01, 0x00, 0xCC,             // valid cmd 1 (len=1)
            0x03, 0x10, 0x00, 0xDD, 0xEE        // truncated cmd 2 (declares len=16, only 2 bytes present)
        };

        PacketOpcodeFlightRecorder streamRecorder = new PacketOpcodeFlightRecorder();
        PacketFaultDiagnosis diagnosis = streamRecorder.InspectPacketStream(streamBytes, frameNumber: 77);
        Assert.Contains("Truncated payload for opcode 0x03", diagnosis.FaultReason);
        Assert.Equal(9, diagnosis.FaultByteOffset);
        Assert.Equal(3, diagnosis.RecentHistory.Count);
        Assert.NotEmpty(diagnosis.FaultCaptureWindow);

        ReplayDiffHandler handler = new ReplayDiffHandler();
        using JsonDocument argsDoc = JsonDocument.Parse("""
        {
            "action": "flight_recorder",
            "frame": 42,
            "packet_hex": "010200AABBFF0000"
        }
        """);
        ToolCallResult mcpRes = await handler.HandleAsync(argsDoc.RootElement);
        Assert.False(mcpRes.IsError);
        string mcpJson = JsonSerializer.Serialize(mcpRes);
        Assert.Contains("Corrupt/bad server opcode 0xFF", mcpJson);
    }

    [Fact]
    public void DdsMipmapAndAlphaValidator_DetectsTruncatedMips_And_DeadAlpha50PercentVramSavings()
    {
        // 1. Construct a valid 4x4 DXT5 (BC3) 1-mip texture where alpha is 100% opaque (alpha0=255, alpha1=255, indices=0)
        byte[] opaqueDxt5 = CreateSyntheticDdsBuffer("DXT5", width: 4, height: 4, mipCount: 1, payloadSize: 16);
        opaqueDxt5[128] = 255; // alpha0
        opaqueDxt5[129] = 255; // alpha1
        for (int i = 130; i < 136; i++)
        {
            opaqueDxt5[i] = 0; // all pixels select alpha0 (255)
        }

        DdsValidationReport opaqueReport = DdsMipmapAndAlphaValidator.ValidateDdsBuffer(opaqueDxt5);
        Assert.True(opaqueReport.IsValidHeader);
        Assert.False(opaqueReport.IsTruncatedMipmapChain);
        Assert.False(opaqueReport.HasNonOpaqueAlpha);
        Assert.True(opaqueReport.IsDeadAlphaOpaqueCandidate);
        Assert.Equal(8, opaqueReport.EstimatedVramSavingsBytes);

        // 2. Make pixel 0 semi-transparent (alpha0 = 128) -> no longer dead-alpha candidate
        byte[] transDxt5 = (byte[])opaqueDxt5.Clone();
        transDxt5[128] = 128;
        DdsValidationReport transReport = DdsMipmapAndAlphaValidator.ValidateDdsBuffer(transDxt5);
        Assert.True(transReport.HasNonOpaqueAlpha);
        Assert.False(transReport.IsDeadAlphaOpaqueCandidate);

        // 3. Construct a truncated mipmap chain (declares 4 mips for 16x16 DXT1, but only provides mip0 = 128 bytes)
        byte[] truncatedDxt1 = CreateSyntheticDdsBuffer("DXT1", width: 16, height: 16, mipCount: 4, payloadSize: 128);
        DdsValidationReport truncReport = DdsMipmapAndAlphaValidator.ValidateDdsBuffer(truncatedDxt1);
        Assert.True(truncReport.IsTruncatedMipmapChain);
        Assert.Equal(4, truncReport.DeclaredMipCount);
        Assert.Equal(1, truncReport.ValidMipLevelsPresent);

        // 4. Verify integration with AssetInspectService.InspectDds
        AssetInspectService service = new AssetInspectService();
        using MemoryStream ms = new MemoryStream(opaqueDxt5);
        AssetInspectionResult inspectRes = service.InspectDds(ms, "ui/test_opaque.dds");
        Assert.True(inspectRes.Success);
        Assert.NotNull(inspectRes.DdsValidation);
        Assert.True(inspectRes.DdsValidation!.IsDeadAlphaOpaqueCandidate);
    }

    [Fact]
    public void Matrix3x4AbsAabbTransform_Matches8CornerBruteForce_And_4TierEntityMatrix_MatchesFullTrig()
    {
        Vector3 localMin = new Vector3(-24.5f, -18.0f, -5.0f);
        Vector3 localMax = new Vector3(32.0f, 41.25f, 88.5f);
        Vector3 origin = new Vector3(410.0f, -920.5f, 64.0f);

        // Verify all 4 tiers of CreateEntityMatrixFast against full Matrix4x4.CreateFromYawPitchRoll
        (float pitch, float yaw, float roll, int expectedTier)[] cases = new (float, float, float, int)[]
        {
            (0.0f,  0.0f,   0.0f,  0),
            (0.0f,  135.0f, 0.0f,  1),
            (25.0f, -60.0f, 0.0f,  2),
            (15.0f, 45.0f,  -30.0f, 3)
        };

        const float degToRad = MathF.PI / 180.0f;

        for (int i = 0; i < cases.Length; i++)
        {
            (float pitch, float yaw, float roll, int expectedTier) c = cases[i];
            Matrix4x4 fastMat = SkeletalAnimationEngine.CreateEntityMatrixFast(
                origin, c.pitch, c.yaw, c.roll, out int actualTier);

            Assert.Equal(c.expectedTier, actualTier);

            Matrix4x4 refMat = Matrix4x4.CreateFromYawPitchRoll(
                c.yaw * degToRad, c.pitch * degToRad, c.roll * degToRad);
            refMat.M41 = origin.X;
            refMat.M42 = origin.Y;
            refMat.M43 = origin.Z;

            AssertMatrixEqual(refMat, fastMat, 1e-5f);

            // Verify TransformAabbFast against brute-force 8-corner transformation
            SkeletalAnimationEngine.TransformAabbFast(localMin, localMax, in fastMat, out Vector3 fastWorldMin, out Vector3 fastWorldMax);

            Vector3 bruteMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bruteMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int cornerMask = 0; cornerMask < 8; cornerMask++)
            {
                Vector3 corner = new Vector3(
                    (cornerMask & 1) != 0 ? localMax.X : localMin.X,
                    (cornerMask & 2) != 0 ? localMax.Y : localMin.Y,
                    (cornerMask & 4) != 0 ? localMax.Z : localMin.Z);
                Vector3 worldCorner = Vector3.Transform(corner, fastMat);
                bruteMin = Vector3.Min(bruteMin, worldCorner);
                bruteMax = Vector3.Max(bruteMax, worldCorner);
            }

            Assert.True(Vector3.Distance(bruteMin, fastWorldMin) < 1e-3f, $"AABB min mismatch on tier {actualTier}: brute={bruteMin}, fast={fastWorldMin}");
            Assert.True(Vector3.Distance(bruteMax, fastWorldMax) < 1e-3f, $"AABB max mismatch on tier {actualTier}: brute={bruteMax}, fast={fastWorldMax}");
        }
    }

    private static byte[] CreateSyntheticDdsBuffer(string fourCC, int width, int height, int mipCount, int payloadSize)
    {
        byte[] buf = new byte[128 + payloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0x20534444u); // "DDS "
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 124u);        // dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 0x00021007u); // dwFlags
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), (uint)mipCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(76, 4), 32u);        // pfSize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), 0x4u);       // DDPF_FOURCC
        Encoding.ASCII.GetBytes(fourCC.PadRight(4, ' ').AsSpan(0, 4), buf.AsSpan(84, 4));
        return buf;
    }

    private static void AssertMatrixEqual(in Matrix4x4 expected, in Matrix4x4 actual, float tolerance)
    {
        Assert.True(MathF.Abs(expected.M11 - actual.M11) <= tolerance);
        Assert.True(MathF.Abs(expected.M12 - actual.M12) <= tolerance);
        Assert.True(MathF.Abs(expected.M13 - actual.M13) <= tolerance);
        Assert.True(MathF.Abs(expected.M21 - actual.M21) <= tolerance);
        Assert.True(MathF.Abs(expected.M22 - actual.M22) <= tolerance);
        Assert.True(MathF.Abs(expected.M23 - actual.M23) <= tolerance);
        Assert.True(MathF.Abs(expected.M31 - actual.M31) <= tolerance);
        Assert.True(MathF.Abs(expected.M32 - actual.M32) <= tolerance);
        Assert.True(MathF.Abs(expected.M33 - actual.M33) <= tolerance);
        Assert.True(MathF.Abs(expected.M41 - actual.M41) <= tolerance);
        Assert.True(MathF.Abs(expected.M42 - actual.M42) <= tolerance);
        Assert.True(MathF.Abs(expected.M43 - actual.M43) <= tolerance);
    }
}
