// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Prebaking.Parsers;
using ASPIRE.Prebaking.Tests.Fixtures;
using FluentAssertions;
using NEXUS.Contracts.EntityCache;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Parsers;

public sealed class EntityFileParserTests
{
    [Fact]
    public async Task ParseEntityStreamAsync_WithAbilityEntity_ReturnsAbilityDefinitionDto()
    {
        EntityFileParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidAbilityEntity));

        object? result = await parser.ParseEntityStreamAsync(stream, "ability_test.entity");

        result.Should().BeOfType<AbilityDefinitionDto>();
        AbilityDefinitionDto ability = (AbilityDefinitionDto)result!;
        ability.Name.Should().Be("Ability_TestStrike");
        ability.DisplayName.Should().Be("Test Strike");
        ability.ActionType.Should().Be("target_entity");
        ability.ManaCost.Should().Be("90,100,110,120");
    }

    [Fact]
    public async Task ParseEntityStreamAsync_WithStateEntity_ReturnsStateDefinitionDto()
    {
        EntityFileParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidStateEntity));

        object? result = await parser.ParseEntityStreamAsync(stream, "state_test.entity");

        result.Should().BeOfType<StateDefinitionDto>();
        StateDefinitionDto state = (StateDefinitionDto)result!;
        state.Name.Should().Be("State_TestBuff");
        state.EffectType.Should().Be("StatusBuff");
        state.Duration.Should().Be(5000);
    }

    [Fact]
    public async Task ParseEntityStreamAsync_WithProjectileEntity_ReturnsProjectileDefinitionDto()
    {
        EntityFileParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidProjectileEntity));

        object? result = await parser.ParseEntityStreamAsync(stream, "projectile_test.entity");

        result.Should().BeOfType<ProjectileDefinitionDto>();
        ProjectileDefinitionDto proj = (ProjectileDefinitionDto)result!;
        proj.Name.Should().Be("Projectile_TestArrow");
        proj.Speed.Should().Be(1200f);
        proj.Flying.Should().BeTrue();
    }

    [Fact]
    public async Task ParseEntityStreamAsync_WithItemEntity_ReturnsItemDefinitionDto()
    {
        EntityFileParser parser = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(SampleXmlData.ValidItemEntity));

        object? result = await parser.ParseEntityStreamAsync(stream, "item_test.entity");

        result.Should().BeOfType<ItemDefinitionDto>();
        ItemDefinitionDto item = (ItemDefinitionDto)result!;
        item.Name.Should().Be("Item_TestBlade");
        item.Cost.Should().Be(1500);
        item.NonInterrupting.Should().BeTrue();
    }
}
