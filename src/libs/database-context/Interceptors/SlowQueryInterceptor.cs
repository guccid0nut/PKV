// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data.Common;
using ASPIRE.Common.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace MERRICK.DatabaseContext.Interceptors;

public class SlowQueryInterceptor(long thresholdMilliseconds = 50, ILogger<SlowQueryInterceptor>? logger = null)
    : DbCommandInterceptor
{
    private readonly ILogger<SlowQueryInterceptor> _logger = logger ?? NullLogger<SlowQueryInterceptor>.Instance;

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        TagDbActivity();
        LogIfSlow(command, eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        TagDbActivity();
        LogIfSlow(command, eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        TagDbActivity();
        LogIfSlow(command, eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        TagDbActivity();
        LogIfSlow(command, eventData);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        TagDbActivity();
        LogIfSlow(command, eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        TagDbActivity();
        LogIfSlow(command, eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    private static void TagDbActivity()
    {
        if (Activity.Current is { } activity)
        {
            activity.SetTag("db.system", "mssql");
            activity.SetTag("db.name", "MERRICK");
        }
    }

    private void LogIfSlow(DbCommand command, CommandExecutedEventData eventData)
    {
        long durationMs = (long)eventData.Duration.TotalMilliseconds;
        if (durationMs > thresholdMilliseconds)
        {
            NexusMetrics.SlowQueriesTotal.Add(1);
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "[SLOW SQL QUERY] Execution took {DurationMs}ms (Threshold: {ThresholdMs}ms):\n{CommandText}",
                    durationMs,
                    thresholdMilliseconds,
                    command.CommandText);
        }
    }
}
