// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional
// ReSharper disable EvaluateParameterInTemplate

using System.Diagnostics.CodeAnalysis;
using System.Text;
using KONGOR.MasterServer.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ASPIRE.Tests.Performance;

[SuppressMessage("ReSharper", "LogMessageIsConditional")]
[SuppressMessage("Performance",
    "CA1873:Evaluation of this argument may be expensive and unnecessary if logging is disabled")]
public class RequestLoggingMiddlewareTests
{
    [Fact]
    [SuppressMessage("ReSharper", "LogMessageIsConditional")]
    public async Task RequestLogging_LargeContentPayload_LogsTruncatedBody()
    {
        // Arrange
        Mock<ILogger<RequestLoggingMiddleware>> loggerMock = new();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Information)).Returns(true);
        bool nextCalled = false;

        RequestLoggingMiddleware middleware = new(Next, loggerMock.Object);

        DefaultHttpContext context = new();
        // Create a body with 4096 'a' chars followed by a unique secret
        const string uniqueSecret = "THE_HIDDEN_SECRET";
        string padding = new('a', 4096);
        string bodyContent = padding + uniqueSecret;

        MemoryStream memoryStream = new(Encoding.UTF8.GetBytes(bodyContent));
        context.Request.Body = memoryStream;
        context.Request.ContentLength = memoryStream.Length;
        context.Request.ContentType = "application/json";
        context.Request.Method = "POST";
        context.Request.Path = "/test";

        // Act
        await middleware.Invoke(context);

        // Assert
        // Verify that LogInformation was called with the truncated body
        // ReSharper disable LogMessageIsConditional
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => IsValidLargePayload(v, padding, uniqueSecret)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        // ReSharper restore LogMessageIsConditional

        // Verify that Next was called
        Assert.True(nextCalled);

        // Verify that the stream position is reset to 0
        Assert.Equal(0, context.Request.Body.Position);

        return;

        Task Next(HttpContext httpContext)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    [SuppressMessage("ReSharper", "LogMessageIsConditional")]
    public async Task RequestLogging_HugeContentLengthHeader_LogsTruncatedBody()
    {
        // Arrange
        Mock<ILogger<RequestLoggingMiddleware>> loggerMock = new();
        loggerMock.Setup(l => l.IsEnabled(LogLevel.Information)).Returns(true);
        RequestLoggingMiddleware middleware = new(Next, loggerMock.Object);

        DefaultHttpContext context = new();
        const string bodyContent = "Small actual content";
        MemoryStream memoryStream = new(Encoding.UTF8.GetBytes(bodyContent));
        context.Request.Body = memoryStream;
        // Simulate huge content length (greater than int.MaxValue) to test overflow handling
        context.Request.ContentLength = (long)int.MaxValue + 100;
        context.Request.ContentType = "application/json";
        context.Request.Method = "POST";
        context.Request.Path = "/huge";

        // Act
        await middleware.Invoke(context);

        // Assert
        // Should not throw overflow exception and log properly
        // ReSharper disable LogMessageIsConditional
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => IsValidHugePayload(v, bodyContent)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        // ReSharper restore LogMessageIsConditional

        Assert.Equal(0, context.Request.Body.Position);

        return;

        static Task Next(HttpContext httpContext)
        {
            return Task.CompletedTask;
        }
    }

    private static bool IsValidLargePayload(object? v, string padding, string uniqueSecret)
    {
        string logMessage = v?.ToString() ?? string.Empty;
        return logMessage.Contains(padding) &&
               logMessage.Contains("[TRUNCATED]") &&
               !logMessage.Contains(uniqueSecret);
    }

    private static bool IsValidHugePayload(object? v, string bodyContent)
    {
        string logMessage = v?.ToString() ?? string.Empty;
        return logMessage.Contains(bodyContent) &&
               logMessage.Contains("[TRUNCATED]");
    }
}
