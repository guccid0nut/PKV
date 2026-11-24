// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Store;

/// <summary>
///     Command to redeem a promotional store coupon code.
///     Protected by distributed lock on the normalized coupon code.
/// </summary>
public sealed record RedeemStoreCodeCommand(
    int AccountId,
    string Code,
    string? HostTime = "0") : IRequest<RedeemStoreCodeResultDto>, ILockableRequest
{
    public string LockKey => $"lock:coupon:{(string.IsNullOrWhiteSpace(Code) ? "unknown" : Code.Trim().ToUpperInvariant())}";
    public TimeSpan LockExpiry => TimeSpan.FromSeconds(10);
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(3);
}

/// <summary>
///     Result of redeeming a store code.
/// </summary>
public sealed record RedeemStoreCodeResultDto(
    bool Success,
    int ErrorCode,
    string? RedeemedField,
    IReadOnlyDictionary<string, object> RawResponse);
