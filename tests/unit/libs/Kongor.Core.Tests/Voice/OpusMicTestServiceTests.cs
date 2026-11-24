// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: OpusMicTestServiceTests.cs
// System: ProjectKongor - Modern Voice Loopback & Microphone Diagnostic Tests
// Policy: Strict Explicit Typing (NO 'var') - Sub-2s Automated Unit Tests
// ============================================================================

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Services.Voice;
using Xunit;

namespace Kongor.Core.Tests.Voice;

public sealed class OpusMicTestServiceTests
{
    [Fact]
    public void CreateWavFile_GeneratesValidRiffHeader()
    {
        short[] pcm = new short[960]; // 20ms at 48 kHz
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(i * 0.1) * 8000.0);
        }

        byte[] wavBytes = OpusMicTestService.CreateWavFile(pcm, 48000, 1);

        wavBytes.Length.Should().Be(44 + (960 * sizeof(short)));

        // RIFF header validation
        string riff = Encoding.ASCII.GetString(wavBytes, 0, 4);
        riff.Should().Be("RIFF");

        string wave = Encoding.ASCII.GetString(wavBytes, 8, 4);
        wave.Should().Be("WAVE");

        string fmt = Encoding.ASCII.GetString(wavBytes, 12, 4);
        fmt.Should().Be("fmt ");

        ushort audioFormat = BitConverter.ToUInt16(wavBytes, 20);
        audioFormat.Should().Be(1); // PCM

        ushort channels = BitConverter.ToUInt16(wavBytes, 22);
        channels.Should().Be(1);

        uint sampleRate = BitConverter.ToUInt32(wavBytes, 24);
        sampleRate.Should().Be(48000);

        ushort bitsPerSample = BitConverter.ToUInt16(wavBytes, 34);
        bitsPerSample.Should().Be(16);

        string data = Encoding.ASCII.GetString(wavBytes, 36, 4);
        data.Should().Be("data");

        uint dataSize = BitConverter.ToUInt32(wavBytes, 40);
        dataSize.Should().Be(960 * sizeof(short));
    }

    [Fact]
    public void HardwareDetection_ExecutesSafelyWithoutExceptions()
    {
        bool hasMic = OpusMicTestService.HasMicrophone();
        bool hasPlayback = OpusMicTestService.HasPlaybackDevice();

        if (OperatingSystem.IsWindows())
        {
            // On Windows dev machines with audio devices, these execute cleanly
            hasPlayback.Should().BeTrue();
        }
    }

    [Fact]
    public async Task RecordAndAuditionAsync_WhenCancelledImmediately_HandlesCleanly()
    {
        if (!OperatingSystem.IsWindows() || !OpusMicTestService.HasMicrophone())
        {
            return;
        }

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        OpusMicTestResult result = await OpusMicTestService.RecordAndAuditionAsync(
            durationSeconds: 1,
            sampleRate: 48000,
            playBackImmediately: false,
            cancellationToken: cts.Token);

        // Immediate cancellation produces 0 samples cleanly without throwing
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Zero audio samples");
    }
}
