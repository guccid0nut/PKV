// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Entities.Game;

[Index(nameof(HeroCode), IsUnique = true)]
[Index(nameof(DisplayName))]
public class HeroDetail
{
    [Key] public int ID { get; set; }

    [MaxLength(100)] public required string HeroCode { get; set; }

    [MaxLength(150)] public required string DisplayName { get; set; }

    [MaxLength(50)] public required string PrimaryAttribute { get; set; }

    [MaxLength(50)] public string AttackType { get; set; } = "Melee";

    public double BaseStrength { get; set; } = 20.0;
    public double StrengthGrowth { get; set; } = 2.0;

    public double BaseAgility { get; set; } = 20.0;
    public double AgilityGrowth { get; set; } = 2.0;

    public double BaseIntelligence { get; set; } = 20.0;
    public double IntelligenceGrowth { get; set; } = 2.0;

    public double Armor { get; set; } = 3.0;

    [MaxLength(20)] public string ArmorMitigation { get; set; } = "15.0%";

    public int MinDamage { get; set; } = 50;
    public int MaxDamage { get; set; } = 55;

    public int MovementSpeed { get; set; } = 300;

    [MaxLength(255)] public required string IconPath { get; set; }

    [MaxLength(255)] public required string SplashImagePath { get; set; }

    [MaxLength(4096)] public required string Lore { get; set; }

    [MaxLength(8192)] public required string AbilitiesJson { get; set; }
}
