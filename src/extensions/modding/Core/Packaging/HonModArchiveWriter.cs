// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Parsing;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Core.Packaging;

/// <summary>
///     High-throughput streaming writer for creating .honmod archives supporting physical, stream, and VFS destinations.
/// </summary>
public static class HonModArchiveWriter
{
    public static async Task WritePackageAsync(
        string destinationFilePath,
        ModManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloadFiles,
        byte[]? iconData = null,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadFiles);

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await using FileStream fs = new(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await WritePackageAsync(fs, manifest, payloadFiles, iconData, compressionLevel, false, ct)
            .ConfigureAwait(false);
    }

    public static async Task WritePackageAsync(
        Stream destinationStream,
        ModManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloadFiles,
        byte[]? iconData = null,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool leaveOpen = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadFiles);

        using ZipArchive zip = new(destinationStream, ZipArchiveMode.Create, leaveOpen);

        // 1. Write mod.xml
        string modXmlContent = ModManifestParser.Serialize(manifest);
        ZipArchiveEntry modXmlEntry = zip.CreateEntry("mod.xml", compressionLevel);
        await using (Stream entryStream = modXmlEntry.Open())
        await using (StreamWriter writer = new(entryStream, Encoding.UTF8))
        {
            await writer.WriteAsync(modXmlContent).ConfigureAwait(false);
        }

        // 2. Write icon if present
        if (iconData != null && iconData.Length > 0)
        {
            string iconName = manifest.Icon ?? "icon.png";
            ZipArchiveEntry iconEntry = zip.CreateEntry(iconName, compressionLevel);
            await using Stream entryStream = iconEntry.Open();
            await entryStream.WriteAsync(iconData, ct).ConfigureAwait(false);
        }

        // 3. Write payload files
        foreach (KeyValuePair<string, byte[]> kvp in payloadFiles)
        {
            string normPath = ModPackage.NormalizeZipPath(kvp.Key);
            if (normPath.Equals("mod.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (iconData != null &&
                normPath.Equals(manifest.Icon ?? "icon.png", StringComparison.OrdinalIgnoreCase)) continue;

            ZipArchiveEntry entry = zip.CreateEntry(normPath, compressionLevel);
            await using Stream entryStream = entry.Open();
            await entryStream.WriteAsync(kvp.Value, ct).ConfigureAwait(false);
        }
    }

    public static async Task WritePackageAsync(
        IVirtualFileSystem vfs,
        string destinationVirtualPath,
        ModManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloadFiles,
        byte[]? iconData = null,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationVirtualPath);

        using MemoryStream ms = new();
        await WritePackageAsync(ms, manifest, payloadFiles, iconData, compressionLevel, true, ct).ConfigureAwait(false);
        await vfs.WriteAllBytesAsync(destinationVirtualPath, ms.ToArray(), ct).ConfigureAwait(false);
    }

    public static void WritePackage(
        string destinationFilePath,
        ModManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloadFiles,
        byte[]? iconData = null,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadFiles);

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        using FileStream fs = new(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        WritePackage(fs, manifest, payloadFiles, iconData, compressionLevel, false);
    }

    public static void WritePackage(
        Stream destinationStream,
        ModManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloadFiles,
        byte[]? iconData = null,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destinationStream);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadFiles);

        using ZipArchive zip = new(destinationStream, ZipArchiveMode.Create, leaveOpen);

        // 1. Write mod.xml
        string modXmlContent = ModManifestParser.Serialize(manifest);
        ZipArchiveEntry modXmlEntry = zip.CreateEntry("mod.xml", compressionLevel);
        using (Stream entryStream = modXmlEntry.Open())
        using (StreamWriter writer = new(entryStream, Encoding.UTF8))
        {
            writer.Write(modXmlContent);
        }

        // 2. Write icon if present
        if (iconData != null && iconData.Length > 0)
        {
            string iconName = manifest.Icon ?? "icon.png";
            ZipArchiveEntry iconEntry = zip.CreateEntry(iconName, compressionLevel);
            using Stream entryStream = iconEntry.Open();
            entryStream.Write(iconData, 0, iconData.Length);
        }

        // 3. Write payload files
        foreach (KeyValuePair<string, byte[]> kvp in payloadFiles)
        {
            string normPath = ModPackage.NormalizeZipPath(kvp.Key);
            if (normPath.Equals("mod.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (iconData != null &&
                normPath.Equals(manifest.Icon ?? "icon.png", StringComparison.OrdinalIgnoreCase)) continue;

            ZipArchiveEntry entry = zip.CreateEntry(normPath, compressionLevel);
            using Stream entryStream = entry.Open();
            entryStream.Write(kvp.Value, 0, kvp.Value.Length);
        }
    }
}
