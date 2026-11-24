// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessVidService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 10 Dedicated Server Graphics Decoupling & Server Bloat Purge
// ============================================================================

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for dedicated server video bloat purge governance,
///     two-tier rolling backups (.bak and .bak.old), and process lock safety.
/// </summary>
public static class HeadlessVidService
{
    public static readonly string[] VideoBloatFilenames = Kongor.Core.Services.Native.HeadlessVidService.VideoBloatFilenames;

    /// <summary>
    ///     Strict invariant check ensuring path targets server and NEVER touches client files.
    /// </summary>
    public static bool IsServerSafeTarget(string path)
        => Kongor.Core.Services.Native.HeadlessVidService.IsServerSafeTarget(path);

    /// <summary>
    ///     Inspects and aggregates video bloat status across dedicated server targets.
    /// </summary>
    public static ServerVidReport GetStatusReport(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.HeadlessVidService.GetStatusReport(repoRootOrGameDir);

    /// <summary>
    ///     Finds all dedicated server video bloat targets within the given repository root or game folder.
    /// </summary>
    public static IReadOnlyList<string> FindServerVidTargets(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.HeadlessVidService.FindServerVidTargets(repoRootOrGameDir);

    /// <summary>
    ///     Safely purges dedicated server video bloat across server targets.
    /// </summary>
    public static ServerVidPurgeResult PurgeServerVideoBloat(
        string repoRootOrGameDir,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.HeadlessVidService.PurgeServerVideoBloat(
            repoRootOrGameDir, skipProcessCheck, logAction);

    /// <summary>
    ///     Rolls back video bloat across server targets to previous (.bak) or old (.bak.old) tier.
    /// </summary>
    public static ServerVidRollbackResult RollbackServerVideoBloat(
        string repoRootOrGameDir,
        string targetTier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.HeadlessVidService.RollbackServerVideoBloat(
            repoRootOrGameDir, targetTier, skipProcessCheck, logAction);
}
