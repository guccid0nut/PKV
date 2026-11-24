// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AiNoiseSuppressionTests.cs
// System: ProjectKongor - AI Noise Suppression & Voice Processing Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class AiNoiseSuppressionTests
{
    [Fact]
    public void ApplyVolumeAndGain_ZeroVolume_MutesAudioCompletely()
    {
        short[] pcm = [1000, 2000, -3000, 4000, -5000];

        AiNoiseSuppressionProcessor.ApplyVolumeAndGain(pcm.AsSpan(), volumePercent: 0f, gainDb: 0f);

        pcm.Should().AllBeEquivalentTo((short)0);
    }

    [Fact]
    public void ApplyVolumeAndGain_UnitySettings_PreservesAudioExactly()
    {
        short[] pcm = [100, -200, 300, -400];
        short[] original = (short[])pcm.Clone();

        AiNoiseSuppressionProcessor.ApplyVolumeAndGain(pcm.AsSpan(), volumePercent: 100f, gainDb: 0f);

        pcm.Should().Equal(original);
    }

    [Fact]
    public void ApplyVolumeAndGain_150PercentVolume_AmplifiesAudio()
    {
        short[] pcm = [1000, -1000];

        AiNoiseSuppressionProcessor.ApplyVolumeAndGain(pcm.AsSpan(), volumePercent: 150f, gainDb: 0f);

        pcm[0].Should().Be(1500);
        pcm[1].Should().Be(-1500);
    }

    [Fact]
    public void ApplyVolumeAndGain_GainBoost6dB_ApproximatelyDoublesAmplitude()
    {
        short[] pcm = [1000, -1000];

        // +6.02 dB corresponds to factor of 2.0x (10^(6/20) ≈ 1.995)
        AiNoiseSuppressionProcessor.ApplyVolumeAndGain(pcm.AsSpan(), volumePercent: 100f, gainDb: 6f);

        pcm[0].Should().BeInRange(1990, 2000);
        pcm[1].Should().BeInRange(-2000, -1990);
    }

    [Fact]
    public void ApplyVolumeAndGain_HighGain_SaturatesWithoutWrapping()
    {
        short[] pcm = [30000, -30000];

        AiNoiseSuppressionProcessor.ApplyVolumeAndGain(pcm.AsSpan(), volumePercent: 150f, gainDb: 18f);

        pcm[0].Should().Be(32767);
        pcm[1].Should().Be(-32768);
    }

    [Fact]
    public void ProcessBuffer_StationaryNoiseFloor_IsAttenuatedByGating()
    {
        // 48 kHz mono, 100ms = 4800 samples of low-amplitude stationary white/pink noise
        int sampleCount = 4800;
        short[] noisePcm = new short[sampleCount];
        Random rng = new Random(1337);
        for (int i = 0; i < sampleCount; i++)
        {
            noisePcm[i] = (short)rng.Next(-800, 800);
        }

        short[] processed = (short[])noisePcm.Clone();

        AiNoiseSuppressionProcessor.ProcessBuffer(
            processed.AsSpan(),
            sampleRate: 48000,
            volumePercent: 100f,
            gainDb: 0f,
            enableNoiseSuppression: true);

        float reductionDb = AiNoiseSuppressionProcessor.CalculateNoiseReductionDb(noisePcm, processed);
        reductionDb.Should().BeGreaterThan(8.0f, "Stationary noise should be substantially attenuated by spectral gating");
    }

    [Fact]
    public void ProcessBuffer_SpeechFormantTone_PreservesEnergyWithHighFidelity()
    {
        // 48 kHz mono, 100ms = 4800 samples of strong 1000 Hz vocal formant sine wave
        int sampleCount = 4800;
        short[] tonePcm = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            tonePcm[i] = (short)(Math.Sin(2.0 * Math.PI * 1000.0 * i / 48000.0) * 12000.0);
        }

        short[] processed = (short[])tonePcm.Clone();

        AiNoiseSuppressionProcessor.ProcessBuffer(
            processed.AsSpan(),
            sampleRate: 48000,
            volumePercent: 100f,
            gainDb: 0f,
            enableNoiseSuppression: true);

        // Vocal formant should pass through with high retention (> 75% peak)
        float maxOriginal = 0f;
        float maxProcessed = 0f;
        for (int i = 2400; i < sampleCount; i++)
        {
            maxOriginal = Math.Max(maxOriginal, Math.Abs(tonePcm[i]));
            maxProcessed = Math.Max(maxProcessed, Math.Abs(processed[i]));
        }

        (maxProcessed / maxOriginal).Should().BeGreaterThan(0.70f, "Speech formant frequencies must be preserved");
    }

    [Fact]
    public void CalculateNoiseReductionDb_IdenticalBuffers_ReturnsZeroDb()
    {
        short[] a = [500, -500, 1000, -1000];
        short[] b = [500, -500, 1000, -1000];

        float db = AiNoiseSuppressionProcessor.CalculateNoiseReductionDb(a, b);
        db.Should().Be(0f);
    }
}
