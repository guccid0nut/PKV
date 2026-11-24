// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: XorFecEncoder.cs
// System: Kongor Core Netcode - Streaming XOR Forward Error Correction Encoder
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
/// Encodes streaming UDP datagrams into blocks of K packets protected by an M=1 XOR parity datagram.
/// Carries an inline Length-Map in the parity header to guarantee byte-for-byte exact payload reconstruction.
/// </summary>
public sealed class XorFecEncoder
{
    public const byte DefaultBlockSize = 4;
    public const int MaxDatagramPayloadSize = 1400;

    private readonly byte _blockSize;
    private readonly byte[] _parityBuffer;
    private readonly ushort[] _packetLengths;
    private ushort _currentGroupId;
    private byte _currentPacketIndex;
    private ushort _maxPayloadLength;

    public byte BlockSize => _blockSize;
    public ushort CurrentGroupId => _currentGroupId;
    public byte CurrentPacketIndex => _currentPacketIndex;

    public XorFecEncoder(byte blockSize = DefaultBlockSize)
    {
        if (blockSize == 0 || blockSize > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be between 1 and 16.");
        }

        _blockSize = blockSize;
        _parityBuffer = new byte[MaxDatagramPayloadSize];
        _packetLengths = new ushort[blockSize];
        _currentGroupId = 1;
        _currentPacketIndex = 0;
        _maxPayloadLength = 0;
    }

    /// <summary>
    /// Encodes a data payload into an FEC envelope. Returns true and marks isBlockFull when K packets have been encoded.
    /// </summary>
    public bool TryEncodeData(
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        out int bytesWritten,
        out bool isBlockFull)
    {
        isBlockFull = false;
        if (payload.Length > MaxDatagramPayloadSize)
        {
            bytesWritten = 0;
            return false;
        }

        FecPacketHeader header = new(_currentGroupId, _currentPacketIndex, _blockSize, (ushort)payload.Length);
        if (!header.TryWriteDataHeader(destination, out int headerWritten))
        {
            bytesWritten = 0;
            return false;
        }

        if (destination.Length < headerWritten + payload.Length)
        {
            bytesWritten = 0;
            return false;
        }

        payload.CopyTo(destination.Slice(headerWritten));
        bytesWritten = headerWritten + payload.Length;

        _packetLengths[_currentPacketIndex] = (ushort)payload.Length;
        if (payload.Length > _maxPayloadLength)
        {
            _maxPayloadLength = (ushort)payload.Length;
        }

        XorFecMath.AccumulateXor(_parityBuffer.AsSpan(0, payload.Length), payload);

        _currentPacketIndex++;
        if (_currentPacketIndex >= _blockSize)
        {
            isBlockFull = true;
        }

        return true;
    }

    /// <summary>
    /// Emits the parity packet for the current block and advances to the next GroupId.
    /// </summary>
    public bool TryEmitParity(Span<byte> destination, out int bytesWritten)
    {
        if (_currentPacketIndex == 0)
        {
            bytesWritten = 0;
            return false;
        }

        FecPacketHeader parityHeader = new(_currentGroupId, _blockSize, _blockSize, _maxPayloadLength);
        if (!parityHeader.TryWriteParityHeader(destination, _packetLengths, out int headerBytesWritten))
        {
            bytesWritten = 0;
            return false;
        }

        if (destination.Length < headerBytesWritten + _maxPayloadLength)
        {
            bytesWritten = 0;
            return false;
        }

        _parityBuffer.AsSpan(0, _maxPayloadLength).CopyTo(destination.Slice(headerBytesWritten));
        bytesWritten = headerBytesWritten + _maxPayloadLength;

        // Reset for next block
        Array.Clear(_parityBuffer, 0, _maxPayloadLength);
        Array.Clear(_packetLengths, 0, _packetLengths.Length);
        _currentGroupId++;
        if (_currentGroupId == 0) _currentGroupId = 1;
        _currentPacketIndex = 0;
        _maxPayloadLength = 0;

        return true;
    }
}
