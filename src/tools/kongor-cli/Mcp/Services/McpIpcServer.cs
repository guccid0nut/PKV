// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NEXUS.Contracts.Ipc;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Interface for the high-performance MCP Named Pipe IPC server.
/// </summary>
public interface IMcpIpcServer : IAsyncDisposable, IDisposable
{
    string PipeName { get; }
    bool IsRunning { get; }
    bool IsListening { get; }
    int ConnectedClientsCount { get; }
    void AttachProcess(Process process);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task BroadcastAsync(WillowmakerIpcMessage message, CancellationToken cancellationToken = default);
    Task<WillowmakerIpcMessage?> SendRequestAsync(WillowmakerIpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default);

    event Action? OnClientConnected;
    event Action<string>? OnLogStreamReceived;
    event Action<WillowmakerIpcMessage>? OnMessageReceived;
    event Action<Exception?>? OnClientDisconnected;
}

/// <summary>
/// Resilient, zero-allocation multi-instance Named Pipe IPC server for ProjectKongor MCP.
/// Features multiplexed RequestId routing, instant crash faulting on process exit, and ArrayPool buffering.
/// </summary>
public sealed class McpIpcServer : IMcpIpcServer
{
    private const int BufferPoolSize = 65536;
    private static readonly ConcurrentDictionary<string, McpIpcServer> ActiveServersByPipe = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _clientsLock = new();
    private readonly List<NamedPipeServerStream> _connectedClients = [];
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<WillowmakerIpcMessage>> _pendingRequests = new();
    private readonly ConcurrentDictionary<NEXUS.Contracts.Ipc.IpcOpCode, ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>>> _pendingRequestsByOpCode = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _logger;
    private readonly TimeProvider _timeProvider;

    private int _nextRequestId;
    private Process? _attachedProcess;
    private Task? _listenerTask;
    private Task? _heartbeatTask;
    private bool _isDisposed;

    public string PipeName { get; }
    public bool IsRunning => _listenerTask != null && !_cts.IsCancellationRequested;
    private volatile bool _isListening;
    public bool IsListening => _isListening && IsRunning;

    public static bool IsPipeHostedInCurrentProcess(string pipeName) =>
        !string.IsNullOrWhiteSpace(pipeName) &&
        ActiveServersByPipe.TryGetValue(pipeName, out McpIpcServer? server) &&
        server.IsRunning;

    public static int GetHostedPipeConnectedClientCount(string pipeName) =>
        !string.IsNullOrWhiteSpace(pipeName) &&
        ActiveServersByPipe.TryGetValue(pipeName, out McpIpcServer? server) &&
        server.IsRunning
            ? server.ConnectedClientsCount
            : 0;

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

    public event Action? OnClientConnected;
    public event Action<string>? OnLogStreamReceived;
    public event Action<WillowmakerIpcMessage>? OnMessageReceived;
    public event Action<Exception?>? OnClientDisconnected;

