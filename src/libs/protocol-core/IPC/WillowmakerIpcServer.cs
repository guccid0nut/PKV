// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Ipc;

namespace ASPIRE.Common.IPC;

public interface IWillowmakerIpcServer : IAsyncDisposable
{
    string PipeName { get; }
    bool IsRunning { get; }
    int ConnectedClientsCount { get; }
    IpcSandboxMode SandboxMode { get; set; }
    string? AuthKey { get; set; }
    event Func<WillowmakerIpcMessage, Task<WillowmakerIpcMessage?>>? OnMessageReceived;
    event Action<string>? OnLogStreamReceived;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task BroadcastAsync(WillowmakerIpcMessage message, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public sealed class WillowmakerIpcServer : IWillowmakerIpcServer
{
    public const string DefaultPipeName = "willowmaker-ipc";
    private readonly Lock _clientsLock = new();
    private readonly List<NamedPipeServerStream> _connectedClients = [];
    private readonly ConcurrentDictionary<NamedPipeServerStream, bool> _authenticatedClients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<WillowmakerIpcServer>? _logger;
    private readonly IpcSecurityValidator _validator;

    private bool _isDisposed;
    private Task? _listenerTask;

    public string PipeName { get; }
    public bool IsRunning => _listenerTask != null && !_cts.IsCancellationRequested;

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

    public IpcSandboxMode SandboxMode { get; set; }
    public string? AuthKey { get; set; }
    public TimeProvider TimeProvider { get; }

    public event Func<WillowmakerIpcMessage, Task<WillowmakerIpcMessage?>>? OnMessageReceived;
    public event Action<string>? OnLogStreamReceived;

    public WillowmakerIpcServer(
        string? pipeName = null,
        ILogger<WillowmakerIpcServer>? logger = null,
        string? authKey = null,
        IpcSandboxMode? sandboxMode = null,
        TimeProvider? timeProvider = null)
    {
        PipeName = pipeName ?? DefaultPipeName;
        _logger = logger;
        TimeProvider = timeProvider ?? TimeProvider.System;
        _validator = new IpcSecurityValidator(timeProvider: TimeProvider);

        AuthKey = authKey ?? Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
        SandboxMode = sandboxMode ?? (!string.IsNullOrWhiteSpace(AuthKey)
            ? IpcSandboxMode.Enforced
            : IpcSandboxMode.Permissive);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listenerTask != null) return Task.CompletedTask;

        _logger?.LogInformation("[WillowmakerIPC] Starting Named Pipe Server on pipe: {PipeName} (Sandbox: {Mode})", PipeName, SandboxMode);
        _listenerTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
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
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                    await client.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[WillowmakerIPC] Failed to write broadcast message to client stream.");
                }
            }
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();

