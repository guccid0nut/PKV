// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Text;
using Kongor.Core.Services.Native;
using Xunit;

namespace Kongor.Core.Tests.Native;

public sealed class NativeZlibBridgeTests
{
    private static bool IsUsableZlibBinary(string dllPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(dllPath))
        {
            return false;
        }

        byte[] peBytes = File.ReadAllBytes(dllPath);
        ReadOnlySpan<byte> proxyMarker = Encoding.Unicode.GetBytes("zlibwapi_real_x64.dll");
        if (peBytes.AsSpan().IndexOf(proxyMarker) < 0)
        {
            // Standalone full zlib 1.3.1 binary (does not forward to zlibwapi_real_x64.dll)
            return true;
        }

        // kongor_zlib proxy shim requires companion zlibwapi_real_x64.dll so thunk_zlibVersion / thunk_compress do not jump to NULL
        string? sourceDir = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrEmpty(sourceDir))
        {
            return false;
        }

        if (File.Exists(Path.Combine(sourceDir, "zlibwapi_real_x64.dll")) ||
            File.Exists(Path.Combine(sourceDir, "windows-x64", "zlibwapi_real_x64.dll")))
        {
            return true;
        }

        return Directory.GetParent(sourceDir) is { } parentDir &&
               File.Exists(Path.Combine(parentDir.FullName, "zlibwapi_real_x64.dll"));
    }

    private static string? TryGetCandidateOrClientDll()
    {
        string? found = NexusTestEnvironment.TryFindNativeBinary("zlibwapi_x64.dll");
        if (found != null && IsUsableZlibBinary(found))
        {
            return found;
        }

        string root = NexusTestEnvironment.RepoRoot;
        string[] fallbacks =
        [
            Path.Combine(root, "src", "tools", "native-binaries", "windows-x64", "zlibwapi_x64.dll"),
            Path.Combine(root, "games", "client", "zlibwapi_x64.dll"),
            Path.Combine(root, "artifacts", "native-build", "release", "zlibwapi_x64.dll")
        ];

        foreach (string candidate in fallbacks)
        {
            if (IsUsableZlibBinary(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetCandidateOrClientDll()
    {
        string? found = TryGetCandidateOrClientDll();
        if (found != null)
        {
            return found;
        }

        throw new FileNotFoundException("Could not locate authentic zlibwapi_x64.dll in repository.");
    }

    [Fact]
    public void VerifyLibrary_WithAuthenticCandidate_SucceedsWithModernVersion()
    {
        string? dllPath = TryGetCandidateOrClientDll();
        if (dllPath is null) return;
        ZlibVerificationResult result = NativeZlibBridge.VerifyLibrary(dllPath);

        Assert.True(result.Success, $"Verification failed: {result.ErrorMessage}");
        Assert.Equal("1.3.1", result.Version);
        Assert.True(result.Crc32Passed);
        Assert.Equal(0xCBF43926u, result.SampleCrc32);
        Assert.True(result.RoundTripPassed);
        Assert.Contains("SECURE", result.SecurityStatus);
        Assert.Contains("CVE-2022-37434 patched", result.SecurityStatus);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ComputeCrc32_StandardTestVector_MatchesIeee8023()
    {
        string? dllPath = TryGetCandidateOrClientDll();
        if (dllPath is null) return;
        using var context = NativeZlibBridge.LoadContext(dllPath);

        Assert.Equal("1.3.1", context.Version);

        byte[] testVector = Encoding.ASCII.GetBytes("123456789");
        uint crc = context.ComputeCrc32(testVector);

        // IEEE 802.3 standard check value for "123456789"
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void CompressAndDecompress_RoundTrip_RestoresOriginalPayload()
    {
        string? dllPath = TryGetCandidateOrClientDll();
        if (dllPath is null) return;
        using var context = NativeZlibBridge.LoadContext(dllPath);

        // 1. Short text payload
        byte[] textPayload = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog 1234567890!");
        byte[] compressedText = context.Compress(textPayload);
        byte[] restoredText = context.Decompress(compressedText, textPayload.Length);
        Assert.Equal(textPayload, restoredText);

        // 2. Highly repetitive 32 KB payload
        byte[] repetitive = new byte[32768];
        for (int i = 0; i < repetitive.Length; i++)
        {
            repetitive[i] = (byte)(i % 16);
        }

        byte[] compressedRepetitive = context.Compress(repetitive);
        Assert.True(compressedRepetitive.Length < repetitive.Length / 10, "Repetitive data must compress significantly.");

        byte[] restoredRepetitive = context.Decompress(compressedRepetitive, repetitive.Length);
        Assert.Equal(repetitive, restoredRepetitive);

        // 3. Binary pattern
        byte[] binary = new byte[1024];
        new Random(42).NextBytes(binary);
        byte[] compressedBinary = context.Compress(binary);
        byte[] restoredBinary = context.Decompress(compressedBinary, binary.Length);
        Assert.Equal(binary, restoredBinary);
    }

    [Fact]
    public void Decompress_CorruptedBuffer_ThrowsInvalidOperationException()
    {
        string? dllPath = TryGetCandidateOrClientDll();
        if (dllPath is null) return;
        using var context = NativeZlibBridge.LoadContext(dllPath);

        byte[] garbage = [0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA];

        var ex = Assert.Throws<InvalidOperationException>(() => context.Decompress(garbage, 100));
        Assert.Contains("uncompress failed", ex.Message);
    }

    [Fact]
    public void VerifyLibrary_WithMissingPath_ReturnsFailureGracefully()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), "Kongor_NonExistent_" + Guid.NewGuid().ToString("N") + ".dll");
        ZlibVerificationResult result = NativeZlibBridge.VerifyLibrary(missingPath);

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.SecurityStatus);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeBinaryManager_VerifyRuntimeZlib_MatchesDirectBridge()
    {
        string? dllPath = TryGetCandidateOrClientDll();
        if (dllPath is null) return;
        ZlibVerificationResult viaManager = NativeBinaryManager.VerifyRuntimeZlib(dllPath);
        ZlibVerificationResult viaBridge = NativeZlibBridge.VerifyLibrary(dllPath);

        Assert.Equal(viaBridge.Success, viaManager.Success);
        Assert.Equal(viaBridge.Version, viaManager.Version);
        Assert.Equal(viaBridge.SampleCrc32, viaManager.SampleCrc32);
        Assert.Equal(viaBridge.SecurityStatus, viaManager.SecurityStatus);
    }
}
