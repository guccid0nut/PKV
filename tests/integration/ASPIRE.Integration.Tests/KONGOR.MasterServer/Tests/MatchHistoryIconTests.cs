// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Statistics;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MatchHistoryIconTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>
{
    private static readonly Regex HeroIdentifierRegexInstance =
        new(@"""cli_name"";\s*s:\d+:""([^""]+)""", RegexOptions.Compiled);

    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie)> SetupAsync(
            string? accountName = null)

    {
        HttpClient client = fixture.Client;

        IServiceScope scope = fixture.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string name = accountName ?? $"IconTest_{Guid.NewGuid().ToString("N")[..8]}";


        User user = new()

        {
            EmailAddress = $"{name}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = "hash",

            SRPPasswordSalt = "salt",

            Role = new Role { Name = UserRoles.User }
        };


        Account account = new() { Name = name, User = user, Type = AccountType.Staff, IsMain = true };


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        return (client, dbContext, cache, cookie);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task MatchHistory_Jereziah_SendsBaseID12_And_HeroIdentifier()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        int matchId = Random.Shared.Next(100000, 999999);

        string uniqueName = $"Jerez_{Guid.NewGuid():N}"[..12];

        (HttpClient client, MerrickContext dbContext, _, string cookie) = await SetupAsync(uniqueName);


        Account account = await dbContext.Accounts.FirstAsync(a => a.Name == uniqueName);


        MatchStatistics matchStats = new()

        {
            MatchID = matchId,

            Map = "caldavar",

            GameMode = "nm",

            TimestampRecorded = TimeProvider.System.GetUtcNow().UtcDateTime,

            TimePlayed = 1800,

            FileName = $"M{matchId}.honreplay",

            ServerID = 1,

            HostAccountName = "Server",

            Version = "4.10.1",

            MapVersion = "4.10.1",

            ReleaseStage = "stable",

            FileSize = 1000,

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            AwardMostAnnihilations = 0,

            AwardMostQuadKills = 0,

            AwardLargestKillStreak = 0,

            AwardMostSmackdowns = 0,

            AwardMostKills = 0,

            AwardMostAssists = 0,

            AwardLeastDeaths = 0,

            AwardMostBuildingDamage = 0,

            AwardMostWardsKilled = 0,

            AwardMostHeroDamageDealt = 0,

            AwardHighestCreepScore = 0,

            BannedHeroes = ""
        };


        PlayerStatistics playerStats = new()

        {
            MatchID = matchId,

            AccountID = account.ID,

            AccountName = account.Name,

            HeroProductID = 121, // Jereziah (Product ID)

            Team = 1,

            Win = 1,

            HeroKills = 5,

            HeroDeaths = 0,

            HeroAssists = 10,

            RankedMatch = 1,

            MVP = 0,

            ClanID = 0,

            ClanTag = "",

            LobbyPosition = 1,

            GroupNumber = 1,

            Benefit = 0,

            Inventory = [],

            Loss = 0,

            Disconnected = 0,

            Conceded = 0,

            Kicked = 0,

            PublicMatch = 0,

            PublicSkillRatingChange = 0,

            RankedSkillRatingChange = 0,

            SocialBonus = 0,

            UsedToken = 0,

            ConcedeVotes = 0,

            HeroDamage = 0,

            GoldFromHeroKills = 0,

            HeroExperience = 0,

            Buybacks = 0,

            GoldLostToDeath = 0,

            SecondsDead = 0,

            TeamCreepKills = 0,

            TeamCreepDamage = 0,

            TeamCreepGold = 0,

            TeamCreepExperience = 0,

            NeutralCreepKills = 0,

            NeutralCreepDamage = 0,

            NeutralCreepGold = 0,

            NeutralCreepExperience = 0,

            BuildingDamage = 0,

            BuildingsRazed = 0,

            ExperienceFromBuildings = 0,

            GoldFromBuildings = 0,

            Denies = 0,

            ExperienceDenied = 0,

            Gold = 0,

            GoldSpent = 0,

            Experience = 0,

            Actions = 0,

            SecondsPlayed = 1800,

            HeroLevel = 25,

            ConsumablesPurchased = 0,

            WardsPlaced = 0,

            FirstBlood = 0,

            DoubleKill = 0,

            TripleKill = 0,

            QuadKill = 0,

            Annihilation = 0,

            KillStreak03 = 0,

            KillStreak04 = 0,

            KillStreak05 = 0,

            KillStreak06 = 0,

            KillStreak07 = 0,

            KillStreak08 = 0,

            KillStreak09 = 0,

            KillStreak10 = 0,

            KillStreak15 = 0,

            Smackdown = 0,

            Humiliation = 0,

            Nemesis = 0,

            Retribution = 0,

            Score = 0,

            GameplayStat0 = 0,

            GameplayStat1 = 0,

            GameplayStat2 = 0,

            GameplayStat3 = 0,

            GameplayStat4 = 0,

            GameplayStat5 = 0,

            GameplayStat6 = 0,

            GameplayStat7 = 0,

            GameplayStat8 = 0,

            GameplayStat9 = 0,

            TimeEarningExperience = 0
        };


        await dbContext.MatchStatistics.AddAsync(matchStats);

        await dbContext.PlayerStatistics.AddAsync(playerStats);

        await dbContext.SaveChangesAsync();


        Dictionary<string, string> payload = new()

        {
            { "f", "match_history_overview" },

            { "nickname", account.Name },

            { "cookie", cookie },

            { "table", "campaign" },

            { "num", "100" },

            { "current_season", "1" }
        };

        FormUrlEncodedContent content = new(payload);

        HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();


        Assert.Contains(matchId.ToString(), body);

        Assert.Contains("12", body);

        Assert.Contains("Hero_Jereziah", body);

        Assert.Contains("caldavar", body);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task MatchDetails_Panda_Returns_Hero_Panda_Identifier()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        int matchId = Random.Shared.Next(100000, 999999);

        string uniqueName = $"Panda_{Guid.NewGuid():N}"[..12];

        (HttpClient client, MerrickContext dbContext, _, string cookie) = await SetupAsync(uniqueName);


        Account account = await dbContext.Accounts.FirstAsync(a => a.Name == uniqueName);


        MatchStatistics matchStats = new()

        {
            MatchID = matchId,

            Map = "caldavar",

            GameMode = "nm",

            TimestampRecorded = TimeProvider.System.GetUtcNow().UtcDateTime,

            TimePlayed = 1800,

            FileName = $"M{matchId}.honreplay",

            ServerID = 1,

            HostAccountName = "Server",

            Version = "4.10.1",

            MapVersion = "4.10.1",

            ReleaseStage = "stable",

            FileSize = 1000,

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            AwardMostAnnihilations = 0,

            AwardMostQuadKills = 0,

            AwardLargestKillStreak = 0,

            AwardMostSmackdowns = 0,

            AwardMostKills = 0,

            AwardMostAssists = 0,

            AwardLeastDeaths = 0,

            AwardMostBuildingDamage = 0,

            AwardMostWardsKilled = 0,

            AwardMostHeroDamageDealt = 0,

            AwardHighestCreepScore = 0,

            BannedHeroes = ""
        };


        AccountStatistics accountStats = new()

        {
            AccountID = account.ID,

            Account = account,

            Type = AccountStatisticsType.Matchmaking,

            MatchesPlayed = 1,

            MatchesWon = 1,

            MatchesLost = 0,

            MatchesConceded = 0,

            MatchesDisconnected = 0,

            MatchesKicked = 0,

            SkillRating = 1500,

            PerformanceScore = 0,

            PlacementMatchesData = ""
        };


        PlayerStatistics playerStats = new()

        {
            MatchID = matchId,

            AccountID = account.ID,

            AccountName = account.Name,

            HeroProductID = 122, // Pandamonium (Product ID)

            Team = 1,

            Win = 1,

            HeroKills = 10,

            HeroDeaths = 0,

            HeroAssists = 5,

            MVP = 0,

            Inventory = ["Item_Loggers", "Item_DuckBoots"],

            ClanID = 0,

            ClanTag = "",

            LobbyPosition = 1,

            GroupNumber = 1,

            Benefit = 0,

            Loss = 0,

            Disconnected = 0,

            Conceded = 0,

            Kicked = 0,

            PublicMatch = 0,

            PublicSkillRatingChange = 0,

            RankedMatch = 0,

            RankedSkillRatingChange = 0,

            SocialBonus = 0,

            UsedToken = 0,

            ConcedeVotes = 0,

            HeroDamage = 0,

            GoldFromHeroKills = 0,

            HeroExperience = 0,

            Buybacks = 0,

            GoldLostToDeath = 0,

            SecondsDead = 0,

            TeamCreepKills = 0,

            TeamCreepDamage = 0,

            TeamCreepGold = 0,

            TeamCreepExperience = 0,

            NeutralCreepKills = 0,

            NeutralCreepDamage = 0,

            NeutralCreepGold = 0,

            NeutralCreepExperience = 0,

            BuildingDamage = 0,

            BuildingsRazed = 0,

            ExperienceFromBuildings = 0,

            GoldFromBuildings = 0,

            Denies = 0,

            ExperienceDenied = 0,

            Gold = 0,

            GoldSpent = 0,

            Experience = 0,

            Actions = 0,

            SecondsPlayed = 1800,

            HeroLevel = 25,

            ConsumablesPurchased = 0,

            WardsPlaced = 0,

            FirstBlood = 0,

            DoubleKill = 0,

            TripleKill = 0,

            QuadKill = 0,

            Annihilation = 0,

            KillStreak03 = 0,

            KillStreak04 = 0,

            KillStreak05 = 0,

            KillStreak06 = 0,

            KillStreak07 = 0,

            KillStreak08 = 0,

            KillStreak09 = 0,

            KillStreak10 = 0,

            KillStreak15 = 0,

            Smackdown = 0,

            Humiliation = 0,

            Nemesis = 0,

            Retribution = 0,

            Score = 0,

            GameplayStat0 = 0,

            GameplayStat1 = 0,

            GameplayStat2 = 0,

            GameplayStat3 = 0,

            GameplayStat4 = 0,

            GameplayStat5 = 0,

            GameplayStat6 = 0,

            GameplayStat7 = 0,

            GameplayStat8 = 0,

            GameplayStat9 = 0,

            TimeEarningExperience = 0
        };


        await dbContext.MatchStatistics.AddAsync(matchStats);

        await dbContext.AccountStatistics.AddAsync(accountStats);

        await dbContext.PlayerStatistics.AddAsync(playerStats);

        await dbContext.SaveChangesAsync();


        Dictionary<string, string> payload = new()

        {
            { "f", "get_match_stats" }, { "match_id", matchId.ToString() }, { "cookie", cookie }
        };

        FormUrlEncodedContent content = new(payload);

        HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();


        Regex heroIdentifierRegex = HeroIdentifierRegex();

        Match match = heroIdentifierRegex.Match(body);


        Assert.True(match.Success);

        Assert.Equal("Hero_Panda", match.Groups[1].Value);
    }


    private static Regex HeroIdentifierRegex()

    {
        return HeroIdentifierRegexInstance;
    }
}
