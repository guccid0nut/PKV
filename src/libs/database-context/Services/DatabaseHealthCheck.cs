// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Services;

public class DatabaseHealthCheck(DatabaseInitializer initializer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Task? task = initializer.ExecuteTask;

        return task switch
        {
            { IsCompletedSuccessfully: true } => Task.FromResult(
                HealthCheckResult.Healthy("[HEALTHY] Database Is Accepting Connections")),
            { IsFaulted: true } => Task.FromResult(
                HealthCheckResult.Unhealthy($"[UNHEALTHY] {task.Exception.InnerException?.Message}", task.Exception)),
            { IsCanceled: true } => Task.FromResult(
                HealthCheckResult.Unhealthy("[UNHEALTHY] Database Initialization Was Cancelled")),
            _ => Task.FromResult(HealthCheckResult.Unhealthy("[UNHEALTHY] Database Initialization Is In Progress"))
        };
    }
}
