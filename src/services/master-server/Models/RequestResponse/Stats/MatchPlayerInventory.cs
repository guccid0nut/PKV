// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

public partial class MatchPlayerInventory
{
    /// <summary>
    ///     The player's account ID.
    /// </summary>
    [PHPProperty("account_id")]
    public required int AccountID { get; init; }

    /// <summary>
    ///     The unique identifier for the match.
    /// </summary>
    [PHPProperty("match_id")]
    public required int MatchID { get; init; }

    /// <summary>
    ///     Item in slot 1 (Top Left), or NULL if the slot is empty.
    /// </summary>
    [PHPProperty("slot_1")]
    public required string? Slot1 { get; init; }

    /// <summary>
    ///     Item in slot 2 (Top Center), or NULL if the slot is empty.
    /// </summary>
    [PHPProperty("slot_2")]
    public required string? Slot2 { get; init; }

    /// <summary>
    ///     Item in slot 3 (Top Right), or NULL if the slot is empty.
    /// </summary>
    [PHPProperty("slot_3")]
    public required string? Slot3 { get; init; }

    /// <summary>
    ///     Item in slot 4 (Bottom Left), or NULL if the slot is empty.
    /// </summary>
    [PHPProperty("slot_4")]
    public required string? Slot4 { get; init; }

    /// <summary>
    ///     Item in slot 5 (Bottom Center), or NULL if the slot is empty.
    /// </summary>
    [PHPProperty("slot_5")]
    public required string? Slot5 { get; init; }

    /// <summary>
    ///     Item in slot 6 (Bottom Right), or NULL if the slot is empty.
    /// </summary>
    [PHPProperty("slot_6")]
    public required string? Slot6 { get; init; }
}
