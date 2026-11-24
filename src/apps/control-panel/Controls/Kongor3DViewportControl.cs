// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using NEXUS.ControlPanel.GUI.Services;
using Color = Avalonia.Media.Color;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace NEXUS.ControlPanel.GUI.Controls;

public enum ViewportDisplayMode
{
    MinimalistCrest,
    ThreeDimensionalHero
}

/// <summary>
/// Dual-mode 3D hero avatar viewport control for Empath (TAO - Together As One).
/// Supports:
/// 1. MinimalistCrest (Default): High-performance Avalonia-native Skia GPU canvas with &lt;15MB RAM and 0ms startup.
/// 2. ThreeDimensionalHero (On-Demand): Full 3D animated hero character (empath_cn.glb) via Three.js and WebView2,
///    with automated Win32 airspace hiding when modal dialogs are active.
/// </summary>
public sealed partial class Kongor3DViewportControl : Grid
{
    public static event Action? OnGlobalKnockoutRequested;
    public static event Action? OnGlobalQuitSoundRequested;
    public static event Action<bool>? OnGlobalModalOverlayChanged;

    public static void RequestKnockout() => OnGlobalKnockoutRequested?.Invoke();
    public static void RequestQuitSound() => OnGlobalQuitSoundRequested?.Invoke();
    public static void SetModalOverlayActive(bool active) => OnGlobalModalOverlayChanged?.Invoke(active);

    public ViewportDisplayMode DisplayMode { get; private set; } = ViewportDisplayMode.ThreeDimensionalHero;

    public bool IsWebViewInitialized =>
        DisplayMode == ViewportDisplayMode.MinimalistCrest || (_webView2Host?.IsWebViewInitialized == true);

    public bool IsViewportReady => true;

    private readonly SkiaCrestCanvas _crestCanvas;
    private EmpathWebView2HostControl? _webView2Host;

    public Kongor3DViewportControl()
    {
        ClipToBounds = true;

        // Base Layer: Native Skia GPU Crest (Headless fallback)
        _crestCanvas = new SkiaCrestCanvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Children.Add(_crestCanvas);

        // Borderless 3D Empath Model Display (when local hero GLB overlay is available; otherwise cleanroom SkiaCrestCanvas)
        if (OperatingSystem.IsWindows() && !IsTestEnvironment() && HasHeroModelAsset())
        {
            _webView2Host = new EmpathWebView2HostControl(HandleWebViewHostFailure)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Children.Insert(0, _webView2Host);
            _crestCanvas.IsVisible = false;
        }
        else
        {
            DisplayMode = ViewportDisplayMode.MinimalistCrest;
            _crestCanvas.IsVisible = true;
        }
    }

    private static readonly object LogSync = new();

