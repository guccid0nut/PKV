// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MemoryModernizationService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Track 1 Client Memory Allocator Modernization (Microsoft mimalloc)
// ============================================================================

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for player client Microsoft mimalloc memory allocator modernization,
///     two-tier rolling backups (.bak and .bak.old), and process lock safety.
/// </summary>
public static class MemoryModernizationService
{
    public static ClientMemoryReport GetMemoryStatus(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.MemoryModernizationService.GetMemoryStatus(repoRootOrGameDir);

    public static IReadOnlyList<string> FindClientDirectories(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.MemoryModernizationService.FindClientDirectories(repoRootOrGameDir);

    public static bool IsClientSafeDirectory(string path)
        => Kongor.Core.Services.Native.MemoryModernizationService.IsClientSafeDirectory(path);

    public static bool IsClientSafeTarget(string path)
        => Kongor.Core.Services.Native.MemoryModernizationService.IsClientSafeTarget(path);

    public static (bool IsRunning, string ProcessNames) AreClientProcessesRunning()
        => Kongor.Core.Services.Native.MemoryModernizationService.AreClientProcessesRunning();

    public static MemoryDeployResult DeployMimalloc(
        string repoRootOrGameDir,
        string? candidateSourceDir = null,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.MemoryModernizationService.DeployMimalloc(
            repoRootOrGameDir, candidateSourceDir, skipProcessCheck, logAction);

    public static MemoryRollbackResult RollbackMimalloc(
        string repoRootOrGameDir,
        string tier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.MemoryModernizationService.RollbackMimalloc(
            repoRootOrGameDir, tier, skipProcessCheck, logAction);
}
