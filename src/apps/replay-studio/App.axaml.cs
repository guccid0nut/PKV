// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ============================================================================
// File: App.axaml.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using ReplayStudio.GUI.Views;

namespace ReplayStudio.GUI;

public partial class App : Application
{
    public static ReplayStudioCliOptions CliOptions { get; set; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ReplaySnapshotEngine snapshotEngine = new();
            PlaybackClock clock = new();
            MapEnvironmentManager mapManager = new();
            CameraDirector cameraDirector = new();
            LightingController lightingController = new();
            ReplayStudioService service = new(snapshotEngine, clock, mapManager);

            MainViewModel mainVm = new(service, clock, cameraDirector, lightingController);

            // Apply CLI options
            if (!string.IsNullOrWhiteSpace(CliOptions.ReplayPath))
            {
                _ = mainVm.LoadReplayFileAsync(CliOptions.ReplayPath);
            }
            else
            {
                mainVm.LoadDemoMatch();
            }

            mainVm.ViewportVM.SetCameraMode(CliOptions.InitialCameraMode);

            if (CliOptions.InitialTimestampSec > 0)
            {
                mainVm.TimelineVM.Seek(CliOptions.InitialTimestampSec * 1000);
            }

            if (CliOptions.TargetHeroSlot.HasValue && CliOptions.TargetHeroSlot.Value >= 0 && CliOptions.TargetHeroSlot.Value < 10)
            {
                mainVm.ViewportVM.FocusHero(CliOptions.TargetHeroSlot.Value);
            }

            if (Math.Abs(CliOptions.PlaybackSpeed - 1.0f) > 0.01f)
            {
                mainVm.TimelineVM.SetSpeed(CliOptions.PlaybackSpeed);
            }

            if (CliOptions.AutoPlay)
            {
                mainVm.TimelineVM.Play();
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
