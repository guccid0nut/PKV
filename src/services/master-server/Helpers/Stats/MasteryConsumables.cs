// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

// ReSharper disable LogMessageIsConditional

using System.Globalization;

namespace KONGOR.MasterServer.Helpers.Stats;

/// <summary>
///     Manages the mastery consumables that live in a user's owned store items as counted entries (for example "ma.Mastery
///     Boost * 3" or "cp.Accursed Mastery Coupon * 1"), and issues the per-hero rewards granted when a hero crosses a
///     mastery level.
/// </summary>
/// <remarks>
///     None of these methods save the tracked changes. The caller is responsible for calling
///     MerrickContext.SaveChangesAsync.
/// </remarks>
// ReSharper disable UnusedMethodReturnValue.Global
public static class MasteryConsumables
{
    public const string MasteryBoostCode = "ma.Mastery Boost";
    public const string SuperMasteryBoostCode = "ma.Super Mastery Boost";

    private const string MasteryBoostPrefix = MasteryBoostCode + " * ";
    private const string SuperMasteryBoostPrefix = SuperMasteryBoostCode + " * ";

    /// <summary>
    ///     The number of regular mastery boosts the user owns in primary counted storage.
    /// </summary>
    public static int MasteryBoostsOwned(User? user)
    {
        return user is null ? 0 : GetCount(user, MasteryBoostPrefix);
    }

    /// <summary>
    ///     The number of super mastery boosts the user owns in primary counted storage.
    /// </summary>
    public static int SuperMasteryBoostsOwned(User? user)
    {
        return user is null ? 0 : GetCount(user, SuperMasteryBoostPrefix);
    }

    /// <summary>
    ///     Returns true if the inventory item code is a per-match boost redemption marker
    ///     (<c>mb.claimed_match:</c> or legacy <c>mboost_claimed:</c>).
    /// </summary>
    public static bool IsMatchClaimMarker(string? itemCode)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return false;

