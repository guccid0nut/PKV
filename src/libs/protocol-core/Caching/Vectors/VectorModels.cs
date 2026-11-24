// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VectorModels.cs
// System: ASPIRE.Common.Caching.Vectors
// Description: Options and result models for Garnet vector search operations.
// ============================================================================

namespace ASPIRE.Common.Caching.Vectors;

/// <summary>
/// Result item returned from a vector similarity search (VSIM).
/// </summary>
public sealed record VectorSearchResult
{
    /// <summary>
    /// Unique identifier of the vector element within the index.
    /// </summary>
    public required string Element { get; init; }

    /// <summary>
    /// Distance or similarity score (e.g., Cosine distance or L2 distance).
    /// Lower values indicate closer similarity under Cosine/L2.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Optional JSON or string metadata attributes associated with the element.
    /// </summary>
    public string? Attributes { get; init; }

    /// <summary>
    /// Raw vector embeddings if requested.
    /// </summary>
    public ReadOnlyMemory<float>? Vector { get; init; }
}

/// <summary>
/// Configuration options for adding an element to a vector set (VADD).
/// </summary>
public sealed record VectorAddOptions
{
    /// <summary>
    /// Distance metric to use when the vector set is created. Defaults to Cosine.
    /// </summary>
    public VectorMetric Metric { get; init; } = VectorMetric.Cosine;

    /// <summary>
    /// Quantization mode for memory optimization. Defaults to full precision (None).
    /// </summary>
    public VectorQuantization Quantization { get; init; } = VectorQuantization.None;

    /// <summary>
    /// Optional metadata payload (e.g. JSON string) stored alongside the element via SETATTR.
    /// </summary>
    public string? Attributes { get; init; }

    /// <summary>
    /// Optional search accuracy / exploration factor (EF) during index construction.
    /// </summary>
    public int? Ef { get; init; }

    /// <summary>
    /// Maximum number of bidirectional links per node in HNSW index (M).
    /// </summary>
    public int? M { get; init; }

    /// <summary>
    /// Dimensionality reduction target, if index-level PCA reduction is desired.
    /// </summary>
    public int? ReduceDim { get; init; }
}

/// <summary>
/// Query options for searching a vector set (VSIM).
/// </summary>
public sealed record VectorSearchOptions
{
    /// <summary>
    /// Maximum number of nearest neighbors to return. Defaults to 10.
    /// </summary>
    public int Count { get; init; } = 10;

    /// <summary>
    /// Whether to return similarity/distance scores alongside elements (WITHSCORES).
    /// </summary>
    public bool WithScores { get; init; } = true;

    /// <summary>
    /// Whether to return stored metadata attributes alongside elements (WITHATTRIBS).
    /// </summary>
    public bool WithAttribs { get; init; }

    /// <summary>
    /// Maximum distance threshold for results (EPSILON).
    /// </summary>
    public float? Epsilon { get; init; }

    /// <summary>
    /// Search accuracy exploration factor (EF) during query traversal.
    /// </summary>
    public int? Ef { get; init; }

    /// <summary>
    /// Attribute filter expression (FILTER).
    /// </summary>
    public string? Filter { get; init; }
}

/// <summary>
/// Detailed metadata and statistics for a Garnet Vector Set index.
/// </summary>
public sealed record VectorIndexInfo
{
    /// <summary>
    /// Key name of the vector set.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Dimensionality of the vectors in this index.
    /// </summary>
    public int Dimension { get; init; }

    /// <summary>
    /// Total number of vector elements stored in this index.
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// Distance metric configured for this index (e.g. COSINE, L2, IP).
    /// </summary>
    public string Metric { get; init; } = "UNKNOWN";

    /// <summary>
    /// Quantization type (e.g. None, SQ8, PQ).
    /// </summary>
    public string QuantType { get; init; } = "None";

    /// <summary>
    /// Complete key-value properties returned by the engine VINFO command.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawDetails { get; init; } = new Dictionary<string, string>();
}
