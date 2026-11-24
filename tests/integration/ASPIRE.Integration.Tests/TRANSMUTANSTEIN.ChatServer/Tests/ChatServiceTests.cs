// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional
// ReSharper disable EvaluateParameterInTemplate

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[SuppressMessage("Performance",
    "CA1873:Evaluation of this argument may be expensive and unnecessary if logging is disabled")]
[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatServiceTests : IDisposable
{
    public void Dispose()
    {
        Log.Initialise(NullLogger.Instance);
    }
    [Fact]
    [SuppressMessage("ReSharper", "LogMessageIsConditional")]
    public Task StopAsync_WhenChatServerIsNull_LogsErrorAndThrows()
    {
        try
        {
            // Arrange
            Mock<IServiceProvider> serviceProviderMock = new();
            Mock<ILogger<ChatService>> loggerMock = new();

            serviceProviderMock
                .Setup(sp => sp.GetService(typeof(ILogger<ChatService>)))
                .Returns(loggerMock.Object);

            // Initialize Static Log for Dispose() call which uses Log.Error
            Log.Initialise(loggerMock.Object);

            // Create ChatService. Note: StartAsync is NOT called, so ChatServer property remains null.
            using ChatService chatService = new(serviceProviderMock.Object);

            // Act
            Task task = chatService.StopAsync(CancellationToken.None);

            // Assert
            Assert.True(task.IsFaulted);
            Assert.IsType<ApplicationException>(task.Exception?.InnerException);
            Assert.Equal("Chat Server Is NULL", task.Exception?.InnerException?.Message);

            // Verify logging
            // ReSharper disable LogMessageIsConditional
            loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.Is<EventId>(e => e.Name == "LogChatServerNullDuringStop"),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
            // ReSharper restore LogMessageIsConditional
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
}
