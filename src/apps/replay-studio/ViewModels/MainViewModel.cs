// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MainViewModel.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;

namespace ReplayStudio.GUI.ViewModels;

/// <summary>
///     Root orchestrator ViewModel coordinating match session ingestion,
///     roster telemetry, timeline scrubbing, 3D viewport rendering, and video export.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly ReplayStudioService _service;
    private readonly PlaybackClock _clock;
    private readonly CameraDirector _cameraDirector;
    private readonly LightingController _lightingController;

    public ReplayStudioService Service => _service;
    public PlaybackClock Clock => _clock;
    public CameraDirector CameraDirector => _cameraDirector;
    public LightingController LightingController => _lightingController;

    public RosterViewModel RosterVM { get; }
    public TimelineViewModel TimelineVM { get; }
    public ViewportViewModel ViewportVM { get; }
    public ExportModalViewModel ExportVM { get; }

    [ObservableProperty] private bool _isReplayLoaded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _matchId = "#00000000";
    [ObservableProperty] private string _gameName = "HoN Replay Studio";
    [ObservableProperty] private string _mapName = "Forests of Caldavar";
    [ObservableProperty] private string _gameMode = "Normal / AP";
    [ObservableProperty] private string _matchDurationFormatted = "00:00";
    [ObservableProperty] private string _winnerTeamName = "Legion";
    [ObservableProperty] private bool _isLegionWinner = true;
    [ObservableProperty] private bool _isHellbourneWinner;
    [ObservableProperty] private int _scoreLegion;
    [ObservableProperty] private int _scoreHellbourne;
    [ObservableProperty] private double _fps = 60.0;
    [ObservableProperty] private double _renderTimeMs = 2.1;
    [ObservableProperty] private string _statusMessage = "Ready";

    public string FpsReadout => $"{Fps:F1} FPS";
    public string RenderTimeReadout => $"{RenderTimeMs:F1} ms";

    public MainViewModel(
        ReplayStudioService service,
        PlaybackClock clock,
        CameraDirector cameraDirector,
        LightingController lightingController,
        ILogger<MainViewModel>? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cameraDirector = cameraDirector ?? throw new ArgumentNullException(nameof(cameraDirector));
        _lightingController = lightingController ?? throw new ArgumentNullException(nameof(lightingController));
        _logger = logger ?? NullLogger<MainViewModel>.Instance;

        RosterVM = new RosterViewModel();
        TimelineVM = new TimelineViewModel(_clock, _service.SnapshotEngine);
        ViewportVM = new ViewportViewModel(_cameraDirector, _lightingController);
        ExportVM = new ExportModalViewModel();
        ExportVM.BindDependencies(_service, _cameraDirector, _lightingController);

        // Wire event handlers
        RosterVM.PovLockChanged += OnPovLockChanged;
        TimelineVM.SeekRequested += OnTimelineSeek;
        TimelineVM.ExportRequested += OnExportRequested;
        _service.ReplayLoaded += OnReplayLoadedFromService;

        _logger.LogInformation("[MainViewModel] Initialized Replay Studio MainViewModel");
    }

    private void OnPovLockChanged(int slot)
    {
        ViewportVM.FocusHero(slot);
        if (slot >= 0 && slot < RosterVM.Players.Count)
        {
            ViewportVM.LockedHeroName = $"{RosterVM.Players[slot].PlayerName} ({RosterVM.Players[slot].CleanHeroName})";
        }
        else
        {
            ViewportVM.LockedHeroName = "None";
        }
    }

    private void OnTimelineSeek(int timeMs)
    {
        GameStateSnapshot snapshot = _service.SnapshotEngine.GetInterpolatedSnapshot(timeMs);
        RosterVM.UpdateFromSnapshot(snapshot);
        ScoreLegion = snapshot.ScoreLegion;
        ScoreHellbourne = snapshot.ScoreHellbourne;

        // Update camera tracking
        Vector3 heroPos = Vector3.Zero;
        float heroHeading = 0f;

        if (ViewportVM.IsPovLocked && ViewportVM.LockedHeroSlot >= 0 && ViewportVM.LockedHeroSlot < 10)
        {
            HeroSnapshot hero = snapshot.Heroes[ViewportVM.LockedHeroSlot];
            if (hero != null)
            {
                heroPos = hero.Position;
                heroHeading = hero.HeadingAngle;
            }
        }

        CameraState camState = _cameraDirector.Update(0.016f, heroPos, heroHeading, timeMs, 16.0f / 9.0f);
        ViewportVM.UpdateReadouts(camState.Position, camState.Target, camState.FovDegrees, heroHeading, _cameraDirector.FreePitchDeg);

        // Update live render stats
        Fps = 60.0;
        RenderTimeMs = 2.1;
        OnPropertyChanged(nameof(FpsReadout));
        OnPropertyChanged(nameof(RenderTimeReadout));
    }

    private void OnExportRequested()
    {
        ExportVM.ConfigureMarkers(TimelineVM.InMarkerMs, TimelineVM.OutMarkerMs);
        ExportVM.OpenModal();
    }

    private void OnReplayLoadedFromService(ReplayMetadata metadata)
    {
        OnMetadataLoaded(metadata);
    }

    public void OnMetadataLoaded(ReplayMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        MatchId = $"#{metadata.MatchId}";
        GameName = string.IsNullOrWhiteSpace(metadata.GameName) ? "HoN Match" : metadata.GameName;
        MapName = string.IsNullOrWhiteSpace(metadata.MapName) ? "Forests of Caldavar" : metadata.MapName;
        GameMode = string.IsNullOrWhiteSpace(metadata.GameMode) ? "Normal / AP" : metadata.GameMode;
        WinnerTeamName = metadata.WinnerTeamName;
        IsLegionWinner = string.Equals(WinnerTeamName, "Legion", StringComparison.OrdinalIgnoreCase);
        IsHellbourneWinner = !IsLegionWinner;

        int durationMs = (int)Math.Max(1000, metadata.MatchLengthMs);
        MatchDurationFormatted = TimelineViewModel.FormatTimestamp(durationMs, false);

        List<CombatEventMarker> combatEvents = [];
        if (metadata.FirstBloodTimeMs > 0)
        {
            combatEvents.Add(new CombatEventMarker
            {
                TimestampMs = (int)metadata.FirstBloodTimeMs,
                EventType = CombatEventType.FirstBlood,
                Description = "First Blood",
                IntensityScore = 0.9f
            });
        }

        TimelineVM.SetMatchDuration(durationMs, combatEvents);
        RosterVM.PopulateRoster(_service.Roster);

        IsReplayLoaded = true;
        StatusMessage = $"Match #{metadata.MatchId} loaded successfully ({MapName})";
        _logger.LogInformation("[MainViewModel] Match metadata applied to UI state");
    }

    [RelayCommand]
    public async Task LoadReplayFileAsync(string replayFilePath)
    {
        if (string.IsNullOrWhiteSpace(replayFilePath)) return;

        IsLoading = true;
        StatusMessage = $"Loading {Path.GetFileName(replayFilePath)}...";
        _logger.LogInformation("[MainViewModel] Loading replay file '{Path}'", replayFilePath);

        try
        {
            bool success = await _service.LoadReplayAsync(replayFilePath);
            if (success && _service.CurrentMetadata != null)
            {
                OnMetadataLoaded(_service.CurrentMetadata);
            }
            else
            {
                StatusMessage = "Failed to parse replay file.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MainViewModel] Exception while loading replay file");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task OpenReplayFileDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.StorageProvider: { } storage })
        {
            string currentDir = Directory.GetCurrentDirectory();
            string[] candidates =
            [
                Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "game", "replays"),
                Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "replays"),
                Path.Combine(currentDir, "games", "client", "userdata", "replays"),
                Path.Combine(currentDir, "games", "client", "userdata", "game", "replays"),
                Path.Combine(currentDir, "games", "client", "replays"),
                Path.Combine(currentDir, "games", "client", "game", "replays"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Heroes of Newerth x64", "game", "replays"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "Heroes of Newerth x64", "game", "replays")
            ];

            IStorageFolder? startFolder = null;
            foreach (string cand in candidates)
            {
                if (Directory.Exists(cand))
                {
                    startFolder = await storage.TryGetFolderFromPathAsync(cand);
                    break;
                }
            }

            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open HoN Replay Archive",
                SuggestedStartLocation = startFolder,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("HoN Replay Archives (*.honreplay;*.zip)")
                    {
                        Patterns = ["*.honreplay", "*.zip"]
                    },
                    new FilePickerFileType("All Files (*.*)")
                    {
                        Patterns = ["*.*"]
                    }
                ]
            });

            if (files.Count > 0)
            {
                await LoadReplayFileAsync(files[0].Path.LocalPath);
            }
        }
    }

    [RelayCommand]
    public void LoadDemoMatch()
    {
        _logger.LogInformation("[MainViewModel] Generating synthetic demo match for UI demonstration");
        ReplayMetadata demo = CreateDemoMetadata();
        _service.LoadMetadata(demo);
        OnMetadataLoaded(demo);
    }

    [RelayCommand]
    public void ResetCamera()
    {
        ViewportVM.ResetCamera();
    }

    [RelayCommand]
    public void ToggleExportModal()
    {
        ExportVM.IsVisible = !ExportVM.IsVisible;
    }

    /// <summary>
    ///     Creates a rich synthetic ReplayMetadata match for UI preview and test harnesses.
    /// </summary>
    public static ReplayMetadata CreateDemoMetadata()
    {
        ReplayMetadata meta = new()
        {
            MatchId = "135892401",
            GameName = "HoN Championship Grand Finals - Game 5",
            MapName = "caldavar",
            GameMode = "All Pick (AP)",
            MatchLengthMs = 42 * 60 * 1000 + 15 * 1000, // 42:15
            Winner = "Legion",
            FirstBloodTimeMs = 145000,
            Version = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion
        };

        // 5 Legion players
        string[] legionHeroes = ["Hero_Devourer", "Hero_Valkyrie", "Hero_WitchSlayer", "Hero_Magmus", "Hero_Behemoth"];
        string[] legionPlayers = ["Testo", "MoonMeander", "AngryTestie", "Chu", "bkid"];

        for (int i = 0; i < 5; i++)
        {
            meta.Players.Add(new ReplayPlayerMetadata
            {
                ClientNum = i,
                Team = 1,
                TeamIndex = i,
                Name = legionPlayers[i],
                HeroName = legionHeroes[i],
                Level = 22 - i * 2,
                Kills = 12 - i * 2,
                Deaths = 3 + i,
                Assists = 14 - i,
                Gold = 18500 - i * 2000,
                GoldPerMinute = 540 - i * 40,
                ExpPerMinute = 620 - i * 45
            });
        }

        // 5 Hellbourne players
        string[] hellbourneHeroes = ["Hero_Pyromancer", "Hero_Soulstealer", "Hero_Chronos", "Hero_Glacius", "Hero_PollywogPriest"];
        string[] hellbournePlayers = ["N0tail", "Era", "Fly", "H4nn1", "Trixi"];

        for (int i = 0; i < 5; i++)
        {
            meta.Players.Add(new ReplayPlayerMetadata
            {
                ClientNum = 5 + i,
                Team = 2,
                TeamIndex = i,
                Name = hellbournePlayers[i],
                HeroName = hellbourneHeroes[i],
                Level = 21 - i * 2,
                Kills = 8 - i,
                Deaths = 7 + i,
                Assists = 10 - i,
                Gold = 15000 - i * 1800,
                GoldPerMinute = 480 - i * 35,
                ExpPerMinute = 560 - i * 40
            });
        }

        return meta;
    }
}
