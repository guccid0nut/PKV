// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Globalization;
using System.Threading.Channels;
using Kongor.Core.Services.Session;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace COMPEL.Services.Telemetry;

public sealed class CompelTelemetryRelayService : IAsyncDisposable
{
    private const float WorldMapDimension = 16384.0f;

    private static readonly SearchValues<char> SanitizeTriggerChars = SearchValues.Create("\0\x1b\r\n");

    private static readonly SearchValues<string> TelemetryFilterSearchValues = SearchValues.Create(
        [
            "Sv:",
            "StartGame",
            "Voice:",
            "EXP_EARNED",
            "GOLD_EARNED",
            "HERO_LEVEL",
            "PLAYER_SELECT",
            "PLAYER_RANDOM",
            "PLAYER_CONNECT",
            "PLAYER_TEAM_CHANGE",
            "ITEM_PURCHASE",
            "ITEM_ASSEMBLE",
            "ITEM_SELL",
            "ABILITY_ACTIVATE",
            "ABILITY_UPGRADE",
            "ITEM_ACTIVATE",
            "KILL",
            "Name:",
            "Client",
            "MatchID",
            "match_id",
            "match ID",
            "[ALL]",
            "[TEAM]"
        ],
        StringComparison.Ordinal);

    private static readonly string[] s_defaultPlayerNames =
        Enumerable.Range(1, 32).Select(i => $"Player #{i}").ToArray();

    public static string GetDefaultPlayerName(int slot) =>
        (uint)slot < (uint)s_defaultPlayerNames.Length ? s_defaultPlayerNames[slot] : $"Player #{slot + 1}";

    private static readonly Dictionary<string, string> s_stringPool = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> s_stringPoolLookup =
        s_stringPool.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly Lock s_stringPoolLock = new();

    public static string GetOrInternString(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return string.Empty;
        lock (s_stringPoolLock)
        {
            if (s_stringPoolLookup.TryGetValue(span, out string? cached))
                return cached;
            string created = span.ToString();
            s_stringPoolLookup[span] = created;
            return created;
        }
    }

    private static readonly Dictionary<string, string> s_heroCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> s_heroCacheLookup =
        s_heroCache.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly Lock s_heroCacheLock = new();

    public static string NormalizeHeroIdentifier(ReadOnlySpan<char> rawHero)
    {
        rawHero = rawHero.Trim();
        if (rawHero.IsEmpty) return string.Empty;

        lock (s_heroCacheLock)
        {
            if (s_heroCacheLookup.TryGetValue(rawHero, out string? cached))
                return cached;

            ReadOnlySpan<char> slice = rawHero;
            if (slice.StartsWith("hero_", StringComparison.OrdinalIgnoreCase))
                slice = slice[5..].Trim();

            string normalized = "hero_" + slice.ToString().ToLowerInvariant();
            s_heroCacheLookup[rawHero] = normalized;
            return normalized;
        }
    }

    private static readonly ConcurrentDictionary<string, string> s_itemDisplayNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetCleanItemDisplayName(string item) =>
        s_itemDisplayNameCache.GetOrAdd(item, static key => key.Replace("Item_", "").Replace("_", " "));

