// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers;
using FluentAssertions;
using NEXUS.Contracts.Ipc;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class WillowmakerIpcFramingTests
{
    [Fact]
    public void HeaderSize_WhenInvoked_Is12Bytes()
    {
        WillowmakerIpcMessage.HeaderSize.Should().Be(12);
        WillowmakerIpcMessage.LegacyHeaderSize.Should().Be(8);
        WillowmakerIpcMessage.HeaderMagic.Should().Be(0x574D);
    }

    [Fact]
    public void Serialize_Writes12ByteHeader_WithRequestId()
    {
        // Arrange
        const uint expectedRequestId = 0x12345678;
        WillowmakerIpcMessage message = WillowmakerIpcMessage.CreateString(
            IpcOpCode.ExecuteCommand,
            "TestCommand",
            requestId: expectedRequestId);

        // Act
        byte[] bytes = WillowmakerIpcMessage.Serialize(message);

        // Assert
        bytes.Length.Should().Be(12 + "TestCommand".Length);
        BitConverter.ToUInt16(bytes, 0).Should().Be(0x574D);
        BitConverter.ToUInt16(bytes, 2).Should().Be((ushort)IpcOpCode.ExecuteCommand);
        BitConverter.ToInt32(bytes, 4).Should().Be("TestCommand".Length);
        BitConverter.ToUInt32(bytes, 8).Should().Be(expectedRequestId);

        bool deserialized = WillowmakerIpcMessage.TryDeserialize(bytes, out WillowmakerIpcMessage? result, out int consumed);
        deserialized.Should().BeTrue();
        result.Should().NotBeNull();
        result!.RequestId.Should().Be(expectedRequestId);
        result.OpCode.Should().Be(IpcOpCode.ExecuteCommand);
        result.PayloadString.Should().Be("TestCommand");
        consumed.Should().Be(bytes.Length);
    }

    [Fact]
    public void EncodeFrame_MatchesSerialize_ExactByteEquality()
    {
        // Arrange
        const uint reqId = 999;
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        ArrayBufferWriter<byte> writer = new();

        // Act
        WillowmakerIpcMessage.EncodeFrame(IpcOpCode.HotReloadMod, reqId, payload, writer);
        byte[] fromWriter = writer.WrittenSpan.ToArray();

        WillowmakerIpcMessage msg = WillowmakerIpcMessage.CreateBytes(IpcOpCode.HotReloadMod, payload, reqId);
        byte[] fromSerialize = WillowmakerIpcMessage.Serialize(msg);

        // Assert
        fromWriter.Should().Equal(fromSerialize);
    }

    [Fact]
    public void ConcurrentRequests_MultiplexOutOfOrder_ResolvesCorrectly()
    {
        // Arrange
        const int requestCount = 50;
        var pendingRequests = new System.Collections.Concurrent.ConcurrentDictionary<uint, TaskCompletionSource<WillowmakerIpcMessage>>();
        List<WillowmakerIpcMessage> requests = [];

        for (uint i = 1; i <= requestCount; i++)
        {
            var msg = WillowmakerIpcMessage.CreateString(IpcOpCode.ExecuteCommand, $"cmd_{i}", requestId: i);
            requests.Add(msg);
            pendingRequests[i] = new TaskCompletionSource<WillowmakerIpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Shuffle responses to simulate out-of-order network/pipe arrival
        List<WillowmakerIpcMessage> responses = requests
            .Select(r => WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, $"resp_{r.RequestId}", requestId: r.RequestId))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        // Act: Dispatch responses out-of-order
        foreach (WillowmakerIpcMessage resp in responses)
        {
            pendingRequests.TryRemove(resp.RequestId, out var tcs).Should().BeTrue();
            tcs!.TrySetResult(resp).Should().BeTrue();
        }

        // Assert: Each request received its exact correlated response
        for (uint i = 1; i <= requestCount; i++)
        {
            pendingRequests.ContainsKey(i).Should().BeFalse();
        }
    }

    [Fact]
    public void CorruptedMagic_WithInvalidInput_ThrowsInvalidDataException()
    {
        byte[] corrupted = new byte[12];
        corrupted[0] = 0xAA;
        corrupted[1] = 0xBB;

        FluentActions.Invoking(() => WillowmakerIpcMessage.TryDeserialize(corrupted, out _, out _))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*Invalid IPC magic header*");
    }

    [Fact]
    public void IncompleteHeader_WhenInvoked_ReturnsFalseWithoutConsuming()
    {
        byte[] incomplete = new byte[11];
        incomplete[0] = 0x4D;
        incomplete[1] = 0x57;

        bool result = WillowmakerIpcMessage.TryDeserialize(incomplete, out WillowmakerIpcMessage? msg, out int consumed);
        result.Should().BeFalse();
        msg.Should().BeNull();
        consumed.Should().Be(0);
    }

    [Fact]
    public void TryDeserializeAny_WhenInvoked_HandlesLegacyModernAndLineDelimitedFraming()
    {
        // 1. Line-delimited
        byte[] lineBytes = System.Text.Encoding.UTF8.GetBytes("Set host_maxFPS 144\n");
        bool lineSuccess = WillowmakerIpcMessage.TryDeserializeAny(lineBytes, out var lineMsg, out int lineConsumed);
        lineSuccess.Should().BeTrue();
        lineMsg.Should().NotBeNull();
        lineMsg!.OpCode.Should().Be(IpcOpCode.ExecuteCommand);
        lineMsg.PayloadString.Should().Be("Set host_maxFPS 144");
        lineConsumed.Should().Be(lineBytes.Length);

        // 2. Modern 12-byte binary
        WillowmakerIpcMessage modernMsg = WillowmakerIpcMessage.CreateString(IpcOpCode.CommandResponse, "ModernAck", requestId: 42);
        byte[] modernBytes = WillowmakerIpcMessage.Serialize(modernMsg);
        bool modernSuccess = WillowmakerIpcMessage.TryDeserializeAny(modernBytes, out var parsedModern, out int modernConsumed);
        modernSuccess.Should().BeTrue();
        parsedModern.Should().NotBeNull();
        parsedModern!.RequestId.Should().Be(42);
        parsedModern.PayloadString.Should().Be("ModernAck");
        modernConsumed.Should().Be(modernBytes.Length);

        // 3. Legacy 8-byte binary
        byte[] legacyPayload = System.Text.Encoding.UTF8.GetBytes("LegacyPayloadData");
        byte[] legacyBytes = new byte[WillowmakerIpcMessage.LegacyHeaderSize + legacyPayload.Length];
        BitConverter.GetBytes(WillowmakerIpcMessage.HeaderMagic).CopyTo(legacyBytes, 0);
        BitConverter.GetBytes((ushort)IpcOpCode.ExecuteCommand).CopyTo(legacyBytes, 2);
        BitConverter.GetBytes(legacyPayload.Length).CopyTo(legacyBytes, 4);
        legacyPayload.CopyTo(legacyBytes, 8);

        bool legacySuccess = WillowmakerIpcMessage.TryDeserializeAny(legacyBytes, out var parsedLegacy, out int legacyConsumed);
        legacySuccess.Should().BeTrue();
        parsedLegacy.Should().NotBeNull();
        parsedLegacy!.RequestId.Should().Be(0);
        parsedLegacy.PayloadString.Should().Be("LegacyPayloadData");
        legacyConsumed.Should().Be(legacyBytes.Length);
    }

    [Fact]
    public void TryDeserializeAny_BackToBackLegacyFramesWithLength10_DoNotDesynchronize()
    {
        // Length 10 has byte 0x0A (newline) in payload length. Must not be confused with text newline!
        byte[] p1 = System.Text.Encoding.UTF8.GetBytes("1234567890"); // length 10
        byte[] p2 = System.Text.Encoding.UTF8.GetBytes("abcdefghij"); // length 10

        byte[] buffer = new byte[(WillowmakerIpcMessage.LegacyHeaderSize + p1.Length) * 2];

        // Frame 1
        BitConverter.GetBytes(WillowmakerIpcMessage.HeaderMagic).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)IpcOpCode.ExecuteCommand).CopyTo(buffer, 2);
        BitConverter.GetBytes(p1.Length).CopyTo(buffer, 4);
        p1.CopyTo(buffer, 8);

        // Frame 2
        int offset2 = WillowmakerIpcMessage.LegacyHeaderSize + p1.Length;
        BitConverter.GetBytes(WillowmakerIpcMessage.HeaderMagic).CopyTo(buffer, offset2);
        BitConverter.GetBytes((ushort)IpcOpCode.ExecuteCommand).CopyTo(buffer, offset2 + 2);
        BitConverter.GetBytes(p2.Length).CopyTo(buffer, offset2 + 4);
        p2.CopyTo(buffer, offset2 + 8);

        // Deserializing frame 1
        bool success1 = WillowmakerIpcMessage.TryDeserializeAny(buffer, out var msg1, out int read1);
        success1.Should().BeTrue();
        msg1!.PayloadString.Should().Be("1234567890");
        read1.Should().Be(offset2);

        // Deserializing frame 2 from remainder
        bool success2 = WillowmakerIpcMessage.TryDeserializeAny(buffer.AsSpan(read1), out var msg2, out int read2);
        success2.Should().BeTrue();
        msg2!.PayloadString.Should().Be("abcdefghij");
        read2.Should().Be(offset2);
    }

    [Fact]
    public void TryDeserializeAny_LegacyFrameFollowedByLineCommand_ParsesCorrectly()
    {
        byte[] legacyPayload = System.Text.Encoding.UTF8.GetBytes("LegacyCmd");
        byte[] lineCommand = System.Text.Encoding.UTF8.GetBytes("Echo hello world\n");

        int legacyFrameSize = WillowmakerIpcMessage.LegacyHeaderSize + legacyPayload.Length;
        byte[] buffer = new byte[legacyFrameSize + lineCommand.Length];

        // Legacy binary frame
        BitConverter.GetBytes(WillowmakerIpcMessage.HeaderMagic).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)IpcOpCode.ExecuteCommand).CopyTo(buffer, 2);
        BitConverter.GetBytes(legacyPayload.Length).CopyTo(buffer, 4);
        legacyPayload.CopyTo(buffer, 8);

        // Line command
        lineCommand.CopyTo(buffer, legacyFrameSize);

        // Frame 1: Legacy
        bool s1 = WillowmakerIpcMessage.TryDeserializeAny(buffer, out var m1, out int r1);
        s1.Should().BeTrue();
        m1!.PayloadString.Should().Be("LegacyCmd");
        r1.Should().Be(legacyFrameSize);

        // Frame 2: Line
        bool s2 = WillowmakerIpcMessage.TryDeserializeAny(buffer.AsSpan(r1), out var m2, out int r2);
        s2.Should().BeTrue();
        m2!.PayloadString.Should().Be("Echo hello world");
        r2.Should().Be(lineCommand.Length);
    }
}
