// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Storage;

/// <summary>
///     Query to retrieve cloud storage backup enrollment information for a player.
/// </summary>
public record GetUserEnrollmentQuery(int AccountId) : IRequest<UserEnrollmentResultDto?>;

/// <summary>
///     Result containing cloud storage enrollment details formatted for game client consumption.
/// </summary>
public record UserEnrollmentResultDto(
    string AccountId,
    string UseCloud,
    string AutomaticCloudUpload,
    string BackupLastUpdatedTime);
