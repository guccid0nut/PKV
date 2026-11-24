// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Kongor.Core.Vfs;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;

namespace WILLOWMAKER.Core.ViewModels;

public sealed partial class LiveLogsViewModel : ObservableObject
{
    private readonly Lock _logSyncLock = new();
    private readonly List<WillowmakerLogMessage> _allLogs = [];

    [ObservableProperty] private bool _autoScroll = true;

    [ObservableProperty] private string _copyNotificationText = string.Empty;

    [ObservableProperty] private string _errorBannerBackground = "#052E16";

    [ObservableProperty] private string _errorBannerBorder = "#10B981";

    [ObservableProperty] private string _errorBannerForeground = "#22C55E";

    [ObservableProperty] private string _errorBannerText = "➔ 100% ERROR FREE - A WINNER IS YOU!";

    [ObservableProperty] private int _errorCount;

    [ObservableProperty] private ObservableCollection<WillowmakerLogMessage> _filteredLogs = [];

    [ObservableProperty] private bool _hasErrors;

    [ObservableProperty] private bool _isCopyNotificationVisible;

    [ObservableProperty] private int _logCount;

    [ObservableProperty] private bool _maskPaths = true;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private string _selectedCategoryFilter = "ALL CATEGORIES";

    [ObservableProperty] private string _selectedLevelFilter = "ALL LEVELS";

    public LiveLogsViewModel()
    {
        UpdateErrorState();
    }

    public string MaskPathsButtonText => MaskPaths ? "🔒 Paths Censored" : "👁 Paths Revealed";

    public List<string> AvailableLevelFilters { get; } =
    [
        "ALL LEVELS",
        "INFO",
        "WARN",
        "ERROR",
        "DEBUG"
    ];

    public List<string> AvailableCategoryFilters { get; } =
    [
        "ALL CATEGORIES",
        "MODS",
        "PARAMETERS",
        "EXECUTABLE",
        "GAME CLIENT",
        "COMMAND",
        "INITIALISE",
        "GUARD",
        "VERSION",
        "SYNCHRONISE",
        "GENERAL",
        "HOTRELOAD",
        "IPC",
        "ENGINE"
    ];

    public static string NormalizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return string.Empty;

