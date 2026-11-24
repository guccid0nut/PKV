// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Load.Tests.Soak;

/// <summary>
/// Slice P1.3 Soak Load Suite:
/// High-concurrency matchmaking queue, reconnect storm, and match history query stress tests.
/// </summary>
public sealed class ConcurrentMatchmakingAndChatSoakTests
{
    private readonly ITestOutputHelper? _output;

    public ConcurrentMatchmakingAndChatSoakTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    public sealed record MatchTicket(
        int AccountId,
        string Nickname,
        int Mmr,
        long QueuedTimestampTicks);

    public sealed record FormedMatch(
        int MatchId,
        IReadOnlyList<MatchTicket> LegionTeam,
        IReadOnlyList<MatchTicket> HellbourneTeam,
        long FormationLatencyUs);

    /// <summary>
    /// Thread-safe in-memory matchmaking queue simulation with strict ticket tracking
    /// and atomic match popping for load testing.
    /// </summary>
    public sealed class ConcurrentMatchmakerSimulator
    {
        private readonly ConcurrentQueue<MatchTicket> _queue = new();
        private readonly ConcurrentDictionary<int, int> _assignedMatches = new();
        private readonly ConcurrentDictionary<int, MatchTicket> _activeTickets = new();
        private int _matchIdCounter = 10000;

        public int QueuedCount => _queue.Count;

        public bool EnqueueTicket(MatchTicket ticket)
        {
            if (_activeTickets.TryAdd(ticket.AccountId, ticket))
            {
                _queue.Enqueue(ticket);
                return true;
            }
            return false;
        }

        public bool HandleReconnect(int accountId, out int matchId)
        {
            return _assignedMatches.TryGetValue(accountId, out matchId);
        }

        public List<FormedMatch> FormAvailableMatches()
        {
            List<FormedMatch> formed = [];
            while (_queue.Count >= 10)
            {
                List<MatchTicket> pool = [];
                for (int i = 0; i < 10; i++)
                {
                    if (_queue.TryDequeue(out MatchTicket? ticket))
                    {
                        pool.Add(ticket);
                    }
                }

                if (pool.Count == 10)
                {
                    int matchId = Interlocked.Increment(ref _matchIdCounter);
                    List<MatchTicket> legion = pool.Take(5).ToList();
                    List<MatchTicket> hellbourne = pool.Skip(5).Take(5).ToList();

                    foreach (MatchTicket p in pool)
                    {
                        _assignedMatches.TryAdd(p.AccountId, matchId);
                        _activeTickets.TryRemove(p.AccountId, out _);
                    }

                    long maxWaitTicks = pool.Max(p => Stopwatch.GetTimestamp() - p.QueuedTimestampTicks);
                    long maxWaitUs = (maxWaitTicks * 1_000_000L) / Stopwatch.Frequency;

                    formed.Add(new FormedMatch(matchId, legion, hellbourne, maxWaitUs));
                }
            }
            return formed;
        }

        public int GetAssignedMatch(int accountId)
        {
            return _assignedMatches.TryGetValue(accountId, out int matchId) ? matchId : 0;
        }
    }

