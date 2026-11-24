// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Wave3MultiPodFailoverLoadTests.cs
// System: ASPIRE.Load.Tests - Wave 3B Stateless Matchmaking Failover Stress
// Policy: Strict Explicit Typing (NO 'var')
//
// Scope Note:
//   Exercises high-contention multi-pod coordination semantics (atomic ID allocation,
//   exactly-once ready-check quorum, exactly-once pending-match failover transitions,
//   and TTL expiry enforcement) against a shared offline ValkeyMatchmakingStateStore
//   backplane (redis: null). Live Valkey 8.1 Lua script execution across separate
//   ConnectionMultiplexer instances is verified by the Manual live test in
//   Wave3StatelessMatchmakingTests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ASPIRE.Common.Services.Matchmaking;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Load.Tests;

public sealed class Wave3MultiPodFailoverLoadTests
{
    private sealed class ControllableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public async Task FiftyConcurrentReadyChecks_FiveHundredVotesAcrossPods_ReachQuorumExactlyFiftyTimes()
    {
        const int matchCount = 50;
        const int playersPerMatch = 10;

        ValkeyMatchmakingStateStore sharedClusterStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "load-cluster-backplane");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Stopwatch sw = Stopwatch.StartNew();
        int[] readyCheckIds = new int[matchCount];

        // Phase 1: Register 50 concurrent 10-player ready checks using distributed ID allocation
        for (int m = 0; m < matchCount; m++)
        {
            int readyCheckId = await sharedClusterStore.AllocateNextReadyCheckIdAsync();
            readyCheckIds[m] = readyCheckId;
            int[] players = Enumerable.Range(m * 100 + 1, playersPerMatch).ToArray();
            bool created = await sharedClusterStore.TryCreateReadyCheckAsync(
                new DistributedReadyCheckSnapshot(
                    ReadyCheckId: readyCheckId,
                    GroupGuids: [$"g-{m}-a", $"g-{m}-b"],
                    RequiredAccountIds: players,
                    AcceptedAccountIds: [],
                    DeclinedByAccountId: null,
                    Status: "OPEN",
                    OwnerPodId: $"pod-{m % 4}",
                    CreatedAtUtc: now,
                    ExpiresAtUtc: now.AddSeconds(30)),
                TimeSpan.FromMinutes(2));
            created.Should().BeTrue();
        }

        // Phase 2: Blast 500 concurrent player accept votes + 100 duplicate retries across all 50 ready checks
        List<Task<DistributedReadyCheckVoteResult>> voteTasks = new(matchCount * (playersPerMatch + 2));
        for (int m = 0; m < matchCount; m++)
        {
            int readyCheckId = readyCheckIds[m];
            for (int p = 0; p < playersPerMatch; p++)
            {
                int accountId = m * 100 + 1 + p;
                voteTasks.Add(Task.Run(() => sharedClusterStore.TrySubmitReadyCheckVoteAsync(readyCheckId, accountId, accepted: true)));
            }
            // Inject duplicate accept retries under contention
            int dupAccountId = m * 100 + 1;
            voteTasks.Add(Task.Run(() => sharedClusterStore.TrySubmitReadyCheckVoteAsync(readyCheckId, dupAccountId, accepted: true)));
            voteTasks.Add(Task.Run(() => sharedClusterStore.TrySubmitReadyCheckVoteAsync(readyCheckId, dupAccountId + 1, accepted: true)));
        }

        DistributedReadyCheckVoteResult[] results = await Task.WhenAll(voteTasks);
        sw.Stop();

        int quorumWins = results.Count(r => r.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached);
        int acceptedNonQuorum = results.Count(r => r.Outcome == DistributedReadyCheckVoteOutcome.Accepted);

