// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessAudioStubTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 6 Server Fleet Security & Audio Headless Null Stub (fmodex64.dll)
// ============================================================================

using System;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;
using HeadlessAudioStubService = WILLOWMAKER.Core.Services.HeadlessAudioStubService;

namespace WILLOWMAKER.Tests;

public sealed class HeadlessAudioStubTests
{
    [Fact]
    public void FindServerAudioTargets_DiscoversServerTargets_AndStrictlyExcludesClient()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        if (!NexusTestEnvironment.HasGameAssets || !Directory.Exists(Path.Combine(repoRoot, "games", "server")))
        {
            return;
        }

        var targets = HeadlessAudioStubService.FindServerAudioTargets(repoRoot);

        Assert.NotEmpty(targets);
        foreach (string target in targets)
        {
            Assert.Contains("server", target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client", target, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("fmodex64.dll", target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void HeadlessStub_SatisfiesAll90K2ImportsWithExportParity()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string k2Path = Path.Combine(repoRoot, "games", "server", "k2_x64.dll");
        string candidateStub = Path.Combine(repoRoot, "native", "build", "kongor_audio_stub_build", "Release", "fmodex64_null.dll");
        if (!File.Exists(candidateStub))
        {
            candidateStub = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "fmodex64_null.dll");
        }

        if (!File.Exists(k2Path) || !File.Exists(candidateStub))
        {
            // Skip in environments where binaries aren't checked out
            return;
        }

        var k2Imports = PeImportAddressTableService.GetImports(k2Path);
        var fmodImportModule = k2Imports.ImportedModules.FirstOrDefault(m => m.DllName.Equals("fmodex64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fmodImportModule);
        Assert.Equal(90, fmodImportModule.ImportedFunctions.Count);

        var stubExports = NativeBinaryManager.ExtractPeExportNames(candidateStub);
        Assert.Equal(90, stubExports.Count);

        // Verify 100% parity
        var missing = fmodImportModule.ImportedFunctions
            .Where(f => !stubExports.Contains(f, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void DeployAndRollback_InTempEnvironment_PerformsTwoTierBackupAndRestoration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_AudioStubTest_" + Guid.NewGuid().ToString("N"));
        string serverDir = Path.Combine(tempDir, "games", "server", "windows-x64");
        Directory.CreateDirectory(serverDir);

        try
        {
            string targetDll1 = Path.Combine(tempDir, "games", "server", "fmodex64.dll");
            string targetDll2 = Path.Combine(tempDir, "games", "server", "windows-x64", "fmodex64.dll");
            byte[] originalBytes = new byte[150000]; // simulate 150KB original binary
            Random.Shared.NextBytes(originalBytes);
            File.WriteAllBytes(targetDll1, originalBytes);
            File.WriteAllBytes(targetDll2, originalBytes);

            string repoRoot = NexusTestEnvironment.RepoRoot;
            string candidateStub = Path.Combine(repoRoot, "native", "build", "kongor_audio_stub_build", "Release", "fmodex64_null.dll");
        if (!File.Exists(candidateStub))
        {
            candidateStub = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "fmodex64_null.dll");
        }
            if (!File.Exists(candidateStub))
            {
                // Create mock stub
                candidateStub = Path.Combine(tempDir, "fmodex64_null.dll");
                File.WriteAllBytes(candidateStub, new byte[16896]);
            }

            // 1. Initial status - should NOT be headless
            var report1 = HeadlessAudioStubService.GetAudioStubStatus(tempDir);
            Assert.False(report1.IsFleetHeadless);
            Assert.Equal(2, report1.Targets.Count);
            Assert.All(report1.Targets, t =>
            {
                Assert.False(t.IsHeadlessStub);
                Assert.False(t.HasBackup);
            });

            // 2. Deploy Headless Stub
            var deployResult = HeadlessAudioStubService.DeployHeadlessStub(tempDir, candidateStub, skipProcessCheck: true);
            Assert.True(deployResult.Success);
            Assert.Equal(2, deployResult.DeployedPaths.Count);
            Assert.True(File.Exists(targetDll1 + ".bak"));
            Assert.True(File.Exists(targetDll2 + ".bak"));

            // 3. Status after deploy - should be headless
            var report2 = HeadlessAudioStubService.GetAudioStubStatus(tempDir);
            Assert.True(report2.IsFleetHeadless);
            Assert.All(report2.Targets, t =>
            {
                Assert.True(t.IsHeadlessStub);
                Assert.True(t.HasBackup);
                Assert.False(t.HasBackupOld);
            });

            // 4. Second deploy - should rotate .bak -> .bak.old
            var deployResult2 = HeadlessAudioStubService.DeployHeadlessStub(tempDir, candidateStub, skipProcessCheck: true);
            Assert.True(deployResult2.Success);
            Assert.True(File.Exists(targetDll1 + ".bak.old"));
            Assert.True(File.Exists(targetDll2 + ".bak.old"));

            var report3 = HeadlessAudioStubService.GetAudioStubStatus(tempDir);
            Assert.All(report3.Targets, t => Assert.True(t.HasBackupOld));

            // 5. Rollback to previous tier (.bak)
            var rollbackResult = HeadlessAudioStubService.RollbackHeadlessStub(tempDir, "previous", skipProcessCheck: true);
            Assert.True(rollbackResult.Success);
            Assert.Equal(2, rollbackResult.RestoredPaths.Count);

            // 6. Rollback to old tier (.bak.old)
            var rollbackOldResult = HeadlessAudioStubService.RollbackHeadlessStub(tempDir, "old", skipProcessCheck: true);
            Assert.True(rollbackOldResult.Success);

            byte[] restoredBytes1 = File.ReadAllBytes(targetDll1);
            Assert.Equal(originalBytes.Length, restoredBytes1.Length);
            Assert.Equal(originalBytes, restoredBytes1);

            byte[] restoredBytes2 = File.ReadAllBytes(targetDll2);
            Assert.Equal(originalBytes.Length, restoredBytes2.Length);
            Assert.Equal(originalBytes, restoredBytes2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
