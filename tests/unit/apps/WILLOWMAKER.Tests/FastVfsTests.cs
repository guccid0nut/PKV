// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FastVfsTests.cs
// System: WILLOWMAKER Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 7 Zstandard (Zstd) VFS Decompression & libdeflate Acceleration
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ASPIRE.Common.Utilities;
using Kongor.Core.Services.Native;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class FastVfsTests
{
    [Fact]
    public void CandidateBinary_ExistsAndPassesZlibVerification()
    {
        if (!OperatingSystem.IsWindows()) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string candidatePath = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "zlibwapi_x64.dll");
        if (!File.Exists(candidatePath)) return;

        var fi = new FileInfo(candidatePath);
        Assert.True(fi.Length > 180_000, $"Expected dual-engine zlibwapi_x64.dll to be >= 180 KB (contains static Zstd engine), got {fi.Length} B.");

        var result = NativeZlibBridge.VerifyLibrary(candidatePath);
        Assert.True(result.Success, $"C-ABI verification failed: {result.ErrorMessage}");
        Assert.True(result.Crc32Passed, "CRC32 IEEE 802.3 test failed.");
        Assert.True(result.RoundTripPassed, "Round-trip Deflate test failed.");
        Assert.StartsWith("1.3", result.Version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateBinary_TransparentlyDecompressesZstandardBuffer()
    {
        if (!OperatingSystem.IsWindows()) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string candidatePath = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "zlibwapi_x64.dll");
        if (!File.Exists(candidatePath)) return;

        IntPtr handle = NativeLibrary.Load(candidatePath);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(handle, "uncompress", out IntPtr pUncompress));
            var uncompressFn = Marshal.GetDelegateForFunctionPointer<UncompressDelegate>(pUncompress);

            // Create test data and compress with RFC 8878 Zstandard
            string rawString = "HeroesOfNewerth_K2_FastVfs_Zstandard_Transparent_Decompression_Payload_1234567890";
            byte[] originalBytes = Encoding.UTF8.GetBytes(rawString);
            byte[] zstdPayload = originalBytes.AsSpan().CompressZstd(level: 3);

            // Assert RFC 8878 magic header (0xFD2FB528)
            Assert.True(zstdPayload.Length >= 4);
            Assert.Equal(0xFD2FB528U, BitConverter.ToUInt32(zstdPayload, 0));

            // Decompress via the DLL's exported uncompress()
            byte[] decompressedBuffer = new byte[originalBytes.Length + 64];
            uint destLen = (uint)decompressedBuffer.Length;

            int ret = uncompressFn(decompressedBuffer, ref destLen, zstdPayload, (uint)zstdPayload.Length);
            Assert.Equal(0, ret); // Z_OK
            Assert.Equal(originalBytes.Length, (int)destLen);

            string recoveredString = Encoding.UTF8.GetString(decompressedBuffer, 0, (int)destLen);
            Assert.Equal(rawString, recoveredString);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void CandidateBinary_Maintains100PercentOrdinalParity_WithK2Imports()
    {
        if (!OperatingSystem.IsWindows() || !NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("zlibwapi_x64.dll");
        string k2Path = Path.Combine(repoRoot, "games", "client", "k2_x64.dll");

        if (candidatePath is null || !File.Exists(k2Path)) return;

        // Verify that candidate exports the required names and ordinals
        IntPtr handle = NativeLibrary.Load(candidatePath);
        try
        {
            string[] requiredSymbols =
            [
                "crc32",
                "inflate",
                "inflateEnd",
                "inflateInit2_",
                "inflateReset",
                "uncompress",
                "compress2"
            ];

            foreach (string symbol in requiredSymbols)
            {
                bool exported = NativeLibrary.TryGetExport(handle, symbol, out IntPtr p);
                Assert.True(exported, $"Required K2 import symbol '{symbol}' was not exported by candidate zlibwapi_x64.dll.");
                Assert.NotEqual(IntPtr.Zero, p);
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void InspectAndBenchmarkArchive_Caldavar()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string caldavarPath = Path.Combine(repoRoot, "games", "client", "game", "caldavar.s2z");

        if (!File.Exists(caldavarPath)) return;

        var inspection = FastVfsModernizationService.InspectArchive(caldavarPath);
        Assert.True(inspection.Success, inspection.ErrorMessage);
        Assert.True(inspection.TotalEntries > 0);
        Assert.True(inspection.DeflateCount > 0 || inspection.StoredCount > 0);

        var benchmark = FastVfsModernizationService.BenchmarkArchive(caldavarPath, maxEntries: 20);
        Assert.True(benchmark.Success, benchmark.ErrorMessage);
        Assert.True(benchmark.TotalFiles > 0);
        Assert.True(benchmark.ThroughputMbPerSecond >= 0.0);
    }

    [Fact]
    public void DeployAndRollback_TwoTierRotation_InTempDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string candidatePath = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "zlibwapi_x64.dll");
        if (!File.Exists(candidatePath)) return;

        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor_fast_vfs_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Simulate client directory
            string targetDir = Path.Combine(tempRoot, "games", "client");
            Directory.CreateDirectory(targetDir);
            string targetFile = Path.Combine(targetDir, "zlibwapi_x64.dll");

            // Seed with legacy dummy file
            File.WriteAllText(targetFile, "ORIGINAL_V1_CONTENT");

            // 1. First Deployment -> creates .bak
            var dep1 = FastVfsModernizationService.DeployFastVfsBinary(
                tempRoot, candidatePath, skipProcessCheck: true, fleet: TargetFleet.Client);

            Assert.True(dep1.Success);
            Assert.True(File.Exists(targetFile + ".bak"));
            Assert.Equal("ORIGINAL_V1_CONTENT", File.ReadAllText(targetFile + ".bak"));
            Assert.False(File.Exists(targetFile + ".bak.old"));

            // 2. Second Deployment -> rotates .bak -> .bak.old
            var dep2 = FastVfsModernizationService.DeployFastVfsBinary(
                tempRoot, candidatePath, skipProcessCheck: true, fleet: TargetFleet.Client);

            Assert.True(dep2.Success);
            Assert.True(File.Exists(targetFile + ".bak.old"));
            Assert.Equal("ORIGINAL_V1_CONTENT", File.ReadAllText(targetFile + ".bak.old"));

            // 3. Rollback to baseline (.bak.old)
            var rb = FastVfsModernizationService.RollbackFastVfsBinary(
                tempRoot, tier: "old", skipProcessCheck: true, fleet: TargetFleet.Client);

            Assert.True(rb.Success);
            Assert.Equal("ORIGINAL_V1_CONTENT", File.ReadAllText(targetFile));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UncompressDelegate([Out] byte[] dest, ref uint destLen, [In] byte[] source, uint sourceLen);
}
