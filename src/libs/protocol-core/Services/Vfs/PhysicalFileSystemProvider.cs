// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using NEXUS.Contracts.Vfs;

namespace Kongor.Core.Services.Vfs;

/// <summary>
///     Real physical filesystem provider implementing <see cref="IVirtualFileSystem" /> with root sandboxing.
/// </summary>
public sealed class PhysicalFileSystemProvider : IVirtualFileSystem
{
    private readonly Lock _mountLock = new();

    private readonly ConcurrentDictionary<string, List<MountedVfsEntry>>
        _mounts = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public PhysicalFileSystemProvider(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(RootDirectory)) Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public bool FileExists(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        // 1. Check mounts
        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return true;

        // 2. Check physical disk
        string diskPath = ResolvePhysicalPath(norm);
        return File.Exists(diskPath);
    }

    public bool DirectoryExists(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        // Root always exists if this VFS is initialized
        if (string.IsNullOrEmpty(norm)) return true;

        // 1. Check mounts
        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.DirectoryExists(subPath))
                return true;

        foreach (string mountKey in _mounts.Keys)
            if (mountKey.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                mountKey.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        // 2. Check physical disk
        string diskPath = ResolvePhysicalPath(norm);
        return Directory.Exists(diskPath);
    }

    public Stream OpenRead(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.OpenRead(subPath);

        string diskPath = ResolvePhysicalPath(norm);
        return new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
    }

    public async Task<Stream> OpenReadAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.OpenReadAsync(subPath, cancellationToken).ConfigureAwait(false);

        string diskPath = ResolvePhysicalPath(norm);
        if (!File.Exists(diskPath))
            throw new FileNotFoundException($"File not found in physical VFS: {virtualPath}", diskPath);

