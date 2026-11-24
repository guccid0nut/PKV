// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using CsCheck;
using FluentAssertions;
using Kongor.Core.Services.Native;
using ProjectKongor.Contracts.Generated;
using Xunit;

namespace Kongor.Core.Tests;

/// <summary>
/// Property-based fuzzing and round-trip invariant verification for K2 network packet headers
/// using CsCheck (Property-Based Testing).
/// </summary>
public sealed class NetPacketCsCheckFuzzTests
{
    private const uint KongorMagic = 0x4B4F4E47; // "KONG"

    public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out GameServerPacketHeader header)
    {
        header = default;
        if (buffer.Length < GameServerPacketHeader.SizeInBytes)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        if (magic != KongorMagic)
        {
            return false;
        }

        ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2));
        uint serverTick = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(16, 4));

        header = new GameServerPacketHeader
        {
            Magic = magic,
            PacketId = packetId,
            Flags = flags,
            ServerTick = serverTick,
            PayloadSize = payloadSize,
            Crc32 = crc32
        };
        return true;
    }

    public static byte[] SerializePacket(in GameServerPacketHeader header, ReadOnlySpan<byte> payload = default)
    {
        byte[] bytes = new byte[GameServerPacketHeader.SizeInBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), header.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), header.PacketId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), header.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), header.ServerTick);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), header.PayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), header.Crc32);
        if (!payload.IsEmpty)
        {
            payload.CopyTo(bytes.AsSpan(GameServerPacketHeader.SizeInBytes));
        }
        return bytes;
    }

    [Fact]
    public void Property1_ArbitraryByteBuffers_NeverThrowUnhandledExceptions()
    {
        // Arbitrary byte buffers (0..4096 bytes) fed into GameServerPacketHeader parser
        Gen.Byte.Array[0, 4096].Sample(buffer =>
        {
            Action act = () =>
            {
                bool ok = TryParseHeader(buffer, out GameServerPacketHeader header);
                if (ok)
                {
                    header.Magic.Should().Be(KongorMagic);
                }
            };

            act.Should().NotThrow<IndexOutOfRangeException>();
            act.Should().NotThrow<OverflowException>();
            act.Should().NotThrow<AccessViolationException>();
        }, iter: 500);
    }

    [Fact]
    public void Property1_TruncatedHeaders_SafelyRejectedWithoutThrowing()
    {
        // Truncated headers (0..19 bytes) must always return false and never throw
        Gen.Byte.Array[0, 19].Sample(truncated =>
        {
            bool ok = TryParseHeader(truncated, out _);
            ok.Should().BeFalse("Truncated buffer (< 20 bytes) must fail cleanly.");
        }, iter: 200);
    }

    [Fact]
    public void Property1_OversizedPayloadAndFlippedCrc_RejectedGracefully()
    {
        // Oversized PayloadSize (uint.MaxValue) and bit-flipped CRC32
        Gen.Select(Gen.UShort, Gen.UShort, Gen.UInt, Gen.Byte.Array[0, 256]).Sample((packetId, flags, serverTick, payload) =>
        {
            uint crc = NativeZlibBridge.ComputeManagedCrc32Ieee(payload);
            uint flippedCrc = crc ^ 0xFFFFFFFF; // Bit-flipped CRC

            // Test 1: Oversized PayloadSize (uint.MaxValue)
            GameServerPacketHeader oversized = new()
            {
                Magic = KongorMagic,
                PacketId = packetId,
                Flags = flags,
                ServerTick = serverTick,
                PayloadSize = uint.MaxValue,
                Crc32 = crc
            };
            byte[] oversizedBytes = SerializePacket(oversized, payload);
            bool parseOk = TryParseHeader(oversizedBytes, out GameServerPacketHeader parsedHeader);
            parseOk.Should().BeTrue();
            // Invariant check: available payload bytes < declared PayloadSize
            int availablePayload = oversizedBytes.Length - GameServerPacketHeader.SizeInBytes;
            (parsedHeader.PayloadSize > (uint)availablePayload).Should().BeTrue("Oversized payload boundary must be detectable.");

            // Test 2: Flipped CRC
            GameServerPacketHeader corrupted = new()
            {
                Magic = KongorMagic,
                PacketId = packetId,
                Flags = flags,
                ServerTick = serverTick,
                PayloadSize = (uint)payload.Length,
                Crc32 = flippedCrc
            };
            byte[] corruptedBytes = SerializePacket(corrupted, payload);
            bool parseOk2 = TryParseHeader(corruptedBytes, out GameServerPacketHeader parsedHeader2);
            parseOk2.Should().BeTrue();
            uint computed = NativeZlibBridge.ComputeManagedCrc32Ieee(payload);
            (computed == parsedHeader2.Crc32).Should().BeFalse("Corrupted CRC32 must not match computed payload CRC.");
        }, iter: 300);
    }

    [Fact]
    public void Property2_ValidPacketHeader_RoundTripsByteForByteWithCrc32Across1000SeedsInSub500Ms()
    {
        Stopwatch sw = Stopwatch.StartNew();

        Gen.Select(Gen.UShort, Gen.UShort, Gen.UInt, Gen.Byte.Array[0, 512]).Sample((packetId, flags, serverTick, payload) =>
        {
            uint crc = NativeZlibBridge.ComputeManagedCrc32Ieee(payload);
            GameServerPacketHeader header = new()
            {
                Magic = KongorMagic,
                PacketId = packetId,
                Flags = flags,
                ServerTick = serverTick,
                PayloadSize = (uint)payload.Length,
                Crc32 = crc
            };

            byte[] wireBytes = SerializePacket(header, payload);

            // Invariant 1: Wire size must be exact HeaderSize + PayloadSize
            wireBytes.Length.Should().Be(GameServerPacketHeader.SizeInBytes + payload.Length);

            // Invariant 2: Parsing must succeed
            bool parseSuccess = TryParseHeader(wireBytes, out GameServerPacketHeader parsed);
            parseSuccess.Should().BeTrue();

            // Invariant 3: Exact round-trip field equality
            parsed.Magic.Should().Be(KongorMagic);
            parsed.PacketId.Should().Be(packetId);
            parsed.Flags.Should().Be(flags);
            parsed.ServerTick.Should().Be(serverTick);
            parsed.PayloadSize.Should().Be((uint)payload.Length);
            parsed.Crc32.Should().Be(crc);

            // Invariant 4: Verified payload CRC matches header CRC
            ReadOnlySpan<byte> parsedPayload = wireBytes.AsSpan(GameServerPacketHeader.SizeInBytes, (int)parsed.PayloadSize);
            uint verifiedCrc = NativeZlibBridge.ComputeManagedCrc32Ieee(parsedPayload);
            verifiedCrc.Should().Be(header.Crc32);
        }, iter: 1000);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "1,000 CsCheck property rounds must complete in < 500 ms.");
    }
}
