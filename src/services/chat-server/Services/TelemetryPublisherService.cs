// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Telemetry;
using MERRICK.DatabaseContext.Entities.Game;
using NEXUS.Contracts.CQRS.Telemetry;
using TRANSMUTANSTEIN.ChatServer.Serialization;
using RedisDatabase = StackExchange.Redis.IDatabase;

#pragma warning disable CA1848, CA2254
// ReSharper disable LogMessageIsConditional
// ReSharper disable LoggingMessageIsConditional
// ReSharper disable LogMessageIsConditionalWarning
// ReSharper disable StringInterpolationInLoggingIsExpensive
// ReSharper disable PassStringInterpolationToLog

namespace TRANSMUTANSTEIN.ChatServer.Services;

public partial class TelemetryPublisherService : ITelemetryPublisherService, IDisposable, IAsyncDisposable
{
    public const string TelemetryHashKey = PopulationBreakdownDefaults.TelemetryRedisHashKey;
    public const string TelemetryChannelKey = "TELEMETRY:EVENTS";
    public const string PopulationBreakdownField = PopulationBreakdownDefaults.PopulationBreakdownRedisField;

    private readonly record struct TelemetrySnapshot(
        int ConcurrentConnectedUsers,
        int ActiveMatchServers,
        int ActiveMatchmakingQueues,
        double AverageWaitTimeSeconds,
        string ActiveRegionsJson);

    private static readonly TimeSpan DefaultDatabaseSyncThrottle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultCommandFlushInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPopulationPublishInterval = TimeSpan.FromSeconds(5);

    private int _ccuCounter;
    private int _matchServerCounter;
    private readonly ConcurrentDictionary<int, string> _activeServerRegionsMap = new();

    private readonly Lock _stateLock = new();
    private readonly Lock _populationLock = new();
    private readonly SemaphoreSlim _dbSyncLock = new(1, 1);
    private readonly SemaphoreSlim _populationPublishLock = new(1, 1);
    private TelemetrySnapshot _desiredSnapshot = new(0, 0, 0, 0.0, "[]");
    private TelemetrySnapshot? _lastSyncedSnapshot;
    private bool _hasPublishedQueueState;
    private long _lastDatabaseSyncTimestamp;
    private string? _lastPopulationFingerprint;
    private long _lastPopulationPublishTimestamp;
    private readonly TimeSpan _databaseSyncThrottle;
    private readonly TimeSpan _commandFlushInterval;
    private readonly TimeSpan _populationPublishInterval;
    private readonly ITimer? _trailingFlushTimer;
    private readonly ITimer? _commandFlushTimer;
    private bool _trailingFlushScheduled;
    private int _trailingFlushInFlight;
    private int _commandFlushInFlight;
    private Task? _activeTrailingFlushTask;
    private Task? _activeCommandFlushTask;
    private long _pendingChatCommands;
    private int _disposed;

    private readonly RedisDatabase? _distributedCache;
    private readonly ILogger<TelemetryPublisherService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISubscriber? _subscriber;
    private readonly TimeProvider _timeProvider;

    public TelemetryPublisherService(
        IServiceProvider serviceProvider,
        ILogger<TelemetryPublisherService> logger,
        IConnectionMultiplexer? multiplexer = null,
        TimeProvider? timeProvider = null)
        : this(serviceProvider, logger, multiplexer, timeProvider, DefaultDatabaseSyncThrottle, DefaultCommandFlushInterval, DefaultPopulationPublishInterval)
    {
    }

