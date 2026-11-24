// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Matchmaking;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Domain.Matches;
using Xunit;

namespace Kongor.Core.Tests.Matchmaking;

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}

public sealed class RedisMatchmakingStateTests
{
    [Fact]
    public async Task TryAcquireSlotLeaseAsync_UncontestedSlot_AcquiresSuccessfully()
    {
        TestTimeProvider timeProvider = new();
        RedisServerSlotLeaseService leaseService = new(
            NullLogger<RedisServerSlotLeaseService>.Instance,
            redis: null,
            timeProvider: timeProvider);

        SlotLeaseResult result = await leaseService.TryAcquireSlotLeaseAsync(
            region: "USE",
            serverAddress: "10.0.0.1",
            serverPort: 11235,
            workerId: "Worker_Alpha",
            leaseDuration: TimeSpan.FromSeconds(10));

        result.Succeeded.Should().BeTrue();
        result.LockKey.Should().Be("lock:server_slot:use:10.0.0.1:11235");
        result.LeaseToken.Should().Be("Worker_Alpha");
    }

    [Fact]
    public async Task TryAcquireSlotLeaseAsync_ContestedSlot_RejectsSecondWorker()
    {
        TestTimeProvider timeProvider = new();
        RedisServerSlotLeaseService leaseService = new(
            NullLogger<RedisServerSlotLeaseService>.Instance,
            redis: null,
            timeProvider: timeProvider);

        // Worker Alpha acquires
        SlotLeaseResult alphaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "USE",
            serverAddress: "10.0.0.1",
            serverPort: 11235,
            workerId: "Worker_Alpha",
            leaseDuration: TimeSpan.FromSeconds(10));

        alphaResult.Succeeded.Should().BeTrue();

