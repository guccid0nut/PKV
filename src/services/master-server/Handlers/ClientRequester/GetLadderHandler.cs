// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Services.Plinko;
using KONGOR.MasterServer.Services.Quests;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Domain;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public sealed class GetLadderHandler(
    MerrickContext context,
    ILogger<GetLadderHandler> logger,
    IWarEffortService? warEffortService = null) : IClientRequestHandler
{
    public const int MaxLadderEntries = 100;
    public const int DefaultPageSize = 10;

    public async Task<IActionResult> HandleRequestAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = httpContext.Request;
        string rawMode = ExtractValue(request, "mode") ?? ExtractValue(request, "table") ?? "season";
        string normalizedMode = NormalizeMode(rawMode, out AccountStatisticsType statsType);

        int pageSize = DefaultPageSize;
        if (int.TryParse(ExtractValue(request, "page_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPageSize))
        {
            pageSize = Math.Clamp(parsedPageSize, 1, MaxLadderEntries);
        }

        int requestedPage = 1;
        if (int.TryParse(ExtractValue(request, "page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPage))
        {
            requestedPage = Math.Max(1, parsedPage);
        }

        if (string.Equals(normalizedMode, "codex", StringComparison.Ordinal))
        {
            return await HandleCodexLadderAsync(pageSize, requestedPage, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(normalizedMode, "quests", StringComparison.Ordinal))
        {
            return await HandleQuestsLadderAsync(pageSize, requestedPage, cancellationToken).ConfigureAwait(false);
        }

        IQueryable<AccountStatistics> baseQuery = context.AccountStatistics
            .AsNoTracking()
            .Include(s => s.Account)
            .ThenInclude(a => a!.Clan)
            .Where(s => s.Type == statsType && s.Account != null && s.Account.Type != AccountType.ServerHost)
            .OrderByDescending(s => s.SkillRating)
            .ThenByDescending(s => s.MatchesWon)
            .ThenBy(s => s.AccountID);

        List<AccountStatistics> topHundred = await baseQuery
            .Take(MaxLadderEntries)
            .ToListAsync(cancellationToken);

        int totalEntries = topHundred.Count;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalEntries / (double)pageSize));
        int currentPage = Math.Clamp(requestedPage, 1, totalPages);

        int skip = (currentPage - 1) * pageSize;
        List<AccountStatistics> pageSlice = topHundred
            .Skip(skip)
            .Take(pageSize)
            .ToList();

        logger.LogInformation(
            "[GetLadderHandler] Mode={Mode} (StatsType={StatsType}) Page={Page}/{TotalPages} PageSize={PageSize} TotalEntries={TotalEntries} ReturnedRows={RowCount}",
            normalizedMode,
            statsType,
            currentPage,
            totalPages,
            pageSize,
            totalEntries,
            pageSlice.Count);

        Dictionary<object, object> response = new()
        {
            ["mode"] = normalizedMode,
            ["page"] = currentPage.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["total_entries"] = totalEntries.ToString(CultureInfo.InvariantCulture),
            ["total_pages"] = totalPages.ToString(CultureInfo.InvariantCulture),
            ["count"] = pageSlice.Count.ToString(CultureInfo.InvariantCulture)
        };

        List<string> entriesTokens = new(pageSlice.Count);
        for (int i = 0; i < pageSlice.Count; i++)
        {
            AccountStatistics stat = pageSlice[i];
            int globalRank = skip + i + 1;
            string accountName = stat.Account?.Name ?? $"Hero_{stat.AccountID}";
            string? clanTag = stat.Account?.Clan?.Tag;
            string displayName = !string.IsNullOrWhiteSpace(clanTag)
                ? $"[{clanTag}] {accountName}"
                : accountName;

            int wins = Math.Max(0, stat.MatchesWon);
            int losses = Math.Max(0, stat.MatchesLost);
            int totalMatches = Math.Max(1, wins + losses);
            double winPct = (wins * 100.0) / totalMatches;
            int roundedMmr = (int)Math.Round(stat.SkillRating);
            (string tierLabel, string tierColor) = ResolveTierBadge(roundedMmr);

            Dictionary<object, object> rowEntry = new()
            {
                ["rank"] = globalRank.ToString(CultureInfo.InvariantCulture),
                ["name"] = displayName,
                ["nickname"] = accountName,
                ["mmr"] = roundedMmr.ToString(CultureInfo.InvariantCulture),
                ["wins"] = wins.ToString(CultureInfo.InvariantCulture),
                ["losses"] = losses.ToString(CultureInfo.InvariantCulture),
                ["win_pct"] = $"{winPct.ToString("F1", CultureInfo.InvariantCulture)}%",
                ["tier"] = tierLabel,
                ["tier_col"] = tierColor
            };

            response[i.ToString(CultureInfo.InvariantCulture)] = rowEntry;

            string safeDisplayName = displayName.Replace('|', '_').Replace(';', '_');
            string safeAccountName = accountName.Replace('|', '_').Replace(';', '_');
            entriesTokens.Add($"{globalRank}|{safeDisplayName}|{safeAccountName}|{roundedMmr}|{wins}|{losses}|{winPct.ToString("F1", CultureInfo.InvariantCulture)}%|{tierLabel}|{tierColor}");
        }

        response["entries_str"] = string.Join(";", entriesTokens);

        return new ContentResult
        {
            Content = PhpSerialization.Serialize(response),
            ContentType = "text/plain; charset=utf-8"
        };
    }

    private static string NormalizeMode(string rawMode, out AccountStatisticsType statsType)
    {
        string cleaned = rawMode.Trim().ToLowerInvariant();
        if (cleaned is "codex" or "graffiti")
        {
            statsType = AccountStatisticsType.Matchmaking;
            return "codex";
        }

        if (cleaned is "quests" or "quest" or "wareffort" or "war_effort")
        {
            statsType = AccountStatisticsType.Matchmaking;
            return "quests";
        }

        if (cleaned is "midwars" or "mid_wars" or "mw")
        {
            statsType = AccountStatisticsType.MidWars;
            return "midwars";
        }

        if (cleaned is "public" or "casual" or "quickmatch")
        {
            statsType = AccountStatisticsType.Public;
            return "public";
        }

        statsType = AccountStatisticsType.Matchmaking;
        return "season";
    }

    private static (string Tier, string ColorHex) ResolveTierBadge(int mmr)
    {
        if (mmr >= 1950) return ("IMMORTAL", "#ff9922");
        if (mmr >= 1850) return ("LEGENDARY", "#b878ff");
        if (mmr >= 1780) return ("DIAMOND I", "#45d4ff");
        if (mmr >= 1720) return ("DIAMOND II", "#45d4ff");
        if (mmr >= 1650) return ("GOLD I", "#f5c542");
        if (mmr >= 1580) return ("GOLD II", "#f5c542");
        if (mmr >= 1500) return ("SILVER I", "#c5d1db");
        return ("BRONZE I", "#cd7f32");
    }

    private async Task<IActionResult> HandleCodexLadderAsync(
        int pageSize,
        int requestedPage,
        CancellationToken cancellationToken)
    {
        List<GraffitiLadderStanding> topHundred =
            await CodexGraffitiService.QueryGraffitiStandingsAsync(context, MaxLadderEntries, cancellationToken).ConfigureAwait(false);

        int totalEntries = topHundred.Count;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalEntries / (double)pageSize));
        int currentPage = Math.Clamp(requestedPage, 1, totalPages);

        int skip = (currentPage - 1) * pageSize;
        List<GraffitiLadderStanding> pageSlice = topHundred
            .Skip(skip)
            .Take(pageSize)
            .ToList();

        logger.LogInformation(
            "[GetLadderHandler] Mode=codex Page={Page}/{TotalPages} PageSize={PageSize} TotalEntries={TotalEntries} ReturnedRows={RowCount}",
            currentPage,
            totalPages,
            pageSize,
            totalEntries,
            pageSlice.Count);

        Dictionary<object, object> response = new()
        {
            ["mode"] = "codex",
            ["page"] = currentPage.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["total_entries"] = totalEntries.ToString(CultureInfo.InvariantCulture),
            ["total_pages"] = totalPages.ToString(CultureInfo.InvariantCulture),
            ["count"] = pageSlice.Count.ToString(CultureInfo.InvariantCulture)
        };

        List<string> entriesTokens = new(pageSlice.Count);
        for (int i = 0; i < pageSlice.Count; i++)
        {
            GraffitiLadderStanding row = pageSlice[i];
            int globalRank = skip + i + 1;
            int totalMatches = row.Wins + row.Losses;
            double winPct = totalMatches > 0 ? (row.Wins * 100.0) / totalMatches : 0.0;
            (string tierLabel, string tierColor) = ResolveGraffitiTierBadge(row.WallsCompleted, globalRank);

            Dictionary<object, object> rowEntry = new()
            {
                ["rank"] = globalRank.ToString(CultureInfo.InvariantCulture),
                ["name"] = row.DisplayName,
                ["nickname"] = row.AccountName,
                ["mmr"] = row.WallsCompleted.ToString(CultureInfo.InvariantCulture),
                ["wins"] = row.AvatarsTagged.ToString(CultureInfo.InvariantCulture),
                ["losses"] = row.Losses.ToString(CultureInfo.InvariantCulture),
                ["win_pct"] = $"{winPct.ToString("F1", CultureInfo.InvariantCulture)}%",
                ["tier"] = tierLabel,
                ["tier_col"] = tierColor
            };

            response[i.ToString(CultureInfo.InvariantCulture)] = rowEntry;

            string safeDisplayName = row.DisplayName.Replace('|', '_').Replace(';', '_');
            string safeAccountName = row.AccountName.Replace('|', '_').Replace(';', '_');
            entriesTokens.Add($"{globalRank}|{safeDisplayName}|{safeAccountName}|{row.WallsCompleted}|{row.AvatarsTagged}|{row.Losses}|{winPct.ToString("F1", CultureInfo.InvariantCulture)}%|{tierLabel}|{tierColor}");
        }

        response["entries_str"] = string.Join(";", entriesTokens);

        return new ContentResult
        {
            Content = PhpSerialization.Serialize(response),
            ContentType = "text/plain; charset=utf-8"
        };
    }

    private static (string Tier, string ColorHex) ResolveGraffitiTierBadge(int wallsCompleted, int rank)
    {
        if (wallsCompleted >= 10 || (wallsCompleted > 0 && rank <= 3)) return ("Graffiti Master", "#ff4fd8");
        if (wallsCompleted >= 5 || (wallsCompleted > 0 && rank <= 10)) return ("Graffiti Star", "#ffd86b");
        if (wallsCompleted >= 2 || wallsCompleted > 0) return ("Graffiti Hero", "#45d4ff");
        return ("Street Punk", "#c5d1db");
    }

    private async Task<IActionResult> HandleQuestsLadderAsync(
        int pageSize,
        int requestedPage,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WarEffortLeaderboardEntry> entries = [];
        int totalEntries = 0;
        int cycleNumber = 1;

        if (warEffortService is not null && warEffortService.IsEnabled)
        {
            (entries, totalEntries, cycleNumber) = await warEffortService
                .GetLeaderboardAsync(requestedPage, pageSize, cycleNumber: null, cancellationToken)
                .ConfigureAwait(false);
        }

        int totalPages = Math.Max(1, (int)Math.Ceiling(totalEntries / (double)pageSize));
        int currentPage = Math.Clamp(requestedPage, 1, totalPages);

        logger.LogInformation(
            "[GetLadderHandler] Mode=quests Cycle={Cycle} Page={Page}/{TotalPages} PageSize={PageSize} TotalEntries={TotalEntries} ReturnedRows={RowCount}",
            cycleNumber, currentPage, totalPages, pageSize, totalEntries, entries.Count);

        Dictionary<object, object> response = new()
        {
            ["mode"] = "quests",
            ["cycle"] = cycleNumber.ToString(CultureInfo.InvariantCulture),
            ["page"] = currentPage.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["total_entries"] = totalEntries.ToString(CultureInfo.InvariantCulture),
            ["total_pages"] = totalPages.ToString(CultureInfo.InvariantCulture),
            ["count"] = entries.Count.ToString(CultureInfo.InvariantCulture)
        };

        List<string> entriesTokens = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            WarEffortLeaderboardEntry row = entries[i];
            string displayName = !string.IsNullOrWhiteSpace(row.ClanTag)
                ? $"[{row.ClanTag}] {row.AccountName}"
                : row.AccountName;
            (string tierLabel, string tierColor) = ResolveWarEffortTierBadge(row.TotalBagPoints, row.Rank);

            Dictionary<object, object> rowEntry = new()
            {
                ["rank"] = row.Rank.ToString(CultureInfo.InvariantCulture),
                ["name"] = displayName,
                ["nickname"] = row.AccountName,
                ["mmr"] = row.TotalBagPoints.ToString(CultureInfo.InvariantCulture),
                ["wins"] = row.QuestsCompletedCount.ToString(CultureInfo.InvariantCulture),
                ["losses"] = "0",
                ["win_pct"] = "100.0%",
                ["tier"] = tierLabel,
                ["tier_col"] = tierColor
            };

            response[i.ToString(CultureInfo.InvariantCulture)] = rowEntry;

            string safeDisplayName = displayName.Replace('|', '_').Replace(';', '_');
            string safeAccountName = row.AccountName.Replace('|', '_').Replace(';', '_');
            entriesTokens.Add($"{row.Rank}|{safeDisplayName}|{safeAccountName}|{row.TotalBagPoints}|{row.QuestsCompletedCount}|0|100.0%|{tierLabel}|{tierColor}");
        }

        response["entries_str"] = string.Join(";", entriesTokens);

        return new ContentResult
        {
            Content = PhpSerialization.Serialize(response),
            ContentType = "text/plain; charset=utf-8"
        };
    }

    private static (string Tier, string ColorHex) ResolveWarEffortTierBadge(int points, int rank)
    {
        if (points >= 2600 || (points > 0 && rank <= 3)) return ("Warlord", "#ff9922");
        if (points >= 1500 || (points > 0 && rank <= 10)) return ("Commander", "#ffd86b");
        if (points >= 700) return ("Vanguard", "#45d4ff");
        if (points >= 200) return ("Contender", "#88e06c");
        return ("Recruit", "#c5d1db");
    }

    private static string? ExtractValue(HttpRequest request, string key)
    {
        if (request.HasFormContentType && request.Form.ContainsKey(key))
            return request.Form[key].FirstOrDefault();

        if (request.Query.ContainsKey(key))
            return request.Query[key].FirstOrDefault();

        if (request.QueryString.HasValue &&
            SpanFormUrlEncodedParser.TryExtractParameter(
                request.QueryString.Value.AsSpan(),
                key,
                out ReadOnlySpan<char> span))
        {
            return span.ToString();
        }

        return null;
    }
}
