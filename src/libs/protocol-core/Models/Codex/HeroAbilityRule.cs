// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Models.Codex;

public sealed class HeroAbilityRule
{
    public int Slot { get; set; } = 1; // 1=Q, 2=W, 3=E, 4=R, 5=Stats
    public string Key { get; set; } = "Q";
    public int MaxLevel { get; set; } = 4;
    public List<int> RequiredLevels { get; set; } = [1, 3, 5, 7];
    public int MinHeroLevel => RequiredLevels.Count > 0 ? RequiredLevels[0] : 1;
    public bool IsInnateOrEarlyUlt { get; set; }
}
