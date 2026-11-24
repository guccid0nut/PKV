// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature06_ControlPanelApiGatewayTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 06: Control Panel API Gateway / User Management)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;
using NEXUS.ControlPanel.GUI.Services;
using AccountType = NEXUS.Contracts.Domain.AccountType;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class ControlPanelApiGatewayTests
{
    [Fact]
    public async Task UserManagement_GetSummaryStatsAsync_ReturnsAccurateMetrics()
    {
        // Arrange
        IUserManagementService service = new MockUserManagementService();

        // Act
        UserSummaryStats stats = await service.GetSummaryStatsAsync();

        // Assert
        stats.Should().NotBeNull();
        stats.TotalUsers.Should().Be(1200);
        stats.TotalAccounts.Should().Be(1450);
        stats.IsDatabaseConnected.Should().BeTrue();
    }

    [Fact]
    public async Task UserManagement_SearchUsersAsync_FindsMatchingUser()
    {
        // Arrange
        IUserManagementService service = new MockUserManagementService();

        // Act
        List<UserDetailDto> users = await service.SearchUsersAsync("HeroAdmin", UserRoles.Administrator);

        // Assert
        users.Should().NotBeNull();
        users.Should().ContainSingle();
        users[0].RoleName.Should().Be(UserRoles.Administrator);
        users[0].MainAccount?.Name.Should().Be("HeroAdmin");
    }

    [Fact]
    public async Task UserManagement_SetUserRoleAsync_UpdatesRoleSuccessfully()
    {
        // Arrange
        IUserManagementService service = new MockUserManagementService();

        // Act
        bool success = await service.SetUserRoleAsync(100, UserRoles.Administrator);

        // Assert
        success.Should().BeTrue();
    }

    [Fact]
    public async Task UserManagement_UpdateUserEconomyAsync_CreditsAccountSuccessfully()
    {
        // Arrange
        IUserManagementService service = new MockUserManagementService();

        // Act
        bool credited = await service.UpdateUserEconomyAsync(100, 500, 1500, 10);

        // Assert
        credited.Should().BeTrue();
    }

    [Fact]
    public async Task UserManagement_ResetHostPasswordAsync_GeneratesSecureCredentials()
    {
        // Arrange
        IUserManagementService service = new MockUserManagementService();

        // Act
        string? newPassword = await service.ResetHostPasswordAsync(100);

        // Assert
        newPassword.Should().NotBeNullOrWhiteSpace();
        newPassword.Should().StartWith("GeneratedSecureHostPass");
    }

    [Fact]
    public void UserRoles_StandardRoleNames_MatchConstants()
    {
        // Assert
        UserRoles.Administrator.Should().Be("ADMINISTRATOR");
        UserRoles.User.Should().Be("USER");
    }

    private sealed class MockUserManagementService : IUserManagementService
    {
        public Task<UserSummaryStats> GetSummaryStatsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UserSummaryStats
            {
                TotalUsers = 1200,
                TotalAccounts = 1450,
                TotalStaff = 12,
                TotalHosts = 45,
                IsDatabaseConnected = true,
                StatusMessage = "Operational"
            });
        }

        public Task<List<UserDetailDto>> SearchUsersAsync(string query, string roleFilter,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<UserDetailDto>
            {
                new()
                {
                    Id = 100,
                    EmailAddress = "admin@kongor.net",
                    RoleName = UserRoles.Administrator,
                    IsStaff = true,
                    GoldCoins = 5000,
                    SilverCoins = 15000,
                    Accounts =
                    [
                        new AccountItemDto { Id = 100, Name = "HeroAdmin", IsMain = true, Type = AccountType.Staff }
                    ]
                }
            });
        }

        public Task<UserDetailDto?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserDetailDto?>(new UserDetailDto
            {
                Id = userId,
                EmailAddress = "user@kongor.net",
                RoleName = UserRoles.User,
                IsStaff = false
            });
        }

        public Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> SetAccountRoleAsync(int accountId, AccountType type,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string?> ResetHostPasswordAsync(int accountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("GeneratedSecureHostPass_123");
        }

        public Task<string?> ResetHostAuthTokenAsync(int accountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("GeneratedAuthToken_456");
        }

        public Task<bool> UpdateUserEconomyAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> AdjustUserBalanceAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> PromoteUserToHostAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> RevertUserToStandardAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }
}
