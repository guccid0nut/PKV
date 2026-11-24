// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

/// <summary>
///     Value-Weighted Regression Suite for Issue 8B (POSIX shadow-directory elimination, empty-dir self-heal,
///     and root tos*/privacy*/honicon_* symlinking) and Issue 8C (Smart Auto-Rescue Mesa Zink preserving
///     proprietary NVIDIA and hardware Mesa OpenGL).
/// </summary>
public sealed class LinuxPosixAndZinkTests
{
    [Fact]
    public void EnsurePosixClientRootPrepared_OnWindows_NeverCreatesEmptyShadowDirectories()
    {
        // protects: Prevention of Bug 8B where Windows runs of EnsurePosixClientRootPrepared created empty real
        //           directories linux-x64/{base,KONGOR,ClientPackageFiles,override} and shared/ that blocked
        //           Linux/macOS symlinks after bundling.
        // fails_when: Calling EnsurePosixClientRootPrepared on Windows for linux-x64/ or macos-x64/ creates any
        //             empty shadow tier directory on disk.
        // seam: Real LaunchConfigWriterService.EnsurePosixClientRootPrepared against real temp client directory tree.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempGames = Path.Combine(Path.GetTempPath(), $"Kongor_NoWinShadow_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempGames, "client");
        string sharedRoot = Path.Combine(tempGames, "shared");
        string linuxRoot = Path.Combine(clientRoot, "linux-x64");
        string macosRoot = Path.Combine(clientRoot, "macos-x64");

        try
        {
            Directory.CreateDirectory(sharedRoot);
            Directory.CreateDirectory(Path.Combine(clientRoot, "base"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "KONGOR"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "ClientPackageFiles"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "override"));
            Directory.CreateDirectory(Path.Combine(clientRoot, "game"));
            Directory.CreateDirectory(linuxRoot);
            Directory.CreateDirectory(macosRoot);

            List<string> logs = [];
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(linuxRoot, (_, msg) => logs.Add(msg));
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(macosRoot, (_, msg) => logs.Add(msg));

            foreach (string posixSub in new[] { linuxRoot, macosRoot })
            {
                foreach (string tier in new[] { "base", "KONGOR", "ClientPackageFiles", "override", "game" })
                {
                    Assert.False(
                        Directory.Exists(Path.Combine(posixSub, tier)),
                        $"Windows EnsurePosixClientRootPrepared must NOT create '{posixSub}/{tier}'.");
                }
            }

            Assert.False(Directory.Exists(Path.Combine(clientRoot, "shared")));
            Assert.Contains(logs, m => m.Contains("Skipping POSIX VFS symlink preparation on Windows", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempGames))
            {
                Directory.Delete(tempGames, recursive: true);
            }
        }
    }

