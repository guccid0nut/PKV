// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for Wave 2 native C# media and texture oracles:
/// 1. WindowCapturer (UserIdleSeconds, AutoHDR detection, 0.33x preview generator).
/// 2. AudioStudioService (CoreAudio per-PID peak meter, state filtering, headless resilience).
/// 3. AssetBakeService (Premultiplied-alpha resampling, border cutout, edge halo measurement).
/// 4. VerifiedClipRecorderService (Luma entropy analysis, ROI pairwise diff, QPC-synced float WAV writer).
/// </summary>
public sealed class MediaAndTextureOraclesTests : IDisposable
{
    private readonly string _tempDir;

    public MediaAndTextureOraclesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kongor_wave2_oracles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
        catch
        {
            // Ignore transient cleanup errors in temp
        }
    }

    #region 1. WindowCapturer Oracles

    [Fact]
    public void WindowCapturer_GetUserIdleSeconds_ReturnsNonNegative()
    {
        double idle = WindowCapturer.GetUserIdleSeconds();
        idle.Should().BeGreaterOrEqualTo(0.0, "human idle time must never be negative");
    }

    [Theory]
    [InlineData("AutoHDREnable = 1", true, 1, "ENABLED_ODD_1")]
    [InlineData("AutoHDREnable=3", true, 3, "ENABLED_ODD_3")]
    [InlineData("AutoHDREnable = 2", false, 2, "DISABLED_EVEN_2")]
    [InlineData("AutoHDREnable=0", false, 0, "DISABLED_EVEN_0")]
    public void WindowCapturer_DetectAutoHdr_ParsesSimulatedRegistryProperly(
        string simulatedString,
        bool expectedActive,
        int expectedRaw,
        string expectedState)
    {
        var status = WindowCapturer.DetectAutoHdr("hon_x64", simulatedString);

        status.Should().NotBeNull();
        status.AutoHdrActive.Should().Be(expectedActive);
        status.RawValue.Should().Be(expectedRaw);
        status.State.Should().Be(expectedState);
        status.SourceKey.Should().Be("SIMULATED_REGISTRY");
    }

    [Fact]
    public void WindowCapturer_DetectAutoHdr_Empty_ReturnsNotSet()
    {
        var status = WindowCapturer.DetectAutoHdr("hon_x64", "EmptySettings=None");

        status.Should().NotBeNull();
        status.AutoHdrActive.Should().BeFalse();
        status.RawValue.Should().Be(0);
        status.State.Should().Be("NOT_SET");
        status.SourceKey.Should().Be("NONE");
    }

    [Fact]
    public void WindowCapturer_GeneratePreview_CreatesOneThirdScaleImage()
    {
        string originalPath = Path.Combine(_tempDir, "test_capture.png");
        using (var img = new Image<Rgba32>(300, 150))
        {
            img.Mutate(ctx => ctx.BackgroundColor(Color.Goldenrod));
            img.SaveAsPng(originalPath);
        }

        string? previewPath = WindowCapturer.GeneratePreview(originalPath);

        previewPath.Should().NotBeNull();
        File.Exists(previewPath).Should().BeTrue();
        previewPath.Should().EndWith("_preview.png");

        using var previewImg = Image.Load(previewPath!);
        previewImg.Width.Should().Be(100);
        previewImg.Height.Should().Be(50);
    }

    #endregion

    #region 2. AudioStudioService Oracles

    [Fact]
    public void AudioStudioService_SamplePidPeak_NonExistentPid_DoesNotThrow()
    {
        var result = AudioStudioService.SamplePidPeak(99999999);

        result.Should().NotBeNull();
        result.PidMaxPeak.Should().Be(0.0f);
        result.MatchingActiveSessions.Should().Be(0);
    }

    #endregion

    #region 3. AssetBakeService Alpha & Cutout Oracles

    [Fact]
    public void AssetBakeService_SyntheticGoldIcon_HasExpectedDimensionsAndCenter()
    {
        using var icon = AssetBakeService.CreateSyntheticGoldIcon(64, 64, solidDarkBorder: false);

        icon.Width.Should().Be(64);
        icon.Height.Should().Be(64);

        // Center pixel should be opaque gold
        var centerPixel = icon[32, 32];
        centerPixel.A.Should().Be(255);
        centerPixel.R.Should().BeGreaterThan((byte)150);
        centerPixel.G.Should().BeGreaterThan((byte)150);
    }

    [Fact]
    public void AssetBakeService_PremultipliedAlpha_PreservesEdgeLuminanceOverNaive()
    {
        using var source = AssetBakeService.CreateSyntheticGoldIcon(128, 128, solidDarkBorder: false);

        using var premult = AssetBakeService.ResizePremultiplied(source, 32, 32);
        using var naive = AssetBakeService.ResizeNaiveStraightAlpha(source, 32, 32);

        AssetBakeService.MeasureEdgeLumaStats(premult, out double preOpLuma, out double preEdLuma, out int preEdges);
        AssetBakeService.MeasureEdgeLumaStats(naive, out double naiveOpLuma, out double naiveEdLuma, out int naiveEdges);

        preEdges.Should().BeGreaterThan(0, "there should be semi-transparent edge pixels");
        // Premultiplied alpha eliminates the dark halo bleed, so mean edge luminance should be >= naive
        preEdLuma.Should().BeGreaterOrEqualTo(naiveEdLuma - 0.5);
    }

    [Fact]
    public void AssetBakeService_FloodFillBorderCutout_ConvertsBorderToTransparent()
    {
        // 32x32 image with solid white background and black square in center (10..22)
        using var img = new Image<Rgba32>(32, 32);
        img.Mutate(ctx => ctx.BackgroundColor(Color.White));
        for (int y = 10; y < 22; y++)
        {
            for (int x = 10; x < 22; x++)
            {
                img[x, y] = Color.Black;
            }
        }

        int cleared = AssetBakeService.FloodFillBorderCutout(img, tolerance: 10);

        cleared.Should().BeGreaterThan(0);
        img[0, 0].A.Should().Be(0, "corner pixel should be cut out to transparent");
        img[16, 16].A.Should().Be(255, "center solid shape should remain untouched");
    }

    #endregion

    #region 4. VerifiedClipRecorderService Oracles

    [Fact]
    public void VerifiedClipRecorderService_ComputeLumaStats_CalculatesAccurateLuma()
    {
        using var frame = new Image<Rgba32>(64, 64);
        frame.Mutate(ctx => ctx.BackgroundColor(Color.White));

        VerifiedClipRecorderService.ComputeLumaStats(frame, stride: 1, out double meanLuma, out double stdDev);

        meanLuma.Should().BeApproximately(255.0, 1.0);
        stdDev.Should().BeApproximately(0.0, 0.1);
    }

    [Fact]
    public void VerifiedClipRecorderService_WriteQpcSyncedFloatWav_ProducesValidWaveHeader()
    {
        string wavPath = Path.Combine(_tempDir, "test_qpc.wav");

        VerifiedClipRecorderService.WriteQpcSyncedFloatWav(
            wavPath: wavPath,
            durationSeconds: 0.1,
            silentMode: false,
            out int totalFramesWritten,
            out int qpcSilenceFramesPadded,
            out double peakMax,
            out double rmsDb);

        File.Exists(wavPath).Should().BeTrue();
        var bytes = File.ReadAllBytes(wavPath);
        bytes.Length.Should().BeGreaterThan(44);

        // Verify RIFF and WAVE magic
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WAVE");
    }

    #endregion
}
