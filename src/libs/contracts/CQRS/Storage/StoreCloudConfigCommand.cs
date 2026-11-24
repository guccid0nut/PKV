// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Storage;

/// <summary>
///     Command to store/upload a player's cloud configuration archive.
///     Implements <see cref="ILockableRequest"/> to prevent concurrent writes to cloud storage.
/// </summary>
public record StoreCloudConfigCommand(int AccountId, string Bucket, string? FileModifyTime, byte[] FileBytes)
    : ILockableRequest, IRequest<StoreCloudConfigResultDto>
{
    public string LockKey => $"lock:account:{AccountId}:cloud_storage";
    public TimeSpan LockExpiry => TimeSpan.FromSeconds(5);
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(2);
}

/// <summary>
///     Result of cloud config storage request.
/// </summary>
public record StoreCloudConfigResultDto(bool Success);
