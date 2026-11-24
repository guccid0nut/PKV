// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.Infrastructure;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class HeroGuideRepositoryTests
{
    private static User CreateTestUser(int id = 1, string email = "g@k.net") => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "s",
        SRPPasswordHash = "h",
        PBKDF2PasswordHash = "p",
        Role = new Role { ID = 1, Name = "User" }
    };

    [Fact]
    public async Task HeroGuideRepository_GetByIdAsync_ReturnsGuideWithAuthorAndClan()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Clan clan = new() { ID = 1, Name = "ClanG", Tag = "CG" };
            Account author = new() { ID = 10, Name = "GuideAuthor", User = user, Clan = clan };
            HeroGuide guide = new()
            {
                ID = 100,
                Name = "Mid Valk Guide",
                HeroIdentifier = "Hero_Valkyrie",
                Author = author,
                AuthorID = 10,
                Rating = 0.95f,
                Public = true,
                Featured = true
            };

            db.Users.Add(user);
            db.Clans.Add(clan);
            db.Accounts.Add(author);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            HeroGuide? result = await repo.GetByIdAsync(100);
            result.Should().NotBeNull();
            result!.Name.Should().Be("Mid Valk Guide");
            result.Author.Should().NotBeNull();
            result.Author.Clan.Should().NotBeNull();
            result.Author.Clan!.Tag.Should().Be("CG");
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeroGuideRepository_GetGuidesForHeroAsync_FiltersFeaturedPublicAndUserAuthored()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account myAccount = new() { ID = 20, Name = "Me", User = user };
            Account otherAccount = new() { ID = 30, Name = "Other", User = user };

            HeroGuide featuredGuide = new() { ID = 1, Name = "Featured", HeroIdentifier = "Hero_Pyromancer", Author = otherAccount, AuthorID = 30, Featured = true, Public = false, Rating = 0.9f };
            HeroGuide publicGuide = new() { ID = 2, Name = "Public", HeroIdentifier = "Hero_Pyromancer", Author = otherAccount, AuthorID = 30, Featured = false, Public = true, Rating = 0.8f };
            HeroGuide myPrivateGuide = new() { ID = 3, Name = "MyPrivate", HeroIdentifier = "Hero_Pyromancer", Author = myAccount, AuthorID = 20, Featured = false, Public = false, Rating = 0.5f };
            HeroGuide otherPrivateGuide = new() { ID = 4, Name = "OtherPrivate", HeroIdentifier = "Hero_Pyromancer", Author = otherAccount, AuthorID = 30, Featured = false, Public = false, Rating = 0.5f };

            db.Users.Add(user);
            db.Accounts.AddRange(myAccount, otherAccount);
            db.HeroGuides.AddRange(featuredGuide, publicGuide, myPrivateGuide, otherPrivateGuide);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            IReadOnlyList<HeroGuide> guides = await repo.GetGuidesForHeroAsync("Hero_Pyromancer", 20, "Me");
            guides.Should().HaveCount(3);
            guides.Should().Contain(g => g.ID == 1);
            guides.Should().Contain(g => g.ID == 2);
            guides.Should().Contain(g => g.ID == 3);
            guides.Should().NotContain(g => g.ID == 4);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeroGuideRepository_GetActiveGuideForHeroAsync_PrioritizesDefaultThenAuthoredThenFeatured()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account myAcc = new() { ID = 40, Name = "Player40", User = user };
            Account official = new() { ID = 50, Name = "Official", User = user };

            HeroGuide officialFeatured = new() { ID = 10, Name = "Official", HeroIdentifier = "Hero_FlintBeastwood", Author = official, AuthorID = 50, Featured = true, Public = true, Rating = 0.95f };
            HeroGuide myAuthored = new() { ID = 20, Name = "Mine", HeroIdentifier = "Hero_FlintBeastwood", Author = myAcc, AuthorID = 40, Featured = false, Public = false, Rating = 0.7f };
            HeroGuide chosenDefault = new() { ID = 30, Name = "Chosen", HeroIdentifier = "Hero_FlintBeastwood", Author = official, AuthorID = 50, Featured = false, Public = true, Rating = 0.85f };

            db.Users.Add(user);
            db.Accounts.AddRange(myAcc, official);
            db.HeroGuides.AddRange(officialFeatured, myAuthored, chosenDefault);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);

            // Case 1: user default guide ID [30] is passed -> returns chosenDefault (ID 30)
            HeroGuide? active1 = await repo.GetActiveGuideForHeroAsync("Hero_FlintBeastwood", 40, "Player40", [30]);
            active1.Should().NotBeNull();
            active1!.ID.Should().Be(30);

            // Case 2: no default guide passed -> returns user authored guide (ID 20)
            HeroGuide? active2 = await repo.GetActiveGuideForHeroAsync("Hero_FlintBeastwood", 40, "Player40", null);
            active2.Should().NotBeNull();
            active2!.ID.Should().Be(20);

            // Case 3: no default and no authored guide -> fallback to official featured (ID 10)
            HeroGuide? active3 = await repo.GetActiveGuideForHeroAsync("Hero_FlintBeastwood", 999, "Other", null);
            active3.Should().NotBeNull();
            active3!.ID.Should().Be(10);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HeroGuideRepository_SetDefaultGuideAsync_ReplacesGuidesForSameHero()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account acc = new()
            {
                ID = 60,
                Name = "Player60",
                User = user,
                DefaultGuideIDs = [101, 201]
            };
            HeroGuide valkGuide1 = new() { ID = 101, HeroIdentifier = "Hero_Valkyrie", Author = acc, AuthorID = 60 };
            HeroGuide valkGuide2 = new() { ID = 102, HeroIdentifier = "Hero_Valkyrie", Author = acc, AuthorID = 60 };
            HeroGuide flintGuide = new() { ID = 201, HeroIdentifier = "Hero_FlintBeastwood", Author = acc, AuthorID = 60 };

            db.Users.Add(user);
            db.Accounts.Add(acc);
            db.HeroGuides.AddRange(valkGuide1, valkGuide2, flintGuide);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            int result = await repo.SetDefaultGuideAsync(60, 102, true);
            result.Should().Be(102);

            Account? refreshed = await db.Accounts.FindAsync(60);
            refreshed!.DefaultGuideIDs.Should().Contain(102);
            refreshed.DefaultGuideIDs.Should().NotContain(101);
            refreshed.DefaultGuideIDs.Should().Contain(201);
        }
    }

    [Fact]
    public async Task HeroGuideRepository_VoteGuideAsync_UpdatesVotesAndRecalculatesRating()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account author = new() { ID = 70, Name = "Author70", User = user };
            HeroGuide guide = new()
            {
                ID = 500,
                Name = "VoteMe",
                HeroIdentifier = "Hero_Magmus",
                Author = author,
                AuthorID = 70,
                UpVotes = 3,
                DownVotes = 1,
                Rating = 0.75f
            };

            db.Users.Add(user);
            db.Accounts.Add(author);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            (int up, int down, float rating) = await repo.VoteGuideAsync(500, 1);
            up.Should().Be(4);
            down.Should().Be(1);
            rating.Should().Be(0.80f);
        }
    }

    [Fact]
    public async Task HeroGuideRepository_SaveGuideAsync_CreatesAndUpdatesGuide()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(1);
            Account author = new() { ID = 80, Name = "Author80", User = user };
            db.Users.Add(user);
            db.Accounts.Add(author);
            await db.SaveChangesAsync();

            HeroGuideRepository repo = new(db);
            HeroGuide newGuide = new()
            {
                Name = "Created Guide",
                HeroIdentifier = "Hero_WitchSlayer",
                Author = author,
                AuthorID = 80,
                StartingItems = "item_boots",
                Public = true
            };

            HeroGuide saved = await repo.SaveGuideAsync(newGuide);
            saved.ID.Should().BeGreaterThan(0);

            saved.Name = "Updated Guide Title";
            HeroGuide updated = await repo.SaveGuideAsync(saved);
            updated.Name.Should().Be("Updated Guide Title");
        }
    }
}
