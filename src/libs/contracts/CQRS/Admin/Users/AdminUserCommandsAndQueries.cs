// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Admin;

namespace NEXUS.Contracts.CQRS.Admin.Users;

/// <summary>
///     Query to retrieve overall system statistics for users, accounts, staff, and hosts.
/// </summary>
public record GetAdminUserSummaryQuery : IRequest<UserSummaryStatsDto>;

/// <summary>
///     Query to search for users by nickname, email, or discord handle with optional role filtering.
/// </summary>
public record SearchAdminUsersQuery(string? Query, string? RoleFilter) : IRequest<List<AdminUserDetailDto>>;

/// <summary>
///     Query to retrieve detailed profile and game accounts for a specific user ID.
/// </summary>
public record GetAdminUserByIdQuery(int Id) : IRequest<AdminUserDetailDto?>;

/// <summary>
///     Command to assign a global system role (Administrator, User, etc.) to a user.
/// </summary>
public record SetUserRoleCommand(int UserId, string RoleName) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to reset dedicated server host SRP password credentials.
/// </summary>
public record ResetHostPasswordCommand(int AccountId) : IRequest<ResetCredentialResponse?>;

/// <summary>
///     Command to reset dedicated server host authentication token.
/// </summary>
public record ResetHostAuthTokenCommand(int AccountId) : IRequest<ResetCredentialResponse?>;

/// <summary>
///     Command to update absolute user economy balances (gold, silver, plinko).
/// </summary>
public record UpdateUserEconomyCommand(
    int UserId,
    int GoldCoins,
    int SilverCoins,
    int PlinkoTickets) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to promote a user to Administrator and their main account to Staff.
/// </summary>
public record PromoteUserToStaffCommand(int UserId) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to promote a user's main account to ServerHost.
/// </summary>
public record PromoteUserToHostCommand(int UserId) : IRequest<AdminActionResultDto>;

/// <summary>
///     Command to revert a user to standard User role and all accounts to Normal.
/// </summary>
public record RevertUserToStandardCommand(int UserId) : IRequest<AdminActionResultDto>;
