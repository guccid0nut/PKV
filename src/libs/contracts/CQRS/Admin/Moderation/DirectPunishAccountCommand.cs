// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Admin.Moderation;

/// <summary>
///     Command to apply an administrative sanction (ban, mute, or suspend) to an account directly.
/// </summary>
/// <param name="TargetAccountName">The target account name to sanction.</param>
/// <param name="PunishmentType">The sanction type ("ban", "mute", "suspend").</param>
/// <param name="DurationHours">Optional duration of the sanction in hours.</param>
/// <param name="Reason">Reason description for the sanction.</param>
/// <param name="Notes">Optional staff notes.</param>
/// <param name="StaffAccountId">The staff member account ID issuing the sanction.</param>
public sealed record DirectPunishAccountCommand(
    string TargetAccountName,
    string PunishmentType,
    int? DurationHours,
    string? Reason,
    string? Notes,
    int StaffAccountId) : IRequest<AdminActionResultDto>;
