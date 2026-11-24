// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Repositories;

public interface IInviteRepository
{
    Task<int> GetTotalMatchmakingGamesAsync(int userId, CancellationToken ct = default);

    Task<(int TotalGames, int MaxAllowedActiveCodes, int ActiveUnusedCodes, int RemainingCapacity, bool CanGenerate)>
        GetUserQuotaStatusAsync(int userId, CancellationToken ct = default);

    Task<UserInvite?> CreateInviteCodeAsync(int userId, string? ipAddress = null, CancellationToken ct = default);

    Task<(bool IsValid, string? Message, UserInvite? Invite)> ValidateInviteCodeAsync(string code,
        CancellationToken ct = default);

    Task<bool> RedeemInviteCodeAsync(string code, int redeemedUserId, int redeemedAccountId, string? ipAddress = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<UserInvite>> GetUserInvitesAsync(int userId, CancellationToken ct = default);

    Task<IReadOnlyList<User>> GetInviteLineageTreeAsync(int rootUserId, int maxDepth = 4,
        CancellationToken ct = default);

    Task<int> RevokeUnusedInvitesForUserAsync(int userId, string reason, CancellationToken ct = default);

    Task LogModeToggleAsync(int staffAccountId, string staffAccountName, bool enabled, string? notes = null,
        string? ip = null, CancellationToken ct = default);

    Task<IReadOnlyList<InviteSystemAuditLog>> GetModeAuditLogsAsync(int count = 50, CancellationToken ct = default);

    Task<bool> IsInviteOnlyModeEnabledAsync(CancellationToken ct = default);

    Task SetInviteOnlyModeAsync(bool enabled, CancellationToken ct = default);

    Task<(int TotalGenerated, int TotalRedeemed)> GetInviteCountsAsync(CancellationToken ct = default);

    Task<int?> ResolveBootstrapAdminUserIdAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UserInvite>> CreateBootstrapInviteCodesAsync(int adminUserId, int count, string? ipAddress = null, CancellationToken ct = default);
}
