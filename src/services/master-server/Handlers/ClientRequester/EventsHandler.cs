// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// For PhpSerialization
using KONGOR.MasterServer.Extensions;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class EventsHandler(ILogger<EventsHandler> logger) : IClientRequestHandler
{
    private ILogger Logger { get; } = logger;

    public Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? functionName = Request.GetQueryOrFormValue("f");

        LogEventsRequest(functionName ?? "NULL");

        Dictionary<string, object> response = new()
        {
            ["success"] = true,
            ["data"] = new Dictionary<string, object>()
        };

        return Task.FromResult<IActionResult>(new OkObjectResult(JsonSerializer.Serialize(response)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[Events] Request for {FunctionName} (STUB)")]
    private partial void LogEventsRequest(string functionName);
}
