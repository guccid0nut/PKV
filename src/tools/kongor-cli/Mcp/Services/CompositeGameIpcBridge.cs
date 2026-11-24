// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using NEXUS.Contracts.Ipc;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Dual-tier composite IPC bridge implementing <see cref="IGameIpcBridge"/>.
/// Unites Named Pipe IPC (primary, for dedicated server workers or -pipe clients)
/// and In-Engine Mailbox IPC (secondary, for player game clients running in background).
/// Guarantees zero window focus theft and programmatic background execution without Win32 Ctrl+F8 keystrokes.
/// </summary>
public sealed class CompositeGameIpcBridge : IGameIpcBridge
{
    private readonly IGameIpcBridge _primary;
    private readonly IGameIpcBridge _secondary;
    private readonly IGameIpcBridge? _tertiary;
    private readonly Action<string>? _logger;
    private bool _isDisposed;

    public IGameIpcBridge Primary => _primary;
    public IGameIpcBridge Secondary => _secondary;
    public IGameIpcBridge? Tertiary => _tertiary;

    public string LastChannel { get; private set; } = "named_pipe";
    public string? LastFallbackReason { get; private set; }
    public Func<bool>? NativePipePresenceProbe { get; set; }

    public string PipeName => _primary.PipeName;
    public string ServerName => _primary.ServerName;

    public bool IsConnected => (!_isDisposed) && (_primary.IsConnected || _secondary.IsConnected || (_tertiary?.IsConnected ?? false));
    public bool IsAuthenticated => (!_isDisposed) && (_primary.IsAuthenticated || _secondary.IsAuthenticated || (_tertiary?.IsAuthenticated ?? false));

    public string? AuthKey
    {
        get => _primary.AuthKey ?? _secondary.AuthKey ?? _tertiary?.AuthKey;
        set
        {
            _primary.AuthKey = value;
            _secondary.AuthKey = value;
            if (_tertiary != null) _tertiary.AuthKey = value;
        }
    }

    public event Action? OnConnected;
    public event Action<Exception?>? OnDisconnected;
    public event Action<IpcMessage>? OnMessageReceived;
    public event Action<string>? OnLogStreamReceived;

    public CompositeGameIpcBridge(
        IGameIpcBridge primary,
        IGameIpcBridge secondary,
        Action<string>? logger = null)
        : this(primary, secondary, null, logger)
    {
    }

    public CompositeGameIpcBridge(
        IGameIpcBridge primary,
        IGameIpcBridge secondary,
        IGameIpcBridge? tertiary,
        Action<string>? logger = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        _tertiary = tertiary;
        _logger = logger;

        _primary.OnConnected += () => OnConnected?.Invoke();
        _secondary.OnConnected += () => OnConnected?.Invoke();
        if (_tertiary != null) _tertiary.OnConnected += () => OnConnected?.Invoke();

        _primary.OnDisconnected += ex =>
        {
            if (!IsConnected) OnDisconnected?.Invoke(ex);
        };
        _secondary.OnDisconnected += ex =>
        {
            if (!IsConnected) OnDisconnected?.Invoke(ex);
        };
        if (_tertiary != null)
        {
            _tertiary.OnDisconnected += ex =>
            {
                if (!IsConnected) OnDisconnected?.Invoke(ex);
            };
        }

        _primary.OnMessageReceived += msg => OnMessageReceived?.Invoke(msg);
        _secondary.OnMessageReceived += msg => OnMessageReceived?.Invoke(msg);
        if (_tertiary != null) _tertiary.OnMessageReceived += msg => OnMessageReceived?.Invoke(msg);

        _primary.OnLogStreamReceived += log => OnLogStreamReceived?.Invoke(log);
        _secondary.OnLogStreamReceived += log => OnLogStreamReceived?.Invoke(log);
        if (_tertiary != null) _tertiary.OnLogStreamReceived += log => OnLogStreamReceived?.Invoke(log);
    }

    private void Log(string message) => _logger?.Invoke(message);

    public bool IsNativePipePresent()
    {
        if (NativePipePresenceProbe != null)
        {
            return NativePipePresenceProbe();
        }

        string pipeName = _primary.PipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return false;
        }

