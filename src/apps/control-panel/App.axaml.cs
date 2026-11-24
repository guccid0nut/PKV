// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.ViewModels;
using NEXUS.ControlPanel.GUI.Views;

namespace NEXUS.ControlPanel.GUI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                string debugLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gui_debug.log");
                void AppLog(string msg)
                {
                    try { File.AppendAllText(debugLog, $"[{TimeProvider.System.GetUtcNow():O}] [APP] {msg}{Environment.NewLine}"); } catch { }
                }

                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    AppLog($"[UI THREAD UNHANDLED EXCEPTION] {e.Exception}");
                };

                Console.WriteLine("[ControlPanel] Initializing services...");
                ISecretsManagerService secretsManager = new SecretsManagerService();
                IDockerHealthService dockerHealth = new DockerHealthService();
                IDatabaseHealthService databaseHealth = new DatabaseHealthService();
                IPortScannerService portScanner = new PortScannerService();
                GameClientAssetService assetService = new(secretsManager);
                NexusOrchestratorService orchestrator = new(secretsManager, dockerHealth);

                // Asynchronously ensure 3D viewport models and voicelines are extracted from client
                _ = assetService.EnsureAssetsExtractedAsync();

                Console.WriteLine("[ControlPanel] Creating MainViewModel...");
                MainViewModel mainViewModel = new(
                    orchestrator,
                    secretsManager,
                    dockerHealth,
                    portScanner,
                    databaseHealth,
                    assetService: assetService);

                bool noActivate = string.Equals(
                    Environment.GetEnvironmentVariable("KONGOR_NO_ACTIVATE"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);

                Console.WriteLine("[ControlPanel] Creating MainWindow...");
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainViewModel,
                    WindowState = WindowState.Normal,
                    ShowActivated = !noActivate
                };
                desktop.MainWindow.Opened += (_, _) =>
                {
                    AppLog("MainWindow.Opened fired");
                    if (string.Equals(Environment.GetEnvironmentVariable("KONGOR_AUTO_START_CLUSTER"), "1", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = orchestrator.StartClusterAsync(withGameServers: string.Equals(Environment.GetEnvironmentVariable("KONGOR_AUTO_START_COMPEL"), "1", StringComparison.OrdinalIgnoreCase));
                    }
                };
                desktop.MainWindow.Closed += (_, _) => AppLog($"MainWindow.Closed fired! Stack={Environment.StackTrace}");
                desktop.MainWindow.Show();
                if (!noActivate)
                {
                    desktop.MainWindow.Activate();
                }
                Console.WriteLine($"[ControlPanel] MainWindow created and shown (noActivate={noActivate}).");
                AppLog($"MainWindow shown (noActivate={noActivate})");

                // Unowned top-level Pop-Out Game Server Chat window coordinator (R8)
                GameServerChatPopOutWindow? popOutChatWindow = null;

                void OpenOrFocusPopOutChatWindow()
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (popOutChatWindow is not null)
                        {
                            if (popOutChatWindow.WindowState == WindowState.Minimized)
                            {
                                popOutChatWindow.WindowState = WindowState.Normal;
                            }
                            popOutChatWindow.Activate();
                            AppLog("[POPOUT-CHAT] Focused existing GameServerChatPopOutWindow.");
                            return;
                        }

                        popOutChatWindow = new GameServerChatPopOutWindow
                        {
                            DataContext = mainViewModel.GameServers
                        };
                        popOutChatWindow.Closed += (_, _) =>
                        {
                            popOutChatWindow = null;
                            mainViewModel.GameServers.NotifyPopOutWindowClosed();
                            AppLog("[POPOUT-CHAT] GameServerChatPopOutWindow closed and re-docked.");
                        };
                        popOutChatWindow.Show();
                        popOutChatWindow.Activate();
                        AppLog("[POPOUT-CHAT] Opened new unowned top-level GameServerChatPopOutWindow.");
                    });
                }

                void ClosePopOutChatWindowSafe(bool focusMainWindow)
                {
                    void CloseOnUi()
                    {
                        if (popOutChatWindow is not null)
                        {
                            GameServerChatPopOutWindow win = popOutChatWindow;
                            popOutChatWindow = null;
                            try
                            {
                                win.Close();
                                AppLog("[POPOUT-CHAT] Closed GameServerChatPopOutWindow cleanly.");
                            }
                            catch (Exception closeEx)
                            {
                                AppLog($"[POPOUT-CHAT] Close notice: {closeEx.Message}");
                            }
                        }

                        if (focusMainWindow && desktop.MainWindow is not null)
                        {
                            mainViewModel.SelectedNavigationIndex = 5;
                            if (desktop.MainWindow.WindowState == WindowState.Minimized)
                            {
                                desktop.MainWindow.WindowState = WindowState.Normal;
                            }
                            desktop.MainWindow.Activate();
                        }
                    }

                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        CloseOnUi();
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(CloseOnUi);
                    }
                }

                mainViewModel.GameServers.ConfigureChatPopOutHost(
                    onRequestPopOutChat: OpenOrFocusPopOutChatWindow,
                    onRequestDockChat: () => ClosePopOutChatWindowSafe(focusMainWindow: true));

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    AppLog($"[UNHANDLED EXCEPTION] {e.ExceptionObject}");
                };
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    AppLog($"[UNOBSERVED TASK EXCEPTION] {e.Exception}");
                };

                int isShuttingDown = 0;
                int shutdownCompleted = 0;

                void PerformGracefulExit(string reason)
                {
                    AppLog($"PerformGracefulExit called with reason: {reason}");
                    if (Interlocked.Exchange(ref isShuttingDown, 1) == 0)
                    {
                        ClosePopOutChatWindowSafe(focusMainWindow: false);
                        Task.Run(async () =>
                        {
                            // Start quit audio concurrently so orchestrator teardown begins immediately without audio blocking
                            Task soundTask = Task.Run(() =>
                            {
                                try
                                {
                                    SoundEffectService.PlayBanjoQuitSync();
                                }
                                catch (Exception soundEx)
                                {
                                    AppLog($"[SHUTDOWN] Quit audio notice: {soundEx.Message}");
                                }
                            });

                            try
                            {
                                AppLog("[SHUTDOWN] Disposing view model timers and stopping orchestrated services via orchestrator.StopAllAsync()...");
                                try { mainViewModel.Dispose(); } catch (Exception vmEx) { AppLog($"[SHUTDOWN] ViewModel dispose notice: {vmEx.Message}"); }
                                await orchestrator.StopAllAsync().ConfigureAwait(false);
                                orchestrator.Dispose();
                                try { (dockerHealth as IDisposable)?.Dispose(); } catch (Exception dockerEx) { AppLog($"[SHUTDOWN] DockerHealth dispose notice: {dockerEx.Message}"); }
                                AppLog("[SHUTDOWN] Orchestrated services stopped cleanly.");
                            }
                            catch (Exception stopEx)
                            {
                                AppLog($"[SHUTDOWN] Error during orchestrator.StopAllAsync(): {stopEx}");
                            }

                            try
                            {
                                await soundTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            }
                            catch (Exception waitEx)
                            {
                                AppLog($"[SHUTDOWN] Quit audio wait notice: {waitEx.Message}");
                            }

                            Interlocked.Exchange(ref shutdownCompleted, 1);
                            Dispatcher.UIThread.Post(() => desktop.Shutdown());
                        });
                    }
                }

                void HideMainWindowSafe()
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        desktop.MainWindow?.Hide();
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() => desktop.MainWindow?.Hide());
                    }
                }

                void PerformDetachExit(string reason)
                {
                    AppLog($"PerformDetachExit called with reason: {reason}");
                    if (Interlocked.Exchange(ref isShuttingDown, 1) == 0)
                    {
                        ClosePopOutChatWindowSafe(focusMainWindow: false);
                        HideMainWindowSafe();
                        try
                        {
                            mainViewModel.Dispose();
                            orchestrator.DetachForBackgroundExit();
                            (dockerHealth as IDisposable)?.Dispose();
                            AppLog("[DETACH-EXIT] Detached background monitors cleanly; Docker containers and companion apps preserved.");
                        }
                        catch (Exception detachEx)
                        {
                            AppLog($"[DETACH-EXIT] Error during DetachForBackgroundExit: {detachEx}");
                        }

                        Interlocked.Exchange(ref shutdownCompleted, 1);
                        Dispatcher.UIThread.Post(() => desktop.Shutdown());
                    }
                }

                mainViewModel.OnRequestDetachExit += reason => PerformDetachExit(reason);

                mainViewModel.OnRequestFullShutdownExit += reason =>
                {
                    HideMainWindowSafe();
                    PerformGracefulExit(reason);
                };

                DashboardViewModel.OnRequestAppExit += () =>
                {
                    Console.WriteLine("[ControlPanel] DashboardViewModel.OnRequestAppExit triggered");
                    if (Volatile.Read(ref isShuttingDown) == 0)
                    {
                        desktop.MainWindow?.Hide();
                        PerformGracefulExit("DashboardRequest");
                    }
                };

                desktop.MainWindow.Closing += (sender, e) =>
                {
                    Console.WriteLine("[ControlPanel] MainWindow.Closing event fired");
                    AppLog($"MainWindow.Closing fired (CloseReason={e.CloseReason}, IsProgrammatic={e.IsProgrammatic}, isShuttingDown={Volatile.Read(ref isShuttingDown)}, shutdownCompleted={Volatile.Read(ref shutdownCompleted)})");
                    if (Volatile.Read(ref shutdownCompleted) != 0)
                    {
                        return;
                    }

                    e.Cancel = true;
                    if (Volatile.Read(ref isShuttingDown) != 0)
                    {
                        return;
                    }

                    if (e.CloseReason == WindowCloseReason.OSShutdown)
                    {
                        desktop.MainWindow?.Hide();
                        PerformGracefulExit("OSShutdown");
                        return;
                    }

                    Dispatcher.UIThread.Post(() => _ = mainViewModel.RequestWindowCloseAsync());
                };

                desktop.ShutdownRequested += (_, e) =>
                {
                    Console.WriteLine("[ControlPanel] desktop.ShutdownRequested event fired");
                    AppLog($"desktop.ShutdownRequested fired (isShuttingDown={Volatile.Read(ref isShuttingDown)}, shutdownCompleted={Volatile.Read(ref shutdownCompleted)})");
                    if (Volatile.Read(ref shutdownCompleted) == 0)
                    {
                        e.Cancel = true;
                        desktop.MainWindow?.Hide();
                        PerformGracefulExit("ShutdownRequested");
                    }
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ControlPanel ERROR] Framework initialization failed: {ex.Message}\n{ex.StackTrace}");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
