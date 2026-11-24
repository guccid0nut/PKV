// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Configuration.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.Extensions.Configuration;

namespace KONGOR.MasterServer.Services.Store;

/// <summary>
///     Authoritative resolution service that builds the full expanded set of owned upgrades
///     for a player account, guaranteeing client and dedicated server authorization parity
///     across canonical defaults, user inventory, and community mod content (ID &gt;= 6000).
/// </summary>
public class UpgradesResolutionService(
    ILogger<UpgradesResolutionService> logger,
    IStoreRepository? storeRepository = null,
    IConfiguration? configuration = null) : IUpgradesResolutionService
{
    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    private static volatile bool _globalUnlockAllItems = true;
    private static readonly AsyncLocal<bool?> AsyncLocalUnlockAllOverride = new();

    /// <summary>
    ///     Global toggle for sandbox mode (grant all store items and avatars) vs progression mode (inventory only).
    ///     Defaults to true for backward compatibility and local sandbox play; AsyncLocal-isolated for parallel tests.
    /// </summary>
    public static bool UnlockAllItems
    {
        get => AsyncLocalUnlockAllOverride.Value ?? _globalUnlockAllItems;
        set => AsyncLocalUnlockAllOverride.Value = value;
    }

    public static bool ReadUnlockAllSetting(IConfiguration? config)
    {
        if (config != null)
        {
            string? explicitMode = config["Store:Mode"] ?? config["STORE_MODE"];
            if (!string.IsNullOrWhiteSpace(explicitMode))
            {
                return NEXUS.Contracts.Store.StoreEntitlementPolicy.ResolveMode(config, _globalUnlockAllItems) == NEXUS.Contracts.Store.StoreMode.PreservationFreeAll;
            }

            string? val = config["Store:UnlockAllItems"]
                ?? config["UnlockAllItems"]
                ?? config["unlock-all-items"]
                ?? config["Parameters:unlock-all-items"];
            if (bool.TryParse(val, out bool bVal))
            {
                return bVal;
            }
        }
        return _globalUnlockAllItems;
    }

    /// <summary>
    ///     Initializes the process-wide default for <see cref="UnlockAllItems" /> once at application startup.
    /// </summary>
    public static bool InitializeGlobalUnlockAllSetting(IConfiguration? config)
    {
        bool resolved = ReadUnlockAllSetting(config);
        _globalUnlockAllItems = resolved;
        return resolved;
    }

    public bool IsUnlockAllEnabled => GetUnlockAllSetting(configuration);

    public static bool GetUnlockAllSetting(IConfiguration? config)
    {
        if (config != null)
        {
            string? explicitMode = config["Store:Mode"] ?? config["STORE_MODE"];
            if (!string.IsNullOrWhiteSpace(explicitMode))
            {
                return NEXUS.Contracts.Store.StoreEntitlementPolicy.IsFreeUnlockAll(config, UnlockAllItems);
            }

            string? val = config["Store:UnlockAllItems"]
                ?? config["UnlockAllItems"]
                ?? config["unlock-all-items"]
                ?? config["Parameters:unlock-all-items"];
            if (bool.TryParse(val, out bool bVal))
            {
                return bVal;
            }
        }
        return UnlockAllItems;
    }

    public async Task<HashSet<string>> ResolveExpandedOwnedItemsAsync(Account account, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string>? repoOwned = null;
        if (storeRepository != null)
        {
            try
            {
                repoOwned = await storeRepository.GetOwnedItemCodesByAccountIdAsync(account.ID, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[UpgradesResolution] Failed to fetch inventory from StoreRepository for account {AccountName}", account.Name);
            }
        }

        bool shouldUnlockAll = GetUnlockAllSetting(configuration);
        bool excludeWarEffortExclusives = NEXUS.Contracts.Store.StoreEntitlementPolicy.ShouldExcludeWarEffortExclusives(configuration);
        IEnumerable<string> sourceItems = repoOwned ?? account.User?.InventoryItems?.Select(i => i.ItemCode) ?? [];
        return ExpandOwnedItems(sourceItems, shouldUnlockAll, account.Type, account.Clan?.Tag, excludeWarEffortExclusives);
    }

    /// <summary>
    ///     Synchronously expands a collection of item codes with canonical defaults,
    ///     all custom mod items (ID &gt;= 6000), and dual-prefixed codes (aa.Hero_*.Alt* and Hero_*.Alt*),
    ///     while enforcing <see cref="NEXUS.Contracts.Store.StoreEntitlementPolicy"/> on Staff/GM/Clan/SBT
    ///     and War Effort exclusive items.
    /// </summary>
    public static HashSet<string> ExpandOwnedItems(
        IEnumerable<string>? rawItems,
        bool? unlockAllItems = null,
        NEXUS.Contracts.Domain.AccountType accountType = NEXUS.Contracts.Domain.AccountType.Normal,
        string? clanTag = null,
        bool excludeWarEffortExclusives = false)
    {
        HashSet<string> expandedOwned = new(StringComparer.OrdinalIgnoreCase);

        // 1. Canonical store defaults that every player owns
        expandedOwned.Add(StoreDefaults.DefaultTaunt);
        expandedOwned.Add(StoreDefaults.DefaultAccountIcon);
        expandedOwned.Add(StoreDefaults.DefaultChatColor);
        foreach (string def in StoreDefaults.DefaultOwnedItems)
        {
            if (!string.IsNullOrWhiteSpace(def))
                expandedOwned.Add(def.Trim());
        }

        bool shouldUnlockAll = unlockAllItems ?? UnlockAllItems;

        if (shouldUnlockAll)
        {
            // 2. Universally grant public store items and alt avatars while strictly keeping Staff/GM/Clan/SBT entitlements gated.
            if (StoreItems?.StoreItems != null)
            {
                foreach (StoreItem modItem in StoreItems.StoreItems)
                {
                    if (!NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                            accountType, clanTag, modItem.Code, modItem.PrefixedCode, modItem.Name))
                    {
                        continue;
                    }

                    if (excludeWarEffortExclusives &&
                        NEXUS.Contracts.Store.StoreEntitlementPolicy.IsWarEffortExclusive(
                            modItem.Code, modItem.PrefixedCode, modItem.Name, modItem.ID))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(modItem.PrefixedCode))
                        expandedOwned.Add(modItem.PrefixedCode.Trim());
                    if (!string.IsNullOrWhiteSpace(modItem.Code))
                        expandedOwned.Add(modItem.Code.Trim());
                }
            }

            // 3. Universally grant all supplementary community mod avatars and base skins defined in client metadata (heroes.xml)
            foreach (string modSkin in SupplementaryModAvatars.Keys)
            {
                if (string.IsNullOrWhiteSpace(modSkin)) continue;
                string trimmed = modSkin.Trim();
                expandedOwned.Add(trimmed);
                if (!trimmed.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
                    expandedOwned.Add($"aa.{trimmed}");
            }
        }

        // 4. Resolve and dual-expand provided source items
        if (rawItems != null)
        {
            foreach (string rawItem in rawItems)
            {
                if (string.IsNullOrWhiteSpace(rawItem)) continue;

                string item = rawItem.Trim();
                expandedOwned.Add(item);

                StoreItem? storeItem = StoreItems?.GetByPrefixedCode(item)
                                       ?? StoreItems?.GetByCode(item)
                                       ?? StoreItems?.GetByName(item);

                if (storeItem is null && int.TryParse(item, out int numericId))
                    storeItem = StoreItems?.GetByID(numericId);

                if (storeItem != null)
                {
                    if (!string.IsNullOrWhiteSpace(storeItem.PrefixedCode))
                        expandedOwned.Add(storeItem.PrefixedCode.Trim());
                    if (!string.IsNullOrWhiteSpace(storeItem.Code))
                        expandedOwned.Add(storeItem.Code.Trim());
                }

                if (item.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && item.Contains('.'))
                    expandedOwned.Add("aa." + item);
                else if (item.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
                    expandedOwned.Add(item[3..]);
            }
        }

        // 5. Grant universal mode passes and tokens to prevent disconnect_no_tokens across all game modes
        string[] modePasses =
        [
            "m.allmodes.pass",
            "m.allmodes.access",
            "m.midwars.pass",
            "m.midwars.access",
            "m.captainsmode.pass",
            "m.captainsmode.access",
            "m.caldavar.pass",
            "m.caldavar.access",
            "m.krosmode.pass",
            "m.krosmode.access",
            "m.riftwars.pass",
            "m.riftwars.access",
            "m.botmatch.pass",
            "m.botmatch.access",
            "m.casual.pass",
            "m.casual.access",
            "m.casualmode.pass",
            "m.casualmode.access",
            "m.public.pass",
            "m.public.access",
            "m.tmm.pass",
            "m.tmm.access",
            "m.ranked.pass",
            "m.ranked.access",
            "m.shufflemode.pass",
            "m.shufflemode.access",
            "m.blitz.pass",
            "m.blitz.access",
            "m.core.pass",
            "m.core.access",
            "m.custom.pass",
            "m.custom.access"
        ];
        foreach (string pass in modePasses)
        {
            expandedOwned.Add(pass);
        }

        return expandedOwned;
    }
}
