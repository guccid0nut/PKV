// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.DTOs;

public record UserInviteDto(
    int Id,
    string Code,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RedeemedAt,
    string? RedeemedAccountName,
    bool IsRevoked,
    bool IsRedeemed,
    string? RevocationReason = null);

public record GenerateInviteResultDto(
    bool Success,
    string? Code,
    string? Message,
    int RemainingQuota,
    int MaxAllowedActive,
    int ActiveUnused,
    int TotalCompletedGames);

public record ValidateInviteResultDto(
    bool IsValid,
    string? Message,
    string? InviterName = null);

public record InviteTreeNodeDto(
    int UserId,
    string? DiscordUsername,
    string? DiscordId,
    string? MainAccountName,
    int? MainAccountId,
    DateTimeOffset CreatedAt,
    bool IsBanned,
    string? BanReason,
    int TotalGames,
    int? InvitedByUserId,
    List<InviteTreeNodeDto> Invitees);

public record InviteModeToggleRequestDto(
    bool Enabled,
    string? Notes = null);

public record InviteSystemStatusDto(
    bool IsInviteOnlyEnabled,
    int TotalInvitesGenerated,
    int TotalRedeemed,
    List<InviteAuditLogDto> RecentAuditLogs);

public record InviteAuditLogDto(
    int Id,
    int StaffAccountId,
    string StaffAccountName,
    bool Enabled,
    DateTimeOffset Timestamp,
    string? Notes);

public record PruneSubtreeRequestDto(
    int RootUserId,
    string Reason,
    bool BanAccounts = true);
