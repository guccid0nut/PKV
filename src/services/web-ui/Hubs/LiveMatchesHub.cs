// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;

namespace DAWNBRINGER.WebPortal.UI.Hubs;

public class LiveMatchesHub(
    ILiveMatchService liveMatchService,
    ILogger<LiveMatchesHub> logger,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : Hub<ILiveMatchesClient>
{
    public const string OverviewGroupName = "LiveMatchesOverview";

    public async Task JoinOverview()
    {
        if (!StaffAuthorizationHelper.IsStaff(Context.User))
        {
            logger.LogWarning(
                "[LiveHub] Access Denied: Non-staff connection {ConnectionId} (User: {User}) attempted to join Live Matches Overview",
                Context.ConnectionId, Context.User?.Identity?.Name ?? "Anonymous");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, OverviewGroupName);
        List<LiveMatchData> activeMatches = await liveMatchService.GetActiveLiveMatchesAsync();
        await Clients.Caller.OnOverviewUpdated(activeMatches);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[LiveHub] Staff Client {ConnectionId} subscribed to Live Matches Overview ({Count} active matches)",
                Context.ConnectionId, activeMatches.Count);
    }

    public async Task LeaveOverview()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, OverviewGroupName);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[LiveHub] Client {ConnectionId} unsubscribed from Live Matches Overview",
                Context.ConnectionId);
    }

    public async Task JoinMatch(int matchId)
    {
        if (!StaffAuthorizationHelper.IsStaff(Context.User))
        {
            logger.LogWarning(
                "[LiveHub] Access Denied: Non-staff connection {ConnectionId} (User: {User}) attempted to spectate Match #{MatchId}",
                Context.ConnectionId, Context.User?.Identity?.Name ?? "Anonymous", matchId);
            return;
        }

        string groupName = GetMatchGroupName(matchId);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        LiveMatchData? snapshot = await liveMatchService.GetLiveMatchSnapshotAsync(matchId);
        if (snapshot != null) await Clients.Caller.OnMatchSnapshot(snapshot);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[LiveHub] Staff Client {ConnectionId} joined Match #{MatchId} room (Snapshot Sent: {HasSnapshot})",
                Context.ConnectionId, matchId, snapshot != null);
    }

    public async Task LeaveMatch(int matchId)
    {
        string groupName = GetMatchGroupName(matchId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[LiveHub] Client {ConnectionId} left Match #{MatchId} room", Context.ConnectionId,
                matchId);
    }

    public async Task SubmitMatchTick(LiveServerTickPayloadDto payload)
    {
        HttpContext? httpContext = Context?.Features is not null ? Context.GetHttpContext() : null;
        if (!StaffAuthorizationHelper.IsStaff(Context?.User) &&
            !HasTrustedInternalServiceKey(httpContext, configuration, environment))
        {
            logger.LogWarning(
                "[SECURITY-ALERT] [LiveHub] Blocked unauthorized SubmitMatchTick for Match #{MatchId} from Connection {ConnectionId} (User: {User}, RemoteIp: {RemoteIp})",
                payload?.MatchID ?? 0,
                Context?.ConnectionId ?? "unknown",
                Context?.User?.Identity?.Name ?? "Anonymous",
                httpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            throw new HubException("Unauthorized: valid internal service key or staff authorization is required.");
        }

        if (payload is { MatchID: > 0 })
        {
            await liveMatchService.ApplyServerTickAsync(payload);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("[LiveHub] Ingested live server tick for Match #{MatchId} ({PlayerCount} players)",
                    payload.MatchID, payload.Players.Count);
        }
    }

    public static string GetMatchGroupName(int matchId)
    {
        return $"Match_{matchId}";
    }

    internal static bool HasTrustedInternalServiceKey(
        HttpContext? httpContext,
        IConfiguration? explicitConfig = null,
        IHostEnvironment? explicitEnvironment = null)
    {
        if (httpContext == null)
        {
            return false;
        }

        if (!httpContext.Request.Headers.TryGetValue(SecurityConstants.InternalServiceKeyHeader, out StringValues headerValues) ||
            StringValues.IsNullOrEmpty(headerValues))
        {
            return false;
        }

        IConfiguration? cfg = explicitConfig ?? httpContext.RequestServices?.GetService<IConfiguration>();
        IHostEnvironment? env = explicitEnvironment ?? httpContext.RequestServices?.GetService<IHostEnvironment>();
        bool isDevelopment = env?.IsDevelopment() == true;

        string? expectedKey = cfg?["InternalServiceKey"];
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            if (!isDevelopment)
            {
                return false;
            }

            expectedKey = SecurityConstants.DefaultInternalServiceKey;
        }
        else if (!isDevelopment &&
                 string.Equals(expectedKey, SecurityConstants.DefaultInternalServiceKey, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(headerValues.ToString());
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
