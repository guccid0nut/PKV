// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services;

/// <summary>
///     A hosted service that initializes the Match ID counter in Redis at application startup.
///     This ensures the counter is synchronized with the maximum existing Match ID in the database,
///     preserving uniqueness and continuity.
/// </summary>
public class MatchIdInitializerService(IServiceProvider serviceProvider, ILogger<MatchIdInitializerService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Match ID Counter...");

        using IServiceScope scope = serviceProvider.CreateScope();
        try
        {
            MerrickContext merrickContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            IDatabase distributedCache = scope.ServiceProvider.GetRequiredService<IDatabase>();

            await distributedCache.InitializeMatchIdCounter(merrickContext);

            logger.LogInformation("Match ID Counter Initialized Successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to initialize Match ID Counter from database. Redis match counter will retain existing state.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
