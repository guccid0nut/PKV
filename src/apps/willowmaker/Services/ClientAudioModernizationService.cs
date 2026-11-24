// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ClientAudioModernizationService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 8 Client Low-Latency WASAPI & 3D Spatial HRTF Audio (fmodex64.dll)
// ============================================================================

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for player client low-latency WASAPI and 3D HRTF audio modernization,
///     two-tier rolling backups (.bak and .bak.old), and process lock safety.
/// </summary>
public static class ClientAudioModernizationService
{
    /// <summary>
    ///     Inspects and aggregates audio modernization status across client targets.
    /// </summary>
    public static ClientAudioReport GetClientAudioStatus(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.GetClientAudioStatus(repoRootOrGameDir);

    /// <summary>
    ///     Finds all player client audio target paths within the given repository root or game folder.
    /// </summary>
    public static IReadOnlyList<string> FindClientAudioTargets(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.FindClientAudioTargets(repoRootOrGameDir);

    /// <summary>
    ///     Validates that a path targets client audio and never a server directory.
    /// </summary>
    public static bool IsClientSafeTarget(string path)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.IsClientSafeTarget(path);

    /// <summary>
    ///     Inspects PE exports to check whether a DLL is the Project Kongor audio proxy shim.
    /// </summary>
    public static bool IsKongorAudioProxyBinary(string filePath)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.IsKongorAudioProxyBinary(filePath);

    /// <summary>
    ///     Checks whether any player client processes are executing.
    /// </summary>
    public static bool IsClientRunning(out string runningProcessNames)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.IsClientRunning(out runningProcessNames);

    /// <summary>
    ///     Safely deploys the low-latency WASAPI & 3D HRTF proxy shim across client targets.
    /// </summary>
    public static ClientAudioDeployResult DeployClientAudioProxy(
        string repoRootOrGameDir,
        string candidatePath,
        bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.DeployClientAudioProxy(
            repoRootOrGameDir, candidatePath, skipProcessCheck);

    /// <summary>
    ///     Rolls back fmodex64.dll across client targets to vanilla FMOD Ex.
    /// </summary>
    public static ClientAudioRollbackResult RollbackClientAudioProxy(
        string repoRootOrGameDir,
        bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.ClientAudioModernizationService.RollbackClientAudioProxy(
            repoRootOrGameDir, skipProcessCheck);
}
