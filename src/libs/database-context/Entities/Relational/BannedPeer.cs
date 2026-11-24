// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Entities.Relational;
 
public class BannedPeer
{
    public required int ID { get; set; }

    [MaxLength(15)] public required string Name { get; set; }

    [MaxLength(30)] public required string BanReason { get; set; }
}
