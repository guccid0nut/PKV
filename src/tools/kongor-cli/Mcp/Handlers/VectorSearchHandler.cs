// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VectorSearchHandler.cs
// System: Kongor.Cli.Mcp.Handlers
// Description: MCP Tool Handler for 'kongor_vector_search' (native Garnet vector operations).
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using ASPIRE.Common.Caching.Vectors;
using Kongor.Cli.Mcp.Protocol;
using StackExchange.Redis;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_vector_search'.
/// Exposes sub-millisecond vector similarity search, element ingestion, and index inspection
/// directly over Microsoft Garnet Vector Sets (VADD, VSIM, VREM, VINFO, VDIM).
/// </summary>
public sealed class VectorSearchHandler
{
    private readonly Lazy<(IGarnetVectorClient Client, bool IsLive)> _lazyClient;
    private readonly Action<string> _logger;

    private IGarnetVectorClient _vectorClient => _lazyClient.Value.Client;
    private bool _isLiveGarnet => _lazyClient.Value.IsLive;

    public VectorSearchHandler(
        IGarnetVectorClient? vectorClient = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _logger = logger ?? (msg => Console.Error.WriteLine($"[VectorSearch] {msg}"));

        if (vectorClient != null)
        {
            IGarnetVectorClient captured = vectorClient;
            bool isLive = captured is GarnetVectorClient;
            _lazyClient = new Lazy<(IGarnetVectorClient Client, bool IsLive)>(() => (captured, isLive));
        }
        else
        {
            // Defer ConnectionMultiplexer.Connect until kongor_vector_search is actually invoked
            // so McpToolRegistry startup never pays a 350-400ms Redis handshake tax on unrelated tools.
            _lazyClient = new Lazy<(IGarnetVectorClient Client, bool IsLive)>(
                InitializeClient,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "search";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string key = "vectors:default";
        if (arguments.TryGetProperty("key", out JsonElement keyElem) && !string.IsNullOrWhiteSpace(keyElem.GetString()))
        {
            key = keyElem.GetString()!.Trim();
        }

        _logger($"[HandleAsync] Action: '{action}', Key: '{key}', Mode: {(_isLiveGarnet ? "Live Garnet" : "In-Process")}");

        Stopwatch sw = Stopwatch.StartNew();

        switch (action)
        {
            case "add":
                return await HandleAddAsync(key, arguments, sw, cancellationToken).ConfigureAwait(false);

            case "search":
                return await HandleSearchAsync(key, arguments, sw, cancellationToken).ConfigureAwait(false);

            case "remove":
                return await HandleRemoveAsync(key, arguments, sw, cancellationToken).ConfigureAwait(false);

            case "info":
                return await HandleInfoAsync(key, sw, cancellationToken).ConfigureAwait(false);

            case "dimension":
            case "dim":
                return await HandleDimensionAsync(key, sw, cancellationToken).ConfigureAwait(false);

            case "list":
                return await HandleListAsync(arguments, sw, cancellationToken).ConfigureAwait(false);

            default:
                sw.Stop();
                return ToolCallResult.Error($"Unknown vector action '{action}'. Supported actions: 'search', 'add', 'remove', 'info', 'dimension', 'list'.");
        }
    }

    private async Task<ToolCallResult> HandleAddAsync(
        string key,
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!arguments.TryGetProperty("element", out JsonElement elemProperty) || string.IsNullOrWhiteSpace(elemProperty.GetString()))
        {
            return ToolCallResult.Error("Argument 'element' (unique string identifier) is required for 'add' action.");
        }
        string element = elemProperty.GetString()!.Trim();

        float[]? vector = ExtractVector(arguments);
        if (vector == null || vector.Length == 0)
        {
            return ToolCallResult.Error("Argument 'vector' (array of floats) is required for 'add' action.");
        }

        string? attributes = null;
        if (arguments.TryGetProperty("attributes", out JsonElement attrElem))
        {
            attributes = attrElem.ValueKind == JsonValueKind.String
                ? attrElem.GetString()
                : attrElem.GetRawText();
        }

        VectorMetric metric = VectorMetric.Cosine;
        if (arguments.TryGetProperty("metric", out JsonElement metricElem) && !string.IsNullOrWhiteSpace(metricElem.GetString()))
        {
            string mStr = metricElem.GetString()!.Trim().ToLowerInvariant();
            metric = mStr switch
            {
                "l2" or "euclidean" => VectorMetric.Euclidean,
                "ip" or "innerproduct" or "dot" => VectorMetric.InnerProduct,
                _ => VectorMetric.Cosine
            };
        }

        VectorQuantization quant = VectorQuantization.None;
        if (arguments.TryGetProperty("quantization", out JsonElement quantElem) && !string.IsNullOrWhiteSpace(quantElem.GetString()))
        {
            string qStr = quantElem.GetString()!.Trim().ToLowerInvariant();
            quant = qStr switch
            {
                "sq8" or "xpreq8" => VectorQuantization.SQ8,
                "pq" => VectorQuantization.PQ,
                _ => VectorQuantization.None
            };
        }

        VectorAddOptions options = new()
        {
            Metric = metric,
            Quantization = quant,
            Attributes = attributes
        };

        bool success = await _vectorClient.AddAsync(key, element, vector, options, ct).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Success(new
        {
            success,
            action = "add",
            key,
            element,
            dimensions = vector.Length,
            metric = metric.ToString(),
            quantization = quant.ToString(),
            mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private async Task<ToolCallResult> HandleSearchAsync(
        string key,
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken ct)
    {
        int count = 10;
        if (arguments.TryGetProperty("count", out JsonElement countElem) && countElem.TryGetInt32(out int c))
        {
            count = Math.Max(1, Math.Min(100, c));
        }

        bool withScores = true;
        if (arguments.TryGetProperty("with_scores", out JsonElement scoresElem) && scoresElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            withScores = scoresElem.GetBoolean();
        }

        bool withAttribs = false;
        if (arguments.TryGetProperty("with_attribs", out JsonElement attribsElem) && attribsElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            withAttribs = attribsElem.GetBoolean();
        }

        float? epsilon = null;
        if (arguments.TryGetProperty("epsilon", out JsonElement epsElem) && epsElem.TryGetSingle(out float eps))
        {
            epsilon = eps;
        }

        string? filter = null;
        if (arguments.TryGetProperty("filter", out JsonElement filterElem) && !string.IsNullOrWhiteSpace(filterElem.GetString()))
        {
            filter = filterElem.GetString()!.Trim();
        }

        VectorSearchOptions searchOptions = new()
        {
            Count = count,
            WithScores = withScores,
            WithAttribs = withAttribs,
            Epsilon = epsilon,
            Filter = filter
        };

        IReadOnlyList<VectorSearchResult> matches;

        // Either search by explicit float vector or by existing element reference
        float[]? vector = ExtractVector(arguments);
        if (vector != null && vector.Length > 0)
        {
            matches = await _vectorClient.SearchAsync(key, vector, searchOptions, ct).ConfigureAwait(false);
        }
        else if (arguments.TryGetProperty("element", out JsonElement refElem) && !string.IsNullOrWhiteSpace(refElem.GetString()))
        {
            string element = refElem.GetString()!.Trim();
            matches = await _vectorClient.SearchByElementAsync(key, element, searchOptions, ct).ConfigureAwait(false);
        }
        else
        {
            sw.Stop();
            return ToolCallResult.Error("Search requires either 'vector' (float array) or 'element' (existing element id to search by).");
        }

        sw.Stop();

        return ToolCallResult.Success(new
        {
            success = true,
            action = "search",
            key,
            matchCount = matches.Count,
            results = matches.Select(m => new
            {
                element = m.Element,
                score = Math.Round(m.Score, 5),
                attributes = m.Attributes
            }),
            mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private async Task<ToolCallResult> HandleRemoveAsync(
        string key,
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!arguments.TryGetProperty("element", out JsonElement elemProperty) || string.IsNullOrWhiteSpace(elemProperty.GetString()))
        {
            return ToolCallResult.Error("Argument 'element' is required for 'remove' action.");
        }
        string element = elemProperty.GetString()!.Trim();

        bool removed = await _vectorClient.RemoveAsync(key, element, ct).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Success(new
        {
            success = true,
            action = "remove",
            key,
            element,
            removed,
            mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private async Task<ToolCallResult> HandleInfoAsync(
        string key,
        Stopwatch sw,
        CancellationToken ct)
    {
        VectorIndexInfo? info = await _vectorClient.GetInfoAsync(key, ct).ConfigureAwait(false);
        sw.Stop();

        if (info == null)
        {
            return ToolCallResult.Success(new
            {
                success = false,
                action = "info",
                key,
                message = $"Vector index '{key}' not found or empty.",
                mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
                latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            });
        }

        return ToolCallResult.Success(new
        {
            success = true,
            action = "info",
            key = info.Key,
            dimensions = info.Dimension,
            size = info.Size,
            metric = info.Metric,
            quantization = info.QuantType,
            details = info.RawDetails,
            mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private async Task<ToolCallResult> HandleDimensionAsync(
        string key,
        Stopwatch sw,
        CancellationToken ct)
    {
        int dim = await _vectorClient.GetDimensionAsync(key, ct).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Success(new
        {
            success = dim >= 0,
            action = "dimension",
            key,
            dimensions = dim,
            mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private async Task<ToolCallResult> HandleListAsync(
        JsonElement arguments,
        Stopwatch sw,
        CancellationToken ct)
    {
        string pattern = "*";
        if (arguments.TryGetProperty("pattern", out JsonElement patElem) && !string.IsNullOrWhiteSpace(patElem.GetString()))
        {
            pattern = patElem.GetString()!.Trim();
        }

        IReadOnlyList<string> indexes = await _vectorClient.ListIndexesAsync(pattern, ct).ConfigureAwait(false);
        sw.Stop();

        return ToolCallResult.Success(new
        {
            success = true,
            action = "list",
            pattern,
            count = indexes.Count,
            indexes,
            mode = _isLiveGarnet ? "Live Garnet" : "In-Process Baseline",
            latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        });
    }

    private static float[]? ExtractVector(JsonElement arguments)
    {
        if (arguments.TryGetProperty("vector", out JsonElement vecElem) && vecElem.ValueKind == JsonValueKind.Array)
        {
            List<float> floats = new();
            foreach (JsonElement item in vecElem.EnumerateArray())
            {
                if (item.TryGetSingle(out float val))
                {
                    floats.Add(val);
                }
            }
            return floats.ToArray();
        }

        return null;
    }

    private (IGarnetVectorClient Client, bool IsLive) InitializeClient()
    {
        if (AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return (new InProcessGarnetVectorClient(), false);
        }

        string host = "127.0.0.1";
        int port = 28298;

        string? envConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ??
                          Environment.GetEnvironmentVariable("DISTRIBUTED_CACHE") ??
                          Environment.GetEnvironmentVariable("GARNET_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(envConn))
        {
            var parts = envConn.Split(',', StringSplitOptions.TrimEntries)[0].Split(':');
            if (parts.Length > 0) host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out int p)) port = p;
        }

        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.ReceiveTimeout = 200;
            socket.SendTimeout = 200;

            var result = socket.BeginConnect(host, port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(200, true);

            if (success && socket.Connected)
            {
                socket.EndConnect(result);
                ConfigurationOptions config = new()
                {
                    EndPoints = { { host, port } },
                    AbortOnConnectFail = false,
                    ConnectTimeout = 500,
                    SyncTimeout = 500
                };

                IConnectionMultiplexer muxer = ConnectionMultiplexer.Connect(config);
                if (muxer.IsConnected)
                {
                    _logger($"Live cache connected at {host}:{port}. Initialized GarnetVectorClient.");
                    return (new GarnetVectorClient(muxer), true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"Could not connect to live Garnet cache at {host}:{port} ({ex.Message}). Using in-process test double.");
        }

        return (new InProcessGarnetVectorClient(), false);
    }
}
