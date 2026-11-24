// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Execution context and routing parameters for K2 navigation.
/// </summary>
public sealed record K2NavigationContext
{
    public string Destination { get; init; } = "main_menu";
    public string? Category { get; init; }
    public string? SubCategory { get; init; }
    public string? Tab { get; init; }
    public string? Subpage { get; init; }
    public string? Hero { get; init; }
    public string? AltSkin { get; init; }
    public string? Search { get; init; }
    public string? Account { get; init; }
    public string? GameName { get; init; }
    public string? Map { get; init; }
    public string? MatchId { get; init; }
    public int? Slot { get; init; }
}

/// <summary>
/// Authoritative route definition for a destination in the K2 Game Client.
/// </summary>
public sealed record K2NavigationRoute
{
    public string Destination { get; init; } = string.Empty;
    public string TargetInterface { get; init; } = "main";
    public string VerificationWidget { get; init; } = string.Empty;
    public string? ExpectedCvar { get; init; }
    public string? ExpectedCvarValue { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public Func<K2NavigationContext, string> BuildLuaScript { get; init; } = _ => string.Empty;
}

/// <summary>
/// Canonical registry mapping logical UI destinations to K2 interfaces, container widgets,
/// expected cvar states, and proven Lua transition scripts.
/// </summary>
public static class K2RouteRegistry
{
    private static readonly ConcurrentDictionary<string, K2NavigationRoute> Routes = new(StringComparer.OrdinalIgnoreCase);

