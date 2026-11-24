// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors

using ASPIRE.Common.Hosting;
using MERRICK.DatabaseContext.Extensions;

namespace TRANSMUTANSTEIN.ChatServer.Services;

/// <summary>
///     Orchestrates the "While You Wait" Queue Mini-Game (Queue Arcade) fleet:
///     1. Keeps dedicated Queue Arcade servers continuously rotating evenly through a unified Fisher-Yates
///        shuffle-bag of 5v5, Party/Arcade, and 10v10 maps (with bots filling all slots until real players swap in).
///     2. Hot-pushes opted-in queued matchmaking groups into ongoing Arcade matches (preserving party cohesion
///        and Ranked queue timers) when gated past Wasted-Load Queue-ETA checks.
///     3. Hot-pulls players out of Arcade matches with a 3-second pre-transfer alert when Ranked pops,
///        or cleanly releases slots when players voluntarily disconnect while staying in queue.
/// </summary>
internal sealed class QueueArcadeCoordinatorService : ResilientBackgroundService, IQueueArcadeService
{
    public static readonly string[] UnifiedArcadeMapPool =
    [
        // Classic & Fast-Paced Lanes (5v5)
        "midwars",
        "caldavar_wtf",
        "grimmscrossing",
        "thegrimmhunt",

        // Party & Arcade Modes (5v5)
        "soccer",
        "capturetheflag",
        "team_deathmatch",
        "riftwars",
        "devowars",
        "archers",
        "valkwars",
        "pebbles_duel",
        "arena",

        // 10v10 Chaos Brawls
        "midwars_10v10",
        "devowars_10v10",
        "truedevo_10v10"
    ];

    public static readonly string[] BotSafeArcadeMapPool =
    [
        // Classic & Fast-Paced Lanes (5v5)
        "midwars",
        "caldavar_wtf",
        "grimmscrossing",
        "thegrimmhunt",

        // Bot-Compatible Brawl & Sports Modes (5v5)
        "soccer",
        "team_deathmatch",
        "devowars",

        // Bot-Compatible 10v10 Chaos Brawls
        "midwars_10v10",
        "devowars_10v10"
    ];

