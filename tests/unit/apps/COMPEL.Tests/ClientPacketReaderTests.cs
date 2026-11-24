// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Buffers.Binary;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests;

/// <summary>
///     Verifies the length guard and the field offsets used to read a client datagram, both taken from the reference proxy.
/// </summary>
public sealed class ClientPacketReaderTests
{
    private static byte[] BuildDatagram(uint challenge, ushort counter, int length)
    {
        byte[] datagram = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(28), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(32), counter);

        return datagram;
    }

    [Fact]
    public void The_Minimum_Length_Differs_By_Kind()
    {
        ClientPacketReader.MinimumLength(ProxyForwarderKind.Game).Should().Be(43);
        ClientPacketReader.MinimumLength(ProxyForwarderKind.Voice).Should().Be(41);
    }

    [Fact]
    public void The_Challenge_And_Counter_Are_Read_From_Their_Reference_Offsets()
    {
        byte[] datagram = BuildDatagram(challenge: 0x11223344, counter: 4321, length: 64);

        bool read = ClientPacketReader.TryRead(datagram, ProxyForwarderKind.Game, out uint challenge, out ushort counter);

        read.Should().BeTrue();
        challenge.Should().Be(0x11223344u);
        counter.Should().Be((ushort)4321);
    }

    // The Guard Exists So No Field Is Ever Read Out Of Bounds: A Datagram One Byte Short Of The Minimum Must Be Refused Outright
    [Fact]
    public void A_Datagram_One_Byte_Below_The_Minimum_Is_Refused()
    {
        foreach (ProxyForwarderKind kind in (ProxyForwarderKind[])[ProxyForwarderKind.Game, ProxyForwarderKind.Voice])
        {
            byte[] datagram = new byte[ClientPacketReader.MinimumLength(kind) - 1];

            bool read = ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter);

            read.Should().BeFalse();
            challenge.Should().Be(0u);
            counter.Should().Be((ushort)0);
        }
    }

    [Fact]
    public void An_Empty_Datagram_Is_Refused_Without_Throwing()
    {
        bool read = ClientPacketReader.TryRead([], ProxyForwarderKind.Game, out _, out _);

        read.Should().BeFalse();
    }

    [Fact]
    public void A_Datagram_Exactly_At_The_Minimum_Is_Accepted()
    {
        byte[] datagram = BuildDatagram(challenge: 7, counter: 9, length: ClientPacketReader.MinimumLength(ProxyForwarderKind.Game));

        bool read = ClientPacketReader.TryRead(datagram, ProxyForwarderKind.Game, out uint challenge, out ushort counter);

        read.Should().BeTrue();
        challenge.Should().Be(7u);
        counter.Should().Be((ushort)9);
    }
}
