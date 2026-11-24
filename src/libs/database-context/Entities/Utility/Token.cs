// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Entities.Utility;

public class Token
{
    [Key] public int ID { get; set; }

    public int? UserID { get; set; }

    public TokenPurpose Purpose { get; set; }

    [MaxLength(256)] public string EmailAddress { get; set; } = string.Empty;

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset? TimestampConsumed { get; set; }

    public Guid Value { get; set; } = Guid.NewGuid();

    [MaxLength(4096)] public string Data { get; set; } = string.Empty;

    public DateTime? Expires { get; set; }
}
