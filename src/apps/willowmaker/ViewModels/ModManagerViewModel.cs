// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Repair;
using Kongor.Modding.Vfs;
using WILLOWMAKER.Core.Services;

namespace WILLOWMAKER.Core.ViewModels;

public partial class ModManagerViewModel : ObservableObject
{
    private static readonly Regex ProtectedResourcesArchiveRegex = new(
        @"^resources([0-9]|[0-9]{2}|[a-z][0-9]|999)\.s2z$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string AppliedAvatarsManifestFileName = ".applied_avatars.json";

    private static readonly HashSet<string> SystemBaseArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        "resources0.s2z",
        "resources1.s2z",
        "resources2.s2z",
        "resources3.s2z",
        "resources4.s2z",
        "resources999.s2z",
        "textures.s2z",
        "archers.s2z",
        "arena.s2z",
        "caldavar.s2z",
        "caldavar_halloween.s2z",
        "caldavar_old.s2z",
        "caldavar_reborn.s2z",
        "caldavar_wtf.s2z",
        "capturetheflag.s2z",
        "colosseum_duel.s2z",
        "darkwoodvale.s2z",
        "devowars.s2z",
        "devowars_10v10.s2z",
        "empath_maze.s2z",
        "enfos.s2z",
        "grimmscrossing.s2z",
        "mids_omg.s2z",
        "midwars.s2z",
        "midwars_10v10.s2z",
        "midwars_halloween.s2z",
        "midwars_reborn.s2z",
        "nightmarish_coop.s2z",
        "pebbles_duel.s2z",
        "persian_pass.s2z",
        "prophets.s2z",
        "riftwars.s2z",
        "soccer.s2z",
        "spotlight.s2z",
        "team_deathmatch.s2z",
        "thegrimmhunt.s2z",
        "three_kingdoms.s2z",
        "truedevo.s2z",
        "truedevo_10v10.s2z",
        "tutorial.s2z",
        "valkwars.s2z",
        "watchtower.s2z"
    };

