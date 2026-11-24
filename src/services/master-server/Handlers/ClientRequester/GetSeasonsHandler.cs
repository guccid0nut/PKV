// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class GetSeasonsHandler : IClientRequestHandler
{
    private static readonly string CachedSeasonsResponse = BuildSeasonsResponse();

    public Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IActionResult>(new ContentResult
        {
            Content = CachedSeasonsResponse,
            ContentType = "text/plain; charset=utf-8"
        });
    }

    private static string BuildSeasonsResponse()
    {
        List<string> seasonItems = [];
        for (int seasonId = 12; seasonId >= 1; seasonId--)
        {
            seasonItems.Add($"{seasonId},false");
            seasonItems.Add($"{seasonId},true");
        }

        string seasonsString = string.Join("|", seasonItems);

        Dictionary<object, object> response = new()
        {
            { 0, seasonsString }, { "0", seasonsString }, { "seasons", seasonsString }
        };

        return PhpSerialization.Serialize(response);
    }
}
