// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ASPIRE.Common.Services.Mastery;

/// <summary>
///     Represents a mastery tier reward configuration item.
/// </summary>
public class MasteryReward
{
    public required int RequiredLevel { get; set; }

    public required int ProductIdentifier { get; set; }

    public required int ProductQuantity { get; set; }

    public required int GoldCoins { get; set; }

    public required int SilverCoins { get; set; }

    public required int PlinkoTickets { get; set; }

    public required string? ProductName { get; set; }

    public required string? ProductCode { get; set; }

    public required string? ProductLocalResource { get; set; }
}

/// <summary>
///     Configuration root for mastery reward tiers.
/// </summary>
public class MasteryRewardsConfiguration
{
    public required List<MasteryReward> MasteryRewards { get; set; }
}
