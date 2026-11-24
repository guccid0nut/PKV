// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ViewportView.axaml.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReplayStudio.GUI.Controls;
using ReplayStudio.GUI.ViewModels;

namespace ReplayStudio.GUI.Views;

public partial class ViewportView : UserControl
{
    public VulkanViewportControl? ViewportControl => this.FindControl<VulkanViewportControl>("ViewportHost");

    public ViewportView()
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
        if (DataContext is ViewportViewModel vm && ViewportControl != null)
        {
            ViewportControl.CameraDirector = vm.Director;
        }
    }
}
