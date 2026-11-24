// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Prebaked account cosmetic upgrade (chat symbol, account icon, custom ward, courier, announcer).
/// </summary>
[MemoryPackable]
public partial class CosmeticUpgradeDto
{
    public string Name { get; set; } = string.Empty; // e.g. "afghanistan", "Default Icon", "aluna_ward"
    public string Category { get; set; } = string.Empty; // "ChatSymbol", "AccountIcon", "Ward", "Courier", etc.
    public string Texture { get; set; } = string.Empty; // e.g. "/ui/icons/flags/afghanistan.tga"
    public string Modifier { get; set; } = string.Empty; // e.g. "W_Aluna", "C_Penguin"
    public string Color { get; set; } = string.Empty;
    public string InGameColor { get; set; } = string.Empty;
    public int SortIndex { get; set; }
    public string VoiceSet { get; set; } = string.Empty; // e.g. "flamboyant"
    public string ArcadeText { get; set; } = string.Empty; // e.g. "unicorn"
}
