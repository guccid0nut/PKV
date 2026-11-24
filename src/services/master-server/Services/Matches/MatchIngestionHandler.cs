// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Constants;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;
using KONGOR.MasterServer.Services.Background;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Services;
using PlayerEntity = MERRICK.DatabaseContext.Entities.Statistics.PlayerStatistics;

using NEXUS.Contracts.Domain.Matches;

namespace KONGOR.MasterServer.Services.Matches;

public sealed record PlayerIngestionEntry(
    PlayerEntity PlayerStats,
    Account Account,
    int GroupNum,
    int GroupSize,
    bool IsWin,
    bool IsMvp,
    string HeroKey);

public sealed class IngestedMatchContext
{
    public required int MatchID { get; init; }
    public required MatchStatistics MatchStatistics { get; init; }
    public StatsForSubmissionRequestForm? Form { get; init; }
    public MatchFinalizationPayload? BinaryPayload { get; init; }
    public required AccountStatisticsType MatchType { get; init; }
    public required int DurationSeconds { get; init; }
    public required Dictionary<int, string> PlayerAccountNames { get; init; }
    public required Dictionary<string, Account> AccountMap { get; init; }
    public required List<PlayerIngestionEntry> ProcessedPlayerEntries { get; init; }
    public bool IsUnrankedListenServer { get; init; }
}

