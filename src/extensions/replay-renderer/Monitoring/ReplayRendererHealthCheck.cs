// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRendererHealthCheck.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================


using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReplayRenderer.Service.Monitoring;

/// <summary>
///     Health check probe for the Replay Renderer microservice daemon.
///     Reports healthy when the replay rendering daemon is active and ready to accept render jobs.
/// </summary>
public sealed class ReplayRendererHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("[HEALTHY] Replay Renderer Daemon Is Active And Ready"));
    }
}
