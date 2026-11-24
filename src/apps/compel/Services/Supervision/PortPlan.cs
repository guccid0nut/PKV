// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Services.Supervision;

/// <summary>
///     Computes the game, voice, and ping port allocation for the match server manager from the instance count, port-range
///     offset, and whether the proxy is enabled.
///     This is the single source of truth for COMPEL's port arithmetic, used by the supervisor, the proxy, the ping
///     responder, and the status endpoint.
/// </summary>
public sealed class PortPlan(int instances, int offset, bool useProxy)
{
    /// <summary>
    ///     The first game-server port when the offset is zero.
    /// </summary>
    public const int BaseGamePort = 11235;

    /// <summary>
    ///     The first voice-server port when the offset is zero.
    ///     Positioned at 11735 (BaseGamePort + 500) to ensure complete deconfliction with
    ///     all 3 fleet tiers (Local 0..99, Docker 100..199, K3s 200..299: 11235..11534)
    ///     and the COMPEL Voice Relay service at 11600.
    /// </summary>
    public const int BaseVoicePort = 11735;

    /// <summary>
    ///     The default voice relay port when COMPEL voice relay is enabled.
    ///     Positioned at 11600 to prevent collision with game server ports (11235..11534)
    ///     and dedicated voice server ports (11735..12034).
    /// </summary>
    public const int BaseVoiceRelayPort = 11600;

    /// <summary>
    ///     The port on which COMPEL answers master-server pings when the offset is zero and the proxy is disabled.
    /// </summary>
    public const int BasePingPort = 11234;

    /// <summary>
    ///     The amount by which the proxy's public ports sit above the local server ports. The resulting 20000-29999 range is
    ///     where Heroes Of Newerth supports anti-DDoS and anti-cheat protection natively.
    /// </summary>
    public const int ProxyPublicOffset = 10000;

    /// <summary>
    ///     The standard tier partition size (number of port slots reserved per fleet tier).
    /// </summary>
    public const int TierPartitionSize = 100;

    /// <summary>
    ///     The width of the valid game and voice port windows across all tiers (Local, Docker, K3s);
    ///     the offset plus the instance count must fit within it.
    /// </summary>
    public const int PortRangeWindow = 300;

    public PortPlan(MatchServerManagerOptions options) : this(options.Instances, options.PortRangeOffset,
        options.UseProxy)
    {
    }

    public int Instances { get; } = instances;

    public int Offset { get; } = offset;

    public bool UseProxy { get; } = useProxy;

    // Local Ports: The Ports The Game Servers Actually Bind. The Manager Always Allocates These In The 112xx / 114xx Range, Regardless Of Whether The Proxy Is Enabled.

    public int LocalGameStart => BaseGamePort + Offset;

    public int LocalGameEnd => LocalGameStart + Instances - 1;

    public int LocalVoiceStart => BaseVoicePort + Offset;

    public int LocalVoiceEnd => LocalVoiceStart + Instances - 1;

    // Public Ports: The Ports Clients Connect To. Without The Proxy They Equal The Local Ports; With The Proxy They Are Offset Above Them, And The Proxy Forwards Them Down To The Local Ports.

    public int PublicGameStart => UseProxy ? LocalGameStart + ProxyPublicOffset : LocalGameStart;

    public int PublicGameEnd => UseProxy ? LocalGameEnd + ProxyPublicOffset : LocalGameEnd;

    public int PublicVoiceStart => UseProxy ? LocalVoiceStart + ProxyPublicOffset : LocalVoiceStart;

    public int PublicVoiceEnd => UseProxy ? LocalVoiceEnd + ProxyPublicOffset : LocalVoiceEnd;

    /// <summary>
    ///     The port on which COMPEL answers master-server pings. With the proxy enabled it sits in the public anti-cheat
    ///     range.
    /// </summary>
    public int PingPort => (UseProxy ? BasePingPort + ProxyPublicOffset : BasePingPort) + Offset;
}
