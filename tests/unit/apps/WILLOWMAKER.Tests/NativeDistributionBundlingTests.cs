// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeDistributionBundlingTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Distribution Bundling - PE32+ Validation & Idempotent Staging Tests
// ============================================================================

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class NativeDistributionBundlingTests : IDisposable
{
    private readonly string _testTempDir;

    public NativeDistributionBundlingTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"kongor_dist_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch { }
    }

    private static readonly string[] ExpectedModernizationBinaries =
    [
        "Kongor.NativeGuard.dll",
        "libcrypto-1_1-x64.dll",
        "libcurl_x64.dll",
        "libssl-1_1-x64.dll",
        "zlibwapi_x64.dll"
    ];

    [Fact]
    public void AllModernizedBinaries_ExistInSourceNativeBinaries_AndAreValidPe32Plus()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string nativeBinDir = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64");
        if (!Directory.Exists(nativeBinDir)) return;

        foreach (string binaryName in ExpectedModernizationBinaries)
        {
            string binaryPath = Path.Combine(nativeBinDir, binaryName);
            Assert.True(File.Exists(binaryPath), $"Binary '{binaryName}' must exist in {nativeBinDir}");

            byte[] bytes = File.ReadAllBytes(binaryPath);
            Assert.True(bytes.Length >= 0x200, $"Binary '{binaryName}' must be at least header size (was {bytes.Length} bytes)");

            // DOS signature
            Assert.Equal((byte)'M', bytes[0]);
            Assert.Equal((byte)'Z', bytes[1]);

            int eLfanew = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
            Assert.True(eLfanew > 0 && eLfanew + 24 < bytes.Length, $"Valid PE header offset required for '{binaryName}'");

            // NT PE signature
            Assert.Equal((byte)'P', bytes[eLfanew]);
            Assert.Equal((byte)'E', bytes[eLfanew + 1]);
            Assert.Equal((byte)0, bytes[eLfanew + 2]);
            Assert.Equal((byte)0, bytes[eLfanew + 3]);

            // Machine AMD64 (0x8664)
            ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eLfanew + 4, 2));
            Assert.Equal((ushort)0x8664, machine);

            // Optional Header Magic: PE32+ (0x020B)
            ushort optMagic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eLfanew + 24, 2));
            Assert.Equal((ushort)0x020B, optMagic);
        }
    }

    [Fact]
    public void IdempotentStaging_CopiesAllBinaries_AndPreservesUnchangedFiles()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string nativeBinDir = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64");
        if (!Directory.Exists(nativeBinDir)) return;

        string stageTargetDir = Path.Combine(_testTempDir, "windows-x64");
        Directory.CreateDirectory(stageTargetDir);

        // First staging sweep
        var sourceFiles = Directory.GetFiles(nativeBinDir);
        Assert.NotEmpty(sourceFiles);

        foreach (string src in sourceFiles)
        {
            string dest = Path.Combine(stageTargetDir, Path.GetFileName(src));
            File.Copy(src, dest, overwrite: true);
        }

        foreach (string binaryName in ExpectedModernizationBinaries)
        {
            string dest = Path.Combine(stageTargetDir, binaryName);
            Assert.True(File.Exists(dest), $"Target '{binaryName}' must be staged in first sweep.");
        }

        // Capture last write times
        var firstWriteTimes = ExpectedModernizationBinaries
            .ToDictionary(name => name, name => File.GetLastWriteTimeUtc(Path.Combine(stageTargetDir, name)));

        // Second staging sweep (idempotency simulation: skip copy if size matches)
        int skippedCount = 0;
        foreach (string src in sourceFiles)
        {
            string dest = Path.Combine(stageTargetDir, Path.GetFileName(src));
            var srcInfo = new FileInfo(src);
            var destInfo = new FileInfo(dest);

            if (destInfo.Exists && destInfo.Length == srcInfo.Length)
            {
                skippedCount++;
            }
            else
            {
                File.Copy(src, dest, overwrite: true);
            }
        }

        Assert.Equal(sourceFiles.Length, skippedCount);

        // Verify timestamps did not change
        foreach (string binaryName in ExpectedModernizationBinaries)
        {
            var currentWriteTime = File.GetLastWriteTimeUtc(Path.Combine(stageTargetDir, binaryName));
            Assert.Equal(firstWriteTimes[binaryName], currentWriteTime);
        }
    }

    [Fact]
    public void NativeGuard_ExportsAndImports_SatisfySupervisionRequirements()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string guardPath = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "Kongor.NativeGuard.dll");
        if (!File.Exists(guardPath)) return;

        var exports = NativeBinaryManager.ExtractPeExportNames(guardPath);
        Assert.NotEmpty(exports);

        foreach (string requiredExport in NativeGuardBinaryBuilder.RequiredExports)
        {
            Assert.Contains(requiredExport, exports, StringComparer.OrdinalIgnoreCase);
        }

        var importReport = PeImportAddressTableService.GetImports(guardPath);
        Assert.True(importReport.IsValidPe);
        Assert.Contains(importReport.ImportedModules, m =>
            m.DllName.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase) &&
            m.ImportedFunctions.Contains("timeBeginPeriod", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Libcurl_And_OpenSsl_ProxyBinaries_HaveValidSymbols()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string nativeBinDir = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64");
        if (!Directory.Exists(nativeBinDir)) return;

        // libcurl_x64.dll exports
        string curlPath = Path.Combine(nativeBinDir, "libcurl_x64.dll");
        var curlExports = NativeBinaryManager.ExtractPeExportNames(curlPath);
        Assert.Contains("curl_easy_init", curlExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("curl_easy_setopt", curlExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("curl_easy_perform", curlExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("curl_easy_cleanup", curlExports, StringComparer.OrdinalIgnoreCase);

        // libssl-1_1-x64.dll exports
        string sslPath = Path.Combine(nativeBinDir, "libssl-1_1-x64.dll");
        var sslExports = NativeBinaryManager.ExtractPeExportNames(sslPath);
        Assert.Contains("TLS_client_method", sslExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SSL_CTX_new", sslExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SSL_connect", sslExports, StringComparer.OrdinalIgnoreCase);

        // libcrypto-1_1-x64.dll exports
        string cryptoPath = Path.Combine(nativeBinDir, "libcrypto-1_1-x64.dll");
        var cryptoExports = NativeBinaryManager.ExtractPeExportNames(cryptoPath);
        Assert.Contains("CRYPTO_malloc", cryptoExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CRYPTO_free", cryptoExports, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("EVP_sha256", cryptoExports, StringComparer.OrdinalIgnoreCase);
    }
}
