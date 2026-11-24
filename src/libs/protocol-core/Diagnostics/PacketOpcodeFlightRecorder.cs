// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Text;

namespace Kongor.Protocol.Diagnostics;

public sealed record PacketOpcodeEntry(
    int SequenceIndex,
    int FrameNumber,
    byte Opcode,
    string OpcodeName,
    int ByteOffset,
    int PayloadLength,
    string PreviewHex);

public sealed record PacketFaultDiagnosis(
    int TotalCommandsProcessed,
    int FaultByteOffset,
    string FaultReason,
    IReadOnlyList<PacketOpcodeEntry> RecentHistory,
    string FormattedHistory,
    byte[] FaultCaptureWindow);

/// <summary>
/// 32-opcode circular flight recorder and fault packet window exporter modeled after
/// Xash3D-FWGS cl_parse.c (MSG_COUNT = 32, MSG_MASK = 31) (#141).
/// </summary>
public sealed class PacketOpcodeFlightRecorder
{
    public const int MSG_COUNT = 32;
    public const int MSG_MASK = MSG_COUNT - 1;

    private readonly PacketOpcodeEntry?[] _ring = new PacketOpcodeEntry?[MSG_COUNT];
    private int _numMessagesRead;

    public int TotalMessagesRecorded => _numMessagesRead;

    public void Reset()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _numMessagesRead = 0;
    }

    public void RecordCommand(
        int frameNumber,
        byte opcode,
        string opcodeName,
        int byteOffset,
        ReadOnlySpan<byte> payloadSlice)
    {
        int seq = _numMessagesRead++;
        int slot = seq & MSG_MASK;

        int previewLen = Math.Min(payloadSlice.Length, 8);
        string previewHex = previewLen > 0
            ? Convert.ToHexString(payloadSlice.Slice(0, previewLen))
            : string.Empty;

        _ring[slot] = new PacketOpcodeEntry(
            seq,
            frameNumber,
            opcode,
            string.IsNullOrWhiteSpace(opcodeName) ? $"svc_0x{opcode:X2}" : opcodeName,
            byteOffset,
            payloadSlice.Length,
            previewHex);
    }

    public IReadOnlyList<PacketOpcodeEntry> GetChronologicalHistory()
    {
        int count = Math.Min(_numMessagesRead, MSG_COUNT);
        List<PacketOpcodeEntry> list = new List<PacketOpcodeEntry>(count);
        int startSeq = _numMessagesRead - count;

        for (int seq = startSeq; seq < _numMessagesRead; seq++)
        {
            PacketOpcodeEntry? entry = _ring[seq & MSG_MASK];
            if (entry is not null)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    public string FormatMessageHistory(int currentReadCount, string faultReason)
    {
        IReadOnlyList<PacketOpcodeEntry> history = GetChronologicalHistory();
        StringBuilder sb = new StringBuilder(512);
        sb.AppendLine($"[XASH3D-NET-FLIGHT-RECORDER] Fault at offset {currentReadCount}: {faultReason}");
        sb.AppendLine($"Last {history.Count} parsed server/replay opcodes (ring capacity={MSG_COUNT}):");

        for (int i = 0; i < history.Count; i++)
        {
            PacketOpcodeEntry e = history[i];
            sb.AppendLine(
                $"  [{e.SequenceIndex:D4}] frame={e.FrameNumber} offset=0x{e.ByteOffset:X4} opcode=0x{e.Opcode:X2} ({e.OpcodeName}) len={e.PayloadLength} hex={e.PreviewHex}");
        }

        return sb.ToString();
    }

    public static byte[] ExportFaultCaptureBytes(ReadOnlySpan<byte> packetBuffer, int faultOffset, int maxWindowBytes = 512)
    {
        if (packetBuffer.IsEmpty || maxWindowBytes <= 0)
        {
            return Array.Empty<byte>();
        }

        int clampedFault = Math.Clamp(faultOffset, 0, packetBuffer.Length);
        int halfWindow = maxWindowBytes / 2;
        int start = Math.Max(0, clampedFault - halfWindow);
        int length = Math.Min(maxWindowBytes, packetBuffer.Length - start);

        return packetBuffer.Slice(start, length).ToArray();
    }

    /// <summary>
    /// Walks a length-prefixed command buffer ([opcode:1][payloadLen:2 LE][payload:payloadLen])
    /// and records every command into the 32-slot ring. If a truncated frame or unknown/corrupt
    /// opcode is encountered, returns a complete fault diagnosis with the 32-command trace.
    /// </summary>
    public PacketFaultDiagnosis InspectPacketStream(
        ReadOnlySpan<byte> packetBuffer,
        int frameNumber = 0,
        IReadOnlyDictionary<byte, string>? knownOpcodes = null)
    {
        int offset = 0;
        while (offset < packetBuffer.Length)
        {
            int cmdStartOffset = offset;
            byte opcode = packetBuffer[offset++];

            string opcodeName = $"svc_0x{opcode:X2}";
            if (knownOpcodes is not null && knownOpcodes.TryGetValue(opcode, out string? mappedName) && mappedName is not null)
            {
                opcodeName = mappedName;
            }
            else if (opcode == 0xFF)
            {
                string reason = $"Corrupt/bad server opcode 0xFF at byte offset 0x{cmdStartOffset:X4}";
                IReadOnlyList<PacketOpcodeEntry> recent = GetChronologicalHistory();
                return new PacketFaultDiagnosis(
                    _numMessagesRead,
                    cmdStartOffset,
                    reason,
                    recent,
                    FormatMessageHistory(cmdStartOffset, reason),
                    ExportFaultCaptureBytes(packetBuffer, cmdStartOffset));
            }

            if (offset + 2 > packetBuffer.Length)
            {
                string reason = $"Truncated 16-bit length header for opcode 0x{opcode:X2} ({opcodeName}) at offset 0x{cmdStartOffset:X4}";
                IReadOnlyList<PacketOpcodeEntry> recent = GetChronologicalHistory();
                return new PacketFaultDiagnosis(
                    _numMessagesRead,
                    cmdStartOffset,
                    reason,
                    recent,
                    FormatMessageHistory(cmdStartOffset, reason),
                    ExportFaultCaptureBytes(packetBuffer, cmdStartOffset));
            }

            ushort payloadLen = (ushort)(packetBuffer[offset] | (packetBuffer[offset + 1] << 8));
            offset += 2;

            if (offset + payloadLen > packetBuffer.Length)
            {
                string reason = $"Truncated payload for opcode 0x{opcode:X2} ({opcodeName}): needed {payloadLen} bytes at 0x{offset:X4}, only {packetBuffer.Length - offset} remaining";
                RecordCommand(frameNumber, opcode, opcodeName, cmdStartOffset, packetBuffer.Slice(offset));
                IReadOnlyList<PacketOpcodeEntry> recent = GetChronologicalHistory();
                return new PacketFaultDiagnosis(
                    _numMessagesRead,
                    cmdStartOffset,
                    reason,
                    recent,
                    FormatMessageHistory(cmdStartOffset, reason),
                    ExportFaultCaptureBytes(packetBuffer, cmdStartOffset));
            }

            RecordCommand(frameNumber, opcode, opcodeName, cmdStartOffset, packetBuffer.Slice(offset, payloadLen));
            offset += payloadLen;
        }

        IReadOnlyList<PacketOpcodeEntry> okHistory = GetChronologicalHistory();
        return new PacketFaultDiagnosis(
            _numMessagesRead,
            offset,
            "OK",
            okHistory,
            FormatMessageHistory(offset, "OK"),
            Array.Empty<byte>());
    }
}
