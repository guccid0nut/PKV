// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.CQRS.Admin;

/// <summary>
///     Standard result DTO for administrative and moderation actions.
/// </summary>
public sealed record AdminActionResultDto(
    bool Success,
    string Message,
    int? AffectedId = null,
    string? Details = null);
