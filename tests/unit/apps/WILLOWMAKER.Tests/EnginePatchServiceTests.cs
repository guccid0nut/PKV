// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using WILLOWMAKER.Core.Enumerations;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Tests.Infrastructure;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class EnginePatchServiceTests
{
    [Fact]
    public void EnginePatchService_SuppressLegacyDomains_ReplacesAsciiAndWideDomainsWithNullPaddedZeros()
    {
        // Arrange
        string domain = "client.sea.heroesofnewerth.com";
        byte[] asciiBytes = Encoding.ASCII.GetBytes($"prefix_{domain}_suffix");
        byte[] wideBytes = Encoding.Unicode.GetBytes($"prefix_{domain}_suffix");

        // Act
        int asciiReplaced = EnginePatchService.SuppressLegacyDomains(asciiBytes);
        int wideReplaced = EnginePatchService.SuppressLegacyDomains(wideBytes);

        // Assert
        Assert.True(asciiReplaced > 0);
        Assert.True(wideReplaced > 0);

        string modifiedAscii = Encoding.ASCII.GetString(asciiBytes);
        Assert.DoesNotContain("client.sea.heroesofnewerth.com", modifiedAscii);
        Assert.Contains("0.0.0.0", modifiedAscii);

        string modifiedWide = Encoding.Unicode.GetString(wideBytes);
        Assert.DoesNotContain("client.sea.heroesofnewerth.com", modifiedWide);
        Assert.Contains("0.0.0.0", modifiedWide);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void EnginePatchService_EnsureEnginePatched_PatchesFloatConstantAndThrottleJumpWithBackup()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Prepare a mock k2_x64.dll with known offsets
            int fileSize = 0xFB5000;
            byte[] mockK2Bytes = new byte[fileSize];

            // Put 250.0f (0x437A0000) at 0xFB416C
            mockK2Bytes[0xFB416C] = 0x00;
            mockK2Bytes[0xFB416D] = 0x00;
            mockK2Bytes[0xFB416E] = 0x7A;
            mockK2Bytes[0xFB416F] = 0x43;

            // Put throttle jump (7C 96) at 0x8B72A8
            mockK2Bytes[0x8B72A8] = 0x7C;
            mockK2Bytes[0x8B72A9] = 0x96;

            // Put User Directory Initialization pattern at 0x9FE37A
            byte[] userDirPattern =
            [
                0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
                0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
                0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
                0x3C, 0x40, 0x00, 0x75, 0xF5
            ];
            Buffer.BlockCopy(userDirPattern, 0, mockK2Bytes, 0x9FE37A, userDirPattern.Length);

            // Put a dead legacy domain inside
            byte[] domainBytes = Encoding.ASCII.GetBytes("client.sea.heroesofnewerth.com");
            Buffer.BlockCopy(domainBytes, 0, mockK2Bytes, 0x1000, domainBytes.Length);

            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, mockK2Bytes);

            // Act - First Run (Patch)
            bool result = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(result);
            Assert.True(File.Exists(k2Path + ".bak"), "Backup .bak must be created.");

            byte[] patchedBytes = File.ReadAllBytes(k2Path);

            // Verify 1000.0f (0x447A0000) at 0xFB416C
            Assert.Equal(0x00, patchedBytes[0xFB416C]);
            Assert.Equal(0x00, patchedBytes[0xFB416D]);
            Assert.Equal(0x7A, patchedBytes[0xFB416E]);
            Assert.Equal(0x44, patchedBytes[0xFB416F]);

            // Verify NOP NOP (90 90) at 0x8B72A8
            Assert.Equal(0x90, patchedBytes[0x8B72A8]);
            Assert.Equal(0x90, patchedBytes[0x8B72A9]);

            // Verify User Directory Redirection at 0x9FE37A ('u','s','e','r' wide: 48 B8 75 00 73 00 65 00 72 00)
            Assert.Equal(0x48, patchedBytes[0x9FE37A]);
            Assert.Equal(0xB8, patchedBytes[0x9FE37B]);
            Assert.Equal((byte)'u', patchedBytes[0x9FE37C]);
            Assert.Equal(0x00, patchedBytes[0x9FE37D]);
            Assert.Equal((byte)'s', patchedBytes[0x9FE37E]);
            Assert.Equal(0x00, patchedBytes[0x9FE37F]);
            Assert.Equal((byte)'e', patchedBytes[0x9FE380]);
            Assert.Equal(0x00, patchedBytes[0x9FE381]);
            Assert.Equal((byte)'r', patchedBytes[0x9FE382]);
            Assert.Equal(0x00, patchedBytes[0x9FE383]);

            // Act - Second Run (Idempotency)
            bool secondResult = EnginePatchService.EnsureEnginePatched(tempDir);
            Assert.True(secondResult);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnginePatchService_RedirectUserDirectory_SuccessfullyRedirectsK2UserDirectory()
    {
        // Arrange
        byte[] mockBuffer = new byte[0xA00000];
        byte[] userDirPattern =
        [
            0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
            0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
            0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
            0x3C, 0x40, 0x00, 0x75, 0xF5, 0x48, 0x8D, 0x55, 0x90, 0x48, 0x8B, 0xCE, 0xE8, 0xD5, 0x58, 0x8A,
            0xFF, 0xEB
        ];
        Buffer.BlockCopy(userDirPattern, 0, mockBuffer, 0x9FE37A, userDirPattern.Length);

        // Act
        bool redirected = EnginePatchService.RedirectUserDirectory(mockBuffer);

        // Assert
        Assert.True(redirected);
        Assert.Equal(0x48, mockBuffer[0x9FE37A]);
        Assert.Equal(0xB8, mockBuffer[0x9FE37B]);
        Assert.Equal((byte)'u', mockBuffer[0x9FE37C]);
        Assert.Equal(0x00, mockBuffer[0x9FE37D]);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void EnginePatchService_EnsureEnginePatched_PatchesRealClientBinariesIfPresent()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Check if games/client exists in repository
        string? current = Directory.GetCurrentDirectory();
        string? clientDir = null;
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "games", "client");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "k2_x64.dll")))
            {
                clientDir = candidate;
                break;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        if (Directory.Exists(clientDir) && File.Exists(Path.Combine(clientDir, "k2_x64.dll")))
        {
            List<string> logs = [];
            bool patched = EnginePatchService.EnsureEnginePatched(clientDir, logs.Add);
            Assert.True(patched, string.Join(Environment.NewLine, logs));

            byte[] k2Bytes = File.ReadAllBytes(Path.Combine(clientDir, "k2_x64.dll"));
            Assert.Equal(0x48, k2Bytes[0x9FE37A]);
            Assert.Equal(0xB8, k2Bytes[0x9FE37B]);
            Assert.Equal((byte)'u', k2Bytes[0x9FE37C]);
            Assert.Equal(0x00, k2Bytes[0x9FE37D]);
            Assert.Equal((byte)'s', k2Bytes[0x9FE37E]);
            Assert.Equal(0x00, k2Bytes[0x9FE37F]);
            Assert.Equal((byte)'e', k2Bytes[0x9FE380]);
            Assert.Equal(0x00, k2Bytes[0x9FE381]);
            Assert.Equal((byte)'r', k2Bytes[0x9FE382]);
            Assert.Equal(0x00, k2Bytes[0x9FE383]);
        }
    }

    [Fact]
    public void EnginePatchService_WindowsTimerResolution_CanEnableAndDisposeCleanly()
    {
        // Act & Assert
        using WindowsTimerResolution timer = WindowsTimerResolution.Enable();
        if (OperatingSystem.IsWindows()) Assert.True(timer.IsActive);
    }

    [Fact]
    public void EnginePatchService_IsUserDirectoryPatched_DetectsPatchedAndUnpatchedBuffers()
    {
        byte[] mockBuffer = new byte[0xA00000];
        Assert.False(EnginePatchService.IsUserDirectoryPatched(mockBuffer));

        byte[] userDirPattern =
        [
            0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
            0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
            0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
            0x3C, 0x40, 0x00, 0x75, 0xF5, 0x48, 0x8D, 0x55, 0x90, 0x48, 0x8B, 0xCE, 0xE8, 0xD5, 0x58, 0x8A,
            0xFF, 0xEB
        ];
        Buffer.BlockCopy(userDirPattern, 0, mockBuffer, 0x9FE37A, userDirPattern.Length);

        Assert.False(EnginePatchService.IsUserDirectoryPatched(mockBuffer));
        Assert.True(EnginePatchService.RedirectUserDirectory(mockBuffer));
        Assert.True(EnginePatchService.IsUserDirectoryPatched(mockBuffer));
    }

    [Fact]
    public void EnginePatchService_LocationGuard_IgnoresUserdataDirectoryWithFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LocationGuard_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create WILLOWMAKER executable
            string exeName = DeploymentManifest.ApplicationExecutableFileName;
            File.WriteAllBytes(Path.Combine(tempDir, exeName), [0x4D, 0x5A]);

            // Create userdata directory with nested files
            string usergameLogs = Path.Combine(tempDir, "userdata", "game", "logs");
            Directory.CreateDirectory(usergameLogs);
            File.WriteAllText(Path.Combine(usergameLogs, "console.log"), "Log initialized");

            // Assess safety
            LocationGuard.Result result = LocationGuard.AssessLocationSafety(tempDir);

            // Must NOT flag userdata as foreign entry
            Assert.DoesNotContain("userdata/**", result.ForeignEntries);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnginePatchService_LocationGuard_ResolvesGameClientDirectoryFromDeepBuildPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string? resolved = LocationGuard.ResolveGameClientDirectory(baseDir);

        // Should resolve the repo's games/client directory containing hon_x64.exe
        Assert.NotNull(resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.True(File.Exists(Path.Combine(resolved, DeploymentManifest.HeroesOfNewerthExecutableFileName)));
    }

    [Fact]
    public void LocationGuard_AssessLocationSafety_ReturnsSafeWhenClientDirDiscovered()
    {
        string baseDir = AppContext.BaseDirectory;
        LocationGuard.Result result = LocationGuard.AssessLocationSafety(baseDir);

        Assert.Equal(LocationSafetyVerdict.Safe, result.Verdict);
        Assert.Empty(result.ForeignEntries);
    }

    [Fact]
    public void LauncherSettings_DefaultCdnAndUpdates_DisabledInPreparationMode()
    {
        LauncherSettingsModel settings = new();
        Assert.True(settings.EnableCdnSync);
        Assert.False(settings.EnableAutoUpdates);
    }

    [Fact]
    public void RedirectUserDirectory_SmallAndEmptyBuffers_HandledSafelyWithoutException()
    {
        byte[] empty = [];
        Assert.False(EnginePatchService.RedirectUserDirectory(empty));
        Assert.False(EnginePatchService.IsUserDirectoryPatched(empty));

        byte[] small = new byte[10];
        Assert.False(EnginePatchService.RedirectUserDirectory(small));
        Assert.False(EnginePatchService.IsUserDirectoryPatched(small));
    }

    [Fact]
    public void EnginePatchService_EnsureUserDirectoryInitialized_CreatesAllRequiredSubdirectories()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UserDirInit_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel.EnsureUserDirectoryInitialized(tempDir);

            string userdata = Path.Combine(tempDir, "userdata");
            Assert.True(Directory.Exists(userdata));
            Assert.True(Directory.Exists(Path.Combine(userdata, "game")));
            Assert.True(Directory.Exists(Path.Combine(userdata, "game", "logs")));
            Assert.True(Directory.Exists(Path.Combine(userdata, "game", "replays")));
            Assert.True(Directory.Exists(Path.Combine(userdata, "game", "shaders")));
            Assert.True(Directory.Exists(Path.Combine(userdata, "editor")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnginePatchService_EnsureEnginePatched_PatchesLinuxAndMacBinariesAtKnownOffsets()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_CrossPlatform_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Linux libk2-x86_64.so (offset 0x01533D10)
            int linuxOffset = 0x01533D10;
            byte[] linuxBytes = new byte[linuxOffset + 0x100];
            linuxBytes[linuxOffset] = 0x00;
            linuxBytes[linuxOffset + 1] = 0x00;
            linuxBytes[linuxOffset + 2] = 0x7A;
            linuxBytes[linuxOffset + 3] = 0x43; // 250.0f
            string linuxPath = Path.Combine(tempDir, "libk2-x86_64.so");
            File.WriteAllBytes(linuxPath, linuxBytes);

            // macOS libk2.dylib (offset 0x01614B8C)
            int macOffset = 0x01614B8C;
            byte[] macBytes = new byte[macOffset + 0x100];
            macBytes[macOffset] = 0x00;
            macBytes[macOffset + 1] = 0x00;
            macBytes[macOffset + 2] = 0x7A;
            macBytes[macOffset + 3] = 0x43; // 250.0f
            string macPath = Path.Combine(tempDir, "libk2.dylib");
            File.WriteAllBytes(macPath, macBytes);

            // Act
            bool result = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(result);

            byte[] patchedLinux = File.ReadAllBytes(linuxPath);
            Assert.Equal(0x00, patchedLinux[linuxOffset]);
            Assert.Equal(0x00, patchedLinux[linuxOffset + 1]);
            Assert.Equal(0x7A, patchedLinux[linuxOffset + 2]);
            Assert.Equal(0x44, patchedLinux[linuxOffset + 3]); // 1000.0f

            byte[] patchedMac = File.ReadAllBytes(macPath);
            Assert.Equal(0x00, patchedMac[macOffset]);
            Assert.Equal(0x00, patchedMac[macOffset + 1]);
            Assert.Equal(0x7A, patchedMac[macOffset + 2]);
            Assert.Equal(0x43, patchedMac[macOffset + 3]); // Preserved by default (OBL-01 Apple CodeDirectory integrity)

            // Opt-in override without mutating process-wide environment variables across parallel test classes
            Assert.True(EnginePatchService.EnsureEnginePatched(tempDir, patchMacOsDylibsOverride: true));
            byte[] optInMac = File.ReadAllBytes(macPath);
            Assert.Equal(0x44, optInMac[macOffset + 3]); // Patched when macOS dylib opt-in is enabled

            // Idempotent second pass
            bool secondPass = EnginePatchService.EnsureEnginePatched(tempDir);
            Assert.True(secondPass);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnginePatchService_LocationGuard_IgnoresRtxConfigurationAndLogs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LocationGuard_Rtx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string exeName = DeploymentManifest.ApplicationExecutableFileName;
            File.WriteAllBytes(Path.Combine(tempDir, exeName), [0x4D, 0x5A]);

            File.WriteAllText(Path.Combine(tempDir, "rtx.conf"), "rtx.enable = True");
            File.WriteAllText(Path.Combine(tempDir, "rtx.log"), "RTX log output");

            LocationGuard.Result result = LocationGuard.AssessLocationSafety(tempDir);

            Assert.DoesNotContain("rtx.conf", result.ForeignEntries);
            Assert.DoesNotContain("rtx.log", result.ForeignEntries);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureEnginePatched_PatchesVidDriver_ChangeDisplaySettingsWBypass()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_VidDriver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Prepare mock vid_d3d9_x64.dll
            int minSize = EnginePatchService.VidDriverOffset + 0x100;
            byte[] vidBytes = new byte[minSize];

            // Vanilla bytes at 0x0005B0D2: 85 C0 74 6D
            vidBytes[EnginePatchService.VidDriverOffset] = 0x85;
            vidBytes[EnginePatchService.VidDriverOffset + 1] = 0xC0;
            vidBytes[EnginePatchService.VidDriverOffset + 2] = 0x74;
            vidBytes[EnginePatchService.VidDriverOffset + 3] = 0x6D;

            Assert.False(EnginePatchService.IsVidDriverPatched(vidBytes));

            string vidPath = Path.Combine(tempDir, "vid_d3d9_x64.dll");
            File.WriteAllBytes(vidPath, vidBytes);

            // Act - EnsureEnginePatched
            bool success = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(success);
            Assert.True(File.Exists(vidPath + ".bak"), "Backup .bak must be created before modifying vid_d3d9_x64.dll.");

            byte[] patchedBytes = File.ReadAllBytes(vidPath);
            Assert.True(EnginePatchService.IsVidDriverPatched(patchedBytes));
            Assert.Equal(0x85, patchedBytes[EnginePatchService.VidDriverOffset]);
            Assert.Equal(0xC0, patchedBytes[EnginePatchService.VidDriverOffset + 1]);
            Assert.Equal(0xEB, patchedBytes[EnginePatchService.VidDriverOffset + 2]); // Patched from 0x74 to 0xEB (jmp)
            Assert.Equal(0x6D, patchedBytes[EnginePatchService.VidDriverOffset + 3]);

            // Idempotent re-run
            bool secondRun = EnginePatchService.EnsureEnginePatched(tempDir);
            Assert.True(secondRun);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnginePatchService_IsK2Patched_RecognizesServerFloatOffset()
    {
        // Dedicated server k2_x64.dll has 1000 FPS float constant at 0x00F6913C
        int minSize = 0x00F6913C + 16;
        byte[] buffer = new byte[minSize];

        // Initially unpatched
        Assert.False(EnginePatchService.IsK2Patched(buffer));

        // Patch 1000.0f at server offset
        buffer[0x00F6913C] = 0x00;
        buffer[0x00F6913C + 1] = 0x00;
        buffer[0x00F6913C + 2] = 0x7A;
        buffer[0x00F6913C + 3] = 0x44;

        Assert.True(EnginePatchService.IsK2Patched(buffer));
    }

    [Fact]
    public void EnsureEnginePatched_PatchesVidDriverSetModeBypass_Successfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_VidDriverSetMode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Prepare mock vid_d3d9_x64.dll covering VidDriverSetModeBypassOffset
            int minSize = EnginePatchService.VidDriverSetModeBypassOffset + 0x100;
            byte[] vidBytes = new byte[minSize];

            // Vanilla bytes for VidDriverSetModeOriginal at 0x0005BED0
            Buffer.BlockCopy(EnginePatchService.VidDriverSetModeOriginal, 0, vidBytes,
                EnginePatchService.VidDriverSetModeBypassOffset, EnginePatchService.VidDriverSetModeOriginal.Length);

            Assert.False(EnginePatchService.IsVidDriverPatched(vidBytes));

            string vidPath = Path.Combine(tempDir, "vid_d3d9_x64.dll");
            File.WriteAllBytes(vidPath, vidBytes);

            // Act - EnsureEnginePatched
            bool success = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(success);
            Assert.True(File.Exists(vidPath + ".bak"), "Backup .bak must be created before modifying vid_d3d9_x64.dll.");

            byte[] patchedBytes = File.ReadAllBytes(vidPath);
            Assert.True(EnginePatchService.IsVidDriverPatched(patchedBytes));
            Assert.Equal(0xC3, patchedBytes[EnginePatchService.VidDriverSetModeBypassOffset]); // Patched to RET (0xC3)
            for (int i = 1; i < EnginePatchService.VidDriverSetModePatched.Length; i++)
            {
                Assert.Equal(0x90, patchedBytes[EnginePatchService.VidDriverSetModeBypassOffset + i]); // NOP
            }

            // Idempotent re-run
            bool secondRun = EnginePatchService.EnsureEnginePatched(tempDir);
            Assert.True(secondRun);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureEnginePatched_PatchesGameDll_BotMatchAutoConcedeGuards_Successfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_GameDll_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(gameDir);

        try
        {
            var practiceFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_AUTO_CONCEDE_PRACTICE_BOTS");
            var loadingFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_AUTO_CONCEDE_LOADING_GUARD");
            var heroSelectFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_BOT_HERO_SELECT_BYPASS");
            var disconnectGuardFix = EnginePatchService.RegisteredCrashFixes.First(f => f.Id == "CRASH_MATCHMAKING_DISCONNECT_GUARD");

            int fileSize = 0x100000;
            byte[] mockGameBytes = new byte[fileSize];

            // Embed original patterns at mock offsets
            int practiceOffset = 0x800A2;
            int loadingOffset = 0x825E1;
            int heroSelectOffset = 0x69644;
            int disconnectOffset = 0x82C81;

            Buffer.BlockCopy(practiceFix.OriginalBytes, 0, mockGameBytes, practiceOffset, practiceFix.OriginalBytes.Length);
            Buffer.BlockCopy(loadingFix.OriginalBytes, 0, mockGameBytes, loadingOffset, loadingFix.OriginalBytes.Length);
            Buffer.BlockCopy(heroSelectFix.OriginalBytes, 0, mockGameBytes, heroSelectOffset, heroSelectFix.OriginalBytes.Length);
            Buffer.BlockCopy(disconnectGuardFix.OriginalBytes, 0, mockGameBytes, disconnectOffset, disconnectGuardFix.OriginalBytes.Length);

            Assert.False(EnginePatchService.IsCrashFixApplied(mockGameBytes, practiceFix));
            Assert.False(EnginePatchService.IsCrashFixApplied(mockGameBytes, loadingFix));
            Assert.False(EnginePatchService.IsCrashFixApplied(mockGameBytes, heroSelectFix));
            Assert.False(EnginePatchService.IsCrashFixApplied(mockGameBytes, disconnectGuardFix));

            string gameDllPath = Path.Combine(gameDir, "game_x64.dll");
            File.WriteAllBytes(gameDllPath, mockGameBytes);

            // Act - EnsureEnginePatched
            bool success = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(success);
            Assert.True(File.Exists(gameDllPath + ".bak"), "Backup .bak must be created before modifying game_x64.dll.");

            byte[] patchedBytes = File.ReadAllBytes(gameDllPath);
            Assert.True(EnginePatchService.IsCrashFixApplied(patchedBytes, practiceFix));
            Assert.True(EnginePatchService.IsCrashFixApplied(patchedBytes, loadingFix));
            Assert.True(EnginePatchService.IsCrashFixApplied(patchedBytes, heroSelectFix));
            Assert.True(EnginePatchService.IsCrashFixApplied(patchedBytes, disconnectGuardFix));

            // Verify patched bytes match expected
            byte[] actualPractice = new byte[practiceFix.PatchedBytes.Length];
            Buffer.BlockCopy(patchedBytes, practiceOffset, actualPractice, 0, practiceFix.PatchedBytes.Length);
            Assert.Equal(practiceFix.PatchedBytes, actualPractice);

            byte[] actualLoading = new byte[loadingFix.PatchedBytes.Length];
            Buffer.BlockCopy(patchedBytes, loadingOffset, actualLoading, 0, loadingFix.PatchedBytes.Length);
            Assert.Equal(loadingFix.PatchedBytes, actualLoading);

            byte[] actualHeroSelect = new byte[heroSelectFix.PatchedBytes.Length];
            Buffer.BlockCopy(patchedBytes, heroSelectOffset, actualHeroSelect, 0, heroSelectFix.PatchedBytes.Length);
            Assert.Equal(heroSelectFix.PatchedBytes, actualHeroSelect);

            byte[] actualDisconnect = new byte[disconnectGuardFix.PatchedBytes.Length];
            Buffer.BlockCopy(patchedBytes, disconnectOffset, actualDisconnect, 0, disconnectGuardFix.PatchedBytes.Length);
            Assert.Equal(disconnectGuardFix.PatchedBytes, actualDisconnect);

            // Idempotent re-run
            bool secondRun = EnginePatchService.EnsureEnginePatched(tempDir);
            Assert.True(secondRun);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureEnginePatched_VidDriverPatching_IsStrictlyIdempotentAndDoesNotCorruptUnrelatedPrologues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_VidDriverMultiPrologues_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            int minSize = EnginePatchService.VidDriverSetModeBypassOffset + 0x1000;
            byte[] vidBytes = new byte[minSize];

            // Setup ChangeDisplaySettingsW failure at VidDriverOffset (0x0005B0D0)
            vidBytes[EnginePatchService.VidDriverOffset] = 0x85;
            vidBytes[EnginePatchService.VidDriverOffset + 1] = 0xC0;
            vidBytes[EnginePatchService.VidDriverOffset + 2] = 0x74;
            vidBytes[EnginePatchService.VidDriverOffset + 3] = 0x6D;

            // Setup SetMode failure at VidDriverSetModeBypassOffset (0x0005BED0)
            Buffer.BlockCopy(EnginePatchService.VidDriverSetModeOriginal, 0, vidBytes,
                EnginePatchService.VidDriverSetModeBypassOffset, EnginePatchService.VidDriverSetModeOriginal.Length);

            // Add multiple unrelated functions with the same 10-byte prologue elsewhere in the buffer
            int[] unrelatedOffsets = [0x00018000, 0x00020000, 0x00030000, 0x00040000];
            foreach (int offset in unrelatedOffsets)
            {
                Buffer.BlockCopy(EnginePatchService.VidDriverSetModeOriginal, 0, vidBytes,
                    offset, EnginePatchService.VidDriverSetModeOriginal.Length);
            }

            string vidPath = Path.Combine(tempDir, "vid_d3d9_x64.dll");
            File.WriteAllBytes(vidPath, vidBytes);

            // Run 5 iterations of EnsureEnginePatched (simulating 5 client launches)
            for (int run = 0; run < 5; run++)
            {
                bool success = EnginePatchService.EnsureEnginePatched(tempDir);
                Assert.True(success);
            }

            byte[] patchedBytes = File.ReadAllBytes(vidPath);

            // Verify the two target offsets were patched properly
            Assert.Equal(0xC3, patchedBytes[EnginePatchService.VidDriverSetModeBypassOffset]);
            Assert.Equal(0xEB, patchedBytes[EnginePatchService.VidDriverOffset + 2]);

            // Crucial: ALL unrelated function prologues MUST remain completely untouched!
            foreach (int offset in unrelatedOffsets)
            {
                for (int i = 0; i < EnginePatchService.VidDriverSetModeOriginal.Length; i++)
                {
                    Assert.Equal(EnginePatchService.VidDriverSetModeOriginal[i], patchedBytes[offset + i]);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnginePatchService_ApplyCrashFix_WithShiftedOffset_LocatesAndPatchesViaAob()
    {
        // Arrange: Select a crash fix and shift its location by +0x200 bytes away from KnownOffset
        var fix = EnginePatchService.RegisteredCrashFixes[0]; // CRASH_SNAPSHOT_DELTA_NULL
        int shiftedOffset = fix.KnownOffset + 0x200;
        byte[] buffer = new byte[shiftedOffset + fix.OriginalBytes.Length + 0x1000];

        // Put the original bytes at the shifted offset instead of KnownOffset
        Buffer.BlockCopy(fix.OriginalBytes, 0, buffer, shiftedOffset, fix.OriginalBytes.Length);

        // Act: Apply crash fix
        bool applied = EnginePatchService.ApplyCrashFix(buffer, fix);

        // Assert: AOB scanner found the shifted pattern and applied PatchedBytes
        Assert.True(applied);
        for (int i = 0; i < fix.PatchedBytes.Length; i++)
        {
            Assert.Equal(fix.PatchedBytes[i], buffer[shiftedOffset + i]);
        }

        // Assert: Idempotent second run
        bool reapplied = EnginePatchService.ApplyCrashFix(buffer, fix);
        Assert.False(reapplied);
    }

    [Fact]
    public void EnginePatchService_UserDirectoryRedirection_WithShiftedOffset_LocatesAndPatchesViaAob()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "EnginePatch_AobShift_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            int fileSize = 0xFB5000;
            byte[] mockK2Bytes = new byte[fileSize];

            // Put float constant at known offset
            mockK2Bytes[0xFB416C] = 0x00;
            mockK2Bytes[0xFB416D] = 0x00;
            mockK2Bytes[0xFB416E] = 0x7A;
            mockK2Bytes[0xFB416F] = 0x43;

            // Shift UserDir pattern by +0x400 bytes from default 0x9FE37A
            int shiftedUserDirOffset = 0x9FE37A + 0x400;
            byte[] userDirPattern =
            [
                0x48, 0x8D, 0x45, 0x90, 0x48, 0x89, 0x44, 0x24, 0x20, 0x45, 0x33, 0xC9, 0x45, 0x33, 0xC0, 0x41,
                0x8D, 0x51, 0x05, 0x33, 0xC9, 0xFF, 0x15, 0x73, 0xC9, 0x40, 0x00, 0x85, 0xC0, 0x75, 0x24, 0x48,
                0x8D, 0x45, 0x90, 0x49, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0xFF, 0xC0, 0x66, 0x42, 0x83,
                0x3C, 0x40, 0x00, 0x75, 0xF5
            ];
            Buffer.BlockCopy(userDirPattern, 0, mockK2Bytes, shiftedUserDirOffset, userDirPattern.Length);

            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, mockK2Bytes);

            // Act
            bool result = EnginePatchService.EnsureEnginePatched(tempDir);

            // Assert
            Assert.True(result);
            byte[] patchedBytes = File.ReadAllBytes(k2Path);

            // Verify user directory redirection was successfully applied at shifted offset
            Assert.Equal(0x48, patchedBytes[shiftedUserDirOffset]);
            Assert.Equal(0xB8, patchedBytes[shiftedUserDirOffset + 1]);
            Assert.Equal((byte)'u', patchedBytes[shiftedUserDirOffset + 2]);
            Assert.Equal(0x00, patchedBytes[shiftedUserDirOffset + 3]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}



