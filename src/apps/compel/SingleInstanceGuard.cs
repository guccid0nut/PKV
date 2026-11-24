// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL;

/// <summary>
///     Ensures that only one COMPEL process runs against a given installation at a time, using an exclusively-held lock
///     file that the operating system releases automatically when the process exits, even on a crash.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream? lockStream;

    private SingleInstanceGuard(FileStream? lockStream)
    {
        this.lockStream = lockStream;
    }

    public void Dispose()
    {
        lockStream?.Dispose();
    }

    /// <summary>
    ///     Attempts to exclusively acquire the lock file at <paramref name="lockFilePath" />. Returns <see langword="false" />
    ///     if another process already holds it.
    /// </summary>
    public static bool TryAcquire(string lockFilePath, out SingleInstanceGuard guard)
    {
        try
        {
            FileStream stream = new(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            guard = new SingleInstanceGuard(stream);

            return true;
        }

        catch (IOException)
        {
            guard = new SingleInstanceGuard(null);

            return false;
        }
    }
}
