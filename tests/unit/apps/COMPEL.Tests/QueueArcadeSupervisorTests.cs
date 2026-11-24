// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: QueueArcadeSupervisorTests.cs
// System: COMPEL Tests - Queue Arcade Dedicated Worker CVar & Empty-Lobby Immortality Verification
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class QueueArcadeSupervisorTests
{
    [Fact]
    public void DedicatedWorkerArguments_WhenQueueArcadeModeEnabled_InjectsZeroPenaltyCvarsAndPrefix()
    {
        // protects: DedicatedWorkerArguments QueueArcadeMode CVar injection (0 AFK timeout, disabled replay/stats/votekick,
        //           and automatic "QUEUE ARCADE" server name prefix on PortRangeOffset 250).
        // fails_when: Queue Arcade workers inherit AFK kicks, write unnecessary .honreplay WAL files, submit Ranked stats,
        //             or omit the "QUEUE ARCADE" prefix used by ChatServer detection.
        // seam: Real DedicatedWorkerArguments.Build + PortPlan with PortRangeOffset = 250.
        string syntheticCredential = Guid.NewGuid().ToString("N");
        MatchServerManagerOptions options = new()
        {
            UserName = "KONGOR",
            Password = syntheticCredential,
            Instances = 1,
            Gateway = "127.0.0.1",
            Location = "USE",
            ServerNamePrefix = "BATTLEGROUNDS",
            UseProxy = true,
            PortRangeOffset = 250,
            QueueArcadeMode = true,
            QueueArcadeRoundDurationSeconds = 600,
            RuntimeArtefactsPath = "DEFAULT"
        };

        PortPlan plan = new(options);
        plan.LocalGameStart.Should().Be(11485);
        plan.PublicGameStart.Should().Be(21485);

        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: plan.LocalGameStart,
            voicePort: plan.LocalVoiceStart,
            publicGamePort: plan.PublicGameStart,
            publicVoicePort: plan.PublicVoiceStart,
            serverName: $"{options.ServerNamePrefix} #1");

        string[] args = DedicatedWorkerArguments.Build(
            worker,
            options,
            plan,
            serverAddress: "127.0.0.1",
            masterServerHostAndPort: "127.0.0.1:55555");

        string joined = string.Join(' ', args);
        joined.Should().Contain("QUEUE ARCADE BATTLEGROUNDS #1");
        joined.Should().Contain("sv_afkTimeout 0");
        joined.Should().Contain("sv_afkWarningTime 0");
        joined.Should().Contain("svr_recordReplay false");
        joined.Should().Contain("g_voteKickCooldownTime 99999999");
        joined.Should().Contain("svr_submitMatchStatItems false");
        joined.Should().Contain("svr_submitMatchStatAbilities false");
        joined.Should().Contain("svr_submitMatchStatFrags false");
    }

    [Fact]
    public void ShouldReapEmptyLobby_WhenQueueArcadeModeEnabled_NeverReapsZeroHumanArcadeServer()
    {
        // protects: MatchServerManagerSupervisor.ShouldReapEmptyLobby exemption when QueueArcadeMode is true so an ongoing
        //           Queue Arcade mini-game stays warm with bots when 0 human players are currently connected.
        // fails_when: FleetSupervisor kills a warm Queue Arcade match after EmptyLobbyReapSeconds when Ranked pulls all humans out.
        // seam: Real MatchServerManagerSupervisor.ShouldReapEmptyLobby state machine comparison (Regular vs QueueArcadeMode).
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MatchServerManagerOptions regularOptions = new()
        {
            Instances = 1,
            AutoStart = false,
            EmptyLobbyReapSeconds = 120,
            QueueArcadeMode = false
        };

        MatchServerManagerOptions arcadeOptions = new()
        {
            Instances = 1,
            AutoStart = false,
            EmptyLobbyReapSeconds = 120,
            QueueArcadeMode = true
        };

        using MatchServerManagerSupervisor regularSupervisor = new(
            Options.Create(regularOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        using MatchServerManagerSupervisor arcadeSupervisor = new(
            Options.Create(arcadeOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        DedicatedServerWorker zeroHumanOpenMatch = new(1, 11485, 11985, 21485, 21985, "QUEUE ARCADE #1")
        {
            IsRunningOverride = true,
            CurrentState = "Occupied",
            MatchId = 5001,
            ConnectedClients = 0,
            LobbyOpenedAtUtc = now.AddMinutes(-10)
        };

        regularSupervisor.ShouldReapEmptyLobby(zeroHumanOpenMatch, now, ignoreProcessRunningForTest: true)
            .Should().BeTrue("Regular game servers must reap stale 0-human lobbies after EmptyLobbyReapSeconds");

        arcadeSupervisor.ShouldReapEmptyLobby(zeroHumanOpenMatch, now, ignoreProcessRunningForTest: true)
            .Should().BeFalse("Queue Arcade servers must stay warm with 0 humans so newly queued players can drop in instantly");
    }

    [Fact]
    public void InitializeWorkers_WhenQueueArcadeModeTierQ_AssignsQPrefix4000SeriesIndicesAndPortIsolation()
    {
        // protects: Side-by-side Tier 'Q' port and index partitioning (#Q1/#Q2 -> InstanceIndex 4001/4002, UDP 11485..11486)
        //           and MatchServerManagerOptionsValidator acceptance of PortRangeOffset = 250 + WarmInstancesTarget = 1.
        // fails_when: Queue Arcade workers collide with Ranked Tier 'L' (#L1 = 1001, UDP 11235) or PortRangeOffset = 250 fails validation.
        // seam: Real MatchServerManagerSupervisor.InitializeWorkersForTest + MatchServerManagerOptionsValidator.
        MatchServerManagerOptions arcadeOptions = new()
        {
            UserName = "guccihost_arcade",
            Password = Guid.NewGuid().ToString("N"),
            Instances = 2,
            WarmInstancesTarget = 1,
            AutoStart = false,
            Gateway = "127.0.0.1",
            Location = "USE",
            ServerNamePrefix = "DONUT ARENA",
            UseProxy = false,
            PortRangeOffset = 250,
            FleetTier = "Q",
            QueueArcadeMode = true,
            RuntimeArtefactsPath = "DEFAULT"
        };

        ValidateOptionsResult validation = new MatchServerManagerOptionsValidator().Validate(null, arcadeOptions);
        validation.Succeeded.Should().BeTrue();
        new PortPlan(arcadeOptions).LocalGameStart.Should().Be(11485);

        using MatchServerManagerSupervisor supervisor = new(
            Options.Create(arcadeOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);

        supervisor.InitializeWorkersForTest();

        supervisor.Workers.Count.Should().Be(2);
        DedicatedServerWorker[] workers = [.. supervisor.Workers.OrderBy(w => w.InstanceIndex)];
        DedicatedServerWorker q1 = workers[0];
        DedicatedServerWorker q2 = workers[1];

        q1.InstanceIndex.Should().Be(4001);
        q1.Port.Should().Be(11485);
        q1.VoicePort.Should().Be(11985);
        q1.PublicGamePort.Should().Be(11485);
        q1.ServerName.Should().Be("QUEUE ARCADE DONUT ARENA #Q1");

        q2.InstanceIndex.Should().Be(4002);
        q2.Port.Should().Be(11486);
        q2.VoicePort.Should().Be(11986);
        q2.PublicGamePort.Should().Be(11486);
        q2.ServerName.Should().Be("QUEUE ARCADE DONUT ARENA #Q2");
    }

    [Fact]
    public void ShouldReapOrphanProcess_PortScopesTermination_NeverKillsLiveRankedServerWorkers()
    {
        // protects: MatchServerManagerSupervisor.KillOrphanedProcesses port-scoped PID guard so launching Queue Arcade
        //           never terminates live Ranked hon_x64.exe matches (and vice versa).
        // fails_when: KillOrphanedProcesses kills all hon_x64.exe processes indiscriminately by process name.
        // seam: Real MatchServerManagerSupervisor.ShouldReapOrphanProcess against a multi-fleet UDP socket owner map.
        MatchServerManagerOptions rankedOptions = new()
        {
            Instances = 2,
            AutoStart = false,
            PortRangeOffset = 0,
            FleetTier = "L",
            QueueArcadeMode = false
        };

        MatchServerManagerOptions arcadeOptions = new()
        {
            Instances = 2,
            AutoStart = false,
            PortRangeOffset = 250,
            FleetTier = "Q",
            QueueArcadeMode = true
        };

        using MatchServerManagerSupervisor rankedSupervisor = new(
            Options.Create(rankedOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);
        rankedSupervisor.InitializeWorkersForTest();

        using MatchServerManagerSupervisor arcadeSupervisor = new(
            Options.Create(arcadeOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);
        arcadeSupervisor.InitializeWorkersForTest();

        Dictionary<int, int> udpOwners = new()
        {
            [11235] = 7101, // Live Ranked #L1 worker
            [11735] = 7101,
            [11485] = 8201, // Stale Queue Arcade #Q1 worker
            [11985] = 8201
        };

        arcadeSupervisor.ShouldReapOrphanProcess(8201, udpOwners).Should().BeTrue("PID 8201 owns Queue Arcade UDP port 11485");
        arcadeSupervisor.ShouldReapOrphanProcess(7101, udpOwners).Should().BeFalse("Queue Arcade startup must NEVER kill live Ranked worker on 11235");

        rankedSupervisor.ShouldReapOrphanProcess(7101, udpOwners).Should().BeTrue("PID 7101 owns Ranked UDP port 11235");
        rankedSupervisor.ShouldReapOrphanProcess(8201, udpOwners).Should().BeFalse("Ranked startup must NEVER kill live Queue Arcade worker on 11485");
    }

    [Fact]
    public void InitializeWorkers_WhenQueueArcadeModeWithAutoStart_KeepsAtLeastTwoWorkersWarm()
    {
        // protects: MatchServerManagerSupervisor.EffectiveWarmInstancesTarget promoting WarmInstancesTarget=1 -> 2 when
        //           QueueArcadeMode=true and Instances >= 2 so standby worker #Q2 (UDP 11486) is warm and ready to
        //           pre-load the next map during #Q1's 12s Victory screen hold.
        // fails_when: WarmInstancesTarget=1 leaves #Q2 with DesiredRunning=false in QueueArcadeMode, forcing a 20s cold boot.
        // seam: Real MatchServerManagerSupervisor.InitializeWorkersForTest comparing QueueArcadeMode=true vs false.
        MatchServerManagerOptions regularOptions = new()
        {
            Instances = 2,
            WarmInstancesTarget = 1,
            AutoStart = true,
            PortRangeOffset = 0,
            FleetTier = "L",
            QueueArcadeMode = false
        };

        MatchServerManagerOptions arcadeOptions = new()
        {
            Instances = 2,
            WarmInstancesTarget = 1,
            AutoStart = true,
            PortRangeOffset = 250,
            FleetTier = "Q",
            QueueArcadeMode = true
        };

        using MatchServerManagerSupervisor regularSupervisor = new(
            Options.Create(regularOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);
        regularSupervisor.InitializeWorkersForTest();

        DedicatedServerWorker[] regularWorkers = [.. regularSupervisor.Workers.OrderBy(w => w.InstanceIndex)];
        regularWorkers[0].DesiredRunning.Should().BeTrue("Regular #L1 respects WarmInstancesTarget=1");
        regularWorkers[1].DesiredRunning.Should().BeFalse("Regular #L2 stays cold when WarmInstancesTarget=1");

        using MatchServerManagerSupervisor arcadeSupervisor = new(
            Options.Create(arcadeOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance);
        arcadeSupervisor.InitializeWorkersForTest();

        DedicatedServerWorker[] arcadeWorkers = [.. arcadeSupervisor.Workers.OrderBy(w => w.InstanceIndex)];
        arcadeWorkers.Should().HaveCount(2);
        arcadeWorkers[0].DesiredRunning.Should().BeTrue("Queue Arcade #Q1 (4001) must start warm");
        arcadeWorkers[1].DesiredRunning.Should().BeTrue("Queue Arcade #Q2 (4002) must ALSO stay warm for instant post-victory map rotation");
    }

    [Fact]
    public async Task CompleteMatchAndMaybeRecycleAsync_HoldsCustomMapWorkerInPostMatchHold_AndPreventsMidMatchCorruptMatchIdOverwrite()
    {
        // protects: MatchServerManagerSupervisor post-match PostMatchHold state + 15s client hold (breaking early after 3s once
        //           clients disconnect), EndGame non-termination prior to Stat submission successful, and locked MatchID
        //           protection against CR-merged stdout lines (1414 -> 141424).
        // fails_when: Worker is killed 1s after Stat submission successful while clients remain connected, transitions to
        //             "Available" prematurely during hold, or overwrites MatchId 1414 with 141424.
        // seam: Real MatchServerManagerSupervisor.DispatchStreamLine + CompleteMatchAndMaybeRecycleAsync + FastTimeProvider.
        InstantDelayTimeProvider fastClock = new();
        MatchServerManagerOptions customMapOptions = new()
        {
            Instances = 1,
            AutoStart = false,
            QueueArcadeMode = false,
            RecycleEveryNMatches = 1
        };

        using MatchServerManagerSupervisor supervisor = new(
            Options.Create(customMapOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance,
            timeProvider: fastClock);
        supervisor.InitializeWorkersForTest();

        DedicatedServerWorker worker = supervisor.Workers.Single();
        worker.IsRunningOverride = true;
        worker.DesiredRunning = true;
        worker.CurrentMap = "enfos";
        worker.CurrentState = "InMatch";
        worker.ConnectedClients = 1;
        worker.ActiveClientSlots[0] = 1;

        // 1. Valid MatchID line sets 1414; subsequent CR-corrupted line (141424]) must NOT overwrite while InMatch.
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(worker.Port, "SGame: [15:47:24]   match_id, INT: 1414", false));
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(worker.Port, "SGame: [15:47:24]   match_id, INT: 141424]   match_info, STRING: OK4]", false));
        worker.MatchId.Should().Be(1414);

        // 2. EndGame alone must NOT pull worker out of InMatch before stats submission completes.
        supervisor.DispatchStreamLine(worker, new DedicatedServerLogEntry(worker.Port, "SGame: [15:47:24] EndGame", false));
        worker.CurrentState.Should().Be("InMatch");

        // 3. CompleteMatchAndMaybeRecycleAsync must hold in PostMatchHold for 15s when ConnectedClients > 0.
        // Use a dedicated clock instance for the hold loop so the background 60s EndGame watchdog tick cannot skew DelayTicks.
        InstantDelayTimeProvider holdClock = new();
        using MatchServerManagerSupervisor holdSupervisor = new(
            Options.Create(customMapOptions),
            NullLogger<MatchServerManagerSupervisor>.Instance,
            timeProvider: holdClock);
        holdSupervisor.InitializeWorkersForTest();
        DedicatedServerWorker holdWorker = holdSupervisor.Workers.Single();
        holdWorker.IsRunningOverride = true;
        holdWorker.DesiredRunning = true;
        holdWorker.CurrentMap = "enfos";
        holdWorker.CurrentState = "InMatch";
        holdWorker.ConnectedClients = 1;
        holdWorker.ActiveClientSlots[0] = 1;

        bool recycled = false;
        string stateDuringRecycle = string.Empty;
        await holdSupervisor.CompleteMatchAndMaybeRecycleAsync(holdWorker, w =>
        {
            recycled = true;
            stateDuringRecycle = w.CurrentState;
            return Task.CompletedTask;
        });

        recycled.Should().BeTrue();
        stateDuringRecycle.Should().Be("PostMatchHold", "worker must remain unallocatable during the entire post-match hold");
        holdClock.DelayTicks.Should().Be(15, "connected clients must receive the full 15s Victory/stats hold");

        // 4. Once ConnectedClients drops to 0, hold exits early after minimum 3s (sec 0, 1, 2 = 3 ticks).
        holdWorker.CurrentState = "InMatch";
        holdWorker.CurrentMap = "enfos";
        holdWorker.ConnectedClients = 0;
        holdWorker.ActiveClientSlots.Clear();
        holdClock.DelayTicks = 0;
        recycled = false;
        await holdSupervisor.CompleteMatchAndMaybeRecycleAsync(holdWorker, _ =>
        {
            recycled = true;
            return Task.CompletedTask;
        });

        recycled.Should().BeTrue();
        holdClock.DelayTicks.Should().Be(3, "empty post-match server should recycle after the 3s minimum flush window");
    }

    private sealed class InstantDelayTimeProvider : TimeProvider
    {
        private int _delayTicks;
        public int DelayTicks
        {
            get => Volatile.Read(ref _delayTicks);
            set => Volatile.Write(ref _delayTicks, value);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new ImmediateTimer(this, callback, state, dueTime);
        }

        private sealed class ImmediateTimer : ITimer
        {
            private readonly InstantDelayTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _disposed;

            public ImmediateTimer(InstantDelayTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                ScheduleIfDue(dueTime);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                ScheduleIfDue(dueTime);
                return Volatile.Read(ref _disposed) == 0;
            }

            private void ScheduleIfDue(TimeSpan dueTime)
            {
                if (dueTime == Timeout.InfiniteTimeSpan || Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                Interlocked.Increment(ref _owner._delayTicks);
                _ = Task.Run(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _callback(_state);
                    }
                });
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
