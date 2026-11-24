// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// World coordinate position in 3D space. Clamped to valid K2 map coordinates [-32768.0, 32768.0].
/// </summary>
public sealed class PositionDto
{
    [JsonPropertyName("x")]
    public double X { get; set; } = 0.0;

    [JsonPropertyName("y")]
    public double Y { get; set; } = 0.0;

    [JsonPropertyName("z")]
    public double Z { get; set; } = 0.0;

    public PositionDto() { }

    public PositionDto(double x, double y, double z = 0.0)
    {
        X = Math.Clamp(x, -32768.0, 32768.0);
        Y = Math.Clamp(y, -32768.0, 32768.0);
        Z = z;
    }

    public PositionDto Clone() => new(X, Y, Z);
}

/// <summary>
/// Item entity representation in hero inventory (slots 0-5) or stash (slots 6-11).
/// </summary>
public sealed class ItemDto
{
    [JsonPropertyName("slot")]
    public int Slot { get; set; } = 0;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("charges")]
    public int Charges { get; set; } = 1;

    [JsonPropertyName("inStash")]
    public bool InStash { get; set; } = false;

    [JsonPropertyName("cooldownRemaining")]
    public double CooldownRemaining { get; set; } = 0.0;

    public ItemDto() { }

    public ItemDto(string name, int slot = 0, int charges = 1, bool inStash = false)
    {
        Name = NormalizeItemName(name);
        Slot = Math.Clamp(slot, 0, 11);
        Charges = Math.Max(1, charges);
        InStash = inStash || slot >= 6;
    }

    public static string NormalizeItemName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "Item_HomecomingStone";
        string trimmed = rawName.Trim();
        return trimmed.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"Item_{trimmed}";
    }

    public ItemDto Clone() => new()
    {
        Slot = Slot,
        Name = Name,
        Charges = Charges,
        InStash = InStash,
        CooldownRemaining = CooldownRemaining
    };
}

/// <summary>
/// Ability cooldown state tracking for active hero abilities.
/// </summary>
public sealed class CooldownDto
{
    [JsonPropertyName("slot")]
    public int Slot { get; set; } = 0; // 0=Q, 1=W, 2=E, 3=R

