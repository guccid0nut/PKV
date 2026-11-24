// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MERRICK.DatabaseContext.Services;

public sealed class DatabaseReadinessHealthCheck(DatabaseInitializer initializer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (initializer.IsCompleted)
        {
            return Task.FromResult(HealthCheckResult.Healthy("MERRICK database initialization completed."));
        }

        if (initializer.LastError is not null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(initializer.LastError));
        }

        return Task.FromResult(HealthCheckResult.Degraded("MERRICK database initialization is still in progress."));
    }
}
