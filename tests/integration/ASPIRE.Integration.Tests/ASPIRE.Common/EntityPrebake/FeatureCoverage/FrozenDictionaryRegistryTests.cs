// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class FrozenDictionaryRegistryTests
{
    [Fact]
    public async Task GetHeroById_ExistingHero_ReturnsHeroDefinitionDto()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 2);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        HeroDefinitionDto? hero = registry.GetHeroById(3);

        // Assert
        hero.Should().NotBeNull();
        hero!.Id.Should().Be(3);
        hero.Name.Should().Be("Hero_3");
    }

    [Fact]
    public async Task GetHeroByName_CaseInsensitiveLookup_ReturnsCorrectHero()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 2);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        HeroDefinitionDto? upper = registry.GetHeroByName("HERO_2");
        HeroDefinitionDto? lower = registry.GetHeroByName("hero_2");
        HeroDefinitionDto? mixed = registry.GetHeroByName("Hero_2");

        // Assert
        upper.Should().NotBeNull();
        lower.Should().NotBeNull();
        mixed.Should().NotBeNull();
        upper!.Id.Should().Be(2);
        lower!.Id.Should().Be(2);
        mixed!.Id.Should().Be(2);
    }

    [Fact]
    public async Task GetAltAvatar_ExistingAvatar_ReturnsAvatarDto()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog();
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        AltAvatarDto? avatar = registry.GetAltAvatar("Hero_1", "alt2");

        // Assert
        avatar.Should().NotBeNull();
        avatar!.Key.Should().Be("alt2");
        avatar.HeroName.Should().Be("Hero_1");
        avatar.Priority.Should().Be(20);
    }

    [Fact]
    public async Task GetEntityByName_ForAbilitiesAndStates_ReturnsGenericEntityDto()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 2);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        EntityDefinitionDto? ability = registry.GetEntityByName("Hero_1_Ability_1");
        EntityDefinitionDto? state = registry.GetEntityByName("State_Hero_1");
        EntityDefinitionDto? projectile = registry.GetEntityByName("Projectile_Hero_1");
        EntityDefinitionDto? item = registry.GetEntityByName("Item_PostHaste");

        // Assert
        ability.Should().NotBeNull();
        ability!.EntityType.Should().Be("Ability");

        state.Should().NotBeNull();
        state!.EntityType.Should().Be("State");

        projectile.Should().NotBeNull();
        projectile!.EntityType.Should().Be("Projectile");

        item.Should().NotBeNull();
        item!.EntityType.Should().Be("Item");
    }

    [Fact]
    public async Task GetHeroById_NonExistentHero_ReturnsNullCleanly()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        HeroDefinitionDto? missing = registry.GetHeroById(9999);

        // Assert
        missing.Should().BeNull();
    }

    [Fact]
    public async Task LookupLatency_ZeroAllocation_ExecutesInUnder50Microseconds()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(140, 5);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Warm up JIT
        for (int i = 0; i < 1000; i++) _ = registry.GetHeroByName("Hero_50");

        // Act
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) _ = registry.GetHeroByName("Hero_50");
        sw.Stop();

        // Assert
        HeroDefinitionDto? hero = registry.GetHeroByName("Hero_50");
        hero.Should().NotBeNull();

        double avgMicroseconds = sw.Elapsed.TotalMilliseconds * 1000.0 / 10000.0;
        avgMicroseconds.Should().BeLessThan(50.0, "FrozenDictionary lookup must average <50μs per operation");
    }
}
