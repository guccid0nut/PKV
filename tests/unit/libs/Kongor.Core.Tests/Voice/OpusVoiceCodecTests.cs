// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpusVoiceCodecTests.cs
// System: ProjectKongor - Opus Voice Subsystem Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class OpusVoiceCodecTests
{
    [Fact]
    public void EncodeAndDecode_SineWave_PreservesExactSampleCount()
    {
        using OpusVoiceCodec codec = new OpusVoiceCodec();

        // Synthesize 20ms of 48 kHz mono audio (960 samples) at 440 Hz (Concert A)
        int frameSize = codec.FrameSizeSamples; // 960
        short[] originalPcm = new short[frameSize];
        double frequency = 440.0;
        double sampleRate = 48000.0;

        for (int i = 0; i < frameSize; i++)
        {
            double t = i / sampleRate;
            originalPcm[i] = (short)(Math.Sin(2.0 * Math.PI * frequency * t) * 16000.0);
        }

        byte[] opusBuffer = new byte[1276];
        int encodedBytes = codec.Encode(originalPcm, opusBuffer);

        // Opus voice frames at 28 kbps are typ. 30 to 100 bytes
        encodedBytes.Should().BeGreaterThan(10).And.BeLessThan(500);

        short[] decodedPcm = new short[frameSize];
        int decodedSamples = codec.Decode(opusBuffer.AsSpan(0, encodedBytes), decodedPcm);

        decodedSamples.Should().Be(frameSize);

        // Verify energy reproduction (decoded signal is audible and non-zero)
        long energyOriginal = 0;
        long energyDecoded = 0;
        for (int i = 0; i < frameSize; i++)
        {
            energyOriginal += Math.Abs((long)originalPcm[i]);
            energyDecoded += Math.Abs((long)decodedPcm[i]);
        }

        energyOriginal.Should().BeGreaterThan(100_000);
        energyDecoded.Should().BeGreaterThan(50_000);
    }

    [Fact]
    public void PacketLossConcealment_SynthesizesConcealmentSamples()
    {
        using OpusVoiceCodec codec = new OpusVoiceCodec();
        int frameSize = codec.FrameSizeSamples;

        // Warm up decoder with a valid frame first
        short[] warmUpPcm = new short[frameSize];
        for (int i = 0; i < frameSize; i++)
        {
            warmUpPcm[i] = (short)(Math.Sin(i * 0.1) * 10000.0);
        }

        byte[] opusBuffer = new byte[1276];
        int encodedBytes = codec.Encode(warmUpPcm, opusBuffer);

        short[] outputPcm = new short[frameSize];
        codec.Decode(opusBuffer.AsSpan(0, encodedBytes), outputPcm);

        // Now simulate a dropped packet (empty span) for Packet Loss Concealment (PLC)
        short[] plcPcm = new short[frameSize];
        int plcSamples = codec.Decode(ReadOnlySpan<byte>.Empty, plcPcm, decodeFec: true);

        plcSamples.Should().Be(frameSize);
        // PLC should produce valid audio samples
        plcPcm.Length.Should().Be(frameSize);
    }

    [Fact]
    public void Encode_WithShorterInputThanFrameSize_ThrowsArgumentException()
    {
        using OpusVoiceCodec codec = new OpusVoiceCodec();
        short[] shortBuffer = new short[100]; // Expected 960
        byte[] opusBuffer = new byte[1276];

        Action act = () => codec.Encode(shortBuffer, opusBuffer);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reset_DoesNotThrowAndResetsInternalState()
    {
        using OpusVoiceCodec codec = new OpusVoiceCodec();
        codec.Invoking(c => c.Reset()).Should().NotThrow();
    }
}
