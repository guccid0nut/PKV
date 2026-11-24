// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json.Serialization;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Models;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using DAWNBRINGER.WebPortal.UI.Infrastructure;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public partial class HomeController
{
    public sealed class GlobalSearchResultItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("subtitle")]
        public string Subtitle { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("badge")]
        public string Badge { get; set; } = string.Empty;

        [JsonPropertyName("iconUrl")]
        public string IconUrl { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        [JsonPropertyName("avatarUrl")]
        public string? AvatarUrl { get; set; }

        [JsonPropertyName("heroIcon")]
        public string? HeroIcon { get; set; }

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonIgnore]
        public int TieBreakerWeight { get; set; }
    }

    private record PortalNavigationPage(
        string Name,
        string Subtitle,
        string Url,
        string Badge,
        string IconEmoji,
        string[] Keywords,
        bool IsStaffOnly = false
    );

    private static readonly IReadOnlyList<PortalNavigationPage> StaticNavigationPages =
    [
        new("Leaderboard", "Top ranked Newerth players", "/leaderboard", "PAGE", "🏆",
            ["leaderboard", "leaderboards", "top", "rank", "ranks", "ranking", "rankings", "trophy", "ladder", "mmr", "competitive"]),
        new("Clans", "Newerth clans, standings & member rosters", ClansController.DirectoryRoute, "PAGE", "🛡️",
            ["clan", "clans", "guild", "guilds", "roster", "rosters", "tag", "tags", "team", "teams"]),
        new("Hero Codex", "All 128 heroes & abilities", "/codex", "PAGE", "🦸",
            ["codex", "hero", "heroes", "roster", "champions", "abilities", "skills"]),
        new("Item Compendium", "Recipes, stats & passive FX", "/items", "PAGE", "📦",
            ["item", "items", "compendium", "shop", "recipes", "gear", "equipment", "artifacts"]),
        new("Hero Guides", "Community builds & leveling", "/guides", "PAGE", "📜",
            ["guide", "guides", "build", "builds", "guide builder", "leveling", "strategy"]),
        new("Newerth Lore", "Interactive comic archives", "/lore", "STAFF", "📖",
            ["lore", "comic", "comics", "story", "stories", "history", "universe", "tales"],
            IsStaffOnly: true),
        new("Vanity Store", "Avatars, Taunts & Announcers", "/store", "PAGE", "🏪",
            ["store", "shop", "armory", "vanity", "avatars", "taunts", "announcers", "skins", "cosmetics"]),
        new("Goblin Plinko", "Daily drop & coin rewards", "/store?category=plinko", "PAGE", "🎪",
            ["plinko", "goblin plinko", "drop", "drops", "tickets", "gambling", "rewards", "coins"]),
        new("Inventory Management", "Player vaults & cosmetic grants", "/staff/inventory", "STAFF", "🎒",
            ["inventory", "inventory management", "vault", "cosmetics", "grants", "staff inventory"],
            IsStaffOnly: true),
        new("Live Matches", "Real-time spectator radar", "/live", "STAFF", "⚔️",
            ["live", "spectator", "radar", "watch", "matches", "stream", "games"],
            IsStaffOnly: true),
        new("Match History", "Scoreboards & replays", "/matches", "PAGE", "📊",
            ["matches", "match history", "history", "recent", "games", "scoreboards"]),
        new("Player Stats", "MMR, KDR & Hero mastery", "/stats", "PAGE", "📈",
            ["stats", "player stats", "statistics", "kda", "mmr", "winrate", "analytics"]),
        new("Donut ARcade Hub", "Donut Mixed Reality Arcade", "/ar-mini-games", "PAGE", "🕹️",
            ["ar", "arcade", "mini games", "augmented reality", "tabletop", "games", "donut"]),
        new("Monkey King Checkers", "Tabletop Legion vs Hellbourne", "/ar-mini-games/monkey-checkers", "PAGE", "👑",
            ["monkey king", "checkers", "monkey king checkers", "board game", "chess", "tabletop"]),
        new("DevoWars AR 1v1", "Tabletop Devourer Hook Battle", "/ar-mini-games/devo-wars", "PAGE", "🥩",
            ["devowars", "devo wars", "hook", "devourer", "meat hook", "duel", "pvp", "butcher"]),
        new("Kongor AR Chess", "Tabletop Legion vs Hellbourne", "/ar-mini-games/chess", "PAGE", "♟️",
            ["chess", "kongor chess", "ar chess", "board game", "tabletop", "legion vs hellbourne"]),
        new("Cloud Sync", "Keybinds & client profiles", "/cloud", "PAGE", "☁️",
            ["cloud", "cloud sync", "keybinds", "settings backup", "profiles", "sync"]),
        new("PKV Setup", "Open-source PKV repository & BYOA setup", "/setup", "PAGE", "🚀",
            ["pkv", "launcher", "setup", "github", "source", "byoa", "project kongor v"]),
        new("Text Generator", "Custom in-game color chat", "/tools/message-generator", "PAGE", "🎨",
            ["text generator", "chat", "color chat", "formatting", "color codes", "colors"]),
        new("Developer Docs", "DocFX API & Engine docs", "/docs", "STAFF", "📖",
            ["docs", "developer docs", "docfx", "api", "engine docs", "documentation", "sdk"],
            IsStaffOnly: true),
        new("Issues & Telemetry", "Self-healing & live triage", "/admin/issues", "STAFF", "⚡",
            ["issues", "telemetry", "triage", "self-healing", "bugs", "diagnostics", "system"],
            IsStaffOnly: true),
        new("Staff Portal", "Reports & Host moderation", "/staff", "STAFF", "🛡️",
            ["staff", "staff portal", "moderation", "admin", "reports", "hosts", "bans"],
            IsStaffOnly: true),
        new("Account Settings", "Password, avatar & preferences", "/Account/Settings", "PAGE", "⚙️",
            ["settings", "account", "profile", "password", "avatar", "discord", "user settings"])
    ];

    [HttpGet("api/global/search")]
    public async Task<IActionResult> SearchGlobal(string q, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            _logger.LogInformation("[GlobalSearch] Empty or short query received: '{Query}'", q);
            return Json(Array.Empty<object>());
        }

        string rawQuery = q.Trim();
        bool isHandleQuery = rawQuery.StartsWith('@');
        bool isClanBracketQuery = !isHandleQuery && rawQuery.StartsWith('[');
        string cleanQuery = rawQuery.ToLowerInvariant().TrimStart('@');
        string cleanClanQuery = isClanBracketQuery
            ? rawQuery.TrimStart('[').TrimEnd(']').Trim().ToLowerInvariant()
            : cleanQuery;

        if (isClanBracketQuery && cleanClanQuery.Length == 0)
        {
            _logger.LogInformation("[GlobalSearch] Empty bracketed clan query received: '{RawQuery}'", rawQuery);
            return Json(Array.Empty<object>());
        }

        bool isStaffUser = StaffAuthorizationHelper.IsStaff(User);

        _logger.LogInformation("[GlobalSearch] Executing global search for query='{RawQuery}', clean='{CleanQuery}', isHandle={IsHandle}, isClanBracket={IsClanBracket}, isStaff={IsStaff}",
            rawQuery, cleanQuery, isHandleQuery, isClanBracketQuery, isStaffUser);

        bool enableCache = _configuration.GetValue("Search:EnableCache", false);
        string cacheKey = $"search:global:{(isClanBracketQuery ? $"[{cleanClanQuery}]" : cleanQuery)}:staff={isStaffUser}";
        if (enableCache && _memoryCache.TryGetValue(cacheKey, out List<object>? cached) && cached != null)
        {
            _logger.LogInformation("[GlobalSearch] Cache HIT for key='{CacheKey}' ({Count} items, {ElapsedMs}ms)",
                cacheKey, cached.Count, stopwatch.ElapsedMilliseconds);
            return Json(cached);
        }

        List<GlobalSearchResultItem> candidates = [];

        // 0. Clans Search (Zero extra network hop per keystroke via 60s cached summary list; supports [TAG] shortcut)
        if (!isHandleQuery)
        {
            try
            {
                IReadOnlyList<NEXUS.Contracts.DTOs.ClanSummaryDto> clans =
                    await GetCachedClanSummariesAsync(cancellationToken);
                foreach (NEXUS.Contracts.DTOs.ClanSummaryDto clan in clans)
                {
                    int score = ComputeClanScore(clan, cleanClanQuery, isClanBracketQuery);
                    if (score > 0)
                    {
                        string mmrLabel = clan.AverageMmr.HasValue
                            ? $"Avg {clan.AverageMmr.Value:F0} MMR"
                            : "Unranked";
                        candidates.Add(new GlobalSearchResultItem
                        {
                            Type = "clan",
                            Name = $"[{clan.Tag}] {clan.Name}",
                            Title = $"[{clan.Tag}] {clan.Name}",
                            Subtitle = $"{clan.MemberCount} {(clan.MemberCount == 1 ? "member" : "members")} • {mmrLabel}",
                            Url = $"{ClansController.DetailRoutePrefix}{clan.Id}",
                            Badge = "CLAN",
                            IconUrl = string.Empty,
                            Icon = clan.Tag,
                            Score = score,
                            TieBreakerWeight = clan.MemberCount
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GlobalSearch] Failed scanning clans for query '{CleanClanQuery}'", cleanClanQuery);
            }
        }

        // 1. Navigation Pages & Tools Search
        if (!isHandleQuery && !isClanBracketQuery)
        {
            foreach (var page in StaticNavigationPages)
            {
                if (page.IsStaffOnly && !isStaffUser)
                {
                    continue;
                }

                int score = ComputePageScore(page, cleanQuery);
                if (score > 0)
                {
                    candidates.Add(new GlobalSearchResultItem
                    {
                        Type = "page",
                        Name = page.Name,
                        Title = page.Name,
                        Subtitle = page.Subtitle,
                        Url = page.Url,
                        Badge = page.Badge,
                        IconUrl = string.Empty,
                        Icon = page.IconEmoji,
                        Score = score
                    });
                }
            }
        }

        // 2. Hero Codex Search
        if (!isHandleQuery && !isClanBracketQuery)
        {
            try
            {
                List<HeroDetailDto> heroes = await GetCachedHeroDetailsAsync(cancellationToken);
                foreach (HeroDetailDto h in heroes)
                {
                    int score = ComputeHeroScore(h, cleanQuery);
                    if (score > 0)
                    {
                        string heroIcon = !string.IsNullOrWhiteSpace(h.IconPath) &&
                                          h.IconPath.StartsWith("/images/", StringComparison.OrdinalIgnoreCase) &&
                                          !h.IconPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                            ? h.IconPath
                            : Services.HeroAndMapAssetResolver.ResolveHeroIconUrl(h.HeroCode);
                        candidates.Add(new GlobalSearchResultItem
                        {
                            Type = "hero",
                            Name = h.DisplayName,
                            Title = h.DisplayName,
                            Subtitle = "Hero Codex",
                            Url = $"/codex/{h.HeroCode}",
                            Badge = "HERO",
                            IconUrl = heroIcon,
                            Icon = heroIcon,
                            Score = score
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GlobalSearch] Failed scanning heroes for query '{CleanQuery}'", cleanQuery);
            }
        }

        // 3. Item Compendium Search
        if (!isHandleQuery && !isClanBracketQuery)
        {
            try
            {
                List<HeroItemDto> items = await GetCachedHeroItemsAsync(cancellationToken);
                foreach (var item in items)
                {
                    if (HonItemMapping.MatchesSearch(item.Code, item.Name, cleanQuery))
                    {
                        int score = HonItemMapping.GetSearchScore(item.Code, item.Name, cleanQuery);
                        if (score > 0)
                        {
                            string slug = HonItemMapping.ToWebSlug(item.Code);
                            string itemIcon = !string.IsNullOrWhiteSpace(item.IconPath) &&
                                              item.IconPath.StartsWith("/images/", StringComparison.OrdinalIgnoreCase) &&
                                              !item.IconPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                                ? item.IconPath
                                : $"/images/items/{slug}.png";
                            candidates.Add(new GlobalSearchResultItem
                            {
                                Type = "item",
                                Name = item.Name,
                                Title = item.Name,
                                Subtitle = $"Item ({item.Cost} Gold)",
                                Url = $"/item/{slug}",
                                Badge = "ITEM",
                                IconUrl = itemIcon,
                                Icon = itemIcon,
                                Score = score
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GlobalSearch] Failed scanning items for query '{CleanQuery}'", cleanQuery);
            }
        }

        // 4. Store Avatars & Cosmetics Search
        if (!isHandleQuery && !isClanBracketQuery && _storeService != null)
        {
            try
            {
                var storeItems = _storeService.GetAllItems();
                foreach (var sItem in storeItems)
                {
                    int score = ComputeStoreItemScore(sItem, cleanQuery);
                    if (score > 0)
                    {
                        string cleanHero = !string.IsNullOrWhiteSpace(sItem.HeroName)
                            ? sItem.HeroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()
                            : string.Empty;
                        string heroFallback = !string.IsNullOrEmpty(cleanHero)
                            ? $"/images/heroes/{cleanHero}.png"
                            : (sItem.Type == 2 ? "/images/default-avatar.png" : "/images/heroes/default.png");

                        string storeIcon;
                        if (!string.IsNullOrWhiteSpace(sItem.Resource))
                        {
                            if (sItem.Resource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                sItem.Resource.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                sItem.Resource.StartsWith("/images/", StringComparison.OrdinalIgnoreCase))
                            {
                                storeIcon = sItem.Resource;
                            }
                            else
                            {
                                storeIcon = $"/api/v1/assets/icon?path={Uri.EscapeDataString(sItem.Resource)}";
                            }
                        }
                        else
                        {
                            storeIcon = heroFallback;
                        }

                        candidates.Add(new GlobalSearchResultItem
                        {
                            Type = "store",
                            Name = sItem.Name,
                            Title = sItem.Name,
                            Subtitle = $"{sItem.TypeName} ({sItem.GoldCost} Gold)",
                            Url = $"/store?search={Uri.EscapeDataString(sItem.Name)}",
                            Badge = "STORE",
                            IconUrl = storeIcon,
                            Icon = storeIcon,
                            HeroIcon = heroFallback,
                            Score = score
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GlobalSearch] Failed scanning store items for query '{CleanQuery}'", cleanQuery);
            }
        }

        // 5. Player Accounts Search
        if (!isClanBracketQuery)
        try
        {
            IReadOnlyList<AccountDto> accounts =
                await _playerStatsClient.SearchAccountsAsync(cleanQuery, 1, 20, cancellationToken);
            foreach (AccountDto a in accounts)
            {
                int score = ComputePlayerScore(a, cleanQuery, isHandleQuery);
                if (score > 0)
                {
                    string avatarUrl;
                    if (!string.IsNullOrEmpty(a.User?.DiscordId) && !string.IsNullOrEmpty(a.User?.DiscordAvatar))
                    {
                        avatarUrl = a.User.DiscordAvatar.StartsWith("a_", StringComparison.OrdinalIgnoreCase)
                            ? $"https://cdn.discordapp.com/avatars/{a.User.DiscordId}/{a.User.DiscordAvatar}.gif"
                            : $"https://cdn.discordapp.com/avatars/{a.User.DiscordId}/{a.User.DiscordAvatar}.png?size=256";
                    }
                    else if (!string.IsNullOrEmpty(a.User?.DiscordId))
                    {
                        ulong dId = ulong.TryParse(a.User.DiscordId, out ulong parsedId) ? parsedId : 0;
                        avatarUrl = $"https://cdn.discordapp.com/embed/avatars/{(dId >> 22) % 6}.png";
                    }
                    else
                    {
                        avatarUrl = $"/avatars/by-name/{Uri.EscapeDataString(a.Name)}";
                    }

                    candidates.Add(new GlobalSearchResultItem
                    {
                        Type = "player",
                        Name = a.Name,
                        Title = a.Name,
                        Subtitle = a.User?.DiscordUsername != null ? $"@{a.User.DiscordUsername}" : "Player",
                        Url = $"/player/{a.Name}",
                        Badge = "PLAYER",
                        IconUrl = avatarUrl,
                        Icon = avatarUrl,
                        AvatarUrl = avatarUrl,
                        Score = score
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GlobalSearch] Failed scanning players for query '{CleanQuery}'", cleanQuery);
        }

        // Unified Sorting: Highest score first, then tie-breaker weight (e.g., member count for duplicate clan tags), then shorter names, then limit to top 12
        var topResults = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.TieBreakerWeight)
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Url, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        // Convert to List<object> for backwards compatibility with existing tests
        List<object> finalResults = topResults.Cast<object>().ToList();

        stopwatch.Stop();
        _logger.LogInformation(
            "[GlobalSearch] Query '{CleanQuery}' produced {CandidateCount} candidates, returning top {ReturnCount} items in {ElapsedMs}ms. Top: [{TopItems}]",
            cleanQuery,
            candidates.Count,
            finalResults.Count,
            stopwatch.ElapsedMilliseconds,
            string.Join(", ", topResults.Take(3).Select(t => $"{t.Type}:{t.Name}({t.Score})")));

        if (enableCache)
        {
            _memoryCache.Set(cacheKey, finalResults, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(45),
                Size = 1
            });
        }

        return Json(finalResults);
    }

    private static int ComputePageScore(PortalNavigationPage page, string query)
    {
        string name = page.Name.ToLowerInvariant();
        if (name == query) return 100;
        if (name.StartsWith(query)) return 91;

        string[] words = name.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query))) return 87;

        if (page.Keywords.Any(k => k.Equals(query, StringComparison.OrdinalIgnoreCase))) return 95;
        if (page.Keywords.Any(k => k.StartsWith(query, StringComparison.OrdinalIgnoreCase))) return 86;
        if (page.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))) return 66;

        return name.Contains(query) ? 55 : 0;
    }

    private static int ComputeHeroScore(HeroDetailDto hero, string query)
    {
        string name = hero.DisplayName.ToLowerInvariant();
        string code = hero.HeroCode.ToLowerInvariant().Replace("hero_", "");

        if (name == query || code == query) return 100;
        if (name.StartsWith(query) || code.StartsWith(query)) return 95;

        string[] words = name.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query))) return 85;

        if (name.Contains(query) || code.Contains(query)) return 58;

        return 0;
    }

    private static int ComputeStoreItemScore(StaffStoreItemDto item, string query)
    {
        string name = item.Name.ToLowerInvariant();
        string code = item.Code.ToLowerInvariant();
        string hero = (item.HeroName ?? string.Empty).ToLowerInvariant();

        if (name == query || code == query) return 96;
        if (name.StartsWith(query)) return 86;

        string[] words = name.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query))) return 80;

        if (!string.IsNullOrEmpty(hero) && hero.StartsWith(query)) return 75;
        if (name.Contains(query) || code.Contains(query) || hero.Contains(query)) return 48;

        return 0;
    }

    private static int ComputePlayerScore(AccountDto account, string query, bool isHandleQuery)
    {
        string name = account.Name.ToLowerInvariant();
        string discord = (account.User?.DiscordUsername ?? string.Empty).ToLowerInvariant();

        if (isHandleQuery)
        {
            if (discord == query) return 100;
            if (discord.StartsWith(query)) return 90;
            if (discord.Contains(query)) return 70;
            if (name == query) return 85;
            return 50;
        }

        if (name == query || (!string.IsNullOrEmpty(discord) && discord == query)) return 98;
        if (name.StartsWith(query) || (!string.IsNullOrEmpty(discord) && discord.StartsWith(query))) return 88;
        if (name.Contains(query) || (!string.IsNullOrEmpty(discord) && discord.Contains(query))) return 45;

        return 40;
    }

    private const string GlobalSearchClansCacheKey = "search:global:clans_catalog_v1";

    private async Task<IReadOnlyList<NEXUS.Contracts.DTOs.ClanSummaryDto>> GetCachedClanSummariesAsync(
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(GlobalSearchClansCacheKey, out IReadOnlyList<NEXUS.Contracts.DTOs.ClanSummaryDto>? cached)
            && cached != null)
        {
            return cached;
        }

        // shortcut: caches up to 500 active clan summaries in DAWNBRINGER memory for 60s to avoid per-keystroke HTTP calls, add a dedicated server-side search endpoint when active clans exceed 500
        NEXUS.Contracts.DTOs.ClanDirectoryResultDto? directory =
            await _playerStatsClient.GetClanDirectoryAsync(page: 1, pageSize: 500, search: null, sort: "mmr", ct: cancellationToken);
        IReadOnlyList<NEXUS.Contracts.DTOs.ClanSummaryDto> items = directory?.Items ?? [];

        _memoryCache.Set(GlobalSearchClansCacheKey, items, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            Size = 1
        });

        _logger.LogInformation("[GlobalSearch] Cached {Count} clan summaries for in-memory spotlight search (TTL=60s)", items.Count);
        return items;
    }

    private static int ComputeClanScore(
        NEXUS.Contracts.DTOs.ClanSummaryDto clan,
        string query,
        bool isBracketQuery)
    {
        if (string.IsNullOrEmpty(query)) return 0;

        string tag = (clan.Tag ?? string.Empty).ToLowerInvariant();
        string name = (clan.Name ?? string.Empty).ToLowerInvariant();

        if (tag == query) return 100;
        if (name == query) return 99;
        if (tag.StartsWith(query, StringComparison.Ordinal)) return isBracketQuery ? 95 : 92;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 90;

        string[] words = name.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(query, StringComparison.Ordinal))) return 84;

        if (tag.Contains(query, StringComparison.Ordinal)) return isBracketQuery ? 78 : 66;
        if (name.Contains(query, StringComparison.Ordinal)) return 60;

        return 0;
    }
}
