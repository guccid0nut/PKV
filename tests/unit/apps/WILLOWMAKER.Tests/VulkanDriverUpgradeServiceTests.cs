// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanDriverUpgradeServiceTests.cs
// System: WILLOWMAKER Unit Tests - Vulkan & DXVK Driver Upgrader
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class VulkanDriverUpgradeServiceTests
{
    [Fact]
    public void VulkanDriverUpgradeService_CheckDotLocalRedirection_DetectsPresenceOfLocalMarker()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_DotLocal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Assert.False(VulkanDriverUpgradeService.CheckDotLocalRedirection(tempDir));

            File.WriteAllBytes(Path.Combine(tempDir, "hon_x64.exe.local"), []);

            Assert.True(VulkanDriverUpgradeService.CheckDotLocalRedirection(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void VulkanDriverUpgradeService_CheckDxvkD3D9_VerifiesMinimumFileSize()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_DxvkSize_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string d3d9Path = Path.Combine(tempDir, "d3d9.dll");
            Assert.False(VulkanDriverUpgradeService.CheckDxvkD3D9(tempDir));

            // Small dummy file (< 1MB)
            File.WriteAllBytes(d3d9Path, new byte[500]);
            Assert.False(VulkanDriverUpgradeService.CheckDxvkD3D9(tempDir));

            // Valid DXVK size (>= 1MB)
            File.WriteAllBytes(d3d9Path, new byte[1_200_000]);
            Assert.True(VulkanDriverUpgradeService.CheckDxvkD3D9(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void InspectClient_LegacyInstallation_ReportsRequiresUpgrade()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_InspectLegacy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Vanilla client with no DXVK and unpatched vid driver
            int minVidSize = EnginePatchService.VidDriverOffset + 0x50;
            byte[] vidBytes = new byte[minVidSize];
            vidBytes[EnginePatchService.VidDriverOffset] = 0x85;
            vidBytes[EnginePatchService.VidDriverOffset + 1] = 0xC0;
            vidBytes[EnginePatchService.VidDriverOffset + 2] = 0x74;
            vidBytes[EnginePatchService.VidDriverOffset + 3] = 0x6D;
            File.WriteAllBytes(Path.Combine(tempDir, "vid_d3d9_x64.dll"), vidBytes);

            ClientGraphicsDriverReport report = VulkanDriverUpgradeService.InspectClient(tempDir);

            Assert.True(report.RequiresUpgrade);
            Assert.False(report.HasDxvkD3D9);
            Assert.False(report.HasDotLocalRedirection);
            Assert.False(report.IsVidDriverPatched);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task VulkanDriverUpgradeService_UpgradeToVulkanAsync_AppliesAllModernizationSteps()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_UpgradeAsync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Setup mock vid_d3d9_x64.dll
            int minVidSize = EnginePatchService.VidDriverOffset + 0x50;
            byte[] vidBytes = new byte[minVidSize];
            vidBytes[EnginePatchService.VidDriverOffset] = 0x85;
            vidBytes[EnginePatchService.VidDriverOffset + 1] = 0xC0;
            vidBytes[EnginePatchService.VidDriverOffset + 2] = 0x74;
            vidBytes[EnginePatchService.VidDriverOffset + 3] = 0x6D;
            File.WriteAllBytes(Path.Combine(tempDir, "vid_d3d9_x64.dll"), vidBytes);

            // Act - perform upgrade
            VulkanUpgradeResult result = await VulkanDriverUpgradeService.UpgradeToVulkanAsync(
                tempDir, RayTracingPreset.Competitive);

            // Assert
            Assert.True(File.Exists(Path.Combine(tempDir, "hon_x64.exe.local")), "hon_x64.exe.local must be created.");
            Assert.True(File.Exists(Path.Combine(tempDir, "dxvk.conf")), "dxvk.conf must be created.");
            Assert.True(File.Exists(Path.Combine(tempDir, "vid_d3d9_x64.dll.bak")), "vid_d3d9_x64.dll.bak backup must be created.");

            byte[] patchedVidBytes = File.ReadAllBytes(Path.Combine(tempDir, "vid_d3d9_x64.dll"));
            Assert.True(EnginePatchService.IsVidDriverPatched(patchedVidBytes));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void VulkanDriverUpgradeService_IsServerDirectory_CorrectlyIdentifiesServerDirectories()
    {
        Assert.True(VulkanDriverUpgradeService.IsServerDirectory("C:/ProjectKongor/games/server"));
        Assert.True(VulkanDriverUpgradeService.IsServerDirectory("games/server/windows-x64"));
        Assert.False(VulkanDriverUpgradeService.IsServerDirectory("C:/ProjectKongor/games/client"));
    }

    [Fact]
    public void CheckDotLocalRedirection_ServerWithWindowsX64_EnforcesBothLocations()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_ServerDotLocal_" + Guid.NewGuid().ToString("N"));
        string win64Dir = Path.Combine(tempDir, "windows-x64");
        Directory.CreateDirectory(win64Dir);

        try
        {
            Assert.False(VulkanDriverUpgradeService.CheckDotLocalRedirection(tempDir));

            // Run EnsureDotLocalRedirection
            VulkanDriverUpgradeService.EnsureDotLocalRedirection(tempDir);

            Assert.True(File.Exists(Path.Combine(tempDir, "hon_x64.exe.local")), "Root marker must exist.");
            Assert.True(File.Exists(Path.Combine(win64Dir, "hon_x64.exe.local")), "windows-x64 marker must exist.");
            Assert.True(VulkanDriverUpgradeService.CheckDotLocalRedirection(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpgradeToVulkanAsync_DedicatedServer_DeploysDotLocalAndCopiesRootD3D9()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_ServerUpgrade_" + Guid.NewGuid().ToString("N"));
        string win64Dir = Path.Combine(tempDir, "windows-x64");
        Directory.CreateDirectory(win64Dir);

        try
        {
            // Seed a valid d3d9.dll in windows-x64
            string win64D3D9 = Path.Combine(win64Dir, "d3d9.dll");
            File.WriteAllBytes(win64D3D9, new byte[1_200_000]);

            // Create mock vid_d3d9_x64.dll in windows-x64
            int minVidSize = EnginePatchService.VidDriverOffset + 0x50;
            byte[] vidBytes = new byte[minVidSize];
            vidBytes[EnginePatchService.VidDriverOffset] = 0x85;
            vidBytes[EnginePatchService.VidDriverOffset + 1] = 0xC0;
            vidBytes[EnginePatchService.VidDriverOffset + 2] = 0x74;
            vidBytes[EnginePatchService.VidDriverOffset + 3] = 0x6D;
            File.WriteAllBytes(Path.Combine(win64Dir, "vid_d3d9_x64.dll"), vidBytes);

            // Act - perform upgrade targeting server
            VulkanUpgradeResult result = await VulkanDriverUpgradeService.UpgradeToVulkanAsync(
                tempDir, RayTracingPreset.Competitive, isServer: true);

            // Assert
            Assert.True(File.Exists(Path.Combine(tempDir, "hon_x64.exe.local")), "Root hon_x64.exe.local must exist.");
            Assert.True(File.Exists(Path.Combine(win64Dir, "hon_x64.exe.local")), "windows-x64 hon_x64.exe.local must exist.");
            Assert.True(File.Exists(Path.Combine(tempDir, "d3d9.dll")), "Root d3d9.dll must be copied from windows-x64.");
            Assert.True(File.Exists(Path.Combine(tempDir, "dxvk.conf")), "Root dxvk.conf must exist.");
            Assert.True(File.Exists(Path.Combine(win64Dir, "dxvk.conf")), "windows-x64 dxvk.conf must exist.");
            Assert.False(File.Exists(Path.Combine(win64Dir, "VkLayer_hon_replay_capture.dll")), "Replay capture layer must NOT be deployed for dedicated server.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void VulkanDriverUpgradeService_CheckDxvkConfig_EnforcesBothRootAndWindowsX64WhenSubfolderExists()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Vulkan_DxvkConfigCheck_" + Guid.NewGuid().ToString("N"));
        string win64Dir = Path.Combine(tempDir, "windows-x64");
        Directory.CreateDirectory(win64Dir);

        try
        {
            // Neither exists
            Assert.False(VulkanDriverUpgradeService.CheckDxvkConfig(tempDir));

            // Only root exists
            File.WriteAllText(Path.Combine(tempDir, "dxvk.conf"), "# test");
            Assert.False(VulkanDriverUpgradeService.CheckDxvkConfig(tempDir));

            // Both exist
            File.WriteAllText(Path.Combine(win64Dir, "dxvk.conf"), "# test");
            Assert.True(VulkanDriverUpgradeService.CheckDxvkConfig(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpgradeToVulkanAsync_WhenServerD3D9Missing_FindsAndStagesFromClient()
    {
        string baseTemp = Path.Combine(Path.GetTempPath(), "Vulkan_Staging_" + Guid.NewGuid().ToString("N"));
        string serverDir = Path.Combine(baseTemp, "games", "server");
        string serverWin64 = Path.Combine(serverDir, "windows-x64");
        string clientDir = Path.Combine(baseTemp, "games", "client");
        string clientWin64 = Path.Combine(clientDir, "windows-x64");

        Directory.CreateDirectory(serverWin64);
        Directory.CreateDirectory(clientWin64);

        try
        {
            // Seed client DXVK d3d9.dll
            File.WriteAllBytes(Path.Combine(clientWin64, "d3d9.dll"), new byte[1_200_000]);

            // Seed mock vid_d3d9_x64.dll in server
            int minVidSize = EnginePatchService.VidDriverOffset + 0x50;
            byte[] vidBytes = new byte[minVidSize];
            vidBytes[EnginePatchService.VidDriverOffset] = 0x85;
            vidBytes[EnginePatchService.VidDriverOffset + 1] = 0xC0;
            vidBytes[EnginePatchService.VidDriverOffset + 2] = 0x74;
            vidBytes[EnginePatchService.VidDriverOffset + 3] = 0x6D;
            File.WriteAllBytes(Path.Combine(serverWin64, "vid_d3d9_x64.dll"), vidBytes);

            // Server initially has NO d3d9.dll at all
            Assert.False(File.Exists(Path.Combine(serverDir, "d3d9.dll")));
            Assert.False(File.Exists(Path.Combine(serverWin64, "d3d9.dll")));

            // Upgrade server
            VulkanUpgradeResult result = await VulkanDriverUpgradeService.UpgradeToVulkanAsync(
                serverDir, RayTracingPreset.Competitive, isServer: true);

            // Assert that FindSourceFile discovered client's d3d9.dll and staged it into server root and server windows-x64
            Assert.True(File.Exists(Path.Combine(serverDir, "d3d9.dll")), "Server root d3d9.dll must be staged.");
            Assert.True(File.Exists(Path.Combine(serverWin64, "d3d9.dll")), "Server windows-x64 d3d9.dll must be staged.");
            Assert.True(File.Exists(Path.Combine(serverDir, "dxvk.conf")), "Server root dxvk.conf must be created.");
            Assert.True(File.Exists(Path.Combine(serverWin64, "dxvk.conf")), "Server windows-x64 dxvk.conf must be created.");
        }
        finally
        {
            if (Directory.Exists(baseTemp))
            {
                Directory.Delete(baseTemp, recursive: true);
            }
        }
    }
}
