// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace COMPEL.Tests;

/// <summary>
///     A test double representing the Heroes of Newerth client's challenge retention rules.
///     The native client keys challenges per destination address and port, and accepts a replacement challenge only when its server creation timestamp is strictly greater than the one it currently holds.
/// </summary>
public sealed class ClientChallengeStoreDouble
{
    private readonly ConcurrentDictionary<IPEndPoint, HeldChallenge> challenges = new();

    /// <summary>
    ///     Processes an incoming challenge packet from the proxy and updates the held challenge for the given destination if its timestamp is strictly greater than the held timestamp.
    /// </summary>
    public bool TryProcessChallengePacket(IPEndPoint destination, ReadOnlySpan<byte> packet)
    {
        ReadOnlySpan<byte> payload;
        if (packet.Length == 18)
            payload = packet;
        else if (packet.Length >= 58)
            payload = packet[40..];
        else
            return false;

        // 0xFF 0xFF ProxyFlag(0x40) ChallengeType(0x00)
        if (payload[0] != 0xFF || payload[1] != 0xFF || payload[2] != 0x40 || payload[3] != 0x00)
            return false;

        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        ushort packetQuota = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
        ushort gameCommandQuota = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]);

        if (challenges.TryGetValue(destination, out HeldChallenge? existing))
        {
            if (timestamp <= existing.Timestamp)
                return false;
        }

        challenges[destination] = new HeldChallenge(value, timestamp, packetQuota, gameCommandQuota);

        return true;
    }

    /// <summary>
    ///     Retrieves the current challenge value and packet quota held for the given destination endpoint.
    /// </summary>
    public bool TryGetChallenge(IPEndPoint destination, out uint challenge, out ushort packetQuota)
    {
        if (challenges.TryGetValue(destination, out HeldChallenge? held))
        {
            challenge = held.Value;
            packetQuota = held.PacketQuota;

            return true;
        }

        challenge = 0;
        packetQuota = 0;

        return false;
    }

    /// <summary>
    ///     Retrieves the timestamp held for the given destination endpoint.
    /// </summary>
    public bool TryGetTimestamp(IPEndPoint destination, out uint timestamp)
    {
        if (challenges.TryGetValue(destination, out HeldChallenge? held))
        {
            timestamp = held.Timestamp;

            return true;
        }

        timestamp = 0;

        return false;
    }

    private sealed record HeldChallenge(uint Value, uint Timestamp, ushort PacketQuota, ushort GameCommandQuota);
}
