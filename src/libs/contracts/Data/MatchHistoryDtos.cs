// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Data;

public class HeroUsageAggregateDTO
{
    public required string HeroName { get; set; }
    public int TotalUse { get; set; }
    public int TotalWins { get; set; }
    public int TotalLosses { get; set; }
}

public class HeroBanAggregateDTO
{
    public required string HeroIdentifier { get; set; }
    public int BanCount { get; set; }
    public double BanRate { get; set; }
}

public class PlayerMatchRecordDTO
{
    public int MatchID { get; init; }
    public int Team { get; init; }
    public int HeroKills { get; init; }
    public int HeroDeaths { get; init; }
    public int HeroAssists { get; init; }
    public uint? HeroProductID { get; init; }
    public string? HeroIdentifier { get; init; }
    public uint? AlternativeAvatarProductID { get; init; }
    public string? AlternativeAvatarName { get; init; }
    public int Win { get; init; }
    public int RankedMatch { get; init; }
    public int PublicMatch { get; init; }
    public string Map { get; init; } = "caldavar";
    public string GameMode { get; init; } = "caldavar";
    public DateTimeOffset TimestampRecorded { get; init; } = TimeProvider.System.GetUtcNow();
    public int TimePlayed { get; init; } = 1800;
    public string FileName { get; init; } = string.Empty;
    public int HeroLevel { get; init; }
    public int Gold { get; init; }
    public int Experience { get; init; }
    public int WardsPlaced { get; init; }
}

public class HeroItemPopularityDTO
{
    public required string HeroIdentifier { get; set; }
    public required string ItemIdentifier { get; set; }
    public int MatchesCount { get; set; }
    public int WinsCount { get; set; }
    public double WinRate => MatchesCount > 0 ? (double)WinsCount / MatchesCount : 0.0;
}

public class HeroItemTimingDTO
{
    public required string HeroIdentifier { get; set; }
    public required string ItemIdentifier { get; set; }
    public int PurchasesCount { get; set; }
    public double AverageGameTimeSeconds { get; set; }
    public int FastestGameTimeSeconds { get; set; }
}

public class HeroSkillBuildWinRateDTO
{
    public required string HeroIdentifier { get; set; }
    public required string AbilityIdentifier { get; set; }
    public byte SlotIndex { get; set; }
    public byte Level { get; set; }
    public int PickCount { get; set; }
    public int WinsCount { get; set; }
    public double WinRate => PickCount > 0 ? (double)WinsCount / PickCount : 0.0;
}

public class PlayerRivalryDTO
{
    public int AccountID { get; set; }
    public int OpponentAccountID { get; set; }
    public int KillsOnOpponent { get; set; }
    public int DeathsToOpponent { get; set; }
}

public class ItemRecipeNodeDTO
{
    public required string ItemCode { get; set; }
    public required string Name { get; set; }
    public int Cost { get; set; }
    public int RecipeCost { get; set; }
    public List<ItemRecipeComponentDTO> Components { get; set; } = [];
}

public class ItemRecipeComponentDTO
{
    public required string ComponentItemCode { get; set; }
    public int Quantity { get; set; }
    public ItemRecipeNodeDTO? SubRecipe { get; set; }
}
