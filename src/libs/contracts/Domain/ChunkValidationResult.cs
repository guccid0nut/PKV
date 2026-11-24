// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Chunk-level integrity verification result.
/// </summary>
internal sealed record ChunkValidationResult(
    string FilePath,
    int ChunkIndex,
    long ByteOffset,
    int ChunkLength,
    string ExpectedSha256,
    string ActualSha256,
    bool IsValid);
