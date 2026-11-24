// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ExportPreset.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using ReplayRenderer.Service.Encoding;

namespace ReplayStudio.GUI.Export;

/// <summary>
///     Selects which video rendering backend produces frames for export.
/// </summary>
public enum ReplayExportBackendMode
{
    /// <summary>
    ///     Genuine offscreen HoN engine rendering via headless <c>hon_x64.exe</c>,
    ///     <c>ReplayRendererService</c>, FFmpeg encoding, and <c>ffprobe</c> container verification.
    ///     Fails closed if <c>hon_x64.exe</c> or the source <c>.honreplay</c> archive is missing.
    /// </summary>
    EngineHeadless = 0,

    /// <summary>
    ///     Explicit software procedural preview renderer (experimental/diagnostics only).
    /// </summary>
    SoftwarePreview = 1
}

/// <summary>
///     Hardware-accelerated and software video codecs supported by the export pipeline.
/// </summary>
public enum ExportVideoCodec
{
    /// <summary>
    ///     NVIDIA NVENC H.264 Hardware Encoder (Universal GPU acceleration).
    /// </summary>
    H264Nvenc = 0,

    /// <summary>
    ///     NVIDIA NVENC HEVC / H.265 Hardware Encoder (High-efficiency 4K GPU acceleration).
    /// </summary>
    HevcNvenc = 1,

    /// <summary>
    ///     Intel QuickSync Video H.264 Hardware Encoder.
    /// </summary>
    H264Qsv = 2,

    /// <summary>
    ///     AMD Advanced Media Framework H.264 Hardware Encoder.
    /// </summary>
    H264Amf = 3,

    /// <summary>
    ///     Software CPU H.264 Encoder (libx264 high-quality universal fallback).
    /// </summary>
    Libx264 = 4
}

