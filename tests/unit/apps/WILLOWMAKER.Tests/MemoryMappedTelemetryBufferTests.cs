// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MemoryMappedTelemetryBufferTests.cs
// System: WILLOWMAKER.Tests - High-Speed Shared Memory Ring Buffer Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using ASPIRE.Common.Telemetry;
using NEXUS.Contracts.Telemetry;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class MemoryMappedTelemetryBufferTests
{
    [Fact]
    public void MemoryMappedTelemetryBuffer_CreateProducer_InitializesValidHeader()
    {
        string mapName = $"Local\\TestKongorTelemetry_Init_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, 1024);

        Assert.Equal(1024u, producer.Capacity);
        Assert.True(producer.IsProducer);
        Assert.Equal(0ul, producer.Head);
        Assert.Equal(0ul, producer.Tail);
    }

    [Fact]
    public void TryWrite_And_ReadLatest_ReturnsCorrectRecord()
    {
        string mapName = $"Local\\TestKongorTelemetry_Write_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, 1024);
        using MemoryMappedTelemetryBuffer consumer = MemoryMappedTelemetryBuffer.OpenConsumer(mapName);

        FrameTelemetryRecord sent = new()
        {
            FrameIndex = 42,
            TimestampQpc = Stopwatch.GetTimestamp(),
            CpuFrameTimeMs = 2.45f,
            GpuFrameTimeMs = 2.10f,
            InputLatencyMs = 3.50f,
            DrawCallCount = 450,
            ActiveEntities = 80,
            ParticlesAlive = 120,
            PingMicroseconds = 18500,
            PacketLossPercent = 0.0f,
            MemoryWorkingSetMb = 512,
            AudioVoicesActive = 64,
            ServerTick = 1280
        };

        bool writeSuccess = producer.TryWrite(sent);
        Assert.True(writeSuccess);

        bool readSuccess = consumer.ReadLatest(out FrameTelemetryRecord received);
        Assert.True(readSuccess);
        Assert.Equal(sent, received);
    }

    [Fact]
    public void MemoryMappedTelemetryBuffer_ReadAvailable_ReadsBatchAndAdvancesTail()
    {
        string mapName = $"Local\\TestKongorTelemetry_Batch_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, 1024);
        using MemoryMappedTelemetryBuffer consumer = MemoryMappedTelemetryBuffer.OpenConsumer(mapName);

        for (ulong i = 1; i <= 20; i++)
        {
            FrameTelemetryRecord record = new()
            {
                FrameIndex = i,
                CpuFrameTimeMs = 4.0f
            };
            producer.TryWrite(record);
        }

        Span<FrameTelemetryRecord> buffer = stackalloc FrameTelemetryRecord[30];
        int readCount = consumer.ReadAvailable(buffer);

        Assert.Equal(20, readCount);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal((ulong)(i + 1), buffer[i].FrameIndex);
        }

        // Reading again should return 0 since tail was advanced
        int secondRead = consumer.ReadAvailable(buffer);
        Assert.Equal(0, secondRead);
    }

    [Fact]
    public void MemoryMappedTelemetryBuffer_RingBuffer_WrapsAroundCapacityCleanly()
    {
        string mapName = $"Local\\TestKongorTelemetry_Wrap_{Guid.NewGuid():N}";
        const uint smallCapacity = 64;

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, smallCapacity);
        using MemoryMappedTelemetryBuffer consumer = MemoryMappedTelemetryBuffer.OpenConsumer(mapName);

        // Write 150 items into a 64-capacity ring (wrapping twice)
        for (ulong i = 1; i <= 150; i++)
        {
            FrameTelemetryRecord record = new()
            {
                FrameIndex = i,
                CpuFrameTimeMs = 2.0f
            };
            producer.TryWrite(record);
        }

        Assert.Equal(150ul, producer.Head);

        // ReadLatest should be 150
        bool latestRead = consumer.ReadLatest(out FrameTelemetryRecord latest);
        Assert.True(latestRead);
        Assert.Equal(150ul, latest.FrameIndex);

        // Consumer fell behind, so it should clamp to the oldest available 64 frames (indices 87 to 150)
        Span<FrameTelemetryRecord> readBuffer = stackalloc FrameTelemetryRecord[100];
        int readCount = consumer.ReadAvailable(readBuffer);

        Assert.Equal((int)smallCapacity, readCount);
        Assert.Equal(87ul, readBuffer[0].FrameIndex);
        Assert.Equal(150ul, readBuffer[readCount - 1].FrameIndex);
    }

    [Fact]
    public void TelemetryRingConsumerService_OnFrameBatch_ProcessesFramesAndCalculatesFps()
    {
        string mapName = $"Local\\TestKongorTelemetry_Service_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, 1024);
        using TelemetryRingConsumerService consumerService = new();

        // Write 100 frames with 4.0ms CPU frametime (250 FPS)
        for (ulong i = 1; i <= 100; i++)
        {
            FrameTelemetryRecord record = new()
            {
                FrameIndex = i,
                CpuFrameTimeMs = 4.0f,
                GpuFrameTimeMs = 3.5f,
                DrawCallCount = 350,
                ActiveEntities = 60,
                PingMicroseconds = 22000
            };
            producer.TryWrite(record);
        }

        consumerService.StartMonitoring(mapName, TimeSpan.FromMilliseconds(10));

        // Allow consumer timer tick to poll shared memory
        bool updated = SpinWait.SpinUntil(() => consumerService.CurrentSnapshot.IsConnected && consumerService.CurrentSnapshot.TotalFramesCaptured > 0, 4000);

        Assert.True(updated);
        ClientTelemetrySnapshot snapshot = consumerService.CurrentSnapshot;
        Assert.True(snapshot.IsConnected);
        Assert.InRange(snapshot.CurrentFps, 200f, 300f);
        Assert.Equal(350u, snapshot.DrawCalls);
        Assert.Equal(22u, snapshot.PingMs);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task MemoryMappedTelemetryBuffer_ConcurrentStress_ProducesAndConsumesWithoutCorruption()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string mapName = $"Local\\TestKongorTelemetry_Stress_{Guid.NewGuid():N}";
        const int totalFramesToSend = 20_000;
        const uint capacity = 2048;

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity);
        using MemoryMappedTelemetryBuffer consumer = MemoryMappedTelemetryBuffer.OpenConsumer(mapName);

        ulong lastConsumedIndex = 0;
        int totalFramesConsumed = 0;
        bool hasOrderingErrors = false;

        Task? producerTask = null;

        Task consumerTask = Task.Run(async () =>
        {
            FrameTelemetryRecord[] readArray = new FrameTelemetryRecord[256];

            while (lastConsumedIndex < totalFramesToSend)
            {
                int read = consumer.ReadAvailable(readArray);
                for (int i = 0; i < read; i++)
                {
                    ulong idx = readArray[i].FrameIndex;
                    if (idx <= lastConsumedIndex && lastConsumedIndex > 0)
                    {
                        hasOrderingErrors = true;
                    }

                    lastConsumedIndex = idx;
                    totalFramesConsumed++;
                }

                if (read == 0)
                {
                    if (producerTask is { IsCompleted: true })
                    {
                        // Final drain check
                        if (consumer.ReadAvailable(readArray) == 0)
                            break;
                    }

                    await Task.Delay(1);
                }
            }
        });

        producerTask = Task.Run(async () =>
        {
            for (ulong i = 1; i <= totalFramesToSend; i++)
            {
                FrameTelemetryRecord record = new()
                {
                    FrameIndex = i,
                    CpuFrameTimeMs = 1.0f,
                    TimestampQpc = (long)i
                };

                producer.TryWrite(record);

                if (i % 256 == 0)
                {
                    await Task.Delay(1);
                }
            }
        });

        await Task.WhenAll(producerTask, consumerTask);

        Assert.False(hasOrderingErrors, "Concurrent reading encountered out-of-order or duplicate frame indices.");
        Assert.True(totalFramesConsumed > 0);
        Assert.Equal((ulong)totalFramesToSend, lastConsumedIndex);
    }

    [Fact]
    public void TelemetryRingHeaderV2_LiveEngineStateFields_ReadCorrectlyAcrossSharedMemory()
    {
        // protects: KongorTelemetryRingHeaderV2 Cache Line 1 binary layout (offsets 84..127) between C++ version.dll and C# MemoryMappedTelemetryBuffer
        // fails_when: struct field offsets drift or null-terminated string decoding truncates/corrupts CurrentMap or PlayerName
        // seam: real OS MemoryMappedFile shared between producer and consumer views
        string mapName = $"Local\\TestKongorTelemetry_V2State_{Guid.NewGuid():N}";

        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, 1024);
        producer.UpdateLiveEngineState(
            gamePhase: 3u,
            activeMatchId: 900123u,
            connectedPlayers: 10u,
            luaMemoryKb: 52400u,
            currentMap: "caldavar",
            playerName: "KongorDev");

        using MemoryMappedTelemetryBuffer consumer = MemoryMappedTelemetryBuffer.OpenConsumer(mapName);
        Assert.Equal(3u, consumer.GamePhase);
        Assert.Equal(900123u, consumer.ActiveMatchId);
        Assert.Equal(10u, consumer.ConnectedPlayers);
        Assert.Equal(52400u, consumer.LuaMemoryKb);
        Assert.Equal("caldavar", consumer.CurrentMap);
        Assert.Equal("KongorDev", consumer.PlayerName);

        bool hasHeader = consumer.TryReadHeaderV2(out TelemetryRingHeaderV2 snapshotHeader);
        Assert.True(hasHeader);
        Assert.Equal("caldavar", snapshotHeader.GetCurrentMapString());
        Assert.Equal("KongorDev", snapshotHeader.GetPlayerNameString());
    }
}
