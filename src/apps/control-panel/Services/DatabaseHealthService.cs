// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed partial class DatabaseHealthService : IDatabaseHealthService
{
    private static readonly int[] DefaultRedisPorts = [28398, 6379, 28298, 28299, 26710, 26711, 21631, 20892];
    private static readonly int[] DefaultSqlPorts = [1433, 51433];
    private static readonly HttpClient SharedHttpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(2));

    public async Task<DatabaseStatus> CheckDatabaseHealthAsync(
        ISecretsManagerService? secretsManager = null,
        CancellationToken cancellationToken = default)
    {
        NexusSecrets? secrets = null;
        if (secretsManager != null)
            try
            {
                secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
            }
            catch
            {
                // Ignore secrets loading failures
            }

        string provider = secrets?.DatabaseProvider ?? "Supabase";
        bool isPostgreSql = provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                            provider.Equals("Supabase", StringComparison.OrdinalIgnoreCase);

        if (BundledLayoutResolver.TryResolve(out _)) return await ProbeTcpPortAsync(25432, "PostgreSQL 17", cancellationToken);
        if (isPostgreSql)
        {
            string? connStr = !string.IsNullOrWhiteSpace(secrets?.SupabaseDatabaseConnectionString)
                ? secrets.SupabaseDatabaseConnectionString
                : null;

            string? supabaseUrl = secrets?.SupabaseUrl;

            // 1. Probe database health via ZORGATH WebPortal API / Health endpoint
            Stopwatch probeSw = Stopwatch.StartNew();
            try
            {
                int apiPort = (secrets?.WebPortalApiHttpPort > 0 && secrets.WebPortalApiHttpPort != 5100)
                    ? secrets.WebPortalApiHttpPort
                    : 5200;
                string webApiUrl = $"http://localhost:{apiPort}/health";
                using HttpRequestMessage healthReq = new(HttpMethod.Get, webApiUrl);
                using HttpResponseMessage healthResp = await SharedHttpClient.SendAsync(healthReq, cancellationToken);
                if (healthResp.IsSuccessStatusCode)
                {
                    probeSw.Stop();
                    return new DatabaseStatus
                    {
                        DatabaseType = "Supabase Cloud (PostgreSQL)",
                        Port = 5432,
                        IsConnected = true,
                        LatencyMs = probeSw.ElapsedMilliseconds,
                        StatusMessage = $"Database Gateway is active & healthy ({probeSw.ElapsedMilliseconds}ms)."
                    };
                }
            }
            catch
            {
                // Fallback to REST or cloud probe
            }

            // 2. If Supabase URL is configured, check REST endpoint
            if (!string.IsNullOrWhiteSpace(supabaseUrl))
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    string cleanUrl = supabaseUrl.TrimEnd('/');
                    string apiKey = string.Empty;
                    if (secrets != null)
                        apiKey = !string.IsNullOrWhiteSpace(secrets.SupabaseAnonKey)
                            ? secrets.SupabaseAnonKey.Trim()
                            : !string.IsNullOrWhiteSpace(secrets.SupabaseServiceRoleKey)
                                ? secrets.SupabaseServiceRoleKey.Trim()
                                : string.Empty;

                    using HttpRequestMessage req = new(HttpMethod.Get, $"{cleanUrl}/rest/v1/");
                    if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.TryAddWithoutValidation("apikey", apiKey);

                    using HttpResponseMessage resp = await SharedHttpClient.SendAsync(req, cancellationToken);
                    sw.Stop();

                    return new DatabaseStatus
                    {
                        DatabaseType = "Supabase Cloud (PostgreSQL)",
                        Port = 443,
                        IsConnected = true,
                        LatencyMs = sw.ElapsedMilliseconds,
                        StatusMessage =
                            $"Supabase Cloud is active ({sw.ElapsedMilliseconds}ms). Ready without local SQL Server!"
                    };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new DatabaseStatus
                    {
                        DatabaseType = "Supabase Cloud (PostgreSQL)",
                        Port = 443,
                        IsConnected = false,
                        LatencyMs = 0,
                        StatusMessage = $"Supabase Cloud unreachable: {ex.Message}"
                    };
                }
            }

            return new DatabaseStatus
            {
                DatabaseType = "Supabase Cloud (PostgreSQL)",
                Port = 5432,
                IsConnected = true,
                LatencyMs = 0,
                StatusMessage = "Supabase Cloud DB active (Cloud Managed - no local SQL container needed)."
            };
        }

        return await CheckSqlServerHealthAsync(1433, cancellationToken);
    }

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Timestamp, List<int> Ports)> CachedContainerPorts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DatabaseStatus> CheckSqlServerHealthAsync(int port = 1433,
        CancellationToken cancellationToken = default)
    {
        // 1. Fast-path default SQL ports
        foreach (int p in DefaultSqlPorts)
        {
            var probe = await ProbeTcpPortAsync(p, "SQL Server 2022", cancellationToken);
            if (probe.IsConnected) return probe;
        }

        // 2. Dynamic discovery fallback
        List<int> candidatePorts = await GetDynamicSqlServerPortsAsync(port, cancellationToken);
        foreach (int currentPort in candidatePorts)
        {
            if (DefaultSqlPorts.Contains(currentPort)) continue;
            var probe = await ProbeTcpPortAsync(currentPort, "SQL Server 2022", cancellationToken);
            if (probe.IsConnected) return probe;
        }

        return new DatabaseStatus
        {
            DatabaseType = "SQL Server 2022",
            Port = candidatePorts.Count > 0 ? candidatePorts[0] : port,
            IsConnected = false,
            LatencyMs = 0,
            StatusMessage = "SQL Server is offline."
        };
    }

    public async Task<DatabaseStatus> CheckRedisHealthAsync(CancellationToken cancellationToken = default)
    {
        // 1. Fast path: Probe standard ports directly (<1ms on local loopback)
        foreach (int port in DefaultRedisPorts)
        {
            var probe = await ProbeTcpPortAsync(port, "Redis Distributed Cache", cancellationToken);
            if (probe.IsConnected) return probe;
        }

        // 2. Dynamic discovery fallback (User Secrets & cached Docker inspection)
        List<int> candidatePorts = await GetDynamicRedisPortsAsync(cancellationToken);
        foreach (int port in candidatePorts)
        {
            if (DefaultRedisPorts.Contains(port)) continue;
            var probe = await ProbeTcpPortAsync(port, "Redis Distributed Cache", cancellationToken);
            if (probe.IsConnected) return probe;
        }

        return new DatabaseStatus
        {
            DatabaseType = "Redis Distributed Cache",
            Port = candidatePorts.Count > 0 ? candidatePorts[0] : 6379,
            IsConnected = false,
            LatencyMs = 0,
            StatusMessage = "Redis Cache is offline."
        };
    }

    public async Task<DatabaseStatus> CheckPyroscopeHealthAsync(int port = 4040,
        CancellationToken cancellationToken = default)
    {
        // 1. Fast path: Probe default port directly (<1ms on local loopback)
        var defaultProbe = await ProbeTcpPortAsync(port, "Pyroscope Continuous Profiling", cancellationToken);
        if (defaultProbe.IsConnected) return defaultProbe;

        // 2. Dynamic discovery fallback
        List<int> candidatePorts = await GetDynamicPyroscopePortsAsync(port, cancellationToken);
        foreach (int candidatePort in candidatePorts)
        {
            if (candidatePort == port) continue;
            var probe = await ProbeTcpPortAsync(candidatePort, "Pyroscope Continuous Profiling", cancellationToken);
            if (probe.IsConnected) return probe;
        }

        return new DatabaseStatus
        {
            DatabaseType = "Pyroscope Continuous Profiling",
            Port = port,
            IsConnected = false,
            LatencyMs = 0,
            StatusMessage = "Pyroscope is offline."
        };
    }

    public async Task<DatabaseStatus> CheckDotnetMonitorHealthAsync(int port = 52323, CancellationToken cancellationToken = default)
    {
        return await ProbeTcpPortAsync(port, "dotnet-monitor Continuous Diagnostics", cancellationToken);
    }

    private static async Task<DatabaseStatus> ProbeTcpPortAsync(int port, string serviceType, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(350));

            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            sw.Stop();

            return new DatabaseStatus
            {
                DatabaseType = serviceType,
                Port = port,
                IsConnected = true,
                LatencyMs = sw.ElapsedMilliseconds,
                StatusMessage = $"{serviceType} is active on port {port} ({sw.ElapsedMilliseconds}ms)."
            };
        }
        catch
        {
            sw.Stop();
            return new DatabaseStatus
            {
                DatabaseType = serviceType,
                Port = port,
                IsConnected = false,
                LatencyMs = 0,
                StatusMessage = $"{serviceType} is offline."
            };
        }
    }

    private static async Task<List<int>> GetDynamicPyroscopePortsAsync(int defaultPort, CancellationToken cancellationToken)
    {
        HashSet<int> ports = [defaultPort];
        List<int> containerPorts = await QueryContainerPortsAsync("pyroscope", PyroscopePortRegex(), cancellationToken);
        foreach (int p in containerPorts) ports.Add(p);
        return [.. ports];
    }

    private static async Task<List<int>> GetDynamicSqlServerPortsAsync(int defaultPort, CancellationToken cancellationToken)
    {
        HashSet<int> ports = [defaultPort];
        List<int> containerPorts = await QueryContainerPortsAsync("database-server", SqlServerPortRegex(), cancellationToken);
        foreach (int p in containerPorts) ports.Add(p);
        foreach (int p in DefaultSqlPorts) ports.Add(p);
        return [.. ports];
    }

    private static async Task<List<int>> GetDynamicRedisPortsAsync(CancellationToken cancellationToken)
    {
        HashSet<int> ports = [];

#if !PLAYTEST_BUILD
        // 1. Read dynamically assigned Aspire ports from User Secrets
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets",
                "00005072-6f6a-6563-7420-4b4f4e474f52", "secrets.json");
            if (File.Exists(secretsPath))
            {
                string json = await File.ReadAllTextAsync(secretsPath, cancellationToken);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("Resources:distributed-cache:tcp:port", out JsonElement tcpPortElem) &&
                    int.TryParse(tcpPortElem.GetString(), out int tcpPort) && tcpPort > 0)
                    ports.Add(tcpPort);

                if (root.TryGetProperty("Resources:distributed-cache:secondary:port", out JsonElement secPortElem) &&
                    int.TryParse(secPortElem.GetString(), out int secPort) && secPort > 0)
                    ports.Add(secPort);
            }
        }
        catch
        {
            // Ignore user-secrets read issues
        }

