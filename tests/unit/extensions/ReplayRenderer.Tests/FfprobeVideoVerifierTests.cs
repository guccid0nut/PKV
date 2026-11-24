// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FfprobeVideoVerifierTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Models;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class FfprobeVideoVerifierTests(ITestOutputHelper output) : IDisposable
{
    private const string ChocolateyFfmpeg = @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
    private const string ChocolateyFfprobe = @"C:\ProgramData\chocolatey\bin\ffprobe.exe";

    private readonly ITestOutputHelper _output = output;
    private readonly string _tempDir = CreateTempDirectory();

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "FfprobeVerifierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
            _output.WriteLine($"[Teardown Warning] Could not delete temp dir '{_tempDir}': {ex.Message}");
        }
    }

    private static string ResolveToolPath(string preferredChocoPath, string fallbackName)
    {
        return File.Exists(preferredChocoPath) ? preferredChocoPath : fallbackName;
    }

    [Fact]
    public void BuildArguments_MatchesRequiredFfprobeJsonSchemaEntries()
    {
        string samplePath = Path.Combine(_tempDir, "sample.mp4");
        string args = FfprobeVideoVerifier.BuildArguments(samplePath);

        args.Should().Contain("-v error");
        args.Should().Contain("-show_entries stream=codec_name,width,height,nb_frames,r_frame_rate:format=duration,size");
        args.Should().Contain("-of json");
        args.Should().Contain($"\"{Path.GetFullPath(samplePath)}\"");
    }

    [Theory]
    [InlineData("60/1", true, 60.0)]
    [InlineData("30000/1001", true, 29.97002997)]
    [InlineData("24", true, 24.0)]
    [InlineData("0/0", false, 0.0)]
    [InlineData("invalid", false, 0.0)]
    [InlineData("", false, 0.0)]
    public void TryParseFrameRate_HandlesRationalAndInvalidStrings(string raw, bool expectedOk, double expectedFps)
    {
        bool ok = FfprobeVideoVerifier.TryParseFrameRate(raw, out double fps);
        ok.Should().Be(expectedOk);
        if (expectedOk)
        {
            fps.Should().BeApproximately(expectedFps, 0.001);
        }
    }

    [Fact]
    public async Task VerifyAsync_RealFfmpegEncodedMp4_PassesRealFfprobeValidation()
    {
        // protects: Real end-to-end FFmpeg -> MP4 -> ffprobe verification fidelity.
        // fails_when: FfprobeVideoVerifier fails to parse real ffprobe JSON output or rejects a valid H.264 MP4.
        // seam: FfmpegPipelineController / ffmpeg.exe -> FfprobeVideoVerifier.VerifyAsync -> ffprobe.exe.

        string ffmpegPath = ResolveToolPath(ChocolateyFfmpeg, "ffmpeg");
        string ffprobePath = ResolveToolPath(ChocolateyFfprobe, "ffprobe");
        string outputMp4 = Path.Combine(_tempDir, "real_encoded_320x180.mp4");

        // Encode 12 synthetic BGRA frames @ 30fps (0.4s) using real ffmpeg.exe into H.264 + AAC faststart MP4
        ProcessStartInfo encodePsi = new()
        {
            FileName = ffmpegPath,
            Arguments =
                $"-y -f lavfi -i testsrc=duration=0.4:size=320x180:rate=30 -f lavfi -i anullsrc=r=48000:cl=stereo -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest -movflags +faststart \"{outputMp4}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using (Process encodeProc = Process.Start(encodePsi)!)
        {
            string encodeErr = await encodeProc.StandardError.ReadToEndAsync();
            await encodeProc.WaitForExitAsync();
            encodeProc.ExitCode.Should().Be(0, $"real ffmpeg encode failed: {encodeErr}");
        }

        FfprobeVideoVerifier verifier = new(NullLogger<FfprobeVideoVerifier>.Instance);
        FfprobeVerificationOptions options = new()
        {
            ExpectedCodecName = FfprobeVideoVerifier.GetExpectedCodecName(VideoCodec.Libx264CpuFallback),
            ExpectedWidth = 320,
            ExpectedHeight = 180,
            MinimumFrameCount = 10,
            MinimumDurationSeconds = 0.2,
            MinimumFileSizeBytes = 1024,
            FfprobeExecutablePath = ffprobePath,
            FfmpegExecutablePath = ffmpegPath
        };

        FfprobeVideoVerificationResult result = await verifier.VerifyAsync(outputMp4, options);

        _output.WriteLine($"[Test] ffprobe summary: {result.Summary}");
        _output.WriteLine($"[Test] ffprobe raw JSON:\n{result.RawJson}");

        result.IsValid.Should().BeTrue(result.FailureReason);
        result.ExitCode.Should().Be(0);
        result.CodecName.Should().Be("h264");
        result.AudioCodecName.Should().Be("aac");
        result.Width.Should().Be(320);
        result.Height.Should().Be(180);
        result.FrameCount.Should().BeGreaterOrEqualTo(10);
        result.FrameRate.Should().BeApproximately(30.0, 0.5);
        result.DurationSeconds.Should().BeGreaterThan(0.2);
        result.FileSizeBytes.Should().BeGreaterThan(1024);
        result.ValidationErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_DimensionOrCodecMismatch_FailsClosedWithActionableErrors()
    {
        string ffmpegPath = ResolveToolPath(ChocolateyFfmpeg, "ffmpeg");
        string ffprobePath = ResolveToolPath(ChocolateyFfprobe, "ffprobe");
        string outputMp4 = Path.Combine(_tempDir, "mismatch_320x180.mp4");

        ProcessStartInfo encodePsi = new()
        {
            FileName = ffmpegPath,
            Arguments =
                $"-y -f lavfi -i color=c=blue:s=320x180:d=0.2:r=30 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart \"{outputMp4}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (Process encodeProc = Process.Start(encodePsi)!)
        {
            await encodeProc.StandardError.ReadToEndAsync();
            await encodeProc.WaitForExitAsync();
            encodeProc.ExitCode.Should().Be(0);
        }

        FfprobeVideoVerifier verifier = new(NullLogger<FfprobeVideoVerifier>.Instance);
        FfprobeVerificationOptions mismatchedOptions = new()
        {
            ExpectedCodecName = "hevc",
            ExpectedWidth = 1920,
            ExpectedHeight = 1080,
            FfprobeExecutablePath = ffprobePath
        };

        FfprobeVideoVerificationResult result = await verifier.VerifyAsync(outputMp4, mismatchedOptions);

        result.IsValid.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Contains("codec mismatch", StringComparison.OrdinalIgnoreCase));
        result.ValidationErrors.Should().Contain(e => e.Contains("width mismatch", StringComparison.OrdinalIgnoreCase));
        result.ValidationErrors.Should().Contain(e => e.Contains("height mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_CorruptPlaceholderFile_FailsClosed()
    {
        string ffprobePath = ResolveToolPath(ChocolateyFfprobe, "ffprobe");
        string fakeTextMp4 = Path.Combine(_tempDir, "corrupt_placeholder.mp4");
        await File.WriteAllTextAsync(fakeTextMp4, "[FAKE_MP4_PLACEHOLDER_BYTES]");

        FfprobeVideoVerifier verifier = new(NullLogger<FfprobeVideoVerifier>.Instance);
        FfprobeVideoVerificationResult textResult = await verifier.VerifyAsync(
            fakeTextMp4,
            new FfprobeVerificationOptions { FfprobeExecutablePath = ffprobePath });

        _output.WriteLine($"[Test] Text placeholder probe (exit={textResult.ExitCode}): {textResult.Summary}");
        textResult.IsValid.Should().BeFalse("text placeholder bytes must never pass ffprobe video verification");
        textResult.ValidationErrors.Should().NotBeEmpty();
        textResult.FailureReason.Should().NotBeNullOrWhiteSpace();

        // Also verify truncated ISO BMFF ftyp header without moov/mdat fails closed
        string truncatedMp4 = Path.Combine(_tempDir, "truncated_ftyp.mp4");
        await File.WriteAllBytesAsync(truncatedMp4, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D]);

        FfprobeVideoVerificationResult truncatedResult = await verifier.VerifyAsync(
            truncatedMp4,
            new FfprobeVerificationOptions { FfprobeExecutablePath = ffprobePath });

        _output.WriteLine($"[Test] Truncated MP4 probe (exit={truncatedResult.ExitCode}): {truncatedResult.Summary}");
        truncatedResult.IsValid.Should().BeFalse("truncated ftyp header without valid video stream must fail closed");
        truncatedResult.ExitCode.Should().NotBe(0);
        truncatedResult.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task VerifyAsync_MissingFileOrZeroByteFile_FailsClosedBeforeSpawningFfprobe()
    {
        FfprobeVideoVerifier verifier = new(NullLogger<FfprobeVideoVerifier>.Instance);

        string missingPath = Path.Combine(_tempDir, "does_not_exist.mp4");
        FfprobeVideoVerificationResult missingResult = await verifier.VerifyAsync(missingPath);
        missingResult.IsValid.Should().BeFalse();
        missingResult.FailureReason.Should().Contain("does not exist");

        string emptyPath = Path.Combine(_tempDir, "empty.mp4");
        await File.WriteAllBytesAsync(emptyPath, []);
        FfprobeVideoVerificationResult emptyResult = await verifier.VerifyAsync(emptyPath);
        emptyResult.IsValid.Should().BeFalse();
        emptyResult.FailureReason.Should().Contain("empty (0 bytes)");
    }

    [Fact]
    public async Task VerifyAsync_MissingFfprobeBinary_FailsClosedWithActionableMessage()
    {
        string dummyPath = Path.Combine(_tempDir, "non_empty.mp4");
        await File.WriteAllBytesAsync(dummyPath, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);

        FfprobeVideoVerifier verifier = new(NullLogger<FfprobeVideoVerifier>.Instance);
        FfprobeVideoVerificationResult result = await verifier.VerifyAsync(
            dummyPath,
            new FfprobeVerificationOptions
            {
                FfprobeExecutablePath = $"nonexistent_ffprobe_{Guid.NewGuid():N}"
            });

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("ffprobe executable");
    }

    [Fact]
    public void ParseAndValidateProbeJson_ZeroFramesOrZeroDuration_FailsClosed()
    {
        string zeroPayload = """
        {
          "streams": [
            {
              "codec_name": "h264",
              "width": 1920,
              "height": 1080,
              "r_frame_rate": "60/1",
              "nb_frames": "0"
            }
          ],
          "format": {
            "duration": "0.000000",
            "size": "0"
          }
        }
        """;

        FfprobeVideoVerificationResult result = FfprobeVideoVerifier.ParseAndValidateProbeJson(
            zeroPayload,
            "test.mp4",
            "ffprobe",
            "-v error",
            0,
            TimeSpan.FromMilliseconds(12),
            string.Empty);

        result.IsValid.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Contains("frame count", StringComparison.OrdinalIgnoreCase));
        result.ValidationErrors.Should().Contain(e => e.Contains("duration", StringComparison.OrdinalIgnoreCase));
        result.ValidationErrors.Should().Contain(e => e.Contains("file size", StringComparison.OrdinalIgnoreCase));
    }
}
