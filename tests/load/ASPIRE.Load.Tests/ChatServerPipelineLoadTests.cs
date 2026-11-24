// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using Xunit;

namespace ASPIRE.Load.Tests;

public class ChatServerPipelineLoadTests
{
    [Fact]
    public async Task ChatPipeline_ConcurrentSessionBursts_ProcessesThroughputWithoutStarvation()
    {
        ServiceCollection services = new();
        Mock<IChatContext> mockContext = new();
        services.AddSingleton(mockContext.Object);
        using ServiceProvider sp = services.BuildServiceProvider();

        const int sessionCount = 50;
        const int packetsPerSession = 50;
        int totalPackets = sessionCount * packetsPerSession;

        // Verify precompiled registry is ready
        ChatCommandRegistry.RegisteredCount.Should().BeGreaterThan(10);

        Stopwatch sw = Stopwatch.StartNew();
        long workerAllocatedBytes = 0;

        Task[] sessionTasks = new Task[sessionCount];
        int processedCount = 0;

        for (int s = 0; s < sessionCount; s++)
        {
            sessionTasks[s] = Task.Run(() =>
            {
                long threadAllocStart = GC.GetAllocatedBytesForCurrentThread();
                SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: 100, maxBurstCapacity: 200);

                for (int p = 0; p < packetsPerSession; p++)
                {
                    bool allowed = limiter.TryConsume(1);
                    allowed.Should().BeTrue();

                    // Check descriptor caching without per-packet factory allocation
                    if (ChatCommandRegistry.TryGetDescriptor((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER, out ChatCommandDescriptor? descriptor))
                    {
                        descriptor.Should().NotBeNull();
                        Interlocked.Increment(ref processedCount);
                    }
                }

                long threadAllocDelta = GC.GetAllocatedBytesForCurrentThread() - threadAllocStart;
                Interlocked.Add(ref workerAllocatedBytes, threadAllocDelta);
            });
        }

        await Task.WhenAll(sessionTasks);
        sw.Stop();
        long totalAllocated = workerAllocatedBytes;

        processedCount.Should().Be(totalPackets);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "High-concurrency packet processing must complete within 2 seconds");

        // Verify allocation sanity: less than 4 KB per packet operation including tasks
        long bytesPerPacket = totalAllocated / totalPackets;
        bytesPerPacket.Should().BeLessThan(4096, "Pipeline must avoid excessive heap allocations per packet");
    }

    [Fact]
    public async Task ChatPipeline_TokenBucketRateLimiter_EnforcesBackpressureUnderFlood()
    {
        const int capacity = 100;
        const int refillRate = 20;
        SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: refillRate, maxBurstCapacity: capacity);

        int allowedCount = 0;
        int rejectedCount = 0;

        // Flood with 300 rapid requests
        for (int i = 0; i < 300; i++)
        {
            if (limiter.TryConsume(1))
                allowedCount++;
            else
                rejectedCount++;
        }

        allowedCount.Should().Be(capacity, "Token bucket capacity must cap immediate burst consumption");
        rejectedCount.Should().Be(200, "Packets exceeding bucket capacity must be backpressured/rejected");

        // After waiting 150ms, tokens should refill
        await Task.Delay(150);
        limiter.TryConsume(1).Should().BeTrue("Tokens must refill smoothly over time");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void ChatPipeline_HighVolumeStressLoad_SustainsThroughput()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const int iterations = 100_000;
        SessionTokenBucketRateLimiter limiter = new(refillRatePerSecond: iterations, maxBurstCapacity: iterations);

        Stopwatch sw = Stopwatch.StartNew();
        int consumed = 0;

        Parallel.For(0, iterations, _ =>
        {
            if (limiter.TryConsume(1))
                Interlocked.Increment(ref consumed);
        });

        sw.Stop();
        consumed.Should().Be(iterations);
        double opsPerSec = (double)iterations / (sw.ElapsedMilliseconds / 1000.0);
        opsPerSec.Should().BeGreaterThan(500_000, "Rate limiter atomic CAS operations must exceed 500k ops/sec");
    }
}
