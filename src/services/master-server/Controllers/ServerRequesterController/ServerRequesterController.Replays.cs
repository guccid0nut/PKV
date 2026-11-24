// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Security;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

public partial class ServerRequesterController
{
    private async Task<IActionResult> HandleGetSpectatorHeader(CancellationToken cancellationToken = default)
    {
        string remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "<unknown>";
        string? session = ExtractServerSessionToken();
        bool isAuthorized = IsAuthorizedInternalService() || IsLocalDevelopmentLoopback();

        if (!isAuthorized && !string.IsNullOrWhiteSpace(session))
        {
            MatchServer? matchServer = await DistributedCache.GetMatchServerBySessionCookie(session)
                                       ?? await DistributedCache.ResurrectMatchServerBySession(session);
            if (matchServer is not null)
            {
                isAuthorized = true;
            }
            else
            {
                MatchServerManager? serverManager =
                    await DistributedCache.GetMatchServerManagerBySessionCookie(session);
                if (serverManager is not null)
                {
                    isAuthorized = true;
                }
            }
        }

        if (!isAuthorized)
        {
            Logger.LogWarning(
                "[ServerRequester:get_spectator_header] Rejected unauthorized spectator upload header request (Session: '{MaskedSession}') from IP '{RemoteIp}'.",
                SecurityMasking.MaskToken(session), remoteIp);
            return Unauthorized("No Match Server Could Be Found For Provided Session Cookie");
        }

        string uploadToken = await ReplayUploadAuthorizer.MintUploadTokenAsync(DistributedCache);
        string baseURL = Configuration.CDN.Host;
        string targetURL = $"{baseURL}/replays/upload";

        Dictionary<string, object> response = new()
        {
            // The Target URL For Uploading Replay Files
            ["target_url"] = targetURL,

            // The Headers Required For Uploading Replay Files
            // Regardless Of Whether Amazon Simple Storage Service (S3) Is Used Or Not, These Headers Are Recursively Applied To The Upload Request
            // The "s3_header" Key Is Hard-Coded In The Game Client, So It Must Be Included Even If S3 Is Not Used
            ["s3_header"] = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/zip",
                [ReplayUploadAuthorizer.ReplayUploadTokenHeader] = uploadToken
            }
        };

        return Ok(PhpSerialization.Serialize(response));
    }

    private async Task<IActionResult> HandleSetReplaySize(CancellationToken cancellationToken = default)
    {
        string? session = Request.Form["session"];

        if (session is null)
        {
            Logger.LogMissingSessionInSetReplaySize();
            return BadRequest(PhpSerialization.Serialize(new { error = "Missing Session" }));
        }

        MatchServer? matchServer = await DistributedCache.GetMatchServerBySessionCookie(session)
                                   ?? await DistributedCache.ResurrectMatchServerBySession(session);

        if (matchServer is null)
        {
            // Log Warning But Return OK To Prevent Crashes/Retries without modifying database state
            Logger.LogMatchServerNotFoundForSession(SecurityMasking.MaskToken(session));
            return Ok(PhpSerialization.Serialize(new { result = "OK" }));
        }

        string? matchIdString = Request.Form["match_id"];
        if (matchIdString is null)
        {
            Logger.LogMissingMatchId();
            return BadRequest(PhpSerialization.Serialize(new { error = "Missing Match ID" }));
        }

        string? fileSizeString = Request.Form["file_size"];
        if (fileSizeString is null)
        {
            Logger.LogMissingFileSize();
            return BadRequest(PhpSerialization.Serialize(new { error = "Missing File Size" }));
        }

        if (int.TryParse(matchIdString, out int matchId) && int.TryParse(fileSizeString, out int fileSize))
        {
            Logger.LogReceivedReplaySize(matchId, fileSize);
            await MatchHistoryRepository.UpdateMatchReplayUrlAsync(matchId, null, fileSize, cancellationToken);
        }
        else
        {
            Logger.LogInvalidMatchIdOrFileSize(matchIdString, fileSizeString);
        }

        // Always return OK to satisfy the server
        return Ok(PhpSerialization.Serialize(new { set_replay_size = "OK" }));
    }
}