    internal TelemetryPublisherService(
        IServiceProvider serviceProvider,
        ILogger<TelemetryPublisherService> logger,
        IConnectionMultiplexer? multiplexer,
        TimeProvider? timeProvider,
        TimeSpan databaseSyncThrottle,
        TimeSpan commandFlushInterval,
        TimeSpan? populationPublishInterval = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _databaseSyncThrottle = databaseSyncThrottle >= TimeSpan.Zero ? databaseSyncThrottle : DefaultDatabaseSyncThrottle;
        _commandFlushInterval = commandFlushInterval > TimeSpan.Zero ? commandFlushInterval : TimeSpan.Zero;
        _populationPublishInterval = populationPublishInterval is { } interval && interval >= TimeSpan.Zero
            ? interval
            : DefaultPopulationPublishInterval;

        if (multiplexer != null)
            try
            {
                _distributedCache = multiplexer.GetDatabase();
                _subscriber = multiplexer.GetSubscriber();
            }
            catch
            {
                _distributedCache = null;
                _subscriber = null;
            }

        _trailingFlushTimer = _timeProvider.CreateTimer(
            OnTrailingFlushTimerCallback,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        if (_commandFlushInterval > TimeSpan.Zero)
        {
            _commandFlushTimer = _timeProvider.CreateTimer(
                OnCommandFlushTimerCallback,
                null,
                _commandFlushInterval,
                _commandFlushInterval);
        }
    }

    public Guid InstanceId { get; } = Guid.NewGuid();

    internal long PendingChatCommands => Interlocked.Read(ref _pendingChatCommands);

    internal bool HasScheduledTrailingFlush
    {
        get
        {
            lock (_stateLock)
            {
                return _trailingFlushScheduled;
            }
        }
    }

    public async Task ResetBaselineOnStartupAsync()
    {
        _logger.LogInformation("[TelemetryPublisher] Resetting telemetry metrics baseline to 0 on server startup...");
        Interlocked.Exchange(ref _ccuCounter, 0);
        Interlocked.Exchange(ref _matchServerCounter, 0);
        Interlocked.Exchange(ref _pendingChatCommands, 0);
        _activeServerRegionsMap.Clear();

        const string emptyRegionsJson = "[]";
        TelemetrySnapshot baselineSnapshot = new(0, 0, 0, 0.0, emptyRegionsJson);
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        PopulationBreakdownResponseDto baselinePopulation = PopulationBreakdownAggregator.CreateEmpty(nowUtc, 0);
        string baselinePopulationJson = JsonSerializer.Serialize(
            baselinePopulation,
            ChatServerJsonContext.Default.PopulationBreakdownResponseDto);
        string baselinePopulationFingerprint = PopulationBreakdownAggregator.BuildContentFingerprint(baselinePopulation);

        lock (_stateLock)
        {
            _desiredSnapshot = baselineSnapshot;
            _lastSyncedSnapshot = null;
            _hasPublishedQueueState = true;
        }

        lock (_populationLock)
        {
            _lastPopulationFingerprint = baselinePopulationFingerprint;
            _lastPopulationPublishTimestamp = _timeProvider.GetTimestamp();
        }

        if (_distributedCache != null)
            try
            {
                await _distributedCache.HashSetAsync(TelemetryHashKey, [
                    new HashEntry("CCU", 0),
                    new HashEntry("ActiveMatchServers", 0),
                    new HashEntry("MatchesInProgress", 0),
                    new HashEntry("ActiveMatchmakingQueues", 0),
                    new HashEntry("AverageWaitTimeSeconds", 0.0),
                    new HashEntry("ActiveRegionsJson", emptyRegionsJson),
                    new HashEntry(PopulationBreakdownField, baselinePopulationJson)
                ]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error resetting Redis telemetry hash.");
            }

        try
        {
            await _dbSyncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (await PersistSnapshotToDatabaseAsync(baselineSnapshot).ConfigureAwait(false))
                {
                    lock (_stateLock)
                    {
                        if (_desiredSnapshot.Equals(baselineSnapshot))
                        {
                            _lastSyncedSnapshot = baselineSnapshot;
                        }
                        _lastDatabaseSyncTimestamp = _timeProvider.GetTimestamp();
                    }

                    _logger.LogInformation("[TelemetryPublisher] SQL Server SystemTelemetryMetrics baseline set to 0.");
                }
            }
            finally
            {
                _dbSyncLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TelemetryPublisher] Error persisting initial telemetry baseline to SQL DB.");
        }

        await PublishTelemetryEventAsync(new TelemetryEventMessage(
            TelemetryEventType.BaselineReset,
            InstanceId,
            Timestamp: _timeProvider.GetUtcNow()
        ));
    }

    public async Task OnClientConnectedAsync(string accountName)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[TelemetryPublisher] Game client connected: {AccountName}", accountName);

        int localCcu = Interlocked.Increment(ref _ccuCounter);

        long ccu = localCcu;
        if (_distributedCache != null)
            try
            {
                ccu = await _distributedCache.HashIncrementAsync(TelemetryHashKey, "CCU");
                if (ccu < 0)
                {
                    await _distributedCache.HashSetAsync(TelemetryHashKey, "CCU", 0);
                    ccu = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error incrementing CCU in Redis.");
            }

        lock (_stateLock)
        {
            _desiredSnapshot = _desiredSnapshot with { ConcurrentConnectedUsers = (int)ccu };
        }

        await FlushSnapshotIfDirtyAsync();

        await PublishTelemetryEventAsync(new TelemetryEventMessage(
            TelemetryEventType.ClientConnected,
            InstanceId,
            accountName,
            Timestamp: _timeProvider.GetUtcNow()
        ));
    }

    public async Task OnClientDisconnectedAsync(string accountName)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[TelemetryPublisher] Game client disconnected: {AccountName}", accountName);

        int localCcu = Math.Max(0, Interlocked.Decrement(ref _ccuCounter));

        long ccu = localCcu;
        if (_distributedCache != null)
            try
            {
                ccu = await _distributedCache.HashDecrementAsync(TelemetryHashKey, "CCU");
                if (ccu < 0)
                {
                    await _distributedCache.HashSetAsync(TelemetryHashKey, "CCU", 0);
                    ccu = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error decrementing CCU in Redis.");
            }

        lock (_stateLock)
        {
            _desiredSnapshot = _desiredSnapshot with { ConcurrentConnectedUsers = (int)ccu };
        }

        await FlushSnapshotIfDirtyAsync();

        await PublishTelemetryEventAsync(new TelemetryEventMessage(
            TelemetryEventType.ClientDisconnected,
            InstanceId,
            accountName,
            Timestamp: _timeProvider.GetUtcNow()
        ));
    }

    public async Task OnMatchServerConnectedAsync(int serverId, string location)
    {
        string normalizedRegion = NormalizeRegionName(location);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "[TelemetryPublisher] Match server node connected. ID: {ServerID}, Region: {Location} (Normalized: {Normalized})",
                serverId, location, normalizedRegion);

        _activeServerRegionsMap[serverId] = normalizedRegion;
        int localServers = Interlocked.Increment(ref _matchServerCounter);

        long serverCount = localServers;
        if (_distributedCache != null)
            try
            {
                serverCount = await _distributedCache.HashIncrementAsync(TelemetryHashKey, "ActiveMatchServers");
                if (serverCount < 0)
                {
                    await _distributedCache.HashSetAsync(TelemetryHashKey, "ActiveMatchServers", 0);
                    serverCount = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error incrementing Match Server count in Redis.");
            }

        List<string> uniqueRegions =
        [
            .. _activeServerRegionsMap.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r)
        ];
        string regionsJson = JsonSerializer.Serialize(uniqueRegions, ChatServerJsonContext.Default.ListString);

        if (_distributedCache != null)
            try
            {
                await _distributedCache.HashSetAsync(TelemetryHashKey, "ActiveRegionsJson", regionsJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error updating ActiveRegionsJson in Redis.");
            }

        lock (_stateLock)
        {
            _desiredSnapshot = _desiredSnapshot with
            {
                ActiveMatchServers = (int)serverCount,
                ActiveRegionsJson = regionsJson
            };
        }

        await FlushSnapshotIfDirtyAsync();

        await PublishTelemetryEventAsync(new TelemetryEventMessage(
            TelemetryEventType.MatchServerConnected,
            InstanceId,
            NumericId: serverId,
            Location: normalizedRegion,
            Timestamp: _timeProvider.GetUtcNow()
        ));
    }

    public async Task OnMatchServerDisconnectedAsync(int serverId, string location)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[TelemetryPublisher] Match server node disconnected. ID: {ServerID}", serverId);

        _activeServerRegionsMap.TryRemove(serverId, out _);
        int localServers = Math.Max(0, Interlocked.Decrement(ref _matchServerCounter));

        long serverCount = localServers;
        if (_distributedCache != null)
            try
            {
                serverCount = await _distributedCache.HashDecrementAsync(TelemetryHashKey, "ActiveMatchServers");
                if (serverCount < 0)
                {
                    await _distributedCache.HashSetAsync(TelemetryHashKey, "ActiveMatchServers", 0);
                    serverCount = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error decrementing Match Server count in Redis.");
            }

        List<string> uniqueRegions =
        [
            .. _activeServerRegionsMap.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r)
        ];
        string regionsJson = JsonSerializer.Serialize(uniqueRegions, ChatServerJsonContext.Default.ListString);

        if (_distributedCache != null)
            try
            {
                await _distributedCache.HashSetAsync(TelemetryHashKey, "ActiveRegionsJson", regionsJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error updating ActiveRegionsJson in Redis.");
            }

        lock (_stateLock)
        {
            _desiredSnapshot = _desiredSnapshot with
            {
                ActiveMatchServers = (int)serverCount,
                ActiveRegionsJson = regionsJson
            };
        }

        await FlushSnapshotIfDirtyAsync();

        await PublishTelemetryEventAsync(new TelemetryEventMessage(
            TelemetryEventType.MatchServerDisconnected,
            InstanceId,
            NumericId: serverId,
            Location: location,
            Timestamp: _timeProvider.GetUtcNow()
        ));
    }

    public async Task RecordQueueStateAsync(int activeQueuesCount)
    {
        bool queueChanged;
        lock (_stateLock)
        {
            queueChanged = !_hasPublishedQueueState || _desiredSnapshot.ActiveMatchmakingQueues != activeQueuesCount;
            _desiredSnapshot = _desiredSnapshot with { ActiveMatchmakingQueues = activeQueuesCount };
            if (queueChanged)
            {
                _hasPublishedQueueState = true;
            }
        }

        if (queueChanged)
        {
            if (_distributedCache != null)
                try
                {
                    await _distributedCache.HashSetAsync(TelemetryHashKey, "ActiveMatchmakingQueues", activeQueuesCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TelemetryPublisher] Error updating active matchmaking queues in Redis.");
                }

            await PublishTelemetryEventAsync(new TelemetryEventMessage(
                TelemetryEventType.QueueStateChanged,
                InstanceId,
                NumericId: activeQueuesCount,
                Timestamp: _timeProvider.GetUtcNow()
            ));
        }

        // Always evaluate the unified desired-state snapshot so dirty non-queue metrics
        // (e.g. CCU or ActiveMatchServers changed during throttle) are still flushed on time,
        // while truly unchanged idle ticks perform zero DB queries.
        await FlushSnapshotIfDirtyAsync();
    }

    public Task PublishPopulationBreakdownAsync(PopulationBreakdownResponseDto snapshot, bool forceImmediate = false)
    {
        if (snapshot is null || Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        int effectiveOnlinePlayers = Math.Max(snapshot.OnlinePlayers, Math.Max(0, Volatile.Read(ref _ccuCounter)));
        DateTimeOffset effectiveUpdatedAt = snapshot.UpdatedAtUtc == default
            ? _timeProvider.GetUtcNow()
            : snapshot.UpdatedAtUtc;

        PopulationBreakdownResponseDto effectiveSnapshot = effectiveOnlinePlayers == snapshot.OnlinePlayers &&
                                                           effectiveUpdatedAt == snapshot.UpdatedAtUtc
            ? snapshot
            : new PopulationBreakdownResponseDto
            {
                OnlinePlayers = effectiveOnlinePlayers,
                GamesInProgressTotal = snapshot.GamesInProgressTotal,
                GamesInProgressByRegion = snapshot.GamesInProgressByRegion,
                ServersAvailableByRegion = snapshot.ServersAvailableByRegion,
                TmmQueues = snapshot.TmmQueues,
                UpdatedAtUtc = effectiveUpdatedAt
            };

        string fingerprint = PopulationBreakdownAggregator.BuildContentFingerprint(effectiveSnapshot);
        long nowTimestamp = _timeProvider.GetTimestamp();

        bool countsChanged;
        bool intervalElapsed;
        lock (_populationLock)
        {
            countsChanged = !string.Equals(fingerprint, _lastPopulationFingerprint, StringComparison.Ordinal);
            intervalElapsed = _lastPopulationPublishTimestamp == 0 ||
                              _timeProvider.GetElapsedTime(_lastPopulationPublishTimestamp, nowTimestamp) >= _populationPublishInterval;

            if (!forceImmediate && !countsChanged && !intervalElapsed)
            {
                return Task.CompletedTask;
            }
        }

        return PublishPopulationBreakdownCoreAsync(effectiveSnapshot, fingerprint, forceImmediate);
    }

    private async Task PublishPopulationBreakdownCoreAsync(
        PopulationBreakdownResponseDto snapshot,
        string fingerprint,
        bool forceImmediate)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await _populationPublishLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            long nowTimestamp = _timeProvider.GetTimestamp();
            bool countsChanged;
            bool intervalElapsed;
            lock (_populationLock)
            {
                countsChanged = !string.Equals(fingerprint, _lastPopulationFingerprint, StringComparison.Ordinal);
                intervalElapsed = _lastPopulationPublishTimestamp == 0 ||
                                  _timeProvider.GetElapsedTime(_lastPopulationPublishTimestamp, nowTimestamp) >= _populationPublishInterval;

                if (!forceImmediate && !countsChanged && !intervalElapsed)
                {
                    return;
                }
            }

            string payloadJson = JsonSerializer.Serialize(
                snapshot,
                ChatServerJsonContext.Default.PopulationBreakdownResponseDto);

            bool publishedToRedis = false;
            if (_distributedCache != null)
            {
                try
                {
                    await _distributedCache.HashSetAsync(
                        TelemetryHashKey,
                        PopulationBreakdownField,
                        payloadJson).ConfigureAwait(false);
                    publishedToRedis = true;
                }
                catch (Exception ex)
                {
                    LogPopulationPublishError(ex);
                    return;
                }
            }

            lock (_populationLock)
            {
                _lastPopulationFingerprint = fingerprint;
                _lastPopulationPublishTimestamp = _timeProvider.GetTimestamp();
            }

            int availableServersTotal = snapshot.ServersAvailableByRegion.Sum(static r => r.Count);
            int totalQueuedPlayers = snapshot.TmmQueues.Sum(static q => q.TotalQueued);

            if (countsChanged || forceImmediate)
            {
                string reason = forceImmediate
                    ? (countsChanged ? "forced_change" : "forced")
                    : "counts_changed";
                string activeRegionsSummary = string.Join(", ", snapshot.GamesInProgressByRegion
                    .Where(static r => !string.Equals(r.Region, PopulationBreakdownDefaults.AllLocationsLabel, StringComparison.Ordinal))
                    .Select(static r => $"{r.Region}={r.Count}"));
                string availableRegionsSummary = string.Join(", ", snapshot.ServersAvailableByRegion
                    .Select(static r => $"{r.Region}={r.Count}"));
                string tmmSummary = string.Join("; ", snapshot.TmmQueues
                    .Select(static q => $"{q.Title}:{q.TotalQueued}"));

                LogPopulationCountsChanged(
                    reason,
                    publishedToRedis,
                    snapshot.OnlinePlayers,
                    snapshot.GamesInProgressTotal,
                    availableServersTotal,
                    totalQueuedPlayers,
                    activeRegionsSummary,
                    availableRegionsSummary,
                    tmmSummary);
            }
            else
            {
                LogPopulationHeartbeatRefreshed(
                    publishedToRedis,
                    snapshot.OnlinePlayers,
                    snapshot.GamesInProgressTotal,
                    availableServersTotal,
                    totalQueuedPlayers);
            }
        }
        finally
        {
            try
            {
                _populationPublishLock.Release();
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogTrace(ex, "[TelemetryPublisher][Population] Population publish lock already disposed during release.");
            }
        }
    }

