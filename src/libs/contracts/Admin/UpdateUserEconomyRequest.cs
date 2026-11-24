// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Request payload to set absolute economy currency values for a user.
/// </summary>
public sealed class UpdateUserEconomyRequest
{
    public int GoldCoins { get; set; }
    public int SilverCoins { get; set; }
    public int PlinkoTickets { get; set; }
}
