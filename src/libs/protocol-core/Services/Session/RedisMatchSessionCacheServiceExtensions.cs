// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RedisMatchSessionCacheServiceExtensions.cs
// System: Kongor Protocol Core - DI Registration Extensions for Live Match Session Cache
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kongor.Core.Services.Session;

public static class RedisMatchSessionCacheServiceExtensions
{
    /// <summary>
    ///     Registers the IRedisMatchSessionCacheService and underlying memory cache in the DI container.
    /// </summary>
    public static IServiceCollection AddRedisMatchSessionCache(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton<IRedisMatchSessionCacheService, RedisMatchSessionCacheService>();
        return services;
    }
}
