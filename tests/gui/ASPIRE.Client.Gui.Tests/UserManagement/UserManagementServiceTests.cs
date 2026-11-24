// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using FluentAssertions;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Domain;
using NEXUS.ControlPanel.GUI.Services;
using Xunit;

namespace ASPIRE.Client.Gui.Tests.UserManagement;

public class UserManagementServiceTests : IDisposable
{
    private readonly MerrickContext _dbContext;
    private readonly string _databaseName;

    public UserManagementServiceTests()
    {
        _databaseName = Guid.NewGuid().ToString();
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        _dbContext = new MerrickContext(options);

        SeedDatabase();
    }

    public void Dispose()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedDatabase()
    {
        Role adminRole = new() { ID = 1, Name = UserRoles.Administrator };
        Role userRole = new() { ID = 2, Name = UserRoles.User };
        _dbContext.Roles.AddRange(adminRole, userRole);

        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.test",
            DiscordId = "123456789",
            DiscordUsername = "AdminDonut",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2_123",
            RoleID = adminRole.ID,
            Role = adminRole,
            GoldCoins = 1000,
            SilverCoins = 5000,
            PlinkoTickets = 10
        };

        Account adminMain = new()
        {
            ID = 101,
            Name = "AdminHero",
            UserID = adminUser.ID,
            User = adminUser,
            IsMain = true,
            Type = AccountType.Staff
        };

        Account adminHost = new()
        {
            ID = 102,
            Name = "AdminDedicatedServer",
            UserID = adminUser.ID,
            User = adminUser,
            OwnerAccountID = adminMain.ID,
            IsMain = false,
            Type = AccountType.ServerHost
        };

        User normalUser = new()
        {
            ID = 2,
            EmailAddress = "player@kongor.test",
            DiscordId = "987654321",
            DiscordUsername = "CasualPlayer",
            SRPPasswordSalt = "salt456",
            SRPPasswordHash = "hash456",
            PBKDF2PasswordHash = "pbkdf2_456",
            RoleID = userRole.ID,
            Role = userRole,
            GoldCoins = 50,
            SilverCoins = 200,
            PlinkoTickets = 1
        };

        Account normalMain = new()
        {
            ID = 201,
            Name = "CasualWarrior",
            UserID = normalUser.ID,
            User = normalUser,
            IsMain = true,
            Type = AccountType.Normal
        };

        User hostUser = new()
        {
            ID = 3,
            EmailAddress = "host_donuthost@kongor.test",
            SRPPasswordSalt = "salt789",
            SRPPasswordHash = "hash789",
            PBKDF2PasswordHash = "pbkdf2_789",
            RoleID = userRole.ID,
            Role = userRole
        };

        HostApplication hostApp = new()
        {
            ID = 1,
            UserID = adminUser.ID,
            User = adminUser,
            MainAccountID = adminMain.ID,
            MainAccount = adminMain,
            HostAccountID = adminHost.ID,
            HostAccount = adminHost,
            HostUsername = "donut_host",
            EncryptedAuthToken = "tok_initial_token_1234"
        };

