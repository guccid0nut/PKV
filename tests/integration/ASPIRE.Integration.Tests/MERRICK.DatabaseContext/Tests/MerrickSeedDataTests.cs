// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Extensions;
using MERRICK.DatabaseContext.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public sealed class MerrickSeedDataTests(ITestOutputHelper testOutputHelper)
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    [Fact]
    public async Task SeedAccounts_AccountsAlreadyExist_DoesNotSeed()
    {
        await using MerrickContext dbContext = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "existing@test.com",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h",
            Role = new Role { Name = "User" }
        };
        Account existingAccount = new() { Name = "ExistingAcc", User = user, IsMain = true };
        await dbContext.Users.AddAsync(user);
        await dbContext.Accounts.AddAsync(existingAccount);
        await dbContext.SaveChangesAsync();

        await SeedDataHandlers.SeedAccounts(dbContext, NullLogger.Instance, CancellationToken.None);

        int accountCount = await dbContext.Accounts.CountAsync();
        Assert.Equal(1, accountCount);
    }

    [Fact]
    public async Task SeedAccounts_UserAndClanExist_SeedsAccountsSuccessfully()
    {
        await using MerrickContext dbContext = CreateInMemoryContext();
        User u1 = new()
        {
            EmailAddress = "admin@test.com",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h",
            Role = new Role { Name = "Admin" }
        };
        User u2 = new()
        {
            EmailAddress = "guest@test.com",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h",
            Role = new Role { Name = "User" }
        };

        Clan c1 = new()
            { Name = "Heroes Of Newerth", Tag = "HON", TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime };
        Clan c2 = new()
            { Name = "Secondary Clan", Tag = "SEC", TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime };

        await dbContext.Users.AddRangeAsync(u1, u2);
        await dbContext.Clans.AddRangeAsync(c1, c2);
        await dbContext.SaveChangesAsync();

        await SeedDataHandlers.SeedAccounts(dbContext, NullLogger.Instance, CancellationToken.None);

        int accountCount = await dbContext.Accounts.CountAsync();
        Assert.True(accountCount >= 2);

        Account? systemAccount = await dbContext.Accounts.FirstOrDefaultAsync(a => a.Name == "KONGOR");
        Assert.NotNull(systemAccount);
        Assert.Equal(666, systemAccount.AscensionLevel);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task FullSeedPipeline_OnEmptyDatabase_SeedsAllUsersClansAndAccounts()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        await using MerrickContext dbContext = CreateInMemoryContext();

        await SeedDataHandlers.SeedUsers(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedClans(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedAccounts(dbContext, NullLogger.Instance, CancellationToken.None);
        if (NexusTestEnvironment.IsManualTestEnabled)
        {
            await SeedDataHandlers.SeedHeroGuides(dbContext, NullLogger.Instance, CancellationToken.None);
            await SeedDataHandlers.SeedHeroItems(dbContext, NullLogger.Instance, CancellationToken.None);
            await SeedDataHandlers.SeedHeroDetails(dbContext, NullLogger.Instance, CancellationToken.None);
        }

        await SeedDataHandlers.EnsureStaffAccountsAsync(dbContext, NullLogger.Instance, CancellationToken.None);

        int userCount = await dbContext.Users.CountAsync();
        int clanCount = await dbContext.Clans.CountAsync();
        int accountCount = await dbContext.Accounts.CountAsync();
        int roleCount = await dbContext.Roles.CountAsync();

        Assert.True(userCount >= 2, $"Expected at least 2 users, got {userCount}");
        Assert.True(clanCount >= 4, $"Expected at least 4 clans, got {clanCount}");
        Assert.True(accountCount >= 4, $"Expected at rationale at least 4 accounts, got {accountCount}");
        Assert.True(roleCount >= 2, $"Expected at least 2 roles, got {roleCount}");
    }

    [Fact]
    public async Task MerrickSeedData_EnsureStaffAccountsAsync_IsolatesHostAccountsFromPlayerUser()
    {
        await using MerrickContext dbContext = CreateInMemoryContext();

        await SeedDataHandlers.SeedUsers(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedClans(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedAccounts(dbContext, NullLogger.Instance, CancellationToken.None);

        User kongorUser = await dbContext.Users.FirstAsync(u => u.EmailAddress == "admin@kongor.local");
        Account guccidonut = new() { Name = "guccidonut", User = kongorUser, IsMain = true, Type = AccountType.Normal };
        await dbContext.Accounts.AddAsync(guccidonut);
        await dbContext.SaveChangesAsync();

        await SeedDataHandlers.EnsureStaffAccountsAsync(dbContext, NullLogger.Instance, CancellationToken.None);

        Account? reloadedGuccidonut =
            await dbContext.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == "guccidonut");
        Account? guccihost =
            await dbContext.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == "guccihost");
        Account? kongor = await dbContext.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == "KONGOR");
        Account? host = await dbContext.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == "HOST");

        Assert.NotNull(reloadedGuccidonut);
        Assert.NotNull(guccihost);
        Assert.NotNull(kongor);
        Assert.NotNull(host);

        Assert.NotEqual(reloadedGuccidonut.User.ID, guccihost.User.ID);
        Assert.StartsWith("host_guccihost@", guccihost.User.EmailAddress);

        Assert.NotEqual(kongor.User.ID, host.User.ID);
        Assert.StartsWith("host_host@", host.User.EmailAddress);
    }

    [Fact]
    public async Task EnsureStaffAccountsAsync_IsIdempotent_AndDoesNotDuplicateSelectedStoreItems()
    {
        await using MerrickContext dbContext = CreateInMemoryContext();

        await SeedDataHandlers.SeedUsers(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedClans(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedAccounts(dbContext, NullLogger.Instance, CancellationToken.None);

        User kongorUser = await dbContext.Users.FirstAsync(u => u.EmailAddress == "admin@kongor.local");
        Account guccidonut = new() { Name = "guccidonut", User = kongorUser, IsMain = true, Type = AccountType.Normal };
        await dbContext.Accounts.AddAsync(guccidonut);
        await dbContext.SaveChangesAsync();

        // First run: equips ai.custom_icon:1
        await SeedDataHandlers.EnsureStaffAccountsAsync(dbContext, NullLogger.Instance, CancellationToken.None);

        Account? reloadedGuccidonut = await dbContext.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.Name == "guccidonut");

        Assert.NotNull(reloadedGuccidonut);
        Assert.True(reloadedGuccidonut.HasSelectedStoreItem("ai.custom_icon:1"));
        int customIconCountFirst = reloadedGuccidonut.SelectedStoreItems
            .Count(i => i.ItemCode == "ai.custom_icon:1");
        Assert.Equal(1, customIconCountFirst);

        // Second run (simulating cluster restart / secondary service): must be idempotent and not throw or duplicate
        await SeedDataHandlers.EnsureStaffAccountsAsync(dbContext, NullLogger.Instance, CancellationToken.None);

        Account? reloadedGuccidonutSecond = await dbContext.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.Name == "guccidonut");

        Assert.NotNull(reloadedGuccidonutSecond);
        int customIconCountSecond = reloadedGuccidonutSecond.SelectedStoreItems
            .Count(i => i.ItemCode == "ai.custom_icon:1");
        Assert.Equal(1, customIconCountSecond);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task DirectSupabase_SeedPipeline_SeedsDatabaseDirectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string rawConnStr =
            Environment.GetEnvironmentVariable("KONGOR_SUPABASE_TEST_CONNECTION_STRING") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawConnStr)) return;
        string connStr = ConfigurationExtensions.NormalizePostgreSqlConnectionString(rawConnStr);
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(connStr, npgsqlOptions =>
        {
            npgsqlOptions.MigrationsHistoryTable("MigrationsHistory", MerrickContext.MetadataSchema);
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        });

        await using MerrickContext dbContext = new(optionsBuilder.Options);

        try
        {
            await dbContext.Database.OpenConnectionAsync();
            testOutputHelper.WriteLine("[DIRECT SUPABASE] Successfully opened connection!");
        }
        catch (Exception ex)
        {
            testOutputHelper.WriteLine(
                $"[DIRECT SUPABASE CONNECTION FAILED] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            if (ex.InnerException != null)
                testOutputHelper.WriteLine(
                    $"[DIRECT SUPABASE INNER EXCEPTION] {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");

            throw;
        }

        await SeedDataHandlers.SeedUsers(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedClans(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedAccounts(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedHeroGuides(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedHeroItems(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.SeedHeroDetails(dbContext, NullLogger.Instance, CancellationToken.None);
        await SeedDataHandlers.EnsureStaffAccountsAsync(dbContext, NullLogger.Instance, CancellationToken.None);

        int userCount = await dbContext.Users.CountAsync();
        int clanCount = await dbContext.Clans.CountAsync();
        int accountCount = await dbContext.Accounts.CountAsync();

        Assert.True(userCount >= 2, $"Expected at least 2 users in Supabase, got {userCount}");
        Assert.True(clanCount >= 4, $"Expected at least 4 clans in Supabase, got {clanCount}");
        Assert.True(accountCount >= 4, $"Expected at least 4 accounts in Supabase, got {accountCount}");
    }
}
