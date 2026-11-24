// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AudioSoftLimiterTests.cs
// System: ProjectKongor - Voice Audio Limiter Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Fast Automated Tests
// ============================================================================

using System;
using System.Collections.Generic;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class AudioSoftLimiterTests
{
    [Fact]
    public void DbToLinear_CalculatesAccurateLinearGain()
    {
        AudioSoftLimiter.DbToLinear(0.0f).Should().Be(1.0f);
        AudioSoftLimiter.DbToLinear(6.0206f).Should().BeApproximately(2.0f, 0.01f);
        AudioSoftLimiter.DbToLinear(12.0f).Should().BeApproximately(3.981f, 0.01f);
    }

    [Fact]
    public void LimitSample_SmallSignals_RemainLinear()
    {
        // Signals under 70% full scale (22936) are passed through without non-linear distortion
        AudioSoftLimiter.LimitSample(0.0f).Should().Be(0);
        AudioSoftLimiter.LimitSample(1000.0f).Should().Be(1000);
        AudioSoftLimiter.LimitSample(-5000.0f).Should().Be(-5000);
        AudioSoftLimiter.LimitSample(20000.0f).Should().Be(20000);
        AudioSoftLimiter.LimitSample(-20000.0f).Should().Be(-20000);
    }

    [Fact]
    public void LimitSample_HugeOverload_NeverExceedsPcm16Range()
    {
        // Signals far beyond full scale are softly compressed and never exceed [-32768, 32767]
        AudioSoftLimiter.LimitSample(50000.0f).Should().BeLessThanOrEqualTo(32767);
        AudioSoftLimiter.LimitSample(50000.0f).Should().BeGreaterThan(30000);

        AudioSoftLimiter.LimitSample(200000.0f).Should().BeLessThanOrEqualTo(32767);
        AudioSoftLimiter.LimitSample(-200000.0f).Should().BeGreaterThanOrEqualTo(-32768);
    }

    [Fact]
    public void MixAndLimit_MultipleLoudStreams_SumsWithoutClipping()
    {
        int sampleCount = 960; // 20ms @ 48 kHz
        short[] peer1 = new short[sampleCount];
        short[] peer2 = new short[sampleCount];
        short[] peer3 = new short[sampleCount];
        short[] peer4 = new short[sampleCount];

        // 4 peers speaking at 20,000 amplitude (sum = 80,000, well above 32,767)
        for (int i = 0; i < sampleCount; i++)
        {
            peer1[i] = 20000;
            peer2[i] = 20000;
            peer3[i] = 20000;
            peer4[i] = 20000;
        }

        List<ReadOnlyMemory<short>> streams = new List<ReadOnlyMemory<short>>
        {
            peer1, peer2, peer3, peer4
        };

        short[] output = new short[sampleCount];
        AudioSoftLimiter.MixAndLimit(streams, output, gainDb: 6.0f); // with +6 dB additional boost

        for (int i = 0; i < sampleCount; i++)
        {
            output[i].Should().BeLessThanOrEqualTo(32767);
            output[i].Should().BeGreaterThan(31000); // Softly saturated near peak
        }
    }

    [Fact]
    public void ProcessFrameInPlace_AppliesGainSmoothly()
    {
        short[] buffer = new short[] { 1000, 5000, 15000, -15000 };
        AudioSoftLimiter.ProcessFrameInPlace(buffer, gainDb: 6.0206f); // ~2x gain

        ((int)buffer[0]).Should().BeCloseTo(2000, 25);
        ((int)buffer[1]).Should().BeCloseTo(10000, 25);
        buffer[2].Should().BeLessThanOrEqualTo(32767);
        buffer[3].Should().BeGreaterThanOrEqualTo(-32768);
    }
}
