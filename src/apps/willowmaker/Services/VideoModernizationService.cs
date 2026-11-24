// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoModernizationService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 9 Hardware-Accelerated Video Decoding (FFmpeg 7.x / DXVA2)
// ============================================================================

using System.Collections.Generic;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for video pipeline modernization, hardware acceleration,
///     dedicated server bloat purge, and process safety.
/// </summary>
public static class VideoModernizationService
{
    public static IReadOnlyList<string> FindClientVideoTargets(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.VideoModernizationService.FindClientVideoTargets(repoRootOrGameDir);

    public static IReadOnlyList<string> FindServerVideoTargets(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.VideoModernizationService.FindServerVideoTargets(repoRootOrGameDir);

    public static VideoModernizationReport GetVideoStatus(string repoRootOrGameDir)
        => Kongor.Core.Services.Native.VideoModernizationService.GetVideoStatus(repoRootOrGameDir);

    public static ServerVideoPurgeResult PurgeServerVideoBloat(string repoRootOrGameDir, bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.VideoModernizationService.PurgeServerVideoBloat(repoRootOrGameDir, skipProcessCheck);

    public static ServerVideoPurgeResult RestoreServerVideo(string repoRootOrGameDir, bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.VideoModernizationService.RestoreServerVideo(repoRootOrGameDir, skipProcessCheck);

    public static VideoDeployResult DeployClientVideoUpgrade(string repoRootOrGameDir, string stagedBinariesDir, bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.VideoModernizationService.DeployClientVideoUpgrade(repoRootOrGameDir, stagedBinariesDir, skipProcessCheck);

    public static VideoRollbackResult RollbackClientVideo(string repoRootOrGameDir, bool skipProcessCheck = false)
        => Kongor.Core.Services.Native.VideoModernizationService.RollbackClientVideo(repoRootOrGameDir, skipProcessCheck);
}
