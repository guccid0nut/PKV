// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NEXUS.Contracts.CQRS.Email;
using NEXUS.Contracts.CQRS.Registration;
using NEXUS.Contracts.Domain;
using Xunit;
using ZORGATH.WebPortal.API.Features.Email;
using ZORGATH.WebPortal.API.Features.Registration;
using ZORGATH.WebPortal.API.Models.Configuration;
using ZORGATH.WebPortal.API.Services.Email;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class EmailAndRegistrationMediatRTests
{
    [Fact]
    public async Task ConsoleEmailService_LogsRegistrationLink_ReturnsTrue()
    {
        Mock<IWebHostEnvironment> env = new();
        env.Setup(e => e.EnvironmentName).Returns("Development");
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            UIBaseUrl = "https://localhost:5557",
            JWT = new OperationalConfigurationJWT
            {
                SigningKey = "SecretKey123456789012345678901234567890",
                Issuer = "KongorAuth",
                Audience = "KongorWeb",
                DurationInHours = 24
            },
            Email = new OperationalConfigurationEmail { ApiKey = "fake_key", FromEmail = "noreply@kongor.net", FromName = "Kongor" } // kongor-security-ignore: synthetic unit-test dummy key
        });

        ConsoleEmailService service = new(config, NullLogger<ConsoleEmailService>.Instance, env.Object);

        bool sentLink = await service.SendEmailAddressRegistrationLink("test@kongor.net", "token-12345");
        bool sentConfirm = await service.SendEmailAddressRegistrationConfirmation("test@kongor.net", "PlayerOne");

        sentLink.Should().BeTrue();
        sentConfirm.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterEmailAddressHandler_ValidNewEmail_DispatchesToken()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEmailService> emailMock = new();
        emailMock.Setup(e => e.SendEmailAddressRegistrationLink(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(true);

        Mock<IWebHostEnvironment> env = new();
        env.Setup(e => e.EnvironmentName).Returns("Development");

        RegisterEmailAddressHandler handler = new(db, emailMock.Object, env.Object, NullLogger<RegisterEmailAddressHandler>.Instance);

        var result = await handler.Handle(new RegisterEmailAddressCommand("newuser@example.com", "newuser@example.com"), default);

        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(200);

        Token? token = db.Tokens.FirstOrDefault(t => t.EmailAddress == "newuser@example.com");
        token.Should().NotBeNull();
        token!.Purpose.Should().Be(TokenPurpose.EmailAddressVerification);
    }

    [Fact]
    public async Task RegisterEmailAddressHandler_MismatchEmail_ReturnsBadRequest()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEmailService> emailMock = new();
        Mock<IWebHostEnvironment> env = new();

        RegisterEmailAddressHandler handler = new(db, emailMock.Object, env.Object, NullLogger<RegisterEmailAddressHandler>.Instance);

        var result = await handler.Handle(new RegisterEmailAddressCommand("user_a@example.com", "user_b@example.com"), default);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task InitiateRegistrationHandler_ValidEmail_DispatchesVerificationLink()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IEmailService> emailMock = new();
        emailMock.Setup(e => e.SendEmailAddressRegistrationLink(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(true);

        InitiateRegistrationHandler handler = new(db, emailMock.Object, NullLogger<InitiateRegistrationHandler>.Instance);

        var result = await handler.Handle(new InitiateRegistrationCommand("init@kongor.net"), default);

        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(200);

        Token? token = db.Tokens.FirstOrDefault(t => t.EmailAddress == "init@kongor.net");
        token.Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterDirectUserHandler_WithDiscordIdAndNoEmail_SynthesizesDiscordEmail()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = MERRICK.DatabaseContext.Constants.UserRoles.User };
        db.Roles.Add(userRole);
        await db.SaveChangesAsync();

        PasswordValidator validator = new(Options.Create(new IdentityOptions()));
        RegisterDirectUserHandler handler = new(
            db,
            validator,
            NullLogger<RegisterDirectUserHandler>.Instance);

        var command = new RegisterDirectUserCommand(
            Email: "",
            Name: "DiscordHero",
            Password: "SecurePassword123!",
            DiscordId: "9988776655",
            DiscordUsername: "DiscordUser#1234",
            DiscordAvatar: "avatar_hash_123");

        var result = await handler.Handle(command, default);

        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        result.UserId.Should().NotBeNull();

        User? createdUser = db.Users.FirstOrDefault(u => u.ID == result.UserId!.Value);
        createdUser.Should().NotBeNull();
        createdUser!.EmailAddress.Should().Be("d_9988776655@discord.projectkongor.local");
        createdUser.DiscordId.Should().Be("9988776655");
        createdUser.DiscordUsername.Should().Be("DiscordUser#1234");
        createdUser.InventoryItems.Should().Contain(i => i.ItemCode == "ai.custom_icon:1");

        Account? mainAccount = db.Accounts.FirstOrDefault(a => a.UserID == createdUser.ID);
        mainAccount.Should().NotBeNull();
        mainAccount!.Name.Should().Be("DiscordHero");
        mainAccount.SelectedStoreItems.Should().Contain("ai.custom_icon:1");
    }
}
