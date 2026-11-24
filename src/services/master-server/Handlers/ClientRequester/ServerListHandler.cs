// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using ASPIRE.Common.Caching;
using KONGOR.MasterServer.Extensions;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class ServerListHandler(
    IDatabase distributedCache,
    IMemoryCache memoryCache,
    ILogger<ServerListHandler> logger,
    INexusHybridCache? hybridCache = null) : IClientRequestHandler
{
    private IDatabase DistributedCache { get; } = distributedCache;
    private IMemoryCache MemoryCache { get; } = memoryCache;
    private ILogger Logger { get; } = logger;
    private INexusHybridCache? HybridCache { get; } = hybridCache;

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;

        string? cookie = ClientRequestHelper.GetCookie(Request);

        if (cookie is null)
        {
            LogMissingCookie();
            return new BadRequestObjectResult("Missing cookie");
        }

        LogRequestReceived(SecurityMasking.MaskToken(cookie));

        string? gameType = Request.GetFormOrQueryValue("gametype") ?? Request.GetFormOrQueryValue("game_type");
        LogGameType(gameType ?? "NULL");

        string? region = Request.GetFormOrQueryValue("region");
        string normalizedRegion = string.IsNullOrWhiteSpace(region) ? "ALL" : region.Trim().ToUpperInvariant();
        LogRegion(normalizedRegion);

        string? remoteIpAddress = null;
        string? forwardedFor = Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                               ?? Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor) && global::KONGOR.MasterServer.Services.Security.TrustedProxyIpResolver.IsTrustedProxyPeer(context.Connection.RemoteIpAddress ?? IPAddress.Loopback))
        {
            string firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (IPAddress.TryParse(firstIp, out IPAddress? parsedForwarded))
            {
                remoteIpAddress = parsedForwarded.IsIPv4MappedToIPv6
                    ? parsedForwarded.MapToIPv4().ToString()
                    : parsedForwarded.ToString();
            }
        }
        remoteIpAddress ??= context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation("[ServerList] Requestor IP: {IP} (Conn: {ConnIP}, Forwarded: {Forwarded})",
                remoteIpAddress, context.Connection.RemoteIpAddress?.ToString(), forwardedFor);

        if (gameType is not ("10" or "90"))
        {
            LogUnknownGameType(gameType ?? "NULL");
            return new UnprocessableEntityObjectResult($@"Unknown Server List Game Type ""{gameType ?? "NULL"}""");
        }

        string cacheKey = $"SERVER-LIST:{gameType}:{normalizedRegion}:{remoteIpAddress ?? "DEFAULT"}:v2";

        // Fast-path: Use INexusHybridCache (L1 in-memory + L2 Redis/Garnet with stampede protection)
        if (HybridCache != null)
        {
            string? serializedPayload = await HybridCache.GetOrCreateAsync<string>(
                cacheKey,
                async ct =>
                {
                    List<MatchServer> servers = await DistributedCache.GetMatchServers();
                    LogServersFound(servers.Count);

                    List<MatchServer> publicServers = [.. servers.Where(s => !s.IsQueueArcadeServer())];
                    int excludedArcadeCount = servers.Count - publicServers.Count;
                    if (excludedArcadeCount > 0 && Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation(
                            "[ServerList] (HybridCache) Excluded {ExcludedArcadeCount} Queue Arcade server(s) from public GameType {GameType} list ({PublicCount}/{TotalCount} remaining).",
                            excludedArcadeCount, gameType, publicServers.Count, servers.Count);
                    }

                    List<MatchServer> filteredServers = ServerListResponse.FilterByRegion(publicServers, normalizedRegion);
                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation(
                            "[ServerList] (HybridCache) Filtered {TotalCount} public servers down to {FilteredCount} for region '{Region}'",
                            publicServers.Count, filteredServers.Count, normalizedRegion);

                        foreach (MatchServer server in filteredServers)
                        {
                            string resolvedIp = ServerListResponse.ResolveIP(server, remoteIpAddress) ?? server.IPAddress;
                            string resolvedPort = ServerListResponse.ResolvePort(server, remoteIpAddress);
                            Logger.LogInformation(
                                "[ServerList] Server {ID} Status: {Status} | Location: '{Location}' | Name: '{Name}' | Resolved: {ResolvedIP}:{ResolvedPort} | Raw: {IP}:{Port} (Public: {PublicIP}, Local: {LocalIP})",
                                server.ID, server.Status, server.Location, server.Name, resolvedIp, resolvedPort, server.IPAddress, server.Port, server.TruePublicIPAddress, server.LocalIPAddress);
                        }
                    }

                    if (gameType == "10")
                    {
                        LogReturningJoinList();
                        List<MatchServer> joinableServers = filteredServers
                            .Where(s => s.Status is ServerStatus.SERVER_STATUS_ACTIVE or ServerStatus.SERVER_STATUS_LOADING or ServerStatus.SERVER_STATUS_IDLE)
                            .ToList();
                        ServerForJoinListResponse serversForJoin = new(joinableServers, cookie, remoteIpAddress);
                        return PhpSerializer.SerializeToString(serversForJoin);
                    }
                    else
                    {
                        LogReturningCreateList();
                        List<MatchServer> availableServers = filteredServers
                            .Where(s => s.Status is ServerStatus.SERVER_STATUS_IDLE or ServerStatus.SERVER_STATUS_SLEEPING or ServerStatus.SERVER_STATUS_UNKNOWN
                                        || (s.Status == ServerStatus.SERVER_STATUS_LOADING && s.ConnectedPlayersCount <= 1))
                            .ToList();
                        if (Logger.IsEnabled(LogLevel.Information))
                        {
                            Logger.LogInformation("[ServerList] Found {Count} available servers for GameType 90 create list.", availableServers.Count);
                        }
                        ServerForCreateListResponse serversForCreate = new(availableServers, cookie, remoteIpAddress);
                        return PhpSerializer.SerializeToString(serversForCreate);
                    }
                },
                tags: ["server-list"],
                l1Expiration: TimeSpan.FromSeconds(2),
                l2Expiration: TimeSpan.FromSeconds(5),
                allowStaleOnError: true,
                cancellationToken: cancellationToken);

            if (serializedPayload is not null)
            {
                return new PhpResult(serializedPayload);
            }

            return new UnprocessableEntityObjectResult("Error generating server list response");
        }

        // Fallback: Standalone MemoryCache path
        if (!MemoryCache.TryGetValue(cacheKey, out IActionResult? cachedResult))
        {
            List<MatchServer> servers = await DistributedCache.GetMatchServers();
            LogServersFound(servers.Count);

            List<MatchServer> publicServers = [.. servers.Where(s => !s.IsQueueArcadeServer())];
            int excludedArcadeCount = servers.Count - publicServers.Count;
            if (excludedArcadeCount > 0 && Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(
                    "[ServerList] (MemoryCache fallback) Excluded {ExcludedArcadeCount} Queue Arcade server(s) from public GameType {GameType} list ({PublicCount}/{TotalCount} remaining).",
                    excludedArcadeCount, gameType, publicServers.Count, servers.Count);
            }

            List<MatchServer> filteredServers = ServerListResponse.FilterByRegion(publicServers, normalizedRegion);
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(
                    "[ServerList] (MemoryCache fallback) Filtered {TotalCount} public servers down to {FilteredCount} for region '{Region}'",
                    publicServers.Count, filteredServers.Count, normalizedRegion);

                foreach (MatchServer server in filteredServers)
                {
                    string resolvedIp = ServerListResponse.ResolveIP(server, remoteIpAddress) ?? server.IPAddress;
                    string resolvedPort = ServerListResponse.ResolvePort(server, remoteIpAddress);
                    Logger.LogInformation(
                        "[ServerList] Server {ID} Status: {Status} | Location: '{Location}' | Name: '{Name}' | Resolved: {ResolvedIP}:{ResolvedPort} | Raw: {IP}:{Port} (Public: {PublicIP}, Local: {LocalIP})",
                        server.ID, server.Status, server.Location, server.Name, resolvedIp, resolvedPort, server.IPAddress, server.Port, server.TruePublicIPAddress, server.LocalIPAddress);
                }
            }

            switch (gameType)
            {
                case "10":
                    LogReturningJoinList();
                    List<MatchServer> joinableServers = filteredServers
                        .Where(s => s.Status is ServerStatus.SERVER_STATUS_ACTIVE or ServerStatus.SERVER_STATUS_LOADING or ServerStatus.SERVER_STATUS_IDLE)
                        .ToList();
                    ServerForJoinListResponse serversForJoin = new(joinableServers, cookie, remoteIpAddress);
                    cachedResult = new PhpResult(serversForJoin);
                    break;

                case "90":
                default:
                    LogReturningCreateList();
                    List<MatchServer> availableServers = filteredServers
                        .Where(s => s.Status is ServerStatus.SERVER_STATUS_IDLE or ServerStatus.SERVER_STATUS_SLEEPING or ServerStatus.SERVER_STATUS_UNKNOWN
                                    || (s.Status == ServerStatus.SERVER_STATUS_LOADING && s.ConnectedPlayersCount <= 1))
                        .ToList();
                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation("[ServerList] (MemoryCache fallback) Found {Count} available servers for GameType 90 create list.", availableServers.Count);
                    }
                    ServerForCreateListResponse serversForCreate = new(availableServers, cookie, remoteIpAddress);
                    cachedResult = new PhpResult(serversForCreate);
                    break;
            }

            MemoryCache.Set(cacheKey, cachedResult, TimeSpan.FromSeconds(2));
        }

        return cachedResult ?? new UnprocessableEntityObjectResult("Error generating server list response");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[ServerList] Request received. Cookie: {CookiePrefix}")]
    private partial void LogRequestReceived(string cookiePrefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ServerList] Missing cookie.")]
    private partial void LogMissingCookie();

    [LoggerMessage(Level = LogLevel.Information, Message = "[ServerList] GameType: {GameType}")]
    private partial void LogGameType(string gameType);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ServerList] Region: {Region}")]
    private partial void LogRegion(string region);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ServerList] Found {Count} match servers in cache.")]
    private partial void LogServersFound(int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ServerList] Returning ServerForJoinListResponse (gameType 10).")]
    private partial void LogReturningJoinList();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ServerList] Returning ServerForCreateListResponse (gameType 90).")]
    private partial void LogReturningCreateList();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ServerList] Unknown gameType '{GameType}'.")]
    private partial void LogUnknownGameType(string gameType);
}

