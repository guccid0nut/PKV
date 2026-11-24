// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ModPacker.cs
// System: Kongor Modding Engine & VFS
// Subsystem: Automated Single-Layer S2Z/HonMod Archive Bundler
// ============================================================================

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Kongor.Modding.Core.Models;

namespace Kongor.Modding.Core.Packaging;

/// <summary>
/// Output format for the mod packager.
/// </summary>
public enum ModPackFormat
{
    S2z,
    HonMod
}

/// <summary>
/// Detailed result metrics of a packaging operation.
/// </summary>
public sealed record ModPackResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public int TotalFiles { get; init; }
    public long UncompressedBytes { get; init; }
    public long CompressedBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public IReadOnlyList<string> PackedEntries { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// High-performance asset package bundler ('kongor mod pack').
/// Scans loose overrides, excludes temporary backup files (.bak, .old, .r2bp),
/// and compiles them into a clean, single-layer .s2z or .honmod archive.
/// </summary>
public static class ModPacker
{
    private static readonly HashSet<string> DefaultExcludeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bak", ".old", ".r2bp", ".tmp", ".log"
    };

    private static readonly HashSet<string> DefaultExcludeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hotreload.trigger", ".hotreload_manifest.json", "thumbs.db", ".ds_store"
    };

    /// <summary>
    /// Packages a directory containing loose overrides into a single clean archive.
    /// </summary>
    public static async Task<ModPackResult> PackDirectoryAsync(
        string sourceDirectory,
        string destinationArchivePath,
        ModPackFormat format = ModPackFormat.S2z,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        IEnumerable<string>? customExcludePatterns = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return new ModPackResult
            {
                Success = false,
                ErrorMessage = $"Source directory does not exist: {sourceDirectory}"
            };
        }

        string fullSource = Path.GetFullPath(sourceDirectory);
        string fullDest = Path.GetFullPath(destinationArchivePath);

        string? destDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        HashSet<string> excludes = new(DefaultExcludeExtensions, StringComparer.OrdinalIgnoreCase);
        if (customExcludePatterns != null)
        {
            foreach (string pat in customExcludePatterns)
            {
                excludes.Add(pat.StartsWith('.') ? pat : "." + pat);
            }
        }

        Dictionary<string, byte[]> filesToPack = new(StringComparer.OrdinalIgnoreCase);
        List<string> packedEntries = [];
        long uncompressedBytes = 0;

        foreach (string filePath in Directory.EnumerateFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath);

            if (excludes.Contains(ext) || DefaultExcludeFiles.Contains(fileName))
            {
                continue;
            }

            // Calculate relative VFS entry path
            string relativePath = Path.GetRelativePath(fullSource, filePath).Replace('\\', '/');
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);

            if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string luaText = Encoding.UTF8.GetString(fileBytes);
                (bool isValid, string? luacErr) =
                    await ASPIRE.Common.Prebaking.Generators.LuaBytecodeCompilerService
                        .ValidateSyntaxWithLuacAsync(luaText, relativePath, ct: ct)
                        .ConfigureAwait(false);
                if (!isValid)
                {
                    return new ModPackResult
                    {
                        Success = false,
                        ErrorMessage = $"Lua 5.1 syntax validation failed for '{relativePath}': {luacErr}"
                    };
                }
            }

            filesToPack[relativePath] = fileBytes;
            packedEntries.Add(relativePath);
            uncompressedBytes += fileBytes.Length;
        }

        if (filesToPack.Count == 0)
        {
            return new ModPackResult
            {
                Success = false,
                ErrorMessage = $"No eligible files found to pack in '{sourceDirectory}'."
            };
        }

        if (format == ModPackFormat.HonMod)
        {
            // Auto-generate a minimal mod.xml manifest if one does not exist
            ModManifest manifest = new()
            {
                Name = Path.GetFileNameWithoutExtension(fullDest),
                Version = "1.0.0",
                Description = "Bundled package created via 'kongor mod pack'",
                Author = "ProjectKongor",
                FileCopies = packedEntries
                    .Select(e => new CopyFileOperation { SourcePath = e, TargetName = e })
                    .ToList()
            };

            await HonModArchiveWriter.WritePackageAsync(fullDest, manifest, filesToPack, ct: ct).ConfigureAwait(false);
        }
        else
        {
            await S2zArchiveWriter.WriteArchiveAsync(fullDest, filesToPack, compressionLevel, ct).ConfigureAwait(false);
        }

        FileInfo destInfo = new(fullDest);
        long compressedBytes = destInfo.Exists ? destInfo.Length : 0;

        string sha256Hex = string.Empty;
        if (destInfo.Exists)
        {
            await using FileStream checkFs = File.OpenRead(fullDest);
            byte[] hash = await SHA256.HashDataAsync(checkFs, ct).ConfigureAwait(false);
            sha256Hex = Convert.ToHexStringLower(hash);
        }

        return new ModPackResult
        {
            Success = true,
            OutputPath = fullDest,
            TotalFiles = filesToPack.Count,
            UncompressedBytes = uncompressedBytes,
            CompressedBytes = compressedBytes,
            Sha256 = sha256Hex,
            PackedEntries = packedEntries.AsReadOnly()
        };
    }
}
