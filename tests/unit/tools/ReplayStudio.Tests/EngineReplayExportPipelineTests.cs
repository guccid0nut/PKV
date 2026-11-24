// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EngineReplayExportPipelineTests.cs
// System: Replay Studio Desktop GUI Tool Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Models;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Export;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ReplayStudio.Tests;

public sealed class EngineReplayExportPipelineTests : IDisposable
{
    private const string ChocolateyFfmpeg = @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
    private const string ChocolateyFfprobe = @"C:\ProgramData\chocolatey\bin\ffprobe.exe";

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly string _stubGameDir;
    private readonly string _stubReplayPath;

    public EngineReplayExportPipelineTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "ReplayStudioEngineExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _stubGameDir = Path.Combine(_tempDir, "client");
        Directory.CreateDirectory(_stubGameDir);
        File.WriteAllText(Path.Combine(_stubGameDir, "hon_x64.exe"), "OFFLINE_TEST_STUB_NEVER_EXECUTED");

        _stubReplayPath = Path.Combine(_tempDir, "sample_match.honreplay");
        CreateMinimalReplayArchive(_stubReplayPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[Teardown Warning] Could not clean up '{_tempDir}': {ex.Message}");
        }
    }

    private static string ResolveToolPath(string preferredChocoPath, string fallbackName)
    {
        return File.Exists(preferredChocoPath) ? preferredChocoPath : fallbackName;
    }

    private static void CreateMinimalReplayArchive(string path)
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <replayinfo version="4.10.1.0" gamemode="All Pick" map="caldavar" matchid="9001" matchlength="60000">
              <player name="Tester" accountid="1" team="1" teamindex="0" clientnum="0" heroname="Hero_Devourer" />
            </replayinfo>
            """;

        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using ZipArchive archive = new(fs, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("replayinfo");
        using Stream stream = entry.Open();
        byte[] payload = Encoding.UTF8.GetBytes(xml);
        stream.Write(payload, 0, payload.Length);
    }

    [Fact]
    public async Task EngineHeadless_WhenReplayPathMissing_FailsClosedWithoutProceduralFallback()
    {
        // protects: ReplayStudio never silently fabricates a procedural grid when EngineHeadless is requested without a .honreplay.
        // fails_when: ExportVideoAsync returns Success=true or writes a file when ReplayFilePath is missing.
        // seam: VideoExportPipeline.ExportVideoAsync -> EngineReplayExportAdapter.TryBuildRenderConfiguration.

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        string outputMp4 = Path.Combine(_tempDir, "missing_replay_out.mp4");

        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.EngineHeadless,
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = outputMp4,
            ReplayFilePath = null,
            GameDirectory = _stubGameDir,
            InMarkerMs = 0,
            OutMarkerMs = 2000
        };

        ExportResult result = await pipeline.ExportVideoAsync(parameters, renderFrameCallback: null);

        result.Success.Should().BeFalse();
        result.BackendMode.Should().Be(ReplayExportBackendMode.EngineHeadless);
        result.TotalFramesRendered.Should().Be(0);
        result.ErrorMessage.Should().Contain(".honreplay");
        result.ErrorMessage.Should().Contain("SoftwarePreview");
        File.Exists(outputMp4).Should().BeFalse();
    }

    [Fact]
    public async Task EngineHeadless_WhenReplayExtensionInvalid_FailsClosedWithActionableError()
    {
        string wrongExtFile = Path.Combine(_tempDir, "not_a_replay.txt");
        await File.WriteAllTextAsync(wrongExtFile, "NOT_A_REPLAY");
        string outputMp4 = Path.Combine(_tempDir, "wrong_ext_out.mp4");

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.EngineHeadless,
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = outputMp4,
            ReplayFilePath = wrongExtFile,
            GameDirectory = _stubGameDir,
            InMarkerMs = 0,
            OutMarkerMs = 1000
        };

        ExportResult result = await pipeline.ExportVideoAsync(parameters, renderFrameCallback: null);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(".honreplay");
        File.Exists(outputMp4).Should().BeFalse();
    }

    [Fact]
    public async Task EngineHeadless_WhenHonExecutableMissing_FailsClosedWithoutProceduralFallback()
    {
        // protects: EngineHeadless fails closed when hon_x64.exe is absent in GameDirectory.
        // fails_when: ExportReplayAsync falls back to RenderProceduralFrame or returns Success=true without hon_x64.exe.
        // seam: VideoExportPipeline.ExportReplayAsync -> EngineReplayExportAdapter.TryBuildRenderConfiguration.

        string emptyGameDir = Path.Combine(_tempDir, "empty_client");
        Directory.CreateDirectory(emptyGameDir);
        string outputMp4 = Path.Combine(_tempDir, "missing_hon_out.mp4");

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ReplaySnapshotEngine snapshotEngine = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        MapEnvironmentManager mapManager = new();

        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.EngineHeadless,
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = outputMp4,
            ReplayFilePath = _stubReplayPath,
            GameDirectory = emptyGameDir,
            InMarkerMs = 0,
            OutMarkerMs = 1000
        };

        ExportResult result = await pipeline.ExportReplayAsync(
            parameters,
            snapshotEngine,
            cameraDirector,
            lightingController,
            mapManager);

        result.Success.Should().BeFalse();
        result.BackendMode.Should().Be(ReplayExportBackendMode.EngineHeadless);
        result.ErrorMessage.Should().Contain("hon_x64.exe");
        result.ErrorMessage.Should().Contain("SoftwarePreview");
        File.Exists(outputMp4).Should().BeFalse();
    }

    [Fact]
    public void EngineReplayExportAdapter_TryBuildRenderConfiguration_EnforcesHeadlessZeroExposureAndExactMapping()
    {
        EngineReplayExportAdapter adapter = new(NullLoggerFactory.Instance);
        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.EngineHeadless,
            Preset = ExportPreset.Rtx4K60,
            OutputFilePath = Path.Combine(_tempDir, "mapped_4k.mp4"),
            ReplayFilePath = _stubReplayPath,
            GameDirectory = _stubGameDir,
            InMarkerMs = 15000,
            OutMarkerMs = 25000,
            PovClientIndex = 4,
            EnableCleanCinematicUi = true,
            FfmpegExecutablePath = ChocolateyFfmpeg,
            FfprobeExecutablePath = ChocolateyFfprobe
        };

        bool ok = adapter.TryBuildRenderConfiguration(parameters, out RenderJobConfiguration? config, out string? error);

        ok.Should().BeTrue(error);
        config.Should().NotBeNull();
        config!.Headless.Should().BeTrue();
        config.AllowForegroundActivation.Should().BeFalse();
        config.VerifyOutputWithFfprobe.Should().BeTrue();
        config.RenderWidth.Should().Be(3840u);
        config.RenderHeight.Should().Be(2160u);
        config.TargetFramerate.Should().Be(60);
        config.Codec.Should().Be(VideoCodec.HevcNvenc);
        config.StartSeconds.Should().Be(15);
        config.MaxDuration.Should().Be(TimeSpan.FromSeconds(10));
        config.PovClientIndex.Should().Be(4);
        config.EnableCleanCinematicUi.Should().BeTrue();
    }

    [Fact]
    public async Task EngineHeadless_WithRealFfmpegAndFfprobeEndToEnd_VerifiesEncodedMp4()
    {
        // protects: Real EngineHeadless -> FFmpeg -> FfprobeVideoVerifier end-to-end contract without launching hon_x64.exe.
        // fails_when: Adapter rejects a valid ffprobe-verified MP4 or fails to populate VerificationResult telemetry.
        // seam: VideoExportPipeline -> EngineReplayExportAdapter (offline delegate) -> ffmpeg.exe -> FfprobeVideoVerifier (ffprobe.exe).

        string ffmpegPath = ResolveToolPath(ChocolateyFfmpeg, "ffmpeg");
        string ffprobePath = ResolveToolPath(ChocolateyFfprobe, "ffprobe");
        string outputMp4 = Path.Combine(_tempDir, "engine_headless_e2e_verified.mp4");

        RenderJobConfiguration? capturedConfig = null;
        EngineReplayExportAdapter adapter = new(
            NullLoggerFactory.Instance,
            new FfprobeVideoVerifier(NullLogger<FfprobeVideoVerifier>.Instance),
            async (config, ct) =>
            {
                capturedConfig = config;
                ProcessStartInfo psi = new()
                {
                    FileName = ffmpegPath,
                    Arguments =
                        $"-y -f lavfi -i testsrc=duration=0.4:size={config.RenderWidth}x{config.RenderHeight}:rate={config.TargetFramerate} -f lavfi -i anullsrc=r=48000:cl=stereo -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest -movflags +faststart \"{config.OutputFilePath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using Process proc = Process.Start(psi)!;
                string stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                proc.ExitCode.Should().Be(0, $"real ffmpeg encode failed: {stderr}");
                return (true, null);
            });

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance, adapter);
        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.EngineHeadless,
            Preset = ExportPreset.CreateCustom(320, 180, 30, "1M", ExportVideoCodec.Libx264),
            OutputFilePath = outputMp4,
            ReplayFilePath = _stubReplayPath,
            GameDirectory = _stubGameDir,
            InMarkerMs = 0,
            OutMarkerMs = 400,
            FfmpegExecutablePath = ffmpegPath,
            FfprobeExecutablePath = ffprobePath
        };

        List<ExportProgress> progressReports = [];
        Progress<ExportProgress> progress = new(p => progressReports.Add(p));

        ExportResult result = await pipeline.ExportVideoAsync(parameters, renderFrameCallback: null, progress: progress);

        _output.WriteLine($"[Test] EngineHeadless E2E verification: {result.VerificationResult?.Summary}");

        capturedConfig.Should().NotBeNull();
        capturedConfig!.Headless.Should().BeTrue();
        capturedConfig.AllowForegroundActivation.Should().BeFalse();

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.BackendMode.Should().Be(ReplayExportBackendMode.EngineHeadless);
        result.ZeroWindowExposureEnforced.Should().BeTrue();
        result.VerificationResult.Should().NotBeNull();
        result.VerificationResult!.IsValid.Should().BeTrue();
        result.VerificationResult.CodecName.Should().Be("h264");
        result.VerificationResult.Width.Should().Be(320);
        result.VerificationResult.Height.Should().Be(180);
        result.VerificationResult.FrameCount.Should().BeGreaterOrEqualTo(10);
        result.VerificationResult.DurationSeconds.Should().BeGreaterThan(0.2);
        result.OutputSizeBytes.Should().BeGreaterThan(1024);
    }

    [Fact]
    public async Task EngineHeadless_WhenRendererWritesCorruptMp4_FailsClosedViaRealFfprobe()
    {
        string ffprobePath = ResolveToolPath(ChocolateyFfprobe, "ffprobe");
        string outputMp4 = Path.Combine(_tempDir, "engine_headless_corrupt.mp4");

        EngineReplayExportAdapter adapter = new(
            NullLoggerFactory.Instance,
            new FfprobeVideoVerifier(NullLogger<FfprobeVideoVerifier>.Instance),
            async (config, ct) =>
            {
                await File.WriteAllTextAsync(config.OutputFilePath, "CORRUPT_NOT_AN_MP4_STREAM", ct);
                return (true, null);
            });

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance, adapter);
        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.EngineHeadless,
            Preset = ExportPreset.CreateCustom(320, 180, 30, "1M", ExportVideoCodec.Libx264),
            OutputFilePath = outputMp4,
            ReplayFilePath = _stubReplayPath,
            GameDirectory = _stubGameDir,
            InMarkerMs = 0,
            OutMarkerMs = 400,
            FfprobeExecutablePath = ffprobePath
        };

        ExportResult result = await pipeline.ExportVideoAsync(parameters, renderFrameCallback: null);

        result.Success.Should().BeFalse();
        result.BackendMode.Should().Be(ReplayExportBackendMode.EngineHeadless);
        result.ErrorMessage.Should().Contain("ffprobe");
        result.VerificationResult.Should().NotBeNull();
        result.VerificationResult!.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task SoftwarePreview_WithRealFfmpegAndFfprobeEndToEnd_ProducesVerifiedMp4()
    {
        // protects: Explicit SoftwarePreview mode streams real BGRA frames into real ffmpeg.exe and passes real ffprobe.exe verification.
        // fails_when: VideoExportPipeline fails to pipe frames, encode MP4, or verify via FfprobeVideoVerifier.
        // seam: VideoExportPipeline.ExportVideoAsync -> ffmpeg.exe -> FfprobeVideoVerifier -> ffprobe.exe.

        string ffmpegPath = ResolveToolPath(ChocolateyFfmpeg, "ffmpeg");
        string ffprobePath = ResolveToolPath(ChocolateyFfprobe, "ffprobe");
        string outputMp4 = Path.Combine(_tempDir, "software_preview_real_e2e.mp4");

        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ExportParameters parameters = new()
        {
            BackendMode = ReplayExportBackendMode.SoftwarePreview,
            Preset = ExportPreset.CreateCustom(320, 180, 30, "1M", ExportVideoCodec.Libx264),
            OutputFilePath = outputMp4,
            InMarkerMs = 0,
            OutMarkerMs = 334, // 10 frames @ 30fps
            IncludeAudio = true,
            FfmpegExecutablePath = ffmpegPath,
            FfprobeExecutablePath = ffprobePath,
            VerifyOutputWithFfprobe = true
        };

        ExportResult result = await pipeline.ExportVideoAsync(parameters, renderFrameCallback: null);

        _output.WriteLine($"[Test] SoftwarePreview real E2E verification: {result.VerificationResult?.Summary}");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.BackendMode.Should().Be(ReplayExportBackendMode.SoftwarePreview);
        result.TotalFramesRendered.Should().Be(10);
        result.OutputSizeBytes.Should().BeGreaterThan(1024);
        result.VerificationResult.Should().NotBeNull();
        result.VerificationResult!.IsValid.Should().BeTrue();
        result.VerificationResult.CodecName.Should().Be("h264");
        result.VerificationResult.AudioCodecName.Should().Be("aac");
        result.VerificationResult.Width.Should().Be(320);
        result.VerificationResult.Height.Should().Be(180);
        result.VerificationResult.FrameCount.Should().Be(10);
        result.VerificationResult.DurationSeconds.Should().BeGreaterThan(0.2);
    }

    [Fact]
    public async Task ExportModalViewModel_DefaultsToSoftwarePreview_AndEngineHeadlessFailsClosedWithoutReplay()
    {
        ExportModalViewModel vm = new();
        string outputMp4 = Path.Combine(_tempDir, "modal_engine_failclosed.mp4");

        vm.SelectedBackendMode.Should().Be(ReplayExportBackendMode.SoftwarePreview);
        vm.BackendModeDescription.Should().Contain("FUTURE DEV / EXPERIMENTAL");
        vm.AvailableBackendModes.Should().Contain(ReplayExportBackendMode.EngineHeadless);
        vm.AvailableBackendModes.Should().Contain(ReplayExportBackendMode.SoftwarePreview);

        vm.SelectedBackendMode = ReplayExportBackendMode.EngineHeadless;
        vm.BackendModeDescription.Should().Contain("FUTURE DEV / EXPERIMENTAL (Not supported in 5.0.2)");
        vm.OutputFilePath = outputMp4;
        vm.ConfigureMarkers(0, 1000);

        await vm.StartExportAsync();

        vm.IsExporting.Should().BeFalse();
        vm.LastExportResult.Should().NotBeNull();
        vm.LastExportResult!.Success.Should().BeFalse();
        vm.LastExportResult.BackendMode.Should().Be(ReplayExportBackendMode.EngineHeadless);
        vm.ExportStatusText.Should().Contain("Export failed");
        vm.ExportStatusText.Should().Contain(".honreplay");
        File.Exists(outputMp4).Should().BeFalse();
    }

    [Fact]
    public async Task ReplayStudioService_LoadReplayAsync_RecordsCurrentReplayFilePathForExportModal()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        bool loaded = await service.LoadReplayAsync(_stubReplayPath);

        loaded.Should().BeTrue();
        service.CurrentReplayFilePath.Should().Be(Path.GetFullPath(_stubReplayPath));

        // Synthetic metadata load without file must clear CurrentReplayFilePath
        service.LoadMetadata(MainViewModel.CreateDemoMetadata());
        service.CurrentReplayFilePath.Should().BeNull();
    }
}
