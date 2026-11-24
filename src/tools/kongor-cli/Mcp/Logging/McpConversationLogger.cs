// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kongor.Cli.Mcp.Logging;

public interface IMcpConversationLogger : IAsyncDisposable
{
    void LogInbound(string rawJson, string? method, string? id, string? toolName, JsonElement? arguments, string? traceId = null, string? spanId = null);
    void LogOutbound(string rawJson, string? id, bool isError, double durationMs, string? traceId = null, string? spanId = null);
    Task FlushAsync();
}

/// <summary>
/// Persistent, non-blocking traffic and conversation logger for the MCP server.
/// Stores conversation history in 'logs/MCP Conversation History/' with automated 4-tier size management:
/// 1. Base64/Binary scrubbing: Replaces large image payloads with compact artifact references.
/// 2. Max File Size Cap: Rolls session files at 10 MB.
/// 3. Directory Quota: Hard 50 MB budget with FIFO pruning of oldest files.
/// 4. Retention Window: 14-day rolling retention.
/// </summary>
public sealed class McpConversationLogger : IMcpConversationLogger
{
    private readonly string _historyDir;
    private readonly TimeProvider _timeProvider;
    private readonly string _sessionId;
    private readonly Channel<string> _logChannel;
    private readonly Task _backgroundWorker;
    private readonly CancellationTokenSource _cts = new();

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB per file
    private const long MaxDirectoryBudgetBytes = 50 * 1024 * 1024; // 50 MB total directory budget
    private const int MaxRetentionDays = 14;

    private string _currentMdPath;
    private string _currentNdjsonPath;
    private int _sessionFilePart = 1;
    private readonly Lock _fileLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pendingFlushes = new();
    private readonly ConcurrentDictionary<string, string> _requestToolNames = new(StringComparer.Ordinal);

    private static string ResolveOrigin() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KONGOR_MCP_ORIGIN"))
            ? Environment.GetEnvironmentVariable("KONGOR_MCP_ORIGIN")!
            : (Handlers.GameServerManageHandler.IsTestExecution ? "test" : "live");

    public McpConversationLogger(string? historyDirectory = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _historyDir = historyDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs", "MCP Conversation History");

        try
        {
            Directory.CreateDirectory(_historyDir);
            PruneStaleLogsAndEnforceBudget();
        }
        catch { }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _sessionId = now.ToString("yyyyMMdd_HHmmss");
        _currentMdPath = Path.Combine(_historyDir, $"session_{_sessionId}.md");
        _currentNdjsonPath = Path.Combine(_historyDir, $"mcp_traffic_{now:yyyy-MM-dd}.ndjson");

        InitializeSessionMarkdownHeader();

        _logChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _backgroundWorker = Task.Run(ProcessLogQueueAsync);
    }

