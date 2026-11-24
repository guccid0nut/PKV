// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NEXUS.Contracts.Ipc;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services.IPC;

public interface IWillowmakerIpcService : IAsyncDisposable
{
    string PipeName { get; }
    bool IsRunning { get; }
    bool IsListening { get; }
    int ConnectedClientsCount { get; }
    IpcSandboxMode SandboxMode { get; set; }
    string? AuthKey { get; set; }
    event Action<string>? OnLogStreamReceived;
    event Action<EngineStateDto>? OnEngineStateReceived;
    event Action<bool>? OnClientConnectionChanged;
    Func<CockpitEndpointSyncRequestDto, Task<CockpitEndpointSyncResponseDto>>? OnEndpointSyncRequested { get; set; }
    Func<Task<bool>>? OnBringToFrontRequested { get; set; }
    Func<CockpitLauncherStatusDto>? OnLauncherStatusRequested { get; set; }
    Task StartServerAsync(CancellationToken cancellationToken = default);
    Task EnsureListeningAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default);
    Task StopServerAsync();
    Task BroadcastAsync(WillowmakerIpcMessage message, CancellationToken cancellationToken = default);
    Task BroadcastHotReloadAsync(HotReloadManifest manifest, CancellationToken cancellationToken = default);

    Task<string> ExecuteRemoteCvarAsync(string command, int timeoutMs = 2000,
        CancellationToken cancellationToken = default);

    Task<EngineStateDto?> QueryEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default);
}

public sealed class WillowmakerIpcService : IWillowmakerIpcService
{
    public const string DefaultPipeName = "willowmaker-ipc";
    private readonly Lock _clientsLock = new();
    private readonly List<NamedPipeServerStream> _connectedClients = [];
    private readonly ConcurrentDictionary<NamedPipeServerStream, bool> _authenticatedClients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _logAction;
    private readonly ILogger<WillowmakerIpcService>? _logger;
    private readonly Dictionary<int, TaskCompletionSource<WillowmakerIpcMessage?>> _pendingRequests = [];
    private readonly Dictionary<int, NamedPipeServerStream> _pendingRequestClients = [];
    private readonly Lock _pendingRequestsLock = new();
    private readonly IpcSecurityValidator _validator;

    private bool _isDisposed;

    private Task? _listenerTask;
    private int _requestIdCounter = 1;

    public string PipeName { get; }
    public bool IsRunning => _listenerTask != null && !_cts.IsCancellationRequested;
    public bool IsListening { get; private set; }

    public IpcSandboxMode SandboxMode { get; set; }
    public string? AuthKey { get; set; }
    public TimeProvider TimeProvider { get; }

    public WillowmakerIpcService(
        string? pipeName = null,
        ILogger<WillowmakerIpcService>? logger = null,
        Action<string>? logAction = null,
        string? authKey = null,
        IpcSandboxMode? sandboxMode = null,
        TimeProvider? timeProvider = null)
    {
        PipeName = pipeName ?? DefaultPipeName;
        _logger = logger;
        _logAction = logAction;
        TimeProvider = timeProvider ?? TimeProvider.System;
        _validator = new IpcSecurityValidator(timeProvider: TimeProvider);

        AuthKey = authKey ?? Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
        SandboxMode = sandboxMode ?? IpcSandboxMode.Enforced;
    }

    public int ConnectedClientsCount
    {
        get
        {
            lock (_clientsLock)
            {
                return _connectedClients.Count;
            }
        }
    }

    public event Action<string>? OnLogStreamReceived;
    public event Action<EngineStateDto>? OnEngineStateReceived;
    public event Action<bool>? OnClientConnectionChanged;
    public Func<CockpitEndpointSyncRequestDto, Task<CockpitEndpointSyncResponseDto>>? OnEndpointSyncRequested { get; set; }
    public Func<Task<bool>>? OnBringToFrontRequested { get; set; }
    public Func<CockpitLauncherStatusDto>? OnLauncherStatusRequested { get; set; }

