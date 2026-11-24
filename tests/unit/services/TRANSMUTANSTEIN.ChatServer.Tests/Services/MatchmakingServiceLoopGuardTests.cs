// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class MatchmakingServiceLoopGuardTests
{
    public MatchmakingServiceLoopGuardTests()
    {
        Log.Initialise(NullLogger.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldGracefullyShutDown_WhenCancelled()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:IntervalInMilliseconds"] = "10"
            })
            .Build();

        using CancellationTokenSource cts = new();
        MatchmakingService service = new(config, mockChatContext.Object);

        // Act
        Task executeTask = service.StartAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert
        executeTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task MatchmakingServiceLoopGuard_ExecuteAsync_ShouldSurviveTransientErrorsInsideLoopBody()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:IntervalInMilliseconds"] = "10"
            })
            .Build();

        using CancellationTokenSource cts = new();
        MatchmakingService service = new(config, mockChatContext.Object);

        // Add a group with null Information or invalid data to provoke a transient exception in tick logic
        MatchmakingGroup malformedGroup = new()
        {
            QueueStartTime = TimeProvider.System.GetUtcNow(),
            Information = null!, // Will throw NullReferenceException during bucket key expansion
            Members = []
        };
        service.Groups[1] = malformedGroup;

        // Act - Start service; the loop runs ticks and should catch the NullReferenceException internally without killing the service
        Task startTask = service.StartAsync(cts.Token);
        await Task.Delay(100); // Allow multiple ticks with errors to be handled

        // Now remove malformed group and let it continue running
        service.Groups.TryRemove(1, out _);
        await Task.Delay(50);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert
        startTask.IsCompletedSuccessfully.Should().BeTrue();
    }
}
