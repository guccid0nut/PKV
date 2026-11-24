// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Resilience;
using Microsoft.Extensions.Caching.Hybrid;
using Polly;
using ZiggyCreatures.Caching.Fusion;

namespace ASPIRE.Common.Caching;

/// <summary>
///     Production-grade hybrid caching implementation backed by <see cref="IFusionCache" /> and <see cref="HybridCache" />.
///     Provides two-tier L1 (in-memory) and L2 (distributed Redis) caching, stampede protection, eager refresh,
///     opt-in fail-safe stale fallback for read-mostly queries, Redis pub/sub backplane invalidation,
///     tag-based invalidation, and zero-allocation MemoryPack binary serialization.
/// </summary>
public class NexusHybridCache(
    HybridCache cache,
    ILogger<NexusHybridCache>? logger = null,
    ResiliencePipeline? resiliencePipeline = null,
    IFusionCache? fusionCache = null) : INexusHybridCache, IStaleTolerantHybridCache
{
    private static readonly TimeSpan DefaultL1Expiration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultL2Expiration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultFailSafeMaxDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultFailSafeThrottleDuration = TimeSpan.FromSeconds(15);
    // Measured cold multi-query profile/stats factories over WAN Postgres (Supabase :6543, ~35ms RTT x 9-14 split queries)
    // legitimately take ~500-550ms; 1000ms prevents false synthetic timeouts while still failing fast on real DB stalls.
    private static readonly TimeSpan DefaultFactorySoftTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan DefaultFactoryHardTimeout = TimeSpan.FromSeconds(5);

    private readonly ResiliencePipeline _resiliencePipeline =
        resiliencePipeline ?? NexusResiliencePipelines.CreateRedisRetryPipeline(logger);

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan? l1Expiration = null,
        TimeSpan? l2Expiration = null,
        CancellationToken cancellationToken = default)
    {
        return await GetOrCreateCoreAsync(
            key,
            factory,
            tags: null,
            l1Expiration: l1Expiration,
            l2Expiration: l2Expiration,
            allowStaleOnError: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        IReadOnlyCollection<string>? tags,
        TimeSpan? l1Expiration = null,
        TimeSpan? l2Expiration = null,
        CancellationToken cancellationToken = default)
    {
        return await GetOrCreateCoreAsync(
            key,
            factory,
            tags: tags,
            l1Expiration: l1Expiration,
            l2Expiration: l2Expiration,
            allowStaleOnError: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        IReadOnlyCollection<string>? tags,
        TimeSpan? l1Expiration,
        TimeSpan? l2Expiration,
        bool allowStaleOnError,
        CancellationToken cancellationToken = default)
    {
        return GetOrCreateCoreAsync(
            key,
            factory,
            tags,
            l1Expiration,
            l2Expiration,
            allowStaleOnError,
            cancellationToken);
    }

    private async Task<T?> GetOrCreateCoreAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        IReadOnlyCollection<string>? tags,
        TimeSpan? l1Expiration,
        TimeSpan? l2Expiration,
        bool allowStaleOnError,
        CancellationToken cancellationToken)
    {
        TimeSpan effectiveL1 = l1Expiration ?? DefaultL1Expiration;
        TimeSpan effectiveL2 = l2Expiration ?? DefaultL2Expiration;

        if (logger?.IsEnabled(LogLevel.Trace) == true)
        {
            logger.LogTrace(
                "[NexusHybridCache] Requesting key='{Key}' (L1={L1}, L2={L2}, AllowStaleOnError={AllowStale}, Engine={Engine})",
                key,
                effectiveL1,
                effectiveL2,
                allowStaleOnError,
                fusionCache is not null ? "FusionCache" : "HybridCache");
        }

        if (fusionCache is not null)
        {
            bool factoryInvoked = false;
            Exception? factoryException = null;

            try
            {
                return await fusionCache.GetOrSetAsync<T?>(
                    key,
                    async ct =>
                    {
                        factoryInvoked = true;
                        try
                        {
                            return await factory(ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            factoryException = ex;
                            throw;
                        }
                    },
                    opts =>
                    {
                        opts.Duration = effectiveL1;
                        opts.DistributedCacheDuration = effectiveL2;
                        opts.IsFailSafeEnabled = allowStaleOnError;
                        opts.FailSafeMaxDuration = DefaultFailSafeMaxDuration;
                        opts.FailSafeThrottleDuration = DefaultFailSafeThrottleDuration;
                        opts.EagerRefreshThreshold = 0.8f;
                        opts.FactorySoftTimeout = allowStaleOnError ? DefaultFactorySoftTimeout : Timeout.InfiniteTimeSpan;
                        opts.FactoryHardTimeout = DefaultFactoryHardTimeout;
                    },
                    tags,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (factoryInvoked && (ReferenceEquals(ex, factoryException) || ex.InnerException is not null && ReferenceEquals(ex.InnerException, factoryException)))
                {
                    logger?.LogWarning(
                        ex,
                        "[NexusHybridCache] Upstream factory threw for key '{Key}' (allowStaleOnError={AllowStale}); propagating fail-closed error.",
                        key,
                        allowStaleOnError);
                    throw;
                }

                logger?.LogWarning(
                    ex,
                    "[NexusHybridCache] FusionCache infrastructure error prior to factory execution for key '{Key}'. Falling back to direct factory.",
                    key);
                return await factory(cancellationToken).ConfigureAwait(false);
            }
        }

        HybridCacheEntryOptions options = new()
        {
            LocalCacheExpiration = effectiveL1,
            Expiration = effectiveL2
        };

        try
        {
            return await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                return await cache.GetOrCreateAsync(
                    key,
                    async token => await factory(token).ConfigureAwait(false),
                    options,
                    tags,
                    ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "[NexusHybridCache] Distributed cache failure for key '{Key}'. Falling back to direct factory execution.",
                key);
            return await factory(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("[NexusHybridCache] Removing cache key: {Key}", key);

        if (fusionCache is not null)
        {
            try
            {
                await fusionCache.RemoveAsync(key, token: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "[NexusHybridCache] Failed to remove FusionCache key '{Key}' due to cache outage.", key);
            }
            return;
        }

        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                await cache.RemoveAsync(key, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "[NexusHybridCache] Failed to remove cache key '{Key}' due to cache outage.", key);
        }
    }

    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("[NexusHybridCache] Removing cache by tag: {Tag}", tag);

        if (fusionCache is not null)
        {
            try
            {
                await fusionCache.RemoveByTagAsync(tag, token: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "[NexusHybridCache] Failed to remove FusionCache tag '{Tag}' due to cache outage.", tag);
            }
            return;
        }

        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                await cache.RemoveByTagAsync(tag, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "[NexusHybridCache] Failed to remove cache tag '{Tag}' due to cache outage.", tag);
        }
    }

    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        List<string> tagList = tags as List<string> ?? [.. tags];
        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("[NexusHybridCache] Removing cache by tags: {Tags}", string.Join(", ", tagList));

        if (fusionCache is not null)
        {
            try
            {
                await fusionCache.RemoveByTagAsync(tagList, token: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "[NexusHybridCache] Failed to remove FusionCache tags due to cache outage.");
            }
            return;
        }

        try
        {
            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                await cache.RemoveByTagAsync(tagList, ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "[NexusHybridCache] Failed to remove cache tags due to cache outage.");
        }
    }
}
