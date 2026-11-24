// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LoopCanBeConvertedToQuery
// ReSharper disable PartOfLoopCanBeConvertedToQuery

using System.Collections.Concurrent;
using ASPIRE.Common.Hosting;
using DAWNBRINGER.WebPortal.UI.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DAWNBRINGER.WebPortal.UI.Services;

public class LiveMatchSimulationWorker(
    LiveMatchService matchService,
    IHubContext<LiveMatchesHub, ILiveMatchesClient> hubContext,
    ILogger<LiveMatchSimulationWorker> logger) : ResilientBackgroundService(logger, nameof(LiveMatchSimulationWorker))
{
    private long _tickCounter = 1000;

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[LiveMatchWorker] Starting Real-Time Live Match Worker (Sync rate: 1.0s)");

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            try
            {
                await SimulateTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LiveMatchWorker] Error updating live match tick");
                await Task.Delay(2000, stoppingToken);
            }
    }

    private async Task SimulateTickAsync(CancellationToken stoppingToken = default)
    {
        long currentTick = Interlocked.Increment(ref _tickCounter);
        await matchService.SyncRedisMatchesAsync(stoppingToken);
        ConcurrentDictionary<int, LiveMatchData> allMatches = matchService.GetAllMatchesInternal();

        foreach (KeyValuePair<int, LiveMatchData> kvp in allMatches)
        {
            LiveMatchData match = kvp.Value;
            LiveMatchDeltaDto delta;

            lock (match)
            {
                match.ElapsedSeconds += 1;
                match.TickId = currentTick;

                delta = new LiveMatchDeltaDto
                {
                    MatchID = match.MatchID,
                    TickId = currentTick,
                    ElapsedSeconds = match.ElapsedSeconds,
                    ScoreLegion = match.ScoreLegion,
                    ScoreHellbourne = match.ScoreHellbourne
                };

                foreach (LivePlayerState player in match.Players) delta.PlayerDeltas.Add(CreatePlayerDelta(player));
            }

            string groupName = LiveMatchesHub.GetMatchGroupName(delta.MatchID);
            await hubContext.Clients.Group(groupName).OnMatchTick(delta);
        }

        List<LiveMatchData> activeList = await matchService.GetActiveLiveMatchesAsync(stoppingToken);
        await hubContext.Clients.Group(LiveMatchesHub.OverviewGroupName).OnOverviewUpdated(activeList);
    }

    private static LivePlayerDeltaState CreatePlayerDelta(LivePlayerState player)
    {
        return new LivePlayerDeltaState
        {
            AccountID = player.AccountID,
            Team = player.Team,
            PositionX = player.PositionX,
            PositionY = player.PositionY,
            HeadingAngle = player.HeadingAngle,
            Level = player.Level,
            CurrentGold = player.CurrentGold,
            HealthPercent = player.HealthPercent,
            ManaPercent = player.ManaPercent,
            IsAlive = player.IsAlive,
            RespawnTimer = player.RespawnTimer,
            LastCastAbility = player.LastCastAbility,
            Kills = player.Kills,
            Deaths = player.Deaths,
            Assists = player.Assists,
            GPM = player.GPM,
            XPM = player.XPM,
            Inventory = [.. player.Inventory]
        };
    }
}
