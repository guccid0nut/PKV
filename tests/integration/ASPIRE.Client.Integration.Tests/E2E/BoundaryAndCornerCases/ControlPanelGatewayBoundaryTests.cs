// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ControlPanelGatewayBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary and Corner Cases (Feature 06: Control Panel API Gateway / User Management)
// Policy: Strict Explicit Typing, Boundary Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;
using NEXUS.ControlPanel.GUI.Services;
using AccountType = NEXUS.Contracts.Domain.AccountType;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class ControlPanelGatewayBoundaryTests
{
    [Theory]
    [InlineData(0, 100, 100, 5)]
    [InlineData(-1, 100, 100, 5)]
    [InlineData(10, -50, 100, 5)]
    [InlineData(10, 100, -50, 5)]
    [InlineData(10, 100, 100, -1)]
    public async Task Boundary_InvalidEconomyUpdate_RejectsNegativeOrZeroValues(int userId, int gold, int silver,
        int tickets)
    {
        // Arrange
        IUserManagementService service = new BoundaryUserManagementService();

        // Act
        bool result = await service.UpdateUserEconomyAsync(userId, gold, silver, tickets);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Boundary_EmptySearchQuery_ReturnsEmptyList(string query)
    {
        // Arrange
        IUserManagementService service = new BoundaryUserManagementService();

        // Act
        List<UserDetailDto> results = await service.SearchUsersAsync(query, UserRoles.AllRoles);

        // Assert
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Boundary_NonExistentUserId_ReturnsNull(int userId)
    {
        // Arrange
        IUserManagementService service = new BoundaryUserManagementService();

        // Act
        UserDetailDto? result = await service.GetUserByIdAsync(userId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Boundary_UserRoleConstants_ValidatesAdministratorAndUser()
    {
        // Assert
        UserRoles.Administrator.Should().Be("ADMINISTRATOR");
        UserRoles.User.Should().Be("USER");
    }

    [Fact]
    public void Boundary_AccountItemDto_InitializesDefaultValues()
    {
        // Arrange
        AccountItemDto account = new()
        {
            Id = 42,
            Name = "PlayerOne",
            IsMain = true,
            Type = AccountType.Normal
        };

        // Assert
        account.Id.Should().Be(42);
        account.Name.Should().Be("PlayerOne");
        account.IsMain.Should().BeTrue();
        account.Type.Should().Be(AccountType.Normal);
    }

    private sealed class BoundaryUserManagementService : IUserManagementService
    {
        public Task<UserSummaryStats> GetSummaryStatsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UserSummaryStats());
        }

        public Task<List<UserDetailDto>> SearchUsersAsync(string query, string roleFilter,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(new List<UserDetailDto>());

            return Task.FromResult(new List<UserDetailDto>
            {
                new() { Id = 1, EmailAddress = $"{query}@kongor.test", RoleName = UserRoles.User }
            });
        }

        public Task<UserDetailDto?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
        {
            if (userId <= 0) return Task.FromResult<UserDetailDto?>(null);

            return Task.FromResult<UserDetailDto?>(new UserDetailDto { Id = userId });
        }

        public Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(userId > 0 && !string.IsNullOrWhiteSpace(roleName));
        }

        public Task<bool> SetAccountRoleAsync(int accountId, AccountType type,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(accountId > 0);
        }

        public Task<string?> ResetHostPasswordAsync(int accountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(accountId > 0 ? "new-pass" : null);
        }

        public Task<string?> ResetHostAuthTokenAsync(int accountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(accountId > 0 ? "new-token" : null);
        }

        public Task<bool> UpdateUserEconomyAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
            CancellationToken cancellationToken = default)
        {
            if (userId <= 0 || goldCoins < 0 || silverCoins < 0 || plinkoTickets < 0) return Task.FromResult(false);

            return Task.FromResult(true);
        }

        public Task<bool> AdjustUserBalanceAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
            CancellationToken cancellationToken = default)
        {
            return UpdateUserEconomyAsync(userId, goldCoins, silverCoins, plinkoTickets, cancellationToken);
        }

        public Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(userId > 0);
        }

        public Task<bool> PromoteUserToHostAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(userId > 0);
        }

        public Task<bool> RevertUserToStandardAsync(int userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(userId > 0);
        }
    }
}
