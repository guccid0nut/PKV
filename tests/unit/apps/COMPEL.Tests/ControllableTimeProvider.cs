// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Tests;

/// <summary>
///     A time provider whose clock only moves when a test moves it, so behaviour proportional to elapsed time can be asserted exactly rather than waited for.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 01, 01, 00, 00, 00, TimeSpan.Zero);

    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => timestamp;

    public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(timestamp);

    internal void Advance(TimeSpan interval) => timestamp += interval.Ticks;
}
