// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: IGarnetVectorClient.cs
// System: ASPIRE.Common.Caching.Vectors
// Description: Contract for Microsoft Garnet native Vector Sets operations.
// ============================================================================

namespace ASPIRE.Common.Caching.Vectors;

/// <summary>
/// Client interface for high-performance sub-millisecond vector similarity search,
/// element ingestion, and index management over Microsoft Garnet Vector Sets.
/// </summary>
public interface IGarnetVectorClient
{
    /// <summary>
    /// Adds or updates an element with its vector embedding in the specified vector set.
    /// If the vector set does not exist, it is implicitly created with the specified metric and dimensionality.
    /// </summary>
    /// <param name="key">Vector set index key.</param>
    /// <param name="element">Unique element identifier within the index.</param>
    /// <param name="vector">Continuous 32-bit floating point vector.</param>
    /// <param name="options">Optional metric, attributes, and quantization settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the element was successfully added or updated.</returns>
    Task<bool> AddAsync(
        string key,
        string element,
        ReadOnlyMemory<float> vector,
        VectorAddOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Searches for the top-k nearest neighbors to the provided query vector.
    /// </summary>
    /// <param name="key">Vector set index key.</param>
    /// <param name="queryVector">Continuous 32-bit floating point query vector.</param>
    /// <param name="options">Query options such as count limit, epsilon, filter, and score inclusion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching elements ranked by similarity distance.</returns>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string key,
        ReadOnlyMemory<float> queryVector,
        VectorSearchOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Searches for nearest neighbors using an existing element in the vector set as the query.
    /// </summary>
    /// <param name="key">Vector set index key.</param>
    /// <param name="element">Existing element identifier within the index.</param>
    /// <param name="options">Query options such as count limit, epsilon, and score inclusion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching elements ranked by similarity distance.</returns>
    Task<IReadOnlyList<VectorSearchResult>> SearchByElementAsync(
        string key,
        string element,
        VectorSearchOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes an element from the vector set.
    /// </summary>
    /// <param name="key">Vector set index key.</param>
    /// <param name="element">Element identifier to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the element existed and was removed.</returns>
    Task<bool> RemoveAsync(
        string key,
        string element,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves index metadata, dimensions, and element count.
    /// </summary>
    /// <param name="key">Vector set index key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>VectorIndexInfo if the index exists, or null otherwise.</returns>
    Task<VectorIndexInfo?> GetInfoAsync(
        string key,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the dimensionality of vectors stored in the specified vector set.
    /// </summary>
    /// <param name="key">Vector set index key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Vector dimension, or -1 if the index does not exist.</returns>
    Task<int> GetDimensionAsync(
        string key,
        CancellationToken ct = default);

    /// <summary>
    /// Lists vector index keys matching the specified pattern.
    /// </summary>
    /// <param name="pattern">Key search pattern (e.g. "vectors:*", "*").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching vector set keys.</returns>
    Task<IReadOnlyList<string>> ListIndexesAsync(
        string pattern = "*",
        CancellationToken ct = default);
}