        return new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
    }

    public byte[] ReadAllBytes(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.ReadAllBytes(subPath);

        string diskPath = ResolvePhysicalPath(norm);
        return File.ReadAllBytes(diskPath);
    }

    public async Task<byte[]> ReadAllBytesAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.ReadAllBytesAsync(subPath, cancellationToken).ConfigureAwait(false);

        string diskPath = ResolvePhysicalPath(norm);
        return await File.ReadAllBytesAsync(diskPath, cancellationToken).ConfigureAwait(false);
    }

    public string ReadAllText(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.ReadAllText(subPath);

        string diskPath = ResolvePhysicalPath(norm);
        return File.ReadAllText(diskPath, Encoding.UTF8);
    }

    public async Task<string> ReadAllTextAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.ReadAllTextAsync(subPath, cancellationToken).ConfigureAwait(false);

        string diskPath = ResolvePhysicalPath(norm);
        return await File.ReadAllTextAsync(diskPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public void WriteAllBytes(string virtualPath, byte[] bytes)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(bytes);
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            mountedVfs!.WriteAllBytes(subPath, bytes);
            return;
        }

        string diskPath = ResolvePhysicalPath(norm);
        string? dir = Path.GetDirectoryName(diskPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(diskPath, bytes);
    }

    public async Task WriteAllBytesAsync(string virtualPath, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(bytes);
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            await mountedVfs!.WriteAllBytesAsync(subPath, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        string diskPath = ResolvePhysicalPath(norm);
        string? dir = Path.GetDirectoryName(diskPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(diskPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public void WriteAllText(string virtualPath, string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            mountedVfs!.WriteAllText(subPath, text);
            return;
        }

        string diskPath = ResolvePhysicalPath(norm);
        string? dir = Path.GetDirectoryName(diskPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(diskPath, text, Encoding.UTF8);
    }

    public async Task WriteAllTextAsync(string virtualPath, string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            await mountedVfs!.WriteAllTextAsync(subPath, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        string diskPath = ResolvePhysicalPath(norm);
        string? dir = Path.GetDirectoryName(diskPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(diskPath, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public void DeleteFile(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            mountedVfs!.DeleteFile(subPath);
            return;
        }

        string diskPath = ResolvePhysicalPath(norm);
        if (File.Exists(diskPath)) File.Delete(diskPath);
    }

    public void CreateDirectory(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            mountedVfs!.CreateDirectory(subPath);
            return;
        }

        string diskPath = ResolvePhysicalPath(norm);
        if (!Directory.Exists(diskPath)) Directory.CreateDirectory(diskPath);
    }

    public IEnumerable<string> EnumerateFiles(
        string virtualPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);
        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

        // 1. Enumerate physical files
        string diskPath = ResolvePhysicalPath(norm);
        if (Directory.Exists(diskPath))
            foreach (string file in Directory.EnumerateFiles(diskPath, "*", searchOption))
            {
                string rel = Path.GetRelativePath(RootDirectory, file).Replace('\\', '/').TrimStart('/');
                string fileName = Path.GetFileName(file);
                if (MatchesPattern(fileName, searchPattern)) results.Add(rel);
            }

        // 2. Enumerate mounted systems
        foreach (KeyValuePair<string, List<MountedVfsEntry>> kvp in _mounts)
        {
            string mountPoint = kvp.Key;
            foreach (MountedVfsEntry entry in kvp.Value)
                if (norm.Equals(mountPoint, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(norm) ||
                    mountPoint.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string subLookup = norm.Length > mountPoint.Length
                        ? norm[(mountPoint.Length + 1)..]
                        : string.Empty;

                    foreach (string subFile in entry.SubVfs.EnumerateFiles(subLookup, searchPattern, searchOption))
                    {
                        string combined = string.IsNullOrEmpty(mountPoint)
                            ? subFile
                            : $"{mountPoint}/{subFile}".TrimStart('/');
                        results.Add(combined);
                    }
                }
        }

        return results;
    }

    public IEnumerable<string> EnumerateDirectories(
        string virtualPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);
        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

        // 1. Enumerate physical directories
        string diskPath = ResolvePhysicalPath(norm);
        if (Directory.Exists(diskPath))
            foreach (string dir in Directory.EnumerateDirectories(diskPath, "*", searchOption))
            {
                string rel = Path.GetRelativePath(RootDirectory, dir).Replace('\\', '/').TrimStart('/');
                string dirName = Path.GetFileName(dir);
                if (MatchesPattern(dirName, searchPattern)) results.Add(rel);
            }

        // 2. Enumerate mounted systems
        foreach (KeyValuePair<string, List<MountedVfsEntry>> kvp in _mounts)
        {
            string mountPoint = kvp.Key;
            if (string.IsNullOrEmpty(norm))
            {
                string topMount = mountPoint.Contains('/') ? mountPoint[..mountPoint.IndexOf('/')] : mountPoint;
                if (!string.IsNullOrEmpty(topMount) && MatchesPattern(topMount, searchPattern)) results.Add(topMount);
            }
            else if (mountPoint.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase))
            {
                string sub = mountPoint[(norm.Length + 1)..];
                string nextDir = sub.Contains('/') ? sub[..sub.IndexOf('/')] : sub;
                string fullSub = $"{norm}/{nextDir}".TrimStart('/');
                if (MatchesPattern(nextDir, searchPattern)) results.Add(fullSub);
            }
        }

        return results;
    }

    public void Mount(string mountPoint, IVirtualFileSystem subVfs, int priority = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(subVfs);
        string normMount = NormalizeVirtualPath(mountPoint);

        lock (_mountLock)
        {
            List<MountedVfsEntry> entries = _mounts.GetOrAdd(normMount, _ => []);
            entries.Add(new MountedVfsEntry(subVfs, priority));
            entries.Sort((a, b) => b.Priority.CompareTo(a.Priority)); // Highest priority first
        }
    }

    public void Unmount(string mountPoint)
    {
        ThrowIfDisposed();
        string normMount = NormalizeVirtualPath(mountPoint);

        lock (_mountLock)
        {
            _mounts.TryRemove(normMount, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_mountLock)
        {
            foreach (List<MountedVfsEntry> entries in _mounts.Values)
            foreach (MountedVfsEntry entry in entries)
                entry.SubVfs.Dispose();

            _mounts.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private string ResolvePhysicalPath(string normalizedVirtualPath)
    {
        string safeRoot = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
        string combined = Path.GetFullPath(Path.Combine(RootDirectory,
            normalizedVirtualPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Access denied: path '{normalizedVirtualPath}' escapes VFS root '{RootDirectory}'.");
        return combined;
    }

    private bool TryGetMountedTarget(string normalizedVirtualPath, [NotNullWhen(true)] out IVirtualFileSystem? subVfs,
        out string subPath)
    {
        // Check exact match or prefixes from longest to shortest
        foreach (KeyValuePair<string, List<MountedVfsEntry>> kvp in _mounts.OrderByDescending(k => k.Key.Length))
        {
            string mountPoint = kvp.Key;
            if (string.IsNullOrEmpty(mountPoint))
            {
                foreach (MountedVfsEntry entry in kvp.Value)
                    if (entry.SubVfs.FileExists(normalizedVirtualPath) ||
                        entry.SubVfs.DirectoryExists(normalizedVirtualPath))
                    {
                        subVfs = entry.SubVfs;
                        subPath = normalizedVirtualPath;
                        return true;
                    }
            }
            else if (normalizedVirtualPath.Equals(mountPoint, StringComparison.OrdinalIgnoreCase))
            {
                if (kvp.Value.Count > 0)
                {
                    subVfs = kvp.Value[0].SubVfs;
                    subPath = string.Empty;
                    return true;
                }
            }
            else if (normalizedVirtualPath.StartsWith(mountPoint + "/", StringComparison.OrdinalIgnoreCase))
            {
                string remainder = normalizedVirtualPath[(mountPoint.Length + 1)..];
                foreach (MountedVfsEntry entry in kvp.Value)
                    if (entry.SubVfs.FileExists(remainder) || entry.SubVfs.DirectoryExists(remainder))
                    {
                        subVfs = entry.SubVfs;
                        subPath = remainder;
                        return true;
                    }

                // If none explicitly had it, return the highest priority mount for write/lookup
                if (kvp.Value.Count > 0)
                {
                    subVfs = kvp.Value[0].SubVfs;
                    subPath = remainder;
                    return true;
                }
            }
        }

        subVfs = null;
        subPath = normalizedVirtualPath;
        return false;
    }

    private static string NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string norm = path.Replace('\\', '/').Trim();
        norm = norm.TrimStart('/');
        while (norm.Contains("//")) norm = norm.Replace("//", "/");
        return norm;
    }

    private static bool MatchesPattern(string fileName, string searchPattern)
    {
        if (string.IsNullOrEmpty(searchPattern) || searchPattern == "*") return true;
        return FileSystemName.MatchesSimpleExpression(searchPattern, fileName);
    }
}

internal sealed record MountedVfsEntry(IVirtualFileSystem SubVfs, int Priority);
