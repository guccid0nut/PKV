// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     The final outcome and aggregated statistics of a synchronisation run.
/// </summary>
public sealed record SynchronisationSummary(
    int FilesDownloaded,
    int FilesDeleted,
    int FilesUpToDate,
    int FilesFailed,
    long BytesDownloaded,
    IReadOnlyList<SynchronisationFailure> Failures)
{
    public override string ToString()
    {
        return
            $"{FilesDownloaded} Downloaded, {FilesDeleted} Deleted, {FilesUpToDate} Up To Date, {FilesFailed} Failed, {BytesDownloaded:N0} Bytes Transferred";
    }
}
