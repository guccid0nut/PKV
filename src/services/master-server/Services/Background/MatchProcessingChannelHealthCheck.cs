// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     Readiness health check verifying that <see cref="IMatchProcessingChannel" /> maintains durable
///     Valkey/Redis Streams backing when <see cref="IMatchProcessingChannel.RequireDurableStreams" /> is enabled.
/// </summary>
public sealed class MatchProcessingChannelHealthCheck(IMatchProcessingChannel channel) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (channel.RequireDurableStreams && !channel.IsRedisStreamsSupported)
        {
            string reason = channel.DegradationReason ?? "Redis/Valkey Streams unsupported or disconnected.";
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"MatchProcessingChannel requires durable Valkey/Redis Streams in Production, but Streams are unavailable: {reason}"));
        }

        if (!string.IsNullOrWhiteSpace(channel.DegradationReason))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"MatchProcessingChannel degraded to volatile in-memory channel: {channel.DegradationReason}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"MatchProcessingChannel healthy (Partitions={channel.PartitionCount}, RedisStreams={channel.IsRedisStreamsSupported}, QueueDepth={channel.TotalQueueDepth})."));
    }
}
