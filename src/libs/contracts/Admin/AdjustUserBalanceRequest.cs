// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Request payload to increment or decrement user balances by specified amounts.
/// </summary>
public sealed class AdjustUserBalanceRequest
{
    public int GoldDelta { get; set; }
    public int SilverDelta { get; set; }
    public int TicketsDelta { get; set; }
}
