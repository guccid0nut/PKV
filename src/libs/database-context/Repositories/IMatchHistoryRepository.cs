// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Statistics;

namespace NEXUS.Contracts.Data;

public interface IMatchHistoryRepository
{
    // --- Existing Methods ---
    Task<MatchStatistics?> GetMatchByIdAsync(int matchId, CancellationToken ct = default);

    Task<IReadOnlyList<MatchStatistics>> GetRecentMatchesByAccountIdAsync(int accountId, int limit = 20,
        CancellationToken ct = default);

    Task SaveMatchAsync(MatchStatistics match, CancellationToken ct = default);
    Task<AccountStatistics?> GetAccountStatisticsAsync(int accountId, CancellationToken ct = default);

    Task<IReadOnlyList<AccountStatistics>> GetAccountStatisticsByIdsAsync(IEnumerable<int> accountIds,
        CancellationToken ct = default);

    Task UpdateAccountStatisticsAsync(AccountStatistics stats, CancellationToken ct = default);
    Task<IReadOnlyList<PlayerStatistics>> GetPlayerStatisticsForMatchAsync(int matchId, CancellationToken ct = default);
    Task<PlayerStatistics?> GetPlayerStatisticsAsync(int matchId, int accountId, CancellationToken ct = default);

    Task<IReadOnlyList<PlayerStatistics>> GetRecentPlayerStatisticsByAccountIdAsync(int accountId, int limit = 20,
        CancellationToken ct = default);

    Task SavePlayerStatisticsAsync(PlayerStatistics playerStats, CancellationToken ct = default);
    Task SavePlayerStatisticsBatchAsync(IEnumerable<PlayerStatistics> playerStatsBatch, CancellationToken ct = default);
    Task<Mastery?> GetMasteryByAccountIdAsync(int accountId, CancellationToken ct = default);
    Task SaveMasteryAsync(Mastery mastery, CancellationToken ct = default);
    Task<IReadOnlyList<AccountStatistics>> GetTopRankedAccountsAsync(int count = 100, CancellationToken ct = default);
    Task<IReadOnlyList<MatchStatistics>> GetRecentMatchesAsync(int limit = 50, CancellationToken ct = default);

    // --- Milestone 1 Expanded Methods ---

    /// <summary>
    /// Aggregates pick count, total wins, and total losses per hero identifier across all player statistics records.
    /// </summary>
    Task<IReadOnlyList<HeroUsageAggregateDTO>> GetHeroUsageAggregatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Aggregates total bans and ban rates per hero identifier from normalized MatchBans records.
    /// </summary>
    Task<IReadOnlyList<HeroBanAggregateDTO>> GetHeroBanRatesAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Fetches all PlayerStatistics rows matching an account ID, exact nickname, or clean nickname variant for award summary calculation.
    /// </summary>
    Task<IReadOnlyList<PlayerStatistics>> GetPlayerStatisticsForAwardSummaryAsync(int accountId, string accountName, string cleanName, CancellationToken ct = default);

    /// <summary>
    /// Fetches all PlayerStatistics rows for a collection of match IDs (e.g. for calculating per-match award maximums across participants).
    /// </summary>
    Task<IReadOnlyList<PlayerStatistics>> GetPlayerStatisticsByMatchIdsAsync(IEnumerable<int> matchIds, CancellationToken ct = default);

    /// <summary>
    /// Fetches MatchStatistics records for a collection of match IDs.
    /// </summary>
    Task<IReadOnlyList<MatchStatistics>> GetMatchesByIdsAsync(IEnumerable<int> matchIds, CancellationToken ct = default);

    /// <summary>
    /// Fetches the most recent match IDs where the specified account participated, ordered descending.
    /// </summary>
    Task<IReadOnlyList<int>> GetRecentMatchIdsByAccountAsync(int accountId, string? accountName = null, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Performs an joined query between PlayerStatistics and MatchStatistics for match history overview with table/season filtering and fallback.
    /// </summary>
    Task<IReadOnlyList<PlayerMatchRecordDTO>> GetPlayerMatchHistoryOverviewAsync(int accountId, string nickname, string? table, string? currentSeason, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Fetches all PlayerStatistics rows for an account and a specific hero identifier.
    /// </summary>
    Task<IReadOnlyList<PlayerStatistics>> GetPlayerHeroStatisticsAsync(int accountId, string heroIdentifier, CancellationToken ct = default);

    /// <summary>
    /// Fetches all PlayerStatistics rows for an account across all matches and heroes.
    /// </summary>
    Task<IReadOnlyList<PlayerStatistics>> GetAllPlayerStatisticsForAccountAsync(int accountId, string? accountName = null, CancellationToken ct = default);

    /// <summary>
    /// Updates the ReplayUrl and optional FileSize for a completed match. Returns true if match exists and was updated.
    /// </summary>
    Task<bool> UpdateMatchReplayUrlAsync(int matchId, string? replayUrl, int? fileSize = null, CancellationToken ct = default);

    /// <summary>
    /// Aggregates the most popular end-game item builds for a given hero from MatchPlayerInventories.
    /// </summary>
    Task<IReadOnlyList<HeroItemPopularityDTO>> GetTopItemBuildsByHeroAsync(string heroIdentifier, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Aggregates the average purchase timing in seconds for items purchased by a given hero from MatchItemEvents.
    /// </summary>
    Task<IReadOnlyList<HeroItemTimingDTO>> GetAverageItemTimingsAsync(string heroIdentifier, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Aggregates skill build win rates for a hero at a specific skill level or slot from MatchAbilityEvents.
    /// </summary>
    Task<IReadOnlyList<HeroSkillBuildWinRateDTO>> GetHeroSkillBuildWinRatesAsync(string heroIdentifier, int level = 1, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Computes rivalries (kills vs deaths against specific opponents) from MatchFrags.
    /// </summary>
    Task<IReadOnlyList<PlayerRivalryDTO>> GetPlayerRivalriesAndNemesisAsync(int accountId, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Fetches all normalized kill/frag events for a match with assists.
    /// </summary>
    Task<IReadOnlyList<MatchFrag>> GetMatchFragsAsync(int matchId, CancellationToken ct = default);
}
