// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.RegularExpressions;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Models;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public partial class HomeController
{
    private static List<HeroItemDto>? _cachedHeroItems;

    private async Task<List<HeroItemDto>> GetCachedHeroItemsAsync(CancellationToken cancellationToken = default)
    {
        if (_hybridCache != null)
        {
            List<HeroItemDto>? cached = await _hybridCache.GetOrCreateAsync(
                "dawn:items:all:v2",
                async ct => await LoadHeroItemsFromDbOrDiskAsync(ct),
                tags: ["items", "compendium"],
                l1Expiration: TimeSpan.FromMinutes(15),
                l2Expiration: TimeSpan.FromHours(1),
                cancellationToken: cancellationToken
            );

            if (cached != null)
            {
                EnsureItemsEnrichedWithCraftingGraph(cached);
                return cached;
            }
        }

        return await LoadHeroItemsFromDbOrDiskAsync(cancellationToken);
    }

    private static string? FindHeroItemsJsonPath()
    {
        string[] candidateSubpaths =
        [
            Path.Combine("src", "libs", "database-context", "Data", "HeroItems.json"),
            Path.Combine("Data", "HeroItems.json"),
            "HeroItems.json"
        ];

        string[] roots = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            DirectoryInfo? dir = new(root);
            while (dir != null)
            {
                foreach (string subpath in candidateSubpaths)
                {
                    string probe = Path.Combine(dir.FullName, subpath);
                    if (System.IO.File.Exists(probe)) return probe;
                }
                dir = dir.Parent;
            }
        }

        return null;
    }

    private static List<HeroItemDto> LoadDiskHeroItems()
    {
        string? path = FindHeroItemsJsonPath();
        if (path != null)
        {
            try
            {
                string json = System.IO.File.ReadAllText(path);
                List<HeroItem>? diskItems = JsonSerializer.Deserialize<List<HeroItem>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (diskItems != null && diskItems.Count > 0)
                {
                    return diskItems.Select(i => i.ToDto()).ToList();
                }
            }
            catch
            {
            }
        }
        return [];
    }

    private async Task<List<HeroItemDto>> LoadHeroItemsFromDbOrDiskAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedHeroItems != null && _cachedHeroItems.Count >= 174)
        {
            EnsureItemsEnrichedWithCraftingGraph(_cachedHeroItems);
            return _cachedHeroItems;
        }

        List<HeroItemDto> res = [];
        try
        {
            IReadOnlyList<HeroItemDto> list = await _playerStatsClient.GetAllHeroItemsAsync(cancellationToken);
            res = [.. list];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HomeController] Error fetching hero items from client API");
        }

        // Overlay missing items OR missing ComponentCodesJson/RecipeCost from HeroItems.json
        bool needsDiskFallback = res.Count < 174 ||
                                 res.All(i => string.IsNullOrWhiteSpace(i.ComponentCodesJson) || i.ComponentCodesJson == "[]");

        if (needsDiskFallback)
        {
            string? foundPath = FindHeroItemsJsonPath();
            if (foundPath != null)
            {
                try
                {
                    string json = await System.IO.File.ReadAllTextAsync(foundPath, cancellationToken);
                    List<HeroItem>? diskItems = JsonSerializer.Deserialize<List<HeroItem>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (diskItems != null && diskItems.Count > 0)
                    {
                        Dictionary<string, HeroItem> diskByCode = new(StringComparer.OrdinalIgnoreCase);
                        foreach (HeroItem di in diskItems)
                        {
                            if (!string.IsNullOrWhiteSpace(di.Code))
                            {
                                diskByCode[di.Code.Trim()] = di;
                            }
                        }

                        int hydratedRecipes = 0;
                        foreach (HeroItemDto existing in res)
                        {
                            if ((string.IsNullOrWhiteSpace(existing.ComponentCodesJson) || existing.ComponentCodesJson == "[]") &&
                                !string.IsNullOrWhiteSpace(existing.Code) &&
                                diskByCode.TryGetValue(existing.Code.Trim(), out HeroItem? diskMatch))
                            {
                                string diskCompJson = diskMatch.ComponentCodesJson;
                                if (!string.IsNullOrWhiteSpace(diskCompJson) && diskCompJson != "[]")
                                {
                                    existing.ComponentCodesJson = diskCompJson;
                                    hydratedRecipes++;
                                }

                                if (existing.RecipeCost <= 0 && diskMatch.RecipeCost > 0)
                                {
                                    existing.RecipeCost = diskMatch.RecipeCost;
                                }
                            }
                        }

                        if (res.Count < 174)
                        {
                            HashSet<string> seenCodes = new(res.Select(i => i.Code), StringComparer.OrdinalIgnoreCase);
                            foreach (HeroItem item in diskItems)
                            {
                                if (!seenCodes.Contains(item.Code))
                                {
                                    seenCodes.Add(item.Code);
                                    res.Add(item.ToDto());
                                }
                            }
                        }

                        if (hydratedRecipes > 0)
                        {
                            _logger.LogInformation(
                                "[HomeController.Items] Self-healed {HydratedCount} item recipe definitions from '{Path}'.",
                                hydratedRecipes, foundPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HomeController] Error loading fallback hero items from {Path}", foundPath);
                }
            }
        }

        if (res.Count > 0)
        {
            EnsureItemsEnrichedWithCraftingGraph(res);
            lock (_heroDataLock)
            {
                _cachedHeroItems = res;
            }
        }

        return res;
    }

    private static void EnsureItemsEnrichedWithCraftingGraph(List<HeroItemDto> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Enrich whenever any recipe item still has unexpanded ComponentCodesJson, missing BuildsInto graph,
        // or raw JsonElement entries deserialized from L2 HybridCache
        bool hasUnexpandedRecipes = items.Any(i =>
            !string.IsNullOrWhiteSpace(i.ComponentCodesJson) &&
            i.ComponentCodesJson != "[]" &&
            (i.Components == null || i.Components.Count == 0));
        bool hasRawCacheElements = items.Any(i =>
            (i.Components is { Count: > 0 } && i.Components[0] is not ItemComponentView) ||
            (i.BuildsInto is { Count: > 0 } && i.BuildsInto[0] is not ItemBuildsIntoView));
        bool hasBuildsIntoGraph = items.Any(i => i.BuildsInto is { Count: > 0 });
        if (!hasUnexpandedRecipes && !hasRawCacheElements && hasBuildsIntoGraph)
        {
            return;
        }

        Dictionary<string, HeroItemDto> lookup = BuildItemLookup(items);
        foreach (HeroItemDto item in items)
        {
            List<ItemComponentView> comps = ResolveComponents(item, lookup);
            List<ItemBuildsIntoView> builds = ResolveBuildsInto(item, items);
            item.Components = [.. comps.Cast<object>()];
            item.BuildsInto = [.. builds.Cast<object>()];
        }
    }

    [HttpGet("items")]
    [HttpGet("compendium")]
    [HttpGet("Home/Items")]
    public IActionResult Items()
    {
        return View();
    }

    private static HeroItemDto? FindMatchingItem(IEnumerable<HeroItemDto> items, string code, string cleanInput)
    {
        List<HeroItemDto> materialized = items as List<HeroItemDto> ?? [.. items];
        string rawClean = code.Replace("Item_", "", StringComparison.OrdinalIgnoreCase).Trim();

        // Pass 1: Direct code or exact name match takes strict precedence over legacy aliases
        HeroItemDto? exactMatch = materialized.FirstOrDefault(i =>
            string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Code, rawClean, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Name, code, StringComparison.OrdinalIgnoreCase) ||
            string.Equals((i.Code ?? string.Empty).Replace("Item_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "").Trim(), cleanInput, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
        {
            return exactMatch;
        }

        // Pass 2: Legacy engine alias, slug, or normalized display-name fallback
        string? aliasTarget = null;
        if (LegacyItemAliases.TryGetValue(rawClean, out string? directAlias) ||
            LegacyItemAliases.TryGetValue(cleanInput, out directAlias))
        {
            aliasTarget = directAlias;
        }

        string engineName = HonItemMapping.ToEngineEntityName(code);
        string webSlug = HonItemMapping.ToWebSlug(code);
        return materialized.FirstOrDefault(i =>
            (!string.IsNullOrEmpty(aliasTarget) && string.Equals(i.Code, aliasTarget, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(engineName) && string.Equals(i.Code, engineName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(webSlug) && string.Equals(HonItemMapping.ToWebSlug(i.Code), webSlug, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals((i.Name ?? string.Empty).Replace(" ", "").Replace("'", "").Trim(), cleanInput.Replace("'", ""), StringComparison.OrdinalIgnoreCase));
    }

    [HttpGet("items/{code}")]
    [HttpGet("item/{code}")]
    [HttpGet("Home/ItemDetail/{code}")]
    public async Task<IActionResult> ItemDetail(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return RedirectToAction("Items");

        List<HeroItemDto> allItems = await GetCachedHeroItemsAsync(cancellationToken);
        string cleanInput = code.Replace("Item_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "").Trim();
        HeroItemDto? item = FindMatchingItem(allItems, code, cleanInput);

        if (item == null)
        {
            List<HeroItemDto> diskItems = LoadDiskHeroItems();
            item = FindMatchingItem(diskItems, code, cleanInput);
            if (item != null)
            {
                lock (_heroDataLock)
                {
                    if (_cachedHeroItems != null && !_cachedHeroItems.Any(i => string.Equals(i.Code, item.Code, StringComparison.OrdinalIgnoreCase)))
                    {
                        _cachedHeroItems.Add(item);
                    }
                }
            }
        }

        if (item == null) return RedirectToAction("Items");

        if (code.StartsWith("Item_", StringComparison.OrdinalIgnoreCase) &&
            !item.ItemIdentifier.StartsWith("Item_", StringComparison.OrdinalIgnoreCase))
        {
            item = new HeroItemDto
            {
                ID = item.ID,
                ItemIdentifier = code,
                DisplayName = item.DisplayName,
                Description = item.Description,
                Cost = item.Cost,
                Category = item.Category,
                RecipeCost = item.RecipeCost,
                IconPath = item.IconPath,
                Flavor = item.Flavor,
                Upgrade = item.Upgrade,
                IsActive = item.IsActive,
                ComponentCodesJson = item.ComponentCodesJson
            };
        }

        Dictionary<string, HeroItemDto> itemLookup = BuildItemLookup(allItems);
        List<ItemComponentView> resolvedComponents = ResolveComponents(item, itemLookup);
        List<ItemBuildsIntoView> buildsIntoList = ResolveBuildsInto(item, allItems);
        List<ItemLevelTierView> levelTiers = ResolveLevelTiers(item);
        List<ItemCraftingTreeNode> backwardTree = BuildBackwardCraftingTree(item, itemLookup);
        List<ItemCraftingTreeNode> forwardTree = BuildForwardCraftingTree(item, allItems, itemLookup);

        ViewBag.Components = resolvedComponents;
        ViewBag.BuildsInto = buildsIntoList;
        ViewBag.LevelTiers = levelTiers;
        ViewBag.BackwardTree = backwardTree;
        ViewBag.ForwardTree = forwardTree;

        _logger.LogDebug(
            "[HomeController.ItemDetail] Rendered crafting graph for '{Code}': {CompCount} direct components, {BuildCount} direct upgrades, backwardRootCount={BackRoots}, forwardRootCount={FwdRoots}.",
            item.Code, resolvedComponents.Count, buildsIntoList.Count, backwardTree.Count, forwardTree.Count);

        ViewData["Item"] = item;
        return View(item);
    }

    [HttpGet("api/items")]
    [HttpGet("api/v1/items")]
    public async Task<IActionResult> GetItems(CancellationToken cancellationToken = default)
    {
        List<HeroItemDto> items = await GetCachedHeroItemsAsync(cancellationToken);
        EnsureItemsEnrichedWithCraftingGraph(items);
        return Json(items);
    }

    public static IReadOnlyDictionary<string, string> LegacyItemAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "morph", "kuldrassheepstick" },
            { "sheepstick", "kuldrassheepstick" },
            { "pierce", "shieldbreaker" },
            { "manaburn1", "nullfireblade" },
            { "manaburn2", "geometersbane" },
            { "spiked_bola", "spikedbola" },
            { "immunity", "shrunkenhead" },
            { "silence", "hellflower" },
            { "protect", "nullstone" },
            { "pushstaff", "tabletofcommand" },
            { "sandscepter", "sandscepter" },
            { "intelligence5", "amuletofexile" },
            { "intelligence6", "stormspirit" },
            { "intelligence7", "staffofthemaster" },
            { "critical1", "riftshards" },
            { "critical2", "riftshards" },
            { "freeze", "frostwolfsskull" },
            { "steamboots", "steamboots" },
            { "posthaste", "posthaste" },
            { "state_posthaste", "posthaste" },
            { "stateposthaste", "posthaste" },
            { "damage9", "doombringer" },
            { "damage10", "mockofbrilliance" },
            { "lifesteal4", "symbolofrage" },
            { "lifesteal5", "abyssalskull" },
            { "nuke", "codex" },
            { "evasion", "wingbow" },
            { "weapon1", "arcana" },
            { "greatarcana", "arcana" },
            { "weapon3", "savagemace" },
            { "gloves3", "alchemistsbones" },
            { "glovesofswiftness", "glovesoftheswift" },
            { "healthmana2", "iconofthegoddess" },
            { "healthmana3", "behemothsheart" },
            { "magicarmor2", "shamansheaddress" },
            { "manaring", "ringofsorcery" },
            { "manaregen3", "ringoftheteacher" },
            { "replenish", "ringofsorcery" },
            { "arcaneboots", "sorceryboots" },
            { "shield2", "helmoftheblacklegion" },
            { "splash", "runedcleaver" },
            { "stealth", "assassinsshroud" },
            { "summon", "puzzlebox" },
            { "excruciator", "barbedarmor" },
            { "runesoftheblight", "blightstones" },
            { "somazssynchronizer", "orbofzamos" },
            { "madfreds_brassknuckles", "madfredsbrassknuckles" },
            { "sasuke", "genjuro" },
            { "voulge", "slayer" },
            { "enhancedmarchers", "ghostmarchers" },
            { "strengthagility", "frostburn" },
            { "lightning1", "thunderclaw" },
            { "lightning2", "chargedhammer" },
            { "groundfamiliar", "groundcourier" },
            { "bottle", "bottle33" },
            { "flamingeye", "wardofsight" },
            { "gadget_flamingeye", "wardofsight" },
            { "gadgetflamingeye", "wardofsight" },
            { "tokenofsight", "wardofsight" },
            { "manaeye", "wardofrevelation" },
            { "gadget_manaeye", "wardofrevelation" },
            { "gadgetmanaeye", "wardofrevelation" },
            { "mysticpotpourri", "refreshingornament" },
            { "regen", "sustainer" },
            { "bolsteringarmband", "bolsteringarmband" },
            { "bolsteringheadband", "bolsteringarmband" },
            { "blessedarmband", "bolsteringarmband" },
            { "elementuserstome", "tomeofelements" },
            { "tomboftheelements", "tomeofelements" },
            { "loggerahatchet", "loggershatchet" },
            { "strength5", "mightyblade" },
            { "strength6", "beastheart" },
            { "agility5", "quickblade" },
            { "agility6", "alacrityband" },
            { "apprenticerobes", "apprenticesrobe" },
            { "sicarius", "firebrand" },
            { "soulscream", "soulscreamring" },
            { "ultorsheavyplate", "ultorsheavyhelm" },
            { "ullorsheavyplate", "ultorsheavyhelm" },
            { "revive", "tokenoflife" },
            { "cheese", "kongorsbananas" }
        };

    private static Dictionary<string, HeroItemDto> BuildItemLookup(IEnumerable<HeroItemDto> allItems)
    {
        Dictionary<string, HeroItemDto> lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (HeroItemDto it in allItems)
        {
            string rawCode = (it.Code ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(rawCode))
            {
                lookup[rawCode] = it;
                string cleanCode = rawCode.Replace("item_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "");
                lookup[cleanCode] = it;
                lookup[$"item_{cleanCode}"] = it;
            }

            string rawIdent = (it.ItemIdentifier ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(rawIdent))
            {
                lookup[rawIdent] = it;
                string cleanIdent = rawIdent.Replace("item_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "");
                lookup[cleanIdent] = it;
                lookup[$"item_{cleanIdent}"] = it;
            }

            string rawName = (it.Name ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(rawName))
            {
                string normName = Regex.Replace(rawName, @"[^a-z0-9]", "");
                lookup[normName] = it;
                lookup[$"item_{normName}"] = it;
            }
        }

        foreach (KeyValuePair<string, string> kvp in LegacyItemAliases)
        {
            if (lookup.TryGetValue(kvp.Value, out HeroItemDto? target))
            {
                lookup.TryAdd(kvp.Key, target);
                lookup.TryAdd($"item_{kvp.Key}", target);
            }
        }

        return lookup;
    }

    private static List<ItemComponentView> ResolveComponents(HeroItemDto item, Dictionary<string, HeroItemDto> itemLookup)
    {
        List<ItemComponentView> resolvedComponents = [];
        if (string.IsNullOrWhiteSpace(item.ComponentCodesJson)) return resolvedComponents;

        try
        {
            List<string>? compCodes = JsonSerializer.Deserialize<List<string>>(item.ComponentCodesJson);
            if (compCodes != null)
            {
                foreach (string rawCode in compCodes)
                {
                    string cCode = rawCode.Trim().ToLowerInvariant();
                    if (cCode == "recipe" || cCode.Contains("recipe"))
                    {
                        resolvedComponents.Add(new ItemComponentView
                        {
                            Name = "Recipe Scroll",
                            Cost = item.RecipeCost > 0 ? item.RecipeCost.ToString() : "0",
                            Icon = "/images/items/recipe.png",
                            Code = "recipe",
                            IsRecipe = true
                        });
                    }
                    else if (itemLookup.TryGetValue(cCode, out HeroItemDto? compItem) ||
                             (LegacyItemAliases.TryGetValue(cCode, out string? alias) && itemLookup.TryGetValue(alias, out compItem)))
                    {
                        resolvedComponents.Add(new ItemComponentView
                        {
                            Name = compItem.DisplayName,
                            Cost = compItem.Cost.ToString(),
                            Icon = string.IsNullOrWhiteSpace(compItem.IconPath) ? $"/images/items/{compItem.Code}.png" : compItem.IconPath,
                            Code = compItem.Code,
                            IsRecipe = false
                        });
                    }
                    else
                    {
                        resolvedComponents.Add(new ItemComponentView
                        {
                            Name = rawCode,
                            Cost = "0",
                            Icon = $"/images/items/{cCode}.png",
                            Code = cCode,
                            IsRecipe = false
                        });
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return resolvedComponents;
    }

    private static List<ItemBuildsIntoView> ResolveBuildsInto(HeroItemDto targetItem, IEnumerable<HeroItemDto> allItems)
    {
        List<ItemBuildsIntoView> buildsIntoList = [];
        string targetCode = (targetItem.Code ?? string.Empty).Trim().ToLowerInvariant();
        string targetIdent = (targetItem.ItemIdentifier ?? string.Empty).Trim().ToLowerInvariant();
        string cleanTarget = targetCode.Replace("item_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "");
        string targetName = Regex.Replace((targetItem.Name ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", "");

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (HeroItemDto other in allItems)
        {
            if (string.IsNullOrWhiteSpace(other.ComponentCodesJson)) continue;
            try
            {
                List<string>? codes = JsonSerializer.Deserialize<List<string>>(other.ComponentCodesJson);
                if (codes == null) continue;

                bool matches = codes.Any(c =>
                {
                    string norm = c.Trim().ToLowerInvariant();
                    string cleanNorm = norm.Replace("item_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "");
                    return string.Equals(norm, targetCode, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(norm, targetIdent, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(cleanNorm, cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(cleanNorm, targetName, StringComparison.OrdinalIgnoreCase) ||
                           (LegacyItemAliases.TryGetValue(norm, out string? alias) &&
                            (string.Equals(alias, cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(alias, targetCode, StringComparison.OrdinalIgnoreCase)));
                });

                if (matches && !string.Equals(other.Code, targetItem.Code, StringComparison.OrdinalIgnoreCase))
                {
                    string otherCode = other.Code ?? other.ItemIdentifier;
                    if (seen.Add(otherCode))
                    {
                        buildsIntoList.Add(new ItemBuildsIntoView
                        {
                            Name = other.DisplayName,
                            Cost = other.Cost.ToString(),
                            Icon = string.IsNullOrWhiteSpace(other.IconPath) ? $"/images/items/{other.Code}.png" : other.IconPath,
                            Code = otherCode
                        });
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        return buildsIntoList;
    }

    public static List<ItemCraftingTreeNode> BuildBackwardCraftingTree(
        HeroItemDto targetItem,
        Dictionary<string, HeroItemDto> itemLookup,
        HashSet<string>? visited = null,
        int depth = 0)
    {
        if (depth > 5 || string.IsNullOrWhiteSpace(targetItem.ComponentCodesJson))
        {
            return [];
        }

        HashSet<string> nextVisited = visited != null
            ? new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(targetItem.Code))
        {
            nextVisited.Add(targetItem.Code);
        }

        List<ItemCraftingTreeNode> nodes = [];
        try
        {
            List<string>? rawCodes = JsonSerializer.Deserialize<List<string>>(targetItem.ComponentCodesJson);
            if (rawCodes == null || rawCodes.Count == 0)
            {
                return nodes;
            }

            bool hasExplicitRecipe = false;
            Dictionary<string, int> quantityByCanonicalCode = new(StringComparer.OrdinalIgnoreCase);
            List<string> orderedUniqueCodes = [];

            foreach (string raw in rawCodes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string cCode = raw.Trim().ToLowerInvariant();
                if (cCode == "recipe" || cCode.Contains("recipe", StringComparison.OrdinalIgnoreCase))
                {
                    hasExplicitRecipe = true;
                    continue;
                }

                string canonicalKey = cCode;
                if (itemLookup.TryGetValue(cCode, out HeroItemDto? matched) ||
                    (LegacyItemAliases.TryGetValue(cCode, out string? alias) && itemLookup.TryGetValue(alias, out matched)))
                {
                    canonicalKey = matched.Code;
                }

                if (!quantityByCanonicalCode.TryGetValue(canonicalKey, out int currentQty))
                {
                    orderedUniqueCodes.Add(canonicalKey);
                    quantityByCanonicalCode[canonicalKey] = 1;
                }
                else
                {
                    quantityByCanonicalCode[canonicalKey] = currentQty + 1;
                }
            }

            foreach (string canonicalKey in orderedUniqueCodes)
            {
                int qty = quantityByCanonicalCode[canonicalKey];
                if (itemLookup.TryGetValue(canonicalKey, out HeroItemDto? compDto) ||
                    (LegacyItemAliases.TryGetValue(canonicalKey, out string? alias) && itemLookup.TryGetValue(alias, out compDto)))
                {
                    List<ItemCraftingTreeNode> subChildren = !nextVisited.Contains(compDto.Code)
                        ? BuildBackwardCraftingTree(compDto, itemLookup, nextVisited, depth + 1)
                        : [];

                    nodes.Add(new ItemCraftingTreeNode
                    {
                        Code = compDto.Code,
                        Name = compDto.DisplayName,
                        Cost = compDto.Cost,
                        RecipeCost = compDto.RecipeCost,
                        Icon = string.IsNullOrWhiteSpace(compDto.IconPath) ? $"/images/items/{compDto.Code}.png" : compDto.IconPath,
                        Category = compDto.Category ?? string.Empty,
                        IsRecipe = false,
                        Quantity = qty,
                        Children = subChildren
                    });
                }
                else
                {
                    nodes.Add(new ItemCraftingTreeNode
                    {
                        Code = canonicalKey,
                        Name = canonicalKey,
                        Cost = 0,
                        Icon = $"/images/items/{canonicalKey.ToLowerInvariant()}.png",
                        IsRecipe = false,
                        Quantity = qty,
                        Children = []
                    });
                }
            }

            if (hasExplicitRecipe || (nodes.Count > 0 && targetItem.RecipeCost > 0))
            {
                nodes.Add(new ItemCraftingTreeNode
                {
                    Code = "recipe",
                    Name = "Recipe Scroll",
                    Cost = targetItem.RecipeCost,
                    RecipeCost = targetItem.RecipeCost,
                    Icon = "/images/items/recipe.png",
                    Category = "Recipe",
                    IsRecipe = true,
                    Quantity = 1,
                    Children = []
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("[HomeController] Failed parsing component codes JSON for item {0}: {1}", targetItem.Code, ex.Message);
        }

        return nodes;
    }

    public static List<ItemCraftingTreeNode> BuildForwardCraftingTree(
        HeroItemDto targetItem,
        IReadOnlyList<HeroItemDto> allItems,
        Dictionary<string, HeroItemDto> itemLookup,
        HashSet<string>? visited = null,
        int depth = 0)
    {
        if (depth > 4)
        {
            return [];
        }

        HashSet<string> nextVisited = visited != null
            ? new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(targetItem.Code))
        {
            nextVisited.Add(targetItem.Code);
        }

        List<ItemBuildsIntoView> directUpgrades = ResolveBuildsInto(targetItem, allItems);
        List<ItemCraftingTreeNode> nodes = [];

        foreach (ItemBuildsIntoView upg in directUpgrades)
        {
            if (string.IsNullOrWhiteSpace(upg.Code) || nextVisited.Contains(upg.Code))
            {
                continue;
            }

            if (itemLookup.TryGetValue(upg.Code, out HeroItemDto? upgDto))
            {
                List<ItemCraftingTreeNode> furtherUpgrades = BuildForwardCraftingTree(
                    upgDto, allItems, itemLookup, nextVisited, depth + 1);

                nodes.Add(new ItemCraftingTreeNode
                {
                    Code = upgDto.Code,
                    Name = upgDto.DisplayName,
                    Cost = upgDto.Cost,
                    RecipeCost = upgDto.RecipeCost,
                    Icon = string.IsNullOrWhiteSpace(upgDto.IconPath) ? $"/images/items/{upgDto.Code}.png" : upgDto.IconPath,
                    Category = upgDto.Category ?? string.Empty,
                    IsRecipe = false,
                    Quantity = 1,
                    Children = furtherUpgrades
                });
            }
            else
            {
                int.TryParse(upg.Cost, out int parsedCost);
                nodes.Add(new ItemCraftingTreeNode
                {
                    Code = upg.Code,
                    Name = upg.Name,
                    Cost = parsedCost,
                    Icon = upg.Icon,
                    IsRecipe = false,
                    Quantity = 1,
                    Children = []
                });
            }
        }

        return nodes;
    }

    private static List<ItemLevelTierView> ResolveLevelTiers(HeroItemDto targetItem)
    {
        List<ItemLevelTierView> levelTiers = [];
        string upgText = $"{targetItem.Upgrade} {targetItem.Description}";
        Match lvlMatch = Regex.Match(upgText, @"level\s*(\d+)|upgradable\s*to\s*level\s*(\d+)", RegexOptions.IgnoreCase);
        int maxLvl = 0;
        if (lvlMatch.Success)
        {
            string val = !string.IsNullOrEmpty(lvlMatch.Groups[1].Value) ? lvlMatch.Groups[1].Value : lvlMatch.Groups[2].Value;
            int.TryParse(val, out maxLvl);
        }

        string cleanCode = (targetItem.Code ?? string.Empty).Trim().ToLowerInvariant().Replace("item_", "").Replace("_", "");
        if (maxLvl <= 1)
        {
            if (cleanCode is "shieldbreaker" or "pierce" or "spellshards" or "puzzlebox" or "nullfireblade" or "manaburn1")
            {
                maxLvl = 3;
            }
            else if (cleanCode == "riftshards")
            {
                maxLvl = 4;
            }
        }

        if (maxLvl > 1)
        {
            int baseCost = targetItem.Cost;
            int scrollUpgradeCost = targetItem.RecipeCost > 0 ? targetItem.RecipeCost : 1000;
            Match scrollMatch = Regex.Match(upgText, @"recipe\s*scroll.*?(\d{1,3}(?:,\d{3})*|\d+)\s*gold", RegexOptions.IgnoreCase);
            if (scrollMatch.Success)
            {
                string rawScroll = scrollMatch.Groups[1].Value.Replace(",", "");
                if (int.TryParse(rawScroll, out int parsedScroll))
                {
                    scrollUpgradeCost = parsedScroll;
                }
            }

            for (int l = 1; l <= maxLvl; l++)
            {
                int prevCost = baseCost + (l - 2) * scrollUpgradeCost;
                int totalCost = baseCost + (l - 1) * scrollUpgradeCost;
                int upgradeCost = (l == 1) ? targetItem.RecipeCost : scrollUpgradeCost;
                levelTiers.Add(new ItemLevelTierView
                {
                    Level = l,
                    PrevTotalCost = (l == 1) ? 0 : prevCost,
                    TotalCost = totalCost,
                    RecipeScrollCost = upgradeCost,
                    IsBaseLevel = (l == 1)
                });
            }
        }

        return levelTiers;
    }

    public class ItemCraftingTreeNode
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Cost { get; set; }
        public int RecipeCost { get; set; }
        public string Icon { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool IsRecipe { get; set; }
        public int Quantity { get; set; } = 1;
        public List<ItemCraftingTreeNode> Children { get; set; } = [];
    }

    public class ItemComponentView
    {
        public string Name { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string name => Name;
        public string Cost { get; set; } = "0";
        [System.Text.Json.Serialization.JsonIgnore]
        public string cost => Cost;
        public string Icon { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string icon => Icon;
        public string Code { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string code => Code;
        public bool IsRecipe { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool isRecipe => IsRecipe;
    }

    public class ItemBuildsIntoView
    {
        public string Name { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string name => Name;
        public string Cost { get; set; } = "0";
        [System.Text.Json.Serialization.JsonIgnore]
        public string cost => Cost;
        public string Icon { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string icon => Icon;
        public string Code { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string code => Code;
    }

    public class ItemLevelTierView
    {
        public int Level { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int level => Level;
        public int PrevTotalCost { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int prevTotalCost => PrevTotalCost;
        public int TotalCost { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int totalCost => TotalCost;
        public int RecipeScrollCost { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int recipeScrollCost => RecipeScrollCost;
        public bool IsBaseLevel { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool isBaseLevel => IsBaseLevel;
    }
}
