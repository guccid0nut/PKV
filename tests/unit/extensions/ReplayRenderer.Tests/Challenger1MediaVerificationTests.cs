// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Challenger1MediaVerificationTests.cs
// System: NEXUS Replay Video Renderer Test Suite - Empirical Media & Stream Verifier
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

/// <summary>
///     Empirical media container, stream, and packet stress verification tests
///     for output_test_render.mp4 written by Challenger 1.
/// </summary>
public sealed class Challenger1MediaVerificationTests(ITestOutputHelper output)
{
    private static readonly string ExpectedRenderPath = FindRenderPath();
    private readonly ITestOutputHelper _output = output;

    private static string FindRenderPath()
    {
        string currentDir = Directory.GetCurrentDirectory();
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "output_test_render.mp4"),
            Path.Combine(currentDir, "output_test_render.mp4")
        ];
        foreach (string candidate in candidates)
            if (File.Exists(candidate))
                return candidate;
        return candidates[0];
    }

    [Fact]
    public void PhysicalOutputFile_Exists_And_Exceeds1MBThreshold()
    {
        if (!NexusTestEnvironment.HasGameAssets && !File.Exists(ExpectedRenderPath)) return;
        _output.WriteLine($"[Test] Verifying physical existence and size of '{ExpectedRenderPath}'...");

        Assert.True(File.Exists(ExpectedRenderPath), $"Output video file must exist at {ExpectedRenderPath}");

        FileInfo fileInfo = new(ExpectedRenderPath);
        long fileSize = fileInfo.Length;
        _output.WriteLine(
            $"[Test] Output file byte length: {fileSize:N0} bytes ({(double)fileSize / (1024 * 1024):F2} MB).");

        Assert.True(fileSize > 1_000_000,
            $"Output video size ({fileSize} bytes) must strictly exceed 1,000,000 bytes.");
    }

    [Fact]
    public void PhysicalOutputFile_Mp4BoxStructure_MoovAtomBeforeMdat()
    {
        if (!NexusTestEnvironment.HasGameAssets && !File.Exists(ExpectedRenderPath)) return;
        _output.WriteLine($"[Test] Parsing ISO Base Media (MP4) box structure for '{ExpectedRenderPath}'...");

        Assert.True(File.Exists(ExpectedRenderPath), $"Target file not found at {ExpectedRenderPath}");

        using FileStream stream = File.OpenRead(ExpectedRenderPath);
        using BinaryReader reader = new(stream);

        List<(string Type, long Offset, long Size)> boxes = [];
        long fileLength = stream.Length;
        long currentOffset = 0;

        while (currentOffset < fileLength)
        {
            if (fileLength - currentOffset < 8) break;

            stream.Position = currentOffset;
            byte[] sizeBytes = reader.ReadBytes(4);
            Array.Reverse(sizeBytes);
            uint size32 = BitConverter.ToUInt32(sizeBytes, 0);

            byte[] typeBytes = reader.ReadBytes(4);
            string boxType = Encoding.ASCII.GetString(typeBytes);

            long actualSize = size32;
            if (size32 == 1)
            {
                byte[] largeSizeBytes = reader.ReadBytes(8);
                Array.Reverse(largeSizeBytes);
                actualSize = (long)BitConverter.ToUInt64(largeSizeBytes, 0);
            }
            else if (size32 == 0)
            {
                actualSize = fileLength - currentOffset;
            }

            boxes.Add((boxType, currentOffset, actualSize));
            _output.WriteLine(
                $"[MP4 Box] Type='{boxType}', Offset={currentOffset} (0x{currentOffset:X8}), Size={actualSize} bytes");

            if (actualSize <= 0) break;

            currentOffset += actualSize;
        }

        (string Type, long Offset, long Size) moovBox = boxes.Find(b => b.Type == "moov");
        (string Type, long Offset, long Size) mdatBox = boxes.Find(b => b.Type == "mdat");

        Assert.True(moovBox.Type == "moov", "Output MP4 must contain a 'moov' atom box.");
        Assert.True(mdatBox.Type == "mdat", "Output MP4 must contain an 'mdat' atom box.");

        _output.WriteLine(
            $"[FastStart Verification] 'moov' offset = {moovBox.Offset}, 'mdat' offset = {mdatBox.Offset}");
        Assert.True(moovBox.Offset < mdatBox.Offset,
            $"Faststart requirement violated: 'moov' atom ({moovBox.Offset}) must appear before 'mdat' atom ({mdatBox.Offset}).");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void PhysicalOutputFile_FfprobeStreamAnalysis_MatchesRenderSpecifications()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (!NexusTestEnvironment.HasGameAssets && !File.Exists(ExpectedRenderPath)) return;
        _output.WriteLine($"[Test] Running ffprobe stream analysis on '{ExpectedRenderPath}'...");

        Assert.True(File.Exists(ExpectedRenderPath), $"Target file not found at {ExpectedRenderPath}");

        ProcessStartInfo startInfo = new()
        {
            FileName = "ffprobe",
            Arguments = $"-v error -show_format -show_streams -of json \"{ExpectedRenderPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrEmpty(stdErr), $"ffprobe reported errors: {stdErr}");

        using JsonDocument doc = JsonDocument.Parse(stdOut);
        JsonElement root = doc.RootElement;

        JsonElement streams = root.GetProperty("streams");
        Assert.Equal(2, streams.GetArrayLength());

        JsonElement videoStream = streams[0];
        Assert.Equal("video", videoStream.GetProperty("codec_type").GetString());
        Assert.Equal("h264", videoStream.GetProperty("codec_name").GetString());
        Assert.Equal(1920, videoStream.GetProperty("width").GetInt32());
        Assert.Equal(1080, videoStream.GetProperty("height").GetInt32());
        Assert.Equal("60/1", videoStream.GetProperty("r_frame_rate").GetString());

        string nbFramesStr = videoStream.GetProperty("nb_frames").GetString()!;
        int videoFrames = int.Parse(nbFramesStr);
        _output.WriteLine($"[Test] ffprobe verified Video Frames: {videoFrames} (Required: >100).");
        Assert.True(videoFrames > 100, $"Total video frames ({videoFrames}) must strictly exceed 100 frames.");
        Assert.True(videoFrames >= 600,
            $"Total video frames ({videoFrames}) expected to be 600 frames for 10s @ 60fps.");

        JsonElement audioStream = streams[1];
        Assert.Equal("audio", audioStream.GetProperty("codec_type").GetString());
        Assert.Equal("aac", audioStream.GetProperty("codec_name").GetString());
        Assert.Equal("48000", audioStream.GetProperty("sample_rate").GetString());
        Assert.Equal(2, audioStream.GetProperty("channels").GetInt32());

        JsonElement format = root.GetProperty("format");
        string durationStr = format.GetProperty("duration").GetString()!;
        double duration = double.Parse(durationStr, CultureInfo.InvariantCulture);
        _output.WriteLine($"[Test] Container Duration: {duration:F2} seconds.");
        Assert.True(duration >= 10.0, $"Container duration ({duration}s) must be at least 10.0 seconds.");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void PhysicalOutputFile_FfmpegFullDecode_ExitsWithZeroErrors()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (!NexusTestEnvironment.HasGameAssets && !File.Exists(ExpectedRenderPath)) return;
        _output.WriteLine($"[Test] Executing complete FFmpeg decoding integrity pass on '{ExpectedRenderPath}'...");

        Assert.True(File.Exists(ExpectedRenderPath), $"Target file not found at {ExpectedRenderPath}");

        ProcessStartInfo startInfo = new()
        {
            FileName = "ffmpeg",
            Arguments = $"-v error -i \"{ExpectedRenderPath}\" -f null -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        string stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);

        _output.WriteLine($"[FFmpeg Decode Pass] ExitCode={process.ExitCode}, Stderr='{stdErr}'");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stdErr), $"FFmpeg decoding reported corruption/errors: {stdErr}");
    }
}
