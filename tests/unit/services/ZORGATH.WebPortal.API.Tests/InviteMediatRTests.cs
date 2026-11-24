// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Invites;
using NEXUS.Contracts.CQRS.Registration;
using NEXUS.Contracts.Data;
using Xunit;
using ZORGATH.WebPortal.API.Features.Invites;
using ZORGATH.WebPortal.API.Features.Registration;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class InviteMediatRTests
{
    [Fact]
    public async Task GenerateInviteHandler_WhenQuotaPermits_CreatesAndReturnsCode()
    {
        Mock<IInviteRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetUserQuotaStatusAsync(1, default))
            .ReturnsAsync((TotalGames: 150, MaxAllowedActiveCodes: 5, ActiveUnusedCodes: 1, RemainingCapacity: 4, CanGenerate: true));

        mockRepo.Setup(r => r.CreateInviteCodeAsync(1, It.IsAny<string?>(), default))
            .ReturnsAsync(new UserInvite { ID = 10, Code = "KONGOR-TEST-1234", CreatedByUserID = 1 });

        GenerateInviteHandler handler = new(mockRepo.Object, NullLogger<GenerateInviteHandler>.Instance);

        var result = await handler.Handle(new GenerateInviteCommand(1, "127.0.0.1"), default);

        result.Success.Should().BeTrue();
        result.Code.Should().Be("KONGOR-TEST-1234");
    }

    [Fact]
    public async Task GenerateInviteHandler_WhenUnder100Games_RejectsWithExplanation()
    {
        Mock<IInviteRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetUserQuotaStatusAsync(2, default))
            .ReturnsAsync((TotalGames: 42, MaxAllowedActiveCodes: 5, ActiveUnusedCodes: 0, RemainingCapacity: 0, CanGenerate: false));

        GenerateInviteHandler handler = new(mockRepo.Object, NullLogger<GenerateInviteHandler>.Instance);

        var result = await handler.Handle(new GenerateInviteCommand(2, "127.0.0.1"), default);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("100 completed matchmaking games");
    }

    [Fact]
    public async Task ValidateInviteHandler_CallsRepository_ReturnsValidity()
    {
        Mock<IInviteRepository> mockRepo = new();
        mockRepo.Setup(r => r.ValidateInviteCodeAsync("VALID-CODE", default))
            .ReturnsAsync((IsValid: true, Message: null, Invite: new UserInvite { Code = "VALID-CODE" }));

        ValidateInviteHandler handler = new(mockRepo.Object);

        var result = await handler.Handle(new ValidateInviteQuery("VALID-CODE"), default);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task PruneInviteSubtreeHandler_RevokesAndOptionallyBansAccounts()
    {
        Mock<IInviteRepository> mockInviteRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();

        User rootUser = new()
        {
            ID = 5,
            EmailAddress = "root@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s"
        };
        mockInviteRepo.Setup(r => r.GetInviteLineageTreeAsync(5, 4, default))
            .ReturnsAsync([rootUser]);

        mockInviteRepo.Setup(r => r.RevokeUnusedInvitesForUserAsync(5, "Cheating", default))
            .ReturnsAsync(3);

        mockAccountRepo.Setup(r => r.GetAccountsForUserAsync(5, default))
            .ReturnsAsync([new Account { ID = 50, Name = "BannedSub", User = rootUser }]);

        PruneInviteSubtreeHandler handler = new(mockInviteRepo.Object, mockAccountRepo.Object, NullLogger<PruneInviteSubtreeHandler>.Instance);

        var result = await handler.Handle(new PruneInviteSubtreeCommand(5, "Cheating", BanAccounts: true), default);

        result.Success.Should().BeTrue();
        result.RevokedCodes.Should().Be(3);
        result.BannedAccounts.Should().Be(1);
    }

    [Fact]
    public async Task RegisterDirectUserHandler_Kongor1111SharedInvite_AllowsMultipleRegistrationsUntilOneWeekExpiry()
    {
        // protects: RegisterDirectUserHandler allows multiple distinct players to register using KONGOR-1111 while invite-only is active, then rejects with 400 after 7 days.
        // fails_when: RegisterDirectUserHandler stamps TimestampRedeemed on KONGOR-1111 after the first user, or fails to reject KONGOR-1111 once TimestampExpires passes.
        // seam: Real RegisterDirectUserHandler + real InviteRepository + real SeedDataHandlers.EnsureSharedInviteCodesAsync against MerrickContext.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.Roles.AddRange(
            new MERRICK.DatabaseContext.Entities.Utility.Role { ID = 1, Name = MERRICK.DatabaseContext.Constants.UserRoles.User },
            new MERRICK.DatabaseContext.Entities.Utility.Role { ID = 2, Name = MERRICK.DatabaseContext.Constants.UserRoles.Administrator });

        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.local",
            RoleID = 2,
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account adminAcc = new()
        {
            ID = 10,
            UserID = 1,
            User = adminUser,
            Name = "Admin",
            Type = NEXUS.Contracts.Domain.AccountType.Staff,
            IsMain = true
        };
        adminUser.Accounts.Add(adminAcc);
        db.Users.Add(adminUser);
        db.Accounts.Add(adminAcc);
        await db.SaveChangesAsync();

        await MERRICK.DatabaseContext.Handlers.SeedDataHandlers.EnsureSharedInviteCodesAsync(db, NullLogger.Instance);

        ManualTimeProvider clock = new(UserInvite.SharedWeeklyInviteCreatedUtc.AddDays(2));
        InviteRepository inviteRepo = new(db, redis: null, NullLogger<InviteRepository>.Instance, clock);
        await inviteRepo.LogModeToggleAsync(adminAcc.ID, adminAcc.Name, enabled: true, notes: "Enable invite-only");

        var passwordValidator = new PasswordValidator(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Identity.IdentityOptions()));
        var handler = new RegisterDirectUserHandler(
            db,
            passwordValidator,
            NullLogger<RegisterDirectUserHandler>.Instance,
            inviteRepo,
            clock);

        // Player 1 registers with KONGOR-1111 -> 201 Created
        var res1 = await handler.Handle(new RegisterDirectUserCommand(
            Email: "",
            Name: "KongorOne",
            Password: "SecurePassword123!",
            DiscordId: "100000001",
            DiscordUsername: "kongor_one",
            InviteCode: "KONGOR-1111"), default);
        res1.Success.Should().BeTrue();
        res1.StatusCode.Should().Be(201);

        // Player 2 registers with the SAME KONGOR-1111 code -> 201 Created (not burned!)
        clock.UtcNow = UserInvite.SharedWeeklyInviteCreatedUtc.AddDays(6);
        var res2 = await handler.Handle(new RegisterDirectUserCommand(
            Email: "",
            Name: "KongorTwo",
            Password: "SecurePassword123!",
            DiscordId: "100000002",
            DiscordUsername: "kongor_two",
            InviteCode: "kongor-1111"), default);
        res2.Success.Should().BeTrue();
        res2.StatusCode.Should().Be(201);

        // Advance past 1 week -> Player 3 rejected with "This invite code has expired."
        clock.UtcNow = UserInvite.SharedWeeklyInviteExpiresUtc.AddMinutes(1);
        var res3 = await handler.Handle(new RegisterDirectUserCommand(
            Email: "",
            Name: "KongorThree",
            Password: "SecurePassword123!",
            DiscordId: "100000003",
            DiscordUsername: "kongor_three",
            InviteCode: "KONGOR-1111"), default);
        res3.Success.Should().BeFalse();
        res3.StatusCode.Should().Be(400);
        res3.ErrorMessage.Should().Be("This invite code has expired.");
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtc;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
