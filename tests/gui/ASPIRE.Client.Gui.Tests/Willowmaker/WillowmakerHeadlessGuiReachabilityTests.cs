// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FluentAssertions;
using WILLOWMAKER.Core;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.ViewModels;
using WILLOWMAKER.Core.Views;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.Willowmaker;

/// <summary>
///     Shared process-wide Avalonia.Headless 11.2.3 initialization fixture running on a dedicated UI thread
///     so parallel xUnit test classes can safely build real XAML visual trees, compiled bindings, layout passes,
///     and modal overlays without violating Avalonia's single-UI-thread affinity invariant.
/// </summary>
public sealed class AvaloniaHeadlessAppFixture
{
    private static readonly object InitLock = new();
    private static readonly BlockingCollection<Action> UiWorkQueue = new();
    private static readonly ManualResetEventSlim UiReady = new(false);
    private static Exception? _initException;
    private static bool _started;
    private static int _uiThreadId;

    public AvaloniaHeadlessAppFixture()
    {
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (!_started)
            {
                _started = true;
                Thread uiThread = new(() =>
                {
                    _uiThreadId = Environment.CurrentManagedThreadId;
                    try
                    {
                        if (Application.Current is null)
                        {
                            AppBuilder.Configure<ClientLauncher>()
                                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                                {
                                    UseHeadlessDrawing = true
                                })
                                .SetupWithoutStarting();
                        }
                    }
                    catch (Exception ex)
                    {
                        _initException = ex;
                    }
                    finally
                    {
                        UiReady.Set();
                    }

                    foreach (Action work in UiWorkQueue.GetConsumingEnumerable())
                    {
                        work();
                    }
                })
                {
                    IsBackground = true,
                    Name = "AvaloniaHeadlessTestUiThread"
                };

                if (OperatingSystem.IsWindows())
                {
                    uiThread.SetApartmentState(ApartmentState.STA);
                }

                uiThread.Start();
            }
        }

        UiReady.Wait();
        if (_initException is not null)
        {
            ExceptionDispatchInfo.Capture(_initException).Throw();
        }
    }

    public static void RunOnUiThread(Action action)
    {
        EnsureInitialized();
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            action();
            return;
        }

        using ManualResetEventSlim done = new(false);
        Exception? captured = null;
        UiWorkQueue.Add(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}