    private void InitializeSessionMarkdownHeader()
    {
        try
        {
            if (!File.Exists(_currentMdPath))
            {
                StringBuilder sb = new();
                sb.AppendLine($"# MCP Conversation Session: {_sessionId}");
                sb.AppendLine($"**Started (UTC)**: {_timeProvider.GetUtcNow():yyyy-MM-dd HH:mm:ss.fffZ}");
                sb.AppendLine($"**Directory**: `{_historyDir}`");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                File.WriteAllText(_currentMdPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { }
    }

    public void LogInbound(string rawJson, string? method, string? id, string? toolName, JsonElement? arguments, string? traceId = null, string? spanId = null)
    {
        string scrubbedJson = ScrubHeavyPayloads(rawJson);
        string scrubbedArgs = arguments.HasValue ? ScrubHeavyPayloads(arguments.Value.GetRawText()) : "{}";
        DateTimeOffset timestamp = _timeProvider.GetUtcNow();

        string? resolvedTraceId = traceId;
        string? resolvedSpanId = spanId;
        if (resolvedTraceId == null && System.Diagnostics.Activity.Current != null && System.Diagnostics.Activity.Current.TraceId != default)
        {
            resolvedTraceId = System.Diagnostics.Activity.Current.TraceId.ToHexString();
        }
        if (resolvedSpanId == null && System.Diagnostics.Activity.Current != null && System.Diagnostics.Activity.Current.SpanId != default)
        {
            resolvedSpanId = System.Diagnostics.Activity.Current.SpanId.ToHexString();
        }

        if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(toolName))
        {
            _requestToolNames[id] = toolName!;
        }

        // 1. Structured NDJSON Record
        string origin = ResolveOrigin();
        string ndjsonLine = JsonSerializer.Serialize(new
        {
            type = "inbound_request",
            origin,
            timestamp = timestamp.ToString("o"),
            id,
            method,
            toolName,
            targetTool = toolName,
            traceId = resolvedTraceId,
            spanId = resolvedSpanId,
            payload = JsonNode.Parse(scrubbedJson)
        });

        // 2. Human-Readable Markdown
        StringBuilder md = new();
        md.AppendLine($"### [{timestamp:HH:mm:ss.fff}] --> INBOUND REQUEST (ID: `{id ?? "notification"}`)");
        string infoLine = $"* **Method**: `{method ?? "unknown"}`" + (string.IsNullOrWhiteSpace(toolName) ? "" : $" | **Tool**: `{toolName}`");
        if (!string.IsNullOrEmpty(resolvedTraceId))
        {
            infoLine += $" | **TraceId**: `{resolvedTraceId}`";
        }
        if (!string.IsNullOrEmpty(resolvedSpanId))
        {
            infoLine += $" | **SpanId**: `{resolvedSpanId}`";
        }
        md.AppendLine(infoLine);
        md.AppendLine("```json");
        md.AppendLine(FormatJsonIndented(scrubbedArgs));
        md.AppendLine("```");
        md.AppendLine();

        QueueWrite(ndjsonLine, md.ToString());
    }

    public void LogOutbound(string rawJson, string? id, bool isError, double durationMs, string? traceId = null, string? spanId = null)
    {
        string scrubbedJson = ScrubHeavyPayloads(rawJson);
        DateTimeOffset timestamp = _timeProvider.GetUtcNow();

        string? resolvedTraceId = traceId;
        string? resolvedSpanId = spanId;
        if (resolvedTraceId == null && System.Diagnostics.Activity.Current != null && System.Diagnostics.Activity.Current.TraceId != default)
        {
            resolvedTraceId = System.Diagnostics.Activity.Current.TraceId.ToHexString();
        }
        if (resolvedSpanId == null && System.Diagnostics.Activity.Current != null && System.Diagnostics.Activity.Current.SpanId != default)
        {
            resolvedSpanId = System.Diagnostics.Activity.Current.SpanId.ToHexString();
        }

        string? correlatedTool = null;
        if (!string.IsNullOrEmpty(id))
        {
            _requestToolNames.TryRemove(id, out correlatedTool);
        }

        // 1. Structured NDJSON Record
        string origin = ResolveOrigin();
        string ndjsonLine = JsonSerializer.Serialize(new
        {
            type = "outbound_response",
            origin,
            timestamp = timestamp.ToString("o"),
            id,
            targetTool = correlatedTool,
            isError,
            durationMs,
            traceId = resolvedTraceId,
            spanId = resolvedSpanId,
            payload = JsonNode.Parse(scrubbedJson)
        });

        // 2. Human-Readable Markdown
        StringBuilder md = new();
        string statusBadge = isError ? "[ERROR]" : "[SUCCESS]";
        md.AppendLine($"### [{timestamp:HH:mm:ss.fff}] <-- OUTBOUND RESPONSE (ID: `{id ?? "unknown"}`) {statusBadge} ({durationMs:F1} ms)");
        if (!string.IsNullOrEmpty(resolvedTraceId) || !string.IsNullOrEmpty(resolvedSpanId))
        {
            md.AppendLine($"* **TraceId**: `{resolvedTraceId ?? "none"}` | **SpanId**: `{resolvedSpanId ?? "none"}`");
        }
        md.AppendLine("```json");
        md.AppendLine(FormatJsonIndented(scrubbedJson));
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();

        QueueWrite(ndjsonLine, md.ToString());
    }

    private void QueueWrite(string ndjsonLine, string mdContent)
    {
        KongorTraceabilityMarks.NotifyLog(ndjsonLine);
        KongorTraceabilityMarks.NotifyLog(mdContent);
        _logChannel.Writer.TryWrite($"NDJSON|{ndjsonLine}");
        _logChannel.Writer.TryWrite($"MD|{mdContent}");
    }

    private async Task ProcessLogQueueAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (await _logChannel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_logChannel.Reader.TryRead(out string? item))
                    {
                        if (string.IsNullOrEmpty(item)) continue;

                        lock (_fileLock)
                        {
                            CheckAndRotateSessionFile();

                            if (item.StartsWith("NDJSON|"))
                            {
                                File.AppendAllText(_currentNdjsonPath, item[7..] + Environment.NewLine, Encoding.UTF8);
                            }
                            else if (item.StartsWith("MD|"))
                            {
                                File.AppendAllText(_currentMdPath, item[3..], Encoding.UTF8);
                            }
                            else if (item.StartsWith("FLUSH|"))
                            {
                                string flushId = item[6..];
                                if (_pendingFlushes.TryRemove(flushId, out TaskCompletionSource? tcs))
                                {
                                    tcs.TrySetResult();
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    private void CheckAndRotateSessionFile()
    {
        try
        {
            FileInfo fi = new(_currentMdPath);
            if (fi.Exists && fi.Length > MaxFileSizeBytes)
            {
                _sessionFilePart++;
                _currentMdPath = Path.Combine(_historyDir, $"session_{_sessionId}_part{_sessionFilePart}.md");
                File.WriteAllText(_currentMdPath, $"# Session {_sessionId} (Part {_sessionFilePart})\n\n---\n\n", Encoding.UTF8);
            }
        }
        catch { }
    }

    private void PruneStaleLogsAndEnforceBudget()
    {
        try
        {
            DirectoryInfo dir = new(_historyDir);
            if (!dir.Exists) return;

            FileInfo[] files = dir.GetFiles();
            DateTime cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-MaxRetentionDays);

            // 1. Delete older than retention window
            foreach (FileInfo f in files)
            {
                if (f.LastWriteTimeUtc < cutoff)
                {
                    try { f.Delete(); } catch { }
                }
            }

            // 2. Enforce total directory budget
            files = dir.GetFiles();
            long totalSize = files.Sum(f => f.Length);

            if (totalSize > MaxDirectoryBudgetBytes)
            {
                List<FileInfo> sorted = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
                foreach (FileInfo f in sorted)
                {
                    if (totalSize <= MaxDirectoryBudgetBytes) break;
                    long len = f.Length;
                    try
                    {
                        f.Delete();
                        totalSize -= len;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Detects multi-megabyte Base64 strings (e.g. screenshots) and replaces them with a lightweight summary.
    /// Preserves 100% of command arguments, error messages, and structured metadata.
    /// </summary>
    public static string ScrubHeavyPayloads(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText) || jsonText.Length < 1024)
        {
            return jsonText;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            JsonNode? node = JsonNode.Parse(jsonText);
            if (node == null) return jsonText;

            ScrubNode(node);
            return node.ToJsonString();
        }
        catch
        {
            return jsonText;
        }
    }

    private static void ScrubNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            List<string> keys = obj.Select(kv => kv.Key).ToList();
            foreach (string key in keys)
            {
                JsonNode? val = obj[key];
                if (val is JsonValue jv && jv.TryGetValue<string>(out string? strVal))
                {
                    if (IsHeavyBase64String(key, strVal))
                    {
                        int kb = strVal.Length / 1024;
                        obj[key] = $"[Base64 Image Data: ~{kb} KB (Scrubbed to conserve log budget)]";
                    }
                }
                else if (val != null)
                {
                    ScrubNode(val);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (item != null) ScrubNode(item);
            }
        }
    }

    private static bool IsHeavyBase64String(string keyName, string value)
    {
        if (value.Length < 4096) return false;

        string lowerKey = keyName.ToLowerInvariant();
        if (lowerKey.Contains("base64") || lowerKey.Contains("image") || lowerKey.Contains("screenshot"))
        {
            return true;
        }

        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith("iVBORw0KGgo", StringComparison.Ordinal)) // PNG header in Base64
        {
            return true;
        }

        return false;
    }

    private static string FormatJsonIndented(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    public async Task FlushAsync()
    {
        string flushId = Guid.NewGuid().ToString("N");
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFlushes[flushId] = tcs;

        if (_logChannel.Writer.TryWrite($"FLUSH|{flushId}"))
        {
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(5));
            using CancellationTokenRegistration reg = timeoutCts.Token.Register(() => tcs.TrySetResult());
            await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            tcs.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        _logChannel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _backgroundWorker.ConfigureAwait(false);
        }
        catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Wave 4 (#231): TigerBeetle-inspired log traceability assertion gate (`src/testing/marks.zig`, `marks.check(log_pattern)`).
/// Allows tests to arm an expected diagnostic/fallback log pattern (`ExpectLogPattern(string pattern)`), record log emissions,
/// and assert via `VerifyHit()` / `Dispose()` that the expected log branch was actually executed rather than dead-coded.
/// </summary>
public sealed class KongorTraceabilityMarks : IDisposable
{
    private static readonly ConcurrentDictionary<Guid, KongorTraceabilityMarks> s_activeMarks = new();

    private readonly Guid _id = Guid.NewGuid();
    private readonly string _expectedPattern;
    private readonly bool _enforceOnDispose;
    private int _hitCount;
    private bool _disposed;
    private bool _failedVerification;

    public string ExpectedPattern => _expectedPattern;
    public int HitCount => Volatile.Read(ref _hitCount);
    public bool IsHit => HitCount > 0;

    public KongorTraceabilityMarks(string expectedPattern, bool enforceOnDispose = true)
    {
        if (string.IsNullOrWhiteSpace(expectedPattern))
        {
            throw new ArgumentException("[KongorTraceabilityMarks] Expected log pattern cannot be null or whitespace.", nameof(expectedPattern));
        }

        _expectedPattern = expectedPattern;
        _enforceOnDispose = enforceOnDispose;
        s_activeMarks[_id] = this;
    }

    public static KongorTraceabilityMarks ExpectLogPattern(string pattern, bool enforceOnDispose = true)
    {
        return new KongorTraceabilityMarks(pattern, enforceOnDispose);
    }

    public static void NotifyLog(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        foreach (KeyValuePair<Guid, KongorTraceabilityMarks> kvp in s_activeMarks)
        {
            kvp.Value.Observe(message);
        }
    }

    public bool Observe(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        if (message.Contains(_expectedPattern, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _hitCount);
            return true;
        }

        return false;
    }

    public void VerifyHit()
    {
        if (HitCount <= 0)
        {
            _failedVerification = true;
            throw new InvalidOperationException(
                $"[KongorTraceabilityMarks] Expected diagnostic log pattern '{_expectedPattern}' was never reached (0 hits). " +
                "Verify that the fallback/error branch was not dead-coded or bypassed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        s_activeMarks.TryRemove(_id, out _);

        if (_enforceOnDispose && !_failedVerification)
        {
            VerifyHit();
        }
    }
}

