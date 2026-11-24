// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using DAWNBRINGER.WebPortal.UI.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class FakeRedisBackingStore
{
    public ConcurrentDictionary<string, RedisValue> KeyValueStore { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, HashSet<string>> SetStore { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Action<RedisChannel, RedisValue>> Subscribers { get; } = new();
    public bool DeliverPubSub { get; set; } = false;

    public IConnectionMultiplexer CreateMultiplexer()
    {
        var mockMux = new Mock<IConnectionMultiplexer>();
        var mockDb = new Mock<IDatabase>();
        var mockSub = new Mock<ISubscriber>();

        mockMux.Setup(m => m.IsConnected).Returns(true);
        mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockDb.Object);
        mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), null)).Returns(mockDb.Object);
        mockMux.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns(mockSub.Object);
        mockMux.Setup(m => m.GetSubscriber(null)).Returns(mockSub.Object);

        mockDb.Setup(d => d.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, CommandFlags _) =>
            {
                string key = k.ToString();
                return KeyValueStore.TryGetValue(key, out RedisValue val) ? val : RedisValue.Null;
            });

        mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, CommandFlags _) =>
            {
                string key = k.ToString();
                RedisValue val = KeyValueStore.TryGetValue(key, out RedisValue v) ? v : RedisValue.Null;
                return Task.FromResult(val);
            });

        mockDb.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, Expiration _, ValueCondition __, CommandFlags ___) =>
            {
                KeyValueStore[k.ToString()] = v;
                return true;
            });

        mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, Expiration _, ValueCondition __, CommandFlags ___) =>
            {
                KeyValueStore[k.ToString()] = v;
                return Task.FromResult(true);
            });

        mockDb.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .Returns((RedisKey k, RedisValue v, TimeSpan? _, When __) =>
            {
                KeyValueStore[k.ToString()] = v;
                return true;
            });

        mockDb.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, TimeSpan? _, When __, CommandFlags ___) =>
            {
                KeyValueStore[k.ToString()] = v;
                return true;
            });

        mockDb.Setup(d => d.StringSet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, TimeSpan? _, bool __, When ___, CommandFlags ____) =>
            {
                KeyValueStore[k.ToString()] = v;
                return true;
            });

        mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .Returns((RedisKey k, RedisValue v, TimeSpan? _, When __) =>
            {
                KeyValueStore[k.ToString()] = v;
                return Task.FromResult(true);
            });

        mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, TimeSpan? _, When __, CommandFlags ___) =>
            {
                KeyValueStore[k.ToString()] = v;
                return Task.FromResult(true);
            });

        mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, TimeSpan? _, bool __, When ___, CommandFlags ____) =>
            {
                KeyValueStore[k.ToString()] = v;
                return Task.FromResult(true);
            });

        mockDb.Setup(d => d.KeyDelete(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, CommandFlags flags) => KeyValueStore.TryRemove(k.ToString(), out RedisValue _));

        mockDb.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, CommandFlags flags) => Task.FromResult(KeyValueStore.TryRemove(k.ToString(), out RedisValue _)));

        mockDb.Setup(d => d.SetAdd(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, CommandFlags _) =>
            {
                var set = SetStore.GetOrAdd(k.ToString(), _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set) { return set.Add(v.ToString()); }
            });

        mockDb.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, CommandFlags _) =>
            {
                var set = SetStore.GetOrAdd(k.ToString(), _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                bool added;
                lock (set) { added = set.Add(v.ToString()); }
                return Task.FromResult(added);
            });

        mockDb.Setup(d => d.SetRemove(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, CommandFlags _) =>
            {
                if (SetStore.TryGetValue(k.ToString(), out var set))
                {
                    lock (set) { return set.Remove(v.ToString()); }
                }
                return false;
            });

        mockDb.Setup(d => d.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, RedisValue v, CommandFlags _) =>
            {
                bool removed = false;
                if (SetStore.TryGetValue(k.ToString(), out var set))
                {
                    lock (set) { removed = set.Remove(v.ToString()); }
                }
                return Task.FromResult(removed);
            });

        mockDb.Setup(d => d.SetMembers(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey k, CommandFlags _) =>
            {
                if (SetStore.TryGetValue(k.ToString(), out var set))
                {
                    lock (set)
                    {
                        return set.Select(s => (RedisValue)s).ToArray();
                    }
                }
                return Array.Empty<RedisValue>();
            });

        mockSub.Setup(s => s.Subscribe(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, __) =>
            {
                lock (Subscribers)
                {
                    Subscribers.Add(handler);
                }
            });

        mockSub.Setup(s => s.Publish(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisChannel ch, RedisValue val, CommandFlags _) =>
            {
                if (DeliverPubSub)
                {
                    List<Action<RedisChannel, RedisValue>> handlers;
                    lock (Subscribers) { handlers = Subscribers.ToList(); }
                    foreach (var h in handlers) { h(ch, val); }
                }
                return 1;
            });

        mockSub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisChannel ch, RedisValue val, CommandFlags _) =>
            {
                if (DeliverPubSub)
                {
                    List<Action<RedisChannel, RedisValue>> handlers;
                    lock (Subscribers) { handlers = Subscribers.ToList(); }
                    foreach (var h in handlers) { h(ch, val); }
                }
                return Task.FromResult(1L);
            });

        return mockMux.Object;
    }
}

