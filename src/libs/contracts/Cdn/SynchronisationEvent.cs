// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     A single observable step in the synchronisation pipeline.
///     <see cref="Plan" /> is populated only on the initial <see cref="SynchronisationEventKind.PlanReady" /> event.
/// </summary>
public sealed record SynchronisationEvent(
    SynchronisationEventKind Kind,
    string Detail,
    long Size,
    SynchronisationPlan? Plan = null);
