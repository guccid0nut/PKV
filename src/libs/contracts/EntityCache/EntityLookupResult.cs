// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.EntityCache;

/// <summary>
///     Represents the result and telemetry of a tiered entity or asset lookup operation.
/// </summary>
/// <param name="Found">Whether the requested asset was found.</param>
/// <param name="SourceTier">The resolution tier: "PreBakedMemory", "PreBakedDisk", "VfsArchive", or "NotFound".</param>
/// <param name="LatencyMicroseconds">Lookup duration in microseconds.</param>
/// <param name="ResolvedPath">The resolved file or archive path.</param>
public readonly record struct EntityLookupResult(
    bool Found,
    string SourceTier,
    long LatencyMicroseconds,
    string ResolvedPath
);