        lock (_clientsLock)
        {
            foreach (NamedPipeServerStream client in _connectedClients)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[WillowmakerIPC] Suppressed client stream disposal error on stop.");
                }
            }

            _connectedClients.Clear();
        }

        _authenticatedClients.Clear();

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger?.LogDebug(ex, "[WillowmakerIPC] Listener task cancelled as expected during StopAsync.");
            }

            _listenerTask = null;
        }

        _logger?.LogInformation("[WillowmakerIPC] Named Pipe Server stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptClientsLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = CreateServerStream(PipeName);
                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                lock (_clientsLock)
                {
                    _connectedClients.Add(serverStream);
                }

                _logger?.LogInformation("[WillowmakerIPC] Client connected to pipe {PipeName}. Total clients: {Count}",
                    PipeName, ConnectedClientsCount);

                _ = Task.Run(() => HandleClientAsync(serverStream, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                serverStream?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                serverStream?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogError(ex, "[WillowmakerIPC] Error accepting named pipe client connection.");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
    }

    private async Task HandleClientAsync(NamedPipeServerStream clientStream, CancellationToken cancellationToken)
    {
        PipeReader reader = PipeReader.Create(clientStream, new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true));

        try
        {
            while (clientStream.IsConnected && !cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (WillowmakerIpcMessage.TryReadFrame(ref buffer, out WillowmakerIpcMessage? message))
                {
                    if (message != null)
                        await ProcessMessageAsync(clientStream, message, cancellationToken).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogDebug(ex, "[WillowmakerIPC] Client handle loop cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[WillowmakerIPC] Client stream disconnected or encountered error.");
        }
        finally
        {
            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[WillowmakerIPC] Ignored server PipeReader completion error.");
            }
            lock (_clientsLock)
            {
                _connectedClients.Remove(clientStream);
            }

            _authenticatedClients.TryRemove(clientStream, out _);

            await clientStream.DisposeAsync().ConfigureAwait(false);
            _logger?.LogInformation("[WillowmakerIPC] Client disconnected from pipe {PipeName}.", PipeName);
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access", Justification = "IPC authentication JSON serialization")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.", Justification = "IPC authentication JSON serialization")]
    private async Task ProcessMessageAsync(NamedPipeServerStream clientStream, WillowmakerIpcMessage message,
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
                IpcAuthPayload? payload = message.ReadJson<IpcAuthPayload>();
                IpcAuthResult result = _validator.ValidateHandshake(AuthKey, payload);
                if (result.IsSuccess)
                {
                    _authenticatedClients[clientStream] = true;
                    authResponse = new IpcAuthResponseDto
                    {
                        Authenticated = true,
                        Message = "Session authenticated successfully for actuation."
                    };
                    _logger?.LogInformation("[WillowmakerIPC] [AUTH] Client '{Client}' successfully authenticated session.", payload?.ClientName);
                }
                else
                {
                    authResponse = new IpcAuthResponseDto
                    {
                        Authenticated = false,
                        Message = result.ErrorMessage ?? "Authentication failed."
                    };
                    _logger?.LogWarning("[WillowmakerIPC] [AUTH] Authentication failed: {Error}", result.ErrorMessage);
                }
            }

            WillowmakerIpcMessage respMsg = WillowmakerIpcMessage.CreateJson(IpcOpCode.AuthResponse, authResponse, message.RequestId);
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
                    using var doc = JsonDocument.Parse(message.Payload);
                    if (doc.RootElement.TryGetProperty("_auth", out JsonElement authEl))
                    {
                        var header = JsonSerializer.Deserialize<IpcAuthHeader>(authEl.GetRawText());
                        string command = doc.RootElement.TryGetProperty("command", out JsonElement cmdEl)
                            ? cmdEl.GetString() ?? string.Empty
                            : message.PayloadString;

                        var cmdResult = _validator.ValidateCommand(AuthKey, command, header);
                        if (cmdResult.IsSuccess)
                        {
                            isAuthenticated = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[WillowmakerIPC] Non-JSON or malformed auth envelope payload ignored.");
                }
            }

            if (!isAuthenticated)
            {
                if (SandboxMode == IpcSandboxMode.Enforced)
                {
                    _logger?.LogWarning("[WillowmakerIPC] [SECURITY] Blocked unauthenticated mutating opcode '{OpCode}' in Enforced sandbox mode.", message.OpCode);
                    WillowmakerIpcMessage errorResp = WillowmakerIpcMessage.CreateString(
                        IpcOpCode.CommandResponse,
                        "[SECURITY ERROR] 401 Unauthorized: Operation requires HMAC-SHA256 authentication.",
                        message.RequestId);
                    byte[] errBytes = WillowmakerIpcMessage.Serialize(errorResp);
                    await clientStream.WriteAsync(errBytes, cancellationToken).ConfigureAwait(false);
                    await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (SandboxMode == IpcSandboxMode.Permissive)
                {
                    _logger?.LogWarning("[WillowmakerIPC] [AUDIT] Unauthenticated mutating opcode '{OpCode}' permitted in Permissive sandbox mode.", message.OpCode);
                }
            }
        }

        // 3. Dispatch standard message
        switch (message.OpCode)
        {
            case IpcOpCode.Ping:
                WillowmakerIpcMessage pong = WillowmakerIpcMessage.CreateString(IpcOpCode.Pong, "PONG", message.RequestId);
                byte[] pongBytes = WillowmakerIpcMessage.Serialize(pong);
                await clientStream.WriteAsync(pongBytes, cancellationToken).ConfigureAwait(false);
                await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;

            case IpcOpCode.LogStreamEvent:
                OnLogStreamReceived?.Invoke(message.PayloadString);
                break;

            default:
                if (OnMessageReceived != null)
                {
                    WillowmakerIpcMessage? response = await OnMessageReceived.Invoke(message).ConfigureAwait(false);
                    if (response != null && clientStream.IsConnected)
                    {
                        if (response.RequestId == 0 && message.RequestId > 0)
                        {
                            response = new WillowmakerIpcMessage
                            {
                                OpCode = response.OpCode,
                                RequestId = message.RequestId,
                                Payload = response.Payload
                            };
                        }

                        byte[] responseBytes = WillowmakerIpcMessage.Serialize(response);
                        await clientStream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
                        await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                break;
        }
    }
}
