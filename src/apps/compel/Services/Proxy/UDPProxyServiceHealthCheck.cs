// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Proxy;

/// <summary>
///     Reports the proxy degraded if one or more of its game/voice ports failed to bind, so a partial bind failure is
///     visible via "/health" instead of being masked by the aggregate "IsRunning" flag.
/// </summary>
internal sealed class UDPProxyServiceHealthCheck(UDPProxyService proxy) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        HealthCheckResult result = proxy.FailedForwarderCount switch
        {
            0 => HealthCheckResult.Healthy(),
            _ => HealthCheckResult.Degraded($"{proxy.FailedForwarderCount} Proxy Port(s) Failed To Bind")
        };

        return Task.FromResult(result);
    }
}
