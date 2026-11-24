// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Fleet;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     MediatR query handler for <see cref="GetHostLeaderboardQuery"/>.
///     Aggregates approved host applications and server host accounts,
///     calculates hosting ratios, community contribution metrics, and badge tiers.
/// </summary>
public sealed class GetHostLeaderboardHandler(
    MerrickContext db,
    ILogger<GetHostLeaderboardHandler> logger)
    : IRequestHandler<GetHostLeaderboardQuery, IReadOnlyList<HostLeaderboardEntryDto>>
{
    public async Task<IReadOnlyList<HostLeaderboardEntryDto>> Handle(
        GetHostLeaderboardQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("[GetHostLeaderboardHandler] Aggregating host leaderboard.");

        List<HostApplication> approvedApps = await db.HostApplications
            .Include(h => h.User).ThenInclude(u => u!.Accounts)
            .Include(h => h.MainAccount)
            .Include(h => h.HostAccount)
            .Where(h => h.Status == HostApplicationStatus.Approved)
            .ToListAsync(cancellationToken);

        List<Account> serverHostAccounts = await db.Accounts
            .Include(a => a.User).ThenInclude(u => u!.Accounts)
            .Where(a => a.Type == AccountType.ServerHost)
            .ToListAsync(cancellationToken);

        int totalMatchesHosted = await db.MatchStatistics.CountAsync(cancellationToken);

        List<User> allUsers =
        [
            .. approvedApps.Select(a => a.User)
                .Concat(serverHostAccounts.Select(s => s.User))
                .Where(u => u != null)
                .GroupBy(u => u!.ID)
                .Select(g => g.First()!)
        ];

        List<HostLeaderboardEntryDto> list = [];

        foreach (User user in allUsers)
        {
            List<HostApplication> userApps = [.. approvedApps.Where(a => a.UserID == user.ID || (a.User != null && a.User.ID == user.ID))];
            List<Account> userHostAccounts = [.. user.Accounts.Where(a => a.Type == AccountType.ServerHost)];

            List<string> hostNames =
            [
                .. userApps.Select(a => a.HostUsername)
                    .Concat(userHostAccounts.Select(a => a.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ];

            if (hostNames.Count == 0) continue;

            HostApplication? primaryApp = userApps.FirstOrDefault();
            Account? mainPlayerAcc = user.Accounts.FirstOrDefault(a => a.IsMain && a.Type != AccountType.ServerHost)
                                     ?? user.Accounts.FirstOrDefault(a => a.Type != AccountType.ServerHost);

            string displayName = primaryApp?.HostUsername
                                 ?? mainPlayerAcc?.Name
                                 ?? hostNames.FirstOrDefault()
                                 ?? "Host";

            List<int> playerAccIds = [.. user.Accounts.Where(a => a.Type != AccountType.ServerHost).Select(a => a.ID)];

            int hosted = await db.MatchStatistics.Where(m => hostNames.Contains(m.HostAccountName)).CountAsync(cancellationToken);
            int played = playerAccIds.Count > 0
                ? await db.PlayerStatistics.Where(ps => playerAccIds.Contains(ps.AccountID)).CountAsync(cancellationToken)
                : 0;

            if (hosted == 0 && primaryApp == null) continue;

            double ratio = played > 0 ? Math.Round((double)hosted / played, 2) : hosted > 0 ? hosted : 0.0;
            double contrib = totalMatchesHosted > 0 ? Math.Round((double)hosted / totalMatchesHosted * 100.0, 1) : 0.0;

            string tier = hosted switch
            {
                >= 250 => "Patron of Newerth",
                _ when ratio >= 2.0 && hosted >= 50 => "Patron of Newerth",
                >= 100 => "Master Host",
                _ when ratio >= 1.0 && hosted >= 25 => "Master Host",
                >= 25 => "Dedicated Host",
                _ when ratio >= 0.5 && hosted >= 10 => "Dedicated Host",
                _ => "Community Host"
            };

            list.Add(new HostLeaderboardEntryDto
            {
                AccountId = mainPlayerAcc?.ID ?? user.ID,
                AccountName = displayName,
                HostAccountName = displayName,
                AvatarUrl = $"/avatars/by-name/{Uri.EscapeDataString(displayName)}",
                GamesHosted = hosted,
                GamesPlayed = played,
                HostingRatio = ratio,
                CommunityContributionPercentage = contrib,
                HostBadgeTier = tier,
                Location = primaryApp?.Location ?? "USE",
                Instances = primaryApp?.Instances ?? 2
            });
        }

        list =
        [
            .. list
                .OrderByDescending(h => h.HostingRatio)
                .ThenByDescending(h => h.GamesHosted)
        ];

        for (int i = 0; i < list.Count; i++) list[i].Rank = i + 1;

        return list;
    }
}
