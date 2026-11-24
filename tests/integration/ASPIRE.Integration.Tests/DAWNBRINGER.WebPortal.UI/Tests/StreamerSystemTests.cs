// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Services;
using NEXUS.Contracts.Clients;
using LinkTwitchRequestDto = ASPIRE.Common.DTOs.LinkTwitchRequestDto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class StreamerSystemTests
{
    private (AccountController Controller, MerrickContext DbContext) CreateAccountController(
        string userName = "TestCaster")
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

        DefaultHttpContext httpContext = new() { User = new ClaimsPrincipal(identity) };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

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

        DefaultHttpContext httpContext = new() { User = new ClaimsPrincipal(identity) };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        return (controller, dbContext);
    }

    private (HomeController Controller, MerrickContext DbContext) CreateHomeController(
        string userName = "TestViewer",
        ITwitchStreamService? twitchStreamService = null)
    {
        MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();
        IUserApiClient userApiClient = TestApiClientFactory.CreateUserApiClient(dbContext);
        IAdminApiClient adminApiClient = TestApiClientFactory.CreateAdminApiClient(dbContext);
        IPlayerStatsApiClient statsApiClient = TestApiClientFactory.CreatePlayerStatsApiClient(dbContext);
        IGuideApiClient guideApiClient = TestApiClientFactory.CreateGuideApiClient(dbContext);

        IConfiguration configuration = new ConfigurationBuilder().Build();
        StaffStoreService storeService = new(configuration, NullLogger<StaffStoreService>.Instance);
        Mock<IMatchReplayService> replayServiceMock = new();
        Mock<ILogger<HomeController>> loggerMock = new();

        HomeController controller = new(statsApiClient, userApiClient, guideApiClient, adminApiClient, configuration,
            replayServiceMock.Object, null, storeService,
            loggerMock.Object, twitchStreamService);

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

    [Fact]
    public async Task LinkTwitch_DirectHandle_UpdatesUserTwitchUsernameAndId()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("TestCaster");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "testcaster@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "TestCaster", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        LinkTwitchRequestDto request = new() { TwitchUsername = "CasterPro", TwitchId = "12345678" };

        IActionResult result = await controller.LinkTwitch(request);
        Assert.NotNull(result);

        User? updatedUser = await dbContext.Users.FindAsync(user.ID);
        Assert.NotNull(updatedUser);
        Assert.Equal("CasterPro", updatedUser.TwitchUsername);
        Assert.Equal("12345678", updatedUser.TwitchId);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LinkTwitch_WithTwitchUrl_CleansHandleAndSaves()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("TestCaster");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "testcaster2@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "TestCaster", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        LinkTwitchRequestDto request = new() { TwitchUsername = "https://twitch.tv/EliteStreamer/" };

        IActionResult result = await controller.LinkTwitch(request);
        Assert.NotNull(result);

        User? updatedUser = await dbContext.Users.FindAsync(user.ID);
        Assert.NotNull(updatedUser);
        Assert.Equal("EliteStreamer", updatedUser.TwitchUsername);
    }

    [Fact]
    public async Task StreamerSystem_DisconnectTwitch_ClearsUserTwitchInfo()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("TestCaster");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "testcaster_disc@nexus.local",
            TwitchUsername = "TwitchToDisconnect",
            TwitchId = "998877",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "TestCaster", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.DisconnectTwitch();
        Assert.NotNull(result);

        User? updatedUser = await dbContext.Users.FindAsync(user.ID);
        Assert.NotNull(updatedUser);
        Assert.Null(updatedUser.TwitchUsername);
        Assert.Null(updatedUser.TwitchId);
    }

    [Fact]
    public async Task StreamerSystem_ApplyStreamer_CreatesPendingStreamerApplicationAndSetsTwitch()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("TestCaster");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "testcaster3@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "TestCaster", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        ApplyStreamerRequestDto request = new()
        {
            TwitchUsername = "@LiveGamer99",
            TwitchId = "887766",
            TwitchChannelUrl = "https://twitch.tv/LiveGamer99",
            ApplicationNotes = "Streaming Mid Wars daily at 7pm EST"
        };

        IActionResult result = await controller.ApplyStreamer(request);
        Assert.NotNull(result);

        StreamerApplication? app = await dbContext.StreamerApplications
            .FirstOrDefaultAsync(s => s.MainAccountID == account.ID);

        Assert.NotNull(app);
        Assert.Equal(StreamerApplicationStatus.Pending, app.Status);
        Assert.Equal("LiveGamer99", app.TwitchUsername);
        Assert.Equal("887766", app.TwitchId);
        Assert.Equal("https://twitch.tv/LiveGamer99", app.TwitchChannelUrl);
        Assert.Equal("Streaming Mid Wars daily at 7pm EST", app.ApplicationNotes);

        User? updatedUser = await dbContext.Users.FindAsync(user.ID);
        Assert.NotNull(updatedUser);
        Assert.Equal("LiveGamer99", updatedUser.TwitchUsername);
        Assert.Equal("887766", updatedUser.TwitchId);
    }

    [Fact]
    public async Task ApplyStreamer_WhenAlreadyConnected_SubmitsWithOnlyNotes()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("LinkedGamer");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "linkedgamer@nexus.local",
            TwitchUsername = "LinkedGamerLive",
            TwitchId = "554433",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "LinkedGamer", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        // User submits only notes because their Twitch account is already linked
        ApplyStreamerRequestDto request = new() { ApplicationNotes = "Daily stream Mon-Fri 6PM UTC" };

        IActionResult result = await controller.ApplyStreamer(request);
        Assert.NotNull(result);

        StreamerApplication? app = await dbContext.StreamerApplications
            .FirstOrDefaultAsync(s => s.MainAccountID == account.ID);

        Assert.NotNull(app);
        Assert.Equal(StreamerApplicationStatus.Pending, app.Status);
        Assert.Equal("LinkedGamerLive", app.TwitchUsername);
        Assert.Equal("554433", app.TwitchId);
        Assert.Equal("https://twitch.tv/LinkedGamerLive", app.TwitchChannelUrl);
        Assert.Equal("Daily stream Mon-Fri 6PM UTC", app.ApplicationNotes);
    }

    [Fact]
    public async Task StreamerSystem_AccountSettings_LoadsStreamerApplicationAndBadges()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("StreamerMain");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "streamermain@nexus.local",
            TwitchUsername = "StreamerMainLive",
            TwitchId = "11223344",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "StreamerMain", User = user, Type = AccountType.Streamer, IsMain = true };
        dbContext.Accounts.Add(account);

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = account.ID,
            MainAccount = account,
            TwitchUsername = "StreamerMainLive",
            TwitchId = "11223344",
            TwitchChannelUrl = "https://twitch.tv/StreamerMainLive",
            Status = StreamerApplicationStatus.Approved
        };
        dbContext.StreamerApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await controller.Settings();
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        AccountSettingsViewModel model = Assert.IsType<AccountSettingsViewModel>(viewResult.Model);

        Assert.True(model.IsStreamer);
        Assert.Equal("StreamerMainLive", model.TwitchUsername);
        Assert.Equal("11223344", model.TwitchId);
        Assert.True(model.HasTwitchLinked);
        Assert.NotNull(model.StreamerApp);
        Assert.Equal(StreamerApplicationStatus.Approved, model.StreamerApp.Status);
    }

    [Fact]
    public async Task ApplyHost_MultipleHostRequests_AllowsSuccessiveHostNodesOnceApproved()
    {
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("MultiHost");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "multihost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "MultiHost", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(mainAccount);

        // User already has 1 approved host account: MultiHost_host
        User hostUser = new()
        {
            EmailAddress = "host_multihost_host@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2"
        };
        Account hostAccount = new()
        {
            Name = "MultiHost_host",
            User = hostUser,
            Type = AccountType.ServerHost,
            IsMain = false,
            OwnerAccountID = mainAccount.ID,
            OwnerAccount = mainAccount
        };
        dbContext.Users.Add(hostUser);
        dbContext.Accounts.Add(hostAccount);

        HostApplication priorApp = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostAccountID = hostAccount.ID,
            HostAccount = hostAccount,
            HostUsername = "MultiHost_host",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 2
        };
        dbContext.HostApplications.Add(priorApp);
        await dbContext.SaveChangesAsync();

        // Apply for additional host account
        ApplyHostRequestDto request = new()
        {
            Location = "EU",
            Instances = 4,
            ServerNamePrefix = "EU Node 2",
            ApplicationNotes = "Additional server in Frankfurt"
        };

        IActionResult result = await controller.ApplyHost(request);
        Assert.NotNull(result);

        // Verify a new pending application is created with desired username MultiHost_host2
        List<HostApplication> applications = await dbContext.HostApplications
            .Where(ha => ha.MainAccountID == mainAccount.ID)
            .OrderBy(ha => ha.ID)
            .ToListAsync();

        Assert.Equal(2, applications.Count);
        HostApplication secondApp = applications[1];
        Assert.Equal(HostApplicationStatus.Pending, secondApp.Status);
        Assert.Equal("MultiHost_host2", secondApp.HostUsername);
        Assert.Equal("EU", secondApp.Location);
        Assert.Equal(4, secondApp.Instances);
    }

    [Fact]
    public async Task ApplyHost_WhilePending_UpdatesCurrentPendingRequest()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (AccountController controller, MerrickContext dbContext) = CreateAccountController("PendingHost");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "pendinghost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account mainAccount = new() { Name = "PendingHost", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(mainAccount);

        HostApplication initialPending = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostUsername = "PendingHos_host",
            Status = HostApplicationStatus.Pending,
            Location = "USE",
            Instances = 2
        };
        dbContext.HostApplications.Add(initialPending);
        await dbContext.SaveChangesAsync();

        // Update application
        ApplyHostRequestDto updateRequest = new()
        {
            Location = "USW",
            Instances = 8,
            ServerNamePrefix = "Upgraded Node",
            ApplicationNotes = "Upgraded to 16GB RAM"
        };

        IActionResult result = await controller.ApplyHost(updateRequest);
        Assert.NotNull(result);

        List<HostApplication> applications = await dbContext.HostApplications
            .Where(ha => ha.MainAccountID == mainAccount.ID)
            .ToListAsync();

        Assert.Single(applications);
        Assert.Equal(HostApplicationStatus.Pending, applications[0].Status);
        Assert.Equal("USW", applications[0].Location);
        Assert.Equal(8, applications[0].Instances);
        Assert.Equal("Upgraded Node", applications[0].ServerNamePrefix);
    }

    [Fact]
    public async Task StaffController_Streamers_ListsPendingAndApprovedCategories()
    {
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController();

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user1 = new()
        {
            EmailAddress = "streamer1@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        User user2 = new()
        {
            EmailAddress = "streamer2@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.AddRange(user1, user2);

        Account acc1 = new() { Name = "StreamerPending", User = user1, Type = AccountType.Normal, IsMain = true };
        Account acc2 = new() { Name = "StreamerApproved", User = user2, Type = AccountType.Streamer, IsMain = true };
        dbContext.Accounts.AddRange(acc1, acc2);

        StreamerApplication app1 = new()
        {
            UserID = user1.ID,
            User = user1,
            MainAccountID = acc1.ID,
            MainAccount = acc1,
            TwitchUsername = "StreamerPendingLive",
            TwitchId = "1001",
            Status = StreamerApplicationStatus.Pending
        };
        StreamerApplication app2 = new()
        {
            UserID = user2.ID,
            User = user2,
            MainAccountID = acc2.ID,
            MainAccount = acc2,
            TwitchUsername = "StreamerApprovedLive",
            TwitchId = "1002",
            Status = StreamerApplicationStatus.Approved
        };
        dbContext.StreamerApplications.AddRange(app1, app2);
        await dbContext.SaveChangesAsync();

        IActionResult result = await staffController.Streamers();
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        StaffStreamersViewModel viewModel = Assert.IsType<StaffStreamersViewModel>(viewResult.Model);

        Assert.Single(viewModel.PendingApplications);
        Assert.Equal("StreamerPending", viewModel.PendingApplications[0].MainAccountName);
        Assert.Equal("1001", viewModel.PendingApplications[0].TwitchId);
        Assert.Single(viewModel.ApprovedStreamers);
        Assert.Equal("StreamerApproved", viewModel.ApprovedStreamers[0].MainAccountName);
        Assert.Equal("1002", viewModel.ApprovedStreamers[0].TwitchId);
        Assert.Equal(2, viewModel.TotalApplications);
        Assert.Equal(1, viewModel.ActiveStreamerCount);
    }

    [Fact]
    public async Task StaffController_ApproveStreamer_ElevatesAccountTypeAndSyncsUserTwitch()
    {
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController("AdminKongor");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "partner@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "PartnerGamer", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = account.ID,
            MainAccount = account,
            TwitchUsername = "PartnerGamerTV",
            TwitchId = "998877",
            Status = StreamerApplicationStatus.Pending
        };
        dbContext.StreamerApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await staffController.ApproveStreamerApplication(app.ID, "Verified community partner");
        Assert.NotNull(result);

        StreamerApplication? updatedApp = await dbContext.StreamerApplications
            .Include(a => a.MainAccount)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ID == app.ID);

        Assert.NotNull(updatedApp);
        Assert.Equal(StreamerApplicationStatus.Approved, updatedApp.Status);
        Assert.Equal("AdminKongor", updatedApp.ReviewedByStaffName);
        Assert.Equal("Verified community partner", updatedApp.StaffNotes);
        Assert.Equal(AccountType.Streamer, updatedApp.MainAccount.Type);
        Assert.Equal("PartnerGamerTV", updatedApp.User.TwitchUsername);
        Assert.Equal("998877", updatedApp.User.TwitchId);
        Assert.NotNull(updatedApp.TimestampReviewed);
    }

    [Fact]
    public async Task StaffController_RejectStreamer_UpdatesStatusToRejected()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController("AdminKongor");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "applicant@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "CasualApplicant", User = user, Type = AccountType.Normal, IsMain = true };
        dbContext.Accounts.Add(account);

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = account.ID,
            MainAccount = account,
            TwitchUsername = "InactiveTwitch",
            Status = StreamerApplicationStatus.Pending
        };
        dbContext.StreamerApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await staffController.RejectStreamerApplication(app.ID, "Twitch channel not yet active");
        Assert.NotNull(result);

        StreamerApplication? updatedApp = await dbContext.StreamerApplications.FindAsync(app.ID);
        Assert.NotNull(updatedApp);
        Assert.Equal(StreamerApplicationStatus.Rejected, updatedApp.Status);
        Assert.Equal("Twitch channel not yet active", updatedApp.StaffNotes);
        Assert.Equal(AccountType.Normal, account.Type);
    }

    [Fact]
    public async Task StaffController_RevokeStreamer_RevertsAccountTypeToNormal()
    {
        (StaffController staffController, MerrickContext dbContext) = CreateStaffController("AdminKongor");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "formerstreamer@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "FormerStreamer", User = user, Type = AccountType.Streamer, IsMain = true };
        dbContext.Accounts.Add(account);

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = account.ID,
            MainAccount = account,
            TwitchUsername = "FormerStreamerLive",
            Status = StreamerApplicationStatus.Approved
        };
        dbContext.StreamerApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result =
            await staffController.RevokeStreamerApplication(app.ID, "Creator no longer broadcasting");
        Assert.NotNull(result);

        StreamerApplication? updatedApp = await dbContext.StreamerApplications
            .Include(a => a.MainAccount)
            .FirstOrDefaultAsync(a => a.ID == app.ID);

        Assert.NotNull(updatedApp);
        Assert.Equal(StreamerApplicationStatus.Revoked, updatedApp.Status);
        Assert.Equal(AccountType.Normal, updatedApp.MainAccount.Type);
    }

    [Fact]
    public async Task HomeController_Stats_ResolvesStreamerBadgesAndChannelUrl()
    {
        (HomeController homeController, MerrickContext dbContext) = CreateHomeController("Viewer");

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "streamerguy@nexus.local",
            TwitchUsername = "streamerguy",
            TwitchId = "776655",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "StreamerGuy", User = user, Type = AccountType.Streamer, IsMain = true };
        dbContext.Accounts.Add(account);

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = account.ID,
            MainAccount = account,
            TwitchUsername = "streamerguy",
            TwitchId = "776655",
            TwitchChannelUrl = "https://twitch.tv/streamerguy",
            Status = StreamerApplicationStatus.Approved
        };
        dbContext.StreamerApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await homeController.Stats("StreamerGuy");
        Assert.NotNull(result);

        Assert.True(homeController.ViewData["IsStreamer"] as bool?);
        Assert.Equal("streamerguy", homeController.ViewData["TwitchUsername"] as string);
        Assert.Equal("https://twitch.tv/streamerguy", homeController.ViewData["TwitchChannelUrl"] as string);
    }

    [Fact]
    public async Task HomeController_Stats_WhenLive_PassesLiveStatusAndStreamInfo()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Mock<ITwitchStreamService> mockTwitchService = new();
        mockTwitchService
            .Setup(s => s.GetStreamStatusAsync("livegamer", "12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitchStreamInfo
            {
                IsLive = true,
                TwitchUsername = "livegamer",
                TwitchId = "12345",
                ChannelUrl = "https://twitch.tv/livegamer",
                StreamTitle = "Mid Wars 1800+ MMR Push",
                GameName = "Heroes of Newerth",
                ViewerCount = 142
            });

        (HomeController homeController, MerrickContext dbContext) =
            CreateHomeController("Viewer", mockTwitchService.Object);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "livegamer@nexus.local",
            TwitchUsername = "livegamer",
            TwitchId = "12345",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "LiveGamer", User = user, Type = AccountType.Streamer, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        IActionResult result = await homeController.Stats("LiveGamer");
        Assert.NotNull(result);

        Assert.True(homeController.ViewData["IsStreamer"] as bool?);
        Assert.True(homeController.ViewData["IsTwitchLive"] as bool?);
        Assert.Equal("Mid Wars 1800+ MMR Push", homeController.ViewData["TwitchStreamTitle"] as string);
        Assert.Equal("Heroes of Newerth", homeController.ViewData["TwitchGameName"] as string);
        Assert.Equal(142, homeController.ViewData["TwitchViewerCount"] as int?);
    }

    [Fact]
    public void TwitchStreamInfo_IsPlayingHon_ReturnsTrueOnlyForHeroesOfNewerth()
    {
        TwitchStreamInfo honStream = new() { IsLive = true, GameName = "Heroes of Newerth" };
        Assert.True(honStream.IsPlayingHon);

        TwitchStreamInfo otherGameStream = new() { IsLive = true, GameName = "Just Chatting" };
        Assert.False(otherGameStream.IsPlayingHon);

        TwitchStreamInfo offlineHonStream = new() { IsLive = false, GameName = "Heroes of Newerth" };
        Assert.False(offlineHonStream.IsPlayingHon);
    }

    [Fact]
    public async Task HomeController_Index_PopulatesOnlyHeroesOfNewerthLiveStreams()
    {
        Mock<ITwitchStreamService> mockTwitchService = new();
        List<TwitchStreamInfo> mockHonStreams =
        [
            new TwitchStreamInfo
            {
                IsLive = true,
                TwitchUsername = "teststreamer",
                StreamTitle = "High MMR Scrims",
                GameName = "Heroes of Newerth",
                ViewerCount = 95,
                ChannelUrl = "https://twitch.tv/teststreamer"
            }
        ];

        mockTwitchService
            .Setup(s => s.GetLiveHonStreamsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockHonStreams);

        (HomeController homeController, MerrickContext dbContext) =
            CreateHomeController("Viewer", mockTwitchService.Object);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "teststreamer@nexus.local",
            TwitchUsername = "teststreamer",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = "TestStreamer", User = user, Type = AccountType.Streamer, IsMain = true };
        dbContext.Accounts.Add(account);

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = account.ID,
            MainAccount = account,
            TwitchUsername = "teststreamer",
            Status = StreamerApplicationStatus.Approved
        };
        dbContext.StreamerApplications.Add(app);
        await dbContext.SaveChangesAsync();

        IActionResult result = await homeController.Index();
        Assert.NotNull(result);

        List<TwitchStreamInfo>? liveStreams = homeController.ViewData["LiveHonStreams"] as List<TwitchStreamInfo>;
        Assert.NotNull(liveStreams);
        Assert.Single(liveStreams);
        Assert.Equal("teststreamer", liveStreams[0].TwitchUsername);
        Assert.Equal("Heroes of Newerth", liveStreams[0].GameName);
        Assert.True(liveStreams[0].IsPlayingHon);
    }

    [Theory]
    [InlineData(AccountType.Staff)]
    [InlineData(AccountType.GameMaster)]
    [InlineData(AccountType.MatchModerator)]
    [InlineData(AccountType.MatchCaster)]
    [InlineData(AccountType.VIP)]
    public async Task HomeController_Stats_SpecialAccountTypesWithLinkedTwitch_TreatedAsStreamers(
        AccountType accountType)
    {
        Mock<ITwitchStreamService> mockTwitchService = new();
        mockTwitchService
            .Setup(s => s.GetStreamStatusAsync("specialcaster", "998877", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitchStreamInfo
            {
                IsLive = true,
                TwitchUsername = "specialcaster",
                TwitchId = "998877",
                ChannelUrl = "https://twitch.tv/specialcaster",
                StreamTitle = "Casting Finals",
                GameName = "Heroes of Newerth",
                ViewerCount = 350
            });

        (HomeController homeController, MerrickContext dbContext) =
            CreateHomeController("Viewer", mockTwitchService.Object);

        Role userRole = new() { Name = UserRoles.User };
        dbContext.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"{accountType.ToString().ToLowerInvariant()}@nexus.local",
            TwitchUsername = "specialcaster",
            TwitchId = "998877",
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        dbContext.Users.Add(user);

        Account account = new() { Name = $"Hon{accountType}", User = user, Type = accountType, IsMain = true };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync();

        IActionResult result = await homeController.Stats($"Hon{accountType}");
        Assert.NotNull(result);

        Assert.True(homeController.ViewData["IsStreamer"] as bool?);
        Assert.True(homeController.ViewData["IsTwitchLive"] as bool?);
        Assert.Equal("specialcaster", homeController.ViewData["TwitchUsername"] as string);
        Assert.Equal("Casting Finals", homeController.ViewData["TwitchStreamTitle"] as string);
        Assert.Equal("Heroes of Newerth", homeController.ViewData["TwitchGameName"] as string);
        Assert.Equal(350, homeController.ViewData["TwitchViewerCount"] as int?);
    }
}
