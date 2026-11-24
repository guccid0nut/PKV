// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.ClientRequester;

public partial class HeroStatsResponseDTO
{
    [PHPProperty("error_code")] public int ErrorCode { get; init; } = 0;

    [PHPProperty("account_id")] public int AccountID { get; init; }

    [PHPProperty("mastery_info")] public List<HeroMasteryInfo> MasteryInfo { get; init; } = [];
}

public partial class ReplayResponseDTO
{
    [PHPProperty("res")] public bool Success { get; init; } = true;
}

public partial class RewardsResponseDTO
{
    [PHPProperty("vested_threshold")] public string VestedThreshold { get; init; } = "5";

    [PHPProperty(0)] public bool Success { get; init; } = true;
}
