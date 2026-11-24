// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using ASPIRE.Common.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Services.Telemetry;

public interface IMasterServerRequestTracker
{
    void TrackRequest();
    long DrainRequestCount();
}

public sealed class MasterServerRequestTracker : IMasterServerRequestTracker
{
    private long _pendingRequests;

    public void TrackRequest()
    {
        Interlocked.Increment(ref _pendingRequests);
    }

    public long DrainRequestCount()
    {
        return Interlocked.Exchange(ref _pendingRequests, 0);
    }
}

public sealed class MasterServerTelemetryWorker(
    IServiceProvider serviceProvider,
    IMasterServerRequestTracker requestTracker,
    ILogger<MasterServerTelemetryWorker> logger) : ResilientBackgroundService(logger, nameof(MasterServerTelemetryWorker))
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;

                IConnectionMultiplexer? muxer = serviceProvider.GetService<IConnectionMultiplexer>();
                if (muxer is null || !muxer.IsConnected)
                    continue;

                IDatabase redis = muxer.GetDatabase();

                long pendingRequests = requestTracker.DrainRequestCount();
                if (pendingRequests > 0)
                {
                    await redis.HashIncrementAsync("TELEMETRY:METRICS", "MasterServerRequestsTotal", pendingRequests).ConfigureAwait(false);
                }

                long mem = Process.GetCurrentProcess().WorkingSet64;
                await redis.HashSetAsync("TELEMETRY:METRICS", "MEM_KONGOR", mem).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to publish periodic telemetry metrics to Redis.");
            }
        }
    }
}
