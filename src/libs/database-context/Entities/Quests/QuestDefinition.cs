// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MERRICK.DatabaseContext.Entities.Quests;

[Table("QuestDefinitions", Schema = "data")]
public class QuestDefinition
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    [MaxLength(64)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Category { get; set; } = "general";

    [MaxLength(64)]
    public string NativeTriggerName { get; set; } = "achievement_value_games_won";

    [MaxLength(32)]
    public string QuestTypeDef { get; set; } = string.Empty;

    public int QuestTypeKey { get; set; }

    [MaxLength(64)]
    public string? HeroIdentifier { get; set; }

    [MaxLength(32)]
    public string? HeroRole { get; set; }

    [MaxLength(32)]
    public string? HeroAttribute { get; set; }

    [MaxLength(16)]
    public string? HeroAttackType { get; set; }

    public int HeroTeamId { get; set; }

    public int GoalValue { get; set; } = 1;

    public int BagPointsReward { get; set; } = 100;

    [MaxLength(64)]
    public string? CustomMetricKey { get; set; }

    [MaxLength(256)]
    public string? IconPath { get; set; }

    public bool IsNativeEngineTrigger { get; set; } = true;

    public int Weight { get; set; } = 10;

    public bool IsEnabled { get; set; } = true;
}
