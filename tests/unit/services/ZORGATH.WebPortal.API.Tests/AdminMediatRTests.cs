// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using FluentValidation.Results;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Accounts;
using NEXUS.Contracts.CQRS.Admin.Applications;
using NEXUS.Contracts.CQRS.Admin.Inventory;
using NEXUS.Contracts.CQRS.Admin.Moderation;
using NEXUS.Contracts.CQRS.Admin.Reports;
using NEXUS.Contracts.CQRS.Admin.Users;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Events;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.Admin.Accounts;
using ZORGATH.WebPortal.API.Features.Admin.Applications;
using ZORGATH.WebPortal.API.Features.Admin.Inventory;
using ZORGATH.WebPortal.API.Features.Admin.Moderation;
using ZORGATH.WebPortal.API.Features.Admin.Reports;
using ZORGATH.WebPortal.API.Features.Admin.Users;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class AdminMediatRTests
{
    [Fact]
    public async Task BanAccountHandler_ExecutesBan_PublishesEventAndReturnsSuccess()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IAccountRepository> mockRepo = new();
        Mock<IEventBus> mockBus = new();

        User user = new()
        {
            ID = 101,
            EmailAddress = "toxic@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 101,
            Name = "ToxicPlayer",
            Type = AccountType.Normal,
            User = user
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        BanAccountHandler handler = new(
            db,
            mockRepo.Object,
            NullLogger<BanAccountHandler>.Instance,
            mockBus.Object);

        DateTimeOffset expiresAt = TimeProvider.System.GetUtcNow().AddDays(14);
        BanAccountCommand command = new(101, expiresAt, "Harassment in all-chat", BannedBy: 999);

        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.AffectedId.Should().Be(101);
        result.Message.Should().Contain("ToxicPlayer");

        mockRepo.Verify(r => r.BanAccountAsync(101, expiresAt, "Harassment in all-chat", 999, It.IsAny<CancellationToken>()), Times.Once);
        mockBus.Verify(b => b.PublishAsync(It.Is<AccountBannedEvent>(e => e.AccountId == 101 && e.BanReason == "Harassment in all-chat"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void BanAccountValidator_EnforcesRules()
    {
        BanAccountValidator validator = new();

        ValidationResult invalidId = validator.Validate(new BanAccountCommand(0, null, "Reason"));
        invalidId.IsValid.Should().BeFalse();
        invalidId.Errors.Should().Contain(e => e.PropertyName == "AccountId");

        ValidationResult invalidReason = validator.Validate(new BanAccountCommand(10, null, ""));
        invalidReason.IsValid.Should().BeFalse();
        invalidReason.Errors.Should().Contain(e => e.PropertyName == "Reason");

        ValidationResult valid = validator.Validate(new BanAccountCommand(10, null, "Legitimate ban reason"));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task MuteAccountHandler_MutesAccount_PublishesEventAndSaves()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEventBus> mockBus = new();

        User user = new()
        {
            ID = 202,
            EmailAddress = "spammy@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 202,
            Name = "SpammyPlayer",
            Type = AccountType.Normal,
            User = user
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        MuteAccountHandler handler = new(
            db,
            NullLogger<MuteAccountHandler>.Instance,
            mockBus.Object);

        DateTimeOffset expiresAt = TimeProvider.System.GetUtcNow().AddDays(3);
        MuteAccountCommand command = new(202, expiresAt, "Mic spamming", MutedBy: 999);

        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        account.MuteExpiration.Should().Be(expiresAt);
        account.MuteReason.Should().Be("Mic spamming");

        mockBus.Verify(b => b.PublishAsync(It.Is<AccountMutedEvent>(e => e.AccountId == 202 && e.MuteReason == "Mic spamming"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DismissReportHandler_AppealsReport_ClearsSanctions()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEventBus> mockBus = new();

        User user = new()
        {
            ID = 50,
            EmailAddress = "appealed@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 303,
            Name = "WronglyBanned",
            Type = AccountType.Disabled,
            BanExpiration = TimeProvider.System.GetUtcNow().AddYears(10),
            BanReason = "Mistake",
            MuteExpiration = TimeProvider.System.GetUtcNow().AddDays(5),
            MuteReason = "Mistake",
            User = user
        };
        PlayerReport report = new()
        {
            ID = 10,
            ReportedAccountID = 303,
            ReportedAccountName = "WronglyBanned",
            ReporterAccountName = "ReporterAcc",
            Status = "PENDING",
            Reason = "Griefing"
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.PlayerReports.Add(report);
        await db.SaveChangesAsync();

        DismissReportHandler handler = new(
            db,
            NullLogger<DismissReportHandler>.Instance,
            mockBus.Object);

        DismissReportCommand command = new(10, StaffAccountId: 999, Comment: "Innocent after replay review");
        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        report.Status.Should().Be("APPEALED");
        report.StaffNotes.Should().Be("Innocent after replay review");
        account.BanExpiration.Should().BeNull();
        account.MuteExpiration.Should().BeNull();
        account.Type.Should().Be(AccountType.Legacy);

        mockBus.Verify(b => b.PublishAsync(It.Is<AccountUnmutedEvent>(e => e.AccountId == 303), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ActionReportHandler_AppliesBanAction_UpdatesReportAndTarget()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user = new()
        {
            ID = 60,
            EmailAddress = "feeder@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 404,
            Name = "IntentionalFeeder",
            Type = AccountType.Normal,
            User = user
        };
        PlayerReport report = new()
        {
            ID = 20,
            ReportedAccountID = 404,
            ReportedAccountName = "IntentionalFeeder",
            ReporterAccountName = "Teammate",
            Status = "PENDING",
            Reason = "Intentional Feeding"
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.PlayerReports.Add(report);
        await db.SaveChangesAsync();

        ActionReportHandler handler = new(
            db,
            NullLogger<ActionReportHandler>.Instance);

        ActionReportCommand command = new(
            ReportId: 20,
            ActionType: "ban",
            DurationHours: 48,
            Reason: "Feeding",
            StaffNotes: "Confirmed courier feeding",
            StaffAccountId: 999);

        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        report.Status.Should().Be("RESOLVED");
        report.PunishmentType.Should().Be("BAN");
        account.Type.Should().Be(AccountType.Disabled);
        account.BanExpiration.Should().NotBeNull();
        account.BanReason.Should().Be("Confirmed courier feeding");
    }

    [Fact]
    public async Task AdjustUserBalanceHandler_ModifiesBalances_AddsTransaction()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user = new()
        {
            ID = 70,
            EmailAddress = "rich@kongor.net",
            GoldCoins = 100,
            SilverCoins = 500,
            PlinkoTickets = 2,
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 701,
            Name = "RichPlayer",
            IsMain = true,
            User = user
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AdjustUserBalanceHandler handler = new(
            db,
            NullLogger<AdjustUserBalanceHandler>.Instance);

        AdjustUserBalanceCommand command = new(
            UserId: 70,
            GoldDelta: 50,
            SilverDelta: -200,
            TicketsDelta: 3,
            Reason: "Tournament reward");

        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        user.GoldCoins.Should().Be(150);
        user.SilverCoins.Should().Be(300);
        user.PlinkoTickets.Should().Be(5);

        db.EconomyTransactions.Should().ContainSingle(t =>
            t.UserID == 70 &&
            t.GoldCoinsDelta == 50 &&
            t.SilverCoinsDelta == -200 &&
            t.PlinkoTicketsDelta == 3 &&
            t.TransactionType == "AdminAdjustBalance");
    }

    [Fact]
    public void AdjustUserBalanceValidator_EnforcesRules()
    {
        AdjustUserBalanceValidator validator = new();

        ValidationResult invalidUser = validator.Validate(new AdjustUserBalanceCommand(0, 10, 0, 0));
        invalidUser.IsValid.Should().BeFalse();
        invalidUser.Errors.Should().Contain(e => e.PropertyName == "UserId");

        ValidationResult valid = validator.Validate(new AdjustUserBalanceCommand(5, 10, 0, 0));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task SetAccountRoleHandler_UpdatesType_PublishesBannedEventWhenDisabled()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEventBus> mockBus = new();

        User user = new()
        {
            ID = 80,
            EmailAddress = "cheater@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 801,
            Name = "CheaterAcc",
            Type = AccountType.Normal,
            User = user
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        SetAccountRoleHandler handler = new(
            db,
            NullLogger<SetAccountRoleHandler>.Instance,
            mockBus.Object);

        SetAccountRoleCommand command = new(801, (int)AccountType.Disabled, "Cheat detected");
        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        account.Type.Should().Be(AccountType.Disabled);

        mockBus.Verify(b => b.PublishAsync(It.Is<AccountBannedEvent>(e => e.AccountId == 801 && e.BanReason == "Cheat detected"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SetAccountRoleValidator_EnforcesRules()
    {
        SetAccountRoleValidator validator = new();

        ValidationResult invalidAccount = validator.Validate(new SetAccountRoleCommand(0, (int)AccountType.Normal));
        invalidAccount.IsValid.Should().BeFalse();
        invalidAccount.Errors.Should().Contain(e => e.PropertyName == "AccountId");

        ValidationResult invalidType = validator.Validate(new SetAccountRoleCommand(10, 99999));
        invalidType.IsValid.Should().BeFalse();
        invalidType.Errors.Should().Contain(e => e.PropertyName == "AccountType");

        ValidationResult valid = validator.Validate(new SetAccountRoleCommand(10, (int)AccountType.Staff));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AdminApiController_Endpoints_DelegateToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IAccountRepository> mockRepo = new();
        Mock<IMediator> mockMediator = new();

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetPlayerReportsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlayerReportDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetPlayerReportsCountQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        mockMediator
            .Setup(m => m.Send(It.IsAny<DismissReportCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Dismissed", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<ActionReportCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Actioned", 2));

        mockMediator
            .Setup(m => m.Send(It.IsAny<BanAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Banned", 3));

        mockMediator
            .Setup(m => m.Send(It.IsAny<UnbanAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Unbanned", 4));

        mockMediator
            .Setup(m => m.Send(It.IsAny<MuteAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Muted", 5));

        mockMediator
            .Setup(m => m.Send(It.IsAny<UnmuteAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Unmuted", 6));

        AdminApiController controller = new(mockMediator.Object);

        // 1. GetPlayerReports
        IActionResult reportsResult = await controller.GetPlayerReports(null, null, null, null, null);
        reportsResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerReportsQuery>(), It.IsAny<CancellationToken>()), Times.Once);

        // 2. GetPlayerReportsCount
        IActionResult countResult = await controller.GetPlayerReportsCount(null, null, null, null, null);
        var okCount = countResult.Should().BeOfType<OkObjectResult>().Subject;
        okCount.Value.Should().Be(42);
        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerReportsCountQuery>(), It.IsAny<CancellationToken>()), Times.Once);

        // 3. DismissReport
        IActionResult dismissResult = await controller.DismissReport(1, new DismissReportRequestDto(999, "Note"));
        dismissResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<DismissReportCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // 4. ActionReport
        IActionResult actionResult = await controller.ActionReport(new ActionReportRequestDto(2, 999, "ban", "Reason", 24, "Notes"));
        actionResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<ActionReportCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // 5. BanAccount
        IActionResult banResult = await controller.BanAccount(3, new BanAccountRequestDto(null, "Toxic", 999));
        banResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<BanAccountCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // 6. UnbanAccount
        IActionResult unbanResult = await controller.UnbanAccount(4);
        unbanResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<UnbanAccountCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // 7. MuteAccount
        IActionResult muteResult = await controller.MuteAccount(5, new MuteAccountRequestDto(null, "Spam", 999));
        muteResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<MuteAccountCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // 8. UnmuteAccount
        IActionResult unmuteResult = await controller.UnmuteAccount(6);
        unmuteResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<UnmuteAccountCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdminUserController_Endpoints_DelegateToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IMediator> mockMediator = new();

        mockMediator
            .Setup(m => m.Send(It.IsAny<AdjustUserBalanceCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Balance adjusted", 70));

        mockMediator
            .Setup(m => m.Send(It.IsAny<SetAccountRoleCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Role set", 801));

        AdminUserController controller = new(
            mockMediator.Object,
            NullLogger<AdminUserController>.Instance);

        // 1. AdjustUserBalance
        IActionResult adjustResult = await controller.AdjustUserBalance(70, new AdjustUserBalanceRequest
        {
            GoldDelta = 10,
            SilverDelta = 20,
            TicketsDelta = 1
        });
        adjustResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<AdjustUserBalanceCommand>(), It.IsAny<CancellationToken>()), Times.Once);

        // 2. SetAccountRole
        IActionResult roleResult = await controller.SetAccountRole(801, new SetAccountRoleRequest
        {
            Type = (int)AccountType.Staff
        });
        roleResult.Should().BeOfType<OkObjectResult>();
        mockMediator.Verify(m => m.Send(It.IsAny<SetAccountRoleCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DirectPunishAccountHandler_AppliesSanctions_AndCreatesReport()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEventBus> mockBus = new();

        User user = new()
        {
            ID = 90,
            EmailAddress = "toxic2@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 901,
            Name = "ToxicDirect",
            Type = AccountType.Normal,
            User = user
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        DirectPunishAccountHandler handler = new(db, mockBus.Object);
        DirectPunishAccountCommand command = new("ToxicDirect", "mute", 24, "Trolling", "Observed trolling", 999);

        AdminActionResultDto result = await handler.Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue();
        account.MuteExpiration.Should().NotBeNull();
        account.MuteReason.Should().Be("Observed trolling");

        db.PlayerReports.Should().ContainSingle(r => r.ReportedAccountName == "ToxicDirect" && r.PunishmentType == "MUTE");
        mockBus.Verify(b => b.PublishAsync(It.Is<AccountMutedEvent>(e => e.AccountId == 901), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetPlayerReportByIdHandler_ReturnsMappedDto()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.PlayerReports.Add(new PlayerReport
        {
            ID = 55,
            ReportedAccountID = 10,
            ReportedAccountName = "Offender55",
            ReporterAccountName = "Reporter55",
            Status = "PENDING",
            Reason = "Griefing"
        });
        await db.SaveChangesAsync();

        GetPlayerReportByIdHandler handler = new(db);
        PlayerReportDto? report = await handler.Handle(new GetPlayerReportByIdQuery(55), CancellationToken.None);

        report.Should().NotBeNull();
        report!.ID.Should().Be(55);
        report.ReportedAccountName.Should().Be("Offender55");

        PlayerReportDto? missing = await handler.Handle(new GetPlayerReportByIdQuery(999), CancellationToken.None);
        missing.Should().BeNull();
    }

    [Fact]
    public async Task HostApplicationHandlers_ManageApplicationsCorrectly()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User hostUser = new()
        {
            ID = 301,
            EmailAddress = "applicant@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account mainAccount = new()
        {
            ID = 301,
            Name = "MainApplicant",
            User = hostUser
        };
        HostApplication app = new()
        {
            ID = 1,
            User = hostUser,
            MainAccount = mainAccount,
            HostUsername = "HostAlphaServer",
            Location = "US East",
            Status = HostApplicationStatus.Pending
        };
        db.Users.Add(hostUser);
        db.Accounts.Add(mainAccount);
        db.HostApplications.Add(app);
        await db.SaveChangesAsync();

        GetHostApplicationsHandler getHandler = new(db);
        List<HostApplicationDto> list = await getHandler.Handle(new GetHostApplicationsQuery(), CancellationToken.None);
        list.Should().ContainSingle(h => h.HostUsername == "HostAlphaServer");

        ApproveHostApplicationHandler approveHandler = new(db);
        AdminActionResultDto approveRes = await approveHandler.Handle(new ApproveHostApplicationCommand(1), CancellationToken.None);
        approveRes.Success.Should().BeTrue();
        app.Status.Should().Be(HostApplicationStatus.Approved);
        app.HostAccountID.Should().NotBeNull();

        RevokeHostApplicationHandler revokeHandler = new(db);
        AdminActionResultDto revokeRes = await revokeHandler.Handle(new RevokeHostApplicationCommand(1, "Server shutdown"), CancellationToken.None);
        revokeRes.Success.Should().BeTrue();
        app.Status.Should().Be(HostApplicationStatus.Revoked);
        app.HostAccount?.Type.Should().Be(AccountType.Disabled);

        RejectHostApplicationHandler rejectHandler = new(db);
        HostApplication app2 = new()
        {
            ID = 2,
            HostUsername = "HostBetaServer",
            Status = HostApplicationStatus.Pending
        };
        db.HostApplications.Add(app2);
        await db.SaveChangesAsync();

        AdminActionResultDto rejectRes = await rejectHandler.Handle(new RejectHostApplicationCommand(2, "Hardware insufficient"), CancellationToken.None);
        rejectRes.Success.Should().BeTrue();
        app2.Status.Should().Be(HostApplicationStatus.Rejected);
    }

    [Fact]
    public async Task StreamerApplicationHandlers_ManageApplicationsCorrectly()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User streamerUser = new()
        {
            ID = 401,
            EmailAddress = "streamer@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account mainAccount = new()
        {
            ID = 401,
            Name = "StreamerGuy",
            Type = AccountType.Normal,
            User = streamerUser
        };
        StreamerApplication app = new()
        {
            ID = 10,
            User = streamerUser,
            MainAccount = mainAccount,
            Status = StreamerApplicationStatus.Pending
        };
        db.Users.Add(streamerUser);
        db.Accounts.Add(mainAccount);
        db.StreamerApplications.Add(app);
        await db.SaveChangesAsync();

        GetStreamerApplicationsHandler getHandler = new(db);
        List<StreamerApplicationDto> list = await getHandler.Handle(new GetStreamerApplicationsQuery(), CancellationToken.None);
        list.Should().ContainSingle();

        ApproveStreamerApplicationHandler approveHandler = new(db);
        AdminActionResultDto approveRes = await approveHandler.Handle(new ApproveStreamerApplicationCommand(10), CancellationToken.None);
        approveRes.Success.Should().BeTrue();
        app.Status.Should().Be(StreamerApplicationStatus.Approved);
        mainAccount.Type.Should().Be(AccountType.Streamer);

        RevokeStreamerApplicationHandler revokeHandler = new(db);
        AdminActionResultDto revokeRes = await revokeHandler.Handle(new RevokeStreamerApplicationCommand(10), CancellationToken.None);
        revokeRes.Success.Should().BeTrue();
        app.Status.Should().Be(StreamerApplicationStatus.Revoked);
        mainAccount.Type.Should().Be(AccountType.Normal);

        RejectStreamerApplicationHandler rejectHandler = new(db);
        StreamerApplication app2 = new()
        {
            ID = 20,
            Status = StreamerApplicationStatus.Pending
        };
        db.StreamerApplications.Add(app2);
        await db.SaveChangesAsync();

        AdminActionResultDto rejectRes = await rejectHandler.Handle(new RejectStreamerApplicationCommand(20, "Not enough followers"), CancellationToken.None);
        rejectRes.Success.Should().BeTrue();
        app2.Status.Should().Be(StreamerApplicationStatus.Rejected);
    }

    [Fact]
    public async Task AdminAccountHandlers_RecentAndDetail_WorkCorrectly()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 501,
            EmailAddress = "recent@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 501,
            Name = "RecentPlayer",
            User = user
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        GetRecentAccountsHandler recentHandler = new(db);
        List<AccountDto> recent = await recentHandler.Handle(new GetRecentAccountsQuery(10), CancellationToken.None);
        recent.Should().ContainSingle(a => a.Name == "RecentPlayer");

        GetAccountDetailHandler detailHandler = new(db);
        AccountDto? detail = await detailHandler.Handle(new GetAccountDetailQuery("RecentPlayer"), CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.Name.Should().Be("RecentPlayer");
    }

    [Fact]
    public async Task InventoryHandlers_AddRemoveReset_WorkCorrectly()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 601,
            EmailAddress = "inv@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 601,
            Name = "InvPlayer",
            User = user
        };
        StoreItem storeItem = new()
        {
            Id = 1,
            Code = "hero_skin_pyro",
            Name = "Pyromancer Skin",
            LocalContent = "{}"
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(storeItem);
        await db.SaveChangesAsync();

        AddInventoryItemHandler addHandler = new(db);
        AdminActionResultDto addRes = await addHandler.Handle(new AddInventoryItemCommand("InvPlayer", "hero_skin_pyro"), CancellationToken.None);
        addRes.Success.Should().BeTrue();
        account.User.InventoryItems.Should().ContainSingle(i => i.ItemCode == "hero_skin_pyro");

        RemoveInventoryItemHandler removeHandler = new(db);
        AdminActionResultDto removeRes = await removeHandler.Handle(new RemoveInventoryItemCommand("InvPlayer", "hero_skin_pyro"), CancellationToken.None);
        removeRes.Success.Should().BeTrue();
        account.User.InventoryItems.Should().BeEmpty();

        account.User.InventoryItems.Add(new UserInventory
        {
            User = account.User,
            AccountID = account.ID,
            ItemCode = "temporary_item",
            Quantity = 1
        });
        await db.SaveChangesAsync();

        ResetInventoryHandler resetHandler = new(db);
        AdminActionResultDto resetRes = await resetHandler.Handle(new ResetInventoryCommand("InvPlayer"), CancellationToken.None);
        resetRes.Success.Should().BeTrue();
        account.User.InventoryItems.Should().BeEmpty();
    }

    [Fact]
    public async Task AdminApiController_NewEndpoints_DelegateToMediatR()
    {
        Mock<IMediator> mockMediator = new();

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetPlayerReportByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlayerReportDto { ID = 1, Reason = "Test" });

        mockMediator
            .Setup(m => m.Send(It.IsAny<DirectPunishAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Punished", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetHostApplicationsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HostApplicationDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<ApproveHostApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Approved", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<RejectHostApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Rejected", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<RevokeHostApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Revoked", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetStreamerApplicationsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamerApplicationDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<ApproveStreamerApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Approved", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<RejectStreamerApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Rejected", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<RevokeStreamerApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Revoked", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetRecentAccountsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetAccountDetailQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountDto { ID = 1, Name = "AdminFound" });

        mockMediator
            .Setup(m => m.Send(It.IsAny<AddInventoryItemCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Added", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<RemoveInventoryItemCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Removed", 1));

        mockMediator
            .Setup(m => m.Send(It.IsAny<ResetInventoryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminActionResultDto(true, "Reset", 1));

        AdminApiController controller = new(mockMediator.Object);

        var reportRes = await controller.GetPlayerReportById(1);
        reportRes.Should().BeOfType<OkObjectResult>();

        var directRes = await controller.DirectPunishAccount(new DirectPunishRequestDto("Hero", 1, "ban", "Reason"));
        directRes.Should().BeOfType<OkObjectResult>();

        var hostsRes = await controller.GetHostApplications();
        hostsRes.Should().BeOfType<OkObjectResult>();

        var appApproveRes = await controller.ApproveHostApplication(1);
        appApproveRes.Should().BeOfType<OkObjectResult>();

        var appRejectRes = await controller.RejectHostApplication(1, new RejectApplicationRequestDto("Reason"));
        appRejectRes.Should().BeOfType<OkObjectResult>();

        var appRevokeRes = await controller.RevokeHostApplication(1);
        appRevokeRes.Should().BeOfType<OkObjectResult>();

        var streamersRes = await controller.GetStreamerApplications();
        streamersRes.Should().BeOfType<OkObjectResult>();

        var strApproveRes = await controller.ApproveStreamerApplication(1);
        strApproveRes.Should().BeOfType<OkObjectResult>();

        var strRejectRes = await controller.RejectStreamerApplication(1, new RejectApplicationRequestDto("Reason"));
        strRejectRes.Should().BeOfType<OkObjectResult>();

        var strRevokeRes = await controller.RevokeStreamerApplication(1);
        strRevokeRes.Should().BeOfType<OkObjectResult>();

        var recentRes = await controller.GetRecentAccounts();
        recentRes.Should().BeOfType<OkObjectResult>();

        var detailRes = await controller.GetAccountDetail("AdminFound");
        detailRes.Should().BeOfType<OkObjectResult>();

        var addInvRes = await controller.AddInventoryItem(new ModifyInventoryRequestDto("Hero", "Code"));
        addInvRes.Should().BeOfType<OkObjectResult>();

        var remInvRes = await controller.RemoveInventoryItem(new ModifyInventoryRequestDto("Hero", "Code"));
        remInvRes.Should().BeOfType<OkObjectResult>();

        var resetInvRes = await controller.ResetInventory(new ResetInventoryRequestDto("Hero"));
        resetInvRes.Should().BeOfType<OkObjectResult>();

        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerReportByIdQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<DirectPunishAccountCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetHostApplicationsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<ApproveHostApplicationCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetStreamerApplicationsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<ApproveStreamerApplicationCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetRecentAccountsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetAccountDetailQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<AddInventoryItemCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<RemoveInventoryItemCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<ResetInventoryCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
