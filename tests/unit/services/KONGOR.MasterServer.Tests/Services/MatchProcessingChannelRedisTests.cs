// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class MatchProcessingChannelRedisTests
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
    public async Task EnqueueAsync_WithoutRedis_WritesToInMemoryPartitionChannel()
    {
        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            connectionMultiplexer: null);

        MatchProcessingJob job = CreateTestJob(9001, accountId: 100);
        bool enqueued = await channel.EnqueueAsync(job);

        enqueued.Should().BeTrue();
        int partition = channel.ResolvePartition(job);
        channel.GetQueueDepth(partition).Should().Be(1);
        channel.TotalQueueDepth.Should().Be(1);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await foreach (MatchSubmissionPayload payload in channel.ReadAllAsync(partition, cts.Token))
        {
            payload.MatchID.Should().Be(9001);
            break;
        }

        channel.GetQueueDepth(partition).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_WithRedis_PublishesToRedisStream()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamAddAsync(
                "kongor:stream:matches",
                It.IsAny<NameValueEntry[]>(),
                null,
                null,
                false,
                null,
                StreamTrimMode.KeepReferences,
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("1700000000000-0"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4, StreamKey = "kongor:stream:matches" },
            mockMultiplexer.Object);

        MatchProcessingJob job = CreateTestJob(9002, accountId: 101);
        bool enqueued = await channel.EnqueueAsync(job);

        enqueued.Should().BeTrue();
        mockDatabase.Verify(d => d.StreamAddAsync(
            "kongor:stream:matches",
            It.Is<NameValueEntry[]>(entries => entries.Any(e => e.Name == "matchId" && e.Value == 9002)),
            null,
            null,
            false,
            null,
            StreamTrimMode.KeepReferences,
            CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_WhenRedisThrows_FallsBackToInMemoryChannel()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                null,
                null,
                false,
                null,
                StreamTrimMode.KeepReferences,
                CommandFlags.None))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis offline"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            mockMultiplexer.Object);

        MatchProcessingJob job = CreateTestJob(9003, accountId: 102);
        bool enqueued = await channel.EnqueueAsync(job);

        enqueued.Should().BeTrue();
        int partition = channel.ResolvePartition(job);
        channel.GetQueueDepth(partition).Should().Be(1);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithRedisStreamMessageId_CallsStreamAcknowledge()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamAcknowledgeAsync(
                "kongor:stream:matches",
                "kongor_match_workers",
                "1700000000000-0",
                CommandFlags.None))
            .ReturnsAsync(1);

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { StreamKey = "kongor:stream:matches", ConsumerGroup = "kongor_match_workers" },
            mockMultiplexer.Object);

        MatchProcessingJob job = CreateTestJob(9004);
        job.RedisStreamMessageId = "1700000000000-0";

        await channel.AcknowledgeAsync(job);

        mockDatabase.Verify(d => d.StreamAcknowledgeAsync(
            "kongor:stream:matches",
            "kongor_match_workers",
            "1700000000000-0",
            CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task MatchProcessingBackgroundWorker_WhenMatchSucceeds_AcknowledgesJob()
    {
        Mock<IMatchProcessingChannel> mockChannel = new();
        Mock<IMatchProcessingService> mockService = new();
        Mock<IServiceScopeFactory> mockScopeFactory = new();
        Mock<IServiceScope> mockScope = new();
        Mock<IServiceProvider> mockServiceProvider = new();

        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
        mockServiceProvider.Setup(p => p.GetService(typeof(IMatchProcessingService)))
            .Returns(mockService.Object);

        MatchProcessingBackgroundWorker worker = new(
            mockChannel.Object,
            mockScopeFactory.Object,
            NullLogger<MatchProcessingBackgroundWorker>.Instance);

        MatchSubmissionPayload payload = new()
        {
            MatchID = 9005,
            AccountId = 100,
            RedisStreamMessageId = "stream-msg-9005",
            Form = new StatsForSubmissionRequestForm
            {
                Function = "submit_stats",
                MatchStats = new Dictionary<string, string> { ["match_id"] = "9005" },
                TeamStats = [],
                PlayerStats = []
            }
        };

        await worker.ProcessMatchPayloadAsync(payload, 0, CancellationToken.None);

        mockService.Verify(s => s.ProcessMatchSubmissionAsync(payload, It.IsAny<CancellationToken>()), Times.Once);
        mockChannel.Verify(c => c.AcknowledgeAsync(payload, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SerializeAndDeserialize_DirectMemoryPackBinary_RoundTripsAccurately()
    {
        MatchProcessingJob originalJob = CreateTestJob(9005, accountId: 105);
        byte[] payloadBytes = MemoryPack.MemoryPackSerializer.Serialize(originalJob);
        MatchJobStreamEnvelope envelope = new()
        {
            MatchId = originalJob.MatchID,
            AccountId = originalJob.AccountId,
            Partition = 1,
            Payload = payloadBytes,
            SerializationFormat = 1,
            EnqueuedAt = originalJob.EnqueuedAt
        };

        byte[] envelopeBytes = MemoryPack.MemoryPackSerializer.Serialize(envelope);
        envelopeBytes.Should().NotBeEmpty();

        MatchJobStreamEnvelope? deserializedEnvelope = MemoryPack.MemoryPackSerializer.Deserialize<MatchJobStreamEnvelope>(envelopeBytes);
        deserializedEnvelope.Should().NotBeNull();
        deserializedEnvelope!.SerializationFormat.Should().Be(1);

        MatchProcessingJob? deserializedJob = MemoryPack.MemoryPackSerializer.Deserialize<MatchProcessingJob>(deserializedEnvelope.Payload);
        deserializedJob.Should().NotBeNull();
        deserializedJob!.MatchID.Should().Be(9005);
        deserializedJob.AccountId.Should().Be(105);
        deserializedJob.Form!.Function.Should().Be("submit_stats");
    }

    [Fact]
    public async Task ReadAllAsync_MultiPartition_MultiplexesAcrossPartitionsWithoutThrashing()
    {
        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            connectionMultiplexer: null);

        for (int i = 1; i <= 4; i++)
        {
            MatchProcessingJob job = CreateTestJob(9100 + i, accountId: i * 10);
            bool enqueued = await channel.EnqueueAsync(job);
            enqueued.Should().BeTrue();
        }

        channel.TotalQueueDepth.Should().Be(4);

        List<MatchSubmissionPayload> consumed = [];
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await foreach (MatchSubmissionPayload payload in channel.ReadAllAsync(cts.Token))
        {
            consumed.Add(payload);
            if (consumed.Count == 4) break;
        }

        consumed.Should().HaveCount(4);
        consumed.Select(p => p.MatchID).Should().BeEquivalentTo([9101, 9102, 9103, 9104]);
        channel.TotalQueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task EnsureConsumerGroupAsync_WhenGarnetReturnsUnknownCommand_DisablesStreamsAndDegradesToInMemoryChannel()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            mockMultiplexer.Object);

        channel.IsRedisStreamsSupported.Should().BeTrue();

        MatchProcessingJob job1 = CreateTestJob(9201, accountId: 201);
        bool enqueued1 = await channel.EnqueueAsync(job1);

        enqueued1.Should().BeTrue();
        channel.IsRedisStreamsSupported.Should().BeFalse();

        int partition1 = channel.ResolvePartition(job1);
        channel.GetQueueDepth(partition1).Should().Be(1);

        // Subsequent enqueue should directly use in-memory channel without calling StreamAddAsync
        MatchProcessingJob job2 = CreateTestJob(9202, accountId: 201);
        bool enqueued2 = await channel.EnqueueAsync(job2);

        enqueued2.Should().BeTrue();
        channel.GetQueueDepth(partition1).Should().Be(2);

        mockDatabase.Verify(d => d.StreamAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<NameValueEntry[]>(),
            null,
            null,
            false,
            CommandFlags.None), Times.Never);
    }

    [Fact]
    public async Task ReadAllAsync_WhenGarnetReturnsUnknownCommand_TransitionsToInMemoryWithoutThrowing()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'xgroup'"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            mockMultiplexer.Object);

        MatchProcessingJob job = CreateTestJob(9203, accountId: 202);
        int partition = channel.ResolvePartition(job);

        // Enqueue directly (which will fail stream and write in-memory)
        await channel.EnqueueAsync(job);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        List<MatchSubmissionPayload> read = [];

        await foreach (MatchSubmissionPayload payload in channel.ReadAllAsync(partition, cts.Token))
        {
            read.Add(payload);
            break;
        }

        read.Should().ContainSingle();
        read[0].MatchID.Should().Be(9203);
        channel.IsRedisStreamsSupported.Should().BeFalse();
    }

    [Fact]
    public void TryEnqueue_WhenGarnetReturnsUnknownCommand_DisablesStreamsAndEnqueuesInMemory()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamAdd(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()))
            .Throws(new RedisServerException("ERR unknown command 'XADD'"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            mockMultiplexer.Object);

        MatchProcessingJob job = CreateTestJob(9204, accountId: 203);
        bool enqueued = channel.TryEnqueue(job);

        enqueued.Should().BeTrue();
        channel.IsRedisStreamsSupported.Should().BeFalse();

        int partition = channel.ResolvePartition(job);
        channel.GetQueueDepth(partition).Should().Be(1);

        // Second TryEnqueue immediately writes in-memory without calling StreamAdd
        MatchProcessingJob job2 = CreateTestJob(9205, accountId: 203);
        bool enqueued2 = channel.TryEnqueue(job2);

        enqueued2.Should().BeTrue();
        channel.GetQueueDepth(partition).Should().Be(2);

        mockDatabase.Verify(d => d.StreamAdd(
            It.IsAny<RedisKey>(),
            It.IsAny<NameValueEntry[]>(),
            It.IsAny<RedisValue?>(),
            It.IsAny<long?>(),
            It.IsAny<bool>(),
            It.IsAny<long?>(),
            It.IsAny<StreamTrimMode>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task AcknowledgeAsync_WhenGarnetReturnsUnknownCommand_DisablesStreamsWithoutThrowing()
    {
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();

        mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'XACK'"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions { PartitionCount = 4 },
            mockMultiplexer.Object);

        MatchProcessingJob job = CreateTestJob(9206);
        job.RedisStreamMessageId = "msg-123";

        await channel.AcknowledgeAsync(job);

        channel.IsRedisStreamsSupported.Should().BeFalse();

        // Second acknowledge should immediately return without touching Redis
        await channel.AcknowledgeAsync(job);

        mockDatabase.Verify(d => d.StreamAcknowledgeAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }
}

