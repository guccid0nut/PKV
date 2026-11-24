// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Services.Rating;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KONGOR.MasterServer.Services.Matches;

public interface IOpenSkillRatingCalculator
{
    Task CalculateAndUpdateRatingsAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default);
}

public sealed class OpenSkillRatingCalculator(
    MerrickContext merrickContext,
    IOpenSkillRatingService openSkillRatingService,
    ILogger<OpenSkillRatingCalculator> logger) : IOpenSkillRatingCalculator
{
    public async Task CalculateAndUpdateRatingsAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default)
    {
        var processedPlayerEntries = context.ProcessedPlayerEntries;
        if (processedPlayerEntries.Count == 0) return;

        if (context.IsUnrankedListenServer || context.MatchType == NEXUS.Contracts.Domain.AccountStatisticsType.Cooperative)
        {
            foreach (PlayerIngestionEntry entry in processedPlayerEntries)
            {
                entry.PlayerStats.RankedMatch = 0;
                entry.PlayerStats.PublicMatch = 1;
                entry.PlayerStats.RankedSkillRatingChange = 0;
                entry.PlayerStats.PublicSkillRatingChange = 0;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[OpenSkillRatingCalculator] Match #{MatchId} (ListenServer={IsListen}, MatchType={MatchType}); skipping OpenSkill MMR/PSR changes for {Count} player(s).",
                    context.MatchID, context.IsUnrankedListenServer, context.MatchType, processedPlayerEntries.Count);
            }

            return;
        }

        List<int> processedAccountIds = [.. processedPlayerEntries.Select(e => e.Account.ID).Distinct()];
        List<AccountStatistics> allAccountStats = await merrickContext.AccountStatistics
            .Where(s => processedAccountIds.Contains(s.AccountID) && s.Type == context.MatchType)
            .ToListAsync(cancellationToken);
        Dictionary<int, AccountStatistics> accountStatsMap = allAccountStats.ToDictionary(s => s.AccountID);

        foreach (var entry in processedPlayerEntries)
        {
            if (!accountStatsMap.TryGetValue(entry.Account.ID, out AccountStatistics? accStats))
            {
                accStats = new AccountStatistics
                {
                    AccountID = entry.Account.ID,
                    Account = entry.Account,
                    Type = context.MatchType,
                    SkillRating = 1500.0,
                    Mu = 25.0,
                    Sigma = 25.0 / 3.0,
                    MatchesPlayed = 0
                };
                await merrickContext.AccountStatistics.AddAsync(accStats, cancellationToken);
                accountStatsMap[entry.Account.ID] = accStats;
            }
            else if (accStats.Mu <= 0 || accStats.Sigma <= 0)
            {
                (double derivedMu, double derivedSigma) = openSkillRatingService.DeriveInitialStateFromLegacyRating(
                    accStats.SkillRating > 0 ? accStats.SkillRating : 1500.0,
                    accStats.MatchesPlayed);
                accStats.Mu = derivedMu;
                accStats.Sigma = derivedSigma;
            }
        }

        // Determine Winning Team
        int winningTeam = 1;
        if (context.BinaryPayload != null && context.BinaryPayload.WinningTeam is 1 or 2)
        {
            winningTeam = context.BinaryPayload.WinningTeam;
        }
        else if (context.Form != null && context.Form.MatchStats.TryGetValue("winner", out string? winStr) &&
            int.TryParse(winStr, out int parsedWinner) && parsedWinner is 1 or 2)
        {
            winningTeam = parsedWinner;
        }
        else if (processedPlayerEntries.Any(e => e.PlayerStats.Team == 1 && e.IsWin))
        {
            winningTeam = 1;
        }
        else if (processedPlayerEntries.Any(e => e.PlayerStats.Team == 2 && e.IsWin))
        {
            winningTeam = 2;
        }
        else if (context.MatchStatistics is not null &&
                 context.MatchStatistics.ScoreTeam2 > context.MatchStatistics.ScoreTeam1)
        {
            winningTeam = 2;
        }

        List<OpenSkillPlayerRating> ratingInputs = [];
        foreach (var entry in processedPlayerEntries)
        {
            if (accountStatsMap.TryGetValue(entry.Account.ID, out AccountStatistics? accStats))
            {
                ratingInputs.Add(new OpenSkillPlayerRating
                {
                    AccountId = entry.Account.ID,
                    AccountName = entry.Account.Name,
                    Team = entry.PlayerStats.Team,
                    GroupNumber = entry.GroupNum,
                    GroupSize = entry.GroupSize,
                    MuBefore = accStats.Mu,
                    SigmaBefore = accStats.Sigma,
                    PsrBefore = accStats.SkillRating > 0 ? accStats.SkillRating : 1500.0,
                    Kills = entry.PlayerStats.HeroKills,
                    Deaths = entry.PlayerStats.HeroDeaths,
                    Assists = entry.PlayerStats.HeroAssists,
                    HeroDamage = entry.PlayerStats.HeroDamage,
                    TowerDamage = entry.PlayerStats.BuildingDamage,
                    IsMvp = entry.IsMvp,
                    MatchesPlayed = accStats.MatchesPlayed
                });
            }
        }

        OpenSkillMatchContext matchRatingContext = new()
        {
            MatchId = context.MatchID,
            WinningTeam = winningTeam,
            Players = ratingInputs,
            DurationSeconds = context.DurationSeconds
        };

        IReadOnlyList<OpenSkillPlayerResult> ratingResults = openSkillRatingService.CalculateMatchRatings(matchRatingContext);
        Dictionary<int, OpenSkillPlayerResult> resultMap = ratingResults.ToDictionary(r => r.AccountId);

        foreach (var entry in processedPlayerEntries)
        {
            if (resultMap.TryGetValue(entry.Account.ID, out OpenSkillPlayerResult? res) &&
                accountStatsMap.TryGetValue(entry.Account.ID, out AccountStatistics? accStats))
            {
                accStats.Mu = res.MuAfter;
                accStats.Sigma = res.SigmaAfter;
                accStats.SkillRating = res.PsrAfter;
                accStats.MatchesPlayed++;
                if (entry.IsWin) accStats.MatchesWon++;
                else accStats.MatchesLost++;
                if (entry.PlayerStats.Conceded > 0) accStats.MatchesConceded++;
                if (entry.PlayerStats.Disconnected > 0) accStats.MatchesDisconnected++;

                if (context.MatchType is NEXUS.Contracts.Domain.AccountStatisticsType.Matchmaking
                    or NEXUS.Contracts.Domain.AccountStatisticsType.MatchmakingCasual
                    or NEXUS.Contracts.Domain.AccountStatisticsType.MidWars
                    or NEXUS.Contracts.Domain.AccountStatisticsType.RiftWars)
                {
                    entry.PlayerStats.RankedMatch = 1;
                    entry.PlayerStats.PublicMatch = 0;
                    entry.PlayerStats.RankedSkillRatingChange = res.DeltaPsr;
                    entry.PlayerStats.PublicSkillRatingChange = 0;
                }
                else
                {
                    entry.PlayerStats.RankedMatch = 0;
                    entry.PlayerStats.PublicMatch = 1;
                    entry.PlayerStats.RankedSkillRatingChange = 0;
                    entry.PlayerStats.PublicSkillRatingChange = res.DeltaPsr;
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[OpenSkillRatingCalculator] [OpenSkill] Match #{MatchId} Account '{AccountName}' (Team {Team}): PSR {PsrBefore:F3} -> {PsrAfter:F3} ({DeltaPsr:+0.000;-0.000;+0.000}), \u03bc: {MuBefore:F3} -> {MuAfter:F3}, \u03c3: {SigmaBefore:F3} -> {SigmaAfter:F3}",
                        context.MatchID, entry.Account.Name, entry.PlayerStats.Team, res.PsrBefore, res.PsrAfter, res.DeltaPsr,
                        res.MuBefore, res.MuAfter, res.SigmaBefore, res.SigmaAfter);
                }
            }
        }

        if (context.MatchStatistics is not null && ratingResults.Count > 0)
        {
            var team1Psrs = ratingResults.Where(r => r.Team == 1).Select(r => r.PsrAfter).ToList();
            var team2Psrs = ratingResults.Where(r => r.Team == 2).Select(r => r.PsrAfter).ToList();

            if (team1Psrs.Count > 0)
                context.MatchStatistics.AveragePSRTeamOne = (int)Math.Round(team1Psrs.Average());
            if (team2Psrs.Count > 0)
                context.MatchStatistics.AveragePSRTeamTwo = (int)Math.Round(team2Psrs.Average());
            context.MatchStatistics.AveragePSR = (int)Math.Round(ratingResults.Select(r => r.PsrAfter).Average());
        }
    }
}
