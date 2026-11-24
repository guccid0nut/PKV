// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Configuration.Store;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KONGOR.MasterServer.Features.Store;

/// <summary>
/// Helper methods for categorizing user-owned vault data and populating selected upgrades.
/// Extracted from StoreController to eliminate direct persistence dependencies from controllers.
/// </summary>
public static class StoreVaultHelpers
{
    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    /// <summary>
    /// Categorizes owned store items into lists grouped by <see cref="StoreItemType"/> in a single pass.
    /// </summary>
    public static Dictionary<StoreItemType, List<string>> CategorizeUserOwnedVaultData(
        Account account,
        MerrickContext? merrickContext = null,
        ILogger? logger = null)
    {
        Dictionary<StoreItemType, List<string>> categories = [];
        Dictionary<StoreItemType, HashSet<int>> addedIDsByType = [];

        HashSet<string> ownedCodes = new(StringComparer.OrdinalIgnoreCase);

        if (account.User?.InventoryItems != null)
        {
            foreach (UserInventory inv in account.User.InventoryItems)
            {
                if (!string.IsNullOrWhiteSpace(inv.ItemCode)) ownedCodes.Add(inv.ItemCode.Trim());
            }
        }

        if (merrickContext != null)
        {
            try
            {
                int userId = account.User?.ID ?? 0;
                int accountId = account.ID;
                List<string> dbInvCodes = merrickContext.UserInventory
                    .AsNoTracking()
                    .Where(i => i.UserID == userId || i.AccountID == accountId)
                    .Select(i => i.ItemCode)
                    .Distinct()
                    .ToList();

                foreach (string dbCode in dbInvCodes)
                {
                    if (!string.IsNullOrWhiteSpace(dbCode)) ownedCodes.Add(dbCode.Trim());
                }
            }
            catch (Exception ex)
            {
                if (logger?.IsEnabled(LogLevel.Warning) == true)
                    logger.LogWarning(ex, "[StoreVaultHelpers] Failed to fetch direct UserInventory for Account #{AccountId}", account.ID);
            }
        }

        ownedCodes.Add(StoreDefaults.DefaultTaunt);
        ownedCodes.Add(StoreDefaults.DefaultAccountIcon);
        ownedCodes.Add(StoreDefaults.DefaultChatColor);

        if (UpgradesResolutionService.UnlockAllItems && StoreItems?.StoreItems != null)
        {
            foreach (StoreItem storeItem in StoreItems.StoreItems)
            {
                if (storeItem != null && storeItem.IsEnabled)
                {
                    if (!string.IsNullOrWhiteSpace(storeItem.PrefixedCode))
                        ownedCodes.Add(storeItem.PrefixedCode.Trim());
                    else if (!string.IsNullOrWhiteSpace(storeItem.Code))
                        ownedCodes.Add(storeItem.Code.Trim());
                }
            }
        }

        bool isStaff = account.Type == AccountType.Staff;
        bool isGuci = string.Equals(account.Clan?.Tag, AccountExtensions.GuciClanTag, StringComparison.OrdinalIgnoreCase);

        // Sanitize restricted name colors and symbols for non-qualifying accounts
        if (!isStaff)
        {
            foreach (string staffColor in AccountExtensions.StaffOnlyNameColors)
            {
                ownedCodes.Remove(staffColor);
                if (!staffColor.StartsWith("cc.", StringComparison.OrdinalIgnoreCase))
                    ownedCodes.Remove($"cc.{staffColor}");
            }
            ownedCodes.Remove("cs.frostburnlogo");
            ownedCodes.Remove("frostburnlogo");
            ownedCodes.Remove("cc.gucistaff");
            ownedCodes.Remove("gucistaff");
        }

        if (!isGuci)
        {
            ownedCodes.Remove("cc.guci");
            ownedCodes.Remove("guci");
            ownedCodes.Remove("cc.gucistaff");
            ownedCodes.Remove("gucistaff");
        }

        foreach (string ownedCode in ownedCodes)
        {
            StoreItem? item = StoreItems?.GetByPrefixedCode(ownedCode)
                              ?? StoreItems?.GetByCode(ownedCode)
                              ?? StoreItems?.GetByName(ownedCode);

            if (item is null && int.TryParse(ownedCode, out int numericID)) item = StoreItems?.GetByID(numericID);

            if (item is null)
            {
                if (ownedCode.StartsWith("ai.custom_icon:", StringComparison.Ordinal))
                {
                    string iconCode = ownedCode[3..];
                    if (categories.TryGetValue(StoreItemType.AccountIcon, out List<string>? iconList).Equals(false) ||
                        iconList is null)
                    {
                        iconList = [];
                        categories[StoreItemType.AccountIcon] = iconList;
                    }

                    iconList.Add(string.Join("`", iconCode, ownedCode, "web"));
                }

                continue;
            }

            if (item.Code.Contains("Mastery Boost", StringComparison.OrdinalIgnoreCase)) continue;

            if (addedIDsByType.TryGetValue(item.StoreItemType, out HashSet<int>? addedSet).Equals(false) ||
                addedSet is null)
            {
                addedSet = [];
                addedIDsByType[item.StoreItemType] = addedSet;
            }

            if (addedSet.Add(item.ID))
            {
                if (categories.TryGetValue(item.StoreItemType, out List<string>? typeList).Equals(false) ||
                    typeList is null)
                {
                    typeList = [];
                    categories[item.StoreItemType] = typeList;
                }

                typeList.Add(string.Join("`", item.ID, item.PrefixedCode, item.Resource));
            }
        }

        return categories;
    }

