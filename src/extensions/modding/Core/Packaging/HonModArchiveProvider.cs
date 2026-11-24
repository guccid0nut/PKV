// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Core.Packaging;

/// <summary>
///     Archive provider for HoN .honmod mod packages.
/// </summary>
internal sealed class HonModArchiveProvider : IArchiveProvider
{
    private bool _disposed;

    public bool SupportsExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        string clean = extension.TrimStart('.').ToLowerInvariant();
        return clean is "honmod" or "zip";
    }

    public Task<IArchiveReader> OpenReaderAsync(Stream archiveStream, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(archiveStream);

        IArchiveReader reader = S2zArchiveReader.Open(archiveStream, "mod.honmod", true);
        return Task.FromResult(reader);
    }

    public Task<IArchiveWriter> CreateWriterAsync(Stream destinationStream,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destinationStream);

        IArchiveWriter writer = new S2zStreamArchiveWriter(destinationStream, leaveOpen: true);
        return Task.FromResult(writer);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
