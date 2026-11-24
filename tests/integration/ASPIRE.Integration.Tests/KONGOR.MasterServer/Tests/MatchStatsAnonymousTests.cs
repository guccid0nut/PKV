// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Statistics;


// ReSharper disable once CheckNamespace

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class MatchStatsAnonymousTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private static readonly Regex MatchSummRegexInstance = new(@"s:10:""match_summ"";", RegexOptions.Compiled);


    private static readonly Regex MatchPlayerStatsRegexInstance =
        new(@"s:18:""match_player_stats"";", RegexOptions.Compiled);


    private static readonly Regex SelectedUpgradesRegexInstance =
        new(@"s:17:""selected_upgrades"";", RegexOptions.Compiled);


    private static readonly Regex TestAvatarRegexInstance = new(@"s:\d+:""aa\.test_avatar"";", RegexOptions.Compiled);


    [Fact]
    public async Task GetMatchStats_WithEmptyCookie_ReturnsSuccess()

    {
        // Arrange

        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        HttpClient client = webApplicationFactory.CreateClient();


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        // Seed MatchStatistics

        int matchId = Random.Shared.Next(900000000, 999999999);

        MatchStatistics matchStats = new()

        {
            MatchID = matchId,

            ServerID = 1,

            HostAccountName = "TestHost",

            Map = "caldavar",

            MapVersion = "1.0",

            Version = "4.10.1",

            GameMode = "normal",

            TimePlayed = 1200, // Seconds

            FileSize = 1024,

            FileName = $"M{matchId}.honreplay",

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "Live",

            BannedHeroes = null,

            AwardMostAnnihilations = null,

            AwardMostQuadKills = null,

            AwardLargestKillStreak = null,

            AwardMostSmackdowns = null,

            AwardMostKills = null,

            AwardMostAssists = null,

            AwardLeastDeaths = null,

            AwardMostBuildingDamage = null,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = null,

            AwardHighestCreepScore = null
        };


        await dbContext.MatchStatistics.AddAsync(matchStats);

        await dbContext.SaveChangesAsync();


        // payload with empty cookie

        Dictionary<string, string> payload = new()

        {
            { "f", "get_match_stats" }, { "match_id", matchId.ToString() }, { "cookie", "" }
        };


        // Act

        HttpResponseMessage response =
            await client.PostAsync("/client_requester.php", new FormUrlEncodedContent(payload));

        string content = await response.Content.ReadAsStringAsync();


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        // Verify response contains basic match info (serialized PHP array)

        Regex matchSummRegex = MatchSummRegex();

        Assert.Matches(matchSummRegex, content);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetMatchStats_WithCookie_ReturnsPersonalizedStats()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange

        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        HttpClient client = webApplicationFactory.CreateClient();


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase distributedCache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        // Seed MatchStatistics

        int matchId = Random.Shared.Next(900000000, 999999999);

        MatchStatistics matchStats = new()

        {
            MatchID = matchId,

            ServerID = 1,

            HostAccountName = "TestHost",

            Map = "caldavar",

            MapVersion = "1.0",

            Version = "4.10.1",

            GameMode = "normal",

            TimePlayed = 1200, // Seconds

            FileSize = 1024,

            FileName = $"M{matchId}.honreplay",

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "Live",

            BannedHeroes = null,

            AwardMostAnnihilations = null,

            AwardMostQuadKills = null,

            AwardLargestKillStreak = null,

            AwardMostSmackdowns = null,

            AwardMostKills = null,

            AwardMostAssists = null,

            AwardLeastDeaths = null,

            AwardMostBuildingDamage = null,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = null,

            AwardHighestCreepScore = null
        };


        await dbContext.MatchStatistics.AddAsync(matchStats);


        Role userRole = new() { Name = UserRoles.User };


        string uniqueAccountName = $"AuthUser_{Guid.NewGuid():N}"[..12];

        User user = new()

        {
            EmailAddress = $"{uniqueAccountName}@kongor.net",

            SRPPasswordHash = "srphash",

            SRPPasswordSalt = "srpsalt",

            PBKDF2PasswordHash = "hash",

            Role = userRole
        };

        await dbContext.Users.AddAsync(user);


        Account account = new() { User = user, Name = uniqueAccountName, IsMain = true };

        account.SelectedStoreItems.Add("aa.test_avatar");


        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        string cookie = Guid.NewGuid().ToString("N");

        await distributedCache.SetAccountNameForSessionCookie(cookie, uniqueAccountName);


        // payload with valid cookie

        Dictionary<string, string> payload = new()

        {
            { "f", "get_match_stats" }, { "match_id", matchId.ToString() }, { "cookie", cookie }
        };


        // Act

        HttpResponseMessage response =
            await client.PostAsync("/client_requester.php", new FormUrlEncodedContent(payload));

        string content = await response.Content.ReadAsStringAsync();


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        Regex matchSummRegex = MatchSummRegex();

        Assert.Matches(matchSummRegex, content);


        Regex avatarRegex = TestAvatarRegex();

        Assert.Matches(avatarRegex, content);
    }


    [Fact]
    public async Task GetMatchStats_WithInvalidMatchID_ReturnsSoftFailure()

    {
        // Arrange

        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        HttpClient client = webApplicationFactory.CreateClient();


        // payload with mismatched/missing Match ID

        Dictionary<string, string> payload = new()

        {
            { "f", "get_match_stats" },

            { "match_id", "123456789" }, // ID does not exist in seeded DB

            { "cookie", "any_cookie" }
        };


        // Act

        HttpResponseMessage response =
            await client.PostAsync("/client_requester.php", new FormUrlEncodedContent(payload));

        string content = await response.Content.ReadAsStringAsync();


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        Regex matchSummRegex = MatchSummRegex();

        Assert.Matches(matchSummRegex, content);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetMatchStats_ViaQueryString_WithEmptyCookie_ReturnsSuccess()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange

        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        HttpClient client = webApplicationFactory.CreateClient();


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        // Seed MatchStatistics

        int matchId = Random.Shared.Next(900000000, 999999999);

        MatchStatistics matchStats = new()

        {
            MatchID = matchId,

            ServerID = 1,

            HostAccountName = "TestHost",

            Map = "caldavar",

            MapVersion = "1.0",

            Version = "4.10.1",

            GameMode = "normal",

            TimePlayed = 1200,

            FileSize = 1024,

            FileName = $"M{matchId}.honreplay",

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "Live",

            BannedHeroes = null,

            AwardMostAnnihilations = null,

            AwardMostQuadKills = null,

            AwardLargestKillStreak = null,

            AwardMostSmackdowns = null,

            AwardMostKills = null,

            AwardMostAssists = null,

            AwardLeastDeaths = null,

            AwardMostBuildingDamage = null,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = null,

            AwardHighestCreepScore = null
        };


        await dbContext.MatchStatistics.AddAsync(matchStats);

        await dbContext.SaveChangesAsync();


        // Payload WITHOUT "f" in body, but with invalid/empty cookie

        Dictionary<string, string> payload = new() { { "match_id", matchId.ToString() }, { "cookie", "" } };


        // Act

        HttpResponseMessage response =
            await client.PostAsync("/client_requester.php?f=get_match_stats", new FormUrlEncodedContent(payload));

        string content = await response.Content.ReadAsStringAsync();


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);


        Regex matchSummRegex = MatchSummRegex();

        Assert.Matches(matchSummRegex, content);
    }


    [Fact]
    public async Task GetMatchStats_WithMixedCase_ReturnsSuccess()

    {
        // Arrange

        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory = fixture.Factory;

        HttpClient client = webApplicationFactory.CreateClient();


        using IServiceScope scope = webApplicationFactory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


        // Seed MatchStatistics

        int matchId = Random.Shared.Next(900000000, 999999999);

        MatchStatistics matchStats = new()

        {
            MatchID = matchId,

            ServerID = 1,

            HostAccountName = "TestHost",

            Map = "caldavar",

            MapVersion = "1.0",

            Version = "4.10.1",

            GameMode = "normal",

            TimePlayed = 1200,

            FileSize = 1024,

            FileName = $"M{matchId}.honreplay",

            ConnectionState = 0,

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            ScoreTeam1 = 0,

            ScoreTeam2 = 0,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "Live",

            BannedHeroes = null,

            AwardMostAnnihilations = null,

            AwardMostQuadKills = null,

            AwardLargestKillStreak = null,

            AwardMostSmackdowns = null,

            AwardMostKills = null,

            AwardMostAssists = null,

            AwardLeastDeaths = null,

            AwardMostBuildingDamage = null,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = null,

            AwardHighestCreepScore = null
        };


        await dbContext.MatchStatistics.AddAsync(matchStats);

        await dbContext.SaveChangesAsync();


        Dictionary<string, string> payload = new() { { "match_id", matchId.ToString() }, { "cookie", "" } };


        // Act

        HttpResponseMessage response =
            await client.PostAsync("/client_requester.php?f=Get_Match_Stats", new FormUrlEncodedContent(payload));


        // Assert

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }


    private static Regex MatchSummRegex()

    {
        return MatchSummRegexInstance;
    }

    private static Regex TestAvatarRegex()

    {
        return TestAvatarRegexInstance;
    }
}
