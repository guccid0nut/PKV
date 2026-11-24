// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FfmpegArgumentBuilder.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ReplayRenderer.Service.Models;

namespace ReplayRenderer.Service.Encoding;

/// <summary>
///     Options required for building FFmpeg CLI arguments.
/// </summary>
public sealed class FfmpegEncodingOptions
{
    public required string VideoInputSource { get; set; } // e.g. @"\\.\pipe\hon_frame_pipe" or "-" (stdin)
    public required string AudioInputSource { get; set; } // e.g. @"\\.\pipe\hon_audio_pipe"
    public required string OutputFilePath { get; set; }
    public uint Width { get; set; } = 1920;
    public uint Height { get; set; } = 1080;
    public int Framerate { get; set; } = 60;
    public VideoCodec Codec { get; set; } = VideoCodec.H264Nvenc;
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;
    public int AudioBitrateKbps { get; set; } = 320;
    public int ConstantQuality { get; set; } = 18;
    public string VideoBitrate { get; set; } = "25M";
    public string MaxBitrate { get; set; } = "40M";
    public string BufferSize { get; set; } = "50M";
}

/// <summary>
///     Generates production-grade CLI arguments for hardware-accelerated NVENC and libx264 FFmpeg encoding.
/// </summary>
public static class FfmpegArgumentBuilder
{
    /// <summary>
    ///     Builds the full FFmpeg command line arguments string from configuration options.
    /// </summary>
    public static string BuildArguments(FfmpegEncodingOptions options)
    {
        string videoInput = options.VideoInputSource.Equals("-", StringComparison.Ordinal)
            ? $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {options.Width}x{options.Height} -r {options.Framerate} -i -"
            : $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {options.Width}x{options.Height} -r {options.Framerate} -thread_queue_size 1024 -i \"{options.VideoInputSource}\"";

        string audioInput = !string.IsNullOrWhiteSpace(options.AudioInputSource) &&
                            !options.AudioInputSource.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? $"-f s16le -ar {options.AudioSampleRate} -ac {options.AudioChannels} -probesize 32 -analyzeduration 0 -thread_queue_size 1024 -i \"{options.AudioInputSource}\""
            : $"-f lavfi -i anullsrc=channel_layout=stereo:sample_rate={options.AudioSampleRate}";

        int gopSize = options.Framerate * 2; // Exactly 2.0-second GOP length

        string videoEncodingArgs = options.Codec switch
        {
            VideoCodec.H264Nvenc =>
                $"-c:v h264_nvenc -preset p7 -tune hq -rc vbr -cq {options.ConstantQuality} -b:v {options.VideoBitrate} -maxrate {options.MaxBitrate} -bufsize {options.BufferSize} -spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -g {gopSize} -bf 3 -b_ref_mode middle -pix_fmt yuv420p",

            VideoCodec.HevcNvenc =>
                $"-c:v hevc_nvenc -preset p7 -tune hq -rc vbr -cq {Math.Max(10, options.ConstantQuality - 2)} -b:v {options.VideoBitrate} -maxrate {options.MaxBitrate} -bufsize {options.BufferSize} -spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -g {gopSize} -bf 3 -b_ref_mode middle -pix_fmt yuv420p",

            _ =>
                $"-c:v libx264 -preset slow -crf {options.ConstantQuality} -g {gopSize} -pix_fmt yuv420p"
        };

        string audioEncodingArgs = $"-c:a aac -b:a {options.AudioBitrateKbps}k";
        string containerArgs = "-shortest -movflags +faststart";
        string fullOutputPath = Path.GetFullPath(options.OutputFilePath);

        return
            $"-y {videoInput} {audioInput} {videoEncodingArgs} {audioEncodingArgs} {containerArgs} \"{fullOutputPath}\"";
    }

    /// <summary>
    ///     Helper method that builds arguments directly from a RenderJobConfiguration object and pipe endpoints.
    /// </summary>
    public static string BuildArgumentsFromConfig(RenderJobConfiguration config, string videoSource, string audioSource)
    {
        FfmpegEncodingOptions options = new()
        {
            VideoInputSource = videoSource,
            AudioInputSource = audioSource,
            OutputFilePath = config.OutputFilePath,
            Width = config.RenderWidth,
            Height = config.RenderHeight,
            Framerate = config.TargetFramerate,
            Codec = config.Codec,
            AudioSampleRate = config.AudioSampleRate,
            AudioChannels = config.AudioChannels,
            AudioBitrateKbps = config.AudioBitrateKbps,
            ConstantQuality = config.ConstantQuality,
            VideoBitrate = config.VideoBitrate,
            MaxBitrate = config.MaxBitrate,
            BufferSize = config.BufferSize
        };

        return BuildArguments(options);
    }
}
