// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Constants;
using FluentAssertions;
using MERRICK.DatabaseContext.Data;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Data;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Tests.Infrastructure;

namespace ZORGATH.WebPortal.API.Tests;

/// <summary>
///     Empirical Adversarial Challenger Test Suite for Milestone 5 WebPortal API integration:
///     1. StoreApiController.GetBalances with Account.SelectedStoreItemCodes.
///     2. Fallback to StoreDefaults.DefaultSelectedItems when empty or main account missing.
///     3. Roundtrip fidelity across DTO boundaries without truncation.
/// </summary>
public sealed class WebPortalApiSecurityAndResilienceTests
{
    private static (User user, Account account) CreateTestUserAndAccount(int userId = 50, int accountId = 500, string name = "WebPortalPlayer")
    {
        User user = new()
        {
            ID = userId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new Role { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2",
            GoldCoins = 500,
            SilverCoins = 1500,
            PlinkoTickets = 3,
            InventoryItems =
            [
                new UserInventory { ItemCode = "aa.Hero_Devourer.alt" },
                new UserInventory { ItemCode = "c.chat_red" }
            ]
        };

        Account account = new()
        {
            ID = accountId,
            UserID = userId,
            Name = name,
            User = user,
            IsMain = true
        };

        user.Accounts.Add(account);
        return (user, account);
    }

    [Fact]
    public async Task GetBalances_WithPopulatedSelectedStoreItems_ReturnsCodesInDto()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        List<string> selectedCodes = ["aa.Hero_Devourer.alt", "c.chat_red", "ai.custom_icon:1"];
        account.SetSelectedStoreItems(selectedCodes);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetUserByIdAsync(user.ID, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Mock<IInventoryRepository> mockInventoryRepo = new();

        StoreApiController controller = new(mockInventoryRepo.Object, mockAccountRepo.Object);

        // Act
        IActionResult result = await controller.GetBalances(user.ID, CancellationToken.None);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        UserBalancesDto dto = okResult.Value.Should().BeOfType<UserBalancesDto>().Subject;

        dto.UserId.Should().Be(user.ID);
        dto.GoldCoins.Should().Be(500);
        dto.SilverCoins.Should().Be(1500);
        dto.PlinkoTickets.Should().Be(3);
        dto.OwnedStoreItems.Should().BeEquivalentTo(new[] { "aa.Hero_Devourer.alt", "c.chat_red" });
        dto.SelectedStoreItems.Should().BeEquivalentTo(selectedCodes);
    }

    [Fact]
    public async Task GetBalances_WithEmptySelectedStoreItems_ReturnsEmptyList()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        account.SelectedStoreItems.Clear();

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetUserByIdAsync(user.ID, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Mock<IInventoryRepository> mockInventoryRepo = new();

        StoreApiController controller = new(mockInventoryRepo.Object, mockAccountRepo.Object);

        // Act
        IActionResult result = await controller.GetBalances(user.ID, CancellationToken.None);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        UserBalancesDto dto = okResult.Value.Should().BeOfType<UserBalancesDto>().Subject;

        // When mainAccount is non-null, SelectedStoreItemCodes returns [] so null-coalescing does not trigger
        dto.SelectedStoreItems.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBalances_WhenUserHasNoAccounts_FallsBackToDefaultSelectedItems()
    {
        // Arrange
        (User user, _) = CreateTestUserAndAccount();
        user.Accounts.Clear();

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetUserByIdAsync(user.ID, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        Mock<IInventoryRepository> mockInventoryRepo = new();

        StoreApiController controller = new(mockInventoryRepo.Object, mockAccountRepo.Object);

        // Act
        IActionResult result = await controller.GetBalances(user.ID, CancellationToken.None);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        UserBalancesDto dto = okResult.Value.Should().BeOfType<UserBalancesDto>().Subject;

        dto.SelectedStoreItems.Should().BeEquivalentTo(StoreDefaults.DefaultSelectedItems);
    }

    [Fact]
    public async Task GetBalances_WhenUserNotFound_ReturnsNotFound()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo.Setup(r => r.GetUserByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Mock<IInventoryRepository> mockInventoryRepo = new();

        StoreApiController controller = new(mockInventoryRepo.Object, mockAccountRepo.Object);

        // Act
        IActionResult result = await controller.GetBalances(999, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
