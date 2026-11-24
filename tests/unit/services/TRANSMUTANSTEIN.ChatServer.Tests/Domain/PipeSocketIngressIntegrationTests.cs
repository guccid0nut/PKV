// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PipeSocketIngressIntegrationTests.cs
// System: TRANSMUTANSTEIN.ChatServer.Tests - Pipeline Socket Ingress Integration Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Domain;

public sealed class PipeSocketIngressIntegrationTests
{
    private static async Task<(Socket Client, Socket Server)> CreateConnectedSocketPairAsync()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> acceptTask = listener.AcceptAsync();
        await client.ConnectAsync(listener.LocalEndPoint!);
        Socket server = await acceptTask;

        return (client, server);
    }

    [Fact]
    public async Task PipeSocketIngress_EndToEndMessageFlow_ProcessesPacketsAccurately()
    {
        (Socket client, Socket server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(server);
            handler.Start();

            // Construct 3 K2 length-prefixed chat command frames
            byte[] frame1 = CreateK2Frame(opcode: 0x1100, payload: Encoding.UTF8.GetBytes("HELLO_KONGOR_CHAT"));
            byte[] frame2 = CreateK2Frame(opcode: 0x1200, payload: Encoding.UTF8.GetBytes("WHISPER_FROM_DEV"));
            byte[] frame3 = CreateK2Frame(opcode: 0x1300, payload: [0x01, 0x02, 0x03, 0x04]);

            await client.SendAsync(frame1, SocketFlags.None);
            await client.SendAsync(frame2, SocketFlags.None);
            await client.SendAsync(frame3, SocketFlags.None);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            int packetsReceived = 0;

            while (packetsReceived < 3)
            {
                ReadResult result = await handler.Input.ReadAsync(cts.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                while (ChatSession.TryReadPacketFrame(ref buffer, out ReadOnlySequence<byte> payload, out bool violation, out ushort declaredLen))
                {
                    violation.Should().BeFalse();
                    declaredLen.Should().BeGreaterThan(0);

                    byte[] payloadBytes = payload.ToArray();
                    payloadBytes.Length.Should().BeGreaterThanOrEqualTo(2);
                    ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payloadBytes.AsSpan(0, 2));

                    if (packetsReceived == 0)
                    {
                        opcode.Should().Be(0x1100);
                        Encoding.UTF8.GetString(payloadBytes.AsSpan(2)).Should().Be("HELLO_KONGOR_CHAT");
                    }
                    else if (packetsReceived == 1)
                    {
                        opcode.Should().Be(0x1200);
                        Encoding.UTF8.GetString(payloadBytes.AsSpan(2)).Should().Be("WHISPER_FROM_DEV");
                    }
                    else if (packetsReceived == 2)
                    {
                        opcode.Should().Be(0x1300);
                        payloadBytes.AsSpan(2).ToArray().Should().Equal([0x01, 0x02, 0x03, 0x04]);
                    }

                    packetsReceived++;
                }

                consumed = buffer.Start;
                examined = buffer.End;
                handler.Input.AdvanceTo(consumed, examined);
            }

            packetsReceived.Should().Be(3);
            await handler.DisposeAsync();
        }
    }

    [Fact]
    public async Task PipeSocketIngress_BurstPackets_MaintainsFramingIntegrity()
    {
        (Socket client, Socket server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(server);
            handler.Start();

            const int packetCount = 200;
            byte[] allPackets = new byte[packetCount * 16];
            for (int i = 0; i < packetCount; i++)
            {
                ushort opcode = (ushort)(0x2000 + i);
                byte[] payloadData = new byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(payloadData.AsSpan(0, 4), i);

                byte[] frame = CreateK2Frame(opcode, payloadData);
                frame.Length.Should().Be(16);
                frame.CopyTo(allPackets.AsSpan(i * 16));
            }

            // Blast all packets in a single TCP chunk
            await client.SendAsync(allPackets, SocketFlags.None);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            int packetsReceived = 0;

            while (packetsReceived < packetCount)
            {
                ReadResult result = await handler.Input.ReadAsync(cts.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                while (ChatSession.TryReadPacketFrame(ref buffer, out ReadOnlySequence<byte> payload, out bool violation, out ushort declaredLen))
                {
                    violation.Should().BeFalse();
                    declaredLen.Should().Be(14); // 2 bytes opcode + 12 bytes payload data

                    byte[] payloadBytes = payload.ToArray();
                    ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payloadBytes.AsSpan(0, 2));
                    int index = BinaryPrimitives.ReadInt32LittleEndian(payloadBytes.AsSpan(2, 4));

                    opcode.Should().Be((ushort)(0x2000 + packetsReceived));
                    index.Should().Be(packetsReceived);

                    packetsReceived++;
                }

                consumed = buffer.Start;
                examined = buffer.End;
                handler.Input.AdvanceTo(consumed, examined);
            }

            packetsReceived.Should().Be(packetCount);
            await handler.DisposeAsync();
        }
    }

    [Fact]
    public async Task PipeSocketIngress_Disconnection_SignalsCompletionGracefully()
    {
        (Socket client, Socket server) = await CreateConnectedSocketPairAsync();
        using (server)
        {
            PipeSocketHandler handler = new(server);
            handler.Start();

            byte[] frame = CreateK2Frame(opcode: 0x3000, payload: [0xAA, 0xBB]);
            await client.SendAsync(frame, SocketFlags.None);

            // Shutdown and close client socket
            client.Shutdown(SocketShutdown.Both);
            client.Close();
            client.Dispose();

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            bool sawCompletion = false;

            while (!sawCompletion)
            {
                ReadResult result = await handler.Input.ReadAsync(cts.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (ChatSession.TryReadPacketFrame(ref buffer, out _, out _, out _))
                {
                }

                handler.Input.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    sawCompletion = true;
                }
            }

            sawCompletion.Should().BeTrue();
            await handler.DisposeAsync();
        }
    }

    [Fact]
    public async Task PipeSocketIngress_MultiSegmentSplitFrame_AssemblesCorrectly()
    {
        (Socket client, Socket server) = await CreateConnectedSocketPairAsync();
        using (client)
        using (server)
        {
            PipeSocketHandler handler = new(server);
            handler.Start();

            byte[] fullFrame = CreateK2Frame(opcode: 0x4000, payload: Encoding.UTF8.GetBytes("SPLIT_PACKET_CONTENT"));

            // Send first 2 bytes (length header only)
            await client.SendAsync(fullFrame.AsMemory(0, 2), SocketFlags.None);
            await Task.Delay(50); // Small pause simulating TCP window fragmentation

            // Send the rest of the frame
            await client.SendAsync(fullFrame.AsMemory(2), SocketFlags.None);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            int packetsReceived = 0;

            while (packetsReceived < 1)
            {
                ReadResult result = await handler.Input.ReadAsync(cts.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                while (ChatSession.TryReadPacketFrame(ref buffer, out ReadOnlySequence<byte> payload, out bool violation, out ushort declaredLen))
                {
                    violation.Should().BeFalse();
                    byte[] payloadBytes = payload.ToArray();
                    ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(payloadBytes.AsSpan(0, 2));
                    opcode.Should().Be(0x4000);
                    Encoding.UTF8.GetString(payloadBytes.AsSpan(2)).Should().Be("SPLIT_PACKET_CONTENT");
                    packetsReceived++;
                }

                consumed = buffer.Start;
                examined = buffer.End;
                handler.Input.AdvanceTo(consumed, examined);
            }

            packetsReceived.Should().Be(1);
            await handler.DisposeAsync();
        }
    }

    private static byte[] CreateK2Frame(ushort opcode, byte[] payload)
    {
        ushort totalPayloadLength = (ushort)(2 + payload.Length);
        byte[] frame = new byte[2 + totalPayloadLength];

        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), totalPayloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), opcode);
        payload.CopyTo(frame.AsSpan(4));

        return frame;
    }
}
