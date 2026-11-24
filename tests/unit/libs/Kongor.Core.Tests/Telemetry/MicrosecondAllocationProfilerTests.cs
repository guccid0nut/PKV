// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Telemetry;

public class MicrosecondAllocationProfilerTests
{
    [Fact]
    public void MicrosecondAllocationProfiler_Track_MeasuresThreadAllocationsAndElapsedTime()
    {
        // Arrange & Act
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        using (MicrosecondAllocationProfiler.Track("ByteAllocationTest", "UnitTests"))
        {
            // Allocate 128KB on current thread
            byte[] buffer = new byte[128 * 1024];
            buffer[0] = 1;
        }
        long endBytes = GC.GetAllocatedBytesForCurrentThread();

        // Assert: Thread allocated at least 128KB during this window
        (endBytes - startBytes).Should().BeGreaterThanOrEqualTo(128 * 1024);
    }

    [Fact]
    public void MicrosecondAllocationProfiler_TrackActivity_EnrichesActivityWithMicrosecondAllocationTags()
    {
        // Arrange
        Activity? capturedActivity = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = s => s.Name == MicrosecondAllocationProfiler.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => capturedActivity = a
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        using (var scope = MicrosecondAllocationProfiler.TrackActivity("LuaExecution", "K2Lua"))
        {
            scope.Activity.Should().NotBeNull();
            scope.Activity!.GetTagItem("code.function").Should().Be("LuaExecution");
            scope.Activity.GetTagItem("allocation.category").Should().Be("K2Lua");

            // Allocate memory
            byte[] data = new byte[64 * 1024];
            data[0] = 42;
        }

        // Assert
        capturedActivity.Should().NotBeNull();
        capturedActivity!.GetTagItem("allocation.operation").Should().Be("LuaExecution");
        capturedActivity.GetTagItem("allocation.category").Should().Be("K2Lua");

        object? bytesTag = capturedActivity.GetTagItem("allocation.bytes");
        bytesTag.Should().NotBeNull();
        Convert.ToInt64(bytesTag).Should().BeGreaterThanOrEqualTo(64 * 1024);

        object? usTag = capturedActivity.GetTagItem("allocation.microseconds");
        usTag.Should().NotBeNull();
        Convert.ToDouble(usTag).Should().BeGreaterThan(0.0);

        object? rateTag = capturedActivity.GetTagItem("allocation.rate_mb_per_sec");
        rateTag.Should().NotBeNull();
        Convert.ToDouble(rateTag).Should().BeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public void Record_DirectInvocation_SetsExpectedTagsAndCalculatesThroughput()
    {
        // Arrange
        using ActivitySource testSource = new("Kongor.Test.Profiler");
        using ActivityListener listener = new()
        {
            ShouldListenTo = s => s.Name == testSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using Activity? activity = testSource.StartActivity("TestOp");
        activity.Should().NotBeNull();

        // 1MB allocated in 1,000 microseconds (1 millisecond) => 1,000 MB/s
        const long bytes = 1024 * 1024;
        const double elapsedUs = 1000.0;

        // Act
        MicrosecondAllocationProfiler.Record(bytes, elapsedUs, "FastOp", "Benchmark");

        // Assert
        activity!.GetTagItem("allocation.bytes").Should().Be(bytes);
        activity.GetTagItem("allocation.microseconds").Should().Be(elapsedUs);
        activity.GetTagItem("allocation.operation").Should().Be("FastOp");
        activity.GetTagItem("allocation.category").Should().Be("Benchmark");

        double rate = Convert.ToDouble(activity.GetTagItem("allocation.rate_mb_per_sec"));
        rate.Should().BeApproximately(1000.0, 1.0);
    }
}
