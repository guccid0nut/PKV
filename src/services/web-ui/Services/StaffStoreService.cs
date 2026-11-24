// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

#pragma warning disable CA2254
#pragma warning disable CA1848
#pragma warning disable CA1304
#pragma warning disable CA1305
#pragma warning disable CA1311

using System.Collections.Concurrent;
using System.Text.Json;
using DAWNBRINGER.WebPortal.UI.Models;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;

namespace DAWNBRINGER.WebPortal.UI.Services;

public interface IStaffStoreService
{
    bool IsUnlockAllItemsEnabled => true;

    StaffStoreViewModel GetCatalog(string? category, string? search, int page = 1, int pageSize = 16,
        IReadOnlyCollection<string>? userOwnedCodes = null, string? ownershipFilter = "all",
        IReadOnlyCollection<string>? userEquippedCodes = null);

    StaffStoreViewModel GetCatalog(string? category, string? search, int page, int pageSize,
        IReadOnlyCollection<string>? userOwnedCodes, string? ownershipFilter,
        IReadOnlyCollection<string>? userEquippedCodes, bool isAuthenticated) =>
        GetCatalog(category, search, page, pageSize, userOwnedCodes, ownershipFilter, userEquippedCodes);

    StaffStoreItemDto? GetItemById(int id);
    StaffStoreItemDto? ResolveItem(string? codeOrIdOrName);
    IReadOnlyList<StaffStoreItemDto> GetAllItems();
    IReadOnlyList<StaffStoreItemDto> GetItemsByHero(string heroName);
    IReadOnlyList<StaffStoreItemDto> GetSearchableAvatars();
    bool UpdateItem(int id, int goldCost, int silverCost, bool isEnabled, bool purchasable);
    bool ApplyBulkDiscount(string category, string currency, int discountPercent);
    bool RestoreBaselinePrices();
}

