// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Telemetry.V1;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Services.Grpc;
using Xunit;

namespace Grpc.Challenger.Tests;

public sealed class CancellingAsyncStreamReader<T>(
    IList<T> items,
    int cancelAfterCount = -1,
    CancellationTokenSource? ctsToCancel = null) : IAsyncStreamReader<T>
{
    private readonly int _cancelAfterCount = cancelAfterCount;
    private readonly CancellationTokenSource? _ctsToCancel = ctsToCancel;
    private readonly IList<T> _items = items;
    private int _index = -1;

    public T Current => _index >= 0 && _index < _items.Count ? _items[_index] : default!;

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _index++;
        if (_cancelAfterCount >= 0 && _index >= _cancelAfterCount)
        {
            _ctsToCancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_index < _items.Count) return Task.FromResult(true);

        return Task.FromResult(false);
    }
}

public sealed class TelemetryStreamServiceChallengerTests
{
    [Fact]
    public async Task StreamCombatEvents_WithEmptyStream_ReturnsZeroFrames()
    {
        GrpcTelemetryStreamService service = new(NullLogger<GrpcTelemetryStreamService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        CancellingAsyncStreamReader<CombatTelemetryFrame> emptyReader =
            new([]);

        StreamSummaryResponse response = await service.StreamCombatEvents(emptyReader, ctx);

        response.Should().NotBeNull();
        response.TotalFramesReceived.Should().Be(0);
        response.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task StreamCombatEvents_WithLargeVolumeAndExtremePayloads_ProcessesAllFrames()
    {
        GrpcTelemetryStreamService service = new(NullLogger<GrpcTelemetryStreamService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        List<CombatTelemetryFrame> frames = [];

        for (int i = 0; i < 500; i++)
        {
            CombatTelemetryFrame frame = new()
            {
                MatchId = 100000 + (ulong)i,
                FrameIndex = (uint)i,
                TimestampUtcMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                GameTimeSeconds = (uint)(i / 10)
            };

            // Add extreme player data
            for (uint p = 1; p <= 10; p++)
                frame.Players.Add(new PlayerLiveState
                {
                    AccountId = p,
                    SlotIndex = (int)p - 1,
                    HeroId = p * 10,
                    Position = new Vector3D { X = float.MaxValue, Y = float.MinValue, Z = float.NaN },
                    Health = int.MaxValue,
                    MaxHealth = int.MaxValue,
                    Mana = -100,
                    MaxMana = 5000,
                    Gold = int.MinValue,
                    Level = 25
                });

            // Add events
            frame.Events.Add(new CombatEvent
            {
                EventType = CombatEventType.EventTypeHeroKill,
                SourceAccountId = 1,
                TargetAccountId = 2,
                Location = new Vector3D { X = 123.45f, Y = 678.90f, Z = 0f },
                Narrative = "First Blood"
            });

            frames.Add(frame);
        }

        CancellingAsyncStreamReader<CombatTelemetryFrame> reader =
            new(frames);

        StreamSummaryResponse response = await service.StreamCombatEvents(reader, ctx);

        response.Should().NotBeNull();
        response.TotalFramesReceived.Should().Be(500);
        response.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task StreamCombatEvents_WhenClientCancelsMidStream_PropagatesCancellationGracefully()
    {
        GrpcTelemetryStreamService service = new(NullLogger<GrpcTelemetryStreamService>.Instance);

        CancellationTokenSource cts = new();
        ServerCallContext ctx = new AdversarialServerCallContext(cts.Token);

        List<CombatTelemetryFrame> frames = [];
        for (int i = 0; i < 50; i++) frames.Add(new CombatTelemetryFrame { MatchId = 999, FrameIndex = (uint)i });

        // Cancel after 10 frames
        CancellingAsyncStreamReader<CombatTelemetryFrame> reader =
            new(frames, 10, cts);

        // Awaiting should throw OperationCanceledException
        Func<Task> act = async () => await service.StreamCombatEvents(reader, ctx);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StreamCombatEvents_WithAlreadyCancelledToken_CancelsImmediately()
    {
        GrpcTelemetryStreamService service = new(NullLogger<GrpcTelemetryStreamService>.Instance);

        CancellationTokenSource cts = new();
        cts.Cancel();
        ServerCallContext ctx = new AdversarialServerCallContext(cts.Token);

        List<CombatTelemetryFrame> frames =
        [
            new() { MatchId = 1, FrameIndex = 0 }
        ];

        CancellingAsyncStreamReader<CombatTelemetryFrame> reader =
            new(frames);

        Func<Task> act = async () => await service.StreamCombatEvents(reader, ctx);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IngestMatchSummary_WithVariousBoundaryValues_CallsPublisherAndReturnsStorageUri()
    {
        Mock<ITelemetryPublisherService> mockPub = new();
        mockPub.Setup(p => p.OnMatchEndedAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        GrpcTelemetryStreamService service = new(
            NullLogger<GrpcTelemetryStreamService>.Instance,
            mockPub.Object
        );
        ServerCallContext ctx = new AdversarialServerCallContext();

        MatchSummaryPayload payload = new()
        {
            MatchId = 777777,
            DurationSeconds = uint.MaxValue,
            WinningTeam = 2,
            ReplayFileSha256 = "deadbeefcafebabe0123456789abcdef",
            ReplayFileSizeBytes = long.MaxValue
        };

        IngestSummaryResponse response = await service.IngestMatchSummary(payload, ctx);

        response.Should().NotBeNull();
        response.Success.Should().BeTrue();
        response.StorageUri.Should().Be("/replays/M777777.honreplay");

        mockPub.Verify(p => p.OnMatchEndedAsync(777777), Times.Once);
    }

    [Fact]
    public async Task IngestMatchSummary_WithNullPublisher_SucceedsWithoutException()
    {
        GrpcTelemetryStreamService service = new(
            NullLogger<GrpcTelemetryStreamService>.Instance
        );
        ServerCallContext ctx = new AdversarialServerCallContext();

        MatchSummaryPayload payload = new()
        {
            MatchId = 12345,
            DurationSeconds = 1200,
            WinningTeam = 1,
            ReplayFileSha256 = "abc",
            ReplayFileSizeBytes = 1000
        };

        IngestSummaryResponse response = await service.IngestMatchSummary(payload, ctx);

        response.Should().NotBeNull();
        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TelemetryStreamService_HighConcurrencyStreaming_MaintainsIsolation()
    {
        GrpcTelemetryStreamService service = new(NullLogger<GrpcTelemetryStreamService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        List<Task> tasks = [];

        for (int c = 0; c < 30; c++)
        {
            ulong matchId = (ulong)(c + 1000);
            tasks.Add(Task.Run(async () =>
            {
                List<CombatTelemetryFrame> frames = [];
                for (int f = 0; f < 50; f++)
                    frames.Add(new CombatTelemetryFrame
                    {
                        MatchId = matchId,
                        FrameIndex = (uint)f,
                        GameTimeSeconds = (uint)f,
                        TimestampUtcMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
                    });

                CancellingAsyncStreamReader<CombatTelemetryFrame> reader =
                    new(frames);

                StreamSummaryResponse streamRes = await service.StreamCombatEvents(reader, ctx);

                streamRes.TotalFramesReceived.Should().Be(50);
                streamRes.IsHealthy.Should().BeTrue();
            }));
        }

        await Task.WhenAll(tasks);
    }
}