    static K2RouteRegistry()
    {
        RegisterRoute(new K2NavigationRoute
        {
            Destination = "main_menu",
            TargetInterface = "main",
            VerificationWidget = "systembar",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "",
            Aliases = ["main", "menu", "home", "dashboard"],
            BuildLuaScript = _ =>
                "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
                "if main then " +
                "Set('_mainmenu_currentpanel', ''); " +
                "local switcher = main:GetWidget('MainMenuPanelSwitcher'); " +
                "if switcher and switcher.DoEvent then " +
                "switcher:DoEvent(); " +
                "elseif main.HoNMainF then " +
                "main:HoNMainF('MainMenuPanelToggle', '', nil, false); " +
                "end; " +
                "local sysbar = main:GetWidget('systembar'); " +
                "if sysbar and sysbar.SetVisible then sysbar:SetVisible(true); end; " +
                "Echo('[NEXUS_EVENT] OPEN_MAIN_MENU|{\"success\":true}'); " +
                "end"
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "store",
            TargetInterface = "main",
            VerificationWidget = "store_container2",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "store_container2",
            Aliases = ["shop", "store2", "altavatars", "skins"],
            BuildLuaScript = ctx =>
            {
                string? item = ctx.AltSkin ?? ctx.Hero ?? ctx.Search;
                if (!string.IsNullOrWhiteSpace(ctx.Hero) &&
                    !string.IsNullOrWhiteSpace(ctx.AltSkin) &&
                    !ctx.AltSkin.Contains('.', StringComparison.Ordinal) &&
                    !int.TryParse(ctx.AltSkin, out _))
                {
                    string heroKey = ctx.Hero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
                        ? ctx.Hero
                        : $"Hero_{ctx.Hero}";
                    item = $"{heroKey}.{ctx.AltSkin}";
                }
                return UiFlowHandler.BuildOpenStoreLua(ctx.Tab ?? ctx.Category, ctx.Subpage, item);
            }
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "learnatorium",
            TargetInterface = "main",
            VerificationWidget = "learnatorium_base",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "compendium",
            Aliases = ["learn", "guide", "learnatorium_base"],
            BuildLuaScript = _ => UiFlowHandler.BuildOpenLearnatoriumLua()
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "compendium",
            TargetInterface = "main",
            VerificationWidget = "compendium",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "compendium",
            Aliases = ["heroes", "hero_compendium", "hero_grid", "roster"],
            BuildLuaScript = _ => UiFlowHandler.BuildOpenLearnatoriumLua()
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "watch",
            TargetInterface = "main",
            VerificationWidget = "watch_system",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "watch_system",
            Aliases = ["replays", "streams", "broadcasts"],
            BuildLuaScript = ctx => UiFlowHandler.BuildOpenWatchLua(ctx.Tab ?? "replays")
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "live",
            TargetInterface = "main",
            VerificationWidget = "watch_system",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "watch_system",
            Aliases = ["live_matches", "spectate"],
            BuildLuaScript = _ => UiFlowHandler.BuildOpenWatchLua("live")
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "options",
            TargetInterface = "main",
            VerificationWidget = "game_options",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "game_options",
            Aliases = ["settings", "game_options", "config", "preferences"],
            BuildLuaScript = ctx =>
            {
                string tab = ctx.Tab ?? ctx.Category ?? "graphics";
                int categoryIndex = UiFlowHandler.OptionsTabCategories.TryGetValue(tab, out int idx) ? idx : 1;
                int subCategoryIndex = 0;
                string? rawSub = ctx.SubCategory ?? ctx.Subpage;
                if (!string.IsNullOrWhiteSpace(rawSub))
                {
                    if (int.TryParse(rawSub, out int parsedSub))
                    {
                        subCategoryIndex = parsedSub;
                    }
                    else if (UiFlowHandler.OptionsSubCategories.TryGetValue(rawSub.Trim(), out int namedSub))
                    {
                        subCategoryIndex = namedSub;
                    }
                }
                return UiFlowHandler.BuildOpenOptionsLua(tab, categoryIndex, subCategoryIndex);
            }
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "profile",
            TargetInterface = "main",
            VerificationWidget = "playerstats",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "playerstats",
            Aliases = ["stats", "playerstats", "ladder"],
            BuildLuaScript = ctx => UiFlowHandler.BuildOpenStatsLua(ctx.Account, ctx.Tab ?? "overview")
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "match_stats",
            TargetInterface = "main",
            VerificationWidget = "match_stats",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "match_stats",
            Aliases = ["match_info", "postgame", "matchstats"],
            BuildLuaScript = ctx => UiFlowHandler.BuildOpenMatchStatsLua(ctx.MatchId ?? ctx.Subpage, ctx.Account)
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "region_select",
            TargetInterface = "main",
            VerificationWidget = "region_select",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "region_select",
            Aliases = ["regions", "region"],
            BuildLuaScript = _ =>
                "local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main'); " +
                "if main then " +
                "if main_SimpleTrigger then pcall(main_SimpleTrigger, 'region_select'); end " +
                "local rs = main:GetWidget('region_select'); if rs and rs.SetVisible then rs:SetVisible(true); end " +
                "Echo('[NEXUS_EVENT] OPEN_REGION_SELECT|{\"success\":true}'); " +
                "end"
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "hero_preview",
            TargetInterface = "main",
            VerificationWidget = "store_container2",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "store_container2",
            Aliases = ["preview", "model_preview", "avatar_preview"],
            BuildLuaScript = ctx =>
                UiFlowHandler.BuildOpenStoreLua("heroes", ctx.Hero, ctx.AltSkin)
        });

        RegisterRoute(new K2NavigationRoute
        {
            Destination = "lobby",
            TargetInterface = "main",
            VerificationWidget = "hosted_game",
            ExpectedCvar = "_mainmenu_currentpanel",
            ExpectedCvarValue = "hosted_game",
            Aliases = ["game_lobby", "custom_game", "public_games", "hosted_games", "create_game", "hosted_game_create"],
            BuildLuaScript = ctx =>
            {
                string lobbyType = string.Equals(ctx.Subpage, "create_game", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ctx.Tab, "create_game", StringComparison.OrdinalIgnoreCase)
                    ? "create_game"
                    : "custom";
                return UiFlowHandler.BuildOpenLobbyLua("hosted_game", lobbyType, ctx.Map ?? "caldavar", ctx.Hero ?? "Hero_Pyromancer", ctx.GameName ?? "PracticeGame");
            }
        });
    }

    public static void RegisterRoute(K2NavigationRoute route)
    {
        Routes[route.Destination] = route;
        foreach (string alias in route.Aliases)
        {
            Routes[alias] = route;
        }
    }

    public static K2NavigationRoute? ResolveRoute(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Routes.TryGetValue("main_menu", out K2NavigationRoute? defaultRoute) ? defaultRoute : null;
        }

        string clean = destination.Trim().ToLowerInvariant();
        if (Routes.TryGetValue(clean, out K2NavigationRoute? route))
        {
            return route;
        }

        // Fuzzy match without underscores/hyphens/spaces
        string normalized = clean.Replace("_", "").Replace("-", "").Replace(" ", "");
        foreach (var kvp in Routes)
        {
            string keyNorm = kvp.Key.Replace("_", "").Replace("-", "").Replace(" ", "");
            if (string.Equals(keyNorm, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    public static IReadOnlyList<K2NavigationRoute> GetAllRoutes()
    {
        return Routes.Values.DistinctBy(r => r.Destination).ToList();
    }
}
