// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Kongor.Cli.Mcp.Logging;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Protocol;

/// <summary>
/// Core Model Context Protocol (MCP) JSON-RPC 2.0 Server over stdio transport.
/// Supports bidirectional communication including reverse-RPC sampling (sampling/createMessage).
/// </summary>
public sealed partial class McpServer : IMcpSamplingService, IDisposable
{
    private static readonly ActivitySource s_activitySource = new("Kongor.Mcp", McpProtocolVersion.ServerVersion);
    private static readonly Regex ServerPortRegex = new(@"^kongor://servers/(\d+)/logs$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DedicatedServerPortRegex = new(@"^kongor://servers/(\d+)/logs$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReplaySummaryRegex = new(@"^kongor://replays/([^/]+)/summary$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> KnownStaticResourceUris = new(StringComparer.OrdinalIgnoreCase)
    {
        "kongor://logs/master-server",
        "kongor://logs/chat-server",
        "kongor://client/console",
        "kongor://client/telemetry",
        "kongor://client/errors",
        "kongor://engine/vfs-manifest",
        "kongor://cluster/metrics",
        "kongor://replays/latest/summary",
        "kongor://servers/{port}/logs",
        "kongor://replays/{matchId}/summary",
        "kongor://servers/registry",
        "kongor://about"
    };

    static McpServer()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Kongor.Mcp",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        });
    }

    private readonly McpToolRegistry _toolRegistry;
    private readonly TextWriter _errorWriter;
    private readonly TimeProvider _timeProvider;
    private readonly IMcpConversationLogger? _conversationLogger;
    private bool _isInitialized;
    private string _currentLogLevel = "info";
    private TextWriter? _activeOutput;
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private ClientCapabilities? _clientCapabilities;
    private string _negotiatedProtocolVersion = McpProtocolVersion.Current;
    private readonly ConcurrentQueue<string> _sentNotifications = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pendingClientRequests = new();
    private readonly ConcurrentQueue<string> _sentRequests = new();
    private readonly ConcurrentDictionary<string, byte> _activeSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IDisposable> _logWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _watcherLock = new();
    private long _nextRequestId = 1000;
    private long _lastIpcConsoleNotifyTicks;
    private long _lastIpcTelemetryNotifyTicks;
    private readonly ConcurrentDictionary<string, long> _lastWatcherNotifyTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _diagnosticChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
    private readonly CancellationTokenSource _diagnosticCts = new();
    private readonly Task _diagnosticDrainTask;

    public McpToolRegistry Registry => _toolRegistry;
    public bool IsInitialized => _isInitialized;
    public string CurrentLogLevel => _currentLogLevel;
    public ClientCapabilities? ClientCapabilities => _clientCapabilities;
    public bool IsSamplingSupported => _clientCapabilities?.Sampling != null;
    public IReadOnlyList<string> SentNotifications => _sentNotifications.ToArray();
    public void ClearSentNotifications() => _sentNotifications.Clear();
    public IReadOnlyList<string> SentRequests => _sentRequests.ToArray();
    public void ClearSentRequests() => _sentRequests.Clear();
    public IReadOnlyCollection<string> Subscriptions => _activeSubscriptions.Keys.ToArray();
    public bool IsSubscribed(string uri) => _activeSubscriptions.ContainsKey(uri);
    public int InFlightRequestCount => _inFlightRequests.Count;
    private readonly bool _readOnlyMode;
    public bool IsReadOnlyMode => _readOnlyMode;
    internal IMcpConversationLogger? ConversationLogger => _conversationLogger;

