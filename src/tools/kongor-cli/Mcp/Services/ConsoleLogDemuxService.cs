// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ConsoleLogDemuxService.cs
// System: Kongor MCP Server - High-Throughput Log Demultiplexer & Mailbox IPC
// Purpose: Implements a thread-safe sequential console.log tail reader and
//          response demultiplexer. Eliminates _sendLock polling contention so
//          multiple concurrent Mailbox IPC callers share a single FileShare.ReadWrite
//          stream without holding locks across waiting loops or missing lines.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Thread-safe sequential tail reader and response demultiplexer for K2 console.log.
/// Dispatches assembled chunked [NEXUS_MAILBOX_RES] frames and legacy correlated frames
/// to awaiting TaskCompletionSources without holding send locks or spawning redundant file readers.
/// </summary>
public sealed class ConsoleLogDemuxService : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, ConsoleLogDemuxService> SharedInstances = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _consoleLogPath;
    private readonly Action<string>? _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> _responseChunks = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _cts = new();
    private readonly object _startLock = new();
    private Task? _tailTask;
    private bool _isDisposed;

    public string ConsoleLogPath => _consoleLogPath;
    public int PendingRequestsCount => _pendingRequests.Count;

    public event Action<string, string>? OnResponseReceived;
    public event Action<string>? OnAckReceived;
    public event Action<string>? OnRawLineReceived;

    public ConsoleLogDemuxService(string consoleLogPath, Action<string>? logger = null)
    {
        _consoleLogPath = Path.GetFullPath(consoleLogPath);
        _logger = logger;
    }

    public static ConsoleLogDemuxService GetOrCreate(string consoleLogPath, Action<string>? logger = null)
    {
        string fullPath = Path.GetFullPath(consoleLogPath);
        return SharedInstances.GetOrAdd(fullPath, path => new ConsoleLogDemuxService(path, logger));
    }

    public Task<string> RegisterPending(string reqId)
    {
        EnsureReaderStarted();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[reqId] = tcs;
        return tcs.Task;
    }

    public void UnregisterPending(string reqId)
    {
        _pendingRequests.TryRemove(reqId, out _);
        _responseChunks.TryRemove(reqId, out _);
    }

    public async Task<string> WaitForResponseAsync(string reqId, int timeoutMs, CancellationToken cancellationToken = default)
    {
        Task<string> responseTask = RegisterPending(reqId);
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await responseTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ConsoleLogDemuxService timed out after {timeoutMs}ms waiting for response to request #{reqId}.");
        }
        finally
        {
            UnregisterPending(reqId);
        }
    }

    public void EnsureReaderStarted()
    {
        if (_tailTask != null || _isDisposed) return;
        lock (_startLock)
        {
            if (_tailTask != null || _isDisposed) return;
            _tailTask = Task.Run(() => RunTailLoopAsync(_cts.Token));
        }
    }

    private async Task RunTailLoopAsync(CancellationToken ct)
    {
        FileStream? stream = null;
        StreamReader? reader = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (stream == null || reader == null)
                {
                    if (!File.Exists(_consoleLogPath))
                    {
                        await Task.Delay(15, ct).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        Encoding enc = DetectLogEncoding(_consoleLogPath);
                        stream = new FileStream(_consoleLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        long endPos = stream.Length;
                        if (enc == Encoding.Unicode && (endPos % 2) != 0) endPos--;
                        long backPos = Math.Max(0, endPos - 4096);
                        if (enc == Encoding.Unicode && (backPos % 2) != 0) backPos++;
                        stream.Seek(backPos, SeekOrigin.Begin);
                        reader = new StreamReader(stream, enc);
                        _logger?.Invoke($"[ConsoleLogDemuxService] Opened tail reader on '{_consoleLogPath}' at offset {backPos}/{endPos} (enc: {enc.EncodingName})");
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"[ConsoleLogDemuxService] Transient open error for '{_consoleLogPath}': {ex.Message}");
                        await Task.Delay(15, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                string? line = null;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Debug.WriteLine($"[ConsoleLogDemuxService] Transient readline error on '{_consoleLogPath}': {ex.Message}");
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                if (line != null)
                {
                    ProcessLine(line);
                }
                else
                {
                    // Check for file truncation or restart
                    if (stream.Length < stream.Position)
                    {
                        _logger?.Invoke($"[ConsoleLogDemuxService] File truncation detected on '{_consoleLogPath}', resetting reader.");
                        reader.Dispose();
                        await stream.DisposeAsync().ConfigureAwait(false);
                        stream = null;
                        reader = null;
                        continue;
                    }

                    await Task.Delay(2, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"[ConsoleLogDemuxService] Normal cancellation shutdown on '{_consoleLogPath}': {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[ConsoleLogDemuxService] Exception in tail reader loop: {ex}");
        }
        finally
        {
            reader?.Dispose();
            if (stream != null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        OnRawLineReceived?.Invoke(line);

        int ackIdx = line.IndexOf("[NEXUS_MAILBOX_ACK] ", StringComparison.Ordinal);
        if (ackIdx >= 0)
        {
            string ackId = line.Substring(ackIdx + "[NEXUS_MAILBOX_ACK] ".Length).Trim();
            _logger?.Invoke($"[ConsoleLogDemuxService] In-engine ACK received for request '{ackId}'");
            OnAckReceived?.Invoke(ackId);
            return;
        }

        int idx = line.IndexOf("[NEXUS_MAILBOX_RES] ", StringComparison.Ordinal);
        if (idx < 0) return;

        string resPayload = line.Substring(idx + "[NEXUS_MAILBOX_RES] ".Length).TrimEnd('\r', '\n');
        int firstSpace = resPayload.IndexOf(' ');
        if (firstSpace > 0)
        {
            string lineId = resPayload.Substring(0, firstSpace).Trim();
            int secondSpace = resPayload.IndexOf(' ', firstSpace + 1);
            if (secondSpace > 0)
            {
                string chunkInfo = resPayload.Substring(firstSpace + 1, secondSpace - firstSpace - 1).Trim();
                string chunkData = resPayload.Substring(secondSpace + 1);
                int slashIdx = chunkInfo.IndexOf('/');
                if (slashIdx > 0 &&
                    int.TryParse(chunkInfo.AsSpan(0, slashIdx), out int seq) &&
                    int.TryParse(chunkInfo.AsSpan(slashIdx + 1), out int total))
                {
                    if (total <= 1)
                    {
                        CompleteResponse(lineId, chunkData);
                        return;
                    }

                    var chunkMap = _responseChunks.GetOrAdd(lineId, _ => new ConcurrentDictionary<int, string>());
                    chunkMap[seq] = chunkData;

                    if (chunkMap.Count == total)
                    {
                        _responseChunks.TryRemove(lineId, out _);
                        IEnumerable<string> orderedChunks = chunkMap.OrderBy(kv => kv.Key).Select(kv => kv.Value);
                        string fullJson = string.Concat(orderedChunks);
                        CompleteResponse(lineId, fullJson);
                    }
                    return;
                }
            }

            // Fallback: unchunked with reqId (e.g. "[NEXUS_MAILBOX_RES] 123 {json}")
            string remaining = resPayload.Substring(firstSpace + 1).TrimStart();
            if (remaining.StartsWith('{') || remaining.StartsWith('['))
            {
                CompleteResponse(lineId, remaining);
                return;
            }
        }

        // Fallback: legacy without leading reqId (e.g. "[NEXUS_MAILBOX_RES] {json}")
        string trimmedPayload = resPayload.TrimStart();
        if (trimmedPayload.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmedPayload);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    string lineId = idProp.ToString();
                    CompleteResponse(lineId, trimmedPayload);
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[ConsoleLogDemuxService] Ignored non-JSON legacy mailbox line: {ex.Message}");
            }
        }
    }

    private void CompleteResponse(string reqId, string fullJson)
    {
        _logger?.Invoke($"[ConsoleLogDemuxService] Completed response for reqId='{reqId}' ({fullJson.Length} bytes)");
        if (_pendingRequests.TryRemove(reqId, out var tcs))
        {
            tcs.TrySetResult(fullJson);
        }
        OnResponseReceived?.Invoke(reqId, fullJson);
    }

    public static Encoding DetectLogEncoding(string filePath)
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
                if (read >= 2 && header[1] == 0x00 && header[0] != 0x00)
                {
                    return Encoding.Unicode;
                }
                return Utf8NoBom;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConsoleLogDemuxService] Ignored DetectLogEncoding error: {ex.Message}");
        }
        return Encoding.Unicode;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            Debug.WriteLine($"[ConsoleLogDemuxService] CTS already disposed during Cancel: {ex.Message}");
        }

        if (_tailTask != null)
        {
            _ = _tailTask.ContinueWith(_ =>
            {
                try { _cts.Dispose(); } catch (ObjectDisposedException ex) { Debug.WriteLine($"[ConsoleLogDemuxService] CTS already disposed: {ex.Message}"); }
            }, TaskScheduler.Default);
        }
        else
        {
            _cts.Dispose();
        }

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();
        _responseChunks.Clear();

        SharedInstances.TryRemove(_consoleLogPath, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        if (_tailTask != null)
        {
            try
            {
                await _tailTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                Debug.WriteLine($"[ConsoleLogDemuxService] Tail loop canceled cleanly: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ConsoleLogDemuxService] Tail loop shutdown notice: {ex.Message}");
            }
        }

        _cts.Dispose();

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();
        _responseChunks.Clear();

        SharedInstances.TryRemove(_consoleLogPath, out _);
    }
}
