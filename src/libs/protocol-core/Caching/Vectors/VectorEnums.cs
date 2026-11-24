// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VectorEnums.cs
// System: ASPIRE.Common.Caching.Vectors
// Description: Distance metrics and quantization modes for Microsoft Garnet Vector Sets.
// ============================================================================

namespace ASPIRE.Common.Caching.Vectors;

/// <summary>
/// Vector distance metrics supported natively by Microsoft Garnet.
/// </summary>
public enum VectorMetric
{
    /// <summary>
    /// Cosine distance (1 - cosine_similarity). Ideal for normalized embeddings, bot states, and semantic queries.
    /// </summary>
    Cosine,

    /// <summary>
    /// Euclidean / L2 squared distance. Ideal for spatial coordinates and physical positions.
    /// </summary>
    Euclidean,

    /// <summary>
    /// Inner product (dot product).
    /// </summary>
    InnerProduct
}

/// <summary>
/// Vector quantization strategies supported by Garnet.
/// </summary>
public enum VectorQuantization
{
    /// <summary>
    /// Full 32-bit floating point precision (NOQUANT).
    /// </summary>
    None,

    /// <summary>
    /// 8-bit scalar quantization (XPREQ8) for 4x memory savings.
    /// </summary>
    SQ8,

    /// <summary>
    /// Product quantization.
    /// </summary>
    PQ
}
