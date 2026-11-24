// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class GameHandler(IDatabase distributedCache, ILogger<GameHandler> logger) : IClientRequestHandler
{
    private IDatabase DistributedCache { get; } = distributedCache;
    private ILogger Logger { get; } = logger;

    public Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;
        string? function = Request.GetFormOrQueryValue("f");
        // Query fallback handled inside GetFormOrQueryValue

        return function switch
        {
            "create_game" => HandleCreateGame(context, cancellationToken),
            "new_game_available" => HandleNewGameAvailable(),
            _ => Task.FromResult<IActionResult>(new BadRequestObjectResult($"Unknown function: {function}"))
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[GameHandler] Unknown function '{FunctionName}'.")]
    private partial void LogUnknownFunction(string functionName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GameHandler] CreateGame request: Name='{Name}', Map='{Map}', Mode='{Mode}'")]
    private partial void LogCreateGameRequest(string name, string map, string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[GameHandler] Missing required parameters.")]
    private partial void LogMissingParameters();

    [LoggerMessage(Level = LogLevel.Information, Message = "[GameHandler] Fetching match servers from cache...")]
    private partial void LogFetchingServers();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[GameHandler] No idle servers available out of {TotalCount}.")]
    private partial void LogNoServersAvailable(int totalCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GameHandler] Selected server '{ServerName}' (ID: {ServerId})")]
    private partial void LogSelectedServer(string serverName, int serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[GameHandler] Unauthorized attempt to create game.")]
    private partial void LogUnauthorizedAttempt();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GameHandler] Saving match start data for MatchID: {MatchId}")]
    private partial void LogSavingMatchData(int matchId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[GameHandler] Match {MatchId} created successfully for {HostAccountName}.")]
    private partial void LogMatchCreated(int matchId, string hostAccountName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[GameHandler] Handling new_game_available stub.")]
    private partial void LogNewGameAvailableStub();

    private async Task<IActionResult> HandleCreateGame(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;

        string? name = Request.GetFormOrQueryValue("name");
        string? map = Request.GetFormOrQueryValue("map");
        string? mode = Request.GetFormOrQueryValue("mode");
        string? teamSize = Request.GetFormOrQueryValue("team_size");
        string? privateGame = Request.GetFormOrQueryValue("private");

        LogCreateGameRequest(name ?? "NULL", map ?? "NULL", mode ?? "NULL");

        if (name is null || map is null || mode is null)
        {
            LogMissingParameters();
            return new PhpResult(new { error = "Missing Required Parameters" }, StatusCodes.Status400BadRequest);
        }

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

        LogFetchingServers();
        List<MatchServer> rawServers = await DistributedCache.GetMatchServers();
        List<MatchServer> allServers = [.. rawServers.Where(s => !s.IsQueueArcadeServer())];
        if (allServers.Count < rawServers.Count && Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[GameHandler] Excluded {ExcludedArcadeCount} Queue Arcade server(s) from create_game pool ({EligibleCount}/{TotalCount} eligible).",
                rawServers.Count - allServers.Count,
                allServers.Count,
                rawServers.Count);
        }

        string? region = Request.GetFormOrQueryValue("region");
        List<MatchServer> candidateServers = allServers;
        if (!string.IsNullOrWhiteSpace(region) && !region.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            candidateServers = ServerListResponse.FilterByRegion(allServers, region);
        }

        List<MatchServer> availableServers =
        [
            .. candidateServers
                .Where(s => s.Status is ServerStatus.SERVER_STATUS_IDLE or ServerStatus.SERVER_STATUS_SLEEPING or ServerStatus.SERVER_STATUS_UNKNOWN
                            || (s.Status == ServerStatus.SERVER_STATUS_LOADING && s.ConnectedPlayersCount <= 1))
        ];

        // If no servers available in requested region, fall back to any available server across all regions
        if (availableServers.Count == 0 && candidateServers != allServers)
        {
            availableServers =
            [
                .. allServers
                    .Where(s => s.Status is ServerStatus.SERVER_STATUS_IDLE or ServerStatus.SERVER_STATUS_SLEEPING or ServerStatus.SERVER_STATUS_UNKNOWN
                                || (s.Status == ServerStatus.SERVER_STATUS_LOADING && s.ConnectedPlayersCount <= 1))
            ];
        }

        if (availableServers.Count == 0)
        {
            LogNoServersAvailable(allServers.Count);
            return new PhpResult(new { match_id = 0, error = "No Servers Available" });
        }

        MatchServer selectedServer = availableServers[Random.Shared.Next(availableServers.Count)];
        LogSelectedServer(selectedServer.Name, selectedServer.ID);

        if (context.Items["SessionAccountName"] is not string accountName)
        {
            LogUnauthorizedAttempt();
            return new PhpResult(new { error = "Unauthorized" }, StatusCodes.Status401Unauthorized);
        }

        int matchID = await DistributedCache.GenerateNextMatchID();

        MatchStartData matchStartData = new()
        {
            MatchID = matchID,
            MatchName = name,
            ServerID = selectedServer.ID,
            ServerName = selectedServer.Name,
            HostAccountName = accountName,
            Map = map,
            MaximumPlayersCount = int.TryParse(teamSize, out int parsedTeamSize) && parsedTeamSize > 0 ? Math.Clamp(parsedTeamSize * 2, 2, 24) : 10,
            MatchMode = mode,
            Version = NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion,
            IsCasual = mode.Contains("casual", StringComparison.OrdinalIgnoreCase),
            MatchType = privateGame == "1" ? 3 : 0,
            Options = MatchOptions.None
        };

        LogSavingMatchData(matchStartData.MatchID);
        await DistributedCache.SetMatchStartData(matchStartData);

        // Transition server immediately to LOADING with match name so it disappears from create list and enters join list
        selectedServer.Status = ServerStatus.SERVER_STATUS_LOADING;
        selectedServer.Name = name;
        await DistributedCache.SetMatchServer(selectedServer.HostAccountName, selectedServer);

        LogMatchCreated(matchStartData.MatchID, accountName);

        string resolvedAddress = ServerListResponse.ResolveIP(selectedServer, remoteIpAddress) ?? selectedServer.IPAddress;
        string resolvedPort = ServerListResponse.ResolvePort(selectedServer, remoteIpAddress);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "[GameHandler] Returning create_game connection endpoint: {ResolvedAddress}:{ResolvedPort} (Server ID {ServerId}, Name: '{Name}') to Requestor: {RequestorIP}",
                resolvedAddress, resolvedPort, selectedServer.ID, selectedServer.Name, remoteIpAddress);
        }

        return new PhpResult(new Dictionary<string, object>
        {
            { "match_id", matchStartData.MatchID },
            { "server_id", selectedServer.ID },
            { "server_address", resolvedAddress },
            { "server_port", int.TryParse(resolvedPort, out int p) ? p : selectedServer.Port },
            { "error", false }
        });
    }

    private Task<IActionResult> HandleNewGameAvailable()
    {
        LogNewGameAvailableStub();
        return Task.FromResult<IActionResult>(new PhpResult(true));
    }
}