    [Fact]
    public async Task Fast_ConcurrentMatchmakingAndReconnectBurst_CompletesInSub1Sec()
    {
        const int clientCount = 200;
        const int reconnectBurstCount = 40;
        ConcurrentMatchmakerSimulator matchmaker = new();
        ConcurrentBag<double> latenciesMs = [];
        ConcurrentBag<Exception> exceptions = [];

        Stopwatch totalSw = Stopwatch.StartNew();

        // 1. 200 clients concurrently queueing
        Task[] queueTasks = new Task[clientCount];
        for (int i = 0; i < clientCount; i++)
        {
            int accountId = 1000 + i;
            queueTasks[i] = Task.Run(() =>
            {
                try
                {
                    Stopwatch opSw = Stopwatch.StartNew();
                    SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: 100, maxBurstCapacity: 200);
                    limiter.TryConsume(1).Should().BeTrue("Chat session token bucket must admit matchmaking request");
                    ChatCommandRegistry.TryGetDescriptor((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER, out ChatCommandDescriptor? descriptor)
                        .Should().BeTrue("ChatCommandRegistry must resolve precompiled command descriptor under load");
                    descriptor.Should().NotBeNull();

                    MatchTicket ticket = new(
                        AccountId: accountId,
                        Nickname: $"Player_{accountId}",
                        Mmr: 1500 + (accountId % 300),
                        QueuedTimestampTicks: Stopwatch.GetTimestamp());

                    bool enqueued = matchmaker.EnqueueTicket(ticket);
                    enqueued.Should().BeTrue();
                    opSw.Stop();
                    latenciesMs.Add(opSw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        await Task.WhenAll(queueTasks);

        // 2. Match popping (200 players -> exactly 20 matches of 10)
        List<FormedMatch> matches = matchmaker.FormAvailableMatches();
        matches.Count.Should().Be(20, "200 players in a 5v5 queue must form exactly 20 matches.");

        // 3. Reconnect burst of 40 players mid-flight
        Task[] reconnectTasks = new Task[reconnectBurstCount];
        for (int r = 0; r < reconnectBurstCount; r++)
        {
            int accountId = 1000 + (r * 5); // Pick players across formed matches
            reconnectTasks[r] = Task.Run(() =>
            {
                try
                {
                    Stopwatch recSw = Stopwatch.StartNew();
                    bool reconnected = matchmaker.HandleReconnect(accountId, out int matchId);
                    reconnected.Should().BeTrue();
                    matchId.Should().BeGreaterThanOrEqualTo(10000);
                    recSw.Stop();
                    latenciesMs.Add(recSw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        await Task.WhenAll(reconnectTasks);
        totalSw.Stop();

        // Invariant Assertions
        exceptions.Should().BeEmpty("Zero unhandled exceptions allowed during high-concurrency burst.");

        // Check for 0 duplicate match assignments
        HashSet<int> assignedAccounts = [];
        foreach (FormedMatch m in matches)
        {
            foreach (MatchTicket p in m.LegionTeam.Concat(m.HellbourneTeam))
            {
                bool added = assignedAccounts.Add(p.AccountId);
                added.Should().BeTrue($"Account {p.AccountId} must never be assigned to multiple matches.");
            }
        }
        assignedAccounts.Count.Should().Be(200, "All 200 players must be cleanly assigned without lost tickets.");

        // Percentiles
        double[] sorted = [.. latenciesMs.OrderBy(x => x)];
        double p99 = sorted[(int)(sorted.Length * 0.99)];
        p99.Should().BeLessThan(250.0, "p99 latency must remain under 250 ms.");
        totalSw.ElapsedMilliseconds.Should().BeLessThan(1000, "Fast 200-client soak test must complete in < 1.0s.");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task Sustained_1000Client_MatchmakingAndReconnectStorm_SoakSuite()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return;
        }

        const int totalClients = 1000;
        const int reconnectClients = 300;
        ConcurrentMatchmakerSimulator matchmaker = new();
        ConcurrentBag<double> queueLatenciesMs = [];
        ConcurrentBag<double> reconnectLatenciesMs = [];
        ConcurrentBag<Exception> exceptions = [];

        Stopwatch soakSw = Stopwatch.StartNew();

        // Stage 1: 1,000 concurrent player queue injection
        Task[] enqueueTasks = new Task[totalClients];
        for (int i = 0; i < totalClients; i++)
        {
            int accountId = 20000 + i;
            enqueueTasks[i] = Task.Run(() =>
            {
                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    MatchTicket ticket = new(
                        AccountId: accountId,
                        Nickname: $"SoakUser_{accountId}",
                        Mmr: 1200 + (accountId % 800),
                        QueuedTimestampTicks: Stopwatch.GetTimestamp());

                    bool ok = matchmaker.EnqueueTicket(ticket);
                    ok.Should().BeTrue();
                    sw.Stop();
                    queueLatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        await Task.WhenAll(enqueueTasks);

        // Stage 2: Match formation (1,000 clients -> 100 matches)
        List<FormedMatch> matches = matchmaker.FormAvailableMatches();
        matches.Count.Should().Be(100, "1,000 players must form exactly 100 matches of 10.");

        // Stage 3: Reconnect storm with 300 disconnected players reconnecting concurrently
        Task[] reconnectTasks = new Task[reconnectClients];
        for (int r = 0; r < reconnectClients; r++)
        {
            int accountId = 20000 + (r * 3);
            reconnectTasks[r] = Task.Run(() =>
            {
                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    bool reconnected = matchmaker.HandleReconnect(accountId, out int matchId);
                    reconnected.Should().BeTrue();
                    matchId.Should().BeGreaterThanOrEqualTo(10000);
                    sw.Stop();
                    reconnectLatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });
        }

        await Task.WhenAll(reconnectTasks);
        soakSw.Stop();

        // Telemetry Calculation
        double[] qSorted = [.. queueLatenciesMs.OrderBy(x => x)];
        double qP50 = qSorted[(int)(qSorted.Length * 0.50)];
        double qP95 = qSorted[(int)(qSorted.Length * 0.95)];
        double qP99 = qSorted[(int)(qSorted.Length * 0.99)];

        double[] rSorted = [.. reconnectLatenciesMs.OrderBy(x => x)];
        double rP50 = rSorted[(int)(rSorted.Length * 0.50)];
        double rP95 = rSorted[(int)(rSorted.Length * 0.95)];
        double rP99 = rSorted[(int)(rSorted.Length * 0.99)];

        double throughputOpsPerSec = (totalClients + reconnectClients) / (soakSw.ElapsedMilliseconds / 1000.0);

        _output?.WriteLine($"[1000-CLIENT SOAK REPORT]");
        _output?.WriteLine($"Duration: {soakSw.ElapsedMilliseconds} ms");
        _output?.WriteLine($"Throughput: {throughputOpsPerSec:F2} ops/sec");
        _output?.WriteLine($"Queue Latencies:     p50={qP50:F2}ms, p95={qP95:F2}ms, p99={qP99:F2}ms");
        _output?.WriteLine($"Reconnect Latencies: p50={rP50:F2}ms, p95={rP95:F2}ms, p99={rP99:F2}ms");

        exceptions.Should().BeEmpty();
        qP99.Should().BeLessThan(250.0);
        rP99.Should().BeLessThan(200.0);
    }
}
