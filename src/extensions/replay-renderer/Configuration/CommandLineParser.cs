// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CommandLineParser.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Replay;

namespace ReplayRenderer.Service.Configuration;

/// <summary>
///     Parses and validates command line arguments into a RenderJobConfiguration instance.
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    ///     Parses CLI arguments and resolves paths, resolution, start offset, and player slot.
    ///     Returns null if required arguments are missing or invalid.
    /// </summary>
    public static RenderJobConfiguration? Parse(string[] args, string? baseDirectory = null)
    {
        string rootDir = baseDirectory ?? Directory.GetCurrentDirectory();

        string? replayPath = null;
        string? outputPath = null;
        string? gameDir = null;
        string? layerDir = null;
        string? playerArg = null;
        string? povArg = null;
        uint width = 1920;
        uint height = 1080;
        int fps = 60;
        int startSeconds = 0;
        VideoCodec codec = VideoCodec.H264Nvenc;
        float timescale = 1.0f;
        bool cinematicUi = false;
        string bitrate = "25M";
        string maxrate = "40M";
        int cq = 18;
        string ffmpegPath = "ffmpeg";
        string ffprobePath = "ffprobe";
        bool verifyOutputWithFfprobe = true;
        bool headless = true;
        bool allowForegroundActivation = false;
        bool sharedMem = true;
        TimeSpan? maxDuration = null;
        int timeoutMinutes = 30;
        int stallTimeoutSeconds = 5;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                string value = args[++i];
                switch (key.ToLowerInvariant())
                {
                    case "--replay":
                    case "-r":
                        replayPath = value;
                        break;
                    case "--output":
                    case "-o":
                        outputPath = value;
                        break;
                    case "--player":
                    case "-p":
                        playerArg = value;
                        break;
                    case "--pov":
                        povArg = value;
                        break;
                    case "--resolution":
                    case "-res":
                        if (TryParseResolution(value, out uint resW, out uint resH))
                        {
                            width = resW;
                            height = resH;
                        }

                        break;
                    case "--width":
                    case "-w":
                        if (uint.TryParse(value, out uint parsedWidth)) width = parsedWidth;
                        break;
                    case "--height":
                    case "-h":
                        if (uint.TryParse(value, out uint parsedHeight)) height = parsedHeight;
                        break;
                    case "--start":
                    case "-s":
                        if (int.TryParse(value, out int parsedStart) && parsedStart >= 0) startSeconds = parsedStart;
                        break;
                    case "--fps":
                        if (int.TryParse(value, out int parsedFps)) fps = parsedFps;
                        break;
                    case "--codec":
                    case "-c":
                        if (string.Equals(value, "hevc_nvenc", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(value, "hevc", StringComparison.OrdinalIgnoreCase))
                            codec = VideoCodec.HevcNvenc;
                        else if (string.Equals(value, "libx264", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(value, "cpu", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(value, "x264", StringComparison.OrdinalIgnoreCase))
                            codec = VideoCodec.Libx264CpuFallback;
                        else
                            codec = VideoCodec.H264Nvenc;
                        break;
                    case "--game-dir":
                    case "-g":
                        gameDir = value;
                        break;
                    case "--layer-dir":
                    case "-l":
                        layerDir = value;
                        break;
                    case "--timescale":
                        if (float.TryParse(value, out float parsedTs)) timescale = parsedTs;
                        break;
                    case "--cinematic-ui":
                        if (bool.TryParse(value, out bool parsedCinematic)) cinematicUi = parsedCinematic;
                        break;
                    case "--bitrate":
                    case "-b":
                        bitrate = value;
                        break;
                    case "--maxrate":
                        maxrate = value;
                        break;
                    case "--cq":
                        if (int.TryParse(value, out int parsedCq)) cq = parsedCq;
                        break;
                    case "--ffmpeg":
                        ffmpegPath = value;
                        break;
                    case "--ffprobe":
                        ffprobePath = value;
                        break;
                    case "--verify-ffprobe":
                        if (bool.TryParse(value, out bool parsedVerify)) verifyOutputWithFfprobe = parsedVerify;
                        break;
                    case "--headless":
                        if (bool.TryParse(value, out bool parsedHeadless)) headless = parsedHeadless;
                        break;
                    case "--allow-foreground-activation":
                        if (bool.TryParse(value, out bool parsedAllowFg)) allowForegroundActivation = parsedAllowFg;
                        break;
                    case "--timeout":
                        if (int.TryParse(value, out int parsedTimeout)) timeoutMinutes = parsedTimeout;
                        break;
                    case "--stall-timeout":
                        if (int.TryParse(value, out int parsedStall)) stallTimeoutSeconds = parsedStall;
                        break;
                    case "--duration":
                    case "-d":
                        if (double.TryParse(value, out double parsedDuration) && parsedDuration > 0)
                            maxDuration = TimeSpan.FromSeconds(parsedDuration);
                        break;
                }
            }
            else
            {
                switch (key.ToLowerInvariant())
                {
                    case "--shared-memory":
                        sharedMem = true;
                        break;
                    case "--named-pipe":
                        sharedMem = false;
                        break;
                    case "--cinematic-ui":
                        cinematicUi = true;
                        break;
                    case "--headless":
                        headless = true;
                        break;
                    case "--allow-foreground-activation":
                        allowForegroundActivation = true;
                        break;
                    case "--skip-ffprobe":
                        verifyOutputWithFfprobe = false;
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(replayPath) || string.IsNullOrWhiteSpace(outputPath) ||
            !File.Exists(replayPath)) return null;

        // Auto-detect game directory if not specified
        if (string.IsNullOrWhiteSpace(gameDir)) gameDir = DetectGameDirectory(rootDir);

        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        // Auto-detect Vulkan layer directory if not specified
        if (string.IsNullOrWhiteSpace(layerDir)) layerDir = DetectVulkanLayerDirectory(gameDir, rootDir);

        layerDir ??= AppContext.BaseDirectory;

        // Resolve Player POV (slot 0..9)
        int resolvedPov = 0;
        if (!string.IsNullOrWhiteSpace(playerArg))
            resolvedPov = ResolvePlayerSlot(playerArg, replayPath);
        else if (!string.IsNullOrWhiteSpace(povArg))
            if (int.TryParse(povArg, out int parsedPov))
                resolvedPov = parsedPov;

        return new RenderJobConfiguration
        {
            ReplayFilePath = Path.GetFullPath(replayPath),
            OutputFilePath = Path.GetFullPath(outputPath),
            GameDirectory = Path.GetFullPath(gameDir),
            VulkanLayerPath = Path.GetFullPath(layerDir),
            RenderWidth = width,
            RenderHeight = height,
            TargetFramerate = fps,
            StartSeconds = startSeconds,
            Codec = codec,
            PovClientIndex = resolvedPov,
            Timescale = timescale,
            EnableCleanCinematicUi = cinematicUi,
            VideoBitrate = bitrate,
            MaxBitrate = maxrate,
            ConstantQuality = cq,
            FfmpegExecutablePath = ffmpegPath,
            FfprobeExecutablePath = ffprobePath,
            VerifyOutputWithFfprobe = verifyOutputWithFfprobe,
            Headless = headless,
            AllowForegroundActivation = allowForegroundActivation,
            UseSharedMemoryIpc = sharedMem,
            MaxDuration = maxDuration,
            ProcessTimeout = TimeSpan.FromMinutes(timeoutMinutes),
            StallTimeout = TimeSpan.FromSeconds(stallTimeoutSeconds)
        };
    }

    /// <summary>
    ///     Parses a resolution string formatted as &lt;Width&gt;x&lt;Height&gt; (e.g. 1920x1080, 2560x1440)
    ///     or standard preset labels (720p, 1080p, 1440p, 2K, 4K, 2160p).
    /// </summary>
    public static bool TryParseResolution(string resolutionStr, out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(resolutionStr)) return false;

        string normalized = resolutionStr.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "4k":
            case "2160p":
            case "uhd":
                width = 3840;
                height = 2160;
                return true;
            case "1440p":
            case "2k":
            case "qhd":
                width = 2560;
                height = 1440;
                return true;
            case "1080p":
            case "fhd":
                width = 1920;
                height = 1080;
                return true;
            case "720p":
            case "hd":
                width = 1280;
                height = 720;
                return true;
        }

        string[] parts = resolutionStr.Split(['x', 'X', '*', ':', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && uint.TryParse(parts[0], out uint w) && uint.TryParse(parts[1], out uint h) && w > 0 &&
            h > 0)
        {
            width = w;
            height = h;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Resolves a player identifier (slot index 0..9, player name, hero name, or team slot alias) into an integer client
    ///     index.
    /// </summary>
    public static int ResolvePlayerSlot(string playerArg, string? replayFilePath)
    {
        if (string.IsNullOrWhiteSpace(playerArg)) return 0;

        if (int.TryParse(playerArg, out int slot))
            if (slot is >= 0 and <= 15)
                return slot;

        if (!string.IsNullOrWhiteSpace(replayFilePath) && File.Exists(replayFilePath))
            try
            {
                ReplayMetadata metadata = ReplayMetadataParser.ParseFromFile(replayFilePath);
                PlayerResolutionResult result = metadata.ResolvePlayer(playerArg);
                if (result.Confidence != PlayerMatchConfidence.Fallback) return result.ClientIndex;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to resolve player '{playerArg}' from replay file: {ex.Message}",
                    ex);
            }

        throw new ArgumentException(
            $"Player '{playerArg}' could not be resolved to a valid slot (0..9), hero, team slot, or player name in replay metadata.");
    }

    /// <summary>
    ///     Automatically searches for the HoN client directory containing hon_x64.exe.
    /// </summary>
    public static string? DetectGameDirectory(string? baseDir = null)
    {
        string root = baseDir ?? Directory.GetCurrentDirectory();
        string[] candidateSubpaths =
        [
            Path.Combine(root, "games", "client"),
            Path.Combine(root, "client"),
            Path.Combine(root, "..", "..", "games", "client"),
            Path.Combine(root, "..", "games", "client"),
            Path.Combine(AppContext.BaseDirectory, "games", "client"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "games", "client"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "games", "client"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "games", "client"),
            Path.Combine(AppContext.BaseDirectory, "..", "games", "client"),
            root
        ];

        for (int i = 0; i < candidateSubpaths.Length; i++)
            try
            {
                string fullPath = Path.GetFullPath(candidateSubpaths[i]);
                if (File.Exists(Path.Combine(fullPath, "hon_x64.exe"))) return fullPath;
            }
            catch
            {
                // Ignore path errors
            }

        return null;
    }

    /// <summary>
    ///     Automatically searches for the Vulkan capture layer directory containing VkLayer_hon_replay_capture.json/dll.
    /// </summary>
    public static string? DetectVulkanLayerDirectory(string? gameDir = null, string? baseDir = null)
    {
        string root = baseDir ?? Directory.GetCurrentDirectory();
        string[] candidateSubpaths =
        [
            Path.Combine(root, "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(root, "native", "vulkan-layer", "build", "Release"),
            Path.Combine(root, "..", "..", "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(root, "..", "..", "native", "vulkan-layer", "build", "Release"),
            Path.Combine(root, "..", "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(root, "..", "native", "vulkan-layer", "build", "Release"),
            Path.Combine(AppContext.BaseDirectory, "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(AppContext.BaseDirectory, "native", "vulkan-layer", "build", "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "vulkan-layer", "build",
                "x64", "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "vulkan-layer", "build",
                "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "native", "vulkan-layer", "build", "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "native", "vulkan-layer", "build", "Release"),
            Path.Combine(root, "games", "client"),
            gameDir ?? string.Empty,
            AppContext.BaseDirectory
        ];

        for (int i = 0; i < candidateSubpaths.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(candidateSubpaths[i])) continue;
            try
            {
                string fullPath = Path.GetFullPath(candidateSubpaths[i]);
                if (File.Exists(Path.Combine(fullPath, "VkLayer_hon_replay_capture.json")) ||
                    File.Exists(Path.Combine(fullPath, "VkLayer_hon_replay_capture.dll")))
                    return fullPath;
            }
            catch
            {
                // Ignore path errors
            }
        }

        return null;
    }
}
