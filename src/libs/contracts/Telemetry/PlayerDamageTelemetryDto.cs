// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Detailed combat damage, healing, shielding, and economy breakdown per player.
/// </summary>
public sealed class PlayerDamageTelemetryDto
{
    public required int AccountId { get; set; }
    public required string AccountName { get; set; }
    public required string HeroIdentifier { get; set; }
    public required string HeroDisplayName { get; set; }
    public required int Team { get; set; }
    public required int HeroLevel { get; set; }
    public required int HeroKills { get; set; }
    public required int HeroDeaths { get; set; }
    public required int HeroAssists { get; set; }
    public required int TotalGold { get; set; }
    public required int TotalExperience { get; set; }
    public required int Gpm { get; set; }
    public required int Xpm { get; set; }
    public required int HeroDamage { get; set; }
    public required int BuildingDamage { get; set; }
    public required int CreepDamage { get; set; }
    public required int NeutralDamage { get; set; }
    public required double PureDamage { get; set; }
    public required double DamageTaken { get; set; }
    public required double HealingDone { get; set; }
    public required double CrowdControlDuration { get; set; }
    public required double ShieldsDamageAbsorbed { get; set; }
    public required int WardsPlaced { get; set; }
    public required int Denies { get; set; }
    public required int Actions { get; set; }
    public required double DamageSharePercent { get; set; }
}