        _dbContext.Users.AddRange(adminUser, normalUser, hostUser);
        _dbContext.Accounts.AddRange(adminMain, adminHost, normalMain);
        _dbContext.HostApplications.Add(hostApp);
        _dbContext.SaveChanges();
    }

    [Fact]
    public async Task GetSummaryStatsAsync_ReturnsCorrectDirectDbStats()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        UserSummaryStats stats = await service.GetSummaryStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.IsDatabaseConnected.Should().BeTrue();
        stats.TotalUsers.Should().Be(2); // Excludes host_*
        stats.TotalAccounts.Should().Be(3);
        stats.TotalStaff.Should().Be(1);
        stats.TotalHosts.Should().Be(1);
        stats.StatusMessage.Should().Contain("Supabase PostgreSQL (Direct DB Mode)");
    }

    [Fact]
    public async Task SearchUsersAsync_FindsUserByQueryAndRole()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act - Search by query
        List<UserDetailDto> results = await service.SearchUsersAsync("Admin", "ALL");

        // Assert
        results.Should().HaveCount(1);
        results[0].DiscordUsername.Should().Be("AdminDonut");
        results[0].Accounts.Should().HaveCount(2); // Main + Owned Host
        results[0].Accounts.Should().Contain(a => a.Type == AccountType.ServerHost);

        // Act - Search by role filter STAFF
        List<UserDetailDto> staffResults = await service.SearchUsersAsync(string.Empty, "STAFF");
        staffResults.Should().HaveCount(1);
        staffResults[0].Id.Should().Be(1);

        // Act - Search by role filter STANDARD
        List<UserDetailDto> standardResults = await service.SearchUsersAsync(string.Empty, "STANDARD");
        standardResults.Should().HaveCount(1);
        standardResults[0].Id.Should().Be(2);
    }

    [Fact]
    public async Task GetUserByIdAsync_ReturnsDetailedUser()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        UserDetailDto? result = await service.GetUserByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.EmailAddress.Should().Be("admin@kongor.test");
        result.RoleName.Should().Be(UserRoles.Administrator);
        result.IsStaff.Should().BeTrue();
        result.Accounts.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetUserRoleAsync_UpdatesRoleInDb()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool result = await service.SetUserRoleAsync(2, UserRoles.Administrator);

        // Assert
        result.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        User? user = await verifyDb.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.ID == 2);
        user.Should().NotBeNull();
        user!.Role.Name.Should().Be(UserRoles.Administrator);
    }

    [Fact]
    public async Task SetAccountRoleAsync_UpdatesAccountType()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool result = await service.SetAccountRoleAsync(201, AccountType.VIP);

        // Assert
        result.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        Account? account = await verifyDb.Accounts.FindAsync(201);
        account.Should().NotBeNull();
        account!.Type.Should().Be(AccountType.VIP);
    }

    [Fact]
    public async Task UpdateUserEconomyAsync_MutatesBalancesAndRecordsTransaction()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool success = await service.UpdateUserEconomyAsync(2, 500, 1500, 25);

        // Assert
        success.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        User? user = await verifyDb.Users.FindAsync(2);
        user.Should().NotBeNull();
        user!.GoldCoins.Should().Be(500);
        user.SilverCoins.Should().Be(1500);
        user.PlinkoTickets.Should().Be(25);

        EconomyTransaction? tx = await verifyDb.EconomyTransactions.FirstOrDefaultAsync(t => t.UserID == 2);
        tx.Should().NotBeNull();
        tx!.TransactionType.Should().Be("ControlPanelUpdateEconomy");
        tx.GoldCoinsDelta.Should().Be(450); // 500 - 50
    }

    [Fact]
    public async Task AdjustUserBalanceAsync_AppliesDeltas()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool success = await service.AdjustUserBalanceAsync(2, 100, -50, 5);

        // Assert
        success.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        User? user = await verifyDb.Users.FindAsync(2);
        user.Should().NotBeNull();
        user!.GoldCoins.Should().Be(150); // 50 + 100
        user.SilverCoins.Should().Be(150); // 200 - 50
        user.PlinkoTickets.Should().Be(6); // 1 + 5
    }

    [Fact]
    public async Task PromoteUserToStaffAsync_UpdatesRoleAndMainAccount()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool success = await service.PromoteUserToStaffAsync(2);

        // Assert
        success.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        User? user = await verifyDb.Users.Include(u => u.Role).Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == 2);
        user.Should().NotBeNull();
        user!.Role.Name.Should().Be(UserRoles.Administrator);
        user.Accounts.First(a => a.IsMain).Type.Should().Be(AccountType.Staff);
    }

    [Fact]
    public async Task PromoteUserToHostAsync_UpdatesMainAccountToHost()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool success = await service.PromoteUserToHostAsync(2);

        // Assert
        success.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        User? user = await verifyDb.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == 2);
        user.Should().NotBeNull();
        user!.Accounts.First(a => a.IsMain).Type.Should().Be(AccountType.ServerHost);
    }

    [Fact]
    public async Task RevertUserToStandardAsync_ResetsRoleAndAccounts()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        bool success = await service.RevertUserToStandardAsync(1);

        // Assert
        success.Should().BeTrue();

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        User? user = await verifyDb.Users.Include(u => u.Role).Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == 1);
        user.Should().NotBeNull();
        user!.Role.Name.Should().Be(UserRoles.User);
        user.Accounts.Should().AllSatisfy(a => a.Type.Should().Be(AccountType.Normal));
    }

    [Fact]
    public async Task ResetHostPasswordAsync_UpdatesUserSrpSaltAndHash()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        string? password = await service.ResetHostPasswordAsync(102);

        // Assert
        password.Should().NotBeNullOrWhiteSpace();
        password!.Length.Should().Be(16);

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        Account? account = await verifyDb.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.ID == 102);
        account.Should().NotBeNull();
        account!.User.SRPPasswordSalt.Should().NotBeNullOrEmpty();
        account.User.SRPPasswordHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResetHostAuthTokenAsync_UpdatesHostApplicationToken()
    {
        // Arrange
        UserManagementService service = new(dbContextFactory: () => new MerrickContext(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options));

        // Act
        string? token = await service.ResetHostAuthTokenAsync(102);

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
        token!.Should().StartWith("tok_");

        using MerrickContext verifyDb = new(
            new DbContextOptionsBuilder<MerrickContext>().UseInMemoryDatabase(_databaseName).Options);
        HostApplication? app = await verifyDb.HostApplications.FirstOrDefaultAsync(h => h.HostAccountID == 102);
        app.Should().NotBeNull();
        HostSecurityService.DecryptPassword(app!.EncryptedAuthToken!).Should().Be(token);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LiveSupabase_GetSummaryStats_ConnectsDirectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        SecretsManagerService secretsManager = new();
        UserManagementService service = new(secretsManager);

        UserSummaryStats stats = await service.GetSummaryStatsAsync();
        stats.Should().NotBeNull();
        stats.IsDatabaseConnected.Should().BeTrue();
        stats.StatusMessage.Should().Contain("Supabase PostgreSQL (Direct DB Mode)");
        stats.TotalUsers.Should().BeGreaterThan(0);
        stats.TotalAccounts.Should().BeGreaterThan(0);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LiveSupabase_SearchUsers_ReturnsUsersAndAccounts()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        SecretsManagerService secretsManager = new();
        UserManagementService service = new(secretsManager);

        List<UserDetailDto> users = await service.SearchUsersAsync(string.Empty, "ALL");
        users.Should().NotBeNull();
        users.Should().NotBeEmpty();
        users.Should().Contain(u => u.Accounts.Count > 0);
    }
}
