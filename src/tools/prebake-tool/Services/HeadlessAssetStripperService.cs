// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessAssetStripperService.cs
// System: prebake-tool - High-Density Headless Server Asset Stripping
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Prebaking.Generators;

/// <summary>
/// Summary report of the headless server asset stripping operation.
/// </summary>
public sealed record HeadlessStrippingResult(
    bool Success,
    string SourceDirectory,
    string TargetDirectory,
    int TotalFilesScanned,
    int FilesPreserved,
    int FilesStripped,
    long OriginalBytes,
    long StrippedBytes,
    double ReductionPercentage,
    long ElapsedMilliseconds);

/// <summary>
/// Strips non-essential media assets (textures, sounds, videos, redundant UI graphics) from a game
/// server directory distribution, producing a lightweight (~200 MB) headless simulation-only image.
/// Headless dedicated game servers run with 'vid_renderer none' and 'sound_driver none', requiring only
/// collision, navigation, gameplay XML entities, bot scripts, balance CVars, and Linux ELF/shared libraries.
/// </summary>
public static class HeadlessAssetStripperService
{
    private static readonly HashSet<string> StrippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Textures & 2D Art
        ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".psd", ".tif", ".tiff",
        // Audio & Sound Banks
        ".wav", ".mp3", ".ogg", ".fsb", ".fev", ".bank", ".sfx", ".flac",
        // Video & Cutscenes
        ".bik", ".webm", ".mp4", ".avi"
    };

    private static readonly HashSet<string> PreservedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Native Binaries & Compatibility
        ".so", "", ".dll", ".exe",
        // Configurations & Engine Scripts
        ".cfg", ".xml", ".lua", ".resources", ".upgrades", ".txt", ".str",
        // Simulation Entities & Collision
        ".entity", ".mdf", ".gamemechanics", ".clip", ".smdl", ".model", ".material", ".effect",
        // Packages & Maps
        ".s2z"
    };

    public static async Task<HeadlessStrippingResult> StripServerDistributionAsync(
        string sourceDir,
        string targetDir,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source server directory does not exist: {sourceDir}");
        }

        Directory.CreateDirectory(targetDir);

        Stopwatch sw = Stopwatch.StartNew();
        int totalScanned = 0;
        int preservedCount = 0;
        int strippedCount = 0;
        long originalBytes = 0;
        long strippedBytes = 0;

        DirectoryInfo sourceInfo = new(sourceDir);
        FileInfo[] allFiles = sourceInfo.GetFiles("*", SearchOption.AllDirectories);
        totalScanned = allFiles.Length;

        logger?.LogInformation("[HeadlessStripper] Scanning {Count} files in source directory '{Source}'...", totalScanned, sourceDir);

        foreach (FileInfo file in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            originalBytes += file.Length;
            string extension = file.Extension;
            string relativePath = Path.GetRelativePath(sourceDir, file.FullName);

            // Determine if this file should be stripped
            bool shouldStrip = ShouldStripFile(relativePath, extension);

            if (shouldStrip)
            {
                strippedCount++;
            }
            else
            {
                preservedCount++;
                strippedBytes += file.Length;

                string destPath = Path.Combine(targetDir, relativePath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // Copy preserved file
                File.Copy(file.FullName, destPath, overwrite: true);
            }
        }

        sw.Stop();

        double reductionPercent = originalBytes > 0
            ? (1.0 - ((double)strippedBytes / originalBytes)) * 100.0
            : 0.0;

        logger?.LogInformation(
            "[HeadlessStripper] Completed stripping in {Ms} ms. Preserved {Preserved}/{Total} files. Size reduced from {OrigMb:F1} MB to {StripMb:F1} MB ({Red:F1}% reduction).",
            sw.ElapsedMilliseconds, preservedCount, totalScanned, originalBytes / (1024.0 * 1024.0), strippedBytes / (1024.0 * 1024.0), reductionPercent);

        return await Task.FromResult(new HeadlessStrippingResult(
            Success: true,
            SourceDirectory: sourceDir,
            TargetDirectory: targetDir,
            TotalFilesScanned: totalScanned,
            FilesPreserved: preservedCount,
            FilesStripped: strippedCount,
            OriginalBytes: originalBytes,
            StrippedBytes: strippedBytes,
            ReductionPercentage: reductionPercent,
            ElapsedMilliseconds: sw.ElapsedMilliseconds));
    }

    /// <summary>
    /// Evaluates whether a specific relative path and extension should be stripped for a headless dedicated server.
    /// </summary>
    public static bool ShouldStripFile(string relativePath, string extension)
    {
        // 1. Files in UI directory that are pure texture/image assets
        if (relativePath.StartsWith("game/ui", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("game\\ui", StringComparison.OrdinalIgnoreCase))
        {
            if (StrippedExtensions.Contains(extension))
            {
                return true;
            }
        }

        // 2. Pure sound directories (music, sounds, speech)
        if (relativePath.Contains("/sounds/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("\\sounds\\", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("/music/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("\\music\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Known media extension match
        if (StrippedExtensions.Contains(extension))
        {
            return true;
        }

        return false;
    }
}
