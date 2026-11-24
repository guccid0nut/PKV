// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Cdn;

/// <summary>
///     A single non-fatal failure that occurred during synchronisation.
/// </summary>
public sealed record SynchronisationFailure(string Path, string Reason);
