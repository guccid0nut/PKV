// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: InProcessGarnetVectorClient.cs
// System: ASPIRE.Common.Caching.Vectors
// Description: In-memory test double for IGarnetVectorClient enabling sub-millisecond offline tests.
// ============================================================================

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ASPIRE.Common.Caching.Vectors;

/// <summary>
/// Fast in-memory implementation of <see cref="IGarnetVectorClient"/> for unit tests and local mock environments.
/// Implements standard vector math (Cosine distance, Euclidean distance, Inner Product) without requiring a running Garnet instance.
/// </summary>
public sealed class InProcessGarnetVectorClient : IGarnetVectorClient
{
    private sealed record StoredElement(string Id, float[] Vector, string? Attributes);

    private sealed class VectorIndex
    {
        public string Key { get; }
        public VectorMetric Metric { get; set; } = VectorMetric.Cosine;
        public VectorQuantization Quantization { get; set; } = VectorQuantization.None;
        public int Dimension { get; set; }
        public ConcurrentDictionary<string, StoredElement> Elements { get; } = new(StringComparer.Ordinal);

        public VectorIndex(string key)
        {
            Key = key;
        }
    }

    private readonly ConcurrentDictionary<string, VectorIndex> _indexes = new(StringComparer.Ordinal);

    public Task<bool> AddAsync(
        string key,
        string element,
        ReadOnlyMemory<float> vector,
        VectorAddOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);

        VectorIndex index = _indexes.GetOrAdd(key, k => new VectorIndex(k)
        {
            Metric = options?.Metric ?? VectorMetric.Cosine,
            Quantization = options?.Quantization ?? VectorQuantization.None,
            Dimension = vector.Length
        });

        if (index.Dimension == 0)
        {
            index.Dimension = vector.Length;
        }
        else if (index.Dimension != vector.Length)
        {
            throw new ArgumentException(
                $"Vector dimension mismatch for index '{key}'. Expected {index.Dimension}, received {vector.Length}.");
        }

        StoredElement stored = new(element, vector.ToArray(), options?.Attributes);
        index.Elements[element] = stored;

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string key,
        ReadOnlyMemory<float> queryVector,
        VectorSearchOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_indexes.TryGetValue(key, out VectorIndex? index))
        {
            IReadOnlyList<VectorSearchResult> empty = Array.Empty<VectorSearchResult>();
            return Task.FromResult(empty);
        }

        ReadOnlySpan<float> qSpan = queryVector.Span;
        List<VectorSearchResult> results = new();

        foreach (StoredElement item in index.Elements.Values)
        {
            double distance = ComputeDistance(qSpan, item.Vector, index.Metric);

            if (options?.Epsilon is float eps && distance > eps)
            {
                continue;
            }

            results.Add(new VectorSearchResult
            {
                Element = item.Id,
                Score = distance,
                Attributes = options?.WithAttribs == true ? item.Attributes : null,
                Vector = item.Vector
            });
        }

        // Rank by distance (lowest score first for Cosine and Euclidean)
        results.Sort((a, b) => a.Score.CompareTo(b.Score));

        int maxCount = options?.Count ?? 10;
        IReadOnlyList<VectorSearchResult> limited = results.Take(maxCount).ToList();
        return Task.FromResult(limited);
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchByElementAsync(
        string key,
        string element,
        VectorSearchOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);

        if (!_indexes.TryGetValue(key, out VectorIndex? index) ||
            !index.Elements.TryGetValue(element, out StoredElement? target))
        {
            IReadOnlyList<VectorSearchResult> empty = Array.Empty<VectorSearchResult>();
            return Task.FromResult(empty);
        }

        return SearchAsync(key, target.Vector, options, ct);
    }

    public Task<bool> RemoveAsync(
        string key,
        string element,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(element);

        if (_indexes.TryGetValue(key, out VectorIndex? index))
        {
            bool removed = index.Elements.TryRemove(element, out _);
            return Task.FromResult(removed);
        }

        return Task.FromResult(false);
    }

    public Task<VectorIndexInfo?> GetInfoAsync(
        string key,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_indexes.TryGetValue(key, out VectorIndex? index))
        {
            return Task.FromResult<VectorIndexInfo?>(null);
        }

        Dictionary<string, string> details = new()
        {
            ["key"] = index.Key,
            ["dimension"] = index.Dimension.ToString(),
            ["size"] = index.Elements.Count.ToString(),
            ["distance-metric"] = index.Metric.ToString().ToUpperInvariant(),
            ["quant-type"] = index.Quantization.ToString()
        };

        VectorIndexInfo info = new()
        {
            Key = index.Key,
            Dimension = index.Dimension,
            Size = index.Elements.Count,
            Metric = index.Metric.ToString().ToUpperInvariant(),
            QuantType = index.Quantization.ToString(),
            RawDetails = details
        };

        return Task.FromResult<VectorIndexInfo?>(info);
    }

    public Task<int> GetDimensionAsync(
        string key,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_indexes.TryGetValue(key, out VectorIndex? index))
        {
            return Task.FromResult(index.Dimension);
        }

        return Task.FromResult(-1);
    }

    public Task<IReadOnlyList<string>> ListIndexesAsync(
        string pattern = "*",
        CancellationToken ct = default)
    {
        Regex regex = new(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        List<string> matched = _indexes.Keys
            .Where(k => regex.IsMatch(k))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(matched);
    }

    private static double ComputeDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b, VectorMetric metric)
    {
        int length = Math.Min(a.Length, b.Length);
        if (length == 0) return 1.0;

        switch (metric)
        {
            case VectorMetric.Euclidean:
            {
                double sum = 0.0;
                for (int i = 0; i < length; i++)
                {
                    double diff = a[i] - b[i];
                    sum += diff * diff;
                }
                return Math.Sqrt(sum);
            }

            case VectorMetric.InnerProduct:
            {
                double dot = 0.0;
                for (int i = 0; i < length; i++)
                {
                    dot += a[i] * b[i];
                }
                return -dot; // Negative for distance ranking (higher dot product = lower distance)
            }

            case VectorMetric.Cosine:
            default:
            {
                double dot = 0.0;
                double magA = 0.0;
                double magB = 0.0;

                for (int i = 0; i < length; i++)
                {
                    dot += a[i] * b[i];
                    magA += a[i] * a[i];
                    magB += b[i] * b[i];
                }

                if (magA <= 0.0 || magB <= 0.0)
                {
                    return 1.0;
                }

                double similarity = dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
                return Math.Max(0.0, 1.0 - similarity);
            }
        }
    }
}
