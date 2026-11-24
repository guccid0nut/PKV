// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Represents the result of a version check against the GitHub releases API.
/// </summary>
public sealed record VersionCheckResult(
    bool IsUpdateAvailable,
    Version? LatestVersion,
    string? DownloadURL,
    string? ReleasePageURL,
    string? ExpectedSha256 = null);
