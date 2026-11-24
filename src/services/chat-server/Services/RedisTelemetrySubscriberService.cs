// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Hosting;
using TRANSMUTANSTEIN.ChatServer.Serialization;

#pragma warning disable CA1848, CA2254
// ReSharper disable LogMessageIsConditional
// ReSharper disable LoggingMessageIsConditional
// ReSharper disable LogMessageIsConditionalWarning
// ReSharper disable StringInterpolationInLoggingIsExpensive
// ReSharper disable PassStringInterpolationToLog

namespace TRANSMUTANSTEIN.ChatServer.Services;

public class RedisTelemetrySubscriberService : ResilientBackgroundService, ITelemetrySubscriberService
{
    private readonly ILogger<RedisTelemetrySubscriberService> _logger;
    private readonly TelemetryPublisherService? _publisherService;
    private readonly ISubscriber? _subscriber;

    public RedisTelemetrySubscriberService(
        ITelemetryPublisherService? publisherService,
        ILogger<RedisTelemetrySubscriberService> logger,
        IConnectionMultiplexer? multiplexer = null)
        : base(logger, nameof(RedisTelemetrySubscriberService))
    {
        _logger = logger;
        _publisherService = publisherService as TelemetryPublisherService;
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
        if (_subscriber == null || _publisherService == null)
        {
            _logger.LogInformation(
                "[TelemetrySubscriber] Redis subscriber or publisher unavailable. Running in local mode.");
            return;
        }

        try
        {
#pragma warning disable CA1873
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("[TelemetrySubscriber] Subscribing to Redis channel {Channel}",
                    TelemetryPublisherService.TelemetryChannelKey);
#pragma warning restore CA1873

            await _subscriber.SubscribeAsync(
                RedisChannel.Literal(TelemetryPublisherService.TelemetryChannelKey),
                (channel, message) =>
                {
                    try
                    {
                        byte[]? rawBytes = (byte[]?)message;
                        if (rawBytes == null || rawBytes.Length == 0) return;

                        TelemetryEventMessage? eventMsg;
                        if (rawBytes[0] == (byte)'{')
                        {
                            eventMsg = JsonSerializer.Deserialize(rawBytes, ChatServerJsonContext.Default.TelemetryEventMessage);
                        }
                        else
                        {
                            eventMsg = MemoryPack.MemoryPackSerializer.Deserialize<TelemetryEventMessage>(rawBytes);
                        }

                        if (eventMsg != null) _publisherService.HandleRemoteTelemetryEvent(eventMsg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[TelemetrySubscriber] Error processing incoming Pub/Sub telemetry message.");
                    }
                });

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TelemetrySubscriber] Unexpected error in Redis telemetry subscriber loop.");
        }
    }
}
