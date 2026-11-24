// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Configuration;
using NEXUS.Contracts.ControlPlane;
using NEXUS.Contracts.Domain;
using NEXUS.ControlPanel.GUI.Services;
using NEXUS.ControlPanel.GUI.Services.Models;
using NEXUS.ControlPanel.GUI.ViewModels;
using WILLOWMAKER.Core.ViewModels;
using Application = Avalonia.Application;
using Control = Avalonia.Controls.Control;

namespace Kongor.HeadlessRenderer;

/// <summary>
/// Headless Avalonia application used for offscreen rendering.
/// </summary>
public sealed class HeadlessKongorApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

/// <summary>
/// Result of a headless UI rendering operation.
/// </summary>
public sealed record UiRenderResult
{
    public bool Success { get; init; }
    public string ViewName { get; init; } = string.Empty;
    public string ResolvedType { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public string Theme { get; init; } = "dark";
    public string? ArtifactPath { get; init; }
    public string Base64Png { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Service interface for rendering Avalonia XAML views to PNG snapshots using Avalonia Headless and Skia.
/// </summary>
public interface IHeadlessUiRenderer
{
    Task<UiRenderResult> RenderViewAsync(
        string viewName,
        int width = 1280,
        int height = 720,
        string theme = "dark",
        bool mockData = true,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetSupportedViews();
}

/// <summary>
/// Avalonia Headless offscreen XAML view renderer using Skia RenderTargetBitmap.
/// Supports dynamic view resolution across NEXUS Control Panel and WILLOWMAKER launcher views,
/// dark/light ThemeVariant switching, layout measurement, and realistic mock ViewModel data injection.
/// </summary>
public sealed class HeadlessUiRenderer : IHeadlessUiRenderer
{
    private readonly ILogger<HeadlessUiRenderer> _logger;
    private readonly TimeProvider _timeProvider;
    private static readonly BlockingCollection<Action> _workQueue = new();
    private static Thread? _uiThread;
    private static readonly Lock _initLock = new();
    private static volatile bool _isInitialized;

    private static readonly Dictionary<string, Type> KnownViews = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ModManagerView"] = typeof(WILLOWMAKER.Core.Views.ModManagerView),
        ["ModManager"] = typeof(WILLOWMAKER.Core.Views.ModManagerView),
        ["ReplayStudioView"] = typeof(NEXUS.ControlPanel.GUI.Views.ReplayStudioView),
        ["ReplayStudio"] = typeof(NEXUS.ControlPanel.GUI.Views.ReplayStudioView),
        ["SecretsConfigView"] = typeof(NEXUS.ControlPanel.GUI.Views.SecretsConfigView),
        ["SecretsConfig"] = typeof(NEXUS.ControlPanel.GUI.Views.SecretsConfigView),
        ["DiagnosticsView"] = typeof(NEXUS.ControlPanel.GUI.Views.DiagnosticsView),
        ["Diagnostics"] = typeof(NEXUS.ControlPanel.GUI.Views.DiagnosticsView),
        ["DashboardView"] = typeof(NEXUS.ControlPanel.GUI.Views.DashboardView),
        ["Dashboard"] = typeof(NEXUS.ControlPanel.GUI.Views.DashboardView),
        ["GameServersView"] = typeof(NEXUS.ControlPanel.GUI.Views.GameServersView),
        ["GameServers"] = typeof(NEXUS.ControlPanel.GUI.Views.GameServersView),
        ["UsersView"] = typeof(NEXUS.ControlPanel.GUI.Views.UsersView),
        ["Users"] = typeof(NEXUS.ControlPanel.GUI.Views.UsersView),
        ["LiveLogsView"] = typeof(NEXUS.ControlPanel.GUI.Views.LiveLogsView),
        ["LiveLogs"] = typeof(NEXUS.ControlPanel.GUI.Views.LiveLogsView),
        ["ReplayConverterView"] = typeof(WILLOWMAKER.Core.Views.ReplayConverterView),
        ["ReplayConverter"] = typeof(WILLOWMAKER.Core.Views.ReplayConverterView),
        ["MainView"] = typeof(WILLOWMAKER.Core.Views.MainView),
        ["MainWindow"] = typeof(NEXUS.ControlPanel.GUI.Views.MainWindow),
        ["HeroViewportView"] = typeof(NEXUS.ControlPanel.GUI.Controls.Kongor3DViewportControl),
        ["HeroViewport"] = typeof(NEXUS.ControlPanel.GUI.Controls.Kongor3DViewportControl),
        ["FileLockDialog"] = typeof(WILLOWMAKER.Core.Views.FileLockDialog),
        ["LocationGuardDialog"] = typeof(WILLOWMAKER.Core.Views.LocationGuardDialog),
        ["SynchronisationBypassDialog"] = typeof(WILLOWMAKER.Core.Views.SynchronisationBypassDialog),
        ["UpdateDialog"] = typeof(WILLOWMAKER.Core.Views.UpdateDialog)
    };

    public HeadlessUiRenderer(ILogger<HeadlessUiRenderer>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<HeadlessUiRenderer>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        if (_isInitialized && _uiThread != null && _uiThread.IsAlive)
        {
            return;
        }

        lock (_initLock)
        {
            if (_isInitialized && _uiThread != null && _uiThread.IsAlive)
            {
                return;
            }

            ManualResetEventSlim ready = new(false);
            Exception? initEx = null;

            _uiThread = new Thread(() =>
            {
                try
                {
                    if (Application.Current == null)
                    {
                        AppBuilder.Configure<NEXUS.ControlPanel.GUI.App>()
                            .UseSkia()
                            .UseHeadless(new AvaloniaHeadlessPlatformOptions
                            {
                                UseHeadlessDrawing = false
                            })
                            .WithInterFont()
                            .SetupWithoutStarting();

                        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                        }
                    }
                    ready.Set();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HeadlessUiRenderer] EnsureInitialized failed: {ex}");
                    initEx = ex;
                    ready.Set();
                    return;
                }

                // Dedicated UI event pump for all render and control instantiation requests
                foreach (Action work in _workQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[HeadlessUiRenderer] Work error: {ex}");
                    }
                }
            })
            {
                IsBackground = true,
                Name = "AvaloniaHeadlessUiThread"
            };

            if (OperatingSystem.IsWindows())
            {
                _uiThread.SetApartmentState(ApartmentState.STA);
            }
            _uiThread.Start();

            ready.Wait();
            if (initEx != null && Application.Current == null)
            {
                throw initEx;
            }
            _isInitialized = true;
        }
    }