public interface IMatchIngestionHandler
{
    Task<IngestedMatchContext> IngestMatchSubmissionAsync(
        MatchSubmissionPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed class MatchIngestionHandler(
    MerrickContext merrickContext,
    IHeroDefinitionService heroDefinitionService,
    ILogger<MatchIngestionHandler> logger) : IMatchIngestionHandler
{
    public async Task<IngestedMatchContext> IngestMatchSubmissionAsync(
        MatchSubmissionPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.BinaryPayload is not null)
        {
            return await IngestBinaryMatchSubmissionInternalAsync(payload.MatchID, payload.BinaryPayload, payload.MatchServer, cancellationToken);
        }

        int matchID = payload.MatchID;
        StatsForSubmissionRequestForm form = payload.Form ?? throw new InvalidOperationException("Both BinaryPayload and Form cannot be null in MatchSubmissionPayload.");
        MatchServer? matchServer = payload.MatchServer;

        MatchStatistics? existingMatchStatistics =
            await merrickContext.MatchStatistics.FirstOrDefaultAsync(stats => stats.MatchID == matchID,
                cancellationToken);

        if (existingMatchStatistics is null)
        {
            MatchStatistics matchStatistics = form.ToMatchStatistics(matchServer?.ID, matchServer?.HostAccountName);
            await merrickContext.MatchStatistics.AddAsync(matchStatistics, cancellationToken);
            existingMatchStatistics = matchStatistics;
        }

        // Extract all player account names from submission
        Dictionary<int, string> playerAccountNames = [];
        foreach (int playerIndex in form.PlayerStats.Keys)
        {
            string heroKey = form.PlayerStats[playerIndex].Keys.First();
            string fullAccountName = form.PlayerStats[playerIndex][heroKey]["nickname"];
            string accountName = AccountExtensions.SeparateClanTagFromAccountName(fullAccountName).AccountName;
            playerAccountNames[playerIndex] = accountName;
        }

        List<string> distinctAccountNames = [.. playerAccountNames.Values.Distinct()];

        // 1. Batch fetch existing stats
        List<string> existingStatsAccountNames = await merrickContext.PlayerStatistics
            .Where(stats => stats.MatchID == matchID && distinctAccountNames.Contains(stats.AccountName))
            .Select(stats => stats.AccountName)
            .ToListAsync(cancellationToken);

        HashSet<string> existingStatsSet = new(existingStatsAccountNames, StringComparer.OrdinalIgnoreCase);

        // 2. Batch fetch accounts with User and Mastery
        List<string> namesToFetchAccountsFor = [.. distinctAccountNames.Where(n => !existingStatsSet.Contains(n))];
        Dictionary<string, Account> accountMap = new(StringComparer.OrdinalIgnoreCase);

        if (namesToFetchAccountsFor.Count > 0)
        {
            List<Account> fetchedAccounts = await merrickContext.Accounts
                .Include(account => account.Clan)
                .Include(account => account.User)
                .Include(account => account.Mastery).ThenInclude(m => m!.HeroExperiences)
                .Where(account => namesToFetchAccountsFor.Contains(account.Name))
                .ToListAsync(cancellationToken);

            foreach (Account acc in fetchedAccounts) accountMap.TryAdd(acc.Name, acc);
        }

        // Match duration in seconds
        int durationSeconds = form.MatchStats.TryGetValue("time_played", out string? timePlayedStr) &&
                              int.TryParse(timePlayedStr, out int parsedDuration)
            ? parsedDuration
            : 1800;

        MatchRecord? matchRecord = await merrickContext.MatchRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchID, cancellationToken);

        bool isCasualMatch = (form.MatchStats.TryGetValue("casual", out string? casVal) && casVal == "1") ||
                             (form.MatchStats.TryGetValue("cas", out string? casShortVal) && casShortVal == "1") ||
                             (matchRecord?.IsCasual ?? false);
        string? gm = form.MatchStats.TryGetValue("gamemode", out string? gmVal) ? gmVal : matchRecord?.MatchMode;
        string canonicalMap = ASPIRE.Common.Constants.MapCatalog.NormalizeSlug(existingMatchStatistics.Map);
        ASPIRE.Common.Constants.CustomMapStatSchema mapSchema =
            ASPIRE.Common.Constants.CustomMapStatSchemaCatalog.ResolveForMap(canonicalMap, form.PlayerStats.Count);

        existingMatchStatistics.CustomSchemaKey = mapSchema.SchemaKey;
        existingMatchStatistics.CustomSchemaVersion = mapSchema.SchemaVersion;

        AccountStatisticsType matchType;
        if (matchRecord?.MatchType == 2 || (!string.IsNullOrWhiteSpace(gm) && gm.Contains("bot", StringComparison.OrdinalIgnoreCase)))
        {
            matchType = AccountStatisticsType.Cooperative;
        }
        else if (payload.IsUnrankedListenServer || (matchRecord is not null && matchRecord.MatchType == 0))
        {
            // Public/Hosted custom lobbies and listen servers must never mutate Ranked MMR buckets.
            matchType = AccountStatisticsType.Public;
        }
        else if (!ASPIRE.Common.Constants.MapCatalog.IsTmmEligibleMap(canonicalMap))
        {
            // Non-TMM custom maps (soccer, enfos, devowars*, truedevo*, arena, etc.) stay isolated in Public.
            matchType = AccountStatisticsType.Public;
        }
        else if (canonicalMap is "midwars" or "midwars_halloween" ||
                 (!string.IsNullOrWhiteSpace(gm) && gm.Contains("midwars", StringComparison.OrdinalIgnoreCase)))
        {
            matchType = AccountStatisticsType.MidWars;
        }
        else if (canonicalMap == "riftwars" ||
                 (!string.IsNullOrWhiteSpace(gm) && gm.Contains("riftwars", StringComparison.OrdinalIgnoreCase)))
        {
            matchType = AccountStatisticsType.RiftWars;
        }
        else if (isCasualMatch || (!string.IsNullOrWhiteSpace(gm) && gm.Contains("casual", StringComparison.OrdinalIgnoreCase)))
        {
            matchType = AccountStatisticsType.MatchmakingCasual;
        }
        else
        {
            matchType = AccountStatisticsType.Matchmaking;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[MatchIngestionHandler] Match #{MatchId} classified: Map='{CanonicalMap}' (Raw='{RawMap}'), GameMode='{GameMode}', MatchRecordType={RecordMatchType}, IsListenServer={IsListen} => Bucket={MatchType}",
                matchID, canonicalMap, existingMatchStatistics.Map, gm ?? "", matchRecord?.MatchType, payload.IsUnrankedListenServer, matchType);
        }

        if (matchType == AccountStatisticsType.MatchmakingCasual &&
            !existingMatchStatistics.GameMode.Contains("casual", StringComparison.OrdinalIgnoreCase))
        {
            existingMatchStatistics.GameMode = string.IsNullOrWhiteSpace(existingMatchStatistics.GameMode)
                ? "caldavar_casual_bp"
                : $"{existingMatchStatistics.GameMode}_casual";
        }

