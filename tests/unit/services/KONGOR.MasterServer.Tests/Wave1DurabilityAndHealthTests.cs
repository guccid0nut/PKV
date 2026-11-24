// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Telemetry;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class Wave1DurabilityAndHealthTests
{
    [Fact]
    public async Task MatchProcessingChannel_WhenRequireDurableStreamsTrue_FailsLoudAndReportsUnhealthyOnGarnetNoStreams()
    {
        // protects: Production ranked match ingestion durability against silent volatile degradation when cache lacks Redis Streams.
        // fails_when: DisableRedisStreams swallows ERR unknown command 'XGROUP' and reports Healthy while RequireDurableStreams=true.
        // seam: Real MatchProcessingChannel + MatchProcessingChannelHealthCheck + simulated Garnet RedisServerException.

        Mock<IConnectionMultiplexer> redisMock = new();
        Mock<IDatabase> dbMock = new();
        redisMock.SetupGet(r => r.IsConnected).Returns(true);
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);
        dbMock.Setup(d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'XGROUP'"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions
            {
                PartitionCount = 4,
                UseRedisStreams = true,
                RequireDurableStreams = true
            },
            redisMock.Object);

        MatchProcessingChannelHealthCheck healthCheck = new(channel);
        HealthCheckResult preCheck = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        preCheck.Status.Should().Be(HealthStatus.Healthy);

        MatchProcessingJob job = new() { MatchID = 9001, AccountId = 42 };
        Func<Task> act = async () => await channel.EnqueueAsync(job);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valkey/valkey:8.1-alpine*");

        HealthCheckResult postCheck = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        postCheck.Status.Should().Be(HealthStatus.Unhealthy);
        postCheck.Description.Should().Contain("XGROUP");
    }

    [Fact]
    public async Task MatchProcessingChannel_WhenRequireDurableStreamsFalse_ReportsDegradedAndFallsBackToMemory()
    {
        // protects: Local/containerless dev resiliency while surfacing visible Degraded health status when Streams are absent.
        // fails_when: HealthCheck reports Healthy instead of Degraded after Streams fallback.
        // seam: Real MatchProcessingChannel + MatchProcessingChannelHealthCheck.

        Mock<IConnectionMultiplexer> redisMock = new();
        Mock<IDatabase> dbMock = new();
        redisMock.SetupGet(r => r.IsConnected).Returns(true);
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);
        dbMock.Setup(d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR unknown command 'XGROUP'"));

        MatchProcessingChannel channel = new(
            NullLogger<MatchProcessingChannel>.Instance,
            new MatchProcessingChannelOptions
            {
                PartitionCount = 2,
                UseRedisStreams = true,
                RequireDurableStreams = false
            },
            redisMock.Object);

        bool enqueued = await channel.EnqueueAsync(new MatchProcessingJob { MatchID = 9002, AccountId = 7 });
        enqueued.Should().BeTrue();
        channel.TotalQueueDepth.Should().Be(1);

        MatchProcessingChannelHealthCheck healthCheck = new(channel);
        HealthCheckResult status = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        status.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task DiscordPresenceRedisSubscriber_DoesNotAmplifyRedisWritesOnSubscriberReplicas()
    {
        // protects: Single-write Valkey/Redis presence fan-out (publishers persist presence:dto:{id} once; N subscriber pods only push SignalR).
        // fails_when: DiscordPresenceRedisSubscriber re-executes db.StringSetAsync on every subscribed MasterServer replica.
        // seam: Real DiscordPresenceRedisSubscriber.ProcessMessageAsync + IConnectionMultiplexer / IDatabase verification.

        Mock<IConnectionMultiplexer> redisMock = new();
        Mock<IDatabase> dbMock = new();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        Mock<IHubContext<PresenceHub>> hubContextMock = new();
        Mock<IHubClients> hubClientsMock = new();
        Mock<IClientProxy> groupProxyMock = new();
        hubClientsMock.Setup(c => c.Group("555")).Returns(groupProxyMock.Object);
        hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

        DiscordPresenceRedisSubscriber subscriber = new(
            redisMock.Object,
            hubContextMock.Object,
            NullLogger<DiscordPresenceRedisSubscriber>.Instance);

        string json = JsonSerializer.Serialize(
            new DiscordPresenceUpdateDto { AccountId = 555, State = "In Match", Details = "Caldavar" },
            MasterServerJsonContext.Default.DiscordPresenceUpdateDto);

        MethodInfo processMethod = typeof(DiscordPresenceRedisSubscriber)
            .GetMethod("ProcessMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)processMethod.Invoke(subscriber, [(RedisValue)json, CancellationToken.None])!;

        dbMock.Verify(
            d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task MatchProcessingChannel_LiveValkeyStreams_SurvivesContainerRestartWithZeroLostMatches()
    {
        // protects: Wave 1 Hard Exit Criterion — MatchProcessingChannel runs on Valkey 8.1 Streams with 0 fallback warnings and survives container restart with 0 lost matches.
        // fails_when: Valkey rejects XADD/XGROUP, falls back to volatile memory, loses messages across docker restart, or reports Degraded/Unhealthy.
        // seam: Real StackExchange.Redis ConnectionMultiplexer + MatchProcessingChannel + MatchProcessingChannelHealthCheck against live valkey/valkey:8.1-alpine container.
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string endpoint = Environment.GetEnvironmentVariable("KONGOR_VALKEY_CANARY_ENDPOINT") ?? "127.0.0.1:28398";
        string containerName = Environment.GetEnvironmentVariable("KONGOR_VALKEY_CANARY_CONTAINER") ?? "kongor-wave1-valkey-canary";
        string streamKey = $"kongor:canary:matches:{Guid.NewGuid():N}";
        string groupName = $"kongor_canary_workers_{Guid.NewGuid():N}";

        RecordingLogger<MatchProcessingChannel> logger = new();
        MatchProcessingChannelOptions options = new()
        {
            PartitionCount = 4,
            UseRedisStreams = true,
            RequireDurableStreams = true,
            StreamKey = streamKey,
            ConsumerGroup = groupName
        };

        const int expectedMatches = 10;
        HashSet<int> expectedIds = Enumerable.Range(80001, expectedMatches).ToHashSet();

        // Phase 1: Enqueue 10 matches into live Valkey Streams before restart
        using (ConnectionMultiplexer producerMux = await ConnectionMultiplexer.ConnectAsync($"{endpoint},abortConnect=false"))
        {
            MatchProcessingChannel producerChannel = new(logger, options, producerMux);
            MatchProcessingChannelHealthCheck preHealth = new(producerChannel);

            foreach (int matchId in expectedIds)
            {
                bool enqueued = await producerChannel.EnqueueAsync(new MatchProcessingJob
                {
                    MatchID = matchId,
                    AccountId = 1000 + matchId
                });
                enqueued.Should().BeTrue();
            }

            producerChannel.IsRedisStreamsSupported.Should().BeTrue();
            producerChannel.TotalQueueDepth.Should().Be(0, "all 10 jobs must persist directly into Valkey Streams without in-memory fallback");
            (await preHealth.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
            long preLen = await producerMux.GetDatabase().StreamLengthAsync(streamKey);
            preLen.Should().Be(expectedMatches);
        }

        // Phase 2: Restart Valkey container to prove AOF persistence across container reboot
        using (Process restartProc = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"restart {containerName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!)
        {
            await restartProc.WaitForExitAsync();
            restartProc.ExitCode.Should().Be(0, "docker restart on Valkey canary container must succeed");
        }

        // Phase 3: Reconnect brand-new MatchProcessingChannel after restart and drain all 10 matches
        using ConnectionMultiplexer consumerMux = await ConnectionMultiplexer.ConnectAsync($"{endpoint},abortConnect=false");
        MatchProcessingChannel consumerChannel = new(logger, options, consumerMux);
        MatchProcessingChannelHealthCheck postHealth = new(consumerChannel);

        HashSet<int> recoveredIds = [];
        using CancellationTokenSource readCts = new(TimeSpan.FromSeconds(15));
        await foreach (MatchSubmissionPayload payload in consumerChannel.ReadAllAsync(0, readCts.Token))
        {
            recoveredIds.Add(payload.MatchID);
            await consumerChannel.AcknowledgeAsync(payload, readCts.Token);
            if (recoveredIds.Count >= expectedMatches)
            {
                break;
            }
        }

        recoveredIds.Should().BeEquivalentTo(expectedIds, "all 10 matches must survive Valkey container restart with 0 lost matches");
        logger.WarningsAndErrors.Should().BeEmpty("MatchProcessingChannel must emit 0 fallback warnings against Valkey 8.1 Streams");
        (await postHealth.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        StreamPendingInfo pending = await consumerMux.GetDatabase().StreamPendingAsync(streamKey, groupName);
        pending.PendingMessageCount.Should().Be(0, "all recovered stream entries must be acknowledged via XACK");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> WarningsAndErrors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                WarningsAndErrors.Add($"[{logLevel}] {formatter(state, exception)} {exception?.Message}");
            }
        }
    }
}
