// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.Globalization;
using System.Text.Json;
using KONGOR.MasterServer.Services.Matches;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Quests;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Quests;

namespace KONGOR.MasterServer.Services.Quests;

/// <summary>
///     Authoritative Stage-2 match progression evaluator for War Effort objectives.
///     Executes inside the <see cref="MatchProcessingService"/> database transaction so quest progress
///     commits atomically alongside match statistics and Hero Mastery.
/// </summary>
public sealed class WarEffortProgressionService : IWarEffortProgressionService
{
    private readonly MerrickContext _db;
    private readonly IWarEffortService _warEffortService;
    private readonly IOptionsMonitor<WarEffortOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WarEffortProgressionService> _logger;

    public WarEffortProgressionService(
        MerrickContext db,
        IWarEffortService warEffortService,
        IOptionsMonitor<WarEffortOptions> optionsMonitor,
        ILogger<WarEffortProgressionService> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _warEffortService = warEffortService;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ProcessMatchProgressionAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default)
    {
        WarEffortOptions opts = _optionsMonitor.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogDebug("[WAR_EFFORT][SKIP] MatchId={MatchId} skipped: WarEffort:Enabled is false.", context.MatchID);
            return;
        }

        if (context.ProcessedPlayerEntries.Count == 0)
        {
            _logger.LogDebug("[WAR_EFFORT][SKIP] MatchId={MatchId} skipped: zero processed players.", context.MatchID);
            return;
        }

        string? skipReason = EvaluateMatchEligibility(context, opts);
        if (skipReason is not null)
        {
            _logger.LogInformation(
                "[WAR_EFFORT][SKIP] MatchId={MatchId} Map={Map} Mode={Mode} Duration={Duration}s Players={Players} CountBotMatches={CountBots} Reason={Reason}",
                context.MatchID,
                context.MatchStatistics.Map,
                context.MatchStatistics.GameMode,
                context.DurationSeconds,
                context.ProcessedPlayerEntries.Count,
                opts.CountBotMatches,
                skipReason);
            return;
        }

        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        string mapLower = (context.MatchStatistics.Map ?? string.Empty).Trim().ToLowerInvariant();