        List<PlayerIngestionEntry> processedPlayerEntries = [];

        foreach (int playerIndex in form.PlayerStats.Keys)
        {
            string accountName = playerAccountNames[playerIndex];

            if (!existingStatsSet.Contains(accountName))
            {
                if (!accountMap.TryGetValue(accountName, out Account? account))
                {
                    account = await merrickContext.Accounts
                        .Include(a => a.Clan)
                        .Include(a => a.User)
                        .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
                        .FirstOrDefaultAsync(a => a.Name == accountName, cancellationToken);

                    if (account is null)
                    {
                        User? guestUser = await merrickContext.Users.FirstOrDefaultAsync(u => u.ID == 2, cancellationToken)
                                          ?? await merrickContext.Users.FirstOrDefaultAsync(cancellationToken);

                        if (guestUser is not null)
                        {
                            account = new Account
                            {
                                Name = accountName.Length > 15 ? accountName[..15] : accountName,
                                UserID = guestUser.ID,
                                User = guestUser,
                                Type = AccountType.Guest,
                                IsMain = false
                            };
                            await merrickContext.Accounts.AddAsync(account, cancellationToken);
                            await merrickContext.SaveChangesAsync(cancellationToken);
                        }
                    }

                    if (account is not null)
                    {
                        accountMap[accountName] = account;
                    }
                    else
                    {
                        if (logger.IsEnabled(LogLevel.Warning))
                            logger.LogWarning(
                                "[MatchIngestionHandler] Skipping stats recording for non-user account {AccountName}",
                                accountName);

                        continue;
                    }
                }

                PlayerEntity playerStatistics = form.ToPlayerStatistics(playerIndex, account.ID, account.Name,
                    account.Clan?.ID, account.Clan?.Tag, heroDefinitionService);

                // Compute structured JSONB custom stats from gameplaystat0..9 + combat metrics
                Dictionary<string, double> customValues = CustomMapStatSchemaCatalog.BuildPlayerCustomStats(
                    mapSchema,
                    playerStatistics.GameplayStat0, playerStatistics.GameplayStat1, playerStatistics.GameplayStat2,
                    playerStatistics.GameplayStat3, playerStatistics.GameplayStat4, playerStatistics.GameplayStat5,
                    playerStatistics.GameplayStat6, playerStatistics.GameplayStat7, playerStatistics.GameplayStat8,
                    playerStatistics.GameplayStat9,
                    playerStatistics.HeroKills, playerStatistics.HeroDeaths,
                    playerStatistics.TeamCreepKills, playerStatistics.NeutralCreepKills,
                    playerStatistics.Denies, playerStatistics.HeroDamage, playerStatistics.BuildingDamage,
                    playerStatistics.Gold, playerStatistics.Experience, playerStatistics.WardsPlaced,
                    playerStatistics.SecondsPlayed);
                playerStatistics.CustomStats = CustomMapStatSchemaCatalog.SerializePlayerCustomStatsJson(customValues);

                if (payload.IsUnrankedListenServer || matchType is AccountStatisticsType.Public or AccountStatisticsType.Cooperative)
                {
                    playerStatistics.RankedMatch = 0;
                    playerStatistics.PublicMatch = 1;
                    playerStatistics.RankedSkillRatingChange = 0;
                    playerStatistics.PublicSkillRatingChange = 0;
                }
                else
                {
                    playerStatistics.RankedMatch = 1;
                    playerStatistics.PublicMatch = 0;
                }

                await merrickContext.PlayerStatistics.AddAsync(playerStatistics, cancellationToken);

                // Determine party/group size for this player
                string heroKey = form.PlayerStats[playerIndex].Keys.First();
                Dictionary<string, string> playerDict = form.PlayerStats[playerIndex][heroKey];
                string groupNumStr = playerDict.TryGetValue("group_num", out string? gnVal) ? gnVal : "1";
                int groupNum = int.TryParse(groupNumStr, out int gn) ? gn : 1;

                int groupSize = 1;
                if (groupNum > 0)
                    groupSize = form.PlayerStats.Values.Count(p =>
                    {
                        string hk = p.Keys.First();
                        return p[hk].TryGetValue("group_num", out string? otherGn) && otherGn == groupNumStr;
                    });
                groupSize = Math.Clamp(groupSize, 1, 5);

                bool isWin = playerStatistics.Win > 0;
                bool isMvp = playerStatistics.MVP > 0 ||
                             (existingMatchStatistics.MVPAccountID.HasValue &&
                              existingMatchStatistics.MVPAccountID.Value == account.ID) ||
                             (form.MatchStats.TryGetValue("awd_mann", out string? mann) && mann == "1" &&
                              playerStatistics.Annihilation > 0);

                processedPlayerEntries.Add(new PlayerIngestionEntry(
                    playerStatistics, account, groupNum, groupSize, isWin, isMvp, heroKey));
            }
        }

