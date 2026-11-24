// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Status of a community game server hosting application.
/// </summary>
public enum HostApplicationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Revoked = 3
}
