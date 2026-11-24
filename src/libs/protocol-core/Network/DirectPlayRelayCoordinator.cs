// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace Kongor.Core.Network;

/// <summary>
///     Selected DirectPlay P2P transport path based on UPnP IGD mapping, RFC 5389/5780 STUN NAT classification,
///     and configured community relay endpoints.
/// </summary>
public enum DirectPlayTransportStrategy
{
    DirectUpnp = 0,
    StunHolePunch = 1,
    CompelUdpProxyRelay = 2,
    CoturnTurnRelay = 3,
    RatholeUdpTunnel = 4
}

/// <summary>
///     Optional community relay endpoints and parameters for DirectPlay CGNAT / Symmetric NAT fallback.
/// </summary>
public sealed record DirectPlayRelayOptions(
    string? CompelProxyHost = null,
    int CompelProxyPort = 20000,
    string? CoturnHost = null,
    int CoturnPort = 3478,
    int CoturnTlsPort = 5349,
    string CoturnRealm = "p2p.kongor.local",
    string? RatholeServerHost = null,
    int RatholeControlPort = 2333,
    int RatholePublicUdpPort = 20550,
    string RatholeServiceName = "kongor_directplay_udp",
    string RatholeTokenEnvVar = "KONGOR_RATHOLE_TOKEN",
    string TurnSecretEnvVar = "KONGOR_TURN_SECRET",
    int VoicePortOffset = 500);

/// <summary>
///     Complete transport decision, invite recommendation, and turnkey CGNAT relay artifacts for DirectPlay hosting.
/// </summary>
public sealed record DirectPlayTransportPlan(
    DirectPlayTransportStrategy Strategy,
    NatBehaviorClassification NatClassification,
    bool RequiresRelay,
    string RecommendedInviteAddress,
    string? CompelRelayEndPoint,
    string? TurnServerUri,
    string? TurnAllocationProfile,
    string? RatholeClientToml,
    string? RatholeServerToml,
    string DiagnosticSummary,
    string UserGuidance);

/// <summary>
///     Coordinates DirectPlay P2P transport selection and CGNAT-piercing relay fallbacks:
///     (1) Direct UPnP / Open Internet,
///     (2) STUN-assisted UDP hole-punching for Cone NAT,
///     (3) COMPEL's existing UDP proxy relay using its exact 40-byte watermark envelope &amp; challenge framing,
///     (4) coturn RFC 5766/8656 TURN relay (UDP or TLS/TCP when UDP is blocked), and
///     (5) rathole self-hosted UDP tunnel TOML generation.
/// </summary>
public static class DirectPlayRelayCoordinator
{
    // Exact wire-protocol constants mirrored from src/apps/compel/Services/Proxy/UDPForwarder.cs & ClientPacketReader.cs:
    // WATERMARK_LEN_TOTAL (20) + ENHANCED_WATERMARK_LEN_TOTAL (20) = 40 bytes.
    public const int CompelWatermarkPrefixLength = 40;
    public const int CompelChallengeOffset = 28;
    public const int CompelSequenceCounterOffset = 32;
    public const int CompelGameMinimumDatagramLength = 43;
    public const int CompelVoiceMinimumDatagramLength = 41;

    // COMPEL proxy control packet marker (PACKET_PROXY bit 6) and challenge sub-type.
    public const byte CompelProxyPacketFlag = 0x40;
    public const byte CompelChallengePacketType = 0x00;
    public const int CompelChallengePacketLength = 18;
    public const ushort CompelDefaultChallengeExpirySeconds = 60;

