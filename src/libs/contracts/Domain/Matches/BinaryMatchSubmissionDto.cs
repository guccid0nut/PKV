// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.Domain.Matches;

/// <summary>
///     High-performance zero-allocation binary DTO representing per-player final match statistics.
/// </summary>
[MemoryPackable]
public partial record PlayerMatchSummaryDto
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    public required int Team { get; init; } // 1 = Legion, 2 = Hellbourne
    public required string HeroKey { get; init; }
    public int Level { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int CreepKills { get; init; }
    public int CreepDenies { get; init; }
    public int NeutralKills { get; init; }
    public int GoldPerMinute { get; init; }
    public int ExpPerMinute { get; init; }
    public int TotalGold { get; init; }
    public int TotalDamageHero { get; init; }
    public int TotalDamageBuilding { get; init; }
    public int WardsPlaced { get; init; }
    public int GroupNum { get; init; }
    public int GroupSize { get; init; }
    public bool IsMvp { get; init; }
    public string[] InventoryItemKeys { get; init; } = [];
    public int SecondsPlayed { get; init; }
    public double[] GameplayStats { get; init; } = [];
}

/// <summary>
///     High-performance zero-allocation binary DTO representing discrete in-game combat timeline events.
/// </summary>
[MemoryPackable]
public partial record CombatTimelineEventDto
{
    public required uint GameTick { get; init; }
    public required byte EventType { get; init; } // 1 = Kill, 2 = Assist, 3 = TowerKill, 4 = BossKill, 5 = ItemPurchased
    public required int SourceAccountId { get; init; }
    public required int TargetAccountId { get; init; }
    public required string Identifier { get; init; } // Hero/Item/Ability/Building key
    public int Value { get; init; }
    public float PositionX { get; init; }
    public float PositionY { get; init; }
}

/// <summary>
///     High-throughput binary match submission payload ingested via <c>/api/v5/matches/submit</c>.
///     Designed for sub-millisecond zero-GC deserialization via <see cref="MemoryPackSerializer"/>.
/// </summary>
[MemoryPackable]
public partial record MatchFinalizationPayload
{
    public required int MatchId { get; init; }
    public required string MapName { get; init; }
    public required uint DurationSeconds { get; init; }
    public required byte WinningTeam { get; init; } // 1 = Legion, 2 = Hellbourne, 0 = Draw/Aborted
    public int ServerId { get; init; }
    public string? ServerAddress { get; init; }
    public string? HostAccountName { get; init; }
    public AccountStatisticsType MatchType { get; init; } = AccountStatisticsType.Matchmaking;
    public required PlayerMatchSummaryDto[] Players { get; init; }
    public CombatTimelineEventDto[] CombatTimeline { get; init; } = [];
    public byte[]? ReplayChecksumSha256 { get; init; }
}

/// <summary>
///     Binary receipt returned to the dedicated server upon successful match ingestion.
/// </summary>
[MemoryPackable]
public partial record MatchFinalizationReceipt
{
    public required int MatchId { get; init; }
    public required bool Success { get; init; }
    public required DateTimeOffset FinalizedAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>
///     Distributed matchmaking ticket serialized to Redis Streams across multi-node matchmaker clusters.
/// </summary>
[MemoryPackable]
public partial record MatchmakingTicketDto
{
    public required string TicketId { get; init; }
    public required int GroupGuid { get; init; }
    public required string Region { get; init; }
    public required string GameType { get; init; }
    public required string MapName { get; init; }
    public required int PlayerCount { get; init; }
    public required double AverageRating { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public required int[] MemberAccountIds { get; init; }
}
