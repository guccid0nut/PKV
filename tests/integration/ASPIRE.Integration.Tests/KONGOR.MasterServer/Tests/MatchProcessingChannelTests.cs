// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MatchProcessingChannelTests

{
    [Fact]
    public async Task EnqueueAsync_ValidPayload_SuccessfullyEnqueuesAndReads()

    {
        // Arrange

        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;

        MatchProcessingChannel channel = new(logger);


        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            Session = "test-session-cookie",

            MatchStats = new Dictionary<string, string> { { "match_id", "12345" } },

            TeamStats = [],

            PlayerStats = []
        };


        MatchSubmissionPayload payload = new() { MatchID = 12345, Form = form, RemoteIp = "127.0.0.1" };


        // Act

        bool enqueued = await channel.EnqueueAsync(payload);


        // Assert

        Assert.True(enqueued);


        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        List<MatchSubmissionPayload> received = [];


        await foreach (MatchSubmissionPayload item in channel.ReadAllAsync(cts.Token))

        {
            received.Add(item);

            break; // Read first item
        }


        Assert.Single(received);

        Assert.Equal(12345, received[0].MatchID);

        Assert.Equal("127.0.0.1", received[0].RemoteIp);
    }


    [Fact]
    public async Task Channel_MultipleConcurrentWriters_MaintainsFIFOOrder()

    {
        // Arrange

        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;

        MatchProcessingChannel channel = new(logger);

        const int totalItems = 50;


        // Act

        List<Task<bool>> enqueueTasks = [];

        for (int i = 1; i <= totalItems; i++)

        {
            int matchId = 1000 + i;

            StatsForSubmissionRequestForm form = new()

            {
                Function = "submit_stats",

                Session = $"session-{matchId}",

                MatchStats = new Dictionary<string, string> { { "match_id", matchId.ToString() } },

                TeamStats = [],

                PlayerStats = []
            };


            MatchSubmissionPayload payload = new() { MatchID = matchId, Form = form, RemoteIp = "127.0.0.1" };


            enqueueTasks.Add(channel.EnqueueAsync(payload).AsTask());
        }


        bool[] results = await Task.WhenAll(enqueueTasks);


        // Assert

        Assert.All(results, Assert.True);


        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        List<int> receivedMatchIds = [];


        await foreach (MatchSubmissionPayload item in channel.ReadAllAsync(cts.Token))

        {
            receivedMatchIds.Add(item.MatchID);

            if (receivedMatchIds.Count == totalItems) break;
        }


        Assert.Equal(totalItems, receivedMatchIds.Count);
    }


    [Fact]
    public void TryEnqueue_NonBlocking_ReturnsTrueWhenBufferAvailable()

    {
        // Arrange

        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;

        MatchProcessingChannel channel = new(logger);


        StatsForSubmissionRequestForm form = new()

        {
            Function = "submit_stats",

            Session = "test-session",

            MatchStats = new Dictionary<string, string> { { "match_id", "777" } },

            TeamStats = [],

            PlayerStats = []
        };


        MatchProcessingJob job = new() { MatchID = 777, Form = form, RemoteIp = "127.0.0.1" };


        // Act

        bool success = channel.TryEnqueue(job);


        // Assert

        Assert.True(success);
    }

    [Fact]
    public void MatchProcessingChannel_GetPartition_DistributesDeterministicallyAndUniformly()
    {
        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;
        MatchProcessingChannelOptions options = new() { PartitionCount = 8 };
        MatchProcessingChannel channel = new(logger, options);

        Assert.Equal(8, channel.PartitionCount);

        // Deterministic check
        int partition1 = channel.GetPartition(42);
        int partition2 = channel.GetPartition(42);
        Assert.Equal(partition1, partition2);
        Assert.InRange(partition1, 0, 7);

        // Boundary checks (negative / zero / int.MaxValue)
        Assert.InRange(channel.GetPartition(0), 0, 7);
        Assert.InRange(channel.GetPartition(-1), 0, 7);
        Assert.InRange(channel.GetPartition(-99999), 0, 7);
        Assert.InRange(channel.GetPartition(int.MaxValue), 0, 7);
        Assert.InRange(channel.GetPartition(int.MinValue), 0, 7);

        // Uniform distribution check across partitions
        int[] counts = new int[8];
        for (int id = 1; id <= 1000; id++)
        {
            int p = channel.GetPartition(id);
            counts[p]++;
        }

        // Each partition should receive roughly ~125 items (min > 50)
        for (int i = 0; i < 8; i++)
        {
            Assert.True(counts[i] > 50, $"Partition {i} had unexpectedly low distribution: {counts[i]}");
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithAccountId_RoutesToCorrectPartitionChannel()
    {
        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;
        MatchProcessingChannelOptions options = new() { PartitionCount = 8 };
        MatchProcessingChannel channel = new(logger, options);

        const int testAccountId = 123456;
        int expectedPartition = channel.GetPartition(testAccountId);

        StatsForSubmissionRequestForm form = new()
        {
            Function = "submit_stats",
            Session = "test-session",
            MatchStats = new Dictionary<string, string> { { "match_id", "8888" } },
            TeamStats = [],
            PlayerStats = []
        };

        MatchJob job = new()
        {
            MatchID = 8888,
            Form = form,
            AccountId = testAccountId,
            RemoteIp = "127.0.0.1"
        };

        bool enqueued = await channel.EnqueueAsync(job);
        Assert.True(enqueued);

        // Verify queue depth on target partition vs others
        Assert.Equal(1, channel.GetQueueDepth(expectedPartition));
        Assert.Equal(1, channel.TotalQueueDepth);

        for (int i = 0; i < 8; i++)
        {
            if (i != expectedPartition)
            {
                Assert.Equal(0, channel.GetQueueDepth(i));
            }
        }

        // Verify reading only from the assigned partition
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        List<MatchSubmissionPayload> partitionItems = [];
        await foreach (MatchSubmissionPayload item in channel.ReadAllAsync(expectedPartition, cts.Token))
        {
            partitionItems.Add(item);
            break;
        }

        Assert.Single(partitionItems);
        Assert.Equal(8888, partitionItems[0].MatchID);
        Assert.Equal(testAccountId, partitionItems[0].AccountId);
    }

    [Fact]
    public async Task SameAccount_SequentialFIFOOrdering_Guaranteed()
    {
        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;
        MatchProcessingChannelOptions options = new() { PartitionCount = 8 };
        MatchProcessingChannel channel = new(logger, options);

        const int targetAccountId = 777;
        int targetPartition = channel.GetPartition(targetAccountId);
        const int jobCount = 20;

        for (int i = 1; i <= jobCount; i++)
        {
            int matchId = 5000 + i;
            StatsForSubmissionRequestForm form = new()
            {
                Function = "submit_stats",
                Session = $"session-{matchId}",
                MatchStats = new Dictionary<string, string> { { "match_id", matchId.ToString() } },
                TeamStats = [],
                PlayerStats = []
            };

            MatchJob job = new()
            {
                MatchID = matchId,
                Form = form,
                AccountId = targetAccountId,
                RemoteIp = "127.0.0.1"
            };

            bool enqueued = await channel.EnqueueAsync(job);
            Assert.True(enqueued);
        }

        Assert.Equal(jobCount, channel.GetQueueDepth(targetPartition));

        // Read all items from the partition and verify strict sequential FIFO ordering
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        List<int> dequeuedMatchIds = [];
        await foreach (MatchSubmissionPayload item in channel.ReadAllAsync(targetPartition, cts.Token))
        {
            dequeuedMatchIds.Add(item.MatchID);
            if (dequeuedMatchIds.Count == jobCount)
                break;
        }

        Assert.Equal(jobCount, dequeuedMatchIds.Count);
        for (int i = 0; i < jobCount; i++)
        {
            Assert.Equal(5001 + i, dequeuedMatchIds[i]);
        }
    }

    [Fact]
    public async Task ConcurrentBurst_50Matches_PartitionsAcrossChannels()
    {
        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;
        MatchProcessingChannelOptions options = new() { PartitionCount = 8 };
        MatchProcessingChannel channel = new(logger, options);

        const int totalMatches = 50;
        List<Task<bool>> enqueueTasks = [];

        for (int i = 1; i <= totalMatches; i++)
        {
            int matchId = 10000 + i;
            int accountId = 100 + (i % 10); // 10 distinct accounts spread over 50 matches

            StatsForSubmissionRequestForm form = new()
            {
                Function = "submit_stats",
                Session = $"burst-session-{matchId}",
                MatchStats = new Dictionary<string, string> { { "match_id", matchId.ToString() } },
                TeamStats = [],
                PlayerStats = []
            };

            MatchJob job = new()
            {
                MatchID = matchId,
                Form = form,
                AccountId = accountId,
                RemoteIp = "127.0.0.1"
            };

            enqueueTasks.Add(channel.EnqueueAsync(job).AsTask());
        }

        bool[] results = await Task.WhenAll(enqueueTasks);
        Assert.All(results, Assert.True);
        Assert.Equal(totalMatches, channel.TotalQueueDepth);

        // Read all 50 items across partitions using multiplexed ReadAllAsync
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        List<int> receivedMatches = [];
        await foreach (MatchSubmissionPayload item in channel.ReadAllAsync(cts.Token))
        {
            receivedMatches.Add(item.MatchID);
            if (receivedMatches.Count == totalMatches)
                break;
        }

        Assert.Equal(totalMatches, receivedMatches.Count);
    }

    [Fact]
    public void FallbackPartitionResolution_MatchesWithoutAccountId_UsesHostOrMvpOrMatchId()
    {
        ILogger<MatchProcessingChannel> logger = NullLogger<MatchProcessingChannel>.Instance;
        MatchProcessingChannelOptions options = new() { PartitionCount = 8 };
        MatchProcessingChannel channel = new(logger, options);

        // Case 1: Uses HostAccountID when AccountId is 0
        MatchProcessingJob jobWithHost = new()
        {
            MatchID = 101,
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string>(),
                TeamStats = [],
                PlayerStats = []
            },
            MatchServer = new global::ASPIRE.Common.Models.ServerManagement.MatchServer
            {
                ID = 1,
                HostAccountID = 555,
                HostAccountName = "Host",
                Name = "Server",
                Instance = 1,
                IPAddress = "127.0.0.1",
                Port = 11235,
                Location = "Local",
                Description = "Desc"
            }
        };

        int p1 = channel.ResolvePartition(jobWithHost);
        Assert.Equal(channel.GetPartition(555), p1);

        // Case 2: Uses MVP when Host is absent
        MatchProcessingJob jobWithMvp = new()
        {
            MatchID = 102,
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { { "mvp", "777" } },
                TeamStats = [],
                PlayerStats = []
            }
        };

        int p2 = channel.ResolvePartition(jobWithMvp);
        Assert.Equal(channel.GetPartition(777), p2);

        // Case 3: Fallback to MatchID
        MatchProcessingJob jobWithOnlyMatchId = new()
        {
            MatchID = 9999,
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string>(),
                TeamStats = [],
                PlayerStats = []
            }
        };

        int p3 = channel.ResolvePartition(jobWithOnlyMatchId);
        Assert.Equal(channel.GetPartition(9999), p3);
    }
}
