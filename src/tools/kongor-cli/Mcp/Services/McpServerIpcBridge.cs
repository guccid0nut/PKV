// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using NEXUS.Contracts.Ipc;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Server-side Named Pipe IPC bridge implementing <see cref="IGameIpcBridge"/> by wrapping <see cref="IMcpIpcServer"/>.
/// Enables MCP tools to act as a Named Pipe server (\\.\pipe\willowmaker-ipc) that running game client binaries
/// (hon_x64.exe launched with -pipe willowmaker-ipc) connect to as clients.
/// </summary>
public sealed class McpServerIpcBridge : IGameIpcBridge
{
    private readonly IMcpIpcServer _server;
    private readonly Action<string>? _logAction;
    private readonly NexusEventCoordinator _eventCoordinator;
    private bool _isDisposed;

    public string PipeName => _server.PipeName;
    public string ServerName => ".";
    public bool IsConnected => _server.ConnectedClientsCount > 0;
    public bool IsAuthenticated { get; private set; }
    public string? AuthKey { get; set; }

    public IMcpIpcServer Server => _server;

    public event Action? OnConnected;
    public event Action<Exception?>? OnDisconnected;
    public event Action<IpcMessage>? OnMessageReceived;
    public event Action<string>? OnLogStreamReceived;

    public McpServerIpcBridge(
        IMcpIpcServer? server = null,
        Action<string>? logAction = null,
        string? authKey = null,
        bool autoStart = true)
    {
        _logAction = logAction;
        _server = server ?? new McpIpcServer(logger: logAction);
        AuthKey = authKey ?? Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
        _eventCoordinator = new NexusEventCoordinator((cmd, timeout, ct) => ExecuteCommandAsync(cmd, timeout, ct), _logAction);

        _server.OnLogStreamReceived += msg =>
        {
            _eventCoordinator.HandleLogStream(msg);
            OnLogStreamReceived?.Invoke(msg);
        };
        _server.OnMessageReceived += msg => OnMessageReceived?.Invoke(ToIpcMessage(msg));
        _server.OnClientDisconnected += ex =>
        {
            _eventCoordinator.Reset();
            OnDisconnected?.Invoke(ex);
        };
        _server.OnClientConnected += () => OnConnected?.Invoke();

        if (autoStart)
        {
            try
            {
                _ = _server.StartAsync();
            }
            catch (Exception ex)
            {
                _logAction?.Invoke($"[McpServerIpcBridge] Auto-start warning: {ex.Message}");
            }
        }
    }

    public void AttachProcess(Process process)
    {
        _server.AttachProcess(process);
    }

    private void Log(string message) => _logAction?.Invoke(message);

    public async Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (IsConnected) return;

        if (timeoutMs <= 0) return;

        Log($"Awaiting client connection on Named Pipe \\\\.\\pipe\\{PipeName} (Timeout: {timeoutMs}ms)...");

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        TaskCompletionSource connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onClientConnected = () => connectTcs.TrySetResult();

        _server.OnClientConnected += onClientConnected;

