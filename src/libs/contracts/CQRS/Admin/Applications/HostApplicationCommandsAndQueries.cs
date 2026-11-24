// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Admin.Applications;

/// <summary>
///     Query to retrieve all dedicated server host applications.
/// </summary>
public sealed record GetHostApplicationsQuery : IRequest<List<HostApplicationDto>>;

/// <summary>
///     Command to approve a dedicated server host application.
/// </summary>
/// <param name="ApplicationId">The unique host application ID.</param>
public sealed record ApproveHostApplicationCommand(int ApplicationId) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to reject a dedicated server host application.
/// </summary>
/// <param name="ApplicationId">The unique host application ID.</param>
/// <param name="Reason">Optional explanation for rejection.</param>
public sealed record RejectHostApplicationCommand(int ApplicationId, string? Reason) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to revoke an approved dedicated server host application.
/// </summary>
/// <param name="ApplicationId">The unique host application ID.</param>
/// <param name="Reason">Optional explanation for revocation.</param>
public sealed record RevokeHostApplicationCommand(int ApplicationId, string? Reason = null) : IRequest<AdminActionResultDto>;
