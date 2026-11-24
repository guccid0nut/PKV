// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeSecurityUpgradeService.cs
// System: WILLOWMAKER - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 4 Modern TLS 1.3 ABI Proxy Shim & libcurl 8.8+ Upgrade
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Core.Services.Native;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Willowmaker application facade for native security binary governance,
///     modern TLS 1.3 verification, OpenSSL ABI compliance, and two-tier rollback.
/// </summary>
public static class NativeSecurityUpgradeService
{
    /// <summary>
    ///     Inspects and aggregates security compliance, versions, and backup tiers for all target binaries.
    /// </summary>
    public static SecurityUpgradeReport GetSecurityStatus(string repoRootOrGameDir, TargetFleet fleet = TargetFleet.All)
        => Kongor.Core.Services.Native.NativeSecurityUpgradeService.GetSecurityStatus(repoRootOrGameDir, fleet);

    /// <summary>
    ///     Finds all target paths for security binaries within the given repository root or game folder.
    /// </summary>
    public static IReadOnlyList<string> FindSecurityTargets(string repoRootOrGameDir, TargetFleet fleet = TargetFleet.All)
        => Kongor.Core.Services.Native.NativeSecurityUpgradeService.FindSecurityTargets(repoRootOrGameDir, fleet);

    /// <summary>
    ///     Safely upgrades native security binaries across all discovered target paths with two-tier backup rotation.
    /// </summary>
    public static SecurityUpgradeResult UpgradeSecurityBinaries(
        string repoRootOrGameDir,
        string? candidateDir = null,
        bool skipProcessCheck = false,
        bool skipExportCheck = false,
        bool redirectIat = false,
        TargetFleet fleet = TargetFleet.All,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.NativeSecurityUpgradeService.UpgradeSecurityBinaries(
            repoRootOrGameDir, candidateDir, skipProcessCheck, skipExportCheck, redirectIat, fleet, logAction);

    /// <summary>
    ///     Rolls back security binaries across all targets to previous (.bak) or old (.bak.old) tier.
    /// </summary>
    public static RollbackResult RollbackSecurityBinaries(
        string repoRootOrGameDir,
        string targetTier = "previous",
        bool skipProcessCheck = false,
        TargetFleet fleet = TargetFleet.All,
        Action<string>? logAction = null)
        => Kongor.Core.Services.Native.NativeSecurityUpgradeService.RollbackSecurityBinaries(
            repoRootOrGameDir, targetTier, skipProcessCheck, fleet, logAction);

    /// <summary>
    ///     Performs a cryptographic TLS 1.3 handshake verification against a target HTTPS URL.
    /// </summary>
    public static Task<TlsHandshakeResult> VerifyTls13HandshakeAsync(
        string targetUrl = "https://masterserver.naeu.heroesofnewerth.com",
        CancellationToken ct = default)
        => Kongor.Core.Services.Native.NativeSecurityUpgradeService.VerifyTls13HandshakeAsync(targetUrl, ct);
}
