// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FfmpegPipelineController.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayRenderer.Service.ProcessLifecycle;

namespace ReplayRenderer.Service.Encoding;

/// <summary>
///     Supervises the execution, streaming inputs, telemetry parsing, and container finalization of ffmpeg.exe.
/// </summary>
public sealed class FfmpegPipelineController(
    ILogger<FfmpegPipelineController> logger,
    WindowsJobObjectSandbox? jobSandbox = null) : IAsyncDisposable
{
    private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowsJobObjectSandbox? _jobSandbox = jobSandbox;
    private readonly ILogger<FfmpegPipelineController> _logger = logger;
    private Process? _ffmpegProcess;

    public Task<int> ExitTask => _exitTcs.Task;
    public bool IsRunning => _ffmpegProcess != null && !_ffmpegProcess.HasExited;
    public int? ProcessId => _ffmpegProcess?.Id;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(10));
        _ffmpegProcess?.Dispose();
        _ffmpegProcess = null;
    }

    public event Action<string>? OnLogLineReceived;

    /// <summary>
    ///     Launches the FFmpeg encoding process with configured command-line arguments.
    /// </summary>
    public void Start(FfmpegEncodingOptions options, string ffmpegExecutable = "ffmpeg")
    {
        string arguments = FfmpegArgumentBuilder.BuildArguments(options);
        _logger.LogInformation("[FFmpegController] Launching FFmpeg encoder: {Exe} {Args}", ffmpegExecutable,
            arguments);

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputFilePath));
        if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegExecutable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("[FFmpeg Stderr] {Line}", e.Data);
                OnLogLineReceived?.Invoke(e.Data);
            }
        };

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("[FFmpeg Stdout] {Line}", e.Data);
                OnLogLineReceived?.Invoke(e.Data);
            }
        };

        process.Exited += (sender, e) =>
        {
            int exitCode = -1;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FFmpegController] Could not read FFmpeg exit code.");
            }

            _logger.LogInformation("[FFmpegController] FFmpeg encoder exited with code {ExitCode}.", exitCode);
            _exitTcs.TrySetResult(exitCode);
        };

        if (!process.Start())
        {
            _logger.LogError("[FFmpegController] Failed to start FFmpeg process.");
            throw new InvalidOperationException($"Failed to launch FFmpeg process: {ffmpegExecutable}");
        }

        // Sandbox FFmpeg in Job Object if available
        _jobSandbox?.AssignProcess(process);

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        _ffmpegProcess = process;
        _logger.LogInformation("[FFmpegController] FFmpeg encoder successfully started under PID {PID}.", process.Id);
    }

    /// <summary>
    ///     Waits for FFmpeg to finish encoding and finalize the MP4 container (writing faststart moov atom).
    ///     If FFmpeg does not exit within the timeout, terminates the process.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_ffmpegProcess == null || _ffmpegProcess.HasExited) return;

        _logger.LogInformation(
            "[FFmpegController] Awaiting FFmpeg buffer flush and MP4 faststart finalization (PID: {PID})...",
            _ffmpegProcess.Id);

        try
        {
            using CancellationTokenSource cts = new(timeout);
            await _ffmpegProcess.WaitForExitAsync(cts.Token);
            _logger.LogInformation("[FFmpegController] FFmpeg process finalized cleanly.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[FFmpegController] FFmpeg finalization timed out after {Timeout}s. Forcing process kill...",
                timeout.TotalSeconds);
            try
            {
                _ffmpegProcess.Kill();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FFmpegController] Error killing FFmpeg process: {Message}", ex.Message);
            }
        }
    }
}
