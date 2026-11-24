// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Behaviors;

/// <summary>
///     MediatR pipeline behavior that monitors request execution time and issues warnings
///     for queries or commands that exceed the performance threshold (500 ms).
/// </summary>
public sealed class PerformanceBehavior<TRequest, TResponse>(
    ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private const long ThresholdMs = 500;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        TResponse response = await next(cancellationToken);
        sw.Stop();

        long elapsedMs = sw.ElapsedMilliseconds;
        if (elapsedMs > ThresholdMs)
        {
            string requestName = typeof(TRequest).Name;
            logger.LogWarning(
                "[MediatR:Performance] ⚠️ SLOW REQUEST {RequestName} took {ElapsedMs}ms (threshold: {ThresholdMs}ms) | Request: {@Request}",
                requestName,
                elapsedMs,
                ThresholdMs,
                request);
        }

        return response;
    }
}
