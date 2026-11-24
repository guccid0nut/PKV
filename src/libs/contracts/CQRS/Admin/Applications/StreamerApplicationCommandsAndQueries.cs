// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Admin.Applications;

/// <summary>
///     Query to retrieve all streamer program applications.
/// </summary>
public sealed record GetStreamerApplicationsQuery : IRequest<List<StreamerApplicationDto>>;

/// <summary>
///     Command to approve a streamer program application.
/// </summary>
/// <param name="ApplicationId">The unique streamer application ID.</param>
public sealed record ApproveStreamerApplicationCommand(int ApplicationId) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to reject a streamer program application.
/// </summary>
/// <param name="ApplicationId">The unique streamer application ID.</param>
/// <param name="Reason">Optional explanation for rejection.</param>
public sealed record RejectStreamerApplicationCommand(int ApplicationId, string? Reason) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to revoke an approved streamer program application.
/// </summary>
/// <param name="ApplicationId">The unique streamer application ID.</param>
public sealed record RevokeStreamerApplicationCommand(int ApplicationId) : IRequest<AdminActionResultDto>;
