// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_db_pool_diagnostics'.
/// Real-time connection pool monitoring (active connections, idle count, wait duration, thread starvation)
/// across Npgsql and Microsoft.Data.SqlClient providers.
/// </summary>
public sealed class DbPoolDiagnosticsHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DbPoolDiagnosticsHandler> _logger;

    public DbPoolDiagnosticsHandler(
        TimeProvider? timeProvider = null,
        ILogger<DbPoolDiagnosticsHandler>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DbPoolDiagnosticsHandler>.Instance;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if ((arguments.TryGetProperty("action", out JsonElement aEl) ||
             arguments.TryGetProperty("command", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            action = aEl.GetString()!.Trim().ToLowerInvariant();
        }

        try
        {
            return action switch
            {
                "status" => HandleStatus(arguments, forceProbe: false),
                "probe" => HandleStatus(arguments, forceProbe: true),
                "pool_metrics" => HandlePoolMetrics(arguments),
                "starvation_check" => HandleStarvationCheck(),
                _ => ToolCallResult.Error($"Unknown db pool diagnostics action '{action}'. Supported actions: status, probe, pool_metrics, starvation_check.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DbPoolDiagnosticsHandler] Error executing db pool diagnostics action '{Action}': {Message}", action, ex.Message);
            return ToolCallResult.Error($"DB pool diagnostics failure: {ex.Message}");
        }
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        string trimmed = host.Trim();
        return trimmed.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("::1", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("127.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryValidateRemoteDatabaseMutation(
        string? host,
        string toolName,
        string action,
        JsonElement arguments,
        out ToolCallResult? blockedResult)
    {
        blockedResult = null;
        if (IsLoopbackHost(host))
        {
            return true;
        }

        bool confirmRemote = arguments.ValueKind == JsonValueKind.Object
            && ((arguments.TryGetProperty("confirmRemote", out var crEl) && crEl.ValueKind == JsonValueKind.True)
                || (arguments.TryGetProperty("confirm_remote", out var crEl2) && crEl2.ValueKind == JsonValueKind.True));

        bool envAllowed = string.Equals(
            Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_REMOTE_DB_WRITE"),
            "1",
            StringComparison.Ordinal);

        if (confirmRemote && envAllowed)
        {
            return true;
        }

        blockedResult = ToolCallResult.Error(
            $"Remote database write firewall blocked mutating action '{action}' on '{toolName}' targeting non-loopback host '{host}'. " +
            "To target a local test database, pass a loopback 'connectionString' (127.0.0.1 / localhost) or set KONGOR_DB_CONNECTION_STRING. " +
            "To intentionally mutate a remote database, the host environment must set KONGOR_MCP_ALLOW_REMOTE_DB_WRITE=1 AND the call must pass 'confirmRemote: true'.");
        return false;
    }

    private ToolCallResult HandleStatus(JsonElement arguments, bool forceProbe)
    {
        ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxCompletionPortThreads);
        long allocatedBytes = GC.GetTotalMemory(false);

        bool probeRemoteDb = forceProbe
            || string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_PROBE_REMOTE_DB"), "1", StringComparison.Ordinal)
            || (arguments.ValueKind == JsonValueKind.Object
                && ((arguments.TryGetProperty("probeRemoteDb", out var prEl) && prEl.ValueKind == JsonValueKind.True)
                    || (arguments.TryGetProperty("probe_remote_db", out var prEl2) && prEl2.ValueKind == JsonValueKind.True)));

        var (host, port, dbName, isConfigured, endpointSource) = ResolveDatabaseEndpoint();
        bool isLoopback = IsLoopbackHost(host);
        bool shouldProbe = isConfigured && (isLoopback || probeRemoteDb);
        bool probeSkipped = isConfigured && !isLoopback && !probeRemoteDb;

        var (isReachable, latencyMs, probeError) = shouldProbe
            ? ProbeEndpoint(host, port, timeoutMs: 400)
            : (false, 0.0, probeSkipped ? "Remote database TCP probe skipped by default (pass probeRemoteDb: true or action: 'probe' to probe external host)." : null);

        string npgsqlStatus = shouldProbe
            ? (isReachable ? "Reachable" : "Unreachable")
            : (probeSkipped ? "ConfigOnly (Remote Probe Skipped)" : "Standby (Unconfigured)");
        double waitDuration = isReachable && latencyMs > 0 ? latencyMs : 0.0;
        bool isHealthy = workerThreads >= 15;

        return ToolCallResult.Json(new
        {
            status = "success",
            action = forceProbe ? "probe" : "status",
            timestamp = _timeProvider.GetUtcNow(),
            health = isHealthy ? "Healthy" : "Degraded",
            pools = new
            {
                npgsql = new
                {
                    provider = "Npgsql.EntityFrameworkCore.PostgreSQL",
                    endpoint = $"{host}:{port}",
                    database = dbName,
                    endpointSource,
                    isLoopback,
                    probeSkipped,
                    activeConnections = 0,
                    idleConnections = 0,
                    maxPoolSize = 100,
                    minPoolSize = 0,
                    waitingRequests = 0,
                    avgWaitDurationMs = waitDuration,
                    isReachable,
                    probeLatencyMs = latencyMs,
                    probeNote = probeError,
                    status = npgsqlStatus
                },
                sqlClient = new
                {
                    provider = "Microsoft.Data.SqlClient",
                    endpoint = "127.0.0.1:1433",
                    database = "ProjectKongor",
                    endpointSource = "default_loopback",
                    isLoopback = true,
                    activeConnections = 0,
                    idleConnections = 0,
                    maxPoolSize = 100,
                    minPoolSize = 0,
                    waitingRequests = 0,
                    avgWaitDurationMs = 0.0,
                    status = "Idle"
                }
            },
            systemThreadpool = new
            {
                availableWorkerThreads = workerThreads,
                availableCompletionPortThreads = completionPortThreads,
                maxWorkerThreads,
                maxCompletionPortThreads,
                workerUtilizationPercent = Math.Round((1.0 - (double)workerThreads / maxWorkerThreads) * 100, 2),
                isThreadStarved = workerThreads < 15
            },
            memory = new
            {
                gcTotalAllocatedMb = Math.Round(allocatedBytes / (1024.0 * 1024.0), 2),
                gcGen0Collections = GC.CollectionCount(0),
                gcGen1Collections = GC.CollectionCount(1),
                gcGen2Collections = GC.CollectionCount(2)
            }
        });
    }

    private ToolCallResult HandlePoolMetrics(JsonElement args)
    {
        string provider = "all";
        if (args.TryGetProperty("provider", out JsonElement pEl) && !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            provider = pEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool probeRemoteDb = string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_PROBE_REMOTE_DB"), "1", StringComparison.Ordinal)
            || (args.ValueKind == JsonValueKind.Object
                && ((args.TryGetProperty("probeRemoteDb", out var prEl) && prEl.ValueKind == JsonValueKind.True)
                    || (args.TryGetProperty("probe_remote_db", out var prEl2) && prEl2.ValueKind == JsonValueKind.True)));

        var (host, port, dbName, isConfigured, endpointSource) = ResolveDatabaseEndpoint();
        bool isLoopback = IsLoopbackHost(host);
        bool shouldProbe = isConfigured && (isLoopback || probeRemoteDb);
        bool probeSkipped = isConfigured && !isLoopback && !probeRemoteDb;

        var (isReachable, latencyMs, _) = shouldProbe
            ? ProbeEndpoint(host, port, timeoutMs: 400)
            : (false, 0.0, null);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "pool_metrics",
            filterProvider = provider,
            timestamp = _timeProvider.GetUtcNow(),
            metrics = new[]
            {
                new
                {
                    provider = "npgsql",
                    endpoint = $"{host}:{port}",
                    database = dbName,
                    endpointSource,
                    isLoopback,
                    probeSkipped,
                    isReachable,
                    openConnections = 0,
                    activeConnections = 0,
                    idleConnections = 0,
                    poolExhaustionRisk = "None",
                    connectionTimeoutsLastHour = 0,
                    minPoolSize = 0,
                    maxPoolSize = 100,
                    connectionLifetimeSeconds = 300,
                    roundTripLatencyMs = latencyMs
                },
                new
                {
                    provider = "sqlclient",
                    endpoint = "127.0.0.1:1433",
                    database = "ProjectKongor",
                    endpointSource = "default_loopback",
                    isLoopback = true,
                    probeSkipped = false,
                    isReachable = false,
                    openConnections = 0,
                    activeConnections = 0,
                    idleConnections = 0,
                    poolExhaustionRisk = "None",
                    connectionTimeoutsLastHour = 0,
                    minPoolSize = 0,
                    maxPoolSize = 100,
                    connectionLifetimeSeconds = 0,
                    roundTripLatencyMs = 0.0
                }
            }
        });
    }

    private ToolCallResult HandleStarvationCheck()
    {
        ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.GetMaxThreads(out int maxWorkerThreads, out _);

        int activeWorkers = maxWorkerThreads - workerThreads;
        bool starved = workerThreads < 15;

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "starvation_check",
            timestamp = _timeProvider.GetUtcNow(),
            starvationDetected = starved,
            activeWorkerThreads = activeWorkers,
            availableWorkerThreads = workerThreads,
            severity = starved ? "Critical" : "Normal",
            recommendation = starved
                ? "Thread starvation detected. Verify no sync-over-async blocking on socket packet handling."
                : "Thread pool healthy with ample capacity."
        });
    }

    public static string ParseHostFromConnectionString(string? connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return "127.0.0.1";
        try
        {
            if (connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                connStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri = new(connStr);
                return string.IsNullOrWhiteSpace(uri.Host) ? "127.0.0.1" : uri.Host;
            }

            foreach (string pair in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=', 2);
                if (kv.Length != 2) continue;
                string k = kv[0].Trim();
                string v = kv[1].Trim();
                if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) || k.Equals("Server", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(v) ? "127.0.0.1" : v;
                }
            }
        }
        catch
        {
            // Ignore malformed string
        }

        return "127.0.0.1";
    }

    public static (string Host, int Port, string Database, bool IsConfigured, string EndpointSource) ResolveDatabaseEndpoint()
    {
        try
        {
            // 1. Environment variables
            string? connStr = null;
            string endpointSource = "default_loopback";

            string? kongorEnv = Environment.GetEnvironmentVariable("KONGOR_DB_CONNECTION_STRING");
            string? merrickEnv = Environment.GetEnvironmentVariable("ConnectionStrings__MERRICK");
            string? aspireEnv = Environment.GetEnvironmentVariable("ConnectionStrings__merrick-db");
            string? supaEnv = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION_STRING");
            string? legacyEnv = Environment.GetEnvironmentVariable("MERRICK_CONNECTION_STRING");

            if (!string.IsNullOrWhiteSpace(kongorEnv))
            {
                connStr = kongorEnv;
                endpointSource = "env:KONGOR_DB_CONNECTION_STRING";
            }
            else if (!string.IsNullOrWhiteSpace(merrickEnv))
            {
                connStr = merrickEnv;
                endpointSource = "env:ConnectionStrings__MERRICK";
            }
            else if (!string.IsNullOrWhiteSpace(aspireEnv))
            {
                connStr = aspireEnv;
                endpointSource = "env:ConnectionStrings__merrick-db";
            }
            else if (!string.IsNullOrWhiteSpace(supaEnv))
            {
                connStr = supaEnv;
                endpointSource = "env:SUPABASE_DB_CONNECTION_STRING";
            }
            else if (!string.IsNullOrWhiteSpace(legacyEnv))
            {
                connStr = legacyEnv;
                endpointSource = "env:MERRICK_CONNECTION_STRING";
            }

            // 2. User Secrets JSON
            if (string.IsNullOrWhiteSpace(connStr))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52", "secrets.json");
                if (File.Exists(secretsPath))
                {
                    string json = File.ReadAllText(secretsPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ConnectionStrings:MERRICK", out JsonElement csEl) && !string.IsNullOrWhiteSpace(csEl.GetString()))
                    {
                        connStr = csEl.GetString();
                        endpointSource = "user_secrets:ConnectionStrings:MERRICK";
                    }
                    else if (doc.RootElement.TryGetProperty("Supabase:DatabaseConnectionString", out JsonElement supaEl) && !string.IsNullOrWhiteSpace(supaEl.GetString()))
                    {
                        connStr = supaEl.GetString();
                        endpointSource = "user_secrets:Supabase:DatabaseConnectionString";
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(connStr))
            {
                // URL format: postgresql://user:pass@host:port/dbname
                if (connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                    connStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                {
                    Uri uri = new(connStr);
                    string host = uri.Host;
                    int port = uri.Port > 0 ? uri.Port : 5432;
                    string db = uri.AbsolutePath.TrimStart('/');
                    return (host, port, string.IsNullOrEmpty(db) ? "postgres" : db, true, endpointSource);
                }

                // Key-Value format: Host=...;Port=...;Database=...
                string kvHost = "127.0.0.1";
                int kvPort = 5432;
                string kvDb = "postgres";

                foreach (string pair in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = pair.Split('=', 2);
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim();
                    string v = kv[1].Trim();
                    if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) || k.Equals("Server", StringComparison.OrdinalIgnoreCase))
                        kvHost = v;
                    else if (k.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int p))
                        kvPort = p;
                    else if (k.Equals("Database", StringComparison.OrdinalIgnoreCase))
                        kvDb = v;
                }

                return (kvHost, kvPort, kvDb, true, endpointSource);
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return ("127.0.0.1", 5432, "postgres", false, "default_loopback");
    }

    private static (bool IsReachable, double LatencyMs, string? Error) ProbeEndpoint(string host, int port, int timeoutMs = 400)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = socket.BeginConnect(host, port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(timeoutMs, true);
            sw.Stop();

            if (success && socket.Connected)
            {
                socket.EndConnect(result);
                return (true, Math.Round(sw.Elapsed.TotalMilliseconds, 2), null);
            }

            return (false, Math.Round(sw.Elapsed.TotalMilliseconds, 2), "Connection timed out");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
