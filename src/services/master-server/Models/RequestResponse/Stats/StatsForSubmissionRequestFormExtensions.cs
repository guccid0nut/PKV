// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

public static class StatsForSubmissionRequestFormExtensions
{
    private static int GetIntOrDefault(IReadOnlyDictionary<string, string> values, string key, int defaultValue = 0) =>
        values.TryGetValue(key, out string? raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;

    private static uint GetUIntOrDefault(IReadOnlyDictionary<string, string> values, string key, uint defaultValue = 0) =>
        values.TryGetValue(key, out string? raw) &&
        uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : defaultValue;

    private static double GetDoubleOrDefault(IReadOnlyDictionary<string, string> values, string key, double defaultValue = 0) =>
        values.TryGetValue(key, out string? raw) &&
        double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : defaultValue;

    private static double GetSanitizedGameplayStat(IReadOnlyDictionary<string, string> values, string key)
    {
        double parsed = GetDoubleOrDefault(values, key);
        if (!double.IsFinite(parsed))
        {
            return 0.0;
        }

        return Math.Clamp(parsed, -1_000_000.0, 1_000_000.0);
    }

    private static string GetStringOrDefault(IReadOnlyDictionary<string, string> values, string key, string defaultValue = "") =>
        values.TryGetValue(key, out string? raw) && raw is not null
            ? raw
            : defaultValue;

    public static MatchStatistics ToMatchStatistics(this StatsForSubmissionRequestForm form, int? matchServerID = null,
        string? hostAccountName = null)
    {
        MatchStatistics statistics = new()
        {
            // A Stats Re-Submission Request Form Contains The Match Server ID While A Stats Submission Request Form Does Not
            ServerID = form.ServerID ?? matchServerID ?? throw new InvalidOperationException("Server ID Is NULL"),

            // A Stats Re-Submission Request Form Contains The Host Account Name While A Stats Submission Request Form Does Not
            HostAccountName =
                form.HostAccountName ??
                hostAccountName ?? throw new InvalidOperationException("Host Account Name Is NULL"),
            MatchID = int.Parse(form.MatchStats[MatchStatsProtocolKeys.MatchId], CultureInfo.InvariantCulture),
            Map = GetStringOrDefault(form.MatchStats, MatchStatsProtocolKeys.Map),
            MapVersion = GetStringOrDefault(form.MatchStats, MatchStatsProtocolKeys.MapVersion),
            TimePlayed = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.TimePlayed),
            FileSize = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.FileSize),
            FileName = GetStringOrDefault(form.MatchStats, MatchStatsProtocolKeys.FileName),
            ConnectionState = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.ConnectionState),
            Version = GetStringOrDefault(form.MatchStats, MatchStatsProtocolKeys.Version),
            AveragePSR = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.AverageSkillRating),
            AveragePSRTeamOne = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.AverageSkillRatingTeamOne),
            AveragePSRTeamTwo = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.AverageSkillRatingTeamTwo),
            GameMode = GetStringOrDefault(form.MatchStats, MatchStatsProtocolKeys.GameMode),
            ScoreTeam1 = form.TeamStats is not null && form.TeamStats.TryGetValue(1, out Dictionary<string, int>? team1) && team1.Count > 0
                ? team1.Values.First()
                : (form.TeamStats is { Count: > 0 } && form.TeamStats.First().Value.Count > 0 ? form.TeamStats.First().Value.First().Value : 0),
            ScoreTeam2 = form.TeamStats is not null && form.TeamStats.TryGetValue(2, out Dictionary<string, int>? team2) && team2.Count > 0
                ? team2.Values.First()
                : (form.TeamStats is { Count: > 1 } && form.TeamStats.Last().Value.Count > 0 ? form.TeamStats.Last().Value.First().Value : 0),
            TeamScoreGoal = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.TeamScoreGoal),
            PlayerScoreGoal = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.PlayerScoreGoal),
            NumberOfRounds = GetIntOrDefault(form.MatchStats, MatchStatsProtocolKeys.NumberOfRounds),
            ReleaseStage = GetStringOrDefault(form.MatchStats, MatchStatsProtocolKeys.ReleaseStage),
            BannedHeroes = form.MatchStats.TryGetValue(MatchStatsProtocolKeys.BannedHeroes, out string? banned)
                ? banned
                : null,
            ScheduledEventID = form.MatchStats.TryGetValue(MatchStatsProtocolKeys.EventId, out string? eventId) &&
                               int.TryParse(eventId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedEventId)
                ? parsedEventId
                : null,
            ScheduledMatchID =
                form.MatchStats.TryGetValue(MatchStatsProtocolKeys.MatchupId, out string? matchupId) &&
                int.TryParse(matchupId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMatchupId)
                    ? parsedMatchupId
                    : null,
            MVPAccountID = form.MatchStats.TryGetValue(MatchStatsProtocolKeys.MostValuablePlayer, out string? mvp) &&
                           int.TryParse(mvp, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMvpId)
                ? parsedMvpId
                : null,
            AwardMostAnnihilations = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostAnnihilations),
            AwardMostQuadKills = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostQuadKills),
            AwardLargestKillStreak = GetIntOrDefault(form.MatchStats, MatchAwardConstants.LargestKillStreak),
            AwardMostSmackdowns = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostSmackdowns),
            AwardMostKills = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostKills),
            AwardMostAssists = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostAssists),
            AwardLeastDeaths = GetIntOrDefault(form.MatchStats, MatchAwardConstants.LeastDeaths),
            AwardMostBuildingDamage = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostBuildingDamage),
            AwardMostWardsKilled = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostWardsKilled),
            AwardMostHeroDamageDealt = GetIntOrDefault(form.MatchStats, MatchAwardConstants.MostHeroDamageDealt),
            AwardHighestCreepScore = GetIntOrDefault(form.MatchStats, MatchAwardConstants.HighestCreepScore),
            FragHistory = form.FragHistory?.Select(frag => new MERRICK.DatabaseContext.Entities.Statistics.FragEvent
            {
                SourceID = frag.SourceID,
                TargetID = frag.TargetID,
                GameTimeSeconds = frag.GameTimeSeconds,
                SupporterIDs = frag.SupporterIDs
            }).ToList()
        };

        return statistics;
    }

    public static PlayerStatistics ToPlayerStatistics(this StatsForSubmissionRequestForm form, int playerIndex,
        int accountID, string accountName, int? clanID, string? clanTag, IHeroDefinitionService heroDefinitionService)
    {
        string hero = form.PlayerStats[playerIndex].Keys.Single();

        Dictionary<string, string> player = form.PlayerStats[playerIndex][hero];

        // 2026-01-11: Public Games (and others) may send 0 or uint.MaxValue for hero_id.
        // We must attempt to resolve the ID from the `hero` string key if the payload ID is invalid.
        uint parsedHeroId = GetUIntOrDefault(player, PlayerStatsProtocolKeys.HeroId);
        uint heroProductId = 0;

        if (parsedHeroId is > 0 and not uint.MaxValue)
        {
            uint baseIdFromParsed = heroDefinitionService.GetBaseHeroId(parsedHeroId);
            heroProductId = baseIdFromParsed > 0 ? baseIdFromParsed : parsedHeroId;
        }

        if (heroProductId == 0) heroProductId = heroDefinitionService.GetBaseHeroId(hero);

        string? resolvedAltAvatarName =
            player.TryGetValue(PlayerStatsProtocolKeys.AlternativeAvatarName, out string? altAvatarName) &&
            !string.IsNullOrWhiteSpace(altAvatarName)
                ? altAvatarName
                : hero.Contains('.')
                    ? hero
                    : null;

        PlayerStatistics statistics = new()
        {
            MatchID = int.Parse(form.MatchStats[MatchStatsProtocolKeys.MatchId], CultureInfo.InvariantCulture),
            AccountID = accountID,
            AccountName = accountName,
            ClanID = clanID,
            ClanTag = clanTag,
            Team = GetIntOrDefault(player, PlayerStatsProtocolKeys.Team),
            LobbyPosition = GetIntOrDefault(player, PlayerStatsProtocolKeys.Position),
            GroupNumber = GetIntOrDefault(player, PlayerStatsProtocolKeys.GroupNumber),
            Benefit = GetIntOrDefault(player, PlayerStatsProtocolKeys.Benefit),
            HeroProductID = heroProductId,
            HeroIdentifier = HeroDefinitionService.CleanHeroIdentifier(hero),
            AlternativeAvatarName = resolvedAltAvatarName,
            AlternativeAvatarProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.AlternativeAvatarProductId, out string? altAvatarPid) &&
                uint.TryParse(altAvatarPid, out uint pid) && pid != uint.MaxValue
                    ? pid
                    : null,
            WardProductName = player.TryGetValue(PlayerStatsProtocolKeys.WardName, out string? wardName)
                ? wardName
                : null,
            WardProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.WardProductId, out string? wardPid) &&
                uint.TryParse(wardPid, out uint wpid) &&
                wpid != uint.MaxValue
                    ? wpid
                    : null,
            TauntProductName = player.TryGetValue(PlayerStatsProtocolKeys.TauntName, out string? tauntName)
                ? tauntName
                : null,
            TauntProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.TauntProductId, out string? tauntPid) &&
                uint.TryParse(tauntPid, out uint tpid) &&
                tpid != uint.MaxValue
                    ? tpid
                    : null,
            AnnouncerProductName =
                player.TryGetValue(PlayerStatsProtocolKeys.AnnouncerName, out string? announcerName)
                    ? announcerName
                    : null,
            AnnouncerProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.AnnouncerProductId, out string? announcerPid) &&
                uint.TryParse(announcerPid, out uint apid) && apid != uint.MaxValue
                    ? apid
                    : null,
            CourierProductName = player.TryGetValue(PlayerStatsProtocolKeys.CourierName, out string? courierName)
                ? courierName
                : null,
            CourierProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.CourierProductId, out string? courierPid) &&
                uint.TryParse(courierPid, out uint cpid) &&
                cpid != uint.MaxValue
                    ? cpid
                    : null,
            AccountIconProductName = player.TryGetValue(PlayerStatsProtocolKeys.AccountIconName, out string? iconName)
                ? iconName
                : null,
            AccountIconProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.AccountIconProductId, out string? iconPid) &&
                uint.TryParse(iconPid, out uint ipid) &&
                ipid != uint.MaxValue
                    ? ipid
                    : null,
            ChatColourProductName = player.TryGetValue(PlayerStatsProtocolKeys.ChatColorName, out string? chatName)
                ? chatName
                : null,
            ChatColourProductID =
                player.TryGetValue(PlayerStatsProtocolKeys.ChatColorProductId, out string? chatPid) &&
                uint.TryParse(chatPid, out uint chpid) &&
                chpid != uint.MaxValue
                    ? chpid
                    : null,
            Inventory = form.PlayerInventory is not null &&
                        form.PlayerInventory.TryGetValue(playerIndex, out Dictionary<int, string>? inventory)
                ? [.. inventory.Values]
                : [],
            Win = GetIntOrDefault(player, PlayerStatsProtocolKeys.Wins),
            Loss = GetIntOrDefault(player, PlayerStatsProtocolKeys.Losses),
            Disconnected = GetIntOrDefault(player, PlayerStatsProtocolKeys.Disconnects),
            Conceded = GetIntOrDefault(player, PlayerStatsProtocolKeys.Concedes),
            Kicked = GetIntOrDefault(player, PlayerStatsProtocolKeys.Kicked),
            PublicMatch = GetIntOrDefault(player, PlayerStatsProtocolKeys.PublicMatchCount),
            PublicSkillRatingChange = GetDoubleOrDefault(player, PlayerStatsProtocolKeys.PublicSkillRating),
            RankedMatch = GetIntOrDefault(player, PlayerStatsProtocolKeys.RankedMatchCount),
            RankedSkillRatingChange = GetDoubleOrDefault(player, PlayerStatsProtocolKeys.RankedSkillRating),

            // --- FIX START ---
            // Checks if "mvp" key exists in player dictionary, otherwise defaults to 0
            MVP = GetIntOrDefault(player, PlayerStatsProtocolKeys.MostValuablePlayer),
            // --- FIX END ---

            SocialBonus = GetIntOrDefault(player, PlayerStatsProtocolKeys.SocialBonus),
            UsedToken = GetIntOrDefault(player, PlayerStatsProtocolKeys.UsedToken),
            ConcedeVotes = GetIntOrDefault(player, PlayerStatsProtocolKeys.ConcedeVotes),
            HeroKills = GetIntOrDefault(player, PlayerStatsProtocolKeys.HeroKills),
            HeroDamage = GetIntOrDefault(player, PlayerStatsProtocolKeys.HeroDamage),
            GoldFromHeroKills = GetIntOrDefault(player, PlayerStatsProtocolKeys.GoldFromHeroKills),
            HeroAssists = GetIntOrDefault(player, PlayerStatsProtocolKeys.HeroAssists),
            HeroExperience = GetIntOrDefault(player, PlayerStatsProtocolKeys.HeroExperience),
            HeroDeaths = GetIntOrDefault(player, PlayerStatsProtocolKeys.Deaths),
            Buybacks = GetIntOrDefault(player, PlayerStatsProtocolKeys.Buybacks),
            GoldLostToDeath = GetIntOrDefault(player, PlayerStatsProtocolKeys.GoldLostToDeath),
            SecondsDead = GetIntOrDefault(player, PlayerStatsProtocolKeys.SecondsDead),
            TeamCreepKills = GetIntOrDefault(player, PlayerStatsProtocolKeys.TeamCreepKills),
            TeamCreepDamage = GetIntOrDefault(player, PlayerStatsProtocolKeys.TeamCreepDamage),
            TeamCreepGold = GetIntOrDefault(player, PlayerStatsProtocolKeys.TeamCreepGold),
            TeamCreepExperience = GetIntOrDefault(player, PlayerStatsProtocolKeys.TeamCreepExperience),
            NeutralCreepKills = GetIntOrDefault(player, PlayerStatsProtocolKeys.NeutralCreepKills),
            NeutralCreepDamage = GetIntOrDefault(player, PlayerStatsProtocolKeys.NeutralCreepDamage),
            NeutralCreepGold = GetIntOrDefault(player, PlayerStatsProtocolKeys.NeutralCreepGold),
            NeutralCreepExperience = GetIntOrDefault(player, PlayerStatsProtocolKeys.NeutralCreepExperience),
            BuildingDamage = GetIntOrDefault(player, PlayerStatsProtocolKeys.BuildingDamage),
            BuildingsRazed = GetIntOrDefault(player, PlayerStatsProtocolKeys.BuildingsRazed),
            ExperienceFromBuildings = GetIntOrDefault(player, PlayerStatsProtocolKeys.ExperienceFromBuildings),
            GoldFromBuildings = GetIntOrDefault(player, PlayerStatsProtocolKeys.GoldFromBuildings),
            Denies = GetIntOrDefault(player, PlayerStatsProtocolKeys.Denies),
            ExperienceDenied = GetIntOrDefault(player, PlayerStatsProtocolKeys.ExperienceDenied),
            Gold = GetIntOrDefault(player, PlayerStatsProtocolKeys.Gold),
            GoldSpent = GetIntOrDefault(player, PlayerStatsProtocolKeys.GoldSpent),
            Experience = GetIntOrDefault(player, PlayerStatsProtocolKeys.Experience),
            Actions = GetIntOrDefault(player, PlayerStatsProtocolKeys.Actions),
            SecondsPlayed = GetIntOrDefault(player, PlayerStatsProtocolKeys.SecondsPlayed),
            HeroLevel = GetIntOrDefault(player, PlayerStatsProtocolKeys.HeroLevel),
            ConsumablesPurchased = GetIntOrDefault(player, PlayerStatsProtocolKeys.Consumables),
            WardsPlaced = GetIntOrDefault(player, PlayerStatsProtocolKeys.Wards),
            FirstBlood = GetIntOrDefault(player, PlayerStatsProtocolKeys.FirstBlood),
            DoubleKill = GetIntOrDefault(player, PlayerStatsProtocolKeys.DoubleKill),
            TripleKill = GetIntOrDefault(player, PlayerStatsProtocolKeys.TripleKill),
            QuadKill = GetIntOrDefault(player, PlayerStatsProtocolKeys.QuadKill),
            Annihilation = GetIntOrDefault(player, PlayerStatsProtocolKeys.Annihilation),
            KillStreak03 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak03),
            KillStreak04 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak04),
            KillStreak05 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak05),
            KillStreak06 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak06),
            KillStreak07 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak07),
            KillStreak08 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak08),
            KillStreak09 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak09),
            KillStreak10 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak10),
            KillStreak15 = GetIntOrDefault(player, PlayerStatsProtocolKeys.KillStreak15),
            Smackdown = GetIntOrDefault(player, PlayerStatsProtocolKeys.Smackdown),
            Humiliation = GetIntOrDefault(player, PlayerStatsProtocolKeys.Humiliation),
            Nemesis = GetIntOrDefault(player, PlayerStatsProtocolKeys.Nemesis),
            Retribution = GetIntOrDefault(player, PlayerStatsProtocolKeys.Retribution),
            Score = GetIntOrDefault(player, PlayerStatsProtocolKeys.Score),
            GameplayStat0 = GetSanitizedGameplayStat(player, GameplayStatsConstants.HealingDoneKey),
            GameplayStat1 = GetSanitizedGameplayStat(player, GameplayStatsConstants.DamageTakenKey),
            GameplayStat2 = GetSanitizedGameplayStat(player, GameplayStatsConstants.CrowdControlDurationKey),
            GameplayStat3 = GetSanitizedGameplayStat(player, GameplayStatsConstants.BuffsAppliedKey),
            GameplayStat4 = GetSanitizedGameplayStat(player, GameplayStatsConstants.PureDamageDealtKey),
            GameplayStat5 = GetSanitizedGameplayStat(player, GameplayStatsConstants.BossDamageDealtKey),
            GameplayStat6 = GetSanitizedGameplayStat(player, GameplayStatsConstants.PowerupsCollectedKey),
            GameplayStat7 = GetSanitizedGameplayStat(player, GameplayStatsConstants.SkillshotsLandedKey),
            GameplayStat8 = GetSanitizedGameplayStat(player, GameplayStatsConstants.ShieldsDamageAbsorbedKey),
            GameplayStat9 = GetSanitizedGameplayStat(player, GameplayStatsConstants.ObjectiveScoreKey),
            TimeEarningExperience = GetIntOrDefault(player, PlayerStatsProtocolKeys.TimeEarningExperience),
            ItemHistory = form.ItemHistory?.Where(item => item.AccountID == accountID).Select(item =>
                new MERRICK.DatabaseContext.Entities.Statistics.ItemEvent
                {
                    ItemName = item.ItemName,
                    GameTimeSeconds = item.GameTimeSeconds,
                    EventType = item.EventType
                }).ToList(),
            AbilityHistory = form.AbilityHistory is not null &&
                             form.AbilityHistory.TryGetValue(accountID, out List<AbilityEvent>? abilities)
                ?
                [
                    .. abilities.Select(ability => new MERRICK.DatabaseContext.Entities.Statistics.AbilityEvent
                    {
                        HeroName = ability.HeroName,
                        AbilityName = ability.AbilityName,
                        GameTimeSeconds = ability.GameTimeSeconds,
                        SlotIndex = ability.SlotIndex
                    })
                ]
                : null
        };

        return statistics;
    }
}
