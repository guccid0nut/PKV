// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class EchoHandler(ILogger<EchoHandler> logger) : IClientRequestHandler
{
    private ILogger Logger { get; } = logger;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("[EchoHandler] Received Echo Request.");

        // TEST 2: PHP Serialized Array (Matches WORKING_PAYLOADS.txt structure)
        // a:1:{s:3:"msg";s:4:"ECHO";}
        const string payload = "a:1:{s:3:\"msg\";s:4:\"ECHO\";}";

        // Force Content-Type to match WORKING_PAYLOADS.txt exactly
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(payload, cancellationToken);

        return new EmptyResult();
    }
}
