// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessVidPurgeTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 10 Dedicated Server Graphics Decoupling & Server Bloat Purge
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Kongor.Core.Services.Native;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class HeadlessVidPurgeTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "server")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    [Fact]
    public void FindServerVidTargets_DiscoversServerTargets_AndStrictlyExcludesClient()
    {
        string repoRoot = FindRepoRoot();
        var targets = HeadlessVidService.FindServerVidTargets(repoRoot);

        foreach (string target in targets)
        {
            Assert.Contains("server", target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client", target, StringComparison.OrdinalIgnoreCase);
            string fileName = Path.GetFileName(target);
            Assert.Contains(fileName, HeadlessVidService.VideoBloatFilenames, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void IsServerSafeTarget_StrictlyRejectsClientPaths()
    {
        // Must reject client paths unconditionally
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\client\vid_d3d9_x64.dll"));
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\client\windows-x64\d3d9.dll"));
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"/opt/kongor/games/client/dxvk.conf"));
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\client\vid_gl2_x64.dll"));

        // Must reject non-video files even in server
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\hon_x64.exe"));
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\k2_x64.dll"));
        Assert.False(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\fmodex64.dll"));

        // Must accept valid server video bloat paths
        Assert.True(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\vid_d3d9_x64.dll"));
        Assert.True(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\windows-x64\d3d9.dll"));
        Assert.True(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\windows-x64\vid_gl2_x64.dll"));
        Assert.True(HeadlessVidService.IsServerSafeTarget(@"C:\ProjectKongor\games\server\dxvk.conf"));
    }

    [Fact]
    public void PurgeAndRollback_InTempEnvironment_PerformsTwoTierBackupAndRestoration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_VidPurgeTest_" + Guid.NewGuid().ToString("N"));
        string serverRoot = Path.Combine(tempDir, "games", "server");
        string serverWin64 = Path.Combine(serverRoot, "windows-x64");
        Directory.CreateDirectory(serverWin64);

        try
        {
            // Create mock files
            string target1 = Path.Combine(serverRoot, "vid_d3d9_x64.dll");
            string target2 = Path.Combine(serverWin64, "d3d9.dll");
            string target3 = Path.Combine(serverRoot, "dxvk.conf");

            byte[] contentV1 = [1, 2, 3, 4, 5, 6, 7, 8];
            File.WriteAllBytes(target1, contentV1);
            File.WriteAllBytes(target2, contentV1);
            File.WriteAllText(target3, "# dxvk conf v1");

            // Initial status: all active
            var initialReport = HeadlessVidService.GetStatusReport(tempDir);
            Assert.False(initialReport.IsFleetPurged);
            Assert.True(initialReport.TotalActiveBytes > 0);

            // Run first purge (Tier 1: active -> .bak)
            var purgeResult1 = HeadlessVidService.PurgeServerVideoBloat(tempDir, skipProcessCheck: true);
            Assert.True(purgeResult1.Success);
            Assert.True(purgeResult1.BytesFreed > 0);

            // Verify active files removed and .bak files exist
            Assert.False(File.Exists(target1));
            Assert.True(File.Exists(target1 + ".bak"));
            Assert.Equal(contentV1, File.ReadAllBytes(target1 + ".bak"));

            Assert.False(File.Exists(target2));
            Assert.True(File.Exists(target2 + ".bak"));

            Assert.False(File.Exists(target3));
            Assert.True(File.Exists(target3 + ".bak"));

            // Verify status report reflects purged state
            var purgedReport = HeadlessVidService.GetStatusReport(tempDir);
            Assert.True(purgedReport.IsFleetPurged);
            Assert.Equal(0, purgedReport.TotalActiveBytes);
            Assert.True(purgedReport.TotalSavedBytes > 0);

            // Recreate active files with V2 content to test Tier 2 rotation (.bak -> .bak.old)
            byte[] contentV2 = [9, 10, 11, 12, 13, 14, 15, 16];
            File.WriteAllBytes(target1, contentV2);

            var purgeResult2 = HeadlessVidService.PurgeServerVideoBloat(tempDir, skipProcessCheck: true);
            Assert.True(purgeResult2.Success);
            Assert.Contains(target1 + ".bak.old", purgeResult2.RotatedBakOldPaths);

            // Verify rotation: .bak has V2 content, .bak.old has V1 content
            Assert.True(File.Exists(target1 + ".bak"));
            Assert.Equal(contentV2, File.ReadAllBytes(target1 + ".bak"));
            Assert.True(File.Exists(target1 + ".bak.old"));
            Assert.Equal(contentV1, File.ReadAllBytes(target1 + ".bak.old"));

            // Test Rollback to "previous" (.bak -> active)
            var rollbackPrevious = HeadlessVidService.RollbackServerVideoBloat(tempDir, targetTier: "previous", skipProcessCheck: true);
            Assert.True(rollbackPrevious.Success);
            Assert.True(File.Exists(target1));
            Assert.Equal(contentV2, File.ReadAllBytes(target1));

            // Test Rollback to "old" (.bak.old -> active)
            var rollbackOld = HeadlessVidService.RollbackServerVideoBloat(tempDir, targetTier: "old", skipProcessCheck: true);
            Assert.True(rollbackOld.Success);
            Assert.True(File.Exists(target1));
            Assert.Equal(contentV1, File.ReadAllBytes(target1));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
