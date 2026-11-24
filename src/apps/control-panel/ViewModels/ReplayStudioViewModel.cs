// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.Contracts.Replay;
using NEXUS.ControlPanel.GUI.Services;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class ReplayStudioViewModel : ViewModelBase
{
    private readonly ILocalReplayParserService _parserService;
    private readonly IReplayRenderManagerService _renderManager;
    [ObservableProperty] private string _activeElapsedDisplay = "00:00";
    [ObservableProperty] private string _activeFpsDisplay = "60.0 FPS";
    [ObservableProperty] private ReplayRenderJobItemDto? _activeJob;
    [ObservableProperty] private double _activeProgressPercent;
    [ObservableProperty] private string _activeRemainingDisplay = "--:--";

    private ParsedReplayMetadata? _currentMetadata;
    [ObservableProperty] private string _durationDisplay = "34:12";
    [ObservableProperty] private string _fileSizeDisplay = string.Empty;
    [ObservableProperty] private string _gameDate = string.Empty;
    [ObservableProperty] private string _gameMode = "All Pick";

    [ObservableProperty] private bool _isProcessingActive;

    [ObservableProperty] private bool _isReplayLoaded;
    [ObservableProperty] private string _loadedFileName = string.Empty;
    [ObservableProperty] private string _loadedFilePath = string.Empty;
    [ObservableProperty] private string _mapName = "Forests of Caldavar";
    [ObservableProperty] private int _matchId;
    [ObservableProperty] private string _outputDirectory = string.Empty;
    [ObservableProperty] private string _selectedCameraMode = "PlayerPOV";

    [ObservableProperty] private ReplayDropdownItemViewModel? _selectedDiscoveredReplay;
    [ObservableProperty] private int _selectedFramerate = 60;

    [ObservableProperty] private ReplayPlayerCardViewModel? _selectedPlayer;
    [ObservableProperty] private string _selectedResolution = "1080p";
    [ObservableProperty] private string _serverName = "Project KONGOR Match Server #1";
    [ObservableProperty] private bool _showHud = true;
    [ObservableProperty] private string _winnerColorHex = "#00F0FF";
    [ObservableProperty] private string _winnerDisplay = "Legion Victory";

    public ReplayStudioViewModel(
        ILocalReplayParserService? parserService = null,
        IReplayRenderManagerService? renderManager = null)
    {
        _parserService = parserService ?? new LocalReplayParserService();
        _renderManager = renderManager ?? new ReplayRenderManagerService();

        OutputDirectory = _renderManager.GetDefaultOutputDirectory();

        _renderManager.QueueChanged += OnQueueChanged;
        _renderManager.JobProgressUpdated += OnJobProgressUpdated;

        ScanDiscoveredReplays();
        RefreshQueueState();
    }

    public ObservableCollection<ReplayDropdownItemViewModel> DiscoveredReplays { get; } = [];
    public ObservableCollection<ReplayPlayerCardViewModel> LegionPlayers { get; } = [];
    public ObservableCollection<ReplayPlayerCardViewModel> HellbournePlayers { get; } = [];
    public ObservableCollection<ReplayRenderJobItemDto> QueuedJobs { get; } = [];
    public ObservableCollection<ReplayRenderJobItemDto> CompletedJobs { get; } = [];

    public string[] CameraModes { get; } = ["PlayerPOV", "ActionCam", "FreeCam", "Overview"];
    public string[] Resolutions { get; } = ["1080p", "1440p", "4k"];
    public int[] Framerates { get; } = [60, 120];

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshQueueState);
    }

    private void OnJobProgressUpdated(object? sender, ReplayRenderJobItemDto job)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ActiveJob?.JobId == job.JobId)
            {
                ActiveProgressPercent = job.ProgressPercent;
                ActiveFpsDisplay = $"{job.Fps:F1} FPS";
                ActiveElapsedDisplay = TimeSpan.FromSeconds(job.ElapsedSeconds).ToString(@"mm\:ss");
                ActiveRemainingDisplay = TimeSpan.FromSeconds(job.EstimatedRemainingSeconds).ToString(@"mm\:ss");
            }
        });
    }

    private void RefreshQueueState()
    {
        ActiveJob = _renderManager.ActiveJob;
        IsProcessingActive = _renderManager.IsProcessing;

        if (ActiveJob != null)
        {
            ActiveProgressPercent = ActiveJob.ProgressPercent;
            ActiveFpsDisplay = $"{ActiveJob.Fps:F1} FPS";
            ActiveElapsedDisplay = TimeSpan.FromSeconds(ActiveJob.ElapsedSeconds).ToString(@"mm\:ss");
            ActiveRemainingDisplay = TimeSpan.FromSeconds(ActiveJob.EstimatedRemainingSeconds).ToString(@"mm\:ss");
        }

        QueuedJobs.Clear();
        foreach (ReplayRenderJobItemDto j in _renderManager.QueuedJobs) QueuedJobs.Add(j);

        CompletedJobs.Clear();
        foreach (ReplayRenderJobItemDto c in _renderManager.CompletedJobs) CompletedJobs.Add(c);
    }

    [RelayCommand]
    public void RefreshDiscoveredReplays()
    {
        ScanDiscoveredReplays();
    }

    private void ScanDiscoveredReplays()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Heroes of Newerth x64",
                "game", "replays"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents",
                "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(AppContext.BaseDirectory, "userdata", "game", "replays"),
            Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "userdata", "game", "replays"),
            Path.Combine(Directory.GetCurrentDirectory(), "games", "client", "game", "replays"),
            Path.Combine(Directory.GetCurrentDirectory(), "tests", "fixtures", "replays")
        ];

        List<string> foundFiles = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                string[] files =
                [
                    .. Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                ];

                foreach (string file in files)
                    if (seen.Add(Path.GetFileName(file)))
                        foundFiles.Add(file);
            }
            catch
            {
            }
        }

        DiscoveredReplays.Clear();
        List<ReplayDropdownItemViewModel> items =
        [
            .. foundFiles
                .Select(f => new ReplayDropdownItemViewModel(f))
                .OrderByDescending(i => i.LastModified)
        ];

        foreach (ReplayDropdownItemViewModel item in items) DiscoveredReplays.Add(item);

        if (DiscoveredReplays.Count > 0)
            SelectedDiscoveredReplay = DiscoveredReplays[0];
        else
            LoadSampleReplay();
    }

    partial void OnSelectedDiscoveredReplayChanged(ReplayDropdownItemViewModel? value)
    {
        if (value != null && File.Exists(value.FilePath)) _ = LoadReplayFileAsync(value.FilePath);
    }

    public async Task LoadReplayFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        ParsedReplayMetadata? meta = await _parserService.ParseReplayFileAsync(filePath);
        if (meta != null) ApplyMetadata(meta);
    }

    private void ApplyMetadata(ParsedReplayMetadata meta)
    {
        _currentMetadata = meta;
        IsReplayLoaded = true;
        LoadedFilePath = meta.FilePath;
        LoadedFileName = meta.FileName;
        FileSizeDisplay = $"{meta.FileSizeBytes / (1024.0 * 1024.0):F2} MB";
        MatchId = meta.MatchId;
        MapName = meta.MapName;
        DurationDisplay = meta.DurationDisplay;
        GameMode = meta.GameMode;
        GameDate = meta.GameDate;
        ServerName = meta.ServerName;
        WinnerDisplay = meta.WinnerDisplay;
        WinnerColorHex = meta.WinnerTeam == 1 ? "#00F0FF" : "#FF0055";

        LegionPlayers.Clear();
        foreach (ParsedReplayPlayer p in meta.LegionPlayers) LegionPlayers.Add(new ReplayPlayerCardViewModel(p));

        HellbournePlayers.Clear();
        foreach (ParsedReplayPlayer p in meta.HellbournePlayers)
            HellbournePlayers.Add(new ReplayPlayerCardViewModel(p));

        // Auto select first player
        if (LegionPlayers.Count > 0) SelectPlayer(LegionPlayers[0]);
    }

    private void LoadSampleReplay()
    {
        ParsedReplayMetadata sample = new()
        {
            MatchId = 15984210,
            FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Heroes of Newerth x64", "game", "replays", "M15984210.honreplay"),
            FileName = "M15984210.honreplay",
            FileSizeBytes = 14285712,
            MapName = "Forests of Caldavar",
            DurationSeconds = 2145,
            DurationDisplay = "35:45",
            GameMode = "All Pick",
            GameDate = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm"),
            WinnerTeam = 1,
            ServerName = "US-East High Performance Match Server",
            LegionPlayers =
            [
                new ParsedReplayPlayer
                {
                    AccountId = 1001, Name = "GucciHost", HeroName = "Hero_Empath", Level = 16, Kills = 4, Deaths = 2,
                    Assists = 19, Gold = 11250, Mmr = 1750, Team = 1
                },
                new ParsedReplayPlayer
                {
                    AccountId = 1002, Name = "ApexPredator", HeroName = "Hero_WitchSlayer", Level = 18, Kills = 11,
                    Deaths = 4, Assists = 12, Gold = 14300, Mmr = 1820, Team = 1
                },
                new ParsedReplayPlayer
                {
                    AccountId = 1003, Name = "ChronosKing", HeroName = "Hero_Chronos", Level = 20, Kills = 14,
                    Deaths = 3, Assists = 8, Gold = 21500, Mmr = 1890, Team = 1
                },
                new ParsedReplayPlayer
                {
                    AccountId = 1004, Name = "PyromancerGod", HeroName = "Hero_Pyromancer", Level = 17, Kills = 8,
                    Deaths = 6, Assists = 15, Gold = 13800, Mmr = 1710, Team = 1
                },
                new ParsedReplayPlayer
                {
                    AccountId = 1005, Name = "PebblesRock", HeroName = "Hero_Pebbles", Level = 17, Kills = 9,
                    Deaths = 5, Assists = 11, Gold = 12900, Mmr = 1680, Team = 1
                }
            ],
            HellbournePlayers =
            [
                new ParsedReplayPlayer
                {
                    AccountId = 2001, Name = "NightStalker", HeroName = "Hero_Devourer", Level = 18, Kills = 10,
                    Deaths = 7, Assists = 6, Gold = 13200, Mmr = 1780, Team = 2
                },
                new ParsedReplayPlayer
                {
                    AccountId = 2002, Name = "SoulStealer", HeroName = "Hero_Soulstealer", Level = 19, Kills = 7,
                    Deaths = 8, Assists = 5, Gold = 17500, Mmr = 1840, Team = 2
                },
                new ParsedReplayPlayer
                {
                    AccountId = 2003, Name = "SwiftbladeSlash", HeroName = "Hero_Swiftblade", Level = 19, Kills = 5,
                    Deaths = 9, Assists = 4, Gold = 16200, Mmr = 1760, Team = 2
                },
                new ParsedReplayPlayer
                {
                    AccountId = 2004, Name = "GlaciusChill", HeroName = "Hero_Glacius", Level = 14, Kills = 2,
                    Deaths = 12, Assists = 14, Gold = 7800, Mmr = 1650, Team = 2
                },
                new ParsedReplayPlayer
                {
                    AccountId = 2005, Name = "MagebaneMana", HeroName = "Hero_Magebane", Level = 17, Kills = 3,
                    Deaths = 10, Assists = 3, Gold = 15100, Mmr = 1720, Team = 2
                }
            ]
        };

        foreach (ParsedReplayPlayer p in sample.LegionPlayers.Concat(sample.HellbournePlayers))
        {
            p.HeroDisplayName = p.HeroName.Replace("Hero_", "");
            p.HeroIconUrl = $"/Assets/heroes/{p.HeroName.ToLowerInvariant()}/icon.png";
        }

        ApplyMetadata(sample);
    }

    [RelayCommand]
    public void SelectPlayer(ReplayPlayerCardViewModel? player)
    {
        if (player == null) return;

        foreach (ReplayPlayerCardViewModel p in LegionPlayers.Concat(HellbournePlayers)) p.IsSelected = false;

        player.IsSelected = true;
        SelectedPlayer = player;
    }

    [RelayCommand]
    public async Task EnqueueRenderJobAsync()
    {
        int accId = SelectedPlayer?.AccountId ?? 0;
        int pIndex = SelectedPlayer?.PlayerIndex ?? -1;
        string pName = SelectedPlayer?.Name ?? "Free Cam";

        ReplayRenderJobRequestDto request = new()
        {
            MatchId = MatchId,
            ReplayFilePath = LoadedFilePath,
            TargetAccountId = accId,
            TargetPlayerIndex = pIndex,
            TargetPlayerName = pName,
            CameraMode = SelectedCameraMode,
            Resolution = SelectedResolution,
            Framerate = SelectedFramerate,
            ShowHud = ShowHud,
            OutputPath = Path.Combine(OutputDirectory,
                $"Match_{MatchId}_{pName}_{SelectedResolution}_{SelectedFramerate}fps.mp4")
        };

        await _renderManager.EnqueueJobAsync(request, _currentMetadata);
    }

    [RelayCommand]
    public async Task CancelActiveJobAsync()
    {
        await _renderManager.CancelActiveJobAsync();
    }

    [RelayCommand]
    public void RemoveQueuedJob(string? jobId)
    {
        if (!string.IsNullOrWhiteSpace(jobId)) _renderManager.RemoveQueuedJob(jobId);
    }

    private static bool IsTestEnvironment()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTHOST_PID")) ||
               AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                   a.GetName().Name?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true ||
                   a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);
    }

    [RelayCommand]
    public void OpenVideoFile(string? path)
    {
        if (IsTestEnvironment()) return;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
            }
    }

    [RelayCommand]
    public void OpenOutputFolder(string? path)
    {
        if (IsTestEnvironment()) return;

        string dir = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? Path.GetDirectoryName(path) ?? OutputDirectory
            : OutputDirectory;

        if (Directory.Exists(dir))
            try
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch
            {
            }
    }
}
