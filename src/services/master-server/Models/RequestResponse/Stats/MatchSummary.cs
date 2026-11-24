// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

using Kongor.Core.Configuration;

public partial class MatchSummary(
    MatchStatistics matchStatistics,
    List<PlayerStatistics> playerStatistics,
    MatchStartData? matchStartData)
{
    private static readonly object ReplayDirLock = new();
    public static readonly List<string> ConfiguredReplayDirectories = [];

    /// <summary>
    ///     The unique identifier for the match.
    /// </summary>
    [PHPProperty("match_id")]
    public int MatchID { get; init; } = matchStatistics.MatchID;

    /// <summary>
    ///     The server ID where the match was hosted.
    /// </summary>
    [PHPProperty("server_id")]
    public int ServerID { get; init; } = matchStatistics.ServerID;

    /// <summary>
    ///     The server name where the match was hosted.
    /// </summary>
    [PHPProperty("name")]
    public string ServerName { get; init; } = ResolveServerName(matchStatistics, matchStartData);

    /// <summary>
    ///     The map on which the match was played (e.g. "caldavar", "midwars", "grimms_crossing").
    /// </summary>
    [PHPProperty("map")]
    public string Map { get; init; } = matchStatistics.Map;

    /// <summary>
    ///     The version of the map used in the match.
    /// </summary>
    [PHPProperty("map_version")]
    public string MapVersion { get; init; } = matchStatistics.MapVersion;

    /// <summary>
    ///     The duration of the match in seconds.
    /// </summary>
    [PHPProperty("time_played")]
    public string TimePlayed { get; init; } = matchStatistics.TimePlayed.ToString();

    /// <summary>
    ///     The host where the match replay file is stored.
    ///     This is typically "localhost" in development environments, or "kongor.net" in production environments.
    /// </summary>
    [PHPProperty("file_host")]
    public string FileHost { get; init; } = Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS") != null
        ? Uri.TryCreate(Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS"), UriKind.Absolute, out Uri? uri)
            ? uri.Authority
            : Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS")!
        : Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY") ?? "localhost";

    /// <summary>
    ///     The size of the match replay file in bytes.
    /// </summary>
    [PHPProperty("file_size")]
    public int FileSize { get; init; } = ResolveFileSize(matchStatistics);

    /// <summary>
    ///     The filename of the match replay file.
    /// </summary>
    [PHPProperty("file_name")]
    public string FileName { get; init; } = NormalizeReplayFileName(matchStatistics);

    /// <summary>
    ///     The connection state or match state code.
    /// </summary>
    [PHPProperty("c_state")]
    public string ConnectionState { get; init; } = matchStatistics.ConnectionState.ToString();

    /// <summary>
    ///     The game client version used for the match.
    /// </summary>
    [PHPProperty("version")]
    public string Version { get; init; } = matchStatistics.Version;

    /// <summary>
    ///     The average Player Skill Rating (PSR) of all players in the match.
    /// </summary>
    [PHPProperty("avgpsr")]
    public string AveragePSR { get; init; } = matchStatistics.AveragePSR.ToString();

    /// <summary>
    ///     The match date, originally formatted as "M/D/YYYY" (e.g. "3/15/2024").
    /// </summary>
    [PHPProperty("date")]
    public string Date { get; init; } = TimeProvider.System.GetUtcNow().ToString("dd/MM/yyyy");

    /// <summary>
    ///     The match time, originally formatted in 12-hour format with AM/PM (e.g. "2:30:45 PM").
    /// </summary>
    [PHPProperty("time")]
    public string Time { get; init; } = TimeProvider.System.GetUtcNow().ToString("HH:mm:ss");

    /// <summary>
    ///     The match name.
    /// </summary>
    [PHPProperty("mname")]
    public string MatchName { get; init; } = matchStartData?.MatchName ?? "Match";

    /// <summary>
    ///     The arranged match type that categorises how the match was created.
    ///     <code>
    ///         00 -> Public Match
    ///         01 -> Ranked Normal/Casual Matchmaking
    ///         02 -> Scheduled Tournament Match
    ///         03 -> Unscheduled League Match
    ///         04 -> MidWars Matchmaking
    ///         05 -> Bot Co-Op Matchmaking
    ///         06 -> Unranked Normal/Casual Matchmaking
    ///         07 -> RiftWars Matchmaking
    ///         08 -> Public Pre-Lobby
    ///         09 -> Custom Map Matchmaking
    ///         10 -> Ranked Season Normal/Casual Matchmaking
    ///     </code>
    /// </summary>
    [PHPProperty("class")]
    public int Class { get; init; } = matchStartData?.MatchType ?? 0;

    /// <summary>
    ///     Whether the match was private (1) or public (0).
    /// </summary>
    [PHPProperty("private")]
    public string Private { get; init; } = IsPrivateMatch(playerStatistics).ToString();

    /// <summary>
    ///     Normal Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("nm")]
    public string NormalMode { get; init; } = matchStartData?.MatchMode is "nm" ? "1" : "0";

    /// <summary>
    ///     Single Draft Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("sd")]
    public string SingleDraft { get; init; } = matchStartData?.MatchMode is "sd" ? "1" : "0";

    /// <summary>
    ///     Random Draft Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("rd")]
    public string RandomDraft { get; init; } = matchStartData?.MatchMode is "rd" ? "1" : "0";

    /// <summary>
    ///     Deathmatch Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("dm")]
    public string Deathmatch { get; init; } = matchStartData?.MatchMode is "dm" ? "1" : "0";

    /// <summary>
    ///     Banning Draft Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("bd")]
    public string BanningDraft { get; init; } = matchStartData?.MatchMode is "bd" ? "1" : "0";

    /// <summary>
    ///     Banning Pick Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("bp")]
    public string BanningPick { get; init; } = matchStartData?.MatchMode is "bp" ? "1" : "0";

    /// <summary>
    ///     All Random Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("ar")]
    public string AllRandom { get; init; } = matchStartData?.MatchMode is "ar" ? "1" : "0";

    /// <summary>
    ///     Captains Draft Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("cd")]
    public string CaptainsDraft { get; init; } = matchStartData?.MatchMode is "cd" ? "1" : "0";

    /// <summary>
    ///     Captains Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("cm")]
    public string CaptainsMode { get; init; } = matchStartData?.MatchMode is "cm" ? "1" : "0";

    /// <summary>
    ///     Lock Pick Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("lp")]
    public string LockPick { get; init; } = matchStartData?.MatchMode is "lp" ? "1" : "0";

    /// <summary>
    ///     Blind Ban Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("bb")]
    public string BlindBan { get; init; } = matchStartData?.MatchMode is "bb" ? "1" : "0";

    /// <summary>
    ///     Bot Match Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("bm")]
    public string BotMatch { get; init; } = matchStartData?.MatchMode is "bm" ? "1" : "0";

    /// <summary>
    ///     Kros (ability draft) Mode flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("km")]
    public string KrosMode { get; init; } = matchStartData?.MatchMode is "km" ? "1" : "0";

    /// <summary>
    ///     Whether the match is part of an organized league system.
    ///     <code>
    ///         0 -> Regular Match (public, matchmaking, tournament, etc.)
    ///         1 -> League Match (organized competitive league play)
    ///     </code>
    ///     <remark>
    ///         League matches belong to a dedicated competitive league structure with player rosters, seasonal standings, and
    ///         separate win/loss tracking.
    ///     </remark>
    /// </summary>
    [PHPProperty("league")]
    public string League { get; init; } = (matchStartData?.League ?? 0).ToString();

    /// <summary>
    ///     The maximum number of players allowed in the match (typically 2, 6, or 10).
    /// </summary>
    [PHPProperty("max_players")]
    public string MaximumPlayersCount { get; init; } = (matchStartData?.MaximumPlayersCount ?? 10).ToString();

    /// <summary>
    ///     Deprecated skill-based server filter that was used for matchmaking.
    ///     <code>
    ///         0 -> Noobs Only
    ///         1 -> Noobs Allowed
    ///         2 -> Professionals
    ///     </code>
    ///     This feature is no longer active and the field has no functional purpose.
    /// </summary>
    [PHPProperty("tier")]
    public string Tier { get; init; } = (matchStartData?.Tier ?? 0).ToString();

    /// <summary>
    ///     No Repick option flag (1 = repicking disabled, 0 = repicking allowed).
    /// </summary>
    [PHPProperty("no_repick")]
    public string NoHeroRepick { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoHeroRepick) == true ? "1" : "0";

    /// <summary>
    ///     No Agility Heroes option flag (1 = agility heroes banned, 0 = allowed).
    /// </summary>
    [PHPProperty("no_agi")]
    public string NoAgilityHeroes { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoAgilityHeroes) == true ? "1" : "0";

    /// <summary>
    ///     Drop Items On Death option flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("drp_itm")]
    public string DropItems { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.DropItems) == true ? "1" : "0";

    /// <summary>
    ///     No Respawn Timer option flag (1 = picking timer disabled, 0 = timer enabled).
    /// </summary>
    [PHPProperty("no_timer")]
    public string NoRespawnTimer { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoRespawnTimer) == true ? "1" : "0";

    /// <summary>
    ///     Reverse Hero Selection option flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("rev_hs")]
    public string ReverseHeroSelection { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.ReverseHeroSelection) == true ? "1" : "0";

    /// <summary>
    ///     No Swap option flag (1 = hero swapping disabled, 0 = swapping allowed).
    /// </summary>
    [PHPProperty("no_swap")]
    public string NoHeroSwap { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoHeroSwap) == true ? "1" : "0";

    /// <summary>
    ///     No Intelligence Heroes option flag (1 = intelligence heroes banned, 0 = allowed).
    /// </summary>
    [PHPProperty("no_int")]
    public string NoIntelligenceHeroes { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoIntelligenceHeroes) == true ? "1" : "0";

    /// <summary>
    ///     Alternate Picking option flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("alt_pick")]
    public string AlternateHeroPicking { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.AlternateHeroPicking) == true ? "1" : "0";

    /// <summary>
    ///     Ban Phase option flag (1 = ban phase enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("veto")]
    public string BanPhase { get; init; } = matchStartData?.Options.HasFlag(MatchOptions.BanPhase) == true ? "1" : "0";

    /// <summary>
    ///     Shuffle Teams option flag (1 = shuffle teams enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("shuf")]
    public string ShuffleTeams { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.ShuffleTeams) == true ? "1" : "0";

    /// <summary>
    ///     No Strength Heroes option flag (1 = strength heroes banned, 0 = allowed).
    /// </summary>
    [PHPProperty("no_str")]
    public string NoStrengthHeroes { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoStrengthHeroes) == true ? "1" : "0";

    /// <summary>
    ///     No Power-Ups option flag (1 = power-ups/runes disabled, 0 = enabled).
    /// </summary>
    [PHPProperty("no_pups")]
    public string NoPowerUps { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoPowerUps) == true ? "1" : "0";

    /// <summary>
    ///     Duplicate Heroes option flag (1 = duplicate heroes allowed, 0 = each hero unique).
    /// </summary>
    [PHPProperty("dup_h")]
    public string DuplicateHeroes { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.DuplicateHeroes) == true ? "1" : "0";

    /// <summary>
    ///     All Pick Mode option flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("ap")]
    public string AllPick { get; init; } = matchStartData?.Options.HasFlag(MatchOptions.AllPick) == true ? "1" : "0";

    /// <summary>
    ///     Balanced Random Mode option flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("br")]
    public string BalancedRandom { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.BalancedRandom) == true ? "1" : "0";

    /// <summary>
    ///     Easy Mode option flag (1 = easy mode enabled, 0 = normal difficulty).
    /// </summary>
    [PHPProperty("em")]
    public string EasyMode { get; init; } = matchStartData?.Options.HasFlag(MatchOptions.EasyMode) == true ? "1" : "0";

    /// <summary>
    ///     Casual Mode option flag (1 = casual mode enabled, 0 = normal mode).
    /// </summary>
    [PHPProperty("cas")]
    public string CasualMode { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.CasualMode) == true ? "1" : "0";

    /// <summary>
    ///     Reverse Selection option flag (1 = enabled, 0 = disabled).
    /// </summary>
    [PHPProperty("rs")]
    public string ReverseSelection { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.ReverseSelection) == true ? "1" : "0";

    /// <summary>
    ///     No Leaver option flag (1 = no leaver penalty applied, 0 = leaver penalties enabled).
    /// </summary>
    [PHPProperty("nl")]
    public string NoLeavers { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoLeavers) == true ? "1" : "0";

    /// <summary>
    ///     Official Match flag (1 = official tournament match, 0 = unofficial).
    /// </summary>
    [PHPProperty("officl")]
    public string Official { get; init; } = matchStartData?.Options.HasFlag(MatchOptions.Official) == true ? "1" : "0";

    /// <summary>
    ///     No Statistics option flag (1 = match stats not recorded, 0 = stats recorded).
    /// </summary>
    [PHPProperty("no_stats")]
    public string NoStatistics { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.NoStatistics) == true ? "1" : "0";

    /// <summary>
    ///     Auto Balanced option flag (1 = teams automatically balanced, 0 = manual teams).
    /// </summary>
    [PHPProperty("ab")]
    public string AutoBalanced { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.AutoBalanced) == true ? "1" : "0";

    /// <summary>
    ///     Hardcore Mode option flag (1 = hardcore difficulty enabled, 0 = normal).
    /// </summary>
    [PHPProperty("hardcore")]
    public string Hardcore { get; init; } = matchStartData?.Options.HasFlag(MatchOptions.Hardcore) == true ? "1" : "0";

    /// <summary>
    ///     Development Heroes option flag (1 = development/unreleased heroes allowed, 0 = only released heroes).
    /// </summary>
    [PHPProperty("dev_heroes")]
    public string DevelopmentHeroes { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.DevelopmentHeroes) == true ? "1" : "0";

    /// <summary>
    ///     Verified Only option flag (1 = only verified accounts allowed, 0 = all accounts allowed).
    /// </summary>
    [PHPProperty("verified_only")]
    public string VerifiedOnly { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.VerifiedOnly) == true ? "1" : "0";

    /// <summary>
    ///     Gated option flag (1 = gated/restricted match, 0 = open match).
    /// </summary>
    [PHPProperty("gated")]
    public string Gated { get; init; } = matchStartData?.Options.HasFlag(MatchOptions.Gated) == true ? "1" : "0";

    /// <summary>
    ///     Blitz Mode option flag (1 = rapid fire mode enabled, 0 = normal ability cooldowns).
    /// </summary>
    [PHPProperty("rapidfire")]
    public string BlitzMode { get; init; } =
        matchStartData?.Options.HasFlag(MatchOptions.BlitzMode) == true ? "1" : "0";

    /// <summary>
    ///     The UNIX timestamp (in seconds) when the match started.
    /// </summary>
    [PHPProperty("timestamp")]
    public string Timestamp { get; init; } = Convert
        .ToInt32(Math.Min(matchStatistics.TimestampRecorded.ToUnixTimeSeconds(), Convert.ToInt64(int.MaxValue)))
        .ToString();

    /// <summary>
    ///     The URL for the match replay file.
    /// </summary>
    [PHPProperty("url")]
    public string URL => !string.IsNullOrEmpty(matchStatistics.ReplayUrl)
        ? matchStatistics.ReplayUrl
        : $"http://{FileHost}/replays/M{MatchID}.honreplay";

    /// <summary>
    ///     The size of the match replay file (human-readable format or bytes as string).
    /// </summary>
    [PHPProperty("size")]
    public int Size { get; init; } = ResolveFileSize(matchStatistics);

    /// <summary>
    ///     The directory path where the replay file is stored.
    /// </summary>
    [PHPProperty("dir")]
    public string Directory => "/replays";

    /// <summary>
    ///     The S3 download URL for the match replay file.
    /// </summary>
    [PHPProperty("s3_url")]
    public string S3URL => !string.IsNullOrEmpty(matchStatistics.ReplayUrl)
        ? matchStatistics.ReplayUrl
        : $"http://{FileHost}/replays/M{MatchID}.honreplay";

    /// <summary>
    ///     The winning team ("1" for Legion, "2" for Hellbourne).
    ///     Determined by analysing player statistics from the match.
    /// </summary>
    [PHPProperty("winning_team")]
    public string WinningTeam { get; init; } = GetWinningTeam(playerStatistics).ToString();

    /// <summary>
    ///     The match mode.
    ///     <code>
    ///         "nm"  -> GAME_MODE_NORMAL
    ///         "rd"  -> GAME_MODE_RANDOM_DRAFT
    ///         "sd"  -> GAME_MODE_SINGLE_DRAFT
    ///         "dm"  -> GAME_MODE_DEATHMATCH
    ///         "bd"  -> GAME_MODE_BANNING_DRAFT
    ///         "cd"  -> GAME_MODE_CAPTAINS_DRAFT
    ///         "cm"  -> GAME_MODE_CAPTAINS_MODE
    ///         "bp"  -> GAME_MODE_BANNING_PICK
    ///         "ar"  -> GAME_MODE_ALL_RANDOM
    ///         "lp"  -> GAME_MODE_LOCKPICK
    ///         "bb"  -> GAME_MODE_BLIND_BAN
    ///         "bm"  -> GAME_MODE_BOT_MATCH
    ///         "km"  -> GAME_MODE_KROS_MODE
    ///         "fp"  -> GAME_MODE_FORCEPICK
    ///         "sp"  -> GAME_MODE_SOCCERPICK
    ///         "ss"  -> GAME_MODE_SOLO_SAME_HERO
    ///         "sm"  -> GAME_MODE_SOLO_DIFF_HERO
    ///         "cp"  -> GAME_MODE_COUNTER_PICK
    ///         "mwb" -> GAME_MODE_MIDWARS_BETA
    ///         "hb"  -> GAME_MODE_HEROBAN
    ///         "rb"  -> GAME_MODE_REBORN
    ///     </code>
    /// </summary>
    [PHPProperty("gamemode")]
    public string GameMode { get; init; } = matchStatistics.GameMode;

    /// <summary>
    ///     The account ID of the Most Valuable Player (MVP) in the match.
    /// </summary>
    [PHPProperty("mvp")]
    public string MVP { get; init; } = (matchStatistics.MVPAccountID ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Annihilations" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_mann")]
    public string AwardMostAnnihilations { get; init; } = (matchStatistics.AwardMostAnnihilations ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Quad Kills" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_mqk")]
    public string AwardMostQuadKills { get; init; } = (matchStatistics.AwardMostQuadKills ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Longest Killing Spree" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_lgks")]
    public string AwardLongestKillingSpree { get; init; } =
        GetLongestKillingSpreeAwardRecipientID(playerStatistics).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Smackdowns" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_msd")]
    public string AwardMostSmackdowns { get; init; } = (matchStatistics.AwardMostSmackdowns ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Kills" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_mkill")]
    public string AwardMostKills { get; init; } = (matchStatistics.AwardMostKills ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Assists" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_masst")]
    public string AwardMostAssists { get; init; } = (matchStatistics.AwardMostAssists ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Least Deaths" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_ledth")]
    public string AwardLeastDeaths { get; init; } = (matchStatistics.AwardLeastDeaths ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Building Damage" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_mbdmg")]
    public string AwardMostBuildingDamage { get; init; } = (matchStatistics.AwardMostBuildingDamage ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Wards" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_mwk")]
    public string AwardMostWards { get; init; } = GetMostWardsAwardRecipientID(playerStatistics).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Most Hero Damage Dealt" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_mhdd")]
    public string AwardMostHeroDamageDealt { get; init; } = (matchStatistics.AwardMostHeroDamageDealt ?? -1).ToString();

    /// <summary>
    ///     The account ID of the player who earned the "Highest Creep Score" award, or "-1" if no player earned this award.
    /// </summary>
    [PHPProperty("awd_hcs")]
    public string AwardHighestCreepScore { get; init; } = (matchStatistics.AwardHighestCreepScore ?? -1).ToString();

    /// <summary>
    ///     Selected upgrades string for custom account icon, chat symbol, and name color.
    /// </summary>
    [PHPProperty("selected_upgrades")]
    public string SelectedUpgrades { get; init; } = "";

    private static string NormalizeReplayFileName(MatchStatistics matchStatistics)
    {
        if (!string.IsNullOrWhiteSpace(matchStatistics.FileName) &&
            matchStatistics.FileName.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase) &&
            matchStatistics.FileName.StartsWith("M", StringComparison.OrdinalIgnoreCase))
            return matchStatistics.FileName;

        return $"M{matchStatistics.MatchID}.honreplay";
    }

    private static int GetMostWardsAwardRecipientID(List<PlayerStatistics> playerStatistics)
    {
        if (playerStatistics.Where(player => player.WardsPlaced > 0).Any())
            return playerStatistics.OrderByDescending(player => player.WardsPlaced)
                .ThenByDescending(player => player.Experience).First().ID;

        return -1;
    }

    private static int GetLongestKillingSpreeAwardRecipientID(List<PlayerStatistics> playerStatistics)
    {
        if (playerStatistics.Where(player => player.KillStreak15 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak15)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak10 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak10)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak09 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak09)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak08 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak08)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak07 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak07)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak06 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak06)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak05 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak05)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak04 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak04)
                .ThenByDescending(player => player.Experience).First().ID;

        if (playerStatistics.Where(player => player.KillStreak03 > 0).Any())
            return playerStatistics.OrderByDescending(player => player.KillStreak03)
                .ThenByDescending(player => player.Experience).First().ID;

        return -1;
    }

    private static int GetWinningTeam(List<PlayerStatistics>? playerStatistics)
    {
        if (playerStatistics is null) return 0;

        List<int> winningTeams =
        [
            .. playerStatistics
                .Where(player => player is { Loss: 0, Win: 1 })
                .Select(player => player.Team)
                .Distinct()
        ];

        return winningTeams.Count == 1 ? winningTeams[0] : 0;
    }

    private static int IsPrivateMatch(List<PlayerStatistics> playerStatistics)
    {
        List<PlayerStatistics> publicStatuses = [.. playerStatistics.DistinctBy(player => player.PublicMatch)];

        // If all players have the same public match status, return it. Otherwise default to Public (0)
        return publicStatuses.Count == 1 ? publicStatuses.Single().PublicMatch is 0 ? 1 : 0 : 0;
    }

    private static string ResolveServerName(MatchStatistics matchStatistics, MatchStartData? matchStartData)
    {
        if (matchStartData != null && !string.IsNullOrWhiteSpace(matchStartData.ServerName) &&
            matchStartData.ServerName != "Unknown")
            return matchStartData.ServerName;

        if (!string.IsNullOrWhiteSpace(matchStatistics.HostAccountName) && matchStatistics.HostAccountName != "Unknown")
            return matchStatistics.HostAccountName;

        if (!string.IsNullOrWhiteSpace(matchStatistics.FileName) && matchStatistics.FileName != "Unknown")
            return matchStatistics.FileName;

        return matchStatistics.ServerID > 0 ? $"NEXUS Match Server #{matchStatistics.ServerID}" : "NEXUS Local Server";
    }

    public static void SetReplayDirectories(params string?[] paths)
    {
        lock (ReplayDirLock)
        {
            ConfiguredReplayDirectories.Clear();
            foreach (string? p in paths)
                if (!string.IsNullOrWhiteSpace(p) &&
                    !ConfiguredReplayDirectories.Contains(p, StringComparer.OrdinalIgnoreCase))
                    ConfiguredReplayDirectories.Add(p);
        }
    }

    public static List<string> GetAllReplayDirectories()
    {
        List<string> dirs;
        lock (ReplayDirLock)
        {
            dirs = [.. ConfiguredReplayDirectories];
        }

        if (BundledLayoutResolver.TryResolve(out BundledLayout? layout))
        {
            string[] bundledPaths =
            [
                Path.Combine(layout.ClientDir, "userdata", "game", "replays"),
                Path.Combine(layout.ClientDir, "userdata", "Heroes of Newerth x64", "game", "replays"),
                Path.Combine(layout.ClientDir, "userdata", "Heroes of Newerth x64", "override", "replays"),
                Path.Combine(layout.ClientDir, "game", "replays"),
                Path.Combine(layout.ClientDir, "replays"),
                Path.Combine(layout.DedicatedServerDir, "userdata", "game", "replays"),
                Path.Combine(layout.DedicatedServerDir, "userdata", "Heroes of Newerth x64", "server", "replays"),
                Path.Combine(AppContext.BaseDirectory, "Resources", "Replays"),
                Path.Combine(layout.Root, "services-win-x64", "master-server", "Resources", "Replays"),
                Path.Combine(layout.Root, "userdata", "game", "replays")
            ];

            foreach (string bPath in bundledPaths)
            {
                try
                {
                    string fullPath = Path.GetFullPath(bPath);
                    if (!dirs.Contains(fullPath, StringComparer.OrdinalIgnoreCase) && System.IO.Directory.Exists(fullPath))
                        dirs.Add(fullPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MatchSummary] Ignoring invalid bundled replay path '{bPath}': {ex.Message}");
                }
            }

            return dirs;
        }

        string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tempPath = Path.GetTempPath();

        string[] standardPaths =
        [
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "games", "client", "userdata", "game", "replays"),
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "userdata", "game", "replays"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "Replays"),
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Resources", "Replays"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "replays"),
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", "replays"),
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "replays"),
            Path.Combine(AppContext.BaseDirectory, "replays"),
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "games", "client", "replays"),
            Path.Combine(System.IO.Directory.GetCurrentDirectory(), "games", "shared", "replays"),
            // 64-bit Heroes of Newerth Standard & OneDrive Documents Paths
            Path.Combine(myDocs, "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(userProfile, "Documents", "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "game", "replays"),
            // Legacy 32-bit Paths
            Path.Combine(myDocs, "Heroes of Newerth", "game", "replays"),
            Path.Combine(userProfile, "Documents", "Heroes of Newerth", "game", "replays"),
            Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth", "game", "replays"),
            // COMPEL & Dedicated Server Artifact Paths
            Path.Combine(myDocs, "HON", "game", "replays"),
            Path.Combine(userProfile, "Documents", "HON", "game", "replays"),
            Path.Combine(userProfile, "OneDrive", "Documents", "HON", "game", "replays"),
            Path.Combine(tempPath, "HON", "DOCUMENTS", "HEROES OF NEWERTH x64", "GAME", "replays"),
            Path.Combine(tempPath, "HON", "game", "replays")
        ];

        foreach (string path in standardPaths)
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!dirs.Contains(fullPath, StringComparer.OrdinalIgnoreCase) && System.IO.Directory.Exists(fullPath))
                    dirs.Add(fullPath);
            }
            catch
            {
                // Ignore invalid path formats
            }

        return dirs;
    }

    public static bool TryFindReplayFile(int matchId, string? customFileName, out string resolvedPath, out int fileSize)
    {
        resolvedPath = string.Empty;
        fileSize = 0;

        string standardFileName = $"M{matchId}.honreplay";
        List<string> fileNamesToCheck = [standardFileName];
        if (!string.IsNullOrWhiteSpace(customFileName) &&
            !fileNamesToCheck.Contains(customFileName, StringComparer.OrdinalIgnoreCase))
            fileNamesToCheck.Add(customFileName);

        foreach (string? candidate in from dir in GetAllReplayDirectories()
                 where System.IO.Directory.Exists(dir)
                 from name in fileNamesToCheck
                 select Path.Combine(dir, name)
                 into candidate
                 where File.Exists(candidate)
                 select candidate)
            try
            {
                FileInfo fi = new(candidate);
                resolvedPath = candidate;
                fileSize = (int)fi.Length;
                return true;
            }
            catch
            {
                // Skip unreadable file
            }

        return false;
    }

    private static int ResolveFileSize(MatchStatistics matchStatistics)
    {
        if (matchStatistics.FileSize > 0) return matchStatistics.FileSize;

        if (TryFindReplayFile(matchStatistics.MatchID, matchStatistics.FileName, out _, out int foundSize))
            return foundSize;

        if (!string.IsNullOrEmpty(matchStatistics.ReplayUrl) || matchStatistics.MatchID > 0) return 5242880;

        return 0;
    }
}