public class DistributedRoomManagerTests
{
    [Fact]
    public void DevoWars_CrossReplica_SplitBrainPrevention_WithoutPubSub()
    {
        // Arrange: Pub/sub explicitly disabled to simulate cold cache or newly booted replica
        var store = new FakeRedisBackingStore { DeliverPubSub = false };
        var replicaA = new DevoWarsRoomManager(NullLogger<DevoWarsRoomManager>.Instance, store.CreateMultiplexer());
        var replicaB = new DevoWarsRoomManager(NullLogger<DevoWarsRoomManager>.Instance, store.CreateMultiplexer());

        // Act 1: Player 1 joins on replicaA
        var (roomA, slot1, started1) = replicaA.JoinRoom("HON1", "conn1", "DevoPlayer1", "p1");
        slot1.Should().Be(1);
        started1.Should().BeFalse();
        roomA.Player1.Should().NotBeNull();
        roomA.Player1!.Name.Should().Be("DevoPlayer1");
        roomA.Player2.Should().BeNull();

        // Assert 1: replicaB (cold cache) hydrates Player 1 from Redis IDatabase
        DevoRoom? roomBHydrated = replicaB.GetRoom("HON1");
        roomBHydrated.Should().NotBeNull();
        roomBHydrated!.Player1.Should().NotBeNull();
        roomBHydrated.Player1!.Name.Should().Be("DevoPlayer1");

        // Act 2: Player 2 joins on replicaB
        var (roomB, slot2, started2) = replicaB.JoinRoom("HON1", "conn2", "DevoPlayer2", "p2");
        slot2.Should().Be(2);
        started2.Should().BeTrue();
        roomB.Status.Should().Be(DevoMatchStatus.WaitingForReady);
        roomB.Player1!.Name.Should().Be("DevoPlayer1");
        roomB.Player2!.Name.Should().Be("DevoPlayer2");

        // Assert 2: replicaA sees unified 2-player state when querying GetRoom
        DevoRoom? unifiedRoomA = replicaA.GetRoom("HON1");
        unifiedRoomA.Should().NotBeNull();
        unifiedRoomA!.Player1!.Name.Should().Be("DevoPlayer1");
        unifiedRoomA.Player2!.Name.Should().Be("DevoPlayer2");
        unifiedRoomA.Status.Should().Be(DevoMatchStatus.WaitingForReady);

        // Act 3: Ready checks across replicas
        var (_, p1Ready) = replicaA.SetPlayerReady("HON1", "conn1", false);
        p1Ready.Should().BeFalse();

        var (startedRoomB, bothReady) = replicaB.SetPlayerReady("HON1", "conn2", false);
        bothReady.Should().BeTrue();
        startedRoomB!.Status.Should().Be(DevoMatchStatus.Playing);

        // Act 4: Score registration across replicas
        var (scoreRoomA, won, winner) = replicaA.RegisterScore("HON1", 1, winningThreshold: 5);
        won.Should().BeFalse();
        winner.Should().Be(0);
        scoreRoomA!.Score1.Should().Be(1);

        replicaB.GetRoom("HON1")!.Score1.Should().Be(1);
    }

