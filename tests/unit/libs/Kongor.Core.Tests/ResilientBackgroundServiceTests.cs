// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kongor.Core.Tests;

public class ResilientBackgroundServiceTests
{
    private sealed class TestResilientWorker : ResilientBackgroundService
    {
        private readonly Func<CancellationToken, Task> _work;

        public int InvocationCount { get; private set; }

        public TestResilientWorker(
            Func<CancellationToken, Task> work,
            string serviceName = "TestWorker",
            TimeSpan? initialBackoff = null,
            TimeSpan? maxBackoff = null,
            ILogger? logger = null)
            : base(logger ?? NullLogger.Instance, serviceName, initialBackoff ?? TimeSpan.FromMilliseconds(5), maxBackoff ?? TimeSpan.FromMilliseconds(10))
        {
            _work = work;
        }

        protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
        {
            InvocationCount++;
            await _work(stoppingToken);
        }

        public void CallResetFailureCounter() => ResetFailureCounter();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTransientExceptionThrown_RetriesWithBackoffAndRecovers()
    {
        // Arrange
        string workerName = $"TransientWorker-{Guid.NewGuid():N}";
        int attempts = 0;
        using TestResilientWorker worker = new(async ct =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("Simulated transient socket glitch");
            }
            await Task.Yield();
        }, workerName);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        // Act
        await worker.StartAsync(cts.Token);
        for (int i = 0; i < 200 && Volatile.Read(ref attempts) < 2; i++)
        {
            await Task.Delay(25);
        }

        // Assert
        attempts.Should().Be(2);
        worker.TotalUnhandledExceptions.Should().Be(1);
        worker.ConsecutiveFailures.Should().Be(1);
        worker.IsDegraded.Should().BeFalse();
        worker.LastExceptionMessage.Should().Be("Simulated transient socket glitch");
    }

    [Fact]
    public async Task ExecuteAsync_WhenThreeConsecutiveExceptions_MarksDegraded()
    {
        // Arrange
        string workerName = $"DegradedWorker-{Guid.NewGuid():N}";
        int attempts = 0;
        using CancellationTokenSource cts = new();

        using TestResilientWorker worker = new(ct =>
        {
            attempts++;
            if (attempts >= 3)
            {
                cts.Cancel(); // stop after reaching 3 consecutive failures
            }
            throw new InvalidOperationException($"Simulated fault #{attempts}");
        }, workerName);

        // Act
        try
        {
            await worker.StartAsync(cts.Token);
            for (int i = 0; i < 200 && (!worker.IsDegraded || worker.ConsecutiveFailures < 3); i++)
            {
                await Task.Delay(25);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        // Assert
        worker.ConsecutiveFailures.Should().BeGreaterOrEqualTo(3);
        worker.IsDegraded.Should().BeTrue();
        ResilientBackgroundServiceRegistry.HasDegradedWorkers(out IReadOnlyList<ResilientBackgroundService> degraded).Should().BeTrue();
        degraded.Should().Contain(w => w.ServiceName == workerName);
    }

    [Fact]
    public async Task ResetFailureCounter_ClearsDegradedState()
    {
        // Arrange
        string workerName = $"ResettableWorker-{Guid.NewGuid():N}";
        int attempts = 0;
        using CancellationTokenSource cts = new();
        using TestResilientWorker degradedWorker = new(_ =>
        {
            attempts++;
            if (attempts >= 3) cts.Cancel();
            throw new InvalidOperationException("fault");
        }, workerName);

        try
        {
            await degradedWorker.StartAsync(cts.Token);
            for (int i = 0; i < 60 && !degradedWorker.IsDegraded; i++)
            {
                await Task.Delay(50);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert degraded
        degradedWorker.IsDegraded.Should().BeTrue();

        // Act: reset
        degradedWorker.CallResetFailureCounter();

        // Assert recovered
        degradedWorker.ConsecutiveFailures.Should().Be(0);
        degradedWorker.IsDegraded.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOneShotStartupWorker_CompletesOnceWithoutRestarting()
    {
        // Arrange
        string workerName = $"OneShotWorker-{Guid.NewGuid():N}";
        int runCount = 0;
        using TestResilientWorker worker = new(ct =>
        {
            runCount++;
            return Task.CompletedTask;
        }, workerName);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(50);

        // Assert
        runCount.Should().Be(1);
        worker.ConsecutiveFailures.Should().Be(0);
        worker.TotalUnhandledExceptions.Should().Be(0);
        worker.IsDegraded.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationTokenTriggered_ExitsCleanly()
    {
        // Arrange
        string workerName = $"CancelledWorker-{Guid.NewGuid():N}";
        using CancellationTokenSource cts = new();
        cts.Cancel(); // pre-cancelled

        int runCount = 0;
        using TestResilientWorker worker = new(async ct =>
        {
            runCount++;
            await Task.Delay(1000, ct);
        }, workerName);

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(50);

        // Assert
        worker.ConsecutiveFailures.Should().Be(0);
        worker.TotalUnhandledExceptions.Should().Be(0);
        worker.IsDegraded.Should().BeFalse();
    }

    [Fact]
    public void Registry_UnregistersOnDispose()
    {
        // Arrange
        string workerName = $"DisposableWorker-{Guid.NewGuid():N}";

        // Act
        TestResilientWorker worker = new(_ => Task.CompletedTask, workerName);
        ResilientBackgroundServiceRegistry.AllWorkers.Should().Contain(w => w.ServiceName == workerName);

        worker.Dispose();

        // Assert
        ResilientBackgroundServiceRegistry.AllWorkers.Should().NotContain(w => w.ServiceName == workerName);
    }
}
