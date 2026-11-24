// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: IDeltaPatchEngine.cs
// System: Kongor Protocol Core - Differential Binary Delta Patching Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;

namespace ASPIRE.Common.Patching;

/// <summary>
///     Universal interface for computing and applying differential binary delta patches.
///     Enables sub-1.5 MB client game patches by transmitting only modified byte ranges
///     compressed with Zstandard rather than multi-gigabyte raw game binaries.
/// </summary>
public interface IDeltaPatchEngine
{
    /// <summary>
    ///     Computes a compact binary delta representing the transformation from source to target.
    /// </summary>
    byte[] CreateDelta(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target);

    /// <summary>
    ///     Applies a binary delta to source bytes to reconstruct the exact target payload.
    ///     Verifies target SHA-256 checksum for cryptographic integrity.
    /// </summary>
    byte[] ApplyDelta(ReadOnlySpan<byte> source, ReadOnlySpan<byte> delta, string? expectedSha256 = null);

    /// <summary>
    ///     Streams a binary delta into a source file and writes the reconstructed target file
    ///     using atomic temporary file swapping and SHA-256 verification.
    /// </summary>
    Task ApplyDeltaToFileAsync(
        string sourceFilePath,
        Stream deltaStream,
        string targetFilePath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default);
}
