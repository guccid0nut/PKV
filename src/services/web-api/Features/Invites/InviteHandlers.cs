// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations.Statistics;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Repositories;
using NEXUS.Contracts.CQRS.Invites;
using NEXUS.Contracts.Data;

namespace ZORGATH.WebPortal.API.Features.Invites;

public sealed class GenerateInviteHandler(
    IInviteRepository inviteRepo,
    ILogger<GenerateInviteHandler> logger)
    : IRequestHandler<GenerateInviteCommand, GenerateInviteResult>
{
    public async Task<GenerateInviteResult> Handle(GenerateInviteCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating invite code for User #{UserId}", request.UserId);
        var quota = await inviteRepo.GetUserQuotaStatusAsync(request.UserId, cancellationToken);

        if (!quota.CanGenerate)
        {
            string msg = quota.TotalGames < 100
                ? "You need at least 100 completed matchmaking games to earn an invite code."
                : quota.ActiveUnusedCodes >= 5
                    ? "You already have the maximum of 5 active invite codes at once. Share them with friends before generating more!"
                    : "You have generated all available invite codes for your current match tier. Play more matches to unlock another code!";

            return new GenerateInviteResult(
                false,
                null,
                msg,
                quota.RemainingCapacity,
                quota.MaxAllowedActiveCodes,
                quota.ActiveUnusedCodes,
                quota.TotalGames);
        }

        UserInvite? invite = await inviteRepo.CreateInviteCodeAsync(request.UserId, request.IpAddress, cancellationToken);
        if (invite == null)
        {
            logger.LogError("Failed to create invite code in database for User #{UserId}", request.UserId);
            return new GenerateInviteResult(
                false,
                null,
                "Failed to generate invite code.",
                0,
                quota.MaxAllowedActiveCodes,
                quota.ActiveUnusedCodes,
                quota.TotalGames);
        }

        var updatedQuota = await inviteRepo.GetUserQuotaStatusAsync(request.UserId, cancellationToken);
        return new GenerateInviteResult(
            true,
            invite.Code,
            "Invite code generated successfully!",
            updatedQuota.RemainingCapacity,
            updatedQuota.MaxAllowedActiveCodes,
            updatedQuota.ActiveUnusedCodes,
            updatedQuota.TotalGames);
    }
}

public sealed class GetUserQuotaHandler(IInviteRepository inviteRepo)
    : IRequestHandler<GetUserQuotaQuery, GenerateInviteResult>
{
    public async Task<GenerateInviteResult> Handle(GetUserQuotaQuery request, CancellationToken cancellationToken)
    {
        var quota = await inviteRepo.GetUserQuotaStatusAsync(request.UserId, cancellationToken);
        return new GenerateInviteResult(
            quota.CanGenerate,
            null,
            quota.CanGenerate ? "Quota available." : "Quota exhausted or minimum match requirement not met.",
            quota.RemainingCapacity,
            quota.MaxAllowedActiveCodes,
            quota.ActiveUnusedCodes,
            quota.TotalGames);
    }
}

public sealed class GetMyInvitesHandler(IInviteRepository inviteRepo)
    : IRequestHandler<GetMyInvitesQuery, List<InviteSummaryDto>>
{
    public async Task<List<InviteSummaryDto>> Handle(GetMyInvitesQuery request, CancellationToken cancellationToken)
    {
        var invites = await inviteRepo.GetUserInvitesAsync(request.UserId, cancellationToken);
        return invites.Select(i => new InviteSummaryDto(
            i.ID,
            i.Code,
            i.TimestampCreated,
            i.TimestampRedeemed,
            i.RedeemedByAccount?.Name,
            i.IsRevoked,
            i.IsRedeemed,
            i.RevocationReason)).ToList();
    }
}

public sealed class ValidateInviteHandler(IInviteRepository inviteRepo)
    : IRequestHandler<ValidateInviteQuery, ValidateInviteResult>
{
    public async Task<ValidateInviteResult> Handle(ValidateInviteQuery request, CancellationToken cancellationToken)
    {
        var (isValid, message, invite) = await inviteRepo.ValidateInviteCodeAsync(request.Code, cancellationToken);
        string? inviterName = invite?.CreatedByUser?.Accounts.FirstOrDefault(a => a.IsMain)?.Name
                              ?? invite?.CreatedByUser?.DiscordUsername;

        return new ValidateInviteResult(isValid, message, inviterName);
    }
}

public sealed class GetInviteOnlyModeHandler(IInviteRepository inviteRepo)
    : IRequestHandler<GetInviteOnlyModeQuery, bool>
{
    public async Task<bool> Handle(GetInviteOnlyModeQuery request, CancellationToken cancellationToken)
    {
        return await inviteRepo.IsInviteOnlyModeEnabledAsync(cancellationToken);
    }
}

public sealed class GetInviteSystemStatusHandler(IInviteRepository inviteRepo)
    : IRequestHandler<GetInviteSystemStatusQuery, InviteSystemStatusResult>
{
    public async Task<InviteSystemStatusResult> Handle(GetInviteSystemStatusQuery request, CancellationToken cancellationToken)
    {
        bool isEnabled = await inviteRepo.IsInviteOnlyModeEnabledAsync(cancellationToken);
        var (totalGen, totalRed) = await inviteRepo.GetInviteCountsAsync(cancellationToken);
        var auditLogs = await inviteRepo.GetModeAuditLogsAsync(50, cancellationToken);

        var auditDtos = auditLogs.Select(l => new InviteAuditLogEntryDto(
            l.ID,
            l.StaffAccountID,
            l.StaffAccountName,
            l.InviteOnlyEnabled,
            l.Timestamp,
            l.Notes)).ToList();

        return new InviteSystemStatusResult(isEnabled, totalGen, totalRed, auditDtos);
    }
}

