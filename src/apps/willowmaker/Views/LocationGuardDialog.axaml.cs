// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Views;

public partial class LocationGuardDialog : Window
{
    private readonly string _sourceDirectory;
    private readonly string? _targetDirectoryOverride;
    private readonly bool _launchOnRelocate;

    /// <summary>
    ///     Controls whether closing this terminal guard dialog exits the process (true in production runtime,
    ///     set to false in headless GUI tests so the xUnit host is never terminated).
    /// </summary>
    public bool ExitProcessOnClose { get; set; } = true;

    public LocationGuard.RelocationResult? LastRelocationResult { get; private set; }

    public LocationGuardDialog()
    {
        InitializeComponent();
        _sourceDirectory = Environment.CurrentDirectory;
        _launchOnRelocate = true;
        RelocationTargetText.Text = LocationGuard.ResolveRecommendedRelocationDirectory(_sourceDirectory);
    }

    public LocationGuardDialog(
        string message,
        string reason,
        IReadOnlyList<string> foreignEntries,
        string? sourceDirectory = null,
        string? targetDirectoryOverride = null,
        bool launchOnRelocate = true) : this()
    {
        _sourceDirectory = string.IsNullOrWhiteSpace(sourceDirectory) ? Environment.CurrentDirectory : sourceDirectory;
        _targetDirectoryOverride = targetDirectoryOverride;
        _launchOnRelocate = launchOnRelocate;
        MessageText.Text = message;
        ReasonText.Text = reason;
        RelocationTargetText.Text = string.IsNullOrWhiteSpace(_targetDirectoryOverride)
            ? LocationGuard.ResolveRecommendedRelocationDirectory(_sourceDirectory)
            : _targetDirectoryOverride;

        if (foreignEntries.Count is 0)
        {
            ForeignEntriesHeading.IsVisible = false;
            ForeignEntriesScroll.IsVisible = false;
        }
        else
        {
            ForeignEntriesText.Text = string.Join(Environment.NewLine,
                LocationGuard.ApplyForeignEntriesDisplayCap(foreignEntries));
        }
    }

    public LocationGuard.RelocationResult ExecuteSelfRelocate()
    {
        LocationGuard.RelocationResult result = LocationGuard.RelocateToDedicatedFolder(
            _sourceDirectory,
            _targetDirectoryOverride,
            launchRelocated: _launchOnRelocate);

        LastRelocationResult = result;
        RelocationStatusText.IsVisible = true;
        RelocationStatusText.Text = result.Message;
        RelocationStatusText.Foreground = result.Succeeded
            ? Avalonia.Media.Brush.Parse("#34D399")
            : Avalonia.Media.Brush.Parse("#F87171");

        if (result.Succeeded)
        {
            SelfRelocateButton.IsEnabled = false;
            SelfRelocateButton.Content = "Relocated ✓";
        }

        return result;
    }

    private void SelfRelocate_Click(object? sender, RoutedEventArgs arguments)
    {
        LocationGuard.RelocationResult result = ExecuteSelfRelocate();
        if (result.Succeeded && _launchOnRelocate)
        {
            Close();
        }
    }

    private void OK_Click(object? sender, RoutedEventArgs arguments)
    {
        Close();
    }

    // The Guard Dialog Terminates The Process On Any Close Path: Exit Button, Title-Bar Close, Alt+F4, Escape Button, Or Programmatic Close
    // This Ensures The User Cannot Dismiss The Dialog And Then Interact With The Main Window Underneath
    protected override void OnClosed(EventArgs arguments)
    {
        base.OnClosed(arguments);

        if (ExitProcessOnClose)
        {
            Environment.Exit(0);
        }
    }
}