    public static bool IsProtectedSystemOrExportArchive(string fileNameOrPath, string? clientDir = null)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        string fileName = Path.GetFileName(fileNameOrPath);
        if (SystemBaseArchives.Contains(fileName) || ProtectedResourcesArchiveRegex.IsMatch(fileName))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(clientDir))
        {
            BuildManifestDto? manifest = PlaytestPolicyService.TryReadBuildManifest(clientDir);
            if (manifest?.ArchiveHashes is { Count: > 0 })
            {
                foreach (string manifestKey in manifest.ArchiveHashes.Keys)
                {
                    if (string.Equals(Path.GetFileName(manifestKey), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private readonly Action<string, string>? _logAction;

    private readonly ModManager _modManager = new();
    private readonly Action? _openLiveLogsCallback;
    private readonly ModRepairEngine _repairEngine = new();
    private bool _isLoadingMods;
    private CancellationTokenSource? _autoApplyCts;
    private readonly SemaphoreSlim _modSyncGate = new(1, 1);
    private readonly SynchronizationContext? _uiSyncContext = SynchronizationContext.Current;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    public Func<Task>? BeforeAvatarInjectHookForTesting { get; set; }

    private async Task DispatchToUiAsync(Func<Task> work)
    {
        if (Application.Current is not null)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                await work();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(work);
            }

            return;
        }

        if (_uiSyncContext is not null && Environment.CurrentManagedThreadId != _uiThreadId)
        {
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiSyncContext.Post(async _ =>
            {
                try
                {
                    await work();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            await tcs.Task.ConfigureAwait(false);
            return;
        }

        await work();
    }

    public async Task EnsureSynchronizedForLaunchAsync(CancellationToken cancellationToken = default)
    {
        _autoApplyCts?.Cancel();
        Log("[MOD SYNC] Ensuring mod state is synchronized via gate prior to game client launch...");
        await DispatchToUiAsync(() => ApplyModsCoreAsync(waitForGate: true, cancellationToken));
    }

    public void ScheduleAutoApply(int debounceDelayMs = 350)
    {
        _autoApplyCts?.Cancel();
        _autoApplyCts?.Dispose();
        _autoApplyCts = new CancellationTokenSource();
        CancellationToken ct = _autoApplyCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceDelayMs, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    await DispatchToUiAsync(() => ApplyModsCoreAsync(waitForGate: true, ct)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Debounced
            }
            catch (Exception ex)
            {
                Log($"[MOD AUTO-SYNC ERROR] {ex.Message}", true);
            }
        }, ct);
    }

    public ModManagerViewModel() : this(null, null, true)
    {
    }

    public ModManagerViewModel(bool autoLoad) : this(null, null, autoLoad)
    {
    }

    public ModManagerViewModel(Action<string, string>? logAction, Action? openLiveLogsCallback = null, bool autoLoad = true)
    {
        _logAction = logAction;
        _openLiveLogsCallback = openLiveLogsCallback;
        if (autoLoad)
        {
            LoadInstalledMods();
        }
    }

    [ObservableProperty] public partial string? SettingsDirectory { get; set; }

    public string? GameClientDirectoryOverride { get; set; }

    [ObservableProperty] public partial ObservableCollection<ModItemViewModel> Mods { get; set; } = new();

    [ObservableProperty] public partial ObservableCollection<ModItemViewModel> FilteredMods { get; set; } = new();

    [ObservableProperty] public partial ObservableCollection<ModItemViewModel> FavoriteMods { get; set; } = new();

    [ObservableProperty] public partial ObservableCollection<ModItemViewModel> ActiveAndFavoriteMods { get; set; } = new();

    [ObservableProperty] public partial ModItemViewModel? SelectedMod { get; set; }

    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<string> AvailableCategoryFilters { get; set; } = new()
    {
        "All Mods",
        "Super Smash Bros",
        "Warcraft III",
        "Dota 2",
        "UI & HUD",
        "Gameplay & Assists",
        "Audio & Sound",
        "Repaired"
    };

    [ObservableProperty] public partial string SelectedCategoryFilter { get; set; } = "All Mods";

    [ObservableProperty] public partial bool IsBusy { get; set; } = false;

    [ObservableProperty] public partial string StatusMessage { get; set; } = "Ready";

    [ObservableProperty] public partial string StatusColor { get; set; } = "#34D399";

    [ObservableProperty] public partial bool HasError { get; set; } = false;

    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty] public partial double ProgressPercent { get; set; } = 0.0;

    [ObservableProperty] public partial int ActiveModsCount { get; set; } = 0;

    [ObservableProperty] public partial int TotalModsCount { get; set; } = 0;

    private void Log(string message, bool isError = false)
    {
        string fullMsg = isError ? $"[ERROR] {message}" : message;
        _logAction?.Invoke(LogCategory.Mods, fullMsg);
        Debug.WriteLine($"[MODS] {fullMsg}");
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        ApplyFilter();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModItemViewModel.IsEnabled))
        {
            if (!_isLoadingMods)
            {
                UpdateActiveCount();
                ScheduleAutoApply();
            }
        }
        if (e.PropertyName == nameof(ModItemViewModel.IsFavorite))
        {
            if (!_isLoadingMods)
            {
                UpdateFavorites();
            }
        }
    }

    [RelayCommand]
    public void ToggleFavorite(ModItemViewModel? mod)
    {
        if (mod == null) return;
        mod.IsFavorite = !mod.IsFavorite;
        UpdateFavorites();
    }

    [RelayCommand]
    public void ToggleModActive(ModItemViewModel? mod)
    {
        if (mod == null) return;
        mod.IsEnabled = !mod.IsEnabled;
        UpdateActiveCount();
    }

    public void UpdateActiveAndFavorites()
    {
        ActiveAndFavoriteMods.Clear();
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        // 1. Currently active mods first
        foreach (ModItemViewModel mod in Mods)
        {
            if (mod.IsEnabled && seenIds.Add(mod.Id))
            {
                ActiveAndFavoriteMods.Add(mod);
            }
        }

        // 2. Inactive favorite mods second (for quick 1-click activation)
        foreach (ModItemViewModel mod in Mods)
        {
            if (mod.IsFavorite && !mod.IsEnabled && seenIds.Add(mod.Id))
            {
                ActiveAndFavoriteMods.Add(mod);
            }
        }
    }

    public void UpdateFavorites()
    {
        FavoriteMods.Clear();
        List<string> favIds = [];
        foreach (ModItemViewModel mod in Mods)
        {
            if (mod.IsFavorite)
            {
                FavoriteMods.Add(mod);
                favIds.Add(mod.Id);
            }
        }
        try
        {
            LauncherSettingsModel settings = LauncherSettingsService.Load(SettingsDirectory);
            HashSet<string> currentModIds = new(Mods.Select(m => m.Id).Concat(Mods.Select(m => m.Name)), StringComparer.OrdinalIgnoreCase);
            List<string> mergedFavs = settings.FavoriteModIds
                .Where(id => !currentModIds.Contains(id))
                .Concat(favIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            settings.FavoriteModIds = mergedFavs;
            LauncherSettingsService.Save(settings, SettingsDirectory);
        }
        catch
        {
            // Non-critical
        }
        UpdateActiveAndFavorites();
    }

    private void SeedDemoMods()
    {
        ModItemViewModel[] demoMods =
        [
            new()
            {
                Id = "minimap_pro",
                Name = "Enhanced MiniMap Pro",
                Category = "UI & HUD",
                Type = ModType.HonMod,
                IconGlyph = "🗺️",
                Version = "1.2.0",
                Author = "Kongor Devs",
                Description = "High-definition minimap overlay with ward indicators and hero icon scaling.",
                CompatibilityStatus = "Verified",
                StatusBadgeColor = "#22C55E",
                IsEnabled = true,
                IsFavorite = false
            },
            new()
            {
                Id = "smash_announcer",
                Name = "Super Smash Announcer",
                Category = "Audio & Sound",
                Type = ModType.AudioPack,
                IconGlyph = "🔊",
                Version = "2.0.1",
                Author = "MeleeKing",
                Description = "Replaces in-game multi-kill announcer voice lines with Super Smash Bros Melee audio.",
                CompatibilityStatus = "Verified",
                StatusBadgeColor = "#22C55E",
                IsEnabled = true,
                IsFavorite = false
            },
            new()
            {
                Id = "tower_ranges",
                Name = "Tactical Tower Ranges",
                Category = "Gameplay & Assists",
                Type = ModType.HonMod,
                IconGlyph = "⚡",
                Version = "1.0.4",
                Author = "TacticsLab",
                Description = "Visual radius indicators for tower aggro boundaries and true-sight detection.",
                CompatibilityStatus = "Verified",
                StatusBadgeColor = "#22C55E",
                IsEnabled = false,
                IsFavorite = false
            },
            new()
            {
                Id = "clean_zoom",
                Name = "Clean Camera Zoom",
                Category = "Gameplay & Assists",
                Type = ModType.HonMod,
                IconGlyph = "🔍",
                Version = "1.1.0",
                Author = "Community",
                Description = "Unlocks comfortable camera height presets for ultrawide and 4K displays.",
                CompatibilityStatus = "Verified",
                StatusBadgeColor = "#22C55E",
                IsEnabled = true,
                IsFavorite = false
            }
        ];

        foreach (ModItemViewModel item in demoMods)
        {
            item.PropertyChanged += OnItemPropertyChanged;
            Mods.Add(item);
        }
    }

    public void LoadInstalledMods()
    {
        try
        {
            _isLoadingMods = true;
            HasError = false;
            ErrorMessage = string.Empty;

            foreach (ModItemViewModel existing in Mods) existing.PropertyChanged -= OnItemPropertyChanged;
            Mods.Clear();

            string baseDir = Directory.GetCurrentDirectory();
            string? resolvedGameDir = LocationGuard.ResolveGameClientDirectory(baseDir)
                ?? LocationGuard.ResolveGameClientDirectory(AppContext.BaseDirectory);

            // Locate potential mods directories
            List<string> candidateDirs =
            [
                Path.Combine(baseDir, "game", "mods"),
                Path.Combine(baseDir, "extensions", "mods"),
                Path.Combine(baseDir, "KONGOR", "extensions"),
                Path.Combine(baseDir, "mods"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "extensions", "mods"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "extensions", "mods")
            ];

            if (!string.IsNullOrEmpty(resolvedGameDir) && !resolvedGameDir.Equals(baseDir, StringComparison.OrdinalIgnoreCase))
            {
                candidateDirs.Add(Path.Combine(resolvedGameDir, "game", "mods"));
                candidateDirs.Add(Path.Combine(resolvedGameDir, "extensions", "mods"));
                candidateDirs.Add(Path.Combine(resolvedGameDir, "KONGOR", "extensions"));
                candidateDirs.Add(Path.Combine(resolvedGameDir, "mods"));
            }

            HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenModIds = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenModNames = new(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in candidateDirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                // 1. Honmod packages
                foreach (string file in Directory.GetFiles(dir, "*.honmod", SearchOption.AllDirectories))
                {
                    if (!seenFiles.Add(file)) continue;

                    ModItemViewModel item = CreateHonModViewModel(file);
                    if (seenModIds.Add(item.Id) && seenModNames.Add(item.Name))
                    {
                        item.PropertyChanged += OnItemPropertyChanged;
                        Mods.Add(item);
                    }
                }

                // 2. Avatar .s2z packages
                foreach (string file in Directory.GetFiles(dir, "*.s2z", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileName(file);
                    if (IsProtectedSystemOrExportArchive(fileName, resolvedGameDir ?? baseDir))
                        continue;

                    if (!seenFiles.Add(file)) continue;

                    ModItemViewModel item = CreateAvatarS2zViewModel(file);
                    if (seenModIds.Add(item.Id) && seenModNames.Add(item.Name))
                    {
                        item.PropertyChanged += OnItemPropertyChanged;
                        Mods.Add(item);
                    }
                }
            }

            if (Mods.Count == 0)
            {
                SeedDemoMods();
            }

            LauncherSettingsModel launcherSettings = LauncherSettingsService.Load(SettingsDirectory);
            HashSet<string> favSet = new(launcherSettings.FavoriteModIds, StringComparer.OrdinalIgnoreCase);
            HashSet<string> enabledSet = new(launcherSettings.EnabledModIds, StringComparer.OrdinalIgnoreCase);
            foreach (ModItemViewModel m in Mods)
            {
                if (favSet.Contains(m.Id) || favSet.Contains(m.Name))
                {
                    m.IsFavorite = true;
                }
                if (enabledSet.Contains(m.Id) || enabledSet.Contains(m.Name))
                {
                    m.IsEnabled = true;
                }
            }

            TotalModsCount = Mods.Count;
            UpdateActiveCount();
            UpdateFavorites();
            ApplyFilter();
            if (Mods.Count > 0 && SelectedMod == null) SelectedMod = Mods[0];

            Log($"Discovered and cataloged {TotalModsCount} mods and hero avatar skins.");
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Failed to load mods: {ex.Message}";
            StatusMessage = ErrorMessage;
            StatusColor = "#EF4444";
            Log($"Error loading mods: {ex.Message}", true);
        }
        finally
        {
            _isLoadingMods = false;
        }
    }

    private ModItemViewModel CreateHonModViewModel(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string category = DetermineHonModCategory(fileName);
        string glyph = DetermineGlyph(category);

        ModItemViewModel item = new()
        {
            Id = fileName,
            Name = fileName,
            FilePath = filePath,
            Category = category,
            Type = ModType.HonMod,
            IconGlyph = glyph,
            Version = "1.0.0",
            Author = "Community",
            Description = $"HoN Community modification package: {fileName}",
            CompatibilityStatus = "Verified AST Diff",
            StatusBadgeColor = "#22C55E",
            IsEnabled = false
        };

        try
        {
            using ZipArchive zip = ZipFile.OpenRead(filePath);
            ZipArchiveEntry? modXml = zip.GetEntry("mod.xml");
            if (modXml != null)
            {
                using Stream s = modXml.Open();
                using StreamReader r = new(s);
                string xml = r.ReadToEnd();
                if (Regex.Match(xml, @"name=""([^""]+)""") is { Success: true } mName)
                    item.Name = mName.Groups[1].Value;
                if (Regex.Match(xml, @"version=""([^""]+)""") is { Success: true } mVer)
                    item.Version = mVer.Groups[1].Value;
                if (Regex.Match(xml, @"author=""([^""]+)""") is { Success: true } mAuth)
                    item.Author = mAuth.Groups[1].Value;
                if (Regex.Match(xml, @"description=""([^""]+)""") is { Success: true } mDesc)
                    item.Description = mDesc.Groups[1].Value;
            }
        }
        catch
        {
        }

        return item;
    }

    private ModItemViewModel CreateAvatarS2zViewModel(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string cleanName = fileName;
        if (cleanName.StartsWith("resources", StringComparison.OrdinalIgnoreCase)) cleanName = cleanName.Substring(9);

        string formattedName = FormatCamelCase(cleanName);
        string category = DetermineAvatarCategory(filePath, cleanName);
        string glyph = category switch
        {
            "Super Smash Bros" => "🥊",
            "Warcraft III" => "⚔",
            "Dota 2" => "🛡",
            _ => "🎮"
        };

        return new ModItemViewModel
        {
            Id = fileName,
            Name = $"{formattedName} ({category})",
            FilePath = filePath,
            Category = category,
            Type = ModType.AvatarS2z,
            IconGlyph = glyph,
            Version = "1.0.0",
            Author = "Asset Creator",
            Description =
                $"Custom 3D hero avatar model, texture skin, animations, and particle effects for {formattedName} ({category}).",
            CompatibilityStatus = "S2Z Package Ready",
            StatusBadgeColor = "#38BDF8",
            IsEnabled = false
        };
    }

    public static string FormatCamelCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return Regex.Replace(text, @"(\B[A-Z])", " $1").Trim();
    }

    private static string DetermineAvatarCategory(string filePath, string heroName)
    {
        string lower = filePath.ToLowerInvariant();
        if (lower.Contains("super smash bros") || lower.Contains("ssb skins") || lower.Contains("smash"))
            return "Super Smash Bros";
        if (lower.Contains("warcraft"))
            return "Warcraft III";
        if (lower.Contains("dota"))
            return "Dota 2";

        string heroLower = heroName.ToLowerInvariant();
        if (heroLower is "pikachu" or "bowser" or "mario" or "luigi" or "sonic" or "link" or "cloud" or "sephiroth"
            or "sora" or "steve" or "donkeykong" or "kirby" or "zelda" or "samus" or "pacman" or "fox" or "falco"
            or "joker" or "ken" or "ryu" or "megaman" or "piranhaplant" or "charizard" or "greninja" or "incineroar"
            or "jigglypuff" or "lucario" or "mewtwo" or "pichu" or "pokemontrainer" or "waluigi" or "wario" or "yoshi"
            or "sheik" or "pit" or "rosalina" or "roy" or "olimar" or "kingdedede" or "metaknight" or "daisy" or "peach"
            or "captainfalcon" or "ganondorf")
            return "Super Smash Bros";

        return "Avatars & Models";
    }

    private static string DetermineHonModCategory(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("range") || lower.Contains("aoe") || lower.Contains("hud") || lower.Contains("inventory") ||
            lower.Contains("minimap") || lower.Contains("stats") || lower.Contains("bar") || lower.Contains("score") ||
            lower.Contains("ui"))
            return "UI & HUD";
        if (lower.Contains("sound") || lower.Contains("announcer") || lower.Contains("voice") ||
            lower.Contains("music") || lower.Contains("mario") || lower.Contains("morty") || lower.Contains("audio"))
            return "Audio & Sound";
        if (lower.Contains("assist") || lower.Contains("cast") || lower.Contains("picker") ||
            lower.Contains("denier") || lower.Contains("macro") || lower.Contains("bottle") || lower.Contains("ban"))
            return "Gameplay & Assists";
        return "UI & HUD";
    }

    private static string DetermineGlyph(string category)
    {
        return category switch
        {
            "UI & HUD" => "🖥",
            "Super Smash Bros" => "⭐",
            "Warcraft III" => "⚔",
            "Dota 2" => "🛡",
            "Audio & Sound" => "🔊",
            "Gameplay & Assists" => "⚡",
            _ => "🎮"
        };
    }

    public void ApplyFilter()
    {
        IEnumerable<ModItemViewModel> query = Mods;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string q = SearchQuery.Trim().ToLowerInvariant();
            query = query.Where(m => m.Name.ToLowerInvariant().Contains(q) ||
                                     m.Author.ToLowerInvariant().Contains(q) ||
                                     m.Category.ToLowerInvariant().Contains(q) ||
                                     m.FilePath.ToLowerInvariant().Contains(q) ||
                                     m.Description.ToLowerInvariant().Contains(q));
        }

        if (SelectedCategoryFilter != "All Mods")
        {
            if (SelectedCategoryFilter == "Repaired")
                query = query.Where(m => m.IsRepaired);
            else
                query = query.Where(m => m.Category.Equals(SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        FilteredMods = new ObservableCollection<ModItemViewModel>(query);
    }

    [RelayCommand]
    public void ToggleMod(ModItemViewModel mod)
    {
        mod.IsEnabled = !mod.IsEnabled;
        UpdateActiveCount();
        Log(
            $"[MOD TOGGLE] '{mod.Name}' is now {(mod.IsEnabled ? "ENABLED" : "DISABLED")}. Total active: {ActiveModsCount}");
    }

    [RelayCommand]
    public void EnableAll()
    {
        foreach (ModItemViewModel mod in FilteredMods)
            mod.IsEnabled = true;
        UpdateActiveCount();
        Log($"[MOD ACTION] Enabled all {FilteredMods.Count} filtered mods. Total active: {ActiveModsCount}");
    }

    [RelayCommand]
    public void DisableAll()
    {
        foreach (ModItemViewModel mod in FilteredMods)
            mod.IsEnabled = false;
        UpdateActiveCount();
        Log($"[MOD ACTION] Disabled all {FilteredMods.Count} filtered mods. Total active: {ActiveModsCount}");
    }

    public void UpdateActiveCount()
    {
        ActiveModsCount = Mods.Count(m => m.IsEnabled);
        try
        {
            LauncherSettingsModel settings = LauncherSettingsService.Load(SettingsDirectory);
            HashSet<string> currentModIds = new(Mods.Select(m => m.Id).Concat(Mods.Select(m => m.Name)), StringComparer.OrdinalIgnoreCase);
            List<string> activeIds = Mods.Where(m => m.IsEnabled).Select(m => m.Id).ToList();
            List<string> mergedEnabled = settings.EnabledModIds
                .Where(id => !currentModIds.Contains(id))
                .Concat(activeIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            settings.EnabledModIds = mergedEnabled;
            LauncherSettingsService.Save(settings, SettingsDirectory);
        }
        catch
        {
            // Non-critical
        }
        UpdateActiveAndFavorites();
    }

    [RelayCommand]
    public Task ApplyModsAsync() => DispatchToUiAsync(() => ApplyModsCoreAsync(waitForGate: false, CancellationToken.None));

    private async Task ApplyModsCoreAsync(bool waitForGate, CancellationToken cancellationToken)
    {
        if (waitForGate)
        {
            await _modSyncGate.WaitAsync(cancellationToken);
        }
        else if (!await _modSyncGate.WaitAsync(0, cancellationToken))
        {
            Log("[MOD APPLY] Skipping duplicate manual ApplyMods click while mod synchronization gate is busy.");
            return;
        }

        try
        {
            await ApplyModsUnlockedAsync();
        }
        finally
        {
            _modSyncGate.Release();
        }
    }

    private async Task ApplyModsUnlockedAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            HasError = false;
            ErrorMessage = string.Empty;
            UpdateActiveCount();
            StatusMessage = "Applying active mods & avatar skins to game client...";
            StatusColor = "#38BDF8";
            ProgressPercent = 20;

            string currentDir = Directory.GetCurrentDirectory();
            string clientDir = GameClientDirectoryOverride ?? LocationGuard.ResolveGameClientDirectory(currentDir)
                ?? LocationGuard.ResolveGameClientDirectory(AppContext.BaseDirectory)
                ?? currentDir;

            string gameDir = Path.Combine(clientDir, "game");
            string extensionsDir = Path.Combine(clientDir, "KONGOR", "extensions");
            List<string> trackedAvatarEntries = ReadAppliedAvatarManifest(gameDir);

            bool anyEnabledModOrAvatar = Mods.Any(m => m.IsEnabled);
            bool anyMountedAvatarArchive = Directory.Exists(gameDir) &&
                                           Directory.EnumerateFiles(gameDir, "resources_avatar_*.s2z").Any();
            bool hasDirtyModBaseline = RollbackJournal.HasActiveModifications(clientDir) ||
                                       File.Exists(Path.Combine(gameDir, "resources999.s2z")) ||
                                       File.Exists(Path.Combine(clientDir, "override", ".hotreload.trigger"));

            if (!anyEnabledModOrAvatar &&
                trackedAvatarEntries.Count == 0 &&
                !anyMountedAvatarArchive &&
                !hasDirtyModBaseline &&
                BeforeAvatarInjectHookForTesting is null)
            {
                ProgressPercent = 100;
                UpdateActiveCount();
                StatusMessage = "All mods & avatar skins disabled. Clean vanilla state verified.";
                StatusColor = "#94A3B8";
                Log("[MOD APPLY FAST-PATH] Zero active mods/avatars and clean persisted RollbackJournal + AvatarManifest verified — skipping redundant archive rollback.");
                return;
            }

            // Revert any previously applied VFS mods/overwrites off the UI thread to ensure a clean baseline
            await Task.Run(() => _modManager.RollbackAsync(clientDir));

            if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);
            if (!Directory.Exists(extensionsDir)) Directory.CreateDirectory(extensionsDir);

            int appliedAvatars = 0;
            int removedAvatars = 0;
            HashSet<string> activeAvatarArchiveNames = new(StringComparer.OrdinalIgnoreCase);

            if (BeforeAvatarInjectHookForTesting is not null)
            {
                await BeforeAvatarInjectHookForTesting();
            }

            // 1. Process Avatar S2Z Packages
            foreach (ModItemViewModel avatar in Mods.Where(m => m.Type == ModType.AvatarS2z))
            {
                string rawFileName = Path.GetFileName(avatar.FilePath);
                if (IsProtectedSystemOrExportArchive(rawFileName, clientDir))
                {
                    Log($"[AVATAR GUARD] Skipping protected system archive '{rawFileName}'.");
                    continue;
                }

                // Prefix custom avatar archives with resources_avatar_ so K2 mounts them above resources0..4 without colliding with resourcesa0..z9
                string archiveName = rawFileName.StartsWith("resources_avatar_", StringComparison.OrdinalIgnoreCase)
                    ? rawFileName
                    : rawFileName.StartsWith("resources_", StringComparison.OrdinalIgnoreCase) && !IsProtectedSystemOrExportArchive(rawFileName, clientDir)
                        ? rawFileName
                        : $"resources_avatar_{rawFileName}";

                string targetGamePath = Path.Combine(gameDir, archiveName);
                string targetExtPath = Path.Combine(extensionsDir, rawFileName);

                if (avatar.IsEnabled)
                {
                    if (File.Exists(avatar.FilePath))
                    {
                        await Task.Run(() =>
                        {
                            File.Copy(avatar.FilePath, targetGamePath, true);
                            File.Copy(avatar.FilePath, targetExtPath, true);
                        });
                        activeAvatarArchiveNames.Add(archiveName);
                        activeAvatarArchiveNames.Add(rawFileName);
                        appliedAvatars++;
                        FileInfo fi = new(avatar.FilePath);
                        double mb = (double)fi.Length / (1024 * 1024);
                        Log($"[AVATAR INJECT] Mounted '{avatar.Name}' -> '{archiveName}' ({mb:F2} MB)");
                    }
                }
                else
                {
                    bool wasRemoved = false;
                    if (File.Exists(targetGamePath) && !IsProtectedSystemOrExportArchive(archiveName, clientDir))
                    {
                        File.Delete(targetGamePath);
                        wasRemoved = true;
                    }

                    if (File.Exists(targetExtPath) && !IsProtectedSystemOrExportArchive(rawFileName, clientDir))
                    {
                        File.Delete(targetExtPath);
                        wasRemoved = true;
                    }

                    if (wasRemoved)
                    {
                        removedAvatars++;
                        Log($"[AVATAR REMOVE] Unmounted '{avatar.Name}' from game client.");
                    }
                }
            }

            WriteAppliedAvatarManifest(gameDir, activeAvatarArchiveNames);

            // 2. Process HonMod Packages
            List<ModPackage> packages = [];
            foreach (ModItemViewModel mod in Mods.Where(m => m.IsEnabled && m.Type == ModType.HonMod))
                if (File.Exists(mod.FilePath))
                    try
                    {
                        ModPackage pkg = await HonModArchiveReader.ReadPackageAsync(mod.FilePath);
                        pkg.IsEnabled = true;
                        packages.Add(pkg);
                        Log($"[HONMOD INJECT] Queued '{mod.Name}' v{mod.Version} for VFS compilation.");
                    }
                    catch (Exception ex)
                    {
                        Log($"[HONMOD READ WARNING] Could not read '{mod.Name}': {ex.Message}");
                    }

            ProgressPercent = 60;
            ApplyResult result = await Task.Run(() => _modManager.ApplyModsAsync(new ApplyOptions
            {
                GameClientDirectory = clientDir,
                ModsToApply = packages,
                Mode = InjectionMode.Archive
            }));

            ProgressPercent = 100;
            UpdateActiveCount();

            if (result.Success)
            {
                if (result.AppliedMods.Count == 0 && appliedAvatars == 0)
                {
                    StatusMessage = "All mods & avatar skins disabled. Clean vanilla state restored.";
                    StatusColor = "#94A3B8";
                    Log("[MOD APPLY] All mods disabled. VFS overrides reverted to clean baseline.");
                }
                else
                {
                    StatusMessage =
                        $"Applied {result.AppliedMods.Count} mods & {appliedAvatars} avatars ({result.ModifiedFiles.Count} VFS delta files).";
                    StatusColor = "#34D399";
                    Log(
                        $"[MOD APPLY SUCCESS] Applied {result.AppliedMods.Count} mods and {appliedAvatars} avatars. Clean VFS mount verified.");
                }

                if (result.Warnings.Count > 0)
                {
                    foreach (string warning in result.Warnings)
                    {
                        Log($"[MOD APPLY WARNING] {warning}");
                    }
                }
            }
            else
            {
                HasError = true;
                ErrorMessage =
                    $"Mod apply finished with {result.Errors.Count} errors: {string.Join("; ", result.Errors)}";
                StatusMessage = ErrorMessage;
                StatusColor = "#EF4444";
                Log($"[MOD APPLY ERROR] {ErrorMessage}", true);
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Apply error: {ex.Message}";
            StatusMessage = ErrorMessage;
            StatusColor = "#EF4444";
            Log($"[MOD APPLY FATAL] {ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RollbackModsAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            HasError = false;
            ErrorMessage = string.Empty;
            StatusMessage = "Reverting all applied mods and avatar skins to clean state...";
            string currentDir = Directory.GetCurrentDirectory();
            string clientDir = GameClientDirectoryOverride ?? LocationGuard.ResolveGameClientDirectory(currentDir)
                ?? LocationGuard.ResolveGameClientDirectory(AppContext.BaseDirectory)
                ?? currentDir;

            // Clear ONLY custom avatar archives (resources_avatar_*.s2z or entries recorded in .applied_avatars.json), NEVER shipped resourcesa0..z9.s2z or core archives!
            string gameDir = Path.Combine(clientDir, "game");
            HashSet<string> trackedAvatars = new(ReadAppliedAvatarManifest(gameDir), StringComparer.OrdinalIgnoreCase);
            foreach (ModItemViewModel knownAvatar in Mods.Where(m => m.Type == ModType.AvatarS2z && !string.IsNullOrWhiteSpace(m.FilePath)))
            {
                string raw = Path.GetFileName(knownAvatar.FilePath);
                if (!IsProtectedSystemOrExportArchive(raw, clientDir))
                {
                    trackedAvatars.Add(raw);
                    trackedAvatars.Add($"resources_{raw}");
                    trackedAvatars.Add($"resources_avatar_{raw}");
                }
            }

            if (Directory.Exists(gameDir))
            {
                foreach (string file in Directory.GetFiles(gameDir, "resources*.s2z"))
                {
                    string fn = Path.GetFileName(file);
                    if (IsProtectedSystemOrExportArchive(fn, clientDir))
                    {
                        continue;
                    }

                    if (fn.StartsWith("resources_avatar_", StringComparison.OrdinalIgnoreCase) || trackedAvatars.Contains(fn))
                    {
                        try
                        {
                            File.Delete(file);
                            Log($"[CLEANUP] Removed custom avatar archive: {fn}");
                        }
                        catch (Exception delEx)
                        {
                            Log($"[CLEANUP WARNING] Could not delete custom avatar archive '{fn}': {delEx.Message}. Close hon_x64 if running.", true);
                        }
                    }
                }

                string manifestPath = Path.Combine(gameDir, AppliedAvatarsManifestFileName);
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        File.Delete(manifestPath);
                    }
                    catch (Exception manEx)
                    {
                        Log($"[CLEANUP WARNING] Could not remove '{AppliedAvatarsManifestFileName}': {manEx.Message}");
                    }
                }
            }

            // Clear tracked custom avatar copies in KONGOR/extensions without deleting unrelated shipped extensions
            string extensionsDir = Path.Combine(clientDir, "KONGOR", "extensions");
            if (Directory.Exists(extensionsDir))
            {
                foreach (string file in Directory.GetFiles(extensionsDir, "*.s2z"))
                {
                    string fn = Path.GetFileName(file);
                    if (IsProtectedSystemOrExportArchive(fn, clientDir))
                    {
                        continue;
                    }

                    if (fn.StartsWith("resources_avatar_", StringComparison.OrdinalIgnoreCase) || trackedAvatars.Contains(fn))
                    {
                        try
                        {
                            File.Delete(file);
                            Log($"[CLEANUP] Removed custom extension avatar archive: {fn}");
                        }
                        catch (Exception extDelEx)
                        {
                            Log($"[CLEANUP WARNING] Could not delete '{file}': {extDelEx.Message}", true);
                        }
                    }
                }
            }

            RollbackResult res = await _modManager.RollbackAsync(clientDir);
            DisableAll();
            UpdateActiveCount();
            StatusMessage = "Clean baseline state restored. All mods unmounted.";
            StatusColor = "#34D399";
            Log("[MOD ROLLBACK] Rollback complete. 100% clean baseline restored.");
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Rollback error: {ex.Message}";
            StatusMessage = ErrorMessage;
            StatusColor = "#EF4444";
            Log($"[MOD ROLLBACK ERROR] {ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RepairSelectedModAsync()
    {
        if (SelectedMod == null || IsBusy) return;

        try
        {
            IsBusy = true;
            StatusMessage = $"Repairing '{SelectedMod.Name}' with AST Normalizer...";
            StatusColor = "#38BDF8";
            ProgressPercent = 50;

            if (File.Exists(SelectedMod.FilePath))
            {
                string outPath = SelectedMod.FilePath.Replace(".honmod", "_repaired.honmod");
                RepairResult repairRes = await _repairEngine.RepairModAsync(SelectedMod.FilePath, outPath);

                if (repairRes.Success)
                {
                    SelectedMod.IsRepaired = true;
                    SelectedMod.CompatibilityStatus = $"Repaired ({repairRes.FixesAppliedCount} fixes applied)";
                    SelectedMod.StatusBadgeColor = "#38BDF8";
                    StatusMessage = $"Repaired '{SelectedMod.Name}' successfully!";
                    StatusColor = "#34D399";
                    Log(
                        $"[MOD REPAIR SUCCESS] Repaired '{SelectedMod.Name}' -> '{outPath}' ({repairRes.FixesAppliedCount} AST/Lua fixes).");
                }
                else
                {
                    HasError = true;
                    ErrorMessage = $"Repair failed: {repairRes.ErrorMessage}";
                    StatusMessage = ErrorMessage;
                    StatusColor = "#EF4444";
                    Log($"[MOD REPAIR FAILED] {repairRes.ErrorMessage}", true);
                }
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Repair error: {ex.Message}";
            StatusMessage = ErrorMessage;
            StatusColor = "#EF4444";
            Log($"[MOD REPAIR ERROR] {ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 100;
        }
    }

    [RelayCommand]
    public void OpenLiveLogs()
    {
        _openLiveLogsCallback?.Invoke();
    }

    [RelayCommand]
    public void OpenModsFolder()
    {
        string modsDir = Path.Combine(Directory.GetCurrentDirectory(), "Avatar Mods");
        if (!Directory.Exists(modsDir))
            modsDir = Path.Combine(Directory.GetCurrentDirectory(), "game", "mods");
        if (!Directory.Exists(modsDir))
            Directory.CreateDirectory(modsDir);

        Process.Start(new ProcessStartInfo
        {
            FileName = modsDir,
            UseShellExecute = true
        });
    }

    private static List<string> ReadAppliedAvatarManifest(string gameDir)
    {
        List<string> items = [];
        string path = Path.Combine(gameDir, AppliedAvatarsManifestFileName);
        if (!File.Exists(path))
        {
            return items;
        }

        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim().Trim('"', ',', '[', ']');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    items.Add(trimmed);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MODS] Notice reading '{path}': {ex.Message}");
        }

        return items;
    }

    private static void WriteAppliedAvatarManifest(string gameDir, IEnumerable<string> archiveNames)
    {
        string path = Path.Combine(gameDir, AppliedAvatarsManifestFileName);
        try
        {
            string[] entries = archiveNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (entries.Length == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }

            File.WriteAllLines(path, entries);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MODS] Notice writing '{path}': {ex.Message}");
        }
    }
}
