// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Runtime.InteropServices;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Telemetry;

public class EbpfAllocationTraceAdapterTests
{
    [Fact]
    public void EbpfAllocationTraceAdapter_IsNativeEbpfSupported_MatchesHostOperatingSystem()
    {
        // Assert
        bool expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        EbpfAllocationTraceAdapter.IsNativeEbpfSupported.Should().Be(expected);
    }

    [Fact]
    public void EbpfAllocationTraceAdapter_EmitAllocationTrace_GeneratesActivityWithSemanticAttributesAndEvents()
    {
        // Arrange
        Activity? capturedActivity = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = s => s.Name == EbpfAllocationTraceAdapter.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => capturedActivity = a
        };
        ActivitySource.AddActivityListener(listener);

        const string typeName = "System.Byte[]";
        const long sizeBytes = 65536;
        const double durationUs = 42.5;

        // Act
        using (Activity? activity = EbpfAllocationTraceAdapter.EmitAllocationTrace(typeName, sizeBytes, durationUs))
        {
            activity.Should().NotBeNull();
            activity!.OperationName.Should().Be($"AllocationTick/{typeName}");
            activity.GetTagItem("profiler.system").Should().Be("ContinuousProfiling");
            activity.GetTagItem("memory.allocated_type").Should().Be(typeName);
            activity.GetTagItem("memory.allocated_bytes").Should().Be(sizeBytes);
            activity.GetTagItem("memory.duration_us").Should().Be(durationUs);
            activity.GetTagItem("thread.id").Should().Be(Environment.CurrentManagedThreadId);

            string expectedSource = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "eBPF" : "EventPipe";
            activity.GetTagItem("ebpf.trace_source").Should().Be(expectedSource);

            // Verify event
            activity.Events.Should().ContainSingle(e => e.Name == "allocation_tick");
            ActivityEvent evt = activity.Events.First();
            evt.Tags.First(t => t.Key == "type").Value.Should().Be(typeName);
            evt.Tags.First(t => t.Key == "bytes").Value.Should().Be(sizeBytes);
            evt.Tags.First(t => t.Key == "duration_us").Value.Should().Be(durationUs);
        }

        // Assert
        capturedActivity.Should().NotBeNull();
    }

    [Fact]
    public void EmitAllocationTrace_WithCustomSource_UsesProvidedSourceTag()
    {
        // Arrange
        using ActivityListener listener = new()
        {
            ShouldListenTo = s => s.Name == EbpfAllocationTraceAdapter.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        using Activity? activity = EbpfAllocationTraceAdapter.EmitAllocationTrace(
            "CustomType",
            2048,
            15.0,
            customSource: "CustomEbpfProbe");

        // Assert
        activity.Should().NotBeNull();
        activity!.GetTagItem("ebpf.trace_source").Should().Be("CustomEbpfProbe");
    }
}
