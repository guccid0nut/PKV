// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class FloodPreventionServiceTests
{
    private static ChatSession CreateTestSession(int accountId, string accountName, AccountType type)
    {
        Mock<IServiceProvider> mockProvider = new();
        mockProvider.Setup(p => p.GetService(typeof(ILogger<ChatSession>)))
            .Returns(NullLogger<ChatSession>.Instance);
        mockProvider.Setup(p => p.GetService(typeof(IWebHostEnvironment)))
            .Returns(Mock.Of<IWebHostEnvironment>());
        mockProvider.Setup(p => p.GetService(typeof(IChatContext)))
            .Returns(Mock.Of<IChatContext>());
        mockProvider.Setup(p => p.GetService(typeof(IMatchmakingService)))
            .Returns(Mock.Of<IMatchmakingService>());
        mockProvider.Setup(p => p.GetService(typeof(IDiscordPresencePublisherService)))
            .Returns(Mock.Of<IDiscordPresencePublisherService>());

        TCPServer server = new(IPAddress.Loopback, 0);

        Role role = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{accountName}@test.com",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "hash",
            Role = role
        };

        Account account = new()
        {
            ID = accountId,
            Name = accountName,
            Type = type,
            IsMain = true,
            User = user
        };

        ChatSession session = new(server, mockProvider.Object) { Account = account };

        return session;
    }

    [Fact]
    public void CheckAndHandleFloodPrevention_StaffAccount_IsExempt()
    {
        // Arrange
        NullLogger<FloodPreventionService> logger = NullLogger<FloodPreventionService>.Instance;
        FloodPreventionService service = new(logger);

        ChatSession session = CreateTestSession(9001, "AdminUser", AccountType.Staff);

        // Act - Call multiple times exceeding FLOOD_THRESHOLD
        for (int i = 0; i < 50; i++)
        {
            bool allowed = service.CheckAndHandleFloodPrevention(session);
            Assert.True(allowed, $"Staff account should be exempt on call {i}");
        }
    }

    [Fact]
    public void CheckAndHandleFloodPrevention_StandardUser_AllowsUpToThresholdThenBlocks()
    {
        // Arrange
        NullLogger<FloodPreventionService> logger = NullLogger<FloodPreventionService>.Instance;
        FloodPreventionService service = new(logger);

        ChatSession session = CreateTestSession(9002, "RegularPlayer", AccountType.Normal);

        // Act - Call up to FLOOD_THRESHOLD times
        const int threshold = ChatProtocol.FLOOD_THRESHOLD;
        for (int i = 0; i <= threshold; i++)
        {
            bool allowed = service.CheckAndHandleFloodPrevention(session);
            Assert.True(allowed, $"Regular user request {i} should be allowed up to threshold");
        }

        // The next request exceeds threshold
        bool floodBlocked = service.CheckAndHandleFloodPrevention(session);
        Assert.False(floodBlocked, "Request exceeding FLOOD_THRESHOLD must be blocked");
    }

    [Fact]
    public async Task ServiceLifecycle_StartAndStopAsync_ExecutesCleanly()
    {
        // Arrange
        NullLogger<FloodPreventionService> logger = NullLogger<FloodPreventionService>.Instance;
        FloodPreventionService service = new(logger);

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        service.Dispose();
    }
}
