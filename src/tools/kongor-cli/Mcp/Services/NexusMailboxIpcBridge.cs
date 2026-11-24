// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using NEXUS.Contracts.Ipc;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// In-Engine Lua Mailbox IPC Bridge implementing <see cref="IGameIpcBridge"/>.
/// Communicates with the K2 engine client via userdataKONGOR/extensions/nexus_ipc/req_current.lua
/// and console.log / res_{id}.json, executing commands, RPCs, Lua snippets, and UI triggers without
/// stealing window focus, simulating keyboard strokes (Ctrl+F8), or moving the mouse cursor.
/// </summary>
public sealed class NexusMailboxIpcBridge : IGameIpcBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _mailboxDir;
    private readonly bool _hasCustomMailboxDir;
    private readonly Action<string>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MailboxResponseRecord>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> _responseChunks = new();
    private readonly ConsoleLogDemuxService _demuxService;
    private int _inFlightRequestsCount;
    private readonly IGameIpcBridge _signalBridge;
    private readonly bool _ownsSignalBridge;
    private readonly NexusEventCoordinator _eventCoordinator;
    private int _reqIdCounter;
    private bool _isDisposed;
    private bool _manuallyDisconnected;
    private bool? _forceConnected;

    public int PendingRequestsCount => _pendingRequests.Count;

    public string PipeName => "nexus-mailbox";
    public string ServerName => ".";
    public string MailboxDirectory => _mailboxDir;

    public bool? ForceConnected
    {
        get => _forceConnected;
        set => _forceConnected = value;
    }

    public bool IsConnected
    {
        get
        {
            if (_isDisposed || _manuallyDisconnected) return false;
            if (_forceConnected.HasValue) return _forceConnected.Value;

            if (!Directory.Exists(_mailboxDir)) return false;

            if (IsGameClientRunning()) return true;

            if (HasRecentMailboxActivity(TimeSpan.FromSeconds(15))) return true;

            return false;
        }
    }

    public bool IsAuthenticated { get; private set; } = true;
    public string? AuthKey { get; set; }

    public event Action? OnConnected;
    public event Action<Exception?>? OnDisconnected;
    public event Action<IpcMessage>? OnMessageReceived;
    public event Action<string>? OnLogStreamReceived;

    public NexusMailboxIpcBridge(
        string? mailboxDir = null,
        Action<string>? logger = null,
        TimeProvider? timeProvider = null,
        string? authKey = null,
        IGameIpcBridge? signalBridge = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reqIdCounter = timeProvider != null
            ? (int)(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() & 0x3FFFFFFF)
            : (((Environment.ProcessId & 0x3FFF) << 16) | (int)(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() & 0xFFFF));
        AuthKey = authKey
            ?? Environment.GetEnvironmentVariable("KONGOR_IPC_TOKEN")
            ?? Environment.GetEnvironmentVariable("KONGOR_IPC_AUTH_KEY");
        _hasCustomMailboxDir = !string.IsNullOrWhiteSpace(mailboxDir);
        _mailboxDir = ResolveMailboxDirectory(mailboxDir);
        _ownsSignalBridge = signalBridge == null;
        _signalBridge = signalBridge ?? new GameIpcBridge(NativeClientIpcBridge.DefaultNativePipeName, ".", msg => Log(msg), AuthKey);
        _eventCoordinator = new NexusEventCoordinator((cmd, timeout, ct) => ExecuteCommandAsync(cmd, timeout, ct), _logger);
        _demuxService = new ConsoleLogDemuxService(ResolveConsoleLogPath(), msg => Log(msg));

        try
        {
            if (!Directory.Exists(_mailboxDir))
            {
                Directory.CreateDirectory(_mailboxDir);
            }
            CleanupStaleFiles();
        }
        catch (Exception ex)
        {
            Log($"[NexusMailboxIpcBridge] Warning initializing mailbox directory '{_mailboxDir}': {ex.Message}");
        }
    }

    private void Log(string message)
    {
        if (_logger != null) _logger(message);
        else Console.Error.WriteLine(message);
    }

    public static string FindRepoRoot(string? startingDir = null)
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(
                out var ws,
                new Kongor.Core.Configuration.WorkspaceResolveOptions
                {
                    InvocationCwd = !string.IsNullOrWhiteSpace(startingDir) ? startingDir : Environment.CurrentDirectory
                }) && ws is not null)
        {
            return ws.Root;
        }

        return !string.IsNullOrWhiteSpace(startingDir) ? startingDir : Environment.CurrentDirectory;
    }

    public static string ResolveMailboxDirectory(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string currentDir = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(repoRoot, "games", "client", "userdataKONGOR", "extensions", "nexus_ipc"),
            Path.Combine(currentDir, "games", "client", "userdataKONGOR", "extensions", "nexus_ipc"),
            Path.Combine(repoRoot, "userdataKONGOR", "extensions", "nexus_ipc"),
            Path.Combine(currentDir, "userdataKONGOR", "extensions", "nexus_ipc"),
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "nexus_ipc"),
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "nexus_ipc"),
            Path.Combine(baseDir, "games", "client", "userdataKONGOR", "extensions", "nexus_ipc"),
            Path.Combine(baseDir, "userdataKONGOR", "extensions", "nexus_ipc"),
            Path.Combine(currentDir, "nexus_ipc")
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(candidates[0]);
    }

    private void CleanupStaleFiles()
    {
        try
        {
            if (!Directory.Exists(_mailboxDir)) return;

            var dirInfo = new DirectoryInfo(_mailboxDir);
            DateTime threshold = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5);

            string reqCurrentLua = Path.Combine(_mailboxDir, "req_current.lua");
            try
            {
                if (!File.Exists(reqCurrentLua) || File.GetLastWriteTimeUtc(reqCurrentLua) < threshold)
                {
                    File.WriteAllText(reqCurrentLua, "return { id = \"idle\", type = \"idle\" }\n", Utf8NoBom);
                }
            }
            catch (Exception ex) { Log($"[NexusMailboxIpcBridge] Ignored CleanupStaleFiles req_current.lua reset error: {ex.Message}"); }

            string reqCurrentJson = Path.Combine(_mailboxDir, "req_current.json");
            try
            {
                if (File.Exists(reqCurrentJson) && File.GetLastWriteTimeUtc(reqCurrentJson) < threshold)
                {
                    File.Delete(reqCurrentJson);
                }
            }
            catch (Exception ex) { Log($"[NexusMailboxIpcBridge] Ignored CleanupStaleFiles req_current.json delete error: {ex.Message}"); }

            foreach (var file in dirInfo.GetFiles("req_*.tmp"))
            {
                try { if (file.LastWriteTimeUtc < threshold) file.Delete(); } catch (Exception ex) { Log($"[NexusMailboxIpcBridge] Ignored CleanupStaleFiles tmp delete error: {ex.Message}"); }
            }
            foreach (var file in dirInfo.GetFiles("req_*.lua"))
            {
                try { if (file.LastWriteTimeUtc < threshold) file.Delete(); } catch (Exception ex) { Log($"[NexusMailboxIpcBridge] Ignored CleanupStaleFiles lua delete error: {ex.Message}"); }
            }
            foreach (var file in dirInfo.GetFiles("res_*.json"))
            {
                if (file.Name.Equals("res_last.json", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (file.LastWriteTimeUtc < threshold) file.Delete(); } catch (Exception ex) { Log($"[NexusMailboxIpcBridge] Ignored CleanupStaleFiles json delete error: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log($"[NexusMailboxIpcBridge] Ignored CleanupStaleFiles error: {ex.Message}");
        }
    }

    private string ResolveConsoleLogPath()
    {
        // 1. Direct in mailboxDir (useful for tests or custom paths)
        string inMailbox = Path.Combine(_mailboxDir, "console.log");
        if (File.Exists(inMailbox)) return inMailbox;

        if (!string.IsNullOrEmpty(_mailboxDir) &&
            !_mailboxDir.Contains("games", StringComparison.OrdinalIgnoreCase) &&
            !_mailboxDir.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase))
        {
            return inMailbox;
        }

        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string currentDir = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory;

        string[] candidateDirs =
        [
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "override"),
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "override"),
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "game"),
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "game"),
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "base"),
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "base"),
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "KONGOR", "extensions"),
            Path.Combine(currentDir, "games", "client", "userdata", "Heroes of Newerth x64", "KONGOR", "extensions"),
            Path.Combine(repoRoot, "games", "client", "userdataKONGOR", "extensions"),
            Path.Combine(currentDir, "games", "client", "userdataKONGOR", "extensions"),
            Path.Combine(currentDir, "userdataKONGOR", "extensions"),
            Path.Combine(repoRoot, "userdataKONGOR", "extensions"),
            Path.Combine(baseDir, "games", "client", "userdataKONGOR", "extensions"),
            Path.GetDirectoryName(_mailboxDir) ?? _mailboxDir
        ];

        string? newestLog = null;
        DateTime newestTime = DateTime.MinValue;

        foreach (string dir in candidateDirs)
        {
            string path = Path.Combine(dir, "console.log");
            if (File.Exists(path))
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite > newestTime)
                {
                    newestTime = lastWrite;
                    newestLog = path;
                }
            }
        }

        if (newestLog != null) return newestLog;

        string parentDir = Path.GetDirectoryName(_mailboxDir) ?? _mailboxDir;
        return Path.Combine(parentDir, "console.log");
    }

    private static string? ResolveOverrideScriptDirectory()
    {
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string repoOverride = Path.Combine(repoRoot, "games", "client", "override", "ui", "scripts", "nexus");
        if (Directory.Exists(repoOverride)) return repoOverride;

        string clientDir = Path.Combine(Environment.CurrentDirectory, "games", "client", "override", "ui", "scripts", "nexus");
        if (Directory.Exists(clientDir)) return clientDir;

        string baseClientDir = Path.Combine(AppContext.BaseDirectory, "games", "client", "override", "ui", "scripts", "nexus");
        if (Directory.Exists(baseClientDir)) return baseClientDir;

        return null;
    }

    private bool IsGameClientRunning()
    {
        if (GameServerManageHandler.IsTestExecution && _forceConnected != true) return false;

        try
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            foreach (var p in procs)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    if (p.HasExited) continue;
                    string? cmd = GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                    if (cmd != null && cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NexusMailboxIpcBridge] Ignored process inspection error in IsGameClientRunning: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NexusMailboxIpcBridge] Ignored GetProcessesByName error in IsGameClientRunning: {ex.Message}");
        }
        return false;
    }

    private bool HasRecentMailboxActivity(TimeSpan maxAge)
    {
        try
        {
            if (!_hasCustomMailboxDir)
            {
                string consoleLog = ResolveConsoleLogPath();
                if (File.Exists(consoleLog))
                {
                    var diff = _timeProvider.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(consoleLog);
                    if (diff <= maxAge) return true;
                }
            }

            string lastRes = Path.Combine(_mailboxDir, "res_last.json");
            if (File.Exists(lastRes))
            {
                var diff = _timeProvider.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(lastRes);
                if (diff <= maxAge) return true;
            }

            string heartbeat = Path.Combine(_mailboxDir, "heartbeat.json");
            if (File.Exists(heartbeat))
            {
                var diff = _timeProvider.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(heartbeat);
                if (diff <= maxAge) return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NexusMailboxIpcBridge] Ignored HasRecentMailboxActivity error: {ex.Message}");
        }
        return false;
    }

    public async Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        _manuallyDisconnected = false;

        if (!Directory.Exists(_mailboxDir))
        {
            Directory.CreateDirectory(_mailboxDir);
        }

        if (IsConnected)
        {
            if (_ownsSignalBridge && !_signalBridge.IsConnected && IsGameClientRunning())
            {
                try
                {
                    await _signalBridge.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                    if (_signalBridge.IsConnected)
                    {
                        await EnsureEngineArmedAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[NexusMailboxIpcBridge] Ignored signalBridge connect error in ConnectAsync: {ex.Message}");
                }
            }

            OnConnected?.Invoke();
            return;
        }

        // Offline fast-path: when neither hon_x64 nor recent mailbox heartbeat is active,
        // return immediately (< 1ms) rather than polling Task.Delay(50) up to timeoutMs.
        if (_forceConnected == false ||
            (_forceConnected == null && !IsGameClientRunning() && !HasRecentMailboxActivity(TimeSpan.FromSeconds(15))))
        {
            Log($"[NexusMailboxIpcBridge] Fast-path offline in ConnectAsync: no running game client or active mailbox heartbeat at '{_mailboxDir}'.");
            return;
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        try
        {
            while (!IsConnected && !linked.IsCancellationRequested)
            {
                await Task.Delay(50, linked.Token).ConfigureAwait(false);
            }

            if (IsConnected)
            {
                if (_ownsSignalBridge && !_signalBridge.IsConnected && IsGameClientRunning())
                {
                    try
                    {
                        await _signalBridge.ConnectAsync(500, linked.Token).ConfigureAwait(false);
                        if (_signalBridge.IsConnected)
                        {
                            await EnsureEngineArmedAsync(linked.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[NexusMailboxIpcBridge] Ignored signalBridge connect error in ConnectAsync loop: {ex.Message}");
                    }
                }

                OnConnected?.Invoke();
                Log($"[NexusMailboxIpcBridge] Connected to game client mailbox at '{_mailboxDir}'.");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log($"[NexusMailboxIpcBridge] ConnectAsync timed out after {timeoutMs}ms (no running game client detected).");
        }
    }

    public Task<bool> AuthenticateAsync(string? authKey = null, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        IsAuthenticated = true;
        return Task.FromResult(true);
    }

    public async Task<string> ExecuteCommandAsync(string command, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        string trimmed = command.Trim();
        if (trimmed.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteLuaAsync(trimmed[7..].TrimStart(), timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = new
        {
            id = reqId,
            type = "command",
            command = trimmed,
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        Log($"[NexusMailboxIpcBridge] Dispatching command #{reqId}: '{command}'");
        var res = await SendMailboxRequestAsync(reqId, payload, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (!res.Success)
        {
            string err = res.Error ?? "Command execution failed in engine.";
            Log($"[NexusMailboxIpcBridge] Command #{reqId} error: {err}");
            return $"[ERROR] {err}";
        }

        string resultString = res.Result.ValueKind switch
        {
            JsonValueKind.String => res.Result.GetString() ?? string.Empty,
            JsonValueKind.Object => res.Result.GetRawText(),
            JsonValueKind.Array => res.Result.GetRawText(),
            _ => $"Executed command '{command}' successfully via Nexus Mailbox IPC."
        };

        Log($"[NexusMailboxIpcBridge] Command #{reqId} succeeded: {resultString}");
        return resultString;
    }

    public async Task<string> ExecuteLuaAsync(string luaCode, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaCode);

        string cleanLua = luaCode.Trim();
        if (cleanLua.StartsWith("Script ", StringComparison.OrdinalIgnoreCase))
        {
            cleanLua = cleanLua[7..].TrimStart();
        }

        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = new
        {
            id = reqId,
            type = "lua",
            code = cleanLua,
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        Log($"[NexusMailboxIpcBridge] Dispatching Lua #{reqId}: '{cleanLua}'");
        var res = await SendMailboxRequestAsync(reqId, payload, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (!res.Success)
        {
            string err = res.Error ?? "Lua execution failed in engine.";
            Log($"[NexusMailboxIpcBridge] Lua #{reqId} error: {err}");
            return $"[ERROR] {err}";
        }

        string resultString = res.Result.ValueKind switch
        {
            JsonValueKind.String => res.Result.GetString() ?? string.Empty,
            JsonValueKind.Object => res.Result.GetRawText(),
            JsonValueKind.Array => res.Result.GetRawText(),
            JsonValueKind.Null => "Lua executed successfully via Nexus Mailbox IPC.",
            JsonValueKind.Undefined => "Lua executed successfully via Nexus Mailbox IPC.",
            _ => res.Result.GetRawText()
        };

        if (string.IsNullOrEmpty(resultString) || resultString == "null")
        {
            resultString = "Lua executed successfully via Nexus Mailbox IPC.";
        }

        Log($"[NexusMailboxIpcBridge] Lua #{reqId} succeeded: {resultString}");
        return resultString;
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

        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = new
        {
            id = reqId,
            type = "ui",
            @interface = interfaceName,
            widget = widgetName,
            action = eventType,
            eventIndex,
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        Log($"[NexusMailboxIpcBridge] Dispatching UI event #{reqId}: {interfaceName}:{widgetName}.{eventType} (index: {eventIndex})");
        var res = await SendMailboxRequestAsync(reqId, payload, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (!res.Success)
        {
            string err = res.Error ?? $"Failed to dispatch UI event to widget '{widgetName}'.";
            Log($"[NexusMailboxIpcBridge] UI event #{reqId} error: {err}");
            throw new InvalidOperationException($"UI event dispatch failed: {err}");
        }

        string output = $"Triggered UI event '{eventType}' on widget '{widgetName}' (interface: '{interfaceName}') via Nexus Mailbox IPC.";
        Log($"[NexusMailboxIpcBridge] UI event #{reqId} succeeded.");
        return output;
    }

    public async Task<MailboxResponseRecord> DispatchTaoRawAsync(
        string action,
        object? payload = null,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var reqPayload = new
        {
            id = reqId,
            type = "tao",
            action = action.Trim(),
            payload,
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        Log($"[NexusMailboxIpcBridge] Dispatching TAO action #{reqId}: '{action}'");
        return await SendMailboxRequestAsync(reqId, reqPayload, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ExecuteRpcAsync<T>(string method, object? parameters = null, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = new
        {
            id = reqId,
            type = "rpc",
            method = method.Trim(),
            @params = parameters,
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        Log($"[NexusMailboxIpcBridge] Dispatching RPC #{reqId}: '{method}'");
        var res = await SendMailboxRequestAsync(reqId, payload, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (!res.Success)
        {
            string err = res.Error ?? $"RPC method '{method}' returned error.";
            Log($"[NexusMailboxIpcBridge] RPC #{reqId} error: {err}");
            throw new InvalidOperationException($"RPC '{method}' failed: {err}");
        }

        if (typeof(T) == typeof(JsonElement))
        {
            return (T)(object)res.Result;
        }

        if (res.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(res.Result.GetRawText(), JsonOptions);
    }

    public async Task<long> PingAsync(int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = new
        {
            id = reqId,
            type = "ping",
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        var sw = Stopwatch.StartNew();
        var res = await SendMailboxRequestAsync(reqId, payload, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (!res.Success)
        {
            throw new InvalidOperationException($"Ping #{reqId} failed: {res.Error}");
        }

        return sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
    }

    public async Task<EngineStateDto?> GetEngineStateAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (IsGameClientRunning())
        {
            try
            {
                if (!_signalBridge.IsConnected)
                {
                    await _signalBridge.ConnectAsync(Math.Min(timeoutMs, 500), cancellationToken).ConfigureAwait(false);
                }
                if (_signalBridge.IsConnected)
                {
                    var nativeState = await _signalBridge.GetEngineStateAsync(Math.Min(timeoutMs, 1000), cancellationToken).ConfigureAwait(false);
                    if (nativeState != null)
                    {
                        return nativeState;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[NexusMailboxIpcBridge] Native GetEngineState fast-path fallback: {ex.Message}");
            }
        }

        try
        {
            var state = await ExecuteRpcAsync<EngineStateDto>("core.get_engine_state", null, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (state != null) return state;
        }
        catch (Exception ex)
        {
            Log($"[NexusMailboxIpcBridge] Ignored core.get_engine_state RPC error: {ex.Message}");
        }

        int reqId = Interlocked.Increment(ref _reqIdCounter);
        var payload = new
        {
            id = reqId,
            type = "ping",
            timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        var res = await SendMailboxRequestAsync(reqId, payload, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (res.Success && res.Result.ValueKind == JsonValueKind.Object)
        {
            int phase = res.Result.TryGetProperty("phase", out var pElem) && pElem.TryGetInt32(out int p) ? p : 0;
            return new EngineStateDto
            {
                GamePhase = phase,
                GamePhaseName = phase == 3 ? "Active Match (3)" : (phase == 0 ? "Login (0)" : $"Phase ({phase})"),
                CurrentFps = 120.0
            };
        }

        return null;
    }

    public Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
        => _eventCoordinator.WaitForEventAsync(eventName, timeoutMs, cancellationToken);

    public async Task SendMessageAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.OpCode == IpcOpCode.ExecuteCommand)
        {
            await ExecuteCommandAsync(message.PayloadString, 3000, cancellationToken).ConfigureAwait(false);
        }
        else if (message.OpCode == IpcOpCode.Ping)
        {
            await PingAsync(1000, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IpcMessage?> SendRequestAsync(IpcMessage request, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.OpCode)
        {
            case IpcOpCode.ExecuteCommand:
            {
                string res = await ExecuteCommandAsync(request.PayloadString, timeoutMs, cancellationToken).ConfigureAwait(false);
                return IpcMessage.CreateString(IpcOpCode.CommandResponse, res, request.RequestId);
            }

            case IpcOpCode.Ping:
            {
                await PingAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                return IpcMessage.CreateString(IpcOpCode.Pong, "PONG", request.RequestId);
            }

            case IpcOpCode.GetEngineState:
            {
                var state = await GetEngineStateAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                return state != null
                    ? IpcMessage.CreateJson(IpcOpCode.EngineStateResponse, state, request.RequestId)
                    : IpcMessage.CreateString(IpcOpCode.EngineStateResponse, "{}", request.RequestId);
            }

            case IpcOpCode.DispatchUiEvent:
            {
                using var doc = JsonDocument.Parse(request.PayloadString);
                var root = doc.RootElement;
                string iface = root.TryGetProperty("interfaceName", out var iElem) ? iElem.GetString() ?? "main" : "main";
                string widget = root.TryGetProperty("widgetName", out var wElem) ? wElem.GetString() ?? string.Empty : string.Empty;
                string ev = root.TryGetProperty("eventType", out var eElem) ? eElem.GetString() ?? "click" : "click";
                int? idx = root.TryGetProperty("eventIndex", out var idxElem) && idxElem.TryGetInt32(out int iv) ? iv : null;

                string res = await DispatchUiEventAsync(iface, widget, ev, idx, timeoutMs, cancellationToken).ConfigureAwait(false);
                return IpcMessage.CreateString(IpcOpCode.CommandResponse, res, request.RequestId);
            }

            default:
                throw new NotSupportedException($"OpCode '{request.OpCode}' is not supported over Mailbox IPC.");
        }
    }

    public Task DisconnectAsync()
    {
        _manuallyDisconnected = true;
        _eventCoordinator.Reset();
        OnDisconnected?.Invoke(null);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();

        await DisconnectAsync().ConfigureAwait(false);
        await _demuxService.DisposeAsync().ConfigureAwait(false);
        if (_ownsSignalBridge)
        {
            await _signalBridge.DisposeAsync().ConfigureAwait(false);
        }
        _sendLock.Dispose();
    }

    public sealed record MailboxResponseRecord(int Id, string Type, bool Success, JsonElement Result, string? Error, long Timestamp);

    private bool _engineArmed;

    private async Task EnsureEngineArmedAsync(CancellationToken cancellationToken)
    {
        if (_engineArmed || !_signalBridge.IsConnected) return;

        try
        {
            if (!string.IsNullOrWhiteSpace(AuthKey))
            {
                string safeTokenLiteral = WrapSafeLuaLongBracket(AuthKey);
                await _signalBridge.ExecuteCommandAsync(
                    $"Script if Cvar and Cvar.CreateCvar then local cv = Cvar.GetCvar('_kongor_ipc_token') or Cvar.CreateCvar('_kongor_ipc_token', 'string', '') if cv then cv:Set({safeTokenLiteral}) end end",
                    1000,
                    cancellationToken).ConfigureAwait(false);
            }
            await _signalBridge.ExecuteCommandAsync("Script dofile('/ui/scripts/nexus/nexus_mailbox.lua')", 1500, cancellationToken).ConfigureAwait(false);
            await _signalBridge.ExecuteCommandAsync("FlushLogs", 500, cancellationToken).ConfigureAwait(false);
            _engineArmed = true;
            Log("[NexusMailboxIpcBridge] Armed nexus_mailbox.lua and verified log flush.");
        }
        catch (Exception ex)
        {
            Log($"[NexusMailboxIpcBridge] Warning while arming engine: {ex.Message}");
        }
    }

    private async Task SendSignalAsync(string reqId, CancellationToken cancellationToken)
    {
        if (!IsGameClientRunning())
        {
            Log($"[NexusMailboxIpcBridge] Game client not running, skipping native pipe signal for request '{reqId}'.");
            return;
        }

        try
        {
            if (!_signalBridge.IsConnected)
            {
                await _signalBridge.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
            }
            if (_signalBridge.IsConnected)
            {
                if (!_engineArmed)
                {
                    await EnsureEngineArmedAsync(cancellationToken).ConfigureAwait(false);
                }
                string cmd = string.IsNullOrWhiteSpace(reqId)
                    ? "Trigger NexusMailboxSignal \"\"; FlushLogs"
                    : $"Trigger NexusMailboxSignal {reqId}; FlushLogs";
                await _signalBridge.ExecuteCommandAsync(cmd, 1000, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"[NexusMailboxIpcBridge] Signal attempt via native pipe encountered: {ex.Message}");
        }

        throw new InvalidOperationException("Cannot signal K2 engine: Named pipe 'kongor-native-client' is not reachable. Ensure the client is launched using scripts/launch_screen_practice.ps1 with NativeGuard enabled.");
    }

    private async Task<MailboxResponseRecord> SendMailboxRequestAsync(int reqId, object payload, int timeoutMs, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_mailboxDir))
        {
            Directory.CreateDirectory(_mailboxDir);
        }

        string reqLuaFile = Path.Combine(_mailboxDir, "req_current.lua");
        string reqCorrelatedLuaFile = Path.Combine(_mailboxDir, $"req_{reqId}.lua");
        string resFile = Path.Combine(_mailboxDir, $"res_{reqId}.json");

        var tcs = new TaskCompletionSource<MailboxResponseRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[reqId] = tcs;

        // Register with demuxer BEFORE sending signal so lines emitted during signal dispatch are never missed
        Task<string> demuxTask = _demuxService.RegisterPending(reqId.ToString());

        Log($"[NexusMailboxIpcBridge] Starting request #{reqId}: _mailboxDir='{_mailboxDir}', reqCorrelatedLuaFile='{reqCorrelatedLuaFile}'");

        int inFlight = Interlocked.Increment(ref _inFlightRequestsCount);
        try
        {
            // 1. Write request files under brief lock and release IMMEDIATELY
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                string jsonString = Utf8NoBom.GetString(jsonBytes);

                string reqType = "tao";
                string action = "";
                string method = "";
                string code = "";
                string command = "";
                string ifaceName = "";
                string widgetName = "";
                string mapName = "";
                string heroName = "";
                int? eventIndex = null;
                try
                {
                    using var pDoc = JsonDocument.Parse(jsonBytes);
                    var root = pDoc.RootElement;
                    if (root.TryGetProperty("type", out var tProp)) reqType = tProp.GetString() ?? "tao";
                    if (root.TryGetProperty("action", out var aProp)) action = aProp.GetString() ?? "";
                    if (root.TryGetProperty("method", out var mProp)) method = mProp.GetString() ?? "";
                    if (root.TryGetProperty("code", out var cProp)) code = cProp.GetString() ?? "";
                    if (root.TryGetProperty("command", out var cmdProp)) command = cmdProp.GetString() ?? "";
                    if (root.TryGetProperty("interface", out var ifProp)) ifaceName = ifProp.GetString() ?? "";
                    if (root.TryGetProperty("widget", out var wProp)) widgetName = wProp.GetString() ?? "";
                    if (root.TryGetProperty("eventIndex", out var eiProp) && eiProp.ValueKind == JsonValueKind.Number && eiProp.TryGetInt32(out int eiVal)) eventIndex = eiVal;
                    if (root.TryGetProperty("params", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
                    {
                        if (string.IsNullOrEmpty(command) && paramsProp.TryGetProperty("command", out var pCmd)) command = pCmd.GetString() ?? "";
                        if (paramsProp.TryGetProperty("map", out var pMap)) mapName = pMap.GetString() ?? "";
                        if (paramsProp.TryGetProperty("hero", out var pHero)) heroName = pHero.GetString() ?? "";
                    }
                }
                catch (Exception ex)
                {
                    Log($"[NexusMailboxIpcBridge] Ignored payload JSON parse error: {ex.Message}");
                }

                string eventIndexLua = eventIndex.HasValue ? eventIndex.Value.ToString() : "nil";
                string tokenLua = WrapSafeLuaLongBracket(AuthKey ?? string.Empty);
                string payloadLua = WrapSafeLuaLongBracket(jsonString);
                string codeLua = WrapSafeLuaLongBracket(code);
                string commandLua = WrapSafeLuaLongBracket(command);
                string luaContent = $"-- Compatibility shim for legacy mock runners: if Nexus and Nexus.Mailbox and Nexus.Mailbox.ProcessFromJson then Nexus.Mailbox.ProcessFromJson({payloadLua}) end\n" +
                                    $"return {{\n    token = {tokenLua},\n    payload = {payloadLua},\n    id = \"{reqId}\",\n    type = \"{reqType}\",\n    action = \"{action}\",\n    method = \"{method}\",\n    interface = \"{ifaceName}\",\n    widget = \"{widgetName}\",\n    eventIndex = {eventIndexLua},\n    map = \"{mapName}\",\n    hero = \"{heroName}\",\n    code = {codeLua},\n    command = {commandLua}\n}}\n";

                // Always write correlated request file req_{reqId}.lua
                await WriteFileWithRetryAsync(reqCorrelatedLuaFile, luaContent, cancellationToken).ConfigureAwait(false);

                // Only write shared req_current.lua if no concurrent requests are in-flight (avoids clobbering)
                if (inFlight == 1)
                {
                    await WriteFileWithRetryAsync(reqLuaFile, luaContent, cancellationToken).ConfigureAwait(false);
                }

                Log($"[NexusMailboxIpcBridge] Wrote reqCorrelatedLuaFile '{reqCorrelatedLuaFile}' (inFlight: {inFlight})");

                // Dispatch Change-Signal over Native Pipe
                await SendSignalAsync(reqId.ToString(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            // 2. Await response without holding _sendLock across polling loop
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (tcs.Task.IsCompleted)
                {
                    return await tcs.Task.ConfigureAwait(false);
                }

                // Check 1: Response demuxed from console.log
                if (demuxTask.IsCompleted)
                {
                    string fullJson = await demuxTask.ConfigureAwait(false);
                    var parsed = ParseResponse(fullJson);
                    CleanupRequestFiles(reqCorrelatedLuaFile);
                    if (_pendingRequests.TryRemove(reqId, out var regTcs))
                    {
                        regTcs.TrySetResult(parsed);
                    }
                    return parsed;
                }

                // Check 2: File response res_{id}.json (test mocks or custom file runners)
                if (File.Exists(resFile))
                {
                    MailboxResponseRecord? parsed = null;
                    for (int retry = 0; retry < 10; retry++)
                    {
                        try
                        {
                            using var fs = new FileStream(resFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var reader = new StreamReader(fs, Utf8NoBom);
                            string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                parsed = ParseResponse(content);
                                break;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or JsonException)
                        {
                            Debug.WriteLine($"[NexusMailboxIpcBridge] Transient error reading '{resFile}': {ex.Message}");
                        }

                        await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    }

                    if (parsed != null)
                    {
                        try { File.Delete(resFile); } catch (Exception ex) { Log($"[NexusMailboxIpcBridge] Ignored resFile delete error: {ex.Message}"); }
                        CleanupRequestFiles(reqCorrelatedLuaFile);
                        if (_pendingRequests.TryRemove(reqId, out var regTcs))
                        {
                            regTcs.TrySetResult(parsed);
                        }
                        return parsed;
                    }
                }

                await Task.WhenAny(demuxTask, Task.Delay(5, cancellationToken)).ConfigureAwait(false);
            }

            _pendingRequests.TryRemove(reqId, out _);
            CleanupRequestFiles(reqCorrelatedLuaFile);
            throw new TimeoutException($"Mailbox IPC request #{reqId} timed out after {timeoutMs}ms awaiting response from running client.");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightRequestsCount);
            _demuxService.UnregisterPending(reqId.ToString());
            _pendingRequests.TryRemove(reqId, out _);
        }
    }

    private async Task WriteFileWithRetryAsync(string path, string content, CancellationToken cancellationToken)
    {
        byte[] bytes = Utf8NoBom.GetBytes(content);
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException ex) when (attempt < 14)
            {
                Log($"[NexusMailboxIpcBridge] Retry {attempt + 1}/15 writing '{path}' after IOException: {ex.Message}");
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void CleanupRequestFiles(params string?[] paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
            {
                if (p.EndsWith("req_current.lua", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.WriteAllText(p, "return { id = \"idle\", type = \"idle\" }\n", Utf8NoBom); } catch (Exception ex) { Debug.WriteLine($"[NexusMailboxIpcBridge] Ignored req_current.lua idle write error: {ex.Message}"); }
                }
                else
                {
                    try { File.Delete(p); } catch (Exception ex) { Debug.WriteLine($"[NexusMailboxIpcBridge] Ignored request file delete error: {ex.Message}"); }
                }
            }
        }
    }

    private MailboxResponseRecord ParseResponse(string jsonContent)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        int id = 0;
        if (root.TryGetProperty("id", out var idElem))
        {
            if (idElem.ValueKind == JsonValueKind.Number && idElem.TryGetInt32(out int idVal))
            {
                id = idVal;
            }
            else if (idElem.ValueKind == JsonValueKind.String && int.TryParse(idElem.GetString(), out int sVal))
            {
                id = sVal;
            }
        }

        string type = root.TryGetProperty("type", out var typeElem) ? typeElem.GetString() ?? string.Empty : string.Empty;
        bool success = root.TryGetProperty("success", out var sElem) && sElem.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(sElem.GetString(), out bool b) && b,
            _ => false
        };
        string? error = root.TryGetProperty("error", out var eElem) && eElem.ValueKind == JsonValueKind.String ? eElem.GetString() : null;
        long timestamp = 0;
        if (root.TryGetProperty("timestamp", out var tElem))
        {
            if (tElem.ValueKind == JsonValueKind.Number && tElem.TryGetInt64(out long ts))
            {
                timestamp = ts;
            }
            else if (tElem.ValueKind == JsonValueKind.String && long.TryParse(tElem.GetString(), out long sts))
            {
                timestamp = sts;
            }
        }

        JsonElement result = root.TryGetProperty("result", out var rElem) ? rElem.Clone() : default;

        _eventCoordinator.HandleLogStream(jsonContent);
        OnLogStreamReceived?.Invoke(jsonContent);
        OnMessageReceived?.Invoke(IpcMessage.CreateString(IpcOpCode.CommandResponse, jsonContent, (uint)id));

        return new MailboxResponseRecord(id, type, success, result, error, timestamp);
    }

    private static Encoding DetectLogEncoding(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] header = new byte[4];
                int read = fs.Read(header, 0, 4);
                if (read >= 2)
                {
                    if (header[0] == 0xFF && header[1] == 0xFE) return Encoding.Unicode; // UTF-16 LE BOM (K2 Standard)
                    if (header[0] == 0xFE && header[1] == 0xFF) return Encoding.BigEndianUnicode; // UTF-16 BE BOM
                }
                if (read >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
                {
                    return Encoding.UTF8;
                }
                // If second byte is 0x00 and first byte is ASCII, it's UTF-16 LE without BOM
                if (read >= 2 && header[1] == 0x00 && header[0] != 0x00)
                {
                    return Encoding.Unicode;
                }
                // Otherwise it's UTF-8 / ASCII (e.g. unit tests)
                return Utf8NoBom;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NexusMailboxIpcBridge] Ignored DetectLogEncoding error: {ex.Message}");
        }
        return Encoding.Unicode; // Default to Unicode for K2 engine console logs
    }

    private (FileStream? Stream, StreamReader? Reader) OpenLogTailReader(string consoleLogPath)
    {
        if (!File.Exists(consoleLogPath))
        {
            Log($"[NexusMailboxIpcBridge] consoleLogPath not found: '{consoleLogPath}'");
            return (null, null);
        }
        try
        {
            var enc = DetectLogEncoding(consoleLogPath);
            var stream = new FileStream(consoleLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long endPos = stream.Length;
            if (enc == Encoding.Unicode && (endPos % 2) != 0) endPos--;
            long backPos = Math.Max(0, endPos - 512);
            if (enc == Encoding.Unicode && (backPos % 2) != 0) backPos++;
            stream.Seek(backPos, SeekOrigin.Begin);
            var reader = new StreamReader(stream, enc);
            Log($"[NexusMailboxIpcBridge] Opened console.log tail at '{consoleLogPath}', endPos={endPos}, backPos={backPos}, enc={enc.EncodingName}");
            return (stream, reader);
        }
        catch (Exception ex)
        {
            Log($"[NexusMailboxIpcBridge] Exception opening console.log tail: {ex.Message}");
            return (null, null);
        }
    }

    public static string WrapSafeLuaLongBracket(string? rawPayload, int minEquals = 4)
    {
        string safe = rawPayload ?? string.Empty;
        string body = safe.StartsWith('\n') || safe.StartsWith('\r') ? "\n" + safe : safe;
        int eqCount = Math.Max(0, minEquals);
        while (true)
        {
            string eqRun = new('=', eqCount);
            string closeDelim = $"]{eqRun}]";
            string probe = body + closeDelim;
            if (probe.IndexOf(closeDelim, StringComparison.Ordinal) == body.Length)
            {
                return $"[{eqRun}[{body}]{eqRun}]";
            }

            eqCount++;
        }
    }
}
