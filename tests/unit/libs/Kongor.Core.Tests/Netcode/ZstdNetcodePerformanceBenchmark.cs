// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Kongor.Core.Tests.Netcode;

public sealed class ZstdNetcodePerformanceBenchmark(ITestOutputHelper? output = null)
{
    [Fact]
    [Trait("Category", "Manual")]
    public async Task RuntimeAsync_LoopbackRelayRoundTrip_MeasuresP99LatencyAndHeapAllocations()
    {
        // protects: Sub-millisecond async UDP relay packet forwarding + heap allocation budget under .NET 11 runtime-async
        // fails_when: Async state-machine regressions inflate p99 loopback relay round-trip latency above 2.0 ms
        // seam: Real ClientNetcodeRelay UDP loopback sockets + ZstdDeltaCompressor
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        using Socket mockServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        mockServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint serverEp = (IPEndPoint)mockServer.LocalEndPoint!;

        await using ClientNetcodeRelay relay = new(serverEp);
        relay.Start();

        using Socket gameClient = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        gameClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint relayEp = new(IPAddress.Loopback, relay.LocalPort);

        byte[] clientPayload = new byte[256];
        Random.Shared.NextBytes(clientPayload);
        byte[] rawServerFrame = new byte[512];
        for (int i = 0; i < rawServerFrame.Length; i++) rawServerFrame[i] = (byte)(i & 0x3F);
        byte[] compressedFrame = ZstdDeltaCompressor.CompressDelta(rawServerFrame, level: 1);

        byte[] serverRecvBuf = new byte[1024];
        byte[] clientRecvBuf = new byte[1024];
        EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);

        // Warm-up 50 round-trips
        for (int w = 0; w < 50; w++)
        {
            await gameClient.SendToAsync(clientPayload, SocketFlags.None, relayEp);
            SocketReceiveFromResult srvIn = await mockServer.ReceiveFromAsync(serverRecvBuf, SocketFlags.None, anyEp);
            await mockServer.SendToAsync(compressedFrame, SocketFlags.None, srvIn.RemoteEndPoint);
            _ = await gameClient.ReceiveFromAsync(clientRecvBuf, SocketFlags.None, anyEp);
        }

        const int rounds = 400;
        double[] latenciesMicros = new double[rounds];
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < rounds; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            await gameClient.SendToAsync(clientPayload, SocketFlags.None, relayEp);
            SocketReceiveFromResult srvIn = await mockServer.ReceiveFromAsync(serverRecvBuf, SocketFlags.None, anyEp);
            await mockServer.SendToAsync(compressedFrame, SocketFlags.None, srvIn.RemoteEndPoint);
            SocketReceiveFromResult cliIn = await gameClient.ReceiveFromAsync(clientRecvBuf, SocketFlags.None, anyEp);
            latenciesMicros[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            cliIn.ReceivedBytes.Should().Be(rawServerFrame.Length);
        }

        long allocDeltaBytes = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        Array.Sort(latenciesMicros);
        double p50Micros = latenciesMicros[rounds / 2];
        double p99Micros = latenciesMicros[(int)(rounds * 0.99)];
        double bytesPerRoundTrip = (double)allocDeltaBytes / rounds;

        Console.WriteLine($"[NET11-RUNTIME-ASYNC-BENCH] rounds={rounds} p50_us={p50Micros:F2} p99_us={p99Micros:F2} total_alloc_bytes={allocDeltaBytes} alloc_per_rtt_bytes={bytesPerRoundTrip:F1}");
        output?.WriteLine($"[NET11-RUNTIME-ASYNC-BENCH] rounds={rounds} p50_us={p50Micros:F2} p99_us={p99Micros:F2} total_alloc_bytes={allocDeltaBytes} alloc_per_rtt_bytes={bytesPerRoundTrip:F1}");

