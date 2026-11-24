// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Information model for a running dedicated game server worker process.
/// </summary>
public sealed class GameServerWorkerInfo
{
    public int ProcessId { get; set; }
    public int Port { get; set; }
    public string ServerName { get; set; } = "Project KONGOR Dedicated Server";
    public string? Map { get; set; }
    public int Tickrate { get; set; } = ClientModMountBuilder.CanonicalSimulationTickRate;
    public string Executable { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public long WorkingSetBytes { get; set; }
    public double WorkingSetMb => Math.Round(WorkingSetBytes / (1024.0 * 1024.0), 2);
    public string CommandLine { get; set; } = string.Empty;
    public bool Responding { get; set; }
    public DateTimeOffset StartTime { get; set; }
}

/// <summary>
/// MCP Tool Handler for 'kongor_game_server_manage' (alias: 'kongor_game_server_manager').
/// Controls dedicated game server worker instances (hon_x64.exe in -dedicated mode).
/// Discovers running dedicated server processes, launches new workers, inspects status, and manages termination.
/// </summary>
public sealed class GameServerManageHandler : IDisposable
{
    private static readonly HttpClient SharedCompelHttpClient = ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromMilliseconds(1500));

    private readonly ConcurrentDictionary<int, GameServerWorkerInfo> _trackedWorkers = new();
    private readonly ConcurrentDictionary<int, StreamWriter> _workerStdin = new();
    private readonly ConcurrentDictionary<int, Process> _workerProcesses = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pipeServerCts = new();
    private readonly ConcurrentQueue<string> _recentServerOutput = new();
    private const int MaxServerRingBufferLines = 500;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly IServerPortRegistry _portRegistry;
    private readonly HttpClient _httpClient;
    private readonly bool _hasCustomHttpClient;
    private readonly string _handlerId = Guid.NewGuid().ToString("N");

    public IServerPortRegistry PortRegistry => _portRegistry;

