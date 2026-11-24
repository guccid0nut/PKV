// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LoopCanBeConvertedToQuery
// ReSharper disable PartOfLoopCanBeConvertedToQuery

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.Extensions.Cache;
using ASPIRE.Common.Models.ServerManagement;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Services.Replay;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

public class LiveMatchService : ILiveMatchService
{
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<int, LiveMatchData> _liveMatches = new();
    private readonly ILogger<LiveMatchService>? _logger;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly IServiceProvider? _serviceProvider;

    public LiveMatchService(
        IConfiguration? configuration = null,
        IHostEnvironment? hostEnvironment = null,
        IConnectionMultiplexer? multiplexer = null,
        IServiceProvider? serviceProvider = null,
        ILogger<LiveMatchService>? logger = null)
    {
        _multiplexer = multiplexer;
        _serviceProvider = serviceProvider;
        _logger = logger;

        if (_multiplexer is { IsConnected: true })
            try
            {
                ISubscriber subscriber = _multiplexer.GetSubscriber();
                subscriber.Subscribe(DistributedCacheExtensions.LiveMatchTelemetryChannel, (channel, message) =>
                {
                    if (message.IsNullOrEmpty) return;

                    try
                    {
                        byte[]? bytes = (byte[]?)message;
                        if (bytes == null) return;

                        LiveServerTickPayloadDto? payload =
                            JsonSerializer.Deserialize<LiveServerTickPayloadDto>(bytes, CaseInsensitiveJsonOptions);
                        if (payload is { MatchID: > 0 }) _ = ApplyServerTickAsync(payload);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[LiveMatchService] Error handling Redis PubSub live telemetry tick");
                    }
                });

                subscriber.Subscribe(RedisChannel.Literal("match:concluded"), (channel, message) =>
                {
                    if (message.IsNullOrEmpty) return;

                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse((string)message!);
                        if (doc.RootElement.TryGetProperty("MatchID", out JsonElement matchIdProp) &&
                            matchIdProp.TryGetInt32(out int matchId))
                            if (_liveMatches.TryRemove(matchId, out _))
                            {
                                if (_logger != null && _logger.IsEnabled(LogLevel.Information))
                                    _logger.LogInformation("[LiveMatchService] Instant conclusion for Match #{MatchId}",
                                        matchId);

                                if (_serviceProvider != null)
                                {
                                    using IServiceScope scope = _serviceProvider.CreateScope();
                                    IHubContext<LiveMatchesHub, ILiveMatchesClient>? hubContext =
                                        scope.ServiceProvider
                                            .GetService<IHubContext<LiveMatchesHub, ILiveMatchesClient>>();
                                    if (hubContext != null) _ = hubContext.Clients.All.OnMatchConcluded(matchId, 0);
                                }
                            }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[LiveMatchService] Error handling match:concluded event");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[LiveMatchService] Failed to subscribe to Redis live telemetry channel");
            }
    }

    public async Task<List<LiveMatchData>> GetActiveLiveMatchesAsync(CancellationToken cancellationToken = default)
    {
        await SyncRedisMatchesAsync(cancellationToken);

        List<LiveMatchData> list = [];
        List<LiveMatchData> sorted = [.. _liveMatches.Values.OrderByDescending(m => m.MatchID)];
        for (int i = 0; i < sorted.Count; i++)
        {
            LiveMatchData match = sorted[i];
            lock (match)
            {
                list.Add(CloneSnapshot(match));
            }
        }

        return list;
    }

    public async Task<LiveMatchData?> GetLiveMatchSnapshotAsync(int matchId,
        CancellationToken cancellationToken = default)
    {
        if (_liveMatches.TryGetValue(matchId, out LiveMatchData? existingMatch))
            lock (existingMatch)
            {
                return CloneSnapshot(existingMatch);
            }

        await SyncRedisMatchesAsync(cancellationToken);

        if (_liveMatches.TryGetValue(matchId, out LiveMatchData? match))
            lock (match)
            {
                return CloneSnapshot(match);
            }

        return null;
    }

    public async Task<int> PruneStaleMatchesAsync(CancellationToken cancellationToken = default)
    {
        int prunedCount = 0;
        if (_multiplexer is not { IsConnected: true }) return prunedCount;

        try
        {
            IDatabase db = _multiplexer.GetDatabase();
            EndPoint[] endpoints = _multiplexer.GetEndPoints();
            if (endpoints.Length == 0) return prunedCount;

            IServer server = _multiplexer.GetServer(endpoints[0]);
            foreach (RedisKey key in server.Keys(pattern: "MATCH-START-DATA:*"))
            {
                RedisValue cachedValue = await db.StringGetAsync(key);
                if (cachedValue.IsNullOrEmpty) continue;

                byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString());
                MatchStartData? matchStartData = JsonSerializer.Deserialize<MatchStartData>(bytes);
                if (matchStartData != null)
                {
                    int elapsedSeconds = (int)(TimeProvider.System.GetUtcNow() - matchStartData.TimestampStarted)
                        .TotalSeconds;
                    if (elapsedSeconds > 900) // Older than 15 minutes
                    {
                        await db.RemoveMatchStartData(matchStartData.MatchID);
                        _liveMatches.TryRemove(matchStartData.MatchID, out _);
                        prunedCount++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "[LiveMatchService] Error pruning stale matches.");
        }

        return prunedCount;
    }

    public Task<bool> ApplyServerTickAsync(LiveServerTickPayloadDto tick, CancellationToken cancellationToken = default)
    {
        if (tick is not { MatchID: > 0 }) return Task.FromResult(false);

        LiveMatchData match = _liveMatches.GetOrAdd(tick.MatchID,
            id => new LiveMatchData
            {
                MatchID = id,
                TickId = 1,
                ServerName = "Dedicated Game Server",
                Map = !string.IsNullOrEmpty(tick.Map) ? tick.Map : "midwars",
                GameMode = !string.IsNullOrEmpty(tick.GameMode) ? tick.GameMode : "Public Game",
                ElapsedSeconds = tick.ElapsedSeconds,
                Players = []
            });

        lock (match)
        {
            match.TickId++;
            match.LastUpdatedUtc = TimeProvider.System.GetUtcNow();
            match.ElapsedSeconds = tick.ElapsedSeconds;
            if (!string.IsNullOrEmpty(tick.Map)) match.Map = tick.Map;
            if (!string.IsNullOrEmpty(tick.GameMode)) match.GameMode = tick.GameMode;
            match.ScoreLegion = tick.ScoreLegion;
            match.ScoreHellbourne = tick.ScoreHellbourne;
            match.GoldAdvantageLegion = tick.GoldAdvantageLegion;
            match.GoldAdvantageHellbourne = tick.GoldAdvantageHellbourne;

            if (tick.Structures is { Count: > 0 }) match.Structures = new Dictionary<string, bool>(tick.Structures);

            match.ActiveWards = [.. tick.ActiveWards.Select(w => (LiveWardState)w)];

            if (tick.RecentEvents is { Count: > 0 })
                match.RecentEvents = [.. tick.RecentEvents.Select(e => (LiveCombatEvent)e)];

            if (tick.Players is { Count: > 0 })
                for (int i = 0; i < tick.Players.Count; i++)
                {
                    LivePlayerTelemetryDto playerTick = tick.Players[i];
                    LivePlayerState? p = match.Players.FirstOrDefault(x =>
                        x.AccountID == playerTick.AccountID || x.AccountName.Equals(playerTick.AccountName,
                            StringComparison.OrdinalIgnoreCase));
                    if (p == null)
                    {
                        string resolvedName = playerTick.AccountName is { Length: > 0 } pName &&
                                              !pName.StartsWith("Player #", StringComparison.OrdinalIgnoreCase)
                            ? playerTick.AccountName
                            : playerTick.AccountID == 1 || playerTick.Slot == 0
                                ? "guccidonut"
                                : $"Player #{playerTick.Slot + 1}";

                        p = new LivePlayerState
                        {
                            AccountID = playerTick.AccountID > 0 ? playerTick.AccountID : playerTick.Slot + 1,
                            AccountName = resolvedName,
                            Team = playerTick.Team
                        };
                        match.Players.Add(p);
                    }
                    else if (playerTick.AccountName is { Length: > 0 } pName &&
                             !pName.StartsWith("Player #", StringComparison.OrdinalIgnoreCase))
                    {
                        p.AccountName = playerTick.AccountName;
                    }

                    p.Team = playerTick.Team;
                    if (!string.IsNullOrEmpty(playerTick.HeroIdentifier))
                    {
                        p.HeroIdentifier = playerTick.HeroIdentifier;
                        p.HeroDisplayName = Heroes.ResolveHeroDisplayName(playerTick.HeroIdentifier);
                        (List<ReplayPlayerAbilityDto> calculatedAbilities, string? lastCast) =
                            HonReplayParser.CalculateSnapshotAbilities(
                                p.HeroIdentifier,
                                p.AccountID,
                                match.ElapsedSeconds,
                                p.HealthPercent,
                                []);
                        p.Abilities = calculatedAbilities;
                        p.LastCastAbility = playerTick.LastCastAbility ?? lastCast;
                    }

                    p.Level = Math.Max(1, playerTick.Level);
                    p.PositionX = Math.Clamp(playerTick.PositionX, 0.0f, 1.0f);
                    p.PositionY = Math.Clamp(playerTick.PositionY, 0.0f, 1.0f);
                    p.HeadingAngle = playerTick.HeadingAngle;
                    p.HealthPercent = Math.Clamp(playerTick.HealthPercent, 0, 100);
                    p.ManaPercent = Math.Clamp(playerTick.ManaPercent, 0, 100);
                    p.IsAlive = playerTick.IsAlive;
                    p.RespawnTimer = playerTick.RespawnTimer;
                    p.CurrentGold = playerTick.CurrentGold;
                    p.Kills = playerTick.Kills;
                    p.Deaths = playerTick.Deaths;
                    p.Assists = playerTick.Assists;
                    p.GPM = playerTick.GPM;
                    p.XPM = playerTick.XPM;
                    p.Inventory = [.. playerTick.Inventory];
                }
        }

        // Broadcast to SignalR Spectators
        if (_serviceProvider != null)
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            IHubContext<LiveMatchesHub, ILiveMatchesClient>? hubContext =
                scope.ServiceProvider.GetService<IHubContext<LiveMatchesHub, ILiveMatchesClient>>();
            if (hubContext != null)
            {
                string groupName = LiveMatchesHub.GetMatchGroupName(tick.MatchID);
                LiveMatchData snapshot = CloneSnapshot(match);

                _ = hubContext.Clients.Group(groupName).OnMatchSnapshot(snapshot);
                _ = hubContext.Clients.Group(LiveMatchesHub.OverviewGroupName).OnOverviewUpdated([snapshot]);
            }
        }

        return Task.FromResult(true);
    }

    public Task<bool> ApplyTelemetryReportAsync(LiveTelemetryReportDto report,
        CancellationToken cancellationToken = default)
    {
        if (report is not { MatchID: > 0 }) return Task.FromResult(false);

        LiveMatchData match = _liveMatches.GetOrAdd(report.MatchID,
            id => new LiveMatchData
            {
                MatchID = id,
                TickId = 1,
                ServerName = "Live Dedicated Server",
                Map = "midwars",
                GameMode = "Live Game",
                ElapsedSeconds = 0,
                Players = []
            });

        lock (match)
        {
            match.TickId++;

            // Find or create player
            LivePlayerState? player = match.Players.FirstOrDefault(p => p.AccountID == report.AccountID);
            if (player == null)
            {
                string cleanAccount = report.AccountName is { Length: > 0 } rName &&
                                      !rName.StartsWith("Player #", StringComparison.OrdinalIgnoreCase)
                    ? report.AccountName.Trim()
                    : report.AccountID == 1
                        ? "guccidonut"
                        : $"Player #{report.AccountID}";

                player = new LivePlayerState
                {
                    AccountID = report.AccountID,
                    AccountName = cleanAccount,
                    Team = report.Team > 0 ? report.Team : 1,
                    HeroIdentifier = string.Empty,
                    HeroDisplayName = "Picking Hero...",
                    Level = 1,
                    PositionX = 0.5f,
                    PositionY = 0.5f,
                    HealthPercent = 100,
                    ManaPercent = 100,
                    IsAlive = true
                };
                match.Players.Add(player);
            }

            if (report.AccountName is { Length: > 0 } repName &&
                !repName.StartsWith("Player #", StringComparison.OrdinalIgnoreCase))
                player.AccountName = report.AccountName.Trim();

            if (report.Team > 0) player.Team = report.Team;

            if (!string.IsNullOrWhiteSpace(report.HeroIdentifier))
            {
                string cleanHero = report.HeroIdentifier.Trim();
                player.HeroIdentifier = cleanHero;
                player.HeroDisplayName = Heroes.ResolveHeroDisplayName(cleanHero);
                player.Abilities = HonReplayParser.GetHeroBaseAbilities(cleanHero);
            }

            if (report.PositionX.HasValue) player.PositionX = Math.Clamp(report.PositionX.Value, 0.0f, 1.0f);

            if (report.PositionY.HasValue) player.PositionY = Math.Clamp(report.PositionY.Value, 0.0f, 1.0f);

            if (report.HeadingAngle.HasValue) player.HeadingAngle = report.HeadingAngle.Value;

            if (report.HealthPercent.HasValue) player.HealthPercent = Math.Clamp(report.HealthPercent.Value, 0, 100);

            if (report.ManaPercent.HasValue) player.ManaPercent = Math.Clamp(report.ManaPercent.Value, 0, 100);

            if (report.Level.HasValue) player.Level = Math.Max(1, report.Level.Value);

            if (report.CurrentGold.HasValue) player.CurrentGold = report.CurrentGold.Value;

            if (report.Kills.HasValue) player.Kills = report.Kills.Value;

            if (report.Deaths.HasValue) player.Deaths = report.Deaths.Value;

            if (report.Assists.HasValue) player.Assists = report.Assists.Value;

            if (report.GPM.HasValue) player.GPM = report.GPM.Value;

            if (report.XPM.HasValue) player.XPM = report.XPM.Value;

            if (report.Inventory != null) player.Inventory = report.Inventory;

            // Handle Wards
            if (report.Wards is { Count: > 0 })
                for (int wIdx = 0; wIdx < report.Wards.Count; wIdx++)
                {
                    LiveWardState ward = report.Wards[wIdx];
                    if (!match.ActiveWards.Any(w =>
                            Math.Abs(w.PositionX - ward.PositionX) < 0.01f &&
                            Math.Abs(w.PositionY - ward.PositionY) < 0.01f))
                        match.ActiveWards.Add(ward);
                }

            // Handle Combat Events
            if (!string.IsNullOrWhiteSpace(report.CombatEventDescription))
            {
                match.RecentEvents.Insert(0,
                    new LiveCombatEvent
                    {
                        EventType = "KILL",
                        AttackerName = player.AccountName,
                        AttackerHero = player.HeroIdentifier,
                        MatchTimeSeconds = match.ElapsedSeconds,
                        Description = report.CombatEventDescription
                    });
                if (match.RecentEvents.Count > 20) match.RecentEvents.RemoveAt(match.RecentEvents.Count - 1);
            }
        }

        // Broadcast to SignalR Spectators
        if (_serviceProvider != null)
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            IHubContext<LiveMatchesHub, ILiveMatchesClient>? hubContext =
                scope.ServiceProvider.GetService<IHubContext<LiveMatchesHub, ILiveMatchesClient>>();
            if (hubContext != null)
            {
                string groupName = LiveMatchesHub.GetMatchGroupName(report.MatchID);
                LiveMatchData snapshot = CloneSnapshot(match);

                _ = hubContext.Clients.Group(groupName).OnMatchSnapshot(snapshot);
                _ = hubContext.Clients.Group(LiveMatchesHub.OverviewGroupName).OnOverviewUpdated([snapshot]);
            }
        }

        return Task.FromResult(true);
    }

