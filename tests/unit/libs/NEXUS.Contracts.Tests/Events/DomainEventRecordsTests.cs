// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using NEXUS.Contracts.Events;
using Xunit;

namespace NEXUS.Contracts.Tests.Events;

public sealed class DomainEventRecordsTests
{
    [Fact]
    public void MatchCompletedEvent_ConvenienceConstructor_ShouldInitializeValidDefaults()
    {
        // Arrange & Act
        List<int> accountIds = [101, 102, 103, 104, 105];
        MatchCompletedEvent @event = new(
            42001,
            accountIds,
            1,
            TimeSpan.FromMinutes(35),
            "ranked",
            5,
            "127.0.0.1:11235"
        );

        // Assert
        @event.EventId.Should().NotBeEmpty();
        @event.OccurredAt.Should().BeCloseTo(TimeProvider.System.GetUtcNow(), TimeSpan.FromSeconds(5));
        @event.MatchId.Should().Be(42001);
        @event.AccountIds.Should().BeEquivalentTo(accountIds);
        @event.WinnerTeam.Should().Be(1);
        @event.Duration.Should().Be(TimeSpan.FromMinutes(35));
        @event.GameMode.Should().Be("ranked");
        @event.ServerId.Should().Be(5);
        @event.ServerAddress.Should().Be("127.0.0.1:11235");
        (@event is IDomainEvent).Should().BeTrue();
    }

    [Fact]
    public void MatchCompletedEvent_PrimaryConstructor_ShouldPreserveExactMetadata()
    {
        // Arrange
        Guid customEventId = Guid.NewGuid();
        DateTimeOffset customTimestamp = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        List<int> accountIds = [1, 2, 3];

        // Act
        MatchCompletedEvent @event = new(
            customEventId,
            customTimestamp,
            999,
            accountIds,
            2,
            TimeSpan.FromMinutes(20),
            "midwars",
            10,
            "10.0.0.1"
        );

        // Assert
        @event.EventId.Should().Be(customEventId);
        @event.OccurredAt.Should().Be(customTimestamp);
        @event.MatchId.Should().Be(999);
        @event.WinnerTeam.Should().Be(2);
        @event.GameMode.Should().Be("midwars");
    }

    [Fact]
    public void AccountBannedEvent_ConvenienceConstructor_ShouldInitializeValidDefaults()
    {
        // Arrange & Act
        DateTimeOffset expiration = TimeProvider.System.GetUtcNow().AddDays(14);
        AccountBannedEvent @event = new(
            777,
            "GriefingPlayer",
            expiration,
            "Toxic behavior in public matchmaking",
            1
        );

        // Assert
        @event.EventId.Should().NotBeEmpty();
        @event.OccurredAt.Should().BeCloseTo(TimeProvider.System.GetUtcNow(), TimeSpan.FromSeconds(5));
        @event.AccountId.Should().Be(777);
        @event.AccountName.Should().Be("GriefingPlayer");
        @event.BanExpiration.Should().Be(expiration);
        @event.BanReason.Should().Be("Toxic behavior in public matchmaking");
        @event.BannedByAccountId.Should().Be(1);
        (@event is IDomainEvent).Should().BeTrue();
    }

    [Fact]
    public void HeroAvatarPurchasedEvent_ConvenienceConstructor_ShouldInitializeValidDefaults()
    {
        // Arrange & Act
        HeroAvatarPurchasedEvent @event = new(
            888,
            444,
            1234,
            "Hero_Pyromancer.Alt2",
            0,
            350
        );

        // Assert
        @event.EventId.Should().NotBeEmpty();
        @event.OccurredAt.Should().BeCloseTo(TimeProvider.System.GetUtcNow(), TimeSpan.FromSeconds(5));
        @event.AccountId.Should().Be(888);
        @event.UserId.Should().Be(444);
        @event.ProductId.Should().Be(1234);
        @event.ItemCode.Should().Be("Hero_Pyromancer.Alt2");
        @event.CurrencyType.Should().Be(0);
        @event.CostAmount.Should().Be(350);
        @event.Timestamp.Should().BeCloseTo(TimeProvider.System.GetUtcNow(), TimeSpan.FromSeconds(5));
        (@event is IDomainEvent).Should().BeTrue();
    }

    [Fact]
    public void DomainEvents_RecordValueEquality_ShouldWorkAsExpected()
    {
        // Arrange
        Guid eventId = Guid.NewGuid();
        DateTimeOffset timestamp = TimeProvider.System.GetUtcNow();

        AccountBannedEvent event1 = new(eventId, timestamp, 100, "UserA", null, "ReasonA", 1);
        AccountBannedEvent event2 = new(eventId, timestamp, 100, "UserA", null, "ReasonA", 1);
        AccountBannedEvent event3 = new(Guid.NewGuid(), timestamp, 100, "UserA", null, "ReasonA", 1);

        // Assert
        event1.Should().Be(event2);
        event1.Should().NotBe(event3);
    }
}
