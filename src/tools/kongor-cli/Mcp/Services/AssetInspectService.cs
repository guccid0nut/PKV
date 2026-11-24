// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Kongor.Core.Diagnostics;
using Kongor.Protocol.Diagnostics;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Dimensions of an inspected visual asset.
/// </summary>
public sealed record ImageDimensions(int Width, int Height);

/// <summary>
/// Solid color analysis result ($white, $black, $invis, or not solid).
/// </summary>
public sealed record SolidColorCheck(bool IsSolid, string? Type, double Confidence = 1.0);

/// <summary>
/// Result of an asset inspection operation.
/// </summary>
public sealed class AssetInspectionResult
{
    public bool Success { get; init; }
    public string Path { get; init; } = string.Empty;
    public string? ResolvedPath { get; init; }
    public string? VfsOrigin { get; init; }
    public string? ResolvedArchive { get; init; }
    public string Format { get; init; } = string.Empty;
    public ImageDimensions? Dimensions { get; init; }
    public int BitsPerPixel { get; init; }
    public int AlphaDepth { get; init; }
    public bool HasAlpha { get; init; }
    public string ImageType { get; init; } = string.Empty;
    public string? DescriptorByte { get; init; }
    public string Origin { get; init; } = "Unknown";
    public string? OrientationWarning { get; init; }
    public SolidColorCheck SolidColor { get; init; } = new(false, null);
    public int MipMapCount { get; init; } = 1;
    public string? FourCC { get; init; }
    public long FileSizeBytes { get; init; }
    public DdsValidationReport? DdsValidation { get; init; }
    public ByteDistributionProfile? EntropyProfile { get; init; }
    public bool HeaderFallbackApplied { get; init; }
    public string? Error { get; init; }
    public string Timestamp { get; init; } = TimeProvider.System.GetUtcNow().ToString("o");
}

/// <summary>
/// Interface for asset inspection services.
/// </summary>
public interface IAssetInspectService
{
    Task<AssetInspectionResult> InspectAssetAsync(string assetPath, string? repoRoot = null, CancellationToken cancellationToken = default);
    AssetInspectionResult InspectTga(Stream stream, string resolvedPath, string? vfsOrigin = null, string? archiveOrigin = null);
    AssetInspectionResult InspectDds(Stream stream, string resolvedPath, string? vfsOrigin = null, string? archiveOrigin = null);
    string? ResolveVfsPath(string vfsPath, string repoRoot, out string? vfsOrigin, out string? resolvedArchive);
}

/// <summary>
/// Pure managed C# asset inspector for K2 engine texture formats (.tga, .dds).
/// Features zero native binary dependencies, VFS path traversal, descriptor bit 5 origin inspection,
/// and pixel sampling for solid color detection ($white, $black, $invis).
/// </summary>
public sealed class AssetInspectService : IAssetInspectService
{
    private readonly Action<string>? _logAction;

    public AssetInspectService(Action<string>? logAction = null)
    {
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke($"[AssetInspectService] {message}");
    }

