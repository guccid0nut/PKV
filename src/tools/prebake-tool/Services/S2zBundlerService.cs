// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO.Compression;
using Kongor.Modding.Core.Packaging;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Prebaking.Generators;

public static class S2zBundlerService
{
    public static async Task<BundlingResult> BundleToS2zAsync(
        string targetArchivePath,
        IReadOnlyDictionary<string, byte[]> archiveFiles,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetArchivePath);
        ArgumentNullException.ThrowIfNull(archiveFiles);

        string? targetDir = Path.GetDirectoryName(targetArchivePath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        string tempArchivePath = targetArchivePath + ".tmp." + Guid.NewGuid().ToString("N");
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            logger?.LogInformation("Packaging {Count} files into S2Z archive: {Path}", archiveFiles.Count,
                targetArchivePath);
            await S2zArchiveWriter.WriteArchiveAsync(tempArchivePath, archiveFiles, compressionLevel, ct)
                .ConfigureAwait(false);

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
                try
                {
                    File.Move(tempArchivePath, targetArchivePath, true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxRetries)
                {
                    logger?.LogWarning(ex,
                        "Failed to move temporary S2Z archive to {Path} on attempt {Attempt}/{Max}. Retrying in 200ms...",
                        targetArchivePath, attempt, maxRetries);
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string message = $"Cannot access or overwrite S2Z archive at '{targetArchivePath}'. " +
                                     "The file is locked by another process (e.g. game client 'hon_x64.exe' is running). " +
                                     "Please close the game client or run with VFS override staging (--target-mode vfs).";
                    logger?.LogError(ex, "{Message}", message);
                    throw new IOException(message, ex);
                }

            sw.Stop();

            FileInfo fi = new(targetArchivePath);
            logger?.LogInformation("Successfully created S2Z archive {Path} ({Bytes} bytes) in {ElapsedMs} ms",
                targetArchivePath, fi.Length, sw.ElapsedMilliseconds);

            return new BundlingResult(
                true,
                targetArchivePath,
                archiveFiles.Count,
                fi.Length,
                sw.ElapsedMilliseconds);
        }
        finally
        {
            if (File.Exists(tempArchivePath))
                try
                {
                    File.Delete(tempArchivePath);
                }
                catch
                {
                    /* ignore */
                }
        }
    }
}

public readonly record struct BundlingResult(
    bool Success,
    string ArchivePath,
    int TotalFiles,
    long TotalBytes,
    long ElapsedMilliseconds);
