// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

// {"grab_last_matches":["all_stats","match_summ"]}

public partial class GrabLastMatchesResponse
{
    [PHPProperty("all_stats")] public required Dictionary<int, List<MatchPlayerStatistics>> AllStats { get; init; }

    [PHPProperty("match_summ")] public required Dictionary<int, MatchSummary> MatchSummary { get; init; }
}