    public async Task RecordMatchFoundWaitTimeAsync(string queueType, int playerCount, double waitSeconds)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "[TelemetryPublisher] Match formed in queue {QueueType} for {Count} players. Wait duration: {Wait}s",
                queueType, playerCount, waitSeconds);

        double newAvgWait = waitSeconds;
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            MerrickContext merrick = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            MatchmakingQueueWaitEntry entry = new()
            {
                QueueType = queueType,
                PlayerCount = playerCount,
                WaitDurationSeconds = waitSeconds,
                TimestampMatched = _timeProvider.GetUtcNow()
            };
            merrick.MatchmakingQueueWaitEntries.Add(entry);
            await merrick.SaveChangesAsync();

            // Calculate rolling average wait time across last 50 matches
            List<double> recentWaitTimes = await merrick.MatchmakingQueueWaitEntries
                .AsNoTracking()
                .OrderByDescending(e => e.ID)
                .Take(50)
                .Select(e => e.WaitDurationSeconds)
                .ToListAsync();

            if (recentWaitTimes.Count > 0) newAvgWait = recentWaitTimes.Average();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TelemetryPublisher] Error recording matchmaking queue wait entry in SQL DB.");
        }

        if (_distributedCache != null)
            try
            {
                await _distributedCache.HashSetAsync(TelemetryHashKey, "AverageWaitTimeSeconds", newAvgWait);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error updating average wait time in Redis.");
            }

        lock (_stateLock)
        {
            _desiredSnapshot = _desiredSnapshot with { AverageWaitTimeSeconds = newAvgWait };
        }

        await FlushSnapshotIfDirtyAsync();
    }

    public Task RecordChatCommandAsync()
    {
        Interlocked.Increment(ref _pendingChatCommands);
        return Task.CompletedTask;
    }

    public async Task OnMatchStartedAsync(int matchId)
    {
        if (_distributedCache != null)
            try
            {
                long matches = await _distributedCache.HashIncrementAsync(TelemetryHashKey, "MatchesInProgress");
                if (matches < 0) await _distributedCache.HashSetAsync(TelemetryHashKey, "MatchesInProgress", 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error incrementing MatchesInProgress in Redis.");
            }
    }

    public async Task OnMatchEndedAsync(int matchId)
    {
        if (_distributedCache != null)
            try
            {
                long matches = await _distributedCache.HashDecrementAsync(TelemetryHashKey, "MatchesInProgress");
                if (matches < 0) await _distributedCache.HashSetAsync(TelemetryHashKey, "MatchesInProgress", 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TelemetryPublisher] Error decrementing MatchesInProgress in Redis.");
            }
    }

    public static string NormalizeRegionName(string? location) =>
        PopulationBreakdownDefaults.NormalizeServerRegion(location);

    public void HandleRemoteTelemetryEvent(TelemetryEventMessage message)
    {
        if (message.PublisherInstanceId == InstanceId) return;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[TelemetryPublisher] Processing remote telemetry event {Type} from Instance {Id}",
                message.EventType, message.PublisherInstanceId);

        switch (message.EventType)
        {
            case TelemetryEventType.ClientConnected:
                Interlocked.Increment(ref _ccuCounter);
                break;
            case TelemetryEventType.ClientDisconnected:
                int ccuVal = Interlocked.Decrement(ref _ccuCounter);
                if (ccuVal < 0) Interlocked.CompareExchange(ref _ccuCounter, 0, ccuVal);

                break;
            case TelemetryEventType.MatchServerConnected:
                Interlocked.Increment(ref _matchServerCounter);
                if (message.NumericId > 0 && !string.IsNullOrWhiteSpace(message.Location))
                    _activeServerRegionsMap[message.NumericId] = message.Location;

                break;
            case TelemetryEventType.MatchServerDisconnected:
                int msVal = Interlocked.Decrement(ref _matchServerCounter);
                if (msVal < 0) Interlocked.CompareExchange(ref _matchServerCounter, 0, msVal);

                if (message.NumericId > 0) _activeServerRegionsMap.TryRemove(message.NumericId, out _);

                break;
            case TelemetryEventType.BaselineReset:
                Interlocked.Exchange(ref _ccuCounter, 0);
                Interlocked.Exchange(ref _matchServerCounter, 0);
                _activeServerRegionsMap.Clear();
                break;
        }
    }

    private async Task PublishTelemetryEventAsync(TelemetryEventMessage message)
    {
        NexusMetrics.TelemetryEventsPublished.Add(1);

        if (_subscriber != null)
            try
            {
                byte[] binaryPayload = MemoryPack.MemoryPackSerializer.Serialize(message);
                await _subscriber.PublishAsync(RedisChannel.Literal(TelemetryChannelKey), binaryPayload);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex,
                        "[TelemetryPublisher] Failed publishing event to Redis Pub/Sub channel {Channel}",
                        TelemetryChannelKey);
            }
    }

    internal async Task FlushSnapshotIfDirtyAsync(bool force = false, bool allowAfterDispose = false)
    {
        if (!allowAfterDispose && Volatile.Read(ref _disposed) != 0) return;

        lock (_stateLock)
        {
            if (_lastSyncedSnapshot.HasValue && _desiredSnapshot.Equals(_lastSyncedSnapshot.Value))
            {
                return;
            }
        }

        try
        {
            await _dbSyncLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!allowAfterDispose && Volatile.Read(ref _disposed) != 0) return;

            TelemetrySnapshot desiredSnapshot;
            long now = _timeProvider.GetTimestamp();

            lock (_stateLock)
            {
                if (_lastSyncedSnapshot.HasValue && _desiredSnapshot.Equals(_lastSyncedSnapshot.Value))
                {
                    return;
                }

                if (!force && _lastDatabaseSyncTimestamp != 0)
                {
                    TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastDatabaseSyncTimestamp, now);
                    if (elapsed < _databaseSyncThrottle)
                    {
                        ScheduleTrailingFlushLocked(_databaseSyncThrottle - elapsed);
                        return;
                    }
                }

                desiredSnapshot = _desiredSnapshot;
            }

            if (!await PersistSnapshotToDatabaseAsync(desiredSnapshot).ConfigureAwait(false))
            {
                return;
            }

            lock (_stateLock)
            {
                _lastSyncedSnapshot = desiredSnapshot;
                _lastDatabaseSyncTimestamp = _timeProvider.GetTimestamp();

                if (!_desiredSnapshot.Equals(desiredSnapshot) && Volatile.Read(ref _disposed) == 0)
                {
                    ScheduleTrailingFlushLocked(_databaseSyncThrottle);
                }
            }
        }
        finally
        {
            try
            {
                _dbSyncLock.Release();
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogTrace(ex, "[TelemetryPublisher] DB sync lock already disposed during release.");
            }
        }
    }

    internal async Task FlushPendingChatCommandsAsync(bool allowAfterDispose = false)
    {
        if (!allowAfterDispose && Volatile.Read(ref _disposed) != 0) return;

        long delta = Interlocked.Exchange(ref _pendingChatCommands, 0);
        if (delta <= 0) return;

        if (_distributedCache == null) return;

        try
        {
            await _distributedCache.HashIncrementAsync(TelemetryHashKey, "ChatCommandsTotal", delta).ConfigureAwait(false);
            long mem = Process.GetCurrentProcess().WorkingSet64;
            await _distributedCache.HashSetAsync(TelemetryHashKey, "MEM_TRANSMUTANSTEIN", mem).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "[TelemetryPublisher] Deferred command count flush failed; restoring {Delta} pending commands.", delta);
            if (Volatile.Read(ref _disposed) == 0)
            {
                Interlocked.Add(ref _pendingChatCommands, delta);
            }
        }
    }

    private void ScheduleTrailingFlushLocked(TimeSpan delay)
    {
        if (_trailingFlushScheduled || _trailingFlushTimer is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        TimeSpan clampedDelay = delay > TimeSpan.FromMilliseconds(1) ? delay : TimeSpan.FromMilliseconds(1);
        _trailingFlushScheduled = true;
        try
        {
            _trailingFlushTimer.Change(clampedDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            _trailingFlushScheduled = false;
        }
    }

    private void OnTrailingFlushTimerCallback(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _trailingFlushInFlight, 1) != 0) return;

        _activeTrailingFlushTask = Task.Run(async () =>
        {
            try
            {
                lock (_stateLock)
                {
                    _trailingFlushScheduled = false;
                }

                await FlushSnapshotIfDirtyAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TelemetryPublisher] Trailing-edge telemetry flush failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _trailingFlushInFlight, 0);
            }
        });
    }

    private void OnCommandFlushTimerCallback(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _commandFlushInFlight, 1) != 0) return;

        _activeCommandFlushTask = Task.Run(async () =>
        {
            try
            {
                await FlushPendingChatCommandsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "[TelemetryPublisher] Periodic command batch flush failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _commandFlushInFlight, 0);
            }
        });
    }

    private async Task<bool> PersistSnapshotToDatabaseAsync(TelemetrySnapshot snapshot)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            MerrickContext merrick = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            SystemTelemetryMetrics? metrics = await merrick.SystemTelemetryMetrics.FirstOrDefaultAsync(m => m.ID == 1);
            if (metrics == null)
            {
                metrics = new SystemTelemetryMetrics();
                merrick.SystemTelemetryMetrics.Add(metrics);
            }

            metrics.ConcurrentConnectedUsers = snapshot.ConcurrentConnectedUsers;
            metrics.ActiveMatchServers = snapshot.ActiveMatchServers;
            metrics.ActiveMatchmakingQueues = snapshot.ActiveMatchmakingQueues;
            metrics.AverageWaitTimeSeconds = snapshot.AverageWaitTimeSeconds;
            metrics.ActiveRegionsJson = string.IsNullOrWhiteSpace(snapshot.ActiveRegionsJson) ? "[]" : snapshot.ActiveRegionsJson;
            metrics.TimestampLastUpdated = _timeProvider.GetUtcNow();

            await merrick.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TelemetryPublisher] Failed syncing SystemTelemetryMetrics record in SQL DB.");
            return false;
        }
    }

    private async Task AwaitInFlightTimersAsync()
    {
        Task? trailing = _activeTrailingFlushTask;
        if (trailing is not null)
        {
            try
            {
                await trailing.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "[TelemetryPublisher] In-flight trailing flush task faulted during shutdown.");
            }
        }

        Task? cmdFlush = _activeCommandFlushTask;
        if (cmdFlush is not null)
        {
            try
            {
                await cmdFlush.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "[TelemetryPublisher] In-flight command flush task faulted during shutdown.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_trailingFlushTimer is not null)
        {
            await _trailingFlushTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_commandFlushTimer is not null)
        {
            await _commandFlushTimer.DisposeAsync().ConfigureAwait(false);
        }

        await AwaitInFlightTimersAsync().ConfigureAwait(false);
        await FlushSnapshotIfDirtyAsync(force: true, allowAfterDispose: true).ConfigureAwait(false);
        await FlushPendingChatCommandsAsync(allowAfterDispose: true).ConfigureAwait(false);

        _dbSyncLock.Dispose();
        _populationPublishLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _trailingFlushTimer?.Dispose();
        _commandFlushTimer?.Dispose();

        try
        {
            if (_dbSyncLock.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    TelemetrySnapshot? dirtySnapshot = null;
                    lock (_stateLock)
                    {
                        if (!_lastSyncedSnapshot.HasValue || !_desiredSnapshot.Equals(_lastSyncedSnapshot.Value))
                        {
                            dirtySnapshot = _desiredSnapshot;
                        }
                    }

                    if (dirtySnapshot.HasValue)
                    {
                        using IServiceScope scope = _serviceProvider.CreateScope();
                        MerrickContext merrick = scope.ServiceProvider.GetRequiredService<MerrickContext>();
                        SystemTelemetryMetrics? metrics = merrick.SystemTelemetryMetrics.FirstOrDefault(m => m.ID == 1);
                        if (metrics == null)
                        {
                            metrics = new SystemTelemetryMetrics();
                            merrick.SystemTelemetryMetrics.Add(metrics);
                        }

                        metrics.ConcurrentConnectedUsers = dirtySnapshot.Value.ConcurrentConnectedUsers;
                        metrics.ActiveMatchServers = dirtySnapshot.Value.ActiveMatchServers;
                        metrics.ActiveMatchmakingQueues = dirtySnapshot.Value.ActiveMatchmakingQueues;
                        metrics.AverageWaitTimeSeconds = dirtySnapshot.Value.AverageWaitTimeSeconds;
                        metrics.ActiveRegionsJson = string.IsNullOrWhiteSpace(dirtySnapshot.Value.ActiveRegionsJson)
                            ? "[]"
                            : dirtySnapshot.Value.ActiveRegionsJson;
                        metrics.TimestampLastUpdated = _timeProvider.GetUtcNow();

                        merrick.SaveChanges();

                        lock (_stateLock)
                        {
                            _lastSyncedSnapshot = dirtySnapshot.Value;
                            _lastDatabaseSyncTimestamp = _timeProvider.GetTimestamp();
                        }
                    }
                }
                finally
                {
                    _dbSyncLock.Release();
                }
            }

            long pendingCommands = Interlocked.Exchange(ref _pendingChatCommands, 0);
            if (pendingCommands > 0 && _distributedCache != null)
            {
                _distributedCache.HashIncrement(TelemetryHashKey, "ChatCommandsTotal", pendingCommands);
                long mem = Process.GetCurrentProcess().WorkingSet64;
                _distributedCache.HashSet(TelemetryHashKey, "MEM_TRANSMUTANSTEIN", mem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "[TelemetryPublisher] Best-effort synchronous Dispose flush encountered an exception.");
        }

        _dbSyncLock.Dispose();
        _populationPublishLock.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[TelemetryPublisher][Population] Published snapshot (Reason={Reason}, RedisWritten={RedisWritten}, OnlinePlayers={OnlinePlayers}, GamesInProgress={GamesInProgressTotal}, AvailableServers={AvailableServersTotal}, QueuedPlayers={TotalQueuedPlayers}, ActiveRegions=[{ActiveRegions}], AvailableRegions=[{AvailableRegions}], TmmQueues=[{TmmQueues}]).")]
    private partial void LogPopulationCountsChanged(
        string reason,
        bool redisWritten,
        int onlinePlayers,
        int gamesInProgressTotal,
        int availableServersTotal,
        int totalQueuedPlayers,
        string activeRegions,
        string availableRegions,
        string tmmQueues);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "[TelemetryPublisher][Population] Refreshed unchanged 5s heartbeat snapshot (RedisWritten={RedisWritten}, OnlinePlayers={OnlinePlayers}, GamesInProgress={GamesInProgressTotal}, AvailableServers={AvailableServersTotal}, QueuedPlayers={TotalQueuedPlayers}).")]
    private partial void LogPopulationHeartbeatRefreshed(
        bool redisWritten,
        int onlinePlayers,
        int gamesInProgressTotal,
        int availableServersTotal,
        int totalQueuedPlayers);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "[TelemetryPublisher][Population] Failed publishing PopulationBreakdownJson snapshot to Redis.")]
    private partial void LogPopulationPublishError(Exception ex);
}
