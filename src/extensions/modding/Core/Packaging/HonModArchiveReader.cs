// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Parsing;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Core.Packaging;

/// <summary>
///     High-throughput streaming reader for .honmod (ZIP) packages and unpacked mod directories.
/// </summary>
public static class HonModArchiveReader
{
    /// <summary>
    ///     Reads and parses a .honmod package from file path.
    /// </summary>
    public static async Task<ModPackage> ReadPackageAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (Directory.Exists(filePath)) return await ReadFromDirectoryAsync(filePath, ct).ConfigureAwait(false);

        if (!File.Exists(filePath)) throw new FileNotFoundException($"Mod file not found: {filePath}");

        FileInfo fileInfo = new(filePath);
        string sha256;
        await using (FileStream fs = File.OpenRead(filePath))
        {
            byte[] hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            sha256 = Convert.ToHexStringLower(hashBytes);
        }

        FileStream zipStream = File.OpenRead(filePath);
        try
        {
            return await ReadPackageFromStreamInternalAsync(zipStream, filePath, fileInfo.Length, sha256, true, null,
                ct).ConfigureAwait(false);
        }
        catch
        {
            zipStream.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Reads and parses a .honmod package from an open stream.
    /// </summary>
    public static async Task<ModPackage> ReadPackageAsync(
        Stream archiveStream,
        string packageName = "mod.honmod",
        bool leaveOpen = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        long length = archiveStream.CanSeek ? archiveStream.Length : 0;
        string sha256 = string.Empty;

        if (archiveStream.CanSeek)
        {
            long origPos = archiveStream.Position;
            archiveStream.Position = 0;
            byte[] hashBytes = await SHA256.HashDataAsync(archiveStream, ct).ConfigureAwait(false);
            sha256 = Convert.ToHexStringLower(hashBytes);
            archiveStream.Position = origPos;
        }

        return await ReadPackageFromStreamInternalAsync(archiveStream, packageName, length, sha256, !leaveOpen, null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads and parses a .honmod package from a virtual file system.
    /// </summary>
    public static async Task<ModPackage> ReadPackageAsync(
        IVirtualFileSystem vfs,
        string virtualPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        if (vfs.DirectoryExists(virtualPath))
            return await ReadFromDirectoryAsync(vfs, virtualPath, ct).ConfigureAwait(false);

        if (!vfs.FileExists(virtualPath)) throw new FileNotFoundException($"Virtual mod file not found: {virtualPath}");

        byte[] fileBytes = await vfs.ReadAllBytesAsync(virtualPath, ct).ConfigureAwait(false);
        byte[] hashBytes = SHA256.HashData(fileBytes);
        string sha256 = Convert.ToHexStringLower(hashBytes);

        MemoryStream ms = new(fileBytes);
        try
        {
            return await ReadPackageFromStreamInternalAsync(ms, virtualPath, fileBytes.Length, sha256, true, vfs, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            ms.Dispose();
            throw;
        }
    }

    private static async Task<ModPackage> ReadPackageFromStreamInternalAsync(
        Stream stream,
        string filePath,
        long length,
        string sha256,
        bool ownsArchive,
        IVirtualFileSystem? vfs,
        CancellationToken ct)
    {
        ZipArchive? zip = null;

        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, !ownsArchive);

            foreach (ZipArchiveEntry zipEntry in zip.Entries)
            {
                ModPackage.EnsureSafeZipEntry(zipEntry);
            }

            ZipArchiveEntry? modXmlEntry = zip.Entries.FirstOrDefault(e =>
                !ModPackage.IsArchiveDirectoryPlaceholder(e) &&
                ModPackage.NormalizeZipPath(e.FullName).Equals("mod.xml", StringComparison.OrdinalIgnoreCase));

            if (modXmlEntry == null) throw new InvalidDataException($"Missing 'mod.xml' manifest inside '{filePath}'");

            string xmlContent;
            await using (Stream entryStream = modXmlEntry.Open())
            using (StreamReader reader = new(entryStream, Encoding.UTF8))
            {
                xmlContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            ModManifest manifest = ModManifestParser.Parse(xmlContent);

            byte[]? iconData = null;
            string iconEntryName = ModPackage.NormalizeZipPath(manifest.Icon ?? "icon.png");
            ZipArchiveEntry? iconEntry = zip.Entries.FirstOrDefault(e =>
                !ModPackage.IsArchiveDirectoryPlaceholder(e) &&
                (ModPackage.NormalizeZipPath(e.FullName).Equals(iconEntryName, StringComparison.OrdinalIgnoreCase) ||
                 ModPackage.NormalizeZipPath(e.FullName).Equals("icon.png", StringComparison.OrdinalIgnoreCase)));

            if (iconEntry != null)
            {
                await using Stream iconStream = iconEntry.Open();
                using MemoryStream ms = new(iconEntry.Length is > 0 and <= int.MaxValue ? (int)iconEntry.Length : 4096);
                await iconStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                iconData = ms.ToArray();
            }

            return new ModPackage(filePath, manifest, zip, ownsArchive, vfs)
            {
                FileSizeBytes = length,
                Sha256Hash = sha256,
                IconData = iconData,
                IsEnabled = false,
                IsDirectorySource = false
            };
        }
        catch
        {
            if (ownsArchive)
            {
                zip?.Dispose();
                if (zip == null) stream.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    ///     Reads and parses a mod from an unpacked directory source.
    /// </summary>
    public static async Task<ModPackage> ReadFromDirectoryAsync(string directoryPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        string modXmlPath = ModPackage.ResolveContainedDiskPath(directoryPath, "mod.xml");
        if (!File.Exists(modXmlPath))
            throw new FileNotFoundException($"Missing 'mod.xml' in directory: {directoryPath}");

        string xmlContent = await File.ReadAllTextAsync(modXmlPath, Encoding.UTF8, ct).ConfigureAwait(false);
        ModManifest manifest = ModManifestParser.Parse(xmlContent);

        byte[]? iconData = null;
        string iconPath = ModPackage.ResolveContainedDiskPath(directoryPath, manifest.Icon ?? "icon.png");
        if (File.Exists(iconPath)) iconData = await File.ReadAllBytesAsync(iconPath, ct).ConfigureAwait(false);

        DirectoryInfo dirInfo = new(directoryPath);
        FileInfo[] allFiles =
        [
            .. dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
        ];
        long totalSize = allFiles.Sum(f => f.Length);

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (FileInfo file in allFiles)
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget != null)
            {
                string alert =
                    $"[SECURITY-ALERT][MODDING-ZIPSLIP] Blocked symlink/reparse file '{file.FullName}' inside mod directory '{directoryPath}'.";
                Console.Error.WriteLine(alert);
                throw new SecurityException(alert);
            }

            byte[] fileBytes = await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
            hasher.AppendData(fileBytes);
        }

        string sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());

        return new ModPackage
        {
            FilePath = directoryPath,
            Manifest = manifest,
            FileSizeBytes = totalSize,
            Sha256Hash = sha256,
            IconData = iconData,
            IsEnabled = false,
            IsDirectorySource = true
        };
    }

    /// <summary>
    ///     Reads and parses a mod from an unpacked directory inside a virtual file system.
    /// </summary>
    public static async Task<ModPackage> ReadFromDirectoryAsync(
        IVirtualFileSystem vfs,
        string virtualDirectoryPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualDirectoryPath);

        string modXmlPath = string.IsNullOrEmpty(virtualDirectoryPath)
            ? "mod.xml"
            : $"{virtualDirectoryPath.TrimEnd('/')}/mod.xml";

        if (!vfs.FileExists(modXmlPath))
            throw new FileNotFoundException($"Missing 'mod.xml' in virtual directory: {virtualDirectoryPath}");

        string xmlContent = await vfs.ReadAllTextAsync(modXmlPath, ct).ConfigureAwait(false);
        ModManifest manifest = ModManifestParser.Parse(xmlContent);

        byte[]? iconData = null;
        string normIcon = ModPackage.NormalizeZipPath(manifest.Icon ?? "icon.png");
        string iconPath = string.IsNullOrEmpty(virtualDirectoryPath)
            ? normIcon
            : $"{virtualDirectoryPath.TrimEnd('/')}/{normIcon}";

        if (vfs.FileExists(iconPath)) iconData = await vfs.ReadAllBytesAsync(iconPath, ct).ConfigureAwait(false);

        List<string> allFiles =
        [
            .. vfs.EnumerateFiles(virtualDirectoryPath, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        ];

        long totalSize = 0;
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in allFiles)
        {
            byte[] fileBytes = await vfs.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            totalSize += fileBytes.Length;
            hasher.AppendData(fileBytes);
        }

        string sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());

        return new ModPackage(virtualDirectoryPath, manifest, vfs, true)
        {
            FileSizeBytes = totalSize,
            Sha256Hash = sha256,
            IconData = iconData,
            IsEnabled = false,
            IsDirectorySource = true
        };
    }
}
