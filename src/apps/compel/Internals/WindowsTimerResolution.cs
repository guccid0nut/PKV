// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.InteropServices;

namespace COMPEL.Internals;

/// <summary>
///     Sets and restores high-resolution system timer resolution (1.0ms) on Windows.
///     Standard Windows timer resolution defaults to ~15.6ms, which can cause micro-jitter
///     and scheduling drift in 20Hz / 50ms game server tick loops. Setting 1.0ms resolution
///     ensures exact, deterministic tick pacing for spawned game server instances and async tasks.
/// </summary>
public sealed partial class WindowsTimerResolution : IDisposable
{
    private const uint TargetTimerPeriodMilliseconds = 1;

    public bool IsActive { get; private set; }

    public void Dispose()
    {
        if (IsActive && OperatingSystem.IsWindows())
            try
            {
                _ = TimeEndPeriod(TargetTimerPeriodMilliseconds);
                IsActive = false;
                GC.SuppressFinalize(this);
            }
            catch
            {
                // Suppress shutdown exception
            }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint uPeriod);

    public static WindowsTimerResolution Enable(ILogger? logger = null)
    {
        WindowsTimerResolution instance = new();
        if (OperatingSystem.IsWindows())
            try
            {
                uint result = TimeBeginPeriod(TargetTimerPeriodMilliseconds);
                if (result == 0) // TIMERR_NOERROR = 0
                {
                    instance.IsActive = true;
                    logger?.LogInformation(
                        "[WindowsTimerResolution] Enforced high-resolution system timer ({Period}ms) for deterministic game server tick pacing.",
                        TargetTimerPeriodMilliseconds);
                }
                else
                {
                    logger?.LogWarning(
                        "[WindowsTimerResolution] timeBeginPeriod({Period}) returned error code {Result}.",
                        TargetTimerPeriodMilliseconds, result);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[WindowsTimerResolution] Failed to invoke winmm.dll timeBeginPeriod.");
            }

        return instance;
    }
}
