// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class LiveLogsViewModel : ViewModelBase, IDisposable
{
    private readonly List<LogMessage> _allLogs = [];
    private readonly ConcurrentQueue<LogMessage> _incomingLogQueue = new();
    private readonly INexusOrchestratorService _orchestrator;
    private int _isDrainScheduled;

    [ObservableProperty] private bool _autoScroll = true;

    [ObservableProperty] private string _copyNotificationText = string.Empty;

    [ObservableProperty] private string _errorBannerBackground = "#052E16";

    [ObservableProperty] private string _errorBannerBorder = "#10B981";

    [ObservableProperty] private string _errorBannerForeground = "#22C55E";

    [ObservableProperty] private string _errorBannerText = "➔ 100% ERROR FREE - A WINNER IS YOU!";

    [ObservableProperty] private int _errorCount;

    [ObservableProperty] private ObservableCollection<LogMessage> _filteredLogs = [];

    [ObservableProperty] private bool _hasErrors;

    [ObservableProperty] private bool _isCopyNotificationVisible;

    [ObservableProperty] private int _logCount;

    [ObservableProperty] private bool _maskPaths = true;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private string _selectedLevelFilter = "ALL LEVELS";

    [ObservableProperty] private string _selectedServiceFilter = "ALL SERVICES";

    public LiveLogsViewModel(INexusOrchestratorService orchestrator)
    {
        _orchestrator = orchestrator;
        _orchestrator.OnLogReceived += HandleNewLog;

        foreach (LogMessage msg in _orchestrator.LogHistory)
        {
            msg.IsRevealed = !MaskPaths;
            _allLogs.Add(msg);
            FilteredLogs.Add(msg);
        }

        LogCount = FilteredLogs.Count;
        UpdateErrorState();
    }

    public string MaskPathsButtonText => MaskPaths ? "🔒 Paths Censored" : "👁 Paths Revealed";

    public string AutoScrollButtonText => AutoScroll ? "⬇ Auto-Scroll ON" : "⏸ Auto-Scroll PAUSED";

    public List<string> AvailableLevelFilters { get; } = ["ALL LEVELS", "INFO", "WARN", "ERROR", "DEBUG"];

    public List<string> AvailableServiceFilters { get; } =
    [
        "ALL SERVICES",
        "KONGOR",
        "CHAT SERVER",
        "GAME CLIENT",
        "DATABASE",
        "REDIS CACHE",
        "DOCKER STACK",
        "ASPIRE HOST"
    ];

    public void Dispose()
    {
        _orchestrator.OnLogReceived -= HandleNewLog;
    }

    partial void OnAutoScrollChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoScrollButtonText));
    }

    partial void OnMaskPathsChanged(bool value)
    {
        OnPropertyChanged(nameof(MaskPathsButtonText));
        foreach (LogMessage log in _allLogs) log.IsRevealed = !value;
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

    partial void OnSelectedServiceFilterChanged(string value)
    {
        _ = value;
        RefreshFilteredLogs();
    }

    private void HandleNewLog(LogMessage log)
    {
        if (Dispatcher.UIThread.CheckAccess() && _incomingLogQueue.IsEmpty)
        {
            ProcessSingleLog(log);
        }
        else
        {
            _incomingLogQueue.Enqueue(log);
            if (Interlocked.CompareExchange(ref _isDrainScheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(DrainBatchOnUIThread);
            }
        }
    }

    public void FlushPendingLogs()
    {
        if (_incomingLogQueue.IsEmpty) return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            DrainBatchOnUIThread();
        }
        else
        {
            Dispatcher.UIThread.Invoke(DrainBatchOnUIThread);
        }
    }

    private void ProcessSingleLog(LogMessage log)
    {
        log.IsRevealed = !MaskPaths;
        _allLogs.Add(log);

        if (MatchesFilter(log))
        {
            FilteredLogs.Add(log);
            if (FilteredLogs.Count > 1500)
            {
                TrimPreservingErrors(FilteredLogs, 1500);
            }
        }

        if (_allLogs.Count > 10500)
        {
            TrimListPreservingErrors(_allLogs, 10000);
        }

        LogCount = FilteredLogs.Count;
        UpdateErrorState();
    }

    private void DrainBatchOnUIThread()
    {
        try
        {
            bool stateChanged = false;
            int count = 0;

            while (_incomingLogQueue.TryDequeue(out LogMessage? log) && count < 200)
            {
                count++;
                log.IsRevealed = !MaskPaths;
                _allLogs.Add(log);

                if (MatchesFilter(log))
                {
                    FilteredLogs.Add(log);
                    stateChanged = true;
                }
            }

            if (FilteredLogs.Count > 1500)
            {
                TrimPreservingErrors(FilteredLogs, 1500);
                stateChanged = true;
            }

            if (_allLogs.Count > 10500)
            {
                TrimListPreservingErrors(_allLogs, 10000);
            }

            if (stateChanged)
            {
                LogCount = FilteredLogs.Count;
                UpdateErrorState();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isDrainScheduled, 0);

            if (!_incomingLogQueue.IsEmpty && Interlocked.CompareExchange(ref _isDrainScheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(DrainBatchOnUIThread);
            }
        }
    }

    private static void TrimPreservingErrors(ObservableCollection<LogMessage> collection, int targetCapacity)
    {
        int toRemove = collection.Count - targetCapacity;
        if (toRemove <= 0) return;

        // Pass 1: Remove oldest non-error logs first so that critical errors are preserved for diagnostics
        for (int i = 0; i < collection.Count && toRemove > 0;)
        {
            if (!IsErrorLog(collection[i]))
            {
                collection.RemoveAt(i);
                toRemove--;
            }
            else
            {
                i++;
            }
        }

        // Pass 2: Fallback if all items are errors
        while (collection.Count > targetCapacity && toRemove > 0)
        {
            collection.RemoveAt(0);
            toRemove--;
        }
    }

    private static void TrimListPreservingErrors(List<LogMessage> list, int targetCapacity)
    {
        int toRemove = list.Count - targetCapacity;
        if (toRemove <= 0) return;

        for (int i = 0; i < list.Count && toRemove > 0;)
        {
            if (!IsErrorLog(list[i]))
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
    public void ToggleAutoScroll()
    {
        AutoScroll = !AutoScroll;
    }

    [RelayCommand]
    public void ToggleGlobalPathMasking()
    {
        MaskPaths = !MaskPaths;
    }

    [RelayCommand]
    public void ToggleLogReveal(LogMessage? log)
    {
        log?.IsRevealed = !log.IsRevealed;
    }

    [RelayCommand]
    public void ClearLogs()
    {
        _orchestrator.ClearLogs();
        while (_incomingLogQueue.TryDequeue(out _)) { }
        _allLogs.Clear();
        FilteredLogs.Clear();
        LogCount = 0;
        UpdateErrorState();
    }

    private void UpdateErrorState()
    {
        int errors = _allLogs.Count(IsErrorLog);
        ErrorCount = errors;
        HasErrors = errors > 0;

        if (HasErrors)
        {
            ErrorBannerText = "➔ EACH ERROR IS ONE STEP CLOSER";
            ErrorBannerForeground = "#FF4D6D";
            ErrorBannerBackground = "#2B0B13";
            ErrorBannerBorder = "#EF4444";
        }
        else
        {
            ErrorBannerText = "➔ 100% ERROR FREE - A WINNER IS YOU!";
            ErrorBannerForeground = "#22C55E";
            ErrorBannerBackground = "#052E16";
            ErrorBannerBorder = "#10B981";
        }
    }

    private static bool IsErrorLog(LogMessage log)
    {
        return log.IsError ||
               string.Equals(log.Level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(log.Level, "FATAL", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public async Task CopyLogsAsync()
    {
        string text = GetLogsAsPlainText();
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: { } clipboard
                })
            {
                // Copies raw full unmasked text to clipboard so debugging is unaffected
                await clipboard.SetTextAsync(text);
                ShowNotification($"Copied {FilteredLogs.Count} raw log lines (with full paths) to clipboard!");
            }
        }
        catch
        {
            // Clipboard fallback
        }
    }

    public string GetLogsAsPlainText()
    {
        StringBuilder sb = new();
        foreach (LogMessage log in FilteredLogs)
            // Uses raw full unmasked text
            sb.AppendLine(log.DisplayText);

        return sb.ToString();
    }

    private void ShowNotification(string message)
    {
        CopyNotificationText = message;
        IsCopyNotificationVisible = true;

        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            IsCopyNotificationVisible = false;
            timer.Stop();
        };
        timer.Start();
    }

    private void RefreshFilteredLogs()
    {
        FilteredLogs.Clear();
        foreach (LogMessage log in _allLogs.Where(MatchesFilter)) FilteredLogs.Add(log);

        LogCount = FilteredLogs.Count;
    }

    private bool MatchesFilter(LogMessage log)
    {
        return (SelectedLevelFilter == "ALL LEVELS" ||
                string.Equals(log.Level, SelectedLevelFilter, StringComparison.OrdinalIgnoreCase)) &&
               (SelectedServiceFilter == "ALL SERVICES" ||
                string.Equals(log.ServiceName, SelectedServiceFilter, StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrWhiteSpace(SearchText) ||
                log.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                log.ServiceName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }
}
