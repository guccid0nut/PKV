// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Models.RequestResponse.ClientRequester;
using KONGOR.MasterServer.Services.Security;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class HeroStatsHandler(
    IMatchHistoryRepository matchHistoryRepository,
    IAccountRepository accountRepository,
    ISessionContextResolver sessionResolver,
    IHeroDefinitionService heroDefinitions,
    ILogger<HeroStatsHandler> logger,
    INexusHybridCache? hybridCache = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IClientRequestHandler
{
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private ISessionContextResolver SessionResolver { get; } = sessionResolver;
    private IHeroDefinitionService HeroDefinitions { get; } = heroDefinitions;
    private ILogger Logger { get; } = logger;
    private IServiceScopeFactory? ServiceScopeFactory { get; } = serviceScopeFactory;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? functionName = request.GetQueryOrFormValue("f");

        LogRequestForFunction(functionName ?? "NULL");

        if (string.Equals(functionName, "get_hero_stats", StringComparison.OrdinalIgnoreCase))
            return await HandleGetHeroStatsAsync(context, cancellationToken);

        if (string.Equals(functionName, "get_campaign_hero_stats", StringComparison.OrdinalIgnoreCase))
            return await HandleGetCampaignHeroStatsAsync(context, cancellationToken);

        if (string.Equals(functionName, "get_account_all_hero_stats", StringComparison.OrdinalIgnoreCase)
            || string.Equals(functionName, "get_account_mastery", StringComparison.OrdinalIgnoreCase))
            return await HandleGetAccountAllHeroStatsAsync(context, cancellationToken);

        HeroStatsResponseDTO response = new();
        LogReturningEmptyStub();
        return new PhpResult(response);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[HeroStats] Request for {FunctionName}")]
    private partial void LogRequestForFunction(string functionName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[HeroStats] Returning empty dictionary stub.")]
    private partial void LogReturningEmptyStub();

    [LoggerMessage(Level = LogLevel.Information, Message = "[HeroStats] Returning mastery info for {AccountName}")]
    private partial void LogReturningMasteryInfo(string accountName);

    private async Task<IActionResult> HandleGetHeroStatsAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string hero = request.GetFormOrQueryValue("hero") ?? string.Empty;
        string nickname = request.GetFormOrQueryValue("nickname") ?? string.Empty;

        Account? account = null;
        if (!string.IsNullOrEmpty(nickname))
            account = await AccountRepository.GetByNicknameAsync(nickname, cancellationToken);

        account ??= await SessionResolver.ResolveAccountAsync(context, cancellationToken);

        List<PlayerStatistics> playerStats = [];
        if (account != null && !string.IsNullOrEmpty(hero))
        {
            IReadOnlyList<PlayerStatistics> stats = await MatchHistoryRepository.GetPlayerHeroStatisticsAsync(
                account.ID, hero, cancellationToken);
            playerStats = stats.ToList();
        }

        Dictionary<string, object> response = BuildHeroStatsDictionary(playerStats);
        response["0"] = true;
        response["errors"] = string.Empty;
        response["success"] = "1";
        response["hero"] = hero;

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleGetCampaignHeroStatsAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string heroName = request.GetFormOrQueryValue("hero_name") ?? string.Empty;
        string nickname = request.GetFormOrQueryValue("nickname") ?? string.Empty;
        string isCasual = request.GetFormOrQueryValue("is_casual") ?? string.Empty;

        Account? account = null;
        if (!string.IsNullOrEmpty(nickname))
            account = await AccountRepository.GetByNicknameAsync(nickname, cancellationToken);

        account ??= await SessionResolver.ResolveAccountAsync(context, cancellationToken);

        List<PlayerStatistics> playerStats = [];
        if (account != null && !string.IsNullOrEmpty(heroName))
        {
            IReadOnlyList<PlayerStatistics> stats = await MatchHistoryRepository.GetPlayerHeroStatisticsAsync(
                account.ID, heroName, cancellationToken);
            playerStats = stats.ToList();
        }

        Dictionary<string, object> response = BuildCampaignHeroStatsDictionary(playerStats, isCasual == "1");
        response["0"] = true;
        response["errors"] = string.Empty;
        response["success"] = "1";
        response["hero_name"] = heroName;

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleGetAccountAllHeroStatsAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string nickname = request.GetFormOrQueryValue("nickname") ?? string.Empty;

        Account? account = null;
        if (!string.IsNullOrEmpty(nickname))
            account = await AccountRepository.GetByNicknameAsync(nickname, cancellationToken);

        account ??= await SessionResolver.ResolveAccountAsync(context, cancellationToken);
        int accountId = account?.ID ?? 0;

        if (hybridCache != null && accountId > 0)
        {
            string cacheKey = $"hero:stats:account:{accountId}";
            string? cachedPayload = await hybridCache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    if (ServiceScopeFactory != null)
                    {
                        using IServiceScope scope = ServiceScopeFactory.CreateScope();
                        IMatchHistoryRepository scopedMatchRepo = scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
                        IHeroDefinitionService scopedHeroDefs = scope.ServiceProvider.GetRequiredService<IHeroDefinitionService>();
                        Dictionary<string, object> dict = await BuildAccountAllHeroStatsDictionaryCoreAsync(account, accountId, scopedMatchRepo, scopedHeroDefs, token);
                        return PhpSerializer.Serialize(dict);
                    }

                    Dictionary<string, object> defaultDict = await BuildAccountAllHeroStatsDictionaryAsync(account, accountId, token);
                    return PhpSerializer.Serialize(defaultDict);
                },
                [$"account:{accountId}"],
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                allowStaleOnError: true,
                cancellationToken);

            if (account != null) LogReturningMasteryInfo(account.Name);
            return new PhpResult(cachedPayload, contentType: "text/plain; charset=utf-8");
        }

        Dictionary<string, object> response = await BuildAccountAllHeroStatsDictionaryAsync(account, accountId, cancellationToken);
        if (account != null) LogReturningMasteryInfo(account.Name);
        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private Task<Dictionary<string, object>> BuildAccountAllHeroStatsDictionaryAsync(Account? account, int accountId,
        CancellationToken cancellationToken)
    {
        return BuildAccountAllHeroStatsDictionaryCoreAsync(account, accountId, MatchHistoryRepository, HeroDefinitions, cancellationToken);
    }

    private async Task<Dictionary<string, object>> BuildAccountAllHeroStatsDictionaryCoreAsync(Account? account, int accountId,
        IMatchHistoryRepository matchHistoryRepo, IHeroDefinitionService heroDefs,
        CancellationToken cancellationToken)
    {
        Mastery? mastery = null;
        List<PlayerStatistics> playerStats = [];
        if (account != null)
        {
            try
            {
                mastery = account.Mastery ??
                          await matchHistoryRepo.GetMasteryByAccountIdAsync(account.ID, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                    Logger.LogWarning(ex, "[HeroStats] Masteries table lookup bypassed");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                IReadOnlyList<PlayerStatistics> stats = await matchHistoryRepo.GetAllPlayerStatisticsForAccountAsync(
                    account.ID, account.Name, cancellationToken);
                playerStats = stats.ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Warning))
                    Logger.LogWarning(ex, "[HeroStats] PlayerStatistics lookup bypassed");
            }
        }

        Dictionary<string, int> heroStatsExpMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlayerStatistics ps in playerStats)
        {
            string identifier = ps.HeroIdentifier;
            if (string.IsNullOrEmpty(identifier) && ps.HeroProductID is > 0)
                identifier = heroDefs.GetHeroIdentifier(ps.HeroProductID.Value);

            if (!string.IsNullOrEmpty(identifier))
            {
                int calcExp = ps.SecondsPlayed * 5;
                if (calcExp == 0) calcExp = ps.HeroExperience;

                heroStatsExpMap[identifier] = Math.Max(heroStatsExpMap.GetValueOrDefault(identifier, 0), calcExp);
            }
        }

        List<string> allHeroes = Heroes.AllHeroIdentifiers();
        List<object> masteryInfoList = new(allHeroes.Count);
        List<object> rankedList = new(allHeroes.Count);
        List<object> casualList = new(allHeroes.Count);
        List<object> camList = new(allHeroes.Count);
        List<object> camCsList = new(allHeroes.Count);

        foreach (string heroName in allHeroes)
        {
            uint heroId = heroDefs.GetBaseHeroId(heroName);
            int exp = mastery?.GetHeroExperienceByHeroIdentifier(heroName) ?? 0;
            if (exp == 0 && heroStatsExpMap.TryGetValue(heroName, out int statsExp)) exp = statsExp;

            int level = Mastery.GetLevelFromExperience(exp);

            string iconPath = ResolveHeroIconPath(heroName);
            string displayName = Heroes.ResolveHeroDisplayName(heroName);

            Dictionary<object, object> heroRecord = new()
            {
                // Integer positional keys for K2 engine Lua varargs (arg[1]..arg[17])
                [1] = (int)heroId,
                [2] = heroName,
                [3] = iconPath,
                [17] = exp.ToString(CultureInfo.InvariantCulture),

                // String key aliases for hashtable / direct lookup compatibility
                ["hero_id"] = (int)heroId,
                ["hero_name"] = heroName,
                ["heroname"] = heroName,
                ["disp_name"] = displayName,
                ["icon"] = iconPath,
                ["exp"] = exp,
                ["pts"] = exp,
                ["mastery_pts"] = exp,
                ["mastery_level"] = level,
                ["mastery_rank"] = level,
                ["level"] = level,
                ["rank"] = level
            };

            masteryInfoList.Add(heroRecord);

            List<PlayerStatistics> heroStatsList =
            [
                .. playerStats
                    .Where(ps => string.Equals(ps.HeroIdentifier, heroName, StringComparison.OrdinalIgnoreCase) ||
                                 (ps.HeroProductID is > 0 &&
                                  string.Equals(HeroDefinitions.GetHeroIdentifier(ps.HeroProductID.Value), heroName,
                                      StringComparison.OrdinalIgnoreCase)))
            ];

            Dictionary<string, object> heroRanked =
                BuildSinglePrefixStatsDict([.. heroStatsList.Where(s => s.RankedMatch == 1)], "rnk_ph_");
            heroRanked["cli_name"] = heroName;
            rankedList.Add(heroRanked);

            Dictionary<string, object> heroCasual =
                BuildSinglePrefixStatsDict([.. heroStatsList.Where(s => s.PublicMatch == 1)], "cs_ph_");
            heroCasual["cli_name"] = heroName;
            casualList.Add(heroCasual);

            Dictionary<string, object> heroCam =
                BuildSinglePrefixStatsDict([.. heroStatsList.Where(s => s.RankedMatch == 1)], "cam_ph_");
            heroCam["cli_name"] = heroName;
            camList.Add(heroCam);

            Dictionary<string, object> heroCamCs =
                BuildSinglePrefixStatsDict([.. heroStatsList.Where(s => s.PublicMatch == 1)], "cam_cs_ph_");
            heroCamCs["cli_name"] = heroName;
            camCsList.Add(heroCamCs);
        }

        Dictionary<string, object> allHeroStatsDict = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ranked"] = rankedList,
            ["casual"] = casualList,
            ["campaign"] = camList,
            ["campaign_casual"] = camCsList
        };

        return new Dictionary<string, object>
        {
            ["0"] = true,
            ["error_code"] = 0,
            ["account_id"] = accountId,
            ["mastery_info"] = masteryInfoList,
            ["all_hero_stats"] = allHeroStatsDict
        };
    }

    private static Dictionary<string, object> BuildSinglePrefixStatsDict(List<PlayerStatistics> stats, string prefix)
    {
        Dictionary<string, object> dict = new(StringComparer.OrdinalIgnoreCase);
        string[] statKeys =
        [
            "used", "wins", "losses", "herokills", "deaths", "heroassists",
            "teamcreepkills", "denies", "exp", "gold", "actions", "time_earning_exp"
        ];

        foreach (string key in statKeys) dict[$"{prefix}{key}"] = "0";

        int totalUsed = stats.Count;
        int totalWins = stats.Count(s => s.Win == 1);
        int totalLosses = stats.Count(s => s.Loss == 1);
        int heroKills = stats.Sum(s => s.HeroKills);
        int heroDeaths = stats.Sum(s => s.HeroDeaths);
        int heroAssists = stats.Sum(s => s.HeroAssists);
        int gold = stats.Sum(s => s.Gold);
        int exp = stats.Sum(s => s.Experience);
        int actions = stats.Sum(s => s.Actions);
        int secs = stats.Sum(s => s.SecondsPlayed);
        int timeExp = stats.Sum(s => s.TimeEarningExperience);
        int denies = stats.Sum(s => s.Denies);
        int creepKills = stats.Sum(s => s.TeamCreepKills);

        dict[$"{prefix}used"] = totalUsed.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}wins"] = totalWins.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}losses"] = totalLosses.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}herokills"] = heroKills.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}deaths"] = heroDeaths.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}heroassists"] = heroAssists.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}teamcreepkills"] = creepKills.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}denies"] = denies.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}exp"] = exp.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}gold"] = gold.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}actions"] = actions.ToString(CultureInfo.InvariantCulture);
        dict[$"{prefix}time_earning_exp"] =
            (timeExp > 0 ? timeExp : secs > 0 ? secs : 1).ToString(CultureInfo.InvariantCulture);

        return dict;
    }

    private static Dictionary<string, object> BuildHeroStatsDictionary(List<PlayerStatistics> stats)
    {
        Dictionary<string, object> dict = new(StringComparer.OrdinalIgnoreCase);

        // Prefixes: rnk_ph_ (Ranked), cs_ph_ (Casual), ph_ (Public / Total)
        string[] prefixes = ["rnk_ph_", "cs_ph_", "ph_"];
        string[] statKeys =
        [
            "used", "wins", "losses", "concedes", "concedevotes", "buybacks", "discos", "kicked",
            "pub_skill", "pub_count", "amm_solo_rating", "amm_solo_count", "amm_team_rating", "amm_team_count",
            "avg_score", "herokills", "herodmg", "heroexp", "herokillsgold", "heroassists", "deaths",
            "goldlost2death", "secs_dead", "teamcreepkills", "teamcreepdmg", "teamcreepexp", "teamcreepgold",
            "neutralcreepkills", "neutralcreepdmg", "neutralcreepexp", "neutralcreepgold", "bdmg", "bdmgexp",
            "razed", "bgold", "denies", "exp_denied", "gold", "gold_spent", "exp", "actions", "secs",
            "consumables", "wards", "time_earning_exp", "bloodlust", "doublekill", "triplekill", "quadkill",
            "annihilation", "ks3", "ks4", "ks5", "ks6", "ks7", "ks8", "ks9", "ks10", "ks15",
            "smackdown", "humiliation", "nemesis", "retribution"
        ];

        int totalUsed = stats.Count;
        int totalWins = stats.Count(s => s.Win == 1);
        int totalLosses = stats.Count(s => s.Loss == 1);
        int heroKills = stats.Sum(s => s.HeroKills);
        int heroDeaths = stats.Sum(s => s.HeroDeaths);
        int heroAssists = stats.Sum(s => s.HeroAssists);
        int heroDmg = stats.Sum(s => s.HeroDamage);
        int gold = stats.Sum(s => s.Gold);
        int goldSpent = stats.Sum(s => s.GoldSpent);
        int exp = stats.Sum(s => s.Experience);
        int actions = stats.Sum(s => s.Actions);
        int secs = stats.Sum(s => s.SecondsPlayed);
        int timeExp = stats.Sum(s => s.TimeEarningExperience);
        int wards = stats.Sum(s => s.WardsPlaced);
        int denies = stats.Sum(s => s.Denies);
        int creepKills = stats.Sum(s => s.TeamCreepKills);
        int neutralKills = stats.Sum(s => s.NeutralCreepKills);
        int bdmg = stats.Sum(s => s.BuildingDamage);
        int razed = stats.Sum(s => s.BuildingsRazed);

        foreach (string prefix in prefixes)
        {
            foreach (string key in statKeys) dict[$"{prefix}{key}"] = "0";

            dict[$"{prefix}used"] = totalUsed.ToString();
            dict[$"{prefix}wins"] = totalWins.ToString();
            dict[$"{prefix}losses"] = totalLosses.ToString();
            dict[$"{prefix}herokills"] = heroKills.ToString();
            dict[$"{prefix}deaths"] = heroDeaths.ToString();
            dict[$"{prefix}heroassists"] = heroAssists.ToString();
            dict[$"{prefix}herodmg"] = heroDmg.ToString();
            dict[$"{prefix}gold"] = gold.ToString();
            dict[$"{prefix}gold_spent"] = goldSpent.ToString();
            dict[$"{prefix}exp"] = exp.ToString();
            dict[$"{prefix}actions"] = actions.ToString();
            dict[$"{prefix}secs"] = (secs > 0 ? secs : 1).ToString();
            dict[$"{prefix}time_earning_exp"] = (timeExp > 0 ? timeExp : secs > 0 ? secs : 1).ToString();
            dict[$"{prefix}wards"] = wards.ToString();
            dict[$"{prefix}denies"] = denies.ToString();
            dict[$"{prefix}teamcreepkills"] = creepKills.ToString();
            dict[$"{prefix}neutralcreepkills"] = neutralKills.ToString();
            dict[$"{prefix}bdmg"] = bdmg.ToString();
            dict[$"{prefix}razed"] = razed.ToString();
        }

        return dict;
    }

    private static Dictionary<string, object> BuildCampaignHeroStatsDictionary(List<PlayerStatistics> stats,
        bool isCasual)
    {
        Dictionary<string, object> dict = new(StringComparer.OrdinalIgnoreCase);
        string prefix = isCasual ? "cam_cs_ph_" : "cam_ph_";

        string[] statKeys =
        [
            "used", "wins", "losses", "concedes", "concedevotes", "buybacks", "discos", "kicked",
            "pub_skill", "pub_count", "amm_solo_rating", "amm_solo_count", "amm_team_rating", "amm_team_count",
            "avg_score", "herokills", "herodmg", "heroexp", "herokillsgold", "heroassists", "deaths",
            "goldlost2death", "secs_dead", "teamcreepkills", "teamcreepdmg", "teamcreepexp", "teamcreepgold",
            "neutralcreepkills", "neutralcreepdmg", "neutralcreepexp", "neutralcreepgold", "bdmg", "bdmgexp",
            "razed", "bgold", "denies", "exp_denied", "gold", "gold_spent", "exp", "actions", "secs",
            "consumables", "wards", "time_earning_exp", "bloodlust", "doublekill", "triplekill", "quadkill",
            "annihilation", "ks3", "ks4", "ks5", "ks6", "ks7", "ks8", "ks9", "ks10", "ks15",
            "smackdown", "humiliation", "nemesis", "retribution"
        ];

        foreach (string key in statKeys) dict[$"{prefix}{key}"] = "0";

        int totalUsed = stats.Count;
        int totalWins = stats.Count(s => s.Win == 1);
        int totalLosses = stats.Count(s => s.Loss == 1);
        int heroKills = stats.Sum(s => s.HeroKills);
        int heroDeaths = stats.Sum(s => s.HeroDeaths);
        int heroAssists = stats.Sum(s => s.HeroAssists);
        int heroDmg = stats.Sum(s => s.HeroDamage);
        int gold = stats.Sum(s => s.Gold);
        int goldSpent = stats.Sum(s => s.GoldSpent);
        int exp = stats.Sum(s => s.Experience);
        int actions = stats.Sum(s => s.Actions);
        int secs = stats.Sum(s => s.SecondsPlayed);
        int timeExp = stats.Sum(s => s.TimeEarningExperience);
        int wards = stats.Sum(s => s.WardsPlaced);
        int denies = stats.Sum(s => s.Denies);
        int creepKills = stats.Sum(s => s.TeamCreepKills);
        int neutralKills = stats.Sum(s => s.NeutralCreepKills);
        int bdmg = stats.Sum(s => s.BuildingDamage);
        int razed = stats.Sum(s => s.BuildingsRazed);

        dict[$"{prefix}used"] = totalUsed.ToString();
        dict[$"{prefix}wins"] = totalWins.ToString();
        dict[$"{prefix}losses"] = totalLosses.ToString();
        dict[$"{prefix}herokills"] = heroKills.ToString();
        dict[$"{prefix}deaths"] = heroDeaths.ToString();
        dict[$"{prefix}heroassists"] = heroAssists.ToString();
        dict[$"{prefix}herodmg"] = heroDmg.ToString();
        dict[$"{prefix}gold"] = gold.ToString();
        dict[$"{prefix}gold_spent"] = goldSpent.ToString();
        dict[$"{prefix}exp"] = exp.ToString();
        dict[$"{prefix}actions"] = actions.ToString();
        dict[$"{prefix}secs"] = (secs > 0 ? secs : 1).ToString();
        dict[$"{prefix}time_earning_exp"] = (timeExp > 0 ? timeExp : secs > 0 ? secs : 1).ToString();
        dict[$"{prefix}wards"] = wards.ToString();
        dict[$"{prefix}denies"] = denies.ToString();
        dict[$"{prefix}teamcreepkills"] = creepKills.ToString();
        dict[$"{prefix}neutralcreepkills"] = neutralKills.ToString();
        dict[$"{prefix}bdmg"] = bdmg.ToString();
        dict[$"{prefix}razed"] = razed.ToString();

        return dict;
    }

    private static string ResolveHeroIconPath(string heroName)
    {
        string clean = heroName.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? heroName[5..]
            : heroName;

        return clean.ToLowerInvariant() switch
        {
            "accursed" => "/heroes/accursed/icon.tga",
            "adrenaline" => "/heroes/adrenaline/icon.tga",
            "aluna" => "/heroes/aluna/icon.tga",
            "andromeda" => "/heroes/andromeda/icon.tga",
            "apex" => "/heroes/apex/icon.tga",
            "arachna" => "/heroes/arachna/icon.tga",
            "armadon" => "/heroes/armadon/icon.tga",
            "artesia" => "/heroes/artesia/icon.tga",
            "artillery" => "/heroes/artillery/alt/icon.tga",
            "babayaga" => "/heroes/babayaga/icon.tga",
            "behemoth" => "/heroes/behemoth/icon.tga",
            "bephelgor" => "/heroes/bephelgor/icon.tga",
            "berzerker" => "/heroes/berzerker/icon.tga",
            "blitz" => "/heroes/blitz/icon.tga",
            "bombardier" or "bomb" => "/heroes/bomb/icon.tga",
            "bubbles" => "/heroes/bubbles/icon.tga",
            "bushwack" => "/heroes/bushwack/bushwack_hd/icon.tga",
            "calamity" => "/heroes/calamity/icon.tga",
            "chi" or "chipper" => "/heroes/chipper/icon.tga",
            "chronos" => "/heroes/chronos/hd_chronos/icon.tga",
            "circe" => "/heroes/circe/icon.tga",
            "corrupteddisciple" or "corrupted_disciple" => "/heroes/corrupted_disciple/icon.tga",
            "cthulhuphant" => "/heroes/cthulhuphant/icon.tga",
            "dampeer" => "/heroes/dampeer/icon.tga",
            "deadlift" => "/heroes/deadlift/icon.tga",
            "deadwood" => "/heroes/deadwood/icon.tga",
            "defiler" => "/heroes/defiler/icon.tga",
            "devourer" => "/heroes/devourer/icon.tga",
            "diseasedrider" => "/heroes/diseasedrider/icon.tga",
            "doctorrepulsor" or "doctor_repulsor" => "/heroes/doctor_repulsor/icon.tga",
            "dreadknight" or "salforis" or "lordsalforis" => "/heroes/dreadknight/icon.tga",
            "drunkenmaster" => "/heroes/drunkenmaster/icon.tga",
            "dwarfmagi" or "dwarf_magi" => "/heroes/dwarf_magi/icon.tga",
            "ebulus" or "slither" => "/heroes/ebulus/icon.tga",
            "electrician" => "/heroes/electrician/icons/hero.tga",
            "ellonia" => "/heroes/ellonia/icon.tga",
            "emeraldwarden" or "emerald_warden" => "/heroes/emerald_warden/icon.tga",
            "empath" => "/heroes/empath/icon.tga",
            "engineer" => "/heroes/engineer/icon.tga",
            "fade" or "nighthound" => "/heroes/fade/icon.tga",
            "fairy" or "nymphora" => "/heroes/fairy/icon.tga",
            "flamedragon" => "/heroes/flamedragon/icon.tga",
            "flintbeastwood" or "flint_beastwood" => "/heroes/flint_beastwood/icon.tga",
            "flux" => "/heroes/flux/icon.tga",
            "forsakenarcher" or "forsaken_archer" => "/heroes/forsaken_archer/icon.tga",
            "frosty" or "glacius" => "/heroes/frosty/icon.tga",
            "gauntlet" => "/heroes/gauntlet/icon.tga",
            "gemini" => "/heroes/gemini/icon.tga",
            "geomancer" => "/heroes/geomancer/hd_geomancer/icon.tga",
            "gladiator" => "/heroes/gladiator/icon.tga",
            "goldenveil" => "/heroes/goldenveil/icon.tga",
            "grinex" => "/heroes/grinex/icon.tga",
            "gunblade" => "/heroes/gunblade/icon.tga",
            "hammerstorm" => "/heroes/hammerstorm/icon.tga",
            "hantumon" => "/heroes/hantumon/icon.tga",
            "hellbringer" or "helldemon" => "/heroes/hellbringer/icon.tga",
            "hiro" or "swiftblade" => "/heroes/hiro/icon.tga",
            "hunter" or "bloodhunter" => "/heroes/hunter/icon.tga",
            "hydromancer" => "/heroes/hydromancer/icon.tga",
            "ichor" => "/heroes/ichor/icon.tga",
            "javaras" or "magebane" or "magebringer" => "/heroes/javaras/icon.tga",
            "jereziah" or "jeraziah" => "/heroes/jereziah/icon.tga",
            "kane" => "/heroes/kane/icon.tga",
            "kenisis" or "kinesis" => "/heroes/kenisis/icon.tga",
            "kingklout" or "king_klout" => "/heroes/king_klout/icon.tga",
            "klanx" => "/heroes/klanx/icon.tga",
            "kraken" => "/heroes/kraken/icon.tga",
            "krixi" or "moonqueen" => "/heroes/krixi/icon.tga",
            "kunas" or "thunderbringer" => "/heroes/kunas/icon.tga",
            "legionnaire" => "/heroes/legionnaire/icon.tga",
            "lodestone" => "/heroes/lodestone/icon.tga",
            "magmar" => "/heroes/magmar/icon.tga",
            "maliken" => "/heroes/maliken/icon.tga",
            "martyr" => "/heroes/martyr/icon.tga",
            "masterofarms" or "master_of_arms" => "/heroes/master_of_arms/icon.tga",
            "midas" => "/heroes/midas/icon.tga",
            "mimix" => "/heroes/mimix/icon.tga",
            "moira" => "/heroes/moira/icon.tga",
            "monarch" => "/heroes/monarch/icon.tga",
            "monkeyking" or "monkey_king" => "/heroes/monkey_king/hd_monkey_king/icon.tga",
            "moraxus" => "/heroes/moraxus/icon.tga",
            "mumra" => "/heroes/mumra/icon.tga",
            "nitro" => "/heroes/nitro/icon.tga",
            "nomad" => "/heroes/nomad/icon.tga",
            "oogie" => "/heroes/oogie/icon/icon.tga",
            "ophelia" => "/heroes/ophelia/icon.tga",
            "panda" or "pandamonium" => "/heroes/panda/icon.tga",
            "parallax" => "/heroes/parallax/icon.tga",
            "parasite" => "/heroes/parasite/icon.tga",
            "pearl" => "/heroes/pearl/icon.tga",
            "pestilence" => "/heroes/pestilence/icon.tga",
            "plant" or "bramble" => "/heroes/plant/icon.tga",
            "pollywogpriest" or "voodoo" => "/heroes/pollywogpriest/icon.tga",
            "predator" => "/heroes/predator/icon.tga",
            "prisoner" => "/heroes/prisoner/icon.tga",
            "prophet" => "/heroes/prophet/icon.tga",
            "puppetmaster" => "/heroes/puppetmaster/icon.tga",
            "pyromancer" => "/heroes/pyromancer/icon.tga",
            "ra" or "amunra" => "/heroes/ra/icon.tga",
            "rally" => "/heroes/rally/icon.tga",
            "rampage" => "/heroes/rampage/icon.tga",
            "ravenor" => "/heroes/ravenor/icon.tga",
            "revenant" => "/heroes/revenant/icon.tga",
            "rhapsody" => "/heroes/rhapsody/icon.tga",
            "riftmage" or "riftwalker" => "/heroes/riftmage/icon.tga",
            "riptide" => "/heroes/riptide/icon.tga",
            "rocky" or "pebbles" => "/heroes/rocky/icon.tga",
            "salomon" => "/heroes/salomon/icon.tga",
            "sandwraith" or "sand_wraith" => "/heroes/sand_wraith/hd_sandwraith2/icon.tga",
            "sapphire" => "/heroes/sapphire/icon.tga",
            "scar" or "madman" => "/heroes/scar/icon.tga",
            "scout" => "/heroes/scout/icon.tga",
            "shadowblade" => "/heroes/shadowblade/icon.tga",
            "shaman" or "dementedshaman" => "/heroes/shaman/icon.tga",
            "shellshock" => "/heroes/shellshock/icon.tga",
            "silhouette" => "/heroes/silhouette/icon.tga",
            "sirbenzington" or "sir_benzington" => "/heroes/sir_benzington/icon.tga",
            "skrap" => "/heroes/skrap/icon.tga",
            "solstice" => "/heroes/solstice/icon.tga",
            "soulstealer" => "/heroes/soulstealer/icon.tga",
            "succubis" or "succubus" => "/heroes/succubis/icon.tga",
            "taint" or "gravekeeper" => "/heroes/taint/icon.tga",
            "tarot" => "/heroes/tarot/hd_tarot/icon.tga",
            "tempest" => "/heroes/tempest/icon.tga",
            "treant" or "keeper" or "keeperoftheforest" => "/heroes/treant/icon.tga",
            "tremble" => "/heroes/tremble/hd_tremble/icon.tga",
            "tundra" => "/heroes/tundra/icon.tga",
            "valkyrie" => "/heroes/valkyrie/icon.tga",
            "vanya" or "darklady" or "thedarklady" => "/heroes/vanya/icon.tga",
            "vindicator" => "/heroes/vindicator/icon.tga",
            "warchief" => "/heroes/warchief/icon.tga",
            "witchslayer" or "witch_slayer" => "/heroes/witch_slayer/icon.tga",
            "wolfman" or "warbeast" => "/heroes/wolfman/icon.tga",
            "xalynx" or "torturer" => "/heroes/xalynx/icon.tga",
            "yogi" or "wildsoul" => "/heroes/yogi/icons/hero.tga",
            "zephyr" => "/heroes/zephyr/icon.tga",
            _ => $"/heroes/{clean.ToLowerInvariant()}/icon.tga"
        };
    }
}
