// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FfmpegArgumentBuilderTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Models;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class FfmpegArgumentBuilderTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void BuildArguments_H264Nvenc1080p60_GeneratesCorrectDualPipeArguments()
    {
        _output.WriteLine("[Test] Building FFmpeg CLI arguments for 1080p60 NVENC H.264 dual pipe...");

        FfmpegEncodingOptions options = new()
        {
            VideoInputSource = @"\\.\pipe\hon_frame_pipe_test",
            AudioInputSource = @"\\.\pipe\hon_audio_pipe_test",
            OutputFilePath = "C:\\NexusVideos\\replay_1080p.mp4",
            Width = 1920,
            Height = 1080,
            Framerate = 60,
            Codec = VideoCodec.H264Nvenc,
            AudioSampleRate = 48000,
            AudioChannels = 2,
            AudioBitrateKbps = 320,
            ConstantQuality = 18,
            VideoBitrate = "25M",
            MaxBitrate = "40M",
            BufferSize = "50M"
        };

        string args = FfmpegArgumentBuilder.BuildArguments(options);
        _output.WriteLine($"[Test] Generated CLI string:\n{args}");

        // Overwrite and Video Pipe
        Assert.StartsWith("-y ", args);
        Assert.Contains("-f rawvideo", args);
        Assert.Contains("-vcodec rawvideo", args);
        Assert.Contains("-pix_fmt bgra", args);
        Assert.Contains("-s 1920x1080", args);
        Assert.Contains("-r 60", args);
        Assert.Contains("-thread_queue_size 1024", args);
        Assert.Contains("-i \"\\\\.\\pipe\\hon_frame_pipe_test\"", args);

        // Audio Pipe
        Assert.Contains("-f s16le", args);
        Assert.Contains("-ar 48000", args);
        Assert.Contains("-ac 2", args);
        Assert.Contains("-i \"\\\\.\\pipe\\hon_audio_pipe_test\"", args);

        // NVENC H.264 Encoding Parameters
        Assert.Contains("-c:v h264_nvenc", args);
        Assert.Contains("-preset p7", args);
        Assert.Contains("-tune hq", args);
        Assert.Contains("-rc vbr", args);
        Assert.Contains("-cq 18", args);
        Assert.Contains("-b:v 25M", args);
        Assert.Contains("-maxrate 40M", args);
        Assert.Contains("-bufsize 50M", args);
        Assert.Contains("-spatial-aq 1", args);
        Assert.Contains("-temporal-aq 1", args);
        Assert.Contains("-rc-lookahead 32", args);
        Assert.Contains("-g 120", args); // 60 FPS * 2 = 120 GOP
        Assert.Contains("-bf 3", args);
        Assert.Contains("-b_ref_mode middle", args);
        Assert.Contains("-pix_fmt yuv420p", args);

        // Audio and Container
        Assert.Contains("-c:a aac -b:a 320k", args);
        Assert.Contains("-movflags +faststart", args);
        Assert.EndsWith("\"" + Path.GetFullPath("C:\\NexusVideos\\replay_1080p.mp4") + "\"", args);
    }

    [Fact]
    public void BuildArguments_HevcNvenc4K60_GeneratesOptimizedHevcParameters()
    {
        _output.WriteLine("[Test] Building FFmpeg CLI arguments for 4K60 NVENC HEVC...");

        FfmpegEncodingOptions options = new()
        {
            VideoInputSource = @"\\.\pipe\hon_4k_pipe",
            AudioInputSource = @"\\.\pipe\hon_audio_4k",
            OutputFilePath = "C:\\NexusVideos\\replay_4k.mp4",
            Width = 3840,
            Height = 2160,
            Framerate = 60,
            Codec = VideoCodec.HevcNvenc,
            AudioSampleRate = 48000,
            AudioChannels = 2,
            AudioBitrateKbps = 320,
            ConstantQuality = 18,
            VideoBitrate = "60M",
            MaxBitrate = "90M",
            BufferSize = "120M"
        };

        string args = FfmpegArgumentBuilder.BuildArguments(options);
        _output.WriteLine($"[Test] Generated HEVC 4K CLI string:\n{args}");

        Assert.Contains("-s 3840x2160", args);
        Assert.Contains("-c:v hevc_nvenc", args);
        Assert.Contains("-cq 16", args); // Math.Max(10, 18 - 2) = 16 for HEVC
        Assert.Contains("-b:v 60M", args);
        Assert.Contains("-maxrate 90M", args);
        Assert.Contains("-bufsize 120M", args);
        Assert.Contains("-g 120", args);
    }

    [Fact]
    public void BuildArguments_Libx264CpuFallback_GeneratesSoftwareEncodingParameters()
    {
        _output.WriteLine("[Test] Building FFmpeg CLI arguments for libx264 CPU fallback...");

        FfmpegEncodingOptions options = new()
        {
            VideoInputSource = @"\\.\pipe\hon_cpu_pipe",
            AudioInputSource = @"\\.\pipe\hon_audio_cpu",
            OutputFilePath = "C:\\NexusVideos\\replay_cpu.mp4",
            Width = 1920,
            Height = 1080,
            Framerate = 30,
            Codec = VideoCodec.Libx264CpuFallback,
            AudioSampleRate = 48000,
            AudioChannels = 2,
            AudioBitrateKbps = 192,
            ConstantQuality = 20
        };

        string args = FfmpegArgumentBuilder.BuildArguments(options);
        _output.WriteLine($"[Test] Generated libx264 CLI string:\n{args}");

        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-preset slow", args);
        Assert.Contains("-crf 20", args);
        Assert.Contains("-g 60", args); // 30 FPS * 2 = 60 GOP
        Assert.Contains("-pix_fmt yuv420p", args);
        Assert.Contains("-c:a aac -b:a 192k", args);
    }

    [Fact]
    public void BuildArguments_StdinVideoInput_ConfiguresDashInputCorrectly()
    {
        _output.WriteLine("[Test] Building FFmpeg CLI arguments for stdin '-' video input...");

        FfmpegEncodingOptions options = new()
        {
            VideoInputSource = "-",
            AudioInputSource = @"\\.\pipe\hon_audio",
            OutputFilePath = "out.mp4",
            Width = 1280,
            Height = 720,
            Framerate = 60
        };

        string args = FfmpegArgumentBuilder.BuildArguments(options);
        _output.WriteLine($"[Test] Generated stdin CLI string:\n{args}");

        Assert.Contains("-f rawvideo -vcodec rawvideo -pix_fmt bgra -s 1280x720 -r 60 -i -", args);
        Assert.DoesNotContain("-thread_queue_size 1024 -i \"-\"", args);
    }

    [Fact]
    public void BuildArgumentsFromConfig_WhenInvoked_MatchesDirectOptionsOutput()
    {
        _output.WriteLine("[Test] Testing BuildArgumentsFromConfig equivalence...");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "match.honreplay",
            OutputFilePath = "rendered.mp4",
            GameDirectory = "C:\\HoN",
            VulkanLayerPath = "C:\\Layers",
            RenderWidth = 1920,
            RenderHeight = 1080,
            TargetFramerate = 60,
            Codec = VideoCodec.H264Nvenc,
            AudioSampleRate = 48000,
            AudioChannels = 2,
            AudioBitrateKbps = 320,
            ConstantQuality = 18,
            VideoBitrate = "30M",
            MaxBitrate = "45M",
            BufferSize = "60M"
        };

        string videoPipe = @"\\.\pipe\hon_vpipe";
        string audioPipe = @"\\.\pipe\hon_apipe";

        string generatedArgs = FfmpegArgumentBuilder.BuildArgumentsFromConfig(config, videoPipe, audioPipe);
        _output.WriteLine($"[Test] Generated args from config:\n{generatedArgs}");

        Assert.Contains("-s 1920x1080", generatedArgs);
        Assert.Contains("-b:v 30M", generatedArgs);
        Assert.Contains("-maxrate 45M", generatedArgs);
        Assert.Contains("-bufsize 60M", generatedArgs);
        Assert.Contains(videoPipe, generatedArgs);
        Assert.Contains(audioPipe, generatedArgs);
        Assert.Contains("-movflags +faststart", generatedArgs);
    }
}