        if (string.IsNullOrWhiteSpace(existingMatchStatistics.CustomMatchStats))
        {
            existingMatchStatistics.CustomMatchStats = BuildCustomMatchMetaJson(
                canonicalMap, mapSchema, existingMatchStatistics, processedPlayerEntries);
        }

        return new IngestedMatchContext
        {
            MatchID = matchID,
            MatchStatistics = existingMatchStatistics,
            Form = form,
            MatchType = matchType,
            DurationSeconds = durationSeconds,
            PlayerAccountNames = playerAccountNames,
            AccountMap = accountMap,
            ProcessedPlayerEntries = processedPlayerEntries,
            IsUnrankedListenServer = payload.IsUnrankedListenServer
        };
    }

    private async Task<IngestedMatchContext> IngestBinaryMatchSubmissionInternalAsync(
        int matchID,
        MatchFinalizationPayload binary,
        MatchServer? matchServer,
        CancellationToken cancellationToken)
    {
        MatchStatistics? existingMatchStatistics =
            await merrickContext.MatchStatistics.FirstOrDefaultAsync(stats => stats.MatchID == matchID, cancellationToken);

        if (existingMatchStatistics is null)
        {
            MatchStatistics matchStatistics = new()
            {
                ServerID = binary.ServerId > 0 ? binary.ServerId : (matchServer?.ID ?? 0),
                HostAccountName = binary.HostAccountName ?? matchServer?.HostAccountName ?? "Server",
                MatchID = matchID,
                Map = binary.MapName,
                MapVersion = "1.0",
                TimePlayed = (int)binary.DurationSeconds,
                FileSize = 0,
                FileName = $"match_{matchID}.s2z",
                ConnectionState = 1,
                Version = "4.10.1",
                AveragePSR = 1500,
                AveragePSRTeamOne = 1500,
                AveragePSRTeamTwo = 1500,
                GameMode = binary.MatchType.ToString(),
                ScoreTeam1 = binary.Players.Where(p => p.Team == 1).Sum(p => p.Kills),
                ScoreTeam2 = binary.Players.Where(p => p.Team == 2).Sum(p => p.Kills),
                TeamScoreGoal = 0,
                PlayerScoreGoal = 0,
                NumberOfRounds = 1,
                ReleaseStage = "live"
            };
            await merrickContext.MatchStatistics.AddAsync(matchStatistics, cancellationToken);
            existingMatchStatistics = matchStatistics;
        }

        MatchRecord? matchRecord = await merrickContext.MatchRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchID, cancellationToken);

        string canonicalMap = ASPIRE.Common.Constants.MapCatalog.NormalizeSlug(existingMatchStatistics.Map);
        ASPIRE.Common.Constants.CustomMapStatSchema mapSchema =
            ASPIRE.Common.Constants.CustomMapStatSchemaCatalog.ResolveForMap(canonicalMap, binary.Players.Length);

        existingMatchStatistics.CustomSchemaKey = mapSchema.SchemaKey;
        existingMatchStatistics.CustomSchemaVersion = mapSchema.SchemaVersion;

        string gm = !string.IsNullOrWhiteSpace(existingMatchStatistics.GameMode)
            ? existingMatchStatistics.GameMode
            : (matchRecord?.MatchMode ?? binary.MatchType.ToString());

        AccountStatisticsType matchType;
        if (matchRecord?.MatchType == 2 || gm.Contains("bot", StringComparison.OrdinalIgnoreCase))
        {
            matchType = AccountStatisticsType.Cooperative;
        }
        else if (matchRecord is not null && matchRecord.MatchType == 0)
        {
            matchType = AccountStatisticsType.Public;
        }
        else if (!ASPIRE.Common.Constants.MapCatalog.IsTmmEligibleMap(canonicalMap))
        {
            matchType = AccountStatisticsType.Public;
        }
        else
        {
            matchType = binary.MatchType;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[MatchIngestionHandler] Binary match #{MatchId} classified: Map='{CanonicalMap}', MatchRecordType={RecordMatchType}, BinaryType={BinaryType} => Bucket={MatchType}",
                matchID, canonicalMap, matchRecord?.MatchType, binary.MatchType, matchType);
        }

        Dictionary<int, string> playerAccountNames = [];
        for (int i = 0; i < binary.Players.Length; i++)
        {
            playerAccountNames[i] = binary.Players[i].AccountName;
        }

        List<string> distinctAccountNames = [.. playerAccountNames.Values.Distinct()];

        List<string> existingStatsAccountNames = await merrickContext.PlayerStatistics
            .Where(stats => stats.MatchID == matchID && distinctAccountNames.Contains(stats.AccountName))
            .Select(stats => stats.AccountName)
            .ToListAsync(cancellationToken);

        HashSet<string> existingStatsSet = new(existingStatsAccountNames, StringComparer.OrdinalIgnoreCase);

        List<string> namesToFetchAccountsFor = [.. distinctAccountNames.Where(n => !existingStatsSet.Contains(n))];
        Dictionary<string, Account> accountMap = new(StringComparer.OrdinalIgnoreCase);

        if (namesToFetchAccountsFor.Count > 0)
        {
            List<Account> fetchedAccounts = await merrickContext.Accounts
                .Include(account => account.Clan)
                .Include(account => account.User)
                .Include(account => account.Mastery).ThenInclude(m => m!.HeroExperiences)
                .Where(account => namesToFetchAccountsFor.Contains(account.Name))
                .ToListAsync(cancellationToken);

            foreach (Account acc in fetchedAccounts) accountMap.TryAdd(acc.Name, acc);
        }

        List<PlayerIngestionEntry> processedPlayerEntries = [];

        foreach (var playerDto in binary.Players)
        {
            string accountName = playerDto.AccountName;
            if (existingStatsSet.Contains(accountName)) continue;

            if (!accountMap.TryGetValue(accountName, out Account? account))
            {
                account = await merrickContext.Accounts
                    .Include(a => a.Clan)
                    .Include(a => a.User)
                    .Include(a => a.Mastery).ThenInclude(m => m!.HeroExperiences)
                    .FirstOrDefaultAsync(a => a.Name == accountName, cancellationToken);

                if (account is not null)
                {
                    accountMap[accountName] = account;
                }
                else
                {
                    logger.LogWarning(
                        "[SECURITY-ALERT] [MatchIngestionHandler] Skipping binary stats recording for unregistered/unknown account '{AccountName}' on Match #{MatchId}.",
                        accountName, matchID);
                    continue;
                }
            }

            bool isWin = playerDto.Team == binary.WinningTeam;
            string cleanHeroId = HeroDefinitionService.CleanHeroIdentifier(playerDto.HeroKey);
            uint heroProductId = heroDefinitionService.GetBaseHeroId(cleanHeroId);
            string? altAvatarName = !string.IsNullOrWhiteSpace(playerDto.HeroKey) && playerDto.HeroKey.Contains('.')
                ? playerDto.HeroKey
                : null;
            int secondsPlayed = playerDto.SecondsPlayed > 0
                ? playerDto.SecondsPlayed
                : (int)binary.DurationSeconds;
            int totalExperience = (int)Math.Round(playerDto.ExpPerMinute * Math.Max(secondsPlayed / 60.0, 1.0));
            double[] gs = playerDto.GameplayStats ?? [];
            PlayerEntity playerStatistics = new()
            {
                MatchID = matchID,
                AccountID = account.ID,
                AccountName = account.Name,
                HeroIdentifier = cleanHeroId,
                HeroProductID = heroProductId > 0 ? heroProductId : null,
                AlternativeAvatarName = altAvatarName,
                HeroLevel = playerDto.Level,
                Team = playerDto.Team,
                Kills = playerDto.Kills,
                Deaths = playerDto.Deaths,
                Assists = playerDto.Assists,
                TeamCreepKills = playerDto.CreepKills,
                Denies = playerDto.CreepDenies,
                NeutralCreepKills = playerDto.NeutralKills,
                Gold = playerDto.TotalGold,
                GoldSpent = playerDto.TotalGold,
                Experience = totalExperience,
                SecondsPlayed = secondsPlayed,
                WardsPlaced = playerDto.WardsPlaced,
                BuildingDamage = playerDto.TotalDamageBuilding,
                HeroDamage = playerDto.TotalDamageHero,
                GameplayStat0 = SanitizeBinaryGameplayStat(gs, 0),
                GameplayStat1 = SanitizeBinaryGameplayStat(gs, 1),
                GameplayStat2 = SanitizeBinaryGameplayStat(gs, 2),
                GameplayStat3 = SanitizeBinaryGameplayStat(gs, 3),
                GameplayStat4 = SanitizeBinaryGameplayStat(gs, 4),
                GameplayStat5 = SanitizeBinaryGameplayStat(gs, 5),
                GameplayStat6 = SanitizeBinaryGameplayStat(gs, 6),
                GameplayStat7 = SanitizeBinaryGameplayStat(gs, 7),
                GameplayStat8 = SanitizeBinaryGameplayStat(gs, 8),
                GameplayStat9 = SanitizeBinaryGameplayStat(gs, 9),
                Win = isWin ? 1 : 0,
                Loss = isWin ? 0 : 1,
                MVP = playerDto.IsMvp ? 1 : 0,
                RankedMatch = matchType is AccountStatisticsType.Public or AccountStatisticsType.Cooperative ? 0 : 1,
                PublicMatch = matchType is AccountStatisticsType.Public or AccountStatisticsType.Cooperative ? 1 : 0,
                RankedSkillRatingChange = 0,
                PublicSkillRatingChange = 0
            };

            Dictionary<string, double> binaryCustomValues = CustomMapStatSchemaCatalog.BuildPlayerCustomStats(
                mapSchema,
                playerStatistics.GameplayStat0, playerStatistics.GameplayStat1, playerStatistics.GameplayStat2,
                playerStatistics.GameplayStat3, playerStatistics.GameplayStat4, playerStatistics.GameplayStat5,
                playerStatistics.GameplayStat6, playerStatistics.GameplayStat7, playerStatistics.GameplayStat8,
                playerStatistics.GameplayStat9,
                playerStatistics.HeroKills, playerStatistics.HeroDeaths,
                playerStatistics.TeamCreepKills, playerStatistics.NeutralCreepKills,
                playerStatistics.Denies, playerStatistics.HeroDamage, playerStatistics.BuildingDamage,
                playerStatistics.Gold, playerStatistics.Experience, playerStatistics.WardsPlaced,
                playerStatistics.SecondsPlayed);
            playerStatistics.CustomStats = CustomMapStatSchemaCatalog.SerializePlayerCustomStatsJson(binaryCustomValues);

            await merrickContext.PlayerStatistics.AddAsync(playerStatistics, cancellationToken);

            processedPlayerEntries.Add(new PlayerIngestionEntry(
                playerStatistics,
                account,
                playerDto.GroupNum > 0 ? playerDto.GroupNum : 1,
                playerDto.GroupSize > 0 ? playerDto.GroupSize : 1,
                isWin,
                playerDto.IsMvp,
                playerDto.HeroKey));
        }

        if (string.IsNullOrWhiteSpace(existingMatchStatistics.CustomMatchStats))
        {
            existingMatchStatistics.CustomMatchStats = BuildCustomMatchMetaJson(
                canonicalMap, mapSchema, existingMatchStatistics, processedPlayerEntries);
        }

        return new IngestedMatchContext
        {
            MatchID = matchID,
            MatchStatistics = existingMatchStatistics,
            Form = null,
            BinaryPayload = binary,
            MatchType = matchType,
            DurationSeconds = (int)binary.DurationSeconds,
            PlayerAccountNames = playerAccountNames,
            AccountMap = accountMap,
            ProcessedPlayerEntries = processedPlayerEntries,
            IsUnrankedListenServer = false
        };
    }

    private static double SanitizeBinaryGameplayStat(double[] values, int index)
    {
        if (index < 0 || index >= values.Length) return 0.0;
        double v = values[index];
        return double.IsFinite(v) ? Math.Clamp(v, -1_000_000.0, 1_000_000.0) : 0.0;
    }

    private static string BuildCustomMatchMetaJson(
        string canonicalMap,
        CustomMapStatSchema mapSchema,
        MatchStatistics existingMatchStatistics,
        List<PlayerIngestionEntry> processedPlayerEntries)
    {
        Dictionary<string, object?> matchCustomMeta = new(StringComparer.Ordinal)
        {
            ["map_slug"] = canonicalMap,
            ["schema_key"] = mapSchema.SchemaKey,
            ["schema_version"] = mapSchema.SchemaVersion,
            ["is_10v10"] = mapSchema.Is10v10 || processedPlayerEntries.Count > 10,
            ["is_coop_pve"] = mapSchema.IsCoOpPvE,
            ["player_count"] = processedPlayerEntries.Count
        };

        if (mapSchema.SchemaKey == "enfos" && processedPlayerEntries.Count > 0)
        {
            double maxWave = processedPlayerEntries.Max(e => e.PlayerStats.GameplayStat0);
            double diffCode = processedPlayerEntries.Max(e => e.PlayerStats.GameplayStat1);
            double legLives = processedPlayerEntries
                .Where(e => e.PlayerStats.Team == 1)
                .Select(e => e.PlayerStats.GameplayStat3)
                .DefaultIfEmpty(existingMatchStatistics.ScoreTeam1)
                .Max();
            double hbLives = processedPlayerEntries
                .Where(e => e.PlayerStats.Team == 2)
                .Select(e => e.PlayerStats.GameplayStat3)
                .DefaultIfEmpty(existingMatchStatistics.ScoreTeam2)
                .Max();

            matchCustomMeta["wave_reached"] = (int)Math.Round(maxWave);
            matchCustomMeta["max_waves"] = 39;
            matchCustomMeta["difficulty"] = CustomMapStatSchemaCatalog.FormatDifficultyCode(diffCode);
            matchCustomMeta["legion_lives_left"] = (int)Math.Round(legLives);
            matchCustomMeta["hellbourne_lives_left"] = (int)Math.Round(hbLives);
        }
        else if (mapSchema.SchemaKey == "duel_lives" && processedPlayerEntries.Count > 0)
        {
            double legLives = processedPlayerEntries
                .Where(e => e.PlayerStats.Team == 1)
                .Select(e => e.PlayerStats.GameplayStat0)
                .DefaultIfEmpty(existingMatchStatistics.ScoreTeam1)
                .Max();
            double hbLives = processedPlayerEntries
                .Where(e => e.PlayerStats.Team == 2)
                .Select(e => e.PlayerStats.GameplayStat0)
                .DefaultIfEmpty(existingMatchStatistics.ScoreTeam2)
                .Max();

            matchCustomMeta["legion_lives_left"] = (int)Math.Round(legLives);
            matchCustomMeta["hellbourne_lives_left"] = (int)Math.Round(hbLives);
        }
        else if (mapSchema.SchemaKey == "arena" && processedPlayerEntries.Count > 0)
        {
            PlayerIngestionEntry topEntry = processedPlayerEntries
                .OrderByDescending(e => e.PlayerStats.GameplayStat0)
                .ThenByDescending(e => e.PlayerStats.HeroKills)
                .ThenBy(e => e.PlayerStats.AccountID)
                .First();
            double roundsPlayed = processedPlayerEntries.Max(e => e.PlayerStats.GameplayStat1);

            matchCustomMeta["top_round_wins"] = (int)Math.Round(topEntry.PlayerStats.GameplayStat0);
            matchCustomMeta["top_round_winner"] = topEntry.PlayerStats.AccountName;
            matchCustomMeta["rounds_played"] = (int)Math.Round(roundsPlayed);
        }

        return JsonSerializer.Serialize(matchCustomMeta);
    }
}
