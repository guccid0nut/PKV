// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Hosting;
using DAWNBRINGER.WebPortal.UI.Hubs;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

public class WebPortalTelemetrySubscriberService : ResilientBackgroundService
{
    private const string TelemetryChannelKey = "TELEMETRY:EVENTS";
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly ILogger<WebPortalTelemetrySubscriberService> _logger;

    private readonly IServiceProvider _serviceProvider;
    private readonly ISubscriber? _subscriber;

    public WebPortalTelemetrySubscriberService(
        IServiceProvider serviceProvider,
        IHubContext<TelemetryHub> hubContext,
        ILogger<WebPortalTelemetrySubscriberService> logger,
        IConnectionMultiplexer? multiplexer = null)
        : base(logger, nameof(WebPortalTelemetrySubscriberService))
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;

        if (multiplexer != null)
            try
            {
                _subscriber = multiplexer.GetSubscriber();
            }
            catch
            {
                _subscriber = null;
            }
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (_subscriber == null)
        {
            _logger.LogInformation(
                "[WebPortalTelemetrySubscriber] Redis multiplexer unavailable. Real-time Pub/Sub push disabled.");
            return;
        }

        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[WebPortalTelemetrySubscriber] Subscribing to Redis telemetry channel {Channel}",
                    TelemetryChannelKey);

            await _subscriber.SubscribeAsync(
                RedisChannel.Literal(TelemetryChannelKey),
                (_, _) =>
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using IServiceScope scope = _serviceProvider.CreateScope();
                            IAdminMetricsService adminMetricsService =
                                scope.ServiceProvider.GetRequiredService<IAdminMetricsService>();
                            AdminMetricsDTO metrics = await adminMetricsService.GetAdminMetricsAsync(stoppingToken);
                            await _hubContext.Clients.All.SendAsync("ReceiveMetricsUpdate", metrics, stoppingToken);
                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug(
                                    "[WebPortalTelemetrySubscriber] Broadcasted metrics snapshot (CCU={Ccu}, ActiveServers={ActiveServers}, PopStatus={PopStatus}, GamesInProgress={GamesInProgress}).",
                                    metrics.ConcurrentConnectedUsers,
                                    metrics.ActiveMatchServers,
                                    metrics.PopulationBreakdown?.Status ?? "null",
                                    metrics.PopulationBreakdown?.GamesInProgressTotal ?? 0);
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsEnabled(LogLevel.Warning))
                                _logger.LogWarning(ex,
                                    "[WebPortalTelemetrySubscriber] Error broadcasting metrics snapshot to SignalR clients.");
                        }
                    }, CancellationToken.None);
                });

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (OperationCanceledException)
        {
            // Shutdown gracefully
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebPortalTelemetrySubscriber] Unexpected error in Redis Pub/Sub subscription loop.");
        }
    }
}
