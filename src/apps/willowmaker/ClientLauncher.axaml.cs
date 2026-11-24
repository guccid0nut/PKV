// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core;

public class ClientLauncher : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainViewModel viewModel = new();

            MainWindow mainWindow = new()
            {
                DataContext = viewModel
            };

            viewModel.RequestBringToFront = () =>
            {
                void FocusWindow()
                {
                    if (mainWindow.WindowState == WindowState.Minimized)
                    {
                        mainWindow.WindowState = WindowState.Normal;
                    }

                    if (!mainWindow.IsVisible)
                    {
                        mainWindow.Show();
                    }

                    bool prevTopmost = mainWindow.Topmost;
                    mainWindow.Topmost = true;
                    mainWindow.Activate();
                    mainWindow.Focus();
                    mainWindow.Topmost = prevTopmost;
                }

                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    FocusWindow();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(FocusWindow);
                }

                return true;
            };

            // "Window.Opened" Fires Once The Main Window Is Realised And Able To Host UI Elements (Such As Modal Dialogs)
            // This Is The Trigger Which Signals That Start-Up Events With UI Elements Owned By The Main Window Can Start To Execute
            mainWindow.Opened += (_, _) => viewModel.OnMainWindowOpened();
            mainWindow.Closed += (_, _) => viewModel.DisposeApplicationBackgroundServices();
            desktop.Exit += (_, _) => viewModel.DisposeApplicationBackgroundServices();

            desktop.MainWindow = mainWindow;
        }

        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