        p99Micros.Should().BeLessThan(2000.0, "Loopback ClientNetcodeRelay round-trip p99 must stay under 2.0 ms");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void SubMillisecond_Latency_Compression_And_Decompression_Benchmark()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange - Generate realistic 15-entity snapshot delta payload (~1KB)
        SnapshotFrameDelta frame = new()
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = 500,
                ServerTick = 25000,
                ClientAckSequence = 499,
                EntityCount = 15
            }
        };

        Random rng = new(999);
        for (int i = 0; i < 15; i++)
        {
            frame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = (ushort)(1000 + i),
                EntityType = (byte)(i < 5 ? 1 : 2),
                ChangedMask = DeltaFieldMask.FullState,
                Coordinates = new EntityCoordinateDelta
                {
                    PosX = 8192.0f + (float)rng.NextDouble() * 100.0f,
                    PosY = 8192.0f + (float)rng.NextDouble() * 100.0f,
                    PosZ = 128.0f,
                    Yaw = 1000,
                    Pitch = 0,
                    VelocityX = 200,
                    VelocityY = 100,
                    VelocityZ = 0,
                    MovementFlags = 1
                },
                Combat = new EntityCombatDelta
                {
                    CurrentHealth = 1000.0f,
                    MaxHealth = 1200.0f,
                    CurrentMana = 400.0f,
                    MaxMana = 600.0f,
                    AttackCooldown = 0.2f,
                    CastCooldown = 0.0f,
                    StatusFlags = 0,
                    Armor = 6.0f,
                    MagicArmor = 5.5f
                },
                Animation = new EntityAnimationDelta
                {
                    AnimationId = 3,
                    SequenceIndex = 0,
                    CurrentFrame = 12,
                    PlaybackSpeed = 1.0f,
                    BlendWeight = 255
                },
                Inventory = new EntityInventoryDelta
                {
                    Slot0 = new ItemSlotDelta { ItemId = 50, Charges = 1, CooldownRemaining = 0 },
                    Slot1 = new ItemSlotDelta { ItemId = 60, Charges = 0, CooldownRemaining = 0 },
                    Slot2 = new ItemSlotDelta(),
                    Slot3 = new ItemSlotDelta(),
                    Slot4 = new ItemSlotDelta(),
                    Slot5 = new ItemSlotDelta(),
                    Stash0 = new ItemSlotDelta(),
                    Stash1 = new ItemSlotDelta(),
                    Stash2 = new ItemSlotDelta()
                },
                Orders = new EntityOrderDelta
                {
                    OrderType = 1,
                    TargetEntityId = 0,
                    TargetPosX = 8200.0f,
                    TargetPosY = 8200.0f,
                    TargetPosZ = 128.0f,
                    AbilitySlot = 0
                }
            });
        }

        byte[] rawPayload = frame.Serialize();
        byte[] compressedBuffer = new byte[rawPayload.Length + 128];
        byte[] decompressedBuffer = new byte[rawPayload.Length + 128];

        // Warm up JIT and cache
        for (int i = 0; i < 50; i++)
        {
            int cLen = ZstdDeltaCompressor.CompressDelta(rawPayload, compressedBuffer, level: 3);
            _ = ZstdDeltaCompressor.TryDecompressDelta(compressedBuffer.AsSpan(0, cLen), decompressedBuffer, out _);
        }

        // Benchmark 1,000 compression cycles
        const int iterations = 1000;
        Stopwatch compStopwatch = Stopwatch.StartNew();
        int finalCompressedLen = 0;

        for (int i = 0; i < iterations; i++)
        {
            finalCompressedLen = ZstdDeltaCompressor.CompressDelta(rawPayload, compressedBuffer, level: 3);
        }
        compStopwatch.Stop();

        // Benchmark 1,000 decompression cycles
        Stopwatch decompStopwatch = Stopwatch.StartNew();
        int finalDecompressedLen = 0;

        for (int i = 0; i < iterations; i++)
        {
            _ = ZstdDeltaCompressor.TryDecompressDelta(compressedBuffer.AsSpan(0, finalCompressedLen), decompressedBuffer, out finalDecompressedLen);
        }
        decompStopwatch.Stop();

        double avgCompressionMicros = (compStopwatch.Elapsed.TotalMicroseconds) / iterations;
        double avgDecompressionMicros = (decompStopwatch.Elapsed.TotalMicroseconds) / iterations;
        double compressionRatio = (1.0 - ((double)finalCompressedLen / rawPayload.Length)) * 100.0;

        output?.WriteLine($"=== Zstandard Netcode Benchmark Results ===");
        output?.WriteLine($"Raw Payload Size:          {rawPayload.Length} bytes");
        output?.WriteLine($"Compressed Payload Size:   {finalCompressedLen} bytes");
        output?.WriteLine($"Compression Ratio:         {compressionRatio:F2}% reduction");
        output?.WriteLine($"Avg Compression Latency:   {avgCompressionMicros:F2} µs ({avgCompressionMicros / 1000.0:F4} ms)");
        output?.WriteLine($"Avg Decompression Latency: {avgDecompressionMicros:F2} µs ({avgDecompressionMicros / 1000.0:F4} ms)");

        // Assert sub-millisecond execution (< 1.0 ms = 1000 µs)
        avgCompressionMicros.Should().BeLessThan(1000.0, "Compression must complete in sub-millisecond budget");
        avgDecompressionMicros.Should().BeLessThan(500.0, "Decompression must complete in sub-millisecond budget");
        finalDecompressedLen.Should().Be(rawPayload.Length);
        decompressedBuffer.AsSpan(0, finalDecompressedLen).SequenceEqual(rawPayload).Should().BeTrue();
    }
}
