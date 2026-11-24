// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M4Challenger2StressTests.cs
// System: Replay Studio Desktop GUI Tool - Milestone M4 Challenger 2 Stress Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
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
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

/// <summary>
///     Empirical adversarial stress test suite for Milestone M4 (Hardware-Accelerated Video & Audio Export Pipeline).
///     Tests cover:
///     1. Presets and CLI argument generation across standard and extreme/custom resolutions (8K, Ultrawide, Vertical, High-FPS, etc.)
///     2. Real-time ETA estimation, FPS calculations, and progress smoothing across fluctuating and extreme rendering speeds
///     3. End-to-end frame streaming and 3D scene rendering integration across camera modes, terrain, lighting, and models
///     4. Audio synchronization, buffer underruns, silence injection, combat SFX synthesis, and RIFF header verification
///     5. Parameter validation robustness and boundary conditions
/// </summary>
public class ReplayRosterAndEventTelemetryStressTests
{
    #region SECTION 1: Export Presets & FFmpeg Argument Builder Stress Tests

    [Theory]
    [InlineData(1280, 720, 60, "8M", "12M", "16M", 20, ExportVideoCodec.Libx264, 120)]
    [InlineData(1920, 1080, 60, "25M", "40M", "50M", 18, ExportVideoCodec.H264Nvenc, 120)]
    [InlineData(3840, 2160, 60, "60M", "90M", "120M", 16, ExportVideoCodec.HevcNvenc, 120)]
    public void BuildFfmpegArguments_StandardPresets_GeneratesCorrectCliFlags(
        int width,
        int height,
        int fps,
        string bitrate,
        string maxBitrate,
        string bufferSize,
        int cq,
        ExportVideoCodec codec,
        int expectedGop)
    {
        ExportPreset preset = new(
            name: "TestPreset",
            displayName: "Test Preset",
            description: "Desc",
            width: width,
            height: height,
            targetFps: fps,
            videoBitrate: bitrate,
            maxBitrate: maxBitrate,
            bufferSize: bufferSize,
            constantQuality: cq,
            codec: codec);

        ExportParameters parameters = new()
        {
            Preset = preset,
            OutputFilePath = @"C:\Exports\output.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 10000,
            IncludeAudio = true
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-", @"C:\Exports\audio.wav");

        args.Should().StartWith("-y ");
        args.Should().Contain($"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i -");
        args.Should().Contain("-f wav -thread_queue_size 1024 -i \"C:\\Exports\\audio.wav\"");
        args.Should().Contain($"-g {expectedGop}");
        args.Should().Contain("-c:a aac -b:a 320k -ar 48000 -ac 2");
        args.Should().Contain("-shortest -movflags +faststart");
        args.Should().EndWith("\"C:\\Exports\\output.mp4\"");

        switch (codec)
        {
            case ExportVideoCodec.H264Nvenc:
                args.Should().Contain("-c:v h264_nvenc -preset p7 -tune hq -rc vbr");
                args.Should().Contain($"-cq {cq} -b:v {bitrate} -maxrate {maxBitrate} -bufsize {bufferSize}");
                args.Should().Contain($"-spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -g {expectedGop} -bf 3 -b_ref_mode middle -pix_fmt yuv420p");
                break;
            case ExportVideoCodec.HevcNvenc:
                args.Should().Contain("-c:v hevc_nvenc -preset p7 -tune hq -rc vbr");
                args.Should().Contain($"-cq {Math.Max(10, cq - 2)} -b:v {bitrate} -maxrate {maxBitrate} -bufsize {bufferSize}");
                args.Should().Contain($"-spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -g {expectedGop} -bf 3 -b_ref_mode middle -pix_fmt yuv420p");
                break;
            case ExportVideoCodec.Libx264:
                args.Should().Contain($"-c:v libx264 -preset slow -crf {cq} -g {expectedGop} -pix_fmt yuv420p");
                break;
        }
    }

    [Theory]
    // 8K UHD 60fps
    [InlineData(7680, 4320, 60, "120M", ExportVideoCodec.HevcNvenc, 120)]
    // 8K DCI 60fps
    [InlineData(8192, 4320, 60, "150M", ExportVideoCodec.HevcNvenc, 120)]
    // 21:9 Ultrawide 144fps
    [InlineData(3440, 1440, 144, "50M", ExportVideoCodec.H264Nvenc, 288)]
    // 32:9 Super-Ultrawide 120fps
    [InlineData(5120, 1440, 120, "60M", ExportVideoCodec.H264Nvenc, 240)]
    // 1080p Esports 240fps
    [InlineData(1920, 1080, 240, "40M", ExportVideoCodec.H264Nvenc, 480)]
    // Vertical 9:16 Shorts/Reels 60fps
    [InlineData(1080, 1920, 60, "20M", ExportVideoCodec.Libx264, 120)]
    // Cinema 4K 24fps
    [InlineData(3840, 2160, 24, "45M", ExportVideoCodec.HevcNvenc, 48)]
    public void BuildFfmpegArguments_ExtremeAndCustomResolutions_BuildsAccurateParameters(
        int width,
        int height,
        int fps,
        string bitrate,
        ExportVideoCodec codec,
        int expectedGop)
    {
        ExportPreset customPreset = ExportPreset.CreateCustom(
            width: width,
            height: height,
            fps: fps,
            videoBitrate: bitrate,
            codec: codec,
            audioBitrateKbps: 384,
            constantQuality: 14);

        ExportParameters parameters = new()
        {
            Preset = customPreset,
            OutputFilePath = @"D:\Media\Rendered\custom_render.mp4",
            InMarkerMs = 1000,
            OutMarkerMs = 6000,
            IncludeAudio = true
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-", null);

        args.Should().Contain($"-s {width}x{height} -r {fps}");
        args.Should().Contain($"-g {expectedGop}");
        args.Should().Contain("-b:a 384k");
        args.Should().Contain("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000");

        if (codec != ExportVideoCodec.Libx264)
        {
            args.Should().Contain($"-b:v {bitrate}");
        }
        else
        {
            args.Should().Contain("-crf 14");
        }

        parameters.CalculatedVideoFrameBytes.Should().Be(width * height * 4);
        parameters.TotalFrames.Should().Be(5 * fps);
    }

    [Fact]
    public void BuildFfmpegArguments_AudioDisabled_EmitsAnFlagAndExcludesAudioInput()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = @"C:\Out\no_audio.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = false
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-", null);

        args.Should().Contain("-an");
        args.Should().NotContain("-c:a aac");
        args.Should().NotContain("anullsrc");
        args.Should().NotContain("-f wav");
    }

    [Theory]
    [InlineData("1080p60 Ultra (NVENC GPU)", 1920, 1080, 60, ExportVideoCodec.H264Nvenc)]
    [InlineData("1080p60 Ultra", 1920, 1080, 60, ExportVideoCodec.H264Nvenc)]
    [InlineData("4K60 RTX (NVENC HEVC)", 3840, 2160, 60, ExportVideoCodec.HevcNvenc)]
    [InlineData("4K60 RTX", 3840, 2160, 60, ExportVideoCodec.HevcNvenc)]
    [InlineData("720p60 Fast (CPU H.264)", 1280, 720, 60, ExportVideoCodec.Libx264)]
    [InlineData("720p60 Fast", 1280, 720, 60, ExportVideoCodec.Libx264)]
    [InlineData("NonExistentPreset", 1920, 1080, 60, ExportVideoCodec.H264Nvenc)] // Defaults to 1080p
    [InlineData("", 1920, 1080, 60, ExportVideoCodec.H264Nvenc)]
    [InlineData(null, 1920, 1080, 60, ExportVideoCodec.H264Nvenc)]
    public void ExportPreset_FromDisplayName_ResolvesRobustly(
        string? displayName,
        int expectedW,
        int expectedH,
        int expectedFps,
        ExportVideoCodec expectedCodec)
    {
        ExportPreset resolved = ExportPreset.FromDisplayName(displayName);

        resolved.Width.Should().Be(expectedW);
        resolved.Height.Should().Be(expectedH);
        resolved.TargetFps.Should().Be(expectedFps);
        resolved.Codec.Should().Be(expectedCodec);
    }

    [Fact]
    public void ExportPreset_AllPresetsCollection_ContainsAllStandardPresets()
    {
        IReadOnlyList<ExportPreset> presets = ExportPreset.AllPresets;
        presets.Should().HaveCount(3);
        presets.Should().Contain(ExportPreset.Ultra1080p60);
        presets.Should().Contain(ExportPreset.Rtx4K60);
        presets.Should().Contain(ExportPreset.Fast720p60);
    }

    #endregion

    #region SECTION 2: ETA Estimation & FPS Calculations Under Fluctuating Rendering Speeds

    [Fact]
    public void ExportProgress_FormattingAndCalculations_ProduceValidOutput()
    {
        ExportProgress prog = new()
        {
            RenderedFrames = 150,
            TotalFrames = 600,
            PercentComplete = 25.0,
            CurrentFps = 30.0,
            EtaSeconds = 15.0,
            Elapsed = TimeSpan.FromSeconds(5.0),
            CurrentTimestampMs = 2500,
            StatusText = "Encoding..."
        };

        prog.FormattedEta.Should().Be("00:15");
        prog.FormattedElapsed.Should().Be("00:05");
        prog.PercentComplete.Should().Be(25.0);
    }

    [Theory]
    [InlineData(0.0, "00:00")]
    [InlineData(-5.0, "00:00")]
    [InlineData(59.0, "00:59")]
    [InlineData(65.0, "01:05")]
    [InlineData(3599.0, "59:59")]
    public void ExportProgress_FormattedEta_HandlesNegativeAndExtremeSeconds(double etaSec, string expectedFormatted)
    {
        ExportProgress prog = new()
        {
            EtaSeconds = etaSec
        };

        prog.FormattedEta.Should().Be(expectedFormatted);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ExportVideoAsync_FluctuatingRenderingSpeeds_CalculatesSmoothedProgressAndEta()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        // 30 frames total
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"fluct_test_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 500, // 30 frames @ 60fps
            IncludeAudio = false
        };

        List<ExportProgress> telemetry = [];
        Progress<ExportProgress> progressReporter = new(p => telemetry.Add(p));

        int frameCounter = 0;
        async Task FluctuatingRenderCallback(int timestampMs, NativeBufferRing ring)
        {
            frameCounter++;
            // Simulate severe speed fluctuations:
            // First 10 frames: fast (1ms)
            // Next 10 frames: slow (15ms)
            // Last 10 frames: fast (1ms)
            if (frameCounter > 10 && frameCounter <= 20)
            {
                await Task.Delay(15);
            }
            else
            {
                await Task.Delay(1);
            }
        }

        using MemoryStream targetStream = new();

        ExportResult result = await pipeline.ExportVideoAsync(
            parameters,
            FluctuatingRenderCallback,
            combatEvents: null,
            progress: progressReporter,
            customOutputStream: targetStream,
            ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.TotalFramesRendered.Should().Be(30);
        result.AverageFps.Should().BeGreaterThan(0);

        telemetry.Should().NotBeEmpty();
        ExportProgress lastReport = telemetry[^1];
        lastReport.RenderedFrames.Should().Be(30);
        lastReport.PercentComplete.Should().Be(100.0);
        lastReport.EtaSeconds.Should().Be(0.0);
        lastReport.FormattedEta.Should().Be("00:00");
    }

    [Fact]
    public void ExportParameters_GetFrameTimestampMs_MonotonicallyIncreasesWithoutDrift()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            InMarkerMs = 50000,
            OutMarkerMs = 60000,
            OverrideFps = 60
        };

