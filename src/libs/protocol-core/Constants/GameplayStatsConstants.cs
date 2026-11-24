// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

/// <summary>
///     Defines canonical names, protocol dictionary keys, and semantic aliases for the 10 generic
///     gameplay telemetry channels (gameplaystat0 - gameplaystat9) sent by dedicated game servers.
/// </summary>
public static class GameplayStatsConstants
{
    /// <summary>
    ///     Protocol key for Total Healing Done to allies. (gameplaystat0)
    /// </summary>
    public const string HealingDoneKey = "gameplaystat0";

    /// <summary>
    ///     Protocol key for Total Damage Taken / Mitigated prior to reductions. (gameplaystat1)
    /// </summary>
    public const string DamageTakenKey = "gameplaystat1";

    /// <summary>
    ///     Protocol key for Total Crowd Control / Disable Duration applied in seconds. (gameplaystat2)
    /// </summary>
    public const string CrowdControlDurationKey = "gameplaystat2";

    /// <summary>
    ///     Protocol key for Total Buff / Positive Utility status applications. (gameplaystat3)
    /// </summary>
    public const string BuffsAppliedKey = "gameplaystat3";

    /// <summary>
    ///     Protocol key for Total Pure / True Damage Dealt (bypassing armor and magic resistance). (gameplaystat4)
    /// </summary>
    public const string PureDamageDealtKey = "gameplaystat4";

    /// <summary>
    ///     Protocol key for Total Damage Dealt to Boss / Neutral Objectives (e.g., Kongor, Transmutanstein). (gameplaystat5)
    /// </summary>
    public const string BossDamageDealtKey = "gameplaystat5";

    /// <summary>
    ///     Protocol key for Total Powerups / Runes Collected. (gameplaystat6)
    /// </summary>
    public const string PowerupsCollectedKey = "gameplaystat6";

    /// <summary>
    ///     Protocol key for Total Skillshots / Non-Targeted Abilities Landed. (gameplaystat7)
    /// </summary>
    public const string SkillshotsLandedKey = "gameplaystat7";

    /// <summary>
    ///     Protocol key for Total Barrier / Shield Damage Absorbed. (gameplaystat8)
    /// </summary>
    public const string ShieldsDamageAbsorbedKey = "gameplaystat8";

    /// <summary>
    ///     Protocol key for Custom Map / Event Objective Scoring (e.g., Rift captures, MidWars bonus objectives).
    ///     (gameplaystat9)
    /// </summary>
    public const string ObjectiveScoreKey = "gameplaystat9";

    // --- Backwards-Compatible Aliases ---
    public const string CustomChannel5Key = BossDamageDealtKey;
    public const string CustomChannel6Key = PowerupsCollectedKey;
    public const string CustomChannel7Key = SkillshotsLandedKey;
    public const string CustomChannel8Key = ShieldsDamageAbsorbedKey;
    public const string CustomChannel9Key = ObjectiveScoreKey;
}
