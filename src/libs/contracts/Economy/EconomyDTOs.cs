// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Economy;

/// <summary>
///     Represents currency delta values (Gold, Silver, Plinko Tickets).
/// </summary>
public record CurrencyDeltaDTO(
    int GoldDelta,
    int SilverDelta,
    int PlinkoDelta
);

/// <summary>
///     Current wallet snapshot for a user.
/// </summary>
public record CurrencyBalancesDTO(
    int UserId,
    int GoldCoins,
    int SilverCoins,
    int PlinkoTickets
);

/// <summary>
///     Request to compute and award match completion rewards for a participant.
/// </summary>
public record MatchRewardRequestDTO(
    int UserId,
    int AccountId,
    string AccountName,
    int MatchId,
    int GroupSize,
    bool IsWin,
    bool IsMvp,
    int DurationSeconds
);

/// <summary>
///     Result of an awarded match reward operation.
/// </summary>
public record MatchRewardResultDTO(
    int UserId,
    int AccountId,
    string AccountName,
    int MatchId,
    CurrencyDeltaDTO CurrenciesEarned,
    CurrencyBalancesDTO BalancesAfter,
    bool Success,
    string? ErrorMessage = null
);

/// <summary>
///     Generic balance adjustment outcome.
/// </summary>
public record CurrencyOperationResultDTO(
    int UserId,
    int? AccountId,
    CurrencyDeltaDTO DeltaApplied,
    CurrencyBalancesDTO BalancesAfter,
    bool Success,
    string? ErrorMessage = null
);
