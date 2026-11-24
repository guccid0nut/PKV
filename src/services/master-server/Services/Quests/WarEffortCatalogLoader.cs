// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccid0nut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;
using MERRICK.DatabaseContext.Entities.Quests;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KONGOR.MasterServer.Services.Quests;

internal sealed class WarEffortCatalogDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("cycleLengthDays")]
    public int CycleLengthDays { get; set; } = 28;

    [JsonPropertyName("quests")]
    public List<WarEffortQuestSeed> Quests { get; set; } = [];

    [JsonPropertyName("bagMilestones")]
    public List<WarEffortBagSeed> BagMilestones { get; set; } = [];
}

internal sealed class WarEffortQuestSeed
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "general";

    [JsonPropertyName("nativeTriggerName")]
    public string NativeTriggerName { get; set; } = "achievement_value_games_won";

    [JsonPropertyName("questTypeDef")]
    public string QuestTypeDef { get; set; } = string.Empty;

    [JsonPropertyName("questTypeKey")]
    public int QuestTypeKey { get; set; }

    [JsonPropertyName("heroIdentifier")]
    public string? HeroIdentifier { get; set; }

    [JsonPropertyName("heroRole")]
    public string? HeroRole { get; set; }

    [JsonPropertyName("heroAttribute")]
    public string? HeroAttribute { get; set; }

    [JsonPropertyName("heroAttackType")]
    public string? HeroAttackType { get; set; }

    [JsonPropertyName("heroTeamId")]
    public int HeroTeamId { get; set; }

    [JsonPropertyName("goalValue")]
    public int GoalValue { get; set; } = 1;

    [JsonPropertyName("bagPointsReward")]
    public int BagPointsReward { get; set; } = 100;

    [JsonPropertyName("customMetric")]
    public string? CustomMetricKey { get; set; }

    [JsonPropertyName("iconPath")]
    public string? IconPath { get; set; }

    [JsonPropertyName("isNativeEngineTrigger")]
    public bool IsNativeEngineTrigger { get; set; } = true;

    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 10;
}

internal sealed class WarEffortBagSeed
{
    [JsonPropertyName("bagIndex")]
    public int BagIndex { get; set; }

    [JsonPropertyName("requiredPoints")]
    public int RequiredPoints { get; set; }

    [JsonPropertyName("rewardType")]
    public string RewardType { get; set; } = "MMPOINTS";

    [JsonPropertyName("rewardAmount")]
    public int RewardAmount { get; set; }

    [JsonPropertyName("productId")]
    public int? ProductId { get; set; }

    [JsonPropertyName("altSilverFallback")]
    public int AltSilverFallback { get; set; } = 250;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

internal static class WarEffortCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Lazy<WarEffortCatalogDocument> CachedCatalog = new(LoadCatalogInternal);

    public static WarEffortCatalogDocument GetCatalog() => CachedCatalog.Value;

