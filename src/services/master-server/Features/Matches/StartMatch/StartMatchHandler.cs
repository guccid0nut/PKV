// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Logging;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain.Identifiers;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Matches.StartMatch;

public interface IStartMatchHandler
{
    Task<StartMatchResponse> HandleAsync(StartMatchCommand command, CancellationToken ct = default);
    Task<bool> AbandonMatchAsync(int matchId, string reason, string? session = null, CancellationToken ct = default);
    Task<int> ReclaimEmptyHostedLobbiesForPlayerAsync(string playerAccountName, int currentServerId, CancellationToken ct = default);
}

public sealed class StartMatchHandler(
    IDatabase distributedCache,
    ILogger<StartMatchHandler> logger,
    MerrickContext? databaseContext = null) : IStartMatchHandler
{
    private readonly StartMatchValidator _validator = new();

    public async Task<StartMatchResponse> HandleAsync(StartMatchCommand command, CancellationToken ct = default)
    {
        (bool isValid, string? errorMessage) = _validator.Validate(command);
        if (!isValid)
        {
            return StartMatchResponse.BadRequest(errorMessage ?? "Invalid match start parameters");
        }

        string disabledHeroList = ResolveDisabledHeroList(command);

        MatchServer? matchServer = await distributedCache.GetMatchServerBySessionCookie(command.Session!)
                                   ?? await distributedCache.ResurrectMatchServerBySession(command.Session!);
        if (matchServer == null)
        {
            return StartMatchResponse.Unauthorized("No Match Server Could Be Found For Provided Session Cookie");
        }

        if (matchServer.IsQueueArcadeServer() && command.MatchType == 0)
        {
            logger.LogWarning(
                "[StartMatchHandler] Blocked public custom game creation (MatchType=0, MatchName='{MatchName}', Host='{HostAccountName}') on Queue Arcade Server #{ServerID} (HostAccount='{ServerHostAccount}', Instance={Instance}, Port={Port}).",
                command.MatchName,
                command.HostAccountName,
                matchServer.ID,
                matchServer.HostAccountName,
                matchServer.Instance,
                matchServer.Port);
            return StartMatchResponse.Forbidden("Queue Arcade servers cannot host public custom games.");
        }

        // 1. Idempotency Check: prevent duplicate Match IDs if start_game is retransmitted for the same lobby
        string leaseKey = $"{matchServer.ID}:{command.Map}:{command.MatchName}";
        int? activeLeaseId = await distributedCache.GetActiveMatchLease(leaseKey);
        if (activeLeaseId.HasValue && activeLeaseId.Value > 0)
        {
            if (databaseContext != null)
            {
                MatchRecord? existingRecord = await databaseContext.MatchRecords
                    .FirstOrDefaultAsync(m => m.MatchID == activeLeaseId.Value, ct);

                if (existingRecord != null && (existingRecord.State == MatchLifecycleState.Lobby || existingRecord.State == MatchLifecycleState.Active))
                {
                    logger.LogInformation(
                        "[StartMatchHandler] Idempotency match lease hit: Server {ServerID} re-using Match #{MatchID} for map '{Map}'",
                        matchServer.ID, existingRecord.MatchID, command.Map);
                    return StartMatchResponse.Succeeded(new MatchId(existingRecord.MatchID), existingRecord.CreatedAt, disabledHeroList);
                }
            }
            else
            {
                logger.LogInformation(
                    "[StartMatchHandler] Idempotency match lease hit (in-memory): Server {ServerID} re-using Match #{MatchID} for map '{Map}'",
                    matchServer.ID, activeLeaseId.Value, command.Map);
                return StartMatchResponse.Succeeded(new MatchId(activeLeaseId.Value), TimeProvider.System.GetUtcNow(), disabledHeroList);
            }
        }

        // 2. Prune and tombstone any previous match associated with this server ID that didn't complete cleanly
        MatchStartData? previousMatch = await distributedCache.GetMatchStartDataByMatchServerID(matchServer.ID);
        if (previousMatch != null)
        {
            if (!string.IsNullOrWhiteSpace(previousMatch.ServerName))
            {
                matchServer.Name = previousMatch.ServerName;
            }

            await distributedCache.RemoveMatchStartData(previousMatch.MatchID);
            await distributedCache.KeyDeleteAsync($"MATCH-PLAYERS:[\"{previousMatch.MatchID}\"]");

            if (databaseContext != null)
            {
                MatchRecord? prevRecord = await databaseContext.MatchRecords
                    .FirstOrDefaultAsync(m => m.MatchID == previousMatch.MatchID, ct);

                if (prevRecord != null && (prevRecord.State == MatchLifecycleState.Lobby || prevRecord.State == MatchLifecycleState.Active))
                {
                    prevRecord.State = MatchLifecycleState.Abandoned;
                    prevRecord.TerminationReason = "SupercededByNewMatch";
                    prevRecord.EndedAt = TimeProvider.System.GetUtcNow();
                    await databaseContext.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "[StartMatchHandler] Tombstoned previous Match #{MatchID} on Server {ServerID} as Abandoned (Superceded)",
                        previousMatch.MatchID, matchServer.ID);
                }
            }
        }

        // 3. Monotonically generate authoritative Match ID
        int generatedId = await distributedCache.GenerateNextMatchID();
        MatchId matchId = new(generatedId);
        DateTimeOffset startedAt = TimeProvider.System.GetUtcNow();

        int serverPort = matchServer.Port;

        // 4. Persist authoritative MatchRecord to PostgreSQL database
        if (databaseContext != null)
        {
            MatchRecord matchRecord = new()
            {
                MatchID = matchId.Value,
                ServerID = matchServer.ID,
                ServerPort = serverPort,
                ServerName = command.MatchName ?? matchServer.Name,
                HostAccountName = command.HostAccountName!,
                Map = command.Map!,
                MatchMode = command.MatchMode!,
                IsCasual = command.IsCasual,
                MatchType = command.MatchType,
                State = MatchLifecycleState.Lobby,
                CreatedAt = startedAt
            };

            databaseContext.MatchRecords.Add(matchRecord);
            await databaseContext.SaveChangesAsync(ct);
        }

        // 5. Store match start data and lease in Redis cache
        MatchStartData matchStartData = new()
        {
            MatchID = matchId.Value,
            ServerID = matchServer.ID,
            ServerName = matchServer.Name,
            Map = command.Map!,
            Version = command.Version!,
            MatchName = command.MatchName!,
            HostAccountName = command.HostAccountName!,
            IsCasual = command.IsCasual,
            MatchType = command.MatchType,
            MatchMode = command.MatchMode!,
            TimestampStarted = startedAt
        };

        await distributedCache.SetMatchStartData(matchStartData);
        await distributedCache.SetActiveMatchLease(leaseKey, matchId.Value);

        matchServer.Status = ServerStatus.SERVER_STATUS_LOADING;
        matchServer.ConnectedPlayersCount = 0;
        matchServer.Description = string.Empty;
        matchServer.Name = command.MatchName!;
        await distributedCache.SetMatchServer(matchServer.HostAccountName ?? command.HostAccountName!, matchServer);

        if (command.MatchType == 0 &&
            !string.IsNullOrWhiteSpace(command.HostAccountName) &&
            !string.Equals(command.HostAccountName, "DedicatedHost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command.HostAccountName, matchServer.HostAccountName, StringComparison.OrdinalIgnoreCase))
        {
            await ReclaimEmptyHostedLobbiesForPlayerAsync(command.HostAccountName, matchServer.ID, ct);
        }

        logger.LogMatchStarted(matchStartData.MatchID, command.HostAccountName!, matchServer.ID, command.Map!);

        return StartMatchResponse.Succeeded(matchId, startedAt, disabledHeroList);
    }

    private static string ResolveDisabledHeroList(StartMatchCommand command)
    {
        // Gwynplaine is disabled for matchmaking games in FoC ranked normal, FoC quickmatch casual, and Midwars ranked.
        // He remains enabled in Practice matches, Bot matches (MatchType == 5 or MatchMode == "bm" / "botmatch"), and Public matches (MatchType == 0).
        bool isBotOrPractice = command.MatchType == 5 ||
            string.Equals(command.MatchMode, "bm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.MatchMode, "botmatch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.MatchMode, "practice", StringComparison.OrdinalIgnoreCase);

        if (isBotOrPractice || command.MatchType == 0)
        {
            return string.Empty;
        }

        string cleanMap = (command.Map ?? string.Empty).ToLowerInvariant();
        if (cleanMap.Contains("caldavar") || cleanMap.Contains("midwars"))
        {
            return "Hero_Gwynplaine";
        }

        return string.Empty;
    }

    public async Task<bool> AbandonMatchAsync(int matchId, string reason, string? session = null, CancellationToken ct = default)
    {
        MatchStartData? startData = await distributedCache.GetMatchStartData(matchId);
        MatchRecord? record = null;

        if (databaseContext != null)
        {
            record = await databaseContext.MatchRecords
                .FirstOrDefaultAsync(m => m.MatchID == matchId, ct);

            if (record != null && record.State != MatchLifecycleState.Completed)
            {
                record.State = record.State == MatchLifecycleState.Lobby
                    ? MatchLifecycleState.Cancelled
                    : MatchLifecycleState.Abandoned;
                record.TerminationReason = reason;
                record.EndedAt = TimeProvider.System.GetUtcNow();
                await databaseContext.SaveChangesAsync(ct);
                logger.LogInformation(
                    "[StartMatchHandler] Match #{MatchID} transitioned to {State} ({Reason})",
                    matchId, record.State, reason);
            }

            if (startData != null)
            {
                string leaseKey = $"{startData.ServerID}:{startData.Map}:{startData.MatchName}";
                await distributedCache.RemoveActiveMatchLease(leaseKey);
            }
            else if (record != null)
            {
                string leaseKey = $"{record.ServerID}:{record.Map}:{record.ServerName}";
                await distributedCache.RemoveActiveMatchLease(leaseKey);
            }
        }
        else if (startData != null)
        {
            string leaseKey = $"{startData.ServerID}:{startData.Map}:{startData.MatchName}";
            await distributedCache.RemoveActiveMatchLease(leaseKey);
        }

        await distributedCache.RemoveMatchStartData(matchId);
        await distributedCache.KeyDeleteAsync($"MATCH-PLAYERS:[\"{matchId}\"]");

        MatchServer? matchServer = null;
        if (!string.IsNullOrEmpty(session))
        {
            matchServer = await distributedCache.GetMatchServerBySessionCookie(session)
                          ?? await distributedCache.ResurrectMatchServerBySession(session);
        }

        int fallbackServerId = startData?.ServerID ?? record?.ServerID ?? 0;
        if (matchServer == null && fallbackServerId > 0)
        {
            matchServer = await distributedCache.GetMatchServerByID(fallbackServerId);
        }

        if (matchServer != null)
        {
            string previousName = matchServer.Name;
            matchServer.Status = ServerStatus.SERVER_STATUS_IDLE;
            matchServer.ConnectedPlayersCount = 0;
            matchServer.Description = string.Empty;
            if (!string.IsNullOrWhiteSpace(startData?.ServerName))
            {
                matchServer.Name = startData.ServerName;
            }

            await distributedCache.SetMatchServer(matchServer.HostAccountName, matchServer);
            logger.LogInformation(
                "[StartMatchHandler] Scrubbed abandoned lobby state on Server #{ServerID} (Match #{MatchID}, Reason='{Reason}', Name='{PreviousName}'->'{RestoredName}', Status=IDLE, Players=0).",
                matchServer.ID,
                matchId,
                reason,
                previousName,
                matchServer.Name);
        }

        return true;
    }

    public async Task<int> ReclaimEmptyHostedLobbiesForPlayerAsync(
        string playerAccountName,
        int currentServerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerAccountName) ||
            string.Equals(playerAccountName, "DedicatedHost", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        List<MatchServer> servers = await distributedCache.GetMatchServers();
        int reclaimedCount = 0;

        foreach (MatchServer candidate in servers)
        {
            if (candidate.ID == currentServerId ||
                candidate.IsQueueArcadeServer() ||
                candidate.ConnectedPlayersCount > 0 ||
                candidate.Status != ServerStatus.SERVER_STATUS_LOADING)
            {
                continue;
            }

            MatchStartData? staleStart = await distributedCache.GetMatchStartDataByMatchServerID(candidate.ID);
            if (staleStart == null ||
                staleStart.MatchType != 0 ||
                staleStart.ConnectedPlayersCount > 0 ||
                !string.Equals(staleStart.HostAccountName, playerAccountName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> connectedPlayers = await distributedCache.GetMatchConnectedPlayers(staleStart.MatchID);
            if (connectedPlayers.Count > 0)
            {
                continue;
            }

            logger.LogInformation(
                "[StartMatchHandler] Reclaiming abandoned 0/10 player-hosted lobby Match #{MatchID} ('{MatchName}') on Server #{StaleServerID} because host '{HostAccountName}' moved to Server #{TargetServerID}.",
                staleStart.MatchID,
                staleStart.MatchName,
                candidate.ID,
                playerAccountName,
                currentServerId);

            await AbandonMatchAsync(staleStart.MatchID, "HostMovedToAnotherServer", candidate.Cookie, ct);
            reclaimedCount++;
        }

        return reclaimedCount;
    }
}
