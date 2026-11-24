// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Features.Matches.GetMatchHistory;

public sealed record GetMatchHistoryResponse(
    bool Success,
    int StatusCode,
    string? ErrorMessage = null,
    Dictionary<object, object>? Data = null)
{
    public static GetMatchHistoryResponse Succeeded(Dictionary<object, object> data) =>
        new(true, 200, Data: data);

    public static GetMatchHistoryResponse Unauthorized(string message) =>
        new(false, 401, ErrorMessage: message);

    public static GetMatchHistoryResponse NotFound(string message) =>
        new(false, 404, ErrorMessage: message);
}