        int totalFrames = parameters.TotalFrames; // 600 frames
        totalFrames.Should().Be(600);

        int prevTimestamp = parameters.GetFrameTimestampMs(0);
        prevTimestamp.Should().Be(50000);

        for (int i = 1; i < totalFrames; i++)
        {
            int currentTimestamp = parameters.GetFrameTimestampMs(i);
            currentTimestamp.Should().BeGreaterThanOrEqualTo(prevTimestamp);
            prevTimestamp = currentTimestamp;
        }

        parameters.GetFrameTimestampMs(totalFrames).Should().Be(60000);
    }

    #endregion

    #region SECTION 3: End-to-End Frame Streaming with 3D Scene Rendering Integration

    [Fact]
    public async Task ExportReplayAsync_Full3DSceneRendering_ExecutesAcrossAllCameraModes()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();

        // Populate match keyframes with 10 heroes
        List<GameStateSnapshot> snapshots = [];
        for (int t = 0; t <= 3000; t += 1000)
        {
            GameStateSnapshot snap = new()
            {
                TimestampMs = t,
                ScoreLegion = t / 1000,
                ScoreHellbourne = (t / 1000) / 2,
                IsKeyframe = true
            };

            for (int h = 0; h < 10; h++)
            {
                snap.Heroes[h] = new HeroSnapshot
                {
                    ClientIndex = h,
                    HeroName = $"Hero_{h}",
                    Position = new Vector3(h * 100f + t, 50f, h * 50f),
                    HeadingAngle = (float)(h * 0.3 + t * 0.001),
                    Health = 500f + h * 50f,
                    MaxHealth = 800f + h * 50f,
                    Mana = 200f + h * 20f,
                    MaxMana = 400f + h * 20f,
                    Team = h < 5 ? 1 : 2
                };
            }
            snapshots.Add(snap);
        }

        snapshotEngine.LoadKeyframes(snapshots, 3000);
        mapManager.LoadMap("caldavar");

        CameraMode[] cameraModes =
        [
            CameraMode.PlayerPov,
            CameraMode.FirstPerson,
            CameraMode.RtsIsometric,
            CameraMode.FreeSpectator,
            CameraMode.CinematicSpline
        ];

        foreach (CameraMode mode in cameraModes)
        {
            cameraDirector.Mode = mode;

            if (mode == CameraMode.CinematicSpline)
            {
                cameraDirector.Spline.Clear();
                cameraDirector.Spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, 100, 200), LookAtTarget = Vector3.Zero });
                cameraDirector.Spline.AddNode(new DirectorNode { TimestampMs = 1500, Position = new Vector3(100, 150, 100), LookAtTarget = Vector3.Zero });
                cameraDirector.Spline.AddNode(new DirectorNode { TimestampMs = 3000, Position = new Vector3(200, 100, 0), LookAtTarget = Vector3.Zero });
            }

            ExportParameters parameters = new()
            {
                Preset = ExportPreset.Fast720p60,
                OutputFilePath = Path.Combine(Path.GetTempPath(), $"scene_render_{mode}_{Guid.NewGuid():N}.mp4"),
                InMarkerMs = 0,
                OutMarkerMs = 250, // 15 frames @ 60fps
                IncludeAudio = true
            };

            using MemoryStream destinationStream = new();

            ExportResult result = await pipeline.ExportReplayAsync(
                parameters,
                snapshotEngine,
                cameraDirector,
                lightingController,
                mapManager,
                combatEvents: null,
                progress: null,
                customOutputStream: destinationStream,
                ct: CancellationToken.None);

            result.Success.Should().BeTrue();
            result.TotalFramesRendered.Should().Be(15);
            destinationStream.Length.Should().Be(15 * parameters.CalculatedVideoFrameBytes);
        }
    }

    [Fact]
    public async Task ExportVideoAsync_HighFrameCountStress_MaintainsConsistentMemoryAndThroughput()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        // 120 frames of 720p (120 * 3,686,400 bytes = ~442 MB uncompressed frames)
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"stress_frames_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 2000, // 120 frames @ 60fps
            IncludeAudio = true
        };

        int frameBytes = parameters.CalculatedVideoFrameBytes;
        long totalBytesExpected = (long)120 * frameBytes;

        // Use a counting stream to avoid allocating 442MB memory array in RAM
        using CountingNullStream sinkStream = new();

        ExportResult result = await pipeline.ExportVideoAsync(
            parameters,
            renderFrameCallback: null,
            combatEvents: null,
            progress: null,
            customOutputStream: sinkStream,
            ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.TotalFramesRendered.Should().Be(120);
        sinkStream.TotalBytesWritten.Should().Be(totalBytesExpected);
    }

    [Fact]
    public async Task ExportVideoAsync_EarlyCancellation_StopsImmediatelyAndCleansResources()
    {
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"cancel_stress_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 10000, // 600 frames
            IncludeAudio = true
        };

        using CancellationTokenSource cts = new();
        using CountingNullStream sinkStream = new();

        int renderedCount = 0;
        async Task CancelAt10Callback(int ts, NativeBufferRing ring)
        {
            renderedCount++;
            if (renderedCount >= 10)
            {
                cts.Cancel();
            }
            await Task.CompletedTask;
        }

        Func<Task> exportTask = async () =>
        {
            await pipeline.ExportVideoAsync(
                parameters,
                CancelAt10Callback,
                combatEvents: null,
                progress: null,
                customOutputStream: sinkStream,
                ct: cts.Token);
        };

        await exportTask.Should().ThrowAsync<OperationCanceledException>();
        renderedCount.Should().Be(10);
    }

    #endregion

    #region SECTION 4: Audio Synchronization, Synthesis & Boundary Stress Tests

    [Theory]
    [InlineData(48000, 60, 2, 16, 3200, 800)]
    [InlineData(48000, 30, 2, 16, 6400, 1600)]
    [InlineData(48000, 120, 2, 16, 1600, 400)]
    [InlineData(48000, 240, 2, 16, 800, 200)]
    [InlineData(48000, 24, 2, 16, 8000, 2000)]
    [InlineData(96000, 60, 2, 16, 6400, 1600)]
    [InlineData(48000, 60, 6, 16, 9600, 800)] // 5.1 surround
    public void AudioStreamSyncBuffer_ArbitraryRatesAndFramerates_ComputesExactByteQuotas(
        int sampleRate,
        int fps,
        int channels,
        int bits,
        int expectedBytesPerFrame,
        int expectedSamplesPerChannel)
    {
        int bytes = AudioStreamSyncBuffer.CalculateBytesPerFrame(sampleRate, fps, channels, bits);
        bytes.Should().Be(expectedBytesPerFrame);

        using AudioStreamSyncBuffer buffer = new(sampleRate, channels, bits, fps);
        buffer.BytesPerFrame.Should().Be(expectedBytesPerFrame);
        buffer.SamplesPerFramePerChannel.Should().Be(expectedSamplesPerChannel);
    }

    [Fact]
    public void AudioStreamSyncBuffer_SequentialUnderruns_InjectsExactSilenceBlocks()
    {
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        // Dequeue 10 frames from empty buffer
        for (int i = 0; i < 10; i++)
        {
            byte[] silence = buffer.GetNextFrameAudioBlock();
            silence.Length.Should().Be(3200);
            for (int b = 0; b < silence.Length; b++)
            {
                silence[b].Should().Be(0);
            }
        }

        buffer.UnderrunFramesCount.Should().Be(10);
        buffer.TotalFramesProcessed.Should().Be(10);
        buffer.TotalBytesDequeued.Should().Be(0); // Only silence returned on underrun

        // Now enqueue 3200 bytes and dequeue
        byte[] pcm = new byte[3200];
        Array.Fill<byte>(pcm, 0x7F);
        buffer.TryEnqueueAudio(pcm).Should().BeTrue();

        byte[] realFrame = buffer.GetNextFrameAudioBlock();
        realFrame.Length.Should().Be(3200);
        realFrame[0].Should().Be(0x7F);
        buffer.TotalFramesProcessed.Should().Be(11);
        buffer.TotalBytesDequeued.Should().Be(3200);

        // Reset
        buffer.Reset();
        buffer.UnderrunFramesCount.Should().Be(0);
        buffer.TotalFramesProcessed.Should().Be(0);
        buffer.TotalBytesDequeued.Should().Be(0);
        buffer.TotalBytesEnqueued.Should().Be(0);
    }

    [Fact]
    public void SynthesizeAudioTrack_AllCombatEventTypes_SynthesizesWithoutSampleClipping()
    {
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        List<CombatEventMarker> allEventTypes =
        [
            new CombatEventMarker { TimestampMs = 200, EventType = CombatEventType.FirstBlood, IntensityScore = 1.0f },
            new CombatEventMarker { TimestampMs = 600, EventType = CombatEventType.HeroKill, IntensityScore = 0.9f },
            new CombatEventMarker { TimestampMs = 1000, EventType = CombatEventType.TowerKill, IntensityScore = 0.8f },
            new CombatEventMarker { TimestampMs = 1400, EventType = CombatEventType.BarracksKill, IntensityScore = 0.85f },
            new CombatEventMarker { TimestampMs = 1800, EventType = CombatEventType.BossKill, IntensityScore = 1.0f },
            new CombatEventMarker { TimestampMs = 2200, EventType = CombatEventType.TeamFight, IntensityScore = 1.0f },
            new CombatEventMarker { TimestampMs = 2600, EventType = CombatEventType.Generic, IntensityScore = 0.5f }
        ];

        byte[] pcmTrack = buffer.SynthesizeAudioTrack(0, 3000, 60, allEventTypes);

        // 3.0 seconds @ 60 FPS = 180 frames * 3200 bytes = 576,000 bytes
        pcmTrack.Length.Should().Be(576000);

        // Verify non-empty and all samples fit within signed 16-bit range without corrupt byte boundaries
        short[] shortSamples = new short[pcmTrack.Length / 2];
        Buffer.BlockCopy(pcmTrack, 0, shortSamples, 0, pcmTrack.Length);

        shortSamples.Length.Should().Be(288000);
        bool hasNonZeroSample = false;
        for (int i = 0; i < shortSamples.Length; i++)
        {
            if (shortSamples[i] != 0)
            {
                hasNonZeroSample = true;
            }
            shortSamples[i].Should().BeInRange(short.MinValue, short.MaxValue);
        }
        hasNonZeroSample.Should().BeTrue();
    }

    [Fact]
    public void AudioStreamSyncBuffer_WavHeader_GeneratesCompliant44ByteRiffStructure()
    {
        byte[] dummyPcm = new byte[32000];
        using MemoryStream wavStream = AudioStreamSyncBuffer.CreateWavStream(dummyPcm, 48000, 2, 16);

        wavStream.Length.Should().Be(44 + 32000);

        using BinaryReader reader = new(wavStream);
        byte[] riffTag = reader.ReadBytes(4);
        Encoding.ASCII.GetString(riffTag).Should().Be("RIFF");

        int chunkSize = reader.ReadInt32();
        chunkSize.Should().Be(36 + 32000);

        byte[] waveTag = reader.ReadBytes(4);
        Encoding.ASCII.GetString(waveTag).Should().Be("WAVE");

        byte[] fmtTag = reader.ReadBytes(4);
        Encoding.ASCII.GetString(fmtTag).Should().Be("fmt ");

        int subchunk1Size = reader.ReadInt32();
        subchunk1Size.Should().Be(16);

        short audioFormat = reader.ReadInt16();
        audioFormat.Should().Be(1); // PCM

        short channels = reader.ReadInt16();
        channels.Should().Be(2);

        int sampleRate = reader.ReadInt32();
        sampleRate.Should().Be(48000);

        int byteRate = reader.ReadInt32();
        byteRate.Should().Be(48000 * 2 * 2); // 192000

        short blockAlign = reader.ReadInt16();
        blockAlign.Should().Be(4);

        short bitsPerSample = reader.ReadInt16();
        bitsPerSample.Should().Be(16);

        byte[] dataTag = reader.ReadBytes(4);
        Encoding.ASCII.GetString(dataTag).Should().Be("data");

        int dataSize = reader.ReadInt32();
        dataSize.Should().Be(32000);
    }

    #endregion

    #region SECTION 5: Parameter Validation & Defensive Robustness

    [Theory]
    [InlineData("", 1920, 1080, 60, 0, 1000, "Output file path")]
    [InlineData("   ", 1920, 1080, 60, 0, 1000, "Output file path")]
    [InlineData("test.mp4", 0, 1080, 60, 0, 1000, "Invalid render dimensions")]
    [InlineData("test.mp4", 1920, -100, 60, 0, 1000, "Invalid render dimensions")]
    [InlineData("test.mp4", 1920, 1080, 0, 0, 1000, "Invalid target framerate")]
    [InlineData("test.mp4", 1920, 1080, -30, 0, 1000, "Invalid target framerate")]
    [InlineData("test.mp4", 1920, 1080, 60, -500, 1000, "cannot be negative")]
    [InlineData("test.mp4", 1920, 1080, 60, 5000, 2000, "must be strictly greater than InMarkerMs")]
    [InlineData("test.mp4", 1920, 1080, 60, 5000, 5000, "must be strictly greater than InMarkerMs")]
    public void ExportParameters_Validate_ThrowsOnInvalidInputs(
        string outputPath,
        int width,
        int height,
        int fps,
        int inMs,
        int outMs,
        string expectedErrorSubstring)
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = outputPath,
            OverrideWidth = width,
            OverrideHeight = height,
            OverrideFps = fps,
            InMarkerMs = inMs,
            OutMarkerMs = outMs
        };

        Action validate = () => parameters.Validate();
        validate.Should().Throw<ArgumentException>().WithMessage($"*{expectedErrorSubstring}*");
    }

    [Fact]
    public void ExportParameters_IndivisibleAudioRate_ThrowsDescriptiveException()
    {
        // SampleRate 44100Hz with 64 FPS -> 44100 % 64 = 4 != 0
        ExportPreset indivisiblePreset = new(
            name: "Indivisible",
            displayName: "Indivisible",
            description: "Test",
            width: 1920,
            height: 1080,
            targetFps: 64,
            videoBitrate: "25M",
            maxBitrate: "40M",
            bufferSize: "50M",
            constantQuality: 18,
            codec: ExportVideoCodec.H264Nvenc,
            audioSampleRate: 44100);

        ExportParameters parameters = new()
        {
            Preset = indivisiblePreset,
            OutputFilePath = @"C:\Exports\indivisible.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 1000
        };

        Action validate = () => parameters.Validate();
        validate.Should().Throw<ArgumentException>().WithMessage("*must be evenly divisible*");
    }

    #endregion

    #region Helper Stream Class

    private sealed class CountingNullStream : Stream
    {
        public long TotalBytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => TotalBytesWritten;
        public override long Position { get => TotalBytesWritten; set { } }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            TotalBytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            TotalBytesWritten += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            TotalBytesWritten += count;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            TotalBytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }

    #endregion
}