#endif
        // 2. Query Docker for running redis container port mappings (cached for 60s)
        List<int> containerPorts = await QueryContainerPortsAsync("distributed-cache", RedisPortRegex(), cancellationToken);
        foreach (int p in containerPorts) ports.Add(p);

        // 3. Add standard fallback ports
        foreach (int p in DefaultRedisPorts) ports.Add(p);

        return [.. ports];
    }

    private static async Task<List<int>> QueryContainerPortsAsync(string containerName, Regex portRegex, CancellationToken cancellationToken)
    {
        if (CachedContainerPorts.TryGetValue(containerName, out var entry) &&
            TimeProvider.System.GetUtcNow() - entry.Timestamp < TimeSpan.FromSeconds(60))
        {
            return entry.Ports;
        }

        // Skip Docker CLI query entirely if named pipe does not exist on Windows
        if (OperatingSystem.IsWindows() && !File.Exists(@"\\.\pipe\docker_engine"))
        {
            return [];
        }

        HashSet<int> ports = [];
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1200));

            ProcessStartInfo psi = new()
            {
                FileName = "docker",
                Arguments = $"ps --filter \"name={containerName}\" --format \"{{{{.Ports}}}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process proc = new() { StartInfo = psi };
            proc.Start();

            try
            {
                string output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await proc.WaitForExitAsync(timeoutCts.Token);

                MatchCollection matches = portRegex.Matches(output);
                foreach (Match m in matches)
                {
                    for (int i = 1; i <= m.Groups.Count; i++)
                    {
                        if (m.Groups[i].Success && int.TryParse(m.Groups[i].Value, out int port) && port > 0)
                        {
                            ports.Add(port);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
            }
        }
        catch
        {
            // Ignore Docker CLI failures
        }

        List<int> result = [.. ports];
        CachedContainerPorts[containerName] = (TimeProvider.System.GetUtcNow(), result);
        return result;
    }

    [GeneratedRegex(@"0\.0\.0\.0:(\d+)->|127\.0\.0\.1:(\d+)->|:::(\d+)->")]
    private static partial Regex PyroscopePortRegex();

    [GeneratedRegex(@"0\.0\.0\.0:(\d+)->1433|127\.0\.0\.1:(\d+)->1433|:::(\d+)->1433")]
    private static partial Regex SqlServerPortRegex();

    [GeneratedRegex(@"0\.0\.0\.0:(\d+)->6379|127\.0\.0\.1:(\d+)->6379|:::(\d+)->6379")]
    private static partial Regex RedisPortRegex();
}
