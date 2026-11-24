// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;

namespace NEXUS.Contracts.Domain.Identifiers;

/// <summary>
///     High-performance zero-allocation cache key builders for strongly typed domain identifiers.
/// </summary>
public static class CacheKeyExtensions
{
    public static string ToAccountSessionKey(this AccountId id) =>
        string.Create(CultureInfo.InvariantCulture, $"ACC-SESSION:{id.Value}");

    public static string ToAccountStatsKey(this AccountId id) =>
        string.Create(CultureInfo.InvariantCulture, $"hero:stats:account:{id.Value}");

    public static string ToAccountTag(this AccountId id) =>
        string.Create(CultureInfo.InvariantCulture, $"account:{id.Value}");

    public static string ToMatchKey(this MatchId id) =>
        string.Create(CultureInfo.InvariantCulture, $"match:{id.Value}");

    public static string ToClanTag(this ClanId id) =>
        string.Create(CultureInfo.InvariantCulture, $"clan:{id.Value}");

    public static string ToClanKey(this ClanId id) =>
        string.Create(CultureInfo.InvariantCulture, $"clan:roster:{id.Value}");

    public static string ToUserKey(this UserId id) =>
        string.Create(CultureInfo.InvariantCulture, $"user:{id.Value}");
}
