// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using NEXUS.Contracts.Vfs;

namespace Kongor.Core.Services.Vfs;

/// <summary>
///     100% in-memory virtual file system backed by concurrent dictionaries,
///     supporting thread-safe file operations, directories, streaming, and mounts with zero disk access.
/// </summary>
public sealed class InMemoryVirtualFileSystem : IVirtualFileSystem
{
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _mountLock = new();

    private readonly ConcurrentDictionary<string, List<MountedVfsEntry>>
        _mounts = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public InMemoryVirtualFileSystem()
    {
        _directories.TryAdd(string.Empty, 0);
    }

    public bool FileExists(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return true;

        return _files.ContainsKey(norm);
    }

    public bool DirectoryExists(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (string.IsNullOrEmpty(norm)) return true;

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.DirectoryExists(subPath))
                return true;

        if (_directories.ContainsKey(norm)) return true;

        foreach (string mountKey in _mounts.Keys)
            if (mountKey.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                mountKey.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        string prefix = norm + "/";
        return _files.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public Stream OpenRead(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.OpenRead(subPath);

        if (!_files.TryGetValue(norm, out byte[]? bytes))
            throw new FileNotFoundException($"Virtual file not found in memory: {virtualPath}");

        return new MemoryStream(bytes, false);
    }

    public async Task<Stream> OpenReadAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.OpenReadAsync(subPath, cancellationToken).ConfigureAwait(false);

        if (!_files.TryGetValue(norm, out byte[]? bytes))
            throw new FileNotFoundException($"Virtual file not found in memory: {virtualPath}");

        return new MemoryStream(bytes, false);
    }

    public byte[] ReadAllBytes(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.ReadAllBytes(subPath);

        if (!_files.TryGetValue(norm, out byte[]? bytes))
            throw new FileNotFoundException($"Virtual file not found in memory: {virtualPath}");

        byte[] copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, bytes.Length);
        return copy;
    }

    public async Task<byte[]> ReadAllBytesAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.ReadAllBytesAsync(subPath, cancellationToken).ConfigureAwait(false);

        if (!_files.TryGetValue(norm, out byte[]? bytes))
            throw new FileNotFoundException($"Virtual file not found in memory: {virtualPath}");

        byte[] copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, bytes.Length);
        return await Task.FromResult(copy).ConfigureAwait(false);
    }

    public string ReadAllText(string virtualPath)
    {
        byte[] bytes = ReadAllBytes(virtualPath);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<string> ReadAllTextAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadAllBytesAsync(virtualPath, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
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

        EnsureParentDirectories(norm);
        byte[] copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, bytes.Length);
        _files[norm] = copy;
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

        EnsureParentDirectories(norm);
        byte[] copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, bytes.Length);
        _files[norm] = copy;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void WriteAllText(string virtualPath, string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        WriteAllBytes(virtualPath, bytes);
    }

    public async Task WriteAllTextAsync(string virtualPath, string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await WriteAllBytesAsync(virtualPath, bytes, cancellationToken).ConfigureAwait(false);
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

        _files.TryRemove(norm, out _);
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

        EnsureParentDirectories(norm + "/dummy");
        _directories.TryAdd(norm, 0);
    }

    public IEnumerable<string> EnumerateFiles(
        string virtualPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);
        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

        string prefix = string.IsNullOrEmpty(norm) ? string.Empty : norm + "/";

        foreach (string file in _files.Keys)
            if (string.IsNullOrEmpty(prefix))
            {
                if (searchOption == SearchOption.TopDirectoryOnly && file.Contains('/')) continue;

                string fileName = Path.GetFileName(file);
                if (MatchesPattern(fileName, searchPattern)) results.Add(file);
            }
            else if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rel = file[prefix.Length..];
                if (searchOption == SearchOption.TopDirectoryOnly && rel.Contains('/')) continue;

                string fileName = Path.GetFileName(file);
                if (MatchesPattern(fileName, searchPattern)) results.Add(file);
            }

        // Include files from mounted systems
        foreach (KeyValuePair<string, List<MountedVfsEntry>> kvp in _mounts)
        {
            string mountPoint = kvp.Key;
            foreach (MountedVfsEntry entry in kvp.Value)
                if (norm.Equals(mountPoint, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(norm) ||
                    mountPoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

        // Discover all directory paths from explicit dirs and file parents
        HashSet<string> allKnownDirs = new(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in _directories.Keys)
            if (!string.IsNullOrEmpty(dir))
                allKnownDirs.Add(dir);

        foreach (string file in _files.Keys)
        {
            int idx = file.LastIndexOf('/');
            while (idx > 0)
            {
                string dir = file[..idx];
                allKnownDirs.Add(dir);
                idx = dir.LastIndexOf('/');
            }
        }

        string prefix = string.IsNullOrEmpty(norm) ? string.Empty : norm + "/";

        foreach (string dir in allKnownDirs)
            if (string.IsNullOrEmpty(prefix))
            {
                if (searchOption == SearchOption.TopDirectoryOnly && dir.Contains('/')) continue;

                string dirName = dir.Contains('/') ? Path.GetFileName(dir) : dir;
                if (MatchesPattern(dirName, searchPattern)) results.Add(dir);
            }
            else if (dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rel = dir[prefix.Length..];
                if (searchOption == SearchOption.TopDirectoryOnly && rel.Contains('/')) continue;

                string dirName = Path.GetFileName(dir);
                if (MatchesPattern(dirName, searchPattern)) results.Add(dir);
            }

        // Include directories from mounted systems
        foreach (KeyValuePair<string, List<MountedVfsEntry>> kvp in _mounts)
        {
            string mountPoint = kvp.Key;
            if (string.IsNullOrEmpty(norm))
            {
                string topMount = mountPoint.Contains('/') ? mountPoint[..mountPoint.IndexOf('/')] : mountPoint;
                if (!string.IsNullOrEmpty(topMount) && MatchesPattern(topMount, searchPattern)) results.Add(topMount);
            }
            else if (mountPoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string sub = mountPoint[prefix.Length..];
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
            entries.Sort((a, b) => b.Priority.CompareTo(a.Priority));
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
            _files.Clear();
            _directories.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EnsureParentDirectories(string normalizedFilePath)
    {
        int lastSlash = normalizedFilePath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            string dirPath = normalizedFilePath[..lastSlash];
            string[] parts = dirPath.Split('/');
            StringBuilder sb = new();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(parts[i]);
                _directories.TryAdd(sb.ToString(), 0);
            }
        }
    }

    private bool TryGetMountedTarget(string normalizedVirtualPath, [NotNullWhen(true)] out IVirtualFileSystem? subVfs,
        out string subPath)
    {
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
