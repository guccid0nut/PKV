// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Compression;
using ASPIRE.Common.Utilities;
using Kongor.Modding.Core.Models;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Core.Packaging;

/// <summary>
///     Stream-oriented archive writer for .s2z packages implementing <see cref="IArchiveWriter" />.
/// </summary>
public sealed class S2zStreamArchiveWriter : IArchiveWriter
{
    private readonly CompressionLevel _compressionLevel;
    private readonly Stream _destinationStream;
    private readonly bool _leaveOpen;
    private readonly ZipArchive _zip;
    private readonly long _initialPosition;
    private int _entryCount;
    private long _uncompressedBytesWritten;
    private bool _disposed;

    public S2zStreamArchiveWriter(
        Stream destinationStream,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);
        _destinationStream = destinationStream;
        _compressionLevel = compressionLevel;
        _leaveOpen = leaveOpen;
        _initialPosition = destinationStream.CanSeek ? destinationStream.Position : 0L;
        _zip = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true);
    }

    public async Task WriteEntryAsync(string entryPath, byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        EnsureNextEntryWithinK2Limits(entryPath, data.LongLength);
        string normPath = ModPackage.NormalizeZipPath(entryPath);
        ZipArchiveEntry entry = _zip.CreateEntry(normPath, _compressionLevel);
        await using (Stream entryStream = entry.Open())
        {
            await entryStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        VerifyStreamArchiveGrowth(entryPath);
    }

    public async Task WriteEntryAsync(string entryPath, Stream dataStream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataStream);

        long knownLength = dataStream.CanSeek ? (dataStream.Length - dataStream.Position) : 0L;
        EnsureNextEntryWithinK2Limits(entryPath, knownLength);
        string normPath = ModPackage.NormalizeZipPath(entryPath);
        ZipArchiveEntry entry = _zip.CreateEntry(normPath, _compressionLevel);
        await using (Stream entryStream = entry.Open())
        {
            byte[] copyBuf = new byte[64 * 1024];
            long copied = 0L;
            int read;
            while ((read = await dataStream.ReadAsync(copyBuf, cancellationToken).ConfigureAwait(false)) > 0)
            {
                copied += read;
                if (copied > S2zArchiveWriter.MaxSafeS2zEntryUncompressedBytes)
                {
                    throw new InvalidOperationException(
                        $"[S2zArchiveWriter] Entry '{normPath}' uncompressed size ({copied:N0} bytes) exceeds K2 signed 32-bit safety limit ({S2zArchiveWriter.MaxSafeS2zEntryUncompressedBytes:N0} bytes). Split assets across multiple .s2z archives.");
                }

                await entryStream.WriteAsync(copyBuf.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (!dataStream.CanSeek)
            {
                _uncompressedBytesWritten += copied;
            }
        }

        VerifyStreamArchiveGrowth(entryPath);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        VerifyStreamArchiveGrowth("<flush>");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _zip.Dispose();
            try
            {
                if (_destinationStream.CanSeek)
                {
                    S2zArchiveWriter.EnsureStreamArchiveWithinK2Bounds(_destinationStream, _initialPosition);
                }
            }
            finally
            {
                if (!_leaveOpen) _destinationStream.Dispose();
            }
        }
    }

    private void EnsureNextEntryWithinK2Limits(string entryPath, long entryLength)
    {
        if (_entryCount + 1 > S2zArchiveWriter.MaxSafeS2zEntryCount)
        {
            throw new InvalidOperationException(
                $"[S2zArchiveWriter] Cannot add entry '{entryPath}': entry count ({_entryCount + 1}) exceeds pre-Zip64 K2 limit ({S2zArchiveWriter.MaxSafeS2zEntryCount}).");
        }

        if (entryLength > S2zArchiveWriter.MaxSafeS2zEntryUncompressedBytes)
        {
            throw new InvalidOperationException(
                $"[S2zArchiveWriter] Entry '{entryPath}' length ({entryLength:N0} bytes) exceeds K2 signed 32-bit limit ({S2zArchiveWriter.MaxSafeS2zEntryUncompressedBytes:N0} bytes).");
        }

        _entryCount++;
        _uncompressedBytesWritten += Math.Max(0L, entryLength);
    }

    private void VerifyStreamArchiveGrowth(string contextEntry)
    {
        if (_destinationStream.CanSeek)
        {
            long archiveLength = _destinationStream.Length - _initialPosition;
            if (archiveLength > S2zArchiveWriter.MaxSafeS2zArchiveBytes)
            {
                throw new InvalidOperationException(
                    $"[S2zArchiveWriter] Archive exceeded K2 1.5 GiB signed 32-bit offset limit ({archiveLength:N0} > {S2zArchiveWriter.MaxSafeS2zArchiveBytes:N0} bytes) after writing '{contextEntry}'.");
            }
        }
    }
}

