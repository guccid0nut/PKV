// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.CompilerServices;
using NEXUS.Contracts.Cdn;

namespace WILLOWMAKER.Tests;

/// <summary>
///     Sandboxes the WillowMaker test run away from the developer's REAL game client.
///     <para>
///         Setting view-model properties in tests (e.g. <c>new MainViewModel { SelectedRefreshRate = "360 Hz" }</c>) fires the
///         settings-change handlers, which call <c>WriteCustomConfiguration()</c> / <c>EnsureVulkanDxvkConfigured()</c>
///         WITHOUT a directory. Those resolve the client via <c>LocationGuard.ResolveGameClientDirectory</c>, which walks up
///         from the test bin folder to the repository's <c>games/client</c> — i.e. the real install — and would rewrite its
///         autoexec.cfg / dxvk.conf. (Before the CWD-fallback fix they instead littered the current directory / repo root.)
///     </para>
///     <para>
///         <c>HON_GAME_PATH</c> is the highest-priority resolver input, so pointing it at a throw-away folder with a dummy
///         client executable keeps every implicit write inside the sandbox.
///     </para>
/// </summary>
public static class WillowmakerTestSandboxInitializer
{
    public static string SandboxClientDirectory { get; private set; } = string.Empty;

    [ModuleInitializer]
    public static void Initialize()
    {
        SandboxClientDirectory = Path.Combine(Path.GetTempPath(), "WILLOWMAKER_TestSandbox_" + Environment.ProcessId, "games", "client");
        Directory.CreateDirectory(SandboxClientDirectory);

        string dummyExecutable = Path.Combine(SandboxClientDirectory, DeploymentManifest.HeroesOfNewerthExecutableFileName);
        if (!File.Exists(dummyExecutable))
            File.WriteAllBytes(dummyExecutable, []);

        Environment.SetEnvironmentVariable("HON_GAME_PATH", SandboxClientDirectory);
        _ = new Avalonia.Controls.ComboBoxItem { Content = "warmup" };
        _ = new Avalonia.Controls.ComboBox();
        Console.Error.WriteLine($"[WILLOWMAKER.Tests] HON_GAME_PATH sandboxed to '{SandboxClientDirectory}' (protects the real games/client install).");
    }
}