    public McpIpcServer(
        string? pipeName = null,
        Action<string>? logger = null,
        TimeProvider? timeProvider = null)
    {
        PipeName = pipeName ?? "willowmaker-ipc";
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static McpIpcServer CreateForClient(int pid, Action<string>? logger = null, TimeProvider? timeProvider = null)
    {
        return new McpIpcServer($"kongor-ipc-client-{pid}", logger, timeProvider);
    }

    public static McpIpcServer CreateForServer(int port, Action<string>? logger = null, TimeProvider? timeProvider = null)
    {
        return new McpIpcServer($"kongor-ipc-server-{port}", logger, timeProvider);
    }

    public void AttachProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        _attachedProcess = process;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;

            if (process.HasExited)
            {
                OnProcessExited(process, EventArgs.Empty);
            }
            else
            {
                _logger?.Invoke($"[McpIpcServer] Attached process watcher for PID {process.Id}.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[McpIpcServer] Warning: Failed to hook Process.Exited: {ex.Message}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int pid = 0;
        int exitCode = -1;

        if (sender is Process p)
        {
            try
            {
                pid = p.Id;
                exitCode = p.ExitCode;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[McpIpcServer] Could not read exited sender Process metadata: {ex.Message}");
            }
        }
        else if (_attachedProcess != null)
        {
            try
            {
                pid = _attachedProcess.Id;
                exitCode = _attachedProcess.ExitCode;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[McpIpcServer] Could not read attached Process metadata on exit: {ex.Message}");
            }
        }

        _logger?.Invoke($"[McpIpcServer] Game process (PID {pid}) exited with code {exitCode}. Faulting all in-flight requests...");
        var faultEx = new GameProcessTerminatedException(pid, exitCode);
        FaultAllPendingRequests(faultEx);
    }

    public void FaultAllPendingRequests(Exception exception)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }

        foreach (var kvp in _pendingRequestsByOpCode)
        {
            while (kvp.Value.TryDequeue(out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listenerTask != null) return Task.CompletedTask;

        ActiveServersByPipe[PipeName] = this;
        _logger?.Invoke($"[McpIpcServer] Starting Named Pipe server on \\\\.\\pipe\\{PipeName}...");
        _listenerTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token), CancellationToken.None);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token), CancellationToken.None);

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
                    _logger?.Invoke($"[McpIpcServer] Failed to write broadcast message to client stream: {ex.Message}");
                }
            }
        }
    }

    public async Task<WillowmakerIpcMessage?> SendRequestAsync(
        WillowmakerIpcMessage request,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_attachedProcess is { HasExited: true })
        {
            int pid = 0;
            int exitCode = -1;
            try { pid = _attachedProcess.Id; exitCode = _attachedProcess.ExitCode; } catch (Exception ex) { _logger?.Invoke($"[McpIpcServer] Could not inspect exited attached process: {ex.Message}"); }
            throw new GameProcessTerminatedException(pid, exitCode);
        }

        NamedPipeServerStream? targetClient = null;
        lock (_clientsLock)
        {
            targetClient = _connectedClients.FirstOrDefault(c => c.IsConnected);
        }

        if (targetClient == null)
        {
            throw new IOException($"No connected clients on Named Pipe \\\\.\\pipe\\{PipeName}.");
        }

        uint requestId = request.RequestId;
        WillowmakerIpcMessage finalRequest = request;

        if (requestId == 0)
        {
            int next = Interlocked.Increment(ref _nextRequestId);
            if (next <= 0)
            {
                Interlocked.Exchange(ref _nextRequestId, 1);
                next = 1;
            }
            requestId = (uint)next;

            finalRequest = new WillowmakerIpcMessage
            {
                OpCode = request.OpCode,
                RequestId = requestId,
                Payload = request.Payload
            };
        }

        TaskCompletionSource<WillowmakerIpcMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        if (TryGetExpectedResponseOpCode(finalRequest.OpCode, out var expectedOpCode))
        {
            var queue = _pendingRequestsByOpCode.GetOrAdd(expectedOpCode, _ => new ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>>());
            queue.Enqueue(tcs);
        }

        byte[] serialized = WillowmakerIpcMessage.Serialize(finalRequest);

        try
        {
            await targetClient.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
            await targetClient.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pendingRequests.TryRemove(requestId, out _);
            tcs.TrySetException(ex);
            throw;
        }

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _cts.Token);

        using CancellationTokenRegistration reg = linkedCts.Token.Register(() =>
        {
            if (timeoutCts.IsCancellationRequested)
            {
                if (_pendingRequests.TryRemove(requestId, out var pendingTcs))
                {
                    pendingTcs.TrySetException(new TimeoutException($"IPC request '{finalRequest.OpCode}' (RequestId: {requestId}) timed out after {timeoutMs}ms."));
                }
            }
            else
            {
                if (_pendingRequests.TryRemove(requestId, out var pendingTcs))
                {
                    pendingTcs.TrySetCanceled(linkedCts.Token);
                }
            }
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();

        if (_attachedProcess != null)
        {
            try
            {
                _attachedProcess.Exited -= OnProcessExited;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[McpIpcServer] Failed to detach Process.Exited handler: {ex.Message}");
            }
            _attachedProcess = null;
        }

        lock (_clientsLock)
        {
            foreach (NamedPipeServerStream client in _connectedClients)
            {
                try { client.Dispose(); } catch (Exception ex) { _logger?.Invoke($"[McpIpcServer] Error disposing client pipe stream: {ex.Message}"); }
            }
            _connectedClients.Clear();
        }

        FaultAllPendingRequests(new IOException("IPC Server stopped."));

        if (_listenerTask != null)
        {
            ActiveServersByPipe.TryRemove(new KeyValuePair<string, McpIpcServer>(PipeName, this));
            try { await _listenerTask.ConfigureAwait(false); } catch (Exception ex) { _logger?.Invoke($"[McpIpcServer] Listener task stop notice: {ex.Message}"); }
            _listenerTask = null;
        }

        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); } catch (Exception ex) { _logger?.Invoke($"[McpIpcServer] Heartbeat task stop notice: {ex.Message}"); }
            _heartbeatTask = null;
        }

        _logger?.Invoke($"[McpIpcServer] Named Pipe server on \\\\.\\pipe\\{PipeName} stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    public void Dispose()
    {
#pragma warning disable RS0030 // Justification: Synchronous IDisposable bridge to DisposeAsync
        DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore RS0030
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
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                        AccessControlType.Allow));

                    serverStream = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                        BufferPoolSize,
                        BufferPoolSize,
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
                }

                _isListening = true;
                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _isListening = false;

                lock (_clientsLock)
                {
                    _connectedClients.Add(serverStream);
                }

                _logger?.Invoke($"[McpIpcServer] Client connected to pipe \\\\.\\pipe\\{PipeName}. Active clients: {ConnectedClientsCount}");
                OnClientConnected?.Invoke();

                _ = Task.Run(() => HandleClientAsync(serverStream, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _isListening = false;
                serverStream?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _isListening = false;
                serverStream?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger?.Invoke($"[McpIpcServer] Error accepting client connection: {ex.Message}");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        _isListening = false;
    }

    private async Task HandleClientAsync(NamedPipeServerStream clientStream, CancellationToken cancellationToken)
    {
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(BufferPoolSize);
        using MemoryStream accumulatedBuffer = new();
        Exception? disconnectException = null;

        try
        {
            while (clientStream.IsConnected && !cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                int bytesRead = await clientStream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                accumulatedBuffer.Write(readBuffer, 0, bytesRead);
                if (accumulatedBuffer.Length > WillowmakerIpcMessage.MaxPayloadSize + WillowmakerIpcMessage.HeaderSize)
                    throw new InvalidDataException($"IPC server incoming buffer overflow: {accumulatedBuffer.Length} bytes exceeds limit.");

                byte[] currentBytes = accumulatedBuffer.ToArray();
                int offset = 0;

                while (offset < currentBytes.Length)
                {
                    ReadOnlySpan<byte> remainingSpan = currentBytes.AsSpan(offset);
                    if (WillowmakerIpcMessage.TryDeserializeAny(remainingSpan, out WillowmakerIpcMessage? message, out int bytesConsumed))
                    {
                        offset += bytesConsumed;
                        if (message != null)
                        {
                            DispatchIncomingMessage(message);
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                if (offset > 0)
                {
                    byte[] unreadBytes = currentBytes.AsSpan(offset).ToArray();
                    accumulatedBuffer.SetLength(0);
                    if (unreadBytes.Length > 0)
                    {
                        accumulatedBuffer.Write(unreadBytes, 0, unreadBytes.Length);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            disconnectException = ex;
            _logger?.Invoke($"[McpIpcServer] Client error on pipe \\\\.\\pipe\\{PipeName}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);

            lock (_clientsLock)
            {
                _connectedClients.Remove(clientStream);
            }

            await clientStream.DisposeAsync().ConfigureAwait(false);
            _logger?.Invoke($"[McpIpcServer] Client disconnected from pipe \\\\.\\pipe\\{PipeName}. Remaining: {ConnectedClientsCount}");
            OnClientDisconnected?.Invoke(disconnectException);
        }
    }

    private void DispatchIncomingMessage(WillowmakerIpcMessage message)
    {
        // 1. Multiplexed RequestId matching
        if (message.RequestId > 0 && _pendingRequests.TryRemove(message.RequestId, out TaskCompletionSource<WillowmakerIpcMessage>? tcs))
        {
            tcs.TrySetResult(message);
            return;
        }

        // 1b. Fallback: match by OpCode for clients that do not echo RequestId
        if (_pendingRequestsByOpCode.TryGetValue(message.OpCode, out var queue))
        {
            while (queue.TryDequeue(out var pendingTcs))
            {
                if (pendingTcs.TrySetResult(message))
                    return;
            }
        }

        // 2. Unsolicited or broadcast stream events
        switch (message.OpCode)
        {
            case NEXUS.Contracts.Ipc.IpcOpCode.LogStreamEvent:
                OnLogStreamReceived?.Invoke(message.PayloadString);
                break;

            case NEXUS.Contracts.Ipc.IpcOpCode.Pong:
                // Handled heartbeat pong
                break;

            default:
                OnMessageReceived?.Invoke(message);
                break;
        }
    }

    private static bool TryGetExpectedResponseOpCode(NEXUS.Contracts.Ipc.IpcOpCode requestOpCode, out NEXUS.Contracts.Ipc.IpcOpCode responseOpCode)
    {
        switch (requestOpCode)
        {
            case NEXUS.Contracts.Ipc.IpcOpCode.Ping:
                responseOpCode = NEXUS.Contracts.Ipc.IpcOpCode.Pong;
                return true;
            case NEXUS.Contracts.Ipc.IpcOpCode.ExecuteCommand:
            case NEXUS.Contracts.Ipc.IpcOpCode.DispatchUiEvent:
            case NEXUS.Contracts.Ipc.IpcOpCode.GetMatchState:
                responseOpCode = NEXUS.Contracts.Ipc.IpcOpCode.CommandResponse;
                return true;
            case NEXUS.Contracts.Ipc.IpcOpCode.GetEngineState:
                responseOpCode = NEXUS.Contracts.Ipc.IpcOpCode.EngineStateResponse;
                return true;
            case NEXUS.Contracts.Ipc.IpcOpCode.Authenticate:
                responseOpCode = NEXUS.Contracts.Ipc.IpcOpCode.AuthResponse;
                return true;
            case NEXUS.Contracts.Ipc.IpcOpCode.InjectSubTickInput:
                responseOpCode = NEXUS.Contracts.Ipc.IpcOpCode.SubTickInputResponse;
                return true;
            case NEXUS.Contracts.Ipc.IpcOpCode.UpdateTelemetryMetrics:
                responseOpCode = NEXUS.Contracts.Ipc.IpcOpCode.TelemetryMetricsResponse;
                return true;
            default:
                responseOpCode = default;
                return false;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            try
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                if (ConnectedClientsCount > 0)
                {
                    WillowmakerIpcMessage ping = WillowmakerIpcMessage.CreateString(NEXUS.Contracts.Ipc.IpcOpCode.Ping, "PING");
                    await BroadcastAsync(ping, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[McpIpcServer] Heartbeat exception: {ex.Message}");
            }
        }
    }
}
