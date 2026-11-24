// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.IO;
using Avalonia;

namespace NEXUS.ControlPanel.GUI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gui_debug.log");
        void Log(string msg)
        {
            try { File.AppendAllText(logPath, $"[{TimeProvider.System.GetUtcNow():O}] {msg}{Environment.NewLine}"); } catch { }
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log($"[FATAL UNHANDLED DOMAIN EXCEPTION] {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log($"[UNOBSERVED TASK EXCEPTION] {e.Exception}");
        };

        Log("[GUI STARTING]");
        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            Log("[GUI EXITED NORMALLY]");
        }
        catch (Exception ex)
        {
            Log($"[GUI CRASHED IN MAIN] {ex}");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { UseDBusMenu = false })
            .WithInterFont()
            .With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software
                ],
                CompositionMode =
                [
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.LowLatencyDxgiSwapChain,
                    Win32CompositionMode.RedirectionSurface
                ]
            })
            .LogToTrace();
    }
}
