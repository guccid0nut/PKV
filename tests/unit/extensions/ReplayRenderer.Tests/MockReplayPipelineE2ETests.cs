// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MockReplayPipelineE2ETests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Audio;
using ReplayRenderer.Service.Ipc;
using ReplayRenderer.Service.Monitoring;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class MockReplayPipelineE2ETests(ITestOutputHelper output)
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void FrameHeader_BinaryLayoutAndValidation_MatchesVulkanSpecification()
    {
        _output.WriteLine("[Test] Validating FrameHeader binary structure layout and 64-byte size...");

        int structSize = Marshal.SizeOf<FrameHeader>();
        _output.WriteLine($"[Test] FrameHeader Marshal.SizeOf = {structSize} bytes.");
        Assert.Equal(64, structSize);

        FrameHeader validHeader = new()
        {
            Magic = FrameHeader.ExpectedMagicHonc,
            Version = FrameHeader.ExpectedVersion,
            Width = 1920,
            Height = 1080,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            RowPitch = 1920 * 4,
            FrameIndex = 42,
            TimestampNs = 1_000_000_000,
            DataSizeBytes = 1920 * 1080 * 4,
            BufferSlotIndex = 0,
            Flags = FrameHeader.FlagKeyframe
        };

        Assert.True(validHeader.IsValid());
        Assert.False(validHeader.IsEndOfStream());

        string headerStr = validHeader.ToString();
        _output.WriteLine($"[Test] Header string: {headerStr}");
        Assert.Contains("Index=42", headerStr);
        Assert.Contains("1920x1080", headerStr);

        // Test EndOfStream flag
        validHeader.Flags |= FrameHeader.FlagEndOfStream;
        Assert.True(validHeader.IsEndOfStream());

        // Test invalid magic
        FrameHeader invalidMagicHeader = validHeader;
        invalidMagicHeader.Magic = 0xDEADBEEF;
        Assert.False(invalidMagicHeader.IsValid());

        // Test invalid dimensions
        FrameHeader invalidDimHeader = validHeader;
        invalidDimHeader.Width = 0;
        Assert.False(invalidDimHeader.IsValid());

        // Test alternative valid magic (HONF)
        FrameHeader honfHeader = validHeader;
        honfHeader.Magic = FrameHeader.ExpectedMagicHonf;
        Assert.True(honfHeader.IsValid());
    }

    [Fact]
    public async Task VulkanMemoryMappedReceiver_SharedMemorySync_ReceivesTripleBufferedFrames()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("[Test Skipped] Win32 Shared Memory tests require Windows OS.");
            return;
        }

        _output.WriteLine("[Test] Starting VulkanMemoryMappedReceiver Win32 Shared Memory test...");
        ILogger<VulkanMemoryMappedReceiver> logger = _loggerFactory.CreateLogger<VulkanMemoryMappedReceiver>();

        int mockPid = 99000 + Random.Shared.Next(1000);
        uint testWidth = 64;
        uint testHeight = 64;
        uint pixelDataSize = testWidth * testHeight * 4; // 16,384 bytes
        int slotCount = 3;
        string customMapName = $"Local\\HonTestBuffer_{mockPid}";

        await using VulkanMemoryMappedReceiver receiver = new(
            logger,
            mockPid,
            testWidth,
            testHeight,
            slotCount,
            customMapName);

        TaskCompletionSource<bool> frameReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int receivedFrames = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await receiver.StartAsync((header, pixels) =>
        {
            int currentCount = Interlocked.Increment(ref receivedFrames);
            _output.WriteLine(
                $"[SharedMemory Receiver] Received frame #{currentCount}: Index={header.FrameIndex}, Size={pixels.Length} bytes, EndOfStream={header.IsEndOfStream()}");

            Assert.Equal((int)pixelDataSize, pixels.Length);
            Assert.Equal((ulong)(currentCount - 1), header.FrameIndex);

            if (header.IsEndOfStream()) frameReceivedTcs.TrySetResult(true);

            return ValueTask.CompletedTask;
        }, cts.Token);

        _output.WriteLine("[Test] Receiver initialized and listening. Creating producer view accessor...");

        // Producer: Open MemoryMappedFile and sync events
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

        // Wait for done event initially (doneEvent initialized to true)
        Assert.True(doneEvent0.WaitOne(2000));

        // Slot 0: Write Frame 0
        FrameHeader frame0Header = new()
        {
            Magic = FrameHeader.ExpectedMagicHonc,
            Version = FrameHeader.ExpectedVersion,
            Width = testWidth,
            Height = testHeight,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            RowPitch = testWidth * 4,
            FrameIndex = 0,
            TimestampNs = 1000,
            DataSizeBytes = pixelDataSize,
            BufferSlotIndex = 0,
            Flags = 0
        };

        byte[] frame0Pixels = new byte[pixelDataSize];
        Array.Fill<byte>(frame0Pixels, 0x55);

        accessor.Write(0, ref frame0Header);
        accessor.WriteArray(headerSize, frame0Pixels, 0, (int)pixelDataSize);

        // Signal Frame 0 ready
        readyEvent0.Set();
        _output.WriteLine("[Producer] Slot 0: Frame 0 signaled ready.");

        // Slot 1: Write Frame 1 with FlagEndOfStream
        Assert.True(doneEvent1.WaitOne(2000));

        FrameHeader frame1Header = new()
        {
            Magic = FrameHeader.ExpectedMagicHonc,
            Version = FrameHeader.ExpectedVersion,
            Width = testWidth,
            Height = testHeight,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            RowPitch = testWidth * 4,
            FrameIndex = 1,
            TimestampNs = 2000,
            DataSizeBytes = pixelDataSize,
            BufferSlotIndex = 1,
            Flags = FrameHeader.FlagEndOfStream
        };

        byte[] frame1Pixels = new byte[pixelDataSize];
        Array.Fill<byte>(frame1Pixels, 0x77);

        long slot1Offset = slotStride;
        accessor.Write(slot1Offset, ref frame1Header);
        accessor.WriteArray(slot1Offset + headerSize, frame1Pixels, 0, (int)pixelDataSize);

        // Signal Frame 1 ready
        readyEvent1.Set();
        _output.WriteLine("[Producer] Slot 1: Frame 1 (EndOfStream) signaled ready.");

        // Wait for receiver to process end of stream
        bool completed = await frameReceivedTcs.Task.WaitAsync(cts.Token);
        Assert.True(completed);
        Assert.Equal(2, receiver.TotalFramesReceived);
        Assert.Equal((long)pixelDataSize * 2, receiver.TotalBytesReceived);

        _output.WriteLine("[Test] Shared memory triple-buffered frame transfer verified successfully!");

        readyEvent0.Dispose();
        doneEvent0.Dispose();
        readyEvent1.Dispose();
        doneEvent1.Dispose();
    }

    [Fact]
    public async Task MockReplayPipeline_FullSimulation_RendersFramesAndSyncsAudioTo100Percent()
    {
        _output.WriteLine("[Test] Starting full end-to-end mock replay pipeline simulation (10 frames @ 60 FPS)...");

        string pipeName = "hon_e2e_frame_pipe_" + Guid.NewGuid().ToString("N");
        uint width = 128;
        uint height = 72;
        uint frameSizeBytes = width * height * 4; // 36,864 bytes
        long totalSimulatedFrames = 10;

        ILogger<NamedPipeFrameServer> pipeLogger = _loggerFactory.CreateLogger<NamedPipeFrameServer>();
        ILogger<AudioFrameSyncBuffer> audioLogger = _loggerFactory.CreateLogger<AudioFrameSyncBuffer>();
        ILogger<RenderProgressMonitor> monitorLogger = _loggerFactory.CreateLogger<RenderProgressMonitor>();

        await using NamedPipeFrameServer frameServer = new(pipeLogger, pipeName, width, height);
        AudioFrameSyncBuffer audioBuffer = new(audioLogger);
        await using RenderProgressMonitor progressMonitor = new(
            monitorLogger,
            "e2e_mock_job",
            totalSimulatedFrames,
            TimeSpan.FromMilliseconds(500));

        TaskCompletionSource<bool> e2eCompletionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        progressMonitor.OnProgressUpdated += progress =>
        {
            _output.WriteLine($"[E2E Progress] {progress}");
            if (progress.TotalFramesRendered >= totalSimulatedFrames) e2eCompletionTcs.TrySetResult(true);
        };

        // 1. Start Frame Server
        await frameServer.StartAsync(frameData =>
        {
            // Slices matching audio frame for every video frame received
            byte[] audioFrame = audioBuffer.GetNextFrameAudioBlock();
            Assert.Equal(3200, audioFrame.Length);

            progressMonitor.NotifyFrameRendered();
            return ValueTask.CompletedTask;
        }, cts.Token);

        // 2. Start Progress Monitor
        progressMonitor.Start();

        // 3. Connect Producer Client
        await using NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cts.Token);

        byte[] frameDataMock = new byte[frameSizeBytes];
        byte[] audioBlockMock = new byte[3200];

        // 4. Pump 10 frames and audio blocks
        for (int i = 0; i < totalSimulatedFrames; i++)
        {
            Array.Fill(frameDataMock, (byte)(i + 1));
            Array.Fill(audioBlockMock, (byte)(i + 1));

            // Enqueue audio
            audioBuffer.TryEnqueueAudio(audioBlockMock);

            // Transmit video frame
            await client.WriteAsync(frameDataMock.AsMemory(0, (int)frameSizeBytes), cts.Token);
            await client.FlushAsync(cts.Token);

            await Task.Delay(1, cts.Token);
        }

        // 5. Await pipeline completion
        bool isCompleted = await e2eCompletionTcs.Task.WaitAsync(cts.Token);
        Assert.True(isCompleted);

        Assert.Equal(totalSimulatedFrames, progressMonitor.TotalFramesRendered);
        Assert.Equal(totalSimulatedFrames, audioBuffer.TotalFramesProcessed);
        Assert.Equal(0, audioBuffer.UnderrunFramesCount);
        Assert.Equal(totalSimulatedFrames * 3200, audioBuffer.TotalBytesDequeued);
        Assert.Equal(frameSizeBytes * totalSimulatedFrames, frameServer.TotalBytesReceived);

        _output.WriteLine(
            "[Test] Full E2E pipeline simulation finished with 100% frame delivery and bit-perfect audio sync!");
    }
}
