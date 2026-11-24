// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Entities.Utility;

/// <summary>
///     A single-use promotional code that, when redeemed by an account, grants a combination of gold coins, silver coins,
///     Plinko tickets, and optionally a specific store product.
///     Codes are global single-use: once any account has redeemed a code, no other account can redeem it.
/// </summary>
[Index(nameof(Code), IsUnique = true)]
public class RedeemableCode
{
    [Key] public int ID { get; set; }

    /// <summary>
    ///     The code string entered by the player, stored in upper case to support case-insensitive lookup.
    /// </summary>
    [MaxLength(32)]
    public required string Code { get; set; }

    /// <summary>
    ///     The gold coin reward granted on redemption, or zero if no gold is awarded.
    /// </summary>
    public int GoldCoinsReward { get; set; }

    /// <summary>
    ///     The silver coin reward granted on redemption, or zero if no silver is awarded.
    /// </summary>
    public int SilverCoinsReward { get; set; }

    /// <summary>
    ///     The Plinko ticket reward granted on redemption, or zero if no tickets are awarded.
    ///     Tickets are granted silently because the client's redemption UI does not render a ticket reward card.
    /// </summary>
    public int PlinkoTicketsReward { get; set; }

    /// <summary>
    ///     The store product identifier granted on redemption, or <see langword="null" /> if the code is currency-only.
    /// </summary>
    public int? ProductID { get; set; }

    /// <summary>
    ///     The moment at which the code was created.
    /// </summary>
    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    /// <summary>
    ///     The moment at which the code was redeemed, or <see langword="null" /> if the code is still unused.
    /// </summary>
    public DateTimeOffset? TimestampRedeemed { get; set; }

    /// <summary>
    ///     The account that redeemed this code, or <see langword="null" /> if the code is still unused.
    /// </summary>
    public Account? RedeemedByAccount { get; set; }

    /// <summary>
    ///     The foreign key of the account that redeemed this code, or <see langword="null" /> if the code is still unused.
    /// </summary>
    public int? RedeemedByAccountID { get; set; }
}
