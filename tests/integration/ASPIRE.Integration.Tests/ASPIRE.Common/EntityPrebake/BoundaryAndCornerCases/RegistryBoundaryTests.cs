// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class RegistryBoundaryTests
{
    [Fact]
    public async Task GetHeroById_ZeroId_ReturnsHeroIfPresentOrNull()
    {
        // Arrange
        EntityCacheContainer container = new() { Version = 1 };
        container.Heroes.Add(new HeroDefinitionDto { Id = 0, Name = "Hero_Zero" });

        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        HeroDefinitionDto? zeroHero = registry.GetHeroById(0);

        // Assert
        zeroHero.Should().NotBeNull();
        zeroHero!.Name.Should().Be("Hero_Zero");
    }

    [Fact]
    public async Task GetHeroById_MaxUintId_ReturnsMatchingHero()
    {
        // Arrange
        EntityCacheContainer container = new() { Version = 1 };
        container.Heroes.Add(new HeroDefinitionDto { Id = uint.MaxValue, Name = "Hero_Max" });

        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        HeroDefinitionDto? maxHero = registry.GetHeroById(uint.MaxValue);

        // Assert
        maxHero.Should().NotBeNull();
        maxHero!.Name.Should().Be("Hero_Max");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetHeroByName_NullOrEmpty_ReturnsNullImmediately(string? invalidName)
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        HeroDefinitionDto? hero = registry.GetHeroByName(invalidName!);

        // Assert
        hero.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetEntityByName_NullOrEmpty_ReturnsNullImmediately(string? invalidName)
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        EntityDefinitionDto? entity = registry.GetEntityByName(invalidName!);

        // Assert
        entity.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "alt1")]
    [InlineData("Hero_1", null)]
    [InlineData("", "alt1")]
    [InlineData("Hero_1", "")]
    public async Task GetAltAvatar_NullOrEmptyInputs_ReturnsNull(string? heroName, string? avatarKey)
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 2);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        AltAvatarDto? avatar = registry.GetAltAvatar(heroName!, avatarKey!);

        // Assert
        avatar.Should().BeNull();
    }

    [Fact]
    public async Task Registry_EmptyContainer_InitializesSafelyWithoutExceptions()
    {
        // Arrange
        EntityCacheContainer empty = new();
        using TestPrebakedEntityRegistry registry = new(empty);

        // Act
        await registry.InitializeAsync();

        // Assert
        registry.IsPrewarmed.Should().BeTrue();
        registry.TotalEntitiesLoaded.Should().Be(0);
        registry.GetHeroById(1).Should().BeNull();
    }
}