    [JsonPropertyName("abilityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AbilityName { get; set; }

    [JsonPropertyName("remainingSeconds")]
    public double RemainingSeconds { get; set; } = 0.0;

    [JsonPropertyName("totalDurationSeconds")]
    public double TotalDurationSeconds { get; set; } = 0.0;

    public CooldownDto() { }

    public CooldownDto(int slot, double remainingSeconds, string? abilityName = null, double totalDuration = 0.0)
    {
        Slot = Math.Clamp(slot, 0, 15);
        RemainingSeconds = Math.Max(0.0, remainingSeconds);
        AbilityName = abilityName;
        TotalDurationSeconds = Math.Max(RemainingSeconds, totalDuration);
    }

    public CooldownDto Clone() => new()
    {
        Slot = Slot,
        AbilityName = AbilityName,
        RemainingSeconds = RemainingSeconds,
        TotalDurationSeconds = TotalDurationSeconds
    };
}

/// <summary>
/// Detailed player and hero state snapshot representation.
/// </summary>
public sealed class PlayerStateDto
{
    [JsonPropertyName("hero")]
    public string Hero { get; set; } = "Hero_Pyromancer";

    [JsonPropertyName("level")]
    public int Level { get; set; } = 1;

    [JsonPropertyName("gold")]
    public int Gold { get; set; } = 0;

    [JsonPropertyName("health")]
    public double Health { get; set; } = 600.0;

    [JsonPropertyName("maxHealth")]
    public double MaxHealth { get; set; } = 600.0;

    [JsonPropertyName("mana")]
    public double Mana { get; set; } = 300.0;

    [JsonPropertyName("maxMana")]
    public double MaxMana { get; set; } = 300.0;

    [JsonPropertyName("position")]
    public PositionDto Position { get; set; } = new();

    [JsonPropertyName("items")]
    public List<ItemDto> Items { get; set; } = [];

    [JsonPropertyName("cooldowns")]
    public List<CooldownDto> Cooldowns { get; set; } = [];

    public PlayerStateDto Clone() => new()
    {
        Hero = Hero,
        Level = Level,
        Gold = Gold,
        Health = Health,
        MaxHealth = MaxHealth,
        Mana = Mana,
        MaxMana = MaxMana,
        Position = Position.Clone(),
        Items = Items.Select(i => i.Clone()).ToList(),
        Cooldowns = Cooldowns.Select(c => c.Clone()).ToList()
    };
}

/// <summary>
/// Root Game State Snapshot DTO. Supports both nested PlayerState and flat property accessors.
/// </summary>
public sealed class GameSnapshotDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"snap_{Guid.NewGuid():N}";

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();

    [JsonPropertyName("gamePhase")]
    public int GamePhase { get; set; } = 3;

    [JsonPropertyName("matchTimeSeconds")]
    public double MatchTimeSeconds { get; set; } = 0.0;

    [JsonPropertyName("tick")]
    public long Tick { get; set; } = 0L;

    [JsonPropertyName("player")]
    public PlayerStateDto Player { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Flat compatibility accessors for seamless JSON serialization & tests
    [JsonIgnore]
    public string Hero
    {
        get => Player.Hero;
        set => Player.Hero = value;
    }

    [JsonIgnore]
    public int Level
    {
        get => Player.Level;
        set => Player.Level = Math.Clamp(value, 1, 25);
    }

    [JsonIgnore]
    public int Gold
    {
        get => Player.Gold;
        set => Player.Gold = Math.Max(0, value);
    }

    [JsonIgnore]
    public double Health
    {
        get => Player.Health;
        set => Player.Health = value;
    }

    [JsonIgnore]
    public double MaxHealth
    {
        get => Player.MaxHealth;
        set => Player.MaxHealth = value;
    }

    [JsonIgnore]
    public double Mana
    {
        get => Player.Mana;
        set => Player.Mana = value;
    }

    [JsonIgnore]
    public double MaxMana
    {
        get => Player.MaxMana;
        set => Player.MaxMana = value;
    }

    [JsonIgnore]
    public double X
    {
        get => Player.Position.X;
        set => Player.Position.X = Math.Clamp(value, -32768.0, 32768.0);
    }

    [JsonIgnore]
    public double Y
    {
        get => Player.Position.Y;
        set => Player.Position.Y = Math.Clamp(value, -32768.0, 32768.0);
    }

    [JsonIgnore]
    public double Z
    {
        get => Player.Position.Z;
        set => Player.Position.Z = value;
    }

    [JsonIgnore]
    public List<string> ItemNames => Player.Items.Select(i => i.Name).ToList();

    public GameSnapshotDto Clone() => new()
    {
        Id = Id,
        Label = Label,
        Timestamp = Timestamp,
        GamePhase = GamePhase,
        MatchTimeSeconds = MatchTimeSeconds,
        Tick = Tick,
        Player = Player.Clone(),
        Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>
    /// Generates compact, deterministic Lua table representation for Nexus.ApplySnapshot.
    /// </summary>
    public string ToLuaSnapshotTable()
    {
        var itemStrs = Player.Items.Select(i => $"'{i.Name.Replace("'", "\\'")}'");
        string itemsArray = $"{{ {string.Join(", ", itemStrs)} }}";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{{ gold = {0}, level = {1}, pos = {{ x = {2:F1}, y = {3:F1}, z = {4:F1} }}, items = {5} }}",
            Player.Gold,
            Player.Level,
            Player.Position.X,
            Player.Position.Y,
            Player.Position.Z,
            itemsArray);
    }
}

/// <summary>
/// Frame-accurate replay tick calculations for K2 engine (20 ticks per second, 50ms delta).
/// </summary>
public static class ReplayTickMath
{
    public const double TicksPerSecond = 20.0;
    public const double TickDeltaMs = 50.0;
    public const long TicksPerMinute = 1200L;
    public const long TicksPerHour = 72000L;

    public static long FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return 0;
        return (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
    }

    public static long FromMilliseconds(long milliseconds)
    {
        if (milliseconds <= 0) return 0;
        return (long)Math.Round(milliseconds / TickDeltaMs, MidpointRounding.AwayFromZero);
    }

    public static double ToSeconds(long tick)
    {
        return Math.Max(0L, tick) / TicksPerSecond;
    }

    public static long ToMilliseconds(long tick)
    {
        return Math.Max(0L, tick) * 50L;
    }

    public static string ToFormattedTimestamp(long tick)
    {
        double totalSeconds = ToSeconds(tick);
        int minutes = (int)(totalSeconds / 60.0);
        double seconds = totalSeconds % 60.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:00.000}", minutes, seconds);
    }

    public static long FromFormattedTimestamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return 0;
        string clean = timestamp.Trim();

        string[] parts = clean.Split(':');
        if (parts.Length == 2)
        {
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            {
                if (m < 0) m = 0;
                if (s < 0) s = 0;
                return (long)(m * 1200) + (long)Math.Round(s * TicksPerSecond, MidpointRounding.AwayFromZero);
            }
        }
        else if (parts.Length == 3)
        {
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            {
                if (h < 0) h = 0;
                if (m < 0) m = 0;
                if (s < 0) s = 0;
                return (long)(h * 72000) + (long)(m * 1200) + (long)Math.Round(s * TicksPerSecond, MidpointRounding.AwayFromZero);
            }
        }
        else if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double rawSec))
        {
            return FromSeconds(rawSec);
        }

        return 0;
    }

    public static long ResolveTick(JsonElement arguments)
    {
        // 1. Explicit tick / frame
        if ((arguments.TryGetProperty("tick", out JsonElement tkEl) ||
             arguments.TryGetProperty("frame", out tkEl)) &&
            tkEl.TryGetInt64(out long tk))
        {
            return Math.Max(0L, tk);
        }

        // 2. Seconds timestamp
        if ((arguments.TryGetProperty("timeSeconds", out JsonElement tsEl) ||
             arguments.TryGetProperty("time", out tsEl) ||
             arguments.TryGetProperty("seconds", out tsEl)) &&
            tsEl.TryGetDouble(out double tsVal))
        {
            return FromSeconds(tsVal);
        }

        // 3. Milliseconds timestamp
        if ((arguments.TryGetProperty("timeMs", out JsonElement tmEl) ||
             arguments.TryGetProperty("ms", out tmEl) ||
             arguments.TryGetProperty("milliseconds", out tmEl)) &&
            tmEl.TryGetInt64(out long tmVal))
        {
            return FromMilliseconds(tmVal);
        }

        // 4. Formatted timestamp string (MM:SS, MM:SS.fff, HH:MM:SS.fff)
        if ((arguments.TryGetProperty("timestamp", out JsonElement strEl) ||
             arguments.TryGetProperty("timeFormatted", out strEl) ||
             arguments.TryGetProperty("formatted", out strEl)) &&
            !string.IsNullOrWhiteSpace(strEl.GetString()))
        {
            return FromFormattedTimestamp(strEl.GetString()!);
        }

        return 0L;
    }
}

/// <summary>
/// MCP Tool Handler for 'kongor_game_snapshot' (aliases: 'kongor_snapshot', 'kongor_checkpoint').
/// Provides mid-game state checkpointing, persistence, restoration with Phase 3 lifecycle safety guards,
/// and frame-accurate replay tick seeking (20 ticks/sec).
/// </summary>
public sealed class GameSnapshotHandler
{
    private static readonly int[] AllowedPhasesPhase3Only = [3];
    private readonly ConcurrentDictionary<string, GameSnapshotDto> _snapshotStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly GameLuaHandler? _gameLuaHandler;
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry? _portRegistry;
    private readonly IStateReconciliationService _reconciliationService;
    private readonly IClientCommandRemediationService? _remediationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly Func<CancellationToken, Task<int>>? _phaseQueryOverride;
    private readonly ITaoClient _taoClient;

    public GameSnapshotHandler(
        IGameIpcBridge? gameIpcBridge = null,
        GameLuaHandler? gameLuaHandler = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        Func<CancellationToken, Task<int>>? phaseQueryOverride = null,
        ITaoClient? taoClient = null)
        : this(gameIpcBridge, gameLuaHandler, null, null, null, remediationService, timeProvider, logger, phaseQueryOverride, taoClient)
    {
    }

