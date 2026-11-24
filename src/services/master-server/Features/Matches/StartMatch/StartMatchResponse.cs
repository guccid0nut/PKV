// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Domain.Identifiers;

namespace KONGOR.MasterServer.Features.Matches.StartMatch;

public sealed record StartMatchResponse(
    bool Success,
    int StatusCode,
    string? ErrorMessage = null,
    Dictionary<string, object>? ResponseData = null)
{
    public static StartMatchResponse Succeeded(MatchId matchId, DateTimeOffset startedAt, string disabledHeroList = "") => new(
        true,
        200,
        ResponseData: new Dictionary<string, object>
        {
            { "match_id", matchId.Value },
            { "match_date", startedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") },
            { "file_host", "masterserver.kongor.net" },
            { "is_recommended", false },
            { "soccer_hero_list", "" },
            { "free_hero_list", "" },
            { "early_access_hero_list", "" },
            { "disabled_hero_list", disabledHeroList }
        });

    public static StartMatchResponse BadRequest(string message) =>
        new(false, 400, ErrorMessage: message);

    public static StartMatchResponse Unauthorized(string message) =>
        new(false, 401, ErrorMessage: message);

    public static StartMatchResponse Forbidden(string message) =>
        new(false, 403, ErrorMessage: message);
}
