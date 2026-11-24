// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class LauncherSettingsTests
{
    [Fact]
    public void LauncherSettingsService_SaveAndLoad_RoundTripsCorrectly()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_Settings_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = new()
            {
                EnableRayTracing = true,
                RefreshRate = "360 Hz",
                ShadowFidelity = "High (2048)",
                VulkanHudMode = "Pro Diagnostics",
                EnableNvidiaReflex = true,
                EnableDirectFlip = true,
                PresentationMode = "DXVK Mailbox (Low-Latency)",
                SwapchainBackend = "D3D9On12 (DirectX 12)",
                MasterServerAddress = "api.kongor.net",
                CustomMasterServerAddress = "test.server.io"
            };

            // Act
            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);

            // Assert
            Assert.True(loaded.EnableRayTracing);
            Assert.Equal("360 Hz", loaded.RefreshRate);
            Assert.Equal("High (2048)", loaded.ShadowFidelity);
            Assert.Equal("Pro Diagnostics", loaded.VulkanHudMode);
            Assert.True(loaded.EnableNvidiaReflex);
            Assert.True(loaded.EnableDirectFlip);
            Assert.Equal("DXVK Mailbox (Low-Latency)", loaded.PresentationMode);
            Assert.Equal("D3D9On12 (DirectX 12)", loaded.SwapchainBackend);
            Assert.Equal("api.kongor.net", loaded.MasterServerAddress);
            Assert.Equal("test.server.io", loaded.CustomMasterServerAddress);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_WriteCustomConfiguration_ReflectsSelectedHzAndShadows()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_Config_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                SelectedRefreshRate = "360 Hz",
                SelectedShadowFidelity = "High (2048)"
            };

            // Act
            viewModel.WriteCustomConfiguration(tempDir);

            // Assert
            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));

            string content = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"vid_refreshRate\" \"360\"", content);
            Assert.Contains("SetSave \"vid_shadowmapSize\" \"2048\"", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_WriteCustomConfiguration_DirectFlipEnabled_PreservesInGameVsyncChoiceAndSeedsZeroLagFirstRun()
    {
        // protects: Zero-lag first-run defaults + persistence of player's in-game HoN VSync checkbox across restarts
        // fails_when: autoexec.cfg or startup.cfg clobbers d3d_presentInterval on every DirectFlip launch
        // seam: Real filesystem writes via MainViewModel.WriteCustomConfiguration + ConfigureChildClientEnvironment
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_DirectFlip_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel viewModel = new()
            {
                SelectedDisplayMode = "Exclusive Fullscreen",
                EnableDirectFlip = true,
                SelectedPresentationMode = "DirectFlip (FLIP_DISCARD)"
            };

            // Act 1: First-run initialization in DirectFlip mode
            viewModel.WriteCustomConfiguration(tempDir);

            // Assert 1: autoexec.cfg omits d3d_presentInterval clobber; canonical startup.cfg seeds 0/false
            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));

            string content = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"vid_borderless\" \"false\"", content);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\"", content);
            Assert.DoesNotContain("SetSave \"d3d_presentInterval\"", content);
            Assert.DoesNotContain("SetSave \"d3d_tripleBuffering\"", content);

            string canonicalStartupPath = OperatingSystem.IsLinux()
                ? Path.Combine(tempDir, "userdata", ".Heroes of Newerth", "game", "startup.cfg")
                : Path.Combine(tempDir, "userdata", "Heroes of Newerth x64", "game", "startup.cfg");
            Assert.True(File.Exists(canonicalStartupPath));
            string firstRunStartup = File.ReadAllText(canonicalStartupPath);
            Assert.Contains("SetSave \"d3d_presentInterval\" \"0\"", firstRunStartup);
            Assert.Contains("SetSave \"gl_swapInterval\" \"0\"", firstRunStartup);
            Assert.Contains("SetSave \"d3d_tripleBuffering\" \"false\"", firstRunStartup);

            // Act 2: Player toggles VSync ON inside HoN Video Options (saving "1" into startup.cfg) and relaunches
            string playerToggledStartup = firstRunStartup
                .Replace("SetSave \"d3d_presentInterval\" \"0\"", "SetSave \"d3d_presentInterval\" \"1\"")
                .Replace("SetSave \"vid_vsync\" \"false\"", "SetSave \"vid_vsync\" \"true\"");
            File.WriteAllText(canonicalStartupPath, playerToggledStartup);
            viewModel.WriteCustomConfiguration(tempDir);

            string persistedStartup = File.ReadAllText(canonicalStartupPath);
            Assert.Contains("SetSave \"d3d_presentInterval\" \"1\"", persistedStartup);
            Assert.Contains("SetSave \"vid_vsync\" \"true\"", persistedStartup);

            // Act 3: Switching explicitly to DXVK Mailbox & Legacy modes updates autoexec.cfg pacing
            viewModel.SelectedPresentationMode = "DXVK Mailbox (Low-Latency)";
            viewModel.WriteCustomConfiguration(tempDir);
            string mailboxAutoexec = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"d3d_presentInterval\" \"0\"", mailboxAutoexec);
            Assert.Contains("SetSave \"d3d_tripleBuffering\" \"false\"", mailboxAutoexec);

            viewModel.SelectedPresentationMode = "Legacy Blit (DWM Composited)";
            viewModel.WriteCustomConfiguration(tempDir);
            string legacyAutoexec = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"d3d_presentInterval\" \"1\"", legacyAutoexec);
            Assert.Contains("SetSave \"d3d_tripleBuffering\" \"true\"", legacyAutoexec);

            // Act 4: Verify child environment exports DXVK 3.0.2 DXVK_SHADER_CACHE_PATH
            System.Diagnostics.ProcessStartInfo psi = new("hon_x64.exe");
            viewModel.ConfigureChildClientEnvironment(psi, tempDir, isWindowsOverride: true);
            string expectedDxvkCache = Path.Combine(tempDir, "userdataKONGOR", "dxvk_cache");
            Assert.Equal("1", psi.EnvironmentVariables["DXVK_SHADER_CACHE"]);
            Assert.Equal(expectedDxvkCache, psi.EnvironmentVariables["DXVK_SHADER_CACHE_PATH"]);
            Assert.Equal(expectedDxvkCache, psi.EnvironmentVariables["DXVK_STATE_CACHE_PATH"]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_WriteCustomConfiguration_SynchronizesDisplayModeAcrossStartupAndGuidProfiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_SyncTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(gameDir);
        string userGameDir = Path.Combine(tempDir, "userdata", "Heroes of Newerth x64", "game");
        Directory.CreateDirectory(userGameDir);

        try
        {
            // Seed canonical startup.cfg in userdata/Heroes of Newerth x64/game/
            string canonicalStartup = Path.Combine(userGameDir, "startup.cfg");
            File.WriteAllText(canonicalStartup, "SetSave \"vid_borderless\" \"true\" \"0\"\nSetSave \"vid_fullscreen\" \"false\" \"0\"\nSetSave \"vid_resolution\" \"1280,720\" \"0\"\n");

            // Seed user GUID profile config with keybindings
            string guidProfile = Path.Combine(userGameDir, "30e3278e-7178-48e6-b4c4-bbea813864fc.cfg");
            File.WriteAllText(guidProfile, "SetSave \"keybind_attack\" \"A\" \"0\"\nSetSave \"vid_borderless\" \"true\" \"0\"\nSetSave \"vid_fullscreen\" \"false\" \"0\"\nSetSave \"cg_chatMacro1\" \"help\" \"0\"\n");

            MainViewModel viewModel = new()
            {
                SelectedDisplayMode = "Exclusive Fullscreen",
                SelectedResolution = "1920x1080 (1080p FHD)",
                SelectedRefreshRate = "144 Hz"
            };

            // Act 1: Sync Exclusive Fullscreen
            viewModel.WriteCustomConfiguration(tempDir);

            // Assert 1: canonical startup.cfg updated
            string canonicalContent = File.ReadAllText(canonicalStartup);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\" \"0\"", canonicalContent);
            Assert.Contains("SetSave \"vid_borderless\" \"false\" \"0\"", canonicalContent);
            Assert.Contains("SetSave \"vid_resolution\" \"1920,1080\" \"0\"", canonicalContent);
            Assert.Contains("SetSave \"vid_refreshRate\" \"144\" \"0\"", canonicalContent);

            // Assert 3: GUID profile updated without losing keybindings or appending unwanted lines
            string guidContent = File.ReadAllText(guidProfile);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\" \"0\"", guidContent);
            Assert.Contains("SetSave \"vid_borderless\" \"false\" \"0\"", guidContent);
            Assert.Contains("SetSave \"keybind_attack\" \"A\" \"0\"", guidContent);
            Assert.Contains("SetSave \"cg_chatMacro1\" \"help\" \"0\"", guidContent);
            Assert.True(File.Exists(guidProfile + ".bak"), "Backup .bak must be created for GUID profile");

            // Assert 4: dxvk.conf has allowFse in Exclusive Fullscreen (isolated in userdataKONGOR)
            string dxvkPath = Path.Combine(tempDir, "userdataKONGOR", "dxvk.conf");
            Assert.True(File.Exists(dxvkPath));
            string dxvkContent = File.ReadAllText(dxvkPath);
            Assert.Contains("dxvk.allowFse = false", dxvkContent);
            Assert.Contains("SetSave \"d3d_exclusive\" \"false\" \"0\"", canonicalContent);

            // Act 2: Switch back to Borderless Windowed
            viewModel.SelectedDisplayMode = "Borderless Windowed";
            viewModel.WriteCustomConfiguration(tempDir);

            // Assert 5: Borderless Windowed uses non-exclusive fullscreen scanout (vid_fullscreen=true, vid_borderless=true, d3d_exclusive=false)
            string canonicalContent2 = File.ReadAllText(canonicalStartup);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\" \"0\"", canonicalContent2);
            Assert.Contains("SetSave \"vid_borderless\" \"true\" \"0\"", canonicalContent2);

            guidContent = File.ReadAllText(guidProfile);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\" \"0\"", guidContent);
            Assert.Contains("SetSave \"vid_borderless\" \"true\" \"0\"", guidContent);
            Assert.Contains("SetSave \"keybind_attack\" \"A\" \"0\"", guidContent);

            // Act 3: Switch to Windowed mode -> vid_fullscreen=false, vid_borderless=false
            viewModel.SelectedDisplayMode = "Windowed";
            viewModel.WriteCustomConfiguration(tempDir);
            string canonicalWindowed = File.ReadAllText(canonicalStartup);
            Assert.Contains("SetSave \"vid_fullscreen\" \"false\" \"0\"", canonicalWindowed);
            Assert.Contains("SetSave \"vid_borderless\" \"false\" \"0\"", canonicalWindowed);

            if (File.Exists(dxvkPath))
            {
                string dxvkWindowedContent = File.ReadAllText(dxvkPath);
                Assert.DoesNotContain("dxvk.allowFse = true", dxvkWindowedContent);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }


    [Fact]
    public void LauncherSettingsModel_EnabledModIds_RoundTripsCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_Mods_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = new()
            {
                FavoriteModIds = ["minimap_pro", "tower_ranges"],
                EnabledModIds = ["minimap_pro", "smash_announcer", "custom_avatar_s2z"]
            };

            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);

            Assert.Equal(2, loaded.FavoriteModIds.Count);
            Assert.Contains("minimap_pro", loaded.FavoriteModIds);
            Assert.Contains("tower_ranges", loaded.FavoriteModIds);

            Assert.Equal(3, loaded.EnabledModIds.Count);
            Assert.Contains("minimap_pro", loaded.EnabledModIds);
            Assert.Contains("smash_announcer", loaded.EnabledModIds);
            Assert.Contains("custom_avatar_s2z", loaded.EnabledModIds);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_SaveLauncherSettings_PreservesEmptyFavorites_WhenModsLoaded()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_EmptyFav_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel initial = new()
            {
                FavoriteModIds = ["old_fav1", "old_fav2"],
                EnabledModIds = ["mod1"]
            };
            LauncherSettingsService.Save(initial, tempDir);

            MainViewModel vm = new();
            vm.ModManager = new ModManagerViewModel(autoLoad: false) { SettingsDirectory = tempDir };
            vm.ModManager.Mods.Add(new ModItemViewModel { Id = "mod1", Name = "Mod 1", IsFavorite = false, IsEnabled = true });
            vm.ModManager.Mods.Add(new ModItemViewModel { Id = "mod2", Name = "Mod 2", IsFavorite = false, IsEnabled = false });
            vm.ModManager.UpdateFavorites();

            vm.SaveLauncherSettings(tempDir);

            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);
            Assert.Empty(loaded.FavoriteModIds);
            Assert.Single(loaded.EnabledModIds);
            Assert.Contains("mod1", loaded.EnabledModIds);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_MasterServerAddress_PersistsAndRestoresAcrossSessions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_MasterServer_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm1 = new();
            vm1.MasterServerAddress = new Avalonia.Controls.ComboBoxItem { Content = "api.kongor.net" };
            vm1.SaveLauncherSettings(tempDir);

            LauncherSettingsModel loadedModel = LauncherSettingsService.Load(tempDir);
            Assert.Equal("api.kongor.net", loadedModel.MasterServerAddress);

            // With PKV Guard active and kongor.net disabled, legacy kongor.net migrates to newbs.io
            MainViewModel vm2 = new();
            vm2.LoadLauncherSettings(tempDir);
            Assert.Equal(EndpointPairRegistry.MasterServerNewbsIo, vm2.LoadedMasterServerAddress);

            // When kongor.net PKV is explicitly enabled, it restores verbatim
            try
            {
                EndpointPairRegistry.IsKongorNetPkvEnabled = true;
                MainViewModel vm3 = new();
                vm3.LoadLauncherSettings(tempDir);
                Assert.Equal("api.kongor.net", vm3.LoadedMasterServerAddress);
            }
            finally
            {
                EndpointPairRegistry.IsKongorNetPkvEnabled = false;
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_CustomMasterServerAddress_PersistsAndRestoresCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_CustomServer_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm1 = new();
            vm1.MasterServerAddress = new Avalonia.Controls.ComboBoxItem { Content = "Custom Address ..." };
            vm1.CustomMasterServerAddress = "192.168.1.150:55555";
            vm1.SaveLauncherSettings(tempDir);

            LauncherSettingsModel loadedModel = LauncherSettingsService.Load(tempDir);
            Assert.Equal("Custom Address ...", loadedModel.MasterServerAddress);
            Assert.Equal("192.168.1.150:55555", loadedModel.CustomMasterServerAddress);

            MainViewModel vm2 = new();
            vm2.LoadLauncherSettings(tempDir);
            Assert.Equal("Custom Address ...", vm2.LoadedMasterServerAddress);
            Assert.Equal("192.168.1.150:55555", vm2.CustomMasterServerAddress);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LauncherSettingsModel_DisplayModeAndRcas_RoundTripsCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_DisplayMode_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = new()
            {
                DisplayMode = "Borderless Windowed",
                EnableFidelityFxRcas = true,
                FidelityFxRcasSharpness = 0.45f
            };

            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);

            Assert.Equal("Borderless Windowed", loaded.DisplayMode);
            Assert.True(loaded.EnableFidelityFxRcas);
            Assert.Equal(0.45f, loaded.FidelityFxRcasSharpness, precision: 2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void MainViewModel_DisplayMode_PersistsAndWritesCorrectCvars()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_DisplayModeCvars_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Test 1: Borderless Windowed (non-exclusive borderless fullscreen: vid_borderless=true, vid_fullscreen=true, d3d_exclusive=false)
            MainViewModel vmBorderless = new()
            {
                SelectedDisplayMode = "Borderless Windowed"
            };
            vmBorderless.WriteCustomConfiguration(tempDir);

            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));
            string contentBorderless = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"vid_borderless\" \"true\"", contentBorderless);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\"", contentBorderless);
            Assert.Contains("SetSave \"d3d_exclusive\" \"false\"", contentBorderless);

            // Test 1b: Windowed
            MainViewModel vmWindowed = new()
            {
                SelectedDisplayMode = "Windowed"
            };
            vmWindowed.WriteCustomConfiguration(tempDir);
            string contentWindowed = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"vid_borderless\" \"false\"", contentWindowed);
            Assert.Contains("SetSave \"vid_fullscreen\" \"false\"", contentWindowed);

            // Test 2: Exclusive Fullscreen
            MainViewModel vmFullscreen = new()
            {
                SelectedDisplayMode = "Exclusive Fullscreen"
            };
            vmFullscreen.WriteCustomConfiguration(tempDir);
            string contentFullscreen = File.ReadAllText(autoexecPath);
            Assert.Contains("SetSave \"vid_borderless\" \"false\"", contentFullscreen);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\"", contentFullscreen);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void MainViewModel_FidelityFxRcas_PersistsAndFormatsSharpnessText()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_Rcas_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm1 = new();
            vm1.EnableFidelityFxRcas = true;
            vm1.FidelityFxRcasSharpness = 0.65f;
            Assert.Equal("65%", vm1.FidelityFxRcasSharpnessText);

            vm1.SaveLauncherSettings(tempDir);

            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);
            Assert.True(loaded.EnableFidelityFxRcas);
            Assert.Equal(0.65f, loaded.FidelityFxRcasSharpness, precision: 2);

            MainViewModel vm2 = new();
            vm2.LoadLauncherSettings(tempDir);
            Assert.True(vm2.EnableFidelityFxRcas);
            Assert.Equal(0.65f, vm2.FidelityFxRcasSharpness, precision: 2);
            Assert.Equal("65%", vm2.FidelityFxRcasSharpnessText);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LauncherSettingsModel_NetworkProfile_RoundTripsCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_NetProfile_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = new()
            {
                NetworkProfile = "Optimized 60-Tick (Broadband / Wi-Fi)"
            };

            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);

            Assert.Equal("Optimized 60-Tick (Broadband / Wi-Fi)", loaded.NetworkProfile);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void MainViewModel_NetworkProfile_WritesCorrect128TickAnd60TickCvars()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_NetCvars_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Test 1: 128-Tick Profile
            MainViewModel vm128 = new()
            {
                SelectedNetworkProfile = "Competitive 128-Tick (Esports / Fiber)"
            };
            vm128.WriteCustomConfiguration(tempDir);

            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));
            string content128 = File.ReadAllText(autoexecPath);

            Assert.Contains("SetSave \"cl_packetSendFPS\" \"85\"", content128);
            Assert.DoesNotContain("SetSave \"cl_packetSendFPS\" \"128\"", content128);
            Assert.Contains("SetSave \"net_FPS\" \"128\"", content128);
            Assert.Contains("SetSave \"net_maxPacketRate\" \"128\"", content128);
            Assert.Contains("SetSave \"net_maxBPS\" \"262144\"", content128);
            Assert.Contains("SetSave \"net_maxPacketSize\" \"1400\"", content128);
            Assert.Contains("SetSave \"cl_packetEpsilon\" \"0.0005\"", content128);
            Assert.Contains("SetSave \"cl_smoothGameTime\" \"true\"", content128);
            Assert.Contains("SetSave \"cl_lerp\" \"true\"", content128);
            Assert.Contains("SetSave \"cl_delta\" \"true\"", content128);
            Assert.Contains("SetSave \"cl_packetResendTime\" \"30\"", content128);
            Assert.Contains("SetSave \"cg_precacheResources\" \"false\"", content128);
            Assert.DoesNotContain("CreateString", content128);
            Assert.DoesNotContain("svr_proxyPort", content128);
            Assert.DoesNotContain("man_startServerPort", content128);

            // Test 2: 60-Tick Profile
            MainViewModel vm60 = new()
            {
                SelectedNetworkProfile = "Optimized 60-Tick (Broadband / Wi-Fi)"
            };
            vm60.WriteCustomConfiguration(tempDir);
            string content60 = File.ReadAllText(autoexecPath);

            Assert.Contains("SetSave \"cl_packetSendFPS\" \"60\"", content60);
            Assert.Contains("SetSave \"net_FPS\" \"60\"", content60);
            Assert.Contains("SetSave \"net_maxPacketRate\" \"60\"", content60);
            Assert.Contains("SetSave \"net_maxBPS\" \"131072\"", content60);
            Assert.Contains("SetSave \"net_maxPacketSize\" \"1400\"", content60);
            Assert.Contains("SetSave \"cl_packetEpsilon\" \"0.001\"", content60);
            Assert.Contains("SetSave \"cl_packetResendTime\" \"50\"", content60);
            Assert.Contains("SetSave \"cg_precacheResources\" \"false\"", content60);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(60, 60, "60 Hz")]
    [InlineData(144, 144, "144 Hz")]
    [InlineData(180, 180, "180 Hz")]
    [InlineData(300, 300, "300 Hz")]
    [InlineData(500, 500, "500 Hz")]
    [InlineData(30, 60, "60 Hz")]
    [InlineData(700, 500, "500 Hz")]
    public void HardwareDetection_GetFirstBootRefreshRate_ClampsAndFormatsCorrectly(int inputHz, int expectedHz, string expectedLabel)
    {
        var (hz, label) = HardwareDetectionService.GetFirstBootRefreshRate(inputHz);
        Assert.Equal(expectedHz, hz);
        Assert.Equal(expectedLabel, label);
    }

    [Theory]
    [InlineData(60, "60 Hz")]
    [InlineData(180, "180 Hz")]
    [InlineData(300, "300 Hz")]
    public void LauncherSettingsModel_CreateFirstBootDefaults_ProducesBorderlessWindowedAndNativeHz(int nativeHz, string expectedHzLabel)
    {
        LauncherSettingsModel defaults = LauncherSettingsModel.CreateFirstBootDefaults(nativeHz);

        Assert.Equal("Borderless Windowed", defaults.DisplayMode);
        Assert.Equal(expectedHzLabel, defaults.RefreshRate);
        Assert.Equal(nativeHz, defaults.FpsCap);
    }

    [Fact]
    public void LauncherSettingsService_Load_WhenMissing_ReturnsFirstBootDefaults()
    {
        string nonExistentDir = Path.Combine(Path.GetTempPath(), "NonExistentSettings_" + Guid.NewGuid().ToString("N"));
        LauncherSettingsModel loaded = LauncherSettingsService.Load(nonExistentDir);

        Assert.NotNull(loaded);
        Assert.Equal("Borderless Windowed", loaded.DisplayMode);
        Assert.EndsWith("Hz", loaded.RefreshRate);
        Assert.True(loaded.FpsCap >= 60 && loaded.FpsCap <= 500);
    }

    [Fact]
    public void MainViewModel_EnsureRefreshRateAvailable_InsertsSortedBeforeUncapped()
    {
        MainViewModel vm = new();

        Assert.Contains("165 Hz", vm.AvailableRefreshRates);
        Assert.Contains("240 Hz", vm.AvailableRefreshRates);
        Assert.DoesNotContain("180 Hz", vm.AvailableRefreshRates);

        vm.EnsureRefreshRateAvailable("180 Hz");
        int idx180 = vm.AvailableRefreshRates.IndexOf("180 Hz");
        int idx165 = vm.AvailableRefreshRates.IndexOf("165 Hz");
        int idx240 = vm.AvailableRefreshRates.IndexOf("240 Hz");
        int idxUncapped = vm.AvailableRefreshRates.IndexOf("Uncapped (Max FPS)");

        Assert.True(idx180 > idx165, "180 Hz must be after 165 Hz");
        Assert.True(idx180 < idx240, "180 Hz must be before 240 Hz");
        Assert.True(idx180 < idxUncapped, "180 Hz must be before Uncapped");

        vm.EnsureRefreshRateAvailable("300 Hz");
        int idx300 = vm.AvailableRefreshRates.IndexOf("300 Hz");
        int idx360 = vm.AvailableRefreshRates.IndexOf("360 Hz");

        Assert.True(idx300 > idx240, "300 Hz must be after 240 Hz");
        Assert.True(idx300 < idx360, "300 Hz must be before 360 Hz");

        int initialCount = vm.AvailableRefreshRates.Count;
        vm.EnsureRefreshRateAvailable("180 Hz");
        Assert.Equal(initialCount, vm.AvailableRefreshRates.Count);
    }

    [Fact]
    public void MainViewModel_DefaultDisplayMode_IsBorderlessWindowed()
    {
        MainViewModel vm = new();
        Assert.Equal("Borderless Windowed", vm.SelectedDisplayMode);
    }

    [Fact]
    public void P2pHostServerService_BuildDedicatedHostArguments_MountsClientPackageFiles_WhenPresent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "P2pHostArgsTest_" + Guid.NewGuid().ToString("N"));
        string cpfDir = Path.Combine(tempDir, "ClientPackageFiles");
        Directory.CreateDirectory(cpfDir);

        try
        {
            string argsWithCpf = global::WILLOWMAKER.Services.P2P.P2pHostServerService.BuildDedicatedHostArguments(tempDir, 11235, 11236, "TestServer");
            Assert.Contains("base;game;ClientPackageFiles", argsWithCpf);
            Assert.Contains(";override;server", argsWithCpf);

            Directory.Delete(cpfDir, true);
            string argsWithoutCpf = global::WILLOWMAKER.Services.P2P.P2pHostServerService.BuildDedicatedHostArguments(tempDir, 11235, 11236, "TestServer");
            Assert.DoesNotContain("ClientPackageFiles", argsWithoutCpf);
            Assert.Contains("-mod base;game", argsWithoutCpf);
            Assert.Contains(";override;server", argsWithoutCpf);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LauncherSettingsModel_CreateFirstBootDefaults_NonWindows_DefaultsToUncapped1000Fps()
    {
        LauncherSettingsModel defaults = LauncherSettingsModel.CreateFirstBootDefaults(detectedHzOverride: null, isWindowsOverride: false);
        Assert.Equal("Borderless Windowed", defaults.DisplayMode);
        Assert.Equal("Uncapped (Max FPS)", defaults.RefreshRate);
        Assert.Equal(1000, defaults.FpsCap);
    }

    [Fact]
    public void LauncherSettingsModel_CreateFirstBootDefaults_NonWindows_WithHzOverride_PreservesExplicitHz()
    {
        LauncherSettingsModel defaults = LauncherSettingsModel.CreateFirstBootDefaults(detectedHzOverride: 180, isWindowsOverride: false);
        Assert.Equal("Borderless Windowed", defaults.DisplayMode);
        Assert.Equal("180 Hz", defaults.RefreshRate);
        Assert.Equal(180, defaults.FpsCap);
    }

    [Fact]
    public void P2pHostServerService_EnsureSharedClientAssetsLinked_LinksValidAssetsAndSkipsBinariesAndTextures()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "P2pLinkTest_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempRoot, "client");
        string serverDir = Path.Combine(tempRoot, "server_p2p");
        string clientGameDir = Path.Combine(clientDir, "game");
        string serverGameDir = Path.Combine(serverDir, "game");

        Directory.CreateDirectory(clientGameDir);
        Directory.CreateDirectory(serverDir);

        try
        {
            // Seed eligible files
            File.WriteAllText(Path.Combine(clientGameDir, "resources0.s2z"), "dummy_s2z_data");
            File.WriteAllText(Path.Combine(clientGameDir, "game_settings_extra.cfg"), "set test 1");

            // Seed excluded files
            File.WriteAllText(Path.Combine(clientGameDir, "textures.s2z"), "textures_data");
            File.WriteAllText(Path.Combine(clientGameDir, "cgame-x86_64.so"), "elf_data");
            File.WriteAllText(Path.Combine(clientGameDir, "cgame.dylib"), "macho_data");
            File.WriteAllText(Path.Combine(clientGameDir, "game_x64.dll"), "pe_data");
            File.WriteAllText(Path.Combine(clientGameDir, "foo.s2z.bak"), "bak_data");

            // Act
            global::WILLOWMAKER.Services.P2P.P2pHostServerService.EnsureSharedClientAssetsLinked(serverDir, clientDir);

            // Assert: eligible assets must be linked / copied
            Assert.True(File.Exists(Path.Combine(serverGameDir, "resources0.s2z")));
            Assert.True(File.Exists(Path.Combine(serverGameDir, "game_settings_extra.cfg")));

            // Assert: excluded assets must be strictly skipped
            Assert.False(File.Exists(Path.Combine(serverGameDir, "textures.s2z")));
            Assert.False(File.Exists(Path.Combine(serverGameDir, "cgame-x86_64.so")));
            Assert.False(File.Exists(Path.Combine(serverGameDir, "cgame.dylib")));
            Assert.False(File.Exists(Path.Combine(serverGameDir, "game_x64.dll")));
            Assert.False(File.Exists(Path.Combine(serverGameDir, "foo.s2z.bak")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void LauncherSettingsService_SafeModeAndWatchdog_RoundTripsCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_SafeMode_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel model = new()
            {
                AutoRestartMidMatchCrash = true,
                LastCrashFaultSummary = "cgame_x64.dll + 0x1234 • 0xC0000005",
                LastCrashTimeUtc = "2026-11-04T00:00:00Z",
                ConsecutiveCrashCount = 3
            };

            LauncherSettingsService.Save(model, tempDir);
            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);

            Assert.True(loaded.AutoRestartMidMatchCrash);
            Assert.Equal("cgame_x64.dll + 0x1234 • 0xC0000005", loaded.LastCrashFaultSummary);
            Assert.Equal("2026-11-04T00:00:00Z", loaded.LastCrashTimeUtc);
            Assert.Equal(3, loaded.ConsecutiveCrashCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ClientModMountSpec_SafeMode_PreservesOverrideAndOmitsExtensions()
    {
        // Normal canonical: has override and extensions
        string canonical = Kongor.Core.Vfs.ClientModMountSpec.GetCanonicalModString(includeOverride: true, isSafeMode: false);
        Assert.Contains("override", canonical);
        Assert.Contains("KONGOR/extensions", canonical);

        // Safe mode with override: has override, strictly omits extensions
        string safeMode = Kongor.Core.Vfs.ClientModMountSpec.GetCanonicalModString(includeOverride: true, isSafeMode: true);
        Assert.Contains("override", safeMode);
        Assert.DoesNotContain("KONGOR/extensions", safeMode);

        // Safe mode pure compatibility: omits both override and extensions
        string safeModePure = Kongor.Core.Vfs.ClientModMountSpec.GetCanonicalModString(includeOverride: false, isSafeMode: true);
        Assert.DoesNotContain("override", safeModePure);
        Assert.DoesNotContain("KONGOR/extensions", safeModePure);
    }

    [Fact]
    public void MainViewModel_ConfigureChildClientEnvironment_SafeMode_InjectsExpectedEnvironmentVariables()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_Env_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm = new();
            System.Diagnostics.ProcessStartInfo psi = new();

            vm.ConfigureChildClientEnvironment(psi, tempDir, isWindowsOverride: true, isSafeMode: true);

            Assert.True(psi.EnvironmentVariables.ContainsKey("KONGOR_SAFE_MODE"));
            Assert.Equal("1", psi.EnvironmentVariables["KONGOR_SAFE_MODE"]);
            Assert.True(psi.EnvironmentVariables.ContainsKey("VK_LOADER_LAYERS_DISABLE"));
            Assert.Contains("~implicit~", psi.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void HardwareDetection_DetectAvailableMonitors_ReturnsAtLeastOneValidMonitor()
    {
        var monitors = HardwareDetectionService.DetectAvailableMonitors();

        Assert.NotEmpty(monitors);
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        Assert.NotNull(primary.DeviceName);
        Assert.True(primary.NativeWidth > 0);
        Assert.True(primary.NativeHeight > 0);
        Assert.True(primary.CurrentRefreshRateHz >= 30);
        Assert.NotEmpty(primary.SupportedResolutions);
        Assert.Contains(primary.SupportedResolutions, r => r.IsNative);
    }

    [Fact]
    public void MainViewModel_SelectedMonitor_UpdatesResolutionsAndEffectiveRefreshRate()
    {
        MainViewModel vm = new();
        Assert.NotEmpty(vm.AvailableMonitors);

        var firstMon = vm.AvailableMonitors[0];
        vm.SelectedMonitor = firstMon;

        Assert.NotEmpty(vm.AvailableResolutions);
        Assert.Contains(firstMon.SupportedResolutions[0].DisplayLabel, vm.AvailableResolutions);

        // By default, manual override is disabled, summary shows Auto with monitor max Hz
        Assert.False(vm.ManualRefreshRateOverrideEnabled);
        Assert.Contains("Auto:", vm.EffectiveRefreshRateSummary);
        Assert.Contains($"{firstMon.MaxRefreshRateHz} Hz", vm.EffectiveRefreshRateSummary);

        // When manual override is enabled with a valid FPS cap
        vm.ManualRefreshRateOverrideEnabled = true;
        vm.ManualMaxFpsText = "240";
        Assert.Equal("Manual Override: 240 FPS Cap", vm.EffectiveRefreshRateSummary);

        // When manual override is toggled off, returns to Auto
        vm.ManualRefreshRateOverrideEnabled = false;
        Assert.Contains("Auto:", vm.EffectiveRefreshRateSummary);
    }

    [Fact]
    public void HardwareDetection_NormalizeMonitorDeviceToken_HandlesBackslashVariants()
    {
        // protects: Canonical backslash-free monitor tokens across C#, C++ Hook_SetMode, and K2 console CVars
        // fails_when: K2 console strips '\\.\DISPLAY5' into '\.DISPLAY5' and fails equality comparison
        // seam: HardwareDetectionService.NormalizeMonitorDeviceToken & AreSameMonitorDevice
        Assert.Equal("DISPLAY1", HardwareDetectionService.NormalizeMonitorDeviceToken(@"\\.\DISPLAY1"));
        Assert.Equal("DISPLAY2", HardwareDetectionService.NormalizeMonitorDeviceToken(@"\.DISPLAY2"));
        Assert.Equal("DISPLAY5", HardwareDetectionService.NormalizeMonitorDeviceToken("display5"));
        Assert.True(HardwareDetectionService.AreSameMonitorDevice(@"\\.\DISPLAY5", @"\.DISPLAY5"));
        Assert.True(HardwareDetectionService.AreSameMonitorDevice(@"\\.\DISPLAY2", "DISPLAY2"));
        Assert.False(HardwareDetectionService.AreSameMonitorDevice(@"\\.\DISPLAY1", "DISPLAY2"));
    }

    [Fact]
    public void MainViewModel_SelectedMonitor_AutoMaxRefreshRate_BeatsLegacy240HzSettings()
    {
        // protects: Per-monitor auto max refresh rate (e.g. 200Hz / 144Hz / 60Hz) against legacy 240Hz launcher_settings.json shadowing
        // fails_when: Legacy RefreshRate="240 Hz" or FpsCap=240 overwrites the selected monitor's real max Hz in autoexec.cfg
        // seam: Real LauncherSettingsService.Save -> LoadLauncherSettings -> WriteCustomConfiguration file output
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_LegacyShadow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            LauncherSettingsModel legacy = new()
            {
                RefreshRate = "240 Hz",
                FpsCap = 240,
                ManualRefreshRateOverrideEnabled = false
            };
            LauncherSettingsService.Save(legacy, tempDir);

            MainViewModel vm = new();
            vm.LoadLauncherSettings(tempDir);

            var customMon = new MonitorDisplayInfo(
                Index: 2,
                DeviceName: @"\\.\DISPLAY2",
                FriendlyName: "27E6QC",
                IsPrimary: false,
                BoundsX: -2560,
                BoundsY: 0,
                NativeWidth: 2560,
                NativeHeight: 1440,
                CurrentRefreshRateHz: 60,
                MaxRefreshRateHz: 144,
                SupportedResolutions: new[]
                {
                    new MonitorResolutionOption(2560, 1440, 144, true, "2560x1440 (1440p QHD) [Native]", new List<int> { 144, 120, 60 }),
                    new MonitorResolutionOption(3840, 2160, 60, false, "3840x2160 (4K UHD)", new List<int> { 60 })
                });

            vm.AvailableMonitors.Add(customMon);
            vm.SelectedMonitor = customMon;
            vm.WriteCustomConfiguration(tempDir);

            Assert.Equal("2: 27E6QC — 2560x1440 @ 144 Hz", customMon.DisplayLabel);
            Assert.Contains("Auto: 144 Hz", vm.EffectiveRefreshRateSummary);

            string autoexec = File.ReadAllText(Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg"));
            Assert.Contains("SetSave \"vid_display\" \"DISPLAY2\"", autoexec);
            Assert.Contains("SetSave \"options_display\" \"DISPLAY2\"", autoexec);
            Assert.Contains("SetSave \"vid_refreshRate\" \"144\"", autoexec);
            Assert.Contains("SetSave \"options_refreshrate\" \"144\"", autoexec);
            Assert.Contains("SetSave \"host_maxFPS\" \"144\"", autoexec);
            Assert.Contains("SetSave \"vid_maxFPS\" \"144\"", autoexec);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_SaveAndLoadLauncherSettings_PersistsMonitorAndManualCap()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_MonitorTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm1 = new();
            var targetMon = vm1.AvailableMonitors.Last();
            vm1.SelectedMonitor = targetMon;
            vm1.ManualRefreshRateOverrideEnabled = true;
            vm1.ManualMaxFpsText = "165";
            vm1.SaveLauncherSettings(tempDir);

            LauncherSettingsModel loaded = LauncherSettingsService.Load(tempDir);
            Assert.Equal(targetMon.DeviceName, loaded.SelectedMonitorDeviceName);
            Assert.True(loaded.ManualRefreshRateOverrideEnabled);
            Assert.Equal(165, loaded.ManualMaxFpsOverride);
            Assert.Equal(165, loaded.FpsCap);

            MainViewModel vm2 = new();
            vm2.LoadLauncherSettings(tempDir);
            Assert.Equal(targetMon.DeviceName, vm2.SelectedMonitor?.DeviceName);
            Assert.True(vm2.ManualRefreshRateOverrideEnabled);
            Assert.Equal("165", vm2.ManualMaxFpsText);
            Assert.Equal("Manual Override: 165 FPS Cap", vm2.EffectiveRefreshRateSummary);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static byte[] BuildSyntheticEdid128(string modelName, int width, int height, int refreshHz, byte descriptorTag = 0xFC)
    {
        const int hBlank = 160;
        const int vBlank = 42;
        int hTotal = width + hBlank;
        int vTotal = height + vBlank;
        int pixelClock10kHz = (int)Math.Round((double)hTotal * vTotal * refreshHz / 10_000.0);
        bool needsDisplayIdExt = pixelClock10kHz > ushort.MaxValue;

        byte[] edid = new byte[needsDisplayIdExt ? 256 : 128];
        edid[0] = 0x00;
        edid[1] = 0xFF;
        edid[2] = 0xFF;
        edid[3] = 0xFF;
        edid[4] = 0xFF;
        edid[5] = 0xFF;
        edid[6] = 0xFF;
        edid[7] = 0x00;
        edid[18] = 1;
        edid[19] = 4;

        // Descriptor 1 (offset 54): Base DTD (capped at 60Hz if >655.35MHz requires DisplayID extension block)
        int baseHz = needsDisplayIdExt ? 60 : refreshHz;
        int basePixelClock10kHz = (int)Math.Round((double)hTotal * vTotal * baseHz / 10_000.0);
        edid[54] = (byte)(basePixelClock10kHz & 0xFF);
        edid[55] = (byte)((basePixelClock10kHz >> 8) & 0xFF);
        edid[56] = (byte)(width & 0xFF);
        edid[57] = (byte)(hBlank & 0xFF);
        edid[58] = (byte)(((width >> 8) & 0x0F) << 4 | ((hBlank >> 8) & 0x0F));
        edid[59] = (byte)(height & 0xFF);
        edid[60] = (byte)(vBlank & 0xFF);
        edid[61] = (byte)(((height >> 8) & 0x0F) << 4 | ((vBlank >> 8) & 0x0F));
        edid[71] = 0x18; // Digital separate sync, non-interlaced (bit 7 = 0)

        // Descriptor 2 (offset 72): Monitor Name (0xFC) or ASCII Text (0xFE)
        edid[72] = 0;
        edid[73] = 0;
        edid[74] = 0;
        edid[75] = descriptorTag;
        edid[76] = 0;
        for (int i = 0; i < 13; i++)
        {
            edid[77 + i] = i < modelName.Length
                ? (byte)modelName[i]
                : (i == modelName.Length ? (byte)0x0A : (byte)0x20);
        }

        edid[126] = needsDisplayIdExt ? (byte)1 : (byte)0;
        int sum = 0;
        for (int i = 0; i < 127; i++) sum += edid[i];
        edid[127] = (byte)((256 - (sum & 0xFF)) & 0xFF);

        if (needsDisplayIdExt)
        {
            // Block 1 (offset 128): VESA DisplayID 1.3 Extension Block (0x70) with Type I Detailed Timing (0x03)
            const int b = 128;
            edid[b + 0] = 0x70; // DisplayID extension tag
            edid[b + 1] = 0x12; // DisplayID 1.2/1.3
            edid[b + 2] = 23;   // Section byte count (3-byte block header + 20-byte Type I descriptor)
            edid[b + 3] = 0x00;
            edid[b + 4] = 0x00;
            edid[b + 5] = 0x03; // Tag 0x03: Type I Detailed Timing
            edid[b + 6] = 0x00;
            edid[b + 7] = 20;   // 20-byte descriptor payload
            int dispClockMinus1 = pixelClock10kHz - 1;
            int wMinus1 = width - 1;
            int hbMinus1 = hBlank - 1;
            int hMinus1 = height - 1;
            int vbMinus1 = vBlank - 1;
            edid[b + 8] = (byte)(dispClockMinus1 & 0xFF);
            edid[b + 9] = (byte)((dispClockMinus1 >> 8) & 0xFF);
            edid[b + 10] = (byte)((dispClockMinus1 >> 16) & 0xFF);
            edid[b + 11] = 0x88; // Preferred progressive timing
            edid[b + 12] = (byte)(wMinus1 & 0xFF);
            edid[b + 13] = (byte)((wMinus1 >> 8) & 0xFF);
            edid[b + 14] = (byte)(hbMinus1 & 0xFF);
            edid[b + 15] = (byte)((hbMinus1 >> 8) & 0xFF);
            edid[b + 20] = (byte)(hMinus1 & 0xFF);
            edid[b + 21] = (byte)((hMinus1 >> 8) & 0xFF);
            edid[b + 22] = (byte)(vbMinus1 & 0xFF);
            edid[b + 23] = (byte)((vbMinus1 >> 8) & 0xFF);
        }

        return edid;
    }

    private static string FormatXrandrEdidHexBlock(byte[] edid)
    {
        string hex = Convert.ToHexString(edid).ToLowerInvariant();
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < hex.Length; i += 32)
        {
            int len = Math.Min(32, hex.Length - i);
            sb.Append("\t\t").Append(hex.AsSpan(i, len)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void HardwareDetection_ParseEdidModelName_And_DtdRefreshRates_ExtractsExactModelAndHighHzModes()
    {
        // protects: Cross-platform VESA EDID 128-byte parsing for monitor model names and Wayland/DRM DTD refresh rates
        // fails_when: EDID descriptor 0xFC/0xFE or DTD pixel-clock formula miscomputes 200Hz/144Hz timings
        // seam: HardwareDetectionService.ParseEdidModelName & ParseEdidDetailedTimingRefreshRates on raw binary EDID
        byte[] edid200 = BuildSyntheticEdid128("VG27AQ5A", 2560, 1440, 200, descriptorTag: 0xFC);
        Assert.Equal("VG27AQ5A", HardwareDetectionService.ParseEdidModelName(edid200));

        var dtdRates = HardwareDetectionService.ParseEdidDetailedTimingRefreshRates(edid200);
        Assert.True(dtdRates.TryGetValue((2560, 1440), out List<int>? rates));
        Assert.Contains(200, rates!);

        byte[] edidFallback = BuildSyntheticEdid128("27E6QC", 2560, 1440, 144, descriptorTag: 0xFE);
        Assert.Equal("27E6QC", HardwareDetectionService.ParseEdidModelName(edidFallback));
    }

    [Fact]
    public void HardwareDetection_ParseXrandrPropsMonitors_ParsesMultiHeadEdidNegativeOffsetsAndMaxHz()
    {
        // protects: Linux X11/XWayland multi-monitor discovery across primary, negative-offset secondary, and 60Hz tertiary panels
        // fails_when: xrandr --props parser drops negative coordinates (-2560+0), misses continuation-line 144Hz modes, or includes interlaced modes
        // seam: HardwareDetectionService.ParseXrandrPropsMonitors & FormatKongorDetectedMonitorsCvar
        string edidMon1 = FormatXrandrEdidHexBlock(BuildSyntheticEdid128("VG27AQ5A", 2560, 1440, 200));
        string edidMon2 = FormatXrandrEdidHexBlock(BuildSyntheticEdid128("27E6QC", 2560, 1440, 144));
        string edidMon3 = FormatXrandrEdidHexBlock(BuildSyntheticEdid128("ASUS MB16AC", 1280, 720, 60));

        string sampleXrandr =
            "Screen 0: minimum 8 x 8, current 6400 x 1440, maximum 32767 x 32767\n" +
            "DP-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis) 597mm x 336mm\n" +
            "\tEDID:\n" + edidMon1 +
            "\tlink-status: Good\n" +
            "   2560x1440    199.98*+ 144.00   119.99    59.95\n" +
            "   1920x1080i    60.00\n" +
            "   1920x1080    144.00    60.00\n" +
            "HDMI-A-1 connected 2560x1440-2560+0 (normal left inverted right x axis y axis) 597mm x 336mm\n" +
            "\tEDID:\n" + edidMon2 +
            "   2560x1440     59.95*\n" +
            "                144.00+  120.00\n" +
            "DP-2 connected 1280x720+2560+0 (normal left inverted right x axis y axis) 344mm x 193mm\n" +
            "\tEDID:\n" + edidMon3 +
            "   1280x720      60.00*+\n" +
            "HDMI-A-2 disconnected (normal left inverted right x axis y axis)\n";

        IReadOnlyList<MonitorDisplayInfo> monitors = HardwareDetectionService.ParseXrandrPropsMonitors(sampleXrandr);
        Assert.Equal(3, monitors.Count);

        Assert.Equal(1, monitors[0].Index);
        Assert.Equal("DP-1", monitors[0].NormalizedDeviceToken);
        Assert.Equal("VG27AQ5A", monitors[0].FriendlyName);
        Assert.True(monitors[0].IsPrimary);
        Assert.Equal(200, monitors[0].MaxRefreshRateHz);
        Assert.Equal("1: VG27AQ5A (Primary) — 2560x1440 @ 200 Hz", monitors[0].DisplayLabel);
        Assert.Equal(2, monitors[0].SupportedResolutions.Count); // 1920x1080i interlaced excluded

        Assert.Equal(2, monitors[1].Index);
        Assert.Equal("HDMI-A-1", monitors[1].NormalizedDeviceToken);
        Assert.Equal("27E6QC", monitors[1].FriendlyName);
        Assert.False(monitors[1].IsPrimary);
        Assert.Equal(-2560, monitors[1].BoundsX);
        Assert.Equal(60, monitors[1].CurrentRefreshRateHz);
        Assert.Equal(144, monitors[1].MaxRefreshRateHz);
        Assert.Equal("2: 27E6QC — 2560x1440 @ 144 Hz", monitors[1].DisplayLabel);

        Assert.Equal(3, monitors[2].Index);
        Assert.Equal("DP-2", monitors[2].NormalizedDeviceToken);
        Assert.Equal("ASUS MB16AC", monitors[2].FriendlyName);
        Assert.Equal(60, monitors[2].MaxRefreshRateHz);
        Assert.Equal("3: ASUS MB16AC — 1280x720 @ 60 Hz", monitors[2].DisplayLabel);

        string cvarPayload = HardwareDetectionService.FormatKongorDetectedMonitorsCvar(monitors);
        Assert.Contains("DP-1|1|VG27AQ5A|1|2560,1440|200|2560x1440@200,144,120,60", cvarPayload);
        Assert.Contains("HDMI-A-1|2|27E6QC|0|2560,1440|144|2560x1440@144,120,60", cvarPayload);
        Assert.Contains("DP-2|3|ASUS MB16AC|0|1280,720|60|1280x720@60", cvarPayload);
    }

    [Fact]
    public void HardwareDetection_ParseLinuxSysfsDrmMonitors_ReadsConnectedConnectorsEdidAndModes()
    {
        // protects: Pure-Wayland Linux monitor enumeration when XWayland/xrandr is unavailable
        // fails_when: /sys/class/drm/card*-* scanner includes disconnected ports or misses EDID DTD refresh rates
        // seam: Real filesystem directory layout matching Linux kernel DRM sysfs
        string tempDrm = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_DrmSysfs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDrm);

        try
        {
            string dp1 = Path.Combine(tempDrm, "card0-DP-1");
            Directory.CreateDirectory(dp1);
            File.WriteAllText(Path.Combine(dp1, "status"), "connected\n");
            File.WriteAllBytes(Path.Combine(dp1, "edid"), BuildSyntheticEdid128("VG27AQ5A", 2560, 1440, 200));
            File.WriteAllText(Path.Combine(dp1, "modes"), "2560x1440\n1920x1080\n");

            string hdmi1 = Path.Combine(tempDrm, "card0-HDMI-A-1");
            Directory.CreateDirectory(hdmi1);
            File.WriteAllText(Path.Combine(hdmi1, "status"), "connected\n");
            File.WriteAllBytes(Path.Combine(hdmi1, "edid"), BuildSyntheticEdid128("27E6QC", 1920, 1080, 144));
            File.WriteAllText(Path.Combine(hdmi1, "modes"), "1920x1080\n");

            string dp2Disconnected = Path.Combine(tempDrm, "card0-DP-2");
            Directory.CreateDirectory(dp2Disconnected);
            File.WriteAllText(Path.Combine(dp2Disconnected, "status"), "disconnected\n");

            IReadOnlyList<MonitorDisplayInfo> monitors = HardwareDetectionService.ParseLinuxSysfsDrmMonitors(tempDrm);
            Assert.Equal(2, monitors.Count);
            Assert.Equal("DP-1", monitors[0].NormalizedDeviceToken);
            Assert.Equal("VG27AQ5A", monitors[0].FriendlyName);
            Assert.Equal(200, monitors[0].MaxRefreshRateHz);
            Assert.Equal("HDMI-A-1", monitors[1].NormalizedDeviceToken);
            Assert.Equal("27E6QC", monitors[1].FriendlyName);
            Assert.Equal(144, monitors[1].MaxRefreshRateHz);
        }
        finally
        {
            if (Directory.Exists(tempDrm)) Directory.Delete(tempDrm, true);
        }
    }

    [Fact]
    public void HardwareDetection_MacOsSystemProfilerJson_And_CrossPlatformChildEnv_PropagatesMonitorContract()
    {
        // protects: macOS display friendly-name extraction and POSIX child process / autoexec.cfg monitor propagation
        // fails_when: ConfigureChildClientEnvironment returns early on Linux/macOS before setting KONGOR_TARGET_MONITOR / KONGOR_DETECTED_MONITORS
        // seam: ParseMacOsSystemProfilerDisplaysJson + MainViewModel.ConfigureChildClientEnvironment + WriteCustomConfiguration
        const string sampleMacJson = """
            {
              "SPDisplaysDataType" : [
                {
                  "_name" : "Apple M3 Max",
                  "spdisplays_ndrvs" : [
                    { "_name" : "Color LCD" },
                    { "_name" : "Studio Display" },
                    { "_name" : "spdisplays_display_connector" }
                  ]
                }
              ]
            }
            """;

        List<string> names = HardwareDetectionService.ParseMacOsSystemProfilerDisplaysJson(sampleMacJson);
        Assert.Equal(["Color LCD", "Studio Display"], names);

        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_CrossEnv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            MainViewModel vm = new();
            var linuxMon = new MonitorDisplayInfo(
                Index: 2,
                DeviceName: "DP-1",
                FriendlyName: "VG27AQ5A",
                IsPrimary: false,
                BoundsX: 0,
                BoundsY: 0,
                NativeWidth: 2560,
                NativeHeight: 1440,
                CurrentRefreshRateHz: 200,
                MaxRefreshRateHz: 200,
                SupportedResolutions:
                [
                    new MonitorResolutionOption(2560, 1440, 200, true, "2560x1440 (1440p QHD) [Native]", [200, 144, 60])
                ]);
            vm.AvailableMonitors.Add(linuxMon);
            vm.SelectedMonitor = linuxMon;

            System.Diagnostics.ProcessStartInfo psi = new();
            vm.ConfigureChildClientEnvironment(psi, tempDir, isWindowsOverride: false, isSafeMode: false);

            Assert.Equal("DP-1", psi.EnvironmentVariables["KONGOR_TARGET_MONITOR"]);
            Assert.Equal("0", psi.EnvironmentVariables["KONGOR_MANUAL_FPS_OVERRIDE"]);
            Assert.Contains("DP-1|2|VG27AQ5A|0|2560,1440|200|2560x1440@200,144,60", psi.EnvironmentVariables["KONGOR_DETECTED_MONITORS"]);

            vm.WriteCustomConfiguration(tempDir);
            string autoexec = File.ReadAllText(Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg"));
            Assert.DoesNotContain("CreateString", autoexec);
            Assert.Contains("SetSave \"kongor_detected_monitors\"", autoexec);
            Assert.Contains("DP-1|2|VG27AQ5A|0|2560,1440|200|2560x1440@200,144,60", autoexec);
            Assert.Contains("SetSave \"vid_display\" \"DP-1\"", autoexec);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MainViewModel_ConfigWriter_CompetitiveProfileAndLaunchArgs_Apply85HzCapAndOmitInvalidCConsoleLines()
    {
        // Protects: K2 cl_packetSendFPS [1, 85] ceiling compliance and removal of invalid CConsole CreateString / port 0 directives.
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_NetAndCConsole_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            MainViewModel vm = new()
            {
                SelectedNetworkProfile = "Competitive 128-Tick (Esports / Fiber)",
                ManualRefreshRateOverrideEnabled = true,
                ManualMaxFpsText = "240"
            };
            MonitorDisplayInfo mon = new(
                Index: 1,
                DeviceName: @"\\.\DISPLAY1",
                FriendlyName: "TestMonitor",
                IsPrimary: true,
                BoundsX: 0,
                BoundsY: 0,
                NativeWidth: 1920,
                NativeHeight: 1080,
                CurrentRefreshRateHz: 240,
                MaxRefreshRateHz: 240,
                SupportedResolutions:
                [
                    new MonitorResolutionOption(1920, 1080, 240, true, "1920x1080 (1080p FHD) [Native]", [240, 144, 60])
                ]);
            vm.AvailableMonitors.Add(mon);
            vm.SelectedMonitor = mon;

            vm.WriteCustomConfiguration(tempDir);

            string autoexecPath = Path.Combine(tempDir, "userdataKONGOR", "configuration", "autoexec.cfg");
            Assert.True(File.Exists(autoexecPath));
            string autoexec = File.ReadAllText(autoexecPath);

            Assert.Contains("SetSave \"cl_packetSendFPS\" \"85\"", autoexec);
            Assert.DoesNotContain("SetSave \"cl_packetSendFPS\" \"128\"", autoexec);
            Assert.Contains("SetSave \"net_maxPacketRate\" \"128\"", autoexec);
            Assert.Contains("SetSave \"kongor_manual_fps_override\" \"1\"", autoexec);
            Assert.Contains("SetSave \"kongor_manual_max_fps\" \"240\"", autoexec);
            Assert.Contains("SetSave \"kongor_detected_monitors\"", autoexec);
            Assert.DoesNotContain("CreateString", autoexec);
            Assert.DoesNotContain("svr_proxyPort", autoexec);
            Assert.DoesNotContain("man_startServerPort", autoexec);

            List<string> launchArgs = vm.BuildClientLaunchArgumentList(
                tempDir,
                "127.0.0.1:55555",
                PlaytestPolicyDto.CreateDeveloperDefault());
            Assert.DoesNotContain(launchArgs, a => a.Contains("svr_proxyPort", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(launchArgs, a => a.Contains("man_startServerPort", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
