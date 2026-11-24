// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: BinaryDeltaPatchEngine.cs
// System: Kongor Protocol Core - Differential Binary Delta Patching Engine
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using ASPIRE.Common.Memory;

namespace ASPIRE.Common.Patching;

/// <summary>
///     High-performance differential binary delta engine for game clients and server binaries.
///     Computes sub-1.5 MB differential patches using sliding-window block matching,
///     Zstandard compression, and SHA-256 cryptographic verification.
/// </summary>
internal sealed class BinaryDeltaPatchEngine : IDeltaPatchEngine
{
    public const uint DeltaMagic = 0x544C444B; // "KDLT" (Kongor Delta)
    public const byte FormatVersion = 1;
    public const byte CompressionZstd = 1;
    public const byte CompressionNone = 0;

    private const byte OpCopy = 0x01;
    private const byte OpInsert = 0x02;
    private const int MatchBlockSize = 16;

    /// <summary>
    ///     Computes a compact binary delta representing the transformation from source to target.
    /// </summary>
    public byte[] CreateDelta(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target)
    {
        byte[] targetSha256 = SHA256.HashData(target);

        // 1. Build Block Match Table on Source
        Dictionary<uint, List<int>> sourceIndex = BuildSourceIndex(source);

        // 2. Generate uncompressed delta operations
        using MemoryStream uncompressedOpsStream = NexusMemoryStreamManager.Shared.GetStream("DeltaOpsGen");
        using (BinaryWriter writer = new(uncompressedOpsStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            List<byte> pendingLiterals = new(1024);
            int tOffset = 0;

            while (tOffset < target.Length)
            {
                if (tOffset + MatchBlockSize <= target.Length)
                {
                    uint blockHash = ComputeBlockHash(target.Slice(tOffset, MatchBlockSize));
                    int bestMatchSourceOffset = -1;
                    int bestMatchLength = 0;

                    if (sourceIndex.TryGetValue(blockHash, out List<int>? candidateOffsets))
                    {
                        for (int c = 0; c < candidateOffsets.Count; c++)
                        {
                            int sCandidate = candidateOffsets[c];
                            if (source.Slice(sCandidate, MatchBlockSize).SequenceEqual(target.Slice(tOffset, MatchBlockSize)))
                            {
                                int matchLen = MatchBlockSize;
                                while (sCandidate + matchLen < source.Length &&
                                       tOffset + matchLen < target.Length &&
                                       source[sCandidate + matchLen] == target[tOffset + matchLen])
                                {
                                    matchLen++;
                                }

                                if (matchLen > bestMatchLength)
                                {
                                    bestMatchLength = matchLen;
                                    bestMatchSourceOffset = sCandidate;
                                }
                            }
                        }
                    }

                    if (bestMatchLength >= MatchBlockSize)
                    {
                        // Flush pending literals first
                        FlushLiterals(writer, pendingLiterals);

                        // Emit OP_COPY
                        writer.Write(OpCopy);
                        writer.Write(bestMatchSourceOffset);
                        writer.Write(bestMatchLength);

                        tOffset += bestMatchLength;
                        continue;
                    }
                }

                pendingLiterals.Add(target[tOffset]);
                tOffset++;
            }

            // Flush any remaining literals
            FlushLiterals(writer, pendingLiterals);
        }

        byte[] uncompressedOps = uncompressedOpsStream.ToArray();

        // 3. Compress Operations Body with Zstandard
        using MemoryStream finalDeltaStream = NexusMemoryStreamManager.Shared.GetStream("FinalDelta");
        using (BinaryWriter headerWriter = new(finalDeltaStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // Header: Magic (4B), Version (1B), Compression (1B), SourceLen (8B), TargetLen (8B), TargetSha256 (32B)
            headerWriter.Write(DeltaMagic);
            headerWriter.Write(FormatVersion);
            headerWriter.Write(CompressionZstd);
            headerWriter.Write((long)source.Length);
            headerWriter.Write((long)target.Length);
            headerWriter.Write(targetSha256);
        }

        using (ZstandardStream zstd = new(finalDeltaStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zstd.Write(uncompressedOps, 0, uncompressedOps.Length);
        }

        return finalDeltaStream.ToArray();
    }

    /// <summary>
    ///     Applies a binary delta to source bytes to reconstruct the exact target payload.
    /// </summary>
    public byte[] ApplyDelta(ReadOnlySpan<byte> source, ReadOnlySpan<byte> delta, string? expectedSha256 = null)
    {
        if (delta.Length < 4 + 1 + 1 + 8 + 8 + 32)
        {
            throw new InvalidDataException("Delta payload is smaller than required header.");
        }

        // 1. Validate Header
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(delta[0..4]);
        if (magic != DeltaMagic)
        {
            throw new InvalidDataException($"Invalid delta magic: 0x{magic:X8}, expected 0x{DeltaMagic:X8}.");
        }

        byte version = delta[4];
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported delta format version: {version}.");
        }

        byte compression = delta[5];
        long sourceLength = BinaryPrimitives.ReadInt64LittleEndian(delta[6..14]);
        long targetLength = BinaryPrimitives.ReadInt64LittleEndian(delta[14..22]);
        ReadOnlySpan<byte> headerTargetSha256 = delta.Slice(22, 32);

        if (source.Length != sourceLength)
        {
            throw new InvalidDataException($"Source buffer length mismatch: got {source.Length}, expected {sourceLength}.");
        }

        // 2. Decompress Operations Body
        ReadOnlySpan<byte> bodyCompressed = delta[54..];
        byte[] uncompressedOps;

        if (compression == CompressionZstd)
        {
            using MemoryStream compressedStream = new(bodyCompressed.ToArray());
            using ZstandardStream zstd = new(compressedStream, CompressionMode.Decompress);
            using MemoryStream decompressedStream = new();
            zstd.CopyTo(decompressedStream);
            uncompressedOps = decompressedStream.ToArray();
        }
        else
        {
            uncompressedOps = bodyCompressed.ToArray();
        }

        // 3. Execute Operations into Target Buffer
        byte[] target = new byte[targetLength];
        int tCursor = 0;

        using MemoryStream opsStream = new(uncompressedOps);
        using BinaryReader reader = new(opsStream);

        while (opsStream.Position < opsStream.Length)
        {
            byte op = reader.ReadByte();
            if (op == OpCopy)
            {
                int sOffset = reader.ReadInt32();
                int length = reader.ReadInt32();

                if (sOffset < 0 || sOffset + length > source.Length)
                {
                    throw new InvalidDataException($"OP_COPY source range [0x{sOffset:X}..0x{sOffset + length:X}] out of bounds.");
                }

                if (tCursor + length > target.Length)
                {
                    throw new InvalidDataException($"OP_COPY target overflow: cursor {tCursor} + len {length} > target {target.Length}.");
                }

                source.Slice(sOffset, length).CopyTo(target.AsSpan(tCursor, length));
                tCursor += length;
            }
            else if (op == OpInsert)
            {
                int length = reader.ReadInt32();
                if (length < 0 || tCursor + length > target.Length)
                {
                    throw new InvalidDataException($"OP_INSERT target overflow: cursor {tCursor} + len {length} > target {target.Length}.");
                }

                byte[] literalBytes = reader.ReadBytes(length);
                if (literalBytes.Length != length)
                {
                    throw new InvalidDataException("Unexpected EOF while reading OP_INSERT literals.");
                }

                literalBytes.CopyTo(target.AsSpan(tCursor, length));
                tCursor += length;
            }
            else
            {
                throw new InvalidDataException($"Unknown delta opcode: 0x{op:X2}.");
            }
        }

        if (tCursor != target.Length)
        {
            throw new InvalidDataException($"Target length mismatch after patching: got {tCursor}, expected {target.Length}.");
        }

        // 4. Verify SHA-256 Integrity
        byte[] computedSha256 = SHA256.HashData(target);
        if (!CryptographicOperations.FixedTimeEquals(computedSha256, headerTargetSha256))
        {
            throw new InvalidDataException("Patched target SHA-256 verification failed (corrupt delta).");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string computedHex = Convert.ToHexStringLower(computedSha256);
            if (!string.Equals(computedHex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Target SHA-256 ({computedHex}) did not match expected hash ({expectedSha256}).");
            }
        }

        return target;
    }

    /// <summary>
    ///     Streams a binary delta into a source file and writes the reconstructed target file
    ///     using atomic temporary file swapping and SHA-256 verification.
    /// </summary>
    public async Task ApplyDeltaToFileAsync(
        string sourceFilePath,
        Stream deltaStream,
        string targetFilePath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deltaStream);
        ArgumentNullException.ThrowIfNull(targetFilePath);

        byte[] sourceBytes = File.Exists(sourceFilePath)
            ? await File.ReadAllBytesAsync(sourceFilePath, cancellationToken)
            : [];

        using MemoryStream ms = new();
        await deltaStream.CopyToAsync(ms, cancellationToken);
        byte[] deltaBytes = ms.ToArray();

        byte[] targetBytes = ApplyDelta(sourceBytes, deltaBytes, expectedSha256);

        string targetDir = Path.GetDirectoryName(targetFilePath) ?? AppContext.BaseDirectory;
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string tempFilePath = $"{targetFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempFilePath, targetBytes, cancellationToken);
            File.Move(tempFilePath, targetFilePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
            throw;
        }
    }

    private static void FlushLiterals(BinaryWriter writer, List<byte> literals)
    {
        if (literals.Count == 0) return;

        writer.Write(OpInsert);
        writer.Write(literals.Count);
        writer.Write(literals.ToArray());
        literals.Clear();
    }

    private static Dictionary<uint, List<int>> BuildSourceIndex(ReadOnlySpan<byte> source)
    {
        Dictionary<uint, List<int>> index = new();
        if (source.Length < MatchBlockSize) return index;

        // Step by 8 bytes to balance index size and match resolution
        const int step = 8;
        for (int i = 0; i <= source.Length - MatchBlockSize; i += step)
        {
            uint hash = ComputeBlockHash(source.Slice(i, MatchBlockSize));
            if (!index.TryGetValue(hash, out List<int>? offsets))
            {
                offsets = new List<int>(4);
                index[hash] = offsets;
            }
            offsets.Add(i);
        }

        return index;
    }

    private static uint ComputeBlockHash(ReadOnlySpan<byte> block)
    {
        // FNV-1a 32-bit fast block hash
        uint hash = 2166136261;
        for (int i = 0; i < block.Length; i++)
        {
            hash ^= block[i];
            hash *= 16777619;
        }
        return hash;
    }
}
