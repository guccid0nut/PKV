// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations.Statistics;

namespace ASPIRE.Tests.ASPIRE.Common;

public class RankExtensionsTests
{
    [Theory]
    [InlineData(1950, Rank.IMMORTAL)]
    [InlineData(1900, Rank.LEGENDARY_1)]
    [InlineData(1850, Rank.LEGENDARY_2)]
    [InlineData(1805, Rank.DIAMOND_1)]
    [InlineData(1765, Rank.DIAMOND_2)]
    [InlineData(1725, Rank.DIAMOND_3)]
    [InlineData(1685, Rank.GOLD_1)]
    [InlineData(1645, Rank.GOLD_2)]
    [InlineData(1610, Rank.GOLD_3)]
    [InlineData(1575, Rank.GOLD_4)]
    [InlineData(1540, Rank.SILVER_1)]
    [InlineData(1505, Rank.SILVER_2)]
    [InlineData(1470, Rank.SILVER_3)]
    [InlineData(1435, Rank.SILVER_4)]
    [InlineData(1400, Rank.SILVER_5)]
    [InlineData(1360, Rank.BRONZE_1)]
    [InlineData(1330, Rank.BRONZE_2)]
    [InlineData(1300, Rank.BRONZE_3)]
    [InlineData(1275, Rank.BRONZE_4)]
    [InlineData(1250, Rank.BRONZE_5)]
    [InlineData(1249, Rank.NO_MEDAL)]
    [InlineData(0, Rank.NO_MEDAL)]
    public void GetRank_ReturnsExpectedRank_ForGivenRating(double rating, Rank expectedRank)
    {
        // Act
        Rank result = RankExtensions.GetRank(rating);

        // Assert
        Assert.Equal(expectedRank, result);
    }
}
