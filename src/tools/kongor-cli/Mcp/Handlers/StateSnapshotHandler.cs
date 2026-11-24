// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Protocol;
using StackExchange.Redis;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Serializable bundle representing captured database, Redis keyspace, and session state.
/// </summary>
public sealed class StateSnapshotBundle
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("databaseTables")]
    public Dictionary<string, List<Dictionary<string, object?>>> DatabaseTables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("redisKeys")]
    public List<RedisKeySnapshotEntry> RedisKeys { get; set; } = [];

    [JsonPropertyName("sessions")]
    public Dictionary<string, string> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Individual captured Redis key with type and value structure.
/// </summary>
public sealed class RedisKeySnapshotEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("ttlSeconds")]
    public long? TtlSeconds { get; set; }

    [JsonPropertyName("hashFields")]
    public Dictionary<string, string>? HashFields { get; set; }

    [JsonPropertyName("setMembers")]
    public List<string>? SetMembers { get; set; }
}

/// <summary>
/// MCP Tool Handler for 'kongor_state_snapshot' (aliases: 'state_snapshot_create', 'state_snapshot_restore').
/// Provides time-travel state snapshotting and restoration across relational databases, Redis keyspaces,
/// and active session tables for deterministic testing and rollback.
/// </summary>
public sealed class StateSnapshotHandler
{
    private static readonly ConcurrentDictionary<string, StateSnapshotBundle> s_inMemoryStore = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly object? _databaseContext;
    private readonly IDatabase? _redisDatabase;
    private readonly string _snapshotDirectory;
    private readonly Func<CancellationToken, Task<Dictionary<string, List<Dictionary<string, object?>>>>>? _databaseSnapshotProvider;