/// <summary>
///     High-throughput writer for HoN .s2z game resource archives supporting physical, stream, and VFS destinations.
/// </summary>
public static class S2zArchiveWriter
{
    /// <summary>
    ///     Maximum safe `.s2z` archive size (1.5 GiB = 1,610,612,736 bytes) keeping all offsets well below K2's signed 32-bit `int32_t` reader overflow (`0x7FFFFFFF`) and `Zip64` thresholds.
    /// </summary>
    public const long MaxSafeS2zArchiveBytes = 1_610_612_736L;

    /// <summary>
    ///     Maximum uncompressed size of any single entry inside an `.s2z` archive (1.5 GiB).
    /// </summary>
    public const long MaxSafeS2zEntryUncompressedBytes = 1_610_612_736L;

    /// <summary>
    ///     Maximum entries in a standard non-Zip64 EOCD record (`0xFFFF` = 65535 is reserved as the Zip64 sentinel).
    /// </summary>
    public const int MaxSafeS2zEntryCount = 65_534;

    private const uint ZipEocdSignature = 0x06054B50u;
    private const uint Zip64EocdSignature = 0x06064B50u;
    private const uint Zip64EocdLocatorSignature = 0x07064B50u;

    public static async Task WriteArchiveAsync(
        string destinationFilePath,
        IReadOnlyDictionary<string, byte[]> files,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        ArgumentNullException.ThrowIfNull(files);

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string tempPath = destinationFilePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, true))
            {
                await WriteArchiveAsync(fs, files, compressionLevel, true, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                EnsureStreamArchiveWithinK2Bounds(fs, 0L);
            }

            try
            {
                File.Move(tempPath, destinationFilePath, true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                List<FileLockingProcess> lockers = FileLockDetector.GetLockingProcesses(destinationFilePath);
                string lockDiagnostic = FileLockDetector.FormatLockingProcessesDiagnostic(destinationFilePath);
                throw new IOException(
                    $"Failed to atomically replace .s2z archive '{destinationFilePath}' ({lockers.Count} locking process(es) detected). {lockDiagnostic} Close hon_x64.exe or the locking process before injecting.",
                    moveEx);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    Debug.WriteLine($"[S2zArchiveWriter] Failed to delete temporary staging archive '{tempPath}': {cleanupEx.Message}");
                }
            }
        }
    }

    public static async Task WriteArchiveAsync(
        Stream destinationStream,
        IReadOnlyDictionary<string, byte[]> files,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool leaveOpen = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count > MaxSafeS2zEntryCount)
        {
            throw new InvalidOperationException(
                $"[S2zArchiveWriter] Cannot write .s2z archive with {files.Count:N0} entries (exceeds non-Zip64 K2 limit of {MaxSafeS2zEntryCount:N0}).");
        }

        long initialPos = destinationStream.CanSeek ? destinationStream.Position : 0L;
        CountingWriteStream countingStream = new CountingWriteStream(destinationStream, MaxSafeS2zArchiveBytes);
        try
        {
            using (ZipArchive zip = new ZipArchive(countingStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (KeyValuePair<string, byte[]> kvp in files)
                {
                    ArgumentNullException.ThrowIfNull(kvp.Value);
                    if (kvp.Value.LongLength > MaxSafeS2zEntryUncompressedBytes)
                    {
                        throw new InvalidOperationException(
                            $"[S2zArchiveWriter] Entry '{kvp.Key}' uncompressed size ({kvp.Value.LongLength:N0} bytes) exceeds K2 signed 32-bit safety limit ({MaxSafeS2zEntryUncompressedBytes:N0} bytes).");
                    }

                    string normPath = ModPackage.NormalizeZipPath(kvp.Key);
                    ZipArchiveEntry entry = zip.CreateEntry(normPath, compressionLevel);
                    await using Stream entryStream = entry.Open();
                    await entryStream.WriteAsync(kvp.Value, ct).ConfigureAwait(false);
                }
            }

            if (destinationStream.CanSeek && destinationStream.CanRead)
            {
                EnsureStreamArchiveWithinK2Bounds(destinationStream, initialPos);
            }
        }
        finally
        {
            if (!leaveOpen)
            {
                destinationStream.Dispose();
            }
        }
    }

    public static async Task WriteArchiveAsync(
        IVirtualFileSystem vfs,
        string destinationVirtualPath,
        IReadOnlyDictionary<string, byte[]> files,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationVirtualPath);
        ArgumentNullException.ThrowIfNull(files);

        using MemoryStream ms = new MemoryStream();
        await WriteArchiveAsync(ms, files, compressionLevel, true, ct).ConfigureAwait(false);
        byte[] archiveBytes = ms.ToArray();
        if (!ValidateArchiveBufferIntegrity(archiveBytes, out _))
        {
            throw new InvalidOperationException(
                $"[S2zArchiveWriter] Generated VFS .s2z archive '{destinationVirtualPath}' ({archiveBytes.Length:N0} bytes) failed K2 32-bit / non-Zip64 integrity verification.");
        }

        await vfs.WriteAllBytesAsync(destinationVirtualPath, archiveBytes, ct).ConfigureAwait(false);
    }

    public static async Task AppendOrUpdateArchiveAsync(
        string targetFilePath,
        IReadOnlyDictionary<string, byte[]> filesToUpdate,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        ArgumentNullException.ThrowIfNull(filesToUpdate);

        if (!File.Exists(targetFilePath))
        {
            await WriteArchiveAsync(targetFilePath, filesToUpdate, compressionLevel, ct).ConfigureAwait(false);
            return;
        }

        string tempPath = targetFilePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Dictionary<string, byte[]> merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            using (S2zArchiveReader reader = S2zArchiveReader.Open(targetFilePath))
            {
                foreach (string entryName in reader.EntryNames)
                    merged[entryName] = await reader.ReadEntryBytesAsync(entryName, ct).ConfigureAwait(false);
            }

            foreach (KeyValuePair<string, byte[]> kvp in filesToUpdate)
                merged[ModPackage.NormalizeZipPath(kvp.Key)] = kvp.Value;

            await WriteArchiveAsync(tempPath, merged, compressionLevel, ct).ConfigureAwait(false);
            try
            {
                File.Move(tempPath, targetFilePath, true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                List<FileLockingProcess> lockers = FileLockDetector.GetLockingProcesses(targetFilePath);
                string lockDiagnostic = FileLockDetector.FormatLockingProcessesDiagnostic(targetFilePath);
                throw new IOException(
                    $"Failed to atomically update .s2z archive '{targetFilePath}' ({lockers.Count} locking process(es) detected). {lockDiagnostic} Close hon_x64.exe or the locking process before injecting.",
                    moveEx);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    Debug.WriteLine($"[S2zArchiveWriter] Failed to delete temporary update archive '{tempPath}': {cleanupEx.Message}");
                }
            }
        }
    }

    public static async Task AppendOrUpdateArchiveAsync(
        IVirtualFileSystem vfs,
        string targetVirtualPath,
        IReadOnlyDictionary<string, byte[]> filesToUpdate,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVirtualPath);
        ArgumentNullException.ThrowIfNull(filesToUpdate);

        if (!vfs.FileExists(targetVirtualPath))
        {
            await WriteArchiveAsync(vfs, targetVirtualPath, filesToUpdate, compressionLevel, ct).ConfigureAwait(false);
            return;
        }

        Dictionary<string, byte[]> merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using (S2zArchiveReader reader =
               await S2zArchiveReader.OpenAsync(vfs, targetVirtualPath, ct).ConfigureAwait(false))
        {
            foreach (string entryName in reader.EntryNames)
                merged[entryName] = await reader.ReadEntryBytesAsync(entryName, ct).ConfigureAwait(false);
        }

        foreach (KeyValuePair<string, byte[]> kvp in filesToUpdate)
            merged[ModPackage.NormalizeZipPath(kvp.Key)] = kvp.Value;

        await WriteArchiveAsync(vfs, targetVirtualPath, merged, compressionLevel, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wave 4 (#229) &amp; [L46-P1-03]: Validates an in-memory `.s2z` archive image against torn writes,
    /// 1.5 GiB signed 32-bit offset overflow, `Zip64` EOCD/Locator headers (`0x06064B50` / `0x07064B50`),
    /// and central directory bit-flips simulated by `DeterministicStorageFaultSimulator`.
    /// </summary>
    public static bool ValidateArchiveBufferIntegrity(ReadOnlySpan<byte> archiveBytes, out int entryCount)
    {
        entryCount = 0;
        // Minimum ZIP EOCD size is 22 bytes, must not exceed 1.5 GiB signed 32-bit K2 limit
        if (archiveBytes.Length < 22 || archiveBytes.Length > MaxSafeS2zArchiveBytes)
        {
            return false;
        }

        if (ContainsZip64RecordsOrOverflowedEocd(archiveBytes))
        {
            Debug.WriteLine("[S2zArchiveWriter] Rejected .s2z buffer containing Zip64 EOCD/Locator records or 32-bit sentinel overflow.");
            return false;
        }

        try
        {
            using MemoryStream ms = new MemoryStream(archiveBytes.ToArray(), writable: false);
            using ZipArchive archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxSafeS2zEntryCount)
            {
                return false;
            }

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length > MaxSafeS2zEntryUncompressedBytes || entry.CompressedLength > MaxSafeS2zArchiveBytes)
                {
                    return false;
                }

                using Stream entryStream = entry.Open();
                byte[] scratch = new byte[256];
                while (entryStream.Read(scratch, 0, scratch.Length) > 0)
                {
                }
                entryCount++;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[S2zArchiveWriter] Archive buffer integrity validation failed: {ex.Message}");
            entryCount = 0;
            return false;
        }
    }

    internal static void EnsureStreamArchiveWithinK2Bounds(Stream archiveStream, long archiveStartOffset)
    {
        long archiveLength = archiveStream.Length - archiveStartOffset;
        if (archiveLength < 22)
        {
            throw new InvalidOperationException(
                $"[S2zArchiveWriter] Generated .s2z archive is truncated ({archiveLength} bytes < 22-byte minimum EOCD).");
        }

        if (archiveLength > MaxSafeS2zArchiveBytes)
        {
            throw new InvalidOperationException(
                $"[S2zArchiveWriter] Generated .s2z archive size ({archiveLength:N0} bytes) exceeds K2 signed 32-bit 1.5 GiB limit ({MaxSafeS2zArchiveBytes:N0} bytes). Split assets across multiple .s2z archives.");
        }

        if (!archiveStream.CanRead)
        {
            return;
        }

        long savedPos = archiveStream.Position;
        try
        {
            int tailLen = (int)Math.Min(archiveLength, 65_636L);
            byte[] tail = new byte[tailLen];
            archiveStream.Position = archiveStream.Length - tailLen;
            int totalRead = 0;
            while (totalRead < tailLen)
            {
                int r = archiveStream.Read(tail, totalRead, tailLen - totalRead);
                if (r <= 0) break;
                totalRead += r;
            }

            if (ContainsZip64RecordsOrOverflowedEocd(tail.AsSpan(0, totalRead)))
            {
                throw new InvalidOperationException(
                    "[S2zArchiveWriter] Generated .s2z archive contains Zip64 EOCD/Locator records (0x06064B50 / 0x07064B50) or 32-bit EOCD sentinel overflow, which crashes k2_x64.dll. Ensure allowZip64 = false and < 65,535 entries / < 1.5 GiB.");
            }
        }
        finally
        {
            archiveStream.Position = savedPos;
        }
    }

    public static bool ContainsZip64RecordsOrOverflowedEocd(ReadOnlySpan<byte> tailBytes)
    {
        if (tailBytes.Length < 22)
        {
            return true;
        }

        int searchStart = Math.Max(0, tailBytes.Length - 65_636);
        int eocdOffset = -1;

        for (int i = tailBytes.Length - 22; i >= searchStart; i--)
        {
            uint sig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.Slice(i, 4));
            if (sig == ZipEocdSignature)
            {
                ushort commentLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tailBytes.Slice(i + 20, 2));
                if (i + 22 + commentLen == tailBytes.Length)
                {
                    eocdOffset = i;
                    break;
                }
            }
        }

        if (eocdOffset < 0)
        {
            return true;
        }

        ushort diskEntries = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tailBytes.Slice(eocdOffset + 8, 2));
        ushort totalEntries = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tailBytes.Slice(eocdOffset + 10, 2));
        uint cdSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.Slice(eocdOffset + 12, 4));
        uint cdOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.Slice(eocdOffset + 16, 4));

        if (diskEntries == 0xFFFF ||
            totalEntries == 0xFFFF ||
            cdSize == 0xFFFFFFFFu ||
            cdOffset == 0xFFFFFFFFu ||
            cdOffset > (uint)MaxSafeS2zArchiveBytes ||
            (ulong)cdOffset + cdSize > (ulong)MaxSafeS2zArchiveBytes)
        {
            return true;
        }

        // Check immediately preceding 20-byte Zip64 EOCD Locator (0x07064B50) and any Zip64 EOCD (0x06064B50) in tail
        if (eocdOffset >= 20)
        {
            uint locatorSig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.Slice(eocdOffset - 20, 4));
            if (locatorSig == Zip64EocdLocatorSignature)
            {
                return true;
            }
        }

        int zip64ScanStart = Math.Max(0, eocdOffset - 128);
        for (int j = zip64ScanStart; j <= eocdOffset - 4; j++)
        {
            uint sig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.Slice(j, 4));
            if (sig == Zip64EocdSignature || sig == Zip64EocdLocatorSignature)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CountingWriteStream(Stream inner, long maxBytes) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly long _maxBytes = maxBytes;
        private long _written;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void EnsureCapacity(int delta)
        {
            long next = _written + delta;
            long currentSpan = _inner.CanSeek ? _inner.Position + delta : next;
            if (next > _maxBytes || currentSpan > _maxBytes)
            {
                throw new InvalidOperationException(
                    $"[S2zArchiveWriter] Archive write exceeded K2 signed 32-bit 1.5 GiB ceiling ({Math.Max(next, currentSpan):N0} > {_maxBytes:N0} bytes). Split assets across multiple .s2z archives.");
            }

            _written = next;
        }
    }
}
