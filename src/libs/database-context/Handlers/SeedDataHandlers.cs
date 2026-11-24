// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable All
// ReSharper disable SpecifyACultureInStringConversionExplicitly
// ReSharper disable StringToLowerIsCultureSpecific
// ReSharper disable StringCompareToIsCultureSpecific.1
// ReSharper disable StringIndexOfIsCultureSpecific.1

#pragma warning disable CA1304
#pragma warning disable CA1311
#pragma warning disable CA1305

using ASPIRE.Common.Security;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;
using System.Text.Json.Serialization;
using Kongor.Core.Security;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Identity;
using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace MERRICK.DatabaseContext.Handlers;

public static class SeedDataHandlers
{
    private static List<HeroItem>? s_cachedHeroItems;
    private static List<HeroDetail>? s_cachedHeroDetails;
    private static readonly Lock s_seedLock = new();

    public static async Task SeedUsers(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
#if PLAYTEST_BUILD
        _ = context;
        _ = cancellationToken;
        logger.LogInformation("[Bootstrap] SeedUsers skipped in playtest build");
        await Task.CompletedTask;
        return;
#endif
#if !PLAYTEST_BUILD
        if (await context.Users.AnyAsync(cancellationToken))
        {
            logger.LogInformation("[SeedUsers] Users already exist in database, skipping.");
            return;
        }

        Role? roleAdministrator =
            await context.Roles.FirstOrDefaultAsync(role => role.Name == UserRoles.Administrator, cancellationToken);
        if (roleAdministrator == null)
        {
            logger.LogInformation("[SeedUsers] Creating Administrator role...");
            roleAdministrator = new Role { Name = UserRoles.Administrator };
            await context.Roles.AddAsync(roleAdministrator, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        Role? roleGuest =
            await context.Roles.FirstOrDefaultAsync(role => role.Name == UserRoles.User, cancellationToken);
        if (roleGuest == null)
        {
            logger.LogInformation("[SeedUsers] Creating User role...");
            roleGuest = new Role { Name = UserRoles.User };
            await context.Roles.AddAsync(roleGuest, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("[SeedUsers] Creating administrator user (admin@kongor.local)...");
        User userAdministrator = new()
        {
            EmailAddress = "admin@kongor.local",
            Role = roleAdministrator,
            SRPPasswordSalt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053",
            SRPPasswordHash = "9fb9cf192701aba7aaa38e6f15457082c93cfbe060bdbdc5fa67a986b355700c",
            PBKDF2PasswordHash = "AQAAAAIAAYagAAAAEMUkpLAr01NjkKRPaXCyTa17nlOdPKJucn5QYur+wQBTDKCpgsAcREenK+pGJPBCRw==",
            GoldCoins = 5_555_555,
            SilverCoins = 555_555_555,
            PlinkoTickets = 5_555_555,
            TotalLevel = 666,
            TotalExperience = 222_11_666
        };

        string[] defaultSeedItems =
        [
            "ai.custom_icon:1", "av.Flamboyant", "av.Ramsay", "av.ASMR", "av.Unreal", "av.GLaDOS", "av.RickMorty", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep",
            "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP",
            "w.8bit_ward"
        ];

        foreach (string itemCode in defaultSeedItems)
        {
            userAdministrator.InventoryItems.Add(new UserInventory
            {
                User = userAdministrator,
                ItemCode = itemCode,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "Seed"
            });
        }

        await context.Users.AddAsync(userAdministrator, cancellationToken);

        logger.LogInformation("[SeedUsers] Creating guest user (guest@project.kongor)...");
        User userGuest = new()
        {
            EmailAddress = "guest@project.kongor",
            Role = roleGuest,
            SRPPasswordSalt = "81c278a3ed03cf4549e787feac5ffe1d051029e15159b1cf0b087b4f8a85cb69",
            SRPPasswordHash = "bfad34f4dc65064dcae6e2064e12d63b299909b6c0cdb33306a91f26bb624e35",
            PBKDF2PasswordHash = "AQAAAAIAAYagAAAAECx4DCy3qYARlevpaVUJXy28QyvXDcQiJZ1KOSx547WDLBV0pdx2MX5m+Pe3xhtQAg==",
            GoldCoins = 5_555_555,
            SilverCoins = 555_555_555,
            PlinkoTickets = 5_555_555,
            TotalLevel = 0,
            TotalExperience = 0
        };

        foreach (string itemCode in defaultSeedItems)
        {
            userGuest.InventoryItems.Add(new UserInventory
            {
                User = userGuest,
                ItemCode = itemCode,
                Quantity = 1,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = "Seed"
            });
        }

        await context.Users.AddAsync(userGuest, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[SeedUsers] Successfully persisted users to database.");
#endif
    }

    public static async Task SeedClans(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
#if PLAYTEST_BUILD
        _ = context;
        _ = cancellationToken;
        logger.LogInformation("[Bootstrap] SeedClans skipped in playtest build");
        await Task.CompletedTask;
        return;
#endif
#if !PLAYTEST_BUILD
        if (await context.Clans.AnyAsync(cancellationToken))
        {
            logger.LogInformation("[SeedClans] Clans already exist in database, skipping.");
            return;
        }

        IEnumerable<Clan> clans =
        [
            new() { Name = "KONGOR", Tag = "K" },
            new() { Name = "Project KONGOR", Tag = "PK" },
            new() { Name = "Project KONGOR Open-Source", Tag = "PKOS" },
            new() { Name = "Heroes Of Newerth", Tag = "HON" },
            new() { Name = "GUCI", Tag = "GUCI" }
        ];

        await context.Clans.AddRangeAsync(clans, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[SeedClans] Successfully persisted clans to database.");
#endif
    }

    public static async Task SeedAccounts(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
#if PLAYTEST_BUILD
        _ = context;
        _ = cancellationToken;
        logger.LogInformation("[Bootstrap] SeedAccounts skipped in playtest build");
        await Task.CompletedTask;
        return;
#endif
#if !PLAYTEST_BUILD
        if (await context.Accounts.AnyAsync(cancellationToken))
        {
            logger.LogInformation("[SeedAccounts] Accounts already exist in database, skipping.");
            return;
        }

        if (!await context.Users.AnyAsync(cancellationToken) || !await context.Clans.AnyAsync(cancellationToken))
        {
            logger.LogWarning("[SeedAccounts] Users or Clans missing, unable to seed Accounts.");
            return;
        }

        logger.LogInformation("[SeedAccounts] Querying users and clans for Account seeding...");
        User user = await context.Users.FirstAsync(cancellationToken);
        Clan clan = await context.Clans.FirstAsync(cancellationToken);

        Account systemAccount = new()
        {
            Name = "KONGOR",
            User = user,
            Type = AccountType.Staff,
            IsMain = true,
            Clan = null,
            ClanTier = ClanTier.None,
            TimestampJoinedClan = null,
            AscensionLevel = 666,
            AutoConnectChatChannels = [.. ChatChannels.AllDefaultChannels],
            SelectedStoreItems =
            [
                "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep",
                "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP",
                "w.8bit_ward"
            ]
        };

        await context.AddAsync(systemAccount, cancellationToken);

        string[] subAccountNames = [ /* [K] */ "ONGOR"];

        foreach (string subAccountName in subAccountNames)
        {
            Account subAccount = new()
            {
                Name = subAccountName,
                User = user,
                Type = AccountType.Staff,
                IsMain = false,
                Clan = clan,
                ClanTier = ClanTier.Leader,
                TimestampJoinedClan = TimeProvider.System.GetUtcNow(),
                AscensionLevel = 666,
                AutoConnectChatChannels = [.. ChatChannels.AllDefaultChannels, clan.GetChatChannelName()],
                SelectedStoreItems =
                [
                    "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cc.frostburnlogo", "cr.Punk Creep",
                    "cs.frostburnlogo", "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt", "te.Punk TP",
                    "w.8bit_ward"
                ]
            };

            await context.AddAsync(subAccount, cancellationToken);
        }

        string[] hostAccountNames = ["HOST", "SERVER", "kongor_host_docker", "kongor_host_k3s", "kongor_host_arc"];

        foreach (string hostAccountName in hostAccountNames)
        {
            Account hostAccount = new()
            {
                Name = hostAccountName,
                User = user,
                Type = AccountType.ServerHost,
                IsMain = false,
                Clan = null,
                ClanTier = ClanTier.None,
                TimestampJoinedClan = null,
                AscensionLevel = 666,
                AutoConnectChatChannels = [ChatChannels.ServerHostsChannel],
                SelectedStoreItems =
                [
                    "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cr.Punk Creep",
                    "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt",
                    "te.Punk TP", "w.8bit_ward"
                ]
            };

            await context.AddAsync(hostAccount, cancellationToken);
        }

        User userGuest = await context.Users.Skip(1).FirstAsync(cancellationToken);
        Clan clanGuest = await context.Clans.Skip(1).FirstAsync(cancellationToken);

        for (int iterator = 0; iterator < 100 + 1; iterator++)
        {
            Account guestAccount = new()
            {
                Name = iterator is 0 ? "MODERATOR" : $"GUEST-{iterator:D3}",
                User = userGuest,
                Type = iterator is 0 ? AccountType.MatchModerator : AccountType.Guest,
                IsMain = iterator is 0,
                Clan = clanGuest,
                ClanTier = iterator is 0 ? ClanTier.Leader : ClanTier.Member,
                TimestampJoinedClan = TimeProvider.System.GetUtcNow(),
                AscensionLevel = 0,
                AutoConnectChatChannels = [ChatChannels.GuestsChannel, clan.GetChatChannelName()],
                SelectedStoreItems =
                [
                    "ai.custom_icon:1", "av.Flamboyant", "c.cat_courier", "cr.Punk Creep",
                    "m.Super-Taunt", "sc.paragon_circle_upgrade", "t.Dumpster_Taunt",
                    "te.Punk TP", "w.8bit_ward"
                ]
            };

            await context.AddAsync(guestAccount, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        // Also Include Friended/Ignored/Banned Peer Seeding To Reduce The Application Startup Duration

        await SeedFriendedPeers(context, logger, cancellationToken);
        await SeedIgnoredPeers(context, logger, cancellationToken);
        await SeedBannedPeers(context, logger, cancellationToken);
#endif
    }

#if !PLAYTEST_BUILD
    private static async Task SeedFriendedPeers(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await context.Accounts.AnyAsync(cancellationToken) || !await context.Clans.AnyAsync(cancellationToken))
        {
            return;
        }

        if (await context.Friendships.AnyAsync(cancellationToken))
        {
            return;
        }

        Account systemAccount = await context.Accounts.Include(account => account.User).FirstAsync(cancellationToken);

        List<Account> subAccounts = await context.Accounts.Include(account => account.User)
            .Include(account => account.Clan)
            .Where(account => account.User == systemAccount.User).Where(account => account.IsMain.Equals(false))
            .Where(account => account.Type != AccountType.ServerHost).ToListAsync(cancellationToken);

        List<Account> hostAccounts = await context.Accounts.Where(account => account.Type == AccountType.ServerHost)
            .ToListAsync(cancellationToken);

        List<Friendship> friendshipsToSeed = [];
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        // Seed Personas: Bidirectional relationships between subAccounts and systemAccount
        foreach (Account subAccount in subAccounts)
        {
            IEnumerable<Account> peers = subAccounts.Except([subAccount]).Append(systemAccount);
            foreach (Account peer in peers)
            {
                if (!friendshipsToSeed.Any(f => f.AccountID == subAccount.ID && f.FriendAccountID == peer.ID))
                {
                    friendshipsToSeed.Add(new Friendship
                    {
                        AccountID = subAccount.ID,
                        FriendAccountID = peer.ID,
                        FriendGroup = "PERSONAS",
                        Status = FriendshipStatus.Accepted,
                        TimestampCreated = now
                    });
                }

                if (!friendshipsToSeed.Any(f => f.AccountID == peer.ID && f.FriendAccountID == subAccount.ID))
                {
                    friendshipsToSeed.Add(new Friendship
                    {
                        AccountID = peer.ID,
                        FriendAccountID = subAccount.ID,
                        FriendGroup = "PERSONAS",
                        Status = FriendshipStatus.Accepted,
                        TimestampCreated = now
                    });
                }
            }
        }

        // Seed Server Host Clusters: Bidirectional mesh among host accounts
        foreach (Account hostAccount in hostAccounts)
        {
            foreach (Account peerHost in hostAccounts.Where(h => h.ID != hostAccount.ID))
            {
                if (!friendshipsToSeed.Any(f => f.AccountID == hostAccount.ID && f.FriendAccountID == peerHost.ID))
                {
                    friendshipsToSeed.Add(new Friendship
                    {
                        AccountID = hostAccount.ID,
                        FriendAccountID = peerHost.ID,
                        FriendGroup = "CLUSTERS",
                        Status = FriendshipStatus.Accepted,
                        TimestampCreated = now
                    });
                }
            }
        }

        await context.Friendships.AddRangeAsync(friendshipsToSeed, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Successfully seeded {Count} authoritative relational friendships.", friendshipsToSeed.Count);
    }

    private static async Task SeedIgnoredPeers(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await context.Accounts.AnyAsync(cancellationToken))
        {
            return;
        }

        if (await context.AccountPeers.AnyAsync(p => p.Type == PeerType.Ignored, cancellationToken))
        {
            return;
        }

        Account systemAccount = await context.Accounts.FirstAsync(cancellationToken);

        List<Account> guestAccounts = await context.Accounts.Include(account => account.User)
            .Where(account => account.IsMain.Equals(false)).Where(account => account.Type == AccountType.Guest)
            .ToListAsync(cancellationToken);

        foreach (Account guestAccount in guestAccounts)
        {
            context.AccountPeers.Add(new AccountPeer
            {
                AccountID = systemAccount.ID,
                PeerAccountID = guestAccount.ID,
                Type = PeerType.Ignored
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedBannedPeers(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!await context.Accounts.AnyAsync(cancellationToken))
        {
            return;
        }

        if (await context.AccountPeers.AnyAsync(p => p.Type == PeerType.Banned, cancellationToken))
        {
            return;
        }

        Account systemAccount = await context.Accounts.FirstAsync(cancellationToken);

        List<Account> guestAccounts = await context.Accounts.Include(account => account.User)
            .Where(account => account.IsMain.Equals(false)).Where(account => account.Type == AccountType.Guest)
            .ToListAsync(cancellationToken);

        foreach (Account guestAccount in guestAccounts)
        {
            context.AccountPeers.Add(new AccountPeer
            {
                AccountID = systemAccount.ID,
                PeerAccountID = guestAccount.ID,
                Type = PeerType.Banned,
                Reason = "Non-Main Guest Account"
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

#endif

    public static async Task SeedHeroGuides(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
        if (!File.Exists(DataFiles.HeroGuides))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("HeroGuides seed file missing at {Path}", DataFiles.HeroGuides);
            }

            return;
        }

        if (!await context.Accounts.AnyAsync(cancellationToken))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("No accounts found in DB. Skipping HeroGuides seeding until an account exists.");
            }

            return;
        }

        int existingDbGuideCount = await context.HeroGuides.CountAsync(cancellationToken);
        if (existingDbGuideCount > 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Database HeroGuides table is fully populated ({Count} guides)",
                    existingDbGuideCount);
            }

            return;
        }

        Account author = await context.Accounts.FirstAsync(cancellationToken);

        DeserializationDTOs.GuideGetDTO[]? guideDTOs = JsonSerializer.Deserialize<DeserializationDTOs.GuideGetDTO[]>
        (await File.ReadAllTextAsync(DataFiles.HeroGuides, cancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (guideDTOs is { Length: > 0 })
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Auto-seeding {Count} HeroGuides into database...", guideDTOs.Length);
            }

            List<HeroGuide> guides = new(guideDTOs.Length);
            foreach (DeserializationDTOs.GuideGetDTO guideDto in guideDTOs)
            {
                HeroGuide guide = new()
                {
                    Name = guideDto.Name,
                    HeroName = guideDto.HeroName,
                    HeroIdentifier = guideDto.HeroIdentifier,
                    Intro = guideDto.Intro,
                    Content = guideDto.Content,
                    Author = author,
                    Rating = guideDto.Rating,
                    UpVotes = guideDto.UpVotes,
                    DownVotes = guideDto.DownVotes,
                    Public = guideDto.Public,
                    Featured = guideDto.Featured,
                    TargetMap = "caldavar",
                    TimestampCreated = TimeProvider.System.GetUtcNow()
                };

                // Seed 1NF Items (filtering out blank/empty strings)
                if (guideDto.StartingItems is { Count: > 0 })
                {
                    int order = 0;
                    foreach (string item in guideDto.StartingItems)
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            guide.Items.Add(new HeroGuideItem
                            {
                                SlotType = GuideSlotType.Starting,
                                OrderIndex = order++,
                                ItemCode = item.Trim()
                            });
                        }
                    }
                }

                if (guideDto.EarlyGameItems is { Count: > 0 })
                {
                    int order = 0;
                    foreach (string item in guideDto.EarlyGameItems)
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            guide.Items.Add(new HeroGuideItem
                            {
                                SlotType = GuideSlotType.EarlyGame,
                                OrderIndex = order++,
                                ItemCode = item.Trim()
                            });
                        }
                    }
                }

                if (guideDto.CoreItems is { Count: > 0 })
                {
                    int order = 0;
                    foreach (string item in guideDto.CoreItems)
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            guide.Items.Add(new HeroGuideItem
                            {
                                SlotType = GuideSlotType.Core,
                                OrderIndex = order++,
                                ItemCode = item.Trim()
                            });
                        }
                    }
                }

                if (guideDto.LuxuryItems is { Count: > 0 })
                {
                    int order = 0;
                    foreach (string item in guideDto.LuxuryItems)
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                        {
                            guide.Items.Add(new HeroGuideItem
                            {
                                SlotType = GuideSlotType.Luxury,
                                OrderIndex = order++,
                                ItemCode = item.Trim()
                            });
                        }
                    }
                }

                // Seed 1NF Abilities
                if (guideDto.AbilityQueue is { Count: > 0 })
                {
                    int seq = 1;
                    foreach (string ability in guideDto.AbilityQueue)
                    {
                        if (!string.IsNullOrWhiteSpace(ability))
                        {
                            guide.Abilities.Add(new HeroGuideAbility
                            {
                                LevelSequence = seq++,
                                AbilityCode = ability.Trim()
                            });
                        }
                    }
                }

                guides.Add(guide);
            }

            await context.HeroGuides.AddRangeAsync(guides, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Successfully auto-seeded {Count} HeroGuides into database", guideDTOs.Length);
            }
        }
    }

    public static async Task SeedHeroItems(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
        if (!File.Exists(DataFiles.HeroItems))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("HeroItems seed file missing at {Path}", DataFiles.HeroItems);
            }

            return;
        }