        return category.Trim().TrimEnd('_').Replace("_", " ").ToUpperInvariant();
    }

    partial void OnMaskPathsChanged(bool value)
    {
        OnPropertyChanged(nameof(MaskPathsButtonText));
        lock (_logSyncLock)
        {
            for (int i = 0; i < _allLogs.Count; i++) _allLogs[i].IsRevealed = !value;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        RefreshFilteredLogs();
    }

    partial void OnSelectedLevelFilterChanged(string value)
    {
        _ = value;
        RefreshFilteredLogs();
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        _ = value;
        RefreshFilteredLogs();
    }

    [RelayCommand]
    public void FilterByModsCategory()
    {
        SelectedCategoryFilter = "MODS";
    }

    public void AddLog(string category, string message)
    {
        string level = "INFO";
        bool isError = false;

        if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            level = "ERROR";
            isError = true;
        }
        else if (message.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("warning", StringComparison.OrdinalIgnoreCase))
        {
            level = "WARN";
        }
        else if (message.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("trace", StringComparison.OrdinalIgnoreCase))
        {
            level = "DEBUG";
        }

        WillowmakerLogMessage logItem = new()
        {
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category,
            Message = message,
            Level = level,
            IsError = isError,
            IsRevealed = !MaskPaths
        };

        if (Dispatcher.UIThread.CheckAccess())
            ProcessNewLog(logItem);
        else
            Dispatcher.UIThread.Post(() => ProcessNewLog(logItem));
    }

    private void ProcessNewLog(WillowmakerLogMessage log)
    {
        lock (_logSyncLock)
        {
            ProcessNewLogCore(log);
        }
    }

    private void ProcessNewLogCore(WillowmakerLogMessage log)
    {
        _allLogs.Add(log);

        if (MatchesFilter(log))
        {
            FilteredLogs.Add(log);
            if (FilteredLogs.Count > 2000)
            {
                TrimPreservingErrors(FilteredLogs, 2000);
            }
        }

        if (_allLogs.Count > 10500)
        {
            TrimListPreservingErrors(_allLogs, 10000);
        }

        LogCount = FilteredLogs.Count;
        UpdateErrorState();
    }

    private static void TrimPreservingErrors(ObservableCollection<WillowmakerLogMessage> collection, int targetCapacity)
    {
        int toRemove = collection.Count - targetCapacity;
        if (toRemove <= 0) return;

        for (int i = 0; i < collection.Count && toRemove > 0;)
        {
            if (!collection[i].IsError && !string.Equals(collection[i].Level, "ERROR", StringComparison.OrdinalIgnoreCase) && !string.Equals(collection[i].Level, "FATAL", StringComparison.OrdinalIgnoreCase))
            {
                collection.RemoveAt(i);
                toRemove--;
            }
            else
            {
                i++;
            }
        }

        while (collection.Count > targetCapacity && toRemove > 0)
        {
            collection.RemoveAt(0);
            toRemove--;
        }
    }

    private static void TrimListPreservingErrors(List<WillowmakerLogMessage> list, int targetCapacity)
    {
        int toRemove = list.Count - targetCapacity;
        if (toRemove <= 0) return;

        for (int i = 0; i < list.Count && toRemove > 0;)
        {
            if (!list[i].IsError && !string.Equals(list[i].Level, "ERROR", StringComparison.OrdinalIgnoreCase) && !string.Equals(list[i].Level, "FATAL", StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
                toRemove--;
            }
            else
            {
                i++;
            }
        }

        while (list.Count > targetCapacity && toRemove > 0)
        {
            list.RemoveAt(0);
            toRemove--;
        }
    }

    [RelayCommand]
    public void ToggleGlobalPathMasking()
    {
        MaskPaths = !MaskPaths;
    }

    [RelayCommand]
    public void ClearLogs()
    {
        lock (_logSyncLock)
        {
            ClearLogsCore();
        }
    }

    private void ClearLogsCore()
    {
        _allLogs.Clear();
        FilteredLogs.Clear();
        LogCount = 0;
        UpdateErrorState();
    }

    [RelayCommand]
    public async Task CopyLogs()
    {
        StringBuilder sb = new();
        for (int i = 0; i < FilteredLogs.Count; i++) sb.AppendLine(FilteredLogs[i].DisplayText);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(sb.ToString());
            CopyNotificationText = $"✓ Copied {FilteredLogs.Count} filtered log lines to clipboard!";
            IsCopyNotificationVisible = true;

            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Dispatcher.UIThread.Post(() => IsCopyNotificationVisible = false);
            });
        }
    }

    [RelayCommand]
    public async Task ExportBugZip()
    {
        try
        {
            string root = LocationGuard.ResolveGameClientDirectory() ?? Environment.CurrentDirectory;
            var settings = LauncherSettingsService.Load(root);
            string modStr = ClientModMountSpec.GetCanonicalModString(includeOverride: !settings.PureCompatibilityMode);

            DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: root,
                isCrashTriggered: false,
                triageResult: null,
                activeModString: modStr,
                masterServerHost: settings.MasterServerAddress);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(result.MarkdownSnippet);
            }

            CopyNotificationText = $"✓ Exported {Path.GetFileName(result.ZipPath)} ({result.ZipSizeBytes / 1024} KB) & copied summary!";
            IsCopyNotificationVisible = true;

            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                Dispatcher.UIThread.Post(() => IsCopyNotificationVisible = false);
            });
        }
        catch (Exception ex)
        {
            CopyNotificationText = $"Export error: {ex.Message}";
            IsCopyNotificationVisible = true;
        }
    }

    private bool MatchesFilter(WillowmakerLogMessage log)
    {
        if (SelectedLevelFilter != "ALL LEVELS")
            if (!string.Equals(log.Level, SelectedLevelFilter, StringComparison.OrdinalIgnoreCase))
                return false;

        if (SelectedCategoryFilter != "ALL CATEGORIES")
        {
            string selectedNorm = NormalizeCategory(SelectedCategoryFilter);
            string logNorm = NormalizeCategory(log.Category);

            if (!string.Equals(logNorm, selectedNorm, StringComparison.OrdinalIgnoreCase) &&
                !logNorm.StartsWith(selectedNorm, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string search = SearchText.Trim();
            bool matchesMessage = log.Message.Contains(search, StringComparison.OrdinalIgnoreCase);
            bool matchesCategory = log.Category.Contains(search, StringComparison.OrdinalIgnoreCase);
            bool matchesLevel = log.Level.Contains(search, StringComparison.OrdinalIgnoreCase);

            if (!matchesMessage && !matchesCategory && !matchesLevel) return false;
        }

        return true;
    }

    private void RefreshFilteredLogs()
    {
        lock (_logSyncLock)
        {
            RefreshFilteredLogsCore();
        }
    }

    private void RefreshFilteredLogsCore()
    {
        FilteredLogs.Clear();
        for (int i = 0; i < _allLogs.Count; i++)
        {
            WillowmakerLogMessage log = _allLogs[i];
            if (MatchesFilter(log)) FilteredLogs.Add(log);
        }

        LogCount = FilteredLogs.Count;
        UpdateErrorState();
    }

    private void UpdateErrorState()
    {
        int count = 0;
        for (int i = 0; i < _allLogs.Count; i++)
            if (_allLogs[i].IsError)
                count++;

        ErrorCount = count;
        HasErrors = count > 0;

        if (HasErrors)
        {
            ErrorBannerText = "➔ EACH ERROR IS ONE STEP CLOSER TO PERFECTION!";
            ErrorBannerBackground = "#450A0A"; // Deep crimson
            ErrorBannerBorder = "#EF4444"; // Vivid red
            ErrorBannerForeground = "#F87171"; // Light red
        }
        else
        {
            ErrorBannerText = "➔ 100% ERROR FREE - A WINNER IS YOU!";
            ErrorBannerBackground = "#052E16"; // Deep emerald
            ErrorBannerBorder = "#10B981"; // Vivid emerald
            ErrorBannerForeground = "#22C55E"; // Bright green
        }
    }
}
