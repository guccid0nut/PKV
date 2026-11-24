// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Supabase;
using KONGOR.MasterServer.Features.Matches.GetMatchHistory;
using KONGOR.MasterServer.Features.Matches.StartMatch;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Services.Catalog;
using KONGOR.MasterServer.Services.Quests;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Storage;
using KONGOR.MasterServer.Services.Store;

namespace KONGOR.MasterServer.Extensions;

// ReSharper disable UnusedMethodReturnValue.Global
public static class MasterServerServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Client Request Dispatcher and all Keyed Client Request Handlers for KONGOR Master Server.
    /// </summary>
    public static IServiceCollection AddMasterServerHandlers(this IServiceCollection services)
    {
        // Register TimeProvider
        services.AddSingleton(TimeProvider.System);

        // Register Zero-Allocation Pre-Baked Catalog Cache
        services.AddSingleton<IPrebakedCatalogCache, PrebakedCatalogCache>();

        // Register Vertical Slice Feature Handlers
        services.AddScoped<ISubmitMatchStatsHandler, SubmitMatchStatsHandler>();
        services.AddScoped<IStartMatchHandler, StartMatchHandler>();
        services.AddScoped<IGetMatchHistoryHandler, GetMatchHistoryHandler>();
        services.AddScoped<global::KONGOR.MasterServer.Services.Plinko.ICodexGraffitiService, global::KONGOR.MasterServer.Services.Plinko.CodexGraffitiService>();
        services.AddScoped<IWarEffortService, WarEffortService>();
        services.AddScoped<IWarEffortProgressionService, WarEffortProgressionService>();

        // Register Client Request Dispatcher
        services.AddScoped<ClientRequestDispatcher>();

        // Register Concrete Handlers for Direct FrozenDictionary Dispatch
        services.AddScoped<PreAuthHandler>();
        services.AddScoped<SRPAuthHandler>();
        services.AddScoped<SwitchAuthHandler>();
        services.AddScoped<AuthHandler>();
        services.AddScoped<Aids2CookieHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<ServerListHandler>();
        services.AddScoped<HeroesHandler>();
        services.AddScoped<SimpleStatsHandler>();
        services.AddScoped<InitStatsHandler>();
        services.AddScoped<MatchStatsHandler>();
        services.AddScoped<MatchHistoryHandler>();
        services.AddScoped<HeroStatsHandler>();
        services.AddScoped<GetPlayerAwardSummHandler>();
        services.AddScoped<GetSeasonsHandler>();
        services.AddScoped<GetLadderHandler>();
        services.AddScoped<EchoHandler>();
        services.AddScoped<GameHandler>();
        services.AddScoped<UpgradesHandler>();
        services.AddScoped<SelectedUpgradesHandler>();
        services.AddScoped<ProductsHandler>();
        services.AddScoped<DailySpecialHandler>();
        services.AddScoped<GetBundleContentsHandler>();
        services.AddScoped<IBundleCatalogService, BundleCatalogService>();
        services.AddScoped<IUpgradesResolutionService, UpgradesResolutionService>();
        services.AddScoped<RewardsHandler>();
        services.AddScoped<ClaimMasteryRewardHandler>();
        services.AddScoped<UseMasteryBoostHandler>();
        services.AddScoped<ReplayHandler>();
        services.AddScoped<GuideHandler>();
        services.AddScoped<EventsHandler>();
        services.AddScoped<ClanHandler>();
        services.AddScoped<FriendsHandler>();
        services.AddScoped<ReferralHandler>();
        services.AddScoped<HeroUsageHandler>();
        services.AddScoped<PlayerPointsHandler>();
        services.AddScoped<NotificationsHandler>();

        // Register Keyed Authentication Handlers
        services.AddKeyedScoped<IClientRequestHandler, PreAuthHandler>("pre_auth");
        services.AddKeyedScoped<IClientRequestHandler, SRPAuthHandler>("srpauth");
        services.AddKeyedScoped<IClientRequestHandler, SwitchAuthHandler>("switch_auth");
        services.AddKeyedScoped<IClientRequestHandler, AuthHandler>("auth");
        services.AddKeyedScoped<IClientRequestHandler, Aids2CookieHandler>("aids2cookie");
        services.AddKeyedScoped<IClientRequestHandler, LogoutHandler>("logout");

        // Register Server List & Heroes Handlers
        services.AddKeyedScoped<IClientRequestHandler, ServerListHandler>("get_server_list");
        services.AddKeyedScoped<IClientRequestHandler, ServerListHandler>("grab_server_list");
        services.AddKeyedScoped<IClientRequestHandler, ServerListHandler>("server_list");
        services.AddKeyedScoped<IClientRequestHandler, HeroesHandler>("get_all_heroes");
        services.AddKeyedScoped<IClientRequestHandler, HeroesHandler>("get_hero_list");

        // Register Stats Handlers
        services.AddKeyedScoped<IClientRequestHandler, SimpleStatsHandler>("show_simple_stats");
        services.AddKeyedScoped<IClientRequestHandler, SimpleStatsHandler>("show_stats");
        services.AddKeyedScoped<IClientRequestHandler, InitStatsHandler>("get_initstats");
        services.AddKeyedScoped<IClientRequestHandler, MatchStatsHandler>("get_match_stats");
        services.AddKeyedScoped<IClientRequestHandler, MatchStatsHandler>("final_match_stats");
        services.AddKeyedScoped<IClientRequestHandler, MatchHistoryHandler>("grab_last_matches");
        services.AddKeyedScoped<IClientRequestHandler, MatchHistoryHandler>("grab_last_matches_from_nick");
        services.AddKeyedScoped<IClientRequestHandler, MatchHistoryHandler>("match_history_overview");
        services.AddKeyedScoped<IClientRequestHandler, HeroStatsHandler>("get_account_all_hero_stats");
        services.AddKeyedScoped<IClientRequestHandler, HeroStatsHandler>("get_hero_stats");
        services.AddKeyedScoped<IClientRequestHandler, HeroStatsHandler>("get_campaign_hero_stats");
        services.AddKeyedScoped<IClientRequestHandler, HeroStatsHandler>("get_account_mastery");
        services.AddKeyedScoped<IClientRequestHandler, GetPlayerAwardSummHandler>("get_player_award_summ");
        services.AddKeyedScoped<IClientRequestHandler, GetSeasonsHandler>("get_seasons");
        services.AddKeyedScoped<IClientRequestHandler, GetLadderHandler>("get_ladder");

        // ECHO TEST (Debug)
        services.AddKeyedScoped<IClientRequestHandler, EchoHandler>("echotest");

        // Register Game Handlers
        services.AddKeyedScoped<IClientRequestHandler, GameHandler>("create_game");
        services.AddKeyedScoped<IClientRequestHandler, GameHandler>("new_game_available");

        // Register Store Handlers
        services.AddKeyedScoped<IClientRequestHandler, UpgradesHandler>("get_upgrades");
        services.AddKeyedScoped<IClientRequestHandler, SelectedUpgradesHandler>("selected_upgrades");
        services.AddKeyedScoped<IClientRequestHandler, UpgradesHandler>("debug_upgrades_diff");
        services.AddKeyedScoped<IClientRequestHandler, ProductsHandler>("get_products");
        services.AddKeyedScoped<IClientRequestHandler, DailySpecialHandler>("get_daily_special");
        services.AddKeyedScoped<IClientRequestHandler, GetBundleContentsHandler>("get_bundle_contents");
        services.AddKeyedScoped<IClientRequestHandler, RewardsHandler>("claim_season_rewards");
        services.AddKeyedScoped<IClientRequestHandler, ClaimMasteryRewardHandler>("claim_mastery_reward");
        services.AddKeyedScoped<IClientRequestHandler, ClaimMasteryRewardHandler>("take_mastery_reward");
        services.AddKeyedScoped<IClientRequestHandler, UseMasteryBoostHandler>("use_mastery_boost");
        services.AddKeyedScoped<IClientRequestHandler, UseMasteryBoostHandler>("apply_mastery_boost");
        services.AddKeyedScoped<IClientRequestHandler, UseMasteryBoostHandler>("mastery_boost");
        services.AddKeyedScoped<IClientRequestHandler, UseMasteryBoostHandler>("use_boost");

        // Register Misc Handlers
        services.AddKeyedScoped<IClientRequestHandler, ReplayHandler>("upload_replay");
        services.AddKeyedScoped<IClientRequestHandler, GuideHandler>("get_guide_list_filtered");
        services.AddKeyedScoped<IClientRequestHandler, GuideHandler>("get_guide");
        services.AddKeyedScoped<IClientRequestHandler, EventsHandler>("client_events_info");
        services.AddKeyedScoped<IClientRequestHandler, EventsHandler>("get_special_messages");
        services.AddKeyedScoped<IClientRequestHandler, ClanHandler>("set_rank");
        services.AddKeyedScoped<IClientRequestHandler, FriendsHandler>("remove_buddy");
        services.AddKeyedScoped<IClientRequestHandler, FriendsHandler>("new_banned");
        services.AddKeyedScoped<IClientRequestHandler, FriendsHandler>("remove_banned");
        services.AddKeyedScoped<IClientRequestHandler, FriendsHandler>("new_ignored");
        services.AddKeyedScoped<IClientRequestHandler, FriendsHandler>("remove_ignored");

        // Register Referral Handlers
        services.AddKeyedScoped<IClientRequestHandler, ReferralHandler>("set_ingame_referrer");
        services.AddKeyedScoped<IClientRequestHandler, ReferralHandler>("get_referrals");

        // Register Learnatorium Hero Usage Handlers
        services.AddKeyedScoped<IClientRequestHandler, HeroUsageHandler>("get_hero_usage_list");
        services.AddKeyedScoped<IClientRequestHandler, HeroUsageHandler>("get_hero_usage_graph");

        // Register Points & Revival Handlers
        services.AddKeyedScoped<IClientRequestHandler, PlayerPointsHandler>("get_points");
        services.AddKeyedScoped<IClientRequestHandler, PlayerPointsHandler>("receive_revival_gift");

        // Register System & Chat Notification Handlers
        services.AddKeyedScoped<IClientRequestHandler, NotificationsHandler>("remove_all_notifications");
        services.AddKeyedScoped<IClientRequestHandler, NotificationsHandler>("delete_notification");
        services.AddKeyedScoped<IClientRequestHandler, NotificationsHandler>("test_create_notification");

        return services;
    }

    public static IServiceCollection AddReplayStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReplayStorageSettings>(configuration.GetSection("ReplayStorage"));

        string? providerSetting = configuration["ReplayStorage:Provider"];
        string providerType = providerSetting ?? string.Empty;

        if (string.IsNullOrWhiteSpace(providerType))
        {
            string? supabaseUrl = configuration["Supabase:Url"] ?? configuration["SupabaseUrl"];
            string? supabaseKey = configuration["Supabase:ServiceRoleKey"] ?? configuration["SupabaseServiceRoleKey"] ??
                configuration["Supabase:AnonKey"] ?? configuration["SupabaseAnonKey"];

            if (!string.IsNullOrWhiteSpace(supabaseUrl) && !string.IsNullOrWhiteSpace(supabaseKey))
                providerType = "Supabase";
            else
                providerType = "Local";
        }

        // Validate Cloud Object Storage configuration variables (AWS S3, Cloudflare R2, Google Cloud Storage, MinIO)
        if (providerType.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ||
            providerType.Equals("S3", StringComparison.OrdinalIgnoreCase))
        {
            string? bucketName = configuration["ReplayStorage:Cloud:BucketName"] ??
                                 configuration["ReplayStorage:S3:BucketName"];
            string? accessKey = configuration["ReplayStorage:Cloud:AccessKey"] ??
                                configuration["ReplayStorage:S3:AccessKey"];
            string? secretKey = configuration["ReplayStorage:Cloud:SecretKey"] ??
                                configuration["ReplayStorage:S3:SecretKey"];

            if (string.IsNullOrEmpty(bucketName) || string.IsNullOrEmpty(accessKey) ||
                string.IsNullOrEmpty(secretKey))
                providerType = "Local";
        }

        switch (providerType.ToLowerInvariant())
        {
            case "supabase":
                services.AddSingleton<IReplayStorageProvider, SupabaseStorageProvider>();
                break;
            case "cloud":
            case "s3":
                services.AddSingleton<IReplayStorageProvider, CloudStorageProvider>();
                break;
            default:
                services.AddSingleton<IReplayStorageProvider, LocalDiskStorageProvider>();
                break;
        }

        services.AddHttpClient<SupabaseStorageClient>();
        services.AddSingleton<IReplayOfflineBufferService, ReplayOfflineBufferService>();
        services.AddSingleton<ReplayAutoUploadService>();
        services.AddSingleton<IReplayAutoUploadService>(sp => sp.GetRequiredService<ReplayAutoUploadService>());
        services.AddHostedService(sp => sp.GetRequiredService<ReplayAutoUploadService>());

        return services;
    }
}
