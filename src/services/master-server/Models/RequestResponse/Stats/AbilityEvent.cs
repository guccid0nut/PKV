// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

[MemoryPackable]
public partial class AbilityEvent
{
    [FromForm(Name = "hero_cli_name")] public required string HeroName { get; set; }

    [FromForm(Name = "ability_cli_name")] public required string AbilityName { get; set; }

    [FromForm(Name = "secs")] public required int GameTimeSeconds { get; set; }

    [FromForm(Name = "slot")] public required byte SlotIndex { get; set; }
}
