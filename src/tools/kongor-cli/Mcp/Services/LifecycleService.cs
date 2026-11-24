// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Service status data model for MCP responses and internal tracking.
/// </summary>
public sealed class ServiceStatusInfo
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "Stopped";

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("responding")]
    public bool Responding { get; set; }

    [JsonPropertyName("additionalInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdditionalInfo { get; set; }
}

/// <summary>
/// Canonical metadata and endpoint definition for a ProjectKongor ecosystem service.
/// </summary>
public sealed record ServiceEndpointInfo(
    string ServiceKey,
    string DisplayName,
    int PrimaryPort,
    IReadOnlyList<int> AdditionalPorts,
    string ProjectName,
    string RelativeProjectPath,
    string ProcessName
);

/// <summary>
/// Structured log entry extracted from service log files.
/// </summary>
public sealed class ServiceLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = "INFO";

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; set; }
}

/// <summary>
/// Result of a cluster or microservice lifecycle operation.
/// </summary>
public sealed class ClusterLifecycleResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "success";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "status";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "cluster";

    [JsonPropertyName("clusterState")]
    public string ClusterState { get; set; } = "Stopped";

    [JsonPropertyName("dashboardUrl")]
    public string DashboardUrl { get; set; } = "https://localhost:5550";

    [JsonPropertyName("services")]
    public Dictionary<string, ServiceStatusInfo> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("recentLogs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ServiceLogEntry>? RecentLogs { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
/// Contract for the Server & Microservices Lifecycle Controller.
/// </summary>
public interface ILifecycleService
{
    Task<ClusterLifecycleResult> StartAsync(string target, bool withGameServers = false, string? customWorkingDir = null, CancellationToken cancellationToken = default);
    Task<ClusterLifecycleResult> StopAsync(string target, CancellationToken cancellationToken = default);
    Task<ClusterLifecycleResult> RestartAsync(string target, bool withGameServers = false, string? customWorkingDir = null, CancellationToken cancellationToken = default);
    Task<ClusterLifecycleResult> GetStatusAsync(string target = "cluster", bool withGameServers = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ServiceLogEntry>> GetRecentLogsAsync(string? service = null, int maxLines = 100, string? level = "all", CancellationToken cancellationToken = default);
    IReadOnlyDictionary<string, ServiceEndpointInfo> GetRegisteredEndpoints();
    Task<bool> ProbePortAsync(string host, int port, int timeoutMs = 150, CancellationToken cancellationToken = default);
    HashSet<int> GetActiveListeningPorts();
}

/// <summary>
/// Native .NET 11 implementation of the Server & Microservices Lifecycle Controller.
/// Manages Aspire microservice cluster, COMPEL dedicated servers, active TCP port probing,
/// fast process tree termination, and multi-file log tailing.
/// </summary>
public sealed partial class LifecycleService : ILifecycleService, IDisposable
{
    private static readonly string[] ClusterProcessNames =
    [
        "ASPIRE.ApplicationHost",
        "dcp",
        "dcpctrl",
        "dcp-host",
        "aspire-managed",
        "KONGOR.MasterServer",
        "TRANSMUTANSTEIN.ChatServer",
        "ZORGATH.WebPortal.API",
        "DAWNBRINGER.WebPortal.UI",
        "MERRICK.DatabaseContext",
        "COMPEL",
        "hon_x64",
        "WILLOWMAKER",
        "ReplayRenderer.Service",
        "VBCSCompiler",
        "testhost",
        "testhost.x86",
        "vstest.console"
    ];

    private static readonly Dictionary<string, ServiceEndpointInfo> KnownEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["master-server"] = new(
            "master-server",
            "KONGOR Master Server",
            55555,
            [],
            "KONGOR.MasterServer",
            "src/services/master-server/KONGOR.MasterServer.csproj",
            "KONGOR.MasterServer"
        ),
        ["chat-server"] = new(
            "chat-server",
            "TRANSMUTANSTEIN Chat Server",
            11031,
            [11032, 11033, 5554],
            "TRANSMUTANSTEIN.ChatServer",
            "src/services/chat-server/TRANSMUTANSTEIN.ChatServer.csproj",
            "TRANSMUTANSTEIN.ChatServer"
        ),
        ["web-portal-api"] = new(
            "web-portal-api",
            "ZORGATH Web Portal API",
            5200,
            [5201],
            "ZORGATH.WebPortal.API",
            "src/services/web-api/ZORGATH.WebPortal.API.csproj",
            "ZORGATH.WebPortal.API"
        ),
        ["web-portal-ui"] = new(
            "web-portal-ui",
            "DAWNBRINGER Web Portal UI",
            5266,
            [5557],
            "DAWNBRINGER.WebPortal.UI",
            "src/services/web-ui/DAWNBRINGER.WebPortal.UI.csproj",
            "DAWNBRINGER.WebPortal.UI"
        ),
        ["redis"] = new(
            "redis",
            "Distributed Redis Cache",
            28298,
            [6379, 28300],
            "distributed-cache",
            "src/services/aspire-host/ASPIRE.ApplicationHost.csproj",
            "redis"
        ),
        ["database"] = new(
            "database",
            "MERRICK Database (Supabase PostgreSQL)",
            5432,
            [],
            "MERRICK.DatabaseContext",
            "src/libs/database-context/MERRICK.DatabaseContext.csproj",
            "postgres"
        ),
        ["pyroscope"] = new(
            "pyroscope",
            "Pyroscope Continuous Profiler",
            4040,
            [],
            "pyroscope",
            "src/services/aspire-host/ASPIRE.ApplicationHost.csproj",
            "pyroscope"
        ),
        ["aspire-host"] = new(
            "aspire-host",
            "ASPIRE Application Host",
            5550,
            [5551, 5552],
            "ASPIRE.ApplicationHost",
            "src/services/aspire-host/ASPIRE.ApplicationHost.csproj",
            "ASPIRE.ApplicationHost"
        ),
        ["compel"] = new(
            "compel",
            "COMPEL Dedicated Game Server Host",
            11234,
            [21235, 21236, 21237, 21238],
            "COMPEL",
            "src/apps/compel/COMPEL.csproj",
            "COMPEL"
        ),
        ["control-panel"] = new(
            "control-panel",
            "NEXUS Control Panel Desktop GUI",
            0,
            [],
            "NEXUS.ControlPanel.GUI",
            "src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj",
            "NEXUS.ControlPanel.GUI"
        )
    };

    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _serviceStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly TextWriter _logWriter;
    private readonly string? _customSolutionRoot;
    private string _clusterState = "Stopped";
    private string _dashboardUrl = "https://localhost:5550";
    private bool _disposed;

    public LifecycleService(TimeProvider? timeProvider = null, TextWriter? logWriter = null, string? customSolutionRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logWriter = logWriter ?? Console.Error;
        _customSolutionRoot = customSolutionRoot;

        // Initialize known service states
        foreach (string key in KnownEndpoints.Keys)
        {
            _serviceStates[key] = "Stopped";
        }
    }

    public IReadOnlyDictionary<string, ServiceEndpointInfo> GetRegisteredEndpoints() => KnownEndpoints;

    public async Task<ClusterLifecycleResult> StartAsync(
        string target,
        bool withGameServers = false,
        string? customWorkingDir = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedTarget = NormalizeTarget(target);
        LogDiagnostic($"[LifecycleService] Starting target: '{normalizedTarget}' (withGameServers: {withGameServers})...");

        string repoRoot = customWorkingDir ?? FindSolutionRoot();

        try
        {
            if (normalizedTarget == "docker-stack")
            {
                _clusterState = "Starting";
                var (exitCode, output) = await DockerStackProbe.RunStackScriptAsync(
                    "up",
                    ["-Performance", "Balanced", "-StopHostConflict"],
                    repoRoot,
                    (line, _) => LogDiagnostic($"[DockerStack] {line}"),
                    cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    _clusterState = "Faulted";
                    var errStatus = await GetStatusAsync(normalizedTarget, withGameServers, cancellationToken).ConfigureAwait(false);
                    errStatus.Status = "error";
                    errStatus.Message = $"Docker stack launch exited with code {exitCode}. Inspect logs/docker_stack.log for remediation.";
                    return errStatus;
                }
                _clusterState = "Running";
            }
            else if (normalizedTarget is "cluster" or "all")
            {
                await StartClusterInternalAsync(repoRoot, withGameServers, cancellationToken).ConfigureAwait(false);
            }
            else if (normalizedTarget is "compel" or "game-servers")
            {
                await StartSingleServiceInternalAsync("compel", repoRoot, cancellationToken).ConfigureAwait(false);
            }
            else if (KnownEndpoints.ContainsKey(normalizedTarget))
            {
                await StartSingleServiceInternalAsync(normalizedTarget, repoRoot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                LogDiagnostic($"[LifecycleService] Unknown target '{target}'. Falling back to full cluster start.");
                await StartClusterInternalAsync(repoRoot, withGameServers, cancellationToken).ConfigureAwait(false);
            }

            // Inspect and return post-startup status
            return await GetStatusAsync(normalizedTarget, withGameServers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Error starting '{normalizedTarget}': {ex.Message}");
            _clusterState = "Faulted";

            var errorResult = await GetStatusAsync(normalizedTarget, withGameServers, cancellationToken).ConfigureAwait(false);
            errorResult.Status = "error";
            errorResult.Message = $"Failed to start '{normalizedTarget}': {ex.Message}";
            return errorResult;
        }
    }

    public async Task<ClusterLifecycleResult> StopAsync(string target, CancellationToken cancellationToken = default)
    {
        string normalizedTarget = NormalizeTarget(target);
        LogDiagnostic($"[LifecycleService] Stopping target: '{normalizedTarget}'...");

        try
        {
            if (normalizedTarget == "docker-stack")
            {
                _clusterState = "Stopping";
                await DockerStackProbe.RunStackScriptAsync(
                    "down",
                    repoRoot: FindSolutionRoot(),
                    onLine: (line, _) => LogDiagnostic($"[DockerStack] {line}"),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _clusterState = "Stopped";
            }
            else if (normalizedTarget is "cluster" or "all")
            {
                await StopClusterInternalAsync(cancellationToken).ConfigureAwait(false);
                DockerComposeStackSnapshot activeDocker =
                    await DockerStackProbe.ProbeAsync(FindSolutionRoot(), includeFleetHttpStatus: false, cancellationToken).ConfigureAwait(false);
                if (activeDocker.IsStackActive)
                {
                    LogDiagnostic("[LifecycleService] Active 'kongor-stack' Docker containers detected during cluster stop; running 'down'...");
                    await DockerStackProbe.RunStackScriptAsync(
                        "down",
                        repoRoot: FindSolutionRoot(),
                        onLine: (line, _) => LogDiagnostic($"[DockerStack] {line}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            else if (normalizedTarget is "compel" or "game-servers")
            {
                StopSingleServiceInternal("compel");
            }
            else if (KnownEndpoints.ContainsKey(normalizedTarget))
            {
                StopSingleServiceInternal(normalizedTarget);
            }
            else
            {
                LogDiagnostic($"[LifecycleService] Stopping all matching processes for unknown target '{target}'.");
                await StopClusterInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            return await GetStatusAsync(normalizedTarget, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Error stopping '{normalizedTarget}': {ex.Message}");
            var errorResult = await GetStatusAsync(normalizedTarget, false, cancellationToken).ConfigureAwait(false);
            errorResult.Status = "error";
            errorResult.Message = $"Failed to stop '{normalizedTarget}': {ex.Message}";
            return errorResult;
        }
    }

    public async Task<ClusterLifecycleResult> RestartAsync(
        string target,
        bool withGameServers = false,
        string? customWorkingDir = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedTarget = NormalizeTarget(target);
        LogDiagnostic($"[LifecycleService] Restarting target: '{normalizedTarget}'...");

        if (normalizedTarget == "cluster")
        {
            string repoRoot = customWorkingDir ?? FindSolutionRoot();
            bool hasHostAspire = _runningProcesses.TryGetValue("cluster", out Process? hostCluster) && SafeIsRunning(hostCluster);
            if (!hasHostAspire)
            {
                DockerComposeStackSnapshot activeDocker =
                    await DockerStackProbe.ProbeAsync(repoRoot, includeFleetHttpStatus: false, cancellationToken).ConfigureAwait(false);
                if (activeDocker.IsStackActive)
                {
                    string preset = activeDocker.PerformancePreset ?? "Balanced";
                    LogDiagnostic($"[LifecycleService] Active 'kongor-stack' detected (preset='{preset}'); restarting Docker stack instead of host Aspire...");
                    await DockerStackProbe.RunStackScriptAsync(
                        "down",
                        repoRoot: repoRoot,
                        onLine: (line, _) => LogDiagnostic($"[DockerStack] {line}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var (upExit, _) = await DockerStackProbe.RunStackScriptAsync(
                        "up",
                        ["-Performance", preset],
                        repoRoot: repoRoot,
                        onLine: (line, _) => LogDiagnostic($"[DockerStack] {line}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var restartedStatus = await GetStatusAsync(normalizedTarget, withGameServers, cancellationToken).ConfigureAwait(false);
                    if (upExit != 0)
                    {
                        restartedStatus.Status = "error";
                        restartedStatus.Message = $"Docker stack restart exited with code {upExit}.";
                    }
                    return restartedStatus;
                }
            }
        }

        await StopAsync(normalizedTarget, cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogDiagnostic("[LifecycleService] Restart delay cancelled.");
        }

        return await StartAsync(normalizedTarget, withGameServers, customWorkingDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClusterLifecycleResult> GetStatusAsync(
        string target = "cluster",
        bool withGameServers = false,
        CancellationToken cancellationToken = default)
    {
        string normalizedTarget = NormalizeTarget(target);
        LogDiagnostic($"[LifecycleService] Querying health status for '{normalizedTarget}'...");

        HashSet<int> activePorts = GetActiveListeningPorts();
        Dictionary<string, int> processMap = GetActiveProcessIdMap();

        Dictionary<string, ServiceStatusInfo> services = new(StringComparer.OrdinalIgnoreCase);

        bool isMasterRunning = activePorts.Contains(55555);
        bool isChatRunning = activePorts.Contains(11031) || activePorts.Contains(11032) || activePorts.Contains(5554);
        bool isApiRunning = activePorts.Contains(5200) || activePorts.Contains(5201);
        bool isUiRunning = activePorts.Contains(5266) || activePorts.Contains(5557);
        bool isAspireRunning = activePorts.Contains(5550) || processMap.ContainsKey("ASPIRE.ApplicationHost");
        bool isCompelRunning = activePorts.Contains(11234) || processMap.ContainsKey("COMPEL");
        bool isRedisRunning = activePorts.Contains(28298) || activePorts.Contains(6379);
        bool isPyroscopeRunning = activePorts.Contains(4040);

        foreach (KeyValuePair<string, ServiceEndpointInfo> kvp in KnownEndpoints)
        {
            string key = kvp.Key;
            ServiceEndpointInfo ep = kvp.Value;

            bool isPortListening = activePorts.Contains(ep.PrimaryPort) || ep.AdditionalPorts.Any(p => activePorts.Contains(p));
            int pid = processMap.TryGetValue(ep.ProcessName, out int procId) ? procId : 0;

            if (pid == 0 && _runningProcesses.TryGetValue(key, out Process? p) && SafeIsRunning(p))
            {
                try
                {
                    pid = p.Id;
                }
                catch
                {
                    pid = 0;
                }
            }

            string state = (isPortListening || pid > 0)
                ? "Running"
                : (_serviceStates.TryGetValue(key, out string? prev) && prev == "Starting" ? "Starting" : "Stopped");

            _serviceStates[key] = state;

            services[key] = new ServiceStatusInfo
            {
                Port = ep.PrimaryPort,
                State = state,
                ProcessId = pid,
                Responding = isPortListening
            };
        }

        // Check if remote/cloud database (e.g. Supabase Cloud) is active when local 5432 is not listening
        if (services.TryGetValue("database", out ServiceStatusInfo? dbInfo) && !dbInfo.Responding)
        {
            try
            {
                string? dbCs = Environment.GetEnvironmentVariable("ConnectionStrings__MERRICK")
                    ?? Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION_STRING")
                    ?? Environment.GetEnvironmentVariable("DATABASE_URL");

                if (string.IsNullOrWhiteSpace(dbCs))
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52", "secrets.json");
                    if (File.Exists(secretsPath))
                    {
                        string json = File.ReadAllText(secretsPath);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("ConnectionStrings:MERRICK", out JsonElement csElem))
                            dbCs = csElem.GetString();
                        else if (doc.RootElement.TryGetProperty("Supabase:DatabaseConnectionString", out JsonElement supaElem))
                            dbCs = supaElem.GetString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(dbCs) && dbCs.Contains("supabase", StringComparison.OrdinalIgnoreCase))
                {
                    services["database"] = new ServiceStatusInfo
                    {
                        Port = 6543,
                        State = "Running",
                        ProcessId = 0,
                        Responding = true,
                        AdditionalInfo = "Supabase Cloud Active"
                    };
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[LifecycleService] Ignored database connection string probe error: {ex.Message}");
            }
        }

        // Check Docker Desktop / Daemon & kongor-stack containers
        bool isDockerActive = OperatingSystem.IsWindows() && File.Exists(@"\\.\pipe\docker_engine");
        DockerComposeStackSnapshot dockerStack = isDockerActive
            ? await DockerStackProbe.ProbeAsync(FindSolutionRoot(), includeFleetHttpStatus: true, cancellationToken).ConfigureAwait(false)
            : DockerComposeStackSnapshot.Inactive(daemonAvailable: false);

        services["docker"] = new ServiceStatusInfo
        {
            Port = 0,
            State = isDockerActive ? "Running" : "Stopped",
            ProcessId = 0,
            Responding = isDockerActive,
            AdditionalInfo = dockerStack.IsStackActive
                ? $"Docker Engine active; kongor-stack running ({dockerStack.RunningContainerCount}/{dockerStack.TotalContainerCount} containers, preset={dockerStack.PerformancePreset ?? "Balanced"})."
                : (isDockerActive ? "Docker Engine is running and ready." : "Docker daemon is not running.")
        };

        if (dockerStack.IsStackActive)
        {
            (string Key, string ComposeSvc)[] dockerMappings =
            [
                ("master-server", "master-server"),
                ("chat-server", "chat-server"),
                ("web-portal-api", "web-api"),
                ("web-portal-ui", "web-ui"),
                ("redis", "garnet")
            ];
            foreach (var (svcKey, composeSvc) in dockerMappings)
            {
                DockerComposeServiceState? container = dockerStack.TryGetRunningService(composeSvc);
                if (container != null && services.TryGetValue(svcKey, out ServiceStatusInfo? existing) && existing.ProcessId == 0)
                {
                    services[svcKey] = new ServiceStatusInfo
                    {
                        Port = existing.Port,
                        State = "Running",
                        ProcessId = 0,
                        Responding = true,
                        AdditionalInfo = $"Docker container '{container.ContainerName}' ({container.DisplaySummary})"
                    };
                }
            }

            DockerComposeServiceState? pgContainer = dockerStack.TryGetRunningService("postgres");
            if (pgContainer != null)
            {
                services["database"] = new ServiceStatusInfo
                {
                    Port = 25432,
                    State = "Running",
                    ProcessId = 0,
                    Responding = true,
                    AdditionalInfo = $"Docker PostgreSQL 17 '{pgContainer.ContainerName}' ({pgContainer.DisplaySummary})"
                };
            }

            DockerComposeServiceState? matchCompel = dockerStack.TryGetRunningService("compel-dedicated");
            DockerComposeServiceState? arcadeCompel = dockerStack.TryGetRunningService("compel-queue-arcade");
            if ((matchCompel != null || arcadeCompel != null) &&
                services.TryGetValue("compel", out ServiceStatusInfo? compelExisting) &&
                compelExisting.ProcessId == 0)
            {
                services["compel"] = new ServiceStatusInfo
                {
                    Port = matchCompel != null ? 11334 : 11484,
                    State = "Running",
                    ProcessId = 0,
                    Responding = (dockerStack.MatchFleet?.Reachable == true) || (dockerStack.ArcadeFleet?.Reachable == true),
                    AdditionalInfo =
                        $"Docker COMPEL Supervisors -> Match(:11334, {dockerStack.MatchFleet?.ActiveWorkerCount ?? 0} workers), Arcade(:11484, {dockerStack.ArcadeFleet?.ActiveWorkerCount ?? 0} workers)"
                };
            }
        }

        // 1 & 3: K2 Player Game Client and Dedicated Server Workers
        Process? gameClientProc = null;
        List<(int Pid, int Port, long MemMb)> dedicatedServers = [];
        try
        {
            Process[] honProcs = Process.GetProcessesByName("hon_x64");
            if (honProcs.Length > 0)
            {
                foreach (Process p in honProcs)
                {
                    if (p.Id == Environment.ProcessId || !SafeIsRunning(p)) continue;
                    string? cmd = GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                    if (cmd != null && cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                    {
                        int svrPort = GameServerManageHandler.ExtractPortFromCommandLine(cmd) ?? 11235;
                        long mem = 0;
                        try { mem = p.WorkingSet64 / (1024 * 1024); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored dedicated server WorkingSet64 error: {ex.Message}"); }
                        dedicatedServers.Add((p.Id, svrPort, mem));
                    }
                    else if (gameClientProc == null)
                    {
                        if (cmd == null || !cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                        {
                            gameClientProc = p;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Ignored hon_x64 process enumeration error: {ex.Message}");
        }

        int clientPid = gameClientProc != null ? gameClientProc.Id : 0;
        long clientMemMb = 0;
        if (gameClientProc != null)
        {
            try { clientMemMb = gameClientProc.WorkingSet64 / (1024 * 1024); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored client WorkingSet64 error: {ex.Message}"); }
        }

        services["game-client"] = new ServiceStatusInfo
        {
            Port = 0,
            State = clientPid > 0 ? "Running" : "Stopped",
            ProcessId = clientPid,
            Responding = clientPid > 0 && gameClientProc?.Responding == true,
            AdditionalInfo = clientPid > 0
                ? $"Player Client (PID: {clientPid}, {clientMemMb} MB, IPC: \\\\.\\pipe\\willowmaker-ipc)"
                : "Player client not running"
        };

        // 2. WILLOWMAKER Desktop Launcher (WILLOWMAKER.exe)
        Process? launcherProc = null;
        try
        {
            Process[] wmProcs = Process.GetProcessesByName("WILLOWMAKER");
            if (wmProcs.Length > 0)
            {
                launcherProc = wmProcs.FirstOrDefault(p => p.Id != Environment.ProcessId && SafeIsRunning(p));
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Ignored WILLOWMAKER process enumeration error: {ex.Message}");
        }

        int launcherPid = launcherProc != null ? launcherProc.Id : 0;
        long launcherMemMb = 0;
        if (launcherProc != null)
        {
            try { launcherMemMb = launcherProc.WorkingSet64 / (1024 * 1024); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored WILLOWMAKER WorkingSet64 error: {ex.Message}"); }
        }

        services["willowmaker"] = new ServiceStatusInfo
        {
            Port = 0,
            State = launcherPid > 0 ? "Running" : "Stopped",
            ProcessId = launcherPid,
            Responding = launcherPid > 0 && launcherProc?.Responding == true,
            AdditionalInfo = launcherPid > 0
                ? $"Desktop Launcher GUI (PID: {launcherPid}, {launcherMemMb} MB)"
                : "Desktop launcher not running"
        };

        // 2b. NEXUS Control Panel Desktop GUI (NEXUS.ControlPanel.GUI)
        Process? cpProc = null;
        try
        {
            Process[] cpProcs = Process.GetProcessesByName("NEXUS.ControlPanel.GUI");
            if (cpProcs.Length > 0)
            {
                cpProc = cpProcs.FirstOrDefault(p => p.Id != Environment.ProcessId && SafeIsRunning(p));
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Ignored ControlPanel process enumeration error: {ex.Message}");
        }

        int cpPid = cpProc != null ? cpProc.Id : 0;
        long cpMemMb = 0;
        if (cpProc != null)
        {
            try { cpMemMb = cpProc.WorkingSet64 / (1024 * 1024); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored ControlPanel WorkingSet64 error: {ex.Message}"); }
        }

        services["control-panel"] = new ServiceStatusInfo
        {
            Port = 0,
            State = cpPid > 0 ? "Running" : "Stopped",
            ProcessId = cpPid,
            Responding = cpPid > 0 && cpProc?.Responding == true,
            AdditionalInfo = cpPid > 0
                ? $"NEXUS Control Panel Desktop GUI (PID: {cpPid}, {cpMemMb} MB)"
                : "NEXUS Control Panel GUI not running"
        };

        // 3. Dedicated Game Servers Status
        int firstSvrPort = dedicatedServers.Count > 0
            ? dedicatedServers[0].Port
            : (dockerStack.TotalActiveGameWorkers > 0 ? 11335 : 11235);
        int firstSvrPid = dedicatedServers.Count > 0 ? dedicatedServers[0].Pid : 0;
        bool hasAnyGameWorkers = dedicatedServers.Count > 0 || dockerStack.TotalActiveGameWorkers > 0;
        services["game-servers"] = new ServiceStatusInfo
        {
            Port = firstSvrPort,
            State = hasAnyGameWorkers ? "Running" : "Stopped",
            ProcessId = firstSvrPid,
            Responding = hasAnyGameWorkers,
            AdditionalInfo = dedicatedServers.Count > 0
                ? $"{dedicatedServers.Count} dedicated worker(s) active: {string.Join(", ", dedicatedServers.Select(s => $"Port {s.Port} (PID {s.Pid}, {s.MemMb} MB)"))}"
                : (dockerStack.TotalActiveGameWorkers > 0
                    ? $"{dockerStack.TotalActiveGameWorkers} containerized Linux dedicated worker(s) active: Match Tier D={dockerStack.MatchFleet?.ActiveWorkerCount ?? 0} (UDP 11335+), Queue Arcade Tier Q={dockerStack.ArcadeFleet?.ActiveWorkerCount ?? 0} (UDP 11485+)"
                    : "No dedicated servers active")
        };

        // Determine overall cluster state (core game services are master-server + chat-server; web-portal-api/ui are optional)
        bool isMasterHealthy = isMasterRunning || (services.TryGetValue("master-server", out ServiceStatusInfo? msInfo) && msInfo.State == "Running");
        bool isChatHealthy = isChatRunning || (services.TryGetValue("chat-server", out ServiceStatusInfo? csInfo) && csInfo.State == "Running");
        _clusterState = EvaluateClusterState(isMasterHealthy, isChatHealthy, isApiRunning, isUiRunning, isAspireRunning, _clusterState);
        if (dockerStack.IsStackActive && !isAspireRunning)
        {
            _dashboardUrl = "http://127.0.0.1:5266";
        }

        // If target is single service, ensure it is highlighted
        if (normalizedTarget is not "cluster" and not "all" && services.TryGetValue(normalizedTarget, out ServiceStatusInfo? targetStatus))
        {
            // Keep all services in dictionary for comprehensive client consumption
        }

        return new ClusterLifecycleResult
        {
            Status = "success",
            Action = "status",
            Target = normalizedTarget,
            ClusterState = _clusterState,
            DashboardUrl = _dashboardUrl,
            Services = services,
            Timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
    }

    public Task<IReadOnlyList<ServiceLogEntry>> GetRecentLogsAsync(
        string? service = null,
        int maxLines = 100,
        string? level = "all",
        CancellationToken cancellationToken = default) =>
        GetRecentLogsAsync(service, maxLines, level, customSolutionRoot: null, cancellationToken);

    public async Task<IReadOnlyList<ServiceLogEntry>> GetRecentLogsAsync(
        string? service,
        int maxLines,
        string? level,
        string? customSolutionRoot,
        CancellationToken cancellationToken = default)
    {
        int effectiveMaxLines = Math.Max(1, maxLines);
        string? effectiveCustomRoot = !string.IsNullOrWhiteSpace(customSolutionRoot)
            ? customSolutionRoot
            : _customSolutionRoot;
        string solutionRoot = !string.IsNullOrWhiteSpace(effectiveCustomRoot) && Directory.Exists(effectiveCustomRoot)
            ? effectiveCustomRoot
            : FindSolutionRoot();
        List<ServiceLogEntry> resultLogs = [];

        List<(string DirectoryPath, string DefaultTag, string Pattern, SearchOption Option)> searchPaths =
        [
            (Path.Combine(solutionRoot, "src", "services", "master-server", "logs"), "master-server", "*.log", SearchOption.TopDirectoryOnly),
            (Path.Combine(solutionRoot, "src", "services", "chat-server", "logs"), "chat-server", "*.log", SearchOption.TopDirectoryOnly),
            (Path.Combine(solutionRoot, "src", "services", "web-api", "logs"), "web-portal-api", "*.log", SearchOption.TopDirectoryOnly),
            (Path.Combine(solutionRoot, "src", "services", "web-ui", "logs"), "web-portal-ui", "*.log", SearchOption.TopDirectoryOnly),
            (Path.Combine(solutionRoot, "games", "server"), "compel", "COMPEL.log", SearchOption.TopDirectoryOnly),
            (Path.Combine(solutionRoot, "games", "server", "userdata", "game", "logs"), "compel", "*.log", SearchOption.TopDirectoryOnly),
            (Path.Combine(solutionRoot, "logs", "docker", "master-server"), "master-server", "*.log", SearchOption.AllDirectories),
            (Path.Combine(solutionRoot, "logs", "docker", "chat-server"), "chat-server", "*.log", SearchOption.AllDirectories),
            (Path.Combine(solutionRoot, "logs", "docker", "web-api"), "web-portal-api", "*.log", SearchOption.AllDirectories),
            (Path.Combine(solutionRoot, "logs", "docker", "web-ui"), "web-portal-ui", "*.log", SearchOption.AllDirectories),
            (Path.Combine(solutionRoot, "logs", "docker", "compel-dedicated"), "compel", "*.log", SearchOption.AllDirectories),
            (Path.Combine(solutionRoot, "logs", "docker", "compel-queue-arcade"), "compel", "*.log", SearchOption.AllDirectories)
        ];

        string? normalizedService = string.IsNullOrWhiteSpace(service) ? null : NormalizeTarget(service);
        string? filterTag = string.IsNullOrWhiteSpace(normalizedService) ||
                            normalizedService.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                            normalizedService.Equals("cluster", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalizedService;

        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        List<(string FilePath, string ServiceTag, DateTime LastWriteUtc, bool IsCanonicalCurrentLog)> candidates = [];

        void AddCandidateFile(string filePath, string serviceTag)
        {
            if (filterTag != null && !serviceTag.Equals(filterTag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                if (!File.Exists(fullPath) || !seenPaths.Add(fullPath))
                {
                    return;
                }

                FileInfo info = new(fullPath);
                string fileNameNoExt = Path.GetFileNameWithoutExtension(fullPath);
                bool hasRollingDigits = fileNameNoExt.Any(char.IsDigit);
                candidates.Add((fullPath, serviceTag, info.LastWriteTimeUtc, !hasRollingDigits));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored candidate log metadata error for '{filePath}': {ex.Message}");
            }
        }

        string topLogsDir = Path.Combine(solutionRoot, "logs");
        if (Directory.Exists(topLogsDir))
        {
            try
            {
                foreach (string file in Directory.GetFiles(topLogsDir, "*.log", SearchOption.TopDirectoryOnly))
                {
                    string? inferredTag = InferTopLevelLogServiceTag(Path.GetFileName(file));
                    if (inferredTag != null)
                    {
                        AddCandidateFile(file, inferredTag);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored top-level logs directory scan error: {ex.Message}");
            }
        }

        foreach ((string dir, string defaultTag, string pattern, SearchOption searchOption) in searchPaths)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!Directory.Exists(dir)) continue;

            if (filterTag != null && !defaultTag.Equals(filterTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string[] files = Directory.GetFiles(dir, pattern, searchOption);
                foreach (string file in files)
                {
                    AddCandidateFile(file, defaultTag);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored log directory read error: {ex.Message}");
            }
        }

        List<(string FilePath, string ServiceTag, DateTime LastWriteUtc, bool IsCanonicalCurrentLog)> rankedCandidates = candidates
            .OrderByDescending(c => c.LastWriteUtc)
            .ThenByDescending(c => c.IsCanonicalCurrentLog)
            .ToList();

        Dictionary<string, int> collectedPerService = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string file, string serviceTag, DateTime lastWriteUtc, bool _) in rankedCandidates)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!File.Exists(file)) continue;

            if (collectedPerService.TryGetValue(serviceTag, out int alreadyCollected) && alreadyCollected >= effectiveMaxLines)
            {
                continue;
            }

            try
            {
                using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(fs);

                List<string> lines = [];
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }

                if (lines.Count == 0) continue;

                int remainingNeeded = effectiveMaxLines - alreadyCollected;
                int startIdx = Math.Max(0, lines.Count - (effectiveMaxLines * 4));
                List<ServiceLogEntry> fileMatched = [];
                DateTimeOffset fileBaseUtc = new(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc));

                for (int i = startIdx; i < lines.Count; i++)
                {
                    string raw = lines[i];
                    string clean = AnsiEscapeRegex().Replace(raw, string.Empty);
                    string logLevel = ParseLogLevel(clean);

                    if (!MatchesSeverity(logLevel, level))
                        continue;

                    fileMatched.Add(new ServiceLogEntry
                    {
                        Timestamp = fileBaseUtc.AddTicks(i - lines.Count + 1),
                        ServiceName = serviceTag,
                        Message = clean,
                        Level = logLevel,
                        IsError = logLevel is "ERROR" or "FATAL",
                        SourceFile = Path.GetFileName(file)
                    });
                }

                if (fileMatched.Count > 0)
                {
                    IEnumerable<ServiceLogEntry> tailFromFile = fileMatched.TakeLast(remainingNeeded);
                    int addedCount = 0;
                    foreach (ServiceLogEntry entry in tailFromFile)
                    {
                        resultLogs.Add(entry);
                        addedCount++;
                    }
                    collectedPerService[serviceTag] = alreadyCollected + addedCount;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored log file read error: {ex.Message}");
            }
        }

        return resultLogs.OrderBy(l => l.Timestamp).TakeLast(effectiveMaxLines).ToList();
    }

    private static string? InferTopLevelLogServiceTag(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.StartsWith("master-server", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("master_server", StringComparison.OrdinalIgnoreCase))
        {
            return "master-server";
        }

        if (fileName.StartsWith("chat-server", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("chat_server", StringComparison.OrdinalIgnoreCase))
        {
            return "chat-server";
        }

        if (fileName.StartsWith("web-portal-api", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("web-api", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("web_api", StringComparison.OrdinalIgnoreCase))
        {
            return "web-portal-api";
        }

        if (fileName.StartsWith("web-portal-ui", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("web-ui", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("web_ui", StringComparison.OrdinalIgnoreCase))
        {
            return "web-portal-ui";
        }

        if (fileName.StartsWith("compel", StringComparison.OrdinalIgnoreCase))
        {
            return "compel";
        }

        if (fileName.StartsWith("docker_stack", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("docker-stack", StringComparison.OrdinalIgnoreCase))
        {
            return "docker-stack";
        }

        if (fileName.StartsWith("cluster", StringComparison.OrdinalIgnoreCase))
        {
            return "cluster";
        }

        return null;
    }

    public HashSet<int> GetActiveListeningPorts()
    {
        HashSet<int> ports = [];
        try
        {
            IPGlobalProperties ipProps = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] listeners = ipProps.GetActiveTcpListeners();
            foreach (IPEndPoint ep in listeners)
            {
                ports.Add(ep.Port);
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Warning: Failed to query active TCP listeners: {ex.Message}");
        }

        return ports;
    }

    public async Task<bool> ProbePortAsync(string host, int port, int timeoutMs = 150, CancellationToken cancellationToken = default)
    {
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(timeoutMs));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartClusterInternalAsync(string repoRoot, bool withGameServers, CancellationToken cancellationToken)
    {
        _clusterState = "Starting";
        Stopwatch sw = Stopwatch.StartNew();

        // 1. Fast proactive cleanup of lingering processes
        TerminateClusterProcessesFast();
        sw.Stop();
        LogDiagnostic($"[LifecycleService] Pre-start process cleanup took {sw.ElapsedMilliseconds}ms.");

        // 2. Build start info for ASPIRE.ApplicationHost
        string extraArgs = withGameServers ? " --launch-game-servers" : string.Empty;
        ProcessStartInfo startInfo = CreateFastProcessStartInfo(
            repoRoot,
            "ASPIRE.ApplicationHost",
            "ASPIRE.ApplicationHost Development",
            extraArgs
        );

        startInfo.EnvironmentVariables["DOTNET_ASPIRE_SHOW_CHILD_CONSOLE_OUTPUT"] = "true";
        startInfo.EnvironmentVariables["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        startInfo.EnvironmentVariables["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
        startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = "https://localhost:5550";

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = false };
        _runningProcesses["cluster"] = process;
        _runningProcesses["aspire-host"] = process;

        string clusterLogDir = Path.Combine(repoRoot, "logs");
        Directory.CreateDirectory(clusterLogDir);
        string clusterLogFile = Path.Combine(clusterLogDir, "cluster.log");

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                try { File.AppendAllText(clusterLogFile, $"[{_timeProvider.GetUtcNow():o}] [STDOUT] {e.Data}{Environment.NewLine}"); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored cluster stdout log append error: {ex.Message}"); }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                try { File.AppendAllText(clusterLogFile, $"[{_timeProvider.GetUtcNow():o}] [STDERR] {e.Data}{Environment.NewLine}"); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored cluster stderr log append error: {ex.Message}"); }
            }
        };

        LogDiagnostic($"[LifecycleService] Launching ASPIRE ApplicationHost ({startInfo.FileName} {startInfo.Arguments})...");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // If withGameServers is specified, also ensure COMPEL starts if not automatically spawned
        if (withGameServers)
        {
            _serviceStates["compel"] = "Starting";
        }

        // MCP-238: If ASPIRE.ApplicationHost exits early or does not bring core game services
        // (master-server:55555 and chat-server:11031) online within 2s, directly launch them.
        if (!GameServerManageHandler.IsTestExecution)
        {
            Stopwatch waitSw = Stopwatch.StartNew();
            bool masterReady = false;
            bool chatReady = false;
            while (waitSw.ElapsedMilliseconds < 2000 && !cancellationToken.IsCancellationRequested)
            {
                masterReady = await ProbePortAsync("127.0.0.1", 55555, 100, cancellationToken).ConfigureAwait(false);
                chatReady = await ProbePortAsync("127.0.0.1", 11031, 100, cancellationToken).ConfigureAwait(false);
                if (masterReady && chatReady) break;
                if (!SafeIsRunning(process)) break;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            if (!masterReady)
            {
                LogDiagnostic("[LifecycleService] MCP-238 Fallback: Starting 'master-server' directly because port 55555 is not listening after Aspire launch.");
                await StartSingleServiceInternalAsync("master-server", repoRoot, cancellationToken).ConfigureAwait(false);
            }

            if (!chatReady)
            {
                LogDiagnostic("[LifecycleService] MCP-238 Fallback: Starting 'chat-server' directly because port 11031 is not listening after Aspire launch.");
                await StartSingleServiceInternalAsync("chat-server", repoRoot, cancellationToken).ConfigureAwait(false);
            }

            if (withGameServers && !await ProbePortAsync("127.0.0.1", 11234, 100, cancellationToken).ConfigureAwait(false))
            {
                LogDiagnostic("[LifecycleService] MCP-238 Fallback: Starting 'compel' directly for withGameServers=true.");
                await StartSingleServiceInternalAsync("compel", repoRoot, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string EvaluateClusterState(
        bool isMasterHealthy,
        bool isChatHealthy,
        bool isApiRunning,
        bool isUiRunning,
        bool isAspireRunning,
        string currentState = "Stopped")
    {
        if (isMasterHealthy && isChatHealthy)
        {
            return "Running";
        }

        if (isMasterHealthy || isChatHealthy || isApiRunning || isUiRunning || isAspireRunning)
        {
            return "Degraded";
        }

        if (currentState is "Starting" or "Stopping")
        {
            return currentState;
        }

        return "Stopped";
    }

    private async Task StartSingleServiceInternalAsync(string serviceKey, string repoRoot, CancellationToken cancellationToken)
    {
        if (serviceKey == "pyroscope")
        {
            _serviceStates[serviceKey] = "Starting";
            string composeFile = Path.Combine(repoRoot, "infra", "docker", "docker-compose.pyroscope.yml");
            try
            {
                using Process dockerProc = new();
                dockerProc.StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = File.Exists(composeFile)
                        ? $"compose -f \"{composeFile}\" up -d"
                        : "run -d --name kongor-pyroscope -p 4040:4040 --restart unless-stopped grafana/pyroscope:latest",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                dockerProc.Start();
                await dockerProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                _serviceStates[serviceKey] = dockerProc.ExitCode == 0 ? "Running" : "Faulted";
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[LifecycleService] Failed to start Pyroscope via Docker: {ex.Message}");
                _serviceStates[serviceKey] = "Faulted";
            }
            return;
        }

        if (serviceKey == "redis")
        {
            _serviceStates[serviceKey] = "Starting";
            string composeFile = Path.Combine(repoRoot, "infra", "docker", "docker-compose.garnet.yml");
            try
            {
                using Process dockerProc = new();
                dockerProc.StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = File.Exists(composeFile)
                        ? $"compose -f \"{composeFile}\" up -d"
                        : "run -d --name kongor-garnet-cache -p 28298:6379 --restart unless-stopped ghcr.io/microsoft/garnet:latest",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                dockerProc.Start();
                await dockerProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                _serviceStates[serviceKey] = dockerProc.ExitCode == 0 ? "Running" : "Faulted";
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[LifecycleService] Failed to start Redis/Garnet via Docker: {ex.Message}");
                _serviceStates[serviceKey] = "Faulted";
            }
            return;
        }

        TerminateProcessByServiceName(serviceKey);

        if (!KnownEndpoints.TryGetValue(serviceKey, out ServiceEndpointInfo? ep))
        {
            throw new ArgumentException($"Unknown service '{serviceKey}'", nameof(serviceKey));
        }

        _serviceStates[serviceKey] = "Starting";

        ProcessStartInfo startInfo = CreateFastProcessStartInfo(
            repoRoot,
            ep.ProjectName,
            $"{ep.ProjectName} Development",
            string.Empty
        );

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = false };
        _runningProcesses[serviceKey] = process;

        string svcLogDir = Path.Combine(repoRoot, "logs");
        Directory.CreateDirectory(svcLogDir);
        string svcLogFile = Path.Combine(svcLogDir, $"{serviceKey}.log");

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                try { File.AppendAllText(svcLogFile, $"[{_timeProvider.GetUtcNow():o}] [STDOUT] {e.Data}{Environment.NewLine}"); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored service stdout log append error: {ex.Message}"); }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                try { File.AppendAllText(svcLogFile, $"[{_timeProvider.GetUtcNow():o}] [STDERR] {e.Data}{Environment.NewLine}"); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored service stderr log append error: {ex.Message}"); }
            }
        };

        LogDiagnostic($"[LifecycleService] Launching service '{serviceKey}' ({startInfo.FileName} {startInfo.Arguments})...");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!GameServerManageHandler.IsTestExecution && ep.PrimaryPort > 0)
        {
            Stopwatch portWait = Stopwatch.StartNew();
            while (portWait.ElapsedMilliseconds < 2500 && !cancellationToken.IsCancellationRequested)
            {
                if (await ProbePortAsync("127.0.0.1", ep.PrimaryPort, 100, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                if (!SafeIsRunning(process))
                {
                    break;
                }
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        _serviceStates[serviceKey] = SafeIsRunning(process) ? "Running" : "Faulted";
    }

    private async Task StopClusterInternalAsync(CancellationToken cancellationToken)
    {
        _clusterState = "Stopping";

        if (GameServerManageHandler.IsTestExecution)
        {
            _clusterState = "Stopped";
            foreach (string k in KnownEndpoints.Keys)
            {
                _serviceStates[k] = "Stopped";
            }
            return;
        }

        if (_runningProcesses.TryRemove("cluster", out Process? clusterProc))
        {
            if (SafeIsRunning(clusterProc))
            {
                try
                {
                    clusterProc.Kill(true);
                    await clusterProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"[LifecycleService] Cluster process termination exception: {ex.Message}");
                }
                finally
                {
                    try { clusterProc.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored clusterProc dispose error: {ex.Message}"); }
                }
            }
        }

        foreach (KeyValuePair<string, Process> kvp in _runningProcesses)
        {
            try
            {
                if (SafeIsRunning(kvp.Value))
                {
                    kvp.Value.Kill(true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored running process kill error: {ex.Message}");
            }
            finally
            {
                try { kvp.Value.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored running process dispose error: {ex.Message}"); }
            }
        }
        _runningProcesses.Clear();

        // Execute fast process tree kill for all known cluster binaries
        TerminateClusterProcessesFast();

        _clusterState = "Stopped";
        foreach (string k in KnownEndpoints.Keys)
        {
            _serviceStates[k] = "Stopped";
        }
    }

    private void StopSingleServiceInternal(string serviceKey)
    {
        _serviceStates[serviceKey] = "Stopping";

        if (serviceKey == "pyroscope")
        {
            StopMatchingDockerContainers(["kongor-pyroscope", "pyroscope"]);
            _serviceStates[serviceKey] = "Stopped";
            return;
        }

        if (serviceKey == "redis")
        {
            StopMatchingDockerContainers(["kongor-garnet-cache", "garnet-cache", "kongor-stack-garnet", "distributed-cache"]);
            _serviceStates[serviceKey] = "Stopped";
            return;
        }

        if (_runningProcesses.TryRemove(serviceKey, out Process? proc))
        {
            try
            {
                if (SafeIsRunning(proc))
                {
                    proc.Kill(true);
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[LifecycleService] Ignored service process kill error: {ex.Message}");
            }
            finally
            {
                try { proc.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[LifecycleService] Ignored service process dispose error: {ex.Message}"); }
            }
        }

        TerminateProcessByServiceName(serviceKey);
        _serviceStates[serviceKey] = "Stopped";
    }

    private void StopMatchingDockerContainers(string[] namePrefixes)
    {
        try
        {
            HashSet<string> containersToStop = new(StringComparer.OrdinalIgnoreCase);
            using (Process listProc = new())
            {
                listProc.StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps --format \"{{.Names}}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (listProc.Start())
                {
                    string stdout = listProc.StandardOutput.ReadToEnd();
                    listProc.WaitForExit(2000);
                    foreach (string line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (namePrefixes.Any(prefix => line.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            containersToStop.Add(line);
                        }
                    }
                }
            }

            if (containersToStop.Count == 0 && namePrefixes.Length > 0)
            {
                containersToStop.Add(namePrefixes[0]);
            }

            foreach (string containerName in containersToStop)
            {
                using Process dockerProc = new();
                dockerProc.StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"stop {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                dockerProc.Start();
                dockerProc.WaitForExit(3500);
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[LifecycleService] Ignored docker stop error: {ex.Message}");
        }
    }

    public static void TerminateClusterProcessesFast()
    {
        if (GameServerManageHandler.IsTestExecution)
        {
            return;
        }

        int currentPid = Environment.ProcessId;

        foreach (string procName in ClusterProcessNames)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(procName);
                foreach (Process proc in processes)
                {
                    if (proc.Id == currentPid) continue;
                    try
                    {
                        if (procName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
                        {
                            string? cmd = GameClientManageHandler.TryGetProcessCommandLine(proc.Id);
                            if (cmd == null || !cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                            {
                                continue; // Never kill player game client during cluster start/stop
                            }
                        }

                        proc.Kill(true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LifecycleService] Ignored cluster process kill error: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored cluster process query error: {ex.Message}");
            }
        }

        // Kill only cluster-service dotnet (.NET Host) processes, preserving MCP server and unrelated tools
        try
        {
            Process[] dotnetProcs = Process.GetProcessesByName("dotnet");
            foreach (Process dp in dotnetProcs)
            {
                if (dp.Id == currentPid) continue;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        string? cmd = GameClientManageHandler.TryGetProcessCommandLine(dp.Id);
                        if (cmd != null && (cmd.Contains("kongor.dll mcp", StringComparison.OrdinalIgnoreCase) || 
                                            cmd.Contains("Kongor.Cli.dll mcp", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        if (cmd != null &&
                            !cmd.Contains("master-server", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("chat-server", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("game-stats-ingestor", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("replay-processor", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("web-api", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("Kongor.AppHost", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("Kongor.Services", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("MasterServer", StringComparison.OrdinalIgnoreCase) &&
                            !cmd.Contains("ChatServer", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    dp.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LifecycleService] Ignored dotnet process kill error: {ex.Message}");
                }
                finally
                {
                    dp.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LifecycleService] Ignored dotnet process query error: {ex.Message}");
        }
    }

    public static void TerminateProcessByServiceName(string serviceKey)
    {
        if (GameServerManageHandler.IsTestExecution)
        {
            return;
        }

        string[] targetProcessNames = serviceKey.ToLowerInvariant() switch
        {
            "master-server" => ["KONGOR.MasterServer"],
            "chat-server" => ["TRANSMUTANSTEIN.ChatServer"],
            "web-portal-api" => ["ZORGATH.WebPortal.API"],
            "web-portal-ui" => ["DAWNBRINGER.WebPortal.UI"],
            "compel" or "game-servers" => ["COMPEL", "hon_x64"],
            "control-panel" or "nexus" or "admin" => ["NEXUS.ControlPanel.GUI"],
            "aspire-host" or "cluster" => ["ASPIRE.ApplicationHost", "dcp", "dcpctrl", "dcp-host", "aspire-managed"],
            _ => []
        };

        foreach (string targetName in targetProcessNames)
        {
            try
            {
                Process[] matching = Process.GetProcessesByName(targetName);
                foreach (Process proc in matching)
                {
                    try
                    {
                        if (string.Equals(targetName, "hon_x64", StringComparison.OrdinalIgnoreCase))
                        {
                            string? cmd = GameClientManageHandler.TryGetProcessCommandLine(proc.Id);
                            if (cmd == null || !cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                            {
                                continue; // Preserve user's player client!
                            }
                        }

                        proc.Kill(true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LifecycleService] Ignored target process kill error: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LifecycleService] Ignored target process query error: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, int> GetActiveProcessIdMap()
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        int currentPid = Environment.ProcessId;

        try
        {
            foreach (string procName in ClusterProcessNames)
            {
                Process[] procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    Process first = procs.FirstOrDefault(p => p.Id != currentPid) ?? procs[0];
                    map[procName] = first.Id;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LifecycleService] Ignored active process ID map error: {ex.Message}");
        }

        return map;
    }

    internal static bool SafeIsRunning(Process? process)
    {
        if (process == null) return false;
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static ProcessStartInfo CreateFastProcessStartInfo(
        string root,
        string projectName,
        string launchProfile,
        string extraArgs = "")
    {
        string relativeDir = GetProjectRelativeDir(root, projectName);
        string projectWorkingDir = Path.Combine(root, relativeDir);

        FindBestBinary(projectWorkingDir, projectName, out string? targetExe, out string? targetDll);

        ProcessStartInfo psi;

        if (targetExe != null)
        {
            psi = new ProcessStartInfo
            {
                FileName = targetExe,
                Arguments = extraArgs.Trim(),
                WorkingDirectory = projectWorkingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else if (targetDll != null)
        {
            string arguments = string.IsNullOrWhiteSpace(extraArgs)
                ? $"\"{targetDll}\""
                : $"\"{targetDll}\" {extraArgs.Trim()}";

            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = projectWorkingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            string projPath = Path.Combine(projectWorkingDir, $"{projectName}.csproj");
            if (!File.Exists(projPath))
            {
                projPath = Path.Combine(root, relativeDir, $"{projectName}.csproj");
            }

            string appArgs = string.IsNullOrWhiteSpace(extraArgs) ? string.Empty : $" -- {extraArgs.Trim()}";
            string runArgs = $"run --project \"{projPath}\" --launch-profile \"{launchProfile}\"{appArgs}";
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = runArgs,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        ApplyCliPerformanceEnvVars(psi);
        ApplyLaunchProfileEnvVars(psi, projectName, root);

        return psi;
    }

    private static void ApplyCliPerformanceEnvVars(ProcessStartInfo psi)
    {
        psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        psi.EnvironmentVariables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        EnsureDotNetSdkEnvironment(psi);
    }

    public static void EnsureDotNetSdkEnvironment(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);

        string? existingPsiRoot = psi.Environment.TryGetValue("DOTNET_ROOT", out string? val) ? val : null;
        string? dotnetRoot = ResolveDotNetRoot(
            getEnv: key => string.Equals(key, "DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(existingPsiRoot)
                ? existingPsiRoot
                : Environment.GetEnvironmentVariable(key));

        if (!string.IsNullOrWhiteSpace(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            psi.Environment["DOTNET_ROOT"] = dotnetRoot;
            if (psi.Environment.ContainsKey("DOTNET_ROOT_X64") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64")))
            {
                psi.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
            }
            if (!psi.Environment.ContainsKey("DOTNET_ROLL_FORWARD"))
            {
                psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMinor";
            }

            char pathSep = Path.PathSeparator;
            StringComparison pathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string currentPath = psi.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string normalizedRoot = dotnetRoot.TrimEnd('/', '\\');

            string[] filteredEntries = currentPath
                .Split(pathSep, StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !string.Equals(entry.TrimEnd('/', '\\'), normalizedRoot, pathCmp))
                .ToArray();

            psi.EnvironmentVariables["PATH"] = filteredEntries.Length > 0
                ? $"{dotnetRoot}{pathSep}{string.Join(pathSep, filteredEntries)}"
                : dotnetRoot;
        }
        psi.EnvironmentVariables["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
    }

    /// <summary>
    /// Auto-discovers the best .NET 11.0 runtime/SDK installation across Windows, Linux, and macOS,
    /// preferring stable GA 11.x releases over preview installations so child processes never fail hostfxr resolution.
    /// </summary>
    public static string? ResolveDotNetRoot(
        string? platformOverride = null,
        Func<string, string?>? getEnv = null,
        string? homeOverride = null)
    {
        Func<string, string?> envLookup = getEnv ?? Environment.GetEnvironmentVariable;
        string plat = (platformOverride ?? (OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux")).ToLowerInvariant();
        bool isWindows = plat is "win32" or "windows";

        string resolvedHome = !string.IsNullOrWhiteSpace(homeOverride)
            ? homeOverride
            : envLookup("USERPROFILE")
              ?? envLookup("HOME")
              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string? explicitOverride = envLookup("KONGOR_DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitOverride) && ScoreDotNetCandidate(explicitOverride, 0, isWindows) != null)
        {
            return explicitOverride;
        }

        List<string> candidates = [];
        string? envDotNetRoot = envLookup("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envDotNetRoot))
        {
            candidates.Add(envDotNetRoot);
        }

        if (isWindows)
        {
            string? localApp = envLookup("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localApp) && homeOverride == null)
            {
                localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                candidates.Add(Path.Combine(localApp, "Microsoft", "dotnet"));
            }
            else if (!string.IsNullOrWhiteSpace(resolvedHome))
            {
                candidates.Add(Path.Combine(resolvedHome, "AppData", "Local", "Microsoft", "dotnet"));
            }

            if (!string.IsNullOrWhiteSpace(resolvedHome))
            {
                candidates.Add(Path.Combine(resolvedHome, ".dotnet"));
            }

            string? progFiles = envLookup("ProgramFiles");
            if (!string.IsNullOrWhiteSpace(progFiles))
            {
                candidates.Add(Path.Combine(progFiles, "dotnet"));
            }
            if (homeOverride == null)
            {
                candidates.Add(@"C:\Program Files\dotnet");
            }
        }
        else if (plat is "darwin" or "macos" or "osx")
        {
            if (!string.IsNullOrWhiteSpace(resolvedHome))
            {
                candidates.Add(Path.Combine(resolvedHome, ".dotnet"));
            }
            if (homeOverride == null)
            {
                candidates.Add("/opt/homebrew/opt/dotnet/libexec");
                candidates.Add("/usr/local/share/dotnet");
                candidates.Add("/usr/local/opt/dotnet/libexec");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(resolvedHome))
            {
                candidates.Add(Path.Combine(resolvedHome, ".dotnet"));
            }
            if (homeOverride == null)
            {
                candidates.Add("/usr/share/dotnet");
                candidates.Add("/usr/lib/dotnet");
                candidates.Add("/usr/lib64/dotnet");
                candidates.Add("/usr/local/share/dotnet");
                candidates.Add("/snap/dotnet-sdk/current");
            }
        }

        HashSet<string> seen = new(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        (int Tier, int Minor, int Patch, int HasExe, int HasSdk11, string Prerelease, int NegPriority, string Root)? best = null;

        for (int i = 0; i < candidates.Count; i++)
        {
            string cand = candidates[i].Trim();
            string norm = cand.TrimEnd('/', '\\');
            if (string.IsNullOrWhiteSpace(norm) || !seen.Add(norm))
            {
                continue;
            }

            var scored = ScoreDotNetCandidate(cand, i, isWindows);
            if (scored == null)
            {
                continue;
            }

            var entry = (scored.Value.Tier, scored.Value.Minor, scored.Value.Patch, scored.Value.HasExe, scored.Value.HasSdk11, scored.Value.Prerelease, -i, cand);
            if (best == null || CompareDotNetScore(entry, best.Value) > 0)
            {
                best = entry;
            }
        }

        return best?.Root;
    }

    private static int CompareDotNetScore(
        (int Tier, int Minor, int Patch, int HasExe, int HasSdk11, string Prerelease, int NegPriority, string Root) a,
        (int Tier, int Minor, int Patch, int HasExe, int HasSdk11, string Prerelease, int NegPriority, string Root) b)
    {
        int c = a.Tier.CompareTo(b.Tier);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;
        c = a.HasExe.CompareTo(b.HasExe);
        if (c != 0) return c;
        c = a.HasSdk11.CompareTo(b.HasSdk11);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Prerelease, b.Prerelease);
        if (c != 0) return c;
        return a.NegPriority.CompareTo(b.NegPriority);
    }

    private static (int Tier, int Minor, int Patch, int HasExe, int HasSdk11, string Prerelease)? ScoreDotNetCandidate(
        string candidate,
        int priorityIndex,
        bool isWindows)
    {
        try
        {
            if (!Directory.Exists(candidate))
            {
                return null;
            }

            string dotnetBin = isWindows ? "dotnet.exe" : "dotnet";
            bool hasExe = File.Exists(Path.Combine(candidate, dotnetBin));
            bool hasHostFxr = Directory.Exists(Path.Combine(candidate, "host", "fxr"));
            string sharedAppDir = Path.Combine(candidate, "shared", "Microsoft.NETCore.App");
            bool hasSharedApp = Directory.Exists(sharedAppDir);

            if (!hasExe && !(hasHostFxr && hasSharedApp))
            {
                return null;
            }

            (int Minor, int Patch, bool IsStable, string Prerelease)? bestV11 = null;
            if (hasSharedApp)
            {
                foreach (string subDir in Directory.EnumerateDirectories(sharedAppDir))
                {
                    string verName = Path.GetFileName(subDir);
                    Match m = Regex.Match(verName, @"^11\.(\d+)\.(\d+)(?:-(.+))?$");
                    if (!m.Success)
                    {
                        continue;
                    }
                    int minor = int.Parse(m.Groups[1].Value);
                    int patch = int.Parse(m.Groups[2].Value);
                    string pre = m.Groups[3].Success ? m.Groups[3].Value : string.Empty;
                    bool isStable = string.IsNullOrEmpty(pre);

                    if (bestV11 == null
                        || (isStable && !bestV11.Value.IsStable)
                        || (isStable == bestV11.Value.IsStable && minor > bestV11.Value.Minor)
                        || (isStable == bestV11.Value.IsStable && minor == bestV11.Value.Minor && patch > bestV11.Value.Patch)
                        || (isStable == bestV11.Value.IsStable && minor == bestV11.Value.Minor && patch == bestV11.Value.Patch && string.CompareOrdinal(pre, bestV11.Value.Prerelease) > 0))
                    {
                        bestV11 = (minor, patch, isStable, pre);
                    }
                }
            }

            int hasSdk11 = 0;
            string sdkDir = Path.Combine(candidate, "sdk");
            if (Directory.Exists(sdkDir))
            {
                foreach (string subDir in Directory.EnumerateDirectories(sdkDir))
                {
                    if (Path.GetFileName(subDir).StartsWith("11.", StringComparison.Ordinal))
                    {
                        hasSdk11 = 1;
                        break;
                    }
                }
            }

            if (bestV11 != null)
            {
                int tier = bestV11.Value.IsStable ? 2 : 1;
                return (tier, bestV11.Value.Minor, bestV11.Value.Patch, hasExe ? 1 : 0, hasSdk11, bestV11.Value.Prerelease);
            }

            return hasExe ? (0, 0, 0, 1, hasSdk11, string.Empty) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LifecycleService] ScoreDotNetCandidate notice for '{candidate}' (idx={priorityIndex}): {ex.Message}");
            return null;
        }
    }

    private static void ApplyLaunchProfileEnvVars(ProcessStartInfo psi, string projectName, string root)
    {
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables["DOTNET_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:5551";
        psi.EnvironmentVariables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";

        if (projectName.Equals("ASPIRE.ApplicationHost", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = "https://localhost:5550";
            psi.EnvironmentVariables["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:5551";
            psi.EnvironmentVariables["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"] = "http://localhost:5552";
            psi.EnvironmentVariables["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
            psi.EnvironmentVariables["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        }
        else if (projectName.Equals("KONGOR.MasterServer", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://0.0.0.0:55555;http://localhost:55555";
        }
        else if (projectName.Equals("TRANSMUTANSTEIN.ChatServer", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://0.0.0.0:11031;http://localhost:11031";
        }
        else if (projectName.Equals("ZORGATH.WebPortal.API", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://0.0.0.0:5200;http://localhost:5200";
        }
        else if (projectName.Equals("DAWNBRINGER.WebPortal.UI", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://0.0.0.0:5266;http://localhost:5266";
        }
        else if (projectName.Equals("COMPEL", StringComparison.OrdinalIgnoreCase))
        {
            string honServerDir = Path.Combine(root, "games", "hon server");
            if (!Directory.Exists(honServerDir))
            {
                try
                {
                    string rawServer = Path.Combine(root, "games", "server");
                    Directory.CreateSymbolicLink(honServerDir, rawServer);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LifecycleService] Ignored hon server symlink creation error: {ex.Message}");
                }
            }
            if (Directory.Exists(honServerDir))
            {
                psi.WorkingDirectory = honServerDir;
                psi.EnvironmentVariables["COMPEL_INSTALLATION_DIRECTORY"] = honServerDir;
            }
            psi.EnvironmentVariables["COMPEL_USE_PROXY"] = "false";
            psi.EnvironmentVariables["COMPEL_SERVER_NAME_PREFIX"] = "^oKONGOR ARENA ^g";
        }
    }

    private static string GetProjectRelativeDir(string root, string projectName)
    {
        string relative = projectName switch
        {
            "ASPIRE.ApplicationHost" => Path.Combine("src", "services", "aspire-host"),
            "KONGOR.MasterServer" => Path.Combine("src", "services", "master-server"),
            "TRANSMUTANSTEIN.ChatServer" => Path.Combine("src", "services", "chat-server"),
            "ZORGATH.WebPortal.API" => Path.Combine("src", "services", "web-api"),
            "DAWNBRINGER.WebPortal.UI" => Path.Combine("src", "services", "web-ui"),
            "COMPEL" => Path.Combine("src", "apps", "compel"),
            "NEXUS.ControlPanel.GUI" => Path.Combine("src", "apps", "control-panel"),
            _ => projectName
        };

        string fullCandidate = Path.Combine(root, relative);
        if (Directory.Exists(fullCandidate) || File.Exists(Path.Combine(fullCandidate, $"{projectName}.csproj")))
        {
            return relative;
        }

        return relative;
    }

    private static void FindBestBinary(string projectWorkingDir, string projectName, out string? exePath, out string? dllPath)
    {
        exePath = null;
        dllPath = null;

        string[] frameworks = ["net11.0", "net10.0"];
        string[] configs = ["Debug", "Release"];

        foreach (string config in configs)
        {
            foreach (string tfm in frameworks)
            {
                string candidateExe = Path.Combine(projectWorkingDir, "bin", config, tfm, $"{projectName}.exe");
                string candidateDll = Path.Combine(projectWorkingDir, "bin", config, tfm, $"{projectName}.dll");

                if (File.Exists(candidateExe))
                {
                    exePath = candidateExe;
                    return;
                }
                if (File.Exists(candidateDll))
                {
                    dllPath = candidateDll;
                    return;
                }
            }
        }

        string binDir = Path.Combine(projectWorkingDir, "bin");
        if (Directory.Exists(binDir))
        {
            string[] exes = Directory.GetFiles(binDir, $"{projectName}.exe", SearchOption.AllDirectories);
            if (exes.Length > 0)
            {
                exePath = exes.OrderByDescending(File.GetLastWriteTimeUtc).First();
                return;
            }

            string[] dlls = Directory.GetFiles(binDir, $"{projectName}.dll", SearchOption.AllDirectories);
            if (dlls.Length > 0)
            {
                dllPath = dlls.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
        }
    }

    private static string FindSolutionRoot() => DockerStackProbe.FindRepoRoot();

    private void HandleProcessOutput(string text, bool isError)
    {
        string clean = AnsiEscapeRegex().Replace(text, string.Empty);
        if (clean.Contains("login?t=", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("Now listening on: https://localhost:5550", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("Dashboard running at", StringComparison.OrdinalIgnoreCase))
        {
            _clusterState = "Running";
            Match match = DashboardUrlRegex().Match(clean);
            if (match.Success)
            {
                _dashboardUrl = match.Value;
            }
        }
    }

    private static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "cluster";

        string t = target.Trim().ToLowerInvariant();
        return t switch
        {
            "cluster" or "all" => t,
            "docker" or "stack" or "docker-stack" => "docker-stack",
            "master" or "masterserver" or "master-server" or "kongor" => "master-server",
            "chat" or "chatserver" or "chat-server" or "transmutanstein" => "chat-server",
            "api" or "web-api" or "webapi" or "web-portal-api" or "zorgath" => "web-portal-api",
            "ui" or "web-ui" or "webui" or "web-portal-ui" or "dawnbringer" => "web-portal-ui",
            "compel" or "gameserver" or "game-server" or "game-servers" or "gameservers" => "compel",
            "redis" or "cache" or "distributed-cache" => "redis",
            "db" or "database" or "merrick" or "postgres" or "postgresql" => "database",
            "pyroscope" or "profiler" => "pyroscope",
            "aspire" or "aspire-host" or "apphost" => "aspire-host",
            "control-panel" or "nexus" or "admin" or "controlpanel" or "nexus.controlpanel.gui" or "gui" => "control-panel",
            _ => t
        };
    }

    private static string ParseLogLevel(string text)
    {
        if (text.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) || text.Contains("fatal:", StringComparison.OrdinalIgnoreCase))
            return "FATAL";
        if (text.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) || text.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WARNING:", StringComparison.OrdinalIgnoreCase) || text.Contains("warn:", StringComparison.OrdinalIgnoreCase))
            return "WARN";
        if (text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) || text.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error:", StringComparison.OrdinalIgnoreCase) || text.Contains("fail:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exception:", StringComparison.OrdinalIgnoreCase))
            return "ERROR";
        if (text.Contains("[DBUG]", StringComparison.OrdinalIgnoreCase) || text.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("dbug:", StringComparison.OrdinalIgnoreCase) || text.Contains("debug:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("trace:", StringComparison.OrdinalIgnoreCase))
            return "DEBUG";

        return "INFO";
    }

    private static bool MatchesSeverity(string actualLevel, string? filterLevel)
    {
        if (string.IsNullOrWhiteSpace(filterLevel) || filterLevel.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;

        if (filterLevel.Equals("error", StringComparison.OrdinalIgnoreCase))
            return actualLevel is "ERROR" or "FATAL";

        if (filterLevel.Equals("warn", StringComparison.OrdinalIgnoreCase) || filterLevel.Equals("warning", StringComparison.OrdinalIgnoreCase))
            return actualLevel is "WARN" or "ERROR" or "FATAL";

        if (filterLevel.Equals("info", StringComparison.OrdinalIgnoreCase))
            return true;

        return true;
    }

    private void LogDiagnostic(string message)
    {
        try
        {
            _logWriter.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] {message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LifecycleService] Ignored LogDiagnostic write error: {ex.Message}");
        }
    }

    [GeneratedRegex(@"\x1B\[[^@-~]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"https?://[^\s""']+(?:login\?t=[a-zA-Z0-9_\-]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex DashboardUrlRegex();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Note: Do NOT terminate background microservices on MCP server Dispose.
        // The cluster is designed to persist across MCP tool invocations and restarts.
        // Controlled shutdown only occurs via explicit StopAsync() or TerminateClusterProcessesFast().
        _runningProcesses.Clear();
    }
}
