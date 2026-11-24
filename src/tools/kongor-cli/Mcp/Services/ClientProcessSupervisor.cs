// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service interface for client process resolution, lifecycle supervision, and window positioning.
/// </summary>
public interface IClientProcessSupervisor
{
    bool TryResolveClientExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable);
    Process? FindRunningClientProcess();
    bool IsClientProcess(Process process);
    string? TryGetProcessCommandLine(int processId);
    bool SetClientWindowHeadless(IntPtr hwnd, int width = 1280, int height = 720, int offscreenX = -10000, int offscreenY = -10000);
    bool SetClientWindowForeground(IntPtr hwnd, int x = 0, int y = 0, int width = 1920, int height = 1080, bool activate = true);
    bool SetClientWindowMinimized(IntPtr hwnd);
    bool IsWindowOffscreen(IntPtr hwnd);
    void HideAllProcessWindows(int processId, int offscreenX = -10000, int offscreenY = -10000);
    void HideAllProcessWindows(int processId, int offscreenX, int offscreenY, int width, int height) => HideAllProcessWindows(processId, offscreenX, offscreenY);
    void StartOffscreenWindowWatchdog(Process process, int offscreenX = -10000, int offscreenY = -10000);
    void StartOffscreenWindowWatchdog(Process process, int offscreenX, int offscreenY, int width, int height) => StartOffscreenWindowWatchdog(process, offscreenX, offscreenY);
    void StopOffscreenWindowWatchdog(int processId = 0);
    bool IsUserClient(Process process);
}

/// <summary>
/// Supervises player client process (hon_x64.exe) discovery, process tree inspection,
/// and headless / offscreen desktop window containment.
/// </summary>
public sealed class ClientProcessSupervisor : IClientProcessSupervisor
{
    public static volatile bool SuppressOffscreenWatchdog = false;
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> ActiveWatchdogs = new();
    private static readonly ConcurrentDictionary<int, string> ProcessCommandLineCache = new();

    static ClientProcessSupervisor()
    {
        WindowCapturer.OnTransientCaptureStateChanged = isCapturing => SuppressOffscreenWatchdog = isCapturing;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOREDRAW = 0x0008;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    public bool TryResolveClientExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable)
    {
        executable = null;

        // 1. Explicit Custom Path (must not be WILLOWMAKER)
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(customPath);
                FileInfo customFi = new(expanded);
                if (customFi.Exists && !IsWillowmakerExecutable(customFi.Name))
                {
                    executable = customFi;
                    return true;
                }
                string fullPath = Path.GetFullPath(expanded);
                if (File.Exists(fullPath) && !IsWillowmakerExecutable(Path.GetFileName(fullPath)))
                {
                    executable = new FileInfo(fullPath);
                    return true;
                }
            }
            catch { }

