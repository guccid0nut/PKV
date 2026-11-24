// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpenSslAbiProxyTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 4 Modern TLS 1.3 ABI Proxy Shim & OpenSSL 1.1.1w Upgrade
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;
using PeImportAddressTableService = WILLOWMAKER.Core.Services.PeImportAddressTableService;

namespace WILLOWMAKER.Tests;

public sealed class OpenSslAbiProxyTests
{
    [Fact]
    public void OpenSslAbiProxy_CandidateCrypto_ExistsAndMeetsVersionThreshold()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? cryptoPath = NexusTestEnvironment.TryFindNativeBinary("libcrypto-1_1-x64.dll");
        if (cryptoPath is null) return;

        var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(cryptoPath);
        Assert.True(vi.FileMajorPart >= 1, "FileMajorPart must be >= 1");
        
        // OpenSSL 1.1.1w version check
        bool isVersion111 = vi.FileMajorPart == 1 && vi.FileMinorPart == 1 && vi.FileBuildPart >= 1;
        Assert.True(isVersion111, $"OpenSSL version must be >= 1.1.1w (current: {vi.FileVersion}) to remediate CVE-2022-4304.");
    }

    [Fact]
    public void OpenSslAbiProxy_CandidateSsl_ExistsAndExportsTlsApis()
    {
        string? sslPath = NexusTestEnvironment.TryFindNativeBinary("libssl-1_1-x64.dll");
        if (sslPath is null) return;

        var exports = NativeBinaryManager.ExtractPeExportNames(sslPath);
        Assert.NotEmpty(exports);
        Assert.True(exports.Count >= 450, $"Expected at least 450 exports in libssl-1_1-x64.dll, found {exports.Count}");

        // Standard TLS/SSL APIs
        Assert.Contains("TLS_client_method", exports);
        Assert.Contains("TLS_server_method", exports);
        Assert.Contains("SSL_CTX_new", exports);
        Assert.Contains("SSL_CTX_free", exports);
        Assert.Contains("SSL_new", exports);
        Assert.Contains("SSL_free", exports);
        Assert.Contains("SSL_connect", exports);
        Assert.Contains("SSL_accept", exports);
        Assert.Contains("SSL_read", exports);
        Assert.Contains("SSL_write", exports);
        Assert.Contains("SSL_shutdown", exports);
    }

    [Fact]
    public void VerifyCryptoParity_CandidateLibcrypto_HasZeroMissingK2Functions()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("libcrypto-1_1-x64.dll");
        if (candidatePath is null) return;

        string k2Path = Path.Combine(repoRoot, "games", "client", "windows-x64", "k2_x64.dll");
        if (!File.Exists(k2Path))
        {
            k2Path = Path.Combine(repoRoot, "games", "client", "k2_x64.dll");
        }

        if (!File.Exists(k2Path))
        {
            return; // Skip if client binaries not present in current test environment
        }

        var k2Imports = PeImportAddressTableService.GetImports(k2Path);
        var cryptoMod = k2Imports.ImportedModules.FirstOrDefault(m => m.DllName.Contains("crypto", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cryptoMod);

        var candidateExports = NativeBinaryManager.ExtractPeExportNames(candidatePath);
        Assert.NotEmpty(candidateExports);
        Assert.True(candidateExports.Count >= 4000, $"Expected at least 4,000 exports, found {candidateExports.Count}");

        // Assert 100% parity across all functions imported by k2_x64.dll
        var missing = cryptoMod.ImportedFunctions
            .Where(fn => !candidateExports.Contains(fn, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void ExtractPeExportNames_CandidateLibcrypto_ContainsAllCoreBignumAndHashingApis()
    {
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("libcrypto-1_1-x64.dll");
        if (candidatePath is null) return;

        var exports = NativeBinaryManager.ExtractPeExportNames(candidatePath);

        // Core BigNum APIs for SRP-6a authentication and public key cryptography
        Assert.Contains("BN_new", exports);
        Assert.Contains("BN_free", exports);
        Assert.Contains("BN_clear_free", exports);
        Assert.Contains("BN_bin2bn", exports);
        Assert.Contains("BN_bn2bin", exports);
        Assert.Contains("BN_mod_exp", exports);
        Assert.Contains("BN_mul", exports);
        Assert.Contains("BN_div", exports);
        Assert.Contains("BN_add", exports);
        Assert.Contains("BN_sub", exports);
        Assert.Contains("BN_cmp", exports);
        Assert.Contains("BN_ucmp", exports);
        Assert.Contains("BN_CTX_new", exports);
        Assert.Contains("BN_CTX_free", exports);
        Assert.Contains("BN_set_word", exports);
        Assert.Contains("BN_get_word", exports);

        // Cryptographic hash algorithms
        Assert.Contains("SHA256_Init", exports);
        Assert.Contains("SHA256_Update", exports);
        Assert.Contains("SHA256_Final", exports);
        Assert.Contains("SHA1_Init", exports);
        Assert.Contains("SHA1_Update", exports);
        Assert.Contains("SHA1_Final", exports);
        Assert.Contains("SHA512_Init", exports);
        Assert.Contains("SHA512_Update", exports);
        Assert.Contains("SHA512_Final", exports);
        Assert.Contains("MD5_Init", exports);
        Assert.Contains("MD5_Update", exports);
        Assert.Contains("MD5_Final", exports);
    }

    [Fact]
    public void OpenSslAbiProxy_CandidateOpenSsl_HasValidImportsAndDependencies()
    {
        string? cryptoPath = NexusTestEnvironment.TryFindNativeBinary("libcrypto-1_1-x64.dll");
        string? sslPath = NexusTestEnvironment.TryFindNativeBinary("libssl-1_1-x64.dll");
        if (cryptoPath is null || sslPath is null) return;

        var cryptoImports = PeImportAddressTableService.GetImports(cryptoPath);
        Assert.NotEmpty(cryptoImports.ImportedModules);

        // libcrypto should import standard Windows subsystem DLLs
        var cryptoModuleNames = cryptoImports.ImportedModules.Select(m => m.DllName.ToUpperInvariant()).ToList();
        Assert.Contains(cryptoModuleNames, m => m.Contains("KERNEL32"));

        // libssl should import libcrypto
        var sslImports = PeImportAddressTableService.GetImports(sslPath);
        Assert.NotEmpty(sslImports.ImportedModules);
        var sslModuleNames = sslImports.ImportedModules.Select(m => m.DllName.ToUpperInvariant()).ToList();
        Assert.Contains(sslModuleNames, m => m.Contains("CRYPTO"));
    }

    [Fact]
    public void ExtractPeExportNames_CorruptedBuffer_ReturnsEmptyWithoutThrowing()
    {
        // 1. Truncated buffer
        byte[] tiny = new byte[32];
        var resTiny = NativeBinaryManager.ExtractPeExportNames(tiny);
        Assert.Empty(resTiny);

        // 2. Corrupt e_lfanew
        byte[] badLfanew = new byte[256];
        badLfanew[0] = 0x4D;
        badLfanew[1] = 0x5A;
        BitConverter.GetBytes(0x7FFFFFFF).CopyTo(badLfanew, 0x3C);
        var resBad = NativeBinaryManager.ExtractPeExportNames(badLfanew);
        Assert.Empty(resBad);

        // 3. Negative e_lfanew
        byte[] negLfanew = new byte[256];
        negLfanew[0] = 0x4D;
        negLfanew[1] = 0x5A;
        BitConverter.GetBytes(-100).CopyTo(negLfanew, 0x3C);
        var resNeg = NativeBinaryManager.ExtractPeExportNames(negLfanew);
        Assert.Empty(resNeg);

        // 4. Zero buffer
        byte[] zeros = new byte[4096];
        var resZeros = NativeBinaryManager.ExtractPeExportNames(zeros);
        Assert.Empty(resZeros);
    }

    [Fact]
    public void NativeProxies_KongorSsl_DefFile_ExportsAllCoreTlsSymbols()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string defPath = Path.Combine(repoRoot, "native", "proxies", "kongor_ssl", "kongor_ssl.def");
        Assert.True(File.Exists(defPath), $"kongor_ssl.def must exist at: {defPath}");

        string defContent = File.ReadAllText(defPath);
        Assert.Contains("SSL_connect", defContent);
        Assert.Contains("SSL_read", defContent);
        Assert.Contains("SSL_write", defContent);
        Assert.Contains("TLS_client_method", defContent);
        Assert.Contains("SSL_CTX_new", defContent);
        Assert.Contains("SSL_new", defContent);
        Assert.Contains("SSL_free", defContent);
    }

    [Fact]
    public void NativeProxies_KongorCrypto_DefFile_ExportsAllCoreCryptoSymbols()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string defPath = Path.Combine(repoRoot, "native", "proxies", "kongor_crypto", "kongor_crypto.def");
        Assert.True(File.Exists(defPath), $"kongor_crypto.def must exist at: {defPath}");

        string defContent = File.ReadAllText(defPath);
        // Core BigNum & SHA symbols required by k2_x64.dll
        Assert.Contains("BN_new", defContent);
        Assert.Contains("BN_hex2bn", defContent);
        Assert.Contains("BN_mod_exp", defContent);
        Assert.Contains("SHA256_Init", defContent);
        Assert.Contains("SHA256_Update", defContent);
        Assert.Contains("SHA256_Final", defContent);
        Assert.Contains("CRYPTO_free", defContent);
        Assert.Contains("RAND_seed", defContent);
    }
}