    public async Task<AssetInspectionResult> InspectAssetAsync(
        string assetPath,
        string? repoRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

        string root = repoRoot ?? FindProjectRoot();
        Log($"Resolving asset path: '{assetPath}' within root: '{root}'");

        string normalized = assetPath.Replace('\\', '/').Trim();
        string ext = Path.GetExtension(normalized).ToLowerInvariant();

        // 1. Resolve VFS path or locate file on disk
        string? resolvedFilePath = ResolveVfsPath(normalized, root, out string? vfsOrigin, out string? resolvedArchive);

        if (resolvedFilePath == null && resolvedArchive == null)
        {
            Log($"Asset '{assetPath}' could not be resolved across disk or VFS archives.");
            return new AssetInspectionResult
            {
                Success = false,
                Path = assetPath,
                Format = ext.TrimStart('.').ToUpperInvariant(),
                Error = $"Asset file '{assetPath}' not found on disk or within mounted .s2z archives (Root: '{root}', CWD: '{Directory.GetCurrentDirectory()}')."
            };
        }

        // 2. Open stream either from disk file or from .s2z archive entry
        try
        {
            if (!string.IsNullOrEmpty(resolvedArchive) && resolvedFilePath != null)
            {
                // Archive format: resolvedArchive is zip file path, resolvedFilePath is zip entry path
                Log($"Opening asset from .s2z archive: '{resolvedArchive}' -> '{resolvedFilePath}'");
                using FileStream zipFs = new(resolvedArchive, FileMode.Open, FileAccess.Read, FileShare.Read);
                using ZipArchive archive = new(zipFs, ZipArchiveMode.Read);
                ZipArchiveEntry? entry = archive.GetEntry(resolvedFilePath)
                    ?? archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), resolvedFilePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    return new AssetInspectionResult
                    {
                        Success = false,
                        Path = assetPath,
                        ResolvedArchive = resolvedArchive,
                        Error = $"Entry '{resolvedFilePath}' not found inside archive '{resolvedArchive}'."
                    };
                }

                using MemoryStream ms = new((int)Math.Min(Math.Max(entry.Length, 64), 100 * 1024 * 1024));
                await using (Stream entryStream = entry.Open())
                {
                    await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                }
                ms.Position = 0;

                string entryExt = Path.GetExtension(entry.FullName).ToLowerInvariant();
                return entryExt switch
                {
                    ".tga" => InspectTga(ms, entry.FullName, vfsOrigin, resolvedArchive),
                    ".dds" => InspectDds(ms, entry.FullName, vfsOrigin, resolvedArchive),
                    _ => InspectUnknown(ms, entry.FullName, vfsOrigin, resolvedArchive)
                };
            }
            else if (resolvedFilePath != null && File.Exists(resolvedFilePath))
            {
                Log($"Opening asset directly from disk: '{resolvedFilePath}'");
                await using FileStream fs = new(resolvedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string fileExt = Path.GetExtension(resolvedFilePath).ToLowerInvariant();

                return fileExt switch
                {
                    ".tga" => InspectTga(fs, resolvedFilePath, vfsOrigin, null),
                    ".dds" => InspectDds(fs, resolvedFilePath, vfsOrigin, null),
                    _ => InspectUnknown(fs, resolvedFilePath, vfsOrigin, null)
                };
            }
            else
            {
                return new AssetInspectionResult
                {
                    Success = false,
                    Path = assetPath,
                    Error = $"Unable to read asset '{assetPath}'."
                };
            }
        }
        catch (Exception ex)
        {
            Log($"Exception inspecting asset '{assetPath}': {ex.Message}");
            return new AssetInspectionResult
            {
                Success = false,
                Path = assetPath,
                ResolvedPath = resolvedFilePath,
                ResolvedArchive = resolvedArchive,
                Error = $"Error parsing asset: {ex.Message}"
            };
        }
    }

    public AssetInspectionResult InspectTga(Stream stream, string resolvedPath, string? vfsOrigin = null, string? archiveOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[18];
        int read = stream.Read(header, 0, 18);
        if (read < 18)
        {
            return new AssetInspectionResult
            {
                Success = false,
                Path = resolvedPath,
                ResolvedPath = resolvedPath,
                Format = "TGA",
                Error = $"File is too short to contain a valid 18-byte TGA header (read {read} bytes)."
            };
        }

        byte idLength = header[0];
        byte colorMapType = header[1];
        byte imageTypeCode = header[2];
        ushort colorMapOrigin = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(3, 2));
        ushort colorMapLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(5, 2));
        byte colorMapEntrySize = header[7];
        ushort xOrigin = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        ushort yOrigin = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12, 2));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
        byte pixelDepth = header[16];
        byte descriptor = header[17];

        int alphaDepth = descriptor & 0x0F;
        bool horizontalFlip = (descriptor & 0x10) != 0;
        bool originTopLeft = (descriptor & 0x20) != 0;

        string origin = originTopLeft ? "Top-Left" : "Bottom-Left";
        string? orientationWarning = originTopLeft
            ? $"WARNING: Descriptor byte 0x{descriptor:X2} has bit 5 set (Top-Left origin). In K2 engine, this texture will render inverted/upside-down unless flipped!"
            : null;

        string imageType = imageTypeCode switch
        {
            0 => "No Image Data",
            1 => "Uncompressed Color-Mapped",
            2 => pixelDepth == 32 ? "Uncompressed True-Color (RGBA)" : "Uncompressed True-Color (RGB)",
            3 => "Uncompressed Black & White (Grayscale)",
            9 => "RLE Color-Mapped",
            10 => pixelDepth == 32 ? "RLE True-Color (RGBA)" : "RLE True-Color (RGB)",
            11 => "RLE Black & White (Grayscale)",
            _ => $"Unknown TGA Type ({imageTypeCode})"
        };

        bool hasAlpha = alphaDepth > 0 || pixelDepth == 32;

        Log($"TGA Header Parsed: {width}x{height}, {pixelDepth}bpp, AlphaDepth: {alphaDepth}, Origin: {origin} (Descriptor: 0x{descriptor:X2})");

        // Skip ID field if present
        if (idLength > 0 && stream.CanSeek)
        {
            stream.Seek(18 + idLength, SeekOrigin.Begin);
        }
        else if (idLength > 0)
        {
            byte[] skip = new byte[idLength];
            stream.ReadExactly(skip, 0, idLength);
        }

        // Skip color map specification if present
        if (colorMapType == 1 && colorMapLength > 0)
        {
            int colorMapBytes = colorMapLength * ((colorMapEntrySize + 7) / 8);
            if (stream.CanSeek)
            {
                stream.Seek(colorMapBytes, SeekOrigin.Current);
            }
            else
            {
                byte[] skip = new byte[colorMapBytes];
                stream.ReadExactly(skip, 0, colorMapBytes);
            }
        }

        // Detect solid color ($white, $black, $invis)
        SolidColorCheck solidColor = DetectTgaSolidColor(stream, imageTypeCode, width, height, pixelDepth);

        long fileSize = stream.CanSeek ? stream.Length : 18;

        return new AssetInspectionResult
        {
            Success = true,
            Path = resolvedPath,
            ResolvedPath = resolvedPath,
            VfsOrigin = vfsOrigin,
            ResolvedArchive = archiveOrigin,
            Format = "TGA",
            Dimensions = new ImageDimensions(width, height),
            BitsPerPixel = pixelDepth,
            AlphaDepth = alphaDepth,
            HasAlpha = hasAlpha,
            ImageType = imageType,
            DescriptorByte = $"0x{descriptor:X2}",
            Origin = origin,
            OrientationWarning = orientationWarning,
            SolidColor = solidColor,
            MipMapCount = 1,
            FileSizeBytes = fileSize,
            Timestamp = TimeProvider.System.GetUtcNow().ToString("o")
        };
    }

    public AssetInspectionResult InspectDds(Stream stream, string resolvedPath, string? vfsOrigin = null, string? archiveOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[128];
        int read = stream.Read(header, 0, 128);
        if (read < 128)
        {
            return new AssetInspectionResult
            {
                Success = false,
                Path = resolvedPath,
                ResolvedPath = resolvedPath,
                Format = "DDS",
                Error = $"File is too short to contain a valid 128-byte DDS header (read {read} bytes)."
            };
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != 0x20534444) // 'DDS '
        {
            return new AssetInspectionResult
            {
                Success = false,
                Path = resolvedPath,
                ResolvedPath = resolvedPath,
                Format = "DDS",
                Error = $"Invalid DDS magic header: 0x{magic:X8}. Expected 'DDS ' (0x20534444)."
            };
        }

        uint dwSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint dwFlags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint dwHeight = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        uint dwWidth = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        uint dwPitchOrLinearSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        uint dwDepth = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4));
        uint dwMipMapCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4));

        // DDS_PIXELFORMAT (32 bytes at offset 76)
        uint pfSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76, 4));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
        byte[] fourCcBytes = header[84..88];
        string fourCC = Encoding.ASCII.GetString(fourCcBytes).Trim('\0', ' ');
        uint rgbBitCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88, 4));
        uint rBitMask = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(92, 4));
        uint gBitMask = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(96, 4));
        uint bBitMask = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(100, 4));
        uint aBitMask = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(104, 4));

        bool isFourCC = (pfFlags & 0x4) != 0; // DDPF_FOURCC
        bool isRgb = (pfFlags & 0x40) != 0;    // DDPF_RGB
        bool hasAlphaFlag = (pfFlags & 0x1) != 0 || aBitMask != 0; // DDPF_ALPHAPIXELS

        string formatDescription;
        int bitsPerPixel;
        bool hasAlpha;

        if (isFourCC)
        {
            formatDescription = fourCC switch
            {
                "DXT1" => "DXT1 / BC1 Compressed (RGB / 1-bit Alpha)",
                "DXT2" => "DXT2 / BC2 Compressed (Premultiplied Alpha)",
                "DXT3" => "DXT3 / BC2 Compressed (Explicit 4-bit Alpha)",
                "DXT4" => "DXT4 / BC3 Compressed (Premultiplied Alpha)",
                "DXT5" => "DXT5 / BC3 Compressed (Interpolated 8-bit Alpha)",
                "DX10" => "DX10 Extended Header",
                "ATI1" or "BC4U" => "ATI1 / BC4 Compressed (Single Channel)",
                "ATI2" or "BC5U" => "ATI2 / BC5 Compressed (Two Channel Normal Map)",
                _ => $"FourCC: {fourCC}"
            };

            bitsPerPixel = fourCC switch
            {
                "DXT1" or "ATI1" or "BC4U" => 4,
                "DXT3" or "DXT5" or "ATI2" or "BC5U" => 8,
                _ => 8
            };

            hasAlpha = fourCC is "DXT1" or "DXT2" or "DXT3" or "DXT4" or "DXT5";
        }
        else if (isRgb)
        {
            bitsPerPixel = (int)rgbBitCount;
            hasAlpha = hasAlphaFlag;
            formatDescription = bitsPerPixel == 32
                ? (hasAlpha ? "Uncompressed RGBA (32-bit)" : "Uncompressed RGBX (32-bit)")
                : $"Uncompressed RGB ({bitsPerPixel}-bit)";
        }
        else
        {
            formatDescription = "DDS Custom Pixel Format";
            bitsPerPixel = (int)rgbBitCount;
            hasAlpha = hasAlphaFlag;
        }

        int mipMaps = dwMipMapCount == 0 ? 1 : (int)dwMipMapCount;

        Log($"DDS Header Parsed: {dwWidth}x{dwHeight}, Format: {formatDescription}, FourCC: '{fourCC}', Mips: {mipMaps}");

        // Solid color detection for DDS
        SolidColorCheck solidColor = DetectDdsSolidColor(stream, fourCC, isFourCC, bitsPerPixel, (int)dwWidth, (int)dwHeight);

        long fileSize = stream.CanSeek ? stream.Length : 128;
        DdsValidationReport? ddsValidation = null;
        if (stream.CanSeek && fileSize >= 128 && fileSize <= 64 * 1024 * 1024)
        {
            long savedPos = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            byte[] fullDdsBytes = new byte[(int)fileSize];
            int totalRead = 0;
            while (totalRead < fullDdsBytes.Length)
            {
                int n = stream.Read(fullDdsBytes, totalRead, fullDdsBytes.Length - totalRead);
                if (n <= 0) break;
                totalRead += n;
            }
            stream.Seek(savedPos, SeekOrigin.Begin);
            ddsValidation = DdsMipmapAndAlphaValidator.ValidateDdsBuffer(fullDdsBytes.AsSpan(0, totalRead));
        }

        return new AssetInspectionResult
        {
            Success = true,
            Path = resolvedPath,
            ResolvedPath = resolvedPath,
            VfsOrigin = vfsOrigin,
            ResolvedArchive = archiveOrigin,
            Format = "DDS",
            Dimensions = new ImageDimensions((int)dwWidth, (int)dwHeight),
            BitsPerPixel = bitsPerPixel,
            AlphaDepth = hasAlpha ? 8 : 0,
            HasAlpha = hasAlpha,
            ImageType = formatDescription,
            DescriptorByte = null,
            Origin = "Top-Left (DirectX Standard)",
            OrientationWarning = ddsValidation?.DiagnosticWarning,
            SolidColor = solidColor,
            MipMapCount = mipMaps,
            FourCC = isFourCC ? fourCC : null,
            FileSizeBytes = fileSize,
            DdsValidation = ddsValidation,
            Timestamp = TimeProvider.System.GetUtcNow().ToString("o")
        };
    }

    private static AssetInspectionResult InspectUnknown(Stream stream, string resolvedPath, string? vfsOrigin, string? archiveOrigin)
    {
        string ext = Path.GetExtension(resolvedPath).TrimStart('.').ToUpperInvariant();
        long size = stream.CanSeek ? stream.Length : 0;
        ByteDistributionProfile? entropyProfile = null;
        bool headerFallback = false;

        if (stream.CanSeek && size > 0)
        {
            long savedPos = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            int maxRead = (int)Math.Min(size, 4 * 1024 * 1024);
            byte[] sampleBuf = new byte[maxRead];
            int totalRead = 0;
            while (totalRead < maxRead)
            {
                int n = stream.Read(sampleBuf, totalRead, maxRead - totalRead);
                if (n <= 0)
                {
                    break;
                }
                totalRead += n;
            }
            stream.Seek(savedPos, SeekOrigin.Begin);

            ReadOnlySpan<byte> analysisSpan = BoundedDecompressionPaginator.SliceToHeaderFallbackIfOversized(
                sampleBuf.AsSpan(0, totalRead),
                fullAnalysisMaxBytes: 1024 * 1024,
                out headerFallback);
            entropyProfile = BinaryEntropyAndDigramAnalyzer.AnalyzeEntropyAndDistribution(analysisSpan);
        }

        return new AssetInspectionResult
        {
            Success = true,
            Path = resolvedPath,
            ResolvedPath = resolvedPath,
            VfsOrigin = vfsOrigin,
            ResolvedArchive = archiveOrigin,
            Format = ext,
            ImageType = $"Generic Asset ({ext})",
            FileSizeBytes = size,
            EntropyProfile = entropyProfile,
            HeaderFallbackApplied = headerFallback,
            Timestamp = TimeProvider.System.GetUtcNow().ToString("o")
        };
    }

    /// <summary>
    /// Phase 13 (#165): Formats an XML/Lua syntax diagnostic with 120-column word-boundary windowing
    /// and a <c>^~~~~</c> caret underline for ultra-long or minified K2 <c>.interface</c> / <c>.entity</c> lines.
    /// </summary>
    public static FormattedCaretDiagnostic FormatSyntaxDiagnosticCaret(
        string sourceText,
        int lineOneBased,
        int columnOneBased,
        int tokenLength,
        string message,
        int maxLineLength = CaretDiagnosticWindowFormatter.DefaultMaxLineLength)
    {
        return CaretDiagnosticWindowFormatter.FormatDiagnostic(
            sourceText,
            lineOneBased,
            columnOneBased,
            tokenLength,
            message,
            maxLineLength);
    }

    /// <summary>
    /// Phase 13 (#160): Parses a custom <c>.tbl</c> stringtable and verifies in O(N log N) time whether
    /// any byte sequence is an ambiguous prefix of a longer sequence.
    /// </summary>
    public static CustomTableEncodingAndPrefixAuditor AuditCustomStringTable(
        string tableContent,
        out PrefixAmbiguityConflict? firstConflict)
    {
        CustomTableEncodingAndPrefixAuditor auditor = CustomTableEncodingAndPrefixAuditor.ParseTableContent(tableContent);
        auditor.HasAmbiguousPrefixSequences(out firstConflict);
        return auditor;
    }

    /// <summary>
    /// Resolves a VFS path or relative/absolute path across K2 VFS mount order and .s2z archives.
    /// </summary>
    public string? ResolveVfsPath(string vfsPath, string repoRoot, out string? vfsOrigin, out string? resolvedArchive)
    {
        vfsOrigin = null;
        resolvedArchive = null;

        string normalized = vfsPath.Replace('\\', '/').TrimStart('/');

        // 1. Direct path check (if absolute or relative to CWD)
        if (File.Exists(vfsPath))
        {
            vfsOrigin = "Direct File";
            return Path.GetFullPath(vfsPath);
        }

        string directInRoot = Path.Combine(repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(directInRoot))
        {
            vfsOrigin = "Repository Relative";
            return directInRoot;
        }

        // 2. Traversal of VFS search roots in HoN K2 priority order
        string[] searchBases =
        [
            "games/client/override",
            "games/client/game",
            "games/client/base",
            "games/client",
            "games/shared",
            "games/shared/00000000",
            "games/client/editor",
            "games/client/editor/00000000"
        ];

        foreach (string basePath in searchBases)
        {
            string candidate = Path.Combine(repoRoot, basePath.Replace('/', Path.DirectorySeparatorChar), normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                vfsOrigin = basePath;
                Log($"Resolved VFS '{vfsPath}' directly to '{candidate}' (Origin: {basePath})");
                return candidate;
            }
        }

        // 3. Search within .s2z archives
        string[] archiveDirectories =
        [
            Path.Combine(repoRoot, "games", "client", "game"),
            Path.Combine(repoRoot, "games", "client"),
            Path.Combine(repoRoot, "games", "client", "override"),
            Path.Combine(repoRoot, "games", "client", "Avatar Mods"),
            Path.Combine(repoRoot, "games", "shared")
        ];

        foreach (string dir in archiveDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            string[] s2zFiles = Directory.GetFiles(dir, "*.s2z", SearchOption.AllDirectories);
            // Sort resources0.s2z first
            Array.Sort(s2zFiles, (a, b) =>
            {
                bool aRes0 = Path.GetFileName(a).Equals("resources0.s2z", StringComparison.OrdinalIgnoreCase);
                bool bRes0 = Path.GetFileName(b).Equals("resources0.s2z", StringComparison.OrdinalIgnoreCase);
                if (aRes0 && !bRes0) return -1;
                if (!aRes0 && bRes0) return 1;
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            foreach (string s2z in s2zFiles)
            {
                try
                {
                    using FileStream zipStream = new(s2z, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using ZipArchive zip = new(zipStream, ZipArchiveMode.Read);

                    ZipArchiveEntry? match = zip.GetEntry(normalized)
                        ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        resolvedArchive = s2z;
                        vfsOrigin = $"archive:{Path.GetFileName(s2z)}";
                        Log($"Resolved VFS '{vfsPath}' inside archive '{s2z}' entry: '{match.FullName}'");
                        return match.FullName;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed reading archive '{s2z}': {ex.Message}");
                }
            }
        }

        return null;
    }

    private static SolidColorCheck DetectTgaSolidColor(Stream stream, byte imageType, int width, int height, int pixelDepth)
    {
        int bytesPerPixel = pixelDepth / 8;
        if (bytesPerPixel < 1 || width <= 0 || height <= 0)
        {
            return new SolidColorCheck(false, null);
        }

        int totalPixels = width * height;
        int sampleLimit = Math.Min(totalPixels, 1024);

        try
        {
            if (imageType is 2 or 3) // Uncompressed True-Color or Grayscale
            {
                long startPos = stream.CanSeek ? stream.Position : 0;
                long totalDataBytes = (long)totalPixels * bytesPerPixel;
                long availableBytes = stream.CanSeek ? (stream.Length - startPos) : totalDataBytes;
                long bytesToScan = Math.Min(totalDataBytes, availableBytes);
                int pixelCount = (int)(bytesToScan / bytesPerPixel);
                if (pixelCount == 0) return new SolidColorCheck(false, null);

                int sampleCount = Math.Min(pixelCount, 1024);
                long stride = Math.Max(1, pixelCount / sampleCount);

                bool allWhite = true;
                bool allBlack = true;
                bool allInvis = true;
                byte[] pixel = new byte[bytesPerPixel];

                for (int i = 0; i < sampleCount; i++)
                {
                    if (stream.CanSeek)
                    {
                        stream.Position = startPos + (i * stride * bytesPerPixel);
                    }
                    int read = stream.Read(pixel, 0, bytesPerPixel);
                    if (read < bytesPerPixel) break;

                    byte b = pixel[0];
                    byte g = bytesPerPixel >= 2 ? pixel[1] : b;
                    byte r = bytesPerPixel >= 3 ? pixel[2] : b;
                    byte a = bytesPerPixel >= 4 ? pixel[3] : (byte)255;

                    if (a > 5) allInvis = false;
                    if (r < 250 || g < 250 || b < 250 || a < 250) allWhite = false;
                    if (r > 5 || g > 5 || b > 5 || a < 250) allBlack = false;

                    if (!allWhite && !allBlack && !allInvis) break;
                }

                if (allInvis) return new SolidColorCheck(true, "$invis");
                if (allWhite) return new SolidColorCheck(true, "$white");
                if (allBlack) return new SolidColorCheck(true, "$black");
            }
            else if (imageType is 10 or 11) // RLE True-Color or Grayscale
            {
                // Sample decoded RLE stream
                bool allWhite = true;
                bool allBlack = true;
                bool allInvis = true;
                int sampled = 0;

                byte[] pixelBuf = new byte[bytesPerPixel];

                while (sampled < sampleLimit)
                {
                    int head = stream.ReadByte();
                    if (head == -1) break;

                    bool isRle = (head & 0x80) != 0;
                    int count = (head & 0x7F) + 1;

                    if (isRle)
                    {
                        int rRead = stream.Read(pixelBuf, 0, bytesPerPixel);
                        if (rRead < bytesPerPixel) break;

                        byte b = pixelBuf[0];
                        byte g = bytesPerPixel >= 2 ? pixelBuf[1] : b;
                        byte r = bytesPerPixel >= 3 ? pixelBuf[2] : b;
                        byte a = bytesPerPixel >= 4 ? pixelBuf[3] : (byte)255;

                        if (a > 5) allInvis = false;
                        if (r < 250 || g < 250 || b < 250 || a < 250) allWhite = false;
                        if (r > 5 || g > 5 || b > 5 || a < 250) allBlack = false;

                        sampled += count;
                    }
                    else
                    {
                        for (int k = 0; k < count && sampled < sampleLimit; k++)
                        {
                            int rRead = stream.Read(pixelBuf, 0, bytesPerPixel);
                            if (rRead < bytesPerPixel) break;

                            byte b = pixelBuf[0];
                            byte g = bytesPerPixel >= 2 ? pixelBuf[1] : b;
                            byte r = bytesPerPixel >= 3 ? pixelBuf[2] : b;
                            byte a = bytesPerPixel >= 4 ? pixelBuf[3] : (byte)255;

                            if (a > 5) allInvis = false;
                            if (r < 250 || g < 250 || b < 250 || a < 250) allWhite = false;
                            if (r > 5 || g > 5 || b > 5 || a < 250) allBlack = false;

                            sampled++;
                        }
                    }

                    if (!allWhite && !allBlack && !allInvis) break;
                }

                if (sampled > 0)
                {
                    if (allInvis) return new SolidColorCheck(true, "$invis");
                    if (allWhite) return new SolidColorCheck(true, "$white");
                    if (allBlack) return new SolidColorCheck(true, "$black");
                }
            }
        }
        catch
        {
            // Ignore sampling read errors on truncated files
        }

        return new SolidColorCheck(false, null);
    }

    private static SolidColorCheck DetectDdsSolidColor(Stream stream, string fourCC, bool isFourCC, int bpp, int width, int height)
    {
        try
        {
            if (isFourCC && fourCC == "DXT1")
            {
                // DXT1: 8 bytes per 4x4 block: 2 bytes color0 (565), 2 bytes color1 (565), 4 bytes lookup table
                byte[] blockBuf = new byte[8 * 64]; // Sample up to 64 blocks
                int read = stream.Read(blockBuf, 0, blockBuf.Length);
                int blockCount = read / 8;
                if (blockCount == 0) return new SolidColorCheck(false, null);

                bool allWhite = true;
                bool allBlack = true;
                bool allInvis = true;

                for (int b = 0; b < blockCount; b++)
                {
                    int offset = b * 8;
                    ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(blockBuf.AsSpan(offset, 2));
                    ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(blockBuf.AsSpan(offset + 2, 2));
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(blockBuf.AsSpan(offset + 4, 4));

                    if (c0 != 0xFFFF || c1 != 0xFFFF) allWhite = false;
                    if (c0 != 0x0000 || c1 != 0x0000) allBlack = false;

                    // In DXT1, transparent pixel is index 3 (binary 11) when c0 <= c1
                    if (!(c0 <= c1 && bits == 0xFFFFFFFF)) allInvis = false;

                    if (!allWhite && !allBlack && !allInvis) break;
                }

                if (allInvis) return new SolidColorCheck(true, "$invis");
                if (allWhite) return new SolidColorCheck(true, "$white");
                if (allBlack) return new SolidColorCheck(true, "$black");
            }
            else if (isFourCC && (fourCC == "DXT5" || fourCC == "DXT3"))
            {
                // DXT3/5: 16 bytes per block: 8 bytes alpha, 8 bytes color
                byte[] blockBuf = new byte[16 * 32];
                int read = stream.Read(blockBuf, 0, blockBuf.Length);
                int blockCount = read / 16;
                if (blockCount == 0) return new SolidColorCheck(false, null);

                bool allWhite = true;
                bool allBlack = true;
                bool allInvis = true;

                for (int b = 0; b < blockCount; b++)
                {
                    int offset = b * 16;
                    if (fourCC == "DXT5")
                    {
                        byte a0 = blockBuf[offset];
                        byte a1 = blockBuf[offset + 1];
                        if (a0 > 5 || a1 > 5) allInvis = false;
                        if (a0 < 250 || a1 < 250) { allWhite = false; allBlack = false; }
                    }
                    else // DXT3
                    {
                        ulong alphaBits = BinaryPrimitives.ReadUInt64LittleEndian(blockBuf.AsSpan(offset, 8));
                        if (alphaBits != 0) allInvis = false;
                        if (alphaBits != 0xFFFFFFFFFFFFFFFF) { allWhite = false; allBlack = false; }
                    }

                    ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(blockBuf.AsSpan(offset + 8, 2));
                    ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(blockBuf.AsSpan(offset + 10, 2));

                    if (c0 != 0xFFFF || c1 != 0xFFFF) allWhite = false;
                    if (c0 != 0x0000 || c1 != 0x0000) allBlack = false;

                    if (!allWhite && !allBlack && !allInvis) break;
                }

                if (allInvis) return new SolidColorCheck(true, "$invis");
                if (allWhite) return new SolidColorCheck(true, "$white");
                if (allBlack) return new SolidColorCheck(true, "$black");
            }
            else if (!isFourCC && bpp >= 24)
            {
                int bytesPerPixel = bpp / 8;
                byte[] pixelBuf = new byte[bytesPerPixel * 256];
                int read = stream.Read(pixelBuf, 0, pixelBuf.Length);
                int pixelCount = read / bytesPerPixel;
                if (pixelCount == 0) return new SolidColorCheck(false, null);

                bool allWhite = true;
                bool allBlack = true;
                bool allInvis = true;

                for (int i = 0; i < pixelCount; i++)
                {
                    int offset = i * bytesPerPixel;
                    byte r = pixelBuf[offset];
                    byte g = pixelBuf[offset + 1];
                    byte b = pixelBuf[offset + 2];
                    byte a = bytesPerPixel >= 4 ? pixelBuf[offset + 3] : (byte)255;

                    if (a > 5) allInvis = false;
                    if (r < 250 || g < 250 || b < 250 || a < 250) allWhite = false;
                    if (r > 5 || g > 5 || b > 5 || a < 250) allBlack = false;

                    if (!allWhite && !allBlack && !allInvis) break;
                }

                if (allInvis) return new SolidColorCheck(true, "$invis");
                if (allWhite) return new SolidColorCheck(true, "$white");
                if (allBlack) return new SolidColorCheck(true, "$black");
            }
        }
        catch
        {
            // Suppress sampling exceptions
        }

        return new SolidColorCheck(false, null);
    }

    private static string FindProjectRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")))
            {
                return current;
            }
            string? parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "ProjectKongor.slnx")))
            {
                return baseDir;
            }
            string? parent = Path.GetDirectoryName(baseDir);
            if (parent == null || parent == baseDir) break;
            baseDir = parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
