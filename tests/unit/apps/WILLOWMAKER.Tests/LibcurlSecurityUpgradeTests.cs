// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LibcurlSecurityUpgradeTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 4 Modern TLS 1.3 ABI Proxy Shim & libcurl 8.8+ Upgrade
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;
using NativeSecurityUpgradeService = WILLOWMAKER.Core.Services.NativeSecurityUpgradeService;
using PeImportAddressTableService = WILLOWMAKER.Core.Services.PeImportAddressTableService;

namespace WILLOWMAKER.Tests;

public sealed class LibcurlSecurityUpgradeTests
{
    [Fact]
    public void StagedCandidate_Libcurl_ExistsAndMeetsVersionThreshold()
    {
        if (!OperatingSystem.IsWindows()) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string candidatePath = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "libcurl_x64.dll");
        if (!File.Exists(candidatePath)) return;

        var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(candidatePath);
        Assert.True(vi.FileMajorPart > 8 || (vi.FileMajorPart == 8 && vi.FileMinorPart >= 4),
            $"libcurl version must be >= 8.4.0 (current: {vi.FileVersion}) to remediate CVE-2023-38545.");
    }

    [Fact]
    public void VerifyExports_CandidateLibcurl_HasZeroMissingK2Functions()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("libcurl_x64.dll");
        if (candidatePath is null) return;

        string k2Path = Path.Combine(repoRoot, "games", "client", "windows-x64", "k2_x64.dll");
        if (!File.Exists(k2Path))
        {
            k2Path = Path.Combine(repoRoot, "games", "client", "k2_x64.dll");
        }

        if (!File.Exists(k2Path))
        {
            return; // Skip if client binaries not present
        }

        var k2Imports = PeImportAddressTableService.GetImports(k2Path);
        var curlMod = k2Imports.ImportedModules.FirstOrDefault(m => m.DllName.Contains("curl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(curlMod);

        var candidateExports = NativeBinaryManager.ExtractPeExportNames(candidatePath);
        Assert.NotEmpty(candidateExports);
        Assert.True(candidateExports.Count >= 82, $"Expected at least 82 exports, found {candidateExports.Count}");

        // Assert zero missing functions among all 18 functions imported by k2_x64.dll
        var missing = curlMod.ImportedFunctions
            .Where(fn => !candidateExports.Contains(fn, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void ExtractPeExportNames_CandidateLibcurl_ContainsAllCoreCurlApis()
    {
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("libcurl_x64.dll");
        if (candidatePath is null) return;

        var exports = NativeBinaryManager.ExtractPeExportNames(candidatePath);

        // Core easy API
        Assert.Contains("curl_easy_init", exports);
        Assert.Contains("curl_easy_cleanup", exports);
        Assert.Contains("curl_easy_setopt", exports);
        Assert.Contains("curl_easy_perform", exports);
        Assert.Contains("curl_easy_getinfo", exports);
        Assert.Contains("curl_easy_strerror", exports);

        // Multi API
        Assert.Contains("curl_multi_init", exports);
        Assert.Contains("curl_multi_cleanup", exports);
        Assert.Contains("curl_multi_add_handle", exports);
        Assert.Contains("curl_multi_remove_handle", exports);
        Assert.Contains("curl_multi_perform", exports);
        Assert.Contains("curl_multi_info_read", exports);

        // Global and helper APIs
        Assert.Contains("curl_global_init", exports);
        Assert.Contains("curl_global_cleanup", exports);
        Assert.Contains("curl_slist_append", exports);
        Assert.Contains("curl_slist_free_all", exports);
        Assert.Contains("curl_version", exports);
        Assert.Contains("curl_version_info", exports);
    }

    [Fact]
    public void LibcurlSecurityUpgrade_FindSecurityTargets_DiscoversClientAndServerTargets()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        var targets = NativeSecurityUpgradeService.FindSecurityTargets(repoRoot);

        Assert.NotEmpty(targets);
        Assert.Contains(targets, t => t.Contains("libcurl_x64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Contains("libcrypto-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Contains("libssl-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoTierBackupRotation_Lifecycle_RotatesBakToBakOldAndPreservesBaseline()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_SecTwoTier_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client", "windows-x64");
        Directory.CreateDirectory(clientDir);

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string realCandidate = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "libcurl_x64.dll");

        try
        {
            string targetDll = Path.Combine(clientDir, "libcurl_x64.dll");
            string bakPath = targetDll + ".bak";
            string bakOldPath = targetDll + ".bak.old";

            // Stage initial baseline
            byte[] baselineBytes = Encoding.UTF8.GetBytes("ORIGINAL_LIBCURL_7_71_1_BASELINE");
            File.WriteAllBytes(targetDll, baselineBytes);

            // Create fake candidate
            string candidateDir = Path.Combine(tempDir, "candidates");
            Directory.CreateDirectory(candidateDir);
            string fakeCand = Path.Combine(candidateDir, "libcurl_x64.dll");
            if (File.Exists(realCandidate))
            {
                File.Copy(realCandidate, fakeCand);
            }
            else
            {
                File.WriteAllBytes(fakeCand, Encoding.UTF8.GetBytes("UPGRADE_CANDIDATE_V1"));
            }

            // Step 1: First Upgrade
            var firstUpgrade = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
                tempDir, candidateDir, skipProcessCheck: true, skipExportCheck: true);

            Assert.True(firstUpgrade.Success);
            Assert.True(File.Exists(targetDll));
            Assert.True(File.Exists(bakPath), ".bak must be created on first upgrade.");
            Assert.False(File.Exists(bakOldPath), ".bak.old must not exist on first upgrade.");

            byte[] bakBytes = File.ReadAllBytes(bakPath);
            Assert.Equal(baselineBytes, bakBytes);

            // Step 2: Second Upgrade
            File.WriteAllBytes(fakeCand, Encoding.UTF8.GetBytes("UPGRADE_CANDIDATE_V2_DATA"));
            var secondUpgrade = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
                tempDir, candidateDir, skipProcessCheck: true, skipExportCheck: true);

            Assert.True(secondUpgrade.Success);
            Assert.True(File.Exists(bakPath));
            Assert.True(File.Exists(bakOldPath), ".bak.old must now exist after rotation.");

            byte[] bakOldBytes = File.ReadAllBytes(bakOldPath);
            Assert.Equal(baselineBytes, bakOldBytes);

            // Step 3: Rollback to 'previous' (.bak)
            var prevRollback = NativeSecurityUpgradeService.RollbackSecurityBinaries(
                tempDir, "previous", skipProcessCheck: true);
            Assert.True(prevRollback.Success);

            // Step 4: Rollback to 'old' (.bak.old)
            var oldRollback = NativeSecurityUpgradeService.RollbackSecurityBinaries(
                tempDir, "old", skipProcessCheck: true);
            Assert.True(oldRollback.Success);

            byte[] restoredBaseline = File.ReadAllBytes(targetDll);
            Assert.Equal(baselineBytes, restoredBaseline);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
