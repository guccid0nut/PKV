// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientAudioTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 8 Client Low-Latency WASAPI & 3D Spatial HRTF Audio (fmodex64.dll)
// ============================================================================

using System;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;
using ClientAudioModernizationService = WILLOWMAKER.Core.Services.ClientAudioModernizationService;

namespace WILLOWMAKER.Tests;

public sealed class ClientAudioTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "client")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    [Fact]
    public void FindClientAudioTargets_DiscoversClientTargets_AndStrictlyExcludesServer()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = FindRepoRoot();
        var targets = ClientAudioModernizationService.FindClientAudioTargets(repoRoot);

        Assert.NotEmpty(targets);
        foreach (string target in targets)
        {
            Assert.Contains("client", target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("server", target, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("fmodex64.dll", target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("c:/ProjectKongor/games/server/fmodex64.dll", false)]
    [InlineData("c:/ProjectKongor/games/server/windows-x64/fmodex64.dll", false)]
    [InlineData("c:/ProjectKongor/dedicated/fmodex64.dll", false)]
    [InlineData("c:/ProjectKongor/games/client/fmodex64.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/windows-x64/fmodex64.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/k2_x64.dll", false)]
    [InlineData("", false)]
    public void IsClientSafeTarget_EnforcesStrictIsolation(string path, bool expectedSafe)
    {
        bool actualSafe = ClientAudioModernizationService.IsClientSafeTarget(path);
        Assert.Equal(expectedSafe, actualSafe);
    }

    [Fact]
    public void ClientProxy_SatisfiesAll90K2ImportsWithExportParity()
    {
        string repoRoot = FindRepoRoot();
        string k2Path = Path.Combine(repoRoot, "games", "client", "k2_x64.dll");
        string candidateProxy = Path.Combine(repoRoot, "native", "build", "kongor_audio_build", "Release", "fmodex64.dll");
        if (!File.Exists(candidateProxy))
        {
            candidateProxy = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "fmodex64_spatial.dll");
        }

        if (!File.Exists(k2Path) || !File.Exists(candidateProxy))
        {
            // Skip in environments where binaries aren't checked out
            return;
        }

        var k2Imports = PeImportAddressTableService.GetImports(k2Path);
        var fmodImportModule = k2Imports.ImportedModules.FirstOrDefault(m => m.DllName.Equals("fmodex64.dll", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fmodImportModule);
        Assert.Equal(90, fmodImportModule.ImportedFunctions.Count);

        var proxyExports = NativeBinaryManager.ExtractPeExportNames(candidateProxy);
        Assert.True(proxyExports.Count >= 90);

        // Verify 100% parity: every symbol imported by k2_x64.dll must be exported by the proxy
        var missing = fmodImportModule.ImportedFunctions
            .Where(f => !proxyExports.Contains(f, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void DeployAndRollback_InTempEnvironment_PreservesCoreEngineAndPerformsTwoTierBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_ClientAudioTest_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client", "windows-x64");
        Directory.CreateDirectory(clientDir);

        try
        {
            string targetDll1 = Path.Combine(tempDir, "games", "client", "fmodex64.dll");
            string targetDll2 = Path.Combine(tempDir, "games", "client", "windows-x64", "fmodex64.dll");

            // Simulate 1.5MB genuine FMOD Ex engine
            byte[] genuineEngineBytes = new byte[1500000];
            Random.Shared.NextBytes(genuineEngineBytes);
            File.WriteAllBytes(targetDll1, genuineEngineBytes);
            File.WriteAllBytes(targetDll2, genuineEngineBytes);

            string repoRoot = FindRepoRoot();
            string candidateProxy = Path.Combine(repoRoot, "native", "build", "kongor_audio_build", "Release", "fmodex64.dll");
        if (!File.Exists(candidateProxy))
        {
            candidateProxy = Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "fmodex64_spatial.dll");
        }
            if (!File.Exists(candidateProxy))
            {
                candidateProxy = Path.Combine(tempDir, "fmodex64_spatial.dll");
                File.WriteAllBytes(candidateProxy, new byte[29696]); // ~30KB proxy
            }

            // 1. Initial status - vanilla DirectSound
            var report1 = ClientAudioModernizationService.GetClientAudioStatus(tempDir);
            Assert.False(report1.IsFleetModernized);
            Assert.Equal(2, report1.Targets.Count);
            Assert.Equal(85.33f, report1.MeasuredLatencyMs);
            Assert.All(report1.Targets, t =>
            {
                Assert.False(t.IsWasapiProxy);
                Assert.False(t.HasCoreEngine);
                Assert.False(t.HasBackup);
            });

            // 2. Deploy Client Audio Proxy
            var deployResult = ClientAudioModernizationService.DeployClientAudioProxy(tempDir, candidateProxy, skipProcessCheck: true);
            Assert.True(deployResult.Success);
            Assert.Equal(2, deployResult.DeployedPaths.Count);
            Assert.Equal(2, deployResult.CorePreservedPaths.Count);

            // Verify genuine engine was preserved as fmodex64_core.dll
            string coreDll1 = Path.Combine(tempDir, "games", "client", "fmodex64_core.dll");
            string coreDll2 = Path.Combine(tempDir, "games", "client", "windows-x64", "fmodex64_core.dll");
            Assert.True(File.Exists(coreDll1));
            Assert.True(File.Exists(coreDll2));
            Assert.Equal(genuineEngineBytes.Length, new FileInfo(coreDll1).Length);
            Assert.Equal(genuineEngineBytes.Length, new FileInfo(coreDll2).Length);

            // Verify .bak exists
            Assert.True(File.Exists(targetDll1 + ".bak"));
            Assert.True(File.Exists(targetDll2 + ".bak"));

            // 3. Status after deploy - WASAPI & HRTF active
            var report2 = ClientAudioModernizationService.GetClientAudioStatus(tempDir);
            Assert.True(report2.IsFleetModernized);
            Assert.Equal(10.66f, report2.MeasuredLatencyMs);
            Assert.True(report2.LatencyReductionPercent > 80f);
            Assert.All(report2.Targets, t =>
            {
                Assert.True(t.IsWasapiProxy);
                Assert.True(t.HasCoreEngine);
                Assert.True(t.HasBackup);
                Assert.False(t.HasBackupOld);
            });

            // 4. Second deploy - should rotate .bak -> .bak.old
            var deployResult2 = ClientAudioModernizationService.DeployClientAudioProxy(tempDir, candidateProxy, skipProcessCheck: true);
            Assert.True(deployResult2.Success);
            Assert.True(File.Exists(targetDll1 + ".bak.old"));
            Assert.True(File.Exists(targetDll2 + ".bak.old"));

            var report3 = ClientAudioModernizationService.GetClientAudioStatus(tempDir);
            Assert.All(report3.Targets, t => Assert.True(t.HasBackupOld));

            // 5. Rollback to genuine engine
            var rollbackResult = ClientAudioModernizationService.RollbackClientAudioProxy(tempDir, skipProcessCheck: true);
            Assert.True(rollbackResult.Success);
            Assert.Equal(2, rollbackResult.RestoredPaths.Count);

            byte[] restoredBytes1 = File.ReadAllBytes(targetDll1);
            Assert.Equal(genuineEngineBytes.Length, restoredBytes1.Length);
            Assert.Equal(genuineEngineBytes, restoredBytes1);

            byte[] restoredBytes2 = File.ReadAllBytes(targetDll2);
            Assert.Equal(genuineEngineBytes.Length, restoredBytes2.Length);
            Assert.Equal(genuineEngineBytes, restoredBytes2);

            // Status after rollback
            var report4 = ClientAudioModernizationService.GetClientAudioStatus(tempDir);
            Assert.False(report4.IsFleetModernized);
            Assert.Equal(85.33f, report4.MeasuredLatencyMs);
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
