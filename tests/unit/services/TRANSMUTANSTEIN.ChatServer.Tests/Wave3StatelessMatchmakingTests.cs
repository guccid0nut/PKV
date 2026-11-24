// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Wave3StatelessMatchmakingTests.cs
// System: TRANSMUTANSTEIN ChatServer Tests - Wave 3B Stateless Valkey Matchmaking
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Services.Matchmaking;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Attributes;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Contracts;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Extensions;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class Wave3StatelessMatchmakingTests
{
    private sealed class ControllableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class SimulatedChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        private readonly object _packetLock = new();
        private readonly List<byte[]> _sentPackets = [];

        public override bool IsSessionConnected => true;

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            byte[] copy = buffer.ToArray();
            lock (_packetLock)
            {
                _sentPackets.Add(copy);
            }
            return true;
        }

        public List<byte[]> SnapshotPackets()
        {
            lock (_packetLock)
            {
                return [.. _sentPackets];
            }
        }
    }

    [Fact]
    public async Task ReadyCheck_ConcurrentMultiPodAccepts_ReachQuorumExactlyOnce()
    {
        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "pod-a");

        int[] requiredPlayers = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        bool created = await store.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 5001,
                GroupGuids: ["grp-1", "grp-2"],
                RequiredAccountIds: requiredPlayers,
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-a",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));

        created.Should().BeTrue();

        // Duplicate create must fail atomically
        bool dupCreate = await store.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 5001,
                GroupGuids: ["grp-1", "grp-2"],
                RequiredAccountIds: requiredPlayers,
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-b",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));
        dupCreate.Should().BeFalse();

        // Non-participant must be rejected cleanly
        DistributedReadyCheckVoteResult nonPart = await store.TrySubmitReadyCheckVoteAsync(5001, 9999, accepted: true);
        nonPart.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.NotParticipant);

        // Concurrent 10-player accept race across pods
        Task<DistributedReadyCheckVoteResult>[] tasks = requiredPlayers
            .Select(pid => Task.Run(() => store.TrySubmitReadyCheckVoteAsync(5001, pid, accepted: true)))
            .ToArray();

        DistributedReadyCheckVoteResult[] results = await Task.WhenAll(tasks);

        results.Count(r => r.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached).Should().Be(1);
        results.Count(r => r.Outcome == DistributedReadyCheckVoteOutcome.Accepted).Should().Be(9);

        // Post-quorum vote or expire must report AlreadyResolved
        DistributedReadyCheckVoteResult lateVote = await store.TrySubmitReadyCheckVoteAsync(5001, 101, accepted: false);
        lateVote.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.AlreadyResolved);
        lateVote.Status.Should().Be("QUORUM_REACHED");

        DistributedReadyCheckVoteResult lateExpire = await store.TryExpireReadyCheckAsync(5001);
        lateExpire.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.AlreadyResolved);
    }

    [Fact]
    public async Task ReadyCheck_Expire_ReturnsExactSortedMissingPlayers()
    {
        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "pod-a");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 5002,
                GroupGuids: ["grp-10"],
                RequiredAccountIds: [11, 22, 33, 44],
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-a",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));

        await store.TrySubmitReadyCheckVoteAsync(5002, 11, accepted: true);
        await store.TrySubmitReadyCheckVoteAsync(5002, 44, accepted: true);

        DistributedReadyCheckVoteResult expired = await store.TryExpireReadyCheckAsync(5002);
        expired.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.Expired);
        expired.AcceptedCount.Should().Be(2);
        expired.TotalPlayers.Should().Be(4);
        expired.MissingAccountIds.Should().Equal(22, 33);
    }

    [Fact]
    public async Task PendingMatch_ConcurrentConfirmAndExpire_AtMostOneWins()
    {
        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "pod-a");

        const string sampleLeaseId = "lease-alpha";
        DistributedPendingMatchSnapshot snap = new(
            MatchUpId: 77001,
            AllocatedServerId: 42,
            ResolvedMapName: "caldavar",
            GroupGuids: ["grp-1", "grp-2"],
            MemberAccountIds: [101, 102],
            SlotLockKey: "lock:server_slot:usw:127.0.0.1:11235",
            SlotLeaseToken: sampleLeaseId,
            OwnerPodId: "pod-a",
            CreatedAtUtc: DateTimeOffset.UtcNow);

        bool registered = await store.TryRegisterPendingMatchAsync(snap, TimeSpan.FromMinutes(2));
        registered.Should().BeTrue();

        Task<DistributedPendingMatchTransitionResult> confirmA = Task.Run(() =>
            store.TryConfirmPendingMatchAsync(77001, 90001, "127.0.0.1", 11235));
        Task<DistributedPendingMatchTransitionResult> confirmB = Task.Run(() =>
            store.TryConfirmPendingMatchAsync(77001, 90001, "127.0.0.1", 11235));
        Task<DistributedPendingMatchTransitionResult> expireB = Task.Run(() =>
            store.TryExpirePendingMatchAsync(77001));

        DistributedPendingMatchTransitionResult[] outcomes = await Task.WhenAll(confirmA, confirmB, expireB);
        outcomes.Count(o => o.Transitioned).Should().Be(1);
    }

    [Fact]
    public async Task MatchmakingService_PodFailover_PodBCompletesPodAReadyCheckAndConfirmsPendingMatch()
    {
        // Shared distributed state store (simulates shared Valkey backplane across Pod A and Pod B)
        ValkeyMatchmakingStateStore sharedStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            podId: "cluster-shared");

        Mock<IRedisServerSlotLeaseService> leaseMock = new();
        leaseMock
            .Setup(s => s.ReleaseSlotLeaseAsync("lock:server_slot:usw:127.0.0.1:11235", "lease-pod-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Mock<IChatContext> podBContextMock = new();
        podBContextMock.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());
        podBContextMock.SetupGet(c => c.MatchServerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());

        IConfiguration config = new ConfigurationBuilder().Build();
        using MatchmakingService podB = new(
            config,
            podBContextMock.Object,
            slotLeaseService: leaseMock.Object,
            distributedStateStore: sharedStore);

        // 1. Pod A creates ReadyCheck 8801 for players 10 & 20, accepts player 10, then Pod A dies.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await sharedStore.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 8801,
                GroupGuids: ["grp-1", "grp-2"],
                RequiredAccountIds: [10, 20],
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-a-dead",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(25),
                MapName: "caldavar",
                GameMode: "All Pick"),
            TimeSpan.FromSeconds(60));
        DistributedReadyCheckVoteResult podAVote = await sharedStore.TrySubmitReadyCheckVoteAsync(8801, 10, accepted: true);
        podAVote.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.Accepted);

        // 2. Pod B (which has ZERO local _pendingReadyChecks) receives Player 20's accept vote -> reaches quorum!
        podB.PendingReadyChecks.Should().BeEmpty();
        bool podBVoteHandled = await podB.SubmitReadyCheckResponseAsync(8801, 20, accepted: true);
        podBVoteHandled.Should().BeTrue();

        DistributedReadyCheckSnapshot? finalRc = await sharedStore.GetReadyCheckAsync(8801);
        finalRc.Should().NotBeNull();
        finalRc!.Status.Should().Be("QUORUM_REACHED");
        finalRc.AcceptedAccountIds.Should().Equal(10, 20);

        // 3. Pod A registered PendingMatch 99001, then died. Game server sends ConfirmMatch to Pod B!
        const string podALeaseId = "lease-pod-a";
        await sharedStore.TryRegisterPendingMatchAsync(
            new DistributedPendingMatchSnapshot(
                MatchUpId: 99001,
                AllocatedServerId: 7,
                ResolvedMapName: "caldavar",
                GroupGuids: ["grp-1", "grp-2"],
                MemberAccountIds: [10, 20],
                SlotLockKey: "lock:server_slot:usw:127.0.0.1:11235",
                SlotLeaseToken: podALeaseId,
                OwnerPodId: "pod-a-dead",
                CreatedAtUtc: now,
                MatchType: 10),
            TimeSpan.FromMinutes(2));

        podB.PendingMatches.Should().BeEmpty();
        await podB.ConfirmMatch(99001, 555123, "127.0.0.1", 11235);

        // Verify lease released and subsequent duplicate confirm on Pod C is a clean no-op
        leaseMock.Verify(
            s => s.ReleaseSlotLeaseAsync("lock:server_slot:usw:127.0.0.1:11235", "lease-pod-a", It.IsAny<CancellationToken>()),
            Times.Once);

        DistributedPendingMatchTransitionResult dupConfirm = await sharedStore.TryConfirmPendingMatchAsync(99001, 555123, "127.0.0.1", 11235);
        dupConfirm.Transitioned.Should().BeFalse();
        dupConfirm.FinalStatus.Should().Be("CONFIRMED");

        IReadOnlyList<DistributedMatchmakingStreamEvent> events = await sharedStore.ReadRecentEventsAsync(20);
        events.Should().Contain(e => e.EventType == "readycheck_quorum_reached");
        events.Should().Contain(e => e.EventType == "MATCH_ALLOCATION_REQUESTED");
        events.Should().Contain(e => e.EventType == "pending_match_confirmed");
        events.Should().Contain(e => e.EventType == "MATCH_CONFIRMED_DISPATCH");
    }

    [Theory]
    [InlineData(null, "kongor:mm")]
    [InlineData("custom:mm:cluster", "custom:mm:cluster")]
    [InlineData("{already:tagged}:mm", "already:tagged")]
    [InlineData("bad{}prefix", "badprefix")]
    public async Task ValkeyLuaScripts_AllCapturedKeysShareSingleClusterHashTag_ZeroCrossSlotViolations(
        string? customPrefix,
        string expectedHashTag)
    {
        List<(string Script, RedisKey[] Keys, RedisValue[] Values)> capturedScripts = [];

        Mock<IDatabase> dbMock = new();
        dbMock
            .Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]?>(),
                It.IsAny<RedisValue[]?>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((string script, RedisKey[]? keys, RedisValue[]? values, CommandFlags _) =>
            {
                RedisKey[] capturedKeys = keys ?? [];
                RedisValue[] capturedValues = values ?? [];
                capturedScripts.Add((script, capturedKeys, capturedValues));

                if (script.Contains("TransitionPendingMatch", StringComparison.OrdinalIgnoreCase) ||
                    script.Contains("state ~= 'PENDING'", StringComparison.Ordinal))
                {
                    return RedisResult.Create([RedisResult.Create((RedisValue)"1"), RedisResult.Create((RedisValue)"CONFIRMED"), RedisResult.Create((RedisValue)"")]);
                }

                if (script.Contains("QUORUM_REACHED", StringComparison.Ordinal))
                {
                    return RedisResult.Create([RedisResult.Create((RedisValue)4), RedisResult.Create((RedisValue)1), RedisResult.Create((RedisValue)2), RedisResult.Create((RedisValue)"OPEN")]);
                }

                if (script.Contains("SDIFF", StringComparison.Ordinal))
                {
                    return RedisResult.Create([RedisResult.Create((RedisValue)7), RedisResult.Create((RedisValue)1), RedisResult.Create((RedisValue)2), RedisResult.Create((RedisValue)"EXPIRED"), RedisResult.Create((RedisValue)"202")]);
                }

                return RedisResult.Create((RedisValue)"1");
            });

        Mock<IConnectionMultiplexer> muxMock = new();
        muxMock.SetupGet(m => m.IsConnected).Returns(true);
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(dbMock.Object);

        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: muxMock.Object,
            podId: "cluster-audit-pod",
            keyPrefix: customPrefix);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await store.TryRegisterPendingMatchAsync(
            new DistributedPendingMatchSnapshot(
                MatchUpId: 100501,
                AllocatedServerId: 5,
                ResolvedMapName: "caldavar",
                GroupGuids: ["g1"],
                MemberAccountIds: [101, 202],
                SlotLockKey: null,
                SlotLeaseToken: null,
                OwnerPodId: "cluster-audit-pod",
                CreatedAtUtc: now),
            TimeSpan.FromMinutes(2));

        await store.TryConfirmPendingMatchAsync(100501, 555, "127.0.0.1", 11235);
        await store.TryExpirePendingMatchAsync(100501);

        await store.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 200501,
                GroupGuids: ["g1", "g2"],
                RequiredAccountIds: [101, 101, 202],
                AcceptedAccountIds: [101],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "cluster-audit-pod",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));

        await store.TrySubmitReadyCheckVoteAsync(200501, 202, accepted: true);
        await store.TryExpireReadyCheckAsync(200501);

        capturedScripts.Should().HaveCount(6, "all 6 Lua script paths must execute against IDatabase");

        foreach ((string script, RedisKey[] keys, RedisValue[] values) in capturedScripts)
        {
            keys.Should().NotBeEmpty();
            HashSet<string> hashTags = [];
            foreach (RedisKey key in keys)
            {
                string keyStr = key.ToString();
                keyStr.Should().Contain("{", "every cluster key must contain a hash tag");
                keyStr.Should().Contain("}", "every cluster key must close its hash tag");
                hashTags.Add(ValkeyMatchmakingStateStore.ExtractRedisHashTag(keyStr));
            }

            hashTags.Should().ContainSingle(
                $"every key in a multi-key Lua script must share one Redis Cluster hash slot to prevent CROSSSLOT (keys: {string.Join(", ", keys.Select(k => k.ToString()))})");
            hashTags.Single().Should().Be(expectedHashTag);

            if (script.Contains("readycheck_created", StringComparison.Ordinal))
            {
                keys.Should().HaveCount(5, "CreateReadyCheckLua must maintain KEYS[5]=account_readycheck hash");
                keys[4].ToString().Should().EndWith(":account_readycheck");
                script.Should().Contain("SCARD", "CreateReadyCheckLua must derive total_players from SCARD(KEYS[2])");
                // ARGV[6]=requiredCount(3), ARGV[7..9]=required(101,101,202), ARGV[10]=prePopulatedAccepted(101)
                values[5].ToString().Should().Be("3");
                values.Should().HaveCount(10);
                values[9].ToString().Should().Be("101");
            }

            if (script.Contains("state ~= 'PENDING'", StringComparison.Ordinal))
            {
                script.Should().Contain("HDEL", "TransitionPendingMatchLua must delete leaked snapshot hash entries on expiry");
            }
        }
    }

    [Fact]
    public void AotJsonContext_RoundTripsAllSnapshotsAndPayloads_PreservesRecoveryMetadata()
    {
        DateTimeOffset now = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);

        DistributedGroupSnapshot groupIn = new(
            GroupGuid: "grp-aot-01",
            LeaderAccountId: 1001,
            MemberAccountIds: [1001, 1002, 1003],
            MapName: "caldavar",
            GroupType: 1,
            GameType: 2,
            GameModes: ["ap", "sd"],
            GameRegions: ["use", "usw"],
            QueueStartTimeUtc: now.AddSeconds(-30),
            OwnerPodId: "pod-aot",
            UpdatedAtUtc: now,
            Ranked: true,
            ClientVersion: "4.10.1.0",
            MatchFidelity: 2,
            BotDifficulty: 3,
            RandomizeBots: true,
            TeamSize: 5);

        string groupJson = JsonSerializer.Serialize(groupIn, ValkeyMatchmakingJsonContext.Default.DistributedGroupSnapshot);
        DistributedGroupSnapshot? groupOut = JsonSerializer.Deserialize(groupJson, ValkeyMatchmakingJsonContext.Default.DistributedGroupSnapshot);
        groupOut.Should().BeEquivalentTo(groupIn);

        string sampleSlotLease = "lease-aot";
        DistributedPendingMatchSnapshot pmIn = new(
            MatchUpId: 100123,
            AllocatedServerId: 44,
            ResolvedMapName: "midwars",
            GroupGuids: ["grp-aot-01", "grp-aot-02"],
            MemberAccountIds: [1001, 1002],
            SlotLockKey: "lock:slot:use:127.0.0.1:11235",
            SlotLeaseToken: sampleSlotLease,
            OwnerPodId: "pod-aot",
            CreatedAtUtc: now,
            Status: "PENDING",
            MatchType: 4,
            GroupType: 1,
            GameType: 3,
            GameModes: ["ap"],
            GameRegions: ["use"],
            TeamSize: 5,
            Ranked: false,
            BotDifficulty: 2,
            RandomizeBots: true);

        string pmJson = JsonSerializer.Serialize(pmIn, ValkeyMatchmakingJsonContext.Default.DistributedPendingMatchSnapshot);
        DistributedPendingMatchSnapshot? pmOut = JsonSerializer.Deserialize(pmJson, ValkeyMatchmakingJsonContext.Default.DistributedPendingMatchSnapshot);
        pmOut.Should().BeEquivalentTo(pmIn);

        DistributedReadyCheckSnapshot rcIn = new(
            ReadyCheckId: 200123,
            GroupGuids: ["grp-aot-01", "grp-aot-02"],
            RequiredAccountIds: [1001, 1002],
            AcceptedAccountIds: [1001],
            DeclinedByAccountId: null,
            Status: "OPEN",
            OwnerPodId: "pod-aot",
            CreatedAtUtc: now,
            ExpiresAtUtc: now.AddSeconds(15),
            MapName: "caldavar",
            GameMode: "All Pick",
            GroupType: 1,
            GameType: 1,
            GameModes: ["All Pick"],
            GameRegions: ["USE"],
            Ranked: true,
            ClientVersion: "4.10.1.0",
            MatchFidelity: 1,
            BotDifficulty: 2,
            RandomizeBots: false,
            RequiredMemberGroups:
            [
                new DistributedReadyCheckGroupMembership("grp-aot-01", [1001], now.AddSeconds(-40)),
                new DistributedReadyCheckGroupMembership("grp-aot-02", [1002], now.AddSeconds(-20))
            ]);

        string rcJson = JsonSerializer.Serialize(rcIn, ValkeyMatchmakingJsonContext.Default.DistributedReadyCheckSnapshot);
        DistributedReadyCheckSnapshot? rcOut = JsonSerializer.Deserialize(rcJson, ValkeyMatchmakingJsonContext.Default.DistributedReadyCheckSnapshot);
        rcOut.Should().BeEquivalentTo(rcIn);

        GroupRemovedPayload grpRemovedIn = new("grp-aot-01", "pod-aot");
        string grpRemovedJson = JsonSerializer.Serialize(grpRemovedIn, ValkeyMatchmakingJsonContext.Default.GroupRemovedPayload);
        JsonSerializer.Deserialize(grpRemovedJson, ValkeyMatchmakingJsonContext.Default.GroupRemovedPayload)
            .Should().Be(grpRemovedIn);

        MatchConfirmedDispatchPayload dispatchIn = new(
            MatchUpId: 100123,
            RealMatchId: 999888,
            MatchType: 4,
            ServerAddress: "127.0.0.1",
            ServerPort: 11235,
            LocalAddress: "127.0.0.1",
            LocalPort: 11235,
            ResolvedMapName: "midwars",
            GroupType: 1,
            GameType: 3,
            TeamSize: 5,
            GameModes: ["ap"],
            GameRegions: ["use"],
            TargetAccountIds: [1001, 1002],
            DispatchedByPod: "pod-b");
        string dispatchJson = JsonSerializer.Serialize(dispatchIn, ValkeyMatchmakingJsonContext.Default.MatchConfirmedDispatchPayload);
        JsonSerializer.Deserialize(dispatchJson, ValkeyMatchmakingJsonContext.Default.MatchConfirmedDispatchPayload)
            .Should().BeEquivalentTo(dispatchIn);
    }

    [Fact]
    public async Task RedisConfigured_WhenRedisThrowsOrDisconnects_PropagatesStoreExceptionWithoutSilentLocalFallback()
    {
        // 1. CROSSSLOT server error -> explicit ClusterCrossSlotMisconfiguration
        Mock<IDatabase> crossSlotDb = new();
        crossSlotDb
            .Setup(db => db.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]?>(), It.IsAny<RedisValue[]?>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("CROSSSLOT Keys in request don't hash to the same slot"));

        Mock<IConnectionMultiplexer> crossSlotMux = new();
        crossSlotMux.SetupGet(m => m.IsConnected).Returns(true);
        crossSlotMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(crossSlotDb.Object);

        ValkeyMatchmakingStateStore crossSlotStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: crossSlotMux.Object,
            podId: "pod-crossslot");

        Func<Task> crossSlotAct = async () => await crossSlotStore.TrySubmitReadyCheckVoteAsync(200001, 101, accepted: true);
        DistributedMatchmakingStoreException crossSlotEx = (await crossSlotAct.Should().ThrowAsync<DistributedMatchmakingStoreException>()).Which;
        crossSlotEx.FailureKind.Should().Be(DistributedMatchmakingStoreFailureKind.ClusterCrossSlotMisconfiguration);
        crossSlotEx.Message.Should().Contain("[VALKEY-MM-ERROR]");

        // 2. Transient RedisConnectionException -> explicit TransientConnectivity (no silent local fallback!)
        Mock<IDatabase> transientDb = new();
        transientDb
            .Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Valkey node unreachable"));

        Mock<IConnectionMultiplexer> transientMux = new();
        transientMux.SetupGet(m => m.IsConnected).Returns(true);
        transientMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(transientDb.Object);

        ValkeyMatchmakingStateStore transientStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: transientMux.Object,
            podId: "pod-transient");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Func<Task> upsertAct = async () => await transientStore.UpsertGroupAsync(new DistributedGroupSnapshot(
            "g-fail", 1, [1], "caldavar", 1, 1, ["ap"], ["use"], now, "pod-transient", now));
        DistributedMatchmakingStoreException transientEx = (await upsertAct.Should().ThrowAsync<DistributedMatchmakingStoreException>()).Which;
        transientEx.FailureKind.Should().Be(DistributedMatchmakingStoreFailureKind.TransientConnectivity);

        // 3. Disconnected multiplexer -> explicit NotConnected (refuses silent local fallback!)
        Mock<IConnectionMultiplexer> disconnectedMux = new();
        disconnectedMux.SetupGet(m => m.IsConnected).Returns(false);

        ValkeyMatchmakingStateStore disconnectedStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: disconnectedMux.Object,
            podId: "pod-disconnected");

        Func<Task> allocAct = async () => await disconnectedStore.AllocateNextMatchUpIdAsync();
        DistributedMatchmakingStoreException disconnectedEx = (await allocAct.Should().ThrowAsync<DistributedMatchmakingStoreException>()).Which;
        disconnectedEx.FailureKind.Should().Be(DistributedMatchmakingStoreFailureKind.NotConnected);
    }

    [Fact]
    public async Task DistributedIdAllocationAndAccountLookup_UsesAtomicIncrAndAccountReadyCheckHash()
    {
        long matchUpSeq = 0;
        long readyCheckSeq = 0;
        string metaStatus = "OPEN";
        bool staleMappingDeleted = false;

        Mock<IDatabase> dbMock = new();
        dbMock
            .Setup(db => db.StringIncrementAsync(
                It.Is<RedisKey>(k => k.ToString() == "{kongor:mm}:seq:matchup_id"),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref matchUpSeq));

        dbMock
            .Setup(db => db.StringIncrementAsync(
                It.Is<RedisKey>(k => k.ToString() == "{kongor:mm}:seq:readycheck_id"),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref readyCheckSeq));

        dbMock
            .Setup(db => db.HashGetAsync(
                It.Is<RedisKey>(k => k.ToString() == "{kongor:mm}:account_readycheck"),
                It.Is<RedisValue>(f => f.ToString() == "4242"),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)"200001");

        dbMock
            .Setup(db => db.HashGetAsync(
                It.Is<RedisKey>(k => k.ToString() == "{kongor:mm}:readycheck:200001:meta"),
                It.Is<RedisValue>(f => f.ToString() == "status"),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => (RedisValue)metaStatus);

        dbMock
            .Setup(db => db.HashDeleteAsync(
                It.Is<RedisKey>(k => k.ToString() == "{kongor:mm}:account_readycheck"),
                It.Is<RedisValue>(f => f.ToString() == "4242"),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(() =>
            {
                staleMappingDeleted = true;
                return true;
            });

        Mock<IConnectionMultiplexer> muxMock = new();
        muxMock.SetupGet(m => m.IsConnected).Returns(true);
        muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(dbMock.Object);

        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: muxMock.Object,
            podId: "pod-seq");

        int m1 = await store.AllocateNextMatchUpIdAsync();
        int m2 = await store.AllocateNextMatchUpIdAsync();
        int rc1 = await store.AllocateNextReadyCheckIdAsync();
        int rc2 = await store.AllocateNextReadyCheckIdAsync();

        m1.Should().Be(100_001);
        m2.Should().Be(100_002);
        rc1.Should().Be(200_001);
        rc2.Should().Be(200_002);

        // Active OPEN ready check resolves cleanly
        int? foundActive = await store.FindActiveReadyCheckIdForAccountAsync(4242);
        foundActive.Should().Be(200_001);
        staleMappingDeleted.Should().BeFalse();

        // Once meta status transitions away from OPEN, lookup returns null and prunes stale mapping
        metaStatus = "DECLINED";
        int? foundStale = await store.FindActiveReadyCheckIdForAccountAsync(4242);
        foundStale.Should().BeNull();
        staleMappingDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task TwoPods_CrossPodDeclineSuppressesOwnerLaunch_AndCrossPodAcceptAndFailoverConfirmAvoidDoubleDispatch()
    {
        Log.Initialise(NullLogger.Instance);
        ControllableTimeProvider clock = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        ValkeyMatchmakingStateStore sharedStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            timeProvider: clock,
            podId: "shared-cluster-backplane");

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:EnableReadyCheck"] = "true",
                ["Matchmaking:ReadyCheckTimeoutSeconds"] = "15.0",
                ["Matchmaking:PlayersPerTeam"] = "1"
            })
            .Build();

        using FloodPreventionService flood = new(NullLogger<FloodPreventionService>.Instance);
        using TCPServer tcpServer = new(IPAddress.Loopback, 0);

        ConcurrentDictionary<string, ChatSession> podAClients = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<int, ChatSession> podAServers = new();
        Mock<IChatContext> podAContext = CreateChatContextMock(podAClients, podAServers);

        ConcurrentDictionary<string, ChatSession> podBClients = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<int, ChatSession> podBServers = new();
        Mock<IChatContext> podBContext = CreateChatContextMock(podBClients, podBServers);

        using ServiceProvider spA = CreateSessionServiceProvider(podAContext.Object, flood);
        using ServiceProvider spB = CreateSessionServiceProvider(podBContext.Object, flood);

        using MatchmakingService podA = new(config, podAContext.Object, timeProvider: clock, distributedStateStore: sharedStore);
        using MatchmakingService podB = new(config, podBContext.Object, timeProvider: clock, distributedStateStore: sharedStore);

        SimulatedChatSession gameServerA = CreateGameServerSession(tcpServer, spA, serverId: 901, location: "USE");
        podAServers[901] = gameServerA;

        // --- SCENARIO 1: Cross-pod DECLINE via SendInstantMessage on Pod B prevents Owner Pod A from launching match ---
        SimulatedChatSession p101 = CreateClientSession(tcpServer, spA, 101, "Player_101");
        SimulatedChatSession p102 = CreateClientSession(tcpServer, spB, 102, "Player_102");
        podAClients[p101.Account.Name] = p101;
        podBClients[p102.Account.Name] = p102;

        MatchmakingGroup g101 = CreateQueuedGroup(podA, p101, ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "caldavar", clock.GetUtcNow().AddSeconds(-30));
        MatchmakingGroup g102 = CreateQueuedGroup(podA, p102, ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "caldavar", clock.GetUtcNow().AddSeconds(-20));

        bool started1 = await podA.ProcessMatchForSimulationAsync([g101, g102]);
        started1.Should().BeTrue();
        PendingReadyCheckState rc1 = podA.PendingReadyChecks.Values.Single();
        rc1.ReadyCheckId.Should().BeGreaterThan(200_000, "distributed readyCheckId must come from store sequence");

        // Pod B receives DECLINE via SendInstantMessage (async command processor path)
        SendInstantMessage imOnPodB = new(podBContext.Object, queueArcadeService: null, matchmakingService: podB);
        ChatBuffer declineImBuf = new();
        declineImBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM);
        declineImBuf.WriteString(MatchmakingService.ReadyCheckSenderName);
        declineImBuf.WriteString($"DECLINE|{rc1.ReadyCheckId}");
        declineImBuf.WriteInt8(0);
        await ((IAsynchronousCommandProcessor<ChatSession>)imOnPodB).Process(p102, new ChatBuffer(declineImBuf.AsSpan()));

        // Owner Pod A then receives ACCEPT from Player 101 -> Valkey rejects as AlreadyResolved -> ZERO game server allocation!
        bool ownerAcceptAfterCrossPodDecline = await podA.SubmitReadyCheckResponseAsync(rc1.ReadyCheckId, 101, accepted: true);
        ownerAcceptAfterCrossPodDecline.Should().BeFalse("Owner pod MUST respect Valkey DECLINED status");
        podA.PendingReadyChecks.Should().BeEmpty("Stale local ready check must be purged");
        gameServerA.SnapshotPackets().Should().BeEmpty("Declined match MUST NEVER launch ProcessMatchUnsafe");

        // --- SCENARIO 2: Cross-pod Native 0x0F05 Accept on Pod B + Whisper Accept on Pod A -> MidWars (MatchType=4) ---
        SimulatedChatSession p201 = CreateClientSession(tcpServer, spB, 201, "Player_201");
        SimulatedChatSession p202 = CreateClientSession(tcpServer, spA, 202, "Player_202");
        podBClients[p201.Account.Name] = p201;
        podAClients[p202.Account.Name] = p202;

        MatchmakingGroup g201 = CreateQueuedGroup(podA, p201, ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, "midwars", clock.GetUtcNow().AddSeconds(-40));
        MatchmakingGroup g202 = CreateQueuedGroup(podA, p202, ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, "midwars", clock.GetUtcNow().AddSeconds(-15));

        bool started2 = await podA.ProcessMatchForSimulationAsync([g201, g202]);
        started2.Should().BeTrue();
        PendingReadyCheckState rc2 = podA.PendingReadyChecks.Values.Single();
        rc2.ReadyCheckId.Should().BeGreaterThan(rc1.ReadyCheckId);

        // Player 201 clicks classic Accept Match (0x0F05) routed to failover Pod B -> resolves via FindActiveReadyCheckIdForAccountAsync!
        AcceptPendingMatch accept0F05OnPodB = new(podB);
        ChatBuffer acceptBuf = new();
        acceptBuf.WriteCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH);
        await accept0F05OnPodB.Process(p201, new ChatBuffer(acceptBuf.AsSpan()));

        // Player 202 accepts via SendWhisper on Pod A -> QuorumReached -> allocates gameServerA with MatchType=4!
        SendWhisper whisperOnPodA = new(podAContext.Object, distributedState: null, queueArcadeService: null, matchmakingService: podA);
        ChatBuffer whisperAcceptBuf = new();
        whisperAcceptBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisperAcceptBuf.WriteString(MatchmakingService.ReadyCheckSenderName);
        whisperAcceptBuf.WriteString($"ACCEPT|{rc2.ReadyCheckId}");
        await ((IAsynchronousCommandProcessor<ChatSession>)whisperOnPodA).Process(p202, new ChatBuffer(whisperAcceptBuf.AsSpan()));

        podA.PendingMatches.Should().ContainSingle();
        int matchUpId = podA.PendingMatches.Keys.Single();
        matchUpId.Should().BeGreaterThan(100_000);
        gameServerA.SnapshotPackets().Should().Contain(p =>
            p.Length >= 4 && BitConverter.ToUInt16(p, 2) == ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);

        // --- SCENARIO 3: Failover Pod B confirms FIRST (preserving MatchType=4 & publishing MATCH_CONFIRMED_DISPATCH),
        //                 then Owner Pod A receives duplicate ConfirmMatch and suppresses duplicate 0x0062! ---
        await podB.ConfirmMatch(matchUpId, matchId: 888444, serverAddress: "127.0.0.1", serverPort: 11235);

        // Player 201 on Pod B received MATCH_FOUND_UPDATE + GROUP_QUEUE_UPDATE + 0x0062 with MatchType == 4 (NOT hardcoded 10!)
        List<byte[]> p201Packets = p201.SnapshotPackets();
        p201Packets.Should().Contain(p => p.Length >= 4 && BitConverter.ToUInt16(p, 2) == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE);
        p201Packets.Should().Contain(p => p.Length >= 4 && BitConverter.ToUInt16(p, 2) == ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_QUEUE_UPDATE);
        byte[]? autoConnectP201 = p201Packets.FirstOrDefault(p => p.Length >= 5 && BitConverter.ToUInt16(p, 2) == 0x0062);
        autoConnectP201.Should().NotBeNull();
        autoConnectP201![4].Should().Be(4, "Failover ConfirmMatch MUST preserve serialized MidWars MatchType=4 instead of hardcoding 10");

        // Remote Player 202 (connected to Pod A, not Pod B) received a MATCH_CONFIRMED_DISPATCH stream event!
        IReadOnlyList<DistributedMatchmakingStreamEvent> events = await sharedStore.ReadRecentEventsAsync(30);
        DistributedMatchmakingStreamEvent? dispatchEvt = events.FirstOrDefault(e => e.EventType == "MATCH_CONFIRMED_DISPATCH");
        dispatchEvt.Should().NotBeNull();
        MatchConfirmedDispatchPayload? dispatchPayload = JsonSerializer.Deserialize(
            dispatchEvt!.PayloadJson,
            ValkeyMatchmakingJsonContext.Default.MatchConfirmedDispatchPayload);
        dispatchPayload.Should().NotBeNull();
        dispatchPayload!.MatchType.Should().Be(4);
        dispatchPayload.TargetAccountIds.Should().Contain(202);

        // Duplicate ConfirmMatch arriving at original Owner Pod A MUST be suppressed because Valkey already transitioned to CONFIRMED!
        int p202AutoConnectBefore = p202.SnapshotPackets().Count(p => p.Length >= 4 && BitConverter.ToUInt16(p, 2) == 0x0062);
        await podA.ConfirmMatch(matchUpId, matchId: 888444, serverAddress: "127.0.0.1", serverPort: 11235);
        int p202AutoConnectAfter = p202.SnapshotPackets().Count(p => p.Length >= 4 && BitConverter.ToUInt16(p, 2) == 0x0062);
        p202AutoConnectAfter.Should().Be(p202AutoConnectBefore, "Owner pod must NOT double-send AUTO_MATCH_CONNECT after another pod already confirmed");
    }

    [Fact]
    public async Task GroupLifecycleHooks_SyncAndRemoveDistributedGroupState()
    {
        Log.Initialise(NullLogger.Instance);
        ControllableTimeProvider clock = new(new DateTimeOffset(2026, 11, 19, 14, 0, 0, TimeSpan.Zero));
        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            timeProvider: clock,
            podId: "pod-group-hooks");

        using FloodPreventionService flood = new(NullLogger<FloodPreventionService>.Instance);
        using TCPServer tcpServer = new(IPAddress.Loopback, 0);
        ConcurrentDictionary<string, ChatSession> clients = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<int, ChatSession> servers = new();
        Mock<IChatContext> chatContext = CreateChatContextMock(clients, servers);
        using ServiceProvider sp = CreateSessionServiceProvider(chatContext.Object, flood);

        IConfiguration config = new ConfigurationBuilder().Build();
        using MatchmakingService service = new(config, chatContext.Object, timeProvider: clock, distributedStateStore: store);

        SimulatedChatSession leader = CreateClientSession(tcpServer, sp, 701, "GroupLeader");
        MatchmakingGroup group = CreateQueuedGroup(service, leader, ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL, "caldavar", queueStartTime: null);
        group.BindMatchmakingService(service);
        group.Information.Ranked = true;
        group.Information.ClientVersion = "4.10.1.9";
        group.Information.MatchFidelity = 2;
        group.Information.BotDifficulty = 3;
        group.Information.RandomizeBots = true;

        // 1. JoinQueue triggers distributed group sync
        group.JoinQueue();
        await WaitUntilAsync(async () =>
        {
            IReadOnlyList<DistributedGroupSnapshot> active = await service.ListDistributedActiveGroupsAsync();
            return active.Any(g => g.GroupGuid == group.GUID.ToString("N") && g.QueueStartTimeUtc.HasValue);
        });

        IReadOnlyList<DistributedGroupSnapshot> queuedGroups = await service.ListDistributedActiveGroupsAsync();
        DistributedGroupSnapshot snap = queuedGroups.Should().ContainSingle(g => g.GroupGuid == group.GUID.ToString("N")).Subject;
        snap.Ranked.Should().BeTrue();
        snap.ClientVersion.Should().Be("4.10.1.9");
        snap.MatchFidelity.Should().Be(2);
        snap.BotDifficulty.Should().Be(3);
        snap.RandomizeBots.Should().BeTrue();
        snap.QueueStartTimeUtc.Should().NotBeNull();

        // 2. LeaveQueue updates snapshot with QueueStartTimeUtc = null
        group.LeaveQueue(leader.Account.ID);
        await WaitUntilAsync(async () =>
        {
            IReadOnlyList<DistributedGroupSnapshot> active = await service.ListDistributedActiveGroupsAsync();
            return active.Any(g => g.GroupGuid == group.GUID.ToString("N") && !g.QueueStartTimeUtc.HasValue);
        });

        // 3. RemoveMember disbands empty group -> removes snapshot from Valkey store!
        group.RemoveMember(service, leader.Account.ID);
        await WaitUntilAsync(async () =>
        {
            IReadOnlyList<DistributedGroupSnapshot> active = await service.ListDistributedActiveGroupsAsync();
            return active.All(g => g.GroupGuid != group.GUID.ToString("N"));
        });

        (await service.ListDistributedActiveGroupsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LocalFallback_EnforcesExpiryDeduplicatesRequiredPlayersAndRespectsCancellation()
    {
        ControllableTimeProvider clock = new(new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero));
        ValkeyMatchmakingStateStore store = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: null,
            timeProvider: clock,
            podId: "pod-local-rules");

        DateTimeOffset now = clock.GetUtcNow();

        // 1. Duplicate RequiredAccountIds [10, 10, 20] + pre-populated AcceptedAccountIds [10]
        bool created = await store.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 201001,
                GroupGuids: ["g1"],
                RequiredAccountIds: [10, 10, 20],
                AcceptedAccountIds: [10],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-local-rules",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(15)),
            TimeSpan.FromSeconds(60));
        created.Should().BeTrue();

        (await store.FindActiveReadyCheckIdForAccountAsync(20)).Should().Be(201001);

        DistributedReadyCheckVoteResult vote20 = await store.TrySubmitReadyCheckVoteAsync(201001, 20, accepted: true);
        vote20.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.QuorumReached, "duplicate required IDs must not deadlock quorum and pre-populated accepts must count");
        vote20.AcceptedCount.Should().Be(2);
        vote20.TotalPlayers.Should().Be(2);
        (await store.FindActiveReadyCheckIdForAccountAsync(20)).Should().BeNull("resolved ready check clears account mapping");

        // 2. ReadyCheck expiry enforced on vote after ExpiresAtUtc
        await store.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: 201002,
                GroupGuids: ["g2"],
                RequiredAccountIds: [30, 40],
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-local-rules",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(15)),
            TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(16));
        (await store.FindActiveReadyCheckIdForAccountAsync(30)).Should().BeNull("expired ready check must not appear active");
        DistributedReadyCheckVoteResult lateVote = await store.TrySubmitReadyCheckVoteAsync(201002, 30, accepted: true);
        lateVote.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.Expired);

        // 3. PendingMatch TTL expiry refuses post-TTL confirmation
        DateTimeOffset pmNow = clock.GetUtcNow();
        await store.TryRegisterPendingMatchAsync(
            new DistributedPendingMatchSnapshot(
                MatchUpId: 101002,
                AllocatedServerId: 12,
                ResolvedMapName: "caldavar",
                GroupGuids: ["g3"],
                MemberAccountIds: [50, 60],
                SlotLockKey: null,
                SlotLeaseToken: null,
                OwnerPodId: "pod-local-rules",
                CreatedAtUtc: pmNow),
            TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(31));
        DistributedPendingMatchTransitionResult lateConfirm = await store.TryConfirmPendingMatchAsync(101002, 999, "127.0.0.1", 11235);
        lateConfirm.Transitioned.Should().BeFalse();
        lateConfirm.FinalStatus.Should().Be("EXPIRED");

        // 4. CancellationToken respected immediately
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        Func<Task> canceledAct = async () => await store.ListActiveGroupsAsync(cts.Token);
        await canceledAct.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task LiveValkey_TwoSeparatePodInstances_ExecuteAllAtomicLuaScriptsAndStreams()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled)
        {
            return;
        }

        string endpoint = Environment.GetEnvironmentVariable("KONGOR_VALKEY_TEST_ENDPOINT")
            ?? throw new InvalidOperationException(
                "KONGOR_VALKEY_TEST_ENDPOINT must be set when running Manual live Valkey tests (e.g. '127.0.0.1:6379').");

        string uniquePrefix = $"kongor:mm:test:{Guid.NewGuid():N}";
        using ConnectionMultiplexer muxA = await ConnectionMultiplexer.ConnectAsync(endpoint);
        using ConnectionMultiplexer muxB = await ConnectionMultiplexer.ConnectAsync(endpoint);

        ValkeyMatchmakingStateStore podA = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: muxA,
            podId: "valkey-pod-a",
            keyPrefix: uniquePrefix);

        ValkeyMatchmakingStateStore podB = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: muxB,
            podId: "valkey-pod-b",
            keyPrefix: uniquePrefix);

        podA.IsDistributedActive.Should().BeTrue();
        podB.IsDistributedActive.Should().BeTrue();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 0. Distributed atomic ID allocation produces distinct IDs across Pod A & Pod B
        int rcIdA = await podA.AllocateNextReadyCheckIdAsync();
        int rcIdB = await podB.AllocateNextReadyCheckIdAsync();
        rcIdA.Should().BeGreaterThan(200_000);
        rcIdB.Should().Be(rcIdA + 1);

        int matchUpIdA = await podA.AllocateNextMatchUpIdAsync();
        int matchUpIdB = await podB.AllocateNextMatchUpIdAsync();
        matchUpIdA.Should().BeGreaterThan(100_000);
        matchUpIdB.Should().Be(matchUpIdA + 1);

        // 1. Group written by Pod A is immediately visible on Pod B
        await podA.UpsertGroupAsync(new DistributedGroupSnapshot(
            GroupGuid: "grp-42",
            LeaderAccountId: 1001,
            MemberAccountIds: [1001, 1002],
            MapName: "caldavar",
            GroupType: 1,
            GameType: 1,
            GameModes: ["ap"],
            GameRegions: ["usw"],
            QueueStartTimeUtc: now,
            OwnerPodId: podA.PodId,
            UpdatedAtUtc: now));

        IReadOnlyList<DistributedGroupSnapshot> podBGroups = await podB.ListActiveGroupsAsync();
        podBGroups.Should().ContainSingle(g => g.GroupGuid == "grp-42" && g.LeaderAccountId == 1001);

        // 2. ReadyCheck created by Pod A -> lookup on Pod B -> 10 concurrent votes split across Pod A & Pod B -> exactly 1 QuorumReached
        int[] players = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        bool rcCreated = await podA.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: rcIdA,
                GroupGuids: ["grp-42", "grp-43"],
                RequiredAccountIds: players,
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: podA.PodId,
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));
        rcCreated.Should().BeTrue();

        (await podB.FindActiveReadyCheckIdForAccountAsync(2)).Should().Be(rcIdA);

        Task<DistributedReadyCheckVoteResult>[] voteTasks = players.Select(p =>
            (p % 2 == 0 ? podB : podA).TrySubmitReadyCheckVoteAsync(rcIdA, p, accepted: true)).ToArray();
        DistributedReadyCheckVoteResult[] voteResults = await Task.WhenAll(voteTasks);

        voteResults.Count(r => r.Outcome == DistributedReadyCheckVoteOutcome.QuorumReached).Should().Be(1);
        voteResults.Count(r => r.Outcome == DistributedReadyCheckVoteOutcome.Accepted).Should().Be(9);
        (await podB.FindActiveReadyCheckIdForAccountAsync(2)).Should().BeNull();

        // 3. ReadyCheck expire test with SDIFF missing players
        await podA.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: rcIdB,
                GroupGuids: ["grp-44"],
                RequiredAccountIds: [50, 60, 70],
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: podA.PodId,
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));
        await podB.TrySubmitReadyCheckVoteAsync(rcIdB, 60, accepted: true);
        DistributedReadyCheckVoteResult expRes = await podB.TryExpireReadyCheckAsync(rcIdB);
        expRes.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.Expired);
        expRes.MissingAccountIds.Should().Equal(50, 70);

        // 4. PendingMatch registered by Pod A -> confirmed atomically by Pod B after Pod A "dies"
        const string valkeyLeaseId = "lease-valkey";
        bool pmReg = await podA.TryRegisterPendingMatchAsync(
            new DistributedPendingMatchSnapshot(
                MatchUpId: matchUpIdA,
                AllocatedServerId: 9,
                ResolvedMapName: "midwars",
                GroupGuids: ["grp-42", "grp-43"],
                MemberAccountIds: players,
                SlotLockKey: "lock:server_slot:usw:127.0.0.1:11235",
                SlotLeaseToken: valkeyLeaseId,
                OwnerPodId: podA.PodId,
                CreatedAtUtc: now,
                MatchType: 4),
            TimeSpan.FromMinutes(2));
        pmReg.Should().BeTrue();

        Task<DistributedPendingMatchTransitionResult> c1 = podB.TryConfirmPendingMatchAsync(matchUpIdA, 88888, "127.0.0.1", 11235);
        Task<DistributedPendingMatchTransitionResult> e1 = podA.TryExpirePendingMatchAsync(matchUpIdA);
        DistributedPendingMatchTransitionResult[] pmOutcomes = await Task.WhenAll(c1, e1);
        pmOutcomes.Count(o => o.Transitioned).Should().Be(1);

        // 5. Verify Valkey Streams audit log contains events from both Pod A & Pod B
        IReadOnlyList<DistributedMatchmakingStreamEvent> streamEvents = await podB.ReadRecentEventsAsync(50);
        streamEvents.Should().Contain(e => e.EventType == "group_upserted" && e.PodId == "valkey-pod-a");
        streamEvents.Should().Contain(e => e.EventType == "readycheck_quorum_reached");
        streamEvents.Should().Contain(e => e.EventType == "readycheck_expired");
        streamEvents.Should().Contain(e => e.EventType == "pending_match_registered");

        await podB.RemoveGroupAsync("grp-42");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _sync = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string formatted = formatter(state, exception);
            lock (_sync)
            {
                _entries.Add((logLevel, formatted));
            }
        }

        public List<(LogLevel Level, string Message)> Snapshot()
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    [Fact]
    public async Task ChatServerDi_DefaultsToSinglePodLocalMode_AndFormatsStartupRecoveryLogsWithoutLiteralPlaceholders()
    {
        string? prevEnv = Environment.GetEnvironmentVariable("KONGOR_MM_DISTRIBUTED_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_MM_DISTRIBUTED_ENABLED", null);

            Mock<IDatabase> dbMock = new();
            dbMock
                .Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync([]);

            Mock<IConnectionMultiplexer> muxMock = new();
            muxMock.SetupGet(m => m.IsConnected).Returns(true);
            muxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(dbMock.Object);

            // 1. Default 5.0.2 DI (Matchmaking:DistributedState:Enabled unset / false) -> single-pod local mode even when Redis is connected
            IConfiguration defaultConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            ServiceCollection defaultServices = new();
            defaultServices.AddLogging();
            defaultServices.AddSingleton<IConfiguration>(defaultConfig);
            defaultServices.AddSingleton<IConnectionMultiplexer>(muxMock.Object);
            defaultServices.AddChatServerServices();

            using ServiceProvider defaultProvider = defaultServices.BuildServiceProvider();
            IDistributedMatchmakingStateStore defaultStore = defaultProvider.GetRequiredService<IDistributedMatchmakingStateStore>();
            defaultStore.IsDistributedActive.Should().BeFalse("Matchmaking:DistributedState:Enabled must default to false (single-pod local mode) in 5.0.2");

            // 2. Explicit opt-in (Matchmaking:DistributedState:Enabled = true) -> distributed Valkey mode active
            IConfiguration optInConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Matchmaking:DistributedState:Enabled"] = "true",
                    ["Matchmaking:DistributedState:PodId"] = "pod-optin-01",
                    ["Matchmaking:IntervalInMilliseconds"] = "60000"
                })
                .Build();

            ServiceCollection optInServices = new();
            optInServices.AddLogging();
            optInServices.AddSingleton<IConfiguration>(optInConfig);
            optInServices.AddSingleton<IConnectionMultiplexer>(muxMock.Object);
            optInServices.AddChatServerServices();

            using ServiceProvider optInProvider = optInServices.BuildServiceProvider();
            IDistributedMatchmakingStateStore optInStore = optInProvider.GetRequiredService<IDistributedMatchmakingStateStore>();
            optInStore.IsDistributedActive.Should().BeTrue("explicit Matchmaking:DistributedState:Enabled=true must wire connected Valkey multiplexer");
            optInStore.PodId.Should().Be("pod-optin-01");

            // 3. Verify MatchmakingService startup recovery log formats positional values without literal {PodId} or {ActiveCount} brackets
            CapturingLogger<MatchmakingService> captureLogger = new();
            Log.Initialise(captureLogger);
            try
            {
                Mock<IChatContext> ctxMock = CreateChatContextMock(
                    new ConcurrentDictionary<string, ChatSession>(StringComparer.OrdinalIgnoreCase),
                    new ConcurrentDictionary<int, ChatSession>());
                using MatchmakingService mmService = new(optInConfig, ctxMock.Object, logger: captureLogger, distributedStateStore: optInStore);
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
                await mmService.StartAsync(cts.Token);
                await WaitUntilAsync(() => Task.FromResult(
                    captureLogger.Snapshot().Any(e => e.Message.Contains("[Matchmaking][DistributedRecovery]", StringComparison.Ordinal))));
                await mmService.StopAsync(CancellationToken.None);

                List<(LogLevel Level, string Message)> logs = captureLogger.Snapshot();
                string recoveryLine = logs
                    .Select(e => e.Message)
                    .First(m => m.Contains("[Matchmaking][DistributedRecovery]", StringComparison.Ordinal));
                recoveryLine.Should().Contain("Pod=pod-optin-01 observed 0 active group snapshot(s) in Valkey on startup (Queued=0).");
                recoveryLine.Should().NotContain("{PodId}");
                recoveryLine.Should().NotContain("{ActiveCount}");
            }
            finally
            {
                Log.Initialise(NullLogger.Instance);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_MM_DISTRIBUTED_ENABLED", prevEnv);
        }
    }

    [Fact]
    public async Task GarnetIncompatibleCache_UnknownCommandXinfoOrXadd_LogsCriticalAndAutoDegradesToSinglePodLocalMode()
    {
        // 1. Startup/lazy XINFO STREAM capability probe against Garnet ('ERR unknown command') -> degrades cleanly to local mode
        CapturingLogger<ValkeyMatchmakingStateStore> probeLogger = new();
        Mock<IDatabase> garnetProbeDb = new();
        garnetProbeDb
            .Setup(db => db.ExecuteAsync("XINFO", It.IsAny<object[]>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'XINFO'"));

        Mock<IConnectionMultiplexer> garnetProbeMux = new();
        garnetProbeMux.SetupGet(m => m.IsConnected).Returns(true);
        garnetProbeMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(garnetProbeDb.Object);

        ValkeyMatchmakingStateStore probeStore = new(
            probeLogger,
            redis: garnetProbeMux.Object,
            podId: "pod-garnet-probe");

        probeStore.IsDistributedActive.Should().BeTrue();
        IReadOnlyList<DistributedGroupSnapshot> initialGroups = await probeStore.ListActiveGroupsAsync();
        initialGroups.Should().BeEmpty();
        probeStore.IsDegradedToLocalMode.Should().BeTrue();
        probeStore.IsDistributedActive.Should().BeFalse();
        probeLogger.Snapshot().Should().ContainSingle(e =>
            e.Level == LogLevel.Critical &&
            e.Message.Contains("[VALKEY-MM-INCOMPATIBLE]", StringComparison.Ordinal) &&
            e.Message.Contains("docker compose -f infra/docker/docker-compose.infra.yml up -d valkey", StringComparison.Ordinal));

        // 2. Direct player queue & ready-check action hitting Garnet 'ERR unknown command XADD' -> auto-degrades without crashing player queue
        CapturingLogger<ValkeyMatchmakingStateStore> queueLogger = new();
        Mock<IDatabase> garnetXaddDb = new();
        garnetXaddDb
            .Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        garnetXaddDb
            .Setup(db => db.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'XADD'"));
        garnetXaddDb
            .Setup(db => db.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'XADD'"));
        garnetXaddDb
            .Setup(db => db.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]?>(), It.IsAny<RedisValue[]?>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR Error running script: @user_script:15: ERR unknown command 'XADD'"));

        Mock<IConnectionMultiplexer> garnetXaddMux = new();
        garnetXaddMux.SetupGet(m => m.IsConnected).Returns(true);
        garnetXaddMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(garnetXaddDb.Object);

        ValkeyMatchmakingStateStore queueStore = new(
            queueLogger,
            redis: garnetXaddMux.Object,
            podId: "pod-garnet-xadd");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DistributedGroupSnapshot queuedGroup = new(
            GroupGuid: "grp-garnet-local-1",
            LeaderAccountId: 501,
            MemberAccountIds: [501, 502],
            MapName: "caldavar",
            GroupType: 1,
            GameType: 1,
            GameModes: ["ap"],
            GameRegions: ["use"],
            QueueStartTimeUtc: now,
            OwnerPodId: "pod-garnet-xadd",
            UpdatedAtUtc: now);

        await queueStore.UpsertGroupAsync(queuedGroup);
        queueStore.IsDegradedToLocalMode.Should().BeTrue();
        queueStore.IsDistributedActive.Should().BeFalse();
        queueLogger.Snapshot().Should().ContainSingle(e =>
            e.Level == LogLevel.Critical &&
            e.Message.Contains("[VALKEY-MM-INCOMPATIBLE]", StringComparison.Ordinal));

        IReadOnlyList<DistributedGroupSnapshot> activeLocalGroups = await queueStore.ListActiveGroupsAsync();
        activeLocalGroups.Should().ContainSingle(g => g.GroupGuid == "grp-garnet-local-1");

        int localRcId = await queueStore.AllocateNextReadyCheckIdAsync();
        bool rcCreated = await queueStore.TryCreateReadyCheckAsync(
            new DistributedReadyCheckSnapshot(
                ReadyCheckId: localRcId,
                GroupGuids: ["grp-garnet-local-1"],
                RequiredAccountIds: [501, 502],
                AcceptedAccountIds: [],
                DeclinedByAccountId: null,
                Status: "OPEN",
                OwnerPodId: "pod-garnet-xadd",
                CreatedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(20)),
            TimeSpan.FromSeconds(60));
        rcCreated.Should().BeTrue();

        DistributedReadyCheckVoteResult v1 = await queueStore.TrySubmitReadyCheckVoteAsync(localRcId, 501, accepted: true);
        DistributedReadyCheckVoteResult v2 = await queueStore.TrySubmitReadyCheckVoteAsync(localRcId, 502, accepted: true);
        v1.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.Accepted);
        v2.Outcome.Should().Be(DistributedReadyCheckVoteOutcome.QuorumReached);

        // 3. Non-'unknown command' Redis script errors (e.g. WRONGTYPE) MUST continue failing loud with DistributedMatchmakingStoreException
        Mock<IDatabase> wrongTypeDb = new();
        wrongTypeDb
            .Setup(db => db.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]?>(), It.IsAny<RedisValue[]?>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value"));

        Mock<IConnectionMultiplexer> wrongTypeMux = new();
        wrongTypeMux.SetupGet(m => m.IsConnected).Returns(true);
        wrongTypeMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(wrongTypeDb.Object);

        ValkeyMatchmakingStateStore wrongTypeStore = new(
            NullLogger<ValkeyMatchmakingStateStore>.Instance,
            redis: wrongTypeMux.Object,
            podId: "pod-wrongtype");

        Func<Task> wrongTypeAct = async () => await wrongTypeStore.TrySubmitReadyCheckVoteAsync(200999, 501, accepted: true);
        DistributedMatchmakingStoreException wrongTypeEx = (await wrongTypeAct.Should().ThrowAsync<DistributedMatchmakingStoreException>()).Which;
        wrongTypeEx.FailureKind.Should().Be(DistributedMatchmakingStoreFailureKind.RedisCommandError);
    }

    private static Mock<IChatContext> CreateChatContextMock(
        ConcurrentDictionary<string, ChatSession> clients,
        ConcurrentDictionary<int, ChatSession> servers)
    {
        Mock<IChatContext> mock = new();
        mock.SetupGet(c => c.ClientChatSessions).Returns(clients);
        mock.SetupGet(c => c.MatchServerChatSessions).Returns(servers);
        mock.SetupGet(c => c.MatchServerManagerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());
        mock.SetupGet(c => c.ChatChannels).Returns(new ConcurrentDictionary<string, ChatChannel>(StringComparer.OrdinalIgnoreCase));
        mock.SetupGet(c => c.ChatChannelsByID).Returns(new ConcurrentDictionary<int, ChatChannel>());
        return mock;
    }

    private static ServiceProvider CreateSessionServiceProvider(IChatContext chatContext, FloodPreventionService flood)
    {
        ServiceCollection services = new();
        services.AddSingleton(chatContext);
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(Mock.Of<IMatchmakingService>());
        services.AddSingleton(flood);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        return services.BuildServiceProvider();
    }

    private static SimulatedChatSession CreateClientSession(
        TCPServer tcpServer,
        IServiceProvider sp,
        int accountId,
        string name)
    {
        SimulatedChatSession session = new(tcpServer, sp)
        {
            Account = new Account
            {
                ID = accountId,
                Name = name,
                User = new User
                {
                    ID = accountId,
                    EmailAddress = $"{name}@kongor.test",
                    Role = new DbRole { ID = 1, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ClientMetadata = new ClientChatSessionMetadata
            {
                RemoteIP = "127.0.0.1",
                SessionCookie = $"cookie_{accountId}",
                SessionAuthenticationHash = $"auth_{accountId}",
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                OperatingSystemIdentifier = 1,
                OperatingSystemVersionMajor = 10,
                OperatingSystemVersionMinor = 0,
                OperatingSystemVersionPatch = 0,
                OperatingSystemBuildCode = "Windows",
                OperatingSystemArchitecture = "x64",
                ClientVersionMajor = 4,
                ClientVersionMinor = 10,
                ClientVersionPatch = 1,
                ClientVersionRevision = 0,
                LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
                ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
                ClientRegion = "USE",
                ClientLanguage = "en"
            }
        };
        session.MarkAuthenticated(ChatPeerRole.Client);
        return session;
    }

    private static SimulatedChatSession CreateGameServerSession(
        TCPServer tcpServer,
        IServiceProvider sp,
        int serverId,
        string location)
    {
        SimulatedChatSession session = new(tcpServer, sp)
        {
            Account = new Account
            {
                ID = 9000 + serverId,
                Name = $"HOST_{serverId}",
                User = new User
                {
                    ID = 9000 + serverId,
                    EmailAddress = $"host_{serverId}@kongor.test",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = serverId,
                SessionCookie = $"srv_cookie_{serverId}",
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Address = "127.0.0.1",
                Port = 11235,
                LocalAddress = "127.0.0.1",
                LocalPort = 11235,
                Location = location,
                MapName = "caldavar",
                GameName = $"GameServer_{serverId}",
                IsQueueArcade = false
            }
        };
        session.MarkAuthenticated(ChatPeerRole.MatchServer);
        return session;
    }

    private static MatchmakingGroup CreateQueuedGroup(
        MatchmakingService service,
        SimulatedChatSession leaderSession,
        ChatProtocol.TMMGameType gameType,
        string mapName,
        DateTimeOffset? queueStartTime)
    {
        MatchmakingGroupMember member = new(leaderSession)
        {
            Slot = 1,
            IsLeader = true,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = 1500.0
        };

        MatchmakingGroup group = new()
        {
            Members = [member],
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = gameType,
                MapName = mapName,
                GameModes = ["ap"],
                GameRegions = ["USE"],
                Ranked = true,
                MatchFidelity = 0,
                BotDifficulty = 0,
                RandomizeBots = false
            },
            QueueStartTime = queueStartTime
        };

        service.Groups[leaderSession.Account.ID] = group;
        return group;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, int timeoutMs = 2000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(15);
        }

        (await predicate()).Should().BeTrue("expected asynchronous group state sync condition to complete within timeout");
    }
}
