// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

public partial class ServerRequesterController
{
    private async Task<IActionResult> HandleShutdown(CancellationToken cancellationToken = default)
    {
        if (!Request.HasFormContentType)
        {
            Logger.LogWarning("[Shutdown] Rejected shutdown request without form content type.");
            return BadRequest("Invalid Form Content-Type");
        }

        string? session = Request.Form["session"];

        if (string.IsNullOrWhiteSpace(session))
            return BadRequest(@"Missing Value For Form Parameter ""session""");

        MatchServer? matchServer = await DistributedCache.GetMatchServerBySessionCookie(session)
                                   ?? await DistributedCache.ResurrectMatchServerBySession(session);
        if (matchServer is null)
        {
            Logger.LogInformation(
                "[Shutdown] Session cookie '{MaskedSession}' already evicted or expired; acknowledging idempotent shutdown.",
                SecurityMasking.MaskToken(session));
            return Ok();
        }

        await DistributedCache.RemoveMatchServerByID(matchServer.ID);
        Logger.LogInformation("[Shutdown] Removed MatchServer {ID} on Port {Port} from cache.", matchServer.ID,
            matchServer.Port);

        return Ok();
    }
}
