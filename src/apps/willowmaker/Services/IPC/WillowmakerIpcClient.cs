// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using WILLOWMAKER.Core.Services.Serialization;

namespace WILLOWMAKER.Core.Services.IPC;

public enum IpcConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted,
    Disposed
}

public sealed class WillowmakerIpcClientOptions
{
    public bool AutoReconnect { get; set; } = true;
    public int InitialBackoffMs { get; set; } = 200;
    public int MaxBackoffMs { get; set; } = 5000;
    public double BackoffMultiplier { get; set; } = 2.0;
    public double JitterRatio { get; set; } = 0.2;
    public int DefaultTimeoutMs { get; set; } = 3000;
    public int MaxRetryAttempts { get; set; } = 0; // 0 = Infinite
}

public interface IWillowmakerIpcClient : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    string? AuthKey { get; set; }
    IpcConnectionState State { get; }
    string PipeName { get; }
    string ServerName { get; }

    event Action? OnConnected;
    event Action<Exception?>? OnDisconnected;
    event Action<int, TimeSpan>? OnReconnecting;
    event Action? OnReconnected;
    event Action<WillowmakerIpcMessage>? OnMessageReceived;
    event Action<string>? OnLogStreamReceived;

    Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default);
    Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default);
    Task SendMessageAsync(WillowmakerIpcMessage message, CancellationToken cancellationToken = default);

    Task<WillowmakerIpcMessage?> SendRequestAsync(WillowmakerIpcMessage request, int timeoutMs = 5000,
        CancellationToken cancellationToken = default);

    Task<long> PingAsync(CancellationToken cancellationToken = default);
    Task<CockpitEndpointSyncResponseDto?> SyncEndpointsAsync(CockpitEndpointSyncRequestDto request, int timeoutMs = 3000, CancellationToken cancellationToken = default);
    Task<bool> BringToFrontAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default);
    Task<CockpitLauncherStatusDto?> GetLauncherStatusAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public sealed class WillowmakerIpcClient : IWillowmakerIpcClient
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<WillowmakerIpcClient>? _logger;
    private readonly WillowmakerIpcClientOptions _options;

    private int _nextRequestId;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<WillowmakerIpcMessage>>
        _pendingRequestsByRequestId = new();
    private readonly ConcurrentDictionary<IpcOpCode, ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>>>
        _pendingRequests = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _isDisposed;
    private bool _manuallyDisconnected;
    private NamedPipeClientStream? _pipeClient;
    private Task? _readLoopTask;
    private Task? _reconnectTask;

    public bool IsConnected => State == IpcConnectionState.Connected && (_pipeClient?.IsConnected ?? false);
    public bool IsAuthenticated { get; private set; }
    public string? AuthKey { get; set; }
    public IpcConnectionState State { get; private set; } = IpcConnectionState.Disconnected;

    public string PipeName { get; }
    public string ServerName { get; }

    public WillowmakerIpcClient(
        string? pipeName = null,
        string serverName = ".",
        WillowmakerIpcClientOptions? options = null,
        ILogger<WillowmakerIpcClient>? logger = null,
        string? authKey = null)
    {
        PipeName = pipeName ?? WillowmakerIpcService.DefaultPipeName;
        ServerName = serverName;
        _options = options ?? new WillowmakerIpcClientOptions();
        _logger = logger;
        AuthKey = authKey ?? Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
    }

    public event Action? OnConnected;
    public event Action<Exception?>? OnDisconnected;
    public event Action<int, TimeSpan>? OnReconnecting;
    public event Action? OnReconnected;
    public event Action<WillowmakerIpcMessage>? OnMessageReceived;
    public event Action<string>? OnLogStreamReceived;

    public async Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        _manuallyDisconnected = false;
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;

            await ConnectInternalAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            State = IpcConnectionState.Connected;
            OnConnected?.Invoke();

            if (!string.IsNullOrWhiteSpace(AuthKey))
            {
                try
                {
                    await AuthenticateAsync(AuthKey, Math.Min(timeoutMs, 2000), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[WillowmakerIPC] Auto-handshake authentication failed on connect.");
                }
            }
        }
        finally
        {
            try
            {
                _connectionLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        string keyToUse = authKey ?? AuthKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyToUse))
        {
            _logger?.LogWarning("[WillowmakerIPC] Cannot authenticate: AuthKey is null or empty.");
            IsAuthenticated = false;
            return false;
        }

        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        var payload = NEXUS.Contracts.Ipc.IpcSecurityValidator.CreateHandshakePayload(keyToUse, "willowmaker-client");
        WillowmakerIpcMessage authMsg = WillowmakerIpcMessage.CreateJson(IpcOpCode.Authenticate, payload, WillowmakerJsonSerializerContext.Default.IpcAuthPayload);

        WillowmakerIpcMessage? response = await SendRequestAsync(authMsg, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (response != null)
        {
            var authResp = response.ReadJson(WillowmakerJsonSerializerContext.Default.IpcAuthResponseDto);
            if (authResp != null && authResp.Authenticated)
            {
                IsAuthenticated = true;
                _logger?.LogInformation("[WillowmakerIPC] Authentication handshake succeeded: {Message}", authResp.Message);
                return true;
            }

            _logger?.LogWarning("[WillowmakerIPC] Authentication handshake rejected: {Message}", authResp?.Message ?? "Unknown failure");
        }

        IsAuthenticated = false;
        return false;
    }

    public async Task SendMessageAsync(WillowmakerIpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!IsConnected || _pipeClient == null)
            throw new InvalidOperationException($"IPC Client is not connected (Current State: {State}).");

        byte[] serialized = WillowmakerIpcMessage.Serialize(message);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipeClient.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
            await _pipeClient.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async Task<WillowmakerIpcMessage?> SendRequestAsync(
        WillowmakerIpcMessage request,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
        _pendingRequestsByRequestId[requestId] = tcs;

        IpcOpCode expectedResponseOpCode = GetExpectedResponseOpCode(finalRequest.OpCode);
        ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>> queue = _pendingRequests.GetOrAdd(
            expectedResponseOpCode,
            _ => new ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>>());
        queue.Enqueue(tcs);

        byte[] serialized = WillowmakerIpcMessage.Serialize(finalRequest);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected || _pipeClient == null)
                throw new InvalidOperationException($"IPC Client is not connected (Current State: {State}).");

            try
            {
                await _pipeClient.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                await _pipeClient.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _pendingRequestsByRequestId.TryRemove(requestId, out _);
                tcs.TrySetException(ex);
                throw;
            }
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _cts.Token);

        using CancellationTokenRegistration reg = linkedCts.Token.Register(() =>
        {
            _pendingRequestsByRequestId.TryRemove(requestId, out _);
            if (timeoutCts.IsCancellationRequested)
                tcs.TrySetException(
                    new TimeoutException($"IPC request '{finalRequest.OpCode}' (RequestId: {requestId}) timed out after {timeoutMs}ms."));
            else
                tcs.TrySetCanceled(linkedCts.Token);
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequestsByRequestId.TryRemove(requestId, out _);
        }
    }

    public async Task<long> PingAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        WillowmakerIpcMessage ping = WillowmakerIpcMessage.CreateString(IpcOpCode.Ping, "PING");
        WillowmakerIpcMessage? pong = await SendRequestAsync(ping, _options.DefaultTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        if (pong == null || pong.OpCode != IpcOpCode.Pong)
            throw new InvalidOperationException("IPC Ping failed: invalid response received from server.");

        return sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
    }

    public async Task<CockpitEndpointSyncResponseDto?> SyncEndpointsAsync(
        CockpitEndpointSyncRequestDto request,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        WillowmakerIpcMessage reqMsg = WillowmakerIpcMessage.CreateJson(
            IpcOpCode.SyncEndpoints,
            request,
            WillowmakerJsonSerializerContext.Default.CockpitEndpointSyncRequestDto);
        WillowmakerIpcMessage? respMsg = await SendRequestAsync(reqMsg, timeoutMs, cancellationToken).ConfigureAwait(false);
        return respMsg?.ReadJson(WillowmakerJsonSerializerContext.Default.CockpitEndpointSyncResponseDto);
    }

    public async Task<bool> BringToFrontAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        WillowmakerIpcMessage reqMsg = WillowmakerIpcMessage.CreateString(IpcOpCode.BringToFront, "FOCUS");
        WillowmakerIpcMessage? respMsg = await SendRequestAsync(reqMsg, timeoutMs, cancellationToken).ConfigureAwait(false);
        return respMsg != null &&
               respMsg.OpCode == IpcOpCode.BringToFrontResponse &&
               string.Equals(respMsg.PayloadString, "OK", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CockpitLauncherStatusDto?> GetLauncherStatusAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            await ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        WillowmakerIpcMessage reqMsg = WillowmakerIpcMessage.CreateString(IpcOpCode.GetLauncherStatus, "STATUS");
        WillowmakerIpcMessage? respMsg = await SendRequestAsync(reqMsg, timeoutMs, cancellationToken).ConfigureAwait(false);
        return respMsg?.ReadJson(WillowmakerJsonSerializerContext.Default.CockpitLauncherStatusDto);
    }

    public async Task DisconnectAsync()
    {
        _manuallyDisconnected = true;
        IsAuthenticated = false;
        State = IpcConnectionState.Disconnected;

        try
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CleanupExistingConnection();

                if (_readLoopTask != null)
                {
                    try
                    {
                        await _readLoopTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore expected cancellation
                    }

                    _readLoopTask = null;
                }
            }
            finally
            {
                try
                {
                    _connectionLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        catch (ObjectDisposedException)
        {
            CleanupExistingConnection();
        }

        OnDisconnected?.Invoke(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task ConnectInternalAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        CleanupExistingConnection();

        State = IpcConnectionState.Connecting;
        _logger?.LogInformation(
            "[WillowmakerIPC] Connecting to Named Pipe: \\\\{Server}\\pipe\\{PipeName} (Timeout: {Timeout}ms)",
            ServerName, PipeName, timeoutMs);

        NamedPipeClientStream client = new(
            ServerName,
            PipeName,
            PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.WriteThrough);

        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _cts.Token);

        try
        {
            await client.ConnectAsync(linkedCts.Token).ConfigureAwait(false);
            _pipeClient = client;

            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
            _logger?.LogInformation("[WillowmakerIPC] Connected successfully to Named Pipe: \\\\{Server}\\pipe\\{PipeName}",
                ServerName, PipeName);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static IpcOpCode GetExpectedResponseOpCode(IpcOpCode requestOpCode)
    {
        return requestOpCode switch
        {
            IpcOpCode.Ping => IpcOpCode.Pong,
            IpcOpCode.Authenticate => IpcOpCode.AuthResponse,
            IpcOpCode.ExecuteCommand => IpcOpCode.CommandResponse,
            IpcOpCode.GetEngineState => IpcOpCode.EngineStateResponse,
            IpcOpCode.SyncEndpoints => IpcOpCode.SyncEndpointsResponse,
            IpcOpCode.BringToFront => IpcOpCode.BringToFrontResponse,
            IpcOpCode.GetLauncherStatus => IpcOpCode.LauncherStatusResponse,
            _ => throw new ArgumentException(
                $"OpCode '{requestOpCode}' is not a request that expects a direct response.", nameof(requestOpCode))
        };
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_pipeClient == null) return;
        PipeReader reader = PipeReader.Create(_pipeClient, new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true));
        Exception? disconnectException = null;

        try
        {
            while (_pipeClient != null && _pipeClient.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadMessageFromSequence(ref buffer, out WillowmakerIpcMessage? message))
                {
                    if (message != null) DispatchIncomingMessage(message);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            disconnectException = ex;
            _logger?.LogDebug(ex, "[WillowmakerIPC] Client read loop terminated with error.");
        }
        finally
        {
            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore reader completion errors
            }
            HandleDisconnection(disconnectException);
        }
    }

    private static bool TryReadMessageFromSequence(ref ReadOnlySequence<byte> buffer, out WillowmakerIpcMessage? message)
    {
        message = null;
        if (buffer.IsEmpty) return false;

        if (buffer.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = buffer.FirstSpan;
            if (WillowmakerIpcMessage.TryDeserializeAny(span, out message, out int bytesConsumed))
            {
                buffer = buffer.Slice(bytesConsumed);
                return true;
            }
            return false;
        }

        int length = (int)Math.Min(buffer.Length, 128 * 1024);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            buffer.Slice(0, length).CopyTo(rented);
            ReadOnlySpan<byte> span = rented.AsSpan(0, length);
            if (WillowmakerIpcMessage.TryDeserializeAny(span, out message, out int bytesConsumed))
            {
                buffer = buffer.Slice(bytesConsumed);
                return true;
            }
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void DispatchIncomingMessage(WillowmakerIpcMessage message)
    {
        switch (message.OpCode)
        {
            case IpcOpCode.LogStreamEvent:
                OnLogStreamReceived?.Invoke(message.PayloadString);
                break;

            default:
                if (message.RequestId > 0 && _pendingRequestsByRequestId.TryRemove(message.RequestId, out TaskCompletionSource<WillowmakerIpcMessage>? reqTcs))
                {
                    reqTcs.TrySetResult(message);
                    return;
                }

                if (_pendingRequests.TryGetValue(message.OpCode,
                        out ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>>? queue))
                {
                    while (queue.TryDequeue(out TaskCompletionSource<WillowmakerIpcMessage>? tcs))
                    {
                        // Match response to the first active TCS waiting for this OpCode.
                        if (tcs.TrySetResult(message))
                            return;
                    }
                }

                OnMessageReceived?.Invoke(message);
                break;
        }
    }

    private void HandleDisconnection(Exception? ex)
    {
        IsAuthenticated = false;
        if (_isDisposed || _manuallyDisconnected)
        {
            State = IpcConnectionState.Disconnected;
            return;
        }

        State = IpcConnectionState.Disconnected;
        _logger?.LogWarning(ex, "[WillowmakerIPC] Connection lost to pipe \\\\{Server}\\pipe\\{PipeName}.", ServerName,
            PipeName);

        // Fail pending TCS by RequestId
        foreach (KeyValuePair<uint, TaskCompletionSource<WillowmakerIpcMessage>> kvp in _pendingRequestsByRequestId)
        {
            if (_pendingRequestsByRequestId.TryRemove(kvp.Key, out TaskCompletionSource<WillowmakerIpcMessage>? tcs))
            {
                tcs.TrySetException(new IOException("IPC pipe connection terminated while awaiting response.", ex));
            }
        }

        // Fail all pending TCS by OpCode
        foreach (KeyValuePair<IpcOpCode, ConcurrentQueue<TaskCompletionSource<WillowmakerIpcMessage>>> kvp in
                 _pendingRequests)
            while (kvp.Value.TryDequeue(out TaskCompletionSource<WillowmakerIpcMessage>? tcs))
                tcs.TrySetException(new IOException("IPC pipe connection terminated while awaiting response.", ex));

        OnDisconnected?.Invoke(ex);

        if (_options.AutoReconnect && !_cts.IsCancellationRequested)
        {
            if (_reconnectTask == null || _reconnectTask.IsCompleted)
            {
                _reconnectTask = Task.Run(() => ReconnectLoopAsync(_cts.Token), CancellationToken.None);
            }
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        State = IpcConnectionState.Reconnecting;
        int attempt = 0;

        while (!cancellationToken.IsCancellationRequested && !_isDisposed && !_manuallyDisconnected)
        {
            attempt++;
            if (_options.MaxRetryAttempts > 0 && attempt > _options.MaxRetryAttempts)
            {
                State = IpcConnectionState.Faulted;
                _logger?.LogError(
                    "[WillowmakerIPC] Exceeded maximum reconnection attempts ({Max}). Stopping auto-reconnect.",
                    _options.MaxRetryAttempts);
                break;
            }

            // Calculate exponential backoff with full jitter
            double baseDelay = Math.Min(_options.MaxBackoffMs,
                _options.InitialBackoffMs * Math.Pow(_options.BackoffMultiplier, attempt - 1));
            double jitter = baseDelay * _options.JitterRatio * (Random.Shared.NextDouble() * 2.0 - 1.0);
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Max(50, baseDelay + jitter));

            _logger?.LogInformation("[WillowmakerIPC] Reconnecting attempt {Attempt} in {DelayMs:F0}ms...", attempt,
                delay.TotalMilliseconds);
            OnReconnecting?.Invoke(attempt, delay);

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_manuallyDisconnected || _isDisposed || IsConnected) return;

                await ConnectInternalAsync(_options.DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
                State = IpcConnectionState.Connected;

                if (!string.IsNullOrWhiteSpace(AuthKey))
                {
                    try
                    {
                        await AuthenticateAsync(AuthKey, Math.Min(_options.DefaultTimeoutMs, 2000), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[WillowmakerIPC] Re-authentication failed after reconnect.");
                    }
                }

                _logger?.LogInformation("[WillowmakerIPC] Reconnected successfully on attempt {Attempt}!", attempt);
                OnReconnected?.Invoke();
                OnConnected?.Invoke();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogDebug(ex, "[WillowmakerIPC] Reconnection attempt {Attempt} failed: {Message}", attempt,
                    ex.Message);
            }
            finally
            {
                try
                {
                    _connectionLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private void CleanupExistingConnection()
    {
        if (_pipeClient != null)
        {
            try
            {
                _pipeClient.Dispose();
            }
            catch
            {
                // Suppress disposal exception
            }

            _pipeClient = null;
        }
    }
}
