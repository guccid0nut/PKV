// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Vfs;

/// <summary>
///     Stream-oriented archive writer interface for packaging files into game archives.
/// </summary>
public interface IArchiveWriter : IDisposable
{
    /// <summary>
    ///     Writes a binary payload as an entry in the archive.
    /// </summary>
    Task WriteEntryAsync(string entryPath, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes a stream payload as an entry in the archive.
    /// </summary>
    Task WriteEntryAsync(string entryPath, Stream dataStream, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Flushes any pending written entries to the underlying stream.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