        // Worker Beta attempts to acquire same slot
        SlotLeaseResult betaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "USE",
            serverAddress: "10.0.0.1",
            serverPort: 11235,
            workerId: "Worker_Beta",
            leaseDuration: TimeSpan.FromSeconds(10));

        betaResult.Succeeded.Should().BeFalse();
        betaResult.ContestedBy.Should().Be("Worker_Alpha");
    }

    [Fact]
    public async Task ReleaseSlotLeaseAsync_AllowsSubsequentWorkerToAcquire()
    {
        TestTimeProvider timeProvider = new();
        RedisServerSlotLeaseService leaseService = new(
            NullLogger<RedisServerSlotLeaseService>.Instance,
            redis: null,
            timeProvider: timeProvider);

        SlotLeaseResult alphaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "EUW",
            serverAddress: "10.0.0.2",
            serverPort: 11236,
            workerId: "Worker_Alpha",
            leaseDuration: TimeSpan.FromSeconds(10));

        alphaResult.Succeeded.Should().BeTrue();

        // Release
        bool released = await leaseService.ReleaseSlotLeaseAsync(alphaResult.LockKey, alphaResult.LeaseToken);
        released.Should().BeTrue();

        // Worker Beta can now acquire
        SlotLeaseResult betaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "EUW",
            serverAddress: "10.0.0.2",
            serverPort: 11236,
            workerId: "Worker_Beta",
            leaseDuration: TimeSpan.FromSeconds(10));

        betaResult.Succeeded.Should().BeTrue();
        betaResult.LeaseToken.Should().Be("Worker_Beta");
    }

    [Fact]
    public async Task TryAcquireSlotLeaseAsync_ExpiredLease_AllowsTakeover()
    {
        TestTimeProvider timeProvider = new();
        RedisServerSlotLeaseService leaseService = new(
            NullLogger<RedisServerSlotLeaseService>.Instance,
            redis: null,
            timeProvider: timeProvider);

        SlotLeaseResult alphaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "SEA",
            serverAddress: "10.0.0.3",
            serverPort: 11237,
            workerId: "Worker_Alpha",
            leaseDuration: TimeSpan.FromSeconds(5));

        alphaResult.Succeeded.Should().BeTrue();

        // Advance time past expiry
        timeProvider.Advance(TimeSpan.FromSeconds(6));

        // Worker Beta should successfully take over expired slot
        SlotLeaseResult betaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "SEA",
            serverAddress: "10.0.0.3",
            serverPort: 11237,
            workerId: "Worker_Beta",
            leaseDuration: TimeSpan.FromSeconds(5));

        betaResult.Succeeded.Should().BeTrue();
        betaResult.LeaseToken.Should().Be("Worker_Beta");
    }

    [Fact]
    public async Task RenewSlotLeaseAsync_ExtendsLeaseDuration()
    {
        TestTimeProvider timeProvider = new();
        RedisServerSlotLeaseService leaseService = new(
            NullLogger<RedisServerSlotLeaseService>.Instance,
            redis: null,
            timeProvider: timeProvider);

        SlotLeaseResult alphaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "USE",
            serverAddress: "10.0.0.4",
            serverPort: 11238,
            workerId: "Worker_Alpha",
            leaseDuration: TimeSpan.FromSeconds(5));

        alphaResult.Succeeded.Should().BeTrue();

        // Advance 3 seconds
        timeProvider.Advance(TimeSpan.FromSeconds(3));

        // Renew for another 10 seconds
        bool renewed = await leaseService.RenewSlotLeaseAsync(alphaResult.LockKey, alphaResult.LeaseToken, TimeSpan.FromSeconds(10));
        renewed.Should().BeTrue();

        // Advance 4 seconds (would have expired under original 5s TTL)
        timeProvider.Advance(TimeSpan.FromSeconds(4));

        // Beta still rejected because lease was extended
        SlotLeaseResult betaResult = await leaseService.TryAcquireSlotLeaseAsync(
            region: "USE",
            serverAddress: "10.0.0.4",
            serverPort: 11238,
            workerId: "Worker_Beta",
            leaseDuration: TimeSpan.FromSeconds(5));

        betaResult.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task RedisMatchmakingStreamService_PublishAndReadTickets_QueueOrderPreserved()
    {
        RedisMatchmakingStreamService streamService = new(
            NullLogger<RedisMatchmakingStreamService>.Instance,
            redis: null);

        MatchmakingTicketDto ticket1 = new()
        {
            TicketId = "ticket_1",
            GroupGuid = 1001,
            Region = "USE",
            GameType = "Ranked5v5",
            MapName = "caldavar",
            PlayerCount = 2,
            AverageRating = 1650.0,
            QueuedAt = DateTimeOffset.UtcNow,
            MemberAccountIds = [201, 202]
        };

        MatchmakingTicketDto ticket2 = new()
        {
            TicketId = "ticket_2",
            GroupGuid = 1002,
            Region = "USE",
            GameType = "Ranked5v5",
            MapName = "caldavar",
            PlayerCount = 3,
            AverageRating = 1620.0,
            QueuedAt = DateTimeOffset.UtcNow,
            MemberAccountIds = [301, 302, 303]
        };

        string? id1 = await streamService.PublishTicketAsync(ticket1);
        string? id2 = await streamService.PublishTicketAsync(ticket2);

        id1.Should().NotBeNullOrEmpty();
        id2.Should().NotBeNullOrEmpty();

        var tickets = await streamService.ReadTicketsAsync(
            region: "USE",
            gameType: "Ranked5v5",
            mapName: "caldavar",
            consumerGroup: "mm_brokers",
            consumerName: "broker_1",
            count: 5);

        tickets.Should().HaveCount(2);
        tickets[0].Ticket.TicketId.Should().Be("ticket_1");
        tickets[0].Ticket.GroupGuid.Should().Be(1001);
        tickets[1].Ticket.TicketId.Should().Be("ticket_2");
        tickets[1].Ticket.GroupGuid.Should().Be(1002);

        bool acked = await streamService.AcknowledgeTicketAsync("matchmaking:stream:use:ranked5v5:caldavar", "mm_brokers", tickets[0].MessageId);
        acked.Should().BeTrue();
    }
}
