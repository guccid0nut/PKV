// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using KONGOR.MasterServer.Infrastructure;
using Microsoft.AspNetCore.Http;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Infrastructure;

[SuppressMessage("ReSharper", "LogMessageIsConditional")]
public sealed class ExceptionLoggingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenNoException_CallsNext()
    {
        // Arrange
        Mock<ILogger<ExceptionLoggingMiddleware>> loggerMock = new();
        bool nextCalled = false;

        ExceptionLoggingMiddleware middleware = new(Next, loggerMock.Object);
        DefaultHttpContext context = new();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        return;

        Task Next(HttpContext _)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenTaskCanceledAndRequestAborted_SuppressesException()
    {
        // Arrange
        Mock<ILogger<ExceptionLoggingMiddleware>> loggerMock = new();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        CancellationTokenSource cts = new();
        await cts.CancelAsync();

        ExceptionLoggingMiddleware middleware = new(Next, loggerMock.Object);
        DefaultHttpContext context = new() { RequestAborted = cts.Token };

        // Act & Assert
        await middleware.InvokeAsync(context);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
        return;

        Task Next(HttpContext _)
        {
            throw new TaskCanceledException("A task was canceled.");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenUnhandledException_LogsErrorAndRethrows()
    {
        // Arrange
        Mock<ILogger<ExceptionLoggingMiddleware>> loggerMock = new();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Error)).Returns(true);
        InvalidOperationException expectedException = new("Database connection failed");

        ExceptionLoggingMiddleware middleware = new(Next, loggerMock.Object);
        DefaultHttpContext context = new();

        // Act & Assert
        InvalidOperationException thrown =
            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Equal("Database connection failed", thrown.Message);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Database connection failed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        return;

        Task Next(HttpContext _)
        {
            throw expectedException;
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenOperationCanceledWithoutRequestAborted_SuppressesException()
    {
        // Arrange
        Mock<ILogger<ExceptionLoggingMiddleware>> loggerMock = new();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);

        ExceptionLoggingMiddleware middleware = new(Next, loggerMock.Object);
        DefaultHttpContext context = new();

        // Act & Assert
        await middleware.InvokeAsync(context);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
        return;

        Task Next(HttpContext _)
        {
            throw new OperationCanceledException("Operation timed out or was canceled internally.");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenWrappedOperationCanceledException_SuppressesException()
    {
        // Arrange
        Mock<ILogger<ExceptionLoggingMiddleware>> loggerMock = new();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);

        ExceptionLoggingMiddleware middleware = new(Next, loggerMock.Object);
        DefaultHttpContext context = new();

        // Act & Assert
        await middleware.InvokeAsync(context);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
        return;

        Task Next(HttpContext _)
        {
            throw new InvalidOperationException("Wrapper exception", new OperationCanceledException("Inner cancel"));
        }
    }
}
