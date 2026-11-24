// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Store;

/// <summary>
///     Guard rail isolating modern normalized inventory lookups (core.UserInventory)
///     to dedicated canary pilot accounts without risking live accounts or legacy data.
/// </summary>
internal static class InventoryCanaryGate
{
    /// <summary>
    ///     Master killswitch for the canary pipeline. When false, 100% of requests
    ///     fall back to the legacy dual-read behavior.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    ///     Designated pilot account name.
    /// </summary>
    public const string CanaryPilotAccountName = "CanaryPilotUser";

    /// <summary>
    ///     Determines whether an account is enrolled in the zero-risk normalized inventory pilot.
    /// </summary>
    public static bool IsCanaryAccount(int accountId, string? accountName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        return string.Equals(accountName.Trim(), CanaryPilotAccountName, StringComparison.OrdinalIgnoreCase);
    }
}
