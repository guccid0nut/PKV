// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Economy;

/// <summary>
/// Result DTO encapsulating the outcome of a match reward distribution or economy ledger mutation.
/// </summary>
public record CurrencyRewardResult
{
    public bool Success { get; init; }
    public int GoldEarned { get; init; }
    public int SilverEarned { get; init; }
    public int PlinkoTicketsEarned { get; init; }
    public int GoldBalanceAfter { get; init; }
    public int SilverBalanceAfter { get; init; }
    public int PlinkoTicketsBalanceAfter { get; init; }
    public string? ErrorMessage { get; init; }

    public static CurrencyRewardResult Failed(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };

    public static CurrencyRewardResult Succeeded(
        int goldEarned,
        int silverEarned,
        int plinkoEarned,
        int goldAfter,
        int silverAfter,
        int plinkoAfter) => new()
    {
        Success = true,
        GoldEarned = goldEarned,
        SilverEarned = silverEarned,
        PlinkoTicketsEarned = plinkoEarned,
        GoldBalanceAfter = goldAfter,
        SilverBalanceAfter = silverAfter,
        PlinkoTicketsBalanceAfter = plinkoAfter
    };
}

/// <summary>
/// Pure immutable calculation record for currency reward deltas.
/// </summary>
public record CurrencyRewardCalculation(int GoldEarned, int SilverEarned, int PlinkoTicketsEarned);
