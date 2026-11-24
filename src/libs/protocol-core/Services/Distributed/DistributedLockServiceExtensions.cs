// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ASPIRE.Common.Extensions;

/// <summary>
///     Dependency injection registration extensions for distributed locking services.
/// </summary>
public static class DistributedLockServiceExtensions
{
    /// <summary>
    ///     Registers the <see cref="IDistributedLockService"/> backed by Redis Redlock with in-memory fallback.
    /// </summary>
    public static IServiceCollection AddDistributedLockService(this IServiceCollection services)
    {
        services.TryAddSingleton<IDistributedLockService>(sp =>
        {
            IConnectionMultiplexer? multiplexer = sp.GetService<IConnectionMultiplexer>();
            IDatabase? database = sp.GetService<IDatabase>() ?? multiplexer?.GetDatabase();
            TimeProvider timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            ILogger<RedisRedlockService> logger = sp.GetRequiredService<ILogger<RedisRedlockService>>();

            return new RedisRedlockService(multiplexer, database, timeProvider, logger);
        });

        return services;
    }
}
