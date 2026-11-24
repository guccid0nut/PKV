// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Internals;

/// <summary>
///     Provides high-resolution multimedia system timer pacing across operating systems,
///     guaranteeing sub-millisecond tick precision (1.0ms) without thread scheduling jitter.
/// </summary>
public interface IPrecisionTimerService : IDisposable
{
    /// <summary>
    ///     Indicates whether high-precision timer pacing is currently active.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    ///     Enables high-precision 1.0ms timer pacing.
    /// </summary>
    void Enable();

    /// <summary>
    ///     Restores standard system timer resolution.
    /// </summary>
    void Disable();
}
