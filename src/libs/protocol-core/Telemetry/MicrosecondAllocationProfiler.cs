// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;

namespace ASPIRE.Common.Telemetry;

/// <summary>
/// High-performance, zero-allocation microsecond memory profiler for continuous profiling.
/// Captures exact thread-allocated bytes and microsecond execution latency without attaching invasive debuggers.
/// </summary>
public static class MicrosecondAllocationProfiler
{
    public const string ActivitySourceName = "NEXUS.AllocationProfiler";
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    /// <summary>
    /// Begins tracking thread allocations and microsecond duration for a scoped code block.
    /// Usage: using var _ = MicrosecondAllocationProfiler.Track("LuaExecution", "PlayerStats");
    /// </summary>
    public static AllocationScope Track(string operation, string category = "General")
    {
        return new AllocationScope(operation, category);
    }

    /// <summary>
    /// Starts an OpenTelemetry trace activity and tracks microsecond allocations.
    /// </summary>
    public static ScopedActivityAllocation TrackActivity(string operation, string category = "General")
    {
        Activity? activity = Source.StartActivity($"Allocation/{operation}", ActivityKind.Internal);
        activity?.SetTag("code.function", operation);
        activity?.SetTag("allocation.category", category);
        return new ScopedActivityAllocation(activity, operation, category);
    }

    /// <summary>
    /// Records allocation telemetry metrics and tags on the active Activity.
    /// </summary>
    public static void Record(long bytes, double elapsedMicroseconds, string operation, string category)
    {
        double rateMbSec = elapsedMicroseconds > 0.0
            ? (bytes / (1024.0 * 1024.0)) / (elapsedMicroseconds / 1_000_000.0)
            : 0.0;

        Activity? current = Activity.Current;
        if (current != null)
        {
            current.SetTag("allocation.bytes", bytes);
            current.SetTag("allocation.microseconds", elapsedMicroseconds);
            current.SetTag("allocation.rate_mb_per_sec", rateMbSec);
            current.SetTag("allocation.operation", operation);
            current.SetTag("allocation.category", category);
        }

        NexusMetrics.RecordAllocation(bytes, elapsedMicroseconds, operation, category);
    }
}

/// <summary>
/// Zero-heap-allocation ref struct tracking thread-allocated bytes and microsecond duration.
/// </summary>
public readonly ref struct AllocationScope
{
    private readonly long _startBytes;
    private readonly long _startTicks;
    private readonly string _operation;
    private readonly string _category;

    public AllocationScope(string operation, string category)
    {
        _operation = operation;
        _category = category;
        _startBytes = GC.GetAllocatedBytesForCurrentThread();
        _startTicks = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        long endTicks = Stopwatch.GetTimestamp();
        long endBytes = GC.GetAllocatedBytesForCurrentThread();

        long allocatedBytes = Math.Max(0, endBytes - _startBytes);
        long elapsedTicks = Math.Max(0, endTicks - _startTicks);
        double elapsedUs = (elapsedTicks * 1_000_000.0) / Stopwatch.Frequency;

        MicrosecondAllocationProfiler.Record(allocatedBytes, elapsedUs, _operation, _category);
    }
}

/// <summary>
/// Disposable wrapper that completes an Activity and records microsecond allocation tags.
/// </summary>
public readonly struct ScopedActivityAllocation : IDisposable
{
    private readonly Activity? _activity;
    private readonly long _startBytes;
    private readonly long _startTicks;
    private readonly string _operation;
    private readonly string _category;

    public ScopedActivityAllocation(Activity? activity, string operation, string category)
    {
        _activity = activity;
        _operation = operation;
        _category = category;
        _startBytes = GC.GetAllocatedBytesForCurrentThread();
        _startTicks = Stopwatch.GetTimestamp();
    }

    public Activity? Activity => _activity;

    public void Dispose()
    {
        long endTicks = Stopwatch.GetTimestamp();
        long endBytes = GC.GetAllocatedBytesForCurrentThread();

        long allocatedBytes = Math.Max(0, endBytes - _startBytes);
        long elapsedTicks = Math.Max(0, endTicks - _startTicks);
        double elapsedUs = (elapsedTicks * 1_000_000.0) / Stopwatch.Frequency;

        MicrosecondAllocationProfiler.Record(allocatedBytes, elapsedUs, _operation, _category);
        _activity?.Dispose();
    }
}