        try
        {
            return File.Exists($@"\\.\pipe\{pipeName}");
        }
        catch (Exception ex)
        {
            Log($"[CompositeBridge] Native pipe presence probe warning: {ex.Message}");
            return false;
        }
    }

    public async Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        if (_primary.IsConnected) return;

        // Proactively attempt connection on primary and any disconnected fallback channels
        var primaryTask = Task.Run(async () =>
        {
            try { await _primary.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Primary ConnectAsync skipped: {ex.Message}"); }
        }, cancellationToken);

        var secondaryTask = !_secondary.IsConnected ? Task.Run(async () =>
        {
            try { await _secondary.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Secondary ConnectAsync skipped: {ex.Message}"); }
        }, cancellationToken) : Task.CompletedTask;

        var tertiaryTask = (_tertiary != null && !_tertiary.IsConnected) ? Task.Run(async () =>
        {
            try { await _tertiary.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Tertiary ConnectAsync skipped: {ex.Message}"); }
        }, cancellationToken) : Task.CompletedTask;

        await Task.WhenAll(primaryTask, secondaryTask, tertiaryTask).ConfigureAwait(false);

        if (IsConnected)
        {
            OnConnected?.Invoke();
        }
    }

    public async Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        bool primaryAuth = false;
        try
        {
            primaryAuth = await _primary.AuthenticateAsync(authKey, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"[CompositeBridge] Primary AuthenticateAsync failed: {ex.Message}");
        }

        bool secondaryAuth = false;
        try
        {
            secondaryAuth = await _secondary.AuthenticateAsync(authKey, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"[CompositeBridge] Secondary AuthenticateAsync failed: {ex.Message}");
        }

        bool tertiaryAuth = false;
        if (_tertiary != null)
        {
            try
            {
                tertiaryAuth = await _tertiary.AuthenticateAsync(authKey, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Tertiary AuthenticateAsync failed: {ex.Message}");
            }
        }

        return primaryAuth || secondaryAuth || tertiaryAuth;
    }

    public Task<string> ExecuteCommandAsync(string command, int timeoutMs = 3000, CancellationToken cancellationToken = default)
        => ExecuteCommandOnChannelAsync(command, requestedChannel: null, timeoutMs, cancellationToken);

    public async Task<string> ExecuteCommandOnChannelAsync(
        string command,
        string? requestedChannel,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        string trimmed = command.Trim();
        if (trimmed.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteLuaOnChannelAsync(trimmed[7..].TrimStart(), requestedChannel, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(requestedChannel, "mailbox", StringComparison.OrdinalIgnoreCase))
        {
            if (_tertiary == null)
            {
                throw new InvalidOperationException("Mailbox channel was explicitly requested, but no tertiary mailbox bridge is configured.");
            }
            if (!_tertiary.IsConnected)
            {
                await _tertiary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            LastChannel = "mailbox";
            LastFallbackReason = "explicit_channel_override";
            return await _tertiary.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(requestedChannel, "named_pipe", StringComparison.OrdinalIgnoreCase))
        {
            if (!_primary.IsConnected)
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            LastChannel = "named_pipe";
            LastFallbackReason = null;
            return await _primary.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        // Priority 1: Primary Native In-Process Pipe (kongor-native-client)
        if (!_primary.IsConnected)
        {
            try
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Primary fast-connect before ExecuteCommandAsync skipped: {ex.Message}");
            }
        }

        Exception? primaryError = null;
        if (_primary.IsConnected)
        {
            try
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                return await _primary.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                throw;
            }
            catch (Exception ex)
            {
                primaryError = ex;
                Log($"[CompositeBridge] Primary IPC command failed ({ex.Message}).");
            }
        }

        if (_secondary.IsConnected)
        {
            try
            {
                LastChannel = "willowmaker";
                LastFallbackReason = primaryError != null ? "primary_io_error" : "primary_disconnected";
                return await _secondary.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Secondary IPC command failed ({ex.Message}).");
            }
        }

        if (!IsNativePipePresent() && _tertiary != null)
        {
            if (!_tertiary.IsConnected)
            {
                try
                {
                    await _tertiary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[CompositeBridge] Tertiary fallback connect in ExecuteCommandAsync failed: {ex.Message}");
                }
            }

            if (_tertiary.IsConnected)
            {
                LastChannel = "mailbox";
                LastFallbackReason = "pipe_absent";
                return await _tertiary.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (primaryError != null)
        {
            Log("[CompositeBridge] Native pipe is present on host; suppressing unrequested 3000ms mailbox fallback for ExecuteCommandAsync.");
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryError).Throw();
        }

        if (!_secondary.IsConnected && _secondary is McpServerIpcBridge)
        {
            throw new IOException($"No active game client IPC connection on '\\\\.\\pipe\\{_primary.PipeName}' or '\\\\.\\pipe\\{_secondary.PipeName}'. Ensure hon_x64.exe is running.");
        }
        LastChannel = "willowmaker";
        LastFallbackReason = "primary_disconnected";
        return await _secondary.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> ExecuteLuaAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
        => ExecuteLuaOnChannelAsync(luaCode, requestedChannel: null, timeoutMs, cancellationToken);

    public async Task<string> ExecuteLuaOnChannelAsync(
        string luaCode,
        string? requestedChannel,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);

        if (string.Equals(requestedChannel, "mailbox", StringComparison.OrdinalIgnoreCase))
        {
            if (_tertiary == null)
            {
                throw new InvalidOperationException("Mailbox channel was explicitly requested, but no tertiary mailbox bridge is configured.");
            }
            if (!_tertiary.IsConnected)
            {
                await _tertiary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            LastChannel = "mailbox";
            LastFallbackReason = "explicit_channel_override";
            return await _tertiary.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(requestedChannel, "named_pipe", StringComparison.OrdinalIgnoreCase))
        {
            if (!_primary.IsConnected)
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            LastChannel = "named_pipe";
            LastFallbackReason = null;
            return await _primary.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        // Priority 1: Primary Native In-Process Pipe (kongor-native-client)
        if (!_primary.IsConnected)
        {
            try
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Primary fast-connect before ExecuteLuaAsync skipped: {ex.Message}");
            }
        }

        Exception? primaryError = null;
        if (_primary.IsConnected)
        {
            try
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                return await _primary.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                throw;
            }
            catch (Exception ex)
            {
                primaryError = ex;
                Log($"[CompositeBridge] Primary Native Lua execution failed ({ex.Message}).");
            }
        }

        if (_secondary.IsConnected)
        {
            try
            {
                LastChannel = "willowmaker";
                LastFallbackReason = primaryError != null ? "primary_io_error" : "primary_disconnected";
                return await _secondary.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Secondary IPC Lua execution failed ({ex.Message}).");
            }
        }

        // Only allow tertiary (file-backed mailbox) fallback when the native pipe does not exist on the host
        if (!IsNativePipePresent() && _tertiary != null)
        {
            if (!_tertiary.IsConnected)
            {
                try
                {
                    await _tertiary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[CompositeBridge] Proactive tertiary Lua fallback failed: {ex.Message}");
                }
            }

            if (_tertiary.IsConnected)
            {
                LastChannel = "mailbox";
                LastFallbackReason = "pipe_absent";
                return await _tertiary.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (primaryError != null)
        {
            Log("[CompositeBridge] Native pipe is present on host; suppressing unrequested 3000ms mailbox fallback for ExecuteLuaAsync.");
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryError).Throw();
        }

        if (!_secondary.IsConnected && _secondary is McpServerIpcBridge)
        {
            throw new IOException($"No active game client IPC connection on '\\\\.\\pipe\\{_primary.PipeName}' or '\\\\.\\pipe\\{_secondary.PipeName}'. Ensure hon_x64.exe is running.");
        }
        LastChannel = "willowmaker";
        LastFallbackReason = "primary_disconnected";
        return await _secondary.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DuplexLuaRpcResult> EvalLuaDuplexAsync(
        string luaCode,
        uint timeoutMs = 3000,
        CancellationToken ct = default)
    {
        try
        {
            if (!_primary.IsConnected)
            {
                try
                {
                    await _primary.ConnectAsync((int)Math.Min(timeoutMs, 500), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[CompositeBridge] Primary fast-connect before EvalLuaDuplexAsync skipped: {ex.Message}");
                }
            }

            if (_primary.IsConnected)
            {
                DuplexLuaRpcResult res = await _primary.EvalLuaDuplexAsync(luaCode, timeoutMs, ct).ConfigureAwait(false);
                if (res.Success ||
                    res.TimedOut ||
                    (!res.TransportUnsupported && !res.IsUninitializedDefault))
                {
                    LastChannel = "named_pipe";
                    LastFallbackReason = null;
                    return res;
                }

                Log($"[CompositeBridge] Primary EvalLuaDuplexAsync reported TransportUnsupported={res.TransportUnsupported}, IsUninitializedDefault={res.IsUninitializedDefault}; falling back to secondary bridge.");
            }
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"[CompositeBridge] Primary EvalLuaDuplexAsync failed: {ex.Message}");
        }

        try
        {
            return await _secondary.EvalLuaDuplexAsync(luaCode, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"[CompositeBridge] Secondary EvalLuaDuplexAsync fallback: {ex.Message}");
            return new DuplexLuaRpcResult(false, false, 0, 0, "Duplex Lua RPC not supported by secondary bridge", TransportUnsupported: true);
        }
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

        if (!_primary.IsConnected)
        {
            try
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Primary fast-connect before DispatchUiEventAsync skipped: {ex.Message}");
            }
        }

        Exception? primaryError = null;
        if (_primary.IsConnected)
        {
            try
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                return await _primary.DispatchUiEventAsync(interfaceName, widgetName, eventType, eventIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                throw;
            }
            catch (Exception ex)
            {
                primaryError = ex;
                Log($"[CompositeBridge] Primary Native UI event dispatch failed ({ex.Message}).");
            }
        }

        if (_secondary.IsConnected)
        {
            LastChannel = "willowmaker";
            LastFallbackReason = primaryError != null ? "primary_io_error" : "primary_disconnected";
            return await _secondary.DispatchUiEventAsync(interfaceName, widgetName, eventType, eventIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (!IsNativePipePresent() && _tertiary != null)
        {
            if (!_tertiary.IsConnected)
            {
                try
                {
                    await _tertiary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[CompositeBridge] Proactive tertiary DispatchUiEventAsync fallback failed: {ex.Message}");
                }
            }

            if (_tertiary.IsConnected)
            {
                LastChannel = "mailbox";
                LastFallbackReason = "pipe_absent";
                return await _tertiary.DispatchUiEventAsync(interfaceName, widgetName, eventType, eventIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (primaryError != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryError).Throw();
        }

        LastChannel = "willowmaker";
        LastFallbackReason = "primary_disconnected";
        return await _secondary.DispatchUiEventAsync(interfaceName, widgetName, eventType, eventIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ExecuteRpcAsync<T>(
        string method,
        object? parameters = null,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (_tertiary?.IsConnected == true)
        {
            try
            {
                return await _tertiary.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_primary.IsConnected || _secondary.IsConnected)
            {
                Log($"[CompositeBridge] Tertiary Mailbox RPC failed ({ex.Message}). Falling back.");
            }
        }

        if (_primary.IsConnected)
        {
            try
            {
                return await _primary.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected)
            {
                Log($"[CompositeBridge] Primary RPC failed ({ex.Message}). Falling back to secondary IPC.");
            }
        }

        if (_secondary.IsConnected)
        {
            return await _secondary.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (_tertiary != null)
        {
            try
            {
                await _tertiary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
                if (_tertiary.IsConnected)
                {
                    return await _tertiary.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Proactive tertiary ExecuteRpcAsync fallback failed: {ex.Message}");
            }
        }

        return await _primary.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> PingAsync(int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        if (!_primary.IsConnected)
        {
            try
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Primary fast-connect in PingAsync skipped: {ex.Message}");
            }
        }

        if (_primary.IsConnected)
        {
            try
            {
                LastChannel = "named_pipe";
                LastFallbackReason = null;
                return await _primary.PingAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected || (!IsNativePipePresent() && _tertiary?.IsConnected == true))
            {
                Log($"[CompositeBridge] Primary PingAsync failed ({ex.Message}); falling back.");
            }
        }

        if (_secondary.IsConnected)
        {
            try
            {
                LastChannel = "willowmaker";
                LastFallbackReason = "primary_disconnected";
                return await _secondary.PingAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsNativePipePresent() && _tertiary?.IsConnected == true)
            {
                Log($"[CompositeBridge] Secondary PingAsync failed ({ex.Message}); falling back to tertiary.");
            }
        }

        if (!IsNativePipePresent() && _tertiary?.IsConnected == true)
        {
            LastChannel = "mailbox";
            LastFallbackReason = "pipe_absent";
            return await _tertiary.PingAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        LastChannel = "named_pipe";
        LastFallbackReason = null;
        return await _primary.PingAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EngineStateDto?> GetEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (!_primary.IsConnected)
        {
            try
            {
                await _primary.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Primary fast-connect in GetEngineStateAsync skipped: {ex.Message}");
            }
        }

        if (_primary.IsConnected)
        {
            try
            {
                var state = await _primary.GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                if (state != null) return state;
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Primary GetEngineStateAsync failed: {ex.Message}");
            }
        }

        if (_secondary.IsConnected)
        {
            try
            {
                var state = await _secondary.GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                if (state != null) return state;
            }
            catch (Exception ex)
            {
                Log($"[CompositeBridge] Secondary GetEngineStateAsync failed: {ex.Message}");
            }
        }

        if (!IsNativePipePresent() && _tertiary?.IsConnected == true)
        {
            return await _tertiary.GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        return await _primary.GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (_tertiary?.IsConnected == true)
        {
            return await _tertiary.WaitForEventAsync(eventName, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        if (_primary.IsConnected)
        {
            return await _primary.WaitForEventAsync(eventName, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        return await _secondary.WaitForEventAsync(eventName, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        if (_primary.IsConnected)
        {
            await _primary.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_tertiary?.IsConnected == true)
        {
            await _tertiary.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _secondary.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcMessage?> SendRequestAsync(IpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (_primary.IsConnected)
        {
            try
            {
                return await _primary.SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected || _tertiary?.IsConnected == true)
            {
                Log($"[CompositeBridge] Primary SendRequestAsync failed ({ex.Message}); falling back.");
            }
        }
        if (_tertiary?.IsConnected == true)
        {
            try
            {
                return await _tertiary.SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected)
            {
                Log($"[CompositeBridge] Tertiary SendRequestAsync failed ({ex.Message}); falling back to secondary.");
            }
        }
        return await _secondary.SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SubTickInputResult> InjectSubTickInputAsync(
        string action,
        long hardwareTimestampQpc = 0,
        uint inputSequence = 0,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        if (_primary.IsConnected)
        {
            try
            {
                return await _primary.InjectSubTickInputAsync(action, hardwareTimestampQpc, inputSequence, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected || _tertiary?.IsConnected == true)
            {
                Log($"[CompositeBridge] Primary InjectSubTickInputAsync failed ({ex.Message}); falling back.");
            }
        }

        if (_tertiary?.IsConnected == true)
        {
            try
            {
                return await _tertiary.InjectSubTickInputAsync(action, hardwareTimestampQpc, inputSequence, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected)
            {
                Log($"[CompositeBridge] Tertiary InjectSubTickInputAsync failed ({ex.Message}); falling back to secondary.");
            }
        }

        return await _secondary.InjectSubTickInputAsync(action, hardwareTimestampQpc, inputSequence, timeoutMs, cancellationToken).ConfigureAwait(false);
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
        if (_primary.IsConnected)
        {
            try
            {
                return await _primary.PushTelemetryUpdateAsync(drawCalls, activeEntities, particlesAlive, pingMicroseconds, serverTick, inputLatencyMs, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected || _tertiary?.IsConnected == true)
            {
                Log($"[CompositeBridge] Primary PushTelemetryUpdateAsync failed ({ex.Message}); falling back.");
            }
        }

        if (_tertiary?.IsConnected == true)
        {
            try
            {
                return await _tertiary.PushTelemetryUpdateAsync(drawCalls, activeEntities, particlesAlive, pingMicroseconds, serverTick, inputLatencyMs, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_secondary.IsConnected)
            {
                Log($"[CompositeBridge] Tertiary PushTelemetryUpdateAsync failed ({ex.Message}); falling back to secondary.");
            }
        }

        return await _secondary.PushTelemetryUpdateAsync(drawCalls, activeEntities, particlesAlive, pingMicroseconds, serverTick, inputLatencyMs, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        try { await _primary.DisconnectAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Primary DisconnectAsync warning: {ex.Message}"); }
        try { await _secondary.DisconnectAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Secondary DisconnectAsync warning: {ex.Message}"); }
        if (_tertiary != null)
        {
            try { await _tertiary.DisconnectAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Tertiary DisconnectAsync warning: {ex.Message}"); }
        }
        OnDisconnected?.Invoke(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { await _primary.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Primary DisposeAsync warning: {ex.Message}"); }
        try { await _secondary.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Secondary DisposeAsync warning: {ex.Message}"); }
        if (_tertiary != null)
        {
            try { await _tertiary.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"[CompositeBridge] Tertiary DisposeAsync warning: {ex.Message}"); }
        }
    }
}