    private static readonly Dictionary<string, string[]> ItemRecipeComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Item_BloodChalice"] =
        [
            "Item_Scarab", "Item_CrushingClaws", "Item_MarkOfTheNovice", "Item_Recipe_BloodChalice",
            "Item_BloodChalice_Recipe"
        ],
        ["Item_Astrolabe"] =
        [
            "Item_Lifetube", "Item_MajorTotem", "Item_Recipe_Astrolabe", "Item_Astrolabe_Recipe"
        ],
        ["Item_GhostMarchers"] =
        [
            "Item_Marchers", "Item_Punchdagger", "Item_Recipe_GhostMarchers", "Item_GhostMarchers_Recipe"
        ],
        ["Item_Steamboots"] =
        [
            "Item_Marchers", "Item_Fleetfeet", "Item_Recipe_Steamboots", "Item_Steamboots_Recipe"
        ],
        ["Item_PlatedGreaves"] =
        [
            "Item_Marchers", "Item_Ringmail", "Item_Recipe_PlatedGreaves", "Item_PlatedGreaves_Recipe"
        ],
        ["Item_PostHaste"] =
        [
            "Item_Marchers", "Item_Recipe_PostHaste", "Item_PostHaste_Recipe"
        ],
        ["Item_SolsBulwark"] =
        [
            "Item_Ringmail", "Item_RingOfSorcery", "Item_Recipe_SolsBulwark", "Item_SolsBulwark_Recipe"
        ],
        ["Item_DaemonicBreastplate"] =
        [
            "Item_SolsBulwark", "Item_Warpcft", "Item_Chainmail", "Item_Recipe_DaemonicBreastplate",
            "Item_DaemonicBreastplate_Recipe"
        ],
        ["Item_BarrierIdol"] =
        [
            "Item_MysticVestments", "Item_HecklersTalisman", "Item_Recipe_BarrierIdol", "Item_BarrierIdol_Recipe"
        ],
        ["Item_NullStone"] =
        [
            "Item_Perseverance", "Item_AcolytesStaff", "Item_Recipe_NullStone", "Item_NullStone_Recipe"
        ]
    };

    private readonly AddressResolver _addressResolver;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _httpClient;
    private readonly HubConnection? _hubConnection;
    private readonly ILogger<CompelTelemetryRelayService> _logger;

    private readonly ConcurrentDictionary<int, MatchTelemetryState> _matchStates = new();
    private readonly ConcurrentDictionary<int, int> _workerPortToMatchMap = new();

    private readonly MatchServerManagerOptions _options;
    private readonly IRedisMatchSessionCacheService? _sessionCache;
    private readonly Task _processTask;

    private readonly Channel<LiveServerTickPayload> _telemetryChannel;
    private int _latestActiveMatchId;
    private long _droppedTelemetryTicks;

    public long DroppedTelemetryTicks => Interlocked.Read(ref _droppedTelemetryTicks);

    public CompelTelemetryRelayService(
        AddressResolver addressResolver,
        ILogger<CompelTelemetryRelayService> logger,
        IHostEnvironment? environment = null,
        IOptions<MatchServerManagerOptions>? options = null,
        IRedisMatchSessionCacheService? sessionCache = null,
        IConfiguration? configuration = null)
    {
        _addressResolver = addressResolver;
        _logger = logger;
        _options = options?.Value ?? new MatchServerManagerOptions();
        _sessionCache = sessionCache;

        SocketsHttpHandler socketsHttpHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
        };
        _httpClient = new HttpClient(socketsHttpHandler) { Timeout = TimeSpan.FromSeconds(3) };

        string? internalKey = configuration?["InternalServiceKey"] ?? Environment.GetEnvironmentVariable("InternalServiceKey");
        if (string.IsNullOrWhiteSpace(internalKey) && (environment?.IsDevelopment() ?? false))
        {
            internalKey = NEXUS.Contracts.Security.SecurityConstants.DefaultInternalServiceKey;
        }

        if (!string.IsNullOrWhiteSpace(internalKey))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                NEXUS.Contracts.Security.SecurityConstants.InternalServiceKeyHeader,
                internalKey);
        }

        string hubUrl = !string.IsNullOrWhiteSpace(_options.LiveHubUrl)
            ? _options.LiveHubUrl
            : "https://localhost:5557/hubs/live-matches";

        bool allowUntrusted = (environment?.IsDevelopment() ?? false)
            || _options.AllowUntrustedCertificates
            || hubUrl.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase)
            || hubUrl.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase);

        try
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl, httpOptions =>
                {
                    if (allowUntrusted)
                    {
                        httpOptions.HttpMessageHandlerFactory = handler =>
                        {
                            if (handler is HttpClientHandler clientHandler)
                                clientHandler.ServerCertificateCustomValidationCallback =
                                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                            return handler;
                        };
                    }
                })
                .WithAutomaticReconnect([
                    TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)
                ])
                .Build();

            _hubConnection.Reconnecting += error =>
            {
                _logger.LogWarning(error, "[CompelTelemetry] SignalR Hub connection lost, reconnecting...");
                return Task.CompletedTask;
            };

            _hubConnection.Reconnected += connectionId =>
            {
                _logger.LogInformation(
                    "[CompelTelemetry] SignalR Hub connection restored. ConnectionId: {ConnectionId}", connectionId);
                return Task.CompletedTask;
            };

            _hubConnection.Closed += error =>
            {
                _logger.LogWarning(error, "[CompelTelemetry] SignalR Hub connection closed");
                return Task.CompletedTask;
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CompelTelemetry] Could not initialize SignalR HubConnection to {HubUrl}", hubUrl);
        }

        BoundedChannelOptions channelOptions = new(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _telemetryChannel = Channel.CreateBounded<LiveServerTickPayload>(channelOptions);
        _processTask = Task.Run(ProcessTelemetryQueueAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _telemetryChannel.Writer.TryComplete();
        await _cts.CancelAsync();
        try
        {
            await _processTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CompelTelemetry] Process task ended during DisposeAsync.");
        }

        if (_hubConnection != null)
            try
            {
                await _hubConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CompelTelemetry] HubConnection dispose error.");
            }

        _cts.Dispose();
        _httpClient.Dispose();
    }

    private MatchTelemetryState GetOrCreateMatchState(int matchId)
    {
        int key = matchId > 0 ? matchId : _latestActiveMatchId;
        return _matchStates.GetOrAdd(key, id => new MatchTelemetryState { MatchID = id });
    }

    public void OnServerStdoutLine(string rawLine)
    {
        OnServerStdoutLine(0, rawLine, 0);
    }

    public void OnServerStdoutLine(int sourceMatchIdOrPort, string rawLine)
    {
        OnServerStdoutLine(sourceMatchIdOrPort, rawLine, sourceMatchIdOrPort >= 1000 ? sourceMatchIdOrPort : 0);
    }

    public void OnServerStdoutLine(int sourceMatchId, string rawLine, int workerPort)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return;
        // Sanitize terminal overwrites (\r), ANSI/VT escapes, and UTF-16LE null bytes BEFORE filter/MatchID parsing
        if (rawLine.AsSpan().IndexOfAny(SanitizeTriggerChars) >= 0)
        {
            foreach (string segment in CompelStreamSanitizer.SanitizeLines(rawLine))
            {
                OnServerStdoutLine(sourceMatchId, segment, workerPort);
            }
            return;
        }

        ReadOnlySpan<char> lineSpan = rawLine.AsSpan().Trim();
        if (lineSpan.IsEmpty) return;

        // 0. High-performance SIMD multi-pattern short circuit (zero allocations)
        if (lineSpan.IndexOfAny(TelemetryFilterSearchValues) < 0)
            return;

        CompelMetrics.TelemetryLogLinesProcessed.Add(1);

        int parsedMatchId = 0;
        if (FastCombatLogParser.TryParseMatchId(lineSpan, out int mid))
        {
            parsedMatchId = mid;
            _latestActiveMatchId = mid;
            if (workerPort > 0)
                _workerPortToMatchMap[workerPort] = mid;
            else if (sourceMatchId >= 1000)
                _workerPortToMatchMap[sourceMatchId] = mid;
            _logger.LogInformation("[CompelTelemetry] Captured Match ID #{MatchId}", mid);
        }

        int activeMatchId = 0;
        if (parsedMatchId > 0)
        {
            activeMatchId = parsedMatchId;
        }
        else if (workerPort > 0 && _workerPortToMatchMap.TryGetValue(workerPort, out int portMatchId))
        {
            activeMatchId = portMatchId;
        }
        else if (sourceMatchId > 0)
        {
            if (_workerPortToMatchMap.TryGetValue(sourceMatchId, out int mappedMatchId))
                activeMatchId = mappedMatchId;
            else if (sourceMatchId < 10000)
                activeMatchId = sourceMatchId;
            else
                activeMatchId = _latestActiveMatchId;
        }
        else
        {
            activeMatchId = _latestActiveMatchId;
        }

        MatchTelemetryState matchState = GetOrCreateMatchState(activeMatchId);
        bool stateChanged = false;

        lock (matchState.Lock)
        {
            if (activeMatchId > 0 && matchState.State.MatchID != activeMatchId)
            {
                matchState.State.MatchID = activeMatchId;
                matchState.MatchID = activeMatchId;
                stateChanged = true;
            }

            // 1. Voice Client Mapping
            if (FastCombatLogParser.TryParseVoiceClientAdd(lineSpan,
                    out FastCombatLogParser.VoiceClientAddEvent voiceMatch))
            {
                int slot = voiceMatch.PlayerSlot;
                string explicitName = GetOrInternString(voiceMatch.ExplicitName);
                int accountId = voiceMatch.AccountId;

                string name = !string.IsNullOrWhiteSpace(explicitName)
                    ? explicitName
                    : matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) existing) &&
                      !string.IsNullOrWhiteSpace(existing.AccountName)
                        ? existing.AccountName
                        : GetDefaultPlayerName(slot);

                matchState.SlotPlayerMap[slot] = (accountId, name);
                EnsurePlayerExists(matchState, slot, accountId, name);
                _logger.LogInformation(
                    "[CompelTelemetry] (Match #{MatchId}) Voice: Adding client num {Slot}, {Name}, ID: {AccountId}",
                    matchState.MatchID, slot, name, accountId);
                stateChanged = true;
            }

            // 2. Chat / Player Message
            if (FastCombatLogParser.TryParseChatMessage(lineSpan, out FastCombatLogParser.ChatMessageEvent cChat))
            {
                string chatUser = GetOrInternString(cChat.AccountName);
                int accountId =
                    matchState.SlotPlayerMap.TryGetValue(0, out (int AccountID, string AccountName) existing)
                        ? existing.AccountID
                        : 1;
                matchState.SlotPlayerMap[0] = (accountId, chatUser);
                EnsurePlayerExists(matchState, 0, accountId, chatUser);
                LivePlayerTelemetry? p0 = matchState.State.Players.FirstOrDefault(x => x.Slot == 0);
                if (p0 != null && p0.AccountName != chatUser)
                {
                    p0.AccountName = chatUser;
                    _logger.LogInformation(
                        "[CompelTelemetry] (Match #{MatchId}) Resolved Player Account Name from chat: '{Name}'",
                        matchState.MatchID, chatUser);
                    stateChanged = true;
                }
            }

            // 3. Team Joined
            if (FastCombatLogParser.TryParseTeamJoined(lineSpan, out FastCombatLogParser.TeamJoinedEvent cTeam))
            {
                int slot = cTeam.PlayerSlot;
                int team = cTeam.Team;
                string defaultName = GetDefaultPlayerName(slot);
                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, defaultName);
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.Team = team;
                if (team == 2)
                {
                    p.PositionX = 0.740f;
                    p.PositionY = 0.220f;
                    p.HeadingAngle = -2.356f;
                }
                else
                {
                    p.PositionX = 0.140f;
                    p.PositionY = 0.520f;
                    p.HeadingAngle = 0.785f;
                }

                string teamLabel = team == 2 ? "Hellbourne" : team == 1 ? "Legion" : "Spectator";
                _logger.LogInformation(
                    "[CompelTelemetry] (Match #{MatchId}) Client #{Slot} : {Name} : switched to Team {Team} ({TeamLabel}) (Pos: {X:F3}, {Y:F3})",
                    matchState.MatchID, slot, p.AccountName, team, teamLabel, p.PositionX, p.PositionY);
                stateChanged = true;
            }

            // 4. Repick
            if (FastCombatLogParser.TryParseRepick(lineSpan, out FastCombatLogParser.RepickEvent cRepick))
            {
                int slot = cRepick.PlayerSlot;
                LivePlayerTelemetry? p = matchState.State.Players.FirstOrDefault(x => x.Slot == slot);
                if (p != null)
                {
                    p.HeroIdentifier = string.Empty;
                    _logger.LogInformation("[CompelTelemetry] (Match #{MatchId}) Client #{Slot} repicked hero",
                        matchState.MatchID, slot);
                    stateChanged = true;
                }
            }

            // 5. Hero Pick
            if (FastCombatLogParser.TryParseHeroPick(lineSpan, out FastCombatLogParser.HeroPickEvent cHero))
            {
                int slot = cHero.PlayerSlot;
                string heroId = NormalizeHeroIdentifier(cHero.HeroIdentifier);

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.HeroIdentifier = heroId;
                _logger.LogInformation("[CompelTelemetry] (Match #{MatchId}) Client #{Slot} selected hero '{HeroId}'",
                    matchState.MatchID, slot, heroId);
                stateChanged = true;
            }

            // M*.log: PLAYER_CONNECT
            if (FastCombatLogParser.TryParsePlayerConnect(lineSpan, out FastCombatLogParser.PlayerConnectEvent pConn))
            {
                int slot = pConn.PlayerSlot;
                string name = GetOrInternString(pConn.Name);
                int accId = pConn.AccountId;
                matchState.SlotPlayerMap[slot] = (accId, name);
                EnsurePlayerExists(matchState, slot, accId, name);
                _logger.LogInformation(
                    "[CompelTelemetry] (Match #{MatchId}) Player connect: Slot #{Slot}, {Name}, ID: {Id}",
                    matchState.MatchID, slot, name, accId);
                stateChanged = true;
            }

            // M*.log: PLAYER_TEAM_CHANGE
            if (FastCombatLogParser.TryParsePlayerTeamChange(lineSpan,
                    out FastCombatLogParser.PlayerTeamChangeEvent pTeam))
            {
                int slot = pTeam.PlayerSlot;
                int team = pTeam.Team;
                string defaultName = GetDefaultPlayerName(slot);
                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, defaultName);
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.Team = team;
                p.PositionX = team == 2 ? 0.740f : 0.140f;
                p.PositionY = team == 2 ? 0.220f : 0.520f;
                p.HeadingAngle = team == 2 ? -2.356f : 0.785f;
                _logger.LogInformation("[CompelTelemetry] (Match #{MatchId}) Player #{Slot} team change: {Team}",
                    matchState.MatchID, slot, team);
                stateChanged = true;
            }

            // M*.log: PLAYER_SELECT / PLAYER_RANDOM
            if (FastCombatLogParser.TryParsePlayerSelect(lineSpan, out FastCombatLogParser.PlayerSelectEvent pSel))
            {
                int slot = pSel.PlayerSlot;
                string heroId = NormalizeHeroIdentifier(pSel.HeroIdentifier);

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.HeroIdentifier = heroId;
                _logger.LogInformation("[CompelTelemetry] (Match #{MatchId}) Client #{Slot} picked hero '{HeroId}'",
                    matchState.MatchID, slot, heroId);
                stateChanged = true;
            }

            // M*.log: HERO_LEVEL
            if (FastCombatLogParser.TryParseHeroLevel(lineSpan, out FastCombatLogParser.HeroLevelEvent hLvl))
            {
                int timeMs = hLvl.TimeMs;
                int slot = hLvl.PlayerSlot;
                int team = hLvl.Team;
                int level = hLvl.Level;

                matchState.State.ElapsedSeconds = timeMs / 1000;

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.Team = team;
                p.Level = level;

                if (hLvl.HasCoordinates)
                {
                    float k2X = hLvl.X;
                    float k2Y = hLvl.Y;
                    p.PositionX = Math.Clamp(k2X / WorldMapDimension, 0.0f, 1.0f);
                    p.PositionY = Math.Clamp(1.0f - k2Y / WorldMapDimension, 0.0f, 1.0f);
                }

                _logger.LogInformation("[CompelTelemetry] (Match #{MatchId}) Player #{Slot} reached Level {Level}",
                    matchState.MatchID, slot, level);
                stateChanged = true;
            }

            // M*.log: GOLD_EARNED
            if (FastCombatLogParser.TryParseGoldEarned(lineSpan, out FastCombatLogParser.GoldEarnedEvent gEarn))
            {
                int timeMs = gEarn.TimeMs;
                int slot = gEarn.PlayerSlot;
                int team = gEarn.Team;
                int gold = gEarn.Gold;

                matchState.State.ElapsedSeconds = timeMs / 1000;
                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.Team = team;
                p.CurrentGold += gold;

                if (gEarn.HasCoordinates)
                {
                    float k2X = gEarn.X;
                    float k2Y = gEarn.Y;
                    p.PositionX = Math.Clamp(k2X / WorldMapDimension, 0.0f, 1.0f);
                    p.PositionY = Math.Clamp(1.0f - k2Y / WorldMapDimension, 0.0f, 1.0f);
                }

                float elapsedMinutes = Math.Max(0.5f, matchState.State.ElapsedSeconds / 60.0f);
                p.GPM = (int)(p.CurrentGold / elapsedMinutes);
                stateChanged = true;
            }

            // M*.log: ITEM_PURCHASE / ASSEMBLE / SELL
            if (FastCombatLogParser.TryParseItemPurchase(lineSpan, out FastCombatLogParser.ItemPurchaseEvent iPurch))
            {
                int timeMs = iPurch.TimeMs;
                int slot = iPurch.PlayerSlot;
                string item = GetOrInternString(iPurch.ItemName);
                bool isSell = iPurch.IsSell;
                bool isAssemble = iPurch.IsAssemble;

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);

                if (isSell)
                {
                    p.Inventory.Remove(item);
                    string cleanItemName = GetCleanItemDisplayName(item);
                    matchState.State.RecentEvents.Insert(0, new LiveCombatEventTelemetry
                    {
                        EventType = "ITEM",
                        AttackerName = p.AccountName,
                        AttackerHero = p.HeroIdentifier,
                        VictimName = item,
                        VictimHero = item,
                        MatchTimeSeconds = timeMs / 1000,
                        Description = $"💰 {p.AccountName} sold {cleanItemName}"
                    });
                }
                else
                {
                    // If this item is composed of recipe components, strip the components from inventory
                    if (ItemRecipeComponents.TryGetValue(item, out string[]? components))
                        foreach (string comp in components)
                            p.Inventory.Remove(comp);

                    if (!p.Inventory.Contains(item) && p.Inventory.Count < 6) p.Inventory.Add(item);

                    if (isAssemble)
                    {
                        string cleanItemName = GetCleanItemDisplayName(item);
                        matchState.State.RecentEvents.Insert(0, new LiveCombatEventTelemetry
                        {
                            EventType = "ITEM",
                            AttackerName = p.AccountName,
                            AttackerHero = p.HeroIdentifier,
                            VictimName = item,
                            VictimHero = item,
                            MatchTimeSeconds = timeMs / 1000,
                            Description = $"🏆 {p.AccountName} assembled {cleanItemName}!"
                        });
                    }
                }

                if (matchState.State.RecentEvents.Count > 20)
                    matchState.State.RecentEvents.RemoveAt(matchState.State.RecentEvents.Count - 1);
                stateChanged = true;
            }

            // M*.log: ABILITY_ACTIVATE
            if (FastCombatLogParser.TryParseAbilityActivate(lineSpan,
                    out FastCombatLogParser.AbilityActivateEvent abAct))
            {
                int timeMs = abAct.TimeMs;
                int slot = abAct.PlayerSlot;
                string abilityName = GetOrInternString(abAct.AbilityName);

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.LastCastAbility = abilityName;
                p.LastCastTimeMs = timeMs;
                stateChanged = true;
            }

            // M*.log: ABILITY_UPGRADE
            if (FastCombatLogParser.TryParseAbilityUpgrade(lineSpan, out FastCombatLogParser.AbilityUpgradeEvent abUpg))
            {
                int slot = abUpg.PlayerSlot;
                string abilityName = GetOrInternString(abUpg.AbilityName);
                int abLevel = abUpg.Level;

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.AbilityLevels[abilityName] = abLevel;
                stateChanged = true;
            }

            // 6. EXP_EARNED (Live Player Coordinates)
            if (FastCombatLogParser.TryParseExpEarned(lineSpan, out FastCombatLogParser.ExpEarnedEvent expEarned))
            {
                int timeMs = expEarned.TimeMs;
                float k2X = expEarned.X;
                float k2Y = expEarned.Y;
                int slot = expEarned.PlayerSlot;
                int team = expEarned.Team;
                float exp = expEarned.Experience;

                matchState.State.ElapsedSeconds = timeMs / 1000;
                float normX = Math.Clamp(k2X / WorldMapDimension, 0.0f, 1.0f);
                float normY = Math.Clamp(1.0f - k2Y / WorldMapDimension, 0.0f, 1.0f);

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);
                p.Team = team;
                p.PositionX = normX;
                p.PositionY = normY;
                p.TotalExperience += exp;
                float elapsedMinutes = Math.Max(0.5f, matchState.State.ElapsedSeconds / 60.0f);
                p.XPM = (int)(p.TotalExperience / elapsedMinutes);
                stateChanged = true;
            }

            // 7. ITEM_ACTIVATE (Wards and Consumables)
            if (FastCombatLogParser.TryParseItemActivate(lineSpan, out FastCombatLogParser.ItemActivateEvent itemAct))
            {
                int timeMs = itemAct.TimeMs;
                float k2X = itemAct.X;
                float k2Y = itemAct.Y;
                int slot = itemAct.PlayerSlot;
                int team = itemAct.Team;
                string item = GetOrInternString(itemAct.ItemName);

                float normX = Math.Clamp(k2X / WorldMapDimension, 0.0f, 1.0f);
                float normY = Math.Clamp(1.0f - k2Y / WorldMapDimension, 0.0f, 1.0f);

                (int AccountID, string AccountName) slotInfo =
                    matchState.SlotPlayerMap.TryGetValue(slot, out (int AccountID, string AccountName) info)
                        ? info
                        : (slot + 1, GetDefaultPlayerName(slot));
                EnsurePlayerExists(matchState, slot, slotInfo.AccountID, slotInfo.AccountName);
                LivePlayerTelemetry p = matchState.State.Players.First(x => x.Slot == slot);

                // Consumables and Wards disappear from inventory upon activation
                p.Inventory.Remove(item);
                string? partialMatch = p.Inventory.FirstOrDefault(i =>
                    i.Contains(item.Replace("Item_", ""), StringComparison.OrdinalIgnoreCase) ||
                    item.Contains(i.Replace("Item_", ""), StringComparison.OrdinalIgnoreCase));
                if (partialMatch != null) p.Inventory.Remove(partialMatch);

                if (item.Contains("FlamingEye", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("ManaEye", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("Spyglass", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("Ward", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("Eye", StringComparison.OrdinalIgnoreCase))
                {
                    string wardType =
                        item.Contains("ManaEye", StringComparison.OrdinalIgnoreCase) ||
                        item.Contains("Rev", StringComparison.OrdinalIgnoreCase)
                            ? "Revelation"
                            : "Sight";
                    string placer = p.AccountName;

                    matchState.State.ActiveWards.Add(new LiveWardTelemetry
                    {
                        WardType = wardType,
                        Team = team,
                        PositionX = normX,
                        PositionY = normY,
                        VisionRadius = wardType == "Sight" ? 0.085f : 0.065f,
                        PlacerHeroName = placer,
                        ExpiresInSeconds = 360
                    });

                    if (matchState.State.ActiveWards.Count > 30) matchState.State.ActiveWards.RemoveAt(0);

                    matchState.State.RecentEvents.Insert(0, new LiveCombatEventTelemetry
                    {
                        EventType = "WARD",
                        AttackerName = placer,
                        AttackerHero = p.HeroIdentifier,
                        VictimName = $"{wardType} Ward",
                        VictimHero = item,
                        MatchTimeSeconds = timeMs / 1000,
                        Description = $"👁️ {placer} placed a {wardType} Ward"
                    });

                    if (matchState.State.RecentEvents.Count > 20)
                        matchState.State.RecentEvents.RemoveAt(matchState.State.RecentEvents.Count - 1);

                    _logger.LogInformation(
                        "[CompelTelemetry] (Match #{MatchId}) Ward placed by {Placer} ({WardType}) at ({X:F3}, {Y:F3})",
                        matchState.MatchID, placer, wardType, normX, normY);
                    stateChanged = true;
                }
            }

            // 8. KILL (Combat Events against Heroes, Bosses, Towers)
            if (FastCombatLogParser.TryParseKill(lineSpan, out FastCombatLogParser.KillEvent killEvent))
            {
                int timeMs = killEvent.TimeMs;
                int team = killEvent.Team;
                string target = GetOrInternString(killEvent.Target);
                string attacker = GetOrInternString(killEvent.Attacker);

                bool isHeroKill = target.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase);
                bool isTowerKill = target.StartsWith("Building_", StringComparison.OrdinalIgnoreCase);
                bool isBossKill = target.Contains("Kongor", StringComparison.OrdinalIgnoreCase) ||
                                  target.Contains("Transmutanstein", StringComparison.OrdinalIgnoreCase) ||
                                  target.Contains("Zorgath", StringComparison.OrdinalIgnoreCase) ||
                                  target.StartsWith("Boss_", StringComparison.OrdinalIgnoreCase);

                if (isHeroKill)
                {
                    if (team == 1) matchState.State.ScoreLegion++;
                    else if (team == 2) matchState.State.ScoreHellbourne++;

                    LivePlayerTelemetry? killer = matchState.State.Players.FirstOrDefault(x =>
                        x.HeroIdentifier.Equals(attacker, StringComparison.OrdinalIgnoreCase) ||
                        x.AccountName.Equals(attacker, StringComparison.OrdinalIgnoreCase));
                    if (killer != null) killer.Kills++;

                    LivePlayerTelemetry? victim = matchState.State.Players.FirstOrDefault(x =>
                        x.HeroIdentifier.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                        x.AccountName.Equals(target, StringComparison.OrdinalIgnoreCase));
                    if (victim != null) victim.Deaths++;

                    matchState.State.RecentEvents.Insert(0, new LiveCombatEventTelemetry
                    {
                        EventType = "KILL",
                        AttackerName = killer?.AccountName ?? attacker,
                        AttackerHero = attacker,
                        VictimName = victim?.AccountName ?? target,
                        VictimHero = target,
                        MatchTimeSeconds = timeMs / 1000,
                        Description = $"⚔️ {killer?.AccountName ?? attacker} killed {victim?.AccountName ?? target}!"
                    });
                    stateChanged = true;
                }
                else if (isBossKill)
                {
                    string bossName = target.Replace("Boss_", "").Replace("Creep_", "").Replace("_", " ");
                    matchState.State.RecentEvents.Insert(0, new LiveCombatEventTelemetry
                    {
                        EventType = "BOSS",
                        AttackerName = attacker,
                        AttackerHero = attacker,
                        VictimName = bossName,
                        VictimHero = target,
                        MatchTimeSeconds = timeMs / 1000,
                        Description = $"👑 {attacker} defeated {bossName}!"
                    });
                    stateChanged = true;
                }
                else if (isTowerKill)
                {
                    string structKey = target.ToLowerInvariant().Replace("building_", "");
                    matchState.State.Structures[structKey] = false;

                    string structName = target.Replace("Building_", "").Replace("_", " ");
                    matchState.State.RecentEvents.Insert(0, new LiveCombatEventTelemetry
                    {
                        EventType = "TOWER",
                        AttackerName = attacker,
                        AttackerHero = attacker,
                        VictimName = target,
                        VictimHero = target,
                        MatchTimeSeconds = timeMs / 1000,
                        Description = $"🏰 {attacker} destroyed {structName}!"
                    });
                    stateChanged = true;
                }

                if (matchState.State.RecentEvents.Count > 20)
                    matchState.State.RecentEvents.RemoveAt(matchState.State.RecentEvents.Count - 1);
                _logger.LogInformation(
                    "[CompelTelemetry] (Match #{MatchId}) Kill event: {Attacker} killed {Target} (Legion: {SL}, Hellbourne: {SH})",
                    matchState.MatchID, attacker, target, matchState.State.ScoreLegion,
                    matchState.State.ScoreHellbourne);
                stateChanged = true;
            }

            if (stateChanged && matchState.State.MatchID > 0)
            {
                LiveServerTickPayload snapshot = matchState.State.Clone();
                if (_telemetryChannel.Reader.Count >= 2048)
                {
                    Interlocked.Increment(ref _droppedTelemetryTicks);
                    CompelMetrics.TelemetryTicksDropped.Add(1);
                }
                _telemetryChannel.Writer.TryWrite(snapshot);
            }
        }
    }

    private static void EnsurePlayerExists(MatchTelemetryState matchState, int slot, int accountId, string name)
    {
        LivePlayerTelemetry? p = matchState.State.Players.FirstOrDefault(x => x.Slot == slot);
        if (p == null)
        {
            int defaultTeam = slot >= 5 ? 2 : 1;
            float defaultX = defaultTeam == 2 ? 0.740f : 0.140f;
            float defaultY = defaultTeam == 2 ? 0.220f : 0.520f;
            float defaultAngle = defaultTeam == 2 ? -2.356f : 0.785f;

            p = new LivePlayerTelemetry
            {
                Slot = slot,
                AccountID = accountId > 0 ? accountId : slot + 1,
                AccountName = !string.IsNullOrEmpty(name) ? name : GetDefaultPlayerName(slot),
                Team = defaultTeam,
                PositionX = defaultX,
                PositionY = defaultY,
                HeadingAngle = defaultAngle
            };
            matchState.State.Players.Add(p);
        }
        else
        {
            if (!string.IsNullOrEmpty(name) && p.AccountName.StartsWith("Player #", StringComparison.OrdinalIgnoreCase))
                p.AccountName = name;
            if (accountId > 0) p.AccountID = accountId;
        }
    }

    private async Task ProcessTelemetryQueueAsync()
    {
        if (_hubConnection != null)
            try
            {
                await _hubConnection.StartAsync(_cts.Token).ConfigureAwait(false);
                _logger.LogInformation("[CompelTelemetry] Connected to Dawnbringer SignalR LiveMatchesHub at {HubUrl}",
                    _options.LiveHubUrl);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "[CompelTelemetry] Initial SignalR connection to LiveMatchesHub deferred/failed; will use HTTP fallback");
            }

        ChannelReader<LiveServerTickPayload> reader = _telemetryChannel.Reader;
        while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
        while (reader.TryRead(out LiveServerTickPayload? payload))
        {
            if (payload == null) continue;

            // Coalesce rapid intermediate items by picking the newest available
            while (reader.TryRead(out LiveServerTickPayload? newerPayload))
                if (newerPayload != null)
                    payload = newerPayload;

            await SendTelemetryPayloadAsync(payload).ConfigureAwait(false);
        }
    }

    private async Task SendTelemetryPayloadAsync(LiveServerTickPayload payload)
    {
        bool signalRSucceeded = false;
        if (_hubConnection != null)
        {
            if (_hubConnection.State == HubConnectionState.Disconnected)
                try
                {
                    await _hubConnection.StartAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "[CompelTelemetry] SignalR reconnect attempt failed.");
                }

            if (_hubConnection.State == HubConnectionState.Connected)
                try
                {
                    await _hubConnection.InvokeAsync("SubmitMatchTick", payload, _cts.Token).ConfigureAwait(false);
                    signalRSucceeded = true;
                    CompelMetrics.TelemetryTicksDispatched.Add(1);
                    _logger.LogInformation(
                        "[CompelTelemetry] SignalR streamed live tick for Match #{MatchId} ({PlayerCount} players, Score: {Legion}:{Hellbourne}) to LiveMatchesHub",
                        payload.MatchID, payload.Players.Count, payload.ScoreLegion, payload.ScoreHellbourne);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[CompelTelemetry] SignalR invocation failed; falling back to HTTP");
                }
        }

        if (!signalRSucceeded) await SendTelemetryHttpRequestAsync(payload).ConfigureAwait(false);

        if (_sessionCache != null)
        {
            try
            {
                NEXUS.Contracts.Telemetry.LiveServerTickPayloadDto dto = payload.ToDto();
                await _sessionCache.SetLiveMatchStateAsync(payload.MatchID, dto, ct: _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CompelTelemetry] Failed to cache live match state in Redis session cache");
            }
        }
    }

    private async Task SendTelemetryHttpRequestAsync(LiveServerTickPayload payload)
    {
        try
        {
            string targetUrl =
                $"http://{_addressResolver.MasterServerHostAndPort}/server_requester.php?f=live_tick&match_id={payload.MatchID.ToString(CultureInfo.InvariantCulture)}";
            using JsonContent jsonContent =
                JsonContent.Create(payload, ControlPlaneJSONContext.Default.LiveServerTickPayload);
            HttpResponseMessage response =
                await _httpClient.PostAsync(targetUrl, jsonContent, _cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                CompelMetrics.TelemetryTicksDispatched.Add(1);
                _logger.LogInformation(
                    "[CompelTelemetry] Relayed live tick for Match #{MatchId} ({PlayerCount} players, Score: {Legion}:{Hellbourne}) to {Url}",
                    payload.MatchID, payload.Players.Count, payload.ScoreLegion, payload.ScoreHellbourne, targetUrl);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogTrace(ex, "[CompelTelemetry] HTTP live tick relay canceled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CompelTelemetry] Failed to relay live tick for Match #{MatchId}", payload.MatchID);
        }
    }

    public LiveServerTickPayload? GetMatchSnapshot(int matchId)
    {
        if (_matchStates.TryGetValue(matchId, out MatchTelemetryState? matchState))
            lock (matchState.Lock)
            {
                return matchState.State.Clone();
            }

        return null;
    }

    public sealed class MatchTelemetryState
    {
        public int MatchID { get; set; }
        public LiveServerTickPayload State { get; } = new();
        public object Lock { get; } = new();
        public ConcurrentDictionary<int, (int AccountID, string AccountName)> SlotPlayerMap { get; } = new();
    }
}