public sealed class ToggleInviteOnlyModeHandler(IInviteRepository inviteRepo)
    : IRequestHandler<ToggleInviteOnlyModeCommand, ToggleInviteOnlyModeResult>
{
    public async Task<ToggleInviteOnlyModeResult> Handle(ToggleInviteOnlyModeCommand request, CancellationToken cancellationToken)
    {
        await inviteRepo.LogModeToggleAsync(
            request.StaffAccountId,
            request.StaffAccountName,
            request.Enabled,
            request.Notes,
            request.IpAddress,
            cancellationToken);

        return new ToggleInviteOnlyModeResult(true, request.Enabled);
    }
}

public sealed class GetInviteLineageTreeHandler(IInviteRepository inviteRepo)
    : IRequestHandler<GetInviteLineageTreeQuery, InviteTreeNodeResult?>
{
    public async Task<InviteTreeNodeResult?> Handle(GetInviteLineageTreeQuery request, CancellationToken cancellationToken)
    {
        var lineageUsers = await inviteRepo.GetInviteLineageTreeAsync(request.UserId, request.MaxDepth, cancellationToken);
        if (lineageUsers.Count == 0) return null;

        User root = lineageUsers.First(u => u.ID == request.UserId);
        return BuildTreeNode(root, lineageUsers);
    }

    private static InviteTreeNodeResult BuildTreeNode(User user, IReadOnlyList<User> allUsers)
    {
        Account? mainAcc = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        bool isBanned = mainAcc?.IsBanned ?? false;
        string? banReason = mainAcc?.BanReason;

        int totalGames = user.Accounts
            .SelectMany(a => a.Statistics)
            .Where(s => s.Type == AccountStatisticsType.Matchmaking ||
                        s.Type == AccountStatisticsType.MatchmakingCasual ||
                        s.Type == AccountStatisticsType.MidWars)
            .Sum(s => s.MatchesPlayed);

        var children = allUsers.Where(u => u.InvitedByUserID == user.ID).ToList();
        var childNodes = children.Select(c => BuildTreeNode(c, allUsers)).ToList();

        return new InviteTreeNodeResult(
            user.ID,
            user.DiscordUsername,
            user.DiscordId,
            mainAcc?.Name,
            mainAcc?.ID,
            user.TimestampCreated,
            isBanned,
            banReason,
            totalGames,
            user.InvitedByUserID,
            childNodes);
    }
}

public sealed class PruneInviteSubtreeHandler(
    IInviteRepository inviteRepo,
    IAccountRepository accountRepo,
    ILogger<PruneInviteSubtreeHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<PruneInviteSubtreeCommand, PruneInviteSubtreeResult>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PruneInviteSubtreeResult> Handle(PruneInviteSubtreeCommand request, CancellationToken cancellationToken)
    {
        logger.LogWarning("[InviteHandlers] Pruning invite subtree for User #{RootUserId}. Reason: {Reason}, BanAccounts: {BanAccounts}",
            request.RootUserId, request.Reason, request.BanAccounts);

        var lineageUsers = await inviteRepo.GetInviteLineageTreeAsync(request.RootUserId, 4, cancellationToken);
        int revokedCount = 0;
        int bannedCount = 0;

        foreach (var u in lineageUsers)
        {
            revokedCount += await inviteRepo.RevokeUnusedInvitesForUserAsync(u.ID, request.Reason, cancellationToken);

            if (request.BanAccounts)
            {
                var accounts = await accountRepo.GetAccountsForUserAsync(u.ID, cancellationToken);
                foreach (var acc in accounts)
                {
                    await accountRepo.BanAccountAsync(acc.ID, _timeProvider.GetUtcNow().AddYears(100), request.Reason, 1, cancellationToken);
                    bannedCount++;
                }
            }
        }

        return new PruneInviteSubtreeResult(true, lineageUsers.Count, revokedCount, bannedCount);
    }
}

public sealed class BootstrapInvitesHandler(
    IInviteRepository inviteRepo,
    ILogger<BootstrapInvitesHandler> logger)
    : IRequestHandler<BootstrapInvitesCommand, BootstrapInvitesResult>
{
    public async Task<BootstrapInvitesResult> Handle(BootstrapInvitesCommand request, CancellationToken cancellationToken)
    {
        int? adminUserId = await inviteRepo.ResolveBootstrapAdminUserIdAsync(cancellationToken);
        if (adminUserId is null)
        {
            return new BootstrapInvitesResult(false, 0, []);
        }

        IReadOnlyList<UserInvite> invites = await inviteRepo.CreateBootstrapInviteCodesAsync(
            adminUserId.Value,
            request.Count,
            request.IpAddress,
            cancellationToken);

        List<string> codes = invites.Select(i => i.Code).ToList();
        logger.LogInformation("[Bootstrap] Generated {Count} admin invite codes via BootstrapInvitesHandler", codes.Count);
        return new BootstrapInvitesResult(true, codes.Count, codes);
    }
}
