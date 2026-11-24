// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GarnetVectorClient.cs
// System: ASPIRE.Common.Caching.Vectors
// Description: Production high-throughput client for Microsoft Garnet Vector Sets.
// ============================================================================

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ASPIRE.Common.Caching.Vectors;

/// <summary>
/// Production implementation of <see cref="IGarnetVectorClient"/> executing native RESP vector commands
/// (VADD, VSIM, VREM, VINFO, VDIM) over Microsoft Garnet with zero-allocation binary FP32 marshaling.
/// </summary>
public sealed class GarnetVectorClient : IGarnetVectorClient
{
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly ILogger<GarnetVectorClient> _logger;

    public GarnetVectorClient(
        IConnectionMultiplexer? multiplexer = null,
        ILogger<GarnetVectorClient>? logger = null)
    {
        _multiplexer = multiplexer;
        _logger = logger ?? NullLogger<GarnetVectorClient>.Instance;
    }

    /// <summary>
    /// Encodes a 32-bit floating point vector directly into a binary IEEE 754 byte array
    /// expected by Garnet's 'FP32' argument without intermediate string formatting.
    /// </summary>
    public static byte[] ToFp32Bytes(ReadOnlyMemory<float> vector)
    {
        return MemoryMarshal.AsBytes(vector.Span).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(
        string key,
        string element,
        ReadOnlyMemory<float> vector,
        VectorAddOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);

        IDatabase? db = GetDatabase();
        if (db == null)
        {
            _logger.LogWarning("Cannot execute VADD on '{Key}': Redis/Garnet connection multiplexer is offline.", key);
            return false;
        }

        try
        {
            List<object> args = new();
            args.Add(key);

            if (options?.ReduceDim is int reduceDim && reduceDim > 0)
            {
                args.Add("REDUCE");
                args.Add(reduceDim);
            }

            args.Add("FP32");
            args.Add(ToFp32Bytes(vector));
            args.Add(element);

            VectorQuantization quant = options?.Quantization ?? VectorQuantization.None;
            if (quant == VectorQuantization.SQ8)
            {
                args.Add("XPREQ8");
            }
            else if (quant == VectorQuantization.None)
            {
                args.Add("NOQUANT");
            }

            if (options?.Ef is int ef && ef > 0)
            {
                args.Add("EF");
                args.Add(ef);
            }

            if (!string.IsNullOrEmpty(options?.Attributes))
            {
                args.Add("SETATTR");
                args.Add(options.Attributes);
            }

            if (options?.M is int m && m > 0)
            {
                args.Add("M");
                args.Add(m);
            }

            VectorMetric metric = options?.Metric ?? VectorMetric.Cosine;
            args.Add("XDISTANCE_METRIC");
            args.Add(metric switch
            {
                VectorMetric.Cosine => "COSINE",
                VectorMetric.Euclidean => "L2",
                VectorMetric.InnerProduct => "IP",
                _ => "COSINE"
            });

            RedisResult result = await db.ExecuteAsync("VADD", args.ToArray()).ConfigureAwait(false);
            return !result.IsNull;
        }
        catch (RedisServerException ex) when (ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "Garnet Vector Sets (VADD) command is not supported on the target cache node. Ensure --enable-vector-set-preview is enabled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute VADD on key '{Key}' for element '{Element}'.", key, element);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string key,
        ReadOnlyMemory<float> queryVector,
        VectorSearchOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        IDatabase? db = GetDatabase();
        if (db == null)
        {
            _logger.LogWarning("Cannot execute VSIM on '{Key}': Redis/Garnet connection multiplexer is offline.", key);
            return Array.Empty<VectorSearchResult>();
        }

        try
        {
            List<object> args = new();
            args.Add(key);
            args.Add("FP32");
            args.Add(ToFp32Bytes(queryVector));

            bool withScores = options?.WithScores ?? true;
            if (withScores)
            {
                args.Add("WITHSCORES");
            }

            bool withAttribs = options?.WithAttribs ?? false;
            if (withAttribs)
            {
                args.Add("WITHATTRIBS");
            }

            if (options?.Count is int count && count > 0)
            {
                args.Add("COUNT");
                args.Add(count);
            }

            if (options?.Epsilon is float eps)
            {
                args.Add("EPSILON");
                args.Add(eps.ToString(CultureInfo.InvariantCulture));
            }

            if (options?.Ef is int ef && ef > 0)
            {
                args.Add("EF");
                args.Add(ef);
            }

            if (!string.IsNullOrEmpty(options?.Filter))
            {
                args.Add("FILTER");
                args.Add(options.Filter);
            }

            RedisResult result = await db.ExecuteAsync("VSIM", args.ToArray()).ConfigureAwait(false);
            return ParseSearchResults(result, withScores, withAttribs);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "Garnet Vector Sets (VSIM) command is not supported on the target cache node.");
            return Array.Empty<VectorSearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute VSIM on key '{Key}'.", key);
            return Array.Empty<VectorSearchResult>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchByElementAsync(
        string key,
        string element,
        VectorSearchOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);

        IDatabase? db = GetDatabase();
        if (db == null)
        {
            _logger.LogWarning("Cannot execute VSIM on '{Key}': Redis/Garnet connection multiplexer is offline.", key);
            return Array.Empty<VectorSearchResult>();
        }

        try
        {
            List<object> args = new();
            args.Add(key);
            args.Add("ELE");
            args.Add(element);

            bool withScores = options?.WithScores ?? true;
            if (withScores)
            {
                args.Add("WITHSCORES");
            }

            bool withAttribs = options?.WithAttribs ?? false;
            if (withAttribs)
            {
                args.Add("WITHATTRIBS");
            }

            if (options?.Count is int count && count > 0)
            {
                args.Add("COUNT");
                args.Add(count);
            }

            if (options?.Epsilon is float eps)
            {
                args.Add("EPSILON");
                args.Add(eps.ToString(CultureInfo.InvariantCulture));
            }

            if (options?.Ef is int ef && ef > 0)
            {
                args.Add("EF");
                args.Add(ef);
            }

            if (!string.IsNullOrEmpty(options?.Filter))
            {
                args.Add("FILTER");
                args.Add(options.Filter);
            }

            RedisResult result = await db.ExecuteAsync("VSIM", args.ToArray()).ConfigureAwait(false);
            return ParseSearchResults(result, withScores, withAttribs);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(ex, "Garnet Vector Sets (VSIM) command is not supported on the target cache node.");
            return Array.Empty<VectorSearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute VSIM by element '{Element}' on key '{Key}'.", element, key);
            return Array.Empty<VectorSearchResult>();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        string key,
        string element,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);

        IDatabase? db = GetDatabase();
        if (db == null) return false;

        try
        {
            RedisResult result = await db.ExecuteAsync("VREM", key, element).ConfigureAwait(false);
            if (result is not null && !result.IsNull)
            {
                if (int.TryParse((string?)result, out int count))
                {
                    return count > 0;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute VREM on key '{Key}' for element '{Element}'.", key, element);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<VectorIndexInfo?> GetInfoAsync(
        string key,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        IDatabase? db = GetDatabase();
        if (db == null) return null;

        try
        {
            RedisResult result = await db.ExecuteAsync("VINFO", key).ConfigureAwait(false);
            if (result is null || result.IsNull)
            {
                return null;
            }

            RedisResult[]? array = (RedisResult[]?)result;
            if (array is null || array.Length == 0)
            {
                return null;
            }

            Dictionary<string, string> details = new(StringComparer.OrdinalIgnoreCase);

            // VINFO returns alternating [key, value] pairs in RESP array
            for (int i = 0; i + 1 < array.Length; i += 2)
            {
                string propKey = (string?)array[i] ?? string.Empty;
                string propVal = (string?)array[i + 1] ?? string.Empty;
                if (!string.IsNullOrEmpty(propKey))
                {
                    details[propKey] = propVal;
                }
            }

            int dim = 0;
            if (details.TryGetValue("dimension", out string? dimStr) ||
                details.TryGetValue("dimensions", out dimStr) ||
                details.TryGetValue("vector-dimension", out dimStr) ||
                details.TryGetValue("dim", out dimStr) ||
                details.TryGetValue("input-vector-dimensions", out dimStr))
            {
                int.TryParse(dimStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out dim);
            }

            if (dim <= 0)
            {
                dim = await GetDimensionAsync(key, ct).ConfigureAwait(false);
            }

            long size = 0;
            if (details.TryGetValue("size", out string? sizeStr) ||
                details.TryGetValue("count", out sizeStr) ||
                details.TryGetValue("elements", out sizeStr))
            {
                long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            }

            string metric = details.TryGetValue("distance-metric", out string? m) ? m : "COSINE";
            string quant = details.TryGetValue("quant-type", out string? q) ? q : "None";

            return new VectorIndexInfo
            {
                Key = key,
                Dimension = dim,
                Size = size,
                Metric = metric,
                QuantType = quant,
                RawDetails = details
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute VINFO on key '{Key}'.", key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> GetDimensionAsync(
        string key,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        IDatabase? db = GetDatabase();
        if (db == null) return -1;

        try
        {
            RedisResult result = await db.ExecuteAsync("VDIM", key).ConfigureAwait(false);
            if (result is not null && !result.IsNull)
            {
                if (result.Type == ResultType.Integer)
                {
                    return (int)result;
                }

                if (int.TryParse((string?)result, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dim))
                {
                    return dim;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VDIM command execution failed for key '{Key}'. Attempting fallback to VINFO.", key);
        }

        // Resilient fallback: Query VINFO if VDIM is unavailable or timed out
        try
        {
            RedisResult infoResult = await db.ExecuteAsync("VINFO", key).ConfigureAwait(false);
            if (infoResult is not null && !infoResult.IsNull && (RedisResult[]?)infoResult is { Length: > 1 } array)
            {
                for (int i = 0; i + 1 < array.Length; i += 2)
                {
                    string prop = (string?)array[i] ?? string.Empty;
                    if (prop.Equals("input-vector-dimensions", StringComparison.OrdinalIgnoreCase) ||
                        prop.Equals("dimension", StringComparison.OrdinalIgnoreCase) ||
                        prop.Equals("dimensions", StringComparison.OrdinalIgnoreCase) ||
                        prop.Equals("dim", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = (string?)array[i + 1] ?? string.Empty;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dim))
                        {
                            return dim;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute fallback VINFO on key '{Key}'.", key);
        }

        return -1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListIndexesAsync(
        string pattern = "*",
        CancellationToken ct = default)
    {
        IDatabase? db = GetDatabase();
        if (db == null) return Array.Empty<string>();

        try
        {
            RedisResult result = await db.ExecuteAsync("KEYS", pattern).ConfigureAwait(false);
            if (result is not null && !result.IsNull && (RedisResult[]?)result is { } keys)
            {
                List<string> matchedKeys = new();
                foreach (RedisResult k in keys)
                {
                    string keyStr = (string?)k ?? string.Empty;
                    if (!string.IsNullOrEmpty(keyStr))
                    {
                        matchedKeys.Add(keyStr);
                    }
                }
                return matchedKeys;
            }
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list keys matching '{Pattern}'.", pattern);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Parses RESP results from VSIM command into typed <see cref="VectorSearchResult"/> objects.
    /// Handles both flat RESP arrays and nested array variants across RESP2/RESP3.
    /// </summary>
    public static IReadOnlyList<VectorSearchResult> ParseSearchResults(
        RedisResult? result,
        bool withScores,
        bool withAttribs)
    {
        if (result is null || result.IsNull)
        {
            return Array.Empty<VectorSearchResult>();
        }

        if (result.Type == ResultType.SimpleString || result.Type == ResultType.BulkString)
        {
            string? single = (string?)result;
            if (string.IsNullOrEmpty(single)) return Array.Empty<VectorSearchResult>();
            return new[] { new VectorSearchResult { Element = single, Score = 0.0 } };
        }

        RedisResult[]? array = (RedisResult[]?)result;
        if (array is null || array.Length == 0)
        {
            return Array.Empty<VectorSearchResult>();
        }

        List<VectorSearchResult> results = new();

        // Variant A: Nested arrays [[elem, score, attrib], [elem, score, attrib]]
        if (array[0].Type == ResultType.Array && (RedisResult[]?)array[0] is { Length: > 0 })
        {
            foreach (RedisResult item in array)
            {
                if ((RedisResult[]?)item is { Length: > 0 } subArray)
                {
                    string element = (string?)subArray[0] ?? string.Empty;
                    double score = 0.0;
                    string? attribs = null;

                    if (subArray.Length > 1 &&
                        double.TryParse((string?)subArray[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                    {
                        score = s;
                    }

                    if (subArray.Length > 2)
                    {
                        attribs = (string?)subArray[2];
                    }

                    if (!string.IsNullOrEmpty(element))
                    {
                        results.Add(new VectorSearchResult
                        {
                            Element = element,
                            Score = score,
                            Attributes = attribs
                        });
                    }
                }
            }
            return results;
        }

        // Variant B: Flat array [elem1, score1, attribs1, elem2, score2, attribs2]
        int stride = 1 + (withScores ? 1 : 0) + (withAttribs ? 1 : 0);
        if (stride <= 1 || array.Length % stride != 0)
        {
            if (withScores && array.Length >= 2 && array.Length % 2 == 0)
            {
                stride = 2;
            }
            else
            {
                stride = 1;
            }
        }

        for (int i = 0; i < array.Length; i += stride)
        {
            string element = (string?)array[i] ?? string.Empty;
            double score = 0.0;
            string? attribs = null;

            if (withScores && i + 1 < array.Length)
            {
                if (double.TryParse((string?)array[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                {
                    score = s;
                }
            }

            if (withAttribs && i + 2 < array.Length)
            {
                attribs = (string?)array[i + 2];
            }

            if (!string.IsNullOrEmpty(element))
            {
                results.Add(new VectorSearchResult
                {
                    Element = element,
                    Score = score,
                    Attributes = attribs
                });
            }
        }

        return results;
    }

    private IDatabase? GetDatabase()
    {
        if (_multiplexer == null || !_multiplexer.IsConnected)
        {
            return null;
        }

        return _multiplexer.GetDatabase();
    }
}
