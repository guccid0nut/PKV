// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.AccountManagement;

/// <summary>
///     Command to create a subaccount for a main account.
///     Implements <see cref="ILockableRequest"/> to prevent concurrent subaccount creation and race conditions.
/// </summary>
public record CreateSubAccountCommand(int MainAccountId, string SubNickname, string Password)
    : ILockableRequest, IRequest<CreateSubAccountResultDto>
{
    public string LockKey => $"lock:account:{MainAccountId}:create_subaccount";
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(10);
}

/// <summary>
///     Result of subaccount creation, containing golden PHP response structure.
/// </summary>
public record CreateSubAccountResultDto(
    bool Success,
    int ErrorCode,
    string ErrorMessage,
    int? SubAccountId,
    string? SubAccountName,
    Dictionary<string, object> RawResponse);