    /// <summary>
    /// Wraps an arbitrary payload inside a Lua long-bracket literal (<c>[===[...]===]</c>) whose
    /// <c>=</c> delimiter level is strictly greater than any <c>]=*]</c> sequence appearing in <paramref name="payload"/>,
    /// preventing Lua long-string breakout injection.
    /// </summary>
    public static string WrapLuaLongString(string payload, int minEquals = 4)
    {
        string safePayload = payload ?? string.Empty;
        string body = safePayload.StartsWith('\n') || safePayload.StartsWith('\r')
            ? "\n" + safePayload
            : safePayload;

        int eqCount = Math.Max(0, minEquals);
        while (true)
        {
            string eqRun = new string('=', eqCount);
            string closingDelimiter = $"]{eqRun}]";
            // Guard against both interior occurrences and trailing ']' followed by closing delimiter
            string candidateWithClosing = body + closingDelimiter;
            int firstClose = candidateWithClosing.IndexOf(closingDelimiter, StringComparison.Ordinal);
            if (firstClose == body.Length)
            {
                return $"[{eqRun}[{body}]{eqRun}]";
            }

            eqCount++;
        }
    }

    public Task StartServerAsync(CancellationToken cancellationToken = default)
    {
        if (_listenerTask != null) return Task.CompletedTask;

        LogInfo($"Starting Named Pipe IPC server on pipe: \\\\.\\pipe\\{PipeName} (SandboxMode={SandboxMode})");
        _listenerTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task EnsureListeningAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            await StartServerAsync(cancellationToken).ConfigureAwait(false);
        }

