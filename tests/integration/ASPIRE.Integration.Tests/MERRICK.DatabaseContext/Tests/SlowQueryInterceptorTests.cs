// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data.Common;
using System.Reflection;
using MERRICK.DatabaseContext.Interceptors;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class SlowQueryInterceptorTests
{
    [Fact]
    public void SlowQueryInterceptor_WhenExecuted_InstantiatesWithDefaultThreshold()
    {
        SlowQueryInterceptor interceptor = new();
        Assert.NotNull(interceptor);
    }

    [Fact]
    public void SlowQueryInterceptor_CustomThreshold_ExecutesWithoutError()
    {
        SlowQueryInterceptor interceptor = new(10);
        Assert.NotNull(interceptor);
    }

    [Fact]
    public void SlowQueryInterceptor_WhenQueryExceedsThreshold_LogsWarning()
    {
        Mock<ILogger<SlowQueryInterceptor>> mockLogger = new();
        mockLogger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);

        SlowQueryInterceptor interceptor = new(50, mockLogger.Object);

        Mock<DbCommand> mockCommand = new();
        mockCommand.SetupGet(c => c.CommandText).Returns("SELECT * FROM Accounts");

        CommandExecutedEventData eventData = CreateEventData(mockCommand.Object, TimeSpan.FromMilliseconds(100));

        int result = interceptor.NonQueryExecuted(mockCommand.Object, eventData, 1);
        Assert.Equal(1, result);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("[SLOW SQL QUERY]") && v.ToString()!.Contains("100ms")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void SlowQueryInterceptor_WhenQueryBelowThreshold_DoesNotLogWarning()
    {
        Mock<ILogger<SlowQueryInterceptor>> mockLogger = new();
        mockLogger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);

        SlowQueryInterceptor interceptor = new(100, mockLogger.Object);

        Mock<DbCommand> mockCommand = new();
        mockCommand.SetupGet(c => c.CommandText).Returns("SELECT * FROM Accounts");

        CommandExecutedEventData eventData = CreateEventData(mockCommand.Object, TimeSpan.FromMilliseconds(10));

        int result = interceptor.NonQueryExecuted(mockCommand.Object, eventData, 1);
        Assert.Equal(1, result);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    public static CommandExecutedEventData CreateEventData(DbCommand command, TimeSpan duration)
    {
        Type type = typeof(CommandExecutedEventData);
        ConstructorInfo constructor = type.GetConstructors()[0];
        ParameterInfo[] parameters = constructor.GetParameters();
        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo p = parameters[i];
            if (p.ParameterType == typeof(DbCommand)) args[i] = command;
            else if (p.ParameterType == typeof(TimeSpan)) args[i] = duration;
            else if (p.ParameterType == typeof(DateTimeOffset)) args[i] = TimeProvider.System.GetUtcNow();
            else if (p.ParameterType == typeof(Guid)) args[i] = Guid.NewGuid();
            else if (p.ParameterType.IsValueType) args[i] = Activator.CreateInstance(p.ParameterType);
            else args[i] = null;
        }

        return (CommandExecutedEventData)constructor.Invoke(args);
    }
}