public sealed class WillowmakerHeadlessGuiReachabilityTests(AvaloniaHeadlessAppFixture fixture)
    : IClassFixture<AvaloniaHeadlessAppFixture>
{
    private readonly AvaloniaHeadlessAppFixture _fixture = fixture;

    [Fact]
    public void MainView_HeadlessVisualTree_MountsDirectPlayModManagerAndAdvancedDrawer_WithZeroKrispText()
    {
        // protects: Real Avalonia.Headless XAML reachability of WILLOWMAKER MainView, DirectPlayView, ModManagerView,
        //           Advanced drawer toggle, and removal of trademarked "Powered by Krisp" wording.
        // fails_when: MainView XAML fails to load, DirectPlay/ModManager overlays are unmounted, Advanced drawer
        //             is missing/visible by default, or any TextBlock still mentions "Krisp".
        // seam: Avalonia.Headless AppBuilder + MainWindow/MainView real visual tree & measure/arrange pass.
        _ = _fixture;
        AvaloniaHeadlessAppFixture.RunOnUiThread(() =>
        {
            MainViewModel vm = new();
            Window hostWindow = new()
            {
                Width = 1280,
                Height = 800,
                DataContext = vm,
                Content = new MainView { DataContext = vm }
            };

            try
            {
                hostWindow.Show();
                hostWindow.UpdateLayout();

                MainView mainView = hostWindow.Content.Should().BeOfType<MainView>().Subject;

                // 1. DirectPlayView and ModManagerView MUST be mounted inside MainView visual tree
                DirectPlayView? directPlay = mainView.FindControl<DirectPlayView>("DirectPlayViewControl");
                ModManagerView? modManager = mainView.FindControl<ModManagerView>("ModManagerViewControl");
                directPlay.Should().NotBeNull("DirectPlayView must be mounted and reachable from MainView");
                modManager.Should().NotBeNull("ModManagerView must be mounted and reachable from MainView");

                // 2. Quick-access buttons: DirectPlay stays visible; Mod Manager button is hidden
                //    because all client mods are now integrated directly into HoN's in-game settings.
                Button? openDirectPlayBtn = mainView.FindControl<Button>("OpenDirectPlayButton");
                Button? openModManagerBtn = mainView.FindControl<Button>("OpenModManagerButton");
                openDirectPlayBtn.Should().NotBeNull();
                openDirectPlayBtn!.IsVisible.Should().BeTrue("DirectPlay P2P quick-access button must remain visible");
                openModManagerBtn.Should().NotBeNull();
                openModManagerBtn!.IsVisible.Should().BeFalse("Mod Manager quick-access button must be hidden now that mods live in HoN in-game settings");

                Grid? directPlayOverlay = mainView.FindControl<Grid>("DirectPlayOverlay");
                Grid? modManagerOverlay = mainView.FindControl<Grid>("ModManagerOverlay");
                directPlayOverlay.Should().NotBeNull();
                modManagerOverlay.Should().NotBeNull();
                directPlayOverlay!.IsVisible.Should().BeFalse();
                modManagerOverlay!.IsVisible.Should().BeFalse();

                vm.ToggleDirectPlayCommand.Execute(null);
                hostWindow.UpdateLayout();
                directPlayOverlay.IsVisible.Should().BeTrue();
                vm.CloseDirectPlayCommand.Execute(null);

                vm.ToggleModManagerCommand.Execute(null);
                hostWindow.UpdateLayout();
                modManagerOverlay.IsVisible.Should().BeTrue();
                vm.CloseModManagerCommand.Execute(null);

                // 3. Advanced settings drawer starts collapsed and expands on toggle
                StackPanel? advancedGraphics = mainView.FindControl<StackPanel>("AdvancedGraphicsDrawer");
                advancedGraphics.Should().NotBeNull();
                advancedGraphics!.IsVisible.Should().BeFalse();

                vm.ToggleAdvancedOptionsCommand.Execute(null);
                hostWindow.UpdateLayout();
                advancedGraphics.IsVisible.Should().BeTrue();

                // 4. Zero "Krisp" branding anywhere in rendered TextBlocks
                List<string> krispMatches = mainView.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(tb => tb.Text ?? string.Empty)
                    .Where(text => text.Contains("Krisp", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                krispMatches.Should().BeEmpty("trademarked 'Powered by Krisp' branding must be replaced with accurate Built-In DSP wording");
            }
            finally
            {
                hostWindow.Close();
                vm.DisposeApplicationBackgroundServices();
            }
        });
    }

    [Fact]
    public void LocationGuardDialog_HeadlessRender_SupportsOneClickSelfRelocateWithoutExitingTestHost()
    {
        // protects: LocationGuardDialog 1-click self-relocate UX and ExitProcessOnClose testability.
        // fails_when: SelfRelocateButton is missing or relocation fails to copy WILLOWMAKER payload safely.
        // seam: Avalonia.Headless LocationGuardDialog instantiation, ExecuteSelfRelocate, and Close().
        _ = _fixture;
        AvaloniaHeadlessAppFixture.RunOnUiThread(() =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"kongor_gui_reloc_{Guid.NewGuid():N}");
            string sourceDir = Path.Combine(tempRoot, "DownloadsLike");
            string targetDir = Path.Combine(tempRoot, "Relocated");
            Directory.CreateDirectory(sourceDir);

            try
            {
                string exeName = DeploymentManifest.ApplicationExecutableFileName;
                File.WriteAllText(Path.Combine(sourceDir, exeName), "willowmaker-payload");
                File.WriteAllText(Path.Combine(sourceDir, "family_photo.jpg"), "keep-untouched");

                LocationGuardDialog dialog = new(
                    message: "Unsafe folder detected.",
                    reason: "Foreign Entries Detected",
                    foreignEntries: ["family_photo.jpg"],
                    sourceDirectory: sourceDir,
                    targetDirectoryOverride: targetDir,
                    launchOnRelocate: false)
                {
                    ExitProcessOnClose = false
                };

                dialog.Show();
                dialog.UpdateLayout();

                Button? relocateBtn = dialog.FindControl<Button>("SelfRelocateButton");
                relocateBtn.Should().NotBeNull();
                relocateBtn!.IsEnabled.Should().BeTrue();

                LocationGuard.RelocationResult result = dialog.ExecuteSelfRelocate();
                result.Succeeded.Should().BeTrue();
                relocateBtn.IsEnabled.Should().BeFalse();
                File.Exists(Path.Combine(targetDir, exeName)).Should().BeTrue();
                File.Exists(Path.Combine(targetDir, "family_photo.jpg")).Should().BeFalse();
                File.Exists(Path.Combine(sourceDir, "family_photo.jpg")).Should().BeTrue();

                dialog.Close();
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        });
    }
}
