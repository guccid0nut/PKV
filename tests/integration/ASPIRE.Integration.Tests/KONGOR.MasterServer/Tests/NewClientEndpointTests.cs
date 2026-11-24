// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Collections;
using System.Net;
using System.Security.Cryptography;
using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Statistics;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class NewClientEndpointTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>

{
    private const string DefaultTestPassword = "Password123!";

    private const string DefaultTestSalt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053";


    private async Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie, Account Account)>
        SetupTestAccountAsync()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();

        IServiceScope scope = factory.Services.CreateScope();

        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();


        string cookie = Guid.NewGuid().ToString("N");

        string accountName = $"Player_{Guid.NewGuid().ToString("N")[..6]}";


        Role userRole = await dbContext.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };

        if (userRole.ID == 0 || dbContext.Entry(userRole).State == EntityState.Detached)
            await dbContext.Roles.AddAsync(userRole);


        string srpHash = SRPAuthenticationHandlers.ComputeSRPPasswordHash(DefaultTestPassword, DefaultTestSalt, false);


        User user = new()

        {
            EmailAddress = $"{accountName}@kongor.net",

            DiscordId = "123456789012345678",

            DiscordUsername = $"{accountName}#0001",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = srpHash,

            SRPPasswordSalt = DefaultTestSalt,

            Role = userRole,

            SilverCoins = 500,

            GoldCoins = 150,

            PlinkoTickets = 10
        };


        Account account = new() { Name = accountName, User = user, Type = AccountType.Normal, IsMain = true };


        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        return (client, dbContext, cache, cookie, account);
    }


    private static PlayerStatistics CreateDummyPlayerStats(int matchId, int accountId, string accountName,
        string heroIdentifier)

    {
        return new PlayerStatistics

        {
            MatchID = matchId,

            AccountID = accountId,

            AccountName = accountName,

            HeroIdentifier = heroIdentifier,

            HeroProductID = 1,

            MVP = 0,

            ClanID = null,

            ClanTag = null,

            Team = 1,

            LobbyPosition = 1,

            GroupNumber = 1,

            Benefit = 0,

            Inventory = ["Item_Steamboots", "Item_PortalKey"],

            Win = 1,

            Loss = 0,

            Disconnected = 0,

            Conceded = 0,

            Kicked = 0,

            PublicMatch = 1,

            PublicSkillRatingChange = 5.0,

            RankedMatch = 0,

            RankedSkillRatingChange = 0.0,

            SocialBonus = 0,

            UsedToken = 0,

            ConcedeVotes = 0,

            HeroKills = 10,

            HeroDamage = 15000,

            GoldFromHeroKills = 2500,

            HeroAssists = 8,

            HeroExperience = 12000,

            HeroDeaths = 2,

            Buybacks = 0,

            GoldLostToDeath = 200,

            SecondsDead = 40,

            TeamCreepKills = 120,

            TeamCreepDamage = 10000,

            TeamCreepGold = 4000,

            TeamCreepExperience = 5000,

            NeutralCreepKills = 20,

            NeutralCreepDamage = 3000,

            NeutralCreepGold = 1000,

            NeutralCreepExperience = 1200,

            BuildingDamage = 2000,

            BuildingsRazed = 2,

            ExperienceFromBuildings = 500,

            GoldFromBuildings = 600,

            Denies = 15,

            ExperienceDenied = 800,

            Gold = 12000,

            GoldSpent = 11000,

            Experience = 15000,

            Actions = 2500,

            SecondsPlayed = 1800,

            HeroLevel = 25,

            ConsumablesPurchased = 4,

            WardsPlaced = 3,

            FirstBlood = 1,

            DoubleKill = 2,

            TripleKill = 1,

            QuadKill = 0,

            Annihilation = 0,

            KillStreak03 = 2,

            KillStreak04 = 1,

            KillStreak05 = 1,

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

            Score = 1500,

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

            TimeEarningExperience = 1600
        };
    }


    [Fact]
    public async Task SetInGameReferrer_And_GetReferrals_Succeeds()

    {
        (HttpClient client, MerrickContext db, IDatabase _, string cookie, Account account) =
            await SetupTestAccountAsync();


        // 1. Set Referrer

        Dictionary<string, string> setReferrerParams = new()

        {
            ["f"] = "set_ingame_referrer",
            ["cookie"] = cookie,
            ["referrer"] = "TestMentor",
            ["match_id"] = "1001"
        };

        FormUrlEncodedContent setContent = new(setReferrerParams);

        HttpResponseMessage setResponse = await client.PostAsync("client_requester.php", setContent);

        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);


        string setPhpStr = await setResponse.Content.ReadAsStringAsync();

        object? setObj = PhpSerializer.Deserialize(setPhpStr);

        Assert.NotNull(setObj);

        IDictionary setDict = Assert.IsType<IDictionary>(setObj, false);

        Assert.Equal(true, setDict["0"]);

        Assert.Equal("TestMentor", setDict["referrer"]);


        // 2. Get Referrals

        Dictionary<string, string> getReferralsParams = new()

        {
            ["f"] = "get_referrals",
            ["cookie"] = cookie,
            ["account_id"] = account.ID.ToString()
        };

        FormUrlEncodedContent getContent = new(getReferralsParams);

        HttpResponseMessage getResponse = await client.PostAsync("client_requester.php", getContent);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);


        string getPhpStr = await getResponse.Content.ReadAsStringAsync();

        object? getObj = PhpSerializer.Deserialize(getPhpStr);

        Assert.NotNull(getObj);

        IDictionary getDict = Assert.IsType<IDictionary>(getObj, false);

        Assert.Equal(true, getDict["0"]);

        Assert.NotNull(getDict["rewards"]);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetHeroUsageList_And_Graph_Succeeds()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (HttpClient client, MerrickContext _, IDatabase _, string cookie, _) = await SetupTestAccountAsync();


        // 1. Hero Usage List

        Dictionary<string, string> usageParams = new()

        {
            ["f"] = "get_hero_usage_list",
            ["cookie"] = cookie,
            ["sort"] = "use"
        };

        FormUrlEncodedContent usageContent = new(usageParams);

        HttpResponseMessage usageResponse = await client.PostAsync("client_requester.php", usageContent);

        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);


        string usagePhpStr = await usageResponse.Content.ReadAsStringAsync();

        object? usageObj = PhpSerializer.Deserialize(usagePhpStr);

        Assert.NotNull(usageObj);

        IDictionary usageDict = Assert.IsType<IDictionary>(usageObj, false);

        Assert.Equal(true, usageDict["0"]);

        Assert.NotNull(usageDict["heroes"]);


        // 2. Hero Usage Graph

        Dictionary<string, string> graphParams = new()

        {
            ["f"] = "get_hero_usage_graph",
            ["cookie"] = cookie,
            ["hero"] = "Hero_Chronos"
        };

        FormUrlEncodedContent graphContent = new(graphParams);

        HttpResponseMessage graphResponse = await client.PostAsync("client_requester.php", graphContent);

        Assert.Equal(HttpStatusCode.OK, graphResponse.StatusCode);


        string graphPhpStr = await graphResponse.Content.ReadAsStringAsync();

        object? graphObj = PhpSerializer.Deserialize(graphPhpStr);

        Assert.NotNull(graphObj);

        IDictionary graphDict = Assert.IsType<IDictionary>(graphObj, false);

        Assert.Equal(true, graphDict["0"]);

        Assert.Equal("Hero_Chronos", graphDict["hero"]);
    }


    [Fact]
    public async Task GetPoints_And_RevivalGift_Succeeds()

    {
        (HttpClient client, MerrickContext _, IDatabase _, string cookie, Account account) =
            await SetupTestAccountAsync();


        // 1. Get Points

        Dictionary<string, string> pointsParams = new() { ["f"] = "get_points", ["cookie"] = cookie };

        FormUrlEncodedContent pointsContent = new(pointsParams);

        HttpResponseMessage pointsResponse = await client.PostAsync("client_requester.php", pointsContent);

        Assert.Equal(HttpStatusCode.OK, pointsResponse.StatusCode);


        string pointsPhpStr = await pointsResponse.Content.ReadAsStringAsync();

        object? pointsObj = PhpSerializer.Deserialize(pointsPhpStr);

        Assert.NotNull(pointsObj);

        IDictionary pointsDict = Assert.IsType<IDictionary>(pointsObj, false);

        Assert.Equal(true, pointsDict["0"]);

        Assert.Equal("500", pointsDict["silver"]);

        Assert.Equal("150", pointsDict["gold"]);


        // 2. Revival Gift

        Dictionary<string, string> revivalParams = new() { ["f"] = "receive_revival_gift", ["cookie"] = cookie };

        FormUrlEncodedContent revivalContent = new(revivalParams);

        HttpResponseMessage revivalResponse = await client.PostAsync("client_requester.php", revivalContent);

        Assert.Equal(HttpStatusCode.OK, revivalResponse.StatusCode);


        string revivalPhpStr = await revivalResponse.Content.ReadAsStringAsync();

        object? revivalObj = PhpSerializer.Deserialize(revivalPhpStr);

        Assert.NotNull(revivalObj);

        IDictionary revivalDict = Assert.IsType<IDictionary>(revivalObj, false);

        Assert.Equal(true, revivalDict["0"]);

        Assert.Equal(true, revivalDict["gift_claimed"]);
    }


    [Fact]
    public async Task ReportPlayer_And_MatchHistory_Endpoints_Succeed()

    {
        (HttpClient client, MerrickContext db, IDatabase _, string cookie, Account account) =
            await SetupTestAccountAsync();


        // Seed PlayerStatistics

        PlayerStatistics ps = CreateDummyPlayerStats(9999, account.ID, account.Name, "Hero_Devourer");

        db.PlayerStatistics.Add(ps);

        await db.SaveChangesAsync();


        // 1. Submit RAP Report

        Dictionary<string, string> reportParams = new()

        {
            ["cookie"] = cookie,

            ["reported_nickname"] = "GrieferPlayer",

            ["match_id"] = "9999",

            ["reason"] = "Verbal Abuse",

            ["comment"] = "Unsportsmanlike conduct"
        };

        FormUrlEncodedContent reportContent = new(reportParams);

        HttpResponseMessage reportResponse = await client.PostAsync("master/report/report", reportContent);

        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);


        string reportPhpStr = await reportResponse.Content.ReadAsStringAsync();

        object? reportObj = PhpSerializer.Deserialize(reportPhpStr);

        Assert.NotNull(reportObj);

        IDictionary reportDict = Assert.IsType<IDictionary>(reportObj, false);

        Assert.Equal(true, reportDict["0"]);

        Assert.Equal("OK", reportDict["status"]);


        // 2. RAP Match List

        Dictionary<string, string> matchListParams = new() { ["cookie"] = cookie };

        FormUrlEncodedContent matchListContent = new(matchListParams);

        HttpResponseMessage matchListResponse = await client.PostAsync("rap/match_list2.php", matchListContent);

        Assert.Equal(HttpStatusCode.OK, matchListResponse.StatusCode);


        string matchListPhpStr = await matchListResponse.Content.ReadAsStringAsync();

        object? matchListObj = PhpSerializer.Deserialize(matchListPhpStr);

        Assert.NotNull(matchListObj);

        IDictionary matchListDict = Assert.IsType<IDictionary>(matchListObj, false);

        Assert.Equal(true, matchListDict["0"]);

        Assert.NotNull(matchListDict["matches"]);


        // 3. RAP Player List for Match

        Dictionary<string, string> playerListParams = new() { ["match_id"] = "9999" };

        FormUrlEncodedContent playerListContent = new(playerListParams);

        HttpResponseMessage playerListResponse = await client.PostAsync("rap/player_list.php", playerListContent);

        Assert.Equal(HttpStatusCode.OK, playerListResponse.StatusCode);


        string playerListPhpStr = await playerListResponse.Content.ReadAsStringAsync();

        object? playerListObj = PhpSerializer.Deserialize(playerListPhpStr);

        Assert.NotNull(playerListObj);

        IDictionary playerListDict = Assert.IsType<IDictionary>(playerListObj, false);

        Assert.Equal(true, playerListDict["0"]);

        Assert.Equal("9999", playerListDict["match_id"]);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task CodexGraffiti_Endpoints_Succeed()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (HttpClient client, MerrickContext _, IDatabase _, string cookie, _) = await SetupTestAccountAsync();


        // 1. Get Graffiti Wall List

        Dictionary<string, string> wallParams = new() { ["cookie"] = cookie };

        FormUrlEncodedContent wallContent = new(wallParams);

        HttpResponseMessage wallResponse =
            await client.PostAsync("master/eventgraffiti/getgraffitiwalllist", wallContent);

        Assert.Equal(HttpStatusCode.OK, wallResponse.StatusCode);


        string wallPhpStr = await wallResponse.Content.ReadAsStringAsync();

        object? wallObj = PhpSerializer.Deserialize(wallPhpStr);

        Assert.NotNull(wallObj);

        IDictionary wallDict = Assert.IsType<IDictionary>(wallObj, false);

        Assert.Equal(true, wallDict["0"]);

        Assert.NotNull(wallDict["walls"]);


        // 2. Lottery Roll

        Dictionary<string, string> lotteryParams = new() { ["cookie"] = cookie };

        FormUrlEncodedContent lotteryContent = new(lotteryParams);

        HttpResponseMessage lotteryResponse = await client.PostAsync("master/eventgraffiti/lottery", lotteryContent);

        Assert.Equal(HttpStatusCode.OK, lotteryResponse.StatusCode);


        string lotteryPhpStr = await lotteryResponse.Content.ReadAsStringAsync();

        object? lotteryObj = PhpSerializer.Deserialize(lotteryPhpStr);

        Assert.NotNull(lotteryObj);

        IDictionary lotteryDict = Assert.IsType<IDictionary>(lotteryObj, false);

        Assert.Equal(true, lotteryDict["0"]);

        Assert.NotNull(lotteryDict["won_item_name"]);


        // 3. Community Goals

        HttpResponseMessage statsResponse = await client.PostAsync("master/eventgraffiti/getgraffitistats",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);
    }


    [Fact]
    public async Task AccountManagement_And_DiscordRedirect_Succeeds()

    {
        (HttpClient client, MerrickContext db, IDatabase _, string cookie, Account account) =
            await SetupTestAccountAsync();


        // 1. Nickname Check (Available)

        string newUniqueNick = $"NewNick_{Guid.NewGuid().ToString("N")[..5]}";

        Dictionary<string, string> checkParams = new() { ["nickname"] = newUniqueNick };

        FormUrlEncodedContent checkContent = new(checkParams);

        HttpResponseMessage checkResponse = await client.PostAsync("signup/nick_check.php", checkContent);

        Assert.Equal(HttpStatusCode.OK, checkResponse.StatusCode);


        string checkPhpStr = await checkResponse.Content.ReadAsStringAsync();

        object? checkObj = PhpSerializer.Deserialize(checkPhpStr);

        Assert.NotNull(checkObj);

        IDictionary checkDict = Assert.IsType<IDictionary>(checkObj, false);

        Assert.Equal(true, checkDict["0"]);

        Assert.Equal(true, checkDict["available"]);


        // 2. Change Nickname

        Dictionary<string, string> changeNickParams = new()

        {
            ["cookie"] = cookie,
            ["new_nickname"] = newUniqueNick,
            ["password"] = DefaultTestPassword
        };

        FormUrlEncodedContent changeNickContent = new(changeNickParams);

        HttpResponseMessage changeNickResponse =
            await client.PostAsync("client_require_nickchange.php", changeNickContent);

        Assert.Equal(HttpStatusCode.OK, changeNickResponse.StatusCode);


        string changeNickPhpStr = await changeNickResponse.Content.ReadAsStringAsync();

        object? changeNickObj = PhpSerializer.Deserialize(changeNickPhpStr);

        Assert.NotNull(changeNickObj);

        IDictionary changeNickDict = Assert.IsType<IDictionary>(changeNickObj, false);

        Assert.Equal(true, changeNickDict["0"]);

        Assert.Equal(newUniqueNick, changeNickDict["nickname"]);


        // 3. Create Subaccount

        string subAccountNick = $"Sub_{Guid.NewGuid().ToString("N")[..5]}";

        Dictionary<string, string> subParams = new()

        {
            ["cookie"] = cookie,
            ["sub_nickname"] = subAccountNick,
            ["password"] = DefaultTestPassword
        };

        FormUrlEncodedContent subContent = new(subParams);

        HttpResponseMessage subResponse = await client.PostAsync("client_require_identity.php", subContent);

        Assert.Equal(HttpStatusCode.OK, subResponse.StatusCode);


        string subPhpStr = await subResponse.Content.ReadAsStringAsync();

        object? subObj = PhpSerializer.Deserialize(subPhpStr);

        Assert.NotNull(subObj);

        IDictionary subDict = Assert.IsType<IDictionary>(subObj, false);

        Assert.Equal(true, subDict["0"]);

        Assert.Equal(subAccountNick, subDict["nickname"]);


        // 4. Check Promo Code

        string promoCode = $"PROMO_{Guid.NewGuid().ToString("N")[..6]}";

        RedeemableCode rc = new() { Code = promoCode, GoldCoinsReward = 100, SilverCoinsReward = 500 };

        db.RedeemableCodes.Add(rc);

        await db.SaveChangesAsync();


        Dictionary<string, string> promoParams = new() { ["promocode"] = promoCode };

        FormUrlEncodedContent promoContent = new(promoParams);

        HttpResponseMessage promoResponse = await client.PostAsync("signup/test_promocode_check.php", promoContent);

        Assert.Equal(HttpStatusCode.OK, promoResponse.StatusCode);


        string promoPhpStr = await promoResponse.Content.ReadAsStringAsync();

        object? promoObj = PhpSerializer.Deserialize(promoPhpStr);

        Assert.NotNull(promoObj);

        IDictionary promoDict = Assert.IsType<IDictionary>(promoObj, false);

        Assert.Equal(true, promoDict["0"]);

        Assert.Equal(true, promoDict["valid"]);


        // 5. Direct Discord Web Portal Signup (No email, Discord OAuth instructions)

        HttpResponseMessage signupRedirectResponse = await client.PostAsync("signup/client_create_account.php",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.OK, signupRedirectResponse.StatusCode);


        string signupPhpStr = await signupRedirectResponse.Content.ReadAsStringAsync();

        object? signupObj = PhpSerializer.Deserialize(signupPhpStr);

        Assert.NotNull(signupObj);

        IDictionary signupDict = Assert.IsType<IDictionary>(signupObj, false);

        Assert.Equal(false, signupDict["0"]);

        Assert.Equal("DISCORD_OAUTH2", signupDict["auth_type"]);
    }


    [Fact]
    public async Task RequestSubAccount_WithInvalidPassword_ReturnsErrorCode24()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupTestAccountAsync();


        string subAccountNick = $"Sub_{Guid.NewGuid().ToString("N")[..5]}";

        Dictionary<string, string> subParams = new()

        {
            ["cookie"] = cookie,
            ["sub_nickname"] = subAccountNick,
            ["password"] = "InvalidPass999!"
        };

        FormUrlEncodedContent subContent = new(subParams);

        HttpResponseMessage subResponse = await client.PostAsync("client_require_identity.php", subContent);

        Assert.Equal(HttpStatusCode.OK, subResponse.StatusCode);


        string subPhpStr = await subResponse.Content.ReadAsStringAsync();

        object? subObj = PhpSerializer.Deserialize(subPhpStr);

        Assert.NotNull(subObj);

        IDictionary subDict = Assert.IsType<IDictionary>(subObj, false);

        Assert.Equal(false, subDict["0"]);

        Assert.Equal(0, subDict["success"]);

        Assert.Equal(24, subDict["error_code"]);

        Assert.Equal("24", subDict["errors"]);
    }


    [Fact]
    public async Task RequestSubAccount_WithMd5Password_Succeeds()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupTestAccountAsync();


        string md5Password = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(DefaultTestPassword)));

        string subAccountNick = $"Sub_{Guid.NewGuid().ToString("N")[..5]}";

        Dictionary<string, string> subParams = new()

        {
            ["cookie"] = cookie,
            ["sub_nickname"] = subAccountNick,
            ["password"] = md5Password
        };

        FormUrlEncodedContent subContent = new(subParams);

        HttpResponseMessage subResponse = await client.PostAsync("client_require_identity.php", subContent);

        Assert.Equal(HttpStatusCode.OK, subResponse.StatusCode);


        string subPhpStr = await subResponse.Content.ReadAsStringAsync();

        object? subObj = PhpSerializer.Deserialize(subPhpStr);

        Assert.NotNull(subObj);

        IDictionary subDict = Assert.IsType<IDictionary>(subObj, false);

        Assert.Equal(true, subDict["0"]);

        Assert.Equal(1, subDict["success"]);

        Assert.Equal(subAccountNick, subDict["nickname"]);
    }


    [Fact]
    public async Task RequestSubAccount_WithTooLongNickname_ReturnsErrorCode5()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupTestAccountAsync();


        string md5Password = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(DefaultTestPassword)));

        const string subAccountNick = "CosmicRevolution"; // 16 characters (max is 15)

        Dictionary<string, string> subParams = new()

        {
            ["cookie"] = cookie,
            ["sub_nickname"] = subAccountNick,
            ["password"] = md5Password
        };

        FormUrlEncodedContent subContent = new(subParams);

        HttpResponseMessage subResponse = await client.PostAsync("client_require_identity.php", subContent);

        Assert.Equal(HttpStatusCode.OK, subResponse.StatusCode);


        string subPhpStr = await subResponse.Content.ReadAsStringAsync();

        object? subObj = PhpSerializer.Deserialize(subPhpStr);

        Assert.NotNull(subObj);

        IDictionary subDict = Assert.IsType<IDictionary>(subObj, false);

        Assert.Equal(false, subDict["0"]);

        Assert.Equal(0, subDict["success"]);

        Assert.Equal(5, subDict["error_code"]);

        Assert.Equal("Nickname must be between 3 and 15 characters.", subDict["errors"]);
    }
}
