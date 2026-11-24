// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;

namespace ASPIRE.Common.Netcode.Ebpf;

/// <summary>
///     Action returned by the eBPF/XDP filter program.
/// </summary>
internal enum XdpAction
{
    Pass = 0,
    Drop = 1
}

/// <summary>
///     Statistics counters recorded by the eBPF kernel program.
/// </summary>
internal sealed class EbpfFilterStats
{
    public long PacketsTotal { get; set; }
    public long PacketsPassed { get; set; }
    public long PacketsDroppedMalformed { get; set; }
    public long PacketsDroppedMagic { get; set; }
    public long PacketsDroppedRateLimit { get; set; }
    public long BytesProcessed { get; set; }
}

/// <summary>
///     [EXPERIMENTAL - NOT DEPLOYED]
///     Cross-platform managed verifier replicating the exact kernel logic executed by <c>kongor_xdp_filter.bpf.c</c>.
///     Validates minimum K2 UDP frame header length (>= 3 payload bytes), drops all-zero amplification probes,
///     and enforces per-IP token-bucket burst limits without imposing a bogus ASCII magic header that would drop real K2 traffic.
/// </summary>
internal sealed class EbpfPacketFilterVerifier
{
    public const int MinK2PayloadBytes = 3; // 2-byte K2 sequence/OOB header + 1-byte packet opcode
    public const uint NullAmplificationProbe = 0x00000000U;
    public const ushort PortMin = 11235;
    public const ushort PortMax = 11238;

    private readonly EbpfFilterStats _stats = new();
    private readonly Dictionary<uint, (long LastTimeMs, int Tokens)> _rateLimits = [];

    public EbpfFilterStats Stats => _stats;

    /// <summary>
    ///     Processes an incoming raw Layer 2 Ethernet frame through the XDP filter rules.
    /// </summary>
    public XdpAction ProcessPacket(ReadOnlySpan<byte> packet, long currentTimeMs = 0)
    {
        _stats.PacketsTotal++;
        _stats.BytesProcessed += packet.Length;

        // 1. Ethernet Header (14 bytes)
        if (packet.Length < 14)
        {
            _stats.PacketsDroppedMalformed++;
            return XdpAction.Drop;
        }

        ushort ethType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(12, 2));
        if (ethType != 0x0800) // Not IPv4 (e.g. ARP, IPv6)
        {
            _stats.PacketsPassed++;
            return XdpAction.Pass;
        }

        // 2. IPv4 Header
        ReadOnlySpan<byte> ipPacket = packet[14..];
        if (ipPacket.Length < 20)
        {
            _stats.PacketsDroppedMalformed++;
            return XdpAction.Drop;
        }

        byte ihl = (byte)(ipPacket[0] & 0x0F);
        int ipHdrLen = ihl * 4;
        if (ipHdrLen < 20 || ipPacket.Length < ipHdrLen)
        {
            _stats.PacketsDroppedMalformed++;
            return XdpAction.Drop;
        }

        byte protocol = ipPacket[9];
        if (protocol != 17) // Not UDP
        {
            _stats.PacketsPassed++;
            return XdpAction.Pass;
        }

        uint srcIp = BinaryPrimitives.ReadUInt32LittleEndian(ipPacket.Slice(12, 4));

        // 3. UDP Header (8 bytes)
        ReadOnlySpan<byte> udpPacket = ipPacket[ipHdrLen..];
        if (udpPacket.Length < 8)
        {
            _stats.PacketsDroppedMalformed++;
            return XdpAction.Drop;
        }

        ushort destPort = BinaryPrimitives.ReadUInt16BigEndian(udpPacket.Slice(2, 2));
        if (destPort < PortMin || destPort > PortMax)
        {
            _stats.PacketsPassed++;
            return XdpAction.Pass;
        }

        ushort udpLen = BinaryPrimitives.ReadUInt16BigEndian(udpPacket.Slice(4, 2));
        if (udpLen < 8 + MinK2PayloadBytes || udpPacket.Length < 8 + MinK2PayloadBytes)
        {
            _stats.PacketsDroppedMalformed++;
            return XdpAction.Drop;
        }

        // 4. Reject all-zero 4-byte null amplification probes while allowing legitimate K2 frames
        if (udpLen >= 12 && udpPacket.Length >= 12)
        {
            uint headerPrefix = BinaryPrimitives.ReadUInt32LittleEndian(udpPacket.Slice(8, 4));
            if (headerPrefix == NullAmplificationProbe)
            {
                _stats.PacketsDroppedMagic++;
                return XdpAction.Drop;
            }
        }

        // 5. Rate Limiter (Token bucket)
        if (currentTimeMs > 0)
        {
            if (_rateLimits.TryGetValue(srcIp, out var entry))
            {
                if (currentTimeMs - entry.LastTimeMs >= 1000)
                {
                    entry = (currentTimeMs, 1000);
                }

                if (entry.Tokens > 0)
                {
                    _rateLimits[srcIp] = (entry.LastTimeMs, entry.Tokens - 1);
                }
                else
                {
                    _stats.PacketsDroppedRateLimit++;
                    return XdpAction.Drop;
                }
            }
            else
            {
                _rateLimits[srcIp] = (currentTimeMs, 999);
            }
        }

        _stats.PacketsPassed++;
        return XdpAction.Pass;
    }
}
