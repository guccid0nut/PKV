// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All


using System.Collections;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.RequestResponse.GameData;
using KONGOR.MasterServer.Services.Requester;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;


namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class GuideHandlerTests

{
    [Fact]
    public void GuideResponseSuccess_Serialization_IncludesVotePropertyAndMatchesClientSchema()

    {
        Account author = new()

        {
            ID = 1,

            Name = "Nome",

            IsMain = true,

            User = new User

            {
                EmailAddress = "nome@test.com",

                Role = new Role { ID = 1, Name = "Admin" },

                PBKDF2PasswordHash = "hash",

                SRPPasswordHash = "hash",

                SRPPasswordSalt = "salt"
            }
        };


        HeroGuide guide = new()

        {
            ID = 42,

            Name = "Devourer Pro Guide",

            HeroName = "Devourer",

            HeroIdentifier = "Hero_Devourer",

            Intro = "Devourer guide intro text",

            Content = "Devourer guide content text",

            StartingItems = "Item_RunesOfTheBlight|Item_HealthPotion",

            EarlyGameItems = "Item_Striders|Item_BloodChalice",

            CoreItems = "Item_PortalKey",

            LuxuryItems = "Item_BehemothsHeart",

            AbilityQueue = "Ability_Devourer2|Ability_Devourer1",

            Author = author,

            Rating = 4.5f,

            UpVotes = 10,

            DownVotes = 1,

            Public = true,

            Featured = true,

            TimestampCreated = TimeProvider.System.GetUtcNow()
        };


        const int hostTime = 123456789;

        GuideResponseSuccess response = new(guide, hostTime, true, true);

        string serialized = PhpSerializer.SerializeToString(response);


        Assert.NotNull(serialized);


        object? deserialized = PhpSerializer.Deserialize(serialized);

        Assert.NotNull(deserialized);

        IDictionary dict = Assert.IsType<PhpHashtable>(deserialized);


        Assert.True(dict.Contains("vote"),
            "Serialized GuideResponseSuccess MUST contain 'vote' property for client UI alignment.");

        Assert.Equal(0, Convert.ToInt32(dict["vote"]));

        Assert.Equal(1, Convert.ToInt32(dict["default"]));

        Assert.Equal(1, Convert.ToInt32(dict["favorite"]));


        Assert.True(dict.Contains("errors"));

        Assert.True(dict.Contains("success"));

        Assert.Equal(1, Convert.ToInt32(dict["success"]));

        Assert.True(dict.Contains("i_start"));

        Assert.Equal("Item_RunesOfTheBlight|Item_HealthPotion", dict["i_start"]?.ToString());

        Assert.True(dict.Contains("i_laning"));

        Assert.True(dict.Contains("i_core"));

        Assert.True(dict.Contains("i_luxury"));

        Assert.True(dict.Contains("abilQ"));

        Assert.True(dict.Contains("txt_intro"));

        Assert.True(dict.Contains("txt_guide"));

        Assert.True(dict.Contains("hosttime"));

        Assert.Equal(hostTime, Convert.ToInt32(dict["hosttime"]));
    }


    [Fact]
    public void GuideListResponse_Serialization_ContainsIsYoursWhenAuthorMatches()

    {
        Account author = new()

        {
            ID = 10,

            Name = "AuthorUser",

            IsMain = true,

            User = new User

            {
                EmailAddress = "author@test.com",

                Role = new Role { ID = 1, Name = "User" },

                PBKDF2PasswordHash = "hash",

                SRPPasswordHash = "hash",

                SRPPasswordSalt = "salt"
            }
        };


        HeroGuide guide = new()

        {
            ID = 101,

            Name = "My Custom Guide",

            HeroName = "Accursed",

            HeroIdentifier = "Hero_Accursed",

            Intro = "Intro",

            Content = "Content",

            StartingItems = "Item_RunesOfTheBlight",

            EarlyGameItems = "Item_Striders",

            CoreItems = "Item_PortalKey",

            LuxuryItems = "Item_BarrierIdol",

            AbilityQueue = "Ability_Accursed2",

            Author = author,

            Rating = 5.0f,

            UpVotes = 5,

            DownVotes = 0,

            Public = true,

            Featured = false,

            TimestampCreated = TimeProvider.System.GetUtcNow()
        };


        const int hostTime = 987654321;

        GuideListResponse listResponse = new([guide], author, hostTime);

        string serialized = PhpSerializer.SerializeToString(listResponse);


        Assert.NotNull(serialized);

        object? deserialized = PhpSerializer.Deserialize(serialized);

        IDictionary dict = Assert.IsType<PhpHashtable>(deserialized);


        Assert.True(dict.Contains("guide_list"));

        string guideListStr = dict["guide_list"]?.ToString() ?? string.Empty;


        Assert.Contains("is_yours", guideListStr);

        Assert.DoesNotContain("not_yours", guideListStr);

        Assert.Contains("|1.00|", guideListStr);
    }


    [Fact]
    public void GuideListResponse_Serialization_EscapesSingleQuotesInGuideNameAndAuthor()

    {
        Account author = new()

        {
            ID = 5,

            Name = "WhatYouGot's",

            IsMain = true,

            User = new User

            {
                EmailAddress = "wyg@test.com",

                Role = new Role { ID = 1, Name = "User" },

                PBKDF2PasswordHash = "hash",

                SRPPasswordHash = "hash",

                SRPPasswordSalt = "salt"
            }
        };


        HeroGuide guide = new()

        {
            ID = 501,

            Name = "WhatYouGot's Berzerker Guide",

            HeroName = "Berzerker",

            HeroIdentifier = "Hero_Berzerker",

            Intro = "Intro",

            Content = "Content",

            StartingItems = "Item_RunesOfTheBlight",

            EarlyGameItems = "Item_Striders",

            CoreItems = "Item_PortalKey",

            LuxuryItems = "Item_BarrierIdol",

            AbilityQueue = "Ability_Berzerker1",

            Author = author,

            Rating = 5.0f,

            UpVotes = 5,

            DownVotes = 0,

            Public = true,

            Featured = false,

            TimestampCreated = TimeProvider.System.GetUtcNow()
        };


        const int hostTime = 64253;

        GuideListResponse listResponse = new([guide], author, hostTime);

        string serialized = PhpSerializer.SerializeToString(listResponse);


        Assert.NotNull(serialized);

        object? deserialized = PhpSerializer.Deserialize(serialized);

        IDictionary dict = Assert.IsType<PhpHashtable>(deserialized);


        Assert.True(dict.Contains("guide_list"));

        string guideListStr = dict["guide_list"]?.ToString() ?? string.Empty;


        Assert.Contains("WhatYouGots Berzerker Guide", guideListStr);
    }


    [Fact]
    public void GuideListResponse_Serialization_ReflectsDefaultAndFavoriteGuideSelections()

    {
        Account requestingAccount = new()

        {
            ID = 99,

            Name = "PlayerOne",

            IsMain = true,

            DefaultGuideIDs = [502],

            FavoriteGuideIDs = [501],

            User = new User

            {
                EmailAddress = "p1@test.com",

                Role = new Role { ID = 1, Name = "User" },

                PBKDF2PasswordHash = "hash",

                SRPPasswordHash = "hash",

                SRPPasswordSalt = "salt"
            }
        };


        HeroGuide guide1 = new()

        {
            ID = 501,

            Name = "Fav Guide",

            HeroName = "Valkyrie",

            HeroIdentifier = "Hero_Valkyrie",

            Intro = "",

            Content = "",

            StartingItems = "",

            EarlyGameItems = "",

            CoreItems = "",

            LuxuryItems = "",

            AbilityQueue = "",

            Author = requestingAccount,

            Rating = 5.0f,

            UpVotes = 1,

            DownVotes = 0,

            Public = true,

            Featured = false,

            TimestampCreated = TimeProvider.System.GetUtcNow()
        };


        HeroGuide guide2 = new()

        {
            ID = 502,

            Name = "Default Guide",

            HeroName = "Valkyrie",

            HeroIdentifier = "Hero_Valkyrie",

            Intro = "",

            Content = "",

            StartingItems = "",

            EarlyGameItems = "",

            CoreItems = "",

            LuxuryItems = "",

            AbilityQueue = "",

            Author = requestingAccount,

            Rating = 4.0f,

            UpVotes = 1,

            DownVotes = 0,

            Public = true,

            Featured = false,

            TimestampCreated = TimeProvider.System.GetUtcNow()
        };


        GuideListResponse response = new([guide1, guide2], requestingAccount, 12345);

        string serialized = PhpSerializer.SerializeToString(response);

        object? deserialized = PhpSerializer.Deserialize(serialized);

        IDictionary dict = Assert.IsType<PhpHashtable>(deserialized);


        string guideListStr = dict["guide_list"]?.ToString() ?? string.Empty;

        Assert.Contains("501|", guideListStr);

        Assert.Contains("is_fav", guideListStr);

        Assert.Contains("502|", guideListStr);

        Assert.Contains("is_def", guideListStr);
    }


    [Fact]
    public void GuideHandler_ClientRequestDispatcher_MapsGuideActionRoutesToGuideHandler()

    {
        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("get_guide_list_filtered"));

        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("get_guide"));

        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("guide_default_set"));

        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("guide_default_remove"));

        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("guide_favorite_set"));

        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("guide_favorite_remove"));

        Assert.True(ClientRequestDispatcher.HandlerMap.ContainsKey("guide_vote"));


        Assert.Equal(typeof(GuideHandler), ClientRequestDispatcher.HandlerMap["guide_default_set"]);

        Assert.Equal(typeof(GuideHandler), ClientRequestDispatcher.HandlerMap["guide_favorite_set"]);

        Assert.Equal(typeof(GuideHandler), ClientRequestDispatcher.HandlerMap["guide_vote"]);
    }


    [Fact]
    public void GuideHandler_GuideResponseSuccess_NormalizesItemsAndAbilityQueueForClientEngine()

    {
        Account author = new()

        {
            ID = 1,

            Name = "guccidonut",

            IsMain = true,

            User = new User

            {
                EmailAddress = "gucci@nexus.local",

                Role = new Role { ID = 1, Name = "User" },

                PBKDF2PasswordHash = "h",

                SRPPasswordHash = "h",

                SRPPasswordSalt = "s"
            }
        };


        HeroGuide guide = new()

        {
            ID = 99,

            Name = "DONUT EMPATH",

            HeroName = "Empath",

            HeroIdentifier = "Hero_Empath",

            Intro = "Intro",

            Content = "Content",

            StartingItems = "Powersupply,astrolobe,RunesOfTheBlight",

            EarlyGameItems = "tabletofcommand",

            CoreItems = "Item_Astrolabe|Item_PushStaff",

            LuxuryItems = "stormspirit",

            AbilityQueue = "2,1,1,2,1,4,1,2,2,2,4,3,3,3,5,4,5,5,5,5,5,5,5,5,5",

            Author = author,

            Rating = 5.0f,

            UpVotes = 1,

            DownVotes = 0,

            Public = true,

            Featured = false,

            TimestampCreated = TimeProvider.System.GetUtcNow()
        };


        GuideResponseSuccess response = new(guide, 123456);


        // Items must be pipe-delimited with canonical Item_ prefix and mapped to game engine entities

        Assert.Equal("Item_PowerSupply|Item_Astrolabe|Item_RunesOfTheBlight", response.StartingItems);

        Assert.Equal("Item_PushStaff", response.EarlyGameItems);

        Assert.Equal("Item_Astrolabe|Item_PushStaff", response.CoreItems);

        Assert.Equal("Item_Intelligence6", response.LuxuryItems);


        // Ability queue must be pipe-delimited with hero-specific Ability_{HeroCode} prefixes and Ability_AttributeBoost for stats

        Assert.StartsWith("Ability_Empath2|Ability_Empath1|Ability_Empath1|Ability_Empath2", response.AbilityQueue);

        Assert.Contains("Ability_Empath4", response.AbilityQueue);

        Assert.Contains("Ability_AttributeBoost", response.AbilityQueue);

        Assert.DoesNotContain(",", response.AbilityQueue);
    }


    [Fact]
    public async Task GetGuideList_VisibilityRules_RestrictsUnendorsedGuidesToAuthorAndStaff()

    {
        string dbName = Guid.NewGuid().ToString();
        using (MerrickContext setupContext = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Role userRole = new() { ID = 1, Name = UserRoles.User };

            Role adminRole = new() { ID = 2, Name = UserRoles.Administrator };

            setupContext.Roles.AddRange(userRole, adminRole);


            User creatorUser = new()

            {
                ID = 1,

                EmailAddress = "creator@test.com",

                Role = userRole,

                SRPPasswordSalt = "s",

                SRPPasswordHash = "h",

                PBKDF2PasswordHash = "p"
            };

            User otherUser = new()

            {
                ID = 2,

                EmailAddress = "other@test.com",

                Role = userRole,

                SRPPasswordSalt = "s",

                SRPPasswordHash = "h",

                PBKDF2PasswordHash = "p"
            };

            User staffUser = new()

            {
                ID = 3,

                EmailAddress = "staff@test.com",

                Role = adminRole,

                SRPPasswordSalt = "s",

                SRPPasswordHash = "h",

                PBKDF2PasswordHash = "p"
            };


            Account creatorAccount = new()

            {
                ID = 10,

                Name = "Creator",

                User = creatorUser,

                IsMain = true,

                Type = AccountType.Normal
            };

            Account otherAccount = new()

            {
                ID = 20,

                Name = "GuestOrOther",

                User = otherUser,

                IsMain = true,

                Type = AccountType.Normal
            };

            Account staffAccount = new()

            {
                ID = 30,

                Name = "StaffMember",

                User = staffUser,

                IsMain = true,

                Type = AccountType.Staff
            };


            HeroGuide officialGuide = new()

            {
                ID = 100,

                Name = "Official Valk Guide",

                HeroName = "Valkyrie",

                HeroIdentifier = "Hero_Valkyrie",

                Author = creatorAccount,

                Featured = true,

                Public = true,

                Rating = 5.0f,

                UpVotes = 1,

                DownVotes = 0,

                StartingItems = "Item_RunesOfTheBlight",

                EarlyGameItems = "Item_Marchers",

                CoreItems = "Item_PushStaff",

                LuxuryItems = "Item_GeometersBane",

                AbilityQueue = "Ability_1",

                Intro = "Intro",

                Content = "Content"
            };


            HeroGuide communityPublicGuide = new()

            {
                ID = 101,

                Name = "Community Unendorsed Valk Guide",

                HeroName = "Valkyrie",

                HeroIdentifier = "Hero_Valkyrie",

                Author = creatorAccount,

                Featured = false,

                Public = true,

                Rating = 5.0f,

                UpVotes = 1,

                DownVotes = 0,

                StartingItems = "Item_RunesOfTheBlight",

                EarlyGameItems = "Item_Marchers",

                CoreItems = "Item_PushStaff",

                LuxuryItems = "Item_GeometersBane",

                AbilityQueue = "Ability_1",

                Intro = "Intro",

                Content = "Content"
            };


            HeroGuide privateDraftGuide = new()

            {
                ID = 102,

                Name = "Private Personal Valk Guide",

                HeroName = "Valkyrie",

                HeroIdentifier = "Hero_Valkyrie",

                Author = creatorAccount,

                Featured = false,

                Public = false,

                Rating = 5.0f,

                UpVotes = 1,

                DownVotes = 0,

                StartingItems = "Item_RunesOfTheBlight",

                EarlyGameItems = "Item_Marchers",

                CoreItems = "Item_PushStaff",

                LuxuryItems = "Item_GeometersBane",

                AbilityQueue = "Ability_1",

                Intro = "Intro",

                Content = "Content"
            };


            setupContext.Users.AddRange(creatorUser, otherUser, staffUser);

            setupContext.Accounts.AddRange(creatorAccount, otherAccount, staffAccount);

            setupContext.HeroGuides.AddRange(officialGuide, communityPublicGuide, privateDraftGuide);

            await setupContext.SaveChangesAsync();
        }


        using MerrickContext testContext = TestDbContextFactory.CreateInMemoryContext(dbName);

        Mock<IDatabase> mockCache = new();

        GuideHandler handler = new(
            new HeroGuideRepository(testContext),
            new AccountRepository(testContext),
            new SessionRepository(testContext),
            mockCache.Object,
            NullLogger<GuideHandler>.Instance);


        // 1. Regular other player / guest account (otherAccount #20)

        DefaultHttpContext otherContext = new();
        otherContext.Items["SessionAccountName"] = "GuestOrOther";

        otherContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>

        {
            { "f", "get_guide_list_filtered" }, { "account", "20" }, { "hero", "Hero_Valkyrie" }
        });


        IActionResult otherResult = await handler.HandleRequestAsync(otherContext);

        PhpResult otherPhp = Assert.IsType<PhpResult>(otherResult);

        GuideListResponse otherList = Assert.IsType<GuideListResponse>(otherPhp.Data);

        string otherSerialized = PhpSerializer.SerializeToString(otherList);

        // Must see Official guide and Public Community guide

        Assert.Contains("Official Valk Guide", otherSerialized);

        Assert.Contains("Community Unendorsed Valk Guide", otherSerialized);

        // Must NOT see other players' private drafts

        Assert.DoesNotContain("Private Personal Valk Guide", otherSerialized);


        // 2. Creator (creatorAccount #10)

        DefaultHttpContext creatorContext = new();
        creatorContext.Items["SessionAccountName"] = "Creator";

        creatorContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>

        {
            { "f", "get_guide_list_filtered" }, { "account", "10" }, { "hero", "Hero_Valkyrie" }
        });


        IActionResult creatorResult = await handler.HandleRequestAsync(creatorContext);

        PhpResult creatorPhp = Assert.IsType<PhpResult>(creatorResult);

        GuideListResponse creatorList = Assert.IsType<GuideListResponse>(creatorPhp.Data);

        string creatorSerialized = PhpSerializer.SerializeToString(creatorList);

        // Creator sees Official, their Community submission, and their Private draft

        Assert.Contains("Official Valk Guide", creatorSerialized);

        Assert.Contains("Community Unendorsed Valk Guide", creatorSerialized);

        Assert.Contains("Private Personal Valk Guide", creatorSerialized);


        // 3. Staff Member (staffAccount #30)

        DefaultHttpContext staffContext = new();
        staffContext.Items["SessionAccountName"] = "StaffMember";

        staffContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>

        {
            { "f", "get_guide_list_filtered" }, { "account", "30" }, { "hero", "Hero_Valkyrie" }
        });


        IActionResult staffResult = await handler.HandleRequestAsync(staffContext);

        PhpResult staffPhp = Assert.IsType<PhpResult>(staffResult);

        GuideListResponse staffList = Assert.IsType<GuideListResponse>(staffPhp.Data);

        string staffSerialized = PhpSerializer.SerializeToString(staffList);

        // Staff sees Official guide and Public community submissions for moderation/review

        Assert.Contains("Official Valk Guide", staffSerialized);

        Assert.Contains("Community Unendorsed Valk Guide", staffSerialized);

        // Staff does NOT see other players' private drafts

        Assert.DoesNotContain("Private Personal Valk Guide", staffSerialized);
    }


    [Fact]
    public async Task GetGuide_PrioritizesUserDefaultGuide_AndUsesTimestampLastUpdated()
    {
        using MerrickContext context = TestDbContextFactory.CreateInMemoryContext();

        Account userAccount = new()

        {
            ID = 55,

            Name = "ValkPro",

            IsMain = true,

            User = new User

            {
                EmailAddress = "valkpro@test.com",

                Role = new Role { ID = 1, Name = "User" },

                PBKDF2PasswordHash = "hash",

                SRPPasswordHash = "hash",

                SRPPasswordSalt = "salt"
            },

            DefaultGuideIDs = [102]
        };


        HeroGuide officialGuide = new()

        {
            ID = 101,

            Name = "Official Valk Guide",

            HeroName = "Valkyrie",

            HeroIdentifier = "Hero_Valkyrie",

            Intro = "Official intro",

            Content = "Official content",

            StartingItems = "Item_RunesOfTheBlight",

            EarlyGameItems = "Item_Marchers",

            CoreItems = "Item_Nullstone",

            LuxuryItems = "Item_GeometersBane",

            AbilityQueue = "Ability_1|Ability_2",

            Author = userAccount,

            Rating = 5.0f,

            UpVotes = 100,

            DownVotes = 0,

            Public = true,

            Featured = true,

            TimestampCreated = TimeProvider.System.GetUtcNow().AddDays(-10)
        };


        DateTimeOffset updatedTime = TimeProvider.System.GetUtcNow().AddMinutes(-5);

        HeroGuide customDefaultGuide = new()

        {
            ID = 102,

            Name = "Custom User Default Valk Guide",

            HeroName = "Valkyrie",

            HeroIdentifier = "Hero_Valkyrie",

            Intro = "Custom intro",

            Content = "Custom content",

            StartingItems = "Item_MinorTotem",

            EarlyGameItems = "Item_Striders",

            CoreItems = "Item_PortalKey",

            LuxuryItems = "Item_Freeze",

            AbilityQueue = "Ability_1|Ability_3",

            Author = userAccount,

            Rating = 4.0f,

            UpVotes = 5,

            DownVotes = 0,

            Public = false,

            Featured = false,

            TimestampCreated = TimeProvider.System.GetUtcNow().AddDays(-5),

            TimestampLastUpdated = updatedTime
        };


        context.Accounts.Add(userAccount);

        context.HeroGuides.AddRange(officialGuide, customDefaultGuide);

        await context.SaveChangesAsync();


        Mock<IDatabase> mockCache = new();

        GuideHandler handler = new(
            new HeroGuideRepository(context),
            new AccountRepository(context),
            new SessionRepository(context),
            mockCache.Object,
            NullLogger<GuideHandler>.Instance);


        DefaultHttpContext httpContext = new();
        httpContext.Items["SessionAccountName"] = "ValkPro";

        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>

        {
            { "f", "get_guide" }, { "account", "55" }, { "hero", "Hero_Valkyrie" }
        });


        IActionResult result = await handler.HandleRequestAsync(httpContext);

        PhpResult phpResult = Assert.IsType<PhpResult>(result);

        GuideResponseSuccess response = Assert.IsType<GuideResponseSuccess>(phpResult.Data);


        // Prioritizes user's default guide #102 over the official guide #101

        Assert.Equal("Custom User Default Valk Guide", response.Name);

        Assert.Equal(1, response.Default);

        Assert.Equal(updatedTime, response.TimestampCreated);
    }


    [Fact]
    public async Task GetGuideList_PrivateGuide_IsHiddenFromOtherAccountsAndGuests()

    {
        string dbName = Guid.NewGuid().ToString();
        using (MerrickContext setupContext = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Role userRole = new() { ID = 1, Name = UserRoles.User };

            setupContext.Roles.Add(userRole);


            User gucciUser = new()

            {
                ID = 1,

                EmailAddress = "gucci@donut.com",

                Role = userRole,

                SRPPasswordSalt = "s",

                SRPPasswordHash = "h",

                PBKDF2PasswordHash = "p"
            };

            User guestUser = new()

            {
                ID = 2,

                EmailAddress = "guest@test.com",

                Role = userRole,

                SRPPasswordSalt = "s",

                SRPPasswordHash = "h",

                PBKDF2PasswordHash = "p"
            };


            Account gucciAccount = new()

            {
                ID = 1,

                Name = "guccidonut",

                User = gucciUser,

                IsMain = true,

                Type = AccountType.Normal
            };

            Account guestAccount = new()

            {
                ID = 5,

                Name = "GUEST-05",

                User = guestUser,

                IsMain = true,

                Type = AccountType.Normal
            };


            HeroGuide publicCommunityGuide = new()

            {
                ID = 201,

                Name = "DONUT EMPATH COMMUNITY GUIDE",

                HeroName = "Empath",

                HeroIdentifier = "Hero_Empath",

                Author = gucciAccount,

                Featured = false,

                Public = true,

                Rating = 5.0f,

                UpVotes = 1,

                DownVotes = 0,

                StartingItems = "Item_RunesOfTheBlight",

                EarlyGameItems = "Item_Marchers",

                CoreItems = "Item_PushStaff",

                LuxuryItems = "Item_GeometersBane",

                AbilityQueue = "Ability_1",

                Intro = "Intro",

                Content = "Content"
            };


            HeroGuide privateGucciGuide = new()

            {
                ID = 202,

                Name = "DONUT EMPATH",

                HeroName = "Empath",

                HeroIdentifier = "Hero_Empath",

                Author = gucciAccount,

                Featured = false,

                Public = false,

                Rating = 5.0f,

                UpVotes = 1,

                DownVotes = 0,

                StartingItems = "Item_RunesOfTheBlight",

                EarlyGameItems = "Item_Marchers",

                CoreItems = "Item_PushStaff",

                LuxuryItems = "Item_GeometersBane",

                AbilityQueue = "Ability_1",

                Intro = "Intro",

                Content = "Content"
            };


            setupContext.Users.AddRange(gucciUser, guestUser);

            setupContext.Accounts.AddRange(gucciAccount, guestAccount);

            setupContext.HeroGuides.AddRange(publicCommunityGuide, privateGucciGuide);

            await setupContext.SaveChangesAsync();
        }


        using MerrickContext testContext = TestDbContextFactory.CreateInMemoryContext(dbName);

        Mock<IDatabase> mockCache = new();

        GuideHandler handler = new(
            new HeroGuideRepository(testContext),
            new AccountRepository(testContext),
            new SessionRepository(testContext),
            mockCache.Object,
            NullLogger<GuideHandler>.Instance);


        // 1. Guest-05 queries guide list

        DefaultHttpContext guestContext = new();
        guestContext.Items["SessionAccountName"] = "GUEST-05";

        guestContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>

        {
            { "f", "get_guide_list_filtered" }, { "account", "5" }, { "hero", "Hero_Empath" }
        });


        IActionResult guestResult = await handler.HandleRequestAsync(guestContext);

        PhpResult guestPhp = Assert.IsType<PhpResult>(guestResult);

        GuideListResponse guestList = Assert.IsType<GuideListResponse>(guestPhp.Data);

        string guestSerialized = PhpSerializer.SerializeToString(guestList);


        // Guest sees public community guide

        Assert.Contains("DONUT EMPATH COMMUNITY GUIDE", guestSerialized);

        // Guest MUST NOT see private guide for guccidonut

        Assert.DoesNotContain("`DONUT EMPATH`", guestSerialized);

        Assert.DoesNotContain("|DONUT EMPATH|", guestSerialized);


        // 2. guccidonut queries guide list

        DefaultHttpContext gucciContext = new();
        gucciContext.Items["SessionAccountName"] = "guccidonut";

        gucciContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>

        {
            { "f", "get_guide_list_filtered" }, { "account", "1" }, { "hero", "Hero_Empath" }
        });


        IActionResult gucciResult = await handler.HandleRequestAsync(gucciContext);

        PhpResult gucciPhp = Assert.IsType<PhpResult>(gucciResult);

        GuideListResponse gucciList = Assert.IsType<GuideListResponse>(gucciPhp.Data);

        string gucciSerialized = PhpSerializer.SerializeToString(gucciList);


        // guccidonut sees BOTH their public guide and their private guide

        Assert.Contains("DONUT EMPATH COMMUNITY GUIDE", gucciSerialized);

        Assert.Contains("DONUT EMPATH", gucciSerialized);
    }
}