    public static async Task EnsureCatalogSeededAsync(
        MerrickContext db,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        WarEffortCatalogDocument catalog = GetCatalog();

        List<QuestDefinition> existingQuests = await db.QuestDefinitions
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, QuestDefinition> existingById = existingQuests.ToDictionary(q => q.Id);

        bool changed = false;
        foreach (WarEffortQuestSeed seed in catalog.Quests)
        {
            if (existingById.TryGetValue(seed.Id, out QuestDefinition? current))
            {
                if (current.Title != seed.Title ||
                    current.Description != seed.Description ||
                    current.GoalValue != seed.GoalValue ||
                    current.BagPointsReward != seed.BagPointsReward ||
                    current.IconPath != seed.IconPath ||
                    current.CustomMetricKey != seed.CustomMetricKey ||
                    current.IsNativeEngineTrigger != seed.IsNativeEngineTrigger ||
                    current.HeroTeamId != seed.HeroTeamId ||
                    current.HeroAttribute != seed.HeroAttribute ||
                    current.HeroAttackType != seed.HeroAttackType ||
                    current.HeroRole != seed.HeroRole)
                {
                    current.Title = seed.Title;
                    current.Description = seed.Description;
                    current.Category = seed.Category;
                    current.NativeTriggerName = seed.NativeTriggerName;
                    current.QuestTypeDef = seed.QuestTypeDef;
                    current.QuestTypeKey = seed.QuestTypeKey;
                    current.HeroIdentifier = seed.HeroIdentifier;
                    current.HeroRole = seed.HeroRole;
                    current.HeroAttribute = seed.HeroAttribute;
                    current.HeroAttackType = seed.HeroAttackType;
                    current.HeroTeamId = seed.HeroTeamId;
                    current.GoalValue = seed.GoalValue;
                    current.BagPointsReward = seed.BagPointsReward;
                    current.CustomMetricKey = seed.CustomMetricKey;
                    current.IconPath = seed.IconPath;
                    current.IsNativeEngineTrigger = seed.IsNativeEngineTrigger;
                    current.Weight = seed.Weight;
                    changed = true;
                }
                continue;
            }

            db.QuestDefinitions.Add(new QuestDefinition
            {
                Id = seed.Id,
                Code = seed.Code,
                Title = seed.Title,
                Description = seed.Description,
                Category = seed.Category,
                NativeTriggerName = seed.NativeTriggerName,
                QuestTypeDef = seed.QuestTypeDef,
                QuestTypeKey = seed.QuestTypeKey,
                HeroIdentifier = seed.HeroIdentifier,
                HeroRole = seed.HeroRole,
                HeroAttribute = seed.HeroAttribute,
                HeroAttackType = seed.HeroAttackType,
                HeroTeamId = seed.HeroTeamId,
                GoalValue = seed.GoalValue,
                BagPointsReward = seed.BagPointsReward,
                CustomMetricKey = seed.CustomMetricKey,
                IconPath = seed.IconPath,
                IsNativeEngineTrigger = seed.IsNativeEngineTrigger,
                Weight = seed.Weight,
                IsEnabled = true
            });
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("[WAR_EFFORT] Synced {Count} quest definitions from catalog.", catalog.Quests.Count);
        }
    }

