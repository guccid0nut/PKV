// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GarnetVectorServiceExtensions.cs
// System: ASPIRE.Common.Extensions
// Description: Dependency injection registration for Microsoft Garnet Vector Client.
// ============================================================================

using ASPIRE.Common.Caching.Vectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ASPIRE.Common.Extensions;

/// <summary>
/// Service collection extensions for registering Microsoft Garnet native Vector Set services.
/// </summary>
public static class GarnetVectorServiceExtensions
{
    /// <summary>
    /// Registers the production <see cref="IGarnetVectorClient"/> backed by Microsoft Garnet
    /// over the ambient <see cref="IConnectionMultiplexer"/>.
    /// </summary>
    public static IServiceCollection AddGarnetVectorClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IGarnetVectorClient>(sp =>
        {
            IConnectionMultiplexer? multiplexer = sp.GetService<IConnectionMultiplexer>();
            ILogger<GarnetVectorClient> logger = sp.GetRequiredService<ILogger<GarnetVectorClient>>();
            return new GarnetVectorClient(multiplexer, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers the high-performance in-process <see cref="IGarnetVectorClient"/> test double
    /// for offline unit testing and development environments without a live Garnet node.
    /// </summary>
    public static IServiceCollection AddInProcessGarnetVectorClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IGarnetVectorClient, InProcessGarnetVectorClient>();
        return services;
    }
}