        int existingDbItemCount = await context.HeroItems.CountAsync(cancellationToken);
        if (existingDbItemCount > 0 && !await context.HeroItemComponents.AnyAsync(cancellationToken))
        {
            await BackfillHeroItemComponentsAsync(context, logger, cancellationToken);
            return;
        }
        if (existingDbItemCount > 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Database HeroItems table is fully populated ({Count} items)",
                    existingDbItemCount);
            }

            return;
        }

        List<HeroItem> seedItems;
        lock (s_seedLock)
        {
            if (s_cachedHeroItems == null)
            {
                JsonSerializerOptions options = new()
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                };
                s_cachedHeroItems = JsonSerializer.Deserialize<List<HeroItem>>
                (File.ReadAllText(DataFiles.HeroItems), options) ?? [];
            }

            seedItems =
            [
                .. s_cachedHeroItems.Select(i => new HeroItem
                {
                    Code = i.Code,
                    Name = i.Name,
                    Cost = i.Cost,
                    Description = i.Description,
                    Flavor = i.Flavor,
                    IconPath = i.IconPath,
                    ComponentCodesJson = i.ComponentCodesJson,
                    RecipeCost = i.RecipeCost,
                    Upgrade = i.Upgrade,
                    Category = i.Category,
                    IsActive = i.IsActive
                })
            ];
        }

        foreach (HeroItem item in seedItems)
        {
            if (item.Components.Count > 1)
            {
                item.Components = item.Components
                    .GroupBy(c => c.ComponentItemCode)
                    .Select(g => new HeroItemComponent
                    {
                        ParentItem = item,
                        ComponentItemCode = g.Key,
                        Quantity = g.Sum(c => c.Quantity)
                    })
                    .ToList();
            }
        }

        if (seedItems.Count > 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Auto-seeding {Count} HeroItems into database...", seedItems.Count);
            }

            await context.HeroItems.AddRangeAsync(seedItems, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Successfully auto-seeded {Count} HeroItems into database", seedItems.Count);
            }
        }
    }

    private static async Task BackfillHeroItemComponentsAsync(
        MerrickContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<HeroItem> jsonItems;
        lock (s_seedLock)
        {
            if (s_cachedHeroItems == null)
            {
                JsonSerializerOptions options = new()
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                };
                s_cachedHeroItems = JsonSerializer.Deserialize<List<HeroItem>>(
                    File.ReadAllText(DataFiles.HeroItems), options) ?? [];
            }

            jsonItems = [.. s_cachedHeroItems];
        }

        Dictionary<string, int> dbItemIdsByCode = await context.HeroItems
            .AsNoTracking()
            .ToDictionaryAsync(i => i.Code, i => i.ID, StringComparer.OrdinalIgnoreCase, cancellationToken);

        List<HeroItemComponent> toInsert = [];
        foreach (HeroItem jsonItem in jsonItems)
        {
            if (string.IsNullOrWhiteSpace(jsonItem.Code) ||
                !dbItemIdsByCode.TryGetValue(jsonItem.Code, out int parentId) ||
                jsonItem.Components is not { Count: > 0 })
            {
                continue;
            }

            foreach (IGrouping<string, HeroItemComponent> grp in jsonItem.Components
                         .Where(c => !string.IsNullOrWhiteSpace(c.ComponentItemCode))
                         .GroupBy(c => c.ComponentItemCode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                toInsert.Add(new HeroItemComponent
                {
                    ParentItemID = parentId,
                    ComponentItemCode = grp.Key,
                    Quantity = Math.Max(1, grp.Sum(c => Math.Max(1, c.Quantity)))
                });
            }
        }

        if (toInsert.Count > 0)
        {
            await context.HeroItemComponents.AddRangeAsync(toInsert, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[SeedHeroItems] Auto-backfilled {ComponentCount} HeroItemComponents recipe links across {ItemCount} existing HeroItems.",
                toInsert.Count, dbItemIdsByCode.Count);
        }
    }

    public static async Task SeedHeroDetails(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(DataFiles.HeroDetails))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("HeroDetails seed file missing at {Path}", DataFiles.HeroDetails);
            }

            return;
        }

        int existingDbHeroCount = await context.HeroDetails.CountAsync(cancellationToken);
        if (existingDbHeroCount > 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Database HeroDetails table is fully populated ({Count} heroes)",
                    existingDbHeroCount);
            }

            return;
        }

        List<HeroDetail> heroes;
        lock (s_seedLock)
        {
            if (s_cachedHeroDetails == null)
            {
                s_cachedHeroDetails = JsonSerializer.Deserialize<List<HeroDetail>>
                (File.ReadAllText(DataFiles.HeroDetails),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }

            heroes =
            [
                .. s_cachedHeroDetails.Select(h => new HeroDetail
                {
                    HeroCode = h.HeroCode,
                    DisplayName = h.DisplayName,
                    PrimaryAttribute = h.PrimaryAttribute,
                    AttackType = h.AttackType,
                    BaseStrength = h.BaseStrength,
                    StrengthGrowth = h.StrengthGrowth,
                    BaseAgility = h.BaseAgility,
                    AgilityGrowth = h.AgilityGrowth,
                    BaseIntelligence = h.BaseIntelligence,
                    IntelligenceGrowth = h.IntelligenceGrowth,
                    Armor = h.Armor,
                    ArmorMitigation = h.ArmorMitigation,
                    MinDamage = h.MinDamage,
                    MaxDamage = h.MaxDamage,
                    MovementSpeed = h.MovementSpeed,
                    IconPath = h.IconPath,
                    SplashImagePath = h.SplashImagePath,
                    Lore = h.Lore,
                    AbilitiesJson = h.AbilitiesJson
                })
            ];
        }

        if (heroes.Count > 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Auto-seeding {Count} HeroDetails into database...", heroes.Count);
            }

            await context.HeroDetails.AddRangeAsync(heroes, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Successfully auto-seeded {Count} HeroDetails into database", heroes.Count);
            }
        }
    }

    public static async Task ExportDatabaseToSeedFiles(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
        JsonSerializerOptions jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        // 1. Export HeroItems
        List<HeroItem> dbItems = await context.HeroItems.AsNoTracking().ToListAsync(cancellationToken);
        if (dbItems.Count > 0)
        {
            string json = JsonSerializer.Serialize(dbItems, jsonOptions);
            await File.WriteAllTextAsync(DataFiles.HeroItems, json, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Exported {Count} HeroItems from Database to {Path}", dbItems.Count,
                    DataFiles.HeroItems);
            }
        }

        // 2. Export HeroDetails
        List<HeroDetail> dbDetails = await context.HeroDetails.AsNoTracking().ToListAsync(cancellationToken);
        if (dbDetails.Count > 0)
        {
            string json = JsonSerializer.Serialize(dbDetails, jsonOptions);
            await File.WriteAllTextAsync(DataFiles.HeroDetails, json, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Exported {Count} HeroDetails from Database to {Path}", dbDetails.Count,
                    DataFiles.HeroDetails);
            }
        }

        // 3. Export HeroGuides
        List<HeroGuide> dbGuides = await context.HeroGuides
            .AsNoTracking()
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .ToListAsync(cancellationToken);
        if (dbGuides.Count > 0)
        {
            string json = JsonSerializer.Serialize(dbGuides, jsonOptions);
            await File.WriteAllTextAsync(DataFiles.HeroGuides, json, cancellationToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Exported {Count} HeroGuides from Database to {Path}", dbGuides.Count,
                    DataFiles.HeroGuides);
            }
        }
    }

    public static bool IsDevelopmentSeedEnabled(IConfiguration? configuration = null)
    {
        if (string.Equals(configuration?["KONGOR_SEED_DEV_DATA"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? env = configuration?["ASPNETCORE_ENVIRONMENT"]
                      ?? configuration?["DOTNET_ENVIRONMENT"]
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                      ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        if (!string.IsNullOrWhiteSpace(env))
        {
            return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
        }

        return configuration == null ||
               !string.Equals(configuration["KONGOR_STANDALONE"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task EnsureStaffAccountsAsync(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken, IConfiguration? configuration = null)
    {
        bool updated = false;

        // Ensure roles exist
        Role? defaultUserRole =
            await context.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, cancellationToken);
        if (defaultUserRole == null)
        {
            defaultUserRole = new Role { Name = UserRoles.User };
            await context.Roles.AddAsync(defaultUserRole, cancellationToken);
            updated = true;
        }

#if PLAYTEST_BUILD
        _ = configuration;
        if (updated)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        logger.LogInformation("[Bootstrap] EnsureStaffAccountsAsync dev staff/host seeding skipped in playtest build");
        return;
#endif
#if !PLAYTEST_BUILD
        if (!IsDevelopmentSeedEnabled(configuration))
        {
            if (updated)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("[Bootstrap] EnsureStaffAccountsAsync static demo/staff seeding skipped outside Development environment");
            return;
        }

        List<string> staffAccountNames =
        [
            "guccidonut",
            "Sooner_Magic"
        ];

        List<string> relevantAccountNames =
        [
            .. staffAccountNames,
            "guccihost",
            "guccihost_local",
            "guccihost_dock",
            "guccihost_k3s",
            "guccihost_arc",
            "KONGOR",
            "HOST",
            "SERVER"
        ];

        List<Account> allRelevantAccounts = await context.Accounts
            .Include(a => a.User)
                .ThenInclude(u => u.Accounts)
            .Include(a => a.SelectedStoreItems)
            .Where(a => relevantAccountNames.Contains(a.Name))
            .ToListAsync(cancellationToken);

        List<string> candidateHostEmails =
        [
            "host_guccihost@nexus.local",
            "host_guccihost_local@nexus.local",
            "host_guccihost_dock@nexus.local",
            "host_guccihost_k3s@nexus.local",
            "host_guccihost_arc@nexus.local"
        ];

        List<User> targetUsers = await context.Users
            .Include(u => u.Accounts)
            .Include(u => u.Role)
            .Where(u => u.Accounts.Any(a => relevantAccountNames.Contains(a.Name)) ||
                        candidateHostEmails.Contains(u.EmailAddress))
            .ToListAsync(cancellationToken);

        List<HostApplication> allHostApplications = await context.HostApplications
            .Include(h => h.HostAccount)
            .ToListAsync(cancellationToken);

        foreach (Account acc in allRelevantAccounts.Where(a => staffAccountNames.Contains(a.Name)))
        {
            if (acc.Type != AccountType.Staff)
            {
                acc.Type = AccountType.Staff;
                updated = true;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("[Seed] Granted Staff account type to '{AccountName}'", acc.Name);
                }
            }
        }

        // Dedicated User for guccihost: credentials resolved securely from configuration / User Secrets
        string? configuredHostPassword = configuration?["HOST_PASSWORD"]
                                         ?? configuration?["HostAccount:Password"]
                                         ?? configuration?["Host:Password"]
                                         ?? configuration?["Compel:Password"]
                                         ?? configuration?["MatchServer:Password"]
                                         ?? Environment.GetEnvironmentVariable("HOST_PASSWORD");

        User? gucciHostUser = targetUsers.FirstOrDefault(u => u.EmailAddress == "host_guccihost@nexus.local");
        string srpSalt = gucciHostUser?.SRPPasswordSalt ?? HostSecurityService.GenerateSrpSalt();
        string srpHash = !string.IsNullOrWhiteSpace(configuredHostPassword)
            ? HostSecurityService.ComputeSrpHash(configuredHostPassword, srpSalt, passwordIsHashed: false)
            : gucciHostUser?.SRPPasswordHash ?? "9fb9cf192701aba7aaa38e6f15457082c93cfbe060bdbdc5fa67a986b355700c";

        if (gucciHostUser == null)
        {
            gucciHostUser = new User
            {
                EmailAddress = "host_guccihost@nexus.local",
                Role = defaultUserRole,
                SRPPasswordSalt = srpSalt,
                SRPPasswordHash = srpHash,
                PBKDF2PasswordHash =
                    "AQAAAAIAAYagAAAAEMUkpLAr01NjkKRPaXCyTa17nlOdPKJucn5QYur+wQBTDKCpgsAcREenK+pGJPBCRw=="
            };
            await context.Users.AddAsync(gucciHostUser, cancellationToken);
            targetUsers.Add(gucciHostUser);
            updated = true;
        }
        else if (!string.IsNullOrWhiteSpace(configuredHostPassword) && gucciHostUser.SRPPasswordHash != srpHash)
        {
            gucciHostUser.SRPPasswordSalt = srpSalt;
            gucciHostUser.SRPPasswordHash = srpHash;
            updated = true;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[Seed] Synchronized host SRP credentials from configuration for 'guccihost'");
            }
        }

        Account? hostAccount =
            allRelevantAccounts.FirstOrDefault(a => a.Name.Equals("guccihost", StringComparison.OrdinalIgnoreCase));
        if (hostAccount == null)
        {
            hostAccount = new Account
            {
                Name = "guccihost",
                User = gucciHostUser,
                Type = AccountType.ServerHost,
                IsMain = false,
                Clan = null,
                ClanTier = ClanTier.None,
                TimestampJoinedClan = null,
                AscensionLevel = 666,
                AutoConnectChatChannels = [ChatChannels.ServerHostsChannel]
            };
            await context.Accounts.AddAsync(hostAccount, cancellationToken);
            allRelevantAccounts.Add(hostAccount);
            updated = true;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[Seed] Created ServerHost account 'guccihost' with dedicated host User");
            }
        }
        else
        {
            if (hostAccount.User.ID != gucciHostUser.ID)
            {
                hostAccount.User = gucciHostUser;
                updated = true;
            }

            if (hostAccount.Type != AccountType.ServerHost)
            {
                hostAccount.Type = AccountType.ServerHost;
                updated = true;
            }
        }

        string[] dedicatedFleetHostNames = ["guccihost_local", "guccihost_dock", "guccihost_k3s", "guccihost_arc"];
        foreach (string fleetHostName in dedicatedFleetHostNames)
        {
            string fleetEmail = $"host_{fleetHostName.ToLowerInvariant()}@nexus.local";
            User? fleetHostUser = targetUsers.FirstOrDefault(u =>
                u.EmailAddress.Equals(fleetEmail, StringComparison.OrdinalIgnoreCase));
            string fleetSalt = fleetHostUser?.SRPPasswordSalt ?? srpSalt;
            string fleetHash = !string.IsNullOrWhiteSpace(configuredHostPassword)
                ? HostSecurityService.ComputeSrpHash(configuredHostPassword, fleetSalt, passwordIsHashed: false)
                : fleetHostUser?.SRPPasswordHash ?? srpHash;

            if (fleetHostUser == null)
            {
                fleetHostUser = new User
                {
                    EmailAddress = fleetEmail,
                    Role = defaultUserRole,
                    SRPPasswordSalt = fleetSalt,
                    SRPPasswordHash = fleetHash,
                    PBKDF2PasswordHash =
                        "AQAAAAIAAYagAAAAEMUkpLAr01NjkKRPaXCyTa17nlOdPKJucn5QYur+wQBTDKCpgsAcREenK+pGJPBCRw=="
                };
                await context.Users.AddAsync(fleetHostUser, cancellationToken);
                targetUsers.Add(fleetHostUser);
                updated = true;
            }
            else if (!string.IsNullOrWhiteSpace(configuredHostPassword) && fleetHostUser.SRPPasswordHash != fleetHash)
            {
                fleetHostUser.SRPPasswordSalt = fleetSalt;
                fleetHostUser.SRPPasswordHash = fleetHash;
                updated = true;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[Seed] Synchronized host SRP credentials from configuration for '{HostName}'",
                        fleetHostName);
                }
            }

            Account? fleetHostAccount = allRelevantAccounts.FirstOrDefault(a =>
                a.Name.Equals(fleetHostName, StringComparison.OrdinalIgnoreCase));
            if (fleetHostAccount == null)
            {
                fleetHostAccount = new Account
                {
                    Name = fleetHostName,
                    User = fleetHostUser,
                    Type = AccountType.ServerHost,
                    IsMain = false,
                    Clan = null,
                    ClanTier = ClanTier.None,
                    TimestampJoinedClan = null,
                    AscensionLevel = 666,
                    AutoConnectChatChannels = [ChatChannels.ServerHostsChannel]
                };
                await context.Accounts.AddAsync(fleetHostAccount, cancellationToken);
                allRelevantAccounts.Add(fleetHostAccount);
                updated = true;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[Seed] Created ServerHost account '{HostName}' with dedicated host User '{HostEmail}'",
                        fleetHostName,
                        fleetEmail);
                }
            }
            else
            {
                if (fleetHostAccount.User.ID != fleetHostUser.ID)
                {
                    fleetHostAccount.User = fleetHostUser;
                    updated = true;
                }

                if (fleetHostAccount.Type != AccountType.ServerHost)
                {
                    fleetHostAccount.Type = AccountType.ServerHost;
                    updated = true;
                }
            }
        }

        // Auto-heal: Ensure ALL ServerHost accounts have their own dedicated User (host_{name}@nexus.local)
        // and are NEVER sharing a User record with a human player's account
        List<Account> allHostAccounts = [.. allRelevantAccounts.Where(a => a.Type == AccountType.ServerHost)];

        foreach (Account hAcc in allHostAccounts)
        {
            bool sharesWithPlayer = hAcc.User.Accounts.Any(a => a.ID != hAcc.ID && a.Type != AccountType.ServerHost);
            bool isPersonalEmail = !hAcc.User.EmailAddress.StartsWith("host_", StringComparison.OrdinalIgnoreCase);

            if (sharesWithPlayer || isPersonalEmail)
            {
                string expectedHostEmail = $"host_{hAcc.Name.ToLowerInvariant()}@nexus.local";
                User? dedicatedUser = targetUsers.FirstOrDefault(u => u.EmailAddress == expectedHostEmail);

                if (dedicatedUser == null)
                {
                    dedicatedUser = new User
                    {
                        EmailAddress = expectedHostEmail,
                        Role = defaultUserRole,
                        SRPPasswordSalt = hAcc.User.SRPPasswordSalt,
                        SRPPasswordHash = hAcc.User.SRPPasswordHash,
                        PBKDF2PasswordHash = !string.IsNullOrEmpty(hAcc.User.PBKDF2PasswordHash)
                            ? hAcc.User.PBKDF2PasswordHash
                            : "AQAAAAIAAYagAAAAEMUkpLAr01NjkKRPaXCyTa17nlOdPKJucn5QYur+wQBTDKCpgsAcREenK+pGJPBCRw=="
                    };
                    await context.Users.AddAsync(dedicatedUser, cancellationToken);
                    targetUsers.Add(dedicatedUser);
                }

                hAcc.User = dedicatedUser;
                updated = true;

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[AutoHeal] Separated ServerHost account '{HostName}' from shared player user and bound to dedicated host User '{HostEmail}'",
                        hAcc.Name, expectedHostEmail);
                }
            }
        }

        // Ensure guccidonut is marked as Main Account and linked to guccihost HostApplication
        Account? guccidonutAccount = allRelevantAccounts
            .FirstOrDefault(a => a.Name.Equals("guccidonut", StringComparison.OrdinalIgnoreCase) ||
                                 a.Name.Equals("guccidonuts", StringComparison.OrdinalIgnoreCase));

        if (guccidonutAccount != null)
        {
            if (!guccidonutAccount.IsMain)
            {
                guccidonutAccount.IsMain = true;
                updated = true;
            }

            if (hostAccount != null && hostAccount.OwnerAccountID != guccidonutAccount.ID)
            {
                hostAccount.OwnerAccountID = guccidonutAccount.ID;
                hostAccount.OwnerAccount = guccidonutAccount;
                updated = true;
            }

            Clan? guciClan = await context.Clans.FirstOrDefaultAsync(c => c.Tag == "GUCI", cancellationToken);
            if (guciClan == null)
            {
                guciClan = new Clan { Name = "GUCI", Tag = "GUCI" };
                await context.Clans.AddAsync(guciClan, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }

            if (guccidonutAccount.ClanID != guciClan.ID)
            {
                guccidonutAccount.Clan = guciClan;
                guccidonutAccount.ClanID = guciClan.ID;
                guccidonutAccount.ClanTier = ClanTier.Leader;
                guccidonutAccount.TimestampJoinedClan = TimeProvider.System.GetUtcNow();
                updated = true;
                logger.LogInformation("[Seed] Attached Account '{AccountName}' to Clan 'GUCI' as Leader", guccidonutAccount.Name);
            }

            bool hasCustomIcon = await context.UserInventory.AnyAsync(
                i => i.UserID == guccidonutAccount.User.ID && i.ItemCode == "ai.custom_icon:1",
                cancellationToken);
            if (!hasCustomIcon)
            {
                context.UserInventory.Add(new UserInventory
                {
                    UserID = guccidonutAccount.User.ID,
                    AccountID = guccidonutAccount.ID,
                    ItemCode = "ai.custom_icon:1",
                    Quantity = 1,
                    IsPermanent = true,
                    TimestampAcquired = TimeProvider.System.GetUtcNow(),
                    Source = "Seed"
                });
                updated = true;
            }

            if (!guccidonutAccount.HasSelectedStoreItem("ai.custom_icon:1"))
            {
                bool existsInDb = await context.AccountSelectedStoreItems.AnyAsync(
                    s => s.AccountID == guccidonutAccount.ID && s.ItemCode == "ai.custom_icon:1",
                    cancellationToken);

                if (!existsInDb)
                {
                    guccidonutAccount.SelectedStoreItems.RemoveAll(item =>
                        item.ItemCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase));
                    guccidonutAccount.AddSelectedStoreItem("ai.custom_icon:1");
                    updated = true;
                }
                else
                {
                    logger.LogInformation(
                        "[Seed] Account '{AccountName}' already has 'ai.custom_icon:1' in database. Skipping duplicate insert.",
                        guccidonutAccount.Name);
                }
            }

            List<Account> otherAccounts =
                [.. guccidonutAccount.User.Accounts.Where(a => a.ID != guccidonutAccount.ID && a.IsMain)];

            foreach (Account other in otherAccounts)
            {
                other.IsMain = false;
                updated = true;
            }

            var fleetConfigs = new[]
            {
                (Name: "guccihost", Prefix: "^pDONUT ^oARENA ^g"),
                (Name: "guccihost_local", Prefix: "^pDONUT ^oARENA ^g"),
                (Name: "guccihost_dock", Prefix: "^pDONUT ^oARENA ^g"),
                (Name: "guccihost_k3s", Prefix: "^pDONUT ^oARENA ^g")
            };

            foreach (var fleet in fleetConfigs)
            {
                Account? targetHostAccount = allRelevantAccounts
                    .FirstOrDefault(a => a.Name.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase));

                if (targetHostAccount != null && targetHostAccount.OwnerAccountID != guccidonutAccount.ID)
                {
                    targetHostAccount.OwnerAccountID = guccidonutAccount.ID;
                    targetHostAccount.OwnerAccount = guccidonutAccount;
                    updated = true;
                }

                HostApplication? targetHostApp = allHostApplications
                    .FirstOrDefault(h => h.HostUsername.Equals(fleet.Name, StringComparison.OrdinalIgnoreCase));

                if (targetHostApp != null)
                {
                    if (targetHostApp.MainAccountID != guccidonutAccount.ID)
                    {
                        targetHostApp.MainAccountID = guccidonutAccount.ID;
                        targetHostApp.MainAccount = guccidonutAccount;
                        targetHostApp.UserID = guccidonutAccount.User.ID;
                        targetHostApp.User = guccidonutAccount.User;
                        targetHostApp.Status = HostApplicationStatus.Approved;
                        if (targetHostAccount != null)
                        {
                            targetHostApp.HostAccountID = targetHostAccount.ID;
                            targetHostApp.HostAccount = targetHostAccount;
                        }

                        updated = true;
                    }

                    if (targetHostApp.ServerNamePrefix != fleet.Prefix)
                    {
                        targetHostApp.ServerNamePrefix = fleet.Prefix;
                        updated = true;
                    }

                    if (!string.IsNullOrWhiteSpace(configuredHostPassword))
                    {
                        string expectedEncryptedPassword = HostSecurityService.EncryptPassword(configuredHostPassword);
                        if (targetHostApp.EncryptedHostPassword != expectedEncryptedPassword)
                        {
                            targetHostApp.EncryptedHostPassword = expectedEncryptedPassword;
                            updated = true;
                            if (logger.IsEnabled(LogLevel.Information))
                            {
                                logger.LogInformation(
                                    "[Seed] Synchronized EncryptedHostPassword for '{HostUsername}' application from configuration",
                                    fleet.Name);
                            }
                        }
                    }
                }
                else if (targetHostAccount != null)
                {
                    HostApplication newHostApp = new()
                    {
                        UserID = guccidonutAccount.User.ID,
                        User = guccidonutAccount.User,
                        MainAccountID = guccidonutAccount.ID,
                        MainAccount = guccidonutAccount,
                        HostAccountID = targetHostAccount.ID,
                        HostAccount = targetHostAccount,
                        HostUsername = fleet.Name,
                        EncryptedHostPassword = HostSecurityService.EncryptPassword(
                            !string.IsNullOrWhiteSpace(configuredHostPassword)
                                ? configuredHostPassword
                                : HostSecurityService.GenerateRandomHostPassword()),
                        Status = HostApplicationStatus.Approved,
                        Location = "USE",
                        Instances = 2,
                        ServerNamePrefix = fleet.Prefix,
                        TimestampReviewed = TimeProvider.System.GetUtcNow(),
                        ReviewedByStaffName = "SystemSeed"
                    };
                    await context.HostApplications.AddAsync(newHostApp, cancellationToken);
                    allHostApplications.Add(newHostApp);
                    updated = true;
                }
            }
        }

        User? adminUserToFix = targetUsers.FirstOrDefault(u => u.EmailAddress == "admin@kongor.local" || u.Accounts.Any(a => a.Name.Equals("KONGOR", StringComparison.OrdinalIgnoreCase)));
        if (adminUserToFix != null && adminUserToFix.SRPPasswordHash !=
            "9fb9cf192701aba7aaa38e6f15457082c93cfbe060bdbdc5fa67a986b355700c")
        {
            adminUserToFix.SRPPasswordHash = "9fb9cf192701aba7aaa38e6f15457082c93cfbe060bdbdc5fa67a986b355700c";
            updated = true;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[Seed] Synchronized admin@kongor.local SRP password hash for 'KONGOR'");
            }
        }

        Account? kongorAccount = allRelevantAccounts
            .FirstOrDefault(a => a.Name.Equals("KONGOR", StringComparison.OrdinalIgnoreCase));

        if (kongorAccount != null)
        {
            List<Account> kongorHosts =
            [
                .. allRelevantAccounts
                    .Where(a => a.Name.Equals("HOST", StringComparison.OrdinalIgnoreCase) ||
                                a.Name.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
            ];

            foreach (Account kh in kongorHosts)
            {
                if (kh.OwnerAccountID != kongorAccount.ID)
                {
                    kh.OwnerAccountID = kongorAccount.ID;
                    kh.OwnerAccount = kongorAccount;
                    updated = true;
                }
            }

            Account? hostAcc =
                kongorHosts.FirstOrDefault(a => a.Name.Equals("HOST", StringComparison.OrdinalIgnoreCase));

            HostApplication? kongorHostApp = allHostApplications
                .FirstOrDefault(h => h.MainAccountID == kongorAccount.ID ||
                                     h.HostUsername.Equals("HOST", StringComparison.OrdinalIgnoreCase));

            if (kongorHostApp == null)
            {
                HostApplication newHostApp = new()
                {
                    UserID = kongorAccount.User.ID,
                    User = kongorAccount.User,
                    MainAccountID = kongorAccount.ID,
                    MainAccount = kongorAccount,
                    HostAccountID = hostAcc?.ID,
                    HostAccount = hostAcc,
                    HostUsername = "HOST",
                    Status = HostApplicationStatus.Pending,
                    Location = "USE/USW",
                    Instances = 4,
                    ServerNamePrefix = "Project KONGOR Dedicated Node",
                    TimestampReviewed = TimeProvider.System.GetUtcNow(),
                    ReviewedByStaffName = "SystemSeed"
                };
                await context.HostApplications.AddAsync(newHostApp, cancellationToken);
                allHostApplications.Add(newHostApp);
                updated = true;
            }
            else if (kongorHostApp.Status == HostApplicationStatus.Approved)
            {
                // Deprecate duplicate seed host application so only the active volunteer host (guccihost) is approved
                kongorHostApp.Status = HostApplicationStatus.Pending;
                updated = true;
            }
        }

        // Auto-heal: Ensure all approved HostApplications have OwnerAccountID synced
        List<HostApplication> approvedHostApps =
            [.. allHostApplications.Where(h => h.Status == HostApplicationStatus.Approved && h.HostAccount != null)];

        foreach (HostApplication hApp in approvedHostApps)
        {
            if (hApp.HostAccount != null && hApp.HostAccount.OwnerAccountID != hApp.MainAccountID)
            {
                hApp.HostAccount.OwnerAccountID = hApp.MainAccountID;
                updated = true;
            }

            if (string.IsNullOrEmpty(hApp.EncryptedAuthToken))
            {
                hApp.EncryptedAuthToken =
                    HostSecurityService.EncryptPassword(HostSecurityService.GenerateRandomAuthToken());
                updated = true;
            }

            if (string.IsNullOrEmpty(hApp.EncryptedHostPassword))
            {
                hApp.EncryptedHostPassword = HostSecurityService.EncryptPassword(
                    !string.IsNullOrWhiteSpace(configuredHostPassword)
                        ? configuredHostPassword
                        : HostSecurityService.GenerateRandomHostPassword());
                updated = true;
            }
        }

        if (updated)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
#endif
    }

    public static async Task EnsureStandaloneBootstrapAsync(MerrickContext context, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(configuration["KONGOR_STANDALONE"], "true", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(configuration["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"]))
        {
            return;
        }

        if (await context.Users.AnyAsync(u => u.EmailAddress == "admin@kongor.local", cancellationToken))
        {
            await EnsureQueueArcadeHostAccountsAsync(context, configuration, logger, cancellationToken);
            logger.LogInformation("[Bootstrap] Standalone bootstrap already satisfied (admin@kongor.local exists)");
            return;
        }

        Role? adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.Administrator, cancellationToken);
        bool rolesAdded = false;
        if (adminRole == null)
        {
            adminRole = new Role { Name = UserRoles.Administrator };
            await context.Roles.AddAsync(adminRole, cancellationToken);
            rolesAdded = true;
        }

        Role? userRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, cancellationToken);
        if (userRole == null)
        {
            userRole = new Role { Name = UserRoles.User };
            await context.Roles.AddAsync(userRole, cancellationToken);
            rolesAdded = true;
        }

        if (rolesAdded)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        string adminPassword = configuration["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"]!;
        string hostPassword = !string.IsNullOrWhiteSpace(configuration["KONGOR_BOOTSTRAP_HOST_PASSWORD"])
            ? configuration["KONGOR_BOOTSTRAP_HOST_PASSWORD"]!
            : Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        int starterGold = configuration.GetValue("KONGOR_STANDALONE_STARTER_GOLD", 10000);
        int starterSilver = configuration.GetValue("KONGOR_STANDALONE_STARTER_SILVER", 25000);
        int starterTickets = configuration.GetValue("KONGOR_STANDALONE_STARTER_TICKETS", 25);
        int inviteCount = Math.Max(0, configuration.GetValue("KONGOR_BOOTSTRAP_INVITE_COUNT", 10));

        PasswordHasher<User> passwordHasher = new();
        Argon2idHasher argon2Hasher = new();

        string adminSalt = S2Crypto.GenerateSrpSalt();
        string adminSrpHash = S2Crypto.ComputeSrpFromPlaintext(adminPassword, adminSalt);
        string adminArgonHash = await argon2Hasher.HashPasswordAsync(adminPassword, cancellationToken);

        User adminUser = new()
        {
            EmailAddress = "admin@kongor.local",
            Role = adminRole,
            SRPPasswordSalt = adminSalt,
            SRPPasswordHash = adminSrpHash,
            Argon2idPasswordHash = adminArgonHash,
            GoldCoins = starterGold,
            SilverCoins = starterSilver,
            PlinkoTickets = starterTickets,
            TotalLevel = 1,
            TotalExperience = 0
        };
        adminUser.PBKDF2PasswordHash = passwordHasher.HashPassword(adminUser, adminPassword);

        Account adminAccount = new()
        {
            Name = "Admin",
            User = adminUser,
            Type = AccountType.Staff,
            IsMain = true
        };
        adminUser.Accounts.Add(adminAccount);

        string hostSalt = S2Crypto.GenerateSrpSalt();
        string hostSrpHash = S2Crypto.ComputeSrpFromPlaintext(hostPassword, hostSalt);
        string hostArgonHash = await argon2Hasher.HashPasswordAsync(hostPassword, cancellationToken);

        User hostUser = new()
        {
            EmailAddress = "host@kongor.local",
            Role = userRole,
            SRPPasswordSalt = hostSalt,
            SRPPasswordHash = hostSrpHash,
            Argon2idPasswordHash = hostArgonHash,
            TotalLevel = 1,
            TotalExperience = 0
        };
        hostUser.PBKDF2PasswordHash = passwordHasher.HashPassword(hostUser, hostPassword);

        Account hostAccount = new()
        {
            Name = "kongor_host",
            User = hostUser,
            Type = AccountType.ServerHost,
            IsMain = false,
            AscensionLevel = 666,
            AutoConnectChatChannels = [ChatChannels.ServerHostsChannel]
        };
        hostUser.Accounts.Add(hostAccount);

        await context.Users.AddAsync(adminUser, cancellationToken);
        await context.Accounts.AddAsync(adminAccount, cancellationToken);
        await context.Users.AddAsync(hostUser, cancellationToken);
        await context.Accounts.AddAsync(hostAccount, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        hostAccount.OwnerAccountID = adminAccount.ID;
        hostAccount.OwnerAccount = adminAccount;

        InviteSystemAuditLog auditLog = new()
        {
            StaffAccountID = adminAccount.ID,
            StaffAccountName = adminAccount.Name,
            InviteOnlyEnabled = true,
            Notes = "Standalone initial bootstrap",
            Timestamp = TimeProvider.System.GetUtcNow()
        };
        await context.InviteSystemAuditLogs.AddAsync(auditLog, cancellationToken);

        List<string> generatedCodes = new(inviteCount);
        for (int i = 0; i < inviteCount; i++)
        {
            string code = await InviteRepository.GenerateUniqueInviteCodeAsync(context, cancellationToken);
            UserInvite invite = new()
            {
                Code = code,
                CreatedByUserID = adminUser.ID,
                TimestampCreated = TimeProvider.System.GetUtcNow()
            };
            await context.UserInvites.AddAsync(invite, cancellationToken);
            generatedCodes.Add(code);
        }

        await context.SaveChangesAsync(cancellationToken);

        string? outputPath = configuration["KONGOR_BOOTSTRAP_INVITES_OUTPUT_PATH"];
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllLinesAsync(outputPath, generatedCodes, cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Bootstrap] Could not set 0600 file mode on {OutputPath}", outputPath);
                }
            }
        }

        logger.LogInformation("[Bootstrap] created Admin, kongor_host, invite-only=on, invites={InviteCount}", inviteCount);
        await EnsureQueueArcadeHostAccountsAsync(context, configuration, logger, cancellationToken);
    }

    public static async Task EnsureSharedInviteCodesAsync(
        MerrickContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        User? ownerUser = await context.Users
            .AsNoTracking()
            .OrderBy(u => u.ID)
            .FirstOrDefaultAsync(u => u.EmailAddress == "admin@kongor.local", cancellationToken)
            ?? await context.Users
                .AsNoTracking()
                .Include(u => u.Role)
                .OrderBy(u => u.ID)
                .FirstOrDefaultAsync(u => u.Role.Name == UserRoles.Administrator, cancellationToken)
            ?? await context.Users
                .AsNoTracking()
                .OrderBy(u => u.ID)
                .FirstOrDefaultAsync(cancellationToken);

        if (ownerUser is null)
        {
            logger.LogDebug("[SeedSharedInvites] Skipping shared invite seed because no Users exist yet.");
            return;
        }

        UserInvite? existing = await context.UserInvites
            .FirstOrDefaultAsync(i => i.Code == UserInvite.SharedWeeklyInviteCode, cancellationToken);

        if (existing is null)
        {
            UserInvite sharedInvite = new()
            {
                Code = UserInvite.SharedWeeklyInviteCode,
                CreatedByUserID = ownerUser.ID,
                CreatorIpAddress = UserInvite.MultiUseCreatorMarker,
                TimestampCreated = UserInvite.SharedWeeklyInviteCreatedUtc,
                TimestampExpires = UserInvite.SharedWeeklyInviteExpiresUtc,
                IsRevoked = false
            };

            await context.UserInvites.AddAsync(sharedInvite, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[SeedSharedInvites] Provisioned shared multi-use invite code '{Code}' owned by User #{OwnerId} (expires {ExpiresUtc:O}).",
                sharedInvite.Code,
                ownerUser.ID,
                sharedInvite.TimestampExpires);
            return;
        }

        bool updated = false;
        if (!string.Equals(existing.CreatorIpAddress, UserInvite.MultiUseCreatorMarker, StringComparison.OrdinalIgnoreCase))
        {
            existing.CreatorIpAddress = UserInvite.MultiUseCreatorMarker;
            updated = true;
        }

        if (!existing.TimestampExpires.HasValue)
        {
            existing.TimestampExpires = UserInvite.SharedWeeklyInviteExpiresUtc;
            updated = true;
        }

        if (existing.TimestampRedeemed.HasValue)
        {
            existing.TimestampRedeemed = null;
            existing.RedeemedByUserID = null;
            existing.RedeemedByAccountID = null;
            existing.RedeemerIpAddress = null;
            updated = true;
        }

        if (updated)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[SeedSharedInvites] Synchronized shared multi-use invite code '{Code}' (expires {ExpiresUtc:O}, revoked={IsRevoked}).",
                existing.Code,
                existing.TimestampExpires,
                existing.IsRevoked);
        }
        else
        {
            logger.LogDebug(
                "[SeedSharedInvites] Verified shared multi-use invite code '{Code}' already active (expires {ExpiresUtc:O}, revoked={IsRevoked}).",
                existing.Code,
                existing.TimestampExpires,
                existing.IsRevoked);
        }
    }

    internal static async Task EnsureQueueArcadeHostAccountsAsync(
        MerrickContext context,
        IConfiguration? configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        Role? userRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, cancellationToken);
        if (userRole is null)
        {
            return;
        }

        HashSet<string> candidateBaseHostNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "guccihost",
            "kongor_host"
        };

        string? configuredHostName = configuration?["HostAccount:UserName"]
                                     ?? configuration?["Compel:HostUserName"]
                                     ?? configuration?["Compel:UserName"]
                                     ?? configuration?["HOST_USERNAME"]
                                     ?? Environment.GetEnvironmentVariable("HOST_USERNAME");
        if (!string.IsNullOrWhiteSpace(configuredHostName))
        {
            string trimmed = configuredHostName.Trim();
            string[] suffixes = ["_arc", "_arcade", "_local", "_dock", "_docker", "_k3s"];
            foreach (string suffix in suffixes)
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
                {
                    trimmed = trimmed[..^suffix.Length];
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                candidateBaseHostNames.Add(trimmed);
            }
        }

        bool addedAny = false;
        foreach (string baseName in candidateBaseHostNames)
        {
            string stem = baseName.Length > 11 ? baseName[..11].TrimEnd('_') : baseName.TrimEnd('_');
            if (string.IsNullOrWhiteSpace(stem))
            {
                continue;
            }

            string arcadeAccountName = $"{stem}_arc";
            Account? existingArcade = await context.Accounts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.Name == arcadeAccountName, cancellationToken);
            Account? baseHost = await context.Accounts
                .Include(a => a.User)
                .FirstOrDefaultAsync(a => a.Name == baseName && a.Type == AccountType.ServerHost, cancellationToken);

            if (baseHost?.User is null)
            {
                continue;
            }

            if (existingArcade is not null)
            {
                if (existingArcade.Type == AccountType.ServerHost &&
                    existingArcade.User is not null &&
                    (existingArcade.User.SRPPasswordHash != baseHost.User.SRPPasswordHash ||
                     existingArcade.User.SRPPasswordSalt != baseHost.User.SRPPasswordSalt))
                {
                    existingArcade.User.SRPPasswordSalt = baseHost.User.SRPPasswordSalt;
                    existingArcade.User.SRPPasswordHash = baseHost.User.SRPPasswordHash;
                    addedAny = true;
                    logger.LogInformation(
                        "[Seed] Synchronized Queue Arcade ServerHost '{ArcadeAccount}' SRP credentials with base host '{BaseHost}'",
                        arcadeAccountName,
                        baseName);
                }

                continue;
            }

            string arcadeEmail = $"host_{arcadeAccountName.ToLowerInvariant()}@nexus.local";
            User? arcadeUser = await context.Users.FirstOrDefaultAsync(u => u.EmailAddress == arcadeEmail, cancellationToken);
            if (arcadeUser is null)
            {
                arcadeUser = new User
                {
                    EmailAddress = arcadeEmail,
                    Role = userRole,
                    SRPPasswordSalt = baseHost.User.SRPPasswordSalt,
                    SRPPasswordHash = baseHost.User.SRPPasswordHash,
                    Argon2idPasswordHash = baseHost.User.Argon2idPasswordHash
                };
                arcadeUser.PBKDF2PasswordHash = !string.IsNullOrEmpty(baseHost.User.PBKDF2PasswordHash)
                    ? baseHost.User.PBKDF2PasswordHash
                    : new PasswordHasher<User>().HashPassword(
                        arcadeUser,
                        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
                await context.Users.AddAsync(arcadeUser, cancellationToken);
            }

            Account arcadeAccount = new()
            {
                Name = arcadeAccountName,
                User = arcadeUser,
                OwnerAccountID = baseHost.OwnerAccountID,
                Type = AccountType.ServerHost,
                IsMain = false,
                Clan = null,
                ClanTier = ClanTier.None,
                TimestampJoinedClan = null,
                AscensionLevel = 666,
                AutoConnectChatChannels = [ChatChannels.ServerHostsChannel]
            };
            await context.Accounts.AddAsync(arcadeAccount, cancellationToken);
            addedAny = true;

            logger.LogInformation(
                "[Seed] Provisioned dedicated Queue Arcade ServerHost account '{ArcadeAccount}' from base host '{BaseHost}'",
                arcadeAccountName,
                baseName);
        }

        if (addedAny)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public static async Task SeedLoreComics(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
        if (await context.LoreArcs.AnyAsync(cancellationToken))
        {
            logger.LogInformation("[SeedLoreComics] Lore arcs and comics already seeded.");
            return;
        }

        logger.LogInformation("[SeedLoreComics] Seeding initial Project Kongor parallel universe lore & comics...");

        Account? adminAccount = await context.Accounts.FirstOrDefaultAsync(a => a.Name == "Kongor", cancellationToken)
                                ?? await context.Accounts.FirstOrDefaultAsync(cancellationToken);

        LoreArc arc1 = new()
        {
            Title = "The Primeval Canopy",
            Slug = "primeval-canopy",
            Description =
                "Before the cosmic convergence and the dark runic corruption, the ancient Ironwood forest was a sanctuary of primordial balance. Witness the untamed era of peace before the dawn was shattered.",
            BannerImageUrl = "/images/lore/arcs/primeval-canopy-banner.webp",
            OrderIndex = 1,
            IsPublished = true,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };

        await context.LoreArcs.AddAsync(arc1, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // Comic #1: Whispers in the Ironwood
        LoreComic comic1 = new()
        {
            ArcID = arc1.ID,
            Title = "Whispers in the Ironwood",
            Slug = "whispers-in-the-ironwood",
            IssueNumber = 1,
            Synopsis =
                "Long before he was known as the terror of the pit, Kongor guarded his family in the high branches. When young Ophelia slips away from her sanctuary village into the deep woods, an unexpected friendship is born—one that will echo across the shattered cosmos.",
            CoverImageUrl = "/images/lore/comics/issue-1/cover.webp",
            CinematicVideoUrl = null,
            IsPublished = true,
            ViewCount = 142,
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampPublished = TimeProvider.System.GetUtcNow()
        };

        await context.LoreComics.AddAsync(comic1, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // Credits for Comic #1
        context.LoreCredits.AddRange(
            new LoreCredit
            {
                ComicID = comic1.ID, AccountID = adminAccount?.ID, Role = "Story & Narrative Lead",
                CustomNameOverride = "Project Kongor Lore Team", OrderIndex = 0
            },
            new LoreCredit
            {
                ComicID = comic1.ID, AccountID = null, Role = "Lead 3D Illustrator",
                CustomNameOverride = "Blender Cinematic Studio", OrderIndex = 1
            },
            new LoreCredit
            {
                ComicID = comic1.ID, AccountID = null, Role = "Colorist & Compositor",
                CustomNameOverride = "Kongor Visuals", OrderIndex = 2
            }
        );

        // Pages for Comic #1
        context.LorePages.AddRange(
            new LorePage
            {
                ComicID = comic1.ID,
                PageNumber = 1,
                ImageUrl = "/images/lore/comics/issue-1/page-1.webp",
                DialogueTranscript =
                    "Narrator: In the quiet mist of the Primeval Canopy, the world was young. Kongor gently watched over the sleeping troop from the high branches."
            },
            new LorePage
            {
                ComicID = comic1.ID,
                PageNumber = 2,
                ImageUrl = "/images/lore/comics/issue-1/page-2.webp",
                DialogueTranscript =
                    "Narrator: Far from the rigid stone sanctums of her people, young Ophelia wandered where the elders strictly forbade."
            },
            new LorePage
            {
                ComicID = comic1.ID,
                PageNumber = 3,
                ImageUrl = "/images/lore/comics/issue-1/page-3.webp",
                DialogueTranscript =
                    "Ophelia: 'You're not a monster like the scrolls say... you're peaceful.' Kongor extended a giant hand, offering a ripe sun-fig."
            },
            new LorePage
            {
                ComicID = comic1.ID,
                PageNumber = 4,
                ImageUrl = "/images/lore/comics/issue-1/page-4.webp",
                DialogueTranscript =
                    "Narrator: As dusk painted the treetops gold, an unspoken vow was forged between the forest guardian and the child of the enclave."
            }
        );

        // Comic #2: Echoes on the Perimeter
        LoreComic comic2 = new()
        {
            ArcID = arc1.ID,
            Title = "Echoes on the Perimeter",
            Slug = "echoes-on-the-perimeter",
            IssueNumber = 2,
            Synopsis =
                "Scout tracks strange metallic vibrations and runic energy leaks fracturing the forest boundary. Meanwhile, Ophelia discovers a dormant obsidian monolith pulsating with dark crimson resonance near Kongor’s sacred watering pool. Kongor senses the disturbance, protecting his kin.",
            CoverImageUrl = "/images/lore/comics/issue-2/cover.webp",
            CinematicVideoUrl = null,
            IsPublished = true,
            ViewCount = 89,
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampPublished = TimeProvider.System.GetUtcNow()
        };

        await context.LoreComics.AddAsync(comic2, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // Credits for Comic #2
        context.LoreCredits.AddRange(
            new LoreCredit
            {
                ComicID = comic2.ID, AccountID = adminAccount?.ID, Role = "Story & Lore Lead",
                CustomNameOverride = "Project Kongor Lore Team", OrderIndex = 0
            },
            new LoreCredit
            {
                ComicID = comic2.ID, AccountID = null, Role = "Pencils & Rigs",
                CustomNameOverride = "Blender Cinematic Studio", OrderIndex = 1
            },
            new LoreCredit
            {
                ComicID = comic2.ID, AccountID = null, Role = "Lighting Director",
                CustomNameOverride = "EEVEE Next Studio", OrderIndex = 2
            }
        );

        // Pages for Comic #2
        context.LorePages.AddRange(
            new LorePage
            {
                ComicID = comic2.ID,
                PageNumber = 1,
                ImageUrl = "/images/lore/comics/issue-2/page-1.webp",
                DialogueTranscript =
                    "Scout: 'The ward-lines are severed... clean cuts. Something artificial entered the outer grove.'"
            },
            new LorePage
            {
                ComicID = comic2.ID,
                PageNumber = 2,
                ImageUrl = "/images/lore/comics/issue-2/page-2.webp",
                DialogueTranscript =
                    "Ophelia: 'Kongor, look at the water pool... the stones are glowing with a deep red light.'"
            },
            new LorePage
            {
                ComicID = comic2.ID,
                PageNumber = 3,
                ImageUrl = "/images/lore/comics/issue-2/page-3.webp",
                DialogueTranscript =
                    "Narrator: Kongor stood tall, his gentle eyes narrowing as a low, resonant rumble echoed in his chest. He could smell the ozone of unnatural power."
            },
            new LorePage
            {
                ComicID = comic2.ID,
                PageNumber = 4,
                ImageUrl = "/images/lore/comics/issue-2/page-4.webp",
                DialogueTranscript =
                    "Narrator: The shadows lengthened. In the brush, twin amber eyes glinted behind jagged obsidian armor."
            }
        );

        // Comic #3: The Shattered Dawn
        LoreComic comic3 = new()
        {
            ArcID = arc1.ID,
            Title = "The Shattered Dawn",
            Slug = "the-shattered-dawn",
            IssueNumber = 3,
            Synopsis =
                "A rogue vanguard of corrupted hunters and Predator track the crimson resonance into the deep grove. As the hunters trap Ophelia and threaten Kongor's troop, Kongor unleashes a ferocious, primal roar, awakening the ancient primordial fury buried within his bloodline to defend his friends.",
            CoverImageUrl = "/images/lore/comics/issue-3/cover.webp",
            CinematicVideoUrl = null,
            IsPublished = true,
            ViewCount = 64,
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampPublished = TimeProvider.System.GetUtcNow()
        };

        await context.LoreComics.AddAsync(comic3, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // Credits for Comic #3
        context.LoreCredits.AddRange(
            new LoreCredit
            {
                ComicID = comic3.ID, AccountID = adminAccount?.ID, Role = "Story & Script Lead",
                CustomNameOverride = "Project Kongor Lore Team", OrderIndex = 0
            },
            new LoreCredit
            {
                ComicID = comic3.ID, AccountID = null, Role = "Lead Action Choreographer",
                CustomNameOverride = "Blender Cinematic Studio", OrderIndex = 1
            },
            new LoreCredit
            {
                ComicID = comic3.ID, AccountID = null, Role = "Sound & VFX Design",
                CustomNameOverride = "Kongor Audio Works", OrderIndex = 2
            }
        );

        // Pages for Comic #3
        context.LorePages.AddRange(
            new LorePage
            {
                ComicID = comic3.ID,
                PageNumber = 1,
                ImageUrl = "/images/lore/comics/issue-3/page-1.webp",
                DialogueTranscript =
                    "Narrator: Metal chains hissed through the air. The beast-hunters struck without warning, enclosing the sacred clearing in electrified iron."
            },
            new LorePage
            {
                ComicID = comic3.ID,
                PageNumber = 2,
                ImageUrl = "/images/lore/comics/issue-3/page-2.webp",
                DialogueTranscript =
                    "Ophelia: 'Kongor! Run! They came for the monolith energy!' Predator: 'Target acquired. Capture the beast.'"
            },
            new LorePage
            {
                ComicID = comic3.ID,
                PageNumber = 3,
                ImageUrl = "/images/lore/comics/issue-3/page-3.webp",
                DialogueTranscript =
                    "ROOOOOAAAAR! The gentle giant vanished. An earth-shattering shockwave tore through the trees as primordial crimson energy surged through Kongor's veins!"
            },
            new LorePage
            {
                ComicID = comic3.ID,
                PageNumber = 4,
                ImageUrl = "/images/lore/comics/issue-3/page-4.webp",
                DialogueTranscript =
                    "Narrator: The gentle days of the Ironwood canopy were over. The legend of Kongor had begun."
            }
        );

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "[SeedLoreComics] Successfully seeded 1 Arc and 3 Comic Issues with 12 pages and credits.");
    }

    public static async Task SeedMatchStatisticsAndRankings(MerrickContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
#if PLAYTEST_BUILD
        _ = context;
        _ = cancellationToken;
        logger.LogInformation("[Bootstrap] SeedMatchStatisticsAndRankings skipped in playtest build");
        await Task.CompletedTask;
        return;
#endif
#if !PLAYTEST_BUILD
        if (await context.AccountStatistics.AnyAsync(cancellationToken) &&
            await context.MatchStatistics.AnyAsync(cancellationToken))
        {
            logger.LogInformation("[SeedMatchStatisticsAndRankings] Statistics and matches already exist in database.");
            return;
        }

        List<Account> accounts = await context.Accounts
            .Include(a => a.Clan)
            .Where(a => a.Type != AccountType.ServerHost)
            .Take(100)
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            logger.LogWarning("[SeedMatchStatisticsAndRankings] No accounts found to seed statistics for.");
            return;
        }

        logger.LogInformation(
            "[SeedMatchStatisticsAndRankings] Seeding initial AccountStatistics and MatchStatistics for {Count} accounts...",
            accounts.Count);

        Random random = new(42);

        // 1. Seed AccountStatistics for each account
        if (!await context.AccountStatistics.AnyAsync(cancellationToken))
        {
            foreach (Account acc in accounts)
            {
                int mmPlayed = random.Next(15, 120);
                int mmWon = (int)(mmPlayed * (0.45 + (random.NextDouble() * 0.35)));
                int mmLost = mmPlayed - mmWon;
                double rating = 1400.0 + (random.NextDouble() * 650.0);

                AccountStatistics mmStats = new()
                {
                    Account = acc,
                    AccountID = acc.ID,
                    Type = AccountStatisticsType.Matchmaking,
                    MatchesPlayed = mmPlayed,
                    MatchesWon = mmWon,
                    MatchesLost = mmLost,
                    MatchesConceded = random.Next(0, 5),
                    MatchesDisconnected = random.Next(0, 3),
                    MatchesKicked = 0,
                    SkillRating = rating,
                    PerformanceScore = 3.5 + (random.NextDouble() * 2.0),
                    PlacementMatchesData = "{}"
                };
                context.AccountStatistics.Add(mmStats);

                int pubPlayed = random.Next(5, 50);
                int pubWon = (int)(pubPlayed * (0.40 + (random.NextDouble() * 0.30)));
                int pubLost = pubPlayed - pubWon;

                AccountStatistics pubStats = new()
                {
                    Account = acc,
                    AccountID = acc.ID,
                    Type = AccountStatisticsType.Public,
                    MatchesPlayed = pubPlayed,
                    MatchesWon = pubWon,
                    MatchesLost = pubLost,
                    MatchesConceded = random.Next(0, 2),
                    MatchesDisconnected = random.Next(0, 1),
                    MatchesKicked = 0,
                    SkillRating = 1450.0 + (random.NextDouble() * 450.0),
                    PerformanceScore = 3.0 + (random.NextDouble() * 1.8),
                    PlacementMatchesData = "{}"
                };
                context.AccountStatistics.Add(pubStats);

                int midPlayed = random.Next(10, 80);
                int midWon = (int)(midPlayed * (0.50 + (random.NextDouble() * 0.25)));
                int midLost = midPlayed - midWon;
                double midRating = 1420.0 + (random.NextDouble() * 600.0);

                AccountStatistics midStats = new()
                {
                    Account = acc,
                    AccountID = acc.ID,
                    Type = AccountStatisticsType.MidWars,
                    MatchesPlayed = midPlayed,
                    MatchesWon = midWon,
                    MatchesLost = midLost,
                    MatchesConceded = random.Next(0, 3),
                    MatchesDisconnected = random.Next(0, 2),
                    MatchesKicked = 0,
                    SkillRating = midRating,
                    PerformanceScore = 3.2 + (random.NextDouble() * 2.2),
                    PlacementMatchesData = "{}"
                };
                context.AccountStatistics.Add(midStats);
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        // 2. Seed Sample Matches if none exist
        if (!await context.MatchStatistics.AnyAsync(cancellationToken))
        {
            string[] heroList =
            [
                "Hero_Accursed", "Hero_Predator", "Hero_Hammerstorm", "Hero_Behemoth", "Hero_Panda",
                "Hero_Armadon", "Hero_Jeraziah", "Hero_Rampage", "Hero_Tundra", "Hero_Gladiator",
                "Hero_Bubbles", "Hero_Pyromancer", "Hero_WitchSlayer", "Hero_Valkyrie", "Hero_Scout",
                "Hero_Mageblight", "Hero_Devourer", "Hero_FlintBeastwood", "Hero_Soulstealer", "Hero_Chronos"
            ];

            string[] itemPool =
            [
                "Item_LoggersHatchet", "Item_IronShield", "Item_Marchers", "Item_Steamboots",
                "Item_EnhancedMarchers", "Item_PostHaste", "Item_PortalKey", "Item_Bottle",
                "Item_BehemothsHeart", "Item_DaemonicBreastplate", "Item_Immunity", "Item_Sheepstick",
                "Item_RestorationStone", "Item_SavageMace", "Item_Wingbow", "Item_Dawnbringer"
            ];

            (string Map, string Mode, bool IsRanked)[] scenarios =
            [
                ("caldavar", "picking", true),
                ("caldavar", "picking", true),
                ("caldavar", "sd", true),
                ("caldavar", "casual", false),
                ("caldavar", "cm", true),
                ("midwars", "midwars", true),
                ("midwars", "midwars", false),
                ("grimmscrossing", "picking", false),
                ("riftwars", "riftwars", false),
                ("watchtower", "picking", false)
            ];

            for (int m = 0; m < scenarios.Length; m++)
            {
                int matchId = 10001 + m;
                (string Map, string Mode, bool IsRanked) sc = scenarios[m];
                int timePlayed = random.Next(900, 2400);

                MatchStatistics match = new()
                {
                    MatchID = matchId,
                    ServerID = 1,
                    HostAccountName = "guccihost",
                    Map = sc.Map,
                    MapVersion = "1.0",
                    TimePlayed = timePlayed,
                    FileSize = 1024 * random.Next(300, 1200),
                    FileName = $"M{matchId}.honreplay",
                    ReplayUrl = $"https://replays.projectkongor.com/M{matchId}.honreplay",
                    VideoUrl = null,
                    ConnectionState = 0,
                    Version = "4.10.1",
                    AveragePSR = 1500 + random.Next(-50, 150),
                    AveragePSRTeamOne = 1500 + random.Next(-50, 150),
                    AveragePSRTeamTwo = 1500 + random.Next(-50, 150),
                    GameMode = sc.Mode,
                    ScoreTeam1 = random.Next(15, 45),
                    ScoreTeam2 = random.Next(15, 45),
                    TeamScoreGoal = 0,
                    PlayerScoreGoal = 0,
                    NumberOfRounds = 1,
                    ReleaseStage = "RELEASE",
                    BannedHeroes = "",
                    TimestampRecorded = TimeProvider.System.GetUtcNow().AddHours(-1 * (m * 4 + 1)),
                    AwardMostAnnihilations = -1,
                    AwardMostQuadKills = -1,
                    AwardLargestKillStreak = -1,
                    AwardMostSmackdowns = -1,
                    AwardMostKills = -1,
                    AwardMostAssists = -1,
                    AwardLeastDeaths = -1,
                    AwardMostBuildingDamage = -1,
                    AwardMostWardsKilled = -1,
                    AwardMostHeroDamageDealt = -1,
                    AwardHighestCreepScore = -1
                };

                context.MatchStatistics.Add(match);

                bool legionWon = match.ScoreTeam1 > match.ScoreTeam2;

                for (int p = 0; p < Math.Min(10, accounts.Count); p++)
                {
                    Account acc = accounts[p];
                    int team = p < 5 ? 1 : 2;
                    bool won = (team == 1 && legionWon) || (team == 2 && !legionWon);
                    string hero = heroList[(p + m * 3) % heroList.Length];

                    int kills = random.Next(0, 16);
                    int deaths = random.Next(0, 12);
                    int assists = random.Next(1, 20);
                    int creepKills = random.Next(30, 250);
                    int heroDamage = kills * 1500 + assists * 600 + random.Next(3000, 15000);
                    int gold = creepKills * 45 + kills * 300 + assists * 150 + random.Next(2000, 6000);

                    List<string> inv = [.. itemPool.OrderBy(_ => random.Next()).Take(random.Next(3, 7))];

                    PlayerStatistics ps = new()
                    {
                        MatchID = matchId,
                        AccountID = acc.ID,
                        AccountName = acc.Name,
                        ClanID = acc.Clan?.ID,
                        ClanTag = acc.Clan?.Tag,
                        Team = team,
                        LobbyPosition = (p % 5) + 1,
                        GroupNumber = 0,
                        Benefit = 0,
                        HeroProductID = (uint)(100 + (p % 50)),
                        HeroIdentifier = hero,
                        Inventory = inv,
                        Win = won ? 1 : 0,
                        Loss = won ? 0 : 1,
                        MVP = p == 0 && won ? 1 : 0,
                        Disconnected = 0,
                        Conceded = 0,
                        Kicked = 0,
                        PublicMatch = sc.IsRanked ? 0 : 1,
                        PublicSkillRatingChange = sc.IsRanked ? 0 : (won ? 5.0 : -5.0),
                        RankedMatch = sc.IsRanked ? 1 : 0,
                        RankedSkillRatingChange = sc.IsRanked ? (won ? 7.5 : -7.5) : 0,
                        SocialBonus = 0,
                        UsedToken = 0,
                        ConcedeVotes = 0,
                        HeroKills = kills,
                        HeroDeaths = deaths,
                        HeroAssists = assists,
                        HeroDamage = heroDamage,
                        GoldFromHeroKills = kills * 250,
                        HeroExperience = timePlayed * 10,
                        Buybacks = random.Next(0, 2),
                        GoldLostToDeath = deaths * 100,
                        SecondsDead = deaths * 25,
                        TeamCreepKills = creepKills,
                        TeamCreepDamage = creepKills * 100,
                        TeamCreepGold = creepKills * 40,
                        TeamCreepExperience = creepKills * 50,
                        NeutralCreepKills = random.Next(0, 50),
                        NeutralCreepDamage = random.Next(0, 5000),
                        NeutralCreepGold = random.Next(0, 1500),
                        NeutralCreepExperience = random.Next(0, 2000),
                        BuildingDamage = random.Next(500, 6000),
                        BuildingsRazed = random.Next(0, 3),
                        ExperienceFromBuildings = random.Next(500, 2000),
                        GoldFromBuildings = random.Next(400, 1500),
                        Denies = random.Next(0, 20),
                        ExperienceDenied = random.Next(0, 1000),
                        Gold = gold,
                        GoldSpent = (int)(gold * 0.85),
                        Experience = timePlayed * 12,
                        Actions = timePlayed * 3,
                        SecondsPlayed = timePlayed,
                        HeroLevel = Math.Min(25, 6 + (timePlayed / 90)),
                        ConsumablesPurchased = random.Next(1, 6),
                        WardsPlaced = random.Next(0, 8),
                        FirstBlood = (p == 0) ? 1 : 0,
                        DoubleKill = kills >= 4 ? 1 : 0,
                        TripleKill = kills >= 7 ? 1 : 0,
                        QuadKill = kills >= 10 ? 1 : 0,
                        Annihilation = kills >= 14 ? 1 : 0,
                        KillStreak03 = kills >= 3 ? 1 : 0,
                        KillStreak04 = kills >= 4 ? 1 : 0,
                        KillStreak05 = kills >= 5 ? 1 : 0,
                        KillStreak06 = kills >= 6 ? 1 : 0,
                        KillStreak07 = kills >= 7 ? 1 : 0,
                        KillStreak08 = kills >= 8 ? 1 : 0,
                        KillStreak09 = kills >= 9 ? 1 : 0,
                        KillStreak10 = kills >= 10 ? 1 : 0,
                        KillStreak15 = kills >= 15 ? 1 : 0,
                        Smackdown = random.Next(0, 3),
                        Humiliation = random.Next(0, 2),
                        Nemesis = random.Next(0, 3),
                        Retribution = random.Next(0, 2),
                        Score = kills * 100 + assists * 50 - deaths * 30,
                        GameplayStat0 = random.Next(0, 5000),
                        GameplayStat1 = random.Next(5000, 30000),
                        GameplayStat2 = random.Next(0, 60),
                        GameplayStat3 = random.Next(0, 20),
                        GameplayStat4 = random.Next(0, 3000),
                        GameplayStat5 = 0,
                        GameplayStat6 = random.Next(0, 8),
                        GameplayStat7 = random.Next(5, 50),
                        GameplayStat8 = 0,
                        GameplayStat9 = 0,
                        TimeEarningExperience = (int)(timePlayed * 0.9)
                    };

                    context.PlayerStatistics.Add(ps);
                }
            }

            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[SeedMatchStatisticsAndRankings] Successfully seeded match statistics and player scoreboards.");
        }
#endif
    }

    public static async Task SeedStoreItems(MerrickContext context, ILogger logger, CancellationToken cancellationToken)
    {
        if (await context.StoreItems.AnyAsync(cancellationToken))
        {
            logger.LogInformation("[SeedStoreItems] Store items already exist in database, skipping.");
            return;
        }

        logger.LogInformation("[SeedStoreItems] Seeding baseline store items catalog...");

        List<StoreItem> baselineItems =
        [
            // Account Icons
            new StoreItem
            {
                Name = "Default Icon", Code = "ai.Default Icon", LocalContent = "default_icon", Price = 0,
                PriceInMMP = 0, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Custom Account Icon", Code = "ai.custom_icon:1", LocalContent = "custom_icon_1", Price = 100,
                PriceInMMP = 500, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Silver Shield Icon", Code = "ai.silver_shield", LocalContent = "silver_shield", Price = 150,
                PriceInMMP = 750, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Gold Crown Icon", Code = "ai.gold_crown", LocalContent = "gold_crown", Price = 300,
                PriceInMMP = 1500, Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },

            // Chat Colors
            new StoreItem
            {
                Name = "White Chat Color", Code = "cc.white", LocalContent = "white_chat", Price = 0, PriceInMMP = 0,
                Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Frostburn Logo Chat Color", Code = "cc.frostburnlogo", LocalContent = "frostburn_chat",
                Price = 250, PriceInMMP = 1500, Purchasable = true, Enabled = true, Premium = false, IsBundle = false,
                UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Gold Chat Color", Code = "cc.gold", LocalContent = "gold_chat", Price = 500, PriceInMMP = 3000,
                Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Ruby Chat Color", Code = "cc.ruby", LocalContent = "ruby_chat", Price = 350, PriceInMMP = 2000,
                Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Emerald Chat Color", Code = "cc.emerald", LocalContent = "emerald_chat", Price = 350,
                PriceInMMP = 2000, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },

            // Clan Shields
            new StoreItem
            {
                Name = "Frostburn Logo Clan Shield", Code = "cs.frostburnlogo", LocalContent = "frostburn_shield",
                Price = 300, PriceInMMP = 1800, Purchasable = true, Enabled = true, Premium = false, IsBundle = false,
                UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Kongor Skull Clan Shield", Code = "cs.kongor_skull", LocalContent = "kongor_shield",
                Price = 400, PriceInMMP = 2200, Purchasable = true, Enabled = true, Premium = false, IsBundle = false,
                UpgradeId = 0, UpgradeType = 0
            },

            // Taunts
            new StoreItem
            {
                Name = "Standard Taunt", Code = "t.Standard", LocalContent = "standard_taunt", Price = 0,
                PriceInMMP = 0, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Dumpster Taunt", Code = "t.Dumpster_Taunt", LocalContent = "dumpster_taunt", Price = 650,
                PriceInMMP = 3800, Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Super Taunt", Code = "m.Super-Taunt", LocalContent = "super_taunt", Price = 600,
                PriceInMMP = 3500, Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Baby Taunt", Code = "t.Baby_Taunt", LocalContent = "baby_taunt", Price = 550, PriceInMMP = 3200,
                Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Gore Taunt", Code = "t.Gore_Taunt", LocalContent = "gore_taunt", Price = 700, PriceInMMP = 4000,
                Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },

            // Announcers
            new StoreItem
            {
                Name = "Flamboyant Announcer", Code = "av.Flamboyant", LocalContent = "flamboyant_pack", Price = 500,
                PriceInMMP = 3000, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Badass Announcer", Code = "av.Badass", LocalContent = "badass_pack", Price = 500,
                PriceInMMP = 3000, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "English Gentleman Announcer", Code = "av.EnglishGentleman",
                LocalContent = "english_gentleman_pack", Price = 450, PriceInMMP = 2800, Purchasable = true,
                Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Pimp Announcer", Code = "av.Pimp", LocalContent = "pimp_pack", Price = 550, PriceInMMP = 3200,
                Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "BreakyCPK Announcer", Code = "av.BreakyCPK", LocalContent = "breakycpk_pack", Price = 600,
                PriceInMMP = 3500, Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },

            // Couriers
            new StoreItem
            {
                Name = "Cat Courier", Code = "c.cat_courier", LocalContent = "cat_courier", Price = 400,
                PriceInMMP = 2500, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Baby Kongor Courier", Code = "c.kongor_baby", LocalContent = "kongor_baby_courier", Price = 600,
                PriceInMMP = 3600, Purchasable = true, Enabled = true, Premium = true, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Flying Pig Courier", Code = "c.flying_pig", LocalContent = "flying_pig_courier", Price = 350,
                PriceInMMP = 2000, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },

            // Wards
            new StoreItem
            {
                Name = "8-Bit Ward", Code = "w.8bit_ward", LocalContent = "8bit_ward", Price = 300, PriceInMMP = 1800,
                Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Eye of Newt Ward", Code = "w.eye_of_newt", LocalContent = "eye_of_newt_ward", Price = 250,
                PriceInMMP = 1500, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },

            // Teleport Effects & Upgrades
            new StoreItem
            {
                Name = "Punk TP Effect", Code = "te.Punk TP", LocalContent = "punk_tp_effect", Price = 350,
                PriceInMMP = 2000, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Paragon Circle Upgrade", Code = "sc.paragon_circle_upgrade", LocalContent = "paragon_circle",
                Price = 500, PriceInMMP = 3000, Purchasable = true, Enabled = true, Premium = false, IsBundle = false,
                UpgradeId = 0, UpgradeType = 0
            },
            new StoreItem
            {
                Name = "Punk Creep Skin", Code = "cr.Punk Creep", LocalContent = "punk_creep", Price = 450,
                PriceInMMP = 2700, Purchasable = true, Enabled = true, Premium = false, IsBundle = false, UpgradeId = 0,
                UpgradeType = 0
            }
        ];

        await context.StoreItems.AddRangeAsync(baselineItems, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[SeedStoreItems] Successfully persisted {Count} store items to catalog.",
            baselineItems.Count);
    }
}
