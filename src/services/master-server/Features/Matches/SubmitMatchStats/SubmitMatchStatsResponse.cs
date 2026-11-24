// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Features.Matches.SubmitMatchStats;

public sealed record SubmitMatchStatsResponse(
    bool Success,
    int StatusCode,
    string? ErrorMessage = null,
    Dictionary<string, object>? ResponseData = null)
{
    public static SubmitMatchStatsResponse Succeeded(int matchId) => new(
        true,
        200,
        ResponseData: new Dictionary<string, object>
        {
            { "match_id", matchId },
            { "match_info", "OK" },
            { "match_summ", "OK" },
            { "match_stats", "OK" },
            { "match_history", "OK" }
        });

    public static SubmitMatchStatsResponse BadRequest(string message) =>
        new(false, 400, ErrorMessage: message);

    /// <summary>
    ///     Transient failure (e.g. the processing queue rejected the job). The game server should retry the submission.
    /// </summary>
    public static SubmitMatchStatsResponse ServiceUnavailable(string message) =>
        new(false, 503, ErrorMessage: message);
}
