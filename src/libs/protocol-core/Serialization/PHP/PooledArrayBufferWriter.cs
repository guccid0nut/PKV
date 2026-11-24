// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;

namespace ASPIRE.Common.Serialization.PHP;

/// <summary>
///     High-performance IBufferWriter implementation backed by ArrayPool{byte} to eliminate heap allocations.
/// </summary>
public sealed class PooledArrayBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultInitialCapacity = 512;
    private byte[] _buffer;
    private bool _disposed;

    public PooledArrayBufferWriter(int initialCapacity = DefaultInitialCapacity)
    {
        int capacity = initialCapacity > 0 ? initialCapacity : DefaultInitialCapacity;
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        WrittenCount = 0;
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WrittenCount);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, WrittenCount);
    public int WrittenCount { get; private set; }

    public int Capacity => _buffer.Length;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (WrittenCount + count > _buffer.Length)
            throw new InvalidOperationException("Cannot advance past buffer capacity.");

        WrittenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsMemory(WrittenCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsSpan(WrittenCount);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            WrittenCount = 0;
            byte[] toReturn = _buffer;
            _buffer = [];
            if (toReturn.Length > 0) ArrayPool<byte>.Shared.Return(toReturn);
        }
    }

    public void Reset()
    {
        WrittenCount = 0;
    }

    private void CheckAndResizeBuffer(int sizeHint)
    {
        int minimumSize = sizeHint > 0 ? sizeHint : 1;
        int availableSpace = _buffer.Length - WrittenCount;

        if (availableSpace < minimumSize)
        {
            int currentLength = _buffer.Length;
            int growBy = Math.Max(minimumSize, currentLength);
            int newSize = currentLength + growBy;

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, WrittenCount).CopyTo(newBuffer);

            byte[] oldBuffer = _buffer;
            _buffer = newBuffer;
            ArrayPool<byte>.Shared.Return(oldBuffer);
        }
    }
}
