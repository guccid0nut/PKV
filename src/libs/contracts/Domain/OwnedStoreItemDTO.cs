// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

public class OwnedStoreItemDTO
{
    [PHPProperty("data")] public string Data { get; init; } = string.Empty;

    [PHPProperty("start_time")] public string StartTime { get; init; } = string.Empty;

    [PHPProperty("end_time")] public string EndTime { get; init; } = string.Empty;

    [PHPProperty("used")] public int Used { get; init; } = 0;

    [PHPProperty("score")] public string Score { get; init; } = "0";

    [PHPProperty("expiration_date")] public string ExpirationDate { get; init; } = "0";

    [PHPProperty("perm")] public string Permanent { get; init; } = "1";
}
