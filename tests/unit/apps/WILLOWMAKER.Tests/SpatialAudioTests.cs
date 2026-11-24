// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SpatialAudioTests.cs
// System: WILLOWMAKER.Tests
// ============================================================================

using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class SpatialAudioTests
{
    [Fact]
    public void GenerateAlsoftConfig_DefaultSettings_EnablesHrtfAnd48kHz()
    {
        // Arrange
        AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

        // Act
        string config = service.GenerateAlsoftConfig(
            enableHrtf: true,
            hrtfProfileName: AudioModernizationService.BuiltInKemarName,
            enableOcclusionDamping: true,
            frequency: 48000);

        // Assert
        Assert.Contains("hrtf = true", config);
        Assert.Contains("frequency = 48000", config);
        Assert.Contains("stereo-mode = headphones", config);
        Assert.Contains("stereo-encoding = hrtf", config);
        Assert.Contains("default-hrtf = Built-In 48000hz", config);
        Assert.Contains("resampler = cubic", config);
        Assert.Contains("occlusion-gain = 0.25", config);
        Assert.Contains("sources = 256", config);
    }

    [Fact]
    public void GenerateAlsoftConfig_OcclusionDampingDisabled_SetsOcclusionGainToOne()
    {
        // Arrange
        AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

        // Act
        string config = service.GenerateAlsoftConfig(
            enableHrtf: true,
            hrtfProfileName: "CustomSofaProfile.sofa",
            enableOcclusionDamping: false,
            frequency: 48000);

        // Assert
        Assert.Contains("occlusion-gain = 1.0", config);
        Assert.Contains("default-hrtf = CustomSofaProfile.sofa", config);
    }

    [Fact]
    public void GenerateK2AudioConfig_128VoicesAndOcclusion_GeneratesCorrectCvars()
    {
        // Arrange
        AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

        // Act
        string cvars = service.GenerateK2AudioConfig(
            enableSpatialAudio: true,
            enableOcclusionDamping: true,
            numChannels: 128,
            mixRate: 48000);

        // Assert
        Assert.Contains("SetSave \"sound_numChannels\" \"128\"", cvars);
        Assert.Contains("SetSave \"sound_bufferLength\" \"256\"", cvars);
        Assert.Contains("SetSave \"sound_mixRate\" \"48000\"", cvars);
        Assert.Contains("SetSave \"sound_geometryOcclusion\" \"true\"", cvars);
        Assert.Contains("SetSave \"sound_customRolloff\" \"true\"", cvars);
        Assert.Contains("SetSave \"sound_distanceFactor\" \"1.0000\"", cvars);
        Assert.Contains("SetSave \"sound_dopplerFactor\" \"1.0000\"", cvars);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, 0f, 0f)]
    [InlineData(10f, 0f, 0f, 10f, 0f, 0f)]   // +X (Right) in K2 -> +X (Right) in OpenAL
    [InlineData(0f, 10f, 0f, 0f, 0f, -10f)]  // +Y (Forward) in K2 -> -Z (Forward) in OpenAL
    [InlineData(0f, 0f, 10f, 0f, 10f, 0f)]   // +Z (Up) in K2 -> +Y (Up) in OpenAL
    public void ConvertK2ToOpenAlCoordinates_WhenTransformingAxes_CorrectlyMapsCoordinates(
        float k2X, float k2Y, float k2Z,
        float expectedAlX, float expectedAlY, float expectedAlZ)
    {
        // Act
        (float alX, float alY, float alZ) = AudioModernizationService.ConvertK2ToOpenAlCoordinates(k2X, k2Y, k2Z);

        // Assert
        Assert.Equal(expectedAlX, alX);
        Assert.Equal(expectedAlY, alY);
        Assert.Equal(expectedAlZ, alZ);
    }

    [Fact]
    public void SpatialAudio_ConvertK2ListenerOrientation_TransformsForwardAndUpVectors()
    {
        // K2: Looking forward (+Y) with head up (+Z)
        float k2FwdX = 0f, k2FwdY = 1f, k2FwdZ = 0f;
        float k2UpX = 0f, k2UpY = 0f, k2UpZ = 1f;

        // Act
        (float fwdX, float fwdY, float fwdZ, float upX, float upY, float upZ) =
            AudioModernizationService.ConvertK2ListenerOrientation(
                k2FwdX, k2FwdY, k2FwdZ,
                k2UpX, k2UpY, k2UpZ);

        // Assert: OpenAL forward should be (0, 0, -1) and up should be (0, 1, 0)
        Assert.Equal(0f, fwdX);
        Assert.Equal(0f, fwdY);
        Assert.Equal(-1f, fwdZ);

        Assert.Equal(0f, upX);
        Assert.Equal(1f, upY);
        Assert.Equal(0f, upZ);
    }

    [Fact]
    public void SpatialAudio_DiscoverAvailableProfiles_FindsCustomSofaAndMhrFiles()
    {
        // Arrange
        string tempRoot = Path.Combine(Path.GetTempPath(), "SpatialAudioTest_" + Guid.NewGuid().ToString("N"));
        string hrtfDir = Path.Combine(tempRoot, "hrtf");
        Directory.CreateDirectory(hrtfDir);

        try
        {
            File.WriteAllText(Path.Combine(hrtfDir, "Aural_ID_Custom.sofa"), "fake_sofa_data");
            File.WriteAllText(Path.Combine(hrtfDir, "Nearfield_IEM.mhr"), "fake_mhr_data");
            File.WriteAllText(Path.Combine(hrtfDir, "ignore_me.txt"), "text");

            AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

            // Act
            IReadOnlyList<string> profiles = service.DiscoverAvailableProfiles(tempRoot);

            // Assert
            Assert.Contains(AudioModernizationService.BuiltInKemarName, profiles);
            Assert.Contains(AudioModernizationService.CompetitivePinpointName, profiles);
            Assert.Contains(AudioModernizationService.CinematicWideStageName, profiles);
            Assert.Contains("Aural_ID_Custom.sofa", profiles);
            Assert.Contains("Nearfield_IEM.mhr", profiles);
            Assert.DoesNotContain("ignore_me.txt", profiles);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void SpatialAudio_ApplyAudioModernization_CreatesDirectoryAndWritesConfigFile()
    {
        // Arrange
        string tempRoot = Path.Combine(Path.GetTempPath(), "SpatialAudioTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            AudioModernizationService service = new(NullLogger<AudioModernizationService>.Instance);

            // Act
            service.ApplyAudioModernization(
                tempRoot,
                enableSpatialAudio: true,
                hrtfProfile: AudioModernizationService.CompetitivePinpointName,
                enableOcclusionDamping: true,
                channels: 128);

            // Assert
            string expectedIni = Path.Combine(tempRoot, "alsoft.ini");
            string expectedHrtfDir = Path.Combine(tempRoot, "hrtf");

            Assert.True(File.Exists(expectedIni));
            Assert.True(Directory.Exists(expectedHrtfDir));

            string content = File.ReadAllText(expectedIni);
            Assert.Contains("default-hrtf = Competitive Pinpoint 48kHz", content);
            Assert.Contains("hrtf = true", content);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void LauncherSettingsModel_SpatialAudioSettings_SerializesAndDeserializesCorrectly()
    {
        // Arrange
        LauncherSettingsModel model = new()
        {
            EnableSpatialAudio = true,
            SpatialHrtfProfile = "Pro_Gaming_48k.sofa",
            SpatialAudioChannels = 256,
            EnableOcclusionDamping = true
        };

        // Act
        string json = JsonSerializer.Serialize(model);
        LauncherSettingsModel? deserialized = JsonSerializer.Deserialize<LauncherSettingsModel>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized.EnableSpatialAudio);
        Assert.Equal("Pro_Gaming_48k.sofa", deserialized.SpatialHrtfProfile);
        Assert.Equal(256, deserialized.SpatialAudioChannels);
        Assert.True(deserialized.EnableOcclusionDamping);
    }

    [Fact]
    public void BinauralSpatialAudioSynthesizer_ResolveAudioFilePath_FindsExtractedGameAudio()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        // Act & Assert
        string? pyro = BinauralSpatialAudioSynthesizer.ResolveAudioFilePath("pyromancer_dragon_stun.wav");
        string? hammer = BinauralSpatialAudioSynthesizer.ResolveAudioFilePath("hammerstorm_cleave.wav");
        string? blood = BinauralSpatialAudioSynthesizer.ResolveAudioFilePath("first_blood.wav");
        string? doubleKill = BinauralSpatialAudioSynthesizer.ResolveAudioFilePath("double_kill.wav");
        string? song = BinauralSpatialAudioSynthesizer.ResolveAudioFilePath("can_you_feel_my_heart.wav");

        Assert.NotNull(pyro);
        Assert.True(File.Exists(pyro));
        Assert.NotNull(hammer);
        Assert.True(File.Exists(hammer));
        Assert.NotNull(blood);
        Assert.True(File.Exists(blood));
        Assert.NotNull(doubleKill);
        Assert.True(File.Exists(doubleKill));
        Assert.NotNull(song);
        Assert.True(File.Exists(song));
    }

    [Fact]
    public void BinauralSpatialAudioSynthesizer_SpellCastAndMeleeAttack_GeneratesValidWavData()
    {
        // Act
        byte[] spellWav = BinauralSpatialAudioSynthesizer.GenerateSpellCastWav(45f, 1.0f);
        byte[] meleeWav = BinauralSpatialAudioSynthesizer.GenerateMeleeAttackWav(-45f, 1.0f);
        byte[] announcerWav = BinauralSpatialAudioSynthesizer.GenerateAnnouncerWav("first_blood", 0f, 1.0f);

        // Assert
        Assert.NotNull(spellWav);
        Assert.True(spellWav.Length > 1000);
        Assert.Equal((byte)'R', spellWav[0]);
        Assert.Equal((byte)'I', spellWav[1]);
        Assert.Equal((byte)'F', spellWav[2]);
        Assert.Equal((byte)'F', spellWav[3]);

        Assert.NotNull(meleeWav);
        Assert.True(meleeWav.Length > 1000);
        Assert.Equal((byte)'R', meleeWav[0]);

        Assert.NotNull(announcerWav);
        Assert.True(announcerWav.Length > 1000);
        Assert.Equal((byte)'R', announcerWav[0]);
    }

    [Fact]
    public void BinauralSpatialAudioSynthesizer_AcousticProfiles_ProduceDistinctWaveforms()
    {
        // Arrange
        SpatialAudioOptions competitive = new(AudioModernizationService.CompetitivePinpointName, 128, true);
        SpatialAudioOptions cinematic = new(AudioModernizationService.CinematicWideStageName, 128, true);
        SpatialAudioOptions builtin = new(AudioModernizationService.BuiltInKemarName, 128, true);

        // Act
        byte[] compWav = BinauralSpatialAudioSynthesizer.GenerateDirectionalWav(60f, 1.0f, competitive);
        byte[] cineWav = BinauralSpatialAudioSynthesizer.GenerateDirectionalWav(60f, 1.0f, cinematic);
        byte[] builtWav = BinauralSpatialAudioSynthesizer.GenerateDirectionalWav(60f, 1.0f, builtin);

        // Assert: Waveforms should not be identical because of Biquad EQ & ITD differences
        Assert.NotEqual(compWav, cineWav);
        Assert.NotEqual(compWav, builtWav);
        Assert.NotEqual(cineWav, builtWav);
    }

    [Fact]
    public void BinauralSpatialAudioSynthesizer_ChannelsMode_ProduceDistinctReflections()
    {
        // Arrange
        SpatialAudioOptions ch64 = new(AudioModernizationService.BuiltInKemarName, 64, true);
        SpatialAudioOptions ch256 = new(AudioModernizationService.BuiltInKemarName, 256, true);

        // Act
        byte[] wav64 = BinauralSpatialAudioSynthesizer.GenerateDirectionalWav(90f, 1.0f, ch64);
        byte[] wav256 = BinauralSpatialAudioSynthesizer.GenerateDirectionalWav(90f, 1.0f, ch256);

        // Assert: 256 channels includes multi-tap room reflections; 64 channels is dry
        Assert.NotEqual(wav64, wav256);
    }

    [Fact]
    public void BinauralSpatialAudioSynthesizer_UpdateContinuousPosition_UpdatesLiveParametersWithoutException()
    {
        // Arrange
        SpatialAudioOptions options = new(AudioModernizationService.CompetitivePinpointName, 128, true);

        // Act
        BinauralSpatialAudioSynthesizer.UpdateContinuousPosition(45f, 0.8f, options);
        BinauralSpatialAudioSynthesizer.UpdateContinuousPosition(0f, 0.0f, options);

        // protects: Real-time listener azimuth, distance, and optional SpatialAudioOptions state mutation contract in BinauralSpatialAudioSynthesizer.UpdateContinuousPosition.
        // fails_when: UpdateContinuousPosition fails to store live azimuth/distance, overwrites existing options when null is passed, or inadvertently starts streaming.
        // seam: BinauralSpatialAudioSynthesizer.UpdateContinuousPosition static state + IsContinuousStreamingActive contract.
        FieldInfo? azimuthField = typeof(BinauralSpatialAudioSynthesizer).GetField("_dynamicAzimuth", BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo? distanceField = typeof(BinauralSpatialAudioSynthesizer).GetField("_dynamicDistance", BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo? optionsField = typeof(BinauralSpatialAudioSynthesizer).GetField("_dynamicOptions", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(azimuthField);
        Assert.NotNull(distanceField);
        Assert.NotNull(optionsField);

        BinauralSpatialAudioSynthesizer.UpdateContinuousPosition(45f, 0.8f, options);
        Assert.Equal(45f, (float)azimuthField.GetValue(null)!);
        Assert.Equal(0.8f, (float)distanceField.GetValue(null)!);
        Assert.Same(options, optionsField.GetValue(null));

        BinauralSpatialAudioSynthesizer.UpdateContinuousPosition(-30f, 0.25f, null);
        Assert.Equal(-30f, (float)azimuthField.GetValue(null)!);
        Assert.Equal(0.25f, (float)distanceField.GetValue(null)!);
        Assert.Same(options, optionsField.GetValue(null));
        Assert.False(BinauralSpatialAudioSynthesizer.IsContinuousStreamingActive);
    }

    [Fact]
    public void BinauralSpatialAudioSynthesizer_KongorSlain_ResolvesAndGeneratesValidWav()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        // Act
        string? path = BinauralSpatialAudioSynthesizer.ResolveAudioFilePath("kongor_slain.wav");
        byte[] wav = BinauralSpatialAudioSynthesizer.GenerateAnnouncerWav("kongor_slain", 0f, 1.0f);

        // Assert
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.NotNull(wav);
        Assert.True(wav.Length > 1000);
        Assert.Equal((byte)'R', wav[0]);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void AvailableSpatialAuditionTracks_DoesNotContainOrbitSweep_AsItIsDedicatedButton()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        MainViewModel vm = new();
        Assert.DoesNotContain(vm.AvailableSpatialAuditionTracks, t => t.Contains("Orbit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.AvailableSpatialAuditionTracks, t => t.Contains("Heart", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.AvailableSpatialAuditionTracks, t => t.Contains("Pyromancer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void SpatialAudio_UpdateBeaconCoordinatesOnly_UpdatesCoordinatesAccurately()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        MainViewModel vm = new();
        vm.UpdateBeaconCoordinatesOnly(90f, 0.75f);
        Assert.Equal(90f, vm.SpatialAzimuthAngle);
        Assert.Equal(0.75f, vm.SpatialDistance);
        Assert.Contains("Right Ear", vm.SpatialAudioFeedbackText);
    }

    [Fact]
    public void SpatialAudioEngine_SetListenerPoseFromK2_DoesNotThrow_WhenUninitialized()
    {
        using NEXUS.SpatialAudio.SpatialAudioEngine engine = new(NullLogger<NEXUS.SpatialAudio.SpatialAudioEngine>.Instance);
        
        // Should safely no-op without crashing when OpenAL device is not opened
        Exception? ex = Record.Exception(() => engine.SetListenerPoseFromK2(100f, 200f, 50f, 0f, 1f, 0f, 0f, 0f, 1f));
        Assert.Null(ex);
    }

    [Fact]
    public void SpatialAudioEngine_ComputeCliffAndObstacleOcclusion_FlatLineOfSight_ReturnsUnattenuated()
    {
        Vector3 listener = new(1000f, 1000f, 128f);
        Vector3 source = new(1400f, 1000f, 128f);

        NEXUS.SpatialAudio.SpatialOcclusionResult result =
            NEXUS.SpatialAudio.SpatialAudioEngine.ComputeCliffAndObstacleOcclusion(
                listener,
                source,
                (x, y) => 128f);

        Assert.Equal(0, result.OccludedSteps);
        Assert.Equal(1.0f, result.GainMultiplier, 3);
        Assert.Equal(1.0f, result.LowPassGainHf, 3);
    }

    [Fact]
    public void SpatialAudioEngine_ComputeCliffAndObstacleOcclusion_HighGroundCliffAndRidge_AttenuatesAndClamps()
    {
        Vector3 listener = new(1000f, 1000f, 0f);
        Vector3 sourceOnHighGround = new(1200f, 1000f, 160f);

        // 1. Direct elevation differential without sampler (>= 128f cliff threshold)
        NEXUS.SpatialAudio.SpatialOcclusionResult cliffResult =
            NEXUS.SpatialAudio.SpatialAudioEngine.ComputeCliffAndObstacleOcclusion(listener, sourceOnHighGround);
        Assert.True(cliffResult.OccludedSteps >= 1);
        Assert.True(cliffResult.GainMultiplier < 1.0f && cliffResult.GainMultiplier >= 0.25f);
        Assert.True(cliffResult.LowPassGainHf < 1.0f && cliffResult.LowPassGainHf >= 0.18f);

        // 2. Full ridge blocking all intermediate ray steps clamps cleanly to floor (0.25f gain, 0.18f HF)
        Vector3 behindRidge = new(1500f, 1000f, 0f);
        NEXUS.SpatialAudio.SpatialOcclusionResult ridgeResult =
            NEXUS.SpatialAudio.SpatialAudioEngine.ComputeCliffAndObstacleOcclusion(
                listener,
                behindRidge,
                (x, y) => 300f,
                cliffBlockThresholdUnits: 128f,
                raySteps: 8);

        Assert.Equal(7, ridgeResult.OccludedSteps);
        Assert.Equal(0.30f, ridgeResult.GainMultiplier, 2);
        Assert.Equal(0.18f, ridgeResult.LowPassGainHf, 2);

        // 3. Degenerate zero-distance source returns 1.0f without NaN
        NEXUS.SpatialAudio.SpatialOcclusionResult samePosResult =
            NEXUS.SpatialAudio.SpatialAudioEngine.ComputeCliffAndObstacleOcclusion(listener, listener);
        Assert.Equal(0, samePosResult.OccludedSteps);
        Assert.Equal(1.0f, samePosResult.GainMultiplier);
        Assert.Equal(1.0f, samePosResult.LowPassGainHf);
    }

    [Fact]
    public void SpatialAudioEngine_ReinitializeWithHrtfProfile_UpdatesSelectedProfileSafely()
    {
        using NEXUS.SpatialAudio.SpatialAudioEngine engine = new(NullLogger<NEXUS.SpatialAudio.SpatialAudioEngine>.Instance);
        Assert.Equal(-1, engine.SelectedHrtfProfileIndex);

        Exception? ex = Record.Exception(() => engine.ReinitializeWithHrtfProfile(2, 48000));
        Assert.Null(ex);
        Assert.Equal(2, engine.SelectedHrtfProfileIndex);
    }
}

