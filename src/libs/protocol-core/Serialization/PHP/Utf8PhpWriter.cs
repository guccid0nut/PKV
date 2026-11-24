// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Buffers.Text;
using System.Globalization;

namespace ASPIRE.Common.Serialization.PHP;

/// <summary>
///     High-performance ref struct for writing PHP-serialized wire payloads directly to an
///     <see cref="IBufferWriter{Byte}" />
///     with zero GC allocations.
/// </summary>
public ref struct Utf8PhpWriter
{
    private readonly IBufferWriter<byte> _writer;
    private Span<byte> _span;
    private int _buffered;

    private static ReadOnlySpan<byte> NullBytes => "N;"u8;
    private static ReadOnlySpan<byte> TrueBytes => "b:1;"u8;
    private static ReadOnlySpan<byte> FalseBytes => "b:0;"u8;

    public Utf8PhpWriter(IBufferWriter<byte> writer)
    {
        _writer = writer;
        _span = _writer.GetSpan(256);
        _buffered = 0;
    }

    /// <summary>
    ///     Flushes buffered bytes to the underlying buffer writer.
    /// </summary>
    public void Flush()
    {
        if (_buffered <= 0) return;

        _writer.Advance(_buffered);
        _buffered = 0;
        _span = default;
    }

    private void EnsureCapacity(int needed)
    {
        if (_span.Length - _buffered >= needed) return;

        if (_buffered > 0)
        {
            _writer.Advance(_buffered);
            _buffered = 0;
        }

        _span = _writer.GetSpan(Math.Max(needed, 256));
    }

    /// <summary>
    ///     Writes PHP null value ("N;").
    /// </summary>
    public void WriteNull()
    {
        EnsureCapacity(2);
        NullBytes.CopyTo(_span[_buffered..]);
        _buffered += 2;
    }

    /// <summary>
    ///     Writes PHP boolean value ("b:1;" or "b:0;").
    /// </summary>
    public void WriteBool(bool value)
    {
        EnsureCapacity(4);
        ReadOnlySpan<byte> bytes = value ? TrueBytes : FalseBytes;
        bytes.CopyTo(_span[_buffered..]);
        _buffered += 4;
    }

    /// <summary>
    ///     Writes PHP integer value ("i:{value};").
    /// </summary>
    public void WriteInt(int value)
    {
        EnsureCapacity(16);
        _span[_buffered++] = (byte)'i';
        _span[_buffered++] = (byte)':';

        if (Utf8Formatter.TryFormat(value, _span[_buffered..], out int bytesWritten))
        {
            _buffered += bytesWritten;
        }
        else
        {
            Span<char> chars = stackalloc char[32];
            if (value.TryFormat(chars, out int charsWritten, provider: CultureInfo.InvariantCulture))
            {
                EnsureCapacity(charsWritten + 4);
                int encoded = Encoding.UTF8.GetBytes(chars[..charsWritten], _span[_buffered..]);
                _buffered += encoded;
            }
        }

        _span[_buffered++] = (byte)';';
    }

    /// <summary>
    ///     Writes PHP long integer value ("i:{value};").
    /// </summary>
    public void WriteLong(long value)
    {
        EnsureCapacity(32);
        _span[_buffered++] = (byte)'i';
        _span[_buffered++] = (byte)':';

        if (Utf8Formatter.TryFormat(value, _span[_buffered..], out int bytesWritten))
        {
            _buffered += bytesWritten;
        }
        else
        {
            Span<char> chars = stackalloc char[32];
            if (value.TryFormat(chars, out int charsWritten, provider: CultureInfo.InvariantCulture))
            {
                EnsureCapacity(charsWritten + 4);
                int encoded = Encoding.UTF8.GetBytes(chars[..charsWritten], _span[_buffered..]);
                _buffered += encoded;
            }
        }

        _span[_buffered++] = (byte)';';
    }

    /// <summary>
    ///     Writes PHP double floating-point value ("d:{value};").
    /// </summary>
    public void WriteDouble(double value)
    {
        EnsureCapacity(32);
        _span[_buffered++] = (byte)'d';
        _span[_buffered++] = (byte)':';

        if (Utf8Formatter.TryFormat(value, _span[_buffered..], out int bytesWritten))
        {
            _buffered += bytesWritten;
        }
        else
        {
            Span<char> chars = stackalloc char[32];
            if (value.TryFormat(chars, out int charsWritten, provider: CultureInfo.InvariantCulture))
            {
                EnsureCapacity(charsWritten + 4);
                int encoded = Encoding.UTF8.GetBytes(chars[..charsWritten], _span[_buffered..]);
                _buffered += encoded;
            }
        }

        _span[_buffered++] = (byte)';';
    }

    /// <summary>
    ///     Writes PHP string value ("s:{byteLength}:\"{content}\";").
    /// </summary>
    public void WriteString(ReadOnlySpan<char> value)
    {
        int utf8ByteLength = Encoding.UTF8.GetByteCount(value);
        EnsureCapacity(16);

        _span[_buffered++] = (byte)'s';
        _span[_buffered++] = (byte)':';

        if (Utf8Formatter.TryFormat(utf8ByteLength, _span[_buffered..], out int lenWritten))
        {
            _buffered += lenWritten;
        }
        else
        {
            EnsureCapacity(32);
            if (Utf8Formatter.TryFormat(utf8ByteLength, _span[_buffered..], out lenWritten))
            {
                _buffered += lenWritten;
            }
            else
            {
                Span<char> chars = stackalloc char[32];
                if (utf8ByteLength.TryFormat(chars, out int charsWritten, provider: CultureInfo.InvariantCulture))
                {
                    EnsureCapacity(charsWritten + 4);
                    int encoded = Encoding.UTF8.GetBytes(chars[..charsWritten], _span[_buffered..]);
                    _buffered += encoded;
                }
            }
        }

        _span[_buffered++] = (byte)':';
        _span[_buffered++] = (byte)'"';

        if (utf8ByteLength > 0)
        {
            EnsureCapacity(utf8ByteLength + 2);
            int encoded = Encoding.UTF8.GetBytes(value, _span[_buffered..]);
            _buffered += encoded;
        }

        EnsureCapacity(2);
        _span[_buffered++] = (byte)'"';
        _span[_buffered++] = (byte)';';
    }

    /// <summary>
    ///     Writes PHP array header ("a:{count}:{").
    /// </summary>
    public void WriteArrayHeader(int count)
    {
        EnsureCapacity(16);
        _span[_buffered++] = (byte)'a';
        _span[_buffered++] = (byte)':';

        if (Utf8Formatter.TryFormat(count, _span[_buffered..], out int countWritten))
        {
            _buffered += countWritten;
        }
        else
        {
            EnsureCapacity(32);
            if (Utf8Formatter.TryFormat(count, _span[_buffered..], out countWritten))
            {
                _buffered += countWritten;
            }
            else
            {
                Span<char> chars = stackalloc char[32];
                if (count.TryFormat(chars, out int charsWritten, provider: CultureInfo.InvariantCulture))
                {
                    EnsureCapacity(charsWritten + 4);
                    int encoded = Encoding.UTF8.GetBytes(chars[..charsWritten], _span[_buffered..]);
                    _buffered += encoded;
                }
            }
        }

        _span[_buffered++] = (byte)':';
        _span[_buffered++] = (byte)'{';
    }

    /// <summary>
    ///     Writes PHP array footer ("}").
    /// </summary>
    public void WriteArrayFooter()
    {
        EnsureCapacity(1);
        _span[_buffered++] = (byte)'}';
    }

    /// <summary>
    ///     Writes a key as an integer ("i:{index};").
    /// </summary>
    public void WriteKey(int index)
    {
        WriteInt(index);
    }

    /// <summary>
    ///     Writes a key as a string ("s:{length}:\"{name}\";").
    /// </summary>
    public void WriteKey(ReadOnlySpan<char> name)
    {
        WriteString(name);
    }
}
