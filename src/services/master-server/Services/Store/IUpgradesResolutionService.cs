// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;

namespace KONGOR.MasterServer.Services.Store;

/// <summary>
///     Provides authoritative resolution and dual-key expansion of owned store items and upgrades
///     for both game client sessions and dedicated game servers.
/// </summary>
public interface IUpgradesResolutionService
{
    /// <summary>
    ///     Gets whether sandbox unlock-all mode is enabled for the current scoped configuration.
    /// </summary>
    bool IsUnlockAllEnabled => UpgradesResolutionService.UnlockAllItems;

    /// <summary>
    ///     Resolves all canonical defaults, database-owned items, and custom mod content (ID &gt;= 6000)
    ///     into an expanded set containing both prefixed (e.g. "aa.Hero_Rampage.Alt16") and bare codes.
    /// </summary>
    Task<HashSet<string>> ResolveExpandedOwnedItemsAsync(Account account, CancellationToken cancellationToken = default);
}
