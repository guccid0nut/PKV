// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Vfs;

/// <summary>
///     Provider factory capable of opening readers and creating writers for specific archive extensions.
/// </summary>
public interface IArchiveProvider : IDisposable
{
    /// <summary>
    ///     Checks if this provider handles archives with the specified extension (e.g. ".s2z", ".honmod", ".zip").
    /// </summary>
    bool SupportsExtension(string extension);

    /// <summary>
    ///     Opens an archive reader over a readable stream.
    /// </summary>
    Task<IArchiveReader> OpenReaderAsync(Stream archiveStream, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates an archive writer over a writable stream.
    /// </summary>
    Task<IArchiveWriter> CreateWriterAsync(Stream destinationStream, CancellationToken cancellationToken = default);
}
