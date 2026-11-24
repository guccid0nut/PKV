// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Serialization.PHP;

namespace ASPIRE.Common.Services.Mastery;

/// <summary>
///     Status result for mastery reward claim evaluation.
/// </summary>
public enum MasteryClaimStatus
{
    Success,
    AlreadyClaimed,
    LevelNotFound,
    InvalidLevel,
    InsufficientLevel
}

/// <summary>
///     Result of evaluating a mastery tier reward claim.
/// </summary>
public record MasteryClaimResult(
    MasteryClaimStatus Status,
    int Level,
    string? ClaimKey,
    string? ProductCode,
    int ProductQuantity,
    int GoldCoins,
    int SilverCoins,
    int PlinkoTickets,
    string? ErrorMessage = null
);

/// <summary>
///     Pure domain calculator for mastery progression experience, rewards, and claim evaluation.
/// </summary>
public static class MasteryRewardCalculator
{
    /// <summary>
    ///     Evaluates whether a player is eligible to claim a mastery reward for the specified level.
    /// </summary>
    public static MasteryClaimResult EvaluateClaim(
        MasteryRewardsConfiguration? config,
        int requestedLevel,
        IReadOnlyCollection<string>? ownedStoreItems)
    {
        return EvaluateClaim(config, requestedLevel, ownedStoreItems, earnedTotalLevel: null);
    }

    /// <summary>
    ///     Evaluates whether a player is eligible to claim a mastery reward for the specified level,
    ///     optionally enforcing the player's earned total mastery level gate.
    /// </summary>
    public static MasteryClaimResult EvaluateClaim(
        MasteryRewardsConfiguration? config,
        int requestedLevel,
        IReadOnlyCollection<string>? ownedStoreItems,
        int? earnedTotalLevel)
    {
        if (config?.MasteryRewards == null || config.MasteryRewards.Count == 0)
            return new MasteryClaimResult(
                MasteryClaimStatus.LevelNotFound,
                requestedLevel,
                null,
                null,
                0,
                0,
                0,
                0,
                "No rewards configured");

        MasteryReward? reward = config.MasteryRewards.FirstOrDefault(r =>
            r.RequiredLevel == requestedLevel || (requestedLevel is >= 0 and <= 1 && r.RequiredLevel <= 1));

        if (reward == null)
            return new MasteryClaimResult(
                MasteryClaimStatus.LevelNotFound,
                requestedLevel,
                null,
                null,
                0,
                0,
                0,
                0,
                $"No reward found for level {requestedLevel}");

        string claimKey = $"mastery_reward_lv_{reward.RequiredLevel}";
        string? productCode = reward.ProductCode;
        int normalizedQuantity = reward.ProductQuantity > 0 ? reward.ProductQuantity : 1;

        if (earnedTotalLevel.HasValue && earnedTotalLevel.Value < reward.RequiredLevel)
            return new MasteryClaimResult(
                MasteryClaimStatus.InsufficientLevel,
                reward.RequiredLevel,
                claimKey,
                productCode,
                normalizedQuantity,
                reward.GoldCoins,
                reward.SilverCoins,
                reward.PlinkoTickets,
                $"Account mastery level {earnedTotalLevel.Value} is below required level {reward.RequiredLevel}");

        if (ownedStoreItems != null)
            if (ownedStoreItems.Contains(claimKey) ||
                (!string.IsNullOrEmpty(productCode) &&
                 !IsRepeatableConsumableProduct(productCode) &&
                 ownedStoreItems.Contains(productCode)))
                return new MasteryClaimResult(
                    MasteryClaimStatus.AlreadyClaimed,
                    reward.RequiredLevel,
                    claimKey,
                    productCode,
                    normalizedQuantity,
                    reward.GoldCoins,
                    reward.SilverCoins,
                    reward.PlinkoTickets,
                    "Reward already claimed");

        return new MasteryClaimResult(
            MasteryClaimStatus.Success,
            reward.RequiredLevel,
            claimKey,
            productCode,
            normalizedQuantity,
            reward.GoldCoins,
            reward.SilverCoins,
            reward.PlinkoTickets);
    }

