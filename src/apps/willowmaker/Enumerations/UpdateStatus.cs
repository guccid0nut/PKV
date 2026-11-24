// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace WILLOWMAKER.Core.Enumerations;

/// <summary>
///     Represents the stage or outcome of checking whether a newer version of the application is available.
/// </summary>
public enum UpdateStatus
{
    CheckInProgress,
    ApplicationUpToDate,
    UpdateAvailable,
    UpdateDownloading,
    UpdateRestarting,
    RepositoryUnreachable
}