    private static readonly HashSet<string> BotActiveStandardMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "caldavar_wtf",
        "midwars",
        "midwars_10v10",
        "grimmscrossing",
        "soccer",
        "devowars",
        "devowars_10v10",
        "team_deathmatch",
        "thegrimmhunt"
    };

    // Verified .bot XML definitions in games/server/override/bots/definitions/*.bot (100/100 A-Z audited).
    // Spawned post-LoadWorld via AddBot console commands inside ConfirmArcadeMatchReady (after CTeamInfo 1 & 2 exist).
    public static readonly string[] ArcadeBotPool =
    [
        "AccursedBot",
        "AdrenalineBot",
        "AndromedaBot",
        "ApexBot",
        "ArtesiaBot",
        "ArtilleryBot",
        "BehemothBot",
        "BerzerkerBot",
        "BlitzBot",
        "BubblesBot",
        "BushwackBot",
        "CalamityBot",
        "ChiBot",
        "ChipperBot",
        "CirceBot",
        "CorruptedDiscipleBot",
        "CthulhuphantBot",
        "DeadliftBot",
        "DeadwoodBot",
        "DevourerBot",
        "DreadknightBot",
        "DrunkenMasterBot",
        "EbulusBot",
        "ElectricianBot",
        "EmeraldWardenBot",
        "EmpathBot",
        "FadeBot",
        "FairyBot",
        "FlameDragonBot",
        "FluxBot",
        "GauntletBot",
        "GeminiBot",
        "GeomancerBot",
        "GladiatorBot",
        "GoldenveilBot",
        "GravekeeperBot",
        "GrinexBot",
        "GwynplaineBot",
        "HantumonBot",
        "HellbringerBot",
        "HiroBot",
        "IchorBot",
        "JereziahBot",
        "KaneBot",
        "KenisisBot",
        "KingKloutBot",
        "KlanxBot",
        "KrakenBot",
        "KunasBot",
        "LodestoneBot",
        "MartyrBot",
        "MasterOfArmsBot",
        "MimixBot",
        "MoiraBot",
        "MonarchBot",
        "MonkeyKingBot",
        "MoraxusBot",
        "MumraBot",
        "NitroBot",
        "NomadBot",
        "OogieBot",
        "OpheliaBot",
        "ParallaxBot",
        "PearlBot",
        "PestilenceBot",
        "PlantBot",
        "PollywogPriestBot",
        "ProphetBot",
        "PuppetMasterBot",
        "RallyBot",
        "RampageBot",
        "RhapBot",
        "RiftmageBot",
        "RiptideBot",
        "RockyBot",
        "SalomonBot",
        "SandWraithBot",
        "SapphireBot",
        "ScarBot",
        "ScoutBot",
        "ShellshockBot",
        "SilhouetteBot",
        "SirBenzingtonBot",
        "SkrapBot",
        "SolsticeBot",
        "SoulstealerBot",
        "TarotBot",
        "TempestBot",
        "TreantBot",
        "TrembleBot",
        "TundraBot",
        "ValkyrieBot",
        "VanyaBot",
        "VindicatorBot",
        "VoodooBot",
        "WarchiefBot",
        "WolfManBot",
        "XalynxBot",
        "YogiBot",
        "ZephyrBot"
    ];

    public static readonly string[] RlSelfPlayMapPool =
    [
        "midwars",
        "grimmscrossing",
        "thegrimmhunt",
    ];

    public const string RedisEnabledKey = "kongor:queue_arcade:enabled";
    public const string RedisIdleModeKey = "kongor:queue_arcade:idle_mode";
    public const string RedisRoundDurationSecondsKey = "kongor:queue_arcade:round_duration_seconds";
    public const string RedisVictoryHoldSecondsKey = "kongor:queue_arcade:victory_hold_seconds";
    public const string EndArcadeMatchCommand = "StartPostMatch 1 false true";
    public static readonly TimeSpan ArcadeAllowTicketTtl = TimeSpan.FromMinutes(15);

    private static int _nextArcadeMatchUpId = -10000;

    private readonly IConfiguration _configuration;
    private readonly IChatContext _chatContext;
    private readonly IMatchmakingService _matchmakingService;
    private readonly StackExchange.Redis.IDatabase? _distributedCache;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly Lock _stateLock = new();

    private DateTimeOffset? _lastRedisEnabledPollUtc;
    private bool? _cachedRedisEnabledOverride;
    private ArcadeIdleMode? _cachedRedisIdleMode;
    private int? _cachedRedisRoundDurationSeconds;
    private double? _cachedRedisVictoryHoldSeconds;
    private int? _cachedRedisPlayersPerTeamOverride;

    private readonly Dictionary<int, ArcadeMatchRuntimeState> _arcadeByServerId = [];
    private readonly Dictionary<int, int> _serverIdByMatchUpId = [];
    private readonly Dictionary<int, int> _lastEndedMatchUpIdByServerId = [];
    private readonly Dictionary<int, DateTimeOffset> _postMatchSettleRetryAfterUtc = [];
    private readonly Dictionary<int, int> _postMatchSettleAttempts = [];
    private readonly Dictionary<int, DateTimeOffset> _postMatchVictoryHoldUntilUtc = [];
    private readonly Dictionary<int, DateTimeOffset> _loadingTimeoutBackoffUntilUtc = [];
    private readonly Dictionary<int, DateTimeOffset> _lastCookieRegisteredAtByServerId = [];
    private readonly Dictionary<int, ArcadeIdleMode> _lastHibernateLoggedByServerId = [];
    private readonly List<string> _shuffleBag = [];
    private readonly List<string> _selfPlayShuffleBag = [];
    private readonly List<string> _rotationHistory = [];
    private string? _lastSelectedMap;
    private string? _lastSelectedSelfPlayMap;
    private bool? _lastIncludeCustomPartyMaps;
    private int _nextSelfPlayCandidateTeam = 1;

    private long _matchesStartedTotal;
    private long _matchesCompletedTotal;
    private long _roundTimerRotationsTotal;
    private long _playersPushedTotal;
    private long _wastedLoadBlocksTotal;
    private long _prePullWarningsSentTotal;
    private long _rankedPullsTotal;
    private long _manualLeavesTotal;
    private long _midLoadPullOverridesTotal;
    private long _regionMismatchSkipsTotal;
    private long _warmupDeferralsTotal;
    private long _botReadinessFallbackTimeoutsTotal;
    private long _inflightOrCooldownSkipsTotal;
    private long _circuitBreakerTripsTotal;
    private long _pendingJoinRetriesTotal;
    private long _pendingJoinTimeoutsTotal;
    private long _loadingTimeoutQuarantinesTotal;
    private long _victoryAutoPullsTotal;
    private long _victoryKickFallbacksTotal;
    private long _hibernateHoldsTotal;
    private long _selfPlayMatchesStartedTotal;
    private int _consecutiveRapidServerDisconnects;
    private DateTimeOffset? _rapidDisconnectBackoffUntilUtc;

    private sealed class ArcadeMatchRuntimeState
    {
        public required int ServerId { get; init; }
        public required int MatchUpId { get; init; }
        public int MatchId { get; set; } = -1;
        public required string MapName { get; init; }
        public required byte TeamSize { get; init; }
        public required int ArcadeMode { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? EndGameSentAtUtc { get; set; }
        public bool EndGameSent => EndGameSentAtUtc.HasValue;
        public bool BotsSeeded { get; set; }
        public int ExpectedBotCount { get; set; }
        public int ReportedBotCount { get; set; }
        public int DispatchedBotCommandCount { get; set; }
        public DateTimeOffset? BotCommandsDispatchedAtUtc { get; set; }
        public bool BotsSeededReady { get; set; }
        public DateTimeOffset? BotsSeededReadyAtUtc { get; set; }
        public HashSet<int> Team1Accounts { get; } = [];
        public HashSet<int> Team2Accounts { get; } = [];
    }

    public QueueArcadeCoordinatorService(
        IConfiguration configuration,
        IChatContext chatContext,
        IMatchmakingService matchmakingService,
        TimeProvider? timeProvider = null,
        int? randomSeed = null,
        StackExchange.Redis.IDatabase? distributedCache = null)
        : base(Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueArcadeCoordinatorService>.Instance, nameof(QueueArcadeCoordinatorService), timeProvider: timeProvider)
    {
        _configuration = configuration;
        _chatContext = chatContext;
        _matchmakingService = matchmakingService;
        _distributedCache = distributedCache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = randomSeed.HasValue ? new Random(randomSeed.Value) : Random.Shared;

        if (_matchmakingService is MatchmakingService concreteMatchmaking)
        {
            concreteMatchmaking.AttachQueueArcadeService(this);
        }
    }

    public IReadOnlyList<string> RotationHistory
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _rotationHistory];
            }
        }
    }

    public QueueArcadeMetricsSnapshot GetMetricsSnapshot()
    {
        ArcadeIdleMode idleMode = ResolveEffectiveIdleMode();
        int totalLobbyDemand = CountOptedInLobbyOrArcadeDemandAcrossAllRegions();

        lock (_stateLock)
        {
            int activeMatches = _arcadeByServerId.Values.Count(s => s.MatchId > 0 && !s.EndGameSent);
            int activeHumans = _arcadeByServerId.Values.Sum(s => s.Team1Accounts.Count + s.Team2Accounts.Count);

            return new QueueArcadeMetricsSnapshot(
                MatchesStartedTotal: Interlocked.Read(ref _matchesStartedTotal),
                MatchesCompletedTotal: Interlocked.Read(ref _matchesCompletedTotal),
                RoundTimerRotationsTotal: Interlocked.Read(ref _roundTimerRotationsTotal),
                PlayersPushedTotal: Interlocked.Read(ref _playersPushedTotal),
                WastedLoadBlocksTotal: Interlocked.Read(ref _wastedLoadBlocksTotal),
                PrePullWarningsSentTotal: Interlocked.Read(ref _prePullWarningsSentTotal),
                RankedPullsTotal: Interlocked.Read(ref _rankedPullsTotal),
                ManualLeavesTotal: Interlocked.Read(ref _manualLeavesTotal),
                MidLoadPullOverridesTotal: Interlocked.Read(ref _midLoadPullOverridesTotal),
                ActiveArcadeMatchesCount: activeMatches,
                ActiveHumanPlayersInArcadeCount: activeHumans,
                LastSelectedMap: _lastSelectedMap,
                RegionMismatchSkipsTotal: Interlocked.Read(ref _regionMismatchSkipsTotal),
                BotReadinessFallbackTimeoutsTotal: Interlocked.Read(ref _botReadinessFallbackTimeoutsTotal),
                EffectiveIdleMode: idleMode,
                RegionalLobbyDemandCount: totalLobbyDemand,
                HibernateHoldsTotal: Interlocked.Read(ref _hibernateHoldsTotal),
                SelfPlayMatchesStartedTotal: Interlocked.Read(ref _selfPlayMatchesStartedTotal));
        }
    }

    internal static ArcadeIdleMode ParseIdleMode(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return ArcadeIdleMode.Hibernate;
        }

        string trimmed = rawValue.Trim().ToLowerInvariant();
        return trimmed is "selfplay" or "self_play" or "self-play" or "rl" or "1" or "true"
            ? ArcadeIdleMode.SelfPlay
            : ArcadeIdleMode.Hibernate;
    }

    internal ArcadeIdleMode ResolveEffectiveIdleMode()
    {
        return _cachedRedisIdleMode ?? ParseIdleMode(_configuration.GetValue<string>("QueueArcade:IdleMode"));
    }

    internal async Task SetIdleModeAsync(ArcadeIdleMode mode, string actor = "operator")
    {
        _cachedRedisIdleMode = mode;
        if (_distributedCache is not null)
        {
            string redisValue = mode == ArcadeIdleMode.SelfPlay ? "selfplay" : "hibernate";
            try
            {
                await _distributedCache.StringSetAsync(RedisIdleModeKey, redisValue).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "[QueueArcade] [SelfPlay] Failed persisting Redis key '{RedisKey}'={Value}: {Error}",
                    RedisIdleModeKey,
                    redisValue,
                    ex.Message);
            }
        }

        Log.Information(
            "[QueueArcade] [SelfPlay] IdleMode updated to {IdleMode} by {Actor}",
            mode,
            actor);
    }

    internal IReadOnlyList<ActiveArcadeMatchSnapshot> GetActiveArcadeMatchesSnapshot()
    {
        lock (_stateLock)
        {
            return _arcadeByServerId.Values
                .Select(s => new ActiveArcadeMatchSnapshot(
                    s.ServerId,
                    s.MatchUpId,
                    s.MatchId,
                    s.MapName,
                    s.TeamSize,
                    s.ArcadeMode,
                    s.Team1Accounts.Count,
                    s.Team2Accounts.Count,
                    s.CreatedAtUtc,
                    s.StartedAtUtc,
                    s.EndGameSent))
                .ToList();
        }
    }

    public static bool IsBotActiveMap(string mapName) => BotActiveStandardMaps.Contains(mapName);

    public static string NormalizeServerRegion(string? serverLocation) =>
        string.IsNullOrWhiteSpace(serverLocation) ? "USE" : serverLocation.Trim();

    public static bool IsRegionTokenMatch(string? playerRegionToken, string normalizedServerRegion)
    {
        if (string.IsNullOrWhiteSpace(playerRegionToken))
        {
            return false;
        }

        string cleanPlayer = playerRegionToken.Trim();
        if (normalizedServerRegion.Equals("ANY", StringComparison.OrdinalIgnoreCase) || normalizedServerRegion == "*")
        {
            return true;
        }

        if (cleanPlayer.Equals("ANY", StringComparison.OrdinalIgnoreCase) || cleanPlayer == "*")
        {
            return true;
        }

        // HoN client TMM UI (regions.lua) sends 'use'/'usw' checkboxes (no 'us' checkbox), whereas 'US' is a valid svr_location code
        if (normalizedServerRegion.Equals("US", StringComparison.OrdinalIgnoreCase))
        {
            return cleanPlayer.Equals("US", StringComparison.OrdinalIgnoreCase) ||
                   cleanPlayer.Equals("USE", StringComparison.OrdinalIgnoreCase) ||
                   cleanPlayer.Equals("USW", StringComparison.OrdinalIgnoreCase);
        }

        return cleanPlayer.Equals(normalizedServerRegion, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsArcadeServerRegionCompatible(IReadOnlyList<string>? groupRegions, string? serverLocation)
    {
        string normalizedServer = NormalizeServerRegion(serverLocation);
        if (normalizedServer.Equals("ANY", StringComparison.OrdinalIgnoreCase) || normalizedServer == "*")
        {
            return true;
        }

        if (groupRegions is null || groupRegions.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < groupRegions.Count; i++)
        {
            if (IsRegionTokenMatch(groupRegions[i], normalizedServer))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetSingleHeroBotDefinition(string mapName, out string? botDefinition) =>
        BotMatchMapCatalog.TryGetSingleHeroBot(mapName, out botDefinition);

    public static byte ResolveTeamSizeForMap(string mapName) =>
        mapName.EndsWith("_10v10", StringComparison.OrdinalIgnoreCase) ? (byte)10 : (byte)5;

    public static string BuildArcadeMatchSettings(string mapName, byte teamSize, int botDifficulty)
    {
        if (BotMatchMapCatalog.TryGetSingleHeroBot(mapName, out string? singleHeroBotDef))
        {
            string teamBotList = string.Join('|', Enumerable.Repeat(singleHeroBotDef, teamSize));
            return $"mode:botmatch map:{mapName} teamsize:{teamSize} casual:true allheroes:true allowduplicate:true bots1:{teamBotList} bots2:{teamBotList} botdifficulty:{botDifficulty} nostats:true spectators:20";
        }

        if (BotMatchMapCatalog.TryGetCustomBotPool(mapName, out IReadOnlyList<string>? customPool))
        {
            string[] shuffled = [.. customPool];
            Random.Shared.Shuffle(shuffled);
            string[] team1Bots = new string[teamSize];
            string[] team2Bots = new string[teamSize];
            for (int i = 0; i < teamSize; i++)
            {
                team1Bots[i] = shuffled[(i * 2) % shuffled.Length];
                team2Bots[i] = shuffled[((i * 2) + 1) % shuffled.Length];
            }

            return $"mode:botmatch map:{mapName} teamsize:{teamSize} casual:true allheroes:true bots1:{string.Join('|', team1Bots)} bots2:{string.Join('|', team2Bots)} botdifficulty:{botDifficulty} nostats:true spectators:20";
        }

        return $"mode:botmatch map:{mapName} teamsize:{teamSize} casual:true allheroes:true randombots:{teamSize}|{teamSize} botdifficulty:{botDifficulty} nostats:true spectators:20";
    }

    private string[] GetActiveMapPool() =>
        _configuration.GetValue("QueueArcade:IncludeCustomPartyMaps", false)
            ? UnifiedArcadeMapPool
            : BotSafeArcadeMapPool;

    private void RefillShuffleBagUnsafe(string? avoidFirstMap)
    {
        string[] activePool = GetActiveMapPool();
        string[] shuffled = [.. activePool];
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        if (!string.IsNullOrWhiteSpace(avoidFirstMap) &&
            shuffled.Length > 1 &&
            shuffled[0].Equals(avoidFirstMap, StringComparison.OrdinalIgnoreCase))
        {
            int swapIndex = _random.Next(1, shuffled.Length);
            (shuffled[0], shuffled[swapIndex]) = (shuffled[swapIndex], shuffled[0]);
        }

        _shuffleBag.AddRange(shuffled);
    }

    public string SelectNextMap(string? previousMap = null)
    {
        lock (_stateLock)
        {
            return SelectNextMapUnsafe(previousMap);
        }
    }

    private string SelectNextMapUnsafe(string? previousMap = null)
    {
        bool includeCustomPartyMaps = _configuration.GetValue("QueueArcade:IncludeCustomPartyMaps", false);
        if (_lastIncludeCustomPartyMaps.HasValue && _lastIncludeCustomPartyMaps.Value != includeCustomPartyMaps)
        {
            _shuffleBag.Clear();
        }
        _lastIncludeCustomPartyMaps = includeCustomPartyMaps;

        string[] activePool = includeCustomPartyMaps ? UnifiedArcadeMapPool : BotSafeArcadeMapPool;
        string? effectivePrev = previousMap ?? _lastSelectedMap;

        if (_shuffleBag.Count == 0)
        {
            RefillShuffleBagUnsafe(effectivePrev);
        }
        else if (!string.IsNullOrWhiteSpace(effectivePrev) &&
                 _shuffleBag[0].Equals(effectivePrev, StringComparison.OrdinalIgnoreCase))
        {
            if (_shuffleBag.Count > 1)
            {
                int swapIndex = _random.Next(1, _shuffleBag.Count);
                (_shuffleBag[0], _shuffleBag[swapIndex]) = (_shuffleBag[swapIndex], _shuffleBag[0]);
            }
            else
            {
                _shuffleBag.Clear();
                RefillShuffleBagUnsafe(effectivePrev);
            }
        }

        string chosen = _shuffleBag[0];
        _shuffleBag.RemoveAt(0);
        _lastSelectedMap = chosen;
        _rotationHistory.Add(chosen);

        Log.Information(
            "[QueueArcade] [MapRotator] Selected next mini-game map '{ChosenMap}' (PreviousMap='{PreviousMap}', BagRemaining={BagRemaining}/{PoolSize}, IncludeCustomPartyMaps={IncludeCustomPartyMaps}, RotationCount={RotationCount})",
            chosen,
            effectivePrev ?? "none",
            _shuffleBag.Count,
            activePool.Length,
            includeCustomPartyMaps,
            _rotationHistory.Count);

        return chosen;
    }

    private string SelectNextSelfPlayMapUnsafe(string? previousMap = null)
    {
        string? effectivePrev = previousMap ?? _lastSelectedSelfPlayMap;

        if (_selfPlayShuffleBag.Count == 0)
        {
            List<string> refilled = [.. RlSelfPlayMapPool];
            for (int i = refilled.Count - 1; i > 0; i--)
            {
                int j = _random.Next(0, i + 1);
                (refilled[i], refilled[j]) = (refilled[j], refilled[i]);
            }

            if (refilled.Count > 1 &&
                !string.IsNullOrWhiteSpace(effectivePrev) &&
                refilled[0].Equals(effectivePrev, StringComparison.OrdinalIgnoreCase))
            {
                int swapIndex = _random.Next(1, refilled.Count);
                (refilled[0], refilled[swapIndex]) = (refilled[swapIndex], refilled[0]);
            }

            _selfPlayShuffleBag.AddRange(refilled);
        }
        else if (!string.IsNullOrWhiteSpace(effectivePrev) &&
                 _selfPlayShuffleBag[0].Equals(effectivePrev, StringComparison.OrdinalIgnoreCase))
        {
            if (_selfPlayShuffleBag.Count > 1)
            {
                int swapIndex = _random.Next(1, _selfPlayShuffleBag.Count);
                (_selfPlayShuffleBag[0], _selfPlayShuffleBag[swapIndex]) = (_selfPlayShuffleBag[swapIndex], _selfPlayShuffleBag[0]);
            }
            else
            {
                _selfPlayShuffleBag.Clear();
                return SelectNextSelfPlayMapUnsafe(effectivePrev);
            }
        }

        string chosen = _selfPlayShuffleBag[0];
        _selfPlayShuffleBag.RemoveAt(0);
        _lastSelectedSelfPlayMap = chosen;
        _rotationHistory.Add(chosen);

        Log.Information(
            "[QueueArcade] [SelfPlayMapRotator] Selected next 5v5 arena map '{ChosenMap}' (PreviousMap='{PreviousMap}', BagRemaining={BagRemaining}/{PoolSize})",
            chosen,
            effectivePrev ?? "none",
            _selfPlayShuffleBag.Count,
            RlSelfPlayMapPool.Length);

        return chosen;
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        int intervalMs = _configuration.GetValue("QueueArcade:IntervalInMilliseconds", 1000);
        Log.Information("[QueueArcade] Coordinator service starting (TickInterval={IntervalMs}ms)", intervalMs);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(Math.Max(25, intervalMs)), _timeProvider);
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await EvaluateTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[QueueArcade] Transient error during Queue Arcade tick; continuing loop");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        Log.Information("[QueueArcade] Coordinator service stopped");
    }

    public void OnArcadeServerHandshake(ChatSession serverSession)
    {
        if (serverSession.ServerMetadata is null || !serverSession.ServerMetadata.IsQueueArcade)
        {
            return;
        }

        int serverId = serverSession.ServerMetadata.ServerID;
        lock (_stateLock)
        {
            _loadingTimeoutBackoffUntilUtc.Remove(serverId);
            _postMatchVictoryHoldUntilUtc.Remove(serverId);
        }

        Log.Information(
            "[QueueArcade] [Handshake] Dedicated Queue Arcade server registered: ServerID={ServerID} Address={Address}:{Port} Local={LocalAddress}:{LocalPort} Location='{Location}'",
            serverId,
            serverSession.ServerMetadata.Address,
            serverSession.ServerMetadata.Port,
            serverSession.ServerMetadata.LocalAddress,
            serverSession.ServerMetadata.LocalPort,
            serverSession.ServerMetadata.Location);

        EnsureArcadeMatchesRunning(_timeProvider.GetUtcNow());
    }

    public void ConfirmArcadeMatchReady(ChatSession serverSession, int matchupId, int matchId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int serverId = serverSession.ServerMetadata?.ServerID ?? -1;
        List<string>? botCommandsToDispatch = null;
        string seededMapName = string.Empty;

        lock (_stateLock)
        {
            if (!_arcadeByServerId.TryGetValue(serverId, out ArcadeMatchRuntimeState? state) &&
                (!_serverIdByMatchUpId.TryGetValue(matchupId, out int mappedServerId) ||
                 !_arcadeByServerId.TryGetValue(mappedServerId, out state)))
            {
                Log.Warning(
                    "[QueueArcade] [Confirm] Received AnnounceMatch for untracked Arcade MatchUpID={MatchUpID} MatchID={MatchID} on ServerID={ServerID}; synthesizing runtime state",
                    matchupId,
                    matchId,
                    serverId);

                string fallbackMap = serverSession.ServerMetadata?.MapName is { Length: > 0 } m ? m : "midwars";
                byte fallbackTeamSize = ResolveTeamSizeForMap(fallbackMap);
                state = new ArcadeMatchRuntimeState
                {
                    ServerId = serverId,
                    MatchUpId = matchupId,
                    MatchId = matchId,
                    MapName = fallbackMap,
                    TeamSize = fallbackTeamSize,
                    ArcadeMode = IsBotActiveMap(fallbackMap) ? 1 : 2,
                    CreatedAtUtc = now,
                    StartedAtUtc = now
                };
                _arcadeByServerId[serverId] = state;
            }
            else
            {
                state.MatchId = matchId;
                state.StartedAtUtc = now;
            }

            if (serverSession.ServerMetadata is not null)
            {
                serverSession.ServerMetadata.MatchID = matchId;
                serverSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE;
            }

            _postMatchSettleAttempts.Remove(state.ServerId);
            _postMatchSettleRetryAfterUtc.Remove(state.ServerId);
            _loadingTimeoutBackoffUntilUtc.Remove(state.ServerId);
            _postMatchVictoryHoldUntilUtc.Remove(state.ServerId);

            if (!state.BotsSeeded)
            {
                state.BotsSeeded = true;
                if (state.ArcadeMode == 1)
                {
                    seededMapName = state.MapName;
                    state.ExpectedBotCount = state.TeamSize * 2;
                    state.ReportedBotCount = 0;
                    state.DispatchedBotCommandCount = 0;
                    state.BotCommandsDispatchedAtUtc = null;
                    state.BotsSeededReady = false;
                    state.BotsSeededReadyAtUtc = null;
                    botCommandsToDispatch = new List<string>(state.TeamSize * 2);

                    if (BotMatchMapCatalog.TryGetSingleHeroBot(state.MapName, out string? singleHeroBot))
                    {
                        for (int i = 0; i < state.TeamSize; i++)
                        {
                            botCommandsToDispatch.Add($"AddBot 1 {singleHeroBot}");
                            botCommandsToDispatch.Add($"AddBot 2 {singleHeroBot}");
                        }
                    }
                    else
                    {
                        string[] shuffledPool = BotMatchMapCatalog.TryGetCustomBotPool(state.MapName, out IReadOnlyList<string>? customPool)
                            ? [.. customPool]
                            : [.. ArcadeBotPool];
                        for (int i = shuffledPool.Length - 1; i > 0; i--)
                        {
                            int swapIdx = _random.Next(i + 1);
                            (shuffledPool[i], shuffledPool[swapIdx]) = (shuffledPool[swapIdx], shuffledPool[i]);
                        }

                        for (int i = 0; i < state.TeamSize; i++)
                        {
                            string legionBot = shuffledPool[(i * 2) % shuffledPool.Length];
                            string hellbourneBot = shuffledPool[((i * 2) + 1) % shuffledPool.Length];
                            botCommandsToDispatch.Add($"AddBot 1 {legionBot}");
                            botCommandsToDispatch.Add($"AddBot 2 {hellbourneBot}");
                        }
                    }
                }
                else
                {
                    state.ExpectedBotCount = 0;
                    state.ReportedBotCount = 0;
                    state.BotsSeededReady = true;
                    state.BotsSeededReadyAtUtc = now;
                    Log.Information(
                        "[QueueArcade] [SeedBots] Skipping lane-bot AddBot seeding on custom party map '{MapName}' (ArcadeMode={ArcadeMode}, ServerID={ServerID}, MatchID={MatchID})",
                        state.MapName,
                        state.ArcadeMode,
                        state.ServerId,
                        matchId);
                }
            }

            Log.Information(
                "[QueueArcade] [Ready] Arcade Match Ready! ServerID={ServerID} MatchUpID={MatchUpID} -> MatchID={MatchID} Map='{MapName}' TeamSize={TeamSize}v{TeamSize} ArcadeMode={ArcadeMode}",
                state.ServerId,
                state.MatchUpId,
                matchId,
                state.MapName,
                state.TeamSize,
                state.ArcadeMode);

            if (matchId <= 0)
            {
                Log.Warning(
                    "[QueueArcade] [AuthDegraded] ServerID={ServerID} announced MatchUpID={MatchUpID} on Map='{MapName}' with MatchID={MatchID}; holding player pushes until session auth is resynced.",
                    state.ServerId,
                    state.MatchUpId,
                    state.MapName,
                    matchId);
            }
        }

        if (botCommandsToDispatch is { Count: > 0 })
        {
            const int botsPerPacket = 4;
            int batchCount = 0;
            for (int offset = 0; offset < botCommandsToDispatch.Count; offset += botsPerPacket)
            {
                int take = Math.Min(botsPerPacket, botCommandsToDispatch.Count - offset);
                string batchCmd = string.Join(";", botCommandsToDispatch.GetRange(offset, take));
                SendRemoteConsoleCommand(serverSession, batchCmd);
                batchCount++;
            }

            if (matchId > 0)
            {
                DateTimeOffset dispatchedAtUtc = _timeProvider.GetUtcNow();
                lock (_stateLock)
                {
                    if (_arcadeByServerId.TryGetValue(serverId, out ArcadeMatchRuntimeState? postSeedState) &&
                        postSeedState.MatchId == matchId)
                    {
                        postSeedState.DispatchedBotCommandCount = botCommandsToDispatch.Count;
                        postSeedState.BotCommandsDispatchedAtUtc = dispatchedAtUtc;
                    }
                }
            }

            Log.Information(
                "[QueueArcade] [SeedBots] Dispatched {BotCount} post-LoadWorld AddBot commands across {BatchCount} batch(es) on ServerID={ServerID} MatchID={MatchID} Map='{MapName}'",
                botCommandsToDispatch.Count,
                batchCount,
                serverId,
                matchId,
                seededMapName);
        }
    }

    public void NotifyArcadeServerBotReadiness(int serverId, int activeBotCount, bool explicitReadyAck = false)
    {
        lock (_stateLock)
        {
            if (!_arcadeByServerId.TryGetValue(serverId, out ArcadeMatchRuntimeState? state))
            {
                return;
            }

            if (state.MatchId <= 0)
            {
                Log.Debug(
                    "[QueueArcade] [BotReadiness] Ignoring readiness update on ServerID={ServerID} while MatchID={MatchID} <= 0 (ActiveBotCount={ActiveBotCount}, ExplicitAck={ExplicitAck})",
                    serverId,
                    state.MatchId,
                    activeBotCount,
                    explicitReadyAck);
                return;
            }

            if (!explicitReadyAck && (!state.BotsSeeded || state.ExpectedBotCount <= 0))
            {
                Log.Debug(
                    "[QueueArcade] [BotReadiness] Ignoring non-explicit readiness update on ServerID={ServerID} MatchID={MatchID} before bot seeding is active (BotsSeeded={BotsSeeded}, Expected={Expected}, ActiveBotCount={ActiveBotCount})",
                    serverId,
                    state.MatchId,
                    state.BotsSeeded,
                    state.ExpectedBotCount,
                    activeBotCount);
                return;
            }

            state.ReportedBotCount = Math.Max(state.ReportedBotCount, activeBotCount);
            if (!state.BotsSeededReady)
            {
                bool isReady = explicitReadyAck ||
                               (state.ExpectedBotCount > 0 && state.ReportedBotCount >= state.ExpectedBotCount);

                if (isReady)
                {
                    state.BotsSeededReady = true;
                    state.BotsSeededReadyAtUtc = _timeProvider.GetUtcNow();
                    Log.Information(
                        "[QueueArcade] [BotReadiness] ServerID={ServerID} MatchID={MatchID} bot readiness achieved: Reported={Reported}/{Expected} ExplicitAck={ExplicitAck} (Elapsed={ElapsedMs:F1}ms)",
                        serverId,
                        state.MatchId,
                        state.ReportedBotCount,
                        state.ExpectedBotCount,
                        explicitReadyAck,
                        state.StartedAtUtc.HasValue ? (_timeProvider.GetUtcNow() - state.StartedAtUtc.Value).TotalMilliseconds : 0.0);
                }
            }
        }
    }

    public void NotifyArcadeServerStatus(int serverId, int matchId, ChatProtocol.ServerStatus status, int activeClientCount)
    {
        int confirmedSeededBotCount = 0;
        bool shouldPromoteExplicitReady = false;

        if (status == ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE && matchId > 0)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            lock (_stateLock)
            {
                if (_arcadeByServerId.TryGetValue(serverId, out ArcadeMatchRuntimeState? state) &&
                    state.MatchId == matchId &&
                    !state.BotsSeededReady &&
                    state.BotsSeeded &&
                    state.ExpectedBotCount > 0 &&
                    state.BotCommandsDispatchedAtUtc.HasValue &&
                    state.DispatchedBotCommandCount >= state.ExpectedBotCount &&
                    (now - state.BotCommandsDispatchedAtUtc.Value) >= TimeSpan.FromMilliseconds(150))
                {
                    confirmedSeededBotCount = state.ExpectedBotCount;
                    shouldPromoteExplicitReady = true;
                }
            }
        }

        if (shouldPromoteExplicitReady)
        {
            Log.Information(
                "[QueueArcade] [BotReadiness] Active post-AddBot status confirmation received from ServerID={ServerID} MatchID={MatchID} (DispatchedBots={DispatchedBots}, ActiveHumans={ActiveHumans}); promoting explicit bot readiness.",
                serverId,
                matchId,
                confirmedSeededBotCount,
                activeClientCount);
            NotifyArcadeServerBotReadiness(serverId, confirmedSeededBotCount, explicitReadyAck: true);
            return;
        }

        NotifyArcadeServerBotReadiness(serverId, activeClientCount, explicitReadyAck: false);
    }

    public void OnArcadeMatchEnded(ChatSession serverSession, int matchId, string reason)
    {
        if (serverSession.ServerMetadata is null || !serverSession.ServerMetadata.IsQueueArcade)
        {
            return;
        }

        int serverId = serverSession.ServerMetadata.ServerID;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        HashSet<int> affectedAccounts = [];
        string endedMap = serverSession.ServerMetadata.MapName;
        bool hadTrackedMatch = false;
        int endedTrackedMatchId = matchId;

        lock (_stateLock)
        {
            if (_arcadeByServerId.TryGetValue(serverId, out ArcadeMatchRuntimeState? current))
            {
                bool matchesCurrent =
                    string.Equals(reason, "round_timer_force_recycle", StringComparison.Ordinal) ||
                    (reason.StartsWith("abandon_match", StringComparison.Ordinal) &&
                     (matchId <= 0 || matchId == current.MatchId || matchId == current.MatchUpId || current.MatchId <= 0)) ||
                    (matchId != 0 && (matchId == current.MatchId || matchId == current.MatchUpId)) ||
                    (current.MatchId <= 0 && matchId > 0);

                if (!matchesCurrent)
                {
                    Log.Information(
                        "[QueueArcade] [MatchEndedIgnored] Ignoring stale/duplicate MatchComplete (IncomingId={IncomingId}, Reason='{Reason}') on ServerID={ServerID}; active state is MatchUpID={ActiveMatchUpId} MatchID={ActiveMatchId} Map='{ActiveMap}'",
                        matchId,
                        reason,
                        serverId,
                        current.MatchUpId,
                        current.MatchId,
                        current.MapName);
                    return;
                }

                _lastEndedMatchUpIdByServerId[serverId] = current.MatchUpId;
                _postMatchSettleAttempts.Remove(serverId);
                _postMatchSettleRetryAfterUtc.Remove(serverId);
                _arcadeByServerId.Remove(serverId);
                _serverIdByMatchUpId.Remove(current.MatchUpId);
                endedMap = current.MapName;
                if (current.MatchId > 0)
                {
                    endedTrackedMatchId = current.MatchId;
                }
                affectedAccounts.UnionWith(current.Team1Accounts);
                affectedAccounts.UnionWith(current.Team2Accounts);
                hadTrackedMatch = true;
                _consecutiveRapidServerDisconnects = 0;
                _rapidDisconnectBackoffUntilUtc = null;
            }
        }

        if (hadTrackedMatch)
        {
            Interlocked.Increment(ref _matchesCompletedTotal);
        }

        foreach (int affectedAccountId in affectedAccounts)
        {
            RevokeArcadeAllowTicketFireAndForget(serverId, affectedAccountId, $"match_ended:{reason}");
        }

        double victoryHoldSeconds = Math.Max(
            0.0,
            _cachedRedisVictoryHoldSeconds ?? _configuration.GetValue("QueueArcade:VictoryScreenHoldSeconds", 12.0));
        bool allowVictoryHold = victoryHoldSeconds > 0.0 && !string.Equals(reason, "round_timer_force_recycle", StringComparison.Ordinal);
        int victoryHeldPlayers = 0;

        foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
        {
            if (clientSession.ClientMetadata is null || clientSession.Account is null) continue;

            if ((hadTrackedMatch && clientSession.ClientMetadata.ActiveArcadeServerId == serverId) ||
                (matchId > 0 && clientSession.ClientMetadata.ActiveArcadeMatchId == matchId) ||
                affectedAccounts.Contains(clientSession.Account.ID))
            {
                bool wasStillJoiningEndedMatch =
                    clientSession.ClientMetadata.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME &&
                    clientSession.ClientMetadata.ArcadeJoinSentAtUtc.HasValue &&
                    (now - clientSession.ClientMetadata.ArcadeJoinSentAtUtc.Value).TotalSeconds < 16.0;

                MatchmakingGroup? group = _matchmakingService.GetMatchmakingGroupByMemberID(clientSession.Account.ID);
                bool matchesEndedArcadeMatchId =
                    clientSession.ClientMetadata.MatchID is null ||
                    clientSession.ClientMetadata.MatchID == matchId ||
                    (endedTrackedMatchId > 0 && clientSession.ClientMetadata.MatchID == endedTrackedMatchId);

                bool isEligibleForVictoryAutoPull =
                    allowVictoryHold &&
                    !wasStillJoiningEndedMatch &&
                    matchesEndedArcadeMatchId &&
                    clientSession.ClientMetadata.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME &&
                    clientSession.ClientMetadata.QueueArcadeOptIn &&
                    !clientSession.ClientMetadata.PendingRankedMatchId.HasValue &&
                    group?.QueueStartTime.HasValue == true &&
                    group.Members.All(m => !m.IsInGame) &&
                    clientSession.ClientMetadata.PauseArcadeUntilNextQueueUtc != group.QueueStartTime;

                int resolvedEndedMatchId = endedTrackedMatchId > 0 ? endedTrackedMatchId : matchId;
                if (clientSession.ClientMetadata.MatchID == matchId ||
                    (endedTrackedMatchId > 0 && clientSession.ClientMetadata.MatchID == endedTrackedMatchId))
                {
                    clientSession.ClientMetadata.MatchID = null;
                    clientSession.ClientMetadata.ClientAckedMatchId = null;
                }

                clientSession.ClientMetadata.ActiveArcadeMatchId = null;
                clientSession.ClientMetadata.ActiveArcadeServerId = null;
                clientSession.ClientMetadata.ArcadeJoinSentAtUtc = null;
                clientSession.ClientMetadata.LastArcadePushAttemptAtUtc = null;
                clientSession.ClientMetadata.AwaitingVictoryTransferFromServerId = null;
                clientSession.ClientMetadata.VictoryTransferKickSent = false;
                RevokeArcadeAllowTicketFireAndForget(serverId, clientSession.Account.ID, $"match_ended:{reason}");
                clientSession.ClientMetadata.RankedPullWarningSent = false;

                if (isEligibleForVictoryAutoPull)
                {
                    clientSession.ClientMetadata.PostArcadeVictoryMatchId = Math.Max(1, resolvedEndedMatchId);
                    clientSession.ClientMetadata.PostArcadeVictoryServerId = serverId;
                    clientSession.ClientMetadata.PostArcadeVictoryEndedAtUtc = now;
                    clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = now.AddSeconds(victoryHoldSeconds);
                    victoryHeldPlayers++;

                    SendSystemWhisper(
                        clientSession,
                        $"[QUEUE ARCADE] Round complete! Auto-pulling you into the next mini-game in {(int)Math.Round(victoryHoldSeconds)} seconds (no need to disconnect — or type /arcade leave for lobby).");

                    Log.Information(
                        "[QueueArcade] [VictoryHold] AccountID={AccountID} ('{PlayerName}') holding on Victory screen for {HoldSec:F1}s on ServerID={ServerID} MatchID={MatchID} before auto-pull to next mini-game",
                        clientSession.Account?.ID ?? -1,
                        clientSession.Account?.Name ?? "unknown",
                        victoryHoldSeconds,
                        serverId,
                        resolvedEndedMatchId);
                }
                else
                {
                    clientSession.ClientMetadata.PostArcadeVictoryMatchId = null;
                    clientSession.ClientMetadata.PostArcadeVictoryServerId = null;
                    clientSession.ClientMetadata.PostArcadeVictoryEndedAtUtc = null;
                    if (wasStillJoiningEndedMatch)
                    {
                        clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = now.AddSeconds(3);
                    }
                }
            }
        }

        if (victoryHeldPlayers > 0)
        {
            string endedServerRegion = NormalizeServerRegion(serverSession.ServerMetadata.Location);
            int otherRegionalArcadeServers = _chatContext.MatchServerChatSessions.Values.Count(s =>
                s.IsSessionConnected &&
                s.ServerMetadata is { IsQueueArcade: true } sm &&
                sm.ServerID != serverId &&
                (IsRegionTokenMatch(NormalizeServerRegion(sm.Location), endedServerRegion) ||
                 IsRegionTokenMatch(endedServerRegion, NormalizeServerRegion(sm.Location))));

            double serverHoldSeconds = otherRegionalArcadeServers > 0
                ? victoryHoldSeconds + 8.0
                : Math.Min(victoryHoldSeconds, 10.0);

            lock (_stateLock)
            {
                _postMatchVictoryHoldUntilUtc[serverId] = now.AddSeconds(serverHoldSeconds);
            }

            serverSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE;
            serverSession.ServerMetadata.MatchID = -1;

            Log.Information(
                "[QueueArcade] [MatchEnded] Mini-game match ended on ServerID={ServerID} (MatchID={MatchID}, Map='{MapName}', Reason='{Reason}', VictoryHeldPlayers={VictoryHeldPlayers}, OtherRegionalServers={OtherRegionalServers}). Holding ServerID={ServerID} out of IDLE for {ServerHoldSec:F1}s while pre-warming next map immediately.",
                serverId,
                matchId,
                endedMap,
                reason,
                victoryHeldPlayers,
                otherRegionalArcadeServers,
                serverId,
                serverHoldSeconds);
        }
        else
        {
            serverSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
            serverSession.ServerMetadata.MatchID = -1;

            Log.Information(
                "[QueueArcade] [MatchEnded] Mini-game match ended on ServerID={ServerID} (MatchID={MatchID}, Map='{MapName}', Reason='{Reason}', Tracked={Tracked}, ClearedPlayers={ClearedCount}). Evaluating demand/idle mode before next start.",
                serverId,
                matchId,
                endedMap,
                reason,
                hadTrackedMatch,
                affectedAccounts.Count);
        }

        EnsureArcadeMatchesRunning(now);
    }

    public void OnArcadeServerNotIdle(ChatSession serverSession, byte currentStatus, int matchUpId)
    {
        if (serverSession.ServerMetadata is null || !serverSession.ServerMetadata.IsQueueArcade)
        {
            return;
        }

        int serverId = serverSession.ServerMetadata.ServerID;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool shouldDispatchEndGame = false;
        string? rewoundMapName = null;

        lock (_stateLock)
        {
            if (currentStatus != (byte)ChatProtocol.ServerStatus.SERVER_STATUS_IDLE)
            {
                serverSession.ServerMetadata.Status = (ChatProtocol.ServerStatus)currentStatus;
            }

            if (!_arcadeByServerId.TryGetValue(serverId, out ArcadeMatchRuntimeState? current))
            {
                _postMatchSettleAttempts.Remove(serverId);
                _postMatchSettleRetryAfterUtc.Remove(serverId);
                shouldDispatchEndGame = true;
            }
            else if (!current.EndGameSent &&
                     current.MatchId <= 0 &&
                     _postMatchSettleAttempts.GetValueOrDefault(serverId, 0) == 0 &&
                     ((_lastEndedMatchUpIdByServerId.TryGetValue(serverId, out int justEndedMatchUpId) && matchUpId == justEndedMatchUpId) ||
                      matchUpId == current.MatchUpId))
            {
                // K2 sends 0x0515 (MatchComplete) at the start of GamePhase 8 (sv_gameEndPhaseTime = 500ms) while still holding
                // m_eStatus = ACTIVE (3). Rewind our newly selected map onto the front of _shuffleBag and schedule a 1.5s retry
                // so the map launches cleanly right after GamePhase 8 finishes (or forces StartPostMatch if K2 rejects a 2nd time).
                _postMatchSettleAttempts[serverId] = 1;
                _postMatchSettleRetryAfterUtc[serverId] = now.AddSeconds(1.5);
                rewoundMapName = current.MapName;
                _shuffleBag.Insert(0, current.MapName);
                if (_rotationHistory.Count > 0 &&
                    _rotationHistory[^1].Equals(current.MapName, StringComparison.OrdinalIgnoreCase))
                {
                    _rotationHistory.RemoveAt(_rotationHistory.Count - 1);
                    _lastSelectedMap = _rotationHistory.Count > 0 ? _rotationHistory[^1] : null;
                }

                _arcadeByServerId.Remove(serverId);
                _serverIdByMatchUpId.Remove(current.MatchUpId);
                Interlocked.Decrement(ref _matchesStartedTotal);
            }
            else if (current.EndGameSent ||
                     _postMatchSettleAttempts.GetValueOrDefault(serverId, 0) > 0 ||
                     (matchUpId != 0 && current.MatchUpId != matchUpId))
            {
                _postMatchSettleAttempts.Remove(serverId);
                _postMatchSettleRetryAfterUtc.Remove(serverId);
                if (!current.EndGameSent && current.MatchId <= 0)
                {
                    _arcadeByServerId.Remove(serverId);
                    _serverIdByMatchUpId.Remove(current.MatchUpId);
                }

                shouldDispatchEndGame = true;
            }
        }

        if (rewoundMapName is not null)
        {
            Log.Information(
                "[QueueArcade] [PostMatchSettle] ServerID={ServerID} still in 500ms GamePhase 8 teardown (ServerMatchUpID={ServerMatchUpID}); rewound Map='{MapName}' for automatic retry in 1.5s",
                serverId,
                matchUpId,
                rewoundMapName);
            return;
        }

        if (shouldDispatchEndGame)
        {
            SendRemoteConsoleCommand(serverSession, EndArcadeMatchCommand);
            Log.Warning(
                "[QueueArcade] [NotIdleRecovery] ServerID={ServerID} reported NET_CHAT_GS_NOT_IDLE (Status={Status}, ServerMatchUpID={ServerMatchUpID}); dispatched '{Command}' to recycle server cleanly",
                serverId,
                (ChatProtocol.ServerStatus)currentStatus,
                matchUpId,
                EndArcadeMatchCommand);
        }
    }

    public bool ReleasePlayerFromArcade(int accountId, string reason, bool pauseCurrentQueueWait = false)
    {
        bool isClientLeftMatch = string.Equals(reason, "client_left_match", StringComparison.Ordinal);
        int? targetServerId = null;
        int? previousArcadeMatchId = null;
        bool wasJoiningOrInArcade = false;
        bool isRankedMatchTransfer = reason.StartsWith("ranked_match_", StringComparison.Ordinal);
        string playerName = $"Account#{accountId}";
        ChatSession? matchedClientSession = null;

        foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
        {
            if (clientSession.Account?.ID != accountId || clientSession.ClientMetadata is null)
            {
                continue;
            }

            matchedClientSession = clientSession;
            playerName = clientSession.Account.Name;

            if (isClientLeftMatch && clientSession.ClientMetadata.AwaitingVictoryTransferFromServerId is int oldVictoryServerId)
            {
                clientSession.ClientMetadata.AwaitingVictoryTransferFromServerId = null;
                clientSession.ClientMetadata.VictoryTransferKickSent = false;
                RevokeArcadeAllowTicketFireAndForget(oldVictoryServerId, accountId, "victory_transfer_old_server");

                if (_chatContext.MatchServerChatSessions.TryGetValue(oldVictoryServerId, out ChatSession? oldSrv) &&
                    oldSrv.IsSessionConnected &&
                    oldSrv.ServerMetadata is not null)
                {
                    SendRemoteConsoleCommand(oldSrv, $"kongor_arcade_release {accountId}");
                }

                Log.Information(
                    "[QueueArcade] [VictoryTransferLeftMatch] Absorbed stale LeftMatch from OldServerID={OldServerID} for AccountID={AccountID} ('{PlayerName}') while transferring into ActiveArcadeServerID={NewServerID} MatchID={NewMatchID}",
                    oldVictoryServerId,
                    accountId,
                    playerName,
                    clientSession.ClientMetadata.ActiveArcadeServerId ?? -1,
                    clientSession.ClientMetadata.ActiveArcadeMatchId ?? -1);
                return true;
            }

            previousArcadeMatchId = clientSession.ClientMetadata.ActiveArcadeMatchId
                                    ?? clientSession.ClientMetadata.PostArcadeVictoryMatchId;
            targetServerId = clientSession.ClientMetadata.ActiveArcadeServerId
                             ?? clientSession.ClientMetadata.PostArcadeVictoryServerId;
            wasJoiningOrInArcade =
                previousArcadeMatchId.HasValue ||
                targetServerId.HasValue ||
                clientSession.ClientMetadata.ArcadeJoinSentAtUtc.HasValue;
            isRankedMatchTransfer =
                isRankedMatchTransfer ||
                clientSession.ClientMetadata.AwaitingArcadeDisconnectForRankedPull ||
                clientSession.ClientMetadata.RankedPullWarningSent ||
                clientSession.ClientMetadata.PendingRankedMatchId.HasValue;

            clientSession.ClientMetadata.ActiveArcadeMatchId = null;
            clientSession.ClientMetadata.ActiveArcadeServerId = null;
            clientSession.ClientMetadata.ArcadeJoinSentAtUtc = null;
            clientSession.ClientMetadata.LastArcadePushAttemptAtUtc = null;
            clientSession.ClientMetadata.PostArcadeVictoryMatchId = null;
            clientSession.ClientMetadata.PostArcadeVictoryServerId = null;
            clientSession.ClientMetadata.PostArcadeVictoryEndedAtUtc = null;
            clientSession.ClientMetadata.AwaitingVictoryTransferFromServerId = null;
            clientSession.ClientMetadata.VictoryTransferKickSent = false;
            clientSession.ClientMetadata.RankedPullWarningSent = false;
            clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = null;
            break;
        }

        bool removedFromSlot = false;
        bool isMatchStillRunningOnServer = false;
        lock (_stateLock)
        {
            foreach (ArcadeMatchRuntimeState state in _arcadeByServerId.Values)
            {
                bool hadAccountInSlot = state.Team1Accounts.Remove(accountId) | state.Team2Accounts.Remove(accountId);
                if (hadAccountInSlot)
                {
                    targetServerId ??= state.ServerId;
                    removedFromSlot = true;
                }

                if ((hadAccountInSlot ||
                     (previousArcadeMatchId.HasValue && state.MatchId == previousArcadeMatchId.Value) ||
                     (targetServerId.HasValue && state.ServerId == targetServerId.Value)) &&
                    !state.EndGameSent &&
                    _chatContext.MatchServerChatSessions.TryGetValue(state.ServerId, out ChatSession? srv) &&
                    srv.IsSessionConnected)
                {
                    isMatchStillRunningOnServer = true;
                }
            }
        }

        bool isTargetServerStillConnected =
            !targetServerId.HasValue ||
            (_chatContext.MatchServerChatSessions.TryGetValue(targetServerId.Value, out ChatSession? checkServer) &&
             checkServer.IsSessionConnected);

        bool hadArcadeAssociation = wasJoiningOrInArcade || removedFromSlot;
        bool effectiveManualOptOut =
            pauseCurrentQueueWait &&
            hadArcadeAssociation &&
            !isRankedMatchTransfer &&
            (!isClientLeftMatch || isTargetServerStillConnected);

        if (effectiveManualOptOut && matchedClientSession?.ClientMetadata is not null)
        {
            matchedClientSession.ClientMetadata.QueueArcadeOptIn = false;
            matchedClientSession.ClientMetadata.PauseArcadeUntilNextQueueUtc = null;
            matchedClientSession.ClientMetadata.LastVoluntarilyLeftArcadeMatchId = previousArcadeMatchId;
            matchedClientSession.ClientMetadata.LastVoluntarilyLeftArcadeAtUtc = _timeProvider.GetUtcNow();

            if (isClientLeftMatch)
            {
                SendSystemWhisper(
                    matchedClientSession,
                    "[QUEUE ARCADE] Queue Mini-Games DISABLED after disconnecting. Click ARCADE: OFF at the top or type /arcade anytime to hop back in!");

                Log.Information(
                    "[QueueArcade] [ManualDisconnectOptOut] AccountID={AccountID} ('{PlayerName}') manually disconnected from Queue Arcade (ServerID={ServerID}, MatchID={MatchID}); set QueueArcadeOptIn=false across current and future queues until re-enabled",
                    accountId,
                    playerName,
                    targetServerId ?? -1,
                    previousArcadeMatchId ?? -1);
            }
        }
        else if (isRankedMatchTransfer)
        {
            Log.Information(
                "[QueueArcade] [ServerTransferKeepOptIn] Preserved QueueArcadeOptIn={OptIn} for AccountID={AccountID} ('{PlayerName}') during Ranked match transfer (Reason='{Reason}')",
                matchedClientSession?.ClientMetadata?.QueueArcadeOptIn ?? true,
                accountId,
                playerName,
                reason);
        }

        if (!hadArcadeAssociation)
        {
            return false;
        }

        if (effectiveManualOptOut)
        {
            Interlocked.Increment(ref _manualLeavesTotal);
        }

        if (targetServerId.HasValue)
        {
            RevokeArcadeAllowTicketFireAndForget(targetServerId.Value, accountId, reason);
        }

        bool shouldKickLingeringClient =
            isMatchStillRunningOnServer &&
            (isRankedMatchTransfer || string.Equals(reason, "client_session_cleanup", StringComparison.Ordinal));

        if (targetServerId.HasValue &&
            _chatContext.MatchServerChatSessions.TryGetValue(targetServerId.Value, out ChatSession? serverSession) &&
            serverSession.IsSessionConnected &&
            serverSession.ServerMetadata is not null)
        {
            string releaseCommand = shouldKickLingeringClient
                ? $"kongor_arcade_release {accountId} kick"
                : $"kongor_arcade_release {accountId}";
            SendRemoteConsoleCommand(serverSession, releaseCommand);
        }

        Log.Information(
            "[QueueArcade] [SlotRelease] Released AccountID={AccountID} ('{PlayerName}') from Arcade ServerID={ServerID} MatchID={MatchID} (Reason='{Reason}', ManualOptOut={ManualOptOut}, MatchStillRunning={MatchStillRunning}, KickLingeringClient={KickLingeringClient})",
            accountId,
            playerName,
            targetServerId ?? -1,
            previousArcadeMatchId ?? -1,
            reason,
            effectiveManualOptOut,
            isMatchStillRunningOnServer,
            shouldKickLingeringClient);
        return false;
    }

    public void NotifyRankedMatchAllocating(IReadOnlyList<MatchmakingGroup> groups, string resolvedMapName)
    {
        foreach (MatchmakingGroup group in groups)
        {
            foreach (MatchmakingGroupMember member in group.Members)
            {
                ClientChatSessionMetadata? meta = member.Session?.ClientMetadata;
                if (meta is null ||
                    (!meta.ActiveArcadeMatchId.HasValue &&
                     !meta.ActiveArcadeServerId.HasValue &&
                     !meta.PostArcadeVictoryMatchId.HasValue &&
                     !meta.PostArcadeVictoryServerId.HasValue))
                {
                    continue;
                }

                if (meta.RankedPullWarningSent)
                {
                    continue;
                }

                meta.RankedPullWarningSent = true;
                Interlocked.Increment(ref _prePullWarningsSentTotal);

                SendSystemWhisper(
                    member.Session!,
                    $"[QUEUE ARCADE] RANKED MATCH FOUND ({resolvedMapName})! Pulling you from mini-game into your Ranked match in ~3 seconds...");

                Log.Information(
                    "[QueueArcade] [PrePullAlert] Sent 3s Ranked pre-transfer alert to AccountID={AccountID} ('{AccountName}') in Arcade MatchID={ArcadeMatchID} -> Ranked Map '{RankedMap}'",
                    member.Account.ID,
                    member.Account.Name,
                    meta.ActiveArcadeMatchId ?? -1,
                    resolvedMapName);
            }
        }
    }

    public void OnPlayersPulledToRankedMatch(IReadOnlyList<MatchmakingGroupMember> members, int rankedMatchId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (MatchmakingGroupMember member in members)
        {
            ClientChatSessionMetadata? meta = member.Session?.ClientMetadata;
            if (meta is null)
            {
                continue;
            }

            meta.PauseArcadeUntilNextQueueUtc = null;
            meta.LastVoluntarilyLeftArcadeMatchId = null;
            meta.LastVoluntarilyLeftArcadeAtUtc = null;

            bool wasOnVictoryScreen = meta.PostArcadeVictoryMatchId.HasValue || meta.PostArcadeVictoryServerId.HasValue;
            meta.PostArcadeVictoryMatchId = null;
            meta.PostArcadeVictoryServerId = null;
            meta.PostArcadeVictoryEndedAtUtc = null;
            meta.AwaitingVictoryTransferFromServerId = null;
            meta.VictoryTransferKickSent = false;

            if (!meta.ActiveArcadeMatchId.HasValue && !meta.ActiveArcadeServerId.HasValue && !wasOnVictoryScreen)
            {
                continue;
            }

            if (!meta.RankedPullWarningSent && member.Session is not null)
            {
                meta.RankedPullWarningSent = true;
                Interlocked.Increment(ref _prePullWarningsSentTotal);
                SendSystemWhisper(
                    member.Session,
                    $"[QUEUE ARCADE] RANKED MATCH FOUND (Match #{rankedMatchId})! Transferring you from mini-game now...");
            }

            if (meta.ArcadeJoinSentAtUtc.HasValue)
            {
                double msSinceArcadeJoin = (now - meta.ArcadeJoinSentAtUtc.Value).TotalMilliseconds;
                if (msSinceArcadeJoin < 4000.0)
                {
                    Interlocked.Increment(ref _midLoadPullOverridesTotal);
                    Log.Information(
                        "[QueueArcade] [MidLoadGuard] AccountID={AccountID} ('{AccountName}') joined Arcade {ElapsedMs:F0}ms ago (<4000ms); releasing Arcade slot cleanly before Ranked MatchID={RankedMatchID} AUTO_MATCH_CONNECT override",
                        member.Account.ID,
                        member.Account.Name,
                        msSinceArcadeJoin,
                        rankedMatchId);
                }
            }

            Interlocked.Increment(ref _rankedPullsTotal);
            ReleasePlayerFromArcade(member.Account.ID, $"ranked_match_confirmed:{rankedMatchId}", pauseCurrentQueueWait: false);
        }
    }

    public bool TryHandleArcadeChatCommand(ChatSession clientSession, string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage) || clientSession.ClientMetadata is null)
        {
            return false;
        }

        string trimmed = rawMessage.Trim();
        if (!trimmed.StartsWith("/arcade", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("!arcade", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!tokens[0].Equals("/arcade", StringComparison.OrdinalIgnoreCase) &&
            !tokens[0].Equals("!arcade", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ClientChatSessionMetadata meta = clientSession.ClientMetadata;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        MatchmakingGroup? group = _matchmakingService.GetMatchmakingGroupByMemberID(clientSession.Account.ID);
        bool isCurrentlyQueued = group?.QueueStartTime.HasValue == true;
        bool isPausedForCurrentQueue = isCurrentlyQueued && meta.PauseArcadeUntilNextQueueUtc == group!.QueueStartTime;

        string subCommand = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "toggle";

        if (meta.LastArcadeChatCommand is not null &&
            string.Equals(meta.LastArcadeChatCommand, subCommand, StringComparison.Ordinal) &&
            meta.LastArcadeChatCommandAtUtc.HasValue &&
            (now - meta.LastArcadeChatCommandAtUtc.Value).TotalSeconds < 1.0)
        {
            Log.Information(
                "[QueueArcade] [CmdDedup] Suppressed duplicate '{Command}' from AccountID={AccountID} ('{Name}') within {ElapsedMs:F0}ms",
                trimmed,
                clientSession.Account.ID,
                clientSession.Account.Name,
                (now - meta.LastArcadeChatCommandAtUtc.Value).TotalMilliseconds);
            return true;
        }

        meta.LastArcadeChatCommand = subCommand;
        meta.LastArcadeChatCommandAtUtc = now;

        // Unified Toggle Rule: bare "/arcade" or "!arcade" enables & re-joins when OFF (or paused),
        // and disables auto-join when ON.
        if (subCommand == "toggle")
        {
            subCommand = (!meta.QueueArcadeOptIn || meta.PauseArcadeUntilNextQueueUtc.HasValue)
                ? "join"
                : "off";
        }

        switch (subCommand)
        {
            case "on":
            case "enable":
            case "join":
            case "rejoin":
            case "hop":
            case "resume":
                if (meta.LastArcadeChatJoinAtUtc.HasValue &&
                    (now - meta.LastArcadeChatJoinAtUtc.Value).TotalSeconds < 5.0)
                {
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] Please wait a few seconds before using /arcade join again.");
                    return true;
                }

                meta.LastArcadeChatJoinAtUtc = now;
                meta.QueueArcadeOptIn = true;
                meta.PauseArcadeUntilNextQueueUtc = null;
                meta.LastVoluntarilyLeftArcadeMatchId = null;
                meta.LastVoluntarilyLeftArcadeAtUtc = null;
                meta.ArcadePushCooldownUntilUtc = null;
                meta.LastArcadePushAttemptAtUtc = null;

                if (isCurrentlyQueued)
                {
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] Queue Mini-Games ENABLED! Re-joining Queue Arcade on the next tick...");
                }
                else
                {
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] Queue Mini-Games ENABLED. You will automatically join ongoing mini-games while waiting in queue.");
                }

                Log.Information(
                    "[QueueArcade] [OptIn] AccountID={AccountID} ('{Name}') enabled/rejoined Queue Arcade via '{Command}' (InQueue={InQueue})",
                    clientSession.Account.ID,
                    clientSession.Account.Name,
                    trimmed,
                    isCurrentlyQueued);
                break;

            case "leave":
            case "exit":
            case "pause":
            case "off":
            case "disable":
                meta.QueueArcadeOptIn = false;
                meta.PauseArcadeUntilNextQueueUtc = null;
                meta.PostArcadeVictoryMatchId = null;
                meta.PostArcadeVictoryServerId = null;
                meta.PostArcadeVictoryEndedAtUtc = null;
                meta.AwaitingVictoryTransferFromServerId = null;
                meta.VictoryTransferKickSent = false;
                if (meta.ActiveArcadeMatchId.HasValue || meta.ActiveArcadeServerId.HasValue)
                {
                    ReleasePlayerFromArcade(clientSession.Account.ID, "chat_cmd_arcade_off", pauseCurrentQueueWait: true);
                }
                SendSystemWhisper(
                    clientSession,
                    "[QUEUE ARCADE] Queue Mini-Games DISABLED. Click ARCADE: OFF at the top or type /arcade anytime to hop back in!");
                Log.Information(
                    "[QueueArcade] [OptOut] AccountID={AccountID} ('{Name}') disabled Queue Arcade via '{Command}'",
                    clientSession.Account.ID,
                    clientSession.Account.Name,
                    trimmed);
                break;

            case "status":
            case "info":
                QueueArcadeMetricsSnapshot metrics = GetMetricsSnapshot();
                string statusText = meta.QueueArcadeOptIn && !isPausedForCurrentQueue ? "ENABLED" : "DISABLED";
                SendSystemWhisper(
                    clientSession,
                    $"[QUEUE ARCADE] Status: {statusText} | IdleMode: {metrics.EffectiveIdleMode} | LobbyDemand: {metrics.RegionalLobbyDemandCount} | Active Mini-Games: {metrics.ActiveArcadeMatchesCount} | Players In Arcade: {metrics.ActiveHumanPlayersInArcadeCount} | SelfPlayMatches: {metrics.SelfPlayMatchesStartedTotal} | Current/Last Map: {metrics.LastSelectedMap ?? "none"}");
                break;

            case "selfplay":
            case "rl":
                if (clientSession.Account?.Type is not AccountType.Staff)
                {
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] Permission denied: /arcade selfplay is restricted to Staff accounts.");
                    return true;
                }

                string arg = tokens.Length > 2 ? tokens[2].Trim().ToLowerInvariant() : "status";
                if (arg is "status" or "info")
                {
                    ArcadeIdleMode currentMode = ResolveEffectiveIdleMode();
                    QueueArcadeMetricsSnapshot currentMetrics = GetMetricsSnapshot();
                    SendSystemWhisper(
                        clientSession,
                        $"[QUEUE ARCADE] RL Self-Play Status: IdleMode={currentMode} | SelfPlayMatches={currentMetrics.SelfPlayMatchesStartedTotal} | ActiveMatches={currentMetrics.ActiveArcadeMatchesCount} | LobbyDemand={currentMetrics.RegionalLobbyDemandCount}");
                }
                else if (arg is "on" or "enable" or "1" or "true")
                {
                    _cachedRedisIdleMode = ArcadeIdleMode.SelfPlay;
                    if (_distributedCache is not null)
                    {
                        try
                        {
                            _distributedCache.StringSet(RedisIdleModeKey, "selfplay");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("[QueueArcade] [SelfPlay] Failed persisting Redis '{RedisKey}'=selfplay: {Error}", RedisIdleModeKey, ex.Message);
                        }
                    }

                    Log.Information(
                        "[QueueArcade] [SelfPlay] Staff AccountID={AccountID} ('{Name}') enabled RL Self-Play idle mode.",
                        clientSession.Account.ID,
                        clientSession.Account.Name);
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] RL Self-Play Idle Mode ENABLED (IdleMode=SelfPlay). Bots will train 5v5 Candidate-vs-Champion rounds whenever 0 players are in lobby/queue.");
                    EnsureArcadeMatchesRunning(now);
                }
                else if (arg is "off" or "disable" or "hibernate" or "0" or "false")
                {
                    _cachedRedisIdleMode = ArcadeIdleMode.Hibernate;
                    if (_distributedCache is not null)
                    {
                        try
                        {
                            _distributedCache.StringSet(RedisIdleModeKey, "hibernate");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("[QueueArcade] [SelfPlay] Failed persisting Redis '{RedisKey}'=hibernate: {Error}", RedisIdleModeKey, ex.Message);
                        }
                    }

                    Log.Information(
                        "[QueueArcade] [SelfPlay] Staff AccountID={AccountID} ('{Name}') switched idle mode to Hibernate.",
                        clientSession.Account.ID,
                        clientSession.Account.Name);
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] RL Self-Play Idle Mode DISABLED (IdleMode=Hibernate). Any running round will finish cleanly, then servers will hibernate until players enter lobby/queue.");
                }
                else
                {
                    SendSystemWhisper(
                        clientSession,
                        "[QUEUE ARCADE] Usage: /arcade selfplay [on|off|status]");
                }
                break;

            case "help":
            case "?":
                string staffHint = clientSession.Account?.Type is AccountType.Staff
                    ? " | /arcade selfplay [on|off|status]"
                    : string.Empty;
                SendSystemWhisper(
                    clientSession,
                    $"[QUEUE ARCADE] Commands: /arcade (toggle ON/OFF or hop in) | /arcade join | /arcade off (or /arcade leave) | /arcade status{staffHint}");
                break;

            default:
                SendSystemWhisper(
                    clientSession,
                    "[QUEUE ARCADE] Unknown subcommand. Try /arcade, /arcade join, /arcade off, or /arcade status.");
                break;
        }

        return true;
    }

    public async Task EvaluateTickAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        await RefreshConnectedArcadeServerSessionCookiesAsync().ConfigureAwait(false);

        // 1. Always prune disconnected Arcade servers & enforce round-duration rotation timers (gentle drain when paused)
        await MaintainActiveArcadeServersAsync(now).ConfigureAwait(false);

        bool effectiveEnabled = await ResolveEffectiveEnabledAsync(now).ConfigureAwait(false);
        if (!effectiveEnabled)
        {
            return;
        }

        // 2. Spin up new random non-repeating maps on IDLE Queue Arcade servers (demand-driven beyond primary #Q1)
        EnsureArcadeMatchesRunning(now);

        // 3. Auto-push eligible queued matchmaking groups into running Arcade matches (packing #Q1 first)
        await PushEligibleQueuedGroupsAsync(now).ConfigureAwait(false);

        // 4. Proactively warm secondary overflow server (#Q2+) immediately if #Q1 just crossed >= 80% occupancy
        EnsureArcadeMatchesRunning(now);
    }

    private async Task<bool> ResolveEffectiveEnabledAsync(DateTimeOffset now)
    {
        bool configDefault = _configuration.GetValue("QueueArcade:Enabled", true);
        if (_distributedCache is null)
        {
            return configDefault;
        }

        if (!_lastRedisEnabledPollUtc.HasValue || (now - _lastRedisEnabledPollUtc.Value).TotalSeconds >= 2.0)
        {
            _lastRedisEnabledPollUtc = now;
            try
            {
                StackExchange.Redis.RedisValue rawValue = await _distributedCache.StringGetAsync(RedisEnabledKey).ConfigureAwait(false);
                bool? parsedEnabled = null;
                if (rawValue.HasValue)
                {
                    string rawEnabledText = rawValue.ToString().Trim();
                    if (bool.TryParse(rawEnabledText, out bool parsedRedisFlag))
                    {
                        parsedEnabled = parsedRedisFlag;
                    }
                    else if (rawEnabledText == "1")
                    {
                        parsedEnabled = true;
                    }
                    else if (rawEnabledText == "0")
                    {
                        parsedEnabled = false;
                    }
                }

                if (_cachedRedisEnabledOverride != parsedEnabled)
                {
                    Log.Information(
                        "[QueueArcade] [LiveToggle] Synchronized Redis key '{RedisKey}'={Enabled} (config fallback={ConfigDefault})",
                        RedisEnabledKey,
                        parsedEnabled?.ToString() ?? "<config>",
                        configDefault);
                    _cachedRedisEnabledOverride = parsedEnabled;
                }

                StackExchange.Redis.RedisValue rawRoundDuration = await _distributedCache.StringGetAsync(RedisRoundDurationSecondsKey).ConfigureAwait(false);
                int? nextRoundDuration = null;
                if (rawRoundDuration.HasValue &&
                    int.TryParse(rawRoundDuration.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedRoundDuration) &&
                    parsedRoundDuration >= 20)
                {
                    nextRoundDuration = parsedRoundDuration;
                }

                if (_cachedRedisRoundDurationSeconds != nextRoundDuration)
                {
                    Log.Information(
                        "[QueueArcade] [LiveToggle] Synchronized Redis key '{RedisKey}'={RoundDuration}",
                        RedisRoundDurationSecondsKey,
                        nextRoundDuration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<config>");
                    _cachedRedisRoundDurationSeconds = nextRoundDuration;
                }

                StackExchange.Redis.RedisValue rawVictoryHold = await _distributedCache.StringGetAsync(RedisVictoryHoldSecondsKey).ConfigureAwait(false);
                double? nextVictoryHold = null;
                if (rawVictoryHold.HasValue &&
                    double.TryParse(rawVictoryHold.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedVictoryHold) &&
                    parsedVictoryHold is >= 0.0 and <= 60.0)
                {
                    nextVictoryHold = parsedVictoryHold;
                }

                if (_cachedRedisVictoryHoldSeconds != nextVictoryHold)
                {
                    Log.Information(
                        "[QueueArcade] [LiveToggle] Synchronized Redis key '{RedisKey}'={VictoryHold}",
                        RedisVictoryHoldSecondsKey,
                        nextVictoryHold?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<config>");
                    _cachedRedisVictoryHoldSeconds = nextVictoryHold;
                }

                StackExchange.Redis.RedisValue rawTeamSize = await _distributedCache.StringGetAsync(MatchmakingService.RedisPlayersPerTeamKey).ConfigureAwait(false);
                int? nextTeamSize = null;
                if (rawTeamSize.HasValue &&
                    int.TryParse(rawTeamSize.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedTeamSize) &&
                    parsedTeamSize is >= 1 and <= 5)
                {
                    nextTeamSize = parsedTeamSize;
                }

                if (_cachedRedisPlayersPerTeamOverride != nextTeamSize)
                {
                    Log.Information(
                        "[QueueArcade] [LiveToggle] Synchronized Redis key '{RedisKey}'={PlayersPerTeam}",
                        MatchmakingService.RedisPlayersPerTeamKey,
                        nextTeamSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<config>");
                    _cachedRedisPlayersPerTeamOverride = nextTeamSize;
                }

                StackExchange.Redis.RedisValue rawIdleMode = await _distributedCache.StringGetAsync(RedisIdleModeKey).ConfigureAwait(false);
                ArcadeIdleMode? nextIdleMode = rawIdleMode.HasValue
                    ? ParseIdleMode(rawIdleMode.ToString())
                    : null;
                if (_cachedRedisIdleMode != nextIdleMode)
                {
                    Log.Information(
                        "[QueueArcade] [LiveToggle] Synchronized Redis key '{RedisKey}'={IdleMode} (config fallback={ConfigDefault})",
                        RedisIdleModeKey,
                        nextIdleMode?.ToString() ?? "<config>",
                        ParseIdleMode(_configuration.GetValue<string>("QueueArcade:IdleMode")));
                    _cachedRedisIdleMode = nextIdleMode;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "[QueueArcade] [LiveToggle] Could not read Redis key '{RedisKey}' ({Error}); preserving effective={Effective}",
                    RedisEnabledKey,
                    ex.Message,
                    _cachedRedisEnabledOverride ?? configDefault);
            }
        }

        return _cachedRedisEnabledOverride ?? configDefault;
    }

    private async Task MaintainActiveArcadeServersAsync(DateTimeOffset now)
    {
        int roundDurationSeconds = _cachedRedisRoundDurationSeconds ?? _configuration.GetValue("QueueArcade:RoundDurationSeconds", 600);
        int loadingTimeoutSeconds = _configuration.GetValue("QueueArcade:LoadingTimeoutSeconds", 15);
        int loadingQuarantineSeconds = _configuration.GetValue("QueueArcade:LoadingTimeoutQuarantineSeconds", 60);

        List<int> disconnectedServerIds = [];
        List<(ChatSession Session, ArcadeMatchRuntimeState State)> roundExpired = [];
        List<(ChatSession Session, ArcadeMatchRuntimeState State)> forceRecycle = [];
        List<(ChatSession Session, ArcadeMatchRuntimeState State)> loadingTimedOut = [];
        List<(ChatSession Session, ArcadeMatchRuntimeState State)> authDegraded = [];
        List<ChatSession> expiredVictoryHoldSessions = [];

        lock (_stateLock)
        {
            foreach ((int serverId, ArcadeMatchRuntimeState state) in _arcadeByServerId)
            {
                if (!_chatContext.MatchServerChatSessions.TryGetValue(serverId, out ChatSession? serverSession) ||
                    !serverSession.IsSessionConnected ||
                    serverSession.ServerMetadata is null)
                {
                    disconnectedServerIds.Add(serverId);
                    continue;
                }

                if (state.MatchId <= 0)
                {
                    if (!state.StartedAtUtc.HasValue)
                    {
                        if ((now - state.CreatedAtUtc).TotalSeconds >= loadingTimeoutSeconds)
                        {
                            loadingTimedOut.Add((serverSession, state));
                        }
                    }
                    else
                    {
                        // ConfirmArcadeMatchReady already ran (AnnounceMatch arrived), so the server is NOT wedged in LOADING;
                        // MasterServer f=start_game failed auth (MatchID <= 0) -> resync/recycle cleanly without 60s quarantine.
                        authDegraded.Add((serverSession, state));
                    }
                    continue;
                }

                if (!state.EndGameSent &&
                    state.StartedAtUtc.HasValue &&
                    (now - state.StartedAtUtc.Value).TotalSeconds >= 20.0 &&
                    _consecutiveRapidServerDisconnects > 0)
                {
                    _consecutiveRapidServerDisconnects = 0;
                    _rapidDisconnectBackoffUntilUtc = null;
                }

                if (!state.EndGameSent &&
                    state.StartedAtUtc.HasValue &&
                    roundDurationSeconds > 0 &&
                    (now - state.StartedAtUtc.Value).TotalSeconds >= roundDurationSeconds)
                {
                    state.EndGameSentAtUtc = now;
                    roundExpired.Add((serverSession, state));
                }
                else if (state.EndGameSentAtUtc.HasValue &&
                         (now - state.EndGameSentAtUtc.Value).TotalSeconds >= 15.0)
                {
                    forceRecycle.Add((serverSession, state));
                }
            }

            foreach (int serverId in disconnectedServerIds)
            {
                _postMatchSettleAttempts.Remove(serverId);
                _postMatchSettleRetryAfterUtc.Remove(serverId);
                if (_arcadeByServerId.Remove(serverId, out ArcadeMatchRuntimeState? removed))
                {
                    _serverIdByMatchUpId.Remove(removed.MatchUpId);
                    double lifetimeSeconds = (now - removed.CreatedAtUtc).TotalSeconds;
                    if (lifetimeSeconds < 20.0)
                    {
                        _consecutiveRapidServerDisconnects++;
                        int backoffSeconds = Math.Min(60, 15 * _consecutiveRapidServerDisconnects);
                        _rapidDisconnectBackoffUntilUtc = now.AddSeconds(backoffSeconds);
                        Interlocked.Increment(ref _circuitBreakerTripsTotal);
                        Log.Warning(
                            "[QueueArcade] [CircuitBreaker] Arcade ServerID={ServerID} disconnected {LifetimeSec:F1}s (<20s) after CreateMatch (Map='{MapName}', ConsecutiveRapidDisconnects={Consecutive}); backing off new CreateMatch dispatches for {BackoffSec}s (Safe-Mode retirement / rapid exit guard)",
                            serverId,
                            lifetimeSeconds,
                            removed.MapName,
                            _consecutiveRapidServerDisconnects,
                            backoffSeconds);
                    }
                    else
                    {
                        _consecutiveRapidServerDisconnects = 0;
                        _rapidDisconnectBackoffUntilUtc = null;
                        Log.Warning(
                            "[QueueArcade] [Cleanup] Evicted disconnected Arcade ServerID={ServerID} (Map='{MapName}', LifetimeSec={LifetimeSec:F1})",
                            serverId,
                            removed.MapName,
                            lifetimeSeconds);
                    }
                }
            }

            List<int> readySettleServerIds = [];
            foreach ((int settleServerId, DateTimeOffset retryAfterUtc) in _postMatchSettleRetryAfterUtc)
            {
                if (now >= retryAfterUtc)
                {
                    readySettleServerIds.Add(settleServerId);
                }
            }

            foreach (int disconnectedId in disconnectedServerIds)
            {
                _postMatchVictoryHoldUntilUtc.Remove(disconnectedId);
            }

            List<int> expiredQuarantineIds = [];
            foreach ((int quarantineServerId, DateTimeOffset quarantineUntilUtc) in _loadingTimeoutBackoffUntilUtc)
            {
                if (now >= quarantineUntilUtc)
                {
                    expiredQuarantineIds.Add(quarantineServerId);
                }
            }

            foreach (int quarantineServerId in expiredQuarantineIds)
            {
                _loadingTimeoutBackoffUntilUtc.Remove(quarantineServerId);
            }

            List<int> expiredVictoryHoldIds = [];
            foreach ((int holdServerId, DateTimeOffset holdUntilUtc) in _postMatchVictoryHoldUntilUtc)
            {
                if (now >= holdUntilUtc)
                {
                    expiredVictoryHoldIds.Add(holdServerId);
                }
            }

            foreach (int holdServerId in expiredVictoryHoldIds)
            {
                _postMatchVictoryHoldUntilUtc.Remove(holdServerId);
                if (!_arcadeByServerId.ContainsKey(holdServerId) &&
                    _chatContext.MatchServerChatSessions.TryGetValue(holdServerId, out ChatSession? holdSession) &&
                    holdSession.IsSessionConnected &&
                    holdSession.ServerMetadata is not null)
                {
                    holdSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
                    holdSession.ServerMetadata.MatchID = -1;
                    expiredVictoryHoldSessions.Add(holdSession);
                }
            }

            foreach (int settleServerId in readySettleServerIds)
            {
                _postMatchSettleRetryAfterUtc.Remove(settleServerId);
                if (!_arcadeByServerId.ContainsKey(settleServerId) &&
                    !_postMatchVictoryHoldUntilUtc.ContainsKey(settleServerId) &&
                    _chatContext.MatchServerChatSessions.TryGetValue(settleServerId, out ChatSession? settleSession) &&
                    settleSession.IsSessionConnected &&
                    settleSession.ServerMetadata is not null)
                {
                    settleSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
                    Log.Information(
                        "[QueueArcade] [PostMatchSettleRetry] 1.5s GamePhase 8 settle window elapsed on ServerID={ServerID}; marking IDLE to retry rewound map",
                        settleServerId);
                }
            }
        }

        foreach (ChatSession holdSession in expiredVictoryHoldSessions)
        {
            SendRemoteConsoleCommand(holdSession, EndArcadeMatchCommand);
            Log.Information(
                "[QueueArcade] [VictoryHoldExpired] Post-match Victory hold elapsed on ServerID={ServerID}; dispatched '{Command}' and marked IDLE for rotation",
                holdSession.ServerMetadata?.ServerID ?? -1,
                EndArcadeMatchCommand);
        }

        int disconnectCooldownSeconds = _configuration.GetValue("QueueArcade:ServerDisconnectRepushCooldownSeconds", 6);
        foreach (int disconnectedServerId in disconnectedServerIds)
        {
            foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
            {
                if (clientSession.ClientMetadata?.ActiveArcadeServerId == disconnectedServerId)
                {
                    clientSession.ClientMetadata.ActiveArcadeMatchId = null;
                    clientSession.ClientMetadata.ActiveArcadeServerId = null;
                    clientSession.ClientMetadata.ArcadeJoinSentAtUtc = null;
                    clientSession.ClientMetadata.LastArcadePushAttemptAtUtc = null;
                    clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = now.AddSeconds(disconnectCooldownSeconds);
                    clientSession.ClientMetadata.RankedPullWarningSent = false;

                    Log.Information(
                        "[QueueArcade] [DisconnectCooldown] Arcade ServerID={ServerID} disconnected; applied {CooldownSec}s re-push cooldown for AccountID={AccountID} ('{AccountName}') so K2 client unwinds cleanly",
                        disconnectedServerId,
                        disconnectCooldownSeconds,
                        clientSession.Account?.ID ?? -1,
                        clientSession.Account?.Name ?? "unknown");
                }
                else if (clientSession.ClientMetadata?.PostArcadeVictoryServerId == disconnectedServerId)
                {
                    clientSession.ClientMetadata.PostArcadeVictoryMatchId = null;
                    clientSession.ClientMetadata.PostArcadeVictoryServerId = null;
                    clientSession.ClientMetadata.PostArcadeVictoryEndedAtUtc = null;
                    clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = now.AddSeconds(1);
                }
            }
        }

        foreach ((ChatSession serverSession, ArcadeMatchRuntimeState state) in loadingTimedOut)
        {
            lock (_stateLock)
            {
                _arcadeByServerId.Remove(state.ServerId);
                _serverIdByMatchUpId.Remove(state.MatchUpId);
                _loadingTimeoutBackoffUntilUtc[state.ServerId] = now.AddSeconds(loadingQuarantineSeconds);
            }

            Interlocked.Increment(ref _loadingTimeoutQuarantinesTotal);
            if (serverSession.ServerMetadata is not null)
            {
                serverSession.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_KILLED;
            }

            SendRemoteConsoleCommand(serverSession, EndArcadeMatchCommand);
            SendRemoteConsoleCommand(serverSession, "Quit");

            Log.Warning(
                "[QueueArcade] [LoadingQuarantine] ServerID={ServerID} wedged in LOADING for >={TimeoutSec}s on Map='{MapName}' (MatchUpID={MatchUpID}); quarantined for {QuarantineSec}s, marked KILLED, and dispatched 'Quit' so healthy warm standby takes over immediately",
                state.ServerId,
                loadingTimeoutSeconds,
                state.MapName,
                state.MatchUpId,
                loadingQuarantineSeconds);
        }

        foreach ((ChatSession serverSession, ArcadeMatchRuntimeState state) in authDegraded)
        {
            lock (_stateLock)
            {
                _arcadeByServerId.Remove(state.ServerId);
                _serverIdByMatchUpId.Remove(state.MatchUpId);
                _postMatchSettleRetryAfterUtc[state.ServerId] = now.AddSeconds(2.0);
            }

            RefreshServerSessionCookieFromCache(serverSession);
            if (serverSession.ServerMetadata is not null)
            {
                serverSession.ServerMetadata.MatchID = -1;
            }

            SendRemoteConsoleCommand(serverSession, EndArcadeMatchCommand);

            Log.Warning(
                "[QueueArcade] [AuthDegraded] ServerID={ServerID} announced MatchUpID={MatchUpID} on Map='{MapName}' with MatchID={MatchID} (start_game auth failure); refreshed SessionCookie from cache and dispatched '{Command}' for clean 2s resync without 60s LoadingQuarantine",
                state.ServerId,
                state.MatchUpId,
                state.MapName,
                state.MatchId,
                EndArcadeMatchCommand);
        }

        foreach ((ChatSession serverSession, ArcadeMatchRuntimeState state) in roundExpired)
        {
            Interlocked.Increment(ref _roundTimerRotationsTotal);
            SendRemoteConsoleCommand(serverSession, EndArcadeMatchCommand);
            Log.Information(
                "[QueueArcade] [RoundTimer] Round duration ({RoundDuration}s) elapsed on ServerID={ServerID} MatchID={MatchID} Map='{MapName}'; dispatched '{Command}' to rotate map",
                roundDurationSeconds,
                state.ServerId,
                state.MatchId,
                state.MapName,
                EndArcadeMatchCommand);
        }

        foreach ((ChatSession serverSession, ArcadeMatchRuntimeState state) in forceRecycle)
        {
            Log.Warning(
                "[QueueArcade] [RoundTimerFallback] ServerID={ServerID} MatchID={MatchID} did not report MatchComplete within 15s of '{Command}'; recycling to next map",
                state.ServerId,
                state.MatchId,
                EndArcadeMatchCommand);
            OnArcadeMatchEnded(serverSession, state.MatchId, "round_timer_force_recycle");
        }

        EvictRegionMismatchedArcadePlayers();
        await RetryOrExpirePendingArcadeJoinsAsync(now).ConfigureAwait(false);
    }

    private async Task RetryOrExpirePendingArcadeJoinsAsync(DateTimeOffset now)
    {
        double retryIntervalSeconds = _configuration.GetValue("QueueArcade:PendingJoinRetryIntervalSeconds", 6.0);
        double pendingTimeoutSeconds = _configuration.GetValue("QueueArcade:PendingJoinTimeoutSeconds", 24.0);

        List<(ChatSession ClientSession, int AccountId, int MatchId, int ServerId, double ElapsedSec)> timedOutJoins = [];
        List<(ChatSession ClientSession, int AccountId, int MatchId, int ServerId, string TargetAddress, int TargetPort, double ElapsedSec)> retryJoins = [];
        List<(ChatSession ClientSession, int OldServerId, int NewMatchId, int NewServerId, string TargetAddress, int TargetPort, double ElapsedSec)> victoryKickFallbacks = [];

        foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
        {
            if (!clientSession.IsSessionConnected ||
                clientSession.Account is null ||
                clientSession.ClientMetadata is not
                {
                    ActiveArcadeServerId: int activeServerId,
                    ActiveArcadeMatchId: int activeMatchId,
                    ArcadeJoinSentAtUtc: DateTimeOffset firstSentUtc
                } meta)
            {
                continue;
            }

            if (meta.PendingRankedMatchId.HasValue)
            {
                continue;
            }

            bool isVictoryTransfer = meta.AwaitingVictoryTransferFromServerId.HasValue;
            bool hasAckedJoin = isVictoryTransfer
                ? (meta.ClientAckedMatchId == activeMatchId ||
                   meta.MatchID == activeMatchId ||
                   meta.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME)
                : (meta.ClientAckedMatchId == activeMatchId ||
                   meta.MatchID == activeMatchId ||
                   meta.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
                       or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
            if (hasAckedJoin)
            {
                meta.AwaitingVictoryTransferFromServerId = null;
                meta.VictoryTransferKickSent = false;
                continue;
            }

            ChatSession? serverSession = null;
            bool matchStillActive = false;
            lock (_stateLock)
            {
                if (_arcadeByServerId.TryGetValue(activeServerId, out ArcadeMatchRuntimeState? state) &&
                    state.MatchId == activeMatchId &&
                    !state.EndGameSent &&
                    _chatContext.MatchServerChatSessions.TryGetValue(activeServerId, out serverSession) &&
                    serverSession.IsSessionConnected &&
                    serverSession.ServerMetadata is not null)
                {
                    matchStillActive = true;
                }
            }

            if (!matchStillActive || serverSession?.ServerMetadata is null)
            {
                continue;
            }

            double totalElapsedSeconds = (now - firstSentUtc).TotalSeconds;
            double sinceLastAttemptSeconds = meta.LastArcadePushAttemptAtUtc.HasValue
                ? (now - meta.LastArcadePushAttemptAtUtc.Value).TotalSeconds
                : totalElapsedSeconds;

            if (pendingTimeoutSeconds > 0.0 && totalElapsedSeconds >= pendingTimeoutSeconds)
            {
                timedOutJoins.Add((clientSession, clientSession.Account.ID, activeMatchId, activeServerId, totalElapsedSeconds));
            }
            else if (meta.AwaitingVictoryTransferFromServerId is int oldVictoryServerId &&
                     !meta.VictoryTransferKickSent &&
                     totalElapsedSeconds >= 2.5)
            {
                IPAddress? clientIp = clientSession.GetRemoteClientIP();
                (string targetAddress, int targetPort) = MatchmakingService.ResolveClientTargetAddress(
                    serverSession.ServerMetadata.Address,
                    serverSession.ServerMetadata.Port,
                    serverSession.ServerMetadata.LocalAddress,
                    serverSession.ServerMetadata.LocalPort,
                    clientIp);
                victoryKickFallbacks.Add((clientSession, oldVictoryServerId, activeMatchId, activeServerId, targetAddress, targetPort, totalElapsedSeconds));
            }
            else if (retryIntervalSeconds > 0.0 && sinceLastAttemptSeconds >= retryIntervalSeconds)
            {
                IPAddress? clientIp = clientSession.GetRemoteClientIP();
                (string targetAddress, int targetPort) = MatchmakingService.ResolveClientTargetAddress(
                    serverSession.ServerMetadata.Address,
                    serverSession.ServerMetadata.Port,
                    serverSession.ServerMetadata.LocalAddress,
                    serverSession.ServerMetadata.LocalPort,
                    clientIp);
                retryJoins.Add((clientSession, clientSession.Account.ID, activeMatchId, activeServerId, targetAddress, targetPort, totalElapsedSeconds));
            }
        }

        foreach ((ChatSession clientSession, int oldServerId, int newMatchId, int newServerId, string targetAddress, int targetPort, double elapsedSec) in victoryKickFallbacks)
        {
            if (clientSession.ClientMetadata is null || clientSession.Account is null)
            {
                continue;
            }

            clientSession.ClientMetadata.VictoryTransferKickSent = true;
            clientSession.ClientMetadata.LastArcadePushAttemptAtUtc = now;
            Interlocked.Increment(ref _victoryKickFallbacksTotal);

            if (_chatContext.MatchServerChatSessions.TryGetValue(oldServerId, out ChatSession? oldSrv) &&
                oldSrv.IsSessionConnected &&
                oldSrv.ServerMetadata is not null)
            {
                string safeWithTag = SanitizeConsoleToken(clientSession.Account.GetNameWithClanTag());
                string safePlain = SanitizeConsoleToken(clientSession.Account.Name);
                string kickCmd = string.Equals(safeWithTag, safePlain, StringComparison.OrdinalIgnoreCase)
                    ? $"Kick \"{safePlain}\""
                    : $"Kick \"{safeWithTag}\";Kick \"{safePlain}\"";
                SendRemoteConsoleCommand(oldSrv, kickCmd);
            }

            await GrantArcadeAllowTicketAsync(newServerId, clientSession.Account.ID, newMatchId).ConfigureAwait(false);

            ChatBuffer retryAutoConnect = new();
            retryAutoConnect.WriteCommand(0x0062);
            retryAutoConnect.WriteInt8((byte)ChatProtocol.ArrangedMatchType.AM_UNSCHEDULED_MATCH);
            retryAutoConnect.WriteInt32(newMatchId);
            retryAutoConnect.WriteString(targetAddress);
            retryAutoConnect.WriteInt16((short)targetPort);
            retryAutoConnect.WriteInt32(_random.Next());
            clientSession.Send(retryAutoConnect);

            Log.Warning(
                "[QueueArcade] [VictoryPullKickFallback] AccountID={AccountID} ('{AccountName}') remained on OldServerID={OldServerID} {ElapsedSec:F1}s after Victory 0x0062; dispatched Kick + re-sent 0x0062 for NewServerID={NewServerID} MatchID={NewMatchID} ({Address}:{Port})",
                clientSession.Account.ID,
                clientSession.Account.Name,
                oldServerId,
                elapsedSec,
                newServerId,
                newMatchId,
                targetAddress,
                targetPort);
        }

        foreach ((ChatSession clientSession, int accountId, int matchId, int serverId, double elapsedSec) in timedOutJoins)
        {
            Interlocked.Increment(ref _pendingJoinTimeoutsTotal);
            ReleasePlayerFromArcade(accountId, "pending_join_timeout", pauseCurrentQueueWait: false);
            if (clientSession.ClientMetadata is not null)
            {
                clientSession.ClientMetadata.ArcadePushCooldownUntilUtc = now.AddSeconds(3);
            }

            Log.Warning(
                "[QueueArcade] [PendingJoinTimeout] AccountID={AccountID} ('{AccountName}') did not transition to JOINING_GAME/IN_GAME within {ElapsedSec:F1}s of Arcade MatchID={MatchID} (ServerID={ServerID}); released reserved bot slot for retry",
                accountId,
                clientSession.Account?.Name ?? "unknown",
                elapsedSec,
                matchId,
                serverId);
        }

        foreach ((ChatSession clientSession, int accountId, int matchId, int serverId, string targetAddress, int targetPort, double elapsedSec) in retryJoins)
        {
            if (clientSession.ClientMetadata is null)
            {
                continue;
            }

            Interlocked.Increment(ref _pendingJoinRetriesTotal);
            clientSession.ClientMetadata.LastArcadePushAttemptAtUtc = now;

            await GrantArcadeAllowTicketAsync(serverId, accountId, matchId).ConfigureAwait(false);

            ChatBuffer retryAutoConnect = new();
            retryAutoConnect.WriteCommand(0x0062);
            retryAutoConnect.WriteInt8((byte)ChatProtocol.ArrangedMatchType.AM_UNSCHEDULED_MATCH);
            retryAutoConnect.WriteInt32(matchId);
            retryAutoConnect.WriteString(targetAddress);
            retryAutoConnect.WriteInt16((short)targetPort);
            retryAutoConnect.WriteInt32(_random.Next());
            clientSession.Send(retryAutoConnect);

            Log.Information(
                "[QueueArcade] [PendingJoinRetry] Re-sent 0x0062 AutoMatchConnect to AccountID={AccountID} ('{AccountName}') for Arcade MatchID={MatchID} ServerID={ServerID} ({Address}:{Port}, PendingElapsed={ElapsedSec:F1}s)",
                accountId,
                clientSession.Account?.Name ?? "unknown",
                matchId,
                serverId,
                targetAddress,
                targetPort,
                elapsedSec);
        }
    }

    private void EvictRegionMismatchedArcadePlayers()
    {
        List<(ChatSession ClientSession, string ServerRegion, string SelectedRegions)> toEvict = [];

        foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
        {
            if (clientSession.ClientMetadata?.ActiveArcadeServerId is not int activeServerId ||
                clientSession.Account is null)
            {
                continue;
            }

            if (!_chatContext.MatchServerChatSessions.TryGetValue(activeServerId, out ChatSession? srv) ||
                srv.ServerMetadata is null)
            {
                continue;
            }

            MatchmakingGroup? group = _matchmakingService.GetMatchmakingGroupByMemberID(clientSession.Account.ID);
            if (group?.Information?.GameRegions is not { Length: > 0 } selectedRegions)
            {
                continue;
            }

            if (!IsArcadeServerRegionCompatible(selectedRegions, srv.ServerMetadata.Location))
            {
                toEvict.Add((
                    clientSession,
                    NormalizeServerRegion(srv.ServerMetadata.Location),
                    string.Join(", ", selectedRegions)));
            }
        }

        foreach ((ChatSession clientSession, string serverRegion, string selectedRegions) in toEvict)
        {
            ReleasePlayerFromArcade(clientSession.Account.ID, $"region_changed_away_from_{serverRegion}", pauseCurrentQueueWait: false);
            SendSystemWhisper(
                clientSession,
                $"[QUEUE ARCADE] Released '{serverRegion}' mini-game slot because your queue regions changed to [{selectedRegions}].");
        }
    }

    private int CountOptedInQueuedPlayersForRegion(string serverRegion)
    {
        int total = 0;
        foreach (MatchmakingGroup group in _matchmakingService.Groups.Values)
        {
            if (!group.QueueStartTime.HasValue ||
                group.Information is null ||
                group.Information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP)
            {
                continue;
            }

            if (!IsArcadeServerRegionCompatible(group.Information.GameRegions, serverRegion))
            {
                continue;
            }

            foreach (MatchmakingGroupMember member in group.Members)
            {
                if (member.Session is { IsSessionConnected: true, ClientMetadata.QueueArcadeOptIn: true })
                {
                    total++;
                }
            }
        }

        return total;
    }

    private int CountOptedInLobbyOrArcadeDemandAcrossAllRegions()
    {
        return CountOptedInLobbyOrArcadeDemandCore(serverRegion: null);
    }

    private int CountOptedInLobbyOrArcadeDemandForRegion(string serverRegion)
    {
        return CountOptedInLobbyOrArcadeDemandCore(serverRegion);
    }

    private int CountOptedInLobbyOrArcadeDemandCore(string? serverRegion)
    {
        int total = 0;
        foreach (ChatSession clientSession in _chatContext.ClientChatSessions.Values)
        {
            if (!clientSession.IsSessionConnected ||
                clientSession.Account is null ||
                clientSession.ClientMetadata is not { QueueArcadeOptIn: true } meta)
            {
                continue;
            }

            // Exclude players busy in or transferring to a non-Arcade (Public/Ranked) match
            if (meta.PendingRankedMatchId.HasValue ||
                (meta.MatchID.HasValue && meta.MatchID.Value != meta.ActiveArcadeMatchId) ||
                (!meta.ActiveArcadeMatchId.HasValue &&
                 meta.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                     or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME))
            {
                continue;
            }

            if (serverRegion is null)
            {
                total++;
                continue;
            }

            MatchmakingGroup? group = _matchmakingService.GetMatchmakingGroupByMemberID(clientSession.Account.ID);
            if (group?.Information is { GameRegions.Length: > 0 })
            {
                if (group.Information.GroupType == ChatProtocol.TMMType.TMM_TYPE_COOP ||
                    !IsArcadeServerRegionCompatible(group.Information.GameRegions, serverRegion))
                {
                    continue;
                }
            }
            else if (!string.IsNullOrWhiteSpace(meta.ClientRegion))
            {
                string normalizedClientRegion = NormalizeServerRegion(meta.ClientRegion);
                if (!IsRegionTokenMatch(normalizedClientRegion, serverRegion) &&
                    !IsRegionTokenMatch(serverRegion, normalizedClientRegion))
                {
                    continue;
                }
            }

            total++;
        }

        return total;
    }

    private void EnsureArcadeMatchesRunning(DateTimeOffset now)
    {
        bool effectiveEnabled = _cachedRedisEnabledOverride ?? _configuration.GetValue("QueueArcade:Enabled", true);
        if (!effectiveEnabled)
        {
            return;
        }

        ArcadeIdleMode effectiveIdleMode = ResolveEffectiveIdleMode();

        // Order by LocalPort/Port first so #Q1 (11485) is always selected as the warm primary server before #Q2 (11486)
        List<ChatSession> idleArcadeServers = _chatContext.MatchServerChatSessions.Values
            .Where(s => s.IsSessionConnected &&
                        s.ServerMetadata is { IsQueueArcade: true, Status: ChatProtocol.ServerStatus.SERVER_STATUS_IDLE })
            .OrderBy(s => s.ServerMetadata!.LocalPort > 0 ? s.ServerMetadata.LocalPort : s.ServerMetadata.Port)
            .ThenBy(s => s.ServerMetadata!.ServerID)
            .ToList();

        if (idleArcadeServers.Count == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_rapidDisconnectBackoffUntilUtc.HasValue)
            {
                if (now < _rapidDisconnectBackoffUntilUtc.Value)
                {
                    double remainingSec = (_rapidDisconnectBackoffUntilUtc.Value - now).TotalSeconds;
                    Log.Debug(
                        "[QueueArcade] [CircuitBreaker] Holding CreateMatch on {IdleCount} idle Queue Arcade server(s) for {RemainingSec:F1}s (ConsecutiveRapidDisconnects={Consecutive})",
                        idleArcadeServers.Count,
                        remainingSec,
                        _consecutiveRapidServerDisconnects);
                    return;
                }

                _rapidDisconnectBackoffUntilUtc = null;
            }
        }

        foreach (ChatSession arcadeServer in idleArcadeServers)
        {
            int serverId = arcadeServer.ServerMetadata.ServerID;
            string serverRegion = NormalizeServerRegion(arcadeServer.ServerMetadata.Location);
            int regionalQueuedPlayers = CountOptedInQueuedPlayersForRegion(serverRegion);
            int regionalLobbyDemand = CountOptedInLobbyOrArcadeDemandForRegion(serverRegion);
            int effectiveDemand = Math.Max(regionalQueuedPlayers, regionalLobbyDemand);

            string mapName;
            byte teamSize;
            int arcadeMode;
            int botDifficulty;
            int rlMode;
            int rlCandTeam;
            int matchUpId;
            bool isSelfPlayRound;

            lock (_stateLock)
            {
                if (_arcadeByServerId.ContainsKey(serverId) ||
                    arcadeServer.ServerMetadata.Status != ChatProtocol.ServerStatus.SERVER_STATUS_IDLE ||
                    (_postMatchVictoryHoldUntilUtc.TryGetValue(serverId, out DateTimeOffset victoryHoldUntil) && now < victoryHoldUntil) ||
                    (_loadingTimeoutBackoffUntilUtc.TryGetValue(serverId, out DateTimeOffset loadingBackoffUntil) && now < loadingBackoffUntil))
                {
                    continue;
                }

                // Demand-driven overflow: Keep 1 primary Arcade server warm while demand exists (or SelfPlay is ON),
                // and keep secondary overflow servers (#Q2+) idle until existing matches in that region hit >=80% fill
                // or regional queued players exceed active capacity in that region.
                int activeMatchesInRegion = 0;
                bool anyMatchUnder80Percent = false;
                int activeCapacityInRegion = 0;

                foreach (ArcadeMatchRuntimeState activeState in _arcadeByServerId.Values)
                {
                    if (activeState.EndGameSent)
                    {
                        continue;
                    }

                    // Do not let a slow-loading (>= 8s) or auth-degraded (StartedAtUtc && MatchId <= 0) server block warm standby failover!
                    bool isLoadingSlowOrAuthDegraded =
                        (!activeState.StartedAtUtc.HasValue && (now - activeState.CreatedAtUtc).TotalSeconds >= 8.0) ||
                        (activeState.StartedAtUtc.HasValue && activeState.MatchId <= 0);
                    if (isLoadingSlowOrAuthDegraded)
                    {
                        continue;
                    }

                    string activeRegion = "USE";
                    if (_chatContext.MatchServerChatSessions.TryGetValue(activeState.ServerId, out ChatSession? activeSrv) &&
                        activeSrv.ServerMetadata is not null)
                    {
                        activeRegion = NormalizeServerRegion(activeSrv.ServerMetadata.Location);
                    }

                    if (!IsRegionTokenMatch(activeRegion, serverRegion) &&
                        !IsRegionTokenMatch(serverRegion, activeRegion))
                    {
                        continue;
                    }

                    activeMatchesInRegion++;
                    int maxPlayers = activeState.TeamSize * 2;
                    int humanCount = activeState.Team1Accounts.Count + activeState.Team2Accounts.Count;
                    activeCapacityInRegion += maxPlayers;

                    int eightyPercent = Math.Max(1, (int)Math.Ceiling(maxPlayers * 0.8));
                    if (humanCount < eightyPercent)
                    {
                        anyMatchUnder80Percent = true;
                    }
                }

                if (activeMatchesInRegion > 0 && anyMatchUnder80Percent && regionalQueuedPlayers <= activeCapacityInRegion)
                {
                    continue;
                }

                if (effectiveDemand <= 0 && effectiveIdleMode != ArcadeIdleMode.SelfPlay)
                {
                    Interlocked.Increment(ref _hibernateHoldsTotal);
                    if (!_lastHibernateLoggedByServerId.TryGetValue(serverId, out ArcadeIdleMode prevLoggedMode) ||
                        prevLoggedMode != effectiveIdleMode)
                    {
                        _lastHibernateLoggedByServerId[serverId] = effectiveIdleMode;
                        Log.Information(
                            "[QueueArcade] [Hibernate] Holding ServerID={ServerID} ('{Name}', Region='{ServerRegion}') in SERVER_STATUS_IDLE (LobbyDemand={LobbyDemand}, Queued={Queued}, IdleMode={IdleMode}, ActiveMatchesInRegion={ActiveMatches}).",
                            serverId,
                            arcadeServer.Account?.Name ?? "QueueArcade",
                            serverRegion,
                            regionalLobbyDemand,
                            regionalQueuedPlayers,
                            effectiveIdleMode,
                            activeMatchesInRegion);
                    }
                    continue;
                }

                _lastHibernateLoggedByServerId.Remove(serverId);
                isSelfPlayRound = effectiveDemand <= 0 && effectiveIdleMode == ArcadeIdleMode.SelfPlay;
                if (isSelfPlayRound)
                {
                    mapName = SelectNextSelfPlayMapUnsafe();
                    teamSize = 5;
                    arcadeMode = 1;
                    botDifficulty = 3;
                    rlMode = 2;
                    rlCandTeam = _nextSelfPlayCandidateTeam;
                    _nextSelfPlayCandidateTeam = rlCandTeam == 1 ? 2 : 1;
                }
                else
                {
                    mapName = SelectNextMapUnsafe();
                    teamSize = ResolveTeamSizeForMap(mapName);
                    bool botActive = IsBotActiveMap(mapName);
                    arcadeMode = botActive ? 1 : 2;
                    int configuredBotDifficulty = Math.Clamp(_configuration.GetValue("QueueArcade:BotDifficulty", 3), 1, 3);
                    botDifficulty = botActive
                        ? (mapName.Equals("soccer", StringComparison.OrdinalIgnoreCase) ? 3 : configuredBotDifficulty)
                        : 0;
                    rlMode = botActive ? 1 : 0;
                    rlCandTeam = 0;
                }

                matchUpId = Interlocked.Decrement(ref _nextArcadeMatchUpId);

                ArcadeMatchRuntimeState runtimeState = new()
                {
                    ServerId = serverId,
                    MatchUpId = matchUpId,
                    MatchId = -1,
                    MapName = mapName,
                    TeamSize = teamSize,
                    ArcadeMode = arcadeMode,
                    CreatedAtUtc = now
                };

                _arcadeByServerId[serverId] = runtimeState;
                _serverIdByMatchUpId[matchUpId] = serverId;
                arcadeServer.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_LOADING;
                arcadeServer.ServerMetadata.MapName = mapName;
                arcadeServer.ServerMetadata.TeamSize = teamSize;
                arcadeServer.ServerMetadata.CasualMode = true;
            }

            // Configure NativeGuard arcade slot-swap mode as its own standalone command packet first,
            // followed by 500ms GamePhase 8 teardown, RL CVars & zero-penalty CVars BEFORE CreateMatch
            SendRemoteConsoleCommand(arcadeServer, $"kongor_arcade_mode {arcadeMode}");
            string preMatchCommands = string.Join(";",
            [
                "sv_gameEndPhaseTime 500",
                "sv_matchmakingDisconnectCheck false",
                "sv_afkTimeout 0",
                "sv_afkWarningTime 0",
                "svr_recordReplay false",
                "g_voteKickCooldownTime 99999999",
                "svr_submitMatchStatItems false",
                "svr_submitMatchStatAbilities false",
                "svr_submitMatchStatFrags false",
                $"Set kongor_rl_mode {rlMode}",
                $"Set kongor_rl_cand_team {rlCandTeam}"
            ]);
            SendRemoteConsoleCommand(arcadeServer, preMatchCommands);

            string matchSettings = BuildArcadeMatchSettings(mapName, teamSize, botDifficulty);

            ChatBuffer createMatch = new();
            createMatch.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);
            createMatch.WriteInt8((byte)ChatProtocol.ArrangedMatchType.AM_UNSCHEDULED_MATCH);
            createMatch.WriteInt32(matchUpId);
            createMatch.WriteInt32(0);
            createMatch.WriteInt32(0);
            createMatch.WriteString($"QUEUE ARCADE [{mapName}] #");
            createMatch.WriteString(matchSettings);
            createMatch.WriteInt8(0);
            createMatch.WriteInt8(0);
            createMatch.WriteInt8(0); // 0 pre-assigned humans; 2*teamSize bot slots are dynamically hot-reserved
            createMatch.WriteInt32(0); // GroupIds Count (mandatory K2 CServerChatConnection::ProcessPacket trailer)

            arcadeServer.Send(createMatch);
            Interlocked.Increment(ref _matchesStartedTotal);
            if (isSelfPlayRound)
            {
                Interlocked.Increment(ref _selfPlayMatchesStartedTotal);
                Log.Information(
                    "[QueueArcade] [SelfPlay] Starting 5v5 Candidate-vs-Champion arena match on ServerID={ServerID} (Region='{ServerRegion}'): MatchUpID={MatchUpID} Map='{MapName}' CandidateTeam={CandTeam} RLMode={RlMode}",
                    serverId,
                    serverRegion,
                    matchUpId,
                    mapName,
                    rlCandTeam,
                    rlMode);
            }
            else
            {
                Log.Information(
                    "[QueueArcade] [WakeUp] Demand detected on ServerID={ServerID} (Region='{ServerRegion}'): LobbyDemand={LobbyDemand} Queued={QueuedPlayers} Map='{MapName}' RLMode={RlMode}",
                    serverId,
                    serverRegion,
                    regionalLobbyDemand,
                    regionalQueuedPlayers,
                    mapName,
                    rlMode);
            }

            Log.Information(
                "[QueueArcade] [CreateMatch] Launching mini-game on ServerID={ServerID} (Region='{ServerRegion}'): MatchUpID={MatchUpID} Map='{MapName}' TeamSize={TeamSize}v{TeamSize} ArcadeMode={ArcadeMode} RegionalQueuedPlayers={QueuedPlayers} Settings='{Settings}'",
                serverId,
                serverRegion,
                matchUpId,
                mapName,
                teamSize,
                arcadeMode,
                regionalQueuedPlayers,
                matchSettings);
        }
    }

    private async Task PushEligibleQueuedGroupsAsync(DateTimeOffset now)
    {
        double minWaitSeconds = _configuration.GetValue("QueueArcade:MinQueueWaitBeforePushSeconds", 5.0);
        double botReadinessFallbackTimeoutSeconds = _configuration.GetValue<double?>("QueueArcade:BotReadinessFallbackTimeoutSeconds")
            ?? _configuration.GetValue("QueueArcade:MinMatchWarmupBeforePushSeconds", 5.0);
        double inflightHandshakeGuardSeconds = _configuration.GetValue("QueueArcade:InflightHandshakeGuardSeconds", 16.0);
        int configuredRankedTeamSize = (_matchmakingService as MatchmakingService)?.ResolveEffectivePlayersPerTeam()
                                       ?? _cachedRedisPlayersPerTeamOverride
                                       ?? _configuration.GetValue<int?>("Matchmaking:PlayersPerTeam")
                                       ?? SharedConfiguration.MatchmakingConfiguration?.Ranked?.Match?.TeamSize
                                       ?? 5;
        if (configuredRankedTeamSize <= 0)
        {
            configuredRankedTeamSize = 5;
        }

        int targetRankedMatchPlayers = configuredRankedTeamSize * 2;
        int defaultWastedLoadThreshold = Math.Max(2, (int)Math.Ceiling(targetRankedMatchPlayers * 0.8));
        int wastedLoadThreshold = _configuration.GetValue("QueueArcade:WastedLoadPlayerThreshold", defaultWastedLoadThreshold);

        List<MatchmakingGroup> queuedGroups = _matchmakingService.Groups.Values
            .Where(g => g.QueueStartTime.HasValue &&
                        g.Information is not null &&
                        g.Information.GroupType != ChatProtocol.TMMType.TMM_TYPE_COOP)
            .OrderBy(g => g.QueueStartTime)
            .ToList();

        if (queuedGroups.Count == 0)
        {
            return;
        }

        foreach (MatchmakingGroup group in queuedGroups)
        {
            if (!group.QueueStartTime.HasValue)
            {
                continue;
            }

            double waitedSeconds = (now - group.QueueStartTime.Value).TotalSeconds;
            if (waitedSeconds < minWaitSeconds)
            {
                continue;
            }

            List<MatchmakingGroupMember> eligibleMembers = [];
            bool groupHasInflightOrCoolingMember = false;
            foreach (MatchmakingGroupMember m in group.Members)
            {
                if (m.Session is not { IsSessionConnected: true, ClientMetadata: not null })
                {
                    continue;
                }

                ClientChatSessionMetadata meta = m.Session.ClientMetadata;
                if (!meta.QueueArcadeOptIn ||
                    meta.ActiveArcadeMatchId.HasValue ||
                    meta.MatchID is not null ||
                    meta.PauseArcadeUntilNextQueueUtc == group.QueueStartTime)
                {
                    continue;
                }

                bool isCoolingDown =
                    meta.ArcadePushCooldownUntilUtc.HasValue && now < meta.ArcadePushCooldownUntilUtc.Value;
                bool isReadyForVictoryAutoPull =
                    meta.PostArcadeVictoryMatchId.HasValue &&
                    meta.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME &&
                    !isCoolingDown;
                bool isInflightState =
                    meta.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME ||
                    (meta.LastKnownClientState == ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME && !isReadyForVictoryAutoPull);
                bool isRecentPushAttempt =
                    meta.LastArcadePushAttemptAtUtc.HasValue &&
                    (now - meta.LastArcadePushAttemptAtUtc.Value).TotalSeconds < inflightHandshakeGuardSeconds;

                if (isInflightState || isCoolingDown || isRecentPushAttempt)
                {
                    groupHasInflightOrCoolingMember = true;
                    Interlocked.Increment(ref _inflightOrCooldownSkipsTotal);
                    Log.Debug(
                        "[QueueArcade] [InflightGuard] Deferring Arcade push for Group {GroupGuid} (AccountID={AccountID} '{AccountName}'): State={ClientState} CoolingDown={CoolingDown} RecentPushAttempt={RecentPush}",
                        group.GUID,
                        m.Account.ID,
                        m.Account.Name,
                        meta.LastKnownClientState,
                        isCoolingDown,
                        isRecentPushAttempt);
                    break;
                }

                eligibleMembers.Add(m);
            }

            if (groupHasInflightOrCoolingMember || eligibleMembers.Count == 0)
            {
                continue;
            }

            // Wasted-Load Queue-ETA Gate: do not push players into a map load right before their Ranked match pops!
            int compatibleQueuedPlayers = CountCompatibleQueuedPlayers(group, queuedGroups);
            if (compatibleQueuedPlayers >= wastedLoadThreshold)
            {
                Interlocked.Increment(ref _wastedLoadBlocksTotal);
                Log.Information(
                    "[QueueArcade] [WastedLoadGate] Skipping Arcade push for Group {GroupGuid} ({EligibleCount} players): bucket has {CompatiblePlayers}/{TargetPlayers} compatible queued players (>= threshold {Threshold})",
                    group.GUID,
                    eligibleMembers.Count,
                    compatibleQueuedPlayers,
                    targetRankedMatchPlayers,
                    wastedLoadThreshold);
                continue;
            }

            string? preferredClientRegion = eligibleMembers[0].Session?.ClientMetadata?.ClientRegion;
            foreach (MatchmakingGroupMember m in group.Members)
            {
                if (m.IsLeader && !string.IsNullOrWhiteSpace(m.Session?.ClientMetadata?.ClientRegion))
                {
                    preferredClientRegion = m.Session.ClientMetadata.ClientRegion;
                    break;
                }
            }

            // Select a ready Arcade match IN A COMPATIBLE REGION with capacity for the entire party on the same team
            ChatSession? targetServerSession = null;
            ArcadeMatchRuntimeState? chosenState = null;
            int targetTeam = 1;
            bool sawIncompatibleRegionalServer = false;
            bool sawWarmingServer = false;
            int[] challengeSalts = [];

            lock (_stateLock)
            {
                bool bestIsHomePingMatch = false;
                int bestHumanCount = -1;

                List<ArcadeMatchRuntimeState> orderedCandidates = _arcadeByServerId.Values
                    .OrderBy(c =>
                        _chatContext.MatchServerChatSessions.TryGetValue(c.ServerId, out ChatSession? s) && s.ServerMetadata is not null
                            ? (s.ServerMetadata.LocalPort > 0 ? s.ServerMetadata.LocalPort : s.ServerMetadata.Port)
                            : int.MaxValue)
                    .ThenBy(c => c.ServerId)
                    .ToList();

                foreach (ArcadeMatchRuntimeState candidate in orderedCandidates)
                {
                    if (candidate.MatchId <= 0 || candidate.EndGameSent)
                    {
                        continue;
                    }

                    if (!_chatContext.MatchServerChatSessions.TryGetValue(candidate.ServerId, out ChatSession? srv) ||
                        !srv.IsSessionConnected ||
                        srv.ServerMetadata is null)
                    {
                        continue;
                    }

                    // Strict Region Gate: never pull a queued player into a Queue Arcade server outside their selected region(s)!
                    if (!IsArcadeServerRegionCompatible(group.Information.GameRegions, srv.ServerMetadata.Location))
                    {
                        sawIncompatibleRegionalServer = true;
                        continue;
                    }

                    // Post-LoadWorld Bot-Seeding Warmup & Event-Driven Readiness Gate:
                    if (candidate.ArcadeMode != 1 || candidate.ExpectedBotCount == 0)
                    {
                        if (candidate.StartedAtUtc.HasValue && (now - candidate.StartedAtUtc.Value).TotalSeconds >= 0.25)
                        {
                            candidate.BotsSeededReady = true;
                            candidate.BotsSeededReadyAtUtc ??= now;
                        }
                    }

                    if (!candidate.BotsSeededReady)
                    {
                        double elapsedSinceStart = candidate.StartedAtUtc.HasValue
                            ? (now - candidate.StartedAtUtc.Value).TotalSeconds
                            : 0.0;

                        if (candidate.StartedAtUtc.HasValue && elapsedSinceStart >= botReadinessFallbackTimeoutSeconds)
                        {
                            candidate.BotsSeededReady = true;
                            candidate.BotsSeededReadyAtUtc = now;
                            Interlocked.Increment(ref _botReadinessFallbackTimeoutsTotal);
                            Log.Warning(
                                "[QueueArcade] [BotReadiness] Fallback timeout ({TimeoutSeconds:F1}s) reached for ServerID={ServerID} MatchID={MatchID}. Marking bots ready (Reported={Reported}/{Expected})",
                                botReadinessFallbackTimeoutSeconds,
                                candidate.ServerId,
                                candidate.MatchId,
                                candidate.ReportedBotCount,
                                candidate.ExpectedBotCount);
                        }
                        else
                        {
                            sawWarmingServer = true;
                            continue;
                        }
                    }

                    bool fitsTeam1 = candidate.Team1Accounts.Count + eligibleMembers.Count <= candidate.TeamSize;
                    bool fitsTeam2 = candidate.Team2Accounts.Count + eligibleMembers.Count <= candidate.TeamSize;
                    if (!fitsTeam1 && !fitsTeam2)
                    {
                        continue;
                    }

                    string normalizedServerRegion = NormalizeServerRegion(srv.ServerMetadata.Location);
                    bool isHomePingMatch = !string.IsNullOrWhiteSpace(preferredClientRegion) &&
                                           IsRegionTokenMatch(preferredClientRegion, normalizedServerRegion);
                    int candidateHumanCount = candidate.Team1Accounts.Count + candidate.Team2Accounts.Count;

                    // Multi-region tie-breaker: prefer player's home ClientRegion (lowest ping) first,
                    // then pack players into the most populated active regional match (#Q1) before spilling onto #Q2.
                    bool isBetterChoice = chosenState is null ||
                                          (isHomePingMatch && !bestIsHomePingMatch) ||
                                          (isHomePingMatch == bestIsHomePingMatch && candidateHumanCount > bestHumanCount);
                    if (!isBetterChoice)
                    {
                        continue;
                    }

                    bestIsHomePingMatch = isHomePingMatch;
                    bestHumanCount = candidateHumanCount;
                    targetTeam = fitsTeam1 && fitsTeam2
                        ? (candidate.Team1Accounts.Count <= candidate.Team2Accounts.Count ? 1 : 2)
                        : (fitsTeam1 ? 1 : 2);
                    chosenState = candidate;
                    targetServerSession = srv;
                }

                if (chosenState is not null)
                {
                    HashSet<int> teamSet = targetTeam == 1 ? chosenState.Team1Accounts : chosenState.Team2Accounts;
                    challengeSalts = new int[eligibleMembers.Count];
                    for (int idx = 0; idx < eligibleMembers.Count; idx++)
                    {
                        teamSet.Add(eligibleMembers[idx].Account.ID);
                        challengeSalts[idx] = _random.Next();
                    }
                }
            }

            if (chosenState is null || targetServerSession is null)
            {
                if (sawWarmingServer)
                {
                    Interlocked.Increment(ref _warmupDeferralsTotal);
                    Log.Debug(
                        "[QueueArcade] [WarmupGate] Deferring Arcade push for Group {GroupGuid} ({EligibleCount} players): regional Queue Arcade server is finishing post-LoadWorld bot seeding warmup ({WarmupSec:F1}s fallback)",
                        group.GUID,
                        eligibleMembers.Count,
                        botReadinessFallbackTimeoutSeconds);
                }
                else if (sawIncompatibleRegionalServer)
                {
                    Interlocked.Increment(ref _regionMismatchSkipsTotal);
                    Log.Debug(
                        "[QueueArcade] [RegionGate] Skipping Arcade push for Group {GroupGuid} (SelectedRegions=[{SelectedRegions}]): no active Queue Arcade server in matching region",
                        group.GUID,
                        string.Join(", ", group.Information.GameRegions ?? []));
                }
                continue;
            }

            string matchedServerRegion = NormalizeServerRegion(targetServerSession.ServerMetadata.Location);

            // 1) Populate K2's native CHostServer::m_mapArrangedRoster (+0x4630) FIRST via 0x0517 so c_conn IsOnRoster passes!
            SendDynamicRosterAddition(targetServerSession, eligibleMembers);

            // 2) Dispatch slot reservations SECOND on the same TCP control channel so NativeGuard claims CPlayer slots before UDP 'P' arrives
            List<string> reserveCommands = new(eligibleMembers.Count);
            foreach (MatchmakingGroupMember member in eligibleMembers)
            {
                string safeName = SanitizeConsoleToken(member.Account.GetNameWithClanTag());
                reserveCommands.Add($"kongor_arcade_reserve {member.Account.ID} {targetTeam} \"{safeName}\"");
            }
            SendRemoteConsoleCommand(targetServerSession, string.Join(";", reserveCommands));

            // 3) Dispatch 0x0062 (AM_UNSCHEDULED_MATCH = 3) THIRD to each opted-in party member while preserving group.QueueStartTime!
            for (int idx = 0; idx < eligibleMembers.Count; idx++)
            {
                MatchmakingGroupMember member = eligibleMembers[idx];
                IPAddress? clientIp = member.Session.GetRemoteClientIP();
                (string targetAddress, int targetPort) = MatchmakingService.ResolveClientTargetAddress(
                    targetServerSession.ServerMetadata.Address,
                    targetServerSession.ServerMetadata.Port,
                    targetServerSession.ServerMetadata.LocalAddress,
                    targetServerSession.ServerMetadata.LocalPort,
                    clientIp);

                bool wasOnVictoryScreen = member.Session.ClientMetadata.PostArcadeVictoryMatchId.HasValue;
                int? previousVictoryServerId = member.Session.ClientMetadata.PostArcadeVictoryServerId;

                member.Session.ClientMetadata.ActiveArcadeMatchId = chosenState.MatchId;
                member.Session.ClientMetadata.ActiveArcadeServerId = chosenState.ServerId;
                member.Session.ClientMetadata.ArcadeJoinSentAtUtc = now;
                member.Session.ClientMetadata.LastArcadePushAttemptAtUtc = now;
                member.Session.ClientMetadata.ArcadePushCooldownUntilUtc = null;
                member.Session.ClientMetadata.RankedPullWarningSent = false;
                member.Session.ClientMetadata.PostArcadeVictoryMatchId = null;
                member.Session.ClientMetadata.PostArcadeVictoryServerId = null;
                member.Session.ClientMetadata.PostArcadeVictoryEndedAtUtc = null;
                member.Session.ClientMetadata.AwaitingVictoryTransferFromServerId = wasOnVictoryScreen ? previousVictoryServerId : null;
                member.Session.ClientMetadata.VictoryTransferKickSent = false;

                if (wasOnVictoryScreen)
                {
                    Interlocked.Increment(ref _victoryAutoPullsTotal);
                    Log.Information(
                        "[QueueArcade] [VictoryPull] Auto-pulling AccountID={AccountID} ('{AccountName}') directly from Victory screen (OldServerID={OldServerID}) -> Ready Arcade ServerID={NewServerID} MatchID={MatchID} Map='{MapName}'",
                        member.Account.ID,
                        member.Account.Name,
                        previousVictoryServerId ?? -1,
                        chosenState.ServerId,
                        chosenState.MatchId,
                        chosenState.MapName);
                }

                SendSystemWhisper(
                    member.Session,
                    $"[QUEUE ARCADE] Joining '{chosenState.MapName}' [{matchedServerRegion}] (Team {(targetTeam == 1 ? "Legion" : "Hellbourne")}) while you wait in queue! Leave anytime or type /arcade off.");

                await GrantArcadeAllowTicketAsync(chosenState.ServerId, member.Account.ID, chosenState.MatchId).ConfigureAwait(false);

                ChatBuffer autoConnect = new();
                autoConnect.WriteCommand(0x0062);
                autoConnect.WriteInt8((byte)ChatProtocol.ArrangedMatchType.AM_UNSCHEDULED_MATCH);
                autoConnect.WriteInt32(chosenState.MatchId);
                autoConnect.WriteString(targetAddress);
                autoConnect.WriteInt16((short)targetPort);
                autoConnect.WriteInt32(challengeSalts[idx]);

                member.Session.Send(autoConnect);
                Interlocked.Increment(ref _playersPushedTotal);

                Log.Information(
                    "[QueueArcade] [AutoPush] Pushed AccountID={AccountID} ('{AccountName}', Group={GroupGuid}, SelectedRegions=[{SelectedRegions}]) -> Arcade ServerID={ServerID} Region='{ServerRegion}' MatchID={MatchID} Map='{MapName}' Team={Team} Endpoint={Address}:{Port} (QueueWait={WaitSec:F1}s, QueueRetained={QueueRetained})",
                    member.Account.ID,
                    member.Account.Name,
                    group.GUID,
                    string.Join(",", group.Information.GameRegions ?? []),
                    chosenState.ServerId,
                    matchedServerRegion,
                    chosenState.MatchId,
                    chosenState.MapName,
                    targetTeam,
                    targetAddress,
                    targetPort,
                    waitedSeconds,
                    group.QueueStartTime.HasValue);
            }
        }
    }

    private async Task GrantArcadeAllowTicketAsync(int serverId, int accountId, int matchId)
    {
        if (_distributedCache is null || serverId <= 0 || accountId <= 0)
        {
            return;
        }

        string allowKey = MatchServer.GetQueueArcadeAllowKey(serverId, accountId);
        try
        {
            await _distributedCache.StringSetAsync(
                allowKey,
                matchId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArcadeAllowTicketTtl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[QueueArcade] [AllowTicket] Failed to write '{AllowKey}' (MatchID={MatchID}) to distributed cache",
                allowKey,
                matchId);
        }
    }

    private void RevokeArcadeAllowTicketFireAndForget(int serverId, int accountId, string reason)
    {
        if (_distributedCache is null || serverId <= 0 || accountId <= 0)
        {
            return;
        }

        string allowKey = MatchServer.GetQueueArcadeAllowKey(serverId, accountId);
        try
        {
            Task<bool> deleteTask = _distributedCache.KeyDeleteAsync(allowKey);
            _ = deleteTask.ContinueWith(
                t => Log.Warning(
                    t.Exception,
                    "[QueueArcade] [AllowTicket] Failed to revoke '{AllowKey}' (Reason='{Reason}')",
                    allowKey,
                    reason),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[QueueArcade] [AllowTicket] Failed to initiate delete for '{AllowKey}' (Reason='{Reason}')",
                allowKey,
                reason);
        }
    }

    private static int CountCompatibleQueuedPlayers(MatchmakingGroup targetGroup, List<MatchmakingGroup> allQueuedGroups)
    {
        int total = 0;
        string[] targetRegions = targetGroup.Information.GameRegions ?? [];

        foreach (MatchmakingGroup candidate in allQueuedGroups)
        {
            if (candidate.Information.GroupType != targetGroup.Information.GroupType ||
                candidate.Information.GameType != targetGroup.Information.GameType ||
                !string.Equals(candidate.Information.MapName, targetGroup.Information.MapName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] candidateRegions = candidate.Information.GameRegions ?? [];
            bool regionsOverlap = targetRegions.Length == 0 ||
                                  candidateRegions.Length == 0 ||
                                  targetRegions.Any(r => candidateRegions.Contains(r, StringComparer.OrdinalIgnoreCase));

            if (regionsOverlap)
            {
                total += candidate.Members.Count;
            }
        }

        return total;
    }

    private static void SendDynamicRosterAddition(ChatSession serverSession, List<MatchmakingGroupMember> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        ChatBuffer rosterPacket = new();
        rosterPacket.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_PLAYER_BENEFITS);
        int clampedCount = Math.Min(members.Count, byte.MaxValue);
        rosterPacket.WriteInt8((byte)clampedCount);
        for (int i = 0; i < clampedCount; i++)
        {
            rosterPacket.WriteInt32(members[i].Account.ID);
            rosterPacket.WriteInt8(0);
        }

        serverSession.Send(rosterPacket);
        Log.Information(
            "[QueueArcade] [RosterSync] Dispatched 0x0517 dynamic roster addition ({Count} player(s): [{AccountIds}]) -> ServerID={ServerID}",
            clampedCount,
            string.Join(",", members.Take(clampedCount).Select(m => m.Account.ID)),
            serverSession.ServerMetadata?.ServerID ?? 0);
    }

    private void SendRemoteConsoleCommand(ChatSession serverSession, string commandString)
    {
        RefreshServerSessionCookieFromCache(serverSession);

        ChatBuffer packet = new();
        packet.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);
        packet.WriteString(serverSession.ServerMetadata?.SessionCookie ?? string.Empty);
        packet.WriteString(commandString);
        serverSession.Send(packet);
    }

    private async Task RefreshConnectedArcadeServerSessionCookiesAsync()
    {
        if (_distributedCache is null)
        {
            return;
        }

        foreach (ChatSession serverSession in _chatContext.MatchServerChatSessions.Values)
        {
            if (!serverSession.IsSessionConnected || serverSession.ServerMetadata is not { IsQueueArcade: true, ServerID: > 0 } meta)
            {
                continue;
            }

            try
            {
                ASPIRE.Common.Models.ServerManagement.MatchServer? cached =
                    await _distributedCache.GetMatchServerByID(meta.ServerID).ConfigureAwait(false);
                ApplyCachedServerSessionCookie(serverSession, cached);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[QueueArcade] [SessionSync] Non-fatal async cache lookup failure for ServerID={ServerID}", meta.ServerID);
            }
        }
    }

    private void RefreshServerSessionCookieFromCache(ChatSession serverSession)
    {
        if (_distributedCache is null || serverSession.ServerMetadata is not { ServerID: > 0 } meta)
        {
            return;
        }

        try
        {
            StackExchange.Redis.RedisValue hashField = _distributedCache.HashGet("MATCH-SERVER-INDEX", meta.ServerID);
            if (hashField.IsNullOrEmpty)
            {
                return;
            }

            StackExchange.Redis.RedisValue rawBytes = _distributedCache.HashGet("MATCH-SERVERS", hashField);
            if (rawBytes.IsNullOrEmpty)
            {
                return;
            }

            byte[] bytes = (byte[])rawBytes!;
            if (bytes.Length == 0)
            {
                return;
            }

            ASPIRE.Common.Models.ServerManagement.MatchServer? cached = bytes[0] == (byte)'{'
                ? System.Text.Json.JsonSerializer.Deserialize(bytes, Kongor.Core.Serialization.JSON.KongorCoreJsonContext.Default.MatchServer)
                : MemoryPack.MemoryPackSerializer.Deserialize<ASPIRE.Common.Models.ServerManagement.MatchServer>(bytes);
            ApplyCachedServerSessionCookie(serverSession, cached);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[QueueArcade] [SessionSync] Non-fatal cache lookup failure for ServerID={ServerID}", meta.ServerID);
        }
    }

    private void ApplyCachedServerSessionCookie(
        ChatSession serverSession,
        ASPIRE.Common.Models.ServerManagement.MatchServer? cached)
    {
        if (cached is null || string.IsNullOrWhiteSpace(cached.Cookie) || serverSession.ServerMetadata is not { ServerID: > 0 } meta)
        {
            return;
        }

        bool hasLastRegistered = _lastCookieRegisteredAtByServerId.TryGetValue(meta.ServerID, out DateTimeOffset lastRegisteredAt);
        bool isActiveOrLoadingEngineState =
            cached.Status is ASPIRE.Common.Models.ServerManagement.ServerStatus.SERVER_STATUS_LOADING
                or ASPIRE.Common.Models.ServerManagement.ServerStatus.SERVER_STATUS_ACTIVE;
        bool isNewerEngineSession =
            (!hasLastRegistered || cached.TimestampRegistered > lastRegisteredAt) &&
            (isActiveOrLoadingEngineState || string.IsNullOrEmpty(cached.Description));

        if (isActiveOrLoadingEngineState || isNewerEngineSession)
        {
            if (!hasLastRegistered || cached.TimestampRegistered > lastRegisteredAt)
            {
                _lastCookieRegisteredAtByServerId[meta.ServerID] = cached.TimestampRegistered;
            }

            if (!string.Equals(cached.Cookie, meta.SessionCookie, StringComparison.Ordinal))
            {
                Log.Information(
                    "[QueueArcade] [SessionSync] Refreshed in-process ServerID={ServerID} SessionCookie from MasterServer cache (Status={Status}, RegisteredAt={RegisteredAt:O})",
                    meta.ServerID,
                    cached.Status,
                    cached.TimestampRegistered);
                meta.SessionCookie = cached.Cookie;
            }
        }
    }

    private static void SendSystemWhisper(ChatSession clientSession, string message)
    {
        ChatBuffer whisper = new();
        whisper.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisper.WriteString("System");
        whisper.WriteString(message);
        clientSession.Send(whisper);
    }

    private static string SanitizeConsoleToken(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        StringBuilder sb = new(raw.Length);
        foreach (char c in raw)
        {
            if (c is not ('"' or ';' or '\r' or '\n' or '\\'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
