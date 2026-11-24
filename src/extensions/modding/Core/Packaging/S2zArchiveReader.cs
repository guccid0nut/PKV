// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Kongor.Core.Vfs;
using Kongor.Modding.Core.Models;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Core.Packaging;

/// <summary>
///     High-performance reader for HoN .s2z game resource archives implementing <see cref="IArchiveReader" />.
/// </summary>
public sealed class S2zArchiveReader : IArchiveReader
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly bool _leaveOpen;
    private readonly Stream? _underlyingStream;
    private bool _disposed;

    public S2zArchiveReader(string archivePath, Stream stream, ZipArchive archive, bool leaveOpen = false)
    {
        ArchivePath = archivePath;
        _underlyingStream = stream;
        _archive = archive;
        _leaveOpen = leaveOpen;
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string norm = ModPackage.NormalizeZipPath(entry.FullName);
            _entries[norm] = entry;
        }
    }

    public string ArchivePath { get; }
    public IReadOnlyCollection<string> EntryNames => _entries.Keys;

    public bool HasEntry(string entryPath)
    {
        string norm = ModPackage.NormalizeZipPath(entryPath);
        return _entries.ContainsKey(norm);
    }

    public async Task<Stream> OpenEntryStreamAsync(string entryPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = ModPackage.NormalizeZipPath(entryPath);
        if (!_entries.TryGetValue(norm, out ZipArchiveEntry? entry))
            throw new FileNotFoundException($"Entry '{entryPath}' not found in archive '{ArchivePath}'");

        using Stream entryStream = entry.Open();
        MemoryStream ms = new((int)Math.Min(Math.Max(entry.Length, 64), int.MaxValue));
        try
        {
            await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            return ms;
        }
        catch
        {
            ms.Dispose();
            throw;
        }
    }

    public async Task<byte[]> ReadEntryBytesAsync(string entryPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = ModPackage.NormalizeZipPath(entryPath);
        if (!_entries.TryGetValue(norm, out ZipArchiveEntry? entry))
            throw new FileNotFoundException($"Entry '{entryPath}' not found in archive '{ArchivePath}'");

        await using Stream entryStream = entry.Open();
        using MemoryStream ms = new();
        await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task<string> ReadEntryTextAsync(string entryPath, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadEntryBytesAsync(entryPath, cancellationToken).ConfigureAwait(false);
        return GameResourceResolver.DecodeUtf8Text(bytes);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _archive.Dispose();
            if (!_leaveOpen && _underlyingStream != null) _underlyingStream.Dispose();
        }
    }

    public static S2zArchiveReader Open(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException($"S2Z archive not found: {archivePath}");

        FileStream? fs = null;
        ZipArchive? zip = null;
        try
        {
            fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
            return new S2zArchiveReader(archivePath, fs, zip, false);
        }
        catch
        {
            zip?.Dispose();
            fs?.Dispose();
            throw;
        }
    }

    public static S2zArchiveReader Open(Stream archiveStream, string? archiveName = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ZipArchive zip = new(archiveStream, ZipArchiveMode.Read, leaveOpen);
        return new S2zArchiveReader(archiveName ?? "stream.s2z", archiveStream, zip, leaveOpen);
    }

    public static async Task<S2zArchiveReader> OpenAsync(IVirtualFileSystem vfs, string virtualPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        Stream stream = await vfs.OpenReadAsync(virtualPath, ct).ConfigureAwait(false);
        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, false);
            return new S2zArchiveReader(virtualPath, stream, zip, false);
        }
        catch
        {
            zip?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
///     Multi-tier K2 resource resolver aligned with <see cref="ClientModMountSpec" /> precedence
///     (override -> KONGOR/* -> ../shared -> ClientPackageFiles -> game -> game/resources*.s2z -> base -> base/*.s2z).
/// </summary>
public sealed class GameResourceResolver : IDisposable
{
    private sealed record MountTier(
        string TierName,
        string? LooseDirectoryPath,
        List<S2zArchiveReader> DescendingArchives);

    private readonly List<S2zArchiveReader> _ownedArchives = [];
    private readonly List<MountTier> _tiers = [];
    private readonly IVirtualFileSystem? _vfs;
    private bool _disposed;

    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = [];

    public GameResourceResolver(IEnumerable<string> archivePaths, string? overrideDir = null,
        IVirtualFileSystem? vfs = null, Action<string>? warningLogger = null)
    {
        _vfs = vfs;

        if (!string.IsNullOrEmpty(overrideDir))
        {
            _tiers.Add(new MountTier("override", overrideDir, []));
        }

        List<S2zArchiveReader> openedAscending = [];
        foreach (string path in archivePaths)
        {
            S2zArchiveReader? reader = TryOpenArchive(path, vfs, _warnings, warningLogger);
            if (reader != null)
            {
                _ownedArchives.Add(reader);
                openedAscending.Add(reader);
            }
        }

        openedAscending.Reverse();
        if (openedAscending.Count > 0)
        {
            _tiers.Add(new MountTier("archives", null, openedAscending));
        }
    }

    private GameResourceResolver(
        IVirtualFileSystem? vfs,
        List<MountTier> tiers,
        List<S2zArchiveReader> ownedArchives,
        List<string> warnings)
    {
        _vfs = vfs;
        _tiers = tiers;
        _ownedArchives = ownedArchives;
        _warnings = warnings;
    }

    /// <summary>
    ///     Builds a K2-faithful multi-tier resolver from <see cref="ClientModMountSpec.CanonicalMountTiers" />
    ///     (or <see cref="ClientModMountSpec.PureCompatibilityMountTiers" /> when <paramref name="includeOverride" /> is false),
    ///     checking each tier's loose files before its descending <c>resources*.s2z</c> archives.
    /// </summary>
    public static GameResourceResolver CreateForClient(
        string gameClientDirectory,
        IVirtualFileSystem? vfs = null,
        bool includeOverride = true,
        string? excludedArchiveName = "resources999.s2z",
        Action<string>? warningLogger = null)
    {
        IReadOnlyList<string> mountSpec = includeOverride
            ? ClientModMountSpec.CanonicalMountTiers
            : ClientModMountSpec.PureCompatibilityMountTiers;

        List<MountTier> tiers = [];
        List<S2zArchiveReader> ownedArchives = [];
        List<string> warnings = [];

        for (int i = mountSpec.Count - 1; i >= 0; i--)
        {
            string tierRel = mountSpec[i];
            if (vfs != null)
            {
                string tierVfsDir = NormalizeVirtualPathSegments(gameClientDirectory, tierRel);
                if (!vfs.DirectoryExists(tierVfsDir)
                    && string.IsNullOrEmpty(gameClientDirectory)
                    && tierRel.StartsWith("../", StringComparison.Ordinal))
                {
                    string stripped = tierRel[3..];
                    if (vfs.DirectoryExists(stripped))
                    {
                        tierVfsDir = stripped;
                    }
                }

                List<S2zArchiveReader> tierArchives = [];
                if (vfs.DirectoryExists(tierVfsDir))
                {
                    IEnumerable<string> candidateS2z = vfs.EnumerateFiles(tierVfsDir, "*.s2z")
                        .Where(f => f.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase)
                                    && (string.IsNullOrEmpty(excludedArchiveName)
                                        || !Path.GetFileName(f).Equals(excludedArchiveName, StringComparison.OrdinalIgnoreCase)));

                    foreach (string s2zPath in OrderArchivesDescending(candidateS2z))
                    {
                        S2zArchiveReader? reader = TryOpenArchive(s2zPath, vfs, warnings, warningLogger);
                        if (reader != null)
                        {
                            ownedArchives.Add(reader);
                            tierArchives.Add(reader);
                        }
                    }
                }

                tiers.Add(new MountTier(tierRel, tierVfsDir, tierArchives));
            }
            else
            {
                string tierDiskDir = Path.GetFullPath(
                    Path.Combine(gameClientDirectory, tierRel.Replace('/', Path.DirectorySeparatorChar)));

                List<S2zArchiveReader> tierArchives = [];
                if (Directory.Exists(tierDiskDir))
                {
                    IEnumerable<string> candidateS2z = Directory.GetFiles(tierDiskDir, "*.s2z", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase)
                                    && (string.IsNullOrEmpty(excludedArchiveName)
                                        || !Path.GetFileName(f).Equals(excludedArchiveName, StringComparison.OrdinalIgnoreCase)));

                    foreach (string s2zPath in OrderArchivesDescending(candidateS2z))
                    {
                        S2zArchiveReader? reader = TryOpenArchive(s2zPath, null, warnings, warningLogger);
                        if (reader != null)
                        {
                            ownedArchives.Add(reader);
                            tierArchives.Add(reader);
                        }
                    }
                }

                tiers.Add(new MountTier(tierRel, tierDiskDir, tierArchives));
            }
        }

        return new GameResourceResolver(vfs, tiers, ownedArchives, warnings);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (S2zArchiveReader a in _ownedArchives) a.Dispose();
            _ownedArchives.Clear();
            _tiers.Clear();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Looks up and loads a game text file across configured K2 VFS mount tiers (stripping any 3-byte UTF-8 BOM).
    /// </summary>
    public async Task<string?> TryReadGameTextAsync(string relativePath, CancellationToken ct = default)
    {
        byte[]? bytes = await TryReadGameBytesAsync(relativePath, ct).ConfigureAwait(false);
        return bytes != null ? DecodeUtf8Text(bytes) : null;
    }

    public async Task<(string? Content, string? SourceTier)> TryResolveGameTextWithSourceAsync(
        string relativePath,
        CancellationToken ct = default)
    {
        (byte[]? bytes, string? source) = await TryResolveGameBytesWithSourceAsync(relativePath, ct).ConfigureAwait(false);
        return bytes != null ? (DecodeUtf8Text(bytes), source) : (null, null);
    }

    public async Task<byte[]?> TryReadGameBytesAsync(string relativePath, CancellationToken ct = default)
    {
        (byte[]? bytes, _) = await TryResolveGameBytesWithSourceAsync(relativePath, ct).ConfigureAwait(false);
        return bytes;
    }

    public async Task<(byte[]? Bytes, string? SourceTier)> TryResolveGameBytesWithSourceAsync(
        string relativePath,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string norm = ModPackage.NormalizeZipPath(relativePath);

        foreach (MountTier tier in _tiers)
        {
            if (!string.IsNullOrEmpty(tier.LooseDirectoryPath))
            {
                if (_vfs != null)
                {
                    string vfsLoose = NormalizeVirtualPathSegments(tier.LooseDirectoryPath, norm);
                    if (_vfs.FileExists(vfsLoose))
                    {
                        byte[] bytes = await _vfs.ReadAllBytesAsync(vfsLoose, ct).ConfigureAwait(false);
                        return (bytes, $"loose:{tier.TierName}");
                    }
                }
                else
                {
                    string diskLoose = Path.Combine(
                        tier.LooseDirectoryPath,
                        norm.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(diskLoose))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(diskLoose, ct).ConfigureAwait(false);
                        return (bytes, $"loose:{tier.TierName}");
                    }
                }
            }

            foreach (S2zArchiveReader archive in tier.DescendingArchives)
            {
                if (archive.HasEntry(norm))
                {
                    byte[] bytes = await archive.ReadEntryBytesAsync(norm, ct).ConfigureAwait(false);
                    return (bytes, $"s2z:{tier.TierName}/{Path.GetFileName(archive.ArchivePath)}");
                }
            }
        }

        return (null, null);
    }

    internal static string DecodeUtf8Text(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static string NormalizeVirtualPathSegments(params string?[] parts)
    {
        List<string> stack = [];
        foreach (string? part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            string[] tokens = part.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (token == ".") continue;
                if (token == "..")
                {
                    if (stack.Count > 0 && stack[^1] != "..")
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        stack.Add("..");
                    }
                }
                else
                {
                    stack.Add(token);
                }
            }
        }

        return string.Join('/', stack);
    }

    internal static IEnumerable<string> OrderArchivesDescending(IEnumerable<string> archivePaths)
    {
        return archivePaths
            .Select(path =>
            {
                string fileNameNoExt = Path.GetFileNameWithoutExtension(path);
                bool isNumberedResources = fileNameNoExt.StartsWith("resources", StringComparison.OrdinalIgnoreCase)
                                           && int.TryParse(
                                               fileNameNoExt.AsSpan("resources".Length),
                                               NumberStyles.None,
                                               CultureInfo.InvariantCulture,
                                               out int num);
                int parsedNum = isNumberedResources
                    ? int.Parse(fileNameNoExt.AsSpan("resources".Length), CultureInfo.InvariantCulture)
                    : -1;
                return (Path: path, IsNumbered: isNumberedResources, Number: parsedNum, Name: fileNameNoExt);
            })
            .OrderByDescending(x => x.IsNumbered)
            .ThenByDescending(x => x.Number)
            .ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path);
    }

    private static S2zArchiveReader? TryOpenArchive(
        string path,
        IVirtualFileSystem? vfs,
        List<string> warnings,
        Action<string>? warningLogger)
    {
        try
        {
            if (vfs != null)
            {
                if (!vfs.FileExists(path)) return null;
                Stream? s = null;
                ZipArchive? zip = null;
                try
                {
                    s = vfs.OpenRead(path);
                    zip = new ZipArchive(s, ZipArchiveMode.Read, false);
                    return new S2zArchiveReader(path, s, zip, false);
                }
                catch
                {
                    zip?.Dispose();
                    s?.Dispose();
                    throw;
                }
            }

            if (File.Exists(path))
            {
                return S2zArchiveReader.Open(path);
            }
        }
        catch (Exception ex)
        {
            string warnMsg = $"Failed to open archive '{path}': {ex.Message}";
            warnings.Add(warnMsg);
            warningLogger?.Invoke(warnMsg);
        }

        return null;
    }
}
