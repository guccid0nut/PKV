// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using ASPIRE.Common.Communication;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Handlers;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class StandaloneBootstrapTests
{
    [Fact]
    public async Task EnsureStandaloneBootstrapAsync_ValidConfiguration_CreatesExpectedUsersAccountsAuditLogAndInvites()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KONGOR_STANDALONE"] = "true",
                ["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"] = "AdminSecret123!",
                ["KONGOR_BOOTSTRAP_HOST_PASSWORD"] = "HostSecret456!",
                ["KONGOR_STANDALONE_STARTER_GOLD"] = "10000",
                ["KONGOR_STANDALONE_STARTER_SILVER"] = "25000",
                ["KONGOR_STANDALONE_STARTER_TICKETS"] = "25",
                ["KONGOR_BOOTSTRAP_INVITE_COUNT"] = "5"
            })
            .Build();

        await SeedDataHandlers.EnsureStandaloneBootstrapAsync(db, configuration, NullLogger.Instance);

        // Verify Roles
        Role? adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.Administrator);
        Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User);
        adminRole.Should().NotBeNull();
        userRole.Should().NotBeNull();

        // Verify Users (Admin, Ranked kongor_host, and Queue Arcade kongor_host_arc)
        List<User> users = await db.Users.Include(u => u.Role).Include(u => u.Accounts).ToListAsync();
        users.Should().HaveCount(3);

        User adminUser = users.Single(u => u.EmailAddress == "admin@kongor.local");
        adminUser.Role.Name.Should().Be(UserRoles.Administrator);
        adminUser.GoldCoins.Should().Be(10000);
        adminUser.SilverCoins.Should().Be(25000);
        adminUser.PlinkoTickets.Should().Be(25);
        adminUser.SRPPasswordSalt.Should().NotBeNullOrWhiteSpace();
        adminUser.SRPPasswordHash.Should().NotBeNullOrWhiteSpace();
        adminUser.PBKDF2PasswordHash.Should().NotBeNullOrWhiteSpace();
        adminUser.Argon2idPasswordHash.Should().NotBeNullOrWhiteSpace();

        User hostUser = users.Single(u => u.EmailAddress == "host@kongor.local");
        hostUser.Role.Name.Should().Be(UserRoles.User);
        hostUser.SRPPasswordSalt.Should().NotBeNullOrWhiteSpace();
        hostUser.SRPPasswordHash.Should().NotBeNullOrWhiteSpace();
        hostUser.PBKDF2PasswordHash.Should().NotBeNullOrWhiteSpace();
        hostUser.Argon2idPasswordHash.Should().NotBeNullOrWhiteSpace();

        User arcadeHostUser = users.Single(u => u.EmailAddress == "host_kongor_host_arc@nexus.local");
        arcadeHostUser.SRPPasswordSalt.Should().Be(hostUser.SRPPasswordSalt);
        arcadeHostUser.SRPPasswordHash.Should().Be(hostUser.SRPPasswordHash);

        // Verify Accounts
        List<Account> accounts = await db.Accounts.ToListAsync();
        accounts.Should().HaveCount(3);

        Account adminAccount = accounts.Single(a => a.Name == "Admin");
        adminAccount.Type.Should().Be(AccountType.Staff);
        adminAccount.IsMain.Should().BeTrue();
        adminAccount.UserID.Should().Be(adminUser.ID);

        Account hostAccount = accounts.Single(a => a.Name == "kongor_host");
        hostAccount.Type.Should().Be(AccountType.ServerHost);
        hostAccount.IsMain.Should().BeFalse();
        hostAccount.AscensionLevel.Should().Be(666);
        hostAccount.AutoConnectChatChannels.Should().Contain(ChatChannels.ServerHostsChannel);
        hostAccount.OwnerAccountID.Should().Be(adminAccount.ID);

        Account arcadeAccount = accounts.Single(a => a.Name == "kongor_host_arc");
        arcadeAccount.Name.Length.Should().BeLessThanOrEqualTo(15);
        arcadeAccount.Type.Should().Be(AccountType.ServerHost);
        arcadeAccount.IsMain.Should().BeFalse();
        arcadeAccount.AscensionLevel.Should().Be(666);
        arcadeAccount.AutoConnectChatChannels.Should().Contain(ChatChannels.ServerHostsChannel);
        arcadeAccount.OwnerAccountID.Should().Be(adminAccount.ID);

        // Verify InviteSystemAuditLog
        InviteSystemAuditLog? auditLog = await db.InviteSystemAuditLogs.FirstOrDefaultAsync();
        auditLog.Should().NotBeNull();
        auditLog!.StaffAccountID.Should().Be(adminAccount.ID);
        auditLog.StaffAccountName.Should().Be("Admin");
        auditLog.InviteOnlyEnabled.Should().BeTrue();
        auditLog.Notes.Should().Be("Standalone initial bootstrap");

        // Verify Invites
        List<UserInvite> invites = await db.UserInvites.ToListAsync();
        invites.Should().HaveCount(5);
        foreach (UserInvite invite in invites)
        {
            invite.CreatedByUserID.Should().Be(adminUser.ID);
            invite.Code.Should().StartWith("KONGOR-");
            invite.Code.Should().MatchRegex(@"^KONGOR-[A-Z0-9]{4}-[A-Z0-9]{4}$");
        }
    }

    [Fact]
    public async Task EnsureStandaloneBootstrapAsync_SecondRun_IsIdempotentNoOp()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KONGOR_STANDALONE"] = "true",
                ["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"] = "AdminSecret123!",
                ["KONGOR_BOOTSTRAP_INVITE_COUNT"] = "3"
            })
            .Build();

        await SeedDataHandlers.EnsureStandaloneBootstrapAsync(db, configuration, NullLogger.Instance);
        int initialUsers = await db.Users.CountAsync();
        int initialAccounts = await db.Accounts.CountAsync();
        int initialInvites = await db.UserInvites.CountAsync();
        int initialAudits = await db.InviteSystemAuditLogs.CountAsync();

        initialUsers.Should().Be(3);
        initialAccounts.Should().Be(3);
        initialInvites.Should().Be(3);
        initialAudits.Should().Be(1);

        // Second run must be an idempotent no-op
        await SeedDataHandlers.EnsureStandaloneBootstrapAsync(db, configuration, NullLogger.Instance);

        (await db.Users.CountAsync()).Should().Be(initialUsers);
        (await db.Accounts.CountAsync()).Should().Be(initialAccounts);
        (await db.UserInvites.CountAsync()).Should().Be(initialInvites);
        (await db.InviteSystemAuditLogs.CountAsync()).Should().Be(initialAudits);
    }

    [Theory]
    [InlineData("false", "AdminSecret123!")]
    [InlineData(null, "AdminSecret123!")]
    [InlineData("true", null)]
    [InlineData("true", "")]
    [InlineData("true", "   ")]
    public async Task EnsureStandaloneBootstrapAsync_UnsetStandaloneOrMissingPassword_IsNoOp(string? standalone, string? adminPassword)
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Dictionary<string, string?> dict = [];
        if (standalone != null) dict["KONGOR_STANDALONE"] = standalone;
        if (adminPassword != null) dict["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"] = adminPassword;

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();

        await SeedDataHandlers.EnsureStandaloneBootstrapAsync(db, configuration, NullLogger.Instance);

        (await db.Users.CountAsync()).Should().Be(0);
        (await db.Accounts.CountAsync()).Should().Be(0);
        (await db.UserInvites.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnsureStandaloneBootstrapAsync_WithOutputPath_WritesInviteCodesToFile()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        string tempOutputDir = Path.Combine(Path.GetTempPath(), $"kongor_test_dir_{Guid.NewGuid():N}");
        string tempFilePath = Path.Combine(tempOutputDir, "standalone_invites.txt");

        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KONGOR_STANDALONE"] = "true",
                    ["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"] = "AdminSecret123!",
                    ["KONGOR_BOOTSTRAP_INVITE_COUNT"] = "4",
                    ["KONGOR_BOOTSTRAP_INVITES_OUTPUT_PATH"] = tempFilePath
                })
                .Build();

            await SeedDataHandlers.EnsureStandaloneBootstrapAsync(db, configuration, NullLogger.Instance);

            File.Exists(tempFilePath).Should().BeTrue();
            string[] fileLines = await File.ReadAllLinesAsync(tempFilePath);
            fileLines.Should().HaveCount(4);

            List<string> dbCodes = await db.UserInvites.Select(i => i.Code).ToListAsync();
            fileLines.Should().BeEquivalentTo(dbCodes);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
            if (Directory.Exists(tempOutputDir))
            {
                Directory.Delete(tempOutputDir, true);
            }
        }
    }

    [Fact]
    public async Task DatabaseReadinessHealthCheck_ReflectsInitializerCompletionAndError()
    {
        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();
        DatabaseInitializer initializer = new(provider, NullLogger<DatabaseInitializer>.Instance);
        DatabaseReadinessHealthCheck healthCheck = new(initializer);

        // 1. Initial state: not completed, no error -> Degraded (in progress)
        HealthCheckResult result1 = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        result1.Status.Should().Be(HealthStatus.Degraded);
        result1.Description.Should().Contain("in progress");

        // 2. Error state: LastError populated -> Unhealthy with error description
        initializer.SetCompletedForTesting(completed: false, lastError: "Connection refused: postgres:5432");
        HealthCheckResult result2 = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        result2.Status.Should().Be(HealthStatus.Unhealthy);
        result2.Description.Should().Be("Connection refused: postgres:5432");

        // 3. Completed state: IsCompleted == true -> Healthy
        initializer.SetCompletedForTesting(completed: true, lastError: null);
        HealthCheckResult result3 = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        result3.Status.Should().Be(HealthStatus.Healthy);
        result3.Description.Should().Be("MERRICK database initialization completed.");
    }

    [Fact]
    public async Task CreateBootstrapInviteCodesAsync_GeneratesRequestedCountBypassingQuota()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        InviteRepository repo = new(db, redis: null, NullLogger<InviteRepository>.Instance);

        IReadOnlyList<UserInvite> invites = await repo.CreateBootstrapInviteCodesAsync(adminUserId: 42, count: 6);

        invites.Should().HaveCount(6);
        invites.Select(i => i.Code).Distinct().Should().HaveCount(6);

        List<UserInvite> persisted = await db.UserInvites.ToListAsync();
        persisted.Should().HaveCount(6);
        foreach (UserInvite invite in persisted)
        {
            invite.CreatedByUserID.Should().Be(42);
            invite.Code.Should().StartWith("KONGOR-");
        }

        // Passing count <= 0 returns empty without error
        IReadOnlyList<UserInvite> zeroInvites = await repo.CreateBootstrapInviteCodesAsync(adminUserId: 42, count: 0);
        zeroInvites.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureQueueArcadeHostAccountsAsync_CustomConfiguredSelfHoster_AutoCreatesAndSyncsDerivedArcadeAccount()
    {
        // protects: Automatic startup derivation and SRP password synchronization of <custom>_arcade for self-hosters.
        // fails_when: Contributors configuring a custom HostAccount:UserName must manually insert <custom>_arcade in SQL.
        // seam: Real SeedDataHandlers.EnsureQueueArcadeHostAccountsAsync against MerrickContext.
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        Role userRole = new() { Name = UserRoles.User };
        await db.Roles.AddAsync(userRole);

        User customUser = new()
        {
            EmailAddress = "host_communityhost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt_v1",
            SRPPasswordHash = "hash_v1",
            PBKDF2PasswordHash = "pbkdf2_v1"
        };
        await db.Users.AddAsync(customUser);
        await db.Accounts.AddAsync(new Account
        {
            Name = "communityhost",
            User = customUser,
            Type = AccountType.ServerHost,
            IsMain = false
        });
        await db.SaveChangesAsync();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostAccount:UserName"] = "communityhost"
            })
            .Build();

        await SeedDataHandlers.EnsureQueueArcadeHostAccountsAsync(db, config, NullLogger.Instance);

        Account? arcadeAcc = await db.Accounts.Include(a => a.User).SingleOrDefaultAsync(a => a.Name == "communityho_arc");
        arcadeAcc.Should().NotBeNull();
        arcadeAcc!.Name.Length.Should().BeLessThanOrEqualTo(15);
        arcadeAcc.Type.Should().Be(AccountType.ServerHost);
        arcadeAcc.User.SRPPasswordHash.Should().Be("hash_v1");

        // Rotate base password -> re-run syncs communityho_arc SRP hash automatically!
        customUser.SRPPasswordSalt = "salt_v2";
        customUser.SRPPasswordHash = "hash_v2";
        await db.SaveChangesAsync();

        await SeedDataHandlers.EnsureQueueArcadeHostAccountsAsync(db, config, NullLogger.Instance);
        (await db.Accounts.Include(a => a.User).SingleAsync(a => a.Name == "communityho_arc"))
            .User.SRPPasswordHash.Should().Be("hash_v2");

        // Verify blank base PBKDF2 hash generates an unguessable CSPRNG ASP.NET Identity hash (never static dev seed 'KONGOR')
        User srpOnlyBaseUser = new()
        {
            EmailAddress = "host_srponlyhost@nexus.local",
            Role = userRole,
            SRPPasswordSalt = "salt_srp",
            SRPPasswordHash = "hash_srp",
            PBKDF2PasswordHash = string.Empty
        };
        await db.Users.AddAsync(srpOnlyBaseUser);
        await db.Accounts.AddAsync(new Account
        {
            Name = "srponlyhost",
            User = srpOnlyBaseUser,
            Type = AccountType.ServerHost,
            IsMain = false
        });
        await db.SaveChangesAsync();

        IConfiguration srpOnlyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HostAccount:UserName"] = "srponlyhost" })
            .Build();
        await SeedDataHandlers.EnsureQueueArcadeHostAccountsAsync(db, srpOnlyConfig, NullLogger.Instance);

        Account srpOnlyArcade = await db.Accounts.Include(a => a.User).SingleAsync(a => a.Name == "srponlyhost_arc");
        srpOnlyArcade.Name.Length.Should().BeLessThanOrEqualTo(15);
        srpOnlyArcade.User.PBKDF2PasswordHash.Should().NotBeNullOrWhiteSpace();
        new Microsoft.AspNetCore.Identity.PasswordHasher<User>()
            .VerifyHashedPassword(srpOnlyArcade.User, srpOnlyArcade.User.PBKDF2PasswordHash, "KONGOR")
            .Should().Be(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
    }
}
