// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Mastery;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Store;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class StoreConfigurationTests

{
    [Fact]
    public void LoadConfiguration_LoadStoreConfig_ReturnsValidItems()

    {
        // Act - Trigger static constructor loading

        StoreItemsConfiguration config = JSONConfiguration.StoreItemsConfiguration;


        // Assert

        Assert.NotNull(config);

        Assert.NotNull(config.StoreItems);

        Assert.NotEmpty(config.StoreItems);


        // Verify a known item from the json (ID 15: Female Pyromancer)

        StoreItem? pyromancer = config.StoreItems.FirstOrDefault(x => x.ID == 15);

        Assert.NotNull(pyromancer);

        Assert.Equal("Female Pyromancer", pyromancer.Name);

        Assert.Equal(400, pyromancer.GoldCost);
    }


    [Fact]
    public void LoadConfiguration_LoadMasteryConfig_HandlesNulls()

    {
        // Act

        MasteryRewardsConfiguration config = JSONConfiguration.MasteryRewardsConfiguration;


        // Assert

        Assert.NotNull(config);

        Assert.NotNull(config.MasteryRewards);


        // Find a level with empty rewards (e.g. Level 3 was changed to nulls)

        MasteryReward? level3 = config.MasteryRewards.FirstOrDefault(x => x.RequiredLevel == 3);

        Assert.NotNull(level3);


        // Verify null modernization

        Assert.Null(level3.ProductName);

        Assert.Null(level3.ProductCode);

        Assert.Null(level3.ProductLocalResource);


        // Find a level with rewards (e.g. Level 0/1)

        MasteryReward? level1 = config.MasteryRewards.FirstOrDefault(x => x.RequiredLevel is 0 or 1);

        Assert.NotNull(level1);

        Assert.Equal("Mastery Boost", level1.ProductName);
    }


    [Fact]
    public void LoadConfiguration_VerifyTauntsAndAnnouncers_ResolvesCorrectly()

    {
        StoreItemsConfiguration config = JSONConfiguration.StoreItemsConfiguration;


        List<StoreItem> taunts = [.. config.StoreItems.Where(x => x.StoreItemType == StoreItemType.Taunt)];

        List<StoreItem> announcers =

            [.. config.StoreItems.Where(x => x.StoreItemType == StoreItemType.AnnouncerVoice)];


        Assert.NotEmpty(taunts);

        Assert.NotEmpty(announcers);
    }
}
