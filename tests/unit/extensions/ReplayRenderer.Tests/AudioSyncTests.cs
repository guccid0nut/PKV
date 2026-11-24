// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AudioSyncTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Audio;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class AudioSyncTests(ITestOutputHelper output)
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void AudioFrameSyncBuffer_ConfigurationMath_CalculatesExactFrameBytes()
    {
        _output.WriteLine(
            "[Test] Validating AudioFrameSyncBuffer configuration math for standard 48kHz stereo 16-bit PCM @ 60 FPS...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer48k = new(logger);

        _output.WriteLine(
            $"[Test] Configured: Rate={buffer48k.SampleRate}, Ch={buffer48k.Channels}, Bits={buffer48k.BitsPerSample}, FPS={buffer48k.TargetFps}, BytesPerFrame={buffer48k.BytesPerFrame}");
        Assert.Equal(48000, buffer48k.SampleRate);
        Assert.Equal(2, buffer48k.Channels);
        Assert.Equal(16, buffer48k.BitsPerSample);
        Assert.Equal(60, buffer48k.TargetFps);
        Assert.Equal(3200, buffer48k.BytesPerFrame); // 800 samples/chan * 2 chan * 2 bytes = 3200 bytes

        _output.WriteLine("[Test] Validating 96kHz 2-channel 16-bit @ 60 FPS...");
        AudioFrameSyncBuffer buffer96k = new(logger, 96000);
        Assert.Equal(6400, buffer96k.BytesPerFrame); // 1600 samples/chan * 2 chan * 2 bytes = 6400 bytes

        _output.WriteLine("[Test] Validating 48kHz 6-channel (5.1 surround) 16-bit @ 60 FPS...");
        AudioFrameSyncBuffer buffer51 = new(logger, 48000, 6);
        Assert.Equal(9600, buffer51.BytesPerFrame); // 800 samples/chan * 6 chan * 2 bytes = 9600 bytes
    }

    [Fact]
    public void AudioFrameSyncBuffer_EnqueueEmptySpan_ReturnsFalse()
    {
        _output.WriteLine("[Test] Testing TryEnqueueAudio with empty span...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger);
        bool result = buffer.TryEnqueueAudio([]);

        _output.WriteLine($"[Test] TryEnqueueAudio(Empty) returned: {result}");
        Assert.False(result);
        Assert.Equal(0, buffer.TotalBytesEnqueued);
    }

    [Fact]
    public void AudioFrameSyncBuffer_ExactFrameEnqueueDequeue_PreservesDataIntegrity()
    {
        _output.WriteLine("[Test] Testing exact 3200-byte frame enqueue and dequeue...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger);

        byte[] inputFrame = new byte[3200];
        for (int i = 0; i < inputFrame.Length; i++) inputFrame[i] = (byte)(i % 256);

        bool enqueueSuccess = buffer.TryEnqueueAudio(inputFrame);
        Assert.True(enqueueSuccess);
        Assert.Equal(3200, buffer.TotalBytesEnqueued);
        _output.WriteLine($"[Test] Enqueued {buffer.TotalBytesEnqueued} bytes.");

        byte[] outputFrame = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, outputFrame.Length);
        Assert.Equal(1, buffer.TotalFramesProcessed);
        Assert.Equal(0, buffer.UnderrunFramesCount);
        Assert.Equal(3200, buffer.TotalBytesDequeued);

        for (int i = 0; i < 3200; i++) Assert.Equal(inputFrame[i], outputFrame[i]);
        _output.WriteLine("[Test] Data integrity verified for single exact frame.");
    }

    [Fact]
    public void AudioFrameSyncBuffer_PartialChunkAccumulation_SlicesExactFrames()
    {
        _output.WriteLine("[Test] Testing partial chunk accumulation (1000 + 1500 + 1000 = 3500 bytes)...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger);

        byte[] chunk1 = new byte[1000];
        byte[] chunk2 = new byte[1500];
        byte[] chunk3 = new byte[1000];

        Array.Fill<byte>(chunk1, 0xAA);
        Array.Fill<byte>(chunk2, 0xBB);
        Array.Fill<byte>(chunk3, 0xCC);

        buffer.TryEnqueueAudio(chunk1);
        buffer.TryEnqueueAudio(chunk2);
        buffer.TryEnqueueAudio(chunk3);

        Assert.Equal(3500, buffer.TotalBytesEnqueued);
        _output.WriteLine($"[Test] Enqueued 3 chunks, total: {buffer.TotalBytesEnqueued} bytes.");

        // First frame: 3200 bytes (1000 of 0xAA, 1500 of 0xBB, 700 of 0xCC)
        byte[] frame1 = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, frame1.Length);
        Assert.Equal(1, buffer.TotalFramesProcessed);
        Assert.Equal(0, buffer.UnderrunFramesCount);
        Assert.Equal(3200, buffer.TotalBytesDequeued);

        // Verify chunk1 portion
        for (int i = 0; i < 1000; i++) Assert.Equal(0xAA, frame1[i]);
        // Verify chunk2 portion
        for (int i = 1000; i < 2500; i++) Assert.Equal(0xBB, frame1[i]);
        // Verify chunk3 portion (700 bytes)
        for (int i = 2500; i < 3200; i++) Assert.Equal(0xCC, frame1[i]);

        _output.WriteLine("[Test] Frame 1 sliced and validated from partial chunks.");

        // Remaining in buffer is 300 bytes (3500 - 3200 = 300).
        // Asking for next frame will cause an underrun because 300 < 3200.
        byte[] frame2 = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, frame2.Length);
        Assert.Equal(2, buffer.TotalFramesProcessed);
        Assert.Equal(1, buffer.UnderrunFramesCount);

        // Silence buffer should be all zeros
        for (int i = 0; i < 3200; i++) Assert.Equal(0, frame2[i]);
        _output.WriteLine("[Test] Frame 2 underrun generated 3200 bytes of 0-PCM silence as expected.");
    }

    [Fact]
    public void AudioFrameSyncBuffer_MultiFrameEnqueue_DequeuesSequentialFramesInFIFOOrder()
    {
        _output.WriteLine("[Test] Testing 5 consecutive frames (16,000 bytes) FIFO order...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger);

        byte[] bigStream = new byte[3200 * 5];
        for (int f = 0; f < 5; f++)
        for (int b = 0; b < 3200; b++)
            bigStream[f * 3200 + b] = (byte)(f + 1);

        buffer.TryEnqueueAudio(bigStream);
        Assert.Equal(16000, buffer.TotalBytesEnqueued);

        for (int f = 0; f < 5; f++)
        {
            byte[] frame = buffer.GetNextFrameAudioBlock();
            Assert.Equal(3200, frame.Length);
            byte expectedVal = (byte)(f + 1);

            for (int b = 0; b < 3200; b++) Assert.Equal(expectedVal, frame[b]);
            _output.WriteLine($"[Test] Frame #{f + 1} verified with expected marker byte 0x{expectedVal:X2}.");
        }

        Assert.Equal(5, buffer.TotalFramesProcessed);
        Assert.Equal(0, buffer.UnderrunFramesCount);
        Assert.Equal(16000, buffer.TotalBytesDequeued);
    }

    [Fact]
    public void AudioFrameSyncBuffer_OverflowTruncation_DropsExcessToMaintainMemorySafety()
    {
        _output.WriteLine("[Test] Testing overflow protection (> 5 seconds of audio = 960,000 bytes)...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger);

        // 5 seconds limit = 3200 * 60 * 5 = 960,000 bytes.
        // Let's enqueue 970,000 bytes of 0x11, then enqueue 3200 bytes of 0x22.
        byte[] excessiveAudio = new byte[970000];
        Array.Fill<byte>(excessiveAudio, 0x11);
        buffer.TryEnqueueAudio(excessiveAudio);

        byte[] freshAudio = new byte[3200];
        Array.Fill<byte>(freshAudio, 0x22);
        buffer.TryEnqueueAudio(freshAudio);

        _output.WriteLine($"[Test] Total bytes enqueued across overflow: {buffer.TotalBytesEnqueued}");

        byte[] frame = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, frame.Length);
        Assert.Equal(0x22, frame[0]);
        _output.WriteLine("[Test] Truncation successfully purged stale buffer and retained fresh frame.");
    }

    [Fact]
    public void AudioFrameSyncBuffer_Reset_ClearsAllCountersAndAccumulator()
    {
        _output.WriteLine("[Test] Testing AudioFrameSyncBuffer.Reset...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger);
        byte[] testData = new byte[3200];
        buffer.TryEnqueueAudio(testData);
        buffer.GetNextFrameAudioBlock();
        buffer.GetNextFrameAudioBlock(); // causes underrun

        Assert.True(buffer.TotalFramesProcessed > 0);
        Assert.True(buffer.UnderrunFramesCount > 0);
        Assert.True(buffer.TotalBytesEnqueued > 0);

        buffer.Reset();
        _output.WriteLine("[Test] Reset invoked.");

        Assert.Equal(0, buffer.TotalFramesProcessed);
        Assert.Equal(0, buffer.UnderrunFramesCount);
        Assert.Equal(0, buffer.TotalBytesEnqueued);
        Assert.Equal(0, buffer.TotalBytesDequeued);

        // Next dequeue should be underrun frame
        byte[] silentFrame = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, silentFrame.Length);
        Assert.Equal(1, buffer.TotalFramesProcessed);
        Assert.Equal(1, buffer.UnderrunFramesCount);
    }
}
