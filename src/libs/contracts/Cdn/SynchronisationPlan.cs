// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     The work the synchronisation has decided to do, calculated up-front and reported once via the
///     <see cref="SynchronisationEventKind.PlanReady" /> event.
/// </summary>
public sealed record SynchronisationPlan(
    int FilesToDownload,
    int FilesToDelete,
    int FilesToSkip,
    int FilesUpToDate,
    long TotalBytesToDownload)
{
    public override string ToString()
    {
        return
            $"{FilesToDownload} To Download ({TotalBytesToDownload:N0} Bytes), {FilesToDelete} To Delete, {FilesToSkip} To Skip, {FilesUpToDate} Up To Date";
    }
}