    public bool CancelInFlightRequest(string requestId)
    {
        if (_inFlightRequests.TryGetValue(requestId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public McpServer(
        McpToolRegistry? toolRegistry = null,
        TextWriter? errorWriter = null,
        TimeProvider? timeProvider = null,
        IMcpConversationLogger? conversationLogger = null,
        bool readOnlyMode = false)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _toolRegistry = toolRegistry ?? new McpToolRegistry(registerDefaults: true, _timeProvider);
        _errorWriter = errorWriter ?? Console.Error;
        _conversationLogger = conversationLogger;
        _readOnlyMode = readOnlyMode || Environment.GetEnvironmentVariable("KONGOR_MCP_READ_ONLY") == "1";
        _diagnosticDrainTask = Task.Run(() => DrainDiagnosticsLoopAsync(_diagnosticCts.Token), CancellationToken.None);
        _toolRegistry.ToolListChanged += OnToolListChanged;
        _toolRegistry.ClientCommandRemediationService.ErrorRecorded += OnClientCommandErrorRecorded;
        if (_toolRegistry.ServerPortRegistry != null)
        {
            _toolRegistry.ServerPortRegistry.OnRegistryChanged += OnServerPortRegistryChanged;
        }
        if (_toolRegistry.GameIpcBridge != null)
        {
            _toolRegistry.GameIpcBridge.OnLogStreamReceived += OnIpcLogStreamReceived;
            _toolRegistry.GameIpcBridge.OnMessageReceived += OnIpcMessageReceived;
        }
    }

    private void OnServerPortRegistryChanged(IReadOnlyList<ServerWorkerRegistration> workers)
    {
        if (_activeSubscriptions.ContainsKey("kongor://servers/registry"))
        {
            _ = NotifyResourceUpdatedAsync("kongor://servers/registry");
        }
    }

    private void OnClientCommandErrorRecorded(ClientCommandErrorRecord record)
    {
        if (_activeSubscriptions.ContainsKey("kongor://client/errors"))
        {
            _ = NotifyResourceUpdatedAsync("kongor://client/errors");
        }
    }

    private void OnIpcLogStreamReceived(string logLine)
    {
        if (_activeSubscriptions.ContainsKey("kongor://client/console"))
        {
            long now = Environment.TickCount64;
            if (now - _lastIpcConsoleNotifyTicks >= 50)
            {
                _lastIpcConsoleNotifyTicks = now;
                _ = NotifyResourceUpdatedAsync("kongor://client/console");
            }
        }
    }

    private void OnIpcMessageReceived(IpcMessage msg)
    {
        if (_activeSubscriptions.ContainsKey("kongor://client/telemetry"))
        {
            long now = Environment.TickCount64;
            if (now - _lastIpcTelemetryNotifyTicks >= 50)
            {
                _lastIpcTelemetryNotifyTicks = now;
                _ = NotifyResourceUpdatedAsync("kongor://client/telemetry");
            }
        }
    }

    public void Dispose()
    {
        _toolRegistry.ToolListChanged -= OnToolListChanged;
        _toolRegistry.ClientCommandRemediationService.ErrorRecorded -= OnClientCommandErrorRecorded;
        if (_toolRegistry.ServerPortRegistry != null)
        {
            _toolRegistry.ServerPortRegistry.OnRegistryChanged -= OnServerPortRegistryChanged;
        }
        if (_toolRegistry.GameIpcBridge != null)
        {
            _toolRegistry.GameIpcBridge.OnLogStreamReceived -= OnIpcLogStreamReceived;
            _toolRegistry.GameIpcBridge.OnMessageReceived -= OnIpcMessageReceived;
        }
        try
        {
            _toolRegistry.PerfWindowHandler.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpServer] PerfWindowHandler dispose warning: {ex.Message}");
        }
        lock (_watcherLock)
        {
            foreach (var kvp in _logWatchers)
            {
                try
                {
                    if (kvp.Value is FileSystemWatcher fsw)
                    {
                        fsw.EnableRaisingEvents = false;
                    }
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _logWatchers.Clear();
        }
        try
        {
            _outputLock.Dispose();
        }
        catch { }
        try
        {
            _diagnosticCts.Cancel();
            _diagnosticCts.Dispose();
        }
        catch { }
    }

    public void EnsureOutcomeLogInitialized()
    {
        string? customTelemetryDir = Environment.GetEnvironmentVariable("KONGOR_MCP_TELEMETRY_DIR");
        if (string.IsNullOrWhiteSpace(_toolRegistry.OutcomeLogFilePath) &&
            Environment.GetEnvironmentVariable("KONGOR_MCP_OUTCOME_LOG_DISABLED") != "1")
        {
            if (Handlers.GameServerManageHandler.IsTestExecution && string.IsNullOrWhiteSpace(customTelemetryDir))
            {
                string testTelemetryDir = Path.Combine(Path.GetTempPath(), "ProjectKongor", "mcp-test-telemetry", "logs");
                Directory.CreateDirectory(testTelemetryDir);
                _toolRegistry.OutcomeLogFilePath = Path.Combine(testTelemetryDir, "mcp_tool_outcomes.jsonl");
            }
            else
            {
                string logDir = !string.IsNullOrWhiteSpace(customTelemetryDir)
                    ? customTelemetryDir
                    : (Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out var bundledLayout)
                        ? bundledLayout.Logs
                        : Path.Combine(FindProjectRoot(), "logs"));
                _toolRegistry.OutcomeLogFilePath = Path.Combine(logDir, "mcp_tool_outcomes.jsonl");
            }
        }

        if (!string.IsNullOrWhiteSpace(_toolRegistry.OutcomeLogFilePath))
        {
            LogDiagnostic($"[MCP Server] Outcome telemetry log initialized: {_toolRegistry.OutcomeLogFilePath}");
        }
    }

    /// <summary>
    /// Run the MCP server over standard input and standard output streams.
    /// </summary>
    public Task RunStdioAsync(CancellationToken cancellationToken = default)
    {
        EnsureOutcomeLogInitialized();
        return RunAsync(Console.In, Console.Out, cancellationToken);
    }

    /// <summary>
    /// Run the MCP server message pump using provided input reader and output writer.
    /// Strictly guarantees stdout is used only for JSON-RPC messages and stderr for diagnostics.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        LogDiagnostic($"[MCP Server] Starting ProjectKongor MCP JSON-RPC Server v{McpProtocolVersion.ServerVersion} (Protocol: {McpProtocolVersion.Current})...");
        _activeOutput = output;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"[MCP Server] Read error: {ex.Message}");
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (line is null)
                {
                    // EOF reached on input stream (stream closed or client disconnected)
                    break;
                }

                line = line.TrimStart('\uFEFF').Trim();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                LogDiagnostic($"[MCP Server] Received raw message: {TruncateLog(line)}");

                // Inspect message structure to enable non-blocking concurrent request execution
                bool isRequest = false;
                string? requestIdStr = null;
                JsonRpcId? requestId = null;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("id", out JsonElement idProp) &&
                        doc.RootElement.TryGetProperty("method", out _))
                    {
                        isRequest = true;
                        requestIdStr = idProp.ValueKind == JsonValueKind.Number
                            ? idProp.GetInt64().ToString()
                            : idProp.GetString() ?? string.Empty;
                        requestId = ParseJsonRpcId(idProp);
                    }
                }
                catch
                {
                    // Fall back to inline processing for malformed JSON so standard ParseError response is generated
                    isRequest = false;
                }

                if (isRequest && !string.IsNullOrEmpty(requestIdStr))
                {
                    var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _inFlightRequests[requestIdStr] = reqCts;

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            string? responseJson = await ProcessMessageAsync(line, reqCts.Token).ConfigureAwait(false);
                            if (responseJson != null)
                            {
                                LogDiagnostic($"[MCP Server] Sending response: {TruncateLog(responseJson)}");
                                await SendRawMessageAsync(responseJson, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) when (reqCts.IsCancellationRequested)
                        {
                            LogDiagnostic($"[MCP Server] Request '{requestIdStr}' was cancelled by client.");
                            var cancelResponse = JsonRpcResponse.CreateError(
                                requestId,
                                -32800,
                                "Request was cancelled."
                            );
                            string cancelJson = JsonRpcSerializer.Serialize(cancelResponse);
                            await SendRawMessageAsync(cancelJson, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogDiagnostic($"[MCP Server] Unhandled error processing request '{requestIdStr}': {ex.Message}");
                            var errorResponse = JsonRpcResponse.CreateError(
                                requestId,
                                JsonRpcErrorCode.InternalError,
                                $"Internal error: {ex.Message}"
                            );
                            string errJson = JsonRpcSerializer.Serialize(errorResponse);
                            await SendRawMessageAsync(errJson, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            _inFlightRequests.TryRemove(requestIdStr, out _);
                            _inFlightTasks.TryRemove(requestIdStr, out _);
                            reqCts.Dispose();
                        }
                    }, cancellationToken);

                    _inFlightTasks[requestIdStr] = task;
                }
                else
                {
                    // Notifications, server responses, and malformed requests execute inline (<1ms) without blocking
                    string? responseJson = await ProcessMessageAsync(line, cancellationToken).ConfigureAwait(false);

                    if (responseJson != null)
                    {
                        LogDiagnostic($"[MCP Server] Sending response: {TruncateLog(responseJson)}");
                        await SendRawMessageAsync(responseJson, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!_inFlightTasks.IsEmpty)
            {
                LogDiagnostic($"[MCP Server] Draining {_inFlightTasks.Count} in-flight tasks...");
                try
                {
                    await Task.WhenAll(_inFlightTasks.Values).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }
        finally
        {
            _activeOutput = null;
        }

        if (_conversationLogger != null)
        {
            try { await _conversationLogger.FlushAsync().ConfigureAwait(false); } catch { }
        }

        LogDiagnostic("[MCP Server] MCP Server stopped.");
    }

    private static JsonRpcId? ParseJsonRpcId(JsonElement idProp)
    {
        if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out long l))
            return new JsonRpcId(l);
        if (idProp.ValueKind == JsonValueKind.String)
            return new JsonRpcId(idProp.GetString()!);
        return null;
    }

    /// <summary>
    /// Thread-safe writer for standard output stream preventing interleaved JSON-RPC frames.
    /// </summary>
    public async Task SendRawMessageAsync(string json, CancellationToken cancellationToken = default)
    {
        if (_activeOutput == null) return;
        await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _activeOutput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _activeOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    internal event Func<string, object?, CancellationToken, Task>? ExternalNotificationDispatcher;

    private void OnToolListChanged()
    {
        LogDiagnostic("[MCP Server] Tool list changed; dispatching 'notifications/tools/list_changed'.");
        _ = SendNotificationAsync("notifications/tools/list_changed", null);
    }

    /// <summary>
    /// Sends a JSON-RPC 2.0 notification to the client.
    /// </summary>
    public async Task SendNotificationAsync(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        object notification = @params != null
            ? new { jsonrpc = "2.0", method, @params }
            : new { jsonrpc = "2.0", method };
        string json = JsonRpcSerializer.Serialize(notification);
        _sentNotifications.Enqueue(json);
        while (_sentNotifications.Count > 1000 && _sentNotifications.TryDequeue(out _)) { }
        LogDiagnostic($"[MCP Server] Sending notification '{method}': {TruncateLog(json)}");
        await SendRawMessageAsync(json, cancellationToken).ConfigureAwait(false);
        if (ExternalNotificationDispatcher != null)
        {
            try
            {
                await ExternalNotificationDispatcher(method, @params, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[MCP Server] External notification dispatch failed for '{method}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sends an MCP progress notification ($/progress or notifications/progress) to the client.
    /// </summary>
    public Task SendProgressNotificationAsync(object progressToken, double progress, double? total = null, string? message = null, CancellationToken cancellationToken = default)
    {
        var @params = new ProgressNotificationParams
        {
            ProgressToken = progressToken,
            Progress = progress,
            Total = total,
            Message = message
        };
        return SendNotificationAsync("notifications/progress", @params, cancellationToken);
    }

    /// <summary>
    /// Processes a single line message and returns the serialized JSON-RPC response or null if notification.
    /// </summary>
    public async Task<string?> ProcessMessageAsync(string line, CancellationToken cancellationToken = default)
    {
        line = line.TrimStart('\uFEFF').Trim();
        int firstBrace = line.IndexOf('{');
        int lastBrace = line.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            line = line.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        JsonRpcRequest? request;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                var parseErrorResponse = JsonRpcResponse.CreateError(
                    null,
                    JsonRpcErrorCode.InvalidRequest,
                    "Invalid Request: Root payload must be a JSON object."
                );
                return JsonRpcSerializer.Serialize(parseErrorResponse);
            }

            if (!doc.RootElement.TryGetProperty("jsonrpc", out JsonElement jsonRpcProp) ||
                jsonRpcProp.ValueKind != JsonValueKind.String ||
                jsonRpcProp.GetString() != "2.0")
            {
                JsonRpcId? id = null;
                if (doc.RootElement.TryGetProperty("id", out JsonElement idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out long l))
                        id = new JsonRpcId(l);
                    else if (idProp.ValueKind == JsonValueKind.String)
                        id = new JsonRpcId(idProp.GetString()!);
                }

                var invalidRequestResponse = JsonRpcResponse.CreateError(
                    id,
                    JsonRpcErrorCode.InvalidRequest,
                    "Invalid Request: Message must specify 'jsonrpc': '2.0'."
                );
                return JsonRpcSerializer.Serialize(invalidRequestResponse);
            }

            // Check if this is a response to a server-initiated request (has 'id', and ('result' or 'error'), without 'method')
            if (doc.RootElement.TryGetProperty("id", out JsonElement respIdProp) &&
                !doc.RootElement.TryGetProperty("method", out _) &&
                (doc.RootElement.TryGetProperty("result", out _) || doc.RootElement.TryGetProperty("error", out _)))
            {
                string respId = respIdProp.ValueKind == JsonValueKind.Number
                    ? respIdProp.GetInt64().ToString()
                    : respIdProp.GetString() ?? string.Empty;

                LogDiagnostic($"[MCP Server] Received response for server request id '{respId}'");

                if (_pendingClientRequests.TryRemove(respId, out var pendingTcs))
                {
                    var rpcResponse = JsonRpcSerializer.Deserialize<JsonRpcResponse>(line);
                    if (rpcResponse != null)
                    {
                        pendingTcs.TrySetResult(rpcResponse);
                    }
                    else
                    {
                        pendingTcs.TrySetException(new InvalidOperationException($"Failed to deserialize client response for request {respId}"));
                    }
                }
                else
                {
                    LogDiagnostic($"[MCP Server] No pending request found matching response id '{respId}' (possibly timed out or cancelled).");
                }

                return null;
            }

            request = JsonRpcSerializer.Deserialize<JsonRpcRequest>(line);
            if (request != null)
            {
                request.RawJson = line;
            }
        }
        catch (JsonException ex)
        {
            LogDiagnostic($"[MCP Server] JSON parse error: {ex.Message}");
            var parseErrorResponse = JsonRpcResponse.CreateError(
                null,
                JsonRpcErrorCode.ParseError,
                $"Parse error: {ex.Message}"
            );
            return JsonRpcSerializer.Serialize(parseErrorResponse);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
        {
            var invalidRequestResponse = JsonRpcResponse.CreateError(
                request?.Id,
                JsonRpcErrorCode.InvalidRequest,
                "Invalid Request: Message must specify 'jsonrpc': '2.0' and contain a valid 'method' property."
            );
            return JsonRpcSerializer.Serialize(invalidRequestResponse);
        }

        Stopwatch sw = Stopwatch.StartNew();

        string? toolName = null;
        JsonElement? toolArgs = null;

        if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
        {
            if (request.Params.Value.TryGetProperty("name", out JsonElement tnEl) && tnEl.ValueKind == JsonValueKind.String)
                toolName = tnEl.GetString();
            if (request.Params.Value.TryGetProperty("arguments", out JsonElement argsEl))
                toolArgs = argsEl;
        }

        string? traceParent = ExtractTraceparent(request, line);
        string? traceState = ExtractTracestate(request, line);

        Activity? activity = null;
        if (request.Method == "tools/call" || traceParent != null)
        {
            string actName = request.Method == "tools/call"
                ? (!string.IsNullOrEmpty(toolName) ? $"MCP {toolName}" : "MCP tools/call")
                : $"MCP {request.Method}";

            if (traceParent != null && ActivityContext.TryParse(traceParent, traceState, out ActivityContext parentContext))
            {
                activity = s_activitySource.StartActivity(actName, ActivityKind.Server, parentContext);
            }
            else
            {
                if (!string.IsNullOrEmpty(traceParent))
                {
                    LogDiagnostic($"[MCP Server] Malformed W3C traceparent '{traceParent}'. Starting fresh W3C root trace per W3C specification.");
                }
                Activity.Current = null;
                activity = s_activitySource.StartActivity(actName, ActivityKind.Server);
            }

            if (activity != null)
            {
                if (!string.IsNullOrEmpty(toolName)) activity.SetTag("mcp.tool", toolName);
                activity.SetTag("mcp.id", request.Id?.ToString());
            }
        }

        string? actTraceId = activity != null && activity.TraceId != default ? activity.TraceId.ToHexString() : null;
        string? actSpanId = activity != null && activity.SpanId != default ? activity.SpanId.ToHexString() : null;

        try
        {
            _conversationLogger?.LogInbound(line, request.Method, request.Id?.ToString(), toolName, toolArgs ?? request.Params, actTraceId, actSpanId);

            // Notification: client sent a message without 'id'
            if (request.IsNotification)
            {
                await HandleNotificationAsync(request, cancellationToken).ConfigureAwait(false);
                return null;
            }

            // Request: must generate response matching request ID
            JsonRpcResponse response;
            try
            {
                response = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LogDiagnostic($"[MCP Server] Request {request.Id} was cancelled by caller/client.");
                response = JsonRpcResponse.CreateError(request.Id, -32800, "Request was cancelled.");
            }
            string responseJson;
            try
            {
                responseJson = JsonRpcSerializer.Serialize(response);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[MCP Server] [ERROR] Failed serializing JSON-RPC response for request {request.Id}: {ex.Message}");
                response = JsonRpcResponse.CreateError(request.Id, -32603, $"Response payload exceeds serialization limits: {ex.Message}");
                responseJson = JsonRpcSerializer.Serialize(response);
            }

            _conversationLogger?.LogOutbound(responseJson, request.Id?.ToString(), response.Error != null, sw.Elapsed.TotalMilliseconds, actTraceId, actSpanId);
            return responseJson;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private async Task HandleNotificationAsync(JsonRpcRequest notification, CancellationToken cancellationToken)
    {
        LogDiagnostic($"[MCP Server] Handling notification '{notification.Method}'");

        switch (notification.Method)
        {
            case "notifications/initialized":
                _isInitialized = true;
                LogDiagnostic("[MCP Server] Client acknowledged initialization ('notifications/initialized'). Ready for tool execution.");
                break;

            case "notifications/cancelled":
            case "$/cancelRequest":
                HandleCancellationNotification(notification);
                break;

            default:
                LogDiagnostic($"[MCP Server] Ignored unhandled notification '{notification.Method}'");
                break;
        }

        await Task.CompletedTask;
    }

    private void HandleCancellationNotification(JsonRpcRequest notification)
    {
        string? targetId = null;
        string? reason = null;

        if (notification.Params.HasValue && notification.Params.Value.ValueKind == JsonValueKind.Object)
        {
            var p = notification.Params.Value;
            if (p.TryGetProperty("requestId", out JsonElement reqIdEl))
            {
                targetId = reqIdEl.ValueKind == JsonValueKind.Number ? reqIdEl.GetInt64().ToString() : reqIdEl.GetString();
            }
            else if (p.TryGetProperty("id", out JsonElement idEl))
            {
                targetId = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString();
            }

            if (p.TryGetProperty("reason", out JsonElement reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            {
                reason = reasonEl.GetString();
            }
        }

        if (!string.IsNullOrEmpty(targetId))
        {
            if (_inFlightRequests.TryGetValue(targetId, out var cts))
            {
                LogDiagnostic($"[MCP Server] Cancelling in-flight request '{targetId}' (Reason: {reason ?? "none given"}).");
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"[MCP Server] Error requesting cancellation for request '{targetId}': {ex.Message}");
                }
            }
            else
            {
                LogDiagnostic($"[MCP Server] Cancellation received for request '{targetId}', but it is not currently in flight.");
            }
        }
        else
        {
            LogDiagnostic("[MCP Server] Received cancellation notification without target 'requestId' or 'id'.");
        }
    }

    private async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        LogDiagnostic($"[MCP Server] Dispatching method '{request.Method}' (id: {request.Id})");

        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "server/discover" => HandleServerDiscover(request),
                "ping" => HandlePing(request),
                "tools/list" => HandleToolsList(request),
                "tools/call" => await HandleToolsCallAsync(request, cancellationToken).ConfigureAwait(false),
                "resources/list" => HandleResourcesList(request),
                "resources/templates/list" => HandleResourceTemplatesList(request),
                "resources/read" => await HandleResourcesReadAsync(request, cancellationToken).ConfigureAwait(false),
                "resources/subscribe" => HandleResourcesSubscribe(request),
                "resources/unsubscribe" => HandleResourcesUnsubscribe(request),
                "prompts/list" => HandlePromptsList(request),
                "prompts/get" => HandlePromptsGet(request),
                "logging/setLevel" => HandleSetLogLevel(request),
                _ => JsonRpcResponse.CreateError(
                    request.Id,
                    JsonRpcErrorCode.MethodNotFound,
                    $"Method '{request.Method}' not found on this server.")
            };
        }
        catch (JsonException ex)
        {
            LogDiagnostic($"[MCP Server] Invalid params for '{request.Method}': {ex.Message}");
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                $"Invalid params for method '{request.Method}': {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            LogDiagnostic($"[MCP Server] Invalid params for '{request.Method}': {ex.Message}");
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                $"Invalid params for method '{request.Method}': {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogDiagnostic($"[MCP Server] Request '{request.Id}' was cancelled by client.");
            return JsonRpcResponse.CreateError(
                request.Id,
                -32800, // RequestCancelled per LSP/MCP specification
                "Request was cancelled.");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[MCP Server] Internal error processing '{request.Method}': {ex}");
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InternalError,
                $"Internal error processing method '{request.Method}': {ex.Message}");
        }
    }

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        InitializeParams? @params = null;
        if (request.Params.HasValue)
        {
            @params = JsonRpcSerializer.Deserialize<InitializeParams>(request.Params.Value);
        }

        _clientCapabilities = @params?.Capabilities;

        string clientName = @params?.ClientInfo?.Name ?? "unknown";
        string clientVersion = @params?.ClientInfo?.Version ?? "unknown";
        string negotiatedProtocol = McpProtocolVersion.Negotiate(@params?.ProtocolVersion);
        _negotiatedProtocolVersion = negotiatedProtocol;
        LogDiagnostic($"[MCP Server] Initialize received from client '{clientName}' v{clientVersion}. Requested protocol: {@params?.ProtocolVersion ?? "(default)"}, negotiated: {negotiatedProtocol}");

        var result = new InitializeResult
        {
            ProtocolVersion = negotiatedProtocol,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Logging = new LoggingCapability(),
                Resources = new ResourceCapability { ListChanged = true, Subscribe = true },
                Prompts = new PromptsCapability { ListChanged = true }
            },
            ServerInfo = new ImplementationInfo
            {
                Name = McpProtocolVersion.ServerName,
                Version = McpProtocolVersion.ServerVersion,
                Description = McpProtocolVersion.ServerDescription,
                Author = McpProtocolVersion.ServerAuthor,
                License = McpProtocolVersion.ServerLicense,
                RepositoryUrl = McpProtocolVersion.ServerRepositoryUrl
            },
            Instructions = McpProtocolVersion.ServerInstructions
        };

        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse HandleServerDiscover(JsonRpcRequest request)
    {
        LogDiagnostic("[MCP Server] Responded to 'server/discover' handshake.");
        var result = new
        {
            protocolVersion = McpProtocolVersion.Current,
            capabilities = new
            {
                tools = new { listChanged = true },
                logging = new { },
                resources = new { listChanged = true, subscribe = true },
                prompts = new { listChanged = true }
            },
            serverInfo = new
            {
                name = McpProtocolVersion.ServerName,
                version = McpProtocolVersion.ServerVersion,
                description = McpProtocolVersion.ServerDescription,
                author = McpProtocolVersion.ServerAuthor,
                license = McpProtocolVersion.ServerLicense,
                repositoryUrl = McpProtocolVersion.ServerRepositoryUrl
            }
        };
        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse HandleResourceTemplatesList(JsonRpcRequest request)
    {
        var result = new ResourceTemplateListResult
        {
            ResourceTemplates =
            [
                new ResourceTemplate
                {
                    UriTemplate = "kongor://servers/{port}/logs",
                    Name = "Dedicated Server Logs by Port",
                    Description = "Live tail of engine console logs for a dedicated game server worker listening on the specified port",
                    MimeType = "text/plain"
                },
                new ResourceTemplate
                {
                    UriTemplate = "kongor://replays/{matchId}/summary",
                    Name = "Replay Summary by Match ID",
                    Description = "Parsed telemetry summary and match outcome for recorded match replay",
                    MimeType = "application/json"
                }
            ]
        };

        LogDiagnostic($"[MCP Server] resources/templates/list returning {result.ResourceTemplates.Count} templates.");
        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse HandleResourcesList(JsonRpcRequest request)
    {
        var result = new ResourceListResult
        {
            Resources =
            [
                new McpResource
                {
                    Uri = "kongor://about",
                    Name = "TAO MCP Server Authorship, License & Citation",
                    Description = "Authorship, AGPL-3.0-or-later license, and citation metadata for TAO (Together As One) MCP Server created by guccidonut for Project KONGOR V",
                    MimeType = "application/json"
                },
                new McpResource
                {
                    Uri = "kongor://logs/master-server",
                    Name = "KONGOR Master Server Logs",
                    Description = "Live log stream and recent outputs from KONGOR Master Server",
                    MimeType = "text/plain"
                },
                new McpResource
                {
                    Uri = "kongor://logs/chat-server",
                    Name = "TRANSMUTANSTEIN Chat Server Logs",
                    Description = "Live log stream and connection events from TRANSMUTANSTEIN Chat Server",
                    MimeType = "text/plain"
                },
                new McpResource
                {
                    Uri = "kongor://client/console",
                    Name = "Player Game Client Console",
                    Description = "Engine console log tail and execution diagnostics for running hon_x64.exe",
                    MimeType = "text/plain"
                },
                new McpResource
                {
                    Uri = "kongor://client/telemetry",
                    Name = "Player Game Client Telemetry & Match State",
                    Description = "Live real-time in-game hero, match state, game phase, and engine telemetry",
                    MimeType = "application/json"
                },
                new McpResource
                {
                    Uri = "kongor://engine/vfs-manifest",
                    Name = "K2 VFS Mount & Override Manifest",
                    Description = "Active Virtual File System search order, mounted packages, and override shadow status",
                    MimeType = "application/json"
                },
                new McpResource
                {
                    Uri = "kongor://cluster/metrics",
                    Name = "KONGOR Cluster & Host Metrics",
                    Description = "Telemetry snapshot of CPU usage, process memory, active service ports, and host health",
                    MimeType = "application/json"
                },
                new McpResource
                {
                    Uri = "kongor://client/errors",
                    Name = "Recent Client Command Errors & Remediation",
                    Description = "Recent client command failures and self-healing recommendations from ClientCommandRemediationService",
                    MimeType = "application/json"
                },
                new McpResource
                {
                    Uri = "kongor://replays/latest/summary",
                    Name = "Latest Replay Telemetry Summary",
                    Description = "Parsed match summary, team compositions, and turning points from the most recent .honreplay file",
                    MimeType = "application/json"
                },
                new McpResource
                {
                    Uri = "kongor://servers/{port}/logs",
                    Name = "Dedicated Server Console Log Tail",
                    Description = "Dynamic resource template for dedicated server logs by listen port",
                    MimeType = "text/plain"
                },
                new McpResource
                {
                    Uri = "kongor://servers/registry",
                    Name = "Dedicated Server Port Registry",
                    Description = "Live listing of active dedicated game servers, listen ports, process IDs, and match assignments",
                    MimeType = "application/json"
                }
            ]
        };

        LogDiagnostic($"[MCP Server] resources/list returning {result.Resources.Count} resources.");
        return JsonRpcResponse.Success(request.Id, result);
    }

    private async Task<JsonRpcResponse> HandleResourcesReadAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!request.Params.HasValue ||
            request.Params.Value.ValueKind != JsonValueKind.Object ||
            !request.Params.Value.TryGetProperty("uri", out JsonElement uriEl) ||
            uriEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(uriEl.GetString()))
        {
            return JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCode.InvalidParams, "Missing or invalid required 'uri' parameter for 'resources/read'.");
        }

        string uri = uriEl.GetString()!.Trim();
        LogDiagnostic($"[MCP Server] resources/read for URI: '{uri}'");

        string textContent;
        string mimeType = "text/plain";

        try
        {
            if (uri.Equals("kongor://engine/vfs-manifest", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                var status = VfsDiagnostics.GetVfsStatus();
                textContent = JsonRpcSerializer.SerializeIndented(status);
            }
            else if (uri.Equals("kongor://logs/master-server", StringComparison.OrdinalIgnoreCase))
            {
                textContent = ReadLatestLogTail("src/services/master-server/logs", 100);
            }
            else if (uri.Equals("kongor://logs/chat-server", StringComparison.OrdinalIgnoreCase))
            {
                textContent = ReadLatestLogTail("src/services/chat-server/logs", 100);
            }
            else if (uri.Equals("kongor://client/console", StringComparison.OrdinalIgnoreCase))
            {
                textContent = ReadLatestLogTail("userdata/logs", 100);
            }
            else if (uri.Equals("kongor://client/telemetry", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                textContent = await ReadClientTelemetryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (uri.Equals("kongor://cluster/metrics", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                textContent = ReadClusterMetrics();
            }
            else if (uri.Equals("kongor://client/errors", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                textContent = ReadClientErrors();
            }
            else if (uri.Equals("kongor://replays/latest/summary", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                textContent = ReadLatestReplaySummary();
            }
            else if (uri.Equals("kongor://replays/{matchId}/summary", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                textContent = ReadLatestReplaySummary();
            }
            else if (uri.Equals("kongor://servers/{port}/logs", StringComparison.OrdinalIgnoreCase))
            {
                textContent = ReadDedicatedServerLogTail("11235", 100);
            }
            else if (ReplaySummaryRegex.Match(uri) is { Success: true } replayMatch)
            {
                mimeType = "application/json";
                string matchId = replayMatch.Groups[1].Value;
                textContent = ReadReplaySummary(matchId);
            }
            else if (ServerPortRegex.Match(uri) is { Success: true } serverMatch)
            {
                string portStr = serverMatch.Groups[1].Value;
                textContent = ReadDedicatedServerLogTail(portStr, 100);
            }
            else if (uri.Equals("kongor://about", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                textContent = JsonRpcSerializer.SerializeIndented(new
                {
                    name = McpProtocolVersion.ServerName,
                    title = McpProtocolVersion.ServerTitle,
                    version = McpProtocolVersion.ServerVersion,
                    author = McpProtocolVersion.ServerAuthor,
                    project = "Project KONGOR V (PKV)",
                    license = McpProtocolVersion.ServerLicense,
                    correspondingSourceUrl = McpProtocolVersion.ServerRepositoryUrl,
                    citationFile = "CITATION.cff",
                    noticeRequirement = "Pursuant to AGPL-3.0 Section 7(b), author attribution to guccidonut and Project KONGOR V in NOTICE and source distributions must be preserved."
                });
            }
            else if (uri.Equals("kongor://servers/registry", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = "application/json";
                var workers = _toolRegistry.ServerPortRegistry.GetActiveWorkers(pruneStale: true);
                textContent = JsonRpcSerializer.SerializeIndented(new
                {
                    status = "success",
                    totalServers = workers.Count,
                    servers = workers,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            else
            {
                return JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCode.InvalidParams, $"Unknown or unsupported resource URI: '{uri}'.");
            }
        }
        catch (Exception ex)
        {
            textContent = $"[Resource Read Error] {ex.Message}";
        }

        var result = new ReadResourceResult
        {
            Contents =
            [
                new ResourceContents
                {
                    Uri = uri,
                    MimeType = mimeType,
                    Text = textContent
                }
            ]
        };

        return await Task.FromResult(JsonRpcResponse.Success(request.Id, result)).ConfigureAwait(false);
    }

    internal async Task<string> ReadClientTelemetryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EngineStateDto? engineState = null;
            if (_toolRegistry.GameIpcBridge != null)
            {
                try
                {
                    engineState = await _toolRegistry.GameIpcBridge.GetEngineStateAsync(2000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"[ReadClientTelemetryAsync] IPC state probe unavailable: {ex.Message}");
                }
            }

            if (engineState == null)
            {
                var offlineData = new
                {
                    status = "offline",
                    clientRunning = false,
                    hero = (object?)null,
                    telemetry = (object?)null,
                    matchState = (object?)null,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                };
                return JsonRpcSerializer.SerializeIndented(offlineData);
            }

            int gamePhase;
            string gamePhaseName;
            if (engineState.GamePhase == 1 || engineState.GamePhase == 2 || engineState.GamePhase == 3)
            {
                gamePhase = engineState.GamePhase;
                gamePhaseName = gamePhase == 1 ? "Picking Phase" : (gamePhase == 2 ? "Active Match" : "Post Match");
            }
            else if (!string.IsNullOrEmpty(engineState.CurrentMap) && !engineState.CurrentMap.Equals("idle", StringComparison.OrdinalIgnoreCase))
            {
                gamePhase = 2;
                gamePhaseName = "Active Match";
            }
            else
            {
                gamePhase = 0;
                gamePhaseName = "In Lobby/Menu";
            }

            var telemetryData = new
            {
                status = "success",
                clientRunning = true,
                gamePhase,
                gamePhaseName,
                hero = (object?)null,
                telemetry = new
                {
                    currentFps = engineState.CurrentFps,
                    memoryWorkingSetBytes = engineState.MemoryWorkingSetBytes,
                    memoryWorkingSetMb = engineState.MemoryWorkingSetMb,
                    activeMatchId = engineState.ActiveMatchId > 0 ? (int?)engineState.ActiveMatchId : null,
                    currentMap = string.IsNullOrWhiteSpace(engineState.CurrentMap) ? null : engineState.CurrentMap,
                    playerName = string.IsNullOrWhiteSpace(engineState.PlayerName) ? null : engineState.PlayerName,
                    connectedPlayers = engineState.ConnectedPlayers,
                    uptimeSeconds = engineState.UptimeSeconds,
                    gamePhase,
                    gamePhaseName
                },
                matchState = new
                {
                    activeMatch = engineState.ActiveMatchId > 0,
                    matchId = engineState.ActiveMatchId > 0 ? (int?)engineState.ActiveMatchId : null,
                    currentMap = string.IsNullOrWhiteSpace(engineState.CurrentMap) ? null : engineState.CurrentMap,
                    matchTimeSeconds = (double?)null
                },
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            return JsonRpcSerializer.SerializeIndented(telemetryData);
        }
        catch (Exception ex)
        {
            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "error",
                errorMessage = ex.Message
            });
        }
    }

    private JsonRpcResponse HandleResourcesSubscribe(JsonRpcRequest request)
    {
        if (!request.Params.HasValue ||
            request.Params.Value.ValueKind != JsonValueKind.Object ||
            !request.Params.Value.TryGetProperty("uri", out JsonElement uriEl) ||
            uriEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(uriEl.GetString()))
        {
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                "Missing or empty required 'uri' parameter for 'resources/subscribe'.");
        }

        string uri = uriEl.GetString()!.Trim();
        if (!IsValidResourceUri(uri))
        {
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                $"Unknown or unsupported resource URI: '{uri}'.");
        }

        _activeSubscriptions[uri] = 1;
        LogDiagnostic($"[MCP Server] Registered subscription for URI '{uri}'. Total subscriptions: {_activeSubscriptions.Count}");

        EnsureLogWatcherForUri(uri);

        return JsonRpcResponse.Success(request.Id, new JsonObject());
    }

    private JsonRpcResponse HandleResourcesUnsubscribe(JsonRpcRequest request)
    {
        if (!request.Params.HasValue ||
            request.Params.Value.ValueKind != JsonValueKind.Object ||
            !request.Params.Value.TryGetProperty("uri", out JsonElement uriEl) ||
            uriEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(uriEl.GetString()))
        {
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                "Missing or empty required 'uri' parameter for 'resources/unsubscribe'.");
        }

        string uri = uriEl.GetString()!.Trim();
        if (!IsValidResourceUri(uri))
        {
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                $"Unknown or unsupported resource URI: '{uri}'.");
        }

        _activeSubscriptions.TryRemove(uri, out _);
        LogDiagnostic($"[MCP Server] Unregistered subscription for URI '{uri}'. Total subscriptions: {_activeSubscriptions.Count}");

        CleanupLogWatcherForUri(uri);

        return JsonRpcResponse.Success(request.Id, new JsonObject());
    }

    public static bool IsValidResourceUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        string u = uri.Trim();
        if (KnownStaticResourceUris.Contains(u)) return true;
        if (DedicatedServerPortRegex.IsMatch(u)) return true;
        if (ReplaySummaryRegex.IsMatch(u)) return true;
        return false;
    }

    public async Task NotifyResourceUpdatedAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        string u = uri.Trim();

        bool notifyConcrete = _activeSubscriptions.ContainsKey(u);
        bool notifyServerTemplate = DedicatedServerPortRegex.IsMatch(u) && _activeSubscriptions.ContainsKey("kongor://servers/{port}/logs");
        bool notifyReplayTemplate = ReplaySummaryRegex.IsMatch(u) && _activeSubscriptions.ContainsKey("kongor://replays/{matchId}/summary");

        if (notifyConcrete)
        {
            LogDiagnostic($"[MCP Server] Dispatching 'notifications/resources/updated' for subscribed URI: {u}");
            await SendNotificationAsync("notifications/resources/updated", new { uri = u }, cancellationToken).ConfigureAwait(false);
        }

        if (notifyServerTemplate)
        {
            LogDiagnostic($"[MCP Server] Dispatching 'notifications/resources/updated' for subscribed template URI: kongor://servers/{{port}}/logs");
            await SendNotificationAsync("notifications/resources/updated", new { uri = "kongor://servers/{port}/logs" }, cancellationToken).ConfigureAwait(false);
        }

        if (notifyReplayTemplate)
        {
            LogDiagnostic($"[MCP Server] Dispatching 'notifications/resources/updated' for subscribed template URI: kongor://replays/{{matchId}}/summary");
            await SendNotificationAsync("notifications/resources/updated", new { uri = "kongor://replays/{matchId}/summary" }, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureLogWatcherForUri(string uri)
    {
        lock (_watcherLock)
        {
            if (!_activeSubscriptions.ContainsKey(uri)) return;

            try
            {
                string root = FindProjectRoot();
                if (uri.Equals("kongor://client/console", StringComparison.OrdinalIgnoreCase))
                {
                    if (_logWatchers.ContainsKey(uri)) return;
                    string clientLogsDir = Path.Combine(root, "games", "client", "userdata", "logs");
                    string dir = Directory.Exists(Path.Combine(root, "games", "client"))
                        ? clientLogsDir
                        : Path.Combine(root, "logs");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var fsw = new FileSystemWatcher(dir, "*.log")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };

                    FileSystemEventHandler handler = (_, _) =>
                    {
                        long now = Environment.TickCount64;
                        if (_lastWatcherNotifyTicks.TryGetValue(uri, out long last) && now - last < 50) return;
                        _lastWatcherNotifyTicks[uri] = now;
                        _ = NotifyResourceUpdatedAsync("kongor://client/console");
                    };

                    fsw.Changed += handler;
                    fsw.Created += handler;
                    fsw.Renamed += (s, e) => handler(s, e);
                    _logWatchers[uri] = fsw;
                }
                else if (uri.Equals("kongor://servers/{port}/logs", StringComparison.OrdinalIgnoreCase) ||
                         DedicatedServerPortRegex.IsMatch(uri))
                {
                    const string watcherKey = "__watcher_servers__";
                    if (_logWatchers.ContainsKey(watcherKey)) return;

                    string dir = Path.Combine(root, "logs");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var fsw = new FileSystemWatcher(dir, "*.log")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        IncludeSubdirectories = true,
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };

                    FileSystemEventHandler handler = (_, e) =>
                    {
                        string? port = null;
                        var m = Regex.Match(e.FullPath, @"[\\/](\d{4,5})[\\/]");
                        if (m.Success)
                        {
                            port = m.Groups[1].Value;
                        }
                        else
                        {
                            var fnMatch = Regex.Match(Path.GetFileName(e.FullPath), @"(\d{4,5})");
                            if (fnMatch.Success) port = fnMatch.Groups[1].Value;
                        }

                        string target = !string.IsNullOrEmpty(port) ? $"kongor://servers/{port}/logs" : "kongor://servers/{port}/logs";
                        long now = Environment.TickCount64;
                        if (_lastWatcherNotifyTicks.TryGetValue(target, out long last) && now - last < 50) return;
                        _lastWatcherNotifyTicks[target] = now;
                        _ = NotifyResourceUpdatedAsync(target);
                    };

                    fsw.Changed += handler;
                    fsw.Created += handler;
                    fsw.Renamed += (s, e) => handler(s, e);
                    _logWatchers[watcherKey] = fsw;
                }
                else if (uri.Equals("kongor://logs/master-server", StringComparison.OrdinalIgnoreCase))
                {
                    if (_logWatchers.ContainsKey(uri)) return;
                    string dir = Path.Combine(root, "src", "services", "master-server", "logs");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var fsw = new FileSystemWatcher(dir, "*.log")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };

                    FileSystemEventHandler handler = (_, _) =>
                    {
                        long now = Environment.TickCount64;
                        if (_lastWatcherNotifyTicks.TryGetValue(uri, out long last) && now - last < 50) return;
                        _lastWatcherNotifyTicks[uri] = now;
                        _ = NotifyResourceUpdatedAsync("kongor://logs/master-server");
                    };

                    fsw.Changed += handler;
                    fsw.Created += handler;
                    fsw.Renamed += (s, e) => handler(s, e);
                    _logWatchers[uri] = fsw;
                }
                else if (uri.Equals("kongor://logs/chat-server", StringComparison.OrdinalIgnoreCase))
                {
                    if (_logWatchers.ContainsKey(uri)) return;
                    string dir = Path.Combine(root, "src", "services", "chat-server", "logs");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var fsw = new FileSystemWatcher(dir, "*.log")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };

                    FileSystemEventHandler handler = (_, _) =>
                    {
                        long now = Environment.TickCount64;
                        if (_lastWatcherNotifyTicks.TryGetValue(uri, out long last) && now - last < 50) return;
                        _lastWatcherNotifyTicks[uri] = now;
                        _ = NotifyResourceUpdatedAsync("kongor://logs/chat-server");
                    };

                    fsw.Changed += handler;
                    fsw.Created += handler;
                    fsw.Renamed += (s, e) => handler(s, e);
                    _logWatchers[uri] = fsw;
                }
                else if (uri.Equals("kongor://replays/latest/summary", StringComparison.OrdinalIgnoreCase) ||
                         uri.Equals("kongor://replays/{matchId}/summary", StringComparison.OrdinalIgnoreCase) ||
                         ReplaySummaryRegex.IsMatch(uri))
                {
                    const string watcherKey = "__watcher_replays__";
                    if (_logWatchers.ContainsKey(watcherKey)) return;
                    string clientReplaysDir = Path.Combine(root, "games", "client", "userdata", "game", "replays");
                    string dir = Directory.Exists(clientReplaysDir)
                        ? clientReplaysDir
                        : Path.Combine(root, "artifacts", "replays");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var fsw = new FileSystemWatcher(dir, "*.honreplay")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        IncludeSubdirectories = true,
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };

                    FileSystemEventHandler handler = (_, e) =>
                    {
                        long now = Environment.TickCount64;
                        string key = "kongor://replays/latest/summary";
                        if (!_lastWatcherNotifyTicks.TryGetValue(key, out long last) || now - last >= 50)
                        {
                            _lastWatcherNotifyTicks[key] = now;
                            _ = NotifyResourceUpdatedAsync("kongor://replays/latest/summary");
                            _ = NotifyResourceUpdatedAsync("kongor://replays/{matchId}/summary");
                            var m = Regex.Match(Path.GetFileName(e.FullPath), @"(\d+)");
                            if (m.Success)
                            {
                                _ = NotifyResourceUpdatedAsync($"kongor://replays/{m.Groups[1].Value}/summary");
                            }
                        }
                    };

                    fsw.Changed += handler;
                    fsw.Created += handler;
                    fsw.Renamed += (s, e) => handler(s, e);
                    _logWatchers[watcherKey] = fsw;
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[MCP Server] Failed creating file watcher for URI '{uri}': {ex.Message}");
            }
        }
    }

    private void CleanupLogWatcherForUri(string uri)
    {
        lock (_watcherLock)
        {
            if (uri.Equals("kongor://servers/{port}/logs", StringComparison.OrdinalIgnoreCase) ||
                DedicatedServerPortRegex.IsMatch(uri))
            {
                bool hasRemainingServerSubs = _activeSubscriptions.Keys.Any(k =>
                    DedicatedServerPortRegex.IsMatch(k) || k.Equals("kongor://servers/{port}/logs", StringComparison.OrdinalIgnoreCase));
                if (!hasRemainingServerSubs && _logWatchers.TryRemove("__watcher_servers__", out var srvWatcher))
                {
                    DisposeWatcher(srvWatcher);
                }
            }
            else if (uri.Equals("kongor://replays/latest/summary", StringComparison.OrdinalIgnoreCase) ||
                     uri.Equals("kongor://replays/{matchId}/summary", StringComparison.OrdinalIgnoreCase) ||
                     ReplaySummaryRegex.IsMatch(uri))
            {
                bool hasRemainingReplaySubs = _activeSubscriptions.Keys.Any(k =>
                    ReplaySummaryRegex.IsMatch(k) ||
                    k.Equals("kongor://replays/latest/summary", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("kongor://replays/{matchId}/summary", StringComparison.OrdinalIgnoreCase));
                if (!hasRemainingReplaySubs && _logWatchers.TryRemove("__watcher_replays__", out var replayWatcher))
                {
                    DisposeWatcher(replayWatcher);
                }
            }
            else if (_logWatchers.TryRemove(uri, out var watcher))
            {
                DisposeWatcher(watcher);
            }
        }
    }

    private static void DisposeWatcher(IDisposable watcher)
    {
        try
        {
            if (watcher is FileSystemWatcher fsw)
            {
                fsw.EnableRaisingEvents = false;
            }
            watcher.Dispose();
        }
        catch { }
    }

    private static string ReadLatestLogTail(string relativeDir, int maxLines)
    {
        try
        {
            string root = FindProjectRoot();
            string dir = Path.Combine(root, relativeDir);
            if (!Directory.Exists(dir))
            {
                return $"[MCP Resource] Directory '{relativeDir}' does not exist on disk yet.";
            }

            var files = Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Length > 0)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0)
            {
                return $"[MCP Resource] No log files found in '{relativeDir}'.";
            }

            var latest = files[0];
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            List<string> lines = [];
            while (sr.ReadLine() is { } l) lines.Add(l);

            int start = Math.Max(0, lines.Count - maxLines);
            return $"--- Tail ({lines.Count - start}/{lines.Count} lines from {latest.Name}) ---\n" + string.Join("\n", lines.Skip(start));
        }
        catch (Exception ex)
        {
            return $"[MCP Resource] Failed to read log: {ex.Message}";
        }
    }

    private static string ReadDedicatedServerLogTail(string port, int maxLines)
    {
        try
        {
            string root = FindProjectRoot();
            string[] searchDirs =
            [
                Path.Combine(root, "games", "server", "logs"),
                Path.Combine(root, "logs", "servers", port),
                Path.Combine(root, "logs", "servers"),
                Path.Combine(root, "logs")
            ];

            List<FileInfo> matchingFiles = [];
            foreach (var dir in searchDirs)
            {
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, $"*{port}*.log", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.Length > 0);
                    matchingFiles.AddRange(files);
                }
            }

            if (matchingFiles.Count == 0)
            {
                return $"[MCP Resource] No server logs found for port {port}.";
            }

            var latest = matchingFiles.OrderByDescending(f => f.LastWriteTimeUtc).First();
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            List<string> lines = [];
            while (sr.ReadLine() is { } l) lines.Add(l);

            int start = Math.Max(0, lines.Count - maxLines);
            return $"--- Tail ({lines.Count - start}/{lines.Count} lines from {latest.Name} on port {port}) ---\n" + string.Join("\n", lines.Skip(start));
        }
        catch (Exception ex)
        {
            return $"[MCP Resource] Failed reading server log for port {port}: {ex.Message}";
        }
    }

    private string ReadClientErrors()
    {
        try
        {
            var inMemErrors = _toolRegistry.ClientCommandRemediationService.GetRecentErrors(20);
            if (inMemErrors.Count > 0)
            {
                return JsonRpcSerializer.SerializeIndented(new
                {
                    status = "success",
                    source = "in_memory",
                    count = inMemErrors.Count,
                    errors = inMemErrors
                });
            }

            string root = FindProjectRoot();
            string ndjsonPath = Path.Combine(root, "logs", "MCP Conversation History", "client_command_errors.ndjson");
            if (File.Exists(ndjsonPath))
            {
                var lines = File.ReadAllLines(ndjsonPath);
                var recentLines = lines.TakeLast(20).ToList();
                return JsonRpcSerializer.SerializeIndented(new
                {
                    status = "success",
                    source = "disk_ndjson",
                    count = recentLines.Count,
                    lines = recentLines
                });
            }

            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "success",
                count = 0,
                message = "No client command errors recorded."
            });
        }
        catch (Exception ex)
        {
            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "error",
                errorMessage = ex.Message
            });
        }
    }

    private string ReadClusterMetrics()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            return JsonRpcSerializer.SerializeIndented(new
            {
                timestamp = _timeProvider.GetUtcNow().ToString("o"),
                host = new
                {
                    machineName = Environment.MachineName,
                    osVersion = Environment.OSVersion.ToString(),
                    processorCount = Environment.ProcessorCount,
                    is64BitOperatingSystem = Environment.Is64BitOperatingSystem
                },
                process = new
                {
                    processName = proc.ProcessName,
                    pid = proc.Id,
                    workingSetMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 2),
                    threads = proc.Threads.Count,
                    upTime = (_timeProvider.GetUtcNow() - proc.StartTime.ToUniversalTime()).ToString(@"dd\.hh\:mm\:ss")
                },
                clusterServices = new[]
                {
                    new { name = "Master Server", defaultPort = 55555, protocol = "HTTP/REST" },
                    new { name = "Chat Server", defaultPort = 11031, protocol = "TCP/Binary" },
                    new { name = "Web API", defaultPort = 5200, protocol = "HTTP/REST" },
                    new { name = "Web UI", defaultPort = 5266, protocol = "HTTP/Web" },
                    new { name = "COMPEL Supervisor", defaultPort = 11234, protocol = "HTTP/REST" },
                    new { name = "Dedicated Server 1", defaultPort = 11235, protocol = "UDP/Game" }
                }
            });
        }
        catch (Exception ex)
        {
            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "error",
                errorMessage = ex.Message
            });
        }
    }

    private static string ReadLatestReplaySummary()
    {
        try
        {
            string root = FindProjectRoot();
            string[] searchDirs =
            [
                Path.Combine(root, "replays"),
                Path.Combine(root, "games", "client", "replays"),
                Path.Combine(root, "userdata", "replays")
            ];

            FileInfo? latestReplay = null;
            foreach (var dir in searchDirs)
            {
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.honreplay", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.Length > 0)
                        .OrderByDescending(fi => fi.LastWriteTimeUtc);
                    var candidate = files.FirstOrDefault();
                    if (candidate != null && (latestReplay == null || candidate.LastWriteTimeUtc > latestReplay.LastWriteTimeUtc))
                    {
                        latestReplay = candidate;
                    }
                }
            }

            if (latestReplay == null)
            {
                return JsonRpcSerializer.SerializeIndented(new
                {
                    status = "no_replays_found",
                    message = "No .honreplay files found in replays/ directories.",
                    searchedDirectories = searchDirs
                });
            }

            string? manifestSnippet = null;
            try
            {
                using var zip = ZipFile.OpenRead(latestReplay.FullName);
                var manifestEntry = zip.GetEntry("manifest.xml") ?? zip.GetEntry("replay.manifest");
                if (manifestEntry != null)
                {
                    using var ms = manifestEntry.Open();
                    using var sr = new StreamReader(ms);
                    manifestSnippet = sr.ReadToEnd();
                }
            }
            catch
            {
                // Not a valid zip or corrupted
            }

            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "success",
                fileName = latestReplay.Name,
                fullPath = latestReplay.FullName,
                sizeBytes = latestReplay.Length,
                lastModified = latestReplay.LastWriteTimeUtc.ToString("o"),
                hasManifest = manifestSnippet != null,
                manifest = manifestSnippet
            });
        }
        catch (Exception ex)
        {
            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "error",
                errorMessage = ex.Message
            });
        }
    }

    public static string FindProjectRoot()
    {
        if (Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out var bundledLayout))
        {
            return bundledLayout.Root;
        }

        string[] candidateStarts =
        [
            AppContext.BaseDirectory,
            AppDomain.CurrentDomain.BaseDirectory,
            Directory.GetCurrentDirectory()
        ];

        // Pass 1: Look specifically for the ProjectKongor solution or PROJECT.md marker
        foreach (string start in candidateStarts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            try
            {
                DirectoryInfo? current = new(start);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                        File.Exists(Path.Combine(current.FullName, "ProjectKongor.sln")) ||
                        File.Exists(Path.Combine(current.FullName, "PROJECT.md")))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[McpServer] FindProjectRoot pass 1 walk skipped: {ex.Message}");
            }
        }

        // Pass 2: Fallback to .git directory marker
        foreach (string start in candidateStarts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            try
            {
                DirectoryInfo? current = new(start);
                while (current != null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[McpServer] FindProjectRoot pass 2 walk skipped: {ex.Message}");
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private JsonRpcResponse HandlePromptsList(JsonRpcRequest request)
    {
        var result = new PromptListResult
        {
            Prompts =
            [
                new McpPrompt
                {
                    Name = "debug-k2-ui-blackscreen",
                    Description = "Engineering runbook to isolate K2 black screen, missing textures, and broken UI interfaces.",
                    Arguments =
                    [
                        new PromptArgument { Name = "interface", Description = "Target interface name (e.g. 'main')", Required = false }
                    ]
                },
                new McpPrompt
                {
                    Name = "setup-scrimmage-test",
                    Description = "Engineering runbook to orchestrate local scrimmage: Aspire cluster + dedicated server + 2 clients + 8 bots.",
                    Arguments =
                    [
                        new PromptArgument { Name = "port", Description = "Dedicated server listen port (default: 11235)", Required = false },
                        new PromptArgument { Name = "map", Description = "Match map name (default: 'caldavar')", Required = false }
                    ]
                },
                new McpPrompt
                {
                    Name = "generate_hero_mod",
                    Description = "Automated template for creating custom heroes, Lua balance curves, and textures.",
                    Arguments =
                    [
                        new PromptArgument { Name = "heroName", Description = "Internal entity name for custom hero (e.g. 'Hero_CustomPyro')", Required = true },
                        new PromptArgument { Name = "primaryAttribute", Description = "Primary attribute: Strength, Agility, or Intelligence", Required = false },
                        new PromptArgument { Name = "role", Description = "Hero archetype/role e.g. Nuker, Carry, Support", Required = false }
                    ]
                },
                new McpPrompt
                {
                    Name = "analyze_replay_teamfight",
                    Description = "Auto-triage replay files for teamfight turning points, gold swings, and hero deaths.",
                    Arguments =
                    [
                        new PromptArgument { Name = "replayPath", Description = "Path to .honreplay file (defaults to latest recorded match)", Required = false },
                        new PromptArgument { Name = "matchId", Description = "Match ID to query and inspect", Required = false }
                    ]
                },
                new McpPrompt
                {
                    Name = "diagnose_crash_exitcode",
                    Description = "Automated root-cause analysis for dedicated server or client crash exit codes and minidump triage.",
                    Arguments =
                    [
                        new PromptArgument { Name = "exitCode", Description = "Hex or decimal process exit code (e.g. '0xC0000005', '-1073741819')", Required = true },
                        new PromptArgument { Name = "logPath", Description = "Path to crash minidump or latest engine log", Required = false }
                    ]
                }
            ]
        };

        LogDiagnostic($"[MCP Server] prompts/list returning {result.Prompts.Count} prompts.");
        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse HandlePromptsGet(JsonRpcRequest request)
    {
        if (!request.Params.HasValue ||
            request.Params.Value.ValueKind != JsonValueKind.Object ||
            !request.Params.Value.TryGetProperty("name", out JsonElement nameEl) ||
            nameEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            return JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCode.InvalidParams, "Missing or invalid required 'name' parameter for 'prompts/get'.");
        }

        string promptName = nameEl.GetString()!.Trim();
        LogDiagnostic($"[MCP Server] prompts/get for prompt: '{promptName}'");

        if (promptName.Equals("debug-k2-ui-blackscreen", StringComparison.OrdinalIgnoreCase))
        {
            string targetInterface = string.Empty;
            if (request.Params.Value.TryGetProperty("arguments", out JsonElement argsEl))
            {
                if (argsEl.TryGetProperty("interfacePath", out JsonElement ipEl) ||
                    argsEl.TryGetProperty("interface", out ipEl) ||
                    argsEl.TryGetProperty("path", out ipEl))
                {
                    targetInterface = $"\nTarget Interface: {ipEl.GetString()}";
                }
            }

            var result = new GetPromptResult
            {
                Description = "Runbook: Diagnosing K2 Blackscreen and Broken UI",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = "user",
                        Content = new TextContentItem(
                            "Execute standard ProjectKongor UI Blackscreen diagnostics:\n" +
                            "1. Purge shader, texture, and UI caches via `kongor_engine_cache_purge`.\n" +
                            "2. Inspect active UI tree with `kongor_k2_ui_inspect` to verify widget hierarchy.\n" +
                            "3. Verify active VFS mounts with `kongor_vfs_resolve` to ensure textures/fallbacks are not shadowed.\n" +
                            "4. Check for unhandled client errors with `kongor_game_client_manage action: 'errors'`." +
                            targetInterface
                        )
                    }
                ]
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        if (promptName.Equals("setup-scrimmage-test", StringComparison.OrdinalIgnoreCase))
        {
            var result = new GetPromptResult
            {
                Description = "Runbook: Setting Up Local Scrimmage Test Environment",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = "user",
                        Content = new TextContentItem(
                            "Execute standard ProjectKongor Scrimmage Setup:\n" +
                            "1. Start Aspire microservices: `kongor_server_manage action: 'start', target: 'cluster'`.\n" +
                            "2. Launch dedicated game server: `kongor_game_server_manage action: 'launch', port: 11235`.\n" +
                            "3. Seed test account: `kongor_db_fixture action: 'seed_account', username: 'DevTester'`.\n" +
                            "4. Connect client: `kongor_game_client_manage action: 'launch', connectAddress: '127.0.0.1:11235'`.\n" +
                            "5. Add fake players to balance teams: `kongor_game_command command: 'AddFakePlayer 1', port: 11235` and `AddFakePlayer 2`.\n" +
                            "6. Query match state via `kongor_match_state`."
                        )
                    }
                ]
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        if (promptName.Equals("generate_hero_mod", StringComparison.OrdinalIgnoreCase))
        {
            string heroName = "Hero_Custom";
            string primaryAttr = "Intelligence";
            string role = "Nuker";
            if (request.Params.Value.TryGetProperty("arguments", out JsonElement argsEl))
            {
                if (argsEl.TryGetProperty("heroName", out JsonElement hEl) || argsEl.TryGetProperty("hero_name", out hEl))
                    heroName = hEl.GetString() ?? heroName;
                if (argsEl.TryGetProperty("primaryAttribute", out JsonElement paEl) || argsEl.TryGetProperty("primary_attribute", out paEl))
                    primaryAttr = paEl.GetString() ?? primaryAttr;
                if (argsEl.TryGetProperty("role", out JsonElement rEl))
                    role = rEl.GetString() ?? role;
            }

            var result = new GetPromptResult
            {
                Description = $"Runbook: Generating Custom Hero Mod for '{heroName}'",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = "user",
                        Content = new TextContentItem(
                            $"Follow these steps to generate and mount the custom hero mod '{heroName}':\n" +
                            $"1. Define Entity XML: Scaffold `heroes/{heroName}/{heroName.ToLowerInvariant()}.entity` with PrimaryAttribute='{primaryAttr}' and Archetype='{role}'.\n" +
                            $"2. Inspect & Sanitize Textures: Inspect icon and ability .tga/.dds textures with `kongor_asset_inspect` to ensure 0x08 origin (bottom-left) and non-white channels.\n" +
                            $"3. Compile & Package Mod: Build `.honmod` or `.s2z` package with `kongor_mod_build action: 'build_honmod', output_path: 'mods/{heroName}.honmod'`.\n" +
                            $"4. Mount into Override: Mount the package into games/client/override using `kongor_mod_build action: 'mount', mod_path: 'mods/{heroName}.honmod'`.\n" +
                            $"5. Purge Engine Cache: Execute `kongor_engine_cache_purge action: 'all'` to flush texture and shader caches.\n" +
                            $"6. In-Game Practice Verification: Launch directly into Caldavar with `kongor_match_launch map: 'caldavar', hero: '{heroName}'`."
                        )
                    }
                ]
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        if (promptName.Equals("analyze_replay_teamfight", StringComparison.OrdinalIgnoreCase))
        {
            string targetMatch = "latest";
            if (request.Params.Value.TryGetProperty("arguments", out JsonElement argsEl))
            {
                if (argsEl.TryGetProperty("matchId", out JsonElement mEl) || argsEl.TryGetProperty("match_id", out mEl))
                    targetMatch = mEl.GetString() ?? targetMatch;
                else if (argsEl.TryGetProperty("replayPath", out JsonElement rpEl) || argsEl.TryGetProperty("replay_path", out rpEl))
                    targetMatch = rpEl.GetString() ?? targetMatch;
            }

            var result = new GetPromptResult
            {
                Description = $"Runbook: Triaging Replay Teamfight Turning Points ({targetMatch})",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = "user",
                        Content = new TextContentItem(
                            $"Execute Replay Teamfight Analysis for match '{targetMatch}':\n" +
                            $"1. Query Replay Metadata: Read `kongor://replays/latest/summary` to fetch duration, team rosters, and winner.\n" +
                            $"2. Inspect Match Milestones: Execute `kongor_replay_inspect action: 'summary'` to extract hero kills and net-worth graph.\n" +
                            $"3. Identify Turning Points: Locate critical teamfight clusters where team gold differential swung by >= 1,500 gold.\n" +
                            $"4. Playback & Seek: Use `kongor_replay_playback action: 'seek'` to jump directly to the target teamfight timestamps."
                        )
                    }
                ]
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        if (promptName.Equals("diagnose_crash_exitcode", StringComparison.OrdinalIgnoreCase))
        {
            string exitCode = "0xC0000005";
            string logPath = "default";
            if (request.Params.Value.TryGetProperty("arguments", out JsonElement argsEl))
            {
                if (argsEl.TryGetProperty("exitCode", out JsonElement ecEl) || argsEl.TryGetProperty("exit_code", out ecEl))
                    exitCode = ecEl.GetString() ?? exitCode;
                if (argsEl.TryGetProperty("logPath", out JsonElement lpEl) || argsEl.TryGetProperty("log_path", out lpEl))
                    logPath = lpEl.GetString() ?? logPath;
            }

            string reason;
            if (exitCode.Contains("C0000005", StringComparison.OrdinalIgnoreCase) || exitCode.Contains("-1073741819"))
                reason = "STATUS_ACCESS_VIOLATION (Null pointer dereference or corrupted memory in K2 engine binary)";
            else if (exitCode.Contains("C00000FD", StringComparison.OrdinalIgnoreCase))
                reason = "STATUS_STACK_OVERFLOW (Infinite recursion in Lua script or nested XML UI templates)";
            else if (exitCode.Contains("E0434352", StringComparison.OrdinalIgnoreCase))
                reason = "STATUS_CLR_EXCEPTION (Unhandled .NET Exception in managed host process)";
            else
                reason = $"Process terminated with abnormal exit code {exitCode}";

            var result = new GetPromptResult
            {
                Description = $"Runbook: Diagnosing Process Crash ({exitCode})",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = "user",
                        Content = new TextContentItem(
                            $"Automated Crash Triage for Exit Code {exitCode} [{reason}]:\n" +
                            $"1. Query Recent Failures: Read `kongor://client/errors` to inspect recently collected command errors and auto-remediations.\n" +
                            $"2. Check Dedicated Server Logs: Read `kongor://servers/11235/logs` (or target port) to inspect engine panic or minidump trace.\n" +
                            $"3. Validate Script & Interface Syntax: Run `kongor_k2_lint target_directory: 'games/client/override'` to detect malformed XML or Lua syntax.\n" +
                            $"4. Check VFS Resolution: Verify asset paths with `kongor_vfs_resolve` to ensure required binary resources exist."
                        )
                    }
                ]
            };
            return JsonRpcResponse.Success(request.Id, result);
        }

        return JsonRpcResponse.CreateError(request.Id, JsonRpcErrorCode.InvalidParams, $"Prompt '{promptName}' not found.");
    }

    private JsonRpcResponse HandlePing(JsonRpcRequest request)
    {
        return JsonRpcResponse.Success(request.Id, new JsonObject());
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        bool? includeAliases = null;
        string? toolsets = null;

        if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
        {
            JsonElement paramsEl = request.Params.Value;
            if ((paramsEl.TryGetProperty("includeAliases", out JsonElement incEl) ||
                 paramsEl.TryGetProperty("include_aliases", out incEl)) &&
                (incEl.ValueKind == JsonValueKind.True || incEl.ValueKind == JsonValueKind.False))
            {
                includeAliases = incEl.GetBoolean();
            }

            if (paramsEl.TryGetProperty("toolsets", out JsonElement tsEl) && tsEl.ValueKind == JsonValueKind.String)
            {
                toolsets = tsEl.GetString();
            }
        }

        var tools = _toolRegistry.ListTools(includeAliases: includeAliases, toolsets: toolsets);
        var result = new ToolsListResult
        {
            Tools = [.. tools]
        };

        LogDiagnostic($"[MCP Server] tools/list (includeAliases={includeAliases?.ToString() ?? "env"}, toolsets={toolsets ?? "env"}) returning {tools.Count} tools: {string.Join(", ", tools.Select(t => t.Name))}");
        return JsonRpcResponse.Success(request.Id, result);
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!request.Params.HasValue || request.Params.Value.ValueKind != JsonValueKind.Object)
        {
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                "Invalid params for 'tools/call': Expected an object containing 'name' and 'arguments'.");
        }

        JsonElement paramsElement = request.Params.Value;

        if (!paramsElement.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return JsonRpcResponse.CreateError(
                request.Id,
                JsonRpcErrorCode.InvalidParams,
                "Invalid params for 'tools/call': 'name' property is required and must be a string.");
        }

        string toolName = nameElement.GetString()!;

        JsonElement argumentsElement = paramsElement.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object
            ? args
            : JsonElement.Parse("{}");
        LogDiagnostic($"[MCP Server] Executing tool '{toolName}' with arguments: {argumentsElement.GetRawText()}");

        if (_readOnlyMode && !IsToolAllowedInReadOnlyMode(toolName, argumentsElement))
        {
            LogDiagnostic($"[MCP Server] Blocked mutating tool '{toolName}' in read-only mode.");
            return JsonRpcResponse.Success(
                request.Id,
                ToolCallResult.Error($"Tool '{toolName}' is blocked because the MCP server is running in '--read-only' mode. Launch without '--read-only' to allow mutating operations."));
        }

        Activity? activity = Activity.Current;
        bool createdActivity = false;
        if (activity == null)
        {
            string? traceParent = ExtractTraceparent(request, request.RawJson);
            string? traceState = ExtractTracestate(request, request.RawJson);

            if (traceParent != null && ActivityContext.TryParse(traceParent, traceState, out ActivityContext parentCtx))
            {
                activity = s_activitySource.StartActivity($"MCP {toolName}", ActivityKind.Server, parentCtx);
            }
            else
            {
                if (!string.IsNullOrEmpty(traceParent))
                {
                    LogDiagnostic($"[MCP Server] Malformed W3C traceparent '{traceParent}'. Starting fresh W3C root trace per W3C specification.");
                }
                Activity.Current = null;
                activity = s_activitySource.StartActivity($"MCP {toolName}", ActivityKind.Server);
            }
            createdActivity = activity != null;
        }

        if (activity != null)
        {
            activity.SetTag("mcp.tool", toolName);
            activity.SetTag("mcp.id", request.Id?.ToString());
        }

        object? progressToken = ExtractProgressToken(request, request.RawJson);

        IMcpProgressReporter? progressReporter = progressToken != null
            ? new ActionMcpProgressReporter((prog, total, msg, ct) =>
                SendProgressNotificationAsync(progressToken, prog, total, msg, ct))
            : null;

        string? agentId = ExtractAgentId(request, request.RawJson, argumentsElement);
        if (activity != null && !string.IsNullOrEmpty(agentId))
        {
            activity.SetTag("mcp.agentId", agentId);
        }

        var prevProgressReporter = McpProgressContext.Current;
        var prevSamplingService = McpSamplingContext.Current;
        var prevAgentId = McpAgentContext.CurrentAgentId;
        McpProgressContext.Current = progressReporter;
        McpSamplingContext.Current = this;
        McpAgentContext.CurrentAgentId = agentId;

        ToolCallResult toolResult;
        int toolTimeoutSeconds = 60;
        if (argumentsElement.ValueKind == JsonValueKind.Object)
        {
            if (argumentsElement.TryGetProperty("timeoutMs", out JsonElement tMsEl) && tMsEl.TryGetInt32(out int tMs) && tMs > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, (tMs / 1000) + 15);
            }
            if (argumentsElement.TryGetProperty("durationSeconds", out JsonElement dsEl) && dsEl.TryGetInt32(out int ds) && ds > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, ds + 15);
            }
            if (argumentsElement.TryGetProperty("duration_seconds", out JsonElement dsEl2) && dsEl2.TryGetInt32(out int ds2) && ds2 > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, ds2 + 15);
            }
            if (argumentsElement.TryGetProperty("duration", out JsonElement durEl) && durEl.TryGetInt32(out int dur) && dur > 0)
            {
                toolTimeoutSeconds = Math.Max(toolTimeoutSeconds, dur + 15);
            }
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(toolTimeoutSeconds));

            toolResult = await ExecuteToolWithMiddlewareAsync(toolName, argumentsElement, timeoutCts.Token).ConfigureAwait(false);
            if (activity != null)
            {
                activity.SetTag("mcp.isError", toolResult.IsError);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogDiagnostic($"[MCP Server] Tool '{toolName}' timed out after {toolTimeoutSeconds} seconds.");
            toolResult = ToolCallResult.Error($"Tool '{toolName}' timed out after {toolTimeoutSeconds} seconds. Engine or UI did not respond in time.");
            activity?.SetTag("mcp.timeout", true);
        }
        catch (OperationCanceledException)
        {
            LogDiagnostic($"[MCP Server] Tool '{toolName}' was cancelled by client.");
            throw;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[MCP Server] Tool '{toolName}' encountered an unhandled error: {ex.Message}");
            toolResult = ToolCallResult.Error($"Tool '{toolName}' failed: {ex.Message}");
            activity?.SetTag("mcp.exception", ex.Message);
        }
        finally
        {
            if (createdActivity)
            {
                activity?.Dispose();
            }
            McpProgressContext.Current = prevProgressReporter;
            McpSamplingContext.Current = prevSamplingService;
            McpAgentContext.CurrentAgentId = prevAgentId;
        }

        if (string.CompareOrdinal(_negotiatedProtocolVersion, "2025-06-18") >= 0)
        {
            toolResult = toolResult.WithStructuredContentIfJson();
        }

        return JsonRpcResponse.Success(request.Id, toolResult);
    }

    private Task<ToolCallResult> ExecuteToolWithMiddlewareAsync(string toolName, JsonElement argumentsElement, CancellationToken cancellationToken)
    {
        // ExecutionFidelity.SimulatedTestDouble gating is enforced directly inside McpToolRegistry.ExecuteToolAsync
        // via parsed JSON property inspection so stdio, sdk-stdio, http, and kongor_call_tool share a single gate.
        return _toolRegistry.ExecuteToolAsync(toolName, argumentsElement, cancellationToken);
    }

    /// <summary>
    /// Initiates a sampling/createMessage request to the client LLM (MCP 2024-11-05 Specification).
    /// </summary>
    public async Task<CreateMessageResult> CreateMessageAsync(CreateMessageParams parameters, CancellationToken cancellationToken = default)
    {
        if (!IsSamplingSupported)
        {
            throw new InvalidOperationException("Client does not support MCP sampling ('capabilities.sampling' was not declared during initialize).");
        }

        long requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        string idStr = requestId.ToString();
        _pendingClientRequests[idStr] = tcs;

        using var reg = cancellationToken.Register(() =>
        {
            if (_pendingClientRequests.TryRemove(idStr, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "sampling/createMessage",
            @params = parameters
        };

        string requestJson = JsonRpcSerializer.Serialize(rpcRequest);
        _sentRequests.Enqueue(requestJson);
        while (_sentRequests.Count > 1000 && _sentRequests.TryDequeue(out _)) { }
        LogDiagnostic($"[MCP Server] Initiated sampling request #{requestId}: {TruncateLog(requestJson)}");

        await SendRawMessageAsync(requestJson, cancellationToken).ConfigureAwait(false);

        JsonRpcResponse response = await tcs.Task.ConfigureAwait(false);
        if (response.Error != null)
        {
            throw new InvalidOperationException($"Client sampling rejected: [{response.Error.Code}] {response.Error.Message}");
        }

        if (response.Result is JsonElement elem)
        {
            return JsonRpcSerializer.Deserialize<CreateMessageResult>(elem)
                   ?? throw new InvalidOperationException("Failed to deserialize CreateMessageResult from client response.");
        }

        if (response.Result != null)
        {
            string resJson = JsonRpcSerializer.Serialize(response.Result);
            return JsonRpcSerializer.Deserialize<CreateMessageResult>(resJson)
                   ?? throw new InvalidOperationException("Failed to deserialize CreateMessageResult from client response.");
        }

        throw new InvalidOperationException("Client returned empty result for sampling/createMessage.");
    }

    /// <summary>
    /// High-level assistant helper that prompts the client LLM for root-cause analysis on K2 engine/server crashes.
    /// </summary>
    public async Task<string?> RequestCrashAnalysisAsync(string exitCode, string errorOrLogText, CancellationToken cancellationToken = default)
    {
        if (!IsSamplingSupported)
        {
            return null;
        }

        var parameters = new CreateMessageParams
        {
            SystemPrompt = "You are an expert systems engineer and K2 game engine debugger for ProjectKongor.",
            Messages =
            [
                new SamplingMessage
                {
                    Role = "user",
                    Content = new SamplingContent
                    {
                        Type = "text",
                        Text = $"A dedicated game server or client crashed with exit code {exitCode}.\nDiagnostic log / context:\n{errorOrLogText}\n\nPlease analyze the root cause and provide an actionable fix."
                    }
                }
            ],
            MaxTokens = 500,
            Temperature = 0.2
        };

        try
        {
            var result = await CreateMessageAsync(parameters, cancellationToken).ConfigureAwait(false);
            return result.Content?.Text;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[MCP Server] Crash analysis sampling failed: {ex.Message}");
            return null;
        }
    }

    private JsonRpcResponse HandleSetLogLevel(JsonRpcRequest request)
    {
        if (request.Params.HasValue)
        {
            var @params = JsonRpcSerializer.Deserialize<SetLogLevelParams>(request.Params.Value);
            if (!string.IsNullOrWhiteSpace(@params?.Level))
            {
                _currentLogLevel = @params.Level.ToLowerInvariant();
                LogDiagnostic($"[MCP Server] Log level updated to '{_currentLogLevel}'");
            }
        }

        return JsonRpcResponse.Success(request.Id, new JsonObject());
    }

    public void LogDiagnostic(string message)
    {
        try
        {
            _diagnosticChannel.Writer.TryWrite($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] {message}");
        }
        catch
        {
            // Suppress logging write errors to avoid tearing down stdio message loop
        }
    }

    private async Task DrainDiagnosticsLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _diagnosticChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out string? msg))
                {
                    try
                    {
                        await _errorWriter.WriteLineAsync(msg.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _errorWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static bool TryGetTraceparentString(JsonElement element, out string? traceparent)
    {
        traceparent = null;
        if (element.ValueKind != JsonValueKind.Object) return false;

        string[] propertyNames = ["traceparent", "traceParent", "Traceparent", "TraceParent", "trace_parent"];
        foreach (var propName in propertyNames)
        {
            if (element.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    traceparent = s.Trim();
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryGetTracestateString(JsonElement element, out string? traceState)
    {
        traceState = null;
        if (element.ValueKind != JsonValueKind.Object) return false;

        string[] propertyNames = ["tracestate", "traceState", "TraceState", "trace_state"];
        foreach (var propName in propertyNames)
        {
            if (element.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    traceState = s.Trim();
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryGetMetaElement(JsonElement element, out JsonElement meta)
    {
        meta = default;
        if (element.ValueKind != JsonValueKind.Object) return false;

        string[] metaNames = ["_meta", "meta", "Meta"];
        foreach (var propName in metaNames)
        {
            if (element.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.Object)
            {
                meta = el;
                return true;
            }
        }
        return false;
    }

    private static string? ExtractTraceparent(JsonRpcRequest request, string? rawLine)
    {
        string? traceParent = null;

        if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
        {
            var p = request.Params.Value;
            if (TryGetMetaElement(p, out JsonElement meta) && TryGetTraceparentString(meta, out traceParent))
            {
                return traceParent;
            }
            if (TryGetTraceparentString(p, out traceParent))
            {
                return traceParent;
            }
            if (p.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
            {
                if (TryGetMetaElement(args, out JsonElement argMeta) && TryGetTraceparentString(argMeta, out traceParent))
                {
                    return traceParent;
                }
                if (TryGetTraceparentString(args, out traceParent))
                {
                    return traceParent;
                }
            }
        }

        if (request.ExtensionData != null)
        {
            if (request.ExtensionData.TryGetValue("_meta", out JsonElement extMeta) && extMeta.ValueKind == JsonValueKind.Object)
            {
                if (TryGetTraceparentString(extMeta, out traceParent))
                {
                    return traceParent;
                }
            }
            if (request.ExtensionData.TryGetValue("traceparent", out JsonElement extDirectTp) && extDirectTp.ValueKind == JsonValueKind.String)
            {
                string? s = extDirectTp.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            if (request.ExtensionData.TryGetValue("traceParent", out JsonElement extDirectTp2) && extDirectTp2.ValueKind == JsonValueKind.String)
            {
                string? s = extDirectTp2.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }

        string? lineToInspect = !string.IsNullOrWhiteSpace(rawLine) ? rawLine : request.RawJson;
        if (!string.IsNullOrWhiteSpace(lineToInspect))
        {
            try
            {
                using var rootDoc = JsonDocument.Parse(lineToInspect);
                if (TryGetMetaElement(rootDoc.RootElement, out JsonElement rootMeta) && TryGetTraceparentString(rootMeta, out traceParent))
                {
                    return traceParent;
                }
                if (TryGetTraceparentString(rootDoc.RootElement, out traceParent))
                {
                    return traceParent;
                }
            }
            catch { }
        }

        return null;
    }

    private static string? ExtractTracestate(JsonRpcRequest request, string? rawLine)
    {
        string? traceState = null;

        if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
        {
            var p = request.Params.Value;
            if (TryGetMetaElement(p, out JsonElement meta) && TryGetTracestateString(meta, out traceState))
            {
                return traceState;
            }
            if (TryGetTracestateString(p, out traceState))
            {
                return traceState;
            }
            if (p.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
            {
                if (TryGetMetaElement(args, out JsonElement argMeta) && TryGetTracestateString(argMeta, out traceState))
                {
                    return traceState;
                }
                if (TryGetTracestateString(args, out traceState))
                {
                    return traceState;
                }
            }
        }

        if (request.ExtensionData != null)
        {
            if (request.ExtensionData.TryGetValue("_meta", out JsonElement extMeta) && extMeta.ValueKind == JsonValueKind.Object)
            {
                if (TryGetTracestateString(extMeta, out traceState))
                {
                    return traceState;
                }
            }
            if (request.ExtensionData.TryGetValue("tracestate", out JsonElement extDirectTs) && extDirectTs.ValueKind == JsonValueKind.String)
            {
                string? s = extDirectTs.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            if (request.ExtensionData.TryGetValue("traceState", out JsonElement extDirectTs2) && extDirectTs2.ValueKind == JsonValueKind.String)
            {
                string? s = extDirectTs2.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }

        string? lineToInspect = !string.IsNullOrWhiteSpace(rawLine) ? rawLine : request.RawJson;
        if (!string.IsNullOrWhiteSpace(lineToInspect))
        {
            try
            {
                using var rootDoc = JsonDocument.Parse(lineToInspect);
                if (TryGetMetaElement(rootDoc.RootElement, out JsonElement rootMeta) && TryGetTracestateString(rootMeta, out traceState))
                {
                    return traceState;
                }
                if (TryGetTracestateString(rootDoc.RootElement, out traceState))
                {
                    return traceState;
                }
            }
            catch { }
        }

        return null;
    }

    private static object? ExtractProgressToken(JsonRpcRequest request, string? rawLine)
    {
        object? progressToken = null;

        if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
        {
            var p = request.Params.Value;
            if (TryGetMetaElement(p, out JsonElement meta))
            {
                progressToken = ParseProgressToken(meta);
            }
            if (progressToken == null)
            {
                progressToken = ParseProgressToken(p);
            }
            if (progressToken == null && p.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
            {
                if (TryGetMetaElement(args, out JsonElement argMeta))
                {
                    progressToken = ParseProgressToken(argMeta);
                }
                if (progressToken == null)
                {
                    progressToken = ParseProgressToken(args);
                }
            }
        }

        if (progressToken == null && request.ExtensionData != null)
        {
            if (request.ExtensionData.TryGetValue("_meta", out JsonElement extMeta) && extMeta.ValueKind == JsonValueKind.Object)
            {
                progressToken = ParseProgressToken(extMeta);
            }
            if (progressToken == null)
            {
                foreach (var key in new[] { "progressToken", "progress_token", "ProgressToken" })
                {
                    if (request.ExtensionData.TryGetValue(key, out JsonElement extPt))
                    {
                        progressToken = ParseProgressTokenValue(extPt);
                        if (progressToken != null) break;
                    }
                }
            }
        }

        string? lineToInspect = !string.IsNullOrWhiteSpace(rawLine) ? rawLine : request.RawJson;
        if (progressToken == null && !string.IsNullOrWhiteSpace(lineToInspect))
        {
            try
            {
                using var rootDoc = JsonDocument.Parse(lineToInspect);
                if (TryGetMetaElement(rootDoc.RootElement, out JsonElement rootMeta))
                {
                    progressToken = ParseProgressToken(rootMeta);
                }
                if (progressToken == null)
                {
                    progressToken = ParseProgressToken(rootDoc.RootElement);
                }
            }
            catch { }
        }

        return progressToken;
    }

    private static object? ParseProgressToken(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string[] tokenNames = ["progressToken", "progress_token", "ProgressToken"];
        foreach (var name in tokenNames)
        {
            if (element.TryGetProperty(name, out JsonElement ptEl))
            {
                var val = ParseProgressTokenValue(ptEl);
                if (val != null) return val;
            }
        }
        return null;
    }

    private static object? ParseProgressTokenValue(JsonElement ptEl)
    {
        if (ptEl.ValueKind == JsonValueKind.Number)
        {
            if (ptEl.TryGetInt64(out long lVal))
                return lVal;
            if (ptEl.TryGetDouble(out double dVal))
                return dVal;
            return ptEl.GetRawText();
        }
        if (ptEl.ValueKind == JsonValueKind.String)
            return ptEl.GetString();
        return null;
    }

    private static string? ExtractAgentId(JsonRpcRequest request, string? rawLine, JsonElement argumentsElement)
    {
        // 1. Check explicit tool call arguments
        string[] argProps = ["agent_id", "agentId", "agentOwner", "agent_owner"];
        if (argumentsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argProps)
            {
                if (argumentsElement.TryGetProperty(prop, out JsonElement el) &&
                    el.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(el.GetString()))
                {
                    return el.GetString()!.Trim();
                }
            }
        }

        // 2. Check params._meta and params properties
        if (request.Params.HasValue && request.Params.Value.ValueKind == JsonValueKind.Object)
        {
            var p = request.Params.Value;
            if (TryGetMetaElement(p, out JsonElement meta))
            {
                var id = ParseAgentId(meta);
                if (id != null) return id;
            }
            var directId = ParseAgentId(p);
            if (directId != null) return directId;

            if (p.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
            {
                if (TryGetMetaElement(args, out JsonElement argMeta))
                {
                    var id = ParseAgentId(argMeta);
                    if (id != null) return id;
                }
                var argId = ParseAgentId(args);
                if (argId != null) return argId;
            }
        }

        // 3. Check JSON-RPC ExtensionData
        if (request.ExtensionData != null)
        {
            if (request.ExtensionData.TryGetValue("_meta", out JsonElement extMeta) && extMeta.ValueKind == JsonValueKind.Object)
            {
                var id = ParseAgentId(extMeta);
                if (id != null) return id;
            }
            foreach (var prop in argProps)
            {
                if (request.ExtensionData.TryGetValue(prop, out JsonElement extEl) &&
                    extEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(extEl.GetString()))
                {
                    return extEl.GetString()!.Trim();
                }
            }
        }

        // 4. Raw line / RawJson fallback inspection
        string? lineToInspect = !string.IsNullOrWhiteSpace(rawLine) ? rawLine : request.RawJson;
        if (!string.IsNullOrWhiteSpace(lineToInspect))
        {
            try
            {
                using var rootDoc = JsonDocument.Parse(lineToInspect);
                if (TryGetMetaElement(rootDoc.RootElement, out JsonElement rootMeta))
                {
                    var id = ParseAgentId(rootMeta);
                    if (id != null) return id;
                }
                var directId = ParseAgentId(rootDoc.RootElement);
                if (directId != null) return directId;
            }
            catch { }
        }

        return null;
    }

    private static string? ParseAgentId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string[] candidateProps =
        [
            "antigravity.google/conversation_id",
            "conversation_id",
            "conversationId",
            "agent_id",
            "agentId",
            "agentOwner",
            "agent_owner"
        ];

        foreach (var prop in candidateProps)
        {
            if (element.TryGetProperty(prop, out JsonElement val) &&
                val.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(val.GetString()))
            {
                return val.GetString()!.Trim();
            }
        }

        return null;
    }

    private static string ReadReplaySummary(string matchId)
    {
        if (string.Equals(matchId, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return ReadLatestReplaySummary();
        }

        try
        {
            string root = FindProjectRoot();
            string[] searchDirs =
            [
                Path.Combine(root, "replays"),
                Path.Combine(root, "games", "client", "replays"),
                Path.Combine(root, "userdata", "replays")
            ];

            FileInfo? matchedReplay = null;
            foreach (var dir in searchDirs)
            {
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, $"*{matchId}*.honreplay", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.Length > 0)
                        .OrderByDescending(fi => fi.LastWriteTimeUtc);
                    var candidate = files.FirstOrDefault();
                    if (candidate != null)
                    {
                        matchedReplay = candidate;
                        break;
                    }
                }
            }

            if (matchedReplay == null)
            {
                return JsonRpcSerializer.SerializeIndented(new
                {
                    status = "replay_not_found",
                    matchId,
                    message = $"No .honreplay file matching match ID '{matchId}' found.",
                    searchedDirectories = searchDirs
                });
            }

            string? manifestSnippet = null;
            try
            {
                using var zip = ZipFile.OpenRead(matchedReplay.FullName);
                var manifestEntry = zip.GetEntry("manifest.xml") ?? zip.GetEntry("replay.manifest");
                if (manifestEntry != null)
                {
                    using var ms = manifestEntry.Open();
                    using var sr = new StreamReader(ms);
                    manifestSnippet = sr.ReadToEnd();
                }
            }
            catch { }

            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "success",
                matchId,
                fileName = matchedReplay.Name,
                fullPath = matchedReplay.FullName,
                sizeBytes = matchedReplay.Length,
                lastModified = matchedReplay.LastWriteTimeUtc.ToString("o"),
                hasManifest = manifestSnippet != null,
                manifest = manifestSnippet
            });
        }
        catch (Exception ex)
        {
            return JsonRpcSerializer.SerializeIndented(new
            {
                status = "error",
                matchId,
                errorMessage = ex.Message
            });
        }
    }

    private static string TruncateLog(string text, int maxLength = 250)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "... [truncated]";
    }
}