    private static bool IsRepeatableConsumableProduct(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
            return false;

        return productCode.StartsWith("m.Mastery Boost", StringComparison.OrdinalIgnoreCase) ||
               productCode.StartsWith("m.Super boost", StringComparison.OrdinalIgnoreCase) ||
               productCode.StartsWith("m.super_boost", StringComparison.OrdinalIgnoreCase) ||
               productCode.StartsWith("mb.mastery_boost", StringComparison.OrdinalIgnoreCase) ||
               productCode.StartsWith("mb.super_boost", StringComparison.OrdinalIgnoreCase) ||
               productCode.StartsWith("ma.Mastery Boost", StringComparison.OrdinalIgnoreCase) ||
               productCode.StartsWith("ma.Super Mastery Boost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Applies the evaluated reward claim to the user's owned items and currency balances.
    /// </summary>
    public static bool ApplyClaim(
        MasteryClaimResult claim,
        IList<string> ownedItems,
        ref int goldCoins,
        ref int silverCoins,
        ref int plinkoTickets)
    {
        if (claim.Status != MasteryClaimStatus.Success) return false;

        if (!string.IsNullOrEmpty(claim.ClaimKey)) ownedItems.Add(claim.ClaimKey);

        if (!string.IsNullOrEmpty(claim.ProductCode))
        {
            int qty = claim.ProductQuantity > 0 ? claim.ProductQuantity : 1;
            for (int i = 0; i < qty; i++) ownedItems.Add(claim.ProductCode);
        }

        goldCoins += claim.GoldCoins;
        silverCoins += claim.SilverCoins;
        plinkoTickets += claim.PlinkoTickets;

        return true;
    }

    /// <summary>
    ///     Pure calculation of experience earned in a match for hero mastery progression.
    /// </summary>
    public static int CalculateMatchMasteryExperience(
        int heroLevel,
        bool isWin,
        bool isMvp,
        int durationSeconds = 1800,
        int totalHeroes = 139,
        int heroBonusExperience = 0)
    {
        int heroLevelClamped = Math.Clamp(heroLevel, 1, 25);
        if (totalHeroes <= 0) totalHeroes = 139;

        int baseExp = heroBonusExperience;
        if (baseExp <= 0) baseExp = heroLevelClamped * 20;

        if (isWin) baseExp += 50;

        if (isMvp) baseExp += 50;

        if (durationSeconds is > 0 and < 900)
        {
            double factor = Math.Clamp(durationSeconds / 900.0, 0.25, 1.0);
            baseExp = (int)Math.Round(baseExp * factor);
        }

        return Math.Max(10, baseExp);
    }

    /// <summary>
    ///     Generates the list of HeroMasteryInfo for all heroes.
    /// </summary>
    public static List<HeroMasteryInfo> GenerateMasteryInfo(
        IReadOnlyList<FavHeroDTO> heroes,
        IReadOnlyList<string>? allHeroNames,
        Func<uint, string> getHeroIdentifier)
    {
        Dictionary<string, int> heroXpMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (FavHeroDTO h in heroes)
        {
            string identifier = getHeroIdentifier(h.HeroId);
            if (!string.IsNullOrEmpty(identifier)) heroXpMap[identifier] = (int)(h.SecondsPlayed * 5);
        }

        IReadOnlyList<string> heroNames = allHeroNames ?? Heroes.AllHeroIdentifiers();
        List<HeroMasteryInfo> list = new(heroNames.Count);

        foreach (string heroName in heroNames)
        {
            int exp = heroXpMap.TryGetValue(heroName, out int xp) ? xp : 0;
            list.Add(new HeroMasteryInfo { HeroName = heroName, Experience = exp });
        }

        return list;
    }

    /// <summary>
    ///     Generates the CSV string representation of hero mastery progression.
    /// </summary>
    public static string GenerateMasteryInfoString(
        IReadOnlyDictionary<string, int>? heroExperiences,
        IReadOnlyList<string>? allHeroNames,
        Func<uint, string> getHeroIdentifier,
        IReadOnlyList<FavHeroDTO>? topHeroes = null)
    {
        Dictionary<string, int> heroXpMap = new(StringComparer.OrdinalIgnoreCase);
        if (topHeroes != null)
            foreach (FavHeroDTO h in topHeroes)
            {
                string identifier = getHeroIdentifier(h.HeroId);
                if (!string.IsNullOrEmpty(identifier)) heroXpMap[identifier] = (int)(h.SecondsPlayed * 5);
            }

        IReadOnlyList<string> heroNames = allHeroNames ?? Heroes.AllHeroIdentifiers();
        List<string> parts = new(heroNames.Count * 2);

        foreach (string heroName in heroNames)
        {
            int exp = 0;
            if (heroExperiences != null && heroExperiences.TryGetValue(heroName, out int savedExp)) exp = savedExp;

            if (exp == 0 && heroXpMap.TryGetValue(heroName, out int favXp)) exp = favXp;

            parts.Add(heroName);
            parts.Add(exp.ToString());
        }

        return string.Join(",", parts);
    }

    /// <summary>
    ///     Generates the list of MasteryRewardTier models from catalog tiers and claimed status.
    /// </summary>
    public static List<MasteryRewardTier> GenerateMasteryRewards(
        IReadOnlyList<MasteryRewardTierDTO> configTiers,
        IReadOnlyCollection<string>? ownedStoreItems = null)
    {
        List<MasteryRewardTier> rewardsList = [];

        foreach (MasteryRewardTierDTO reward in configTiers)
        {
            bool alreadyClaimed = false;
            if (ownedStoreItems != null)
            {
                string claimKey = $"mastery_reward_lv_{reward.RequiredLevel}";
                alreadyClaimed = ownedStoreItems.Contains(claimKey) ||
                                 (!string.IsNullOrEmpty(reward.ProductCode) &&
                                  !IsRepeatableConsumableProduct(reward.ProductCode) &&
                                  ownedStoreItems.Contains(reward.ProductCode));
            }

            rewardsList.Add(new MasteryRewardTier
            {
                Level = reward.RequiredLevel,
                AlreadyClaimed = alreadyClaimed,
                Reward = new MasteryRewardModel
                {
                    ProductID = reward.ProductIdentifier,
                    ProductName = reward.ProductName,
                    ProductLocalContent = reward.ProductLocalResource,
                    Quantity = reward.ProductQuantity,
                    GoldCoins = reward.GoldCoins,
                    SilverCoins = reward.SilverCoins,
                    GameTokens = reward.PlinkoTickets
                }
            });
        }

        return rewardsList;
    }

    /// <summary>
    ///     Generates the CSV string representation of mastery reward tiers.
    /// </summary>
    public static string GenerateMasteryRewardsString(
        IReadOnlyList<MasteryRewardTierDTO> configTiers,
        IReadOnlyCollection<string>? ownedStoreItems = null)
    {
        List<string> parts = [];

        foreach (MasteryRewardTierDTO reward in configTiers)
        {
            bool alreadyClaimed = false;
            if (ownedStoreItems != null)
            {
                string claimKey = $"mastery_reward_lv_{reward.RequiredLevel}";
                alreadyClaimed = ownedStoreItems.Contains(claimKey) ||
                                 (!string.IsNullOrEmpty(reward.ProductCode) &&
                                  !IsRepeatableConsumableProduct(reward.ProductCode) &&
                                  ownedStoreItems.Contains(reward.ProductCode));
            }

            parts.Add(reward.RequiredLevel.ToString());
            parts.Add(alreadyClaimed ? "true" : "false");
            parts.Add(reward.ProductIdentifier > 0 ? reward.ProductIdentifier.ToString() : "0");
            parts.Add(!string.IsNullOrWhiteSpace(reward.ProductName) ? reward.ProductName : "0");
            parts.Add(!string.IsNullOrWhiteSpace(reward.ProductLocalResource) ? reward.ProductLocalResource : "0");
            parts.Add(reward.ProductQuantity > 0 ? reward.ProductQuantity.ToString() : "0");
            parts.Add(reward.GoldCoins > 0 ? reward.GoldCoins.ToString() : "0");
            parts.Add(reward.SilverCoins > 0 ? reward.SilverCoins.ToString() : "0");
            parts.Add(reward.PlinkoTickets > 0 ? reward.PlinkoTickets.ToString() : "0");
        }

        return string.Join(",", parts);
    }
}