        try
        {
            if (IsConnected) return;

            using var reg = linkedCts.Token.Register(() =>
            {
                if (timeoutCts.IsCancellationRequested)
                    connectTcs.TrySetException(new TimeoutException($"No game client connected to Named Pipe '\\\\.\\pipe\\{PipeName}' within {timeoutMs}ms."));
                else
                    connectTcs.TrySetCanceled(linkedCts.Token);
            });

            await connectTcs.Task.ConfigureAwait(false);
            Log($"Game client connected successfully to Named Pipe \\\\.\\pipe\\{PipeName}.");
        }
        finally
        {
            _server.OnClientConnected -= onClientConnected;
        }
    }

    /// <summary>
    /// Guarantees that the underlying Named Pipe IPC server is running and actively awaiting connections.
    /// </summary>
    public async Task EnsureListeningAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        int elapsed = 0;
        while (!_server.IsListening && elapsed < timeoutMs)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            elapsed += 25;
        }
    }

    public async Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        string keyToUse = authKey ?? AuthKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyToUse))
        {
            Log("[AUTH] Cannot authenticate: AuthKey is null or empty.");
            IsAuthenticated = false;
            return false;
        }

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        var payload = IpcSecurityValidator.CreateHandshakePayload(keyToUse, "kongor-cli");
        var authMsg = WillowmakerIpcMessage.CreateJson(NEXUS.Contracts.Ipc.IpcOpCode.Authenticate, payload);

        var response = await _server.SendRequestAsync(authMsg, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (response != null)
        {
            var authResp = response.ReadJson<IpcAuthResponseDto>();
            if (authResp != null && authResp.Authenticated)
            {
                IsAuthenticated = true;
                Log($"[AUTH] Handshake successful: {authResp.Message}");
                return true;
            }

            Log($"[AUTH] Handshake rejected: {authResp?.Message ?? "Unknown authentication failure"}");
        }

        IsAuthenticated = false;
        return false;
    }

    public async Task<string> ExecuteCommandAsync(string command, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        Log($"[McpServerIpcBridge] Dispatching console command: '{command}' (Timeout: {timeoutMs}ms)");

        var req = WillowmakerIpcMessage.CreateString(NEXUS.Contracts.Ipc.IpcOpCode.ExecuteCommand, command);
        var resp = await _server.SendRequestAsync(req, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (resp == null || resp.OpCode != NEXUS.Contracts.Ipc.IpcOpCode.CommandResponse)
        {
            throw new InvalidOperationException($"Invalid or null response received for command '{command}'.");
        }

        string result = resp.PayloadString;
        Log($"[McpServerIpcBridge] Received command response: '{result}'");

        if (result.Contains("[SECURITY ERROR] 401 Unauthorized"))
        {
            Log($"[McpServerIpcBridge] [SECURITY ERROR] Remote host rejected unauthenticated command: '{command}'");
            return "[SECURITY ERROR] 401 Unauthorized: Command execution blocked by Named Pipe sandbox. Please configure 'KONGOR_IPC_AUTH_KEY' to authorize mutating commands.";
        }

        return result;
    }

    public async Task<string> ExecuteLuaAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        string cleanLua = Handlers.ClientEvalLuaHandler.NormalizeRawLua(luaCode);
        if (IsConnected)
        {
            try
            {
                var req = WillowmakerIpcMessage.CreateString(NEXUS.Contracts.Ipc.IpcOpCode.EvalLuaSync, cleanLua);
                var resp = await _server.SendRequestAsync(req, timeoutMs, cancellationToken).ConfigureAwait(false);
                if (resp != null && resp.OpCode == NEXUS.Contracts.Ipc.IpcOpCode.EvalLuaResponse)
                {
                    string payloadStr = resp.PayloadString;
                    if (!string.IsNullOrWhiteSpace(payloadStr) && payloadStr.StartsWith('{'))
                    {
                        try
                        {
                            using JsonDocument doc = JsonDocument.Parse(payloadStr);
                            JsonElement root = doc.RootElement;
                            bool success = root.TryGetProperty("success", out JsonElement succEl) && succEl.ValueKind == JsonValueKind.True;
                            if (success)
                            {
                                return root.TryGetProperty("result", out JsonElement resEl) && resEl.ValueKind == JsonValueKind.String
                                    ? resEl.GetString() ?? "nil"
                                    : "nil";
                            }

                            string errVal = root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.String
                                ? errEl.GetString() ?? "Unknown Lua evaluation error"
                                : "Unknown Lua evaluation error";
                            return $"[ERROR] {errVal}";
                        }
                        catch (Exception parseEx)
                        {
                            Log($"[McpServerIpcBridge] Non-JSON EvalLuaResponse fallback: {parseEx.Message}");
                        }
                    }

                    return payloadStr;
                }
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"[McpServerIpcBridge] EvalLuaSync (0x000E) fallback to ExecuteCommandAsync: {ex.Message}");
            }
        }

        string escaped = cleanLua
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return await ExecuteCommandAsync($"Script \"{escaped}\"", timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EngineStateDto?> GetEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        Log($"[McpServerIpcBridge] Querying engine telemetry state (Timeout: {timeoutMs}ms)...");

        var req = WillowmakerIpcMessage.CreateString(NEXUS.Contracts.Ipc.IpcOpCode.GetEngineState, "ALL");
        var resp = await _server.SendRequestAsync(req, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (resp == null || resp.OpCode != NEXUS.Contracts.Ipc.IpcOpCode.EngineStateResponse)
        {
            throw new InvalidOperationException("Invalid or null telemetry response received from engine.");
        }

        EngineStateDto? state = resp.ReadJson<EngineStateDto>();
        if (state != null)
        {
            Log($"[McpServerIpcBridge] Telemetry received: FPS: {state.CurrentFps:F1}, Memory: {state.MemoryWorkingSetMb}MB, Map: {state.CurrentMap}");
        }

        return state;
    }

    public async Task<long> PingAsync(int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        Stopwatch sw = Stopwatch.StartNew();
        var ping = WillowmakerIpcMessage.CreateString(NEXUS.Contracts.Ipc.IpcOpCode.Ping, "PING");
        var pong = await _server.SendRequestAsync(ping, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (pong == null || pong.OpCode != NEXUS.Contracts.Ipc.IpcOpCode.Pong)
        {
            throw new InvalidOperationException("IPC Ping failed: invalid response received from client.");
        }

        return sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
    }

    public async Task SendMessageAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        await _server.BroadcastAsync(ToWillowmakerMessage(message), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcMessage?> SendRequestAsync(IpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        var willowReq = ToWillowmakerMessage(request);
        var willowResp = await _server.SendRequestAsync(willowReq, timeoutMs, cancellationToken).ConfigureAwait(false);
        return willowResp != null ? ToIpcMessage(willowResp) : null;
    }

    public async Task<string> DispatchUiEventAsync(
        string interfaceName,
        string widgetName,
        string eventType,
        int? eventIndex = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetName);
        interfaceName = string.IsNullOrWhiteSpace(interfaceName) ? "main" : interfaceName;
        eventType = string.IsNullOrWhiteSpace(eventType) ? "click" : eventType;

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        if (IsConnected)
        {
            try
            {
                var payload = new { interfaceName, widgetName, eventType, eventIndex };
                var request = WillowmakerIpcMessage.CreateJson(NEXUS.Contracts.Ipc.IpcOpCode.DispatchUiEvent, payload);
                var response = await _server.SendRequestAsync(request, Math.Min(timeoutMs, 1000), cancellationToken).ConfigureAwait(false);
                if (response != null && response.OpCode == NEXUS.Contracts.Ipc.IpcOpCode.CommandResponse && !string.IsNullOrWhiteSpace(response.PayloadString))
                {
                    if (response.PayloadString.Contains("[SECURITY ERROR] 401 Unauthorized"))
                    {
                        Log($"[SECURITY ERROR] Remote host rejected unauthenticated UI event: {interfaceName}:{widgetName}");
                        return "[SECURITY ERROR] 401 Unauthorized: UI dispatch blocked by Named Pipe sandbox. Please configure 'KONGOR_IPC_AUTH_KEY' to authorize UI actuation.";
                    }

                    return response.PayloadString;
                }
            }
            catch (Exception ex)
            {
                Log($"[McpServerIpcBridge] DispatchUiEvent RPC failed ({interfaceName}:{widgetName}), falling back to script command: {ex.Message}");
            }
        }

        string fallbackLua = NativeClientIpcBridge.BuildSafeUiDispatchLua(interfaceName, widgetName, eventType, eventIndex);
        return await ExecuteLuaAsync(fallbackLua, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
        => _eventCoordinator.WaitForEventAsync(eventName, timeoutMs, cancellationToken);

    public Task<T?> ExecuteRpcAsync<T>(string method, object? parameters = null, int timeoutMs = 5000, CancellationToken cancellationToken = default)
        => _eventCoordinator.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken);

    public async Task<SubTickInputResult> InjectSubTickInputAsync(
        string action,
        long hardwareTimestampQpc = 0,
        uint inputSequence = 0,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        if (IsConnected)
        {
            if (hardwareTimestampQpc == 0)
            {
                hardwareTimestampQpc = Stopwatch.GetTimestamp();
            }

            SubTickInputPayloadDto payload = new()
            {
                Action = action,
                HardwareTimestampQpc = hardwareTimestampQpc,
                InputSequence = inputSequence
            };

            WillowmakerIpcMessage request = WillowmakerIpcMessage.CreateJson(NEXUS.Contracts.Ipc.IpcOpCode.InjectSubTickInput, payload);
            WillowmakerIpcMessage? response = await _server.SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);

            if (response != null && response.OpCode == NEXUS.Contracts.Ipc.IpcOpCode.SubTickInputResponse)
            {
                SubTickResponseDto? dto = response.ReadJson<SubTickResponseDto>();
                if (dto != null)
                {
                    return new SubTickInputResult(dto.Success, dto.InputSequence, dto.MeasuredLatencyMs, dto.TimestampQpc, dto.Message);
                }
            }
        }

        return new SubTickInputResult(false, inputSequence, 0f, 0, "No connected clients to receive sub-tick input.");
    }

    public async Task<bool> PushTelemetryUpdateAsync(
        uint drawCalls,
        uint activeEntities,
        uint particlesAlive,
        uint pingMicroseconds,
        uint serverTick,
        float inputLatencyMs = 0f,
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        if (!_server.IsRunning)
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConnected)
        {
            await ConnectAsync(Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
        }

        if (IsConnected)
        {
            TelemetryUpdatePayloadDto payload = new()
            {
                DrawCalls = drawCalls,
                ActiveEntities = activeEntities,
                ParticlesAlive = particlesAlive,
                PingMicroseconds = pingMicroseconds,
                ServerTick = serverTick,
                InputLatencyMs = inputLatencyMs
            };

            WillowmakerIpcMessage request = WillowmakerIpcMessage.CreateJson(NEXUS.Contracts.Ipc.IpcOpCode.UpdateTelemetryMetrics, payload);
            WillowmakerIpcMessage? response = await _server.SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);

            if (response != null && response.OpCode == NEXUS.Contracts.Ipc.IpcOpCode.TelemetryMetricsResponse)
            {
                TelemetryMetricsResponseDto? dto = response.ReadJson<TelemetryMetricsResponseDto>();
                return dto?.Success ?? false;
            }
        }

        return false;
    }

    public async Task DisconnectAsync()
    {
        _eventCoordinator.Reset();
        await _server.StopAsync().ConfigureAwait(false);
        OnDisconnected?.Invoke(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private static IpcMessage ToIpcMessage(WillowmakerIpcMessage msg) =>
        new()
        {
            OpCode = (IpcOpCode)(ushort)msg.OpCode,
            RequestId = msg.RequestId,
            Payload = msg.Payload
        };

    private static WillowmakerIpcMessage ToWillowmakerMessage(IpcMessage msg) =>
        new()
        {
            OpCode = (NEXUS.Contracts.Ipc.IpcOpCode)(ushort)msg.OpCode,
            RequestId = msg.RequestId,
            Payload = msg.Payload
        };
}
