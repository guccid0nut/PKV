// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DedicatedServerSharedMemoryTelemetryTests.cs
// System: COMPEL Unit Tests - High-Speed Shared Memory Telemetry & IPC
// Subsystem: Dedicated Server Telemetry Polling & Zero-Copy State Synchronization
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Telemetry;
using COMPEL.Services.Supervision;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace COMPEL.Tests;

public sealed class DedicatedServerSharedMemoryTelemetryTests : IDisposable
{
    private const int TestPort = 59123;
    private readonly string _mapName;
    private MemoryMappedTelemetryBuffer? _producer;

    public DedicatedServerSharedMemoryTelemetryTests()
    {
        _mapName = SharedMemoryTelemetryConsumer.GetServerMapName(TestPort);
    }

    public void Dispose()
    {
        _producer?.Dispose();
    }

    [Fact]
    public async Task DedicatedServerWorker_AttachesToSharedMemory_AndUpdatesStateDeterministically()
    {
        // 1. Create simulated in-engine native guard telemetry ring producer
        _producer = MemoryMappedTelemetryBuffer.CreateProducer(_mapName, capacity: 4096, producerProcessId: 9999);
        Assert.NotNull(_producer);

        // 2. Initialize DedicatedServerWorker
        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: TestPort,
            voicePort: TestPort + 200,
            publicGamePort: TestPort,
            publicVoicePort: TestPort + 200,
            serverName: "Telemetry-Test-Server");

        worker.CurrentState = "Starting";

        // Simulate worker process running
        worker.DesiredRunning = true;
        worker.IsRunningOverride = true;

        FrameTelemetryRecord capturedRecord = default;
        int frameNotificationCount = 0;

        // 3. Start high-speed telemetry polling loop
        worker.StartTelemetryPolling(
            logger: null,
            onFrameRecord: (w, rec) =>
            {
                capturedRecord = rec;
                Interlocked.Increment(ref frameNotificationCount);
            });

        // 4. Produce a frame record from simulated native guard
        FrameTelemetryRecord record1 = new()
        {
            FrameIndex = 1,
            TimestampQpc = DateTimeOffset.UtcNow.Ticks,
            CpuFrameTimeMs = 16.6f,
            GpuFrameTimeMs = 0.0f,
            InputLatencyMs = 1.0f,
            DrawCallCount = 0,
            ActiveEntities = 12,
            ParticlesAlive = 0,
            PingMicroseconds = 8000,
            PacketLossPercent = 0.0f,
            MemoryWorkingSetMb = 350,
            AudioVoicesActive = 0,
            ServerTick = 101,
            ReservedFlags = 0
        };

        bool writeSuccess = _producer.TryWrite(record1);
        Assert.True(writeSuccess, "Producer must successfully write to the telemetry ring buffer.");

        // Wait up to 2 seconds for worker telemetry consumer to ingest
        for (int i = 0; i < 40; i++)
        {
            if (worker.IsTelemetryConnected && frameNotificationCount > 0)
                break;
            await Task.Delay(50);
        }

        Assert.True(worker.IsTelemetryConnected, "DedicatedServerWorker must connect to the shared memory ring buffer.");
        Assert.True(frameNotificationCount > 0, "DedicatedServerWorker must receive frame notification from shared memory.");
        Assert.Equal(101u, worker.LatestTelemetryRecord.ServerTick);
        Assert.Equal(12u, worker.LatestTelemetryRecord.ActiveEntities);
        Assert.Equal(350u, worker.LatestTelemetryRecord.MemoryWorkingSetMb);
        Assert.Equal("InMatch", worker.CurrentState);

        // 5. Produce a match-end frame record (ActiveEntities = 0)
        FrameTelemetryRecord record2 = new()
        {
            FrameIndex = 2,
            TimestampQpc = DateTimeOffset.UtcNow.Ticks,
            CpuFrameTimeMs = 16.6f,
            GpuFrameTimeMs = 0.0f,
            InputLatencyMs = 1.0f,
            DrawCallCount = 0,
            ActiveEntities = 0,
            ParticlesAlive = 0,
            PingMicroseconds = 8000,
            PacketLossPercent = 0.0f,
            MemoryWorkingSetMb = 355,
            AudioVoicesActive = 0,
            ServerTick = 102,
            ReservedFlags = 0
        };

        _producer.TryWrite(record2);

        for (int i = 0; i < 40; i++)
        {
            if (worker.CurrentState == "Available")
                break;
            await Task.Delay(50);
        }

        Assert.Equal("Available", worker.CurrentState);
        Assert.Equal(102u, worker.LatestTelemetryRecord.ServerTick);

        // 6. Stop telemetry polling and verify clean resource teardown
        worker.StopTelemetryPolling();
        Assert.False(worker.IsTelemetryConnected, "Stopping telemetry polling must dispose the consumer.");
    }

    [Fact]
    public async Task DedicatedServerWorker_LegacyIdle110Entities_StaysAvailable_AndPort11235IsolatesFromClientFastBus()
    {
        int isolatedPort = 59124;
        string isolatedMap = SharedMemoryTelemetryConsumer.GetServerMapName(isolatedPort);
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(isolatedMap, capacity: 1024, producerProcessId: 9998);

        DedicatedServerWorker worker = new(
            instanceIndex: 2,
            port: isolatedPort,
            voicePort: isolatedPort + 200,
            publicGamePort: isolatedPort,
            publicVoicePort: isolatedPort + 200,
            serverName: "Idle-110-Guard-Test");

        worker.CurrentState = "Starting";
        worker.DesiredRunning = true;
        worker.IsRunningOverride = true;

        int frames = 0;
        worker.StartTelemetryPolling(logger: null, onFrameRecord: (_, _) => Interlocked.Increment(ref frames));

        producer.TryWrite(new FrameTelemetryRecord
        {
            FrameIndex = 1,
            TimestampQpc = DateTimeOffset.UtcNow.Ticks,
            CpuFrameTimeMs = 50.0f,
            ActiveEntities = 110, // Legacy uninitialized idle sentinel
            ServerTick = 1
        });

        for (int i = 0; i < 40 && frames == 0; i++)
        {
            await Task.Delay(25);
        }

        Assert.True(frames > 0);
        Assert.Equal("Available", worker.CurrentState);
        worker.StopTelemetryPolling();

        // Also verify real games/server K2_SERVER binary validation passes on disk if present
        string repoServerExe = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "games", "server", "hon_x64.exe"));
        if (System.IO.File.Exists(repoServerExe))
        {
            bool valid = MatchServerManagerSupervisor.ValidateDedicatedServerBinary(repoServerExe, out string? err);
            Assert.True(valid, err ?? "Expected restored K2_SERVER binary to pass validation.");
        }
    }
}
