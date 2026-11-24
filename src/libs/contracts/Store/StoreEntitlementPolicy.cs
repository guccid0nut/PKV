// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NEXUS.Contracts.Domain;

namespace NEXUS.Contracts.Store;

/// <summary>
///     Canonical store economy operating mode across MasterServer, MERRICK, ZORGATH, DAWNBRINGER, ControlPanel, and K2 client UI.
/// </summary>
public enum StoreMode
{
    /// <summary>
    ///     Default preservation policy: all public cosmetics are unlocked free for everyone, while restricted Staff/GM/Clan/SBT
    ///     entitlements remain strictly locked to authorized accounts.
    /// </summary>
    PreservationFreeAll = 0,

    /// <summary>
    ///     Opt-in community progression policy: cosmetics require inventory ownership and Gold/Silver coin or Plinko unlocks.
    /// </summary>
    EarnedProgression = 1
}

/// <summary>
///     Single authoritative policy resolving <see cref="StoreMode"/> (with backward-compatible <c>Store:UnlockAllItems</c> mapping)
///     and enforcing Staff/GM/Clan/SBT cosmetic boundaries across all 7 platform layers.
/// </summary>
public static class StoreEntitlementPolicy
{
    public const string GuciClanTag = "GUCI";

    public static readonly FrozenSet<string> StaffOrGmRestrictedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "frostburnlogo", "cc.frostburnlogo", "cs.frostburnlogo",
        "gmshield", "cc.gmshield", "cs.gmshield",
        "gmgold", "cc.gmgold", "cs.gmgold",
        "banhammer", "cc.banhammer", "cs.banhammer",
        "techtinker", "cc.techtinker", "cs.techtinker",
        "candycane", "cc.candycane", "cs.candycane",
        "sbteye", "cc.sbteye", "cs.sbteye",
        "sbtpremium", "cc.sbtpremium", "cs.sbtpremium",
        "sbtsenior", "cc.sbtsenior", "cs.sbtsenior",
        "Garena Staff", "ai.Garena Staff",
        "HLL Staff", "ai.HLL Staff"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> GuciClanCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "guci", "cc.guci"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> GuciStaffCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gucistaff", "cc.gucistaff"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<int> WarEffortExclusiveProductIds = new HashSet<int>
    {
        2769, // War Effort Contender (Account Icon)
        2770, // War Effort Winner (Account Icon)
        2909, // Dragon's Treasure Ward
        2910  // Gnome Courier
    }.ToFrozenSet();

    public static readonly FrozenSet<string> WarEffortExclusiveCodes = BuildWarEffortExclusiveCodes();

    private static FrozenSet<string> BuildWarEffortExclusiveCodes()
    {
        HashSet<string> codes = new(StringComparer.OrdinalIgnoreCase)
        {
            "war_effort_contender", "ai.war_effort_contender", "War Effort Contender",
            "war_effort_winner", "ai.war_effort_winner", "War Effort Winner",
            "war_effort_ward", "w.war_effort_ward", "Dragon's Treasure Ward",
            "wareffort_courier", "c.wareffort_courier", "Gnome Courier", "Pet_GroundFamiliar.C_WarEffort"
        };
        for (int i = 1; i <= 26; i++)
        {
            codes.Add($"war_effort_contender_{i}");
            codes.Add($"ai.war_effort_contender_{i}");
        }
        return codes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsWarEffortExclusive(string? code, string? prefixedCode = null, string? name = null, int? productId = null)
    {
        if (productId.HasValue && WarEffortExclusiveProductIds.Contains(productId.Value))
            return true;
        return MatchesSet(WarEffortExclusiveCodes, code, prefixedCode, name);
    }

    public static bool ShouldExcludeWarEffortExclusives(IConfiguration? configuration, bool defaultExclude = true)
    {
        if (configuration is null) return defaultExclude;
        string? raw = configuration["WarEffort:ExcludeExclusiveCosmeticsFromUnlockAll"];
        return bool.TryParse(raw, out bool parsed) ? parsed : defaultExclude;
    }

    public static StoreMode ResolveMode(IConfiguration? configuration, bool defaultFreeAll = true)
    {
        if (configuration is not null)
        {
            string? explicitMode = configuration["Store:Mode"] ?? configuration["STORE_MODE"];
            if (!string.IsNullOrWhiteSpace(explicitMode))
            {
                string normalized = explicitMode.Trim();
                if (normalized.Equals("EarnedProgression", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("progression", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("strict", StringComparison.OrdinalIgnoreCase))
                {
                    return StoreMode.EarnedProgression;
                }

                if (normalized.Equals("PreservationFreeAll", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("sandbox", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("free", StringComparison.OrdinalIgnoreCase))
                {
                    return StoreMode.PreservationFreeAll;
                }
            }

            string? unlockAllRaw = configuration["Store:UnlockAllItems"]
                                   ?? configuration["UnlockAllItems"]
                                   ?? configuration["unlock-all-items"]
                                   ?? configuration["Parameters:unlock-all-items"];
            if (bool.TryParse(unlockAllRaw, out bool parsedUnlockAll))
            {
                return parsedUnlockAll ? StoreMode.PreservationFreeAll : StoreMode.EarnedProgression;
            }
        }

        return defaultFreeAll ? StoreMode.PreservationFreeAll : StoreMode.EarnedProgression;
    }

    public static bool IsFreeUnlockAll(IConfiguration? configuration, bool defaultFreeAll = true) =>
        ResolveMode(configuration, defaultFreeAll) == StoreMode.PreservationFreeAll;

    public static bool IsElevatedStaffOrGm(AccountType accountType) =>
        accountType is AccountType.Staff or AccountType.GameMaster;

    public static bool IsRestrictedEntitlement(string? code, string? prefixedCode = null, string? name = null)
    {
        return MatchesSingleToken(code) || MatchesSingleToken(prefixedCode) || MatchesSingleToken(name);
    }

    public static bool IsAccountAuthorizedForRestricted(
        AccountType accountType,
        string? clanTag,
        string? code,
        string? prefixedCode = null,
        string? name = null)
    {
        if (!IsRestrictedEntitlement(code, prefixedCode, name))
        {
            return true;
        }

        bool isStaffOrGm = IsElevatedStaffOrGm(accountType);
        bool isGuci = string.Equals(clanTag?.Trim(), GuciClanTag, StringComparison.OrdinalIgnoreCase);

        if (MatchesSet(GuciStaffCodes, code, prefixedCode, name))
        {
            return isStaffOrGm && isGuci;
        }

        if (MatchesSet(GuciClanCodes, code, prefixedCode, name))
        {
            return isGuci;
        }

        return isStaffOrGm;
    }

    private static bool MatchesSingleToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim();
        return StaffOrGmRestrictedCodes.Contains(trimmed) ||
               GuciClanCodes.Contains(trimmed) ||
               GuciStaffCodes.Contains(trimmed);
    }

    private static bool MatchesSet(FrozenSet<string> set, string? code, string? prefixedCode, string? name)
    {
        return (!string.IsNullOrWhiteSpace(code) && set.Contains(code.Trim())) ||
               (!string.IsNullOrWhiteSpace(prefixedCode) && set.Contains(prefixedCode.Trim())) ||
               (!string.IsNullOrWhiteSpace(name) && set.Contains(name.Trim()));
    }
}
