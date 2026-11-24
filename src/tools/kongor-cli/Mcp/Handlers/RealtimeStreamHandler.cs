// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Log or trace entry captured for real-time streaming.
/// </summary>
public sealed class RealtimeStreamEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = "INFO";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// MCP Tool Handler for 'kongor_realtime_stream' (aliases: 'realtime_telemetry_stream', 'kongor_log_tail', 'kongor_stream').
/// Streams rolling logs, OpenTelemetry trace activities, and active session telemetry.
/// </summary>
public sealed class RealtimeStreamHandler
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<RealtimeStreamEntry>> s_streamBuffers = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _logsRoot;

    public RealtimeStreamHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? logsRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _logsRoot = logsRoot ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
    }

    private void Log(string message) => _logger?.Invoke($"[RealtimeStream] {message}");

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "tail";
        string streamType = "logs";

        if (arguments.TryGetProperty("streamType", out JsonElement stElem) && !string.IsNullOrWhiteSpace(stElem.GetString()))
        {
            streamType = stElem.GetString()!.Trim().ToLowerInvariant();
            action = streamType switch
            {
                "logs" => "tail",
                "metrics" => "metrics",
                "traces" => "traces",
                "sessions" => "sessions",
                _ => streamType
            };
        }
        else if (arguments.TryGetProperty("action", out JsonElement aElem) && !string.IsNullOrWhiteSpace(aElem.GetString()))
        {
            action = aElem.GetString()!.Trim().ToLowerInvariant();
            streamType = action switch
            {
                "tail" or "logs" => "logs",
                "metrics" => "metrics",
                "traces" => "traces",
                "sessions" => "sessions",
                _ => action
            };
        }

        string source = "all";
        if (arguments.TryGetProperty("source", out JsonElement sElem) && !string.IsNullOrWhiteSpace(sElem.GetString()))
        {
            source = sElem.GetString()!.Trim().ToLowerInvariant();
        }

        int maxLines = 50;
        if ((arguments.TryGetProperty("maxLines", out JsonElement mElem) && mElem.TryGetInt32(out int ml) && ml > 0) ||
            (arguments.TryGetProperty("maxEvents", out JsonElement meElem) && meElem.TryGetInt32(out ml) && ml > 0))
        {
            maxLines = Math.Clamp(ml, 1, 500);
        }

        string? filter = null;
        if (arguments.TryGetProperty("filter", out JsonElement fElem) && !string.IsNullOrWhiteSpace(fElem.GetString()))
        {
            filter = fElem.GetString()!.Trim();
        }

        Log($"Handling stream action '{action}' (streamType '{streamType}'), source: '{source}', maxLines: {maxLines}, filter: '{filter}'...");

        return action switch
        {
            "tail" or "logs" => await HandleTailAsync(source, maxLines, filter, arguments, cancellationToken).ConfigureAwait(false),
            "metrics" => HandleMetrics(arguments),
            "traces" => HandleTraces(maxLines, filter, arguments),
            "sessions" => HandleSessions(arguments),
            "subscribe" or "start" => HandleSubscribe(source),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Valid actions are: tail, logs, metrics, traces, sessions, subscribe.")
        };
    }

    private async Task<ToolCallResult> HandleTailAsync(string source, int maxLines, string? filter, JsonElement arguments, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<RealtimeStreamEntry> entries = [];
        bool fromFiles = false;

        // Check if logs directory exists
        if (Directory.Exists(_logsRoot))
        {
            try
            {
                IEnumerable<string> files = Directory.EnumerateFiles(_logsRoot, "*.log", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(_logsRoot, "*.ndjson", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(10);

                foreach (string file in files)
                {
                    string fileSource = Path.GetFileName(file);
                    if (source != "all" && !fileSource.Contains(source, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length > 65536)
                    {
                        fs.Seek(-65536, SeekOrigin.End);
                    }
                    using StreamReader reader = new(fs);
                    // If we seeked into the middle of the file, discard partial first line
                    if (fs.Position > 0)
                    {
                        await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    }
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(filter) && !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        entries.Add(new RealtimeStreamEntry
                        {
                            Timestamp = _timeProvider.GetUtcNow(),
                            Source = fileSource,
                            Level = line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase) ? "ERROR" : "INFO",
                            Message = line
                        });
                    }
                }

                if (entries.Count > 0)
                {
                    fromFiles = true;
                }
            }
            catch (Exception ex)
            {
                Log($"Log tailing warning: {ex.Message}");
            }
        }

        // If no file entries found, require simulation allowance
        if (entries.Count == 0)
        {
            if (!IsSimulationAllowed(arguments))
            {
                return ToolCallResult.Error(
                    $"No log files found in '{_logsRoot}' for source '{source}'. Real-time log stream cannot tail offline or empty logs. " +
                    $"Launch the client or server to generate live logs, or specify a valid logs path. " +
                    $"To allow simulated test double output, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
            }

            entries.Add(new RealtimeStreamEntry
            {
                Timestamp = _timeProvider.GetUtcNow(),
                Source = source,
                Level = "INFO",
                Message = $"[RealtimeStream] Tail connection active for source '{source}'. Ready for streaming events."
            });
        }

        List<RealtimeStreamEntry> resultEntries = entries.TakeLast(maxLines).ToList();
        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = fromFiles ? ExecutionFidelity.LiveVerified.ToString() : ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "tail",
            streamType = "logs",
            source,
            count = resultEntries.Count,
            eventCount = resultEntries.Count,
            entries = resultEntries,
            executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleMetrics(JsonElement arguments)
    {
        if (!IsSimulationAllowed(arguments))
        {
            return ToolCallResult.Error(
                "Live host telemetry stream is offline. Cannot query process CPU/memory metrics without an active target. " +
                "To allow simulated test double metrics, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
        }

        object[] metrics =
        [
            new { name = "kongor.cpu.utilization", value = 12.4, unit = "%" },
            new { name = "kongor.memory.working_set_mb", value = 248.5, unit = "MB" },
            new { name = "kongor.active_sessions", value = 14.0, unit = "count" },
            new { name = "kongor.matches_in_progress", value = 2.0, unit = "count" }
        ];

        return ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "metrics",
            streamType = "metrics",
            count = metrics.Length,
            eventCount = metrics.Length,
            metrics,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleTraces(int maxLines, string? filter, JsonElement arguments)
    {
        List<object> traces = [];
        Activity? current = Activity.Current;

        while (current != null && traces.Count < maxLines)
        {
            if (string.IsNullOrWhiteSpace(filter) || current.OperationName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                traces.Add(new
                {
                    id = current.Id,
                    operationName = current.OperationName,
                    durationMs = current.Duration.TotalMilliseconds,
                    startTimeUtc = current.StartTimeUtc,
                    tags = current.Tags.ToDictionary(t => t.Key, t => t.Value)
                });
            }
            current = current.Parent;
        }

        bool isLiveTraces = traces.Count > 0;
        if (traces.Count == 0)
        {
            if (!IsSimulationAllowed(arguments))
            {
                return ToolCallResult.Error(
                    "No active OpenTelemetry traces captured in current process. Cannot stream live activity traces. " +
                    "To allow simulated test double traces, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
            }

            traces.Add(new
            {
                id = ActivityTraceId.CreateRandom().ToHexString(),
                operationName = "Mcp.RealtimeStream.Probe",
                durationMs = 0.42,
                startTimeUtc = _timeProvider.GetUtcNow(),
                tags = new Dictionary<string, string> { ["mcp.tool"] = "kongor_realtime_stream" }
            });
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = isLiveTraces ? ExecutionFidelity.LiveVerified.ToString() : ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "traces",
            streamType = "traces",
            count = traces.Count,
            eventCount = traces.Count,
            traces,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult HandleSessions(JsonElement arguments)
    {
        bool masterListening = false;
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            masterListening = listeners.Any(ep => ep.Port == 55555);
        }
        catch
        {
            masterListening = false;
        }

        if (masterListening)
        {
            return ToolCallResult.Json(new
            {
                status = "success",
                executionFidelity = ExecutionFidelity.LiveVerified.ToString(),
                action = "sessions",
                streamType = "sessions",
                count = 1,
                eventCount = 1,
                activeSessionsCount = 1,
                sessions = new[]
                {
                    new
                    {
                        sessionId = "sess_master_55555",
                        service = "MasterServer",
                        endpoint = "127.0.0.1:55555",
                        status = "listening"
                    }
                },
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (!IsSimulationAllowed(arguments))
        {
            return ToolCallResult.Error(
                "Master server session stream is offline. Cannot stream active sessions. " +
                "Run Aspire cluster or 'kongor_server_manage action: 'start' target: 'cluster''. " +
                "To allow simulated test double sessions, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "sessions",
            streamType = "sessions",
            count = 1,
            eventCount = 1,
            activeSessionsCount = 1,
            sessions = new[]
            {
                new
                {
                    sessionId = "sess_master_1",
                    service = "MasterServer",
                    connectedClients = 10,
                    status = "healthy"
                }
            },
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private static bool IsSimulationAllowed(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("allowSimulation", out var simElem) &&
            (simElem.ValueKind == JsonValueKind.True || (simElem.ValueKind == JsonValueKind.String && bool.TryParse(simElem.GetString(), out var b) && b)))
        {
            return true;
        }

        return Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS") == "1";
    }

    private ToolCallResult HandleSubscribe(string source)
    {
        string subscriptionId = $"sub_{source}_{Guid.NewGuid().ToString("N")[..8]}";
        s_streamBuffers.TryAdd(subscriptionId, new ConcurrentQueue<RealtimeStreamEntry>());

        Log($"Created real-time stream subscription '{subscriptionId}' for '{source}'");

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "subscribe",
            subscriptionId,
            source,
            streamUri = $"kongor://stream/{subscriptionId}",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }
}

/// <summary>
/// Wave 4 (#230): TigerBeetle ID ordering schemes (`src/testing/id.zig`).
/// </summary>
public enum IdOrder
{
    Sequential = 0,
    Random = 1,
    Reversed = 2,
    Permuted = 3,
    Identity = 4,
    Inversion = 5,
    ZigZag = 6
}

/// <summary>
/// Wave 4 (#230): TigerBeetle-inspired reversible 128-bit ID permutator (`IdPermutation`)
/// using 128-bit `InversionMask` and `ZigZagMask` (alternating `0xAA55...` bits) to defeat
/// B-tree/dictionary sequential insertion bias while remaining 100% deterministically reversible.
/// </summary>
public readonly struct Reversible128BitIdPermutator
{
    public static readonly UInt128 DefaultZigZagMask = new(0xAAAA_5555_AAAA_5555UL, 0x5555_AAAA_5555_AAAAUL);
    private static readonly UInt128 s_oddMultiplier = new(0x9E3779B97F4A7C15UL, 0xD6E8FEB86659FD93UL);
    private static readonly UInt128 s_oddMultiplierInverse = ComputeInverse128(s_oddMultiplier);

    public IdOrder Order { get; }
    public UInt128 InversionMask { get; }
    public UInt128 ZigZagMask { get; }

    public Reversible128BitIdPermutator(
        IdOrder order,
        UInt128? inversionMask = null,
        UInt128? zigZagMask = null)
    {
        Order = order;
        InversionMask = inversionMask ?? new UInt128(0xF0F0_0F0F_F0F0_0F0FUL, 0x0F0F_F0F0_0F0F_F0F0UL);
        ZigZagMask = zigZagMask ?? DefaultZigZagMask;
    }

    public static Reversible128BitIdPermutator FromSeed(IdOrder order, ulong seed)
    {
        ulong s0 = seed ^ 0x9E3779B97F4A7C15UL;
        ulong s1 = ~seed ^ 0xBF58476D1CE4E5B9UL;
        return new Reversible128BitIdPermutator(
            order,
            new UInt128(s0, s1),
            DefaultZigZagMask);
    }

    public UInt128 Encode(UInt128 id)
    {
        switch (Order)
        {
            case IdOrder.Sequential:
            case IdOrder.Identity:
                return id;

            case IdOrder.Reversed:
            case IdOrder.Inversion:
                return ~id;

            case IdOrder.ZigZag:
                return ReverseBits128(id) ^ ZigZagMask;

            case IdOrder.Permuted:
            {
                UInt128 inverted = id ^ InversionMask;
                UInt128 bitReversed = ReverseBits128(inverted);
                return bitReversed ^ ZigZagMask;
            }

            case IdOrder.Random:
            {
                UInt128 x = id ^ InversionMask;
                x ^= x >> 64;
                x = unchecked(x * s_oddMultiplier);
                x ^= x >> 64;
                return x ^ ZigZagMask;
            }

            default:
                return id;
        }
    }

    public UInt128 Decode(UInt128 encoded)
    {
        switch (Order)
        {
            case IdOrder.Sequential:
            case IdOrder.Identity:
                return encoded;

            case IdOrder.Reversed:
            case IdOrder.Inversion:
                return ~encoded;

            case IdOrder.ZigZag:
                return ReverseBits128(encoded ^ ZigZagMask);

            case IdOrder.Permuted:
            {
                UInt128 unzig = encoded ^ ZigZagMask;
                UInt128 unreversed = ReverseBits128(unzig);
                return unreversed ^ InversionMask;
            }

            case IdOrder.Random:
            {
                UInt128 x = encoded ^ ZigZagMask;
                x ^= x >> 64;
                x = unchecked(x * s_oddMultiplierInverse);
                x ^= x >> 64;
                return x ^ InversionMask;
            }

            default:
                return encoded;
        }
    }

    public static UInt128 ReverseBits128(UInt128 value)
    {
        ulong lo = (ulong)value;
        ulong hi = (ulong)(value >> 64);
        return new UInt128(ReverseBits64(lo), ReverseBits64(hi));
    }

    public static ulong ReverseBits64(ulong x)
    {
        x = ((x >> 1) & 0x5555555555555555UL) | ((x & 0x5555555555555555UL) << 1);
        x = ((x >> 2) & 0x3333333333333333UL) | ((x & 0x3333333333333333UL) << 2);
        x = ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((x & 0x0F0F0F0F0F0F0F0FUL) << 4);
        return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x);
    }

    private static UInt128 ComputeInverse128(UInt128 a)
    {
        UInt128 x = UInt128.One;
        UInt128 two = new(0UL, 2UL);
        for (int i = 0; i < 7; i++)
        {
            x = unchecked(x * (two - unchecked(a * x)));
        }
        return x;
    }
}

