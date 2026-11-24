// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public class ChatBuffer : TCPBuffer
{
    /// <summary>
    ///     Initialize a new expandable chat buffer with zero capacity.
    /// </summary>
    public ChatBuffer()
    {
    }

    /// <summary>
    ///     Initialize a new expandable chat buffer with the given capacity.
    /// </summary>
    public ChatBuffer(long capacity) : base(capacity)
    {
    }

    /// <summary>
    ///     Initialize a new expandable chat buffer with the given data.
    /// </summary>
    public ChatBuffer(byte[] data) : base(data)
    {
    }

    /// <summary>
    ///     Initialize a new expandable chat buffer with the given data slice without copying.
    /// </summary>
    public ChatBuffer(byte[] data, long size) : base(data, size)
    {
    }

    /// <summary>
    ///     Initialize a new expandable chat buffer with the given data span.
    /// </summary>
    public ChatBuffer(ReadOnlySpan<byte> data) : base(data)
    {
    }

    /// <summary>
    ///     Append 2 bytes to the buffer for a command opcode.
    /// </summary>
    public void WriteCommandBytes(byte[] value)
    {
        if (value.Length is not 2)
            throw new InvalidDataException(
                $"Chat Command Is Expected To Be 2 Bytes In Length, But It Is {value.Length} Bytes");

        Append(value);
    }

    /// <summary>
    ///     Append 2 bytes to the buffer using zero-allocation binary primitive encoding.
    /// </summary>
    public void WriteCommand(ushort command)
    {
        Reserve(_size + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_data, (int)_size, 2), command);
        _size += 2;
    }

    /// <summary>
    ///     Read 2 bytes from the buffer, and return the result as a byte array.
    /// </summary>
    public byte[] ReadCommandBytes()
    {
        if (_offset is not 0) throw new InvalidDataException($"Offset Is {_offset}, But 0 (Zero) Was Expected");

        if (_size < 2) throw new InvalidDataException($"Unable To Read 2 Bytes From Buffer With Size {_size}");

        byte[] data = _data[..2];
        Shift(2);
        return data;
    }

    /// <summary>
    ///     Read 2 bytes from the buffer and return the command as an unsigned short without allocating.
    /// </summary>
    public ushort ReadCommand()
    {
        if (_size - _offset < 2)
            throw new InvalidDataException(
                $"Unable To Read 2 Bytes From Buffer With Size {_size} And Offset {_offset}");

        ushort command = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_data, (int)_offset, 2));
        Shift(2);
        return command;
    }

    /// <summary>
    ///     Append 1 byte to the buffer.
    /// </summary>
    public void WriteInt8(byte value)
    {
        Append(value);
    }

    /// <summary>
    ///     Read 1 byte from the buffer, and return the result.
    /// </summary>
    public byte ReadInt8()
    {
        if (_size - _offset < 1)
            throw new InvalidDataException($"Unable To Read 1 Byte From Buffer With Size {_size} And Offset {_offset}");

        byte data = _data[_offset];
        Shift(1);
        return data;
    }

    /// <summary>
    ///     Alias for ReadInt8.
    /// </summary>
    public byte ReadByte()
    {
        return ReadInt8();
    }

    /// <summary>
    ///     Append 1 byte with a value of either 0 or 1 to the buffer.
    /// </summary>
    public void WriteBool(bool value)
    {
        WriteInt8(value ? (byte)1 : (byte)0);
    }

    /// <summary>
    ///     Reads 1 byte from the buffer, and return the result as a boolean value if it can be parsed to one.
    /// </summary>
    public bool ReadBool()
    {
        byte data = ReadInt8();

        if (data is not 0 and not 1)
            throw new InvalidDataException($"Unable To Read A Boolean Value From Buffer Byte Value {data}");

        return data is 1;
    }

    /// <summary>
    ///     Append 2 bytes to the buffer.
    /// </summary>
    public void WriteInt16(short value)
    {
        Reserve(_size + 2);
        BinaryPrimitives.WriteInt16LittleEndian(new Span<byte>(_data, (int)_size, 2), value);
        _size += 2;
    }

    /// <summary>
    ///     Read 2 bytes from the buffer, and return the result as a short value.
    /// </summary>
    public short ReadInt16()
    {
        if (_size - _offset < 2)
            throw new InvalidDataException(
                $"Unable To Read 2 Bytes From Buffer With Size {_size} And Offset {_offset}");

        short data = BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(_data, (int)_offset, 2));
        Shift(2);
        return data;
    }

    /// <summary>
    ///     Append 4 bytes to the buffer.
    /// </summary>
    public void WriteInt32(int value)
    {
        Reserve(_size + 4);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(_data, (int)_size, 4), value);
        _size += 4;
    }

    /// <summary>
    ///     Read 4 bytes from the buffer, and return the result as an int value.
    /// </summary>
    public int ReadInt32()
    {
        if (_size - _offset < 4)
            throw new InvalidDataException(
                $"Unable To Read 4 Bytes From Buffer With Size {_size} And Offset {_offset}");

        int data = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_data, (int)_offset, 4));
        Shift(4);
        return data;
    }

    /// <summary>
    ///     Append 8 bytes to the buffer.
    /// </summary>
    public void WriteInt64(long value)
    {
        Reserve(_size + 8);
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(_data, (int)_size, 8), value);
        _size += 8;
    }

    /// <summary>
    ///     Read 8 bytes from the buffer, and return the result as a long value.
    /// </summary>
    public long ReadInt64()
    {
        if (_size - _offset < 8)
            throw new InvalidDataException(
                $"Unable To Read 8 Bytes From Buffer With Size {_size} And Offset {_offset}");

        long data = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_data, (int)_offset, 8));
        Shift(8);
        return data;
    }

    /// <summary>
    ///     Append 4 bytes to the buffer.
    /// </summary>
    public void WriteFloat32(float value)
    {
        Reserve(_size + 4);
        BinaryPrimitives.WriteSingleLittleEndian(new Span<byte>(_data, (int)_size, 4), value);
        _size += 4;
    }

    /// <summary>
    ///     Read 4 bytes from the buffer, and return the result as a float value.
    /// </summary>
    public float ReadFloat32()
    {
        if (_size - _offset < 4)
            throw new InvalidDataException(
                $"Unable To Read 4 Bytes From Buffer With Size {_size} And Offset {_offset}");

        float data = BinaryPrimitives.ReadSingleLittleEndian(new ReadOnlySpan<byte>(_data, (int)_offset, 4));
        Shift(4);
        return data;
    }

    /// <summary>
    ///     Append a character span in UTF-8 encoding followed by a null terminator (0x00).
    ///     Encodes directly into the underlying buffer without intermediate byte array or LINQ allocations.
    /// </summary>
    public void WriteString(ReadOnlySpan<char> value)
    {
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        Reserve(_size + maxByteCount + 1);

        int bytesWritten = Encoding.UTF8.GetBytes(value, new Span<byte>(_data, (int)_size, maxByteCount));
        _size += bytesWritten;
        _data[_size] = 0;
        _size += 1;
    }

    /// <summary>
    ///     Append a string in UTF-8 encoding followed by a null terminator (0x00).
    /// </summary>
    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteString([]);
            return;
        }

        WriteString(value.AsSpan());
    }

    /// <summary>
    ///     Reads an arbitrary number of bytes from the buffer, and return the result as a string value.
    ///     Uses hardware-vectorized SIMD scanning (Span.IndexOf) to locate the null terminator.
    ///     Strips unprintable control characters to shield native K2 text rendering from corrupted packet streams.
    /// </summary>
    public string ReadString()
    {
        if (_offset >= _size) return string.Empty;

        ReadOnlySpan<byte> remaining = new(_data, (int)_offset, (int)(_size - _offset));
        int nullIndex = remaining.IndexOf((byte)0);

        string raw;
        if (nullIndex >= 0)
        {
            raw = Encoding.UTF8.GetString(remaining[..nullIndex]);
            _offset += nullIndex + 1;
        }
        else
        {
            raw = Encoding.UTF8.GetString(remaining);
            _offset = _size;
        }

        return SanitizeIncomingString(raw);
    }

    /// <summary>
    ///     Perimeter sanitization: strips non-printable control characters, BiDi overrides, and unpaired UTF-16 surrogates
    ///     via PerimeterSecuritySanitizer to shield native K2 text rendering from corrupted packet streams.
    /// </summary>
    public static string SanitizeIncomingString(string input)
    {
        return ASPIRE.Common.Security.PerimeterSecuritySanitizer.SanitizePerimeterString(input);
    }

    /// <summary>
    ///     Reads a null-terminated UTF-8 byte span from the buffer without allocating a string.
    /// </summary>
    public ReadOnlySpan<byte> ReadStringSpan()
    {
        if (_offset >= _size) return [];

        ReadOnlySpan<byte> remaining = new(_data, (int)_offset, (int)(_size - _offset));
        int nullIndex = remaining.IndexOf((byte)0);

        if (nullIndex >= 0)
        {
            _offset += nullIndex + 1;
            return remaining[..nullIndex];
        }

        _offset = _size;
        return remaining;
    }

    /// <summary>
    ///     Prepend 2 bytes to the buffer, and return the number of bytes prepended.
    /// </summary>
    public long PrependBufferSize()
    {
        short size = Convert.ToInt16(_size);
        byte[] newData = new byte[Math.Max(_size + 2, Math.Max(64, Capacity * 2))];
        BinaryPrimitives.WriteInt16LittleEndian(newData.AsSpan(0, 2), size);
        Array.Copy(_data, 0, newData, 2, _size);
        _data = newData;
        _size += 2;

        return 2;
    }

    /// <summary>
    ///     Check if there is any remaining data to read in the buffer.
    /// </summary>
    public bool HasRemainingData(int minBytes = 1)
    {
        return _size - _offset >= minBytes;
    }

    /// <summary>
    ///     Reads bytes from the buffer as a span without advancing the offset.
    /// </summary>
    public ReadOnlySpan<byte> PeekSpan(int count)
    {
        return _size - _offset < count ? [] : new ReadOnlySpan<byte>(_data, (int)_offset, count);
    }

    /// <summary>
    ///     Reads bytes from the buffer without advancing the offset.
    /// </summary>
    public byte[] Peek(int count)
    {
        return PeekSpan(count).ToArray();
    }
}
