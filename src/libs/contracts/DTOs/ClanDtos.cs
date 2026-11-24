// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Domain;

namespace NEXUS.Contracts.DTOs;

/// <summary>
///     Aggregated public summary of an active Newerth clan for directory standings and global search.
/// </summary>
public sealed record ClanSummaryDto(
    int Id,
    string Name,
    string Tag,
    DateTimeOffset TimestampCreated,
    int MemberCount,
    int OfficerCount,
    string? LeaderName,
    double? AverageMmr,
    int RankedMemberCount,
    bool IsCompetitiveQualified,
    double CombinedWinRate,
    int CombinedMemberMatches,
    int CombinedMemberWins,
    string ActivityBucket
);

/// <summary>
///     Paginated clan directory response with portal-wide summary KPIs.
/// </summary>
public sealed record ClanDirectoryResultDto(
    IReadOnlyList<ClanSummaryDto> Items,
    int TotalCount,
    int Page,
    int TotalPages,
    int TotalActiveMembers,
    double? HighestQualifiedAvgMmr
);

/// <summary>
///     Public roster entry for an active member of a clan.
///     Uses coarse activity buckets and omits Discord handles for roster privacy.
/// </summary>
public sealed record ClanMemberDto(
    int AccountId,
    string Name,
    ClanTier Tier,
    DateTimeOffset? TimestampJoinedClan,
    string ActivityBucket,
    int ActivitySortOrder,
    double? SkillRating,
    string RankName,
    int MatchesPlayed,
    int MatchesWon,
    double WinRate,
    string AvatarUrl
);

/// <summary>
///     Complete public detail view of a clan, combining its summary and ordered active member roster.
/// </summary>
public sealed record ClanDetailDto(
    ClanSummaryDto Summary,
    IReadOnlyList<ClanMemberDto> Members
);
