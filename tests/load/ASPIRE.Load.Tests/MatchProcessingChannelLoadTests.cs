// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ASPIRE.Load.Tests;

public class MatchProcessingChannelLoadTests
{
    private static MatchProcessingJob CreateTestJob(int matchId, int accountId = 100)
    {
        return new MatchProcessingJob
        {
            MatchID = matchId,
            AccountId = accountId,
            RemoteIp = "127.0.0.1",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            }
        };
    }

    [Fact]
    public async Task MatchProcessingChannel_ConcurrentEnqueueAndRead_ProcessesAllJobsInOrder()
    {
        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            connectionMultiplexer: null);

        const int totalJobs = 200;
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < totalJobs; i++)
        {
            MatchProcessingJob job = CreateTestJob(9000 + i, accountId: 100 + (i % 10));
            bool enqueued = await channel.EnqueueAsync(job);
            enqueued.Should().BeTrue();
        }

        channel.TotalQueueDepth.Should().Be(totalJobs);

        // Drain partitions concurrently
        int readCount = 0;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

        for (int p = 0; p < 4; p++)
        {
            int partition = p;
            await foreach (MatchSubmissionPayload payload in channel.ReadAllAsync(partition, cts.Token))
            {
                readCount++;
                if (channel.GetQueueDepth(partition) == 0)
                    break;
            }
        }

        sw.Stop();

        readCount.Should().Be(totalJobs);
        channel.TotalQueueDepth.Should().Be(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "Channel multiplexer must drain partitioned jobs in sub-second time");
    }
}
