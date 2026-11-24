// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Invites;

// 1. Generate Invite
public record GenerateInviteCommand(int UserId, string? IpAddress = null) : IRequest<GenerateInviteResult>;

public record GenerateInviteResult(
    bool Success,
    string? Code,
    string Message,
    int RemainingCapacity,
    int MaxAllowedActiveCodes,
    int ActiveUnusedCodes,
    int TotalGames);

// 2. User Quota
public record GetUserQuotaQuery(int UserId) : IRequest<GenerateInviteResult>;

// 3. User Invites
public record GetMyInvitesQuery(int UserId) : IRequest<List<InviteSummaryDto>>;

public record InviteSummaryDto(
    int Id,
    string Code,
    DateTimeOffset TimestampCreated,
    DateTimeOffset? TimestampRedeemed,
    string? RedeemedByAccountName,
    bool IsRevoked,
    bool IsRedeemed,
    string? RevocationReason);

// 4. Validate Invite
public record ValidateInviteQuery(string Code) : IRequest<ValidateInviteResult>;

public record ValidateInviteResult(
    bool IsValid,
    string? Message,
    string? InviterName);

// 5. Invite Only Mode
public record GetInviteOnlyModeQuery() : IRequest<bool>;

// 6. Invite System Status
public record GetInviteSystemStatusQuery() : IRequest<InviteSystemStatusResult>;

public record InviteSystemStatusResult(
    bool IsEnabled,
    int TotalGenerated,
    int TotalRedeemed,
    List<InviteAuditLogEntryDto> AuditLogs);

public record InviteAuditLogEntryDto(
    int Id,
    int StaffAccountId,
    string StaffAccountName,
    bool InviteOnlyEnabled,
    DateTimeOffset Timestamp,
    string? Notes);

// 7. Toggle Mode
public record ToggleInviteOnlyModeCommand(
    int StaffAccountId,
    string StaffAccountName,
    bool Enabled,
    string? Notes = null,
    string? IpAddress = null) : IRequest<ToggleInviteOnlyModeResult>;

public record ToggleInviteOnlyModeResult(bool Success, bool Enabled);

// 8. Lineage Tree
public record GetInviteLineageTreeQuery(int UserId, int MaxDepth = 4) : IRequest<InviteTreeNodeResult?>;

public record InviteTreeNodeResult(
    int UserId,
    string? DiscordUsername,
    string? DiscordId,
    string? AccountName,
    int? AccountId,
    DateTimeOffset TimestampCreated,
    bool IsBanned,
    string? BanReason,
    int TotalGames,
    int? InvitedByUserId,
    List<InviteTreeNodeResult> Children);

// 9. Prune Subtree
public record PruneInviteSubtreeCommand(
    int RootUserId,
    string Reason,
    bool BanAccounts) : IRequest<PruneInviteSubtreeResult>;

public record PruneInviteSubtreeResult(
    bool Success,
    int AffectedUsers,
    int RevokedCodes,
    int BannedAccounts);

// 10. Standalone Bootstrap Invites
public record BootstrapInvitesCommand(
    int Count,
    string? IpAddress = null) : IRequest<BootstrapInvitesResult>;

public record BootstrapInvitesResult(
    bool AdminFound,
    int Count,
    List<string> Codes);