    [Fact]
    public void TrySelfHealEmptyShadowDirectory_DeletesOnlyEmptyNonSymlinkDirectoriesAndPreservesUserData()
    {
        // protects: Safe POSIX self-heal of empty shadow directories without ever deleting user mods/files
        //           or active symlinks, and strict no-op when allowPosixHeal is false.
        // fails_when: Empty real directories survive self-heal, non-empty directories are deleted, or Windows
        //             runs with allowPosixHeal=false mutate directories.
        // seam: Real LaunchConfigWriterService.TrySelfHealEmptyShadowDirectory against real filesystem entries.
        string tempRoot = Path.Combine(Path.GetTempPath(), $"Kongor_ShadowHeal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string emptyDir = Path.Combine(tempRoot, "override");
            string nonEmptyDir = Path.Combine(tempRoot, "nonempty_override");
            string markerFile = Path.Combine(nonEmptyDir, "custom.txt");
            Directory.CreateDirectory(emptyDir);
            Directory.CreateDirectory(nonEmptyDir);
            File.WriteAllText(markerFile, "preserve me");

            List<string> logs = [];

            // 1. allowPosixHeal=false must leave emptyDir untouched
            bool healedDisabled = LaunchConfigWriterService.TrySelfHealEmptyShadowDirectory(
                emptyDir, allowPosixHeal: false, (_, msg) => logs.Add(msg));
            Assert.False(healedDisabled);
            Assert.True(Directory.Exists(emptyDir));

            // 2. allowPosixHeal=true must delete emptyDir and log self-heal
            bool healedEmpty = LaunchConfigWriterService.TrySelfHealEmptyShadowDirectory(
                emptyDir, allowPosixHeal: true, (_, msg) => logs.Add(msg));
            Assert.True(healedEmpty);
            Assert.False(Directory.Exists(emptyDir));
            Assert.Contains(logs, m => m.Contains("[POSIX_VFS] Self-healed empty shadow directory", StringComparison.Ordinal));

            // 3. Non-empty directory MUST be preserved intact
            bool healedNonEmpty = LaunchConfigWriterService.TrySelfHealEmptyShadowDirectory(
                nonEmptyDir, allowPosixHeal: true, (_, msg) => logs.Add(msg));
            Assert.False(healedNonEmpty);
            Assert.True(File.Exists(markerFile));
            Assert.Contains(logs, m => m.Contains("Preserving non-empty non-symlink directory", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsurePosixClientRootPrepared_WithPosixOverride_SelfHealsShadowDirsAndNeverCreatesEmptyFallbackDirs()
    {
        // protects: POSIX runtime self-healing of pre-existing empty shadow directories ({base,KONGOR,ClientPackageFiles,override,shared}),
        //           symlinking of root tos*.txt/privacy*.txt/honicon_*.png assets, and guarantee that fallback paths
        //           never recreate empty real directories if symlink creation fails.
        // fails_when: Empty shadow dirs block symlink attempts, fallback creates real directories, or root legal/icon
        //             files are ignored by EnsurePosixClientRootPrepared.
        // seam: Real LaunchConfigWriterService.EnsurePosixClientRootPrepared(..., isPosixOverride: true) on temp FS.
        string tempGames = Path.Combine(Path.GetTempPath(), $"Kongor_PosixSelfHeal_{Guid.NewGuid():N}");
        string clientRoot = Path.Combine(tempGames, "client");
        string sharedRoot = Path.Combine(tempGames, "shared");
        string linuxRoot = Path.Combine(clientRoot, "linux-x64");

        try
        {
            Directory.CreateDirectory(sharedRoot);
            foreach (string tier in new[] { "base", "KONGOR", "ClientPackageFiles", "override", "game" })
            {
                Directory.CreateDirectory(Path.Combine(clientRoot, tier));
            }
            Directory.CreateDirectory(linuxRoot);

            // Seed empty Bug-8B shadow dirs in linux-x64/ and client/shared
            foreach (string shadowTier in new[] { "base", "KONGOR", "ClientPackageFiles", "override" })
            {
                Directory.CreateDirectory(Path.Combine(linuxRoot, shadowTier));
            }
            Directory.CreateDirectory(Path.Combine(clientRoot, "shared"));

            // Seed root-level assets (tos, privacy, honicon)
            File.WriteAllText(Path.Combine(clientRoot, "tos.txt"), "Terms");
            File.WriteAllText(Path.Combine(clientRoot, "privacy_en.txt"), "Privacy");
            File.WriteAllBytes(Path.Combine(clientRoot, "honicon_48.png"), [0x89, 0x50, 0x4E, 0x47]);

            List<string> logs = [];
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(
                linuxRoot,
                (_, msg) => logs.Add(msg),
                isPosixOverride: true);

            // Every empty shadow directory MUST have been self-healed before symlink creation
            foreach (string shadowTier in new[] { "base", "KONGOR", "ClientPackageFiles", "override" })
            {
                string targetPath = Path.Combine(linuxRoot, shadowTier);
                Assert.Contains(
                    logs,
                    m => m.Contains("[POSIX_VFS] Self-healed empty shadow directory", StringComparison.Ordinal) &&
                         m.Contains(targetPath, StringComparison.Ordinal));

                if (Directory.Exists(targetPath))
                {
                    // If it exists (e.g. Linux/macOS or Windows Dev Mode), it MUST be a symlink, never an empty real dir
                    Assert.NotNull(Directory.ResolveLinkTarget(targetPath, returnFinalTarget: false));
                }
            }

            string sharedLink = Path.Combine(clientRoot, "shared");
            Assert.Contains(
                logs,
                m => m.Contains("[POSIX_VFS] Self-healed empty shadow directory", StringComparison.Ordinal) &&
                     m.Contains(sharedLink, StringComparison.Ordinal));
            if (Directory.Exists(sharedLink))
            {
                Assert.NotNull(Directory.ResolveLinkTarget(sharedLink, returnFinalTarget: false));
            }

            // Verify root assets were either symlinked or explicitly warned (on non-DevMode Windows), never ignored
            foreach (string asset in new[] { "tos.txt", "privacy_en.txt", "honicon_48.png" })
            {
                string assetLink = Path.Combine(linuxRoot, asset);
                bool linkedOrWarned =
                    (File.Exists(assetLink) && File.ResolveLinkTarget(assetLink, returnFinalTarget: false) is not null) ||
                    logs.Exists(m => m.Contains(asset, StringComparison.Ordinal));
                Assert.True(linkedOrWarned, $"Expected symlink or diagnostic for root asset '{asset}'.");
            }
        }
        finally
        {
            if (Directory.Exists(tempGames))
            {
                Directory.Delete(tempGames, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080/PCIe/SSE2", true)]
    [InlineData("AMD Radeon RX 7900 XTX (radeonsi, navi31, LLVM 17.0.6, DRM 3.57)", true)]
    [InlineData("Mesa Intel(R) Iris(R) Xe Graphics (ADL GT2)", true)]
    [InlineData("", true)]
    public void ConfigureLinuxGraphicsEnvironment_PreservesNativeGlOnHardwareAndUnprobedRenderers(
        string openGlRenderer,
        bool hardwareVulkanPresent)
    {
        // protects: Proprietary NVIDIA GLX and hardware Mesa OpenGL (radeonsi/iris) from forced MESA_LOADER_DRIVER_OVERRIDE=zink
        //           when KONGOR_USE_ZINK is unset, while preserving persistent shadercache and glthread.
        // fails_when: Smart Auto-Rescue Zink triggers on NVIDIA, AMD, Intel hardware GL, or when glxinfo output is empty.
        // seam: LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment with synthetic glxinfo/vulkaninfo outputs.
        string tempDir = Path.Combine(Path.GetTempPath(), $"Kongor_ZinkHwGl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string glxOut = string.IsNullOrEmpty(openGlRenderer)
                ? string.Empty
                : $"    OpenGL renderer string: {openGlRenderer}\n    OpenGL version string: 4.6 Compatibility\n";
            string vkOut = hardwareVulkanPresent
                ? "GPU0:\n    deviceType = PHYSICAL_DEVICE_TYPE_DISCRETE_GPU\n    deviceName = Hardware GPU\n    driverName = radv\n"
                : string.Empty;

            StringDictionary env = new();
            List<string> logs = [];
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                env,
                tempDir,
                (_, msg) => logs.Add(msg),
                useZinkOverride: string.Empty,
                forceMesaGlxOverride: "0",
                glThreadOverride: "1",
                glxinfoOutputOverride: glxOut,
                vulkaninfoOutputOverride: vkOut);

            Assert.Null(env["MESA_LOADER_DRIVER_OVERRIDE"]);
            Assert.Null(env["__GLX_VENDOR_LIBRARY_NAME"]);
            Assert.Equal("true", env["mesa_glthread"]);
            Assert.Equal("1", env["__GL_THREADED_OPTIMIZATIONS"]);
            Assert.Equal(Path.Combine(tempDir, "userdata", "shadercache"), env["MESA_SHADER_CACHE_DIR"]);
            Assert.Contains(logs, m => m.Contains("Preserving native OpenGL renderer", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("llvmpipe (LLVM 15.0.7, 256 bits)")]
    [InlineData("softpipe")]
    [InlineData("Mesa DRI swrast")]
    public void ConfigureLinuxGraphicsEnvironment_AutoRescuesZinkOnlyWhenSoftwareGlHasHardwareVulkan(
        string softwareRenderer)
    {
        // protects: Smart Auto-Rescue Zink contract: promote MESA_LOADER_DRIVER_OVERRIDE=zink ONLY when KONGOR_USE_ZINK
        //           is unset AND glxinfo explicitly reports software GL (llvmpipe/softpipe/swrast) alongside a non-lavapipe
        //           hardware Vulkan GPU or ICD manifest — never on CPU lavapipe-only systems, and never when KONGOR_USE_ZINK=0.
        // fails_when: Zink promotes on lavapipe-only CPU systems, skips multi-GPU hardware Vulkan behind llvmpipe,
        //             ignores hardware ICD manifests when vulkaninfo is absent, or overrides explicit KONGOR_USE_ZINK=0.
        // seam: LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment with synthetic glxinfo/vulkaninfo/ICD dir inputs.
        string tempDir = Path.Combine(Path.GetTempPath(), $"Kongor_ZinkSwGl_{Guid.NewGuid():N}");
        string icdDir = Path.Combine(tempDir, "icd.d");
        Directory.CreateDirectory(icdDir);

        try
        {
            string swGlx = $"    OpenGL renderer string: {softwareRenderer}\n";

            // 1. CPU lavapipe only + only lvp_icd -> MUST NOT promote Zink
            File.WriteAllText(Path.Combine(icdDir, "lvp_icd.x86_64.json"), "{}");
            File.WriteAllText(Path.Combine(icdDir, "radeon_icd.i686.json"), "{}");
            string cpuOnlyVk = "GPU0:\n    deviceType = PHYSICAL_DEVICE_TYPE_CPU\n    deviceName = llvmpipe (LLVM 15.0.7)\n    driverName = lvp\n";
            StringDictionary cpuOnlyEnv = new();
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                cpuOnlyEnv,
                tempDir,
                useZinkOverride: string.Empty,
                glxinfoOutputOverride: swGlx,
                vulkaninfoOutputOverride: cpuOnlyVk,
                vulkanIcdDirsOverride: [icdDir]);
            Assert.Null(cpuOnlyEnv["MESA_LOADER_DRIVER_OVERRIDE"]);

            // Even with no vulkaninfo output, lvp_ + i686-only ICDs MUST NOT promote Zink
            StringDictionary lvpIcdOnlyEnv = new();
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                lvpIcdOnlyEnv,
                tempDir,
                useZinkOverride: string.Empty,
                glxinfoOutputOverride: swGlx,
                vulkaninfoOutputOverride: string.Empty,
                vulkanIcdDirsOverride: [icdDir]);
            Assert.Null(lvpIcdOnlyEnv["MESA_LOADER_DRIVER_OVERRIDE"]);

            // 2. Multi-GPU vulkaninfo (CPU lavapipe + integrated GPU intel) -> MUST promote Zink
            string multiGpuVk =
                "GPU0:\n    deviceType = PHYSICAL_DEVICE_TYPE_CPU\n    deviceName = llvmpipe\n    driverName = lvp\n" +
                "GPU1:\n    deviceType = PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU\n    deviceName = Intel(R) Graphics (RPL-P)\n    driverName = intel\n";
            StringDictionary rescuedMultiEnv = new();
            List<string> rescueLogs = [];
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                rescuedMultiEnv,
                tempDir,
                (_, msg) => rescueLogs.Add(msg),
                useZinkOverride: string.Empty,
                glxinfoOutputOverride: swGlx,
                vulkaninfoOutputOverride: multiGpuVk);
            Assert.Equal("zink", rescuedMultiEnv["MESA_LOADER_DRIVER_OVERRIDE"]);
            Assert.Contains(rescueLogs, m => m.Contains("Auto-Rescue Zink", StringComparison.Ordinal));

            // 3. Hardware ICD fallback when vulkaninfo is missing -> MUST promote Zink
            File.WriteAllText(Path.Combine(icdDir, "radeon_icd.x86_64.json"), "{}");
            StringDictionary rescuedIcdEnv = new();
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                rescuedIcdEnv,
                tempDir,
                useZinkOverride: string.Empty,
                glxinfoOutputOverride: swGlx,
                vulkaninfoOutputOverride: string.Empty,
                vulkanIcdDirsOverride: [icdDir]);
            Assert.Equal("zink", rescuedIcdEnv["MESA_LOADER_DRIVER_OVERRIDE"]);

            // 4. Explicit KONGOR_USE_ZINK=0 MUST block auto-rescue even with llvmpipe + hardware Vulkan
            StringDictionary optOutEnv = new();
            optOutEnv["MESA_LOADER_DRIVER_OVERRIDE"] = "zink";
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                optOutEnv,
                tempDir,
                useZinkOverride: "0",
                glxinfoOutputOverride: swGlx,
                vulkaninfoOutputOverride: multiGpuVk);
            Assert.Null(optOutEnv["MESA_LOADER_DRIVER_OVERRIDE"]);

            // 5. Explicit KONGOR_USE_ZINK=1 MUST honor opt-in even on NVIDIA hardware GL
            StringDictionary optInEnv = new();
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                optInEnv,
                tempDir,
                useZinkOverride: "1",
                glxinfoOutputOverride: "    OpenGL renderer string: NVIDIA GeForce RTX 4090/PCIe/SSE2\n",
                vulkaninfoOutputOverride: multiGpuVk);
            Assert.Equal("zink", optInEnv["MESA_LOADER_DRIVER_OVERRIDE"]);
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
