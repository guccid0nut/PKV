// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Services;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class Wave2AccountPrivacyTests
{
    private static AccountPrivacyController CreateAuthenticatedController(
        MerrickContext db,
        ISessionRepository sessionRepo,
        int userId)
    {
        AccountPrivacyService service = new(
            db,
            sessionRepo,
            NullLogger<AccountPrivacyService>.Instance);
        AccountPrivacyController controller = new(service);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        ], authenticationType: "DiscordTest"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public async Task ExportMyData_ReturnsSanitizedPlayerArchive_WithoutSecretsOrStaffNotes()
    {
        // protects: Self-service player data export (Discord-only compatible, zero password/token/staff-note leakage).
        // fails_when: Export fails for Discord-only users or leaks credential hashes/staff notes.
        // seam: AccountPrivacyController.ExportMyData against real EF Core MerrickContext.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            ID = 77,
            EmailAddress = "discord_77@kongor.local",
            DiscordId = "123456789012345678",
            DiscordUsername = "kongor_player",
            RoleID = 1,
            Role = userRole,
            SRPPasswordSalt = "SECRET_SRP_SALT_SHOULD_NOT_LEAK",
            SRPPasswordHash = "SECRET_SRP_HASH_SHOULD_NOT_LEAK",
            PBKDF2PasswordHash = "SECRET_PBKDF2_SHOULD_NOT_LEAK",
            GoldCoins = 500,
            SilverCoins = 1200,
            PlinkoTickets = 3
        };
        db.Users.Add(user);

        Account mainAcct = new()
        {
            ID = 501,
            Name = "HeroPlayer",
            UserID = 77,
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        db.Accounts.Add(mainAcct);

        db.PlayerReports.Add(new PlayerReport
        {
            ID = 901,
            ReporterAccountID = 501,
            ReporterAccountName = "HeroPlayer",
            ReportedAccountName = "RudePlayer",
            Reason = "Griefing",
            Status = "RESOLVED",
            StaffNotes = "INTERNAL_CONFIDENTIAL_STAFF_NOTE"
        });
        await db.SaveChangesAsync();

        Mock<ISessionRepository> sessionRepo = new();
        AccountPrivacyController controller = CreateAuthenticatedController(db, sessionRepo.Object, userId: 77);

        IActionResult res = await controller.ExportMyData();
        OkObjectResult ok = res.Should().BeOfType<OkObjectResult>().Subject;
        AccountDataExportDto dto = ok.Value.Should().BeOfType<AccountDataExportDto>().Subject;

        dto.UserId.Should().Be(77);
        dto.DiscordId.Should().Be("123456789012345678");
        dto.DiscordUsername.Should().Be("kongor_player");
        dto.Accounts.Should().ContainSingle(a => a.AccountName == "HeroPlayer");
        dto.ReportsFiled.Should().ContainSingle(r => r.ReportId == 901 && r.ReportedAccountName == "RudePlayer");

        string serialized = System.Text.Json.JsonSerializer.Serialize(dto);
        serialized.Should().NotContain("SECRET_SRP_SALT");
        serialized.Should().NotContain("SECRET_SRP_HASH");
        serialized.Should().NotContain("SECRET_PBKDF2");
        serialized.Should().NotContain("INTERNAL_CONFIDENTIAL_STAFF_NOTE");
    }

    [Fact]
    public async Task DeleteMyAccount_RequiresExactConfirmation_ThenRevokesSessionsUnlinksDiscordAndAnonymizesPii()
    {
        // protects: Discord-only self-service account deletion (zero email required) + session revocation + Account.Name <= 15 chars.
        // fails_when: Deletion succeeds without confirmation, leaves DiscordId/sessions active, or overflows Account.Name(15).
        // seam: AccountPrivacyController.DeleteMyAccount against EF Core MerrickContext + ISessionRepository.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Role userRole = new() { ID = 2, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            ID = 88,
            EmailAddress = "discord_88@kongor.local",
            DiscordId = "987654321098765432",
            DiscordUsername = "leaving_player",
            DiscordAvatar = "avatar_hash",
            RoleID = 2,
            Role = userRole,
            SRPPasswordSalt = "orig_salt",
            SRPPasswordHash = "orig_hash",
            PBKDF2PasswordHash = "orig_pbkdf2"
        };
        db.Users.Add(user);

        Account acct = new()
        {
            ID = 602,
            Name = "LeavingHero",
            UserID = 88,
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        db.Accounts.Add(acct);
        await db.SaveChangesAsync();

        Mock<ISessionRepository> sessionRepo = new();
        AccountPrivacyController controller = CreateAuthenticatedController(db, sessionRepo.Object, userId: 88);

        // Wrong confirmation phrase MUST be rejected with 400 Bad Request
        IActionResult badRes = await controller.DeleteMyAccount(new DeleteAccountRequestDto("delete"));
        badRes.Should().BeOfType<BadRequestObjectResult>();

        // Exact confirmation phrase MUST succeed with 200 OK
        IActionResult okRes = await controller.DeleteMyAccount(
            new DeleteAccountRequestDto(AccountPrivacyController.RequiredDeletionConfirmationPhrase));
        OkObjectResult ok = okRes.Should().BeOfType<OkObjectResult>().Subject;
        AccountDeletionResultDto result = ok.Value.Should().BeOfType<AccountDeletionResultDto>().Subject;

        result.Deleted.Should().BeTrue();
        result.AnonymizedAccountsCount.Should().Be(1);

        sessionRepo.Verify(s => s.RevokeAllSessionsForAccountAsync("LeavingHero", It.IsAny<CancellationToken>()), Times.Once);

        User updatedUser = await db.Users.Include(u => u.Accounts).SingleAsync(u => u.ID == 88);
        updatedUser.DiscordId.Should().BeNull();
        updatedUser.DiscordUsername.Should().BeNull();
        updatedUser.DiscordAvatar.Should().BeNull();
        updatedUser.EmailAddress.Should().EndWith("@anonymized.local");
        updatedUser.SRPPasswordHash.Should().NotBe("orig_hash");

        Account updatedAcct = updatedUser.Accounts.Single();
        updatedAcct.Name.Should().Be("del_602");
        updatedAcct.Name.Length.Should().BeLessOrEqualTo(15);
        updatedAcct.Type.Should().Be(AccountType.Disabled);
    }
}
