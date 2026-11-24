// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Security.Claims;
using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Controllers;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Clients;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class HomeControllerTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static User CreateTestUser(string email = "test@kongor.net")
    {
        return new User
        {
            EmailAddress = email,
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
    }

    private static HomeController CreateController(MerrickContext context, string? userName = null,
        IConfiguration? config = null)
    {
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller =
            new(statsApi, userApi, guideApi, adminApi, config ?? new ConfigurationBuilder().Build());
        DefaultHttpContext httpContext = new();
        if (!string.IsNullOrEmpty(userName))
        {
            ClaimsIdentity identity = new([
                new Claim(ClaimTypes.Name, userName)
            ], "TestAuth");
            httpContext.User = new ClaimsPrincipal(identity);
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static PlayerStatistics CreateDefaultPlayerStatistics(int accountId, int matchId, string accountName,
        string heroIdentifier = "Hero_Vindicator")
    {
        return new PlayerStatistics
        {
            MatchID = matchId,
            AccountID = accountId,
            AccountName = accountName,
            ClanID = null,
            ClanTag = "PK",
            Team = 1,
            LobbyPosition = 0,
            GroupNumber = 0,
            Benefit = 0,
            HeroProductID = 0,
            MVP = 0,
            HeroIdentifier = heroIdentifier,
            Inventory = ["Item_LoggersHatchet", "Item_Marchers"],
            Win = 0,
            Loss = 1,
            Disconnected = 0,
            Conceded = 0,
            Kicked = 0,
            PublicMatch = 1,
            PublicSkillRatingChange = 1500,
            RankedMatch = 0,
            RankedSkillRatingChange = 0,
            SocialBonus = 0,
            UsedToken = 0,
            ConcedeVotes = 0,
            HeroKills = 7,
            HeroDamage = 12000,
            GoldFromHeroKills = 2000,
            HeroAssists = 4,
            HeroExperience = 15000,
            HeroDeaths = 4,
            Buybacks = 0,
            GoldLostToDeath = 500,
            SecondsDead = 120,
            TeamCreepKills = 150,
            TeamCreepDamage = 20000,
            TeamCreepGold = 6000,
            TeamCreepExperience = 10000,
            NeutralCreepKills = 20,
            NeutralCreepDamage = 3000,
            NeutralCreepGold = 1000,
            NeutralCreepExperience = 2000,
            BuildingDamage = 1500,
            BuildingsRazed = 1,
            ExperienceFromBuildings = 500,
            GoldFromBuildings = 800,
            Denies = 15,
            ExperienceDenied = 600,
            Gold = 500,
            GoldSpent = 8500,
            Experience = 25000,
            Actions = 6000,
            SecondsPlayed = 1800,
            HeroLevel = 25,
            ConsumablesPurchased = 5,
            WardsPlaced = 4,
            FirstBlood = 0,
            DoubleKill = 1,
            TripleKill = 0,
            QuadKill = 0,
            Annihilation = 0,
            KillStreak03 = 1,
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
            Score = 100,
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
            TimeEarningExperience = 1800
        };
    }

    private static MatchStatistics CreateDefaultMatchStatistics(int matchId)
    {
        return new MatchStatistics
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "ServerHost",
            Map = "caldavar",
            MapVersion = "1.0",
            TimePlayed = 1800,
            FileSize = 500000,
            FileName = $"M{matchId}.honreplay",
            ReplayUrl = $"http://localhost/replays/M{matchId}.honreplay",
            ConnectionState = 1,
            Version = "4.10.0",
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            GameMode = "Public 5v5",
            ScoreTeam1 = 25,
            ScoreTeam2 = 40,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "release",
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
    }

    [Fact]
    public async Task Matches_WhenUserIsAuthenticated_ReturnsViewWithRecentMatches()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "player1@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account account = new() { Name = "GUEST-01", IsMain = true, User = user };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        PlayerStatistics playerStat = CreateDefaultPlayerStatistics(account.ID, 206056652, account.Name);
        context.PlayerStatistics.Add(playerStat);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "GUEST-01");

        IActionResult result = await controller.Matches();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        List<PlayerStatistics>? recentMatches = viewResult.ViewData["RecentMatches"] as List<PlayerStatistics>;
        Assert.NotNull(recentMatches);
        Assert.Single(recentMatches);
        Assert.Equal(206056652, recentMatches[0].MatchID);
    }

    [Fact]
    public async Task MatchDetails_WithValidMatchId_ReturnsViewWithMatchData()
    {
        await using MerrickContext context = CreateInMemoryContext();
        MatchStatistics matchStat = CreateDefaultMatchStatistics(206056652);
        context.MatchStatistics.Add(matchStat);

        PlayerStatistics player1 = CreateDefaultPlayerStatistics(101, 206056652, "GUEST-01");
        context.PlayerStatistics.Add(player1);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "GUEST-01");

        IActionResult result = await controller.MatchDetails(206056652);

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        MatchStatistics? retrievedMatch = viewResult.ViewData["Match"] as MatchStatistics;
        List<PlayerStatistics>? retrievedPlayers = viewResult.ViewData["PlayerStats"] as List<PlayerStatistics>;

        Assert.NotNull(retrievedMatch);
        Assert.Equal(206056652, retrievedMatch.MatchID);
        Assert.NotNull(retrievedPlayers);
        Assert.Single(retrievedPlayers);
        Assert.Equal("GUEST-01", retrievedPlayers[0].AccountName);
    }

    [Fact]
    public async Task MatchDetails_WithZeroMatchId_RedirectsToMatches()
    {
        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context, "GUEST-01");

        IActionResult result = await controller.MatchDetails();

        RedirectToActionResult redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Matches", redirectResult.ActionName);
    }

    [Fact]
    public async Task MatchDetails_WithNonExistentMatchId_ReturnsNotFound()
    {
        await using MerrickContext context = CreateInMemoryContext();
        HomeController controller = CreateController(context);

        IActionResult result = await controller.MatchDetails(999999);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Stats_WithDiscordUser_PopulatesAvatarUrlAndDiscordUsername()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "discorduser@kongor.net",
            DiscordId = "123456789",
            DiscordAvatar = "abcdef123",
            DiscordUsername = "guccidonut",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account account = new() { Name = "guccidonut", IsMain = true, User = user };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "guccidonut");

        IActionResult result = await controller.Stats("guccidonut");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? avatarUrl = viewResult.ViewData["AvatarUrl"] as string;
        string? discordUsername = viewResult.ViewData["DiscordUsername"] as string;
        string? discordId = viewResult.ViewData["DiscordId"] as string;

        Assert.NotNull(avatarUrl);
        Assert.Equal("https://cdn.discordapp.com/avatars/123456789/abcdef123.png?size=256", avatarUrl);
        Assert.Equal("guccidonut", discordUsername);
        Assert.Null(discordId); // Guarded for non-staff
    }

    [Fact]
    public async Task Stats_WhenStaff_ExposesDiscordId()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "discorduser@kongor.net",
            DiscordId = "123456789",
            DiscordAvatar = "abcdef123",
            DiscordUsername = "guccidonut",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "Administrator" }
        };
        Account account = new() { Name = "guccidonut", IsMain = true, User = user };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "guccidonut");
        ClaimsPrincipal staffPrincipal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "guccidonut"),
            new Claim("is_staff", "true")
        ], "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = staffPrincipal }
        };

        IActionResult result = await controller.Stats("guccidonut");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? discordId = viewResult.ViewData["DiscordId"] as string;
        Assert.Equal("123456789", discordId);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HomeController_SearchPlayers_ReturnsAvatarUrlInSearchResults()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "searchuser@kongor.net",
            DiscordId = "987654321",
            DiscordAvatar = "a_animated123",
            DiscordUsername = "searchguy",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account account = new() { Name = "ClickBotAlpha", IsMain = true, User = user };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        IActionResult result = await controller.SearchPlayers("Click");

        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        IEnumerable<object>? items = jsonResult.Value as IEnumerable<object>;
        Assert.NotNull(items);
        List<object> itemList = [.. items];
        Assert.Single(itemList);

        object firstItem = itemList[0];
        string itemJson = JsonSerializer.Serialize(firstItem);
        using JsonDocument doc = JsonDocument.Parse(itemJson);
        string avatarUrl = doc.RootElement.GetProperty("avatarUrl").GetString() ?? "";

        Assert.Equal("https://cdn.discordapp.com/avatars/987654321/a_animated123.gif", avatarUrl);
    }

    [Fact]
    public async Task Stats_WithoutDiscordUser_PopulatesDefaultAvatarUrl()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "nodiscord@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account account = new() { Name = "GUEST-01", IsMain = true, User = user };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "GUEST-01");

        IActionResult result = await controller.Stats("GUEST-01");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? avatarUrl = viewResult.ViewData["AvatarUrl"] as string;

        Assert.NotNull(avatarUrl);
        Assert.Equal("/images/default-avatar.png", avatarUrl);
    }

    [Fact]
    public async Task Stats_ActivePlayerWithNoReports_SetsActiveStatusAndRoleModelBadge()
    {
        await using MerrickContext context = CreateInMemoryContext();
        Account account = new()
        {
            Name = "GoodPlayer",
            IsMain = true,
            Type = AccountType.Legacy,
            User = CreateTestUser("good@kongor.net")
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        IActionResult result = await controller.Stats("GoodPlayer");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? playerStatus = viewResult.ViewData["PlayerStatus"] as string;
        bool isRoleModel = Convert.ToBoolean(viewResult.ViewData["IsRoleModel"]);
        List<PlayerReport>? disciplinaryReports = viewResult.ViewData["DisciplinaryReports"] as List<PlayerReport>;

        Assert.Equal("Active", playerStatus);
        Assert.True(isRoleModel);
        Assert.NotNull(disciplinaryReports);
        Assert.Empty(disciplinaryReports);
    }

    [Fact]
    public async Task Stats_BannedPlayerWithResolvedReports_SetsBannedStatusAndDisciplinaryHistory()
    {
        await using MerrickContext context = CreateInMemoryContext();
        DateTimeOffset banExpiry = TimeProvider.System.GetUtcNow().AddDays(7);
        Account account = new()
        {
            Name = "BadActor",
            IsMain = true,
            Type = AccountType.Disabled,
            BanExpiration = banExpiry,
            BanReason = "Toxic Behavior & Griefing",
            User = CreateTestUser("bad@kongor.net")
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        PlayerReport report = new()
        {
            ReportedAccountID = account.ID,
            ReportedAccountName = account.Name,
            ReporterAccountID = 100,
            ReporterAccountName = "VictimPlayer",
            MatchID = 998877,
            Reason = "Griefing",
            Description = "Ran down mid repeatedly",
            Status = "RESOLVED",
            PunishmentType = "BAN",
            PunishmentDurationHours = 168,
            PunishmentExpiration = banExpiry,
            ReviewedByStaffName = "StaffAdmin",
            StaffNotes = "Confirmed intentional feed on replay"
        };
        context.PlayerReports.Add(report);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        IActionResult result = await controller.Stats("BadActor");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? playerStatus = viewResult.ViewData["PlayerStatus"] as string;
        string? playerStatusReason = viewResult.ViewData["PlayerStatusReason"] as string;
        DateTimeOffset? playerStatusExpiration = viewResult.ViewData["PlayerStatusExpiration"] as DateTimeOffset?;
        bool isRoleModel = Convert.ToBoolean(viewResult.ViewData["IsRoleModel"]);
        List<PlayerReport>? disciplinaryReports = viewResult.ViewData["DisciplinaryReports"] as List<PlayerReport>;

        Assert.Equal("Banned", playerStatus);
        Assert.Equal("Toxic Behavior & Griefing", playerStatusReason);
        Assert.Equal(banExpiry, playerStatusExpiration);
        Assert.False(isRoleModel);
        Assert.NotNull(disciplinaryReports);
        Assert.Single(disciplinaryReports);
        Assert.Equal("BAN", disciplinaryReports[0].PunishmentType);
        Assert.Equal(998877, disciplinaryReports[0].MatchID);
    }

    [Fact]
    public async Task Stats_SuspendedPlayer_SetsSuspendedStatus()
    {
        await using MerrickContext context = CreateInMemoryContext();
        DateTimeOffset suspExpiry = TimeProvider.System.GetUtcNow().AddHours(24);
        Account account = new()
        {
            Name = "SuspendedPlayer",
            IsMain = true,
            Type = AccountType.Legacy,
            SuspensionExpiration = suspExpiry,
            SuspensionReason = "AFK / Leaving",
            User = CreateTestUser("suspended@kongor.net")
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        IActionResult result = await controller.Stats("SuspendedPlayer");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? playerStatus = viewResult.ViewData["PlayerStatus"] as string;
        string? playerStatusReason = viewResult.ViewData["PlayerStatusReason"] as string;

        Assert.Equal("Suspended", playerStatus);
        Assert.Equal("AFK / Leaving", playerStatusReason);
    }

    [Fact]
    public async Task HomeController_SearchPlayers_ReturnsStatusInSearchResults()
    {
        await using MerrickContext context = CreateInMemoryContext();
        Account account1 = new()
        {
            Name = "CleanUser",
            IsMain = true,
            Type = AccountType.Legacy,
            User = CreateTestUser("clean@kongor.net")
        };
        Account account2 = new()
        {
            Name = "BannedUser",
            IsMain = true,
            Type = AccountType.Disabled,
            BanExpiration = TimeProvider.System.GetUtcNow().AddYears(1),
            User = CreateTestUser("banned@kongor.net")
        };
        context.Accounts.AddRange(account1, account2);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        IActionResult result = await controller.SearchPlayers("User");

        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        IEnumerable<object>? items = jsonResult.Value as IEnumerable<object>;
        Assert.NotNull(items);
        List<object> itemList = [.. items];
        Assert.Equal(2, itemList.Count);

        string json1 = JsonSerializer.Serialize(itemList[0]);
        using JsonDocument doc1 = JsonDocument.Parse(json1);
        string name1 = doc1.RootElement.GetProperty("name").GetString() ?? "";
        string status1 = doc1.RootElement.GetProperty("status").GetString() ?? "";

        string json2 = JsonSerializer.Serialize(itemList[1]);
        using JsonDocument doc2 = JsonDocument.Parse(json2);
        string name2 = doc2.RootElement.GetProperty("name").GetString() ?? "";
        string status2 = doc2.RootElement.GetProperty("status").GetString() ?? "";

        if (name1 == "CleanUser")
        {
            Assert.Equal("Active", status1);
            Assert.Equal("Banned", status2);
        }
        else
        {
            Assert.Equal("Banned", status1);
            Assert.Equal("Active", status2);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Stats_PlayerWithOnlyAppealedReports_MaintainsRoleModelBadge()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext context = CreateInMemoryContext();
        Account account = new()
        {
            Name = "ExoneratedPlayer",
            IsMain = true,
            Type = AccountType.Legacy,
            User = CreateTestUser("exonerated@kongor.net")
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        PlayerReport report = new()
        {
            ReportedAccountID = account.ID,
            ReportedAccountName = account.Name,
            ReporterAccountID = 200,
            ReporterAccountName = "FalseAccuser",
            Reason = "Griefing",
            Description = "Mistaken report",
            Status = "APPEALED",
            PunishmentType = "APPEAL",
            ReviewedByStaffName = "StaffAdmin",
            StaffNotes = "[Appeal Granted] Verified innocence upon replay inspection"
        };
        context.PlayerReports.Add(report);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        IActionResult result = await controller.Stats("ExoneratedPlayer");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? playerStatus = viewResult.ViewData["PlayerStatus"] as string;
        bool isRoleModel = Convert.ToBoolean(viewResult.ViewData["IsRoleModel"]);
        List<PlayerReport>? disciplinaryReports = viewResult.ViewData["DisciplinaryReports"] as List<PlayerReport>;

        Assert.Equal("Active", playerStatus);
        Assert.True(isRoleModel); // Appealed reports do not strip Role Model status!
        Assert.NotNull(disciplinaryReports);
        Assert.Single(disciplinaryReports);
        Assert.Equal("APPEALED", disciplinaryReports[0].Status);
    }

    [Fact]
    public async Task Stats_WhenViewedByStaff_SetsIsStaffTrue()
    {
        await using MerrickContext context = CreateInMemoryContext();
        Account account = new()
        {
            Name = "TargetPlayer",
            IsMain = true,
            Type = AccountType.Legacy,
            User = CreateTestUser("target@kongor.net")
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder().Build();
        HomeController controller = CreateController(context, null, config);
        ClaimsIdentity identity = new([
            new Claim(ClaimTypes.Name, "staffmember"),
            new Claim("is_staff", "true"),
            new Claim(ClaimTypes.Role, "Staff")
        ], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        IActionResult result = await controller.Stats("TargetPlayer");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        bool isStaff = Convert.ToBoolean(viewResult.ViewData["IsStaff"]);

        Assert.True(isStaff);
    }

    [Fact]
    public async Task Stats_SubAccountDisciplinarySanctions_ReflectedOnAssociatedAccounts()
    {
        await using MerrickContext context = CreateInMemoryContext();
        User testUser = CreateTestUser("shareduser@kongor.net");
        Account mainAccount = new() { Name = "MainHero", IsMain = true, Type = AccountType.Legacy, User = testUser };
        Account altAccount = new()
        {
            Name = "AltTroll",
            IsMain = false,
            Type = AccountType.Disabled,
            BanExpiration = TimeProvider.System.GetUtcNow().AddDays(7),
            BanReason = "Severe toxicity on alt",
            User = testUser
        };
        context.Accounts.AddRange(mainAccount, altAccount);
        await context.SaveChangesAsync();

        PlayerReport report = new()
        {
            ReportedAccountID = altAccount.ID,
            ReportedAccountName = altAccount.Name,
            ReporterAccountID = 999,
            ReporterAccountName = "Victim",
            Reason = "Toxicity",
            Description = "Flaming in chat",
            Status = "RESOLVED",
            PunishmentType = "BAN",
            PunishmentDurationHours = 168,
            PunishmentExpiration = TimeProvider.System.GetUtcNow().AddDays(7)
        };
        context.PlayerReports.Add(report);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        // When viewing the main account's stats, the associated alt account's ban is reflected!
        IActionResult result = await controller.Stats("MainHero");

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? playerStatus = viewResult.ViewData["PlayerStatus"] as string;
        bool isRoleModel = Convert.ToBoolean(viewResult.ViewData["IsRoleModel"]);
        List<PlayerReport>? disciplinaryReports = viewResult.ViewData["DisciplinaryReports"] as List<PlayerReport>;

        Assert.Equal("Banned", playerStatus);
        Assert.False(isRoleModel);
        Assert.NotNull(disciplinaryReports);
        Assert.Single(disciplinaryReports);
        Assert.Equal("AltTroll", disciplinaryReports[0].ReportedAccountName);
    }

    [Fact]
    public async Task Index_WithMultipleLinkedAccounts_PopulatesLinkedAccountsViewData()
    {
        MerrickContext context = CreateInMemoryContext();
        User testUser = CreateTestUser("slotuser@nexus.com");
        context.Users.Add(testUser);

        Account mainAccount = new() { Name = "JackpotKing", IsMain = true, Type = AccountType.Normal, User = testUser };
        Account alt1 = new() { Name = "SlotAltOne", IsMain = false, Type = AccountType.Normal, User = testUser };
        Account alt2 = new() { Name = "SlotAltTwo", IsMain = false, Type = AccountType.Normal, User = testUser };

        context.Accounts.AddRange(mainAccount, alt1, alt2);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "JackpotKing");

        IActionResult result = await controller.Index();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        bool hasMultipleAccounts = viewResult.ViewData["HasMultipleAccounts"] as bool? == true;
        List<string>? linkedAccounts = viewResult.ViewData["LinkedAccounts"] as List<string>;
        string? mainAccountName = viewResult.ViewData["MainAccountName"] as string;

        Assert.True(hasMultipleAccounts);
        Assert.NotNull(linkedAccounts);
        Assert.Equal(3, linkedAccounts.Count);
        Assert.Equal("JackpotKing", mainAccountName);
        Assert.Contains("JackpotKing", linkedAccounts);
        Assert.Contains("SlotAltOne", linkedAccounts);
        Assert.Contains("SlotAltTwo", linkedAccounts);
    }

    [Fact]
    public async Task Index_WithSingleAccount_DoesNotTriggerMultiAccountFlag()
    {
        MerrickContext context = CreateInMemoryContext();
        User testUser = CreateTestUser("solouser@nexus.com");
        context.Users.Add(testUser);

        Account soloAccount = new() { Name = "SoloWarrior", IsMain = true, Type = AccountType.Normal, User = testUser };
        context.Accounts.Add(soloAccount);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "SoloWarrior");

        IActionResult result = await controller.Index();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        bool hasMultipleAccounts = viewResult.ViewData["HasMultipleAccounts"] as bool? == true;
        List<string>? linkedAccounts = viewResult.ViewData["LinkedAccounts"] as List<string>;

        Assert.False(hasMultipleAccounts);
        Assert.Null(linkedAccounts);
    }

    [Fact]
    public async Task HomeController_Index_PopulatesMainHero3DModelAvatarsViewData()
    {
        MerrickContext context = CreateInMemoryContext();
        User testUser = CreateTestUser("hero3d@nexus.com");
        context.Users.Add(testUser);

        Account account = new() { Name = "AvatarTester", IsMain = true, Type = AccountType.Normal, User = testUser };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        PlayerStatistics stat = CreateDefaultPlayerStatistics(account.ID, 1001, "AvatarTester", "Hero_Bubbles");
        context.PlayerStatistics.Add(stat);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context, "AvatarTester");

        IActionResult result = await controller.Index();

        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        string? mainHeroGlbUrl = viewResult.ViewData["MainHeroGlbUrl"] as string;
        string? mainHeroAvatarName = viewResult.ViewData["MainHeroAvatarName"] as string;
        List<HeroAvatarModelDto>? avatars = viewResult.ViewData["MainHeroAvatars"] as List<HeroAvatarModelDto>;

        Assert.NotNull(mainHeroGlbUrl);
        Assert.Contains("bubbles", mainHeroGlbUrl);
        Assert.NotNull(mainHeroAvatarName);
        Assert.NotNull(avatars);
        Assert.NotEmpty(avatars);
        Assert.Equal("Bubbles", avatars[0].Name);
        Assert.Equal("/api/v1/assets/3d/hero/bubbles", avatars[0].GlbUrl);
    }

    [Fact]
    public async Task SearchGlobal_FindsNewlyCreatedAccounts_AndMatchesDiscordUsernameAndHandles()
    {
        await using MerrickContext context = CreateInMemoryContext();

        User userLelouch = new()
        {
            EmailAddress = "d_12345678901234567",
            DiscordId = "12345678901234567",
            DiscordUsername = "lelouch0206",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account accountLelouch =
            new() { Name = "Lelouch", IsMain = true, Type = AccountType.Legacy, User = userLelouch };
        Account accountSuzaku =
            new() { Name = "Suzaku", IsMain = false, Type = AccountType.Legacy, User = userLelouch };

        User userClickBotAlpha = new()
        {
            EmailAddress = "d_98765432109876543",
            DiscordId = "98765432109876543",
            DiscordUsername = "clickbot_alpha_01",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account accountClickBotAlpha = new()
        {
            Name = "ClickBotAlpha",
            IsMain = true,
            Type = AccountType.Legacy,
            User = userClickBotAlpha
        };

        User userPrefix = CreateTestUser("prefix@nexus.com");
        Account accountPrefix =
            new() { Name = "User_Newbie", IsMain = true, Type = AccountType.Normal, User = userPrefix };

        context.Users.AddRange(userLelouch, userClickBotAlpha, userPrefix);
        context.Accounts.AddRange(accountLelouch, accountSuzaku, accountClickBotAlpha, accountPrefix);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        // 1. Search by lowercase partial IGN "lelo"
        IActionResult resultLelo = await controller.SearchGlobal("lelo");
        JsonResult jsonLelo = Assert.IsType<JsonResult>(resultLelo);
        List<object>? listLelo = jsonLelo.Value as List<object>;
        Assert.NotNull(listLelo);
        Assert.NotEmpty(listLelo);

        // 2. Search by full Discord username "lelouch0206"
        IActionResult resultDiscord = await controller.SearchGlobal("lelouch0206");
        JsonResult jsonDiscord = Assert.IsType<JsonResult>(resultDiscord);
        List<object>? listDiscord = jsonDiscord.Value as List<object>;
        Assert.NotNull(listDiscord);
        Assert.NotEmpty(listDiscord);

        // 3. Search with '@' handle "@lelouch0206"
        IActionResult resultHandle = await controller.SearchGlobal("@lelouch0206");
        JsonResult jsonHandle = Assert.IsType<JsonResult>(resultHandle);
        List<object>? listHandle = jsonHandle.Value as List<object>;
        Assert.NotNull(listHandle);
        Assert.NotEmpty(listHandle);

        // 4. Search by partial IGN "click"
        IActionResult resultClick = await controller.SearchGlobal("click");
        JsonResult jsonClick = Assert.IsType<JsonResult>(resultClick);
        List<object>? listClick = jsonClick.Value as List<object>;
        Assert.NotNull(listClick);
        Assert.NotEmpty(listClick);

        // 5. Search sub-account "suzaku"
        IActionResult resultSuzaku = await controller.SearchGlobal("suzaku");
        JsonResult jsonSuzaku = Assert.IsType<JsonResult>(resultSuzaku);
        List<object>? listSuzaku = jsonSuzaku.Value as List<object>;
        Assert.NotNull(listSuzaku);
        Assert.NotEmpty(listSuzaku);

        // 6. Search account starting with "User_"
        IActionResult resultUserPrefix = await controller.SearchGlobal("user_newbie");
        JsonResult jsonUserPrefix = Assert.IsType<JsonResult>(resultUserPrefix);
        List<object>? listUserPrefix = jsonUserPrefix.Value as List<object>;
        Assert.NotNull(listUserPrefix);
        Assert.NotEmpty(listUserPrefix);
    }

    [Fact]
    public async Task SearchPlayers_FindsAccountsCaseInsensitively_AndMatchesDiscordUsername()
    {
        await using MerrickContext context = CreateInMemoryContext();

        User userLelouch = new()
        {
            EmailAddress = "d_12345678901234567",
            DiscordId = "12345678901234567",
            DiscordUsername = "lelouch0206",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account accountLelouch =
            new() { Name = "Lelouch", IsMain = true, Type = AccountType.Legacy, User = userLelouch };

        User userClickBotAlpha = new()
        {
            EmailAddress = "d_98765432109876543",
            DiscordId = "98765432109876543",
            DiscordUsername = "clickbot_alpha_01",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "User" }
        };
        Account accountClickBotAlpha = new()
        {
            Name = "ClickBotAlpha",
            IsMain = true,
            Type = AccountType.Legacy,
            User = userClickBotAlpha
        };

        context.Users.AddRange(userLelouch, userClickBotAlpha);
        context.Accounts.AddRange(accountLelouch, accountClickBotAlpha);
        await context.SaveChangesAsync();

        HomeController controller = CreateController(context);

        // 1. Search lowercase "lelo"
        IActionResult resultPlayersLelo = await controller.SearchPlayers("lelo");
        JsonResult jsonPlayersLelo = Assert.IsType<JsonResult>(resultPlayersLelo);
        List<object>? listPlayersLelo = jsonPlayersLelo.Value as List<object>;
        Assert.NotNull(listPlayersLelo);
        Assert.Single(listPlayersLelo);

        // 2. Search Discord handle with '@'
        IActionResult resultPlayersDiscord = await controller.SearchPlayers("@clickbot_alpha_01");
        JsonResult jsonPlayersDiscord = Assert.IsType<JsonResult>(resultPlayersDiscord);
        List<object>? listPlayersDiscord = jsonPlayersDiscord.Value as List<object>;
        Assert.NotNull(listPlayersDiscord);
        Assert.Single(listPlayersDiscord);
    }
}
