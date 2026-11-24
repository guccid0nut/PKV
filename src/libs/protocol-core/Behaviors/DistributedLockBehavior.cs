// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Distributed;
using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS;

namespace ASPIRE.Common.Behaviors;

/// <summary>
///     MediatR pipeline behavior that automatically acquires and manages a distributed Redis lock
///     for any command or query implementing <see cref="ILockableRequest"/>.
/// </summary>
public sealed class DistributedLockBehavior<TRequest, TResponse>(
    ILogger<DistributedLockBehavior<TRequest, TResponse>> logger,
    IDistributedLockService? lockService = null)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ILockableRequest lockable || lockService is null)
        {
            return await next(cancellationToken);
        }

        string lockKey = lockable.LockKey;
        string requestName = typeof(TRequest).Name;

        logger.LogDebug(
            "[MediatR:Lock] Acquiring distributed lock for {RequestName} on resource '{LockKey}'...",
            requestName,
            lockKey);

        await using IDistributedLock lockHandle = await lockService.AcquireLockAsync(
            lockKey,
            lockable.LockExpiry,
            lockable.LockTimeout,
            cancellationToken);

        if (!lockHandle.IsAcquired)
        {
            logger.LogWarning(
                "[MediatR:Lock] ⚠️ Failed to acquire distributed lock for {RequestName} on resource '{LockKey}' within timeout {TimeoutMs}ms",
                requestName,
                lockKey,
                lockable.LockTimeout.TotalMilliseconds);

            throw new TimeoutException($"Could not acquire distributed lock for resource '{lockKey}' within {lockable.LockTimeout.TotalMilliseconds}ms.");
        }

        logger.LogDebug(
            "[MediatR:Lock] Acquired distributed lock for {RequestName} on resource '{LockKey}'. Executing handler...",
            requestName,
            lockKey);

        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            logger.LogDebug(
                "[MediatR:Lock] Released distributed lock for {RequestName} on resource '{LockKey}'.",
                requestName,
                lockKey);
        }
    }
}
