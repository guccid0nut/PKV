// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ASPIRE.Common.Telemetry;

/// <summary>
/// Cross-platform continuous profiling adapter correlating eBPF kernel tracepoints (Linux)
/// and EventPipe GC Allocation Ticks (Windows) into OpenTelemetry Activity spans and events.
/// </summary>
public static class EbpfAllocationTraceAdapter
{
    public const string ActivitySourceName = "NEXUS.EbpfTrace";
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    /// <summary>
    /// Gets whether native eBPF kernel tracing is supported on the current host.
    /// Returns true on Linux kernel 4.18+ with BPF capabilities, false on Windows.
    /// </summary>
    public static bool IsNativeEbpfSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Emits a microsecond allocation trace span with eBPF/EventPipe semantic attributes.
    /// </summary>
    public static Activity? EmitAllocationTrace(
        string typeName,
        long sizeBytes,
        double durationUs,
        string? customSource = null)
    {
        string traceSource = customSource ?? (IsNativeEbpfSupported ? "eBPF" : "EventPipe");

        Activity? activity = Source.StartActivity($"AllocationTick/{typeName}", ActivityKind.Internal);
        if (activity != null)
        {
            activity.SetTag("profiler.system", "ContinuousProfiling");
            activity.SetTag("ebpf.trace_source", traceSource);
            activity.SetTag("memory.allocated_type", typeName);
            activity.SetTag("memory.allocated_bytes", sizeBytes);
            activity.SetTag("memory.duration_us", durationUs);
            activity.SetTag("thread.id", Environment.CurrentManagedThreadId);

            activity.AddEvent(new ActivityEvent("allocation_tick", tags: new ActivityTagsCollection
            {
                ["type"] = typeName,
                ["bytes"] = sizeBytes,
                ["duration_us"] = durationUs,
                ["source"] = traceSource
            }));
        }

        // Also update allocation meters
        NexusMetrics.RecordAllocation(sizeBytes, durationUs, typeName, "AllocationTick");
        return activity;
    }
}
