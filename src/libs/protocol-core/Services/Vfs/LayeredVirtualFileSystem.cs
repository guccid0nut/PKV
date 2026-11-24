// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using NEXUS.Contracts.Vfs;

namespace Kongor.Core.Services.Vfs;

/// <summary>
///     Hierarchical layered virtual file system combining multiple underlying <see cref="IVirtualFileSystem" /> layers
///     (e.g. Memory Overlay -> Override Loose Directory -> Mod Archive -> Baseline Game Archive).
/// </summary>
public sealed class LayeredVirtualFileSystem : IVirtualFileSystem
{
    private readonly List<LayerEntry> _layers = [];

    private readonly ConcurrentDictionary<string, List<MountedVfsEntry>>
        _mounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _syncLock = new();
    private bool _disposed;

    public LayeredVirtualFileSystem()
    {
    }

    public LayeredVirtualFileSystem(IEnumerable<IVirtualFileSystem> initialLayers)
    {
        ArgumentNullException.ThrowIfNull(initialLayers);
        int priority = 100;
        foreach (IVirtualFileSystem layer in initialLayers)
        {
            AddLayer(layer, priority);
            priority -= 10;
        }
    }

    public IReadOnlyList<IVirtualFileSystem> Layers
    {
        get
        {
            lock (_syncLock)
            {
                return [.. _layers.Select(l => l.Vfs)];
            }
        }
    }

    public bool FileExists(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        // 1. Check mounts
        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return true;

        // 2. Query layers in descending priority order
        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.FileExists(norm))
                return true;

        return false;
    }

    public bool DirectoryExists(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (string.IsNullOrEmpty(norm)) return true;

        // 1. Check mounts
        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.DirectoryExists(subPath))
                return true;

        foreach (string mountKey in _mounts.Keys)
            if (mountKey.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                mountKey.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        // 2. Query layers
        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.DirectoryExists(norm))
                return true;

        return false;
    }

    public Stream OpenRead(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.OpenRead(subPath);

        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.FileExists(norm))
                return layer.Vfs.OpenRead(norm);

        throw new FileNotFoundException($"Virtual file '{virtualPath}' not found in any layered VFS provider.");
    }

    public async Task<Stream> OpenReadAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.OpenReadAsync(subPath, cancellationToken).ConfigureAwait(false);

        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.FileExists(norm))
                return await layer.Vfs.OpenReadAsync(norm, cancellationToken).ConfigureAwait(false);

        throw new FileNotFoundException($"Virtual file '{virtualPath}' not found in any layered VFS provider.");
    }

    public byte[] ReadAllBytes(string virtualPath)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return mountedVfs.ReadAllBytes(subPath);

        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.FileExists(norm))
                return layer.Vfs.ReadAllBytes(norm);

        throw new FileNotFoundException($"Virtual file '{virtualPath}' not found in any layered VFS provider.");
    }

    public async Task<byte[]> ReadAllBytesAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
            if (mountedVfs!.FileExists(subPath))
                return await mountedVfs.ReadAllBytesAsync(subPath, cancellationToken).ConfigureAwait(false);

        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.FileExists(norm))
                return await layer.Vfs.ReadAllBytesAsync(norm, cancellationToken).ConfigureAwait(false);

        throw new FileNotFoundException($"Virtual file '{virtualPath}' not found in any layered VFS provider.");
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
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            mountedVfs!.WriteAllBytes(subPath, bytes);
            return;
        }

        IVirtualFileSystem target = GetPrimaryWritableLayer();
        target.WriteAllBytes(norm, bytes);
    }

    public async Task WriteAllBytesAsync(string virtualPath, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            await mountedVfs!.WriteAllBytesAsync(subPath, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        IVirtualFileSystem target = GetPrimaryWritableLayer();
        await target.WriteAllBytesAsync(norm, bytes, cancellationToken).ConfigureAwait(false);
    }

    public void WriteAllText(string virtualPath, string text)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            mountedVfs!.WriteAllText(subPath, text);
            return;
        }

        IVirtualFileSystem target = GetPrimaryWritableLayer();
        target.WriteAllText(norm, text);
    }

    public async Task WriteAllTextAsync(string virtualPath, string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);

        if (TryGetMountedTarget(norm, out IVirtualFileSystem? mountedVfs, out string subPath))
        {
            await mountedVfs!.WriteAllTextAsync(subPath, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        IVirtualFileSystem target = GetPrimaryWritableLayer();
        await target.WriteAllTextAsync(norm, text, cancellationToken).ConfigureAwait(false);
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

        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.FileExists(norm))
                layer.Vfs.DeleteFile(norm);
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

        IVirtualFileSystem target = GetPrimaryWritableLayer();
        target.CreateDirectory(norm);
    }

    public IEnumerable<string> EnumerateFiles(
        string virtualPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        ThrowIfDisposed();
        string norm = NormalizeVirtualPath(virtualPath);
        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

        // 1. Enumerate all layers
        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.DirectoryExists(norm))
                foreach (string file in layer.Vfs.EnumerateFiles(norm, searchPattern, searchOption))
                    results.Add(NormalizeVirtualPath(file));

        // 2. Enumerate mounts
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
                        results.Add(NormalizeVirtualPath(combined));
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

        // 1. Enumerate all layers
        List<LayerEntry> snapshot = GetLayersSnapshot();
        foreach (LayerEntry layer in snapshot)
            if (layer.Vfs.DirectoryExists(norm))
                foreach (string dir in layer.Vfs.EnumerateDirectories(norm, searchPattern, searchOption))
                    results.Add(NormalizeVirtualPath(dir));

        // 2. Enumerate mounts
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

        lock (_syncLock)
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

        lock (_syncLock)
        {
            _mounts.TryRemove(normMount, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_syncLock)
        {
            foreach (LayerEntry layer in _layers) layer.Vfs.Dispose();
            _layers.Clear();

            foreach (List<MountedVfsEntry> entries in _mounts.Values)
            foreach (MountedVfsEntry entry in entries)
                entry.SubVfs.Dispose();

            _mounts.Clear();
        }
    }

    /// <summary>
    ///     Adds a VFS layer with a given priority. Higher priority layers are searched first.
    /// </summary>
    public void AddLayer(IVirtualFileSystem vfs, int priority = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(vfs);

        lock (_syncLock)
        {
            _layers.Add(new LayerEntry(vfs, priority));
            _layers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
    }

    /// <summary>
    ///     Removes a VFS layer from this layered file system.
    /// </summary>
    public bool RemoveLayer(IVirtualFileSystem vfs)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(vfs);

        lock (_syncLock)
        {
            int index = _layers.FindIndex(l => ReferenceEquals(l.Vfs, vfs));
            if (index >= 0)
            {
                _layers.RemoveAt(index);
                return true;
            }

            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private List<LayerEntry> GetLayersSnapshot()
    {
        lock (_syncLock)
        {
            return [.. _layers];
        }
    }

    private IVirtualFileSystem GetPrimaryWritableLayer()
    {
        lock (_syncLock)
        {
            if (_layers.Count == 0)
            {
                InMemoryVirtualFileSystem fallback = new();
                _layers.Add(new LayerEntry(fallback, 100));
                return fallback;
            }

            return _layers[0].Vfs;
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

internal sealed record LayerEntry(IVirtualFileSystem Vfs, int Priority);
