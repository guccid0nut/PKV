// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ============================================================================
// File: MainWindow.axaml.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReplayStudio.GUI.ViewModels;
using ReplayStudio.GUI.Views;

namespace ReplayStudio.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            ViewportView? viewportView = this.FindControl<ViewportView>("ViewportViewInstance");
            if (viewportView?.ViewportControl != null)
            {
                viewportView.ViewportControl.AttachPipeline(
                    viewportView.ViewportControl.BufferRing ?? new Common.NativeBufferRing(1280, 720),
                    vm.CameraDirector);
            }
        }
    }
}
