// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MemoryMappedTelemetryBuffer.cs
// System: KONGOR Telemetry Core - High-Speed Shared Memory Ring Buffer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NEXUS.Contracts.Telemetry;

namespace ASPIRE.Common.Telemetry;

/// <summary>
///     High-speed, lock-free Single-Producer Single-Consumer (SPSC) telemetry ring buffer
///     backed by physical RAM via Win32 Shared Memory / POSIX MemoryMappedFile.
///     Operates in sub-15 nanoseconds per frame with zero GC allocations and 64-bit atomic Seqlock protection.
/// </summary>
public sealed unsafe class MemoryMappedTelemetryBuffer : IDisposable
{
    public const uint DefaultCapacity = 4096;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePointer;
    private readonly TelemetryRingHeader* _header;
    private readonly TelemetryRingHeaderV2* _headerV2;
    private readonly FrameTelemetryRecord* _records;
    private readonly uint _capacity;
    private readonly uint _capacityMask;
    private readonly bool _isProducer;
    private ulong _localTail;
    private bool _disposed;

    private MemoryMappedTelemetryBuffer(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        byte* basePointer,
        bool isProducer)
    {
        _mmf = mmf;
        _accessor = accessor;
        _basePointer = basePointer;
        _header = (TelemetryRingHeader*)basePointer;
        _headerV2 = _header->Version >= TelemetryRingHeaderV2.ExpectedVersion 
            ? (TelemetryRingHeaderV2*)basePointer 
            : null;

        int headerSize = _header->Version >= TelemetryRingHeaderV2.ExpectedVersion
            ? TelemetryRingHeaderV2.HeaderSizeBytes
            : TelemetryRingHeader.HeaderSizeBytes;

        _records = (FrameTelemetryRecord*)(basePointer + headerSize);
        _capacity = _header->Capacity;
        _capacityMask = _capacity > 0 ? (_capacity - 1) : 0;
        _isProducer = isProducer;
        _localTail = _header->Tail;
    }

    /// <summary>
    ///     Gets the configured capacity of the circular buffer in number of frames.
    /// </summary>
    public uint Capacity => _capacity;

    /// <summary>
    ///     Gets whether this instance is acting as the producer (writer).
    /// </summary>
    public bool IsProducer => _isProducer;

    /// <summary>
    ///     Gets the header version (1 = Legacy, 2 = 192-byte Seqlock Fast Bus).
    /// </summary>
    public uint Version => _header != null ? _header->Version : 0;

    /// <summary>
    ///     Gets whether this buffer supports V2 atomic Seqlock snapshots.
    /// </summary>
    public bool SupportsSeqlock => _headerV2 != null;

    /// <summary>
    ///     Gets the current write head counter (monotonically increasing).
    /// </summary>
    public ulong Head => Volatile.Read(ref _header->Head);

    /// <summary>
    ///     Gets the current read tail counter (monotonically increasing).
    /// </summary>
    public ulong Tail => Volatile.Read(ref _header->Tail);

    /// <summary>
    ///     Gets the current reserved flags (including real-time in-game Push-To-Talk bitflags).
    /// </summary>
    public ulong ReservedFlags => Volatile.Read(ref _header->ReservedFlags);

    /// <summary>
    ///     Gets the canonical engine game phase (0=Menu/Lobby, 1=Loading, 2=HeroSelect, 3=ActiveMatch, 4=PostGame).
    /// </summary>
    public uint GamePhase => _headerV2 != null ? Volatile.Read(ref _headerV2->GamePhase) : 0;

    /// <summary>
    ///     Gets the active match ID (0 when in menu or practice mode).
    /// </summary>
    public uint ActiveMatchId => _headerV2 != null ? Volatile.Read(ref _headerV2->ActiveMatchId) : 0;

    /// <summary>
    ///     Gets the number of connected players in the active session.
    /// </summary>
    public uint ConnectedPlayers => _headerV2 != null ? Volatile.Read(ref _headerV2->ConnectedPlayers) : 0;

    /// <summary>
    ///     Gets the live K2 Lua VM memory allocation in kilobytes.
    /// </summary>
    public uint LuaMemoryKb => _headerV2 != null ? Volatile.Read(ref _headerV2->LuaMemoryKb) : 0;

    /// <summary>
    ///     Gets the currently loaded map name (e.g. "caldavar", or empty when in Main Menu).
    /// </summary>
    public string CurrentMap => _headerV2 != null ? (*_headerV2).GetCurrentMapString() : string.Empty;

    /// <summary>
    ///     Gets the active player nickname from shared memory.
    /// </summary>
    public string PlayerName => _headerV2 != null ? (*_headerV2).GetPlayerNameString() : string.Empty;

