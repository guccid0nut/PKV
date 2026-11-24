// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class LaunchConfigWriterServiceTests
{
    /// <summary>
    ///     protects: LaunchConfigWriterService.UpsertSetSave in-place replacement vs conditional append contract for K2 startup.cfg and GUID account profiles.
    ///     fails_when: Existing SetSave keys are duplicated instead of replaced, or non-existent keys are appended to GUID profiles when allowAppend is false.
    ///     seam: Real Regex SetSave parser/mutator in LaunchConfigWriterService.UpsertSetSave.
    /// </summary>
    [Fact]
    public void UpsertSetSave_ReplacesExistingAndRespectsAllowAppendFlag()
    {
        string initial = "SetSave \"vid_fullscreen\" \"false\" \"0\"\nSetSave \"cg_chat\" \"true\" \"0\"\n";

        string updated = LaunchConfigWriterService.UpsertSetSave(initial, "vid_fullscreen", "true", allowAppend: false, out bool modified1);
        Assert.True(modified1);
        Assert.Contains("SetSave \"vid_fullscreen\" \"true\" \"0\"", updated);
        Assert.DoesNotContain("SetSave \"vid_fullscreen\" \"false\"", updated);

        string notAppended = LaunchConfigWriterService.UpsertSetSave(updated, "vid_driver", "vid_vulkan", allowAppend: false, out bool modified2);
        Assert.False(modified2);
        Assert.DoesNotContain("vid_driver", notAppended);

        string appended = LaunchConfigWriterService.UpsertSetSave(updated, "vid_driver", "vid_vulkan", allowAppend: true, out bool modified3);
        Assert.True(modified3);
        Assert.Contains("SetSave \"vid_driver\" \"vid_vulkan\" \"0\"", appended);
    }

    /// <summary>
    ///     protects: LaunchConfigWriterService.SynchronizeStartupAndAccountProfiles deterministic startup.cfg and GUID account profile patching without erasing custom binds.
    ///     fails_when: startup.cfg is not created/backed up, or account GUID profile keybindings are lost during display CVar synchronization.
    ///     seam: Real filesystem I/O and atomic temp-file replacement in LaunchConfigWriterService.
    /// </summary>
    [Fact]
    public void SynchronizeStartupAndAccountProfiles_PatchesDisplaySettingsAndPreservesKeybinds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_ConfigWriterSvc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string guidProfileDir = Path.Combine(tempDir, "userdata", "Heroes of Newerth x64", "game");
            Directory.CreateDirectory(guidProfileDir);
            string guidProfilePath = Path.Combine(guidProfileDir, "11111111-2222-3333-4444-555555555555.cfg");
            File.WriteAllText(guidProfilePath,
                "BindButton \"game\" \"Ability1\" \"Q\"\nSetSave \"vid_fullscreen\" \"false\" \"0\"\nSetSave \"vid_resolution\" \"1280,720\" \"0\"\n");

            LaunchConfigWriterService.SynchronizeStartupAndAccountProfiles(
                rootDir: tempDir,
                activeDriver: "vid_vulkan",
                fullscreenSetting: "true",
                borderlessSetting: "false",
                resParam: "2560,1440",
                refreshRateNumber: "240",
                shadowMapSize: "2048",
                shadowDrawDistance: "2500.0000",
                foliageDensity: "1.0000",
                cullGroundSprites: "false",
                textureCompressionSetting: "true",
                ambientOcclusionSetting: "true");

            string updatedGuid = File.ReadAllText(guidProfilePath);
            Assert.Contains("BindButton \"game\" \"Ability1\" \"Q\"", updatedGuid);
            Assert.Contains("SetSave \"vid_fullscreen\" \"true\" \"0\"", updatedGuid);
            Assert.Contains("SetSave \"vid_resolution\" \"2560,1440\" \"0\"", updatedGuid);
            Assert.True(File.Exists(guidProfilePath + ".bak"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
