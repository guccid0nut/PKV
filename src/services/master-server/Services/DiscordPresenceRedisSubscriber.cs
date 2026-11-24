// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using ASPIRE.Common.Hosting;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace KONGOR.MasterServer.Services;

public class DiscordPresenceRedisSubscriber(
    IConnectionMultiplexer redis,
    IHubContext<PresenceHub> hubContext,
    ILogger<DiscordPresenceRedisSubscriber> logger) : ResilientBackgroundService(logger, nameof(DiscordPresenceRedisSubscriber))
{
    private const string PresenceChannel = "discord-presence-updates";

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        ISubscriber subscriber = redis.GetSubscriber();

        await subscriber.SubscribeAsync(RedisChannel.Literal(PresenceChannel), (channel, message) =>
        {
            if (!stoppingToken.IsCancellationRequested) _ = ProcessMessageAsync(message, stoppingToken);
        });

        logger.LogInformation("Discord Presence Redis Subscriber initialized.");

        // Wait until shutdown is signaled
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Clean up Redis subscription on graceful shutdown
        logger.LogInformation("Unsubscribing Discord Presence Redis Subscriber...");
        await subscriber.UnsubscribeAsync(RedisChannel.Literal(PresenceChannel));
    }

    private async Task ProcessMessageAsync(RedisValue message, CancellationToken stoppingToken)
    {
        try
        {
            if (message.IsNullOrEmpty) return;

            DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize(message.ToString(), MasterServerJsonContext.Default.DiscordPresenceUpdateDto);
            if (dto != null)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "Received Discord Presence Update for Account {AccountId}: {State} - {Details}",
                        dto.AccountId, dto.State, dto.Details);

                // Note: Publishers (DiscordPresencePublisherService & MasterServerUserLoggedInEventHandler)
                // persist 'presence:dto:{AccountId}' once prior to pub/sub fan-out so N subscriber replicas
                // do not amplify writes N times against Valkey/Redis.

                // Dispatch exclusively to the owning account's SignalR group so playtesters never receive cross-account presence broadcasts or duplicate packets.
                await hubContext.Clients.Group(dto.AccountId.ToString())
                    .SendAsync("PresenceUpdated", dto, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Discord Presence update from Redis.");
        }
    }
}
