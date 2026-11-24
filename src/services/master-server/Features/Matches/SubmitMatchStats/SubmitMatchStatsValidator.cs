// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Features.Matches.SubmitMatchStats;

public sealed class SubmitMatchStatsValidator
{
    public (bool IsValid, string? ErrorMessage) Validate(SubmitMatchStatsCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Session))
        {
            return (false, @"Missing Value For Form Parameter ""session""");
        }

        if (command.MatchId.Value <= 0)
        {
            return (false, "Invalid Match ID");
        }

        if (command.Form.MatchStats == null || !command.Form.MatchStats.ContainsKey("match_id"))
        {
            return (false, "Missing match_stats[match_id]");
        }

        return (true, null);
    }
}
