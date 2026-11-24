// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Controls;
using NEXUS.ControlPanel.GUI.Services.Models;

namespace NEXUS.ControlPanel.GUI.Services.Standalone;

public sealed class StandaloneClusterSupervisor : IDisposable
{
    private readonly BundledLayout _layout;
    private readonly ISecretsManagerService? _secretsManager;
    private readonly Action<LogMessage>? _appendLog;
    private readonly Action<ServerState>? _setClusterState;
    private readonly Action<string, ServerState>? _setServiceState;
    private readonly ChildProcessJob _job = new();
    private readonly HttpClient _httpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(5));

    private volatile bool _isActive;
    private bool _disposed;

    public bool IsActive => _isActive;

    public StandaloneClusterSupervisor(
        BundledLayout layout,
        ISecretsManagerService? secretsManager = null,
        Action<LogMessage>? appendLog = null,
        Action<ServerState>? setClusterState = null,
        Action<string, ServerState>? setServiceState = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _secretsManager = secretsManager;
        _appendLog = appendLog;
        _setClusterState = setClusterState;
        _setServiceState = setServiceState;
    }

    private void Log(string service, string message, string level = "INFO", bool isError = false)
    {
        _appendLog?.Invoke(new LogMessage
        {
            ServiceName = service,
            Message = message,
            Level = level,
            IsError = isError
        });
        Console.Error.WriteLine($"[NexusStandalone] [{service}] [{level}] {message}");
    }

    private void SetServiceState(string serviceKey, ServerState state)
    {
        _setServiceState?.Invoke(serviceKey, state);
    }

    private void SetClusterState(ServerState state)
    {
        _setClusterState?.Invoke(state);
    }

    public string ComputeProjectName()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(_layout.Root.ToLowerInvariant()));
        string projectHash = Convert.ToHexString(hash).ToLowerInvariant()[..8];
        return $"kongor-{projectHash}";
    }

    public async Task StartClusterAsync(bool withGameServers = false, CancellationToken ct = default)
    {
        _isActive = true;
        SetClusterState(ServerState.Starting);

        try
        {
            Log("SUPERVISOR", "Beginning standalone cluster startup pre-flight checks...");

            // 1. Pre-flight: Docker info
            string dockerCli = DockerHealthService.ResolveDockerCliPath();
            try
            {
                using Process dockerProc = new();
                dockerProc.StartInfo = new ProcessStartInfo(dockerCli, "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                dockerProc.Start();
                if (!dockerProc.WaitForExit(10000) || dockerProc.ExitCode != 0)
                {
                    try { dockerProc.Kill(true); } catch (Exception ex) { Console.Error.WriteLine($"[NexusStandalone] Error killing docker process: {ex.Message}"); }
                    Log("DOCKER", "Docker Desktop must be running to host. Start Docker Desktop and retry Start Cluster.", "ERROR", true);
                    SetClusterState(ServerState.Faulted);
                    throw new InvalidOperationException("Docker Desktop must be running to host. Start Docker Desktop and retry Start Cluster.");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                Log("DOCKER", $"Docker CLI execution failed: {ex.Message}. Docker Desktop must be running to host.", "ERROR", true);
                SetClusterState(ServerState.Faulted);
                throw new InvalidOperationException("Docker Desktop must be running to host. Start Docker Desktop and retry Start Cluster.", ex);
            }

            // 2. Pre-flight: Port checks
            string projectName = ComputeProjectName();
            List<int> portsToCheck = [25432, 28398, 55555, 11031, 11032, 11033, 5554, 5200, 5266];
            if (withGameServers)
            {
                portsToCheck.Add(11234);
            }

            IPGlobalProperties ipProperties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] activeTcpListeners = ipProperties.GetActiveTcpListeners();
            HashSet<int> listeningPorts = activeTcpListeners.Select(ep => ep.Port).ToHashSet();

            foreach (int port in portsToCheck)
            {
                if (listeningPorts.Contains(port))
                {
                    // Allow 25432 and 28398 if owned by our own compose project
                    if ((port == 25432 || port == 28398) && IsPortOwnedByProjectContainers(projectName, port))
                    {
                        Log("SUPERVISOR", $"Port {port} is active and owned by existing project '{projectName}' container; reusing.");
                        continue;
                    }

                    var (ownerPid, ownerName) = ResolvePortOwner(port);
                    string errorMsg = $"[NexusStandalone] Port {port} is already in use by PID {ownerPid} ({ownerName})";
                    Log("SUPERVISOR", errorMsg, "ERROR", true);
                    SetClusterState(ServerState.Faulted);
                    throw new InvalidOperationException($"Port {port} is already in use by PID {ownerPid} ({ownerName}). Free the port and retry.");
                }
            }

            // 3. Pre-flight: Linux /opt/hon/config check
            if (withGameServers && OperatingSystem.IsLinux())
            {
                EnsureLinuxConfigWritable();
            }

            // 4. Ensure Secrets Bootstrapped
            Log("SUPERVISOR", "Bootstrapping standalone secrets...");
            await StandaloneSecretsBootstrapper.EnsureBootstrappedAsync(_layout, _secretsManager, msg => Log("BOOTSTRAP", msg), ct).ConfigureAwait(false);

            // 5. Compose up
            string composeFile = _layout.DockerCompose;
            if (!File.Exists(composeFile))
            {
                string fallback = Path.Combine(_layout.Root, "tools", "export", "docker-compose.standalone.yml");
                if (File.Exists(fallback))
                {
                    composeFile = fallback;
                }
            }

            Log("DOCKER", $"Launching infra containers with project '{projectName}'...");
            using (Process composeProc = new())
            {
                composeProc.StartInfo = new ProcessStartInfo(
                    dockerCli,
                    $"compose -p {projectName} --env-file \"{_layout.DockerEnv}\" -f \"{composeFile}\" up -d")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _layout.Root
                };

                composeProc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log("DOCKER", e.Data);
                };
                composeProc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log("DOCKER", e.Data, "WARN");
                };

                composeProc.Start();
                composeProc.BeginOutputReadLine();
                composeProc.BeginErrorReadLine();
                await composeProc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (composeProc.ExitCode != 0)
                {
                    SetClusterState(ServerState.Faulted);
                    throw new InvalidOperationException($"docker compose up failed with exit code {composeProc.ExitCode}");
                }
            }

            // Wait up to 60s for 25432, pg_isready, and 28398
            Log("DOCKER", "Waiting for database (25432) and cache (28398) to accept connections...");
            using var tcpWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedTcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct, tcpWaitCts.Token);
            await WaitForTcpPortAsync("127.0.0.1", 25432, linkedTcpCts.Token).ConfigureAwait(false);
            await WaitForPostgresReadyAsync(dockerCli, projectName, composeFile, linkedTcpCts.Token).ConfigureAwait(false);
            await WaitForTcpPortAsync("127.0.0.1", 28398, linkedTcpCts.Token).ConfigureAwait(false);
            Log("DOCKER", "PostgreSQL and Garnet endpoints verified healthy.");

            // 6. Build environment mapping
            var env = await BuildEnvironmentMapAsync(ct).ConfigureAwait(false);

            // 7. Boot order: MasterServer first
            SetServiceState("master-server", ServerState.Starting);
            string masterExe = _layout.ServiceExe("master-server");
            string masterDir = _layout.ServiceDir("master-server");
            Process masterProc = StartServiceProcess("master-server", masterExe, masterDir, env, "MASTER SERVER");

            Log("MASTER SERVER", "Waiting up to 240s for MasterServer DB initialization and health...");
            await WaitForHttpHealthAsync("http://127.0.0.1:55555/health", 240, masterProc, "master-server", ct).ConfigureAwait(false);
            SetServiceState("master-server", ServerState.Running);

            // 8. Parallel boot: ChatServer, WebPortalApi, WebPortalUi
            SetServiceState("chat-server", ServerState.Starting);
            SetServiceState("web-portal-api", ServerState.Starting);
            SetServiceState("web-portal-ui", ServerState.Starting);

            string chatExe = _layout.ServiceExe("chat-server");
            string chatDir = _layout.ServiceDir("chat-server");
            Process chatProc = StartServiceProcess("chat-server", chatExe, chatDir, env, "CHAT SERVER");

            string apiExe = _layout.ServiceExe("web-portal-api");
            string apiDir = _layout.ServiceDir("web-portal-api");
            Process apiProc = StartServiceProcess("web-portal-api", apiExe, apiDir, env, "WEB API");

            string uiExe = _layout.ServiceExe("web-portal-ui");
            string uiDir = _layout.ServiceDir("web-portal-ui");
            Process uiProc = StartServiceProcess("web-portal-ui", uiExe, uiDir, env, "WEB UI");

            Log("SUPERVISOR", "Waiting up to 240s for ChatServer, WebAPI, and WebUI health endpoints...");
            Task chatHealth = WaitForHttpHealthAsync("http://127.0.0.1:5554/health", 240, chatProc, "chat-server", ct);
            Task apiHealth = WaitForHttpHealthAsync("http://127.0.0.1:5200/health", 240, apiProc, "web-portal-api", ct);
            Task uiHealth = WaitForHttpHealthAsync("http://127.0.0.1:5266/health", 240, uiProc, "web-portal-ui", ct);

            await Task.WhenAll(chatHealth, apiHealth, uiHealth).ConfigureAwait(false);

            SetServiceState("chat-server", ServerState.Running);
            SetServiceState("web-portal-api", ServerState.Running);
            SetServiceState("web-portal-ui", ServerState.Running);

            SetClusterState(ServerState.Running);
            Log("SUPERVISOR", "Standalone cluster microservices successfully started and healthy.");

            // 9. COMPEL / GameServers if requested
            if (withGameServers)
            {
                await LaunchCompelAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log("SUPERVISOR", $"Cluster startup failed: {ex.Message}", "ERROR", true);
            foreach (string svc in new[] { "compel", "web-portal-ui", "web-portal-api", "chat-server", "master-server" })
            {
                try
                {
                    await _job.StopProcessAsync(svc, CancellationToken.None).ConfigureAwait(false);
                    SetServiceState(svc == "compel" ? "game-servers" : svc, ServerState.Stopped);
                }
                catch (Exception stopEx)
                {
                    Console.Error.WriteLine($"[NexusStandalone] Rollback stop failed for {svc}: {stopEx.Message}");
                }
            }
            SetClusterState(ServerState.Faulted);
            _isActive = false;
            throw;
        }
    }

    public async Task LaunchCompelAsync(CancellationToken ct = default)
    {
        SetServiceState("game-servers", ServerState.Starting);

        if (OperatingSystem.IsLinux())
        {
            EnsureLinuxConfigWritable();
        }

        var env = await BuildEnvironmentMapAsync(ct).ConfigureAwait(false);
        string compelToken = env.GetValueOrDefault("COMPEL_AUTHENTICATION_TOKEN", string.Empty);

        // Probe GET http://127.0.0.1:11234/ping
        bool isAlreadyRunning = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:11234/ping");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                isAlreadyRunning = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Ping check before COMPEL launch: {ex.Message}");
        }

        if (isAlreadyRunning)
        {
            Log("COMPEL", "COMPEL control plane is already running; signaling instances start...");
            using var startReq = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:11234/instances/start");
            if (!string.IsNullOrEmpty(compelToken))
            {
                startReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", compelToken);
            }
            using var startResp = await _httpClient.SendAsync(startReq, ct).ConfigureAwait(false);
            Log("COMPEL", $"Instances start signal response: {startResp.StatusCode}");
            SetServiceState("game-servers", ServerState.Running);
            return;
        }

        string compelExe = _layout.CompelExe;
        string? compelDir = Path.GetDirectoryName(compelExe);
        if (string.IsNullOrEmpty(compelDir)) compelDir = _layout.Root;

        Dictionary<string, string> compelEnv = new(env)
        {
            ["MASTER_SERVER_ADDRESS"] = "127.0.0.1:55555",
            ["HOST_USERNAME"] = "kongor_host",
            ["HOST_PASSWORD"] = env.GetValueOrDefault("KONGOR_BOOTSTRAP_HOST_PASSWORD", string.Empty),
            ["COMPEL_AUTHENTICATION_TOKEN"] = compelToken,
            ["COMPEL_INSTALLATION_DIRECTORY"] = _layout.DedicatedServerDir,
            ["COMPEL_CONTROL_PLANE_BIND_ADDRESS"] = "127.0.0.1",
            ["COMPEL_CONTROL_PLANE_PORT"] = "11234",
            ["COMPEL_PORT_RANGE_OFFSET"] = "0",
            ["COMPEL_CDN_SYNCHRONISATION"] = "false",
            ["COMPEL_AUTO_START"] = "true",
            ["COMPEL_INSTANCES"] = "2"
        };

        Log("COMPEL", $"Spawning COMPEL fleet from '{compelExe}'...");
        Process compelProc = StartServiceProcess("compel", compelExe, compelDir, compelEnv, "COMPEL");

        // Wait up to 120s for COMPEL /ping
        await WaitForHttpHealthAsync("http://127.0.0.1:11234/ping", 120, compelProc, "compel", ct).ConfigureAwait(false);
        SetServiceState("game-servers", ServerState.Running);
        Log("COMPEL", "COMPEL fleet control plane successfully started and ping responder verified.");
    }

    public async Task LaunchQueueArcadeAsync(CancellationToken ct = default)
    {
        SetServiceState("queue-arcade", ServerState.Starting);

        if (OperatingSystem.IsLinux())
        {
            EnsureLinuxConfigWritable();
        }

        Dictionary<string, string> env = await BuildEnvironmentMapAsync(ct).ConfigureAwait(false);
        string compelToken = env.GetValueOrDefault("COMPEL_AUTHENTICATION_TOKEN", string.Empty);

        bool isAlreadyRunning = false;
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, "http://127.0.0.1:11484/ping");
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                isAlreadyRunning = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Ping check before Queue Arcade COMPEL launch: {ex.Message}");
        }

        if (isAlreadyRunning)
        {
            Log("QUEUE ARCADE", "Queue Arcade COMPEL control plane is already running on 11484; signaling instances start...");
            using HttpRequestMessage startReq = new(HttpMethod.Post, "http://127.0.0.1:11484/instances/start");
            if (!string.IsNullOrEmpty(compelToken))
            {
                startReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", compelToken);
            }
            using HttpResponseMessage startResp = await _httpClient.SendAsync(startReq, ct).ConfigureAwait(false);
            Log("QUEUE ARCADE", $"Queue Arcade instances start signal response: {startResp.StatusCode}");
            SetServiceState("queue-arcade", ServerState.Running);
            return;
        }

        string compelExe = _layout.CompelExe;
        string? compelDir = Path.GetDirectoryName(compelExe);
        if (string.IsNullOrEmpty(compelDir)) compelDir = _layout.Root;

        Dictionary<string, string> arcadeEnv = new(env)
        {
            ["MASTER_SERVER_ADDRESS"] = "127.0.0.1:55555",
            ["HOST_USERNAME"] = "kongor_host_arc",
            ["COMPEL_USER_NAME"] = "kongor_host_arc",
            ["HOST_PASSWORD"] = env.GetValueOrDefault("KONGOR_BOOTSTRAP_HOST_PASSWORD", string.Empty),
            ["COMPEL_AUTHENTICATION_TOKEN"] = compelToken,
            ["COMPEL_INSTALLATION_DIRECTORY"] = _layout.DedicatedServerDir,
            ["COMPEL_CONTROL_PLANE_BIND_ADDRESS"] = "127.0.0.1",
            ["COMPEL_CONTROL_PLANE_PORT"] = "11484",
            ["COMPEL_PORT_RANGE_OFFSET"] = "250",
            ["COMPEL_FLEET_TIER"] = "Q",
            ["COMPEL_QUEUE_ARCADE_MODE"] = "true",
            ["KONGOR_QUEUE_ARCADE"] = "1",
            ["COMPEL_SERVER_NAME_PREFIX"] = "QUEUE ARCADE",
            ["COMPEL_CDN_SYNCHRONISATION"] = "false",
            ["COMPEL_AUTO_START"] = "true",
            ["COMPEL_INSTANCES"] = "2",
            ["COMPEL_WARM_INSTANCES_TARGET"] = "1"
        };

        Log("QUEUE ARCADE", $"Spawning Queue Arcade COMPEL fleet from '{compelExe}' on port 11484 (offset 250)...");
        Process arcadeProc = StartServiceProcess("compel-arcade", compelExe, compelDir, arcadeEnv, "QUEUE ARCADE");

        await WaitForHttpHealthAsync("http://127.0.0.1:11484/ping", 120, arcadeProc, "compel-arcade", ct).ConfigureAwait(false);
        SetServiceState("queue-arcade", ServerState.Running);
        Log("QUEUE ARCADE", "Queue Arcade COMPEL fleet control plane started on port 11484 and verified.");
    }

    public async Task StopQueueArcadeAsync(CancellationToken ct = default)
    {
        SetServiceState("queue-arcade", ServerState.Stopping);
        await _job.StopProcessAsync("compel-arcade", ct).ConfigureAwait(false);
        SetServiceState("queue-arcade", ServerState.Stopped);
        Log("QUEUE ARCADE", "Stopped standalone Queue Arcade fleet.");
    }

    public async Task StartServiceAsync(string serviceKey, CancellationToken ct = default)
    {
        if (string.Equals(serviceKey, "game-servers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(serviceKey, "compel", StringComparison.OrdinalIgnoreCase))
        {
            await LaunchCompelAsync(ct).ConfigureAwait(false);
            return;
        }

        SetServiceState(serviceKey, ServerState.Starting);
        var env = await BuildEnvironmentMapAsync(ct).ConfigureAwait(false);

        string exePath = _layout.ServiceExe(serviceKey);
        string workDir = _layout.ServiceDir(serviceKey);
        string displayName = serviceKey.ToUpperInvariant();

        Process proc = StartServiceProcess(serviceKey, exePath, workDir, env, displayName);

        string healthUrl = serviceKey switch
        {
            "master-server" => "http://127.0.0.1:55555/health",
            "chat-server" => "http://127.0.0.1:5554/health",
            "web-portal-api" => "http://127.0.0.1:5200/health",
            "web-portal-ui" => "http://127.0.0.1:5266/health",
            _ => throw new ArgumentOutOfRangeException(nameof(serviceKey), $"Unknown service key: {serviceKey}")
        };

        int timeout = 240;
        await WaitForHttpHealthAsync(healthUrl, timeout, proc, serviceKey, ct).ConfigureAwait(false);
        SetServiceState(serviceKey, ServerState.Running);
    }

    public async Task StopServiceAsync(string serviceKey, CancellationToken ct = default)
    {
        SetServiceState(serviceKey, ServerState.Stopping);
        string targetKey = (string.Equals(serviceKey, "game-servers", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serviceKey, "compel", StringComparison.OrdinalIgnoreCase))
            ? "compel"
            : serviceKey;

        await _job.StopProcessAsync(targetKey, ct).ConfigureAwait(false);
        SetServiceState(serviceKey, ServerState.Stopped);
        Log("SUPERVISOR", $"Stopped service '{serviceKey}'.");
    }

    public async Task StopClusterAsync(CancellationToken ct = default)
    {
        SetClusterState(ServerState.Stopping);
        Log("SUPERVISOR", "Stopping standalone cluster processes in reverse order...");

        // Stop in reverse order: COMPEL Arcade, COMPEL, web-portal-ui, web-portal-api, chat-server, master-server
        string[] order = ["compel-arcade", "compel", "web-portal-ui", "web-portal-api", "chat-server", "master-server"];
        foreach (string svc in order)
        {
            try
            {
                await _job.StopProcessAsync(svc, ct).ConfigureAwait(false);
                string mappedKey = svc == "compel"
                    ? "game-servers"
                    : (svc == "compel-arcade" ? "queue-arcade" : svc);
                SetServiceState(mappedKey, ServerState.Stopped);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Error stopping {svc}: {ex.Message}");
            }
        }

        // Docker compose stop (not down -v; data persists!)
        try
        {
            string dockerCli = DockerHealthService.ResolveDockerCliPath();
            string projectName = ComputeProjectName();
            string composeFile = _layout.DockerCompose;
            if (!File.Exists(composeFile))
            {
                string fallback = Path.Combine(_layout.Root, "tools", "export", "docker-compose.standalone.yml");
                if (File.Exists(fallback))
                {
                    composeFile = fallback;
                }
            }

            Log("DOCKER", $"Stopping container fleet '{projectName}'...");
            using Process composeProc = new();
            composeProc.StartInfo = new ProcessStartInfo(
                dockerCli,
                $"compose -p {projectName} --env-file \"{_layout.DockerEnv}\" -f \"{composeFile}\" stop")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _layout.Root
            };
            composeProc.Start();
            await composeProc.WaitForExitAsync(ct).ConfigureAwait(false);
            Log("DOCKER", "Container fleet stopped.");
        }
        catch (Exception ex)
        {
            Log("DOCKER", $"Error stopping containers: {ex.Message}", "WARN");
        }

        SetClusterState(ServerState.Stopped);
        _isActive = false;
        Log("SUPERVISOR", "Standalone cluster completely stopped.");
    }

    private Process StartServiceProcess(
        string serviceKey,
        string exePath,
        string workingDir,
        Dictionary<string, string> env,
        string serviceDisplayName)
    {
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Cannot start service '{serviceKey}': binary not found at '{exePath}'.", exePath);
        }

        ProcessStartInfo psi = new(exePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var serviceEnv = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
        string masterKestrelUrl = env.GetValueOrDefault("Kestrel__Endpoints__Http__Url", "http://127.0.0.1:55555");
        string dawnbringerUrls = env.GetValueOrDefault("DAWNBRINGER_URLS", "http://127.0.0.1:5266");

        serviceEnv.Remove("Kestrel__Endpoints__Http__Url");
        serviceEnv.Remove("ASPNETCORE_URLS");
        serviceEnv.Remove("APPLICATION_URL");
        serviceEnv.Remove("DAWNBRINGER_URLS");

        psi.Environment.Remove("Kestrel__Endpoints__Http__Url");
        psi.Environment.Remove("ASPNETCORE_URLS");
        psi.Environment.Remove("APPLICATION_URL");

        if (serviceKey == "master-server")
        {
            serviceEnv["Kestrel__Endpoints__Http__Url"] = masterKestrelUrl;
        }
        else if (serviceKey == "web-portal-ui")
        {
            serviceEnv["ASPNETCORE_URLS"] = dawnbringerUrls;
        }
        else if (serviceKey == "web-portal-api")
        {
            serviceEnv["ASPNETCORE_URLS"] = "http://127.0.0.1:5200";
        }
        else if (serviceKey == "chat-server")
        {
            serviceEnv["ASPNETCORE_URLS"] = "http://127.0.0.1:5554";
            serviceEnv["APPLICATION_URL"] = "http://127.0.0.1:5554";
        }

        foreach (var (k, v) in serviceEnv)
        {
            psi.Environment[k] = v;
        }

        Process proc = new() { StartInfo = psi };

        string logFilePath = Path.Combine(_layout.Logs, $"{serviceKey}.log");
        Directory.CreateDirectory(_layout.Logs);
        object fileLock = new();
        StreamWriter? logWriter = new(
            new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 16384),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        bool stdoutClosed = false;
        bool stderrClosed = false;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                lock (fileLock)
                {
                    stdoutClosed = true;
                    if (stdoutClosed && stderrClosed && logWriter is not null)
                    {
                        logWriter.Dispose();
                        logWriter = null;
                    }
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Log(serviceDisplayName, e.Data);
            try
            {
                lock (fileLock)
                {
                    logWriter?.WriteLine($"[{TimeProvider.System.GetUtcNow():O}] [INFO] {e.Data}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Error writing log for {serviceKey}: {ex.Message}");
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                lock (fileLock)
                {
                    stderrClosed = true;
                    if (stdoutClosed && stderrClosed && logWriter is not null)
                    {
                        logWriter.Dispose();
                        logWriter = null;
                    }
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Log(serviceDisplayName, e.Data, "ERROR", true);
            try
            {
                lock (fileLock)
                {
                    logWriter?.WriteLine($"[{TimeProvider.System.GetUtcNow():O}] [ERROR] {e.Data}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Error writing error log for {serviceKey}: {ex.Message}");
            }
        };

        try
        {
            proc.Start();
        }
        catch
        {
            lock (fileLock)
            {
                logWriter?.Dispose();
                logWriter = null;
            }
            throw;
        }
        _job.Attach(serviceKey, proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        Log("SUPERVISOR", $"Spawned {serviceDisplayName} (PID {proc.Id}) from '{exePath}'");
        return proc;
    }

    private async Task WaitForHttpHealthAsync(string url, int timeoutSeconds, Process proc, string serviceKey, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (!linkedCts.IsCancellationRequested)
        {
            if (proc.HasExited)
            {
                string logTail = GetRecentLogTail(serviceKey, 20);
                throw new InvalidOperationException(
                    $"Service '{serviceKey}' exited prematurely with code {proc.ExitCode}.\nRecent log output:\n{logTail}");
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _httpClient.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                if (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    string logTail = GetRecentLogTail(serviceKey, 20);
                    throw new TimeoutException(
                        $"Service '{serviceKey}' did not report healthy at '{url}' within {timeoutSeconds}s.\nRecent log output:\n{logTail}");
                }

                throw;
            }
            catch (Exception ex)
            {
                // Retry until overall timeout (includes per-request 5s HttpClient.Timeout TaskCanceledException)
                Debug.WriteLine($"[NexusStandalone] Probe waiting ({serviceKey} -> {url}): {ex.Message}");
            }

            try
            {
                await Task.Delay(1000, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                string logTail = GetRecentLogTail(serviceKey, 20);
                throw new TimeoutException(
                    $"Service '{serviceKey}' did not report healthy at '{url}' within {timeoutSeconds}s.\nRecent log output:\n{logTail}");
            }
        }

        ct.ThrowIfCancellationRequested();
        string finalLogTail = GetRecentLogTail(serviceKey, 20);
        throw new TimeoutException(
            $"Service '{serviceKey}' did not report healthy at '{url}' within {timeoutSeconds}s.\nRecent log output:\n{finalLogTail}");
    }

    private string GetRecentLogTail(string serviceKey, int maxLines)
    {
        try
        {
            string path = Path.Combine(_layout.Logs, $"{serviceKey}.log");
            if (!File.Exists(path)) return "(no log file found)";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            Queue<string> tail = new(maxLines);
            while (sr.ReadLine() is { } line)
            {
                if (tail.Count == maxLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            return $"(error reading log: {ex.Message})";
        }
    }

    private async Task<Dictionary<string, string>> BuildEnvironmentMapAsync(CancellationToken ct)
    {
        JsonObject vault = [];
        if (File.Exists(_layout.SecretsJson))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_layout.SecretsJson, ct).ConfigureAwait(false);
                vault = JsonNode.Parse(json) as JsonObject ?? [];
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] Error parsing {_layout.SecretsJson}: {ex.Message}");
            }
        }

        string hostingMode = Environment.GetEnvironmentVariable("KONGOR_STANDALONE_HOSTING_MODE") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hostingMode) &&
            SecretsManagerService.TryReadString(vault, "HostingMode", out string vMode) &&
            !string.IsNullOrWhiteSpace(vMode))
        {
            hostingMode = vMode;
        }
        if (string.IsNullOrWhiteSpace(hostingMode)) hostingMode = "Local";

        string lanIp = Environment.GetEnvironmentVariable("KONGOR_STANDALONE_LAN_IP") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(lanIp) &&
            SecretsManagerService.TryReadString(vault, "SelectedLanIp", out string vLan) &&
            !string.IsNullOrWhiteSpace(vLan))
        {
            lanIp = vLan;
        }
        if (string.IsNullOrWhiteSpace(lanIp)) lanIp = GetFirstUpNonLoopbackIPv4();

        string wanHost = Environment.GetEnvironmentVariable("KONGOR_STANDALONE_WAN_HOST") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(wanHost) &&
            SecretsManagerService.TryReadString(vault, "WanHost", out string vWan) &&
            !string.IsNullOrWhiteSpace(vWan))
        {
            wanHost = vWan;
        }

        string pgPass = string.Empty;
        if (SecretsManagerService.TryReadString(vault, "database-password", out string p1) && !string.IsNullOrWhiteSpace(p1))
            pgPass = p1;
        else if (SecretsManagerService.TryReadString(vault, "POSTGRES_PASSWORD", out string p2) && !string.IsNullOrWhiteSpace(p2))
            pgPass = p2;

        string isk = string.Empty;
        if (SecretsManagerService.TryReadString(vault, "InternalServiceKey", out string k1) && !string.IsNullOrWhiteSpace(k1))
            isk = k1;
        else if (SecretsManagerService.TryReadString(vault, "Security:InternalServiceKey", out string k2) && !string.IsNullOrWhiteSpace(k2))
            isk = k2;

        string jwt = string.Empty;
        if (SecretsManagerService.TryReadString(vault, "jwt-signing-key", out string j1) && !string.IsNullOrWhiteSpace(j1))
            jwt = j1;
        else if (SecretsManagerService.TryReadString(vault, "Jwt:SigningKey", out string j2) && !string.IsNullOrWhiteSpace(j2))
            jwt = j2;

        string compelAuthToken = string.Empty;
        if (SecretsManagerService.TryReadString(vault, "Compel:AuthenticationToken", out string c1) && !string.IsNullOrWhiteSpace(c1))
            compelAuthToken = c1;
        else if (SecretsManagerService.TryReadString(vault, "COMPEL_AUTHENTICATION_TOKEN", out string c2) && !string.IsNullOrWhiteSpace(c2))
            compelAuthToken = c2;

        string adminPass = string.Empty;
        if (SecretsManagerService.TryReadString(vault, "Standalone:AdminPassword", out string a1) && !string.IsNullOrWhiteSpace(a1))
            adminPass = a1;

        string hostPass = string.Empty;
        if (SecretsManagerService.TryReadString(vault, "Compel:HostPassword", out string h1) && !string.IsNullOrWhiteSpace(h1))
            hostPass = h1;

        Dictionary<string, string> env = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
            ["Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
            ["KONGOR_STANDALONE"] = "true",
            ["KONGOR_ALLOW_HTTP"] = "true",
            ["OTEL_SDK_DISABLED"] = "true",
            ["GrafanaOtlpEndpoint"] = string.Empty,
            ["PyroscopeServerAddress"] = string.Empty,
            ["Database__Provider"] = "PostgreSQL",
            ["ConnectionStrings__MERRICK"] = $"Host=127.0.0.1;Port=25432;Database=merrick;Username=merrick;Password={pgPass}",
            ["ConnectionStrings__DISTRIBUTED-CACHE"] = "127.0.0.1:28398,abortConnect=false,ssl=false",
            ["InternalServiceKey"] = isk,
            ["jwt-signing-key"] = jwt,
            ["WebPortalApi__BaseUrl"] = "http://127.0.0.1:5200",
            ["DATA_PROTECTION_KEYS_PATH"] = _layout.DataProtectionKeys,
            ["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"] = adminPass,
            ["KONGOR_BOOTSTRAP_HOST_PASSWORD"] = hostPass,
            ["KONGOR_BOOTSTRAP_INVITE_COUNT"] = "10",
            ["KONGOR_BOOTSTRAP_INVITES_OUTPUT_PATH"] = _layout.InviteCodes,
            ["COMPEL_AUTHENTICATION_TOKEN"] = compelAuthToken,
            ["COMPEL_LOCATION"] = SecretsManagerService.TryReadString(vault, "Compel:Location", out string loc1) && !string.IsNullOrWhiteSpace(loc1)
                ? loc1.Trim()
                : (SecretsManagerService.TryReadString(vault, "COMPEL_LOCATION", out string loc2) && !string.IsNullOrWhiteSpace(loc2)
                    ? loc2.Trim()
                    : "USE"),
            ["APPLICATION_URL"] = "http://127.0.0.1:5554",
            ["CHAT_SERVER_INTERNAL_BIND_ADDRESS"] = "127.0.0.1"
        };

        bool isLan = hostingMode.Contains("Lan", StringComparison.OrdinalIgnoreCase) ||
                     hostingMode.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
        bool isWan = hostingMode.Contains("Public", StringComparison.OrdinalIgnoreCase) ||
                     hostingMode.Contains("Internet", StringComparison.OrdinalIgnoreCase);

        if (isWan)
        {
            env["Kestrel__Endpoints__Http__Url"] = "http://0.0.0.0:55555";
            env["CHAT_SERVER_CLIENT_BIND_ADDRESS"] = "0.0.0.0";
            env["CHAT_SERVER_LAN_HOST"] = lanIp;
            env["CHAT_SERVER_PUBLIC_HOST"] = wanHost;
            env["COMPEL_GATEWAY"] = !string.IsNullOrWhiteSpace(wanHost) ? wanHost : lanIp;
            env["CORS_ALLOWED_ORIGINS"] = $"http://127.0.0.1:5266,http://localhost:5266,http://{lanIp}:5266" +
                                          (!string.IsNullOrWhiteSpace(wanHost) ? $",http://{wanHost}:5266" : string.Empty);
            // DAWNBRINGER binds 0.0.0.0:5266
            env["DAWNBRINGER_URLS"] = "http://0.0.0.0:5266";

            if (string.IsNullOrWhiteSpace(wanHost))
            {
                env["KONGOR_DISCOVER_PUBLIC_IP"] = "true";
            }
        }
        else if (isLan)
        {
            env["Kestrel__Endpoints__Http__Url"] = "http://0.0.0.0:55555";
            env["CHAT_SERVER_CLIENT_BIND_ADDRESS"] = "0.0.0.0";
            env["CHAT_SERVER_LAN_HOST"] = lanIp;
            env["COMPEL_GATEWAY"] = lanIp;
            env["CORS_ALLOWED_ORIGINS"] = $"http://127.0.0.1:5266,http://localhost:5266,http://{lanIp}:5266";
            env["DAWNBRINGER_URLS"] = "http://0.0.0.0:5266";
        }
        else
        {
            // Local
            env["Kestrel__Endpoints__Http__Url"] = "http://127.0.0.1:55555";
            env["CHAT_SERVER_CLIENT_BIND_ADDRESS"] = "127.0.0.1";
            env["COMPEL_GATEWAY"] = "127.0.0.1";
            env["CORS_ALLOWED_ORIGINS"] = "http://127.0.0.1:5266,http://localhost:5266";
            env["DAWNBRINGER_URLS"] = "http://127.0.0.1:5266";
        }

        return env;
    }

    private static void EnsureLinuxConfigWritable()
    {
        string configDir = "/opt/hon/config";
        bool writable = false;
        if (Directory.Exists(configDir))
        {
            try
            {
                string probeFile = Path.Combine(configDir, $".probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probeFile, "probe");
                File.Delete(probeFile);
                writable = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] /opt/hon/config probe failed: {ex.Message}");
            }
        }

        if (!writable)
        {
            throw new InvalidOperationException("Linux dedicated server requires writable /opt/hon/config. Run: sudo mkdir -p /opt/hon/config && sudo chown \"$USER:$USER\" /opt/hon/config");
        }
    }

    private static bool IsPortOwnedByProjectContainers(string projectName, int port)
    {
        try
        {
            string dockerCli = DockerHealthService.ResolveDockerCliPath();
            using Process proc = new();
            proc.StartInfo = new ProcessStartInfo(dockerCli, $"ps --filter \"name={projectName}\" --format \"{{{{.Ports}}}}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Contains($":{port}->");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Failed to check container port ownership: {ex.Message}");
            return false;
        }
    }

    private static (int Pid, string ProcessName) ResolvePortOwner(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using Process netstat = new();
                netstat.StartInfo = new ProcessStartInfo("netstat", "-ano -p tcp")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                netstat.Start();
                string output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit();

                foreach (string rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Contains($":{port} ") && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5 && int.TryParse(parts[^1], out int pid))
                        {
                            string name = "unknown";
                            try
                            {
                                using Process p = Process.GetProcessById(pid);
                                name = p.ProcessName;
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[NexusStandalone] Unable to get process name for PID {pid}: {ex.Message}");
                            }
                            return (pid, name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] netstat failed: {ex.Message}");
            }
        }
        else
        {
            try
            {
                using Process ss = new();
                ss.StartInfo = new ProcessStartInfo("ss", "-tlpn")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ss.Start();
                string output = ss.StandardOutput.ReadToEnd();
                ss.WaitForExit();

                foreach (string rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Contains($":{port} "))
                    {
                        var match = Regex.Match(line, @"pid=(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int pid))
                        {
                            string name = "unknown";
                            try
                            {
                                using Process p = Process.GetProcessById(pid);
                                name = p.ProcessName;
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[NexusStandalone] Unable to get process name for PID {pid}: {ex.Message}");
                            }
                            return (pid, name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NexusStandalone] ss failed: {ex.Message}");
            }
        }

        return (0, "unknown");
    }

    private async Task WaitForPostgresReadyAsync(
        string dockerCli,
        string projectName,
        string composeFile,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using Process proc = new();
                proc.StartInfo = new ProcessStartInfo(
                    dockerCli,
                    $"compose -p {projectName} --env-file \"{_layout.DockerEnv}\" -f \"{composeFile}\" exec -T postgres pg_isready -U merrick -d merrick")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _layout.Root
                };
                proc.Start();
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                if (proc.ExitCode == 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NexusStandalone] pg_isready probe waiting: {ex.Message}");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private static async Task WaitForTcpPortAsync(string host, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
                if (tcp.Connected) return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Retry until timeout
                Debug.WriteLine($"[NexusStandalone] Probe waiting: {ex.Message}");
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    public static string GetFirstUpNonLoopbackIPv4()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties props = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Error finding LAN IP: {ex.Message}");
        }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _job.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NexusStandalone] Error disposing ChildProcessJob: {ex.Message}");
        }

        _httpClient.Dispose();
    }
}
