// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging;

namespace COMPEL.Internals;

/// <summary>
///     Cross-platform high-precision system timer implementation.
///     On Windows, enforces 1.0ms timer resolution via <see cref="WindowsTimerResolution" /> (winmm.dll timeBeginPeriod).
///     On Linux and POSIX platforms, leverages the Linux high-resolution timer subsystem (hrtimer / CLOCK_MONOTONIC_RAW).
/// </summary>
public sealed class PrecisionTimerService : IPrecisionTimerService
{
    private readonly ILogger? _logger;
    private WindowsTimerResolution? _windowsTimer;
    private bool _isActive;
    private bool _disposed;

    public PrecisionTimerService(ILogger<PrecisionTimerService>? logger = null)
    {
        _logger = logger;
    }

    public PrecisionTimerService(ILogger? logger)
    {
        _logger = logger;
    }

    public bool IsActive => _isActive;

    public void Enable()
    {
        if (_disposed || _isActive) return;

        if (OperatingSystem.IsWindows())
        {
            _windowsTimer = WindowsTimerResolution.Enable(_logger);
            _isActive = _windowsTimer.IsActive;
        }
        else
        {
            // On Linux / macOS, POSIX high-resolution timers (hrtimer) operate natively with microsecond resolution
            _isActive = true;
            _logger?.LogInformation("[PrecisionTimerService] Linux/POSIX high-resolution hrtimer pacing active (sub-millisecond resolution).");
        }
    }

    public void Disable()
    {
        if (!_isActive) return;

        if (_windowsTimer != null)
        {
            _windowsTimer.Dispose();
            _windowsTimer = null;
        }

        _isActive = false;
        _logger?.LogInformation("[PrecisionTimerService] Restored standard system timer resolution.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disable();
    }
}
