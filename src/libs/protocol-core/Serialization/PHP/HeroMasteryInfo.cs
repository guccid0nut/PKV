// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

public class HeroMasteryInfo
{
    [PHPProperty("heroname")] public required string HeroName { get; init; }

    [PHPProperty("exp")] public int Experience { get; init; }
}

public class MasteryRewardTier
{
    [PHPProperty("level")] public required int Level { get; init; }

    [PHPProperty("alreadygot")] public bool AlreadyClaimed { get; init; }

    [PHPProperty("reward")] public required MasteryRewardModel Reward { get; init; }
}

public class MasteryRewardModel
{
    [PHPProperty("product_id")] public int ProductID { get; init; }

    [PHPProperty("product_name")] public string ProductName { get; init; } = string.Empty;

    [PHPProperty("product_local_content")] public string ProductLocalContent { get; init; } = string.Empty;

    [PHPProperty("quantity")] public int Quantity { get; init; }

    [PHPProperty("points")] public int GoldCoins { get; init; }

    [PHPProperty("mmpoints")] public int SilverCoins { get; init; }

    [PHPProperty("tickets")] public int GameTokens { get; init; }
}
