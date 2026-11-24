// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FleetDrainingServiceTests.cs
// System: COMPEL Tests - Zero-Downtime Rolling Fleet Draining Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Text;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class FleetDrainingServiceTests
{
    private static DedicatedServerWorker CreateMockWorker(int index, int port, string state = "Available")
    {
        DedicatedServerWorker worker = new(index, port, port + 200, port + 10000, port + 10200, $"MockServer_{port}")
        {
            IsRunningOverride = true,
            CurrentState = state
        };

        return worker;
    }

    [Fact]
    public async Task MarkWorkerDraining_WhenIdle_ImmediatelyTriggersCleanExitAndResurrect()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "Available");
        using MemoryStream ms = new();
        using StreamWriter stdinWriter = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = stdinWriter;

        bool resurrected = false;
        FleetDrainingService service = new(
            workerResolver: port => port == 11235 ? worker : null,
            workerResurrector: async w =>
            {
                resurrected = true;
                w.IsDraining = false;
                w.CurrentState = "Available";
                return await Task.FromResult(true);
            },
            logger: NullLogger.Instance);

        // Act
        bool marked = service.MarkWorkerDraining(11235, "Memory Maintenance");

        // Assert
        marked.Should().BeTrue();
        worker.IsDraining.Should().BeFalse("worker was idle, so draining completed immediately");
        resurrected.Should().BeTrue();

        string written = Encoding.UTF8.GetString(ms.ToArray());
        written.Should().Contain("quit", "stdin should have received quit command for clean exit");
    }

    [Fact]
    public async Task MarkWorkerDrainingAsync_WhenIdle_ImmediatelyTriggersCleanExitAndResurrect()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "Available");
        using MemoryStream ms = new();
        using StreamWriter stdinWriter = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = stdinWriter;

        bool resurrected = false;
        FleetDrainingService service = new(
            workerResolver: port => port == 11235 ? worker : null,
            workerResurrector: async w =>
            {
                resurrected = true;
                w.IsDraining = false;
                w.CurrentState = "Available";
                return await Task.FromResult(true);
            },
            logger: NullLogger.Instance);

        // Act
        bool marked = await service.MarkWorkerDrainingAsync(11235, "Memory Maintenance");

        // Assert
        marked.Should().BeTrue();
        worker.IsDraining.Should().BeFalse();
        resurrected.Should().BeTrue();

        string written = Encoding.UTF8.GetString(ms.ToArray());
        written.Should().Contain("quit");
    }

    [Fact]
    public async Task StartAsync_And_StopAsync_ExecuteGracefully()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "Available");
        FleetDrainingService service = new(
            workerResolver: port => worker,
            workerResurrector: async _ => await Task.FromResult(true),
            logger: NullLogger.Instance);

        // Act & Assert
        Func<Task> actStart = async () => await service.StartAsync(CancellationToken.None);
        Func<Task> actStop = async () => await service.StopAsync(CancellationToken.None);

        await actStart.Should().NotThrowAsync();
        await actStop.Should().NotThrowAsync();
    }

    [Fact]
    public void MarkWorkerDraining_WhenInMatch_PreservesMatchAndReportsDraining()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "InMatch");
        using MemoryStream ms = new();
        using StreamWriter stdinWriter = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = stdinWriter;

        bool resurrected = false;
        FleetDrainingService service = new(
            workerResolver: port => port == 11235 ? worker : null,
            workerResurrector: async _ =>
            {
                resurrected = true;
                return await Task.FromResult(true);
            },
            logger: NullLogger.Instance);

        // Act
        bool marked = service.MarkWorkerDraining(11235, "Deploying Balance Patch");

        // Assert
        marked.Should().BeTrue();
        worker.IsDraining.Should().BeTrue("worker is in an active match, so draining is deferred");
        worker.CurrentState.Should().Be("Draining");
        worker.CanAcceptNewMatches.Should().BeFalse();
        resurrected.Should().BeFalse("in-match worker must NOT be aborted while match is playing");

        string written = Encoding.UTF8.GetString(ms.ToArray());
        written.Should().BeEmpty("quit command must NOT be sent while match is active");
    }

    [Fact]
    public async Task OnMatchEndedAsync_WhenDraining_TriggersCleanRetirementAndResurrect()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "InMatch");
        using MemoryStream ms = new();
        using StreamWriter stdinWriter = new(ms, Encoding.UTF8, leaveOpen: true);
        worker.StdinWriter = stdinWriter;

        bool resurrected = false;
        FleetDrainingService service = new(
            workerResolver: port => port == 11235 ? worker : null,
            workerResurrector: async w =>
            {
                resurrected = true;
                w.IsDraining = false;
                w.CurrentState = "Available";
                return await Task.FromResult(true);
            },
            logger: NullLogger.Instance);

        service.MarkWorkerDraining(11235, "Maintenance");

        // Act - Match concludes ("GS_SLEEP" / "Match ended")
        bool handled = await service.OnMatchEndedAsync(worker);

        // Assert
        handled.Should().BeTrue();
        resurrected.Should().BeTrue();
        worker.IsDraining.Should().BeFalse();
        worker.CurrentState.Should().Be("Available");

        string written = Encoding.UTF8.GetString(ms.ToArray());
        written.Should().Contain("quit");
    }

    [Fact]
    public void CanAcceptNewMatches_WhenDraining_ReturnsFalse()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "Available");
        worker.CanAcceptNewMatches.Should().BeTrue();

        // Act
        worker.IsDraining = true;

        // Assert
        worker.CanAcceptNewMatches.Should().BeFalse();
    }

    [Fact]
    public void CancelDraining_RestoresAvailableState()
    {
        // Arrange
        DedicatedServerWorker worker = CreateMockWorker(1, 11235, "InMatch");
        FleetDrainingService service = new(
            workerResolver: port => port == 11235 ? worker : null,
            workerResurrector: async _ => await Task.FromResult(true),
            logger: NullLogger.Instance);

        service.MarkWorkerDraining(11235, "Mistake");
        worker.IsDraining.Should().BeTrue();
        worker.CurrentState.Should().Be("Draining");

        // Act
        bool cancelled = service.CancelDraining(11235);

        // Assert
        cancelled.Should().BeTrue();
        worker.IsDraining.Should().BeFalse();
        worker.CurrentState.Should().Be("InMatch");
        worker.DrainReason.Should().BeNull();
    }

    [Fact]
    public async Task RollingFleetUpgradeAsync_RespectsMaxConcurrentLimit()
    {
        // Arrange: 4 workers
        DedicatedServerWorker w1 = CreateMockWorker(1, 11235, "Available");
        DedicatedServerWorker w2 = CreateMockWorker(2, 11236, "Available");
        DedicatedServerWorker w3 = CreateMockWorker(3, 11237, "Available");
        DedicatedServerWorker w4 = CreateMockWorker(4, 11238, "Available");
        DedicatedServerWorker[] fleet = [w1, w2, w3, w4];

        int currentConcurrentDraining = 0;
        int peakConcurrentDraining = 0;
        Lock syncLock = new();

        FleetDrainingService service = new(
            workerResolver: port => fleet.FirstOrDefault(w => w.Port == port),
            workerResurrector: async w =>
            {
                lock (syncLock)
                {
                    currentConcurrentDraining++;
                    if (currentConcurrentDraining > peakConcurrentDraining)
                        peakConcurrentDraining = currentConcurrentDraining;
                }

                await Task.Delay(20); // Simulate restart latency

                lock (syncLock)
                {
                    currentConcurrentDraining--;
                }

                w.IsDraining = false;
                w.CurrentState = "Available";
                return true;
            },
            logger: NullLogger.Instance);

        // Act: Rolling upgrade with max concurrency 2
        RollingUpgradeOptions options = new(
            MaxConcurrentDraining: 2,
            PerWorkerTimeout: TimeSpan.FromSeconds(5),
            UpgradeReason: "Test Rolling Upgrade");

        RollingFleetUpgradeResult result = await service.RollingFleetUpgradeAsync(fleet, options);

        // Assert
        result.IsComplete.Should().BeTrue();
        result.TotalWorkers.Should().Be(4);
        result.UpgradedWorkers.Should().Be(4);
        result.FailedWorkers.Should().Be(0);

        peakConcurrentDraining.Should().BeLessOrEqualTo(2, "rolling upgrade must never exceed MaxConcurrentDraining");
    }
}
