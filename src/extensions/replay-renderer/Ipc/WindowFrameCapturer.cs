// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WindowFrameCapturer.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.Ipc;

internal readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

internal interface IWindowCaptureSurface : IDisposable
{
    bool TryCapturePrintWindow(IntPtr hwnd, uint flags, int windowWidth, int windowHeight);
    bool TryCaptureWindowDc(IntPtr hwnd, int windowWidth, int windowHeight);
    bool TryCaptureDesktopScreen(WindowBounds bounds, int windowWidth, int windowHeight);
    void CopyPixelsTo(byte[] destination);
}

internal interface IWin32WindowCaptureBackend
{
    IntPtr FindMainWindow(int processId, ILogger logger);
    bool TryGetWindowDimensions(IntPtr hwnd, out int width, out int height);
    bool IsWindowValid(IntPtr hwnd);
    bool TryGetWindowBounds(IntPtr hwnd, out WindowBounds bounds);
    void ActivateForegroundWindow(IntPtr hwnd, ILogger logger);
    IWindowCaptureSurface CreateCaptureSurface(int targetWidth, int targetHeight);
}

/// <summary>
///     High-throughput Win32 DIBSection Window Frame Capturer that continuously grabs raw 32-bit BGRA
///     frames from the active HoN client window at fixed framerate intervals.
///     Enforces zero-window-exposure by default (<see cref="AllowForegroundActivation" /> = <c>false</c>),
///     capturing via <c>PrintWindow(PW_RENDERFULLCONTENT)</c> without restoring, surfacing, or focusing the window.
/// </summary>
public sealed class WindowFrameCapturer : IAsyncDisposable
{
    internal const uint PwClientOnly = 0x00000001;
    internal const uint PwRenderFullContent = 0x00000002;

    private readonly IWin32WindowCaptureBackend _backend;
    private readonly ILogger<WindowFrameCapturer> _logger;
    private readonly int _processId;
    private readonly int _targetFps;
    private readonly uint _targetHeight;
    private readonly uint _targetWidth;
    private Task? _captureTask;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private IntPtr _targetHwnd = IntPtr.Zero;

    public WindowFrameCapturer(
        ILogger<WindowFrameCapturer> logger,
        int processId,
        uint targetWidth,
        uint targetHeight,
        int targetFps,
        bool allowForegroundActivation = false)
        : this(logger, processId, targetWidth, targetHeight, targetFps, allowForegroundActivation, Win32WindowCaptureBackend.Instance)
    {
    }

