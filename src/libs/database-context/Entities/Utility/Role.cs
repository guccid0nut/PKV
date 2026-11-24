// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Entities.Utility;

[Index(nameof(Name), IsUnique = true)]
public class Role
{
    [Key] public int ID { get; set; }

    [StringLength(20)] public required string Name { get; set; }
}
