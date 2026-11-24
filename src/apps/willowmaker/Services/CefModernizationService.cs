// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CefModernizationService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 11 Client CEF Retirement & RmlUi Modern HUD Overlay
// ============================================================================

using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for Chromium Embedded Framework (libcef.dll) retirement,
///     two-tier rolling backups (.bak / .bak.old), and RAM/disk telemetry.
/// </summary>
public static class CefModernizationService
{
    public static string ResolveCefDirectory(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.CefModernizationService.ResolveCefDirectory(repoRootOrGameDir);

    public static IReadOnlyList<string> FindCefTargets(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.CefModernizationService.FindCefTargets(repoRootOrGameDir);

    public static bool IsClientProcessRunning(out string runningProcessNames)
        => Kongor.Core.Services.Native.CefModernizationService.IsClientProcessRunning(out runningProcessNames);

    public static CefStatusReport GetStatus(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.CefModernizationService.GetStatus(repoRootOrGameDir);

    public static CefDeactivationResult Deactivate(string repoRootOrGameDir, bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.CefModernizationService.Deactivate(repoRootOrGameDir, skipProcessCheck);

    public static CefRollbackResult Rollback(string repoRootOrGameDir, string targetTier = "previous", bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.CefModernizationService.Rollback(repoRootOrGameDir, targetTier, skipProcessCheck);
}
