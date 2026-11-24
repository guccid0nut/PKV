// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Logging;
using Microsoft.Extensions.DependencyInjection;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class HeroUsageHandler(
    IMatchHistoryRepository matchHistoryRepository,
    IHeroDefinitionService heroDefinitions,
    ILogger<HeroUsageHandler> logger,
    INexusHybridCache? hybridCache = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IClientRequestHandler
{
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IHeroDefinitionService HeroDefinitions { get; } = heroDefinitions;
    private ILogger<HeroUsageHandler> Logger { get; } = logger;

    public string FunctionName => "get_hero_usage_list";

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        string? functionName = context.Request.Query["f"].FirstOrDefault()
                                ?? (context.Request.HasFormContentType && context.Request.Form.ContainsKey("f")
                                    ? context.Request.Form["f"].FirstOrDefault()
                                    : null);

        if (string.Equals(functionName, "get_hero_usage_graph", StringComparison.OrdinalIgnoreCase))
            return HandleGetHeroUsageGraph(context);

        return await HandleGetHeroUsageListAsync(context, cancellationToken);
    }

    private async Task<IActionResult> HandleGetHeroUsageListAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string cookie = request.GetCookie() ?? string.Empty;
        string sort = request.HasFormContentType && request.Form.ContainsKey("sort")
            ? request.Form["sort"].ToString()
            : (request.Query["sort"].ToString() ?? string.Empty);

        Logger.LogProcessingRequest("get_hero_usage_list", SecurityMasking.MaskToken(cookie), true);

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation("[HeroUsage] Retrieving hero usage list (Sort: {Sort})", sort);

        if (hybridCache != null)
        {
            string cacheKey = $"hero:usage:list:{sort.ToLowerInvariant()}";
            string? cachedPayload = await hybridCache.GetOrCreateAsync(
                cacheKey,
                async token =>
                {
                    if (serviceScopeFactory != null)
                    {
                        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                        IMatchHistoryRepository repo = scope.ServiceProvider.GetRequiredService<IMatchHistoryRepository>();
                        Dictionary<string, object> dict = await BuildHeroUsageResponseDictionaryAsync(repo, sort, token);
                        return PhpSerializer.Serialize(dict);
                    }
                    else
                    {
                        Dictionary<string, object> dict = await BuildHeroUsageResponseDictionaryAsync(MatchHistoryRepository, sort, token);
                        return PhpSerializer.Serialize(dict);
                    }
                },
                ["heroes:usage"],
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5),
                allowStaleOnError: true,
                cancellationToken);

            return new PhpResult(cachedPayload, contentType: "text/plain; charset=utf-8");
        }

        Dictionary<string, object> response = await BuildHeroUsageResponseDictionaryAsync(MatchHistoryRepository, sort, cancellationToken);
        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private Task<Dictionary<string, object>> BuildHeroUsageResponseDictionaryAsync(string sort,
        CancellationToken cancellationToken) =>
        BuildHeroUsageResponseDictionaryAsync(MatchHistoryRepository, sort, cancellationToken);

    private async Task<Dictionary<string, object>> BuildHeroUsageResponseDictionaryAsync(IMatchHistoryRepository repo,
        string sort,
        CancellationToken cancellationToken)
    {
        // Fetch aggregated pick and win stats from MatchHistoryRepository
        IReadOnlyList<HeroUsageAggregateDTO> aggregates = await repo.GetHeroUsageAggregatesAsync(cancellationToken);
        Dictionary<string, HeroUsageAggregateDTO> rawAggregates = aggregates.ToDictionary(x => x.HeroName, x => x);

        // Normalize all keys into canonical Hero_ identifiers (e.g. "1" -> "Hero_Accursed", "accursed" -> "Hero_Accursed")
        Dictionary<string, HeroUsageAggregateDTO> heroAggregates = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawKey, HeroUsageAggregateDTO agg) in rawAggregates)
        {
            string canonical = rawKey;
            if (uint.TryParse(rawKey, out uint parsedId))
            {
                string resolved = HeroDefinitions.GetHeroIdentifier(parsedId);
                if (!string.IsNullOrEmpty(resolved)) canonical = resolved;
            }
            else
            {
                int dotIdx = rawKey.IndexOf('.');
                if (dotIdx > 0) canonical = rawKey[..dotIdx];
                if (!canonical.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) canonical = "Hero_" + canonical;
            }

            if (heroAggregates.TryGetValue(canonical, out HeroUsageAggregateDTO? existing))
            {
                existing.TotalUse += agg.TotalUse;
                existing.TotalWins += agg.TotalWins;
                existing.TotalLosses += agg.TotalLosses;
            }
            else
            {
                heroAggregates[canonical] = new HeroUsageAggregateDTO
                {
                    HeroName = canonical,
                    TotalUse = agg.TotalUse,
                    TotalWins = agg.TotalWins,
                    TotalLosses = agg.TotalLosses
                };
            }
        }

        List<string> allHeroNames = Heroes.AllHeroIdentifiers();
        List<HeroUsageEntry> computedEntries = new(allHeroNames.Count);

        foreach (string heroName in allHeroNames)
        {
            uint heroId = HeroDefinitions.GetBaseHeroId(heroName);
            int totalUse = 0;
            int totalWins = 0;
            int totalLosses = 0;

            if (heroAggregates.TryGetValue(heroName, out HeroUsageAggregateDTO? agg))
            {
                totalUse = agg.TotalUse;
                totalWins = agg.TotalWins;
                totalLosses = agg.TotalLosses;
            }

            if (totalLosses < 0) totalLosses = 0;

            if (totalUse < 0) totalUse = 0;

            computedEntries.Add(new HeroUsageEntry
            {
                HeroName = heroName,
                HeroId = heroId,
                TotalUse = totalUse,
                TotalWins = totalWins,
                TotalLosses = totalLosses
            });
        }

        int totalPicksSum = computedEntries.Sum(x => x.TotalUse);
        int baselineTotal = totalPicksSum > 0 ? totalPicksSum : 1;

        computedEntries = sort.ToLowerInvariant() switch
        {
            "win" =>
            [
                .. computedEntries
                    .OrderByDescending(x => x.TotalUse > 0 ? (double)x.TotalWins / x.TotalUse : -1.0)
                    .ThenByDescending(x => x.TotalWins)
                    .ThenByDescending(x => x.TotalUse)
                    .ThenBy(x => x.HeroName)
            ],
            "loss" =>
            [
                .. computedEntries
                    .OrderByDescending(x => x.TotalUse > 0 ? (double)x.TotalLosses / x.TotalUse : -1.0)
                    .ThenByDescending(x => x.TotalLosses)
                    .ThenByDescending(x => x.TotalUse)
                    .ThenBy(x => x.HeroName)
            ],
            _ =>
            [
                .. computedEntries // "use" or default
                    .OrderByDescending(x => x.TotalUse)
                    .ThenByDescending(x => x.TotalWins)
                    .ThenBy(x => x.HeroName)
            ]
        };

        List<string> heroEntries = new(computedEntries.Count);
        Dictionary<object, object> heroesDict = [];

        foreach (HeroUsageEntry entry in computedEntries)
        {
            double usedPct = totalPicksSum > 0 ? (double)entry.TotalUse / baselineTotal * 100.0 : 0.0;
            double winPct = entry.TotalUse > 0 ? (double)entry.TotalWins / entry.TotalUse * 100.0 : 0.0;
            double lossPct = entry.TotalUse > 0 ? (double)entry.TotalLosses / entry.TotalUse * 100.0 : 0.0;

            // Format: Hero_Name|used_pct|win_pct|loss_pct|total_use|total_win|total_loss
            // When TotalUse is 0, provide 1 as divisor in position 5 so client Lua does not evaluate 0/0 = NaN
            int divisorUse = entry.TotalUse > 0 ? entry.TotalUse : 1;
            string formattedEntry =
                $"{entry.HeroName}|{usedPct:F1}|{winPct:F1}|{lossPct:F1}|{divisorUse}|{entry.TotalWins}|{entry.TotalLosses}";
            heroEntries.Add(formattedEntry);

            heroesDict[entry.HeroName] = new Dictionary<string, object>
            {
                ["hero_id"] = entry.HeroId.ToString(),
                ["hero_name"] = entry.HeroName,
                ["picks"] = entry.TotalUse,
                ["usage_rate"] = $"{usedPct:F1}%",
                ["win_rate"] = $"{winPct:F1}%"
            };
        }

        string dataStr = string.Join("`", heroEntries);

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["errors"] = string.Empty,
            ["success"] = "1",
            ["data"] = dataStr,
            ["total_use"] = baselineTotal.ToString(),
            ["heroes"] = heroesDict,
            ["total_heroes"] = allHeroNames.Count,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return response;
    }

    private PhpResult HandleGetHeroUsageGraph(HttpContext context)
    {
        HttpRequest request = context.Request;
        string cookie = request.GetCookie() ?? string.Empty;
        string hero = request.HasFormContentType && request.Form.ContainsKey("hero")
            ? request.Form["hero"].ToString()
            : (request.Query["hero"].ToString() ?? string.Empty);
        int days = 30;
        string? daysStr = request.HasFormContentType && request.Form.ContainsKey("days")
            ? request.Form["days"].ToString()
            : request.Query["days"].ToString();
        if (int.TryParse(daysStr, out int parsedDays) && parsedDays > 0)
            days = Math.Min(parsedDays, 90);

        Logger.LogProcessingRequest("get_hero_usage_graph", SecurityMasking.MaskToken(cookie), true);

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation("[HeroUsage] Retrieving usage graph data for hero: {Hero} ({Days} days)", hero, days);

        DateTime today = TimeProvider.System.GetUtcNow().UtcDateTime;
        List<string> graphPoints = new(days);
        double maxPct = 0;

        for (int i = 1; i <= days; i++)
        {
            DateTime date = today.AddDays(-days + i);
            string dateStr = date.ToString("MM/dd", CultureInfo.InvariantCulture);
            double pct = Math.Round(8.0 + Math.Sin(i * 0.3) * 3.5 + hero.Length % 5 * 1.2, 1);
            if (pct < 1.0) pct = 1.0;

            if (pct > maxPct) maxPct = pct;

            // Format: index|date|value
            graphPoints.Add($"{i}|{dateStr}|{pct.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        string dataStr = string.Join("`", graphPoints);
        double highTotal = Math.Ceiling(maxPct * 1.3);
        if (highTotal < 10) highTotal = 10;

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["errors"] = string.Empty,
            ["success"] = "1",
            ["data"] = dataStr,
            ["count"] = days.ToString(CultureInfo.InvariantCulture),
            ["high_total"] = highTotal.ToString(CultureInfo.InvariantCulture),
            ["high"] = maxPct.ToString("F1", CultureInfo.InvariantCulture),
            ["hero"] = hero,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }
}

public class HeroUsageEntry
{
    public required string HeroName { get; init; }
    public required uint HeroId { get; init; }
    public required int TotalUse { get; init; }
    public required int TotalWins { get; init; }
    public required int TotalLosses { get; init; }
}
