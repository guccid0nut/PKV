// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FfprobeVideoVerifier.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Models;

namespace ReplayRenderer.Service.Encoding;

/// <summary>
///     Validation constraints and execution settings for post-export <c>ffprobe</c> video verification.
/// </summary>
public sealed class FfprobeVerificationOptions
{
    /// <summary>
    ///     Expected video stream codec name reported by ffprobe (e.g. "h264", "hevc").
    ///     If null or whitespace, any non-empty video codec is accepted.
    /// </summary>
    public string? ExpectedCodecName { get; set; }

    /// <summary>
    ///     Expected video stream width in pixels. If null, any positive width is accepted.
    /// </summary>
    public int? ExpectedWidth { get; set; }

    /// <summary>
    ///     Expected video stream height in pixels. If null, any positive height is accepted.
    /// </summary>
    public int? ExpectedHeight { get; set; }

    /// <summary>
    ///     Minimum required encoded video frame count. Default: 1.
    /// </summary>
    public long MinimumFrameCount { get; set; } = 1;

    /// <summary>
    ///     Minimum required container duration in seconds. Default: 0.001s (strictly non-zero).
    /// </summary>
    public double MinimumDurationSeconds { get; set; } = 0.001;

    /// <summary>
    ///     Minimum required container size in bytes. Default: 1 byte.
    /// </summary>
    public long MinimumFileSizeBytes { get; set; } = 1;

    /// <summary>
    ///     Maximum time to wait for <c>ffprobe</c> to finish probing the output container. Default: 15s.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Optional explicit path or binary name for <c>ffprobe</c>. Defaults to "ffprobe".
    /// </summary>
    public string? FfprobeExecutablePath { get; set; }

    /// <summary>
    ///     Optional path to the paired <c>ffmpeg</c> executable so sibling <c>ffprobe</c> binaries can be resolved.
    /// </summary>
    public string? FfmpegExecutablePath { get; set; }
}

/// <summary>
///     Structured verification report emitted by <see cref="FfprobeVideoVerifier" /> after inspecting an output video file.
/// </summary>
public sealed class FfprobeVideoVerificationResult
{
    public bool IsValid { get; init; }
    public string VideoFilePath { get; init; } = string.Empty;
    public string FfprobeExecutablePath { get; init; } = string.Empty;
    public string FfprobeArguments { get; init; } = string.Empty;
    public int ExitCode { get; init; } = -1;
    public string CodecName { get; init; } = string.Empty;
    public string? AudioCodecName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long FrameCount { get; init; }
    public string RawFrameRate { get; init; } = string.Empty;
    public double FrameRate { get; init; }
    public double DurationSeconds { get; init; }
    public long FileSizeBytes { get; init; }
    public TimeSpan ProbeElapsed { get; init; }
    public string RawJson { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public string Summary { get; init; } = string.Empty;

    public string? FailureReason => ValidationErrors.Count > 0 ? string.Join("; ", ValidationErrors) : null;
}

/// <summary>
///     Invokes real <c>ffprobe</c> to verify that an exported MP4 contains a decodable video stream with
///     expected codec, dimensions, and strictly non-zero duration, file size, and frame count.
/// </summary>
public sealed class FfprobeVideoVerifier(ILogger<FfprobeVideoVerifier>? logger = null)
{
    private const string ChocolateyFfprobePath = @"C:\ProgramData\chocolatey\bin\ffprobe.exe";

    private readonly ILogger<FfprobeVideoVerifier> _logger = logger ?? NullLogger<FfprobeVideoVerifier>.Instance;

    /// <summary>
    ///     Maps a <see cref="VideoCodec" /> encoder selection to the canonical <c>ffprobe</c> <c>codec_name</c>.
    /// </summary>
    public static string GetExpectedCodecName(VideoCodec codec)
    {
        return codec switch
        {
            VideoCodec.HevcNvenc => "hevc",
            _ => "h264"
        };
    }

    /// <summary>
    ///     Normalizes encoder/codec aliases (e.g. "libx264", "h264_nvenc", "h265") to canonical <c>ffprobe</c> codec names.
    /// </summary>
    public static string NormalizeCodecName(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName)) return string.Empty;

