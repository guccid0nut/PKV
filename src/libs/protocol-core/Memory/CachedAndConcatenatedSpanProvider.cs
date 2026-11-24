// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Kongor.Core.Memory;

/// <summary>
/// Phase 13 (#157): 64 KiB Slot-Aligned O(1) LRU <c>SlotLruCachedProvider</c> (<c>cached_provider.cpp:11-149</c>),
/// Zero-Copy <c>VirtualConcatenatedProvider</c> (<c>concatenated_provider.cpp:19-66</c>),
/// and 4 KiB Sliding-Window In-Place Stream Shifter (<c>provider.cpp:211-262</c>).
/// Also fixes the upstream ImHex <c>provider.cpp:143</c> unsigned subtraction underflow bug when
/// <c>offset &lt; baseAddress</c> or <c>size &gt; actualSize</c>.
/// </summary>
public sealed class SlotLruCachedProvider : IDisposable
{
    public const int DefaultSlotSize = 0x10000; // 64 KiB (cached_provider.cpp:L14)
    public const int DefaultMaxCachedSlots = 1000; // cached_provider.cpp:L15
    public const int SlidingShiftWindowSize = 0x1000; // 4 KiB (provider.cpp:L218)

    private sealed class CacheSlot
    {
        public readonly long SlotIndex;
        public readonly byte[] Data;
        public int ValidLength;
        public LinkedListNode<long>? LruNode;

        public CacheSlot(long slotIndex, byte[] data, int validLength)
        {
            SlotIndex = slotIndex;
            Data = data;
            ValidLength = validLength;
        }
    }

    private readonly Func<long, Span<byte>, int> _backingReader;
    private readonly Action<long, ReadOnlySpan<byte>>? _backingWriter;
    private readonly long _baseAddress;
    private readonly long _actualSize;
    private readonly int _slotSize;
    private readonly int _maxCachedSlots;

    private readonly Dictionary<long, CacheSlot> _slots = new Dictionary<long, CacheSlot>();
    private readonly LinkedList<long> _lruQueue = new LinkedList<long>();
    private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

    private long _cacheHits;
    private long _cacheMisses;
    private bool _disposed;

    public SlotLruCachedProvider(
        Func<long, Span<byte>, int> backingReader,
        long actualSize,
        long baseAddress = 0,
        int slotSize = DefaultSlotSize,
        int maxCachedSlots = DefaultMaxCachedSlots,
        Action<long, ReadOnlySpan<byte>>? backingWriter = null)
    {
        ArgumentNullException.ThrowIfNull(backingReader);
        ArgumentOutOfRangeException.ThrowIfNegative(actualSize);
        ArgumentOutOfRangeException.ThrowIfNegative(baseAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedSlots);

        _backingReader = backingReader;
        _backingWriter = backingWriter;
        _actualSize = actualSize;
        _baseAddress = baseAddress;
        _slotSize = slotSize;
        _maxCachedSlots = maxCachedSlots;
    }

    public SlotLruCachedProvider(
        Stream backingStream,
        long baseAddress = 0,
        int slotSize = DefaultSlotSize,
        int maxCachedSlots = DefaultMaxCachedSlots)
        : this(
            (long relOffset, Span<byte> dest) =>
            {
                lock (backingStream)
                {
                    backingStream.Position = relOffset;
                    int totalRead = 0;
                    while (totalRead < dest.Length)
                    {
                        int read = backingStream.Read(dest.Slice(totalRead));
                        if (read <= 0)
                        {
                            break;
                        }

                        totalRead += read;
                    }

                    return totalRead;
                }
            },
            backingStream.Length,
            baseAddress,
            slotSize,
            maxCachedSlots,
            backingStream.CanWrite
                ? (long relOffset, ReadOnlySpan<byte> src) =>
                {
                    lock (backingStream)
                    {
                        backingStream.Position = relOffset;
                        backingStream.Write(src);
                    }
                }
                : null)
    {
    }

