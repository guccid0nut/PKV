// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NamedPipeStreamingTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Ipc;
using ReplayRenderer.Service.Ipc;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class NamedPipeStreamingTests(ITestOutputHelper output)
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void NamedPipeFrameServer_PropertiesAndDimensions_InitializeCorrectly()
    {
        _output.WriteLine("[Test] Testing NamedPipeFrameServer property initialization...");
        ILogger<NamedPipeFrameServer> logger = _loggerFactory.CreateLogger<NamedPipeFrameServer>();

        string pipeName = "test_pipe_" + Guid.NewGuid().ToString("N");
        NamedPipeFrameServer server = new(logger, pipeName, 1920, 1080);

        Assert.Equal(pipeName, server.PipeName);
        Assert.Equal($@"\\.\pipe\{pipeName}", server.PipePath);
        Assert.Equal(1920u, server.Width);
        Assert.Equal(1080u, server.Height);
        Assert.Equal(1920u * 1080u * 4u, server.FrameSizeBytes); // 8,294,400 bytes
        Assert.Equal(0, server.TotalFramesReceived);
        Assert.Equal(0, server.TotalBytesReceived);
        Assert.False(server.IsConnected);

        _output.WriteLine($"[Test] FrameSizeBytes verified: {server.FrameSizeBytes:N0} bytes.");
    }

    [Fact]
    public async Task NamedPipeFrameServer_ClientConnectAndFrameStreaming_DeliversPixelExactFrames()
    {
        _output.WriteLine("[Test] Starting NamedPipeFrameServer end-to-end streaming test...");
        ILogger<NamedPipeFrameServer> logger = _loggerFactory.CreateLogger<NamedPipeFrameServer>();

        string pipeName = "hon_test_frame_pipe_" + Guid.NewGuid().ToString("N");
        uint testWidth = 128;
        uint testHeight = 64;
        uint frameSize = testWidth * testHeight * 4; // 32,768 bytes

        await using NamedPipeFrameServer server = new(logger, pipeName, testWidth, testHeight);

        TaskCompletionSource<bool> frame1Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> frame2Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int frameCounter = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await server.StartAsync(frameData =>
        {
            int current = Interlocked.Increment(ref frameCounter);
            _output.WriteLine($"[Server Callback] Received frame #{current} ({frameData.Length} bytes).");

            if (current == 1)
            {
                Assert.Equal((int)frameSize, frameData.Length);
                Assert.Equal(0xAA, frameData.Span[0]);
                Assert.Equal(0xAA, frameData.Span[frameData.Length - 1]);
                frame1Tcs.TrySetResult(true);
            }
            else if (current == 2)
            {
                Assert.Equal((int)frameSize, frameData.Length);
                Assert.Equal(0xBB, frameData.Span[0]);
                Assert.Equal(0xBB, frameData.Span[frameData.Length - 1]);
                frame2Tcs.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        }, cts.Token);

        _output.WriteLine($"[Test] Server started on pipe {server.PipePath}. Connecting mock client...");

        // Connect client
        await using NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cts.Token);
        _output.WriteLine("[Test] Client connected to pipe.");

        // Frame 1 payload
        byte[] frame1Bytes = new byte[frameSize];
        Array.Fill<byte>(frame1Bytes, 0xAA);

        // Frame 2 payload
        byte[] frame2Bytes = new byte[frameSize];
        Array.Fill<byte>(frame2Bytes, 0xBB);

        // Stream Frame 1 in smaller chunks to test stream reconstruction
        int halfSize = (int)frameSize / 2;
        await client.WriteAsync(frame1Bytes.AsMemory(0, halfSize), cts.Token);
        await client.FlushAsync(cts.Token);
        await Task.Delay(50, cts.Token);
        await client.WriteAsync(frame1Bytes.AsMemory(halfSize, halfSize), cts.Token);
        await client.FlushAsync(cts.Token);

        _output.WriteLine("[Test] Frame 1 transmitted in 2 chunks. Awaiting server verification...");
        bool frame1Received = await frame1Tcs.Task.WaitAsync(cts.Token);
        Assert.True(frame1Received);

        // Stream Frame 2 in full
        await client.WriteAsync(frame2Bytes.AsMemory(0, (int)frameSize), cts.Token);
        await client.FlushAsync(cts.Token);

        _output.WriteLine("[Test] Frame 2 transmitted in full. Awaiting server verification...");
        bool frame2Received = await frame2Tcs.Task.WaitAsync(cts.Token);
        Assert.True(frame2Received);

        Assert.Equal(2, server.TotalFramesReceived);
        Assert.Equal((long)frameSize * 2, server.TotalBytesReceived);
        _output.WriteLine(
            $"[Test] Verified total received: {server.TotalFramesReceived} frames, {server.TotalBytesReceived:N0} bytes.");

        // Close client to signal EOF
        client.Close();
        _output.WriteLine("[Test] Client disconnected cleanly.");
    }

    [Fact]
    public async Task NamedPipeFrameServer_CancellationToken_CancelsReadLoopWithoutHanging()
    {
        _output.WriteLine("[Test] Testing cancellation token responsiveness on NamedPipeFrameServer...");
        ILogger<NamedPipeFrameServer> logger = _loggerFactory.CreateLogger<NamedPipeFrameServer>();

        string pipeName = "hon_cancel_pipe_" + Guid.NewGuid().ToString("N");
        await using NamedPipeFrameServer server = new(logger, pipeName, 64, 64);

        using CancellationTokenSource cts = new();

        await server.StartAsync(frame => ValueTask.CompletedTask, cts.Token);
        _output.WriteLine("[Test] Server started and listening for connection...");

        // Trigger cancellation while waiting for connection
        cts.Cancel();
        _output.WriteLine("[Test] CancellationToken canceled.");

        // Give a brief window to ensure no exceptions or unhandled tasks
        await Task.Delay(100);

        Assert.False(server.IsConnected);
        _output.WriteLine("[Test] Server canceled cleanly without throwing unhandled exceptions.");
    }

    [Fact]
    public void WillowmakerIpcBridge_CreateCommandPacket_GeneratesExactBinaryHeaderAndPayload()
    {
        _output.WriteLine("[Test] Validating WillowmakerIpcBridge packet binary formatting and opcode headers...");

        string command = "StartReplay \"replays/match_test.honreplay\"";
        byte[] packet = WillowmakerIpcBridge.CreateCommandPacket(command);

        // Header: Magic (2 bytes) + OpCode (2 bytes) + Length (4 bytes) + RequestId (4 bytes) = 12 bytes
        Assert.Equal(WillowmakerIpcMessage.HeaderSize + Encoding.UTF8.GetByteCount(command), packet.Length);

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0, 2));
        ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4, 4));
        uint requestId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
        string payload = Encoding.UTF8.GetString(packet, WillowmakerIpcMessage.HeaderSize, payloadLen);

        Assert.Equal(WillowmakerIpcBridge.WillowmakerMagic, magic);
        Assert.Equal(WillowmakerIpcBridge.OpCodeExecuteCommand, opCode);
        Assert.Equal(0u, requestId);
        Assert.Equal(command, payload);

        _output.WriteLine(
            $"[Test] Packet verified: Magic=0x{magic:X4}, OpCode={opCode}, Len={payloadLen}, Payload='{payload}'");
    }

    [Fact]
    public void WillowmakerIpcBridge_CreatePacket_PongOpCode_FormatsCorrectly()
    {
        _output.WriteLine("[Test] Validating Pong packet creation...");

        byte[] packet = WillowmakerIpcBridge.CreatePacket(WillowmakerIpcBridge.OpCodePong, "PONG");

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0, 2));
        ushort opCode = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4, 4));
        uint requestId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
        string payload = Encoding.UTF8.GetString(packet, WillowmakerIpcMessage.HeaderSize, payloadLen);

        Assert.Equal(WillowmakerIpcBridge.WillowmakerMagic, magic);
        Assert.Equal(WillowmakerIpcBridge.OpCodePong, opCode);
        Assert.Equal(0u, requestId);
        Assert.Equal("PONG", payload);
        Assert.Equal(4, payloadLen);
    }
}
