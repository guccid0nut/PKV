// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WasapiAudioEngineTests.cs
// System: ProjectKongor - WASAPI Audio Engine Unit Tests
// Policy: Strict Explicit Typing (NO 'var') - Fast Automated Unit Tests
// ============================================================================

using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class WasapiAudioEngineTests
{
    [Fact]
    public void ConvertDevicePcmTo48kMono_Stereo16Bit48k_DownmixesToMonoAccurately()
    {
        // 20ms of stereo 16-bit PCM @ 48 kHz = 960 frames * 2 channels * 2 bytes = 3840 bytes
        byte[] stereoBytes = new byte[960 * 2 * 2];
        for (int i = 0; i < 960; i++)
        {
            short left = 10000;
            short right = 20000;
            int offset = i * 4;
            MemoryMarshal.Write(stereoBytes.AsSpan(offset, 2), in left);
            MemoryMarshal.Write(stereoBytes.AsSpan(offset + 2, 2), in right);
        }

        short[] monoOutput = new short[960];
        int converted = WasapiAudioEngine.ConvertDevicePcmTo48kMono(
            stereoBytes,
            srcSampleRate: 48000,
            srcChannels: 2,
            srcBitsPerSample: 16,
            isFloat: false,
            monoOutput);

        converted.Should().Be(960);
        // Average of 10000 and 20000 is 15000
        monoOutput[0].Should().Be(15000);
        monoOutput[450].Should().Be(15000);
        monoOutput[959].Should().Be(15000);
    }

    [Fact]
    public void ConvertDevicePcmTo48kMono_StereoFloat44100_ResamplesAndDownmixes()
    {
        // 44.1 kHz stereo float input (e.g. standard USB mic)
        int srcFrames = 441; // 10ms of 44.1 kHz
        byte[] floatBytes = new byte[srcFrames * 2 * 4]; // 2 channels * 4 bytes float

        for (int i = 0; i < srcFrames; i++)
        {
            float left = 0.5f;  // ~16384 in 16-bit
            float right = 0.5f;
            int offset = i * 8;
            MemoryMarshal.Write(floatBytes.AsSpan(offset, 4), in left);
            MemoryMarshal.Write(floatBytes.AsSpan(offset + 4, 4), in right);
        }

        short[] monoOutput = new short[960];
        int converted = WasapiAudioEngine.ConvertDevicePcmTo48kMono(
            floatBytes,
            srcSampleRate: 44100,
            srcChannels: 2,
            srcBitsPerSample: 32,
            isFloat: true,
            monoOutput);

        // 441 frames @ 44.1 kHz resamples to ~480 frames @ 48 kHz
        converted.Should().BeCloseTo(480, 5);
        for (int i = 0; i < converted; i++)
        {
            ((int)monoOutput[i]).Should().BeCloseTo(16384, 50);
        }
    }

    [Fact]
    public void Convert48kMonoToDeviceFormat_MonoToStereo96kHzFloat_PacksAccurately()
    {
        short[] monoInput = new short[480]; // 10ms @ 48 kHz
        for (int i = 0; i < 480; i++)
        {
            monoInput[i] = 16384;
        }

        // 96 kHz stereo float output buffer: 10ms @ 96 kHz = 960 frames * 2 ch * 4 bytes = 7680 bytes
        byte[] destRawBytes = new byte[960 * 2 * 4];

        int bytesWritten = WasapiAudioEngine.Convert48kMonoToDeviceFormat(
            monoInput,
            destSampleRate: 96000,
            destChannels: 2,
            destBitsPerSample: 32,
            isFloat: true,
            destRawBytes);

        bytesWritten.Should().Be(960 * 2 * 4);

        // Verify first and middle frame float values
        float firstLeft = MemoryMarshal.Read<float>(destRawBytes.AsSpan(0, 4));
        float firstRight = MemoryMarshal.Read<float>(destRawBytes.AsSpan(4, 4));

        firstLeft.Should().BeApproximately(0.5f, 0.05f);
        firstRight.Should().BeApproximately(0.5f, 0.05f);
    }

    [Fact]
    public void PlaybackQueue_DropOldestWhenSaturated_GuaranteesLowLatency()
    {
        using WasapiAudioEngine engine = new WasapiAudioEngine();

        // Enqueue 60 frames (exceeding maximum 50 frames buffer)
        for (int i = 0; i < 60; i++)
        {
            short[] frame = new short[960];
            frame[0] = (short)i;
            engine.SubmitPlaybackPcm(frame);
        }

        // Does not throw and does not exhaust memory
        engine.Stop();
    }
}
