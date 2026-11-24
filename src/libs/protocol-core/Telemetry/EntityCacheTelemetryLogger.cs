// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;

using Kongor.Core.Serialization.JSON;

namespace ASPIRE.Common.Telemetry;

/// <summary>
///     Structured telemetry event model formatted for JSON Lines (NDJSON) emission and PowerShell cmdlets.
/// </summary>
public sealed class EntityTelemetryEvent
{
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = TimeProvider.System.GetUtcNow();

    [JsonPropertyName("event_name")] public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("entity_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityType { get; set; }

    [JsonPropertyName("entity_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityName { get; set; }

    [JsonPropertyName("source_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceTier { get; set; }

    [JsonPropertyName("latency_us")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LatencyMicroseconds { get; set; }

    [JsonPropertyName("resolved_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedPath { get; set; }

    [JsonPropertyName("found")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Found { get; set; }

    [JsonPropertyName("total_entities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalEntities { get; set; }

    [JsonPropertyName("heroes_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HeroesCount { get; set; }

    [JsonPropertyName("avatars_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AvatarsCount { get; set; }

    [JsonPropertyName("abilities_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AbilitiesCount { get; set; }

    [JsonPropertyName("states_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatesCount { get; set; }

    [JsonPropertyName("projectiles_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProjectilesCount { get; set; }

    [JsonPropertyName("items_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ItemsCount { get; set; }

    [JsonPropertyName("cosmetics_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CosmeticsCount { get; set; }

    [JsonPropertyName("preload_resources_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PreloadResourcesCount { get; set; }

    [JsonPropertyName("binary_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BinaryBytes { get; set; }

    [JsonPropertyName("lua_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LuaBytes { get; set; }

    [JsonPropertyName("checksum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Checksum { get; set; }

    [JsonPropertyName("elapsed_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElapsedMilliseconds { get; set; }

    [JsonPropertyName("game_dir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameDir { get; set; }

    [JsonPropertyName("shared_dir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SharedDir { get; set; }

    [JsonPropertyName("output_dir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputDir { get; set; }

    [JsonPropertyName("target_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetMode { get; set; }

    [JsonPropertyName("dry_run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DryRun { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Properties { get; set; }
}

/// <summary>
///     Interface for structured JSON Lines (NDJSON) entity cache telemetry logging.
/// </summary>
public interface IEntityCacheTelemetryLogger
{
    void Log(EntityTelemetryEvent telemetryEvent);
    Task LogAsync(EntityTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default);
    void LogHit(string entityType, string entityName, string sourceTier, double latencyUs, string? resolvedPath = null);
    void LogMiss(string entityType, string entityName, string sourceTier = "NotFound", double latencyUs = 0);
    void LogPrewarmCompleted(int totalEntities, double elapsedMs, string? cachePath = null);
}

/// <summary>
///     Thread-safe structured JSON Lines (NDJSON) telemetry logger emitting events compatible with
///     Windows PowerShell cmdlets (ConvertFrom-Json, Select-String, Format-Table).
/// </summary>
public sealed class EntityCacheTelemetryLogger(string? logFilePath = null) : IEntityCacheTelemetryLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object FileSyncLock = new();
    private static readonly SemaphoreSlim AsyncFileLock = new(1, 1);
    private static string? _globalLogFilePath;

    private readonly string? _instanceLogFilePath = logFilePath;

    // Instance implementations
    public void Log(EntityTelemetryEvent telemetryEvent)
    {
        LogEvent(telemetryEvent, _instanceLogFilePath);
    }

    public Task LogAsync(EntityTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        return LogEventAsync(telemetryEvent, _instanceLogFilePath, cancellationToken);
    }

    public void LogHit(string entityType, string entityName, string sourceTier, double latencyUs,
        string? resolvedPath = null)
    {
        LogHit(entityType, entityName, sourceTier, latencyUs, resolvedPath, _instanceLogFilePath);
    }

    public void LogMiss(string entityType, string entityName, string sourceTier = "NotFound", double latencyUs = 0)
    {
        LogMiss(entityType, entityName, sourceTier, latencyUs, _instanceLogFilePath);
    }

    public void LogPrewarmCompleted(int totalEntities, double elapsedMs, string? cachePath = null)
    {
        LogPrewarmCompleted(totalEntities, elapsedMs, cachePath, _instanceLogFilePath);
    }

    /// <summary>
    ///     Configures the default global NDJSON log file path.
    /// </summary>
    public static void SetGlobalLogFilePath(string? path)
    {
        _globalLogFilePath = path;
    }

    /// <summary>
    ///     Gets the default global NDJSON log file path.
    /// </summary>
    public static string? GetGlobalLogFilePath()
    {
        return _globalLogFilePath;
    }

    /// <summary>
    ///     Formats an event as a single-line NDJSON string.
    /// </summary>
    public static string FormatAsNdjson(EntityTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        return JsonSerializer.Serialize(telemetryEvent, KongorCoreJsonContext.Default.EntityTelemetryEvent);
    }

    /// <summary>
    ///     Synchronously writes a telemetry event to the target NDJSON file.
    /// </summary>
    public static void LogEvent(EntityTelemetryEvent telemetryEvent, string? targetFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        string? effectivePath = targetFilePath ?? _globalLogFilePath;
        if (string.IsNullOrWhiteSpace(effectivePath)) return;

        string line = FormatAsNdjson(telemetryEvent) + Environment.NewLine;
        lock (FileSyncLock)
        {
            EnsureDirectoryExists(effectivePath);
            File.AppendAllText(effectivePath, line, Encoding.UTF8);
        }
    }

    /// <summary>
    ///     Asynchronously writes a telemetry event to the target NDJSON file.
    /// </summary>
    public static async Task LogEventAsync(EntityTelemetryEvent telemetryEvent, string? targetFilePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        string? effectivePath = targetFilePath ?? _globalLogFilePath;
        if (string.IsNullOrWhiteSpace(effectivePath)) return;

        string line = FormatAsNdjson(telemetryEvent) + Environment.NewLine;
        await AsyncFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectoryExists(effectivePath);
            await File.AppendAllTextAsync(effectivePath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AsyncFileLock.Release();
        }
    }

    /// <summary>
    ///     Helper to record a cache hit event.
    /// </summary>
    public static void LogHit(string entityType, string entityName, string sourceTier, double latencyUs,
        string? resolvedPath = null, string? targetFilePath = null)
    {
        LogEvent(new EntityTelemetryEvent
        {
            EventName = "EntityCacheHit",
            EntityType = entityType,
            EntityName = entityName,
            SourceTier = sourceTier,
            LatencyMicroseconds = latencyUs,
            ResolvedPath = resolvedPath,
            Found = true
        }, targetFilePath);
    }

    /// <summary>
    ///     Helper to record a cache miss event.
    /// </summary>
    public static void LogMiss(string entityType, string entityName, string sourceTier = "NotFound",
        double latencyUs = 0, string? targetFilePath = null)
    {
        LogEvent(new EntityTelemetryEvent
        {
            EventName = "EntityCacheMiss",
            EntityType = entityType,
            EntityName = entityName,
            SourceTier = sourceTier,
            LatencyMicroseconds = latencyUs,
            Found = false
        }, targetFilePath);
    }

    /// <summary>
    ///     Helper to record a pre-warming completion event.
    /// </summary>
    public static void LogPrewarmCompleted(int totalEntities, double elapsedMs, string? cachePath = null,
        string? targetFilePath = null)
    {
        LogEvent(new EntityTelemetryEvent
        {
            EventName = "EntityCachePrewarmCompleted",
            TotalEntities = totalEntities,
            ElapsedMilliseconds = elapsedMs,
            ResolvedPath = cachePath,
            Status = "Success"
        }, targetFilePath);
    }

    /// <summary>
    ///     Helper to record pre-baking CLI pipeline execution summary event.
    /// </summary>
    public static void LogPipelineExecuted(
        string gameDir,
        string sharedDir,
        string outputDir,
        int heroesCount,
        int avatarsCount,
        int abilitiesCount,
        int statesCount,
        int projectilesCount,
        int itemsCount,
        int cosmeticsCount,
        int preloadCount,
        long binaryBytes,
        long luaBytes,
        string checksum,
        double elapsedMs,
        string targetMode,
        bool dryRun,
        string? targetFilePath = null)
    {
        LogEvent(new EntityTelemetryEvent
        {
            EventName = "PrebakePipelineExecuted",
            GameDir = gameDir,
            SharedDir = sharedDir,
            OutputDir = outputDir,
            HeroesCount = heroesCount,
            AvatarsCount = avatarsCount,
            AbilitiesCount = abilitiesCount,
            StatesCount = statesCount,
            ProjectilesCount = projectilesCount,
            ItemsCount = itemsCount,
            CosmeticsCount = cosmeticsCount,
            PreloadResourcesCount = preloadCount,
            BinaryBytes = binaryBytes,
            LuaBytes = luaBytes,
            Checksum = checksum,
            ElapsedMilliseconds = elapsedMs,
            TargetMode = targetMode,
            DryRun = dryRun,
            Status = "Completed"
        }, targetFilePath);
    }

    /// <summary>
    ///     Reads and deserializes all events from an NDJSON file.
    /// </summary>
    public static IReadOnlyList<EntityTelemetryEvent> ReadEventsFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) return [];

        List<EntityTelemetryEvent> events = [];
        string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                EntityTelemetryEvent? ev = JsonSerializer.Deserialize(line, KongorCoreJsonContext.Default.EntityTelemetryEvent);
                if (ev != null) events.Add(ev);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return events;
    }

    /// <summary>
    ///     Asynchronously reads and deserializes all events from an NDJSON file.
    /// </summary>
    public static async Task<IReadOnlyList<EntityTelemetryEvent>> ReadEventsFromFileAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) return [];

        List<EntityTelemetryEvent> events = [];
        string[] lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                EntityTelemetryEvent? ev = JsonSerializer.Deserialize(line, KongorCoreJsonContext.Default.EntityTelemetryEvent);
                if (ev != null) events.Add(ev);
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return events;
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
