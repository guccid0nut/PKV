// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Entities.Statistics;

/// <summary>
///     Per-account hero mastery progression.
///     The accumulated experience for each played hero is held in <see cref="HeroExperiences" /> (a JSON column). Heroes
///     with no experience are simply absent.
///     The mastery level for a hero is derived from its experience via <see cref="GetLevelFromExperience" />.
/// </summary>
[Index(nameof(AccountID), IsUnique = true)]
public class Mastery
{
    /// <summary>
    ///     The current maximum mastery level for a single hero.
    /// </summary>
    public const int MaximumMasteryLevel = 15;

    [Key] public int ID { get; set; }

    public int AccountID { get; set; }

    [ForeignKey(nameof(AccountID))] public required Account Account { get; set; }

    /// <summary>
    ///     The accumulated mastery experience for each hero the account has progressed, keyed by hero identifier (for example
    ///     "Hero_Accursed").
    ///     Persisted in the 1NF relational child table <c>stat.HeroMasteryExperiences</c>.
    /// </summary>
    [InverseProperty(nameof(HeroMasteryExperience.Mastery))]
    public List<HeroMasteryExperience> HeroExperiences { get; set; } = [];

    /// <summary>
    ///     The mastery experience accumulated for the hero with the given identifier (for example "Hero_Accursed"), or zero if
    ///     the hero has no accumulated experience.
    /// </summary>
    private static string NormalizeHeroIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return string.Empty;