    public long BaseAddress => _baseAddress;
    public long ActualSize => _actualSize;
    public int SlotSize => _slotSize;
    public int MaxCachedSlots => _maxCachedSlots;
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    public int CachedSlotCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _slots.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Overflow- and underflow-safe bounds check fixing upstream ImHex <c>provider.cpp:143</c>,
    /// where unsigned <c>(offset - baseAddress) &gt; (actualSize - size)</c> wrapped to <c>UINT64_MAX</c>
    /// whenever <c>offset &lt; baseAddress</c> or <c>size &gt; actualSize</c>.
    /// </summary>
    public static bool ValidateBoundsSafe(long offset, int size, long baseAddress, long actualSize)
    {
        if (size < 0 || actualSize < 0 || baseAddress < 0)
        {
            return false;
        }

        if (offset < baseAddress)
        {
            return false;
        }

        if (size > actualSize)
        {
            return false;
        }

        long relativeOffset = offset - baseAddress;
        return relativeOffset <= (actualSize - size);
    }

    /// <summary>
    /// Reads <c>destination.Length</c> bytes starting at virtual <paramref name="offset"/> across 64 KiB cached slots.
    /// Returns false (and clears <paramref name="destination"/>) if the range is out of bounds.
    /// </summary>
    public bool ReadRaw(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.Length == 0)
        {
            return ValidateBoundsSafe(offset, 0, _baseAddress, _actualSize);
        }

        if (!ValidateBoundsSafe(offset, destination.Length, _baseAddress, _actualSize))
        {
            destination.Clear();
            return false;
        }

        long relativeOffset = offset - _baseAddress;
        int remaining = destination.Length;
        int destOffset = 0;

        while (remaining > 0)
        {
            long slotIndex = relativeOffset / _slotSize;
            int offsetInSlot = (int)(relativeOffset % _slotSize);
            int bytesToCopy = Math.Min(remaining, _slotSize - offsetInSlot);

            CacheSlot slot = GetOrLoadSlot(slotIndex);
            int availableInSlot = Math.Max(0, slot.ValidLength - offsetInSlot);
            int actualCopy = Math.Min(bytesToCopy, availableInSlot);

            if (actualCopy > 0)
            {
                slot.Data.AsSpan(offsetInSlot, actualCopy).CopyTo(destination.Slice(destOffset, actualCopy));
            }

            if (actualCopy < bytesToCopy)
            {
                destination.Slice(destOffset + actualCopy, bytesToCopy - actualCopy).Clear();
            }

            relativeOffset += bytesToCopy;
            destOffset += bytesToCopy;
            remaining -= bytesToCopy;
        }

