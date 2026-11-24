// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization;

namespace ASPIRE.Common.Extensions;

public static class NexusHybridCacheServiceExtensions
{
    /// <summary>
    ///     Registers and configures <see cref="IFusionCache" /> exposed via Microsoft <see cref="HybridCache" />
    ///     (<c>.AsHybridCache()</c>) with MemoryPack zero-allocation serialization, multi-tier L1/L2 caching,
    ///     conditional StackExchange.Redis pub/sub backplane invalidation, and <see cref="INexusHybridCache" />.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IServiceCollection AddNexusHybridCache(
        this IServiceCollection services,
        Action<HybridCacheOptions>? configure = null)
    {
        services.AddMemoryCache();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHybridCacheSerializerFactory, MemoryPackHybridCacheSerializerFactory>());
        services.TryAddSingleton<IFusionCacheSerializer, MemoryPackFusionCacheSerializer>();

        services.AddFusionCache()
            .WithOptions(options =>
            {
                options.CacheKeyPrefix = "nexus:";
                options.DistributedCacheCircuitBreakerDuration = TimeSpan.FromSeconds(15);
                options.BackplaneCircuitBreakerDuration = TimeSpan.FromSeconds(15);
                options.ReThrowOriginalExceptions = true;
                options.PreferSyncSerialization = true;
                options.FailSafeActivationLogLevel = LogLevel.Warning;
                // Keep internal multi-line SyntheticTimeoutException stacks at Debug; our structured
                // FactorySyntheticTimeout event handler below emits a clean single-line Warning.
                options.FactorySyntheticTimeoutsLogLevel = LogLevel.Debug;
                options.FactoryErrorsLogLevel = LogLevel.Warning;
                options.SerializationErrorsLogLevel = LogLevel.Warning;
            })
            .WithDefaultEntryOptions(new FusionCacheEntryOptions
            {
                Duration = TimeSpan.FromSeconds(30),
                DistributedCacheDuration = TimeSpan.FromMinutes(5),
                IsFailSafeEnabled = false,
                FailSafeMaxDuration = TimeSpan.FromHours(1),
                FailSafeThrottleDuration = TimeSpan.FromSeconds(15),
                EagerRefreshThreshold = 0.8f,
                FactorySoftTimeout = Timeout.InfiniteTimeSpan,
                FactoryHardTimeout = TimeSpan.FromSeconds(5)
            })
            .TryWithRegisteredMemoryCache()
            .TryWithRegisteredLogger()
            .WithSerializer(static sp => sp.GetRequiredService<IFusionCacheSerializer>())
            .TryWithRegisteredDistributedCache(ignoreMemoryDistributedCache: true)
            .TryWithRegisteredBackplane()
            .WithPostSetup(static (sp, fc) =>
            {
                ILogger<NexusHybridCache>? logger = sp.GetService<ILogger<NexusHybridCache>>();

                fc.Events.FailSafeActivate += (_, e) =>
                    logger?.LogWarning("[FusionCache:FailSafe] Activated stale fallback for key '{Key}'.", e.Key);
                fc.Events.FactorySyntheticTimeout += (_, e) =>
                    logger?.LogWarning("[FusionCache:SoftTimeout] Factory soft-timeout triggered for key '{Key}'.", e.Key);
                fc.Events.EagerRefresh += (_, e) =>
                    logger?.LogDebug("[FusionCache:EagerRefresh] Background eager refresh started for key '{Key}'.", e.Key);
                fc.Events.Backplane.MessagePublished += (_, e) =>
                    logger?.LogDebug("[FusionCache:Backplane] Published invalidation message for key '{Key}'.", e.Message.CacheKey);
                fc.Events.Backplane.MessageReceived += (_, e) =>
                    logger?.LogDebug("[FusionCache:Backplane] Received peer invalidation message for key '{Key}'.", e.Message.CacheKey);

                IConnectionMultiplexer? mux = sp.GetService<IConnectionMultiplexer>();
                if (mux is not null && fc.HasDistributedCache && !fc.HasBackplane)
                {
                    RedisBackplane backplane = new(
                        Options.Create(new RedisBackplaneOptions
                        {
                            ConnectionMultiplexerFactory = () => Task.FromResult(mux)
                        }),
                        sp.GetService<ILogger<RedisBackplane>>());

                    fc.SetupBackplane(backplane);
                    logger?.LogInformation(
                        "[FusionCache:Backplane] Attached StackExchange.Redis pub/sub backplane for multi-replica L1/L2 coherence.");
                }
                else if (mux is not null && !fc.HasDistributedCache)
                {
                    logger?.LogInformation(
                        "[FusionCache:Backplane] Skipping RedisBackplane attachment because no L2 IDistributedCache is registered (avoids L1 self-invalidation).");
                }
            })
            .AsHybridCache();

        services.TryAddSingleton<INexusHybridCache, NexusHybridCache>();

        return services;
    }
}
