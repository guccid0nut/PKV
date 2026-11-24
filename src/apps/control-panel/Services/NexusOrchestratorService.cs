// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using ASPIRE.Common.Utilities;
using Docker.DotNet;
using Docker.DotNet.Models;
using NEXUS.ControlPanel.GUI.Controls;
using NEXUS.ControlPanel.GUI.Services.Models;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Configuration;
using NEXUS.ControlPanel.GUI.Services.Standalone;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed partial class NexusOrchestratorService : INexusOrchestratorService
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
        "hon",
        "WILLOWMAKER",
        "ReplayRenderer.Service"
    ];

    private readonly ConcurrentDictionary<int, string> _sessionPids = new();
    private static readonly Lock _sessionRegistryLock = new();
    private readonly CancellationTokenSource _healthMonitorCts = new();
    private readonly Lock _logLock = new();
    private readonly List<LogMessage> _logs = [];
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, ServerState> _states = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentLogHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _fileTailerPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _streamLastStructuredLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISecretsManagerService? secretsManager;
    private readonly IDockerHealthService? _dockerHealth;
    private readonly DockerStackScriptRunner _stackRunner;
    private readonly Func<string, CancellationToken, Task<DockerComposeStackSnapshot>> _stackProbe;
    private readonly Lock _stackScriptCtsLock = new();
    private CancellationTokenSource? _activeStackScriptCts;
    private int _disposedOrDetached;

    private static readonly string[] NativeLocalHostProcessNames =
    [
        "ASPIRE.ApplicationHost",
        "KONGOR.MasterServer",
        "TRANSMUTANSTEIN.ChatServer",
        "ZORGATH.WebPortal.API",
        "DAWNBRINGER.WebPortal.UI",
        "COMPEL"
    ];
    private readonly StandaloneClusterSupervisor? _standalone;
    private ServerState _clusterState = ServerState.Stopped;
    private bool _isTailingLogs;
    private bool _lastWithGameServers;
    private volatile bool _isDockerStackOperationActive;
    private CancellationTokenSource? _logTailerCts;

    private void RegisterSessionPid(int pid, string serviceName)
    {
        _sessionPids[pid] = serviceName;
        PersistSessionPids();
    }

    private void UnregisterSessionPid(int pid)
    {
        _sessionPids.TryRemove(pid, out _);
        PersistSessionPids();
    }

    private void PersistSessionPids()
    {
        try
        {
            string root = FindSolutionRoot();
            string userdataDir = Path.Combine(root, "userdata");
            if (!Directory.Exists(userdataDir)) Directory.CreateDirectory(userdataDir);
            string registryPath = Path.Combine(userdataDir, "session_pids.json");

            lock (_sessionRegistryLock)
            {
                if (_sessionPids.IsEmpty)
                {
                    if (File.Exists(registryPath)) File.Delete(registryPath);
                    return;
                }

                var pidsList = _sessionPids.Select(kvp => new { pid = kvp.Key, name = kvp.Value }).ToList();
                string json = System.Text.Json.JsonSerializer.Serialize(new { pids = pidsList });
                File.WriteAllText(registryPath, json);
            }
        }
        catch
        {
            // Non-fatal if session file cannot be written
        }
    }

    private void CleanSessionPids()
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            Console.WriteLine("[NexusStandalone] skipped name-based process sweep in bundled mode");
            return;
        }
        CleanSessionPidsCore();
    }

    private void CleanSessionPidsCore()
    {
        if (IsTestEnvironment()) return;

        try
        {
            string root = FindSolutionRoot();
            string registryPath = Path.Combine(root, "userdata", "session_pids.json");
            lock (_sessionRegistryLock)
            {
                if (File.Exists(registryPath))
                {
                    try
                    {
                        string json = File.ReadAllText(registryPath);
                        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("pids", out System.Text.Json.JsonElement pidsArray) &&
                            pidsArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            int currentPid = Environment.ProcessId;
                            foreach (System.Text.Json.JsonElement item in pidsArray.EnumerateArray())
                            {
                                if (item.TryGetProperty("pid", out System.Text.Json.JsonElement pidProp) &&
                                    pidProp.TryGetInt32(out int pid) &&
                                    pid != currentPid)
                                {
                                    try
                                    {
                                        using Process p = Process.GetProcessById(pid);
                                        p.Kill(true);
                                    }
                                    catch
                                    {
                                        // Already exited or invalid PID
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore parse failures
                    }

                    File.Delete(registryPath);
                }
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    public NexusOrchestratorService(
        ISecretsManagerService? secretsManager = null,
        IDockerHealthService? dockerHealth = null,
        DockerStackScriptRunner? stackRunner = null,
        Func<string, CancellationToken, Task<DockerComposeStackSnapshot>>? stackProbe = null)
    {
        this.secretsManager = secretsManager;
        _dockerHealth = dockerHealth;
        _stackRunner = stackRunner ?? ((action, extraArgs, root, onLine, ct) =>
            DockerStackProbe.RunStackScriptAsync(action, extraArgs, root, onLine, ct));
        _stackProbe = stackProbe ?? ((root, ct) =>
            DockerStackProbe.ProbeAsync(root, includeFleetHttpStatus: false, ct));
        if (BundledLayoutResolver.TryResolve(out BundledLayout? bundledLayout))
        {
            _standalone = new StandaloneClusterSupervisor(
                bundledLayout,
                this.secretsManager,
                appendLog: AppendLog,
                setClusterState: state => CurrentClusterState = state,
                setServiceState: (svc, state) => SetServiceState(svc, state));
        }
        RunStartupLogHygiene();
        StartClusterHealthMonitor(_healthMonitorCts.Token);
        StartContinuousLogTailers(_healthMonitorCts.Token);
    }

    public string AspireDashboardUrl { get; private set; } = "https://localhost:5550";

    public ServerState CurrentClusterState
    {
        get => _clusterState;
        private set
        {
            if (_clusterState != value)
            {
                _clusterState = value;
                OnClusterStateChanged?.Invoke(_clusterState);

                if (_clusterState == ServerState.Running)
                    SetAllMicroservicesState(ServerState.Running);
                else if (_clusterState == ServerState.Stopped) SetAllMicroservicesState(ServerState.Stopped);
            }
        }
    }

    public IReadOnlyList<LogMessage> LogHistory
    {
        get
        {
            lock (_logLock)
            {
                return [.. _logs];
            }
        }
    }

    public event Action<ServerState>? OnClusterStateChanged;
    public event Action<string, ServerState>? OnServiceStateChanged;
    public event Action<LogMessage>? OnLogReceived;

    public ServerState GetServiceState(string serviceKey)
    {
        return _states.GetValueOrDefault(serviceKey, _clusterState);
    }


    public Task StartClusterAsync(bool withGameServers = false, string? customWorkingDir = null,
        CancellationToken cancellationToken = default)
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            if (_standalone != null)
            {
                return _standalone.StartClusterAsync(withGameServers, cancellationToken);
            }
            return Task.CompletedTask;
        }

        if (CurrentClusterState is ServerState.Running or ServerState.Starting) return Task.CompletedTask;

        _logTailerCts?.Cancel();
        _logTailerCts?.Dispose();
        _logTailerCts = new CancellationTokenSource();
        _isTailingLogs = false;

        _lastWithGameServers = withGameServers;
        CurrentClusterState = ServerState.Starting;

        Stopwatch cleanupStopwatch = Stopwatch.StartNew();
        // Ensure all lingering cluster processes are terminated in a fast single pass
        TerminateClusterProcessesFast();
        cleanupStopwatch.Stop();

        string workingDir = customWorkingDir ?? FindSolutionRoot();
        List<string> extraArgsList = [];
        if (withGameServers)
        {
            extraArgsList.Add("--launch-game-servers");
        }

        bool isDockerHealthy = IsDockerAvailableFast();
        bool isCacheActive = IsPortListeningFast(28298);
        bool isPyroscopeActive = IsPortListeningFast(4040);

        // Auto-ensure distributed cache and Pyroscope containers if Docker is healthy
        if (isDockerHealthy && !isCacheActive)
        {
            _ = EnsureGarnetContainerAsync(workingDir, cancellationToken);
            isCacheActive = IsPortListeningFast(28298);
        }

        // Auto-ensure Pyroscope profiler container if Docker is healthy but port 4040 is not listening
        if (isDockerHealthy && !isPyroscopeActive)
        {
            _ = EnsurePyroscopeContainerAsync(workingDir, cancellationToken);
            isPyroscopeActive = IsPortListeningFast(4040);
        }

        // If Docker is unavailable, OR if all container infrastructure (cache & profiler) is already active,
        // bypass Aspire container runtime so microservices launch in ~2-3s without DCP 40s container probing delays.
        bool allContainersActive = isCacheActive && isPyroscopeActive;
        bool useContainerlessMode = !isDockerHealthy || allContainersActive;
        if (useContainerlessMode)
        {
            extraArgsList.Add("--no-containers");
        }

        string extraArgs = string.Join(" ", extraArgsList);
        ProcessStartInfo startInfo = CreateFastProcessStartInfo(workingDir, "ASPIRE.ApplicationHost",
            "ASPIRE.ApplicationHost Development", extraArgs);

        if (!useContainerlessMode)
        {
            string dockerDesktopBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker", "Docker", "resources", "bin");
            if (Directory.Exists(dockerDesktopBin))
            {
                string existingPath = startInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!existingPath.StartsWith(dockerDesktopBin, StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.EnvironmentVariables["PATH"] = $"{dockerDesktopBin};{existingPath}";
                }
            }
        }
        else
        {
            // In containerless mode, sanitize PATH so DCP and Aspire do not hang for 30s probing unresponsive Docker CLI shims
            string existingPath = startInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!string.IsNullOrEmpty(existingPath))
            {
                string cleanedPath = string.Join(";", existingPath.Split(';')
                    .Where(p => !p.Contains("Docker", StringComparison.OrdinalIgnoreCase) &&
                                !p.Contains("chocolatey", StringComparison.OrdinalIgnoreCase)));
                startInfo.EnvironmentVariables["PATH"] = cleanedPath;
                EnsureDotNetSdkEnvironment(startInfo);
            }
        }

        startInfo.Environment["DOTNET_ASPIRE_SHOW_CHILD_CONSOLE_OUTPUT"] = "true";
        startInfo.Environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        startInfo.Environment["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";

        if (useContainerlessMode)
        {
            startInfo.Environment["SKIP_CONTAINERS"] = "true";
            startInfo.Environment["NO_CONTAINERS"] = "true";
        }

        try
        {
            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            _processes["cluster"] = process;

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) HandleOutputLine(args.Data, false);
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) HandleOutputLine(args.Data, true);
            };

            process.Exited += (_, _) =>
            {
                UnregisterSessionPid(process.Id);
                CurrentClusterState = ServerState.Stopped;
                SetAllMicroservicesState(ServerState.Stopped);
                AppendLog(new LogMessage
                {
                    ServiceName = "ASPIRE HOST",
                    Message = "ASPIRE Orchestrator cluster terminated.",
                    Level = "INFO",
                    IsError = false
                });
            };

            string containerModeText = isDockerHealthy ? " [Containers: Active]" : " [Containers: Bypassed (Containerless Mode)]";
            string modeText = (withGameServers ? " (WITH GAME SERVERS)" : " (CLUSTER ONLY)") + containerModeText;
            AppendLog(new LogMessage
            {
                ServiceName = "ASPIRE HOST",
                Message =
                    $"Launching Full ASPIRE Cluster{modeText} [Cleanup: {cleanupStopwatch.ElapsedMilliseconds}ms, Binary: {Path.GetFileName(startInfo.FileName)}]...",
                Level = "INFO",
                IsError = false
            });

            process.Start();
            RegisterSessionPid(process.Id, "ASPIRE.ApplicationHost");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            CurrentClusterState = ServerState.Starting;
            SetAllMicroservicesState(ServerState.Starting);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            CurrentClusterState = ServerState.Faulted;
            AppendLog(new LogMessage
            {
                ServiceName = "ASPIRE HOST",
                Message = $"Failed to start cluster: {ex.Message}",
                Level = "ERROR",
                IsError = true
            });
            return Task.FromException(ex);
        }
    }

    public Task StopClusterAsync(CancellationToken cancellationToken = default)
    {
        return StopClusterAsync(forceAll: false, cancellationToken);
    }

    public async Task StopClusterAsync(bool forceAll, CancellationToken cancellationToken = default)
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            if (_standalone != null)
            {
                await _standalone.StopClusterAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (_logTailerCts != null)
        {
            try
            {
                await _logTailerCts.CancelAsync();
            }
            catch
            {
                // Ignore cancellation errors
            }

            _logTailerCts.Dispose();
            _logTailerCts = null;
        }

        _isTailingLogs = false;

        if (_processes.TryRemove("cluster", out Process? process) && !process.HasExited)
        {
            CurrentClusterState = ServerState.Stopping;
            AppendLog(new LogMessage
            {
                ServiceName = "ASPIRE HOST",
                Message = "Stopping ASPIRE Orchestrator cluster...",
                Level = "INFO",
                IsError = false
            });

            try
            {
                UnregisterSessionPid(process.Id);
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "ASPIRE HOST",
                    Message = $"Shutdown error: {ex.Message}",
                    Level = "WARN",
                    IsError = false
                });
            }
            finally
            {
                process.Dispose();
                CurrentClusterState = ServerState.Stopped;
                SetAllMicroservicesState(ServerState.Stopped);
            }
        }
        else
        {
            string root = FindSolutionRoot();
            DockerComposeStackSnapshot dockerSnap = await DockerStackProbe.ProbeAsync(
                root,
                includeFleetHttpStatus: false,
                cancellationToken);

            if (dockerSnap.IsStackActive)
            {
                CurrentClusterState = ServerState.Stopping;
                AppendLog(new LogMessage
                {
                    ServiceName = "DOCKER STACK",
                    Message = $"Stopping kongor-stack Docker Compose cluster ({dockerSnap.RunningContainerCount} running container(s))...",
                    Level = "INFO",
                    IsError = false
                });

                (int exitCode, _) = await DockerStackProbe.RunStackScriptAsync(
                    "down",
                    repoRoot: root,
                    onLine: (line, isErr) => AppendLog(new LogMessage
                    {
                        ServiceName = "DOCKER STACK",
                        Message = line,
                        Level = isErr ? "WARN" : "INFO",
                        IsError = isErr
                    }),
                    cancellationToken: cancellationToken);

                AppendLog(new LogMessage
                {
                    ServiceName = "DOCKER STACK",
                    Message = exitCode == 0
                        ? "kongor-stack Docker Compose cluster stopped cleanly."
                        : $"kongor-stack teardown exited with code {exitCode}.",
                    Level = exitCode == 0 ? "INFO" : "ERROR",
                    IsError = exitCode != 0
                });
            }

            CurrentClusterState = ServerState.Stopped;
            SetAllMicroservicesState(ServerState.Stopped);
        }

        // Only invoke global cluster process kill if explicitly requested (e.g. StopAllAsync / Reset Environment)
        if (forceAll)
        {
            TerminateClusterProcessesFast();
            CleanSessionPids();
        }
        else
        {
            // Terminate only session-tracked processes
            foreach (KeyValuePair<string, Process> kvp in _processes)
            {
                if (!kvp.Value.HasExited)
                {
                    try { kvp.Value.Kill(true); } catch { }
                    UnregisterSessionPid(kvp.Value.Id);
                }
            }
        }
    }

    public async Task RestartClusterAsync(bool withGameServers = false, string? customWorkingDir = null,
        CancellationToken cancellationToken = default)
    {
        string workingDir = customWorkingDir ?? FindSolutionRoot();
        if (!_processes.ContainsKey("cluster"))
        {
            DockerComposeStackSnapshot dockerSnap = await _stackProbe(workingDir, cancellationToken).ConfigureAwait(false);

            if (dockerSnap.IsStackActive || dockerSnap.Services.Count > 0)
            {
                string preset = string.IsNullOrWhiteSpace(dockerSnap.PerformancePreset)
                    ? "Balanced"
                    : dockerSnap.PerformancePreset;

                AppendLog(new LogMessage
                {
                    ServiceName = "DOCKER STACK",
                    Message = $"Fast-restarting kongor-stack Docker Compose cluster (Performance={preset})...",
                    Level = "INFO",
                    IsError = false
                });

                using CancellationTokenSource scriptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_stackScriptCtsLock)
                {
                    _activeStackScriptCts = scriptCts;
                }

                _isDockerStackOperationActive = true;
                ServerState previousState = CurrentClusterState;
                CurrentClusterState = ServerState.Starting;
                try
                {
                    (int exitCode, _) = await _stackRunner(
                        "restart",
                        ["-Performance", preset],
                        workingDir,
                        (line, isErr) => AppendLog(new LogMessage
                        {
                            ServiceName = "DOCKER STACK",
                            Message = line,
                            Level = isErr ? "WARN" : "INFO",
                            IsError = isErr || line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)
                        }),
                        scriptCts.Token).ConfigureAwait(false);

                    if (exitCode == 75)
                    {
                        CurrentClusterState = previousState == ServerState.Stopped ? ServerState.Stopped : ServerState.Running;
                        AppendLog(new LogMessage
                        {
                            ServiceName = "DOCKER STACK",
                            Message = "Restart skipped: another Docker stack operation currently holds the stack lock.",
                            Level = "WARN",
                            IsError = false
                        });
                    }
                    else
                    {
                        CurrentClusterState = exitCode == 0 ? ServerState.Running : ServerState.Faulted;
                    }
                }
                catch (OperationCanceledException)
                {
                    CurrentClusterState = ServerState.Stopped;
                    AppendLog(new LogMessage
                    {
                        ServiceName = "DOCKER STACK",
                        Message = "Docker stack restart cancelled.",
                        Level = "WARN",
                        IsError = false
                    });
                }
                finally
                {
                    lock (_stackScriptCtsLock)
                    {
                        if (ReferenceEquals(_activeStackScriptCts, scriptCts))
                        {
                            _activeStackScriptCts = null;
                        }
                    }

                    _isDockerStackOperationActive = false;
                }

                return;
            }
        }

        bool useGameServers = withGameServers || _lastWithGameServers;
        await StopClusterAsync(cancellationToken);
        await Task.Delay(500, cancellationToken);
        await StartClusterAsync(useGameServers, customWorkingDir, cancellationToken);
    }

    public Task StartServiceAsync(string serviceKey, string? customWorkingDir = null, CancellationToken cancellationToken = default) { if (BundledLayoutResolver.TryResolve(out _)) { if (_standalone != null) return _standalone.StartServiceAsync(serviceKey, cancellationToken); return Task.CompletedTask; } return StartServiceAsyncCore(serviceKey, customWorkingDir, cancellationToken); }

    public static string? TryResolveDockerStackContainerName(string serviceKey) =>
        serviceKey.Trim().ToLowerInvariant() switch
        {
            "master-server" => "kongor-stack-master-server",
            "chat-server" => "kongor-stack-chat-server",
            "web-portal-api" or "web-api" => "kongor-stack-web-api",
            "web-portal-ui" or "web-ui" => "kongor-stack-web-ui",
            "game-servers" or "compel" or "compel-dedicated" => "kongor-stack-compel-dedicated",
            "queue-arcade" or "compel-queue-arcade" => "kongor-stack-compel-arcade",
            _ => null
        };

    private async Task<bool> TryExecuteDockerServiceActionAsync(
        string serviceKey,
        string action,
        CancellationToken cancellationToken)
    {
        if (_processes.ContainsKey(serviceKey) || _processes.ContainsKey("cluster"))
        {
            return false;
        }

        string? containerName = TryResolveDockerStackContainerName(serviceKey);
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return false;
        }

        bool canQueryDocker = (_dockerHealth != null && (IsTestEnvironment() || DockerHealthService.IsDockerDaemonRunningFast())) ||
                              (!IsTestEnvironment() && DockerHealthService.IsDockerDaemonRunningFast());
        if (!canQueryDocker)
        {
            return false;
        }

        DockerClient? ownedClient = null;
        try
        {
            IDockerClient dockerClient;
            if (_dockerHealth != null)
            {
                dockerClient = _dockerHealth.Client;
            }
            else
            {
                ownedClient = new DockerClientConfiguration().CreateClient();
                dockerClient = ownedClient;
            }

            IList<ContainerListResponse> containers = await dockerClient.Containers
                .ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken)
                .ConfigureAwait(false);

            bool containerExists = containers.Any(c =>
                c.Names != null &&
                c.Names.Any(n => string.Equals(n?.Trim().TrimStart('/'), containerName, StringComparison.OrdinalIgnoreCase)));

            if (!containerExists)
            {
                return false;
            }

            string displayName = GetDisplayServiceName(serviceKey);
            switch (action)
            {
                case "start":
                {
                    SetServiceState(serviceKey, ServerState.Starting);
                    AppendLog(new LogMessage
                    {
                        ServiceName = displayName,
                        Message = $"Starting Docker container '{containerName}'...",
                        Level = "INFO"
                    });
                    await dockerClient.Containers
                        .StartContainerAsync(containerName, new ContainerStartParameters(), cancellationToken)
                        .ConfigureAwait(false);
                    SetServiceState(serviceKey, ServerState.Running);
                    AppendLog(new LogMessage
                    {
                        ServiceName = displayName,
                        Message = $"Docker container '{containerName}' started.",
                        Level = "INFO"
                    });
                    return true;
                }
                case "stop":
                {
                    SetServiceState(serviceKey, ServerState.Stopping);
                    AppendLog(new LogMessage
                    {
                        ServiceName = displayName,
                        Message = $"Stopping Docker container '{containerName}'...",
                        Level = "INFO"
                    });
                    await dockerClient.Containers
                        .StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, cancellationToken)
                        .ConfigureAwait(false);
                    SetServiceState(serviceKey, ServerState.Stopped);
                    AppendLog(new LogMessage
                    {
                        ServiceName = displayName,
                        Message = $"Docker container '{containerName}' stopped.",
                        Level = "INFO"
                    });
                    return true;
                }
                case "restart":
                {
                    SetServiceState(serviceKey, ServerState.Starting);
                    AppendLog(new LogMessage
                    {
                        ServiceName = displayName,
                        Message = $"Restarting Docker container '{containerName}'...",
                        Level = "INFO"
                    });
                    await dockerClient.Containers
                        .RestartContainerAsync(containerName, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, cancellationToken)
                        .ConfigureAwait(false);
                    SetServiceState(serviceKey, ServerState.Running);
                    AppendLog(new LogMessage
                    {
                        ServiceName = displayName,
                        Message = $"Docker container '{containerName}' restarted.",
                        Level = "INFO"
                    });
                    return true;
                }
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetServiceState(serviceKey, ServerState.Faulted);
            AppendLog(new LogMessage
            {
                ServiceName = GetDisplayServiceName(serviceKey),
                Message = $"Docker container '{containerName}' {action} failed: {ex.Message}",
                Level = "ERROR",
                IsError = true
            });
            return true;
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    private async Task StartServiceAsyncCore(string serviceKey, string? customWorkingDir = null,
        CancellationToken cancellationToken = default)
    {
        if (serviceKey == "cluster")
        {
            await StartClusterAsync(false, customWorkingDir, cancellationToken);
            return;
        }

        if (serviceKey == "pyroscope")
        {
            SetServiceState(serviceKey, ServerState.Starting);
            string rootDir = customWorkingDir ?? FindSolutionRoot();
            bool started = await EnsurePyroscopeContainerAsync(rootDir, cancellationToken);
            SetServiceState(serviceKey, started ? ServerState.Running : ServerState.Faulted);
            AppendLog(new LogMessage
            {
                ServiceName = "PYROSCOPE",
                Message = started ? "Pyroscope continuous profiler container started on port 4040." : "Failed to start Pyroscope container via Docker.",
                Level = started ? "INFO" : "ERROR",
                IsError = !started
            });
            return;
        }

        // Terminate any previous standalone instance of this specific service unless managed in kongor-stack Docker
        if (await TryExecuteDockerServiceActionAsync(serviceKey, "start", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        TerminateProcessByServiceName(serviceKey);

        if (_processes.TryGetValue(serviceKey, out Process? existing) && !existing.HasExited) return;

        SetServiceState(serviceKey, ServerState.Starting);
        string root = customWorkingDir ?? FindSolutionRoot();

        ProcessStartInfo? psi = serviceKey switch
        {
            "master-server" => CreateFastProcessStartInfo(root, "KONGOR.MasterServer",
                "KONGOR.MasterServer Development"),
            "chat-server" => CreateFastProcessStartInfo(root, "TRANSMUTANSTEIN.ChatServer",
                "TRANSMUTANSTEIN.ChatServer Development"),
            "web-portal-api" => CreateFastProcessStartInfo(root, "ZORGATH.WebPortal.API",
                "ZORGATH.WebPortal.API Development"),
            "web-portal-ui" => CreateFastProcessStartInfo(root, "DAWNBRINGER.WebPortal.UI",
                "DAWNBRINGER.WebPortal.UI Development"),
            "game-servers" => await FindGameServerStartInfoAsync(root, cancellationToken),
            _ => null
        };

        if (psi == null)
        {
            SetServiceState(serviceKey, ServerState.Stopped);
            return;
        }

        try
        {
            Process process = new() { StartInfo = psi, EnableRaisingEvents = true };
            _processes[serviceKey] = process;

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) HandleOutputLine(args.Data, false);
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data)) HandleOutputLine(args.Data, true);
            };

            process.Exited += (_, _) =>
            {
                UnregisterSessionPid(process.Id);
                SetServiceState(serviceKey, ServerState.Stopped);
                AppendLog(new LogMessage
                {
                    ServiceName = GetDisplayServiceName(serviceKey),
                    Message = $"{GetDisplayServiceName(serviceKey)} process stopped.",
                    Level = "INFO",
                    IsError = false
                });
            };

            AppendLog(new LogMessage
            {
                ServiceName = GetDisplayServiceName(serviceKey),
                Message =
                    $"Starting independent service: {GetDisplayServiceName(serviceKey)} [Binary: {Path.GetFileName(psi.FileName)}]...",
                Level = "INFO",
                IsError = false
            });

            process.Start();
            RegisterSessionPid(process.Id, serviceKey);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            SetServiceState(serviceKey, ServerState.Running);
        }
        catch (Exception ex)
        {
            SetServiceState(serviceKey, ServerState.Faulted);
            AppendLog(new LogMessage
            {
                ServiceName = GetDisplayServiceName(serviceKey),
                Message = $"Failed to launch {serviceKey}: {ex.Message}",
                Level = "ERROR",
                IsError = true
            });
            throw;
        }
    }

    public Task StopServiceAsync(string serviceKey, CancellationToken cancellationToken = default) { if (BundledLayoutResolver.TryResolve(out _)) { if (_standalone != null) return _standalone.StopServiceAsync(serviceKey, cancellationToken); return Task.CompletedTask; } return StopServiceAsyncCore(serviceKey, cancellationToken); } private async Task StopServiceAsyncCore(string serviceKey, CancellationToken cancellationToken = default)
    {
        if (serviceKey == "cluster")
        {
            await StopClusterAsync(cancellationToken);
            return;
        }

        if (serviceKey == "pyroscope")
        {
        SetServiceState(serviceKey, ServerState.Stopping);
            string rootDir = FindSolutionRoot();
            string composeFile = Path.Combine(rootDir, "infra", "docker", "docker-compose.pyroscope.yml");
            try
            {
                if (File.Exists(composeFile))
                {
                    string dockerCli = DockerHealthService.ResolveDockerCliPath();
                    using Process proc = new();
                    proc.StartInfo = new ProcessStartInfo
                    {
                        FileName = dockerCli,
                        Arguments = $"compose -f \"{composeFile}\" down",
                        WorkingDirectory = rootDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    proc.Start();
                    await proc.WaitForExitAsync(cancellationToken);
                }
                else
                {
                    using var client = new DockerClientConfiguration().CreateClient();
                    await client.Containers.StopContainerAsync("kongor-pyroscope",
                        new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NexusOrchestratorService] Note: Pyroscope stop exception: {ex.Message}");
            }
            SetServiceState(serviceKey, ServerState.Stopped);
            AppendLog(new LogMessage
            {
                ServiceName = "PYROSCOPE",
                Message = "Pyroscope container stopped.",
                Level = "INFO",
                IsError = false
            });
            return;
        }

        if (await TryExecuteDockerServiceActionAsync(serviceKey, "stop", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SetServiceState(serviceKey, ServerState.Stopping);

        if (_processes.TryRemove(serviceKey, out Process? process) && !process.HasExited)
            try
            {
                UnregisterSessionPid(process.Id);
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                // Cleanup
            }
            finally
            {
                process.Dispose();
            }

        // Terminate only this microservice's exact process without killing parent Aspire or DCP

        TerminateProcessByServiceName(serviceKey);

        SetServiceState(serviceKey, ServerState.Stopped);
        AppendLog(new LogMessage
        {
            ServiceName = GetDisplayServiceName(serviceKey),
            Message = $"{GetDisplayServiceName(serviceKey)} stopped.",
            Level = "INFO",
            IsError = false
        });
    }

    public async Task RestartServiceAsync(string serviceKey, string? customWorkingDir = null,
        CancellationToken cancellationToken = default)
    {
        if (!BundledLayoutResolver.TryResolve(out _) &&
            await TryExecuteDockerServiceActionAsync(serviceKey, "restart", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await StopServiceAsync(serviceKey, cancellationToken);
        await Task.Delay(500, cancellationToken);
        await StartServiceAsync(serviceKey, customWorkingDir, cancellationToken);
    }

    public async Task StartAllAsync(bool includeGameServers = false, CancellationToken cancellationToken = default)
    {
        await StartClusterAsync(includeGameServers, null, cancellationToken);
    }

    private void CancelActiveStackScript()
    {
        lock (_stackScriptCtsLock)
        {
            try
            {
                _activeStackScriptCts?.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                Debug.WriteLine($"[ControlPanel] [ORCHESTRATOR] CancelActiveStackScript notice: {ex.Message}");
            }
        }
    }

    private bool ShouldMarkMonitoredServiceStopped(string serviceKey)
    {
        ServerState current = GetServiceState(serviceKey);
        if (current == ServerState.Stopped)
        {
            return false;
        }

        if (CurrentClusterState is ServerState.Stopped or ServerState.Stopping)
        {
            return true;
        }

        return CurrentClusterState != ServerState.Starting && current is ServerState.Running or ServerState.Stopping;
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        CancelActiveStackScript();
        await StopQueueArcadeServersAsync(cancellationToken);

        // Pass 1: Graceful stop of cluster and all microservices
        await StopClusterAsync(forceAll: true, cancellationToken);

        foreach (string key in _processes.Keys) await StopServiceAsync(key, cancellationToken);

        // Brief delay before pass 2
        await Task.Delay(100, cancellationToken);

        // Pass 2: Fast cleanup of all remaining child processes (explicit nuclear stop)
        TerminateClusterProcessesFast();
        CleanSessionPids();
    }

    public void ClearLogs()
    {
        lock (_logLock)
        {
            _logs.Clear();
        }
    }

    public async Task LaunchGameClientAsync(string? customClientPath = null,
        CancellationToken cancellationToken = default)
    {
        if (BundledLayoutResolver.TryResolve(out BundledLayout? layout))
        {
            string launcherExe = layout.LauncherExe;
            string launcherDir = Path.GetDirectoryName(launcherExe) ?? layout.Root;

            if (IsTestEnvironment())
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "GAME CLIENT",
                    Message = $"[TestEnvironment] Suppressed desktop external process spawn for '{launcherExe}' during automated test run.",
                    Level = "INFO"
                });
                return;
            }

            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = launcherExe,
                    WorkingDirectory = launcherDir,
                    UseShellExecute = true
                };
                Process? clientProc = Process.Start(psi);
                AppendLog(new LogMessage
                {
                    ServiceName = "GAME CLIENT",
                    Message = $"Launched Game Client: {launcherExe}",
                    Level = "INFO",
                    IsError = false
                });
            }
            catch (Exception ex)
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "GAME CLIENT",
                    Message = $"Failed to launch Game Client: {ex.Message}",
                    Level = "ERROR",
                    IsError = true
                });
            }
            return;
        }

        string clientPath = customClientPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientPath) && secretsManager != null)
            try
            {
                NexusSecrets secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                clientPath = secrets.GameClientPath;
            }
            catch
            {
                // Fallback to environment
            }

        if (string.IsNullOrWhiteSpace(clientPath))
            clientPath = Environment.GetEnvironmentVariable("HON_GAME_PATH") ??
                         Environment.GetEnvironmentVariable("WILLOWMAKER_PATH") ??
                         string.Empty;

        string solutionRoot = FindSolutionRoot();
        string gamesClientDir = Path.Combine(solutionRoot, "games", "client");
        if (!string.IsNullOrWhiteSpace(clientPath) && Directory.Exists(clientPath))
            gamesClientDir = clientPath;

        string exeToRun = string.Empty;
        string workingDir = gamesClientDir;

        // 1. Prioritize active build outputs for WILLOWMAKER launcher (Release first, then Debug)
        string[] candidateLaunchers =
        [
            Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Release", "net11.0", "WILLOWMAKER.exe"),
            Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Debug", "net11.0", "WILLOWMAKER.exe"),
            Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Release", "net10.0", "WILLOWMAKER.exe"),
            Path.Combine(solutionRoot, "src", "apps", "willowmaker", "bin", "Debug", "net10.0", "WILLOWMAKER.exe")
        ];

        string? resolvedLauncher = candidateLaunchers
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(resolvedLauncher))
        {
            exeToRun = resolvedLauncher;
            workingDir = gamesClientDir;
        }
        else if (!string.IsNullOrWhiteSpace(clientPath) && File.Exists(clientPath))
        {
            exeToRun = clientPath;
            workingDir = clientPath.Contains("willowmaker", StringComparison.OrdinalIgnoreCase)
                ? gamesClientDir
                : Path.GetDirectoryName(clientPath) ?? gamesClientDir;
        }
        else
        {
            string fallbackHon = Path.Combine(gamesClientDir, "hon_x64.exe");
            if (File.Exists(fallbackHon))
            {
                exeToRun = fallbackHon;
                workingDir = gamesClientDir;
            }
        }

        if (string.IsNullOrEmpty(exeToRun) || !File.Exists(exeToRun))
        {
            AppendLog(new LogMessage
            {
                ServiceName = "GAME CLIENT",
                Message = $"Could not find WILLOWMAKER.exe or hon_x64.exe in configured path: {clientPath}",
                Level = "ERROR",
                IsError = true
            });
            return;
        }

        try
        {
            string masterServerHost = CurrentClusterState == ServerState.Running ? "127.0.0.1:55555" : "masterserver.newbs.io";
            if (CurrentClusterState != ServerState.Running && secretsManager != null)
                try
                {
                    NexusSecrets sec = await secretsManager.LoadSecretsAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(sec.MasterServerAddress))
                    {
                        if (Uri.TryCreate(sec.MasterServerAddress.Trim(), UriKind.Absolute, out Uri? u))
                            masterServerHost = u.Authority;
                        else
                            masterServerHost = sec.MasterServerAddress.Trim();
                    }
                }
                catch
                {
                    // Fallback to default
                }

            if (masterServerHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                masterServerHost = masterServerHost[7..];
            else if (masterServerHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                masterServerHost = masterServerHost[8..];
            masterServerHost = masterServerHost.TrimEnd('/');

            if (masterServerHost.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                masterServerHost = masterServerHost.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);

            string arguments = string.Empty;
            if (exeToRun.EndsWith("hon_x64.exe", StringComparison.OrdinalIgnoreCase) ||
                exeToRun.EndsWith("hon.exe", StringComparison.OrdinalIgnoreCase))
            {
                arguments =
                    $"-masterserver {masterServerHost} -chatserver 127.0.0.1:11031 -webserver {masterServerHost} -messageserver {masterServerHost} {ClientModMountBuilder.GetCanonicalModString()}";
            }
            else if (CurrentClusterState == ServerState.Running &&
                     exeToRun.Contains("willowmaker", StringComparison.OrdinalIgnoreCase))
            {
                arguments = "--masterserver localhost:55555";
            }

            string userdataDir = Path.Combine(workingDir, "userdata");
            string usergameDir = Path.Combine(userdataDir, "game");
            Directory.CreateDirectory(userdataDir);
            Directory.CreateDirectory(usergameDir);
            Directory.CreateDirectory(Path.Combine(usergameDir, "logs"));
            Directory.CreateDirectory(Path.Combine(usergameDir, "replays"));
            Directory.CreateDirectory(Path.Combine(usergameDir, "shaders"));
            Directory.CreateDirectory(Path.Combine(userdataDir, "editor"));

            // Ensure game client working directories are created
            AppendLog(new LogMessage
            {
                ServiceName = "GAME CLIENT",
                Message = $"Userdata subdirectories initialized for working directory: {workingDir}",
                Level = "INFO",
                IsError = false
            });

            if (IsTestEnvironment())
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "GAME CLIENT",
                    Message = $"[TestEnvironment] Suppressed desktop external process spawn for '{exeToRun}' during automated test run.",
                    Level = "INFO"
                });
                return;
            }

            ProcessStartInfo psi = new()
            {
                FileName = exeToRun,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };
            Process? clientProc = Process.Start(psi);
            if (clientProc != null) RegisterSessionPid(clientProc.Id, "GAME CLIENT");

            CancellationToken monitorToken = GetMonitorTokenOrCanceled();
            string clientLogPath = Path.Combine(workingDir, "WILLOWMAKER.log");
            StartFileLogTailer(clientLogPath, "GAME CLIENT", monitorToken);

            string[] candidateConsoleLogs =
            [
                Path.Combine(workingDir, "userdata", "game", "console.log"),
                Path.Combine(workingDir, "userdata", "game", "logs", "console.log"),
                Path.Combine(workingDir, "userdata", "logs", "console.log"),
                Path.Combine(workingDir, "userdata", "console.log"),
                Path.Combine(workingDir, "game", "logs", "console.log"),
                Path.Combine(workingDir, "game", "console.log"),
                Path.Combine(workingDir, "console.log")
            ];
            StartFileLogTailer(candidateConsoleLogs, "GAME CLIENT", monitorToken);

            AppendLog(new LogMessage
            {
                ServiceName = "GAME CLIENT",
                Message = $"Launched Game Client: {exeToRun}",
                Level = "INFO",
                IsError = false
            });
        }
        catch (Exception ex)
        {
            AppendLog(new LogMessage
            {
                ServiceName = "GAME CLIENT",
                Message = $"Failed to launch Game Client: {ex.Message}",
                Level = "ERROR",
                IsError = true
            });
        }
    }

    public Task LaunchGameServerAsync(string? customServerPath = null, CancellationToken cancellationToken = default) { if (BundledLayoutResolver.TryResolve(out _)) { if (_standalone != null) return _standalone.LaunchCompelAsync(cancellationToken); return Task.CompletedTask; } return LaunchGameServerAsyncCore(customServerPath, cancellationToken); } private async Task LaunchGameServerAsyncCore(string? customServerPath = null,
        CancellationToken cancellationToken = default)
    {
        // 0. If COMPEL is already running as part of the cluster (listening on control plane port 11234),
        // signal it to start dedicated server worker instances rather than spawning a duplicate process.
        try
        {
            using HttpClient client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromMilliseconds(1500));
            using HttpResponseMessage pingResp = await client.GetAsync("http://127.0.0.1:11234/ping", cancellationToken);
            if (pingResp.IsSuccessStatusCode)
            {
                string token = string.Empty;
                if (secretsManager != null)
                {
                    try
                    {
                        NexusSecrets sec = await secretsManager.LoadSecretsAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(sec.CompelAuthenticationToken) && sec.CompelAuthenticationToken != "...")
                            token = sec.CompelAuthenticationToken.Trim();
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(token))
                    token = Environment.GetEnvironmentVariable("COMPEL_AUTHENTICATION_TOKEN") ?? string.Empty;

                using HttpRequestMessage startReq = new(HttpMethod.Post, "http://127.0.0.1:11234/instances/start");
                if (!string.IsNullOrWhiteSpace(token))
                    startReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

                using HttpResponseMessage startResp = await client.SendAsync(startReq, cancellationToken);
                if (startResp.IsSuccessStatusCode)
                {
                    AppendLog(new LogMessage
                    {
                        ServiceName = "GAME SERVER",
                        Message = "Started dedicated game server instances via running COMPEL cluster supervisor on port 11234.",
                        Level = "INFO",
                        IsError = false
                    });
                    return;
                }
            }
        }
        catch
        {
            // COMPEL not running on cluster port 11234, proceed to launch standalone COMPEL.exe
        }

        string serverPath = customServerPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverPath) && secretsManager != null)
            try
            {
                NexusSecrets secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                serverPath = secrets.CompelPath;
            }
            catch
            {
                // Fallback to environment
            }

        if (string.IsNullOrWhiteSpace(serverPath))
            serverPath = Environment.GetEnvironmentVariable("COMPEL_PATH") ?? string.Empty;

        string exeToRun = string.Empty;
        string workingDir = string.Empty;

        // 1. Direct configured path resolution from Secrets Vault
        if (!string.IsNullOrWhiteSpace(serverPath))
        {
            if (File.Exists(serverPath))
            {
                exeToRun = serverPath;
                workingDir = Path.GetDirectoryName(serverPath) ?? string.Empty;
            }
            else if (Directory.Exists(serverPath))
            {
                string[] directCandidates = ["COMPEL.exe", "hon_x64.exe", "hon.exe", "COMPEL", "hon_x64", "hon"];
                foreach (string candidate in directCandidates)
                {
                    string full = Path.Combine(serverPath, candidate);
                    if (File.Exists(full))
                    {
                        exeToRun = full;
                        workingDir = serverPath;
                        break;
                    }
                }
            }
        }

        // 2. Select newest executable across deployed canonical release and active build outputs
        string solutionRoot = FindSolutionRoot();
        string honServerDir = Path.Combine(solutionRoot, "games", "hon server");
        string rawServerDir = Path.Combine(solutionRoot, "games", "server");

        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(honServerDir) && Directory.Exists(rawServerDir))
            {
                try
                {
                    Directory.CreateSymbolicLink(honServerDir, rawServerDir);
                }
                catch { }
            }
        }

        string preferredServerDir = (OperatingSystem.IsWindows() && Directory.Exists(honServerDir))
            ? honServerDir
            : rawServerDir;

        string[] candidateFiles =
        [
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net11.0", "COMPEL.exe"),
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net11.0", "COMPEL.exe"),
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net10.0", "COMPEL.exe"),
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net10.0", "COMPEL.exe"),
            Path.Combine(honServerDir, "COMPEL.exe"),
            Path.Combine(rawServerDir, "COMPEL.exe"),
            Path.Combine(honServerDir, "hon_x64.exe"),
            Path.Combine(rawServerDir, "hon_x64.exe")
        ];

        string? newestCandidate = candidateFiles
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(newestCandidate))
        {
            if (string.IsNullOrEmpty(exeToRun) ||
                !File.Exists(exeToRun) ||
                File.GetLastWriteTimeUtc(newestCandidate) > File.GetLastWriteTimeUtc(exeToRun))
            {
                exeToRun = newestCandidate;
                workingDir = newestCandidate.Contains("apps", StringComparison.OrdinalIgnoreCase)
                    ? preferredServerDir
                    : Path.GetDirectoryName(newestCandidate) ?? preferredServerDir;
            }
        }
        else
        {
            workingDir = preferredServerDir;
        }

        if (OperatingSystem.IsWindows() && !workingDir.Any(char.IsWhiteSpace) && Directory.Exists(honServerDir))
        {
            workingDir = honServerDir;
        }

        if (string.IsNullOrEmpty(exeToRun) || !File.Exists(exeToRun))
        {
            AppendLog(new LogMessage
            {
                ServiceName = "GAME SERVER",
                Message = $"Could not find COMPEL.exe or hon_x64.exe in configured path: {serverPath}",
                Level = "ERROR",
                IsError = true
            });
            return;
        }

        try
        {
            if (IsTestEnvironment())
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "GAME SERVER",
                    Message = $"[TestEnvironment] Suppressed desktop external process spawn for '{exeToRun}' during automated test run.",
                    Level = "INFO"
                });
                return;
            }

            int compelOffset = 0;
            int compelInstances = 2;
            int compelControlPlanePort = 11234;
            int masterServerPort = 55555;
            string compelGateway = ASPIRE.Common.Network.SplitHorizonEndpointResolver.GetLocalIPAddress();
            NexusSecrets? loadedSecrets = null;

            if (secretsManager != null)
            {
                try
                {
                    NexusSecrets sec = await secretsManager.LoadSecretsAsync(cancellationToken);
                    loadedSecrets = sec;

                    compelOffset = sec.CompelPortRangeOffset;
                    compelInstances = sec.CompelInstances > 0 ? sec.CompelInstances : 2;
                    compelControlPlanePort = sec.CompelControlPlanePort > 0
                        ? sec.CompelControlPlanePort
                        : 11234 + compelOffset;
                    masterServerPort = sec.MasterServerPort > 0 ? sec.MasterServerPort : 55555;

                    if (!string.IsNullOrWhiteSpace(sec.SelectedLanIp))
                    {
                        compelGateway = sec.SelectedLanIp.Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(sec.CompelGateway) &&
                             !sec.CompelGateway.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                             !sec.CompelGateway.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    {
                        compelGateway = sec.CompelGateway.Trim();
                    }
                }
                catch (Exception secEx)
                {
                    AppendLog(new LogMessage
                    {
                        ServiceName = "GAME SERVER",
                        Message = $"[WARN] Could not load COMPEL overrides from SecretsManager ({secEx.GetType().Name}: {secEx.Message}); using auto-detected LAN gateway {compelGateway} and offset {compelOffset}.",
                        Level = "WARN"
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(compelGateway))
            {
                compelGateway = "127.0.0.1";
            }

            string localMasterEndpoint = $"127.0.0.1:{masterServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            if (OperatingSystem.IsWindows())
            {
                Kongor.Core.Network.FirewallUdpAuditReport? fwReport =
                    await Kongor.Core.Network.WindowsFirewallUdpCoverageAuditor.RunAuditAsync(
                        compelOffset, compelInstances, compelGateway, cancellationToken);
                if (fwReport is { IsFullyCovered: false, WarningBanner: not null })
                {
                    AppendLog(new LogMessage
                    {
                        ServiceName = "GAME SERVER",
                        Message = fwReport.WarningBanner,
                        Level = "WARN"
                    });
                    AppendLog(new LogMessage
                    {
                        ServiceName = "GAME SERVER",
                        Message = $"[WAN-REACHABILITY] Fix in elevated PowerShell: {fwReport.RemediationPowerShellCommand} | Router NAT forward: {fwReport.RouterPortForwardSummary}",
                        Level = "WARN"
                    });
                }
            }

            ProcessStartInfo psi = BuildLocalCompelProcessStartInfo(
                exeToRun,
                workingDir,
                loadedSecrets,
                compelOffset,
                compelControlPlanePort,
                compelInstances,
                compelGateway,
                localMasterEndpoint,
                queueArcadeMode: false,
                resolvedHostUsername: loadedSecrets?.HostAccountUserName);
            Process? serverProc = Process.Start(psi);
            if (serverProc != null) RegisterSessionPid(serverProc.Id, "GAME SERVER");

            string today = TimeProvider.System.GetUtcNow().ToString("yyyyMMdd");
            string[] candidateServerLogs =
            [
                Path.Combine(workingDir, $"COMPEL{today}.log"),
                Path.Combine(Path.GetDirectoryName(exeToRun) ?? workingDir, $"COMPEL{today}.log"),
                Path.Combine(honServerDir, $"COMPEL{today}.log"),
                Path.Combine(rawServerDir, $"COMPEL{today}.log"),
                Path.Combine(workingDir, "logs", $"COMPEL-{today}.log"),
                Path.Combine(honServerDir, "logs", $"COMPEL-{today}.log"),
                Path.Combine(workingDir, "COMPEL.log"),
                Path.Combine(Path.GetDirectoryName(exeToRun) ?? workingDir, "COMPEL.log"),
                Path.Combine(honServerDir, "COMPEL.log"),
                Path.Combine(rawServerDir, "COMPEL.log"),
                Path.Combine(workingDir, "userdata", "game", "console.log"),
                Path.Combine(workingDir, "userdata", "game", "logs", "console.log"),
                Path.Combine(workingDir, "game", "console.log"),
                Path.Combine(workingDir, "console.log")
            ];

            StartFileLogTailer(candidateServerLogs, "GAME SERVER", cancellationToken);

            AppendLog(new LogMessage
            {
                ServiceName = "GAME SERVER",
                Message = $"Launched COMPEL Game Server from {exeToRun}",
                Level = "INFO"
            });
        }
        catch (Exception ex)
        {
            AppendLog(new LogMessage
            {
                ServiceName = "GAME SERVER",
                Message = $"Failed to launch Game Server: {ex.Message}",
                Level = "ERROR",
                IsError = true
            });
        }
    }

    internal const string QueueArcadeRedisEnabledKey = "kongor:queue_arcade:enabled";
    internal const int QueueArcadePortRangeOffset = 250;
    internal const int QueueArcadeControlPlanePort = 11484;

    internal void TrackSessionPidForTest(int pid, string serviceName)
    {
        _sessionPids[pid] = serviceName;
    }

    internal static ProcessStartInfo BuildLocalCompelProcessStartInfo(
        string exeToRun,
        string workingDir,
        NexusSecrets? secrets,
        int portRangeOffset,
        int controlPlanePort,
        int instances,
        string gateway,
        string masterServerEndpoint,
        bool queueArcadeMode,
        string? resolvedHostUsername)
    {
        string offsetStr = portRangeOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string controlPortStr = controlPlanePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string instancesStr = instances.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string fleetTier = queueArcadeMode ? "Q" : "L";
        int warmTarget = queueArcadeMode
            ? (instances > 1 ? 1 : instances)
            : instances;
        string warmTargetStr = warmTarget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string resolvedLocation = !string.IsNullOrWhiteSpace(secrets?.CompelLocation)
            ? secrets.CompelLocation.Trim()
            : "USE";

        List<string> args =
        [
            "--launch-game-servers",
            $"--Compel:QueueArcadeMode={(queueArcadeMode ? "true" : "false")}",
            "--Compel:AutoStart=true",
            $"--Compel:PortRangeOffset={offsetStr}",
            $"--Compel:ControlPlanePort={controlPortStr}",
            $"--Compel:Instances={instancesStr}",
            $"--Compel:FleetTier={fleetTier}",
            $"--Compel:Location=\"{resolvedLocation}\"",
            $"--Compel:Gateway={gateway}",
            $"--Compel:MasterServerAddress={masterServerEndpoint}",
            $"--Compel:InstallationDirectory=\"{workingDir}\""
        ];

        if (queueArcadeMode)
        {
            args.Add($"--Compel:WarmInstancesTarget={warmTargetStr}");
        }

        string trimmedHostUser = resolvedHostUsername?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmedHostUser))
        {
            args.Add($"--HostAccount:UserName=\"{trimmedHostUser}\"");
        }

        ProcessStartInfo psi = new()
        {
            FileName = exeToRun,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = false,
            Arguments = string.Join(" ", args)
        };

        psi.Environment["COMPEL_INSTALLATION_DIRECTORY"] = workingDir;
        psi.Environment["COMPEL_AUTO_START"] = "true";
        psi.Environment["COMPEL_USE_PROXY"] = "false";
        psi.Environment["COMPEL_PORT_RANGE_OFFSET"] = offsetStr;
        psi.Environment["COMPEL_CONTROL_PLANE_PORT"] = controlPortStr;
        psi.Environment["COMPEL_INSTANCES"] = instancesStr;
        psi.Environment["COMPEL_FLEET_TIER"] = fleetTier;
        psi.Environment["COMPEL_LOCATION"] = resolvedLocation;
        psi.Environment["COMPEL_GATEWAY"] = gateway;
        psi.Environment["MASTER_SERVER_ADDRESS"] = masterServerEndpoint;
        psi.Environment["COMPEL_QUEUE_ARCADE_MODE"] = queueArcadeMode ? "true" : "false";
        psi.Environment["KONGOR_QUEUE_ARCADE"] = queueArcadeMode ? "1" : "0";

        if (queueArcadeMode)
        {
            psi.Environment["COMPEL_WARM_INSTANCES_TARGET"] = warmTargetStr;
        }

        if (!string.IsNullOrWhiteSpace(trimmedHostUser))
        {
            psi.Environment["HOST_USERNAME"] = trimmedHostUser;
            psi.Environment["COMPEL_USER_NAME"] = trimmedHostUser;
        }

        if (secrets != null)
        {
            if (!string.IsNullOrWhiteSpace(secrets.HostAccountPassword))
            {
                psi.Environment["HOST_PASSWORD"] = secrets.HostAccountPassword;
            }

            if (!string.IsNullOrWhiteSpace(secrets.CompelAuthenticationToken) && secrets.CompelAuthenticationToken != "...")
            {
                psi.Environment["COMPEL_AUTHENTICATION_TOKEN"] = secrets.CompelAuthenticationToken.Trim();
            }

            if (!string.IsNullOrWhiteSpace(secrets.CompelLocation))
            {
                psi.Environment["COMPEL_LOCATION"] = secrets.CompelLocation.Trim();
            }

            string configuredPrefix = secrets.CompelServerNamePrefix?.Trim() ?? string.Empty;
            if (queueArcadeMode)
            {
                string arcadePrefix = string.IsNullOrWhiteSpace(configuredPrefix)
                    ? "QUEUE ARCADE"
                    : (configuredPrefix.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase)
                        ? configuredPrefix
                        : $"QUEUE ARCADE {configuredPrefix}");
                psi.Environment["COMPEL_SERVER_NAME_PREFIX"] = arcadePrefix;
            }
            else if (!string.IsNullOrWhiteSpace(configuredPrefix))
            {
                psi.Environment["COMPEL_SERVER_NAME_PREFIX"] = configuredPrefix;
            }
        }
        else if (queueArcadeMode)
        {
            psi.Environment["COMPEL_SERVER_NAME_PREFIX"] = "QUEUE ARCADE";
        }

        return psi;
    }

    public Task LaunchQueueArcadeServerAsync(string? customServerPath = null, CancellationToken cancellationToken = default)
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            if (_standalone != null) return _standalone.LaunchQueueArcadeAsync(cancellationToken);
            return Task.CompletedTask;
        }
        return LaunchQueueArcadeServerAsyncCore(customServerPath, cancellationToken);
    }

    private async Task LaunchQueueArcadeServerAsyncCore(string? customServerPath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpClient client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromMilliseconds(1500));
            using HttpResponseMessage pingResp = await client.GetAsync($"http://127.0.0.1:{QueueArcadeControlPlanePort}/ping", cancellationToken);
            if (pingResp.IsSuccessStatusCode)
            {
                string token = string.Empty;
                if (secretsManager != null)
                {
                    try
                    {
                        NexusSecrets sec = await secretsManager.LoadSecretsAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(sec.CompelAuthenticationToken) && sec.CompelAuthenticationToken != "...")
                            token = sec.CompelAuthenticationToken.Trim();
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(token))
                    token = Environment.GetEnvironmentVariable("COMPEL_AUTHENTICATION_TOKEN") ?? string.Empty;

                using HttpRequestMessage startReq = new(HttpMethod.Post, $"http://127.0.0.1:{QueueArcadeControlPlanePort}/instances/start");
                if (!string.IsNullOrWhiteSpace(token))
                    startReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

                using HttpResponseMessage startResp = await client.SendAsync(startReq, cancellationToken);
                if (startResp.IsSuccessStatusCode)
                {
                    AppendLog(new LogMessage
                    {
                        ServiceName = "QUEUE ARCADE",
                        Message = $"Started Queue Arcade worker instances via running COMPEL supervisor on port {QueueArcadeControlPlanePort}.",
                        Level = "INFO",
                        IsError = false
                    });
                    return;
                }
            }
        }
        catch
        {
            // Queue Arcade COMPEL not yet running on port 11484; proceed to spawn isolated supervisor
        }

        NexusSecrets? loadedSecrets = null;
        string serverPath = customServerPath ?? string.Empty;
        if (secretsManager != null)
        {
            try
            {
                loadedSecrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(serverPath))
                    serverPath = loadedSecrets.CompelPath;
            }
            catch (Exception secEx)
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "QUEUE ARCADE",
                    Message = $"[WARN] Could not load secrets for Queue Arcade launch ({secEx.GetType().Name}: {secEx.Message}). Using defaults.",
                    Level = "WARN"
                });
            }
        }

        if (string.IsNullOrWhiteSpace(serverPath))
            serverPath = Environment.GetEnvironmentVariable("COMPEL_PATH") ?? string.Empty;

        string solutionRoot = FindSolutionRoot();
        string honServerDir = Path.Combine(solutionRoot, "games", "hon server");
        string rawServerDir = Path.Combine(solutionRoot, "games", "server");

        if (OperatingSystem.IsWindows() && !Directory.Exists(honServerDir) && Directory.Exists(rawServerDir))
        {
            try { Directory.CreateSymbolicLink(honServerDir, rawServerDir); } catch { }
        }

        string preferredServerDir = (OperatingSystem.IsWindows() && Directory.Exists(honServerDir))
            ? honServerDir
            : rawServerDir;

        string exeToRun = string.Empty;
        string workingDir = string.Empty;

        if (!string.IsNullOrWhiteSpace(serverPath))
        {
            if (File.Exists(serverPath))
            {
                exeToRun = serverPath;
                workingDir = Path.GetDirectoryName(serverPath) ?? string.Empty;
            }
            else if (Directory.Exists(serverPath))
            {
                string[] directCandidates = ["COMPEL.exe", "hon_x64.exe", "hon.exe", "COMPEL", "hon_x64", "hon"];
                foreach (string candidate in directCandidates)
                {
                    string full = Path.Combine(serverPath, candidate);
                    if (File.Exists(full))
                    {
                        exeToRun = full;
                        workingDir = serverPath;
                        break;
                    }
                }
            }
        }

        string[] candidateFiles =
        [
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net11.0", "COMPEL.exe"),
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net11.0", "COMPEL.exe"),
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net10.0", "COMPEL.exe"),
            Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net10.0", "COMPEL.exe"),
            Path.Combine(honServerDir, "COMPEL.exe"),
            Path.Combine(rawServerDir, "COMPEL.exe"),
            Path.Combine(honServerDir, "hon_x64.exe"),
            Path.Combine(rawServerDir, "hon_x64.exe")
        ];

        string? newestCandidate = candidateFiles
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(newestCandidate))
        {
            if (string.IsNullOrEmpty(exeToRun) ||
                !File.Exists(exeToRun) ||
                File.GetLastWriteTimeUtc(newestCandidate) > File.GetLastWriteTimeUtc(exeToRun))
            {
                exeToRun = newestCandidate;
                workingDir = newestCandidate.Contains("apps", StringComparison.OrdinalIgnoreCase)
                    ? preferredServerDir
                    : Path.GetDirectoryName(newestCandidate) ?? preferredServerDir;
            }
        }
        else
        {
            workingDir = preferredServerDir;
        }

        if (OperatingSystem.IsWindows() && !workingDir.Any(char.IsWhiteSpace) && Directory.Exists(honServerDir))
        {
            workingDir = honServerDir;
        }

        if (string.IsNullOrEmpty(exeToRun) || !File.Exists(exeToRun))
        {
            AppendLog(new LogMessage
            {
                ServiceName = "QUEUE ARCADE",
                Message = $"Could not find COMPEL.exe in configured path '{serverPath}'. Build COMPEL first via: scripts/Invoke-KongorTestLock.ps1 -Owner compel -- dotnet build src/apps/compel/COMPEL.csproj",
                Level = "ERROR",
                IsError = true
            });
            return;
        }

        string baseHostUser = !string.IsNullOrWhiteSpace(loadedSecrets?.HostAccountUserName)
            ? loadedSecrets.HostAccountUserName.Trim()
            : "guccihost";
        string arcadeHostUser = ContainerLaunchCommandBuilder.ResolveQueueArcadeHostUsername(baseHostUser);
        ContainerLaunchCommandBuilder.EnsureDistinctHostAccounts(baseHostUser, arcadeHostUser);

        if (IsTestEnvironment())
        {
            AppendLog(new LogMessage
            {
                ServiceName = "QUEUE ARCADE",
                Message = $"[TestEnvironment] Suppressed desktop external Queue Arcade spawn for '{exeToRun}' (Account: {arcadeHostUser}, Port: {QueueArcadeControlPlanePort}).",
                Level = "INFO"
            });
            return;
        }

        int arcadeInstances = loadedSecrets is { QueueArcadeInstances: > 0 } ? loadedSecrets.QueueArcadeInstances : 2;
        int masterServerPort = loadedSecrets is { MasterServerPort: > 0 } ? loadedSecrets.MasterServerPort : 55555;
        string compelGateway = ASPIRE.Common.Network.SplitHorizonEndpointResolver.GetLocalIPAddress();
        if (!string.IsNullOrWhiteSpace(loadedSecrets?.SelectedLanIp))
        {
            compelGateway = loadedSecrets.SelectedLanIp.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(loadedSecrets?.CompelGateway) &&
                 !loadedSecrets.CompelGateway.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                 !loadedSecrets.CompelGateway.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            compelGateway = loadedSecrets.CompelGateway.Trim();
        }
        if (string.IsNullOrWhiteSpace(compelGateway)) compelGateway = "127.0.0.1";

        string localMasterEndpoint = $"127.0.0.1:{masterServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Kongor.Core.Network.FirewallUdpAuditReport? fwReport =
                    await Kongor.Core.Network.WindowsFirewallUdpCoverageAuditor.RunAuditAsync(
                        QueueArcadePortRangeOffset, arcadeInstances, compelGateway, cancellationToken);
                if (fwReport is { IsFullyCovered: false, WarningBanner: not null })
                {
                    AppendLog(new LogMessage
                    {
                        ServiceName = "QUEUE ARCADE",
                        Message = fwReport.WarningBanner,
                        Level = "WARN"
                    });
                }
            }

            ProcessStartInfo psi = BuildLocalCompelProcessStartInfo(
                exeToRun,
                workingDir,
                loadedSecrets,
                QueueArcadePortRangeOffset,
                QueueArcadeControlPlanePort,
                arcadeInstances,
                compelGateway,
                localMasterEndpoint,
                queueArcadeMode: true,
                resolvedHostUsername: arcadeHostUser);

            Process? arcadeProc = Process.Start(psi);
            if (arcadeProc != null)
            {
                _processes["queue-arcade"] = arcadeProc;
                RegisterSessionPid(arcadeProc.Id, "QUEUE ARCADE");
            }

            string today = TimeProvider.System.GetUtcNow().ToString("yyyyMMdd");
            string[] candidateArcadeLogs =
            [
                Path.Combine(workingDir, $"COMPEL-Arcade{today}.log"),
                Path.Combine(Path.GetDirectoryName(exeToRun) ?? workingDir, $"COMPEL-Arcade{today}.log"),
                Path.Combine(honServerDir, $"COMPEL-Arcade{today}.log"),
                Path.Combine(rawServerDir, $"COMPEL-Arcade{today}.log"),
                Path.Combine(workingDir, "logs", $"COMPEL-Arcade-{today}.log"),
                Path.Combine(honServerDir, "logs", $"COMPEL-Arcade-{today}.log"),
                Path.Combine(workingDir, "COMPEL-Arcade.log"),
                Path.Combine(honServerDir, "COMPEL-Arcade.log")
            ];

            StartFileLogTailer(candidateArcadeLogs, "QUEUE ARCADE", cancellationToken);

            string resolvedArcadeLocation = !string.IsNullOrWhiteSpace(loadedSecrets?.CompelLocation)
                ? loadedSecrets.CompelLocation.Trim()
                : "USE";

            AppendLog(new LogMessage
            {
                ServiceName = "QUEUE ARCADE",
                Message = $"Launched Queue Arcade COMPEL Fleet ({arcadeInstances} instances, 1 warm, region '{resolvedArcadeLocation}', host '{arcadeHostUser}', control http://127.0.0.1:{QueueArcadeControlPlanePort}, UDP {11235 + QueueArcadePortRangeOffset}..{11235 + QueueArcadePortRangeOffset + arcadeInstances - 1}).",
                Level = "INFO"
            });
        }
        catch (Exception ex)
        {
            AppendLog(new LogMessage
            {
                ServiceName = "QUEUE ARCADE",
                Message = $"Failed to launch Queue Arcade Fleet: {ex.Message}",
                Level = "ERROR",
                IsError = true
            });
            throw;
        }
    }

    internal HashSet<int> CollectQueueArcadeTargetPids(
        int? tcpControlOwnerPid,
        IReadOnlyDictionary<int, int> udpPortOwners)
    {
        int currentPid = Environment.ProcessId;
        HashSet<int> targets = [];

        if (_processes.TryGetValue("queue-arcade", out Process? trackedProc))
        {
            try
            {
                if (!trackedProc.HasExited && trackedProc.Id > 0 && trackedProc.Id != currentPid)
                {
                    targets.Add(trackedProc.Id);
                }
            }
            catch
            {
                // Process handle already disposed or exited
            }
        }

        foreach (KeyValuePair<int, string> kvp in _sessionPids)
        {
            if (string.Equals(kvp.Value, "QUEUE ARCADE", StringComparison.OrdinalIgnoreCase) &&
                kvp.Key > 0 &&
                kvp.Key != currentPid)
            {
                targets.Add(kvp.Key);
            }
        }

        if (tcpControlOwnerPid is > 0 && tcpControlOwnerPid.Value != currentPid)
        {
            targets.Add(tcpControlOwnerPid.Value);
        }

        const int minGamePort = 11235 + QueueArcadePortRangeOffset; // 11485
        const int maxGamePort = minGamePort + 49;                    // 11534
        const int minVoicePort = 11735 + QueueArcadePortRangeOffset; // 11985
        const int maxVoicePort = minVoicePort + 49;                  // 12034
        const int arcadeVoiceRelayPort = 11603;

        foreach (KeyValuePair<int, int> kvp in udpPortOwners)
        {
            int port = kvp.Key;
            int ownerPid = kvp.Value;
            if (ownerPid <= 0 || ownerPid == currentPid) continue;

            bool isArcadePort =
                port == QueueArcadeControlPlanePort ||
                port == arcadeVoiceRelayPort ||
                (port >= minGamePort && port <= maxGamePort) ||
                (port >= minVoicePort && port <= maxVoicePort);

            if (isArcadePort)
            {
                targets.Add(ownerPid);
            }
        }

        return targets;
    }

    public async Task StopQueueArcadeServersAsync(CancellationToken cancellationToken = default)
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            if (_standalone != null)
            {
                await _standalone.StopQueueArcadeAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // 1. Graceful HTTP stop on Queue Arcade control plane (11484)
        try
        {
            string token = string.Empty;
            if (secretsManager != null)
            {
                try
                {
                    NexusSecrets sec = await secretsManager.LoadSecretsAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(sec.CompelAuthenticationToken) && sec.CompelAuthenticationToken != "...")
                        token = sec.CompelAuthenticationToken.Trim();
                }
                catch { }
            }

            using HttpClient client = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromMilliseconds(1500));
            using HttpRequestMessage stopReq = new(HttpMethod.Post, $"http://127.0.0.1:{QueueArcadeControlPlanePort}/instances/stop");
            if (!string.IsNullOrWhiteSpace(token))
                stopReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage stopResp = await client.SendAsync(stopReq, cancellationToken);
            if (stopResp.IsSuccessStatusCode)
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "QUEUE ARCADE",
                    Message = $"Signaled graceful worker stop to Queue Arcade supervisor on port {QueueArcadeControlPlanePort}.",
                    Level = "INFO"
                });
            }
        }
        catch
        {
            // Control plane already offline
        }

        // 2. Stop Docker Queue Arcade container if Docker daemon is reachable
        if (!IsTestEnvironment() && IsDockerAvailableFast())
        {
            try
            {
                using DockerClient dockerClient = new DockerClientConfiguration().CreateClient();
                foreach (string arcadeContainerName in new[]
                         {
                             ContainerLaunchCommandBuilder.QueueArcadeContainerName,
                             ContainerLaunchCommandBuilder.LegacyQueueArcadeContainerName
                         })
                {
                    try
                    {
                        await dockerClient.Containers.StopContainerAsync(
                            arcadeContainerName,
                            new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
                            cancellationToken);
                        AppendLog(new LogMessage
                        {
                            ServiceName = "QUEUE ARCADE",
                            Message = $"Stopped Docker container '{arcadeContainerName}'.",
                            Level = "INFO"
                        });
                    }
                    catch (DockerContainerNotFoundException notFoundEx)
                    {
                        Debug.WriteLine($"[NexusOrchestratorService] Container '{arcadeContainerName}' not present: {notFoundEx.Message}");
                    }
                }
            }
            catch (DockerContainerNotFoundException)
            {
                // No Docker Queue Arcade container deployed
            }
            catch (Exception dockerEx)
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "QUEUE ARCADE",
                    Message = $"[Note] Docker Queue Arcade stop check: {dockerEx.Message}",
                    Level = "WARN"
                });
            }
        }

        // 3. Surgical PID termination scoped strictly to Queue Arcade ports & session registry
        int? tcpOwner = null;
        Dictionary<int, int> udpOwners = [];
        try
        {
            tcpOwner = ASPIRE.Common.Netcode.Sockets.NativeSocketTable.TryGetPortOwnerPid(
                QueueArcadeControlPlanePort,
                System.Net.Sockets.ProtocolType.Tcp);
            udpOwners = ASPIRE.Common.Netcode.Sockets.NativeSocketTable.GetAllPortOwners(System.Net.Sockets.ProtocolType.Udp);
        }
        catch
        {
            // Best-effort socket table snapshot
        }

        HashSet<int> targetPids = CollectQueueArcadeTargetPids(tcpOwner, udpOwners);
        int stoppedCount = 0;

        foreach (int pid in targetPids)
        {
            try
            {
                using Process proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                    stoppedCount++;
                }
            }
            catch
            {
                // Already exited
            }
            finally
            {
                UnregisterSessionPid(pid);
            }
        }

        if (_processes.TryRemove("queue-arcade", out Process? removedProc))
        {
            try { removedProc.Dispose(); } catch { }
        }

        SetServiceState("queue-arcade", ServerState.Stopped);
        AppendLog(new LogMessage
        {
            ServiceName = "QUEUE ARCADE",
            Message = stoppedCount > 0
                ? $"Stopped {stoppedCount} Queue Arcade process(es) cleanly (Ranked game servers untouched)."
                : "Queue Arcade servers stopped (no active local Queue Arcade processes found).",
            Level = "INFO"
        });
    }

    private async Task<StackExchange.Redis.ConnectionMultiplexer?> TryConnectQueueArcadeRedisAsync(
        NexusSecrets? secrets,
        CancellationToken cancellationToken)
    {
        List<int> candidatePorts = [];
        if (BundledLayoutResolver.TryResolve(out _))
        {
            candidatePorts.Add(28398);
        }

        if (secrets is { DistributedCachePort: > 0 } && !candidatePorts.Contains(secrets.DistributedCachePort))
        {
            candidatePorts.Add(secrets.DistributedCachePort);
        }

        foreach (int fallbackPort in new[] { 28298, 28398, 6379 })
        {
            if (!candidatePorts.Contains(fallbackPort))
            {
                candidatePorts.Add(fallbackPort);
            }
        }

        string password = secrets?.DistributedCachePassword?.Trim() ?? string.Empty;

        foreach (int port in candidatePorts)
        {
            if (!IsPortListeningFast(port)) continue;

            string[] passwordCandidates = string.IsNullOrWhiteSpace(password)
                ? [string.Empty]
                : [password, string.Empty];

            foreach (string pwd in passwordCandidates)
            {
                try
                {
                    StackExchange.Redis.ConfigurationOptions opts = new()
                    {
                        ConnectTimeout = 800,
                        SyncTimeout = 800,
                        AbortOnConnectFail = true
                    };
                    opts.EndPoints.Add("127.0.0.1", port);
                    if (!string.IsNullOrEmpty(pwd))
                    {
                        opts.Password = pwd;
                    }

                    StackExchange.Redis.ConnectionMultiplexer mux =
                        await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(opts).WaitAsync(cancellationToken);
                    if (mux.IsConnected)
                    {
                        return mux;
                    }
                    await mux.DisposeAsync();
                }
                catch
                {
                    // Try next candidate
                }
            }
        }

        return null;
    }

    public async Task<(bool Enabled, bool LiveRedisSynced, string StatusSummary)> GetQueueArcadeStateAsync(
        CancellationToken cancellationToken = default)
    {
        bool vaultEnabled = true;
        NexusSecrets? secrets = null;
        if (secretsManager != null)
        {
            try
            {
                secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                vaultEnabled = secrets.QueueArcadeEnabled;
            }
            catch
            {
                // Fallback to default true
            }
        }

        StackExchange.Redis.ConnectionMultiplexer? mux = await TryConnectQueueArcadeRedisAsync(secrets, cancellationToken);
        if (mux != null)
        {
            await using (mux)
            {
                try
                {
                    StackExchange.Redis.IDatabase db = mux.GetDatabase();
                    StackExchange.Redis.RedisValue raw = await db.StringGetAsync(QueueArcadeRedisEnabledKey);
                    if (raw.HasValue)
                    {
                        string text = raw.ToString().Trim();
                        bool redisEnabled = !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(text, "0", StringComparison.Ordinal);
                        return (redisEnabled, true, redisEnabled ? "Enabled (Live)" : "Paused (Live)");
                    }

                    await db.StringSetAsync(QueueArcadeRedisEnabledKey, vaultEnabled ? "true" : "false");
                    return (vaultEnabled, true, vaultEnabled ? "Enabled (Live)" : "Paused (Live)");
                }
                catch
                {
                    // Fall through to Vault state
                }
            }
        }

        return (vaultEnabled, false, vaultEnabled ? "Enabled (Vault)" : "Paused (Vault)");
    }

    public async Task<(bool Enabled, bool LiveRedisSynced, string StatusMessage)> SetQueueArcadeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        NexusSecrets? secrets = null;
        if (secretsManager != null)
        {
            try
            {
                secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                secrets.QueueArcadeEnabled = enabled;
                await secretsManager.SaveSecretsAsync(secrets, null, cancellationToken);
            }
            catch (Exception ex)
            {
                AppendLog(new LogMessage
                {
                    ServiceName = "QUEUE ARCADE",
                    Message = $"[WARN] Could not persist QueueArcade:Enabled={enabled} to Secrets Vault: {ex.Message}",
                    Level = "WARN"
                });
            }
        }

        bool liveSynced = false;
        StackExchange.Redis.ConnectionMultiplexer? mux = await TryConnectQueueArcadeRedisAsync(secrets, cancellationToken);
        if (mux != null)
        {
            await using (mux)
            {
                try
                {
                    StackExchange.Redis.IDatabase db = mux.GetDatabase();
                    await db.StringSetAsync(QueueArcadeRedisEnabledKey, enabled ? "true" : "false");
                    liveSynced = true;
                }
                catch (Exception redisEx)
                {
                    AppendLog(new LogMessage
                    {
                        ServiceName = "QUEUE ARCADE",
                        Message = $"[WARN] Failed writing {QueueArcadeRedisEnabledKey} to Redis: {redisEx.Message}",
                        Level = "WARN"
                    });
                }
            }
        }

        string stateWord = enabled ? "ENABLED" : "PAUSED (active mini-games will drain gently)";
        string message = liveSynced
            ? $"Queue Arcade {stateWord} — synced live via Redis ({QueueArcadeRedisEnabledKey}) and saved to Vault."
            : $"Queue Arcade {stateWord} in Vault, but Redis cache is offline — start Redis or restart Chat Server for live toggle to take effect.";

        AppendLog(new LogMessage
        {
            ServiceName = "QUEUE ARCADE",
            Message = message,
            Level = liveSynced ? "INFO" : "WARN"
        });

        return (enabled, liveSynced, message);
    }

    public static bool IsManagedProjectKongorContainer(
        ContainerListResponse? container,
        out bool isComposeStack,
        out string displayName)
    {
        isComposeStack = false;
        displayName = string.Empty;
        if (container is null)
        {
            return false;
        }

        List<string> normalizedNames = container.Names?
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Select(static n => n.Trim().TrimStart('/'))
            .Where(static n => n.Length > 0)
            .ToList() ?? [];

        displayName = normalizedNames.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(container.ID))
        {
            displayName = container.ID[..Math.Min(12, container.ID.Length)];
        }

        if (container.Labels != null &&
            container.Labels.TryGetValue("com.docker.compose.project", out string? composeProject) &&
            string.Equals(composeProject?.Trim(), DockerStackProbe.ComposeProjectName, StringComparison.OrdinalIgnoreCase))
        {
            isComposeStack = true;
            return true;
        }

        foreach (string candidateName in normalizedNames)
        {
            if (candidateName.StartsWith("kongor-stack-", StringComparison.OrdinalIgnoreCase))
            {
                isComposeStack = true;
                return true;
            }

            if (candidateName.Equals(ContainerLaunchCommandBuilder.RankedContainerName, StringComparison.OrdinalIgnoreCase) ||
                candidateName.Equals(ContainerLaunchCommandBuilder.QueueArcadeContainerName, StringComparison.OrdinalIgnoreCase) ||
                candidateName.Equals(ContainerLaunchCommandBuilder.LegacyRankedContainerName, StringComparison.OrdinalIgnoreCase) ||
                candidateName.Equals(ContainerLaunchCommandBuilder.LegacyQueueArcadeContainerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string image = container.Image?.Trim() ?? string.Empty;
        if (image.StartsWith("kongor-stack-", StringComparison.OrdinalIgnoreCase))
        {
            isComposeStack = true;
            return true;
        }

        if (image.StartsWith("projectkongor/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public async Task<RuntimeExitInspectionResult> InspectRuntimeForExitAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Console.WriteLine("[ControlPanel] [EXIT-PROBE] Inspecting active Docker containers and local host services before window close...");

        if (BundledLayoutResolver.TryResolve(out _) && _standalone is { IsActive: true })
        {
            sw.Stop();
            const string standaloneMsg = "Standalone local cluster is active.";
            Console.WriteLine($"[ControlPanel] [EXIT-PROBE] Completed in {sw.ElapsedMilliseconds}ms: Standalone active.");
            AppendLog(new LogMessage
            {
                ServiceName = "ORCHESTRATOR",
                Message = $"[EXIT-PROBE] {standaloneMsg}",
                Level = "INFO"
            });
            return new RuntimeExitInspectionResult(
                HasRunningDockerServices: false,
                RunningDockerContainerCount: 0,
                RunningDockerContainerNames: [],
                IsDockerStackActive: false,
                HasRunningLocalHostServices: true,
                SummaryMessage: standaloneMsg);
        }

        List<string> runningDockerNames = [];
        HashSet<string> seenDockerNames = new(StringComparer.OrdinalIgnoreCase);
        bool isComposeStack = _isDockerStackOperationActive;

        bool canQueryDocker = (_dockerHealth != null && (IsTestEnvironment() || DockerHealthService.IsDockerDaemonRunningFast())) ||
                              (!IsTestEnvironment() && DockerHealthService.IsDockerDaemonRunningFast());

        if (canQueryDocker)
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            DockerClient? ownedClient = null;
            try
            {
                IDockerClient dockerClient;
                if (_dockerHealth != null)
                {
                    dockerClient = _dockerHealth.Client;
                }
                else
                {
                    ownedClient = new DockerClientConfiguration().CreateClient();
                    dockerClient = ownedClient;
                }

                IList<ContainerListResponse> containers = await dockerClient.Containers
                    .ListContainersAsync(new ContainersListParameters { All = false }, timeoutCts.Token)
                    .ConfigureAwait(false);

                foreach (ContainerListResponse container in containers)
                {
                    if (IsManagedProjectKongorContainer(container, out bool containerIsStack, out string name))
                    {
                        if (containerIsStack)
                        {
                            isComposeStack = true;
                        }

                        if (!string.IsNullOrWhiteSpace(name) && seenDockerNames.Add(name))
                        {
                            runningDockerNames.Add(name);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlPanel] [EXIT-PROBE] Docker.DotNet container listing failed: {ex.Message}");
                AppendLog(new LogMessage
                {
                    ServiceName = "DOCKER STACK",
                    Message = $"[EXIT-PROBE] Docker.DotNet container inspection fallback triggered: {ex.Message}",
                    Level = "WARN"
                });

                if (!IsTestEnvironment() && DockerStackProbe.IsDockerEngineAvailable())
                {
                    try
                    {
                        using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        probeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                        DockerComposeStackSnapshot fallbackSnap = await DockerStackProbe
                            .ProbeAsync(FindSolutionRoot(), includeFleetHttpStatus: false, probeCts.Token)
                            .ConfigureAwait(false);

                        if (fallbackSnap.IsStackActive)
                        {
                            isComposeStack = true;
                            foreach (DockerComposeServiceState svc in fallbackSnap.Services.Values.Where(static s => s.IsRunning))
                            {
                                string name = string.IsNullOrWhiteSpace(svc.ContainerName) ? svc.Service : svc.ContainerName;
                                if (seenDockerNames.Add(name))
                                {
                                    runningDockerNames.Add(name);
                                }
                            }
                        }
                    }
                    catch (Exception probeEx)
                    {
                        Console.WriteLine($"[ControlPanel] [EXIT-PROBE] DockerStackProbe fallback skipped: {probeEx.Message}");
                    }
                }
            }
            finally
            {
                ownedClient?.Dispose();
            }
        }

        bool hasDocker = runningDockerNames.Count > 0 || _isDockerStackOperationActive;

        bool hasLocalHost = false;
        foreach (KeyValuePair<string, Process> kvp in _processes)
        {
            if (string.Equals(kvp.Key, "game-client", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SafeHasExited(kvp.Value))
            {
                hasLocalHost = true;
                break;
            }
        }

        if (!hasLocalHost && !IsTestEnvironment())
        {
            foreach (string procName in NativeLocalHostProcessNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(procName);
                    foreach (Process proc in procs)
                    {
                        try
                        {
                            if (!hasLocalHost && proc.Id != Environment.ProcessId && !IsContainerizedLinuxProcess(proc.Id))
                            {
                                hasLocalHost = true;
                            }
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
                catch (Exception procEnumEx)
                {
                    Debug.WriteLine($"[ControlPanel] [EXIT-PROBE] Non-fatal process enumeration notice for '{procName}': {procEnumEx.Message}");
                }

                if (hasLocalHost)
                {
                    break;
                }
            }
        }

        sw.Stop();
        string summary = hasDocker
            ? (isComposeStack
                ? $"Docker stack active ({Math.Max(1, runningDockerNames.Count)} container(s))."
                : $"Docker container(s) active: {string.Join(", ", runningDockerNames)}.")
            : (hasLocalHost
                ? "Local host cluster services are active."
                : "All background services are stopped.");

        string probeLog =
            $"[EXIT-PROBE] Completed in {sw.ElapsedMilliseconds}ms: HasDocker={hasDocker} (count={runningDockerNames.Count}, stack={isComposeStack}, names=[{string.Join(", ", runningDockerNames)}]), HasLocalHost={hasLocalHost}.";
        Console.WriteLine($"[ControlPanel] {probeLog}");
        AppendLog(new LogMessage
        {
            ServiceName = "ORCHESTRATOR",
            Message = probeLog,
            Level = "INFO"
        });

        return new RuntimeExitInspectionResult(
            HasRunningDockerServices: hasDocker,
            RunningDockerContainerCount: runningDockerNames.Count,
            RunningDockerContainerNames: runningDockerNames,
            IsDockerStackActive: isComposeStack,
            HasRunningLocalHostServices: hasLocalHost,
            SummaryMessage: summary);
    }

    public void DetachForBackgroundExit()
    {
        if (Interlocked.Exchange(ref _disposedOrDetached, 1) != 0)
        {
            Console.WriteLine("[ControlPanel] [ORCHESTRATOR] DetachForBackgroundExit skipped (already detached or disposed).");
            return;
        }

        const string detachMsg =
            "[ORCHESTRATOR] DetachForBackgroundExit invoked — stopping UI log/health pollers while preserving all Docker containers, session_pids.json, and companion processes (WILLOWMAKER / hon_x64).";
        Console.WriteLine($"[ControlPanel] {detachMsg}");
        AppendLog(new LogMessage
        {
            ServiceName = "ORCHESTRATOR",
            Message = detachMsg,
            Level = "INFO"
        });

        try
        {
            _logTailerCts?.Cancel();
            _logTailerCts?.Dispose();
            _logTailerCts = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlPanel] [ORCHESTRATOR] Non-fatal log tailer detach error: {ex.Message}");
        }

        try
        {
            _healthMonitorCts.Cancel();
            _healthMonitorCts.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ControlPanel] [ORCHESTRATOR] Non-fatal health monitor detach error: {ex.Message}");
        }

        foreach (Process process in _processes.Values)
        {
            try
            {
                try { process.CancelOutputRead(); }
                catch (Exception cancelOutEx) { Debug.WriteLine($"[ControlPanel] [ORCHESTRATOR] CancelOutputRead notice: {cancelOutEx.Message}"); }
                try { process.CancelErrorRead(); }
                catch (Exception cancelErrEx) { Debug.WriteLine($"[ControlPanel] [ORCHESTRATOR] CancelErrorRead notice: {cancelErrEx.Message}"); }
                process.Dispose();
            }
            catch (Exception procDisposeEx)
            {
                Debug.WriteLine($"[ControlPanel] [ORCHESTRATOR] Background process detach notice: {procDisposeEx.Message}");
            }
        }

        _processes.Clear();
    }

    private CancellationToken GetMonitorTokenOrCanceled()
    {
        if (Volatile.Read(ref _disposedOrDetached) != 0)
        {
            return new CancellationToken(canceled: true);
        }

        try
        {
            return _healthMonitorCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return new CancellationToken(canceled: true);
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ControlPanel] [ORCHESTRATOR] SafeHasExited notice: {ex.Message}");
            return true;
        }
    }

    private static bool IsContainerizedLinuxProcess(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            string cgroupPath = $"/proc/{pid}/cgroup";
            if (!File.Exists(cgroupPath))
            {
                return false;
            }

            string content = File.ReadAllText(cgroupPath);
            return content.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("containerd", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ControlPanel] [ORCHESTRATOR] Linux cgroup read notice for pid={pid}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedOrDetached, 1) != 0)
        {
            return;
        }

        Console.WriteLine("[ControlPanel] [ORCHESTRATOR] Dispose invoked — terminating tracked session processes and cleaning up monitors.");
        for (int pass = 1; pass <= 2; pass++)
        {
            // 1. Terminate directly tracked session processes
            foreach (Process process in _processes.Values)
                if (!SafeHasExited(process))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                        // Cleanup
                    }

                    if (pass == 2) process.Dispose();
                }

            if (pass == 2) _processes.Clear();
        }

        CleanSessionPids();

        try
        {
            _logTailerCts?.Cancel();
            _logTailerCts?.Dispose();
            _logTailerCts = null;
        }
        catch (Exception tailerEx)
        {
            Console.WriteLine($"[ControlPanel] [ORCHESTRATOR] Non-fatal log tailer dispose notice: {tailerEx.Message}");
        }
        _healthMonitorCts.Cancel();
        _healthMonitorCts.Dispose();
        _standalone?.Dispose();
    }

    public void AppendLog(LogMessage log)
    {
        string normalized = $"{log.ServiceName}|{log.Level}|{log.Message.Trim()}";
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        if (_recentLogHashes.Count > 600)
        {
            DateTimeOffset cutoff = now.AddSeconds(-3);
            foreach (KeyValuePair<string, DateTimeOffset> kvp in _recentLogHashes)
            {
                if (kvp.Value < cutoff)
                {
                    _recentLogHashes.TryRemove(kvp.Key, out _);
                }
            }
        }

        if (_recentLogHashes.TryGetValue(normalized, out DateTimeOffset lastSeen) && (now - lastSeen).TotalMilliseconds < 1500)
        {
            return;
        }

        _recentLogHashes[normalized] = now;

        lock (_logLock)
        {
            if (_logs.Count > 3200)
            {
                int toRemove = _logs.Count - 3000;
                // Pass 1: Prune older non-error logs first so critical errors are preserved for diagnostics
                for (int i = 0; i < _logs.Count && toRemove > 0;)
                {
                    if (!_logs[i].IsError &&
                        !string.Equals(_logs[i].Level, "ERROR", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(_logs[i].Level, "FATAL", StringComparison.OrdinalIgnoreCase))
                    {
                        _logs.RemoveAt(i);
                        toRemove--;
                    }
                    else
                    {
                        i++;
                    }
                }

                // Pass 2: Fallback if all logs are errors
                while (_logs.Count > 3000 && toRemove > 0)
                {
                    _logs.RemoveAt(0);
                    toRemove--;
                }
            }

            _logs.Add(log);
        }

        OnLogReceived?.Invoke(log);

        if (log.IsError || string.Equals(log.Level, "ERROR", StringComparison.OrdinalIgnoreCase))
            if (CurrentClusterState is not ServerState.Stopping and not ServerState.Stopped)
            {
                Kongor3DViewportControl.RequestKnockout();
                SoundEffectService.PlayErrorAlert();
            }
    }

    public void SetServiceState(string serviceKey, ServerState state)
    {
        _states[serviceKey] = state;
        OnServiceStateChanged?.Invoke(serviceKey, state);
    }

    public void SetAllMicroservicesState(ServerState state)
    {
        _clusterState = state;
        string[] services = ["master-server", "chat-server", "web-portal-api", "web-portal-ui"];
        foreach (string service in services) SetServiceState(service, state);
    }

    public static bool IsInteractiveGameClientProcess(Process proc)
    {
        try
        {
            string title = proc.MainWindowTitle;
            if (!string.IsNullOrEmpty(title) &&
                title.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Console", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Server", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Dedicated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        try
        {
            string? modulePath = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(modulePath) &&
                (modulePath.Contains(@"\games\client\", StringComparison.OrdinalIgnoreCase) ||
                 modulePath.Contains("/games/client/", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static void TerminateClusterProcessesFast()
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            Console.WriteLine("[NexusStandalone] skipped name-based process sweep in bundled mode");
            return;
        }
        TerminateClusterProcessesFastCore();
    }

    private static void TerminateClusterProcessesFastCore()
    {
        if (IsTestEnvironment()) return;

        int currentPid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                string pwshKill = $"Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {{ ($_.CommandLine -match 'scratch[\\\\/]run_|run_master_server|run_chat_server') -and $_.ProcessId -ne {currentPid} }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}";
                using Process p = new();
                p.StartInfo = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{pwshKill}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                p.WaitForExit(3000);
            }
            catch { }
        }

        foreach (string procName in ClusterProcessNames)
            try
            {
                Process[] processes = Process.GetProcessesByName(procName);
                foreach (Process proc in processes)
                    try
                    {
                        if (proc.Id == currentPid) continue;
                        if ((procName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                             procName.Equals("hon", StringComparison.OrdinalIgnoreCase)) &&
                            IsInteractiveGameClientProcess(proc))
                        {
                            continue;
                        }
                        proc.Kill(true);
                    }
                    catch
                    {
                        // Process already exited
                    }
                    finally
                    {
                        proc.Dispose();
                    }
            }
            catch
            {
                // Ignore process query issues
            }

        // When not running in an automated test host, terminate orphan dotnet host processes spawned by the cluster
        if (!IsTestEnvironment())
        {
            try
            {
                // Global dotnet.exe sweep disabled: it previously killed unrelated dotnet publish/test processes (e.g. launch_docker_stack.ps1 sync).
                Process[] dotnetProcs = [];
                foreach (Process dp in dotnetProcs)
                {
                    if (dp.Id == currentPid) continue;
                    try
                    {
                        dp.Kill(true);
                    }
                    catch
                    {
                        // Already exited or inaccessible
                    }
                    finally
                    {
                        dp.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore process query issues
            }
        }
    }

    private static void TerminateProcessByServiceName(string serviceKey)
    {
        if (BundledLayoutResolver.TryResolve(out _))
        {
            Console.WriteLine("[NexusStandalone] skipped name-based process sweep in bundled mode");
            return;
        }
        TerminateProcessByServiceNameCore(serviceKey);
    }

    private static void TerminateProcessByServiceNameCore(string serviceKey)
    {
        if (IsTestEnvironment()) return;

        string[] targetProcessNames = serviceKey switch
        {
            "master-server" => ["KONGOR.MasterServer"],
            "chat-server" => ["TRANSMUTANSTEIN.ChatServer"],
            "web-portal-api" => ["ZORGATH.WebPortal.API"],
            "web-portal-ui" => ["DAWNBRINGER.WebPortal.UI"],
            "game-servers" or "compel" => ["COMPEL", "hon_x64"],
            _ => []
        };

        foreach (string targetName in targetProcessNames)
            try
            {
                Process[] matching = Process.GetProcessesByName(targetName);
                foreach (Process proc in matching)
                    try
                    {
                        if (targetName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) &&
                            IsInteractiveGameClientProcess(proc))
                        {
                            continue;
                        }
                        proc.Kill(true);
                    }
                    catch
                    {
                        // Process already exited
                    }
                    finally
                    {
                        proc.Dispose();
                    }
            }
            catch
            {
                // Ignore process query issues
            }

        // Also terminate any dotnet host or supervisor script running this service on Windows
        if (OperatingSystem.IsWindows())
        {
            string dllMatch = serviceKey switch
            {
                "master-server" => "KONGOR.MasterServer|run_master_server",
                "chat-server" => "TRANSMUTANSTEIN.ChatServer|run_chat_server",
                "web-portal-api" => "ZORGATH.WebPortal.API",
                "web-portal-ui" => "DAWNBRINGER.WebPortal.UI",
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(dllMatch))
            {
                try
                {
                    string pwshKill = $"Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {{ ($_.CommandLine -match '{dllMatch}') -and $_.ProcessId -ne {Environment.ProcessId} }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}";
                    using Process p = new();
                    p.StartInfo = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{pwshKill}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    p.Start();
                    p.WaitForExit(3000);
                }
                catch { }
            }
        }
    }

    public static string GetProjectRelativeDir(string root, string projectName)
    {
        string directDir = Path.Combine(root, projectName);
        string directProj = Path.Combine(directDir, $"{projectName}.csproj");
        string directBin = Path.Combine(directDir, "bin");
        if (Directory.Exists(directBin) || File.Exists(directProj)) return projectName;

        string relative = projectName switch
        {
            "ASPIRE.ApplicationHost" => Path.Combine("src", "services", "aspire-host"),
            "KONGOR.MasterServer" => Path.Combine("src", "services", "master-server"),
            "TRANSMUTANSTEIN.ChatServer" => Path.Combine("src", "services", "chat-server"),
            "ZORGATH.WebPortal.API" => Path.Combine("src", "services", "web-api"),
            "DAWNBRINGER.WebPortal.UI" => Path.Combine("src", "services", "web-ui"),
            "ReplayRenderer.Service" => Path.Combine("src", "extensions", "replay-renderer"),
            "COMPEL" => Path.Combine("src", "apps", "compel"),
            "WILLOWMAKER" => Path.Combine("src", "apps", "willowmaker"),
            "NEXUS.ControlPanel.GUI" => Path.Combine("src", "apps", "control-panel"),
            _ => projectName
        };

        if (File.Exists(Path.Combine(root, relative, $"{projectName}.csproj")) ||
            Directory.Exists(Path.Combine(root, relative))) return relative;

        // Dynamic fallback across src directories
        string[] candidateSubfolders =
        [
            Path.Combine("src", "services"),
            Path.Combine("src", "apps"),
            Path.Combine("src", "extensions"),
            Path.Combine("src", "libs"),
            Path.Combine("src", "tools")
        ];

        foreach (string sub in candidateSubfolders)
        {
            string candidateDir = Path.Combine(root, sub);
            if (Directory.Exists(candidateDir))
                foreach (string dir in Directory.GetDirectories(candidateDir))
                    if (File.Exists(Path.Combine(dir, $"{projectName}.csproj")))
                        return Path.GetRelativePath(root, dir);
        }

        return relative;
    }

    private static bool IsValidDotNetBinary(string binaryPath, string projectName)
    {
        if (!File.Exists(binaryPath)) return false;
        string dir = Path.GetDirectoryName(binaryPath) ?? string.Empty;
        return File.Exists(Path.Combine(dir, $"{projectName}.runtimeconfig.json"));
    }

    private static void FindBestBinary(string projectWorkingDir, string projectName, out string? exePath, out string? dllPath)
    {
        exePath = null;
        dllPath = null;

        string[] frameworks = ["net11.0", "net10.0"];
        string[] configs = ["Release", "Debug"];

        List<(string? Exe, string? Dll, DateTime LastWriteUtc, int Priority)> candidates = [];
        int priority = 0;

        foreach (string config in configs)
        {
            foreach (string tfm in frameworks)
            {
                string candidateExe = Path.Combine(projectWorkingDir, "bin", config, tfm, $"{projectName}.exe");
                string candidateDll = Path.Combine(projectWorkingDir, "bin", config, tfm, $"{projectName}.dll");

                bool hasExe = IsValidDotNetBinary(candidateExe, projectName);
                bool hasDll = IsValidDotNetBinary(candidateDll, projectName);
                if (hasExe || hasDll)
                {
                    DateTime exeStamp = hasExe ? File.GetLastWriteTimeUtc(candidateExe) : DateTime.MinValue;
                    DateTime dllStamp = hasDll ? File.GetLastWriteTimeUtc(candidateDll) : DateTime.MinValue;
                    DateTime newestStamp = exeStamp >= dllStamp ? exeStamp : dllStamp;
                    if (projectName.Equals("ASPIRE.ApplicationHost", StringComparison.OrdinalIgnoreCase))
                    {
                        string servicesRoot = Path.GetFullPath(Path.Combine(projectWorkingDir, ".."));
                        string chatDll = Path.Combine(servicesRoot, "chat-server", "bin", config, tfm, "TRANSMUTANSTEIN.ChatServer.dll");
                        string masterDll = Path.Combine(servicesRoot, "master-server", "bin", config, tfm, "KONGOR.MasterServer.dll");
                        string webUiDll = Path.Combine(servicesRoot, "web-ui", "bin", config, tfm, "DAWNBRINGER.WebPortal.UI.dll");
                        string webApiDll = Path.Combine(servicesRoot, "web-api", "bin", config, tfm, "ZORGATH.WebPortal.API.dll");
                        if (File.Exists(chatDll) && File.GetLastWriteTimeUtc(chatDll) > newestStamp)
                            newestStamp = File.GetLastWriteTimeUtc(chatDll);
                        if (File.Exists(masterDll) && File.GetLastWriteTimeUtc(masterDll) > newestStamp)
                            newestStamp = File.GetLastWriteTimeUtc(masterDll);
                        if (File.Exists(webUiDll) && File.GetLastWriteTimeUtc(webUiDll) > newestStamp)
                            newestStamp = File.GetLastWriteTimeUtc(webUiDll);
                        if (File.Exists(webApiDll) && File.GetLastWriteTimeUtc(webApiDll) > newestStamp)
                            newestStamp = File.GetLastWriteTimeUtc(webApiDll);
                    }
                    candidates.Add((hasExe ? candidateExe : null, hasExe ? null : candidateDll, newestStamp, priority));
                }

                priority++;
            }
        }

        if (candidates.Count > 0)
        {
            var winner = candidates
                .OrderByDescending(c => c.LastWriteUtc)
                .ThenBy(c => c.Priority)
                .First();
            exePath = winner.Exe;
            dllPath = winner.Dll;
            return;
        }

        // Fallback: search any subfolder in bin/
        string binDir = Path.Combine(projectWorkingDir, "bin");
        if (Directory.Exists(binDir))
        {
            string[] exes = Directory.GetFiles(binDir, $"{projectName}.exe", SearchOption.AllDirectories).Where(e => IsValidDotNetBinary(e, projectName)).ToArray();
            if (exes.Length > 0)
            {
                exePath = exes.OrderByDescending(File.GetLastWriteTimeUtc).First();
                return;
            }

            string[] dlls = Directory.GetFiles(binDir, $"{projectName}.dll", SearchOption.AllDirectories).Where(d => IsValidDotNetBinary(d, projectName)).ToArray();
            if (dlls.Length > 0)
            {
                dllPath = dlls.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
        }
    }

    public static ProcessStartInfo CreateFastProcessStartInfo(string root, string projectName, string launchProfile,
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
            // Fallback to dotnet run if no compiled binaries are found
            string runArgs = BuildFastRunArguments(root, projectName, launchProfile, extraArgs);
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
        ApplyLaunchProfileEnvVars(psi, projectName);

        return psi;
    }

    private static void ApplyLaunchProfileEnvVars(ProcessStartInfo psi, string projectName)
    {
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables["DOTNET_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:5551";
        psi.EnvironmentVariables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";
        psi.EnvironmentVariables["DOTNET_ASPIRE_SHOW_CHILD_CONSOLE_OUTPUT"] = "true";
        psi.EnvironmentVariables["ASPIRE_SHOW_CHILD_CONSOLE_OUTPUT"] = "true";
        psi.EnvironmentVariables["DCP_SHOW_CHILD_CONSOLE_OUTPUT"] = "true";

        if (projectName.Equals("ASPIRE.ApplicationHost", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["ASPNETCORE_URLS"] = "https://localhost:5550";
            psi.EnvironmentVariables["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:5551";
            psi.EnvironmentVariables["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"] = "http://localhost:5552";
            psi.EnvironmentVariables["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
            psi.EnvironmentVariables["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        }
        else if (projectName.Equals("DAWNBRINGER.WebPortal.UI", StringComparison.OrdinalIgnoreCase))
        {
            psi.EnvironmentVariables["services__web-portal-api__http__0"] = "http://localhost:5200";
            psi.EnvironmentVariables["services__web-portal-api__https__0"] = "https://localhost:5201";
        }
    }

    private static string BuildFastRunArguments(string root, string projectName, string launchProfile,
        string extraArgs = "")
    {
        string relativeDir = GetProjectRelativeDir(root, projectName);
        string projectDir = Path.Combine(root, relativeDir);
        string projPath = Path.Combine(projectDir, $"{projectName}.csproj");

        FindBestBinary(projectDir, projectName, out _, out string? dllPath);
        string noBuild = dllPath != null ? "--no-build " : string.Empty;
        string appArgs = string.IsNullOrWhiteSpace(extraArgs) ? string.Empty : $" -- {extraArgs.Trim()}";
        return $"run {noBuild}--project \"{projPath}\" --launch-profile \"{launchProfile}\"{appArgs}";
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
        string exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrWhiteSpace(dotnetRoot) || !Directory.Exists(dotnetRoot) || !File.Exists(Path.Combine(dotnetRoot, exeName)))
        {
            List<string> candidates = [];
            if (OperatingSystem.IsWindows())
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localApp))
                {
                    candidates.Add(Path.Combine(localApp, "Microsoft", "dotnet"));
                }
                string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(progFiles))
                {
                    candidates.Add(Path.Combine(progFiles, "dotnet"));
                }
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    candidates.Add(Path.Combine(home, ".dotnet"));
                }
                candidates.Add("/usr/share/dotnet");
                candidates.Add("/usr/lib/dotnet");
                candidates.Add("/usr/local/share/dotnet");
                candidates.Add("/opt/homebrew/opt/dotnet/libexec");
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, exeName)))
                {
                    dotnetRoot = candidate;
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            psi.Environment["DOTNET_ROOT"] = dotnetRoot;
            string currentPath = psi.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!currentPath.StartsWith(dotnetRoot, StringComparison.OrdinalIgnoreCase))
            {
                psi.EnvironmentVariables["PATH"] = $"{dotnetRoot}{Path.PathSeparator}{currentPath}";
            }
        }
        psi.EnvironmentVariables["DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
    }

    private void StartClusterHealthMonitor(CancellationToken cancellationToken)
    {
        if (IsTestEnvironment()) return;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
                try
                {
                    IPGlobalProperties ipProps = IPGlobalProperties.GetIPGlobalProperties();
                    IPEndPoint[] listeners = ipProps.GetActiveTcpListeners();
                    HashSet<int> activePorts = [.. listeners.Select(l => l.Port)];

                    bool isMasterRunning = activePorts.Contains(55555);
                    bool isChatRunning = activePorts.Contains(11031) || activePorts.Contains(11032) ||
                                         activePorts.Contains(5554);
                    bool isApiRunning = activePorts.Contains(5200) || activePorts.Contains(5201);
                    bool isUiRunning = activePorts.Contains(5557) || activePorts.Contains(5266);
                    bool isDashboardRunning = activePorts.Contains(5550);

                    if (isMasterRunning)
                    {
                        if (GetServiceState("master-server") != ServerState.Running)
                            SetServiceState("master-server", ServerState.Running);
                    }
                    else if (CurrentClusterState is not ServerState.Starting)
                    {
                        if (ShouldMarkMonitoredServiceStopped("master-server"))
                            SetServiceState("master-server", ServerState.Stopped);
                    }

                    if (isChatRunning)
                    {
                        if (GetServiceState("chat-server") != ServerState.Running)
                            SetServiceState("chat-server", ServerState.Running);
                    }
                    else if (CurrentClusterState is not ServerState.Starting)
                    {
                        if (ShouldMarkMonitoredServiceStopped("chat-server"))
                            SetServiceState("chat-server", ServerState.Stopped);
                    }

                    if (isApiRunning)
                    {
                        if (GetServiceState("web-portal-api") != ServerState.Running)
                            SetServiceState("web-portal-api", ServerState.Running);
                    }
                    else if (CurrentClusterState is not ServerState.Starting)
                    {
                        if (ShouldMarkMonitoredServiceStopped("web-portal-api"))
                            SetServiceState("web-portal-api", ServerState.Stopped);
                    }

                    if (isUiRunning)
                    {
                        if (GetServiceState("web-portal-ui") != ServerState.Running)
                            SetServiceState("web-portal-ui", ServerState.Running);
                    }
                    else if (CurrentClusterState is not ServerState.Starting)
                    {
                        if (ShouldMarkMonitoredServiceStopped("web-portal-ui"))
                            SetServiceState("web-portal-ui", ServerState.Stopped);
                    }

                    bool isAnyServiceRunning = isDashboardRunning || isMasterRunning || isChatRunning || isApiRunning || isUiRunning;
                    bool isClusterSufficientlyRunning = isDashboardRunning || (isMasterRunning && isChatRunning);
                    bool isClusterProcAlive = (_processes.TryGetValue("cluster", out Process? clusterProc) && !clusterProc.HasExited) || (_standalone is { IsActive: true }) || _isDockerStackOperationActive;

                    CurrentClusterState = EvaluateClusterState(CurrentClusterState, isClusterSufficientlyRunning, isAnyServiceRunning, isClusterProcAlive);

                    int pollDelayMs = CurrentClusterState == ServerState.Starting ? 500 : 1000;
                    await Task.Delay(pollDelayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(1000, cancellationToken);
                }
        }, cancellationToken);
    }

    public static ServerState EvaluateClusterState(
        ServerState currentState,
        bool isClusterSufficientlyRunning,
        bool isAnyServiceRunning,
        bool isClusterProcAlive)
    {
        if (isClusterSufficientlyRunning)
        {
            if (currentState is ServerState.Starting or ServerState.Stopped)
                return ServerState.Running;
            return currentState;
        }

        if (currentState == ServerState.Running)
        {
            if (!isAnyServiceRunning && !isClusterProcAlive)
            {
                return ServerState.Stopped;
            }
            return currentState;
        }

        if (currentState == ServerState.Starting)
        {
            // Cluster is in startup phase - keep in Starting state unless host process has actually exited
            if (!isClusterProcAlive)
            {
                return ServerState.Stopped;
            }
            return currentState;
        }

        return currentState;
    }

    private void StartContinuousLogTailers(CancellationToken cancellationToken)
    {
        if (IsTestEnvironment()) return;

        Task.Run(async () =>
        {
            string solutionRoot = FindSolutionRoot();

            List<(string DirectoryPath, string ServiceTag, string SearchPattern)> watchers =
            [
                (Path.Combine(solutionRoot, "src", "services", "master-server", "logs"), "KONGOR", "*.log"),
                (Path.Combine(solutionRoot, "src", "services", "chat-server", "logs"), "CHAT SERVER", "*.log"),
                (Path.Combine(solutionRoot, "src", "services", "web-api", "logs"), "PORTAL API", "*.log"),
                (Path.Combine(solutionRoot, "src", "services", "web-ui", "logs"), "PORTAL UI", "*.log"),
                (Path.Combine(solutionRoot, "games", "client"), "GAME CLIENT", "WILLOWMAKER.log"),
                (Path.Combine(solutionRoot, "games", "client", "userdata", "game", "logs"), "GAME CLIENT", "*.log"),
                (Path.Combine(solutionRoot, "games", "client", "userdata", "logs"), "GAME CLIENT", "*.log"),
                (Path.Combine(solutionRoot, "games", "client", "userdata", "game"), "GAME CLIENT", "console.log"),
                (Path.Combine(solutionRoot, "games", "server"), "GAME SERVER", "COMPEL.log"),
                (Path.Combine(solutionRoot, "games", "server", "userdata", "game", "logs"), "GAME SERVER", "*.log"),
                (Path.Combine(solutionRoot, "games", "server", "userdata", "logs"), "GAME SERVER", "*.log"),
                (Path.Combine(solutionRoot, "games", "server", "userdata", "game"), "GAME SERVER", "console.log"),
                (Path.Combine(solutionRoot, "userdata", "logs"), "GAME CLIENT", "*.log"),
                (Path.Combine(solutionRoot, "logs"), "ASPIRE HOST", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "master-server"), "KONGOR", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "chat-server"), "CHAT SERVER", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "web-api"), "PORTAL API", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "web-ui"), "PORTAL UI", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "compel-dedicated"), "GAME SERVER", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "compel-dedicated", "game", "logs"), "GAME SERVER", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "compel-queue-arcade"), "QUEUE ARCADE", "*.log"),
                (Path.Combine(solutionRoot, "logs", "docker", "compel-queue-arcade", "game", "logs"), "QUEUE ARCADE", "*.log")
            ];

            if (OperatingSystem.IsWindows())
            {
                string myDocs = BundledLayoutResolver.TryResolve(out _) ? string.Empty : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(myDocs))
                {
                    watchers.Add((Path.Combine(myDocs, "Heroes of Newerth x64", "game", "logs", "manager"), "GAME SERVER", "*.log"));
                    watchers.Add((Path.Combine(myDocs, "Heroes of Newerth x64", "game", "logs"), "GAME SERVER", "*.log"));
                    watchers.Add((Path.Combine(myDocs, "Heroes of Newerth x64", "game", "logs"), "GAME SERVER", "*.clog"));
                }

                string userProfile = BundledLayoutResolver.TryResolve(out _) ? string.Empty : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    watchers.Add((Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "game", "logs", "manager"), "GAME SERVER", "*.log"));
                    watchers.Add((Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "game", "logs"), "GAME SERVER", "*.log"));
                    watchers.Add((Path.Combine(userProfile, "OneDrive", "Documents", "Heroes of Newerth x64", "game", "logs"), "GAME SERVER", "*.clog"));
                }
            }

            bool initialPass = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool hadNewLogs = false;

                    foreach ((string fullDir, string defaultServiceTag, string pattern) in watchers)
                    {
                        if (!Directory.Exists(fullDir)) continue;

                        string[] files = Directory.GetFiles(fullDir, pattern, SearchOption.TopDirectoryOnly);
                        foreach (string file in files)
                        {
                            try
                            {
                                FileInfo fi = new(file);
                                if (!fi.Exists) continue;

                                long fileLen = fi.Length;

                                if (initialPass)
                                {
                                    // Start tailing from the end of existing log files on application startup
                                    _fileTailerPositions[file] = fileLen;
                                    continue;
                                }

                                long prevPos = _fileTailerPositions.GetOrAdd(file, _ => fileLen);

                                if (fileLen > prevPos)
                                {
                                    hadNewLogs = true;
                                    using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                    fs.Seek(prevPos, SeekOrigin.Begin);
                                    using StreamReader reader = new(fs);

                                    string? line;
                                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                                    {
                                        if (string.IsNullOrWhiteSpace(line)) continue;

                                        string serviceName = DetectServiceName(line);
                                        if (serviceName == "ASPIRE HOST" && defaultServiceTag != "ASPIRE HOST")
                                        {
                                            serviceName = defaultServiceTag;
                                        }

                                        string cleanText = SimdAnsiStripper.StripAnsi(line);
                                        string fileStreamKey = $"file:{file}";
                                        _streamLastStructuredLevels.TryGetValue(fileStreamKey, out string? lastStructuredLevel);
                                        string level = ParseLogLevel(cleanText, ref lastStructuredLevel);
                                        _streamLastStructuredLevels[fileStreamKey] = lastStructuredLevel;

                                        AppendLog(new LogMessage
                                        {
                                            Timestamp = TimeProvider.System.GetUtcNow(),
                                            ServiceName = serviceName,
                                            Message = line,
                                            Level = level,
                                            IsError = level is "ERROR" or "FATAL"
                                        });
                                    }

                                    _fileTailerPositions[file] = fs.Position;
                                }
                                else if (fileLen < prevPos)
                                {
                                    _fileTailerPositions[file] = 0;
                                }
                            }
                            catch
                            {
                                // File locked temporarily, retry next cycle
                            }
                        }
                    }

                    initialPass = false;

                    int delayMs = hadNewLogs ? 250 : 750;
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }, cancellationToken);
    }

    private void RunStartupLogHygiene()
    {
        if (IsTestEnvironment()) return;

        Task.Run(() =>
        {
            try
            {
                string solutionRoot = FindSolutionRoot();
                DateTimeOffset cutoff = TimeProvider.System.GetUtcNow().AddDays(-7);
                const long maxLogSizeBytes = 10 * 1024 * 1024; // 10 MB

                string[] candidateDirs =
                [
                    Path.Combine(solutionRoot, "logs"),
                    Path.Combine(solutionRoot, "src", "services", "master-server", "logs"),
                    Path.Combine(solutionRoot, "src", "services", "chat-server", "logs"),
                    Path.Combine(solutionRoot, "src", "services", "web-api", "logs"),
                    Path.Combine(solutionRoot, "src", "services", "web-ui", "logs"),
                    Path.Combine(solutionRoot, "games", "client", "userdata", "game", "logs"),
                    Path.Combine(solutionRoot, "games", "client", "userdata", "logs"),
                    Path.Combine(solutionRoot, "games", "server", "userdata", "game", "logs"),
                    Path.Combine(solutionRoot, "games", "server", "userdata", "logs"),
                    Path.Combine(solutionRoot, "games", "server")
                ];

                foreach (string dir in candidateDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    string[] logFiles = Directory.GetFiles(dir, "*.log", SearchOption.TopDirectoryOnly);
                    foreach (string log in logFiles)
                    {
                        try
                        {
                            FileInfo fi = new(log);
                            if (!fi.Exists) continue;

                            // 1. Delete stale logs older than 7 days
                            if (fi.LastWriteTimeUtc < cutoff.UtcDateTime)
                            {
                                fi.Delete();
                                continue;
                            }

                            // 2. Truncate oversized logs (>10MB)
                            if (fi.Length > maxLogSizeBytes)
                            {
                                using FileStream fs = new(log, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
                            }
                        }
                        catch
                        {
                            // Skip locked files
                        }
                    }

                    // Also clean up stale crash dumps
                    string[] dmpFiles = Directory.GetFiles(dir, "*.dmp", SearchOption.TopDirectoryOnly);
                    foreach (string dmp in dmpFiles)
                    {
                        try
                        {
                            File.Delete(dmp);
                        }
                        catch
                        {
                            // Skip locked files
                        }
                    }
                }
            }
            catch
            {
                // Hygiene failure should never prevent UI startup
            }
        });
    }

    private async Task<ProcessStartInfo?> FindGameServerStartInfoAsync(string root,
        CancellationToken cancellationToken = default)
    {
        string? customPath = null;
        if (secretsManager != null)
            try
            {
                NexusSecrets secrets = await secretsManager.LoadSecretsAsync(cancellationToken);
                customPath = secrets.CompelPath;
            }
            catch
            {
                // Fallback to environment
            }

        if (string.IsNullOrWhiteSpace(customPath)) customPath = Environment.GetEnvironmentVariable("COMPEL_PATH");

        string foundExe = string.Empty;
        string workDir = string.Empty;

        // 1. Direct configured path resolution from Secrets Vault
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (File.Exists(customPath))
            {
                foundExe = customPath;
                workDir = Path.GetDirectoryName(customPath) ?? string.Empty;
            }
            else if (Directory.Exists(customPath))
            {
                string[] serverCandidates = ["COMPEL.exe", "hon_x64.exe", "hon.exe", "COMPEL", "hon_x64", "hon"];
                foreach (string candidate in serverCandidates)
                {
                    string full = Path.Combine(customPath, candidate);
                    if (File.Exists(full))
                    {
                        foundExe = full;
                        workDir = customPath;
                        break;
                    }
                }
            }
        }

        // 2. Relative workspace fallback only if not configured or not found
        if (string.IsNullOrEmpty(foundExe) || !File.Exists(foundExe))
        {
            string parentDir = Path.GetFullPath(Path.Combine(root, "..", "..", "..", ".."));
            string grandParentDir = Path.GetFullPath(Path.Combine(parentDir, ".."));

            string solutionRoot = FindSolutionRoot();
            string[] fallbackFolders =
            [
                Path.Combine(solutionRoot, "games", "server"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net11.0"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net11.0"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Debug", "net10.0"),
                Path.Combine(solutionRoot, "src", "apps", "compel", "bin", "Release", "net10.0"),
                Path.Combine(solutionRoot, "games", "shared"),
                Path.Combine(solutionRoot, "src", "apps", "compel")
            ];

            foreach (string folder in fallbackFolders)
                if (Directory.Exists(folder))
                {
                    string[] serverCandidates = ["COMPEL.exe", "hon_x64.exe", "hon.exe", "COMPEL", "hon_x64", "hon"];
                    foreach (string candidate in serverCandidates)
                    {
                        string full = Path.Combine(folder, candidate);
                        if (File.Exists(full))
                        {
                            foundExe = full;
                            workDir = folder;
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(foundExe)) break;
                }
        }

        if (string.IsNullOrEmpty(foundExe) || !File.Exists(foundExe))
        {
            AppendLog(new LogMessage
            {
                ServiceName = "GAME SERVER",
                Message =
                    $"COMPEL.exe not found in configured path: {customPath}. Set COMPEL_PATH in Secrets Vault.",
                Level = "WARN",
                IsError = false
            });
            return null;
        }

        return new ProcessStartInfo
        {
            FileName = foundExe,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private void StartFileLogTailer(string logFilePath, string serviceTag, CancellationToken cancellationToken)
    {
        StartFileLogTailer([logFilePath], serviceTag, cancellationToken);
    }

    private void StartFileLogTailer(string[] candidatePaths, string serviceTag, CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            try
            {
                string? matchedPath = candidatePaths.FirstOrDefault(File.Exists);
                int retries = 0;
                while (matchedPath is null && retries < 40 && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken);
                    matchedPath = candidatePaths.FirstOrDefault(File.Exists);
                    retries++;
                }

                if (matchedPath is null || !File.Exists(matchedPath)) return;

                await using FileStream fs = new(matchedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(fs);
                fs.Seek(0, SeekOrigin.End);
                string? lastStructuredLevel = null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            string cleanLine = SimdAnsiStripper.StripAnsi(line);
                            string lineLevel = ParseLogLevel(cleanLine, ref lastStructuredLevel);
                            AppendLog(new LogMessage
                            {
                                Timestamp = TimeProvider.System.GetUtcNow(),
                                ServiceName = serviceTag,
                                Message = line,
                                Level = lineLevel,
                                IsError = lineLevel is "ERROR" or "FATAL"
                            });
                        }
                    }
                    else
                    {
                        await Task.Delay(200, cancellationToken);
                    }
                }
            }
            catch
            {
                // Background stream ended gracefully
            }
        }, cancellationToken);
    }

    private void StartAspireLogTailer(string logFilePath, CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            try
            {
                int retries = 0;
                while (!File.Exists(logFilePath) && retries < 40 && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken);
                    retries++;
                }

                if (!File.Exists(logFilePath)) return;

                await using FileStream fs = new(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(fs);

                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) HandleOutputLine(line, false, "aspire-log");
                    }
                    else
                    {
                        await Task.Delay(200, cancellationToken);
                    }
                }
            }
            catch
            {
                // Background stream ended gracefully
            }
        }, cancellationToken);
    }

    private void HandleOutputLine(string text, bool isError, string streamKey = "aspire-stdout")
    {
        string cleanText = SimdAnsiStripper.StripAnsi(text);
        string level;
        if (isError)
        {
            level = "ERROR";
        }
        else
        {
            _streamLastStructuredLevels.TryGetValue(streamKey, out string? lastStructuredLevel);
            level = ParseLogLevel(cleanText, ref lastStructuredLevel);
            _streamLastStructuredLevels[streamKey] = lastStructuredLevel;
        }

        if (cleanText.Contains("login?t=", StringComparison.OrdinalIgnoreCase) ||
            cleanText.Contains("Now listening on: https://localhost:5550", StringComparison.OrdinalIgnoreCase) ||
            cleanText.Contains("Dashboard running at", StringComparison.OrdinalIgnoreCase) ||
            cleanText.Contains("Dashboard:", StringComparison.OrdinalIgnoreCase))
        {
            CurrentClusterState = ServerState.Running;
            Match match = DashboardUrlRegex().Match(cleanText);
            if (match.Success) AspireDashboardUrl = match.Value;
        }

        if (!_isTailingLogs && cleanText.Contains("Logs:", StringComparison.OrdinalIgnoreCase))
        {
            Match logMatch = AspireLogsRegex().Match(cleanText);
            if (logMatch.Success && _logTailerCts is not null)
            {
                _isTailingLogs = true;
                string logFile = logMatch.Groups[1].Value.Trim();
                StartAspireLogTailer(logFile, _logTailerCts.Token);
            }
        }

        if (text.Contains("changed state:", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("Running", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("master-server", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("master-server", ServerState.Running);

                if (text.Contains("chat-server", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("chat-server", ServerState.Running);

                if (text.Contains("web-portal-api", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("web-portal-api", ServerState.Running);

                if (text.Contains("web-portal-ui", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("web-portal-ui", ServerState.Running);

                if (text.Contains("database", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("database", ServerState.Running);

                if (text.Contains("distributed-cache", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("redis", ServerState.Running);
            }
            else if (text.Contains("Finished", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("Exited", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("FailedToStart", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Contains("master-server", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("master-server", ServerState.Stopped);

                if (text.Contains("chat-server", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("chat-server", ServerState.Stopped);

                if (text.Contains("web-portal-api", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("web-portal-api", ServerState.Stopped);

                if (text.Contains("web-portal-ui", StringComparison.OrdinalIgnoreCase))
                    SetServiceState("web-portal-ui", ServerState.Stopped);
            }
        }
        else if (text.Contains("Now listening on:", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Application started.", StringComparison.OrdinalIgnoreCase))
        {
            string detected = DetectServiceName(text);
            if (detected == "KONGOR")
                SetServiceState("master-server", ServerState.Running);
            else if (detected == "CHAT SERVER")
                SetServiceState("chat-server", ServerState.Running);
            else if (detected == "PORTAL API")
                SetServiceState("web-portal-api", ServerState.Running);
            else if (detected == "PORTAL UI") SetServiceState("web-portal-ui", ServerState.Running);
        }

        if (text.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("The request was aborted", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Error while copying content to a stream", StringComparison.OrdinalIgnoreCase))
        {
            // Normal Aspire/DCP socket teardown on stop or restart
            level = "DEBUG";
            isError = false;
        }

        string serviceName = DetectServiceName(text);

        LogMessage msg = new()
        {
            Timestamp = TimeProvider.System.GetUtcNow(),
            ServiceName = serviceName,
            Message = text,
            Level = level,
            IsError = isError
        };

        AppendLog(msg);
    }

    public static string ParseLogLevel(string text)
    {
        string? ignored = null;
        return ParseLogLevel(text, ref ignored);
    }

    public static string ParseLogLevel(string text, ref string? lastStructuredLevel)
    {
        if (string.IsNullOrEmpty(text))
            return "INFO";

        // Honor structured Serilog / MEL level tokens ([FTL], [ERR], [WRN], [INF], [DBG], [VRB], warn:, fail:, etc.)
        // before substring matching, and record lastStructuredLevel so multi-line continuation lines
        // (exception headers like "SyntheticTimeoutException: ..." and "   at ..." stack frames) inherit
        // the parent structured entry's severity instead of escalating [WRN] continuations to [ERROR].
        if (text.Contains(" [FTL] ", StringComparison.Ordinal) ||
            text.StartsWith("[FTL] ", StringComparison.Ordinal) ||
            text.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("crit:", StringComparison.OrdinalIgnoreCase))
        {
            lastStructuredLevel = "FATAL";
            return "FATAL";
        }

        if (text.Contains(" [ERR] ", StringComparison.Ordinal) ||
            text.StartsWith("[ERR] ", StringComparison.Ordinal) ||
            text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("fail:", StringComparison.OrdinalIgnoreCase))
        {
            lastStructuredLevel = "ERROR";
            return "ERROR";
        }

        if (text.Contains(" [WRN] ", StringComparison.Ordinal) ||
            text.StartsWith("[WRN] ", StringComparison.Ordinal) ||
            text.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("warn:", StringComparison.OrdinalIgnoreCase))
        {
            lastStructuredLevel = "WARN";
            return "WARN";
        }

        if (text.Contains(" [INF] ", StringComparison.Ordinal) ||
            text.StartsWith("[INF] ", StringComparison.Ordinal) ||
            text.Contains("[INFO]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("info:", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                lastStructuredLevel = "FATAL";
                return "FATAL";
            }

            if (text.Contains("[DedicatedServer:", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("warning:", StringComparison.OrdinalIgnoreCase)))
            {
                lastStructuredLevel = "WARN";
                return "WARN";
            }

            lastStructuredLevel = "INFO";
            return "INFO";
        }

        if (text.Contains(" [DBG] ", StringComparison.Ordinal) ||
            text.StartsWith("[DBG] ", StringComparison.Ordinal) ||
            text.Contains(" [VRB] ", StringComparison.Ordinal) ||
            text.StartsWith("[VRB] ", StringComparison.Ordinal) ||
            text.Contains("[DBUG]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("dbug:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("trce:", StringComparison.OrdinalIgnoreCase))
        {
            lastStructuredLevel = "DEBUG";
            return "DEBUG";
        }

        // Unprefixed continuation line (stack frame, stack boundary, or "<Namespace.Type>Exception: ..." header)
        // immediately following a structured log line inherits that structured entry's severity.
        if (lastStructuredLevel is not null && IsExceptionContinuationLine(text))
        {
            return lastStructuredLevel;
        }

        lastStructuredLevel = null;

        if (text.Contains("fatal:", StringComparison.OrdinalIgnoreCase))
            return "FATAL";

        if (text.Contains("WARNING:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WARN:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("warn:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("warning:", StringComparison.OrdinalIgnoreCase))
            return "WARN";

        if (text.Contains("[PREDICTIVE_PATHING]", StringComparison.OrdinalIgnoreCase))
            return "INFO";

        if (text.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fail:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exception:", StringComparison.OrdinalIgnoreCase))
        {
            if (IsUnprefixedExceptionHeader(text))
            {
                lastStructuredLevel = "ERROR";
            }

            return "ERROR";
        }

        if (text.Contains("dbug:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("debug:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("trace:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DashboardClient", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CircuitHost", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("dcp-host", StringComparison.OrdinalIgnoreCase))
            return "DEBUG";

        return "INFO";
    }

    private static bool IsExceptionContinuationLine(string text)
    {
        if (text.StartsWith("   at ", StringComparison.Ordinal) ||
            text.StartsWith("\tat ", StringComparison.Ordinal) ||
            text.StartsWith("--- End of stack trace", StringComparison.Ordinal) ||
            text.StartsWith(" ---> ", StringComparison.Ordinal) ||
            text.StartsWith("   --- End of inner exception stack trace ---", StringComparison.Ordinal))
        {
            return true;
        }

        return IsUnprefixedExceptionHeader(text);
    }

    private static bool IsUnprefixedExceptionHeader(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> trimmed = text.TrimStart();
        if (trimmed.StartsWith("---> ", StringComparison.Ordinal))
        {
            trimmed = trimmed[5..].TrimStart();
        }

        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> headerBeforeColon = trimmed[..colonIdx];
        int parenIdx = headerBeforeColon.IndexOf('(');
        ReadOnlySpan<char> typeToken = (parenIdx > 0 ? headerBeforeColon[..parenIdx] : headerBeforeColon).TrimEnd();
        if (typeToken.IsEmpty || typeToken.IndexOfAny(" \t[]".AsSpan()) >= 0)
        {
            return false;
        }

        return typeToken.EndsWith("Exception", StringComparison.OrdinalIgnoreCase) ||
               typeToken.Contains("Exception`", StringComparison.OrdinalIgnoreCase);
    }

    public static string DetectServiceName(string text)
    {
        if (text.Contains("[DOCKER-STACK]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("docker-stack", StringComparison.OrdinalIgnoreCase))
            return "DOCKER STACK";

        if (text.Contains("master-server", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("KONGOR", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MasterServer", StringComparison.OrdinalIgnoreCase))
            return "KONGOR";

        if (text.Contains("chat-server", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TRANSMUTANSTEIN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ChatServer", StringComparison.OrdinalIgnoreCase))
            return "CHAT SERVER";

        if (text.Contains("web-portal-api", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ZORGATH", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WebPortal.API", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PlayerStatsApi", StringComparison.OrdinalIgnoreCase))
            return "PORTAL API";

        if (text.Contains("web-portal-ui", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DAWNBRINGER", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WebPortal.UI", StringComparison.OrdinalIgnoreCase))
            return "PORTAL UI";

        if (text.Contains("hon-game-servers", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("COMPEL", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GameServer", StringComparison.OrdinalIgnoreCase))
            return "GAME SERVER";

        if (text.Contains("WILLOWMAKER", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hon-client", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GameClient", StringComparison.OrdinalIgnoreCase))
            return "GAME CLIENT";

        if (text.Contains("database", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MERRICK", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sqlserver", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("postgres", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("npgsql", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("supabase", StringComparison.OrdinalIgnoreCase))
            return "DATABASE";

        if (text.Contains("distributed-cache", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("redis", StringComparison.OrdinalIgnoreCase))
            return "REDIS CACHE";

        return "ASPIRE HOST";
    }

    public static string GetDisplayServiceName(string key)
    {
        return key switch
        {
            "docker-stack" => "DOCKER STACK",
            "master-server" => "KONGOR",
            "chat-server" => "CHAT SERVER",
            "web-portal-api" => "PORTAL API",
            "web-portal-ui" => "PORTAL UI",
            "game-servers" => "GAME SERVER",
            "queue-arcade" => "QUEUE ARCADE",
            "pyroscope" => "PYROSCOPE",
            "redis" => "REDIS CACHE",
            _ => key.ToUpperInvariant()
        };
    }

    private static string FindSolutionRoot()
    {
        if (KongorWorkspaceResolver.TryResolve(out KongorWorkspaceResult? ws) && ws is not null)
        {
            return ws.Root;
        }
        if (BundledLayoutResolver.TryResolve(out BundledLayout? layout))
        {
            return layout.Root;
        }
        return FindSolutionRootCore();
    }

    private static string FindSolutionRootCore()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.slnx")) ||
                File.Exists(Path.Combine(current, "NEXUS.DistributedApplication.sln")))
                return current;

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;

            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    [GeneratedRegex(@"https?://[^\s""']+(?:login\?t=[a-zA-Z0-9_\-]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex DashboardUrlRegex();

    [GeneratedRegex(@"Logs:\s*""?([^""\r\n]+\.(?:log|txt))""?", RegexOptions.IgnoreCase)]
    private static partial Regex AspireLogsRegex();

    private static bool IsDockerAvailableFast()
    {
        if (IsTestEnvironment()) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                bool pipeExists = File.Exists(@"\\.\pipe\docker_engine") ||
                                  File.Exists(@"\\.\pipe\dockerDesktopLinuxEngine") ||
                                  File.Exists(@"\\.\pipe\dockerDesktopEngine");

                bool processExists = Process.GetProcessesByName("Docker Desktop").Length > 0 ||
                                    Process.GetProcessesByName("com.docker.backend").Length > 0 ||
                                    Process.GetProcessesByName("dockerd").Length > 0;

                return pipeExists && processExists;
            }

            return File.Exists("/var/run/docker.sock") ||
                   Process.GetProcessesByName("dockerd").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPortListeningFast(int port)
    {
        try
        {
            IPGlobalProperties ipProps = IPGlobalProperties.GetIPGlobalProperties();
            return ipProps.GetActiveTcpListeners().Any(l => l.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTestEnvironment()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTHOST_PID")) ||
               AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                   a.GetName().Name?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true ||
                   a.GetName().Name?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true);
    }

    public static async Task<bool> EnsureGarnetContainerAsync(string? repoRoot = null, CancellationToken cancellationToken = default)
    {
        if (IsPortListeningFast(28298)) return true;
        if (!IsDockerAvailableFast()) return false;

        string root = repoRoot ?? FindSolutionRoot();
        string composeFile = Path.Combine(root, "infra", "docker", "docker-compose.garnet.yml");

        if (File.Exists(composeFile))
        {
            string dockerCli = DockerHealthService.ResolveDockerCliPath();
            try
            {
                using Process proc = new();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = dockerCli,
                    Arguments = $"compose -f \"{composeFile}\" up -d",
                    WorkingDirectory = Path.GetDirectoryName(composeFile)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                proc.Start();
                await proc.WaitForExitAsync(cancellationToken);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public static async Task<bool> EnsurePyroscopeContainerAsync(string? repoRoot = null, CancellationToken cancellationToken = default)
    {
        if (IsPortListeningFast(4040)) return true;
        if (!IsDockerAvailableFast()) return false;

        string root = repoRoot ?? FindSolutionRoot();
        string composeFile = Path.Combine(root, "infra", "docker", "docker-compose.pyroscope.yml");

        if (File.Exists(composeFile))
        {
            string dockerCli = DockerHealthService.ResolveDockerCliPath();
            try
            {
                using Process proc = new();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = dockerCli,
                    Arguments = $"compose -f \"{composeFile}\" up -d",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                proc.Start();
                await proc.WaitForExitAsync(cancellationToken);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // Direct container management via Docker.DotNet with safe image pull pre-flight
        try
        {
            using var client = new DockerClientConfiguration().CreateClient();

            // Try starting existing container first
            try
            {
                bool started = await client.Containers.StartContainerAsync("kongor-pyroscope",
                    new ContainerStartParameters(), cancellationToken);
                if (started) return true;
            }
            catch (DockerContainerNotFoundException)
            {
                // Container does not exist yet; proceed to create
            }

            // Ensure image is pulled before creating container
            try
            {
                await client.Images.InspectImageAsync("grafana/pyroscope:latest", cancellationToken);
            }
            catch (DockerImageNotFoundException)
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = "grafana/pyroscope:latest" },
                    new AuthConfig(),
                    new Progress<JSONMessage>(),
                    cancellationToken);
            }

            var createParams = new CreateContainerParameters
            {
                Name = "kongor-pyroscope",
                Image = "grafana/pyroscope:latest",
                HostConfig = new HostConfig
                {
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["4040/tcp"] = new List<PortBinding> { new() { HostPort = "4040" } }
                    }
                },
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    ["4040/tcp"] = default
                }
            };

            var created = await client.Containers.CreateContainerAsync(createParams, cancellationToken);
            return await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexusOrchestratorService] Failed to ensure Pyroscope container: {ex.Message}");
            return false;
        }
    }
}

