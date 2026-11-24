// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Services.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Events;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Events;

public sealed class MasterServerEventHandlerTests
{
    [Fact]
    public async Task HandleAsync_AccountBannedEvent_ShouldEvictSessionDataFromRedis()
    {
        // Arrange
        Mock<IDatabase> mockDatabase = new();
        mockDatabase
            .Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        MasterServerAccountBannedEventHandler handler = new(
            mockDatabase.Object,
            NullLogger<MasterServerAccountBannedEventHandler>.Instance);

        AccountBannedEvent bannedEvent = new(
            505,
            "Griefer01",
            TimeProvider.System.GetUtcNow().AddDays(30),
            "Intentional Feeding"
        );

        // Act
        Func<Task> act = async () => await handler.HandleAsync(bannedEvent);

        // Assert
        await act.Should().NotThrowAsync();
        mockDatabase.Verify(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.AtLeast(2));
    }
}
