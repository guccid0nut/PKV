// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using NEXUS.Contracts.Ipc;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Native in-process Named Pipe IPC bridge implementing <see cref="IGameIpcBridge"/>.
/// Communicates directly with the native pipe server hosted inside <c>version.dll</c>
/// (<c>\\.\pipe\kongor-native-client</c>) running within the <c>hon_x64.exe</c> game process.
/// Provides sub-millisecond command ingestion during loading screens and match phases.
/// </summary>
public sealed class NativeClientIpcBridge : IGameIpcBridge
{
    public const string DefaultNativePipeName = "kongor-native-client";

    private readonly GameIpcBridge _inner;
    private readonly Action<string>? _logger;
    private bool _isDisposed;

    public string PipeName => _inner.PipeName;
    public string ServerName => _inner.ServerName;
    public bool IsConnected => (!_isDisposed) && _inner.IsConnected;
    public bool IsAuthenticated => (!_isDisposed) && _inner.IsAuthenticated;

    public string? AuthKey
    {
        get => _inner.AuthKey;
        set => _inner.AuthKey = value;
    }

    public event Action? OnConnected
    {
        add => _inner.OnConnected += value;
        remove => _inner.OnConnected -= value;
    }

    public event Action<Exception?>? OnDisconnected
    {
        add => _inner.OnDisconnected += value;
        remove => _inner.OnDisconnected -= value;
    }

    public event Action<IpcMessage>? OnMessageReceived
    {
        add => _inner.OnMessageReceived += value;
        remove => _inner.OnMessageReceived -= value;
    }

    public event Action<string>? OnLogStreamReceived
    {
        add => _inner.OnLogStreamReceived += value;
        remove => _inner.OnLogStreamReceived -= value;
    }

    public NativeClientIpcBridge(
        string? pipeName = null,
        string serverName = ".",
        Action<string>? logger = null,
        string? authKey = null)
    {
        _logger = logger;
        _inner = new GameIpcBridge(
            pipeName ?? DefaultNativePipeName,
            serverName,
            msg => Log(msg),
            authKey);
    }

    private void Log(string message) => _logger?.Invoke(message);

    public async Task ConnectAsync(int timeoutMs = 1500, CancellationToken cancellationToken = default)
    {
        Log($"[NativeClientIpcBridge] Probing in-process pipe: \\\\{ServerName}\\pipe\\{PipeName} (Timeout: {timeoutMs}ms)...");
        await _inner.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        return await _inner.AuthenticateAsync(authKey, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecuteCommandAsync(string command, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Log($"[NativeClientIpcBridge] Dispatching native command: '{command}' (Timeout: {timeoutMs}ms)");
        return await _inner.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecuteLuaAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);
        Log($"[NativeClientIpcBridge] Dispatching native synchronous Lua evaluation (Timeout: {timeoutMs}ms)");
        return await _inner.ExecuteLuaAsync(luaCode, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public Task<DuplexLuaRpcResult> EvalLuaDuplexAsync(string luaCode, uint timeoutMs = 3000, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);
        Log($"[NativeClientIpcBridge] Dispatching native duplex Lua RPC evaluation (Timeout: {timeoutMs}ms)");
        return _inner.EvalLuaDuplexAsync(luaCode, timeoutMs, ct);
    }

    public async Task<EngineStateDto?> GetEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        return await _inner.GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> PingAsync(int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        return await _inner.PingAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        await _inner.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcMessage?> SendRequestAsync(IpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        return await _inner.SendRequestAsync(request, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public static string EscapeLuaSingleQuoted(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    public static string BuildSafeUiDispatchLua(
        string interfaceName,
        string widgetName,
        string eventType,
        int? eventIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetName);
        string safeIface = EscapeLuaSingleQuoted(string.IsNullOrWhiteSpace(interfaceName) ? "main" : interfaceName);
        string safeWidget = EscapeLuaSingleQuoted(widgetName);
        string safeEvent = EscapeLuaSingleQuoted(string.IsNullOrWhiteSpace(eventType) ? "click" : eventType);

        if (eventIndex.HasValue)
        {
            return
                $"local iface = (UIManager and UIManager.GetInterface and UIManager.GetInterface('{safeIface}')) or (UIManager and UIManager.GetActiveInterface and UIManager.GetActiveInterface()) " +
                $"local names = {{'{safeWidget}', '{safeWidget}_btn', '{safeWidget}_button', '{safeWidget}_catcher'}} " +
                $"for _, n in ipairs(names) do " +
                $"  local w = (iface and iface.GetWidget and iface:GetWidget(n)) or (GetWidget and GetWidget(n, '{safeIface}', true)) " +
                $"  if w and w.DoEventN then w:DoEventN({eventIndex.Value}) return 'DISPATCHED:' .. n end " +
                $"end";
        }

        bool isClickLike = safeEvent.Equals("click", StringComparison.OrdinalIgnoreCase) ||
                           safeEvent.Equals("onclick", StringComparison.OrdinalIgnoreCase);
        string callbackKey = isClickLike ? "onclick" : safeEvent;

        return
            $"local iface = (UIManager and UIManager.GetInterface and UIManager.GetInterface('{safeIface}')) or (UIManager and UIManager.GetActiveInterface and UIManager.GetActiveInterface()) " +
            $"local names = {{'{safeWidget}', '{safeWidget}_btn', '{safeWidget}_button', '{safeWidget}_catcher'}} " +
            $"for _, n in ipairs(names) do " +
            $"  local w = (iface and iface.GetWidget and iface:GetWidget(n)) or (GetWidget and GetWidget(n, '{safeIface}', true)) " +
            $"  if w then " +
            $"    local acted = false " +
            $"    if w.GetCallback then local okCb, cb = pcall(w.GetCallback, w, '{callbackKey}') if okCb and type(cb) == 'function' then pcall(cb, w) acted = true end end " +
            $"    if ({(isClickLike ? "true" : "false")}) and type(w.onclicklua) == 'string' and #w.onclicklua > 0 then if iface and iface.UICmd then pcall(iface.UICmd, iface, w.onclicklua) acted = true else local fn = loadstring(w.onclicklua) if fn then pcall(fn) acted = true end end end " +
            $"    if not acted and w.DoEvent then pcall(function() w:DoEvent() end) end " +
            $"    return 'DISPATCHED:' .. n " +
            $"  end " +
            $"end";
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
        return await _inner.DispatchUiEventAsync(interfaceName, widgetName, eventType, eventIndex, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        return await _inner.WaitForEventAsync(eventName, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ExecuteRpcAsync<T>(string method, object? parameters = null, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        return await _inner.ExecuteRpcAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SubTickInputResult> InjectSubTickInputAsync(
        string action,
        long hardwareTimestampQpc = 0,
        uint inputSequence = 0,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        return await _inner.InjectSubTickInputAsync(action, hardwareTimestampQpc, inputSequence, timeoutMs, cancellationToken).ConfigureAwait(false);
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
        return await _inner.PushTelemetryUpdateAsync(drawCalls, activeEntities, particlesAlive, pingMicroseconds, serverTick, inputLatencyMs, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _inner.DisconnectAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
