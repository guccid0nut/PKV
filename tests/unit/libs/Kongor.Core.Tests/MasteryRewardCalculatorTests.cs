// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Serialization.PHP;
using ASPIRE.Common.Services.Mastery;
using FluentAssertions;
using NEXUS.Contracts.Services;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class MasteryRewardCalculatorTests
{
    private readonly MasteryRewardsConfiguration _testConfig = new()
    {
        MasteryRewards =
        [
            new MasteryReward
            {
                RequiredLevel = 1,
                ProductIdentifier = 100,
                ProductCode = "test_item_1",
                ProductName = "Level 1 Item",
                ProductLocalResource = "/icons/1.tga",
                ProductQuantity = 1,
                GoldCoins = 0,
                SilverCoins = 50,
                PlinkoTickets = 1
            },
            new MasteryReward
            {
                RequiredLevel = 5,
                ProductIdentifier = 105,
                ProductCode = "test_item_5",
                ProductName = "Level 5 Item",
                ProductLocalResource = "/icons/5.tga",
                ProductQuantity = 2,
                GoldCoins = 25,
                SilverCoins = 100,
                PlinkoTickets = 5
            }
        ]
    };

    [Fact]
    public void EvaluateClaim_WithValidUnclaimedTier_ReturnsSuccess()
    {
        List<string> ownedItems = ["some_other_item"];
        MasteryClaimResult result = MasteryRewardCalculator.EvaluateClaim(_testConfig, 5, ownedItems);

        result.Status.Should().Be(MasteryClaimStatus.Success);
        result.Level.Should().Be(5);
        result.ProductCode.Should().Be("test_item_5");
        result.ProductQuantity.Should().Be(2);
        result.GoldCoins.Should().Be(25);
        result.SilverCoins.Should().Be(100);
        result.PlinkoTickets.Should().Be(5);
        result.ClaimKey.Should().Be("mastery_reward_lv_5");
    }

    [Fact]
    public void EvaluateClaim_WhenClaimTokenAlreadyPresent_ReturnsAlreadyClaimed()
    {
        List<string> ownedItems = ["mastery_reward_lv_5"];
        MasteryClaimResult result = MasteryRewardCalculator.EvaluateClaim(_testConfig, 5, ownedItems);

        result.Status.Should().Be(MasteryClaimStatus.AlreadyClaimed);
        result.ErrorMessage.Should().Be("Reward already claimed");
    }

    [Fact]
    public void EvaluateClaim_WhenProductCodeAlreadyPresent_ReturnsAlreadyClaimed()
    {
        List<string> ownedItems = ["test_item_5"];
        MasteryClaimResult result = MasteryRewardCalculator.EvaluateClaim(_testConfig, 5, ownedItems);

        result.Status.Should().Be(MasteryClaimStatus.AlreadyClaimed);
    }

    [Fact]
    public void EvaluateClaim_WhenLevelNotFound_ReturnsLevelNotFound()
    {
        List<string> ownedItems = [];
        MasteryClaimResult result = MasteryRewardCalculator.EvaluateClaim(_testConfig, 99, ownedItems);

        result.Status.Should().Be(MasteryClaimStatus.LevelNotFound);
    }

    [Fact]
    public void EvaluateClaim_WhenConfigNullOrEmpty_ReturnsLevelNotFound()
    {
        MasteryClaimResult result = MasteryRewardCalculator.EvaluateClaim(null, 5, []);
        result.Status.Should().Be(MasteryClaimStatus.LevelNotFound);

        MasteryClaimResult result2 =
            MasteryRewardCalculator.EvaluateClaim(new MasteryRewardsConfiguration { MasteryRewards = [] }, 5, []);
        result2.Status.Should().Be(MasteryClaimStatus.LevelNotFound);
    }

    [Fact]
    public void ApplyClaim_WhenSuccessful_UpdatesItemsAndBalances()
    {
        MasteryClaimResult claim = new(
            MasteryClaimStatus.Success,
            5,
            "mastery_reward_lv_5",
            "test_item_5",
            2,
            25,
            100,
            5);

        List<string> owned = ["existing_item"];
        int gold = 10;
        int silver = 20;
        int plinko = 1;

        bool applied = MasteryRewardCalculator.ApplyClaim(claim, owned, ref gold, ref silver, ref plinko);

        applied.Should().BeTrue();
        owned.Should().Contain("mastery_reward_lv_5");
        owned.Should().Contain("test_item_5");
        owned.FindAll(x => x == "test_item_5").Should().HaveCount(2);
        gold.Should().Be(35);
        silver.Should().Be(120);
        plinko.Should().Be(6);
    }

    [Fact]
    public void ApplyClaim_WhenNotSuccess_ReturnsFalseWithoutModifyingState()
    {
        MasteryClaimResult claim = new(
            MasteryClaimStatus.AlreadyClaimed,
            5,
            "mastery_reward_lv_5",
            "test_item_5",
            2,
            25,
            100,
            5);

        List<string> owned = ["existing_item"];
        int gold = 10;
        int silver = 20;
        int plinko = 1;

        bool applied = MasteryRewardCalculator.ApplyClaim(claim, owned, ref gold, ref silver, ref plinko);

        applied.Should().BeFalse();
        owned.Should().HaveCount(1);
        gold.Should().Be(10);
        silver.Should().Be(20);
        plinko.Should().Be(1);
    }

    [Fact]
    public void MasteryRewardCalculator_CalculateMatchMasteryExperience_CalculatesExpectedExperience()
    {
        // Level 10 hero (base = 10*20 = 200), win (+50), MVP (+50), full match duration 1800s -> 300
        int exp = MasteryRewardCalculator.CalculateMatchMasteryExperience(
            10,
            true,
            true,
            1800);

        exp.Should().Be(300);

        // Short match duration 450s (factor 450/900 = 0.5) -> 300 * 0.5 = 150
        int shortExp = MasteryRewardCalculator.CalculateMatchMasteryExperience(
            10,
            true,
            true,
            450);

        shortExp.Should().Be(150);
    }

    [Fact]
    public void MasteryRewardCalculator_GenerateMasteryInfo_CorrectlyMapsPlayedHeroes()
    {
        List<FavHeroDTO> favHeroes =
        [
            new() { HeroId = 1, SecondsPlayed = 100 }
        ];

        List<string> allHeroes = ["Hero_Test1", "Hero_Test2"];
        List<HeroMasteryInfo> info = MasteryRewardCalculator.GenerateMasteryInfo(
            favHeroes,
            allHeroes,
            id => id == 1 ? "Hero_Test1" : "");

        info.Should().HaveCount(2);
        info.Should().ContainSingle(h => h.HeroName == "Hero_Test1" && h.Experience == 500);
        info.Should().ContainSingle(h => h.HeroName == "Hero_Test2" && h.Experience == 0);
    }

    [Fact]
    public void MasteryRewardCalculator_GenerateMasteryRewards_MapsTiersWithClaimedFlags()
    {
        List<MasteryRewardTierDTO> tiers =
        [
            new(1, 101, "item_1", "Item 1", "/path1.tga", 1, 10, 20, 1),
            new(2, 102, "item_2", "Item 2", "/path2.tga", 1, 20, 40, 2)
        ];

        List<string> ownedItems = ["mastery_reward_lv_1"];

        List<MasteryRewardTier> result = MasteryRewardCalculator.GenerateMasteryRewards(tiers, ownedItems);

        result.Should().HaveCount(2);
        result[0].Level.Should().Be(1);
        result[0].AlreadyClaimed.Should().BeTrue();
        result[1].Level.Should().Be(2);
        result[1].AlreadyClaimed.Should().BeFalse();
    }

    [Fact]
    public void MasteryRewardCalculator_GenerateMasteryRewardsString_GeneratesValidCsvString()
    {
        List<MasteryRewardTierDTO> tiers =
        [
            new(1, 101, "item_1", "Item 1", "/path1.tga", 1, 10, 20, 1)
        ];

        string csv = MasteryRewardCalculator.GenerateMasteryRewardsString(tiers, ["mastery_reward_lv_1"]);
        csv.Should().Be("1,true,101,Item 1,/path1.tga,1,10,20,1");
    }
}
