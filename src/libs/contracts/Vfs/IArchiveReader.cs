// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Vfs;

/// <summary>
///     High-performance read interface for inspecting and extracting entries from an archive stream.
/// </summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>
    ///     Gets all entry names stored within the archive.
    /// </summary>
    IReadOnlyCollection<string> EntryNames { get; }

    /// <summary>
    ///     Checks whether an entry exists at the specified relative archive path.
    /// </summary>
    bool HasEntry(string entryPath);

    /// <summary>
    ///     Opens a stream to read the contents of the specified entry.
    /// </summary>
    Task<Stream> OpenEntryStreamAsync(string entryPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asynchronously reads all bytes of the specified archive entry.
    /// </summary>
    Task<byte[]> ReadEntryBytesAsync(string entryPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asynchronously reads all text of the specified archive entry using UTF-8 encoding.
    /// </summary>
    Task<string> ReadEntryTextAsync(string entryPath, CancellationToken cancellationToken = default);
}
