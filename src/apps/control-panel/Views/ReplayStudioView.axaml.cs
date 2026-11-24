// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.ViewModels;

namespace NEXUS.ControlPanel.GUI.Views;

public partial class ReplayStudioView : UserControl
{
    public ReplayStudioView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, DropFile);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private async void DropFile(object? sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.Contains(DataFormats.Files)) return;

            IEnumerable<IStorageItem>? items = e.Data.GetFiles();
            if (items == null) return;

            IStorageItem? first = items.FirstOrDefault();
            if (first != null && (first.Path.LocalPath.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase) ||
                                  first.Path.LocalPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                  first.Path.LocalPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            {
                if (DataContext is ReplayStudioViewModel vm)
                {
                    await vm.LoadReplayFileAsync(first.Path.LocalPath);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReplayStudioView] DropFile unhandled error: {ex}");
            System.Diagnostics.Trace.WriteLine($"[ReplayStudioView] DropFile unhandled error: {ex}");
        }
    }

    public async void OnBrowseReplayClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            IStorageFolder? startFolder = null;
            try
            {
                string primaryDir = ReplayRenderManagerService.GetPrimaryReplayDirectory();
                if (Directory.Exists(primaryDir))
                {
                    startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(primaryDir);
                }
            }
            catch (Exception folderEx)
            {
                System.Diagnostics.Trace.WriteLine($"[ReplayStudioView] Unable to resolve initial replay folder: {folderEx.Message}");
            }

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Heroes of Newerth Replay File",
                AllowMultiple = false,
                SuggestedStartLocation = startFolder,
                FileTypeFilter =
                [
                    new FilePickerFileType("HoN Replay Archives (*.honreplay;*.zip;*.log)")
                    {
                        Patterns = ["*.honreplay", "*.zip", "*.log"]
                    }
                ]
            });

            if (files.Count > 0 && DataContext is ReplayStudioViewModel vm)
            {
                await vm.LoadReplayFileAsync(files[0].Path.LocalPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReplayStudioView] OnBrowseReplayClicked unhandled error: {ex}");
            System.Diagnostics.Trace.WriteLine($"[ReplayStudioView] OnBrowseReplayClicked unhandled error: {ex}");
        }
    }
}
