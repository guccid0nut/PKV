// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CefModernizationTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 11 Client CEF Retirement & RmlUi Modern HUD Overlay
// ============================================================================

using System;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;
using CefModernizationService = WILLOWMAKER.Core.Services.CefModernizationService;

namespace WILLOWMAKER.Tests;

public sealed class CefModernizationTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "games", "client")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    [Fact]
    public void FindCefTargets_DiscoversCefDirectory_AndExcludesOtherModules()
    {
        string repoRoot = FindRepoRoot();
        var targets = CefModernizationService.FindCefTargets(repoRoot);

        string? cleanupDir = null;
        if (targets.Count == 0 || !targets.Any(t => t.EndsWith("libcef.dll", StringComparison.OrdinalIgnoreCase)))
        {
            // In clean git checkouts or environments where large/backup binaries are excluded by .gitignore,
            // construct a sandbox directory to test discovery and exclusion semantics.
            cleanupDir = Path.Combine(Path.GetTempPath(), "Kongor_CefTargetTest_" + Guid.NewGuid().ToString("N"));
            string cefDir = Path.Combine(cleanupDir, "games", "client", "cef");
            Directory.CreateDirectory(cefDir);
            File.WriteAllBytes(Path.Combine(cefDir, "libcef.dll"), new byte[64]);
            File.WriteAllBytes(Path.Combine(cleanupDir, "games", "client", "chrome_elf.dll"), new byte[64]);
            targets = CefModernizationService.FindCefTargets(cleanupDir);
        }

        try
        {
            Assert.NotEmpty(targets);
            Assert.Contains(targets, t => t.EndsWith("libcef.dll", StringComparison.OrdinalIgnoreCase));

            foreach (string target in targets)
            {
                Assert.True(
                    target.Contains("cef", StringComparison.OrdinalIgnoreCase) ||
                    target.EndsWith("chrome_elf.dll", StringComparison.OrdinalIgnoreCase),
                    $"Target '{target}' should be inside a cef directory or be chrome_elf.dll");
            }
        }
        finally
        {
            if (cleanupDir != null && Directory.Exists(cleanupDir))
            {
                Directory.Delete(cleanupDir, true);
            }
        }
    }

    [Fact]
    public void StatusReport_InTempEnvironment_AccuratelyIdentifiesActiveVsNeutralizedState()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_CefStatusTest_" + Guid.NewGuid().ToString("N"));
        string cefDir = Path.Combine(tempDir, "games", "client", "cef");
        Directory.CreateDirectory(cefDir);

        try
        {
            string libCefPath = Path.Combine(cefDir, "libcef.dll");
            string cefPakPath = Path.Combine(cefDir, "cef.pak");
            File.WriteAllBytes(libCefPath, new byte[1024]);
            File.WriteAllBytes(cefPakPath, new byte[512]);

            // 1. Initially Active
            var report1 = CefModernizationService.GetStatus(tempDir);
            Assert.True(report1.IsActive);
            Assert.False(report1.IsDeactivated);
            Assert.Equal(2, report1.ActiveFileCount);
            Assert.Equal(0, report1.DeactivatedFileCount);
            Assert.Equal(1536, report1.TotalActiveSizeBytes);
            Assert.Equal(0, report1.EstimatedRamSavingsBytes);

            // 2. Deactivate
            var deactResult = CefModernizationService.Deactivate(tempDir, skipProcessCheck: true);
            Assert.True(deactResult.Success);
            Assert.Equal(2, deactResult.DeactivatedCount);

            // 3. Status should now be Deactivated
            var report2 = CefModernizationService.GetStatus(tempDir);
            Assert.False(report2.IsActive);
            Assert.True(report2.IsDeactivated);
            Assert.Equal(0, report2.ActiveFileCount);
            Assert.Equal(2, report2.DeactivatedFileCount);
            Assert.Equal(1536, report2.TotalReclaimedSizeBytes);
            Assert.True(report2.EstimatedRamSavingsBytes > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void DeactivateAndRollback_InTempEnvironment_PerformsTwoTierBackupAndRestoration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_CefRotationTest_" + Guid.NewGuid().ToString("N"));
        string cefDir = Path.Combine(tempDir, "games", "client", "cef");
        Directory.CreateDirectory(cefDir);

        try
        {
            string libCefPath = Path.Combine(cefDir, "libcef.dll");
            string icuPath = Path.Combine(cefDir, "icudtl.dat");

            byte[] originalLibCef = new byte[2048];
            Random.Shared.NextBytes(originalLibCef);
            File.WriteAllBytes(libCefPath, originalLibCef);
            File.WriteAllBytes(icuPath, new byte[1024]);

            // 1. First Deactivation -> creates .bak
            var deact1 = CefModernizationService.Deactivate(tempDir, skipProcessCheck: true);
            Assert.True(deact1.Success);
            Assert.Equal(2, deact1.DeactivatedCount);

            Assert.False(File.Exists(libCefPath));
            Assert.True(File.Exists(libCefPath + ".bak"));
            Assert.False(File.Exists(libCefPath + ".bak.old"));
            Assert.Equal(originalLibCef, File.ReadAllBytes(libCefPath + ".bak"));

            // 2. Simulate new modified active file then deactivate again -> rotates .bak to .bak.old
            byte[] updatedLibCef = new byte[4096];
            Random.Shared.NextBytes(updatedLibCef);
            File.WriteAllBytes(libCefPath, updatedLibCef);

            var deact2 = CefModernizationService.Deactivate(tempDir, skipProcessCheck: true);
            Assert.True(deact2.Success);
            Assert.Single(deact2.RotatedBakOldPaths);

            Assert.False(File.Exists(libCefPath));
            Assert.True(File.Exists(libCefPath + ".bak"));
            Assert.True(File.Exists(libCefPath + ".bak.old"));
            Assert.Equal(updatedLibCef, File.ReadAllBytes(libCefPath + ".bak"));
            Assert.Equal(originalLibCef, File.ReadAllBytes(libCefPath + ".bak.old"));

            // 3. Rollback from tier 'previous' -> restores updatedLibCef
            var rollPrev = CefModernizationService.Rollback(tempDir, targetTier: "previous", skipProcessCheck: true);
            Assert.True(rollPrev.Success);
            Assert.True(File.Exists(libCefPath));
            Assert.Equal(updatedLibCef, File.ReadAllBytes(libCefPath));

            // 4. Rollback from tier 'old' / baseline -> restores originalLibCef
            var rollOld = CefModernizationService.Rollback(tempDir, targetTier: "old", skipProcessCheck: true);
            Assert.True(rollOld.Success);
            Assert.True(File.Exists(libCefPath));
            Assert.Equal(originalLibCef, File.ReadAllBytes(libCefPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Deactivate_WhenAlreadyDeactivated_IsIdempotentAndSafe()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_CefIdempotentTest_" + Guid.NewGuid().ToString("N"));
        string cefDir = Path.Combine(tempDir, "games", "client", "cef");
        Directory.CreateDirectory(cefDir);

        try
        {
            string libCefPath = Path.Combine(cefDir, "libcef.dll");
            File.WriteAllBytes(libCefPath, new byte[512]);

            // First deactivation
            var res1 = CefModernizationService.Deactivate(tempDir, skipProcessCheck: true);
            Assert.True(res1.Success);
            Assert.Equal(1, res1.DeactivatedCount);

            // Second deactivation should be a safe no-op
            var res2 = CefModernizationService.Deactivate(tempDir, skipProcessCheck: true);
            Assert.True(res2.Success);
            Assert.Equal(0, res2.DeactivatedCount);
            Assert.Contains("already deactivated", res2.Message, StringComparison.OrdinalIgnoreCase);
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
