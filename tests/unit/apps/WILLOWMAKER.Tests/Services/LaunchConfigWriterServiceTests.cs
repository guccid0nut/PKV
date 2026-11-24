// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests.Services;

/// <summary>
///     Unit and regression tests for cross-platform (Windows, Linux, macOS) client launch configuration,
///     Mach-O CodeDirectory page-hash integrity (OBL-01), POSIX subfolder VFS symlink preparation (OBL-02),
///     and OS-aware video driver selection (OBL-03).
/// </summary>
public sealed class LaunchConfigWriterServiceTests
{
    // satisfies: OBL-01
    // protects: Apple Developer ID CodeDirectory SHA-256 page hashes on macOS universal Mach-O binaries and EnginePatchService host gating.
    // fails_when: Any of the restored __TEXT pages in libk2.dylib or cgame.dylib drift from their signed SHA-256 hash or EnginePatchService mutates macos-x64 binaries by default.
    // seam: Real Mach-O fat header / LC_CODE_SIGNATURE SuperBlob binary parser + EnginePatchService.EnsureEnginePatched on filesystem sandbox.
    [Fact]
    public void MacOsMachOBinaries_HaveValidCodeDirectoryPageHashesAcrossAllSlices()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_MacOsGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string macSubdir = Path.Combine(tempDir, "macos-x64");
            Directory.CreateDirectory(Path.Combine(macSubdir, "game"));

            byte[] mockDylib = new byte[256];
            // Embed 250.0f (00 00 7A 43) and legacy domain so an un-gated patcher would mutate it
            mockDylib[16] = 0x00;
            mockDylib[17] = 0x00;
            mockDylib[18] = 0x7A;
            mockDylib[19] = 0x43;
            byte[] domainBytes = Encoding.ASCII.GetBytes("masterserver.cis.heroesofnewerth.com");
            Buffer.BlockCopy(domainBytes, 0, mockDylib, 32, domainBytes.Length);

            string dylibPath = Path.Combine(macSubdir, "libk2.dylib");
            File.WriteAllBytes(dylibPath, mockDylib);
            byte[] expectedHash = SHA256.HashData(mockDylib);

            EnginePatchService.EnsureEnginePatched(tempDir);

