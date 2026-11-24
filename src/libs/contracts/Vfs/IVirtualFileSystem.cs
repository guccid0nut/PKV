// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Vfs;

/// <summary>
///     Abstraction for virtual file system operations, supporting physical storage,
///     in-memory hierarchies, mount points, and layered virtual overlays.
/// </summary>
public interface IVirtualFileSystem : IDisposable
{
    /// <summary>
    ///     Checks whether a file exists at the given virtual path.
    /// </summary>
    bool FileExists(string virtualPath);

    /// <summary>
    ///     Checks whether a directory exists at the given virtual path.
    /// </summary>
    bool DirectoryExists(string virtualPath);

    /// <summary>
    ///     Opens a readable stream for the specified virtual path.
    /// </summary>
    Stream OpenRead(string virtualPath);

    /// <summary>
    ///     Asynchronously opens a readable stream for the specified virtual path.
    /// </summary>
    Task<Stream> OpenReadAsync(string virtualPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads all bytes from the specified virtual file.
    /// </summary>
    byte[] ReadAllBytes(string virtualPath);

    /// <summary>
    ///     Asynchronously reads all bytes from the specified virtual file.
    /// </summary>
    Task<byte[]> ReadAllBytesAsync(string virtualPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads all text from the specified virtual file using UTF-8 encoding.
    /// </summary>
    string ReadAllText(string virtualPath);

    /// <summary>
    ///     Asynchronously reads all text from the specified virtual file using UTF-8 encoding.
    /// </summary>
    Task<string> ReadAllTextAsync(string virtualPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the byte content to the specified virtual file path, creating directories if needed.
    /// </summary>
    void WriteAllBytes(string virtualPath, byte[] bytes);

    /// <summary>
    ///     Asynchronously writes the byte content to the specified virtual file path.
    /// </summary>
    Task WriteAllBytesAsync(string virtualPath, byte[] bytes, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the text content to the specified virtual file path using UTF-8 encoding.
    /// </summary>
    void WriteAllText(string virtualPath, string text);

    /// <summary>
    ///     Asynchronously writes the text content to the specified virtual file path using UTF-8 encoding.
    /// </summary>
    Task WriteAllTextAsync(string virtualPath, string text, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the specified virtual file.
    /// </summary>
    void DeleteFile(string virtualPath);

    /// <summary>
    ///     Creates a directory hierarchy at the specified virtual path.
    /// </summary>
    void CreateDirectory(string virtualPath);

    /// <summary>
    ///     Enumerates relative or virtual file paths within the given directory.
    /// </summary>
    IEnumerable<string> EnumerateFiles(
        string virtualPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>
    ///     Enumerates relative or virtual directory paths within the given directory.
    /// </summary>
    IEnumerable<string> EnumerateDirectories(
        string virtualPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>
    ///     Mounts a child virtual file system onto a virtual path prefix with an execution priority.
    ///     Higher priority mounts are searched first.
    /// </summary>
    void Mount(string mountPoint, IVirtualFileSystem subVfs, int priority = 0);

    /// <summary>
    ///     Unmounts any virtual file system mounted at the specified virtual path prefix.
    /// </summary>
    void Unmount(string mountPoint);
}