            return false;
        }

        string baseDir = workingDir ?? Environment.CurrentDirectory;
        string repoRoot = FindRepoRoot(baseDir);

        // Candidate client filenames - explicitly excluding WILLOWMAKER
        string[] candidateNames =
        [
            "hon_x64.exe",
            "hon_x64",
            "hon.exe",
            "hon"
        ];

        string[] candidateDirectories =
        [
            Path.Combine(repoRoot, "games", "client"),
            Path.Combine(repoRoot, "games", "server"),
            baseDir,
            AppContext.BaseDirectory
        ];

        foreach (string dir in candidateDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (string name in candidateNames)
            {
                string fullPath = Path.Combine(dir, name);
                if (File.Exists(fullPath))
                {
                    executable = new FileInfo(fullPath);
                    return true;
                }
            }
        }

        // Search subdirectories inside games/client and games/server for hon_x64.exe
        string[] candidateSubRoots = [Path.Combine(repoRoot, "games", "client"), Path.Combine(repoRoot, "games", "server")];
        foreach (string subRoot in candidateSubRoots)
        {
            if (!Directory.Exists(subRoot)) continue;
            try
            {
                foreach (string candidateFile in Directory.EnumerateFiles(subRoot, "hon_x64.exe", SearchOption.AllDirectories))
                {
                    FileInfo fi = new(candidateFile);
                    if (fi.Exists && !IsWillowmakerExecutable(fi.Name))
                    {
                        executable = fi;
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    public static bool IsWillowmakerExecutable(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return false;
        string clean = filename.Trim();
        return clean.Equals("WILLOWMAKER.exe", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("WILLOWMAKER", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("willowmaker.exe", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("willowmaker", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsClientProcess(Process process)
    {
        try
        {
            if (process == null || process.HasExited) return false;

            // Never treat WILLOWMAKER GUI as a player game client
            if (IsWillowmakerExecutable(process.ProcessName)) return false;

            // Dedicated servers launch with -dedicated and have svr_ or Dedicated in title/cmdline
            try
            {
                string? cmdLine = TryGetProcessCommandLine(process.Id);
                if (!string.IsNullOrEmpty(cmdLine))
                {
                    if (cmdLine.Contains("-dedicated", StringComparison.OrdinalIgnoreCase) ||
                        cmdLine.Contains("-server", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            catch { }

            string title = process.MainWindowTitle;
            if (!string.IsNullOrEmpty(title))
            {
                if (title.Contains("dedicated", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Unnamed_Server", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Exclude binaries running from games/server
            string? modulePath = null;
            try { modulePath = process.MainModule?.FileName; } catch { }
            if (!string.IsNullOrEmpty(modulePath))
            {
                if (IsWillowmakerExecutable(Path.GetFileName(modulePath))) return false;
                if (modulePath.Contains("games\\server", StringComparison.OrdinalIgnoreCase) ||
                    modulePath.Contains("games/server", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    RECT rect;
                    if (GetWindowRect(process.MainWindowHandle, out rect))
                    {
                        int w = rect.Right - rect.Left;
                        int h = rect.Bottom - rect.Top;
                        if (w <= 0 || h <= 0) return false;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch { }
            }

            string? cmd = TryGetProcessCommandLine(process.Id);
            if (!string.IsNullOrEmpty(cmd))
            {
                if (cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains("-server", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains("-manager", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    string title2 = process.MainWindowTitle;
                    if (title2.Contains("dedicated", StringComparison.OrdinalIgnoreCase) ||
                        title2.EndsWith("hon_x64.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsUserClient(Process process)
    {
        try
        {
            if (process == null || process.HasExited) return false;
            if (!IsClientProcess(process)) return false;

            // Check if it has a visible window on the active desktop screen
            IntPtr hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    if (rect.Left > -5000 && rect.Top > -5000 && (rect.Right - rect.Left) > 100 && (rect.Bottom - rect.Top) > 100)
                    {
                        // Onscreen visible window -> player's active game!
                        return true;
                    }
                }
            }

            string? cmd = TryGetProcessCommandLine(process.Id);
            if (!string.IsNullOrEmpty(cmd))
            {
                if (cmd.Contains("KONGOR_HEADLESS", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains("vid_pos -", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Contains("-x -", StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Test client
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public Process? FindRunningClientProcess()
    {
        if (Handlers.GameServerManageHandler.IsTestExecution)
        {
            return null;
        }

        try
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            foreach (Process p in procs)
            {
                if (p.Id == Environment.ProcessId || p.HasExited) continue;
                if (IsClientProcess(p)) return p;
            }
        }
        catch { }

        return null;
    }

    public string? TryGetProcessCommandLine(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;

        if (ProcessCommandLineCache.TryGetValue(processId, out string? cached))
        {
            return cached;
        }

        try
        {
            using Process proc = Process.GetProcessById(processId);
            if (proc.HasExited) return null;

            ProcessStartInfo psi = new("powershell", $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter \\\"ProcessId = {processId}\\\").CommandLine\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process? p = Process.Start(psi);
            if (p != null)
            {
                string cmd = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    ProcessCommandLineCache[processId] = cmd;
                    return cmd;
                }
            }
        }
        catch { }

        return null;
    }

    public bool SetClientWindowHeadless(IntPtr hwnd, int width = 1280, int height = 720, int offscreenX = -10000, int offscreenY = -10000)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        // Position window offscreen without SW_HIDE so DXVK/Vulkan swapchain presentation remains active at 60 FPS
        return SetWindowPos(hwnd, IntPtr.Zero, offscreenX, offscreenY, width, height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    public void StartOffscreenWindowWatchdog(Process process, int offscreenX = -10000, int offscreenY = -10000)
        => StartOffscreenWindowWatchdog(process, offscreenX, offscreenY, 1280, 720);

    public void StartOffscreenWindowWatchdog(Process process, int offscreenX, int offscreenY, int width, int height)
    {
        if (!OperatingSystem.IsWindows() || process == null) return;

        int pid = process.Id;
        StopOffscreenWindowWatchdog(pid);

        CancellationTokenSource cts = new CancellationTokenSource();
        ActiveWatchdogs[pid] = cts;

        // Immediately hide and reposition any existing windows for the process
        HideAllProcessWindows(pid, offscreenX, offscreenY, width, height);

        _ = Task.Run(async () =>
        {
            while (!process.HasExited && !cts.IsCancellationRequested)
            {
                if (!SuppressOffscreenWatchdog)
                {
                    HideAllProcessWindows(pid, offscreenX, offscreenY, width, height);
                }
                try
                {
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            ActiveWatchdogs.TryRemove(pid, out _);
        }, cts.Token);
    }

    public void StopOffscreenWindowWatchdog(int processId = 0)
    {
        try
        {
            if (processId > 0)
            {
                if (ActiveWatchdogs.TryRemove(processId, out CancellationTokenSource? cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
            else
            {
                foreach (KeyValuePair<int, CancellationTokenSource> entry in ActiveWatchdogs)
                {
                    try
                    {
                        entry.Value.Cancel();
                        entry.Value.Dispose();
                    }
                    catch { }
                }
                ActiveWatchdogs.Clear();
            }
        }
        catch { }
    }

    public void HideAllProcessWindows(int processId, int offscreenX = -10000, int offscreenY = -10000)
        => HideAllProcessWindows(processId, offscreenX, offscreenY, 1280, 720);

    public void HideAllProcessWindows(int processId, int offscreenX, int offscreenY, int width, int height)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return;
        int safeWidth = width > 0 ? width : 1280;
        int safeHeight = height > 0 ? height : 720;
        try
        {
            int matched = HideProcessWindowsOnCurrentDesktop(processId, offscreenX, offscreenY, safeWidth, safeHeight);
            if (matched == 0)
            {
                // When MCP/SHJ runs inside an isolated sandbox desktop (e.g. exebox-*) while hon_x64.exe
                // was launched onto WinSta0\Default via InteractiveProcessLauncher, inspect Default desktop
                // on a short-lived worker thread so the caller thread's desktop binding is never disturbed.
                Thread worker = new(() =>
                {
                    IntPtr origDesktop = GetThreadDesktop(GetCurrentThreadId());
                    IntPtr defaultDesktop = OpenDesktop("Default", 0, false, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_ENUMERATE | DESKTOP_SWITCHDESKTOP);
                    if (defaultDesktop == IntPtr.Zero)
                    {
                        return;
                    }

                    try
                    {
                        if (SetThreadDesktop(defaultDesktop))
                        {
                            HideProcessWindowsOnCurrentDesktop(processId, offscreenX, offscreenY, safeWidth, safeHeight);
                        }
                    }
                    finally
                    {
                        if (origDesktop != IntPtr.Zero)
                        {
                            SetThreadDesktop(origDesktop);
                        }
                        CloseDesktop(defaultDesktop);
                    }
                })
                {
                    IsBackground = true,
                    Name = "KongorOffscreenContainment"
                };
                worker.Start();
                worker.Join(150);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ClientProcessSupervisor] HideAllProcessWindows notice for PID {processId}: {ex.Message}");
        }
    }

    private static int HideProcessWindowsOnCurrentDesktop(int processId, int offscreenX, int offscreenY, int safeWidth, int safeHeight)
    {
        int matched = 0;
        IntPtr foregroundHwnd = GetForegroundWindow();
        IntPtr fallbackFocusHwnd = IntPtr.Zero;
        bool targetOwnsForeground = false;

        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == (uint)processId)
            {
                matched++;
                long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
                if ((exStyle & WS_EX_NOACTIVATE) == 0)
                {
                    SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_NOACTIVATE));
                }

                bool isForeground = foregroundHwnd == hWnd;
                if (isForeground)
                {
                    targetOwnsForeground = true;
                }

                if (GetWindowRect(hWnd, out RECT rect))
                {
                    int curW = rect.Right - rect.Left;
                    int curH = rect.Bottom - rect.Top;
                    bool alreadyOffscreen = rect.Left <= -4000 || rect.Top <= -4000 || rect.Left >= 4000 || rect.Top >= 4000;
                    if (!alreadyOffscreen || rect.Left != offscreenX || rect.Top != offscreenY || curW != safeWidth || curH != safeHeight || isForeground)
                    {
                        // Position window safely offscreen at HWND_BOTTOM without SW_HIDE so DXVK/Vulkan swapchain presentation remains active
                        SetWindowPos(hWnd, HWND_BOTTOM, offscreenX, offscreenY, safeWidth, safeHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    }
                }
            }
            else if (fallbackFocusHwnd == IntPtr.Zero && IsWindowVisible(hWnd) && GetWindowRect(hWnd, out RECT otherRect))
            {
                int otherW = otherRect.Right - otherRect.Left;
                int otherH = otherRect.Bottom - otherRect.Top;
                bool otherOnScreen = otherRect.Left > -4000 && otherRect.Top > -4000 && otherRect.Left < 4000 && otherRect.Top < 4000;
                if (otherOnScreen && otherW >= 100 && otherH >= 100)
                {
                    fallbackFocusHwnd = hWnd;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (targetOwnsForeground)
        {
            IntPtr yieldTarget = fallbackFocusHwnd != IntPtr.Zero ? fallbackFocusHwnd : GetShellWindow();
            if (yieldTarget != IntPtr.Zero)
            {
                SetForegroundWindow(yieldTarget);
            }
        }

        return matched;
    }

    public bool SetClientWindowForeground(IntPtr hwnd, int x = 0, int y = 0, int width = 1920, int height = 1080, bool activate = true)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        bool res = SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_SHOWWINDOW);
        if (activate)
        {
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
        }
        return res;
    }

    public bool SetClientWindowMinimized(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        return ShowWindow(hwnd, 6); // SW_MINIMIZE
    }

    public bool IsWindowOffscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        if (GetWindowRect(hwnd, out RECT rect))
        {
            return rect.Left >= 5000 || rect.Top >= 5000 || rect.Right <= -5000 || rect.Bottom <= -5000;
        }
        return false;
    }

    public ScrcpyAtomicPipeInterrupter PipeInterrupter { get; } = new();

    private static string FindRepoRoot(string startingDir)
    {
        DirectoryInfo? current = new(startingDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "PROJECT.md")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return startingDir;
    }
}

/// <summary>
/// Atomic pipe/socket handle interrupter ported from scrcpy (app/src/util/intr.c, #234).
/// Registers active SafePipeHandle / Stream handles under lock and invokes Win32 CancelIoEx(handle, IntPtr.Zero)
/// upon Interrupt() to unblock pending kernel Named Pipe reads without CloseHandle handle-recycling races.
/// </summary>
public sealed class ScrcpyAtomicPipeInterrupter
{
    private readonly Lock _sync = new();
    private readonly List<Stream> _registeredStreams = new();
    private readonly List<SafeHandle> _registeredSafeHandles = new();
    private readonly CancellationTokenSource _intrCts = new();
    private int _interrupted;
    private int _cancelIoExCallCount;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    public bool IsInterrupted => Volatile.Read(ref _interrupted) != 0;

    public int CancelIoExCallCount => Volatile.Read(ref _cancelIoExCallCount);

    public CancellationToken Token => _intrCts.Token;

    public bool RegisterHandle(SafeHandle? handle)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _interrupted) != 0)
            {
                return false;
            }

            if (handle != null && !handle.IsInvalid && !handle.IsClosed && !_registeredSafeHandles.Contains(handle))
            {
                _registeredSafeHandles.Add(handle);
            }

            return true;
        }
    }

    public void UnregisterHandle(SafeHandle? handle)
    {
        if (handle == null) return;
        lock (_sync)
        {
            _registeredSafeHandles.Remove(handle);
        }
    }

    public bool RegisterPipe(Stream? stream)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _interrupted) != 0)
            {
                return false;
            }

            if (stream != null && !_registeredStreams.Contains(stream))
            {
                _registeredStreams.Add(stream);
                if (stream is System.IO.Pipes.PipeStream pipeStream &&
                    !pipeStream.SafePipeHandle.IsInvalid &&
                    !pipeStream.SafePipeHandle.IsClosed &&
                    !_registeredSafeHandles.Contains(pipeStream.SafePipeHandle))
                {
                    _registeredSafeHandles.Add(pipeStream.SafePipeHandle);
                }
            }

            return true;
        }
    }

    public void UnregisterPipe(Stream? stream)
    {
        if (stream == null) return;
        lock (_sync)
        {
            _registeredStreams.Remove(stream);
            if (stream is System.IO.Pipes.PipeStream pipeStream)
            {
                _registeredSafeHandles.Remove(pipeStream.SafePipeHandle);
            }
        }
    }

    public void Interrupt()
    {
        SafeHandle[] handleSnapshot;
        lock (_sync)
        {
            Interlocked.Exchange(ref _interrupted, 1);
            handleSnapshot = _registeredSafeHandles.ToArray();
            _registeredSafeHandles.Clear();
            _registeredStreams.Clear();
        }

        foreach (SafeHandle safeHandle in handleSnapshot)
        {
            if (!OperatingSystem.IsWindows() || safeHandle.IsInvalid || safeHandle.IsClosed)
            {
                continue;
            }

            bool refAdded = false;
            try
            {
                safeHandle.DangerousAddRef(ref refAdded);
                if (refAdded)
                {
                    CancelIoEx(safeHandle.DangerousGetHandle(), IntPtr.Zero);
                    Interlocked.Increment(ref _cancelIoExCallCount);
                }
            }
            catch
            {
            }
            finally
            {
                if (refAdded)
                {
                    try
                    {
                        safeHandle.DangerousRelease();
                    }
                    catch
                    {
                    }
                }
            }
        }

        try
        {
            _intrCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task<int> ReadWithInterruptAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!RegisterPipe(stream))
        {
            throw new OperationCanceledException("ScrcpyAtomicPipeInterrupter is already interrupted.");
        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _intrCts.Token);
        using CancellationTokenRegistration reg = cancellationToken.Register(Interrupt);

        try
        {
            int bytesRead = await stream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
            if (IsInterrupted)
            {
                throw new OperationCanceledException("Pipe read aborted by ScrcpyAtomicPipeInterrupter.");
            }
            return bytesRead;
        }
        catch (Exception ex) when (IsInterrupted && ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
            throw new OperationCanceledException("Pipe read aborted immediately by ScrcpyAtomicPipeInterrupter.", ex, _intrCts.Token);
        }
        finally
        {
            UnregisterPipe(stream);
        }
    }
}


