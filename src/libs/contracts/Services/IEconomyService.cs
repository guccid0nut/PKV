// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Economy;

namespace NEXUS.Contracts.Services;

/// <summary>
///     Domain service contract defining currency reward calculations, user wallet mutations,
///     match reward distribution, and audit ledger persistence across ProjectKongor ecosystem.
/// </summary>
public interface IEconomyService
{
    /// <summary>
    ///     Calculates match currency rewards based on party group size, win/loss status,
    ///     MVP performance, and match duration according to economy configuration rules.
    /// </summary>
    CurrencyDeltaDTO CalculateCurrencyRewards(
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds);

    /// <summary>
    ///     Awards match rewards to a single user/account, updating wallet balances
    ///     and recording an EconomyTransaction ledger entry in the database.
    /// </summary>
    Task<MatchRewardResultDTO> AwardMatchRewardsAsync(
        MatchRewardRequestDTO request,
        CancellationToken ct = default);

    /// <summary>
    ///     Awards match rewards for a batch of players in a completed match.
    /// </summary>
    Task<IReadOnlyList<MatchRewardResultDTO>> AwardMatchRewardsBatchAsync(
        int matchId,
        IEnumerable<MatchRewardRequestDTO> requests,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves current currency balances (Gold, Silver, Plinko Tickets) for a user.
    /// </summary>
    Task<CurrencyBalancesDTO?> GetUserBalancesAsync(
        int userId,
        CancellationToken ct = default);

    /// <summary>
    ///     Applies a debit/credit currency change to a user's wallet with ledger auditing.
    /// </summary>
    Task<CurrencyOperationResultDTO> AdjustBalanceAsync(
        int userId,
        int? accountId,
        CurrencyDeltaDTO delta,
        string transactionType,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Pure calculation method for match reward deltas (backward compatibility).
    /// </summary>
    CurrencyRewardCalculation CalculateMatchRewards(
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds);

    /// <summary>
    ///     Atomically calculates and awards match completion rewards to a user's wallet and records the ledger transaction (backward compatibility).
    /// </summary>
    Task<CurrencyRewardResult> AwardMatchRewardsAsync(
        int userId,
        int accountId,
        int matchId,
        int groupSize,
        bool isWin,
        bool isMvp,
        int durationSeconds,
        CancellationToken ct = default);

    /// <summary>
    ///     Records an arbitrary ledger transaction and adjusts the user's balances atomically (backward compatibility).
    /// </summary>
    Task<CurrencyRewardResult> RecordTransactionAsync(
        int userId,
        int? accountId,
        int goldDelta,
        int silverDelta,
        int plinkoDelta,
        string transactionType,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Deducts currencies from a user's wallet with balance validation and optimistic concurrency retry handling (backward compatibility).
    /// </summary>
    Task<CurrencyRewardResult> DeductCurrenciesAsync(
        int userId,
        int? accountId,
        int goldCost,
        int silverCost,
        int plinkoCost,
        string transactionType,
        string? referenceId = null,
        string? description = null,
        CancellationToken ct = default);
}
