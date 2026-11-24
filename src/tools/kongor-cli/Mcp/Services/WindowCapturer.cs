// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Bgra32 = Kongor.Cli.Mcp.Imaging.Bgra32;
using Image = Kongor.Cli.Mcp.Imaging.Image;
using Rgba32 = Kongor.Cli.Mcp.Imaging.Rgba32;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of a window capture operation.
/// </summary>
public enum WindowCaptureErrorCode
{
    None,
    UnsupportedPlatform,
    WindowNotFound,
    InvalidWindowHandle,
    EmptyOrBlackFrame,
    DiskFlushTimeout,
    CaptureFailed
}

/// <summary>
/// Result of a window capture operation.
/// </summary>
public sealed record WindowCaptureResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Hwnd { get; init; } = "0x00000000";
    public int Pid { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? ArtifactPath { get; init; }
    public string Base64Png { get; init; } = string.Empty;
    public string CaptureMethodUsed { get; init; } = "none";
    public string? RequestedCaptureMethod { get; init; }
    public string? FallbackReason { get; init; }
    public string? ErrorMessage { get; init; }
    public WindowCaptureErrorCode ErrorCode { get; init; } = WindowCaptureErrorCode.None;
    public string? PreviewPath { get; init; }
    public AutoHdrStatus? AutoHdr { get; init; }
    public double UserIdleSeconds { get; init; }
}

/// <summary>
/// Service interface for live Win32 window screen capture.
/// </summary>
/// <summary>
/// Status of Windows Auto HDR detection for SDR rendering verification.
/// </summary>
public sealed record AutoHdrStatus
{
    public bool AutoHdrActive { get; init; }
    public int RawValue { get; init; }
    public string State { get; init; } = "NOT_SET";
    public string SourceKey { get; init; } = "NONE";
}

public interface IWindowCapturer
{
    Task<WindowCaptureResult> CaptureAsync(
        string target = "hon_x64",
        string captureMethod = "auto",
        bool saveArtifact = true,
        string? customOutputPath = null,
        bool stealFocus = false,
        CancellationToken cancellationToken = default);

    Task<WindowCaptureResult> CaptureHwndAsync(
        IntPtr hwnd,
        string targetName = "explicit_hwnd",
        string captureMethod = "auto",
        bool saveArtifact = true,
        string? customOutputPath = null,
        bool stealFocus = false,
        CancellationToken cancellationToken = default);

    Task<WindowCaptureResult> CaptureTransientAsync(
        string target = "hon_x64",
        int dwellTimeMs = 350,
        int captureWidth = 1280,
        int captureHeight = 720,
        int offscreenX = -32000,
        int offscreenY = -32000,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(target, "auto", saveArtifact, customOutputPath, stealFocus: false, cancellationToken);

    Task<WindowCaptureResult> CaptureHwndTransientAsync(
        IntPtr hwnd,
        string targetName = "explicit_hwnd",
        int dwellTimeMs = 350,
        int captureWidth = 1280,
        int captureHeight = 720,
        int offscreenX = -10000,
        int offscreenY = -10000,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default) =>
        CaptureHwndAsync(hwnd, targetName, "auto", saveArtifact, customOutputPath, stealFocus: false, cancellationToken);

    Task<WindowCaptureResult> CaptureVulkanOffscreenAsync(
        int pid,
        string targetName = "hon_x64",
        int timeoutMs = 2000,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(targetName, "vulkan_offscreen", saveArtifact, customOutputPath, stealFocus: false, cancellationToken);
}

/// <summary>
/// High-performance Win32 hardware window screen capturer with PerMonitorV2 DPI awareness,
/// DWM extended frame bounds, multi-tier capture cascade (Tier 0: Vulkan Swapchain -> Tier 1: PrintWindow -> Tier 2: Window DC -> Tier 3: Desktop DC),
/// and SixLabors.ImageSharp PNG encoding.
/// </summary>
public sealed class WindowCapturer : IWindowCapturer
{
    private readonly ILogger<WindowCapturer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IVulkanFrameGrabber _vulkanGrabber;
    private static bool _dpiAwarenessSet;
    private static readonly Lock _dpiLock = new();

    /// <summary>
    /// Event triggered when transient capture starts (true) and ends (false) so background window watchdogs don't interrupt.
    /// </summary>
    public static Action<bool>? OnTransientCaptureStateChanged;

    private sealed class NullVulkanFrameGrabber : IVulkanFrameGrabber
    {
        public bool IsSharedMemoryAvailable(int processId) => false;
        public Task<VulkanFrameResult?> GrabSingleFrameAsync(int processId, int timeoutMs = 1500, CancellationToken cancellationToken = default) => Task.FromResult<VulkanFrameResult?>(null);
        public Task<VulkanFrameResult?> GrabSingleFrameFromMapAsync(string mapName, string readyEventPrefix, string doneEventPrefix, int slotCount = 3, int timeoutMs = 1500, CancellationToken cancellationToken = default) => Task.FromResult<VulkanFrameResult?>(null);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public WindowCapturer(
        ILogger<WindowCapturer>? logger = null,
        TimeProvider? timeProvider = null,
        IVulkanFrameGrabber? vulkanGrabber = null)
    {
        _logger = logger ?? NullLogger<WindowCapturer>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (vulkanGrabber != null)
        {
            _vulkanGrabber = vulkanGrabber;
        }
        else if (OperatingSystem.IsWindows())
        {
            _vulkanGrabber = new VulkanMemoryMappedFrameGrabber(timeProvider: _timeProvider);
        }
        else
        {
            _vulkanGrabber = new NullVulkanFrameGrabber();
        }
        EnsureDpiAwareness();
    }

    private void EnsureDpiAwareness()
    {
        if (_dpiAwarenessSet) return;
        lock (_dpiLock)
        {
            if (_dpiAwarenessSet) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                    SetProcessDpiAwarenessContext(new IntPtr(-4));
                    _logger.LogInformation("[WindowCapturer] Configured PerMonitorV2 DPI awareness context.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WindowCapturer] Failed to set PerMonitorV2 DPI awareness: {Message}", ex.Message);
            }
            finally
            {
                _dpiAwarenessSet = true;
            }
        }
    }

    private sealed class DesktopScope : IDisposable
    {
        private readonly IntPtr _hDesktop;

        public DesktopScope(IntPtr hDesktop)
        {
            _hDesktop = hDesktop;
        }

        public void Dispose()
        {
            if (_hDesktop != IntPtr.Zero)
            {
                try { CloseDesktop(_hDesktop); } catch { }
            }
        }
    }

    private static IntPtr s_cachedWinsta = IntPtr.Zero;
    private static readonly Lock s_winstaLock = new();

    private static void EnsureProcessWindowStation()
    {
        if (s_cachedWinsta != IntPtr.Zero) return;
        lock (s_winstaLock)
        {
            if (s_cachedWinsta != IntPtr.Zero) return;
            try
            {
                IntPtr hWinsta = OpenWindowStation("WinSta0", false, 0x000F037F);
                if (hWinsta != IntPtr.Zero)
                {
                    if (SetProcessWindowStation(hWinsta))
                    {
                        s_cachedWinsta = hWinsta;
                    }
                    else
                    {
                        CloseWindowStation(hWinsta);
                    }
                }
            }
            catch { }
        }
    }

    public static IDisposable? EnsureDesktopAttachment()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        EnsureProcessWindowStation();

        try
        {
            IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
            if (hDesk == IntPtr.Zero)
            {
                hDesk = OpenInputDesktop(0, false, 0x01FF);
            }

            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
                return new DesktopScope(hDesk);
            }
        }
        catch { }

        return null;
    }

    public Task<WindowCaptureResult> CaptureAsync(
        string target = "hon_x64",
        string captureMethod = "auto",
        bool saveArtifact = true,
        string? customOutputPath = null,
        bool stealFocus = false,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var tcs = new TaskCompletionSource<WindowCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                using var desktopScope = EnsureDesktopAttachment();
                try
                {
#pragma warning disable RS0030 // Justification: Dedicated Win32 STA capture thread bridge
                    System.Runtime.CompilerServices.TaskAwaiter<WindowCaptureResult> awaiter = CaptureCoreAsync(target, captureMethod, saveArtifact, customOutputPath, stealFocus, cancellationToken).GetAwaiter();
                    WindowCaptureResult result = awaiter.GetResult();
#pragma warning restore RS0030
                    tcs.SetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.SetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        return CaptureCoreAsync(target, captureMethod, saveArtifact, customOutputPath, stealFocus, cancellationToken);
    }

    private async Task<WindowCaptureResult> CaptureCoreAsync(
        string target,
        string captureMethod,
        bool saveArtifact,
        string? customOutputPath,
        bool stealFocus,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[WindowCapturer] Initiating capture for target: '{Target}', method: '{Method}', stealFocus: {StealFocus}", target, captureMethod, stealFocus);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = target,
                ErrorMessage = "Win32 window capture is only supported on Windows operating systems.",
                ErrorCode = WindowCaptureErrorCode.UnsupportedPlatform
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero)
        {
            if (pid > 0 && (captureMethod is "vulkan_offscreen" or "vulkan" || _vulkanGrabber.IsSharedMemoryAvailable(pid)))
            {
                _logger.LogInformation("[WindowCapturer] Target HWND is zero/offscreen, but resolving directly via Vulkan shared memory for PID {Pid}...", pid);
                WindowCaptureResult vRes = await CaptureVulkanOffscreenAsync(pid, target, 5000, saveArtifact, customOutputPath, cancellationToken).ConfigureAwait(false);
                if (vRes.Success)
                {
                    return vRes;
                }

                hwnd = FindWindowForProcessId(pid);
            }

            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("[WindowCapturer] Could not resolve any active window handle for target: '{Target}'", target);
                return new WindowCaptureResult
                {
                    Success = false,
                    Target = target,
                    ErrorMessage = $"Target window not found for query '{target}'. Make sure the target process or window is currently open.",
                    ErrorCode = WindowCaptureErrorCode.WindowNotFound
                };
            }
        }

        return await CaptureHwndAsync(hwnd, target, captureMethod, saveArtifact, customOutputPath, stealFocus, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WindowCaptureResult> CaptureHwndAsync(
        IntPtr hwnd,
        string targetName = "explicit_hwnd",
        string captureMethod = "auto",
        bool saveArtifact = true,
        string? customOutputPath = null,
        bool stealFocus = false,
        CancellationToken cancellationToken = default)
    {
        using var desktopScope = EnsureDesktopAttachment();
        _logger.LogInformation("[WindowCapturer] Capturing HWND 0x{HWND:X8} (Target: '{Target}', StealFocus: {StealFocus})", hwnd.ToInt64(), targetName, stealFocus);

        if (!IsWindow(hwnd))
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = targetName,
                Hwnd = $"0x{hwnd.ToInt64():X8}",
                ErrorMessage = $"Invalid or destroyed window handle: 0x{hwnd.ToInt64():X8}",
                ErrorCode = WindowCaptureErrorCode.InvalidWindowHandle
            };
        }