    private static void LogViewport(string message)
    {
        Console.WriteLine(message);
        try
        {
            string debugLog = Path.Combine(AppContext.BaseDirectory, "gui_debug.log");
            lock (LogSync)
            {
                File.AppendAllText(debugLog, $"[{TimeProvider.System.GetUtcNow():O}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kongor3D] gui_debug.log append warning: {ex.Message}");
        }
    }

    private void HandleWebViewHostFailure(string reason)
    {
        LogViewport($"[Kongor3D][WARN] Falling back to SkiaCrestCanvas because WebView2 host failed: {reason}");
        Dispatcher.UIThread.Post(() =>
        {
            DisplayMode = ViewportDisplayMode.MinimalistCrest;
            if (_webView2Host != null)
            {
                _webView2Host.IsVisible = false;
            }
            _crestCanvas.IsVisible = true;
        });
    }

    private static bool IsTestEnvironment()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTHOST_PID")) ||
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUS_HEADLESS")) ||
               AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                   a.GetName().Name?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true ||
                   a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnGlobalKnockoutRequested += HandleGlobalKnockout;
        OnGlobalQuitSoundRequested += HandleGlobalQuitSound;
        OnGlobalModalOverlayChanged += HandleModalOverlayChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        OnGlobalKnockoutRequested -= HandleGlobalKnockout;
        OnGlobalQuitSoundRequested -= HandleGlobalQuitSound;
        OnGlobalModalOverlayChanged -= HandleModalOverlayChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleGlobalKnockout()
    {
        _crestCanvas.TriggerKnockout();
        _webView2Host?.TriggerKnockout();
    }

    private void HandleGlobalQuitSound()
    {
        _crestCanvas.PlayQuitSound();
        _webView2Host?.PlayQuitSound();
    }

    private void HandleModalOverlayChanged(bool isModalActive)
    {
        if (_webView2Host != null && DisplayMode == ViewportDisplayMode.ThreeDimensionalHero)
        {
            if (isModalActive)
                _webView2Host.HideForModal();
            else
                _webView2Host.ShowFromModal();
        }
    }

    public static readonly IReadOnlyList<string> RequiredViewerAssetRelativePaths =
    [
        Path.Combine("web", "kongor_viewer.html"),
        Path.Combine("lib", "three", "three.r16x.bundle.js"),
        Path.Combine("lib", "three", "three.module.min.js"),
        Path.Combine("lib", "three", "meshopt_decoder.module.js"),
        Path.Combine("lib", "three", "draco", "draco_decoder.js"),
        Path.Combine("js", "kongor-viewer-core.js")
    ];

    public static readonly string OptionalHeroModelRelativePath = Path.Combine("models", "empath_cn.glb");

    public static bool IsForceCleanroomAssets()
    {
        string? flag = Environment.GetEnvironmentVariable("NEXUS_FORCE_CLEANROOM_ASSETS") ??
                       Environment.GetEnvironmentVariable("KONGOR_FORCE_CLEANROOM_ASSETS");
        return string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasHeroModelAsset(string? baseDirectory = null, bool allowRepoOverlaySync = true)
    {
        if (IsForceCleanroomAssets())
            return false;

        string assetsDir = ResolveAndPrepareAssetsDirectory(baseDirectory, allowRepoOverlaySync);
        return File.Exists(Path.Combine(assetsDir, OptionalHeroModelRelativePath));
    }

    /// <summary>
    /// Resolves the runtime Assets directory mapped to https://nexus.local and ensures all Three.js r168,
    /// KongorViewerCore, and (when available) local overlay 3D model files are staged on disk before WebView2 navigation.
    /// </summary>
    public static string ResolveAndPrepareAssetsDirectory(string? baseDirectory = null, bool allowRepoOverlaySync = true)
    {
        string baseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(baseDirectory);

        string assetsDir = Path.Combine(baseDir, "Assets");
        if (!Directory.Exists(assetsDir))
        {
            string? projectRoot = Directory.GetParent(baseDir)?.Parent?.Parent?.FullName;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                string candidateAssets = Path.Combine(projectRoot, "Assets");
                if (Directory.Exists(candidateAssets))
                {
                    assetsDir = candidateAssets;
                }
            }
        }

        // Self-heal models/empath_cn.glb from web/empath_cn.glb if only the web sibling is present
        string primaryModelPath = Path.Combine(assetsDir, OptionalHeroModelRelativePath);
        string fallbackWebModelPath = Path.Combine(assetsDir, "web", "empath_cn.glb");
        if (!File.Exists(primaryModelPath) && File.Exists(fallbackWebModelPath))
        {
            try
            {
                string? modelDir = Path.GetDirectoryName(primaryModelPath);
                if (!string.IsNullOrEmpty(modelDir))
                {
                    Directory.CreateDirectory(modelDir);
                }
                File.Copy(fallbackWebModelPath, primaryModelPath, overwrite: false);
                LogViewport($"[Kongor3D] Self-healed missing model '{primaryModelPath}' from '{fallbackWebModelPath}'.");
            }
            catch (Exception ex)
            {
                LogViewport($"[Kongor3D][WARN] Could not copy fallback Empath model to '{primaryModelPath}': {ex.Message}");
            }
        }

        // Self-heal optional local overlay (games/control-panel-overlay) when running in local dev workspace
        if (allowRepoOverlaySync && !IsForceCleanroomAssets() && !File.Exists(primaryModelPath))
        {
            string? repoRootForOverlay = TryFindRepoRoot(baseDir);
            if (!string.IsNullOrEmpty(repoRootForOverlay))
            {
                string overlayDir = Path.Combine(repoRootForOverlay, "games", "control-panel-overlay");
                if (Directory.Exists(overlayDir))
                {
                    try
                    {
                        CopyDirectoryRecursive(overlayDir, assetsDir);
                        LogViewport($"[Kongor3D] Staged optional local retail overlay from '{overlayDir}' into '{assetsDir}'.");
                    }
                    catch (Exception ex)
                    {
                        LogViewport($"[Kongor3D][WARN] Could not stage optional overlay from '{overlayDir}': {ex.Message}");
                    }
                }
            }
        }

        // Self-heal Three.js r168 + KongorViewerCore from src/services/web-ui/wwwroot if running from an unbuilt dev folder
        bool missingJsBundle = !File.Exists(Path.Combine(assetsDir, "lib", "three", "three.r16x.bundle.js")) ||
                               !File.Exists(Path.Combine(assetsDir, "js", "kongor-viewer-core.js"));
        if (missingJsBundle)
        {
            string? repoRoot = TryFindRepoRoot(baseDir);
            if (!string.IsNullOrEmpty(repoRoot))
            {
                string wwwroot = Path.Combine(repoRoot, "src", "services", "web-ui", "wwwroot");
                if (Directory.Exists(wwwroot))
                {
                    // Stage into baseDir/Assets rather than mutating source tree if possible
                    string targetAssetsDir = Path.Combine(baseDir, "Assets");
                    try
                    {
                        if (!string.Equals(targetAssetsDir, assetsDir, StringComparison.OrdinalIgnoreCase) &&
                            Directory.Exists(assetsDir))
                        {
                            CopyDirectoryRecursive(assetsDir, targetAssetsDir);
                            assetsDir = targetAssetsDir;
                        }

                        string srcThreeDir = Path.Combine(wwwroot, "lib", "three");
                        if (Directory.Exists(srcThreeDir))
                        {
                            CopyDirectoryRecursive(srcThreeDir, Path.Combine(assetsDir, "lib", "three"));
                        }

                        string srcViewerCore = Path.Combine(wwwroot, "js", "kongor-viewer-core.js");
                        if (File.Exists(srcViewerCore))
                        {
                            string destJsDir = Path.Combine(assetsDir, "js");
                            Directory.CreateDirectory(destJsDir);
                            File.Copy(srcViewerCore, Path.Combine(destJsDir, "kongor-viewer-core.js"), overwrite: true);
                        }

                        LogViewport($"[Kongor3D] Synchronized Three.js r168 and kongor-viewer-core.js from '{wwwroot}' into '{assetsDir}'.");
                    }
                    catch (Exception ex)
                    {
                        LogViewport($"[Kongor3D][WARN] Runtime synchronization of Three.js viewer assets into '{assetsDir}' failed: {ex.Message}. Rebuild via: dotnet build src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj");
                    }
                }
            }
        }

        List<string> missing = [];
        for (int i = 0; i < RequiredViewerAssetRelativePaths.Count; i++)
        {
            string rel = RequiredViewerAssetRelativePaths[i];
            if (!File.Exists(Path.Combine(assetsDir, rel)))
            {
                missing.Add(rel);
            }
        }

        if (missing.Count > 0)
        {
            LogViewport($"[Kongor3D][ERROR] Missing {missing.Count} required 3D viewport asset(s) under '{assetsDir}': {string.Join(", ", missing)}. Rebuild via: dotnet build src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj");
        }
        else if (File.Exists(Path.Combine(assetsDir, OptionalHeroModelRelativePath)))
        {
            string voiceDir = Path.Combine(assetsDir, "sounds", "empath");
            int voiceCount = Directory.Exists(voiceDir) ? Directory.GetFiles(voiceDir, "*.ogg").Length : 0;
            long modelBytes = new FileInfo(Path.Combine(assetsDir, OptionalHeroModelRelativePath)).Length;
            LogViewport($"[Kongor3D] Verified all {RequiredViewerAssetRelativePaths.Count} core 3D viewport assets + hero overlay in '{assetsDir}' (empath_cn.glb={modelBytes} bytes, voiceLines={voiceCount}).");
        }
        else
        {
            LogViewport($"[Kongor3D] Cleanroom mode active in '{assetsDir}' (all {RequiredViewerAssetRelativePaths.Count} core JS/HTML assets verified; optional hero GLB overlay absent -> using native SkiaCrestCanvas emblem).");
        }

        return assetsDir;
    }

    private static string? TryFindRepoRoot(string startDir)
    {
        DirectoryInfo? dir = new(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "src", "services", "web-ui", "wwwroot")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string filePath in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destinationDir, Path.GetFileName(filePath));
            if (!File.Exists(destFile))
            {
                File.Copy(filePath, destFile, overwrite: false);
            }
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    public void ToggleMode()
    {
        if (DisplayMode == ViewportDisplayMode.MinimalistCrest)
        {
            if (!OperatingSystem.IsWindows() || IsTestEnvironment())
                return;

            DisplayMode = ViewportDisplayMode.ThreeDimensionalHero;

            if (_webView2Host == null)
            {
                _webView2Host = new EmpathWebView2HostControl(HandleWebViewHostFailure)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                Children.Insert(0, _webView2Host);
            }

            _crestCanvas.IsVisible = false;
            _webView2Host.IsVisible = true;
        }
        else
        {
            DisplayMode = ViewportDisplayMode.MinimalistCrest;

            if (_webView2Host != null)
            {
                _webView2Host.IsVisible = false;
            }

            _crestCanvas.IsVisible = true;
        }
    }

    // ========================================================================
    // Native Win32 HWND + WebView2 Host for Three.js 3D Character Model
    // ========================================================================
    private sealed partial class EmpathWebView2HostControl(Action<string>? onFatalFailure = null) : NativeControlHost
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private CoreWebView2Controller? _controller;
        private CoreWebView2? _coreWebView2;
        private string? _tempUserDataFolder;
        private string _mappedAssetsDir = string.Empty;
        private bool _isHiddenForModal;

        public bool IsWebViewInitialized { get; private set; }

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
            IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public void HideForModal()
        {
            _isHiddenForModal = true;
            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW);
            }
        }

        public void ShowFromModal()
        {
            _isHiddenForModal = false;
            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
                UpdateBounds();
            }
        }

        public void TriggerKnockout()
        {
            if (_coreWebView2 != null && IsWebViewInitialized)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { _ = _coreWebView2.ExecuteScriptAsync("window.triggerKnockout && window.triggerKnockout();"); }
                    catch (Exception ex) { LogViewport($"[Kongor3D][WARN] TriggerKnockout script error: {ex.Message}"); }
                });
            }
        }

        public void PlayQuitSound()
        {
            if (_coreWebView2 != null && IsWebViewInitialized)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { _ = _coreWebView2.ExecuteScriptAsync("window.playQuitSound && window.playQuitSound();"); }
                    catch (Exception ex) { LogViewport($"[Kongor3D][WARN] PlayQuitSound script error: {ex.Message}"); }
                });
            }
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                double scale = VisualRoot?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                int width = (int)Math.Ceiling(Math.Max(180, Bounds.Width) * scale);
                int height = (int)Math.Ceiling(Math.Max(220, Bounds.Height) * scale);

                _hwnd = CreateWindowEx(
                    0,
                    "STATIC",
                    string.Empty,
                    WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                    0, 0, width, height,
                    parent.Handle,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    int win32Err = Marshal.GetLastWin32Error();
                    string msg = $"CreateWindowExW returned NULL HWND (Win32 error {win32Err})";
                    LogViewport($"[Kongor3D][ERROR] {msg}");
                    onFatalFailure?.Invoke(msg);
                    return base.CreateNativeControlCore(parent);
                }

                LogViewport($"[Kongor3D] Created Win32 child HWND=0x{_hwnd:X} ({width}x{height}, scale={scale:F2}).");
                _ = InitializeWebView2Async(_hwnd);
                return new PlatformHandle(_hwnd, "HWND");
            }

            return base.CreateNativeControlCore(parent);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (_coreWebView2 is not null)
            {
                _coreWebView2.NavigationCompleted -= OnWebViewNavigationCompleted;
                _coreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived;
                _coreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _coreWebView2.ProcessFailed -= OnWebViewProcessFailed;
                _coreWebView2 = null;
            }

            _controller?.Close();
            _controller = null;

            if (_hwnd != IntPtr.Zero)
            {
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_tempUserDataFolder is not null && Directory.Exists(_tempUserDataFolder))
            {
                try { Directory.Delete(_tempUserDataFolder, true); }
                catch (Exception ex) { Debug.WriteLine($"[Kongor3D] Temp WebView2 folder cleanup deferred: {ex.Message}"); }
            }

            base.DestroyNativeControlCore(control);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (TopLevel.GetTopLevel(this) is { } topLevel) topLevel.ScalingChanged += OnScalingChanged;
            UpdateBounds();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is { } topLevel) topLevel.ScalingChanged -= OnScalingChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnScalingChanged(object? sender, EventArgs e) => UpdateBounds();

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateBounds();
        }

        private void UpdateBounds()
        {
            if (_hwnd != IntPtr.Zero && Bounds is { Width: > 0, Height: > 0 } && !_isHiddenForModal)
            {
                double scale = VisualRoot?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                int physicalW = (int)Math.Ceiling(Bounds.Width * scale);
                int physicalH = (int)Math.Ceiling(Bounds.Height * scale);

                _ = SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, physicalW, physicalH, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

                if (_controller is not null)
                {
                    _controller.RasterizationScale = scale;
                    _controller.Bounds = new Rectangle(0, 0, physicalW, physicalH);
                    _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 23, 42);
                }
            }
        }

        private async Task InitializeWebView2Async(IntPtr hwnd)
        {
            try
            {
                _mappedAssetsDir = ResolveAndPrepareAssetsDirectory();
                _tempUserDataFolder = Path.Combine(Path.GetTempPath(), $"NEXUS_ControlPanel_WebView2_{Environment.ProcessId}");

                LogViewport($"[Kongor3D] Initializing WebView2 environment (userData='{_tempUserDataFolder}', assetsDir='{_mappedAssetsDir}')...");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _tempUserDataFolder);

                _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
                _coreWebView2 = _controller.CoreWebView2;

                _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 23, 42);

                double scale = VisualRoot?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
                int w = (int)Math.Ceiling(Math.Max(180, Bounds.Width) * scale);
                int h = (int)Math.Ceiling(Math.Max(220, Bounds.Height) * scale);
                _controller.RasterizationScale = scale;
                _controller.Bounds = new Rectangle(0, 0, w, h);

                _coreWebView2.NavigationCompleted += OnWebViewNavigationCompleted;
                _coreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
                _coreWebView2.WebMessageReceived += OnWebMessageReceived;
                _coreWebView2.ProcessFailed += OnWebViewProcessFailed;

                _coreWebView2.SetVirtualHostNameToFolderMapping(
                    "nexus.local",
                    _mappedAssetsDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                _coreWebView2.Settings.IsStatusBarEnabled = false;
                _coreWebView2.Settings.AreDevToolsEnabled = true;
                _coreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _coreWebView2.Settings.IsWebMessageEnabled = true;

                const string viewerUri = "https://nexus.local/web/kongor_viewer.html";
                LogViewport($"[Kongor3D] WebView2 runtime v{env.BrowserVersionString} ready; navigating to '{viewerUri}'.");
                _coreWebView2.Navigate(viewerUri);
                IsWebViewInitialized = true;
            }
            catch (Exception ex)
            {
                string errMsg = $"{ex.GetType().Name}: {ex.Message}";
                LogViewport($"[Kongor3D][ERROR] WebView2 initialization failed: {errMsg}. Verify Microsoft Edge WebView2 Runtime is installed or build via: dotnet build src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj");
                Debug.WriteLine($"[Kongor3D] WebView2 Initialization error: {errMsg}");
                onFatalFailure?.Invoke(errMsg);
            }
        }

        private void OnWebViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                LogViewport($"[Kongor3D] NavigationCompleted succeeded (NavigationId={e.NavigationId}).");
            }
            else
            {
                LogViewport($"[Kongor3D][ERROR] NavigationCompleted failed (Status={e.WebErrorStatus}, HttpStatusCode={e.HttpStatusCode}, assetsDir='{_mappedAssetsDir}').");
            }
        }

        private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                int status = e.Response.StatusCode;
                if (status >= 400)
                {
                    LogViewport($"[Kongor3D][ERROR] HTTP {status} loading '{e.Request.Uri}' from mapped folder '{_mappedAssetsDir}'. Rebuild via: dotnet build src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kongor3D] WebResourceResponseReceived inspect warning: {ex.Message}");
            }
        }

        private static void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    LogViewport($"[Kongor3D][Viewer] {raw}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kongor3D] WebMessageReceived parse warning: {ex.Message}");
            }
        }

        private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            string msg = $"Kind={e.ProcessFailedKind}, Reason={e.Reason}, ExitCode={e.ExitCode}";
            LogViewport($"[Kongor3D][ERROR] WebView2 process failed ({msg}).");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                onFatalFailure?.Invoke(msg);
            }
        }
    }

    // ========================================================================
    // Avalonia-Native Skia GPU Vector Crest (Lightweight Minimalist Default)
    // ========================================================================
    private sealed class SkiaCrestCanvas : Control
    {
        private DispatcherTimer? _animationTimer;
        private double _pulsePhase;
        private double _orbitAngle;
        private double _targetParallaxX;
        private double _targetParallaxY;
        private double _currentParallaxX;
        private double _currentParallaxY;
        private bool _isKnockedState;
        private double _knockoutIntensity;
        private long _lastKnockoutTime;
        private bool _isPointerOver;
        private double _clickPulseRadius;
        private double _clickPulseAlpha;

        private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42)); // #0F172A
        private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 51, 65, 85)), 1.0);
        private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(25, 148, 163, 184)), 0.8);
        private static readonly IBrush TextDimBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        private static readonly IBrush TextCyanBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        private static readonly IBrush TextCrimsonBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

        private static readonly Typeface SubtitleTypeface = new("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold);
        private static readonly Typeface MicroTypeface = new("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.Medium);

        public SkiaCrestCanvas()
        {
            ClipToBounds = true;
        }

        public void TriggerKnockout()
        {
            long now = Environment.TickCount64;
            if (now - _lastKnockoutTime < 2500) return;
            _lastKnockoutTime = now;

            Dispatcher.UIThread.Post(() =>
            {
                _isKnockedState = true;
                _knockoutIntensity = 1.0;
                InvalidateVisual();
            });
        }

        public void PlayQuitSound()
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { SoundEffectService.PlayBanjoQuitSync(); }
                catch { /* audio fallback */ }
            });
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateAnimationTimerState();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            UpdateAnimationTimerState();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty)
            {
                UpdateAnimationTimerState();
            }
        }

        private void UpdateAnimationTimerState()
        {
            if (!IsTestEnvironment() && IsVisible && VisualRoot != null)
            {
                if (_animationTimer == null)
                {
                    _animationTimer = new DispatcherTimer(
                        TimeSpan.FromMilliseconds(33),
                        DispatcherPriority.Render,
                        OnAnimationTick);
                    _animationTimer.Start();
                }
            }
            else
            {
                if (_animationTimer != null)
                {
                    _animationTimer.Stop();
                    _animationTimer = null;
                }
            }
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            _orbitAngle += 0.035;
            if (_orbitAngle > Math.PI * 2) _orbitAngle -= Math.PI * 2;

            _pulsePhase += 0.055;
            if (_pulsePhase > Math.PI * 2) _pulsePhase -= Math.PI * 2;

            _currentParallaxX += (_targetParallaxX - _currentParallaxX) * 0.15;
            _currentParallaxY += (_targetParallaxY - _currentParallaxY) * 0.15;

            if (_clickPulseAlpha > 0)
            {
                _clickPulseRadius += 4.5;
                _clickPulseAlpha = Math.Max(0, _clickPulseAlpha - 0.05);
            }

            if (_knockoutIntensity > 0)
            {
                _knockoutIntensity = Math.Max(0, _knockoutIntensity - 0.012);
                if (_knockoutIntensity <= 0) _isKnockedState = false;
            }

            InvalidateVisual();
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _isPointerOver = true;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _isPointerOver = false;
            _targetParallaxX = 0;
            _targetParallaxY = 0;
            if (IsTestEnvironment()) InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point pos = e.GetPosition(this);
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w > 0 && h > 0)
            {
                _targetParallaxX = Math.Clamp((pos.X - w / 2.0) / (w / 2.0), -1.0, 1.0);
                _targetParallaxY = Math.Clamp((pos.Y - h / 2.0) / (h / 2.0), -1.0, 1.0);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            _clickPulseRadius = 8.0;
            _clickPulseAlpha = 1.0;

            _ = Task.Run(() =>
            {
                try { SoundEffectService.PlayErrorAlert(); }
                catch { /* audio fallback */ }
            });

            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            double width = Bounds.Width;
            double height = Bounds.Height;
            if (width <= 0 || height <= 0) return;

            Rect boundsRect = new(0, 0, width, height);
            context.DrawRectangle(BackgroundBrush, BorderPen, new RoundedRect(boundsRect, 10));

            DrawViewportGrid(context, width, height);

            double cx = width / 2.0 + _currentParallaxX * 14.0;
            double cy = (height * 0.46) + _currentParallaxY * 10.0;
            double baseRadius = Math.Clamp(Math.Min(width, height) * 0.28, 30.0, 80.0);

            DrawAmbientAura(context, cx, cy, baseRadius, width, height);
            DrawOrbitalRings(context, cx, cy, baseRadius);
            DrawEmpathCrest(context, cx, cy, baseRadius);

            if (_clickPulseAlpha > 0)
            {
                Color rippleColor = _isKnockedState
                    ? Color.FromArgb((byte)(_clickPulseAlpha * 200), 239, 68, 68)
                    : Color.FromArgb((byte)(_clickPulseAlpha * 220), 56, 189, 248);
                IPen ripplePen = new Pen(new SolidColorBrush(rippleColor), 1.8);
                context.DrawEllipse(null, ripplePen, new Point(cx, cy), _clickPulseRadius, _clickPulseRadius * 0.85);
            }
        }

        private static void DrawViewportGrid(DrawingContext context, double width, double height)
        {
            double step = 32.0;
            for (double x = step; x < width; x += step)
                context.DrawLine(GridPen, new Point(x, 4), new Point(x, height - 4));
            for (double y = step; y < height; y += step)
                context.DrawLine(GridPen, new Point(4, y), new Point(width - 4, y));
        }

        private void DrawAmbientAura(DrawingContext context, double cx, double cy, double baseRadius, double width, double height)
        {
            double auraPulse = Math.Sin(_pulsePhase) * 0.12;
            double auraRadius = baseRadius * (1.6 + auraPulse);
            byte alpha = (byte)Math.Clamp((0.18 + auraPulse) * 255.0, 30, 90);
            Color glowColor = _knockoutIntensity > 0
                ? Color.FromArgb(alpha, 239, 68, 68)
                : Color.FromArgb(alpha, 56, 189, 248);

            IBrush glowBrush = new RadialGradientBrush
            {
                Center = new RelativePoint(cx, cy, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(cx, cy, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(auraRadius, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(auraRadius * 0.9, RelativeUnit.Absolute),
                GradientStops =
                [
                    new GradientStop(glowColor, 0.0),
                    new GradientStop(Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B), 1.0)
                ]
            };
            context.DrawEllipse(glowBrush, null, new Point(cx, cy), auraRadius, auraRadius * 0.9);
        }

        private void DrawOrbitalRings(DrawingContext context, double cx, double cy, double baseRadius)
        {
            double ringW = baseRadius * 1.5;
            double ringH = baseRadius * 0.55;

            Color ringColor1 = _knockoutIntensity > 0 ? Color.FromArgb(160, 239, 68, 68) : Color.FromArgb(170, 56, 189, 248);
            IPen ringPen1 = new Pen(new SolidColorBrush(ringColor1), 1.4);

            using (context.PushTransform(Matrix.CreateTranslation(-cx, -cy) *
                                        Matrix.CreateRotation(_orbitAngle * 0.5) *
                                        Matrix.CreateTranslation(cx, cy)))
            {
                context.DrawEllipse(null, ringPen1, new Point(cx, cy), ringW, ringH);
            }

            Color ringColor2 = _knockoutIntensity > 0 ? Color.FromArgb(140, 251, 191, 36) : Color.FromArgb(160, 192, 132, 252);
            IPen ringPen2 = new Pen(new SolidColorBrush(ringColor2), 1.2);

            using (context.PushTransform(Matrix.CreateTranslation(-cx, -cy) *
                                        Matrix.CreateRotation(-_orbitAngle * 0.7 + Math.PI / 3.0) *
                                        Matrix.CreateTranslation(cx, cy)))
            {
                context.DrawEllipse(null, ringPen2, new Point(cx, cy), ringW * 0.88, ringH * 0.9);
            }
        }

        private void DrawEmpathCrest(DrawingContext context, double cx, double cy, double baseRadius)
        {
            Color crestColor = _knockoutIntensity > 0 ? Color.FromRgb(239, 68, 68) : Color.FromRgb(56, 189, 248);
            IPen crestPen = new Pen(new SolidColorBrush(crestColor), 2.0);
            IBrush coreFillBrush = new SolidColorBrush(Color.FromArgb(40, crestColor.R, crestColor.G, crestColor.B));

            double r = baseRadius * 0.72;
            double tiltOffset = _currentParallaxX * 6.0;

            Point top = new(cx + tiltOffset, cy - r);
            Point right = new(cx + r * 0.75, cy);
            Point bottom = new(cx - tiltOffset, cy + r * 0.95);
            Point left = new(cx - r * 0.75, cy);

            StreamGeometry diamond = new();
            using (StreamGeometryContext sgc = diamond.Open())
            {
                sgc.BeginFigure(top, true);
                sgc.LineTo(right);
                sgc.LineTo(bottom);
                sgc.LineTo(left);
                sgc.EndFigure(true);
            }
            context.DrawGeometry(coreFillBrush, crestPen, diamond);

            IPen latticePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.0);
            context.DrawLine(latticePen, top, bottom);
            context.DrawLine(latticePen, left, right);

            StreamGeometry leftWing = new();
            using (StreamGeometryContext sgc = leftWing.Open())
            {
                sgc.BeginFigure(new Point(cx - r * 0.4, cy - r * 0.3), false);
                sgc.CubicBezierTo(
                    new Point(cx - r * 1.5, cy - r * 0.9),
                    new Point(cx - r * 1.3, cy + r * 0.4),
                    new Point(cx - r * 0.2, cy + r * 0.5));
            }
            context.DrawGeometry(null, crestPen, leftWing);

            StreamGeometry rightWing = new();
            using (StreamGeometryContext sgc = rightWing.Open())
            {
                sgc.BeginFigure(new Point(cx + r * 0.4, cy - r * 0.3), false);
                sgc.CubicBezierTo(
                    new Point(cx + r * 1.5, cy - r * 0.9),
                    new Point(cx + r * 1.3, cy + r * 0.4),
                    new Point(cx + r * 0.2, cy + r * 0.5));
            }
            context.DrawGeometry(null, crestPen, rightWing);

            double beaconPulse = Math.Sin(_pulsePhase * 2.0) * 1.8;
            double beaconRadius = Math.Max(2.5, 4.0 + beaconPulse);
            IBrush beaconBrush = new SolidColorBrush(_knockoutIntensity > 0 ? Color.FromRgb(251, 191, 36) : Color.FromRgb(255, 255, 255));
            context.DrawEllipse(beaconBrush, null, new Point(cx, cy), beaconRadius, beaconRadius);
        }

        private void DrawFooterStatus(DrawingContext context, double width, double height)
        {
            double y = height - 28.0;

            string title = _isKnockedState ? "TAO DISRUPTED" : "TOGETHER AS ONE";
            IBrush titleBrush = _isKnockedState ? TextCrimsonBrush : TextCyanBrush;

            FormattedText titleText = new(
                title,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                SubtitleTypeface,
                10.0,
                titleBrush);

            double titleX = (width - titleText.Width) / 2.0;
            context.DrawText(titleText, new Point(Math.Max(6, titleX), y));

            string sub = _isKnockedState ? "RECOVERING TELEMETRY..." : (_isPointerOver ? "CLICK FOR AUDIO CUE" : "GPU ACCELERATED // SKIA");
            FormattedText subText = new(
                sub,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MicroTypeface,
                8.0,
                TextDimBrush);

            double subX = (width - subText.Width) / 2.0;
            context.DrawText(subText, new Point(Math.Max(6, subX), y + 13.0));
        }
    }
}