        return itemCode.StartsWith("mb.claimed_match:", StringComparison.OrdinalIgnoreCase) ||
               itemCode.StartsWith("mboost_claimed:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true if the user already has a mastery boost claim marker for <paramref name="matchId" />.
    /// </summary>
    public static bool HasClaimedMatchBoost(User? user, int matchId)
    {
        if (user?.InventoryItems is null || matchId <= 0) return false;

        string primaryMarker = $"mb.claimed_match:{matchId}";
        string legacyMarker = $"mboost_claimed:{matchId}";

        return user.InventoryItems.Any(i =>
            string.Equals(i.ItemCode, primaryMarker, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.ItemCode, legacyMarker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Anchored matcher for mastery boost inventory rows. Supports primary counted storage
    ///     (<c>ma.Mastery Boost * N</c> / <c>ma.Super Mastery Boost * N</c>) and anchored legacy item codes,
    ///     strictly excluding claim markers and excluding <c>Super</c> entries when <paramref name="isSuper" /> is false.
    /// </summary>
    public static bool TryMatchBoostItem(
        string? itemCode,
        bool isSuper,
        int quantity,
        out bool isCountedPrimary,
        out int effectiveCount)
    {
        isCountedPrimary = false;
        effectiveCount = 0;

        if (string.IsNullOrWhiteSpace(itemCode) || quantity <= 0 || IsMatchClaimMarker(itemCode))
            return false;

        if (isSuper)
        {
            if (itemCode.StartsWith(SuperMasteryBoostPrefix, StringComparison.OrdinalIgnoreCase))
            {
                isCountedPrimary = true;
                effectiveCount = int.TryParse(
                    itemCode.AsSpan(SuperMasteryBoostPrefix.Length).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed)
                    ? Math.Max(0, parsed)
                    : quantity;
                return effectiveCount > 0;
            }

            const string legacySuperBundlePrefix = "m.Super boost * ";
            if (itemCode.StartsWith(legacySuperBundlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                int perPack = int.TryParse(
                    itemCode.AsSpan(legacySuperBundlePrefix.Length).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedPack) && parsedPack > 0
                    ? parsedPack
                    : 1;
                effectiveCount = perPack * quantity;
                return effectiveCount > 0;
            }

            if (itemCode.Equals(SuperMasteryBoostCode, StringComparison.OrdinalIgnoreCase) ||
                itemCode.Equals("m.Super boost", StringComparison.OrdinalIgnoreCase) ||
                itemCode.Equals("m.super_boost", StringComparison.OrdinalIgnoreCase) ||
                itemCode.Equals("mb.super_boost", StringComparison.OrdinalIgnoreCase) ||
                itemCode.Equals("super_boost", StringComparison.OrdinalIgnoreCase))
            {
                effectiveCount = quantity;
                return true;
            }

            return false;
        }

        // Strictly exclude any Super boost item when requesting regular mastery boosts.
        if (itemCode.Contains("super", StringComparison.OrdinalIgnoreCase))
            return false;

        if (itemCode.StartsWith(MasteryBoostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            isCountedPrimary = true;
            effectiveCount = int.TryParse(
                itemCode.AsSpan(MasteryBoostPrefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
                ? Math.Max(0, parsed)
                : quantity;
            return effectiveCount > 0;
        }

        const string legacyBundlePrefix = "m.Mastery Boost * ";
        if (itemCode.StartsWith(legacyBundlePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int perPack = int.TryParse(
                itemCode.AsSpan(legacyBundlePrefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedPack) && parsedPack > 0
                ? parsedPack
                : 10;
            effectiveCount = perPack * quantity;
            return effectiveCount > 0;
        }

        if (itemCode.Equals(MasteryBoostCode, StringComparison.OrdinalIgnoreCase) ||
            itemCode.Equals("m.Mastery Boost", StringComparison.OrdinalIgnoreCase) ||
            itemCode.Equals("mb.mastery_boost", StringComparison.OrdinalIgnoreCase) ||
            itemCode.Equals("m.mastery_boost", StringComparison.OrdinalIgnoreCase) ||
            itemCode.Equals("mastery_boost", StringComparison.OrdinalIgnoreCase))
        {
            effectiveCount = quantity;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Returns total owned regular or super mastery boosts across primary counted storage
    ///     and anchored legacy boost rows, excluding per-match claim markers.
    /// </summary>
    public static int GetTotalMasteryBoostsOwned(User? user, bool isSuper)
    {
        if (user?.InventoryItems is null) return 0;

        int total = 0;
        foreach (UserInventory item in user.InventoryItems)
        {
            if (TryMatchBoostItem(item.ItemCode, isSuper, item.Quantity, out _, out int count))
                total += count;
        }

        return total;
    }

    public sealed record BoostRowMutation(
        UserInventory Row,
        string OriginalItemCode,
        int OriginalQuantity,
        bool IsRemoved);

    /// <summary>
    ///     Consumes a single regular or super mastery boost from primary counted storage first,
    ///     then falls back to anchored legacy boost rows. Caller persists via tracked DbContext.
    /// </summary>
    public static bool TryConsumeOneBoost(User user, bool isSuper, out UserInventory? removedRow)
    {
        bool consumed = TryConsumeOneBoostWithTarget(user, isSuper, out List<BoostRowMutation> mutations);
        removedRow = mutations.FirstOrDefault(m => m.IsRemoved)?.Row;
        return consumed;
    }

    /// <summary>
    ///     Consumes a single regular or super mastery boost and records pre-mutation row state
    ///     so relational callers can enforce compare-and-swap concurrency checks.
    /// </summary>
    public static bool TryConsumeOneBoostWithTarget(
        User user,
        bool isSuper,
        out List<BoostRowMutation> mutations)
    {
        mutations = [];
        if (user.InventoryItems is null) return false;

        string primaryPrefix = isSuper ? SuperMasteryBoostPrefix : MasteryBoostPrefix;
        UserInventory? primaryRow = user.InventoryItems.FirstOrDefault(i =>
            i.ItemCode.StartsWith(primaryPrefix, StringComparison.OrdinalIgnoreCase));
        if (primaryRow != null &&
            TryMatchBoostItem(primaryRow.ItemCode, isSuper, primaryRow.Quantity, out bool isPrimary, out int primaryCount) &&
            isPrimary && primaryCount > 0)
        {
            string origCode = primaryRow.ItemCode;
            int origQty = primaryRow.Quantity;
            if (primaryCount > 1)
            {
                int remaining = primaryCount - 1;
                primaryRow.ItemCode = $"{primaryPrefix}{remaining}";
                primaryRow.Quantity = remaining;
                mutations.Add(new BoostRowMutation(primaryRow, origCode, origQty, IsRemoved: false));
            }
            else
            {
                user.InventoryItems.Remove(primaryRow);
                mutations.Add(new BoostRowMutation(primaryRow, origCode, origQty, IsRemoved: true));
            }

            return true;
        }

        UserInventory? legacyRow = null;
        int legacyEffectiveCount = 0;
        foreach (UserInventory candidate in user.InventoryItems)
        {
            if (TryMatchBoostItem(candidate.ItemCode, isSuper, candidate.Quantity, out bool isCounted, out int eff) &&
                !isCounted && eff > 0)
            {
                legacyRow = candidate;
                legacyEffectiveCount = eff;
                break;
            }
        }

        if (legacyRow is null) return false;

        string legacyOrigCode = legacyRow.ItemCode;
        int legacyOrigQty = legacyRow.Quantity;
        int perUnitPack = legacyRow.Quantity > 0 ? legacyEffectiveCount / legacyRow.Quantity : 1;
        if (perUnitPack > 1)
        {
            if (legacyRow.Quantity > 1)
            {
                legacyRow.Quantity -= 1;
                mutations.Add(new BoostRowMutation(legacyRow, legacyOrigCode, legacyOrigQty, IsRemoved: false));
            }
            else
            {
                user.InventoryItems.Remove(legacyRow);
                mutations.Add(new BoostRowMutation(legacyRow, legacyOrigCode, legacyOrigQty, IsRemoved: true));
            }

            string unpackPrefix = isSuper ? SuperMasteryBoostPrefix : MasteryBoostPrefix;
            AddCountedWithTarget(user, unpackPrefix, perUnitPack - 1, legacyRow.AccountID, "MasteryConsumable", out BoostRowMutation? unpackedMutation);
            if (unpackedMutation != null)
                mutations.Add(unpackedMutation);

            return true;
        }

        if (legacyRow.Quantity > 1)
        {
            legacyRow.Quantity -= 1;
            mutations.Add(new BoostRowMutation(legacyRow, legacyOrigCode, legacyOrigQty, IsRemoved: false));
        }
        else
        {
            user.InventoryItems.Remove(legacyRow);
            mutations.Add(new BoostRowMutation(legacyRow, legacyOrigCode, legacyOrigQty, IsRemoved: true));
        }

        return true;
    }

    /// <summary>
    ///     Grants a claimed mastery tier product, normalizing mastery boost rewards into counted
    ///     <see cref="MasteryConsumables" /> entries (<c>ma.Mastery Boost * N</c> / <c>ma.Super Mastery Boost * N</c>).
    /// </summary>
    public static (string NormalizedCode, int NormalizedQuantity) GrantRewardProduct(
        User user,
        int? accountId,
        string productCode,
        int productQuantity,
        DateTimeOffset timestamp,
        string source = "MasteryClaim")
    {
        (string code, int qty, _) = GrantRewardProductWithTarget(
            user, accountId, productCode, productQuantity, timestamp, source);
        return (code, qty);
    }

    /// <summary>
    ///     Grants a claimed mastery tier product and outputs pre-mutation row state when an existing
    ///     counted/stackable inventory row was incremented so relational callers can run CAS updates.
    /// </summary>
    public static (string NormalizedCode, int NormalizedQuantity, BoostRowMutation? UpdatedExistingRow) GrantRewardProductWithTarget(
        User user,
        int? accountId,
        string productCode,
        int productQuantity,
        DateTimeOffset timestamp,
        string source = "MasteryClaim")
    {
        int qty = productQuantity > 0 ? productQuantity : 1;
        user.InventoryItems ??= [];

        if (productCode.Equals(MasteryBoostCode, StringComparison.OrdinalIgnoreCase) ||
            productCode.Equals("m.Mastery Boost", StringComparison.OrdinalIgnoreCase) ||
            productCode.Equals("mb.mastery_boost", StringComparison.OrdinalIgnoreCase))
        {
            AddCountedWithTarget(user, MasteryBoostPrefix, qty, accountId, source, out BoostRowMutation? mutation);
            return (MasteryBoostCode, qty, mutation);
        }

        if (productCode.StartsWith(MasteryBoostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            int perPack = int.TryParse(
                productCode.AsSpan(MasteryBoostPrefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedPack) && parsedPack > 0
                ? parsedPack
                : 1;
            int totalBoosts = perPack * qty;
            AddCountedWithTarget(user, MasteryBoostPrefix, totalBoosts, accountId, source, out BoostRowMutation? mutation);
            return (MasteryBoostCode, totalBoosts, mutation);
        }

        const string legacyBundlePrefix = "m.Mastery Boost * ";
        if (productCode.StartsWith(legacyBundlePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int perPack = int.TryParse(
                productCode.AsSpan(legacyBundlePrefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedPack) && parsedPack > 0
                ? parsedPack
                : 10;
            int totalBoosts = perPack * qty;
            AddCountedWithTarget(user, MasteryBoostPrefix, totalBoosts, accountId, source, out BoostRowMutation? mutation);
            return (MasteryBoostCode, totalBoosts, mutation);
        }

        if (productCode.Equals(SuperMasteryBoostCode, StringComparison.OrdinalIgnoreCase) ||
            productCode.Equals("m.Super boost", StringComparison.OrdinalIgnoreCase) ||
            productCode.Equals("m.super_boost", StringComparison.OrdinalIgnoreCase) ||
            productCode.Equals("mb.super_boost", StringComparison.OrdinalIgnoreCase))
        {
            AddCountedWithTarget(user, SuperMasteryBoostPrefix, qty, accountId, source, out BoostRowMutation? mutation);
            return (SuperMasteryBoostCode, qty, mutation);
        }

        if (productCode.StartsWith(SuperMasteryBoostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            int perPack = int.TryParse(
                productCode.AsSpan(SuperMasteryBoostPrefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedPack) && parsedPack > 0
                ? parsedPack
                : 1;
            int totalBoosts = perPack * qty;
            AddCountedWithTarget(user, SuperMasteryBoostPrefix, totalBoosts, accountId, source, out BoostRowMutation? mutation);
            return (SuperMasteryBoostCode, totalBoosts, mutation);
        }

        const string legacySuperBundlePrefix = "m.Super boost * ";
        if (productCode.StartsWith(legacySuperBundlePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int perPack = int.TryParse(
                productCode.AsSpan(legacySuperBundlePrefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedPack) && parsedPack > 0
                ? parsedPack
                : 1;
            int totalBoosts = perPack * qty;
            AddCountedWithTarget(user, SuperMasteryBoostPrefix, totalBoosts, accountId, source, out BoostRowMutation? mutation);
            return (SuperMasteryBoostCode, totalBoosts, mutation);
        }

        UserInventory? existing = user.InventoryItems.FirstOrDefault(i =>
            string.Equals(i.ItemCode, productCode, StringComparison.OrdinalIgnoreCase));
        BoostRowMutation? existingMutation = null;
        if (existing != null)
        {
            string origCode = existing.ItemCode;
            int origQty = existing.Quantity;
            existing.Quantity += qty;
            existingMutation = new BoostRowMutation(existing, origCode, origQty, IsRemoved: false);
        }
        else
        {
            user.InventoryItems.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = accountId,
                ItemCode = productCode,
                Quantity = qty,
                IsPermanent = true,
                TimestampAcquired = timestamp,
                Source = source
            });
        }

        return (productCode, qty, existingMutation);
    }

    public static bool AddMasteryBoost(User user, int amount = 1)
    {
        return AddCounted(user, MasteryBoostPrefix, amount);
    }

    public static bool AddMasteryBoost(User user, int amount, int? accountId, string source = "MasteryConsumable")
    {
        return AddCounted(user, MasteryBoostPrefix, amount, accountId, source);
    }

    public static bool RemoveMasteryBoost(User user, int amount = 1)
    {
        return RemoveCounted(user, MasteryBoostPrefix, amount);
    }

    public static bool AddSuperMasteryBoost(User user, int amount = 1)
    {
        return AddCounted(user, SuperMasteryBoostPrefix, amount);
    }

    public static bool AddSuperMasteryBoost(User user, int amount, int? accountId, string source = "MasteryConsumable")
    {
        return AddCounted(user, SuperMasteryBoostPrefix, amount, accountId, source);
    }

    public static bool RemoveSuperMasteryBoost(User user, int amount = 1)
    {
        return RemoveCounted(user, SuperMasteryBoostPrefix, amount);
    }

    /// <summary>
    ///     Adds a mastery coupon for the hero with the given identifier (an empty identifier represents the all-avatar
    ///     coupon).
    /// </summary>
    public static bool AddMasteryCoupon(User user, string heroIdentifier, int amount = 1)
    {
        MasteryCoupon? coupon =
            JSONConfiguration.MasteryCoupons.SingleOrDefault(coupon => coupon.Hero.Equals(heroIdentifier));

        if (coupon is null) return false;

        bool added = AddCounted(user, $"{coupon.Code} * ", amount);

        ResolveUnusableCoupons(user);

        return added;
    }

    /// <summary>
    ///     Removes a mastery coupon for the hero with the given identifier (an empty identifier represents the all-avatar
    ///     coupon).
    /// </summary>
    public static bool RemoveMasteryCoupon(User user, string heroIdentifier, int amount = 1)
    {
        MasteryCoupon? coupon =
            JSONConfiguration.MasteryCoupons.SingleOrDefault(coupon => coupon.Hero.Equals(heroIdentifier));

        return coupon is not null && RemoveCounted(user, $"{coupon.Code} * ", amount);
    }

    /// <summary>
    ///     Issues the reward granted when a hero reaches the given mastery level.
    ///     Level 1 grants the hero's mastery account icon, levels 2 and 11 grant the hero's mastery coupon, levels 4, 8 and 12
    ///     grant silver coins (1000, 2000 and 2000 respectively), and levels 6, 9 and 14 grant plinko tickets (100, 200 and
    ///     200 respectively).
    ///     Levels 3, 5, 7, 10, 13 and 15 correspond to cosmetic rewards (silver border, silver badge, silver teleport, gold
    ///     upgrade, emote and epic upgrade) which are handled automatically by the client, and so are intentionally not issued
    ///     here.
    /// </summary>
    /// <returns>
    ///     <see langword="true" /> if a reward was issued, otherwise <see langword="false" />.
    /// </returns>
    public static bool IssueHeroMasteryLevelReward(User user, int level, string heroIdentifier, ILogger logger)
    {
        switch (level)
        {
            case 1:
            {
                string? heroName = Heroes.GetNameByIdentifier(heroIdentifier);

                if (heroName is null)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                        logger.LogError(
                            @"[BUG] Unable To Find Hero Name For Hero With Identifier ""{HeroIdentifier}""",
                            heroIdentifier);

                    return false;
                }

                // Map The In-Game Hero Name To The Mastery Icon Product Name For Heroes Whose Mastery Icon Product Uses A Different Name
                heroName = heroName switch
                {
                    "Blood Hunter" => "Bloodhunter",
                    "Circe" => "Circe The Deceiver",
                    "Qi" => "Chi",
                    "Xemplar" => "Mimix",
                    _ => heroName
                };

                StoreItem? icon = JSONConfiguration.StoreItemsConfiguration
                    .GetEnabledItemsByType(StoreItemType.AccountIcon)
                    .SingleOrDefault(item =>
                        item.Name.StartsWith("Mastery Icon", StringComparison.OrdinalIgnoreCase) &&
                        item.Name.EndsWith(heroName, StringComparison.OrdinalIgnoreCase));

                if (icon is null)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                        logger.LogError(
                            @"[BUG] Unable To Find Mastery Account Icon For Hero With Identifier ""{HeroIdentifier}""",
                            heroIdentifier);

                    return false;
                }

                if (!user.InventoryItems.Any(i => i.ItemCode == icon.PrefixedCode))
                {
                    user.InventoryItems.Add(new UserInventory
                    {
                        UserID = user.ID,
                        ItemCode = icon.PrefixedCode,
                        StoreItemId = null,
                        Quantity = 1,
                        IsPermanent = true,
                        TimestampAcquired = TimeProvider.System.GetUtcNow(),
                        Source = "MasteryReward"
                    });
                }

                return true;
            }

            case 2:
            case 11:
                return AddMasteryCoupon(user, heroIdentifier);

            case 4:
                user.SilverCoins += 1000;
                LogMasteryCurrencyReward(user, 0, 1000, 0, level, heroIdentifier);
                return true;
            case 6:
                user.PlinkoTickets += 100;
                LogMasteryCurrencyReward(user, 0, 0, 100, level, heroIdentifier);
                return true;
            case 8:
                user.SilverCoins += 2000;
                LogMasteryCurrencyReward(user, 0, 2000, 0, level, heroIdentifier);
                return true;
            case 9:
                user.PlinkoTickets += 200;
                LogMasteryCurrencyReward(user, 0, 0, 200, level, heroIdentifier);
                return true;
            case 12:
                user.SilverCoins += 2000;
                LogMasteryCurrencyReward(user, 0, 2000, 0, level, heroIdentifier);
                return true;
            case 14:
                user.PlinkoTickets += 200;
                LogMasteryCurrencyReward(user, 0, 0, 200, level, heroIdentifier);
                return true;

            default: return false;
        }
    }

    private static void LogMasteryCurrencyReward(User user, int goldDelta, int silverDelta, int plinkoDelta, int level,
        string heroIdentifier)
    {
        int? mainAccountId = user.Accounts.FirstOrDefault(a => a.IsMain)?.ID ?? user.Accounts.FirstOrDefault()?.ID;
        user.EconomyTransactions.Add(new EconomyTransaction
        {
            UserID = user.ID,
            AccountID = mainAccountId,
            GoldCoinsDelta = goldDelta,
            SilverCoinsDelta = silverDelta,
            PlinkoTicketsDelta = plinkoDelta,
            GoldCoinsBalanceAfter = user.GoldCoins,
            SilverCoinsBalanceAfter = user.SilverCoins,
            PlinkoTicketsBalanceAfter = user.PlinkoTickets,
            TransactionType = "MasteryLevelReward",
            ReferenceId = $"{heroIdentifier}:lvl{level}",
            Description = $"Hero {heroIdentifier} reached Mastery Level {level} reward",
            Timestamp = TimeProvider.System.GetUtcNow()
        });
    }

    /// <summary>
    ///     Replaces any owned hero-specific mastery coupon with the all-avatar mastery coupon when the user already owns every
    ///     alternative avatar the hero-specific coupon could be applied to.
    /// </summary>
    public static void ResolveUnusableCoupons(User user)
    {
        MasteryCoupon? allAvatarCoupon =
            JSONConfiguration.MasteryCoupons.SingleOrDefault(coupon => coupon.Hero.Equals(string.Empty));

        if (allAvatarCoupon is null) return;

        foreach (MasteryCoupon coupon in JSONConfiguration.MasteryCoupons.Where(coupon =>
                     coupon.Hero.Equals(string.Empty).Equals(false)))
        {
            string ownedCouponPrefix = $"{coupon.Code} * ";

            string? ownedCoupon =
                user.InventoryItems.FirstOrDefault(i =>
                    i.ItemCode.StartsWith(ownedCouponPrefix, StringComparison.Ordinal))?.ItemCode;

            if (ownedCoupon is null) continue;

            List<string> applicableAvatars =
            [
                .. JSONConfiguration.StoreItemsConfiguration
                    .GetEnabledItemsByType(StoreItemType.AlternativeAvatar)
                    .Where(item => item.Code.StartsWith(coupon.Hero, StringComparison.Ordinal))
                    .Select(item => item.PrefixedCode)
            ];

            bool unusable = applicableAvatars.All(av => user.InventoryItems.Any(i => i.ItemCode == av));

            if (unusable.Equals(false)) continue;

            int amount = int.TryParse(ownedCoupon[ownedCouponPrefix.Length..], out int parsed) ? parsed : 1;

            RemoveCounted(user, ownedCouponPrefix, amount);
            AddCounted(user, $"{allAvatarCoupon.Code} * ", amount);
        }
    }

    private static int GetCount(User user, string prefix)
    {
        UserInventory? item = user.InventoryItems?.FirstOrDefault(i => i.ItemCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (item is null) return 0;

        return int.TryParse(item.ItemCode.AsSpan(prefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : item.Quantity;
    }

    private static bool AddCounted(User user, string prefix, int amount, int? accountId = null, string source = "MasteryConsumable")
    {
        return AddCountedWithTarget(user, prefix, amount, accountId, source, out _);
    }

    private static bool AddCountedWithTarget(
        User user,
        string prefix,
        int amount,
        int? accountId,
        string source,
        out BoostRowMutation? existingRowMutation)
    {
        existingRowMutation = null;
        if (amount < 1) return false;
        user.InventoryItems ??= [];

        UserInventory? existing =
            user.InventoryItems.FirstOrDefault(i => i.ItemCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            int? resolvedAccountId = accountId ?? user.Accounts.FirstOrDefault(a => a.IsMain)?.ID ?? user.Accounts.FirstOrDefault()?.ID;
            user.InventoryItems.Add(new UserInventory
            {
                UserID = user.ID,
                AccountID = resolvedAccountId,
                ItemCode = $"{prefix}{amount}",
                Quantity = amount,
                IsPermanent = true,
                TimestampAcquired = TimeProvider.System.GetUtcNow(),
                Source = source
            });

            return true;
        }

        string origCode = existing.ItemCode;
        int origQty = existing.Quantity;
        int owned = int.TryParse(existing.ItemCode.AsSpan(prefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : existing.Quantity;
        int newTotal = owned + amount;
        existing.ItemCode = $"{prefix}{newTotal}";
        existing.Quantity = newTotal;
        existingRowMutation = new BoostRowMutation(existing, origCode, origQty, IsRemoved: false);

        return true;
    }

    private static bool RemoveCounted(User user, string prefix, int amount)
    {
        if (amount < 1 || user.InventoryItems is null) return false;

        UserInventory? existing =
            user.InventoryItems.FirstOrDefault(i => i.ItemCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (existing is null) return false;

        int owned = int.TryParse(existing.ItemCode[prefix.Length..], out int parsed) ? parsed : existing.Quantity;

        if (owned - amount > 0)
        {
            int newTotal = owned - amount;
            existing.ItemCode = $"{prefix}{newTotal}";
            existing.Quantity = newTotal;
        }
        else
        {
            user.InventoryItems.Remove(existing);
        }

        return true;
    }
}