    public async Task SyncRedisMatchesAsync(CancellationToken cancellationToken = default)
    {
        if (_multiplexer is not { IsConnected: true }) return;

        try
        {
            IDatabase db = _multiplexer.GetDatabase();
            EndPoint[] endpoints = _multiplexer.GetEndPoints();
            if (endpoints.Length == 0) return;

            IServer server = _multiplexer.GetServer(endpoints[0]);
            HashSet<int> activeRedisMatchIds = [];
            List<MatchStartData> allRedisMatches = [];
            Dictionary<int, MatchStartData> latestMatchByServer = [];

            foreach (RedisKey key in server.Keys(pattern: "MATCH-START-DATA:*"))
            {
                RedisValue cachedValue = await db.StringGetAsync(key);
                if (cachedValue.IsNullOrEmpty) continue;

                byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString());
                MatchStartData? matchStartData = JsonSerializer.Deserialize<MatchStartData>(bytes);

                if (matchStartData is not null)
                {
                    allRedisMatches.Add(matchStartData);
                    if (!latestMatchByServer.TryGetValue(matchStartData.ServerID, out MatchStartData? existing) ||
                        matchStartData.MatchID > existing.MatchID)
                        latestMatchByServer[matchStartData.ServerID] = matchStartData;
                }
            }

            // Check if any matches have already been submitted to the database
            HashSet<int> submittedMatchIds = [];
            if (_serviceProvider != null)
            {
                using IServiceScope scope = _serviceProvider.CreateScope();
                IPlayerStatsApiClient? statsClient = scope.ServiceProvider.GetService<IPlayerStatsApiClient>();
                if (statsClient != null && allRedisMatches.Count > 0)
                {
                    List<int> candidateIds = [.. allRedisMatches.Select(m => m.MatchID)];
                    IReadOnlyList<int> dbMatches =
                        await statsClient.GetSubmittedMatchIdsAsync(candidateIds, cancellationToken);
                    submittedMatchIds = [.. dbMatches];
                }
            }

            // Find latest active match across all servers
            int globalLatestMatchId = allRedisMatches.Count > 0 ? allRedisMatches.Max(m => m.MatchID) : 0;

            for (int i = 0; i < allRedisMatches.Count; i++)
            {
                MatchStartData matchStartData = allRedisMatches[i];
                bool isReplaced =
                    latestMatchByServer.TryGetValue(matchStartData.ServerID, out MatchStartData? latest) &&
                    latest.MatchID > matchStartData.MatchID;
                bool isSubmitted = submittedMatchIds.Contains(matchStartData.MatchID);
                int elapsedSeconds =
                    (int)(TimeProvider.System.GetUtcNow() - matchStartData.TimestampStarted).TotalSeconds;

                // Auto-expire match if it's already in DB, replaced by newer match on server, older than 15 mins, or not the latest active match
                bool isOutdated = matchStartData.MatchID < globalLatestMatchId && elapsedSeconds > 60;
                if (isSubmitted || isReplaced || elapsedSeconds > 900 || isOutdated)
                {
                    await db.RemoveMatchStartData(matchStartData.MatchID);
                    _liveMatches.TryRemove(matchStartData.MatchID, out _);
                    if (_logger != null && _logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "[LiveMatchService] Auto-expired match #{MatchId} (Submitted: {IsSubmitted}, Replaced: {IsReplaced}, Elapsed: {Elapsed}s).",
                            matchStartData.MatchID, isSubmitted, isReplaced, elapsedSeconds);

                    continue;
                }

                activeRedisMatchIds.Add(matchStartData.MatchID);
                await UpdateOrCreateMatchFromRedisAsync(db, matchStartData, cancellationToken);
            }

            // Prune matches that have concluded and are no longer in Redis AND haven't received telemetry recently
            List<int> currentMatchIds = [.. _liveMatches.Keys];
            for (int i = 0; i < currentMatchIds.Count; i++)
            {
                int matchId = currentMatchIds[i];
                if (!activeRedisMatchIds.Contains(matchId))
                {
                    if (_liveMatches.TryGetValue(matchId, out LiveMatchData? match) &&
                        TimeProvider.System.GetUtcNow() - match.LastUpdatedUtc < TimeSpan.FromMinutes(15))
                        // Match is still active or receiving live telemetry/replay ticks, keep it
                        continue;

                    if (_liveMatches.TryRemove(matchId, out LiveMatchData? _))
                        if (_logger != null && _logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation(
                                "[LiveMatchService] Match #{MatchId} concluded. Removed from active live matches.",
                                matchId);
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "[LiveMatchService] Error synchronizing active matches from Redis.");
        }
    }

    private async Task UpdateOrCreateMatchFromRedisAsync(IDatabase db, MatchStartData data,
        CancellationToken cancellationToken = default)
    {
        int elapsed = Math.Max(0, (int)(TimeProvider.System.GetUtcNow() - data.TimestampStarted).TotalSeconds);
        List<string> connectedPlayers = await db.GetMatchConnectedPlayers(data.MatchID);
        LiveServerTickPayloadDto? serverTelemetry = await db.GetLiveMatchTelemetry(data.MatchID);

        string resolvedPlayerName = connectedPlayers.Count > 0
            ? connectedPlayers[0]
            : ResolveCleanAccountName(data.HostAccountName);

        bool isMidWars = data.Map.Contains("midwars", StringComparison.OrdinalIgnoreCase);
        float legionSpawnX = isMidWars ? 0.140f : 0.135f;
        float legionSpawnY = isMidWars ? 0.520f : 0.865f;
        float hellbourneSpawnX = isMidWars ? 0.740f : 0.865f;
        float hellbourneSpawnY = isMidWars ? 0.220f : 0.135f;

        if (_liveMatches.TryGetValue(data.MatchID, out LiveMatchData? existing))
        {
            lock (existing)
            {
                existing.ElapsedSeconds = serverTelemetry?.ElapsedSeconds ?? elapsed;
                if (!string.IsNullOrEmpty(data.ServerName)) existing.ServerName = data.ServerName;

                if (!string.IsNullOrEmpty(data.Map)) existing.Map = data.Map;

                if (!string.IsNullOrEmpty(data.MatchMode)) existing.GameMode = data.MatchMode;

                if (serverTelemetry != null)
                {
                    existing.ScoreLegion = serverTelemetry.ScoreLegion;
                    existing.ScoreHellbourne = serverTelemetry.ScoreHellbourne;
                    existing.GoldAdvantageLegion = serverTelemetry.GoldAdvantageLegion;
                    existing.GoldAdvantageHellbourne = serverTelemetry.GoldAdvantageHellbourne;
                    existing.ActiveWards = [.. serverTelemetry.ActiveWards.Select(w => (LiveWardState)w)];
                    if (serverTelemetry.Structures.Count > 0)
                        existing.Structures = new Dictionary<string, bool>(serverTelemetry.Structures);

                    if (serverTelemetry.RecentEvents.Count > 0)
                        existing.RecentEvents = [.. serverTelemetry.RecentEvents.Select(e => (LiveCombatEvent)e)];

                    if (serverTelemetry.Players.Count > 0)
                        for (int i = 0; i < serverTelemetry.Players.Count; i++)
                        {
                            LivePlayerTelemetryDto pt = serverTelemetry.Players[i];
                            LivePlayerState? p = existing.Players.FirstOrDefault(x =>
                                x.AccountID == pt.AccountID || x.AccountName.Equals(pt.AccountName,
                                    StringComparison.OrdinalIgnoreCase));
                            if (p != null)
                            {
                                if (pt.Team > 0) p.Team = pt.Team;

                                if (!string.IsNullOrEmpty(pt.HeroIdentifier))
                                {
                                    p.HeroIdentifier = pt.HeroIdentifier;
                                    p.HeroDisplayName = Heroes.ResolveHeroDisplayName(pt.HeroIdentifier);
                                    (List<ReplayPlayerAbilityDto> calculatedAbilities, string? lastCast) =
                                        HonReplayParser.CalculateSnapshotAbilities(
                                            p.HeroIdentifier,
                                            p.AccountID,
                                            existing.ElapsedSeconds,
                                            p.HealthPercent,
                                            []);
                                    p.Abilities = calculatedAbilities;
                                    p.LastCastAbility = pt.LastCastAbility ?? lastCast;
                                }
                                else if (string.IsNullOrEmpty(p.HeroIdentifier))
                                {
                                    p.HeroDisplayName = "Picking Hero...";
                                }

                                p.Level = Math.Max(1, pt.Level);
                                p.PositionX = Math.Clamp(pt.PositionX, 0.0f, 1.0f);
                                p.PositionY = Math.Clamp(pt.PositionY, 0.0f, 1.0f);
                                p.HeadingAngle = pt.HeadingAngle;
                                p.HealthPercent = Math.Clamp(pt.HealthPercent, 0, 100);
                                p.ManaPercent = Math.Clamp(pt.ManaPercent, 0, 100);
                                p.IsAlive = pt.IsAlive;
                                p.RespawnTimer = pt.RespawnTimer;
                                p.CurrentGold = pt.CurrentGold;
                                p.Kills = pt.Kills;
                                p.Deaths = pt.Deaths;
                                p.Assists = pt.Assists;
                                p.GPM = pt.GPM;
                                p.XPM = pt.XPM;
                                p.Inventory = [.. pt.Inventory];
                            }
                            else
                            {
                                string heroId = pt.HeroIdentifier;
                                existing.Players.Add(new LivePlayerState
                                {
                                    AccountID = pt.AccountID > 0 ? pt.AccountID : pt.Slot + 1,
                                    AccountName =
                                        !string.IsNullOrEmpty(pt.AccountName)
                                            ? pt.AccountName
                                            : $"Player #{pt.Slot + 1}",
                                    Team = pt.Team,
                                    HeroIdentifier = heroId,
                                    HeroDisplayName =
                                        !string.IsNullOrEmpty(heroId)
                                            ? Heroes.ResolveHeroDisplayName(heroId)
                                            : "Picking Hero...",
                                    Level = Math.Max(1, pt.Level),
                                    PositionX = pt.PositionX,
                                    PositionY = pt.PositionY,
                                    HeadingAngle = pt.HeadingAngle,
                                    HealthPercent = pt.HealthPercent,
                                    ManaPercent = pt.ManaPercent,
                                    IsAlive = pt.IsAlive,
                                    CurrentGold = pt.CurrentGold,
                                    Kills = pt.Kills,
                                    Deaths = pt.Deaths,
                                    Assists = pt.Assists,
                                    GPM = pt.GPM,
                                    XPM = pt.XPM,
                                    Inventory = [.. pt.Inventory],
                                    Abilities = !string.IsNullOrEmpty(heroId)
                                        ? HonReplayParser.GetHeroBaseAbilities(heroId)
                                        : []
                                });
                            }
                        }

                    if (serverTelemetry.Players.Count == 0 && connectedPlayers.Count > 0 && existing.Players.Count == 0)
                        for (int i = 0; i < connectedPlayers.Count; i++)
                        {
                            string pName = connectedPlayers[i];
                            int team = i % 2 + 1;
                            float spawnX = team == 2 ? hellbourneSpawnX : legionSpawnX;
                            float spawnY = team == 2 ? hellbourneSpawnY : legionSpawnY;
                            float spawnAngle = team == 2 ? -2.356f : 0.785f;

                            existing.Players.Add(new LivePlayerState
                            {
                                AccountID = i + 1,
                                AccountName = pName,
                                Team = team,
                                HeroIdentifier = string.Empty,
                                HeroDisplayName = "Picking Hero...",
                                Level = 1,
                                PositionX = spawnX,
                                PositionY = spawnY,
                                HeadingAngle = spawnAngle,
                                HealthPercent = 100,
                                ManaPercent = 100,
                                IsAlive = true,
                                CurrentGold = 0
                            });
                        }
                }
                else if (existing.Players.Count > 0 && !string.IsNullOrEmpty(resolvedPlayerName))
                {
                    existing.Players[0].AccountName = resolvedPlayerName;
                }
            }
        }
        else
        {
            List<LivePlayerState> initialPlayers = [];
            if (serverTelemetry is { Players.Count: > 0 })
                for (int i = 0; i < serverTelemetry.Players.Count; i++)
                {
                    LivePlayerTelemetryDto pt = serverTelemetry.Players[i];
                    string heroId = pt.HeroIdentifier;
                    initialPlayers.Add(new LivePlayerState
                    {
                        AccountID = pt.AccountID > 0 ? pt.AccountID : pt.Slot + 1,
                        AccountName =
                            !string.IsNullOrEmpty(pt.AccountName) ? pt.AccountName : $"Player #{pt.Slot + 1}",
                        Team = pt.Team,
                        HeroIdentifier = heroId,
                        HeroDisplayName =
                            !string.IsNullOrEmpty(heroId)
                                ? Heroes.ResolveHeroDisplayName(heroId)
                                : "Picking Hero...",
                        Level = Math.Max(1, pt.Level),
                        Kills = pt.Kills,
                        Deaths = pt.Deaths,
                        Assists = pt.Assists,
                        GPM = pt.GPM,
                        XPM = pt.XPM,
                        CurrentGold = pt.CurrentGold,
                        PositionX = pt.PositionX,
                        PositionY = pt.PositionY,
                        HeadingAngle = pt.HeadingAngle,
                        HealthPercent = pt.HealthPercent,
                        ManaPercent = pt.ManaPercent,
                        IsAlive = pt.IsAlive,
                        Inventory = [.. pt.Inventory],
                        Abilities = !string.IsNullOrEmpty(heroId)
                            ? HonReplayParser.GetHeroBaseAbilities(heroId)
                            : []
                    });
                }
            else if (connectedPlayers.Count > 0)
                for (int i = 0; i < connectedPlayers.Count; i++)
                {
                    string pName = connectedPlayers[i];
                    int team = i % 2 + 1;

                    initialPlayers.Add(new LivePlayerState
                    {
                        AccountID = i + 1,
                        AccountName = pName,
                        Team = team,
                        HeroIdentifier = string.Empty, // Hero selection in progress
                        HeroDisplayName = "Picking Hero...",
                        Level = 1,
                        Kills = 0,
                        Deaths = 0,
                        Assists = 0,
                        GPM = 0,
                        XPM = 0,
                        CurrentGold = 0,
                        PositionX = team == 1 ? legionSpawnX : hellbourneSpawnX,
                        PositionY = team == 1 ? legionSpawnY : hellbourneSpawnY,
                        HeadingAngle = team == 1 ? 0.785f : -2.356f,
                        HealthPercent = 100,
                        ManaPercent = 100,
                        IsAlive = true,
                        Inventory = [],
                        Abilities = []
                    });
                }

            LiveMatchData newMatch = new()
            {
                MatchID = data.MatchID,
                TickId = 1,
                ServerName = string.IsNullOrEmpty(data.ServerName) ? $"Server #{data.ServerID}" : data.ServerName,
                Map = string.IsNullOrEmpty(data.Map) ? "midwars" : data.Map,
                GameMode = string.IsNullOrEmpty(data.MatchMode) ? "Public Game" : data.MatchMode,
                ElapsedSeconds = serverTelemetry?.ElapsedSeconds ?? elapsed,
                ScoreLegion = serverTelemetry?.ScoreLegion ?? 0,
                ScoreHellbourne = serverTelemetry?.ScoreHellbourne ?? 0,
                GoldAdvantageLegion = serverTelemetry?.GoldAdvantageLegion ?? 0,
                GoldAdvantageHellbourne = serverTelemetry?.GoldAdvantageHellbourne ?? 0,
                Structures = serverTelemetry != null ? new Dictionary<string, bool>(serverTelemetry.Structures) : [],
                Objectives = new LiveObjectiveState
                {
                    KongorAlive = true,
                    KongorRespawnSecondsRemaining = 0,
                    TopRuneType = "Double Damage",
                    BotRuneType = "Haste"
                },
                ActiveWards = serverTelemetry != null
                    ? [.. serverTelemetry.ActiveWards.Select(w => (LiveWardState)w)]
                    : [],
                GoldTimeline =
                [
                    new LiveGoldTimelinePoint
                    {
                        Minute = 0,
                        Second = 0,
                        LegionNetWorth = 0,
                        HellbourneNetWorth = 0,
                        LegionTotalGold = 0,
                        HellbourneTotalGold = 0
                    }
                ],
                Players = initialPlayers,
                RecentEvents = serverTelemetry != null
                    ? [.. serverTelemetry.RecentEvents.Select(e => (LiveCombatEvent)e)]
                    : []
            };

            _liveMatches[data.MatchID] = newMatch;
            if (_logger != null && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[LiveMatchService] Discovered live match #{MatchId} ({MatchName}) on {ServerName} for player {PlayerName} on map {Map}",
                    data.MatchID, data.MatchName, data.ServerName, resolvedPlayerName, newMatch.Map);
        }

        // Broadcast current snapshot to any active spectators
        if (_liveMatches.TryGetValue(data.MatchID, out LiveMatchData? targetMatch) && _serviceProvider != null)
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            IHubContext<LiveMatchesHub, ILiveMatchesClient>? hubContext =
                scope.ServiceProvider.GetService<IHubContext<LiveMatchesHub, ILiveMatchesClient>>();
            if (hubContext != null)
            {
                string groupName = LiveMatchesHub.GetMatchGroupName(data.MatchID);
                LiveMatchData snapshot = CloneSnapshot(targetMatch);
                _ = hubContext.Clients.Group(groupName).OnMatchSnapshot(snapshot);
            }
        }
    }

