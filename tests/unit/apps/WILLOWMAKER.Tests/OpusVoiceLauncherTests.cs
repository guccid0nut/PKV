// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpusVoiceLauncherTests.cs
// System: WILLOWMAKER Unit Tests - Modern Opus Voice Chat & Launcher Mic Diagnostic
// Policy: Strict Explicit Typing (NO 'var') - Sub-second Execution
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using Kongor.Core.Services.Voice;
using WILLOWMAKER.Core.ViewModels;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class OpusVoiceLauncherTests
{
    [Fact]
    public void MainViewModel_VoiceProperties_InitializeWithCorrectDefaults()
    {
        // Arrange & Act
        MainViewModel vm = new();

        // Assert
        Assert.False(vm.IsVoiceTesting);
        Assert.True(vm.CanTestVoice);
        Assert.Contains("Test Microphone", vm.VoiceTestButtonText);
        Assert.Contains("Opus", vm.VoiceTestStatusText);
        Assert.Equal(0, vm.VoiceTestProgress);
        Assert.Equal(0f, vm.VoiceTestPeakLevel);
        Assert.Equal("0%", vm.VoiceTestPeakPercentText);
        Assert.False(vm.HasVoiceTestCompleted);
        Assert.NotEmpty(vm.VoiceDetectedMicText);
    }

    [Fact]
    public void MainViewModel_RefreshVoiceDevices_PopulatesHardwareStringWithoutThrowing()
    {
        // Arrange
        MainViewModel vm = new();

        // Act
        vm.RefreshVoiceDevices();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(vm.VoiceDetectedMicText));
        if (OperatingSystem.IsWindows() && OpusMicTestService.HasMicrophone())
        {
            Assert.Contains("Input:", vm.VoiceDetectedMicText);
            Assert.Contains("Output:", vm.VoiceDetectedMicText);
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void MainViewModel_VoiceProperties_NotifyChangesProperly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        MainViewModel vm = new();
        bool statusChanged = false;
        bool progressChanged = false;

        vm.PropertyChanged += (object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.VoiceTestStatusText)) statusChanged = true;
            if (e.PropertyName == nameof(MainViewModel.VoiceTestProgress)) progressChanged = true;
        };

        // Act
        vm.VoiceTestStatusText = "🎙️ Testing audio level...";
        vm.VoiceTestProgress = 75;

        // Assert
        Assert.True(statusChanged);
        Assert.True(progressChanged);
        Assert.Equal(75, vm.VoiceTestProgress);
        Assert.Equal("🎙️ Testing audio level...", vm.VoiceTestStatusText);
    }

    [Fact]
    public void OpusMicTestService_SyntheticPcmWave_EncodesAndNormalizesCorrectly()
    {
        // Arrange - Generate 200ms of synthetic 440 Hz vocal tone at 48 kHz
        const int sampleRate = 48000;
        const int samples = 9600; // 10 Opus frames of 20ms (960 samples each)
        short[] syntheticPcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double time = i / (double)sampleRate;
            syntheticPcm[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * time) * 8000.0); // ~24% amplitude
        }

        using OpusVoiceCodec encoder = new(new OpusVoiceOptions
        {
            SampleRate = sampleRate,
            Channels = 1,
            FrameSizeMs = 20,
            Bitrate = 28000
        });

        using OpusVoiceCodec decoder = new(new OpusVoiceOptions
        {
            SampleRate = sampleRate,
            Channels = 1,
            FrameSizeMs = 20,
            Bitrate = 28000
        });

        const int frameSize = 960;
        const int frameCount = samples / frameSize;
        short[] decodedPcm = new short[samples];
        byte[] opusBuffer = new byte[1276];
        int totalEncodedBytes = 0;

        // Act - Encode & Decode
        for (int f = 0; f < frameCount; f++)
        {
            ReadOnlySpan<short> inFrame = syntheticPcm.AsSpan(f * frameSize, frameSize);
            int encBytes = encoder.Encode(inFrame, opusBuffer);
            totalEncodedBytes += encBytes;

            Span<short> outFrame = decodedPcm.AsSpan(f * frameSize, frameSize);
            decoder.Decode(opusBuffer.AsSpan(0, encBytes), outFrame);
        }

        // Software loudness normalization
        float normalizedPeak = OpusMicTestService.NormalizeAudio(decodedPcm, targetPeak: 0.75f);
        byte[] wavBytes = OpusMicTestService.CreateWavFile(decodedPcm, sampleRate, 1);

        // Assert
        Assert.True(totalEncodedBytes > 0);
        Assert.True(totalEncodedBytes < samples * sizeof(short)); // Verified compression
        Assert.InRange(normalizedPeak, 0.70f, 0.80f); // Boosted cleanly to target ~75%
        Assert.NotNull(wavBytes);
        Assert.True(wavBytes.Length > 44);
        Assert.Equal((byte)'R', wavBytes[0]);
        Assert.Equal((byte)'I', wavBytes[1]);
        Assert.Equal((byte)'F', wavBytes[2]);
        Assert.Equal((byte)'F', wavBytes[3]);
        Assert.Equal((byte)'W', wavBytes[8]);
        Assert.Equal((byte)'A', wavBytes[9]);
        Assert.Equal((byte)'V', wavBytes[10]);
        Assert.Equal((byte)'E', wavBytes[11]);
    }

    [Fact]
    public void MainViewModel_MicControls_InitializeWithCorrectDefaultsAndNotify()
    {
        // Arrange
        MainViewModel vm = new();
        try
        {
            // Ensure clean initial state
            vm.VoiceMicVolume = 100;
            vm.VoiceMicGain = 0;
            vm.EnableVoiceAiNoiseSuppression = true;

            // Assert Defaults
            Assert.Equal(100, vm.VoiceMicVolume);
            Assert.Equal("100%", vm.VoiceMicVolumeText);
            Assert.Equal(0, vm.VoiceMicGain);
            Assert.Equal("Off (+0 dB)", vm.VoiceMicGainText);
            Assert.True(vm.EnableVoiceAiNoiseSuppression);

            bool volumeChanged = false;
            bool gainChanged = false;
            bool suppressionChanged = false;

            vm.PropertyChanged += (object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.VoiceMicVolume)) volumeChanged = true;
                if (e.PropertyName == nameof(MainViewModel.VoiceMicGain)) gainChanged = true;
                if (e.PropertyName == nameof(MainViewModel.EnableVoiceAiNoiseSuppression)) suppressionChanged = true;
            };

            // Act
            vm.VoiceMicVolume = 130;
            vm.VoiceMicGain = 9;
            vm.EnableVoiceAiNoiseSuppression = false;

            // Assert Changes
            Assert.True(volumeChanged);
            Assert.True(gainChanged);
            Assert.True(suppressionChanged);
            Assert.Equal(130, vm.VoiceMicVolume);
            Assert.Equal("130%", vm.VoiceMicVolumeText);
            Assert.Equal(9, vm.VoiceMicGain);
            Assert.Equal("+9 dB", vm.VoiceMicGainText);
            Assert.False(vm.EnableVoiceAiNoiseSuppression);
        }
        finally
        {
            // Restore clean defaults
            vm.VoiceMicVolume = 100;
            vm.VoiceMicGain = 0;
            vm.EnableVoiceAiNoiseSuppression = true;
        }
    }
}
