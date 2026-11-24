// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TimelineViewModel.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.ViewModels;

/// <summary>
///     ViewModel managing timeline scrubbing, playback state, speed multipliers,
///     In/Out trim markers, and combat event telemetry graph data.
/// </summary>
public partial class TimelineViewModel : ObservableObject
{
    private readonly ILogger<TimelineViewModel> _logger;
    private readonly PlaybackClock _clock;
    private readonly ReplaySnapshotEngine _snapshotEngine;

    [ObservableProperty] private int _currentTimeMs;
    [ObservableProperty] private int _totalDurationMs;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private float _playbackSpeed = 1.0f;
    [ObservableProperty] private int _inMarkerMs;
    [ObservableProperty] private int _outMarkerMs;
    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private int _netWorthAdvantage;
    [ObservableProperty] private string _currentTimeFormatted = "00:00.000";
    [ObservableProperty] private string _totalDurationFormatted = "00:00";
    [ObservableProperty] private string _inMarkerFormatted = "00:00.000";
    [ObservableProperty] private string _outMarkerFormatted = "00:00.000";
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _statusText = "Ready";

    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    public float[] AvailableSpeeds => PlaybackClock.ValidSpeeds;
    public ObservableCollection<CombatEventMarker> CombatEvents { get; } = [];

    public event Action<int>? SeekRequested;
    public event Action? ExportRequested;

    public TimelineViewModel(PlaybackClock clock, ReplaySnapshotEngine snapshotEngine, ILogger<TimelineViewModel>? logger = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _snapshotEngine = snapshotEngine ?? throw new ArgumentNullException(nameof(snapshotEngine));
        _logger = logger ?? NullLogger<TimelineViewModel>.Instance;

        _clock.TimeUpdated += OnClockTimeUpdated;
        _clock.StateChanged += OnClockStateChanged;
        _clock.SpeedChanged += OnClockSpeedChanged;

        SyncWithClock();
    }

    private void SyncWithClock()
    {
        TotalDurationMs = _clock.TotalDurationMs;
        CurrentTimeMs = _clock.CurrentTimeMs;
        InMarkerMs = _clock.InMarkerMs;
        OutMarkerMs = _clock.OutMarkerMs > 0 ? _clock.OutMarkerMs : _clock.TotalDurationMs;
        IsLooping = _clock.LoopPlayback;
        PlaybackSpeed = _clock.SpeedMultiplier;
        IsPlaying = _clock.IsPlaying;

        UpdateFormattedStrings();
    }

    public void SetMatchDuration(int durationMs, IEnumerable<CombatEventMarker>? events = null)
    {
        TotalDurationMs = durationMs;
        _clock.TotalDurationMs = durationMs;
        _clock.InMarkerMs = 0;
        _clock.OutMarkerMs = durationMs;
        InMarkerMs = 0;
        OutMarkerMs = durationMs;

        CombatEvents.Clear();
        if (events != null)
        {
            foreach (CombatEventMarker ev in events)
            {
                CombatEvents.Add(ev);
            }
        }

        UpdateFormattedStrings();
        _logger.LogInformation("[TimelineViewModel] Configured match duration {DurationMs}ms with {Count} combat events", durationMs, CombatEvents.Count);
    }

    private void OnClockTimeUpdated(int timeMs)
    {
        CurrentTimeMs = timeMs;
        ProgressFraction = TotalDurationMs > 0 ? Math.Clamp((double)timeMs / TotalDurationMs, 0.0, 1.0) : 0.0;
        CurrentTimeFormatted = FormatTimestamp(timeMs, true);

        // Update net worth advantage from snapshot
        GameStateSnapshot snap = _snapshotEngine.SeekNearestKeyframe(timeMs);
        NetWorthAdvantage = snap.NetWorthAdvantage;

        SeekRequested?.Invoke(timeMs);
    }

