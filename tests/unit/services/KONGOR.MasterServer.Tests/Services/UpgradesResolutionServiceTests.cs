// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.ServerRequesterController;
using KONGOR.MasterServer.Helpers.Serialisation;
using KONGOR.MasterServer.Helpers.Stats;
using KONGOR.MasterServer.Models.Configuration;
using KONGOR.MasterServer.Configuration.Store;
using KONGOR.MasterServer.Services.Store;
using ASPIRE.Common.Constants;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class UpgradesResolutionServiceTests
{
    private static (Account account, User user) CreateTestAccount(string name = "waluigi_tester", int accountId = 999)
    {
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            DiscordId = "123456789012345678",
            InventoryItems =
            [
                new UserInventory { UserID = accountId, ItemCode = "Hero_Pyromancer.Alt", Quantity = 1, IsPermanent = true },
                new UserInventory { UserID = accountId, ItemCode = "aa.Hero_WitchSlayer.Alt", Quantity = 1, IsPermanent = true }
            ],
            Role = new MERRICK.DatabaseContext.Entities.Utility.Role { ID = 2, Name = "User" }
        };

        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };

        return (account, user);
    }

    [Fact]
    public async Task UpgradesResolutionService_AutoGrantsAllModSkinsAndDualKeys()
    {
        // Arrange
        UpgradesResolutionService service = new(NullLogger<UpgradesResolutionService>.Instance);
        (Account account, _) = CreateTestAccount();

        // Act
        HashSet<string> upgrades = await service.ResolveExpandedOwnedItemsAsync(account, CancellationToken.None);

        // Assert - Rampage Waluigi mod skin (6117 / Hero_Rampage.Alt16)
        upgrades.Should().Contain("aa.Hero_Rampage.Alt16", "C++ engine requires aa. prefix for CanAccessAltAvatar");
        upgrades.Should().Contain("Hero_Rampage.Alt16", "Dual-key parity ensures UI lookup succeeds");

        // Assert - Other mod skins (e.g. Mario Hero_DwarfMagi.Alt8, Cloud Hero_Hiro.Alt16, Wario Hero_DrunkenMaster.Alt11)
        upgrades.Should().Contain("aa.Hero_DwarfMagi.Alt8");
        upgrades.Should().Contain("Hero_DwarfMagi.Alt8");
        upgrades.Should().Contain("aa.Hero_Hiro.Alt16");
        upgrades.Should().Contain("Hero_Hiro.Alt16");
        upgrades.Should().Contain("aa.Hero_DrunkenMaster.Alt11");
        upgrades.Should().Contain("Hero_DrunkenMaster.Alt11");

        // Assert - User inventory items are dual-expanded
        upgrades.Should().Contain("aa.Hero_Pyromancer.Alt");
        upgrades.Should().Contain("Hero_Pyromancer.Alt");
        upgrades.Should().Contain("aa.Hero_WitchSlayer.Alt");
        upgrades.Should().Contain("Hero_WitchSlayer.Alt");
    }

    [Fact]
    public async Task ServerRequesterController_GetUpgrades_ReturnsModSkinsInMyUpgrades()
    {
        // Arrange
        (Account account, _) = CreateTestAccount();
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchHistoryRepo = new();
        Mock<IDatabase> mockRedis = new();
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

        string cookie = "sess_cookie_test";
        mockRedis
            .Setup(r => r.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains(cookie)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(account.Name);

        mockAccountRepo
            .Setup(a => a.GetByNicknameAsync(account.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        UpgradesResolutionService resolver = new(NullLogger<UpgradesResolutionService>.Instance);

        ServerRequesterController controller = new(
            mockAccountRepo.Object,
            mockMatchHistoryRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            config,
            upgradesResolver: resolver);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("get_upgrades"),
            ["cookie"] = new(cookie)
        });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        IActionResult result = await controller.ServerRequester(CancellationToken.None);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        string phpString = okResult.Value.Should().BeOfType<string>().Subject;

        object? deserialized = PhpSerialization.Deserialize(phpString);
        ASPIRE.Common.Serialization.PHP.PhpHashtable responseMap = deserialized.Should().BeOfType<ASPIRE.Common.Serialization.PHP.PhpHashtable>().Subject;

        responseMap.ContainsKey("my_upgrades").Should().BeTrue();
        List<string> upgradeStrings = new();
        if (responseMap["my_upgrades"] is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Value != null)
                    upgradeStrings.Add(entry.Value.ToString()!);
            }
        }
        else if (responseMap["my_upgrades"] is System.Collections.IEnumerable list)
        {
            foreach (object item in list)
            {
                if (item is System.Collections.DictionaryEntry de)
                    upgradeStrings.Add(de.Value?.ToString()!);
                else
                    upgradeStrings.Add(item.ToString()!);
            }
        }

        upgradeStrings.Should().Contain("aa.Hero_Rampage.Alt16", "Dedicated server must receive aa.Hero_Rampage.Alt16 to authorize Waluigi skin");
        upgradeStrings.Should().Contain("Hero_Rampage.Alt16");
    }

    [Fact]
    public async Task UpgradesResolutionService_AutoGrantsSupplementaryModAvatarsAndNeutralizesCouponsOnModSkins()
    {
        // Arrange
        UpgradesResolutionService service = new(NullLogger<UpgradesResolutionService>.Instance);
        (Account account, _) = CreateTestAccount();

        // Act
        HashSet<string> upgrades = await service.ResolveExpandedOwnedItemsAsync(account, CancellationToken.None);

        // Assert - Supplementary mod skins defined in client metadata (heroes.xml) are auto-granted with dual keys
        upgrades.Should().Contain("aa.Hero_Aluna.Alt6");
        upgrades.Should().Contain("Hero_Aluna.Alt6");
        upgrades.Should().Contain("aa.Hero_Chronos.pog_skin");
        upgrades.Should().Contain("Hero_Chronos.pog_skin");
        upgrades.Should().Contain("aa.Hero_Behemoth.Alt11");
        upgrades.Should().Contain("Hero_Behemoth.Alt11");
        upgrades.Should().Contain("aa.Hero_Magmar.Alt14");
        upgrades.Should().Contain("Hero_Magmar.Alt14");

        // Assert - Mastery coupons never target community mod avatars (ID >= 6000) and have empty expiration date
        var coupon = MasteryCouponHelper.BuildDiscountCoupon("cp.All Avatar Mastery Coupon * 1");
        coupon.Should().NotBeNull();
        coupon!.ApplicableProducts.Should().NotContain("aa.Hero_Adrenaline.Alt7", "Mod avatars (ID >= 6000) must not have coupons applied");
        coupon.ApplicableProducts.Should().NotContain("aa.Hero_Rampage.Alt16", "Waluigi mod skin must not receive coupon tags");
        coupon.DiscountExpirationDate.Should().BeEmpty("Coupons must have empty expiration date to prevent C++ engine trial calculation");
    }

    [Fact]
    public void UpgradesResolutionService_ProgressionMode_Versus_SandboxMode()
    {
        // Arrange
        List<string> inventory = ["Hero_Pyromancer.Alt", "aa.Hero_WitchSlayer.Alt"];

        // Act - Sandbox Mode (unlockAllItems: true)
        HashSet<string> sandboxUpgrades = UpgradesResolutionService.ExpandOwnedItems(inventory, unlockAllItems: true);

        // Act - Progression Mode (unlockAllItems: false)
        HashSet<string> progressionUpgrades = UpgradesResolutionService.ExpandOwnedItems(inventory, unlockAllItems: false);

        // Assert - Sandbox Mode includes inventory, defaults, AND all store / mod items
        sandboxUpgrades.Should().Contain("aa.Hero_Rampage.Alt16");
        sandboxUpgrades.Should().Contain("aa.Hero_Aluna.Alt6");
        sandboxUpgrades.Should().Contain("aa.Hero_Pyromancer.Alt");
        sandboxUpgrades.Should().Contain("m.allmodes.pass");

        // Assert - Progression Mode includes inventory, defaults, and mode passes, but NOT unowned store / mod items
        progressionUpgrades.Should().Contain("aa.Hero_Pyromancer.Alt");
        progressionUpgrades.Should().Contain("Hero_Pyromancer.Alt");
        progressionUpgrades.Should().Contain("aa.Hero_WitchSlayer.Alt");
        progressionUpgrades.Should().Contain("Hero_WitchSlayer.Alt");
        progressionUpgrades.Should().Contain("m.allmodes.pass");
        progressionUpgrades.Should().Contain(StoreDefaults.DefaultTaunt);

        progressionUpgrades.Should().NotContain("aa.Hero_Rampage.Alt16", "Progression mode must not auto-grant unowned mod avatars");
        progressionUpgrades.Should().NotContain("aa.Hero_Aluna.Alt6", "Progression mode must not auto-grant supplementary mod avatars");
        progressionUpgrades.Should().NotContain("Hero_DwarfMagi.Alt8", "Progression mode must not auto-grant unowned store items");
    }

    [Fact]
    public async Task UpgradesResolutionService_ResolveExpandedOwnedItemsAsync_HonorsConfigurationProgressionMode()
    {
        // Arrange
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "false"
            })
            .Build();

        UpgradesResolutionService service = new(
            NullLogger<UpgradesResolutionService>.Instance,
            storeRepository: null,
            configuration: configuration);

        (Account account, _) = CreateTestAccount();

        // Act
        HashSet<string> upgrades = await service.ResolveExpandedOwnedItemsAsync(account, CancellationToken.None);

        // Assert
        upgrades.Should().Contain("aa.Hero_Pyromancer.Alt");
        upgrades.Should().Contain(StoreDefaults.DefaultTaunt);
        upgrades.Should().NotContain("aa.Hero_Rampage.Alt16");
        upgrades.Should().NotContain("Hero_DwarfMagi.Alt8");
    }

    [Fact]
    public async Task UpgradesResolutionService_ResolveExpandedOwnedItemsAsync_HonorsConfigurationSandboxMode()
    {
        // Arrange
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "true"
            })
            .Build();

        UpgradesResolutionService service = new(
            NullLogger<UpgradesResolutionService>.Instance,
            storeRepository: null,
            configuration: configuration);

        (Account account, _) = CreateTestAccount();

        // Act
        HashSet<string> upgrades = await service.ResolveExpandedOwnedItemsAsync(account, CancellationToken.None);

        // Assert
        upgrades.Should().Contain("aa.Hero_Pyromancer.Alt");
        upgrades.Should().Contain("aa.Hero_Rampage.Alt16");
        upgrades.Should().Contain("Hero_DwarfMagi.Alt8");
    }
}

