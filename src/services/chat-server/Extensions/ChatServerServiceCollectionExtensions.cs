// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Extensions;
using ASPIRE.Common.Services.Matchmaking;
using Kongor.Core.Services.Events;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Services;
using TRANSMUTANSTEIN.ChatServer.Services.Agones;
using TRANSMUTANSTEIN.ChatServer.Services.Events;

namespace TRANSMUTANSTEIN.ChatServer.Extensions;

public static class ChatServerServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Domain, Infrastructure, and Background Hosted Services for TRANSMUTANSTEIN Chat Server.
    /// </summary>
    public static void AddChatServerServices(this IServiceCollection services)
    {
        // Register TimeProvider
        services.AddSingleton(TimeProvider.System);

        // Register IDatabase From IConnectionMultiplexer
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

        // Register Memory Cache & Hybrid Cache
        services.AddNexusHybridCache();

        // Register Asynchronous Domain Event Bus & Subscriptions
        services.AddRedisEventBus();
        services.AddEventHandler<ChatServerMatchCompletedEventHandler>();
        services.AddEventHandler<ChatServerAccountBannedEventHandler>();
        services.AddEventHandler<ChatServerHeroAvatarPurchasedEventHandler>();
        services.AddEventHandler<ChatServerUserLoggedOutEventHandler>();

        // Register Chat Context As Singleton (State Holder)
        services.AddSingleton<IChatContext, ChatContext>();

        // Register Chat Service As Background Hosted Service With Support For Dependency Injection
        services.AddSingleton<ChatService>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ChatService>());

        // Register Live Peer & Friendship Invalidation Processor (Invoked by DistributedChatStateService on 'account:peer_invalidation')
        services.AddSingleton<PeerInvalidationSubscriber>();

        // Register Distributed Chat State Service (Redis Backplane)
        services.AddSingleton<IDistributedChatStateService, DistributedChatStateService>();
        services.AddHostedService(serviceProvider =>
            (DistributedChatStateService)serviceProvider.GetRequiredService<IDistributedChatStateService>());

        // Register Distributed Matchmaking Lease, Stream & Atomic Lua State Services (Valkey/Redis)
        // Wave 3B distributed state defaults to single-pod local mode (false) for 5.0.2 unless explicitly opted in
        // via Matchmaking:DistributedState:Enabled=true or KONGOR_MM_DISTRIBUTED_ENABLED=true.
        services.AddSingleton<IRedisServerSlotLeaseService, RedisServerSlotLeaseService>();
        services.AddSingleton<IRedisMatchmakingStreamService, RedisMatchmakingStreamService>();
        services.AddSingleton<IDistributedMatchmakingStateStore>(sp =>
        {
            IConfiguration? configuration = sp.GetService<IConfiguration>();
            bool configEnabled = configuration?.GetValue<bool>("Matchmaking:DistributedState:Enabled") ?? false;
            string? rawEnv = configuration?["KONGOR_MM_DISTRIBUTED_ENABLED"]
                             ?? Environment.GetEnvironmentVariable("KONGOR_MM_DISTRIBUTED_ENABLED");
            bool envEnabled = !string.IsNullOrWhiteSpace(rawEnv) &&
                              ((bool.TryParse(rawEnv.Trim(), out bool parsedEnv) && parsedEnv) ||
                               string.Equals(rawEnv.Trim(), "1", StringComparison.Ordinal));
            bool enabled = configEnabled || envEnabled;

            ILogger<ValkeyMatchmakingStateStore> logger = sp.GetService<ILogger<ValkeyMatchmakingStateStore>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ValkeyMatchmakingStateStore>.Instance;

            return new ValkeyMatchmakingStateStore(
                logger: logger,
                redis: enabled ? sp.GetService<IConnectionMultiplexer>() : null,
                timeProvider: sp.GetService<TimeProvider>(),
                podId: configuration?["Matchmaking:DistributedState:PodId"],
                keyPrefix: configuration?["Matchmaking:DistributedState:KeyPrefix"]);
        });

        // Register Matchmaking Service As Singleton With Support For Dependency Injection And Background Hosted Service
        services.AddSingleton<IMatchmakingService, MatchmakingService>();
        services.AddSingleton<IDiscordPresencePublisherService, DiscordPresencePublisherService>();
        services.AddHostedService(serviceProvider =>
            (MatchmakingService)serviceProvider.GetRequiredService<IMatchmakingService>());

        // Register Queue Arcade ("While You Wait" Mini-Game) Coordinator Service
        services.AddSingleton<IQueueArcadeService>(serviceProvider => new QueueArcadeCoordinatorService(
            serviceProvider.GetRequiredService<IConfiguration>(),
            serviceProvider.GetRequiredService<IChatContext>(),
            serviceProvider.GetRequiredService<IMatchmakingService>(),
            serviceProvider.GetService<TimeProvider>(),
            randomSeed: null,
            distributedCache: serviceProvider.GetService<IDatabase>()));
        services.AddHostedService(serviceProvider =>
            (QueueArcadeCoordinatorService)serviceProvider.GetRequiredService<IQueueArcadeService>());

        // Register Flood Prevention Service As Background Hosted Service With Support For Dependency Injection
        services.AddSingleton<FloodPreventionService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<FloodPreventionService>());

        // Register Config-Driven Sliding-Window Chat Spam & Profanity Moderation Filter
        services.AddSingleton(sp => new ChatModerationFilterService(
            configuration: sp.GetService<IConfiguration>(),
            timeProvider: sp.GetService<TimeProvider>(),
            logger: sp.GetService<ILogger<ChatModerationFilterService>>(),
            optionsOverride: sp.GetService<ChatModerationOptions>()));

        // Register Pending Clan Service
        services.AddSingleton<IPendingClanService, PendingClanService>();

        // Register Telemetry Publisher Service
        services.AddSingleton<ITelemetryPublisherService, TelemetryPublisherService>();

        // Register Agones Cloud-Native GameServer Allocation Client
        services.AddHttpClient();
        services.AddSingleton<IAgonesAllocationClient>(sp => new AgonesAllocationClient(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AgonesAllocationClient)),
            sp.GetService<ILogger<AgonesAllocationClient>>()));

        // Register Split-Horizon Public WAN IP Discovery Service for TMM, Friend Join & Spectate
        services.AddSingleton<SplitHorizonPublicIpDiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<SplitHorizonPublicIpDiscoveryService>());

        // Register Telemetry Subscriber Service (Redis Pub/Sub)
        services.AddSingleton<ITelemetrySubscriberService, RedisTelemetrySubscriberService>();
        services.AddHostedService(provider =>
            (RedisTelemetrySubscriberService)provider.GetRequiredService<ITelemetrySubscriberService>());

        // Register Clan Update Subscriber (Redis)
        services.AddSingleton<ClanUpdateSubscriber>();
        services.AddHostedService(provider => provider.GetRequiredService<ClanUpdateSubscriber>());

        // Register Player Status Hydration Subscriber (Redis)
        services.AddSingleton<PlayerStatusHydrationSubscriber>();
        services.AddHostedService(provider => provider.GetRequiredService<PlayerStatusHydrationSubscriber>());

        // Add Chat Server Health Check
        services.AddHealthChecks().AddCheck<ChatServerHealthCheck>("TRANSMUTANSTEIN Chat Server Health Check");
    }
}