        GetWindowThreadProcessId(hwnd, out uint pidUint);
        int pid = (int)pidUint;
        string windowTitle = GetWindowTitle(hwnd);
        double userIdleSec = GetUserIdleSeconds();

        if (stealFocus && userIdleSec >= 3.0)
        {
            try
            {
                ShowWindow(hwnd, 9); // SW_RESTORE
                IntPtr fgWnd = GetForegroundWindow();
                uint fgThread = fgWnd != IntPtr.Zero ? GetWindowThreadProcessId(fgWnd, out _) : 0;
                uint curThread = GetCurrentThreadId();
                if (fgThread != 0 && fgThread != curThread)
                {
                    AttachThreadInput(curThread, fgThread, true);
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                    SwitchToThisWindow(hwnd, true);
                    AttachThreadInput(curThread, fgThread, false);
                }
                else
                {
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                    SwitchToThisWindow(hwnd, true);
                }
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }

        // Compute exact visual bounds with DWM extended frame bounds
        RECT bounds = GetAccurateWindowBounds(hwnd);
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;

        // If offscreen, minimized, or empty bounds, check client rect for true render dimensions
        if (width <= 0 || height <= 0 || bounds.Left <= -5000 || bounds.Top <= -5000 || bounds.Left >= 5000 || bounds.Top >= 5000)
        {
            if (GetClientRect(hwnd, out RECT clientRect) && clientRect.Right > 0 && clientRect.Bottom > 0)
            {
                width = clientRect.Right - clientRect.Left;
                height = clientRect.Bottom - clientRect.Top;
            }

            if (stealFocus && userIdleSec >= 3.0)
            {
                IntPtr realHwnd = FindWindowForProcessId(pid);
                if (realHwnd != IntPtr.Zero && realHwnd != hwnd)
                {
                    hwnd = realHwnd;
                    try
                    {
                        ShowWindow(hwnd, 9);
                        BringWindowToTop(hwnd);
                        SwitchToThisWindow(hwnd, true);
                        SetForegroundWindow(hwnd);
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                    catch { }
                    bounds = GetAccurateWindowBounds(hwnd);
                    width = bounds.Right - bounds.Left;
                    height = bounds.Bottom - bounds.Top;
                    windowTitle = GetWindowTitle(hwnd);
                }
            }
        }

        if (width <= 0) { width = 1920; }
        if (height <= 0) { height = 1080; }

        _logger.LogInformation("[WindowCapturer] Window bounds for 0x{HWND:X8}: {W}x{H} at ({L},{T})", hwnd.ToInt64(), width, height, bounds.Left, bounds.Top);

        byte[]? pixelBuffer = null;
        string requestedMethod = captureMethod.ToLowerInvariant();
        string methodUsed = requestedMethod;
        string? fallbackReason = null;

        try
        {
            if (methodUsed is "vulkan_offscreen" or "vulkan")
            {
                if (hwnd != IntPtr.Zero)
                {
                    try
                    {
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0100 /* RDW_UPDATENOW */ | 0x0001 /* RDW_INVALIDATE */);
                    }
                    catch { }
                }

                WindowCaptureResult vulkanRes = await CaptureVulkanOffscreenAsync(pid, targetName, 5000, saveArtifact, customOutputPath, cancellationToken).ConfigureAwait(false);
                if (vulkanRes.Success)
                {
                    return vulkanRes with { RequestedCaptureMethod = requestedMethod };
                }

                fallbackReason = vulkanRes.ErrorMessage ?? "Vulkan offscreen shared memory frame unavailable.";
                _logger.LogInformation("[WindowCapturer] vulkan_offscreen capture unavailable for PID {Pid} ({Error}); cascading to PrintWindow fallback (MCP-048 / MCP-298).", pid, fallbackReason);
                pixelBuffer = CaptureWithPrintWindow(hwnd, width, height) ?? CaptureWithWindowDc(hwnd, width, height) ?? CaptureWithDesktopDc(bounds, width, height);
                methodUsed = "print_window";
            }
            else if (methodUsed is "print_window" or "printwindow")
            {
                pixelBuffer = CaptureWithPrintWindow(hwnd, width, height) ?? CaptureWithWindowDc(hwnd, width, height) ?? CaptureWithDesktopDc(bounds, width, height);
                methodUsed = "print_window";
            }
            else if (methodUsed is "window_dc" or "windowdc")
            {
                pixelBuffer = CaptureWithWindowDc(hwnd, width, height) ?? CaptureWithPrintWindow(hwnd, width, height) ?? CaptureWithDesktopDc(bounds, width, height);
                methodUsed = "window_dc";
            }
            else if (methodUsed is "desktop_dc" or "desktopdc")
            {
                pixelBuffer = CaptureWithDesktopDc(bounds, width, height) ?? CaptureWithWindowDc(hwnd, width, height) ?? CaptureWithPrintWindow(hwnd, width, height);
                methodUsed = "desktop_dc";
            }
            else // "auto" multi-tier cascade
            {
                _logger.LogInformation("[WindowCapturer] Executing multi-tier capture cascade (Tier 0: Vulkan Swapchain -> Tier 1: PrintWindow -> Tier 2: Window DC -> Tier 3: Desktop DC)...");

                // Tier 0: Vulkan Swapchain Direct Offscreen Memory (0ms dwell, zero window exposure)
                if (pid > 0 && _vulkanGrabber.IsSharedMemoryAvailable(pid))
                {
                    try
                    {
                        WindowCaptureResult vulkanRes = await CaptureVulkanOffscreenAsync(
                            pid, targetName, 5000, saveArtifact, customOutputPath, cancellationToken).ConfigureAwait(false);
                        if (vulkanRes.Success)
                        {
                            _logger.LogInformation("[WindowCapturer] Tier 0 (Vulkan Offscreen) captured valid frame with zero window exposure.");
                            return vulkanRes with { RequestedCaptureMethod = requestedMethod };
                        }
                        fallbackReason = vulkanRes.ErrorMessage;
                    }
                    catch (Exception ex)
                    {
                        fallbackReason = ex.Message;
                        _logger.LogDebug(ex, "[WindowCapturer] Tier 0 Vulkan capture encountered exception: {Msg}", ex.Message);
                    }
                }

                // Tier 1: PrintWindow
                try
                {
                    pixelBuffer = CaptureWithPrintWindow(hwnd, width, height);
                    if (pixelBuffer != null && HasNonBlackEntropy(pixelBuffer, width, height))
                    {
                        methodUsed = "print_window";
                        _logger.LogInformation("[WindowCapturer] Tier 1 (PrintWindow) captured valid frame.");
                    }
                    else
                    {
                        _logger.LogInformation("[WindowCapturer] Tier 1 (PrintWindow) returned blank/empty buffer. Falling back to Tier 2.");
                        pixelBuffer = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[WindowCapturer] Tier 1 PrintWindow failed: {Msg}", ex.Message);
                }

                // Tier 2: Window DC BitBlt
                if (pixelBuffer == null)
                {
                    try
                    {
                        pixelBuffer = CaptureWithWindowDc(hwnd, width, height);
                        if (pixelBuffer != null && HasNonBlackEntropy(pixelBuffer, width, height))
                        {
                            methodUsed = "window_dc";
                            _logger.LogInformation("[WindowCapturer] Tier 2 (Window DC BitBlt) captured valid frame.");
                        }
                        else
                        {
                            _logger.LogInformation("[WindowCapturer] Tier 2 (Window DC) returned blank/empty buffer. Falling back to Tier 3.");
                            pixelBuffer = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[WindowCapturer] Tier 2 Window DC failed: {Msg}", ex.Message);
                    }
                }

                // Tier 3: Desktop DC BitBlt
                if (pixelBuffer == null)
                {
                    try
                    {
                        pixelBuffer = CaptureWithDesktopDc(bounds, width, height);
                        if (pixelBuffer != null)
                        {
                            methodUsed = "desktop_dc";
                            _logger.LogInformation("[WindowCapturer] Tier 3 (Desktop DC BitBlt) captured frame.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[WindowCapturer] Tier 3 Desktop DC failed: {Msg}", ex.Message);
                    }
                }
            }

            if (pixelBuffer == null)
            {
                // MCP-298: Never raise an offscreen window onto the user's primary desktop at (0, 0) topmost unless stealFocus (explicit window exposure) was requested.
                if (stealFocus && (bounds.Left <= -5000 || bounds.Top <= -5000 || bounds.Left >= 50000 || bounds.Top >= 50000))
                {
                    _logger.LogInformation("[WindowCapturer] Window is parked offscreen ({L}, {T}), GDI capture failed, and stealFocus=true. Cascading to Transient capture fallback...", bounds.Left, bounds.Top);
                    try
                    {
                        var transientRes = await CaptureHwndTransientAsync(
                            hwnd,
                            targetName,
                            dwellTimeMs: 350,
                            captureWidth: width,
                            captureHeight: height,
                            saveArtifact: saveArtifact,
                            customOutputPath: customOutputPath,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (transientRes.Success)
                        {
                            return transientRes;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[WindowCapturer] Transient capture fallback failed: {Msg}", ex.Message);
                    }
                }

                return new WindowCaptureResult
                {
                    Success = false,
                    Target = targetName,
                    WindowTitle = windowTitle,
                    Hwnd = $"0x{hwnd.ToInt64():X8}",
                    Pid = pid,
                    Width = width,
                    Height = height,
                    ErrorMessage = $"Failed to capture pixel buffer from window 0x{hwnd.ToInt64():X8} across all capture modalities.",
                    ErrorCode = WindowCaptureErrorCode.CaptureFailed
                };
            }

            // Encode to Bgra32 Image
            using Kongor.Cli.Mcp.Imaging.Image<Bgra32> image = Image.LoadPixelData<Bgra32>(pixelBuffer, width, height);

            string? artifactPath = null;
        string? previewPath = null;
            if (saveArtifact || !string.IsNullOrWhiteSpace(customOutputPath))
            {
                string artifactDir = Path.Combine(Protocol.McpServer.FindProjectRoot(), "artifacts", "captures");
                Directory.CreateDirectory(artifactDir);

                string sanitizedTarget = SanitizeFileName(string.IsNullOrWhiteSpace(targetName) ? "window" : targetName);
                artifactPath = !string.IsNullOrWhiteSpace(customOutputPath)
                    ? customOutputPath
                    : Path.Combine(artifactDir, $"capture_{sanitizedTarget}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmssfff}.png");

                var (pathAllowed, validatedPath, pathRejection) = ResolveArtifactPath(saveArtifact, artifactPath, timeProvider: _timeProvider);
                if (!pathAllowed)
                {
                    _logger.LogWarning("[WindowCapturer] Rejected artifact path '{Path}': {Reason}", artifactPath, pathRejection);
                    artifactPath = null;
                }
                else
                {
                    artifactPath = validatedPath;
                }
                string? parentDir = !string.IsNullOrEmpty(artifactPath) ? Path.GetDirectoryName(artifactPath) : null;
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                if (!string.IsNullOrEmpty(artifactPath))
                {
                    image.SaveAsPng(artifactPath);
                    try { previewPath = GeneratePreview(artifactPath); } catch (Exception) { previewPath = null; }
                }
                _logger.LogInformation("[WindowCapturer] Saved PNG capture artifact to: '{Path}'", artifactPath);
            }

            using MemoryStream ms = new();
            image.SaveAsPng(ms);
            string base64Png = Convert.ToBase64String(ms.ToArray());

            _logger.LogInformation("[WindowCapturer] Capture succeeded for 0x{HWND:X8} ({W}x{H}, Method: {Method}, Base64 size: {Size} chars)",
                hwnd.ToInt64(), width, height, methodUsed, base64Png.Length);

            return new WindowCaptureResult
            {
                Success = true,
                Target = targetName,
                WindowTitle = windowTitle,
                Hwnd = $"0x{hwnd.ToInt64():X8}",
                Pid = pid,
                Width = width,
                Height = height,
                ArtifactPath = artifactPath,
                PreviewPath = previewPath,
                AutoHdr = DetectAutoHdr(targetName),
                UserIdleSeconds = userIdleSec,
                Base64Png = base64Png,
                CaptureMethodUsed = methodUsed,
                RequestedCaptureMethod = requestedMethod,
                FallbackReason = fallbackReason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WindowCapturer] Exception occurred during window capture: {Message}", ex.Message);
            return new WindowCaptureResult
            {
                Success = false,
                Target = targetName,
                WindowTitle = windowTitle,
                Hwnd = $"0x{hwnd.ToInt64():X8}",
                Pid = pid,
                Width = width,
                Height = height,
                ErrorMessage = $"Capture exception: {ex.Message}"
            };
        }
    }

    public async Task<WindowCaptureResult> CaptureVulkanOffscreenAsync(
        int pid,
        string targetName = "hon_x64",
        int timeoutMs = 5000,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[WindowCapturer] Capturing Vulkan offscreen frame for PID {Pid} (Target: '{Target}', Timeout: {Timeout}ms)...",
            pid, targetName, timeoutMs);

        if (pid <= 0)
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = targetName,
                ErrorMessage = $"Invalid target process ID: {pid}"
            };
        }

        // Startup Grace Period: If shared memory is not yet available, poll for up to 3000ms with 100ms interval
        if (!_vulkanGrabber.IsSharedMemoryAvailable(pid))
        {
            _logger.LogInformation("[WindowCapturer] Vulkan shared memory for PID {Pid} not yet available. Waiting for engine boot / swapchain initialization (up to 3000ms)...", pid);
            int waitedMs = 0;
            const int pollIntervalMs = 100;
            const int maxGraceMs = 3000;
            while (waitedMs < maxGraceMs && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
                waitedMs += pollIntervalMs;
                if (_vulkanGrabber.IsSharedMemoryAvailable(pid))
                {
                    _logger.LogInformation("[WindowCapturer] Vulkan shared memory became available after {Waited}ms.", waitedMs);
                    break;
                }
            }
        }

        VulkanFrameResult? frame = await _vulkanGrabber.GrabSingleFrameAsync(pid, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (frame == null || !frame.Success || frame.PixelData.Length == 0)
        {
            string err = frame?.ErrorMessage ?? $"Failed to acquire Vulkan swapchain frame from shared memory for PID {pid} (timeout or inactive layer). Verify that HON_CAPTURE_ENABLE=1 and VkLayer_hon_replay_capture.dll are active.";
            _logger.LogWarning("[WindowCapturer] Vulkan offscreen capture failed for PID {Pid}: {Error}", pid, err);
            return new WindowCaptureResult
            {
                Success = false,
                Target = targetName,
                Pid = pid,
                ErrorMessage = err,
                ErrorCode = WindowCaptureErrorCode.CaptureFailed
            };
        }

        int width = frame.Width;
        int height = frame.Height;
        bool isBgra = frame.Format == NEXUS.Contracts.Graphics.FrameHeader.FormatB8G8R8A8Unorm || frame.Format == 0;

        using Image image = isBgra
            ? Image.LoadPixelData<Bgra32>(frame.PixelData, width, height)
            : Image.LoadPixelData<Rgba32>(frame.PixelData, width, height);

        string? artifactPath = null;
        string? previewPath = null;
        if (saveArtifact || !string.IsNullOrWhiteSpace(customOutputPath))
        {
            string artifactDir = Path.Combine(Protocol.McpServer.FindProjectRoot(), "artifacts", "captures");
            Directory.CreateDirectory(artifactDir);

            string sanitizedTarget = SanitizeFileName(string.IsNullOrWhiteSpace(targetName) ? "window" : targetName);
            artifactPath = !string.IsNullOrWhiteSpace(customOutputPath)
                ? customOutputPath
                : Path.Combine(artifactDir, $"capture_{sanitizedTarget}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmssfff}.png");

            var (pathAllowed, validatedPath, pathRejection) = ResolveArtifactPath(saveArtifact, artifactPath, timeProvider: _timeProvider);
            if (!pathAllowed)
            {
                _logger.LogWarning("[WindowCapturer] Rejected artifact path '{Path}': {Reason}", artifactPath, pathRejection);
                artifactPath = null;
            }
            else
            {
                artifactPath = validatedPath;
            }
            string? parentDir = !string.IsNullOrEmpty(artifactPath) ? Path.GetDirectoryName(artifactPath) : null;
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            if (!string.IsNullOrEmpty(artifactPath))
            {
                image.SaveAsPng(artifactPath);
                try { previewPath = GeneratePreview(artifactPath); } catch (Exception) { previewPath = null; }
            }
            _logger.LogInformation("[WindowCapturer] Saved Vulkan offscreen PNG capture artifact to: '{Path}'", artifactPath);
        }

        using MemoryStream ms = new();
        image.SaveAsPng(ms);
        string base64Png = Convert.ToBase64String(ms.ToArray());

        _logger.LogInformation("[WindowCapturer] Vulkan offscreen capture succeeded for PID {Pid} ({W}x{H}, Base64 size: {Size} chars)",
            pid, width, height, base64Png.Length);

        return new WindowCaptureResult
        {
            Success = true,
            Target = targetName,
            WindowTitle = $"{targetName} (Vulkan Offscreen)",
            Hwnd = "0x00000000",
            Pid = pid,
            Width = width,
            Height = height,
            ArtifactPath = artifactPath,
            Base64Png = base64Png,
            PreviewPath = previewPath,
            AutoHdr = DetectAutoHdr(targetName),
            UserIdleSeconds = GetUserIdleSeconds(),
            CaptureMethodUsed = "vulkan_offscreen"
        };
    }

    public Task<WindowCaptureResult> CaptureTransientAsync(
        string target = "hon_x64",
        int dwellTimeMs = 350,
        int captureWidth = 1280,
        int captureHeight = 720,
        int offscreenX = -32000,
        int offscreenY = -32000,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var tcs = new TaskCompletionSource<WindowCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                using var desktopScope = EnsureDesktopAttachment();
                try
                {
#pragma warning disable RS0030 // Justification: Dedicated Win32 STA capture thread bridge
                    System.Runtime.CompilerServices.TaskAwaiter<WindowCaptureResult> awaiter = CaptureTransientCoreAsync(
                        target,
                        dwellTimeMs,
                        captureWidth,
                        captureHeight,
                        offscreenX,
                        offscreenY,
                        saveArtifact,
                        customOutputPath,
                        cancellationToken).GetAwaiter();
                    WindowCaptureResult result = awaiter.GetResult();
#pragma warning restore RS0030
                    tcs.SetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.SetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        return CaptureTransientCoreAsync(
            target,
            dwellTimeMs,
            captureWidth,
            captureHeight,
            offscreenX,
            offscreenY,
            saveArtifact,
            customOutputPath,
            cancellationToken);
    }

    private async Task<WindowCaptureResult> CaptureTransientCoreAsync(
        string target,
        int dwellTimeMs,
        int captureWidth,
        int captureHeight,
        int offscreenX,
        int offscreenY,
        bool saveArtifact,
        string? customOutputPath,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[WindowCapturer] Initiating transient capture for target: '{Target}', dwell: {Dwell}ms, size: {W}x{H}",
            target, dwellTimeMs, captureWidth, captureHeight);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = target,
                ErrorMessage = "Win32 transient window capture is only supported on Windows operating systems."
            };
        }

        IntPtr hwnd = ResolveWindowHandle(target, out string windowTitle, out int pid);
        if (hwnd == IntPtr.Zero)
        {
            if (pid > 0 && _vulkanGrabber.IsSharedMemoryAvailable(pid))
            {
                _logger.LogInformation("[WindowCapturer] Transient capture for target '{Target}' has zero HWND, fulfilling via Vulkan shared memory (0ms dwell, 0 window exposure).", target);
                try
                {
                    WindowCaptureResult vulkanRes = await CaptureVulkanOffscreenAsync(
                        pid, target, timeoutMs: 1500, saveArtifact, customOutputPath, cancellationToken).ConfigureAwait(false);
                    if (vulkanRes.Success)
                    {
                        return vulkanRes with { WindowTitle = windowTitle, Hwnd = "0x00000000" };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[WindowCapturer] Transient Tier 0 Vulkan capture on zero HWND failed: {Msg}", ex.Message);
                }
            }

            _logger.LogWarning("[WindowCapturer] Could not resolve any active window handle for target: '{Target}'", target);
            return new WindowCaptureResult
            {
                Success = false,
                Target = target,
                ErrorMessage = $"Target window not found for query '{target}'.",
                ErrorCode = WindowCaptureErrorCode.WindowNotFound
            };
        }

        if (pid > 0 && _vulkanGrabber.IsSharedMemoryAvailable(pid))
        {
            try
            {
                WindowCaptureResult vulkanRes = await CaptureVulkanOffscreenAsync(
                    pid, target, timeoutMs: 1500, saveArtifact, customOutputPath, cancellationToken).ConfigureAwait(false);
                if (vulkanRes.Success)
                {
                    _logger.LogInformation("[WindowCapturer] Transient capture for '{Target}' fulfilled via Tier 0 Vulkan shared memory (0ms dwell, 0 window exposure).", target);
                    return vulkanRes with { WindowTitle = windowTitle, Hwnd = $"0x{hwnd.ToInt64():X8}" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WindowCapturer] Transient Tier 0 Vulkan capture failed: {Msg}", ex.Message);
            }
        }

        return await CaptureHwndTransientAsync(
            hwnd,
            target,
            dwellTimeMs,
            captureWidth,
            captureHeight,
            offscreenX,
            offscreenY,
            saveArtifact,
            customOutputPath,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<WindowCaptureResult> CaptureHwndTransientAsync(
        IntPtr hwnd,
        string targetName = "explicit_hwnd",
        int dwellTimeMs = 350,
        int captureWidth = 1280,
        int captureHeight = 720,
        int offscreenX = -10000,
        int offscreenY = -10000,
        bool saveArtifact = true,
        string? customOutputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            var tcs = new TaskCompletionSource<WindowCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                using var desktopScope = EnsureDesktopAttachment();
                try
                {
#pragma warning disable RS0030 // Justification: Dedicated Win32 STA capture thread bridge
                    System.Runtime.CompilerServices.TaskAwaiter<WindowCaptureResult> awaiter = CaptureHwndTransientCoreAsync(
                        hwnd,
                        targetName,
                        dwellTimeMs,
                        captureWidth,
                        captureHeight,
                        offscreenX,
                        offscreenY,
                        saveArtifact,
                        customOutputPath,
                        cancellationToken).GetAwaiter();
                    WindowCaptureResult result = awaiter.GetResult();
#pragma warning restore RS0030
                    tcs.SetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.SetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        return CaptureHwndTransientCoreAsync(
            hwnd,
            targetName,
            dwellTimeMs,
            captureWidth,
            captureHeight,
            offscreenX,
            offscreenY,
            saveArtifact,
            customOutputPath,
            cancellationToken);
    }

    private async Task<WindowCaptureResult> CaptureHwndTransientCoreAsync(
        IntPtr hwnd,
        string targetName,
        int dwellTimeMs,
        int captureWidth,
        int captureHeight,
        int offscreenX,
        int offscreenY,
        bool saveArtifact,
        string? customOutputPath,
        CancellationToken cancellationToken)
    {
        using var desktopScope = EnsureDesktopAttachment();
        _logger.LogInformation("[WindowCapturer] Transient capture for HWND 0x{HWND:X8} (Target: '{Target}', Dwell: {Dwell}ms)",
            hwnd.ToInt64(), targetName, dwellTimeMs);

        if (!IsWindow(hwnd))
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = targetName,
                Hwnd = $"0x{hwnd.ToInt64():X8}",
                ErrorMessage = $"Invalid or destroyed window handle: 0x{hwnd.ToInt64():X8}",
                ErrorCode = WindowCaptureErrorCode.InvalidWindowHandle
            };
        }

        GetWindowThreadProcessId(hwnd, out uint pidUint);
        int pid = (int)pidUint;
        string windowTitle = GetWindowTitle(hwnd);
        double userIdleSec = GetUserIdleSeconds();

        if (pid > 0 && _vulkanGrabber.IsSharedMemoryAvailable(pid))
        {
            try
            {
                WindowCaptureResult vulkanRes = await CaptureVulkanOffscreenAsync(
                    pid, targetName, timeoutMs: 5000, saveArtifact, customOutputPath, cancellationToken).ConfigureAwait(false);
                if (vulkanRes.Success)
                {
                    _logger.LogInformation("[WindowCapturer] HWND 0x{HWND:X8} transient capture fulfilled via Tier 0 Vulkan shared memory (0ms dwell, 0 window exposure).", hwnd.ToInt64());
                    return vulkanRes with { WindowTitle = windowTitle, Hwnd = $"0x{hwnd.ToInt64():X8}" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WindowCapturer] HWND Transient Tier 0 Vulkan capture failed: {Msg}", ex.Message);
            }
        }

        byte[]? pixelBuffer = null;
        string methodUsed = "transient_desktop_dc";
        ClientProcessSupervisor.SuppressOffscreenWatchdog = true;
        OnTransientCaptureStateChanged?.Invoke(true);
        try
        {
            // 1. Temporarily show and position at (0, 0) topmost
            ShowWindow(hwnd, 1 /* SW_SHOWNORMAL */);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, captureWidth, captureHeight, SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
            UpdateWindow(hwnd);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0100 /* RDW_UPDATENOW */ | 0x0001 /* RDW_INVALIDATE */ | 0x0080 /* RDW_ALLCHILDREN */);

            // 2. Dwell for engine surface present (default ~350ms)
            await Task.Delay(Math.Clamp(dwellTimeMs, 50, 5000), cancellationToken).ConfigureAwait(false);

            // 3. Multi-tier capture: Desktop DC -> Window DC -> PrintWindow
            RECT bounds = new() { Left = 0, Top = 0, Right = captureWidth, Bottom = captureHeight };
            pixelBuffer = CaptureWithDesktopDc(bounds, captureWidth, captureHeight);
            if (pixelBuffer == null || !HasNonBlackEntropy(pixelBuffer, captureWidth, captureHeight))
            {
                var wDc = CaptureWithWindowDc(hwnd, captureWidth, captureHeight);
                if (wDc != null && (pixelBuffer == null || HasNonBlackEntropy(wDc, captureWidth, captureHeight)))
                {
                    pixelBuffer = wDc;
                    methodUsed = "transient_window_dc";
                }
            }
            if (pixelBuffer == null || !HasNonBlackEntropy(pixelBuffer, captureWidth, captureHeight))
            {
                var pw = CaptureWithPrintWindow(hwnd, captureWidth, captureHeight);
                if (pw != null && (pixelBuffer == null || HasNonBlackEntropy(pw, captureWidth, captureHeight)))
                {
                    pixelBuffer = pw;
                    methodUsed = "transient_print_window";
                }
            }
        }
        finally
        {
            // 4. GUARANTEED: Immediately restore window to offscreen without SW_HIDE so DXVK presentation continues
            try
            {
                SetWindowPos(hwnd, HWND_NOTOPMOST, offscreenX, offscreenY, captureWidth, captureHeight, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WindowCapturer] Failed restoring window 0x{HWND:X8} to offscreen: {Msg}", hwnd.ToInt64(), ex.Message);
            }
            finally
            {
                ClientProcessSupervisor.SuppressOffscreenWatchdog = false;
                OnTransientCaptureStateChanged?.Invoke(false);
            }
        }

        if (pixelBuffer == null || !HasNonBlackEntropy(pixelBuffer, captureWidth, captureHeight))
        {
            return new WindowCaptureResult
            {
                Success = false,
                Target = targetName,
                WindowTitle = windowTitle,
                Hwnd = $"0x{hwnd.ToInt64():X8}",
                Pid = pid,
                Width = captureWidth,
                Height = captureHeight,
                ErrorMessage = $"Failed to capture non-black pixel buffer during transient onscreen exposure for window 0x{hwnd.ToInt64():X8}.",
                ErrorCode = WindowCaptureErrorCode.EmptyOrBlackFrame
            };
        }

        // Encode to Bgra32 Image
        using Kongor.Cli.Mcp.Imaging.Image<Bgra32> image = Image.LoadPixelData<Bgra32>(pixelBuffer, captureWidth, captureHeight);

        string? artifactPath = null;
        string? previewPath = null;
        if (saveArtifact || !string.IsNullOrWhiteSpace(customOutputPath))
        {
            string artifactDir = Path.Combine(Protocol.McpServer.FindProjectRoot(), "artifacts", "captures");
            Directory.CreateDirectory(artifactDir);

            string sanitizedTarget = SanitizeFileName(string.IsNullOrWhiteSpace(targetName) ? "window" : targetName);
            artifactPath = !string.IsNullOrWhiteSpace(customOutputPath)
                ? customOutputPath
                : Path.Combine(artifactDir, $"transient_{sanitizedTarget}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmssfff}.png");

            var (pathAllowed, validatedPath, pathRejection) = ResolveArtifactPath(saveArtifact, artifactPath, timeProvider: _timeProvider);
            if (!pathAllowed)
            {
                _logger.LogWarning("[WindowCapturer] Rejected artifact path '{Path}': {Reason}", artifactPath, pathRejection);
                artifactPath = null;
            }
            else
            {
                artifactPath = validatedPath;
            }
            string? parentDir = !string.IsNullOrEmpty(artifactPath) ? Path.GetDirectoryName(artifactPath) : null;
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            if (!string.IsNullOrEmpty(artifactPath))
            {
                image.SaveAsPng(artifactPath);
                try { previewPath = GeneratePreview(artifactPath); } catch (Exception) { previewPath = null; }
            }
            _logger.LogInformation("[WindowCapturer] Saved transient PNG capture artifact to: '{Path}'", artifactPath);
        }

        using MemoryStream ms = new();
        image.SaveAsPng(ms);
        string base64Png = Convert.ToBase64String(ms.ToArray());

        _logger.LogInformation("[WindowCapturer] Transient capture succeeded for 0x{HWND:X8} ({W}x{H}, Base64 size: {Size} chars)",
            hwnd.ToInt64(), captureWidth, captureHeight, base64Png.Length);

        return new WindowCaptureResult
        {
            Success = true,
            Target = targetName,
            WindowTitle = windowTitle,
            Hwnd = $"0x{hwnd.ToInt64():X8}",
            Pid = pid,
            Width = captureWidth,
            Height = captureHeight,
            PreviewPath = previewPath,
            AutoHdr = DetectAutoHdr(targetName),
            UserIdleSeconds = userIdleSec,
            ArtifactPath = artifactPath,
            Base64Png = base64Png,
            CaptureMethodUsed = methodUsed
        };
    }

    #region Win32 Capture Modalities

    private byte[]? CaptureWithPrintWindow(IntPtr hwnd, int width, int height)
    {
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        if (hdcMem == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        BITMAPINFO bmi = CreateBitmapInfo(width, height);
        IntPtr hBitmap = CreateDIBSection(hdcMem, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
        {
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        IntPtr hOldBmp = SelectObject(hdcMem, hBitmap);
        try
        {
            // PW_RENDERFULLCONTENT = 2, PW_CLIENTONLY = 1
            bool success = PrintWindow(hwnd, hdcMem, 2);
            if (!success)
            {
                success = PrintWindow(hwnd, hdcMem, 0);
            }

            if (!success) return null;

            int byteCount = width * height * 4;
            byte[] buffer = new byte[byteCount];
            Marshal.Copy(pBits, buffer, 0, byteCount);
            return buffer;
        }
        finally
        {
            SelectObject(hdcMem, hOldBmp);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private byte[]? CaptureWithWindowDc(IntPtr hwnd, int width, int height)
    {
        IntPtr hdcWnd = GetDC(hwnd);
        if (hdcWnd == IntPtr.Zero) return null;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        if (hdcMem == IntPtr.Zero)
        {
            ReleaseDC(hwnd, hdcWnd);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        BITMAPINFO bmi = CreateBitmapInfo(width, height);
        IntPtr hBitmap = CreateDIBSection(hdcMem, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
        {
            DeleteDC(hdcMem);
            ReleaseDC(hwnd, hdcWnd);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        IntPtr hOldBmp = SelectObject(hdcMem, hBitmap);
        try
        {
            bool blitSuccess = BitBlt(hdcMem, 0, 0, width, height, hdcWnd, 0, 0, SRCCOPY);
            if (!blitSuccess) return null;

            int byteCount = width * height * 4;
            byte[] buffer = new byte[byteCount];
            Marshal.Copy(pBits, buffer, 0, byteCount);
            return buffer;
        }
        finally
        {
            SelectObject(hdcMem, hOldBmp);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(hwnd, hdcWnd);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private byte[]? CaptureWithDesktopDc(RECT bounds, int width, int height)
    {
        // STRICT INVARIANT: If the window is offscreen, Desktop DC is fundamentally invalid.
        // It must NEVER clamp offscreen coordinates to (0, 0) and capture the desktop screen!
        if (bounds.Left <= -5000 || bounds.Top <= -5000 || bounds.Right <= -5000 || bounds.Bottom <= -5000 ||
            bounds.Left >= 50000 || bounds.Top >= 50000)
        {
            _logger.LogWarning("[WindowCapturer] Window bounds ({L}, {T}, {R}, {B}) are offscreen. Desktop DC capture rejected to prevent desktop contamination.",
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            return null;
        }

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        if (hdcMem == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        BITMAPINFO bmi = CreateBitmapInfo(width, height);
        IntPtr hBitmap = CreateDIBSection(hdcMem, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
        {
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        IntPtr hOldBmp = SelectObject(hdcMem, hBitmap);
        try
        {
            int srcX = bounds.Left;
            int srcY = bounds.Top;
            bool blitSuccess = BitBlt(hdcMem, 0, 0, width, height, hdcScreen, srcX, srcY, SRCCOPY);
            if (!blitSuccess) return null;

            int byteCount = width * height * 4;
            byte[] buffer = new byte[byteCount];
            Marshal.Copy(pBits, buffer, 0, byteCount);
            return buffer;
        }
        finally
        {
            SelectObject(hdcMem, hOldBmp);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static BITMAPINFO CreateBitmapInfo(int width, int height)
    {
        BITMAPINFO bmi = new();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // Top-down DIB
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB
        return bmi;
    }

    private static bool HasNonBlackEntropy(byte[] buffer, int width = 0, int height = 0)
    {
        if (buffer.Length < 64) return false;
        int startIndex = (width > 0 && height > 60) ? (width * 50 * 4) : (buffer.Length / 8);
        int endIndex = buffer.Length - ((width > 0 && height > 60) ? (width * 20 * 4) : (buffer.Length / 8));
        if (startIndex >= endIndex) { startIndex = 0; endIndex = buffer.Length; }

        int nonZeroCount = 0;
        int step = Math.Max(4, (endIndex - startIndex) / 1000);

        for (int i = startIndex; i < endIndex; i += step)
        {
            // Check RGB channels (Bgra32 format: [B, G, R, A])
            byte b = buffer[i];
            byte g = (i + 1 < buffer.Length) ? buffer[i + 1] : (byte)0;
            byte r = (i + 2 < buffer.Length) ? buffer[i + 2] : (byte)0;
            if (b > 10 || g > 10 || r > 10)
            {
                nonZeroCount++;
                if (nonZeroCount > 25) return true;
            }
        }
        return false;
    }

    #endregion

    #region Window Resolution and Bounds Query

    public IntPtr ResolveWindowHandle(string target, out string windowTitle, out int pid)
    {
        windowTitle = string.Empty;
        pid = 0;

        string trimmed = target.Trim();

        // -1. Explicit PID or Client instance targeting (e.g. "pid:1234" or "client:1234")
        if (trimmed.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("client:", StringComparison.OrdinalIgnoreCase))
        {
            string pStr = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
            if (int.TryParse(pStr, out int targetPid) && targetPid > 0)
            {
                pid = targetPid;
                IntPtr byPid = FindWindowForProcessId(targetPid);
                if (byPid != IntPtr.Zero && IsWindow(byPid))
                {
                    windowTitle = GetWindowTitle(byPid);
                    return byPid;
                }
                return IntPtr.Zero;
            }
        }

        // 0. Primary check for HoN Game Render Window directly
        if (trimmed.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("game", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("client", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("game_client", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr directGameWnd = FindWindow("K2_Direct3D", null);
            if (directGameWnd == IntPtr.Zero) directGameWnd = FindWindow(null, "Heroes of Newerth x64");
            if (directGameWnd == IntPtr.Zero) directGameWnd = FindWindow(null, "Heroes of Newerth");
            if (directGameWnd != IntPtr.Zero && IsWindow(directGameWnd))
            {
                GetWindowThreadProcessId(directGameWnd, out uint gPid);
                pid = (int)gPid;
                windowTitle = GetWindowTitle(directGameWnd);
                return directGameWnd;
            }

            // Fallback: check if hon_x64 process exists even if window handle is offscreen or not found by FindWindow
            Process[] honProcs = Process.GetProcessesByName("hon_x64");
            try
            {
                if (honProcs.Length > 0)
                {
                    // Prioritize client process with active Vulkan shared memory or non-dedicated window
                    Process targetProc = honProcs.FirstOrDefault(p => _vulkanGrabber.IsSharedMemoryAvailable(p.Id))
                        ?? honProcs.FirstOrDefault(p => FindWindowForProcessId(p.Id) != IntPtr.Zero)
                        ?? honProcs[0];

                    pid = targetProc.Id;
                    IntPtr h = targetProc.MainWindowHandle != IntPtr.Zero && IsWindow(targetProc.MainWindowHandle)
                        ? targetProc.MainWindowHandle
                        : FindWindowForProcessId(pid);

                    if (h != IntPtr.Zero && IsWindow(h))
                    {
                        windowTitle = GetWindowTitle(h);
                        return h;
                    }
                    return IntPtr.Zero;
                }
            }
            finally
            {
                foreach (Process p in honProcs) p.Dispose();
            }
        }

        // 1. Check for "foreground"
        if (trimmed.Equals("foreground", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero && IsWindow(fgHwnd))
            {
                GetWindowThreadProcessId(fgHwnd, out uint fgPid);
                pid = (int)fgPid;
                windowTitle = GetWindowTitle(fgHwnd);
                return fgHwnd;
            }

            // Fall back to checking active hon_x64 or willowmaker processes
            string[] fallbackProcesses = ["hon_x64", "willowmaker", "WILLOWMAKER"];
            foreach (string procName in fallbackProcesses)
            {
                Process[] procMatches = Process.GetProcessesByName(procName);
                try
                {
                    foreach (Process p in procMatches)
                    {
                        try
                        {
                            IntPtr h = p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle)
                                ? p.MainWindowHandle
                                : FindWindowForProcessId(p.Id);

                            if (h != IntPtr.Zero && IsWindow(h))
                            {
                                pid = p.Id;
                                windowTitle = GetWindowTitle(h);
                                return h;
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    foreach (Process p in procMatches) p.Dispose();
                }
            }
        }

        // 2. Check for explicit HWND (Hex: '0x001B0E24' or Integer)
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
        {
            IntPtr candidate = new(hexVal);
            if (IsWindow(candidate))
            {
                GetWindowThreadProcessId(candidate, out uint hexPid);
                pid = (int)hexPid;
                windowTitle = GetWindowTitle(candidate);
                return candidate;
            }
        }

        if (long.TryParse(trimmed, out long intHwnd) && intHwnd > 1000)
        {
            if (int.TryParse(trimmed, out int numericPid))
            {
                IntPtr byPid = FindWindowForProcessId(numericPid);
                if (byPid != IntPtr.Zero)
                {
                    pid = numericPid;
                    windowTitle = GetWindowTitle(byPid);
                    return byPid;
                }
            }

            IntPtr candidate = new(intHwnd);
            if (IsWindow(candidate))
            {
                GetWindowThreadProcessId(candidate, out uint intPid);
                pid = (int)intPid;
                windowTitle = GetWindowTitle(candidate);
                return candidate;
            }
        }

        // 3. Check for Process Name matching (hon_x64, willowmaker, nexus.controlpanel.gui, etc.)
        string procNameQuery = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(trimmed)
            : trimmed;

        Process[] processes = Process.GetProcesses();
        IntPtr fallbackHwnd = IntPtr.Zero;
        string fallbackTitle = string.Empty;
        int fallbackPid = 0;

        foreach (Process p in processes)
        {
            try
            {
                if (p.ProcessName.Equals(procNameQuery, StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Contains(procNameQuery, StringComparison.OrdinalIgnoreCase))
                {
                    if (procNameQuery.Equals("hon_x64", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string? modulePath = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(modulePath) &&
                                (modulePath.Contains("server", StringComparison.OrdinalIgnoreCase) || modulePath.Contains("dedicated", StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }
                        }
                        catch { }
                    }

                    IntPtr h = IntPtr.Zero;
                    if (p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle))
                    {
                        RECT r = GetAccurateWindowBounds(p.MainWindowHandle);
                        if (r.Right - r.Left > 100 && r.Bottom - r.Top > 100)
                        {
                            h = p.MainWindowHandle;
                        }
                    }

                    if (h == IntPtr.Zero)
                    {
                        h = FindWindowForProcessId(p.Id);
                    }

                    if (h != IntPtr.Zero)
                    {
                        string title = GetWindowTitle(h);
                        // If it's a game client window (e.g. "Heroes of Newerth"), prioritize it
                        if (title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                            (!title.Contains("Unnamed_Server", StringComparison.OrdinalIgnoreCase) && !title.Contains("dedicated", StringComparison.OrdinalIgnoreCase)))
                        {
                            pid = p.Id;
                            windowTitle = title;
                            return h;
                        }

                        if (fallbackHwnd == IntPtr.Zero)
                        {
                            fallbackHwnd = h;
                            fallbackTitle = title;
                            fallbackPid = p.Id;
                        }
                    }
                    else if (fallbackPid == 0)
                    {
                        fallbackPid = p.Id;
                        try { fallbackTitle = p.MainWindowTitle; } catch { }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        if (fallbackHwnd != IntPtr.Zero)
        {
            pid = fallbackPid;
            windowTitle = fallbackTitle;
            return fallbackHwnd;
        }

        if (fallbackPid != 0)
        {
            pid = fallbackPid;
            windowTitle = fallbackTitle;
            return IntPtr.Zero;
        }

        // 4. Check for Window Title matching (e.g. "*Heroes of Newerth*", "*WILLOWMAKER*", "*NEXUS*")
        string cleanTitleQuery = trimmed.Trim('*');
        List<(IntPtr Hwnd, string Title, int Pid)> matches = [];

        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h))
            {
                string title = GetWindowTitle(h);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    string cls = GetWindowClassName(h);
                    if (cls.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                        cls.Equals("ExploreWClass", StringComparison.OrdinalIgnoreCase) ||
                        cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
                        cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (title.Contains(cleanTitleQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        GetWindowThreadProcessId(h, out uint pId);
                        matches.Add((h, title, (int)pId));
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        if (matches.Count > 0)
        {
            var best = matches[0];
            pid = best.Pid;
            windowTitle = best.Title;
            return best.Hwnd;
        }

        return IntPtr.Zero;
    }

    public static IntPtr FindWindowForProcessId(int processId)
    {
        try
        {
            IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
            if (hDesk == IntPtr.Zero) hDesk = OpenInputDesktop(0, false, 0x01FF);
            if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
        }
        catch { }

        IntPtr result = IntPtr.Zero;
        IntPtr candidateSized = IntPtr.Zero;
        IntPtr candidateFallback = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (IsWindow(h))
            {
                GetWindowThreadProcessId(h, out uint pId);
                if ((int)pId == processId)
                {
                    if (IsWindowCloaked(h))
                    {
                        return true; // #218: Skip DWMWA_CLOAKED windows on inactive virtual desktops
                    }
                    string cls = GetWindowClassName(h);
                    string title = GetWindowTitle(h);
                    if (title.Contains("dummy", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("fodder", StringComparison.OrdinalIgnoreCase) ||
                        cls.Contains("dummy", StringComparison.OrdinalIgnoreCase))
                    {
                        return true; // Skip dummy OpenGL/Direct3D context creation window
                    }
                    if (cls.StartsWith("K2_", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase))
                    {
                        result = h;
                        return false; // Found exact render window
                    }
                    if (GetWindowRect(h, out RECT r))
                    {
                        int w = r.Right - r.Left;
                        int hgt = r.Bottom - r.Top;
                        if (w > 100 && hgt > 100 && candidateSized == IntPtr.Zero)
                        {
                            candidateSized = h;
                        }
                    }
                    if (candidateFallback == IntPtr.Zero)
                    {
                        candidateFallback = h;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        if (result == IntPtr.Zero)
        {
            result = candidateSized != IntPtr.Zero ? candidateSized : candidateFallback;
        }

        // 2. Thread-level window enumeration fallback across all threads in process
        if (result == IntPtr.Zero && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using Process proc = Process.GetProcessById(processId);
                foreach (ProcessThread thread in proc.Threads)
                {
                    EnumThreadWindows((uint)thread.Id, (h, _) =>
                    {
                        if (IsWindow(h) && !IsWindowCloaked(h))
                        {
                            string cls = GetWindowClassName(h);
                            string title = GetWindowTitle(h);
                            if (title.Contains("dummy", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("fodder", StringComparison.OrdinalIgnoreCase) ||
                                cls.Contains("dummy", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                            if (cls.StartsWith("K2_", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase))
                            {
                                result = h;
                                return false; // Found exact render window
                            }
                            if (GetWindowRect(h, out RECT r))
                            {
                                int w = r.Right - r.Left;
                                int hgt = r.Bottom - r.Top;
                                if (w > 100 && hgt > 100 && candidateSized == IntPtr.Zero)
                                {
                                    candidateSized = h;
                                }
                            }
                            if (candidateFallback == IntPtr.Zero)
                            {
                                candidateFallback = h;
                            }
                        }
                        return true;
                    }, IntPtr.Zero);

                    if (result != IntPtr.Zero)
                    {
                        break;
                    }
                }

                if (result == IntPtr.Zero)
                {
                    result = candidateSized != IntPtr.Zero ? candidateSized : candidateFallback;
                }
            }
            catch { }
        }

        // 3. Desktop-level window enumeration fallback
        if (result == IntPtr.Zero && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                EnsureProcessWindowStation();
                IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk == IntPtr.Zero)
                {
                    hDesk = OpenInputDesktop(0, false, 0x01FF);
                }
                if (hDesk != IntPtr.Zero)
                {
                    try
                    {
                        EnumDesktopWindows(hDesk, (h, _) =>
                        {
                            if (IsWindow(h) && !IsWindowCloaked(h))
                            {
                                GetWindowThreadProcessId(h, out uint pId);
                                if ((int)pId == processId)
                                {
                                    string cls = GetWindowClassName(h);
                                    string title = GetWindowTitle(h);
                                    if (cls.StartsWith("K2_", StringComparison.OrdinalIgnoreCase) ||
                                        title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase))
                                    {
                                        result = h;
                                        return false;
                                    }
                                    if (GetWindowRect(h, out RECT r))
                                    {
                                        int w = r.Right - r.Left;
                                        int hgt = r.Bottom - r.Top;
                                        if (w > 100 && hgt > 100)
                                        {
                                            result = h;
                                            return false;
                                        }
                                    }
                                }
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                    finally
                    {
                        CloseDesktop(hDesk);
                    }
                }
            }
            catch { }
        }

        return result;
    }

    public static RECT GetAccurateWindowBounds(IntPtr hwnd)
    {
        // 1. Primary: DWM Extended Frame Bounds (removes invisible drop shadows on Win10/11)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>());
            if (hr == 0 && dwmRect.Right > dwmRect.Left && dwmRect.Bottom > dwmRect.Top)
            {
                return dwmRect;
            }
        }

        // 2. Fallback: GetWindowRect
        if (GetWindowRect(hwnd, out RECT winRect) && winRect.Right > winRect.Left && winRect.Bottom > winRect.Top)
        {
            return winRect;
        }

        // 3. Fallback: GetClientRect with ClientToScreen
        if (GetClientRect(hwnd, out RECT clientRect))
        {
            POINT pt = new() { X = clientRect.Left, Y = clientRect.Top };
            ClientToScreen(hwnd, ref pt);
            return new RECT
            {
                Left = pt.X,
                Top = pt.Y,
                Right = pt.X + (clientRect.Right - clientRect.Left),
                Bottom = pt.Y + (clientRect.Bottom - clientRect.Top)
            };
        }

        return new RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        StringBuilder sb = new(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        StringBuilder sb = new(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Pure path resolver and security guard for window capture artifacts.
    /// Enforces:
    /// - Always writes to disk if customOutputPath is provided (even if saveArtifact is false).
    /// - Resolves relative paths against the project repo root.
    /// - Enforces .png extension.
    /// - Blocks UNC network shares (\\).
    /// - Restricts destinations to repoRoot (excluding .git/, src/, native/), Path.GetTempPath(), or %USERPROFILE%\.gemini\antigravity\.
    /// </summary>
    public static (bool Allowed, string? FullPath, string? RejectionReason) ResolveArtifactPath(
        bool saveArtifact,
        string? customOutputPath,
        string? repoRoot = null,
        string? defaultDir = null,
        string? defaultFileName = null,
        TimeProvider? timeProvider = null)
    {
        string root = !string.IsNullOrWhiteSpace(repoRoot)
            ? repoRoot
            : Protocol.McpServer.FindProjectRoot();

        bool hasCustomPath = !string.IsNullOrWhiteSpace(customOutputPath);
        if (!saveArtifact && !hasCustomPath)
        {
            return (true, null, null);
        }

        string rawPath;
        if (hasCustomPath)
        {
            rawPath = customOutputPath!.Trim();
        }
        else
        {
            string baseDir = !string.IsNullOrWhiteSpace(defaultDir)
                ? defaultDir
                : Path.Combine(root, "artifacts", "captures");
            string fileName = !string.IsNullOrWhiteSpace(defaultFileName)
                ? defaultFileName
                : $"capture_{(timeProvider ?? TimeProvider.System).GetUtcNow():yyyyMMdd_HHmmssfff}.png";
            rawPath = Path.Combine(baseDir, fileName);
        }

        // 1. Block UNC network shares (e.g., \\server\share)
        if (rawPath.StartsWith(@"\\", StringComparison.Ordinal) || rawPath.StartsWith("//", StringComparison.Ordinal))
        {
            return (false, null, "UNC network paths are rejected for security.");
        }

        // 2. Resolve to full normalized absolute path
        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(root, rawPath));
        }
        catch (Exception ex)
        {
            return (false, null, $"Invalid path format: {ex.Message}");
        }

        // 3. Enforce .png extension
        if (!fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "Artifact file must have a .png extension.");
        }

        // 4. Validate destination directory allowlist:
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string normalizedGemini = !string.IsNullOrWhiteSpace(userProfile)
            ? Path.GetFullPath(Path.Combine(userProfile, ".gemini", "antigravity")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : string.Empty;

        bool isUnderRoot = fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        bool isUnderTemp = fullPath.StartsWith(normalizedTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(fullPath, normalizedTemp, StringComparison.OrdinalIgnoreCase);
        bool isUnderGemini = !string.IsNullOrEmpty(normalizedGemini) &&
                             (fullPath.StartsWith(normalizedGemini + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(fullPath, normalizedGemini, StringComparison.OrdinalIgnoreCase));

        if (!isUnderRoot && !isUnderTemp && !isUnderGemini)
        {
            return (false, null, $"Path '{fullPath}' is outside the permitted directories (repo root, %TEMP%, or %USERPROFILE%\\.gemini\\antigravity).");
        }

        if (isUnderRoot)
        {
            string rel = Path.GetRelativePath(normalizedRoot, fullPath);
            string[] parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string first = parts[0].ToLowerInvariant();
                if (first is ".git" or "src" or "native")
                {
                    return (false, null, $"Writing artifacts directly into protected codebase directory '{first}/' is rejected.");
                }
            }
            if (fullPath.Contains(".s2z", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, "Writing artifacts into .s2z archive paths is rejected.");
            }
        }

        return (true, fullPath, null);
    }

    private static string SanitizeFileName(string input)
    {
        string invalid = new(Path.GetInvalidFileNameChars());
        string escaped = Regex.Escape(invalid);
        return Regex.Replace(input, $"[{escaped}]", "_");
    }

    // ========================================================================
    // #215 (Gem 3C): 2-Tier D3D9Ex Struct Byte Flip & Conditional-Branch Opcode
    // Shared-Texture Escalation (obs-studio/plugins/win-capture/graphics-hook/d3d9-capture.cpp)
    // ========================================================================
    public enum D3D9SharedTextureEscalationTier
    {
        None = 0,
        ByteFlipD3D9Ex = 1,
        OpcodeBranchPatch = 2
    }

    public static bool TryEscalateD3D9ExSharedTexture(
        Span<byte> deviceStructBytes,
        int isD3D9ExOffset,
        Span<byte> branchOpcodeBytes,
        Func<ReadOnlySpan<byte>, ReadOnlySpan<byte>, bool>? createSharedTextureCallback,
        out D3D9SharedTextureEscalationTier tierUsed)
    {
        tierUsed = D3D9SharedTextureEscalationTier.None;

        // Tier 1: d3d9_clsoff is_d3d9ex struct byte flip with RAII restore
        if (isD3D9ExOffset >= 0 && isD3D9ExOffset < deviceStructBytes.Length)
        {
            byte savedByte = deviceStructBytes[isD3D9ExOffset];
            try
            {
                deviceStructBytes[isD3D9ExOffset] = 1;
                bool tier1Created = createSharedTextureCallback == null ||
                                    createSharedTextureCallback(deviceStructBytes, branchOpcodeBytes);
                if (tier1Created)
                {
                    tierUsed = D3D9SharedTextureEscalationTier.ByteFlipD3D9Ex;
                    return true;
                }
            }
            finally
            {
                deviceStructBytes[isD3D9ExOffset] = savedByte;
            }
        }

        // Tier 2: d3d9-patches conditional-branch opcode patch fallback enabling D3DPOOL_DEFAULT shared-handle creation
        if (branchOpcodeBytes.Length >= 2 && branchOpcodeBytes.Length <= 8)
        {
            byte[] savedOpcodes = branchOpcodeBytes.ToArray();
            try
            {
                if (savedOpcodes[0] == 0x74 || savedOpcodes[0] == 0x75)
                {
                    branchOpcodeBytes[0] = 0xEB; // Short unconditional jump
                }
                else
                {
                    for (int i = 0; i < branchOpcodeBytes.Length; i++)
                    {
                        branchOpcodeBytes[i] = 0x90; // NOP slide
                    }
                }

                bool tier2Created = createSharedTextureCallback == null ||
                                    createSharedTextureCallback(deviceStructBytes, branchOpcodeBytes);
                if (tier2Created)
                {
                    tierUsed = D3D9SharedTextureEscalationTier.OpcodeBranchPatch;
                    return true;
                }
            }
            finally
            {
                savedOpcodes.AsSpan().CopyTo(branchOpcodeBytes);
            }
        }

        return false;
    }

    // ========================================================================
    // #217 (Gem 3E): WGC Desktop-Bridge & Extended-Frame-Bounds Border-Free
    // Client Sub-Box Calculator (obs-studio/libobs-winrt/winrt-capture.cpp:251-394)
    // ========================================================================
    public readonly record struct WgcClientSubBox(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int Width,
        int Height,
        bool ClampedToTexture);

    public static WgcClientSubBox ComputeBorderFreeWgcClientSubBox(
        RECT extendedFrameBounds,
        RECT clientRect,
        POINT clientTopLeftScreen,
        int wgcTextureWidth,
        int wgcTextureHeight)
    {
        int rawClientWidth = Math.Max(0, clientRect.Right - clientRect.Left);
        int rawClientHeight = Math.Max(0, clientRect.Bottom - clientRect.Top);
        int rawLeft = clientTopLeftScreen.X - extendedFrameBounds.Left;
        int rawTop = clientTopLeftScreen.Y - extendedFrameBounds.Top;
        int rawRight = rawLeft + rawClientWidth;
        int rawBottom = rawTop + rawClientHeight;

        int maxW = Math.Max(0, wgcTextureWidth);
        int maxH = Math.Max(0, wgcTextureHeight);

        int clampedLeft = Math.Clamp(rawLeft, 0, maxW);
        int clampedTop = Math.Clamp(rawTop, 0, maxH);
        int clampedRight = Math.Clamp(rawRight, clampedLeft, maxW);
        int clampedBottom = Math.Clamp(rawBottom, clampedTop, maxH);

        bool wasClamped = clampedLeft != rawLeft ||
                          clampedTop != rawTop ||
                          clampedRight != rawRight ||
                          clampedBottom != rawBottom;

        return new WgcClientSubBox(
            Left: clampedLeft,
            Top: clampedTop,
            Right: clampedRight,
            Bottom: clampedBottom,
            Width: clampedRight - clampedLeft,
            Height: clampedBottom - clampedTop,
            ClampedToTexture: wasClamped);
    }

    public static bool TryComputeBorderFreeWgcClientSubBoxForWindow(
        IntPtr hwnd,
        int wgcTextureWidth,
        int wgcTextureHeight,
        out WgcClientSubBox subBox)
    {
        subBox = default;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || hwnd == IntPtr.Zero)
        {
            return false;
        }

        RECT extBounds = GetAccurateWindowBounds(hwnd);
        if (!GetClientRect(hwnd, out RECT clientRect))
        {
            return false;
        }

        POINT clientTopLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
        if (!ClientToScreen(hwnd, ref clientTopLeft))
        {
            return false;
        }

        subBox = ComputeBorderFreeWgcClientSubBox(extBounds, clientRect, clientTopLeft, wgcTextureWidth, wgcTextureHeight);
        return subBox.Width > 0 && subBox.Height > 0;
    }

    // ========================================================================
    // #218 (Gem 3G): Penalty-Scored Window Rating Heuristic & DWMWA_CLOAKED Filter
    // Inspired by obs-studio/plugins/win-capture/window-helpers.c:1-460
    // ========================================================================
    public const int DWMWA_CLOAKED = 14;

    private static readonly string[] s_blockedWindowClasses =
    [
        "ConsoleWindowClass",
        "PseudoConsoleWindow",
        "IME",
        "MSCTFIME UI",
        "DummyDWMListenerWindow",
        "Progman",
        "WorkerW",
        "Shell_TrayWnd"
    ];

    public static bool IsWindowCloaked(IntPtr hwnd)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || hwnd == IntPtr.Zero)
        {
            return false;
        }

        int hr = DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int cloakedState, sizeof(int));
        return hr == 0 && cloakedState != 0;
    }

    public static int ComputeObsWindowRating(
        string? className,
        string? windowTitle,
        string? processExeName,
        int width,
        int height,
        bool isVisible,
        bool isCloaked,
        bool isToolWindow)
    {
        if (isCloaked || isToolWindow || width <= 0 || height <= 0)
        {
            return int.MinValue;
        }

        string cls = className?.Trim() ?? string.Empty;
        string title = windowTitle?.Trim() ?? string.Empty;
        string exe = processExeName?.Trim() ?? string.Empty;

        for (int i = 0; i < s_blockedWindowClasses.Length; i++)
        {
            if (cls.Equals(s_blockedWindowClasses[i], StringComparison.OrdinalIgnoreCase))
            {
                return int.MinValue;
            }
        }

        if (cls.Contains("dummy", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("dummy", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("fodder", StringComparison.OrdinalIgnoreCase))
        {
            return int.MinValue;
        }

        int score = 0;
        if (isVisible)
        {
            score += 50;
        }

        // Boost exact HoN / K2 engine render window matches
        if (exe.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
            exe.Equals("hon_x64.exe", StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        if (cls.Equals("K2_Direct3D", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("K2_WindowClass", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("K2_", StringComparison.OrdinalIgnoreCase))
        {
            score += 600;
        }

        if (title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }

        // Penalize generic helper / wrapper classes
        if (cls.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            score -= 200;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            score -= 100;
        }

        if (width < 100 || height < 100)
        {
            score -= 400;
        }
        else if (width >= 640 && height >= 480)
        {
            score += 100;
        }

        return score;
    }

    #endregion

    #region Win32 P/Invoke Declarations

    private const uint SRCCOPY = 0x00CC0020;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBmp, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    #endregion

    public static double GetUserIdleSeconds()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return 0.0;
        }

        try
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(lii);
            if (GetLastInputInfo(ref lii) && lii.dwTime > 0)
            {
                uint nowTicks = GetTickCount();
                if (nowTicks >= lii.dwTime)
                {
                    return (nowTicks - lii.dwTime) / 1000.0;
                }
            }
        }
        catch
        {
            // Fall through
        }

        return 0.0;
    }

    public static AutoHdrStatus DetectAutoHdr(string targetProcessName = "hon_x64", string? simulatedRegistry = null)
    {
        var candidates = new List<(string Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(simulatedRegistry))
        {
            candidates.Add(("SIMULATED_REGISTRY", simulatedRegistry));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
                if (key != null)
                {
                    foreach (string valName in key.GetValueNames())
                    {
                        if (string.Equals(valName, "DirectXUserGlobalSettings", StringComparison.OrdinalIgnoreCase) ||
                            valName.Contains(targetProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            object? rawObj = key.GetValue(valName);
                            if (rawObj != null)
                            {
                                candidates.Add((valName, rawObj.ToString() ?? string.Empty));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Registry read failure, fall back to NOT_SET
            }
        }

        var regex = new System.Text.RegularExpressions.Regex(@"AutoHDREnable\s*=\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var (candKey, candVal) in candidates)
        {
            var match = regex.Match(candVal);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int rawVal))
            {
                bool isOdd = (rawVal % 2) == 1;
                string state = isOdd ? $"ENABLED_ODD_{rawVal}" : $"DISABLED_EVEN_{rawVal}";
                return new AutoHdrStatus
                {
                    AutoHdrActive = isOdd,
                    RawValue = rawVal,
                    State = state,
                    SourceKey = candKey
                };
            }
        }

        return new AutoHdrStatus
        {
            AutoHdrActive = false,
            RawValue = 0,
            State = "NOT_SET",
            SourceKey = "NONE"
        };
    }

    public static string? GeneratePreview(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
        {
            return null;
        }

        try
        {
            string dir = Path.GetDirectoryName(artifactPath) ?? string.Empty;
            string fname = Path.GetFileNameWithoutExtension(artifactPath);
            string previewPath = Path.Combine(dir, $"{fname}_preview.png");

            using Image image = Image.Load(artifactPath);
            int previewW = Math.Max(1, image.Width / 3);
            int previewH = Math.Max(1, image.Height / 3);
            using Image preview = image.Clone(x => x.Resize(previewW, previewH));
            preview.SaveAsPng(previewPath);
            return previewPath;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// #219 (Gem 3L): Hardware Cursor Icon Mask Cache &amp; Offscreen Alpha-Blender.
/// Inspired by <c>obs-studio/plugins/win-capture/cursor-capture.c:1-380</c>.
/// Decodes 32-bit BGRA color DIB or monochrome 1-bpp AND/XOR masks (including AND=1, XOR=1
/// inverted pixel highlight handling), caches decoded cursor bitmaps by HICON in an MRU cache,
/// and alpha-blends onto offscreen BGRA captures at (cursorPos - clientOrigin - hotspot).
/// </summary>
public sealed class HardwareCursorIconMaskCacheBlender
{
    public sealed record CachedCursorBitmap(
        nint HIcon,
        int Width,
        int Height,
        int HotspotX,
        int HotspotY,
        byte[] BgraPixels,
        byte[] InvertMask);

    private readonly int _maxCacheEntries;
    private readonly Dictionary<nint, CachedCursorBitmap> _cache = new();
    private readonly LinkedList<nint> _mruOrder = new();
    private long _cacheHits;
    private long _cacheMisses;

    public HardwareCursorIconMaskCacheBlender(int maxCacheEntries = 32)
    {
        _maxCacheEntries = Math.Max(1, maxCacheEntries);
    }

    public int CachedCount => _cache.Count;
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    /// <summary>
    /// Decodes a 32-bit BGRA color cursor DIB (with optional 1-bpp AND mask fallback when all alpha bytes are zero).
    /// </summary>
    public static CachedCursorBitmap DecodeColorBgraCursorDib(
        nint hIcon,
        int width,
        int height,
        int hotspotX,
        int hotspotY,
        ReadOnlySpan<byte> bgraDibPixels,
        ReadOnlySpan<byte> optionalAndMaskBits = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int pixelCount = width * height;
        byte[] bgra = new byte[pixelCount * 4];
        byte[] invertMask = new byte[pixelCount];

        int copyBytes = Math.Min(bgra.Length, bgraDibPixels.Length);
        bgraDibPixels.Slice(0, copyBytes).CopyTo(bgra);

        bool hasAnyAlpha = false;
        for (int i = 0; i < pixelCount; i++)
        {
            if (bgra[(i * 4) + 3] != 0)
            {
                hasAnyAlpha = true;
                break;
            }
        }

        if (!hasAnyAlpha && !optionalAndMaskBits.IsEmpty)
        {
            int rowBytes = (width + 7) / 8;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int byteIdx = (y * rowBytes) + (x / 8);
                    int bitMask = 0x80 >> (x % 8);
                    bool andBit = byteIdx < optionalAndMaskBits.Length && (optionalAndMaskBits[byteIdx] & bitMask) != 0;
                    int pixelIdx = (y * width) + x;
                    int bgraIdx = pixelIdx * 4;
                    bool hasRgb = bgra[bgraIdx + 0] != 0 || bgra[bgraIdx + 1] != 0 || bgra[bgraIdx + 2] != 0;
                    if (!andBit)
                    {
                        bgra[bgraIdx + 3] = 255;
                    }
                    else if (hasRgb)
                    {
                        invertMask[pixelIdx] = 1;
                    }
                }
            }
        }

        return new CachedCursorBitmap(hIcon, width, height, hotspotX, hotspotY, bgra, invertMask);
    }

    /// <summary>
    /// Decodes a 1-bpp monochrome cursor AND mask + XOR mask pair into 32-bit BGRA pixels and a per-pixel
    /// inversion mask (for the Windows cursor highlight case where AND=1 and XOR=1).
    /// </summary>
    public static CachedCursorBitmap DecodeMonochromeAndXorCursorMasks(
        nint hIcon,
        int width,
        int height,
        int hotspotX,
        int hotspotY,
        ReadOnlySpan<byte> andMaskBits,
        ReadOnlySpan<byte> xorMaskBits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int pixelCount = width * height;
        byte[] bgra = new byte[pixelCount * 4];
        byte[] invertMask = new byte[pixelCount];
        int rowBytes = (width + 7) / 8;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int byteIdx = (y * rowBytes) + (x / 8);
                int bitMask = 0x80 >> (x % 8);
                bool andBit = byteIdx < andMaskBits.Length && (andMaskBits[byteIdx] & bitMask) != 0;
                bool xorBit = byteIdx < xorMaskBits.Length && (xorMaskBits[byteIdx] & bitMask) != 0;

                int pixelIdx = (y * width) + x;
                int bgraIdx = pixelIdx * 4;

                if (!andBit && !xorBit)
                {
                    // AND=0, XOR=0 -> Opaque black (0xFF000000)
                    bgra[bgraIdx + 0] = 0;
                    bgra[bgraIdx + 1] = 0;
                    bgra[bgraIdx + 2] = 0;
                    bgra[bgraIdx + 3] = 255;
                }
                else if (!andBit && xorBit)
                {
                    // AND=0, XOR=1 -> Opaque white (0xFFFFFFFF)
                    bgra[bgraIdx + 0] = 255;
                    bgra[bgraIdx + 1] = 255;
                    bgra[bgraIdx + 2] = 255;
                    bgra[bgraIdx + 3] = 255;
                }
                else if (andBit && !xorBit)
                {
                    // AND=1, XOR=0 -> Transparent (0x00000000)
                    bgra[bgraIdx + 0] = 0;
                    bgra[bgraIdx + 1] = 0;
                    bgra[bgraIdx + 2] = 0;
                    bgra[bgraIdx + 3] = 0;
                }
                else
                {
                    // AND=1, XOR=1 -> Inverted background pixel highlight (I-beam / crosshair)
                    bgra[bgraIdx + 0] = 255;
                    bgra[bgraIdx + 1] = 255;
                    bgra[bgraIdx + 2] = 255;
                    bgra[bgraIdx + 3] = 0;
                    invertMask[pixelIdx] = 1;
                }
            }
        }

        return new CachedCursorBitmap(hIcon, width, height, hotspotX, hotspotY, bgra, invertMask);
    }

    public CachedCursorBitmap GetOrAddCursorBitmap(
        nint hIcon,
        Func<nint, CachedCursorBitmap> decodeFactory)
    {
        if (_cache.TryGetValue(hIcon, out CachedCursorBitmap? existing))
        {
            Interlocked.Increment(ref _cacheHits);
            _mruOrder.Remove(hIcon);
            _mruOrder.AddFirst(hIcon);
            return existing;
        }

        Interlocked.Increment(ref _cacheMisses);
        CachedCursorBitmap decoded = decodeFactory(hIcon);
        if (_cache.Count >= _maxCacheEntries && _mruOrder.Last != null)
        {
            nint evictedKey = _mruOrder.Last.Value;
            _mruOrder.RemoveLast();
            _cache.Remove(evictedKey);
        }

        _cache[hIcon] = decoded;
        _mruOrder.AddFirst(hIcon);
        return decoded;
    }

    /// <summary>
    /// Alpha-blends (and applies monochrome AND=1, XOR=1 inversion where flagged) a cached cursor
    /// onto an offscreen BGRA frame buffer at (cursorScreenX - clientScreenOriginX - hotspotX, cursorScreenY - clientScreenOriginY - hotspotY).
    /// </summary>
    public static int BlendCursorOntoBgraFrame(
        Span<byte> frameBgra,
        int frameWidth,
        int frameHeight,
        CachedCursorBitmap cursor,
        int cursorScreenX,
        int cursorScreenY,
        int clientScreenOriginX,
        int clientScreenOriginY)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || frameBgra.Length < frameWidth * frameHeight * 4)
        {
            return 0;
        }

        int drawOriginX = cursorScreenX - clientScreenOriginX - cursor.HotspotX;
        int drawOriginY = cursorScreenY - clientScreenOriginY - cursor.HotspotY;
        int blendedPixels = 0;

        for (int cy = 0; cy < cursor.Height; cy++)
        {
            int dstY = drawOriginY + cy;
            if (dstY < 0 || dstY >= frameHeight)
            {
                continue;
            }

            for (int cx = 0; cx < cursor.Width; cx++)
            {
                int dstX = drawOriginX + cx;
                if (dstX < 0 || dstX >= frameWidth)
                {
                    continue;
                }

                int cursorPixelIdx = (cy * cursor.Width) + cx;
                int cursorBgraIdx = cursorPixelIdx * 4;
                int dstBgraIdx = ((dstY * frameWidth) + dstX) * 4;

                if (cursorPixelIdx < cursor.InvertMask.Length && cursor.InvertMask[cursorPixelIdx] != 0)
                {
                    // AND=1, XOR=1 monochrome cursor inversion
                    frameBgra[dstBgraIdx + 0] = (byte)(255 - frameBgra[dstBgraIdx + 0]);
                    frameBgra[dstBgraIdx + 1] = (byte)(255 - frameBgra[dstBgraIdx + 1]);
                    frameBgra[dstBgraIdx + 2] = (byte)(255 - frameBgra[dstBgraIdx + 2]);
                    frameBgra[dstBgraIdx + 3] = 255;
                    blendedPixels++;
                    continue;
                }

                byte srcA = cursor.BgraPixels[cursorBgraIdx + 3];
                if (srcA == 0)
                {
                    continue;
                }

                byte srcB = cursor.BgraPixels[cursorBgraIdx + 0];
                byte srcG = cursor.BgraPixels[cursorBgraIdx + 1];
                byte srcR = cursor.BgraPixels[cursorBgraIdx + 2];

                if (srcA == 255)
                {
                    frameBgra[dstBgraIdx + 0] = srcB;
                    frameBgra[dstBgraIdx + 1] = srcG;
                    frameBgra[dstBgraIdx + 2] = srcR;
                    frameBgra[dstBgraIdx + 3] = 255;
                }
                else
                {
                    int invA = 255 - srcA;
                    frameBgra[dstBgraIdx + 0] = (byte)(((srcB * srcA) + (frameBgra[dstBgraIdx + 0] * invA)) / 255);
                    frameBgra[dstBgraIdx + 1] = (byte)(((srcG * srcA) + (frameBgra[dstBgraIdx + 1] * invA)) / 255);
                    frameBgra[dstBgraIdx + 2] = (byte)(((srcR * srcA) + (frameBgra[dstBgraIdx + 2] * invA)) / 255);
                    frameBgra[dstBgraIdx + 3] = 255;
                }

                blendedPixels++;
            }
        }

        return blendedPixels;
    }
}
