// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RenderJobConfiguration.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ReplayRenderer.Service.Models;

/// <summary>
///     Supported video codecs for the FFmpeg encoding pipeline.
/// </summary>
public enum VideoCodec
{
    /// <summary>
    ///     NVIDIA Hardware NVENC H.264 Encoder (High Performance & Universal Compatibility).
    /// </summary>
    H264Nvenc = 0,

    /// <summary>
    ///     NVIDIA Hardware NVENC HEVC/H.265 Encoder (High Efficiency for 4K).
    /// </summary>
    HevcNvenc = 1,

    /// <summary>
    ///     Software CPU Fallback Encoder (libx264) for non-NVIDIA environments.
    /// </summary>
    Libx264CpuFallback = 2
}

/// <summary>
///     Encapsulates all configuration parameters for executing a deterministic replay rendering job.
/// </summary>
public sealed class RenderJobConfiguration
{
    /// <summary>
    ///     Absolute path to the source .honreplay file.
    /// </summary>
    public required string ReplayFilePath { get; set; }

    /// <summary>
    ///     Absolute path to the target output video file (.mp4).
    /// </summary>
    public required string OutputFilePath { get; set; }

    /// <summary>
    ///     Root directory of the Heroes of Newerth game installation containing hon_x64.exe and k2_x64.dll.
    /// </summary>
    public required string GameDirectory { get; set; }

    /// <summary>
    ///     Directory path containing VkLayer_hon_replay_capture.json and VkLayer_hon_replay_capture.dll.
    /// </summary>
    public required string VulkanLayerPath { get; set; }

    /// <summary>
    ///     Target horizontal resolution in pixels (e.g. 1920 or 3840). Default: 1920.
    /// </summary>
    public uint RenderWidth { get; set; } = 1920;

    /// <summary>
    ///     Alias for RenderWidth.
    /// </summary>
    public uint ResolutionWidth
    {
        get => RenderWidth;
        set => RenderWidth = value;
    }

    /// <summary>
    ///     Target vertical resolution in pixels (e.g. 1080 or 2160). Default: 1080.
    /// </summary>
    public uint RenderHeight { get; set; } = 1080;

    /// <summary>
    ///     Alias for RenderHeight.
    /// </summary>
    public uint ResolutionHeight
    {
        get => RenderHeight;
        set => RenderHeight = value;
    }

    /// <summary>
    ///     Locked simulation and video frame rate. Default: 60.
    /// </summary>
    public int TargetFramerate { get; set; } = 60;

    /// <summary>
    ///     Alias for TargetFramerate.
    /// </summary>
    public int TargetFps
    {
        get => TargetFramerate;
        set => TargetFramerate = value;
    }

    /// <summary>
    ///     Target video bitrate in Kbps. Default: 8000.
    /// </summary>
    public int VideoBitrateKbps { get; set; } = 8000;

    /// <summary>
    ///     Point-of-view client index (-1 = Free Spectator / Overhead, 0..9 = Specific Player POV). Default: 0.
    /// </summary>
    public int PovClientIndex { get; set; } = 0;

    /// <summary>
    ///     Playback start time offset in seconds. Default: 0.
    /// </summary>
    public int StartSeconds { get; set; } = 0;

    /// <summary>
    ///     In-game simulation timescale multiplier (1.0 = normal real-time speed). Default: 1.0f.
    /// </summary>
    public float Timescale { get; set; } = 1.0f;

    /// <summary>
    ///     If true, suppresses in-game 2D HUD overlays and UI trees for clean cinematic capture. Default: false.
    /// </summary>
    public bool EnableCleanCinematicUi { get; set; } = false;

    /// <summary>
    ///     Target video encoder codec. Default: H264Nvenc.
    /// </summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264Nvenc;

    /// <summary>
    ///     Alias for Codec.
    /// </summary>
    public VideoCodec SelectedCodec
    {
        get => Codec;
        set => Codec = value;
    }

    /// <summary>
    ///     Name or path of the FFmpeg binary. Default: "ffmpeg".
    /// </summary>
    public string FfmpegExecutablePath { get; set; } = "ffmpeg";

    /// <summary>
    ///     Name or path of the FFprobe binary used for post-export MP4 container verification. Default: "ffprobe".
    /// </summary>
    public string FfprobeExecutablePath { get; set; } = "ffprobe";

    /// <summary>
    ///     When true (default), verifies the encoded MP4 output via <c>ffprobe</c> after FFmpeg exits.
    /// </summary>
    public bool VerifyOutputWithFfprobe { get; set; } = true;

    /// <summary>
    ///     Maximum execution timeout before forcefully terminating the replay process. Default: 30 minutes.
    /// </summary>
    public TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Watchdog stall detection threshold if no video frames are received. Default: 5.0 seconds.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Optional limit on the maximum gameplay video duration to render (e.g. 10 seconds).
    ///     If null, renders until the replay completes or the process exits.
    /// </summary>
    public TimeSpan? MaxDuration { get; set; }

