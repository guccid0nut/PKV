// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ValkeyMatchmakingAndPopulationLoadTests.cs
// System: ASPIRE.Load.Tests - High-Concurrency Population & Matchmaking Proof
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Services.Matchmaking;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using KONGOR.MasterServer.Features.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Telemetry;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace ASPIRE.Load.Tests;

public sealed class ValkeyMatchmakingAndPopulationLoadTests(ITestOutputHelper output)
{
    private static double ComputePercentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            return 0.0;
        }

        double rank = (percentile / 100.0) * (sortedValues.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi)
        {
            return Math.Round(sortedValues[lo], 3);
        }

        double weight = rank - lo;
        return Math.Round((sortedValues[lo] * (1.0 - weight)) + (sortedValues[hi] * weight), 3);
    }

    [Fact]
    public async Task FiveThousandConcurrentPopulationRequests_SingleFlightHybridCache_LimitsUpstreamReadsAndStaysSub25MsP95()
    {
        // protects: /api/telemetry/population single-flight stampede resistance under 5,000 concurrent callers.
        // fails_when: Cache stampede fans out thousands of Redis HashGetAsync calls or p95 latency breaches 25ms.
        // seam: Real ServiceCollection.AddNexusHybridCache (FusionCache/HybridCache) + real GetPopulationBreakdownHandler.

        const int totalCallers = 5000;
        int upstreamRedisCalls = 0;

        PopulationBreakdownResponseDto liveSnapshot = new()
        {
            OnlinePlayers = 4200,
            GamesInProgressTotal = 385,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Status = PopulationTelemetryStatus.Live
        };
        string snapshotJson = JsonSerializer.Serialize(liveSnapshot);

        Mock<IDatabase> redisDbMock = new();
        redisDbMock
            .Setup(db => db.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU",
                It.IsAny<CommandFlags>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref upstreamRedisCalls);
                await Task.Delay(2);
                return (RedisValue)"4200";
            });
        redisDbMock
            .Setup(db => db.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField,
                It.IsAny<CommandFlags>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref upstreamRedisCalls);
                await Task.Delay(2);
                return (RedisValue)snapshotJson;
            });

        ServiceCollection services = new();
        services.AddLogging();
        services.AddNexusHybridCache();
        await using ServiceProvider sp = services.BuildServiceProvider();
        INexusHybridCache hybridCache = sp.GetRequiredService<INexusHybridCache>();

        GetPopulationBreakdownHandler handler = new(
            distributedCache: redisDbMock.Object,
            logger: NullLogger<GetPopulationBreakdownHandler>.Instance,
            hybridCache: hybridCache);

        GetPopulationBreakdownQuery query = new();
        ConcurrentBag<double> callerLatenciesMs = [];
        TaskCompletionSource startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Wave 1 (Cold Stampede): 5,000 simultaneous callers hitting an empty cache collapse into 1 single-flight refresh
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        Stopwatch coldSw = Stopwatch.StartNew();
        Task<PopulationBreakdownResponseDto>[] coldTasks = new Task<PopulationBreakdownResponseDto>[totalCallers];
        for (int i = 0; i < totalCallers; i++)
        {
            coldTasks[i] = Task.Run(async () =>
            {
                await startGate.Task;
                long t0 = Stopwatch.GetTimestamp();
                PopulationBreakdownResponseDto res = await handler.Handle(query, CancellationToken.None);
                double elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                callerLatenciesMs.Add(elapsedMs);
                return res;
            });
        }

        startGate.SetResult();
        PopulationBreakdownResponseDto[] coldResults = await Task.WhenAll(coldTasks);
        coldSw.Stop();

        int coldUpstreamCalls = Volatile.Read(ref upstreamRedisCalls);
        double[] coldSortedMs = callerLatenciesMs.OrderBy(static x => x).ToArray();
        double coldP50 = ComputePercentile(coldSortedMs, 50);
        double coldP95 = ComputePercentile(coldSortedMs, 95);
        double coldP99 = ComputePercentile(coldSortedMs, 99);
        double coldRps = Math.Round(totalCallers / Math.Max(0.001, coldSw.Elapsed.TotalSeconds), 1);

        // Wave 2 (Warm L1 Steady-State Burst): 5,000 simultaneous callers served directly from L1 (< 25ms p95, 0 extra Redis reads)
        ConcurrentBag<double> warmLatenciesMs = [];
        TaskCompletionSource warmGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch warmSw = Stopwatch.StartNew();
        Task<PopulationBreakdownResponseDto>[] warmTasks = new Task<PopulationBreakdownResponseDto>[totalCallers];
        for (int i = 0; i < totalCallers; i++)
        {
            warmTasks[i] = Task.Run(async () =>
            {
                await warmGate.Task;
                long t0 = Stopwatch.GetTimestamp();
                PopulationBreakdownResponseDto res = await handler.Handle(query, CancellationToken.None);
                warmLatenciesMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                return res;
            });
        }

        warmGate.SetResult();
        PopulationBreakdownResponseDto[] warmResults = await Task.WhenAll(warmTasks);
        warmSw.Stop();

        int totalUpstreamCalls = Volatile.Read(ref upstreamRedisCalls);
        int gen0Delta = GC.CollectionCount(0) - gen0Before;
        int gen1Delta = GC.CollectionCount(1) - gen1Before;
        int gen2Delta = GC.CollectionCount(2) - gen2Before;

        double[] warmSortedMs = warmLatenciesMs.OrderBy(static x => x).ToArray();
        double warmP50 = ComputePercentile(warmSortedMs, 50);
        double warmP95 = ComputePercentile(warmSortedMs, 95);
        double warmP99 = ComputePercentile(warmSortedMs, 99);
        double warmRps = Math.Round(totalCallers / Math.Max(0.001, warmSw.Elapsed.TotalSeconds), 1);

        output.WriteLine(
            $"[LOAD-PROOF][Population5k-Cold] callers={totalCallers} | upstreamRedisHashGets={coldUpstreamCalls} (ideal=2) | " +
            $"wallMs={coldSw.ElapsedMilliseconds} | throughput={coldRps} req/s | p50={coldP50}ms p95={coldP95}ms p99={coldP99}ms");
        output.WriteLine(
            $"[LOAD-PROOF][Population5k-Warm] callers={totalCallers} | deltaUpstreamRedisHashGets={totalUpstreamCalls - coldUpstreamCalls} (ideal=0) | " +
            $"wallMs={warmSw.ElapsedMilliseconds} | throughput={warmRps} req/s | p50={warmP50}ms p95={warmP95}ms p99={warmP99}ms | " +
            $"GC(0/1/2)={gen0Delta}/{gen1Delta}/{gen2Delta}");

        coldResults.Should().HaveCount(totalCallers);
        coldResults.Should().OnlyContain(static r => r != null && r.OnlinePlayers == 4200 && r.Status == PopulationTelemetryStatus.Live);
        warmResults.Should().HaveCount(totalCallers);
        warmResults.Should().OnlyContain(static r => r != null && r.OnlinePlayers == 4200 && r.Status == PopulationTelemetryStatus.Live);
        // Single-flight guarantees 1 refresh (2 Redis HashGet fields: CCU + PopulationBreakdownJson), never 10,000+
        coldUpstreamCalls.Should().BeLessThanOrEqualTo(4, "Single-flight NexusHybridCache must collapse 5,000 concurrent callers into <= 2 refreshes");
        totalUpstreamCalls.Should().Be(coldUpstreamCalls, "Warm 5s L1 cache burst must trigger 0 additional upstream Redis calls");
        coldSw.ElapsedMilliseconds.Should().BeLessThan(1000, "Cold 5,000-caller thundering herd must clear within 1 second");
        warmP95.Should().BeLessThan(25.0, "Warm L1 5,000-caller burst must sustain < 25ms p95 tail latency");
    }

    [Fact]
    public async Task TwoThousandFiveHundredPlayerMatchmakingStorm_MultiPodReadyCheckAndFailover_ZeroDuplicatesOrLeaks()
    {
        // protects: 2,500-CCU (250 concurrent 10-player matches) multi-pod matchmaking state store correctness and throughput.
        // fails_when: Any ready check fires quorum != 1 time, duplicate votes corrupt acceptance counts, or racing failover pods double-confirm a match.
        // seam: Real ValkeyMatchmakingStateStore across 250 concurrent ready checks + 3,000 votes + 750 racing failover transitions.

        const int matchCount = 250;
        const int playersPerMatch = 10;
        const int totalPlayers = matchCount * playersPerMatch;

        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "load-pod-coordinator");

        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Stopwatch totalSw = Stopwatch.StartNew();

        // Phase 1: 250 concurrent distributed ID allocations & ready-check registrations (2,500 players)
        Task<int>[] createTasks = new Task<int>[matchCount];
        for (int m = 0; m < matchCount; m++)
        {
            int matchIdx = m;
            createTasks[m] = Task.Run(async () =>
            {
                int readyCheckId = await store.AllocateNextReadyCheckIdAsync();
                int[] requiredPlayers = Enumerable.Range((matchIdx * 100) + 1, playersPerMatch).ToArray();
                bool created = await store.TryCreateReadyCheckAsync(
                    new DistributedReadyCheckSnapshot(
                        ReadyCheckId: readyCheckId,
                        GroupGuids: [$"g-{matchIdx}-legion", $"g-{matchIdx}-hellbourne"],
                        RequiredAccountIds: requiredPlayers,
                        AcceptedAccountIds: [],
                        DeclinedByAccountId: null,
                        Status: "OPEN",
                        OwnerPodId: $"chat-pod-{matchIdx % 4}",
                        CreatedAtUtc: now,
                        ExpiresAtUtc: now.AddSeconds(30)),
                    TimeSpan.FromMinutes(2));
                created.Should().BeTrue();
                return readyCheckId;
            });
        }

        int[] readyCheckIds = await Task.WhenAll(createTasks);
        readyCheckIds.Distinct().Should().HaveCount(matchCount, "Allocated distributed ready-check IDs must be globally unique");

        // Phase 2: 2,500 unique player accept votes + 500 concurrent duplicate vote retries (3,000 total votes)
        ConcurrentBag<double> voteLatenciesMs = [];
        List<Task<DistributedReadyCheckVoteResult>> voteTasks = new(totalPlayers + (matchCount * 2));
        for (int m = 0; m < matchCount; m++)
        {
            int matchIdx = m;
            int rcId = readyCheckIds[m];
            for (int p = 0; p < playersPerMatch; p++)
            {
                int accountId = (matchIdx * 100) + 1 + p;
                voteTasks.Add(Task.Run(async () =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    DistributedReadyCheckVoteResult r = await store.TrySubmitReadyCheckVoteAsync(rcId, accountId, accepted: true);
                    voteLatenciesMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    return r;
                }));
            }

            // 2 duplicate retries per match under contention
            int dupBase = (matchIdx * 100) + 1;
            voteTasks.Add(Task.Run(() => store.TrySubmitReadyCheckVoteAsync(rcId, dupBase, accepted: true)));
            voteTasks.Add(Task.Run(() => store.TrySubmitReadyCheckVoteAsync(rcId, dupBase + 1, accepted: true)));
        }

        DistributedReadyCheckVoteResult[] voteResults = await Task.WhenAll(voteTasks);

        // Phase 3: 250 pending matches racing confirm + duplicate confirm + expire across failover pods (750 operations)
        int[] matchUpIds = new int[matchCount];
        for (int m = 0; m < matchCount; m++)
        {
            int matchUpId = await store.AllocateNextMatchUpIdAsync();
            matchUpIds[m] = matchUpId;
            bool registered = await store.TryRegisterPendingMatchAsync(
                new DistributedPendingMatchSnapshot(
                    MatchUpId: matchUpId,
                    AllocatedServerId: 1000 + m,
                    ResolvedMapName: "caldavar",
                    GroupGuids: [$"g-{m}-legion", $"g-{m}-hellbourne"],
                    MemberAccountIds: Enumerable.Range((m * 100) + 1, playersPerMatch).ToArray(),
                    SlotLockKey: $"lock:server_slot:usw:127.0.0.1:{12000 + m}",
                    SlotLeaseToken: $"lease-v502-{m}",
                    OwnerPodId: $"chat-pod-{m % 4}",
                    CreatedAtUtc: now,
                    MatchType: 10),
                TimeSpan.FromMinutes(2));
            registered.Should().BeTrue();
        }

        List<Task<DistributedPendingMatchTransitionResult>> failoverTasks = new(matchCount * 3);
        for (int m = 0; m < matchCount; m++)
        {
            int matchUpId = matchUpIds[m];
            int realMatchId = 502_000 + m;
            int port = 12000 + m;
            failoverTasks.Add(Task.Run(() => store.TryConfirmPendingMatchAsync(matchUpId, realMatchId, "127.0.0.1", port)));
            failoverTasks.Add(Task.Run(() => store.TryConfirmPendingMatchAsync(matchUpId, realMatchId, "127.0.0.1", port)));
            failoverTasks.Add(Task.Run(() => store.TryExpirePendingMatchAsync(matchUpId)));
        }

        DistributedPendingMatchTransitionResult[] failoverOutcomes = await Task.WhenAll(failoverTasks);
        totalSw.Stop();

        int gen0Delta = GC.CollectionCount(0) - gen0Before;
        int gen1Delta = GC.CollectionCount(1) - gen1Before;
        int gen2Delta = GC.CollectionCount(2) - gen2Before;

        int quorumWins = voteResults.Count(static r => r.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached);
        int acceptedNonQuorum = voteResults.Count(static r => r.Outcome == DistributedReadyCheckVoteOutcome.Accepted);
        int transitions = failoverOutcomes.Count(static o => o.Transitioned);

        double[] sortedVoteMs = voteLatenciesMs.OrderBy(static x => x).ToArray();
        double voteP50 = ComputePercentile(sortedVoteMs, 50);
        double voteP95 = ComputePercentile(sortedVoteMs, 95);
        double voteP99 = ComputePercentile(sortedVoteMs, 99);
        int totalOps = matchCount + voteTasks.Count + matchCount + failoverTasks.Count;
        double opsPerSec = Math.Round(totalOps / Math.Max(0.001, totalSw.Elapsed.TotalSeconds), 1);

        output.WriteLine(
            $"[LOAD-PROOF][Matchmaking2500] players={totalPlayers} matches={matchCount} totalOps={totalOps} | " +
            $"wallMs={totalSw.ElapsedMilliseconds} | throughput={opsPerSec} ops/s | " +
            $"voteLatency p50={voteP50}ms p95={voteP95}ms p99={voteP99}ms | " +
            $"quorumWins={quorumWins}/{matchCount} transitions={transitions}/{matchCount} | " +
            $"GC(0/1/2)={gen0Delta}/{gen1Delta}/{gen2Delta}");

        quorumWins.Should().Be(matchCount, "Every 10-player match among 2,500 players must reach ready-check quorum exactly once");
        acceptedNonQuorum.Should().Be(matchCount * (playersPerMatch - 1));
        transitions.Should().Be(matchCount, "Every pending match must transition exactly once across competing failover pods");
        totalSw.ElapsedMilliseconds.Should().BeLessThan(10000);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task LiveValkey81_TwoThousandFiveHundredPlayerMultiPodLuaStorm_ZeroDuplicatesOrCrossSlotErrors()
    {
        // protects: Real Valkey 8.1 atomic Lua script execution across 4 independent ConnectionMultiplexer pods at 2,500-player concurrency.
        // fails_when: Real Valkey 8.1 Lua scripts raise CROSSSLOT/WRONGTYPE errors, lose votes, or double-confirm matches across real network sockets.
        // seam: 4 separate live StackExchange.Redis ConnectionMultiplexer instances -> ValkeyMatchmakingStateStore -> live Valkey 8.1 server.

        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return;
        }

        string? endpoint = Environment.GetEnvironmentVariable("KONGOR_VALKEY_LOAD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            output.WriteLine("[SKIP-MANUAL] Set KONGOR_VALKEY_LOAD_ENDPOINT (e.g. 127.0.0.1:6399) to run the live Valkey 8.1 multi-pod Lua storm.");
            return;
        }

        const int podCount = 4;
        const int matchCount = 250;
        const int playersPerMatch = 10;
        const int totalPlayers = matchCount * playersPerMatch;

        ConnectionMultiplexer[] muxes = new ConnectionMultiplexer[podCount];
        ValkeyMatchmakingStateStore[] pods = new ValkeyMatchmakingStateStore[podCount];
        try
        {
            ConfigurationOptions opts = ConfigurationOptions.Parse(endpoint);
            opts.AbortOnConnectFail = true;
            opts.ConnectTimeout = 5000;
            opts.SyncTimeout = 10000;
            opts.AsyncTimeout = 10000;

            for (int i = 0; i < podCount; i++)
            {
                muxes[i] = await ConnectionMultiplexer.ConnectAsync(opts);
                pods[i] = new ValkeyMatchmakingStateStore(
                    NullLogger<ValkeyMatchmakingStateStore>.Instance,
                    redis: muxes[i],
                    podId: $"valkey81-pod-{i + 1}");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            Stopwatch sw = Stopwatch.StartNew();

            // Phase 1: 250 concurrent distributed ID allocations + ready-check creations across 4 live pods
            Task<int>[] createTasks = new Task<int>[matchCount];
            for (int m = 0; m < matchCount; m++)
            {
                int matchIdx = m;
                ValkeyMatchmakingStateStore pod = pods[matchIdx % podCount];
                createTasks[m] = Task.Run(async () =>
                {
                    int rcId = await pod.AllocateNextReadyCheckIdAsync();
                    int[] required = Enumerable.Range((matchIdx * 100) + 1, playersPerMatch).ToArray();
                    bool ok = await pod.TryCreateReadyCheckAsync(
                        new DistributedReadyCheckSnapshot(
                            ReadyCheckId: rcId,
                            GroupGuids: [$"live-g-{matchIdx}-a", $"live-g-{matchIdx}-b"],
                            RequiredAccountIds: required,
                            AcceptedAccountIds: [],
                            DeclinedByAccountId: null,
                            Status: "OPEN",
                            OwnerPodId: $"valkey81-pod-{(matchIdx % podCount) + 1}",
                            CreatedAtUtc: now,
                            ExpiresAtUtc: now.AddSeconds(45)),
                        TimeSpan.FromMinutes(2));
                    ok.Should().BeTrue();
                    return rcId;
                });
            }

            int[] rcIds = await Task.WhenAll(createTasks);

            // Phase 2: 2,500 player votes + 500 duplicate retries fanned out across all 4 pods
            ConcurrentBag<double> voteLatenciesMs = [];
            List<Task<DistributedReadyCheckVoteResult>> voteTasks = new(totalPlayers + (matchCount * 2));
            for (int m = 0; m < matchCount; m++)
            {
                int matchIdx = m;
                int rcId = rcIds[m];
                for (int p = 0; p < playersPerMatch; p++)
                {
                    int accountId = (matchIdx * 100) + 1 + p;
                    ValkeyMatchmakingStateStore voterPod = pods[(matchIdx + p) % podCount];
                    voteTasks.Add(Task.Run(async () =>
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        DistributedReadyCheckVoteResult r = await voterPod.TrySubmitReadyCheckVoteAsync(rcId, accountId, accepted: true);
                        voteLatenciesMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                        return r;
                    }));
                }

                int dupAcc = (matchIdx * 100) + 1;
                voteTasks.Add(Task.Run(() => pods[(matchIdx + 1) % podCount].TrySubmitReadyCheckVoteAsync(rcId, dupAcc, accepted: true)));
                voteTasks.Add(Task.Run(() => pods[(matchIdx + 2) % podCount].TrySubmitReadyCheckVoteAsync(rcId, dupAcc + 1, accepted: true)));
            }

            DistributedReadyCheckVoteResult[] voteResults = await Task.WhenAll(voteTasks);

            // Phase 3: 250 pending matches racing confirm/confirm/expire across distinct live pods
            Task<int>[] pendingRegTasks = new Task<int>[matchCount];
            for (int m = 0; m < matchCount; m++)
            {
                int matchIdx = m;
                ValkeyMatchmakingStateStore pod = pods[matchIdx % podCount];
                pendingRegTasks[m] = Task.Run(async () =>
                {
                    int muId = await pod.AllocateNextMatchUpIdAsync();
                    bool ok = await pod.TryRegisterPendingMatchAsync(
                        new DistributedPendingMatchSnapshot(
                            MatchUpId: muId,
                            AllocatedServerId: 2000 + matchIdx,
                            ResolvedMapName: "caldavar",
                            GroupGuids: [$"live-g-{matchIdx}-a", $"live-g-{matchIdx}-b"],
                            MemberAccountIds: Enumerable.Range((matchIdx * 100) + 1, playersPerMatch).ToArray(),
                            SlotLockKey: $"lock:server_slot:usw:127.0.0.1:{14000 + matchIdx}",
                            SlotLeaseToken: $"live-lease-{matchIdx}",
                            OwnerPodId: $"valkey81-pod-{(matchIdx % podCount) + 1}",
                            CreatedAtUtc: now,
                            MatchType: 10),
                        TimeSpan.FromMinutes(2));
                    ok.Should().BeTrue();
                    return muId;
                });
            }

            int[] matchUpIds = await Task.WhenAll(pendingRegTasks);

            List<Task<DistributedPendingMatchTransitionResult>> failoverTasks = new(matchCount * 3);
            for (int m = 0; m < matchCount; m++)
            {
                int muId = matchUpIds[m];
                int realMatchId = 800_000 + m;
                int port = 14000 + m;
                failoverTasks.Add(Task.Run(() => pods[0].TryConfirmPendingMatchAsync(muId, realMatchId, "127.0.0.1", port)));
                failoverTasks.Add(Task.Run(() => pods[1].TryConfirmPendingMatchAsync(muId, realMatchId, "127.0.0.1", port)));
                failoverTasks.Add(Task.Run(() => pods[2].TryExpirePendingMatchAsync(muId)));
            }

            DistributedPendingMatchTransitionResult[] failoverResults = await Task.WhenAll(failoverTasks);
            sw.Stop();

            int quorumWins = voteResults.Count(static r => r.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached);
            int acceptedNonQuorum = voteResults.Count(static r => r.Outcome == DistributedReadyCheckVoteOutcome.Accepted);
            int transitions = failoverResults.Count(static o => o.Transitioned);
            double[] sortedMs = voteLatenciesMs.OrderBy(static x => x).ToArray();
            double p50 = ComputePercentile(sortedMs, 50);
            double p95 = ComputePercentile(sortedMs, 95);
            double p99 = ComputePercentile(sortedMs, 99);
            int totalOps = (matchCount * 2) + voteTasks.Count + failoverTasks.Count;
            double opsPerSec = Math.Round(totalOps / Math.Max(0.001, sw.Elapsed.TotalSeconds), 1);

            output.WriteLine(
                $"[LIVE-VALKEY-8.1][4-Pod-2500CCU] endpoint={endpoint} pods={podCount} players={totalPlayers} matches={matchCount} | " +
                $"totalOps={totalOps} wallMs={sw.ElapsedMilliseconds} throughput={opsPerSec} ops/s | " +
                $"voteLua p50={p50}ms p95={p95}ms p99={p99}ms | quorumWins={quorumWins}/{matchCount} transitions={transitions}/{matchCount}");

            quorumWins.Should().Be(matchCount);
            acceptedNonQuorum.Should().Be(matchCount * (playersPerMatch - 1));
            transitions.Should().Be(matchCount);
        }
        finally
        {
            for (int i = 0; i < podCount; i++)
            {
                if (muxes[i] != null)
                {
                    await muxes[i].CloseAsync();
                    muxes[i].Dispose();
                }
            }
        }
    }
}
