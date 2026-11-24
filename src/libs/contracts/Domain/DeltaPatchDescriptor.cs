// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Metadata definition for delta patches between game versions.
/// </summary>
internal sealed record DeltaPatchDescriptor(
    string TargetRelativePath,
    string SourceVersion,
    string TargetVersion,
    string PatchAlgorithm,
    long PatchSizeBytes,
    string PatchSha256,
    string PatchDownloadUrl);
