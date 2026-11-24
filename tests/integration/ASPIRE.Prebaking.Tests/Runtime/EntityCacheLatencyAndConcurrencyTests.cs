// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Services.EntityCache;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Runtime;

public sealed class EntityCacheLatencyAndConcurrencyTests
{
    private static EntityCacheContainer GeneratePopulatedContainer(int heroCount = 150, int abilitiesPerHero = 4)
    {
        EntityCacheContainer container = new()
        {
            Version = 1,
            BuildTimestampUtc = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("O")
        };

        for (uint i = 1; i <= heroCount; i++)
        {
            string heroName = $"Hero_TestHero_{i}";
            HeroDefinitionDto hero = new()
            {
                Id = i,
                Name = heroName,
                DisplayName = $"Test Hero {i}",
                PrimaryAttribute = i % 3 == 0 ? "Intelligence" : i % 2 == 0 ? "Agility" : "Strength",
                DefaultIcon = $"/heroes/hero_{i}/icon.tga",
                DefaultModel = $"hd_hero_{i}/preview.mdf"
            };

            for (int a = 1; a <= abilitiesPerHero; a++)
            {
                string abilityName = $"Ability_TestHero_{i}_{a}";
                AbilityDefinitionDto ability = new()
                {
                    Name = abilityName,
                    DisplayName = $"Ability {a} for Hero {i}",
                    Icon = $"/heroes/hero_{i}/ability_0{a}/icon.tga",
                    ManaCost = "100",
                    Cooldown = "5000"
                };
                hero.Abilities.Add(ability);
                container.Abilities.Add(ability);
            }

            container.Heroes.Add(hero);

            container.States.Add(new StateDefinitionDto
            {
                Name = $"State_TestHero_{i}_Buff",
                Icon = $"/heroes/hero_{i}/state.tga",
                Duration = 4000
            });

            container.Projectiles.Add(new ProjectileDefinitionDto
            {
                Name = $"Projectile_TestHero_{i}_Missile",
                Model = $"/heroes/hero_{i}/projectile.mdf",
                Speed = 1000f
            });

            container.Items.Add(new ItemDefinitionDto
            {
                Name = $"Item_TestItem_{i}",
                DisplayName = $"Item {i}",
                Cost = (int)(i * 50)
            });

            container.CosmeticUpgrades.Add(new CosmeticUpgradeDto
            {
                Name = $"Cosmetic_TestIcon_{i}",
                Category = "AccountIcon",
                Texture = $"/ui/icons/cosmetic_{i}.tga"
            });
        }

        return container;
    }

    [Fact]
    public async Task EntityLookup_Latency_AveragesSub50Microseconds()
    {
        EntityCacheContainer container = GeneratePopulatedContainer();
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        await registry.InitializeFromContainerAsync(container);

        const int iterations = 10_000;

        // Warm up JIT
        for (int i = 0; i < 500; i++)
        {
            _ = registry.GetHeroById((uint)(i % 150 + 1));
            _ = registry.GetHeroByName($"Hero_TestHero_{i % 150 + 1}");
            _ = registry.GetAbilityByName($"Ability_TestHero_{i % 150 + 1}_1");
        }

        // Benchmark
        long startTimestamp = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            uint id = (uint)(i % 150 + 1);
            HeroDefinitionDto? hero = registry.GetHeroById(id);
            Assert.NotNull(hero);

            AbilityDefinitionDto? ability = registry.GetAbilityByName($"Ability_TestHero_{id}_1");
            Assert.NotNull(ability);

            StateDefinitionDto? state = registry.GetStateByName($"State_TestHero_{id}_Buff");
            Assert.NotNull(state);
        }

        TimeSpan totalElapsed = Stopwatch.GetElapsedTime(startTimestamp);
        double totalMicroseconds = totalElapsed.TotalMilliseconds * 1000.0;
        double averagePerBatchMicroseconds = totalMicroseconds / iterations; // 3 lookups per iteration
        double averagePerLookupMicroseconds = averagePerBatchMicroseconds / 3.0;

        // Verify sub-50μs latency requirement
        Assert.True(averagePerLookupMicroseconds < 50.0,
            $"Expected average lookup latency < 50μs, but was {averagePerLookupMicroseconds:F3}μs");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ConcurrentLookups_100ParallelTasks_ExecuteWithoutRaceConditions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        EntityCacheContainer container = GeneratePopulatedContainer();
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        await registry.InitializeFromContainerAsync(container);

        const int parallelTasks = 100;
        const int lookupsPerTask = 500;

        List<Task> tasks = [];
        for (int t = 0; t < parallelTasks; t++)
        {
            int taskId = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < lookupsPerTask; i++)
                {
                    uint id = (uint)((taskId + i) % 150 + 1);

                    HeroDefinitionDto? hero = registry.GetHeroById(id);
                    Assert.NotNull(hero);
                    Assert.Equal(id, hero.Id);

                    HeroDefinitionDto? heroByName = registry.GetHeroByName($"Hero_TestHero_{id}");
                    Assert.NotNull(heroByName);

                    AbilityDefinitionDto? ability = registry.GetAbilityByName($"Ability_TestHero_{id}_2");
                    Assert.NotNull(ability);

                    EntityDefinitionDto? genEntity = registry.GetEntityByName($"Hero_TestHero_{id}");
                    Assert.NotNull(genEntity);
                }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.True(registry.IsPrewarmed);
    }
}
