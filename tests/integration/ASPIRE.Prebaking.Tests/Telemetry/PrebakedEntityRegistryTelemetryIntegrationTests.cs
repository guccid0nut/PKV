// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.Metrics;
using ASPIRE.Common.Services.EntityCache;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Telemetry;

public sealed class PrebakedEntityRegistryTelemetryIntegrationTests : IDisposable
{
    private readonly List<(string InstrumentName, long Value, Dictionary<string, object?> Tags)>
        _counterRecordings = [];

    private readonly List<(string InstrumentName, double Value, Dictionary<string, object?> Tags)>
        _histogramRecordings = [];

    private readonly MeterListener _listener;
    private readonly object _recordLock = new();

    public PrebakedEntityRegistryTelemetryIntegrationTests()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NexusMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            lock (_recordLock)
            {
                Dictionary<string, object?> tagDict = [];
                foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
                _counterRecordings.Add((instrument.Name, measurement, tagDict));
            }
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            lock (_recordLock)
            {
                Dictionary<string, object?> tagDict = [];
                foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
                _histogramRecordings.Add((instrument.Name, measurement, tagDict));
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private static EntityCacheContainer CreateSampleContainer()
    {
        return new EntityCacheContainer
        {
            Heroes =
            [
                new HeroDefinitionDto
                {
                    Id = 153,
                    Name = "Hero_Accursed",
                    DisplayName = "Accursed",
                    DefaultModel = "models/accursed.mdf",
                    Abilities =
                    [
                        new AbilityDefinitionDto { Name = "Ability_Accursed1", DisplayName = "Cauterize" }
                    ],
                    AltAvatars =
                    [
                        new AltAvatarDto
                        {
                            Key = "Hero_Accursed.Alt", HeroName = "Hero_Accursed", DisplayName = "Bloodstone Accursed"
                        }
                    ]
                }
            ],
            Abilities =
            [
                new AbilityDefinitionDto { Name = "Ability_Pyromancer1", DisplayName = "Phoenix Wave" }
            ],
            States =
            [
                new StateDefinitionDto { Name = "State_Accursed_Ability2", Duration = 5000 }
            ],
            Projectiles =
            [
                new ProjectileDefinitionDto { Name = "Projectile_Accursed_Alt4", Speed = 1200f }
            ],
            Items =
            [
                new ItemDefinitionDto { Name = "Item_Bottle", Cost = 600 }
            ],
            CosmeticUpgrades =
            [
                new CosmeticUpgradeDto { Name = "501", Category = "AccountIcon" }
            ]
        };
    }

    [Fact]
    public async Task RegistryLookups_EmitMetrics_ForHitsAndMisses()
    {
        // Arrange
        using PrebakedEntityRegistry registry = new(NullLogger<PrebakedEntityRegistry>.Instance);
        await registry.InitializeFromContainerAsync(CreateSampleContainer());

        // Act - Hits
        HeroDefinitionDto? hero1 = registry.GetHeroById(153);
        HeroDefinitionDto? hero2 = registry.GetHeroByName("Hero_Accursed");
        AbilityDefinitionDto? ability = registry.GetAbilityByName("Ability_Accursed1");
        StateDefinitionDto? state = registry.GetStateByName("State_Accursed_Ability2");
        ProjectileDefinitionDto? proj = registry.GetProjectileByName("Projectile_Accursed_Alt4");
        ItemDefinitionDto? item = registry.GetItemByName("Item_Bottle");
        CosmeticUpgradeDto? cosByName = registry.GetCosmeticByName("501");
        CosmeticUpgradeDto? cosById = registry.GetCosmeticById(501);
        AltAvatarDto? alt = registry.GetAltAvatar("Hero_Accursed", "Hero_Accursed.Alt");
        EntityDefinitionDto? genEntity = registry.GetEntityByName("Hero_Accursed");

        // Act - Misses
        HeroDefinitionDto? missHero = registry.GetHeroById(9999);
        AbilityDefinitionDto? missAbility = registry.GetAbilityByName("Ability_Missing");
        ItemDefinitionDto? missItem = registry.GetItemByName("Item_Missing");

        // Assert
        hero1.Should().NotBeNull();
        hero2.Should().NotBeNull();
        ability.Should().NotBeNull();
        state.Should().NotBeNull();
        proj.Should().NotBeNull();
        item.Should().NotBeNull();
        cosByName.Should().NotBeNull();
        cosById.Should().NotBeNull();
        alt.Should().NotBeNull();
        genEntity.Should().NotBeNull();

        missHero.Should().BeNull();
        missAbility.Should().BeNull();
        missItem.Should().BeNull();

        lock (_recordLock)
        {
            // Verify Hits
            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Hero" &&
                (string?)r.Tags["source_tier"] == "PreBakedMemory");

            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Ability");

            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Item");

            // Verify Misses
            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_misses_total" &&
                (string?)r.Tags["entity_type"] == "Hero" &&
                (string?)r.Tags["source_tier"] == "NotFound");

            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_misses_total" &&
                (string?)r.Tags["entity_type"] == "Ability");

            // Verify Duration Histogram
            _histogramRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_resolution_duration_microseconds" &&
                (string?)r.Tags["entity_type"] == "Hero");
        }
    }

    [Fact]
    public async Task ResolveAsset_EmitsMetrics_WithCorrectSourceTierTags()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"vfs_telemetry_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string diskFile = Path.Combine(tempDir, "loose_asset.mdf");
        await File.WriteAllTextAsync(diskFile, "test-asset");

        try
        {
            using PrebakedEntityRegistry registry = new(
                NullLogger<PrebakedEntityRegistry>.Instance,
                overrideDirectory: tempDir);

            await registry.InitializeFromContainerAsync(CreateSampleContainer());

            // Act 1: Hit in PreBakedMemory
            EntityLookupResult memResult = registry.ResolveAsset("Hero_Accursed");
            memResult.Found.Should().BeTrue();
            memResult.SourceTier.Should().Be("PreBakedMemory");

            // Act 2: Hit on PreBakedDisk
            EntityLookupResult diskResult = registry.ResolveAsset("loose_asset.mdf");
            diskResult.Found.Should().BeTrue();
            diskResult.SourceTier.Should().Be("PreBakedDisk");

            // Act 3: Miss (NotFound)
            EntityLookupResult missResult = registry.ResolveAsset("nonexistent/path.mdf");
            missResult.Found.Should().BeFalse();
            missResult.SourceTier.Should().Be("NotFound");

            // Assert
            lock (_recordLock)
            {
                _counterRecordings.Should().Contain(r =>
                    r.InstrumentName == "nexus_entity_cache_hits_total" &&
                    (string?)r.Tags["source_tier"] == "PreBakedMemory");

                _counterRecordings.Should().Contain(r =>
                    r.InstrumentName == "nexus_entity_cache_hits_total" &&
                    (string?)r.Tags["source_tier"] == "PreBakedDisk");

                _counterRecordings.Should().Contain(r =>
                    r.InstrumentName == "nexus_entity_cache_misses_total" &&
                    (string?)r.Tags["source_tier"] == "NotFound");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
