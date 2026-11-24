// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class AccountRepositoryTests
{
    [Fact]
    public async Task AccountRepository_BanAccountAsync_DirectlyUpdatesBanFieldsAndDisablesAccount()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 1,
            EmailAddress = "ban_test@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 100,
            Name = "TestPlayer",
            User = user,
            IsMain = true,
            Type = AccountType.Normal,
            BanExpiration = null,
            BanReason = null
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AccountRepository repository = new(db);
        DateTimeOffset banExpires = TimeProvider.System.GetUtcNow().AddDays(7);
        const string reason = "Cheating / Scripting";

        // Act
        await repository.BanAccountAsync(100, banExpires, reason);

        // Assert - verify directly in the database context
        Account? refreshed = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == 100);
        refreshed.Should().NotBeNull();
        refreshed!.Type.Should().Be(AccountType.Disabled);
        refreshed.BanExpiration.Should().Be(banExpires);
        refreshed.BanReason.Should().Be(reason);
    }

    [Fact]
    public async Task AccountRepository_UnbanAccountAsync_DirectlyResetsBanFieldsAndRestoresNormalStatus()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 2,
            EmailAddress = "unban_test@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 200,
            Name = "BannedPlayer",
            User = user,
            IsMain = true,
            Type = AccountType.Disabled,
            BanExpiration = TimeProvider.System.GetUtcNow().AddDays(30),
            BanReason = "Toxic Behavior"
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AccountRepository repository = new(db);

        // Act
        await repository.UnbanAccountAsync(200);

        // Assert
        Account? refreshed = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == 200);
        refreshed.Should().NotBeNull();
        refreshed!.Type.Should().Be(AccountType.Normal);
        refreshed.BanExpiration.Should().BeNull();
        refreshed.BanReason.Should().BeNull();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetByNicknameAsync_CaseInsensitive_DoesNotEagerLoadClanMembers()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = new()
            {
                ID = 3,
                EmailAddress = "clan_test@kongor.net",
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "pbkdf2",
                Role = new Role { ID = 1, Name = "User" }
            };
            Clan clan = new()
            {
                ID = 10,
                Name = "Vanguard Clan",
                Tag = "VG"
            };
            Account account = new()
            {
                ID = 300,
                Name = "ClanLeader",
                User = user,
                IsMain = true,
                Clan = clan,
                Type = AccountType.Normal
            };
            seedDb.Users.Add(user);
            seedDb.Clans.Add(clan);
            seedDb.Accounts.Add(account);
            await seedDb.SaveChangesAsync();
        }

        using MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        AccountRepository repository = new(queryDb);

        // Act - query with different casing
        Account? fetched = await repository.GetByNicknameAsync("clanLEADER");

        // Assert
        fetched.Should().NotBeNull();
        fetched!.ID.Should().Be(300);
        fetched.Clan.Should().NotBeNull();
        fetched.Clan!.Tag.Should().Be("VG");
        fetched.Clan.Name.Should().Be("Vanguard Clan");
    }

    [Fact]
    public async Task AccountRepository_GetForAuthenticationAsync_EndToEndGraph_PopulatesSubAccountsAndClan()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = new()
            {
                ID = 5,
                EmailAddress = "auth_graph@kongor.net",
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "pbkdf2",
                Role = new Role { ID = 1, Name = "User" }
            };
            Clan clan = new()
            {
                ID = 50,
                Name = "Apex Predator",
                Tag = "APEX"
            };
            Account main = new()
            {
                ID = 500,
                Name = "ApexPredator",
                User = user,
                IsMain = true,
                Clan = clan,
                Type = AccountType.Normal,
                TimestampCreated = now.AddDays(-10)
            };
            Account sub = new()
            {
                ID = 501,
                Name = "ApexSmurf",
                User = user,
                IsMain = false,
                Clan = clan,
                Type = AccountType.Normal,
                TimestampCreated = now.AddDays(-1)
            };
            seedDb.Users.Add(user);
            seedDb.Clans.Add(clan);
            seedDb.Accounts.AddRange(main, sub);
            await seedDb.SaveChangesAsync();
        }

        using MerrickContext queryDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        AccountRepository repository = new(queryDb);

        // Act - query with case variation
        Account? fetched = await repository.GetForAuthenticationAsync("apexPREDATOR");

        // Assert
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("ApexPredator");
        fetched.User.Should().NotBeNull();
        fetched.User.Accounts.Should().HaveCount(2);
        fetched.Clan.Should().NotBeNull();
        fetched.Clan!.Members.Should().HaveCount(2);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task BanAccountAsync_NonExistentAccount_CompletesWithoutThrowing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repository = new(db);

        // Act & Assert
        Func<Task> act = async () =>
            await repository.BanAccountAsync(999999, TimeProvider.System.GetUtcNow().AddDays(1), "NonExistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UnbanAccountAsync_NonExistentAccount_CompletesWithoutThrowing()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        AccountRepository repository = new(db);

        // Act & Assert
        Func<Task> act = async () => await repository.UnbanAccountAsync(999999);
        await act.Should().NotThrowAsync();
    }
}