/// <summary>
///     Represents an encoding quality preset specifying video resolution, target framerate,
///     bitrate ceilings, codec preferences, and audio fidelity parameters.
/// </summary>
public sealed class ExportPreset
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int TargetFps { get; set; }
    public string VideoBitrate { get; set; }
    public string MaxBitrate { get; set; }
    public string BufferSize { get; set; }
    public int ConstantQuality { get; set; }
    public int AudioBitrateKbps { get; set; }
    public int AudioSampleRate { get; set; }
    public int AudioChannels { get; set; }
    public ExportVideoCodec Codec { get; set; }
    public ExportVideoCodec FallbackCodec { get; set; }

    public ExportPreset(
        string name,
        string displayName,
        string description,
        int width,
        int height,
        int targetFps,
        string videoBitrate,
        string maxBitrate,
        string bufferSize,
        int constantQuality,
        ExportVideoCodec codec,
        ExportVideoCodec fallbackCodec = ExportVideoCodec.Libx264,
        int audioBitrateKbps = 320,
        int audioSampleRate = 48000,
        int audioChannels = 2)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Description = description ?? string.Empty;
        Width = width;
        Height = height;
        TargetFps = targetFps;
        VideoBitrate = videoBitrate ?? "25M";
        MaxBitrate = maxBitrate ?? "40M";
        BufferSize = bufferSize ?? "50M";
        ConstantQuality = constantQuality;
        Codec = codec;
        FallbackCodec = fallbackCodec;
        AudioBitrateKbps = audioBitrateKbps;
        AudioSampleRate = audioSampleRate;
        AudioChannels = audioChannels;
    }

    /// <summary>
    ///     Predefined Fast 720p60 preset (1280x720, 60fps, 8 Mbps, CPU libx264).
    /// </summary>
    public static ExportPreset Fast720p60 { get; } = new(
        name: "720p60 Fast",
        displayName: "720p60 Fast (CPU H.264)",
        description: "1280x720 @ 60 FPS, 8 Mbps (Universal CPU libx264)",
        width: 1280,
        height: 720,
        targetFps: 60,
        videoBitrate: "8M",
        maxBitrate: "12M",
        bufferSize: "16M",
        constantQuality: 20,
        codec: ExportVideoCodec.Libx264,
        fallbackCodec: ExportVideoCodec.Libx264,
        audioBitrateKbps: 320,
        audioSampleRate: 48000,
        audioChannels: 2);

    /// <summary>
    ///     Predefined Ultra 1080p60 preset (1920x1080, 60fps, 25 Mbps, NVIDIA NVENC GPU with CPU fallback).
    /// </summary>
    public static ExportPreset Ultra1080p60 { get; } = new(
        name: "1080p60 Ultra",
        displayName: "1080p60 Ultra (NVENC GPU)",
        description: "1920x1080 @ 60 FPS, 25 Mbps (Hardware NVENC with QSV/AMF/x264 fallback)",
        width: 1920,
        height: 1080,
        targetFps: 60,
        videoBitrate: "25M",
        maxBitrate: "40M",
        bufferSize: "50M",
        constantQuality: 18,
        codec: ExportVideoCodec.H264Nvenc,
        fallbackCodec: ExportVideoCodec.Libx264,
        audioBitrateKbps: 320,
        audioSampleRate: 48000,
        audioChannels: 2);

    /// <summary>
    ///     Predefined RTX 4K60 preset (3840x2160, 60fps, 60 Mbps, NVIDIA NVENC HEVC / H.265 GPU).
    /// </summary>
    public static ExportPreset Rtx4K60 { get; } = new(
        name: "4K60 RTX",
        displayName: "4K60 RTX (NVENC HEVC)",
        description: "3840x2160 @ 60 FPS, 60 Mbps (Hardware HEVC/H.265 NVENC)",
        width: 3840,
        height: 2160,
        targetFps: 60,
        videoBitrate: "60M",
        maxBitrate: "90M",
        bufferSize: "120M",
        constantQuality: 16,
        codec: ExportVideoCodec.HevcNvenc,
        fallbackCodec: ExportVideoCodec.H264Nvenc,
        audioBitrateKbps: 320,
        audioSampleRate: 48000,
        audioChannels: 2);

    /// <summary>
    ///     List of all standard predefined presets.
    /// </summary>
    public static IReadOnlyList<ExportPreset> AllPresets { get; } =
    [
        Ultra1080p60,
        Rtx4K60,
        Fast720p60
    ];

    /// <summary>
    ///     Resolves a preset from its name or display name, defaulting to 1080p60 Ultra if unmatched.
    /// </summary>
    public static ExportPreset FromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Ultra1080p60;
        }

        foreach (ExportPreset preset in AllPresets)
        {
            if (string.Equals(preset.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preset.Name, displayName, StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains(preset.Name, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return Ultra1080p60;
    }

    /// <summary>
    ///     Creates a custom export preset with user-specified resolution, framerate, and bitrates.
    /// </summary>
    public static ExportPreset CreateCustom(
        int width,
        int height,
        int fps,
        string videoBitrate,
        ExportVideoCodec codec,
        int audioBitrateKbps = 320,
        int constantQuality = 18,
        string? maxBitrate = null,
        string? bufferSize = null)
    {
        return new ExportPreset(
            name: "Custom",
            displayName: $"Custom ({width}x{height} @ {fps}fps)",
            description: $"{width}x{height} @ {fps} FPS, {videoBitrate} ({codec})",
            width: width,
            height: height,
            targetFps: fps,
            videoBitrate: videoBitrate,
            maxBitrate: maxBitrate ?? videoBitrate,
            bufferSize: bufferSize ?? videoBitrate,
            constantQuality: constantQuality,
            codec: codec,
            fallbackCodec: ExportVideoCodec.Libx264,
            audioBitrateKbps: audioBitrateKbps,
            audioSampleRate: 48000,
            audioChannels: 2);
    }
}

/// <summary>
///     Encapsulates all parameters required for executing a video & audio export operation.
/// </summary>
public sealed class ExportParameters
{
    public ReplayExportBackendMode BackendMode { get; set; } = ReplayExportBackendMode.SoftwarePreview;
    public ExportPreset Preset { get; set; } = ExportPreset.Ultra1080p60;
    public string OutputFilePath { get; set; } = string.Empty;
    public string? ReplayFilePath { get; set; }
    public string? GameDirectory { get; set; }
    public string? VulkanLayerPath { get; set; }
    public int InMarkerMs { get; set; }
    public int OutMarkerMs { get; set; }
    public bool IncludeAudio { get; set; } = true;
    public string FfmpegExecutablePath { get; set; } = "ffmpeg";
    public string FfprobeExecutablePath { get; set; } = "ffprobe";
    public bool VerifyOutputWithFfprobe { get; set; } = true;
    public bool AllowSimulatedFallback { get; set; } = false;
    public int PovClientIndex { get; set; } = 0;
    public bool EnableCleanCinematicUi { get; set; } = false;
    public bool UseSharedMemoryIpc { get; set; } = true;
    public bool AllowForegroundActivation { get; set; } = false;
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromMinutes(30);

    // Optional override parameters
    public int? OverrideWidth { get; set; }
    public int? OverrideHeight { get; set; }
    public int? OverrideFps { get; set; }
    public string? OverrideVideoBitrate { get; set; }
    public ExportVideoCodec? OverrideCodec { get; set; }
    public int? OverrideAudioBitrateKbps { get; set; }

    public int Width => OverrideWidth ?? Preset.Width;
    public int Height => OverrideHeight ?? Preset.Height;
    public int TargetFps => OverrideFps ?? Preset.TargetFps;
    public string VideoBitrate => OverrideVideoBitrate ?? Preset.VideoBitrate;
    public string MaxBitrate => Preset.MaxBitrate;
    public string BufferSize => Preset.BufferSize;
    public int ConstantQuality => Preset.ConstantQuality;
    public ExportVideoCodec Codec => OverrideCodec ?? Preset.Codec;
    public int AudioBitrateKbps => OverrideAudioBitrateKbps ?? Preset.AudioBitrateKbps;
    public int AudioSampleRate => Preset.AudioSampleRate;
    public int AudioChannels => Preset.AudioChannels;

    /// <summary>
    ///     Total duration of the export in milliseconds ($t_{out} - t_{in}$).
    /// </summary>
    public int TotalDurationMs => Math.Max(0, OutMarkerMs - InMarkerMs);

    /// <summary>
    ///     Total video frames to render ($N = \lfloor \text{Duration} \times \frac{\text{FPS}}{1000} \rfloor$).
    /// </summary>
    public int TotalFrames
    {
        get
        {
            if (TargetFps <= 0 || TotalDurationMs <= 0)
            {
                return 0;
            }
            return (int)Math.Floor((double)TotalDurationMs * TargetFps / 1000.0);
        }
    }

    /// <summary>
    ///     Calculates the replay timestamp in milliseconds for frame index $i$.
    /// </summary>
    public int GetFrameTimestampMs(int frameIndex)
    {
        if (TargetFps <= 0) return InMarkerMs;
        return InMarkerMs + (int)Math.Floor((double)frameIndex * 1000.0 / TargetFps);
    }

    /// <summary>
    ///     Calculates uncompressed video frame size in bytes ($W \times H \times 4$).
    /// </summary>
    public int CalculatedVideoFrameBytes => Width * Height * 4;

    /// <summary>
    ///     Calculates audio byte quota per video frame:
    ///     $\frac{\text{SampleRate}}{\text{FPS}} \times \text{Channels} \times \frac{\text{BitsPerSample}}{8}$.
    ///     For 48kHz Stereo 16-bit at 60 FPS: $\frac{48000}{60} \times 2 \times 2 = 3200$ bytes.
    /// </summary>
    public int CalculatedAudioBytesPerFrame
    {
        get
        {
            if (TargetFps <= 0) return 0;
            int samplesPerFramePerChannel = AudioSampleRate / TargetFps;
            return samplesPerFramePerChannel * AudioChannels * 2;
        }
    }

    /// <summary>
    ///     Validates export parameters and throws descriptive exceptions for invalid settings.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputFilePath))
        {
            throw new ArgumentException("Output file path cannot be null or whitespace.", nameof(OutputFilePath));
        }

        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentException($"Invalid render dimensions ({Width}x{Height}). Must be positive integers.");
        }

        if (TargetFps <= 0)
        {
            throw new ArgumentException($"Invalid target framerate ({TargetFps}). Must be positive.", nameof(TargetFps));
        }

        if (InMarkerMs < 0)
        {
            throw new ArgumentException($"InMarkerMs ({InMarkerMs}) cannot be negative.", nameof(InMarkerMs));
        }

        if (OutMarkerMs <= InMarkerMs)
        {
            throw new ArgumentException($"OutMarkerMs ({OutMarkerMs}) must be strictly greater than InMarkerMs ({InMarkerMs}).");
        }

        if (AudioSampleRate <= 0 || (AudioSampleRate % TargetFps != 0))
        {
            throw new ArgumentException(
                $"AudioSampleRate ({AudioSampleRate}Hz) must be evenly divisible by TargetFps ({TargetFps}) for bit-perfect frame slicing.");
        }
    }
}

