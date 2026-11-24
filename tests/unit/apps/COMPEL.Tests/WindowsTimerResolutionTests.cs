// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Tests;

/// <summary>
///     Verifies that the high-resolution system timer resolution wrapper initializes,
///     reports active status on Windows, and disposes idempotently.
/// </summary>
public sealed class WindowsTimerResolutionTests
{
    [Fact]
    public void TimerResolution_Enables_And_Disposes_Cleanly()
    {
        using WindowsTimerResolution timer = WindowsTimerResolution.Enable();

        if (OperatingSystem.IsWindows()) Assert.True(timer.IsActive);

        timer.Dispose();
        Assert.False(timer.IsActive);
    }
}