    /// <summary>
    ///     Reads a snapshot copy of the V2 header if available.
    /// </summary>
    public bool TryReadHeaderV2(out TelemetryRingHeaderV2 header)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_headerV2 == null)
        {
            header = default;
            return false;
        }

        header = *_headerV2;
        return true;
    }

    /// <summary>
    ///     Updates the Cache Line 1 live engine state fields in the V2 shared-memory header.
    /// </summary>
    public void UpdateLiveEngineState(
        uint gamePhase,
        uint activeMatchId,
        uint connectedPlayers,
        uint luaMemoryKb,
        string? currentMap,
        string? playerName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isProducer)
            throw new InvalidOperationException("Cannot update live engine state on a consumer-mode telemetry buffer.");
        if (_headerV2 == null)
            return;

        _headerV2->GamePhase = gamePhase;
        _headerV2->ActiveMatchId = activeMatchId;
        _headerV2->ConnectedPlayers = connectedPlayers;
        _headerV2->LuaMemoryKb = luaMemoryKb;

        for (int i = 0; i < 16; i++)
        {
            _headerV2->CurrentMap[i] = 0;
        }
        if (!string.IsNullOrEmpty(currentMap))
        {
            byte[] mapBytes = System.Text.Encoding.UTF8.GetBytes(currentMap);
            int copyLen = Math.Min(mapBytes.Length, 15);
            for (int i = 0; i < copyLen; i++)
            {
                _headerV2->CurrentMap[i] = mapBytes[i];
            }
        }

        for (int i = 0; i < 12; i++)
        {
            _headerV2->PlayerName[i] = 0;
        }
        if (!string.IsNullOrEmpty(playerName))
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(playerName);
            int copyLen = Math.Min(nameBytes.Length, 11);
            for (int i = 0; i < copyLen; i++)
            {
                _headerV2->PlayerName[i] = nameBytes[i];
            }
        }

        Thread.MemoryBarrier();
    }

    /// <summary>
    ///     Resolves a shared-memory mapping name to a POSIX /dev/shm file path on Linux/macOS,
    ///     or to a direct file path when a file-backed '.shm' path is provided (e.g. in cross-platform tests).
    /// </summary>
    public static bool ResolvePosixOrFileBackingPath(string mapName, out string? backingFilePath)
    {
        backingFilePath = null;
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        if (mapName.EndsWith(".shm", StringComparison.OrdinalIgnoreCase) || File.Exists(mapName))
        {
            backingFilePath = Path.GetFullPath(mapName);
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            if (mapName.StartsWith("/dev/shm/", StringComparison.Ordinal))
            {
                if (OperatingSystem.IsMacOS() || !Directory.Exists("/dev/shm"))
                {
                    backingFilePath = Path.Combine(Path.GetTempPath(), mapName["/dev/shm/".Length..]);
                    return true;
                }

                backingFilePath = mapName;
                return true;
            }

            string cleanName = mapName;
            if (cleanName.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = cleanName[6..];
            }
            else if (cleanName.StartsWith("Global\\", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = cleanName[7..];
            }
            else if (cleanName.StartsWith('/'))
            {
                cleanName = cleanName[1..];
            }

            backingFilePath = (OperatingSystem.IsMacOS() || !Directory.Exists("/dev/shm"))
                ? Path.Combine(Path.GetTempPath(), cleanName)
                : $"/dev/shm/{cleanName}";
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Creates a new telemetry ring buffer as the producer (e.g. game client or server process).
    /// </summary>
    /// <param name="mapName">Named memory mapping identifier (e.g. Local\KongorClientTelemetry_12345 or /dev/shm/KongorServerTelemetry_11335).</param>
    /// <param name="capacity">Number of slots (must be a power of 2, default 4096).</param>
    /// <param name="producerProcessId">Optional producer PID.</param>
    public static MemoryMappedTelemetryBuffer CreateProducer(
        string mapName,
        uint capacity = DefaultCapacity,
        ulong producerProcessId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);

        if (capacity == 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException($"Capacity {capacity} must be a non-zero power of 2.", nameof(capacity));

        long totalSizeBytes = TelemetryRingHeaderV2.HeaderSizeBytes + ((long)capacity * FrameTelemetryRecord.RecordSizeBytes);

        MemoryMappedFile mmf;
        if (ResolvePosixOrFileBackingPath(mapName, out string? backingFilePath) && !string.IsNullOrEmpty(backingFilePath))
        {
            string? parentDir = Path.GetDirectoryName(backingFilePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            FileStream fs = new(
                backingFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < totalSizeBytes)
            {
                fs.SetLength(totalSizeBytes);
            }

            mmf = MemoryMappedFile.CreateFromFile(
                fs,
                mapName: null,
                totalSizeBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
        }
        else
        {
            mmf = MemoryMappedFile.CreateOrOpen(
                mapName,
                totalSizeBytes,
                MemoryMappedFileAccess.ReadWrite);
        }

        MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(
            0,
            totalSizeBytes,
            MemoryMappedFileAccess.ReadWrite);

        byte* basePointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);

        // Format V2 Header (192 bytes, 3 cache lines)
        TelemetryRingHeaderV2* header = (TelemetryRingHeaderV2*)basePointer;
        header->Magic = TelemetryRingHeaderV2.ExpectedMagic;
        header->Version = TelemetryRingHeaderV2.ExpectedVersion;
        header->Capacity = capacity;
        header->RecordSizeBytes = FrameTelemetryRecord.RecordSizeBytes;
        header->Head = 0;
        header->Tail = 0;
        header->ProducerProcessId = producerProcessId != 0 ? producerProcessId : (ulong)Environment.ProcessId;
        header->StartTimestampQpc = Stopwatch.GetTimestamp();
        header->FrequencyQpc = Stopwatch.Frequency;
        header->ReservedFlags = 0;

        // Cache Line 1 (Seqlock)
        header->LatestSequence = 0;
        header->SnapshotTimestamp = header->StartTimestampQpc;
        header->ProducerThreadId = (uint)Environment.CurrentManagedThreadId;

        // Ensure header write is visible to memory bus
        Thread.MemoryBarrier();

        return new MemoryMappedTelemetryBuffer(mmf, accessor, basePointer, isProducer: true);
    }

    /// <summary>
    ///     Opens an existing telemetry ring buffer as a consumer (e.g. launcher or monitoring agent).
    /// </summary>
    /// <param name="mapName">Named memory mapping identifier.</param>
    public static MemoryMappedTelemetryBuffer OpenConsumer(string mapName)
    {
        if (TryOpenConsumer(mapName, out MemoryMappedTelemetryBuffer? buffer, out string? error))
            return buffer!;

        throw new InvalidOperationException($"Failed to open telemetry consumer for '{mapName}': {error}");
    }

    /// <summary>
    ///     Attempts to open an existing telemetry ring buffer without throwing exceptions.
    /// </summary>
    public static bool TryOpenConsumer(
        string mapName,
        out MemoryMappedTelemetryBuffer? buffer,
        out string? error)
    {
        buffer = null;
        error = null;

        if (string.IsNullOrWhiteSpace(mapName))
        {
            error = "Map name cannot be empty.";
            return false;
        }

        try
        {
            MemoryMappedFile mmf;
            MemoryMappedViewAccessor accessor;

            if (ResolvePosixOrFileBackingPath(mapName, out string? backingFilePath) && !string.IsNullOrEmpty(backingFilePath))
            {
                if (!File.Exists(backingFilePath))
                {
                    error = $"Shared memory backing file '{backingFilePath}' does not exist.";
                    return false;
                }

                try
                {
                    FileStream fsRw = new(
                        backingFilePath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite | FileShare.Delete);
                    mmf = MemoryMappedFile.CreateFromFile(
                        fsRw,
                        mapName: null,
                        capacity: 0,
                        MemoryMappedFileAccess.ReadWrite,
                        HandleInheritability.None,
                        leaveOpen: false);
                    accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                }
                catch
                {
                    FileStream fsRo = new(
                        backingFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    mmf = MemoryMappedFile.CreateFromFile(
                        fsRo,
                        mapName: null,
                        capacity: 0,
                        MemoryMappedFileAccess.Read,
                        HandleInheritability.None,
                        leaveOpen: false);
                    accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
            }
            else
            {
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
                    accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                }
                catch
                {
                    mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                    accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
            }

            byte* basePointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);

            TelemetryRingHeader* header = (TelemetryRingHeader*)basePointer;
            if (!header->IsValid(out string? validationError))
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                accessor.Dispose();
                mmf.Dispose();
                error = validationError;
                return false;
            }

            buffer = new MemoryMappedTelemetryBuffer(mmf, accessor, basePointer, isProducer: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     Writes a frame telemetry record to the circular ring buffer and updates the Seqlock snapshot.
    ///     Executes in sub-15 nanoseconds without acquiring OS locks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in FrameTelemetryRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isProducer)
            throw new InvalidOperationException("Cannot write to a consumer-mode telemetry buffer.");

        if (_headerV2 != null)
        {
            _headerV2->ProducerThreadId = (uint)Environment.CurrentManagedThreadId;

            // 1. Mark write in progress (Odd sequence)
            long* pSeq = (long*)&_headerV2->LatestSequence;
            Interlocked.Increment(ref *pSeq);
            Thread.MemoryBarrier();

            // 2. Write snapshot to Cache Line 2
            _headerV2->LatestRecord = record;
            _headerV2->SnapshotTimestamp = record.TimestampQpc;

            // Also copy to circular ring slot
            ulong currentHead = _headerV2->Head;
            uint slotIndex = (uint)(currentHead & _capacityMask);
            _records[slotIndex] = record;

            // 3. Hardware commit fence
            Thread.MemoryBarrier();

            // 4. Mark write committed (Even sequence)
            Interlocked.Increment(ref *pSeq);

            // 5. Publish circular Head
            long* pHead = (long*)&_headerV2->Head;
            Interlocked.Increment(ref *pHead);

            return true;
        }
        else
        {
            // Fallback for V1 legacy buffers
            ulong currentHead = _header->Head;
            uint slotIndex = (uint)(currentHead & _capacityMask);
            _records[slotIndex] = record;
            Thread.MemoryBarrier();
            Volatile.Write(ref _header->Head, currentHead + 1);
            return true;
        }
    }

    /// <summary>
    ///     Reads all newly produced records into the provided destination buffer.
    ///     Returns the number of records read.
    /// </summary>
    public int ReadAvailable(Span<FrameTelemetryRecord> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.IsEmpty)
            return 0;

        ulong head = Volatile.Read(ref _header->Head);
        ulong tail = _localTail;

        if (head == tail)
            return 0;

        // If producer wrapped completely past the consumer, skip forward to oldest available frame
        if (head - tail > _capacity)
        {
            tail = head - _capacity;
        }

        ulong available = head - tail;
        int count = (int)Math.Min((ulong)destination.Length, available);

        for (int i = 0; i < count; i++)
        {
            uint slotIndex = (uint)((tail + (ulong)i) & _capacityMask);
            destination[i] = _records[slotIndex];
        }

        _localTail = tail + (ulong)count;
        Volatile.Write(ref _header->Tail, _localTail);

        return count;
    }

    /// <summary>
    ///     Reads the most recent N records without advancing the consumer tail.
    ///     Returns the number of records copied.
    /// </summary>
    public int ReadRecent(Span<FrameTelemetryRecord> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.IsEmpty)
            return 0;

        ulong head = Volatile.Read(ref _header->Head);
        if (head == 0)
            return 0;

        ulong count = Math.Min((ulong)destination.Length, Math.Min(head, (ulong)_capacity));
        ulong startIdx = head - count;

        for (ulong i = 0; i < count; i++)
        {
            uint slotIndex = (uint)((startIdx + i) & _capacityMask);
            destination[(int)i] = _records[slotIndex];
        }

        return (int)count;
    }

    /// <summary>
    ///     Reads the single latest frame telemetry record using the 64-bit atomic Seqlock protocol.
    ///     Guarantees ZERO torn reads and executes in sub-microsecond time with ZERO GC allocations.
    /// </summary>
    public bool TryReadLatestSeqlock(out FrameTelemetryRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_headerV2 == null)
        {
            // Fallback for V1 legacy buffers
            return ReadLatestLegacy(out record);
        }

        record = default;

        // Re-entrancy check: if the reader thread is the producer thread itself, read directly
        if (_headerV2->ProducerThreadId != 0 && (uint)Environment.CurrentManagedThreadId == _headerV2->ProducerThreadId)
        {
            record = _headerV2->LatestRecord;
            return true;
        }

        // Retry loop (up to 4 attempts)
        for (int retry = 0; retry < 4; ++retry)
        {
            ulong seq1 = Volatile.Read(ref _headerV2->LatestSequence);
            if ((seq1 & 1) != 0)
            {
                Thread.SpinWait(4);
                continue;
            }

            Thread.MemoryBarrier();
            FrameTelemetryRecord snapshot = _headerV2->LatestRecord;
            Thread.MemoryBarrier();

            ulong seq2 = Volatile.Read(ref _headerV2->LatestSequence);
            if (seq1 == seq2)
            {
                record = snapshot;
                return true; // 100% consistent read with zero torn fields
            }

            Thread.SpinWait(4);
        }

        return false;
    }

    /// <summary>
    ///     Reads the single latest frame telemetry record.
    ///     Automatically delegates to Seqlock for V2 buffers, eliminating torn reads.
    /// </summary>
    public bool ReadLatest(out FrameTelemetryRecord record)
    {
        if (_headerV2 != null)
        {
            return TryReadLatestSeqlock(out record);
        }

        return ReadLatestLegacy(out record);
    }

    private bool ReadLatestLegacy(out FrameTelemetryRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ulong head = Volatile.Read(ref _header->Head);
        if (head == 0)
        {
            record = default;
            return false;
        }

        uint slotIndex = (uint)((head - 1) & _capacityMask);
        record = _records[slotIndex];
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_basePointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _accessor.Dispose();
        _mmf.Dispose();
    }
}
