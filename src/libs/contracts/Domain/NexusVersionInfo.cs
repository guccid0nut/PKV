// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Centralized Single Source of Truth for Platform Versioning across the NEXUS ecosystem.
///     Combines SemVer 2.0 (Platform Generation), CalVer (Build Stamp), and Legacy Engine Protocol Compatibility.
/// </summary>
public static class NexusVersionInfo
{
    /// <summary>
    ///     NEXUS Monorepo Major Platform Version (SemVer 2.0).
    /// </summary>
    public const string PlatformVersion = "5.0.2";

    /// <summary>
    ///     Calendar Build Release Date (CalVer).
    /// </summary>
    public const string BuildDate = "2026.11.21";

    /// <summary>
    ///     NEXUS Game Engine Protocol Version.
    ///     Modernized K2 engine protocol with DXVK Vulkan 1.3, 1000 FPS tickrate, and memory caching.
    /// </summary>
    public const string EngineProtocolVersion = "5.0.0";

    /// <summary>
    ///     Minimum backwards-compatible legacy engine protocol version supported by the platform.
    ///     Ensures legacy HoN 4.10.1 game client assets and legacy mods operate seamlessly.
    /// </summary>
    public const string MinimumCompatibleEngineProtocolVersion = "4.10.1";

    /// <summary>
    ///     Clean Monorepo Default Directory Name for Game Client assets & binaries.
    /// </summary>
    public const string DefaultClientDirName = "games/client";

    /// <summary>
    ///     Clean Monorepo Default Directory Name for Game Server assets & binaries.
    /// </summary>
    public const string DefaultServerDirName = "games/server";

    /// <summary>
    ///     Full User-Facing Version Display String for GUI Headers, Log Outputs, and Health Endpoints.
    /// </summary>
    public const string FullDisplayVersion = "NEXUS v5.0.2 (Build 2026.11.21 - Engine Protocol v5.0.0)";

    /// <summary>
    ///     Informational Version Header string for HTTP/gRPC requests.
    /// </summary>
    public const string InformationalHeaderVersion = "v5.0.2+2026.11.21 (Engine Protocol v5.0.0)";
}
