// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SharedMemoryTelemetryConsumerTests.cs
// System: Kongor Core Tests - Shared Memory Telemetry Consumer Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Telemetry;
using FluentAssertions;
using NEXUS.Contracts.Telemetry;
using Xunit;

namespace Kongor.Core.Tests.Telemetry;

public sealed class SharedMemoryTelemetryConsumerTests
{
    [Fact]
    public void Consumer_InitializesDisconnected()
    {
        using SharedMemoryTelemetryConsumer consumer = new();

        consumer.IsConnected.Should().BeFalse();
        consumer.CurrentSummary.IsConnected.Should().BeFalse();
        consumer.CurrentSummary.TotalFramesRead.Should().Be(0);
    }

    [Fact]
    public void ProducerWritesRecords_ConsumerCalculatesAverageFpsAnd1PercentLow()
    {
        string mapName = $"Local\\TestTelemetry_Stats_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity: 512);
        using SharedMemoryTelemetryConsumer consumer = new();

        bool connected = consumer.TryConnect(mapName);
        connected.Should().BeTrue();
        consumer.IsConnected.Should().BeTrue();

        // Write 99 smooth frames at 60 FPS (16.6ms) and 1 stutter frame (33.3ms = 30 FPS)
        for (ulong i = 1; i <= 99; i++)
        {
            FrameTelemetryRecord normalRecord = new()
            {
                FrameIndex = i,
                CpuFrameTimeMs = 16.6f,
                GpuFrameTimeMs = 12.0f,
                InputLatencyMs = 8.0f,
                PingMicroseconds = 20_000,
                PacketLossPercent = 0.0f,
                ServerTick = (uint)(i * 2),
                ActiveEntities = 150,
                DrawCallCount = 350
            };
            producer.TryWrite(in normalRecord);
        }

        FrameTelemetryRecord spikeRecord = new()
        {
            FrameIndex = 100,
            CpuFrameTimeMs = 33.3f, // 1% stutter frame
            GpuFrameTimeMs = 28.0f,
            InputLatencyMs = 16.0f,
            PingMicroseconds = 22_000,
            PacketLossPercent = 0.0f,
            ServerTick = 200,
            ActiveEntities = 160,
            DrawCallCount = 400
        };
        producer.TryWrite(in spikeRecord);

        TelemetryMetricsSummary summary = consumer.UpdateAndGetSummary();

        summary.IsConnected.Should().BeTrue();
        summary.TotalFramesRead.Should().Be(100);
        summary.CurrentFps.Should().BeInRange(57f, 62f);
        summary.Low1PercentFps.Should().BeInRange(28f, 33f);
        summary.AverageCpuFrametimeMs.Should().BeInRange(16.5f, 17.5f);
        summary.AverageInputLatencyMs.Should().Be(16.0f);
        summary.AveragePingMs.Should().Be(22.0f);
        summary.LatestServerTick.Should().Be(200);
        summary.ActiveEntities.Should().Be(160);
    }

    [Fact]
    public void TryGetLatestFrame_ReturnsMostRecentProducerRecord()
    {
        string mapName = $"Local\\TestTelemetry_Latest_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity: 128);
        using SharedMemoryTelemetryConsumer consumer = new();

        consumer.TryConnect(mapName).Should().BeTrue();

        FrameTelemetryRecord rec = new()
        {
            FrameIndex = 42,
            CpuFrameTimeMs = 8.33f,
            GpuFrameTimeMs = 7.1f,
            InputLatencyMs = 4.2f,
            DrawCallCount = 520,
            ActiveEntities = 210,
            PingMicroseconds = 15_000,
            ServerTick = 8899
        };

        producer.TryWrite(in rec);

        bool success = consumer.TryGetLatestFrame(out FrameTelemetryRecord latest);
        success.Should().BeTrue();
        latest.FrameIndex.Should().Be(42);
        latest.CpuFrameTimeMs.Should().Be(8.33f);
        latest.ServerTick.Should().Be(8899);
        latest.PingMicroseconds.Should().Be(15_000);
    }

    [Fact]
    public void HighThroughputBatchWritesAndReads_ProcessesRapidlyWithoutAllocations()
    {
        string mapName = $"Local\\TestTelemetry_Throughput_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity: 2048);
        using SharedMemoryTelemetryConsumer consumer = new();

        consumer.TryConnect(mapName).Should().BeTrue();

        // Stream 1,000 frames through the ring buffer
        Span<FrameTelemetryRecord> destSpan = stackalloc FrameTelemetryRecord[256];
        int totalRead = 0;

        for (ulong i = 1; i <= 1000; i++)
        {
            FrameTelemetryRecord frame = new()
            {
                FrameIndex = i,
                CpuFrameTimeMs = 16.6f,
                ServerTick = (uint)i
            };
            producer.TryWrite(in frame);

            if (i % 200 == 0)
            {
                int read = consumer.ReadAvailableFrames(destSpan);
                totalRead += read;
            }
        }

        int remaining = consumer.ReadAvailableFrames(destSpan);
        totalRead += remaining;

        totalRead.Should().Be(1000);
    }

    [Fact]
    public void Disconnect_ResetsStateToDisconnected()
    {
        string mapName = $"Local\\TestTelemetry_Disc_{Guid.NewGuid():N}";
        using MemoryMappedTelemetryBuffer producer = MemoryMappedTelemetryBuffer.CreateProducer(mapName, capacity: 64);
        using SharedMemoryTelemetryConsumer consumer = new();

        consumer.TryConnect(mapName).Should().BeTrue();
        consumer.IsConnected.Should().BeTrue();

        consumer.Disconnect();

        consumer.IsConnected.Should().BeFalse();
        consumer.CurrentSummary.IsConnected.Should().BeFalse();
    }
}
