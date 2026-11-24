// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Security.Claims;
using System.Text.Json;
using ASPIRE.Common.Models.Guides;
using ASPIRE.Common.Models.Codex;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Controllers;
using NEXUS.Contracts.Clients;
using HeroGuideDto = NEXUS.Contracts.Clients.HeroGuideDto;
using SaveHeroGuideRequestDto = NEXUS.Contracts.Clients.SaveHeroGuideRequestDto;
using KONGOR.MasterServer.Models.RequestResponse.GameData;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class HeroGuideSystemTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    private static User CreateTestUser(string email = "author@kongor.net")
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

    private static GuideController CreateController(MerrickContext context, string? userName = "GuideMaster",
        bool isStaff = false, string? email = null)
    {
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        GuideController controller = new(guideApi, userApi, NullLogger<GuideController>.Instance);
        DefaultHttpContext httpContext = new();
        if (!string.IsNullOrEmpty(userName))
        {
            List<Claim> claims =
            [
                new Claim(ClaimTypes.Name, userName)
            ];
            if (isStaff)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Staff"));
                claims.Add(new Claim("is_staff", "true"));
            }

            if (!string.IsNullOrEmpty(email))
            {
                claims.Add(new Claim(ClaimTypes.Email, email));
            }

            ClaimsIdentity identity = new(claims, "TestAuth");
            httpContext.User = new ClaimsPrincipal(identity);
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public async Task GuideController_Index_ReturnsViewWithFilteredGuides()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "GuideMaster", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide guide1 = new()
        {
            Name = "Panda Mid Wars Smasher",
            HeroName = "Pandamonium",
            HeroIdentifier = "Hero_Panda",
            TargetMap = "midwars_ranked",
            Intro = "Smasher build",
            Content = "Focus on early combo",
            StartingItems = "Item_HealthPotion,Item_ManaPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_Immunity,Item_Pierce",
            LuxuryItems = "Item_SymbolOfRage",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            Rating = 5.0f,
            UpVotes = 10,
            DownVotes = 0,
            Public = true,
            Featured = true,
            Author = account
        };

        HeroGuide guide2 = new()
        {
            Name = "Valkyrie Jungle Roamer",
            HeroName = "Valkyrie",
            HeroIdentifier = "Hero_Valkyrie",
            TargetMap = "caldavar_ranked",
            Intro = "Roamer build",
            Content = "Gank side lanes",
            StartingItems = "Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GeometersBane",
            LuxuryItems = "Item_Wingbow",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            Rating = 4.0f,
            UpVotes = 5,
            DownVotes = 1,
            Public = true,
            Featured = false,
            Author = account
        };

        context.HeroGuides.AddRange(guide1, guide2);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "GuideMaster");

        // Act
        IActionResult result = await controller.Index(hero: "Hero_Panda", map: "midwars_ranked");

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        IReadOnlyList<HeroGuideDto> model = Assert.IsAssignableFrom<IReadOnlyList<HeroGuideDto>>(viewResult.Model);
        Assert.Single(model);
        Assert.Equal("Panda Mid Wars Smasher", model[0].Name);
    }

    [Fact]
    public async Task GuideController_SaveGuide_CreatesNewGuideSuccessfully()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "GuideMaster", User = user, IsMain = true };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "GuideMaster");

        SaveHeroGuideRequestDto request = new()
        {
            Name = "Deadwood Tree Slammer",
            HeroName = "Deadwood",
            HeroIdentifier = "Hero_Deadwood",
            TargetMap = "caldavar",
            Intro = "Early burst powerhouse build.",
            Content = "Max W first, acquire Shrunken Head and Helm.",
            StartingItems = "Item_HealthPotion,Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_Immunity,Item_BarbedArmor",
            LuxuryItems = "Item_SymbolOfRage",
            AbilityQueue = "1,2,1,2,1,4,1,2,2,3,4,3,3,3,5,4,5,5,5,5,5,5,5,5,5",
            IsPublic = true
        };

        // Act
        IActionResult result = await controller.SaveGuide(request);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        int guideId = doc.RootElement.GetProperty("guideId").GetInt32();

        Assert.True(success);
        Assert.True(guideId > 0);

        HeroGuide? savedGuide =
            await context.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == guideId);
        Assert.NotNull(savedGuide);
        Assert.Equal("Deadwood Tree Slammer", savedGuide.Name);
        Assert.Equal("caldavar", savedGuide.TargetMap);
        Assert.Equal("GuideMaster", savedGuide.Author.Name);
    }

    [Fact]
    public async Task GuideController_DeleteGuide_ProtectsOfficialGuides()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "GuideMaster", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide officialGuide = new()
        {
            Name = "Official Default Pyro",
            HeroName = "Pyromancer",
            HeroIdentifier = "Hero_Pyromancer",
            TargetMap = "all",
            Intro = "Default",
            Content = "Default",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GrimoireOfPower",
            LuxuryItems = "Item_StaffOfTheMaster",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            Rating = 5.0f,
            UpVotes = 10,
            DownVotes = 0,
            Featured = true,
            Public = true,
            Author = account
        };
        context.HeroGuides.Add(officialGuide);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "GuideMaster");

        // Act
        IActionResult result = await controller.DeleteGuide(officialGuide.ID);

        // Assert
        BadRequestObjectResult badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        string json = JsonSerializer.Serialize(badRequestResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();

        Assert.False(success);
        Assert.Contains("Official", doc.RootElement.GetProperty("message").GetString()!);

        // Ensure not deleted from DB
        HeroGuide? stillExists = await context.HeroGuides.FindAsync(officialGuide.ID);
        Assert.NotNull(stillExists);
    }

    [Fact]
    public async Task GuideController_DeleteGuide_AllowsDeletingAuthorCustomGuide()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "GuideMaster", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide customGuide = new()
        {
            Name = "Custom Chronos Build",
            HeroName = "Chronos",
            HeroIdentifier = "Hero_Chronos",
            TargetMap = "all",
            Intro = "Chronos Carry",
            Content = "Farm and Chronosphere",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GeometersBane",
            LuxuryItems = "Item_Wingbow",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Featured = false,
            Public = true,
            Author = account
        };
        context.HeroGuides.Add(customGuide);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "GuideMaster");

        // Act
        IActionResult result = await controller.DeleteGuide(customGuide.ID);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();

        Assert.True(success);

        // Ensure deleted from DB
        HeroGuide? deleted = await context.HeroGuides.FindAsync(customGuide.ID);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task GuideController_ExportGuide_ReturnsAccuratePackage()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "ProPlayer", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide guide = new()
        {
            Name = "Mid Wars Aluna Sniper",
            HeroName = "Aluna",
            HeroIdentifier = "Hero_Aluna",
            TargetMap = "midwars_ranked",
            Intro = "High power arrow poke build.",
            Content = "Prioritize Grimoire and Staff.",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GrimoireOfPower,Item_StaffOfTheMaster",
            LuxuryItems = "Item_Hellflower",
            AbilityQueue = "1,2,1,3,1,4,1,2,2,2,4,3,3,3,5,4,5,5,5,5,5,5,5,5,5",
            Rating = 5.0f,
            UpVotes = 2,
            DownVotes = 0,
            Public = true,
            Featured = false,
            Author = account
        };
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "ProPlayer");

        // Act
        IActionResult result = await controller.ExportGuide(guide.ID);

        // Assert
        FileContentResult fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", fileResult.ContentType);
        string json = Encoding.UTF8.GetString(fileResult.FileContents);
        GuideExportPackage? package = JsonSerializer.Deserialize<GuideExportPackage>(json);

        Assert.NotNull(package);
        Assert.Equal("Mid Wars Aluna Sniper", package.GuideName);
        Assert.Equal("Hero_Aluna", package.HeroIdentifier);
        Assert.Equal("midwars_ranked", package.TargetMap);
        Assert.Equal("ProPlayer", package.Author);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GuideController_Vote_UpdatesRatingCalculations()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "PlayerOne", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide guide = new()
        {
            Name = "Pebbles Combo Guide",
            HeroName = "Pebbles",
            HeroIdentifier = "Hero_Pebbles",
            TargetMap = "all",
            Intro = "Pebbles burst",
            Content = "Chuck and Stalagmites",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_PortalKey",
            LuxuryItems = "Item_StaffOfTheMaster",
            AbilityQueue = "1,2,1,2,1,4,1,2,3,3,4,3,3,5,5,4,5,5,5,5,5,5,5,5,5",
            UpVotes = 4,
            DownVotes = 1,
            Rating = 4.0f,
            Public = true,
            Featured = false,
            Author = account
        };
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "PlayerOne");

        // Act - Upvote
        IActionResult result = await controller.VoteGuide(guide.ID, isUpvote: true);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        int upVotes = doc.RootElement.GetProperty("upVotes").GetInt32();

        Assert.True(success);
        Assert.Equal(5, upVotes);

        HeroGuide? updated = await context.HeroGuides.FindAsync(guide.ID);
        Assert.NotNull(updated);
        Assert.Equal(5, updated.UpVotes);
    }

    [Fact]
    public async Task GuideController_ToggleOfficial_AllowsStaffAndProtectsAuthorCredit()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User authorUser = CreateTestUser("author@kongor.net");
        Account authorAccount = new() { Name = "TopPlayer", User = authorUser, IsMain = true };

        User staffUser = new()
        {
            EmailAddress = "admin@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = new Role { Name = "Staff" }
        };
        Account staffAccount = new() { Name = "StaffAdmin", User = staffUser, IsMain = true, Type = AccountType.Staff };

        context.Accounts.AddRange(authorAccount, staffAccount);

        HeroGuide guide = new()
        {
            Name = "Top Tier Valk Guide",
            HeroName = "Valkyrie",
            HeroIdentifier = "Hero_Valkyrie",
            TargetMap = "caldavar",
            Intro = "High skill build",
            Content = "Land arrows consistently",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GeometersBane",
            LuxuryItems = "Item_Wingbow",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            UpVotes = 15,
            DownVotes = 0,
            Rating = 5.0f,
            Public = true,
            Featured = false,
            Author = authorAccount
        };
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        GuideController staffController =
            CreateController(context, "StaffAdmin", isStaff: true, email: "admin@kongor.net");

        // Act - Staff toggles featured
        IActionResult result = await staffController.ToggleOfficial(guide.ID);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        bool isFeatured = doc.RootElement.GetProperty("isFeatured").GetBoolean();

        Assert.True(success);
        Assert.True(isFeatured);

        HeroGuide? featuredGuide =
            await context.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == guide.ID);
        Assert.NotNull(featuredGuide);
        Assert.True(featuredGuide.Featured);
        // Verify that the original player author is strictly preserved!
        Assert.Equal("TopPlayer", featuredGuide.Author.Name);
    }

    [Fact]
    public async Task GuideController_ToggleOfficial_DeniesNonStaff()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User regularUser = CreateTestUser("player@kongor.net");
        Account regularAccount = new()
        {
            Name = "RegularPlayer",
            User = regularUser,
            IsMain = true,
            Type = AccountType.Normal
        };
        context.Accounts.Add(regularAccount);

        HeroGuide guide = new()
        {
            Name = "Random Guide",
            HeroName = "Valkyrie",
            HeroIdentifier = "Hero_Valkyrie",
            TargetMap = "caldavar",
            Intro = "Intro",
            Content = "Content",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GeometersBane",
            LuxuryItems = "Item_Wingbow",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            UpVotes = 0,
            DownVotes = 0,
            Rating = 5.0f,
            Public = true,
            Featured = false,
            Author = regularAccount
        };
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        GuideController regularController = CreateController(context, "RegularPlayer");

        // Act - Regular user attempts to feature
        IActionResult result = await regularController.ToggleOfficial(guide.ID);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GuideController_Index_StrictMapFiltering_SeparatesCaldavarAndMidwars()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "GuideAuthor", User = user, IsMain = true };
        context.Accounts.Add(account);

        HeroGuide caldavarGuide = new()
        {
            Name = "Caldavar Strategy",
            HeroName = "Pandamonium",
            HeroIdentifier = "Hero_Panda",
            TargetMap = "caldavar",
            Intro = "Caldavar only",
            Content = "Content",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_PortalKey",
            LuxuryItems = "Item_StaffOfTheMaster",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            UpVotes = 5,
            DownVotes = 0,
            Rating = 5.0f,
            Public = true,
            Featured = false,
            Author = account
        };

        HeroGuide midwarsGuide = new()
        {
            Name = "Mid Wars Madness",
            HeroName = "Pandamonium",
            HeroIdentifier = "Hero_Panda",
            TargetMap = "midwars",
            Intro = "Mid Wars only",
            Content = "Content",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_PortalKey",
            LuxuryItems = "Item_StaffOfTheMaster",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            UpVotes = 3,
            DownVotes = 0,
            Rating = 5.0f,
            Public = true,
            Featured = false,
            Author = account
        };

        context.HeroGuides.AddRange(caldavarGuide, midwarsGuide);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "GuideAuthor");

        // Act 1: Query Caldavar
        IActionResult caldavarResult = await controller.Index(map: "caldavar");
        ViewResult caldavarView = Assert.IsType<ViewResult>(caldavarResult);
        IReadOnlyList<HeroGuideDto> caldavarList =
            Assert.IsAssignableFrom<IReadOnlyList<HeroGuideDto>>(caldavarView.Model);
        Assert.Single(caldavarList);
        Assert.Equal("Caldavar Strategy", caldavarList[0].Name);

        // Act 2: Query Mid Wars
        IActionResult midwarsResult = await controller.Index(map: "midwars");
        ViewResult midwarsView = Assert.IsType<ViewResult>(midwarsResult);
        IReadOnlyList<HeroGuideDto> midwarsList =
            Assert.IsAssignableFrom<IReadOnlyList<HeroGuideDto>>(midwarsView.Model);
        Assert.Single(midwarsList);
        Assert.Equal("Mid Wars Madness", midwarsList[0].Name);
    }

    [Fact]
    public async Task GuideController_ToggleOfficial_DeniesPrivateGuides()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User authorUser = CreateTestUser("player@kongor.net");
        Account authorAccount = new() { Name = "PrivateAuthor", User = authorUser, IsMain = true };
        User staffUser = CreateTestUser("admin@kongor.net");
        Account staffAccount = new() { Name = "StaffAdmin", User = staffUser, IsMain = true, Type = AccountType.Staff };
        context.Accounts.AddRange(authorAccount, staffAccount);

        HeroGuide privateGuide = new()
        {
            Name = "Secret Strat",
            HeroName = "Valkyrie",
            HeroIdentifier = "Hero_Valkyrie",
            TargetMap = "caldavar",
            Intro = "Secret",
            Content = "Secret",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GeometersBane",
            LuxuryItems = "Item_Wingbow",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            UpVotes = 0,
            DownVotes = 0,
            Rating = 5.0f,
            Public = false,
            Featured = false,
            Author = authorAccount
        };
        context.HeroGuides.Add(privateGuide);
        await context.SaveChangesAsync();

        GuideController staffController =
            CreateController(context, "StaffAdmin", isStaff: true, email: "admin@kongor.net");

        // Act - Staff attempts to endorse private guide
        IActionResult result = await staffController.ToggleOfficial(privateGuide.ID);

        // Assert - Should be rejected with BadRequest
        BadRequestObjectResult badResult = Assert.IsType<BadRequestObjectResult>(result);
        string json = JsonSerializer.Serialize(badResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        Assert.False(success);
    }

    [Fact]
    public async Task GuideController_Detail_HidesPrivateGuidesFromOtherUsers()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User authorUser = CreateTestUser("author@kongor.net");
        Account authorAccount = new() { Name = "GuideOwner", User = authorUser, IsMain = true };
        User otherUser = CreateTestUser("stranger@kongor.net");
        Account otherAccount = new() { Name = "OtherPlayer", User = otherUser, IsMain = true };
        context.Accounts.AddRange(authorAccount, otherAccount);

        HeroGuide privateGuide = new()
        {
            Name = "Classified Build",
            HeroName = "Pandamonium",
            HeroIdentifier = "Hero_Panda",
            TargetMap = "caldavar",
            Intro = "Classified",
            Content = "Classified",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_PortalKey",
            LuxuryItems = "Item_StaffOfTheMaster",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            UpVotes = 0,
            DownVotes = 0,
            Rating = 5.0f,
            Public = false,
            Featured = false,
            Author = authorAccount
        };
        context.HeroGuides.Add(privateGuide);
        await context.SaveChangesAsync();

        // Act 1: Author views private guide -> Allowed
        GuideController authorController = CreateController(context, "GuideOwner", email: "author@kongor.net");
        IActionResult authorResult = await authorController.Detail(privateGuide.ID);
        Assert.IsType<ViewResult>(authorResult);

        // Act 2: Other player attempts to view private guide -> NotFound
        GuideController strangerController = CreateController(context, "OtherPlayer", email: "stranger@kongor.net");
        IActionResult strangerResult = await strangerController.Detail(privateGuide.ID);
        Assert.IsType<NotFoundObjectResult>(strangerResult);
    }

    [Fact]
    public async Task GuideController_SaveGuide_ForcesSingleMap_DefaultsToCaldavar()
    {
        // Arrange
        MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser("creator@kongor.net");
        Account account = new() { Name = "GuideCreator", User = user, IsMain = true };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "GuideCreator", email: "creator@kongor.net");

        SaveHeroGuideRequestDto request = new()
        {
            Name = "Single Map Test Build",
            HeroIdentifier = "Hero_Panda",
            HeroName = "Pandamonium",
            TargetMap = "all", // "all" is no longer valid; should normalize to "caldavar"
            Intro = "Intro",
            Content = "Content",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "",
            CoreItems = "Item_PortalKey",
            LuxuryItems = "",
            AbilityQueue = "1,2,3,4,1,2,3,4,1,2,3,4,5,5,5,5,5,5,5,5,5,5,5,5,5",
            IsPublic = true
        };

        // Act
        IActionResult result = await controller.SaveGuide(request);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        HeroGuide? createdGuide = await context.HeroGuides.FirstOrDefaultAsync(g => g.Name == "Single Map Test Build");
        Assert.NotNull(createdGuide);
        Assert.Equal("caldavar", createdGuide.TargetMap);
    }

    [Fact]
    public void HeroAbilityRule_DefaultValues_AreProperlyConfigured()
    {
        // Arrange & Act
        HeroAbilityRule defaultRule = new()
        {
            Slot = 4,
            Key = "R",
            MaxLevel = 3,
            RequiredLevels = [6, 11, 16],
            IsInnateOrEarlyUlt = false
        };

        // Assert
        Assert.Equal(4, defaultRule.Slot);
        Assert.Equal("R", defaultRule.Key);
        Assert.Equal(3, defaultRule.MaxLevel);
        Assert.Equal(6, defaultRule.MinHeroLevel);
        Assert.Equal([6, 11, 16], defaultRule.RequiredLevels);
        Assert.False(defaultRule.IsInnateOrEarlyUlt);
    }

    [Fact]
    public void HeroAbilityRule_EarlyUltHero_AllowsLevel1Ultimate()
    {
        // Arrange & Act (e.g. Shadowblade / Master of Arms)
        HeroAbilityRule earlyUltRule = new()
        {
            Slot = 4,
            Key = "R",
            MaxLevel = 4,
            RequiredLevels = [1, 6, 11, 16],
            IsInnateOrEarlyUlt = true
        };

        // Assert
        Assert.Equal(1, earlyUltRule.MinHeroLevel);
        Assert.Equal(4, earlyUltRule.MaxLevel);
        Assert.True(earlyUltRule.IsInnateOrEarlyUlt);
    }

    [Fact]
    public async Task GuideController_SaveGuide_NormalizesAbilityQueue_ToHeroSpecificEntities()
    {
        // Arrange
        using MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser();
        Account account = new() { Name = "EmpathCreator", User = user, IsMain = true };
        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, "EmpathCreator");

        SaveHeroGuideRequestDto request = new()
        {
            HeroIdentifier = "Hero_Empath",
            Name = "Empath In-Depth Guide",
            TargetMap = "caldavar",
            Intro = "Intro text",
            Content = "Detailed strategy",
            StartingItems = "Item_RunesOfTheBlight,Item_ManaPotion",
            EarlyGameItems = "Item_BloodChalice,Item_Striders",
            CoreItems = "Item_PushStaff,Item_Morph",
            LuxuryItems = "Item_Astrolabe",
            AbilityQueue = "1,2,1,2,1,4,1,2,2,3,4,3,3,3,5,4,5,5,5,5,5,5,5,5,5",
            IsPublic = true
        };

        // Act
        IActionResult result = await controller.SaveGuide(request);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        HeroGuide? createdGuide = await context.HeroGuides.FirstOrDefaultAsync(g => g.Name == "Empath In-Depth Guide");
        Assert.NotNull(createdGuide);
        Assert.StartsWith("Ability_Empath1,Ability_Empath2,Ability_Empath1", createdGuide.AbilityQueue);
        Assert.Contains("Ability_Empath4", createdGuide.AbilityQueue);
        Assert.Contains("Ability_AttributeBoost", createdGuide.AbilityQueue);
    }

    [Fact]
    public void GuideResponseSuccess_NormalizesAbilityQueue_ToHeroSpecificEntities()
    {
        // Arrange
        User user = CreateTestUser();
        Account account = new() { Name = "AuthorUser", User = user, IsMain = true };
        HeroGuide guide = new()
        {
            ID = 101,
            Name = "Empath Guide",
            HeroName = "Empath",
            HeroIdentifier = "Hero_Empath",
            Author = account,
            StartingItems = "Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Striders",
            CoreItems = "Item_PushStaff",
            LuxuryItems = "Item_Astrolabe",
            AbilityQueue = "1|2|3|4|5|Ability_1|Ability_4",
            Intro = "Intro",
            Content = "Content",
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Public = true,
            Featured = false,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };

        // Act
        GuideResponseSuccess response = new(guide, 12345678);

        // Assert
        Assert.Equal(
            "Ability_Empath1|Ability_Empath2|Ability_Empath3|Ability_Empath4|Ability_AttributeBoost|Ability_Empath1|Ability_Empath4",
            response.AbilityQueue);
    }

    [Fact]
    public void HonItemMapping_MapsWebSlugs_ToHoNEngineEntities()
    {
        Assert.Equal("Item_Morph", HonItemMapping.ToEngineEntityName("kuldrassheepstick"));
        Assert.Equal("Item_Morph", HonItemMapping.ToEngineEntityName("sheepstick"));
        Assert.Equal("Item_PushStaff", HonItemMapping.ToEngineEntityName("tabletofcommand"));
        Assert.Equal("Item_RunesOfTheBlight", HonItemMapping.ToEngineEntityName("blightstones"));
        Assert.Equal("Item_Nuke", HonItemMapping.ToEngineEntityName("codex"));
        Assert.Equal("Item_Immunity", HonItemMapping.ToEngineEntityName("shrunkenhead"));
        Assert.Equal("Item_Protect", HonItemMapping.ToEngineEntityName("nullstone"));
        Assert.Equal("Item_Silence", HonItemMapping.ToEngineEntityName("hellflower"));
        Assert.Equal("Item_ManaBurn2", HonItemMapping.ToEngineEntityName("geometersbane"));
        Assert.Equal("Item_Freeze", HonItemMapping.ToEngineEntityName("frostwolfsskull"));
        Assert.Equal("Item_Pierce", HonItemMapping.ToEngineEntityName("shieldbreaker"));
        Assert.Equal("Item_Excruciator", HonItemMapping.ToEngineEntityName("barbedarmor"));
        Assert.Equal("Item_Intelligence6", HonItemMapping.ToEngineEntityName("stormspirit"));
        Assert.Equal("Item_Intelligence6", HonItemMapping.ToEngineEntityName("cyclone"));
        Assert.Equal("Item_HarkonsBlade", HonItemMapping.ToEngineEntityName("harkonsblade"));
        Assert.Equal("Item_SandScepter", HonItemMapping.ToEngineEntityName("sandscepter"));
        Assert.Equal("Item_GrimoireOfPower", HonItemMapping.ToEngineEntityName("grimoireofpower"));
        Assert.Equal("Item_LifeSteal4", HonItemMapping.ToEngineEntityName("symbolofrage"));
        Assert.Equal("Item_LifeSteal5", HonItemMapping.ToEngineEntityName("abyssalskull"));
        Assert.Equal("Item_SpellShards", HonItemMapping.ToEngineEntityName("spellshards"));
        Assert.Equal("Item_ArcaneBomb", HonItemMapping.ToEngineEntityName("arcanebomb"));
        Assert.Equal("Item_Lightning1", HonItemMapping.ToEngineEntityName("thunderclaw"));
        Assert.Equal("Item_Lightning2", HonItemMapping.ToEngineEntityName("chargedhammer"));
        Assert.Equal("Item_Damage10", HonItemMapping.ToEngineEntityName("mockofbrilliance"));
        Assert.Equal("Item_Damage9", HonItemMapping.ToEngineEntityName("doombringer"));
        Assert.Equal("Item_Astrolabe", HonItemMapping.ToEngineEntityName("astrolobe"));
    }

    [Fact]
    public void HonItemMapping_MapsEngineEntities_ToWebSlugs()
    {
        Assert.Equal("kuldrassheepstick", HonItemMapping.ToWebSlug("Item_Morph"));
        Assert.Equal("tabletofcommand", HonItemMapping.ToWebSlug("Item_PushStaff"));
        Assert.Equal("blightstones", HonItemMapping.ToWebSlug("Item_RunesOfTheBlight"));
        Assert.Equal("codex", HonItemMapping.ToWebSlug("Item_Nuke"));
        Assert.Equal("shrunkenhead", HonItemMapping.ToWebSlug("Item_Immunity"));
        Assert.Equal("nullstone", HonItemMapping.ToWebSlug("Item_Protect"));
        Assert.Equal("hellflower", HonItemMapping.ToWebSlug("Item_Silence"));
        Assert.Equal("geometersbane", HonItemMapping.ToWebSlug("Item_ManaBurn2"));
        Assert.Equal("frostwolfsskull", HonItemMapping.ToWebSlug("Item_Freeze"));
        Assert.Equal("shieldbreaker", HonItemMapping.ToWebSlug("Item_Pierce"));
        Assert.Equal("barbedarmor", HonItemMapping.ToWebSlug("Item_Excruciator"));
        Assert.Equal("sandscepter", HonItemMapping.ToWebSlug("Item_SandScepter"));
        Assert.Equal("grimoireofpower", HonItemMapping.ToWebSlug("Item_GrimoireOfPower"));
        Assert.Equal("stormspirit", HonItemMapping.ToWebSlug("Item_Intelligence6"));
        Assert.Equal("harkonsblade", HonItemMapping.ToWebSlug("Item_HarkonsBlade"));
        Assert.Equal("spellshards", HonItemMapping.ToWebSlug("Item_SpellShards"));
        Assert.Equal("arcanebomb", HonItemMapping.ToWebSlug("Item_ArcaneBomb"));
        Assert.Equal("symbolofrage", HonItemMapping.ToWebSlug("Item_LifeSteal4"));
        Assert.Equal("abyssalskull", HonItemMapping.ToWebSlug("Item_LifeSteal5"));
        Assert.Equal("thunderclaw", HonItemMapping.ToWebSlug("Item_Lightning1"));
        Assert.Equal("chargedhammer", HonItemMapping.ToWebSlug("Item_Lightning2"));
        Assert.Equal("mockofbrilliance", HonItemMapping.ToWebSlug("Item_Damage10"));
        Assert.Equal("doombringer", HonItemMapping.ToWebSlug("Item_Damage9"));
    }

    [Fact]
    public async Task GuideController_ToggleDefault_EnforcesSingleDefaultPerHero()
    {
        // Arrange
        using MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser("player@kongor.net");
        Account account = new() { Name = "PlayerOne", User = user, IsMain = true, DefaultGuideIDs = [] };
        context.Accounts.Add(account);

        HeroGuide guide1 = new()
        {
            ID = 10,
            Name = "Panda Guide A",
            HeroName = "Pandamonium",
            HeroIdentifier = "Hero_Panda",
            Author = account,
            StartingItems = "Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Striders",
            CoreItems = "Item_PushStaff",
            LuxuryItems = "Item_Morph",
            AbilityQueue = "1|2|3|4",
            Intro = "Intro A",
            Content = "Content A",
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Public = true,
            Featured = false,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        HeroGuide guide2 = new()
        {
            ID = 11,
            Name = "Panda Guide B",
            HeroName = "Pandamonium",
            HeroIdentifier = "Hero_Panda",
            Author = account,
            StartingItems = "Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Striders",
            CoreItems = "Item_PushStaff",
            LuxuryItems = "Item_Morph",
            AbilityQueue = "1|2|3|4",
            Intro = "Intro B",
            Content = "Content B",
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Public = true,
            Featured = false,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        HeroGuide guideOtherHero = new()
        {
            ID = 20,
            Name = "Empath Guide",
            HeroName = "Empath",
            HeroIdentifier = "Hero_Empath",
            Author = account,
            StartingItems = "Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Striders",
            CoreItems = "Item_Astrolabe",
            LuxuryItems = "Item_PushStaff",
            AbilityQueue = "1|2|3|4",
            Intro = "Intro Other",
            Content = "Content Other",
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Public = true,
            Featured = false,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };

        context.HeroGuides.AddRange(guide1, guide2, guideOtherHero);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, userName: "PlayerOne");

        // Act 1: Set guide1 as default for Panda
        IActionResult res1 = await controller.ToggleDefault(10);
        OkObjectResult okRes1 = Assert.IsType<OkObjectResult>(res1);
        Assert.Contains(10, account.DefaultGuideIDs);
        Assert.Single(account.DefaultGuideIDs);

        // Act 2: Set guideOtherHero as default for Empath
        IActionResult res2 = await controller.ToggleDefault(20);
        OkObjectResult okRes2 = Assert.IsType<OkObjectResult>(res2);
        Assert.Contains(10, account.DefaultGuideIDs);
        Assert.Contains(20, account.DefaultGuideIDs);
        Assert.Equal(2, account.DefaultGuideIDs.Count);

        // Act 3: Set guide2 as default for Panda -> Should replace guide1 with guide2 for Panda
        IActionResult res3 = await controller.ToggleDefault(11);
        OkObjectResult okRes3 = Assert.IsType<OkObjectResult>(res3);
        Assert.DoesNotContain(10, account.DefaultGuideIDs);
        Assert.Contains(11, account.DefaultGuideIDs);
        Assert.Contains(20, account.DefaultGuideIDs);
        Assert.Equal(2, account.DefaultGuideIDs.Count);

        // Act 4: Toggle guide2 again -> Should unset it
        IActionResult res4 = await controller.ToggleDefault(11);
        OkObjectResult okRes4 = Assert.IsType<OkObjectResult>(res4);
        Assert.DoesNotContain(11, account.DefaultGuideIDs);
        Assert.Contains(20, account.DefaultGuideIDs);
        Assert.Single(account.DefaultGuideIDs);
    }

    [Fact]
    public async Task GuideController_ToggleFavorite_AddsAndRemovesFavorites()
    {
        // Arrange
        using MerrickContext context = CreateInMemoryContext();
        User user = CreateTestUser("fav@kongor.net");
        Account account = new() { Name = "FavUser", User = user, IsMain = true, FavoriteGuideIDs = [] };
        context.Accounts.Add(account);

        HeroGuide guide1 = new()
        {
            ID = 50,
            Name = "Guide Fifty",
            HeroName = "Pebbles",
            HeroIdentifier = "Hero_Rocky",
            Author = account,
            StartingItems = "Item_RunesOfTheBlight",
            EarlyGameItems = "Item_Striders",
            CoreItems = "Item_PortalKey",
            LuxuryItems = "Item_Freeze",
            AbilityQueue = "1|2|3|4",
            Intro = "Intro Fifty",
            Content = "Content Fifty",
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Public = true,
            Featured = false,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        context.HeroGuides.Add(guide1);
        await context.SaveChangesAsync();

        GuideController controller = CreateController(context, userName: "FavUser");

        // Act 1: Add favorite
        IActionResult res1 = await controller.ToggleFavorite(50);
        OkObjectResult okRes1 = Assert.IsType<OkObjectResult>(res1);
        Assert.Contains(50, account.FavoriteGuideIDs);

        // Act 2: Remove favorite
        IActionResult res2 = await controller.ToggleFavorite(50);
        OkObjectResult okRes2 = Assert.IsType<OkObjectResult>(res2);
        Assert.DoesNotContain(50, account.FavoriteGuideIDs);
    }

    private static (string itemsPath, string imagesPath) ResolveItemPaths()
    {
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(baseDir);
        string? itemsPath = null;
        string? imagesPath = null;

        while (dir != null)
        {
            string candidateItems =
                Path.Combine(dir.FullName, "src", "libs", "database-context", "Data", "HeroItems.json");
            if (!File.Exists(candidateItems))
                candidateItems = Path.Combine(dir.FullName, "libs", "database-context", "src", "Data",
                    "HeroItems.json");
            if (!File.Exists(candidateItems))
                candidateItems = Path.Combine(dir.FullName, "MERRICK.DatabaseContext", "Data", "HeroItems.json");
            if (!File.Exists(candidateItems))
                candidateItems = Path.Combine(dir.FullName, "Data", "HeroItems.json");

            if (File.Exists(candidateItems) && itemsPath == null)
                itemsPath = candidateItems;

            string candidateImages =
                Path.Combine(dir.FullName, "games", "dawnbringer-overlay", "images", "items");
            if (!Directory.Exists(candidateImages))
                candidateImages = Path.Combine(dir.FullName, "src", "services", "web-ui", "wwwroot", "images", "items");
            if (!Directory.Exists(candidateImages))
                candidateImages = Path.Combine(dir.FullName, "services", "web-ui", "src", "wwwroot", "images", "items");
            if (!Directory.Exists(candidateImages))
                candidateImages = Path.Combine(dir.FullName, "DAWNBRINGER.WebPortal.UI", "wwwroot", "images", "items");

            if (Directory.Exists(candidateImages) && imagesPath == null)
                imagesPath = candidateImages;

            if (itemsPath != null && imagesPath != null)
                break;

            dir = dir.Parent;
        }

        return (itemsPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "HeroItems.json"),
            imagesPath ?? Path.Combine(AppContext.BaseDirectory, "games", "dawnbringer-overlay", "images", "items"));
    }

    private static (string guidesPath, string imagesPath) ResolveGuidePaths()
    {
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(baseDir);
        string? guidesPath = null;
        string? imagesPath = null;

        while (dir != null)
        {
            string candidateGuides =
                Path.Combine(dir.FullName, "src", "libs", "database-context", "Data", "HeroGuides.json");
            if (!File.Exists(candidateGuides))
                candidateGuides = Path.Combine(dir.FullName, "libs", "database-context", "src", "Data",
                    "HeroGuides.json");
            if (!File.Exists(candidateGuides))
                candidateGuides = Path.Combine(dir.FullName, "MERRICK.DatabaseContext", "Data", "HeroGuides.json");
            if (!File.Exists(candidateGuides))
                candidateGuides = Path.Combine(dir.FullName, "Data", "HeroGuides.json");

            if (File.Exists(candidateGuides) && guidesPath == null)
                guidesPath = candidateGuides;

            string candidateImages =
                Path.Combine(dir.FullName, "games", "dawnbringer-overlay", "images", "items");
            if (!Directory.Exists(candidateImages))
                candidateImages = Path.Combine(dir.FullName, "src", "services", "web-ui", "wwwroot", "images", "items");
            if (!Directory.Exists(candidateImages))
                candidateImages = Path.Combine(dir.FullName, "services", "web-ui", "src", "wwwroot", "images", "items");
            if (!Directory.Exists(candidateImages))
                candidateImages = Path.Combine(dir.FullName, "DAWNBRINGER.WebPortal.UI", "wwwroot", "images", "items");

            if (Directory.Exists(candidateImages) && imagesPath == null)
                imagesPath = candidateImages;

            if (guidesPath != null && imagesPath != null)
                break;

            dir = dir.Parent;
        }

        return (guidesPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "HeroGuides.json"),
            imagesPath ?? Path.Combine(AppContext.BaseDirectory, "games", "dawnbringer-overlay", "images", "items"));
    }

    [Fact]
    public void Audit_AllHeroItems_MapToEngineEntities_AndHaveValidWebSlugs()
    {
        (string itemsPath, string imagesPath) = ResolveItemPaths();

        Assert.True(File.Exists(itemsPath), $"HeroItems.json not found at {itemsPath}");
        bool hasOverlayDir = Directory.Exists(imagesPath);
        HashSet<string> existingImages = hasOverlayDir
            ? new HashSet<string>(
                Directory.GetFiles(imagesPath, "*.png").Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase)
            : [];

        string json = File.ReadAllText(itemsPath);
        using JsonDocument doc = JsonDocument.Parse(json);

        List<string> missingMappingErrors = [];
        foreach (JsonElement elem in doc.RootElement.EnumerateArray())
        {
            string code = elem.TryGetProperty("code", out JsonElement codeProp) ? codeProp.GetString() ?? "" : "";
            string name = elem.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(code)) continue;

            string engineEntity = HonItemMapping.ToEngineEntityName(code);
            Assert.StartsWith("Item_", engineEntity);

            string webSlug = HonItemMapping.ToWebSlug(engineEntity);
            Assert.False(string.IsNullOrWhiteSpace(webSlug));

            if (hasOverlayDir)
            {
                bool hasImage = existingImages.Contains(webSlug)
                                || existingImages.Contains(code.ToLowerInvariant())
                                || existingImages.Contains(code.ToLowerInvariant().Replace("_", ""))
                                || existingImages.Contains("item_" + webSlug)
                                || existingImages.Contains("item_" + code.ToLowerInvariant());

                if (!hasImage)
                {
                    missingMappingErrors.Add(
                        $"Item '{name}' (code: '{code}') -> Engine: '{engineEntity}' -> Slug: '{webSlug}' has no image file in {imagesPath}");
                }
            }
            else
            {
                string svg = global::DAWNBRINGER.WebPortal.UI.Services.CleanroomPlaceholderAssets.BuildSvgBadge("items", webSlug);
                Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Empty(missingMappingErrors);
    }

    [Fact]
    public void Audit_AllHeroGuides_ItemsMapProperlyAndHaveImages()
    {
        (string guidesPath, string imagesPath) = ResolveGuidePaths();

        Assert.True(File.Exists(guidesPath), $"HeroGuides.json not found at {guidesPath}");
        bool hasOverlayDir = Directory.Exists(imagesPath);
        HashSet<string> existingImages = hasOverlayDir
            ? new HashSet<string>(
                Directory.GetFiles(imagesPath, "*.png").Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase)
            : [];

        string json = File.ReadAllText(guidesPath);
        using JsonDocument doc = JsonDocument.Parse(json);

        HashSet<string> allGuideItems = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement elem in doc.RootElement.EnumerateArray())
        {
            void ExtractItems(string propName)
            {
                if (elem.TryGetProperty(propName, out JsonElement p))
                {
                    if (p.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement itemElem in p.EnumerateArray())
                        {
                            string? it = itemElem.GetString();
                            if (!string.IsNullOrWhiteSpace(it)) allGuideItems.Add(it);
                        }
                    }
                    else if (p.ValueKind == JsonValueKind.String)
                    {
                        string? itStr = p.GetString();
                        if (!string.IsNullOrWhiteSpace(itStr))
                        {
                            foreach (string it in itStr.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries))
                            {
                                allGuideItems.Add(it.Trim());
                            }
                        }
                    }
                }
            }

            ExtractItems("StartingItems");
            ExtractItems("EarlyGameItems");
            ExtractItems("CoreItems");
            ExtractItems("LuxuryItems");
        }

        List<string> missingMappingErrors = [];
        foreach (string token in allGuideItems)
        {
            string engineEntity = HonItemMapping.ToEngineEntityName(token);
            Assert.StartsWith("Item_", engineEntity);

            string webSlug = HonItemMapping.ToWebSlug(engineEntity);
            Assert.False(string.IsNullOrWhiteSpace(webSlug));

            if (hasOverlayDir)
            {
                bool hasImage = existingImages.Contains(webSlug)
                                || existingImages.Contains(token.ToLowerInvariant().Replace("item_", ""))
                                || existingImages.Contains(token.ToLowerInvariant())
                                || existingImages.Contains("item_" + webSlug)
                                || existingImages.Contains("item_" + token.ToLowerInvariant().Replace("item_", ""));

                if (!hasImage)
                {
                    missingMappingErrors.Add(
                        $"Guide item token '{token}' -> Engine: '{engineEntity}' -> Slug: '{webSlug}' has no image file in {imagesPath}");
                }
            }
            else
            {
                string svg = global::DAWNBRINGER.WebPortal.UI.Services.CleanroomPlaceholderAssets.BuildSvgBadge("items", webSlug);
                Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Empty(missingMappingErrors);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Index_WithHeroFilterAndSearchQuery_ExecutesSuccessfullyWithoutTranslationErrors()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        await using MerrickContext context = CreateInMemoryContext();
        User author = CreateTestUser("guide_search@test.com");
        Account authorAccount = new()
        {
            Name = "SearchAuthor",
            IsMain = true,
            User = author
        };
        context.Users.Add(author);
        context.Accounts.Add(authorAccount);

        HeroGuide guide = new()
        {
            Name = "Mid Wars Pyromancer Burst",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            TargetMap = "midwars",
            Author = authorAccount,
            Intro = "Fast burst guide for midwars",
            Content = "Burst combo",
            StartingItems = "Item_HealthPotion",
            EarlyGameItems = "Item_Steamboots",
            CoreItems = "Item_GrimoireOfPower",
            LuxuryItems = "Item_Spellshards",
            AbilityQueue = "1,2,1,2,1,4,1,2,2,3,4,3,3,3,5,4,5,5,5,5,5,5,5,5,5",
            Rating = 5.0f,
            UpVotes = 5,
            DownVotes = 0,
            Public = true,
            Featured = true
        };
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        GuideController controller =
            CreateController(context, "SearchAuthor", isStaff: false, email: "guide_search@test.com");

        // Act - Search with hero, map, and text query
        IActionResult result =
            await controller.Index(hero: "Pyromancer", map: "midwars", q: "burst", sort: "popular", page: 1);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        Assert.NotNull(viewResult.Model);
    }
}
