// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using NEXUS.Contracts.Clients;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class DiscordAvatarSyncTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public async Task DiscordAvatarSync_RegisterOrLoginDiscordUserAsync_SyncsAvatarAndGrantsCustomIcon()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repository = new(context);

        Role role = new() { Name = UserRoles.User };
        context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "player@kongor.net",
            Role = role,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2",
            InventoryItems =
            [
                new UserInventory { ItemCode = "cc.white", Quantity = 1, IsPermanent = true, TimestampAcquired = TimeProvider.System.GetUtcNow(), Source = "Test" },
                new UserInventory { ItemCode = "t.Standard", Quantity = 1, IsPermanent = true, TimestampAcquired = TimeProvider.System.GetUtcNow(), Source = "Test" }
            ]
        };
        Account account = new()
        {
            Name = "KongorHero",
            User = user,
            IsMain = true,
            SelectedStoreItems = ["ai.Default Icon", "cc.white"]
        };
        user.Accounts.Add(account);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        DiscordAuthRequestDto payload = new(
            DiscordId: "987654321098765432",
            DiscordUsername: "KongorChamp#1234",
            Email: "player@kongor.net",
            DiscordAvatar: "a_discordavatarhash123",
            InGameName: "KongorHero"
        );

        User updatedUser = await repository.RegisterOrLoginDiscordUserAsync(payload);

        updatedUser.DiscordId.Should().Be("987654321098765432");
        updatedUser.DiscordAvatar.Should().Be("a_discordavatarhash123");
        updatedUser.InventoryItems.Should().Contain(i => i.ItemCode == "ai.custom_icon:1");

        Account updatedAccount = updatedUser.Accounts.First(a => a.Name == "KongorHero");
        updatedAccount.SelectedStoreItems.Should().Contain("ai.custom_icon:1");
        updatedAccount.SelectedStoreItems.Should().NotContain("ai.Default Icon");

        // Verify AccountExtensions resolution
        updatedAccount.GetIcon().Should().Be("ai.custom_icon:1");
        updatedAccount.GetIconNoPrefixCode().Should().Be("custom_icon:1");
    }

    [Fact]
    public async Task DiscordAvatarSync_RegisterOrLoginDiscordUserAsync_SubsequentLoginDoesNotDuplicateStoreItems()
    {
        await using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repository = new(context);

        Role role = new() { Name = UserRoles.User };
        context.Roles.Add(role);

        User user = new()
        {
            EmailAddress = "player@kongor.net",
            Role = role,
            DiscordId = "987654321098765432",
            DiscordAvatar = "a_discordavatarhash123",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2",
            InventoryItems =
            [
                new UserInventory { ItemCode = "cc.white", Quantity = 1, IsPermanent = true, TimestampAcquired = TimeProvider.System.GetUtcNow(), Source = "Test" },
                new UserInventory { ItemCode = "t.Standard", Quantity = 1, IsPermanent = true, TimestampAcquired = TimeProvider.System.GetUtcNow(), Source = "Test" },
                new UserInventory { ItemCode = "ai.custom_icon:1", Quantity = 1, IsPermanent = true, TimestampAcquired = TimeProvider.System.GetUtcNow(), Source = "Test" }
            ]
        };
        Account account = new()
        {
            Name = "KongorHero",
            User = user,
            IsMain = true,
            SelectedStoreItems = ["cc.white", "ai.custom_icon:1"]
        };
        user.Accounts.Add(account);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        DiscordAuthRequestDto payload = new(
            DiscordId: "987654321098765432",
            DiscordUsername: "KongorChamp#1234",
            Email: "player@kongor.net",
            DiscordAvatar: "a_discordavatarhash123",
            InGameName: "KongorHero"
        );

        User updatedUser = await repository.RegisterOrLoginDiscordUserAsync(payload);

        updatedUser.Accounts.First().SelectedStoreItems.Count(item => item == "ai.custom_icon:1").Should().Be(1);
    }

    [Fact]
    public void AccountExtensions_GetIcon_FallsBackToCustomIcon_WhenUserHasDiscordIdOrAvatar()
    {
        User user = new()
        {
            EmailAddress = "discorduser@kongor.net",
            DiscordId = "123456789",
            DiscordAvatar = "avatar_hash_abc",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2"
        };
        Account account = new()
        {
            Name = "DiscordPlayer",
            User = user,
            IsMain = true,
            SelectedStoreItems = ["ai.Default Icon"]
        };

        account.GetIcon().Should().Be("ai.custom_icon:1");
        account.GetIconNoPrefixCode().Should().Be("custom_icon:1");
    }
}
