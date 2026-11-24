// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Features.Matches.StartMatch;
using KONGOR.MasterServer.Helpers.Serialisation;
using Microsoft.AspNetCore.Mvc;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

public partial class ServerRequesterController
{
    private string? GetFormOrQueryValue(string key)
    {
        if (Request.HasFormContentType)
        {
            string? formVal = Request.Form[key].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(formVal)) return formVal;
        }

        return Request.Query[key].FirstOrDefault();
    }

    private async Task<IActionResult> HandleMatchStart(CancellationToken cancellationToken = default)
    {
        string remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "<unknown>";
        string? session = ExtractServerSessionToken();
        MatchServer? matchServer = null;

        if (!string.IsNullOrWhiteSpace(session))
        {
            matchServer = await ResolveMatchServerBySessionWithGraceAsync(session, "ServerRequester:start_game", cancellationToken);
        }
        else if (IsLocalDevelopmentLoopback())
        {
            string? portStr = GetFormOrQueryValue("port");
            if (int.TryParse(portStr, out int reqPort) && reqPort is >= 1 and <= 65535)
            {
                List<MatchServer> allServers = await DistributedCache.GetMatchServers();
                matchServer = allServers.FirstOrDefault(s =>
                    (s.Port == reqPort || s.LocalPort == reqPort) &&
                    ((IPAddress.TryParse(s.IPAddress, out IPAddress? sip) && IPAddress.IsLoopback(sip)) ||
                     string.Equals(s.IPAddress, "localhost", StringComparison.OrdinalIgnoreCase)));

                if (matchServer is not null && !string.IsNullOrWhiteSpace(matchServer.Cookie))
                {
                    session = matchServer.Cookie;
                    Logger.LogInformation(
                        "[ServerRequester:start_game] Development loopback port fallback matched MatchServer #{ServerId} on port {Port}.",
                        matchServer.ID, reqPort);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(session))
        {
            Logger.LogWarning(
                "[ServerRequester:start_game] Rejected start_game request missing 'session' parameter from IP '{RemoteIp}'.",
                remoteIp);
            return BadRequest(@"Missing Value For Form Parameter ""session""");
        }

        if (matchServer is null)
        {
            Logger.LogWarning(
                "[ServerRequester:start_game] Unauthorized: No Match Server found for session '{MaskedSession}' from IP '{RemoteIp}'.",
                SecurityMasking.MaskToken(session), remoteIp);
            return Unauthorized("No Match Server Could Be Found For Provided Session Cookie");
        }

        string? map = GetFormOrQueryValue("map");
        if (string.IsNullOrWhiteSpace(map))
        {
            map = "caldavar";
        }

        string? version = GetFormOrQueryValue("version");
        if (string.IsNullOrWhiteSpace(version))
        {
            version = "4.10.1.0";
        }

        // Resilient Host Account Name resolution
        string? rawHost = GetFormOrQueryValue("mstr");
        string hostAccountName = !string.IsNullOrWhiteSpace(rawHost) ? rawHost.TrimEnd(':') : string.Empty;

        if (string.IsNullOrWhiteSpace(hostAccountName))
        {
            string? fallbackHost = GetFormOrQueryValue("login") ??
                                   GetFormOrQueryValue("host") ??
                                   GetFormOrQueryValue("account");

            if (!string.IsNullOrWhiteSpace(fallbackHost))
            {
                hostAccountName = fallbackHost.TrimEnd(':');
            }
        }

        if (string.IsNullOrWhiteSpace(hostAccountName))
        {
            if (!string.IsNullOrWhiteSpace(matchServer.HostAccountName))
            {
                hostAccountName = matchServer.HostAccountName.TrimEnd(':');
            }
            else if (!string.IsNullOrWhiteSpace(matchServer.Name))
            {
                hostAccountName = matchServer.Name;
            }
        }

        if (string.IsNullOrWhiteSpace(hostAccountName))
        {
            hostAccountName = "DedicatedHost";
        }

        // Resilient Match Name resolution
        string? rawMName = GetFormOrQueryValue("mname");
        string matchName = !string.IsNullOrWhiteSpace(rawMName) ? rawMName : matchServer.Name;
        if (string.IsNullOrWhiteSpace(matchName))
        {
            matchName = "Kongor Match";
        }

        // Resilient Casual Mode resolution
        string? casualStr = GetFormOrQueryValue("casual");
        bool isCasual = casualStr == "1";

        // Resilient Match Type resolution
        string? matchTypeStr = GetFormOrQueryValue("arrangedmatchtype");
        int matchType = int.TryParse(matchTypeStr, out int parsedArrangedMatchType) ? parsedArrangedMatchType : 0;

        // Resilient Match Mode resolution
        string? matchModeInput = GetFormOrQueryValue("match_mode");
        if (string.IsNullOrWhiteSpace(matchModeInput))
        {
            matchModeInput = "ap";
        }

        string matchMode = int.TryParse(matchModeInput, out int parsedMatchMode)
            ? MatchModeDefinition.GetCodeFromId(parsedMatchMode)
            : matchModeInput;

        StartMatchCommand command = new(
            matchServer.Cookie,
            map,
            version,
            matchName,
            hostAccountName,
            isCasual,
            matchType,
            matchMode);

        StartMatchResponse result = await StartMatchHandler.HandleAsync(command, cancellationToken);
        if (!result.Success)
        {
            return StatusCode(result.StatusCode, result.ErrorMessage);
        }

        return Ok(PhpSerialization.Serialize(result.ResponseData));
    }

    private async Task<IActionResult> HandleMatchAbandon(CancellationToken cancellationToken = default)
    {
        string remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "<unknown>";
        string? session = ExtractServerSessionToken();
        string? matchIdStr = GetFormOrQueryValue("match_id");
        string reason = GetFormOrQueryValue("reason") ?? "LobbyCancelled";

        if (string.IsNullOrWhiteSpace(matchIdStr) || !int.TryParse(matchIdStr, out int matchId) || matchId <= 0)
        {
            Logger.LogWarning(
                "[ServerRequester:abandon_match] Rejected abandon_match with missing or invalid match_id '{MatchId}' from IP '{RemoteIp}'.",
                matchIdStr ?? "<null>", remoteIp);
            return BadRequest(@"Missing Or Invalid Value For Form Parameter ""match_id""");
        }

        bool authorizedViaInternalKey = IsAuthorizedInternalService();
        MatchServer? matchServer = null;

        if (!authorizedViaInternalKey)
        {
            if (string.IsNullOrWhiteSpace(session))
            {
                Logger.LogWarning(
                    "[ServerRequester:abandon_match] Rejected abandon_match for Match #{MatchId} missing 'session' parameter from IP '{RemoteIp}'.",
                    matchId, remoteIp);
                return BadRequest(@"Missing Value For Form Parameter ""session""");
            }

            matchServer = await ResolveMatchServerBySessionWithGraceAsync(session, "ServerRequester:abandon_match", cancellationToken);
            MatchServerManager? serverManager = matchServer is null
                ? await DistributedCache.GetMatchServerManagerBySessionCookie(session)
                : null;

            if (matchServer is null && serverManager is null)
            {
                Logger.LogWarning(
                    "[ServerRequester:abandon_match] Unauthorized: No Match Server or Manager found for session '{MaskedSession}' on Match #{MatchId} from IP '{RemoteIp}'.",
                    SecurityMasking.MaskToken(session), matchId, remoteIp);
                return Unauthorized("No Match Server Could Be Found For Provided Session Cookie");
            }

            MatchStartData? startData = await DistributedCache.GetMatchStartData(matchId);
            if (startData is not null && startData.ServerID > 0)
            {
                if (matchServer is not null && startData.ServerID != matchServer.ID)
                {
                    Logger.LogWarning(
                        "[ServerRequester:abandon_match] Cross-server abandon blocked: Match #{MatchId} is owned by Server #{OwnerServerId}, but caller authenticated as Server #{CallerServerId} from IP '{RemoteIp}'.",
                        matchId, startData.ServerID, matchServer.ID, remoteIp);
                    return Unauthorized("Match Server Session Does Not Own Specified Match");
                }

                if (serverManager is not null && !serverManager.MatchServerIDs.Contains(startData.ServerID))
                {
                    Logger.LogWarning(
                        "[ServerRequester:abandon_match] Cross-manager abandon blocked: Match #{MatchId} (Server #{OwnerServerId}) is not managed by Manager #{ManagerId} from IP '{RemoteIp}'.",
                        matchId, startData.ServerID, serverManager.ID, remoteIp);
                    return Unauthorized("Server Manager Session Does Not Own Specified Match");
                }
            }
        }

        await StartMatchHandler.AbandonMatchAsync(matchId, reason, matchServer?.Cookie ?? session, cancellationToken);
        Logger.LogInformation(
            "[ServerRequester:abandon_match] Processed authenticated abandon for Match #{MatchId} (Reason: '{Reason}', AuthViaInternalKey: {ViaKey}) from IP '{RemoteIp}'.",
            matchId, reason, authorizedViaInternalKey, remoteIp);

        return Ok(PhpSerialization.Serialize(true));
    }
}
