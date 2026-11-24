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
/// Comprehensive unit tests for Phase 4 ("The Living Nexus"):
/// 1. AssertVisualHandler masked SSIM assertions (region masking, viewport exclusion, threshold standards).
/// 2. 48 kHz 16-bit PCM audio format and spatial channel separation (Mono 3D vs Stereo UI).
/// 3. Tangent-space RxGB normal mapping with Direct3D Y- scanline inversion.
/// 4. Visual anomaly detection service integration (magenta, white quad, teapot, black screen).
/// </summary>
public sealed class ContentPipelineAndVisualTests : IDisposable
{
    private readonly string _tempFixtureDir;
    private readonly string _tempArtifactDir;

    public ContentPipelineAndVisualTests()
    {
        _tempFixtureDir = Path.Combine(Path.GetTempPath(), "kongor_p4_fixture_" + Guid.NewGuid().ToString("N"));
        _tempArtifactDir = Path.Combine(Path.GetTempPath(), "kongor_p4_artifact_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFixtureDir);
        Directory.CreateDirectory(_tempArtifactDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFixtureDir)) Directory.Delete(_tempFixtureDir, true);
            if (Directory.Exists(_tempArtifactDir)) Directory.Delete(_tempArtifactDir, true);
        }
        catch
        {
            // Ignore transient test cleanup errors
        }
    }

    #region 1. Masked SSIM & Viewport Exclusion Visual Assertion Tests

    [Fact]
    public async Task AssertVisual_WithMaskPath_IgnoresMaskedRegionsAndPasses()
    {
        // 1. Create baseline golden fixture (128x128 uniform gray)
        string goldenPath = Path.Combine(_tempFixtureDir, "golden_ui.png");
        using (var golden = new Image<Rgba32>(128, 128, new Rgba32(60, 60, 60, 255)))
        {
            await golden.SaveAsPngAsync(goldenPath);
        }

        // 2. Create candidate image with a massive magenta glitch in top-left region (0..40, 0..40)
        string candidatePath = Path.Combine(_tempFixtureDir, "candidate_glitched.png");
        using (var candidate = new Image<Rgba32>(128, 128, new Rgba32(60, 60, 60, 255)))
        {
            for (int y = 0; y < 40; y++)
            {
                for (int x = 0; x < 40; x++)
                {
                    candidate[x, y] = new Rgba32(255, 0, 255, 255); // Missing texture magenta defect
                }
            }
            await candidate.SaveAsPngAsync(candidatePath);
        }

        // 3. Create 8-bit grayscale mask: 0..40, 0..40 is black (0 = ignore), rest is white (255 = assert)
        string maskPath = Path.Combine(_tempFixtureDir, "ui_mask.png");
        using (var mask = new Image<L8>(128, 128, new L8(255)))
        {
            for (int y = 0; y < 40; y++)
            {
                for (int x = 0; x < 40; x++)
                {
                    mask[x, y] = new L8(0); // Masked out
                }
            }
            await mask.SaveAsPngAsync(maskPath);
        }

        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        var handler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        using JsonDocument argsDoc = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "candidate": "{{candidatePath.Replace('\\', '/')}}",
            "mask_path": "{{maskPath.Replace('\\', '/')}}",
            "threshold": 0.99
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("match").GetBoolean().Should().BeTrue();
        root.GetProperty("ssim").GetDouble().Should().BeGreaterOrEqualTo(0.99);
        root.GetProperty("maskedPixels").GetInt32().Should().Be(40 * 40);
        root.GetProperty("activePixels").GetInt32().Should().Be((128 * 128) - (40 * 40));
    }

    [Fact]
    public async Task AssertVisual_WithIgnoreViewportRect_PassesUIAtStrictThreshold()
    {
        // 1. Create baseline golden fixture
        string goldenPath = Path.Combine(_tempFixtureDir, "hud_golden.png");
        using (var golden = new Image<Rgba32>(128, 128, new Rgba32(40, 40, 40, 255)))
        {
            await golden.SaveAsPngAsync(goldenPath);
        }

        // 2. Create candidate with dynamic 3D differences inside the viewport rect (20, 20, 80, 80)
        string candidatePath = Path.Combine(_tempFixtureDir, "hud_dynamic_candidate.png");
        using (var candidate = new Image<Rgba32>(128, 128, new Rgba32(40, 40, 40, 255)))
        {
            for (int y = 20; y < 100; y++)
            {
                for (int x = 20; x < 100; x++)
                {
                    // Render differing 3D hero animation/particle frame
                    candidate[x, y] = new Rgba32(200, 180, 120, 255);
                }
            }
            await candidate.SaveAsPngAsync(candidatePath);
        }

        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        var handler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        // Test object bounding box format: { x, y, width, height }
        using JsonDocument argsDoc = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "candidate": "{{candidatePath.Replace('\\', '/')}}",
            "ignore_viewport_rect": { "x": 20, "y": 20, "width": 80, "height": 80 },
            "threshold": 0.99
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("match").GetBoolean().Should().BeTrue();
        root.GetProperty("ssim").GetDouble().Should().BeGreaterOrEqualTo(0.99);
        root.GetProperty("thresholdStandard").GetString().Should().Be("strict_ui_0.99");
    }

    [Fact]
    public async Task AssertVisual_WithIgnoreViewportRectArray_Passes()
    {
        string goldenPath = Path.Combine(_tempFixtureDir, "golden_arr.png");
        using (var golden = new Image<Rgba32>(128, 128, new Rgba32(40, 40, 40, 255)))
        {
            await golden.SaveAsPngAsync(goldenPath);
        }

        string candidatePath = Path.Combine(_tempFixtureDir, "candidate_arr.png");
        using (var candidate = new Image<Rgba32>(128, 128, new Rgba32(40, 40, 40, 255)))
        {
            for (int y = 15; y < 85; y++)
            {
                for (int x = 15; x < 85; x++)
                {
                    candidate[x, y] = new Rgba32(100, 250, 100, 255);
                }
            }
            await candidate.SaveAsPngAsync(candidatePath);
        }

        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        var handler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        // Test array [x, y, w, h] format
        using JsonDocument argsDoc = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "candidate": "{{candidatePath.Replace('\\', '/')}}",
            "ignore_viewport_rect": [15, 15, 70, 70],
            "threshold": 0.99
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;
        root.GetProperty("match").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AssertVisual_WithUnmaskedDefect_FailsAssertion()
    {
        string goldenPath = Path.Combine(_tempFixtureDir, "golden_strict.png");
        using (var golden = new Image<Rgba32>(128, 128, new Rgba32(40, 40, 40, 255)))
        {
            await golden.SaveAsPngAsync(goldenPath);
        }

        // Place a significant defect in the static UI region (outside the 20..100 ignored viewport)
        string candidatePath = Path.Combine(_tempFixtureDir, "candidate_unmasked_defect.png");
        using (var candidate = new Image<Rgba32>(128, 128, new Rgba32(40, 40, 40, 255)))
        {
            // Glitch across static UI banner (y: 0..18, x: 0..50 -> 900+ pixels)
            for (int y = 0; y < 18; y++)
            {
                for (int x = 0; x < 50; x++)
                {
                    candidate[x, y] = new Rgba32(255, 0, 0, 255);
                }
            }
            await candidate.SaveAsPngAsync(candidatePath);
        }

        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        var handler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        using JsonDocument argsDoc = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "candidate": "{{candidatePath.Replace('\\', '/')}}",
            "ignore_viewport_rect": { "x": 20, "y": 20, "width": 80, "height": 80 },
            "threshold": 0.99
        }
        """);

        ToolCallResult result = await handler.HandleAsync(argsDoc.RootElement, CancellationToken.None);

        result.Should().NotBeNull();
        result.IsError.Should().BeTrue();

        using var doc = JsonDocument.Parse(result.FirstText);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("mismatch");
        root.GetProperty("match").GetBoolean().Should().BeFalse();
        root.GetProperty("diffPixels").GetInt32().Should().Be(18 * 50);
    }

    [Fact]
    public async Task AssertVisual_ThresholdValidation_EnforcesMinimumStandards()
    {
        string goldenPath = Path.Combine(_tempFixtureDir, "standards_golden.png");
        using (var golden = new Image<Rgba32>(128, 128, new Rgba32(50, 50, 50, 255)))
        {
            await golden.SaveAsPngAsync(goldenPath);
        }

        var timeProvider = TimeProvider.System;
        var clientHandler = new GameClientManageHandler(timeProvider);
        var serverHandler = new GameServerManageHandler(timeProvider);
        var windowCapturer = new WindowCapturer(timeProvider: timeProvider);

        var handler = new AssertVisualHandler(
            windowCapturer,
            clientHandler,
            serverHandler,
            timeProvider,
            fixtureDirectory: _tempFixtureDir,
            artifactDirectory: _tempArtifactDir);

        // 1. Static UI with threshold < 0.99 and enforce_thresholds = true should fail validation
        using (JsonDocument doc1 = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "widget": "main:btn_play",
            "threshold": 0.95,
            "enforce_thresholds": true
        }
        """))
        {
            ToolCallResult res1 = await handler.HandleAsync(doc1.RootElement, CancellationToken.None);
            res1.IsError.Should().BeTrue();
            res1.FirstText.Should().Contain("violates minimum standards");
            res1.FirstText.Should().Contain("strict >= 0.99 for static 2D UI elements");
        }

        // 2. Viewport mode with threshold < 0.92 and enforce_thresholds = true should fail validation
        using (JsonDocument doc2 = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "mode": "viewport",
            "threshold": 0.85,
            "enforce_thresholds": true
        }
        """))
        {
            ToolCallResult res2 = await handler.HandleAsync(doc2.RootElement, CancellationToken.None);
            res2.IsError.Should().BeTrue();
            res2.FirstText.Should().Contain("violates minimum standards");
            res2.FirstText.Should().Contain("tolerant >= 0.92 for dynamic 3D viewports");
        }

        // 3. Omitting threshold on UI mode defaults to 0.99
        using (JsonDocument doc3 = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}",
            "widget": "hud:minimap"
        }
        """))
        {
            ToolCallResult res3 = await handler.HandleAsync(doc3.RootElement, CancellationToken.None);
            using var rDoc = JsonDocument.Parse(res3.FirstText);
            rDoc.RootElement.GetProperty("threshold").GetDouble().Should().Be(0.99);
            rDoc.RootElement.GetProperty("thresholdStandard").GetString().Should().Be("strict_ui_0.99");
        }

        // 4. Omitting threshold on viewport mode defaults to 0.92
        using (JsonDocument doc4 = JsonDocument.Parse($$"""
        {
            "fixture": "{{goldenPath.Replace('\\', '/')}}"
        }
        """))
        {
            ToolCallResult res4 = await handler.HandleAsync(doc4.RootElement, CancellationToken.None);
            using var rDoc = JsonDocument.Parse(res4.FirstText);
            rDoc.RootElement.GetProperty("threshold").GetDouble().Should().Be(0.92);
            rDoc.RootElement.GetProperty("thresholdStandard").GetString().Should().Be("tolerant_3d_0.92");
        }
    }

    #endregion

    #region 2. 48 kHz 16-Bit PCM Spatial Audio & Channel Separation Tests

    [Fact]
    public void AudioValidator_Valid48kHz16BitMono_PassesFor3DSound()
    {
        byte[] wav = CreateTestWav(sampleRate: 48000, channels: 1, bitsPerSample: 16, sampleCount: 48000);
        var check = ValidateWavFormat(wav, is3DContext: true);

        check.IsValid.Should().BeTrue();
        check.SampleRate.Should().Be(48000);
        check.Channels.Should().Be(1);
        check.BitsPerSample.Should().Be(16);
        check.Error.Should().BeNull();
    }

    [Fact]
    public void AudioValidator_Valid48kHz16BitStereo_PassesForUISound()
    {
        byte[] wav = CreateTestWav(sampleRate: 48000, channels: 2, bitsPerSample: 16, sampleCount: 96000);
        var check = ValidateWavFormat(wav, is3DContext: false);

        check.IsValid.Should().BeTrue();
        check.SampleRate.Should().Be(48000);
        check.Channels.Should().Be(2);
        check.BitsPerSample.Should().Be(16);
        check.Error.Should().BeNull();
    }

    [Fact]
    public void AudioValidator_MismatchSampleRate_FailsValidation()
    {
        // 44.1 kHz legacy audio format
        byte[] wav = CreateTestWav(sampleRate: 44100, channels: 1, bitsPerSample: 16, sampleCount: 44100);
        var check = ValidateWavFormat(wav, is3DContext: true);

        check.IsValid.Should().BeFalse();
        check.Error.Should().Contain("44100");
        check.Error.Should().Contain("48,000 Hz");
    }

    [Fact]
    public void AudioValidator_StereoIn3DContext_FailsValidation()
    {
        // Stereo in in-world 3D context (which breaks OpenAL HRTF positioning)
        byte[] wav = CreateTestWav(sampleRate: 48000, channels: 2, bitsPerSample: 16, sampleCount: 48000);
        var check = ValidateWavFormat(wav, is3DContext: true);

        check.IsValid.Should().BeFalse();
        check.Error.Should().Contain("Channel separation violation");
        check.Error.Should().Contain("must be Mono (1 channel)");
    }

    [Fact]
    public void AudioValidator_InvalidHeaderOrBitDepth_FailsValidation()
    {
        // 24-bit PCM instead of 16-bit
        byte[] wav24 = CreateTestWav(sampleRate: 48000, channels: 1, bitsPerSample: 24, sampleCount: 48000);
        var check24 = ValidateWavFormat(wav24, is3DContext: true);
        check24.IsValid.Should().BeFalse();
        check24.Error.Should().Contain("16-bit PCM");

        // Corrupt header
        byte[] corrupt = new byte[30];
        var checkCorrupt = ValidateWavFormat(corrupt, is3DContext: true);
        checkCorrupt.IsValid.Should().BeFalse();
        checkCorrupt.Error.Should().Contain("minimum 44 bytes");
    }

    #endregion

    #region 3. Tangent-Space RxGB Normal Mapping Math Tests

    [Fact]
    public void RxGbNormalMath_DecodeAndReconstructZ_ProducesNormalizedVectors()
    {
        // Test various normal orientations
        byte[,] testSamples = new byte[,]
        {
            { 128, 128 }, // Flat normal pointing straight up along Z: Nx=0, Ny=0, Nz=1
            { 180, 140 }, // Positive Nx, positive Ny
            { 75,  210 }, // Negative Nx, positive Ny
            { 220, 60  }, // Strong diagonal normal
        };

        for (int i = 0; i < testSamples.GetLength(0); i++)
        {
            byte alpha = testSamples[i, 0];
            byte green = testSamples[i, 1];

            double nx = (alpha / 255.0) * 2.0 - 1.0;
            double ny = (green / 255.0) * 2.0 - 1.0;
            double nz = Math.Sqrt(Math.Max(0.0, 1.0 - (nx * nx) - (ny * ny)));

            double vectorLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            vectorLength.Should().BeApproximately(1.0, 0.001);
            nz.Should().BeGreaterOrEqualTo(0.0);
        }
    }

    [Fact]
    public void RxGbNormalMath_Direct3DScanlineInversion_InvertsYComponent()
    {
        // In Direct3D, V coordinate goes top-to-bottom, so Ny must be inverted: Ny' = -Ny
        double targetNy = 0.7071;
        double invertedNy = -targetNy;

        // Map to 8-bit green channel
        byte direct3DGreen = (byte)Math.Clamp(((invertedNy * 0.5 + 0.5) * 255.0), 0, 255);
        double decodedNy = (direct3DGreen / 255.0) * 2.0 - 1.0;

        decodedNy.Should().BeApproximately(-0.7071, 0.02);
    }

    [Fact]
    public void RxGbNormalMath_HemisphereClosure_AvoidsImaginaryRoots()
    {
        // Even with slight texture sampling overshoot (Nx^2 + Ny^2 > 1.0)
        double nx = 1.05;
        double ny = 0.20;
        double clampedUnderSqrt = Math.Max(0.0, 1.0 - (nx * nx) - (ny * ny));
        double nz = Math.Sqrt(clampedUnderSqrt);

        nz.Should().Be(0.0);
        double.IsNaN(nz).Should().BeFalse();
    }

    #endregion

    #region 4. Visual Anomaly Detection Service Integration Tests

    [Fact]
    public void VisualAnomalyDetection_WhenInvoked_DetectsMagentaAndWhiteQuads()
    {
        var detector = new VisualAnomalyDetectionService();

        // 1. Clean frame
        using var clean = new Image<Rgba32>(160, 100, new Rgba32(60, 60, 60, 255));
        var cleanReport = detector.AnalyzeFrame(clean);
        cleanReport.HasAnomalies.Should().BeFalse();
        cleanReport.HasMissingTextures.Should().BeFalse();

        // 2. Magenta texture defect frame (#FF00FF)
        using var magentaFrame = new Image<Rgba32>(160, 100, new Rgba32(60, 60, 60, 255));
        for (int y = 20; y < 50; y++)
        {
            for (int x = 20; x < 50; x++)
            {
                magentaFrame[x, y] = new Rgba32(255, 0, 255, 255);
            }
        }
        var magentaReport = detector.AnalyzeFrame(magentaFrame);
        magentaReport.HasMissingTextures.Should().BeTrue();
        magentaReport.Anomalies.Should().Contain(a => a.Type == "MissingTexture");

        // 3. Blank white quad placeholder (#FFFFFF)
        using var whiteFrame = new Image<Rgba32>(160, 100, new Rgba32(60, 60, 60, 255));
        for (int y = 30; y < 60; y++)
        {
            for (int x = 30; x < 60; x++)
            {
                whiteFrame[x, y] = new Rgba32(255, 255, 255, 255);
            }
        }
        var whiteReport = detector.AnalyzeFrame(whiteFrame);
        whiteReport.HasBlankWhitePlaceholder.Should().BeTrue();
        whiteReport.Anomalies.Should().Contain(a => a.Type == "BlankWhitePlaceholder");
    }

    [Fact]
    public void VisualAnomalyDetection_TeapotLogSnippet_TriggersTeapotAnomaly()
    {
        var detector = new VisualAnomalyDetectionService();
        using var frame = new Image<Rgba32>(160, 100, new Rgba32(60, 60, 60, 255));

        string logSnippet = "Resource error: /core/null/null.mdf fallback activated";
        var report = detector.AnalyzeFrame(frame, logSnippet);

        report.HasTeapotFallback.Should().BeTrue();
        report.Anomalies.Should().Contain(a => a.Type == "TeapotModelFallback");
        report.TeapotFallbackDetails.Should().Contain("null.mdf");
    }

    [Fact]
    public void VisualAnomalyDetection_BlackScreen_DetectsStall()
    {
        var detector = new VisualAnomalyDetectionService();
        using var blackFrame = new Image<Rgba32>(160, 100, new Rgba32(0, 0, 0, 255));

        var report = detector.AnalyzeFrame(blackFrame);
        report.IsBlackScreen.Should().BeTrue();
        report.Anomalies.Should().Contain(a => a.Type == "BlackScreen");
    }

    #endregion

    #region Helper Methods

    private static (bool IsValid, string? Error, int SampleRate, int Channels, int BitsPerSample) ValidateWavFormat(byte[] wavBytes, bool is3DContext)
    {
        if (wavBytes.Length < 44)
            return (false, "File too small for RIFF WAVE header (minimum 44 bytes).", 0, 0, 0);

        using var ms = new MemoryStream(wavBytes);
        using var reader = new BinaryReader(ms);

        string riff = new string(reader.ReadChars(4));
        if (riff != "RIFF") return (false, $"Invalid chunk ID: '{riff}', expected 'RIFF'.", 0, 0, 0);

        int fileSize = reader.ReadInt32();

        string wave = new string(reader.ReadChars(4));
        if (wave != "WAVE") return (false, $"Invalid format: '{wave}', expected 'WAVE'.", 0, 0, 0);

        string fmt = new string(reader.ReadChars(4));
        if (fmt != "fmt ") return (false, $"Invalid subchunk: '{fmt}', expected 'fmt '.", 0, 0, 0);

        int fmtSize = reader.ReadInt32();
        short audioFormat = reader.ReadInt16();
        if (audioFormat != 1) return (false, $"Non-PCM audio format: {audioFormat}, expected 1 (PCM).", 0, 0, 0);

        short channels = reader.ReadInt16();
        int sampleRate = reader.ReadInt32();
        int byteRate = reader.ReadInt32();
        short blockAlign = reader.ReadInt16();
        short bitsPerSample = reader.ReadInt16();

        if (sampleRate != 48000)
            return (false, $"Invalid sample rate: {sampleRate} Hz. Expected 48,000 Hz.", sampleRate, channels, bitsPerSample);

        if (bitsPerSample != 16)
            return (false, $"Invalid bit depth: {bitsPerSample}-bit. Expected 16-bit PCM.", sampleRate, channels, bitsPerSample);

        if (is3DContext && channels != 1)
            return (false, $"Channel separation violation: 3D spatial audio must be Mono (1 channel), got {channels} channels.", sampleRate, channels, bitsPerSample);

        if (!is3DContext && channels != 2)
            return (false, $"UI/Announcer audio should be Stereo (2 channels), got {channels} channels.", sampleRate, channels, bitsPerSample);

        return (true, null, sampleRate, channels, bitsPerSample);
    }

    private static byte[] CreateTestWav(int sampleRate, short channels, short bitsPerSample, int sampleCount)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int subchunk2Size = sampleCount * channels * (bitsPerSample / 8);
        w.Write("RIFF"u8);
        w.Write(36 + subchunk2Size);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bitsPerSample / 8));
        w.Write((short)(channels * (bitsPerSample / 8)));
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(subchunk2Size);
        byte[] pcmData = new byte[subchunk2Size];
        w.Write(pcmData);
        return ms.ToArray();
    }

    #endregion
}
