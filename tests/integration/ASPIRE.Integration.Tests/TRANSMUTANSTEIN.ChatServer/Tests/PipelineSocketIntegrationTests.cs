// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public class PipelineSocketIntegrationTests
{
    [Fact]
    public async Task PipelineReader_FramesLengthPrefixedPackets_CorrectlyAcrossSegments()
    {
        Pipe pipe = new();
        PipeWriter writer = pipe.Writer;
        PipeReader reader = pipe.Reader;

        // Build 2 packets
        // Packet 1: Opcode 0x0100, Payload 4 bytes: [10, 20, 30, 40]
        byte[] packet1Payload = [10, 20, 30, 40];
        ushort packet1TotalLen = (ushort)(2 + packet1Payload.Length); // 6 bytes total (2 byte opcode + 4 byte payload)
        byte[] packet1Bytes = new byte[2 + packet1TotalLen];
        BinaryPrimitives.WriteUInt16LittleEndian(packet1Bytes.AsSpan(0, 2), packet1TotalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(packet1Bytes.AsSpan(2, 2), 0x0100);
        packet1Payload.CopyTo(packet1Bytes.AsSpan(4));

        // Packet 2: Opcode 0x0200, Payload 2 bytes: [99, 100]
        byte[] packet2Payload = [99, 100];
        ushort packet2TotalLen = (ushort)(2 + packet2Payload.Length);
        byte[] packet2Bytes = new byte[2 + packet2TotalLen];
        BinaryPrimitives.WriteUInt16LittleEndian(packet2Bytes.AsSpan(0, 2), packet2TotalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(packet2Bytes.AsSpan(2, 2), 0x0200);
        packet2Payload.CopyTo(packet2Bytes.AsSpan(4));

        // Write packet 1 in 2 separate fragmented chunks to verify reassembly
        await writer.WriteAsync(packet1Bytes.AsMemory(0, 3));
        await writer.WriteAsync(packet1Bytes.AsMemory(3));
        await writer.WriteAsync(packet2Bytes.AsMemory());
        await writer.CompleteAsync();

        List<byte[]> extractedPackets = [];

        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (buffer.Length >= 2)
            {
                SequenceReader<byte> seqReader = new(buffer);
                seqReader.TryRead(out byte b1);
                seqReader.TryRead(out byte b2);
                ushort payloadLength = (ushort)(b1 | (b2 << 8));

                if (seqReader.Remaining < payloadLength)
                {
                    break;
                }

                ReadOnlySequence<byte> packetSlice = buffer.Slice(2, payloadLength);
                extractedPackets.Add(packetSlice.ToArray());
                buffer = buffer.Slice(2 + payloadLength);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        Assert.Equal(2, extractedPackets.Count);
        Assert.Equal(6, extractedPackets[0].Length);
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16LittleEndian(extractedPackets[0].AsSpan(0, 2)));
        Assert.Equal(4, extractedPackets[1].Length);
        Assert.Equal(0x0200, BinaryPrimitives.ReadUInt16LittleEndian(extractedPackets[1].AsSpan(0, 2)));
    }

    [Fact]
    public async Task PipelineReader_HighThroughputBurst_FramesAllPacketsAccurately()
    {
        Pipe pipe = new();
        PipeWriter writer = pipe.Writer;
        PipeReader reader = pipe.Reader;

        const int packetCount = 100;
        using MemoryStream ms = new();

        for (ushort i = 0; i < packetCount; i++)
        {
            byte[] payload = [(byte)(i & 0xFF), (byte)((i >> 8) & 0xFF), 0xAA, 0xBB];
            ushort totalLen = (ushort)(2 + payload.Length);
            byte[] packetBytes = new byte[2 + totalLen];
            BinaryPrimitives.WriteUInt16LittleEndian(packetBytes.AsSpan(0, 2), totalLen);
            BinaryPrimitives.WriteUInt16LittleEndian(packetBytes.AsSpan(2, 2), (ushort)(0x1000 + i));
            payload.CopyTo(packetBytes.AsSpan(4));
            ms.Write(packetBytes);
        }

        byte[] allBytes = ms.ToArray();
        await writer.WriteAsync(allBytes.AsMemory());
        await writer.CompleteAsync();

        List<ushort> extractedOpcodes = [];

        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (buffer.Length >= 2)
            {
                SequenceReader<byte> seqReader = new(buffer);
                seqReader.TryRead(out byte b1);
                seqReader.TryRead(out byte b2);
                ushort payloadLength = (ushort)(b1 | (b2 << 8));

                if (seqReader.Remaining < payloadLength)
                {
                    break;
                }

                ReadOnlySequence<byte> packetSlice = buffer.Slice(2, payloadLength);
                byte[] raw = packetSlice.ToArray();
                ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0, 2));
                extractedOpcodes.Add(opcode);
                buffer = buffer.Slice(2 + payloadLength);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        Assert.Equal(packetCount, extractedOpcodes.Count);
        for (int i = 0; i < packetCount; i++)
        {
            Assert.Equal((ushort)(0x1000 + i), extractedOpcodes[i]);
        }
    }
}
