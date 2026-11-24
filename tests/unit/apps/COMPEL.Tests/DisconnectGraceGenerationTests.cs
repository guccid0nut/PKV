// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Reflection;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Regression tests for the all-clients-disconnected grace timer in <see cref="MatchServerManagerSupervisor" />.
///     Bug: every zero-client event spawned an independent Task.Delay timer, so a
///     disconnect -> reconnect -> disconnect sequence let the FIRST (stale) timer recycle the worker early.
///     Fix: <see cref="DedicatedServerWorker.DisconnectGraceGeneration" /> — only the newest timer may act.
/// </summary>
public sealed class DisconnectGraceGenerationTests
{
    private static readonly MethodInfo DispatchStreamLine = typeof(MatchServerManagerSupervisor)
        .GetMethod("DispatchStreamLine", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Feed(MatchServerManagerSupervisor supervisor, DedicatedServerWorker worker, string line) =>
        DispatchStreamLine.Invoke(supervisor, [worker, new DedicatedServerLogEntry(worker.Port, line, false)]);

    [Fact]
    public async Task StaleGraceTimer_IsSuperseded_WhenClientReconnectsAndDisconnectsAgain()
    {
        CapturingLogger<MatchServerManagerSupervisor> logger = new();
        MatchServerManagerOptions options = new()
        {
            DisconnectGraceSecondsPreMatch = 1,
            DisconnectGraceSecondsInMatch = 1
        };
        MatchServerManagerSupervisor supervisor = new(Options.Create(options), logger);
        DedicatedServerWorker worker = new(1, 11235, 11435, 11235, 11435, "KONGOR Dedicated #1");

        DispatchStreamLine.Should().NotBeNull("the test drives the supervisor's stdout dispatcher via reflection");

        // Connect + disconnect -> timer A armed (generation A).
        Feed(supervisor, worker, "AddClient: client num: 0");
        Feed(supervisor, worker, "Client #0 disconnected: test");
        long generationA = Interlocked.Read(ref worker.DisconnectGraceGeneration);

        // Reconnect + disconnect -> timer B armed; it supersedes A.
        Feed(supervisor, worker, "AddClient: client num: 0");
        Feed(supervisor, worker, "Client #0 disconnected: test");
        long generationB = Interlocked.Read(ref worker.DisconnectGraceGeneration);
        generationB.Should().BeGreaterThan(generationA);

        // Timers only arm while DesiredRunning=true, so stopping now cannot disarm them — but it guarantees timer B can
        // never trigger a real recycle/process launch. No timing window: the supervisor checks the generation BEFORE
        // DesiredRunning, so A must report "superseded" and B (current generation) must report "Aborting".
        worker.DesiredRunning = false;

        (await WaitUntilAsync(() => logger.Messages.Any(m => m.Contains("Grace timer superseded")), TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the stale timer from the first disconnect must stand down instead of recycling the worker");
        (await WaitUntilAsync(() => logger.Messages.Any(m => m.Contains("Aborting worker auto-recycle")), TimeSpan.FromSeconds(15)))
            .Should().BeTrue("timer B owned the current generation and evaluated the (stopped) worker");

        logger.Messages.Count(m => m.Contains("Grace timer superseded")).Should().Be(1);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                return false;
            await Task.Delay(50);
        }
        return true;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception));
    }
}