    internal WindowFrameCapturer(
        ILogger<WindowFrameCapturer> logger,
        int processId,
        uint targetWidth,
        uint targetHeight,
        int targetFps,
        bool allowForegroundActivation,
        IWin32WindowCaptureBackend backend)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _processId = processId;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _targetFps = targetFps > 0 ? targetFps : 60;
        AllowForegroundActivation = allowForegroundActivation;
    }

    /// <summary>
    ///     When <c>false</c> (default), enforces strict zero-window-exposure: never invokes
    ///     <c>ShowWindow(SW_RESTORE)</c>, <c>SetWindowPos(HWND_TOPMOST)</c>, <c>SetForegroundWindow</c>,
    ///     <c>BringWindowToTop</c>, or desktop screen-scraping fallback.
    /// </summary>
    public bool AllowForegroundActivation { get; }

    public bool ForegroundActivationAttempted { get; private set; }
    public bool ZeroWindowExposureEnforced => !AllowForegroundActivation && !ForegroundActivationAttempted;
    public long TotalFramesCaptured { get; private set; }
    public long PrintWindowSuccessCount { get; private set; }
    public long WindowDcFallbackCount { get; private set; }
    public long DesktopFallbackCount { get; private set; }
    public bool IsActive => _captureTask != null && !_captureTask.IsCompleted;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        if (_captureTask != null)
        {
            try
            {
                await _captureTask;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("[WindowCapturer] Capture task stopped after cancellation during disposal.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WindowCapturer] Capture task faulted during disposal: {Message}", ex.Message);
            }
        }

        _cts?.Dispose();
    }

    public Task StartAsync(Func<ReadOnlyMemory<byte>, Task> onFrameCaptured, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFrameCaptured);

        _logger.LogInformation(
            "[WindowCapturer] Starting Window Frame Capturer for PID {PID} ({Width}x{Height} @ {FPS} fps, AllowForegroundActivation={AllowFg}, ZeroWindowExposure={ZeroExposure})...",
            _processId, _targetWidth, _targetHeight, _targetFps, AllowForegroundActivation, ZeroWindowExposureEnforced);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureTask = Task.Run(() => CaptureLoopAsync(onFrameCaptured, _cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task CaptureLoopAsync(Func<ReadOnlyMemory<byte>, Task> onFrameCaptured,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[WindowCapturer] Searching for HoN engine main window handle (PID: {PID})...",
            _processId);

        int scanCount = 0;
        while (!cancellationToken.IsCancellationRequested && _targetHwnd == IntPtr.Zero)
        {
            scanCount++;
            IntPtr candidate = _backend.FindMainWindow(_processId, _logger);
            if (candidate != IntPtr.Zero)
            {
                _backend.TryGetWindowDimensions(candidate, out int w, out int h);

                _logger.LogInformation("[WindowCapturer] Scan #{Scan}: Candidate HWND: 0x{HWND:X8}, Size: {W}x{H}",
                    scanCount, candidate.ToInt64(), w, h);
                if (w >= 320 && h >= 240)
                {
                    _targetHwnd = candidate;
                    _logger.LogInformation(
                        "[WindowCapturer] Hooked active HoN game window (HWND: 0x{HWND:X8}, Rect: {W}x{H}).",
                        _targetHwnd.ToInt64(), w, h);
                    break;
                }
            }
            else if (scanCount % 4 == 0)
            {
                _logger.LogInformation("[WindowCapturer] Scan #{Scan}: No window candidate found for PID {PID} yet...",
                    scanCount, _processId);
            }

            await Task.Delay(250, cancellationToken);
        }

        if (_targetHwnd == IntPtr.Zero)
        {
            _logger.LogWarning("[WindowCapturer] Window handle not found for PID {PID}.", _processId);
            return;
        }

        if (AllowForegroundActivation)
        {
            ForegroundActivationAttempted = true;
            _logger.LogWarning(
                "[WindowCapturer] AllowForegroundActivation=true explicitly enabled; activating HWND 0x{HWND:X8} (ShowWindow/HWND_TOPMOST/SetForegroundWindow).",
                _targetHwnd.ToInt64());
            _backend.ActivateForegroundWindow(_targetHwnd, _logger);
        }
        else
        {
            _logger.LogInformation(
                "[WindowCapturer] Zero-window-exposure enforced (AllowForegroundActivation=false): skipping ShowWindow/HWND_TOPMOST/SetForegroundWindow and capturing HWND 0x{HWND:X8} via PrintWindow(PW_RENDERFULLCONTENT).",
                _targetHwnd.ToInt64());
        }

        int width = (int)_targetWidth;
        int height = (int)_targetHeight;
        int frameSizeBytes = width * height * 4;
        byte[] frameBuffer = new byte[frameSizeBytes];

        long frameIntervalTicks = Stopwatch.Frequency / _targetFps;
        long nextFrameTick = Stopwatch.GetTimestamp();

        using IWindowCaptureSurface surface = _backend.CreateCaptureSurface(width, height);

        try
        {
            _logger.LogInformation(
                "[WindowCapturer] Continuous {FPS} FPS frame capture loop active (ZeroWindowExposure={ZeroExposure}).",
                _targetFps, ZeroWindowExposureEnforced);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_backend.IsWindowValid(_targetHwnd))
                {
                    _logger.LogInformation("[WindowCapturer] Target window closed. Ending capture loop.");
                    break;
                }

                if (_backend.TryGetWindowBounds(_targetHwnd, out WindowBounds winRect) &&
                    winRect.Width > 0 && winRect.Height > 0)
                {
                    bool captured = CaptureSingleFrame(surface, _targetHwnd, winRect);
                    if (!captured && TotalFramesCaptured == 0)
                    {
                        _logger.LogWarning(
                            "[WindowCapturer] Frame capture returned false for HWND 0x{HWND:X8} (Bounds={L},{T} {W}x{H}, AllowForegroundActivation={AllowFg}).",
                            _targetHwnd.ToInt64(), winRect.Left, winRect.Top, winRect.Width, winRect.Height,
                            AllowForegroundActivation);
                    }

                    surface.CopyPixelsTo(frameBuffer);

                    TotalFramesCaptured++;
                    await onFrameCaptured(new ReadOnlyMemory<byte>(frameBuffer));
                }

                nextFrameTick += frameIntervalTicks;
                long currentTick = Stopwatch.GetTimestamp();
                long waitTicks = nextFrameTick - currentTick;

                if (waitTicks > 0)
                {
                    int waitMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
                    if (waitMs > 0) await Task.Delay(waitMs, cancellationToken);
                }
                else
                {
                    // Frame pacing catchup
                    nextFrameTick = currentTick;
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[WindowCapturer] Capture loop canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WindowCapturer] Error during window frame capture: {Message}", ex.Message);
        }
        finally
        {
            _logger.LogInformation(
                "[WindowCapturer] Capture resources released. TotalFrames={Count}, PrintWindow={Pw}, WindowDcFallback={Dc}, DesktopFallback={Desk}, ZeroWindowExposure={ZeroExposure}",
                TotalFramesCaptured, PrintWindowSuccessCount, WindowDcFallbackCount, DesktopFallbackCount,
                ZeroWindowExposureEnforced);
        }
    }

    private bool CaptureSingleFrame(IWindowCaptureSurface surface, IntPtr hwnd, WindowBounds winRect)
    {
        // 1. Primary (Zero-Window-Exposure compliant): PrintWindow with PW_RENDERFULLCONTENT
        if (surface.TryCapturePrintWindow(hwnd, PwRenderFullContent, winRect.Width, winRect.Height))
        {
            PrintWindowSuccessCount++;
            return true;
        }

        // 2. Secondary fallback: Window-specific DC (does not activate/focus window or read desktop pixels)
        if (surface.TryCaptureWindowDc(hwnd, winRect.Width, winRect.Height))
        {
            WindowDcFallbackCount++;
            return true;
        }

        // 3. Tertiary fallback: DWM Desktop screen coordinates ONLY when foreground activation was explicitly opted in
        if (AllowForegroundActivation && winRect.Left >= 0 && winRect.Top >= 0)
        {
            if (surface.TryCaptureDesktopScreen(winRect, winRect.Width, winRect.Height))
            {
                DesktopFallbackCount++;
                return true;
            }
        }

        return false;
    }

    private sealed class Win32WindowCaptureBackend : IWin32WindowCaptureBackend
    {
        public static Win32WindowCaptureBackend Instance { get; } = new();

        public IntPtr FindMainWindow(int processId, ILogger logger)
        {
            // Priority 1: Check exact target PID MainWindowHandle first
            try
            {
                using Process targetProc = Process.GetProcessById(processId);
                targetProc.Refresh();
                if (targetProc.MainWindowHandle != IntPtr.Zero &&
                    GetWindowRect(targetProc.MainWindowHandle, out RECT targetRect))
                {
                    int w = targetRect.Right - targetRect.Left;
                    int h = targetRect.Bottom - targetRect.Top;
                    if (w >= 320 && h >= 240)
                    {
                        logger.LogInformation(
                            "[WindowCapturer] Hooked HoN Game Window from exact target PID MainWindowHandle: PID={PID}, HWND=0x{HWND:X8}, Rect={W}x{H}",
                            targetProc.Id, targetProc.MainWindowHandle.ToInt64(), w, h);
                        return targetProc.MainWindowHandle;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[WindowCapturer] Exact PID {PID} MainWindowHandle probe notice: {Message}",
                    processId, ex.Message);
            }

            // Priority 2: Enumerate desktop windows matching exact target PID first, then fallback K2/HoN windows
            IntPtr exactPidHwnd = IntPtr.Zero;
            IntPtr fallbackHwnd = IntPtr.Zero;

            HashSet<uint> targetPids = [(uint)processId];
            try
            {
                Process[] allHon = Process.GetProcessesByName("hon_x64");
                foreach (Process p in allHon)
                {
                    targetPids.Add((uint)p.Id);
                    p.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[WindowCapturer] Process enumeration notice while resolving hon_x64 PIDs: {Message}",
                    ex.Message);
            }

            EnumWindowsProc proc = (hwnd, lParam) =>
            {
                try
                {
                    uint pid = 0;
                    GetWindowThreadProcessId(hwnd, out pid);

                    StringBuilder cls = new(256);
                    GetClassName(hwnd, cls, 256);
                    string className = cls.ToString();

                    StringBuilder title = new(256);
                    GetWindowText(hwnd, title, 256);
                    string windowTitle = title.ToString();

                    bool isExactPid = pid == (uint)processId;
                    bool isHonMatch = className.StartsWith("K2_", StringComparison.OrdinalIgnoreCase) ||
                                      windowTitle.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                                      targetPids.Contains(pid);

                    if (isExactPid || isHonMatch)
                    {
                        if (GetWindowRect(hwnd, out RECT r))
                        {
                            int w = r.Right - r.Left;
                            int h = r.Bottom - r.Top;
                            if (w >= 320 && h >= 240)
                            {
                                if (isExactPid)
                                {
                                    exactPidHwnd = hwnd;
                                    logger.LogInformation(
                                        "[WindowCapturer] Hooked exact-PID HoN Game Window: PID={PID}, HWND=0x{HWND:X8}, Class='{Class}', Title='{Title}', Rect={W}x{H}",
                                        pid, hwnd.ToInt64(), className, windowTitle, w, h);
                                    return false;
                                }

                                if (fallbackHwnd == IntPtr.Zero)
                                {
                                    fallbackHwnd = hwnd;
                                    logger.LogDebug(
                                        "[WindowCapturer] Recorded fallback HoN Game Window candidate: PID={PID}, HWND=0x{HWND:X8}, Class='{Class}', Title='{Title}', Rect={W}x{H}",
                                        pid, hwnd.ToInt64(), className, windowTitle, w, h);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[WindowCapturer] Window enumeration probe notice for HWND 0x{HWND:X8}: {Message}",
                        hwnd.ToInt64(), ex.Message);
                }

                return true;
            };

            IntPtr hDesk = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_ENUMERATE */);
            if (hDesk != IntPtr.Zero)
            {
                EnumDesktopWindows(hDesk, proc, IntPtr.Zero);
                CloseDesktop(hDesk);
            }
            else
            {
                EnumWindows(proc, IntPtr.Zero);
            }

            GC.KeepAlive(proc);

            if (exactPidHwnd != IntPtr.Zero) return exactPidHwnd;
            if (fallbackHwnd != IntPtr.Zero) return fallbackHwnd;

            // Priority 3: Enumerate threads of target PID for K2_Direct3D window class
            IntPtr threadHwnd = IntPtr.Zero;
            try
            {
                using Process p = Process.GetProcessById(processId);
                p.Refresh();
                foreach (ProcessThread thread in p.Threads)
                {
                    EnumThreadDelegate threadProc = (hwnd, lParam) =>
                    {
                        try
                        {
                            StringBuilder cls = new(256);
                            GetClassName(hwnd, cls, 256);
                            if (cls.ToString().Equals("K2_Direct3D", StringComparison.OrdinalIgnoreCase))
                            {
                                threadHwnd = hwnd;
                                return false;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "[WindowCapturer] Thread window probe notice: {Message}", ex.Message);
                        }

                        return true;
                    };

                    EnumThreadWindows((uint)thread.Id, threadProc, IntPtr.Zero);
                    GC.KeepAlive(threadProc);
                    if (threadHwnd != IntPtr.Zero) break;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[WindowCapturer] Process thread window scan notice for PID {PID}: {Message}",
                    processId, ex.Message);
            }

            return threadHwnd;
        }

        public bool TryGetWindowDimensions(IntPtr hwnd, out int width, out int height)
        {
            if (GetClientRect(hwnd, out RECT rect))
            {
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
                if (width > 0 && height > 0) return true;
            }

            if (GetWindowRect(hwnd, out rect))
            {
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
                return width > 0 && height > 0;
            }

            width = 0;
            height = 0;
            return false;
        }

        public bool IsWindowValid(IntPtr hwnd)
        {
            return IsWindow(hwnd);
        }

        public bool TryGetWindowBounds(IntPtr hwnd, out WindowBounds bounds)
        {
            if (GetWindowRect(hwnd, out RECT rect))
            {
                bounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
                return true;
            }

            bounds = default;
            return false;
        }

        public void ActivateForegroundWindow(IntPtr hwnd, ILogger logger)
        {
            try
            {
                uint fgThreadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                uint curThreadId = GetCurrentThreadId();
                bool attached = AttachThreadInput(curThreadId, fgThreadId, true);
                try
                {
                    ShowWindow(hwnd, 9 /* SW_RESTORE */);
                    SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                        0x0040 /* SWP_SHOWWINDOW */ | 0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */);
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(curThreadId, fgThreadId, false);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[WindowCapturer] Optional foreground activation failed for HWND 0x{HWND:X8}: {Message}",
                    hwnd.ToInt64(), ex.Message);
            }
        }

        public IWindowCaptureSurface CreateCaptureSurface(int targetWidth, int targetHeight)
        {
            return new Win32WindowCaptureSurface(targetWidth, targetHeight);
        }
    }

    private sealed class Win32WindowCaptureSurface : IWindowCaptureSurface
    {
        private readonly int _frameSizeBytes;
        private readonly IntPtr _hBitmap;
        private readonly IntPtr _hdcMem;
        private readonly IntPtr _hdcScreen;
        private readonly IntPtr _hOldBmp;
        private readonly IntPtr _pBits;
        private readonly int _targetHeight;
        private readonly int _targetWidth;
        private bool _disposed;
        private IntPtr _hdcScratch = IntPtr.Zero;
        private IntPtr _hScratchBitmap = IntPtr.Zero;
        private IntPtr _hScratchOldBmp = IntPtr.Zero;
        private int _scratchHeight;
        private int _scratchWidth;

        public Win32WindowCaptureSurface(int targetWidth, int targetHeight)
        {
            _targetWidth = targetWidth;
            _targetHeight = targetHeight;
            _frameSizeBytes = targetWidth * targetHeight * 4;

            BITMAPINFO bmi = new();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = targetWidth;
            bmi.bmiHeader.biHeight = -targetHeight; // Top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            _hdcScreen = GetDC(IntPtr.Zero);
            _hdcMem = CreateCompatibleDC(_hdcScreen);
            _pBits = IntPtr.Zero;
            _hBitmap = CreateDIBSection(_hdcMem, ref bmi, 0, out _pBits, IntPtr.Zero, 0);
            _hOldBmp = SelectObject(_hdcMem, _hBitmap);
        }

        public bool TryCapturePrintWindow(IntPtr hwnd, uint flags, int windowWidth, int windowHeight)
        {
            if (windowWidth == _targetWidth && windowHeight == _targetHeight)
            {
                return PrintWindow(hwnd, _hdcMem, flags);
            }

            EnsureScratchSurface(windowWidth, windowHeight);
            if (_hdcScratch == IntPtr.Zero || _hScratchBitmap == IntPtr.Zero)
            {
                return PrintWindow(hwnd, _hdcMem, flags);
            }

            if (!PrintWindow(hwnd, _hdcScratch, flags))
            {
                return false;
            }

            return StretchBlt(_hdcMem, 0, 0, _targetWidth, _targetHeight,
                _hdcScratch, 0, 0, windowWidth, windowHeight,
                TernaryRasterOperations.SRCCOPY);
        }

        public bool TryCaptureWindowDc(IntPtr hwnd, int windowWidth, int windowHeight)
        {
            IntPtr hdcWnd = GetDC(hwnd);
            if (hdcWnd == IntPtr.Zero) return false;

            try
            {
                return StretchBlt(_hdcMem, 0, 0, _targetWidth, _targetHeight,
                    hdcWnd, 0, 0, windowWidth, windowHeight,
                    TernaryRasterOperations.SRCCOPY);
            }
            finally
            {
                ReleaseDC(hwnd, hdcWnd);
            }
        }

        public bool TryCaptureDesktopScreen(WindowBounds bounds, int windowWidth, int windowHeight)
        {
            return StretchBlt(_hdcMem, 0, 0, _targetWidth, _targetHeight,
                _hdcScreen, bounds.Left, bounds.Top, windowWidth, windowHeight,
                TernaryRasterOperations.SRCCOPY);
        }

        public void CopyPixelsTo(byte[] destination)
        {
            if (_pBits != IntPtr.Zero)
            {
                Marshal.Copy(_pBits, destination, 0, Math.Min(destination.Length, _frameSizeBytes));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseScratchSurface();

            if (_hdcMem != IntPtr.Zero && _hOldBmp != IntPtr.Zero)
            {
                SelectObject(_hdcMem, _hOldBmp);
            }

            if (_hBitmap != IntPtr.Zero) DeleteObject(_hBitmap);
            if (_hdcMem != IntPtr.Zero) DeleteDC(_hdcMem);
            if (_hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _hdcScreen);
        }

        private void EnsureScratchSurface(int width, int height)
        {
            if (_hdcScratch != IntPtr.Zero && _scratchWidth == width && _scratchHeight == height)
            {
                return;
            }

            ReleaseScratchSurface();
            _hdcScratch = CreateCompatibleDC(_hdcScreen);
            if (_hdcScratch == IntPtr.Zero) return;

            _hScratchBitmap = CreateCompatibleBitmap(_hdcScreen, width, height);
            if (_hScratchBitmap == IntPtr.Zero)
            {
                DeleteDC(_hdcScratch);
                _hdcScratch = IntPtr.Zero;
                return;
            }

            _hScratchOldBmp = SelectObject(_hdcScratch, _hScratchBitmap);
            _scratchWidth = width;
            _scratchHeight = height;
        }

        private void ReleaseScratchSurface()
        {
            if (_hdcScratch != IntPtr.Zero && _hScratchOldBmp != IntPtr.Zero)
            {
                SelectObject(_hdcScratch, _hScratchOldBmp);
            }

            if (_hScratchBitmap != IntPtr.Zero) DeleteObject(_hScratchBitmap);
            if (_hdcScratch != IntPtr.Zero) DeleteDC(_hdcScratch);

            _hScratchOldBmp = IntPtr.Zero;
            _hScratchBitmap = IntPtr.Zero;
            _hdcScratch = IntPtr.Zero;
            _scratchWidth = 0;
            _scratchHeight = 0;
        }
    }

    #region Win32 P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint pila, out IntPtr ppvBits,
        IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest,
        IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc,
        TernaryRasterOperations dwRop);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
    }

    private enum TernaryRasterOperations : uint
    {
        SRCCOPY = 0x00CC0020
    }

    #endregion
}