public sealed class StaffStoreService : IStaffStoreService
{
    private static readonly HashSet<string> DeprioritizedAvatarCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hero_Pyromancer.Female",
        "Hero_Krixi.Sexy",
        "Hero_Madman.Nightmare",
        "Hero_Succubus.Naughty",
        "Hero_Gauntlet.El",
        "Hero_Gauntlet.Alt"
    };

    private static readonly ConcurrentDictionary<string, List<StaffStoreItemDto>> s_cachedParsedItems =
        new(StringComparer.OrdinalIgnoreCase);

    private static string? s_cachedResolvedStoreJsonPath;
    private static string? s_cachedResolvedUpgradesJsonPath;
    private static readonly Lock s_pathResolveLock = new();

    private readonly Dictionary<int, (int Gold, int Silver)> _baselinePrices = [];

    private readonly IConfiguration _configuration;
    private readonly bool _isCustomStorePath;
    private readonly List<StaffStoreItemDto> _items = [];
    private readonly Lock _lock = new();
    private readonly ILogger<StaffStoreService> _logger;
    private bool _loaded;
    private string? _storeJsonPath;
    private string? _upgradesJsonPath;

    public StaffStoreService(IConfiguration configuration, ILogger<StaffStoreService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        string? configuredStorePath = configuration["StaffStore:StoreJsonPath"] ?? configuration["Store:StoreJsonPath"];
        if (!string.IsNullOrEmpty(configuredStorePath))
        {
            _storeJsonPath = configuredStorePath;
            _isCustomStorePath = true;
        }

        string? configuredUpgradesPath =
            configuration["StaffStore:UpgradesJsonPath"] ?? configuration["Store:UpgradesJsonPath"];
        if (!string.IsNullOrEmpty(configuredUpgradesPath))
        {
            _upgradesJsonPath = configuredUpgradesPath;
        }
    }

    public bool IsUnlockAllItemsEnabled =>
        NEXUS.Contracts.Store.StoreEntitlementPolicy.IsFreeUnlockAll(_configuration, defaultFreeAll: true);

    private static StaffStoreItemDto CloneForView(
        StaffStoreItemDto src,
        bool unlockAll,
        bool isOwned,
        bool isEquipped)
    {
        bool isRestricted = NEXUS.Contracts.Store.StoreEntitlementPolicy.IsRestrictedEntitlement(src.Code, null, src.Name);
        bool effectiveFreeUnlock = unlockAll && !isRestricted;
        return new()
        {
            Id = src.Id,
            Name = src.Name,
            Code = src.Code,
            Type = src.Type,
            TypeName = src.TypeName,
            Resource = src.Resource,
            GoldCost = effectiveFreeUnlock ? 0 : src.GoldCost,
            SilverCost = effectiveFreeUnlock ? 0 : src.SilverCost,
            Purchasable = !isRestricted && (effectiveFreeUnlock || src.Purchasable),
            IsPremium = src.IsPremium,
            IsBundle = src.IsBundle,
            IsEnabled = effectiveFreeUnlock || src.IsEnabled,
            IsOwnedByUser = isOwned,
            IsEquippedByUser = isEquipped,
            HeroName = src.HeroName,
            AltAvatarSuffix = src.AltAvatarSuffix,
            Description = src.Description
        };
    }

    public StaffStoreViewModel GetCatalog(
        string? category,
        string? search,
        int page = 1,
        int pageSize = 12,
        IReadOnlyCollection<string>? userOwnedCodes = null,
        string? ownershipFilter = "all",
        IReadOnlyCollection<string>? userEquippedCodes = null) =>
        GetCatalog(category, search, page, pageSize, userOwnedCodes, ownershipFilter, userEquippedCodes, false);

    public StaffStoreViewModel GetCatalog(
        string? category,
        string? search,
        int page,
        int pageSize,
        IReadOnlyCollection<string>? userOwnedCodes,
        string? ownershipFilter,
        IReadOnlyCollection<string>? userEquippedCodes,
        bool isAuthenticated)
    {
        EnsureLoaded();

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 12;

        string mainCat = "alt_avatars";
        string? subCat = null;
        string catLower = (category ?? "avatars").ToLowerInvariant();

        IEnumerable<StaffStoreItemDto> query = _items;

        if (catLower == "all")
        {
            mainCat = "all";
            subCat = null;
        }
        else if (catLower is "specials" or "featured")
        {
            mainCat = "featured";
            subCat = null;
            query = query.Where(x => x.IsPremium || x.GoldCost > 0);
        }
        else if (catLower is "avatars" or "avatar" or "alt_avatars" or "alt_avatar" or "heroes" or "hero")
        {
            mainCat = "alt_avatars";
            subCat = "avatars";
            query = query
                .Where(x => x.Type == 3 || x.Type == 12 ||
                            (x.Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && x.Code.Contains('.')))
                .OrderBy(x => DeprioritizedAvatarCodes.Contains(x.Code) ? 1 : 0);
        }
        else if (catLower is "announcers" or "announcer" or "announcement" or "announcements" or "voices" or "voice")
        {
            mainCat = "announcers";
            subCat = "announcers";
            query = query.Where(x =>
                x.Type == 4 || x.TypeName.Contains("Announcer", StringComparison.OrdinalIgnoreCase) ||
                x.Code.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains("announcer", StringComparison.OrdinalIgnoreCase) ||
                (x.Type == 4 && x.Name.Contains("pack", StringComparison.OrdinalIgnoreCase)));
        }
        else if (catLower is "taunts" or "taunt" or "smackdown")
        {
            mainCat = "taunts";
            subCat = "taunts";
            query = query.Where(x =>
                x.Type == 5 || x.Type == 2 || x.TypeName.Equals("Taunt", StringComparison.OrdinalIgnoreCase) ||
                x.Code.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains("taunt", StringComparison.OrdinalIgnoreCase) ||
                x.Code.EndsWith("_Taunt", StringComparison.OrdinalIgnoreCase) ||
                x.Code.Equals("Smackdown", StringComparison.OrdinalIgnoreCase) ||
                x.Code.Equals("GDrop", StringComparison.OrdinalIgnoreCase));
        }
        else if (catLower is "couriers" or "courier")
        {
            mainCat = "game_vanity";
            subCat = "couriers";
            query = query.Where(x =>
                x.Type == 6 || x.Code.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains("courier", StringComparison.OrdinalIgnoreCase));
        }
        else if (catLower is "wards" or "ward")
        {
            mainCat = "game_vanity";
            subCat = "wards";
            query = query.Where(x =>
                x.Type == 7 || x.Type == 11 || x.Code.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains("ward", StringComparison.OrdinalIgnoreCase));
        }
        else if (catLower == "game_vanity")
        {
            mainCat = "game_vanity";
            subCat = "couriers";
            query = query.Where(x => x.Type is 6 or 7 or 11);
        }
        else if (catLower is "account_vanity" or "icons" or "icon" or "flags" or "flag")
        {
            mainCat = "account_vanity";
            subCat = "icons";
            query = query.Where(x =>
                x.Type == 1 || x.Name.StartsWith("Flag", StringComparison.OrdinalIgnoreCase) ||
                x.Code.StartsWith("icon.", StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains("Icon", StringComparison.OrdinalIgnoreCase));
        }
        else if (catLower is "colors" or "color" or "shields" or "shield" or "upgrades" or "upgrade")
        {
            mainCat = "account_vanity";
            subCat = "colors";
            query = query.Where(x => x.Type == 0 || x.Code.Contains("shield") || x.Name.Contains("Color"));
        }
        else if (catLower is "services" or "service")
        {
            mainCat = "account_vanity";
            subCat = "services";
            query = query.Where(x => x.Type == 10 || x.Name.Contains("Change") || x.Name.Contains("Reset"));
        }
        else if (catLower is "bundles" or "bundle")
        {
            mainCat = "bundles";
            subCat = null;
            query = query.Where(x =>
                (x.Type == 8 || x.Name.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
                 (x.Code.StartsWith("bundle", StringComparison.OrdinalIgnoreCase) &&
                  !x.Code.EndsWith(".AltBundle", StringComparison.OrdinalIgnoreCase))) &&
                !x.Code.EndsWith(".AltBundle", StringComparison.OrdinalIgnoreCase));
        }
        else if (catLower is "plinko" or "daily")
        {
            mainCat = "plinko";
            subCat = null;
            query = [];
        }
        else if (catLower == "other")
        {
            mainCat = "other";
            subCat = null;
            query = query.Where(x => x.Type is 15 or 18 or 19 or 10);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string s = search.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.Name.ToLowerInvariant().Contains(s) ||
                x.Code.ToLowerInvariant().Contains(s) ||
                x.Id.ToString().Contains(s) ||
                x.HeroName.ToLowerInvariant().Contains(s));
        }

        bool unlockAll = IsUnlockAllItemsEnabled;
        HashSet<string> normalizedOwnedSet = BuildNormalizedOwnedLookup(userOwnedCodes);
        HashSet<string> equippedSet = new(userEquippedCodes ?? [], StringComparer.OrdinalIgnoreCase);

        List<StaffStoreItemDto> matchedCategoryItems = [];
        foreach (StaffStoreItemDto rawItem in query)
        {
            bool isOwned = (unlockAll && isAuthenticated) || IsItemOwned(rawItem, normalizedOwnedSet);
            bool isEquipped = IsItemEquipped(rawItem, equippedSet);
            matchedCategoryItems.Add(CloneForView(rawItem, unlockAll, isOwned, isEquipped));
        }

        if (unlockAll && isAuthenticated)
        {
            foreach (StaffStoreItemDto item in _items)
            {
                if (!string.IsNullOrWhiteSpace(item.Code))
                {
                    normalizedOwnedSet.Add(item.Code);
                }
            }
        }

        int totalOwned = matchedCategoryItems.Count(x => x.IsOwnedByUser);
        int totalUnowned = matchedCategoryItems.Count - totalOwned;
        int totalEquipped = matchedCategoryItems.Count(x => x.IsEquippedByUser);

        List<StaffStoreItemDto> filteredItems = ownershipFilter?.ToLowerInvariant() switch
        {
            "equipped" => [.. matchedCategoryItems.Where(x => x.IsEquippedByUser)],
            "owned" => [.. matchedCategoryItems.Where(x => x.IsOwnedByUser)],
            "unowned" => [.. matchedCategoryItems.Where(x => !x.IsOwnedByUser)],
            _ => matchedCategoryItems
        };

        int totalItems = filteredItems.Count;
        int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
        if (totalPages < 1) totalPages = 1;
        if (page > totalPages) page = totalPages;

        List<StaffStoreItemDto> pageItems =
        [
            .. filteredItems
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
        ];

        return new StaffStoreViewModel
        {
            Items = pageItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalItems,
            IsUnlockAllItemsEnabled = unlockAll,
            MainCategory = mainCat,
            SubCategory = subCat,
            TotalAvatars =
                _items.Count(x =>
                    x.Type == 3 || x.Type == 12 || (x.Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) &&
                                                    x.Code.Contains('.'))),
            TotalTaunts =
                _items.Count(x =>
                    x.Type == 5 || x.Type == 2 || x.Code.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("taunt", StringComparison.OrdinalIgnoreCase)),
            TotalCouriers =
                _items.Count(x =>
                    x.Type == 6 || x.Code.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("courier", StringComparison.OrdinalIgnoreCase)),
            TotalWards =
                _items.Count(x =>
                    x.Type == 7 || x.Type == 11 || x.Code.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("ward", StringComparison.OrdinalIgnoreCase)),
            TotalAnnouncers =
                _items.Count(x =>
                    x.Type == 4 || x.Code.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("announcer", StringComparison.OrdinalIgnoreCase)),
            TotalIcons =
                _items.Count(x =>
                    x.Type == 1 || x.Name.StartsWith("Flag", StringComparison.OrdinalIgnoreCase) ||
                    x.Code.StartsWith("icon.", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("Icon", StringComparison.OrdinalIgnoreCase)),
            TotalUpgrades =
                _items.Count(x =>
                    x.Type == 0 || x.Code.Contains("shield", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("Color", StringComparison.OrdinalIgnoreCase)),
            TotalBundles =
                _items.Count(x =>
                    (x.Type == 8 || x.Name.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
                     x.Code.StartsWith("bundle", StringComparison.OrdinalIgnoreCase)) &&
                    !x.Code.EndsWith(".AltBundle", StringComparison.OrdinalIgnoreCase)),
            SelectedCategory = category ?? "avatars",
            SearchQuery = search,
            OwnershipFilter = ownershipFilter ?? "all",
            TotalOwnedInFilter = totalOwned,
            TotalUnownedInFilter = totalUnowned,
            TotalEquippedInFilter = totalEquipped,
            UserOwnedItemCodes = normalizedOwnedSet,
            UserEquippedItemCodes = equippedSet
        };
    }

    public StaffStoreItemDto? GetItemById(int id)
    {
        EnsureLoaded();
        bool unlockAll = IsUnlockAllItemsEnabled;
        lock (_lock)
        {
            StaffStoreItemDto? item = _items.FirstOrDefault(x => x.Id == id);
            return item == null ? null : CloneForView(item, unlockAll, item.IsOwnedByUser, item.IsEquippedByUser);
        }
    }

    public IReadOnlyList<StaffStoreItemDto> GetAllItems()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return [.. _items.Select(x => CloneForView(x, IsUnlockAllItemsEnabled, x.IsOwnedByUser, x.IsEquippedByUser))];
        }
    }

    public StaffStoreItemDto? ResolveItem(string? codeOrIdOrName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(codeOrIdOrName)) return null;

        string target = codeOrIdOrName.Trim();

        lock (_lock)
        {
            // 1. Check direct ID
            if (int.TryParse(target, out int id))
            {
                StaffStoreItemDto? byId = _items.FirstOrDefault(x => x.Id == id);
                if (byId != null) return CloneForView(byId, IsUnlockAllItemsEnabled, byId.IsOwnedByUser, byId.IsEquippedByUser);
            }

            // 2. Exact code match
            StaffStoreItemDto? match =
                _items.FirstOrDefault(x => x.Code.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (match != null) return CloneForView(match, IsUnlockAllItemsEnabled, match.IsOwnedByUser, match.IsEquippedByUser);

            // 3. Normalized prefix stripping
            string clean = target;
            if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
            else if (clean.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("c.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
            else if (clean.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("cs.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
            else if (clean.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];

            match = _items.FirstOrDefault(x => x.Code.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (match != null) return CloneForView(match, IsUnlockAllItemsEnabled, match.IsOwnedByUser, match.IsEquippedByUser);

            // 4. Exact or fuzzy Name match
            match = _items.FirstOrDefault(x =>
                x.Name.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (match != null) return CloneForView(match, IsUnlockAllItemsEnabled, match.IsOwnedByUser, match.IsEquippedByUser);

            // 5. Hero & Alt Avatar format (e.g. Hero_Mimix.Alt, Mimix.Alt, aa.Hero_Mimix.Alt)
            if (clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase) || clean.Contains('.'))
            {
                string heroAlt = clean;
                if (heroAlt.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) heroAlt = heroAlt[5..];
                else if (heroAlt.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase)) heroAlt = heroAlt[7..];

                string[] parts = heroAlt.Split('.');
                string hero = parts[0];
                string alt = parts.Length > 1 ? parts[1] : "";

                match = _items.FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.HeroName) &&
                    x.HeroName.Equals(hero, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(alt) || x.AltAvatarSuffix.Equals(alt, StringComparison.OrdinalIgnoreCase) ||
                     x.Code.EndsWith($".{alt}", StringComparison.OrdinalIgnoreCase)));
                if (match != null) return CloneForView(match, IsUnlockAllItemsEnabled, match.IsOwnedByUser, match.IsEquippedByUser);
            }

            // 6. Common vanity prefixes match
            match = _items.FirstOrDefault(x =>
                x.Code.EndsWith(clean, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains(clean, StringComparison.OrdinalIgnoreCase));
            if (match != null) return CloneForView(match, IsUnlockAllItemsEnabled, match.IsOwnedByUser, match.IsEquippedByUser);

            // 7. Fallback item with clean defaults for unrecognized custom codes
            string typeName = "Cosmetic";
            string resource = "/ui/fe2/store/icons/hero_default.tga";
            int itemType = 99;

            if (target.StartsWith("aa.", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase) ||
                (target.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && target.Contains('.')))
            {
                typeName = "Alt Avatar";
                itemType = 3;
            }
            else if (target.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith("AccountIcon_", StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith("icon.", StringComparison.OrdinalIgnoreCase))
            {
                typeName = "Account Icon";
                itemType = 1;
                resource = "/ui/fe2/store/icons/default.tga";
            }
            else if (target.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith("cs.", StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith("NameColor_", StringComparison.OrdinalIgnoreCase))
            {
                typeName = "Name Color";
                itemType = 0;
                resource = "/ui/fe2/store/icons/colors/gold.tga";
            }
            else if (target.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith("Taunt_", StringComparison.OrdinalIgnoreCase) ||
                     target.Contains("Taunt", StringComparison.OrdinalIgnoreCase) ||
                     target.Equals("Smackdown", StringComparison.OrdinalIgnoreCase))
            {
                typeName = "Taunt";
                itemType = 5;
                resource = "/ui/fe2/store/icons/smackdown.tga";
            }
            else if (target.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                     target.StartsWith("Announcer_", StringComparison.OrdinalIgnoreCase))
            {
                typeName = "Announcer";
                itemType = 4;
                resource = "/ui/fe2/store/icons/announcers/default.tga";
            }
            else if (target.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
                     target.Contains("Courier", StringComparison.OrdinalIgnoreCase))
            {
                typeName = "Courier";
                itemType = 6;
                resource = "/ui/fe2/store/icons/courier_default.tga";
            }
            else if (target.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                     target.Contains("Ward", StringComparison.OrdinalIgnoreCase))
            {
                typeName = "Ward";
                itemType = 7;
                resource = "/ui/fe2/store/icons/ward_default.tga";
            }

            return new StaffStoreItemDto
            {
                Id = 0,
                Name = clean.Replace("Avatar_", "").Replace("Hero_", "").Replace("AccountIcon_", "")
                    .Replace("NameColor_", "").Replace("Taunt_", "").Replace("Announcer_", "").Replace("_", " "),
                Code = target,
                Type = itemType,
                TypeName = typeName,
                Resource = resource,
                Purchasable = IsUnlockAllItemsEnabled,
                IsEnabled = true,
                Description = "In-game unlocked cosmetic item."
            };
        }
    }

    public IReadOnlyList<StaffStoreItemDto> GetItemsByHero(string heroName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(heroName)) return [];

        string clean = heroName.Trim().ToLowerInvariant();
        if (clean.StartsWith("hero_", StringComparison.OrdinalIgnoreCase)) clean = clean[5..];
        bool unlockAll = IsUnlockAllItemsEnabled;

        lock (_lock)
        {
            return
            [
                .. _items
                    .Where(x => !string.IsNullOrEmpty(x.HeroName) &&
                                x.HeroName.Equals(clean, StringComparison.OrdinalIgnoreCase))
                    .Select(x => CloneForView(x, unlockAll, x.IsOwnedByUser, x.IsEquippedByUser))
            ];
        }
    }

    public IReadOnlyList<StaffStoreItemDto> GetSearchableAvatars()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return
            [
                .. _items
                    .Where(x => x.Type == 3 || x.Type == 12 ||
                                (x.Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) &&
                                 x.Code.Contains('.')))
                    .Select(x => CloneForView(x, IsUnlockAllItemsEnabled, x.IsOwnedByUser, x.IsEquippedByUser))
            ];
        }
    }

    public bool UpdateItem(int id, int goldCost, int silverCost, bool isEnabled, bool purchasable)
    {
        EnsureLoaded();

        lock (_lock)
        {
            StaffStoreItemDto? item = _items.FirstOrDefault(x => x.Id == id);
            if (item == null)
            {
                return false;
            }

            item.GoldCost = goldCost;
            item.SilverCost = silverCost;
            item.IsEnabled = isEnabled;
            item.Purchasable = purchasable;

            return PersistAllItemsToJson();
        }
    }

    public bool ApplyBulkDiscount(string category, string currency, int discountPercent)
    {
        EnsureLoaded();
        if (discountPercent is <= 0 or > 95) return false;

        lock (_lock)
        {
            // Back up original baseline prices if not already saved
            if (_baselinePrices.Count == 0)
            {
                foreach (StaffStoreItemDto dto in _items)
                {
                    _baselinePrices[dto.Id] = (dto.GoldCost, dto.SilverCost);
                }
            }

            string cat = (string.IsNullOrEmpty(category) ? "all" : category).ToLowerInvariant();
            string curr = (string.IsNullOrEmpty(currency) ? "both" : currency).ToLowerInvariant();

            foreach (StaffStoreItemDto item in _items)
            {
                bool matchesCategory = cat switch
                {
                    "all" => true,
                    "avatars" => item.Type == 3 || item.Type == 12 ||
                                 (item.Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) &&
                                  item.Code.Contains('.')),
                    "taunts" => item.Type == 5 || item.Type == 2 ||
                                item.Code.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                                item.Name.Contains("taunt", StringComparison.OrdinalIgnoreCase),
                    "couriers" => item.Type == 6 || item.Code.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
                                  item.Name.Contains("courier", StringComparison.OrdinalIgnoreCase),
                    "wards" => item.Type == 7 || item.Type == 11 ||
                               item.Code.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                               item.Name.Contains("ward", StringComparison.OrdinalIgnoreCase),
                    "announcers" => item.Type == 4 || item.Code.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                                    item.Name.Contains("announcer", StringComparison.OrdinalIgnoreCase),
                    "icons" => item.Type == 1 || item.Name.StartsWith("Flag", StringComparison.OrdinalIgnoreCase) ||
                               item.Code.StartsWith("icon.", StringComparison.OrdinalIgnoreCase) ||
                               item.Name.Contains("Icon", StringComparison.OrdinalIgnoreCase),
                    "colors" => item.Type == 0 || item.Code.Contains("shield", StringComparison.OrdinalIgnoreCase) ||
                                item.Name.Contains("Color", StringComparison.OrdinalIgnoreCase),
                    "bundles" => (item.Type == 8 || item.Name.Contains("bundle", StringComparison.OrdinalIgnoreCase)) &&
                                 !item.Code.EndsWith(".AltBundle", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };

                if (!matchesCategory) continue;

                (int baseGold, int baseSilver) = _baselinePrices.TryGetValue(item.Id, out (int G, int S) basePrice)
                    ? basePrice
                    : (item.GoldCost, item.SilverCost);

                if (curr is "gold" or "both")
                {
                    if (baseGold > 0)
                    {
                        item.GoldCost = (int)Math.Max(1, Math.Round(baseGold * (100.0 - discountPercent) / 100.0));
                    }
                }

                if (curr is "silver" or "both")
                {
                    if (baseSilver > 0)
                    {
                        item.SilverCost = (int)Math.Max(1, Math.Round(baseSilver * (100.0 - discountPercent) / 100.0));
                    }
                }
            }

            return PersistAllItemsToJson();
        }
    }

    public bool RestoreBaselinePrices()
    {
        EnsureLoaded();

        lock (_lock)
        {
            if (_baselinePrices.Count == 0) return true;

            foreach (StaffStoreItemDto item in _items)
            {
                if (_baselinePrices.TryGetValue(item.Id, out (int baseGold, int baseSilver) basePrice))
                {
                    item.GoldCost = basePrice.baseGold;
                    item.SilverCost = basePrice.baseSilver;
                }
            }

            _baselinePrices.Clear();
            return PersistAllItemsToJson();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }

            if (_storeJsonPath == null)
            {
                if (s_cachedResolvedStoreJsonPath != null)
                {
                    _storeJsonPath = s_cachedResolvedStoreJsonPath;
                    _upgradesJsonPath = s_cachedResolvedUpgradesJsonPath;
                }
                else
                {
                    lock (s_pathResolveLock)
                    {
                        if (s_cachedResolvedStoreJsonPath != null)
                        {
                            _storeJsonPath = s_cachedResolvedStoreJsonPath;
                            _upgradesJsonPath = s_cachedResolvedUpgradesJsonPath;
                        }
                        else
                        {
                            string baseDir = AppContext.BaseDirectory;
                            string localStore = Path.Combine(baseDir, "Configuration", "Store",
                                "StoreItemsConfiguration.json");
                            if (File.Exists(localStore))
                            {
                                _storeJsonPath = localStore;
                            }

                            string localUpgrades = Path.Combine(baseDir, "Data", "Seed", "Upgrades.JSON");
                            if (File.Exists(localUpgrades))
                            {
                                _upgradesJsonPath = localUpgrades;
                            }

                            DirectoryInfo? current = new(baseDir);
                            while (current != null)
                            {
                                string candidate = Path.Combine(current.FullName, "src", "services", "master-server",
                                    "Configuration", "Store",
                                    "StoreItemsConfiguration.json");
                                if (!File.Exists(candidate))
                                {
                                    candidate = Path.Combine(current.FullName, "services", "master-server", "src",
                                        "Configuration", "Store",
                                        "StoreItemsConfiguration.json");
                                }

                                if (!File.Exists(candidate))
                                {
                                    candidate = Path.Combine(current.FullName, "services", "master-server",
                                        "Configuration", "Store",
                                        "StoreItemsConfiguration.json");
                                }

                                if (!File.Exists(candidate))
                                {
                                    candidate = Path.Combine(current.FullName, "KONGOR.MasterServer", "Configuration",
                                        "Store",
                                        "StoreItemsConfiguration.json");
                                }

                                if (File.Exists(candidate))
                                {
                                    _storeJsonPath = candidate;
                                }

                                string candidateUpgrades = Path.Combine(current.FullName, "src", "services",
                                    "master-server", "Data", "Seed",
                                    "Upgrades.JSON");
                                if (!File.Exists(candidateUpgrades))
                                {
                                    candidateUpgrades = Path.Combine(current.FullName, "services", "master-server",
                                        "src", "Data", "Seed",
                                        "Upgrades.JSON");
                                }

                                if (!File.Exists(candidateUpgrades))
                                {
                                    candidateUpgrades = Path.Combine(current.FullName, "services", "master-server",
                                        "Data", "Seed",
                                        "Upgrades.JSON");
                                }

                                if (!File.Exists(candidateUpgrades))
                                {
                                    candidateUpgrades = Path.Combine(current.FullName, "KONGOR.MasterServer", "Data",
                                        "Seed",
                                        "Upgrades.JSON");
                                }

                                if (File.Exists(candidateUpgrades))
                                {
                                    _upgradesJsonPath = candidateUpgrades;
                                }

                                if (_storeJsonPath != null && _upgradesJsonPath != null)
                                {
                                    break;
                                }

                                current = current.Parent;
                            }

                            s_cachedResolvedStoreJsonPath = _storeJsonPath;
                            s_cachedResolvedUpgradesJsonPath = _upgradesJsonPath;
                        }
                    }
                }
            }

            if (_storeJsonPath != null && File.Exists(_storeJsonPath))
            {
                if (s_cachedParsedItems.TryGetValue(_storeJsonPath, out List<StaffStoreItemDto>? cachedList))
                {
                    _items.Clear();
                    foreach (StaffStoreItemDto item in cachedList)
                    {
                        _items.Add(new StaffStoreItemDto
                        {
                            Id = item.Id,
                            Name = item.Name,
                            Code = item.Code,
                            Type = item.Type,
                            TypeName = item.TypeName,
                            Resource = item.Resource,
                            Purchasable = item.Purchasable,
                            GoldCost = item.GoldCost,
                            SilverCost = item.SilverCost,
                            IsPremium = item.IsPremium,
                            IsBundle = item.IsBundle,
                            IsEnabled = item.IsEnabled,
                            HeroName = item.HeroName,
                            AltAvatarSuffix = item.AltAvatarSuffix,
                            Description = item.Description
                        });
                    }
                }
                else
                {
                    try
                    {
                        string json = File.ReadAllText(_storeJsonPath);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        _items.Clear();

                        foreach (JsonElement el in doc.RootElement.EnumerateArray())
                        {
                            int id = el.TryGetProperty("ID", out JsonElement idEl) ? idEl.GetInt32() : 0;
                            string name = el.TryGetProperty("Name", out JsonElement nameEl)
                                ? nameEl.GetString() ?? ""
                                : "";
                            string code = el.TryGetProperty("Code", out JsonElement codeEl)
                                ? codeEl.GetString() ?? ""
                                : "";
                            int type = el.TryGetProperty("Type", out JsonElement typeEl) ? typeEl.GetInt32() : 0;
                            string resource = el.TryGetProperty("Resource", out JsonElement resEl)
                                ? resEl.GetString() ?? ""
                                : "";
                            bool purchasable = el.TryGetProperty("Purchasable", out JsonElement pEl) &&
                                               pEl.GetBoolean();
                            int goldCost = el.TryGetProperty("GoldCost", out JsonElement gEl) ? gEl.GetInt32() : 0;
                            int silverCost = el.TryGetProperty("SilverCost", out JsonElement sEl) ? sEl.GetInt32() : 0;
                            bool isPremium = el.TryGetProperty("IsPremium", out JsonElement premEl) &&
                                             premEl.GetBoolean();
                            bool isBundle = el.TryGetProperty("IsBundle", out JsonElement bEl) && bEl.GetBoolean();
                            bool isEnabled = !el.TryGetProperty("IsEnabled", out JsonElement enEl) || enEl.GetBoolean();

                            // HoN legacy .AltBundle entries are individual early access alt avatar unlocks, not multi-item bundle chests
                            if (code.EndsWith(".AltBundle", StringComparison.OrdinalIgnoreCase))
                            {
                                isBundle = false;
                            }

                            (string hero, string alt) = ParseHeroAndAlt(type, code);
                            string description = GenerateDescription(type, name, code);

                            _items.Add(new StaffStoreItemDto
                            {
                                Id = id,
                                Name = name,
                                Code = code,
                                Type = type,
                                TypeName = GetTypeName(type, isBundle, code),
                                Resource = resource,
                                Purchasable = purchasable,
                                GoldCost = goldCost,
                                SilverCost = silverCost,
                                IsPremium = isPremium,
                                IsBundle = isBundle,
                                IsEnabled = isEnabled,
                                HeroName = hero,
                                AltAvatarSuffix = alt,
                                Description = description
                            });
                        }

                        s_cachedParsedItems[_storeJsonPath] =
                        [
                            .. _items.Select(item => new StaffStoreItemDto
                            {
                                Id = item.Id,
                                Name = item.Name,
                                Code = item.Code,
                                Type = item.Type,
                                TypeName = item.TypeName,
                                Resource = item.Resource,
                                Purchasable = item.Purchasable,
                                GoldCost = item.GoldCost,
                                SilverCost = item.SilverCost,
                                IsPremium = item.IsPremium,
                                IsBundle = item.IsBundle,
                                IsEnabled = item.IsEnabled,
                                HeroName = item.HeroName,
                                AltAvatarSuffix = item.AltAvatarSuffix,
                                Description = item.Description
                            })
                        ];

                        if (_logger.IsEnabled(LogLevel.Information))
                        {
                            _logger.LogInformation("[StaffStoreService] Loaded {Count} store items from '{Path}'",
                                _items.Count, _storeJsonPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Error))
                        {
                            _logger.LogError(ex, "[StaffStoreService] Failed to load store items from '{Path}'",
                                _storeJsonPath);
                        }
                    }
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "[StaffStoreService] StoreItemsConfiguration.json was NOT found at expected path.");
                }
            }

            _loaded = true;
        }
    }

    public static HashSet<string> BuildNormalizedOwnedLookup(IEnumerable<string>? ownedCodes)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (ownedCodes == null) return set;

        foreach (string raw in ownedCodes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string code = raw.Trim();
            set.Add(code);

            // Strip common prefixes
            string clean = code;
            if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
            else if (clean.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("c.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
            else if (clean.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ||
                     clean.StartsWith("cs.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
            else if (clean.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];

            set.Add(clean);

            if (clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = clean[5..];
                set.Add(stripped);
                set.Add($"aa.{clean}");
                set.Add($"aa.{stripped}");
                set.Add($"Avatar_{stripped}");
            }
            else if (clean.StartsWith("Avatar_", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = clean[7..];
                set.Add(stripped);
                set.Add($"Hero_{stripped}");
                set.Add($"aa.Hero_{stripped}");
                set.Add($"aa.{stripped}");
            }
            else if (clean.Contains('.'))
            {
                set.Add($"Hero_{clean}");
                set.Add($"Avatar_{clean}");
                set.Add($"aa.Hero_{clean}");
                set.Add($"aa.{clean}");
            }
        }

        return set;
    }

    public static bool IsItemOwned(StaffStoreItemDto item, HashSet<string>? normalizedOwnedSet)
    {
        if (normalizedOwnedSet == null || normalizedOwnedSet.Count == 0) return false;

        if (normalizedOwnedSet.Contains(item.Code)) return true;
        if (normalizedOwnedSet.Contains(item.Id.ToString())) return true;
        if (!string.IsNullOrEmpty(item.Name) && normalizedOwnedSet.Contains(item.Name)) return true;
        if (!string.IsNullOrEmpty(item.Resource) && normalizedOwnedSet.Contains(item.Resource)) return true;

        string clean = item.Code;
        if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
        else if (clean.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("c.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        else if (clean.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("cs.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
        else if (clean.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];

        if (normalizedOwnedSet.Contains(clean)) return true;

        if (!string.IsNullOrEmpty(item.HeroName) && !string.IsNullOrEmpty(item.AltAvatarSuffix))
        {
            string hero = item.HeroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase);
            string alt = item.AltAvatarSuffix;

            if (normalizedOwnedSet.Contains($"{hero}.{alt}")) return true;
            if (normalizedOwnedSet.Contains($"Hero_{hero}.{alt}")) return true;
            if (normalizedOwnedSet.Contains($"Avatar_{hero}.{alt}")) return true;
            if (normalizedOwnedSet.Contains($"aa.Hero_{hero}.{alt}")) return true;
            if (normalizedOwnedSet.Contains($"aa.{hero}.{alt}")) return true;
        }

        return false;
    }

    public static bool IsItemEquipped(StaffStoreItemDto item, HashSet<string>? equippedSet)
    {
        if (equippedSet == null || equippedSet.Count == 0) return false;

        // 1. Direct code or prefixed codes
        if (equippedSet.Contains(item.Code)) return true;
        if (equippedSet.Contains($"aa.{item.Code}")) return true;
        if (equippedSet.Contains($"ai.{item.Code}")) return true;
        if (equippedSet.Contains($"cc.{item.Code}")) return true;
        if (equippedSet.Contains($"cs.{item.Code}")) return true;
        if (equippedSet.Contains($"t.{item.Code}")) return true;
        if (equippedSet.Contains($"av.{item.Code}")) return true;
        if (equippedSet.Contains($"c.{item.Code}")) return true;
        if (equippedSet.Contains($"w.{item.Code}")) return true;

        // 2. Alt Avatar hero combos (e.g. aa.Hero_Mimix.Alt, aa.Mimix.Alt)
        if (!string.IsNullOrEmpty(item.HeroName) && !string.IsNullOrEmpty(item.AltAvatarSuffix))
        {
            string heroClean = item.HeroName.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase);
            if (equippedSet.Contains($"aa.Hero_{heroClean}.{item.AltAvatarSuffix}") ||
                equippedSet.Contains($"aa.{heroClean}.{item.AltAvatarSuffix}") ||
                equippedSet.Contains($"Hero_{heroClean}.{item.AltAvatarSuffix}") ||
                equippedSet.Contains($"Avatar_{heroClean}.{item.AltAvatarSuffix}"))
            {
                return true;
            }
        }

        // 3. Fallback strip prefix from items in equippedSet
        string clean = item.Code;
        if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("cc.", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("cs.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
        else if (clean.StartsWith("t.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        else if (clean.StartsWith("av.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];
        else if (clean.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith("w.", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];

        return equippedSet.Contains(clean);
    }

    private static string GenerateDescription(int type, string name, string code)
    {
        if (type == 5 || type == 2 || code.StartsWith("t.", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("taunt", StringComparison.OrdinalIgnoreCase))
        {
            return "Rub your skills in the face of your opponents by laying the Smackdown on them!";
        }

        if (type == 6 || code.StartsWith("c.", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("courier", StringComparison.OrdinalIgnoreCase))
        {
            return
                "Loyal battlefield courier to swiftly transport vital equipment and relics from base to the front lines.";
        }

        if (type == 7 || type == 11 || code.StartsWith("w.", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ward", StringComparison.OrdinalIgnoreCase))
        {
            return "Vigilant guardian ward that reveals enemy movements across the fog of war in Newerth.";
        }

        if (type == 4 || code.StartsWith("av.", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("announcer", StringComparison.OrdinalIgnoreCase))
        {
            return "Custom in-game announcer pack replacing combat callouts, kill streaks, and victory celebrations.";
        }

        if (type == 1 || name.StartsWith("Flag", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("icon.", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Icon", StringComparison.OrdinalIgnoreCase))
        {
            return "Exclusive account icon and badge displayed on your player profile and match loading screen.";
        }

        if (type == 0 || code.Contains("shield", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("color", StringComparison.OrdinalIgnoreCase))
        {
            return "Vibrant player name color and shield styling applied across all in-game lobbies and chat channels.";
        }

        if (type == 10)
        {
            return "Official account service to update player handle, reset win-loss statistics, or refresh ratings.";
        }

        if (type == 8)
        {
            return "Exclusive value bundle containing a collection of heroes, alt avatars, and vanity unlocks.";
        }

        if (type == 3 || type == 12 || code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            return
                "Legendary alt avatar featuring a custom 3D character mesh, battle animations, visual spell effects, and voice lines.";
        }

        return "Exclusive cosmetic unlock for Project KONGOR.";
    }

    private bool PersistAllItemsToJson()
    {
        if (string.IsNullOrEmpty(_storeJsonPath) || !File.Exists(_storeJsonPath))
        {
            return true;
        }

        try
        {
            List<Dictionary<string, object>> rawList = [];
            foreach (StaffStoreItemDto dto in _items)
            {
                rawList.Add(new Dictionary<string, object>
                {
                    ["ID"] = dto.Id,
                    ["Name"] = dto.Name,
                    ["Code"] = dto.Code,
                    ["Type"] = dto.Type,
                    ["Resource"] = dto.Resource,
                    ["Purchasable"] = dto.Purchasable,
                    ["GoldCost"] = dto.GoldCost,
                    ["SilverCost"] = dto.SilverCost,
                    ["IsPremium"] = dto.IsPremium,
                    ["IsBundle"] = dto.IsBundle,
                    ["IsEnabled"] = dto.IsEnabled,
                    ["IsDynamic"] = false,
                    ["Required"] = Array.Empty<string>()
                });
            }

            string json = JsonSerializer.Serialize(rawList, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(_storeJsonPath, json);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[StaffStoreService] Successfully persisted {Count} store items to '{Path}'",
                    _items.Count, _storeJsonPath);
            }

            string baseDir = AppContext.BaseDirectory;
            DirectoryInfo? root = new DirectoryInfo(baseDir).Parent?.Parent?.Parent?.Parent;

            // Synchronize any bin output copies
            if (!_isCustomStorePath && root is { Exists: true })
            {
                FileInfo[] extraStoreFiles = root.GetFiles("StoreItemsConfiguration.json", SearchOption.AllDirectories);
                foreach (FileInfo file in extraStoreFiles)
                {
                    if (!file.FullName.Equals(_storeJsonPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.WriteAllText(file.FullName, json);
                        }
                        catch
                        {
                            // Ignored if locked by another active process
                        }
                    }
                }
            }

            // Also synchronize Upgrades.JSON so in-game catalog prices match
            if (!string.IsNullOrEmpty(_upgradesJsonPath) && File.Exists(_upgradesJsonPath))
            {
                try
                {
                    string upgJson = File.ReadAllText(_upgradesJsonPath);
                    using JsonDocument upgDoc = JsonDocument.Parse(upgJson);
                    Dictionary<string, StaffStoreItemDto> itemByCode = _items
                        .Where(x => !string.IsNullOrEmpty(x.Code))
                        .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

                    List<Dictionary<string, object>> updatedUpgrades = [];
                    foreach (JsonElement el in upgDoc.RootElement.EnumerateArray())
                    {
                        string code = el.TryGetProperty("Code", out JsonElement cEl) ? cEl.GetString() ?? "" : "";
                        Dictionary<string, object> upgDict = [];
                        foreach (JsonProperty prop in el.EnumerateObject())
                        {
                            upgDict[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.Number => prop.Value.GetInt32(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => prop.Value.GetString() ?? ""
                            };
                        }

                        if (itemByCode.TryGetValue(code, out StaffStoreItemDto? matchingItem))
                        {
                            upgDict["Price"] = matchingItem.GoldCost.ToString();
                            upgDict["PriceInMMP"] = matchingItem.SilverCost.ToString();
                            upgDict["Enabled"] = matchingItem.IsEnabled ? "true" : "false";
                            upgDict["Purchasable"] = matchingItem.Purchasable ? "true" : "false";
                        }

                        updatedUpgrades.Add(upgDict);
                    }

                    string serializedUpgrades = JsonSerializer.Serialize(updatedUpgrades,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_upgradesJsonPath, serializedUpgrades);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "[StaffStoreService] Successfully synchronized {Count} upgrades in '{Path}'",
                            updatedUpgrades.Count, _upgradesJsonPath);
                    }

                    if (!_isCustomStorePath && root is { Exists: true })
                    {
                        FileInfo[] extraUpgFiles = root.GetFiles("Upgrades.JSON", SearchOption.AllDirectories);
                        foreach (FileInfo file in extraUpgFiles)
                        {
                            if (!file.FullName.Equals(_upgradesJsonPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    File.WriteAllText(file.FullName, serializedUpgrades);
                                }
                                catch
                                {
                                    // Ignored
                                }
                            }
                        }
                    }
                }
                catch (Exception upgEx)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                    {
                        _logger.LogError(upgEx, "[StaffStoreService] Failed to synchronize Upgrades.JSON");
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "[StaffStoreService] Failed to persist store items to '{Path}'", _storeJsonPath);
            }

            return false;
        }
    }

    private static (string Hero, string Alt) ParseHeroAndAlt(int type, string code)
    {
        // Account icons, colors, taunts, announcers, services, wards, couriers, and bundles NEVER have 3D hero models
        if (type is 1 or 0 or 2 or 4 or 5 or 6 or 7 or 8 or 10 or 11)
        {
            return ("", "");
        }

        string clean = code;
        if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];

        if (clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && clean.Contains('.'))
        {
            string[] parts = clean[5..].Split('.');
            string hero = parts[0];
            string alt = parts.Length > 1 ? parts[1] : "";
            return (hero, alt);
        }

        return ("", "");
    }

    private static string GetTypeName(int type, bool isBundle, string code)
    {
        if (isBundle) return "Bundle";
        if (code.EndsWith(".AltBundle", StringComparison.OrdinalIgnoreCase)) return "Alt Avatar";
        return type switch
        {
            0 => "Name Color",
            1 => "Account Icon",
            2 => "Taunt / Upgrade",
            3 => "Alt Avatar",
            4 => "Announcer",
            5 => "Taunt",
            6 => "Courier",
            7 => "Ward",
            8 => "Bundle",
            10 => "Account Service",
            11 => "Ward",
            12 => "Alt Avatar",
            20 => "Alt Avatar",
            _ => "Vanity Item"
        };
    }
}