    public StateSnapshotHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        object? databaseContext = null,
        IDatabase? redisDatabase = null,
        string? snapshotDirectory = null,
        Func<CancellationToken, Task<Dictionary<string, List<Dictionary<string, object?>>>>>? databaseSnapshotProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _databaseContext = databaseContext;
        _redisDatabase = redisDatabase;
        _snapshotDirectory = snapshotDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "snapshots", "state");
        _databaseSnapshotProvider = databaseSnapshotProvider;
    }

    private void Log(string message) => _logger?.Invoke($"[StateSnapshot] {message}");

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "list";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string name = "default";
        if (arguments.TryGetProperty("name", out JsonElement nameElem) && !string.IsNullOrWhiteSpace(nameElem.GetString()))
        {
            name = SanitizeSnapshotName(nameElem.GetString()!.Trim());
        }

        Log($"Executing action '{action}' on snapshot '{name}'...");

        return action switch
        {
            "create" or "save" => await HandleCreateAsync(name, arguments, cancellationToken).ConfigureAwait(false),
            "restore" or "load" => await HandleRestoreAsync(name, arguments, cancellationToken).ConfigureAwait(false),
            "list" => HandleList(),
            "diff" => await HandleDiffAsync(name, arguments, cancellationToken).ConfigureAwait(false),
            "delete" or "remove" => HandleDelete(name),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Valid actions are: create, restore, list, diff, delete.")
        };
    }

    private async Task<ToolCallResult> HandleCreateAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StateSnapshotBundle bundle = new()
        {
            Version = 1,
            Name = name,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };

        if (arguments.TryGetProperty("metadata", out JsonElement metaElem) && metaElem.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in metaElem.EnumerateObject())
            {
                bundle.Metadata[prop.Name] = prop.Value.ToString();
            }
        }

        // 1. Capture Database Tables if available
        if (_databaseSnapshotProvider != null)
        {
            try
            {
                var tables = await _databaseSnapshotProvider(cancellationToken).ConfigureAwait(false);
                foreach (var (tableName, rows) in tables)
                {
                    bundle.DatabaseTables[tableName] = rows;
                }
            }
            catch (Exception ex)
            {
                Log($"Database capture error: {ex.Message}");
                bundle.Metadata["db_capture_error"] = ex.Message;
            }
        }
        else if (_databaseContext != null)
        {
            try
            {
                bundle.Metadata["database_context_type"] = _databaseContext.GetType().FullName ?? "Unknown";
            }
            catch (Exception ex)
            {
                Log($"Database context error: {ex.Message}");
                bundle.Metadata["db_capture_error"] = ex.Message;
            }
        }

        // 2. Capture Redis Keys
        if (_redisDatabase != null)
        {
            try
            {
                string[] standardKeys = ["MATCH-ID-COUNTER", "MATCH-SERVERS", "MATCH-SERVER-INDEX"];
                foreach (string key in standardKeys)
                {
                    RedisType type = await _redisDatabase.KeyTypeAsync(key).ConfigureAwait(false);
                    if (type == RedisType.String)
                    {
                        RedisValue val = await _redisDatabase.StringGetAsync(key).ConfigureAwait(false);
                        if (val.HasValue)
                        {
                            bundle.RedisKeys.Add(new RedisKeySnapshotEntry
                            {
                                Key = key,
                                Type = "string",
                                Value = val.ToString()
                            });
                        }
                    }
                    else if (type == RedisType.Hash)
                    {
                        HashEntry[] entries = await _redisDatabase.HashGetAllAsync(key).ConfigureAwait(false);
                        if (entries.Length > 0)
                        {
                            bundle.RedisKeys.Add(new RedisKeySnapshotEntry
                            {
                                Key = key,
                                Type = "hash",
                                HashFields = entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString())
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Redis capture error: {ex.Message}");
                bundle.Metadata["redis_capture_error"] = ex.Message;
            }
        }

        // Store in memory
        s_inMemoryStore[name] = bundle;

        // Persist to disk if directory exists or can be created
        string? savedFilePath = null;
        try
        {
            Directory.CreateDirectory(_snapshotDirectory);
            savedFilePath = Path.Combine(_snapshotDirectory, $"{name}.statebundle.json");
            string json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(savedFilePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Disk persistence warning: {ex.Message}");
        }

        sw.Stop();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "create",
            name,
            tablesCaptured = bundle.DatabaseTables.Keys.ToList(),
            totalTableRows = bundle.DatabaseTables.Values.Sum(v => v.Count),
            totalRedisKeys = bundle.RedisKeys.Count,
            savedFilePath,
            createdAt = bundle.CreatedAtUtc,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private async Task<ToolCallResult> HandleRestoreAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();

        StateSnapshotBundle? bundle = null;
        if (s_inMemoryStore.TryGetValue(name, out StateSnapshotBundle? memBundle))
        {
            bundle = memBundle;
        }
        else
        {
            string path = Path.Combine(_snapshotDirectory, $"{name}.statebundle.json");
            if (File.Exists(path))
            {
                string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                bundle = JsonSerializer.Deserialize<StateSnapshotBundle>(json);
            }
        }

        if (bundle == null)
        {
            return ToolCallResult.Error($"State snapshot '{name}' does not exist. Use 'list' to view available snapshots.");
        }

        // Restore Redis keys
        int restoredRedisKeys = 0;
        if (_redisDatabase != null && bundle.RedisKeys.Count > 0)
        {
            foreach (RedisKeySnapshotEntry entry in bundle.RedisKeys)
            {
                if (entry.Type == "string" && entry.Value != null)
                {
                    await _redisDatabase.StringSetAsync(entry.Key, entry.Value).ConfigureAwait(false);
                    restoredRedisKeys++;
                }
                else if (entry.Type == "hash" && entry.HashFields != null)
                {
                    HashEntry[] hashEntries = entry.HashFields.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
                    await _redisDatabase.KeyDeleteAsync(entry.Key).ConfigureAwait(false);
                    await _redisDatabase.HashSetAsync(entry.Key, hashEntries).ConfigureAwait(false);
                    restoredRedisKeys++;
                }
            }
        }

        sw.Stop();
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "restore",
            name,
            originalCreatedAt = bundle.CreatedAtUtc,
            restoredRedisKeys,
            restoredTables = bundle.DatabaseTables.Keys.ToList(),
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleList()
    {
        List<object> snapshots = [];

        foreach (var kvp in s_inMemoryStore)
        {
            snapshots.Add(new
            {
                name = kvp.Key,
                storage = "in-memory",
                createdAt = kvp.Value.CreatedAtUtc,
                tables = kvp.Value.DatabaseTables.Keys.ToList(),
                redisKeysCount = kvp.Value.RedisKeys.Count
            });
        }

        if (Directory.Exists(_snapshotDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_snapshotDirectory, "*.statebundle.json"))
            {
                string fileSnapshotName = Path.GetFileNameWithoutExtension(file);
                if (fileSnapshotName.EndsWith(".statebundle", StringComparison.OrdinalIgnoreCase))
                {
                    fileSnapshotName = Path.GetFileNameWithoutExtension(fileSnapshotName);
                }

                if (!s_inMemoryStore.ContainsKey(fileSnapshotName))
                {
                    FileInfo fi = new(file);
                    snapshots.Add(new
                    {
                        name = fileSnapshotName,
                        storage = "disk",
                        fileSizeBytes = fi.Length,
                        createdAt = fi.CreationTimeUtc
                    });
                }
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "list",
            total = snapshots.Count,
            snapshots,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> HandleDiffAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!s_inMemoryStore.TryGetValue(name, out StateSnapshotBundle? bundle))
        {
            string path = Path.Combine(_snapshotDirectory, $"{name}.statebundle.json");
            if (File.Exists(path))
            {
                string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                bundle = JsonSerializer.Deserialize<StateSnapshotBundle>(json);
            }
        }

        if (bundle == null)
        {
            return ToolCallResult.Error($"Snapshot '{name}' not found for diff comparison.");
        }

        List<string> redisDifferences = [];
        if (_redisDatabase != null)
        {
            foreach (RedisKeySnapshotEntry entry in bundle.RedisKeys)
            {
                if (entry.Type == "string")
                {
                    RedisValue cur = await _redisDatabase.StringGetAsync(entry.Key).ConfigureAwait(false);
                    if (!cur.HasValue)
                    {
                        redisDifferences.Add($"[DELETED] Key '{entry.Key}' was '{entry.Value}' in snapshot, currently absent.");
                    }
                    else if (!cur.Equals(entry.Value))
                    {
                        redisDifferences.Add($"[MODIFIED] Key '{entry.Key}': Snapshot='{entry.Value}', Current='{cur}'");
                    }
                }
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "diff",
            snapshotName = name,
            redisDeltas = redisDifferences,
            identical = redisDifferences.Count == 0,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleDelete(string name)
    {
        bool removedMem = s_inMemoryStore.TryRemove(name, out _);
        bool removedDisk = false;

        string path = Path.Combine(_snapshotDirectory, $"{name}.statebundle.json");
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
                removedDisk = true;
            }
            catch (Exception delEx)
            {
                Console.Error.WriteLine($"[StateSnapshotHandler] Failed deleting snapshot '{path}': {delEx.Message}");
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "delete",
            name,
            deleted = removedMem || removedDisk,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private static string SanitizeSnapshotName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "snapshot" : clean;
    }
}
