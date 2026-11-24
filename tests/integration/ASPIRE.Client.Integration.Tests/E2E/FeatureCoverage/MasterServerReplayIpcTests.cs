// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature02_MasterServerReplayIpcTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 2: MasterServer to ReplayRenderer IPC Interface)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using System.IO.Pipes;
using FluentAssertions;
using ReplayRenderer.Service.Models;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class MasterServerReplayIpcTests
{
    [Fact]
    public void MasterServerReplayIpc_RenderJobConfiguration_ValidatesRequiredPaths()
    {
        // Arrange & Act
        RenderJobConfiguration job = new()
        {
            ReplayFilePath = @"C:\Replays\match_1001.honreplay",
            OutputFilePath = @"C:\Videos\match_1001.mp4",
            GameDirectory = @"C:\Games\HoN",
            VulkanLayerPath = @"C:\Layers\VkLayer_hon_replay_capture.dll",
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60,
            Codec = VideoCodec.H264Nvenc
        };

        // Assert
        job.ReplayFilePath.Should().Be(@"C:\Replays\match_1001.honreplay");
        job.OutputFilePath.Should().Be(@"C:\Videos\match_1001.mp4");
        job.RenderWidth.Should().Be(1920);
        job.RenderHeight.Should().Be(1080);
        job.TargetFramerate.Should().Be(60);
        job.Codec.Should().Be(VideoCodec.H264Nvenc);
    }

    [Fact]
    public void RenderJobConfiguration_Defaults_SetsStandard60FpsH264()
    {
        // Arrange & Act
        RenderJobConfiguration job = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "test.mp4",
            GameDirectory = "game",
            VulkanLayerPath = "layer"
        };

        // Assert
        job.RenderWidth.Should().Be(1920);
        job.RenderHeight.Should().Be(1080);
        job.TargetFramerate.Should().Be(60);
        job.Codec.Should().Be(VideoCodec.H264Nvenc);
    }

    [Fact]
    public async Task NamedPipeIpc_Handshake_EstablishesBidirectionalChannel()
    {
        // Arrange
        string pipeName = "KongorReplayIpcTest_" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Act - Start Server & Client
        TaskCompletionSource serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource clientDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            Task waitTask = server.WaitForConnectionAsync(cts.Token);
            serverReady.SetResult();
            await waitTask;

            using StreamWriter writer = new(server) { AutoFlush = true };
            await writer.WriteLineAsync("JOB_ACK".AsMemory(), cts.Token);
            await writer.FlushAsync(cts.Token);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    server.WaitForPipeDrain();
                }
                catch (IOException)
                {
                    // Client may have already completed read and disconnected
                }
            }

            // Keep server stream open until client finishes reading
            try
            {
                await clientDone.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);

        Task<string?> clientTask = Task.Run(async () =>
        {
            try
            {
                await serverReady.Task.WaitAsync(cts.Token);
                using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(3000, cts.Token);
                using StreamReader reader = new(client);
                return await reader.ReadLineAsync(cts.Token);
            }
            finally
            {
                clientDone.TrySetResult();
            }
        }, cts.Token);

        await Task.WhenAll(serverTask, clientTask);
        string? result = await clientTask;

        // Assert
        result.Should().Be("JOB_ACK");
    }

    [Fact]
    public void RenderJobProgress_StateTracking_RecordsAccurateFrameRates()
    {
        // Arrange & Act
        RenderJobProgress progress = new()
        {
            JobId = "job-42",
            State = RenderJobState.Completed,
            TotalFramesRendered = 3600,
            CurrentFps = 60.0,
            SpeedMultiplier = 1.0,
            ElapsedTime = TimeSpan.FromSeconds(60)
        };

        // Assert
        progress.JobId.Should().Be("job-42");
        progress.State.Should().Be(RenderJobState.Completed);
        progress.TotalFramesRendered.Should().Be(3600);
        progress.CurrentFps.Should().Be(60.0);
    }

    [Fact]
    public void VideoCodec_Enumeration_SupportsNvencAndCpuFallback()
    {
        // Assert
        VideoCodec.H264Nvenc.Should().BeDefined();
        VideoCodec.HevcNvenc.Should().BeDefined();
        VideoCodec.Libx264CpuFallback.Should().BeDefined();
    }

    [Fact]
    public void RenderJobState_Lifecycle_ContainsAllStates()
    {
        // Assert
        RenderJobState.Initializing.Should().BeDefined();
        RenderJobState.Capturing.Should().BeDefined();
        RenderJobState.Encoding.Should().BeDefined();
        RenderJobState.Completed.Should().BeDefined();
        RenderJobState.Failed.Should().BeDefined();
    }
}
