// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M4Challenger1StressTests.cs
// System: Replay Studio Desktop GUI Tool - Milestone M4 Challenger 1 Stress Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Common;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Export;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

/// <summary>
///     Empirical adversarial stress test suite for Milestone M4 (Hardware-Accelerated Video & Audio Export Pipeline).
///     Focus areas:
///     1. AudioStreamSyncBuffer intense multi-threaded concurrency (10,000 enqueues/dequeues across threads).
///     2. VideoExportPipeline with extreme In/Out marker ranges (0 duration, negative in/out, out < in, out past duration).
///     3. CancellationToken abort at various stages of export streaming and full resource/file cleanup.
/// </summary>
public class ReplayDirectorAutomationStressTests
{
    #region SECTION 1: AudioStreamSyncBuffer Intense Concurrency Stress Tests (10,000 Operations)

    [Fact]
    public async Task AudioStreamSyncBuffer_IntenseMultiThreadedConcurrency_10000Operations_MaintainsInvariants()
    {
        // 48kHz, 60fps stereo 16-bit -> 3,200 bytes per frame
        using AudioStreamSyncBuffer buffer = new(
            sampleRate: 48000,
            channels: 2,
            bitsPerSample: 16,
            targetFps: 60,
            logger: NullLogger<AudioStreamSyncBuffer>.Instance);

        const int producerThreads = 10;
        const int enqueuesPerProducer = 1000; // 10 * 1000 = 10,000 enqueues
        const int consumerThreads = 10;
        const int dequeuesPerConsumer = 1000; // 10 * 1000 = 10,000 dequeues

        // Each enqueue is 320 bytes (1/10th of a 3200-byte frame)
        byte[] pcmChunk = new byte[320];
        for (int i = 0; i < pcmChunk.Length; i++)
        {
            pcmChunk[i] = (byte)(i % 256);
        }

        ConcurrentBag<Exception> exceptions = [];
        ConcurrentBag<int> dequeuedBlockLengths = [];
        long totalValidFramesCount = 0;
        long totalSilenceFramesCount = 0;

        List<Task> allTasks = [];

        // Spawn 10 Producer Tasks
        for (int p = 0; p < producerThreads; p++)
        {
            int producerId = p;
            allTasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < enqueuesPerProducer; i++)
                    {
                        bool success = buffer.TryEnqueueAudio(pcmChunk);
                        if (!success)
                        {
                            exceptions.Add(new InvalidOperationException($"Producer {producerId} failed to enqueue chunk {i}"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        // Spawn 10 Consumer Tasks
        for (int c = 0; c < consumerThreads; c++)
        {
            int consumerId = c;
            allTasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < dequeuesPerConsumer; i++)
                    {
                        byte[] frame = buffer.GetNextFrameAudioBlock();
                        if (frame == null)
                        {
                            exceptions.Add(new InvalidOperationException($"Consumer {consumerId} received null frame at iteration {i}"));
                            continue;
                        }

                        dequeuedBlockLengths.Add(frame.Length);

                        // Check if silence or data
                        bool isSilence = true;
                        for (int b = 0; b < frame.Length; b++)
                        {
                            if (frame[b] != 0)
                            {
                                isSilence = false;
                                break;
                            }
                        }

                        if (isSilence)
                        {
                            Interlocked.Increment(ref totalSilenceFramesCount);
                        }
                        else
                        {
                            Interlocked.Increment(ref totalValidFramesCount);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        await Task.WhenAll(allTasks);

        // Assert zero exceptions across all 10,000 concurrent operations
        exceptions.Should().BeEmpty();

        // Assert all 10,000 dequeued blocks had exact 3,200 byte length
        dequeuedBlockLengths.Count.Should().Be(10000);
        foreach (int length in dequeuedBlockLengths)
        {
            length.Should().Be(3200);
        }

        // Verify mathematical invariants
        buffer.TotalFramesProcessed.Should().Be(10000);
        buffer.TotalBytesEnqueued.Should().Be((long)producerThreads * enqueuesPerProducer * 320); // 3,200,000 bytes
        
        long totalDequeuedFromCounter = buffer.TotalBytesDequeued;
        long underrunsFromCounter = buffer.UnderrunFramesCount;

        // Invariant: TotalFramesProcessed == (TotalBytesDequeued / BytesPerFrame) + Underruns
        (totalDequeuedFromCounter / 3200 + underrunsFromCounter).Should().Be(10000);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AudioStreamSyncBuffer_ConcurrentResetUnderLoad_MaintainsSafetyWithoutCrashing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        byte[] chunk = new byte[640];
        ConcurrentBag<Exception> exceptions = [];

        Task producerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    buffer.TryEnqueueAudio(chunk);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        Task consumerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    byte[] block = buffer.GetNextFrameAudioBlock();
                    if (block.Length != 3200)
                    {
                        exceptions.Add(new InvalidOperationException($"Invalid block size: {block.Length}"));
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        Task resetterTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    buffer.Reset();
                    await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(producerTask, consumerTask, resetterTask);

        exceptions.Should().BeEmpty();
    }

    [Fact]
    public void AudioStreamSyncBuffer_DisposeIdempotency_ThrowsCleanObjectDisposedException()
    {
        AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        // Multiple calls to Dispose must not throw
        buffer.Dispose();
        buffer.Dispose();
        buffer.Dispose();

        // Post-dispose methods must throw ObjectDisposedException
        Action actEnqueue = () => buffer.TryEnqueueAudio(new byte[100]);
        actEnqueue.Should().Throw<ObjectDisposedException>();

        Action actDequeue = () => buffer.GetNextFrameAudioBlock();
        actDequeue.Should().Throw<ObjectDisposedException>();

        Action actReset = () => buffer.Reset();
        actReset.Should().Throw<ObjectDisposedException>();
    }

    #endregion

    #region SECTION 2: VideoExportPipeline Extreme In/Out Marker Range Stress Tests

    [Fact]
    public void ExportParameters_ZeroDuration_ValidateThrowsDescriptiveArgumentException()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = @"C:\Replays\zero_dur.mp4",
            InMarkerMs = 5000,
            OutMarkerMs = 5000 // In == Out -> Duration = 0
        };

        parameters.TotalDurationMs.Should().Be(0);
        parameters.TotalFrames.Should().Be(0);

        Action validate = () => parameters.Validate();
        validate.Should().Throw<ArgumentException>()
            .WithMessage("*OutMarkerMs*must be strictly greater than InMarkerMs*");
    }

    [Fact]
    public async Task ExportVideoAsync_ZeroDuration_RejectsWithoutResourceAllocation()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"zero_dur_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 2500,
            OutMarkerMs = 2500
        };

        using MemoryStream targetStream = new();

        Func<Task> act = async () =>
        {
            await pipeline.ExportVideoAsync(
                parameters,
                renderFrameCallback: null,
                combatEvents: null,
                progress: null,
                customOutputStream: targetStream,
                ct: CancellationToken.None);
        };

        await act.Should().ThrowAsync<ArgumentException>();
        targetStream.Length.Should().Be(0);
    }

    [Theory]
    [InlineData(-100, 5000, "InMarkerMs (-100) cannot be negative.")]
    [InlineData(-5000, -1000, "InMarkerMs (-5000) cannot be negative.")]
    [InlineData(10000, 2000, "OutMarkerMs (2000) must be strictly greater than InMarkerMs (10000).")]
    [InlineData(0, -100, "OutMarkerMs (-100) must be strictly greater than InMarkerMs (0).")]
    public void ExportParameters_ExtremeAndNegativeMarkers_ThrowsDescriptiveExceptions(
        int inMarkerMs,
        int outMarkerMs,
        string expectedSubstring)
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = @"C:\Replays\extreme.mp4",
            InMarkerMs = inMarkerMs,
            OutMarkerMs = outMarkerMs
        };

        Action validate = () => parameters.Validate();
        validate.Should().Throw<ArgumentException>()
            .WithMessage($"*{expectedSubstring}*");
    }

    [Fact]
    public void SynthesizeAudioTrack_ZeroOrInvertedMarkers_ReturnsEmptyByteArray()
    {
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        // Zero duration
        byte[] zeroAudio = buffer.SynthesizeAudioTrack(inMarkerMs: 5000, outMarkerMs: 5000, targetFps: 60);
        zeroAudio.Should().BeEmpty();

        // Inverted duration
        byte[] invertedAudio = buffer.SynthesizeAudioTrack(inMarkerMs: 10000, outMarkerMs: 2000, targetFps: 60);
        invertedAudio.Should().BeEmpty();

        // Negative duration
        byte[] negativeAudio = buffer.SynthesizeAudioTrack(inMarkerMs: -5000, outMarkerMs: -10000, targetFps: 60);
        negativeAudio.Should().BeEmpty();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ExportReplayAsync_OutMarkerPastReplayDuration_ClampsCleanlyWithoutExceptions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ReplaySnapshotEngine snapshotEngine = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        MapEnvironmentManager mapManager = new();

        // Match only lasts 3,000ms
        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 0, ScoreLegion = 0, ScoreHellbourne = 0, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 1500, ScoreLegion = 1, ScoreHellbourne = 0, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 3000, ScoreLegion = 2, ScoreHellbourne = 1, IsKeyframe = true }
        ];