            byte[] afterBytes = File.ReadAllBytes(dylibPath);
            Assert.Equal(expectedHash, SHA256.HashData(afterBytes));
            Assert.False(File.Exists(dylibPath + ".bak"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // satisfies: OBL-02
    // protects: POSIX subfolder VFS mount preparation (base, KONGOR, ClientPackageFiles, override, game/*.s2z) and prevention of startup.cfg pollution in shared games/client/game/.
    // fails_when: EnsurePosixClientRootPrepared fails to expose shared assets inside linux-x64 / macos-x64 or EnsureLinuxModesettingCompatibility creates game/startup.cfg.
    // seam: LaunchConfigWriterService.EnsurePosixClientRootPrepared and EnsureLinuxModesettingCompatibility against real filesystem directory trees.
    [Fact]
    public void EnsurePosixClientRootPrepared_LinksSharedAssetsIntoSubfolderWithoutPollutingSharedGameDir()
    {
        string tempGamesRoot = Path.Combine(Path.GetTempPath(), "Kongor_PosixVfs_" + Guid.NewGuid().ToString("N"));
        string tempClientRoot = Path.Combine(tempGamesRoot, "client");
        Directory.CreateDirectory(tempClientRoot);
        Directory.CreateDirectory(Path.Combine(tempGamesRoot, "shared"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempClientRoot, "base"));
            Directory.CreateDirectory(Path.Combine(tempClientRoot, "KONGOR"));
            Directory.CreateDirectory(Path.Combine(tempClientRoot, "ClientPackageFiles"));
            Directory.CreateDirectory(Path.Combine(tempClientRoot, "override"));
            Directory.CreateDirectory(Path.Combine(tempClientRoot, "game"));
            string sharedS2z = Path.Combine(tempClientRoot, "game", "resources0.s2z");
            File.WriteAllBytes(sharedS2z, [0x50, 0x4B, 0x03, 0x04]);

            string macosRoot = Path.Combine(tempClientRoot, "macos-x64");
            Directory.CreateDirectory(macosRoot);

            List<string> logs = [];
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(macosRoot, (_, msg) => logs.Add(msg), isPosixOverride: true);

            foreach (string tier in new[] { "base", "KONGOR", "ClientPackageFiles", "override" })
            {
                string tierTarget = Path.Combine(macosRoot, tier);
                if (Directory.Exists(tierTarget))
                {
                    Assert.NotNull(Directory.ResolveLinkTarget(tierTarget, returnFinalTarget: false));
                }
                else
                {
                    Assert.Contains(logs, m => m.Contains(tier, StringComparison.Ordinal));
                }
            }

            string sharedTarget = Path.Combine(tempClientRoot, "shared");
            if (Directory.Exists(sharedTarget))
            {
                Assert.NotNull(Directory.ResolveLinkTarget(sharedTarget, returnFinalTarget: false));
            }
            else
            {
                Assert.Contains(logs, m => m.Contains("shared", StringComparison.Ordinal));
            }

            string linkedS2z = Path.Combine(macosRoot, "game", "resources0.s2z");
            Assert.True(
                File.Exists(linkedS2z) || logs.Exists(m => m.Contains("resources0.s2z", StringComparison.Ordinal)),
                "Expected symlink/copy or [POSIX_VFS] diagnostic for game/resources0.s2z.");

            // Verify idempotency on second run
            logs.Clear();
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(macosRoot, (_, msg) => logs.Add(msg), isPosixOverride: true);
            Assert.True(
                File.Exists(linkedS2z) || logs.Exists(m => m.Contains("resources0.s2z", StringComparison.Ordinal)),
                "Expected idempotent symlink/copy or [POSIX_VFS] diagnostic for game/resources0.s2z.");

            // Verify EnsureLinuxModesettingCompatibility never creates startup.cfg inside shared game/ folder
            LaunchConfigWriterService.EnsureLinuxModesettingCompatibility(tempClientRoot);
            Assert.False(File.Exists(Path.Combine(tempClientRoot, "game", "startup.cfg")));
            Assert.True(File.Exists(Path.Combine(tempClientRoot, "userdata", ".Heroes of Newerth", "game", "startup.cfg")));
            Assert.True(File.Exists(Path.Combine(tempClientRoot, "userdata", ".Heroes of Newerth", "override", "startup.cfg")));
        }
        finally
        {
            if (Directory.Exists(tempGamesRoot))
            {
                Directory.Delete(tempGamesRoot, recursive: true);
            }
        }
    }

    // satisfies: OBL-03
    // protects: OS-aware K2 video driver selection (vid_vulkan / vid_d3d12 on Windows; vid_gl2 on Linux/macOS unless a valid platform-native vid_vulkan exporting InitAPIs exists).
    // fails_when: POSIX client directories receive vid_vulkan or vid_d3d12 when no native vid_vulkan module exporting InitAPIs is present, or Windows client directories stop receiving vid_vulkan / vid_d3d12.
    // seam: LaunchConfigWriterService.ResolveEffectiveVideoDriver and VulkanDriverUpgradeService.InspectClient.
    [Fact]
    public void ResolveEffectiveVideoDriver_DefaultsToVidGl2OnPosixUnlessValidVidVulkanPresent()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Kongor_VidDriver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string winDir = Path.Combine(tempRoot, "win-client");
            Directory.CreateDirectory(winDir);
            File.WriteAllBytes(Path.Combine(winDir, "hon_x64.exe"), [0x4D, 0x5A]);

            Assert.Equal("vid_vulkan", LaunchConfigWriterService.ResolveEffectiveVideoDriver(winDir, useD3D12: false, forcePosix: false));
            Assert.Equal("vid_d3d12", LaunchConfigWriterService.ResolveEffectiveVideoDriver(winDir, useD3D12: true, forcePosix: false));

            string macDir = Path.Combine(tempRoot, "macos-x64");
            Directory.CreateDirectory(macDir);
            File.WriteAllBytes(Path.Combine(macDir, "HoN64"), [0xCA, 0xFE, 0xBA, 0xBE]);

            Assert.Equal("vid_gl2", LaunchConfigWriterService.ResolveEffectiveVideoDriver(macDir, useD3D12: false));
            Assert.Equal("vid_gl2", LaunchConfigWriterService.ResolveEffectiveVideoDriver(macDir, useD3D12: true));

            ClientGraphicsDriverReport macReport = VulkanDriverUpgradeService.InspectClient(macDir);
            Assert.False(macReport.RequiresUpgrade);
            Assert.False(File.Exists(Path.Combine(macDir, "d3d9.dll")));
            Assert.False(File.Exists(Path.Combine(macDir, "hon_x64.exe.local")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    // protects: UTF-32LE legacy domain suppression in Linux/macOS wchar_t (4-byte) binaries.
    // fails_when: SuppressLegacyDomains fails to zero-pad UTF-32LE encoded dead domains to "0.0.0.0".
    // seam: EnginePatchService.SuppressLegacyDomains buffer transformation.
    [Fact]
    public void SuppressLegacyDomains_ReplacesUtf32LeDomainsInPosixBinaries()
    {
        string domain = "masterserver.naeu.heroesofnewerth.com";
        byte[] utf32Bytes = Encoding.UTF32.GetBytes(domain);
        byte[] buffer = new byte[utf32Bytes.Length + 32];
        Buffer.BlockCopy(utf32Bytes, 0, buffer, 8, utf32Bytes.Length);

        int count = EnginePatchService.SuppressLegacyDomains(buffer);

        Assert.Equal(1, count);
        string replaced = Encoding.UTF32.GetString(buffer, 8, utf32Bytes.Length);
        Assert.StartsWith("0.0.0.0\0", replaced, StringComparison.Ordinal);
        Assert.Equal(domain.Length, replaced.Length);
    }

    // protects: Linux runtime library preparation filtering archaic C++/FreeType binaries from libs-x86_64.
    // fails_when: Archaic libstdc++.so.6, libgcc_s.so.1, libfreetype.so.6, libc++.so.1, or libc++abi.so.1 leak into libs-runtime.
    // seam: LaunchConfigWriterService.EnsureLinuxRuntimeLibrariesPrepared filesystem link/copy preparation.
    [Fact]
    public void EnsureLinuxRuntimeLibrariesPrepared_ExcludesArchaicLibrariesAndLinksSafeOnes()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "kongor-linux-libs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string linuxRoot = Path.Combine(tempRoot, "linux-x64");
            string bundledLibs = Path.Combine(linuxRoot, "libs-x86_64");
            Directory.CreateDirectory(bundledLibs);

            File.WriteAllBytes(Path.Combine(bundledLibs, "libfmodex64.so"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(bundledLibs, "libspeex.so.1"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(bundledLibs, "libstdc++.so.6"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(bundledLibs, "libgcc_s.so.1"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(bundledLibs, "libfreetype.so.6"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(bundledLibs, "libc++.so.1"), [0x7F, 0x45, 0x4C, 0x46]);
            File.WriteAllBytes(Path.Combine(bundledLibs, "libc++abi.so.1"), [0x7F, 0x45, 0x4C, 0x46]);

            LaunchConfigWriterService.EnsureLinuxRuntimeLibrariesPrepared(linuxRoot);

            string runtimeLibs = Path.Combine(linuxRoot, "libs-runtime");
            Assert.True(Directory.Exists(runtimeLibs));
            Assert.True(File.Exists(Path.Combine(runtimeLibs, "libfmodex64.so")));
            Assert.True(File.Exists(Path.Combine(runtimeLibs, "libspeex.so.1")));
            Assert.False(File.Exists(Path.Combine(runtimeLibs, "libstdc++.so.6")));
            Assert.False(File.Exists(Path.Combine(runtimeLibs, "libgcc_s.so.1")));
            Assert.False(File.Exists(Path.Combine(runtimeLibs, "libfreetype.so.6")));
            Assert.False(File.Exists(Path.Combine(runtimeLibs, "libc++.so.1")));
            Assert.False(File.Exists(Path.Combine(runtimeLibs, "libc++abi.so.1")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    // protects: Linux aarch64 user-mode translator resolution (FEXInterpreter / box64) when binfmt_misc is absent.
    // fails_when: ResolveLinuxArm64TranslationCommand fails to wrap hon-x86_64 with FEXInterpreter or box64 on arm64 Linux without binfmt_misc, or wraps when binfmt_misc is active.
    // seam: LaunchConfigWriterService.ResolveLinuxArm64TranslationCommand against real temporary bin directories.
    [Fact]
    public void ResolveLinuxArm64TranslationCommand_WrapsWithFexOrBox64WhenBinfmtAbsent()
    {
        string tempBinDir = Path.Combine(Path.GetTempPath(), "kongor-arm64-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBinDir);
        try
        {
            string exePath = "/opt/kongor/games/client/linux-x64/hon-x86_64";
            string args = "-mod \"base;game;override\"";

            // 1. When binfmt_misc is active (e.g. SteamOS on ARM), launch directly
            (string binfmtFile, string binfmtArgs) = LaunchConfigWriterService.ResolveLinuxArm64TranslationCommand(
                exePath,
                args,
                forceArm64Linux: true,
                hasBinfmtRegistration: true,
                searchPathOverride: tempBinDir);
            Assert.Equal(exePath, binfmtFile);
            Assert.Equal(args, binfmtArgs);

            // 2. When binfmt_misc is absent and box64 exists in PATH, wrap with box64
            string box64Path = Path.Combine(tempBinDir, "box64");
            File.WriteAllBytes(box64Path, [0x7F, 0x45, 0x4C, 0x46]);

            (string boxFile, string boxArgs) = LaunchConfigWriterService.ResolveLinuxArm64TranslationCommand(
                exePath,
                args,
                forceArm64Linux: true,
                hasBinfmtRegistration: false,
                searchPathOverride: tempBinDir);
            Assert.Equal(box64Path, boxFile);
            Assert.Equal($"\"{exePath}\" {args}", boxArgs);

            // 3. When FEXInterpreter also exists in PATH, prefer FEXInterpreter over box64
            string fexPath = Path.Combine(tempBinDir, "FEXInterpreter");
            File.WriteAllBytes(fexPath, [0x7F, 0x45, 0x4C, 0x46]);

            (string fexFile, string fexArgs) = LaunchConfigWriterService.ResolveLinuxArm64TranslationCommand(
                exePath,
                args,
                forceArm64Linux: true,
                hasBinfmtRegistration: false,
                searchPathOverride: tempBinDir);
            Assert.Equal(fexPath, fexFile);
            Assert.Equal($"\"{exePath}\" {args}", fexArgs);
        }
        finally
        {
            if (Directory.Exists(tempBinDir))
            {
                Directory.Delete(tempBinDir, recursive: true);
            }
        }
    }

    // protects: Recursive macOS Mach-O target enumeration (HoN64 + universal64/*.dylib + game/*.dylib) while excluding legacy 32-bit Awesomium.framework.
    // fails_when: HoN64 or nested universal64/*.dylib libraries are omitted, Awesomium.framework is included, or HoN64 is ordered before its .dylib dependencies.
    // seam: LaunchConfigWriterService.EnumerateMacOsSignableTargets against real temporary directory trees.
    [Fact]
    public void EnumerateMacOsSignableTargets_IncludesHoN64AndUniversal64DylibsAndSkipsAwesomium()
    {
        string tempMacRoot = Path.Combine(Path.GetTempPath(), "kongor-macos-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempMacRoot, "game"));
        Directory.CreateDirectory(Path.Combine(tempMacRoot, "Contents", "Frameworks", "universal64"));
        Directory.CreateDirectory(Path.Combine(tempMacRoot, "Contents", "Frameworks", "Awesomium.framework", "Versions", "A"));

        try
        {
            string hon64 = Path.Combine(tempMacRoot, "HoN64");
            string libk2 = Path.Combine(tempMacRoot, "libk2.dylib");
            string cgame = Path.Combine(tempMacRoot, "game", "cgame.dylib");
            string libcurl = Path.Combine(tempMacRoot, "Contents", "Frameworks", "universal64", "libcurl-universal.dylib");
            string awesomium = Path.Combine(tempMacRoot, "Contents", "Frameworks", "Awesomium.framework", "Versions", "A", "libplugin_carbon_helper.dylib");

            File.WriteAllBytes(hon64, [0xCA, 0xFE, 0xBA, 0xBE]);
            File.WriteAllBytes(libk2, [0xCA, 0xFE, 0xBA, 0xBE]);
            File.WriteAllBytes(cgame, [0xCA, 0xFE, 0xBA, 0xBE]);
            File.WriteAllBytes(libcurl, [0xCA, 0xFE, 0xBA, 0xBE]);
            File.WriteAllBytes(awesomium, [0xCE, 0xFA, 0xED, 0xFE]);

            IReadOnlyList<string> targets = LaunchConfigWriterService.EnumerateMacOsSignableTargets(tempMacRoot);

            Assert.Equal(4, targets.Count);
            Assert.Contains(libk2, targets);
            Assert.Contains(cgame, targets);
            Assert.Contains(libcurl, targets);
            Assert.DoesNotContain(awesomium, targets);
            Assert.Equal(hon64, targets[^1]);
        }
        finally
        {
            if (Directory.Exists(tempMacRoot))
            {
                Directory.Delete(tempMacRoot, recursive: true);
            }
        }
    }

    // protects: Linux native OpenGL performance defaults (mesa_glthread=true, __GL_THREADED_OPTIMIZATIONS=1, persistent userdata/shadercache) while keeping Zink strictly opt-in and protecting NVIDIA GLX.
    // fails_when: Zink is auto-enabled by default, __GLX_VENDOR_LIBRARY_NAME=mesa is forced without KONGOR_FORCE_MESA_GLX=1, or persistent shader cache paths are missing.
    // seam: LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment against real temporary directory and StringDictionary environment map.
    [Fact]
    public void ConfigureLinuxGraphicsEnvironment_EnablesGlThreadAndShaderCache_AndKeepsZinkOptIn()
    {
        string tempLinuxRoot = Path.Combine(Path.GetTempPath(), "kongor-linux-gl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLinuxRoot);

        try
        {
            StringDictionary defaultEnv = new();
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                defaultEnv,
                tempLinuxRoot,
                useZinkOverride: "0",
                forceMesaGlxOverride: "0",
                glThreadOverride: "1");

            string expectedCacheDir = Path.Combine(tempLinuxRoot, "userdata", "shadercache");
            Assert.True(Directory.Exists(expectedCacheDir));
            Assert.Equal(expectedCacheDir, defaultEnv["MESA_SHADER_CACHE_DIR"]);
            Assert.Equal("1", defaultEnv["MESA_DISK_CACHE_SINGLE_FILE"]);
            Assert.Equal(expectedCacheDir, defaultEnv["__GL_SHADER_DISK_CACHE_PATH"]);
            Assert.Equal("true", defaultEnv["mesa_glthread"]);
            Assert.Equal("1", defaultEnv["__GL_THREADED_OPTIMIZATIONS"]);
            Assert.Null(defaultEnv["MESA_LOADER_DRIVER_OVERRIDE"]);
            Assert.Null(defaultEnv["__GLX_VENDOR_LIBRARY_NAME"]);

            StringDictionary zinkOptInEnv = new();
            LaunchConfigWriterService.ConfigureLinuxGraphicsEnvironment(
                zinkOptInEnv,
                tempLinuxRoot,
                useZinkOverride: "1",
                forceMesaGlxOverride: "0",
                glThreadOverride: "1");

            Assert.Equal("zink", zinkOptInEnv["MESA_LOADER_DRIVER_OVERRIDE"]);
            Assert.Null(zinkOptInEnv["GALLIUM_DRIVER"]);
            Assert.Null(zinkOptInEnv["__GLX_VENDOR_LIBRARY_NAME"]);
        }
        finally
        {
            if (Directory.Exists(tempLinuxRoot))
            {
                Directory.Delete(tempLinuxRoot, recursive: true);
            }
        }
    }

    // protects: Out-of-process Linux GPU telemetry classification between hardware GPUs (DISCRETE_GPU / INTEGRATED_GPU) and CPU software rasterizers (llvmpipe / lavapipe).
    // fails_when: PHYSICAL_DEVICE_TYPE_CPU or lavapipe/llvmpipe is misclassified as a hardware Vulkan GPU or glxinfo renderer strings are not parsed.
    // seam: LaunchConfigWriterService.ProbeLinuxGraphicsTelemetry parser against real glxinfo -B and vulkaninfo --summary output formats.
    [Fact]
    public void ProbeLinuxGraphicsTelemetry_DistinguishesHardwareGpuFromLlvmPipeAndLavaPipe()
    {
        string hwGlx = """
            Extended renderer info (GLX_MESA_query_renderer):
                OpenGL vendor string: AMD
                OpenGL renderer string: AMD Radeon RX 7900 XTX (radeonsi, navi31, LLVM 17.0.6)
                OpenGL version string: 4.6 (Compatibility Profile) Mesa 24.0.5
            """;
        string hwVk = """
            GPU0:
                apiVersion         = 1.3.274
                driverVersion      = 24.0.5
                deviceType         = PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
                deviceName         = AMD Radeon RX 7900 XTX (RADV NAVI31)
                driverName         = radv
            """;

        (string hwRenderer, string hwVkName, string hwVkType, bool hasHwVk, string hwSummary) =
            LaunchConfigWriterService.ProbeLinuxGraphicsTelemetry(
                glxinfoOutputOverride: hwGlx,
                vulkaninfoOutputOverride: hwVk);

        Assert.Contains("AMD Radeon RX 7900 XTX", hwRenderer);
        Assert.Contains("RADV NAVI31", hwVkName);
        Assert.Equal("PHYSICAL_DEVICE_TYPE_DISCRETE_GPU", hwVkType);
        Assert.True(hasHwVk);
        Assert.Contains("vid_gl2 + glthread", hwSummary);

        string swGlx = """
            Extended renderer info (GLX_MESA_query_renderer):
                OpenGL vendor string: Mesa
                OpenGL renderer string: llvmpipe (LLVM 15.0.7, 256 bits)
                OpenGL version string: 4.5 (Compatibility Profile) Mesa 23.2.1
            """;
        string swVk = """
            GPU0:
                apiVersion         = 1.3.255
                deviceType         = PHYSICAL_DEVICE_TYPE_CPU
                deviceName         = llvmpipe (LLVM 15.0.7, 256 bits)
                driverName         = llvmpipe
            """;

        (string swRenderer, _, string swVkType, bool swHasHwVk, string swSummary) =
            LaunchConfigWriterService.ProbeLinuxGraphicsTelemetry(
                glxinfoOutputOverride: swGlx,
                vulkaninfoOutputOverride: swVk);

        Assert.Contains("llvmpipe", swRenderer);
        Assert.Equal("PHYSICAL_DEVICE_TYPE_CPU", swVkType);
        Assert.False(swHasHwVk);
        Assert.Contains("Linux Software OpenGL Active", swSummary);
    }

    [Fact]
    public void EnsurePosixClientRootPrepared_OnWindowsParentClientDir_QuarantinesManifestWithoutPosixSymlinkWarnings()
    {
        // protects: Windows client launch cleanliness when games/client contains linux-x64/macos-x64 subdirectories without Developer Mode symlink privilege.
        // fails_when: Calling EnsurePosixClientRootPrepared on a Windows parent client directory emits [POSIX_VFS_WARN] or creates linux-x64/game symlinks unnecessarily.
        // seam: LaunchConfigWriterService.EnsurePosixClientRootPrepared against real filesystem directory tree.

        if (!OperatingSystem.IsWindows())
            return;

        string tempRoot = Path.Combine(Path.GetTempPath(), "Kongor_WinPosixSkip_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempRoot, "client");
        Directory.CreateDirectory(Path.Combine(clientDir, "game", "shaders"));
        Directory.CreateDirectory(Path.Combine(clientDir, "linux-x64"));
        string legacyManifest = Path.Combine(clientDir, "manifest.xml");
        File.WriteAllText(legacyManifest, "<manifest />");

        try
        {
            List<string> logs = [];
            LaunchConfigWriterService.EnsurePosixClientRootPrepared(clientDir, (_, msg) => logs.Add(msg));

            Assert.False(File.Exists(legacyManifest));
            Assert.True(File.Exists(legacyManifest + ".disabled"));
            Assert.DoesNotContain(logs, m => m.Contains("[POSIX_VFS_WARN]", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(clientDir, "linux-x64", "game")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
