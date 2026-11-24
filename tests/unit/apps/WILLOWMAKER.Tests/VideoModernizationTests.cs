// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoModernizationTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 9 Hardware-Accelerated Video Decoding (FFmpeg 7.x / DXVA2)
// ============================================================================

using System;
using System.IO;
using System.Linq;
using Kongor.Core.Services.Native;
using Xunit;
using VideoModernizationService = WILLOWMAKER.Core.Services.VideoModernizationService;

namespace WILLOWMAKER.Tests;

public sealed class VideoModernizationTests
{
    [Fact]
    public void FindClientVideoTargets_DiscoversClientTargets_AndExcludesServer()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        var targets = VideoModernizationService.FindClientVideoTargets(repoRoot);

        Assert.NotEmpty(targets);
        foreach (string target in targets)
        {
            Assert.Contains("client", target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("server", target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FindServerVideoTargets_DiscoversServerTargets_AndExcludesClient()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        if (!NexusTestEnvironment.HasGameAssets || !Directory.Exists(Path.Combine(repoRoot, "games", "server")))
        {
            return;
        }

        var targets = VideoModernizationService.FindServerVideoTargets(repoRoot);

        Assert.NotEmpty(targets);
        foreach (string target in targets)
        {
            Assert.Contains("server", target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client", target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void VideoProxy_SatisfiesAll238FFmpegExportsWithZeroMissingSymbols()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string genuineAvcodec = Path.Combine(repoRoot, "games", "client", "windows-x64", "avcodec-x64-58.dll.bak");
        if (!File.Exists(genuineAvcodec))
        {
            genuineAvcodec = Path.Combine(repoRoot, "games", "client", "windows-x64", "avcodec-x64-58.dll");
        }

        string candidateProxy = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "video", "avcodec-x64-58.dll");

        if (!File.Exists(genuineAvcodec) || !File.Exists(candidateProxy))
        {
            return;
        }

        var realExports = NativeBinaryManager.ExtractPeExportNames(genuineAvcodec);
        var proxyExports = NativeBinaryManager.ExtractPeExportNames(candidateProxy);

        Assert.True(realExports.Count >= 238);
        Assert.True(proxyExports.Count >= 240); // 238 original + 2 telemetry exports

        var missing = realExports.Where(r => !proxyExports.Contains(r, StringComparer.Ordinal)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void VideoProxy_SatisfiesAll8K2UsedSymbols()
    {
        string repoRoot = NexusTestEnvironment.RepoRoot;
        string candidateProxy = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "video", "avcodec-x64-58.dll");

        if (!File.Exists(candidateProxy))
        {
            return;
        }

        var proxyExports = NativeBinaryManager.ExtractPeExportNames(candidateProxy);

        string[] requiredK2Symbols =
        [
            "av_packet_unref",
            "avcodec_alloc_context3",
            "avcodec_flush_buffers",
            "avcodec_free_context",
            "avcodec_open2",
            "avcodec_parameters_to_context",
            "avcodec_receive_frame",
            "avcodec_send_packet"
        ];

        foreach (string sym in requiredK2Symbols)
        {
            Assert.Contains(sym, proxyExports);
        }
    }

    [Fact]
    public void LoginScreenAsset_RemainsUntouchedAndPreservedInvariant()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string loginBak = Path.Combine(repoRoot, "games", "client", "game", "videos", "login_1080.avi.bak");
        string loginEvictedBak = Path.Combine(repoRoot, "games", ".asset_manifest", "evicted_backup_step12", "override_bak", "videos", "login_1080.avi.bak");
        string r99Archive = Path.Combine(repoRoot, "games", "client", "game", "resources99.s2z");
        string loginActive = Path.Combine(repoRoot, "games", "client", "game", "videos", "login_1080.avi");

        // CRITICAL INVARIANT: The user explicitly specified never to touch the login screen.
        // login_1080.avi.bak must remain strictly intact (either locally, in evicted_backup_step12, or consolidated in resources99.s2z).
        Assert.True(
            File.Exists(loginBak) || File.Exists(loginEvictedBak) || File.Exists(r99Archive),
            "login_1080.avi.bak must exist as a preserved backup or be consolidated.");
        Assert.False(File.Exists(loginActive), "login_1080.avi must not be reactivated, preserving clean static login.");
    }

    [Fact]
    public void PurgeAndRestoreServerVideo_InTempEnvironment_FreesStorageAndRestoresCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_ServerVideoTest_" + Guid.NewGuid().ToString("N"));
        string serverDir = Path.Combine(tempDir, "games", "server", "windows-x64");
        Directory.CreateDirectory(serverDir);

        try
        {
            string[] dllNames = ["avcodec-x64-58.dll", "avdevice-x64-58.dll", "avfilter-x64-7.dll", "avformat-x64-58.dll", "avutil-x64-56.dll"];
            long dummyTotal = 0;
            foreach (string dll in dllNames)
            {
                byte[] bytes = new byte[1024 * 1024]; // 1 MB each
                File.WriteAllBytes(Path.Combine(serverDir, dll), bytes);
                dummyTotal += bytes.Length;
            }

            // 1. Initial Status
            var report1 = VideoModernizationService.GetVideoStatus(tempDir);
            Assert.False(report1.IsServerPurged);
            Assert.Equal(dllNames.Length, report1.ServerTargets.Count);

            // 2. Purge Server Bloat
            var purgeResult = VideoModernizationService.PurgeServerVideoBloat(tempDir, skipProcessCheck: true);
            Assert.True(purgeResult.Success);
            Assert.Equal(dllNames.Length, purgeResult.PurgedPaths.Count);
            Assert.Equal(dummyTotal, purgeResult.BytesFreed);

            // Verify DLLs removed and .purged created
            foreach (string dll in dllNames)
            {
                Assert.False(File.Exists(Path.Combine(serverDir, dll)));
                Assert.True(File.Exists(Path.Combine(serverDir, dll + ".purged")));
            }

            var report2 = VideoModernizationService.GetVideoStatus(tempDir);
            Assert.True(report2.IsServerPurged);
            Assert.Equal(dummyTotal, report2.ServerSpaceSavedBytes);

            // 3. Restore Server
            var restoreResult = VideoModernizationService.RestoreServerVideo(tempDir, skipProcessCheck: true);
            Assert.True(restoreResult.Success);
            Assert.Equal(dllNames.Length, restoreResult.PurgedPaths.Count);

            foreach (string dll in dllNames)
            {
                Assert.True(File.Exists(Path.Combine(serverDir, dll)));
            }

            var report3 = VideoModernizationService.GetVideoStatus(tempDir);
            Assert.False(report3.IsServerPurged);
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
    public void DeployAndRollbackClientVideo_InTempEnvironment_PreservesCoreEngineAndPerformsTwoTierBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_ClientVideoTest_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client", "windows-x64");
        string stagedDir = Path.Combine(tempDir, "staged");
        Directory.CreateDirectory(clientDir);
        Directory.CreateDirectory(stagedDir);

        try
        {
            string avcodecPath = Path.Combine(clientDir, "avcodec-x64-58.dll");
            string avfilterPath = Path.Combine(clientDir, "avfilter-x64-7.dll");
            string avdevicePath = Path.Combine(clientDir, "avdevice-x64-58.dll");

            // Simulate heavy genuine FFmpeg DLLs (2 MB each)
            byte[] genuineAvcodecBytes = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(genuineAvcodecBytes);
            File.WriteAllBytes(avcodecPath, genuineAvcodecBytes);
            File.WriteAllBytes(avfilterPath, new byte[2 * 1024 * 1024]);
            File.WriteAllBytes(avdevicePath, new byte[1024 * 1024]);

            // Staged shims (~30 KB and ~10 KB)
            byte[] shimAvcodecBytes = new byte[30000];
            Random.Shared.NextBytes(shimAvcodecBytes);
            File.WriteAllBytes(Path.Combine(stagedDir, "avcodec-x64-58.dll"), shimAvcodecBytes);
            File.WriteAllBytes(Path.Combine(stagedDir, "avfilter-x64-7.dll"), new byte[9216]);
            File.WriteAllBytes(Path.Combine(stagedDir, "avdevice-x64-58.dll"), new byte[9216]);

            // 1. Initial Status
            var report1 = VideoModernizationService.GetVideoStatus(tempDir);
            Assert.False(report1.IsClientModernized);

            // 2. Deploy Client Upgrade
            var deployResult = VideoModernizationService.DeployClientVideoUpgrade(tempDir, stagedDir, skipProcessCheck: true);
            Assert.True(deployResult.Success);
            Assert.Equal(3, deployResult.DeployedPaths.Count);
            Assert.Single(deployResult.PreservedCorePaths);

            // Verify genuine avcodec preserved as avcodec_core.dll
            string corePath = Path.Combine(clientDir, "avcodec_core.dll");
            Assert.True(File.Exists(corePath));
            Assert.Equal(genuineAvcodecBytes.Length, new FileInfo(corePath).Length);
            Assert.Equal(genuineAvcodecBytes, File.ReadAllBytes(corePath));

            // Verify .bak created
            Assert.True(File.Exists(avcodecPath + ".bak"));

            // Verify active DLL is the shim
            Assert.Equal(shimAvcodecBytes.Length, new FileInfo(avcodecPath).Length);

            var report2 = VideoModernizationService.GetVideoStatus(tempDir);
            Assert.True(report2.IsClientModernized);
            Assert.Contains("Direct3D 11", report2.HardwareAccelerationBackend);

            // 3. Second deploy - verify rolling backup .bak -> .bak.old
            var deployResult2 = VideoModernizationService.DeployClientVideoUpgrade(tempDir, stagedDir, skipProcessCheck: true);
            Assert.True(deployResult2.Success);
            Assert.True(File.Exists(avcodecPath + ".bak.old"));

            // 4. Rollback to vanilla FFmpeg
            var rollbackResult = VideoModernizationService.RollbackClientVideo(tempDir, skipProcessCheck: true);
            Assert.True(rollbackResult.Success);
            Assert.Equal(3, rollbackResult.RestoredPaths.Count);

            // Active DLL is restored to genuine engine
            Assert.Equal(genuineAvcodecBytes.Length, new FileInfo(avcodecPath).Length);
            Assert.Equal(genuineAvcodecBytes, File.ReadAllBytes(avcodecPath));

            var report3 = VideoModernizationService.GetVideoStatus(tempDir);
            Assert.False(report3.IsClientModernized);
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