/// <summary>
///     Real-time progress telemetry emitted during video export.
/// </summary>
public sealed class ExportProgress
{
    public int RenderedFrames { get; set; }
    public int TotalFrames { get; set; }
    public double PercentComplete { get; set; }
    public double CurrentFps { get; set; }
    public double EtaSeconds { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int CurrentTimestampMs { get; set; }
    public string StatusText { get; set; } = string.Empty;

    public string FormattedEta => TimeSpan.FromSeconds(Math.Max(0, EtaSeconds)).ToString(@"mm\:ss");
    public string FormattedElapsed => Elapsed.ToString(@"mm\:ss");
}

/// <summary>
///     Final result summary of a video export operation.
/// </summary>
public sealed class ExportResult
{
    public bool Success { get; set; }
    public ReplayExportBackendMode BackendMode { get; set; } = ReplayExportBackendMode.SoftwarePreview;
    public string OutputFilePath { get; set; } = string.Empty;
    public int TotalFramesRendered { get; set; }
    public TimeSpan Duration { get; set; }
    public long OutputSizeBytes { get; set; }
    public double AverageFps { get; set; }
    public string? ErrorMessage { get; set; }
    public FfprobeVideoVerificationResult? VerificationResult { get; set; }
    public bool ZeroWindowExposureEnforced { get; set; } = true;

    public double OutputSizeMegabytes => (double)OutputSizeBytes / (1024.0 * 1024.0);
}
