// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked alternate hero avatar definition and 3D preview model configurations.
/// </summary>
[MemoryPackable]
public partial class AltAvatarDto
{
    public string Key { get; set; } = string.Empty; // e.g. "Hero_Accursed.Alt"
    public string HeroName { get; set; } = string.Empty; // e.g. "Hero_Accursed"
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty; // e.g. "/alt"
    public string IconPath { get; set; } = string.Empty; // e.g. "/heroes/accursed/alt/icon.tga"
    public string DefaultModel { get; set; } = string.Empty; // e.g. "preview.mdf"
    public string ModelPath { get; set; } = string.Empty;
    public string DefaultEffect { get; set; } = string.Empty; // e.g. "effects/body.effect"
    public string EffectPath { get; set; } = string.Empty;
    public string PreviewModel { get; set; } = string.Empty; // e.g. "/heroes/accursed/alt/preview.mdf"
    public string PreviewEffect { get; set; } = string.Empty; // e.g. "/heroes/accursed/alt/effects/body.effect"
    public string PreviewPos { get; set; } = string.Empty; // e.g. "0 0 -20"
    public string PreviewAngles { get; set; } = string.Empty; // e.g. "0 0 0"
    public float PreviewScale { get; set; } = 1.0f;

    // Voice audio references
    public string SelectedSound { get; set; } = string.Empty;
    public string ConfirmMoveSound { get; set; } = string.Empty;
    public string ConfirmAttackSound { get; set; } = string.Empty;
    public string CooldownSound { get; set; } = string.Empty;
    public string NoManaSound { get; set; } = string.Empty;
    public string SelectedFlavorSound { get; set; } = string.Empty;
    public string TauntedSound { get; set; } = string.Empty;
    public string TauntKillSound { get; set; } = string.Empty;
    public bool LocalizeUnitSounds { get; set; }

    // 3D Viewport Presets from heroes_preview.xml
    public List<PreviewPresetDto> PreviewPresets { get; set; } = [];
}
