// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Persistent registration record for a dedicated game server worker.
/// </summary>
public sealed record ServerWorkerRegistration
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = "Project KONGOR Dedicated Server";

    [JsonPropertyName("map")]
    public string Map { get; init; } = "caldavar";

    [JsonPropertyName("matchId")]
    public long? MatchId { get; init; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset StartTime { get; init; }

    [JsonPropertyName("lastHeartbeat")]
    public DateTimeOffset LastHeartbeat { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "active";
}

/// <summary>
/// Interface for dynamic dedicated server port registry and authoritative port resolution.
/// </summary>
public interface IServerPortRegistry
{
    event Action<IReadOnlyList<ServerWorkerRegistration>>? OnRegistryChanged;
    ServerWorkerRegistration RegisterWorker(int processId, int port, string serverName = "Project KONGOR Dedicated Server", string map = "caldavar", long? matchId = null);
    bool UnregisterWorker(int port);
    bool RecordHeartbeat(int port);
    IReadOnlyList<ServerWorkerRegistration> GetActiveWorkers(bool pruneStale = true);
    int ResolveServerPort(int? explicitPort, string? map, long? matchId, out string resolutionReason);
    (int Port, string Reason) ResolveServerPort(int? explicitPort = null, string? map = null, long? matchId = null);
    int PruneStaleWorkers(bool force = false);
    void Save();
    void Load();
    void Clear();
}

/// <summary>
/// Dynamic Dedicated Server Port Registry implementing Junie's Pillar 5 architecture:
/// - Authoritative tracking of running dedicated server instances by port.
/// - Atomic file persistence ('userdataKONGOR/hon_server_registry.json') with file-sharing resilience.
/// - Automated stale worker pruning based on Win32 process lifecycle checks.
/// - Deterministic multi-server resolution precedence (explicit port -> matchId/map -> most recent active worker).
/// </summary>
public sealed class ServerPortRegistry : IServerPortRegistry
{
    private static readonly Lazy<ServerPortRegistry> DefaultInstanceLazy = new(() => new ServerPortRegistry());
    public static ServerPortRegistry Default => DefaultInstanceLazy.Value;

    public event Action<IReadOnlyList<ServerWorkerRegistration>>? OnRegistryChanged;

    private readonly bool _hasCustomFilePath;
    private readonly ConcurrentDictionary<int, ServerWorkerRegistration> _workers = new();
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly string _registryFilePath;
    private readonly Action<string> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ServerPortRegistry(string? customFilePath = null, TimeProvider? timeProvider = null, Action<string>? logger = null)
    {
        _hasCustomFilePath = customFilePath != null;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[ServerPortRegistry] {msg}"));

        string defaultRegistryDir = Directory.Exists(Path.Combine(Environment.CurrentDirectory, "games", "client"))
            ? Path.Combine(Environment.CurrentDirectory, "games", "client", "userdataKONGOR")
            : Path.Combine(Environment.CurrentDirectory, "artifacts", "userdataKONGOR");

        string baseDir = customFilePath != null
            ? Path.GetDirectoryName(customFilePath) ?? Environment.CurrentDirectory
            : defaultRegistryDir;

        _registryFilePath = customFilePath ?? Path.Combine(baseDir, "hon_server_registry.json");

        try
        {
            if (!string.IsNullOrWhiteSpace(baseDir) && !Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }
        }
        catch { }

        Load();
    }

    public ServerWorkerRegistration RegisterWorker(
        int processId,
        int port,
        string serverName = "Project KONGOR Dedicated Server",
        string map = "caldavar",
        long? matchId = null)
    {
        var now = _timeProvider.GetUtcNow();
        var reg = new ServerWorkerRegistration
        {
            ProcessId = processId,
            Port = port,
            ServerName = serverName,
            Map = map,
            MatchId = matchId,
            StartTime = now,
            LastHeartbeat = now,
            Status = "active"
        };

        _workers[port] = reg;
        _logger($"Registered dedicated server on port {port} (PID: {processId}, Map: '{map}').");
        Save();
        NotifyRegistryChanged();
        return reg;
    }

    public bool UnregisterWorker(int port)
    {
        if (_workers.TryRemove(port, out var removed))
        {
            _logger($"Unregistered dedicated server on port {port} (PID: {removed.ProcessId}).");
            Save();
            NotifyRegistryChanged();
            return true;
        }
        return false;
    }

    public bool RecordHeartbeat(int port)
    {
        if (_workers.TryGetValue(port, out var existing))
        {
            _workers[port] = existing with { LastHeartbeat = _timeProvider.GetUtcNow() };
            return true;
        }
        return false;
    }

    public IReadOnlyList<ServerWorkerRegistration> GetActiveWorkers(bool pruneStale = true)
    {
        if (pruneStale)
        {
            PruneStaleWorkers();
        }

        return _workers.Values.OrderByDescending(w => w.LastHeartbeat).ToList();
    }

    /// <summary>
    /// Implements deterministic multi-server resolution precedence per Pillar 5:
    /// 1. Explicit port provided -> direct match.
    /// 2. MatchId or Map filter -> matching active worker.
    /// 3. Fallback -> most recently active match worker.
    /// 4. Default -> 11235 if no workers are registered.
    /// </summary>
    public int ResolveServerPort(int? explicitPort, string? map, long? matchId, out string resolutionReason)
    {
        if (explicitPort.HasValue && explicitPort.Value > 0)
        {
            resolutionReason = "explicit_port";
            return explicitPort.Value;
        }

        var activeWorkers = GetActiveWorkers(pruneStale: true);

        // 2a. MatchId filter
        if (matchId.HasValue && matchId.Value > 0)
        {
            var matchWorker = activeWorkers.FirstOrDefault(w => w.MatchId == matchId.Value);
            if (matchWorker != null)
            {
                resolutionReason = $"match_id_filter:{matchId.Value}";
                return matchWorker.Port;
            }
        }

        // 2b. Map filter
        if (!string.IsNullOrWhiteSpace(map))
        {
            var mapWorker = activeWorkers.FirstOrDefault(w => string.Equals(w.Map, map, StringComparison.OrdinalIgnoreCase));
            if (mapWorker != null)
            {
                resolutionReason = $"map_filter:{map.Trim().ToLowerInvariant()}";
                return mapWorker.Port;
            }
        }

        // 3. Fallback to most recently active worker
        if (activeWorkers.Count > 0)
        {
            var mostRecent = activeWorkers[0];
            resolutionReason = "most_recent_active";
            return mostRecent.Port;
        }

        // 4. Ultimate fallback to standard default port 11235
        resolutionReason = "default_fallback";
        return 11235;
    }

    public (int Port, string Reason) ResolveServerPort(int? explicitPort = null, string? map = null, long? matchId = null)
    {
        int port = ResolveServerPort(explicitPort, map, matchId, out string reason);
        return (port, reason);
    }

    public int PruneStaleWorkers(bool force = false)
    {
        if (GameServerManageHandler.IsTestExecution && !force)
        {
            // In test execution without force, only prune workers explicitly marked as shutting_down or exited
            int prunedCount = 0;
            foreach (var kvp in _workers)
            {
                if (kvp.Value.Status == "exited" || kvp.Value.Status == "shutting_down")
                {
                    if (_workers.TryRemove(kvp.Key, out _)) prunedCount++;
                }
            }
            return prunedCount;
        }

        List<int> toRemove = [];
        foreach (var kvp in _workers)
        {
            int pid = kvp.Value.ProcessId;
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    toRemove.Add(kvp.Key);
                }
            }
            catch (ArgumentException)
            {
                // Process ID does not exist
                toRemove.Add(kvp.Key);
            }
            catch (Exception ex)
            {
                _logger($"Error probing worker process {pid}: {ex.Message}");
            }
        }

        int removedCount = 0;
        if (toRemove.Count > 0)
        {
            foreach (int port in toRemove)
            {
                if (_workers.TryRemove(port, out var stale))
                {
                    removedCount++;
                    _logger($"Pruned stale dedicated server on port {port} (PID: {stale.ProcessId}).");
                }
            }
            Save();
            NotifyRegistryChanged();
        }
        return removedCount;
    }

    public void Clear()
    {
        _workers.Clear();
        Save();
        NotifyRegistryChanged();
    }

    private void NotifyRegistryChanged()
    {
        try
        {
            var list = _workers.Values.OrderByDescending(w => w.LastHeartbeat).ToList();
            OnRegistryChanged?.Invoke(list);
        }
        catch (Exception ex)
        {
            _logger($"Error dispatching OnRegistryChanged: {ex.Message}");
        }
    }

    public void Save()
    {
        if (!_hasCustomFilePath && GameServerManageHandler.IsTestExecution)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                string dir = Path.GetDirectoryName(_registryFilePath) ?? Environment.CurrentDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempFile = _registryFilePath + ".tmp." + Guid.NewGuid().ToString("N")[..6];
                var list = _workers.Values.OrderBy(w => w.Port).ToList();
                string json = JsonSerializer.Serialize(list, JsonOpts);

                File.WriteAllText(tempFile, json);
                File.Move(tempFile, _registryFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger($"Failed to save server registry: {ex.Message}");
            }
        }
    }

    public void Load()
    {
        if (!_hasCustomFilePath && GameServerManageHandler.IsTestExecution)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                if (!File.Exists(_registryFilePath)) return;

                using var stream = new FileStream(_registryFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var list = JsonSerializer.Deserialize<List<ServerWorkerRegistration>>(stream, JsonOpts);
                if (list != null)
                {
                    _workers.Clear();
                    foreach (var item in list)
                    {
                        _workers[item.Port] = item;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"Failed to load server registry: {ex.Message}");
            }
        }
    }
}
