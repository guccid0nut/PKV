// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Sets and restores high-resolution system timer resolution (1.0ms) on Windows.
///     Standard Windows timer resolution defaults to ~15.6ms, which can cause micro-jitter
///     and scheduling drift in uncapped 1000 FPS game loops. Setting 1.0ms resolution
///     ensures exact, deterministic sub-millisecond thread scheduling for the game client.
///     On non-Windows platforms (Linux/macOS), the kernel natively operates with high-resolution
///     tickless timers, so Win32 calls are safely bypassed.
/// </summary>
public sealed class WindowsTimerResolution : IDisposable
{
    private const uint TargetTimerPeriodMilliseconds = 1;

    public bool IsActive { get; private set; }

    public void Dispose()
    {
        if (IsActive && OperatingSystem.IsWindows())
            try
            {
                TimeEndPeriod(TargetTimerPeriodMilliseconds);
                IsActive = false;
            }
            catch
            {
                // Suppress shutdown exception
            }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    public static WindowsTimerResolution Enable(Action<string>? logAction = null)
    {
        WindowsTimerResolution instance = new();
        if (OperatingSystem.IsWindows())
            try
            {
                uint result = TimeBeginPeriod(TargetTimerPeriodMilliseconds);
                if (result == 0) // TIMERR_NOERROR = 0
                {
                    instance.IsActive = true;
                    logAction?.Invoke(
                        $"[WindowsTimerResolution] Enforced high-resolution system timer ({TargetTimerPeriodMilliseconds}.0ms) for ultra-low latency game thread scheduling.");
                }
                else
                {
                    logAction?.Invoke(
                        $"[WindowsTimerResolution] timeBeginPeriod({TargetTimerPeriodMilliseconds}) returned error code {result}.");
                }
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[WindowsTimerResolution] Failed to invoke winmm.dll timeBeginPeriod: {ex.Message}");
            }
        else
            logAction?.Invoke(
                "[WindowsTimerResolution] Running on POSIX platform (native sub-millisecond hrtimers active).");

        return instance;
    }
}
