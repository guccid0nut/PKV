// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.AccountManagement;

/// <summary>
///     Command to reset player statistics and consume a stat reset item from inventory.
///     Implements <see cref="ILockableRequest"/> to prevent concurrent stat reset operations.
/// </summary>
public record ResetStatsCommand(int AccountId)
    : ILockableRequest, IRequest<ResetStatsResultDto>
{
    public string LockKey => $"lock:account:{AccountId}:stat_reset";
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(10);
}

/// <summary>
///     Result of stat reset request, containing golden PHP response structure.
/// </summary>
public record ResetStatsResultDto(
    bool Success,
    int ErrorCode,
    string ErrorMessage,
    Dictionary<string, object> RawResponse);
