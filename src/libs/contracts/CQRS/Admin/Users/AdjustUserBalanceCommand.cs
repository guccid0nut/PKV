// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Admin.Users;

/// <summary>
///     Adjusts user silver coins, gold coins, or plinko tickets with concurrency protection via <see cref="ILockableRequest"/>.
/// </summary>
public sealed record AdjustUserBalanceCommand(
    int UserId,
    int GoldDelta,
    int SilverDelta,
    int TicketsDelta,
    string Reason = "Admin balance adjustment") : IRequest<AdminActionResultDto>, ILockableRequest
{
    public string LockKey => $"lock:user:{UserId}:economy";
    public TimeSpan LockExpiry => TimeSpan.FromSeconds(15);
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(5);
}
