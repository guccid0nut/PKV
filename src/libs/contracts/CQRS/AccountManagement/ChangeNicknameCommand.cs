// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.AccountManagement;

/// <summary>
///     Command to change an account's player nickname.
///     Implements <see cref="ILockableRequest"/> to prevent concurrent name changes and race conditions.
/// </summary>
public record ChangeNicknameCommand(int AccountId, string NewNickname, string Password, string? Cookie = null)
    : ILockableRequest, IRequest<ChangeNicknameResultDto>
{
    public string LockKey => $"lock:account:{AccountId}:name_change";
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(10);
}

/// <summary>
///     Result of nickname change request, containing golden PHP response structure.
/// </summary>
public record ChangeNicknameResultDto(
    bool Success,
    int ErrorCode,
    string ErrorMessage,
    string? NewNickname,
    string? OldNickname,
    Dictionary<string, object> RawResponse);
