// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Store;

public interface IBundleCatalogService
{
    BundleContentResponse GetBundleContent(int bundleId, Account? account);
}

public class BundleCatalogService(ILogger<BundleCatalogService> logger) : IBundleCatalogService
{
    // Specific Known Bundle Item Mappings
    private static readonly Dictionary<int, int[]> KnownBundleItems = new()
    {
        // Halloween Party Bundle: Ellie Envy, Bonnie Blood Hunter, Preda Tori, Lynn X, Thriller Rhapsody
        [2609] = [2450, 2452, 2448, 2454, 2597],
        // Support Ward Bundle
        [2692] = [3148, 3149, 3150, 3151, 3152, 3933, 3934, 3935],
        // Classic Ganker Ward Bundle
        [2908] = [3204, 3205, 3206, 3207, 3217, 3218],
        // Classic Jungle Ward Bundle
        [2842] = [3149, 3151, 3245, 3272, 3289],
        // Classic Carry Ward Bundle
        [2865] = [3148, 3150, 3465, 3466, 3495],
        // Classic Mid Ward Bundle
        [3054] = [3152, 4069, 4070, 4429, 4430],
        // Classic Off-Lane Ward Bundle
        [3146] = [4848, 4885, 3313, 3314, 3219],
        // Super Doll Box Bundle: Plushie avatars (Behemoth, Night Hound, Devourer, Cthulhuphant, Pestilence, Deadwood, Pandamonium, Myrmidon, Bubbles)
        [4689] = [4593, 4613, 4621, 4636, 4640, 4654, 4658, 4666, 4670]
    };

