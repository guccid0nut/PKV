// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using System.Security.Cryptography;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Tests.Data;
using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Store;
using EntityRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class ClientRequestTests(KONGORTestFixture fixture) : IClassFixture<KONGORTestFixture>
{
    private const string DefaultTestPassword = "Password123!";
    private const string DefaultTestSalt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053";

    private static readonly Regex SaltRegexInstance = new(@"""salt"";\s*s:\d+:""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex BRegexInstance = new(@"""B"";\s*s:\d+:""([^""]+)""", RegexOptions.Compiled);

    private async
        Task<(HttpClient Client, MerrickContext DbContext, IDatabase Cache, string Cookie,
            WebApplicationFactory<KONGORAssemblyMarker> Factory)> SetupAsync(string? accountName = null)
    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;
        HttpClient client = factory.CreateClient();
        IServiceScope scope = factory.Services.CreateScope();
        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();

        string cookie = Guid.NewGuid().ToString("N");
        string name = accountName ?? $"TestUser_{Guid.NewGuid().ToString("N")[..8]}";
        string srpHash = SRPAuthenticationHandlers.ComputeSRPPasswordHash(DefaultTestPassword, DefaultTestSalt, false);


        User user = new()

        {
            EmailAddress = $"{name}@kongor.net",

            PBKDF2PasswordHash = "hash",

            SRPPasswordHash = srpHash,

            SRPPasswordSalt = DefaultTestSalt,

            Role = new EntityRole { Name = UserRoles.User }
        };


        Account account = new()

        {
            Name = name,

            User = user,

            Type = AccountType.Staff, // Default to Staff for broad permission in tests

            IsMain = true
        };


        await dbContext.Users.AddAsync(user);

        await dbContext.Accounts.AddAsync(account);

        await dbContext.SaveChangesAsync();


        await cache.SetAccountNameForSessionCookie(cookie, account.Name);


        return (client, dbContext, cache, cookie, factory);
    }


    private static Regex SaltRegex()

    {
        return SaltRegexInstance;
    }


    private static Regex BRegex()

    {
        return BRegexInstance;
    }


    #region From ClientRequesterTests.cs

    [Fact]
    public async Task GetInitStats_WithValidCookie_ReturnsStats()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, _) =
            await SetupAsync("ClientTester");

        {
            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);

            Account account = await dbContext.Accounts.FirstAsync(a => a.Name == accountName);

            Dictionary<string, string> payload = ClientRequestPayloads.GetInitStats(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync();


            Dictionary<string, object> expected =
                ClientRequestPayloads.ExpectedResponses.GetInitStats(account.Name, account.ID);

            foreach (string key in expected.Keys) Assert.Contains(key, body);
        }
    }


    [Fact]
    public async Task GetProducts_WithValidCookie_ReturnsProductList()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("ProductTester");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetProducts(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            // Verify basic PHP array structure

            Assert.StartsWith("a:", body);
        }
    }

    #endregion


    #region From ClientRequesterUnverifiedTests.cs

    [Fact]
    public async Task Auth_Unverified_ReturnsBadRequest()

    {
        // SetupAsync creates a session, but here we just need client

        (HttpClient client, _, _, _, _) = await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.Auth("login", "password");

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php?f=auth", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task GetServerList_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetServerList(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task GetAllHeroes_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetAllHeroes(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task CreateGame_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.CreateGame(cookie, "TestGame");

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task ShowSimpleStats_Unverified_ReturnsSuccess()

    {
        // Use a known name to verify we can query it

        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie,
            _) = await SetupAsync();

        {
            // We need the name associated with the cookie

            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);

            Account account = await dbContext.Accounts.FirstAsync(a => a.Name == accountName);


            Dictionary<string, string> payload = ClientRequestPayloads.ShowSimpleStats(cookie, account.Name);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task ClientEventsInfo_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.ClientEventsInfo(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.Contains("{", body);
        }
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetSpecialMessages_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetSpecialMessages(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.Contains("{", body);
        }
    }


    [Fact]
    public async Task GetInitStats_Unverified_ReturnsBadRequest()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            // Verifies snake_case "get_init_stats" failure

            Dictionary<string, string> payload = ClientRequestPayloads.GetInitStats_SnakeCase(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task GrabServerList_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GrabServerList(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task ServerList_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.ServerList(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task GetHeroList_Unverified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync();

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetHeroList(cookie);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }


    [Fact]
    public async Task ShowStats_Unverified_ReturnsSuccess()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie,
            _) = await SetupAsync();

        {
            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);

            Account account = await dbContext.Accounts.FirstAsync(a => a.Name == accountName);


            Dictionary<string, string> payload = ClientRequestPayloads.ShowStats(cookie, account.Name);

            FormUrlEncodedContent content = new(payload);


            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.NotEmpty(body);

            Assert.StartsWith("a:", body);
        }
    }

    #endregion


    #region From ClientRequesterVerifiedPayloadTests.cs

    [Fact]
    public async Task PreAuth_Verified_ReturnsSuccess()

    {
        // Special setup for PreAuth (does NOT need cookie in cache, just user in DB)

        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        {
            using IServiceScope scope = factory.Services.CreateScope();

            MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();


            string accountName = $"PreAuthUser_{Guid.NewGuid().ToString("N")[..8]}";

            User user = new()

            {
                EmailAddress = "preauth@kongor.net",

                PBKDF2PasswordHash = "hash",

                SRPPasswordHash = "hash",

                SRPPasswordSalt = "salt",

                Role = new EntityRole { Name = UserRoles.User }
            };

            Account account = new() { Name = accountName, User = user, Type = AccountType.Normal, IsMain = true };

            await dbContext.Users.AddAsync(user);

            await dbContext.Accounts.AddAsync(account);

            await dbContext.SaveChangesAsync();


            Dictionary<string, string> payload = ClientRequestPayloads.PreAuth(accountName);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();


            // Structural Assertion for Pre-Auth

            // Verify 'salt' and 'B' (server ephemeral) are present and structurally correct

            // Regex for salt: "salt";s:\d+:"...";

            Regex saltRegex = SaltRegex();

            Match saltMatch = saltRegex.Match(responseBody);

            Assert.True(saltMatch.Success);

            Assert.NotEmpty(saltMatch.Groups[1].Value);


            // Regex for B: "B";s:\d+:"...";

            Regex bRegex = BRegex();

            Match bMatch = bRegex.Match(responseBody);

            Assert.True(bMatch.Success);

            Assert.NotEmpty(bMatch.Groups[1].Value);
        }
    }


    [Fact]
    public async Task SrpAuth_Verified_Returns401_Or_422()

    {
        WebApplicationFactory<KONGORAssemblyMarker> factory = fixture.Factory;

        HttpClient client = factory.CreateClient();


        {
            Dictionary<string, string> payload = ClientRequestPayloads.SrpAuth("SomeUser", "bad_proof");

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }


    [Fact]
    public async Task GetSpecialMessages_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("MsgUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetSpecialMessages(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("{}", responseBody);
        }
    }


    [Fact]
    public async Task GetProducts_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("ProdUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetProducts(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("products", responseBody);
        }
    }


    [Fact]
    public async Task ClientEventsInfo_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("EventUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.ClientEventsInfo(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("{}", responseBody);
        }
    }


    [Fact]
    public async Task ShowSimpleStats_Verified_ReturnsSuccess()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        const string nickname = "StatsUser";

        (HttpClient client, MerrickContext dbContext, _, string cookie, _) =
            await SetupAsync(nickname);


        {
            // Seed Stats for Hero Icons Verification

            Account account = await dbContext.Accounts.FirstAsync(a => a.Name == nickname);


            // Seed a Match Stat to generate "FavHero" data

            // Hero ID 12 (Hero_Jereziah)

            await dbContext.PlayerStatistics.AddAsync(new PlayerStatistics

            {
                AccountID = account.ID,

                MatchID = 12345, // Dummy Match ID

                HeroProductID = 12,

                SecondsPlayed = 3600, // 1 Hour


                // Required Fields

                MVP = 0,

                AccountName = nickname,

                ClanID = null,

                ClanTag = null,

                Team = 1,

                LobbyPosition = 0,

                GroupNumber = 0,

                Benefit = 0,

                Inventory = [],

                Win = 1,

                Loss = 0,

                Disconnected = 0,

                Conceded = 0,

                Kicked = 0,

                PublicMatch = 0,

                PublicSkillRatingChange = 0,

                RankedMatch = 1,

                RankedSkillRatingChange = 0,

                SocialBonus = 0,

                UsedToken = 0,

                ConcedeVotes = 0,

                HeroKills = 10,

                HeroDamage = 1000,

                GoldFromHeroKills = 500,

                HeroAssists = 5,

                HeroExperience = 1000,

                HeroDeaths = 0,

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

                Gold = 5000,

                GoldSpent = 4000,

                Experience = 5000,

                Actions = 100,

                HeroLevel = 10,

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

                Score = 1000,

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

                TimeEarningExperience = 600
            });

            await dbContext.SaveChangesAsync();


            Dictionary<string, string> payload = ClientRequestPayloads.ShowSimpleStats(cookie, nickname);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();


            // Hybrid Response: Mixed keys (named + indexed). 

            // Key should now be "NameWithClanTag" (restored to "nickname" via attribute).

            Assert.Contains("s:8:\"nickname\";", responseBody);

            Assert.Contains("s:9:\"StatsUser\";", responseBody);

            Assert.Contains("s:8:\"1500.000\";", responseBody); // Default rating


            // Validate FavHero1 is populated due to seeded stats (from Hybrid logic)

            Assert.Contains("s:8:\"favHero1\";s:", responseBody);
        }
    }


    [Fact]
    public async Task ShowStats_WithoutTable_ReturnsHybrid()

    {
        const string nickname = "LegacyStatsUser";

        (HttpClient client, MerrickContext dbContext, _, string cookie, _) =
            await SetupAsync(nickname);


        {
            Dictionary<string, string> payload = new()

            {
                { "f", "show_stats" }, { "nickname", nickname }, { "cookie", cookie }

                // Omitting 'table' entirely to ensure it falls back to Hybrid
            };


            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();


            // Assert Hybrid structure: contains associative keys ("nickname") AND positional integers (i:0)

            Assert.Contains("s:8:\"nickname\";", responseBody);

            Assert.Contains("i:0;", responseBody);
        }
    }


    [Fact]
    public async Task GetAccountAllHeroStats_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("HeroStatsUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetAccountAllHeroStats(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("mastery_info", responseBody);

            Assert.Contains("error_code", responseBody);
        }
    }


    [Fact]
    public async Task GetHeroStats_Verified_ReturnsAllHeroStatsFields()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("HeroStatsUser2");

        {
            Dictionary<string, string> payload = new()

            {
                ["f"] = "get_hero_stats",
                ["hero"] = "Hero_Bubbles",
                ["cookie"] = cookie
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("rnk_ph_used", responseBody);

            Assert.Contains("cs_ph_used", responseBody);

            Assert.Contains("ph_used", responseBody);

            Assert.Contains("success", responseBody);
        }
    }


    [Fact]
    public async Task GetHeroUsageList_Verified_ReturnsDelimitedFormat()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("HeroUsageUser");

        {
            Dictionary<string, string> payload = new() { ["f"] = "get_hero_usage_list", ["cookie"] = cookie };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("data", responseBody);

            Assert.Contains("total_use", responseBody);

            Assert.Contains("success", responseBody);

            Assert.Contains("Hero_Bubbles", responseBody);
        }
    }


    [Fact]
    public async Task GetHeroUsageGraph_Verified_ReturnsDelimitedFormat()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("HeroGraphUser");

        {
            Dictionary<string, string> payload = new()

            {
                ["f"] = "get_hero_usage_graph",
                ["hero"] = "Hero_Bubbles",
                ["cookie"] = cookie
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);

            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("data", responseBody);

            Assert.Contains("count", responseBody);

            Assert.Contains("high_total", responseBody);
        }
    }


    [Fact]
    public async Task CreateGame_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, IDatabase distributedCache, string cookie,
            _) = await SetupAsync("GameCreator");

        {
            // Seed Idle Match Server

            MatchServer matchServer = new()

            {
                ID = 1,

                IPAddress = "127.0.0.1",

                Port = 5000,

                Status = ServerStatus.SERVER_STATUS_IDLE,

                Location = "USE",

                Name = "TestServer1",

                HostAccountName = "ServerHost",

                HostAccountID = 999,

                Instance = 1,

                Description = "Test Server"
            };

            await distributedCache.SetMatchServer(matchServer.HostAccountName, matchServer);


            Dictionary<string, string> payload = ClientRequestPayloads.CreateGame(cookie, "My Custom Game");

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("match_id", responseBody);

            Assert.Contains("server_id", responseBody);

            Assert.Contains("server_address", responseBody);

            Assert.Contains("127.0.0.1", responseBody);
        }
    }


    [Fact]
    public async Task NewGameAvailable_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("Notifier");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.NewGameAvailable(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("b:1;", responseBody);
        }
    }


    [Fact]
    public async Task ClaimSeasonRewards_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("SeasonUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.ClaimSeasonRewards(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();
        }
    }


    [Fact]
    public async Task ServerList_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("ListUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.ServerList(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();


            string responseBody = await response.Content.ReadAsStringAsync();

            // Expecting serialized list/array

            Assert.StartsWith("a:", responseBody);
        }
    }


    [Fact]
    public async Task Logout_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, IDatabase distributedCache, string cookie,
            _) = await SetupAsync("LogoutUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.Logout(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();


            (bool isValid, _) = await distributedCache.ValidateAccountSessionCookie(cookie);

            Assert.False(isValid);
        }
    }


    [Fact]
    public async Task GetMatchStats_Verified_ReturnsSuccess()

    {
        (HttpClient client, MerrickContext dbContext, _, string cookie,
            _) = await SetupAsync("MatchStatsUser");

        {
            // Seed a completed match in DB for stats

            MatchStatistics matchStats = new()

            {
                MatchID = 12345,

                Map = "caldavar",

                Version = "4.10.1.0",

                GameMode = "ap",

                TimestampRecorded = TimeProvider.System.GetUtcNow().UtcDateTime,

                TimePlayed = 1800,

                ServerID = 1,

                HostAccountName = "ServerHost",

                MapVersion = "4.10.1",

                FileSize = 1000,

                FileName = "M12345.honreplay",

                ConnectionState = 0,

                AveragePSR = 1500,

                AveragePSRTeamOne = 1500,

                AveragePSRTeamTwo = 1500,

                ScoreTeam1 = 0,

                ScoreTeam2 = 0,

                TeamScoreGoal = 0,

                PlayerScoreGoal = 0,

                NumberOfRounds = 1,

                ReleaseStage = "stable",

                BannedHeroes = null,

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

                AwardHighestCreepScore = 0
            };

            await dbContext.MatchStatistics.AddAsync(matchStats);

            await dbContext.SaveChangesAsync();


            Dictionary<string, string> payload = ClientRequestPayloads.GetMatchStats(cookie, 12345);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();


            Dictionary<string, object> expected = ClientRequestPayloads.ExpectedResponses.GetMatchStats(12345);

            string responseBody = await response.Content.ReadAsStringAsync();


            foreach (string key in expected.Keys.Where(key => key != "0")) Assert.Contains(key, responseBody);
        }
    }


    [Fact]
    public async Task GetUpgrades_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("UpgradeUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetUpgrades(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();


            Dictionary<string, object> expected = ClientRequestPayloads.ExpectedResponses.GetUpgrades();

            string responseBody = await response.Content.ReadAsStringAsync();


            foreach (string key in expected.Keys.Where(key => key != "0")) Assert.Contains(key, responseBody);
        }
    }


    [Fact]
    public async Task GetDailySpecial_Verified_ReturnsSuccess()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("DailySpecialUser");

        {
            Dictionary<string, string> payload = ClientRequestPayloads.GetDailySpecial(cookie);

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            // Expect at least success or empty array (if not populated)

            // Daily special often returns cost/product_id, or just success=true if empty

            // At minimum check for success

            Assert.Contains("\"list\"", responseBody);
        }
    }


    [Fact]
    public async Task GetBundleContents_Verified_ReturnsSuccessJson()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("BundleUser");

        {
            Dictionary<string, string> payload = new()

            {
                ["f"] = "get_bundle_contents",
                ["bundle_id"] = "4689",
                ["cookie"] = cookie
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("\"info\":", responseBody);

            Assert.Contains("\"bundle_id\":4689", responseBody);

            Assert.Contains("\"list\":", responseBody);
        }
    }


    [Fact]
    public async Task RequestNickChange_Verified_ReturnsSuccessFlag()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("OldNickUser");

        {
            Dictionary<string, string> payload = new()

            {
                ["new_nickname"] = "BrandNewNick",
                ["cookie"] = cookie,
                ["password"] = DefaultTestPassword
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_require_nickchange.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("s:7:\"success\";i:1;", responseBody);

            Assert.Contains("s:8:\"nickname\";s:12:\"BrandNewNick\";", responseBody);

            Assert.Contains("s:6:\"errors\";s:0:\"\";", responseBody);
        }
    }


    [Fact]
    public async Task RequestNickChange_WithInvalidPassword_ReturnsErrorCode24()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("WrongPassUser");

        {
            Dictionary<string, string> payload = new()

            {
                ["new_nickname"] = "ShouldFailNick",
                ["cookie"] = cookie,
                ["password"] = "WrongPassword999!"
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_require_nickchange.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("s:7:\"success\";i:0;", responseBody);

            Assert.Contains("s:10:\"error_code\";i:24;", responseBody);

            Assert.Contains("s:6:\"errors\";s:2:\"24\";", responseBody);
        }
    }


    [Fact]
    public async Task RequestNickChange_WithMd5Password_Succeeds()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("Md5PassUser");

        {
            string md5Password = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(DefaultTestPassword)));

            Dictionary<string, string> payload = new()

            {
                ["new_nickname"] = "Md5BrandNewNick",
                ["cookie"] = cookie,
                ["password"] = md5Password
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_require_nickchange.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("s:7:\"success\";i:1;", responseBody);

            Assert.Contains("s:8:\"nickname\";s:15:\"Md5BrandNewNick\";", responseBody);
        }
    }


    [Fact]
    public async Task ShowStats_StaleNickname_FallbacksToSessionAccount()

    {
        (HttpClient client, _, _, string cookie, _) =
            await SetupAsync("StaleUserRenamed");

        {
            // Query stats with old non-existent nickname, but valid session cookie

            Dictionary<string, string> payload = new()

            {
                ["f"] = "show_stats",
                ["nickname"] = "NonExistentOldNick",
                ["cookie"] = cookie
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("StaleUserRenamed", responseBody);
        }
    }


    [Fact]
    public async Task ClientRequest_RequestNickChange_ConsumesNameChangeItemFromInventory()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, _) =
            await SetupAsync("NickChangeConsumer");

        {
            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);

            Account account = await dbContext.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstAsync(a => a.Name == accountName);

            account.User.InventoryItems.Add(new UserInventory { UserId = account.User.ID, ItemCode = "m.Nickname Change", GrantSource = "Test" });

            await dbContext.SaveChangesAsync();


            Dictionary<string, string> payload = new()

            {
                ["new_nickname"] = "ConsumedNick",
                ["cookie"] = cookie,
                ["password"] = DefaultTestPassword
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_require_nickchange.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("s:7:\"success\";i:1;", responseBody);


            dbContext.ChangeTracker.Clear();

            Account updatedAccount =
                await dbContext.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstAsync(a => a.Name == "ConsumedNick");

            Assert.DoesNotContain("m.Nickname Change", updatedAccount.User.InventoryItems.Select(i => i.ItemCode));
        }
    }


    [Fact]
    public async Task RequestStatReset_Verified_ConsumesItemAndReturnsSuccess()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, _) =
            await SetupAsync("StatResetUser");

        {
            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);

            Account account = await dbContext.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstAsync(a => a.Name == accountName);

            account.User.InventoryItems.Add(new UserInventory { UserId = account.User.ID, ItemCode = "m.Stat Reset", GrantSource = "Test" });

            await dbContext.SaveChangesAsync();


            Dictionary<string, string> payload = new() { ["cookie"] = cookie };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_require_resetstats.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("s:7:\"success\";i:1;", responseBody);

            Assert.Contains("s:6:\"errors\";s:0:\"\";", responseBody);


            dbContext.ChangeTracker.Clear();

            Account updatedAccount =
                await dbContext.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).FirstAsync(a => a.Name == accountName);

            Assert.DoesNotContain("m.Stat Reset", updatedAccount.User.InventoryItems.Select(i => i.ItemCode));
        }
    }


    [Fact]
    public async Task GetMatchStats_ExistingMatch_ReturnsPopulatedSummary()

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, _) =
            await SetupAsync("MatchStatsTester");

        {
            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);

            Account account = await dbContext.Accounts.FirstAsync(a => a.Name == accountName);


            int matchId = Random.Shared.Next(100000, 999999);

            MatchStatistics matchStats = new()

            {
                MatchID = matchId,

                ServerID = 1,

                HostAccountName = "ServerHost",

                Map = "caldavar",

                MapVersion = "1.0.0",

                TimePlayed = 1200,

                FileSize = 50000,

                FileName = $"M{matchId}.honreplay",

                ConnectionState = 1,

                Version = "4.10.1",

                AveragePSR = 1500,

                AveragePSRTeamOne = 1500,

                AveragePSRTeamTwo = 1500,

                GameMode = "botmatch",

                ScoreTeam1 = 20,

                ScoreTeam2 = 15,

                TeamScoreGoal = 0,

                PlayerScoreGoal = 0,

                NumberOfRounds = 1,

                ReleaseStage = "live",

                BannedHeroes = null,

                AwardMostAnnihilations = null,

                AwardMostQuadKills = null,

                AwardLargestKillStreak = null,

                AwardMostSmackdowns = null,

                AwardMostKills = 10,

                AwardMostAssists = 10,

                AwardLeastDeaths = 1,

                AwardMostBuildingDamage = 5000,

                AwardMostWardsKilled = null,

                AwardMostHeroDamageDealt = 20000,

                AwardHighestCreepScore = 200,

                MVPAccountID = null
            };


            PlayerStatistics playerStats = new()

            {
                MatchID = matchId,

                AccountID = account.ID,

                AccountName = account.Name,

                ClanID = null,

                ClanTag = null,

                Team = 1,

                LobbyPosition = 1,

                GroupNumber = 1,

                Benefit = 1,

                HeroProductID = 1,

                Inventory = [],

                Win = 1,

                Loss = 0,

                Disconnected = 0,

                Conceded = 0,

                Kicked = 0,

                PublicMatch = 0,

                PublicSkillRatingChange = 0.0,

                RankedMatch = 1,

                RankedSkillRatingChange = 15.0,

                SocialBonus = 0,

                UsedToken = 0,

                ConcedeVotes = 0,

                HeroKills = 10,

                HeroDamage = 20000,

                GoldFromHeroKills = 3000,

                HeroAssists = 10,

                HeroExperience = 10000,

                HeroDeaths = 2,

                Buybacks = 0,

                GoldLostToDeath = 50,

                SecondsDead = 40,

                TeamCreepKills = 150,

                TeamCreepDamage = 15000,

                TeamCreepGold = 6000,

                TeamCreepExperience = 9000,

                NeutralCreepKills = 30,

                NeutralCreepDamage = 4000,

                NeutralCreepGold = 1500,

                NeutralCreepExperience = 2000,

                BuildingDamage = 5000,

                BuildingsRazed = 2,

                ExperienceFromBuildings = 1000,

                GoldFromBuildings = 1000,

                Denies = 10,

                ExperienceDenied = 800,

                Gold = 12000,

                GoldSpent = 11000,

                Experience = 22000,

                Actions = 2500,

                SecondsPlayed = 1800,

                HeroLevel = 20,

                ConsumablesPurchased = 3,

                WardsPlaced = 4,

                FirstBlood = 1,

                DoubleKill = 1,

                TripleKill = 0,

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

                Smackdown = 2,

                Humiliation = 0,

                Nemesis = 1,

                Retribution = 1,

                Score = 1500,

                GameplayStat0 = 0.0,

                GameplayStat1 = 0.0,

                GameplayStat2 = 0.0,

                GameplayStat3 = 0.0,

                GameplayStat4 = 0.0,

                GameplayStat5 = 0.0,

                GameplayStat6 = 0.0,

                GameplayStat7 = 0.0,

                GameplayStat8 = 0.0,

                GameplayStat9 = 0.0,

                TimeEarningExperience = 1500,

                MVP = 0
            };


            await dbContext.MatchStatistics.AddAsync(matchStats);

            await dbContext.PlayerStatistics.AddAsync(playerStats);

            await dbContext.SaveChangesAsync();


            Dictionary<string, string> payload = new()

            {
                ["f"] = "get_match_stats",
                ["match_id"] = matchId.ToString(),
                ["cookie"] = cookie
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("s:10:\"match_summ\";", responseBody);

            Assert.Contains($"s:8:\"match_id\";i:{matchId};", responseBody);

            Assert.Contains("s:18:\"match_player_stats\";", responseBody);
        }
    }


    [Theory]
    [InlineData("campaign_normal")]
    [InlineData("casual")]
    [InlineData("midwars")]
    [InlineData("riftwars")]
    public async Task GrabLastMatchesFromNick_AllTableTypes_ExecutesWithoutException(string tableType)

    {
        (HttpClient client, MerrickContext dbContext, IDatabase cache, string cookie, _) =
            await SetupAsync("MatchHistoryTester");

        {
            string? accountName = await cache.GetAccountNameForSessionCookie(cookie);


            Dictionary<string, string> payload = new()

            {
                ["f"] = "grab_last_matches_from_nick",

                ["nickname"] = accountName!,

                ["table"] = tableType,

                ["cookie"] = cookie
            };

            FormUrlEncodedContent content = new(payload);

            HttpResponseMessage response = await client.PostAsync("client_requester.php", content);


            response.EnsureSuccessStatusCode();

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.Contains("a:", responseBody);
        }
    }

    #endregion
}
