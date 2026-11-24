// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Security;
using MediatR;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Behaviors;

/// <summary>
///     MediatR pipeline behavior providing extensive structured logging, execution timing,
///     and correlation tracking across every query and command in the cluster.
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        string requestName = typeof(TRequest).Name;
        string correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..8];

        logger.LogInformation(
            "[MediatR:{CorrelationId}] >>> START {RequestName} | Payload: {@Request}",
            correlationId,
            requestName,
            request);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            TResponse response = await next(cancellationToken);
            sw.Stop();

            logger.LogInformation(
                "[MediatR:{CorrelationId}] <<< SUCCESS {RequestName} in {ElapsedMs}ms",
                correlationId,
                requestName,
                sw.ElapsedMilliseconds);

            return response;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();

            logger.LogInformation(
                "[MediatR:{CorrelationId}] ⚡ CANCELED {RequestName} after {ElapsedMs}ms",
                correlationId,
                requestName,
                sw.ElapsedMilliseconds);

            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();

            logger.LogError(
                ex,
                "[MediatR:{CorrelationId}] ❌ FAILED {RequestName} after {ElapsedMs}ms | Error: {ErrorMessage}",
                correlationId,
                requestName,
                sw.ElapsedMilliseconds,
                ex.Message);

            throw;
        }
    }
}