    [Fact]
    public void MonkeyCheckers_CrossReplica_SplitBrainPrevention_WithoutPubSub()
    {
        var store = new FakeRedisBackingStore { DeliverPubSub = false };
        var replicaA = new MonkeyCheckersRoomManager(NullLogger<MonkeyCheckersRoomManager>.Instance, store.CreateMultiplexer());
        var replicaB = new MonkeyCheckersRoomManager(NullLogger<MonkeyCheckersRoomManager>.Instance, store.CreateMultiplexer());

        // Player 1 joins on replicaA (Slot 1: Legion)
        var (roomA, slot1, bothInRoom1) = replicaA.JoinRoom("CHK1", "c1", "LegionPlayer", "p1");
        slot1.Should().Be(1);
        bothInRoom1.Should().BeFalse();

        // replicaB hydrates Player 1 and accepts Player 2 (Slot 2: Hellbourne)
        var (roomB, slot2, bothInRoom2) = replicaB.JoinRoom("CHK1", "c2", "HellbournePlayer", "p2");
        slot2.Should().Be(2);
        bothInRoom2.Should().BeTrue();
        roomB.Status.Should().Be(CheckersMatchStatus.WaitingForReady);

        // replicaA sees both players
        CheckersRoom? unifiedA = replicaA.GetRoom("CHK1");
        unifiedA.Should().NotBeNull();
        unifiedA!.Player1!.Name.Should().Be("LegionPlayer");
        unifiedA.Player2!.Name.Should().Be("HellbournePlayer");

        // Ready and move
        replicaA.SetPlayerReady("CHK1", "c1", false);
        var (readyRoom, bothReady) = replicaB.SetPlayerReady("CHK1", "c2", false);
        bothReady.Should().BeTrue();
        readyRoom!.Status.Should().Be(CheckersMatchStatus.Playing);
        readyRoom.CurrentTurn.Should().Be("legion");

        var (moveRoom, isValid, nextTurn) = replicaA.RegisterMove("CHK1", "c1", 2, 1, 3, 2, false);
        isValid.Should().BeTrue();
        nextTurn.Should().Be("hellbourne");

        replicaB.GetRoom("CHK1")!.CurrentTurn.Should().Be("hellbourne");
    }

    [Fact]
    public void Chess_CrossReplica_SplitBrainPrevention_WithoutPubSub()
    {
        var store = new FakeRedisBackingStore { DeliverPubSub = false };
        var replicaA = new ChessRoomManager(NullLogger<ChessRoomManager>.Instance, store.CreateMultiplexer());
        var replicaB = new ChessRoomManager(NullLogger<ChessRoomManager>.Instance, store.CreateMultiplexer());

        // Player 1 joins on replicaA (Slot 1: White)
        var (roomA, slot1, both1) = replicaA.JoinRoom("CHS1", "c1", "WhitePlayer", "p1");
        slot1.Should().Be(1);
        both1.Should().BeFalse();

        // replicaB hydrates Player 1 and joins Player 2 (Slot 2: Black)
        var (roomB, slot2, both2) = replicaB.JoinRoom("CHS1", "c2", "BlackPlayer", "p2");
        slot2.Should().Be(2);
        both2.Should().BeTrue();
        roomB.Status.Should().Be(ChessMatchStatus.WaitingForReady);

        // replicaA sees both players
        ChessRoom? unifiedA = replicaA.GetRoom("CHS1");
        unifiedA.Should().NotBeNull();
        unifiedA!.Player1!.Name.Should().Be("WhitePlayer");
        unifiedA.Player2!.Name.Should().Be("BlackPlayer");

        // Ready and move
        replicaA.SetPlayerReady("CHS1", "c1", false);
        var (readyRoom, bothReady) = replicaB.SetPlayerReady("CHS1", "c2", false);
        bothReady.Should().BeTrue();
        readyRoom!.Status.Should().Be(ChessMatchStatus.Playing);
        readyRoom.CurrentTurn.Should().Be("w");

        var (moveRoom, valid, turn, _, _, timeout) = replicaA.RegisterMove(
            "CHS1", "c1", "e2", "e4", null,
            "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1");

        valid.Should().BeTrue();
        turn.Should().Be("b");
        timeout.Should().BeFalse();

        replicaB.GetRoom("CHS1")!.CurrentTurn.Should().Be("b");
    }

