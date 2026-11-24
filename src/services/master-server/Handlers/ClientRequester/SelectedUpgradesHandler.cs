// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using ASPIRE.Common.Caching;
using KONGOR.MasterServer.Services.Store;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class SelectedUpgradesHandler(
    IAccountRepository accountRepository,
    ISessionRepository sessionRepository,
    ILogger<SelectedUpgradesHandler> logger,
    INexusHybridCache? hybridCache = null,
    IUpgradesResolutionService? upgradesResolver = null,
    IConfiguration? configuration = null) : IClientRequestHandler
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private ISessionRepository SessionRepository { get; } = sessionRepository;
    private ILogger Logger { get; } = logger;
    private INexusHybridCache? HybridCache { get; } = hybridCache;
    private IUpgradesResolutionService? UpgradesResolver { get; } = upgradesResolver;
    private IConfiguration? Configuration { get; } = configuration;

    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string cookie = ClientRequestHelper.GetCookie(Request) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(cookie)) return new UnauthorizedResult();

        string cleanCookie = cookie.Replace("-", string.Empty);
        string? accountName =
            await SessionRepository.GetAccountNameForSessionCookieAsync(cleanCookie, cancellationToken);

        if (accountName is null) return new UnauthorizedObjectResult($@"Unrecognised Cookie ""{SecurityMasking.MaskToken(cookie)}""");

        Account? account = await AccountRepository.GetByNicknameAsync(accountName, cancellationToken);

        if (account is null) return new UnauthorizedObjectResult("Account Not Found");

        Dictionary<string, string> selectedByPrefix = new(StringComparer.OrdinalIgnoreCase);

        // Pre-populate with currently equipped items so selecting an item in one category
        // (e.g. Chat Symbol cs.) does not erase selections in other categories (e.g. Account Icon ai., Chat Color cc.)
        foreach (string existingItem in account.SelectedStoreItemCodes)
        {
            string? prefix = GetItemPrefix(existingItem);
            if (!string.IsNullOrEmpty(prefix) && KnownPrefixes.Contains(prefix))
            {
                selectedByPrefix[prefix] = existingItem;
            }
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[SelectedUpgrades] Account #{AccountId} ('{AccountName}') initial selections: [{InitialSelections}]",
                account.ID, account.Name, string.Join(", ", selectedByPrefix.Values));
        }

        StringValues upgradeCodes = StringValues.Empty;
        if (Request.HasFormContentType)
        {
            IFormCollection form = await Request.ReadFormAsync(cancellationToken);
            upgradeCodes = form["selected_upgrades[]"];
            if (upgradeCodes.Count == 0) upgradeCodes = form["selected_upgrades"];
        }
        if (upgradeCodes.Count == 0)
        {
            upgradeCodes = Request.Query["selected_upgrades[]"];
            if (upgradeCodes.Count == 0) upgradeCodes = Request.Query["selected_upgrades"];
        }

        List<string> validCodes = upgradeCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .ToList();

        // If f=selected_upgrades was invoked but no upgrade codes were provided, the client has cleared all selections.
        if (validCodes.Count == 0)
        {
            Logger.LogInformation(
                "[SelectedUpgrades] Account #{AccountId} ('{AccountName}') cleared all selected upgrades.",
                account.ID, account.Name);
            account.SetSelectedStoreItems([]);
            await AccountRepository.UpdateAsync(account, cancellationToken);
            await InvalidateProfileCacheAsync(account, cancellationToken);
            return new PhpResult(string.Empty);
        }

        // Multi-item payload: K2 client sends its full active set of selected upgrades.
        // Synchronize by removing any known category that was previously equipped but is omitted in this request.
        if (validCodes.Count > 1)
        {
            HashSet<string> incomingPrefixes = new(StringComparer.OrdinalIgnoreCase);
            foreach (string code in validCodes)
            {
                string? p = GetItemPrefix(code);
                if (!string.IsNullOrEmpty(p)) incomingPrefixes.Add(p);
            }

            List<string> prefixesToRemove = selectedByPrefix.Keys
                .Where(p => KnownPrefixes.Contains(p) && !incomingPrefixes.Contains(p))
                .ToList();

            foreach (string p in prefixesToRemove)
            {
                selectedByPrefix.Remove(p);
                Logger.LogInformation(
                    "[SelectedUpgrades] Synchronized unequip for category '{Prefix}' for Account #{AccountId}",
                    p, account.ID);
            }
        }


        IConfiguration? effectiveConfig = Configuration ?? context.RequestServices?.GetService<IConfiguration>();
        IUpgradesResolutionService? effectiveResolver = UpgradesResolver ?? context.RequestServices?.GetService<IUpgradesResolutionService>();
        bool unlockAll = effectiveResolver?.IsUnlockAllEnabled ?? UpgradesResolutionService.GetUnlockAllSetting(effectiveConfig);
        string unownedEquipPolicy = effectiveConfig?["Store:UnownedEquipPolicy"] ?? "ShadowLog";
        bool enforceUnownedEquip = !unlockAll && string.Equals(unownedEquipPolicy, "Enforce", StringComparison.OrdinalIgnoreCase);
        HashSet<string>? expandedOwned = null;
        if (!unlockAll)
        {
            expandedOwned = effectiveResolver != null
                ? await effectiveResolver.ResolveExpandedOwnedItemsAsync(account, cancellationToken)
                : UpgradesResolutionService.ExpandOwnedItems(account.User?.InventoryItems?.Select(i => i.ItemCode), unlockAllItems: false);
        }

        foreach (string upgradeCode in validCodes)
        {
            // Handle prefix-only unequip (e.g. "c", "av", "w", "cs", "ai", "c.")
            string normalizedPrefix = upgradeCode.EndsWith('.') ? upgradeCode.ToLowerInvariant() : upgradeCode.ToLowerInvariant() + ".";
            if (KnownPrefixes.Contains(normalizedPrefix) && !upgradeCode.Contains(':'))
            {
                selectedByPrefix.Remove(normalizedPrefix);
                Logger.LogInformation(
                    "[SelectedUpgrades] Unequipped category '{Prefix}' via prefix code for Account #{AccountId}",
                    normalizedPrefix, account.ID);
                continue;
            }

            // Handle explicit category unequip requests (e.g. cs.0, cs.none, ai.0, ai.none)
            int dotIdx = upgradeCode.IndexOf('.');
            if (dotIdx > 0)
            {
                string prefix = upgradeCode[..(dotIdx + 1)].ToLowerInvariant();
                string suffix = upgradeCode[(dotIdx + 1)..].Trim();
                if (suffix is "0" or "-1" or "none" or "default")
                {
                    selectedByPrefix.Remove(prefix);
                    Logger.LogInformation(
                        "[SelectedUpgrades] Unequipped category '{Prefix}' for Account #{AccountId}",
                        prefix, account.ID);
                    continue;
                }
            }

            if (upgradeCode.StartsWith("custom_icon", StringComparison.OrdinalIgnoreCase) ||
                upgradeCode.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase))
            {
                string prefixedCode = upgradeCode.Contains(':')
                    ? (upgradeCode.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ? upgradeCode : "ai." + upgradeCode)
                    : "ai.custom_icon:1";

                if (!unlockAll && expandedOwned != null && !expandedOwned.Contains(prefixedCode))
                {
                    Logger.LogWarning(
                        "[ECONOMY-ALERT] Unowned equip attempt by Account #{AccountId} ('{AccountName}') for custom icon '{ItemCode}' (policy={Policy})",
                        account.ID, account.Name, prefixedCode, unownedEquipPolicy);
                    if (enforceUnownedEquip)
                        continue;
                }

                selectedByPrefix["ai."] = prefixedCode;

                Logger.LogInformation(
                    "[SelectedUpgrades] Equipped custom icon '{PrefixedCode}' for Account #{AccountId}",
                    prefixedCode, account.ID);

                continue;
            }

            StoreItem? storeItem = StoreItems.GetByCode(upgradeCode)
                                   ?? StoreItems.GetByPrefixedCode(upgradeCode)
                                   ?? StoreItems.GetByName(upgradeCode);

            if (storeItem is null && int.TryParse(upgradeCode, out int numericID))
                storeItem = StoreItems.GetByID(numericID);

            if (storeItem is null && upgradeCode.Contains('.'))
            {
                string codeWithoutPrefix = upgradeCode[(upgradeCode.IndexOf('.') + 1)..];
                storeItem = StoreItems.GetByCode(codeWithoutPrefix) ?? StoreItems.GetByName(codeWithoutPrefix);
            }

            if (storeItem is null)
            {
                // Fallback: If it starts with a recognized dot-prefix, allow equipping directly
                string? detectedPrefix = GetItemPrefix(upgradeCode);
                if (!string.IsNullOrEmpty(detectedPrefix) && KnownPrefixes.Contains(detectedPrefix))
                {
                    if (!unlockAll && expandedOwned != null && !expandedOwned.Contains(upgradeCode))
                    {
                        Logger.LogWarning(
                            "[ECONOMY-ALERT] Unowned equip attempt by Account #{AccountId} ('{AccountName}') for item '{ItemCode}' (policy={Policy})",
                            account.ID, account.Name, upgradeCode, unownedEquipPolicy);
                        if (enforceUnownedEquip)
                            continue;
                    }

                    selectedByPrefix[detectedPrefix] = upgradeCode;

                    Logger.LogInformation(
                        "[SelectedUpgrades] Equipped store item '{UpgradeCode}' under detected prefix '{Prefix}' for Account #{AccountId}",
                        upgradeCode, detectedPrefix, account.ID);
                    continue;
                }

                if (Logger.IsEnabled(LogLevel.Warning))
                    Logger.LogWarning(
                        @"[SelectedUpgrades] Unable To Resolve Store Item For Upgrade Code ""{UpgradeCode}""",
                        upgradeCode);

                continue;
            }

            if (!unlockAll && expandedOwned != null &&
                !expandedOwned.Contains(storeItem.PrefixedCode) &&
                !expandedOwned.Contains(storeItem.Code))
            {
                Logger.LogWarning(
                    "[ECONOMY-ALERT] Unowned equip attempt by Account #{AccountId} ('{AccountName}') for store item '{ItemCode}' (policy={Policy})",
                    account.ID, account.Name, storeItem.PrefixedCode, unownedEquipPolicy);
                if (enforceUnownedEquip)
                    continue;
            }

            if (storeItem.StoreItemType is StoreItemType.AlternativeAvatar) continue;

            // Always overwrite so the new selected item for this category (prefix) replaces the old choice
            selectedByPrefix[storeItem.Prefix] = storeItem.PrefixedCode;
            Logger.LogInformation(
                "[SelectedUpgrades] Equipped '{PrefixedCode}' (category '{Prefix}') for Account #{AccountId}",
                storeItem.PrefixedCode, storeItem.Prefix, account.ID);
        }

        account.SetSelectedStoreItems(selectedByPrefix.Values);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[SelectedUpgrades] Account #{AccountId} ('{AccountName}') final selections: [{FinalSelections}]",
                account.ID, account.Name, string.Join(", ", selectedByPrefix.Values));
        }

        await AccountRepository.UpdateAsync(account, cancellationToken);
        await InvalidateProfileCacheAsync(account, cancellationToken);

        return new PhpResult(string.Empty);
    }

    private async Task InvalidateProfileCacheAsync(Account account, CancellationToken cancellationToken)
    {
        if (HybridCache != null)
        {
            try
            {
                await HybridCache.RemoveByTagAsync($"account:{account.ID}", cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(account.Name))
                {
                    await HybridCache.RemoveByTagAsync($"account:nick:{account.Name.Trim().ToLowerInvariant()}", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[SelectedUpgrades] Failed to invalidate profile cache tags for Account #{AccountId}", account.ID);
            }
        }
    }

    private static readonly HashSet<string> KnownPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "a.", "av.", "c.", "w.", "t.", "tb.", "te.", "cr.", "sc.", "cs.", "cc.", "ai.", "nc.", "aa.", "h.", "m.", "en."
    };

    private static string? GetItemPrefix(string itemCode)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return null;

        string trimmed = itemCode.Trim();
        if (trimmed.StartsWith("custom_icon", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase))
        {
            return "ai.";
        }

        int dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0)
        {
            string candidate = trimmed[..(dotIndex + 1)].ToLowerInvariant();
            if (KnownPrefixes.Contains(candidate))
            {
                return candidate;
            }
        }

        string withDot = trimmed.ToLowerInvariant() + ".";
        if (KnownPrefixes.Contains(withDot))
        {
            return withDot;
        }

        StoreItem? storeItem = StoreItems.GetByCode(trimmed)
                               ?? StoreItems.GetByPrefixedCode(trimmed)
                               ?? StoreItems.GetByName(trimmed);

        if (storeItem is null && int.TryParse(trimmed, out int numericId))
            storeItem = StoreItems.GetByID(numericId);

        return storeItem?.Prefix;
    }
}