    private static string ResolveCleanAccountName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "guccidonut";

        string clean = rawName.Trim();

        if (clean.Contains("-ROG-", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("DESKTOP-", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("LAPTOP-", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("HOST", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
            return "guccidonut";

        return clean;
    }

    public ConcurrentDictionary<int, LiveMatchData> GetAllMatchesInternal()
    {
        return _liveMatches;
    }

    private static LiveMatchData CloneSnapshot(LiveMatchData source)
    {
        LiveMatchData clone = new()
        {
            MatchID = source.MatchID,
            TickId = source.TickId,
            ServerName = source.ServerName,
            Map = source.Map,
            GameMode = source.GameMode,
            ElapsedSeconds = source.ElapsedSeconds,
            ScoreLegion = source.ScoreLegion,
            ScoreHellbourne = source.ScoreHellbourne,
            GoldAdvantageLegion = source.GoldAdvantageLegion,
            GoldAdvantageHellbourne = source.GoldAdvantageHellbourne,
            Structures = new Dictionary<string, bool>(source.Structures),
            RecentEvents = [.. source.RecentEvents],
            ActiveWards = [.. source.ActiveWards],
            GoldTimeline = [.. source.GoldTimeline],
            Objectives =
                new LiveObjectiveState
                {
                    KongorAlive = source.Objectives.KongorAlive,
                    KongorRespawnSecondsRemaining = source.Objectives.KongorRespawnSecondsRemaining,
                    TopRuneType = source.Objectives.TopRuneType,
                    BotRuneType = source.Objectives.BotRuneType
                },
            Players =
            [
                .. source.Players.Select(p => new LivePlayerState
                {
                    AccountID = p.AccountID,
                    AccountName = p.AccountName,
                    Team = p.Team,
                    HeroIdentifier = p.HeroIdentifier,
                    HeroDisplayName = p.HeroDisplayName,
                    Level = p.Level,
                    Kills = p.Kills,
                    Deaths = p.Deaths,
                    Assists = p.Assists,
                    GPM = p.GPM,
                    XPM = p.XPM,
                    CurrentGold = p.CurrentGold,
                    Inventory = [.. p.Inventory],
                    Abilities = [.. p.Abilities],
                    PositionX = p.PositionX,
                    PositionY = p.PositionY,
                    HeadingAngle = p.HeadingAngle,
                    LastCastAbility = p.LastCastAbility,
                    HealthPercent = p.HealthPercent,
                    ManaPercent = p.ManaPercent,
                    IsAlive = p.IsAlive,
                    RespawnTimer = p.RespawnTimer
                })
            ]
        };

        return clone;
    }
}
