// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FastVfsModernizationService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 7 Zstandard (Zstd) VFS Decompression & libdeflate Acceleration
// ============================================================================

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for Phase 7 Fast VFS modernization, archive benchmarking,
///     and dual-engine zlibwapi_x64.dll governance.
/// </summary>
public static class FastVfsModernizationService
{
    public static ArchiveInspectionReport InspectArchive(string archivePath)
        => Kongor.Core.Services.Native.FastVfsModernizationService.InspectArchive(archivePath);

    public static ArchiveBenchmarkResult BenchmarkArchive(string archivePath, int maxEntries = 100)
        => Kongor.Core.Services.Native.FastVfsModernizationService.BenchmarkArchive(archivePath, maxEntries);

    public static VfsStatusReport GetVfsStatus(string repoRootOrGameDir, TargetFleet fleet = TargetFleet.All)
        => Kongor.Core.Services.Native.FastVfsModernizationService.GetVfsStatus(repoRootOrGameDir, fleet);

    public static VfsOperationResult DeployFastVfsBinary(
        string repoRootOrGameDir,
        string? candidatePath = null,
        bool skipProcessCheck = false,
        TargetFleet fleet = TargetFleet.All,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.FastVfsModernizationService.DeployFastVfsBinary(
            repoRootOrGameDir, candidatePath, skipProcessCheck, fleet, logAction);

    public static VfsOperationResult RollbackFastVfsBinary(
        string repoRootOrGameDir,
        string tier = "previous",
        bool skipProcessCheck = false,
        TargetFleet fleet = TargetFleet.All,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.FastVfsModernizationService.RollbackFastVfsBinary(
            repoRootOrGameDir, tier, skipProcessCheck, fleet, logAction);
}
