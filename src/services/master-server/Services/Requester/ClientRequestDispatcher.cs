// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;

namespace KONGOR.MasterServer.Services.Requester;

public class ClientRequestDispatcher(IServiceProvider serviceProvider)
{
    public static readonly FrozenDictionary<string, Type> HandlerMap =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            // Authentication
            ["pre_auth"] = typeof(PreAuthHandler),
            ["srpauth"] = typeof(SRPAuthHandler),
            ["switch_auth"] = typeof(SwitchAuthHandler),
            ["auth"] = typeof(AuthHandler),
            ["aids2cookie"] = typeof(Aids2CookieHandler),
            ["logout"] = typeof(LogoutHandler),

            // Server List & Heroes
            ["get_server_list"] = typeof(ServerListHandler),
            ["grab_server_list"] = typeof(ServerListHandler),
            ["server_list"] = typeof(ServerListHandler),
            ["get_all_heroes"] = typeof(HeroesHandler),
            ["get_hero_list"] = typeof(HeroesHandler),

            // Stats
            ["show_simple_stats"] = typeof(SimpleStatsHandler),
            ["show_stats"] = typeof(SimpleStatsHandler),
            ["get_initstats"] = typeof(InitStatsHandler),
            ["get_match_stats"] = typeof(MatchStatsHandler),
            ["final_match_stats"] = typeof(MatchStatsHandler),
            ["get_match_info"] = typeof(MatchStatsHandler),
            ["multi_match_stats"] = typeof(MatchStatsHandler),
            ["grab_match_info"] = typeof(MatchStatsHandler),
            ["get_match"] = typeof(MatchStatsHandler),
            ["match_stats"] = typeof(MatchStatsHandler),
            ["grab_last_matches"] = typeof(MatchHistoryHandler),
            ["grab_last_matches_from_nick"] = typeof(MatchHistoryHandler),
            ["match_history_overview"] = typeof(MatchHistoryHandler),
            ["get_account_all_hero_stats"] = typeof(HeroStatsHandler),
            ["get_hero_stats"] = typeof(HeroStatsHandler),
            ["get_campaign_hero_stats"] = typeof(HeroStatsHandler),
            ["get_account_mastery"] = typeof(HeroStatsHandler),
            ["get_player_award_summ"] = typeof(GetPlayerAwardSummHandler),
            ["get_seasons"] = typeof(GetSeasonsHandler),
            ["get_ladder"] = typeof(GetLadderHandler),

            // Debug / Echo
            ["echotest"] = typeof(EchoHandler),

            // Game
            ["create_game"] = typeof(GameHandler),
            ["new_game_available"] = typeof(GameHandler),

            // Store
            ["get_upgrades"] = typeof(UpgradesHandler),
            ["selected_upgrades"] = typeof(SelectedUpgradesHandler),
            ["debug_upgrades_diff"] = typeof(UpgradesHandler),
            ["get_products"] = typeof(ProductsHandler),
            ["get_daily_special"] = typeof(DailySpecialHandler),
            ["get_bundle_contents"] = typeof(GetBundleContentsHandler),
            ["claim_season_rewards"] = typeof(RewardsHandler),
            ["claim_mastery_reward"] = typeof(ClaimMasteryRewardHandler),
            ["take_mastery_reward"] = typeof(ClaimMasteryRewardHandler),
            ["use_mastery_boost"] = typeof(UseMasteryBoostHandler),
            ["apply_mastery_boost"] = typeof(UseMasteryBoostHandler),
            ["mastery_boost"] = typeof(UseMasteryBoostHandler),
            ["use_boost"] = typeof(UseMasteryBoostHandler),

            // Misc
            ["upload_replay"] = typeof(ReplayHandler),
            ["get_guide_list_filtered"] = typeof(GuideHandler),
            ["get_guide"] = typeof(GuideHandler),
            ["guide_default_set"] = typeof(GuideHandler),
            ["guide_default_remove"] = typeof(GuideHandler),
            ["guide_favorite_set"] = typeof(GuideHandler),
            ["guide_favorite_remove"] = typeof(GuideHandler),
            ["guide_vote"] = typeof(GuideHandler),
            ["client_events_info"] = typeof(EventsHandler),
            ["get_special_messages"] = typeof(EventsHandler),
            ["set_rank"] = typeof(ClanHandler),
            ["get_clan_info"] = typeof(ClanHandler),
            ["clan_info"] = typeof(ClanHandler),
            ["get_clan_roster"] = typeof(ClanHandler),
            ["clan_roster"] = typeof(ClanHandler),
            ["create_clan"] = typeof(ClanHandler),
            ["clan_create"] = typeof(ClanHandler),
            ["invite_clan_member"] = typeof(ClanHandler),
            ["clan_invite"] = typeof(ClanHandler),
            ["remove_clan_member"] = typeof(ClanHandler),
            ["clan_kick"] = typeof(ClanHandler),
            ["leave_clan"] = typeof(ClanHandler),
            ["clan_leave"] = typeof(ClanHandler),
            ["disband_clan"] = typeof(ClanHandler),
            ["clan_disband"] = typeof(ClanHandler),
            ["new_buddy"] = typeof(FriendsHandler),
            ["add_buddy"] = typeof(FriendsHandler),
            ["approve_buddy"] = typeof(FriendsHandler),
            ["accept_buddy"] = typeof(FriendsHandler),
            ["reject_buddy"] = typeof(FriendsHandler),
            ["remove_buddy"] = typeof(FriendsHandler),
            ["new_banned"] = typeof(FriendsHandler),
            ["remove_banned"] = typeof(FriendsHandler),
            ["new_ignored"] = typeof(FriendsHandler),
            ["remove_ignored"] = typeof(FriendsHandler),
            ["autocomplete"] = typeof(FriendsHandler),
            ["user_search"] = typeof(FriendsHandler),
            ["search_user"] = typeof(FriendsHandler),
            ["nick_search"] = typeof(FriendsHandler),

            // Referrals & Mentorship
            ["set_ingame_referrer"] = typeof(ReferralHandler),
            ["get_referrals"] = typeof(ReferralHandler),

            // Learnatorium Hero Usage
            ["get_hero_usage_list"] = typeof(HeroUsageHandler),
            ["get_hero_usage_graph"] = typeof(HeroUsageHandler),

            // Balance & Event Gifts
            ["get_points"] = typeof(PlayerPointsHandler),
            ["receive_revival_gift"] = typeof(PlayerPointsHandler),

            // System & Chat Notifications
            ["remove_all_notifications"] = typeof(NotificationsHandler),
            ["delete_notification"] = typeof(NotificationsHandler),
            ["test_create_notification"] = typeof(NotificationsHandler)
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public async Task<IActionResult> DispatchAsync(string functionName, HttpContext context,
        CancellationToken cancellationToken = default)
    {
        if (HandlerMap.TryGetValue(functionName, out Type? handlerType))
            if (serviceProvider.GetService(handlerType) is IClientRequestHandler handler)
                return await handler.HandleRequestAsync(context, cancellationToken);

        if (serviceProvider is not IKeyedServiceProvider keyedServiceProvider)
            return new BadRequestObjectResult(
                $"Unsupported Client Requester Controller Form Parameter: f={functionName}");

        {
            IClientRequestHandler? handler = keyedServiceProvider.GetKeyedService<IClientRequestHandler>(functionName);
            if (handler is not null) return await handler.HandleRequestAsync(context, cancellationToken);
        }

        return new BadRequestObjectResult($"Unsupported Client Requester Controller Form Parameter: f={functionName}");
    }
}
