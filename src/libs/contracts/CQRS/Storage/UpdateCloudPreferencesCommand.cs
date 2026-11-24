// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Storage;

/// <summary>
///     Command to update cloud storage backup preferences for a player.
///     Implements <see cref="ILockableRequest"/> to prevent race conditions during preference updates.
/// </summary>
public record UpdateCloudPreferencesCommand(int AccountId, bool? UseCloud, bool? AutoUpload)
    : ILockableRequest, IRequest<UpdateCloudPreferencesResultDto>
{
    public string LockKey => $"lock:account:{AccountId}:cloud_storage";
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(10);
}

/// <summary>
///     Result of cloud preferences update.
/// </summary>
public record UpdateCloudPreferencesResultDto(bool Success);
