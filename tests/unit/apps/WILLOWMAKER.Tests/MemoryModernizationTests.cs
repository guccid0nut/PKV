// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MemoryModernizationTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Track 1 Client Memory Allocator Modernization (Microsoft mimalloc)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;
using MemoryModernizationService = WILLOWMAKER.Core.Services.MemoryModernizationService;

namespace WILLOWMAKER.Tests;

public sealed class MemoryModernizationTests
{
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
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
    public void FindClientDirectories_DiscoversClientTargets_AndStrictlyExcludesServer()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = FindRepoRoot();
        IReadOnlyList<string> dirs = MemoryModernizationService.FindClientDirectories(repoRoot);

        Assert.NotEmpty(dirs);
        foreach (string d in dirs)
        {
            Assert.Contains("client", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("server", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dedicated", d, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("c:/ProjectKongor/games/server/mimalloc.dll", false)]
    [InlineData("c:/ProjectKongor/games/server/windows-x64/mimalloc.dll", false)]
    [InlineData("c:/ProjectKongor/dedicated/mimalloc.dll", false)]
    [InlineData("c:/ProjectKongor/games/client/mimalloc.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/mimalloc-override.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/mimalloc-redirect.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/version.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/k2_x64.dll", false)]
    [InlineData("", false)]
    public void IsClientSafeTarget_EnforcesStrictIsolation(string path, bool expectedSafe)
    {
        bool actualSafe = MemoryModernizationService.IsClientSafeTarget(path);
        Assert.Equal(expectedSafe, actualSafe);
    }

    [Theory]
    [InlineData("c:/ProjectKongor/games/server", false)]
    [InlineData("c:/ProjectKongor/dedicated", false)]
    [InlineData("c:/ProjectKongor/games/client", true)]
    [InlineData("c:/ProjectKongor/games/client/windows-x64", true)]
    [InlineData("", false)]
    public void IsClientSafeDirectory_EnforcesStrictIsolation(string path, bool expectedSafe)
    {
        bool actualSafe = MemoryModernizationService.IsClientSafeDirectory(path);
        Assert.Equal(expectedSafe, actualSafe);
    }

    [Fact]
    public void DeployAndRollback_InTempEnvironment_PerformsAtomicTwoTierBackups()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_MemoryTest_" + Guid.NewGuid().ToString("N"));
        string clientDir1 = Path.Combine(tempDir, "games", "client");
        string clientDir2 = Path.Combine(tempDir, "games", "client", "windows-x64");
        string sourceDir = Path.Combine(tempDir, "source");

        Directory.CreateDirectory(clientDir1);
        Directory.CreateDirectory(clientDir2);
        Directory.CreateDirectory(sourceDir);

        try
        {
            // Seed mock source mimalloc binaries
            foreach (string binName in Kongor.Core.Services.Native.MemoryModernizationService.RequiredMimallocBinaries)
            {
                File.WriteAllBytes(Path.Combine(sourceDir, binName), new byte[1024]);
            }

            // 1. Initial status - unmodernized
            ClientMemoryReport report1 = MemoryModernizationService.GetMemoryStatus(tempDir);
            Assert.False(report1.IsModernized);
            Assert.Equal("Stock Windows UCRT Heap", report1.MemoryBackend);

            // 2. Deploy mimalloc
            MemoryDeployResult deployRes = MemoryModernizationService.DeployMimalloc(tempDir, sourceDir, skipProcessCheck: true);
            Assert.True(deployRes.Success);
            Assert.Equal(6, deployRes.DeployedPaths.Count); // 3 binaries x 2 directories

            // 3. Status after deploy - modernized
            ClientMemoryReport report2 = MemoryModernizationService.GetMemoryStatus(tempDir);
            Assert.True(report2.IsModernized);
            Assert.Equal("Microsoft mimalloc (v2.3.2)", report2.MemoryBackend);

            // 4. Deploy again with different content to test .bak and .bak.old rotation
            foreach (string binName in Kongor.Core.Services.Native.MemoryModernizationService.RequiredMimallocBinaries)
            {
                File.WriteAllBytes(Path.Combine(sourceDir, binName), new byte[2048]);
            }

            MemoryDeployResult deployRes2 = MemoryModernizationService.DeployMimalloc(tempDir, sourceDir, skipProcessCheck: true);
            Assert.True(deployRes2.Success);
            Assert.Equal(6, deployRes2.RotatedBakPaths.Count);

            // Verify .bak files exist and are 1024 bytes
            string sampleBak = Path.Combine(clientDir1, "mimalloc.dll.bak");
            Assert.True(File.Exists(sampleBak));
            Assert.Equal(1024, new FileInfo(sampleBak).Length);

            // 5. Rollback to previous tier (.bak)
            MemoryRollbackResult rollbackRes = MemoryModernizationService.RollbackMimalloc(tempDir, "previous", skipProcessCheck: true);
            Assert.True(rollbackRes.Success);

            // Verified restored file is 1024 bytes
            string sampleRestored = Path.Combine(clientDir1, "mimalloc.dll");
            Assert.Equal(1024, new FileInfo(sampleRestored).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