    /// <summary>
    ///     When true, uses Win32 Shared Memory (MemoryMappedFile) instead of Named Pipes for frame transfer. Default: true.
    /// </summary>
    public bool UseSharedMemoryIpc { get; set; } = true;

    /// <summary>
    ///     Base name for IPC channels. If null, a unique GUID-based name is generated.
    /// </summary>
    public string? IpcPipeName { get; set; }

    /// <summary>
    ///     Optional DXVK adapter name filter (e.g. "NVIDIA", "AMD", "llvmpipe") scoped strictly to the child process.
    ///     When null or empty, DXVK auto-selects the primary Vulkan device without overriding adapter selection.
    /// </summary>
    public string? DxvkFilterDeviceName { get; set; }

    /// <summary>
    ///     Audio sampling rate in Hz. Default: 48,000 Hz.
    /// </summary>
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>
    ///     Number of audio channels (2 = Stereo). Default: 2.
    /// </summary>
    public int AudioChannels { get; set; } = 2;

    /// <summary>
    ///     Audio bit depth (16 = 16-bit signed integer PCM). Default: 16.
    /// </summary>
    public int AudioBitsPerSample { get; set; } = 16;

    /// <summary>
    ///     Average video target bitrate string for FFmpeg (e.g. "25M" for 1080p, "60M" for 4K). Default: "25M".
    /// </summary>
    public string VideoBitrate { get; set; } = "25M";

    /// <summary>
    ///     Peak video bitrate ceiling string for FFmpeg (e.g. "40M" for 1080p, "90M" for 4K). Default: "40M".
    /// </summary>
    public string MaxBitrate { get; set; } = "40M";

    /// <summary>
    ///     Video buffer verifier (VBV) buffer size string for FFmpeg (e.g. "50M" for 1080p, "120M" for 4K). Default: "50M".
    /// </summary>
    public string BufferSize { get; set; } = "50M";

    /// <summary>
    ///     Constant Perceptual Quality value for NVENC (-cq) or CRF for libx264. Default: 18.
    /// </summary>
    public int ConstantQuality { get; set; } = 18;

    /// <summary>
    ///     Audio AAC encoding bitrate in kbps. Default: 320 kbps.
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 320;

    /// <summary>
    ///     Whether to run in headless windowless mode without creating UI windows. Default: true.
    /// </summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    ///     When false (default), enforces zero-window-exposure during window capture (PrintWindow with
    ///     PW_RENDERFULLCONTENT only; never calls ShowWindow/HWND_TOPMOST/SetForegroundWindow).
    /// </summary>
    public bool AllowForegroundActivation { get; set; } = false;

    /// <summary>
    ///     Calculates the exact byte size of a single uncompressed video frame (Width * Height * 4).
    /// </summary>
    public uint GetCalculatedVideoFrameBytes()
    {
        return RenderWidth * RenderHeight * 4;
    }

    /// <summary>
    ///     Calculates the exact byte quota for audio samples per video frame:
    ///     (AudioSampleRate / TargetFramerate) * AudioChannels * (AudioBitsPerSample / 8).
    ///     For 48kHz Stereo 16-bit at 60 FPS: (48000 / 60) * 2 * 2 = 800 * 4 = 3,200 bytes per frame.
    /// </summary>
    public int GetCalculatedAudioBytesPerFrame()
    {
        int bytesPerSample = AudioBitsPerSample / 8;
        int samplesPerFramePerChannel = AudioSampleRate / TargetFramerate;
        return samplesPerFramePerChannel * AudioChannels * bytesPerSample;
    }

    /// <summary>
    ///     Validates the configuration and throws descriptive exceptions if any parameters are invalid.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ReplayFilePath))
            throw new ArgumentException("ReplayFilePath must not be null or empty.", nameof(ReplayFilePath));

        if (string.IsNullOrWhiteSpace(OutputFilePath))
            throw new ArgumentException("OutputFilePath must not be null or empty.", nameof(OutputFilePath));

        if (string.IsNullOrWhiteSpace(GameDirectory))
            throw new ArgumentException("GameDirectory must not be null or empty.", nameof(GameDirectory));

        if (RenderWidth == 0 || RenderHeight == 0)
            throw new ArgumentException(
                $"Invalid render dimensions: {RenderWidth}x{RenderHeight}. Both must be greater than 0.");

        if (TargetFramerate <= 0)
            throw new ArgumentException($"Invalid target framerate: {TargetFramerate}. Must be greater than 0.",
                nameof(TargetFramerate));

        if (StartSeconds < 0)
            throw new ArgumentException($"Invalid StartSeconds: {StartSeconds}. Must be non-negative.",
                nameof(StartSeconds));

        if (AudioSampleRate <= 0 || AudioSampleRate % TargetFramerate != 0)
            throw new ArgumentException(
                $"AudioSampleRate ({AudioSampleRate}) must be evenly divisible by TargetFramerate ({TargetFramerate}) for exact frame slicing.",
                nameof(AudioSampleRate));
    }
}
