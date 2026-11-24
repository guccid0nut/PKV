// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Thread-safe event listener and correlated JSON-RPC coordinator for in-engine _G.Nexus operations.
/// Dispatches correlated RPC requests over console execution channels and completes TaskCompletionSources
/// when matching [NEXUS_RPC_RES] responses or [NEXUS_EVENT] notifications stream over IPC.
/// </summary>
public sealed class NexusEventCoordinator
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private int _rpcIdCounter;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<JsonElement>> _pendingRpcs = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<JsonElement>>> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, int, CancellationToken, Task<string>> _commandExecutor;
    private readonly Action<string>? _logger;

    public NexusEventCoordinator(Func<string, int, CancellationToken, Task<string>> commandExecutor, Action<string>? logger = null)
    {
        _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
        _logger = logger;
    }

    /// <summary>
    /// Processes inbound engine console/log stream messages for [NEXUS_RPC_RES] and [NEXUS_EVENT] lines.
    /// </summary>
    public void HandleLogStream(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText)) return;

        // 1. Correlated RPC Response: [NEXUS_RPC_RES] {"id": 1, "method": "...", "success": true, "result": {...}, "error": null}
        const string rpcPrefix = "[NEXUS_RPC_RES]";
        int rpcIdx = logText.IndexOf(rpcPrefix, StringComparison.OrdinalIgnoreCase);
        if (rpcIdx >= 0)
        {
            string payload = logText.Substring(rpcIdx + rpcPrefix.Length).Trim();
            int newlineIdx = payload.IndexOfAny(['\r', '\n']);
            if (newlineIdx >= 0) payload = payload.Substring(0, newlineIdx).Trim();

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idElem) && idElem.TryGetUInt32(out uint reqId))
                {
                    if (_pendingRpcs.TryRemove(reqId, out var tcs))
                    {
                        bool success = root.TryGetProperty("success", out var sElem) && sElem.GetBoolean();
                        if (success)
                        {
                            JsonElement result = root.TryGetProperty("result", out var rElem) ? rElem.Clone() : default;
                            tcs.TrySetResult(result);
                        }
                        else
                        {
                            string errorMsg = root.TryGetProperty("error", out var eElem) ? eElem.GetString() ?? "RPC execution error" : "RPC execution error";
                            tcs.TrySetException(new InvalidOperationException(errorMsg));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[NexusEventCoordinator] Error parsing RPC response: {ex.Message}");
            }
        }

        // 2. Structured In-Engine Event: [NEXUS_EVENT] EVENT_NAME|{...}
        const string eventPrefix = "[NEXUS_EVENT]";
        int eventIdx = logText.IndexOf(eventPrefix, StringComparison.OrdinalIgnoreCase);
        if (eventIdx >= 0)
        {
            string payload = logText.Substring(eventIdx + eventPrefix.Length).Trim();
            int newlineIdx = payload.IndexOfAny(['\r', '\n']);
            if (newlineIdx >= 0) payload = payload.Substring(0, newlineIdx).Trim();

            int pipeIdx = payload.IndexOf('|');
            string eventName;
            string dataStr;
            if (pipeIdx > 0)
            {
                eventName = payload.Substring(0, pipeIdx).Trim();
                dataStr = payload.Substring(pipeIdx + 1).Trim();
            }
            else
            {
                eventName = string.Empty;
                dataStr = payload;
            }

            JsonElement eventData = default;
            try
            {
                if (!string.IsNullOrWhiteSpace(dataStr) && (dataStr.StartsWith('{') || dataStr.StartsWith('[')))
                {
                    using var doc = JsonDocument.Parse(dataStr);
                    if (string.IsNullOrEmpty(eventName) && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("event", out var evProp))
                    {
                        eventName = evProp.GetString() ?? string.Empty;
                    }
                    eventData = doc.RootElement.Clone();
                }
            }
            catch
            {
                // Fall back to default
            }

            if (!string.IsNullOrEmpty(eventName) && _pendingEvents.TryGetValue(eventName, out var queue))
            {
                while (queue.TryDequeue(out var tcs))
                {
                    tcs.TrySetResult(eventData);
                }
            }
        }
    }

    /// <summary>
    /// Asynchronously awaits a specific in-engine event name (e.g. "MATCH_READY", "FRAME_SETTLED") up to timeoutMs.
    /// </summary>
    public async Task<JsonElement> WaitForEventAsync(string eventName, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = _pendingEvents.GetOrAdd(eventName.Trim(), _ => new ConcurrentQueue<TaskCompletionSource<JsonElement>>());
        queue.Enqueue(tcs);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var reg = linkedCts.Token.Register(() =>
        {
            if (timeoutCts.IsCancellationRequested)
                tcs.TrySetException(new TimeoutException($"Timed out after {timeoutMs}ms waiting for in-engine Nexus event '{eventName}'."));
            else
                tcs.TrySetCanceled(linkedCts.Token);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a correlated JSON-RPC request to _G.Nexus.RPC over the engine command executor.
    /// Correlates the response via a monotonic Request ID and deserializes the returned result.
    /// </summary>
    public async Task<T?> ExecuteRpcAsync<T>(string method, object? parameters = null, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        int next = Interlocked.Increment(ref _rpcIdCounter);
        if (next <= 0)
        {
            Interlocked.Exchange(ref _rpcIdCounter, 1);
            next = 1;
        }
        uint reqId = (uint)next;

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRpcs[reqId] = tcs;

        string paramsJson = parameters != null ? JsonSerializer.Serialize(parameters) : "{}";
        string escapedJson = paramsJson.Replace("\\", "\\\\").Replace("'", "\\'");
        string scriptCmd = $"Script Nexus.RPC({reqId}, '{method.Trim()}', '{escapedJson}')";

        try
        {
            await _commandExecutor(scriptCmd, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pendingRpcs.TryRemove(reqId, out _);
            throw new InvalidOperationException($"Failed to dispatch RPC script for method '{method}': {ex.Message}", ex);
        }

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var reg = linkedCts.Token.Register(() =>
        {
            if (timeoutCts.IsCancellationRequested)
                tcs.TrySetException(new TimeoutException($"RPC method '{method}' (ReqId {reqId}) timed out after {timeoutMs}ms awaiting response."));
            else
                tcs.TrySetCanceled(linkedCts.Token);
        });

        try
        {
            JsonElement resultElem = await tcs.Task.ConfigureAwait(false);
            if (typeof(T) == typeof(JsonElement))
            {
                return (T)(object)resultElem;
            }

            if (resultElem.ValueKind == JsonValueKind.Undefined || resultElem.ValueKind == JsonValueKind.Null)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(resultElem.GetRawText(), DefaultJsonOptions);
        }
        finally
        {
            _pendingRpcs.TryRemove(reqId, out _);
        }
    }

    /// <summary>
    /// Resets all active pending RPCs and events on disconnection or bridge teardown.
    /// </summary>
    public void Reset()
    {
        foreach (var kvp in _pendingRpcs)
        {
            if (_pendingRpcs.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(new IOException("IPC pipe connection terminated while awaiting RPC response."));
            }
        }
        foreach (var kvp in _pendingEvents)
        {
            while (kvp.Value.TryDequeue(out var tcs))
            {
                tcs.TrySetException(new IOException("IPC pipe connection terminated while awaiting in-engine event."));
            }
        }
    }
}
