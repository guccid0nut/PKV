// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class MatchDeadLetterQueueTests
{
    private static (MatchDeadLetterQueue Queue, Mock<IMatchProcessingChannel> MockChannel) CreateQueue(int maxCapacity = 10)
    {
        Mock<IMatchProcessingChannel> mockChannel = new();
        mockChannel.Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        MatchDeadLetterQueue queue = new(
            mockChannel.Object,
            NullLogger<MatchDeadLetterQueue>.Instance,
            maxCapacity);

        return (queue, mockChannel);
    }

    private static DeadLetterMatchJob CreateTestJob(int matchId, string reason = "Test failure")
    {
        MatchSubmissionPayload payload = new()
        {
            MatchID = matchId,
            RemoteIp = "127.0.0.1",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = matchId.ToString() },
                TeamStats = [],
                PlayerStats = []
            }
        };

        return new DeadLetterMatchJob
        {
            MatchId = matchId,
            PartitionIndex = 0,
            Payload = payload,
            FailureReason = reason,
            ExceptionType = typeof(InvalidOperationException).FullName!,
            StackTrace = "at Test()",
            FailedAt = TimeProvider.System.GetUtcNow()
        };
    }

    [Fact]
    public async Task EnqueueAsync_ValidJob_IncrementsCountAndRecordsRecentJob()
    {
        // Arrange
        var (queue, _) = CreateQueue();
        DeadLetterMatchJob job = CreateTestJob(101, "Malformed player stats");

        // Act
        await queue.EnqueueAsync(job);

        // Assert
        queue.Count.Should().Be(1);
        IReadOnlyList<DeadLetterMatchJob> recent = queue.GetRecentFailedJobs();
        recent.Should().HaveCount(1);
        recent[0].MatchId.Should().Be(101);
        recent[0].FailureReason.Should().Be("Malformed player stats");
    }

    [Fact]
    public async Task ReplayJobAsync_WhenJobExists_EnqueuesToChannelAndIncreasesAttemptCount()
    {
        // Arrange
        var (queue, mockChannel) = CreateQueue();
        DeadLetterMatchJob job = CreateTestJob(202, "Transient DB timeout");
        await queue.EnqueueAsync(job);

        // Act
        bool replayed = await queue.ReplayJobAsync(202);

        // Assert
        replayed.Should().BeTrue();
        job.AttemptCount.Should().Be(2);
        mockChannel.Verify(c => c.EnqueueAsync(job.Payload, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReplayJobAsync_WhenJobDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var (queue, mockChannel) = CreateQueue();

        // Act
        bool replayed = await queue.ReplayJobAsync(999);

        // Assert
        replayed.Should().BeFalse();
        mockChannel.Verify(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_WhenMaxCapacityExceeded_EvictsOldestJobFIFO()
    {
        // Arrange
        var (queue, _) = CreateQueue(maxCapacity: 3);

        // Act
        await queue.EnqueueAsync(CreateTestJob(1));
        await queue.EnqueueAsync(CreateTestJob(2));
        await queue.EnqueueAsync(CreateTestJob(3));
        await queue.EnqueueAsync(CreateTestJob(4));

        // Assert
        queue.Count.Should().Be(3);
        IReadOnlyList<DeadLetterMatchJob> recent = queue.GetRecentFailedJobs(10);
        recent.Select(j => j.MatchId).Should().ContainInOrder(4, 3, 2);
        recent.Select(j => j.MatchId).Should().NotContain(1);
    }

    [Fact]
    public async Task MatchDeadLetterQueue_ClearAsync_RemovesAllJobs()
    {
        // Arrange
        var (queue, _) = CreateQueue();
        await queue.EnqueueAsync(CreateTestJob(1));
        await queue.EnqueueAsync(CreateTestJob(2));

        // Act
        await queue.ClearAsync();

        // Assert
        queue.Count.Should().Be(0);
        queue.GetRecentFailedJobs().Should().BeEmpty();
    }
}
