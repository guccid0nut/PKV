// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

/// <summary>
///     Dynamic Byte Buffer
/// </summary>
public class TCPBuffer
{
    protected byte[] _data;
    protected long _offset;
    protected long _size;

    /// <summary>
    ///     Initialize A New Expandable Buffer With Zero Capacity
    /// </summary>
    public TCPBuffer()
    {
        _data = [];
        _size = 0;
        _offset = 0;
    }

    /// <summary>
    ///     Initialize A New Expandable Buffer With The Given Capacity
    /// </summary>
    public TCPBuffer(long capacity)
    {
        _data = new byte[capacity];
        _size = 0;
        _offset = 0;
    }

    /// <summary>
    ///     Initialize A New Expandable Buffer With The Given Data
    /// </summary>
    public TCPBuffer(byte[] data)
    {
        _data = data;
        _size = data.Length;
        _offset = 0;
    }

    /// <summary>
    ///     Initialize A New Expandable Buffer With The Given Data Slice Without Copying
    /// </summary>
    public TCPBuffer(byte[] data, long size)
    {
        _data = data;
        _size = size;
        _offset = 0;
    }

    /// <summary>
    ///     Initialize A New Expandable Buffer With The Given Data Span
    /// </summary>
    public TCPBuffer(ReadOnlySpan<byte> data)
    {
        _data = [.. data];
        _size = data.Length;
        _offset = 0;
    }

    /// <summary>
    ///     Is The Buffer Empty?
    /// </summary>
    public bool IsEmpty => _size == 0;

    /// <summary>
    ///     Bytes Memory Buffer
    /// </summary>
    public byte[] Data => _data;

    /// <summary>
    ///     Bytes Memory Buffer Capacity
    /// </summary>
    public long Capacity => _data.Length;

    /// <summary>
    ///     Bytes Memory Buffer Size
    /// </summary>
    public long Size => _size;

    /// <summary>
    ///     Bytes Memory Buffer Offset
    /// </summary>
    public long Offset => _offset;

    /// <summary>
    ///     Buffer Indexer Operator
    /// </summary>
    public byte this[long index] => _data[index];

    # region Memory Buffer Methods

    /// <summary>
    ///     Get A Span Of Bytes From The Current Buffer
    /// </summary>
    public Span<byte> AsSpan()
    {
        return new Span<byte>(_data, (int)_offset, (int)_size);
    }

    /// <summary>
    ///     Get A String From The Current Buffer
    /// </summary>
    public override string ToString()
    {
        return ExtractString(0, _size);
    }

    /// <summary>
    ///     Clear The Current Buffer And Its Offset
    /// </summary>
    public void Clear()
    {
        _size = 0;
        _offset = 0;
    }

    /// <summary>
    ///     Extract The String From Buffer Of The Given Offset And Size
    /// </summary>
    public string ExtractString(long offset, long size)
    {
        Debug.Assert(offset + size <= Size, "Invalid Offset And Size");

        return offset + size > Size
            ? throw new ArgumentException("Invalid Offset And Size", nameof(offset))
            : Encoding.UTF8.GetString(_data, (int)offset, (int)size);
    }

    /// <summary>
    ///     Remove The Buffer Of The Given Offset And Size
    /// </summary>
    public void Remove(long offset, long size)
    {
        Debug.Assert(offset + size <= Size, "Invalid Offset And Size");

        if (offset + size > Size) throw new ArgumentException("Invalid Offset And Size", nameof(offset));

        Array.Copy(_data, offset + size, _data, offset, _size - size - offset);
        _size -= size;

        if (_offset >= offset + size)
        {
            _offset -= size;
        }

        else if (_offset >= offset)
        {
            _offset -= _offset - offset;

            if (_offset > Size) _offset = Size;
        }
    }

    /// <summary>
    ///     Reserve The Buffer Of The Given Capacity using geometric growth.
    /// </summary>
    public void Reserve(long capacity)
    {
        Debug.Assert(capacity >= 0, "Invalid Reserve Capacity");

        if (capacity <= Capacity) return;

        long newCapacity = Math.Max(capacity, Math.Max(64, Capacity * 2));
        byte[] data = new byte[newCapacity];
        Array.Copy(_data, 0, data, 0, _size);
        _data = data;
    }

    /// <summary>
    ///     Resize The Current Buffer
    /// </summary>
    public void Resize(long size)
    {
        Reserve(size);
        _size = size;

        if (_offset > _size) _offset = _size;
    }

    /// <summary>
    ///     Shift The Current Buffer Offset
    /// </summary>
    public void Shift(long offset)
    {
        _offset += offset;
    }

    /// <summary>
    ///     Unshift The Current Buffer Offset
    /// </summary>
    public void Unshift(long offset)
    {
        _offset -= offset;
    }

    # endregion

    # region Buffer I/O Methods

    /// <summary>
    ///     Append The Single Byte
    /// </summary>
    /// <param name="value">Byte Value To Append</param>
    public void Append(byte value)
    {
        Reserve(_size + 1);

        _data[_size] = value;
        _size += 1;
    }

    /// <summary>
    ///     Append The Given Buffer
    /// </summary>
    /// <param name="buffer">Buffer To Append</param>
    public void Append(byte[] buffer)
    {
        Reserve(_size + buffer.Length);

        Array.Copy(buffer, 0, _data, _size, buffer.Length);
        _size += buffer.Length;
    }

    /// <summary>
    ///     Append The Given Buffer Fragment
    /// </summary>
    /// <param name="buffer">Buffer To Append</param>
    /// <param name="offset">Buffer Offset</param>
    /// <param name="size">Buffer Size</param>
    public void Append(byte[] buffer, long offset, long size)
    {
        Reserve(_size + size);

        Array.Copy(buffer, offset, _data, _size, size);
        _size += size;
    }

    /// <summary>
    ///     Append The Given Span Of Bytes
    /// </summary>
    /// <param name="buffer">Buffer To Append As A Span Of Bytes</param>
    public void Append(ReadOnlySpan<byte> buffer)
    {
        Reserve(_size + buffer.Length);

        buffer.CopyTo(new Span<byte>(_data, (int)_size, buffer.Length));
        _size += buffer.Length;
    }

    /// <summary>
    ///     Append The Given Buffer
    /// </summary>
    /// <param name="buffer">Buffer To Append</param>
    public void Append(TCPBuffer buffer)
    {
        Append(buffer.AsSpan());
    }

    /// <summary>
    ///     Append The Given Text In UTF-8 Encoding
    /// </summary>
    /// <param name="text">Text To Append</param>
    public void Append(string text)
    {
        int length = Encoding.UTF8.GetMaxByteCount(text.Length);

        Reserve(_size + length);

        long result = Encoding.UTF8.GetBytes(text, 0, text.Length, _data, (int)_size);
        _size += result;
    }

    /// <summary>
    ///     Append The Given Text In Utf-8 Encoding
    /// </summary>
    /// <param name="text">Text To Append As A Span Of Characters</param>
    public void Append(ReadOnlySpan<char> text)
    {
        int length = Encoding.UTF8.GetMaxByteCount(text.Length);

        Reserve(_size + length);

        long result = Encoding.UTF8.GetBytes(text, new Span<byte>(_data, (int)_size, length));
        _size += result;
    }

    # endregion
}
