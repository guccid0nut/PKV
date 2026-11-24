// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Extensions;

public static class AccountExtensions
{
    public const string GuciClanTag = "GUCI";

    public static readonly HashSet<string> StaffOnlyNameColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "cc.frostburnlogo", "cc.gmgold", "cc.gmshield", "cc.banhammer", "cc.techtinker", "cc.candycane",
        "frostburnlogo", "gmgold", "gmshield", "banhammer", "techtinker", "candycane"
    };

    public static string GetNameWithClanTag(this Account account)
    {
        if ((object?)account is null) return "Unknown";

        return account.Clan is null ? account.Name : $"[{account.Clan.Tag}]{account.Name}";
    }

    public static string GetClanTierName(this Account account)
    {
        if ((object?)account is null) return "None";

        return account.ClanTier switch
        {
            ClanTier.None => "None",
            ClanTier.Member => "Member",
            ClanTier.Officer => "Officer",
            ClanTier.Leader => "Leader",
            _ => throw new ArgumentOutOfRangeException(@$"Unsupported Clan Tier ""{account.ClanTier}""")
        };
    }

    public static string GetIcon(this Account account)
    {
        if ((object?)account is null) return StoreDefaults.DefaultAccountIcon;

        string? explicitIcon = account.SelectedStoreItems?.FirstOrDefault(item =>
            item.ItemCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase))?.ItemCode;

        if (!string.IsNullOrWhiteSpace(explicitIcon) &&
            !explicitIcon.Equals(StoreDefaults.DefaultAccountIcon, StringComparison.OrdinalIgnoreCase))
        {
            if (!NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                    account.Type, account.Clan?.Tag, explicitIcon))
            {
                return StoreDefaults.DefaultAccountIcon;
            }

            return explicitIcon;
        }

        // If no explicit store icon is selected (or default icon is selected), but user has Discord linked or owns custom_icon:1, use custom icon
        if (!string.IsNullOrWhiteSpace(account.User?.DiscordAvatar) ||
            !string.IsNullOrWhiteSpace(account.User?.DiscordId) ||
            (account.User?.InventoryItems.Any(item =>
                item.ItemCode.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase)) ?? false))
        {
            return StoreDefaults.CustomAccountIcon;
        }

        return explicitIcon ?? StoreDefaults.DefaultAccountIcon;
    }

    public static string GetIconNoPrefixCode(this Account account)
    {
        return account.GetIcon().Replace("ai.", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetChatSymbol(this Account account)
    {
        if ((object?)account is null) return string.Empty;

        string symbol = (object?)account.SelectedStoreItems is not null
            ? account.SelectedStoreItems.FirstOrDefault(item =>
                item.ItemCode.StartsWith("cs.", StringComparison.OrdinalIgnoreCase))?.ItemCode ?? string.Empty
            : string.Empty;

        // Staff/GM/restricted symbols are strictly gated by account role & clan
        if (!string.IsNullOrWhiteSpace(symbol) &&
            !NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                account.Type, account.Clan?.Tag, symbol))
        {
            return string.Empty;
        }

        return symbol;
    }

    public static string GetChatSymbolNoPrefixCode(this Account account)
    {
        return account.GetChatSymbol().Replace("cs.", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetNameColour(this Account account)
    {
        if ((object?)account is null) return "cc.white";

        bool isStaff = account.Type == AccountType.Staff;
        bool isGuci = string.Equals(account.Clan?.Tag, GuciClanTag, StringComparison.OrdinalIgnoreCase);

        if (isStaff && isGuci)
            return "cc.gucistaff";

        if (isStaff)
            return "cc.frostburnlogo";

        if (isGuci)
            return "cc.guci";

        // Non-staff, non-GUCI: get equipped color, but sanitize away any staff/guci colors
        string equipped = (object?)account.SelectedStoreItems is not null
            ? account.SelectedStoreItems.FirstOrDefault(item =>
                item.ItemCode.StartsWith("cc.", StringComparison.OrdinalIgnoreCase))?.ItemCode ?? "cc.white"
            : "cc.white";

        if (StaffOnlyNameColors.Contains(equipped) ||
            !NEXUS.Contracts.Store.StoreEntitlementPolicy.IsAccountAuthorizedForRestricted(
                account.Type, account.Clan?.Tag, equipped))
        {
            return "cc.white";
        }

        return equipped;
    }

    public static string GetNameColourNoPrefixCode(this Account account)
    {
        return account.GetNameColour().Replace("cc.", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public static (string ClanTag, string AccountName) SeparateClanTagFromAccountName(string accountNameWithClanTag)
    {
        // If no '[' and ']' characters are found, then the account is not part of a clan and has no clan tag.
        if (!accountNameWithClanTag.Contains('[') && !accountNameWithClanTag.Contains(']'))
            return (string.Empty, accountNameWithClanTag);

        // If '[' is not the first character, then the account name contains the '[' and ']' characters, but the account is not part of a clan and has no clan tag.
        if (!accountNameWithClanTag.StartsWith('[')) return (string.Empty, accountNameWithClanTag);

        // Remove the leading '[' character and split at the first occurrence of the ']' character. The resulting account name may contain the '[' and ']' characters.
        string[] segments = accountNameWithClanTag.TrimStart('[').Split(']', 2);

        return (segments.First(), segments.Last());
    }
}