        int elapsed = 0;
        while (!IsListening && elapsed < timeoutMs)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            elapsed += 25;
        }
    }

    public async Task BroadcastAsync(WillowmakerIpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] serialized = WillowmakerIpcMessage.Serialize(message);
        List<NamedPipeServerStream> activeClients;

        lock (_clientsLock)
        {
            activeClients = [.. _connectedClients];
        }

        foreach (NamedPipeServerStream client in activeClients)
            if (client.IsConnected)
                try
                {
                    await client.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                    await client.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogWarning($"Failed to write broadcast message ({message.OpCode}) to client stream: {ex.Message}",
                        ex);
                }
    }

    public async Task BroadcastHotReloadAsync(HotReloadManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        WillowmakerIpcMessage message = WillowmakerIpcMessage.CreateJson(IpcOpCode.HotReloadMod, manifest, WillowmakerJsonSerializerContext.Default.HotReloadManifest);
        await BroadcastAsync(message, cancellationToken).ConfigureAwait(false);
        LogInfo($"Dispatched live hot-reload IPC notification for {manifest.VfsPath} (Valid={manifest.IsValid})");
    }

    public async Task<string> ExecuteRemoteCvarAsync(string command, int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        NamedPipeServerStream? targetClient = GetActiveClient();
        if (targetClient == null || !targetClient.IsConnected)
            throw new InvalidOperationException("Cannot execute remote cvar: no game client connected to IPC pipe.");

        int requestId = Interlocked.Increment(ref _requestIdCounter);
        TaskCompletionSource<WillowmakerIpcMessage?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingRequestsLock)
        {
            _pendingRequests[requestId] = tcs;
            _pendingRequestClients[requestId] = targetClient;
        }

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using CancellationTokenRegistration reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            WillowmakerIpcMessage request = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, command, (uint)requestId);
            byte[] serialized = WillowmakerIpcMessage.Serialize(request);

            await targetClient.WriteAsync(serialized, linkedCts.Token).ConfigureAwait(false);
            await targetClient.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            WillowmakerIpcMessage? response = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            if (response == null) return "ERR: No response payload received";

            return response.PayloadString;
        }
        finally
        {
            lock (_pendingRequestsLock)
            {
                _pendingRequests.Remove(requestId);
                _pendingRequestClients.Remove(requestId);
            }
        }
    }

    public async Task<EngineStateDto?> QueryEngineStateAsync(int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        NamedPipeServerStream? targetClient = GetActiveClient();
        if (targetClient == null || !targetClient.IsConnected) return null;

        int requestId = Interlocked.Increment(ref _requestIdCounter);
        TaskCompletionSource<WillowmakerIpcMessage?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingRequestsLock)
        {
            _pendingRequests[requestId] = tcs;
            _pendingRequestClients[requestId] = targetClient;
        }

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using CancellationTokenRegistration reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            WillowmakerIpcMessage request = WillowmakerIpcMessage.CreateString(IpcOpCode.GetEngineState, "ALL", (uint)requestId);
            byte[] serialized = WillowmakerIpcMessage.Serialize(request);

            await targetClient.WriteAsync(serialized, linkedCts.Token).ConfigureAwait(false);
            await targetClient.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            WillowmakerIpcMessage? response = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            if (response == null) return null;

            return response.ReadJson(WillowmakerJsonSerializerContext.Default.EngineStateDto);
        }
        finally
        {
            lock (_pendingRequestsLock)
            {
                _pendingRequests.Remove(requestId);
                _pendingRequestClients.Remove(requestId);
            }
        }
    }

    public async Task StopServerAsync()
    {
        _cts.Cancel();

        lock (_clientsLock)
        {
            foreach (NamedPipeServerStream client in _connectedClients)
                try
                {
                    client.Dispose();
                }
                catch
                {
                    // Suppress disposal exception
                }

            _connectedClients.Clear();
        }

        lock (_pendingRequestsLock)
        {
            foreach (KeyValuePair<int, TaskCompletionSource<WillowmakerIpcMessage?>> kvp in _pendingRequests)
                kvp.Value.TrySetCanceled();
            _pendingRequests.Clear();
            _pendingRequestClients.Clear();
        }

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch
            {
                // Expected cancellation
            }

            _listenerTask = null;
        }

        LogInfo("Named Pipe IPC Server stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        await StopServerAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void LogInfo(string message)
    {
        _logger?.LogInformation("[WillowmakerIPC] {Message}", message);
        _logAction?.Invoke($"[IPC] {message}");
    }

    private void LogWarning(string message, Exception? ex = null)
    {
        if (ex != null)
            _logger?.LogWarning(ex, "[WillowmakerIPC] {Message}", message);
        else
            _logger?.LogWarning("[WillowmakerIPC] {Message}", message);
        _logAction?.Invoke($"[IPC WARN] {message}");
    }

    private NamedPipeServerStream? GetActiveClient()
    {
        lock (_clientsLock)
        {
            for (int i = 0; i < _connectedClients.Count; i++)
            {
                NamedPipeServerStream client = _connectedClients[i];
                if (client.IsConnected) return client;
            }
        }

        return null;
    }

    private async Task AcceptClientsLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    PipeSecurity pipeSecurity = new();
                    using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
                    if (currentIdentity.User != null)
                    {
                        pipeSecurity.AddAccessRule(new PipeAccessRule(
                            currentIdentity.User,
                            PipeAccessRights.FullControl,
                            AccessControlType.Allow));
                    }

                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));

                    serverStream = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                        64 * 1024,
                        64 * 1024,
                        pipeSecurity);
                }
                else
                {
                    serverStream = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                    HardenUnixPipeSocketPermissions(PipeName);
                }

                IsListening = true;
                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                IsListening = false;

                lock (_clientsLock)
                {
                    _connectedClients.Add(serverStream);
                }

                LogInfo(
                    $"Game client connected to pipe \\\\.\\pipe\\{PipeName}. Total active clients: {_connectedClients.Count}");
                OnClientConnectionChanged?.Invoke(true);

                _ = Task.Run(() => HandleClientAsync(serverStream, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                IsListening = false;
                serverStream?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                IsListening = false;
                serverStream?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    LogWarning($"Error accepting named pipe client connection: {ex.Message}", ex);
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        IsListening = false;
    }

    private void HardenUnixPipeSocketPermissions(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string socketFileName = pipeName.StartsWith('/') ? pipeName : Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");
        try
        {
            if (File.Exists(socketFileName))
            {
                File.SetUnixFileMode(socketFileName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                LogInfo($"Hardened Unix IPC domain socket permissions (0600) on '{socketFileName}'.");
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Could not enforce 0600 Unix domain socket mode on '{socketFileName}': {ex.Message}", ex);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream clientStream, CancellationToken cancellationToken)
    {
        byte[] readBuffer = new byte[64 * 1024];
        using MemoryStream accumulatedBuffer = new();

        try
        {
            while (clientStream.IsConnected && !cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                int bytesRead = await clientStream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                accumulatedBuffer.Write(readBuffer, 0, bytesRead);
                if (accumulatedBuffer.Length > WillowmakerIpcMessage.MaxPayloadSize + WillowmakerIpcMessage.HeaderSize)
                    throw new InvalidDataException($"IPC incoming buffer overflow: {accumulatedBuffer.Length} bytes exceeds limit.");

                byte[] currentBytes = accumulatedBuffer.ToArray();
                int offset = 0;

                while (offset < currentBytes.Length)
                {
                    ReadOnlySpan<byte> remainingSpan = currentBytes.AsSpan(offset);
                    if (WillowmakerIpcMessage.TryDeserializeAny(remainingSpan, out WillowmakerIpcMessage? message,
                            out int bytesConsumed))
                    {
                        offset += bytesConsumed;
                        if (message != null)
                            await ProcessIncomingMessageAsync(clientStream, message, cancellationToken)
                                .ConfigureAwait(false);
                    }
                    else
                    {
                        break; // Wait for full frame or line delimiter
                    }
                }

                if (offset > 0)
                {
                    byte[] unreadBytes = currentBytes.AsSpan(offset).ToArray();
                    accumulatedBuffer.SetLength(0);
                    if (unreadBytes.Length > 0) accumulatedBuffer.Write(unreadBytes, 0, unreadBytes.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            LogWarning($"Client stream read encountered error: {ex.Message}", ex);
        }
        finally
        {
            lock (_clientsLock)
            {
                _connectedClients.Remove(clientStream);
            }

            _authenticatedClients.TryRemove(clientStream, out _);

            await clientStream.DisposeAsync().ConfigureAwait(false);
            LogInfo(
                $"Game client disconnected from pipe \\\\.\\pipe\\{PipeName}. Remaining clients: {ConnectedClientsCount}");
            OnClientConnectionChanged?.Invoke(ConnectedClientsCount > 0);

            lock (_pendingRequestsLock)
            {
                List<int> toFault = [];
                foreach (KeyValuePair<int, TaskCompletionSource<WillowmakerIpcMessage?>> entry in _pendingRequests)
                {
                    if (!entry.Value.Task.IsCompleted)
                    {
                        if (ConnectedClientsCount == 0 || (_pendingRequestClients.TryGetValue(entry.Key, out NamedPipeServerStream? target) && target == clientStream))
                        {
                            entry.Value.TrySetException(new IOException("Game client disconnected from IPC pipe before responding."));
                            toFault.Add(entry.Key);
                        }
                    }
                }

                foreach (int id in toFault)
                {
                    _pendingRequestClients.Remove(id);
                }
            }
        }
    }

    private async Task ProcessIncomingMessageAsync(NamedPipeServerStream clientStream, WillowmakerIpcMessage message,
        CancellationToken cancellationToken)
    {
        // 1. Handle Authenticate handshake opcode
        if (message.OpCode == IpcOpCode.Authenticate)
        {
            IpcAuthResponseDto authResponse;
            if (string.IsNullOrWhiteSpace(AuthKey))
            {
                authResponse = new IpcAuthResponseDto
                {
                    Authenticated = false,
                    Message = "Server has no authentication key configured."
                };
            }
            else
            {
                IpcAuthPayload? payload = message.ReadJson(WillowmakerJsonSerializerContext.Default.IpcAuthPayload);
                IpcAuthResult result = _validator.ValidateHandshake(AuthKey, payload);
                if (result.IsSuccess)
                {
                    _authenticatedClients[clientStream] = true;
                    authResponse = new IpcAuthResponseDto
                    {
                        Authenticated = true,
                        Message = "Session authenticated successfully for actuation."
                    };
                    LogInfo($"[AUTH] Client '{payload?.ClientName}' successfully authenticated session.");
                }
                else
                {
                    authResponse = new IpcAuthResponseDto
                    {
                        Authenticated = false,
                        Message = result.ErrorMessage ?? "Authentication failed."
                    };
                    LogWarning($"[AUTH] Authentication failed: {result.ErrorMessage}");
                }
            }

            WillowmakerIpcMessage respMsg = WillowmakerIpcMessage.CreateJson(IpcOpCode.AuthResponse, authResponse, WillowmakerJsonSerializerContext.Default.IpcAuthResponseDto, message.RequestId);
            byte[] respBytes = WillowmakerIpcMessage.Serialize(respMsg);
            await clientStream.WriteAsync(respBytes, cancellationToken).ConfigureAwait(false);
            await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // 2. Enforce sandboxing on mutating/actuation opcodes
        if (IpcSecurityValidator.IsMutatingOpCode((ushort)message.OpCode))
        {
            bool isAuthenticated = _authenticatedClients.TryGetValue(clientStream, out bool auth) && auth;

            // Check if message carries an ad-hoc signed envelope
            if (!isAuthenticated && !string.IsNullOrWhiteSpace(AuthKey))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(message.Payload);
                    if (doc.RootElement.TryGetProperty("_auth", out JsonElement authEl))
                    {
                        IpcAuthHeader? header = JsonSerializer.Deserialize(authEl.GetRawText(), WillowmakerJsonSerializerContext.Default.IpcAuthHeader);
                        string command = doc.RootElement.TryGetProperty("command", out JsonElement cmdEl)
                            ? cmdEl.GetString() ?? string.Empty
                            : message.PayloadString;

                        IpcAuthResult cmdResult = _validator.ValidateCommand(AuthKey, command, header);
                        if (cmdResult.IsSuccess)
                        {
                            isAuthenticated = true;
                        }
                    }
                }
                catch
                {
                    // Ignore non-JSON payload parse errors
                }
            }

            if (!isAuthenticated)
            {
                bool isCockpitWindowSyncOp = message.OpCode is IpcOpCode.SyncEndpoints or IpcOpCode.BringToFront;
                if (isCockpitWindowSyncOp && string.IsNullOrWhiteSpace(AuthKey))
                {
                    // Same-user OS PipeSecurity SID ACL / 0600 Unix mode already restricts connection to CurrentUser when no session HMAC key is active.
                    LogInfo($"[CockpitIPC] Permitted same-user ACL-guarded opcode '{message.OpCode}' (no explicit HMAC session key configured).");
                }
                else if (SandboxMode == IpcSandboxMode.Enforced)
                {
                    LogWarning($"[SECURITY] Blocked unauthenticated mutating opcode '{message.OpCode}' in Enforced sandbox mode.");
                    WillowmakerIpcMessage errorResp = message.OpCode switch
                    {
                        IpcOpCode.SyncEndpoints => WillowmakerIpcMessage.CreateJson(
                            IpcOpCode.SyncEndpointsResponse,
                            new CockpitEndpointSyncResponseDto
                            {
                                Success = false,
                                ProcessId = Environment.ProcessId,
                                Message = "[SECURITY ERROR] 401 Unauthorized: Operation requires HMAC-SHA256 authentication."
                            },
                            WillowmakerJsonSerializerContext.Default.CockpitEndpointSyncResponseDto,
                            message.RequestId),
                        IpcOpCode.BringToFront => WillowmakerIpcMessage.CreateString(
                            IpcOpCode.BringToFrontResponse,
                            "ERR: 401 Unauthorized",
                            message.RequestId),
                        _ => WillowmakerIpcMessage.CreateString(
                            IpcOpCode.CommandResponse,
                            "[SECURITY ERROR] 401 Unauthorized: Operation requires HMAC-SHA256 authentication.",
                            message.RequestId)
                    };
                    byte[] errBytes = WillowmakerIpcMessage.Serialize(errorResp);
                    await clientStream.WriteAsync(errBytes, cancellationToken).ConfigureAwait(false);
                    await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                else if (SandboxMode == IpcSandboxMode.Permissive)
                {
                    LogWarning($"[AUDIT] Unauthenticated mutating opcode '{message.OpCode}' permitted in Permissive sandbox mode.");
                }
            }
        }

        switch (message.OpCode)
        {
            case IpcOpCode.Ping:
                WillowmakerIpcMessage pong = WillowmakerIpcMessage.CreateString(IpcOpCode.Pong, "PONG", message.RequestId);
                byte[] pongBytes = WillowmakerIpcMessage.Serialize(pong);
                await clientStream.WriteAsync(pongBytes, cancellationToken).ConfigureAwait(false);
                await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;

            case IpcOpCode.SyncEndpoints:
                {
                    CockpitEndpointSyncResponseDto syncResponse;
                    try
                    {
                        CockpitEndpointSyncRequestDto? req = message.ReadJson(WillowmakerJsonSerializerContext.Default.CockpitEndpointSyncRequestDto);
                        if (req is null)
                        {
                            syncResponse = new CockpitEndpointSyncResponseDto
                            {
                                Success = false,
                                ProcessId = Environment.ProcessId,
                                Message = "Invalid or empty CockpitEndpointSyncRequestDto payload."
                            };
                            LogWarning("[CockpitIPC] Rejected SyncEndpoints request: null/invalid JSON payload.");
                        }
                        else if (OnEndpointSyncRequested is null)
                        {
                            syncResponse = new CockpitEndpointSyncResponseDto
                            {
                                Success = false,
                                ProcessId = Environment.ProcessId,
                                Message = "Launcher endpoint sync handler is not registered."
                            };
                            LogWarning("[CockpitIPC] SyncEndpoints received while OnEndpointSyncRequested handler is unbound.");
                        }
                        else
                        {
                            LogInfo($"[CockpitIPC] Processing SyncEndpoints from '{req.Source}' (Master='{req.MasterServerEndpoint}', CDN='{req.CdnEndpoint}', BringToFront={req.BringToFront})");
                            syncResponse = await OnEndpointSyncRequested(req).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"[CockpitIPC] SyncEndpoints handler threw exception: {ex.Message}", ex);
                        syncResponse = new CockpitEndpointSyncResponseDto
                        {
                            Success = false,
                            ProcessId = Environment.ProcessId,
                            Message = $"SyncEndpoints exception: {ex.Message}"
                        };
                    }

                    WillowmakerIpcMessage respFrame = WillowmakerIpcMessage.CreateJson(
                        IpcOpCode.SyncEndpointsResponse,
                        syncResponse,
                        WillowmakerJsonSerializerContext.Default.CockpitEndpointSyncResponseDto,
                        message.RequestId);
                    byte[] respBytes = WillowmakerIpcMessage.Serialize(respFrame);
                    await clientStream.WriteAsync(respBytes, cancellationToken).ConfigureAwait(false);
                    await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

            case IpcOpCode.BringToFront:
                {
                    bool focused = false;
                    try
                    {
                        if (OnBringToFrontRequested is not null)
                        {
                            focused = await OnBringToFrontRequested().ConfigureAwait(false);
                            LogInfo($"[CockpitIPC] Executed BringToFront request (Focused={focused}).");
                        }
                        else
                        {
                            LogWarning("[CockpitIPC] BringToFront received while OnBringToFrontRequested handler is unbound.");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"[CockpitIPC] BringToFront handler threw exception: {ex.Message}", ex);
                    }

                    WillowmakerIpcMessage focusAck = WillowmakerIpcMessage.CreateString(
                        IpcOpCode.BringToFrontResponse,
                        focused ? "OK" : "UNBOUND",
                        message.RequestId);
                    byte[] focusBytes = WillowmakerIpcMessage.Serialize(focusAck);
                    await clientStream.WriteAsync(focusBytes, cancellationToken).ConfigureAwait(false);
                    await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

            case IpcOpCode.GetLauncherStatus:
                {
                    CockpitLauncherStatusDto status;
                    try
                    {
                        status = OnLauncherStatusRequested?.Invoke() ?? new CockpitLauncherStatusDto
                        {
                            ProcessId = Environment.ProcessId,
                            Platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                        };
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"[CockpitIPC] GetLauncherStatus handler threw exception: {ex.Message}", ex);
                        status = new CockpitLauncherStatusDto
                        {
                            ProcessId = Environment.ProcessId,
                            Platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                        };
                    }

                    WillowmakerIpcMessage statusFrame = WillowmakerIpcMessage.CreateJson(
                        IpcOpCode.LauncherStatusResponse,
                        status,
                        WillowmakerJsonSerializerContext.Default.CockpitLauncherStatusDto,
                        message.RequestId);
                    byte[] statusBytes = WillowmakerIpcMessage.Serialize(statusFrame);
                    await clientStream.WriteAsync(statusBytes, cancellationToken).ConfigureAwait(false);
                    await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

            case IpcOpCode.ExecuteCommand:
                try
                {
                    string cmd = message.PayloadString;
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        string currentDir = Directory.GetCurrentDirectory();
                        string[] candidateDirs = [
                            Path.Combine(currentDir, "games", "client", "userdataKONGOR", "extensions", "nexus_ipc"),
                            Path.Combine(currentDir, "userdataKONGOR", "extensions", "nexus_ipc")
                        ];
                        foreach (string mbDir in candidateDirs)
                        {
                            if (Directory.Exists(mbDir) || Directory.Exists(Path.GetDirectoryName(mbDir)!))
                            {
                                Directory.CreateDirectory(mbDir);
                                int reqId = (int)(message.RequestId > 0 ? message.RequestId : TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds() & 0x3FFFFFFF);
                                string jsonPayload = $"{{\"id\":{reqId},\"type\":\"command\",\"command\":{JsonSerializer.Serialize(cmd, WillowmakerJsonSerializerContext.Default.String)},\"timestamp\":{TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()}}}";
                                string safeLongString = WrapLuaLongString(jsonPayload, minEquals: 4);
                                string luaCode = $"if Nexus and Nexus.Mailbox and Nexus.Mailbox.ProcessFromJson then\n  Nexus.Mailbox.ProcessFromJson({safeLongString})\nend\n";
                                string reqLua = Path.Combine(mbDir, "req_current.lua");
                                await File.WriteAllTextAsync(reqLua, luaCode, cancellationToken).ConfigureAwait(false);
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"Failed to stage IPC mailbox command to req_current.lua: {ex.Message}", ex);
                }

                WillowmakerIpcMessage cmdAck = WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "OK", message.RequestId);
                byte[] ackBytes = WillowmakerIpcMessage.Serialize(cmdAck);
                await clientStream.WriteAsync(ackBytes, cancellationToken).ConfigureAwait(false);
                await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;

            case IpcOpCode.LogStreamEvent:
                OnLogStreamReceived?.Invoke(message.PayloadString);
                break;

            case IpcOpCode.EngineStateResponse:
                EngineStateDto? state = message.ReadJson(WillowmakerJsonSerializerContext.Default.EngineStateDto);
                if (state != null) OnEngineStateReceived?.Invoke(state);
                ResolvePendingResponse(message);
                break;

            case IpcOpCode.CommandResponse:
                ResolvePendingResponse(message);
                break;

            default:
                ResolvePendingResponse(message);
                break;
        }
    }

    private void ResolvePendingResponse(WillowmakerIpcMessage message)
    {
        lock (_pendingRequestsLock)
        {
            if (message.RequestId > 0 && _pendingRequests.TryGetValue((int)message.RequestId, out TaskCompletionSource<WillowmakerIpcMessage?>? reqTcs))
            {
                reqTcs.TrySetResult(message);
                return;
            }

            foreach (KeyValuePair<int, TaskCompletionSource<WillowmakerIpcMessage?>> entry in _pendingRequests)
                if (!entry.Value.Task.IsCompleted)
                {
                    entry.Value.TrySetResult(message);
                    return;
                }
        }
    }
}