    private static readonly Dictionary<string, string> HeroNameToPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Swiftblade"] = "Hero_Hiro.",
        ["Demented Shaman"] = "Hero_Shaman.",
        ["Shaman"] = "Hero_Shaman.",
        ["Bubbles"] = "Hero_Bubbles.",
        ["Rampage"] = "Hero_Rampage.",
        ["Klanx"] = "Hero_Klanx.",
        ["Solstice"] = "Hero_Solstice.",
        ["Bombardier"] = "Hero_Bombardier.",
        ["Pyromancer"] = "Hero_Pyromancer.",
        ["Glacius"] = "Hero_Frosty.",
        ["Witch Slayer"] = "Hero_WitchSlayer.",
        ["Gravekeeper"] = "Hero_Gravekeeper.",
        ["Succubus"] = "Hero_Succubis.",
        ["Flint"] = "Hero_FlintBeastwood.",
        ["Valkyrie"] = "Hero_Valkyrie.",
        ["Chronos"] = "Hero_Chronos.",
        ["Predator"] = "Hero_Predator.",
        ["Magmus"] = "Hero_Magmus.",
        ["Behemoth"] = "Hero_Behemoth.",
        ["Gauntlet"] = "Hero_Gauntlet.",
        ["Devourer"] = "Hero_Devourer.",
        ["Legionnaire"] = "Hero_Legionnaire.",
        ["Pebbles"] = "Hero_Rocky.",
        ["Deadwood"] = "Hero_Deadwood.",
        ["Moraxus"] = "Hero_Moraxus.",
        ["Armadon"] = "Hero_Armadon.",
        ["Monkey King"] = "Hero_MonkeyKing.",
        ["Artillery"] = "Hero_Artillery.",
        ["Chipper"] = "Hero_Chipper.",
        ["Artesia"] = "Hero_Artesia.",
        ["Mimix"] = "Hero_Mimix."
    };

    private static StoreItemsConfiguration StoreItems => JSONConfiguration.StoreItemsConfiguration;

    public BundleContentResponse GetBundleContent(int bundleId, Account? account)
    {
        StoreItem? bundleItem = StoreItems.GetByID(bundleId);
        string bundleName = bundleItem?.Name ?? $"Bundle {bundleId}";

        bool isGrabBag = bundleName.Contains("Grab Bag", StringComparison.OrdinalIgnoreCase) ||
                         bundleName.Contains("Grabbag", StringComparison.OrdinalIgnoreCase) ||
                         bundleName.Contains("Bag", StringComparison.OrdinalIgnoreCase) ||
                         bundleName.Contains("Box", StringComparison.OrdinalIgnoreCase);

        List<StoreItem> includedItems = ResolveIncludedItems(bundleId, bundleName, isGrabBag);

        HashSet<string> ownedItems = account?.User?.InventoryItems != null
            ? new HashSet<string>(account.User.InventoryItems.Select(i => i.ItemCode), StringComparer.OrdinalIgnoreCase)
            : [];

        List<BundleItemDTO> itemsList =
        [
            .. includedItems.Select(item =>
            {
                bool itemOwned = ownedItems.Contains(item.PrefixedCode) ||
                                 ownedItems.Contains(item.Code) ||
                                 (item.PrefixedCode.StartsWith("w.", StringComparison.Ordinal) &&
                                  ownedItems.Contains(item.PrefixedCode[2..])) ||
                                 (item.PrefixedCode.StartsWith("aa.", StringComparison.Ordinal) &&
                                  ownedItems.Contains(item.PrefixedCode[3..])) ||
                                 (item.Code.StartsWith("w.", StringComparison.Ordinal) &&
                                  ownedItems.Contains(item.Code[2..])) ||
                                 (item.Code.StartsWith("aa.", StringComparison.Ordinal) &&
                                  ownedItems.Contains(item.Code[3..]));

                return new BundleItemDTO
                {
                    ProductID = item.ID,
                    ItemCode = item.PrefixedCode,
                    GoldCoins = item.GoldCost,
                    SilverCoins = item.SilverCost,
                    LocalContent = item.Resource,
                    IsOwned = itemOwned
                };
            })
        ];

        bool bundleExplicitlyOwned = bundleItem is not null &&
                                     (ownedItems.Contains(bundleItem.PrefixedCode) ||
                                      ownedItems.Contains(bundleItem.Code));

        bool allOwned = bundleExplicitlyOwned || (itemsList.Count > 0 && itemsList.All(x => x.IsOwned));
        int unownedCount = itemsList.Count(x => !x.IsOwned);

        string theme = "default";
        string model = string.Empty;

        if (isGrabBag)
        {
            if (bundleName.Contains("Halloween", StringComparison.OrdinalIgnoreCase))
            {
                theme = "halloween";
                model = "/ui/fe2/grabbag/halloween/pumpkin.mdf";
            }
            else if (bundleName.Contains("Christmas", StringComparison.OrdinalIgnoreCase))
            {
                theme = "christmas";
                model = "/ui/fe2/grabbag/christmas/present/present.mdf";
            }
            else
            {
                theme = "lucky_bundle";
                model = "/ui/fe2/grabbag/lucky_bundle/footlocker.mdf";
            }
        }

        BundleInfoDTO info = new()
        {
            BundleID = bundleId,
            GrabBag = isGrabBag,
            PrizeQuantity = isGrabBag ? 1 : Math.Max(1, itemsList.Count),
            MaxPurchasable = isGrabBag ? Math.Max(0, unownedCount) : allOwned ? 0 : 1,
            GoldCoins = bundleItem?.GoldCost ?? 300,
            SilverCoins = bundleItem?.SilverCost ?? 2000,
            IsOwned = allOwned,
            UiTheme = theme,
            Model = model
        };

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[BundleCatalog] Resolved bundle {BundleId} ({BundleName}): {ItemCount} items, IsGrabBag={IsGrabBag}, AllOwned={AllOwned}, UnownedCount={UnownedCount}",
                bundleId, bundleName, itemsList.Count, isGrabBag, allOwned, unownedCount);

        return new BundleContentResponse { Info = info, List = itemsList };
    }

    private static List<StoreItem> ResolveIncludedItems(int bundleId, string bundleName, bool isGrabBag)
    {
        // 1. Direct Known Bundle ID Mapping
        if (KnownBundleItems.TryGetValue(bundleId, out int[]? itemIds))
        {
            List<StoreItem> explicitItems =
            [
                .. itemIds
                    .Select(StoreItems.GetByID)
                    .Where(item => item is not null)
                    .Cast<StoreItem>()
            ];

            if (explicitItems.Count > 0) return explicitItems;
        }

        // 2. Hero Name Resolution for Hero Grab Bags
        string heroPrefix = ExtractHeroPrefixFromBundle(bundleName);
        if (!string.IsNullOrEmpty(heroPrefix))
        {
            List<StoreItem> avatars =
            [
                .. StoreItems.GetEnabledItemsByType(StoreItemType.AlternativeAvatar)
                    .Where(item => item.Code.StartsWith(heroPrefix, StringComparison.OrdinalIgnoreCase))
            ];

            if (avatars.Count > 0) return avatars;
        }

        // 3. Check for Halloween / Holiday Bundles
        if (bundleName.Contains("Halloween", StringComparison.OrdinalIgnoreCase))
        {
            List<StoreItem> halloweenItems =
            [
                .. StoreItems.GetEnabledItemsByType(StoreItemType.AlternativeAvatar)
                    .Where(item => item.Code.Contains("Halloween", StringComparison.OrdinalIgnoreCase) ||
                                   item.Name.Contains("Halloween", StringComparison.OrdinalIgnoreCase) ||
                                   item.Name.Contains("Thriller", StringComparison.OrdinalIgnoreCase) ||
                                   item.Name.Contains("Bonnie", StringComparison.OrdinalIgnoreCase))
                    .Take(6)
            ];

            if (halloweenItems.Count > 0) return halloweenItems;
        }

        // 4. Check for Ward Bundles
        if (bundleName.Contains("Ward", StringComparison.OrdinalIgnoreCase))
        {
            List<StoreItem> wardItems =
            [
                .. StoreItems.GetEnabledItemsByType(StoreItemType.Ward)
                    .Take(6)
            ];

            if (wardItems.Count > 0) return wardItems;
        }

        // 5. Generic / Seasonal Grab Bags
        if (bundleName.Contains("Taunt", StringComparison.OrdinalIgnoreCase))
            return [.. StoreItems.GetEnabledItemsByType(StoreItemType.Taunt).Take(6)];

        if (bundleName.Contains("Courier", StringComparison.OrdinalIgnoreCase))
            return [.. StoreItems.GetEnabledItemsByType(StoreItemType.Courier).Take(6)];

        // 6. Fallback: Return sample enabled avatars
        List<StoreItem> fallbackAvatars =
        [
            .. StoreItems.GetEnabledItemsByType(StoreItemType.AlternativeAvatar)
                .Take(6)
        ];

        return fallbackAvatars;
    }

    private static string ExtractHeroPrefixFromBundle(string bundleName)
    {
        foreach ((string heroName, string prefix) in HeroNameToPrefix)
            if (bundleName.Contains(heroName, StringComparison.OrdinalIgnoreCase))
                return prefix;

        return string.Empty;
    }
}