        quorumWins.Should().Be(matchCount, "Every 10-player match must trigger server allocation exactly once");
        acceptedNonQuorum.Should().Be(matchCount * (playersPerMatch - 1));
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task FiftyConcurrentPendingMatches_RacingConfirmAndExpireAcrossPods_TransitionAtMostOnce()
    {
        const int matchCount = 50;

        ValkeyMatchmakingStateStore sharedClusterStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "load-cluster-failover");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int[] matchUpIds = new int[matchCount];
        for (int m = 0; m < matchCount; m++)
        {
            int matchUpId = await sharedClusterStore.AllocateNextMatchUpIdAsync();
            matchUpIds[m] = matchUpId;
            bool ok = await sharedClusterStore.TryRegisterPendingMatchAsync(
                new DistributedPendingMatchSnapshot(
                    MatchUpId: matchUpId,
                    AllocatedServerId: 100 + m,
                    ResolvedMapName: "caldavar",
                    GroupGuids: [$"g-{m}-1", $"g-{m}-2"],
                    MemberAccountIds: [m * 10 + 1, m * 10 + 2],
                    SlotLockKey: $"lock:server_slot:usw:127.0.0.1:{11235 + m}",
                    SlotLeaseToken: $"lease-{m}",
                    OwnerPodId: "pod-a-pre-crash",
                    CreatedAtUtc: now,
                    MatchType: 10),
                TimeSpan.FromMinutes(2));
            ok.Should().BeTrue();
        }

        // Simulate Pod A dying and Pods B, C, D racing confirm + confirm + expire simultaneously for all 50 matches
        List<Task<DistributedPendingMatchTransitionResult>> raceTasks = new(matchCount * 3);
        for (int m = 0; m < matchCount; m++)
        {
            int matchUpId = matchUpIds[m];
            int realMatchId = 900_000 + m;
            int port = 11235 + m;
            raceTasks.Add(Task.Run(() => sharedClusterStore.TryConfirmPendingMatchAsync(matchUpId, realMatchId, "127.0.0.1", port)));
            raceTasks.Add(Task.Run(() => sharedClusterStore.TryConfirmPendingMatchAsync(matchUpId, realMatchId, "127.0.0.1", port)));
            raceTasks.Add(Task.Run(() => sharedClusterStore.TryExpirePendingMatchAsync(matchUpId)));
        }