        return true;
    }

    /// <summary>
    /// Writes <paramref name="source"/> at virtual <paramref name="offset"/> with write-through slot update.
    /// </summary>
    public bool WriteRaw(long offset, ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!ValidateBoundsSafe(offset, source.Length, _baseAddress, _actualSize))
        {
            return false;
        }

        if (source.Length == 0)
        {
            return true;
        }

        long relativeOffset = offset - _baseAddress;
        _backingWriter?.Invoke(relativeOffset, source);

        _lock.EnterWriteLock();
        try
        {
            long curRel = relativeOffset;
            int remaining = source.Length;
            int srcOffset = 0;

            while (remaining > 0)
            {
                long slotIndex = curRel / _slotSize;
                int offsetInSlot = (int)(curRel % _slotSize);
                int bytesToWrite = Math.Min(remaining, _slotSize - offsetInSlot);

                if (_slots.TryGetValue(slotIndex, out CacheSlot? existingSlot))
                {
                    source.Slice(srcOffset, bytesToWrite).CopyTo(existingSlot.Data.AsSpan(offsetInSlot, bytesToWrite));
                    int newEnd = offsetInSlot + bytesToWrite;
                    if (newEnd > existingSlot.ValidLength)
                    {
                        existingSlot.ValidLength = newEnd;
                    }

                    TouchSlotUnderWriteLock(existingSlot);
                }

                curRel += bytesToWrite;
                srcOffset += bytesToWrite;
                remaining -= bytesToWrite;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return true;
    }

    public void InvalidateAll()
    {
        _lock.EnterWriteLock();
        try
        {
            _slots.Clear();
            _lruQueue.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private CacheSlot GetOrLoadSlot(long slotIndex)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_slots.TryGetValue(slotIndex, out CacheSlot? existing))
            {
                Interlocked.Increment(ref _cacheHits);
                TouchSlotUnderWriteLock(existing);
                return existing;
            }

            Interlocked.Increment(ref _cacheMisses);

            while (_slots.Count >= _maxCachedSlots && _lruQueue.Last != null)
            {
                long evictedIndex = _lruQueue.Last.Value;
                _lruQueue.RemoveLast();
                _slots.Remove(evictedIndex);
            }

            long slotStartOffset = slotIndex * _slotSize;
            int maxReadable = (int)Math.Min(_slotSize, Math.Max(0L, _actualSize - slotStartOffset));
            byte[] buffer = new byte[_slotSize];
            int validLen = maxReadable > 0
                ? _backingReader(slotStartOffset, buffer.AsSpan(0, maxReadable))
                : 0;

            CacheSlot created = new CacheSlot(slotIndex, buffer, validLen);
            created.LruNode = _lruQueue.AddFirst(slotIndex);
            _slots[slotIndex] = created;
            return created;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void TouchSlotUnderWriteLock(CacheSlot slot)
    {
        if (slot.LruNode != null && slot.LruNode != _lruQueue.First)
        {
            _lruQueue.Remove(slot.LruNode);
            _lruQueue.AddFirst(slot.LruNode);
        }
    }

    /// <summary>
    /// Shifts stream bytes in-place starting at <paramref name="offset"/> by <paramref name="byteDelta"/>
    /// (positive for insertion zero-fill, negative for deletion compaction) using a bounded 4 KiB
    /// sliding window (<c>provider.cpp:211-262</c>).
    /// </summary>
    public static void ShiftBytesInPlace(Stream stream, long offset, long byteDelta)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (byteDelta == 0)
        {
            return;
        }

        byte[] window = new byte[SlidingShiftWindowSize];

        if (byteDelta > 0)
        {
            long oldSize = stream.Length;
            if (offset > oldSize)
            {
                offset = oldSize;
            }

            long newSize = oldSize + byteDelta;
            stream.SetLength(newSize);

            long cursor = oldSize;
            while (cursor > offset)
            {
                int chunk = (int)Math.Min(SlidingShiftWindowSize, cursor - offset);
                cursor -= chunk;

                stream.Position = cursor;
                int read = stream.Read(window, 0, chunk);
                stream.Position = cursor + byteDelta;
                stream.Write(window, 0, read);
            }

            // Zero-fill the newly inserted hole [offset, offset + byteDelta)
            Array.Clear(window, 0, window.Length);
            long zeroRemaining = byteDelta;
            long zeroPos = offset;
            while (zeroRemaining > 0)
            {
                int writeCount = (int)Math.Min(SlidingShiftWindowSize, zeroRemaining);
                stream.Position = zeroPos;
                stream.Write(window, 0, writeCount);
                zeroPos += writeCount;
                zeroRemaining -= writeCount;
            }
        }
        else
        {
            long removeCount = -byteDelta;
            long oldSize = stream.Length;
            if (offset >= oldSize)
            {
                return;
            }

            if (offset + removeCount >= oldSize)
            {
                stream.SetLength(offset);
                return;
            }

            long srcCursor = offset + removeCount;
            long dstCursor = offset;
            while (srcCursor < oldSize)
            {
                int chunk = (int)Math.Min(SlidingShiftWindowSize, oldSize - srcCursor);
                stream.Position = srcCursor;
                int read = stream.Read(window, 0, chunk);
                if (read <= 0)
                {
                    break;
                }

                stream.Position = dstCursor;
                stream.Write(window, 0, read);
                srcCursor += read;
                dstCursor += read;
            }

            stream.SetLength(oldSize - removeCount);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lock.Dispose();
        }
    }
}

