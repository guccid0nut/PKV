// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;
using WILLOWMAKER.Core;

namespace WILLOWMAKER;

public class WILLOWMAKER
{
    [STAThread]
    public static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        // Strategic Initiative [SR-06]: Clean up leftover *.old shadow files from previous self-updates
        try
        {
            global::WILLOWMAKER.Core.Services.VersionChecker.CleanupPendingOldFiles();
        }
        catch { }

        // Silently register this installation/workspace in ~/.kongor/config.json (never mutates PATH or shell profiles)
        _ = Task.Run(() =>
        {
            try
            {
                if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
                {
                    Kongor.Core.Configuration.KongorUserConfigStore.RegisterOrUpdateWorkspace(
                        ws.Root,
                        ws.Kind,
                        setActive: false,
                        diagnosticLogger: msg => System.Diagnostics.Debug.WriteLine(msg));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WILLOWMAKER] Workspace auto-registration skipped: {ex.Message}");
            }
        });

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<ClientLauncher>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { UseDBusMenu = false })
            .WithInterFont()
            .LogToTrace();
    }
}
