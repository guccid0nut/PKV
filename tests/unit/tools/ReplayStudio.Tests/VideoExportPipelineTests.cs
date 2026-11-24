// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoExportPipelineTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ReplayStudio.GUI.Common;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Export;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public class VideoExportPipelineTests
{
    #region 1. Export Preset Tests

    [Fact]
    public void PredefinedPresets_WhenInvoked_ContainExpectedQualityProfiles()
    {
        // 720p60 Fast
        ExportPreset fast = ExportPreset.Fast720p60;
        fast.Width.Should().Be(1280);
        fast.Height.Should().Be(720);
        fast.TargetFps.Should().Be(60);
        fast.VideoBitrate.Should().Be("8M");
        fast.Codec.Should().Be(ExportVideoCodec.Libx264);

        // 1080p60 Ultra
        ExportPreset ultra = ExportPreset.Ultra1080p60;
        ultra.Width.Should().Be(1920);
        ultra.Height.Should().Be(1080);
        ultra.TargetFps.Should().Be(60);
        ultra.VideoBitrate.Should().Be("25M");
        ultra.Codec.Should().Be(ExportVideoCodec.H264Nvenc);

        // 4K60 RTX
        ExportPreset rtx = ExportPreset.Rtx4K60;
        rtx.Width.Should().Be(3840);
        rtx.Height.Should().Be(2160);
        rtx.TargetFps.Should().Be(60);
        rtx.VideoBitrate.Should().Be("60M");
        rtx.Codec.Should().Be(ExportVideoCodec.HevcNvenc);
    }

    [Theory]
    [InlineData("1080p60 Ultra (NVENC GPU)", 1920, 1080, ExportVideoCodec.H264Nvenc)]
    [InlineData("4K60 RTX (NVENC HEVC)", 3840, 2160, ExportVideoCodec.HevcNvenc)]
    [InlineData("720p60 Fast (CPU H.264)", 1280, 720, ExportVideoCodec.Libx264)]
    [InlineData("Unknown Preset", 1920, 1080, ExportVideoCodec.H264Nvenc)] // Fallback to 1080p
    public void FromDisplayName_GivenPresetName_ResolvesCorrectPreset(string displayName, int expectedW, int expectedH, ExportVideoCodec expectedCodec)
    {
        ExportPreset preset = ExportPreset.FromDisplayName(displayName);
        preset.Width.Should().Be(expectedW);
        preset.Height.Should().Be(expectedH);
        preset.Codec.Should().Be(expectedCodec);
    }

    [Fact]
    public void CreateCustom_WhenInvoked_GeneratesCustomPresetProfile()
    {
        ExportPreset custom = ExportPreset.CreateCustom(
            width: 2560,
            height: 1440,
            fps: 120,
            videoBitrate: "45M",
            codec: ExportVideoCodec.H264Nvenc,
            audioBitrateKbps: 320,
            constantQuality: 15);

        custom.Width.Should().Be(2560);
        custom.Height.Should().Be(1440);
        custom.TargetFps.Should().Be(120);
        custom.VideoBitrate.Should().Be("45M");
        custom.Codec.Should().Be(ExportVideoCodec.H264Nvenc);
        custom.ConstantQuality.Should().Be(15);
    }

    #endregion

    #region 2. Codec CLI Argument Generation Tests

    [Fact]
    public void BuildFfmpegArguments_H264Nvenc_ProducesValidCliString()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = @"C:\Replays\match_clip.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 10000,
            IncludeAudio = true
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-", @"C:\Temp\audio.wav");

        args.Should().Contain("-f rawvideo -vcodec rawvideo -pix_fmt bgra -s 1920x1080 -r 60 -i -");
        args.Should().Contain("-f wav -thread_queue_size 1024 -i \"C:\\Temp\\audio.wav\"");
        args.Should().Contain("-c:v h264_nvenc");
        args.Should().Contain("-preset p7");
        args.Should().Contain("-tune hq");
        args.Should().Contain("-rc vbr");
        args.Should().Contain("-cq 18");
        args.Should().Contain("-b:v 25M");
        args.Should().Contain("-maxrate 40M");
        args.Should().Contain("-bufsize 50M");
        args.Should().Contain("-g 120");
        args.Should().Contain("-bf 3");
        args.Should().Contain("-pix_fmt yuv420p");
        args.Should().Contain("-c:a aac -b:a 320k -ar 48000 -ac 2");
        args.Should().Contain("-shortest -movflags +faststart");
        args.Should().Contain("\"C:\\Replays\\match_clip.mp4\"");
    }

    [Fact]
    public void BuildFfmpegArguments_HevcNvenc_ProducesValid4kArgs()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Rtx4K60,
            OutputFilePath = @"C:\Replays\4k_clip.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = true
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-");

        args.Should().Contain("-s 3840x2160");
        args.Should().Contain("-c:v hevc_nvenc");
        args.Should().Contain("-preset p7");
        args.Should().Contain("-b:v 60M");
        args.Should().Contain("-maxrate 90M");
        args.Should().Contain("-bufsize 120M");
        args.Should().Contain("-pix_fmt yuv420p");
    }

    [Fact]
    public void BuildFfmpegArguments_H264Qsv_ProducesIntelQsvArgs()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OverrideCodec = ExportVideoCodec.H264Qsv,
            OutputFilePath = @"C:\Replays\qsv_clip.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 5000
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-");

        args.Should().Contain("-c:v h264_qsv");
        args.Should().Contain("-preset veryslow");
        args.Should().Contain("-global_quality 18");
        args.Should().Contain("-pix_fmt nv12");
    }

    [Fact]
    public void BuildFfmpegArguments_H264Amf_ProducesAmdAmfArgs()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OverrideCodec = ExportVideoCodec.H264Amf,
            OutputFilePath = @"C:\Replays\amf_clip.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 5000
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-");

        args.Should().Contain("-c:v h264_amf");
        args.Should().Contain("-quality quality");
        args.Should().Contain("-rc cbr");
        args.Should().Contain("-pix_fmt nv12");
    }

    [Fact]
    public void BuildFfmpegArguments_Libx264_ProducesCpuArgs()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = @"C:\Replays\cpu_clip.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = false
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters, "-");

        args.Should().Contain("-c:v libx264");
        args.Should().Contain("-preset slow");
        args.Should().Contain("-crf 20");
        args.Should().Contain("-an"); // Audio disabled
        args.Should().NotContain("-c:a aac");
    }

    #endregion

    #region 3. Audio Frame Byte Quota & Synchronization Tests

    [Fact]
    public void AudioByteQuota_48kHz60Fps_Exactly3200BytesPerFrame()
    {
        // Math: (48000 / 60) * 2 channels * (16 / 8) bytes = 800 * 2 * 2 = 3200 bytes
        int bytesPerFrame = AudioStreamSyncBuffer.CalculateBytesPerFrame(
            sampleRate: 48000,
            targetFps: 60,
            channels: 2,
            bitsPerSample: 16);

        bytesPerFrame.Should().Be(3200);

        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);
        buffer.BytesPerFrame.Should().Be(3200);
        buffer.SamplesPerFramePerChannel.Should().Be(800);
    }

    [Fact]
    public void AudioByteQuota_48kHz30Fps_Exactly6400BytesPerFrame()
    {
        int bytesPerFrame = AudioStreamSyncBuffer.CalculateBytesPerFrame(
            sampleRate: 48000,
            targetFps: 30,
            channels: 2,
            bitsPerSample: 16);

        bytesPerFrame.Should().Be(6400);
    }

    [Fact]
    public void AudioStreamSyncBuffer_EnqueueAndDequeue_SlicesBitPerfectFrames()
    {
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        byte[] rawPcm = new byte[6400]; // Exactly 2 frames worth of data
        for (int i = 0; i < rawPcm.Length; i++)
        {
            rawPcm[i] = (byte)(i % 255);
        }

        buffer.TryEnqueueAudio(rawPcm).Should().BeTrue();
        buffer.TotalBytesEnqueued.Should().Be(6400);

        // Frame 1
        byte[] frame1 = buffer.GetNextFrameAudioBlock();
        frame1.Length.Should().Be(3200);
        frame1[0].Should().Be(0);
        frame1[100].Should().Be(100);

        // Frame 2
        byte[] frame2 = buffer.GetNextFrameAudioBlock();
        frame2.Length.Should().Be(3200);
        frame2[0].Should().Be((byte)(3200 % 255));

        // Frame 3 (Underrun -> Silence block of 3200 zeros)
        byte[] frame3 = buffer.GetNextFrameAudioBlock();
        frame3.Length.Should().Be(3200);
        frame3.Should().AllBeEquivalentTo((byte)0);
        buffer.UnderrunFramesCount.Should().Be(1);
    }

    [Fact]
    public void SynthesizeAudioTrack_WhenInvoked_GeneratesCompleteTrackWithCombatMarkers()
    {
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        List<CombatEventMarker> combatEvents =
        [
            new CombatEventMarker { TimestampMs = 1000, EventType = CombatEventType.FirstBlood, IntensityScore = 0.9f },
            new CombatEventMarker { TimestampMs = 2500, EventType = CombatEventType.TowerKill, IntensityScore = 0.8f },
            new CombatEventMarker { TimestampMs = 4000, EventType = CombatEventType.TeamFight, IntensityScore = 1.0f }
        ];

        byte[] pcmData = buffer.SynthesizeAudioTrack(
            inMarkerMs: 0,
            outMarkerMs: 5000,
            targetFps: 60,
            combatEvents: combatEvents);

        // 5 seconds @ 60 FPS = 300 frames * 3,200 bytes = 960,000 bytes
        pcmData.Length.Should().Be(960000);

        // Create WAV stream with 44-byte RIFF header
        using MemoryStream wavStream = AudioStreamSyncBuffer.CreateWavStream(pcmData, 48000, 2, 16);
        wavStream.Length.Should().Be(960000 + 44);

        // Verify RIFF header
        byte[] header = new byte[44];
        wavStream.Read(header, 0, 44);
        Encoding.ASCII.GetString(header, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(header, 8, 4).Should().Be("WAVE");
        Encoding.ASCII.GetString(header, 12, 4).Should().Be("fmt ");
        Encoding.ASCII.GetString(header, 36, 4).Should().Be("data");
    }

    #endregion

    #region 4. Timeline Trim & Frame Calculation Tests

    [Theory]
    [InlineData(0, 10000, 60, 600)]
    [InlineData(15000, 45000, 60, 1800)]
    [InlineData(0, 1000, 60, 60)]
    [InlineData(5000, 5050, 60, 3)]
    public void TimelineTrim_WhenInvoked_CalculatesExactFrameCount(int inMs, int outMs, int fps, int expectedFrames)
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = @"C:\Replays\test.mp4",
            InMarkerMs = inMs,
            OutMarkerMs = outMs,
            OverrideFps = fps
        };

        parameters.TotalDurationMs.Should().Be(outMs - inMs);
        parameters.TotalFrames.Should().Be(expectedFrames);
    }

    [Fact]
    public void GetFrameTimestampMs_WhenInvoked_CalculatesLinearProgression()
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Ultra1080p60,
            OutputFilePath = @"C:\Replays\test.mp4",
            InMarkerMs = 10000,
            OutMarkerMs = 20000,
            OverrideFps = 60
        };

        parameters.GetFrameTimestampMs(0).Should().Be(10000);
        parameters.GetFrameTimestampMs(30).Should().Be(10500);
        parameters.GetFrameTimestampMs(60).Should().Be(11000);
        parameters.GetFrameTimestampMs(600).Should().Be(20000);
    }

    [Fact]
    public void Validate_InvalidParameters_ThrowsDescriptiveExceptions()
    {
        // Empty output path
        ExportParameters p1 = new() { OutputFilePath = "", InMarkerMs = 0, OutMarkerMs = 1000 };
        Action act1 = () => p1.Validate();
        act1.Should().Throw<ArgumentException>().WithMessage("*Output file path*");

        // Inverted markers
        ExportParameters p2 = new() { OutputFilePath = @"C:\test.mp4", InMarkerMs = 5000, OutMarkerMs = 2000 };
        Action act2 = () => p2.Validate();
        act2.Should().Throw<ArgumentException>().WithMessage("*OutMarkerMs*greater than InMarkerMs*");

        // Negative in marker
        ExportParameters p3 = new() { OutputFilePath = @"C:\test.mp4", InMarkerMs = -100, OutMarkerMs = 2000 };
        Action act3 = () => p3.Validate();
        act3.Should().Throw<ArgumentException>().WithMessage("*InMarkerMs*cannot be negative*");
    }

    #endregion

    #region 5. Real-Time Progress, FPS, and ETA Calculations

    [Fact]
    public async Task ExportVideoAsync_CustomStream_PipesExactBytesAndReportsProgress()
    {
        VideoExportPipeline pipeline = new();

        // 720p60 Fast, 10 frames (10 / 60 = 166.6ms)
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 167, // exactly 10 frames @ 60 FPS
            IncludeAudio = false
        };

        int frameBytes = parameters.CalculatedVideoFrameBytes; // 1280 * 720 * 4 = 3,686,400 bytes
        using MemoryStream destinationStream = new();

        List<ExportProgress> progressReports = [];
        Progress<ExportProgress> progress = new(p => progressReports.Add(p));

        ExportResult result = await pipeline.ExportVideoAsync(
            parameters,
            renderFrameCallback: null,
            combatEvents: null,
            progress: progress,
            customOutputStream: destinationStream,
            ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.TotalFramesRendered.Should().Be(10);

        // Verify total bytes written to stream = 10 frames * frameBytes
        destinationStream.Length.Should().Be((long)10 * frameBytes);
    }

    [Fact]
    public async Task ExportVideoAsync_Cancellation_AbortsAndCleansUp()
    {
        VideoExportPipeline pipeline = new();

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"test_cancel_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 5000, // 300 frames
            IncludeAudio = true
        };

        using CancellationTokenSource cts = new();
        using MemoryStream destinationStream = new();

        // Cancel after 2 frames
        int rendered = 0;
        async Task RenderCallback(int ts, NativeBufferRing ring)
        {
            rendered++;
            if (rendered >= 2)
            {
                cts.Cancel();
            }
            await Task.CompletedTask;
        }

        Func<Task> act = async () =>
        {
            await pipeline.ExportVideoAsync(
                parameters,
                RenderCallback,
                combatEvents: null,
                progress: null,
                customOutputStream: destinationStream,
                ct: cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region 6. Synthetic Replay End-to-End Export Execution

    [Fact]
    public async Task ExportReplayAsync_FullSceneIntegration_ExecutesWithoutUiFreeze()
    {
        VideoExportPipeline pipeline = new();
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();

        // Populate match keyframes
        List<GameStateSnapshot> snapshots =
        [
            new GameStateSnapshot { TimestampMs = 0, ScoreLegion = 0, ScoreHellbourne = 0, IsKeyframe = true },
            new GameStateSnapshot { TimestampMs = 5000, ScoreLegion = 3, ScoreHellbourne = 1, IsKeyframe = true }
        ];
        snapshotEngine.LoadKeyframes(snapshots, 5000);
        mapManager.LoadMap("caldavar");

        ExportParameters parameters = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"test_replay_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 0,
            OutMarkerMs = 500, // 30 frames
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
        result.TotalFramesRendered.Should().Be(30);
        result.AverageFps.Should().BeGreaterThan(0);
        destinationStream.Length.Should().Be(30 * parameters.CalculatedVideoFrameBytes);
    }

    #endregion
}