        string clean = identifier.Trim();
        if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase))
            clean = clean[3..];

        int colonIdx = clean.IndexOf(':');
        if (colonIdx > 0)
            clean = clean[..colonIdx];

        int dotIdx = clean.IndexOf('.');
        if (dotIdx > 0)
            clean = clean[..dotIdx];

        return clean;
    }

    /// <summary>
    ///     The mastery experience accumulated for the hero with the given identifier (for example "Hero_Accursed"), or zero if
    ///     the hero has no accumulated experience.
    /// </summary>
    public int GetHeroExperienceByHeroIdentifier(string identifier)
    {
        string normalized = NormalizeHeroIdentifier(identifier);
        if (string.IsNullOrEmpty(normalized))
            return 0;

        return HeroExperiences
            .Where(entry => string.Equals(NormalizeHeroIdentifier(entry.HeroIdentifier), normalized, StringComparison.OrdinalIgnoreCase))
            .Sum(entry => entry.Experience);
    }

    /// <summary>
    ///     The mastery level reached for the hero with the given identifier (for example "Hero_Accursed").
    /// </summary>
    public int GetHeroLevelByHeroIdentifier(string identifier)
    {
        return GetLevelFromExperience(GetHeroExperienceByHeroIdentifier(identifier));
    }

    /// <summary>
    ///     Sets the mastery experience accumulated for the hero with the given identifier (for example "Hero_Accursed"),
    ///     creating the entry if the hero has not been progressed before.
    /// </summary>
    public void SetHeroExperienceByHeroIdentifier(string identifier, int experience)
    {
        string normalized = NormalizeHeroIdentifier(identifier);
        if (string.IsNullOrEmpty(normalized))
            normalized = identifier;

        List<HeroMasteryExperience> matching = HeroExperiences
            .Where(entry => string.Equals(NormalizeHeroIdentifier(entry.HeroIdentifier), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        HeroMasteryExperience? canonical = matching.FirstOrDefault(entry =>
            string.Equals(entry.HeroIdentifier, normalized, StringComparison.OrdinalIgnoreCase))
            ?? matching.FirstOrDefault();

        if (canonical is null)
        {
            HeroExperiences.Add(new HeroMasteryExperience
            {
                MasteryID = ID,
                Mastery = this,
                HeroIdentifier = normalized,
                Experience = experience
            });
        }
        else
        {
            canonical.HeroIdentifier = normalized;
            canonical.Experience = experience;
            foreach (HeroMasteryExperience duplicate in matching.Where(e => !ReferenceEquals(e, canonical)))
            {
                HeroExperiences.Remove(duplicate);
            }
        }
    }

    /// <summary>
    ///     The sum of the mastery levels across all heroes.
    /// </summary>
    public int TotalMasteryLevel()
    {
        return HeroExperiences.Sum(entry => GetLevelFromExperience(entry.Experience));
    }

    /// <summary>
    ///     The sum of the mastery experience across all heroes.
    /// </summary>
    public int TotalMasteryExperience()
    {
        return HeroExperiences.Sum(entry => entry.Experience);
    }

    /// <summary>
    ///     The number of heroes that have reached the maximum mastery level.
    /// </summary>
    public int HeroesAtMaximumMasteryCount()
    {
        return HeroExperiences.Count(entry => GetLevelFromExperience(entry.Experience) == MaximumMasteryLevel);
    }

    /// <summary>
    ///     The percentage of all heroes that have reached the maximum mastery level.
    /// </summary>
    public float HeroesAtMaximumMasteryPercentage(int totalHeroCount)
    {
        return totalHeroCount <= 0
            ? 0f
            : Convert.ToSingle(HeroesAtMaximumMasteryCount()) / Convert.ToSingle(totalHeroCount) * 100f;
    }

    /// <summary>
    ///     The base mastery experience awarded for a single match of the given type, scaled by the hero level reached during
    ///     the match.
    /// </summary>
    public int CalculateMatchExperience(AccountStatisticsType type, int heroLevel)
    {
        const int factor = 20;

        return type switch
        {
            AccountStatisticsType.Matchmaking => heroLevel * factor,
            AccountStatisticsType.MatchmakingCasual => heroLevel * (factor / 2),
            AccountStatisticsType.MidWars => heroLevel * (factor / 2),
            _ => 0
        };
    }

    /// <summary>
    ///     The bonus mastery experience awarded for a single match, scaled by the percentage of all heroes already at the
    ///     maximum mastery level.
    /// </summary>
    public int CalculateBonusExperience(AccountStatisticsType type, int totalHeroCount)
    {
        int experience = HeroesAtMaximumMasteryPercentage(totalHeroCount) switch
        {
            float.NaN => 000,
            < 05 => 000,
            < 10 => 010,
            < 20 => 020,
            < 30 => 030,
            < 40 => 040,
            < 50 => 050,
            < 60 => 060,
            < 70 => 070,
            < 80 => 080,
            < 90 => 090,
            _ => 100
        };

        return type switch
        {
            AccountStatisticsType.Matchmaking => experience,
            AccountStatisticsType.MatchmakingCasual => experience / 2,
            AccountStatisticsType.MidWars => experience / 2,
            _ => 0
        };
    }

    /// <summary>
    ///     The combined base and bonus mastery experience awarded for a single match.
    /// </summary>
    public int CalculateMatchAndBonusExperience(AccountStatisticsType type, int heroLevel, int totalHeroCount)
    {
        return CalculateMatchExperience(type, heroLevel) + CalculateBonusExperience(type, totalHeroCount);
    }

    /// <summary>
    ///     The mastery experience awarded by a regular mastery boost, which is double the combined base and bonus match
    ///     experience.
    /// </summary>
    public int CalculateRegularMasteryBoostExperience(AccountStatisticsType type, int heroLevel, int totalHeroCount)
    {
        return CalculateMatchAndBonusExperience(type, heroLevel, totalHeroCount) * 2;
    }

    /// <summary>
    ///     The mastery level for the given mastery experience.
    /// </summary>
    public static int GetLevelFromExperience(int experience)
    {
        return experience switch
        {
            < 01400 => 00,
            < 03000 => 01,
            < 05000 => 02,
            < 07000 => 03,
            < 09100 => 04,
            < 11300 => 05,
            < 13600 => 06,
            < 16000 => 07,
            < 18500 => 08,
            < 21000 => 09,
            < 23600 => 10,
            < 26400 => 11,
            < 29400 => 12,
            < 32600 => 13,
            < 36100 => 14,
            _ => 15
        };
    }

    /// <summary>
    ///     The experience threshold at the start of the mastery level for the given mastery experience.
    /// </summary>
    public static int GetLowerLevelBoundaryFromExperience(int experience)
    {
        return experience switch
        {
            < 01400 => 00000,
            < 03000 => 01400,
            < 05000 => 03000,
            < 07000 => 05000,
            < 09100 => 07000,
            < 11300 => 09100,
            < 13600 => 11300,
            < 16000 => 13600,
            < 18500 => 16000,
            < 21000 => 18500,
            < 23600 => 21000,
            < 26400 => 23600,
            < 29400 => 26400,
            < 32600 => 29400,
            < 36100 => 32600,
            _ => 36100
        };
    }

    /// <summary>
    ///     The experience threshold at the start of the next mastery level for the given mastery experience.
    /// </summary>
    public static int GetUpperLevelBoundaryFromExperience(int experience)
    {
        return experience switch
        {
            < 01400 => 01400,
            < 03000 => 03000,
            < 05000 => 05000,
            < 07000 => 07000,
            < 09100 => 09100,
            < 11300 => 11300,
            < 13600 => 13600,
            < 16000 => 16000,
            < 18500 => 18500,
            < 21000 => 21000,
            < 23600 => 23600,
            < 26400 => 26400,
            < 29400 => 29400,
            < 32600 => 32600,
            _ => 36100
        };
    }
}
