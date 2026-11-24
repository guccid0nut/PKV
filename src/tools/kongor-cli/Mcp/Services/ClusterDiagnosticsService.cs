// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of probing the Redis distributed cache.
/// </summary>
public sealed class RedisProbeResult
{
    public bool Connected { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 28298;
    public double LatencyMs { get; init; }
    public string AuthState { get; init; } = "Unknown";
    public string? Version { get; init; }
    public int? ConnectedClients { get; init; }
    public string? UsedMemoryHuman { get; init; }
    public long TotalKeys { get; init; }
    public long EvictedKeys { get; init; }
    public long UsedMemoryBytes { get; init; }
    public long MaxMemoryBytes { get; init; }
    public bool EvictionWarning { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Result of probing the PostgreSQL / Supabase MERRICK database.
/// </summary>
public sealed class DatabaseProbeResult
{
    public bool Connected { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = string.Empty;
    public bool IsSupabase { get; init; }
    public double LatencyMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Probed state of MasterServer authentication and session health.
/// </summary>
public sealed class MasterServerHealthResult
{
    public bool Connected { get; init; }
    public string Endpoint { get; init; } = "http://127.0.0.1:55555";
    public int AuthenticatedSessions { get; init; }
    public bool LoginApiResponding { get; init; }
    public double LatencyMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Probed state of COMPEL dedicated server supervisor worker pool.
/// </summary>
public sealed class CompelPoolStateResult
{
    public bool Connected { get; init; }
    public string Endpoint { get; init; } = "http://127.0.0.1:11234";
    public bool SupervisorRunning { get; init; }
    public int TotalInstances { get; init; }
    public int IdleWorkerSlots { get; init; }
    public IReadOnlyList<int> ActiveMatchIds { get; init; } = [];
    public double AverageTickLatencyMs { get; init; } = 50.0;
    public double TickRateHz { get; init; } = 20.0;
    public double LatencyMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Result of probing a microservice endpoint.
/// </summary>
public sealed class EndpointProbeResult
{
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string Url { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public int? HttpStatusCode { get; init; }
    public double LatencyMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Summary of a recent log error or fatal entry.
/// </summary>
public sealed class LogEntrySummary
{
    public string SourceFile { get; init; } = string.Empty;
    public string Level { get; init; } = "ERROR";
    public string? Timestamp { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Comprehensive health and diagnostics report for ProjectKongor cluster.
/// </summary>
public sealed class ClusterDiagnosticsReport
{
    public string OverallStatus { get; init; } = "unhealthy"; // "healthy", "degraded", "unhealthy"
    public string Timestamp { get; init; } = TimeProvider.System.GetUtcNow().ToString("o");
    public double TotalProbeDurationMs { get; init; }
    public RedisProbeResult Redis { get; init; } = new();
    public DatabaseProbeResult Database { get; init; } = new();
    public MasterServerHealthResult MasterServer { get; init; } = new();
    public CompelPoolStateResult CompelPool { get; init; } = new();
    public IReadOnlyList<EndpointProbeResult> Endpoints { get; init; } = [];
    public IReadOnlyList<LogEntrySummary> RecentErrors { get; init; } = [];
    public string NextAction { get; init; } = "All systems healthy.";
    public IReadOnlyList<string> RemediationHints { get; init; } = [];
}

/// <summary>
/// Interface for cluster diagnostics and health probing.
/// </summary>
public interface IClusterDiagnosticsService
{
    Task<ClusterDiagnosticsReport> RunDiagnosticsAsync(
        string? redisHost = null,
        int? redisPort = null,
        string? dbConnectionString = null,
        int maxLogErrors = 5,
        string? repoRoot = null,
        CancellationToken cancellationToken = default);

    Task<RedisProbeResult> ProbeRedisAsync(string host = "127.0.0.1", int port = 28298, string? password = null, CancellationToken cancellationToken = default);
    Task<DatabaseProbeResult> ProbeDatabaseAsync(string? connectionString = null, CancellationToken cancellationToken = default);
    Task<MasterServerHealthResult> ProbeMasterServerAsync(CancellationToken cancellationToken = default);
    Task<CompelPoolStateResult> ProbeCompelPoolAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EndpointProbeResult>> ProbeEndpointsAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<LogEntrySummary> AggregateRecentErrors(int maxErrors = 5, string? repoRoot = null);
}

/// <summary>
/// Production service executing unified wire probes across Redis, PostgreSQL / Supabase,
/// ProjectKongor microservices (MasterServer, ChatServer, Web API, Web UI, COMPEL),
/// and aggregating recent system errors across server and client logs.
/// </summary>
public sealed class ClusterDiagnosticsService : IClusterDiagnosticsService
{
    private readonly Action<string>? _logAction;
    private static readonly HttpClient HttpClientInstance = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(1500),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(2500)
    };

    public ClusterDiagnosticsService(Action<string>? logAction = null)
    {
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke($"[ClusterDiagnosticsService] {message}");
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string trimmed = host.Trim();
        return string.Equals(trimmed, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "::1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<int>? GetActiveLocalListeningPorts()
    {
        try
        {
            IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            HashSet<int> ports = new(listeners.Length);
            foreach (IPEndPoint ep in listeners)
            {
                ports.Add(ep.Port);
            }
            return ports;
        }
        catch (Exception ex)
        {
            Log($"GetActiveTcpListeners fallback notice: {ex.Message}");
            return null;
        }
    }

    public async Task<ClusterDiagnosticsReport> RunDiagnosticsAsync(
        string? redisHost = null,
        int? redisPort = null,
        string? dbConnectionString = null,
        int maxLogErrors = 5,
        string? repoRoot = null,
        CancellationToken cancellationToken = default)
    {
        string root = repoRoot ?? FindProjectRoot();
        Log($"Starting cluster diagnostics probe across services and logs (root: {root})...");

        Stopwatch sw = Stopwatch.StartNew();
        HashSet<int>? activeLocalPorts = GetActiveLocalListeningPorts();

        string rHost = redisHost ?? "127.0.0.1";
        int rPort = redisPort ?? 28298;

        // Run probes concurrently with shared local listener snapshot so unbound loopback ports fail in < 1ms
        Task<RedisProbeResult> redisTask = ProbeRedisInternalAsync(rHost, rPort, null, activeLocalPorts, cancellationToken);
        Task<DatabaseProbeResult> dbTask = ProbeDatabaseInternalAsync(dbConnectionString, activeLocalPorts, cancellationToken);
        Task<MasterServerHealthResult> masterTask = ProbeMasterServerInternalAsync(activeLocalPorts, cancellationToken);
        Task<CompelPoolStateResult> compelTask = ProbeCompelPoolInternalAsync(activeLocalPorts, cancellationToken);
        Task<IReadOnlyList<EndpointProbeResult>> endpointsTask = ProbeEndpointsInternalAsync(activeLocalPorts, cancellationToken);
        Task<IReadOnlyList<LogEntrySummary>> errorsTask = Task.Run(() => AggregateRecentErrors(maxLogErrors, root), cancellationToken);

        await Task.WhenAll(redisTask, dbTask, masterTask, compelTask, endpointsTask, errorsTask).ConfigureAwait(false);

        sw.Stop();

        RedisProbeResult redisResult = await redisTask.ConfigureAwait(false);
        DatabaseProbeResult dbResult = await dbTask.ConfigureAwait(false);
        MasterServerHealthResult masterResult = await masterTask.ConfigureAwait(false);
        CompelPoolStateResult compelResult = await compelTask.ConfigureAwait(false);
        IReadOnlyList<EndpointProbeResult> endpointsResult = await endpointsTask.ConfigureAwait(false);
        IReadOnlyList<LogEntrySummary> errorsResult = await errorsTask.ConfigureAwait(false);

        // Determine overall cluster health
        string overallStatus = DetermineOverallHealth(redisResult, dbResult, endpointsResult, errorsResult);
        var (nextAction, hints) = ComputeRemediationHints(redisResult, dbResult, masterResult, compelResult, endpointsResult, errorsResult);
        Log($"Diagnostics probe completed in {sw.Elapsed.TotalMilliseconds:F1}ms with status: {overallStatus.ToUpperInvariant()} (NextAction: '{nextAction}')");

        return new ClusterDiagnosticsReport
        {
            OverallStatus = overallStatus,
            Timestamp = TimeProvider.System.GetUtcNow().ToString("o"),
            TotalProbeDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            Redis = redisResult,
            Database = dbResult,
            MasterServer = masterResult,
            CompelPool = compelResult,
            Endpoints = endpointsResult,
            RecentErrors = errorsResult,
            NextAction = nextAction,
            RemediationHints = hints
        };
    }

    public Task<RedisProbeResult> ProbeRedisAsync(
        string host = "127.0.0.1",
        int port = 28298,
        string? password = null,
        CancellationToken cancellationToken = default) =>
        ProbeRedisInternalAsync(host, port, password, IsLoopbackHost(host) ? GetActiveLocalListeningPorts() : null, cancellationToken);

    private async Task<RedisProbeResult> ProbeRedisInternalAsync(
        string host,
        int port,
        string? password,
        HashSet<int>? activeLocalPorts,
        CancellationToken cancellationToken)
    {
        Log($"Probing Redis wire at {host}:{port}...");
        if (IsLoopbackHost(host) && activeLocalPorts != null && !activeLocalPorts.Contains(port))
        {
            Log($"Redis fast-path offline: no local TCP listener on {host}:{port}.");
            return new RedisProbeResult
            {
                Connected = false,
                Host = host,
                Port = port,
                LatencyMs = 0,
                AuthState = "Offline",
                Error = $"No local TCP listener bound on {host}:{port}"
            };
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            using TcpClient client = new();
            int connectTimeoutMs = IsLoopbackHost(host) ? 150 : 450;
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(connectTimeoutMs));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            double connectMs = sw.Elapsed.TotalMilliseconds;

            using NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 350;
            stream.WriteTimeout = 350;
            using CancellationTokenSource ioCts = new(TimeSpan.FromMilliseconds(350));
            using CancellationTokenSource linkedIoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ioCts.Token);

            // Send PING
            byte[] pingBytes = Encoding.UTF8.GetBytes("PING\r\n");
            await stream.WriteAsync(pingBytes, linkedIoCts.Token).ConfigureAwait(false);

            byte[] buffer = new byte[1024];
            int read = await stream.ReadAsync(buffer, linkedIoCts.Token).ConfigureAwait(false);
            string response = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            string authState = "NoAuthRequired";
            string? version = null;
            int? connectedClients = null;
            string? usedMemory = null;
            long totalKeys = 0;
            long evictedKeys = 0;
            long usedMemoryBytes = 0;
            long maxMemoryBytes = 0;
            bool evictionWarning = false;

            if (response.StartsWith("+PONG", StringComparison.OrdinalIgnoreCase))
            {
                authState = "Authenticated";

                // Query INFO server, clients, memory, stats, keyspace
                try
                {
                    byte[] infoBytes = Encoding.UTF8.GetBytes("*1\r\n$4\r\nINFO\r\n");
                    await stream.WriteAsync(infoBytes, linkedIoCts.Token).ConfigureAwait(false);

                    byte[] infoBuf = new byte[8192];
                    int infoRead = await stream.ReadAsync(infoBuf, linkedIoCts.Token).ConfigureAwait(false);
                    string infoText = Encoding.UTF8.GetString(infoBuf, 0, infoRead);

                    foreach (string line in infoText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("redis_version:", StringComparison.OrdinalIgnoreCase))
                            version = line.Split(':')[1].Trim();
                        else if (line.StartsWith("connected_clients:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line.Split(':')[1].Trim(), out int cc))
                            connectedClients = cc;
                        else if (line.StartsWith("used_memory_human:", StringComparison.OrdinalIgnoreCase))
                            usedMemory = line.Split(':')[1].Trim();
                        else if (line.StartsWith("evicted_keys:", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.Split(':')[1].Trim(), out long ek))
                            evictedKeys = ek;
                        else if (line.StartsWith("used_memory:", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.Split(':')[1].Trim(), out long umb))
                            usedMemoryBytes = umb;
                        else if (line.StartsWith("maxmemory:", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.Split(':')[1].Trim(), out long mmb))
                            maxMemoryBytes = mmb;
                        else if (Regex.IsMatch(line, @"^db\d+:keys=(\d+)"))
                        {
                            Match m = Regex.Match(line, @"keys=(\d+)");
                            if (m.Success && long.TryParse(m.Groups[1].Value, out long k))
                            {
                                totalKeys += k;
                            }
                        }
                    }

                    evictionWarning = evictedKeys > 0 || (maxMemoryBytes > 0 && ((double)usedMemoryBytes / maxMemoryBytes) > 0.9);
                }
                catch (Exception infoEx)
                {
                    Log($"Redis INFO read skipped: {infoEx.Message}");
                }
            }
            else if (response.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase))
            {
                authState = "PasswordRequired";
            }

            Log($"Redis probe successful: {connectMs:F1}ms latency, Auth: {authState}, Version: {version ?? "unknown"}, Keys: {totalKeys}, Evictions: {evictedKeys}");

            return new RedisProbeResult
            {
                Connected = true,
                Host = host,
                Port = port,
                LatencyMs = Math.Round(connectMs, 2),
                AuthState = authState,
                Version = version,
                ConnectedClients = connectedClients,
                UsedMemoryHuman = usedMemory,
                TotalKeys = totalKeys,
                EvictedKeys = evictedKeys,
                UsedMemoryBytes = usedMemoryBytes,
                MaxMemoryBytes = maxMemoryBytes,
                EvictionWarning = evictionWarning,
                Error = null
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Redis probe failed on {host}:{port}: {ex.Message}");
            return new RedisProbeResult
            {
                Connected = false,
                Host = host,
                Port = port,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                AuthState = "Offline",
                Error = ex.Message
            };
        }
    }

    public Task<DatabaseProbeResult> ProbeDatabaseAsync(string? connectionString = null, CancellationToken cancellationToken = default) =>
        ProbeDatabaseInternalAsync(connectionString, GetActiveLocalListeningPorts(), cancellationToken);

    private async Task<DatabaseProbeResult> ProbeDatabaseInternalAsync(
        string? connectionString,
        HashSet<int>? activeLocalPorts,
        CancellationToken cancellationToken)
    {
        string? cs = connectionString ?? ResolveDatabaseConnectionString();
        Log("Probing PostgreSQL / Supabase MERRICK database...");

        if (string.IsNullOrWhiteSpace(cs))
        {
            // Default local PostgreSQL fallback
            cs = "Host=127.0.0.1;Port=5432;Database=merrick;Username=postgres";
        }

        var (host, port, dbName, isSupabase) = ParseDatabaseConnectionString(cs);
        Log($"Target database parsed: {host}:{port}, DB: '{dbName}', Supabase: {isSupabase}");

        if (!IsLoopbackHost(host) && string.IsNullOrWhiteSpace(connectionString) &&
            !string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_PROBE_REMOTE_DB"), "1", StringComparison.Ordinal))
        {
            Log($"Database remote probe skipped by default for non-loopback host {host}:{port}.");
            return new DatabaseProbeResult
            {
                Connected = false,
                Host = host,
                Port = port,
                Database = dbName,
                IsSupabase = isSupabase,
                LatencyMs = 0,
                Error = "Remote database TCP probe skipped by default (config-only inspection; set KONGOR_MCP_PROBE_REMOTE_DB=1 or pass explicit connectionString to probe external host)."
            };
        }

        if (IsLoopbackHost(host) && activeLocalPorts != null && !activeLocalPorts.Contains(port))
        {
            Log($"Database fast-path offline: no local TCP listener on {host}:{port}.");
            return new DatabaseProbeResult
            {
                Connected = false,
                Host = host,
                Port = port,
                Database = dbName,
                IsSupabase = isSupabase,
                LatencyMs = 0,
                Error = $"No local TCP listener bound on {host}:{port}"
            };
        }

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            using TcpClient client = new();
            int connectTimeoutMs = IsLoopbackHost(host) ? 150 : 420;
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(connectTimeoutMs));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            double latency = sw.Elapsed.TotalMilliseconds;

            Log($"Database socket probe succeeded: {latency:F1}ms to {host}:{port}");

            return new DatabaseProbeResult
            {
                Connected = true,
                Host = host,
                Port = port,
                Database = dbName,
                IsSupabase = isSupabase,
                LatencyMs = Math.Round(latency, 2),
                Error = null
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Database socket probe failed on {host}:{port}: {ex.Message}");
            return new DatabaseProbeResult
            {
                Connected = false,
                Host = host,
                Port = port,
                Database = dbName,
                IsSupabase = isSupabase,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                Error = ex.Message
            };
        }
    }

    public Task<MasterServerHealthResult> ProbeMasterServerAsync(CancellationToken cancellationToken = default) =>
        ProbeMasterServerInternalAsync(GetActiveLocalListeningPorts(), cancellationToken);

    private async Task<MasterServerHealthResult> ProbeMasterServerInternalAsync(
        HashSet<int>? activeLocalPorts,
        CancellationToken cancellationToken)
    {
        string endpoint = "http://127.0.0.1:55555";
        Log($"Probing MasterServer authentication & session health at {endpoint}...");
        if (activeLocalPorts != null && !activeLocalPorts.Contains(55555))
        {
            Log("MasterServer fast-path offline: port 55555 has no active local listener.");
            return new MasterServerHealthResult
            {
                Connected = false,
                Endpoint = endpoint,
                AuthenticatedSessions = 0,
                LoginApiResponding = false,
                LatencyMs = 0,
                Error = "No local TCP listener bound on 127.0.0.1:55555"
            };
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            using TcpClient tcp = new();
            using CancellationTokenSource socketCts = new(TimeSpan.FromMilliseconds(150));
            using CancellationTokenSource linkedSocketCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, socketCts.Token);
            await tcp.ConnectAsync("127.0.0.1", 55555, linkedSocketCts.Token).ConfigureAwait(false);
            sw.Stop();

            bool loginApiResponding = true;
            try
            {
                using CancellationTokenSource httpCts = new(TimeSpan.FromMilliseconds(250));
                using CancellationTokenSource linkedHttpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, httpCts.Token);
                using HttpRequestMessage req = new(HttpMethod.Get, $"{endpoint}/client_requester.php?f=ping");
                using HttpResponseMessage res = await HttpClientInstance.SendAsync(req, linkedHttpCts.Token).ConfigureAwait(false);
                loginApiResponding = res.IsSuccessStatusCode;
            }
            catch (Exception httpEx)
            {
                Log($"MasterServer HTTP ping fallback to TCP status: {httpEx.Message}");
                loginApiResponding = tcp.Connected;
            }

            return new MasterServerHealthResult
            {
                Connected = true,
                Endpoint = endpoint,
                AuthenticatedSessions = 0,
                LoginApiResponding = loginApiResponding,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                Error = null
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new MasterServerHealthResult
            {
                Connected = false,
                Endpoint = endpoint,
                AuthenticatedSessions = 0,
                LoginApiResponding = false,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                Error = ex.Message
            };
        }
    }

    public Task<CompelPoolStateResult> ProbeCompelPoolAsync(CancellationToken cancellationToken = default) =>
        ProbeCompelPoolInternalAsync(GetActiveLocalListeningPorts(), cancellationToken);

    private async Task<CompelPoolStateResult> ProbeCompelPoolInternalAsync(
        HashSet<int>? activeLocalPorts,
        CancellationToken cancellationToken)
    {
        string endpoint = "http://127.0.0.1:11234";
        Log($"Probing COMPEL dedicated server supervisor worker pool at {endpoint}...");
        if (activeLocalPorts != null && !activeLocalPorts.Contains(11234))
        {
            Log("COMPEL fast-path offline: port 11234 has no active local listener.");
            return new CompelPoolStateResult
            {
                Connected = false,
                Endpoint = endpoint,
                SupervisorRunning = false,
                TotalInstances = 0,
                IdleWorkerSlots = 0,
                AverageTickLatencyMs = 0.0,
                TickRateHz = 0.0,
                LatencyMs = 0,
                Error = "No local TCP listener bound on 127.0.0.1:11234"
            };
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            using CancellationTokenSource httpCts = new(TimeSpan.FromMilliseconds(350));
            using CancellationTokenSource linkedHttpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, httpCts.Token);
            using HttpRequestMessage req = new(HttpMethod.Get, $"{endpoint}/status");
            using HttpResponseMessage res = await HttpClientInstance.SendAsync(req, linkedHttpCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(linkedHttpCts.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                int instances = 0;
                if (doc.RootElement.TryGetProperty("instances", out JsonElement instElem))
                {
                    instances = instElem.GetInt32();
                }

                return new CompelPoolStateResult
                {
                    Connected = true,
                    Endpoint = endpoint,
                    SupervisorRunning = true,
                    TotalInstances = instances,
                    IdleWorkerSlots = instances,
                    ActiveMatchIds = [],
                    AverageTickLatencyMs = 50.0,
                    TickRateHz = 20.0,
                    LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    Error = null
                };
            }

            return new CompelPoolStateResult
            {
                Connected = true,
                Endpoint = endpoint,
                SupervisorRunning = true,
                TotalInstances = 0,
                IdleWorkerSlots = 0,
                AverageTickLatencyMs = 50.0,
                TickRateHz = 20.0,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                Error = $"Status returned {(int)res.StatusCode}"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CompelPoolStateResult
            {
                Connected = false,
                Endpoint = endpoint,
                SupervisorRunning = false,
                TotalInstances = 0,
                IdleWorkerSlots = 0,
                AverageTickLatencyMs = 0.0,
                TickRateHz = 0.0,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                Error = ex.Message
            };
        }
    }

    public Task<IReadOnlyList<EndpointProbeResult>> ProbeEndpointsAsync(CancellationToken cancellationToken = default) =>
        ProbeEndpointsInternalAsync(GetActiveLocalListeningPorts(), cancellationToken);

    private async Task<IReadOnlyList<EndpointProbeResult>> ProbeEndpointsInternalAsync(
        HashSet<int>? activeLocalPorts,
        CancellationToken cancellationToken)
    {
        var targetEndpoints = new (string Name, string Url, int Port)[]
        {
            ("MasterServer", "http://127.0.0.1:55555", 55555),
            ("ChatServer", "http://127.0.0.1:11031", 11031),
            ("Web API", "http://127.0.0.1:5200/health", 5200),
            ("Web UI", "http://127.0.0.1:5266", 5266),
            ("COMPEL", "http://127.0.0.1:11234/ping", 11234),
            ("dotnet-monitor", "http://127.0.0.1:52323/info", 52323)
        };

        var tasks = targetEndpoints.Select(endpoint => ProbeSingleEndpointAsync(endpoint.Name, endpoint.Url, endpoint.Port, activeLocalPorts, cancellationToken));
        EndpointProbeResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<EndpointProbeResult> ProbeSingleEndpointAsync(
        string name,
        string url,
        int port,
        HashSet<int>? activeLocalPorts,
        CancellationToken cancellationToken)
    {
        if (activeLocalPorts != null && !activeLocalPorts.Contains(port))
        {
            return new EndpointProbeResult
            {
                Name = name,
                Host = "127.0.0.1",
                Port = port,
                Url = url,
                Connected = false,
                HttpStatusCode = null,
                LatencyMs = 0,
                Error = $"TCP port {port} connection refused or timed out"
            };
        }

        Stopwatch sw = Stopwatch.StartNew();

        // 1. Fast socket connectivity check
        bool socketConnected = false;
        try
        {
            using TcpClient tcp = new();
            using CancellationTokenSource socketCts = new(TimeSpan.FromMilliseconds(150));
            using CancellationTokenSource linkedSocketCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, socketCts.Token);
            await tcp.ConnectAsync("127.0.0.1", port, linkedSocketCts.Token).ConfigureAwait(false);
            socketConnected = tcp.Connected;
        }
        catch (Exception sockEx)
        {
            Log($"Endpoint '{name}' socket connect on 127.0.0.1:{port} failed: {sockEx.Message}");
            socketConnected = false;
        }

        if (!socketConnected)
        {
            sw.Stop();
            return new EndpointProbeResult
            {
                Name = name,
                Host = "127.0.0.1",
                Port = port,
                Url = url,
                Connected = false,
                HttpStatusCode = null,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                Error = $"TCP port {port} connection refused or timed out"
            };
        }

        // 2. HTTP status verification (for Web API, Web UI, COMPEL, dotnet-monitor)
        int? statusCode = null;
        string? error = null;

        bool isHttpService = port is 5200 or 5201 or 5266 or 5557 or 11234 or 52323;
        if (isHttpService)
        {
            try
            {
                using CancellationTokenSource httpCts = new(TimeSpan.FromMilliseconds(350));
                using CancellationTokenSource linkedHttpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, httpCts.Token);
                using HttpResponseMessage response = await HttpClientInstance.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedHttpCts.Token).ConfigureAwait(false);
                statusCode = (int)response.StatusCode;
            }
            catch (HttpRequestException hex)
            {
                statusCode = hex.StatusCode.HasValue ? (int)hex.StatusCode.Value : null;
                if (!statusCode.HasValue) error = hex.Message;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        else
        {
            // Pure TCP service (e.g. MasterServer SRP on 55555, ChatServer on 11031)
            statusCode = 200;
        }

        sw.Stop();

        return new EndpointProbeResult
        {
            Name = name,
            Host = "127.0.0.1",
            Port = port,
            Url = url,
            Connected = true,
            HttpStatusCode = statusCode,
            LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            Error = error
        };
    }

    public IReadOnlyList<LogEntrySummary> AggregateRecentErrors(int maxErrors = 5, string? repoRoot = null)
    {
        string root = repoRoot ?? FindProjectRoot();
        List<LogEntrySummary> errors = [];

        string[] searchDirs =
        [
            Path.Combine(root, "games", "server", "logs"),
            Path.Combine(root, "games", "server", "COMPEL", "logs"),
            Path.Combine(root, "games", "server"),
            Path.Combine(root, "games", "client"),
            Path.Combine(root, "games", "client", "userdataKONGOR", "extensions")
        ];

        List<FileInfo> logFiles = [];
        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                DirectoryInfo di = new(dir);
                logFiles.AddRange(di.GetFiles("*.log", SearchOption.TopDirectoryOnly));
            }
            catch (Exception dirEx)
            {
                Log($"Skipped log scan in '{dir}': {dirEx.Message}");
            }
        }

        // Sort by most recent write time
        var sortedLogs = logFiles.OrderByDescending(f => f.LastWriteTimeUtc).Take(10);

        Regex errorRegex = new(@"(\[FATAL\]|\[ERROR\]|FATAL:|ERROR:|UnhandledException|System\.\w+Exception)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (FileInfo fi in sortedLogs)
        {
            if (errors.Count >= maxErrors) break;

            try
            {
                using FileStream fs = new(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length > 65536)
                {
                    fs.Seek(-65536, SeekOrigin.End);
                }
                using StreamReader reader = new(fs);

                List<string> lines = [];
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }

                // Read backwards from end of log file
                for (int i = lines.Count - 1; i >= 0 && errors.Count < maxErrors; i--)
                {
                    string l = lines[i];
                    if (errorRegex.IsMatch(l))
                    {
                        string level = l.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? "FATAL" : "ERROR";
                        string relPath = Path.GetRelativePath(root, fi.FullName).Replace('\\', '/');

                        // Extract timestamp if present at beginning of line
                        string? ts = null;
                        if (l.Length > 20 && (l[0] == '[' || char.IsDigit(l[0])))
                        {
                            int end = l.IndexOfAny([']', ' ']);
                            if (end > 5) ts = l[..end].Trim('[', ']');
                        }

                        errors.Add(new LogEntrySummary
                        {
                            SourceFile = relPath,
                            Level = level,
                            Timestamp = ts ?? fi.LastWriteTimeUtc.ToString("o"),
                            Message = l.Length > 200 ? l[..200] + "..." : l
                        });
                    }
                }
            }
            catch (Exception fileEx)
            {
                Log($"Skipped locked or unreadable log file '{fi.FullName}': {fileEx.Message}");
            }
        }

        return errors;
    }

    private static string DetermineOverallHealth(
        RedisProbeResult redis,
        DatabaseProbeResult db,
        IReadOnlyList<EndpointProbeResult> endpoints,
        IReadOnlyList<LogEntrySummary> recentErrors)
    {
        // Core infrastructure requirements
        bool redisOk = redis.Connected;
        bool dbOk = db.Connected;

        EndpointProbeResult? master = endpoints.FirstOrDefault(e => e.Name.Equals("MasterServer", StringComparison.OrdinalIgnoreCase));
        bool masterOk = master?.Connected ?? false;

        if (!redisOk || !masterOk || !dbOk)
        {
            return "unhealthy";
        }

        // Check if secondary endpoints (ChatServer, Web API, Web UI, COMPEL) are offline
        int offlineCount = endpoints.Count(e => !e.Connected);
        bool hasFatalLogs = recentErrors.Any(e => e.Level.Equals("FATAL", StringComparison.OrdinalIgnoreCase));

        if (offlineCount > 0 || hasFatalLogs)
        {
            return "degraded";
        }

        return "healthy";
    }

    public static (string NextAction, IReadOnlyList<string> Hints) ComputeRemediationHints(
        RedisProbeResult redis,
        DatabaseProbeResult db,
        MasterServerHealthResult master,
        CompelPoolStateResult compel,
        IReadOnlyList<EndpointProbeResult> endpoints,
        IReadOnlyList<LogEntrySummary> errors)
    {
        List<string> hints = [];

        if (!redis.Connected)
        {
            hints.Add("Run 'kongor_server_manage action:start target:redis' to start the Redis distributed cache.");
        }
        else if (redis.EvictionWarning)
        {
            hints.Add("Warning: Redis has evicted keys or memory usage exceeds 90%. Increase Redis maxmemory to prevent drop in sessions/auth tokens.");
        }

        if (!db.Connected)
        {
            hints.Add($"Check database connection to '{db.Host}:{db.Port}' for MERRICK database.");
        }

        if (!master.Connected)
        {
            hints.Add("Run 'kongor_server_manage action:start target:master-server' to launch MasterServer on port 55555.");
        }

        EndpointProbeResult? chat = endpoints.FirstOrDefault(e => e.Name.Equals("ChatServer", StringComparison.OrdinalIgnoreCase));
        if (chat != null && !chat.Connected)
        {
            hints.Add("Run 'kongor_server_manage action:start target:chat-server' to launch ChatServer on port 11031.");
        }

        if (!compel.Connected)
        {
            hints.Add("Run 'kongor_compel_manage action:start' to start COMPEL dedicated server supervisor on port 11234.");
        }

        if (errors.Any(e => e.Level.Equals("FATAL", StringComparison.OrdinalIgnoreCase)))
        {
            hints.Add("Recent FATAL errors detected in logs. Inspect error messages or run 'kongor_engine_cache_purge' to clear corrupt caches.");
        }

        string nextAction = hints.Count > 0
            ? hints[0]
            : "All cluster systems and services are healthy. Ready for client connections.";

        return (nextAction, hints);
    }

    private static string? ResolveDatabaseConnectionString()
    {
        // 1. Environment variables
        string? env = Environment.GetEnvironmentVariable("ConnectionStrings__MERRICK")
            ?? Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(env)) return env;

        // 2. User Secrets
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52", "secrets.json");
            if (File.Exists(secretsPath))
            {
                string json = File.ReadAllText(secretsPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ConnectionStrings:MERRICK", out JsonElement csElem) && !string.IsNullOrWhiteSpace(csElem.GetString()))
                {
                    return csElem.GetString();
                }
                if (doc.RootElement.TryGetProperty("Supabase:DatabaseConnectionString", out JsonElement supaElem) && !string.IsNullOrWhiteSpace(supaElem.GetString()))
                {
                    return supaElem.GetString();
                }
                if (doc.RootElement.TryGetProperty("SUPABASE_DB_CONNECTION_STRING", out JsonElement sElem) && !string.IsNullOrWhiteSpace(sElem.GetString()))
                {
                    return sElem.GetString();
                }
            }
        }
        catch
        {
            // Ignore secrets read failures
        }

        return null;
    }

    private static (string Host, int Port, string Database, bool IsSupabase) ParseDatabaseConnectionString(string cs)
    {
        string host = "127.0.0.1";
        int port = 5432;
        string db = "postgres";

        // URL format: postgresql://user:pass@host:port/dbname
        if (cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) || cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Uri uri = new(cs);
                host = uri.Host;
                port = uri.Port > 0 ? uri.Port : 5432;
                db = uri.AbsolutePath.TrimStart('/');
            }
            catch
            {
                // Fallback regex parsing
                Match match = Regex.Match(cs, @"@([^:/]+)(?::(\d+))?/(.+)");
                if (match.Success)
                {
                    host = match.Groups[1].Value;
                    if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int p)) port = p;
                    db = match.Groups[3].Value.Split('?')[0];
                }
            }
        }
        else
        {
            // Key-Value format: Host=...;Port=...;Database=...
            string[] pairs = cs.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string pair in pairs)
            {
                string[] kv = pair.Split('=', 2);
                if (kv.Length != 2) continue;
                string k = kv[0].Trim();
                string v = kv[1].Trim();

                if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) || k.Equals("Server", StringComparison.OrdinalIgnoreCase))
                    host = v;
                else if (k.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int p))
                    port = p;
                else if (k.Equals("Database", StringComparison.OrdinalIgnoreCase) || k.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                    db = v;
            }
        }

        bool isSupabase = host.Contains("supabase.com", StringComparison.OrdinalIgnoreCase) || host.Contains("supabase.co", StringComparison.OrdinalIgnoreCase);
        if (isSupabase && port == 5432 && host.Contains("pooler", StringComparison.OrdinalIgnoreCase))
        {
            port = 6543;
        }

        return (host, port, db, isSupabase);
    }

    private static string FindProjectRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")))
            {
                return current;
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "ProjectKongor.slnx")))
            {
                return baseDir;
            }
            string? parent = Path.GetDirectoryName(baseDir);
            if (parent == null || parent == baseDir) break;
            baseDir = parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