    private void OnClockStateChanged(PlaybackState state)
    {
        IsPlaying = (state == PlaybackState.Playing);
        OnPropertyChanged(nameof(PlayPauseIcon));
        StatusText = state switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Stopped => "Stopped",
            _ => "Ready"
        };
    }

    private void OnClockSpeedChanged(float speed)
    {
        PlaybackSpeed = speed;
    }

    private void UpdateFormattedStrings()
    {
        CurrentTimeFormatted = FormatTimestamp(CurrentTimeMs, true);
        TotalDurationFormatted = FormatTimestamp(TotalDurationMs, false);
        InMarkerFormatted = FormatTimestamp(InMarkerMs, true);
        OutMarkerFormatted = FormatTimestamp(OutMarkerMs, true);
        ProgressFraction = TotalDurationMs > 0 ? Math.Clamp((double)CurrentTimeMs / TotalDurationMs, 0.0, 1.0) : 0.0;
    }

    public static string FormatTimestamp(int ms, bool includeMillis = true)
    {
        TimeSpan ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (includeMillis)
        {
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
        }
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        _clock.TogglePlayPause();
    }

    [RelayCommand]
    public void Play()
    {
        _clock.Play();
    }

    [RelayCommand]
    public void Pause()
    {
        _clock.Pause();
    }

    [RelayCommand]
    public void StepForward()
    {
        _clock.StepTick(1); // +50ms
    }

    [RelayCommand]
    public void StepBackward()
    {
        _clock.StepTick(-1); // -50ms
    }

    [RelayCommand]
    public void SetSpeed(float speed)
    {
        _clock.SpeedMultiplier = speed;
        PlaybackSpeed = speed;
    }

    [RelayCommand] public void SetSpeed025() => SetSpeed(0.25f);
    [RelayCommand] public void SetSpeed05() => SetSpeed(0.5f);
    [RelayCommand] public void SetSpeed1() => SetSpeed(1.0f);
    [RelayCommand] public void SetSpeed2() => SetSpeed(2.0f);
    [RelayCommand] public void SetSpeed4() => SetSpeed(4.0f);
    [RelayCommand] public void SetSpeed8() => SetSpeed(8.0f);

    [RelayCommand]
    public void Seek(int timestampMs)
    {
        _clock.CurrentTimeMs = timestampMs;
    }

    [RelayCommand]
    public void SeekFraction(double fraction)
    {
        int target = (int)(Math.Clamp(fraction, 0.0, 1.0) * TotalDurationMs);
        _clock.CurrentTimeMs = target;
    }

    [RelayCommand]
    public void SetInMarker()
    {
        int current = _clock.CurrentTimeMs;
        if (OutMarkerMs > 0 && current > OutMarkerMs)
        {
            current = OutMarkerMs;
        }
        _clock.InMarkerMs = current;
        InMarkerMs = current;
        InMarkerFormatted = FormatTimestamp(InMarkerMs, true);
        _logger.LogInformation("[TimelineViewModel] In-Marker set to {Time}", InMarkerFormatted);
    }

    [RelayCommand]
    public void SetOutMarker()
    {
        int current = _clock.CurrentTimeMs;
        if (current < InMarkerMs)
        {
            current = InMarkerMs;
        }
        _clock.OutMarkerMs = current;
        OutMarkerMs = current;
        OutMarkerFormatted = FormatTimestamp(OutMarkerMs, true);
        _logger.LogInformation("[TimelineViewModel] Out-Marker set to {Time}", OutMarkerFormatted);
    }

    [RelayCommand]
    public void ResetMarkers()
    {
        _clock.InMarkerMs = 0;
        _clock.OutMarkerMs = TotalDurationMs;
        InMarkerMs = 0;
        OutMarkerMs = TotalDurationMs;
        InMarkerFormatted = FormatTimestamp(0, true);
        OutMarkerFormatted = FormatTimestamp(TotalDurationMs, true);
        _logger.LogInformation("[TimelineViewModel] Markers reset to 0 -> {Total}ms", TotalDurationMs);
    }

    [RelayCommand]
    public void ToggleLoop()
    {
        _clock.LoopPlayback = !_clock.LoopPlayback;
        IsLooping = _clock.LoopPlayback;
        _logger.LogInformation("[TimelineViewModel] Loop mode set to {Loop}", IsLooping);
    }

    [RelayCommand]
    public void OpenExportModal()
    {
        ExportRequested?.Invoke();
    }
}
