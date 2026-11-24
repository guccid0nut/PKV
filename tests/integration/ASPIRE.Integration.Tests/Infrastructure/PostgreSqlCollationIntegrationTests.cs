// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ASPIRE.Tests.Infrastructure;

public sealed class PostgreSqlCollationIntegrationTests : IAsyncLifetime
{
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        _connectionString = await PostgreSqlContainerFixture.EnsureStartedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Trait("Category", "Manual")]
    [Fact]
    public async Task PostgreSql_CaseInsensitiveCollation_FindsAccountWithoutToLower()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(_connectionString);

        await using MerrickContext db = new(optionsBuilder.Options);
        
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE COLLATION IF NOT EXISTS case_insensitive (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
        }
        catch
        {
            // Ignore if already exists
        }

        await db.Database.EnsureCreatedAsync();

        string uniqueAccountName = $"P_{Guid.NewGuid():N}"[..10];
        User testUser = new()
        {
            EmailAddress = $"{Guid.NewGuid():N}@kongor.test",
            SRPPasswordSalt = "test_salt",
            SRPPasswordHash = "test_hash",
            PBKDF2PasswordHash = "test_pbkdf2",
            Role = new Role { Name = $"R_{Guid.NewGuid():N}"[..10] }
        };

        Account account = new()
        {
            Name = uniqueAccountName,
            User = testUser
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AccountRepository repository = new(db);

        // Act: Lookup using uppercase / lowercase variations
        Account? matchUpper = await repository.GetByNicknameAsync(uniqueAccountName.ToUpperInvariant());
        Account? matchLower = await repository.GetByNicknameAsync(uniqueAccountName.ToLowerInvariant());
        Account? matchExact = await repository.GetByNicknameAsync(uniqueAccountName);

        // Assert
        matchExact.Should().NotBeNull();
        matchExact!.ID.Should().Be(account.ID);

        matchUpper.Should().NotBeNull();
        matchUpper!.ID.Should().Be(account.ID);

        matchLower.Should().NotBeNull();
        matchLower!.ID.Should().Be(account.ID);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task PostgreSql_StoreCatalog_CaseInsensitiveProductLookup_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(_connectionString);

        await using MerrickContext db = new(optionsBuilder.Options);

        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE COLLATION IF NOT EXISTS case_insensitive (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
        }
        catch
        {
            // Ignore
        }

        await db.Database.EnsureCreatedAsync();

        string uniqueItemCode = $"Item_{Guid.NewGuid():N}";
        StoreItem storeItem = new()
        {
            Code = uniqueItemCode,
            Name = $"Name_{uniqueItemCode}",
            LocalContent = "item_asset_data",
            Price = 100,
            Enabled = true,
            Purchasable = true
        };

        db.StoreItems.Add(storeItem);
        await db.SaveChangesAsync();

        StoreRepository repository = new(db);

        StoreItem? matchUpper = await repository.GetProductByCodeAsync(uniqueItemCode.ToUpperInvariant());
        StoreItem? matchLower = await repository.GetProductByCodeAsync(uniqueItemCode.ToLowerInvariant());

        matchUpper.Should().NotBeNull();
        matchUpper!.Code.Should().Be(uniqueItemCode);

        matchLower.Should().NotBeNull();
        matchLower!.Code.Should().Be(uniqueItemCode);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task PostgreSql_ClanRepository_CaseInsensitiveNameAndTagLookup_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(_connectionString);

        await using MerrickContext db = new(optionsBuilder.Options);

        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE COLLATION IF NOT EXISTS case_insensitive (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
        }
        catch
        {
            // Ignore
        }

        await db.Database.EnsureCreatedAsync();

        string uniqueClanName = $"Clan_{Guid.NewGuid():N}"[..12];
        string uniqueTag = $"T{Guid.NewGuid():N}"[..4];

        Clan clan = new()
        {
            Name = uniqueClanName,
            Tag = uniqueTag,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };

        db.Clans.Add(clan);
        await db.SaveChangesAsync();

        ClanRepository repository = new(db);

        Clan? nameUpper = await repository.GetByNameAsync(uniqueClanName.ToUpperInvariant());
        Clan? nameLower = await repository.GetByNameAsync(uniqueClanName.ToLowerInvariant());
        Clan? tagUpper = await repository.GetByTagAsync(uniqueTag.ToUpperInvariant());
        Clan? tagLower = await repository.GetByTagAsync(uniqueTag.ToLowerInvariant());

        nameUpper.Should().NotBeNull();
        nameUpper!.ID.Should().Be(clan.ID);

        nameLower.Should().NotBeNull();
        nameLower!.ID.Should().Be(clan.ID);

        tagUpper.Should().NotBeNull();
        tagUpper!.ID.Should().Be(clan.ID);

        tagLower.Should().NotBeNull();
        tagLower!.ID.Should().Be(clan.ID);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task PostgreSql_FriendshipRepository_PrefixSearch_CaseInsensitive_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(_connectionString);

        await using MerrickContext db = new(optionsBuilder.Options);

        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE COLLATION IF NOT EXISTS case_insensitive (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
        }
        catch
        {
            // Ignore
        }

        await db.Database.EnsureCreatedAsync();

        string prefix = $"Frnd_{Guid.NewGuid():N}"[..8];
        Account account1 = new()
        {
            Name = $"{prefix}_Alpha",
            User = new User
            {
                EmailAddress = $"{Guid.NewGuid():N}@kongor.test",
                SRPPasswordSalt = "test_salt",
                SRPPasswordHash = "test_hash",
                PBKDF2PasswordHash = "test_pbkdf2",
                Role = new Role { Name = $"R_{Guid.NewGuid():N}"[..10] }
            }
        };

        Account account2 = new()
        {
            Name = $"{prefix}_Beta",
            User = new User
            {
                EmailAddress = $"{Guid.NewGuid():N}@kongor.test",
                SRPPasswordSalt = "test_salt",
                SRPPasswordHash = "test_hash",
                PBKDF2PasswordHash = "test_pbkdf2",
                Role = new Role { Name = $"R_{Guid.NewGuid():N}"[..10] }
            }
        };

        db.Accounts.AddRange(account1, account2);
        await db.SaveChangesAsync();

        FriendshipRepository repository = new(db);

        IReadOnlyList<Account> matchesLower = await repository.SearchAccountsByPrefixAsync(prefix.ToLowerInvariant(), 10);
        IReadOnlyList<Account> matchesUpper = await repository.SearchAccountsByPrefixAsync(prefix.ToUpperInvariant(), 10);

        matchesLower.Should().HaveCountGreaterOrEqualTo(2);
        matchesLower.Select(a => a.Name).Should().Contain([account1.Name, account2.Name]);

        matchesUpper.Should().HaveCountGreaterOrEqualTo(2);
        matchesUpper.Select(a => a.Name).Should().Contain([account1.Name, account2.Name]);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task PostgreSql_HeroGuideRepository_HeroIdentifierLookup_CaseInsensitive_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(_connectionString);

        await using MerrickContext db = new(optionsBuilder.Options);

        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE COLLATION IF NOT EXISTS case_insensitive (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
        }
        catch
        {
            // Ignore
        }

        await db.Database.EnsureCreatedAsync();

        string heroIdentifier = $"Hero_Test_{Guid.NewGuid():N}"[..16];
        Account author = new()
        {
            Name = $"GuideAuth_{Guid.NewGuid():N}"[..12],
            User = new User
            {
                EmailAddress = $"{Guid.NewGuid():N}@kongor.test",
                SRPPasswordSalt = "test_salt",
                SRPPasswordHash = "test_hash",
                PBKDF2PasswordHash = "test_pbkdf2",
                Role = new Role { Name = $"R_{Guid.NewGuid():N}"[..10] }
            }
        };

        HeroGuide guide = new()
        {
            HeroIdentifier = heroIdentifier,
            Name = "Ultimate Carry Guide",
            Intro = "Step by step build",
            Author = author,
            Public = true,
            Featured = true,
            Rating = 5.0f,
            UpVotes = 42
        };

        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        HeroGuideRepository repository = new(db);

        IReadOnlyList<HeroGuide> guidesUpper = await repository.GetGuidesForHeroAsync(heroIdentifier.ToUpperInvariant(), author.ID, author.Name);
        IReadOnlyList<HeroGuide> guidesLower = await repository.GetGuidesForHeroAsync(heroIdentifier.ToLowerInvariant(), author.ID, author.Name);

        guidesUpper.Should().NotBeEmpty();
        guidesUpper[0].ID.Should().Be(guide.ID);

        guidesLower.Should().NotBeEmpty();
        guidesLower[0].ID.Should().Be(guide.ID);
    }
}
