// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RawInputModernizationTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Track 2 High-Polling-Rate Esports Mouse Modernization (Windows Raw Input 8000Hz)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;
using RawInputModernizationService = WILLOWMAKER.Core.Services.RawInputModernizationService;

namespace WILLOWMAKER.Tests;

public sealed class RawInputModernizationTests
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
        IReadOnlyList<string> dirs = RawInputModernizationService.FindClientDirectories(repoRoot);

        Assert.NotEmpty(dirs);
        foreach (string d in dirs)
        {
            Assert.Contains("client", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("server", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dedicated", d, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("c:/ProjectKongor/games/server/version.dll", false)]
    [InlineData("c:/ProjectKongor/games/server/windows-x64/version.dll", false)]
    [InlineData("c:/ProjectKongor/dedicated/version.dll", false)]
    [InlineData("c:/ProjectKongor/games/client/version.dll", true)]
    [InlineData("c:/ProjectKongor/games/client/windows-x64/version.dll", true)]
    [InlineData("", false)]
    public void IsClientSafeTarget_EnforcesStrictIsolation(string path, bool expectedSafe)
    {
        bool actualSafe = RawInputModernizationService.IsClientSafeTarget(path);
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
        bool actualSafe = RawInputModernizationService.IsClientSafeDirectory(path);
        Assert.Equal(expectedSafe, actualSafe);
    }

    [Fact]
    public void DeployAndRollback_InTempEnvironment_PerformsAtomicTwoTierBackups()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_RawInputTest_" + Guid.NewGuid().ToString("N"));
        string clientDir1 = Path.Combine(tempDir, "games", "client");
        string clientDir2 = Path.Combine(tempDir, "games", "client", "windows-x64");
        string sourceDir = Path.Combine(tempDir, "source");

        Directory.CreateDirectory(clientDir1);
        Directory.CreateDirectory(clientDir2);
        Directory.CreateDirectory(sourceDir);

        try
        {
            // Seed mock source version.dll binary
            string sourceFile = Path.Combine(sourceDir, "version.dll");
            File.WriteAllBytes(sourceFile, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // MZ header

            // 1. Initial status - unmodernized
            RawInputReport report1 = RawInputModernizationService.GetRawInputStatus(tempDir);
            Assert.False(report1.IsModernized);
            Assert.Equal(2, report1.Targets.Count);

            // Seed an original stock version.dll so we can test .bak creation
            byte[] originalBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
            File.WriteAllBytes(Path.Combine(clientDir1, "version.dll"), originalBytes);
            File.WriteAllBytes(Path.Combine(clientDir2, "version.dll"), originalBytes);

            // 2. Deploy First Generation
            RawInputDeployResult deploy1 = RawInputModernizationService.DeployRawInput(
                tempDir, sourceDir, skipProcessCheck: true);
            Assert.True(deploy1.Success);
            Assert.Equal(2, deploy1.DeployedPaths.Count);
            Assert.Equal(2, deploy1.RotatedBakPaths.Count);
            Assert.Empty(deploy1.RotatedBakOldPaths);

            // Verify .bak exists and contains originalBytes
            string bakPath = Path.Combine(clientDir1, "version.dll.bak");
            Assert.True(File.Exists(bakPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(bakPath));

            // 3. Deploy Second Generation (triggers .bak -> .bak.old rotation)
            byte[] v2Bytes = new byte[] { 0x55, 0x66, 0x77, 0x88 };
            File.WriteAllBytes(sourceFile, v2Bytes);

            RawInputDeployResult deploy2 = RawInputModernizationService.DeployRawInput(
                tempDir, sourceDir, skipProcessCheck: true);
            Assert.True(deploy2.Success);
            Assert.Equal(2, deploy2.RotatedBakPaths.Count);
            Assert.Equal(2, deploy2.RotatedBakOldPaths.Count);

            string bakOldPath = Path.Combine(clientDir1, "version.dll.bak.old");
            Assert.True(File.Exists(bakOldPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(bakOldPath));

            // 4. Rollback to tier "previous" (.bak)
            RawInputRollbackResult rbPrevious = RawInputModernizationService.RollbackRawInput(
                tempDir, "previous", skipProcessCheck: true);
            Assert.True(rbPrevious.Success);
            Assert.Equal(2, rbPrevious.RestoredPaths.Count);

            // 5. Rollback to tier "old" (.bak.old)
            RawInputRollbackResult rbOld = RawInputModernizationService.RollbackRawInput(
                tempDir, "old", skipProcessCheck: true);
            Assert.True(rbOld.Success);
            Assert.Equal(2, rbOld.RestoredPaths.Count);
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(clientDir1, "version.dll")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort cleanup */ }
            }
        }
    }

    [Fact]
    public void ConfigureStartupCfg_InTempEnvironment_WritesAndReadsCvars()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_RawInputCfgTest_" + Guid.NewGuid().ToString("N"));
        string baseDir = Path.Combine(tempDir, "games", "client", "base");
        Directory.CreateDirectory(baseDir);

        string cfgPath = Path.Combine(baseDir, "startup.cfg");
        File.WriteAllLines(cfgPath, new string[]
        {
            "SetSave host_enableMasterServer true",
            "SetSave vid_resolution \"1920,1080\""
        });

        try
        {
            RawInputCvarResult result = RawInputModernizationService.ConfigureStartupCfg(
                tempDir, enableRawInput: true, sensitivity: 1.5f);

            Assert.True(result.Success);
            Assert.Single(result.ConfigFilesUpdated);

            string[] updatedLines = File.ReadAllLines(cfgPath);
            Assert.Contains(updatedLines, l => l.Contains("in_rawInput true", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(updatedLines, l => l.Contains("m_rawSensitivity 1.5000", StringComparison.OrdinalIgnoreCase));

            RawInputReport report = RawInputModernizationService.GetRawInputStatus(tempDir);
            Assert.True(report.CvarRawInputEnabled);
            Assert.Equal(1.5f, report.MouseSensitivity);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort cleanup */ }
            }
        }
    }
}