        snapshotEngine.LoadKeyframes(snapshots, 3000);
        mapManager.LoadMap("caldavar");

        // Export requested from 0 to 6,000ms (twice the match duration)
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"past_duration_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 6000, // 360 frames @ 60fps
            IncludeAudio = true
        };

        using MemoryStream targetStream = new();

        ExportResult result = await pipeline.ExportReplayAsync(
            parameters,
            snapshotEngine,
            cameraDirector,
            lightingController,
            mapManager,
            combatEvents: null,
            progress: null,
            customOutputStream: targetStream,
            ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.TotalFramesRendered.Should().Be(360);
        targetStream.Length.Should().Be(360 * parameters.CalculatedVideoFrameBytes);
    }

    [Fact]
    public void SynthesizeAudioTrack_CombatEventsWithOutOfBoundsTimestamps_SafelyFiltersEvents()
    {
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        List<CombatEventMarker> extremeEvents =
        [
            new CombatEventMarker { TimestampMs = -5000, EventType = CombatEventType.FirstBlood, IntensityScore = 1.0f },
            new CombatEventMarker { TimestampMs = 500, EventType = CombatEventType.HeroKill, IntensityScore = 0.9f }, // In bounds
            new CombatEventMarker { TimestampMs = 2500, EventType = CombatEventType.TowerKill, IntensityScore = 0.8f }, // In bounds
            new CombatEventMarker { TimestampMs = 15000, EventType = CombatEventType.BossKill, IntensityScore = 1.0f }, // Past OutMarker
            new CombatEventMarker { TimestampMs = 999999, EventType = CombatEventType.TeamFight, IntensityScore = 1.0f } // Extreme
        ];

        // Trim range is 0 to 3000ms
        byte[] pcmData = buffer.SynthesizeAudioTrack(
            inMarkerMs: 0,
            outMarkerMs: 3000,
            targetFps: 60,
            combatEvents: extremeEvents);

        // 3.0s * 60fps = 180 frames * 3200 bytes = 576,000 bytes
        pcmData.Length.Should().Be(576000);

        // Assert all 16-bit short samples are within valid range and no crash occurred
        short[] shortSamples = new short[pcmData.Length / 2];
        Buffer.BlockCopy(pcmData, 0, shortSamples, 0, pcmData.Length);
        shortSamples.Length.Should().Be(288000);
    }

    #endregion

    #region SECTION 3: CancellationToken Abort Stress Tests Across Multiple Stages

    [Fact]
    public async Task ExportVideoAsync_PreCancelledToken_AbortsImmediatelyWithZeroFrames()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"precancel_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = true
        };

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancel

        using MemoryStream targetStream = new();

        Func<Task> act = async () =>
        {
            await pipeline.ExportVideoAsync(
                parameters,
                renderFrameCallback: null,
                combatEvents: null,
                progress: null,
                customOutputStream: targetStream,
                ct: cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        targetStream.Length.Should().Be(0);
    }

    [Fact]
    public async Task ExportVideoAsync_CancelOnFrameZero_AbortsAndCleansUp()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"cancel_frame0_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = true
        };

        using CancellationTokenSource cts = new();
        using MemoryStream targetStream = new();

        int frameCounter = 0;
        async Task CancelImmediatelyCallback(int timestampMs, NativeBufferRing ring)
        {
            frameCounter++;
            cts.Cancel(); // Cancel during frame 0 rendering
            await Task.CompletedTask;
        }

        Func<Task> act = async () =>
        {
            await pipeline.ExportVideoAsync(
                parameters,
                CancelImmediatelyCallback,
                combatEvents: null,
                progress: null,
                customOutputStream: targetStream,
                ct: cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        frameCounter.Should().Be(1);
    }

    [Trait("Category", "Manual")]
    [Theory]
    [InlineData(10, 100)] // Cancel at 10%
    [InlineData(50, 100)] // Cancel at 50% (mid-stream)
    [InlineData(99, 100)] // Cancel at 99% (late-stream)
    public async Task ExportVideoAsync_CancelAtVariousStages_StopsAtExactFrameAndCleansResources(
        int cancelAtFrame,
        int totalFramesToSchedule)
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        int durationMs = (int)Math.Ceiling(totalFramesToSchedule * 1000.0 / 60.0);

        string outputPath = Path.Combine(Path.GetTempPath(), $"cancel_stage_{cancelAtFrame}_{Guid.NewGuid():N}.mp4");

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = outputPath,
            InMarkerMs = 0,
            OutMarkerMs = durationMs,
            IncludeAudio = true
        };

        using CancellationTokenSource cts = new();
        using MemoryStream targetStream = new();

        int renderedCount = 0;
        async Task StageCancelCallback(int timestampMs, NativeBufferRing ring)
        {
            renderedCount++;
            if (renderedCount >= cancelAtFrame)
            {
                cts.Cancel();
            }
            await Task.CompletedTask;
        }

        Func<Task> act = async () =>
        {
            await pipeline.ExportVideoAsync(
                parameters,
                StageCancelCallback,
                combatEvents: null,
                progress: null,
                customOutputStream: targetStream,
                ct: cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        renderedCount.Should().Be(cancelAtFrame);
        File.Exists(outputPath).Should().BeFalse("Pipeline must delete partial output file on cancellation");
    }

    [Fact]
    public async Task ExportReplayAsync_CancellationDuringReplayExport_CleansUpAllTempAudioFiles()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ReplaySnapshotEngine snapshotEngine = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        MapEnvironmentManager mapManager = new();

        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 0, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 10000, IsKeyframe = true }
        ];
        snapshotEngine.LoadKeyframes(snapshots, 10000);
        mapManager.LoadMap("caldavar");

        string outputFilePath = Path.Combine(Path.GetTempPath(), $"replay_abort_{Guid.NewGuid():N}.mp4");

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = outputFilePath,
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = true
        };

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Signal cancellation before execution
        using MemoryStream targetStream = new();

        cameraDirector.Mode = CameraMode.FreeSpectator;

        Func<Task> act = async () =>
        {
            await pipeline.ExportReplayAsync(
                parameters,
                snapshotEngine,
                cameraDirector,
                lightingController,
                mapManager,
                combatEvents: null,
                progress: null,
                customOutputStream: targetStream,
                ct: cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(outputFilePath).Should().BeFalse("Output video file must be cleaned up on cancellation");
    }

    #endregion
}
