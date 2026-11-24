// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.DTOs;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Services;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Services;
using ZORGATH.WebPortal.API.Services.Email;
using ZORGATH.WebPortal.API.Validators;
using ZORGATH.WebPortal.API.Features.Registration;
using ZORGATH.WebPortal.API.Features.PlayerStats;
using ZORGATH.WebPortal.API.Features.Guides;
using ZORGATH.WebPortal.API.Features.Lore;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class AdversarialCancellationTests
{
    private static PasswordValidator CreatePasswordValidator()
    {
        return new PasswordValidator(Options.Create(new IdentityOptions()));
    }

    private static MerrickContext CreateInMemoryDatabase(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static (User user, Account account) SeedUserAndAccount(MerrickContext db, int userId = 1,
        int accountId = 10, string name = "ApiChallenger")
    {
        User user = new()
        {
            ID = userId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value",
            GoldCoins = 500,
            SilverCoins = 2000,
            PlinkoTickets = 5
        };
        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.SaveChanges();
        return (user, account);
    }

    [Fact]
    public async Task RegistrationController_InitiateRegistration_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IEmailService> mockEmail = new();
        PasswordValidator validator = CreatePasswordValidator();
        RegistrationController controller = new(new LocalRegistrationMediator(db, mockEmail.Object, validator));

        RegisterEmailAddressDTO payload = new("newplayer@kongor.net", "newplayer@kongor.net");

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancel

        // Act & Assert
        Func<Task> act = async () => await controller.InitiateRegistration(payload, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Assert no token saved
        int tokenCount = await db.Tokens.CountAsync();
        tokenCount.Should().Be(0);
    }

    [Fact]
    public async Task RegistrationController_CompleteRegistration_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IEmailService> mockEmail = new();
        PasswordValidator validator = CreatePasswordValidator();
        RegistrationController controller = new(new LocalRegistrationMediator(db, mockEmail.Object, validator));

        Guid tokenGuid = Guid.NewGuid();
        db.Tokens.Add(new Token
        {
            Value = tokenGuid,
            Purpose = TokenPurpose.EmailAddressVerification,
            Data = "player@kongor.net",
            EmailAddress = "player@kongor.net",
            TimestampCreated = TimeProvider.System.GetUtcNow()
        });
        await db.SaveChangesAsync();

        RegisterUserWithTokenDTO payload = new(tokenGuid.ToString(), "NewPlayer", "ValidPassword123!");

        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancel

        // Act & Assert
        Func<Task> act = async () => await controller.CompleteRegistration(payload, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Assert token unconsumed
        Token? token = await db.Tokens.SingleOrDefaultAsync(t => t.Value == tokenGuid);
        token.Should().NotBeNull();
        token!.TimestampConsumed.Should().BeNull();
    }

    [Fact]
    public async Task UserController_GetUser_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetUserByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Mock<IUserService> mockUserService = new();
        Mock<IAuthenticationService> mockAuthService = new();

        UserController controller = new(
            mockAccountRepo.Object,
            NullLogger<UserController>.Instance,
            mockUserService.Object,
            mockAuthService.Object);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.GetUser(1, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AdminMetricsController_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        Mock<IAdminMetricsService> mockMetrics = new();
        mockMetrics.Setup(m => m.GetAdminMetricsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        AdminMetricsController controller = new(mockMetrics.Object);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.GetMetrics(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StoreApiController_GetCatalogEntities_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        Mock<IInventoryRepository> mockInventory = new();
        Mock<IAccountRepository> mockAccount = new();
        mockInventory.Setup(i => i.GetStoreCatalogAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        StoreApiController controller = new(mockInventory.Object, mockAccount.Object);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.GetCatalogEntities(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PlayerStatsApiController_GetPlayerProfile_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();

        PlayerStatsApiController controller = new(new LocalPlayerStatsMediator(db, mockMatchRepo.Object, mockAccountRepo.Object));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.GetPlayerProfile(account.Name, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GuideApiController_GetGuides_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        GuideApiController controller = new(new LocalGuideMediator(db));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.GetGuides(currentAccountName: account.Name, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LoreApiController_GetArcs_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        LoreArc arc = new()
            { Title = "Arc 1", Slug = "arc-1", Description = "Desc 1", OrderIndex = 1, IsPublished = true };
        db.LoreArcs.Add(arc);
        await db.SaveChangesAsync();

        LoreApiController controller = new(new LocalLoreMediator(db));

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.GetArcs(false, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LoreApiController_RateComic_PreCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        (User user, Account account) = SeedUserAndAccount(db);

        LoreArc arc = new()
            { Title = "Arc 1", Slug = "arc-1", Description = "Desc 1", OrderIndex = 1, IsPublished = true };
        db.LoreArcs.Add(arc);
        await db.SaveChangesAsync();

        LoreComic comic = new()
        {
            ArcID = arc.ID,
            Title = "Comic 1",
            Slug = "comic-1",
            Synopsis = "Syn",
            CoverImageUrl = "/cov.webp",
            IsPublished = true
        };
        db.LoreComics.Add(comic);
        await db.SaveChangesAsync();

        LoreApiController controller = new(new LocalLoreMediator(db));
        RateComicRequestDto request = new(comic.ID, account.ID, 5);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await controller.RateComic(comic.ID, request, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Rating should NOT be committed to database
        int ratingCount = await db.LoreRatings.CountAsync();
        ratingCount.Should().Be(0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task UserService_RegisterUserAsync_PreCanceledToken_ThrowsOperationCanceledException()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryDatabase();
        Mock<IEmailService> mockEmail = new();
        PasswordValidator validator = CreatePasswordValidator();
        UserService service = new(db, mockEmail.Object, validator, NullLogger<UserService>.Instance);

        Guid tokenGuid = Guid.NewGuid();
        db.Tokens.Add(new Token
        {
            Value = tokenGuid,
            Purpose = TokenPurpose.EmailAddressVerification,
            Data = "user@kongor.net",
            EmailAddress = "user@kongor.net",
            TimestampCreated = TimeProvider.System.GetUtcNow()
        });
        await db.SaveChangesAsync();

        RegisterUserAndMainAccountDTO payload = new(tokenGuid.ToString(), "NewUser", "Password123!", "Password123!");

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await service.RegisterUserAsync(payload, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
