// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TimelineScrubberControl.axaml.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ReplayStudio.GUI.ViewModels;

namespace ReplayStudio.GUI.Controls;

public partial class TimelineScrubberControl : UserControl
{
    public TimelineScrubberControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is TimelineViewModel vm && vm.TotalDurationMs > 0)
        {
            if (sender is Control control)
            {
                double x = e.GetPosition(control).X;
                double width = control.Bounds.Width;
                if (width > 0)
                {
                    double fraction = Math.Clamp(x / width, 0.0, 1.0);
                    vm.SeekFraction(fraction);
                }
            }
        }
    }

    private void OnTrackPointerMoved(object? sender, PointerEventArgs e)
    {
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed && DataContext is TimelineViewModel vm && vm.TotalDurationMs > 0)
        {
            if (sender is Control control)
            {
                double x = e.GetPosition(control).X;
                double width = control.Bounds.Width;
                if (width > 0)
                {
                    double fraction = Math.Clamp(x / width, 0.0, 1.0);
                    vm.SeekFraction(fraction);
                }
            }
        }
    }
}
