// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Security.Claims;
using System.Text.Json;
using ASPIRE.Common.Security;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Clients;
using LeaderboardEntryDto = DAWNBRINGER.WebPortal.UI.Controllers.LeaderboardEntryDto;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class VolunteerHostSystemTests
{
    private (AccountController Controller, MerrickContext DbContext) CreateAccountController(
        string userName = "TestHost")
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IUserApiClient userApiClient = TestApiClientFactory.CreateUserApiClient(dbContext);
        IAdminApiClient adminApiClient = TestApiClientFactory.CreateAdminApiClient(dbContext);

        Mock<IHttpClientFactory> httpFactoryMock = new();
        Mock<ILogger<AccountController>> loggerMock = new();
        Mock<IOutputCacheStore> cacheStoreMock = new();
        Mock<IConfiguration> configMock = new();
        configMock.Setup(c => c["GATEWAY_HOST"]).Returns("kongor.net");

        AccountController controller = new(userApiClient, adminApiClient, httpFactoryMock.Object, loggerMock.Object,
            cacheStoreMock.Object,
            configMock.Object);

        ClaimsIdentity identity = new([
            new Claim(ClaimTypes.Name, userName),
            new Claim(ClaimTypes.NameIdentifier, "1")
        ], "TestAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return (controller, dbContext);
    }

    private (StaffController Controller, MerrickContext DbContext) CreateStaffController(
        string staffName = "AdminStaff")
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IUserApiClient userApiClient = TestApiClientFactory.CreateUserApiClient(dbContext);
        IAdminApiClient adminApiClient = TestApiClientFactory.CreateAdminApiClient(dbContext);
        IPlayerStatsApiClient statsApiClient = TestApiClientFactory.CreatePlayerStatsApiClient(dbContext);

        IConfiguration configuration = new ConfigurationBuilder().Build();
        StaffStoreService storeService = new(configuration, NullLogger<StaffStoreService>.Instance);
        StaffController controller = new(adminApiClient, userApiClient, statsApiClient, storeService,
            logger: NullLogger<StaffController>.Instance);

        ClaimsIdentity identity = new([
            new Claim(ClaimTypes.Name, staffName),
            new Claim(ClaimTypes.Role, "Staff"),
            new Claim("is_staff", "true")
        ], "StaffAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return (controller, dbContext);
    }

    private static HomeController CreateHomeController(MerrickContext dbContext, IConfiguration? config = null)
    {
        IUserApiClient userApiClient = TestApiClientFactory.CreateUserApiClient(dbContext);
        IAdminApiClient adminApiClient = TestApiClientFactory.CreateAdminApiClient(dbContext);
        IPlayerStatsApiClient statsApiClient = TestApiClientFactory.CreatePlayerStatsApiClient(dbContext);
        IGuideApiClient guideApiClient = TestApiClientFactory.CreateGuideApiClient(dbContext);
        return new HomeController(statsApiClient, userApiClient, guideApiClient, adminApiClient,
            config ?? new ConfigurationBuilder().Build());
    }

    [Fact]
    public void HostSecurityService_CryptographyAndPasswords_WorkAsExpected()
    {
        string rawPassword = HostSecurityService.GenerateRandomHostPassword();
        Assert.Equal(16, rawPassword.Length);

        string cipherText = HostSecurityService.EncryptPassword(rawPassword);
        Assert.False(string.IsNullOrWhiteSpace(cipherText));
        Assert.NotEqual(rawPassword, cipherText);

        string decrypted = HostSecurityService.DecryptPassword(cipherText);
        Assert.Equal(rawPassword, decrypted);

        string salt = HostSecurityService.GenerateSrpSalt();
        Assert.False(string.IsNullOrWhiteSpace(salt));

        string hash = HostSecurityService.ComputeSrpHash(rawPassword, salt, false);
        Assert.False(string.IsNullOrWhiteSpace(hash));
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AccountController_ApplyHost_CreatesPendingHostApplication()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (AccountController controller, MerrickContext dbContext) = CreateAccountController();

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "testhost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "TestHost", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(mainAccount);
        await dbContext.SaveChangesAsync();

        ApplyHostRequestDto request = new()
        {
            Location = "USE",
            Instances = 4,
            ServerNamePrefix = "TEST BATTLEGROUNDS",
            ApplicationNotes = "Dedicated 8-core server node"
        };

        IActionResult result = await controller.ApplyHost(request);
        Assert.NotNull(result);

        HostApplication? app = await dbContext.HostApplications
            .FirstOrDefaultAsync(h => h.MainAccountID == mainAccount.ID);

        Assert.NotNull(app);
        Assert.Equal(HostApplicationStatus.Pending, app.Status);
        Assert.Equal("TestHost_host", app.HostUsername);
        Assert.Equal("USE", app.Location);
        Assert.Equal(4, app.Instances);
        Assert.Equal("TEST BATTLEGROUNDS", app.ServerNamePrefix);
    }

    [Fact]
    public async Task StaffController_ApproveHostApplication_ProvisionsHostUserAndServerAccount()
    {
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController();

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "volunteer@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "VolunteerGuy", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(mainAccount);

        HostApplication app = new()
        {
            User = user,
            UserID = user.ID,
            MainAccount = mainAccount,
            MainAccountID = mainAccount.ID,
            HostUsername = "Volunteer_host",
            Status = HostApplicationStatus.Pending,
            Location = "EU",
            Instances = 2,
            ServerNamePrefix = "EU MATCH ARENA"
        };
        dbContext.HostApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await staffController.ApproveHostApplication(app.ID, "Approved for EU region");
        Assert.NotNull(result);

        HostApplication? updatedApp = await dbContext.HostApplications
            .Include(h => h.HostAccount)
            .FirstOrDefaultAsync(h => h.ID == app.ID);

        Assert.NotNull(updatedApp);
        Assert.Equal(HostApplicationStatus.Approved, updatedApp.Status);
        Assert.Equal("AdminStaff", updatedApp.ReviewedByStaffName);
        Assert.False(string.IsNullOrEmpty(updatedApp.EncryptedHostPassword));

        Assert.NotNull(updatedApp.HostAccount);
        Assert.Equal(AccountType.ServerHost, updatedApp.HostAccount.Type);
        Assert.Equal("Volunteer_host", updatedApp.HostAccount.Name);

        // Verify host User entity has SRP credentials
        User? hostUser =
            await dbContext.Users.FirstOrDefaultAsync(u => u.EmailAddress == "host_volunteer_host@nexus.local");
        Assert.NotNull(hostUser);
        Assert.False(string.IsNullOrEmpty(hostUser.SRPPasswordSalt));
        Assert.False(string.IsNullOrEmpty(hostUser.SRPPasswordHash));
        Assert.False(string.IsNullOrEmpty(hostUser.PBKDF2PasswordHash));
    }

    [Fact]
    public async Task AccountController_DownloadCompelConfig_ReturnsValidCompelJsonStructure()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("HostHero");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "hero@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "HostHero", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(mainAccount);

        const string rawPassword = "SecretHostPassword123!"; // kongor-security-ignore: synthetic unit-test host password
        string encryptedPassword = HostSecurityService.EncryptPassword(rawPassword);

        HostApplication app = new()
        {
            User = user,
            UserID = user.ID,
            MainAccount = mainAccount,
            MainAccountID = mainAccount.ID,
            HostUsername = "HostHero_host",
            EncryptedHostPassword = encryptedPassword,
            Status = HostApplicationStatus.Approved,
            Location = "USW",
            Instances = 3,
            ServerNamePrefix = "HERO ARENA"
        };
        dbContext.HostApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.DownloadCompelConfig();
        FileContentResult fileResult = Assert.IsType<FileContentResult>(result);

        Assert.Equal("application/json", fileResult.ContentType);
        Assert.Equal("COMPEL.JSON", fileResult.FileDownloadName);

        string jsonString = Encoding.UTF8.GetString(fileResult.FileContents);
        Assert.Contains("HostHero_host", jsonString);
        Assert.Contains(rawPassword, jsonString);
        Assert.Contains("USW", jsonString);
        Assert.Contains("HERO ARENA", jsonString);
        Assert.Contains("Description", jsonString);
        Assert.Contains("Value", jsonString);
    }

    [Fact]
    public async Task AccountController_GetHostCredentials_AutoHealsMissingEncryptedPassword()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("DonutOwner");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "donut@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "DonutOwner", User = user, Type = AccountType.Normal, IsMain = true };
        Account hostAccount = new() { Name = "donut_host", User = user, Type = AccountType.ServerHost, IsMain = false };
        dbContext.Accounts.AddRange(mainAccount, hostAccount);

        // HostApplication exists but EncryptedHostPassword is null / uninitialized
        HostApplication app = new()
        {
            User = user,
            UserID = user.ID,
            MainAccount = mainAccount,
            MainAccountID = mainAccount.ID,
            HostAccount = hostAccount,
            HostAccountID = hostAccount.ID,
            HostUsername = "donut_host",
            EncryptedHostPassword = null,
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 2,
            ServerNamePrefix = "DONUT ARENA"
        };
        dbContext.HostApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.GetHostCredentials();
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);

        string json = JsonSerializer.Serialize(jsonResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        Assert.Equal("donut_host", doc.RootElement.GetProperty("hostUsername").GetString());
        Assert.True(string.IsNullOrEmpty(doc.RootElement.GetProperty("hostPassword").GetString()));

        IActionResult regenResult = await controller.RegenerateHostPassword();
        JsonResult regenJsonResult = Assert.IsType<JsonResult>(regenResult);
        using JsonDocument regenDoc = JsonDocument.Parse(JsonSerializer.Serialize(regenJsonResult.Value));
        Assert.True(regenDoc.RootElement.GetProperty("success").GetBoolean());
        string? revealedPassword = regenDoc.RootElement.GetProperty("hostPassword").GetString();
        Assert.False(string.IsNullOrEmpty(revealedPassword));

        // Verify the database was auto-healed with encrypted password and host user SRP hashes
        HostApplication? updatedApp = await dbContext.HostApplications.FindAsync(app.ID);
        Assert.NotNull(updatedApp);
        Assert.False(string.IsNullOrEmpty(updatedApp.EncryptedHostPassword));
        Assert.Equal(revealedPassword, HostSecurityService.DecryptPassword(updatedApp.EncryptedHostPassword));
    }

    [Fact]
    public async Task AccountController_RegenerateHostPassword_GeneratesNewPasswordAndUpdatesHashes()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("ResetHostPlayer");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "resethost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "ResetHostPlayer", User = user, Type = AccountType.Normal, IsMain = true };
        Account hostAccount = new()
        {
            Name = "ResetHost_host",
            User = user,
            Type = AccountType.ServerHost,
            IsMain = false
        };
        dbContext.Accounts.AddRange(mainAccount, hostAccount);

        const string initialRawPassword = "OldPassword123!"; // kongor-security-ignore: synthetic unit-test host password
        string initialEncrypted = HostSecurityService.EncryptPassword(initialRawPassword);

        HostApplication app = new()
        {
            User = user,
            UserID = user.ID,
            MainAccount = mainAccount,
            MainAccountID = mainAccount.ID,
            HostAccount = hostAccount,
            HostAccountID = hostAccount.ID,
            HostUsername = "ResetHost_host",
            EncryptedHostPassword = initialEncrypted,
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 4,
            ServerNamePrefix = "RESET ARENA"
        };
        dbContext.HostApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.RegenerateHostPassword();
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);

        string json = JsonSerializer.Serialize(jsonResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        string? newPassword = doc.RootElement.GetProperty("hostPassword").GetString();
        Assert.False(string.IsNullOrEmpty(newPassword));
        Assert.NotEqual(initialRawPassword, newPassword);

        // Verify the database updated
        HostApplication? updatedApp = await dbContext.HostApplications.FindAsync(app.ID);
        Assert.NotNull(updatedApp);
        Assert.NotEqual(initialEncrypted, updatedApp.EncryptedHostPassword);
        Assert.Equal(newPassword, HostSecurityService.DecryptPassword(updatedApp.EncryptedHostPassword!));
    }

    [Fact]
    public async Task StaffController_RevokeHostApplication_DisablesHostAccount()
    {
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController();

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "badhost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "BadHostUser", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(mainAccount);

        Account hostAccount = new()
        {
            Name = "BadHost_host",
            User = user,
            Type = AccountType.ServerHost,
            IsMain = false
        };
        dbContext.Accounts.Add(hostAccount);

        HostApplication app = new()
        {
            User = user,
            UserID = user.ID,
            MainAccount = mainAccount,
            MainAccountID = mainAccount.ID,
            HostAccount = hostAccount,
            HostAccountID = hostAccount.ID,
            HostUsername = "BadHost_host",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 2,
            ServerNamePrefix = "BAD ARENA"
        };
        dbContext.HostApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await staffController.RevokeHostApplication(app.ID, "Server unstable");
        Assert.NotNull(result);

        HostApplication? updatedApp = await dbContext.HostApplications
            .Include(h => h.HostAccount)
            .FirstOrDefaultAsync(h => h.ID == app.ID);

        Assert.NotNull(updatedApp);
        Assert.Equal(HostApplicationStatus.Revoked, updatedApp.Status);
        Assert.NotNull(updatedApp.HostAccount);
        Assert.Equal(AccountType.Disabled, updatedApp.HostAccount.Type);
    }

    [Fact]
    public async Task HomeController_Leaderboard_HostMode_RanksByHostingRatioDescending()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        HomeController controller = CreateHomeController(dbContext, config);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        // Host 1: 100 hosted, 20 played -> Ratio 5.0
        User user1 = new()
        {
            EmailAddress = "host1@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user1);

        Account main1 = new() { Name = "AlphaPlayer", User = user1, Type = AccountType.Normal, IsMain = true };
        Account host1 = new() { Name = "AlphaHost", User = user1, Type = AccountType.ServerHost, IsMain = false };
        dbContext.Accounts.AddRange(main1, host1);

        HostApplication app1 = new()
        {
            User = user1,
            UserID = user1.ID,
            MainAccount = main1,
            MainAccountID = main1.ID,
            HostAccount = host1,
            HostAccountID = host1.ID,
            HostUsername = "AlphaHost",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 4
        };
        dbContext.HostApplications.Add(app1);

        // Host 2: 10 hosted, 100 played -> Ratio 0.10
        User user2 = new()
        {
            EmailAddress = "host2@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user2);

        Account main2 = new() { Name = "BetaPlayer", User = user2, Type = AccountType.Normal, IsMain = true };
        Account host2 = new() { Name = "BetaHost", User = user2, Type = AccountType.ServerHost, IsMain = false };
        dbContext.Accounts.AddRange(main2, host2);

        HostApplication app2 = new()
        {
            User = user2,
            UserID = user2.ID,
            MainAccount = main2,
            MainAccountID = main2.ID,
            HostAccount = host2,
            HostAccountID = host2.ID,
            HostUsername = "BetaHost",
            Status = HostApplicationStatus.Approved,
            Location = "EU",
            Instances = 2
        };
        dbContext.HostApplications.Add(app2);

        // Seed 100 hosted games for AlphaHost, 20 played for AlphaPlayer
        for (int i = 1; i <= 100; i++)
            dbContext.MatchStatistics.Add(CreateDefaultMatchStatistics(1000 + i, "AlphaHost"));

        for (int i = 1; i <= 20; i++)
            dbContext.PlayerStatistics.Add(CreateDefaultPlayerStatistics(main1.ID, 1000 + i, "AlphaPlayer"));

        // Seed 10 hosted games for BetaHost, 100 played for BetaPlayer
        for (int i = 1; i <= 10; i++) dbContext.MatchStatistics.Add(CreateDefaultMatchStatistics(2000 + i, "BetaHost"));

        for (int i = 1; i <= 100; i++)
            dbContext.PlayerStatistics.Add(CreateDefaultPlayerStatistics(main2.ID, 2000 + i, "BetaPlayer"));

        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.Leaderboard(null, "hosts");
        ViewResult viewResult = Assert.IsType<ViewResult>(result);

        Assert.True(Convert.ToBoolean(viewResult.ViewData["IsHostLeaderboard"]));
        Assert.Equal(110, Convert.ToInt32(viewResult.ViewData["TotalCommunityHostedMatches"]));

        List<HostLeaderboardEntryDto>? hostEntries =
            viewResult.ViewData["HostLeaderboardEntries"] as List<HostLeaderboardEntryDto>;
        Assert.NotNull(hostEntries);
        Assert.Equal(2, hostEntries.Count);

        Assert.Equal("AlphaHost", hostEntries[0].AccountName);
        Assert.Equal(1, hostEntries[0].Rank);
        Assert.Equal(100, hostEntries[0].GamesHosted);
        Assert.Equal(20, hostEntries[0].GamesPlayed);
        Assert.Equal(5.0, hostEntries[0].HostingRatio);
        Assert.Equal(90.9, hostEntries[0].CommunityContributionPercentage);

        Assert.Equal("BetaHost", hostEntries[1].AccountName);
        Assert.Equal(2, hostEntries[1].Rank);
        Assert.Equal(10, hostEntries[1].GamesHosted);
        Assert.Equal(100, hostEntries[1].GamesPlayed);
        Assert.Equal(0.10, hostEntries[1].HostingRatio);
        Assert.Equal(9.1, hostEntries[1].CommunityContributionPercentage);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HomeController_Leaderboard_HostMode_IncludesServerHostOwnersWithoutHostApplication()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        HomeController controller = CreateHomeController(dbContext, config);

        Role staffRole = new() { Name = UserRoles.Administrator };
        dbContext.Roles.Add(staffRole);

        User kongorUser = new()
        {
            EmailAddress = "admin@kongor.local",
            Role = staffRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(kongorUser);

        Account mainAccountKongor = new()
        {
            Name = "KONGOR",
            User = kongorUser,
            Type = AccountType.Staff,
            IsMain = true
        };
        Account hostAccount1 = new()
        {
            Name = "HOST",
            User = kongorUser,
            Type = AccountType.ServerHost,
            IsMain = false
        };
        Account hostAccount2 = new()
        {
            Name = "SERVER",
            User = kongorUser,
            Type = AccountType.ServerHost,
            IsMain = false
        };
        dbContext.Accounts.AddRange(mainAccountKongor, hostAccount1, hostAccount2);

        // Seed matches hosted by HOST and SERVER
        for (int i = 1; i <= 3; i++) dbContext.MatchStatistics.Add(CreateDefaultMatchStatistics(5000 + i, "HOST"));

        dbContext.MatchStatistics.Add(CreateDefaultMatchStatistics(5004, "SERVER"));

        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.Leaderboard(null, "hosts");
        ViewResult viewResult = Assert.IsType<ViewResult>(result);

        Assert.True(Convert.ToBoolean(viewResult.ViewData["IsHostLeaderboard"]));
        Assert.Equal(4, Convert.ToInt32(viewResult.ViewData["TotalCommunityHostedMatches"]));

        List<HostLeaderboardEntryDto>? hostEntries =
            viewResult.ViewData["HostLeaderboardEntries"] as List<HostLeaderboardEntryDto>;
        Assert.NotNull(hostEntries);
        Assert.Single(hostEntries);

        Assert.Equal("KONGOR", hostEntries[0].AccountName);
        Assert.Equal(1, hostEntries[0].Rank);
        Assert.Equal(4, hostEntries[0].GamesHosted);
        Assert.Equal(0, hostEntries[0].GamesPlayed);
        Assert.Equal(100.0, hostEntries[0].CommunityContributionPercentage);
    }

    [Fact]
    public async Task HomeController_Leaderboard_WithMatchesSort_RanksByTotalMatchesPlayed()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        HomeController controller = CreateHomeController(dbContext, config);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user1 = new()
        {
            EmailAddress = "player1@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        User user2 = new()
        {
            EmailAddress = "player2@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.AddRange(user1, user2);

        Account accLowMatchesHighWinRate =
            new() { Name = "SniperPro", User = user1, Type = AccountType.Normal, IsMain = true };
        Account accHighMatchesLowerWinRate =
            new() { Name = "GrinderVeteran", User = user2, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.AddRange(accLowMatchesHighWinRate, accHighMatchesLowerWinRate);

        // Seed 10 matches for accLowMatchesHighWinRate (9 Wins = 90% Win Rate)
        for (int i = 1; i <= 10; i++)
        {
            PlayerStatistics ps = CreateDefaultPlayerStatistics(accLowMatchesHighWinRate.ID, 5000 + i, "SniperPro");
            ps.Win = i <= 9 ? 1 : 0;
            dbContext.PlayerStatistics.Add(ps);
        }

        // Seed 50 matches for accHighMatchesLowerWinRate (25 Wins = 50% Win Rate)
        for (int i = 1; i <= 50; i++)
        {
            PlayerStatistics ps = CreateDefaultPlayerStatistics(accHighMatchesLowerWinRate.ID, 6000 + i,
                "GrinderVeteran", "Hero_Pyromancer");
            ps.Win = i <= 25 ? 1 : 0;
            dbContext.PlayerStatistics.Add(ps);
        }

        await dbContext.SaveChangesAsync();

        // 1. When sorting by default "winrate", SniperPro (90%) is #1, GrinderVeteran (50%) is #2
        IActionResult winRateResult = await controller.Leaderboard();
        ViewResult winRateViewResult = Assert.IsType<ViewResult>(winRateResult);
        List<LeaderboardEntryDto>? winRateEntries = winRateViewResult.Model as List<LeaderboardEntryDto>;
        Assert.NotNull(winRateEntries);
        Assert.Equal("SniperPro", winRateEntries[0].AccountName);
        Assert.Equal(90.0, winRateEntries[0].WinRate);

        // 2. When sorting by "matches", GrinderVeteran (50 matches) is #1, SniperPro (10 matches) is #2
        IActionResult matchesResult = await controller.Leaderboard(null, "all", "all", 0, "matches");
        ViewResult matchesViewResult = Assert.IsType<ViewResult>(matchesResult);
        List<LeaderboardEntryDto>? matchesEntries = matchesViewResult.Model as List<LeaderboardEntryDto>;
        Assert.NotNull(matchesEntries);
        Assert.Equal("GrinderVeteran", matchesEntries[0].AccountName);
        Assert.Equal(50, matchesEntries[0].TotalMatches);
        Assert.Equal(1, matchesEntries[0].Rank);

        Assert.Equal("SniperPro", matchesEntries[1].AccountName);
        Assert.Equal(10, matchesEntries[1].TotalMatches);
        Assert.Equal(2, matchesEntries[1].Rank);
    }

    [Fact]
    public async Task
        HomeController_Stats_WithLinkedPlayerAndHostAccounts_CalculatesGamesHostedAndPlayedAcrossAllLinkedAccounts()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        HomeController controller = CreateHomeController(dbContext, config);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "clusterhost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainPlayerAccount = new()
        {
            Name = "ClusterMain",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        Account altPlayerAccount = new()
        {
            Name = "ClusterSmurf",
            User = user,
            Type = AccountType.Normal,
            IsMain = false
        };
        Account hostAccount = new()
        {
            Name = "ClusterMain_host",
            User = user,
            Type = AccountType.ServerHost,
            IsMain = false
        };
        dbContext.Accounts.AddRange(mainPlayerAccount, altPlayerAccount, hostAccount);

        HostApplication hostApp = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainPlayerAccount.ID,
            MainAccount = mainPlayerAccount,
            HostAccountID = hostAccount.ID,
            HostAccount = hostAccount,
            HostUsername = "ClusterMain_host",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 4
        };
        dbContext.HostApplications.Add(hostApp);

        // Seed 50 matches hosted by ClusterMain_host
        for (int i = 1; i <= 50; i++)
            dbContext.MatchStatistics.Add(CreateDefaultMatchStatistics(7000 + i, "ClusterMain_host"));

        // Seed 30 matches played by ClusterMain
        for (int i = 1; i <= 30; i++)
            dbContext.PlayerStatistics.Add(CreateDefaultPlayerStatistics(mainPlayerAccount.ID, 7000 + i,
                "ClusterMain"));

        // Seed 20 matches played by ClusterSmurf (total 50 matches played across linked accounts)
        for (int i = 1; i <= 20; i++)
            dbContext.PlayerStatistics.Add(CreateDefaultPlayerStatistics(altPlayerAccount.ID, 8000 + i,
                "ClusterSmurf"));

        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.Stats("ClusterMain");
        ViewResult viewResult = Assert.IsType<ViewResult>(result);

        Assert.True(Convert.ToBoolean(viewResult.ViewData["HasLinkedHostAccounts"]));
        Assert.True(Convert.ToBoolean(viewResult.ViewData["HasHostAccount"]));
        Assert.Equal(50, Convert.ToInt32(viewResult.ViewData["GamesHosted"]));
        Assert.Equal(50, Convert.ToInt32(viewResult.ViewData["TotalPlayedAcrossSubaccounts"]));
        Assert.Equal(1.00, Convert.ToDouble(viewResult.ViewData["HostingRatio"]));
        Assert.Equal("ClusterMain_host", viewResult.ViewData["HostUsername"]);
        Assert.Equal("Master Host", viewResult.ViewData["HostBadgeTier"]);
    }

    [Fact]
    public async Task HomeController_Stats_ForNonHostPlayer_HasLinkedHostAccountsIsFalse()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IConfiguration config = new ConfigurationBuilder().Build();
        HomeController controller = CreateHomeController(dbContext, config);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "pureplayer@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account playerAccount = new() { Name = "CasualGamer", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(playerAccount);

        for (int i = 1; i <= 10; i++)
            dbContext.PlayerStatistics.Add(CreateDefaultPlayerStatistics(playerAccount.ID, 9000 + i, "CasualGamer"));

        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.Stats("CasualGamer");
        ViewResult viewResult = Assert.IsType<ViewResult>(result);

        Assert.False(Convert.ToBoolean(viewResult.ViewData["HasLinkedHostAccounts"]));
        Assert.Equal(0, Convert.ToInt32(viewResult.ViewData["GamesHosted"]));
        Assert.Equal(10, Convert.ToInt32(viewResult.ViewData["TotalPlayedAcrossSubaccounts"]));
        Assert.Equal(0.0, Convert.ToDouble(viewResult.ViewData["HostingRatio"]));
        Assert.Null(viewResult.ViewData["HostBadgeTier"]);
    }

    [Fact]
    public async Task HostTelemetryHelper_DirectHostLookup_ResolvesTelemetryBidirectionally()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "gucciuser@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "guccidonut", User = user, Type = AccountType.Normal, IsMain = true };
        Account hostAccount = new() { Name = "guccihost", User = user, Type = AccountType.ServerHost, IsMain = false };
        dbContext.Accounts.AddRange(mainAccount, hostAccount);

        HostApplication hostApp = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostAccountID = hostAccount.ID,
            HostAccount = hostAccount,
            HostUsername = "guccihost",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 2
        };
        dbContext.HostApplications.Add(hostApp);

        // Seed 100 hosted games by guccihost
        for (int i = 1; i <= 100; i++)
            dbContext.MatchStatistics.Add(CreateDefaultMatchStatistics(10000 + i, "guccihost"));

        // Seed 25 games played by guccidonut
        for (int i = 1; i <= 25; i++)
            dbContext.PlayerStatistics.Add(CreateDefaultPlayerStatistics(mainAccount.ID, 10000 + i, "guccidonut"));

        await dbContext.SaveChangesAsync();

        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(dbContext);
        AccountDto hostAccountDto = (await userApi.GetAccountByIdAsync(hostAccount.ID))!;
        HostTelemetryResult hostResult = await HostTelemetryHelper.ResolveHostTelemetryAsync(userApi, hostAccountDto);

        Assert.True(hostResult.HasLinkedHostAccounts);
        Assert.Equal(100, hostResult.GamesHosted);
        Assert.Equal(25, hostResult.TotalPlayedAcrossLinkedAccounts);
        Assert.Equal(4.0, hostResult.HostingRatio);
        Assert.Equal("Patron of Newerth", hostResult.HostBadgeTier);
        Assert.Contains("guccihost", hostResult.LinkedHostAccountNames);
        Assert.Contains(mainAccount.ID, hostResult.LinkedPlayerAccountIds);
    }

    [Fact]
    public async Task StaffController_RegenerateCredentials_NeverModifiesApplicantPersonalPassword()
    {
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController("AdminKongor");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        // Applicant's personal User with their personal password
        User playerUser = new()
        {
            EmailAddress = "personal_player@example.com",
            Role = userRole,
            SRPPasswordSalt = "PLAYER_PERSONAL_SALT_123",
            SRPPasswordHash = "PLAYER_PERSONAL_HASH_123",
            PBKDF2PasswordHash = "PLAYER_PERSONAL_PBKDF2_123"
        };
        dbContext.Users.Add(playerUser);

        Account mainAccount = new()
        {
            Name = "MyPlayerAccount",
            User = playerUser,
            IsMain = true,
            Type = AccountType.Normal
        };
        dbContext.Accounts.Add(mainAccount);

        // Host Account initially linked to application
        Account hostAccount = new()
        {
            Name = "myhost_1",
            User = playerUser, // Simulating an old legacy link to the player's personal User
            IsMain = false,
            Type = AccountType.ServerHost
        };
        dbContext.Accounts.Add(hostAccount);

        HostApplication hostApp = new()
        {
            UserID = playerUser.ID,
            User = playerUser,
            MainAccount = mainAccount,
            MainAccountID = mainAccount.ID,
            HostAccount = hostAccount,
            HostAccountID = hostAccount.ID,
            HostUsername = "myhost_1",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 2,
            ServerNamePrefix = "Arena"
        };
        dbContext.HostApplications.Add(hostApp);
        await dbContext.SaveChangesAsync();

        IActionResult result = await staffController.RegenerateHostPassword(hostApp.ID);
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        // Verify player's personal User was NOT modified
        User refreshedPlayerUser = await dbContext.Users.FirstAsync(u => u.ID == playerUser.ID);
        Assert.Equal("PLAYER_PERSONAL_SALT_123", refreshedPlayerUser.SRPPasswordSalt);
        Assert.Equal("PLAYER_PERSONAL_HASH_123", refreshedPlayerUser.SRPPasswordHash);
        Assert.Equal("PLAYER_PERSONAL_PBKDF2_123", refreshedPlayerUser.PBKDF2PasswordHash);

        // Verify a dedicated host User was provisioned with the host's credentials
        User? dedicatedHostUser =
            await dbContext.Users.FirstOrDefaultAsync(u => u.EmailAddress == "host_myhost_1@nexus.local");
        Assert.NotNull(dedicatedHostUser);
        Assert.NotEqual(playerUser.ID, dedicatedHostUser.ID);
        Assert.NotEmpty(dedicatedHostUser.SRPPasswordHash);
        Assert.NotEmpty(dedicatedHostUser.SRPPasswordSalt);
    }

    [Fact]
    public async Task Account_OwnerAccountRelationship_ProperlyLinksHostedNodesToPlayerAccount()
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User playerUser = new()
        {
            EmailAddress = "player_owner@example.com",
            Role = userRole,
            SRPPasswordSalt = "salt1",
            SRPPasswordHash = "hash1",
            PBKDF2PasswordHash = "pbkdf2_1"
        };
        dbContext.Users.Add(playerUser);

        User hostUser1 = new()
        {
            EmailAddress = "host_node1@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt2",
            SRPPasswordHash = "hash2",
            PBKDF2PasswordHash = "pbkdf2_2"
        };
        dbContext.Users.Add(hostUser1);

        User hostUser2 = new()
        {
            EmailAddress = "host_node2@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt3",
            SRPPasswordHash = "hash3",
            PBKDF2PasswordHash = "pbkdf2_3"
        };
        dbContext.Users.Add(hostUser2);

        Account mainAccount = new()
        {
            Name = "PlayerMain",
            User = playerUser,
            IsMain = true,
            Type = AccountType.Normal
        };
        dbContext.Accounts.Add(mainAccount);

        Account hostNode1 = new()
        {
            Name = "node_use_1",
            User = hostUser1,
            Type = AccountType.ServerHost,
            IsMain = false,
            OwnerAccount = mainAccount
        };
        dbContext.Accounts.Add(hostNode1);

        Account hostNode2 = new()
        {
            Name = "node_usw_1",
            User = hostUser2,
            Type = AccountType.ServerHost,
            IsMain = false,
            OwnerAccount = mainAccount
        };
        dbContext.Accounts.Add(hostNode2);

        await dbContext.SaveChangesAsync();

        // Query player account with HostedNodes navigation
        Account reloadedMain = await dbContext.Accounts
            .Include(a => a.HostedNodes)
            .FirstAsync(a => a.ID == mainAccount.ID);

        Assert.Equal(2, reloadedMain.HostedNodes.Count);
        Assert.Contains(reloadedMain.HostedNodes, n => n.Name == "node_use_1");
        Assert.Contains(reloadedMain.HostedNodes, n => n.Name == "node_usw_1");

        // Query host node with OwnerAccount navigation
        Account reloadedNode = await dbContext.Accounts
            .Include(a => a.OwnerAccount)
            .FirstAsync(a => a.Name == "node_use_1");

        Assert.NotNull(reloadedNode.OwnerAccount);
        Assert.Equal(mainAccount.ID, reloadedNode.OwnerAccount.ID);
        Assert.Equal("PlayerMain", reloadedNode.OwnerAccount.Name);
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

    private static MatchStatistics CreateDefaultMatchStatistics(int matchId, string hostAccountName = "ServerHost")
    {
        return new MatchStatistics
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = hostAccountName,
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
            GameMode = "matchmaking",
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
            AwardHighestCreepScore = null,
            TimestampRecorded = TimeProvider.System.GetUtcNow()
        };
    }

    [Fact]
    public void HostSecurityService_GenerateRandomAuthToken_GeneratesSecureToken()
    {
        string token = HostSecurityService.GenerateRandomAuthToken();

        Assert.StartsWith("compel_", token);
        Assert.True(token.Length >= 32);

        string cipherText = HostSecurityService.EncryptPassword(token);
        string decrypted = HostSecurityService.DecryptPassword(cipherText);

        Assert.Equal(token, decrypted);
    }

    [Fact]
    public async Task AccountController_RevealHostPassword_IncludesDecryptedAuthToken()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("TestHostUser");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "testhost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new()
        {
            Name = "TestHostUser",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        dbContext.Accounts.Add(mainAccount);

        string rawAuthToken = HostSecurityService.GenerateRandomAuthToken();
        HostApplication hostApp = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostUsername = "TestHostUser_host",
            EncryptedHostPassword = HostSecurityService.EncryptPassword("SecretPass123!"),
            EncryptedAuthToken = HostSecurityService.EncryptPassword(rawAuthToken),
            Status = HostApplicationStatus.Approved
        };
        dbContext.HostApplications.Add(hostApp);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.GetHostCredentials();
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);

        string json = JsonSerializer.Serialize(jsonResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("TestHostUser_host", doc.RootElement.GetProperty("hostUsername").GetString());
        Assert.Equal("SecretPass123!", doc.RootElement.GetProperty("hostPassword").GetString());
        Assert.Equal(rawAuthToken, doc.RootElement.GetProperty("authToken").GetString());
    }

    [Fact]
    public async Task AccountController_RegenerateHostAuthToken_RotatesAuthTokenSuccessfully()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("RotateUser");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "rotate@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new()
        {
            Name = "RotateUser",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        dbContext.Accounts.Add(mainAccount);

        string oldToken = HostSecurityService.GenerateRandomAuthToken();
        HostApplication hostApp = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostUsername = "RotateUser_host",
            EncryptedHostPassword = HostSecurityService.EncryptPassword("SecretPass123!"),
            EncryptedAuthToken = HostSecurityService.EncryptPassword(oldToken),
            Status = HostApplicationStatus.Approved
        };
        dbContext.HostApplications.Add(hostApp);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.RegenerateHostAuthToken();
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);

        string json = JsonSerializer.Serialize(jsonResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        string newToken = doc.RootElement.GetProperty("authToken").GetString()!;

        Assert.StartsWith("compel_", newToken);
        Assert.NotEqual(oldToken, newToken);

        HostApplication? updatedApp = await dbContext.HostApplications.FindAsync(hostApp.ID);
        Assert.NotNull(updatedApp);
        Assert.Equal(newToken, HostSecurityService.DecryptPassword(updatedApp.EncryptedAuthToken!));
    }

    [Fact]
    public async Task Settings_WithLinkedHostAccount_PopulatesHostPropertiesCorrectly()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("HostTester");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "hosttester@test.com",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash",
            GoldCoins = 100,
            SilverCoins = 200,
            PlinkoTickets = 5
        };
        dbContext.Users.Add(user);

        Account mainAccount = new()
        {
            Name = "HostTester",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        dbContext.Accounts.Add(mainAccount);

        Account hostAccount = new()
        {
            Name = "HostTester_host",
            User = user,
            Type = AccountType.ServerHost,
            IsMain = false
        };
        dbContext.Accounts.Add(hostAccount);

        HostApplication hostApp = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostAccountID = hostAccount.ID,
            HostAccount = hostAccount,
            HostUsername = "HostTester_host",
            Location = "USE",
            Instances = 2,
            Status = HostApplicationStatus.Approved
        };
        dbContext.HostApplications.Add(hostApp);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.Settings();
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        AccountSettingsViewModel model = Assert.IsType<AccountSettingsViewModel>(viewResult.Model);

        Assert.True(model.HasApprovedHostAccount);
        Assert.True(model.HasLinkedHostAccount);
        Assert.Equal("HostTester_host", model.HostUsername);
        Assert.Equal("USE", model.ApplyHostModel.Location);
        Assert.Equal(2, model.ApplyHostModel.Instances);
    }
}