        string normalized = codecName.Trim().ToLowerInvariant();
        return normalized switch
        {
            "h264_nvenc" or "h264_qsv" or "h264_amf" or "libx264" or "x264" or "avc" or "avc1" => "h264",
            "hevc_nvenc" or "libx265" or "x265" or "h265" or "hvc1" or "hev1" => "hevc",
            _ => normalized
        };
    }

    /// <summary>
    ///     Builds the exact <c>ffprobe</c> JSON probe argument line required for stream and container validation.
    /// </summary>
    public static string BuildArguments(string videoFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoFilePath);
        string fullPath = Path.GetFullPath(videoFilePath);
        return $"-v error -show_entries stream=codec_name,width,height,nb_frames,r_frame_rate:format=duration,size -of json \"{fullPath}\"";
    }

    /// <summary>
    ///     Resolves the <c>ffprobe</c> executable path while honoring explicit custom caller paths (for fail-closed tests).
    /// </summary>
    public static string ResolveFfprobeExecutablePath(
        string? ffprobeExecutablePath,
        string? ffmpegExecutablePath = null)
    {
        bool isDefaultName = string.IsNullOrWhiteSpace(ffprobeExecutablePath) ||
                             string.Equals(ffprobeExecutablePath.Trim(), "ffprobe", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ffprobeExecutablePath.Trim(), "ffprobe.exe", StringComparison.OrdinalIgnoreCase);

        if (!isDefaultName)
        {
            return ffprobeExecutablePath!.Trim();
        }

        string binaryName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        if (!string.IsNullOrWhiteSpace(ffmpegExecutablePath))
        {
            string trimmedFfmpeg = ffmpegExecutablePath.Trim();
            if (Path.IsPathRooted(trimmedFfmpeg))
            {
                string? siblingDir = Path.GetDirectoryName(trimmedFfmpeg);
                if (!string.IsNullOrWhiteSpace(siblingDir))
                {
                    string siblingCandidate = Path.Combine(siblingDir, binaryName);
                    if (File.Exists(siblingCandidate))
                    {
                        return siblingCandidate;
                    }
                }
            }
        }

        string? envOverride = Environment.GetEnvironmentVariable("FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return envOverride;
        }

        if (IsExecutableOnPath(binaryName))
        {
            return string.IsNullOrWhiteSpace(ffprobeExecutablePath) ? "ffprobe" : ffprobeExecutablePath.Trim();
        }

        if (OperatingSystem.IsWindows() && File.Exists(ChocolateyFfprobePath))
        {
            return ChocolateyFfprobePath;
        }

        return string.IsNullOrWhiteSpace(ffprobeExecutablePath) ? "ffprobe" : ffprobeExecutablePath.Trim();
    }

    /// <summary>
    ///     Parses a rational framerate string from <c>ffprobe</c> (e.g. "60/1", "30000/1001", "60") into FPS.
    /// </summary>
    public static bool TryParseFrameRate(string? rawFrameRate, out double fps)
    {
        fps = 0.0;
        if (string.IsNullOrWhiteSpace(rawFrameRate)) return false;

        string trimmed = rawFrameRate.Trim();
        int slashIndex = trimmed.IndexOf('/');
        if (slashIndex >= 0)
        {
            string numStr = trimmed[..slashIndex];
            string denStr = trimmed[(slashIndex + 1)..];
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                double.TryParse(denStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
                den > 0.0 &&
                num > 0.0)
            {
                double computed = num / den;
                if (double.IsFinite(computed) && computed > 0.0)
                {
                    fps = computed;
                    return true;
                }
            }

            return false;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double direct) &&
            double.IsFinite(direct) &&
            direct > 0.0)
        {
            fps = direct;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Executes <c>ffprobe</c> against <paramref name="videoFilePath" /> and validates all stream and container constraints.
    /// </summary>
    public async Task<FfprobeVideoVerificationResult> VerifyAsync(
        string videoFilePath,
        FfprobeVerificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        FfprobeVerificationOptions effectiveOptions = options ?? new FfprobeVerificationOptions();
        Stopwatch stopwatch = Stopwatch.StartNew();

        string resolvedFfprobe = ResolveFfprobeExecutablePath(
            effectiveOptions.FfprobeExecutablePath,
            effectiveOptions.FfmpegExecutablePath);

        if (string.IsNullOrWhiteSpace(videoFilePath))
        {
            stopwatch.Stop();
            return BuildFailureResult(
                videoFilePath: string.Empty,
                ffprobeExecutablePath: resolvedFfprobe,
                ffprobeArguments: string.Empty,
                exitCode: -1,
                elapsed: stopwatch.Elapsed,
                rawJson: string.Empty,
                stderr: string.Empty,
                errors: ["Video file path is null or empty. Provide a valid exported .mp4 path."]);
        }

        string fullVideoPath = Path.GetFullPath(videoFilePath);
        string arguments = BuildArguments(fullVideoPath);

        if (!File.Exists(fullVideoPath))
        {
            stopwatch.Stop();
            string missingError = $"Exported video file does not exist at '{fullVideoPath}'.";
            _logger.LogError("[FfprobeVerifier] FAILED: {Error}", missingError);
            return BuildFailureResult(
                videoFilePath: fullVideoPath,
                ffprobeExecutablePath: resolvedFfprobe,
                ffprobeArguments: arguments,
                exitCode: -1,
                elapsed: stopwatch.Elapsed,
                rawJson: string.Empty,
                stderr: string.Empty,
                errors: [missingError]);
        }

        long diskFileSizeBytes = new FileInfo(fullVideoPath).Length;
        if (diskFileSizeBytes <= 0)
        {
            stopwatch.Stop();
            string emptyFileError = $"Exported video file '{fullVideoPath}' is empty (0 bytes).";
            _logger.LogError("[FfprobeVerifier] FAILED: {Error}", emptyFileError);
            return BuildFailureResult(
                videoFilePath: fullVideoPath,
                ffprobeExecutablePath: resolvedFfprobe,
                ffprobeArguments: arguments,
                exitCode: -1,
                elapsed: stopwatch.Elapsed,
                rawJson: string.Empty,
                stderr: string.Empty,
                errors: [emptyFileError]);
        }

        _logger.LogInformation(
            "[FfprobeVerifier] Probing '{VideoPath}' ({DiskSize:N0} bytes) using '{Ffprobe}' {Args} (ExpectedCodec={Codec}, ExpectedDims={W}x{H})...",
            fullVideoPath,
            diskFileSizeBytes,
            resolvedFfprobe,
            arguments,
            effectiveOptions.ExpectedCodecName ?? "any",
            effectiveOptions.ExpectedWidth?.ToString(CultureInfo.InvariantCulture) ?? "any",
            effectiveOptions.ExpectedHeight?.ToString(CultureInfo.InvariantCulture) ?? "any");

        ProcessStartInfo startInfo = new()
        {
            FileName = resolvedFfprobe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = new() { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                stopwatch.Stop();
                string startError =
                    $"Failed to start ffprobe executable '{resolvedFfprobe}'. Install FFmpeg/FFprobe on PATH or configure FfprobeExecutablePath.";
                _logger.LogError("[FfprobeVerifier] FAILED: {Error}", startError);
                return BuildFailureResult(
                    videoFilePath: fullVideoPath,
                    ffprobeExecutablePath: resolvedFfprobe,
                    ffprobeArguments: arguments,
                    exitCode: -1,
                    elapsed: stopwatch.Elapsed,
                    rawJson: string.Empty,
                    stderr: string.Empty,
                    errors: [startError]);
            }
        }
        catch (Win32Exception ex)
        {
            stopwatch.Stop();
            string spawnError =
                $"ffprobe executable '{resolvedFfprobe}' could not be launched ({ex.Message}). Install FFmpeg/FFprobe on PATH or specify FfprobeExecutablePath.";
            _logger.LogError(ex, "[FfprobeVerifier] FAILED to launch ffprobe '{Ffprobe}': {Message}", resolvedFfprobe, ex.Message);
            return BuildFailureResult(
                videoFilePath: fullVideoPath,
                ffprobeExecutablePath: resolvedFfprobe,
                ffprobeArguments: arguments,
                exitCode: -1,
                elapsed: stopwatch.Elapsed,
                rawJson: string.Empty,
                stderr: ex.Message,
                errors: [spawnError]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            string unexpectedError = $"Unexpected error launching ffprobe '{resolvedFfprobe}': {ex.Message}";
            _logger.LogError(ex, "[FfprobeVerifier] FAILED: {Error}", unexpectedError);
            return BuildFailureResult(
                videoFilePath: fullVideoPath,
                ffprobeExecutablePath: resolvedFfprobe,
                ffprobeArguments: arguments,
                exitCode: -1,
                elapsed: stopwatch.Elapsed,
                rawJson: string.Empty,
                stderr: ex.Message,
                errors: [unexpectedError]);
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveOptions.ProbeTimeout);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            stopwatch.Stop();

            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                string trimmedErr = stderr.Trim();
                string exitError = string.IsNullOrWhiteSpace(trimmedErr)
                    ? $"ffprobe exited with non-zero code {exitCode} while probing '{fullVideoPath}'."
                    : $"ffprobe exited with code {exitCode}: {trimmedErr}";
                _logger.LogError(
                    "[FfprobeVerifier] FAILED '{VideoPath}' (exitCode={ExitCode}, probe={ElapsedMs}ms): {Error}",
                    fullVideoPath, exitCode, stopwatch.ElapsedMilliseconds, exitError);
                return BuildFailureResult(
                    videoFilePath: fullVideoPath,
                    ffprobeExecutablePath: resolvedFfprobe,
                    ffprobeArguments: arguments,
                    exitCode: exitCode,
                    elapsed: stopwatch.Elapsed,
                    rawJson: stdout,
                    stderr: trimmedErr,
                    errors: [exitError]);
            }

            FfprobeVideoVerificationResult result = ParseAndValidateProbeJson(
                stdout,
                fullVideoPath,
                resolvedFfprobe,
                arguments,
                exitCode,
                stopwatch.Elapsed,
                stderr.Trim(),
                effectiveOptions,
                diskFileSizeBytes);

            if (result.IsValid)
            {
                _logger.LogInformation(
                    "[FfprobeVerifier] VERIFIED '{VideoPath}': codec={Codec}, audio={Audio}, dims={Width}x{Height}, fps={Fps:F2} ({RawFps}), frames={Frames}, duration={Duration:F3}s, size={Size:N0} bytes (probe={ElapsedMs}ms)",
                    result.VideoFilePath,
                    result.CodecName,
                    result.AudioCodecName ?? "none",
                    result.Width,
                    result.Height,
                    result.FrameRate,
                    result.RawFrameRate,
                    result.FrameCount,
                    result.DurationSeconds,
                    result.FileSizeBytes,
                    result.ProbeElapsed.TotalMilliseconds);
            }
            else
            {
                _logger.LogError(
                    "[FfprobeVerifier] VALIDATION FAILED for '{VideoPath}' (probe={ElapsedMs}ms): {Errors}",
                    result.VideoFilePath,
                    result.ProbeElapsed.TotalMilliseconds,
                    result.FailureReason);
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            TryKillProcess(process);
            string timeoutError =
                $"ffprobe timed out after {effectiveOptions.ProbeTimeout.TotalSeconds:F1}s while probing '{fullVideoPath}'.";
            _logger.LogError("[FfprobeVerifier] TIMEOUT: {Error}", timeoutError);
            return BuildFailureResult(
                videoFilePath: fullVideoPath,
                ffprobeExecutablePath: resolvedFfprobe,
                ffprobeArguments: arguments,
                exitCode: -1,
                elapsed: stopwatch.Elapsed,
                rawJson: string.Empty,
                stderr: timeoutError,
                errors: [timeoutError]);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    /// <summary>
    ///     Parses <c>ffprobe</c> JSON output and enforces codec, dimension, frame count, framerate, duration, and file size invariants.
    /// </summary>
    public static FfprobeVideoVerificationResult ParseAndValidateProbeJson(
        string jsonPayload,
        string videoFilePath,
        string ffprobeExecutablePath,
        string ffprobeArguments,
        int exitCode,
        TimeSpan probeElapsed,
        string stderr,
        FfprobeVerificationOptions? options = null,
        long? fallbackDiskSizeBytes = null)
    {
        FfprobeVerificationOptions effectiveOptions = options ?? new FfprobeVerificationOptions();
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            errors.Add("ffprobe produced empty JSON output.");
            return BuildFailureResult(videoFilePath, ffprobeExecutablePath, ffprobeArguments, exitCode, probeElapsed, jsonPayload, stderr, errors);
        }

        string codecName = string.Empty;
        string? audioCodecName = null;
        int width = 0;
        int height = 0;
        long frameCount = 0;
        string rawFrameRate = string.Empty;
        double parsedFrameRate = 0.0;
        double durationSeconds = 0.0;
        long fileSizeBytes = fallbackDiskSizeBytes ?? 0;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonPayload);
            JsonElement root = doc.RootElement;

            JsonElement? videoStream = null;
            if (root.TryGetProperty("streams", out JsonElement streamsElem) && streamsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streamsElem.EnumerateArray())
                {
                    bool hasWidth = TryReadInt(stream, "width", out int candidateW) && candidateW > 0;
                    bool hasHeight = TryReadInt(stream, "height", out int candidateH) && candidateH > 0;
                    string streamCodec = TryReadString(stream, "codec_name") ?? string.Empty;

                    if (videoStream == null && (hasWidth || hasHeight))
                    {
                        videoStream = stream;
                        codecName = streamCodec;
                        width = candidateW;
                        height = candidateH;
                        TryReadLong(stream, "nb_frames", out frameCount);
                        rawFrameRate = TryReadString(stream, "r_frame_rate") ?? string.Empty;
                        TryParseFrameRate(rawFrameRate, out parsedFrameRate);
                    }
                    else if (!hasWidth && !hasHeight && !string.IsNullOrWhiteSpace(streamCodec) && audioCodecName == null)
                    {
                        audioCodecName = streamCodec;
                    }
                }

                // Fallback: if no stream carried width/height, inspect first stream for diagnostics
                if (videoStream == null && streamsElem.GetArrayLength() > 0)
                {
                    JsonElement first = streamsElem[0];
                    codecName = TryReadString(first, "codec_name") ?? string.Empty;
                    TryReadInt(first, "width", out width);
                    TryReadInt(first, "height", out height);
                    TryReadLong(first, "nb_frames", out frameCount);
                    rawFrameRate = TryReadString(first, "r_frame_rate") ?? string.Empty;
                    TryParseFrameRate(rawFrameRate, out parsedFrameRate);
                }
            }
            else
            {
                errors.Add("ffprobe JSON output did not contain a 'streams' array.");
            }

            if (root.TryGetProperty("format", out JsonElement formatElem) && formatElem.ValueKind == JsonValueKind.Object)
            {
                TryReadDouble(formatElem, "duration", out durationSeconds);
                if (TryReadLong(formatElem, "size", out long formatSize) && formatSize > 0)
                {
                    fileSizeBytes = formatSize;
                }
            }
            else
            {
                errors.Add("ffprobe JSON output did not contain a 'format' object.");
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Failed to parse ffprobe JSON output: {ex.Message}");
            return BuildFailureResult(videoFilePath, ffprobeExecutablePath, ffprobeArguments, exitCode, probeElapsed, jsonPayload, stderr, errors);
        }

        // 1. Validate video codec
        if (string.IsNullOrWhiteSpace(codecName))
        {
            errors.Add("No video stream codec_name found in ffprobe output.");
        }
        else if (!string.IsNullOrWhiteSpace(effectiveOptions.ExpectedCodecName))
        {
            string expectedNormalized = NormalizeCodecName(effectiveOptions.ExpectedCodecName);
            string actualNormalized = NormalizeCodecName(codecName);
            if (!string.Equals(expectedNormalized, actualNormalized, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Video codec mismatch: expected '{expectedNormalized}', got '{codecName}'.");
            }
        }

        // 2. Validate dimensions
        if (width <= 0 || height <= 0)
        {
            errors.Add($"Invalid video dimensions reported by ffprobe: {width}x{height} (must be > 0).");
        }
        else
        {
            if (effectiveOptions.ExpectedWidth.HasValue && width != effectiveOptions.ExpectedWidth.Value)
            {
                errors.Add($"Video width mismatch: expected {effectiveOptions.ExpectedWidth.Value}px, got {width}px.");
            }

            if (effectiveOptions.ExpectedHeight.HasValue && height != effectiveOptions.ExpectedHeight.Value)
            {
                errors.Add($"Video height mismatch: expected {effectiveOptions.ExpectedHeight.Value}px, got {height}px.");
            }
        }

        // 3. Validate non-zero frame count
        long minFrames = Math.Max(1L, effectiveOptions.MinimumFrameCount);
        if (frameCount < minFrames)
        {
            errors.Add($"Video frame count {frameCount} is below minimum required {minFrames} (nb_frames must be non-zero).");
        }

        // 4. Validate positive framerate
        if (parsedFrameRate <= 0.0 || !double.IsFinite(parsedFrameRate))
        {
            errors.Add($"Invalid video framerate '{rawFrameRate}' reported by ffprobe.");
        }

        // 5. Validate non-zero duration
        double minDuration = Math.Max(0.0, effectiveOptions.MinimumDurationSeconds);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0 || durationSeconds < minDuration)
        {
            errors.Add(
                $"Video duration {durationSeconds.ToString("F3", CultureInfo.InvariantCulture)}s is below minimum required {minDuration.ToString("F3", CultureInfo.InvariantCulture)}s.");
        }

        // 6. Validate non-zero file size
        long minBytes = Math.Max(1L, effectiveOptions.MinimumFileSizeBytes);
        if (fileSizeBytes < minBytes)
        {
            errors.Add($"Video file size {fileSizeBytes} bytes is below minimum required {minBytes} bytes.");
        }

        bool isValid = errors.Count == 0;
        string summary = isValid
            ? $"VERIFIED {codecName} {width}x{height} @ {parsedFrameRate.ToString("F2", CultureInfo.InvariantCulture)}fps ({frameCount} frames, {durationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s, {fileSizeBytes:N0} bytes)"
            : $"FAILED ({errors.Count} error(s)): {string.Join("; ", errors)}";

        return new FfprobeVideoVerificationResult
        {
            IsValid = isValid,
            VideoFilePath = videoFilePath,
            FfprobeExecutablePath = ffprobeExecutablePath,
            FfprobeArguments = ffprobeArguments,
            ExitCode = exitCode,
            CodecName = codecName,
            AudioCodecName = audioCodecName,
            Width = width,
            Height = height,
            FrameCount = frameCount,
            RawFrameRate = rawFrameRate,
            FrameRate = parsedFrameRate,
            DurationSeconds = durationSeconds,
            FileSizeBytes = fileSizeBytes,
            ProbeElapsed = probeElapsed,
            RawJson = jsonPayload,
            Stderr = stderr,
            ValidationErrors = errors,
            Summary = summary
        };
    }

    private void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FfprobeVerifier] Could not terminate ffprobe process cleanly: {Message}", ex.Message);
        }
    }

    private static FfprobeVideoVerificationResult BuildFailureResult(
        string videoFilePath,
        string ffprobeExecutablePath,
        string ffprobeArguments,
        int exitCode,
        TimeSpan elapsed,
        string rawJson,
        string stderr,
        List<string> errors)
    {
        return new FfprobeVideoVerificationResult
        {
            IsValid = false,
            VideoFilePath = videoFilePath,
            FfprobeExecutablePath = ffprobeExecutablePath,
            FfprobeArguments = ffprobeArguments,
            ExitCode = exitCode,
            ProbeElapsed = elapsed,
            RawJson = rawJson,
            Stderr = stderr,
            ValidationErrors = errors,
            Summary = $"FAILED ({errors.Count} error(s)): {string.Join("; ", errors)}"
        };
    }

    private static bool IsExecutableOnPath(string binaryName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return false;

        string[] segments = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        char[] invalidPathChars = Path.GetInvalidPathChars();
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i].Trim('"');
            if (string.IsNullOrWhiteSpace(segment) || segment.IndexOfAny(invalidPathChars) >= 0)
            {
                continue;
            }

            string candidate = Path.Combine(segment, binaryName);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement elem)) return null;
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.GetRawText(),
            _ => null
        };
    }

    private static bool TryReadInt(JsonElement obj, string propertyName, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(propertyName, out JsonElement elem)) return false;
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out value)) return true;
        if (elem.ValueKind == JsonValueKind.String &&
            int.TryParse(elem.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadLong(JsonElement obj, string propertyName, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(propertyName, out JsonElement elem)) return false;
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt64(out value)) return true;
        if (elem.ValueKind == JsonValueKind.String &&
            long.TryParse(elem.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadDouble(JsonElement obj, string propertyName, out double value)
    {
        value = 0.0;
        if (!obj.TryGetProperty(propertyName, out JsonElement elem)) return false;
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out value)) return true;
        if (elem.ValueKind == JsonValueKind.String &&
            double.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }
}