    [Fact]
    public void CrossReplica_DisconnectCleanup_AndReverseIndex()
    {
        var store = new FakeRedisBackingStore { DeliverPubSub = false };
        var replicaA = new DevoWarsRoomManager(NullLogger<DevoWarsRoomManager>.Instance, store.CreateMultiplexer());
        var replicaB = new DevoWarsRoomManager(NullLogger<DevoWarsRoomManager>.Instance, store.CreateMultiplexer());

        replicaA.JoinRoom("DISC1", "connA", "PlayerA");
        replicaB.JoinRoom("DISC1", "connB", "PlayerB");

        // Player B disconnects via replicaB
        var (roomAfterLeave, vacatedSlot) = replicaB.Leave("connB");
        vacatedSlot.Should().Be(2);
        roomAfterLeave!.Player2.Should().BeNull();
        roomAfterLeave.Status.Should().Be(DevoMatchStatus.WaitingForOpponent);

        // replicaA observes slot 2 vacated
        var roomA = replicaA.GetRoom("DISC1");
        roomA.Should().NotBeNull();
        roomA!.Player2.Should().BeNull();
        roomA.Status.Should().Be(DevoMatchStatus.WaitingForOpponent);

        // Player A disconnects via replicaA -> room is empty and purged
        var (emptyRoom, vacatedSlotA) = replicaA.Leave("connA");
        vacatedSlotA.Should().Be(1);

        replicaB.GetRoom("DISC1").Should().BeNull();
        replicaA.GetRoom("DISC1").Should().BeNull();
    }

    [Fact]
    public void GracefulOfflineFallback_WhenRedisIsNull_SingleNodeMemoryMode()
    {
        // DevoWars with null Redis
        var devo = new DevoWarsRoomManager(NullLogger<DevoWarsRoomManager>.Instance, redis: null);
        var (dRoom, dSlot1, _) = devo.JoinRoom("DEV1", "c1", "P1");
        dSlot1.Should().Be(1);
        var (_, dSlot2, bothD) = devo.JoinRoom("DEV1", "c2", "P2");
        dSlot2.Should().Be(2);
        bothD.Should().BeTrue();
        devo.SetPlayerReady("DEV1", "c1", false);
        var (_, bothDReady) = devo.SetPlayerReady("DEV1", "c2", false);
        bothDReady.Should().BeTrue();
        var (scoreRoom, won, _) = devo.RegisterScore("DEV1", 1, 10);
        scoreRoom!.Score1.Should().Be(1);
        var resetDevo = devo.ResetMatch("DEV1");
        resetDevo!.Score1.Should().Be(0);
        var (leftRoom, vacatedD) = devo.Leave("c2");
        vacatedD.Should().Be(2);
        leftRoom!.Player2.Should().BeNull();
        devo.Leave("c1");
        devo.GetRoom("DEV1").Should().BeNull();

        // MonkeyCheckers with null Redis
        var checkers = new MonkeyCheckersRoomManager(NullLogger<MonkeyCheckersRoomManager>.Instance, redis: null);
        var (cRoom, cSlot1, _) = checkers.JoinRoom("CHK1", "c1", "P1");
        cSlot1.Should().Be(1);
        var (_, cSlot2, bothC) = checkers.JoinRoom("CHK1", "c2", "P2");
        cSlot2.Should().Be(2);
        bothC.Should().BeTrue();
        checkers.SetPlayerReady("CHK1", "c1", false);
        var (_, bothCReady) = checkers.SetPlayerReady("CHK1", "c2", false);
        bothCReady.Should().BeTrue();
        var (moveRoom, valid, turn) = checkers.RegisterMove("CHK1", "c1", 2, 1, 3, 2, false);
        valid.Should().BeTrue();
        turn.Should().Be("hellbourne");
        checkers.Leave("c1");
        checkers.Leave("c2");
        checkers.GetRoom("CHK1").Should().BeNull();

        // Chess with null Redis
        var chess = new ChessRoomManager(NullLogger<ChessRoomManager>.Instance, redis: null);
        var (chRoom, chSlot1, _) = chess.JoinRoom("CHS1", "c1", "P1");
        chSlot1.Should().Be(1);
        var (_, chSlot2, bothCh) = chess.JoinRoom("CHS1", "c2", "P2");
        chSlot2.Should().Be(2);
        bothCh.Should().BeTrue();
        chess.SetPlayerReady("CHS1", "c1", false);
        var (_, bothChReady) = chess.SetPlayerReady("CHS1", "c2", false);
        bothChReady.Should().BeTrue();
        var (chMove, chValid, chTurn, _, _, _) = chess.RegisterMove(
            "CHS1", "c1", "e2", "e4", null,
            "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1");
        chValid.Should().BeTrue();
        chTurn.Should().Be("b");
        chess.Leave("c1");
        chess.Leave("c2");
        chess.GetRoom("CHS1").Should().BeNull();
    }
}