    public static async Task EnsureBagMilestonesSeededAsync(
        MerrickContext db,
        WarEffortCycle cycle,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        bool hasAny = await db.WarEffortBagMilestones
            .AnyAsync(m => m.CycleId == cycle.Id, cancellationToken)
            .ConfigureAwait(false);
        if (hasAny)
        {
            return;
        }

        WarEffortCatalogDocument catalog = GetCatalog();
        foreach (WarEffortBagSeed bag in catalog.BagMilestones.OrderBy(b => b.BagIndex))
        {
            db.WarEffortBagMilestones.Add(new WarEffortBagMilestone
            {
                CycleId = cycle.Id,
                BagIndex = bag.BagIndex,
                BagId = (cycle.CycleNumber * 100) + bag.BagIndex,
                RequiredPoints = bag.RequiredPoints,
                RewardType = bag.RewardType,
                RewardAmount = bag.RewardAmount,
                ProductId = bag.ProductId,
                AltSilverFallback = bag.AltSilverFallback,
                Title = bag.Title
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogInformation(
            "[WAR_EFFORT] Seeded {Count} bag milestones for Cycle #{CycleNumber} (CycleId={CycleId}).",
            catalog.BagMilestones.Count, cycle.CycleNumber, cycle.Id);
    }

    private static WarEffortCatalogDocument LoadCatalogInternal()
    {
        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "Data", "wareffort", "catalog.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "wareffort", "catalog.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "wareffort", "catalog.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "wareffort", "catalog.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "wareffort", "catalog.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "data", "wareffort", "catalog.json")),
            "/app/Data/wareffort/catalog.json",
            "/app/data/wareffort/catalog.json"
        ];

        foreach (string path in candidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    WarEffortCatalogDocument? doc = JsonSerializer.Deserialize<WarEffortCatalogDocument>(json, JsonOptions);
                    if (doc is not null && doc.Quests.Count > 0 && doc.BagMilestones.Count > 0)
                    {
                        Console.WriteLine($"[WAR_EFFORT] Loaded catalog from '{path}' ({doc.Quests.Count} quests, {doc.BagMilestones.Count} bags).");
                        return doc;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[WAR_EFFORT] Skipped catalog candidate '{path}': {ex.Message}");
            }
        }

        Console.Error.WriteLine("[WAR_EFFORT] WARNING: catalog.json not found at any candidate path; using minimal 7-quest fallback catalog.");
        return BuildFallbackCatalog();
    }

    public static WarEffortCatalogDocument BuildFallbackCatalog()
    {
        return new WarEffortCatalogDocument
        {
            SchemaVersion = 1,
            CycleLengthDays = 28,
            Quests =
            [
                new WarEffortQuestSeed
                {
                    Id = 1001, Code = "native_games_won_1", Title = "Champion of Newerth",
                    Description = "Win 1 qualifying match.", Category = "general",
                    NativeTriggerName = "achievement_value_games_won", GoalValue = 1,
                    BagPointsReward = 100, IsNativeEngineTrigger = true, Weight = 12
                },
                new WarEffortQuestSeed
                {
                    Id = 1002, Code = "native_games_won_2", Title = "Victor's Momentum",
                    Description = "Win 2 qualifying matches.", Category = "general",
                    NativeTriggerName = "achievement_value_games_won", GoalValue = 2,
                    BagPointsReward = 175, IsNativeEngineTrigger = true, Weight = 10
                },
                new WarEffortQuestSeed
                {
                    Id = 1003, Code = "native_caldavar_won_1", Title = "Forests of Caldavar",
                    Description = "Win 1 match on Forests of Caldavar.", Category = "map",
                    NativeTriggerName = "achievement_value_caldavar_won", GoalValue = 1,
                    BagPointsReward = 125, IsNativeEngineTrigger = true, Weight = 10
                },
                new WarEffortQuestSeed
                {
                    Id = 1004, Code = "native_mid_wars_won_1", Title = "Mid Wars Vanguard",
                    Description = "Win 1 Mid Wars match.", Category = "map",
                    NativeTriggerName = "achievement_value_mid_wars_won", GoalValue = 1,
                    BagPointsReward = 100, IsNativeEngineTrigger = true, Weight = 10
                },
                new WarEffortQuestSeed
                {
                    Id = 2001, Code = "kongor_match_played_2", Title = "Frontline Veteran",
                    Description = "Complete 2 qualifying matches in any mode.", Category = "kongor",
                    NativeTriggerName = "achievement_value_games_won", CustomMetricKey = "kongor_match_played",
                    IconPath = "/ui/fe2/quests/quest_icons/games_won.tga", GoalValue = 2,
                    BagPointsReward = 120, IsNativeEngineTrigger = false, Weight = 10
                },
                new WarEffortQuestSeed
                {
                    Id = 2002, Code = "kongor_assists_25", Title = "Teamplay Catalyst",
                    Description = "Earn 25 Hero Assists across qualifying matches.", Category = "kongor",
                    NativeTriggerName = "achievement_value_games_won", CustomMetricKey = "kongor_assists",
                    IconPath = "/ui/fe2/quests/quest_icons/role_support.tga", GoalValue = 25,
                    BagPointsReward = 140, IsNativeEngineTrigger = false, Weight = 9
                },
                new WarEffortQuestSeed
                {
                    Id = 2006, Code = "kongor_strikers_goals_3", Title = "Strikers Striker",
                    Description = "Score 3 Goals in Strikers of Newerth.", Category = "kongor",
                    NativeTriggerName = "achievement_value_games_won", CustomMetricKey = "kongor_strikers_goals",
                    IconPath = "/ui/fe2/quests/quest_icons/games_won.tga", GoalValue = 3,
                    BagPointsReward = 125, IsNativeEngineTrigger = false, Weight = 6
                }
            ],
            BagMilestones = Enumerable.Range(1, 14).Select(i => new WarEffortBagSeed
            {
                BagIndex = i,
                RequiredPoints = i * 100 + Math.Max(0, i - 1) * 75,
                RewardType = i switch
                {
                    3 or 9 or 12 => "POINTS",
                    5 or 10 => "PLINKO_TICKETS",
                    7 or 14 => "PRODUCT_ID",
                    _ => "MMPOINTS"
                },
                RewardAmount = i switch
                {
                    3 => 30,
                    9 => 50,
                    12 => 75,
                    5 => 15,
                    10 => 25,
                    7 or 14 => 1,
                    _ => 100 + (i * 25)
                },
                ProductId = i == 7 ? 2909 : i == 14 ? 2910 : null,
                AltSilverFallback = i == 14 ? 1000 : i == 7 ? 500 : 250,
                Title = i == 14 ? "Treasure of Champions" : $"War Effort Bag #{i}"
            }).ToList()
        };
    }
}
