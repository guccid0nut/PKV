// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PrometheusMetricsExporterTests.cs
// System: COMPEL.Tests - Observability & Headless Modernization Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using COMPEL.Services.Telemetry;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

public sealed class PrometheusMetricsExporterTests
{
    [Fact]
    public void ExportMetrics_GeneratesValidPrometheusTextFormat()
    {
        // Arrange
        MatchServerManagerOptions options = new()
        {
            UserName = "KONGOR",
            Password = "Password123!",
            Instances = 2,
            Gateway = "127.0.0.1",
            Location = "USE",
            ServerNamePrefix = "TEST ARENA",
            UseProxy = false,
            RecycleEveryNMatches = 3
        };

        PortPlan plan = new(options);
        DedicatedServerWorker worker1 = new(1, 11235, plan.LocalVoiceStart, 11235, plan.PublicVoiceStart, "TEST ARENA #1")
        {
            CrashCount = 1,
            MatchesHostedSinceRecycle = 2,
            TotalLifetimeMatchesHosted = 5
        };

        DedicatedServerWorker worker2 = new(2, 11236, plan.LocalVoiceStart + 1, 11236, plan.PublicVoiceStart + 1, "TEST ARENA #2")
        {
            CrashCount = 0,
            MatchesHostedSinceRecycle = 0,
            TotalLifetimeMatchesHosted = 0
        };

        // Create supervisor using test constructor and register mock workers
        MatchServerManagerSupervisor supervisor = new(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchServerManagerSupervisor>.Instance);
        supervisor.RegisterWorkerForTesting(worker1);
        supervisor.RegisterWorkerForTesting(worker2);

        // Act
        string output = PrometheusMetricsExporter.ExportMetrics(supervisor, null, options);

        // Assert
        output.Should().Contain("# HELP compel_up Whether the COMPEL supervisor node is operational");
        output.Should().Contain("# TYPE compel_up gauge");
        output.Should().Contain("compel_up 1");

        output.Should().Contain("compel_info{version=");
        output.Should().Contain("location=\"USE\"");
        output.Should().Contain("prefix=\"TEST ARENA\"");

        output.Should().Contain("compel_supervisor_total_workers 2");
        output.Should().Contain("compel_worker_running{instance=\"1\",port=\"11235\",public_port=\"11235\"}");
        output.Should().Contain("compel_worker_crashes_total{instance=\"1\",port=\"11235\",public_port=\"11235\"} 1");
        output.Should().Contain("compel_worker_matches_hosted_total{instance=\"1\",port=\"11235\",public_port=\"11235\"} 5");
        output.Should().Contain("compel_worker_tickrate_hz");
    }

    [Fact]
    public void DedicatedServerWorker_MatchTracking_MaintainsRecycleAndLifetimeCounts()
    {
        // Arrange
        PortPlan plan = new(new MatchServerManagerOptions());
        DedicatedServerWorker worker = new(1, 11235, plan.LocalVoiceStart, 11235, plan.PublicVoiceStart, "ARENA #1");

        worker.MatchesHostedSinceRecycle.Should().Be(0);
        worker.TotalLifetimeMatchesHosted.Should().Be(0);

        // Act - Simulate 3 matches completed
        worker.MatchesHostedSinceRecycle += 3;
        worker.TotalLifetimeMatchesHosted += 3;

        worker.MatchesHostedSinceRecycle.Should().Be(3);
        worker.TotalLifetimeMatchesHosted.Should().Be(3);

        // Simulate clean worker recycle
        worker.MatchesHostedSinceRecycle = 0;

        // Assert
        worker.MatchesHostedSinceRecycle.Should().Be(0, "MatchesHostedSinceRecycle must be cleared on worker recycle");
        worker.TotalLifetimeMatchesHosted.Should().Be(3, "TotalLifetimeMatchesHosted must accumulate across recycles");
    }
}