        // Preload hero metadata for attribute / attack-type / role quest evaluation
        HashSet<string> playedHeroKeys = context.ProcessedPlayerEntries
            .Select(p => NormalizeHeroKey(p.HeroKey))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, HeroDetail> heroDetailsByCode = playedHeroKeys.Count > 0
            ? (await _db.HeroDetails
                .AsNoTracking()
                .Where(h => playedHeroKeys.Contains(h.HeroCode))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
                .GroupBy(h => h.HeroCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, HeroDetail>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, HashSet<string>> heroRolesByCode = new(StringComparer.OrdinalIgnoreCase);
        if (playedHeroKeys.Count > 0)
        {
            string[] knownRoles = ["solo", "jungle", "carry", "support", "initiator", "ganker", "pusher"];
            var guideTexts = await _db.HeroGuides
                .AsNoTracking()
                .Where(g => playedHeroKeys.Contains(g.HeroIdentifier))
                .Select(g => new { g.HeroIdentifier, g.Name, g.Intro })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var guide in guideTexts)
            {
                if (string.IsNullOrWhiteSpace(guide.HeroIdentifier)) continue;
                string combined = $"{guide.Name} {guide.Intro}";
                foreach (string role in knownRoles)
                {
                    if (combined.Contains(role, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!heroRolesByCode.TryGetValue(guide.HeroIdentifier, out HashSet<string>? set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            heroRolesByCode[guide.HeroIdentifier] = set;
                        }
                        set.Add(role);
                    }
                }
            }
        }

        List<QuestDefinition> allEnabledDefs = await _db.QuestDefinitions
            .AsNoTracking()
            .Where(q => q.IsEnabled)
            .OrderBy(q => q.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int updatedSlotsTotal = 0;
        int completedQuestsTotal = 0;

        foreach (PlayerIngestionEntry entry in context.ProcessedPlayerEntries)
        {
            if (IsBotOrHostAccount(entry))
            {
                continue;
            }

            if (entry.PlayerStats.Disconnected > 0 && !opts.CountBotMatches)
            {
                _logger.LogInformation(
                    "[WAR_EFFORT][SKIP_PLAYER] MatchId={MatchId} AccountId={AccountId} ({Name}) skipped: player disconnected/leaver.",
                    context.MatchID, entry.Account.ID, entry.Account.Name);
                continue;
            }

            WarEffortAccountState accountState = await _warEffortService
                .EnsureActiveCycleAndAccountStateAsync(entry.Account.ID, cancellationToken)
                .ConfigureAwait(false);

            // Re-fetch tracked progress and slots inside the active DbContext transaction
            AccountWarEffortProgress? trackedProgress = await _db.AccountWarEffortProgress
                .FirstOrDefaultAsync(
                    p => p.AccountId == entry.Account.ID && p.CycleId == accountState.Cycle.Id,
                    cancellationToken)
                .ConfigureAwait(false);

            List<AccountQuestSlot> trackedSlots = await _db.AccountQuestSlots
                .Where(s => s.AccountId == entry.Account.ID && s.CycleId == accountState.Cycle.Id)
                .OrderBy(s => s.SlotIndex)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (trackedProgress is null || trackedSlots.Count == 0)
            {
                continue;
            }

            string normalizedHero = NormalizeHeroKey(entry.HeroKey);
            heroDetailsByCode.TryGetValue(normalizedHero, out HeroDetail? heroDetail);
            heroRolesByCode.TryGetValue(normalizedHero, out HashSet<string>? heroRoles);
            Dictionary<string, double> customMetrics = ParseCustomStatsJson(entry.PlayerStats.CustomStats);

            HashSet<int> activeQuestIds = trackedSlots.Select(s => s.QuestDefinitionId).ToHashSet();

            foreach (AccountQuestSlot slot in trackedSlots)
            {
                if (slot.LastMatchId == context.MatchID)
                {
                    continue; // Idempotent guard if match is reprocessed
                }

                if (!accountState.Definitions.TryGetValue(slot.QuestDefinitionId, out QuestDefinition? def))
                {
                    continue;
                }

                int delta = ComputeQuestDelta(def, entry, mapLower, heroDetail, heroRoles, customMetrics);
                if (delta <= 0)
                {
                    continue;
                }

                int before = slot.Progress;
                slot.Progress = Math.Min(slot.Goal, slot.Progress + delta);
                slot.LastMatchId = context.MatchID;
                updatedSlotsTotal++;

                _logger.LogInformation(
                    "[WAR_EFFORT][PROGRESS] MatchId={MatchId} AccountId={AccountId} Slot={SlotIndex} Quest={QuestId} ({Code}): {Before} -> {After}/{Goal} (+{Delta})",
                    context.MatchID, entry.Account.ID, slot.SlotIndex, def.Id, def.Code, before, slot.Progress, slot.Goal, delta);

                if (slot.Progress >= slot.Goal)
                {
                    trackedProgress.TotalBagPoints += def.BagPointsReward;
                    trackedProgress.QuestsCompletedCount += 1;
                    trackedProgress.LastUpdatedUtc = nowUtc;
                    completedQuestsTotal++;

                    _logger.LogInformation(
                        "[WAR_EFFORT][COMPLETE] MatchId={MatchId} AccountId={AccountId} Quest={QuestId} ({Code}) COMPLETED! +{BagPts} pts -> TotalBagPoints={TotalPoints}, QuestsCompleted={CompletedCount}",
                        context.MatchID, entry.Account.ID, def.Id, def.Code, def.BagPointsReward,
                        trackedProgress.TotalBagPoints, trackedProgress.QuestsCompletedCount);

                    // Immediately cycle the completed slot to a fresh objective so the client animates completion cleanly
                    QuestDefinition? nextQuest = WarEffortService.PickWeightedQuest(
                        allEnabledDefs,
                        activeQuestIds,
                        entry.Account.ID,
                        accountState.Cycle.CycleNumber,
                        slot.SlotIndex + trackedProgress.QuestsCompletedCount + context.MatchID,
                        nowUtc);

                    if (nextQuest is not null)
                    {
                        activeQuestIds.Remove(def.Id);
                        activeQuestIds.Add(nextQuest.Id);
                        slot.QuestDefinitionId = nextQuest.Id;
                        slot.Progress = 0;
                        slot.Goal = Math.Max(1, nextQuest.GoalValue);
                        slot.AssignedAtUtc = nowUtc;
                        slot.CompletedAtUtc = null;
                        _logger.LogInformation(
                            "[WAR_EFFORT][REFILL] AccountId={AccountId} Slot={SlotIndex} refilled -> QuestId={QuestId} ({Code})",
                            entry.Account.ID, slot.SlotIndex, nextQuest.Id, nextQuest.Code);
                    }
                    else
                    {
                        slot.CompletedAtUtc = nowUtc;
                    }
                }
            }
        }

        _logger.LogInformation(
            "[WAR_EFFORT][MATCH_DONE] MatchId={MatchId} Map={Map}: UpdatedSlots={UpdatedSlots}, CompletedQuests={CompletedQuests}",
            context.MatchID, context.MatchStatistics.Map, updatedSlotsTotal, completedQuestsTotal);
    }

    internal static string? EvaluateMatchEligibility(IngestedMatchContext context, WarEffortOptions opts)
    {
        if (opts.CountBotMatches)
        {
            return null;
        }

        if (context.IsUnrankedListenServer)
        {
            return "unranked_listen_server";
        }

        string gameMode = (context.MatchStatistics.GameMode ?? string.Empty).Trim().ToLowerInvariant();
        if (gameMode.Contains("practice", StringComparison.Ordinal) ||
            gameMode.Contains("local", StringComparison.Ordinal) ||
            gameMode.Contains("nostats", StringComparison.Ordinal) ||
            gameMode.Contains("tutorial", StringComparison.Ordinal))
        {
            return $"excluded_mode:{gameMode}";
        }

        string hostName = (context.MatchStatistics.HostAccountName ?? string.Empty).Trim().ToLowerInvariant();
        if (hostName.Contains("queuearcade", StringComparison.Ordinal) ||
            hostName.Contains("arcadehost", StringComparison.Ordinal) ||
            hostName.StartsWith("qa_", StringComparison.Ordinal))
        {
            return $"queue_arcade_host:{hostName}";
        }

        if (context.Form is not null && context.Form.MatchStats.TryGetValue("nostats", out string? noStatsVal) &&
            (noStatsVal == "1" || string.Equals(noStatsVal, "true", StringComparison.OrdinalIgnoreCase)))
        {
            return "nostats_flag";
        }

        if (context.DurationSeconds < opts.MinimumMatchDurationSeconds)
        {
            return $"duration_too_short:{context.DurationSeconds}s<{opts.MinimumMatchDurationSeconds}s";
        }

        int humanCount = context.ProcessedPlayerEntries.Count(p => !IsBotOrHostAccount(p));
        if (humanCount < opts.MinimumHumanPlayers)
        {
            return $"insufficient_humans:{humanCount}<{opts.MinimumHumanPlayers}";
        }

        int totalPlayers = context.ProcessedPlayerEntries.Count;
        if (totalPlayers > 0 && humanCount * 2 < totalPlayers)
        {
            return $"bot_majority:{humanCount}/{totalPlayers}";
        }

        return null;
    }

    internal static int ComputeQuestDelta(
        QuestDefinition def,
        PlayerIngestionEntry entry,
        string mapLower,
        HeroDetail? heroDetail,
        HashSet<string>? heroRoles,
        Dictionary<string, double> customMetrics)
    {
        // 1. Custom Kongor objectives (authoritative MasterServer evaluation)
        if (!string.IsNullOrWhiteSpace(def.CustomMetricKey))
        {
            return def.CustomMetricKey.ToLowerInvariant() switch
            {
                "kongor_match_played" => 1,
                "kongor_hero_kills" => Math.Max(0, entry.PlayerStats.HeroKills),
                "kongor_assists" => Math.Max(0, entry.PlayerStats.HeroAssists),
                "kongor_wards_placed" => Math.Max(0, entry.PlayerStats.WardsPlaced),
                "kongor_building_damage" => Math.Max(0, entry.PlayerStats.BuildingDamage),
                "kongor_10v10_match" => mapLower.Contains("10v10", StringComparison.Ordinal) ? 1 : 0,
                "kongor_strikers_goals" => IsStrikersMap(mapLower)
                    ? ReadMetricInt(customMetrics, "goals", "strikers_goals", "score", "scores")
                    : 0,
                "kongor_strikers_saves" => IsStrikersMap(mapLower)
                    ? ReadMetricInt(customMetrics, "saves", "strikers_saves", "blocks")
                    : 0,
                _ => ReadMetricInt(customMetrics, def.CustomMetricKey)
            };
        }

        // 2. Native HoN triggers (authoritative MasterServer mirror of EAchievementTrigger 1..18)
        return (def.NativeTriggerName ?? string.Empty).ToLowerInvariant() switch
        {
            "achievement_value_games_won" => entry.IsWin ? 1 : 0,
            "achievement_value_caldavar_won" => entry.IsWin && mapLower.Contains("caldavar", StringComparison.Ordinal) ? 1 : 0,
            "achievement_value_mid_wars_won" => entry.IsWin && mapLower.Contains("midwars", StringComparison.Ordinal) ? 1 : 0,
            "achievement_value_rift_wars_won" => entry.IsWin && mapLower.Contains("riftwars", StringComparison.Ordinal) ? 1 : 0,
            "achievement_value_devo_wars_won" => entry.IsWin && (mapLower.Contains("devowars", StringComparison.Ordinal) || mapLower.Contains("truedevo", StringComparison.Ordinal)) ? 1 : 0,
            "achievement_value_grimms_won" => entry.IsWin && mapLower.Contains("grimm", StringComparison.Ordinal) ? 1 : 0,
            "achievement_value_grimm_hunt_won" => entry.IsWin && mapLower.Contains("grimm", StringComparison.Ordinal) ? 1 : 0,
            "achievement_value_ctf_won" => entry.IsWin && (mapLower.Contains("ctf", StringComparison.Ordinal) || mapLower.Contains("capturetheflag", StringComparison.Ordinal)) ? 1 : 0,
            "achievement_value_building_damage" => Math.Max(0, entry.PlayerStats.BuildingDamage),
            "achievement_value_hero_played" => !string.IsNullOrWhiteSpace(def.HeroIdentifier) &&
                string.Equals(NormalizeHeroKey(entry.HeroKey), NormalizeHeroKey(def.HeroIdentifier), StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            "achievement_value_attribute_played" => MatchesAttributeQuest(def, entry, heroDetail) ? 1 : 0,
            "achievement_value_attack_type_played" => MatchesAttackTypeQuest(def, entry, heroDetail) ? 1 : 0,
            "achievement_value_role_played" => MatchesRoleQuest(def, heroRoles) ? 1 : 0,
            _ => 0
        };
    }

    private static bool MatchesAttributeQuest(QuestDefinition def, PlayerIngestionEntry entry, HeroDetail? heroDetail)
    {
        if (def.HeroTeamId > 0 && entry.PlayerStats.Team != def.HeroTeamId)
        {
            return false;
        }
        if (heroDetail is null || string.IsNullOrWhiteSpace(def.HeroAttribute))
        {
            return false;
        }
        return string.Equals(heroDetail.PrimaryAttribute?.Trim(), def.HeroAttribute.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAttackTypeQuest(QuestDefinition def, PlayerIngestionEntry entry, HeroDetail? heroDetail)
    {
        if (def.HeroTeamId > 0 && entry.PlayerStats.Team != def.HeroTeamId)
        {
            return false;
        }
        if (heroDetail is null || string.IsNullOrWhiteSpace(def.HeroAttackType))
        {
            return false;
        }
        return string.Equals(heroDetail.AttackType?.Trim(), def.HeroAttackType.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRoleQuest(QuestDefinition def, HashSet<string>? heroRoles)
    {
        if (heroRoles is null || heroRoles.Count == 0 || string.IsNullOrWhiteSpace(def.HeroRole))
        {
            return false;
        }
        return heroRoles.Contains(def.HeroRole.Trim());
    }

    private static bool IsBotOrHostAccount(PlayerIngestionEntry entry)
    {
        if (entry.Account.ID <= 0 || entry.Account.Type == AccountType.ServerHost)
        {
            return true;
        }
        string name = entry.Account.Name ?? string.Empty;
        return name.EndsWith("Bot", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Bot", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[^1]);
    }

    private static bool IsStrikersMap(string mapLower) =>
        mapLower.Contains("soccer", StringComparison.Ordinal) ||
        mapLower.Contains("striker", StringComparison.Ordinal);

    private static string NormalizeHeroKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string trimmed = raw.Trim();
        int dotIdx = trimmed.IndexOf('.');
        if (dotIdx > 0 && trimmed.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..dotIdx];
        }
        return trimmed;
    }

    private static Dictionary<string, double> ParseCustomStatsJson(string? customStatsJson)
    {
        Dictionary<string, double> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(customStatsJson))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(customStatsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double num))
                {
                    result[prop.Name] = num;
                }
                else if (prop.Value.ValueKind == JsonValueKind.String &&
                         double.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    result[prop.Name] = parsed;
                }
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WAR_EFFORT] Ignored malformed CustomStats JSON: {ex.Message}");
        }

        return result;
    }

    private static int ReadMetricInt(Dictionary<string, double> metrics, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (metrics.TryGetValue(key, out double val) && val > 0)
            {
                return (int)Math.Round(val);
            }
        }
        return 0;
    }
}