/// <summary>
/// Presents multiple discontiguous <see cref="ReadOnlyMemory{T}"/> segments as a single zero-copy virtual
/// address space <c>[0, TotalLength)</c> with cross-boundary read splitting (<c>concatenated_provider.cpp:19-66</c>).
/// </summary>
public sealed class VirtualConcatenatedProvider
{
    private readonly List<ReadOnlyMemory<byte>> _segments = new List<ReadOnlyMemory<byte>>();
    private readonly List<long> _segmentStartOffsets = new List<long>();
    private long _totalLength;

    public VirtualConcatenatedProvider()
    {
    }

    public VirtualConcatenatedProvider(IEnumerable<ReadOnlyMemory<byte>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (ReadOnlyMemory<byte> seg in segments)
        {
            AppendSegment(seg);
        }
    }

    public int SegmentCount => _segments.Count;
    public long TotalLength => _totalLength;

    public void AppendSegment(ReadOnlyMemory<byte> segment)
    {
        if (segment.Length == 0)
        {
            return;
        }

        _segmentStartOffsets.Add(_totalLength);
        _segments.Add(segment);
        _totalLength += segment.Length;
    }

    /// <summary>
    /// Translates a unified virtual offset into <c>(segmentIndex, localOffset, availableBytesInSegment)</c>
    /// in O(log S) time via binary search (<c>concatenated_provider.cpp:19-35</c>).
    /// </summary>
    public bool TryTranslateOffset(
        long virtualOffset,
        out int segmentIndex,
        out int localOffset,
        out int availableInSegment)
    {
        segmentIndex = -1;
        localOffset = 0;
        availableInSegment = 0;

        if (virtualOffset < 0 || virtualOffset >= _totalLength || _segments.Count == 0)
        {
            return false;
        }

        int low = 0;
        int high = _segments.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            long segStart = _segmentStartOffsets[mid];
            long segEnd = segStart + _segments[mid].Length;

            if (virtualOffset < segStart)
            {
                high = mid - 1;
            }
            else if (virtualOffset >= segEnd)
            {
                low = mid + 1;
            }
            else
            {
                segmentIndex = mid;
                localOffset = (int)(virtualOffset - segStart);
                availableInSegment = _segments[mid].Length - localOffset;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads <c>destination.Length</c> bytes starting at <paramref name="virtualOffset"/>, transparently splitting
    /// reads that straddle segment boundaries (<c>concatenated_provider.cpp:37-51</c>).
    /// </summary>
    public bool ReadRaw(long virtualOffset, Span<byte> destination)
    {
        if (!SlotLruCachedProvider.ValidateBoundsSafe(virtualOffset, destination.Length, 0, _totalLength))
        {
            destination.Clear();
            return false;
        }

        if (destination.Length == 0)
        {
            return true;
        }

        if (!TryTranslateOffset(virtualOffset, out int segIdx, out int localOffset, out int avail))
        {
            destination.Clear();
            return false;
        }

        int remaining = destination.Length;
        int destOffset = 0;

        while (remaining > 0 && segIdx < _segments.Count)
        {
            ReadOnlySpan<byte> segSpan = _segments[segIdx].Span;
            int toCopy = Math.Min(remaining, avail);
            segSpan.Slice(localOffset, toCopy).CopyTo(destination.Slice(destOffset, toCopy));

            destOffset += toCopy;
            remaining -= toCopy;
            segIdx++;
            localOffset = 0;
            avail = segIdx < _segments.Count ? _segments[segIdx].Length : 0;
        }

        return remaining == 0;
    }
}
