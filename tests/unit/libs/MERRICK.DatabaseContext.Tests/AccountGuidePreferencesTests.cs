// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class AccountGuidePreferencesTests
{
    private static async Task<(Account Account, HeroGuide Guide, MerrickContext Context)> CreateAccountAndGuideAsync()
    {
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));

        User user = new()
        {
            ID = 1,
            EmailAddress = "guide_test@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account account = new()
        {
            ID = 101,
            UserID = 1,
            User = user,
            Name = "GuideUser",
            FavoriteGuideIDs = [],
            DefaultGuideIDs = []
        };

        HeroGuide guide = new()
        {
            ID = 501,
            AuthorID = 101,
            HeroIdentifier = "Hero_Pyromancer",
            Name = "Pyromancer Mid Guide"
        };

        context.Users.Add(user);
        context.Accounts.Add(account);
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        return (account, guide, context);
    }

    [Fact]
    public async Task AccountGuidePreference_RelationalCRUD_PersistsAndNavigates()
    {
        var (account, guide, db) = await CreateAccountAndGuideAsync();

        AccountGuidePreference pref = new()
        {
            AccountID = account.ID,
            GuideID = guide.ID,
            IsFavorite = true,
            IsDefault = false
        };

        db.AccountGuidePreferences.Add(pref);
        await db.SaveChangesAsync();

        AccountGuidePreference? loaded = await db.AccountGuidePreferences
            .Include(p => p.Account)
            .Include(p => p.Guide)
            .FirstOrDefaultAsync(p => p.AccountID == account.ID && p.GuideID == guide.ID);

        loaded.Should().NotBeNull();
        loaded!.IsFavorite.Should().BeTrue();
        loaded.Account.Name.Should().Be("GuideUser");
        loaded.Guide.HeroIdentifier.Should().Be("Hero_Pyromancer");
    }

    [Fact]
    public async Task AccountGuidePreference_CascadeDelete_RemovingAccountDeletesPreference()
    {
        var (account, guide, db) = await CreateAccountAndGuideAsync();

        db.AccountGuidePreferences.Add(new AccountGuidePreference
        {
            AccountID = account.ID,
            GuideID = guide.ID,
            IsFavorite = true
        });
        await db.SaveChangesAsync();

        (await db.AccountGuidePreferences.CountAsync(p => p.AccountID == account.ID)).Should().Be(1);

        db.Accounts.Remove(account);
        await db.SaveChangesAsync();

        (await db.AccountGuidePreferences.CountAsync(p => p.AccountID == account.ID)).Should().Be(0);
    }

    [Fact]
    public async Task AccountGuidePreference_CascadeDelete_RemovingGuideDeletesPreference()
    {
        var (account, guide, db) = await CreateAccountAndGuideAsync();

        db.AccountGuidePreferences.Add(new AccountGuidePreference
        {
            AccountID = account.ID,
            GuideID = guide.ID,
            IsFavorite = true
        });
        await db.SaveChangesAsync();

        (await db.AccountGuidePreferences.CountAsync(p => p.GuideID == guide.ID)).Should().Be(1);

        db.HeroGuides.Remove(guide);
        await db.SaveChangesAsync();

        (await db.AccountGuidePreferences.CountAsync(p => p.GuideID == guide.ID)).Should().Be(0);
    }

    [Fact]
    public async Task SetFavoriteGuideAsync_SynchronizesFavoriteGuideIDsAndPreferences()
    {
        var (account, guide, db) = await CreateAccountAndGuideAsync();
        HeroGuideRepository repo = new(db);

        int result = await repo.SetFavoriteGuideAsync(account.ID, guide.ID, isSet: true);
        result.Should().Be(guide.ID);

        Account? updatedAccount = await db.Accounts
            .Include(a => a.GuidePreferences)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        updatedAccount.Should().NotBeNull();
        updatedAccount!.FavoriteGuideIDs.Should().Contain(guide.ID);
        updatedAccount.GuidePreferences.Should().ContainSingle(p => p.GuideID == guide.ID && p.IsFavorite);

        // Unsetting favorite
        int unsetResult = await repo.SetFavoriteGuideAsync(account.ID, guide.ID, isSet: false);
        unsetResult.Should().Be(-1);

        Account? finalAccount = await db.Accounts
            .Include(a => a.GuidePreferences)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        finalAccount!.FavoriteGuideIDs.Should().NotContain(guide.ID);
        finalAccount.GuidePreferences.Should().NotContain(p => p.GuideID == guide.ID && p.IsFavorite);
    }
}
