// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LinuxChaosSelfHealingTests.cs
// System: COMPEL Unit Tests - Linux 10v10 Multi-Client Tracking, Custom-Map Recycle & Shared-Memory Reset
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using ASPIRE.Common.Telemetry;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace COMPEL.Tests;

public sealed class LinuxChaosSelfHealingTests
{
    private static readonly MethodInfo DispatchStreamLine = typeof(MatchServerManagerSupervisor)
        .GetMethod("DispatchStreamLine", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Feed(MatchServerManagerSupervisor supervisor, DedicatedServerWorker worker, string line) =>
        DispatchStreamLine.Invoke(supervisor, [worker, new DedicatedServerLogEntry(worker.Port, line, false)]);

    [Fact]
    public void LinuxHumanClientConnect_TracksMultiplePlayers_AndSingleRagequitDoesNotRecycleLive10v10Match()
    {
        // protects: Multi-player Linux dedicated server sessions where Dev 'AddClient: client num:' is suppressed
        //           and only Sv 'Finished sending state data to client #N' / 'Voice: Adding client num N' fire alongside bots.
        // fails_when: ActiveClientSlots stays empty on Linux (or counts connectionless bots), causing the first single
        //             human disconnect to hit ConnectedClients == 0 and recycle the live match underneath remaining humans.
        // seam: Real MatchServerManagerSupervisor.DispatchStreamLine fed empirical Linux hon_x64_server log lines.
        CapturingLogger<MatchServerManagerSupervisor> logger = new();
        MatchServerManagerOptions options = new() { AutoStart = false, DisconnectGraceSecondsInMatch = 1 };
        using MatchServerManagerSupervisor supervisor = new(Options.Create(options), logger);
        DedicatedServerWorker worker = new(1, 11335, 11835, 11335, 11835, "KONGOR Linux #1")
        {
            DesiredRunning = false
        };

        Feed(supervisor, worker, "Sv: [03:46:30] Finished sending state data to client #0");
        Feed(supervisor, worker, "Sv: [03:46:35] Voice: Adding client num 0, ID: 1");
        Feed(supervisor, worker, "Sv: [03:46:30] Finished sending state data to client #1");
        Feed(supervisor, worker, "Sv: [03:46:35] Voice: Adding client num 1, ID: 2");
        Feed(supervisor, worker, "[NativeGuard:Linux] CPlayer::InitializeConnectionless patched connectionless player client 2 (bFakePlayer=0, this=0x1c7aa220)");
        Feed(supervisor, worker, "[03:46:38] Client #2 joined team: 1");

        worker.ConnectedClients.Should().Be(2, "both human clients (#0 and #1) must be tracked idempotently while connectionless bots (#2) are excluded");

        Feed(supervisor, worker, "Sv: [03:48:10] Client #1 disconnected: ragequit");

        worker.ConnectedClients.Should().Be(1, "client #0 is still in the match after client #1 ragequits");
        logger.Messages.Should().NotContain(m => m.Contains("Client count hit 0"), "remaining human players must never lose their live server when one player leaves");
    }

    [Fact]
    public void LinuxCustomMapLifecycle_CWorldStartLoad_StartMatch_AndStatSubmissionSuccessful_TriggersImmediateRecycle()
    {
        // protects: Cross-OS WTF/custom-map bleed isolation (forceCustomMapRecycle) on Linux dedicated servers.
        // fails_when: Supervisor misses Linux 'CWorld::StartLoad - <map>', 'SGame: [...] StartMatch', or
        //             'Stat submission successful', leaving dirty custom-map CEntityRegistry state alive across matches.
        // seam: Real MatchServerManagerSupervisor.DispatchStreamLine fed empirical Linux caldavar_wtf lifecycle logs.
        CapturingLogger<MatchServerManagerSupervisor> logger = new();
        MatchServerManagerOptions options = new() { AutoStart = false, RecycleEveryNMatches = 0 };
        using MatchServerManagerSupervisor supervisor = new(Options.Create(options), logger);
        DedicatedServerWorker worker = new(1, 11336, 11836, 11336, 11836, "KONGOR Linux #2")
        {
            IsRunningOverride = true,
            DesiredRunning = true,
            IsDraining = false,
            CurrentState = "Available"
        };

        Feed(supervisor, worker, "CWorld::StartLoad - caldavar_wtf 0");
        worker.CurrentMap.Should().Be("caldavar_wtf");

        // Operator echo '> StartMatch' during countdown must NOT prematurely flip InMatch before countdown ends
        Feed(supervisor, worker, "> StartMatch");
        worker.CurrentState.Should().Be("Available");

        // Actual SGame match start flips worker into InMatch
        Feed(supervisor, worker, "SGame: [03:54:41] StartMatch");
        worker.CurrentState.Should().Be("InMatch");

        // Disarm DesiredRunning only right after threshold log check; keep DesiredRunning=true for threshold check, then disarm before hold expires
        Feed(supervisor, worker, "Stat submission successful");
        Feed(supervisor, worker, "Stat submission successful");
        worker.DesiredRunning = false;

        worker.CurrentState.Should().Be("PostMatchHold");
        worker.MatchesHostedSinceRecycle.Should().Be(1, "duplicate Stat submission successful lines must only count once");
        logger.Messages.Should().Contain(m => m.Contains("Worker reached recycle threshold (1/1)"), "custom map caldavar_wtf must force threshold=1 even when RecycleEveryNMatches=0");
    }

    [Fact]
    public async Task ExecuteCommandOnPortAsync_GiveGoldAll_DispatchesToAll20SlotsFor10v10Matches()
    {
        // protects: GiveGold all <amount> support across all 20 player slots (0..19) in 10v10 matches.
        // fails_when: Slot loop stops at 10, starving players/bots in slots 10..19.
        // seam: Real OS NamedPipeServerStream <-> MatchServerManagerSupervisor.ExecuteCommandOnPortAsync.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $"kongor-test-gold20-{Guid.NewGuid():N}";
        List<string> receivedCommands = new();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        NamedPipeServerStream nextServer = new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            NamedPipeServerStream? currentServer = nextServer;
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    NamedPipeServerStream server = currentServer!;
                    currentServer = i + 1 < 20
                        ? new NamedPipeServerStream(
                            pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous)
                        : null;

                    await using (server.ConfigureAwait(false))
                    {
                        await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

                        byte[] hdr = new byte[12];
                        int hRead = 0;
                        while (hRead < 12)
                        {
                            int r = await server.ReadAsync(hdr.AsMemory(hRead, 12 - hRead), cts.Token).ConfigureAwait(false);
                            if (r <= 0) break;
                            hRead += r;
                        }

                        int bodyLen = BitConverter.ToInt32(hdr.AsSpan(4, 4));
                        byte[] body = new byte[bodyLen];
                        int bRead = 0;
                        while (bRead < bodyLen)
                        {
                            int r = await server.ReadAsync(body.AsMemory(bRead, bodyLen - bRead), cts.Token).ConfigureAwait(false);
                            if (r <= 0) break;
                            bRead += r;
                        }

                        receivedCommands.Add(Encoding.UTF8.GetString(body, 0, bRead));

                        byte[] ackJson = Encoding.UTF8.GetBytes("{\"success\":true}");
                        byte[] resp = new byte[12 + ackJson.Length];
                        resp[0] = 0x4D;
                        resp[1] = 0x57;
                        BitConverter.TryWriteBytes(resp.AsSpan(2, 2), (ushort)0x0004);
                        BitConverter.TryWriteBytes(resp.AsSpan(4, 4), ackJson.Length);
                        BitConverter.TryWriteBytes(resp.AsSpan(8, 4), 1u);
                        ackJson.CopyTo(resp.AsSpan(12));
                        await server.WriteAsync(resp, cts.Token).ConfigureAwait(false);
                        try
                        {
                            await server.FlushAsync(cts.Token).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            // Client already read the complete ACK frame and closed its handle.
                        }
                    }
                }
            }
            finally
            {
                if (currentServer != null)
                {
                    await currentServer.DisposeAsync().ConfigureAwait(false);
                }
            }
        }, cts.Token);

        MatchServerManagerOptions options = new() { Instances = 1, AutoStart = false };
        using MatchServerManagerSupervisor supervisor = new(
            Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchServerManagerSupervisor>.Instance);

        DedicatedServerWorker worker = new(1, 11335, 11835, 11335, 11835, "TestGold20")
        {
            IsRunningOverride = true,
            CurrentState = "InMatch",
            NativePipeNameOverride = pipeName
        };
        supervisor.RegisterWorkerForTesting(worker);

        (bool success, _, _) = await supervisor.ExecuteCommandOnPortAsync(11335, "GiveGold all 25000", cts.Token);
        await serverTask;

        success.Should().BeTrue();
        receivedCommands.Should().HaveCount(20);
        receivedCommands[0].Should().Be("GiveGold 0 25000");
        receivedCommands[19].Should().Be("GiveGold 19 25000");
    }

    [Fact]
    public async Task DedicatedServerWorker_WhenSharedMemoryFrameIndexResetsAfterWorkerRecycle_ResumesTickrateTrackingImmediately()
    {
        // protects: Continuous ServerTickrateTracker ingestion after a worker recycle resets FrameIndex in /dev/shm.
        // fails_when: lastProcessedFrame stays latched at pre-recycle FrameIndex (e.g. 50000), ignoring frames 1..49999.
        // seam: Real MemoryMappedTelemetryBuffer <-> DedicatedServerWorker.StartTelemetryPolling.
        int isolatedPort = 59288;
        string mapName = SharedMemoryTelemetryConsumer.GetServerMapName(isolatedPort);
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity: 1024, producerProcessId: 9997);

        DedicatedServerWorker worker = new(1, isolatedPort, isolatedPort + 200, isolatedPort, isolatedPort + 200, "TelemetryResetTest")
        {
            DesiredRunning = true,
            IsRunningOverride = true,
            CurrentState = "Available"
        };

        int notifications = 0;
        worker.StartTelemetryPolling(
            logger: null,
            onFrameRecord: (_, _) => Interlocked.Increment(ref notifications));

        try
        {
            producer.TryWrite(new FrameTelemetryRecord
            {
                FrameIndex = 50000,
                TimestampQpc = DateTimeOffset.UtcNow.Ticks,
                ActiveEntities = 20,
                ServerTick = 9000
            });

            for (int i = 0; i < 40 && worker.LatestTelemetryRecord.ServerTick != 9000u; i++)
            {
                await Task.Delay(25);
            }
            worker.LatestTelemetryRecord.ServerTick.Should().Be(9000u);

            // Simulate recycled worker resetting FrameIndex back to 1 and advancing to 2
            producer.TryWrite(new FrameTelemetryRecord
            {
                FrameIndex = 1,
                TimestampQpc = DateTimeOffset.UtcNow.Ticks,
                ActiveEntities = 0,
                ServerTick = 10
            });
            await Task.Delay(60);
            producer.TryWrite(new FrameTelemetryRecord
            {
                FrameIndex = 2,
                TimestampQpc = DateTimeOffset.UtcNow.Ticks,
                ActiveEntities = 0,
                ServerTick = 11
            });

            for (int i = 0; i < 40 && worker.LatestTelemetryRecord.ServerTick != 11u; i++)
            {
                await Task.Delay(25);
            }

            worker.LatestTelemetryRecord.ServerTick.Should().Be(11u, "FrameIndex watermark must reset cleanly when recycled worker starts back at frame 1");
            worker.TickrateTracker.CurrentTickrateHz.Should().BeGreaterThan(0.0);
        }
        finally
        {
            worker.StopTelemetryPolling();
        }
    }

    [Fact]
    public async Task DedicatedServerWorker_WhenLinuxActiveEntitiesIsZero_PreservesLogDrivenInMatchUntilStatSubmission()
    {
        // protects: InMatch state integrity and custom-map auto-recycle on Linux workers where /dev/shm ActiveEntities stays 0.
        // fails_when: Telemetry poller sees ActiveEntities == 0 and wipes CurrentState='InMatch' back to 'Available' 10ms into the match.
        // seam: Real MemoryMappedTelemetryBuffer + DedicatedServerWorker.StartTelemetryPolling + MatchServerManagerSupervisor.DispatchStreamLine.
        int isolatedPort = 59290;
        string mapName = SharedMemoryTelemetryConsumer.GetServerMapName(isolatedPort);
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity: 1024, producerProcessId: 9998);

        CapturingLogger<MatchServerManagerSupervisor> logger = new();
        MatchServerManagerOptions options = new() { AutoStart = false, RecycleEveryNMatches = 0 };
        using MatchServerManagerSupervisor supervisor = new(Options.Create(options), logger);
        DedicatedServerWorker worker = new(1, isolatedPort, isolatedPort + 200, isolatedPort, isolatedPort + 200, "LinuxZeroEntitiesTest")
        {
            DesiredRunning = true,
            IsRunningOverride = true,
            IsDraining = false,
            CurrentState = "Available"
        };

        worker.StartTelemetryPolling(logger: null, onFrameRecord: null);
        try
        {
            Feed(supervisor, worker, "CWorld::StartLoad - caldavar_wtf 0");
            Feed(supervisor, worker, "Sv: [04:16:39] Finished sending state data to client #0");
            Feed(supervisor, worker, "SGame: [04:16:52] StartMatch");
            worker.CurrentState.Should().Be("InMatch");

            // Emit multiple Linux shared-memory frames where ActiveEntities == 0 while ServerTick advances
            for (uint tick = 100; tick <= 105; tick++)
            {
                producer.TryWrite(new FrameTelemetryRecord
                {
                    FrameIndex = tick,
                    TimestampQpc = DateTimeOffset.UtcNow.Ticks,
                    ActiveEntities = 0,
                    ServerTick = tick
                });
                await Task.Delay(15);
            }

            worker.CurrentState.Should().Be("InMatch", "Linux ActiveEntities == 0 frames must never wipe active InMatch state");

            Feed(supervisor, worker, "SGame: [04:16:56] Stat submission successful");
            worker.DesiredRunning = false;

            worker.CurrentState.Should().Be("PostMatchHold");
            logger.Messages.Should().Contain(m => m.Contains("Worker reached recycle threshold (1/1)"));
        }
        finally
        {
            worker.StopTelemetryPolling();
        }
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
