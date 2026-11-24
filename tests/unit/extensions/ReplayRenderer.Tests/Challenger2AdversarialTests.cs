// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Challenger2AdversarialTests.cs
// System: NEXUS Replay Video Renderer Test Suite - Empirical Challenger 2
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Audio;
using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Ipc;
using ReplayRenderer.Service.Models;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class Challenger2AdversarialTests(ITestOutputHelper output)
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void AudioFrameSyncBuffer_Adversarial_EmptyAndOddSizedSpans_MaintainStrictBoundaryIntegrity()
    {
        _output.WriteLine(
            "[Adversarial Test] Testing empty spans, 1-byte, 7-byte, and odd-sized chunk accumulations...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger, 48000, 2, 16, 60);

        // 1. Enqueue empty span
        bool emptyResult = buffer.TryEnqueueAudio([]);
        Assert.False(emptyResult);
        Assert.Equal(0, buffer.TotalBytesEnqueued);

        // 2. Enqueue 3200 single-byte chunks to test extreme fragmentation
        byte[] expectedPayload = new byte[3200];
        for (int i = 0; i < 3200; i++)
        {
            byte b = (byte)(i % 251);
            expectedPayload[i] = b;
            byte[] singleByte = [b];
            bool enq = buffer.TryEnqueueAudio(singleByte);
            Assert.True(enq);
        }

        Assert.Equal(3200, buffer.TotalBytesEnqueued);

        // 3. Dequeue frame and assert byte-exact match
        byte[] frame = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, frame.Length);
        Assert.Equal(1, buffer.TotalFramesProcessed);
        Assert.Equal(0, buffer.UnderrunFramesCount);
        Assert.Equal(3200, buffer.TotalBytesDequeued);

        for (int i = 0; i < 3200; i++) Assert.Equal(expectedPayload[i], frame[i]);

        _output.WriteLine("[Adversarial Test] Sliced 3200 individual 1-byte chunks with 100% data integrity.");
    }

    [Fact]
    public void AudioFrameSyncBuffer_Adversarial_BufferOverflowBoundary_DropsOldDataAtExactThreshold()
    {
        _output.WriteLine("[Adversarial Test] Testing exact buffer overflow boundary (5 seconds = 960,000 bytes)...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger, 48000, 2, 16, 60);
        int maxRetentionBytes = buffer.BytesPerFrame * buffer.TargetFps * 5; // 3200 * 60 * 5 = 960,000 bytes

        // Enqueue exactly 960,000 bytes of 0x11
        byte[] fillMax = new byte[maxRetentionBytes];
        Array.Fill<byte>(fillMax, 0x11);
        buffer.TryEnqueueAudio(fillMax);

        Assert.Equal(960000, buffer.TotalBytesEnqueued);

        // Dequeue one frame: should be 0x11
        byte[] frameBeforeOverflow = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, frameBeforeOverflow.Length);
        Assert.Equal(0x11, frameBeforeOverflow[0]);

        // Reset and test overflow trigger: enqueue 960,001 bytes, then fresh frame 0x99
        buffer.Reset();
        byte[] overflowTrigger = new byte[960001];
        Array.Fill<byte>(overflowTrigger, 0x33);
        buffer.TryEnqueueAudio(overflowTrigger);

        byte[] freshChunk = new byte[3200];
        Array.Fill<byte>(freshChunk, 0x99);
        buffer.TryEnqueueAudio(freshChunk);

        // Dequeue should return the fresh chunk 0x99 because the old buffer was truncated
        byte[] frameAfterOverflow = buffer.GetNextFrameAudioBlock();
        Assert.Equal(3200, frameAfterOverflow.Length);
        Assert.Equal(0x99, frameAfterOverflow[0]);
        Assert.Equal(0x99, frameAfterOverflow[3199]);

        _output.WriteLine(
            "[Adversarial Test] Buffer overflow truncation successfully verified at exact byte boundary.");
    }

    [Fact]
    public void AudioFrameSyncBuffer_Adversarial_ConsecutiveUnderruns_InjectsExactZeroVolumePCM()
    {
        _output.WriteLine("[Adversarial Test] Testing 100 consecutive underruns for zero-volume PCM stability...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger, 48000, 2, 16, 60);

        for (int i = 1; i <= 100; i++)
        {
            byte[] silenceFrame = buffer.GetNextFrameAudioBlock();
            Assert.Equal(3200, silenceFrame.Length);
            Assert.Equal(i, buffer.TotalFramesProcessed);
            Assert.Equal(i, buffer.UnderrunFramesCount);

            // Assert all bytes in silence frame are 0x00
            for (int b = 0; b < silenceFrame.Length; b++) Assert.Equal(0, silenceFrame[b]);
        }

        _output.WriteLine("[Adversarial Test] 100 consecutive underruns generated bit-perfect 0-PCM silence frames.");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AudioFrameSyncBuffer_Adversarial_ConcurrentEnqueueDequeue_IsThreadSafe()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        _output.WriteLine("[Adversarial Test] Testing high-concurrency multi-threaded enqueue and dequeue...");
        ILogger<AudioFrameSyncBuffer> logger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();

        AudioFrameSyncBuffer buffer = new(logger, 48000, 2, 16, 60);

        int producerThreads = 4;
        int chunksPerThread = 50;
        int chunkSize = 640; // 640 bytes * 50 = 32,000 bytes per thread * 4 = 128,000 bytes = 40 frames

        Task[] producerTasks = new Task[producerThreads];
        for (int t = 0; t < producerThreads; t++)
            producerTasks[t] = Task.Run(async () =>
            {
                byte[] chunk = new byte[chunkSize];
                for (int c = 0; c < chunksPerThread; c++)
                {
                    buffer.TryEnqueueAudio(chunk);
                    await Task.Delay(1);
                }
            });

        int dequeuedFrames = 0;
        int totalExpectedFrames = 40;

        Task consumerTask = Task.Run(() =>
        {
            for (int f = 0; f < totalExpectedFrames; f++)
            {
                byte[] frame = buffer.GetNextFrameAudioBlock();
                Assert.Equal(3200, frame.Length);
                Interlocked.Increment(ref dequeuedFrames);
            }
        });

        await Task.WhenAll(producerTasks);
        await consumerTask;

        Assert.Equal(totalExpectedFrames, dequeuedFrames);
        Assert.Equal(totalExpectedFrames, buffer.TotalFramesProcessed);
        _output.WriteLine(
            $"[Adversarial Test] Thread-safety verified across {producerThreads} concurrent producers and consumer.");
    }

    [Fact]
    public async Task NamedPipeFrameServer_Adversarial_AbruptClientDisconnect_RecoversWithoutHanging()
    {
        _output.WriteLine("[Adversarial Test] Testing abrupt client disconnect during partial frame transmission...");
        ILogger<NamedPipeFrameServer> logger = _loggerFactory.CreateLogger<NamedPipeFrameServer>();

        string pipeName = "hon_adversarial_disconnect_" + Guid.NewGuid().ToString("N");
        uint testWidth = 100;
        uint testHeight = 100;
        uint frameSize = testWidth * testHeight * 4; // 40,000 bytes

        await using NamedPipeFrameServer server = new(logger, pipeName, testWidth, testHeight);

        int receivedFrames = 0;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await server.StartAsync(frameData =>
        {
            Interlocked.Increment(ref receivedFrames);
            return ValueTask.CompletedTask;
        }, cts.Token);

        // Connect client and send half of a frame, then abruptly close stream
        await using (NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000, cts.Token);
            byte[] partialData = new byte[10000]; // only 10,000 of 40,000 bytes
            await client.WriteAsync(partialData.AsMemory(0, partialData.Length), cts.Token);
            await client.FlushAsync(cts.Token);
            await Task.Delay(50, cts.Token); // Allow server to read the chunk
            // Abrupt close
            client.Close();
        }

        // Give server time to detect EOF and exit loop
        await Task.Delay(200, cts.Token);

        Assert.False(server.IsConnected);
        Assert.Equal(0, receivedFrames); // Partial frame should not be dispatched as complete frame
        Assert.Equal(10000, server.TotalBytesReceived);

        _output.WriteLine(
            "[Adversarial Test] Server gracefully handled abrupt client disconnect during partial frame without deadlock.");
    }

    [Fact]
    public async Task VulkanMemoryMappedReceiver_Adversarial_CorruptedFrameHeader_SkipsFrameAndAdvancesSlot()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[Test Skipped] Win32 Shared Memory tests require Windows OS.");
            return;
        }

        _output.WriteLine(
            "[Adversarial Test] Testing corrupted frame header rejection and slot advancement in VulkanMemoryMappedReceiver...");
        ILogger<VulkanMemoryMappedReceiver> logger = _loggerFactory.CreateLogger<VulkanMemoryMappedReceiver>();

        int mockPid = 88000 + Random.Shared.Next(1000);
        uint testWidth = 32;
        uint testHeight = 32;
        uint pixelDataSize = testWidth * testHeight * 4; // 4096 bytes
        int slotCount = 3;
        string customMapName = $"Local\\HonAdvTestBuffer_{mockPid}";

        await using VulkanMemoryMappedReceiver receiver = new(
            logger,
            mockPid,
            testWidth,
            testHeight,
            slotCount,
            customMapName);

        TaskCompletionSource<bool> validFrameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int validFramesReceived = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await receiver.StartAsync((header, pixels) =>
        {
            Interlocked.Increment(ref validFramesReceived);
            _output.WriteLine($"[Adversarial Receiver] Valid frame received: Index={header.FrameIndex}");
            if (header.IsEndOfStream()) validFrameTcs.TrySetResult(true);
            return ValueTask.CompletedTask;
        }, cts.Token);

        int headerSize = Marshal.SizeOf<FrameHeader>();
        long slotStride = headerSize + pixelDataSize;
        long totalSize = slotStride * slotCount;

        using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(customMapName, MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor
            accessor = mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);

        EventWaitHandle readyEvent0 = EventWaitHandle.OpenExisting($"Local\\HonFrameReady_{mockPid}_0");
        EventWaitHandle doneEvent0 = EventWaitHandle.OpenExisting($"Local\\HonFrameDone_{mockPid}_0");

        EventWaitHandle readyEvent1 = EventWaitHandle.OpenExisting($"Local\\HonFrameReady_{mockPid}_1");
        EventWaitHandle doneEvent1 = EventWaitHandle.OpenExisting($"Local\\HonFrameDone_{mockPid}_1");

        // Slot 0: Write CORRUPTED header (invalid magic: 0xDEADBEEF)
        Assert.True(doneEvent0.WaitOne(2000));
        FrameHeader corruptHeader = new()
        {
            Magic = 0xDEADBEEF,
            Version = 1,
            Width = testWidth,
            Height = testHeight,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            RowPitch = testWidth * 4,
            FrameIndex = 999,
            DataSizeBytes = pixelDataSize
        };
        accessor.Write(0, ref corruptHeader);
        readyEvent0.Set();
        _output.WriteLine("[Adversarial Producer] Signaled slot 0 with CORRUPT magic header.");

        // Slot 1: Write VALID Frame with FlagEndOfStream
        Assert.True(doneEvent1.WaitOne(2000));
        FrameHeader validHeader = new()
        {
            Magic = FrameHeader.ExpectedMagicHonc,
            Version = FrameHeader.ExpectedVersion,
            Width = testWidth,
            Height = testHeight,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            RowPitch = testWidth * 4,
            FrameIndex = 1,
            DataSizeBytes = pixelDataSize,
            Flags = FrameHeader.FlagEndOfStream
        };
        long slot1Offset = slotStride;
        accessor.Write(slot1Offset, ref validHeader);
        readyEvent1.Set();
        _output.WriteLine("[Adversarial Producer] Signaled slot 1 with VALID EndOfStream header.");

        bool completed = await validFrameTcs.Task.WaitAsync(cts.Token);
        Assert.True(completed);

        // Only the valid frame (Slot 1) should be received and counted in callback
        Assert.Equal(1, validFramesReceived);
        Assert.Equal(1, receiver.TotalFramesReceived);

        _output.WriteLine(
            "[Adversarial Test] Corrupted frame was safely ignored, slot advanced, and valid frame received.");

        readyEvent0.Dispose();
        doneEvent0.Dispose();
        readyEvent1.Dispose();
        doneEvent1.Dispose();
    }

    [Theory]
    [InlineData(VideoCodec.H264Nvenc, 1920u, 1080u, 60, 18, "25M", "40M", "50M", 48000, 2, 320, "\\\\.\\pipe\\vpipe",
        "\\\\.\\pipe\\apipe", false)]
    [InlineData(VideoCodec.HevcNvenc, 3840u, 2160u, 60, 20, "60M", "90M", "120M", 48000, 2, 320, "\\\\.\\pipe\\vpipe",
        "\\\\.\\pipe\\apipe", false)]
    [InlineData(VideoCodec.H264Nvenc, 1920u, 1080u, 120, 16, "35M", "50M", "70M", 96000, 2, 320, "-",
        "\\\\.\\pipe\\apipe", true)]
    [InlineData(VideoCodec.Libx264CpuFallback, 1280u, 720u, 30, 22, "10M", "15M", "20M", 48000, 6, 448,
        "\\\\.\\pipe\\vpipe", "\\\\.\\pipe\\apipe", false)]
    [InlineData(VideoCodec.Libx264CpuFallback, 1920u, 1080u, 60, 18, "20M", "30M", "40M", 44100, 2, 192, "-", "-",
        true)]
    public void FfmpegArgumentBuilder_Adversarial_FullEncodingFlagPermutations_GenerateCompliantCli(
        VideoCodec codec,
        uint width,
        uint height,
        int framerate,
        int cq,
        string vBitrate,
        string maxBitrate,
        string bufSize,
        int audioRate,
        int audioChannels,
        int audioBitrate,
        string videoSource,
        string audioSource,
        bool isStdinVideo)
    {
        _output.WriteLine(
            $"[Adversarial Test] Permutation: Codec={codec}, Res={width}x{height}, FPS={framerate}, CQ={cq}, Rate={audioRate}Hz, Chan={audioChannels}");

        FfmpegEncodingOptions options = new()
        {
            VideoInputSource = videoSource,
            AudioInputSource = audioSource,
            OutputFilePath = "C:\\NexusVideos\\permutation_out.mp4",
            Width = width,
            Height = height,
            Framerate = framerate,
            Codec = codec,
            AudioSampleRate = audioRate,
            AudioChannels = audioChannels,
            AudioBitrateKbps = audioBitrate,
            ConstantQuality = cq,
            VideoBitrate = vBitrate,
            MaxBitrate = maxBitrate,
            BufferSize = bufSize
        };

        string cli = FfmpegArgumentBuilder.BuildArguments(options);
        _output.WriteLine($"[Adversarial Test] CLI output: {cli}");

        // Common invariants
        Assert.StartsWith("-y ", cli);
        Assert.Contains($"-s {width}x{height}", cli);
        Assert.Contains($"-r {framerate}", cli);
        Assert.Contains($"-g {framerate * 2}", cli); // GOP is always Framerate * 2
        Assert.Contains($"-ar {audioRate}", cli);
        Assert.Contains($"-ac {audioChannels}", cli);
        Assert.Contains($"-b:a {audioBitrate}k", cli);
        Assert.Contains("-movflags +faststart", cli);

        // Input source checks
        if (isStdinVideo)
        {
            Assert.Contains("-i -", cli);
        }
        else
        {
            Assert.Contains($"-i \"{videoSource}\"", cli);
            Assert.Contains("-thread_queue_size 1024", cli);
        }

        // Codec specific checks
        switch (codec)
        {
            case VideoCodec.H264Nvenc:
                Assert.Contains("-c:v h264_nvenc", cli);
                Assert.Contains("-preset p7", cli);
                Assert.Contains("-tune hq", cli);
                Assert.Contains($"-cq {cq}", cli);
                Assert.Contains($"-b:v {vBitrate}", cli);
                Assert.Contains($"-maxrate {maxBitrate}", cli);
                Assert.Contains($"-bufsize {bufSize}", cli);
                Assert.Contains("-spatial-aq 1 -temporal-aq 1 -rc-lookahead 32", cli);
                break;

            case VideoCodec.HevcNvenc:
                Assert.Contains("-c:v hevc_nvenc", cli);
                Assert.Contains("-preset p7", cli);
                Assert.Contains("-tune hq", cli);
                int expectedHevcCq = Math.Max(10, cq - 2);
                Assert.Contains($"-cq {expectedHevcCq}", cli);
                Assert.Contains($"-b:v {vBitrate}", cli);
                Assert.Contains($"-maxrate {maxBitrate}", cli);
                Assert.Contains($"-bufsize {bufSize}", cli);
                break;

            case VideoCodec.Libx264CpuFallback:
                Assert.Contains("-c:v libx264", cli);
                Assert.Contains("-preset slow", cli);
                Assert.Contains($"-crf {cq}", cli);
                Assert.Contains("-pix_fmt yuv420p", cli);
                break;
        }
    }
}
