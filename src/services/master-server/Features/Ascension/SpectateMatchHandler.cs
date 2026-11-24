// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Common.Models.ServerManagement;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Ascension;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Serialization;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Ascension;

/// <summary>
///     MediatR command handler for spectating an active match (<see cref="SpectateMatchCommand"/>).
///     Resolves match server directory, star player designation, and broadcasts hydration events via Redis PubSub.
/// </summary>
public sealed class SpectateMatchHandler(
    IConnectionMultiplexer redis,
    IAccountRepository? accountRepository = null,
    ILogger<SpectateMatchHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<SpectateMatchCommand, SpectateMatchResultDto>
{
    private readonly IDatabase _distributedCache = redis.GetDatabase();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SpectateMatchResultDto> Handle(
        SpectateMatchCommand request,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[{Timestamp}] [SpectateMatchHandler] Spectate match request: MatchId={MatchId}, SpectatorId={SpectatorId}",
            _timeProvider.GetUtcNow(),
            request.MatchId,
            request.SpectatorAccountId);

        int spectatorAccountId = request.SpectatorAccountId;
        string cookie = string.IsNullOrWhiteSpace(request.Cookie) ? "" : request.Cookie.Split('|')[0].Trim();
        string? spectatorName = null;

        if (!string.IsNullOrEmpty(cookie))
        {
            spectatorName = await _distributedCache.GetAccountNameForSessionCookie(cookie);
            if (spectatorAccountId == 0 && !string.IsNullOrEmpty(spectatorName) && accountRepository is not null)
            {
                Account? spectatorAcc = await accountRepository.GetByNicknameAsync(spectatorName, cancellationToken);
                if (spectatorAcc is not null)
                {
                    spectatorAccountId = spectatorAcc.ID;
                }
            }
        }

        MatchStartData? match = await _distributedCache.GetMatchStartData(request.MatchId);
        string serverAddress = "127.0.0.1:11235";
        string matchName = match?.MatchName ?? $"Match {request.MatchId}";

        if (match is not null)
        {
            MatchServer? matchServer = await _distributedCache.GetMatchServerByID(match.ServerID);
            if (matchServer is not null && !string.IsNullOrWhiteSpace(matchServer.IPAddress))
            {
                serverAddress = $"{matchServer.IPAddress}:{matchServer.Port}";
            }
        }

        List<string> connectedPlayers = await _distributedCache.GetMatchConnectedPlayers(request.MatchId);
        string starPlayerName = "";
        int starPlayerAccountId = 0;

        if (connectedPlayers.Count > 0 && accountRepository is not null)
        {
            IReadOnlyList<Account> accounts = await accountRepository.GetAccountsByNamesAsync(connectedPlayers, cancellationToken);
            if (accounts.Count > 0)
            {
                Account starAcc = accounts.OrderByDescending(a =>
                    a.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking)?.SkillRating
                    ?? a.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public)?.SkillRating
                    ?? 1500).First();
                starPlayerName = starAcc.Name;
                starPlayerAccountId = starAcc.ID;
            }
            else
            {
                starPlayerName = connectedPlayers[0];
            }
        }
        else if (connectedPlayers.Count > 0)
        {
            starPlayerName = connectedPlayers[0];
        }

        if (string.IsNullOrEmpty(starPlayerName))
        {
            starPlayerName = match?.HostAccountName ?? "HostPlayer";
        }

        if (starPlayerAccountId == 0 && !string.IsNullOrEmpty(starPlayerName) && accountRepository is not null)
        {
            Account? acc = await accountRepository.GetByNicknameAsync(starPlayerName, cancellationToken);
            if (acc is not null) starPlayerAccountId = acc.ID;
        }

        PlayerStatusHydrationEvent hydrationEvent = new(
            spectatorAccountId,
            starPlayerAccountId,
            request.MatchId,
            serverAddress,
            matchName,
            starPlayerName,
            spectatorName);

        try
        {
            ISubscriber subscriber = redis.GetSubscriber();
            await subscriber.PublishAsync(
                RedisChannel.Literal("ChatServer:HydratePlayerStatus"),
                JsonSerializer.Serialize(hydrationEvent, MasterServerJsonContext.Default.PlayerStatusHydrationEvent));

            logger?.LogInformation(
                "[{Timestamp}] [SpectateMatchHandler] Published ChatServer:HydratePlayerStatus for Spectator {SpectatorId} ({SpectatorName}), StarPlayer {StarName} ({StarId}), Match {MatchId} at {ServerAddress}",
                _timeProvider.GetUtcNow(),
                spectatorAccountId,
                spectatorName ?? "Unknown",
                starPlayerName,
                starPlayerAccountId,
                request.MatchId,
                serverAddress);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[SpectateMatchHandler] Failed to publish ChatServer:HydratePlayerStatus to Redis");
        }

        string matchVersion = match?.Version ?? "4.10.1.0";
        var responseData = new
        {
            result = 0,
            star_player = starPlayerName,
            match_id = request.MatchId,
            spectate_dir = serverAddress,
            match_version = matchVersion
        };

        return new SpectateMatchResultDto(0, starPlayerName, request.MatchId, serverAddress, matchVersion, responseData);
    }
}
