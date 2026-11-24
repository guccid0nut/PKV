// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using FluentValidation.Results;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Features.AccountManagement;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Tests.Infrastructure;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.CQRS;
using NEXUS.Contracts.CQRS.AccountManagement;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class AccountManagementMediatRTests
{
    private static (User user, Account account) CreateTestUserAndAccount(int userId = 1, int accountId = 10, string name = "Chronos")
    {
        User user = new()
        {
            ID = userId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash_value",
            GoldCoins = 5000,
            SilverCoins = 10000,
            InventoryItems = []
        };
        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            IsMain = true
        };
        return (user, account);
    }

    [Fact]
    public void CheckNicknameAvailabilityValidator_EnforcesNotEmpty()
    {
        CheckNicknameAvailabilityValidator validator = new();
        validator.Validate(new CheckNicknameAvailabilityQuery("")).IsValid.Should().BeFalse();
        validator.Validate(new CheckNicknameAvailabilityQuery("Chronos")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CheckNicknameAvailabilityHandler_WhenInvalidLength_ReturnsInvalidResponse()
    {
        Mock<IAccountRepository> mockRepo = new();
        CheckNicknameAvailabilityHandler handler = new(mockRepo.Object, NullLogger<CheckNicknameAvailabilityHandler>.Instance);

        var result = await handler.Handle(new CheckNicknameAvailabilityQuery("ab"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Available.Should().BeFalse();
        result.ErrorCode.Should().Be(1);
        result.RawResponse["0"].Should().Be(false);
        result.RawResponse["error_code"].Should().Be(1);
        mockRepo.Verify(r => r.ExistsByNicknameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckNicknameAvailabilityHandler_WhenAvailable_ReturnsSuccess()
    {
        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.ExistsByNicknameAsync("Chronos", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        CheckNicknameAvailabilityHandler handler = new(mockRepo.Object, NullLogger<CheckNicknameAvailabilityHandler>.Instance);

        var result = await handler.Handle(new CheckNicknameAvailabilityQuery("Chronos"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Available.Should().BeTrue();
        result.ErrorCode.Should().Be(0);
        result.RawResponse["0"].Should().Be(true);
        result.RawResponse["available"].Should().Be(true);
    }

    [Fact]
    public async Task CheckNicknameAvailabilityHandler_WhenTaken_ReturnsTakenResponse()
    {
        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.ExistsByNicknameAsync("TakenNick", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        CheckNicknameAvailabilityHandler handler = new(mockRepo.Object, NullLogger<CheckNicknameAvailabilityHandler>.Instance);

        var result = await handler.Handle(new CheckNicknameAvailabilityQuery("TakenNick"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Available.Should().BeFalse();
        result.ErrorCode.Should().Be(2);
        result.RawResponse["0"].Should().Be(false);
        result.RawResponse["errors"].Should().Be("Nickname is already taken.");
    }

    [Fact]
    public void ChangeNicknameCommand_ImplementsILockableRequest_WithAccountScopedKey()
    {
        ChangeNicknameCommand command = new(AccountId: 42, NewNickname: "NewName", Password: "secret", Cookie: "sess123");

        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:42:name_change");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ChangeNicknameValidator_EnforcesValidInputs()
    {
        ChangeNicknameValidator validator = new();
        validator.Validate(new ChangeNicknameCommand(0, "NewNick", "pass")).IsValid.Should().BeFalse();
        validator.Validate(new ChangeNicknameCommand(10, "", "pass")).IsValid.Should().BeFalse();
        validator.Validate(new ChangeNicknameCommand(10, "ab", "pass")).IsValid.Should().BeFalse();
        validator.Validate(new ChangeNicknameCommand(10, "ValidNick", "")).IsValid.Should().BeFalse();
        validator.Validate(new ChangeNicknameCommand(10, "ValidNick", "pass")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ChangeNicknameHandler_WhenWrongPassword_ReturnsErrorCode24()
    {
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "OldNick");
        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        Mock<IDatabase> mockRedis = new();
        Mock<IAccountPasswordVerifier> mockVerifier = new();
        mockVerifier.Setup(v => v.VerifyPassword(user, "wrongpass")).Returns(false);
        mockVerifier.Setup(v => v.VerifyPasswordAsync(user, "wrongpass", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        ChangeNicknameHandler handler = new(mockRepo.Object, mockRedis.Object, mockVerifier.Object, NullLogger<ChangeNicknameHandler>.Instance);
        var result = await handler.Handle(new ChangeNicknameCommand(10, "NewNick", "wrongpass"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(24);
        result.RawResponse["error_code"].Should().Be(24);
        result.RawResponse["errors"].Should().Be("24");
    }

    [Fact]
    public async Task ChangeNicknameHandler_WhenValid_ChangesNicknameAndUpdatesSession()
    {
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "OldNick");
        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        mockRepo.Setup(r => r.ChangeNicknameAsync(10, "NewNick", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Mock<IDatabase> mockRedis = new();
        Mock<IAccountPasswordVerifier> mockVerifier = new();
        mockVerifier.Setup(v => v.VerifyPassword(user, "goodpass")).Returns(true);
        mockVerifier.Setup(v => v.VerifyPasswordAsync(user, "goodpass", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        ChangeNicknameHandler handler = new(mockRepo.Object, mockRedis.Object, mockVerifier.Object, NullLogger<ChangeNicknameHandler>.Instance);
        var result = await handler.Handle(new ChangeNicknameCommand(10, "NewNick", "goodpass", "cookie123"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(0);
        result.NewNickname.Should().Be("NewNick");
        result.OldNickname.Should().Be("OldNick");
        result.RawResponse["status"].Should().Be("OK");
        mockRepo.Verify(r => r.ChangeNicknameAsync(10, "NewNick", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void CreateSubAccountCommand_ImplementsILockableRequest_WithMainAccountScopedKey()
    {
        CreateSubAccountCommand command = new(MainAccountId: 55, SubNickname: "Subbie", Password: "pass");

        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:55:create_subaccount");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CreateSubAccountValidator_EnforcesInputs()
    {
        CreateSubAccountValidator validator = new();
        validator.Validate(new CreateSubAccountCommand(0, "Subbie", "pass")).IsValid.Should().BeFalse();
        validator.Validate(new CreateSubAccountCommand(10, "", "pass")).IsValid.Should().BeFalse();
        validator.Validate(new CreateSubAccountCommand(10, "ValidSub", "")).IsValid.Should().BeFalse();
        validator.Validate(new CreateSubAccountCommand(10, "ValidSub", "pass")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CreateSubAccountHandler_WhenAccountUnderSanction_ReturnsSanctionError()
    {
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "BannedPlayer");
        account.Type = AccountType.Disabled;
        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        Mock<IAccountPasswordVerifier> mockVerifier = new();

        CreateSubAccountHandler handler = new(mockRepo.Object, mockVerifier.Object, NullLogger<CreateSubAccountHandler>.Instance);
        var result = await handler.Handle(new CreateSubAccountCommand(10, "SubPlayer", "pass"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(3);
        result.RawResponse["error_code"].Should().Be(3);
    }

    [Fact]
    public async Task CreateSubAccountHandler_WhenValid_CreatesSubAccountSuccessfully()
    {
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "MainPlayer");
        Account subAccount = new() { ID = 11, Name = "SubPlayer", User = user, IsMain = false };
        Mock<IAccountRepository> mockRepo = new();
        mockRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        mockRepo.Setup(r => r.CreateSubAccountAsync(10, "SubPlayer", It.IsAny<CancellationToken>())).ReturnsAsync(subAccount);
        Mock<IAccountPasswordVerifier> mockVerifier = new();
        mockVerifier.Setup(v => v.VerifyPassword(user, "pass")).Returns(true);
        mockVerifier.Setup(v => v.VerifyPasswordAsync(user, "pass", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        CreateSubAccountHandler handler = new(mockRepo.Object, mockVerifier.Object, NullLogger<CreateSubAccountHandler>.Instance);
        var result = await handler.Handle(new CreateSubAccountCommand(10, "SubPlayer", "pass"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(0);
        result.SubAccountId.Should().Be(11);
        result.SubAccountName.Should().Be("SubPlayer");
        result.RawResponse["account_id"].Should().Be("11");
    }

    [Fact]
    public void ResetStatsCommand_ImplementsILockableRequest_WithAccountScopedKey()
    {
        ResetStatsCommand command = new(AccountId: 77);

        command.Should().BeAssignableTo<ILockableRequest>();
        command.LockKey.Should().Be("lock:account:77:stat_reset");
        command.LockTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ResetStatsHandler_RevokesItemsAndReturnsSuccess()
    {
        (User user, Account account) = CreateTestUserAndAccount(1, 10, "ResetPlayer");
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        Mock<IInventoryRepository> mockInvRepo = new();

        ResetStatsHandler handler = new(mockAccountRepo.Object, mockInvRepo.Object, NullLogger<ResetStatsHandler>.Instance);
        var result = await handler.Handle(new ResetStatsCommand(10), CancellationToken.None);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().Be(0);
        result.RawResponse["status"].Should().Be("OK");
        mockInvRepo.Verify(i => i.RevokeItemAsync(10, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public void ValidatePromoCodeValidator_EnforcesNotEmpty()
    {
        ValidatePromoCodeValidator validator = new();
        validator.Validate(new ValidatePromoCodeQuery("")).IsValid.Should().BeFalse();
        validator.Validate(new ValidatePromoCodeQuery("PROMO2026")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidatePromoCodeHandler_WhenValid_ReturnsDiscount()
    {
        Mock<IInventoryRepository> mockInvRepo = new();
        mockInvRepo.Setup(i => i.ValidatePromoCodeAsync("HON2026", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        ValidatePromoCodeHandler handler = new(mockInvRepo.Object, NullLogger<ValidatePromoCodeHandler>.Instance);
        var result = await handler.Handle(new ValidatePromoCodeQuery("HON2026"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Valid.Should().BeTrue();
        result.Discount.Should().Be("20%");
        result.ErrorCode.Should().Be(0);
        result.RawResponse["valid"].Should().Be(true);
    }

    [Fact]
    public async Task ValidatePromoCodeHandler_WhenInvalid_ReturnsInvalidResponse()
    {
        Mock<IInventoryRepository> mockInvRepo = new();
        mockInvRepo.Setup(i => i.ValidatePromoCodeAsync("BOGUS", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        ValidatePromoCodeHandler handler = new(mockInvRepo.Object, NullLogger<ValidatePromoCodeHandler>.Instance);
        var result = await handler.Handle(new ValidatePromoCodeQuery("BOGUS"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Valid.Should().BeFalse();
        result.ErrorCode.Should().Be(1);
        result.RawResponse["valid"].Should().Be(false);
    }

    [Fact]
    public async Task AccountManagementController_DelegatesCheckNicknameAvailabilityToMediatR()
    {
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IInventoryRepository> mockInvRepo = new();
        Mock<IDatabase> mockRedis = new();
        Mock<ISessionContextResolver> mockSession = new();
        Mock<IMediator> mockMediator = new();

        Dictionary<string, object> dummyData = new()
        {
            ["0"] = true,
            ["available"] = true,
            ["nickname"] = "NewGuy"
        };
        NicknameAvailabilityResultDto dummyResult = new(true, 0, "", dummyData);
        mockMediator.Setup(m => m.Send(It.IsAny<CheckNicknameAvailabilityQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyResult);

        AccountManagementController controller = new(
            mockAccountRepo.Object,
            mockInvRepo.Object,
            mockRedis.Object,
            mockSession.Object,
            NullLogger<AccountManagementController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["nickname"] = "NewGuy" });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.CheckNicknameAvailability();

        actionResult.Should().BeOfType<PhpResult>();
        var phpResult = (PhpResult)actionResult;
        phpResult.Value.Should().Be(dummyData);
        mockMediator.Verify(m => m.Send(It.Is<CheckNicknameAvailabilityQuery>(q => q.Nickname == "NewGuy"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
