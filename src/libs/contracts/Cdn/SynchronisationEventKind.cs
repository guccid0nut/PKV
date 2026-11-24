// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     Classifies the entries reported by ContentBroker during distribution synchronisation.
/// </summary>
public enum SynchronisationEventKind
{
    PlanReady,
    DownloadStarted,
    Downloaded,
    Skipped,
    Deleted,
    DownloadFailed,
    DeletionFailed,
    Completed,
    ProgressUpdated
}