    public GameSnapshotHandler(
        IGameIpcBridge? gameIpcBridge,
        GameLuaHandler? gameLuaHandler,
        GameServerManageHandler? serverManageHandler,
        IServerPortRegistry? portRegistry,
        IStateReconciliationService? reconciliationService = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        Func<CancellationToken, Task<int>>? phaseQueryOverride = null,
        ITaoClient? taoClient = null)
    {
        _gameIpcBridge = gameIpcBridge;
        _gameLuaHandler = gameLuaHandler;
        _serverManageHandler = serverManageHandler;
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry;
        _reconciliationService = reconciliationService ?? new StateReconciliationService(timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _logger = logger;
        _phaseQueryOverride = phaseQueryOverride;

        NexusMailboxIpcBridge? mailboxBridge = _gameIpcBridge as NexusMailboxIpcBridge
            ?? (_gameIpcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? gameLuaHandler?.TaoClient ?? new TaoClient(
            mailboxBridge: mailboxBridge,
            fallbackBridge: _gameIpcBridge,
            logger: _logger);
    }

    public ConcurrentDictionary<string, GameSnapshotDto> SnapshotStore => _snapshotStore;
    public int Count => _snapshotStore.Count;
    public void Clear() => _snapshotStore.Clear();
    public bool TryGetSnapshot(string id, out GameSnapshotDto? snapshot) => _snapshotStore.TryGetValue(id, out snapshot);

    private void Log(string message)
    {
        _logger?.Invoke($"[GameSnapshotHandler] {message}");
    }

    private int ResolvePort(JsonElement arguments)
    {
        int? explicitPort = (arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pVal) && pVal > 0) ? pVal : null;
        if (_portRegistry != null)
        {
            return _portRegistry.ResolveServerPort(explicitPort, null, null, out _);
        }
        if (explicitPort.HasValue)
        {
            return explicitPort.Value;
        }
        if (_serverManageHandler != null)
        {
            var running = _serverManageHandler.DiscoverRunningDedicatedServers();
            if (running.Count > 0) return running[0].Port;
        }
        return 11235;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "save";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        Log($"Executing action '{action}'");

        try
        {
            return action switch
            {
                "save" or "checkpoint" => await HandleSaveAsync(arguments, cancellationToken).ConfigureAwait(false),
                "restore" or "apply" or "load" => await HandleRestoreAsync(arguments, cancellationToken).ConfigureAwait(false),
                "apply_fixture" or "fixture" or "inject_fixture" => await HandleApplyFixtureAsync(arguments, cancellationToken).ConfigureAwait(false),
                "world_state" or "inspect" or "world" => await HandleWorldStateAsync(arguments, cancellationToken).ConfigureAwait(false),
                "seek" or "jump" => await HandleSeekAsync(arguments, cancellationToken).ConfigureAwait(false),
                "list" => HandleList(),
                "delete" or "remove" => HandleDelete(arguments),
                "reconcile" or "verify_sync" or "sync" => await HandleReconcileAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => ToolCallResult.Error($"Unknown snapshot action '{action}'. Supported: save, restore, seek, list, delete, apply_fixture, world_state, reconcile.")
            };
        }
        catch (Exception ex)
        {
            Log($"Unhandled error executing '{action}': {ex.Message}");
            _remediationService?.RecordAndRemediate($"kongor_game_snapshot action:{action}", ex.Message, "mcp", "client");

            return ToolCallResult.Json(new
            {
                status = "error",
                action,
                errorCode = "EXECUTION_ERROR",
                message = ex.Message,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private async Task<ToolCallResult> HandleSaveAsync(JsonElement arguments, CancellationToken ct)
    {
        string resolvedId;
        if (arguments.TryGetProperty("id", out JsonElement idEl) && !string.IsNullOrWhiteSpace(idEl.GetString()))
        {
            resolvedId = idEl.GetString()!.Trim();
        }
        else
        {
            resolvedId = $"snap_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        }

        GameSnapshotDto snapshot;
        if (arguments.TryGetProperty("snapshot", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.Object)
        {
            snapshot = ParseSnapshotFromJson(sEl, resolvedId);
        }
        else
        {
            snapshot = ParseSnapshotFromJson(arguments, resolvedId);

            // If empty inline arguments, try collecting live telemetry and native engine state if bridge connected
            if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
            {
                try
                {
                    var engineState = await _gameIpcBridge.GetEngineStateAsync(1000, ct).ConfigureAwait(false);
                    if (engineState != null)
                    {
                        if (engineState.CurrentFps > 0) snapshot.Metadata["fps"] = engineState.CurrentFps.ToString(CultureInfo.InvariantCulture);
                        if (engineState.MemoryWorkingSetMb > 0) snapshot.Metadata["workingSetMb"] = engineState.MemoryWorkingSetMb.ToString(CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(engineState.CurrentMap)) snapshot.Metadata["map"] = engineState.CurrentMap;
                        if (!string.IsNullOrWhiteSpace(engineState.PlayerName)) snapshot.Metadata["player"] = engineState.PlayerName;
                        if (engineState.GamePhase > 0)
                        {
                            snapshot.GamePhase = engineState.GamePhase;
                            snapshot.Metadata["phaseName"] = engineState.GamePhase switch { 1 => "Loading", 2 => "HeroSelect", 3 => "ActiveMatch", 4 => "PostGame", _ => "PreGame" };
                        }
                    }
                }
                catch { }

                try
                {
                    string resp = await _gameIpcBridge.ExecuteCommandAsync(
                        "Script if Nexus and Nexus.CollectTelemetry then Nexus.CollectTelemetry() end",
                        3000, ct).ConfigureAwait(false);

                    if (GameLuaHandler.TryExtractTelemetry(resp, out JsonElement telemetry))
                    {
                        snapshot = ParseSnapshotFromJson(telemetry, resolvedId);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Telemetry capture fallback: {ex.Message}");
                }
            }
        }

        _snapshotStore[resolvedId] = snapshot;
        Log($"Saved game snapshot '{resolvedId}' (Total: {_snapshotStore.Count})");

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "save",
            id = resolvedId,
            snapshot,
            totalStored = _snapshotStore.Count,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleRestoreAsync(JsonElement arguments, CancellationToken ct)
    {
        string? id = arguments.TryGetProperty("id", out JsonElement idEl) && !string.IsNullOrWhiteSpace(idEl.GetString())
            ? idEl.GetString()!.Trim()
            : null;

        // 1. Pre-Flight Phase 3 Check! Query phase via IPC or override
        int currentPhase = await DetermineGamePhaseAsync(arguments, ct).ConfigureAwait(false);
        if (currentPhase != 3)
        {
            string msg = $"Snapshot can only be applied during active match (phase 3) to prevent native C++ engine assertions. Current phase is {currentPhase}.";

            _remediationService?.RecordAndRemediate(
                command: $"kongor_game_snapshot action:restore (phase: {currentPhase})",
                errorResponse: $"INVALID_GAME_PHASE: {msg}",
                channel: "snapshot_preflight",
                target: "client");

            Log($"[LIFECYCLE GUARD] Snapshot restoration blocked: currentPhase={currentPhase}, required=3");

            // CRITICAL: DO NOT dispatch any mutation commands when Phase != 3!
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "restore",
                id,
                errorCode = "INVALID_GAME_PHASE",
                currentPhase,
                allowedPhases = AllowedPhasesPhase3Only,
                message = "Snapshot can only be applied during active match (phase 3)",
                detail = msg,
                remediationHint = "State snapshot mutations (gold, level, position, items) can only be applied during active gameplay (GamePhase 3). In Phase 0 (loading) or Phases 1/2 (hero pick/strategy), the engine hero entity pointer pPlayer->GetHero() is null, causing native Access Violation 0xC0000005 crashes. Ensure the match has fully loaded and transitioned to Phase 3 using 'kongor_match_state' or wait for the active combat phase before restoring snapshots.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        // 2. Resolve target snapshot to restore
        GameSnapshotDto? targetSnapshot = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (!_snapshotStore.TryGetValue(id, out targetSnapshot))
            {
                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = "restore",
                    id,
                    errorCode = "SNAPSHOT_NOT_FOUND",
                    message = $"Snapshot with ID '{id}' was not found in the checkpoint store.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
        }
        else if (arguments.TryGetProperty("snapshot", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.Object)
        {
            targetSnapshot = ParseSnapshotFromJson(sEl, $"inline_{Guid.NewGuid():N}");
        }
        else
        {
            // Check if flat snapshot parameters exist
            if (arguments.TryGetProperty("gold", out _) ||
                arguments.TryGetProperty("level", out _) ||
                arguments.TryGetProperty("items", out _) ||
                arguments.TryGetProperty("pos", out _))
            {
                targetSnapshot = ParseSnapshotFromJson(arguments, $"inline_{Guid.NewGuid():N}");
            }
            else
            {
                return ToolCallResult.Error("Restore requires a valid 'snapshot' object or an existing 'id'.");
            }
        }

        // 3. Apply snapshot mutations via Lua bridge
        string luaTable = targetSnapshot.ToLuaSnapshotTable();
        string command = $"Script if Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot({luaTable}) end";
        string engineResponse = "OK";

        if (_gameIpcBridge != null)
        {
            try
            {
                engineResponse = await _gameIpcBridge.ExecuteCommandAsync(command, 3000, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"IPC snapshot restore dispatch error: {ex.Message}");
                _remediationService?.RecordAndRemediate(
                    command: "ApplySnapshot",
                    errorResponse: ex.Message,
                    channel: "ipc",
                    target: "client");

                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = "restore",
                    id = targetSnapshot.Id,
                    errorCode = "IPC_DISPATCH_FAILED",
                    message = $"Failed to restore snapshot over IPC: {ex.Message}",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
        }

        string target = "client";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim().ToLowerInvariant();
        }
        else if (arguments.TryGetProperty("destination", out JsonElement dEl) && !string.IsNullOrWhiteSpace(dEl.GetString()))
        {
            target = dEl.GetString()!.Trim().ToLowerInvariant();
        }

        string serverResponse = "OK";
        if ((target is "both" or "server" || target.StartsWith("server:", StringComparison.OrdinalIgnoreCase)) && _serverManageHandler != null)
        {
            int srvPort = ResolvePort(arguments);
            var srvRes = await _serverManageHandler.ExecuteCommandOnPortAsync(srvPort, command, autoLaunch: false, ct).ConfigureAwait(false);
            serverResponse = srvRes.Content.FirstOrDefault() is TextContentItem tc ? tc.Text : "OK";
            Log($"[DedicatedServer:{srvPort}] Dispatched snapshot restore command over server channel.");
        }

        Log($"Applied snapshot mutations successfully for ID '{targetSnapshot.Id}' (target: '{target}')");

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "restore",
            target,
            id = targetSnapshot.Id,
            snapshot = targetSnapshot,
            applied = targetSnapshot,
            gamePhase = 3,
            command,
            engineResponse,
            serverResponse = target is "both" or "server" ? serverResponse : (object?)null,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleSeekAsync(JsonElement arguments, CancellationToken ct)
    {
        long tick = ReplayTickMath.ResolveTick(arguments);
        tick = Math.Max(0L, tick);

        double timeSeconds = ReplayTickMath.ToSeconds(tick);
        long timeMs = ReplayTickMath.ToMilliseconds(tick);
        string timeFormatted = ReplayTickMath.ToFormattedTimestamp(tick);

        string cmdSetFrame = $"ReplaySetFrame {tick}";
        string cmdSeek = $"Seek {tick}";

        List<string> dispatchedCommands = [cmdSetFrame, cmdSeek];
        Log($"Replay seeking to tick {tick} ({timeFormatted}) via '{cmdSetFrame}' and '{cmdSeek}'");

        string response1 = "OK";
        string response2 = "OK";

        if (_gameIpcBridge != null)
        {
            try
            {
                response1 = await _gameIpcBridge.ExecuteCommandAsync(cmdSetFrame, 3000, ct).ConfigureAwait(false);
                response2 = await _gameIpcBridge.ExecuteCommandAsync(cmdSeek, 3000, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"IPC replay seek dispatch error: {ex.Message}");
                _remediationService?.RecordAndRemediate(
                    command: $"{cmdSetFrame}; {cmdSeek}",
                    errorResponse: ex.Message,
                    channel: "ipc",
                    target: "client");

                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = "seek",
                    errorCode = "IPC_DISPATCH_FAILED",
                    message = $"Failed to dispatch replay seek: {ex.Message}",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "seek",
            tick,
            timeSeconds,
            timeMs,
            timeFormatted,
            formattedTime = timeFormatted,
            timestamp = timeFormatted,
            commands = dispatchedCommands,
            engineResponses = new[] { response1, response2 },
            completedAt = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleList()
    {
        var list = _snapshotStore.Values.OrderByDescending(s => s.Timestamp).ToList();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "list",
            count = _snapshotStore.Count,
            totalSnapshots = _snapshotStore.Count,
            snapshots = list,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleDelete(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("id", out JsonElement idEl) || string.IsNullOrWhiteSpace(idEl.GetString()))
        {
            return ToolCallResult.Error("Missing required parameter 'id' for action 'delete'.");
        }

        string id = idEl.GetString()!.Trim();
        bool removed = _snapshotStore.TryRemove(id, out var _);

        if (!removed)
        {
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "delete",
                id,
                errorCode = "SNAPSHOT_NOT_FOUND",
                message = $"Snapshot with ID '{id}' was not found in the checkpoint store.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        Log($"Deleted game snapshot '{id}' (Remaining: {_snapshotStore.Count})");

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "delete",
            id,
            deleted = true,
            remainingCount = _snapshotStore.Count,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<int> DetermineGamePhaseAsync(JsonElement arguments, CancellationToken ct)
    {
        // 1. Direct argument override (support for tests and callers)
        if ((arguments.TryGetProperty("currentPhase", out JsonElement cpEl) ||
             arguments.TryGetProperty("phase", out cpEl)) &&
            cpEl.TryGetInt32(out int cp))
        {
            return cp;
        }

        // 2. Injected delegate override
        if (_phaseQueryOverride != null)
        {
            return await _phaseQueryOverride(ct).ConfigureAwait(false);
        }

        // 3. IPC query
        if (_gameIpcBridge != null)
        {
            try
            {
                var engineState = await _gameIpcBridge.GetEngineStateAsync(1000, ct).ConfigureAwait(false);
                if (engineState != null && engineState.GamePhase > 0)
                {
                    return engineState.GamePhase;
                }
            }
            catch { }

            try
            {
                string resp = await _gameIpcBridge.ExecuteCommandAsync(
                    "Script Echo(Nexus and Nexus.GetCurrentPhase and Nexus.GetCurrentPhase() or 0)",
                    1500, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(resp))
                {
                    string clean = resp.Trim();
                    if (int.TryParse(clean, out int p))
                    {
                        return p;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to query game phase via IPC: {ex.Message}");
            }
        }

        // Default simulated active phase if disconnected
        return 3;
    }

    private GameSnapshotDto ParseSnapshotFromJson(JsonElement element, string resolvedId)
    {
        var snapshot = new GameSnapshotDto
        {
            Id = resolvedId,
            Timestamp = _timeProvider.GetUtcNow(),
            GamePhase = 3
        };

        JsonElement targetEl = element;
        if (element.TryGetProperty("player", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Object)
        {
            targetEl = pEl;
        }

        // Hero name
        if (targetEl.TryGetProperty("hero", out JsonElement hEl) && !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            snapshot.Hero = hEl.GetString()!;
        }
        else if (element.TryGetProperty("hero", out hEl) && !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            snapshot.Hero = hEl.GetString()!;
        }

        // Level (clamped [1, 25])
        if (targetEl.TryGetProperty("level", out JsonElement lEl) && lEl.TryGetInt32(out int lvl))
        {
            snapshot.Level = lvl;
        }
        else if (element.TryGetProperty("level", out lEl) && lEl.TryGetInt32(out lvl))
        {
            snapshot.Level = lvl;
        }

        // Gold (clamped >= 0)
        if (targetEl.TryGetProperty("gold", out JsonElement gEl) && gEl.TryGetInt32(out int g))
        {
            snapshot.Gold = g;
        }
        else if (element.TryGetProperty("gold", out gEl) && gEl.TryGetInt32(out g))
        {
            snapshot.Gold = g;
        }

        // Health / MaxHealth
        if (targetEl.TryGetProperty("health", out JsonElement hpEl) && hpEl.TryGetDouble(out double hp))
        {
            snapshot.Health = hp;
        }
        else if (targetEl.TryGetProperty("hp", out hpEl) && hpEl.TryGetDouble(out hp))
        {
            snapshot.Health = hp;
        }

        if (targetEl.TryGetProperty("maxHealth", out JsonElement mhpEl) && mhpEl.TryGetDouble(out double mhp))
        {
            snapshot.MaxHealth = mhp;
        }
        else if (targetEl.TryGetProperty("maxHp", out mhpEl) && mhpEl.TryGetDouble(out mhp))
        {
            snapshot.MaxHealth = mhp;
        }

        // Mana / MaxMana
        if (targetEl.TryGetProperty("mana", out JsonElement manaEl) && manaEl.TryGetDouble(out double mana))
        {
            snapshot.Mana = mana;
        }

        if (targetEl.TryGetProperty("maxMana", out JsonElement mmanaEl) && mmanaEl.TryGetDouble(out double mmana))
        {
            snapshot.MaxMana = mmana;
        }

        // Position coordinates
        if (targetEl.TryGetProperty("position", out JsonElement posEl) && posEl.ValueKind == JsonValueKind.Object)
        {
            ParsePosition(posEl, snapshot.Player.Position);
        }
        else if (targetEl.TryGetProperty("pos", out posEl) && posEl.ValueKind == JsonValueKind.Object)
        {
            ParsePosition(posEl, snapshot.Player.Position);
        }
        else if (element.TryGetProperty("pos", out posEl) && posEl.ValueKind == JsonValueKind.Object)
        {
            ParsePosition(posEl, snapshot.Player.Position);
        }
        else
        {
            if ((targetEl.TryGetProperty("x", out JsonElement xEl) || element.TryGetProperty("x", out xEl)) && xEl.TryGetDouble(out double x))
                snapshot.X = x;
            if ((targetEl.TryGetProperty("y", out JsonElement yEl) || element.TryGetProperty("y", out yEl)) && yEl.TryGetDouble(out double y))
                snapshot.Y = y;
            if ((targetEl.TryGetProperty("z", out JsonElement zEl) || element.TryGetProperty("z", out zEl)) && zEl.TryGetDouble(out double z))
                snapshot.Z = z;
        }

        // Items / Inventory
        JsonElement itemsEl = default;
        bool hasItems = targetEl.TryGetProperty("items", out itemsEl) ||
                        element.TryGetProperty("items", out itemsEl) ||
                        targetEl.TryGetProperty("inventory", out itemsEl) ||
                        element.TryGetProperty("inventory", out itemsEl);

        if (hasItems && itemsEl.ValueKind == JsonValueKind.Array)
        {
            snapshot.Player.Items.Clear();
            int slot = 0;
            foreach (JsonElement itm in itemsEl.EnumerateArray())
            {
                if (itm.ValueKind == JsonValueKind.String)
                {
                    string? name = itm.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        snapshot.Player.Items.Add(new ItemDto(name, slot++));
                    }
                }
                else if (itm.ValueKind == JsonValueKind.Object)
                {
                    string name = itm.TryGetProperty("name", out JsonElement nEl) ? nEl.GetString() ?? "Item_HomecomingStone" : "Item_HomecomingStone";
                    int itmSlot = itm.TryGetProperty("slot", out JsonElement sEl) && sEl.TryGetInt32(out int sVal) ? sVal : slot++;
                    int charges = itm.TryGetProperty("charges", out JsonElement cEl) && cEl.TryGetInt32(out int cVal) ? cVal : 1;
                    bool inStash = itm.TryGetProperty("inStash", out JsonElement isEl) && isEl.GetBoolean();
                    snapshot.Player.Items.Add(new ItemDto(name, itmSlot, charges, inStash));
                }
            }
        }

        // Cooldowns
        JsonElement cdEl = default;
        bool hasCds = targetEl.TryGetProperty("cooldowns", out cdEl) || element.TryGetProperty("cooldowns", out cdEl);
        if (hasCds && cdEl.ValueKind == JsonValueKind.Array)
        {
            snapshot.Player.Cooldowns.Clear();
            int cdSlot = 0;
            foreach (JsonElement cd in cdEl.EnumerateArray())
            {
                if (cd.ValueKind == JsonValueKind.Number && cd.TryGetDouble(out double cdVal))
                {
                    snapshot.Player.Cooldowns.Add(new CooldownDto(cdSlot++, cdVal));
                }
                else if (cd.ValueKind == JsonValueKind.Object)
                {
                    int s = cd.TryGetProperty("slot", out JsonElement sEl) && sEl.TryGetInt32(out int sVal) ? sVal : cdSlot++;
                    double rem = cd.TryGetProperty("remainingSeconds", out JsonElement rEl) && rEl.TryGetDouble(out double rVal) ? rVal : 0.0;
                    string? abName = cd.TryGetProperty("abilityName", out JsonElement aEl) ? aEl.GetString() : null;
                    double dur = cd.TryGetProperty("totalDurationSeconds", out JsonElement dEl) && dEl.TryGetDouble(out double dVal) ? dVal : rem;
                    snapshot.Player.Cooldowns.Add(new CooldownDto(s, rem, abName, dur));
                }
            }
        }

        return snapshot;
    }

    private static void ParsePosition(JsonElement posEl, PositionDto target)
    {
        if (posEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double x)) target.X = Math.Clamp(x, -32768.0, 32768.0);
        if (posEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double y)) target.Y = Math.Clamp(y, -32768.0, 32768.0);
        if (posEl.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetDouble(out double z)) target.Z = z;
    }

    private async Task<ToolCallResult> HandleApplyFixtureAsync(JsonElement arguments, CancellationToken ct)
    {
        JsonElement fixtureEl = default;
        bool hasFixture = arguments.TryGetProperty("fixture", out fixtureEl) && fixtureEl.ValueKind == JsonValueKind.Object;
        if (!hasFixture)
        {
            fixtureEl = arguments;
        }

        string? hero = null;
        if (fixtureEl.TryGetProperty("hero", out JsonElement hEl) && !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            hero = hEl.GetString()!.Trim();
        }

        int? level = null;
        if (fixtureEl.TryGetProperty("level", out JsonElement lvlEl) && lvlEl.TryGetInt32(out int lvlVal))
        {
            level = lvlVal;
        }

        int? gold = null;
        if (fixtureEl.TryGetProperty("gold", out JsonElement gEl) && gEl.TryGetInt32(out int gVal))
        {
            gold = gVal;
        }

        var itemsList = new List<string>();
        if (fixtureEl.TryGetProperty("inventory", out JsonElement invEl) || fixtureEl.TryGetProperty("items", out invEl))
        {
            if (invEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in invEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        itemsList.Add(item.GetString()!.Trim());
                    }
                }
            }
        }

        double? posX = null, posY = null, posZ = null;
        if (fixtureEl.TryGetProperty("pos", out JsonElement posEl) || fixtureEl.TryGetProperty("position", out posEl))
        {
            if (posEl.ValueKind == JsonValueKind.Object)
            {
                if (posEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double x)) posX = x;
                if (posEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double y)) posY = y;
                if (posEl.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetDouble(out double z)) posZ = z;
            }
        }

        bool resetCooldowns = true;
        if (fixtureEl.TryGetProperty("resetCooldowns", out JsonElement rcEl) || fixtureEl.TryGetProperty("reset_cooldowns", out rcEl))
        {
            resetCooldowns = rcEl.GetBoolean();
        }

        bool refreshVitality = true;
        if (fixtureEl.TryGetProperty("refreshVitality", out JsonElement rvEl) || fixtureEl.TryGetProperty("refresh_vitality", out rvEl))
        {
            refreshVitality = rvEl.GetBoolean();
        }

        var fixturePayload = new Dictionary<string, object?>
        {
            ["resetCooldowns"] = resetCooldowns,
            ["refreshVitality"] = refreshVitality
        };
        if (!string.IsNullOrWhiteSpace(hero)) fixturePayload["hero"] = hero;
        if (level.HasValue) fixturePayload["level"] = level.Value;
        if (gold.HasValue) fixturePayload["gold"] = gold.Value;
        if (itemsList.Count > 0) fixturePayload["inventory"] = itemsList;
        if (posX.HasValue && posY.HasValue)
        {
            fixturePayload["pos"] = new { x = posX.Value, y = posY.Value, z = posZ ?? 0 };
        }

        string target = "client";
        if (arguments.TryGetProperty("target", out JsonElement targetEl) && !string.IsNullOrWhiteSpace(targetEl.GetString()))
        {
            target = targetEl.GetString()!.Trim().ToLowerInvariant();
        }

        // Dedicated server target routing
        if (target.StartsWith("server", StringComparison.OrdinalIgnoreCase) && _gameLuaHandler != null)
        {
            return await _gameLuaHandler.HandleAsync(JsonSerializer.SerializeToElement(new
            {
                action = "apply_fixture",
                target,
                hero,
                level,
                gold,
                inventory = itemsList,
                x = posX,
                y = posY,
                z = posZ,
                reset_cooldowns = resetCooldowns,
                refresh_vitality = refreshVitality
            }), ct).ConfigureAwait(false);
        }

        if (target == "client")
        {
            TaoResponse<JsonElement> taoRes = await _taoClient.DispatchAsync("apply_fixture", fixturePayload, 3000, ct).ConfigureAwait(false);
            if (taoRes.Success)
            {
                return ToolCallResult.Success(new
                {
                    status = "success",
                    target = "client",
                    action = "apply_fixture",
                    applied = true,
                    data = taoRes.Data,
                    elapsedMs = taoRes.ElapsedMs
                });
            }
        }

        string fixtureJson = JsonSerializer.Serialize(fixturePayload);
        string safeFixtureJson = fixtureJson.Replace("\\", "\\\\").Replace("'", "\\'");
        string command = $"Script if TAO and TAO.Dispatch then TAO.Dispatch('apply_fixture', '{safeFixtureJson}') elseif Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot({{ gold = {gold ?? 0}, level = {level ?? 1} }}) end";

        object? serverResult = null;
        if (target == "both" && _gameLuaHandler != null)
        {
            var sRes = await _gameLuaHandler.HandleAsync(JsonSerializer.SerializeToElement(new
            {
                action = "tao_dispatch",
                subaction = "apply_fixture",
                payload = fixturePayload,
                target = "server"
            }), ct).ConfigureAwait(false);
            serverResult = sRes.Content.FirstOrDefault() is TextContentItem tc ? tc.Text : "OK";
        }

        string engineResponse = "OK";
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                engineResponse = await _gameIpcBridge.ExecuteCommandAsync(command, 3000, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"IPC fixture apply dispatch error: {ex.Message}");
                _remediationService?.RecordAndRemediate(
                    command: "ApplyFixture",
                    errorResponse: ex.Message,
                    channel: "ipc",
                    target: target);

                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = "apply_fixture",
                    errorCode = "IPC_DISPATCH_FAILED",
                    message = $"Failed to apply fixture over IPC: {ex.Message}",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
        }

        bool hasTaoRes = GameLuaHandler.TryExtractTaoResponse(engineResponse, out JsonElement taoData, out string? taoStatus, out string? taoError);

        return ToolCallResult.Json(new
        {
            status = !string.IsNullOrEmpty(taoError) || taoStatus == "error" ? "error" : "success",
            action = "apply_fixture",
            target,
            fixture = fixturePayload,
            command,
            data = hasTaoRes ? taoData : (object?)null,
            response = engineResponse,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: !string.IsNullOrEmpty(taoError) || taoStatus == "error");
    }

    private async Task<ToolCallResult> HandleWorldStateAsync(JsonElement arguments, CancellationToken ct)
    {
        string target = "client";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim().ToLowerInvariant();
        }
        else if (arguments.TryGetProperty("destination", out JsonElement dEl) && !string.IsNullOrWhiteSpace(dEl.GetString()))
        {
            target = dEl.GetString()!.Trim().ToLowerInvariant();
        }

        int port = ResolvePort(arguments);
        Log($"Querying structured in-engine world state (target: '{target}', port: {port})...");

        // 1. Target: Server Only
        if (target == "server")
        {
            var serverWorld = await GetServerWorldStateAsync(port, ct).ConfigureAwait(false);
            return ToolCallResult.Json(new
            {
                status = "success",
                action = "world_state",
                target = "server",
                port,
                worldState = serverWorld,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // 2. Target: Client
        var clientWorld = await GetClientWorldStateAsync(ct).ConfigureAwait(false);

        // 3. Target: Reconciled or Both
        if (target is "both" or "reconciled")
        {
            var serverWorld = await GetServerWorldStateAsync(port, ct).ConfigureAwait(false);
            var clientJson = JsonSerializer.SerializeToElement(clientWorld);
            var serverJson = JsonSerializer.SerializeToElement(serverWorld);

            double toleranceEpsilon = 50.0;
            if (arguments.TryGetProperty("toleranceEpsilon", out JsonElement epsEl) && epsEl.TryGetDouble(out double ev) && ev >= 0)
            {
                toleranceEpsilon = ev;
            }

            var report = _reconciliationService.ReconcileWorldState(clientJson, serverJson, new ReconciliationOptions { ToleranceEpsilon = toleranceEpsilon });

            return ToolCallResult.Json(new
            {
                status = "success",
                action = "world_state",
                target,
                port,
                isSynchronized = report.IsSynchronized,
                desyncSeverity = report.DesyncSeverity,
                clockDriftSeconds = report.ClockDriftSeconds,
                positionDrift = report.PositionDrift,
                totalDeltas = report.TotalDeltas,
                outOfToleranceCount = report.OutOfToleranceCount,
                deltas = report.Deltas,
                remediationAction = report.RemediationAction,
                remediationHint = report.RemediationHint,
                summary = report.Summary,
                authoritativeSource = report.AuthoritativeSource,
                clientWorldState = clientWorld,
                serverWorldState = serverWorld,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "world_state",
            target = "client",
            worldState = clientWorld,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleReconcileAsync(JsonElement arguments, CancellationToken ct)
    {
        Log("Executing snapshot state reconciliation...");

        double toleranceEpsilon = 50.0;
        if (arguments.TryGetProperty("toleranceEpsilon", out JsonElement epsEl) && epsEl.TryGetDouble(out double ev) && ev >= 0)
        {
            toleranceEpsilon = ev;
        }

        int port = ResolvePort(arguments);
        var options = new ReconciliationOptions { ToleranceEpsilon = toleranceEpsilon };

        if (arguments.TryGetProperty("id", out JsonElement idEl) && !string.IsNullOrWhiteSpace(idEl.GetString()))
        {
            string id = idEl.GetString()!.Trim();
            if (_snapshotStore.TryGetValue(id, out var storedSnapshot) && storedSnapshot != null)
            {
                var clientWorld = await GetClientWorldStateAsync(ct).ConfigureAwait(false);
                var clientSnap = ParseSnapshotFromJson(JsonSerializer.SerializeToElement(clientWorld), $"live_client_{Guid.NewGuid():N}");
                var report = _reconciliationService.ReconcileSnapshots(storedSnapshot, clientSnap, options);

                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "reconcile",
                    id,
                    isSynchronized = report.IsSynchronized,
                    desyncSeverity = report.DesyncSeverity,
                    positionDrift = report.PositionDrift,
                    clockDriftSeconds = report.ClockDriftSeconds,
                    deltas = report.Deltas,
                    summary = report.Summary,
                    remediationAction = report.RemediationAction,
                    remediationHint = report.RemediationHint,
                    snapshot = storedSnapshot,
                    liveState = clientSnap,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        // Default: Reconcile client world state against server authoritative world state!
        return await HandleWorldStateAsync(JsonSerializer.SerializeToElement(new
        {
            action = "world_state",
            target = "reconciled",
            port,
            toleranceEpsilon
        }), ct).ConfigureAwait(false);
    }

    private async Task<object> GetClientWorldStateAsync(CancellationToken ct)
    {
        if (_taoClient.IsConnected)
        {
            try
            {
                TaoResponse<JsonElement> taoRes = await _taoClient.DispatchAsync("get_world_state", null, 3000, ct).ConfigureAwait(false);
                if (taoRes.Success && taoRes.Data.ValueKind == JsonValueKind.Object)
                {
                    return taoRes.Data.Clone();
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] Could not retrieve live client world state via TaoClient: {ex.Message}");
            }
        }

        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                var taoRes = await _taoClient.DispatchAsync("get_world_state", null, 3000, ct).ConfigureAwait(false);
                if (taoRes.Success && taoRes.Data.ValueKind != JsonValueKind.Undefined && taoRes.Data.ValueKind != JsonValueKind.Null)
                {
                    return taoRes.Data.Clone();
                }

                string rpcResult = await _gameIpcBridge.ExecuteRpcAsync<string>("game.get_world_state", null, 3000, ct).ConfigureAwait(false) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rpcResult))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(rpcResult);
                        return doc.RootElement.Clone();
                    }
                    catch { }
                }

                string evalResult = await _gameIpcBridge.ExecuteCommandAsync("Script if TAO and TAO.Dispatch then println(TAO.Dispatch('get_world_state')) end", 3000, ct).ConfigureAwait(false);
                if (GameLuaHandler.TryExtractTelemetry(evalResult, out JsonElement stateEl))
                {
                    return stateEl.Clone();
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] Could not retrieve live client world state: {ex.Message}");
            }
        }

        return CreateFallbackWorldState();
    }

    private async Task<object> GetServerWorldStateAsync(int port, CancellationToken ct)
    {
        if (_serverManageHandler != null)
        {
            try
            {
                var srvRes = await _serverManageHandler.ExecuteCommandOnPortAsync(
                    port,
                    "Script if TAO and TAO.GetWorldState then echo(json_encode(TAO.GetWorldState())) elseif TAO and TAO.Dispatch then echo(TAO.Dispatch('get_world_state')) end",
                    autoLaunch: false,
                    ct).ConfigureAwait(false);

                if (!srvRes.IsError && srvRes.Content.FirstOrDefault() is TextContentItem tc && !string.IsNullOrWhiteSpace(tc.Text))
                {
                    if (GameLuaHandler.TryExtractTelemetry(tc.Text, out JsonElement telemetryEl))
                    {
                        return telemetryEl.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] Server world state query on port {port} threw: {ex.Message}");
            }
        }

        return CreateFallbackWorldState();
    }

    private static object CreateFallbackWorldState(int gamePhase = 3, long timeMs = 45000)
    {
        var fallbackHero = new
        {
            slot = 0,
            playerName = "LocalPlayer",
            heroName = "Hero_Pyromancer",
            level = 1,
            health = 550.0,
            maxHealth = 550.0,
            mana = 280.0,
            maxMana = 280.0,
            gold = 600,
            networth = 600,
            position = new { x = 7400.0, y = 6900.0, z = 128.0 },
            heading = 90.0,
            abilities = new object[]
            {
                new { slot = 0, name = "Ability_Pyromancer1", level = 1, cooldown = 0.0, manaCost = 90 },
                new { slot = 1, name = "Ability_Pyromancer2", level = 0, cooldown = 0.0, manaCost = 100 },
                new { slot = 2, name = "Ability_Pyromancer3", level = 0, cooldown = 0.0, manaCost = 0 },
                new { slot = 3, name = "Ability_Pyromancer4", level = 0, cooldown = 0.0, manaCost = 150 }
            },
            inventory = new object[]
            {
                new { slot = 0, name = "Item_RunesOfTheFallen", charges = 1 },
                new { slot = 1, name = "Item_HealthPotion", charges = 1 }
            }
        };

        return new
        {
            gamePhase,
            gameTimeMs = timeMs,
            isPaused = false,
            timescale = 1.0,
            activeMap = "caldavar",
            team1Heroes = new object[] { fallbackHero },
            team2Heroes = Array.Empty<object>()
        };
    }
}