    /// <summary>
    /// Populates the response dictionary with the product IDs of the account's selected upgrades.
    /// </summary>
    public static void PopulateSelectedUpgrades(
        Account account,
        Dictionary<string, object> response,
        MerrickContext? dbContext = null,
        ILogger? logger = null)
    {
        List<string> upgradeIDs = [];
        HashSet<string> selectedCodes = new(account.SelectedStoreItemCodes, StringComparer.OrdinalIgnoreCase);

        if (dbContext != null && account.ID > 0)
        {
            try
            {
                List<string> dbCodes = dbContext.AccountSelectedStoreItems
                    .AsNoTracking()
                    .Where(s => s.AccountID == account.ID)
                    .Select(s => s.ItemCode)
                    .ToList();

                foreach (string c in dbCodes)
                {
                    if (!string.IsNullOrWhiteSpace(c)) selectedCodes.Add(c.Trim());
                }
            }
            catch (Exception ex)
            {
                if (logger?.IsEnabled(LogLevel.Warning) == true)
                    logger.LogWarning(ex, "[StoreVaultHelpers] Failed to fetch direct AccountSelectedStoreItems for Account #{AccountId}", account.ID);
            }
        }

        // Enforce signature name colors at egress
        bool isStaff = account.Type == AccountType.Staff;
        bool isGuci = string.Equals(account.Clan?.Tag, AccountExtensions.GuciClanTag, StringComparison.OrdinalIgnoreCase);

        // Strip existing name colors from selectedCodes first
        selectedCodes.RemoveWhere(code => code.StartsWith("cc.", StringComparison.OrdinalIgnoreCase));

        if (isStaff && isGuci)
        {
            selectedCodes.Add("cc.gucistaff");
        }
        else if (isStaff)
        {
            selectedCodes.Add("cc.frostburnlogo");
        }
        else if (isGuci)
        {
            selectedCodes.Add("cc.guci");
        }
        else
        {
            // Non-staff, non-GUCI: check if account originally had a non-restricted color
            string origColor = (object?)account.SelectedStoreItems is not null
                ? account.SelectedStoreItems.FirstOrDefault(item =>
                    item.ItemCode.StartsWith("cc.", StringComparison.OrdinalIgnoreCase))?.ItemCode ?? "cc.white"
                : "cc.white";

            if (AccountExtensions.StaffOnlyNameColors.Contains(origColor) ||
                origColor.Equals("cc.guci", StringComparison.OrdinalIgnoreCase) ||
                origColor.Equals("cc.gucistaff", StringComparison.OrdinalIgnoreCase))
            {
                selectedCodes.Add("cc.white");
            }
            else
            {
                selectedCodes.Add(origColor);
            }

            // Also strip staff symbol if present
            selectedCodes.Remove("cs.frostburnlogo");
        }

        foreach (string selectedCode in selectedCodes)
        {
            StoreItem? item = StoreItems.GetByPrefixedCode(selectedCode)
                              ?? StoreItems.GetByCode(selectedCode)
                              ?? StoreItems.GetByName(selectedCode);

            if (item is null && int.TryParse(selectedCode, out int numericID)) item = StoreItems.GetByID(numericID);

            if (item is null && selectedCode.Contains('.'))
            {
                string codeWithoutPrefix = selectedCode[(selectedCode.IndexOf('.') + 1)..];
                item = StoreItems.GetByCode(codeWithoutPrefix) ?? StoreItems.GetByName(codeWithoutPrefix);
            }

            if (item is not null)
                upgradeIDs.Add(item.ID.ToString());

            else if (selectedCode.Contains("custom_icon:")) upgradeIDs.Add(selectedCode[3..]);
        }

        string result = string.Join("|", upgradeIDs);
        response["selectedUpgrades"] = result;

        if (logger?.IsEnabled(LogLevel.Information) == true)
        {
            logger.LogInformation(
                "[StoreVaultHelpers] Account #{AccountId} ('{AccountName}') populated selected upgrades: [{SelectedUpgrades}]",
                account.ID, account.Name, result);
        }
    }
}
