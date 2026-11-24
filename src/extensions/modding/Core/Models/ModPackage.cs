// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security;
using System.Text;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Core.Models;

/// <summary>
///     Represents an in-memory or on-disk loaded HoN mod package (.honmod or directory).
/// </summary>
public sealed class ModPackage : IDisposable
{
    private readonly ZipArchive? _archive;
    private readonly bool _ownsArchive;
    private bool _disposed;

    public ModPackage()
    {
    }

    [SetsRequiredMembers]
    public ModPackage(string filePath, ModManifest manifest, ZipArchive archive, bool ownsArchive = true,
        IVirtualFileSystem? vfs = null)
    {
        FilePath = filePath;
        Manifest = manifest;
        _archive = archive;
        _ownsArchive = ownsArchive;
        Vfs = vfs;
    }

    [SetsRequiredMembers]
    public ModPackage(string filePath, ModManifest manifest, IVirtualFileSystem vfs, bool isDirectorySource = false)
    {
        FilePath = filePath;
        Manifest = manifest;
        Vfs = vfs;
        IsDirectorySource = isDirectorySource;
    }

    public required string FilePath { get; init; }
    public required ModManifest Manifest { get; init; }
    public bool IsEnabled { get; set; }
    public byte[]? IconData { get; set; }
    public long FileSizeBytes { get; init; }
    public string Sha256Hash { get; init; } = string.Empty;
    public bool IsDirectorySource { get; init; }
    public IVirtualFileSystem? Vfs { get; }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsArchive) _archive?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Reads a payload file from the mod package as raw bytes.
    /// </summary>
    public async Task<byte[]> GetPayloadFileAsync(string relativePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string normalizedPath = NormalizeZipPath(relativePath);

        if (IsDirectorySource)
        {
            if (Vfs != null)
            {
                string fullPath = string.IsNullOrEmpty(FilePath)
                    ? normalizedPath
                    : $"{FilePath.TrimEnd('/')}/{normalizedPath}".TrimStart('/');
                if (!Vfs.FileExists(fullPath))
                    throw new FileNotFoundException(
                        $"Payload file '{relativePath}' not found in mod VFS directory '{FilePath}'");
                return await Vfs.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            }

            string diskPath = ResolveContainedDiskPath(FilePath, normalizedPath);
            if (!File.Exists(diskPath))
                throw new FileNotFoundException(
                    $"Payload file '{relativePath}' not found in mod directory '{FilePath}'");
            return await File.ReadAllBytesAsync(diskPath, ct).ConfigureAwait(false);
        }

        if (_archive == null)
        {
            if (Vfs != null && Vfs.FileExists(FilePath))
            {
                Stream s = await Vfs.OpenReadAsync(FilePath, ct).ConfigureAwait(false);
                using ZipArchive zip = new(s, ZipArchiveMode.Read, false);
                return await ExtractEntryBytesAsync(zip, normalizedPath, relativePath, ct).ConfigureAwait(false);
            }

            if (File.Exists(FilePath))
            {
                using FileStream fs = File.OpenRead(FilePath);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read, false);
                return await ExtractEntryBytesAsync(zip, normalizedPath, relativePath, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"No active archive stream for mod package '{Manifest.Name}'");
        }

        return await ExtractEntryBytesAsync(_archive, normalizedPath, relativePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads a payload file from the mod package as raw bytes.
    /// </summary>
    public Task<byte[]> GetPayloadBytesAsync(string relativePath, CancellationToken ct = default)
    {
        return GetPayloadFileAsync(relativePath, ct);
    }

    /// <summary>
    ///     Reads a payload file from the mod package as text.
    /// </summary>
    public async Task<string> GetPayloadTextAsync(string relativePath, CancellationToken ct = default)
    {
        byte[] bytes = await GetPayloadFileAsync(relativePath, ct).ConfigureAwait(false);
        string text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return text;
    }

    /// <summary>
    ///     Checks if a payload file exists inside the package.
    /// </summary>
    public bool HasPayloadFile(string relativePath)
    {
        string normalizedPath = NormalizeZipPath(relativePath);
        if (IsDirectorySource)
        {
            if (Vfs != null)
            {
                string fullPath = string.IsNullOrEmpty(FilePath)
                    ? normalizedPath
                    : $"{FilePath.TrimEnd('/')}/{normalizedPath}".TrimStart('/');
                return Vfs.FileExists(fullPath);
            }

            string fullPathDisk = ResolveContainedDiskPath(FilePath, normalizedPath);
            return File.Exists(fullPathDisk);
        }

        if (_archive != null)
            return MatchesSafeArchiveEntry(_archive, normalizedPath);

        if (Vfs != null && Vfs.FileExists(FilePath))
        {
            using Stream s = Vfs.OpenRead(FilePath);
            using ZipArchive zip = new(s, ZipArchiveMode.Read, false);
            return MatchesSafeArchiveEntry(zip, normalizedPath);
        }

        if (File.Exists(FilePath))
        {
            using FileStream fs = File.OpenRead(FilePath);
            using ZipArchive zip = new(fs, ZipArchiveMode.Read, false);
            return MatchesSafeArchiveEntry(zip, normalizedPath);
        }

        return false;
    }

    private static bool MatchesSafeArchiveEntry(ZipArchive archive, string normalizedPath)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            EnsureSafeZipEntry(entry);
            if (IsArchiveDirectoryPlaceholder(entry))
            {
                continue;
            }

            if (NormalizeZipPath(entry.FullName).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<byte[]> ExtractEntryBytesAsync(ZipArchive archive, string normalizedPath,
        string originalPath, CancellationToken ct)
    {
        ZipArchiveEntry? matched = null;
        foreach (ZipArchiveEntry candidate in archive.Entries)
        {
            EnsureSafeZipEntry(candidate);
            if (IsArchiveDirectoryPlaceholder(candidate))
            {
                continue;
            }

            if (NormalizeZipPath(candidate.FullName).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                matched = candidate;
                break;
            }
        }

        if (matched == null) throw new FileNotFoundException($"Entry '{originalPath}' not found in honmod archive.");

        await using Stream entryStream = matched.Open();
        using MemoryStream ms = new(matched.Length is > 0 and <= int.MaxValue ? (int)matched.Length : 4096);
        await entryStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    public static bool IsArchiveDirectoryPlaceholder(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.IsNullOrEmpty(entry.Name) &&
               (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'));
    }

    public static void EnsureSafeZipEntry(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        bool isUnixSymlink = unixFileType == 0xA000;
        bool isWindowsReparsePoint = (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
        if (isUnixSymlink || isWindowsReparsePoint)
        {
            string alert =
                $"[SECURITY-ALERT][MODDING-ZIPSLIP] Blocked symlink/reparse ZIP entry '{entry.FullName}' (ExternalAttributes=0x{entry.ExternalAttributes:X8}).";
            Console.Error.WriteLine(alert);
            throw new SecurityException(alert);
        }

        if (IsArchiveDirectoryPlaceholder(entry))
        {
            string trimmedDir = entry.FullName.Replace('\\', '/').Trim('/');
            if (trimmedDir.Length > 0)
            {
                _ = NormalizeZipPath(trimmedDir);
            }

            return;
        }

        _ = NormalizeZipPath(entry.FullName);
    }

    public static string NormalizeZipPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            string emptyAlert = "[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected null or empty mod archive path.";
            Console.Error.WriteLine(emptyAlert);
            throw new SecurityException(emptyAlert);
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            string nullAlert = "[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected mod path containing NUL byte.";
            Console.Error.WriteLine(nullAlert);
            throw new SecurityException(nullAlert);
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            string colonAlert =
                $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected mod path '{path}' containing drive or NTFS ADS colon specifier.";
            Console.Error.WriteLine(colonAlert);
            throw new SecurityException(colonAlert);
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith("\\/", StringComparison.Ordinal) ||
            path.StartsWith("/\\", StringComparison.Ordinal))
        {
            string uncAlert = $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected UNC/network mod path '{path}'.";
            Console.Error.WriteLine(uncAlert);
            throw new SecurityException(uncAlert);
        }

        for (int i = 0; i < path.Length; i++)
        {
            if (char.IsControl(path[i]))
            {
                string ctrlAlert =
                    $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected mod path '{path}' containing control character 0x{(int)path[i]:X2}.";
                Console.Error.WriteLine(ctrlAlert);
                throw new SecurityException(ctrlAlert);
            }
        }

        string unified = path.Replace('\\', '/');
        if (unified.StartsWith('/'))
        {
            // Allow at most a single K2 virtual-root leading slash (e.g. "/ui/hud.interface"); "//" was rejected above
            unified = unified[1..];
        }

        string[] rawSegments = unified.Split('/');
        List<string> cleanSegments = new List<string>(rawSegments.Length);
        foreach (string seg in rawSegments)
        {
            if (seg.Length == 0)
            {
                continue;
            }

            if (seg == "." || seg == "..")
            {
                string traversalAlert =
                    $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected directory traversal segment '{seg}' in mod path '{path}'.";
                Console.Error.WriteLine(traversalAlert);
                throw new SecurityException(traversalAlert);
            }

            cleanSegments.Add(seg);
        }

        if (cleanSegments.Count == 0)
        {
            string rootOnlyAlert = $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected empty normalized mod path from '{path}'.";
            Console.Error.WriteLine(rootOnlyAlert);
            throw new SecurityException(rootOnlyAlert);
        }

        return string.Join('/', cleanSegments);
    }

    public static string ResolveContainedDiskPath(string rootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string normalized = NormalizeZipPath(relativePath);

        string fullRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootWithSep = fullRoot + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));

        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootWithSep, pathComparison))
        {
            string escapeAlert =
                $"[SECURITY-ALERT][MODDING-ZIPSLIP] Blocked path escape '{relativePath}' -> '{candidate}' outside '{rootWithSep}'.";
            Console.Error.WriteLine(escapeAlert);
            throw new SecurityException(escapeAlert);
        }

        EnsureNoReparsePointsWithinRoot(fullRoot, candidate, pathComparison);
        return candidate;
    }

    private static void EnsureNoReparsePointsWithinRoot(
        string fullRoot,
        string candidateFullPath,
        StringComparison pathComparison)
    {
        string? current = candidateFullPath;
        while (!string.IsNullOrEmpty(current) &&
               current.Length > fullRoot.Length &&
               current.StartsWith(fullRoot, pathComparison))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileSystemInfo fsi = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0 || fsi.LinkTarget != null)
                {
                    string reparseAlert =
                        $"[SECURITY-ALERT][MODDING-ZIPSLIP] Blocked symlink/reparse point '{current}' inside '{fullRoot}'.";
                    Console.Error.WriteLine(reparseAlert);
                    throw new SecurityException(reparseAlert);
                }
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
