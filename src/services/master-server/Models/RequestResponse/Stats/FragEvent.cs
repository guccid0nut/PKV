// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

[MemoryPackable]
public partial class FragEvent
{
    [FromForm(Name = "killer_id")] public required int SourceID { get; set; }

    [FromForm(Name = "target_id")] public required int TargetID { get; set; }

    [FromForm(Name = "secs")] public required int GameTimeSeconds { get; set; }

    [FromForm(Name = "assisters")] public required List<int>? SupporterIDs { get; set; }
}
