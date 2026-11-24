// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Services.EntityCache;

public static class PrebakedEntityServiceCollectionExtensions
{
    /// <summary>
    ///     Adds the PrebakedEntityRegistry and EntityCachePrewarmer to the service collection.
    /// </summary>
    public static IServiceCollection AddPrebakedEntityServices(
        this IServiceCollection services,
        string? explicitCachePath = null,
        string? overrideDirectory = null,
        string? s2zArchivePath = null)
    {
        services.AddSingleton<IPrebakedEntityRegistry>(sp =>
        {
            ILogger<PrebakedEntityRegistry>? logger = sp.GetService<ILogger<PrebakedEntityRegistry>>();
            IConfiguration? config = sp.GetService<IConfiguration>();
            return new PrebakedEntityRegistry(logger, config, explicitCachePath, overrideDirectory, s2zArchivePath);
        });

        services.AddHostedService<EntityCachePrewarmer>();
        return services;
    }

    /// <summary>
    ///     Adds the PrebakedEntityRegistry singleton to the service collection.
    /// </summary>
    public static IServiceCollection AddPrebakedEntityRegistry(
        this IServiceCollection services,
        string? explicitCachePath = null)
    {
        services.AddSingleton<IPrebakedEntityRegistry>(sp =>
        {
            ILogger<PrebakedEntityRegistry>? logger = sp.GetService<ILogger<PrebakedEntityRegistry>>();
            IConfiguration? config = sp.GetService<IConfiguration>();
            return new PrebakedEntityRegistry(logger, config, explicitCachePath);
        });

        return services;
    }
}