        DistributedPendingMatchTransitionResult[] outcomes = await Task.WhenAll(raceTasks);
        outcomes.Count(o => o.Transitioned).Should().Be(matchCount, "Each pending match must transition exactly once across the cluster");
    }

    [Fact]
    public async Task ConcurrentMultiPodIdAllocation_ThousandAllocations_ZeroCollisions()
    {
        const int allocationsPerSequence = 1000;

        ValkeyMatchmakingStateStore sharedClusterStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "load-id-sequence");

        Task<int>[] matchUpTasks = Enumerable.Range(0, allocationsPerSequence)
            .Select(_ => Task.Run(() => sharedClusterStore.AllocateNextMatchUpIdAsync()))
            .ToArray();
        Task<int>[] readyCheckTasks = Enumerable.Range(0, allocationsPerSequence)
            .Select(_ => Task.Run(() => sharedClusterStore.AllocateNextReadyCheckIdAsync()))
            .ToArray();

        int[] matchUpIds = await Task.WhenAll(matchUpTasks);
        int[] readyCheckIds = await Task.WhenAll(readyCheckTasks);

        matchUpIds.Distinct().Should().HaveCount(allocationsPerSequence, "MatchUpIds must never collide across concurrent pods");
        matchUpIds.Min().Should().BeGreaterThan(100_000);

        readyCheckIds.Distinct().Should().HaveCount(allocationsPerSequence, "ReadyCheckIds must never collide across concurrent pods");
        readyCheckIds.Min().Should().BeGreaterThan(200_000);
    }

    [Fact]
    public async Task ExpiryEnforcementUnderContention_ExpiredReadyChecksAndPendingMatchesNeverSucceed()
    {
        const int batchSize = 40;
        ControllableTimeProvider clock = new(new DateTimeOffset(2026, 11, 19, 16, 0, 0, TimeSpan.Zero));
        ValkeyMatchmakingStateStore sharedClusterStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            timeProvider: clock,
            podId: "load-expiry-gate");

        DateTimeOffset now = clock.GetUtcNow();
        int[] rcIds = new int[batchSize];
        int[] pmIds = new int[batchSize];

        for (int i = 0; i < batchSize; i++)
        {
            rcIds[i] = await sharedClusterStore.AllocateNextReadyCheckIdAsync();
            await sharedClusterStore.TryCreateReadyCheckAsync(
                new DistributedReadyCheckSnapshot(
                    ReadyCheckId: rcIds[i],
                    GroupGuids: [$"exp-g-{i}"],
                    RequiredAccountIds: [i * 10 + 1, i * 10 + 2],
                    AcceptedAccountIds: [],
                    DeclinedByAccountId: null,
                    Status: "OPEN",
                    OwnerPodId: "pod-a",
                    CreatedAtUtc: now,
                    ExpiresAtUtc: now.AddSeconds(15)),
                TimeSpan.FromSeconds(45));

            pmIds[i] = await sharedClusterStore.AllocateNextMatchUpIdAsync();
            await sharedClusterStore.TryRegisterPendingMatchAsync(
                new DistributedPendingMatchSnapshot(
                    MatchUpId: pmIds[i],
                    AllocatedServerId: 500 + i,
                    ResolvedMapName: "caldavar",
                    GroupGuids: [$"exp-g-{i}"],
                    MemberAccountIds: [i * 10 + 1, i * 10 + 2],
                    SlotLockKey: null,
                    SlotLeaseToken: null,
                    OwnerPodId: "pod-a",
                    CreatedAtUtc: now),
                TimeSpan.FromSeconds(30));
        }

        // Advance clock past BOTH ReadyCheck ExpiresAtUtc (15s) AND PendingMatch TTL (30s)
        clock.Advance(TimeSpan.FromSeconds(35));

        List<Task<DistributedReadyCheckVoteResult>> lateVoteTasks = new(batchSize * 2);
        List<Task<DistributedPendingMatchTransitionResult>> lateConfirmTasks = new(batchSize * 2);

        for (int i = 0; i < batchSize; i++)
        {
            int rcId = rcIds[i];
            int pmId = pmIds[i];
            int acc1 = i * 10 + 1;
            int acc2 = i * 10 + 2;
            lateVoteTasks.Add(Task.Run(() => sharedClusterStore.TrySubmitReadyCheckVoteAsync(rcId, acc1, accepted: true)));
            lateVoteTasks.Add(Task.Run(() => sharedClusterStore.TrySubmitReadyCheckVoteAsync(rcId, acc2, accepted: true)));
            lateConfirmTasks.Add(Task.Run(() => sharedClusterStore.TryConfirmPendingMatchAsync(pmId, 800_000 + i, "127.0.0.1", 11235 + i)));
            lateConfirmTasks.Add(Task.Run(() => sharedClusterStore.TryConfirmPendingMatchAsync(pmId, 800_000 + i, "127.0.0.1", 11235 + i)));
        }

        DistributedReadyCheckVoteResult[] voteResults = await Task.WhenAll(lateVoteTasks);
        DistributedPendingMatchTransitionResult[] confirmResults = await Task.WhenAll(lateConfirmTasks);

        voteResults.Should().NotContain(
            r => r.Outcome == DistributedReadyCheckVoteOutcome.Accepted ||
                 r.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached,
            "expired ready checks must never accept votes or reach quorum");
        confirmResults.Should().NotContain(r => r.Transitioned,
            "post-TTL pending matches must never confirm");
        confirmResults.Should().OnlyContain(r => r.FinalStatus == "EXPIRED");
    }
}
