// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Tests.Infrastructure;
using DAWNBRINGER.WebPortal.UI;
using DAWNBRINGER.WebPortal.UI.Services;
using DAWNBRINGER.WebPortal.UI.Services.ApiClient;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Clients;
using StackExchange.Redis;
using Xunit;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[CollectionDefinition("DawnbringerE2E")]
public class DawnbringerE2ECollection : ICollectionFixture<DawnbringerE2ETestFixture>
{
}

public class DawnbringerE2ETestFixture : WebApplicationFactory<IDawnBringerAssemblyMarker>
{
    private readonly string _dbName = "DawnbringerE2E_" + Guid.NewGuid().ToString("N");
    private readonly string _dataProtectionDir = Path.Combine(Path.GetTempPath(), $"dawnbringer-dp-{Guid.NewGuid():N}");
    private MerrickContext? _context;

    public MerrickContext DbContext => _context ??= CreateAndSeedContext();

    private MerrickContext CreateAndSeedContext()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(_dbName);
        SeedDatabase(db);
        return db;
    }

    private static void SeedDatabase(MerrickContext db)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        // 1. Roles
        Role adminRole = new() { ID = 1, Name = global::NEXUS.Contracts.Constants.UserRoles.Administrator };
        Role userRole = new() { ID = 2, Name = "User" };
        db.Roles.AddRange(adminRole, userRole);

        // 2. Users & Accounts
        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "guccidonut@kongor.net",
            DiscordId = "test-discord-guccidonut",
            DiscordUsername = "guccidonut",
            Role = adminRole,
            GoldCoins = 5000,
            SilverCoins = 10000,
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
        };

        Account adminAccount = new()
        {
            ID = 1,
            Name = "guccidonut",
            IsMain = true,
            Type = AccountType.Staff,
            TimestampCreated = now.AddDays(-180).UtcDateTime,
            User = adminUser,
            UserID = adminUser.ID
        };
        adminUser.Accounts = [adminAccount];

        User normalUser = new()
        {
            ID = 2,
            EmailAddress = "normal@kongor.net",
            DiscordId = "test-discord-normal",
            DiscordUsername = "normalplayer",
            Role = userRole,
            GoldCoins = 100,
            SilverCoins = 250,
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
        };

        Account normalAccount = new()
        {
            ID = 2,
            Name = "normalplayer",
            IsMain = true,
            Type = AccountType.Normal,
            TimestampCreated = now.AddDays(-60).UtcDateTime,
            User = normalUser,
            UserID = normalUser.ID
        };
        normalUser.Accounts = [normalAccount];

        User bannedUser = new()
        {
            ID = 3,
            EmailAddress = "banned@kongor.net",
            DiscordId = "test-discord-banned",
            DiscordUsername = "bannedplayer",
            Role = userRole,
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
        };

        Account bannedAccount = new()
        {
            ID = 3,
            Name = "bannedplayer",
            IsMain = true,
            Type = AccountType.Disabled,
            BanReason = "Disciplinary suspension for automation",
            BanExpiration = now.AddDays(30).UtcDateTime,
            TimestampCreated = now.AddDays(-30).UtcDateTime,
            User = bannedUser,
            UserID = bannedUser.ID
        };
        bannedUser.Accounts = [bannedAccount];

        User testAdminUser = new()
        {
            ID = 99,
            EmailAddress = "test-admin@kongor.net",
            DiscordId = "test-admin",
            DiscordUsername = "staff_admin",
            Role = adminRole,
            GoldCoins = 5000,
            SilverCoins = 10000,
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
        };

        Account testAdminAccount = new()
        {
            ID = 99,
            Name = "staff_admin",
            IsMain = true,
            Type = AccountType.Staff,
            TimestampCreated = now.AddDays(-180).UtcDateTime,
            User = testAdminUser,
            UserID = testAdminUser.ID
        };
        testAdminUser.Accounts = [testAdminAccount];

        db.Users.AddRange(adminUser, normalUser, bannedUser, testAdminUser);
        db.Accounts.AddRange(adminAccount, normalAccount, bannedAccount, testAdminAccount);

        // 3. Combat & Account Statistics
        db.AccountStatistics.Add(new AccountStatistics
        {
            AccountID = adminAccount.ID,
            Type = AccountStatisticsType.Public,
            MatchesPlayed = 100,
            MatchesWon = 65,
            MatchesLost = 35,
            SkillRating = 1750
        });

        db.AccountStatistics.Add(new AccountStatistics
        {
            AccountID = normalAccount.ID,
            Type = AccountStatisticsType.Public,
            MatchesPlayed = 10,
            MatchesWon = 5,
            MatchesLost = 5,
            SkillRating = 1500
        });

        // Seed 10 Match records for guccidonut
        for (int i = 1; i <= 10; i++)
        {
            db.PlayerStatistics.Add(new PlayerStatistics
            {
                MatchID = 10000 + i,
                AccountID = adminAccount.ID,
                AccountName = adminAccount.Name,
                Team = 1,
                HeroIdentifier = "Hero_Vindicator",
                Win = i <= 7 ? 1 : 0,
                Loss = i <= 7 ? 0 : 1,
                PublicMatch = 1,
                PublicSkillRatingChange = 1750,
                HeroKills = 8 + i,
                HeroDeaths = 2,
                HeroAssists = 10,
                HeroDamage = 20000 + i * 1000,
                Inventory = ["Item_LoggersHatchet", "Item_Marchers"]
            });

            db.MatchStatistics.Add(new MatchStatistics
            {
                MatchID = 10000 + i,
                Map = "caldavar",
                TimestampRecorded = now.AddDays(-i),
                TimePlayed = 1800,
                HostAccountName = adminAccount.Name,
                FileName = $"hon_match_{10000 + i}.cor"
            });
        }

        // 4. Hero Details (Roster)
        db.HeroDetails.AddRange(
            new HeroDetail
            {
                HeroCode = "Hero_Vindicator",
                DisplayName = "Vindicator",
                PrimaryAttribute = "Intelligence",
                AttackType = "Ranged",
                Lore = "A master of arcane retribution who silences adversaries.",
                IconPath = "/heroes/Hero_Vindicator/icon.tga",
                SplashImagePath = "/heroes/Hero_Vindicator/splash.jpg",
                AbilitiesJson = "[]"
            },
            new HeroDetail
            {
                HeroCode = "Hero_Devourer",
                DisplayName = "Devourer",
                PrimaryAttribute = "Strength",
                AttackType = "Melee",
                Lore = "A ravenous abomination who pulls foes with his butcher hook.",
                IconPath = "/heroes/Hero_Devourer/icon.tga",
                SplashImagePath = "/heroes/Hero_Devourer/splash.jpg",
                AbilitiesJson = "[]"
            },
            new HeroDetail
            {
                HeroCode = "Hero_WitchSlayer",
                DisplayName = "Witch Slayer",
                PrimaryAttribute = "Intelligence",
                AttackType = "Ranged",
                Lore = "An inquisitor wielding silver bullets and unyielding fury.",
                IconPath = "/heroes/Hero_WitchSlayer/icon.tga",
                SplashImagePath = "/heroes/Hero_WitchSlayer/splash.jpg",
                AbilitiesJson = "[]"
            }
        );

        // 5. Items (Compendium)
        db.HeroItems.AddRange(
            new HeroItem
            {
                Code = "Item_LoggersHatchet",
                Name = "Logger's Hatchet",
                Cost = 225,
                Description = "Provides bonus attack damage against enemy and neutral creeps.",
                Flavor = "A standard forestry tool sharpened for combat.",
                IconPath = "/items/hatchet.tga",
                Category = "Consumables",
                IsActive = true
            },
            new HeroItem
            {
                Code = "Item_Marchers",
                Name = "Marchers",
                Cost = 500,
                Description = "Increases Movement Speed by 50.",
                Flavor = "Simple leather footwear.",
                IconPath = "/items/marchers.tga",
                Category = "Relics",
                IsActive = true
            },
            new HeroItem
            {
                Code = "Item_PortalKey",
                Name = "Portal Key",
                Cost = 2150,
                Description = "Allows instant teleportation up to 1200 units away.",
                Flavor = "An ancient arcane stone that bends space.",
                IconPath = "/items/portalkey.tga",
                Category = "Relics",
                IsActive = true
            },
            new HeroItem
            {
                Code = "lifetube",
                Name = "Lifetube",
                Cost = 825,
                Description = "Grants +5 Health Regeneration.",
                Flavor = "Restorative elixir tube.",
                IconPath = "/images/items/lifetube.png",
                Category = "Accessories",
                IsActive = true
            },
            new HeroItem
            {
                Code = "manatube",
                Name = "Manatube",
                Cost = 825,
                Description = "Grants +100% Mana Regeneration.",
                Flavor = "Arcane mana conduit.",
                IconPath = "/images/items/manatube.png",
                Category = "Accessories",
                IsActive = true
            },
            new HeroItem
            {
                Code = "sustainer",
                Name = "Sustainer",
                Cost = 1750,
                Description = "Grants +6 Health Regen and +150% Mana Regen.",
                Flavor = "Twin-vial ornament.",
                IconPath = "/images/items/sustainer.png",
                Category = "Initiation",
                IsActive = true,
                Components =
                [
                    new HeroItemComponent { ComponentItemCode = "lifetube", Quantity = 1 },
                    new HeroItemComponent { ComponentItemCode = "manatube", Quantity = 1 }
                ]
            },
            new HeroItem
            {
                Code = "nullstone",
                Name = "Null Stone",
                Cost = 4700,
                Description = "Blocks single-target enemy spells every 20 seconds.",
                Flavor = "Protective warded gem.",
                IconPath = "/images/items/nullstone.png",
                Category = "Protective",
                IsActive = true,
                Components =
                [
                    new HeroItemComponent { ComponentItemCode = "sustainer", Quantity = 1 }
                ]
            }
        );

        // 6. Guides
        db.HeroGuides.AddRange(
            new HeroGuide
            {
                ID = 1,
                Name = "Competitive Vindicator Mid/Carry",
                HeroName = "Vindicator",
                HeroIdentifier = "Hero_Vindicator",
                Intro = "Strategic guide for dominating early lane and teamfights.",
                Content = "Focus on positioning, silences, and pure damage itemization.",
                Public = true,
                Featured = true,
                Rating = 4.9f,
                AuthorID = adminAccount.ID,
                Author = adminAccount
            },
            new HeroGuide
            {
                ID = 2,
                Name = "Private Devourer Solo Hook Guide",
                HeroName = "Devourer",
                HeroIdentifier = "Hero_Devourer",
                Intro = "Unpublished team draft notes for Devourer angles.",
                Content = "Hook from fog of war behind tier 1 towers.",
                Public = false,
                Featured = false,
                Rating = 3.5f,
                AuthorID = adminAccount.ID,
                Author = adminAccount
            }
        );

        // 7. Store Items
        db.StoreItems.AddRange(
            new StoreItem
            {
                Id = 1,
                Name = "BreakyCPK Announcer",
                Code = "announcer_breaky",
                UpgradeId = 1,
                Price = 500,
                PriceInMMP = 0,
                Purchasable = true,
                Enabled = true,
                LocalContent = "BreakyCPK"
            },
            new StoreItem
            {
                Id = 2,
                Name = "Pirate Announcer",
                Code = "announcer_pirate",
                UpgradeId = 2,
                Price = 600,
                PriceInMMP = 0,
                Purchasable = true,
                Enabled = true,
                LocalContent = "Pirate"
            },
            new StoreItem
            {
                Id = 3,
                Name = "Samuel L. Jackson Announcer",
                Code = "announcer_samuel",
                UpgradeId = 3,
                Price = 700,
                PriceInMMP = 0,
                Purchasable = true,
                Enabled = true,
                LocalContent = "Samuel"
            },
            new StoreItem
            {
                Id = 4,
                Name = "Plinko 5-Pack Tickets",
                Code = "plinko_ticket_5",
                UpgradeId = 4,
                Price = 0,
                PriceInMMP = 500,
                Purchasable = true,
                Enabled = true,
                LocalContent = "Plinko Tickets"
            }
        );

        db.SaveChanges();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataProtectionDir);

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });

        builder.UseEnvironment("Development");
        builder.UseSetting("IS_TEST_ENVIRONMENT", "true");
        builder.UseSetting("ConnectionStrings:DISTRIBUTED-CACHE", string.Empty);
        builder.UseSetting("ConnectionStrings:MERRICK", string.Empty);
        builder.UseSetting("DATA_PROTECTION_KEYS_PATH", _dataProtectionDir);
        builder.UseSetting("Authentication:AllowMockDiscordAuth", "true");
        builder.UseSetting("LiveMatches:EnableSimulation", "false");
        builder.UseSetting("Authentication:Discord:ClientId", "test-discord-client-id");
        builder.UseSetting("Authentication:Discord:ClientSecret", "test-discord-client-secret");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IS_TEST_ENVIRONMENT"] = "true",
                ["ConnectionStrings:DISTRIBUTED-CACHE"] = string.Empty,
                ["ConnectionStrings:MERRICK"] = string.Empty,
                ["DATA_PROTECTION_KEYS_PATH"] = _dataProtectionDir
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Isolate from any local machine Redis/DB dependencies
            services.RemoveAll<IConnectionMultiplexer>();
            services.RemoveAll<IDatabase>();
            services.AddDataProtection()
                .SetApplicationName("DAWNBRINGER")
                .PersistKeysToFileSystem(new DirectoryInfo(_dataProtectionDir));

            services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                List<HealthCheckRegistration> toRemove =
                [
                    .. options.Registrations
                        .Where(r => r.Name.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
                                    r.Name.Contains("npgsql", StringComparison.OrdinalIgnoreCase) ||
                                    r.Name.Contains("sqlserver", StringComparison.OrdinalIgnoreCase) ||
                                    r.Name.Contains("Database", StringComparison.OrdinalIgnoreCase))
                ];
                foreach (HealthCheckRegistration reg in toRemove) options.Registrations.Remove(reg);
            });

            // Register our pre-seeded in-memory MerrickContext and API clients
            services.AddSingleton(DbContext);
            services.AddScoped<IUserApiClient>(_ => TestApiClientFactory.CreateUserApiClient(DbContext));
            services.AddScoped<IAdminApiClient>(_ => TestApiClientFactory.CreateAdminApiClient(DbContext));
            services.AddScoped<IPlayerStatsApiClient>(_ => TestApiClientFactory.CreatePlayerStatsApiClient(DbContext));
            services.AddScoped<IStoreApiClient>(_ => TestApiClientFactory.CreateStoreApiClient(DbContext));
            services.AddScoped<IGuideApiClient>(_ => TestApiClientFactory.CreateGuideApiClient(DbContext));
            services.AddScoped<ILoreApiClient, NullLoreApiClient>();
        });
    }

    public HttpClient CreateAnonymousClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true
        });
    }

    public HttpClient CreateNoRedirectClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string discordId = "test-discord-guccidonut",
        string discordUsername = "guccidonut")
    {
        HttpClient client = CreateAnonymousClient();
        string callbackUrl = $"/Account/DirectDiscordCallback?discordId={Uri.EscapeDataString(discordId)}&discordUsername={Uri.EscapeDataString(discordUsername)}";
        HttpResponseMessage authResponse = await client.GetAsync(callbackUrl);
        authResponse.EnsureSuccessStatusCode();
        return client;
    }

    public async Task<HttpClient> CreateAuthenticatedNoRedirectClientAsync(
        string discordId = "test-discord-guccidonut",
        string discordUsername = "guccidonut")
    {
        HttpClient client = CreateNoRedirectClient();
        string callbackUrl = $"/Account/DirectDiscordCallback?discordId={Uri.EscapeDataString(discordId)}&discordUsername={Uri.EscapeDataString(discordUsername)}";
        HttpResponseMessage authResponse = await client.GetAsync(callbackUrl);
        if ((int)authResponse.StatusCode >= 400)
        {
            authResponse.EnsureSuccessStatusCode();
        }
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _context?.Dispose();
            if (Directory.Exists(_dataProtectionDir))
            {
                try
                {
                    Directory.Delete(_dataProtectionDir, recursive: true);
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"[DawnbringerE2ETestFixture] Cleanup warning for '{_dataProtectionDir}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.Error.WriteLine($"[DawnbringerE2ETestFixture] Cleanup permission warning for '{_dataProtectionDir}': {ex.Message}");
                }
            }
        }
    }
}

