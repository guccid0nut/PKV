// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Extensions.Cache;
using KONGOR.MasterServer.Serialization;
using NEXUS.Contracts.Security;
using DistributedCacheExtensions = ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions;

namespace KONGOR.MasterServer.Controllers.ServerRequesterController;

public partial class ServerRequesterController
{
    private async Task<IActionResult> HandleLiveTick(CancellationToken cancellationToken = default)
    {
        string remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "<unknown>";
        string? session = ExtractServerSessionToken();
        bool isAuthorized = IsAuthorizedInternalService() || IsLocalDevelopmentLoopback();

        if (!isAuthorized && !string.IsNullOrWhiteSpace(session))
        {
            MatchServer? matchServer = await DistributedCacheExtensions.GetMatchServerBySessionCookie(DistributedCache, session)
                                       ?? await DistributedCacheExtensions.ResurrectMatchServerBySession(DistributedCache, session);
            if (matchServer is not null)
            {
                isAuthorized = true;
            }
            else
            {
                MatchServerManager? manager =
                    await DistributedCacheExtensions.GetMatchServerManagerBySessionCookie(DistributedCache, session);
                if (manager is not null)
                {
                    isAuthorized = true;
                }
            }
        }

        if (!isAuthorized)
        {
            Logger.LogWarning(
                "[ServerRequester:live_tick] Rejected unauthorized live telemetry tick (Session: '{MaskedSession}') from IP '{RemoteIp}'.",
                SecurityMasking.MaskToken(session), remoteIp);
            return Unauthorized("Unauthorized telemetry tick request.");
        }

        LiveServerTickPayloadDto? tickPayload = null;

        string? queryMatchIdStr = Request.Query["match_id"].FirstOrDefault();
        int queryMatchId = 0;
        if (!string.IsNullOrEmpty(queryMatchIdStr) && int.TryParse(queryMatchIdStr, out int qmId)) queryMatchId = qmId;

        if (Request.HasJsonContentType() ||
            (Request.ContentType is { } ct && ct.Contains("json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                tickPayload =
                    await JsonSerializer.DeserializeAsync(Request.Body,
                        MasterServerJsonContext.Default.LiveServerTickPayloadDto, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[ServerRequester] Failed to deserialize live_tick JSON stream.");
            }
        }
        else if (Request.HasFormContentType)
        {
            string? matchIdStr = Request.Form["match_id"];
            string? payloadJson = Request.Form["payload"];

            int matchID = 0;
            if (!string.IsNullOrEmpty(matchIdStr) && int.TryParse(matchIdStr, out int parsedMatchId))
                matchID = parsedMatchId;

            if (!string.IsNullOrEmpty(payloadJson))
                try
                {
                    tickPayload =
                        JsonSerializer.Deserialize(payloadJson, MasterServerJsonContext.Default.LiveServerTickPayloadDto);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[ServerRequester] Failed to deserialize live_tick payload JSON.");
                }

            tickPayload ??= new LiveServerTickPayloadDto
            {
                MatchID = matchID,
                ElapsedSeconds = int.TryParse(Request.Form["elapsed"], out int el) ? el : 0,
                ScoreLegion = int.TryParse(Request.Form["score_legion"], out int sl) ? sl : 0,
                ScoreHellbourne = int.TryParse(Request.Form["score_hellbourne"], out int sh) ? sh : 0,
                GoldAdvantageLegion = int.TryParse(Request.Form["gold_adv_legion"], out int gal) ? gal : 0,
                GoldAdvantageHellbourne = int.TryParse(Request.Form["gold_adv_hellbourne"], out int gah) ? gah : 0
            };

            if (tickPayload.MatchID <= 0 && matchID > 0) tickPayload.MatchID = matchID;
        }

        if (tickPayload is { MatchID: <= 0 } && queryMatchId > 0) tickPayload.MatchID = queryMatchId;

        if (tickPayload is { MatchID: > 0 })
        {
            MatchStartData? existingStartData =
                await DistributedCacheExtensions.GetMatchStartData(DistributedCache, tickPayload.MatchID);
            if (existingStartData == null && IsLocalDevelopmentLoopback())
            {
                MatchStartData autoStartData = new()
                {
                    MatchID = tickPayload.MatchID,
                    ServerID = 1,
                    ServerName = "Local Dev Server",
                    Map = "midwars",
                    Version = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion,
                    MatchName = $"Dev Match #{tickPayload.MatchID}",
                    HostAccountName = tickPayload.Players.FirstOrDefault()?.AccountName ?? "DedicatedHost",
                    IsCasual = false,
                    MatchType = 1,
                    MatchMode = "Mid Wars (All Pick)",
                    TimestampStarted = TimeProvider.System.GetUtcNow(),
                    ConnectedPlayersCount = Math.Max(1, tickPayload.Players.Count),
                    MaximumPlayersCount = 10,
                    Tier = 1
                };
                await DistributedCacheExtensions.SetMatchStartData(DistributedCache, autoStartData);
            }

            if (existingStartData != null || IsLocalDevelopmentLoopback())
            {
                await DistributedCache.SetLiveMatchTelemetry(tickPayload.MatchID, tickPayload);
                if (Logger.IsEnabled(LogLevel.Information))
                    Logger.LogInformation(
                        "[ServerRequester] Ingested live authoritative tick for Match #{MatchId} (Elapsed: {Elapsed}s, Players: {PlayerCount}, Wards: {WardCount})",
                        tickPayload.MatchID, tickPayload.ElapsedSeconds, tickPayload.Players.Count,
                        tickPayload.ActiveWards.Count);
            }
        }

        const string response = "a:1:{s:6:\"status\";s:2:\"OK\";}";
        Response.ContentType = "text/plain; charset=utf-8";
        await Response.WriteAsync(response, cancellationToken);
        return new EmptyResult();
    }
}
