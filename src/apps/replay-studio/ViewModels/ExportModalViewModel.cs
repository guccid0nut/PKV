// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ExportModalViewModel.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Export;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;

namespace ReplayStudio.GUI.ViewModels;

/// <summary>
///     ViewModel managing the video and audio export dialog, resolution presets,
///     timeline trim ranges, and FFmpeg encoding progress.
/// </summary>
public partial class ExportModalViewModel : ObservableObject
{
    private readonly ILogger<ExportModalViewModel> _logger;
    private readonly VideoExportPipeline _pipeline;

    private ReplayStudioService? _service;
    private CameraDirector? _cameraDirector;
    private LightingController? _lightingController;
    private CancellationTokenSource? _exportCts;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _outputFilePath = string.Empty;
    [ObservableProperty] private string _selectedPreset = "1080p60 Ultra (NVENC GPU)";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackendModeDescription))]
    private ReplayExportBackendMode _selectedBackendMode = ReplayExportBackendMode.SoftwarePreview;
    [ObservableProperty] private string _replayFilePath = string.Empty;
    [ObservableProperty] private string _gameDirectory = string.Empty;
    [ObservableProperty] private string _ffmpegExecutablePath = "ffmpeg";
    [ObservableProperty] private string _ffprobeExecutablePath = "ffprobe";
    [ObservableProperty] private int _povClientIndex;
    [ObservableProperty] private bool _enableCleanCinematicUi;
    [ObservableProperty] private ExportResult? _lastExportResult;
    [ObservableProperty] private int _inMarkerMs;
    [ObservableProperty] private int _outMarkerMs;
    [ObservableProperty] private string _inMarkerFormatted = "00:00.000";
    [ObservableProperty] private string _outMarkerFormatted = "00:00.000";
    [ObservableProperty] private bool _includeAudio = true;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgressPercent;
    [ObservableProperty] private string _exportStatusText = "Ready to export.";
    [ObservableProperty] private string _progressDetailsText = string.Empty;
    [ObservableProperty] private double _currentFps;
    [ObservableProperty] private string _etaFormatted = "00:00";
    [ObservableProperty] private int _renderedFrames;
    [ObservableProperty] private int _totalFrames;

    public ReplayExportBackendMode[] AvailableBackendModes { get; } =
    [
        ReplayExportBackendMode.EngineHeadless,
        ReplayExportBackendMode.SoftwarePreview
    ];

    public string BackendModeDescription => SelectedBackendMode == ReplayExportBackendMode.EngineHeadless
        ? "FUTURE DEV / EXPERIMENTAL (Not supported in 5.0.2): Offscreen hon_x64.exe engine capture -> FFmpeg -> ffprobe verification"
        : "FUTURE DEV / EXPERIMENTAL: Software procedural preview renderer (diagnostics only)";

    public string[] AvailablePresets { get; } =
    [
        "1080p60 Ultra (NVENC GPU)",
        "4K60 RTX (NVENC HEVC)",
        "720p60 Fast (CPU H.264)"
    ];

    public ExportModalViewModel(
        VideoExportPipeline? pipeline = null,
        ILogger<ExportModalViewModel>? logger = null)
    {
        _pipeline = pipeline ?? new VideoExportPipeline();
        _logger = logger ?? NullLogger<ExportModalViewModel>.Instance;

        string defaultExportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReplayStudio");
        OutputFilePath = Path.Combine(defaultExportDir, "hon_match_export.mp4");
    }

    /// <summary>
    ///     Binds replay studio dependencies required for full 3D viewport rendering during export.
    /// </summary>
    public void BindDependencies(
        ReplayStudioService service,
        CameraDirector cameraDirector,
        LightingController lightingController)
    {
        _service = service;
        _cameraDirector = cameraDirector;
        _lightingController = lightingController;
        _logger.LogInformation("[ExportModalViewModel] Bound export pipeline dependencies");
    }

    public void ConfigureMarkers(int inMs, int outMs)
    {
        InMarkerMs = inMs;
        OutMarkerMs = outMs > inMs ? outMs : inMs + 10000;
        InMarkerFormatted = TimelineViewModel.FormatTimestamp(InMarkerMs, true);
        OutMarkerFormatted = TimelineViewModel.FormatTimestamp(OutMarkerMs, true);
    }

    [RelayCommand]
    public void OpenModal()
    {
        IsVisible = true;
        ExportStatusText = "Ready to export.";
        ProgressDetailsText = string.Empty;
        ExportProgressPercent = 0.0;
        _logger.LogInformation("[ExportModalViewModel] Export dialog opened (In={In}, Out={Out})", InMarkerFormatted, OutMarkerFormatted);
    }

    [RelayCommand]
    public void CloseModal()
    {
        if (IsExporting)
        {
            CancelExport();
        }
        IsVisible = false;
        _logger.LogInformation("[ExportModalViewModel] Export dialog closed");
    }

    [RelayCommand]
    public async Task StartExportAsync()
    {
        if (IsExporting) return;

        IsExporting = true;
        LastExportResult = null;
        ExportProgressPercent = 0.0;
        ExportStatusText = SelectedBackendMode == ReplayExportBackendMode.EngineHeadless
            ? "Initializing offscreen hon_x64.exe replay export pipeline..."
            : "Initializing software preview export pipeline...";
        ProgressDetailsText = SelectedBackendMode == ReplayExportBackendMode.EngineHeadless
            ? "Validating .honreplay archive, hon_x64.exe, and zero-window-exposure settings..."
            : "Preparing render staging buffers...";
        _exportCts = new CancellationTokenSource();

        ExportPreset preset = ExportPreset.FromDisplayName(SelectedPreset);

        string targetPath = OutputFilePath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ReplayStudio");
            targetPath = Path.Combine(defaultDir, $"hon_export_{TimeProvider.System.GetLocalNow():yyyyMMdd_HHmmss}.mp4");
            OutputFilePath = targetPath;
        }

        int startMs = InMarkerMs;
        int endMs = OutMarkerMs > InMarkerMs ? OutMarkerMs : InMarkerMs + 10000;
        string? effectiveReplayPath = !string.IsNullOrWhiteSpace(ReplayFilePath)
            ? ReplayFilePath
            : _service?.CurrentReplayFilePath;
        string? effectiveGameDirectory = !string.IsNullOrWhiteSpace(GameDirectory)
            ? GameDirectory
            : null;

        ExportParameters parameters = new()
        {
            BackendMode = SelectedBackendMode,
            Preset = preset,
            OutputFilePath = targetPath,
            ReplayFilePath = effectiveReplayPath,
            GameDirectory = effectiveGameDirectory,
            InMarkerMs = startMs,
            OutMarkerMs = endMs,
            IncludeAudio = IncludeAudio,
            FfmpegExecutablePath = FfmpegExecutablePath,
            FfprobeExecutablePath = FfprobeExecutablePath,
            PovClientIndex = PovClientIndex,
            EnableCleanCinematicUi = EnableCleanCinematicUi,
            AllowForegroundActivation = false
        };

        _logger.LogInformation(
            "[ExportModalViewModel] Starting export job: Backend={Backend}, Preset='{Preset}', Replay='{Replay}', Out='{Path}', Range=[{In}ms .. {Out}ms]",
            SelectedBackendMode, preset.DisplayName, effectiveReplayPath ?? "<none>", targetPath, startMs, endMs);

        Progress<ExportProgress> progressReporter = new(p =>
        {
            ExportProgressPercent = p.PercentComplete;
            RenderedFrames = p.RenderedFrames;
            TotalFrames = p.TotalFrames;
            CurrentFps = p.CurrentFps;
            EtaFormatted = p.FormattedEta;
            ExportStatusText = p.StatusText;
            ProgressDetailsText = $"Frames: {p.RenderedFrames}/{p.TotalFrames} • {p.CurrentFps:F1} FPS • ETA: {p.FormattedEta}";
        });

        try
        {
            ExportResult result;

            if (SelectedBackendMode == ReplayExportBackendMode.EngineHeadless)
            {
                result = await _pipeline.ExportVideoAsync(
                    parameters,
                    renderFrameCallback: null,
                    combatEvents: null,
                    progress: progressReporter,
                    ct: _exportCts.Token);
            }
            else if (_service != null && _cameraDirector != null && _lightingController != null)
            {
                result = await _pipeline.ExportReplayAsync(
                    parameters,
                    _service.SnapshotEngine,
                    _cameraDirector,
                    _lightingController,
                    _service.MapManager,
                    combatEvents: null,
                    progress: progressReporter,
                    ct: _exportCts.Token);
            }
            else
            {
                result = await _pipeline.ExportVideoAsync(
                    parameters,
                    renderFrameCallback: null,
                    combatEvents: null,
                    progress: progressReporter,
                    ct: _exportCts.Token);
            }

            LastExportResult = result;

            if (result.Success)
            {
                ExportProgressPercent = 100.0;
                if (result.VerificationResult != null && result.VerificationResult.IsValid)
                {
                    ExportStatusText =
                        $"Export completed & ffprobe verified! ({result.VerificationResult.CodecName}, {result.VerificationResult.Width}x{result.VerificationResult.Height}, {result.TotalFramesRendered} frames)";
                    ProgressDetailsText =
                        $"Saved to: {Path.GetFileName(result.OutputFilePath)} ({result.OutputSizeMegabytes:F2} MB, {result.VerificationResult.DurationSeconds:F2}s)";
                }
                else
                {
                    ExportStatusText = $"Export completed! ({result.TotalFramesRendered} frames, {result.AverageFps:F1} avg FPS)";
                    ProgressDetailsText = $"Saved to: {Path.GetFileName(result.OutputFilePath)} ({result.OutputSizeMegabytes:F1} MB)";
                }
                _logger.LogInformation(
                    "[ExportModalViewModel] Export completed successfully (Backend={Backend}): '{Path}' ({Verification})",
                    result.BackendMode, result.OutputFilePath, result.VerificationResult?.Summary ?? "unprobed");
            }
            else
            {
                ExportProgressPercent = 0.0;
                ExportStatusText = $"Export failed: {result.ErrorMessage}";
                ProgressDetailsText = result.VerificationResult?.FailureReason ?? "Export failed without creating a verified MP4.";
                _logger.LogError("[ExportModalViewModel] Export reported failure (Backend={Backend}): {Error}",
                    result.BackendMode, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = "Export cancelled by user.";
            ProgressDetailsText = "Export aborted.";
            _logger.LogInformation("[ExportModalViewModel] Export cancelled by user");
        }
        catch (Exception ex)
        {
            ExportStatusText = $"Export error: {ex.Message}";
            ProgressDetailsText = "Pipeline exception occurred.";
            _logger.LogError(ex, "[ExportModalViewModel] Export exception");
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    public void CancelExport()
    {
        if (_exportCts != null && !_exportCts.IsCancellationRequested)
        {
            _logger.LogInformation("[ExportModalViewModel] User requested export cancellation");
            _exportCts.Cancel();
            ExportStatusText = "Cancelling export...";
            ProgressDetailsText = "Flushing and stopping FFmpeg...";
        }
    }
}
