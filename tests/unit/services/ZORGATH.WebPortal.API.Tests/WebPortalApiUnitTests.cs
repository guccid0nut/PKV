// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Extensions;
using Moq;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.PlayerStats;
using ZORGATH.WebPortal.API.Features.Admin;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class WebPortalApiUnitTests
{
    [Fact]
    public void WebPortalApiUnit_WebPortalApiAssembly_ShouldLoadSuccessfully()
    {
        typeof(WebPortalApiUnitTests).Assembly.Should().NotBeNull();
    }

    [Fact]
    public async Task UserApiController_ExistsAccountName_ShouldSupportRouteAndQuery()
    {
        var accountRepo = new Mock<IAccountRepository>();
        var inventoryRepo = new Mock<IInventoryRepository>();
        var sessionRepo = new Mock<ISessionRepository>();

        accountRepo.Setup(r => r.ExistsByNicknameAsync("guccidonut", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        accountRepo.Setup(r => r.ExistsByNicknameAsync("unknown_warrior", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        UserApiController controller = new(accountRepo.Object, inventoryRepo.Object, sessionRepo.Object);

        IActionResult routeResult = await controller.ExistsAccountName("guccidonut", null);
        routeResult.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(true);

        IActionResult queryResult = await controller.ExistsAccountName(null, "unknown_warrior");
        queryResult.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(false);

        IActionResult emptyResult = await controller.ExistsAccountName(null, null);
        emptyResult.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PlayerStatsApiController_GetPlayerProfile_ReturnsAggregatedStats()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        var matchRepo = new Mock<IMatchHistoryRepository>();
        var accountRepo = new Mock<IAccountRepository>();

        User user = new()
        {
            ID = 10,
            EmailAddress = "test@projectkongor.com",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            PBKDF2PasswordHash = "pbkdf2_hash",
            DiscordId = "123456789",
            DiscordAvatar = "avatar_hash"
        };
        db.Users.Add(user);

        Account account = new()
        {
            ID = 100,
            UserID = user.ID,
            User = user,
            Name = "TestWarrior",
            Type = AccountType.Normal
        };
        db.Accounts.Add(account);

        AccountStatistics rankedStats = new()
        {
            AccountID = 100,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 10,
            MatchesWon = 8,
            MatchesLost = 2,
            SkillRating = 1650
        };
        db.AccountStatistics.Add(rankedStats);

        PlayerStatistics ps1 = new()
        {
            AccountID = 100,
            MatchID = 1,
            HeroIdentifier = "Hero_Pyromancer",
            HeroKills = 12,
            HeroDeaths = 3,
            HeroAssists = 8,
            Win = 1,
            Loss = 0
        };
        PlayerStatistics ps2 = new()
        {
            AccountID = 100,
            MatchID = 2,
            HeroIdentifier = "Hero_Pyromancer",
            HeroKills = 7,
            HeroDeaths = 2,
            HeroAssists = 11,
            Win = 1,
            Loss = 0
        };
        db.PlayerStatistics.AddRange(ps1, ps2);
        await db.SaveChangesAsync();

        PlayerStatsApiController controller = new(new LocalPlayerStatsMediator(db, matchRepo.Object, accountRepo.Object));

        IActionResult result = await controller.GetPlayerProfile("testwarrior");
        result.Should().BeOfType<OkObjectResult>();

        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();

        PlayerProfileSummaryDto? profile = okResult!.Value as PlayerProfileSummaryDto;
        profile.Should().NotBeNull();
        profile!.Account.Name.Should().Be("TestWarrior");
        profile.AggregatedStats.Should().NotBeNull();
        profile.AggregatedStats!.MatchesPlayed.Should().Be(2);
        profile.AggregatedStats!.HeroKills.Should().Be(19);
        profile.AggregatedStats!.Deaths.Should().Be(5);
        profile.AggregatedStats!.HeroAssists.Should().Be(19);
    }

    [Fact]
    public async Task UserApiController_SetInGamePassword_ShouldSucceed()
    {
        var accountRepo = new Mock<IAccountRepository>();
        var inventoryRepo = new Mock<IInventoryRepository>();
        var sessionRepo = new Mock<ISessionRepository>();

        accountRepo.Setup(r => r.SetInGamePasswordAsync(42, "salt123", "srphash", "pbkdf2", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        accountRepo.Setup(r => r.SetInGamePasswordAsync(999, "salt123", "srphash", "pbkdf2", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        UserApiController controller = new(accountRepo.Object, inventoryRepo.Object, sessionRepo.Object);

        IActionResult successResult = await controller.SetInGamePassword(42, new SetInGamePasswordRequestDto("salt123", "srphash", "pbkdf2"));
        successResult.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(true);

        IActionResult notFoundResult = await controller.SetInGamePassword(999, new SetInGamePasswordRequestDto("salt123", "srphash", "pbkdf2"));
        notFoundResult.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UserApiController_RegisterUserAndLogIn_ShouldSucceed()
    {
        var accountRepo = new Mock<IAccountRepository>();
        var inventoryRepo = new Mock<IInventoryRepository>();
        var sessionRepo = new Mock<ISessionRepository>();

        User user = new()
        {
            ID = 55,
            EmailAddress = "newplayer@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash"
        };
        accountRepo.Setup(r => r.ExistsByEmailAsync("newplayer@kongor.net", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        accountRepo.Setup(r => r.ExistsByNicknameAsync("NewWarrior", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        accountRepo.Setup(r => r.GetRoleByNameAsync(UserRoles.User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Name = UserRoles.User });
        accountRepo.Setup(r => r.CreateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((u, _) =>
            {
                user.Argon2idPasswordHash = u.Argon2idPasswordHash;
                user.PBKDF2PasswordHash = u.PBKDF2PasswordHash;
            })
            .ReturnsAsync(user);
        accountRepo.Setup(r => r.GetUserByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        accountRepo.Setup(r => r.GetUserByAccountNameAsync("NewWarrior", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        Token token = new()
        {
            ID = 1,
            Value = Guid.NewGuid(),
            Purpose = TokenPurpose.Session,
            Expires = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(7)
        };
        sessionRepo.Setup(r => r.IssueTokenAsync(user.ID, TokenPurpose.Session, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);

        UserApiController controller = new(accountRepo.Object, inventoryRepo.Object, sessionRepo.Object);

        // Register
        RegisterUserRequestDto regPayload = new("newplayer@kongor.net", "secret123", "NewWarrior");
        IActionResult regResult = await controller.RegisterUser(regPayload);
        regResult.Should().BeOfType<CreatedAtActionResult>();

        // Login
        LogInUserRequestDto loginPayload = new("NewWarrior", "secret123");
        IActionResult loginResult = await controller.LogInUser(loginPayload);
        loginResult.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task PlayerStatsApiController_SaveMatchEvidence_WithDto_PersistsSuccessfully()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        var matchRepo = new Mock<IMatchHistoryRepository>();
        var accountRepo = new Mock<IAccountRepository>();

        PlayerStatsApiController controller = new(new LocalPlayerStatsMediator(db, matchRepo.Object, accountRepo.Object));

        MatchEvidenceDto evidence = new()
        {
            MatchID = 101,
            HasReplay = true,
            HasVoice = true,
            ChatLogJson = "[\"chat msg 1\"]",
            VoiceTrackLegionUrl = "/voice/1",
            VoiceTrackHellbourneUrl = "/voice/2"
        };

        IActionResult result = await controller.SaveMatchEvidence(evidence, 101);
        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(true);

        IActionResult getResult = await controller.GetMatchEvidence(101);
        getResult.Should().BeOfType<OkObjectResult>();
        var dto = (getResult as OkObjectResult)!.Value as MatchEvidenceDto;
        dto.Should().NotBeNull();
        dto!.MatchID.Should().Be(101);
        dto.HasReplay.Should().BeTrue();
        dto.ChatLogJson.Should().Be("[\"chat msg 1\"]");
    }

    [Fact]
    public async Task StoreApiController_GetItemById_Heroes_And_Search_ShouldReturnMappedDtos()
    {
        var inventoryRepo = new Mock<IInventoryRepository>();
        var accountRepo = new Mock<IAccountRepository>();

        List<StoreItem> catalog =
        [
            new StoreItem
            {
                Id = 1,
                Name = "Gladiator Alt Avatar",
                Code = "aa.Hero_Gladiator.alt",
                UpgradeType = 3,
                LocalContent = "/heroes/gladiator",
                Price = 500,
                PriceInMMP = 100,
                Enabled = true,
                Purchasable = true
            },
            new StoreItem
            {
                Id = 2,
                Name = "Devourer Clown",
                Code = "aa.Hero_Devourer.clown",
                UpgradeType = 3,
                LocalContent = "/heroes/devourer",
                Price = 600,
                PriceInMMP = 150,
                Enabled = true,
                Purchasable = true
            }
        ];

        inventoryRepo.Setup(r => r.GetStoreCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog);

        StoreApiController controller = new(inventoryRepo.Object, accountRepo.Object);

        // GetItemById
        IActionResult itemResult = await controller.GetItemById(1);
        itemResult.Should().BeOfType<OkObjectResult>();
        var itemDto = (itemResult as OkObjectResult)!.Value as StoreItemDto;
        itemDto.Should().NotBeNull();
        itemDto!.ID.Should().Be(1);
        itemDto.HeroName.Should().Be("Gladiator");
        itemDto.AltAvatarSuffix.Should().Be("alt");

        // GetItemsByHero
        IActionResult heroResult = await controller.GetItemsByHero("Devourer");
        heroResult.Should().BeOfType<OkObjectResult>();
        var heroList = (heroResult as OkObjectResult)!.Value as List<StoreItemDto>;
        heroList.Should().NotBeNull();
        heroList!.Should().HaveCount(1);
        heroList[0].HeroName.Should().Be("Devourer");

        // SearchStore
        IActionResult searchResult = await controller.SearchStore("Clown");
        searchResult.Should().BeOfType<OkObjectResult>();
        var searchList = (searchResult as OkObjectResult)!.Value as List<StoreItemDto>;
        searchList.Should().NotBeNull();
        searchList!.Should().HaveCount(1);
        searchList[0].Name.Should().Contain("Clown");
    }

    [Fact]
    public void StoreItem_ToDto_ParsesHeroAndAltAvatarSuffix_Correctly()
    {
        StoreItem item = new()
        {
            Id = 50,
            Name = "Devourer Butcher",
            Code = "aa.Hero_Devourer.butcher",
            UpgradeType = 3,
            LocalContent = "/devourer",
            Price = 400,
            PriceInMMP = 100,
            Enabled = true,
            Purchasable = true
        };

        StoreItemDto dto = item.ToDto();
        dto.ID.Should().Be(50);
        dto.HeroName.Should().Be("Devourer");
        dto.AltAvatarSuffix.Should().Be("butcher");
    }

    [Fact]
    public void StoreItem_ToDto_WithNullOrHeroCode_ParsesSafely()
    {
        StoreItem nullCodeItem = new()
        {
            Id = 51,
            Name = "Null Code Item",
            Code = null!,
            LocalContent = "/content",
            UpgradeType = 0
        };
        StoreItemDto nullDto = nullCodeItem.ToDto();
        nullDto.HeroName.Should().BeEmpty();
        nullDto.AltAvatarSuffix.Should().BeEmpty();

        StoreItem heroItem = new()
        {
            Id = 52,
            Name = "Devourer Hero",
            Code = "Hero_Devourer",
            LocalContent = "/content",
            UpgradeType = 0
        };
        StoreItemDto heroDto = heroItem.ToDto();
        heroDto.HeroName.Should().Be("Devourer");
        heroDto.AltAvatarSuffix.Should().BeEmpty();

        StoreItem prefixedHeroItem = new()
        {
            Id = 53,
            Name = "Gladiator Hero",
            Code = "aa.Hero_Gladiator",
            LocalContent = "/content",
            UpgradeType = 0
        };
        StoreItemDto prefixedDto = prefixedHeroItem.ToDto();
        prefixedDto.HeroName.Should().Be("Gladiator");
        prefixedDto.AltAvatarSuffix.Should().BeEmpty();
    }

    [Fact]
    public async Task UserApiController_GetHostTelemetry_ShouldSucceed()
    {
        var accountRepo = new Mock<IAccountRepository>();
        var inventoryRepo = new Mock<IInventoryRepository>();
        var sessionRepo = new Mock<ISessionRepository>();

        HostTelemetryResultDto telemetryDto = new(
            HasLinkedHostAccounts: true,
            HasPendingHostApplication: false,
            LinkedHostAccountNames: ["Host_Alpha"],
            LinkedPlayerAccountIds: [10],
            LinkedPlayerAccountNames: ["SuperPlayer"],
            GamesHosted: 42,
            TotalPlayedAcrossLinkedAccounts: 100,
            HostingRatio: 0.42,
            CommunityContributionPercentage: 5.0,
            HostBadgeTier: "Dedicated Host",
            PrimaryHostApplication: null,
            HostUsername: "SuperHost"
        );

        accountRepo.Setup(r => r.GetHostTelemetryAsync("SuperHost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(telemetryDto);
        accountRepo.Setup(r => r.GetHostTelemetryAsync("NonExistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostTelemetryResultDto?)null);

        UserApiController controller = new(accountRepo.Object, inventoryRepo.Object, sessionRepo.Object);

        IActionResult result = await controller.GetHostTelemetry("SuperHost");
        result.Should().BeOfType<OkObjectResult>();
        var okVal = (result as OkObjectResult)!.Value as HostTelemetryResultDto;
        okVal.Should().NotBeNull();
        okVal!.HostUsername.Should().Be("SuperHost");
        okVal.GamesHosted.Should().Be(42);

        IActionResult notFound = await controller.GetHostTelemetry("NonExistent");
        notFound.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task PlayerStatsApiController_SubmitPlayerReport_WithDto_PersistsCorrectly()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        var matchRepo = new Mock<IMatchHistoryRepository>();
        var accountRepo = new Mock<IAccountRepository>();

        PlayerStatsApiController controller = new(new LocalPlayerStatsMediator(db, matchRepo.Object, accountRepo.Object));

        PlayerReportDto reportDto = new()
        {
            MatchID = 500,
            ReporterAccountID = 10,
            ReporterAccountName = "GoodPlayer",
            ReportedAccountID = 20,
            ReportedAccountName = "ToxicPlayer",
            Reason = "AFK",
            Description = "Left game at minute 5"
        };

        IActionResult result = await controller.SubmitPlayerReport(reportDto);
        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be(true);

        PlayerReport? saved = await db.PlayerReports.FirstOrDefaultAsync(r => r.MatchID == 500);
        saved.Should().NotBeNull();
        saved!.ReporterAccountName.Should().Be("GoodPlayer");
        saved.ReportedAccountName.Should().Be("ToxicPlayer");
        saved.Reason.Should().Be("AFK");
        saved.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task StoreApiController_GetCatalogEntities_WithPagination_ReturnsStaffStoreViewModelDto()
    {
        var inventoryRepo = new Mock<IInventoryRepository>();
        var accountRepo = new Mock<IAccountRepository>();

        List<StoreItem> catalog =
        [
            new StoreItem { Id = 1, Name = "Item A", Code = "aa.Hero_Devourer.alt", LocalContent = "/content", UpgradeType = 3, Price = 100, PriceInMMP = 50, Enabled = true, Purchasable = true },
            new StoreItem { Id = 2, Name = "Item B", Code = "aa.Hero_Devourer.clown", LocalContent = "/content", UpgradeType = 3, Price = 200, PriceInMMP = 100, Enabled = true, Purchasable = true },
            new StoreItem { Id = 3, Name = "Item C", Code = "aa.Hero_Gladiator.alt", LocalContent = "/content", UpgradeType = 3, Price = 300, PriceInMMP = 150, Enabled = true, Purchasable = true }
        ];

        inventoryRepo.Setup(r => r.GetStoreCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog);

        StoreApiController controller = new(inventoryRepo.Object, accountRepo.Object);

        // Without query params -> returns List<StoreItemDto>
        IActionResult defaultResult = await controller.GetCatalogEntities();
        defaultResult.Should().BeOfType<OkObjectResult>();
        var defaultList = (defaultResult as OkObjectResult)!.Value as List<StoreItemDto>;
        defaultList.Should().NotBeNull();
        defaultList!.Should().HaveCount(3);

        // With page / pageSize query params -> returns StaffStoreViewModelDto
        IActionResult pagedResult = await controller.GetCatalogEntities(search: "Item", page: 1, pageSize: 2);
        pagedResult.Should().BeOfType<OkObjectResult>();
        var viewModel = (pagedResult as OkObjectResult)!.Value as StaffStoreViewModelDto;
        viewModel.Should().NotBeNull();
        viewModel!.TotalCount.Should().Be(3);
        viewModel.Items.Should().HaveCount(2);
        viewModel.CurrentPage.Should().Be(1);
        viewModel.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task StoreApiController_SearchStore_WithQueryAndSearchAliases_AndSpecialChars_Succeeds()
    {
        var inventoryRepo = new Mock<IInventoryRepository>();
        var accountRepo = new Mock<IAccountRepository>();

        List<StoreItem> catalog =
        [
            new StoreItem { Id = 1, Name = "Devourer Clown", Code = "aa.Hero_Devourer.clown", LocalContent = "/content", UpgradeType = 3, Enabled = true, Purchasable = true },
            new StoreItem { Id = 2, Name = "Flint Beastwood", Code = "aa.Hero_Flint.alt", LocalContent = "/content", UpgradeType = 3, Enabled = true, Purchasable = true }
        ];

        inventoryRepo.Setup(r => r.GetStoreCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog);

        StoreApiController controller = new(inventoryRepo.Object, accountRepo.Object);

        // Alias 'query' parameter
        IActionResult result1 = await controller.SearchStore(query: "Flint");
        result1.Should().BeOfType<OkObjectResult>();
        var list1 = (result1 as OkObjectResult)!.Value as List<StoreItemDto>;
        list1.Should().NotBeNull();
        list1!.Should().HaveCount(1);
        list1[0].Name.Should().Contain("Flint");

        // Alias 'search' parameter
        IActionResult result2 = await controller.SearchStore(search: "Clown");
        result2.Should().BeOfType<OkObjectResult>();
        var list2 = (result2 as OkObjectResult)!.Value as List<StoreItemDto>;
        list2.Should().NotBeNull();
        list2!.Should().HaveCount(1);
        list2[0].Name.Should().Contain("Clown");

        // Special regex characters in search
        IActionResult result3 = await controller.SearchStore(q: "[Clown]");
        result3.Should().BeOfType<OkObjectResult>();
        var list3 = (result3 as OkObjectResult)!.Value as List<StoreItemDto>;
        list3.Should().NotBeNull();
        list3!.Should().BeEmpty();
    }

    [Fact]
    public async Task AdminApiController_GetHostApplications_StreamerApplications_RecentAccounts_ReturnDtosWithoutCycles()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        var accountRepo = new Mock<IAccountRepository>();

        User user = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "hash",
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampLastActive = TimeProvider.System.GetUtcNow()
        };
        db.Users.Add(user);

        Account account = new()
        {
            ID = 10,
            Name = "AdminAccount",
            User = user,
            UserID = 1,
            Type = AccountType.Staff,
            IsMain = true,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        db.Accounts.Add(account);

        HostApplication hostApp = new()
        {
            ID = 101,
            UserID = 1,
            User = user,
            MainAccountID = 10,
            MainAccount = account,
            HostUsername = "HostAdmin",
            ServerNamePrefix = "HON-US",
            Location = "USE",
            Status = HostApplicationStatus.Approved,
            TimestampApplied = TimeProvider.System.GetUtcNow()
        };
        db.HostApplications.Add(hostApp);

        StreamerApplication streamerApp = new()
        {
            ID = 201,
            UserID = 1,
            User = user,
            MainAccountID = 10,
            MainAccount = account,
            TwitchUsername = "StreamerAdmin",
            Status = StreamerApplicationStatus.Approved,
            TimestampApplied = TimeProvider.System.GetUtcNow()
        };
        db.StreamerApplications.Add(streamerApp);

        await db.SaveChangesAsync();

        AdminApiController controller = new(new LocalAdminMediator(db, accountRepo.Object));

        // Host Applications returns DTOs
        IActionResult hostResult = await controller.GetHostApplications();
        hostResult.Should().BeOfType<OkObjectResult>();
        var hostDtos = (hostResult as OkObjectResult)!.Value as List<HostApplicationDto>;
        hostDtos.Should().NotBeNull();
        hostDtos!.Should().HaveCount(1);
        hostDtos[0].HostUsername.Should().Be("HostAdmin");

        // Streamer Applications returns DTOs
        IActionResult streamerResult = await controller.GetStreamerApplications();
        streamerResult.Should().BeOfType<OkObjectResult>();
        var streamerDtos = (streamerResult as OkObjectResult)!.Value as List<StreamerApplicationDto>;
        streamerDtos.Should().NotBeNull();
        streamerDtos!.Should().HaveCount(1);
        streamerDtos[0].TwitchUsername.Should().Be("StreamerAdmin");

        // Recent Accounts returns DTOs
        IActionResult recentResult = await controller.GetRecentAccounts(10);
        recentResult.Should().BeOfType<OkObjectResult>();
        var recentDtos = (recentResult as OkObjectResult)!.Value as List<AccountDto>;
        recentDtos.Should().NotBeNull();
        recentDtos!.Should().HaveCount(1);
        recentDtos[0].Name.Should().Be("AdminAccount");

        // Account Detail returns DTO
        IActionResult detailResult = await controller.GetAccountDetail("AdminAccount");
        detailResult.Should().BeOfType<OkObjectResult>();
        var detailDto = (detailResult as OkObjectResult)!.Value as AccountDto;
        detailDto.Should().NotBeNull();
        detailDto!.Name.Should().Be("AdminAccount");

        // Verify JSON serialization produces no circular reference exception
        string json = System.Text.Json.JsonSerializer.Serialize(hostDtos);
        json.Should().NotBeNullOrWhiteSpace();
    }
}

