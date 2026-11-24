// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ASPIRE.Common.Services.Vfs;

/// <summary>
///     Provides cryptographic integrity verification and structural security analysis for
///     Heroes of Newerth .s2z archive packages before they are mounted into the VFS.
/// </summary>
public static class S2zPackageSecurityValidator
{
    public const uint ZipLocalFileHeaderMagic = 0x04034B50; // PK\x03\x04
    public const uint ZipEmptyArchiveMagic = 0x06054B50;    // PK\x05\x06 (Empty zip with only EOCD)
    public const uint ZipSpannedArchiveMagic = 0x08074B50;  // PK\x07\x08

    private const long MaxZipBombExpansionRatio = 100; // 100:1 max compression ratio
    private const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024; // 2 GB safety ceiling
    private const int MaxEntryPathLength = 260;

    /// <summary>
    ///     Computes the SHA-256 hexadecimal hash of the specified file.
    /// </summary>
    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for hash calculation", filePath);

        FileStreamOptions options = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 131072
        };

        await using FileStream stream = new(filePath, options);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    ///     Verifies that a local file strictly matches the expected SHA-256 hash.
    /// </summary>
    public static async Task<(bool IsValid, string ActualHashHex)> VerifyPackageHashAsync(
        string filePath,
        string expectedSha256Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256Hex);

        string actualHashHex = await ComputeSha256HexAsync(filePath, cancellationToken).ConfigureAwait(false);
        bool isValid = string.Equals(actualHashHex, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase);

        return (isValid, actualHashHex);
    }

    /// <summary>
    ///     Performs structural validation on an .s2z archive to guarantee that:
    ///     1. No entry attempts path traversal (zip-slip) using '..' or absolute path indicators.
    ///     2. No entry exceeds path length constraints.
    ///     3. Archive does not exhibit compression ratio or expansion profiles indicative of a decompression bomb.
    /// </summary>
    public static (bool IsSafe, string? ViolationReason) VerifyArchiveStructure(string s2zFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(s2zFilePath);

        if (!File.Exists(s2zFilePath))
            return (false, $"Archive file does not exist: {s2zFilePath}");

        try
        {
            using FileStream stream = File.OpenRead(s2zFilePath);
            if (stream.Length < 4)
            {
                return (false, $"Archive file is too small ({stream.Length} bytes) to be a valid zip archive");
            }

            Span<byte> magicBytes = stackalloc byte[4];
            int read = stream.Read(magicBytes);
            if (read < 4)
            {
                return (false, "Could not read archive magic header");
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
            if (magic != ZipLocalFileHeaderMagic && magic != ZipEmptyArchiveMagic && magic != ZipSpannedArchiveMagic)
            {
                return (false, $"Invalid archive magic header 0x{magic:X8}; expected PKZip signature (PK\\x03\\x04)");
            }

            stream.Position = 0;
            using ZipArchive archive = new(stream, ZipArchiveMode.Read);

            long totalCompressed = 0;
            long totalUncompressed = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string fullName = entry.FullName;

                // Check 1: Path Traversal (Zip-Slip)
                if (fullName.Contains("..", StringComparison.Ordinal) ||
                    fullName.StartsWith('/') ||
                    fullName.StartsWith('\\') ||
                    (fullName.Length > 1 && fullName[1] == ':'))
                {
                    return (false, $"Zip-slip path traversal detected in entry: '{fullName}'");
                }

                // Check 2: Entry Path Length Bounds
                if (fullName.Length > MaxEntryPathLength)
                {
                    return (false, $"Entry path length ({fullName.Length}) exceeds maximum allowed ({MaxEntryPathLength}): '{fullName}'");
                }

                totalCompressed += entry.CompressedLength;
                totalUncompressed += entry.Length;

                // Check 3: Individual Entry Zip-Bomb Expansion Ratio
                if (entry.CompressedLength > 0 && entry.Length > 10 * 1024 * 1024)
                {
                    long ratio = entry.Length / entry.CompressedLength;
                    if (ratio > MaxZipBombExpansionRatio)
                    {
                        return (false, $"Suspicious compression ratio ({ratio}:1) in entry '{fullName}' indicates potential zip bomb");
                    }
                }
            }

            // Check 4: Cumulative Expansion Ceiling
            if (totalUncompressed > MaxTotalUncompressedBytes)
            {
                return (false, $"Total uncompressed archive size ({totalUncompressed} bytes) exceeds maximum safety limit ({MaxTotalUncompressedBytes} bytes)");
            }

            return (true, null);
        }
        catch (InvalidDataException ex)
        {
            return (false, $"Archive data is corrupt or malformed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected archive validation error: {ex.Message}");
        }
    }
}
