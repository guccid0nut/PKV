// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessAudioStubService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 6 Server Fleet Security & Audio Headless Null Stub (fmodex64.dll)
// ============================================================================

using System;
using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for dedicated server headless audio null stub governance,
///     two-tier rolling backups (.bak and .bak.old), and process lock safety.
/// </summary>
public static class HeadlessAudioStubService
{
    /// <summary>
    ///     Inspects and aggregates audio stub status across dedicated server targets.
    /// </summary>
    public static AudioStubReport GetAudioStubStatus(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.HeadlessAudioStubService.GetAudioStubStatus(repoRootOrGameDir);

    /// <summary>
    ///     Finds all dedicated server audio target paths within the given repository root or game folder.
    /// </summary>
    public static IReadOnlyList<string> FindServerAudioTargets(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.HeadlessAudioStubService.FindServerAudioTargets(repoRootOrGameDir);

    /// <summary>
    ///     Safely deploys the fmodex64.dll null stub across server targets.
    /// </summary>
    public static AudioStubDeployResult DeployHeadlessStub(
        string repoRootOrGameDir,
        string? candidatePath = null,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.HeadlessAudioStubService.DeployHeadlessStub(
            repoRootOrGameDir, candidatePath, skipProcessCheck, logAction);

    /// <summary>
    ///     Rolls back fmodex64.dll across server targets to previous (.bak) or old (.bak.old) tier.
    /// </summary>
    public static AudioStubRollbackResult RollbackHeadlessStub(
        string repoRootOrGameDir,
        string targetTier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.HeadlessAudioStubService.RollbackHeadlessStub(
            repoRootOrGameDir, targetTier, skipProcessCheck, logAction);
}
