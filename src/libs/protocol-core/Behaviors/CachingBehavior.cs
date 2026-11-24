// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS;

namespace ASPIRE.Common.Behaviors;

/// <summary>
///     MediatR pipeline behavior that transparently resolves and populates cache entries
///     via <see cref="INexusHybridCache"/> for queries implementing <see cref="ICacheableQuery{TResponse}"/>.
/// </summary>
public sealed class CachingBehavior<TRequest, TResponse>(
    ILogger<CachingBehavior<TRequest, TResponse>> logger,
    INexusHybridCache? cache = null)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ICacheableQuery<TResponse> cacheable || cache is null)
        {
            return await next(cancellationToken);
        }

        string cacheKey = cacheable.CacheKey;
        string requestName = typeof(TRequest).Name;

        logger.LogDebug(
            "[MediatR:Cache] Resolving cache key '{CacheKey}' for {RequestName}...",
            cacheKey,
            requestName);

        TResponse? result = await cache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                logger.LogInformation(
                    "[MediatR:Cache] Cache MISS for '{CacheKey}' on {RequestName}. Executing handler...",
                    cacheKey,
                    requestName);
                return await next(ct);
            },
            l1Expiration: cacheable.CacheDuration,
            l2Expiration: cacheable.CacheDuration,
            allowStaleOnError: cacheable.AllowStaleOnError,
            cancellationToken: cancellationToken);

        return result!;
    }
}