    public IReadOnlyList<string> GetSupportedViews() => [.. KnownViews.Keys.OrderBy(k => k)];

    public async Task<UiRenderResult> RenderViewAsync(
        string viewName,
        int width = 1280,
        int height = 720,
        string theme = "dark",
        bool mockData = true,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewName))
        {
            return new UiRenderResult
            {
                Success = false,
                ErrorMessage = "Parameter 'view_name' cannot be null or whitespace."
            };
        }

        width = Math.Clamp(width, 100, 7680);
        height = Math.Clamp(height, 100, 4320);

        EnsureInitialized();

        TaskCompletionSource<UiRenderResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _workQueue.Add(() =>
        {
            try
            {
                UiRenderResult res = RenderInternal(viewName, width, height, theme, mockData, saveArtifact, customOutputPath);
                tcs.SetResult(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HeadlessUiRenderer] Render failed for '{ViewName}': {Message}", viewName, ex.Message);
                tcs.SetResult(new UiRenderResult
                {
                    Success = false,
                    ViewName = viewName,
                    ErrorMessage = $"Render failed: {ex.Message}"
                });
            }
        }, cancellationToken);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
    }

    private UiRenderResult RenderInternal(
        string viewName,
        int width,
        int height,
        string theme,
        bool mockData,
        bool saveArtifact,
        string? customOutputPath)
    {
        try
        {
            // 1. Resolve View Type & Instantiate Control + ViewModel
            Control? control = CreateViewControl(viewName, mockData, out Type? resolvedType, out string? instantiationError);
            if (control == null || resolvedType == null)
            {
                string errorMsg = resolvedType == null
                    ? $"View '{viewName}' could not be resolved. Supported views: {string.Join(", ", KnownViews.Keys.OrderBy(k => k))}"
                    : $"View '{viewName}' resolved to '{resolvedType.FullName}', but instantiation failed: {instantiationError}";
                _logger.LogWarning("[HeadlessUiRenderer] {ErrorMsg}", errorMsg);
                return new UiRenderResult
                {
                    Success = false,
                    ViewName = viewName,
                    ResolvedType = resolvedType?.FullName ?? string.Empty,
                    Width = width,
                    Height = height,
                    Theme = theme,
                    ErrorMessage = errorMsg
                };
            }

            _logger.LogInformation("[HeadlessUiRenderer] Resolved view '{ViewName}' -> '{ResolvedType}'", viewName, resolvedType.FullName);

            // 2. Determine ThemeVariant
            ThemeVariant themeVariant = theme.Trim().ToLowerInvariant() switch
            {
                "light" => ThemeVariant.Light,
                "default" => ThemeVariant.Default,
                _ => ThemeVariant.Dark
            };

            if (Application.Current != null)
            {
                Application.Current.RequestedThemeVariant = themeVariant;
            }

            // 3. Wrap in Window container and perform layout pass
            Window window = new()
            {
                Width = width,
                Height = height,
                SystemDecorations = SystemDecorations.None,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0A0D14")),
                Content = control,
                RequestedThemeVariant = themeVariant
            };

            control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

            window.Show();

            PropertyInfo? platformImplProp = typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object? platformImpl = platformImplProp?.GetValue(window);
            if (platformImpl != null)
            {
                MethodInfo? resizeMethod = platformImpl.GetType().GetMethod("Resize");
                if (resizeMethod != null)
                {
                    ParameterInfo[] pars = resizeMethod.GetParameters();
                    if (pars.Length == 2)
                    {
                        resizeMethod.Invoke(platformImpl, [new Size(width, height), Enum.ToObject(pars[1].ParameterType, 0)]);
                    }
                    else if (pars.Length == 1)
                    {
                        resizeMethod.Invoke(platformImpl, [new Size(width, height)]);
                    }
                }
            }

            window.Width = width;
            window.Height = height;
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // 4. Skia RenderTargetBitmap rasterization
            using RenderTargetBitmap rtb = new(new PixelSize(width, height), new Vector(96, 96));
            rtb.Render(window);
            object? dataContext = control.DataContext;
            control.DataContext = null;
            window.Content = null;
            window.Hide();
            if (dataContext is IDisposable disposableVm)
            {
                try
                {
                    disposableVm.Dispose();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "[HeadlessUiRenderer] Failed to dispose ViewModel for '{ViewName}': {Message}", viewName, disposeEx.Message);
                }
            }

            using MemoryStream ms = new();
            rtb.Save(ms);
            byte[] pngBytes = ms.ToArray();
            string base64Png = Convert.ToBase64String(pngBytes);

            // 5. Save Artifact if requested
            string? artifactPath = null;
            if (saveArtifact)
            {
                string artifactDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts", "ui_renders");
                Directory.CreateDirectory(artifactDir);

                string sanitizedView = SanitizeFileName(viewName);
                artifactPath = !string.IsNullOrWhiteSpace(customOutputPath)
                    ? customOutputPath
                    : Path.Combine(artifactDir, $"{sanitizedView}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmssfff}.png");

                string? parentDir = Path.GetDirectoryName(artifactPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                File.WriteAllBytes(artifactPath, pngBytes);
                _logger.LogInformation("[HeadlessUiRenderer] Saved UI render artifact to: '{Path}'", artifactPath);
            }

            _logger.LogInformation("[HeadlessUiRenderer] Render succeeded for '{ViewName}' ({Width}x{Height}, Output Base64: {Length} chars)",
                viewName, width, height, base64Png.Length);

            return new UiRenderResult
            {
                Success = true,
                ViewName = viewName,
                ResolvedType = resolvedType.FullName ?? resolvedType.Name,
                Width = width,
                Height = height,
                Theme = themeVariant.Key?.ToString()?.ToLowerInvariant() ?? "dark",
                ArtifactPath = artifactPath,
                Base64Png = base64Png
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HeadlessUiRenderer] Exception rendering view '{ViewName}': {Message}", viewName, ex.Message);
            return new UiRenderResult
            {
                Success = false,
                ViewName = viewName,
                Width = width,
                Height = height,
                Theme = theme,
                ErrorMessage = $"Rendering exception for '{viewName}': {ex.Message}"
            };
        }
    }

    private Control? CreateViewControl(string viewName, bool mockData, out Type? resolvedType, out string? instantiationError)
    {
        resolvedType = null;
        instantiationError = null;
        string cleanName = viewName.Trim();

        // 1. Check Known Views Dictionary
        if (KnownViews.TryGetValue(cleanName, out Type? directType))
        {
            resolvedType = directType;
            return InstantiateAndBind(directType, cleanName, mockData, out instantiationError);
        }

        // 2. Reflection search across all loaded assemblies
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly asm in assemblies)
        {
            try
            {
                Type[] types = asm.GetTypes();
                foreach (Type t in types)
                {
                    if (typeof(Control).IsAssignableFrom(t) && !t.IsAbstract &&
                        (t.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                         t.Name.Equals($"{cleanName}View", StringComparison.OrdinalIgnoreCase) ||
                         (t.FullName != null && t.FullName.EndsWith(cleanName, StringComparison.OrdinalIgnoreCase))))
                    {
                        resolvedType = t;
                        return InstantiateAndBind(t, cleanName, mockData, out instantiationError);
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private Control? InstantiateAndBind(Type viewType, string viewName, bool mockData, out string? error)
    {
        error = null;
        try
        {
            object? instance = Activator.CreateInstance(viewType);
            if (instance is not Control control)
            {
                error = $"Type '{viewType.FullName}' is not an Avalonia Control.";
                return null;
            }

            if (mockData)
            {
                try
                {
                    InjectMockDataContext(control, viewType, viewName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HeadlessUiRenderer] Mock data injection skipped for '{Type}': {Msg}", viewType.FullName, ex.Message);
                }
            }

            return control;
        }
        catch (Exception ex)
        {
            error = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
            _logger.LogWarning(ex, "[HeadlessUiRenderer] Failed to instantiate view type '{Type}': {Msg}", viewType.FullName, error);
            return null;
        }
    }

    private void InjectMockDataContext(Control control, Type viewType, string viewName)
    {
        string name = viewType.Name;

        if (name.Contains("ModManager", StringComparison.OrdinalIgnoreCase))
        {
            ModManagerViewModel vm = new(autoLoad: false);
            vm.Mods = new ObservableCollection<ModItemViewModel>
            {
                new()
                {
                    Name = "Hero UI Overhaul v2.4",
                    Author = "Kongor Dev Team",
                    Version = "2.4.1",
                    Category = "Interface",
                    IsEnabled = true,
                    Description = "Enhanced hero status bars, cooldown timers, and tactical ping indicators."
                },
                new()
                {
                    Name = "4K Particle FX Optimization",
                    Author = "ParticleWizard",
                    Version = "1.8.0",
                    Category = "Graphics",
                    IsEnabled = true,
                    Description = "Optimized spell particle effects and high-res spell textures."
                },
                new()
                {
                    Name = "Caldavar Vector Minimap",
                    Author = "MapCrafter",
                    Version = "3.0.0",
                    Category = "Maps",
                    IsEnabled = false,
                    Description = "Vector-crisp minimap with ward vision radius markers."
                },
                new()
                {
                    Name = "Legacy Mod Auto-Repairer",
                    Author = "System",
                    Version = "1.0.0",
                    Category = "Utility",
                    IsEnabled = true,
                    Description = "Auto-repairs legacy HoN .honmod packages into modern .s2z format."
                },
                new()
                {
                    Name = "Custom Announcer Pack - Merrick",
                    Author = "SoundMaster",
                    Version = "1.2.0",
                    Category = "Audio",
                    IsEnabled = true,
                    Description = "Custom in-game announcer voice for multi-kills and killstreaks."
                }
            };
            vm.FilteredMods = new ObservableCollection<ModItemViewModel>(vm.Mods);
            vm.SelectedMod = vm.Mods[0];
            control.DataContext = vm;
        }
        else if (name.Contains("ReplayStudio", StringComparison.OrdinalIgnoreCase))
        {
            ReplayStudioViewModel vm = new();
            vm.MatchId = 1459283;
            vm.MapName = "Forests of Caldavar";
            vm.GameMode = "All Pick (Ranked)";
            vm.GameDate = "2026-09-02 18:30:00";
            vm.DurationDisplay = "38:45";
            vm.WinnerDisplay = "Legion Victory";
            vm.WinnerColorHex = "#00F0FF";
            vm.SelectedResolution = "1440p (2K 60FPS)";
            vm.SelectedCameraMode = "Player POV (Active Hero)";
            vm.ActiveProgressPercent = 68.5;
            vm.ActiveFpsDisplay = "120.0 FPS";
            vm.ActiveElapsedDisplay = "02:15";
            vm.ActiveRemainingDisplay = "01:05";
            vm.IsProcessingActive = true;
            vm.IsReplayLoaded = true;
            control.DataContext = vm;
        }
        else if (name.Contains("SecretsConfig", StringComparison.OrdinalIgnoreCase))
        {
            SecretsConfigViewModel vm = new(new MockSecretsManagerService(), null);
            vm.DatabaseProvider = "Supabase PostgreSQL";
            vm.DatabasePassword = new string('\u2022', 16);
            vm.CloudServiceUrl = "https://example.supabase.co";
            vm.CloudRegion = "us-east-1";
            vm.CloudBucketName = "kongor-replays-vault";
            vm.CloudAccessKey = "sb_access_key_9921";
            vm.CloudSecretKey = new string('\u2022', 16);
            vm.CompelGateway = "compel.projectkongor.internal";
            vm.CompelInstances = 4;
            vm.ChatServerClientPort = 11031;
            vm.DistributedCachePort = 28298;
            control.DataContext = vm;
        }
        else if (name.Contains("Diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsViewModel vm = new(
                new MockPortScannerService(),
                new MockDockerHealthService(),
                new MockDatabaseHealthService());

            vm.IsDockerHealthy = true;
            vm.DockerStatusMessage = "Docker Engine v27.0.3 (Healthy / Active)";
            vm.IsPyroscopeHealthy = true;
            vm.PyroscopeStatusMessage = "Pyroscope Profiler v1.4.0 (Streaming)";
            vm.PyroscopeStatusColor = "#10B981";
            vm.OsInfo = "Windows 11 Pro 64-bit (10.0.26100)";
            vm.DotnetInfo = ".NET 11.0.100 (Preview 7)";
            vm.PortStatuses =
            [
                new PortStatus { Port = 55555, ServiceName = "Master Server (Auth/SRP)", IsAvailable = true, Protocol = "TCP" },
                new PortStatus { Port = 11031, ServiceName = "Chat Server (XMPP/Messaging)", IsAvailable = true, Protocol = "TCP" },
                new PortStatus { Port = 5200, ServiceName = "Web Portal API", IsAvailable = true, Protocol = "TCP" },
                new PortStatus { Port = 5266, ServiceName = "Web Portal UI", IsAvailable = true, Protocol = "TCP" },
                new PortStatus { Port = 11234, ServiceName = "COMPEL Control Plane", IsAvailable = true, Protocol = "TCP" },
                new PortStatus { Port = 28298, ServiceName = "Redis Distributed Cache", IsAvailable = true, Protocol = "TCP" }
            ];
            control.DataContext = vm;
        }
        else if (name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase))
        {
            DashboardViewModel vm = new(
                new MockNexusOrchestratorService(),
                new MockDockerHealthService(),
                new MockDatabaseHealthService(),
                new MockSecretsManagerService());

            vm.ClusterState = ServerState.Running;
            vm.ClusterStatusText = "ONLINE / OPERATIONAL";
            vm.ClusterStatusColor = "#10B981";
            vm.DockerStatusMessage = "Docker Engine Online";
            vm.DockerStatusColor = "#10B981";
            vm.DatabaseStatusMessage = "Connected (Supabase PG 16)";
            vm.DatabaseStatusColor = "#10B981";
            vm.IsDatabaseOnline = true;
            vm.IsDockerOnline = true;
            vm.IsRedisOnline = true;
            vm.IsPyroscopeOnline = true;
            control.DataContext = vm;
        }
        else if (name.Contains("GameServers", StringComparison.OrdinalIgnoreCase))
        {
            GameServersViewModel vm = new(new MockGameServerFleetService());
            vm.TotalHosts = 3;
            vm.OnlineHosts = 3;
            vm.TotalActiveInstances = 8;
            vm.AveragePingMs = 14;
            vm.FleetStatusMessage = "Fleet healthy — 8 active match instances running.";
            control.DataContext = vm;
        }
        else if (name.Contains("Users", StringComparison.OrdinalIgnoreCase))
        {
            UsersViewModel vm = new(new MockUserManagementService());
            vm.TotalAccounts = 1420;
            vm.TotalStaff = 12;
            vm.TotalUsers = 1408;
            vm.IsDatabaseConnected = true;
            vm.StatusMessage = "User database synced.";
            control.DataContext = vm;
        }
        else if (name.Contains("LiveLogs", StringComparison.OrdinalIgnoreCase))
        {
            NEXUS.ControlPanel.GUI.ViewModels.LiveLogsViewModel vm = new(new MockNexusOrchestratorService());
            control.DataContext = vm;
        }
        else if (name.Contains("ReplayConverter", StringComparison.OrdinalIgnoreCase))
        {
            ReplayConverterViewModel vm = new();
            control.DataContext = vm;
        }
        else if (name.Contains("MainView", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("ClientLauncher", StringComparison.OrdinalIgnoreCase))
        {
            WILLOWMAKER.Core.ViewModels.MainViewModel vm = new();
            control.DataContext = vm;
        }
    }

    private static string SanitizeFileName(string input)
    {
        string invalid = new(Path.GetInvalidFileNameChars());
        string escaped = Regex.Escape(invalid);
        return Regex.Replace(input, $"[{escaped}]", "_");
    }

    #region Mock Services for Headless ViewModel Initialization

    private sealed class MockPortScannerService : IPortScannerService
    {
        public Task<List<PortStatus>> ScanRequiredPortsAsync(CancellationToken cancellationToken = default)
        {
            List<PortStatus> statuses =
            [
                new PortStatus { Port = 55555, ServiceName = "Master Server", IsAvailable = false, Protocol = "TCP" },
                new PortStatus { Port = 11031, ServiceName = "Chat Server", IsAvailable = false, Protocol = "TCP" },
                new PortStatus { Port = 5200, ServiceName = "Web Portal API", IsAvailable = false, Protocol = "TCP" },
                new PortStatus { Port = 11234, ServiceName = "COMPEL", IsAvailable = false, Protocol = "TCP" }
            ];
            return Task.FromResult(statuses);
        }
    }

    private sealed class MockDockerHealthService : IDockerHealthService
    {
        public Docker.DotNet.IDockerClient Client => null!;

        public Task<DockerStatus> CheckDockerStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DockerStatus
            {
                IsRunning = true,
                StatusMessage = "Docker Engine Active",
                Version = "27.0.3"
            });
        }

        public Task<bool> IsImageBuiltAsync(string imageName = "projectkongor/compel-dedicated:latest",
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsDedicatedImageStaleAsync(string workspaceRoot,
            string imageName = "projectkongor/compel-dedicated:latest",
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<(bool Success, string Output)> LaunchContainerAsync(string runArguments,
            CancellationToken cancellationToken = default) => Task.FromResult((true, "Container launched"));

        public Task<(bool Success, string Output)> LaunchContainerAsync(Docker.DotNet.Models.CreateContainerParameters parameters,
            CancellationToken cancellationToken = default) => Task.FromResult((true, "Container launched"));

        public Task<(bool Success, string Output)> StopContainerAsync(string containerName = "compel-dedicated-server",
            CancellationToken cancellationToken = default) => Task.FromResult((true, "Container stopped"));

        public Task<bool> StartContainerAsync(string containerName, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> EnsureImagePulledAsync(string imageName, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<(bool Success, string Output)> BuildImageAsync(string dockerfilePath, string contextPath,
            string tag = "projectkongor/compel-dedicated:latest", CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "Image built"));
    }

    private sealed class MockDatabaseHealthService : IDatabaseHealthService
    {
        public Task<DatabaseStatus> CheckDatabaseHealthAsync(ISecretsManagerService? secretsManager = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseStatus
            {
                IsConnected = true,
                StatusMessage = "Supabase PostgreSQL Online",
                DatabaseType = "Supabase"
            });
        }

        public Task<DatabaseStatus> CheckSqlServerHealthAsync(int port = 1433, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseStatus { IsConnected = true, StatusMessage = "SQL Server Online" });
        }

        public Task<DatabaseStatus> CheckRedisHealthAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseStatus { IsConnected = true, StatusMessage = "Redis Cache Online" });
        }

        public Task<DatabaseStatus> CheckPyroscopeHealthAsync(int port = 4040, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseStatus { IsConnected = true, StatusMessage = "Pyroscope Online" });
        }

        public Task<DatabaseStatus> CheckDotnetMonitorHealthAsync(int port = 52323, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseStatus { IsConnected = true, StatusMessage = "dotnet-monitor Online" });
        }
    }

    private sealed class MockNexusOrchestratorService : INexusOrchestratorService
    {
        public ServerState CurrentClusterState => ServerState.Running;
        public IReadOnlyList<LogMessage> LogHistory =>
        [
            new LogMessage { Message = "[Cluster] Aspire Host initialized successfully.", Level = "INF" },
            new LogMessage { Message = "[MasterServer] SRP authentication ready on port 55555.", Level = "INF" },
            new LogMessage { Message = "[COMPEL] Game server fleet active on port 11234.", Level = "INF" }
        ];
        public string AspireDashboardUrl => "https://localhost:5550";

        public event Action<ServerState>? OnClusterStateChanged { add { } remove { } }
        public event Action<string, ServerState>? OnServiceStateChanged { add { } remove { } }
        public event Action<LogMessage>? OnLogReceived { add { } remove { } }

        public ServerState GetServiceState(string serviceKey) => ServerState.Running;
        public Task StartClusterAsync(bool withGameServers = false, string? customWorkingDir = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopClusterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartClusterAsync(bool withGameServers = false, string? customWorkingDir = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartServiceAsync(string serviceKey, string? customWorkingDir = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopServiceAsync(string serviceKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartServiceAsync(string serviceKey, string? customWorkingDir = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAllAsync(bool includeGameServers = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LaunchGameClientAsync(string? customClientPath = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LaunchGameServerAsync(string? customServerPath = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void AppendLog(LogMessage log) { }
        public void ClearLogs() { }
        public void Dispose() { }
    }

    private sealed class MockSecretsManagerService : ISecretsManagerService
    {
        public NexusSecrets CurrentSecrets => new();
        public event Action<NexusSecrets>? SecretsChanged { add { } remove { } }
        public Task<NexusSecrets> LoadSecretsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new NexusSecrets());
        public Task SaveSecretsAsync(NexusSecrets secrets, NexusSecrets? originalSecrets = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveSecretValueAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetToDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string GetSecretsFilePath() => "mock/secrets.json";
        public Task<bool> ValidateSecretsAsync(NexusSecrets secrets, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class MockGameServerFleetService : IGameServerFleetService
    {
        public Task<List<GameServerHostEntry>> GetDiscoveredHostsAsync(CancellationToken cancellationToken = default)
        {
            List<GameServerHostEntry> list =
            [
                new GameServerHostEntry
                {
                    HostAddress = "127.0.0.1",
                    Port = 11234,
                    Name = "US-East Local Fleet Node",
                    Location = "USE",
                    IsLocal = true
                }
            ];
            return Task.FromResult(list);
        }

        public Task<(bool Success, long LatencyMs, CompelPingResponseDto? Response)> PingHostAsync(string hostAddress, int port, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<(bool, long, CompelPingResponseDto?)>((true, 12, new CompelPingResponseDto { Application = "COMPEL", Version = "5.0.2" }));
        }

        public Task<(bool Success, CompelStatusResponseDto? Status, string? ErrorMessage)> GetHostStatusAsync(string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<(bool, CompelStatusResponseDto?, string?)>((true, new CompelStatusResponseDto { Application = "COMPEL", Version = "5.0.2" }, null));
        }

        public Task<(bool Success, string Message)> StartInstancesAsync(string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Started"));
        public Task<(bool Success, string Message)> StopInstancesAsync(string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Stopped"));
        public Task<(bool Success, string Message)> RestartInstancesAsync(string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Restarted"));
        public Task<(bool Success, string Message)> SyncDistributionAsync(string hostAddress, int port, string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Synced"));

        public Task<(bool Success, string Message)> StartWorkerAsync(string hostAddress, int port, int targetServerPort,
            string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Worker started"));

        public Task<(bool Success, string Message)> StopWorkerAsync(string hostAddress, int port, int targetServerPort,
            string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Worker stopped"));

        public Task<(bool Success, string Message)> RestartWorkerAsync(string hostAddress, int port, int targetServerPort,
            string? authToken = null, CancellationToken cancellationToken = default) => Task.FromResult((true, "Worker restarted"));

        public Task<(bool Success, List<CompelDedicatedServerLogLineDto> Logs, string? ErrorMessage)> GetServerLogsAsync(
            string hostAddress, int port, int targetServerPort, int tail = 100, string? authToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(bool, List<CompelDedicatedServerLogLineDto>, string?)>((true, [], null));

        public async IAsyncEnumerable<CompelDedicatedServerLogLineDto> StreamServerLogsAsync(
            string hostAddress, int port, int targetServerPort, int tail = 50, string? authToken = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<(bool Success, string Channel, string Message)> SendWorkerCommandAsync(string hostAddress, int port,
            int targetServerPort, string command, string? authToken = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "stdin", $"Executed: {command}"));
    }

    private sealed class MockUserManagementService : IUserManagementService
    {
        public Task<UserSummaryStats> GetSummaryStatsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UserSummaryStats
            {
                TotalUsers = 1408,
                TotalAccounts = 1420,
                TotalStaff = 12,
                TotalHosts = 3,
                IsDatabaseConnected = true,
                StatusMessage = "Database Connected"
            });
        }

        public Task<List<UserDetailDto>> SearchUsersAsync(string query, string roleFilter, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<List<UserDetailDto>>([]);
        }

        public Task<UserDetailDto?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default) => Task.FromResult<UserDetailDto?>(null);
        public Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SetAccountRoleAsync(int accountId, AccountType type, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ResetHostPasswordAsync(int accountId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("new_pass_123");
        public Task<string?> ResetHostAuthTokenAsync(int accountId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("new_token_123");
        public Task<bool> UpdateUserEconomyAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AdjustUserBalanceAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PromoteUserToHostAsync(int userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RevertUserToStandardAsync(int userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    #endregion
}
