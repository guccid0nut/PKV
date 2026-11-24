// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RawInputModernizationService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Track 2 High-Polling-Rate Esports Mouse Modernization (Windows Raw Input 8000Hz)
// ============================================================================

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for player client Windows Raw Input 8000Hz mouse modernization,
///     two-tier rolling backups (.bak and .bak.old), and process lock safety.
/// </summary>
public static class RawInputModernizationService
{
    public static RawInputReport GetRawInputStatus(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.RawInputModernizationService.GetRawInputStatus(repoRootOrGameDir);

    public static IReadOnlyList<string> FindClientDirectories(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.RawInputModernizationService.FindClientDirectories(repoRootOrGameDir);

    public static bool IsClientSafeDirectory(string path)
        => Kongor.Core.Services.Native.RawInputModernizationService.IsClientSafeDirectory(path);

    public static bool IsClientSafeTarget(string path)
        => Kongor.Core.Services.Native.RawInputModernizationService.IsClientSafeTarget(path);

    public static (bool IsRunning, string ProcessNames) AreClientProcessesRunning()
        => Kongor.Core.Services.Native.RawInputModernizationService.AreClientProcessesRunning();

    public static RawInputDeployResult DeployRawInput(
        string repoRootOrGameDir,
        string? candidateSourceDir = null,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.RawInputModernizationService.DeployRawInput(
            repoRootOrGameDir, candidateSourceDir, skipProcessCheck, logAction);

    public static RawInputRollbackResult RollbackRawInput(
        string repoRootOrGameDir,
        string tier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.RawInputModernizationService.RollbackRawInput(
            repoRootOrGameDir, tier, skipProcessCheck, logAction);

    public static RawInputCvarResult ConfigureStartupCfg(
        string repoRootOrGameDir,
        bool enableRawInput,
        float sensitivity = 1.0f,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.RawInputModernizationService.ConfigureStartupCfg(
            repoRootOrGameDir, enableRawInput, sensitivity, logAction);
}
