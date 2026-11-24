// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Constants;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Domain.Matches;
using NEXUS.Contracts.DTOs;
using NEXUS.Contracts.EntityCache;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;
using DomainMatchStartData = ASPIRE.Common.Models.ServerManagement.MatchStartData;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class CustomMapStatsPipelineTests
{
    private static async Task<List<Account>> SeedPlayersAsync(MerrickContext db, int count, int startId = 1000)
    {
        DbRole role = await db.Set<DbRole>().FirstOrDefaultAsync(r => r.ID == 2)
                      ?? new DbRole { ID = 2, Name = "USER" };
        if (db.Entry(role).State == EntityState.Detached)
        {
            db.Set<DbRole>().Add(role);
        }

        List<Account> accounts = new(count);
        for (int i = 0; i < count; i++)
        {
            int id = startId + i;
            User user = new()
            {
                ID = id,
                EmailAddress = $"custom_p{id}@kongor.test",
                Role = role,
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "pbkdf2",
                GoldCoins = 100,
                SilverCoins = 100,
                InventoryItems = []
            };
            Account account = new()
            {
                ID = id,
                Name = $"Player{id}",
                User = user,
                Type = AccountType.Normal,
                IsMain = true,
                TimestampCreated = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
            db.Accounts.Add(account);
            accounts.Add(account);
        }

        await db.SaveChangesAsync();
        return accounts;
    }

    private static Dictionary<string, string> CreatePlayerStatFields(
        int accountId,
        string nickname,
        string heroKey,
        int team,
        int position,
        bool win,
        int kills,
        int deaths,
        int assists,
        double stat0 = 0,
        double stat1 = 0,
        double stat2 = 0,
        double stat3 = 0,
        double stat4 = 0,
        double stat5 = 0,
        double stat6 = 0)
    {
        return new Dictionary<string, string>
        {
            ["nickname"] = nickname,
            ["account_id"] = accountId.ToString(CultureInfo.InvariantCulture),
            ["hero_id"] = "0",
            ["team"] = team.ToString(CultureInfo.InvariantCulture),
            ["position"] = position.ToString(CultureInfo.InvariantCulture),
            ["group_num"] = "1",
            ["wins"] = win ? "1" : "0",
            ["losses"] = win ? "0" : "1",
            ["discos"] = "0",
            ["concedes"] = "0",
            ["kicked"] = "0",
            ["herokills"] = kills.ToString(CultureInfo.InvariantCulture),
            ["herodmg"] = (kills * 1200).ToString(CultureInfo.InvariantCulture),
            ["heroassists"] = assists.ToString(CultureInfo.InvariantCulture),
            ["deaths"] = deaths.ToString(CultureInfo.InvariantCulture),
            ["teamcreepkills"] = "45",
            ["neutralcreepkills"] = "12",
            ["denies"] = "4",
            ["bdmg"] = "800",
            ["gold"] = "9500",
            ["exp"] = "7200",
            ["actions"] = "1200",
            ["secs"] = "900",
            ["level"] = "16",
            ["wards"] = "2",
            ["gameplaystat0"] = stat0.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat1"] = stat1.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat2"] = stat2.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat3"] = stat3.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat4"] = stat4.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat5"] = stat5.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat6"] = stat6.ToString(CultureInfo.InvariantCulture),
            ["gameplaystat7"] = "0",
            ["gameplaystat8"] = "0",
            ["gameplaystat9"] = "0",
            ["cli_name"] = heroKey
        };
    }

    private static StatsForSubmissionRequestForm BuildSubmissionForm(
        int matchId,
        string mapSlug,
        string gameMode,
        int scoreTeam1,
        int scoreTeam2,
        List<Account> players,
        string heroKey,
        Func<int, (double s0, double s1, double s2, double s3, double s4, double s5, double s6)>? customStatFn = null)
    {
        Dictionary<int, Dictionary<string, Dictionary<string, string>>> playerStats = [];
        int half = players.Count / 2;
        for (int i = 0; i < players.Count; i++)
        {
            int team = i < half ? 1 : 2;
            bool win = team == 1;
            (double s0, double s1, double s2, double s3, double s4, double s5, double s6) =
                customStatFn?.Invoke(i) ?? (0, 0, 0, 0, 0, 0, 0);

            playerStats[i] = new Dictionary<string, Dictionary<string, string>>
            {
                [heroKey] = CreatePlayerStatFields(
                    players[i].ID,
                    players[i].Name,
                    heroKey,
                    team,
                    i,
                    win,
                    kills: 10 + i,
                    deaths: 2,
                    assists: 5,
                    stat0: s0, stat1: s1, stat2: s2, stat3: s3, stat4: s4, stat5: s5, stat6: s6)
            };
        }

        return new StatsForSubmissionRequestForm
        {
            Function = "submit_stats",
            MatchStats = new Dictionary<string, string>
            {
                ["match_id"] = matchId.ToString(CultureInfo.InvariantCulture),
                ["map"] = mapSlug,
                ["map_version"] = "1.0.0",
                ["time_played"] = "900",
                ["gamemode"] = gameMode,
                ["score_team_1"] = scoreTeam1.ToString(CultureInfo.InvariantCulture),
                ["score_team_2"] = scoreTeam2.ToString(CultureInfo.InvariantCulture),
                ["version"] = "4.10.1"
            },
            TeamStats = new Dictionary<int, Dictionary<string, int>>
            {
                [1] = new() { ["tm_score"] = scoreTeam1 },
                [2] = new() { ["tm_score"] = scoreTeam2 }
            },
            PlayerStats = playerStats
        };
    }

    [Fact]
    public void PhpSerialization_And_Clamping_PreserveGameplayStat0Through9_AndRejectInfNaN()
    {
        // protects: Wire-level PHP serialization & sanitization of gameplaystat0..9 before DB persistence.
        // fails_when: [PHPProperty("gameplaystat0..9")] attributes are missing or NaN/Infinity/overflow slips into CustomStats.
        // seam: PhpSerializer.SerializeToString<MatchPlayerStatistics> + StatsForSubmissionRequestFormExtensions.ToPlayerStatistics.
        StatsForSubmissionRequestForm form = new()
        {
            Function = "submit_stats",
            MatchStats = new Dictionary<string, string> { ["match_id"] = "7001" },
            PlayerStats = new Dictionary<int, Dictionary<string, Dictionary<string, string>>>
            {
                [0] = new()
                {
                    ["Hero_Blitz"] = new Dictionary<string, string>
                    {
                        ["nickname"] = "StrikerOne",
                        ["gameplaystat0"] = "7",
                        ["gameplaystat1"] = "3",
                        ["gameplaystat4"] = "99999999",
                        ["gameplaystat5"] = "NaN",
                        ["gameplaystat6"] = "Infinity",
                        ["gameplaystat9"] = "-42.5"
                    }
                }
            }
        };

        PlayerStatistics entity = form.ToPlayerStatistics(0, 10, "StrikerOne", null, null, Mock.Of<IHeroDefinitionService>());
        entity.GameplayStat0.Should().Be(7.0);
        entity.GameplayStat1.Should().Be(3.0);
        entity.GameplayStat4.Should().Be(1_000_000.0);
        entity.GameplayStat5.Should().Be(0.0);
        entity.GameplayStat6.Should().Be(0.0);
        entity.GameplayStat9.Should().Be(-42.5);

        MatchPlayerStatistics wireDto = new(
            new DomainMatchStartData
            {
                MatchID = 7001,
                MatchName = "Soccer Test",
                ServerID = 1,
                ServerName = "Srv1",
                HostAccountName = "Srv1",
                Map = "soccer",
                Version = "4.10.1",
                IsCasual = false,
                MatchType = 0,
                MatchMode = "normal"
            },
            new Account
            {
                ID = 10,
                Name = "StrikerOne",
                Type = AccountType.Normal,
                IsMain = true,
                User = new User
                {
                    ID = 10,
                    EmailAddress = "striker@kongor.test",
                    Role = new DbRole { ID = 2, Name = "USER" },
                    SRPPasswordSalt = "s",
                    SRPPasswordHash = "h",
                    PBKDF2PasswordHash = "p"
                }
            },
            entity,
            new AccountStatistics { Type = AccountStatisticsType.Matchmaking },
            new AccountStatistics { Type = AccountStatisticsType.MidWars },
            new AccountStatistics { Type = AccountStatisticsType.MatchmakingCasual })
        {
            HeroIdentifier = "Hero_Blitz"
        };
        string phpPayload = PhpSerializer.SerializeToString(wireDto);
        phpPayload.Should().Contain("s:13:\"gameplaystat0\";s:1:\"7\"");
        phpPayload.Should().Contain("s:13:\"gameplaystat4\";s:7:\"1000000\"");
        phpPayload.Should().Contain("s:13:\"gameplaystat9\";s:5:\"-42.5\"");
    }

    [Fact]
    public async Task CustomMapMatches_RouteToPublic_DoNotMutateRankedMmr_AndSkipHeroMasteryForCustomUnits()
    {
        // protects: Ranked/MidWars/RiftWars OpenSkill isolation & HeroMastery exclusion for custom map units.
        // fails_when: Custom map without MatchRecord routes into Ranked or creates HeroMasteryExperiences rows for Enfos_Hero_*.
        // seam: Real MatchProcessingService pipeline against MerrickContext (InMemory).
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<Account> players = await SeedPlayersAsync(db, 4, startId: 2100);

        // Pre-seed Ranked MMR to verify it stays untouched
        db.AccountStatistics.Add(new AccountStatistics
        {
            AccountID = players[0].ID,
            Type = AccountStatisticsType.Matchmaking,
            SkillRating = 1625,
            MatchesPlayed = 10,
            MatchesWon = 6,
            MatchesLost = 4
        });
        await db.SaveChangesAsync();

        Mock<IPlayerStatisticsService> mockStatsService = new();
        Mock<IHeroDefinitionService> mockHeroDefService = new();
        Mock<IDatabase> mockRedis = new();

        MatchProcessingService service = new(
            db, mockStatsService.Object, mockHeroDefService.Object, mockRedis.Object,
            NullLogger<MatchProcessingService>.Instance);

        // No MatchRecord row exists on purpose — MapCatalog.IsTmmEligibleMap must still force Public!
        StatsForSubmissionRequestForm form = BuildSubmissionForm(
            88001, "enfos", "normal", 24, 18, players, "Enfos_Hero_Uther",
            i => (s0: 24, s1: 1, s2: 45000 + i * 1000, s3: 24, s4: 2, s5: 90, s6: 12000));

        await service.ProcessMatchSubmissionAsync(new MatchSubmissionPayload
        {
            MatchID = 88001,
            Form = form,
            MatchServer = new MatchServer
            {
                ID = 1,
                Instance = 1,
                HostAccountID = 1,
                HostAccountName = "Srv1",
                IPAddress = "127.0.0.1",
                Port = 11235,
                Name = "Srv1",
                Description = "Test",
                Location = "US"
            },
            IsUnrankedListenServer = false
        });

        MatchStatistics match = await db.MatchStatistics.AsNoTracking().SingleAsync(m => m.MatchID == 88001);
        match.CustomSchemaKey.Should().Be("enfos");
        match.CustomMatchStats.Should().Contain("\"wave_reached\":24");
        match.CustomMatchStats.Should().Contain("\"difficulty\":\"Hard\"");

        List<PlayerStatistics> pStats = await db.PlayerStatistics.AsNoTracking().Where(p => p.MatchID == 88001).ToListAsync();
        pStats.Should().HaveCount(4);
        pStats.Should().OnlyContain(p => p.RankedMatch == 0 && p.PublicMatch == 1 && p.RankedSkillRatingChange == 0);

        AccountStatistics rankedStats = await db.AccountStatistics.AsNoTracking()
            .SingleAsync(s => s.AccountID == players[0].ID && s.Type == AccountStatisticsType.Matchmaking);
        rankedStats.SkillRating.Should().Be(1625);
        rankedStats.MatchesPlayed.Should().Be(10);

        // Enfos_Hero_Uther is a custom unit -> no HeroMasteryExperiences created
        int masteryExpCount = await db.HeroMasteryExperiences.CountAsync();
        masteryExpCount.Should().Be(0);

        AccountMapStatistics? mapStats = await db.AccountMapStatistics.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AccountID == players[0].ID && x.MapSlug == "enfos");
        mapStats.Should().NotBeNull();
        mapStats!.MatchesPlayed.Should().Be(1);
        mapStats.MatchesWon.Should().Be(1);
        mapStats.BestRecords.Should().Contain("\"wave_reached\":24");
    }

    [Fact]
    public async Task ListenServerSubmission_UpdatesMatchesPlayed_WithoutMutatingBestRecords()
    {
        // protects: Leaderboard / BestRecords integrity against listen-server stat farming.
        // fails_when: IsUnrankedListenServer = true writes high watermarks into AccountMapStatistics.BestRecords.
        // seam: Real MatchProcessingService + ReplayPersistenceWorker against MerrickContext.
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<Account> players = await SeedPlayersAsync(db, 2, startId: 2200);

        MatchProcessingService service = new(
            db, new Mock<IPlayerStatisticsService>().Object, new Mock<IHeroDefinitionService>().Object,
            new Mock<IDatabase>().Object, NullLogger<MatchProcessingService>.Instance);

        StatsForSubmissionRequestForm form = BuildSubmissionForm(
            88002, "soccer", "normal", 10, 4, players, "Hero_Blitz",
            i => (s0: 9, s1: 2, s2: 0, s3: 4, s4: 240, s5: 1, s6: 3));

        await service.ProcessMatchSubmissionAsync(new MatchSubmissionPayload
        {
            MatchID = 88002,
            Form = form,
            MatchServer = new MatchServer
            {
                ID = 0,
                Instance = 0,
                HostAccountID = players[0].ID,
                HostAccountName = players[0].Name,
                IPAddress = "127.0.0.1",
                Port = 11235,
                Name = "ListenServer",
                Description = "Listen",
                Location = "US"
            },
            IsUnrankedListenServer = true
        });

        List<AccountMapStatistics> allRows = await db.AccountMapStatistics.AsNoTracking().ToListAsync();
        allRows.Should().ContainSingle(r => r.AccountID == players[0].ID);
        AccountMapStatistics row = allRows.Single(r => r.AccountID == players[0].ID);
        row.MatchesPlayed.Should().Be(1);
        row.BestRecords.Should().Be("{}");
    }

    [Fact]
    public async Task CustomMapStatsController_Serves20PlayerDevowars10v10_EnfosHeroes_AndPlayerCareerFallback()
    {
        // protects: 20-player 10v10 JSON scoreboard, Enfo's icon resolution, and historical player map career fallback.
        // fails_when: Controller caps players at 10, fails Hero_Enfos resolution, or returns empty stats when AccountMapStatistics is unseeded.
        // seam: Real CustomMapStatsController + CustomMapStatsReader + MerrickContext (InMemory).
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<Account> twentyPlayers = await SeedPlayersAsync(db, 20, startId: 2300);

        MatchProcessingService service = new(
            db, new Mock<IPlayerStatisticsService>().Object, new Mock<IHeroDefinitionService>().Object,
            new Mock<IDatabase>().Object, NullLogger<MatchProcessingService>.Instance);

        // 1. 20-player devowars_10v10 match
        StatsForSubmissionRequestForm devo10v10Form = BuildSubmissionForm(
            88003, "devowars_10v10", "normal", 60, 42, twentyPlayers, "Hero_Devourer",
            i => (s0: 3, s1: 2, s2: 1, s3: 0, s4: 0, s5: 0, s6: 0));

        await service.ProcessMatchSubmissionAsync(new MatchSubmissionPayload
        {
            MatchID = 88003,
            Form = devo10v10Form,
            MatchServer = new MatchServer
            {
                ID = 1,
                Instance = 1,
                HostAccountID = 1,
                HostAccountName = "Srv1",
                IPAddress = "127.0.0.1",
                Port = 11235,
                Name = "Srv1",
                Description = "Test",
                Location = "US"
            },
            IsUnrankedListenServer = false
        });

        Mock<IHeroDefinitionService> heroDefMock = new();
        heroDefMock.Setup(h => h.CleanHeroIdentifier(It.IsAny<string?>()))
            .Returns((string? s) => string.IsNullOrWhiteSpace(s) ? "Hero_Unknown" : s.Trim());

        CustomMapStatsController controller = new(
            new CustomMapStatsRepository(db), heroDefMock.Object, NullLogger<CustomMapStatsController>.Instance);

        IActionResult matchResult = await controller.GetMatchCustomStats(88003);
        OkObjectResult okMatch = matchResult.Should().BeOfType<OkObjectResult>().Subject;
        CustomMatchStatsResponseDto dto = okMatch.Value.Should().BeOfType<CustomMatchStatsResponseDto>().Subject;

        dto.Banner.Is10v10.Should().BeTrue();
        dto.Banner.PlayerCount.Should().Be(20);
        dto.Banner.SchemaKey.Should().Be("devowars");
        dto.Players.Should().HaveCount(20);
        dto.Players.Count(p => p.Team == 1).Should().Be(10);
        dto.Players.Count(p => p.Team == 2).Should().Be(10);
        dto.Players[0].FormattedColumns["hook_accuracy_pct"].Should().Be("66.7%");
        dto.Players[0].FormattedColumns["headshots"].Should().Be("1");

        // 2. Historical Enfo's match WITHOUT AccountMapStatistics row -> test fallback aggregation
        db.MatchStatistics.Add(new MatchStatistics
        {
            MatchID = 88004,
            Map = "enfos",
            TimePlayed = 1100,
            ScoreTeam1 = 19,
            ScoreTeam2 = 5,
            GameMode = "normal",
            HostAccountName = "Srv1",
            FileName = "m.s2z",
            Version = "4.10.1"
        });
        db.PlayerStatistics.Add(new PlayerStatistics
        {
            MatchID = 88004,
            AccountID = twentyPlayers[0].ID,
            AccountName = twentyPlayers[0].Name,
            Team = 1,
            Win = 1,
            HeroIdentifier = "Enfos_Hero_Uther",
            HeroKills = 180,
            HeroDeaths = 3,
            HeroAssists = 40,
            GameplayStat0 = 29,
            GameplayStat1 = 1,
            GameplayStat2 = 62000,
            GameplayStat3 = 19,
            GameplayStat4 = 4,
            SecondsPlayed = 1100
        });
        await db.SaveChangesAsync();

        IActionResult enfosMatchRes = await controller.GetMatchCustomStats(88004);
        CustomMatchStatsResponseDto enfosDto = enfosMatchRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<CustomMatchStatsResponseDto>().Subject;
        enfosDto.Banner.WaveReached.Should().Be(29);
        enfosDto.Banner.Difficulty.Should().Be("Hard");
        enfosDto.Players[0].IsCustomUnit.Should().BeTrue();
        enfosDto.Players[0].HeroDisplayName.Should().Be("Uther");
        enfosDto.Players[0].HeroIconVfs.Should().Be("/heroes/hammerstorm/icon.tga");

        IActionResult playerMapRes = await controller.GetPlayerMapStats(twentyPlayers[0].Name, "enfos");
        PlayerMapStatsResponseDto mapCareerDto = playerMapRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PlayerMapStatsResponseDto>().Subject;
        mapCareerDto.MatchesPlayed.Should().Be(1);
        mapCareerDto.TopHeroes.Should().ContainSingle(h => h.HeroDisplayName == "Uther" && h.HeroIconVfs == "/heroes/hammerstorm/icon.tga");
        mapCareerDto.SummaryCards.Should().NotBeEmpty();
        mapCareerDto.BestRecords.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MapCatalog_And_PublicMapFilters_NeverCrossContaminateOverlappingMapNames()
    {
        // protects: Isolation between similarly named maps (grimmscrossing vs thegrimmhunt, devowars vs devowars_10v10/truedevo).
        // fails_when: %grimm% or %devowars% or %midwars% or %caldavar% substring filters return matches from variant custom maps.
        // seam: MatchHistoryRepository.GetMatchHistoryPageAsync against MerrickContext (InMemory).
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<Account> players = await SeedPlayersAsync(db, 1, startId: 2400);

        (int id, string map)[] seedMaps =
        [
            (99001, "grimmscrossing"),
            (99002, "thegrimmhunt"),
            (99003, "devowars"),
            (99004, "devowars_10v10"),
            (99005, "truedevo"),
            (99006, "midwars"),
            (99007, "midwars_10v10"),
            (99008, "caldavar"),
            (99009, "caldavar_wtf")
        ];

        foreach ((int id, string map) in seedMaps)
        {
            db.MatchStatistics.Add(new MatchStatistics
            {
                MatchID = id,
                Map = map,
                GameMode = "normal",
                TimePlayed = 600,
                HostAccountName = "Srv1",
                FileName = $"{id}.s2z",
                Version = "4.10.1"
            });
            db.PlayerStatistics.Add(new PlayerStatistics
            {
                MatchID = id,
                AccountID = players[0].ID,
                AccountName = players[0].Name,
                HeroIdentifier = "Hero_Pyromancer",
                PublicMatch = 1,
                RankedMatch = 0,
                Win = 1
            });
        }

        await db.SaveChangesAsync();

        MatchHistoryRepository repo = new(db);

        IReadOnlyList<PlayerMatchRecordDTO> grimmMatches =
            await repo.GetPlayerMatchHistoryOverviewAsync(players[0].ID, players[0].Name, "grimmscrossing", null, 50);
        grimmMatches.Select(m => m.MatchID).Should().BeEquivalentTo([99001]);

        IReadOnlyList<PlayerMatchRecordDTO> grimmHuntMatches =
            await repo.GetPlayerMatchHistoryOverviewAsync(players[0].ID, players[0].Name, "thegrimmhunt", null, 50);
        grimmHuntMatches.Select(m => m.MatchID).Should().BeEquivalentTo([99002]);

        IReadOnlyList<PlayerMatchRecordDTO> devoMatches =
            await repo.GetPlayerMatchHistoryOverviewAsync(players[0].ID, players[0].Name, "devowars", null, 50);
        devoMatches.Select(m => m.MatchID).Should().BeEquivalentTo([99003]);

        IReadOnlyList<PlayerMatchRecordDTO> midMatches =
            await repo.GetPlayerMatchHistoryOverviewAsync(players[0].ID, players[0].Name, "midwars_public", null, 50);
        midMatches.Select(m => m.MatchID).Should().BeEquivalentTo([99006]);

        IReadOnlyList<PlayerMatchRecordDTO> caldavarPubMatches =
            await repo.GetPlayerMatchHistoryOverviewAsync(players[0].ID, players[0].Name, "caldavar_public", null, 50);
        caldavarPubMatches.Select(m => m.MatchID).Should().BeEquivalentTo([99008]);
    }

    [Fact]
    public void CustomMapCatalog_And_SchemasV2_ResolveArenaHeroes_DuelTeamSizes_AndRemoveDeadColumns()
    {
        // protects: CustomHeroCatalog arena_Hero_* display/icon resolution, 1v1 duel TeamSize=1, and v2 custom stat schemas.
        // fails_when: arena_Hero_* falls back to raw identifier, colosseum/pebbles report 5v5, or dead always-0 columns return.
        // seam: CustomHeroCatalog + MapCatalog + CustomMapStatSchemaCatalog.
        MapCatalog.GetDefinition("colosseum_duel")!.TeamSize.Should().Be(1);
        MapCatalog.GetDefinition("pebbles_duel")!.TeamSize.Should().Be(1);
        MapCatalog.GetDefinition("arena")!.TeamSize.Should().Be(5);

        CustomHeroCatalog.IsCustomMapUnit("arena_Hero_Solstice").Should().BeTrue();
        CustomHeroCatalog.TryStripArenaHeroPrefix("arena_Hero_Solstice", out string stripped).Should().BeTrue();
        stripped.Should().Be("Hero_Solstice");

        (string canonicalId, string displayName, string iconVfs, bool isCustom) =
            CustomHeroCatalog.ResolveHeroDisplay("arena_Hero_Solstice");
        canonicalId.Should().Be("arena_Hero_Solstice");
        displayName.Should().Be("Solstice");
        iconVfs.Should().Be("/heroes/solstice/icon.tga");
        isCustom.Should().BeTrue();

        CustomMapStatSchema duelSchema = CustomMapStatSchemaCatalog.ResolveForMap("colosseum_duel");
        duelSchema.SchemaVersion.Should().Be(2);
        duelSchema.TeamScoreLabel.Should().Be("Lives");
        duelSchema.Fields.Should().NotContain(f => f.Key == "walls_cast" || f.Key == "spell_parries");
        duelSchema.Fields.Single(f => f.Key == "team_lives_left").Aggregation.Should().Be(CustomStatAggregation.Max);

        CustomMapStatSchema arenaSchema = CustomMapStatSchemaCatalog.ResolveForMap("arena");
        arenaSchema.SchemaVersion.Should().Be(2);
        arenaSchema.Fields.Should().NotContain(f => f.Key == "best_killing_spree");
        arenaSchema.Fields.Single(f => f.Key == "rounds_played").Aggregation.Should().Be(CustomStatAggregation.Sum);
        arenaSchema.Fields.Where(f => f.ShowOnScoreboard).Select(f => f.ShortHeader).Should().NotContain("GOLD");

        CustomMapStatSchema valkSchema = CustomMapStatSchemaCatalog.ResolveForMap("valkwars");
        valkSchema.SchemaVersion.Should().Be(2);
        valkSchema.Fields.Should().NotContain(f => f.Key == "best_killing_spree");

        CustomMapStatSchema archersSchema = CustomMapStatSchemaCatalog.ResolveForMap("archers");
        archersSchema.SchemaVersion.Should().Be(2);
        archersSchema.Fields.Should().NotContain(f => f.Key == "best_killing_spree");

        CustomMapStatSchema prophetsSchema = CustomMapStatSchemaCatalog.ResolveForMap("prophets");
        prophetsSchema.SchemaVersion.Should().Be(2);
        prophetsSchema.Fields.Should().NotContain(f => f.Key == "max_income_tier");
        prophetsSchema.Fields.Single(f => f.Key == "rounds_played").Aggregation.Should().Be(CustomStatAggregation.Sum);
    }

    [Fact]
    public async Task BinaryIngestion_PreservesGameplayStatsAndSecondsPlayed_AndRendersDuelLivesAndArenaBanners()
    {
        // protects: BinaryMatchSubmissionDto GameplayStats/SecondsPlayed ingestion, arena_Hero_* controller lookup, and duel_lives/arena banners.
        // fails_when: Binary ingestion drops GameplayStat0..9 or SecondsPlayed, or banner omits lives/top gladiator.
        // seam: Real MatchProcessingService + CustomMapStatsController + CustomMapStatsReader against MerrickContext (InMemory).
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<Account> players = await SeedPlayersAsync(db, 2, startId: 2500);

        Mock<IHeroDefinitionService> heroDefMock = new();
        heroDefMock.Setup(h => h.CleanHeroIdentifier(It.IsAny<string?>()))
            .Returns((string? s) => string.IsNullOrWhiteSpace(s) ? "Hero_Unknown" : s.Trim());
        heroDefMock.Setup(h => h.GetHeroDefinition("Hero_Solstice"))
            .Returns(new HeroDefinitionDto { Name = "Solstice", IconPath = "/heroes/solstice/icon.tga" });
        heroDefMock.Setup(h => h.GetHeroDefinition("Hero_Nomad"))
            .Returns(new HeroDefinitionDto { Name = "Nomad", IconPath = "/heroes/nomad/icon.tga" });

        MatchProcessingService service = new(
            db, new Mock<IPlayerStatisticsService>().Object, heroDefMock.Object,
            new Mock<IDatabase>().Object, NullLogger<MatchProcessingService>.Instance);

        MatchServer server = new()
        {
            ID = 1,
            Instance = 1,
            HostAccountID = 1,
            HostAccountName = "Srv1",
            IPAddress = "127.0.0.1",
            Port = 11235,
            Name = "Srv1",
            Description = "Test",
            Location = "US"
        };

        // 1. Colosseum Duel binary match with lives in GameplayStats[0]
        await service.ProcessMatchSubmissionAsync(new MatchSubmissionPayload
        {
            MatchID = 88101,
            BinaryPayload = new MatchFinalizationPayload
            {
                MatchId = 88101,
                MapName = "colosseum_duel",
                DurationSeconds = 420,
                WinningTeam = 1,
                MatchType = AccountStatisticsType.Public,
                Players =
                [
                    new PlayerMatchSummaryDto
                    {
                        AccountId = players[0].ID, AccountName = players[0].Name, Team = 1,
                        HeroKey = "Hero_Nomad", Level = 6, Kills = 16, Deaths = 5, Assists = 0,
                        TotalGold = 4200, TotalDamageHero = 9600, SecondsPlayed = 420,
                        GameplayStats = [11.0]
                    },
                    new PlayerMatchSummaryDto
                    {
                        AccountId = players[1].ID, AccountName = players[1].Name, Team = 2,
                        HeroKey = "Hero_Nomad", Level = 6, Kills = 5, Deaths = 16, Assists = 0,
                        TotalGold = 2800, TotalDamageHero = 4100, SecondsPlayed = 420,
                        GameplayStats = [0.0]
                    }
                ]
            },
            MatchServer = server,
            IsUnrankedListenServer = false
        });

        // 2. HoN Arena binary match with arena_Hero_Solstice and round wins/played in GameplayStats[0..1]
        await service.ProcessMatchSubmissionAsync(new MatchSubmissionPayload
        {
            MatchID = 88102,
            BinaryPayload = new MatchFinalizationPayload
            {
                MatchId = 88102,
                MapName = "arena",
                DurationSeconds = 600,
                WinningTeam = 1,
                MatchType = AccountStatisticsType.Public,
                Players =
                [
                    new PlayerMatchSummaryDto
                    {
                        AccountId = players[0].ID, AccountName = players[0].Name, Team = 1,
                        HeroKey = "arena_Hero_Solstice", Level = 14, Kills = 15, Deaths = 4, Assists = 2,
                        TotalGold = 12000, TotalDamageHero = 18500, SecondsPlayed = 600,
                        GameplayStats = [12.0, 15.0]
                    },
                    new PlayerMatchSummaryDto
                    {
                        AccountId = players[1].ID, AccountName = players[1].Name, Team = 2,
                        HeroKey = "arena_Hero_Solstice", Level = 12, Kills = 4, Deaths = 15, Assists = 0,
                        TotalGold = 8000, TotalDamageHero = 7400, SecondsPlayed = 600,
                        GameplayStats = [3.0, 15.0]
                    }
                ]
            },
            MatchServer = server,
            IsUnrankedListenServer = false
        });

        CustomMapStatsController controller = new(
            new CustomMapStatsRepository(db), heroDefMock.Object, NullLogger<CustomMapStatsController>.Instance);

        CustomMatchStatsResponseDto duelDto = (await controller.GetMatchCustomStats(88101))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CustomMatchStatsResponseDto>().Subject;
        duelDto.Banner.SchemaKey.Should().Be("duel_lives");
        duelDto.Banner.SchemaVersion.Should().Be(2);
        duelDto.Banner.LegionLivesLeft.Should().Be(11);
        duelDto.Banner.HellbourneLivesLeft.Should().Be(0);
        duelDto.Banner.ScoreSummaryText.Should().Be("Legion Lives Left: 11  |  Hellbourne Lives Left: 0");
        duelDto.Players[0].SecondsPlayed.Should().Be(420);
        duelDto.Players[0].FormattedColumns["team_lives_left"].Should().Be("11");
        duelDto.Players[0].FormattedColumns["net_life_delta"].Should().Be("11");

        CustomMatchStatsResponseDto arenaDto = (await controller.GetMatchCustomStats(88102))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CustomMatchStatsResponseDto>().Subject;
        arenaDto.Banner.SchemaKey.Should().Be("arena");
        arenaDto.Banner.SchemaVersion.Should().Be(2);
        arenaDto.Banner.ScoreSummaryText.Should().Be($"Top Gladiator: {players[0].Name} (12/15 Rounds)");
        arenaDto.Players[0].IsCustomUnit.Should().BeTrue();
        arenaDto.Players[0].HeroDisplayName.Should().Be("Solstice");
        arenaDto.Players[0].HeroIconVfs.Should().Be("/heroes/solstice/icon.tga");
        arenaDto.Players[0].FormattedColumns["round_wins"].Should().Be("12");
        arenaDto.Players[0].FormattedColumns["rounds_played"].Should().Be("15");
    }

    [Fact]
    public async Task RollupSchemaV1ToV2Upgrade_StripsStaleKeys_ResetsRoundsPlayed_AndHidesInternalVersionKey()
    {
        // protects: Safe schema v1 -> v2 upgrade in AccountMapStatistics rollups without DB migration.
        // fails_when: Obsolete v1 keys (best_killing_spree, walls_cast) leak into API responses or _schema_version renders as a BestRecord.
        // seam: ReplayPersistenceWorker.MergeRollupJson + CustomMapStatsReader.GetPlayerMapStatsAsync.
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<Account> players = await SeedPlayersAsync(db, 1, startId: 2600);

        // Seed legacy v1 AccountMapStatistics row (no _schema_version) with obsolete v1 key and old Last-aggregated rounds_played.
        AccountMapStatistics legacyArena = new()
        {
            AccountID = players[0].ID,
            MapSlug = "arena",
            SchemaKey = "arena",
            MatchesPlayed = 1,
            MatchesWon = 1,
            MatchesLost = 0,
            HeroKills = 10,
            Deaths = 2,
            HeroAssists = 1,
            CustomTotals = "{\"round_wins\":8,\"rounds_played\":99,\"best_killing_spree\":0}",
            BestRecords = "{\"round_wins\":8,\"best_killing_spree\":0,\"_schema_version\":1}",
            HeroPlayCountsJson = "{\"arena_Hero_Solstice\":1}"
        };
        db.AccountMapStatistics.Add(legacyArena);
        await db.SaveChangesAsync();

        // 1. Reader strips stale keys & _schema_version immediately even before next match persists.
        CustomMapStatsController controller = new(
            new CustomMapStatsRepository(db), Mock.Of<IHeroDefinitionService>(), NullLogger<CustomMapStatsController>.Instance);
        PlayerMapStatsResponseDto beforeMerge = (await controller.GetPlayerMapStats(players[0].Name, "arena"))
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<PlayerMapStatsResponseDto>().Subject;
        beforeMerge.BestRecords.Should().NotContain(r => r.Key == "best_killing_spree" || r.Key.StartsWith('_'));

        // 2. MergeRollupJson upgrades v1 -> v2: removes best_killing_spree, resets old rounds_played=99 before summing new match (15), stamps _schema_version=2.
        CustomMapStatSchema arenaSchema = CustomMapStatSchemaCatalog.ResolveForMap("arena");
        PlayerStatistics newArenaMatch = new()
        {
            AccountID = players[0].ID,
            AccountName = players[0].Name,
            HeroIdentifier = "arena_Hero_Solstice",
            HeroKills = 12,
            HeroDeaths = 3,
            GameplayStat0 = 10,
            GameplayStat1 = 15,
            HeroDamage = 14000,
            Gold = 9000,
            SecondsPlayed = 500
        };
        (string totalsJson, string bestsJson, string _, string? _, bool upgraded, int prevVer) =
            ReplayPersistenceWorker.MergeRollupJson(legacyArena, arenaSchema, newArenaMatch, "arena_Hero_Solstice", updateBestRecords: true);

        upgraded.Should().BeTrue();
        prevVer.Should().Be(1);
        totalsJson.Should().NotContain("best_killing_spree");
        totalsJson.Should().Contain("\"_schema_version\":2");
        totalsJson.Should().Contain("\"round_wins\":18");
        totalsJson.Should().Contain("\"rounds_played\":15");
        bestsJson.Should().NotContain("best_killing_spree");
    }
}
