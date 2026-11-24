// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     High-performance Zstandard (Zstd) archive and patch decompression service for WILLOWMAKER.
/// </summary>
public interface IZstdArchiveService
{
    bool IsZstandardArchive(string filePath);

    Task DecompressFileAsync(string inputPath, string targetPath, IProgress<double>? progress = null,
        CancellationToken token = default);

    Task DecompressTarZstToDirectoryAsync(string archivePath, string destinationDirectory,
        IProgress<double>? progress = null, CancellationToken token = default);

    Task CompressFileAsync(string inputPath, string targetPath, int compressionLevel = 3,
        CancellationToken token = default);

    Task CompressDirectoryToTarZstAsync(string sourceDirectory, string archivePath, int compressionLevel = 3,
        CancellationToken token = default);
}

public class ZstdArchiveService : IZstdArchiveService
{
    private const uint ZstdMagicNumber = 0xFD2FB528;

    /// <summary>
    ///     Checks if a file starts with the Zstandard magic frame header (0xFD2FB528).
    /// </summary>
    public bool IsZstandardArchive(string filePath)
    {
        if (!File.Exists(filePath)) return false;

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] header = new byte[4];
            int read = stream.Read(header, 0, 4);
            if (read < 4) return false;

            return BitConverter.ToUInt32(header, 0) == ZstdMagicNumber;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Decompresses a standalone .zst file directly to the destination path with optional progress reporting.
    /// </summary>
    public async Task DecompressFileAsync(string inputPath, string targetPath, IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        FileInfo fileInfo = new(inputPath);
        long totalBytes = fileInfo.Length;
        Stopwatch stopwatch = Stopwatch.StartNew();

        await using FileStream inputStream = File.OpenRead(inputPath);
        await using ZstandardStream decompressor = new(inputStream, CompressionMode.Decompress, leaveOpen: true);
        await using FileStream outputStream = File.Create(targetPath);

        byte[] buffer = new byte[128 * 1024]; // 128 KB buffer for optimal throughput
        int bytesRead;
        long totalRead = 0;

        while ((bytesRead = await decompressor.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
        {
            await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            totalRead += bytesRead;

            if (progress != null && totalBytes > 0)
            {
                double percentage = Math.Min(100.0, (double)inputStream.Position / totalBytes * 100.0);
                progress.Report(percentage);
            }
        }

        stopwatch.Stop();
        double elapsedSec = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double throughputMbPerSec = totalRead / (1024.0 * 1024.0) / elapsedSec;
        Console.WriteLine(
            $"[ZstdArchiveService] Decompressed .zst stream ({totalRead / 1024} KB) in {stopwatch.ElapsedMilliseconds} ms ({throughputMbPerSec:F2} MB/s)!");

        progress?.Report(100.0);
    }

    /// <summary>
    ///     Decompresses a .tar.zst archive directly into the destination directory preserving directory hierarchy.
    /// </summary>
    public async Task DecompressTarZstToDirectoryAsync(string archivePath, string destinationDirectory,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!Directory.Exists(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

        FileInfo fileInfo = new(archivePath);
        long totalArchiveBytes = fileInfo.Length;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int entriesCount = 0;

        await using FileStream inputStream = File.OpenRead(archivePath);
        await using ZstandardStream decompressor = new(inputStream, CompressionMode.Decompress, leaveOpen: true);

        // Use standard TAR reader over streaming Zstd decompressor
        await using (TarReader tarReader = new(decompressor))
        {
            TarEntry? entry;
            while ((entry = await tarReader.GetNextEntryAsync(false, token)) != null)
            {
                string destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.Name));

                // Path traversal protection
                if (!destinationPath.StartsWith(Path.GetFullPath(destinationDirectory),
                        StringComparison.OrdinalIgnoreCase)) continue;

                if (entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList)
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    string? parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        Directory.CreateDirectory(parentDir);

                    await entry.ExtractToFileAsync(destinationPath, true, token);
                }

                entriesCount++;

                if (progress != null && totalArchiveBytes > 0)
                {
                    double percentage = Math.Min(100.0, (double)inputStream.Position / totalArchiveBytes * 100.0);
                    progress.Report(percentage);
                }
            }
        }

        stopwatch.Stop();
        double elapsedSec = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double throughputMbPerSec = totalArchiveBytes / (1024.0 * 1024.0) / elapsedSec;
        Console.WriteLine(
            $"[ZstdArchiveService] Extracted {entriesCount} entries from .tar.zst ({totalArchiveBytes / 1024} KB) in {stopwatch.ElapsedMilliseconds} ms ({throughputMbPerSec:F2} MB/s)!");

        progress?.Report(100.0);
    }

    /// <summary>
    ///     Compresses a source file into a .zst file.
    /// </summary>
    public async Task CompressFileAsync(string inputPath, string targetPath, int compressionLevel = 3,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        CompressionLevel compLevel = compressionLevel switch
        {
            <= 1 => CompressionLevel.Fastest,
            >= 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

        await using FileStream inputStream = File.OpenRead(inputPath);
        await using FileStream outputStream = File.Create(targetPath);
        await using ZstandardStream compressor = new(outputStream, compLevel, leaveOpen: true);

        byte[] buffer = new byte[128 * 1024];
        int bytesRead;
        while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
            await compressor.WriteAsync(buffer.AsMemory(0, bytesRead), token);

        await compressor.FlushAsync(token);
    }

    /// <summary>
    ///     Compresses an entire directory into a .tar.zst archive.
    /// </summary>
    public async Task CompressDirectoryToTarZstAsync(string sourceDirectory, string archivePath,
        int compressionLevel = 3, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        string? targetDir = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        CompressionLevel compLevel = compressionLevel switch
        {
            <= 1 => CompressionLevel.Fastest,
            >= 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

        await using FileStream fileOutputStream = File.Create(archivePath);
        await using ZstandardStream compressor = new(fileOutputStream, compLevel, leaveOpen: true);
        await using TarWriter tarWriter = new(compressor, TarEntryFormat.Pax);

        string[] allFiles = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        foreach (string file in allFiles)
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            await tarWriter.WriteEntryAsync(file, relativePath, token);
        }
    }
}