    /// <summary>
    ///     Writes a COMPEL-compatible 40-byte watermark datagram (challenge LE @ 28, sequence counter LE @ 32)
    ///     followed immediately by the K2 payload starting at offset 40.
    /// </summary>
    public static bool TryWriteCompelWatermarkDatagram(
        Span<byte> destination,
        uint challenge,
        ushort sequenceCounter,
        ReadOnlySpan<byte> k2Payload,
        out int bytesWritten,
        bool isVoice = false)
    {
        bytesWritten = 0;
        int totalLen = CompelWatermarkPrefixLength + k2Payload.Length;
        int minLen = isVoice ? CompelVoiceMinimumDatagramLength : CompelGameMinimumDatagramLength;

        if (totalLen < minLen || destination.Length < totalLen)
        {
            return false;
        }

        destination.Slice(0, CompelWatermarkPrefixLength).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(CompelChallengeOffset, 4), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(CompelSequenceCounterOffset, 2), sequenceCounter);
        k2Payload.CopyTo(destination.Slice(CompelWatermarkPrefixLength, k2Payload.Length));

        bytesWritten = totalLen;
        return true;
    }

    /// <summary>
    ///     Builds a 43-byte COMPEL relay keepalive/session probe datagram carrying the 40-byte watermark prefix
    ///     (challenge LE @ 28, sequence counter LE @ 32) plus a 3-byte proxy control header ([connId LE, ProxyPacketFlag=0x40]).
    /// </summary>
    public static bool TryBuildCompelKeepaliveDatagram(
        Span<byte> destination,
        uint challenge,
        ushort sequenceCounter,
        out int bytesWritten,
        ushort connectionId = 0xFFFF)
    {
        Span<byte> controlTail = stackalloc byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(controlTail.Slice(0, 2), connectionId);
        controlTail[2] = CompelProxyPacketFlag;

        return TryWriteCompelWatermarkDatagram(
            destination,
            challenge,
            sequenceCounter,
            controlTail,
            out bytesWritten,
            isVoice: false);
    }

    /// <summary>
    ///     Parses a COMPEL 40-byte watermarked client datagram and extracts the echoed challenge, sequence counter,
    ///     and inner K2 payload slice starting at byte 40.
    /// </summary>
    public static bool TryParseCompelWatermarkDatagram(
        ReadOnlySpan<byte> datagram,
        bool isVoice,
        out uint challenge,
        out ushort sequenceCounter,
        out ReadOnlySpan<byte> k2Payload)
    {
        challenge = 0;
        sequenceCounter = 0;
        k2Payload = ReadOnlySpan<byte>.Empty;

        int minLen = isVoice ? CompelVoiceMinimumDatagramLength : CompelGameMinimumDatagramLength;
        if (datagram.Length < minLen)
        {
            return false;
        }

        challenge = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(CompelChallengeOffset, 4));
        sequenceCounter = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(CompelSequenceCounterOffset, 2));
        k2Payload = datagram.Slice(CompelWatermarkPrefixLength);
        return true;
    }

    /// <summary>
    ///     Builds an exact 18-byte COMPEL proxy challenge packet matching UDPForwarder.BuildChallengePacket:
    ///     [0xFF, 0xFF, 0x40, 0x00, timestamp LE (4B), expiry LE (2B), packetQuota LE (2B), cmdQuota LE (2B), challenge LE (4B)].
    /// </summary>
    public static bool TryBuildCompelChallengePacket(
        Span<byte> destination,
        uint serverCreationTimestamp,
        uint challengeValue,
        ushort packetQuota,
        ushort gameCommandQuota,
        out int bytesWritten,
        ushort expirySeconds = CompelDefaultChallengeExpirySeconds)
    {
        bytesWritten = 0;
        if (destination.Length < CompelChallengePacketLength)
        {
            return false;
        }

        destination[0] = 0xFF;
        destination[1] = 0xFF;
        destination[2] = CompelProxyPacketFlag;
        destination[3] = CompelChallengePacketType;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), serverCreationTimestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8, 2), expirySeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(10, 2), packetQuota);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(12, 2), gameCommandQuota);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(14, 4), challengeValue);

        bytesWritten = CompelChallengePacketLength;
        return true;
    }

    /// <summary>
    ///     Parses an 18-byte COMPEL proxy challenge packet issued by UDPForwarder.
    /// </summary>
    public static bool TryParseCompelChallengePacket(
        ReadOnlySpan<byte> packet,
        out uint serverCreationTimestamp,
        out ushort expirySeconds,
        out ushort packetQuota,
        out ushort gameCommandQuota,
        out uint challengeValue)
    {
        serverCreationTimestamp = 0;
        expirySeconds = 0;
        packetQuota = 0;
        gameCommandQuota = 0;
        challengeValue = 0;

        if (packet.Length < CompelChallengePacketLength ||
            packet[0] != 0xFF ||
            packet[1] != 0xFF ||
            packet[2] != CompelProxyPacketFlag ||
            packet[3] != CompelChallengePacketType)
        {
            return false;
        }

        serverCreationTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4));
        expirySeconds = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2));
        packetQuota = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
        gameCommandQuota = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(12, 2));
        challengeValue = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(14, 4));
        return true;
    }

    /// <summary>
    ///     Evaluates UPnP mapping results, 2-endpoint STUN NAT behavior, and configured community relays to produce
    ///     a deterministic DirectPlay transport plan and turnkey CGNAT relay artifacts.
    /// </summary>
    public static DirectPlayTransportPlan PlanTransport(
        int hostPort,
        string localIp,
        UpnpPortMappingResult? upnpResult,
        StunProbeResult? stunResult,
        DirectPlayRelayOptions? options = null,
        Action<string>? logSink = null)
    {
        int safeHostPort = hostPort is >= 1 and <= 65535 ? hostPort : 11550;
        string safeLocalIp = string.IsNullOrWhiteSpace(localIp) ? "127.0.0.1" : localIp.Trim();
        DirectPlayRelayOptions opts = options ?? new DirectPlayRelayOptions();
        int safeVoicePort = Math.Clamp(safeHostPort + opts.VoicePortOffset, 1, 65535);

        NatBehaviorClassification natClass = stunResult?.Classification ?? NatBehaviorClassification.Unknown;
        bool upnpSucceeded = upnpResult?.Succeeded == true;

        bool upnpExternalIsCgnatOrPrivate = false;
        if (upnpSucceeded &&
            !string.IsNullOrWhiteSpace(upnpResult?.ExternalIpAddress) &&
            IPAddress.TryParse(upnpResult.ExternalIpAddress, out IPAddress? parsedUpnpIp))
        {
            upnpExternalIsCgnatOrPrivate = StunNatClassifier.IsCgnatOrPrivateAddress(parsedUpnpIp);
        }

        if (upnpSucceeded && upnpExternalIsCgnatOrPrivate)
        {
            Log(logSink, $"[P2P-RELAY] [WARN] UPnP succeeded on LAN router, but reported WAN IP '{upnpResult!.ExternalIpAddress}' is private/CGNAT — promoting classification to SymmetricOrCgnat.");
            if (natClass is NatBehaviorClassification.Unknown or NatBehaviorClassification.ConeNat or NatBehaviorClassification.OpenInternet)
            {
                natClass = NatBehaviorClassification.SymmetricOrCgnat;
            }
        }

        string effectiveRatholeHost = !string.IsNullOrWhiteSpace(opts.RatholeServerHost)
            ? SanitizeHostToken(opts.RatholeServerHost, "relay.example.com")
            : "relay.example.com";
        string effectiveCoturnHost = !string.IsNullOrWhiteSpace(opts.CoturnHost)
            ? SanitizeHostToken(opts.CoturnHost, "relay.example.com")
            : "relay.example.com";
        string effectiveCompelHost = !string.IsNullOrWhiteSpace(opts.CompelProxyHost)
            ? SanitizeHostToken(opts.CompelProxyHost, string.Empty)
            : string.Empty;

        string ratholeTokenPlaceholder = $"${{{SanitizeEnvVarName(opts.RatholeTokenEnvVar, "KONGOR_RATHOLE_TOKEN")}}}";
        string turnSecretPlaceholder = $"${{{SanitizeEnvVarName(opts.TurnSecretEnvVar, "KONGOR_TURN_SECRET")}}}";
        bool preferTcpOrTlsTurn = natClass == NatBehaviorClassification.UdpBlocked;

        string? compelRelayEndPoint = !string.IsNullOrEmpty(effectiveCompelHost)
            ? $"{effectiveCompelHost}:{Math.Clamp(opts.CompelProxyPort, 1, 65535)}"
            : null;
        string turnUri = BuildCoturnUri(effectiveCoturnHost, opts.CoturnPort, preferTcpOrTlsTurn, opts.CoturnTlsPort);
        string turnProfile = BuildCoturnAllocationProfile(
            effectiveCoturnHost,
            opts.CoturnPort,
            opts.CoturnTlsPort,
            opts.CoturnRealm,
            preferTcpOrTlsTurn,
            turnSecretPlaceholder);
        string ratholeClientToml = BuildRatholeClientToml(
            effectiveRatholeHost,
            opts.RatholeControlPort,
            safeHostPort,
            safeVoicePort,
            opts.RatholeServiceName,
            ratholeTokenPlaceholder);
        string ratholeServerToml = BuildRatholeServerToml(
            opts.RatholeControlPort,
            opts.RatholePublicUdpPort,
            Math.Clamp(opts.RatholePublicUdpPort + opts.VoicePortOffset, 1, 65535),
            opts.RatholeServiceName,
            ratholeTokenPlaceholder);

        // 1. Open Internet (direct public socket, zero NAT translation)
        if (natClass == NatBehaviorClassification.OpenInternet)
        {
            string invite = stunResult?.PrimaryMappedEndPoint is not null
                ? FormatEndPoint(stunResult.PrimaryMappedEndPoint)
                : FormatHostPort(upnpResult?.ExternalIpAddress ?? safeLocalIp, safeHostPort);

            DirectPlayTransportPlan plan = new DirectPlayTransportPlan(
                DirectPlayTransportStrategy.DirectUpnp,
                natClass,
                RequiresRelay: false,
                RecommendedInviteAddress: invite,
                CompelRelayEndPoint: compelRelayEndPoint,
                TurnServerUri: null,
                TurnAllocationProfile: null,
                RatholeClientToml: null,
                RatholeServerToml: null,
                DiagnosticSummary: $"Direct WAN UDP ready (Open Internet) on {invite}; no relay required.",
                UserGuidance: $"Share '{invite}' directly with friends. No relay needed.");
            LogPlan(logSink, plan);
            return plan;
        }

        // 2. UPnP IGD Port Mapping succeeded with genuine public WAN IP (not blocked by CGNAT/Symmetric/UDP block)
        if (upnpSucceeded &&
            !upnpExternalIsCgnatOrPrivate &&
            natClass is not NatBehaviorClassification.SymmetricOrCgnat and not NatBehaviorClassification.UdpBlocked)
        {
            string wanIp = !string.IsNullOrWhiteSpace(upnpResult!.ExternalIpAddress)
                ? upnpResult.ExternalIpAddress!
                : stunResult?.ExternalIpAddress ?? safeLocalIp;
            string invite = FormatHostPort(wanIp, upnpResult.ExternalPort);

            DirectPlayTransportPlan plan = new DirectPlayTransportPlan(
                DirectPlayTransportStrategy.DirectUpnp,
                natClass,
                RequiresRelay: false,
                RecommendedInviteAddress: invite,
                CompelRelayEndPoint: compelRelayEndPoint,
                TurnServerUri: null,
                TurnAllocationProfile: null,
                RatholeClientToml: null,
                RatholeServerToml: null,
                DiagnosticSummary: $"Direct UPnP UDP mapping active on {invite} (NAT={natClass}).",
                UserGuidance: $"Router UPnP forwarded UDP {upnpResult.ExternalPort}. Share '{invite}' with friends.");
            LogPlan(logSink, plan);
            return plan;
        }

        // 3. Cone NAT (Endpoint-Independent Mapping) without UPnP -> STUN UDP hole-punching viable (with standby relay artifacts)
        if (natClass == NatBehaviorClassification.ConeNat)
        {
            string mappedInvite = stunResult?.PrimaryMappedEndPoint is not null
                ? FormatEndPoint(stunResult.PrimaryMappedEndPoint)
                : FormatHostPort(safeLocalIp, safeHostPort);

            DirectPlayTransportPlan plan = new DirectPlayTransportPlan(
                DirectPlayTransportStrategy.StunHolePunch,
                natClass,
                RequiresRelay: false,
                RecommendedInviteAddress: mappedInvite,
                CompelRelayEndPoint: compelRelayEndPoint,
                TurnServerUri: !string.IsNullOrWhiteSpace(opts.CoturnHost) ? turnUri : null,
                TurnAllocationProfile: !string.IsNullOrWhiteSpace(opts.CoturnHost) ? turnProfile : null,
                RatholeClientToml: ratholeClientToml,
                RatholeServerToml: ratholeServerToml,
                DiagnosticSummary: $"Cone NAT (EIM) detected without UPnP; STUN hole-punch candidate {mappedInvite} (relay standby available).",
                UserGuidance: $"Enable UPnP or forward UDP {safeHostPort} for easiest hosting; STUN hole-punch endpoint is '{mappedInvite}'.");
            LogPlan(logSink, plan);
            return plan;
        }

        // 4. Symmetric NAT, RFC 6598 CGNAT, or UDP-Blocked -> Relay REQUIRED
        if (natClass is NatBehaviorClassification.SymmetricOrCgnat or NatBehaviorClassification.UdpBlocked)
        {
            // 4a. Prefer existing COMPEL UDP Proxy Relay when configured and outbound UDP is not blocked
            if (compelRelayEndPoint is not null && natClass != NatBehaviorClassification.UdpBlocked)
            {
                DirectPlayTransportPlan plan = new DirectPlayTransportPlan(
                    DirectPlayTransportStrategy.CompelUdpProxyRelay,
                    natClass,
                    RequiresRelay: true,
                    RecommendedInviteAddress: compelRelayEndPoint,
                    CompelRelayEndPoint: compelRelayEndPoint,
                    TurnServerUri: turnUri,
                    TurnAllocationProfile: turnProfile,
                    RatholeClientToml: ratholeClientToml,
                    RatholeServerToml: ratholeServerToml,
                    DiagnosticSummary: $"CGNAT/Symmetric NAT requires relay -> routing via COMPEL UDP Proxy ({compelRelayEndPoint}) using 40-byte watermark framing.",
                    UserGuidance: $"Your ISP uses CGNAT/Symmetric NAT. Connect via COMPEL relay '{compelRelayEndPoint}' (friends join this relay address).");
                LogPlan(logSink, plan);
                return plan;
            }

            // 4b. Prefer coturn TURN relay when configured OR when outbound UDP is blocked (TLS/TCP 5349 traversal)
            if (!string.IsNullOrWhiteSpace(opts.CoturnHost) || natClass == NatBehaviorClassification.UdpBlocked)
            {
                string invite = !string.IsNullOrWhiteSpace(opts.RatholeServerHost)
                    ? $"{effectiveRatholeHost}:{Math.Clamp(opts.RatholePublicUdpPort, 1, 65535)}"
                    : turnUri;

                DirectPlayTransportPlan plan = new DirectPlayTransportPlan(
                    DirectPlayTransportStrategy.CoturnTurnRelay,
                    natClass,
                    RequiresRelay: true,
                    RecommendedInviteAddress: invite,
                    CompelRelayEndPoint: compelRelayEndPoint,
                    TurnServerUri: turnUri,
                    TurnAllocationProfile: turnProfile,
                    RatholeClientToml: ratholeClientToml,
                    RatholeServerToml: ratholeServerToml,
                    DiagnosticSummary: natClass == NatBehaviorClassification.UdpBlocked
                        ? $"Outbound UDP blocked -> recommending coturn TLS/TCP TURN relay ({turnUri}) or TCP-backed rathole tunnel."
                        : $"CGNAT/Symmetric NAT detected -> routing via coturn TURN relay ({turnUri}).",
                    UserGuidance: $"Direct UDP hosting is blocked ({natClass}). Deploy infra/docker/docker-compose.p2p-relay.yml and configure {turnUri}.");
                LogPlan(logSink, plan);
                return plan;
            }

            // 4c. Default turnkey CGNAT piercer: rathole UDP tunnel over persistent TCP control channel
            string ratholeInvite = $"{effectiveRatholeHost}:{Math.Clamp(opts.RatholePublicUdpPort, 1, 65535)}";
            DirectPlayTransportPlan ratholePlan = new DirectPlayTransportPlan(
                DirectPlayTransportStrategy.RatholeUdpTunnel,
                natClass,
                RequiresRelay: true,
                RecommendedInviteAddress: ratholeInvite,
                CompelRelayEndPoint: compelRelayEndPoint,
                TurnServerUri: turnUri,
                TurnAllocationProfile: turnProfile,
                RatholeClientToml: ratholeClientToml,
                RatholeServerToml: ratholeServerToml,
                DiagnosticSummary: $"CGNAT/Symmetric NAT detected -> generated turnkey rathole UDP tunnel config (public endpoint {ratholeInvite} -> 127.0.0.1:{safeHostPort}).",
                UserGuidance: $"Direct port-forwarding is blocked by CGNAT/Symmetric NAT. Run rathole client with the generated TOML against infra/docker/docker-compose.p2p-relay.yml and share '{ratholeInvite}'.");
            LogPlan(logSink, ratholePlan);
            return ratholePlan;
        }

        // 5. Unknown NAT (e.g. offline/unprobed STUN and UPnP failed) -> fallback to configured relay or LAN/VPN direct
        if (compelRelayEndPoint is not null)
        {
            DirectPlayTransportPlan plan = new DirectPlayTransportPlan(
                DirectPlayTransportStrategy.CompelUdpProxyRelay,
                natClass,
                RequiresRelay: true,
                RecommendedInviteAddress: compelRelayEndPoint,
                CompelRelayEndPoint: compelRelayEndPoint,
                TurnServerUri: turnUri,
                TurnAllocationProfile: turnProfile,
                RatholeClientToml: ratholeClientToml,
                RatholeServerToml: ratholeServerToml,
                DiagnosticSummary: $"NAT unclassified and UPnP unavailable -> standby COMPEL UDP Proxy relay ready ({compelRelayEndPoint}).",
                UserGuidance: $"Use LAN IP '{safeLocalIp}:{safeHostPort}' locally or COMPEL relay '{compelRelayEndPoint}' for WAN.");
            LogPlan(logSink, plan);
            return plan;
        }

        string fallbackInvite = FormatHostPort(stunResult?.ExternalIpAddress ?? safeLocalIp, safeHostPort);
        DirectPlayTransportPlan fallbackPlan = new DirectPlayTransportPlan(
            DirectPlayTransportStrategy.StunHolePunch,
            natClass,
            RequiresRelay: false,
            RecommendedInviteAddress: fallbackInvite,
            CompelRelayEndPoint: compelRelayEndPoint,
            TurnServerUri: !string.IsNullOrWhiteSpace(opts.CoturnHost) ? turnUri : null,
            TurnAllocationProfile: !string.IsNullOrWhiteSpace(opts.CoturnHost) ? turnProfile : null,
            RatholeClientToml: ratholeClientToml,
            RatholeServerToml: ratholeServerToml,
            DiagnosticSummary: $"NAT classification pending/offline and UPnP unavailable; LAN/direct fallback {fallbackInvite} (relay templates generated).",
            UserGuidance: $"Share LAN/VPN invite '{safeLocalIp}:{safeHostPort}', enable router UPnP, or use infra/docker/docker-compose.p2p-relay.yml for WAN.");
        LogPlan(logSink, fallbackPlan);
        return fallbackPlan;
    }

    /// <summary>
    ///     Generates a ready-to-use rathole v0.5 client TOML mapping local DirectPlay game (and optional voice) UDP ports
    ///     through a TCP control channel to pierce Symmetric NAT and CGNAT without router port forwarding.
    /// </summary>
    public static string BuildRatholeClientToml(
        string serverHost,
        int controlPort,
        int localGamePort,
        int? localVoicePort = null,
        string serviceName = "kongor_directplay_udp",
        string tokenPlaceholder = "${KONGOR_RATHOLE_TOKEN}")
    {
        string safeHost = SanitizeHostToken(serverHost, "relay.example.com");
        int safeControlPort = Math.Clamp(controlPort, 1, 65535);
        int safeGamePort = Math.Clamp(localGamePort, 1, 65535);
        string safeService = SanitizeServiceName(serviceName, "kongor_directplay_udp");
        string safeToken = SanitizePlaceholderToken(tokenPlaceholder, "${KONGOR_RATHOLE_TOKEN}");

        StringBuilder sb = new StringBuilder(512);
        sb.AppendLine("# Generated by Project KONGOR DirectPlayRelayCoordinator (rathole v0.5+ client)");
        sb.AppendLine("[client]");
        sb.Append("remote_addr = \"").Append(safeHost).Append(':').Append(safeControlPort.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");
        sb.Append("default_token = \"").Append(safeToken).AppendLine("\"");
        sb.AppendLine("heartbeat_timeout = 40");
        sb.AppendLine("retry_interval = 2");
        sb.AppendLine();
        sb.AppendLine("[client.transport]");
        sb.AppendLine("type = \"tcp\"");
        sb.AppendLine();
        sb.AppendLine("[client.transport.tcp]");
        sb.AppendLine("nodelay = true");
        sb.AppendLine("keepalive_secs = 20");
        sb.AppendLine("keepalive_interval = 8");
        sb.AppendLine();
        sb.Append("[client.services.").Append(safeService).AppendLine("]");
        sb.AppendLine("type = \"udp\"");
        sb.Append("local_addr = \"127.0.0.1:").Append(safeGamePort.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");

        if (localVoicePort.HasValue && localVoicePort.Value is >= 1 and <= 65535)
        {
            sb.AppendLine();
            sb.Append("[client.services.").Append(safeService).AppendLine("_voice]");
            sb.AppendLine("type = \"udp\"");
            sb.Append("local_addr = \"127.0.0.1:").Append(localVoicePort.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generates a matching rathole v0.5 server TOML exposing public UDP ports for DirectPlay guests.
    /// </summary>
    public static string BuildRatholeServerToml(
        int controlBindPort,
        int publicGameUdpPort,
        int? publicVoiceUdpPort = null,
        string serviceName = "kongor_directplay_udp",
        string tokenPlaceholder = "${KONGOR_RATHOLE_TOKEN}")
    {
        int safeControlPort = Math.Clamp(controlBindPort, 1, 65535);
        int safePublicGamePort = Math.Clamp(publicGameUdpPort, 1, 65535);
        string safeService = SanitizeServiceName(serviceName, "kongor_directplay_udp");
        string safeToken = SanitizePlaceholderToken(tokenPlaceholder, "${KONGOR_RATHOLE_TOKEN}");

        StringBuilder sb = new StringBuilder(512);
        sb.AppendLine("# Generated by Project KONGOR DirectPlayRelayCoordinator (rathole v0.5+ server)");
        sb.AppendLine("[server]");
        sb.Append("bind_addr = \"0.0.0.0:").Append(safeControlPort.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");
        sb.Append("default_token = \"").Append(safeToken).AppendLine("\"");
        sb.AppendLine("heartbeat_interval = 30");
        sb.AppendLine();
        sb.AppendLine("[server.transport]");
        sb.AppendLine("type = \"tcp\"");
        sb.AppendLine();
        sb.AppendLine("[server.transport.tcp]");
        sb.AppendLine("nodelay = true");
        sb.AppendLine("keepalive_secs = 20");
        sb.AppendLine("keepalive_interval = 8");
        sb.AppendLine();
        sb.Append("[server.services.").Append(safeService).AppendLine("]");
        sb.AppendLine("type = \"udp\"");
        sb.Append("bind_addr = \"0.0.0.0:").Append(safePublicGamePort.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");

        if (publicVoiceUdpPort.HasValue && publicVoiceUdpPort.Value is >= 1 and <= 65535)
        {
            sb.AppendLine();
            sb.Append("[server.services.").Append(safeService).AppendLine("_voice]");
            sb.AppendLine("type = \"udp\"");
            sb.Append("bind_addr = \"0.0.0.0:").Append(publicVoiceUdpPort.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("\"");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Builds a standards-compliant RFC 7065 TURN / TURNS URI for coturn relay allocation.
    /// </summary>
    public static string BuildCoturnUri(
        string coturnHost,
        int port = 3478,
        bool preferTcpOrTls = false,
        int tlsPort = 5349)
    {
        string safeHost = SanitizeHostToken(coturnHost, "relay.example.com");
        string formattedHost = safeHost.Contains(':', StringComparison.Ordinal) && !safeHost.StartsWith('[')
            ? $"[{safeHost}]"
            : safeHost;

        if (preferTcpOrTls)
        {
            int safeTlsPort = Math.Clamp(tlsPort, 1, 65535);
            return $"turns:{formattedHost}:{safeTlsPort.ToString(CultureInfo.InvariantCulture)}?transport=tcp";
        }

        int safeUdpPort = Math.Clamp(port, 1, 65535);
        return $"turn:{formattedHost}:{safeUdpPort.ToString(CultureInfo.InvariantCulture)}?transport=udp";
    }

    /// <summary>
    ///     Generates a coturn TURN REST/HMAC ephemeral allocation profile (zero hardcoded secrets).
    /// </summary>
    public static string BuildCoturnAllocationProfile(
        string coturnHost,
        int port = 3478,
        int tlsPort = 5349,
        string realm = "p2p.kongor.local",
        bool preferTcpOrTls = false,
        string secretPlaceholder = "${KONGOR_TURN_SECRET}")
    {
        string primaryUri = BuildCoturnUri(coturnHost, port, preferTcpOrTls, tlsPort);
        string fallbackUri = BuildCoturnUri(coturnHost, port, !preferTcpOrTls, tlsPort);
        string safeRealm = SanitizeHostToken(realm, "p2p.kongor.local");
        string safeSecret = SanitizePlaceholderToken(secretPlaceholder, "${KONGOR_TURN_SECRET}");

        StringBuilder sb = new StringBuilder(384);
        sb.AppendLine("# Generated by Project KONGOR DirectPlayRelayCoordinator (coturn RFC 5766/8656 profile)");
        sb.Append("primary_uri = \"").Append(primaryUri).AppendLine("\"");
        sb.Append("fallback_uri = \"").Append(fallbackUri).AppendLine("\"");
        sb.Append("realm = \"").Append(safeRealm).AppendLine("\"");
        sb.AppendLine("auth_mechanism = \"use-auth-secret\"");
        sb.Append("shared_secret_env = \"").Append(safeSecret).AppendLine("\"");
        sb.AppendLine("credential_ttl_seconds = 3600");
        sb.AppendLine("relay_port_range = \"49152-49252\"");
        return sb.ToString();
    }

    private static string FormatEndPoint(IPEndPoint endPoint)
    {
        IPAddress addr = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        return addr.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{addr}]:{endPoint.Port}"
            : $"{addr}:{endPoint.Port}";
    }

    private static string FormatHostPort(string host, int port)
    {
        string trimmed = host.Trim();
        if (trimmed.Contains(':', StringComparison.Ordinal) && !trimmed.StartsWith('['))
        {
            return $"[{trimmed}]:{port}";
        }

        return $"{trimmed}:{port}";
    }

    private static string SanitizeHostToken(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static string SanitizeServiceName(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static string SanitizeEnvVarName(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static string SanitizePlaceholderToken(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (!char.IsControl(ch) && ch is not '"' and not '\'' and not '\r' and not '\n')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static void LogPlan(Action<string>? logSink, DirectPlayTransportPlan plan)
    {
        Log(logSink, $"[P2P-RELAY] Strategy={plan.Strategy} | NAT={plan.NatClassification} | RequiresRelay={plan.RequiresRelay} | Invite={plan.RecommendedInviteAddress} | {plan.DiagnosticSummary}");
    }

    private static void Log(Action<string>? logSink, string message)
    {
        Trace.WriteLine(message);
        logSink?.Invoke(message);
    }
}
