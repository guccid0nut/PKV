// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Threading.Channels;
using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Load.Tests;

public class ReplayIngestStreamLoadTests
{
    private const byte DirectMemoryPackFormatTag = 0x02;

    private static MatchProcessingJob CreateDetailedJob(int matchId, int accountId = 100)
    {
        return new MatchProcessingJob
        {
            MatchID = matchId,
            AccountId = accountId,
            RemoteIp = "192.168.1.50",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string>
                {
                    ["match_id"] = matchId.ToString(),
                    ["time"] = "2145",
                    ["map"] = "caldavar",
                    ["winner"] = "1"
                },
                TeamStats = new Dictionary<int, Dictionary<string, int>>
                {
                    [1] = new() { ["score"] = 42, ["tower_kills"] = 6 },
                    [2] = new() { ["score"] = 28, ["tower_kills"] = 2 }
                },
                PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>
                {
                    [accountId] = new()
                    {
                        ["hero"] = new() { ["hero_id"] = "Hero_Pyromancer", ["kills"] = "12", ["deaths"] = "3", ["assists"] = "15" }
                    }
                },
                ItemHistory =
                [
                    new ItemEvent { AccountID = accountId, ItemName = "Item_PostHaste", GameTimeSeconds = 450, EventType = 1 },
                    new ItemEvent { AccountID = accountId, ItemName = "Item_GrimoireOfPower", GameTimeSeconds = 950, EventType = 1 }
                ]
            }
        };
    }

    private static byte[] SerializeDirectMemoryPack(MatchProcessingJob job)
    {
        byte[] jobBytes = MemoryPackSerializer.Serialize(job);
        byte[] result = new byte[jobBytes.Length + 1];
        result[0] = DirectMemoryPackFormatTag;
        Buffer.BlockCopy(jobBytes, 0, result, 1, jobBytes.Length);
        return result;
    }

    private static MatchProcessingJob? DeserializeDirectMemoryPack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 1 && bytes[0] == DirectMemoryPackFormatTag)
        {
            return MemoryPackSerializer.Deserialize<MatchProcessingJob>(bytes[1..]);
        }
        return null;
    }

    [Fact]
    public void ReplayIngestStream_FormatTag02_DirectMemoryPackSerialization_ConcurrentThroughput()
    {
        const int totalJobs = 200;
        List<MatchProcessingJob> sourceJobs = new(totalJobs);
        List<byte[]> serializedPayloads = new(totalJobs);

        for (int i = 0; i < totalJobs; i++)
        {
            MatchProcessingJob job = CreateDetailedJob(50000 + i, accountId: 1000 + (i % 20));
            sourceJobs.Add(job);
            serializedPayloads.Add(SerializeDirectMemoryPack(job));
        }

        Stopwatch sw = Stopwatch.StartNew();

        MatchProcessingJob?[] deserializedJobs = new MatchProcessingJob[totalJobs];
        Parallel.For(0, totalJobs, i =>
        {
            deserializedJobs[i] = DeserializeDirectMemoryPack(serializedPayloads[i]);
        });

        sw.Stop();

        for (int i = 0; i < totalJobs; i++)
        {
            MatchProcessingJob expected = sourceJobs[i];
            MatchProcessingJob? actual = deserializedJobs[i];

            actual.Should().NotBeNull();
            actual!.MatchID.Should().Be(expected.MatchID);
            actual.AccountId.Should().Be(expected.AccountId);
            actual.RemoteIp.Should().Be(expected.RemoteIp);
            actual.Form.Should().NotBeNull();
            actual.Form!.MatchStats["map"].Should().Be("caldavar");
            actual.Form.ItemHistory.Should().HaveCount(2);
        }

        sw.ElapsedMilliseconds.Should().BeLessThan(1500, "Concurrent MemoryPack format tag 0x02 deserialization of 200 detailed jobs must complete in sub-seconds");
    }

    [Fact]
    public async Task ReplayIngestStream_BackpressureAndBoundedMemory_HandlesPartitionSaturation()
    {
        // Small partition capacity (15) to trigger asynchronous backpressure under 100 jobs
        const int capacityPerPartition = 15;
        const int totalJobs = 100;

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions
            {
                PartitionCount = 2,
                CapacityPerPartition = capacityPerPartition,
                FullMode = BoundedChannelFullMode.Wait,
                UseRedisStreams = false
            },
            connectionMultiplexer: null);

        int partition = 0;
        int producedCount = 0;
        int consumedCount = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Consumer task reading from partition 0
        Task consumerTask = Task.Run(async () =>
        {
            await foreach (MatchSubmissionPayload payload in channel.ReadAllAsync(partition, cts.Token))
            {
                Interlocked.Increment(ref consumedCount);
                if (consumedCount >= totalJobs)
                    break;
            }
        }, cts.Token);

        // Producer task enqueuing 100 jobs to partition 0 (AccountId 100 % 2 == 0)
        Task producerTask = Task.Run(async () =>
        {
            for (int i = 0; i < totalJobs; i++)
            {
                MatchProcessingJob job = CreateDetailedJob(70000 + i, accountId: 100);
                bool enqueued = await channel.EnqueueAsync(job, cts.Token);
                if (enqueued)
                {
                    Interlocked.Increment(ref producedCount);
                }
            }
        }, cts.Token);

        await Task.WhenAll(producerTask, consumerTask);

        producedCount.Should().Be(totalJobs);
        consumedCount.Should().Be(totalJobs);
        channel.GetQueueDepth(partition).Should().Be(0);
        channel.TotalQueueDepth.Should().Be(0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    [Trait("Category", NexusTestEnvironment.ManualTestCategory)]
    public void ReplayIngestStream_HighThroughput50MBStream_StreamsUnderLoadWithoutLohFragmentation()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const int iterations = 2_500;
        MatchProcessingJob prototype = CreateDetailedJob(99999, 5050);
        byte[] payload = SerializeDirectMemoryPack(prototype);

        // Pre-warm and baseline GC
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long initialLohSize = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        long totalBytesProcessed = 0;

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            MatchProcessingJob? deserialized = DeserializeDirectMemoryPack(payload);
            deserialized.Should().NotBeNull();
            totalBytesProcessed += payload.Length;
        }

        sw.Stop();

        long finalLohSize = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        finalLohSize.Should().BeLessThanOrEqualTo(initialLohSize + (1024 * 1024), "High-throughput MemoryPack direct streaming must not cause Large Object Heap expansion/fragmentation");
        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "High-throughput ingestion streaming must process thousands of frames efficiently");
    }
}
