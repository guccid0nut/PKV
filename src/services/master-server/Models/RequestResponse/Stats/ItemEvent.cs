// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

[MemoryPackable]
public partial class ItemEvent
{
    [FromForm(Name = "account_id")] public required int AccountID { get; set; }

    [FromForm(Name = "cli_name")] public required string ItemName { get; set; }

    [FromForm(Name = "secs")] public required int GameTimeSeconds { get; set; }

    [FromForm(Name = "action")] public required byte EventType { get; set; }
}
