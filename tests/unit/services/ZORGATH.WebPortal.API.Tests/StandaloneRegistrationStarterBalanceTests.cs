// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NEXUS.Contracts.CQRS.Registration;
using Xunit;
using ZORGATH.WebPortal.API.Features.Registration;
using ZORGATH.WebPortal.API.Validators;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class StandaloneRegistrationStarterBalanceTests
{
    private static async Task<(MerrickContext Db, PasswordValidator Validator)> SetupTestEnvironmentAsync()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = MERRICK.DatabaseContext.Constants.UserRoles.User };
        db.Roles.Add(userRole);
        await db.SaveChangesAsync();

        PasswordValidator validator = new(Options.Create(new IdentityOptions()));
        return (db, validator);
    }

    [Fact]
    public async Task Handle_WhenStandaloneModeWithDefaults_GrantsDefaultStarterBalances()
    {
        // Arrange
        var (db, validator) = await SetupTestEnvironmentAsync();
        using (db)
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KONGOR_STANDALONE"] = "true"
                })
                .Build();

            RegisterDirectUserHandler handler = new(
                db,
                validator,
                NullLogger<RegisterDirectUserHandler>.Instance,
                configuration: config);

            var command = new RegisterDirectUserCommand(
                Email: "",
                Name: "StandaloneHero1",
                Password: "SecurePassword123!",
                DiscordId: "111222333");

            // Act
            var result = await handler.Handle(command, default);

            // Assert
            result.Success.Should().BeTrue();
            result.StatusCode.Should().Be(201);
            result.UserId.Should().NotBeNull();

            User? createdUser = db.Users.FirstOrDefault(u => u.ID == result.UserId!.Value);
            createdUser.Should().NotBeNull();
            createdUser!.GoldCoins.Should().Be(10000);
            createdUser.SilverCoins.Should().Be(25000);
            createdUser.PlinkoTickets.Should().Be(25);
        }
    }

    [Fact]
    public async Task Handle_WhenStandaloneModeWithCustomConfig_HonorsConfiguredStarterBalances()
    {
        // Arrange
        var (db, validator) = await SetupTestEnvironmentAsync();
        using (db)
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KONGOR_STANDALONE"] = "true",
                    ["KONGOR_STANDALONE_STARTER_GOLD"] = "500",
                    ["KONGOR_STANDALONE_STARTER_SILVER"] = "1000",
                    ["KONGOR_STANDALONE_STARTER_TICKETS"] = "5"
                })
                .Build();

            RegisterDirectUserHandler handler = new(
                db,
                validator,
                NullLogger<RegisterDirectUserHandler>.Instance,
                configuration: config);

            var command = new RegisterDirectUserCommand(
                Email: "",
                Name: "StandaloneHero2",
                Password: "SecurePassword123!",
                DiscordId: "444555666");

            // Act
            var result = await handler.Handle(command, default);

            // Assert
            result.Success.Should().BeTrue();
            result.StatusCode.Should().Be(201);
            result.UserId.Should().NotBeNull();

            User? createdUser = db.Users.FirstOrDefault(u => u.ID == result.UserId!.Value);
            createdUser.Should().NotBeNull();
            createdUser!.GoldCoins.Should().Be(500);
            createdUser.SilverCoins.Should().Be(1000);
            createdUser.PlinkoTickets.Should().Be(5);
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task Handle_WhenStandaloneDisabledOrUnset_StarterBalancesRemainZero(string? standaloneValue)
    {
        // Arrange
        var (db, validator) = await SetupTestEnvironmentAsync();
        using (db)
        {
            IConfiguration? config = null;

            if (standaloneValue != null)
            {
                config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["KONGOR_STANDALONE"] = standaloneValue,
                        ["KONGOR_STANDALONE_STARTER_GOLD"] = "500"
                    })
                    .Build();
            }

            RegisterDirectUserHandler handler = new(
                db,
                validator,
                NullLogger<RegisterDirectUserHandler>.Instance,
                configuration: config);

            var command = new RegisterDirectUserCommand(
                Email: "",
                Name: "StandardHero",
                Password: "SecurePassword123!",
                DiscordId: "777888999");

            // Act
            var result = await handler.Handle(command, default);

            // Assert
            result.Success.Should().BeTrue();
            result.StatusCode.Should().Be(201);
            result.UserId.Should().NotBeNull();

            User? createdUser = db.Users.FirstOrDefault(u => u.ID == result.UserId!.Value);
            createdUser.Should().NotBeNull();
            createdUser!.GoldCoins.Should().Be(0);
            createdUser.SilverCoins.Should().Be(0);
            createdUser.PlinkoTickets.Should().Be(0);
        }
    }
}