    public GameServerManageHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IServerPortRegistry? portRegistry = null,
        HttpClient? httpClient = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[GameServerManage] {msg}"));
        _portRegistry = portRegistry ?? ServerPortRegistry.Default;
        _httpClient = httpClient ?? SharedCompelHttpClient;
        _hasCustomHttpClient = httpClient != null;
    }

    /// <summary>
    /// Gets authoritative liveness telemetry for dedicated game server worker process(es).
    /// </summary>
    public ServerLivenessStatus GetLivenessStatus(int? port = null)
    {
        var running = DiscoverRunningDedicatedServers();
        GameServerWorkerInfo? target = null;
        if (port.HasValue && port.Value > 0)
        {
            target = running.FirstOrDefault(w => w.Port == port.Value);
        }
        else
        {
            target = running.FirstOrDefault();
        }

        if (target != null)
        {
            return new ServerLivenessStatus
            {
                Running = true,
                ProcessId = target.ProcessId,
                Port = target.Port,
                PipeConnected = target.Responding,
                Map = target.Map
            };
        }

        return new ServerLivenessStatus
        {
            Running = false,
            ProcessId = 0,
            Port = port ?? 0,
            PipeConnected = false,
            Map = null
        };
    }

    public void Dispose()
    {
        foreach (int port in _pipeServerCts.Keys)
        {
            StopNamedPipeListener(port);
        }
    }

    public void StartNamedPipeListener(int port)
    {
        if (!OperatingSystem.IsWindows()) return;

        StopNamedPipeListener(port);

        CancellationTokenSource cts = new();
        _pipeServerCts[port] = cts;
        string pipeName = $"hon-server-{port}";

        _ = Task.Run(async () =>
        {
            _logger($"[DedicatedServer:{port}] Starting Named Pipe server listener on '\\\\.\\pipe\\{pipeName}'...");
            while (!cts.IsCancellationRequested)
            {
                NamedPipeServerStream? serverStream = null;
                try
                {
                    serverStream = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await serverStream.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

                    using StreamReader reader = new(serverStream, Encoding.UTF8, leaveOpen: false);
                    string? line;
                    while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string cmd = line.Trim();
                        _logger($"[DedicatedServer:{port}] Received command over Named Pipe '\\\\.\\pipe\\{pipeName}': '{cmd}'");

                        if (_workerStdin.TryGetValue(port, out StreamWriter? writer))
                        {
                            await writer.WriteLineAsync(cmd.AsMemory(), cts.Token).ConfigureAwait(false);
                            await writer.FlushAsync(cts.Token).ConfigureAwait(false);
                            _logger($"[DedicatedServer:{port}] Forwarded pipe command '{cmd}' to worker stdin stream.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested) break;
                    _logger($"[DedicatedServer:{port}] Named Pipe listener exception: {ex.Message}");
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (serverStream != null)
                    {
                        await serverStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            _logger($"[DedicatedServer:{port}] Named Pipe server listener on '\\\\.\\pipe\\{pipeName}' stopped.");
        }, cts.Token);
    }

    public void StopNamedPipeListener(int port)
    {
        if (_pipeServerCts.TryRemove(port, out CancellationTokenSource? cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (Exception ex)
            {
                _logger($"[DedicatedServer:{port}] [WARN] Exception stopping Named Pipe listener: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        int? processId = arguments.TryGetProperty("processId", out JsonElement pidElem) && pidElem.TryGetInt32(out int p)
            ? p
            : null;

        int port = arguments.TryGetProperty("port", out JsonElement portElem) && portElem.TryGetInt32(out int pt) && pt > 0
            ? pt
            : 11235;

        string masterServer = arguments.TryGetProperty("masterServer", out JsonElement msElem) && !string.IsNullOrWhiteSpace(msElem.GetString())
            ? msElem.GetString()!.Trim()
            : "127.0.0.1:55555";

        string serverName = arguments.TryGetProperty("serverName", out JsonElement nameElem) && !string.IsNullOrWhiteSpace(nameElem.GetString())
            ? nameElem.GetString()!.Trim()
            : "Project KONGOR Dedicated Server";

        string map = arguments.TryGetProperty("map", out JsonElement mapElem) && !string.IsNullOrWhiteSpace(mapElem.GetString())
            ? mapElem.GetString()!.Trim()
            : "caldavar";

        string? customServerPath = arguments.TryGetProperty("customServerPath", out JsonElement customElem) && !string.IsNullOrWhiteSpace(customElem.GetString())
            ? customElem.GetString()!.Trim()
            : null;

        string? workingDirectory = arguments.TryGetProperty("workingDirectory", out JsonElement wdElem) && !string.IsNullOrWhiteSpace(wdElem.GetString())
            ? wdElem.GetString()!.Trim()
            : null;

        int tickrate = ClientModMountBuilder.CanonicalSimulationTickRate;
        if (arguments.TryGetProperty("tickrate", out JsonElement tickElem) || arguments.TryGetProperty("tickRate", out tickElem))
        {
            if (tickElem.ValueKind == JsonValueKind.Number && tickElem.TryGetInt32(out int tr))
            {
                tickrate = Math.Clamp(tr, 20, 60);
            }
            else if (tickElem.ValueKind == JsonValueKind.String && int.TryParse(tickElem.GetString(), out int trParsed))
            {
                tickrate = Math.Clamp(trParsed, 20, 60);
            }
        }

        if (tickrate != ClientModMountBuilder.CanonicalSimulationTickRate)
        {
            _logger($"[WARN] Non-canonical simulation tickrate ({tickrate} Hz != 20 Hz) requested: IEntityState Phase-1 expiration and 20 Hz <onframe> scripts may malfunction.");
        }

        _logger($"Received action '{action}' (Port: {port}, MasterServer: '{masterServer}', ServerName: '{serverName}', Map: '{map}', Tickrate: {tickrate})...");

        try
        {
            switch (action)
            {
                case "launch":
                {
                    return await LaunchWorkerAsync(port, masterServer, serverName, map, customServerPath, workingDirectory, tickrate, cancellationToken).ConfigureAwait(false);
                }

                case "stop":
                {
                    int? portArg = arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pVal) && pVal > 0 ? pVal : null;
                    return StopWorker(processId, portArg);
                }

                case "stop_all":
                {
                    return StopAllWorkers();
                }

                case "logs":
                {
                    int lines = (arguments.TryGetProperty("lines", out JsonElement lElem) && lElem.TryGetInt32(out int lVal) && lVal > 0)
                        ? lVal
                        : (arguments.TryGetProperty("maxLines", out JsonElement maxElem) && maxElem.TryGetInt32(out int ml) && ml > 0)
                            ? ml
                            : 50;

                    string level = arguments.TryGetProperty("level", out JsonElement lvlElem) && !string.IsNullOrWhiteSpace(lvlElem.GetString())
                        ? lvlElem.GetString()!.Trim().ToLowerInvariant()
                        : "all";

                    return await GetServerLogsAsync(port, processId, lines, level, workingDirectory, cancellationToken).ConfigureAwait(false);
                }

                case "command":
                {
                    if (!arguments.TryGetProperty("command", out JsonElement cmdElem) || string.IsNullOrWhiteSpace(cmdElem.GetString()))
                    {
                        return ToolCallResult.Error("Missing required parameter 'command' for action 'command'.");
                    }
                    string cmd = cmdElem.GetString()!.Trim();
                    string channel = arguments.TryGetProperty("channel", out JsonElement chElem) && !string.IsNullOrWhiteSpace(chElem.GetString())
                        ? chElem.GetString()!.Trim().ToLowerInvariant()
                        : "auto";
                    return await ExecuteCommandOnPortAsync(port, cmd, autoLaunch: false, channel: channel, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                case "status":
                default:
                {
                    return GetWorkersStatus();
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception executing game server manage action '{action}': {ex}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Failed executing game server manage action '{action}': {ex.Message}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Error($"Failed executing game server manage action '{action}': {ex.Message}");
        }
    }

    /// <summary>
    /// Detects whether the current execution context is within an automated test runner (xUnit/testhost)
    /// to adhere to AGENTS.md Rule 4 (Zero Desktop GUI Window Spawning).
    /// </summary>
    public static bool IsTestExecution =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a =>
        {
            string? name = a.GetName().Name;
            return name != null && (name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("testhost", StringComparison.OrdinalIgnoreCase));
        });

    public Task<ToolCallResult> LaunchWorkerAsync(
        int preferredPort = 11235,
        string masterServer = "127.0.0.1:55555",
        string serverName = "Project KONGOR Dedicated Server",
        string map = "caldavar",
        string? customServerPath = null,
        string? customWorkingDirectory = null,
        int tickrate = ClientModMountBuilder.CanonicalSimulationTickRate,
        CancellationToken cancellationToken = default)
    {
        return SingleFlightSpawner.ExecuteSingleFlightAsync($"server:{_handlerId}:{preferredPort}", () =>
            LaunchWorkerCoreAsync(preferredPort, masterServer, serverName, map, customServerPath, customWorkingDirectory, tickrate, cancellationToken),
            cancellationToken);
    }

    private async Task<ToolCallResult> LaunchWorkerCoreAsync(
        int preferredPort,
        string masterServer,
        string serverName,
        string map,
        string? customServerPath,
        string? customWorkingDirectory,
        int tickrate,
        CancellationToken cancellationToken)
    {
        int clampedTickrate = Math.Clamp(tickrate, 20, 60);
        await (McpProgressContext.Current?.ReportProgressAsync(0.2, 1.0, $"Initiating dedicated game server worker on port {preferredPort} (Tickrate: {clampedTickrate} Hz)...", cancellationToken) ?? Task.CompletedTask);
        _logger($"Resolving dedicated game server executable for port {preferredPort} (Tickrate: {clampedTickrate} Hz)...");

        // If an explicit custom server path was provided, validate that it resolves
        if (!string.IsNullOrWhiteSpace(customServerPath))
        {
            if (!GameClientManageHandler.TryResolveClientExecutable(customServerPath, customWorkingDirectory, out FileInfo? customExec))
            {
                string err = $"Unable to resolve game server executable 'hon_x64.exe' for dedicated server launch. Checked 'games/client/hon_x64.exe' and '{customServerPath}'.";
                _logger($"[ERROR] {err}");
                await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Launch aborted: {err}", cancellationToken) ?? Task.CompletedTask);
                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = "launch",
                    running = false,
                    errorMessage = err,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        // Fast-path guardrail for test environments per AGENTS.md Rule 4
        if (IsTestExecution)
        {
            await (McpProgressContext.Current?.ReportProgressAsync(0.6, 1.0, $"Configuring worker instance for port {preferredPort} on map '{map}' ({clampedTickrate} Hz)...", cancellationToken) ?? Task.CompletedTask);
            _logger($"[TEST ENVIRONMENT DETECTED] Spawning simulated dedicated server worker on port {preferredPort} ({clampedTickrate} Hz) without real process creation or startup delay.");
            int simulatedPid = Environment.ProcessId;
            GameServerWorkerInfo workerInfo = new()
            {
                ProcessId = simulatedPid,
                Port = preferredPort,
                ServerName = serverName,
                Map = map,
                Tickrate = clampedTickrate,
                Executable = "hon_x64.exe",
                WorkingDirectory = Environment.CurrentDirectory,
                CommandLine = $"-dedicated -server -port {preferredPort} -masterserver {masterServer} -name \"{serverName}\" -map {map} +Set svr_gameFPS {clampedTickrate} +Set svr_maxFramesPerHostFrame 5",
                Responding = true,
                StartTime = _timeProvider.GetUtcNow()
            };

            _trackedWorkers[simulatedPid] = workerInfo;
            _portRegistry.RegisterWorker(simulatedPid, preferredPort, serverName, map);
            if (!_workerStdin.ContainsKey(preferredPort))
            {
                _workerStdin[preferredPort] = new StreamWriter(new MemoryStream(), Encoding.UTF8, leaveOpen: true);
            }
            StartNamedPipeListener(preferredPort);

            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Dedicated game server worker launched on port {preferredPort}.", cancellationToken) ?? Task.CompletedTask);

            return ToolCallResult.Json(new
            {
                status = "success",
                action = "launch",
                running = true,
                processId = simulatedPid,
                port = preferredPort,
                serverName,
                map,
                tickrate = clampedTickrate,
                executable = "hon_x64.exe",
                workingDirectory = Environment.CurrentDirectory,
                arguments = BuildDedicatedServerArguments(Environment.CurrentDirectory, preferredPort, masterServer, serverName, map, clampedTickrate),
                message = $"Simulated dedicated server worker launched on port {preferredPort} (PID: {simulatedPid}, Tickrate: {clampedTickrate} Hz).",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (!GameClientManageHandler.TryResolveClientExecutable(customServerPath, customWorkingDirectory, out FileInfo? executable))
        {
            string err = $"Unable to resolve game server executable 'hon_x64.exe' for dedicated server launch. Checked 'games/client/hon_x64.exe' and '{customServerPath ?? "none"}'.";
            _logger($"[ERROR] {err}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Launch aborted: {err}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }


        string gameDir = customWorkingDirectory ?? executable.DirectoryName ?? Environment.CurrentDirectory;
        _logger($"Resolved server binary: '{executable.FullName}', directory: '{gameDir}'.");

        // Ensure userdata directory initialized
        GameLauncherService.EnsureUserDirectoryInitialized(gameDir, _logger);

        // Ensure port is available or find next available port
        int actualPort = EnsurePortAvailable(preferredPort);
        if (actualPort != preferredPort)
        {
            _logger($"[PORT] Port {preferredPort} was in use; allocated next available port {actualPort}.");
        }

        // Build canonical dedicated server arguments
        IReadOnlyList<string> arguments = BuildDedicatedServerArguments(gameDir, actualPort, masterServer, serverName, map, clampedTickrate);
        string joinedArgs = string.Join(" ", arguments);
        _logger($"Spawning dedicated server worker with arguments: {joinedArgs}");

        await (McpProgressContext.Current?.ReportProgressAsync(0.6, 1.0, $"Launching dedicated server worker for port {actualPort} on map '{map}'...", cancellationToken) ?? Task.CompletedTask);

        ProcessStartInfo startInfo = new()
        {
            FileName = executable.FullName,
            WorkingDirectory = gameDir,
            Arguments = joinedArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger($"[DedicatedServer:{actualPort} STDOUT] {e.Data}");
                _recentServerOutput.Enqueue($"[Port {actualPort}] {e.Data}");
                while (_recentServerOutput.Count > MaxServerRingBufferLines)
                    _recentServerOutput.TryDequeue(out string? _);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger($"[DedicatedServer:{actualPort} STDERR] {e.Data}");
                _recentServerOutput.Enqueue($"[Port {actualPort} STDERR] {e.Data}");
                while (_recentServerOutput.Count > MaxServerRingBufferLines)
                    _recentServerOutput.TryDequeue(out string? _);
            }
        };

        bool started;
        try
        {
            started = process.Start();
        }
        catch (Exception ex)
        {
            string err = $"Failed to start dedicated server process: {ex.Message}";
            _logger($"[ERROR] {err}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Launch failed: {err}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (!started)
        {
            string err = $"Process.Start returned false for '{executable.FullName}'.";
            _logger($"[ERROR] {err}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Launch failed: {err}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        _workerStdin[actualPort] = process.StandardInput;
        _workerProcesses[actualPort] = process;
        JobObjectManager.AssignProcess(process);
        StartNamedPipeListener(actualPort);
        process.Exited += (_, _) =>
        {
            _workerStdin.TryRemove(actualPort, out _);
            _workerProcesses.TryRemove(actualPort, out _);
            _portRegistry.UnregisterWorker(actualPort);
            StopNamedPipeListener(actualPort);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Brief delay to allow initial startup
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        int pid = process.Id;
        bool isRunning = !process.HasExited;

        if (isRunning)
        {
            GameServerWorkerInfo workerInfo = new()
            {
                ProcessId = pid,
                Port = actualPort,
                ServerName = serverName,
                Map = map,
                Tickrate = clampedTickrate,
                Executable = executable.FullName,
                WorkingDirectory = gameDir,
                CommandLine = joinedArgs,
                Responding = true,
                StartTime = _timeProvider.GetUtcNow()
            };

            _trackedWorkers[pid] = workerInfo;
            _portRegistry.RegisterWorker(pid, actualPort, serverName, map);
            _logger($"Dedicated server worker spawned successfully! PID: {pid}, Port: {actualPort}, Server: '{serverName}'");
        }
        else
        {
            _logger($"[WARN] Dedicated server process exited prematurely with exit code: {process.ExitCode}");
        }

        await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Dedicated game server worker launched on port {actualPort}.", cancellationToken) ?? Task.CompletedTask);

        return ToolCallResult.Json(new
        {
            status = isRunning ? "success" : "exited",
            action = "launch",
            running = isRunning,
            processId = pid,
            port = actualPort,
            serverName,
            map,
            tickrate = clampedTickrate,
            executable = executable.FullName,
            workingDirectory = gameDir,
            arguments,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult StopWorker(int? processId, int? port = null)
    {
        List<int> killedIds = [];
        IReadOnlyList<GameServerWorkerInfo> running = DiscoverRunningDedicatedServers();

        if (port.HasValue && port.Value > 0)
        {
            var matchByPort = running.FirstOrDefault(w => w.Port == port.Value);
            if (matchByPort != null)
            {
                processId = matchByPort.ProcessId;
            }
        }

        if (processId.HasValue && processId.Value > 0)
        {
            int pid = processId.Value;
            var targetWorker = running.FirstOrDefault(w => w.ProcessId == pid);
            int targetPort = targetWorker?.Port ?? port ?? 0;

            _logger($"Stopping specific dedicated server PID {pid} (Port: {targetPort})...");
            if (pid == Environment.ProcessId || IsTestExecution)
            {
                _logger($"[TEST MODE] Suppressing Process.Kill for simulated server worker PID {pid}.");
                _trackedWorkers.TryRemove(pid, out _);
                if (targetPort > 0)
                {
                    _portRegistry.UnregisterWorker(targetPort);
                    StopNamedPipeListener(targetPort);
                }
                killedIds.Add(pid);
            }
            else
            {
                try
                {
                    using Process p = Process.GetProcessById(pid);
                    p.Kill(true);
                    p.WaitForExit(2000);
                    killedIds.Add(pid);
                    _trackedWorkers.TryRemove(pid, out _);
                    if (targetPort > 0)
                    {
                        _portRegistry.UnregisterWorker(targetPort);
                        StopNamedPipeListener(targetPort);
                    }
                    _logger($"Terminated server worker PID {pid}.");
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Could not kill process PID {pid}: {ex.Message}");
                }
            }
        }
        else
        {
            // If no PID specified and exactly 1 worker running, terminate that one
            if (running.Count == 1)
            {
                int pid = running[0].ProcessId;
                int targetPort = running[0].Port;
                _logger($"Stopping the single active dedicated server worker PID {pid} (Port: {targetPort})...");
                if (pid == Environment.ProcessId || IsTestExecution)
                {
                    _logger($"[TEST MODE] Suppressing Process.Kill for simulated server worker PID {pid}.");
                    _trackedWorkers.TryRemove(pid, out _);
                    _portRegistry.UnregisterWorker(targetPort);
                    StopNamedPipeListener(targetPort);
                    killedIds.Add(pid);
                }
                else
                {
                    try
                    {
                        using Process p = Process.GetProcessById(pid);
                        p.Kill(true);
                        p.WaitForExit(2000);
                        killedIds.Add(pid);
                        _trackedWorkers.TryRemove(pid, out _);
                        _portRegistry.UnregisterWorker(targetPort);
                        StopNamedPipeListener(targetPort);
                        _logger($"Terminated server worker PID {pid}.");
                    }
                    catch (Exception ex)
                    {
                        _logger($"[WARN] Could not kill process PID {pid}: {ex.Message}");
                    }
                }
            }
            else if (running.Count > 1)
            {
                return ToolCallResult.Json(new
                {
                    status = "ambiguous",
                    action = "stop",
                    running = true,
                    message = $"Multiple dedicated servers are running ({string.Join(", ", running.Select(w => $"PID {w.ProcessId} on port {w.Port}"))}). Specify 'processId' or use action 'stop_all'.",
                    runningWorkers = running,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            else
            {
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "stop",
                    running = false,
                    message = "No active dedicated game server workers were running.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "stop",
            terminatedProcessesCount = killedIds.Count,
            terminatedProcessIds = killedIds,
            message = killedIds.Count > 0
                ? $"Successfully stopped dedicated server worker(s): {string.Join(", ", killedIds)}."
                : "Failed to stop specified dedicated server worker.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult StopAllWorkers()
    {
        _logger("Stopping ALL dedicated game server worker instances...");
        List<int> killedIds = [];

        IReadOnlyList<GameServerWorkerInfo> workers = DiscoverRunningDedicatedServers();
        foreach (GameServerWorkerInfo worker in workers)
        {
            try
            {
                if (worker.ProcessId == Environment.ProcessId || IsTestExecution)
                {
                    _trackedWorkers.TryRemove(worker.ProcessId, out _);
                    _portRegistry.UnregisterWorker(worker.Port);
                    StopNamedPipeListener(worker.Port);
                    killedIds.Add(worker.ProcessId);
                }
                else
                {
                    using Process p = Process.GetProcessById(worker.ProcessId);
                    p.Kill(true);
                    p.WaitForExit(1500);
                    killedIds.Add(worker.ProcessId);
                    _trackedWorkers.TryRemove(worker.ProcessId, out _);
                    _portRegistry.UnregisterWorker(worker.Port);
                    StopNamedPipeListener(worker.Port);
                    _logger($"Terminated dedicated server worker PID {worker.ProcessId} (Port: {worker.Port}).");
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Could not kill process PID {worker.ProcessId}: {ex.Message}");
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "stop_all",
            terminatedProcessesCount = killedIds.Count,
            terminatedProcessIds = killedIds,
            message = killedIds.Count > 0
                ? $"Successfully terminated {killedIds.Count} dedicated game server worker(s)."
                : "No dedicated game server worker processes were running.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult GetWorkersStatus()
    {
        IReadOnlyList<GameServerWorkerInfo> workers = DiscoverRunningDedicatedServers();
        _logger($"Discovered {workers.Count} active dedicated game server worker(s).");

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "status",
            totalWorkers = workers.Count,
            running = workers.Count > 0,
            workers,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the dedicated server on <paramref name="port"/> to be ready.
    /// </summary>
    public async Task<bool> EnsureServerReadyAsync(int port, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (IsTestExecution) return true;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveness = await ExecutionTruth.ProbeServerLivenessAsync(port, this, cancellationToken).ConfigureAwait(false);
            if (liveness.Running && (liveness.PipeConnected || liveness.EngineReady)) return true;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Discovers running dedicated server processes on the machine (hon_x64 with -dedicated or -server).
    /// </summary>
    public IReadOnlyList<GameServerWorkerInfo> DiscoverRunningDedicatedServers()
    {
        List<GameServerWorkerInfo> results = [];
        HashSet<int> seenPids = [];

        // 1. Check in-memory tracked workers
        foreach (KeyValuePair<int, GameServerWorkerInfo> kvp in _trackedWorkers)
        {
            if (kvp.Key == Environment.ProcessId)
            {
                results.Add(kvp.Value);
                seenPids.Add(kvp.Key);
                continue;
            }

            try
            {
                using Process p = Process.GetProcessById(kvp.Key);
                if (!p.HasExited)
                {
                    p.Refresh();
                    kvp.Value.WorkingSetBytes = p.WorkingSet64;
                    kvp.Value.Responding = p.Responding;
                    results.Add(kvp.Value);
                    seenPids.Add(kvp.Key);
                }
                else
                {
                    _trackedWorkers.TryRemove(kvp.Key, out _);
                }
            }
            catch
            {
                _trackedWorkers.TryRemove(kvp.Key, out _);
            }
        }

        // 2. Discover via WMI / process inspection (skip in automated test environments unless custom HttpClient is provided)
        if (!IsTestExecution)
        {
            string[] targetNames = ["hon_x64", "hon"];
            foreach (string name in targetNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    foreach (Process p in procs)
                    {
                        if (seenPids.Contains(p.Id) || p.Id == Environment.ProcessId || p.HasExited)
                            continue;

                        string? cmdLine = GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                        if (IsDedicatedServerProcess(p, cmdLine))
                        {
                            int port = ExtractPortFromCommandLine(cmdLine) ?? 11235;
                            GameServerWorkerInfo info = new()
                            {
                                ProcessId = p.Id,
                                Port = port,
                                ServerName = ExtractServerNameFromCommandLine(cmdLine) ?? "Project KONGOR Dedicated Server",
                                Tickrate = ExtractTickrateFromCommandLine(cmdLine) ?? 60,
                                Executable = TryGetProcessFileName(p) ?? string.Empty,
                                WorkingSetBytes = p.WorkingSet64,
                                Responding = p.Responding,
                                CommandLine = cmdLine ?? string.Empty,
                                StartTime = TryGetProcessStartTime(p)
                            };

                            results.Add(info);
                            seenPids.Add(p.Id);
                        }
                    }
                }
                catch { }
            }
        }

        // 3. Discover via COMPEL Control Plane HTTP (/api/servers on local 11234, Docker 11334, K3s 11434)
        if (!IsTestExecution || _hasCustomHttpClient)
        {
            DiscoverCompelManagedServers(results);
        }

        return results.OrderBy(r => r.Port).ToList().AsReadOnly();
    }

    private void DiscoverCompelManagedServers(List<GameServerWorkerInfo> results)
    {
        HashSet<int> seenPorts = results.Select(r => r.Port).ToHashSet();
        int[] compelPorts = [11234, 11334, 11484, 11434];

        foreach (int compelPort in compelPorts)
        {
            if (!_hasCustomHttpClient && !IsLocalTcpPortListening(compelPort))
            {
                continue;
            }

            try
            {
                using var cts = new CancellationTokenSource(500);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{compelPort}/api/servers");
                string? token = CompelManageHandler.TryResolveAuthToken();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                using HttpResponseMessage resp = _httpClient.Send(req, cts.Token);
                if (!resp.IsSuccessStatusCode) continue;

                using Stream stream = resp.Content.ReadAsStream(cts.Token);
                using JsonDocument doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement worker in doc.RootElement.EnumerateArray())
                {
                    bool isRunning = (worker.TryGetProperty("running", out var runEl) && runEl.ValueKind == JsonValueKind.True) ||
                                     (worker.TryGetProperty("isRunning", out runEl) && runEl.ValueKind == JsonValueKind.True);
                    if (!isRunning) continue;

                    int port = worker.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out int pVal) ? pVal : 0;
                    if (port <= 0 || seenPorts.Contains(port)) continue;

                    int pid = worker.TryGetProperty("processId", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number && pidEl.TryGetInt32(out int pidVal)
                        ? pidVal
                        : (100000 + port);
                    long wsBytes = worker.TryGetProperty("workingSetBytes", out var wsEl) && wsEl.TryGetInt64(out long wsVal) ? wsVal : 0;
                    double tickHz = (worker.TryGetProperty("tickrateHz", out var tickEl) && tickEl.TryGetDouble(out double tVal) && tVal > 0)
                        ? tVal
                        : (worker.TryGetProperty("currentTickrateHz", out tickEl) && tickEl.TryGetDouble(out tVal) && tVal > 0 ? tVal : 50.0);
                    double uptimeSec = worker.TryGetProperty("uptimeSeconds", out var upEl) && upEl.TryGetDouble(out double uVal) ? uVal : 0.0;
                    string? customName = worker.TryGetProperty("serverName", out var snEl) && snEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(snEl.GetString())
                        ? snEl.GetString()
                        : null;

                    results.Add(new GameServerWorkerInfo
                    {
                        ProcessId = pid,
                        Port = port,
                        ServerName = customName ?? (compelPort switch
                        {
                            11334 => $"COMPEL Docker Dedicated Server (:{port})",
                            11484 => $"COMPEL Docker Queue Arcade Server (:{port})",
                            _ => $"COMPEL Dedicated Server (:{port})"
                        }),
                        Tickrate = (int)Math.Round(tickHz),
                        Executable = compelPort switch
                        {
                            11334 => "docker://kongor-stack-compel-dedicated/hon-x86_64-server",
                            11484 => "docker://kongor-stack-compel-arcade/hon-x86_64-server",
                            _ => "compel://hon_x64.exe"
                        },
                        WorkingSetBytes = wsBytes,
                        Responding = true,
                        CommandLine = $"-dedicated -svr_port {port} (managed by COMPEL :{compelPort})",
                        StartTime = _timeProvider.GetUtcNow().AddSeconds(-Math.Max(0, uptimeSec))
                    });
                    seenPorts.Add(port);
                }
            }
            catch (Exception ex)
            {
                _logger($"[DedicatedServer] COMPEL discovery probe on :{compelPort} skipped: {ex.Message}");
            }
        }
    }

    private static bool IsLocalTcpPortListening(int port)
    {
        try
        {
            IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in props.GetActiveTcpListeners())
            {
                if (ep.Port == port)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through and allow HTTP attempt if socket table inspection fails
            return true;
        }
        return false;
    }

    /// <summary>
    /// Pipes a console command or cvar to a specific dedicated game server port.
    /// Unified 3-Tier Routing:
    /// Tier 1: Direct stdin stream -> Native Named Pipe (hon-server-native-{port}) -> Managed Named Pipe (hon-server-{port})
    /// Tier 2: COMPEL Control Plane HTTP POST /api/instances/{port}/command (ports 11234 local / 11334 Docker / 11434 K3s)
    /// Tier 3: Docker compel-dedicated-server /proc/{pid}/fd/0 fallback
    /// </summary>
    public Task<ToolCallResult> ExecuteCommandOnPortAsync(int port, string command, CancellationToken cancellationToken = default)
        => ExecuteCommandOnPortAsync(port, command, autoLaunch: false, channel: "auto", resolutionReason: null, cancellationToken);

    public Task<ToolCallResult> ExecuteCommandOnPortAsync(
        int port,
        string command,
        bool autoLaunch,
        CancellationToken cancellationToken = default)
        => ExecuteCommandOnPortAsync(port, command, autoLaunch, channel: "auto", resolutionReason: null, cancellationToken);

    public Task<ToolCallResult> ExecuteCommandOnPortAsync(
        int port,
        string command,
        bool autoLaunch,
        string channel,
        CancellationToken cancellationToken = default)
        => ExecuteCommandOnPortAsync(port, command, autoLaunch, channel, resolutionReason: null, cancellationToken);

    public async Task<ToolCallResult> ExecuteCommandOnPortAsync(
        int port,
        string command,
        bool autoLaunch,
        string channel,
        string? resolutionReason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolCallResult.Error("Missing required parameter 'command'.");
        }

        string trimmedCommand = command.Trim();
        _logger($"[DedicatedServer:{port}] Piping command: '{trimmedCommand}' (Channel: {channel})");

        bool allowStdin = channel is "auto" or "stdin" or "both";
        bool stdinDispatched = false;
        if (allowStdin && _workerStdin.TryGetValue(port, out StreamWriter? writer))
        {
            try
            {
                await writer.WriteLineAsync(trimmedCommand.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stdinDispatched = true;
                _logger($"[DedicatedServer:{port}] Wrote command '{trimmedCommand}' to stdin stream.");
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Failed to write to server stdin on port {port}: {ex.Message}");
            }
        }

        bool allowPipe = channel is "pipe" or "ipc" or "both" || (channel == "auto" && !stdinDispatched);
        bool pipeDispatched = false;
        string pipeResponse = string.Empty;
        string activePipeChannel = "pipe";
        string nativePipeName = $"hon-server-native-{port}";
        string managedPipeName = $"hon-server-{port}";

        if (allowPipe && OperatingSystem.IsWindows())
        {
            // 1. Probe Native Guard Named Pipe first (\\.\pipe\hon-server-native-{port}, framed 0x4D 0x57 protocol)
            bool nativePipeExists = Directory.GetFiles(@"\\.\pipe\", nativePipeName).Length > 0;
            if (nativePipeExists)
            {
                try
                {
                    await using var nativeBridge = new GameIpcBridge(nativePipeName, logAction: msg => _logger($"[IPC:{nativePipeName}] {msg}"));
                    using var timeoutCts = new CancellationTokenSource(2000);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    await nativeBridge.ConnectAsync(500, linkedCts.Token).ConfigureAwait(false);
                    if (nativeBridge.IsConnected)
                    {
                        pipeResponse = await nativeBridge.ExecuteCommandAsync(trimmedCommand, 1500, linkedCts.Token).ConfigureAwait(false);
                        pipeDispatched = true;
                        activePipeChannel = "native-pipe";
                        _logger($"[DedicatedServer:{port}] Native IPC ('{nativePipeName}') returned: '{pipeResponse}'");
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[DedicatedServer:{port}] Native pipe '{nativePipeName}' probe failed: {ex.Message}");
                }
            }

            // 2. Probe Managed Named Pipe (\\.\pipe\hon-server-{port})
            if (!pipeDispatched)
            {
                bool managedPipeExists = Directory.GetFiles(@"\\.\pipe\", managedPipeName).Length > 0;
                if (managedPipeExists)
                {
                    try
                    {
                        await using NamedPipeClientStream rawClient = new(".", managedPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                        using var rawCts = new CancellationTokenSource(250);
                        using var rawLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, rawCts.Token);
                        await rawClient.ConnectAsync(rawLinkedCts.Token).ConfigureAwait(false);
                        byte[] cmdBytes = Encoding.UTF8.GetBytes(trimmedCommand + "\n");
                        await rawClient.WriteAsync(cmdBytes, rawLinkedCts.Token).ConfigureAwait(false);
                        await rawClient.FlushAsync(rawLinkedCts.Token).ConfigureAwait(false);
                        pipeDispatched = true;
                        pipeResponse = "OK";
                        activePipeChannel = "pipe";
                        _logger($"[DedicatedServer:{port}] Dispatched command to Named Pipe '{managedPipeName}'.");
                    }
                    catch
                    {
                        // Fall back to GameIpcBridge framed protocol on managedPipeName
                    }

                    if (!pipeDispatched)
                    {
                        try
                        {
                            await using var bridge = new GameIpcBridge(managedPipeName, logAction: msg => _logger($"[IPC:{managedPipeName}] {msg}"));
                            using var timeoutCts = new CancellationTokenSource(250);
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                            await bridge.ConnectAsync(200, linkedCts.Token).ConfigureAwait(false);
                            if (bridge.IsConnected)
                            {
                                pipeResponse = await bridge.ExecuteCommandAsync(trimmedCommand, 1500, linkedCts.Token).ConfigureAwait(false);
                                pipeDispatched = true;
                                activePipeChannel = "pipe";
                                _logger($"[DedicatedServer:{port}] IPC ('{managedPipeName}') returned: '{pipeResponse}'");
                            }
                        }
                        catch
                        {
                            // Named pipe might not be enabled or connected; fall through to COMPEL / Docker
                        }
                    }
                }
            }
        }

        // Tier 2: COMPEL Control Plane HTTP POST /api/instances/{port}/command
        bool allowCompel = channel is "auto" or "compel" or "http" or "both";
        if (!stdinDispatched && !pipeDispatched && allowCompel && (!IsTestExecution || _hasCustomHttpClient))
        {
            var compelDispatch = await TryDispatchViaCompelHttpAsync(port, trimmedCommand, cancellationToken).ConfigureAwait(false);
            if (compelDispatch.Success)
            {
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "command",
                    target = $"server:{port}",
                    port,
                    resolvedPort = port,
                    resolutionReason = resolutionReason ?? "explicit_port",
                    command = trimmedCommand,
                    channel = compelDispatch.Channel,
                    dispatchedViaStdin = false,
                    dispatchedViaIpc = true,
                    dispatchedViaPipe = false,
                    dispatchedViaCompel = true,
                    compelEndpoint = compelDispatch.Endpoint,
                    ipcResponse = compelDispatch.Message,
                    message = $"Successfully piped command '{trimmedCommand}' to dedicated game server on port {port} via COMPEL Control Plane ({compelDispatch.Channel}).",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        // Tier 3: Docker container COMPEL /proc/<pid>/fd/0 fallback (Match 11335..11434 + Queue Arcade 11485..11534)
        bool allowDocker = channel is "auto" or "docker" or "both";
        if (!stdinDispatched && !pipeDispatched && allowDocker && !IsTestExecution && port is >= 11335 and <= 11534)
        {
            var dockerDispatch = await TryDispatchViaDockerContainerAsync(port, trimmedCommand, cancellationToken).ConfigureAwait(false);
            if (dockerDispatch.Success)
            {
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "command",
                    target = $"server:{port}",
                    port,
                    resolvedPort = port,
                    resolutionReason = resolutionReason ?? "explicit_port",
                    command = trimmedCommand,
                    channel = "docker-exec",
                    dispatchedViaStdin = false,
                    dispatchedViaIpc = true,
                    dispatchedViaPipe = false,
                    dispatchedViaDocker = true,
                    ipcResponse = dockerDispatch.Message,
                    message = $"Successfully piped command '{trimmedCommand}' to containerized dedicated server on port {port} via docker exec.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        if (!stdinDispatched && !pipeDispatched)
        {
            var runningWorkers = DiscoverRunningDedicatedServers();
            var targetWorker = runningWorkers.FirstOrDefault(w => w.Port == port);
            if (targetWorker == null)
            {
                if (autoLaunch)
                {
                    _logger($"[DedicatedServer:{port}] Server is offline and autoLaunch is enabled. Launching dedicated server worker...");
                    var launchResult = await LaunchWorkerAsync(preferredPort: port, cancellationToken: cancellationToken).ConfigureAwait(false);
                    int waited = 0;
                    while (waited < 3000 && !_workerStdin.ContainsKey(port))
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        waited += 200;
                    }
                    return await ExecuteCommandOnPortAsync(port, command, autoLaunch: false, channel: channel, resolutionReason: resolutionReason, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                return ToolCallResult.Json(new
                {
                    status = ExecutionTruth.StatusTargetOffline,
                    executionMode = ExecutionTruth.ModeOfflineSimulation,
                    action = "command",
                    target = $"server:{port}",
                    port,
                    resolvedPort = port,
                    resolutionReason = resolutionReason ?? "explicit_port",
                    command = trimmedCommand,
                    message = $"No running dedicated game server worker found on port {port}.",
                    targetStatus = new
                    {
                        client = new { running = false, pid = (int?)null, engineReady = false, ipcConnected = false },
                        server = new { running = false, pid = (int?)null, port, pipeConnected = false, engineReady = false }
                    },
                    remediation = new
                    {
                        hint = port is >= 11335 and <= 11434
                            ? $"Dedicated game server on port {port} is offline on host. Pass 'autoLaunch: true', launch server on port {port}, or if running inside Docker use 'docker exec compel-dedicated-server' to inspect container worker state."
                            : $"Dedicated game server on port {port} is offline. Pass 'autoLaunch: true' or launch server on port {port}.",
                        suggestedAction = "kongor_game_server_manage",
                        suggestedParams = new { action = "launch", port }
                    },
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }

            // Target worker is running but orphaned (stdin handle missing and pipe unreachable)
            if (autoLaunch)
            {
                _logger($"[DedicatedServer:{port}] Dedicated server worker PID {targetWorker.ProcessId} is orphaned (unreachable stdin/pipe). Restarting worker to restore dual-channel command routing...");
                StopWorker(targetWorker.ProcessId);
                var launchResult = await LaunchWorkerAsync(preferredPort: port, cancellationToken: cancellationToken).ConfigureAwait(false);
                int waited = 0;
                while (waited < 3000 && !_workerStdin.ContainsKey(port))
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    waited += 200;
                }
                return await ExecuteCommandOnPortAsync(port, command, autoLaunch: false, channel: channel, resolutionReason: resolutionReason, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return ToolCallResult.Json(new
            {
                status = "warning",
                action = "command",
                target = $"server:{port}",
                port,
                resolvedPort = port,
                resolutionReason = resolutionReason ?? "explicit_port",
                command = trimmedCommand,
                message = $"Dedicated server is running on port {port} (PID {targetWorker.ProcessId}), but neither active stdin stream nor Named Pipe '\\\\.\\pipe\\hon-server-native-{port}' / '\\\\.\\pipe\\hon-server-{port}' could be reached.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "command",
            target = $"server:{port}",
            port,
            resolvedPort = port,
            resolutionReason = resolutionReason ?? "explicit_port",
            command = trimmedCommand,
            channel = (stdinDispatched && pipeDispatched) ? "both" : (stdinDispatched ? "stdin" : (pipeDispatched ? activePipeChannel : "none")),
            dispatchedViaStdin = stdinDispatched,
            dispatchedViaIpc = pipeDispatched,
            dispatchedViaPipe = pipeDispatched,
            ipcResponse = pipeResponse,
            message = $"Successfully piped command '{trimmedCommand}' to dedicated game server on port {port}.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    public static int[] GetCandidateCompelControlPorts(int gamePort)
    {
        if (gamePort is >= 11485 and <= 11534 or >= 21485 and <= 21534)
        {
            return [11484, 11334, 11234, 11434];
        }
        if (gamePort is >= 11335 and <= 11434 or >= 21335 and <= 21434)
        {
            return [11334, 11234, 11434, 11484];
        }
        if (gamePort is >= 11435 and <= 11484 or >= 21435 and <= 21484)
        {
            return [11434, 11234, 11334, 11484];
        }
        return [11234, 11334, 11484, 11434];
    }

    private async Task<(bool Success, string Channel, string Endpoint, string Message)> TryDispatchViaCompelHttpAsync(
        int port,
        string command,
        CancellationToken cancellationToken)
    {
        int[] candidatePorts = GetCandidateCompelControlPorts(port);
        string? token = CompelManageHandler.TryResolveAuthToken();
        string payloadJson = JsonSerializer.Serialize(new { command });

        foreach (int compelPort in candidatePorts)
        {
            if (!_hasCustomHttpClient && !IsLocalTcpPortListening(compelPort))
            {
                continue;
            }

            string endpoint = $"http://127.0.0.1:{compelPort}/api/instances/{port}/command";
            try
            {
                using var cts = new CancellationTokenSource(600);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(token))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                using HttpResponseMessage resp = await _httpClient.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                string body = await resp.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                string subChannel = "stdin";
                string msg = "OK";
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String)
                    {
                        subChannel = chEl.GetString() ?? "stdin";
                    }
                    if (doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                    {
                        msg = msgEl.GetString() ?? "OK";
                    }
                }
                catch { }

                string fullChannel = compelPort == 11334 ? $"compel-docker-http:{subChannel}" : $"compel-http:{subChannel}";
                _logger($"[DedicatedServer:{port}] Dispatched '{command}' via COMPEL HTTP ({endpoint}) -> {fullChannel}");
                return (true, fullChannel, endpoint, msg);
            }
            catch (Exception ex)
            {
                _logger($"[DedicatedServer:{port}] COMPEL HTTP dispatch to {endpoint} failed: {ex.Message}");
            }
        }

        return (false, string.Empty, string.Empty, string.Empty);
    }

    private async Task<(bool Success, string Message)> TryDispatchViaDockerContainerAsync(
        int port,
        string command,
        CancellationToken cancellationToken)
    {
        string[] candidateContainers = port is >= 11485 and <= 11534
            ? ["kongor-stack-compel-arcade", "arcade-compel-server", "compel-queue-arcade-server", "kongor-stack-compel-dedicated"]
            : ["kongor-stack-compel-dedicated", "compel-dedicated-server", "ranked-compel-server", "kongor-stack-compel-arcade"];

        string escaped = command.Replace("'", "'\\''");
        foreach (string containerName in candidateContainers)
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "docker",
                    Arguments = $"exec {containerName} sh -c \"for p in /proc/[0-9]*/cmdline; do if tr '\\0' ' ' < \\\"$p\\\" 2>/dev/null | grep -q '{port}'; then pid=$(echo \\\"$p\\\" | cut -d/ -f3); printf '%s\\n' '{escaped}' > /proc/$pid/fd/0 && echo OK:$pid && exit 0; fi; done; exit 1\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using Process? proc = Process.Start(psi);
                if (proc == null) continue;

                using var cts = new CancellationTokenSource(1500);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                string stdout = await proc.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);

                if (proc.ExitCode == 0 && stdout.Contains("OK:", StringComparison.Ordinal))
                {
                    _logger($"[DedicatedServer:{port}] Dispatched command '{command}' via docker exec {containerName} ({stdout.Trim()}).");
                    return (true, stdout.Trim());
                }
            }
            catch (Exception ex)
            {
                _logger($"[DedicatedServer:{port}] Docker exec fallback on '{containerName}' failed: {ex.Message}");
            }
        }

        return (false, string.Empty);
    }

    private static bool IsDedicatedServerProcess(Process process, string? cmdLine)
    {
        if (!string.IsNullOrEmpty(cmdLine))
        {
            if (cmdLine.Contains("-dedicated", StringComparison.OrdinalIgnoreCase) ||
                cmdLine.Contains("-server", StringComparison.OrdinalIgnoreCase) ||
                cmdLine.Contains("svr_port", StringComparison.OrdinalIgnoreCase) ||
                cmdLine.Contains("-manager", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (cmdLine.Contains("willowmaker-ipc", StringComparison.OrdinalIgnoreCase) ||
                cmdLine.Contains(@"\games\client\", StringComparison.OrdinalIgnoreCase) ||
                cmdLine.Contains("/games/client/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Headless hon_x64 process with no window handle (when WMI command line is unavailable)
        if (string.IsNullOrEmpty(cmdLine) &&
            process.MainWindowHandle == IntPtr.Zero &&
            string.IsNullOrEmpty(process.MainWindowTitle))
        {
            return true;
        }

        return false;
    }

    public static int? ExtractPortFromCommandLine(string? cmdLine)
    {
        if (string.IsNullOrEmpty(cmdLine)) return null;

        Match m = Regex.Match(cmdLine, @"(?:-port|port:|svr_port|startServerPort|hon-server-native-|hon-server-)[\s=""]*([0-9]{4,5})", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int port) && port is > 0 and <= 65535)
        {
            return port;
        }

        return null;
    }

    public static int? ExtractTickrateFromCommandLine(string? cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine)) return null;

        Match m = Regex.Match(cmdLine, @"(?:svr_gameFPS|svr_fps)[\s=""]+([0-9]+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int fps))
        {
            return Math.Clamp(fps, 20, 60);
        }

        return null;
    }

    private static string? ExtractServerNameFromCommandLine(string? cmdLine)
    {
        if (string.IsNullOrEmpty(cmdLine)) return null;

        Match m = Regex.Match(cmdLine, @"(?:svr_name)[\s=""]+([^;""+]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        return null;
    }

    private static DateTimeOffset TryGetProcessStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
        }
        catch
        {
            return TimeProvider.System.GetUtcNow();
        }
    }

    private static string? TryGetProcessFileName(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> BuildDedicatedServerArguments(
        string gameDir,
        int port,
        string masterServer,
        string serverName,
        string map,
        int tickrate = ClientModMountBuilder.CanonicalSimulationTickRate)
    {
        string normalizedMasterServer = GameLauncherService.NormalizeMasterServerAddress(masterServer);
        int clampedTickrate = Math.Clamp(tickrate, 20, 60);
        List<string> args =
        [
            "-dedicated",
            $"-masterserver {normalizedMasterServer}",
            $"-webserver {normalizedMasterServer}",
            $"-messageserver {normalizedMasterServer}"
        ];

        // VFS Mod Mounts Chain (bottom-to-top of K2 m_vModStack): base;game;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions;override;server
        List<string> modResources = ["base", "game"];

        string sharedRel = "../shared";
        string sharedAbs = Path.GetFullPath(Path.Combine(gameDir, sharedRel));
        if (Directory.Exists(sharedAbs))
        {
            modResources.Add(sharedRel);
        }
        else if (Directory.Exists(Path.Combine(gameDir, "shared")))
        {
            modResources.Add("shared");
        }
        else
        {
            modResources.Add("../shared");
        }

        modResources.Add("KONGOR/configuration");
        modResources.Add("KONGOR/updates");
        modResources.Add("KONGOR/extensions");
        modResources.Add("override");
        modResources.Add("server");

        args.Add($"-mod {string.Join(";", modResources)}");

        // Port-Specific Named Pipe IPC
        args.Add($"-pipe hon-server-{port}");

        // Dedicated Server Port & Details
        args.Add($"-port {port}");
        args.Add($"-voiceport {port + 200}");
        args.Add($"+Set svr_port {port}");
        args.Add($"+Set svr_voicePort {port + 200}");
        args.Add($"+Set svr_name \"{serverName}\"");
        args.Add("+Set svr_location \"\"");

        // Zero-Lag 128-Tick & Uncapped Performance
        args.Add("+Set host_maxFPS 1000");
        args.Add("+Set svr_maxFPS 1000");
        args.Add("+Set net_maxPacketRate 128");
        args.Add("+Set cl_packetEpsilon 0.001");
        args.Add("+Set sys_sleepTime 0");
        args.Add("+Set mem_maxHeapSize 4096");

        // Variable Simulation Tickrate (20-60 Hz) & Anti-Stall Frame Headroom
        args.Add($"+Set svr_gameFPS {clampedTickrate}");
        args.Add("+Set svr_maxFramesPerHostFrame 5");

        // Logging & Diagnostics
        args.Add("-writeconsole");
        args.Add($"-logfile server_{port}.log");
        args.Add("+Set con_log 1");
        args.Add("+Set host_writeConsole true");
        args.Add("+Set log_flush false");
        args.Add("+Set con_flushLog false");
        args.Add("+Set sys_flushLogs false");

        // Map Startup (Dedicated servers host games via 'CreateGame <world> map:<world> allheroes:true' rather than 'StartGame')
        if (!string.IsNullOrWhiteSpace(map))
        {
            string cleanMap = map.Trim();
            if (!cleanMap.StartsWith("forests_of_", StringComparison.OrdinalIgnoreCase) && cleanMap.Equals("caldavar", StringComparison.OrdinalIgnoreCase))
            {
                cleanMap = "caldavar";
            }
            args.Add($"+CreateGame {cleanMap} map:{cleanMap} allheroes:true");
        }

        return args.AsReadOnly();
    }

    private static int EnsurePortAvailable(int startPort)
    {
        HashSet<int> activePorts = [];
        try
        {
            IPGlobalProperties ipProps = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in ipProps.GetActiveTcpListeners())
            {
                activePorts.Add(ep.Port);
            }
            foreach (var ep in ipProps.GetActiveUdpListeners())
            {
                activePorts.Add(ep.Port);
            }
        }
        catch { }

        int port = startPort;
        while (activePorts.Contains(port))
        {
            port++;
        }

        return port;
    }

    public async Task<ToolCallResult> GetServerLogsAsync(
        int port,
        int? processId,
        int maxLines,
        string level,
        string? customWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string? baseDir = customWorkingDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            var targetWorker = _trackedWorkers.Values.FirstOrDefault(w => w.Port == port)
                ?? DiscoverRunningDedicatedServers().FirstOrDefault(w => w.Port == port);
            if (targetWorker != null && !string.IsNullOrWhiteSpace(targetWorker.WorkingDirectory) && Directory.Exists(targetWorker.WorkingDirectory))
            {
                baseDir = targetWorker.WorkingDirectory;
            }
            else
            {
                baseDir = Path.Combine(FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory), "games", "server");
            }
        }

        List<string> candidateLogDirs =
        [
            Path.Combine(baseDir, "userdataKONGOR", "configuration"),
            Path.Combine(baseDir, "userdataKONGOR", "logs"),
            Path.Combine(baseDir, "userdataKONGOR", "extensions", "logs"),
            Path.Combine(baseDir, "userdata", "game", "logs"),
            Path.Combine(baseDir, "logs"),
            Path.Combine(baseDir, "COMPEL", "logs"),
            Path.Combine(baseDir, "game", "logs"),
            Path.Combine(baseDir, "game"),
            baseDir
        ];

        List<FileInfo> candidateFiles = [];
        foreach (string dir in candidateLogDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                DirectoryInfo di = new(dir);
                candidateFiles.AddRange(di.GetFiles("*.log", SearchOption.TopDirectoryOnly));
            }
            catch
            {
                // Suppress access issues
            }
        }

        // Filter or prioritize files matching port or processId if available
        string portStr = port.ToString();
        var prioritizedLogs = candidateFiles
            .Where(f => !f.Name.Equals("debug.log", StringComparison.OrdinalIgnoreCase) &&
                        !f.Name.EndsWith("_d3d9.log", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f =>
            {
                if (f.Name.Equals($"server_{port}.log", StringComparison.OrdinalIgnoreCase)) return 100;
                if (f.Name.Contains(portStr, StringComparison.OrdinalIgnoreCase)) return 90;
                if (f.Name.Equals("console.log", StringComparison.OrdinalIgnoreCase)) return 80;
                if (f.Name.StartsWith("Slave-", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("M", StringComparison.OrdinalIgnoreCase)) return 70;
                if (f.Name.StartsWith("server", StringComparison.OrdinalIgnoreCase)) return 60;
                return 10;
            })
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .Take(5)
            .ToList();

        var sortedLogs = prioritizedLogs.Count > 0
            ? prioritizedLogs
            : candidateFiles.OrderByDescending(f => f.LastWriteTimeUtc).Take(5).ToList();

        List<ServerLogEntryDto> resultLines = [];

        foreach (FileInfo fi in sortedLogs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                using FileStream fs = new(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(fs);

                List<string> fileLines = [];
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        fileLines.Add(line);
                }

                int start = Math.Max(0, fileLines.Count - (maxLines * 4));
                for (int i = start; i < fileLines.Count; i++)
                {
                    string raw = fileLines[i];
                    string parsedLevel = ParseServerLogLevel(raw);
                    if (MatchesSeverityFilter(parsedLevel, level))
                    {
                        resultLines.Add(new ServerLogEntryDto
                        {
                            Source = fi.Name,
                            Level = parsedLevel,
                            Message = raw
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Could not read log '{fi.FullName}': {ex.Message}");
            }
        }

        // Fallback to ring buffer if no files returned
        if (resultLines.Count == 0 && !_recentServerOutput.IsEmpty)
        {
            string[] ringLines = _recentServerOutput.ToArray();
            int start = Math.Max(0, ringLines.Length - (maxLines * 4));
            for (int i = start; i < ringLines.Length; i++)
            {
                string raw = ringLines[i];
                string parsedLevel = ParseServerLogLevel(raw);
                if (MatchesSeverityFilter(parsedLevel, level))
                {
                    resultLines.Add(new ServerLogEntryDto
                    {
                        Source = "stdout-buffer",
                        Level = parsedLevel,
                        Message = raw
                    });
                }
            }
        }

        var finalLines = resultLines.TakeLast(maxLines).ToList();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "logs",
            port,
            processId,
            level,
            workingDirectory = baseDir,
            discoveredLogFiles = sortedLogs.Select(f => f.Name).ToArray(),
            totalLines = finalLines.Count,
            lines = finalLines,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private static string ParseServerLogLevel(string text)
    {
        if (text.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UnhandledException", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Crash", StringComparison.OrdinalIgnoreCase))
            return "FATAL";

        if (text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Exception:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fail:", StringComparison.OrdinalIgnoreCase))
            return "ERROR";

        if (text.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("warning:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("warn:", StringComparison.OrdinalIgnoreCase))
            return "WARN";

        return "INFO";
    }

    private static bool MatchesSeverityFilter(string actualLevel, string filterLevel)
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

    private static string FindRepoRoot(string startingDir)
    {
        string[] candidateStarts =
        [
            startingDir,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        ];

        foreach (string start in candidateStarts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            try
            {
                DirectoryInfo? current = new(start);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                        File.Exists(Path.Combine(current.FullName, "PROJECT.md")) ||
                        Directory.Exists(Path.Combine(current.FullName, ".git")))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            catch { }
        }

        return startingDir;
    }
}

/// <summary>
/// Individual structured log entry returned by kongor_game_server_manage action 'logs'.
/// </summary>
public sealed class ServerLogEntryDto
{
    public string Source { get; set; } = string.Empty;
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
}