internal class NullLoreApiClient : ILoreApiClient
{
    public Task<IReadOnlyList<LoreArcDto>> GetArcsAsync(bool includeUnpublished = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoreArcDto>>([]);

    public Task<LoreArcDto?> GetArcBySlugAsync(string slug, bool includeUnpublished = false, CancellationToken ct = default) =>
        Task.FromResult<LoreArcDto?>(null);

    public Task<IReadOnlyList<LoreComicSummaryDto>> GetComicsAsync(int? arcId = null, bool includeUnpublished = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoreComicSummaryDto>>([]);

    public Task<LoreComicDetailDto?> GetComicBySlugAsync(string slug, int? currentAccountId = null, bool isStaff = false, CancellationToken ct = default) =>
        Task.FromResult<LoreComicDetailDto?>(null);

    public Task<LoreComicDetailDto?> GetComicByIdAsync(int id, int? currentAccountId = null, bool isStaff = false, CancellationToken ct = default) =>
        Task.FromResult<LoreComicDetailDto?>(null);

    public Task<bool> RateComicAsync(int comicId, int accountId, int score, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<int?> GetUserRatingAsync(int comicId, int accountId, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public Task<LoreRatingMetricDto?> GetStaffRatingMetricsAsync(int comicId, CancellationToken ct = default) =>
        Task.FromResult<LoreRatingMetricDto?>(null);

    public Task<LoreComicSummaryDto?> SaveComicAsync(SaveComicRequestDto request, bool isStaff, CancellationToken ct = default) =>
        Task.FromResult<LoreComicSummaryDto?>(null);

    public Task<bool> DeleteComicAsync(int id, bool isStaff, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<LoreArcDto?> SaveArcAsync(SaveArcRequestDto request, bool isStaff, CancellationToken ct = default) =>
        Task.FromResult<LoreArcDto?>(null);

    public Task<bool> DeleteArcAsync(int id, bool isStaff, CancellationToken ct = default) =>
        Task.FromResult(true);
}
